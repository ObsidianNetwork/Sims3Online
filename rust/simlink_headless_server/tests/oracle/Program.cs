using System.Collections;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SimsOnline.DedicatedServer.Authority;
using SimsOnline.DedicatedServer.HostBridge;
using SimsOnline.DedicatedServer.Mirroring;
using SimsOnline.DedicatedServer.Network;
using SimsOnline.DedicatedServer.Protocol;

// This runner invokes the production controller policies AND its real socket
// recipient queue. Only the bridge host is substituted with recorded inputs.
internal static class Program
{
    private static readonly CancellationTokenSource Deadline = new(TimeSpan.FromMinutes(2));
    private static readonly CancellationToken Ct = Deadline.Token;
    private static readonly SortedDictionary<int, Peer> Peers = new();
    private static long Tick;

    private sealed class Host : IAuthoritativeSimulationHost
    {
        public SimulationSnapshot Snapshot = new(1, "Fixture World", "Fixture Save", GameSpeed.Normal,
            new[] { new SimSnapshot(55, "Fixture Sim", 18, new Vector3Dto(0, 0, 0), null, null) },
            Array.Empty<ObjectSnapshot>());
        public string Drivers = "";
        public Queue<string> Pages = new();
        public Dictionary<string, int> Calls = new();
        public Dictionary<string, int> LastNext = new();
        public Queue<(string[] Args, string Report)> Recorded = new();
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task LoadSaveAsync(string save, CancellationToken ct) => Task.CompletedTask;
        public Task LoadSaveViaMenuAsync(string save, CancellationToken ct) => Task.CompletedTask;
        public Task SaveGameAsync(string save, CancellationToken ct) => Task.CompletedTask;
        public Task MergeLotStateAsync(LotExportBundle bundle, CancellationToken ct) => Task.CompletedTask;
        public Task<CommandResult> ApplyCommandAsync(ClientCommand command, CancellationToken ct) =>
            Task.FromResult(CommandResult.Accepted(command.CommandId));
        public Task<SimulationSnapshot> GetSnapshotAsync(CancellationToken ct) => Task.FromResult(Snapshot);
        public Task<SimulationSnapshot> GetSnapshotSinceAsync(long since, CancellationToken ct) => Task.FromResult(Snapshot);
        public Task SetServerAutonomyAsync(ulong sim, bool disabled, CancellationToken ct) => Task.CompletedTask;
        public Task RegisterAutonomyGateAsync(ulong sim, CancellationToken ct) => Task.CompletedTask;
        public Task ClearAutonomyGateAsync(ulong sim, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<string?> SacsRecorderAsync(IReadOnlyList<string> args, CancellationToken ct)
        {
            if (Recorded.Count > 0)
            {
                var packet = Recorded.Dequeue();
                if (!args.SequenceEqual(packet.Args)) throw new IOException("recorder request mismatch: " + string.Join(",", args));
                return Task.FromResult<string?>(packet.Report);
            }
            if (args[0] == "snapshot") return Task.FromResult<string?>(Drivers);
            string page = Pages.Dequeue();
            Calls[args[1]] = Calls.GetValueOrDefault(args[1]) + 1;
            PresentationStream.ParseSnapshotDriver(page, out int next);
            LastNext[args[1]] = next;
            return Task.FromResult<string?>(page);
        }
    }

