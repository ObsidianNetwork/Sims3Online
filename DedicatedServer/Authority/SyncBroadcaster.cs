using SimsOnline.DedicatedServer.Network;
using SimsOnline.DedicatedServer.Protocol;
using SimsOnline.DedicatedServer.Observability;

namespace SimsOnline.DedicatedServer.Authority;

/// <summary>
/// P5.3: server-initiated steady-state correction stream. Periodically requests a
/// bridge snapshot delta since the last broadcast tick and pushes <see cref="ServerEvent.SnapshotDelta"/>
/// to all joined public clients. Join-time full baselines remain reply-only on
/// <see cref="ClientCommand.JoinSession"/>; this covers continuous desync prevention.
///
/// P5.6: adds adaptive cadence via <see cref="ResyncPolicy"/>. The base interval
/// is immutable; <c>_effectiveIntervalMs</c> is updated each tick by the per-player policy
/// decisions (min of all players' NextIntervalMs —) and consumed at the next
/// <see cref="Task.Delay"/>. A per-player ForceFull sets that player's resync-in-flight latch;
/// the latch is cleared by <see cref="AuthoritativeSession"/> in its AckSnapshot handler
/// BEFORE RecordSnapshotAck, so a stale-tick backlog cannot clear it prematurely (LP-5).
/// </summary>
public sealed class SyncBroadcaster : IAsyncDisposable
{
    private readonly AuthoritativeSession _session;
    private readonly ClientListenerServer _listener;
    // P5.6: base interval is immutable; effective interval is mutable (policy output).
    private readonly double _baseIntervalMs;
    private double _effectiveIntervalMs;
    private readonly ResyncThresholds _thresholds;
    private readonly CancellationToken _shutdownToken;
    private readonly CancellationTokenSource _cts;
    private readonly Task _loop;
    private readonly Task? _presentationLoop;
    private long _presentationLines;
    private long _lastBroadcastTick = -1;
    // P5.6 / per-player resync-in-flight latch (playerId -> ForceFull tick).
    private readonly Dictionary<int, long> _playerResyncFromTick = new();
    private readonly object _resyncLock = new();
    private int _resyncEpoch;

    /// <summary>P5.6 / true when any player has a ForceFull awaiting ack.</summary>
    public bool IsResyncInFlight
    {
        get
        {
            lock (_resyncLock)
            {
                return _playerResyncFromTick.Count > 0;
            }
        }
    }

    /// <summary>P5.6 / per-player resync latch for AckSnapshot clearing (LP-5).</summary>
    public bool IsResyncInFlightForPlayer(int playerId)
    {
        lock (_resyncLock)
        {
            return _playerResyncFromTick.ContainsKey(playerId);
        }
    }

    /// <summary>P5.6 / the tick used as ResyncFromTick in the last ForceFull for this player.</summary>
    public long GetLastResyncFromTickForPlayer(int playerId)
    {
        lock (_resyncLock)
        {
            return _playerResyncFromTick.TryGetValue(playerId, out long tick) ? tick : -1L;
        }
    }

    /// <summary>P5.6 / clears one player's resync-in-flight latch.</summary>
    public void ClearResyncLatchForPlayer(int playerId)
    {
        lock (_resyncLock)
        {
            _playerResyncFromTick.Remove(playerId);
        }
    }

    internal void SetResyncLatchForPlayer(int playerId, long tick)
    {
        lock (_resyncLock)
        {
            _playerResyncFromTick[playerId] = tick;
        }
    }

    /// <summary>min NextIntervalMs across per-player decisions (fast client keeps base cadence).</summary>
    public static double ComputeEffectiveIntervalMs(IReadOnlyList<ResyncDecision> decisions, double baseIntervalMs)
    {
        double min = baseIntervalMs;
        for (int i = 0; i < decisions.Count; i++)
        {
            min = Math.Min(min, decisions[i].NextIntervalMs);
        }

        return min;
    }

    public static SyncBroadcaster? TryStart(
        SessionConfig config,
        AuthoritativeSession session,
        ClientListenerServer listener,
        CancellationToken shutdownToken)
    {
        if (!Runs(config))
        {
            return null;
        }

        return new SyncBroadcaster(session, listener, config.SyncBroadcastInterval!.Value, config.ResyncThresholds, shutdownToken);
    }

