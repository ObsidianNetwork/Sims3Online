using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;

namespace SimsOnline.DedicatedServer.Protocol;

/// <summary>Card 57's closed refusal vocabulary; wire spelling preserves the kernel's names.</summary>
public enum RefusalCode
{
    UnauthorizedClientInsertion, ClientWorldServiceSuppressed, NativeOracleOwned, SlotAlreadyReserved,
    HelloRejected, BadLine, ClientReselectForbidden, NotJoined, WrongPlayer, Superseded,
    ClientNativeModesOff, IdSpaceFull, TestFailed, ObjectInUse, RouteFailed, FailedToStart, NotRun,
    LineTooLong, BulkMalformed, SimNotFound, TargetNotFound, NoMatchingInteraction, PickedObjectNotFound,
    InteractionNotFound, QueueRejected, Dialog,
}

/// <summary>
/// Card 57 public line v1. Content returned by encoders excludes LF. Socket ownership, authority,
/// correlation ordering and retries belong to the session, not this codec. JSON remains independent.
/// Records use their positional constructor order, never JSON property order or computed properties.
/// </summary>
public static class PublicLineCodecV1
{
    public const ulong PUBLIC_PROTOCOL_VERSION = 1;
    public const int PUBLIC_LINE_MAX_BYTES = 4_194_304;
    public const int PUBLIC_BULK_CHUNK_RAW_BYTES = 3_145_632;
    public const int WORLD_CHUNK_RAW_BYTES = WorldSave.ChunkBytes;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly ConcurrentDictionary<ParameterInfo, NullabilityState> NullStates = new();
    private static readonly ConcurrentDictionary<Type, ConstructorInfo> Constructors = new();
    private static readonly IReadOnlyDictionary<string, Type> Commands = Registered(typeof(ClientCommand));
    private static readonly IReadOnlyDictionary<string, Type> Events = Registered(typeof(ServerEvent));

    public sealed record HelloFrame(ulong Version, string Caps);
    public sealed record WelcomeFrame(ulong Version, uint Epoch, string Caps);
    public abstract record ResumeFrame(uint Epoch);
    public sealed record ResumeOkFrame(uint Epoch, ulong FromSeq, ulong Count) : ResumeFrame(Epoch);
    public sealed record ResumeFreshFrame(uint Epoch) : ResumeFrame(Epoch);
    public sealed record CommandFrame(ulong Corr, string Verb, ulong CommandId, int PlayerId, IReadOnlyList<string> Args);
    public sealed record AckFrame(ulong Corr, string Verb, IReadOnlyList<string> Fields);
    public sealed record ErrorFrame(ulong Corr, RefusalCode Code, string Verb, string HexText);
    public sealed record EventFrame(ulong Seq, ulong Tick, ulong Corr, string Kind, IReadOnlyList<string> Fields);
    public sealed record BulkChunk(ulong BulkId, ulong Index, ulong Count, string Base64);
    public sealed record MenuRefusalFrame(string Actor, string Target, string HexType, string HexName,
        bool Refused, RefusalCode? Code, string HexText);
    public sealed record EventEnvelope(ulong Seq, ulong Tick, ulong Corr, ServerEvent Value);

    /// <summary>
    /// Explicit, connection-local bijection for the JSON oracle's GUID command ids. No truncation.
    /// Retries reuse Map's result. Keep this map for the connection's replay window.
    /// </summary>
    public sealed class CommandIds
    {
        private readonly Dictionary<Guid, ulong> outbound = new();
        private readonly Dictionary<ulong, Guid> inbound = new();
        private ulong next;

        public ulong Map(Guid id)
        {
            if (outbound.TryGetValue(id, out ulong existing)) return existing;
            ulong wire = checked(++next);
            outbound.Add(id, wire);
            inbound.Add(wire, id);
            return wire;
        }

        public Guid Resolve(ulong id) => inbound.TryGetValue(id, out Guid value) ? value : GuidFromWireId(id);
    }

    /// <summary>A reversible representation of a wire u64 in the existing GUID-shaped record.</summary>
    public static Guid GuidFromWireId(ulong id) => Guid.ParseExact("0000000000000000" + id.ToString("x16", Invariant), "N");

    public static ulong WireIdFromGuid(Guid id)
    {
        string text = id.ToString("N");
        if (text[..16] != "0000000000000000")
            throw new FormatException("A GUID needs an explicit connection-local CommandIds map for the u64 wire.");
        return ulong.Parse(text[16..], NumberStyles.AllowHexSpecifier, Invariant);
    }

    public static string RefusalToken(RefusalCode code) => code switch
    {
        RefusalCode.NativeOracleOwned => "native_oracle_owned",
        RefusalCode.HelloRejected => "hello_rejected",
        RefusalCode.BadLine => "bad_line",
        RefusalCode.QueueRejected => "queue_rejected",
        RefusalCode.Dialog => "dialog",
        RefusalCode.LineTooLong => "line_too_long",
        RefusalCode.BulkMalformed => "bulk_malformed",
        _ when Enum.IsDefined(code) => code.ToString(),
        _ => throw new FormatException("bad refusal code"),
    };

