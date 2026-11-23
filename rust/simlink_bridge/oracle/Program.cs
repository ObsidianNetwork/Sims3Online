// card 56: the oracle side of the gate-4/5 differential. Reads the corpora in
// rust/simlink_bridge/fixtures (format: rust/simlink_bridge/src/corpus.rs) and prints what the controller's
// BridgeLineCodec makes of them, in the canonical dump the Rust crate prints (src/reply.rs `dump_line`).
//
//   dotnet run --project rust/simlink_bridge/oracle -- encode <verb_corpus.txt> <out>
//   dotnet run --project rust/simlink_bridge/oracle -- decode <reply_corpus.txt> <out>
//   dotnet run --project rust/simlink_bridge/oracle -- decode-replies <csharp_replies.txt> <out>   (gate 5's capture:
//       the reply column, row 3, of scripts/bridge_record_replies.py's file; one dump per row)
//   dotnet run --project rust/simlink_bridge/oracle -- floats <float_corpus.txt> <out>
using System.Globalization;
using System.Reflection;
using System.Text;
using SimsOnline.DedicatedServer.HostBridge;
using SimsOnline.DedicatedServer.Protocol;

internal static class Program
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("usage: encode|decode|decode-replies|floats <corpus> <out>");
            return 2;
        }

        string[] lines = File.ReadAllLines(args[1], new UTF8Encoding(false));
        var output = new StringBuilder();
        foreach (string raw in lines)
        {
            if (raw.Trim().Length == 0 || raw.StartsWith('#'))
            {
                continue;
            }

            string[] row = raw.Split('\t').Select(Unescape).ToArray();
            output.Append(args[0] switch
            {
                "encode" => row[0] + "\t" + Esc(Encode(row)),
                "decode" => row[0] + "\t" + DumpLine(row.Length > 1 ? row[1] : string.Empty),
                "decode-replies" => row[0] + "\t" + DumpLine(row.Length > 2 ? row[2] : string.Empty),
                "floats" => FloatRow(row[0]),
                _ => throw new ArgumentException(args[0]),
            });
            output.Append('\n');
        }

        File.WriteAllText(args[2], output.ToString(), new UTF8Encoding(false));
        return 0;
    }

    // ---- corpus tokens (mirror of src/corpus.rs) ----

    private static string Unescape(string token)
    {
        var sb = new StringBuilder(token.Length);
        for (int i = 0; i < token.Length; i++)
        {
            char c = token[i];
            if (c != '\\' || i + 1 >= token.Length)
            {
                sb.Append(c);
                continue;
            }

            char n = token[++i];
            sb.Append(n switch { 't' => '\t', 'n' => '\n', 'r' => '\r', _ => n });
        }

        return sb.ToString();
    }

    private static string Esc(string s) =>
        s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");

    private sealed class Tokens(string[] items)
    {
        private int _at = 1;
        public bool Done => _at == items.Length;
        public string Next() => items[_at++];
        public string? OptString() { string t = Next(); return t == "~" ? null : t; }
        public ulong U64() => ulong.Parse(Next(), Inv);
        public ulong? OptU64() { string t = Next(); return t == "~" ? null : ulong.Parse(t, Inv); }
        public long I64() => long.Parse(Next(), Inv);
        public long? OptI64() { string t = Next(); return t == "~" ? null : long.Parse(t, Inv); }
        public int I32() => int.Parse(Next(), Inv);
        public uint U32() => uint.Parse(Next(), Inv);
        public int Count() => int.Parse(Next(), Inv);
        public bool Bool() => Next() switch { "1" => true, "0" => false, var t => throw new FormatException(t) };
        public float F32() => float.Parse(Next(), Inv);
        public Vector3Dto Vec3() => ParseVec3(Next());
        public Vector3Dto? OptVec3() { string t = Next(); return t == "~" ? null : ParseVec3(t); }
        public List<ulong> Ids() { string t = Next(); return t.Length == 0 ? new() : t.Split(',').Select(v => ulong.Parse(v, Inv)).ToList(); }
        public List<float> Floats() { string t = Next(); return t.Length == 0 ? new() : t.Split(',').Select(v => float.Parse(v, Inv)).ToList(); }
        public string[] Strings() { int n = Count(); return Enumerable.Range(0, n).Select(_ => Next()).ToArray(); }
        public Dictionary<string, string> Dict()
        {
            int n = Count();
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < n; i++) { string k = Next(); d[k] = Next(); }
            return d;
        }
        public Dictionary<string, string>? OptDict()
        {
            if (items[_at] == "~") { _at++; return null; }
            return Dict();
        }
        public List<(ulong, Vector3Dto)> Positions()
        {
            int n = Count();
            var list = new List<(ulong, Vector3Dto)>();
            for (int i = 0; i < n; i++) { ulong id = U64(); list.Add((id, Vec3())); }
            return list;
        }
        public byte[] HexBytes() => Convert.FromHexString(Next());
        public InteractionRequest Interaction()
        {
            ulong actor = U64();
            string key = Next();
            ulong? target = OptU64();
            Vector3Dto? pos = OptVec3();
            return new InteractionRequest(actor, key, target, pos, Dict());
        }

        private static Vector3Dto ParseVec3(string t)
        {
            string[] v = t.Split(',');
            if (v.Length != 3) throw new FormatException(t);
            return new Vector3Dto(float.Parse(v[0], Inv), float.Parse(v[1], Inv), float.Parse(v[2], Inv));
        }
    }

    private static readonly HashSet<string> Refused = new(StringComparer.Ordinal)
    {
        "ApplyInteractionQueue", "ApplyInteractions", "ApplyForceAlign",
    };

    private static string Encode(string[] row)
    {
        if (Refused.Contains(row[0]))
        {
            return "<refused>";
        }

        var t = new Tokens(row);
        Guid c = Guid.Parse(t.Next());
        BridgeCommand cmd = row[0] switch
        {
            "LoadSave" => new BridgeCommand.LoadSave(c, t.Next()),
            "LoadSaveViaMenu" => new BridgeCommand.LoadSaveViaMenu(c, t.Next()),
            "SaveGame" => new BridgeCommand.SaveGame(c, t.Next()),
            "SetGameSpeed" => new BridgeCommand.SetGameSpeed(c, Enum.Parse<GameSpeed>(t.Next())),
            "SetWorldTime" => new BridgeCommand.SetWorldTime(c, new WorldTime(t.I32(), t.I32(), t.I32())),
            "RequestSnapshot" => new BridgeCommand.RequestSnapshot(c, t.OptI64()),
            "ProbeSimLifecycle" => new BridgeCommand.ProbeSimLifecycle(c),
            "ProbeActorLookup" => new BridgeCommand.ProbeActorLookup(c),
            "ProbeStateInjection" => new BridgeCommand.ProbeStateInjection(c, t.U64(), t.Vec3()),
            "QueueInteraction" => new BridgeCommand.QueueInteraction(c, t.Interaction()),
            "SetActiveSim" => new BridgeCommand.SetActiveSim(c, t.U64()),
            "CancelInteraction" => new BridgeCommand.CancelInteraction(c, t.U64(), t.U64()),
            "LeaveConversation" => new BridgeCommand.LeaveConversation(c, t.U64()),
            "AnswerDialog" => new BridgeCommand.AnswerDialog(c, t.I32(), t.OptString()),
            "ShowDialog" => new BridgeCommand.ShowDialog(c, t.I32(), t.U64(), t.Next(), t.Strings()),
            "DisableAutonomy" => new BridgeCommand.DisableAutonomy(c, t.U64(), t.Bool()),
            "EnableAutonomy" => new BridgeCommand.EnableAutonomy(c, t.U64(), t.Bool()),
            "GateAutonomy" => new BridgeCommand.GateAutonomy(c, t.U64()),
            "UngateAutonomy" => new BridgeCommand.UngateAutonomy(c, t.U64(), t.Bool()),
            "SetSimScope" => new BridgeCommand.SetSimScope(c, t.Ids(), t.Ids()),
            "SetAuthority" => new BridgeCommand.SetAuthority(c, Enum.Parse<AuthorityGateMode>(t.Next()), t.Ids()),
            "ProbeSimState" => new BridgeCommand.ProbeSimState(c, t.Ids()),
            "ApplySimState" => ApplySimState(c, t),
            "SetSimAliases" => SetSimAliases(c, t),
            "ProbeSimPresence" => new BridgeCommand.ProbeSimPresence(c, t.I64()),
            "SpawnSim" => new BridgeCommand.SpawnSim(c, t.U64(), t.U64(), t.Vec3()),
            "DespawnSim" => new BridgeCommand.DespawnSim(c, t.U64(), t.U64()),
            "ExportSimDescription" => new BridgeCommand.ExportSimDescription(c, t.U64()),
            "ProbeSimDetails" => new BridgeCommand.ProbeSimDetails(c, t.Ids()),
            "ApplySimDetails" => new BridgeCommand.ApplySimDetails(c, new SimDetailsSample(t.U64(), t.U64(), t.I32(), t.Next(), t.I32(), t.Next())),
            "ImportSimDescription" => new BridgeCommand.ImportSimDescription(c, new SimDescriptionPayload(t.U64(), t.HexBytes(), t.HexBytes(), t.HexBytes())),
            "SacsRecorder" => new BridgeCommand.SacsRecorder(c, t.Strings()),
            "SacsReplay" => new BridgeCommand.SacsReplay(c, t.Strings()),
            "DiagState" => new BridgeCommand.DiagState(c),
            "DiagCaptureInteraction" => new BridgeCommand.DiagCaptureInteraction(c, t.U64()),
            "QueryInteractionState" => new BridgeCommand.QueryInteractionState(c, t.U64(), t.OptU64()),
            "InjectReadIntent" => new BridgeCommand.InjectReadIntent(c, t.U64(), t.U64(), t.Next(), t.OptDict()),
            "Ping" => new BridgeCommand.Ping(c),
            "Disconnect" => new BridgeCommand.Disconnect(c),
            "Raw" => new BridgeCommand.Raw(c, t.Next(), t.Strings()),
            "SetRole" => new BridgeCommand.SetRole(c, Enum.Parse<HostRole>(t.Next())),
            "ApplyPosition" => new BridgeCommand.ApplyPosition(c, t.U64(), t.Vec3()),
            "ApplyPositions" => new BridgeCommand.ApplyPositions(c, t.Positions()),
            "ApplyMotive" => new BridgeCommand.ApplyMotive(c, t.U64(), t.Next(), t.F32()),
            "ProbeSimMotive" => new BridgeCommand.ProbeSimMotive(c, t.U64(), t.Next()),
            "ProbeSimInteractionQueue" => new BridgeCommand.ProbeSimInteractionQueue(c, t.U64()),
            "ApplyCash" => new BridgeCommand.ApplyCash(c, t.U64(), t.I32()),
            "ProbeHouseholdCash" => new BridgeCommand.ProbeHouseholdCash(c, t.U64()),
            "ApplySkill" => new BridgeCommand.ApplySkill(c, t.U64(), t.Next(), t.I32()),
            "ProbeSimSkill" => new BridgeCommand.ProbeSimSkill(c, t.U64(), t.Next()),
            "ApplyAutonomy" => new BridgeCommand.ApplyAutonomy(c, t.U64(), t.Bool()),
            "ProbeSimAutonomy" => new BridgeCommand.ProbeSimAutonomy(c, t.U64()),
            "MergeLotState" => MergeLotState(c, t),
            "ImportHouseholdContent" => new BridgeCommand.ImportHouseholdContent(c, t.U64(), t.I32(), t.I32(), t.I32(), t.I32(), t.U32(), t.I32(), t.I32(), t.I32()),
            "ExportHouseholdContent" => new BridgeCommand.ExportHouseholdContent(c, t.U64()),
            "MergeTravellingHousehold" => new BridgeCommand.MergeTravellingHousehold(c, t.U64(), t.I32(), t.U32(), t.I32(), t.I32(), t.I32(), t.I32()),
            "ShowNotification" => new BridgeCommand.ShowNotification(c, t.Next()),
            var other => throw new ArgumentException("unknown record " + other),
        };
        if (!t.Done)
        {
            throw new FormatException(row[0] + ": unread tokens");
        }

        return BridgeLineCodec.EncodeCommand(cmd);
    }

    private static BridgeCommand ApplySimState(Guid c, Tokens t)
    {
        ulong sim = t.U64();
        List<float> motives = t.Floats();
        int n = t.Count();
        var buffs = new List<SimBuffDto>();
        for (int i = 0; i < n; i++) { ulong g = t.U64(); buffs.Add(new SimBuffDto(g, t.U64())); }
        return new BridgeCommand.ApplySimState(c, new SimStateSample(sim, motives, buffs));
    }

    private static BridgeCommand SetSimAliases(Guid c, Tokens t)
    {
        int n = t.Count();
        var aliases = new List<SimAlias>();
        for (int i = 0; i < n; i++) { ulong o = t.U64(); aliases.Add(new SimAlias(o, t.U64())); }
        return new BridgeCommand.SetSimAliases(c, aliases, t.Bool());
    }

    private static BridgeCommand MergeLotState(Guid c, Tokens t)
    {
        ulong lot = t.U64();
        List<LotSimExport> sims = t.Positions().Select(p => new LotSimExport(p.Item1, p.Item2)).ToList();
        return new BridgeCommand.MergeLotState(c, new LotExportBundle(0, lot, 0, sims));
    }

    // ---- floats ----

    private static string FloatRow(string hexBits)
    {
        float v = BitConverter.Int32BitsToSingle((int)uint.Parse(hexBits, NumberStyles.HexNumber, Inv));
        return hexBits + "\t" + v.ToString("R", Inv) + "\t" + v.ToString("F2", Inv);
    }

    // ---- the canonical dump (mirror of src/reply.rs dump_line) ----

    private static readonly MethodInfo AcceptConnect =
        typeof(BridgeLineCodec).Assembly.GetType("SimsOnline.DedicatedServer.HostBridge.ProcessMemoryBridge")!
            .GetMethod("AcceptConnectResponse", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static string Try(Func<string> f)
    {
        try { return f(); }
        catch (TargetInvocationException) { return "throw"; }
        catch (Exception) { return "throw"; }
    }

    private static string O<T>(T? v) where T : struct => v.HasValue ? Convert.ToString(v.Value, Inv)! : "~";
    private static string Os(string? v) => v == null ? "~" : Esc(v);
    private static string F(float v) => v.ToString("R", Inv);
    private static string V3(Vector3Dto p) => F(p.X) + "," + F(p.Y) + "," + F(p.Z);
    private static string Ov3(Vector3Dto? p) => p.HasValue ? V3(p.Value) : "~";

    private static string DumpSnapshot(SimulationSnapshot s)
    {
        string sims = string.Join(";", s.Sims.Select(m => string.Join(",",
            m.ObjectId.ToString(Inv), Esc(m.Name), m.HouseholdId.ToString(Inv), V3(m.Position), O(m.CurrentInteractionId),
            O(m.OwningPlayerId), Os(m.CurrentInteractionKey), O(m.LotId), O(m.InteractionQueueCount), O(m.SimDescriptionId))));
        string objs = string.Join(";", s.Objects.Select(b => string.Join(",",
            b.ObjectId.ToString(Inv), Esc(b.ScriptClass), V3(b.Position), O(b.LotId))));
        return $"{s.Tick.ToString(Inv)}|{Esc(s.WorldName)}|{Esc(s.SaveName)}|{s.Speed}|sims=[{sims}]|objs=[{objs}]";
    }

    private static string DumpResponse(string line) => Try(() =>
    {
        BridgeResponse? r = BridgeLineCodec.TryDecodeResponse(line);
        if (r == null) return "~";
        return $"{r.CorrelationId:N}|{(r.IsAccepted ? 1 : 0)}|{Os(r.RejectionReason)}|{Os(r.ProbeReport)}|{(r.Snapshot == null ? "~" : DumpSnapshot(r.Snapshot))}";
    });

    private static string DumpHello(string line) => Try(() =>
    {
        if (!BridgeLineCodec.TryParseHello(line, out BridgeLineCodec.HelloParseResult h)) return "~";
        return $"{Esc(h.WorldName)}|{h.Tick.ToString(Inv)}|{Os(h.BuildStamp)}|{h.WireVersion.ToString(Inv)}|{Os(h.Caps)}|v11={(BridgeLineCodec.CapsIndicateLayoutV11(h.Caps) ? 1 : 0)}";
    });

    private static string DumpBulkRef(string line)
    {
        BridgeLineCodec.BulkRefParseResult? b = BridgeLineCodec.TryParseBulkRef(line);
        if (b == null) return "~";
        var v = b.Value;
        return $"{v.CorrelationId:N}|{Esc(v.Kind)}|{v.SlotIndex.ToString(Inv)}|{v.TotalLength.ToString(Inv)}|{v.Crc32.ToString("X8", Inv)}|{O(v.PartIndex)}|{O(v.PartCount)}";
    }

    private static string DumpChunk(string line)
    {
        var c = BridgeLineCodec.TryDecodeChunk(line);
        if (c == null) return "~";
        var v = c.Value;
        return $"{v.CorrelationId:N}|{v.Index.ToString(Inv)}|{v.Count.ToString(Inv)}|{Esc(v.Payload)}";
    }

    private static string Params(IReadOnlyDictionary<string, string>? p) =>
        p == null ? "~" : string.Join(",", p.Select(kv => Esc(kv.Key) + "=" + Esc(kv.Value)).OrderBy(s => s, StringComparer.Ordinal));

    private static string DumpEvent(string line)
    {
        var drops = new BridgeEventDrops(_ => { });
        ServerEvent? e = BridgeLineCodec.TryDecodeEvent(line, drops);
        if (e == null)
        {
            string[] parts = line.TrimStart('\uFEFF').Split('|');
            string kind = parts.Length >= 2 && parts[0] == "EVENT" && drops.Count(parts[1]) > 0 ? Esc(parts[1]) : "-";
            return "~|dropped=" + kind;
        }

        return e switch
        {
            ServerEvent.InteractionQueued q => $"InteractionQueued|{q.ActorObjectId}|{q.InteractionId}|{Esc(q.InteractionKey)}|{O(q.TargetObjectId)}|{Ov3(q.TargetPosition)}|{Params(q.Parameters)}",
            ServerEvent.InteractionReplayQueued => "InteractionReplayQueued",
            ServerEvent.InteractionStarted s => $"InteractionStarted|{s.ActorObjectId}|{s.InteractionId}",
            ServerEvent.InteractionCompleted s => $"InteractionCompleted|{s.ActorObjectId}|{s.InteractionId}",
            ServerEvent.InteractionFailed f => $"InteractionFailed|{f.ActorObjectId}|{f.InteractionId}|{Esc(f.Reason)}",
            ServerEvent.IntentCaptured i => $"IntentCaptured|{i.ActorObjectId}|{i.TargetObjectId}|{Esc(i.InteractionKey)}|{Ov3(i.TargetPosition)}",
            ServerEvent.UserIntentCaptured u => $"UserIntentCaptured|{u.ActorObjectId}|{u.TargetObjectId}|{Esc(u.DefinitionType)}|{Esc(u.Name)}|{Esc(u.Path)}|{Esc(u.Hit)}|{Ov3(u.HitPoint)}|{Esc(u.Picked)}",
            ServerEvent.DialogRequested d => $"DialogRequested|{d.RequestId}|{d.SimObjectId}|{Esc(d.Kind)}|{string.Join(",", d.Args.Select(Esc))}",
            ServerEvent.DialogAnswerCaptured a => $"DialogAnswerCaptured|{a.RequestId}|{a.SimObjectId}|{Os(a.Answer)}",
            ServerEvent.CommandRejected r => $"CommandRejected|{r.CommandId:N}|{Esc(r.Reason)}",
            ServerEvent.CancelIntentCaptured c => $"CancelIntentCaptured|{c.ActorObjectId}|{c.InteractionId}",
            ServerEvent.LeaveConversationIntentCaptured l => $"LeaveConversationIntentCaptured|{l.ActorObjectId}",
            ServerEvent.SpeedIntentCaptured s => $"SpeedIntentCaptured|{s.Speed}",
            ServerEvent.ServerError s => $"ServerError|{Esc(s.Message)}",
            _ => "unexpected:" + e.GetType().Name,
        };
    }

    private static string DumpLine(string line)
    {
        string trimmed = line.TrimStart('\uFEFF');
        return string.Join("\t",
            "plausible=" + (BridgeLineCodec.IsPlausibleHostFrameLine(trimmed) ? 1 : 0),
            "resp=" + DumpResponse(line),
            "hello=" + DumpHello(line),
            "connect_hello=" + Try(() => BridgeLineCodec.IsConnectHandshakeHello(line) ? "1" : "0"),
            "accept_connect=" + Try(() => (bool)AcceptConnect.Invoke(null, new object[] { line })! ? "1" : "0"),
            "bulkref=" + Try(() => DumpBulkRef(line)),
            "chunk=" + Try(() => DumpChunk(line)),
            "event=" + Try(() => DumpEvent(line)));
    }
}