    /// <summary>whether <see cref="TryStart"/> starts a broadcaster for this config. The presentation stream
    /// runs only inside one, so a join may say the server streams only when this holds.</summary>
    public static bool Runs(SessionConfig config) =>
        config.SyncBroadcastInterval is { } interval && interval > TimeSpan.Zero;

    private SyncBroadcaster(
        AuthoritativeSession session,
        ClientListenerServer listener,
        TimeSpan baseInterval,
        ResyncThresholds thresholds,
        CancellationToken shutdownToken)
    {
        _session = session;
        _listener = listener;
        _baseIntervalMs = baseInterval.TotalMilliseconds;
        _effectiveIntervalMs = _baseIntervalMs;
        _thresholds = thresholds;
        _shutdownToken = shutdownToken;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        _loop = Task.Run(RunLoopAsync);
        _presentationLoop = session.PresentationStreamEnabled ? Task.Run(RunPresentationLoopAsync) : null;
    }

    /// <summary>
    /// pure audience gate. A broadcast tick is worth running only when the session still
    /// has a joined player AND the listener still has at least one live connection to receive it.
    /// After a client controller dies, <see cref="ClientListenerServer"/> prunes the broken
    /// connection on the next write (so <c>liveConnectionCount</c> drops to 0) before the read-loop
    /// finally has necessarily released the player from the joined set — for a half-open connection
    /// the read loop can stay wedged. Without this gate the broadcaster keeps fetching a bridge
    /// snapshot (DIAG round-trip) and encoding a full-volume snapshot_delta every tick for a player
    /// nobody is receiving — the "lag frozen, flood continued" symptom. This is a quiesce only, NOT
    /// a reconnect-grace window (that is P5.7).
    /// </summary>
    public static bool ShouldBroadcast(bool hasJoinedPlayers, int liveConnectionCount) =>
        hasJoinedPlayers && liveConnectionCount > 0;

    /// <summary>
    /// Pure policy gate for unit tests and the broadcast loop.
    /// </summary>
    public static bool ShouldPublishDelta(SimulationSnapshot snapshot, long lastBroadcastTick, out long nextWatermark)
    {
        nextWatermark = snapshot.Tick;
        if (lastBroadcastTick < 0)
        {
            return false;
        }

        return snapshot.Sims.Count > 0 || snapshot.Objects.Count > 0;
    }

    /// <summary>P5.4: broadcast tick minus slowest client ack (non-negative when caught up).</summary>
    public static long ComputeAckLag(long broadcastTick, long minPlayerAckTick) =>
        broadcastTick - minPlayerAckTick;

    public static bool TryParseDiagTick(string report, out long tick)
    {
        tick = 0;
        if (string.IsNullOrEmpty(report))
        {
            return false;
        }

        string[] parts = report.Split(';');
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (!part.StartsWith("tick=", StringComparison.Ordinal))
            {
                continue;
            }

            return long.TryParse(part.AsSpan(5), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out tick);
        }