    public static RefusalCode ParseRefusal(string token)
    {
        foreach (RefusalCode code in Enum.GetValues<RefusalCode>())
            if (RefusalToken(code) == token) return code;
        throw new FormatException("bad refusal code");
    }

    public static string HexEncode(string value) => Convert.ToHexString(Utf8.GetBytes(value)).ToLowerInvariant();
    public static string HexDecode(string field) => Utf8.GetString(HexBytes(field));

    private static byte[] HexBytes(string field)
    {
        if ((field.Length & 1) != 0 || field.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new FormatException("bad hex");
        return Convert.FromHexString(field);
    }

    public static string Line(IEnumerable<string> fields)
    {
        string[] values = fields.ToArray();
        if (values.Any(f => f.IndexOfAny(['|', '\r', '\n']) >= 0)) throw new FormatException("field separator");
        string line = string.Join('|', values);
        if (Utf8.GetByteCount(line) > PUBLIC_LINE_MAX_BYTES) throw new FormatException("line_too_long");
        return line;
    }

    public static string[] Split(ReadOnlySpan<byte> raw)
    {
        if (raw.Length > 0 && raw[^1] == '\n') raw = raw[..^1];
        if (raw.Length > 0 && raw[^1] == '\r') raw = raw[..^1];
        if (raw.Length > PUBLIC_LINE_MAX_BYTES) throw new FormatException("line_too_long");
        string text = Utf8.GetString(raw);
        if (text.IndexOfAny(['\r', '\n']) >= 0) throw new FormatException("field separator");
        return text.Split('|');
    }

    public static bool TrySplit(byte[] raw, out string[] fields, out string? error) =>
        Try(() => Split(raw), out fields, out error);

    private static string[] Frame(string line, string tag, int min)
    {
        // Reject oversized strings before allocating their UTF-8 copy (allow framing CR/LF).
        if (line.Length > PUBLIC_LINE_MAX_BYTES + 2) throw new FormatException("line_too_long");
        string[] fields = Split(Utf8.GetBytes(line));
        if (fields.Length < min || fields[0] != tag) throw new FormatException("bad " + tag + " frame");
        return fields;
    }

    private static string Identifier(string token)
    {
        if (token.Length == 0 || !char.IsAsciiLetter(token[0])
            || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new FormatException("bad identifier");
        return token;
    }

    private static ulong U64(string token) => token.StartsWith("-", StringComparison.Ordinal)
        ? throw new FormatException("bad unsigned integer") : ulong.Parse(token, NumberStyles.AllowLeadingSign, Invariant);
    private static uint U32(string token) => token.StartsWith("-", StringComparison.Ordinal)
        ? throw new FormatException("bad unsigned integer") : uint.Parse(token, NumberStyles.AllowLeadingSign, Invariant);
    private static int I32(string token) => int.Parse(token, NumberStyles.AllowLeadingSign, Invariant);

    public static string EncodeHello(string caps, ulong version = PUBLIC_PROTOCOL_VERSION)
    {
        CheckCaps(caps);
        return Line(["HELLO", version.ToString(Invariant), caps]);
    }

    public static HelloFrame DecodeHello(string line)
    {
        string[] f = Frame(line, "HELLO", 3);
        CheckCaps(f[2]);
        return new(U64(f[1]), f[2]);
    }

    private static void CheckCaps(string caps)
    {
        if (caps.Length != 0 && caps.Split(',').Any(p => p.IndexOf('=') <= 0))
            throw new FormatException("bad caps");
    }

    public static string EncodeWelcome(uint epoch, string caps = "", ulong version = PUBLIC_PROTOCOL_VERSION) =>
        Line(["WELCOME", version.ToString(Invariant), epoch.ToString(Invariant), caps]);

    public static WelcomeFrame DecodeWelcome(string line)
    {
        string[] f = Frame(line, "WELCOME", 4);
        return new(U64(f[1]), U32(f[2]), f[3]);
    }

    public static bool TryDecodeHello(string line, out HelloFrame? value, out string? error) =>
        Try(() => DecodeHello(line), out value, out error);
    public static bool TryDecodeWelcome(string line, out WelcomeFrame? value, out string? error) =>
        Try(() => DecodeWelcome(line), out value, out error);

    public static ResumeFrame DecodeResume(string line)
    {
        string[] f = Frame(line, line.StartsWith("RESUME_OK|", StringComparison.Ordinal) ? "RESUME_OK" : "RESUME_FRESH", 2);
        if (f[0] == "RESUME_FRESH" && f.Length == 2) return new ResumeFreshFrame(U32(f[1]));
        if (f[0] == "RESUME_OK" && f.Length == 4)
        {
            ulong from = U64(f[2]), count = U64(f[3]);
            if (from != 0 && count <= 2048) return new ResumeOkFrame(U32(f[1]), from, count);
        }
        throw new FormatException("bad resume frame");
    }

    public static bool ValidResumeToken(string token) =>
        token.Length == 32 && token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string? ResumeToken(AckFrame ack)
    {
        if (ack.Verb != "JoinSession") return null;
        string[] tokens = ack.Fields.Where(f => f.StartsWith("resume_token=", StringComparison.Ordinal)).ToArray();
        if (tokens.Length == 0) return null;
        if (tokens.Length != 1 || !ValidResumeToken(tokens[0][13..])) throw new FormatException("bad resume token");
        return tokens[0][13..];
    }

    public static string EncodeCommand(CommandFrame frame) => Line(new[]
    {
        "CMD", frame.Corr.ToString(Invariant), Identifier(frame.Verb),
        frame.CommandId.ToString(Invariant), frame.PlayerId.ToString(Invariant),
    }.Concat(frame.Args));

    public static CommandFrame DecodeCommand(string line)
    {
        string[] f = Frame(line, "CMD", 5);
        return new(U64(f[1]), Identifier(f[2]), U64(f[3]), I32(f[4]), f[5..]);
    }

    public static bool TryDecodeCommand(string line, out CommandFrame? value, out string? error) =>
        Try(() => DecodeCommand(line), out value, out error);

    public static string EncodeCommand(ClientCommand value, ulong corr, ulong commandId,
        Func<Guid, ulong>? mapId = null) =>
        EncodeCommand(new CommandFrame(corr, RegisteredName(value.GetType(), Commands), commandId, value.PlayerId,
            Fields(value, 2, mapId)));

    public static string EncodeCommand(ClientCommand value, ulong corr, Func<Guid, ulong>? mapId = null) =>
        EncodeCommand(value, corr, (mapId ?? WireIdFromGuid)(value.CommandId), mapId);

    public static ClientCommand DecodeCommandValue(string line, Func<ulong, Guid>? resolveId = null)
    {
        CommandFrame f = DecodeCommand(line);
        if (!Commands.TryGetValue(f.Verb, out Type? type)) throw new FormatException("unknown command");
        object?[] args = [(resolveId ?? GuidFromWireId)(f.CommandId), f.PlayerId];
        return (ClientCommand)Construct(type, f.Args, args, resolveId);
    }

    public static bool TryDecodeCommandValue(string line, out ClientCommand? value, out string? error,
        Func<ulong, Guid>? resolveId = null) =>
        Try(() => DecodeCommandValue(line, resolveId), out value, out error);

    public static string EncodeAck(AckFrame frame) =>
        Line(new[] { "ACK", frame.Corr.ToString(Invariant), Identifier(frame.Verb) }.Concat(frame.Fields));
    public static AckFrame DecodeAck(string line)
    {
        string[] f = Frame(line, "ACK", 3);
        return new(U64(f[1]), Identifier(f[2]), f[3..]);
    }
    public static bool TryDecodeAck(string line, out AckFrame? value, out string? error) =>
        Try(() => DecodeAck(line), out value, out error);

    public static string EncodeError(ErrorFrame frame) =>
        Line(["ERR", frame.Corr.ToString(Invariant), RefusalToken(frame.Code), frame.Verb, frame.HexText]);
    public static ErrorFrame DecodeError(string line)
    {
        string[] f = Frame(line, "ERR", 5);
        return new(U64(f[1]), ParseRefusal(f[2]), f[3], f[4]);
    }
    public static bool TryDecodeError(string line, out ErrorFrame? value, out string? error) =>
        Try(() => DecodeError(line), out value, out error);

    public static string EncodeEvent(EventFrame frame) => Line(new[]
    {
        "EV", frame.Seq.ToString(Invariant), frame.Tick.ToString(Invariant), frame.Corr.ToString(Invariant), Identifier(frame.Kind),
    }.Concat(frame.Fields));
    public static EventFrame DecodeEvent(string line)
    {
        string[] f = Frame(line, "EV", 5);
        return new(U64(f[1]), U64(f[2]), U64(f[3]), Identifier(f[4]), f[5..]);
    }
    public static bool TryDecodeEvent(string line, out EventFrame? value, out string? error) =>
        Try(() => DecodeEvent(line), out value, out error);
    public static string EncodeEvent(ServerEvent value, ulong seq, ulong tick, ulong corr,
        Func<Guid, ulong>? mapId = null) =>
        EncodeEvent(new EventFrame(seq, tick, corr, RegisteredName(value.GetType(), Events), Fields(value, 0, mapId)));
    public static ServerEvent DecodeEventValue(string line, Func<ulong, Guid>? resolveId = null)
    {
        EventFrame f = DecodeEvent(line);
        return EventValue(f, resolveId);
    }
    private static ServerEvent EventValue(EventFrame f, Func<ulong, Guid>? resolveId = null)
    {
        if (f.Kind == "WorldFile")
        {
            if (f.Fields.Count != 6) throw new FormatException("wrong field count");
            byte[] bytes = HexBytes(f.Fields[5]);
            long length = long.Parse(f.Fields[4], NumberStyles.AllowLeadingSign, Invariant);
            if (bytes.LongLength != length) throw new FormatException("bulk_malformed");
            return new ServerEvent.WorldFileChunk(HexDecode(f.Fields[0]), I32(f.Fields[1]), I32(f.Fields[2]),
                HexDecode(f.Fields[3]), 0L, length, bytes);
        }
        if (!Events.TryGetValue(f.Kind, out Type? type)) throw new FormatException("unknown event");
        return (ServerEvent)Construct(type, f.Fields, [], resolveId);
    }
    public static bool TryDecodeEventValue(string line, out ServerEvent? value, out string? error,
        Func<ulong, Guid>? resolveId = null) =>
        Try(() => DecodeEventValue(line, resolveId), out value, out error);

    public static string EncodeMenuRefusal(MenuRefusalFrame frame) => Line(
    [
        "Rf", frame.Actor, frame.Target, frame.HexType, frame.HexName, frame.Refused ? "1" : "0",
        frame.Refused && frame.Code.HasValue ? RefusalToken(frame.Code.Value) : "",
        frame.Refused ? frame.HexText : "",
    ]);
    public static MenuRefusalFrame DecodeMenuRefusal(string line)
    {
        string[] f = Frame(line, "Rf", 6);
        if (f.Length == 7 && f[5] is not "0" and not "1")
        {
            uint reason = U32(f[5]);
            HexDecode(f[6]);
            return new(f[3], f[4], "", "", reason != 0,
                reason != 0 ? RefusalCode.TestFailed : null, reason != 0 ? f[6] : "");
        }
        bool refused = Bool(f[5]);
        if (refused && f.Length == 7) HexDecode(f[6]);
        RefusalCode? code = !refused ? null : f.Length >= 8
            ? f[6].Length == 0 ? null : ParseRefusal(f[6]) : RefusalCode.TestFailed;
        return new(f[1], f[2], f[3], f[4], refused, code,
            !refused ? "" : f.Length >= 8 ? f[7] : f.Length == 7 ? f[6] : "");
    }
    public static bool TryDecodeMenuRefusal(string line, out MenuRefusalFrame? value, out string? error) =>
        Try(() => DecodeMenuRefusal(line), out value, out error);
    /// <summary>
    /// Decode unchanged mod bytes; the seventh field is a hex tooltip, not a v1 code.
    /// </summary>
    public static MenuRefusalFrame DecodeRecorderMenuRefusal(string line) => DecodeMenuRefusal(line);
    public static (RefusalCode Code, string HexText) SplitAddTooltip(string text)
    {
        const string separator = "| tooltip: ";
        int at = text.IndexOf(separator, StringComparison.Ordinal);
        return (RefusalCode.TestFailed, HexEncode(at < 0 ? text : text[(at + separator.Length)..].TrimStart()));
    }

    private static IReadOnlyDictionary<string, Type> Registered(Type root) =>
        root.GetCustomAttributes<JsonDerivedTypeAttribute>().ToDictionary(a => a.DerivedType.Name, a => a.DerivedType, StringComparer.Ordinal);
    private static string RegisteredName(Type type, IReadOnlyDictionary<string, Type> table) =>
        table.TryGetValue(type.Name, out Type? registered) && registered == type ? type.Name : throw new FormatException("unregistered record");
    private static ConstructorInfo Constructor(Type type) =>
        Constructors.GetOrAdd(type, t => t.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First());
    private static string[] Fields(object value, int skip, Func<Guid, ulong>? mapId) =>
        Constructor(value.GetType()).GetParameters().Skip(skip)
            .Select(p => EncodeValue(value.GetType().GetProperty(p.Name!)!.GetValue(value), p.ParameterType, mapId)).ToArray();

    private static string EncodeValue(object? value, Type type, Func<Guid, ulong>? mapId)
    {
        if (value == null) return "~";
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return HexEncode((string)value);
        if (type == typeof(byte[])) return Convert.ToHexString((byte[])value).ToLowerInvariant();
        if (type == typeof(Guid)) return (mapId ?? WireIdFromGuid)((Guid)value).ToString(Invariant);
        if (type == typeof(DateTimeOffset)) return HexEncode(((DateTimeOffset)value).ToString("O", Invariant));
        if (type == typeof(bool)) return (bool)value ? "1" : "0";
        if (type.IsEnum) return Enum.GetName(type, value) ?? throw new FormatException("bad enum");
        if (type == typeof(float)) return float.IsFinite((float)value)
            ? ((float)value).ToString("R", Invariant) : throw new FormatException("non-finite float");
        if (type == typeof(double)) return double.IsFinite((double)value)
            ? ((double)value).ToString("R", Invariant) : throw new FormatException("non-finite float");
        if (type.IsPrimitive) return Convert.ToString(value, Invariant)!;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
            return "[" + string.Join(",", ((IReadOnlyDictionary<string, string>)value)
                .Select(p => "(" + HexEncode(p.Key) + ";" + HexEncode(p.Value) + ")")) + "]";
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            return "[" + string.Join(",", ((IEnumerable)value).Cast<object?>()
                .Select(v => EncodeValue(v, type.GetGenericArguments()[0], mapId))) + "]";
        return "(" + string.Join(";", Fields(value, 0, mapId)) + ")";
    }

    private static object Construct(Type type, IReadOnlyList<string> fields, object?[] prefix, Func<ulong, Guid>? resolveId)
    {
        ConstructorInfo ctor = Constructor(type);
        ParameterInfo[] parameters = ctor.GetParameters();
        if (parameters.Length != fields.Count + prefix.Length) throw new FormatException("wrong field count");
        object?[] args = new object?[parameters.Length];
        prefix.CopyTo(args, 0);
        for (int i = prefix.Length; i < args.Length; i++)
        {
            string field = fields[i - prefix.Length];
            ParameterInfo parameter = parameters[i];
            if (field == "~" && !parameter.ParameterType.IsValueType
                && NullStates.GetOrAdd(parameter, p => new NullabilityInfoContext().Create(p).ReadState) != NullabilityState.Nullable)
                throw new FormatException("unexpected null");
            args[i] = DecodeValue(field, parameter.ParameterType, resolveId);
        }
        return ctor.Invoke(args);
    }

    private static object? DecodeValue(string field, Type type, Func<ulong, Guid>? resolveId)
    {
        Type? nullable = Nullable.GetUnderlyingType(type);
        if (field == "~")
        {
            if (!type.IsValueType || nullable != null) return null;
            throw new FormatException("unexpected null");
        }
        type = nullable ?? type;
        if (type == typeof(string)) return HexDecode(field);
        if (type == typeof(byte[])) return HexBytes(field);
        if (type == typeof(Guid)) return (resolveId ?? GuidFromWireId)(U64(field));
        if (type == typeof(DateTimeOffset)) return DateTimeOffset.ParseExact(HexDecode(field), "O", Invariant, DateTimeStyles.None);
        if (type == typeof(bool)) return Bool(field);
        if (type.IsEnum)
        {
            if (!Enum.GetNames(type).Contains(field, StringComparer.Ordinal)) throw new FormatException("bad enum");
            return Enum.Parse(type, field);
        }
        if (type == typeof(int)) return I32(field);
        if (type == typeof(long)) return long.Parse(field, NumberStyles.AllowLeadingSign, Invariant);
        if (type == typeof(ulong)) return U64(field);
        if (type == typeof(uint)) return U32(field);
        if (type == typeof(float))
        {
            float f = float.Parse(field, NumberStyles.Float, Invariant);
            return float.IsFinite(f) ? f : throw new FormatException("non-finite float");
        }
        if (type == typeof(double))
        {
            double f = double.Parse(field, NumberStyles.Float, Invariant);
            return double.IsFinite(f) ? f : throw new FormatException("non-finite float");
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
        {
            Dictionary<string, string> map = new(StringComparer.Ordinal);
            foreach (string pair in SplitComposite(field, '[', ']', ','))
            {
                string[] kv = SplitComposite(pair, '(', ')', ';');
                if (kv.Length != 2) throw new FormatException("bad map entry");
                map.Add(HexDecode(kv[0]), HexDecode(kv[1]));
            }
            return map;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            Type element = type.GetGenericArguments()[0];
            string[] items = SplitComposite(field, '[', ']', ',');
            Array array = Array.CreateInstance(element, items.Length);
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i] == "~" && !element.IsValueType) throw new FormatException("unexpected null list item");
                array.SetValue(DecodeValue(items[i], element, resolveId), i);
            }
            return array;
        }
        return Construct(type, SplitComposite(field, '(', ')', ';'), [], resolveId);
    }

