using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Tests;

internal static partial class Program
{
    [Test("PublicLineCodec.AllRegisteredKindsAndConformanceFixtures")]
    private static void PublicLineCodecAllRegisteredKinds()
    {
        HashSet<Type> commands = typeof(ClientCommand).GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(a => a.DerivedType).ToHashSet();
        HashSet<Type> events = typeof(ServerEvent).GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(a => a.DerivedType).ToHashSet();
        List<object> rows = new();
        string? rustInput = Environment.GetEnvironmentVariable("CARD57_CODEC_RUST_IN");
        string[]? rustLines = rustInput == null ? null : File.ReadAllLines(rustInput);
        foreach ((string label, string json) in WireMessages())
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            bool command = doc.RootElement.TryGetProperty("commandId", out _)
                && !doc.RootElement.GetProperty("$type").GetString()!.StartsWith("command_", StringComparison.Ordinal);
            if (command)
            {
                ClientCommand value = PublicProtocolCodec.DecodeCommand(json) with
                {
                    CommandId = PublicLineCodecV1.GuidFromWireId(99),
                    SentryTrace = null,
                    SentryBaggage = null,
                };
                string line = PublicLineCodecV1.EncodeCommand(value, 7, 99);
                Assert(commands.Remove(value.GetType()), "Unregistered or repeated command fixture.");
                Assert(PublicLineCodecV1.TryDecodeCommand(line, out var decoded, out var error), error ?? line);
                Assert(decoded!.Corr == 7 && decoded.CommandId == 99, "Command envelope changed.");
                ClientCommand back = PublicLineCodecV1.DecodeCommandValue(line);
                Assert(PublicProtocolCodec.EncodeCommand(value) == PublicProtocolCodec.EncodeCommand(back), line);
                string? rustLine = rustLines == null ? null : JsonDocument.Parse(rustLines[rows.Count]).RootElement.GetProperty("encoded_line").GetString();
                ClientCommand? cross = rustLine == null ? null : PublicLineCodecV1.DecodeCommandValue(rustLine);
                rows.Add(CodecRow("CMD", value, line, 2, cross));
            }
            else
            {
                ServerEvent value = PublicProtocolCodec.DecodeEvent(json);
                value = value switch
                {
                    ServerEvent.CommandAccepted => new ServerEvent.CommandAccepted(PublicLineCodecV1.GuidFromWireId(99)),
                    ServerEvent.CommandRejected rejected => rejected with { CommandId = PublicLineCodecV1.GuidFromWireId(99) },
                    _ => value,
                };
                string line = PublicLineCodecV1.EncodeEvent(value, 1, 500, 7);
                Assert(events.Remove(value.GetType()), "Unregistered or repeated event fixture.");
                Assert(PublicLineCodecV1.TryDecodeEvent(line, out var decoded, out var error), error ?? line);
                Assert(decoded!.Seq == 1 && decoded.Tick == 500 && decoded.Corr == 7, "Event envelope changed.");
                ServerEvent back = PublicLineCodecV1.DecodeEventValue(line);
                Assert(PublicProtocolCodec.EncodeEvent(value) == PublicProtocolCodec.EncodeEvent(back), line);
                string? rustLine = rustLines == null ? null : JsonDocument.Parse(rustLines[rows.Count]).RootElement.GetProperty("encoded_line").GetString();
                ServerEvent? cross = rustLine == null ? null : PublicLineCodecV1.DecodeEventValue(rustLine);
                rows.Add(CodecRow("EV", value, line, 0, cross));
            }
        }
        Assert(commands.Count == 0 && events.Count == 0, "Every registered kind needs a fixture.");
        string? output = Environment.GetEnvironmentVariable("CARD57_CODEC_TABLE_OUT");
        if (output != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllLines(output, rows.Select(r => JsonSerializer.Serialize(r)), new UTF8Encoding(false));
        }
    }

    private static object CodecRow(string frame, object value, string line, int skip, object? cross)
    {
        ParameterInfo[] fields = value.GetType().GetConstructors().OrderByDescending(c => c.GetParameters().Length)
            .First().GetParameters().Skip(skip).ToArray();
        return new
        {
            frame,
            kind = value.GetType().Name,
            line,
            schema = fields.Select(p => CodecSchema(p.ParameterType)).ToArray(),
            values = fields.Select(p => CodecValue(value.GetType().GetProperty(p.Name!)!.GetValue(value), p.ParameterType)).ToArray(),
            csharp_decoded_rust = cross == null ? null : fields.Select(p =>
                CodecValue(cross.GetType().GetProperty(p.Name!)!.GetValue(cross), p.ParameterType)).ToArray(),
        };
    }

    private static string CodecSchema(Type t)
    {
        if (Nullable.GetUnderlyingType(t) is Type inner) return "?" + CodecSchema(inner);
        if (t == typeof(string)) return "s";
        if (t == typeof(byte[])) return "x";
        if (t == typeof(Guid)) return "g";
        if (t == typeof(DateTimeOffset)) return "t";
        if (t == typeof(bool)) return "b";
        if (t == typeof(float) || t == typeof(double)) return "f";
        if (t.IsEnum) return "e";
        if (t == typeof(int) || t == typeof(long)) return "i";
        if (t == typeof(ulong) || t == typeof(uint)) return "u";
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
            return "{" + CodecSchema(t.GetGenericArguments()[1]) + "}";
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            return "[" + CodecSchema(t.GetGenericArguments()[0]) + "]";
        return "(" + string.Join(";", t.GetConstructors().OrderByDescending(c => c.GetParameters().Length)
            .First().GetParameters().Select(p => CodecSchema(p.ParameterType))) + ")";
    }

    private static object? CodecValue(object? value, Type t)
    {
        if (value == null) return null;
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(Guid)) return PublicLineCodecV1.WireIdFromGuid((Guid)value);
        if (t == typeof(byte[])) return Convert.ToHexString((byte[])value).ToLowerInvariant();
        if (t == typeof(DateTimeOffset)) return ((DateTimeOffset)value).ToString("O");
        if (t.IsEnum) return value.ToString();
        if (t.IsPrimitive || t == typeof(string)) return value;
        if (value is IReadOnlyDictionary<string, string> map) return map;
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            return ((System.Collections.IEnumerable)value).Cast<object?>().Select(v => CodecValue(v, t.GetGenericArguments()[0])).ToArray();
        return t.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First().GetParameters()
            .Select(p => CodecValue(t.GetProperty(p.Name!)!.GetValue(value), p.ParameterType)).ToArray();
    }

    [Test("PublicLineCodec.SealedFramesAndRefusals")]
    private static void PublicLineCodecFrames()
    {
        var ids = new PublicLineCodecV1.CommandIds();
        Guid originalId = Guid.Parse("11111111-2222-3333-4444-555566667777");
        var original = new ClientCommand.ClaimSim(originalId, 42, ulong.MaxValue);
        ulong wireId = ids.Map(originalId);
        Assert(ids.Map(originalId) == wireId && ids.Map(Guid.Empty) != wireId, "Injective stable command-id map.");
        string mapped = PublicLineCodecV1.EncodeCommand(original, ulong.MaxValue, ids.Map);
        Assert(PublicLineCodecV1.DecodeCommandValue(mapped, ids.Resolve) == original, "No GUID truncation.");
        Assert(PublicLineCodecV1.WireIdFromGuid(PublicLineCodecV1.GuidFromWireId(ulong.MaxValue)) == ulong.MaxValue, "Full u64 command id.");
        Assert(PublicLineCodecV1.EncodeHello("ring=1,wfs=1,role=client") == "HELLO|1|ring=1,wfs=1,role=client", "HELLO grammar.");
        Assert(PublicLineCodecV1.DecodeHello("HELLO|1|ring=1,wfs=1").Caps == "ring=1,wfs=1", "HELLO decode.");
        Assert(PublicLineCodecV1.EncodeWelcome(7) == "WELCOME|1|7|", "WELCOME grammar.");
        Assert(PublicLineCodecV1.DecodeWelcome("WELCOME|1|4294967295|").Epoch == uint.MaxValue, "epoch width.");
        Assert(PublicLineCodecV1.EncodeAck(new(7, "ClaimSim", new[] { "1" })) == "ACK|7|ClaimSim|1", "ACK grammar.");
        Assert(PublicLineCodecV1.DecodeAck("ACK|7|ClaimSim|1").Fields.Single() == "1", "ACK decode.");
        foreach (RefusalCode code in Enum.GetValues<RefusalCode>())
        {
            var frame = new PublicLineCodecV1.ErrorFrame(7, code, "QueueInteraction", PublicLineCodecV1.HexEncode("Too busy"));
            Assert(PublicLineCodecV1.DecodeError(PublicLineCodecV1.EncodeError(frame)) == frame, "Refusal round trip: " + code);
        }
        var legacy = PublicLineCodecV1.DecodeMenuRefusal("Rf|7|9|53|43|1");
        Assert(legacy.Code == RefusalCode.TestFailed && legacy.HexText == "", "Six-field Rf.");
        var seven = PublicLineCodecV1.DecodeMenuRefusal("Rf|7|9|53|43|1|546f6f2062757379");
        Assert(seven.Code == RefusalCode.TestFailed && seven.HexText == "546f6f2062757379",
            "Seven-field Rf preserves the mod tooltip like Rust.");
        var modern = legacy with { HexText = PublicLineCodecV1.HexEncode("Too busy") };
        Assert(PublicLineCodecV1.DecodeMenuRefusal(PublicLineCodecV1.EncodeMenuRefusal(modern)) == modern, "Eight-field Rf.");
        string recorder = SimsOnlineServerHost.MenuRefusal.Line(7, 9, "S", "C", true, "Too busy");
        Assert(PublicLineCodecV1.DecodeRecorderMenuRefusal(recorder) == modern, "Seven-field mod tooltip adapter.");
        Assert(PublicLineCodecV1.DecodeRecorderMenuRefusal("Rf|7|9|53|43|1") == legacy, "Six-field mod adapter.");
        Assert(PublicLineCodecV1.EncodeMenuRefusal(modern with { Refused = false }).EndsWith("|0||"), "Rf clear.");
        Assert(PublicLineCodecV1.SplitAddTooltip("Interaction refused | tooltip: The sim is asleep")
            == (RefusalCode.TestFailed, PublicLineCodecV1.HexEncode("The sim is asleep")), "Recorded ERROR tooltip split.");
        Assert(PublicLineCodecV1.SplitAddTooltip("no tooltip").HexText == PublicLineCodecV1.HexEncode("no tooltip"), "Rust unsplit text.");
        Assert(PublicLineCodecV1.DecodeError("ERR|0|line_too_long|-|").Code == RefusalCode.LineTooLong, "Named oversized line.");
    }

    [Test("PublicLineCodec.CompositesAndMalformedInput")]
    private static void PublicLineCodecMalformed()
    {
        Assert(PublicLineCodecV1.SplitComposite("([(61;62),63];64)", '(', ')', ';').SequenceEqual(new[] { "[(61;62),63]", "64" }), "Nested split.");
        Assert(PublicLineCodecV1.HexDecode("613b625d") == "a;b]", "Escaped composite characters.");
        foreach (string line in new[] { "", "CMD", "CMD|x|ClaimSim|1|1|1", "CMD|1|ClaimSim|-1|1|1",
                     "CMD|1|ClaimSim|1|2147483648|1", "CMD|1|Bad Verb|1|1", "CMD|1|ClaimSim|1|1|bad\nline" })
            Assert(!PublicLineCodecV1.TryDecodeCommand(line, out _, out _), "Malformed command accepted: " + line);
        foreach (string line in new[] { "", "EV|1|2", "EV|-1|1|1|SimClaimed|1|1|~", "EV|1|x|1|SimClaimed|1|1|~" })
            Assert(!PublicLineCodecV1.TryDecodeEvent(line, out _, out _), "Malformed event accepted: " + line);
        foreach (string line in new[] { "CMD|1|QueueInteraction|1|1|([1;2))", "CMD|1|ClaimSim|1|1|18446744073709551616",
                     "CMD|1|JoinSession|1|1|ff|~", "CMD|1|JoinSession|1|1|ABC|~", "CMD|1|SetGameSpeed|1|1|999",
                     "CMD|1|JoinSession|1|1|61", "CMD|1|LeaveSession|1|1|extra", "CMD|1|JoinSession|1|1|~|~",
                     "CMD|1|ClaimSim|-0|1|1" })
            Assert(!PublicLineCodecV1.TryDecodeCommandValue(line, out _, out _), "Malformed typed command accepted: " + line);
        foreach (string line in new[] { "EV|1|1|1|DialogRequested|1|1|61|[~]",
                     "EV|1|1|1|SimMotiveChanged|1|61|NaN", "EV|1|1|1|FullSnapshot|~",
                     "EV|1|1|1|SimStateChanged|1|[1]|[(1;2]]" })
            Assert(!PublicLineCodecV1.TryDecodeEventValue(line, out _, out _), "Malformed typed event accepted: " + line);
        Random random = new(57);
        for (int i = 0; i < 10_000; i++)
        {
            string text = "a|b;[]()\r\n" + char.ConvertFromUtf32(random.Next(0x10000, 0x10ffff));
            var value = new ClientCommand.JoinSession(PublicLineCodecV1.GuidFromWireId((ulong)i), -i, text, i % 2 == 0 ? null : "");
            string line = PublicLineCodecV1.EncodeCommand(value, (ulong)i, (ulong)i);
            Assert(PublicLineCodecV1.DecodeCommandValue(line) == value, "Seeded UTF-8 round trip.");
            string garbage = new(Enumerable.Range(0, random.Next(1, 100)).Select(_ => (char)random.Next(1, 128)).ToArray());
            PublicLineCodecV1.TryDecodeCommandValue(garbage, out _, out _);
            PublicLineCodecV1.TryDecodeEventValue(garbage, out _, out _);
        }
        Assert(!PublicLineCodecV1.TrySplit(new byte[] { 0xff }, out _, out _), "Strict UTF-8.");
        Assert(PublicLineCodecV1.TrySplit(Encoding.UTF8.GetBytes("ACK|1|ClaimSim\r\n"), out var fields, out _) && fields.Length == 3, "CRLF framing.");
    }

    [Test("PublicLineCodec.BulkCapsAndAssembly")]
    private static void PublicLineCodecBulk()
    {
        Assert(PublicLineCodecV1.PUBLIC_LINE_MAX_BYTES == 4_194_304, "Literal line cap.");
        Assert(PublicLineCodecV1.PUBLIC_BULK_CHUNK_RAW_BYTES == 3_145_632, "Literal raw chunk cap.");
        Assert(PublicLineCodecV1.TrySplit(Encoding.UTF8.GetBytes(new string('x', 4_194_304) + "\n"), out _, out _), "Exact cap excludes LF.");
        Assert(!PublicLineCodecV1.TrySplit(Encoding.UTF8.GetBytes(new string('x', 4_194_305)), out _, out _), "Cap plus one.");
        byte[] raw = Enumerable.Repeat((byte)0xab, 3_145_633).ToArray();
        string[] maxChunks = PublicLineCodecV1.EncodeBulk(ulong.MaxValue, raw).ToArray();
        Assert(maxChunks.Length == 2 && Encoding.UTF8.GetByteCount(maxChunks[0]) <= 4_194_304, "Chunk split and header budget.");
        string[] chunks = PublicLineCodecV1.EncodeBulk(10, raw).ToArray();
        var receiver = new PublicLineCodecV1.BulkReceiver();
        receiver.Introduce(10);
        Assert(receiver.TrySubmit(chunks[0], out var pending, out _) && pending == null, "Partial bulk.");
        receiver.Introduce(11);
        Assert(receiver.TrySubmit("BULK|11|0|1|", out var empty, out _) && empty!.Length == 0, "Interleaved empty bulk completes.");
        Assert(receiver.TrySubmit(chunks[1], out var complete, out _) && complete!.SequenceEqual(raw), "Bulk bytes.");
        Assert(!receiver.TrySubmit(chunks[1], out _, out _), "Unknown bulk rejected.");
        foreach (string bad in new[] { "BULK|2|1|2|YQ==", "BULK|2|0|0|", "BULK|2|0|1|!!!!", "BULK|2|0|1|Y Q==" })
        {
            var rx = new PublicLineCodecV1.BulkReceiver();
            rx.Introduce(2);
            Assert(!rx.TrySubmit(bad, out _, out _), "Malformed chunk: " + bad);
        }
        foreach (string bad in new[] { "BULK|2|0|2|YQ==", "BULK|2|2|3|YQ==", "BULK|2|1|4|YQ==", "BULK|2|1|3|!!!!" })
        {
            var rx = new PublicLineCodecV1.BulkReceiver();
            rx.Introduce(2);
            Assert(rx.TrySubmit("BULK|2|0|3|YQ==", out _, out _), "Bulk head.");
            Assert(!rx.TrySubmit(bad, out _, out _), "Gap/repeat/count must discard bulk.");
            Assert(!rx.TrySubmit("BULK|2|1|3|YQ==", out _, out _), "Discarded bulk cannot resume.");
        }
        ulong next = 0;
        var original = new ServerEvent.WorldFileChunk("Save", 0, 1, "world.nhd", 0, raw.Length, raw);
        var lines = PublicLineCodecV1.EncodeEventLines(original, 1, 2, 0, ref next);
        Assert(lines[0].EndsWith("|b1") && lines.Count == 3, "Introducer precedes raw world-file bulk.");
        var events = new PublicLineCodecV1.EventReceiver();
        IReadOnlyList<PublicLineCodecV1.EventEnvelope> decoded = Array.Empty<PublicLineCodecV1.EventEnvelope>();
        foreach (string line in lines) Assert(events.TryReceive(line, out decoded, out var error), error ?? "event bulk");
        Assert(decoded.Count == 1 && ((ServerEvent.WorldFileChunk)decoded[0].Value).Data.SequenceEqual(raw), "Typed raw bulk replay.");
        var huge = new ServerEvent.SimAnimation(new[] { new string('a', 2_100_000) });
        var hugeLines = PublicLineCodecV1.EncodeEventLines(huge, 2, 2, 0, ref next);
        Assert(hugeLines[0].EndsWith("|b2"), "Oversized composite spills to bulk.");
        foreach (string line in hugeLines) Assert(events.TryReceive(line, out decoded, out var error), error ?? "text bulk");
        Assert(((ServerEvent.SimAnimation)decoded.Single().Value).Lines.Single() == huge.Lines.Single(), "Typed composite bulk replay.");
        var interleaved = new PublicLineCodecV1.EventReceiver();
        Assert(interleaved.TryReceive("EV|1|1|0|SimAnimation|b1", out _, out _), "First introducer.");
        Assert(interleaved.TryReceive("EV|2|1|0|WorldFileChunk|61|0|1|62|0|1|b2", out _, out _), "Second introducer.");
        Assert(interleaved.TryReceive("BULK|2|0|1|/w==", out decoded, out _) && decoded.Count == 0, "Later completion stays ordered.");
        Assert(interleaved.TryReceive("BULK|1|0|1|WzYxXQ==", out decoded, out _) && decoded.Count == 2, "Both ready in original order.");
        Assert(((ServerEvent.SimAnimation)decoded[0].Value).Lines.Single() == "a", "Encoded text bulk.");
        Assert(((ServerEvent.WorldFileChunk)decoded[1].Value).Data.Single() == 255, "Raw byte bulk.");
        var damaged = new PublicLineCodecV1.EventReceiver();
        Assert(damaged.TryReceive("EV|1|1|0|SimAnimation|b1", out _, out _), "Damaged introducer.");
        Assert(!damaged.TryReceive("BULK|1|0|1|!!!!", out _, out _), "Malformed bulk named.");
        Assert(damaged.TryReceive("EV|2|1|0|GameSpeedChanged|Normal", out decoded, out _) && decoded.Count == 1, "Bad event does not wedge later events.");
        ulong mixedId = 0;
        var mixed = new ServerEvent.WorldFileChunk(new string('a', 2_100_000), 0, 1, "w", 0, 1, new byte[] { 255 });
        var mixedLines = PublicLineCodecV1.EncodeEventLines(mixed, 1, 1, 0, ref mixedId);
        Assert(mixedLines[0].StartsWith("EV|1|1|0|WorldFileChunk|b1|") && mixedLines[0].EndsWith("|b2"), "Bulk ids follow introducer order.");
        var mixedRx = new PublicLineCodecV1.EventReceiver();
        foreach (string line in mixedLines) Assert(mixedRx.TryReceive(line, out decoded, out var error), error ?? "mixed bulk");
        Assert(((ServerEvent.WorldFileChunk)decoded.Single().Value).FolderName == mixed.FolderName, "Mixed bulk replay.");
        ulong ackId = 0;
        var ack = PublicLineCodecV1.EncodeAckLines(new(9, "RequestWorld", new[] { new string('6', 4_194_304) }), ref ackId);
        Assert(ack[0] == "ACK|9|RequestWorld|b1" && ack.Count == 3, "Oversized ACK field sender.");
    }

    [Test("PublicLineCodec.WorldFileChunkSequenceAndBulkRoundTrip")]
    private static void PublicLineCodecWorldFileChunkSequence()
    {
        Assert(PublicLineCodecV1.WORLD_CHUNK_RAW_BYTES == 262144, "World chunks are 256 KiB.");
        WorldSave world = new("Join", new[]
        {
            new WorldSaveFile("empty.dat", Array.Empty<byte>()),
            new WorldSaveFile("exact.nhd", Enumerable.Range(0, 262144).Select(i => (byte)i).ToArray()),
            new WorldSaveFile("over.package", Enumerable.Repeat((byte)0xab, 262145).ToArray()),
        });
        var chunks = world.ToChunks().ToArray();
        Assert(chunks.Select(c => (c.FileIndex, c.Offset, c.Data.Length)).SequenceEqual(new[]
        {
            (0, 0L, 0), (1, 0L, 262144), (2, 0L, 262144), (2, 262144L, 1),
        }), "The oracle's empty, exact and over-boundary sequence.");
        ulong bulkId = 99;
        var receiver = new PublicLineCodecV1.EventReceiver();
        for (int index = 0; index < world.Files.Count; index++)
        {
            WorldSaveFile file = world.Files[index];
            var lines = PublicLineCodecV1.EncodeWorldFileLines(world.FolderName, index, world.Files.Count,
                file, (ulong)(10 + index), 55, 77, ref bulkId);
            string expectedEvent = $"EV|{10 + index}|55|77|WorldFile|{PublicLineCodecV1.HexEncode(world.FolderName)}|{index}|3|{PublicLineCodecV1.HexEncode(file.Name)}|{file.Data.Length}|b{100 + index}";
            Assert(lines[0] == expectedEvent, "WorldFile has six fields and one sequence number per file.");
            int count = Math.Max(1, (file.Data.Length + 262143) / 262144);
            Assert(lines.Count == count + 1, "One bulk per file, cut at 262144.");
            Assert(receiver.TryReceive(lines[0], out var decoded, out var error) && decoded.Count == 0,
                error ?? "WorldFile waits for its bulk.");
            for (int chunkIndex = 0; chunkIndex < count; chunkIndex++)
            {
                var bulk = PublicLineCodecV1.DecodeBulkChunk(lines[chunkIndex + 1]);
                var source = chunks.Where(c => c.FileIndex == index).ElementAt(chunkIndex);
                Assert(bulk.BulkId == (ulong)(100 + index) && bulk.Index == (ulong)chunkIndex
                    && bulk.Count == (ulong)count && Convert.FromBase64String(bulk.Base64).SequenceEqual(source.Data),
                    "Wire bulk bytes and boundaries equal ToChunks.");
                if (source.Data.Length == 262144)
                    Assert(bulk.Base64.Length == 349528, "Full world chunk base64 size.");
                Assert(receiver.TryReceive(lines[chunkIndex + 1], out decoded, out error), error ?? "World bulk.");
                if (chunkIndex + 1 < count) Assert(decoded.Count == 0, "No partial event escapes.");
            }
            Assert(decoded.Count == 1 && decoded[0].Seq == (ulong)(10 + index)
                && decoded[0].Tick == 55 && decoded[0].Corr == 77, "One envelope preserves the event owner.");
            var value = (ServerEvent.WorldFileChunk)decoded[0].Value;
            Assert(value.FolderName == world.FolderName && value.FileIndex == index && value.FileCount == 3
                && value.FileName == file.Name && value.Offset == 0 && value.FileLength == file.Data.Length
                && value.Data.SequenceEqual(file.Data), "WorldFile assembles one complete raw file.");
            if (file.Data.Length == 0) Assert(lines[1] == $"BULK|{100 + index}|0|1|", "Empty file is one empty chunk.");
        }
        Assert(bulkId == 102 && receiver.InFlight == 0, "Each file consumes exactly one bulk id.");
    }

    [Test("PublicLineCodec.WorldFileRejectsMalformedBulkAndKeepsOrdering")]
    private static void PublicLineCodecWorldFileMalformed()
    {
        foreach (string bad in new[]
        {
            "EV|1|1|0|WorldFile|61|0|1|62|1",
            "EV|1|1|0|WorldFile|61|0|1|62|1|00",
            "EV|1|1|0|WorldFile|61|0|1|62|-1|b1",
            "EV|1|1|0|WorldFile|ff|0|1|62|1|b1",
        })
        {
            var receiver = new PublicLineCodecV1.EventReceiver();
            Assert(!receiver.TryReceive(bad, out _, out _) && receiver.InFlight == 0, "Reject malformed introducer.");
        }
        var ordered = new PublicLineCodecV1.EventReceiver();
        Assert(ordered.TryReceive("EV|1|1|0|WorldFile|61|0|1|62|0|b1", out _, out _), "WorldFile introducer.");
        Assert(ordered.TryReceive("EV|2|1|0|GameSpeedChanged|Normal", out var ready, out _) && ready.Count == 0,
            "Later events wait.");
        Assert(!ordered.TryReceive("BULK|1|0|1|YQ==", out ready, out var error) && error == "bulk_malformed",
            "Declared file length must equal assembled bytes.");
        Assert(ready.Count == 1 && ready[0].Seq == 2 && ordered.InFlight == 0, "Poisoned file does not wedge the stream.");
        Assert(ordered.TryReceive("EV|3|1|0|WorldFileChunk|61|0|1|62|0|1|b2", out _, out _),
            "Seven-field compatibility after malformed WorldFile.");
        Assert(ordered.TryReceive("BULK|2|0|1|YQ==", out ready, out _)
            && ((ServerEvent.WorldFileChunk)ready.Single().Value).Data.SequenceEqual(new byte[] { 97 }),
            "Legacy raw bulk still decodes.");
    }

    [Test("PublicLineCodec.WorldFileWriterMarkerSafety")]
    private static void PublicLineCodecWorldFileWriterSafety()
    {
        string saves = Path.Combine(Path.GetTempPath(), "card59-writer-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(saves, "Join.sims3");
        Directory.CreateDirectory(target);
        try
        {
            File.WriteAllText(Path.Combine(target, "player.data"), "keep me");
            WorldSave world = new("Join", new[] { new WorldSaveFile("world.nhd", new byte[] { 1, 2, 3 }) });
            ulong bulkId = 0;
            var lines = PublicLineCodecV1.EncodeWorldFileLines(world.FolderName, 0, 1, world.Files[0], 1, 2, 3, ref bulkId);
            var receiver = new PublicLineCodecV1.EventReceiver();
            IReadOnlyList<PublicLineCodecV1.EventEnvelope> ready = Array.Empty<PublicLineCodecV1.EventEnvelope>();
            foreach (string line in lines) Assert(receiver.TryReceive(line, out ready, out var error), error ?? "WorldFile");
            var chunk = (ServerEvent.WorldFileChunk)ready.Single().Value;
            var writer = new WorldSaveWriter(saves);
            bool refused = false;
            try { writer.Add(chunk); }
            catch (IOException error)
            {
                refused = error.Message.Contains("wasn't written by SimsOnline; refusing to replace it.");
            }
            Assert(refused && !writer.IsComplete && File.ReadAllText(Path.Combine(target, "player.data")) == "keep me"
                && !Directory.Exists(target + ".incoming"), "Unmarked player save survives the named refusal.");
            File.WriteAllText(Path.Combine(target, WorldSaveWriter.MarkerFileName), "prior online save");
            writer = new WorldSaveWriter(saves);
            writer.Add(chunk);
            Assert(writer.IsComplete && writer.FolderName == "Join.sims3" && writer.BytesWritten == 3
                && File.ReadAllBytes(Path.Combine(target, "world.nhd")).SequenceEqual(world.Files[0].Data)
                && !File.Exists(Path.Combine(target, "player.data"))
                && File.Exists(Path.Combine(target, WorldSaveWriter.MarkerFileName))
                && !Directory.Exists(target + ".incoming"), "Marked online save is replaced through staging.");

            writer = new WorldSaveWriter(saves);
            writer.Add(chunk with { FileLength = 4 });
            bool outOfOrder = false;
            try { writer.Add(chunk with { Offset = 1, FileLength = 4 }); }
            catch (InvalidDataException) { outOfOrder = true; }
            Assert(outOfOrder && !Directory.Exists(target + ".incoming")
                && File.ReadAllBytes(Path.Combine(target, "world.nhd")).SequenceEqual(world.Files[0].Data),
                "Failed staged write is cleaned without damaging the previous marked world.");
        }
        finally { Directory.Delete(saves, recursive: true); }
    }

    private static int CodecInFlight(PublicLineCodecV1.EventReceiver receiver) =>
        ((PublicLineCodecV1.BulkReceiver)typeof(PublicLineCodecV1.EventReceiver)
            .GetField("bulks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(receiver)!).InFlight;

    [Test("PublicLineCodec.MalformedBulkEvictsIntroducingEvent")]
    private static void PublicLineCodecMalformedBulkEviction()
    {
        foreach (string bad in new[]
        {
            "BULK|1|0|1", "BULK|1|0|1|Y\nQ==", "BULK|+1|0|1",
            "BULK|1|0|1|" + new string('A', PublicLineCodecV1.PUBLIC_LINE_MAX_BYTES),
        })
        {
            var receiver = new PublicLineCodecV1.EventReceiver();
            Assert(receiver.TryReceive("EV|1|1|0|SimAnimation|b1", out _, out _), "Introducer.");
            Assert(!receiver.TryReceive(bad, out _, out var error) && error == "bulk_malformed", "Malformed bulk must fail.");
            Assert(receiver.TryReceive("EV|2|1|0|GameSpeedChanged|Normal", out var ready, out _)
                && ready.Count == 1 && ready[0].Seq == 2, "Malformed bulk must not wedge pending[0].");
            Assert(CodecInFlight(receiver) == 0, "Malformed bulk must evict its assembly.");
            var bulk = new PublicLineCodecV1.BulkReceiver();
            bulk.Introduce(1);
            Assert(!bulk.TrySubmit(bad, out _, out error) && error == "bulk_malformed" && bulk.InFlight == 0,
                "Standalone assembly eviction.");
        }
        var multi = new PublicLineCodecV1.EventReceiver();
        Assert(multi.TryReceive("EV|1|1|0|SimDescriptionData|42|b1|b2|b3", out _, out _), "Multi introducer.");
        Assert(!multi.TryReceive("BULK|2|0|1", out _, out _) && CodecInFlight(multi) == 0,
            "A failed field must drop all assemblies belonging to its event.");
    }

    [Test("PublicLineCodec.MultiBulkIntroductionIsAtomic")]
    private static void PublicLineCodecMultiBulkAtomic()
    {
        foreach (string line in new[]
        {
            "EV|1|1|0|SimDescriptionData|42|b2|b1|b3",
            "EV|1|1|0|SimDescriptionData|42|b1|b1|b2",
        })
        {
            var receiver = new PublicLineCodecV1.EventReceiver();
            Assert(!receiver.TryReceive(line, out _, out var error) && error == "bulk_malformed", "Bad references.");
            Assert(CodecInFlight(receiver) == 0, "No partial introduction may leak.");
            Assert(receiver.TryReceive("EV|2|1|0|SimAnimation|b1", out _, out _), "Rejected frame must not advance ids.");
        }
    }

    [Test("PublicLineCodec.BulkResourceLimits")]
    private static void PublicLineCodecResourceLimits()
    {
        // Reflection lets the regression compile against the original codec without its new limit constructors.
        var bulk = (PublicLineCodecV1.BulkReceiver)Activator.CreateInstance(
            typeof(PublicLineCodecV1.BulkReceiver), new object[] { 3, 1 })!;
        bulk.Introduce(1);
        Assert(bulk.TrySubmit("BULK|1|0|2|YWI=", out var partial, out _) && partial == null, "Below byte cap.");
        Assert(!bulk.TrySubmit("BULK|1|1|2|Y2Q=", out _, out var error)
            && error == "bulk_malformed" && bulk.InFlight == 0, "Cumulative byte cap drops before writing.");
        bulk.Introduce(2);
        bool rejected = false;
        try { bulk.Introduce(3); }
        catch (FormatException ex) { rejected = ex.Message == "bulk_malformed"; }
        Assert(rejected && bulk.InFlight == 1, "In-flight cap must leave existing state intact.");
        Assert(bulk.TrySubmit("BULK|2|0|1|YWJj", out var exact, out _) && exact!.Length == 3, "Exact byte cap.");
        var receiver = (PublicLineCodecV1.EventReceiver)Activator.CreateInstance(
            typeof(PublicLineCodecV1.EventReceiver), new object?[] { null, 3, 2, 2 })!;
        Assert(!receiver.TryReceive("EV|1|1|0|SimDescriptionData|42|b1|b2|b3", out _, out error)
            && error == "bulk_malformed" && CodecInFlight(receiver) == 0, "Whole-frame in-flight cap.");
        Assert(receiver.TryReceive("EV|2|1|0|SimAnimation|b1", out _, out _), "First pending event.");
        Assert(receiver.TryReceive("EV|3|1|0|GameSpeedChanged|Normal", out _, out _), "Second pending event.");
        Assert(!receiver.TryReceive("EV|4|1|0|SimAnimation|b2", out _, out error)
            && error == "bulk_malformed" && CodecInFlight(receiver) == 1, "Pending cap cannot introduce a bulk.");
        Assert(receiver.TryReceive("BULK|1|0|2|YWI=", out _, out _), "First event chunk.");
        Assert(!receiver.TryReceive("BULK|1|1|2|Y2Q=", out var ready, out error)
            && error == "bulk_malformed" && CodecInFlight(receiver) == 0, "Event byte cap cleanup.");
        Assert(ready.Count == 1 && ready[0].Seq == 3, "Eviction immediately drains the queued event.");
        Assert(receiver.TryReceive("EV|5|1|0|GameSpeedChanged|Normal", out ready, out _)
            && ready.Count == 1 && ready[0].Seq == 5, "Drained event is not delivered twice.");
    }

    [Test("PublicLineCodec.QueuedMalformedEventPreservesReady")]
    private static void PublicLineCodecQueuedMalformed()
    {
        var receiver = new PublicLineCodecV1.EventReceiver();
        Assert(receiver.TryReceive("EV|1|1|0|SimAnimation|b1", out _, out _), "First introducer.");
        Assert(!receiver.TryReceive("EV|2|1|0|GameSpeedChanged|Garbage", out _, out _),
            "Malformed inline event must fail before being queued.");
        Assert(receiver.TryReceive("EV|3|1|0|SimAnimation|b2", out _, out _), "Second introducer.");
        Assert(!receiver.TryReceive("BULK|2|0|1|YmFk", out _, out _), "Malformed assembled event.");
        Assert(CodecInFlight(receiver) == 1, "Only the damaged event is evicted.");
        Assert(receiver.TryReceive("BULK|1|0|1|WzYxXQ==", out var ready, out _)
            && ready.Count == 1 && ready[0].Seq == 1, "Earlier valid event must be delivered successfully.");
        Assert(receiver.TryReceive("EV|4|1|0|GameSpeedChanged|Normal", out ready, out _)
            && ready.Single().Seq == 4 && CodecInFlight(receiver) == 0, "No queued poison remains.");
    }

    [Test("PublicLineCodec.UnsignedNegativeZero")]
    private static void PublicLineCodecUnsignedNegativeZero()
    {
        Assert(!PublicLineCodecV1.TryDecodeWelcome("WELCOME|1|-0|", out _, out _), "Negative-zero epoch.");
        MethodInfo decode = typeof(PublicLineCodecV1).GetMethod("DecodeValue", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool rejected = false;
        try { decode.Invoke(null, new object?[] { "-0", typeof(uint), null }); }
        catch (TargetInvocationException ex) { rejected = ex.InnerException is FormatException; }
        Assert(rejected, "Negative-zero uint field.");
        Assert(PublicLineCodecV1.DecodeWelcome("WELCOME|1|+0|").Epoch == 0, "Rust accepts leading plus.");
    }

    [Test("PublicLineCodec.IOExceptionBecomesTryFailure")]
    private static void PublicLineCodecIOException()
    {
        MethodInfo attempt = typeof(PublicLineCodecV1).GetMethod("Try", BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(bool));
        Func<bool> fail = () => throw new IOException("Stream was too long.");
        object?[] args = [fail, false, null];
        Assert(attempt.Invoke(null, args) is false && (string?)args[2] == "Stream was too long.",
            "IO failures must not escape the Try boundary.");
    }

    [Test("PublicLineCodec.RefusalVocabularyParity")]
    private static void PublicLineCodecRefusalParity()
    {
        string[] tokens =
        [
            "UnauthorizedClientInsertion", "ClientWorldServiceSuppressed", "native_oracle_owned",
            "SlotAlreadyReserved", "hello_rejected", "bad_line", "ClientReselectForbidden", "NotJoined",
            "WrongPlayer", "Superseded", "ClientNativeModesOff", "IdSpaceFull", "TestFailed", "SimNotFound",
            "TargetNotFound", "NoMatchingInteraction", "PickedObjectNotFound", "InteractionNotFound",
            "queue_rejected", "dialog", "ObjectInUse", "RouteFailed", "FailedToStart", "NotRun",
            "line_too_long", "bulk_malformed",
        ];
        Assert(Enum.GetValues<RefusalCode>().Select(PublicLineCodecV1.RefusalToken).ToHashSet().SetEquals(tokens),
            "Exact 26-code Rust wire vocabulary.");
        foreach (string token in tokens)
            Assert(PublicLineCodecV1.RefusalToken(PublicLineCodecV1.ParseRefusal(token)) == token, token);
    }

    [Test("PublicLineCodec.ZeroPaddedBulkFaultEvictsEvent")]
    private static void PublicLineCodecZeroPaddedBulk()
    {
        foreach (string bad in new[]
        {
            "BULK|000000000000000000001|5|1|YQ==",
            "BULK|000000000000000000001|0|1",
            "BULK|+000000000000000000001|5|1|YQ==",
        })
        {
            var receiver = new PublicLineCodecV1.EventReceiver();
            Assert(receiver.TryReceive("EV|1|1|0|SimAnimation|b1", out _, out _), "Introducer.");
            Assert(!receiver.TryReceive(bad, out _, out var error) && error == "bulk_malformed", "Bad padded bulk.");
            Assert(receiver.InFlight == 0, "Padded id must drop the assembly.");
            Assert(receiver.TryReceive("EV|2|1|0|GameSpeedChanged|Normal", out var ready, out _)
                && ready.Count == 1 && ready[0].Seq == 2, "Padded id must not leave a pending owner.");
            var bulk = new PublicLineCodecV1.BulkReceiver();
            bulk.Introduce(1);
            Assert(!bulk.TrySubmit(bad, out _, out _) && bulk.InFlight == 0, "Standalone padded-id cleanup.");
        }
        var zero = new PublicLineCodecV1.BulkReceiver();
        zero.Introduce(0);
        Assert(!zero.TrySubmit("BULK|000000000000000000000|0|1", out _, out _) && zero.InFlight == 0,
            "An all-zero id must also be recognizable.");
    }

    [Test("PublicLineCodec.SevenFieldRefusalValidatesTooltip")]
    private static void PublicLineCodecRefusalTooltipValidation()
    {
        foreach (string bad in new[] { "zz", "A0", "a", "ff" })
        {
            string line = "Rf|7|9|53|43|1|" + bad;
            Assert(!PublicLineCodecV1.TryDecodeMenuRefusal(line, out _, out _), "Invalid seven-field tooltip: " + bad);
            bool rejected = false;
            try { PublicLineCodecV1.DecodeRecorderMenuRefusal(line); }
            catch (ArgumentException) { rejected = true; }
            catch (FormatException) { rejected = true; }
            Assert(rejected, "Recorder alias must apply the same tooltip validation.");
        }
        Assert(PublicLineCodecV1.DecodeMenuRefusal("Rf|7|9|53|43|1|").HexText == "", "Empty tooltip is valid.");
        var clear = PublicLineCodecV1.DecodeMenuRefusal("Rf|7|9|53|43|0|zz");
        Assert(!clear.Refused && clear.Code == null && clear.HexText == "", "Rust ignores tooltip on a boolean clear.");
    }

    [Test("PublicLineCodec.NumericReasonRefusalMatchesRust")]
    private static void PublicLineCodecNumericRefusal()
    {
        foreach (string reason in new[] { "2", "5", "4294967295", "+1", "01" })
        {
            var expected = new PublicLineCodecV1.MenuRefusalFrame("42", "99", "", "", true, RefusalCode.TestFailed, "61");
            string line = "Rf|123|456|42|99|" + reason + "|61";
            Assert(PublicLineCodecV1.TryDecodeMenuRefusal(line, out var actual, out _) && actual == expected,
                "Numeric reason must map actor, target and TestFailed: " + reason);
            Assert(PublicLineCodecV1.DecodeRecorderMenuRefusal(line) == expected, "Numeric recorder alias.");
            Assert(PublicLineCodecV1.DecodeMenuRefusal(PublicLineCodecV1.EncodeMenuRefusal(expected)) == expected,
                "Numeric input normalizes to the public eight-field form.");
        }
        foreach (string reason in new[] { "00", "+0" })
        {
            var clear = PublicLineCodecV1.DecodeMenuRefusal("Rf|123|456|42|99|" + reason + "|61");
            Assert(clear.Actor == "42" && clear.Target == "99" && !clear.Refused && clear.Code == null
                && clear.HexText == "", "Numeric zero clear.");
        }
        foreach (string reason in new[] { "-0", "-1", "4294967296", "reason" })
            Assert(!PublicLineCodecV1.TryDecodeMenuRefusal("Rf|123|456|42|99|" + reason + "|61", out _, out _),
                "Invalid numeric reason.");
        foreach (string tooltip in new[] { "zz", "A0", "a", "ff" })
            foreach (string reason in new[] { "5", "+0" })
                Assert(!PublicLineCodecV1.TryDecodeMenuRefusal("Rf|123|456|42|99|" + reason + "|" + tooltip, out _, out _),
                    "Numeric recorder validates even a clear's tooltip.");
    }

    [Test("PublicLineCodec.BulkFaultDrainsReadyEventsImmediately")]
    private static void PublicLineCodecFaultDrainsReady()
    {
        foreach (string bad in new[] { "BULK|1|0|1", "BULK|1|0|1|YmFk" })
        {
            var receiver = new PublicLineCodecV1.EventReceiver();
            Assert(receiver.TryReceive("EV|1|1|0|SimAnimation|b1", out _, out _), "Head introducer.");
            Assert(receiver.TryReceive("EV|2|1|0|GameSpeedChanged|Normal", out var ready, out _) && ready.Count == 0,
                "Inline event waits behind head.");
            Assert(receiver.TryReceive("EV|3|1|0|SimAnimation|b2", out _, out _), "Later bulk introducer.");
            Assert(receiver.TryReceive("BULK|2|0|1|WzYxXQ==", out ready, out _) && ready.Count == 0,
                "Completed later bulk stays ordered.");
            Assert(!receiver.TryReceive(bad, out ready, out _) && ready.Select(e => e.Seq).SequenceEqual(new ulong[] { 2, 3 })
                && receiver.InFlight == 0, "Fault must return queued ready events on the failing call.");
            Assert(receiver.TryReceive("EV|4|1|0|GameSpeedChanged|Normal", out ready, out _) && ready.Single().Seq == 4,
                "Drained events must not be returned twice.");
        }
    }
}