        return false;
    }

    public static bool TryParseDiagInWorld(string report)
    {
        if (string.IsNullOrEmpty(report))
        {
            return false;
        }

        string[] parts = report.Split(';');
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Equals("inWorld=True", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task RunLoopAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                // P5.6: read _effectiveIntervalMs (set by policy last tick) at Delay time.
                int delayMs = (int)Math.Max(100, _effectiveIntervalMs);
                await Task.Delay(delayMs, _cts.Token);
                try
                {
                    await BroadcastTickAsync(_cts.Token);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[sync] tick failed: {ex.Message}");
                    Console.Out.Flush();
                }
            }
        }
        catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested || _shutdownToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    // presentation stream: its own cadence (PresentationStream.PumpInterval), because animation
    // latency matters more than the snapshot tick, which backs off under load.
    private static readonly bool sTraceTransportLatency =
        Environment.GetEnvironmentVariable("SIMSONLINE_TRANSPORT_TRACE") == "1";

    private async Task RunPresentationLoopAsync()
    {
        Console.WriteLine($"[presentation] poll_ms={Mirroring.PresentationStream.PumpInterval.TotalMilliseconds:F0}");
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                // Server ring path: wake as soon as the mod pushes a page; otherwise poll every PumpInterval.
                await _session.WaitForPresentationWorkAsync(Mirroring.PresentationStream.PumpInterval, _cts.Token);
                if (!ShouldBroadcast(_session.HasJoinedPlayers, _listener.ConnectionCount))
                {
                    _session.DiscardPresentationPages();
                    continue;
                }

                try
                {
                    long started = sTraceTransportLatency ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                    int sent = await _session.PublishPresentationAsync(_listener.EnqueueEventBatchAsync, _cts.Token,
                        _listener.EnqueueEventBatchToPlayerAsync, _listener.EnqueueEventBatchExceptAsync);
                    if (sTraceTransportLatency)
                    {
                        double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        Console.WriteLine($"[transport-latency] stage=presentation_pump elapsed_ms={elapsedMs:F3} lines={sent} targets={_session.PresentationTargetCount}");
                    }
                    long before = _presentationLines;
                    _presentationLines += sent;
                    if (_presentationLines / 1000 > before / 1000)
                    {
                        Console.WriteLine($"[presentation] sim_animation lines={_presentationLines} targets={_session.PresentationTargetCount}");
                        Console.Out.Flush();
                    }
                }
                catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[presentation] tick failed: {ex.Message}");
                    Console.Out.Flush();
                }
            }
        }
        catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested || _shutdownToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task BroadcastTickAsync(CancellationToken cancellationToken)
    {
        // W1: server simulation scope follows the players' lots. Before the quiesce check so the
        // scope also clears once nobody is joined; the session only sends on change.
        try
        {
            string? scopeLog = await _session.SyncSimulationScopeAsync(DateTimeOffset.UtcNow, cancellationToken);
            if (scopeLog != null)
            {
                Console.WriteLine($"[scope] {scopeLog}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[scope] sync failed: {ex.Message}");
        }

        // quiesce. Skip the whole tick (no bridge snapshot fetch, no encode, no
        // broadcast) once there is no live audience — either no joined player or no live
        // connection. Stops the full-volume flood + bridge-hammering to a dropped/half-open
        // client whose player record has not yet been released by the read-loop finally.
        if (!ShouldBroadcast(_session.HasJoinedPlayers, _listener.ConnectionCount))
        {
            return;
        }

        try
        {
            await _session.MaybeBroadcastWorldTimeReconcileAsync(_listener, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[sync] world_time reconcile failed: {ex.Message}");
            Console.Out.Flush();
        }

        if (_lastBroadcastTick < 0)
        {
            long tick = _session.SyncWatermarkTick;
            if (tick < 0)
            {
                // Join may still be fetching its baseline snapshot (player is registered but
                // watermark is not set yet). Avoid bridge DIAG during that window.
                return;
            }

            _lastBroadcastTick = tick;
            return;
        }

        // per-player lag + policy BEFORE fetching snapshot.
        IReadOnlyList<int> connectedPlayers = _listener.GetConnectedPlayerIds();
        if (connectedPlayers.Count == 0)
        {
            return;
        }

        Dictionary<int, ResyncDecision> decisionByPlayer = new(connectedPlayers.Count);
        List<ResyncDecision> decisionsForInterval = new(connectedPlayers.Count);
        foreach (int playerId in connectedPlayers)
        {
            bool ackValid = _session.TryGetPlayerAckTick(playerId, out long ackTick);
            bool resyncInFlight = IsResyncInFlightForPlayer(playerId);
            ResyncDecision decision = ResyncPolicy.Decide(
                broadcastTick: _lastBroadcastTick,
                minAckTick: ackValid ? ackTick : _lastBroadcastTick,
                minAckValid: ackValid,
                resyncInFlight: resyncInFlight,
                baseIntervalMs: _baseIntervalMs,
                thresholds: _thresholds);
            decisionByPlayer[playerId] = decision;
            decisionsForInterval.Add(decision);
        }

        _effectiveIntervalMs = ComputeEffectiveIntervalMs(decisionsForInterval, _baseIntervalMs);

        List<int> forceFullPlayers = new();
        List<int> deltaPlayers = new();
        bool anyAction = false;

        foreach (KeyValuePair<int, ResyncDecision> entry in decisionByPlayer)
        {
            int playerId = entry.Key;
            ResyncDecision decision = entry.Value;
            switch (decision.Action)
            {
                case ResyncAction.Suppress:
                {
                    long lagVal = _session.TryGetPlayerAckTick(playerId, out long ack)
                        ? ComputeAckLag(_lastBroadcastTick, ack)
                        : -1L;
                    Console.WriteLine(
                        $"[resync] policy_triggered player={playerId} action=Suppress lag={lagVal} M={_thresholds.HardTicks} reason=resync_in_flight");
                    break;
                }

                case ResyncAction.ForceFull:
                    anyAction = true;
                    forceFullPlayers.Add(playerId);
                    break;

                case ResyncAction.PublishDelta:
                case ResyncAction.SlowAndPublish:
                    anyAction = true;
                    deltaPlayers.Add(playerId);
                    break;
            }
        }

        if (!anyAction)
        {
            Console.Out.Flush();
            return;
        }

        SimulationSnapshot? fullSnapshot = null;
        SimulationSnapshot? deltaSnapshot = null;
        long nextWatermark = _lastBroadcastTick;

        if (forceFullPlayers.Count > 0)
        {
            try
            {
                fullSnapshot = await _session.GetSnapshotSinceAsync(-1L, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[sync] full snapshot request failed: {ex.Message}");
                Console.Out.Flush();
                return;
            }
        }

        if (deltaPlayers.Count > 0)
        {
            try
            {
                deltaSnapshot = await _session.GetSnapshotSinceAsync(_lastBroadcastTick, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[sync] snapshot request failed: {ex.Message}");
                Console.Out.Flush();
                if (forceFullPlayers.Count == 0)
                {
                    return;
                }

                deltaPlayers.Clear();
                deltaSnapshot = null;
            }

            if (deltaSnapshot != null
                && !ShouldPublishDelta(deltaSnapshot, _lastBroadcastTick, out nextWatermark))
            {
                deltaPlayers.Clear();
                if (forceFullPlayers.Count == 0)
                {
                    _lastBroadcastTick = nextWatermark;
                    return;
                }

                deltaSnapshot = null;
            }
        }

        if (forceFullPlayers.Count == 0 && deltaPlayers.Count == 0)
        {
            return;
        }

        long broadcastedTick = Math.Max(fullSnapshot?.Tick ?? 0L, deltaSnapshot?.Tick ?? 0L);
        if (broadcastedTick <= 0)
        {
            return;
        }

        _lastBroadcastTick = broadcastedTick;
        _session.UpdateInterestLotIndex(fullSnapshot ?? deltaSnapshot!);

        // slice 6: the reconcile-aware refresh merges any pending lot export BEFORE the
        // mode-change event is emitted (trigger 2 ordering), then stale/missing exports for
        // client-native players trigger debounced LotExportRequested pushes (trigger 1 freshness).
        foreach (ServerEvent evt in await _session.RefreshLotLocalAuthorityModesWithReconcileAsync(DateTimeOffset.UtcNow, cancellationToken))
        {
            if (evt is ServerEvent.LotLocalAuthorityModeChanged modeChanged)
            {
                await _listener.SendEventToPlayerAsync(modeChanged.PlayerId, modeChanged, cancellationToken);
            }
            else if (evt is ServerEvent.LotReconcileAcknowledged ack)
            {
                await _listener.SendEventToPlayerAsync(ack.PlayerId, ack, cancellationToken);
            }
        }

        foreach (ServerEvent.LotExportRequested exportRequested in await _session.CollectLotExportRequestsAsync(DateTimeOffset.UtcNow, cancellationToken))
        {
            await _listener.SendEventToPlayerAsync(exportRequested.PlayerId, exportRequested, cancellationToken);
        }

        foreach (int playerId in forceFullPlayers)
        {
            SetResyncLatchForPlayer(playerId, broadcastedTick);
            _resyncEpoch++;
            await _listener.SendSnapshotDeltaToPlayerAsync(playerId, fullSnapshot!, forceFull: true, cancellationToken);
            long lagVal = _session.TryGetPlayerAckTick(playerId, out long ack)
                ? broadcastedTick - ack
                : -1L;
            Console.WriteLine(
                $"[resync] policy_triggered player={playerId} action=ForceFull lag={lagVal} M={_thresholds.HardTicks} reason=hard_threshold epoch={_resyncEpoch} tick={broadcastedTick}");
            if (lagVal > _thresholds.HardTicks)
            {
                SimsOnlineTelemetry.Current?.EmitSnapshotCadenceDrift(
                    broadcastedTick, ack, lagVal);
            }
        }

        foreach (int playerId in deltaPlayers)
        {
            ResyncDecision decision = decisionByPlayer[playerId];
            await _listener.SendSnapshotDeltaToPlayerAsync(playerId, deltaSnapshot!, forceFull: false, cancellationToken);
            if (decision.Action == ResyncAction.SlowAndPublish)
            {
                bool ackValid = _session.TryGetPlayerAckTick(playerId, out long ack);
                long lagVal = ackValid ? ComputeAckLag(broadcastedTick, ack) : -1L;
                Console.WriteLine(
                    $"[sync] snapshot_delta player={playerId} tick={broadcastedTick} sims={deltaSnapshot!.Sims.Count} objects={deltaSnapshot.Objects.Count} ack={FormatAckTick(ackValid, ack)} lag={lagVal} [resync] action=SlowAndPublish interval={decision.NextIntervalMs:F0}ms");
            }
            else
            {
                bool ackValid = _session.TryGetPlayerAckTick(playerId, out long ack);
                long lagVal = ackValid ? ComputeAckLag(broadcastedTick, ack) : -1L;
                Console.WriteLine(
                    $"[sync] snapshot_delta player={playerId} tick={broadcastedTick} sims={deltaSnapshot!.Sims.Count} objects={deltaSnapshot.Objects.Count} ack={FormatAckTick(ackValid, ack)} lag={lagVal}");
            }
        }

        // periodically probe motive/funds/skills and broadcast change events.
        try
        {
            await _session.PublishMotiveFundsSkillsAsync(_listener.EnqueueEventBatchAsync, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[sync] motive probe failed: {ex.Message}");
            Console.Out.Flush();
        }

        // W2 part B: motives + buffs of unclaimed Sims on player lots (clients freeze their heartbeat).
        try
        {
            int sent = await _session.PublishVisibleSimStateAsync(_listener.EnqueueEventBatchAsync, cancellationToken);
            if (sent > 0)
            {
                Console.WriteLine($"[state] sim_state_changed sent={sent}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[sync] sim state mirror failed: {ex.Message}");
            Console.Out.Flush();
        }

        // step 5: claimed Sims' job, household funds and skills.
        try
        {
            int sent = await _session.PublishSimDetailsAsync(_listener.EnqueueEventBatchAsync, cancellationToken);
            if (sent > 0)
            {
                Console.WriteLine($"[details] sim_details_changed={sent}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[sync] sim details failed: {ex.Message}");
            Console.Out.Flush();
        }

        // W2 Sim identity step 3: Sims entering/leaving the server's world -> SimSpawned / SimDespawned.
        try
        {
            (int spawned, int despawned) = await _session.PublishSimPresenceAsync(_listener.EnqueueEventBatchAsync, cancellationToken);
            if (spawned + despawned > 0)
            {
                Console.WriteLine($"[presence] sim_spawned={spawned} sim_despawned={despawned} announced={_session.AnnouncedSimCount}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[sync] sim presence failed: {ex.Message}");
            Console.Out.Flush();
        }

        Console.Out.Flush();
    }

    private static string FormatAckTick(bool ackValid, long ackTick) => ackValid ? ackTick.ToString() : "-";

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _loop.ConfigureAwait(false);
            if (_presentationLoop != null)
            {
                await _presentationLoop.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        _cts.Dispose();
    }
}
