using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using SimsOnline.DedicatedServer.Mirroring;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.Client;

/// <summary>
/// P5.6: client-side unified resync coordinator. Owns the drain loop previously
/// inlined at Program.cs:386-481 and adds cross-delta coalescing, ForceFull drain-and-discard,
/// and debounced single-arbiter RequestSnapshot.
///
/// Design load-bearing points honored:
/// LP-1: adaptive cadence never drops a delta -- coalescing is reader-side; socket reader stays
///        non-blocking; the unbounded channel (LP-7) keeps invariant.
/// LP-2: coalescing at whole-SimSnapshot-record granularity, latest-tick-wins (never field-level).
/// LP-3: events (Interaction*/claim/autonomy/speed-transition) never coalesced across types -- in tick order.
///        exception: multiple InteractionQueued / InteractionReplayQueued lines in the
///        same greedy drain batch are chain-coalesced or batch-dispatched to cut bridge round-trips.
/// LP-4: drift discriminator on per-raw-delta (tick-contiguity), NOT the coalesced endpoint.
/// LP-6: single RequestSnapshot arbiter -- the lone drift path (Program.cs:392-394) moved here,
///        debounced; server ForceFull in flight suppresses client requests.
/// LP-7: unbounded channel preserved; coalescing is reader-side only.
/// LP-8: preserved -- all dispatch goes through TryMirrorDispatchAsync.
///
/// The drain loop is the one owner of the dispatch path's state. Work from other threads (timers) reaches it through
/// <see cref="Post"/>, and runs between lines, in channel order.
/// </summary>
public sealed class MirrorCoordinator
{
    // Post writes this into the channel, and the loop runs one queued work item per marker. It is matched by reference,
    // so no line read from the socket can be taken for it.
    private static readonly string PostedWorkMarker = new("(posted work)".ToCharArray());

    private readonly CoordinatorConfig _config;
    private readonly System.Collections.Concurrent.ConcurrentQueue<Func<CancellationToken, Task>> _posted = new();

    // Drift / resync state.
    private long _lastContiguousTick = -1;   // last raw-delta tick seen (for tick-contiguity LP-4)
    private DateTime _lastRequestSnapshotAt = DateTime.MinValue;
    private bool _serverForceFullInFlight;    // suppresses client requests when ForceFull arrived (LP-6)

