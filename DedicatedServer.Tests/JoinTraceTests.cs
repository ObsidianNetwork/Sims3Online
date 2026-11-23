using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using SimsOnline.DedicatedServer.Authority;
using SimsOnline.DedicatedServer.Mirroring;
using SimsOnline.DedicatedServer.Network;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Tests;

/// <summary>
/// G14: a join writes one timing block: each step with its milliseconds and item counts, each send with the
/// outbound pump's state behind it per recipient, and the events by type with their bytes. So does the first snapshot
/// ack after it, and the joiner's presentation history.
/// </summary>
internal static partial class Program
{
    [Test("ClientListenerServer.JoinTraceWritesOneBlockPerJoin")]
    private static void ClientListenerServerJoinTraceWritesOneBlockPerJoin() =>
        RunJoinTraceWritesOneBlockPerJoinAsync().GetAwaiter().GetResult();

    private static async Task RunJoinTraceWritesOneBlockPerJoinAsync()
    {
        ConcurrentQueue<string> log = new();
        AuthoritativeSession session = await StartCloseLogSessionAsync();
        await using ClientListenerServer listener = new(session, "127.0.0.1:0", connectionLog: log.Enqueue);
        await listener.StartAsync(CancellationToken.None);
        await using OutboundTestPeer first = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);
        await first.JoinAndDrainAsync(60, "first");
        await using OutboundTestPeer joiner = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);
        await joiner.JoinAndDrainAsync(61, "joiner");

        string block = await WaitForCloseLineAsync(log, "[join] player=61 JoinSession ");
        string[] lines = block.Split('\n');
        Assert(lines.All(line => line.StartsWith("[join] player=61 ", StringComparison.Ordinal)),
            "Every line of the block should name the player: " + block);
        Assert(Regex.IsMatch(lines[0], @"^\[join\] player=61 JoinSession remote=127\.0\.0\.1:\d+ utc=\S+Z total=\d+\.\d ms outcome=accepted$"),
            "The header should give the remote, the time, the total and the outcome: " + lines[0]);
        foreach (string step in new[]
                 {
                     @" handle \d+\.\d ms events=\d+$",
                     @" command-gate \d+\.\d ms$",
                     @" snapshot-capture \d+\.\d ms sims=\d+ objects=\d+ tick=\d+$",
                     @" decorate-ownership \d+\.\d ms$",
                     @" interest-lot-index \d+\.\d ms$",
                     @" mirror-scope \d+\.\d ms sims=\d+ objects=\d+$",
                     @" lot-modes \d+\.\d ms changes=\d+$",
                     @" activate \d+\.\d ms$",
                     @" send CommandAccepted to=1 wait=\d+\.\d ms deliver=\d+\.\d ms \| pump in-flight=\S+ .*queued=\d+.* \| self pending=\d+.* \d+ B write=\d+\.\d ms ok$",
                     @" send SessionJoined to=1 wait=\d+\.\d ms deliver=\d+\.\d ms \| pump in-flight=\S+ .*queued=\d+.* \| player:60@127\.0\.0\.1:\d+ pending=\d+.* \d+ B write=\d+\.\d ms ok$",
                     @" send SessionJoined to=1 .* \| self pending=\d+.* \d+ B write=\d+\.\d ms ok$",
                     @" send FullSnapshot to=1 .* \| self pending=\d+.* \d+ B write=\d+\.\d ms ok$"
                 })
        {
            Assert(lines.Any(line => Regex.IsMatch(line, @"^\[join\] player=61 \+\d+\.\d" + step)), $"The block should have a line like '{step}': " + block);
        }
        Assert(Array.FindIndex(lines, line => line.Contains(" snapshot-capture ", StringComparison.Ordinal))
                < Array.FindIndex(lines, line => line.Contains(" send SessionJoined ", StringComparison.Ordinal)),
            "Steps are in start order: " + block);
        string events = lines[^1];
        Assert(Regex.IsMatch(events, @" events reply: CommandAccepted x1 \d+ B, FullSnapshot x1 \d+ B, .*SessionJoined x1 \d+ B.*; broadcast: SessionJoined x1 \d+ B$"),
            "The last line should tally the join's events by type with their bytes: " + events);
        Assert(log.Count(entry => entry.StartsWith("[join] player=61 JoinSession ", StringComparison.Ordinal)) == 1, "One block per join.");

        // The first snapshot ack after the join is traced too; later ones are not.
        await joiner.SendCommandAsync(new ClientCommand.AckSnapshot(Guid.NewGuid(), 61, 1L));
        string ack = await WaitForCloseLineAsync(log, "[join] player=61 AckSnapshot ");
        Assert(Regex.IsMatch(ack, @"^\[join\] player=61 AckSnapshot tick=1 since-join=\d+ ms utc=\S+ total=\d+\.\d ms outcome=accepted\n"),
            "The ack's header should say how long after the join it came: " + ack);
        Assert(ack.Contains(" send CommandAccepted to=1 ", StringComparison.Ordinal)
                && Regex.IsMatch(ack, @" events reply: CommandAccepted x1 \d+ B; broadcast: none$"),
            "The ack's block should list its reply: " + ack);
        Guid second = Guid.NewGuid();
        await joiner.SendCommandAsync(new ClientCommand.AckSnapshot(second, 61, 2L));
        while (await joiner.ReadEventAsync() is not ServerEvent.CommandAccepted accepted || accepted.CommandId != second)
        {
        }
        Assert(log.Count(entry => entry.Contains(" AckSnapshot ", StringComparison.Ordinal)) == 1, "Only the first ack after a join is traced.");
        await session.StopAsync(CancellationToken.None);
    }

    [Test("OutboundDeliveryPump.ReportsWhatItHolds")]
    private static void OutboundDeliveryPumpReportsWhatItHolds() =>
        RunOutboundDeliveryPumpReportsWhatItHoldsAsync().GetAwaiter().GetResult();

    private static async Task RunOutboundDeliveryPumpReportsWhatItHoldsAsync()
    {
        await using OutboundDeliveryPump pump = new(8, (_, _, _) => { });
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        OutboundDeliveryReceipt done = new(1, 1, 0, OutboundFailureKind.None);
        pump.TryEnqueue("slow", 1, false, async _ => { await gate.Task; return done; }, CancellationToken.None, tag: new[] { "a" });
        pump.TryEnqueue("second", 1, false, _ => Task.FromResult(done), CancellationToken.None, tag: new[] { "b" });
        pump.TryEnqueue("third", 1, false, _ => Task.FromResult(done), CancellationToken.None, tag: new[] { "a" });
        await WaitForAsync(() => pump.Pending() is { Count: > 0 } held && held[0].InFlight, TimeSpan.FromSeconds(5));

        IReadOnlyList<OutboundDeliveryPump.PendingOperation> pending = pump.Pending();
        Assert(pending.Select(op => op.Signal).SequenceEqual(new[] { "slow", "second", "third" }) && pending[0].InFlight && !pending[1].InFlight,
            "The pump should list the operation in flight first, then the queue oldest first.");
        long now = Stopwatch.GetTimestamp();
        string pumpText = ClientListenerServer.DescribePump(pending, now);
        Assert(Regex.IsMatch(pumpText, @"^in-flight=slow \d+ ms to=1 queued=2 oldest=second \d+ ms$"), "Pump: " + pumpText);
        string forA = ClientListenerServer.DescribePendingFor(pending, tag => tag is string[] targets && targets.Contains("a"), now);
        Assert(Regex.IsMatch(forA, @"^pending=2 oldest=slow \d+ ms \(in flight\)$"), "Recipient a: " + forA);
        string forB = ClientListenerServer.DescribePendingFor(pending, tag => tag is string[] targets && targets.Contains("b"), now);
        Assert(Regex.IsMatch(forB, @"^pending=1 oldest=second \d+ ms$"), "Recipient b: " + forB);
        Assert(ClientListenerServer.DescribePendingFor(pending, _ => false, now) == "pending=0", "Nothing pending for c.");

        gate.SetResult();
        await WaitForAsync(() => pump.Pending().Count == 0, TimeSpan.FromSeconds(5));
        Assert(ClientListenerServer.DescribePump(pump.Pending(), Stopwatch.GetTimestamp()) == "in-flight=none queued=0", "An idle pump.");
    }

    // The joiner-history setup (AuthoritativeSessionSendsJoinHistoryToTheJoinerOnly), with the trace captured.
    [Test("AuthoritativeSession.TracesTheJoinersHistory")]
    private static void AuthoritativeSessionTracesTheJoinersHistory()
    {
        InMemoryAuthoritativeHost host = new InMemoryAuthoritativeHost();
        AuthoritativeSession session = new AuthoritativeSession(host);
        List<string> traces = new();
        session.TraceLog = traces.Add;
        string fromNow = PresentationStream.FromNowCursor.ToString(System.Globalization.CultureInfo.InvariantCulture);
        host.SacsRecorderHandler = args => args[0] switch
        {
            "ring-on" => "recorder=on;ring=on",
            "dump" when args[1] == fromNow => "seq=10;more=0\n",
            "dump" => "seq=" + args[1] + ";more=0\n",
            "snapshot" => "drivers=7",
            "snapshot-driver" => "lines=1;next=-1\n0|A|7|1|1|2|3|0",
            _ => null
        };
        Func<IReadOnlyList<ServerEvent>, CancellationToken, Task<bool>> enqueue = (_, _) => Task.FromResult(true);
        Func<int, IReadOnlyList<ServerEvent>, CancellationToken, Task<bool>> enqueueToPlayer = (_, _, _) => Task.FromResult(true);
        SimulationSnapshot index = new SimulationSnapshot(5L, "W", "S", GameSpeed.Normal, new List<SimSnapshot>
        {
            new SimSnapshot(1uL, "Owner", 0uL, new Vector3Dto(0f, 0f, 0f), null, 1, null, 1000uL),
            new SimSnapshot(2uL, "Visitor", 0uL, new Vector3Dto(0f, 0f, 0f), null, null, null, 1000uL)
        }, Array.Empty<ObjectSnapshot>());
        session.HandleCommandAsync(new ClientCommand.JoinSession(Guid.NewGuid(), 1, "alice"), CancellationToken.None).GetAwaiter().GetResult();
        session.HandleCommandAsync(new ClientCommand.ClaimSim(Guid.NewGuid(), 1, 1uL), CancellationToken.None).GetAwaiter().GetResult();
        session.UpdateInterestLotIndex(index);
        session.PublishPresentationAsync(enqueue, CancellationToken.None, enqueueToPlayer).GetAwaiter().GetResult();
        Assert(traces.Count == 1 && traces[0].StartsWith("[join] player=1 presentation-history ", StringComparison.Ordinal)
                && traces[0].Contains("outcome=nothing to send", StringComparison.Ordinal)
                && traces[0].Contains(" gather ", StringComparison.Ordinal) && traces[0].Contains(" targets=0 drivers=0 pages=0 lines=0", StringComparison.Ordinal),
            "The first joiner's targets are all fresh (broadcast to everyone), so its own history is empty: " + string.Join(" / ", traces));
        traces.Clear();

        session.HandleCommandAsync(new ClientCommand.JoinSession(Guid.NewGuid(), 2, "bob"), CancellationToken.None).GetAwaiter().GetResult();
        session.UpdateInterestLotIndex(index);
        session.PublishPresentationAsync(enqueue, CancellationToken.None, enqueueToPlayer).GetAwaiter().GetResult();
        Assert(traces.Count == 1, "One history block per joiner: " + string.Join(" / ", traces));
        string[] lines = traces[0].Split('\n');
        Assert(Regex.IsMatch(lines[0], @"^\[join\] player=2 presentation-history utc=\S+ total=\d+\.\d ms outcome=delivered$"), "Header: " + lines[0]);
        Assert(lines.Any(line => Regex.IsMatch(line, @"^\[join\] player=2 \+0\.0 gather \d+\.\d ms targets=2 drivers=1 pages=1 lines=1$")),
            "The gather step should count the targets, drivers, pages and lines: " + traces[0]);
    }

    // with a broadcast that can leave players out (what SyncBroadcaster passes), a joiner's history is gathered
    // in the background. Awaited inline it stopped every established player's stream for 7 s per join (2026-10-21).
    // While it runs the stream goes on to everyone else; the joiner's share is held and sent after the history.
    [Test("AuthoritativeSession.HoldsTheJoinersStreamWhileItsHistoryGathers")]
    private static void AuthoritativeSessionHoldsTheJoinersStreamWhileItsHistoryGathers()
    {
        InMemoryAuthoritativeHost host = new InMemoryAuthoritativeHost();
        AuthoritativeSession session = new AuthoritativeSession(host);
        List<string> traces = new();
        session.TraceLog = traces.Add;
        using ManualResetEventSlim gate = new(initialState: true);
        Queue<string> dumps = new();
        string fromNow = PresentationStream.FromNowCursor.ToString(System.Globalization.CultureInfo.InvariantCulture);
        host.SacsRecorderHandler = args =>
        {
            switch (args[0])
            {
                case "ring-on":
                    return "recorder=on;ring=on";
                case "dump" when args[1] == fromNow:
                    return "seq=10;more=0\n";
                case "dump":
                    lock (dumps)
                    {
                        return dumps.Count > 0 ? dumps.Dequeue() : "seq=" + args[1] + ";more=0\n";
                    }
                case "snapshot":
                    gate.Wait(TimeSpan.FromSeconds(10)); // the slow bridge: one round trip per page
                    return "drivers=7";
                case "snapshot-driver":
                    return "lines=1;next=-1\n0|A|7|1|1|2|3|0";
                default:
                    return null;
            }
        };
        List<ServerEvent> broadcast = new();
        List<(int[] Except, ServerEvent Event)> partial = new();
        Dictionary<int, List<ServerEvent>> toPlayer = new();
        Func<IReadOnlyList<ServerEvent>, CancellationToken, Task<bool>> enqueue = (events, _) => { broadcast.AddRange(events); return Task.FromResult(true); };
        Func<int, IReadOnlyList<ServerEvent>, CancellationToken, Task<bool>> enqueueToPlayer = (player, events, _) =>
        {
            if (!toPlayer.TryGetValue(player, out List<ServerEvent>? list))
            {
                list = new List<ServerEvent>();
                toPlayer[player] = list;
            }
            list.AddRange(events);
            return Task.FromResult(true);
        };
        Func<IReadOnlyList<ServerEvent>, IReadOnlyCollection<int>, CancellationToken, Task<bool>> enqueueExcept = (events, except, _) =>
        {
            foreach (ServerEvent evt in events)
            {
                partial.Add((except.OrderBy(p => p).ToArray(), evt));
            }
            return Task.FromResult(true);
        };
        int Publish() => session.PublishPresentationAsync(enqueue, CancellationToken.None, enqueueToPlayer, enqueueExcept).GetAwaiter().GetResult();
        SimulationSnapshot index = new SimulationSnapshot(5L, "W", "S", GameSpeed.Normal, new List<SimSnapshot>
        {
            new SimSnapshot(1uL, "Owner", 0uL, new Vector3Dto(0f, 0f, 0f), null, 1, null, 1000uL),
            new SimSnapshot(2uL, "Visitor", 0uL, new Vector3Dto(0f, 0f, 0f), null, null, null, 1000uL)
        }, Array.Empty<ObjectSnapshot>());
        session.HandleCommandAsync(new ClientCommand.JoinSession(Guid.NewGuid(), 1, "alice"), CancellationToken.None).GetAwaiter().GetResult();
        session.HandleCommandAsync(new ClientCommand.ClaimSim(Guid.NewGuid(), 1, 1uL), CancellationToken.None).GetAwaiter().GetResult();
        session.UpdateInterestLotIndex(index);
        Publish();
        Assert(broadcast.Count == 1 && partial.Count == 0 && session.HeldJoinerCount == 0,
            "The first join gathers nothing of its own (every target is fresh, broadcast to all): nobody is held.");
        broadcast.Clear();
        traces.Clear();

        session.HandleCommandAsync(new ClientCommand.JoinSession(Guid.NewGuid(), 2, "bob"), CancellationToken.None).GetAwaiter().GetResult();
        session.UpdateInterestLotIndex(index); // the join refreshed the index from this host's empty snapshot
        gate.Reset();
        lock (dumps)
        {
            dumps.Enqueue("seq=11;more=0\n11|P|7|4|5");
        }
        Publish();
        Assert(session.HeldJoinerCount == 1 && !toPlayer.ContainsKey(2) && broadcast.Count == 0,
            "Bob's history is still being gathered: nothing for him yet, and the tick did not wait for it.");
        Assert(partial.Count == 1 && partial[0].Except.SequenceEqual(new[] { 2 }) && partial[0].Event is ServerEvent.SimAnimation live
                && live.Lines.SequenceEqual(new[] { "11|P|7|4|5" }),
            "The live line goes to everyone but bob.");
        lock (dumps)
        {
            dumps.Enqueue("seq=12;more=0\n12|R|7|9|6|0|1");
        }
        Publish();
        Assert(partial.Count == 2 && partial[1].Except.SequenceEqual(new[] { 2 }), "Still held on the next tick.");

        gate.Set();
        for (int i = 0; i < 500 && session.JoinGatherRunning; i++)
        {
            Thread.Sleep(10);
        }
        Assert(!session.JoinGatherRunning, "The gather finishes once the bridge answers.");
        lock (dumps)
        {
            dumps.Enqueue("seq=13;more=0\n13|X|7");
        }
        Publish();
        Assert(toPlayer.TryGetValue(2, out List<ServerEvent>? bobs) && bobs.Count == 1 && bobs[0] is ServerEvent.SimAnimation history
                && history.Lines.SequenceEqual(new[] { "0|A|7|1|1|2|3|0", "11|P|7|4|5", "12|R|7|9|6|0|1" }),
            "Bob gets his history, then the lines held for him, in order. Got " + string.Join(" / ", bobs?.OfType<ServerEvent.SimAnimation>().SelectMany(a => a.Lines) ?? Array.Empty<string>()));
        Assert(broadcast.Count == 1 && broadcast[0] is ServerEvent.SimAnimation after && after.Lines.SequenceEqual(new[] { "13|X|7" })
                && partial.Count == 2 && session.HeldJoinerCount == 0,
            "Then the stream goes to everyone again.");
        Assert(!toPlayer.ContainsKey(1), "Alice gets none of bob's history.");
        Assert(traces.Count == 1, "One history block for bob: " + string.Join(" / ", traces));
        string[] block = traces[0].Split('\n');
        Assert(Regex.IsMatch(block[0], @"^\[join\] player=2 presentation-history utc=\S+ total=\d+\.\d ms outcome=delivered$"), "Header: " + block[0]);
        Assert(block.Any(line => Regex.IsMatch(line, @"^\[join\] player=2 \+0\.0 gather \d+\.\d ms targets=2 drivers=1 pages=1 lines=1$"))
                && block.Any(line => Regex.IsMatch(line, @"^\[join\] player=2 \+0\.0 hold \d+\.\d ms lines=2 ticks=2$")),
            "The block shows the gather and the hold: " + traces[0]);
    }
}