    private sealed class Peer : IDisposable
    {
        public readonly TcpClient Client = new();
        public readonly int Player;
        public StreamReader Reader = null!;
        public StreamWriter Writer = null!;
        public ulong Seq;
        public Peer(int player) { Player = player; }
        public async Task Connect(int port)
        {
            await Client.ConnectAsync(IPAddress.Loopback, port, Ct);
            Reader = new(Client.GetStream(), Encoding.UTF8, false, 4096, true);
            Writer = new(Client.GetStream(), new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        }
        public async Task Send(ClientCommand command) =>
            await Writer.WriteLineAsync(PublicProtocolCodec.EncodeCommand(command).AsMemory(), Ct);
        public void Dispose() { Writer.Dispose(); Reader.Dispose(); Client.Dispose(); }
    }
    private static void Emit(object value) => Console.WriteLine("CARD58 " + JsonSerializer.Serialize(value));
    private static int Int(JsonElement row, string key) => row.GetProperty(key).GetInt32();
    private static long Long(JsonElement row, string key) => row.GetProperty(key).GetInt64();
    private static string Text(JsonElement row, string key) => row.GetProperty(key).GetString()!;
    private static string Hex(string value) => Convert.ToHexString(Encoding.UTF8.GetBytes(value)).ToLowerInvariant();
    private static string Wire(object? value)
    {
        if (value == null) return "~";
        if (value is string text) return Hex(text);
        if (value is bool boolean) return boolean ? "1" : "0";
        if (value.GetType().IsEnum) return value.ToString()!;
        if (value is IEnumerable sequence) return "[" + string.Join(",", sequence.Cast<object?>().Select(Wire)) + "]";
        if (value is IFormattable number) return number.ToString(null, CultureInfo.InvariantCulture);
        return "(" + string.Join(";", value.GetType().GetProperties().Where(p => p.Name != "Hours24").Select(p => Wire(p.GetValue(value)))) + ")";
    }
    private static (string Kind, string[] Fields) Normalize(ServerEvent evt)
    {
        if (evt is ServerEvent.CommandRejected failed)
        {
            string reason = failed.Reason;
            string code = new[] { "ObjectInUse", "RouteFailed", "FailedToStart", "NotRun" }.FirstOrDefault(reason.StartsWith) ?? "bad_line";
            string[] parts = reason.Split(" | tooltip: ", 2, StringSplitOptions.None);
            return ("PickFailed", new[] { code, parts.Length > 1 ? Hex(parts[1]) : "" });
        }
        return (evt.GetType().Name, evt.GetType().GetProperties().Select(p => Wire(p.GetValue(evt))).ToArray());
    }

    // The barrier is a real read-only command with no response payload. Its accepted
    // event follows all previously queued events on that socket, so absence never
    // depends on a sleep or a timed "nothing arrived" assertion.
    private static async Task Drain(bool emit)
    {
        foreach ((int id, Peer peer) in Peers)
            await DrainPeer(id, peer, emit);
    }
    private static async Task DrainPeer(int id, Peer peer, bool emit)
    {
            Guid barrier = Guid.NewGuid();
            await peer.Send(new ClientCommand.RequestSimDescription(barrier, peer.Player, 0));
            while (true)
            {
                string line = await peer.Reader.ReadLineAsync(Ct) ?? throw new IOException("oracle connection closed");
                ServerEvent evt = PublicProtocolCodec.DecodeEvent(line) ?? throw new IOException("oracle event decode");
                if (evt is ServerEvent.CommandAccepted accepted && accepted.CommandId == barrier) break;
                if (evt is ServerEvent.CommandRejected rejected && rejected.CommandId == barrier) break;
                if (!emit || evt is ServerEvent.CommandAccepted) continue;
                var normalized = Normalize(evt);
                Emit(new { connection = id, seq = ++peer.Seq, tick = Tick, kind = normalized.Kind, fields = normalized.Fields });
            }
    }