    private static bool Bool(string field) => field switch
    {
        "0" => false, "1" => true, _ => throw new FormatException("bad bool"),
    };

    public static string[] SplitComposite(string field, char open, char close, char separator)
    {
        if (field.Length < 2 || field[0] != open || field[^1] != close) throw new FormatException("bad composite");
        string inner = field[1..^1];
        if (inner.Length == 0) return [];
        Stack<char> closers = new();
        List<string> items = new();
        int start = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c == '(') closers.Push(')');
            else if (c == '[') closers.Push(']');
            else if (c is ')' or ']')
            {
                if (closers.Count == 0 || closers.Pop() != c) throw new FormatException("bad composite");
            }
            if (c == separator && closers.Count == 0)
            {
                items.Add(inner[start..i]);
                start = i + 1;
            }
        }
        if (closers.Count != 0) throw new FormatException("bad composite");
        items.Add(inner[start..]);
        return items.ToArray();
    }

    private static bool Try<T>(Func<T> decode, out T value, out string? error)
    {
        try
        {
            value = decode();
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException or TargetInvocationException or IOException)
        {
            value = default!;
            error = ex.InnerException?.Message ?? ex.Message;
            return false;
        }
    }

    public static string EncodeBulkChunk(BulkChunk chunk) => Line(
        ["BULK", chunk.BulkId.ToString(Invariant), chunk.Index.ToString(Invariant), chunk.Count.ToString(Invariant), chunk.Base64]);
    public static BulkChunk DecodeBulkChunk(string line)
    {
        string[] f = Frame(line, "BULK", 5);
        Base64Bytes(f[4]);
        return new(U64(f[1]), U64(f[2]), U64(f[3]), f[4]);
    }
    public static bool TryDecodeBulkChunk(string line, out BulkChunk? value, out string? error) =>
        Try(() => DecodeBulkChunk(line), out value, out error);

    private static bool TryExtractBulkId(string line, out ulong id)
    {
        id = 0;
        if (!line.StartsWith("BULK|", StringComparison.Ordinal)) return false;
        int start = line.Length > 5 && line[5] == '+' ? 6 : 5;
        // Leading zeros do not count toward u64's 20 significant decimal digits.
        while (start < line.Length - 1 && line[start] == '0' && line[start + 1] is >= '0' and <= '9') start++;
        int end = start;
        while (end < line.Length && end - start < 20 && line[end] is >= '0' and <= '9') end++;
        return end > start && (end == line.Length || line[end] == '|')
            && ulong.TryParse(line.AsSpan(start, end - start), NumberStyles.None, Invariant, out id);
    }

    private static byte[] Base64Bytes(string field)
    {
        if (field.Length % 4 != 0 || field.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '+' and not '/' and not '='))
            throw new FormatException("bulk_malformed");
        return Convert.FromBase64String(field);
    }

    public static IEnumerable<string> EncodeBulk(ulong bulkId, byte[] bytes) =>
        EncodeBulk(bulkId, bytes, PUBLIC_BULK_CHUNK_RAW_BYTES);

    public static IEnumerable<string> EncodeBulk(ulong bulkId, byte[] bytes, int maxChunkBytes)
    {
        if (maxChunkBytes <= 0 || maxChunkBytes > PUBLIC_BULK_CHUNK_RAW_BYTES)
            throw new ArgumentOutOfRangeException(nameof(maxChunkBytes));
        ulong count = Math.Max(1, ((ulong)bytes.Length + (ulong)maxChunkBytes - 1) / (ulong)maxChunkBytes);
        for (ulong index = 0; index < count; index++)
        {
            int offset = checked((int)(index * (ulong)maxChunkBytes));
            int length = Math.Min(maxChunkBytes, bytes.Length - offset);
            yield return EncodeBulkChunk(new(bulkId, index, count, Convert.ToBase64String(bytes, offset, length)));
        }
    }

    public static IReadOnlyList<string> EncodeWorldFileLines(string folderName, int fileIndex, int fileCount,
        WorldSaveFile file, ulong seq, ulong tick, ulong corr, ref ulong nextBulkId)
    {
        ulong bulkId = checked(++nextBulkId);
        List<string> lines = new()
        {
            EncodeEvent(new EventFrame(seq, tick, corr, "WorldFile", new[]
            {
                HexEncode(folderName), fileIndex.ToString(Invariant), fileCount.ToString(Invariant),
                HexEncode(file.Name), file.Data.LongLength.ToString(Invariant), "b" + bulkId.ToString(Invariant),
            })),
        };
        lines.AddRange(EncodeBulk(bulkId, file.Data, WORLD_CHUNK_RAW_BYTES));
        return lines;
    }

    /// <summary>Introducer first, raw byte-array event fields always bulk, oversized text fields bulk on demand.</summary>
    public static IReadOnlyList<string> EncodeEventLines(ServerEvent value, ulong seq, ulong tick, ulong corr,
        ref ulong nextBulkId, Func<Guid, ulong>? mapId = null)
    {
        string kind = RegisteredName(value.GetType(), Events);
        string[] fields = Fields(value, 0, mapId);
        ParameterInfo[] parameters = Constructor(value.GetType()).GetParameters();
        Dictionary<int, byte[]> raw = new();
        for (int i = 0; i < fields.Length; i++)
        {
            if (parameters[i].ParameterType != typeof(byte[])) continue;
            raw.Add(i, (byte[])value.GetType().GetProperty(parameters[i].Name!)!.GetValue(value)!);
        }
        return WithBulks(fields, f => EncodeEvent(new EventFrame(seq, tick, corr, kind, f)), raw, ref nextBulkId);
    }

    public static IReadOnlyList<string> EncodeAckLines(AckFrame frame, ref ulong nextBulkId) =>
        WithBulks(frame.Fields.ToArray(), f => EncodeAck(frame with { Fields = f }), new(), ref nextBulkId);

    private static IReadOnlyList<string> WithBulks(string[] fields, Func<string[], string> encode,
        Dictionary<int, byte[]> bulk, ref ulong nextBulkId)
    {
        const string placeholder = "b18446744073709551615";
        foreach (int i in bulk.Keys) fields[i] = placeholder;
        while (true)
        {
            try
            {
                encode(fields);
                break;
            }
            catch (FormatException ex) when (ex.Message == "line_too_long")
            {
                int index = Enumerable.Range(0, fields.Length).Where(i => !bulk.ContainsKey(i))
                    .OrderByDescending(i => fields[i].Length).First();
                bulk.Add(index, Utf8.GetBytes(fields[index]));
                fields[index] = placeholder;
            }
        }
        List<string> chunks = new();
        // Assign ids in introducing-field order, including text spilled before raw fields.
        foreach (int i in bulk.Keys.Order())
        {
            ulong id = checked(++nextBulkId);
            fields[i] = "b" + id.ToString(Invariant);
            chunks.AddRange(EncodeBulk(id, bulk[i]));
        }
        return new[] { encode(fields) }.Concat(chunks).ToArray();
    }

    /// <summary>Only introduced ids are accepted. Errors drop the affected assembly; chunks may interleave.</summary>
    public sealed class BulkReceiver
    {
        private sealed class Partial
        {
            public ulong Count;
            public ulong Received;
            public readonly MemoryStream Bytes = new();
        }
        private readonly Dictionary<ulong, Partial> partial = new();
        private readonly int maxBulkBytes;
        private readonly int maxInFlightBulks;
        private ulong? lastIntroduced;
        public BulkReceiver(int maxBulkBytes = 256 * 1024 * 1024, int maxInFlightBulks = 16)
        {
            if (maxBulkBytes < 0 || maxBulkBytes > Array.MaxLength) throw new ArgumentOutOfRangeException(nameof(maxBulkBytes));
            if (maxInFlightBulks <= 0) throw new ArgumentOutOfRangeException(nameof(maxInFlightBulks));
            this.maxBulkBytes = maxBulkBytes;
            this.maxInFlightBulks = maxInFlightBulks;
        }
        public int InFlight => partial.Count;
        internal void ValidateIntroductions(IReadOnlyList<ulong> ids)
        {
            if (ids.Count > maxInFlightBulks - partial.Count) throw new FormatException("bulk_malformed");
            ulong? previous = lastIntroduced;
            foreach (ulong id in ids)
            {
                if (previous.HasValue && id <= previous.Value) throw new FormatException("bulk_malformed");
                previous = id;
            }
        }
        public void Introduce(ulong id)
        {
            ValidateIntroductions([id]);
            partial.Add(id, new Partial());
            lastIntroduced = id;
        }
        public bool TrySubmit(string line, out byte[]? complete, out string? error)
        {
            byte[]? result = null;
            bool hasId = TryExtractBulkId(line, out ulong id);
            bool ok = Try(() =>
            {
                try
                {
                    BulkChunk chunk = DecodeBulkChunk(line);
                    id = chunk.BulkId;
                    hasId = true;
                    if (!partial.TryGetValue(chunk.BulkId, out Partial? state) || chunk.Count == 0
                        || chunk.Index >= chunk.Count || state.Received != chunk.Index
                        || (state.Received != 0 && state.Count != chunk.Count))
                        throw new FormatException("bulk_malformed");
                    byte[] bytes = Base64Bytes(chunk.Base64);
                    if (bytes.Length > PUBLIC_BULK_CHUNK_RAW_BYTES || bytes.Length > maxBulkBytes - state.Bytes.Length)
                        throw new FormatException("bulk_malformed");
                    state.Count = chunk.Count;
                    state.Bytes.Write(bytes);
                    state.Received++;
                    if (state.Received == state.Count)
                    {
                        result = state.Bytes.ToArray();
                        Drop(chunk.BulkId);
                    }
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException or IOException)
                {
                    if (hasId) Drop(id);
                    throw new FormatException("bulk_malformed");
                }
                return true;
            }, out _, out error);
            complete = result;
            return ok;
        }
        internal void Drop(ulong id)
        {
            if (partial.Remove(id, out Partial? state)) state.Bytes.Dispose();
        }
    }

    /// <summary>Reassembles typed events while preserving introducing-line order across interleaved bulks.</summary>
    public sealed class EventReceiver
    {
        private readonly BulkReceiver bulks;
        private readonly List<EventFrame> pending = new();
        private readonly Dictionary<ulong, (string[] Fields, int Index, bool Raw)> owners = new();
        private readonly Func<ulong, Guid>? resolveId;
        private readonly int maxPendingEvents;
        public EventReceiver(Func<ulong, Guid>? resolveId = null, int maxBulkBytes = 256 * 1024 * 1024,
            int maxInFlightBulks = 16, int maxPendingEvents = 1024)
        {
            if (maxPendingEvents <= 0) throw new ArgumentOutOfRangeException(nameof(maxPendingEvents));
            bulks = new(maxBulkBytes, maxInFlightBulks);
            this.resolveId = resolveId;
            this.maxPendingEvents = maxPendingEvents;
        }
        public int InFlight => bulks.InFlight;

        private void DropEvent(string[] fields)
        {
            foreach (ulong id in owners.Where(o => ReferenceEquals(o.Value.Fields, fields)).Select(o => o.Key).ToArray())
            {
                owners.Remove(id);
                bulks.Drop(id);
            }
            pending.RemoveAll(f => ReferenceEquals(f.Fields, fields));
        }

        private void Drain(List<EventEnvelope> ready)
        {
            while (pending.Count != 0
                && !owners.Values.Any(o => ReferenceEquals(o.Fields, pending[0].Fields)))
            {
                EventFrame frame = pending[0];
                pending.RemoveAt(0);
                ready.Add(new(frame.Seq, frame.Tick, frame.Corr, EventValue(frame, resolveId)));
            }
        }

        public bool TryReceive(string line, out IReadOnlyList<EventEnvelope> events, out string? error)
        {
            List<EventEnvelope> ready = new();
            bool ok = Try(() =>
            {
                if (line.StartsWith("BULK|", StringComparison.Ordinal))
                {
                    bool hasId = TryExtractBulkId(line, out ulong id);
                    string[]? affected = hasId && owners.TryGetValue(id, out var found) ? found.Fields : null;
                    try
                    {
                        if (!bulks.TrySubmit(line, out byte[]? complete, out string? bulkError))
                            throw new FormatException(bulkError);
                        if (complete != null)
                        {
                            // Full decoding also accepts a leading plus in u64 fields.
                            if (!hasId) id = DecodeBulkChunk(line).BulkId;
                            var owner = owners[id];
                            affected = owner.Fields;
                            owner.Fields[owner.Index] = owner.Raw ? Convert.ToHexString(complete).ToLowerInvariant() : Utf8.GetString(complete);
                            owners.Remove(id);
                            // Validate before draining: a poisoned queued event must not consume valid ready events.
                            if (!owners.Values.Any(o => ReferenceEquals(o.Fields, owner.Fields)))
                                EventValue(pending.Single(f => ReferenceEquals(f.Fields, owner.Fields)), resolveId);
                        }
                    }
                    catch
                    {
                        if (affected != null)
                        {
                            DropEvent(affected);
                            Drain(ready);
                        }
                        throw;
                    }
                }
                else
                {
                    EventFrame frame = DecodeEvent(line);
                    bool worldFile = frame.Kind == "WorldFile";
                    ParameterInfo[] parameters;
                    if (worldFile)
                    {
                        if (frame.Fields.Count != 6) throw new FormatException("wrong field count");
                        HexDecode(frame.Fields[0]);
                        I32(frame.Fields[1]);
                        I32(frame.Fields[2]);
                        HexDecode(frame.Fields[3]);
                        if (long.Parse(frame.Fields[4], NumberStyles.AllowLeadingSign, Invariant) < 0
                            || frame.Fields[5].Length < 2 || frame.Fields[5][0] != 'b'
                            || !ulong.TryParse(frame.Fields[5].AsSpan(1), NumberStyles.None, Invariant, out _))
                            throw new FormatException("bulk_malformed");
                        parameters = [];
                    }
                    else
                    {
                        if (!Events.TryGetValue(frame.Kind, out Type? type)) throw new FormatException("unknown event");
                        parameters = Constructor(type).GetParameters();
                        if (parameters.Length != frame.Fields.Count) throw new FormatException("wrong field count");
                    }
                    if (pending.Count >= maxPendingEvents) throw new FormatException("bulk_malformed");
                    string[] fields = frame.Fields.ToArray();
                    frame = frame with { Fields = fields };
                    List<(ulong Id, int Index)> references = new();
                    for (int i = 0; i < fields.Length; i++)
                    {
                        if (worldFile && i != 5) continue;
                        if (fields[i].Length < 2 || fields[i][0] != 'b'
                            || !ulong.TryParse(fields[i].AsSpan(1), NumberStyles.None, Invariant, out ulong id)) continue;
                        references.Add((id, i));
                    }
                    bulks.ValidateIntroductions(references.Select(r => r.Id).ToArray());
                    if (references.Count == 0) EventValue(frame, resolveId);
                    foreach (var reference in references)
                    {
                        bulks.Introduce(reference.Id);
                        owners.Add(reference.Id, (fields, reference.Index, worldFile || parameters[reference.Index].ParameterType == typeof(byte[])));
                    }
                    pending.Add(frame);
                }
                Drain(ready);
                return true;
            }, out _, out error);
            events = ready;
            return ok;
        }
    }
}
