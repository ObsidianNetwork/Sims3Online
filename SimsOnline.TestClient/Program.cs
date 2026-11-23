using System.Globalization;
using System.Net.Sockets;
using System.Text;
using SimsOnline.DedicatedServer.Protocol;
using SimsOnline.Client;

namespace SimsOnline.TestClient;

/// <summary>
/// P3.0-P3.4 minimal CLI test client for the SimsOnline dedicated controller's public TCP listener.
///
/// Modes (combinable):
///   default (no --claim-sim, no --delta-snapshot): JoinSession + RequestSnapshot + LeaveSession (P3.0 smoke)
///   --claim-sim N:                                 P3.1 ownership lifecycle (claim -> idempotent -> release -> claim -> auto-release-on-leave)
///   --claim-sim N --queue-x/y/z:                   P3.2 + ownership lifecycle wrapped around a SetGameSpeed(Speed3) + QueueInteraction(Terrain.GoHere)
///   --delta-snapshot:                              P3.3 paused-world full vs delta snapshot comparison
///   --claim-sim N --rival-sim M:                   P3.4 cross-ownership verification - attempts to claim and queue M (expects rejection)
///
/// Read pattern (P3.4):
///   AwaitResponse is commandId-aware: reads lines until it sees a command_accepted or command_rejected
///   for the issued commandId. Broadcast events from other clients are printed but do not satisfy the
///   response wait. This makes the client multi-client safe.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string endpoint = "127.0.0.1:34019";
        string wire = "json";
        int playerId = 1;
        string displayName = "probe";
        int connectTimeoutSeconds = 30;
        int readTimeoutSeconds = 30;
        ulong? claimSimId = null;
        ulong? rivalSimId = null;
        bool skipSnapshot = false;
        float? queueX = null;
        float? queueY = null;
        float? queueZ = null;
        int interactionWaitSeconds = 60;
        bool deltaSnapshot = false;
        int deltaWaitSeconds = 5;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--endpoint" && i + 1 < args.Length) endpoint = args[++i];
            else if (arg == "--wire" && i + 1 < args.Length) wire = PublicWireConnection.ValidateMode(args[++i]);
            else if (arg == "--player" && i + 1 < args.Length) playerId = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (arg == "--display" && i + 1 < args.Length) displayName = args[++i];
            else if (arg == "--connect-timeout-seconds" && i + 1 < args.Length) connectTimeoutSeconds = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (arg == "--read-timeout-seconds" && i + 1 < args.Length) readTimeoutSeconds = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (arg == "--claim-sim" && i + 1 < args.Length) claimSimId = ulong.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (arg == "--rival-sim" && i + 1 < args.Length) rivalSimId = ulong.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (arg == "--skip-snapshot") skipSnapshot = true;
            else if (arg == "--queue-x" && i + 1 < args.Length) queueX = float.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (arg == "--queue-y" && i + 1 < args.Length) queueY = float.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (arg == "--queue-z" && i + 1 < args.Length) queueZ = float.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (arg == "--interaction-wait-seconds" && i + 1 < args.Length) interactionWaitSeconds = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (arg == "--delta-snapshot") deltaSnapshot = true;
            else if (arg == "--delta-wait-seconds" && i + 1 < args.Length) deltaWaitSeconds = int.Parse(args[++i], CultureInfo.InvariantCulture);
        }

        string[] parts = endpoint.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int port))
        {
            Console.Error.WriteLine($"Invalid endpoint: {endpoint}");
            return 1;
        }

        Console.WriteLine($"TestClient connecting to {endpoint} as player={playerId} display=\"{displayName}\" claimSim={(claimSimId.HasValue ? claimSimId.Value.ToString(CultureInfo.InvariantCulture) : "(none)")} rivalSim={(rivalSimId.HasValue ? rivalSimId.Value.ToString(CultureInfo.InvariantCulture) : "(none)")}...");

        using TcpClient client = new();
        using CancellationTokenSource connectCts = new(TimeSpan.FromSeconds(connectTimeoutSeconds));
        try
        {
            await client.ConnectAsync(parts[0], port, connectCts.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Connect failed: {ex.Message}");
            return 1;
        }

        Console.WriteLine("Connected.");
        await using NetworkStream stream = client.GetStream();
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        using CancellationTokenSource readCts = new(TimeSpan.FromSeconds(readTimeoutSeconds));
        PublicWireConnection? lineWire = wire == "line" ? new(Console.Error.WriteLine) : null;
        if (lineWire != null) await PublicWireConnection.HandshakeAsync(stream, reader, "client", readCts.Token);

        async Task<string?> ReadAsync(CancellationToken token) => lineWire != null
            ? await lineWire.ReadJsonAsync(reader, token) : await reader.ReadLineAsync(token);

        static string Truncate(string s) => s.Length > 220 ? s.Substring(0, 220) + $"... (+{s.Length - 220} bytes)" : s;

        async Task WriteAsync(ClientCommand cmd)
        {
            string line = (lineWire?.EncodeCommand(cmd) ?? PublicProtocolCodec.EncodeCommand(cmd)) + "\n";
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            await stream.WriteAsync(bytes, readCts.Token);
            await stream.FlushAsync(readCts.Token);
            Console.WriteLine($"-> {Truncate(line.TrimEnd('\n'))}");
        }

        // Reads until a command_accepted or command_rejected for the given commandId is seen.
        // Returns true on accept, false on reject. Prints every line (including broadcasts from
        // other clients). Throws on read error / connection close.
        async Task<bool> AwaitResponseAsync(Guid commandId, string label)
        {
            string idText = commandId.ToString();
            while (true)
            {
                string? line = await ReadAsync(readCts.Token);
                if (line == null)
                {
                    throw new IOException($"Connection closed waiting for {label} response.");
                }

                if (string.IsNullOrWhiteSpace(line)) continue;
                Console.WriteLine($"<- {Truncate(line)}");

                if (line.Contains("\"command_accepted\"", StringComparison.Ordinal) && line.Contains(idText, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (line.Contains("\"command_rejected\"", StringComparison.Ordinal) && line.Contains(idText, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        // Same as AwaitResponseAsync but also captures the FullSnapshot event when it arrives.
        async Task<(bool accepted, SimulationSnapshot? snapshot)> AwaitSnapshotResponseAsync(Guid commandId, string label)
        {
            string idText = commandId.ToString();
            bool gotResponse = false;
            bool accepted = false;
            SimulationSnapshot? captured = null;
            while (!gotResponse || captured == null)
            {
                string? line = await ReadAsync(readCts.Token);
                if (line == null)
                {
                    Console.Error.WriteLine($"Connection closed waiting for {label} snapshot.");
                    return (accepted, captured);
                }

                if (string.IsNullOrWhiteSpace(line)) continue;
                Console.WriteLine($"<- {Truncate(line)}");

                if (!gotResponse && line.Contains(idText, StringComparison.OrdinalIgnoreCase))
                {
                    if (line.Contains("\"command_accepted\"", StringComparison.Ordinal)) { gotResponse = true; accepted = true; }
                    else if (line.Contains("\"command_rejected\"", StringComparison.Ordinal)) { gotResponse = true; accepted = false; return (false, null); }
                }

                if (captured == null && line.Contains("\"full_snapshot\"", StringComparison.Ordinal))
                {
                    try
                    {
                        ServerEvent evt = PublicProtocolCodec.DecodeEvent(line);
                        if (evt is ServerEvent.FullSnapshot fs) { captured = fs.Snapshot; }
                    }
                    catch { }
                }
            }

            return (accepted, captured);
        }

        async Task<(bool started, bool completed)> AwaitInteractionCompletionAsync(ulong actorId, int maxSeconds)
        {
            string actorPattern = "\"actorObjectId\":" + actorId.ToString(CultureInfo.InvariantCulture);
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(maxSeconds));
            bool started = false;
            bool completed = false;
            while (!completed)
            {
                string? line;
                try { line = await ReadAsync(cts.Token); }
                catch (OperationCanceledException) { break; }
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                Console.WriteLine($"<- {Truncate(line)}");
                bool matchesActor = line.Contains(actorPattern, StringComparison.Ordinal);
                if (line.Contains("\"interaction_started\"", StringComparison.Ordinal) && matchesActor) started = true;
                else if (line.Contains("\"interaction_completed\"", StringComparison.Ordinal) && matchesActor) completed = true;
                else if (line.Contains("\"interaction_failed\"", StringComparison.Ordinal) && matchesActor) { completed = true; }
            }
            return (started, completed);
        }

        // ===== Lifecycle =====

        // 1) JoinSession
        Guid joinId = Guid.NewGuid();
        await WriteAsync(new ClientCommand.JoinSession(joinId, playerId, displayName));
        if (!await AwaitResponseAsync(joinId, "JoinSession")) { Console.Error.WriteLine("JoinSession rejected."); return 1; }

        // 2a) Delta-snapshot mode (mutually exclusive with --claim-sim)
        if (deltaSnapshot && !claimSimId.HasValue)
        {
            Guid pauseId = Guid.NewGuid();
            await WriteAsync(new ClientCommand.SetGameSpeed(pauseId, playerId, GameSpeed.Pause));
            if (!await AwaitResponseAsync(pauseId, "SetGameSpeed(Pause)")) return 1;

            Guid fullId = Guid.NewGuid();
            await WriteAsync(new ClientCommand.RequestSnapshot(fullId, playerId, null));
            var fullResult = await AwaitSnapshotResponseAsync(fullId, "RequestSnapshot(full)");
            if (!fullResult.accepted || fullResult.snapshot == null) { Console.Error.WriteLine("Full snapshot failed."); return 1; }
            SimulationSnapshot full = fullResult.snapshot;
            Console.WriteLine(FormattableString.Invariant($"Full snapshot: tick={full.Tick} sims={full.Sims.Count} objects={full.Objects.Count}"));

            Console.WriteLine($"Waiting {deltaWaitSeconds} s while paused...");
            await Task.Delay(TimeSpan.FromSeconds(deltaWaitSeconds), readCts.Token);

            Guid deltaId = Guid.NewGuid();
            await WriteAsync(new ClientCommand.RequestSnapshot(deltaId, playerId, full.Tick));
            var deltaResult = await AwaitSnapshotResponseAsync(deltaId, "RequestSnapshot(delta)");
            if (!deltaResult.accepted || deltaResult.snapshot == null) { Console.Error.WriteLine("Delta snapshot failed."); return 1; }
            SimulationSnapshot delta = deltaResult.snapshot;
            Console.WriteLine(FormattableString.Invariant($"Delta snapshot: tick={delta.Tick} sims={delta.Sims.Count} objects={delta.Objects.Count} (sinceTick={full.Tick})"));

            int totalFull = full.Sims.Count + full.Objects.Count;
            int totalDelta = delta.Sims.Count + delta.Objects.Count;
            double reduction = totalFull > 0 ? 100.0 * (1.0 - (double)totalDelta / totalFull) : 0;
            Console.WriteLine(FormattableString.Invariant($"Delta reduction: {totalDelta}/{totalFull} entities ({reduction:F1}% smaller)"));

            Guid leaveId = Guid.NewGuid();
            await WriteAsync(new ClientCommand.LeaveSession(leaveId, playerId));
            await AwaitResponseAsync(leaveId, "LeaveSession");
            Console.WriteLine("TestClient delta-snapshot round-trip complete.");
            return 0;
        }

        // 2b) Default mode: full RequestSnapshot (unless skipped)
        if (!skipSnapshot)
        {
            Guid snapId = Guid.NewGuid();
            await WriteAsync(new ClientCommand.RequestSnapshot(snapId, playerId, null));
            if (!await AwaitResponseAsync(snapId, "RequestSnapshot")) return 1;
        }

        // 3) P3.4 cross-ownership reject verification (rival expects rejection)
        int rivalRejectCount = 0;
        if (rivalSimId.HasValue)
        {
            ulong rival = rivalSimId.Value;
            Console.WriteLine($"Attempting cross-ownership ClaimSim on rival actor {rival} (expecting rejection)...");
            Guid rivalClaimId = Guid.NewGuid();
            await WriteAsync(new ClientCommand.ClaimSim(rivalClaimId, playerId, rival));
            bool rivalClaimOk = await AwaitResponseAsync(rivalClaimId, "ClaimSim(rival)");
            if (rivalClaimOk)
            {
                Console.Error.WriteLine($"FAIL: cross-ownership ClaimSim was unexpectedly accepted.");
                return 1;
            }
            Console.WriteLine("OK: cross-ownership ClaimSim rejected as expected.");
            rivalRejectCount++;

            // Only attempt the queue-rival test if we have coordinates
            if (queueX.HasValue && queueY.HasValue && queueZ.HasValue)
            {
                Console.WriteLine($"Attempting cross-ownership QueueInteraction on rival actor {rival} (expecting rejection)...");
                InteractionRequest rivalInteraction = new(rival, "Terrain.GoHere", null, new Vector3Dto(queueX.Value, queueY.Value, queueZ.Value), new Dictionary<string, string>());
                Guid rivalQueueId = Guid.NewGuid();
                await WriteAsync(new ClientCommand.QueueInteraction(rivalQueueId, playerId, rivalInteraction));
                bool rivalQueueOk = await AwaitResponseAsync(rivalQueueId, "QueueInteraction(rival)");
                if (rivalQueueOk)
                {
                    Console.Error.WriteLine($"FAIL: cross-ownership QueueInteraction was unexpectedly accepted.");
                    return 1;
                }
                Console.WriteLine("OK: cross-ownership QueueInteraction rejected as expected.");
                rivalRejectCount++;
            }
        }

        // 4) P3.1/P3.2 ownership cycle on own actor (only if --claim-sim was provided)
        if (claimSimId.HasValue)
        {
            ulong simId = claimSimId.Value;

            Guid claimId = Guid.NewGuid();
            await WriteAsync(new ClientCommand.ClaimSim(claimId, playerId, simId));
            if (!await AwaitResponseAsync(claimId, "ClaimSim(own)")) { Console.Error.WriteLine("Own ClaimSim rejected!"); return 1; }

            // Optional QueueInteraction on own actor
            if (queueX.HasValue && queueY.HasValue && queueZ.HasValue)
            {
                Guid speedId = Guid.NewGuid();
                await WriteAsync(new ClientCommand.SetGameSpeed(speedId, playerId, GameSpeed.Speed3));
                if (!await AwaitResponseAsync(speedId, "SetGameSpeed(Speed3)")) return 1;

                InteractionRequest interaction = new(simId, "Terrain.GoHere", null, new Vector3Dto(queueX.Value, queueY.Value, queueZ.Value), new Dictionary<string, string>());
                Guid queueId = Guid.NewGuid();
                await WriteAsync(new ClientCommand.QueueInteraction(queueId, playerId, interaction));
                if (!await AwaitResponseAsync(queueId, "QueueInteraction(own)")) { Console.Error.WriteLine("QueueInteraction rejected."); return 1; }

                Console.WriteLine($"Waiting up to {interactionWaitSeconds} s for InteractionStarted + InteractionCompleted on actor {simId}...");
                var (started, completed) = await AwaitInteractionCompletionAsync(simId, interactionWaitSeconds);
                if (!started) Console.Error.WriteLine("Did not observe InteractionStarted within window.");
                if (!completed) Console.Error.WriteLine("Did not observe InteractionCompleted within window.");
            }

            // Idempotent re-claim
            Guid idemId = Guid.NewGuid();
            await WriteAsync(new ClientCommand.ClaimSim(idemId, playerId, simId));
            if (!await AwaitResponseAsync(idemId, "ClaimSim(idempotent)")) return 1;

            // Release
            Guid releaseId = Guid.NewGuid();
            await WriteAsync(new ClientCommand.ReleaseSim(releaseId, playerId, simId));
            if (!await AwaitResponseAsync(releaseId, "ReleaseSim")) return 1;

            // Re-claim after release
            Guid reclaimId = Guid.NewGuid();
            await WriteAsync(new ClientCommand.ClaimSim(reclaimId, playerId, simId));
            if (!await AwaitResponseAsync(reclaimId, "ClaimSim(after-release)")) return 1;

            // LeaveSession (auto-releases)
            Guid leaveOwnId = Guid.NewGuid();
            await WriteAsync(new ClientCommand.LeaveSession(leaveOwnId, playerId));
            await AwaitResponseAsync(leaveOwnId, "LeaveSession(auto-release)");
        }
        else
        {
            // 5) Default: LeaveSession (no claims)
            Guid leaveId = Guid.NewGuid();
            await WriteAsync(new ClientCommand.LeaveSession(leaveId, playerId));
            await AwaitResponseAsync(leaveId, "LeaveSession");
        }

        if (rivalSimId.HasValue)
        {
            Console.WriteLine($"P3.4 cross-ownership verification: observed {rivalRejectCount} rival rejection(s).");
        }

        Console.WriteLine("TestClient round-trip complete.");
        return 0;
    }
}