    private static async Task<object> Card59(JsonElement row, AuthoritativeSession session)
    {
        switch (Text(row, "op"))
        {
            case "card59_folder_select":
            {
                // Section B adds the extensionless SAVE_GAME candidate to the
                // controller's stable newest-NHD ordering. This is a contract
                // adapter: HeadlessSims3Host's private helper is suffix-only.
                string save = Text(row, "save");
                string suffix = save.EndsWith(".sims3", StringComparison.OrdinalIgnoreCase) ? save : save + ".sims3";
                string? selected = row.GetProperty("roots").EnumerateArray().SelectMany(root =>
                        new[] { Path.Combine(root.GetString()!, suffix), Path.Combine(root.GetString()!, suffix[..^6]) })
                    .Where(folder => Directory.Exists(folder) && Directory.GetFiles(folder).Any(p => p.EndsWith(".nhd", StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(folder => Directory.GetFiles(folder).Where(p => p.EndsWith(".nhd", StringComparison.OrdinalIgnoreCase)).Max(File.GetLastWriteTimeUtc))
                    .FirstOrDefault();
                return new { selected = selected?.Replace('\\', '/') };
            }
            case "card59_chunks":
            {
                WorldSave world = WorldSave.ReadFolder(Text(row, "folder"), Text(row, "save"));
                return new { folder = world.FolderName, bytes = world.TotalBytes,
                    chunks = world.ToChunks().Select(c => new { file_index = c.FileIndex, file_count = c.FileCount,
                        file_name = c.FileName, offset = c.Offset, file_length = c.FileLength, data = Convert.ToBase64String(c.Data) }).ToArray() };
            }
            case "card59_writer_safety":
            {
                string root = Text(row, "root");
                string own = Path.Combine(root, "Player.sims3", "own.nhd");
                byte[] before = File.ReadAllBytes(own);
                WorldSaveWriter writer = new(root);
                bool refused = false;
                try { writer.Add(new ServerEvent.WorldFileChunk("Player.sims3", 0, 1, "world.nhd", 0, 1, [1])); }
                catch (IOException error) { refused = error.Message.Contains("wasn't written by SimsOnline", StringComparison.Ordinal); }
                return new { refused_by_name = refused, unchanged = before.SequenceEqual(File.ReadAllBytes(own)),
                    staging_exists = Directory.Exists(Path.Combine(root, "Player.sims3.incoming")) };
            }
            case "card59_reuse_decide":
            {
                WorldFingerprint? saved = WorldFingerprint.Parse(row.GetProperty("saved").GetString(), out _);
                WorldFingerprint? now = WorldFingerprint.Parse(row.GetProperty("now").GetString(), out string? parseError);
                bool whole = !row.TryGetProperty("whole", out var complete) || complete.GetBoolean();
                // Production KeepReusableWorld's whole-save guard, before Decide.
                if (!whole && LastGoodSave.Problem([new WorldSaveFile("partial.nhd", [])]) != null) saved = null;
                string? error = row.TryGetProperty("error", out var failure) ? failure.GetString() : null;
                var decision = JoinWorldReuse.Decide(saved, now, error ?? parseError);
                return new { reuse = decision.Reuse, misses = decision.Misses, detail = decision.Detail };
            }
            case "card59_rotation":
            {
                string source = Text(row, "folder");
                string scratch = Path.Combine(Text(row, "scratch"), "csharp");
                string folder = Path.Combine(scratch, "Fixture.sims3");
                if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
                Directory.CreateDirectory(folder);
                foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
                WorldSave world = WorldSave.ReadFolder(folder, "Fixture");
                string kept = LastGoodSave.Keep(folder, world, DateTimeOffset.Parse("2026-11-02T00:00:00Z", CultureInfo.InvariantCulture));
                WorldSave partial = world with { Files = world.Files.Select(f => f.Name.EndsWith(".nhd") ? f with { Data = [] } : f).ToArray() };
                bool failed = false;
                try { LastGoodSave.Keep(folder, partial, DateTimeOffset.UtcNow); }
                catch (InvalidDataException) { failed = LastGoodSave.Verify(kept) == null; }
                Directory.CreateDirectory(kept + ".incoming");
                File.Copy(Directory.GetFiles(source)[0], Path.Combine(kept + ".incoming", "partial"), true);
                bool interrupted = LastGoodSave.Verify(kept) == null;
                Directory.Delete(folder, true);
                var restored = LastGoodSave.EnsureBootable(folder, DateTimeOffset.Parse("2026-11-02T00:00:00Z", CultureInfo.InvariantCulture));
                WorldSave after = WorldSave.ReadFolder(folder, "Fixture");
                return new { kept = LastGoodSave.Verify(kept) == null, failed_keep_preserved = failed,
                    interrupted_keep_preserved = interrupted, restored = restored.Restored,
                    bytes_preserved = after.Files.Count == world.Files.Count && after.Files.Zip(world.Files).All(p => p.First.Name == p.Second.Name && p.First.Data.SequenceEqual(p.Second.Data)) };
            }
            case "card59_lot_mode":
            {
                session.ConfigureClientNativeModes(false);
                List<string[]> modes = new();
                int refresh = 0;
                foreach (var player in row.GetProperty("players").EnumerateArray())
                {
                    int id = player.GetInt32();
                    var join = await session.HandleCommandAsync(new ClientCommand.JoinSession(Guid.NewGuid(), id, "fixture"), Ct);
                    modes.AddRange(join.Events.OfType<ServerEvent.LotLocalAuthorityModeChanged>().Select(evt => Normalize(evt).Fields));
                    var refreshed = await session.RefreshLotLocalAuthorityModesWithReconcileAsync(DateTimeOffset.UtcNow, Ct);
                    refresh += refreshed.OfType<ServerEvent.LotLocalAuthorityModeChanged>().Count();
                }
                return new { modes, refresh_modes = refresh };
            }
            case "card59_rejoin_state":
            {
                var host = new Host { Snapshot = new Host().Snapshot with { Tick = 10 } };
                var cold = new AuthoritativeSession(host);
                cold.ConfigureClientNativeModes(false);
                cold.ConfigureInterestScopedMirroring(false);
                cold.ConfigurePresentationStream(true);
                await cold.StartAsync("Fixture Save", Ct);
                await cold.HandleCommandAsync(new ClientCommand.JoinSession(Guid.NewGuid(), 7, "fixture"), Ct);
                await cold.HandleCommandAsync(new ClientCommand.ClaimSim(Guid.NewGuid(), 7, 55), Ct);
                // The controller listener synthesizes LeaveSession on socket
                // disconnect; an admin-style repeated JoinSession is not cold.
                await cold.HandleCommandAsync(new ClientCommand.LeaveSession(Guid.NewGuid(), 7), Ct);
                var joined = await cold.HandleCommandAsync(new ClientCommand.JoinSession(Guid.NewGuid(), 7, "fixture"), Ct);
                var snapshot = joined.Events.OfType<ServerEvent.FullSnapshot>().Single();
                var settled = PresentationStream.ParseDump(Text(row, "presentation"), out _, out _);
                return new { snapshot = Normalize(snapshot).Fields[0], settled_presentation = settled };
            }
            default: throw new IOException("unknown Card 59 operation");
        }
    }

    public static async Task Main()
    {
        var host = new Host();
        var session = new AuthoritativeSession(host);
        session.ConfigureInterestScopedMirroring(false);
        session.ConfigurePresentationStream(true);
        await session.StartAsync("Fixture Save", Ct);
        await using var listener = new ClientListenerServer(session, "127.0.0.1:0");
        await listener.StartAsync(Ct);
        WorldTime? lastTime = null;
        long lastWall = 0;
        string? input;
        while ((input = await Console.In.ReadLineAsync(Ct)) != null)
        {
            using JsonDocument document = JsonDocument.Parse(input);
            JsonElement row = document.RootElement;
            string op = Text(row, "op");
            if (op.StartsWith("card59_", StringComparison.Ordinal))
            {
                object result = await Card59(row, session);
                Console.WriteLine("CARD59 " + JsonSerializer.Serialize(new { id = row.GetProperty("id").GetString(), result }));
                continue;
            }
            if (row.TryGetProperty("tick", out var tick)) Tick = tick.GetInt64();
            switch (op)
            {
                case "join":
                case "admin":
                {
                    int id = Int(row, "connection");
                    Peer peer = new(op == "admin" ? -99 : Int(row, "player"));
                    await peer.Connect(listener.BoundEndpoint.Port);
                    Peers.Add(id, peer);
                    if (op == "join") await peer.Send(new ClientCommand.JoinSession(Guid.NewGuid(), peer.Player, "fixture"));
                    // A barrier on an older socket can overtake this socket's
                    // JoinSession. First complete the joining socket's turn.
                    await DrainPeer(id, peer, false);
                    await Drain(false);
                    continue;
                }
                case "claim":
                    await session.HandleCommandAsync(new ClientCommand.ClaimSim(Guid.NewGuid(), Int(row, "player"), row.GetProperty("sim").GetUInt64()), Ct);
                    continue;
                case "release":
                    await session.HandleCommandAsync(new ClientCommand.ReleaseSim(Guid.NewGuid(), Int(row, "player"), row.GetProperty("sim").GetUInt64()), Ct);
                    continue;
                case "pump":
                {
                    host.Recorded = new(row.GetProperty("recorder").EnumerateArray().Select(p => (
                        p.GetProperty("args").EnumerateArray().Select(v => v.GetString()!).ToArray(), Text(p, "report"))));
                    await session.PublishPresentationAsync(async (events, ct) => {
                        foreach (var evt in events) await listener.BroadcastEventAsync(evt, ct);
                        return true;
                    }, Ct, enqueueToPlayerAsync: async (player, events, ct) => {
                        foreach (var evt in events) await listener.SendEventToPlayerAsync(player, evt, ct);
                        return true;
                    });
                    if (host.Recorded.Count != 0) throw new IOException("unused recorder inputs");
                    break;
                }
                case "event":
                {
                    ServerEvent? evt = BridgeLineCodec.TryDecodeEvent(Text(row, "line"));
                    if (evt is ServerEvent.CommandRejected failed && row.TryGetProperty("requester", out var requester))
                    {
                        typeof(AuthoritativeSession).GetMethod("RememberPickRequester", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(session, new object[] { failed.CommandId, Peers[requester.GetInt32()].Player });
                    }
                    if (evt != null) listener.TryQueueAuthoritativeEvent(evt, Ct);
                    break;
                }
                case "presentation":
                {
                    var lines = PresentationStream.ParseDump(Text(row, "report"), out _, out _);
                    for (int offset = 0; offset < lines.Count; offset += PresentationStream.MaxLinesPerEvent)
                        await listener.BroadcastEventAsync(
                            new ServerEvent.SimAnimation(lines.Skip(offset).Take(PresentationStream.MaxLinesPerEvent).ToArray()), Ct);
                    break;
                }
                case "history":
                {
                    host.Drivers = Text(row, "drivers");
                    host.Pages = new(row.GetProperty("pages").EnumerateArray().Select(p => Text(p, "report")));
                    host.Calls.Clear(); host.LastNext.Clear();
                    var method = typeof(AuthoritativeSession).GetMethod("PresentationHistoryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var history = await (Task<List<string>>)method.Invoke(session, new object?[] { new ulong[] { 55 }, Ct, null })!;
                    int trimmed = host.Calls.Count(p => p.Value == PresentationStream.MaxDumpPages && host.LastNext[p.Key] >= 0);
                    Emit(new { history_trimmed = trimmed });
                    int player = Peers[Int(row, "connection")].Player;
                    for (int offset = 0; offset < history.Count; offset += PresentationStream.MaxLinesPerEvent)
                        await listener.SendEventToPlayerAsync(player, new ServerEvent.SimAnimation(history.Skip(offset).Take(PresentationStream.MaxLinesPerEvent).ToArray()), Ct);
                    break;
                }
                case "snapshot":
                {
                    var snapshot = Snapshot2BinaryReader.TryDecode(Convert.FromBase64String(Text(row, "body"))) ?? throw new IOException("snapshot decode");
                    Tick = snapshot.Tick;
                    await listener.BroadcastEventAsync(new ServerEvent.SnapshotDelta(snapshot, row.GetProperty("force_full").GetBoolean()), Ct);
                    break;
                }
                case "routed":
                {
                    string[] fields = row.GetProperty("fields").EnumerateArray().Select(s => s.GetString()!).ToArray();
                    // Byte lists are parsed independently of the Rust codec.
                    byte[] List(string field) => JsonDocument.Parse(field).RootElement.EnumerateArray().Select(v => v.GetByte()).ToArray();
                    ServerEvent evt = Text(row, "kind") switch {
                        "SimDescriptionData" => new ServerEvent.SimDescriptionData(ulong.Parse(fields[0]), List(fields[1]), List(fields[2]), List(fields[3])),
                        "WorldFileChunk" => new ServerEvent.WorldFileChunk(Encoding.UTF8.GetString(Convert.FromHexString(fields[0])), int.Parse(fields[1]), int.Parse(fields[2]),
                            Encoding.UTF8.GetString(Convert.FromHexString(fields[3])), long.Parse(fields[4]), long.Parse(fields[5]), List(fields[6])),
                        _ => throw new IOException("unsupported routed fixture"),
                    };
                    await listener.SendEventToPlayerAsync(Peers[Int(row, "requester")].Player, evt, Ct);
                    break;
                }
                case "diag":
                {
                    long now = Long(row, "now");
                    if (WorldTimeReconcilePolicy.TryParseDiagClock(Text(row, "report"), out WorldTime time)
                        && WorldTimeReconcilePolicy.ShouldReconcile(time, lastTime, TimeSpan.FromMilliseconds(now - lastWall)))
                    {
                        lastTime = time; lastWall = now;
                        await listener.BroadcastEventAsync(new ServerEvent.WorldTimeChanged(time), Ct);
                    }
                    break;
                }
                case "cadence":
                {
                    var ack = row.GetProperty("ack");
                    var decision = ResyncPolicy.Decide(Long(row, "tick"), ack.ValueKind == JsonValueKind.Null ? 0 : ack.GetInt64(),
                        ack.ValueKind != JsonValueKind.Null, row.GetProperty("in_flight").GetBoolean(), row.GetProperty("base").GetDouble(),
                        new ResyncThresholds(Long(row, "warn"), Long(row, "hard"), row.GetProperty("maximum").GetDouble()));
                    Emit(new { action = decision.Action.ToString(), interval_ms = decision.NextIntervalMs });
                    continue;
                }
                case "bulk_metadata":
                {
                    var metadata = BridgeLineCodec.TryParseBulkRef(Text(row, "line")) ?? throw new IOException("bulk metadata");
                    Emit(new { bulk = metadata.Kind, slot = metadata.SlotIndex, payload_missing = true });
                    continue;
                }
                case "bridge_command":
                    continue;
                case "admin_reply":
                {
                    var response = BridgeLineCodec.TryDecodeResponse(Text(row, "line")) ?? throw new IOException("admin fixture decode");
                    string reply = JsonSerializer.Serialize(new {
                        ok = true, accepted = response.IsAccepted, reason = response.RejectionReason,
                        report = response.ProbeReport, snapshot = response.Snapshot == null ? null :
                            $"tick={response.Snapshot.Tick} sims={response.Snapshot.Sims.Count} objects={response.Snapshot.Objects.Count}"
                    });
                    Emit(new { admin_response = reply });
                    continue;
                }
                case "bridge_reply":
                {
                    var response = BridgeLineCodec.TryDecodeResponse(Text(row, "line"));
                    Emit(new { bridge_reply = response != null, accepted = response?.IsAccepted });
                    continue;
                }
                default: throw new IOException("unknown trace operation " + op);
            }
            await Drain(true);
        }
        foreach (Peer peer in Peers.Values) peer.Dispose();
    }
}