    public MirrorCoordinator(CoordinatorConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Run <paramref name="work"/> on the drain loop, after the lines already in the channel <see cref="RunAsync"/> reads
    /// (<paramref name="write"/> writes to it). Safe from any thread. Posted work is not a line: it is not counted and
    /// <c>afterLine</c> does not see it. Returns false when the channel is closed: the work will not run.
    /// </summary>
    public bool Post(Func<string, bool> write, Func<CancellationToken, Task> work)
    {
        _posted.Enqueue(work);
        return write(PostedWorkMarker);
    }

    /// <summary>
    /// The next line for a reader that runs before <see cref="RunAsync"/> on the same channel (the client's join drain,
    /// which dispatches inline): posted work met on the way runs here, in channel order, as the loop would run it. Null
    /// once the channel is done; a channel completed with an error throws it.
    /// </summary>
    public async Task<string?> ReadLineAsync(ChannelReader<string> reader, bool jsonStdout, CancellationToken cancellationToken)
    {
        while (await reader.WaitToReadAsync(cancellationToken))
        {
            while (reader.TryRead(out string? line))
            {
                if (!IsPostedWork(line))
                {
                    return line;
                }

                await RunPostedWorkAsync(jsonStdout, cancellationToken);
            }
        }

        return null;
    }

    private static bool IsPostedWork(string line) => ReferenceEquals(line, PostedWorkMarker);

    private async Task RunPostedWorkAsync(bool jsonStdout, CancellationToken cancellationToken)
    {
        if (!_posted.TryDequeue(out Func<CancellationToken, Task>? work))
        {
            return;
        }

        try
        {
            await work(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Like a failed dispatch: logged, and the drain loop goes on.
            PostedWorkFailedLog(jsonStdout, ex.Message);
        }
    }

    // -----------------------------------------------------------------------
    // Pure static: coalesce a batch of snapshot deltas (LP-2, LP-3).
    // Latest-tick-wins at whole-SimSnapshot-record granularity.
    // Objects similarly coalesced latest-tick-wins.
    // The result tick/metadata come from the highest-tick delta in the batch.
    // -----------------------------------------------------------------------

    /// <summary>
    /// P5.6 LP-2: coalesce a list of position-bearing snapshot deltas into a single merged delta.
    /// Latest-tick-wins per ObjectId at whole-record granularity (never field-level merge).
    /// The result's Tick, Speed, WorldName, SaveName come from the highest-tick delta.
    /// Objects are coalesced the same way. Never called with an empty list.
    /// </summary>
    public static SimulationSnapshot Merge(IReadOnlyList<SimulationSnapshot> deltas)
    {
        if (deltas.Count == 0) throw new ArgumentException("Cannot merge an empty delta list.", nameof(deltas));
        if (deltas.Count == 1) return deltas[0];

        // Per-ObjectId latest-wins tracking for Sims and Objects.
        Dictionary<ulong, (long tick, SimSnapshot sim)> simMap = new(capacity: deltas[0].Sims.Count * 2);
        Dictionary<ulong, (long tick, ObjectSnapshot obj)> objMap = new(capacity: deltas[0].Objects.Count * 2);

        SimulationSnapshot? highestTickDelta = null;

        foreach (SimulationSnapshot delta in deltas)
        {
            if (highestTickDelta == null || delta.Tick > highestTickDelta.Tick)
            {
                highestTickDelta = delta;
            }

            foreach (SimSnapshot sim in delta.Sims)
            {
                if (!simMap.TryGetValue(sim.ObjectId, out (long tick, SimSnapshot) existing)
                    || delta.Tick >= existing.tick)
                {
                    simMap[sim.ObjectId] = (delta.Tick, sim);
                }
            }

            foreach (ObjectSnapshot obj in delta.Objects)
            {
                if (!objMap.TryGetValue(obj.ObjectId, out (long tick, ObjectSnapshot) existing)
                    || delta.Tick >= existing.tick)
                {
                    objMap[obj.ObjectId] = (delta.Tick, obj);
                }
            }
        }

        SimulationSnapshot best = highestTickDelta!;

        List<SimSnapshot> mergedSims = new(simMap.Count);
        foreach ((long _, SimSnapshot sim) in simMap.Values) mergedSims.Add(sim);

        List<ObjectSnapshot> mergedObjs = new(objMap.Count);
        foreach ((long _, ObjectSnapshot obj) in objMap.Values) mergedObjs.Add(obj);

        return best with { Sims = mergedSims, Objects = mergedObjs };
    }

    // -----------------------------------------------------------------------
    // RunAsync: replaces Program.cs drain loop (lines 412-481).
    // -----------------------------------------------------------------------

    /// <summary>
    /// P5.6: drain the unbounded channel, coalesce snapshot deltas (LP-2), dispatch events
    /// in order (LP-3), handle ForceFull (LP-6), ACK highest applied tick (through TryMirrorDispatchAsync).
    ///
    /// The <paramref name="afterLine"/> callback is called after each raw line is fully processed
    /// (either dispatched directly or as the last line of a coalesced batch). It receives the raw
    /// line and the current event count; return true to break the drain loop (integration-test exits).
    /// </summary>
    public async Task RunAsync(
        ChannelReader<string> reader,
        MirrorEngine? mirrorEngine,
        bool jsonStdout,
        CancellationToken cancellationToken,
        Func<ClientCommand, Task> sendCommandAsync,
        Func<MirrorEngine?, string, bool, CancellationToken, Func<ClientCommand, Task>, Task<bool>> tryMirrorDispatch,
        Func<string, int, Task<bool>>? afterLine = null,
        Func<MirrorEngine?, IReadOnlyList<ServerEvent.InteractionQueued>, bool, CancellationToken, Func<ClientCommand, Task>, Task<bool>>? tryMirrorDispatchInteractionBatch = null,
        Func<MirrorEngine?, IReadOnlyList<ServerEvent.InteractionReplayQueued>, bool, CancellationToken, Func<ClientCommand, Task>, Task<bool>>? tryMirrorDispatchInteractionReplayBatch = null)
    {
        int eventCount = 0;

        await foreach (string firstLine in reader.ReadAllAsync(cancellationToken))
        {
            if (IsPostedWork(firstLine))
            {
                await RunPostedWorkAsync(jsonStdout, cancellationToken);
                continue;
            }

            // Greedy drain: collect everything currently available (bounded by MaxFoldPerCycle).
            List<string> batch = new List<string>(_config.MaxFoldPerCycle) { firstLine };
            DrainAvailable(reader, batch);
            if (batch.Count == 1)
            {
                await ReadAheadInteractionBatchAsync(reader, batch, cancellationToken);
            }

            if (batch.Count == 1)
            {
                // Fast path: no coalescing.
                bool drift = await tryMirrorDispatch(mirrorEngine, firstLine, jsonStdout, cancellationToken, sendCommandAsync);
                await HandleDriftAsync(drift, sendCommandAsync, jsonStdout, cancellationToken);
                eventCount++;
                if (afterLine != null && await afterLine(firstLine, eventCount)) break;
                continue;
            }

            // Multi-item path: coalesce snapshot deltas, pass events through in order.
            (bool shouldBreak, int newCount) = await ProcessBatchAsync(
                batch, mirrorEngine, jsonStdout, cancellationToken, sendCommandAsync, tryMirrorDispatch,
                afterLine, eventCount, tryMirrorDispatchInteractionBatch, tryMirrorDispatchInteractionReplayBatch);
            eventCount = newCount;
            if (shouldBreak) break;
        }
    }

    private static void DrainAvailable(ChannelReader<string> reader, List<string> batch)
    {
        while (batch.Count < batch.Capacity && reader.TryRead(out string? next))
        {
            batch.Add(next!);
        }
    }

    private async Task ReadAheadInteractionBatchAsync(
        ChannelReader<string> reader,
        List<string> batch,
        CancellationToken cancellationToken)
    {
        TimeSpan readAhead = _config.InteractionBatchReadAhead ?? TimeSpan.Zero;
        if (readAhead <= TimeSpan.Zero
            || batch.Count >= _config.MaxFoldPerCycle
            || !ShouldReadAheadInteraction(batch[0]))
        {
            return;
        }

        DateTime deadline = DateTime.UtcNow + readAhead;
        while (batch.Count < _config.MaxFoldPerCycle)
        {
            DrainAvailable(reader, batch);
            if (batch.Count >= _config.MaxFoldPerCycle)
            {
                return;
            }

            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            Task<bool> waitTask = reader.WaitToReadAsync(cancellationToken).AsTask();
            Task delayTask = Task.Delay(remaining, cancellationToken);
            Task completed = await Task.WhenAny(waitTask, delayTask);
            if (completed != waitTask)
            {
                return;
            }

            if (!await waitTask)
            {
                return;
            }
        }
    }

    private static bool ShouldReadAheadInteraction(string line)
    {
        try
        {
            ServerEvent decoded = PublicProtocolCodec.DecodeEvent(line);
            return decoded is ServerEvent.InteractionQueued or ServerEvent.InteractionReplayQueued;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Process a greedily-drained multi-item batch: coalesce snapshot deltas by speed group,
    /// dispatch events in order, flush accumulated positions before each non-delta item.
    /// Returns (shouldBreak, updatedEventCount).
    /// </summary>
    private async Task<(bool ShouldBreak, int EventCount)> ProcessBatchAsync(
        List<string> batch,
        MirrorEngine? mirrorEngine,
        bool jsonStdout,
        CancellationToken cancellationToken,
        Func<ClientCommand, Task> sendCommandAsync,
        Func<MirrorEngine?, string, bool, CancellationToken, Func<ClientCommand, Task>, Task<bool>> tryMirrorDispatch,
        Func<string, int, Task<bool>>? afterLine,
        int eventCount,
        Func<MirrorEngine?, IReadOnlyList<ServerEvent.InteractionQueued>, bool, CancellationToken, Func<ClientCommand, Task>, Task<bool>>? tryMirrorDispatchInteractionBatch,
        Func<MirrorEngine?, IReadOnlyList<ServerEvent.InteractionReplayQueued>, bool, CancellationToken, Func<ClientCommand, Task>, Task<bool>>? tryMirrorDispatchInteractionReplayBatch)
    {
        // Accumulated snapshot deltas for coalescing. Items from the same speed group fold together.
        List<(SimulationSnapshot snapshot, string rawLine)> pendingDeltas = new();
        GameSpeed? pendingSpeed = null;

        // InteractionQueued lines accumulate in arrival order; chain-aware coalesce at flush.
        List<(ServerEvent.InteractionQueued evt, string rawLine)> pendingInteractions = new();
        // InteractionReplayQueued lines also need same-window batching for GetBook/Sit/ReadBook.
        List<(ServerEvent.InteractionReplayQueued evt, string rawLine)> pendingReplayInteractions = new();

        // reconcile can burst WorldTimeChanged while snapshot applies backlog; keep latest only.
        string? pendingWorldTimeLine = null;

        // SimAnimation events in a row go out as one dispatch. Over the bridge each event costs at least one SACS_REPLAY
        // round trip (about a game tick), and the server ring path sends an event per simulator update, so one event
        // at a time fell behind for good (push-098: 2,500 events in flight, routes 100 s late, and the client ring never
        // took over because it waits for the bridge to drain). The bridge still splits the merged lines into
        // SACS_REPLAY-sized batches, in order; afterLine runs for every raw line.
        List<(IReadOnlyList<string> Lines, string RawLine)> pendingAnimations = new();

        async Task<bool> FlushAnimationsAsync()
        {
            if (pendingAnimations.Count == 0)
            {
                return false;
            }

            string dispatchLine = pendingAnimations.Count == 1
                ? pendingAnimations[0].RawLine
                : PublicProtocolCodec.EncodeEvent(new ServerEvent.SimAnimation(pendingAnimations.SelectMany(static a => a.Lines).ToList()));
            List<string> rawLines = pendingAnimations.Select(static a => a.RawLine).ToList();
            pendingAnimations.Clear();
            bool drift = await tryMirrorDispatch(mirrorEngine, dispatchLine, jsonStdout, cancellationToken, sendCommandAsync);
            await HandleDriftAsync(drift, sendCommandAsync, jsonStdout, cancellationToken);
            foreach (string rawLine in rawLines)
            {
                eventCount++;
                if (afterLine != null && await afterLine(rawLine, eventCount))
                {
                    return true;
                }
            }

            return false;
        }

        async Task<bool> FlushWorldTimeAsync()
        {
            if (pendingWorldTimeLine is not string line)
            {
                return false;
            }

            pendingWorldTimeLine = null;

            bool drift = await tryMirrorDispatch(mirrorEngine, line, jsonStdout, cancellationToken, sendCommandAsync);
            await HandleDriftAsync(drift, sendCommandAsync, jsonStdout, cancellationToken);
            eventCount++;
            if (afterLine != null && await afterLine(line, eventCount))
            {
                return true;
            }

            return false;
        }

        async Task<bool> FlushInteractionsAsync()
        {
            if (pendingInteractions.Count == 0)
            {
                return false;
            }

            int folded = pendingInteractions.Count;
            IReadOnlyList<ServerEvent.InteractionQueued> coalesced =
                InteractionQueuedCoalescer.CoalesceForBatch(
                    pendingInteractions.Select(static v => v.evt));

            if (coalesced.Count >= 2 && tryMirrorDispatchInteractionBatch != null)
            {
                bool drift = await tryMirrorDispatchInteractionBatch(
                    mirrorEngine, coalesced, jsonStdout, cancellationToken, sendCommandAsync);
                await HandleDriftAsync(drift, sendCommandAsync, jsonStdout, cancellationToken);
                InteractionCoalesceLog(jsonStdout, folded, coalesced.Count);
            }
            else
            {
                foreach (ServerEvent.InteractionQueued iq in coalesced)
                {
                    string mergedJson = JsonSerializer.Serialize(
                        (ServerEvent)iq,
                        PublicProtocolCodec.JsonOptions);
                    bool drift = await tryMirrorDispatch(
                        mirrorEngine, mergedJson, jsonStdout, cancellationToken, sendCommandAsync);
                    await HandleDriftAsync(drift, sendCommandAsync, jsonStdout, cancellationToken);
                }
            }

            foreach ((ServerEvent.InteractionQueued _, string rawLine) in pendingInteractions)
            {
                eventCount++;
                if (afterLine != null && await afterLine(rawLine, eventCount))
                {
                    pendingInteractions.Clear();
                    return true;
                }
            }

            pendingInteractions.Clear();
            return false;
        }

        async Task<bool> FlushReplayInteractionsAsync()
        {
            if (pendingReplayInteractions.Count == 0)
            {
                return false;
            }

            int folded = pendingReplayInteractions.Count;
            List<ServerEvent.InteractionReplayQueued> ordered =
                new List<ServerEvent.InteractionReplayQueued>(pendingReplayInteractions.Count);
            foreach ((ServerEvent.InteractionReplayQueued evt, string _) in pendingReplayInteractions)
            {
                ordered.Add(evt);
            }

            if (ordered.Count >= 2 && tryMirrorDispatchInteractionReplayBatch != null)
            {
                bool drift = await tryMirrorDispatchInteractionReplayBatch(
                    mirrorEngine, ordered, jsonStdout, cancellationToken, sendCommandAsync);
                await HandleDriftAsync(drift, sendCommandAsync, jsonStdout, cancellationToken);
                InteractionReplayCoalesceLog(jsonStdout, folded, ordered.Count);
            }
            else
            {
                foreach (ServerEvent.InteractionReplayQueued irq in ordered)
                {
                    string mergedJson = JsonSerializer.Serialize(
                        (ServerEvent)irq,
                        PublicProtocolCodec.JsonOptions);
                    bool drift = await tryMirrorDispatch(
                        mirrorEngine, mergedJson, jsonStdout, cancellationToken, sendCommandAsync);
                    await HandleDriftAsync(drift, sendCommandAsync, jsonStdout, cancellationToken);
                }
            }

            foreach ((ServerEvent.InteractionReplayQueued _, string rawLine) in pendingReplayInteractions)
            {
                eventCount++;
                if (afterLine != null && await afterLine(rawLine, eventCount))
                {
                    pendingReplayInteractions.Clear();
                    return true;
                }
            }

            pendingReplayInteractions.Clear();
            return false;
        }

        // Flush accumulated deltas as one coalesced dispatch.
        async Task<bool> FlushAndCheckAsync(string triggeringLine)
        {
            if (await FlushWorldTimeAsync())
            {
                return true;
            }

            if (await FlushInteractionsAsync())
            {
                return true;
            }

            if (await FlushReplayInteractionsAsync())
            {
                return true;
            }

            if (pendingDeltas.Count == 0) return false;

            if (pendingDeltas.Count == 1)
            {
                // Only one delta: dispatch directly (no merge needed).
                bool d = await tryMirrorDispatch(mirrorEngine, pendingDeltas[0].rawLine, jsonStdout, cancellationToken, sendCommandAsync);
                await HandleDriftAsync(d, sendCommandAsync, jsonStdout, cancellationToken);
            }
            else
            {
                // Check per-raw-delta tick-contiguity for drift (LP-4: on raw, not merged endpoint).
                bool driftGap = false;
                foreach ((SimulationSnapshot rawSnap, string _) in pendingDeltas)
                {
                    bool gap = _lastContiguousTick >= 0 && rawSnap.Tick > _lastContiguousTick + 1;
                    if (gap) driftGap = true;
                    _lastContiguousTick = rawSnap.Tick;
                }

                // Merge and dispatch the coalesced snapshot.
                List<SimulationSnapshot> snapshots = new(pendingDeltas.Count);
                foreach ((SimulationSnapshot s, string _) in pendingDeltas) snapshots.Add(s);
                SimulationSnapshot merged = Merge(snapshots);

                // Serialize the merged snapshot as a SnapshotDelta JSON line so TryMirrorDispatchAsync
                // handles ownership/filter/echo/ACK exactly as it does for a real wire line (LP-8).
                string mergedJson = JsonSerializer.Serialize(
                    (ServerEvent)new ServerEvent.SnapshotDelta(merged),
                    PublicProtocolCodec.JsonOptions);

                bool drift = await tryMirrorDispatch(mirrorEngine, mergedJson, jsonStdout, cancellationToken, sendCommandAsync);
                CoalesceLog(jsonStdout, pendingDeltas.Count, merged.Sims.Count);
                // LP-4: trip only when an internal batch tick-gap AND a position jump both fire —
                // a coalesced contiguous batch with a large merged move is NOT drift.
                if (driftGap && drift)
                    await HandleDriftAsync(true, sendCommandAsync, jsonStdout, cancellationToken);
            }

            // Call afterLine for each raw line in the flushed batch so exit conditions fire.
            foreach ((SimulationSnapshot _, string rawLine) in pendingDeltas)
            {
                eventCount++;
                if (afterLine != null && await afterLine(rawLine, eventCount))
                {
                    pendingDeltas.Clear();
                    pendingSpeed = null;
                    return true;
                }
            }

            pendingDeltas.Clear();
            pendingSpeed = null;
            return false;
        }

        foreach (string item in batch)
        {
            if (IsPostedWork(item))
            {
                // Like any other event, what came before it goes first (LP-3).
                if (await FlushAnimationsAsync() || await FlushAndCheckAsync(item))
                {
                    return (true, eventCount);
                }

                await RunPostedWorkAsync(jsonStdout, cancellationToken);
                continue;
            }

            // Classify: is this a coalesable snapshot delta?
            ServerEvent? decoded = null;
            try { decoded = PublicProtocolCodec.DecodeEvent(item); }
            catch { /* ignore decode errors -- fall through to direct dispatch */ }

            if (decoded is ServerEvent.SimAnimation animation)
            {
                // The first of a run: what came before goes first (as for any other event).
                if (pendingAnimations.Count == 0 && await FlushAndCheckAsync(item))
                {
                    return (true, eventCount);
                }

                pendingAnimations.Add((animation.Lines, item));
                continue;
            }

            if (await FlushAnimationsAsync())
            {
                return (true, eventCount);
            }

            if (decoded is ServerEvent.SnapshotDelta sd)
            {
                if (await FlushInteractionsAsync())
                {
                    return (true, eventCount);
                }

                if (await FlushReplayInteractionsAsync())
                {
                    return (true, eventCount);
                }

                if (sd.ForceFull)
                {
                    // ForceFull: discard accumulated backlog positions, mark server resync received.
                    pendingDeltas.Clear();
                    pendingSpeed = null;
                    _serverForceFullInFlight = false; // server resync delivered

                    // Dispatch the ForceFull directly (full snapshot resets client state).
                    bool d = await tryMirrorDispatch(mirrorEngine, item, jsonStdout, cancellationToken, sendCommandAsync);
                    // Reset contiguous-tick baseline to this tick.
                    _lastContiguousTick = sd.Snapshot.Tick;
                    eventCount++;
                    if (afterLine != null && await afterLine(item, eventCount)) return (true, eventCount);
                    continue;
                }

                // Speed-change mid-batch (LP-3): flush current group before starting new one.
                if (pendingSpeed.HasValue && sd.Snapshot.Speed != pendingSpeed.Value)
                {
                    if (await FlushAndCheckAsync(item)) return (true, eventCount);
                }

                pendingDeltas.Add((sd.Snapshot, item));
                pendingSpeed = sd.Snapshot.Speed;
                // Update contiguous tick tracking per raw delta (LP-4).
                if (_lastContiguousTick < 0) _lastContiguousTick = sd.Snapshot.Tick;
            }
            else if (decoded is ServerEvent.InteractionQueued iq)
            {
                if (await FlushReplayInteractionsAsync())
                {
                    return (true, eventCount);
                }

                pendingInteractions.Add((iq, item));
            }
            else if (decoded is ServerEvent.InteractionReplayQueued irq)
            {
                if (await FlushInteractionsAsync())
                {
                    return (true, eventCount);
                }

                pendingReplayInteractions.Add((irq, item));
            }
            else if (decoded is ServerEvent.WorldTimeChanged)
            {
                pendingWorldTimeLine = item;
            }
            else
            {
                // Non-delta event: flush accumulated positions first, then dispatch in order (LP-3).
                if (await FlushAndCheckAsync(item)) return (true, eventCount);

                bool drift = await tryMirrorDispatch(mirrorEngine, item, jsonStdout, cancellationToken, sendCommandAsync);
                await HandleDriftAsync(drift, sendCommandAsync, jsonStdout, cancellationToken);
                eventCount++;
                if (afterLine != null && await afterLine(item, eventCount)) return (true, eventCount);
            }
        }

        if (await FlushAnimationsAsync())
        {
            return (true, eventCount);
        }

        // Flush any remaining accumulated deltas after exhausting the batch.
        bool finalBreak = await FlushAndCheckAsync(string.Empty);
        return (finalBreak, eventCount);
    }

    private async Task HandleDriftAsync(
        bool driftDetected,
        Func<ClientCommand, Task> sendCommandAsync,
        bool jsonStdout,
        CancellationToken cancellationToken)
    {
        if (!driftDetected) return;

        // LP-6: if server ForceFull is in flight, suppress client request (server resync covers it).
        if (_serverForceFullInFlight) return;

        // Debounce: at most one RequestSnapshot per cooldown window.
        DateTime now = DateTime.UtcNow;
        if (now - _lastRequestSnapshotAt < _config.ResyncCooldown) return;
        _lastRequestSnapshotAt = now;

        try
        {
            Guid snapId = Guid.NewGuid();
            await sendCommandAsync(new ClientCommand.RequestSnapshot(snapId, _config.SelfPlayerId, null));
            string payload = "{\"debounced\":true}";
            if (jsonStdout)
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    kind = "drift_resync_requested",
                    ts = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    payload = new { debounced = true }
                }));
            else
                Console.WriteLine($"[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z] drift_resync_requested: {payload}");
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort -- never abort the drain loop for a failed resync request */ }
    }

    private static void PostedWorkFailedLog(bool jsonStdout, string error)
    {
        if (jsonStdout)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "posted_work_failed",
                ts = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                payload = new { error }
            }));
        }
        else
        {
            Console.WriteLine($"[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z] posted_work_failed: {JsonSerializer.Serialize(new { error })}");
        }
    }

    private static void InteractionCoalesceLog(bool jsonStdout, int folded, int applied)
    {
        if (jsonStdout)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "interaction_coalesce",
                ts = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                payload = new { folded, applied }
            }));
        }
        else
        {
            Console.WriteLine($"[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z] interaction_coalesce: {{\"folded\":{folded},\"applied\":{applied}}}");
        }
    }

    private static void InteractionReplayCoalesceLog(bool jsonStdout, int folded, int applied)
    {
        if (jsonStdout)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "interaction_replay_coalesce",
                ts = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                payload = new { folded, applied }
            }));
        }
        else
        {
            Console.WriteLine($"[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z] interaction_replay_coalesce: {{\"folded\":{folded},\"applied\":{applied}}}");
        }
    }

    private static void CoalesceLog(bool jsonStdout, int folded, int applied)
    {
        if (jsonStdout)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "coalesce",
                ts = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                payload = new { folded, applied }
            }));
        }
        else
        {
            Console.WriteLine($"[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z] coalesce: {{\"folded\":{folded},\"applied\":{applied}}}");
        }
    }
}

/// <summary>P5.6: configuration for <see cref="MirrorCoordinator"/>.</summary>
public sealed record CoordinatorConfig(
    int SelfPlayerId,
    int MaxFoldPerCycle,
    TimeSpan ResyncCooldown,
    TimeSpan? InteractionBatchReadAhead = null);
