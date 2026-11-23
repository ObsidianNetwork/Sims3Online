using System.Globalization;
using System.IO;
using SimsOnline.DedicatedServer.Mirroring;
using SimsOnline.DedicatedServer.Network;
using SimsOnline.DedicatedServer.Protocol;
using SimsOnline.DedicatedServer.Observability;

namespace SimsOnline.DedicatedServer.Authority;

public sealed class AuthoritativeSession
{
    private readonly IAuthoritativeSimulationHost _host;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly Dictionary<int, PlayerSession> _players = new();
    private readonly object _connectionLeaseLock = new();
    private readonly Dictionary<int, Guid> _activeConnectionLeases = new();

    // Connection leases that ever carried a successful JoinSession (the per-connection
    // "hasJoined" flag). Only these leases are subject to the supersede guard in
    // HandleCommandAsync; leases that never joined are trusted admin connections
    // (simctl.py / ut.py deliberately never send join_session so their disconnects
    // cannot release the real client's claims). Only touched under _commandGate.
    private readonly HashSet<Guid> _joinedConnectionLeases = new();

    // P3.1: per-Sim ownership map - simObjectId -> owning playerId. While a Sim is in this
    // dictionary, only the owning player's QueueInteraction requests are honored (P3.2 enforces
    // that gate). Claim is released on explicit ReleaseSim, on LeaveSession for the owner, or
    // when the owning client disconnects (the listener synthesizes a LeaveSession on cleanup).
    private readonly Dictionary<ulong, int> _claims = new();

    // the player behind each user pick the host accepted, by command id, so a pick the server's queue
    // drops later (EVENT|PickFailed) reaches that player only. Bounded: the oldest entries go first. Written
    // under _commandGate, read from the bridge's event thread, hence the lock.
    private const int MaxPickRequesters = 256;
    private readonly Dictionary<Guid, int> _pickRequesters = new();
    private readonly Queue<Guid> _pickRequesterOrder = new();
    private readonly object _pickRequesterLock = new();

    // P4.1.2: per-player role declared via ClientCommand.SetClientRole. Absent
    // entry means the player has not declared a role and lookups fall back to PlayerRole.Server.
    private readonly Dictionary<int, PlayerRole> _playerRoles = new();

    // last speed the session accepted (start-speed write included — Program.cs routes
    // it through HandleCommandAsync). GameSpeedChanged is only broadcast on CHANGE, so a late
    // joiner would otherwise inherit TS3's load-time pause and diverge by game-hours within
    // minutes. Null until the first accepted SetGameSpeed.
    private GameSpeed? _currentSpeed;

    /// <summary>The server TS3 changed its own speed (its speed buttons, or its fast-forward while every Sim
    /// sleeps): that is the authoritative speed now, and what a joining player gets.</summary>
    public void AdoptHostSpeed(GameSpeed speed) => _currentSpeed = speed;

    /// <summary>The authoritative game speed, when one is known.</summary>
    public GameSpeed? CurrentSpeed => _currentSpeed;

    // last world time the session accepted. WorldTimeChanged is only broadcast on
    // change, so a late joiner would boot at TS3 load-time clock. Null until first SetWorldTime.
    private WorldTime? _currentWorldTime;

    // last reconcile broadcast (periodic mirror correction). Separate watermark from
    // _currentWorldTime so join replay stays stable while ShouldReconcile gates traffic.
    private WorldTime? _lastReconciledWorldTime;
    private DateTimeOffset _lastWorldTimeReconcileUtc = DateTimeOffset.MinValue;

    // P5.3: tick from the latest join baseline snapshot; seeds SyncBroadcaster without a
    // redundant full bridge snapshot on the first broadcast interval.
    private long _syncWatermarkTick = -1;

    // P5.4: per-player highest snapshot tick the client confirmed through its mirror loop.
    private readonly Dictionary<int, long> _playerAckTicks = new();

    // P5.6: optional reference to the running broadcaster; used in AckSnapshot to clear the
    // resync-in-flight latch BEFORE RecordSnapshotAck (LP-5: RecordSnapshotAck early-returns
    // on stale ticks so a backlogged client draining old deltas would never reach that site).
    private SyncBroadcaster? _syncBroadcaster;

    // lot-tiered mirror scope (claimed-sim lots = Tier 1). Disabled via --no-interest-scope.
    private bool _interestScopedMirroring = true;
    private SimulationSnapshot? _interestLotIndexSnapshot;
    private readonly Dictionary<int, LotLocalAuthorityHysteresis> _lotLocalAuthorityHysteresis = new();
    // motive/funds/skills probe cycle. Runs every Nth snapshot broadcast to
    // avoid flooding the bridge. Tracks last-known values to detect changes.
    private int _motiveProbeCounter;
    private const int MotiveProbeInterval = 5; // every ~10s at 2s broadcast cadence
    private readonly Dictionary<(ulong, string), float> _lastKnownMotives = new();
    private long _motiveAuthorityGeneration;
    private readonly Dictionary<ulong, int> _lastKnownFunds = new();
    private readonly Dictionary<(ulong, string), int> _lastKnownSkills = new();
    private static readonly string[] TrackedMotives = { "hunger", "energy", "social", "hygiene", "bladder", "fun" };
    private readonly Dictionary<int, LotLocalAuthorityMode> _lotLocalAuthorityStable = new();


    // slice 6: per-player lot export bookkeeping for the three reconcile triggers
    // (checkpoint gate, authority-mode exit, disconnect/leave). Serialised by _commandGate.
    private readonly LotReconcileTracker _lotReconcile = new();
    private LotReconcileSavePolicy _saveReconcilePolicy = LotReconcileSavePolicy.RejectWhenUnreconciled;

    // adversarial durability testing flag. While .omo/run/dev204-suspend-checkpoint.flag
    // exists, CollectLotExportRequestsAsync returns no requests, suspending the checkpoint_freshness
    // cadence. This lets the safety-net triggers (mode-exit, disconnect) fire with a PENDING unmerged
    // export, proving they perform real work (not just the no-op path validated).
    // The file is NOT consumed -- presence-sensed, remove to resume.
    private static readonly string Dev204SuspendFlagPath = Path.Combine(".omo", "run", "dev204-suspend-checkpoint.flag");
    private static bool IsCheckpointSuspendedByFlag()
    {
        try { return File.Exists(Dev204SuspendFlagPath); }
        catch { return false; }
    }

    // slice 5: per-(player, field) server-side last-mutated tick for the seven
    // lot-resident ExportContent entry classes (design §3.2). Used by LotReconcileCoordinator
    // to classify contested fields. Serialised by _commandGate alongside _lotReconcile.
    private readonly LotReconcileFieldTickMap _serverFieldTicks = new();

    // slice 6 + PR#17 review fix: players whose hysteresis-stable mode wants to leave a
    // client-native mode for full_puppet_fallback. The mode commit + event is DEFERRED until the
    // reconcile drain merges the pending export. Players whose merge succeeded are removed by the
    // drain; players with no pending export stay in the set so the exit stays deferred and
    // CollectLotExportRequestsAsync still requests an export (design ordering: reconcile completes
    // before the event that re-enables owned APPLY_* / full puppet).
    private readonly HashSet<int> _pendingModeExitReconciles = new();

    /// <summary> slice 6: checkpoint policy when a client-native player is unreconciled.</summary>
    public void ConfigureSaveReconcilePolicy(LotReconcileSavePolicy policy) => _saveReconcilePolicy = policy;

    public void ConfigureInterestScopedMirroring(bool enabled) => _interestScopedMirroring = enabled;

    // docs/decision-one-simulator.md: the server is the only simulator. With client-native
    // modes off, every player stays in FullPuppetFallback: no lot is handed to a client to simulate,
    // so the server keeps mirroring every Sim on it. Default on until input forwarding lands.
    private bool _clientNativeModes = true;

    public void ConfigureClientNativeModes(bool enabled) => _clientNativeModes = enabled;

    /// <summary>refresh sim->lot index used to gate InteractionQueued broadcasts.</summary>
    public void UpdateInterestLotIndex(SimulationSnapshot snapshot)
    {
        if (_interestLotIndexSnapshot == null)
        {
            _interestLotIndexSnapshot = snapshot;
            return;
        }

        // sparse deltas omit sims — merge so lot-local detection keeps claimed Sim LotId.
        Dictionary<ulong, SimSnapshot> merged = new(_interestLotIndexSnapshot.Sims.Count + snapshot.Sims.Count);
        foreach (SimSnapshot sim in _interestLotIndexSnapshot.Sims)
        {
            merged[sim.ObjectId] = sim;
        }

        foreach (SimSnapshot sim in snapshot.Sims)
        {
            merged[sim.ObjectId] = sim;
        }

        _interestLotIndexSnapshot = _interestLotIndexSnapshot with
        {
            Tick = Math.Max(_interestLotIndexSnapshot.Tick, snapshot.Tick),
            Sims = merged.Values.ToList(),
        };
    }

    // W1 (docs/server-authoritative-plan.md): last simulation scope the server TS3 accepted, and the
    // last one it rejected (retry backoff). Only touched by the SyncBroadcaster loop.
    private SimulationScope? _publishedSimulationScope;
    private SimulationScope? _failedSimulationScope;
    private DateTimeOffset _failedSimulationScopeUtc;

    /// <summary>The scope the server TS3 last accepted (null until the first publish).</summary>
    public SimulationScope? PublishedSimulationScope => _publishedSimulationScope;

    /// <summary>
    /// W1: keep the server TS3 simulating every lot a player occupies (and every player-owned Sim)
    /// at full fidelity, whatever its own active lot. Computes the scope from the claims and the
    /// interest lot index, and sends it to the host only when it changed. Called by the
    /// SyncBroadcaster every tick, including when no player is joined, so the scope clears after
    /// the last player leaves. Returns a log line when it sent (or failed to send), else null.
    /// </summary>
    public async Task<string?> SyncSimulationScopeAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        SimulationScope scope;
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            scope = SimulationScopePolicy.Compute(
                _claims,
                _interestLotIndexSnapshot?.Sims ?? (IReadOnlyList<SimSnapshot>)Array.Empty<SimSnapshot>());
        }
        finally
        {
            _commandGate.Release();
        }

        if (!SimulationScopePolicy.ShouldPublish(
                scope,
                _publishedSimulationScope,
                _failedSimulationScope,
                _failedSimulationScopeUtc,
                now,
                SimulationScopePolicy.RetryAfterFailure))
        {
            return null;
        }

        try
        {
            string? report = await _host.SetSimulationScopeAsync(scope, cancellationToken);
            _publishedSimulationScope = scope;
            _failedSimulationScope = null;
            return $"published {scope} report={report ?? "(host has no scope channel)"}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _failedSimulationScope = scope;
            _failedSimulationScopeUtc = now;
            return $"failed {scope} error={ex.Message}";
        }
    }

    // step 5: claimed Sims' details (job, funds, skills); last sample sent per Sim. Only touched by the
    // SyncBroadcaster loop.
    private int _simDetailsCounter;
    private readonly Dictionary<ulong, SimDetailsSample> _lastSimDetails = new();

    /// <summary> step 5: probe claimed Sims' details every Nth broadcaster tick (~6 s at 2 s).</summary>
    public const int SimDetailsProbeInterval = 3;

    /// <summary> step 5: resend every Sim's details every Nth probe (~60 s) so late joiners catch up.</summary>
    public const int SimDetailsResendInterval = 10;

    private int _simDetailsResendCounter;

    /// <summary>
    /// step 5: every <see cref="SimDetailsProbeInterval"/> calls, probe the claimed Sims' job, household
    /// funds and skills on the server TS3 and broadcast SimDetailsChanged for each one that changed (all of them
    /// every <see cref="SimDetailsResendInterval"/> probes). Clients' UI and pie menus read these from their own
    /// copy. Returns the number of events sent.
    /// </summary>
    public async Task<int> PublishSimDetailsAsync(
        Func<IReadOnlyList<ServerEvent>, CancellationToken, Task<bool>> enqueueBatchAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enqueueBatchAsync);
        List<ulong> candidates;
        bool resend;
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            if (++_simDetailsCounter < SimDetailsProbeInterval)
            {
                return 0;
            }

            _simDetailsCounter = 0;
            resend = ++_simDetailsResendCounter >= SimDetailsResendInterval;
            if (resend)
            {
                _simDetailsResendCounter = 0;
            }

            candidates = _claims.Keys.OrderBy(id => id).ToList();
        }
        finally
        {
            _commandGate.Release();
        }

        foreach (ulong gone in _lastSimDetails.Keys.Where(id => !candidates.Contains(id)).ToList())
        {
            _lastSimDetails.Remove(gone);
        }

        if (candidates.Count == 0)
        {
            return 0;
        }

        IReadOnlyList<SimDetailsSample> samples = await _host.ProbeSimDetailsAsync(candidates, cancellationToken);
        List<SimDetailsSample> changed = samples
            .Where(sample => resend || !_lastSimDetails.TryGetValue(sample.SimObjectId, out SimDetailsSample? last) || last != sample)
            .ToList();
        if (changed.Count == 0)
        {
            return 0;
        }

        List<ServerEvent> events = changed
            .Select(d => (ServerEvent)new ServerEvent.SimDetailsChanged(d.SimObjectId, d.CareerGuid, d.CareerLevel, d.CareerBranch, d.Funds, d.Skills))
            .ToList();
        if (await enqueueBatchAsync(events, cancellationToken))
        {
            foreach (SimDetailsSample sample in changed)
            {
                _lastSimDetails[sample.SimObjectId] = sample;
            }
        }

        return events.Count;
    }

    // W2 part B: visible-state mirror (motives + buffs) for unclaimed Sims on player lots; last sample
    // each client was sent, per Sim. Only touched by the SyncBroadcaster loop.
    private int _visibleStateCounter;
    private readonly Dictionary<ulong, SimStateSample> _lastVisibleState = new();

    /// <summary>
    /// W2 part B (docs/engine/client-side-effects.md): every VisibleSimStatePolicy.ProbeInterval calls,
    /// probe the motives and buffs of unclaimed Sims on player-occupied lots and broadcast a
    /// SimStateChanged for each one that changed. Clients run the heartbeat only for Sims they own, so
    /// this keeps the others' state the server's. Returns the number of events sent.
    /// </summary>
    public async Task<int> PublishVisibleSimStateAsync(
        Func<IReadOnlyList<ServerEvent>, CancellationToken, Task<bool>> enqueueBatchAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enqueueBatchAsync);
        IReadOnlyList<ulong> candidates;
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            _visibleStateCounter++;
            if (_visibleStateCounter < VisibleSimStatePolicy.ProbeInterval)
            {
                return 0;
            }

            _visibleStateCounter = 0;
            candidates = VisibleSimStatePolicy.Candidates(
                _claims,
                _interestLotIndexSnapshot?.Sims ?? (IReadOnlyList<SimSnapshot>)Array.Empty<SimSnapshot>());
        }
        finally
        {
            _commandGate.Release();
        }

        // A Sim that left the candidate set is resent in full when it comes back.
        HashSet<ulong> candidateSet = new(candidates);
        foreach (ulong gone in _lastVisibleState.Keys.Where(id => !candidateSet.Contains(id)).ToList())
        {
            _lastVisibleState.Remove(gone);
        }

        if (candidates.Count == 0)
        {
            return 0;
        }

        IReadOnlyList<SimStateSample> samples = await _host.ProbeSimStateAsync(candidates, cancellationToken);
        List<ServerEvent> events = new();
        List<SimStateSample> changed = new();
        foreach (SimStateSample sample in samples)
        {
            _lastVisibleState.TryGetValue(sample.SimObjectId, out SimStateSample? last);
            if (VisibleSimStatePolicy.HasChanged(sample, last))
            {
                events.Add(new ServerEvent.SimStateChanged(sample.SimObjectId, sample.Motives, sample.Buffs));
                changed.Add(sample);
            }
        }

        if (events.Count == 0)
        {
            return 0;
        }

        if (await enqueueBatchAsync(events, cancellationToken))
        {
            foreach (SimStateSample sample in changed)
            {
                _lastVisibleState[sample.SimObjectId] = sample;
            }
        }

        return events.Count;
    }

    // W2 Sim identity step 3: presence journal cursor, Sims instantiated after the journal baseline and
    // still in the world, and the ones announced to clients. Only touched by the SyncBroadcaster loop
    // (and under _commandGate where the claims / interest index are read).
    private long _presenceSeq;
    private int _presenceResendCounter;
    private readonly Dictionary<ulong, SimPresenceEntry> _postLoadSims = new();
    private readonly HashSet<ulong> _announcedSims = new();

    /// <summary>Post-load Sims currently announced to clients (SimSpawned).</summary>
    public int AnnouncedSimCount => _announcedSims.Count;

    /// <summary>
    /// W2 Sim identity step 3 (docs/engine/sim-identity-and-npc-creation.md): read the server TS3's
    /// presence journal and broadcast SimSpawned for post-load Sims on player lots (SimPresencePolicy)
    /// and SimDespawned for Sims that left the world and that clients can hold. A Sim that left is also
    /// dropped from the interest lot index (deltas never report removals). Returns (spawned, despawned)
    /// events sent; (0, 0) when the host has no presence channel.
    /// </summary>
    public async Task<(int Spawned, int Despawned)> PublishSimPresenceAsync(
        Func<IReadOnlyList<ServerEvent>, CancellationToken, Task<bool>> enqueueBatchAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enqueueBatchAsync);
        List<SimPresenceEntry> entries = new();
        long seq = _presenceSeq;
        for (int round = 0; round < SimPresencePolicy.MaxProbeRounds; round++)
        {
            SimPresenceReport? report = await _host.ProbeSimPresenceAsync(seq, cancellationToken);
            if (report == null)
            {
                return (0, 0);
            }

            entries.AddRange(report.Entries);
            seq = report.Seq;
            if (!report.More)
            {
                break;
            }
        }

        List<ServerEvent> events = new();
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            HashSet<ulong> gone = new();
            foreach (SimPresenceEntry entry in entries)
            {
                if (entry.Spawned)
                {
                    _postLoadSims[entry.ObjectId] = entry;
                    continue;
                }

                bool wasPostLoad = _postLoadSims.Remove(entry.ObjectId);
                bool wasAnnounced = _announcedSims.Remove(entry.ObjectId);
                gone.Add(entry.ObjectId);
                if (SimPresencePolicy.ShouldAnnounceDespawn(entry.ObjectId, _claims, wasPostLoad, wasAnnounced))
                {
                    events.Add(new ServerEvent.SimDespawned(entry.ObjectId, entry.SimDescriptionId));
                }
            }

            if (gone.Count > 0 && _interestLotIndexSnapshot != null)
            {
                _interestLotIndexSnapshot = _interestLotIndexSnapshot with
                {
                    Sims = _interestLotIndexSnapshot.Sims.Where(s => !gone.Contains(s.ObjectId)).ToList(),
                };
            }

            bool resend = ++_presenceResendCounter >= SimPresencePolicy.ResendInterval;
            if (resend)
            {
                _presenceResendCounter = 0;
            }

            events.AddRange(SimPresencePolicy.Announcements(
                _claims,
                _interestLotIndexSnapshot?.Sims ?? (IReadOnlyList<SimSnapshot>)Array.Empty<SimSnapshot>(),
                _postLoadSims.Values,
                _announcedSims,
                resend));
        }
        finally
        {
            _commandGate.Release();
        }

        _presenceSeq = seq;
        if (events.Count == 0)
        {
            return (0, 0);
        }

        if (!await enqueueBatchAsync(events, cancellationToken))
        {
            // Not delivered: unannounced Sims stay unannounced and are offered again next tick.
            return (0, 0);
        }

        int spawned = 0;
        foreach (ServerEvent e in events)
        {
            if (e is ServerEvent.SimSpawned s)
            {
                _announcedSims.Add(s.SimObjectId);
                spawned++;
            }
        }

        return (spawned, events.Count - spawned);
    }

    // presentation stream (Mirroring.PresentationStream): ring cursor (-1 = not started), the
    // targets streamed last tick, and whether every target needs a history snapshot (a player joined).
    // Only touched by the SyncBroadcaster presentation loop, except _presentationJoiners (locked).
    private long _presentationSeq = -1;
    // Experiment C: the SimLink sidecar shadowing the presentation stream (SIMSONLINE_SIMLINK_SHADOW=1), and the server
    // ring path (SIMSONLINE_SIMLINK_SERVER_RING=1): the mod pushes the stream's pages through the ring instead of
    // answering dumps. While pushing, no dump is sent (the cursor and the per-driver forwarding state are the mod's).
    private SimsOnline.DedicatedServer.HostBridge.SimLinkShadowSession? _simLinkShadow;
    private bool _serverPushOn;
    private string? _pushTargetsSent;
    private long _serverPushFallbacks;
    private long _serverPushRearms;
    // Re-arm after a fallback (or a refused push-on): the next try, the current backoff, when push last went on.
    private DateTime _pushRearmAtUtc = DateTime.MaxValue;
    private TimeSpan _pushRearmDelay = TimeSpan.Zero;
    private DateTime _pushOnSinceUtc;

    /// <summary>No pushed page for this long switches the stream back to dumps (the mod sends one a second at least).</summary>
    internal TimeSpan ServerPushSilence { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How long after a fallback the controller tries push again; doubles (up to <see cref="ServerPushRearmMaxDelay"/>)
    /// while push keeps failing within <see cref="ServerPushHealthy"/> of going on, or keeps being refused.</summary>
    internal TimeSpan ServerPushRearmDelay { get; set; } = TimeSpan.FromSeconds(10);

    internal TimeSpan ServerPushRearmMaxDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Push that delivered pages for this long before falling back counts as healthy: the backoff starts over.</summary>
    internal TimeSpan ServerPushHealthy { get; set; } = TimeSpan.FromSeconds(60);

    public long ServerPushRearms => _serverPushRearms;

    /// <summary>Tests: the SimLink session to use instead of opening the server TS3's sidecar.</summary>
    internal SimsOnline.DedicatedServer.HostBridge.SimLinkShadowSession? SimLinkSessionOverride { get; set; }

    /// <summary>Tests: the server game's on-screen walk to use instead of reading the server TS3 (OnScreenWalk).</summary>
    internal string? OnScreenOverride { get; set; }

    public long ServerPushFallbacks => _serverPushFallbacks;

    /// <summary>Whether the presentation stream arrives as pushed pages (server ring path).</summary>
    public bool ServerPushActive => _serverPushOn;

    // Server ring path pacing. Pages arrive on every simulator update (about 30 a second at Normal, about 280 at
    // Triple), and each broadcast is one SimAnimation per client. SIMSONLINE_PRESENTATION_MIN_INTERVAL_MS (default 0):
    // at most one broadcast per that interval; pages that arrive in between are held and go out together, so the first
    // line after a quiet spell is never held. SIMSONLINE_PRESENTATION_INTERVAL_AB_SECONDS (experiment, default 0):
    // alternates between the interval and none every that many seconds, logging each switch with its UTC epoch.
    private static readonly int sPresentationMinIntervalMs = EnvMilliseconds("SIMSONLINE_PRESENTATION_MIN_INTERVAL_MS");
    private static readonly int sPresentationIntervalAbSeconds = EnvMilliseconds("SIMSONLINE_PRESENTATION_INTERVAL_AB_SECONDS");
    private static readonly DateTime sPacingStartUtc = DateTime.UtcNow;
    private DateTime _lastPresentationBroadcastUtc = DateTime.MinValue;
    private long _pacingPhaseLogged = -1;

    /// <summary>Tests: the minimum interval between broadcasts, instead of the environment's.</summary>
    internal TimeSpan? PresentationMinIntervalOverride { get; set; }

    private static int EnvMilliseconds(string name) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : 0;

    private TimeSpan CurrentPresentationMinInterval()
    {
        if (PresentationMinIntervalOverride is TimeSpan forced)
        {
            return forced;
        }

        TimeSpan interval = TimeSpan.FromMilliseconds(sPresentationMinIntervalMs);
        long phase = 0;
        if (sPresentationIntervalAbSeconds > 0)
        {
            phase = (long)((DateTime.UtcNow - sPacingStartUtc).TotalSeconds / sPresentationIntervalAbSeconds);
            if (phase % 2 == 1)
            {
                interval = TimeSpan.Zero;
            }
        }

        if (phase != _pacingPhaseLogged)
        {
            _pacingPhaseLogged = phase;
            DateTime since = sPacingStartUtc + TimeSpan.FromSeconds(phase * (double)sPresentationIntervalAbSeconds);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[presentation-pacing] min_interval_ms={interval.TotalMilliseconds:F0} phase={phase} since_utc_epoch={(since - DateTime.UnixEpoch).TotalSeconds:F3}"));
        }

        return interval;
    }

    /// <summary>The presentation loop's wait: until a pushed page arrives (server ring path, then held for the minimum
    /// interval since the last broadcast) or the pump interval.</summary>
    public async Task WaitForPresentationWorkAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        if (_serverPushOn && _simLinkShadow != null)
        {
            await _simLinkShadow.WaitForPageAsync(interval, cancellationToken);
            TimeSpan hold = _lastPresentationBroadcastUtc + CurrentPresentationMinInterval() - DateTime.UtcNow;
            if (hold > TimeSpan.Zero)
            {
                await Task.Delay(hold, cancellationToken);
            }

            return;
        }

        await Task.Delay(interval, cancellationToken);
    }

    /// <summary>Server ring path with nobody to send to: consume the pushed pages (keeping the cursor) so a later join
    /// doesn't get minutes-old animation.</summary>
    public void DiscardPresentationPages()
    {
        if (!_serverPushOn || _simLinkShadow == null)
        {
            return;
        }

        while (_simLinkShadow.TryTakePage(out var page))
        {
            List<string> pageLines = PresentationStream.ParseDump(page.Text, out long seq, out _);
            _simLinkShadow.ObservePage(pageLines, seq, page.ArrivalQpc); // keeps the shadow comparison's pending set trimmed
            _presentationSeq = Math.Max(_presentationSeq, seq);
        }
    }

    // The pushed pages queued so far, appended to `lines`; the cursor follows their headers.
    private void TakePushedPages(List<string> lines)
    {
        while (_simLinkShadow!.TryTakePage(out var page))
        {
            List<string> pageLines = PresentationStream.ParseDump(page.Text, out long seq, out _);
            _simLinkShadow.ObservePage(pageLines, seq, page.ArrivalQpc);
            lines.AddRange(pageLines);
            _presentationSeq = Math.Max(_presentationSeq, seq);
        }
    }

    // Server ring path: tell the mod the targets when they change (a fresh target is adopted at its next tick).
    private async Task SendPushTargetsAsync(string targetField, CancellationToken cancellationToken)
    {
        if (_pushTargetsSent == targetField)
        {
            return;
        }

        await _host.SacsRecorderAsync(new[] { "push-targets", targetField }, cancellationToken);
        _pushTargetsSent = targetField;
    }

    // Server ring path: no page for ServerPushSilence (the mod sends at least one a second) means the push stopped (a
    // mod error, a stalled game thread): take what is queued, switch push off, and resume dumps from the last
    // page's cursor. The recorder ring still holds what came after (unless it wrapped), so nothing is lost. A paused game
    // doesn't trigger it: the mod's task runs on real time (checked live, push-095).
    private async Task CheckServerPushAliveAsync(List<string> lines, CancellationToken cancellationToken)
    {
        if (!_serverPushOn || _simLinkShadow == null || DateTime.UtcNow - _simLinkShadow.LastPageUtc < ServerPushSilence)
        {
            return;
        }

        string? reply = await _host.SacsRecorderAsync(new[] { "push-off" }, cancellationToken);
        _simLinkShadow.Flush(); // pages published before push-off may still be in the ring
        TakePushedPages(lines);
        _simLinkShadow.UseDumps();
        _serverPushOn = false;
        _pushTargetsSent = null;
        _serverPushFallbacks++;
        TimeSpan lasted = _simLinkShadow.LastPageUtc - _pushOnSinceUtc;
        ScheduleServerPushRearm(healthy: lasted >= ServerPushHealthy);
        Console.WriteLine($"[simlink-server-ring] FALLBACK to dumps: no page for {ServerPushSilence.TotalMilliseconds:F0} ms; cursor={_presentationSeq}; " +
            $"push_lasted={lasted.TotalSeconds:F1}s rearm_in={_pushRearmDelay.TotalSeconds:F1}s; mod={reply ?? "(no reply)"}");
    }

    // The next push-on attempt: the base delay after a healthy push, otherwise double the last one (capped).
    private void ScheduleServerPushRearm(bool healthy)
    {
        _pushRearmDelay = healthy || _pushRearmDelay <= TimeSpan.Zero
            ? ServerPushRearmDelay
            : TimeSpan.FromTicks(Math.Min(_pushRearmDelay.Ticks * 2, ServerPushRearmMaxDelay.Ticks));
        _pushRearmAtUtc = DateTime.UtcNow + _pushRearmDelay;
    }

    // Server ring path: push-on from the current cursor (the end of the last dump, or the ring's end at start) with the
    // current targets. Called only between dumps, never while one is outstanding, so the mod's push starts exactly where
    // the dumps stopped: nothing missing, nothing twice. Pages left from an earlier push are dropped first.
    private async Task<bool> TryStartServerPushAsync(string targetField, bool rearm, CancellationToken cancellationToken)
    {
        SimsOnline.DedicatedServer.HostBridge.SimLinkShadowSession shadow = _simLinkShadow!;
        int stale = shadow.DiscardQueuedPages();
        string? reply = await _host.SacsRecorderAsync(
            new[] { "push-on", _presentationSeq.ToString(CultureInfo.InvariantCulture), targetField }, cancellationToken);
        _serverPushOn = reply != null && reply.Contains("push=on", StringComparison.Ordinal);
        if (_serverPushOn)
        {
            _pushTargetsSent = targetField;
            _pushOnSinceUtc = DateTime.UtcNow;
            _pushRearmAtUtc = DateTime.MaxValue;
            shadow.PushStarted();
            shadow.UsePages();
            if (rearm)
            {
                _serverPushRearms++;
            }
        }
        else
        {
            _pushTargetsSent = null;
            ScheduleServerPushRearm(healthy: false);
        }

        Console.WriteLine($"[simlink-server-ring] {(rearm ? "re-arm " : string.Empty)}{(_serverPushOn ? "on" : "refused")}: cursor={_presentationSeq} " +
            $"stale_pages={stale}{(_serverPushOn ? string.Empty : $" next_try_in={_pushRearmDelay.TotalSeconds:F1}s")} mod={reply ?? "(no reply)"}");
        return _serverPushOn;
    }

    // Server ring path, on dumps after a fallback or a refusal: once the backoff has passed, try push again. Called right
    // after this tick's dumps, so the cursor is current.
    private async Task MaybeRearmServerPushAsync(string targetField, CancellationToken cancellationToken)
    {
        if (_serverPushOn || _simLinkShadow == null || DateTime.UtcNow < _pushRearmAtUtc
            || !SimsOnline.DedicatedServer.HostBridge.SimLinkShadowSession.ServerRingRequested)
        {
            return;
        }

        if (!_simLinkShadow.Draining)
        {
            _pushRearmAtUtc = DateTime.MaxValue; // no page could arrive: stay on dumps
            Console.WriteLine("[simlink-server-ring] no re-arm: the SimLink drain thread has stopped; staying on dumps");
            return;
        }

        await TryStartServerPushAsync(targetField, rearm: true, cancellationToken);
    }
    private HashSet<ulong> _presentationTargets = new();
    // players that joined and still need every target's history. Sent to them alone: a broadcast
    // made every other client replay (and visibly restart) its Sims' animation on each join.
    private readonly HashSet<int> _presentationJoiners = new();
    private long _joinHistoryWithheld;

    // a joiner's history, gathered off the presentation loop. A gather is a snapshot and a page per driver,
    // each one bridge round trip (about 600 ms at the mod's 250 ms idle poll); awaited inline, it stopped every
    // established player's stream for 7.2 s and 7.7 s on each join (2026-10-21, client2.log). While it runs the
    // stream goes on to everyone else, and the joiners' share of it is held, then sent after their history, so the
    // order a joiner sees is the inline one: history, then every line since the gather began. Only the presentation
    // loop touches these; the gather task writes its own result and counts.
    private sealed class JoinGather
    {
        public JoinGather(List<int> players, int targets, long started, Task<List<string>> history, HistoryCounts counts)
        {
            Players = players;
            Targets = targets;
            Started = started;
            History = history;
            Counts = counts;
        }

        public List<int> Players { get; }
        public int Targets { get; }
        public long Started { get; }
        public Task<List<string>> History { get; }
        public HistoryCounts Counts { get; }
        public bool Background { get; init; }
        public List<string> Held { get; } = new();
        public int HeldTicks { get; set; }
    }

    private readonly List<JoinGather> _joinGathers = new();

    // A gather still running after this is abandoned: its joiners take the held lines without history (logged),
    // rather than wait on a stalled bridge with the stream held.
    private static readonly TimeSpan JoinGatherLimit = TimeSpan.FromSeconds(30);

    /// <summary>joiners whose history is being gathered, their live stream held (tests read it).</summary>
    internal int HeldJoinerCount => _joinGathers.Sum(gather => gather.Players.Count);

    /// <summary>whether a background history gather is still asking the recorder (tests wait on it).</summary>
    internal bool JoinGatherRunning => _joinGathers.Any(gather => !gather.History.IsCompleted);

    /// <summary> G14: where a joiner's presentation-history trace block goes (tests capture it).</summary>
    internal Action<string> TraceLog { get; set; } = Console.WriteLine;

    /// <summary>joins whose known-target history was not broadcast (no per-player channel, players established).</summary>
    public long JoinHistoryWithheld => Interlocked.Read(ref _joinHistoryWithheld);

    /// <summary>whether SyncBroadcaster runs the presentation stream (default on;
    /// SIMSONLINE_PRESENTATION_STREAM=0 turns it off). each joiner's session_joined lists presentation_stream
    /// while it is on, so the joiner never starts interaction replay.</summary>
    public bool PresentationStreamEnabled { get; private set; } = true;

    public void ConfigurePresentationStream(bool enabled) => PresentationStreamEnabled = enabled;

    /// <summary>whether the presentation stream also streams routes (clients walk streamed Sims to
    /// the server's route goal). Opt-in, SIMSONLINE_ROUTE_STREAM=1; sent to the server mod with ring-on.</summary>
    public bool RouteStreamEnabled { get; private set; }

    public void ConfigureRouteStream(bool enabled) => RouteStreamEnabled = enabled;

    /// <summary> card 55: whether the server mod's walks take the Rust plan at the RouteCtor seam (RouteRecorder's
    /// plan overrides, RustRoutePlan). Opt-in, SIMSONLINE_RUST_ROUTE=1; sent to the server mod by StartRustRouteAsync once the session starts.</summary>
    public bool RustRouteEnabled { get; private set; }

    public void ConfigureRustRoute(bool enabled) => RustRouteEnabled = enabled;

    private bool _rustRouteSent;

    /// <summary>
    /// card 55 R3: turn the mod's Rust route switch on (once) and answer its plan requests through <paramref name="hop"/>.
    /// Independent of the presentation stream and the ring: the request travels as a bridge EVENT and the plan returns as a
    /// SACS_RECORDER verb. No-op unless SIMSONLINE_RUST_ROUTE=1.
    /// </summary>
    public async Task StartRustRouteAsync(RoutePlanHop? hop, Action<Action<SimsOnline.DedicatedServer.HostBridge.RoutePlanRequest>>? subscribe, CancellationToken cancellationToken)
    {
        if (!RustRouteEnabled || _rustRouteSent)
        {
            return;
        }

        _rustRouteSent = true;
        if (hop != null && subscribe != null)
        {
            subscribe(request => _ = hop.HandleAsync(request, CancellationToken.None));
        }

        await _host.SacsRecorderAsync(new[] { "rust-route-on" }, cancellationToken);
    }

    /// <summary>Sims whose animation is streamed to clients.</summary>
    public int PresentationTargetCount => _presentationTargets.Count;

    /// <summary>
    /// presentation stream: page the server TS3's animation calls for Sims on player lots
    /// (SACS_RECORDER dump with targets) and broadcast them as SimAnimation. A Sim that became a target
    /// first gets its drivers' history (snapshot / snapshot-driver) in the broadcast, so clients play the
    /// path to its current state. A player that joined gets the history of every other target on its own
    /// (<paramref name="enqueueToPlayerAsync"/>), before this tick's broadcast; without that channel the
    /// history goes to everyone. With a broadcast that can leave players out (<paramref name="enqueueBatchExceptAsync"/>,
    /// what SyncBroadcaster passes), the joiner's history is gathered in the background and the joiner's live lines
    /// are held until it is sent; without it the gather is awaited inline and the stream waits for it. Returns the
    /// lines sent; 0 when nobody is claimed or the host has no recorder channel.
    /// </summary>
    // opt-in timing; never log or allocate formatting in the normal stream.
    private static readonly bool sTraceTransportLatency =
        Environment.GetEnvironmentVariable("SIMSONLINE_TRANSPORT_TRACE") == "1";

    public async Task<int> PublishPresentationAsync(
        Func<IReadOnlyList<ServerEvent>, CancellationToken, Task<bool>> enqueueBatchAsync,
        CancellationToken cancellationToken,
        Func<int, IReadOnlyList<ServerEvent>, CancellationToken, Task<bool>>? enqueueToPlayerAsync = null,
        Func<IReadOnlyList<ServerEvent>, IReadOnlyCollection<int>, CancellationToken, Task<bool>>? enqueueBatchExceptAsync = null)
    {
        ArgumentNullException.ThrowIfNull(enqueueBatchAsync);
        HashSet<ulong> targets;
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            targets = _claims.Count == 0
                ? new HashSet<ulong>()
                : PresentationStream.Targets(_claims, _interestLotIndexSnapshot?.Sims ?? (IReadOnlyList<SimSnapshot>)Array.Empty<SimSnapshot>());
        }
        finally
        {
            _commandGate.Release();
        }

        if (_presentationSeq < 0)
        {
            // Start from the ring's end: anything earlier reaches clients through history snapshots.
            string? start = await _host.SacsRecorderAsync(
                sTraceTransportLatency ? new[] { "ring-on", "trace" } : new[] { "ring-on" },
                cancellationToken);
            if (start == null)
            {
                return 0;
            }

            if (RouteStreamEnabled)
            {
                await _host.SacsRecorderAsync(new[] { "routes-on" }, cancellationToken);
            }

            if (_simLinkShadow == null
                && (SimsOnline.DedicatedServer.HostBridge.SimLinkShadowSession.Requested
                    || SimsOnline.DedicatedServer.HostBridge.SimLinkShadowSession.ServerRingRequested))
            {
                _simLinkShadow = SimLinkSessionOverride ?? await SimsOnline.DedicatedServer.HostBridge.SimLinkShadowSession.TryStartAsync(
                    (_host as HeadlessSims3Host)?.HostProcessId,
                    null,
                    (subcommand, token) => _host.SacsRecorderAsync(new[] { subcommand }, token),
                    Console.WriteLine,
                    cancellationToken,
                    mirror: SimsOnline.DedicatedServer.HostBridge.SimLinkShadowSession.Requested);
            }

            if (SimsOnline.DedicatedServer.HostBridge.SimLinkShadowSession.ServerRingRequested)
            {
                // A mod still pushing for an earlier controller would refuse the dump below.
                await _host.SacsRecorderAsync(new[] { "push-off" }, cancellationToken);
            }

            PresentationStream.ParseDump(
                await _host.SacsRecorderAsync(new[] { "dump", PresentationStream.FromNowCursor.ToString(CultureInfo.InvariantCulture) }, cancellationToken),
                out long startSeq,
                out _);
            _presentationSeq = Math.Max(0, startSeq);
            _presentationTargets = new HashSet<ulong>();

            if (_simLinkShadow != null && SimsOnline.DedicatedServer.HostBridge.SimLinkShadowSession.ServerRingRequested)
            {
                await TryStartServerPushAsync("-", rearm: false, cancellationToken);
            }
        }

        if (_serverPushOn && targets.Count == 0)
        {
            // Nothing to stream: the mod filters everything out; keep the cursor moving and the queue empty.
            await SendPushTargetsAsync("-", cancellationToken);
            DiscardPresentationPages();
            await CheckServerPushAliveAsync(new List<string>(), cancellationToken);
            _presentationTargets = targets;
            return 0;
        }

        if (targets.Count == 0)
        {
            // Nothing to stream: skip the backlog, the next targets start with their history.
            PresentationStream.ParseDump(
                await _host.SacsRecorderAsync(new[] { "dump", PresentationStream.FromNowCursor.ToString(CultureInfo.InvariantCulture) }, cancellationToken),
                out long endSeq,
                out _);
            _presentationSeq = Math.Max(_presentationSeq, endSeq);
            _presentationTargets = targets;
            await MaybeRearmServerPushAsync(SimsOnlineServerHost.SimScopeWire.Empty, cancellationToken);
            return 0;
        }

        // Joiners waiting while nothing was streamed stay pending until there is something to send.
        List<int> joiners;
        lock (_presentationJoiners)
        {
            joiners = _presentationJoiners.ToList();
            _presentationJoiners.Clear();
        }

        // without a per-player channel a joiner's history can only go to everyone, and a player already shown
        // these Sims replays it: the late-join reset (Z, StopAllAnimation) and each driver's resync restart the
        // animation it is playing. Broadcast it only while nobody is established; otherwise only fresh targets' history
        // goes out, the joiners take the live stream, and the withheld joins are counted. The per-player path
        // (enqueueToPlayerAsync, what SyncBroadcaster passes) sends the history to the joiner alone.
        bool established = _presentationTargets.Count > 0 && _players.Count > joiners.Count;
        bool broadcastAll = joiners.Count > 0 && enqueueToPlayerAsync == null && !established;
        if (joiners.Count > 0 && enqueueToPlayerAsync == null && established)
        {
            Interlocked.Add(ref _joinHistoryWithheld, joiners.Count);
            Console.WriteLine($"[presentation] join history withheld for {joiners.Count} joiner(s): no per-player channel, {_players.Count - joiners.Count} player(s) established");
        }
        if (_serverPushOn)
        {
            // Targets first, then the history snapshot: a fresh target's lines between the two are duplicated (as with
            // dumps), never missing.
            await SendPushTargetsAsync(SimsOnlineServerHost.SimScopeWire.Format(targets.OrderBy(t => t).ToList()), cancellationToken);
        }

        List<ulong> fresh = (broadcastAll ? targets : targets.Where(t => !_presentationTargets.Contains(t))).OrderBy(t => t).ToList();
        List<string> lines = await PresentationHistoryAsync(fresh, cancellationToken);
        int sent = 0;
        if (!broadcastAll && joiners.Count > 0 && enqueueToPlayerAsync != null)
        {
            // Everything a joiner has not seen except the fresh targets, whose history this tick's
            // broadcast carries to everyone.
            List<ulong> known = targets.Where(t => !fresh.Contains(t)).OrderBy(t => t).ToList();
            // Nothing known (the first join: every target is fresh) gathers nothing, so nobody is held.
            JoinGather gather = StartJoinGather(joiners, known, background: enqueueBatchExceptAsync != null && known.Count > 0, cancellationToken);
            if (!gather.Background)
            {
                await gather.History;
                sent += await DeliverJoinGatherAsync(gather, enqueueToPlayerAsync, cancellationToken);
            }
        }

        _presentationTargets = targets;
        string targetField = SimsOnlineServerHost.SimScopeWire.Format(targets.OrderBy(t => t).ToList());
        if (_serverPushOn)
        {
            TakePushedPages(lines);
            await CheckServerPushAliveAsync(lines, cancellationToken);
        }

        for (int page = 0; !_serverPushOn && page < PresentationStream.MaxDumpPages; page++)
        {
            long startSequence = _presentationSeq;
            long started = sTraceTransportLatency ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
            string? report = await _host.SacsRecorderAsync(
                sTraceTransportLatency
                    ? new[] { "dump", startSequence.ToString(CultureInfo.InvariantCulture), targetField, "trace" }
                    : new[] { "dump", startSequence.ToString(CultureInfo.InvariantCulture), targetField },
                cancellationToken);
            List<string> pageLines = PresentationStream.ParseDump(report, out long seq, out bool more);
            _simLinkShadow?.ObserveDump(pageLines, seq);
            if (sTraceTransportLatency &&
                PresentationStream.TryParseSourceClockSamples(report,
                    out long firstSourceSeq, out long firstSourceUtc,
                    out long lastSourceSeq, out long lastSourceUtc))
            {
                Console.WriteLine(
                    $"[transport-latency] stage=source_record source_seq={firstSourceSeq} " +
                    $"source_utc_ticks={firstSourceUtc} at_utc_ticks={DateTime.UtcNow.Ticks}");
                if (lastSourceSeq != firstSourceSeq)
                {
                    Console.WriteLine(
                        $"[transport-latency] stage=source_record source_seq={lastSourceSeq} " +
                        $"source_utc_ticks={lastSourceUtc} at_utc_ticks={DateTime.UtcNow.Ticks}");
                }
            }
            if (sTraceTransportLatency)
            {
                foreach (var sample in PresentationStream.ParseSacsOriginSamples(report))
                {
                    Console.WriteLine(
                        $"[transport-latency] stage=source_event source_origin=sacs_proxy " +
                        $"source_seq={sample.Sequence} source_utc_ticks={sample.UtcTicks} " +
                        $"at_utc_ticks={DateTime.UtcNow.Ticks}");
                }
            }
            lines.AddRange(pageLines);
            if (sTraceTransportLatency)
            {
                long finished = System.Diagnostics.Stopwatch.GetTimestamp();
                double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(started, finished).TotalMilliseconds;
                Console.WriteLine(
                    $"[transport-latency] stage=server_bridge_dump elapsed_ms={elapsedMs:F3} " +
                    $"start_qpc={started} end_qpc={finished} qpc_freq={System.Diagnostics.Stopwatch.Frequency} page={page} " +
                    $"chars={report?.Length ?? 0} lines={pageLines.Count} from_seq={startSequence} " +
                    $"to_seq={seq} more={(more ? 1 : 0)} targets={targets.Count}");
            }

            if (seq >= 0)
            {
                _presentationSeq = seq;
            }

            if (!more)
            {
                break;
            }
        }

        // A finished background gather goes before this tick's lines: its joiners get history, held lines, then these.
        sent += await DeliverFinishedJoinGathersAsync(enqueueToPlayerAsync, cancellationToken);
        await MaybeRearmServerPushAsync(targetField, cancellationToken);
        if (lines.Count == 0)
        {
            return sent;
        }

        _lastPresentationBroadcastUtc = DateTime.UtcNow;

        // trace *before* invoking network enqueue/delivery, not after
        // a potentially slow recipient has delayed completion. Each SimAnimation
        // message has a stable sequence interval shared with the receiving client.
        List<ServerEvent> outbound = PresentationEvents(lines);
        if (sTraceTransportLatency)
        {
            foreach (ServerEvent.SimAnimation animation in outbound.OfType<ServerEvent.SimAnimation>())
            {
                var span = PresentationStream.EventSequenceBounds(animation.Lines);
                Console.WriteLine(
                    $"[transport-latency] stage=server_presentation_dispatch at_ms={Environment.TickCount64} " +
                    $"at_qpc={System.Diagnostics.Stopwatch.GetTimestamp()} qpc_freq={System.Diagnostics.Stopwatch.Frequency} " +
                    $"at_utc_ticks={DateTime.UtcNow.Ticks} " +
                    $"first_seq={span.First} last_seq={span.Last} lines={animation.Lines.Count}");
            }
        }
        if (_joinGathers.Count > 0 && enqueueBatchExceptAsync != null)
        {
            // Joiners whose history is still being gathered get these lines after it.
            HashSet<int> held = new();
            foreach (JoinGather gather in _joinGathers)
            {
                gather.Held.AddRange(lines);
                gather.HeldTicks++;
                held.UnionWith(gather.Players);
            }

            return await enqueueBatchExceptAsync(outbound, held, cancellationToken) ? sent + lines.Count : sent;
        }

        // Not delivered (no audience): the lines are gone; a later join resyncs every target.
        return await enqueueBatchAsync(outbound, cancellationToken) ? sent + lines.Count : sent;
    }

    // A joiner's history gather: in the background (its joiners held, JoinGather), or for the caller to await. A player
    // joining again while an earlier gather of its is still running takes the new one's history.
    private JoinGather StartJoinGather(List<int> players, List<ulong> known, bool background, CancellationToken cancellationToken)
    {
        foreach (JoinGather pending in _joinGathers)
        {
            pending.Players.RemoveAll(players.Contains);
        }

        _joinGathers.RemoveAll(pending => pending.Players.Count == 0);
        HistoryCounts counts = new();
        async Task<List<string>> GatherAsync()
        {
            List<string> history = await PresentationHistoryAsync(known, cancellationToken, counts);
            counts.Finished = JoinTrace.Now();
            return history;
        }

        long started = JoinTrace.Now();
        JoinGather gather = new(players, known.Count, started, background ? Task.Run(GatherAsync, cancellationToken) : GatherAsync(), counts)
        {
            Background = background,
        };
        if (background)
        {
            _joinGathers.Add(gather);
        }

        return gather;
    }

    // The background gathers that finished, or ran past JoinGatherLimit, sent to their joiners.
    private async Task<int> DeliverFinishedJoinGathersAsync(
        Func<int, IReadOnlyList<ServerEvent>, CancellationToken, Task<bool>>? enqueueToPlayerAsync,
        CancellationToken cancellationToken)
    {
        int sent = 0;
        foreach (JoinGather gather in _joinGathers.ToList())
        {
            if (!gather.History.IsCompleted && System.Diagnostics.Stopwatch.GetElapsedTime(gather.Started) < JoinGatherLimit)
            {
                continue;
            }

            _joinGathers.Remove(gather);
            if (enqueueToPlayerAsync != null)
            {
                sent += await DeliverJoinGatherAsync(gather, enqueueToPlayerAsync, cancellationToken);
            }
        }

        return sent;
    }

    // G14: one trace block per joiner: the gather, the hold (a background gather), the send with the pump behind
    // it, and the events. A gather that failed or ran past its limit sends the held lines alone.
    private async Task<int> DeliverJoinGatherAsync(
        JoinGather gather,
        Func<int, IReadOnlyList<ServerEvent>, CancellationToken, Task<bool>> enqueueToPlayerAsync,
        CancellationToken cancellationToken)
    {
        List<string>? history = gather.History.IsCompletedSuccessfully ? gather.History.Result : null;
        string? failure = history != null ? null
            : !gather.History.IsCompleted ? string.Create(CultureInfo.InvariantCulture, $"still running after {JoinGatherLimit.TotalSeconds:F0} s")
            : gather.History.Exception?.GetBaseException().Message ?? "cancelled";
        if (failure != null)
        {
            Console.WriteLine($"[presentation] join history for player(s) {string.Join(",", gather.Players)} not sent: {failure}; {gather.Held.Count} held line(s) go alone");
        }

        List<string> lines = history == null ? gather.Held : history.Concat(gather.Held).ToList();
        long now = JoinTrace.Now();
        string gatherDetail = string.Create(CultureInfo.InvariantCulture,
            $"targets={gather.Targets} drivers={gather.Counts.Drivers} pages={gather.Counts.Pages} lines={history?.Count ?? 0}")
            + (failure == null ? string.Empty : " failed=" + failure);
        int sent = 0;
        foreach (int player in gather.Players)
        {
            JoinTrace trace = new(
                "player=" + player.ToString(CultureInfo.InvariantCulture), "presentation-history", started: gather.Started);
            trace.Step("gather", gather.Started, gather.Counts.Finished > 0 ? gather.Counts.Finished : now, gatherDetail);
            if (gather.Background)
            {
                trace.Step("hold", gather.Started, now, string.Create(CultureInfo.InvariantCulture,
                    $"lines={gather.Held.Count} ticks={gather.HeldTicks}"));
            }

            if (lines.Count == 0)
            {
                trace.Outcome = "nothing to send";
                trace.Emit(TraceLog);
                continue;
            }

            bool delivered;
            using (JoinTrace.Enter(trace))
            {
                delivered = await enqueueToPlayerAsync(player, PresentationEvents(lines), cancellationToken);
            }
            trace.Outcome = (delivered ? "delivered" : "not delivered") + (failure == null ? string.Empty : " without history");
            trace.Emit(TraceLog);
            if (delivered)
            {
                sent += lines.Count;
            }
        }

        return sent;
    }

    // The drivers animating these Sims, each with its own calls so far (SACS_RECORDER snapshot /
    // snapshot-driver, paged).
    /// <summary> G14: what a history gather asked the server's recorder for.</summary>
    private sealed class HistoryCounts
    {
        public int Drivers;
        public int Pages;
        public long Finished; // JoinTrace.Now() when the gather ended (0 while it runs)
    }

    private async Task<List<string>> PresentationHistoryAsync(
        IReadOnlyList<ulong> sims,
        CancellationToken cancellationToken,
        HistoryCounts? counts = null)
    {
        List<string> lines = new();
        if (sims.Count == 0)
        {
            return lines;
        }

        // One gather at a time: the mod's snapshot keeps its targets (BoundaryState.LastTargets) for the pages after
        // it, so a fresh target's gather between a joiner's snapshot and its pages would change what they read.
        await _historyGate.WaitAsync(cancellationToken);
        try
        {
            string? drivers = await _host.SacsRecorderAsync(new[] { "snapshot", SimsOnlineServerHost.SimScopeWire.Format(sims.ToList()) }, cancellationToken);
            foreach (uint handle in PresentationStream.ParseSnapshotDrivers(drivers))
            {
                int offset = 0;
                if (counts != null)
                {
                    counts.Drivers++;
                }
                for (int page = 0; page < PresentationStream.MaxDumpPages && offset >= 0; page++)
                {
                    if (counts != null)
                    {
                        counts.Pages++;
                    }
                    string? report = await _host.SacsRecorderAsync(
                        new[] { "snapshot-driver", handle.ToString(CultureInfo.InvariantCulture), offset.ToString(CultureInfo.InvariantCulture) },
                        cancellationToken);
                    lines.AddRange(PresentationStream.ParseSnapshotDriver(report, out offset));
                }
            }
        }
        finally
        {
            _historyGate.Release();
        }

        return lines;
    }

    private readonly SemaphoreSlim _historyGate = new(1, 1);

    private static List<ServerEvent> PresentationEvents(List<string> lines)
    {
        List<ServerEvent> events = new();
        for (int i = 0; i < lines.Count; i += PresentationStream.MaxLinesPerEvent)
        {
            events.Add(new ServerEvent.SimAnimation(lines.Skip(i).Take(PresentationStream.MaxLinesPerEvent).ToList()));
        }

        return events;
    }

    public SimulationSnapshot ApplyInterestScopeForPlayer(int playerId, SimulationSnapshot snapshot)
    {
        if (!_interestScopedMirroring || !MirroringInterestScope.ShouldFilter(_claims))
        {
            return snapshot;
        }

        HashSet<ulong> lots = MirroringInterestScope.ComputeInterestLots(_claims, snapshot.Sims);
        HashSet<ulong> claimed = MirroringInterestScope.ClaimedSimIds(_claims);
        return MirroringInterestScope.FilterSnapshot(snapshot, lots, claimed);
    }

    /// <summary>joined player count for lot-local authority detection.</summary>
    public int JoinedPlayerCount => _players.Count;

    /// <summary>instantaneous mode (no hysteresis) for one player.</summary>
    public LotLocalAuthorityMode DetectLotLocalAuthorityModeRawForPlayer(int playerId) =>
        DetectLotLocalAuthorityModeCore(playerId);

    /// <summary>
    /// PR#17 review fix: evaluate the hysteresis-stable mode for a player WITHOUT
    /// committing it to the stable map, queuing a reconcile, or emitting an event. Callers that
    /// need a fresh gating decision (snapshot scoping, interaction gating, SaveGame checkpoint
    /// gate, LeaveSession best-effort merge) use this peek so they cannot silently flip
    /// solo_native/prox_mirror -> full_puppet_fallback and bypass the slice-6 reconcile gate.
    /// Only the reconcile-aware refresh path (RefreshLotLocalAuthorityModesWithReconcileAsync)
    /// commits the stable mode and emits transition events.
    /// </summary>
    public LotLocalAuthorityMode DetectLotLocalAuthorityModeForPlayer(int playerId)
    {
        LotLocalAuthorityMode raw = DetectLotLocalAuthorityModeCore(playerId);
        if (!_lotLocalAuthorityHysteresis.TryGetValue(playerId, out LotLocalAuthorityHysteresis? hysteresis))
        {
            hysteresis = new LotLocalAuthorityHysteresis();
            _lotLocalAuthorityHysteresis[playerId] = hysteresis;
        }

        return hysteresis.Update(
            raw,
            DateTimeOffset.UtcNow,
            LotLocalAuthorityPolicy.EnterHold,
            LotLocalAuthorityPolicy.ExitHold);
    }

    /// <summary>read last committed stable mode without recomputing.</summary>
    public LotLocalAuthorityMode GetStableLotLocalAuthorityMode(int playerId) =>
        _lotLocalAuthorityStable.TryGetValue(playerId, out LotLocalAuthorityMode mode)
            ? mode
            : LotLocalAuthorityMode.FullPuppetFallback;

    /// <summary>
    /// slice 4: refresh stable modes and return per-player transition events. This is the
    /// non-reconcile-aware variant used by JoinSession (command-gate held). The broadcaster must
    /// use RefreshLotLocalAuthorityModesWithReconcileAsync instead so the mode-exit reconcile
    /// completes before events are emitted.
    /// </summary>
    public IReadOnlyList<ServerEvent.LotLocalAuthorityModeChanged> RefreshLotLocalAuthorityModes(
        DateTimeOffset now)
    {
        _commandGate.Wait();
        try
        {
            return RefreshLotLocalAuthorityModesCore(now);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private IReadOnlyList<ServerEvent.LotLocalAuthorityModeChanged> RefreshLotLocalAuthorityModesCore(
        DateTimeOffset now)
    {
        List<ServerEvent.LotLocalAuthorityModeChanged> changes = new();
        foreach (KeyValuePair<int, PlayerSession> entry in _players)
        {
            ComputeStableLotLocalAuthorityMode(entry.Key, now, changes);
        }

        return changes;
    }

    /// <summary>
    /// Commit path: compute the hysteresis-stable mode, detect transitions, and -- on a
    /// client-native -> full_puppet_fallback exit -- queue the reconcile and DEFER the mode
    /// commit + event emission until the reconcile completes. Non-exit transitions commit
    /// immediately. Returns true if the transition was deferred (caller must re-run the commit
    /// after the reconcile drain succeeds).
    /// </summary>
    private bool ComputeStableLotLocalAuthorityMode(
        int playerId,
        DateTimeOffset now,
        List<ServerEvent.LotLocalAuthorityModeChanged> changes)
    {
        LotLocalAuthorityMode raw = DetectLotLocalAuthorityModeCore(playerId);
        if (!_lotLocalAuthorityHysteresis.TryGetValue(playerId, out LotLocalAuthorityHysteresis? hysteresis))
        {
            hysteresis = new LotLocalAuthorityHysteresis();
            _lotLocalAuthorityHysteresis[playerId] = hysteresis;
        }

        LotLocalAuthorityMode stable = hysteresis.Update(
            raw,
            now,
            LotLocalAuthorityPolicy.EnterHold,
            LotLocalAuthorityPolicy.ExitHold);

        if (!_lotLocalAuthorityStable.TryGetValue(playerId, out LotLocalAuthorityMode previous))
        {
            CommitStableLotLocalAuthorityMode(playerId, stable);
            if (!_clientNativeModes)
            {
                // One simulator: tell the client its mode outright, so it stops deriving a
                // client-native mode locally from its own claims (Program.RefreshLotLocalAuthorityMode).
                changes.Add(new ServerEvent.LotLocalAuthorityModeChanged(playerId, stable));
            }

            return false;
        }

        if (previous == stable)
        {
            return false;
        }

        // Transition detected: previous != stable.
        bool isExitToFullPuppet = LotLocalAuthorityMirrorGate.OwnsNativeSimulation(previous)
            && stable == LotLocalAuthorityMode.FullPuppetFallback;

        if (isExitToFullPuppet)
        {
            // slice 6 trigger 2: queue the reconcile and DEFER the mode commit + event
            // until the reconcile drain merges the pending export. If the drain is a no-op (no
            // export stored), the mode stays at the previous native value so CollectLotExportRequests
            // still sees the player as native and requests the export; the commit + event fires on
            // a subsequent refresh once the export lands and merges. This honours the doc's
            // ordering: reconcile completes before the event that re-enables owned APPLY_*.
            _pendingModeExitReconciles.Add(playerId);
            return true; // deferred -- caller re-commits after drain
        }

        // Non-exit transition (e.g. full_puppet -> solo_native, or solo <-> prox): commit + emit.
        CommitStableLotLocalAuthorityMode(playerId, stable);
        changes.Add(new ServerEvent.LotLocalAuthorityModeChanged(playerId, stable));
        return false;
    }

    private void CommitStableLotLocalAuthorityMode(int playerId, LotLocalAuthorityMode mode)
    {
        _lotLocalAuthorityStable[playerId] = mode;
        InvalidateMotiveAuthorityState();
    }

    /// <summary>
    /// Commit a deferred mode-exit transition for a player after the reconcile drain has run.
    /// Emits the LotLocalAuthorityModeChanged event for full_puppet_fallback.
    /// </summary>
    private void CommitDeferredModeExit(int playerId, List<ServerEvent.LotLocalAuthorityModeChanged> changes)
    {
        _lotLocalAuthorityStable[playerId] = LotLocalAuthorityMode.FullPuppetFallback;
        InvalidateMotiveAuthorityState();
        changes.Add(new ServerEvent.LotLocalAuthorityModeChanged(playerId, LotLocalAuthorityMode.FullPuppetFallback));
    }

    /// <summary>
    /// slice 6: refresh stable modes AND run the mode-exit reconcile gate before
    /// returning the transition events. Callers that broadcast (SyncBroadcaster) must use this
    /// variant so the pending lot merge lands in the server TS3 before the mode-change event is
    /// emitted to clients (docs/archive/dev-149-slice5-partial-save-reconciliation.md, trigger 2 ordering).
    /// </summary>
    public async Task<IReadOnlyList<ServerEvent>> RefreshLotLocalAuthorityModesWithReconcileAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // PR#15 review fix 2: this runs on the SyncBroadcaster loop, concurrently with
        // HandleCommandAsync (SubmitLotExport / SaveGame / LeaveSession), all of which mutate the
        // shared hysteresis/_pendingModeExitReconciles/_lotReconcile state under _commandGate. Take
        // the same gate so the refresh + reconcile drain is serialised against command handling.
        // This method is only ever called from the broadcaster (never re-entrantly from within
        // HandleCommandAsync), so acquiring the gate here cannot deadlock.
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            List<ServerEvent.LotLocalAuthorityModeChanged> changes = new();
            List<ServerEvent.LotReconcileAcknowledged> reconcileAcks = new();
            List<int> deferredExits = new();
            foreach (KeyValuePair<int, PlayerSession> entry in _players)
            {
                if (ComputeStableLotLocalAuthorityMode(entry.Key, now, changes))
                {
                    deferredExits.Add(entry.Key);
                }
            }

            // PR#17 review fix: the reconcile drain runs BEFORE the deferred mode-exit
            // commits + events. If a pending export exists, it merges now and the exit commits.
            // If no export is pending, the drain is a no-op and the exit stays deferred -- the
            // mode remains at the previous native value so CollectLotExportRequestsAsync still
            // requests an export; the commit + event fires on a subsequent refresh once the
            // export lands and merges. This enforces the doc's "reconcile completes before the
            // event that re-enables owned APPLY_*" ordering.
            await DrainPendingModeExitReconcilesAsync(reconcileAcks, cancellationToken);

            // After the drain, resolve every deferred exit (this cycle's new ones plus any stale
            // entries from prior cycles whose drain just succeeded). The drain removed players
            // whose merge succeeded from _pendingModeExitReconciles, so:
            //   - player NOT in the set => drain succeeded => reconcile complete. If the exit
            //     condition still holds (raw is full_puppet_fallback), commit + emit. If the Sim
            //     returned to the lot (raw is native), cancel -- the export was merged, the player
            //     stays native, no exit event.
            //   - player STILL in the set => drain was a no-op (no export / merge failed). If the
            //     exit condition cleared, cancel the stale deferral. Otherwise keep deferred.
            HashSet<int> allDeferred = new(deferredExits);
            allDeferred.UnionWith(_pendingModeExitReconciles);
            foreach (int playerId in allDeferred)
            {
                LotLocalAuthorityMode currentRaw = DetectLotLocalAuthorityModeCore(playerId);
                bool stillExiting = currentRaw == LotLocalAuthorityMode.FullPuppetFallback;
                // The reconcile is complete if the drain merged the export (entry removed from
                // _pendingModeExitReconciles) OR the export was already merged out-of-band by
                // SubmitLotExport (the tracker shows the player as reconciled within the freshness
                // window). The latter catches the case where SubmitLotExport's inline merge +
                // MarkMerged ran between broadcaster cycles -- the drain's TryGetPendingMerge
                // returns false (already merged), so the drain is a no-op and the player stays in
                // the set, but the reconcile DID complete. We must NOT treat "no export ever
                // recorded" as complete -- IsReconciledForCheckpoint returns false when there's no
                // entry, so that case stays deferred.
                bool reconcileCompleted = !_pendingModeExitReconciles.Contains(playerId)
                    || _lotReconcile.IsReconciledForCheckpoint(
                        playerId,
                        DateTimeOffset.UtcNow,
                        LotReconcilePolicy.ExportFreshnessWindow);

                if (!stillExiting)
                {
                    // Exit condition cleared (Sim returned to lot / re-claimed): cancel the
                    // deferred exit regardless of drain outcome. The export (if any) was merged
                    // by the drain or by SubmitLotExport; the player stays native.
                    _pendingModeExitReconciles.Remove(playerId);
                    continue;
                }

                if (reconcileCompleted)
                {
                    // Exit condition holds AND the reconcile completed: commit + emit. Remove
                    // from the set in case the drain didn't (out-of-band merge case).
                    _pendingModeExitReconciles.Remove(playerId);
                    CommitDeferredModeExit(playerId, changes);
                }
                // else: exit condition holds but reconcile incomplete -- keep deferred.
            }

            // return the mode-change events + the reconcile acks from the drain. The
            // broadcaster emits both per-player; the ack lets the client track the new version
            // from a mode-exit merge (not just from SubmitLotExport).
            List<ServerEvent> allEvents = new(changes.Count + reconcileAcks.Count);
            allEvents.AddRange(changes);
            allEvents.AddRange(reconcileAcks);
            return allEvents;
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private async Task DrainPendingModeExitReconcilesAsync(List<ServerEvent.LotReconcileAcknowledged> acks, CancellationToken cancellationToken)
    {
        if (_pendingModeExitReconciles.Count == 0)
        {
            return;
        }

        int[] players = _pendingModeExitReconciles.ToArray();
        // PR#17 review fix: remove only players whose merge SUCCEEDED; players with no
        // pending export stay in the set so the caller knows the reconcile did not complete and
        // the mode-exit commit must be deferred.
        foreach (int playerId in players)
        {
            // tuple return; .Merged gates the deferred removal (PR#17 review fix).
            // slice 5 (design §3.1): emit LotReconcileAcknowledged so the client
            // learns the new version even from a mode-exit merge (not just SubmitLotExport).
            // Without this, the client's s_lastReconciledVersion goes stale and the next export
            // forces a false version-mismatch. The broadcaster emits the ack per-player.
            ulong lotId = _lotReconcile.TryGetPendingMerge(playerId, out LotExportBundle pendingBundle)
                ? pendingBundle.LotId
                : 0UL;
            (bool merged, long newVersion) = await TryMergePendingLotExportByPayloadAsync(playerId, "mode_exit", cancellationToken);
            if (merged)
            {
                _pendingModeExitReconciles.Remove(playerId);
                if (newVersion > 0 && lotId != 0UL)
                {
                    acks.Add(new ServerEvent.LotReconcileAcknowledged(playerId, lotId, newVersion));
                }
            }
        }
    }

    /// <summary>
    /// slice 6: best-effort merge of the player's latest unmerged lot export into the
    /// server TS3. Failures are logged and swallowed — mode-exit and leave are best-effort
    /// triggers; only the SaveGame checkpoint gate is allowed to block.
    /// slice 5 (design §3.1): returns the new monotonic checkpoint version (0 if
    /// no merge happened) so the caller can emit <see cref="ServerEvent.LotReconcileAcknowledged"/>
    /// to the owning client. The client echoes this version back in its next
    /// <see cref="ClientCommand.SubmitLotExport"/> so the coordinator can classify contestable
    /// fields (version mismatch means the client missed a server merge).
    /// </summary>
    private async Task<(bool Merged, long NewVersion)> TryMergePendingLotExportAsync(int playerId, string reason, CancellationToken cancellationToken)
    {
        if (!_lotReconcile.TryGetPendingMerge(playerId, out LotExportBundle bundle))
        {
            return (false, 0L);
        }

        try
        {
            // slice 5 (design §3 + §4): build the merge plan via the coordinator.
            // Runs conflict detection (monotonic version + per-field lastMutatedTick) and applies
            // the ownership-split policy. The plan's FilteredBundle is what reaches the host; for
            // lot-resident owned entities the client wins by ownership (§2.1) unless the field is
            // ServerWins (server-only mutation since last reconcile), in which case that Sim is
            // dropped from the apply set so the server-canonical state survives (§4.3 step 3).
            LotReconcilePlan plan = LotReconcileCoordinator.BuildMergePlan(
                bundle,
                _lotReconcile,
                _serverFieldTicks,
                clientExportTime: bundle.ClientTick);

            await _host.MergeLotStateAsync(plan.FilteredBundle, cancellationToken);
            _lotReconcile.MarkMerged(playerId);
            long newVersion = _lotReconcile.BumpVersion(playerId, bundle.LotId);
            _lotReconcile.RecordSnapshot(playerId, bundle.LotId, new LotReconcileSnapshot(
                playerId,
                bundle.LotId,
                newVersion,
                Array.Empty<LotReconcileRelationshipEdge>()));
            // slice 5 (design §3.2): advance the per-field last-reconciled tick to
            // max(ClientTick, ServerTick) so the next ClassifyField call compares against the
            // post-merge baseline, not a hardcoded 0. Without this, a server mutation recorded
            // before the first reconcile keeps classifying as "serverMutated" forever.
            foreach (LotReconcileFieldResult field in plan.Fields)
            {
                long mergedTick = Math.Max(field.ClientTick, field.ServerTick);
                _serverFieldTicks.RecordReconciledTick(playerId, bundle.LotId, field.Field, mergedTick);
            }
            Console.WriteLine($"[reconcile] lot merge player={playerId} lot={bundle.LotId} sims={bundle.Sims.Count} reason={reason} version={newVersion} contested={plan.HasContestedFields}");
            return (true, newVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[reconcile] lot merge FAILED player={playerId} reason={reason}: {ex.Message}");
            return (false, 0L);
        }
    }

    private Task<(bool Merged, long NewVersion)> TryMergePendingLotExportByPayloadAsync(
        int playerId, string reason, CancellationToken cancellationToken)
    {
        if (!_lotReconcile.TryGetPendingMerge(playerId, out LotExportBundle bundle))
        {
            return Task.FromResult((Merged: false, NewVersion: 0L));
        }

        bool hasDeepMergeState = _deepMergeFallback.TryGetValue(playerId, out DeepMergeFallbackReason fallback)
            && fallback != DeepMergeFallbackReason.None;
        return bundle.DeepExport == null && !hasDeepMergeState
            ? TryMergePendingLotExportAsync(playerId, reason, cancellationToken)
            : TryDeepMergePendingLotExportAsync(playerId, reason, cancellationToken);
    }

    /// <summary>
    /// slice 5 (design §3.2): record a server-side mutation of a lot-resident
    /// field. Called when the server TS3 mutates a lot-resident entity (SP touch, townie-on-lot
    /// interaction, economy fallback). The coordinator uses these ticks to classify contested
    /// fields at the next reconcile trigger.
    /// </summary>
    public void RecordServerLotFieldMutation(int playerId, ulong lotId, LotReconcileFieldKind field, long tick)
    {
        _serverFieldTicks.Record(playerId, lotId, field, tick);
    }

    /// <summary>expose the server field tick map for diagnostics + unit-test assertions.</summary>
    public LotReconcileFieldTickMap ServerFieldTicksForTests => _serverFieldTicks;

    /// <summary>expose the reconcile tracker for diagnostics + unit-test assertions.</summary>
    public LotReconcileTracker LotReconcileForTests => _lotReconcile;

    public enum DeepMergeFallbackReason
    {
        None = 0,
        ReloadUnsafeLiveImportQuarantined = 1,
        ValidationRejected = 2,
        DeepPayloadMissingQuarantined = 3,
        TravelMergeQuarantined = 4,
    }

    // per-player fallback reason from the last deep merge attempt.
    private readonly Dictionary<int, DeepMergeFallbackReason> _deepMergeFallback = new();

    /// <summary>
    /// record a pending lot export with deep state for transactional merge.
    /// The export is stored in the existing LotReconcileTracker; the deep payload rides on the
    /// bundle's <see cref="LotExportBundle.DeepExport"/> field.
    /// </summary>
    public void RecordPendingDeepExport(int playerId, LotExportBundle bundle, DateTimeOffset nowUtc, long clientLastReconciledVersion)
    {
        _lotReconcile.Record(playerId, bundle, nowUtc, clientLastReconciledVersion);
    }

    /// <summary>
    /// the fallback reason from the last deep merge attempt for a player.
    /// Used by tests and diagnostics. A non-None value means the export remains pending.
    /// </summary>
    public DeepMergeFallbackReason GetLastDeepMergeFallbackReason(int playerId)
    {
        return _deepMergeFallback.TryGetValue(playerId, out DeepMergeFallbackReason reason)
            ? reason : DeepMergeFallbackReason.None;
    }

    public async Task<(bool Merged, long NewVersion)> TryDeepMergePendingLotExportAsync(
        int playerId, string reason, CancellationToken cancellationToken)
    {
        if (!_lotReconcile.TryGetPendingMerge(playerId, out LotExportBundle bundle))
        {
            return (Merged: false, NewVersion: 0L);
        }

        if (bundle.DeepExport == null)
        {
            _deepMergeFallback[playerId] = DeepMergeFallbackReason.DeepPayloadMissingQuarantined;
            Console.WriteLine(
                $"[reconcile] deep merge QUARANTINED player={playerId} lot={bundle.LotId} "
                + $"reason=deep_payload_missing trigger={reason} pending=true");
            return (Merged: false, NewVersion: 0L);
        }

        HouseholdExportRejectionReason validation = HouseholdExportValidator.Validate(bundle.DeepExport);
        if (validation != HouseholdExportRejectionReason.Accepted)
        {
            _deepMergeFallback[playerId] = DeepMergeFallbackReason.ValidationRejected;
            Console.WriteLine($"[reconcile] deep export REJECTED player={playerId} lot={bundle.LotId} reason={validation}");
            return (Merged: false, NewVersion: 0L);
        }

        // if the payload carries inventory segments, route to the travel-merge path.
        // Otherwise stay quarantined (the v1 property-stream-only path is reload-unsafe).
        bool hasTravelPayload = bundle.DeepExport.SimInventories is { Count: > 0 }
            || bundle.DeepExport.SharedFridgeInventory != null
            || bundle.DeepExport.SharedFamilyInventory != null;

        if (!hasTravelPayload)
        {
            _deepMergeFallback[playerId] = DeepMergeFallbackReason.ReloadUnsafeLiveImportQuarantined;
            Console.WriteLine(
                $"[reconcile] deep merge QUARANTINED player={playerId} lot={bundle.LotId} "
                + $"reason=reload_unsafe_live_import trigger={reason} pending=true");
            return (Merged: false, NewVersion: 0L);
        }

        // travel-merge path. Check if the flag is set.
        string flagPath = System.IO.Path.Combine(".omo", "run", "dev205-enable-travel-merge.flag");
        bool flagEnabled = System.IO.File.Exists(flagPath);

        if (!flagEnabled)
        {
            _deepMergeFallback[playerId] = DeepMergeFallbackReason.TravelMergeQuarantined;
            Console.WriteLine(
                $"[reconcile] travel merge QUARANTINED player={playerId} lot={bundle.LotId} "
                + $"reason=travel_merge_gate trigger={reason} pending=true");
            return (Merged: false, NewVersion: 0L);
        }

        // flag is set -- invoke the travel-merge host method.
        try
        {
            HouseholdImportResult result = await _host.TravelMergeHouseholdAsync(bundle.DeepExport, cancellationToken);
            if (result.Success)
            {
                _deepMergeFallback[playerId] = DeepMergeFallbackReason.None;
                Console.WriteLine(
                    $"[reconcile] travel merge OK player={playerId} lot={bundle.LotId} "
                    + $"entries={result.EntryCount} trigger={reason}");
                _lotReconcile.MarkMerged(playerId);
                long travelNewVersion = _lotReconcile.BumpVersion(playerId, bundle.LotId);
                return (true, travelNewVersion);
            }
            else
            {
                _deepMergeFallback[playerId] = DeepMergeFallbackReason.TravelMergeQuarantined;
                Console.WriteLine(
                    $"[reconcile] travel merge FAILED player={playerId} lot={bundle.LotId} "
                    + $"reason={result.FailureReason} trigger={reason} pending=true");
                return (false, 0L);
            }
        }
        catch (Exception ex)
        {
            _deepMergeFallback[playerId] = DeepMergeFallbackReason.TravelMergeQuarantined;
            Console.WriteLine(
                $"[reconcile] travel merge EXCEPTION player={playerId} lot={bundle.LotId} "
                + $"error={ex.GetType().Name}:{ex.Message} trigger={reason} pending=true");
            return (false, 0L);
        }
    }

    /// <summary>
    /// slice 6: LotExportRequested pushes for joined players in a client-native mode
    /// without a fresh reconciled export (debounced per LotReconcilePolicy.ExportRequestInterval).
    /// Called by SyncBroadcaster each tick; the listener sends each event to its player.
    /// </summary>
    public async Task<IReadOnlyList<ServerEvent.LotExportRequested>> CollectLotExportRequestsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // PR#15 review fix 2: _lotReconcile.ShouldRequestExport mutates the debounce clock
        // (_lastExportRequestUtc) and is read/written under _commandGate by SubmitLotExport. Guard
        // this broadcaster-loop pass with the same gate. Broadcaster-only caller => no re-entrancy.
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            // when checkpoint is suspended (flag file), no export requests
            // are pushed. This lets the safety-net triggers fire with a PENDING unmerged export.
            if (IsCheckpointSuspendedByFlag())
            {
                return Array.Empty<ServerEvent.LotExportRequested>();
            }

            List<ServerEvent.LotExportRequested> requests = new();
            foreach (int playerId in _players.Keys)
            {
                if (!LotLocalAuthorityMirrorGate.OwnsNativeSimulation(GetStableLotLocalAuthorityMode(playerId)))
                {
                    continue;
                }

                if (!_lotReconcile.ShouldRequestExport(
                        playerId,
                        now,
                        LotReconcilePolicy.ExportFreshnessWindow,
                        LotReconcilePolicy.ExportRequestInterval))
                {
                    continue;
                }

                requests.Add(new ServerEvent.LotExportRequested(playerId, TryGetClaimedLotForPlayer(playerId), "checkpoint_freshness"));
            }

            return requests;
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private ulong? TryGetClaimedLotForPlayer(int playerId)
    {
        IReadOnlyList<SimSnapshot> sims = _interestLotIndexSnapshot?.Sims ?? Array.Empty<SimSnapshot>();
        foreach (KeyValuePair<ulong, int> claim in _claims)
        {
            if (claim.Value != playerId)
            {
                continue;
            }

            for (int i = 0; i < sims.Count; i++)
            {
                if (sims[i].ObjectId == claim.Key && sims[i].LotId is ulong lotId)
                {
                    return lotId;
                }
            }
        }

        return null;
    }

    private LotLocalAuthorityMode DetectLotLocalAuthorityModeCore(int playerId)
    {
        if (!_clientNativeModes)
        {
            return LotLocalAuthorityMode.FullPuppetFallback;
        }

        IReadOnlyList<SimSnapshot> sims = _interestLotIndexSnapshot?.Sims ?? Array.Empty<SimSnapshot>();
        LotLocalAuthorityContext context = new(
            JoinedPlayerCount: _players.Count,
            PlayerId: playerId,
            Claims: _claims,
            Sims: sims);
        return LotLocalAuthorityDetector.Detect(context);
    }

    /// <summary>
    /// interest scope + owned-Sim strip for solo_native / prox_mirror mirror broadcast.
    /// </summary>
    public SimulationSnapshot ApplyMirrorScopeForPlayer(int playerId, SimulationSnapshot snapshot)
    {
        snapshot = ApplyInterestScopeForPlayer(playerId, snapshot);
        LotLocalAuthorityMode mode = DetectLotLocalAuthorityModeForPlayer(playerId);
        if (!LotLocalAuthorityMirrorGate.ShouldStripOwnedSimsFromMirrorScope(mode))
        {
            return snapshot;
        }

        HashSet<ulong> owned = LotLocalAuthorityMirrorGate.OwnedSimIdsForPlayer(playerId, _claims);
        return LotLocalAuthorityMirrorGate.StripOwnedSimsFromSnapshot(snapshot, owned);
    }

    public bool ShouldBroadcastInteractionForPlayer(int playerId, ServerEvent.InteractionQueued interaction)
    {
        HashSet<ulong> owned = LotLocalAuthorityMirrorGate.OwnedSimIdsForPlayer(playerId, _claims);
        if (LotLocalAuthorityMirrorGate.ShouldSuppressOwnedMirror(
                DetectLotLocalAuthorityModeForPlayer(playerId),
                owned,
                interaction.ActorObjectId))
        {
            return false;
        }

        if (!_interestScopedMirroring || !MirroringInterestScope.ShouldFilter(_claims))
        {
            return true;
        }

        IReadOnlyList<SimSnapshot> sims = _interestLotIndexSnapshot?.Sims ?? Array.Empty<SimSnapshot>();
        HashSet<ulong> lots = MirroringInterestScope.ComputeInterestLots(_claims, sims);
        HashSet<ulong> claimed = MirroringInterestScope.ClaimedSimIds(_claims);
        Dictionary<ulong, ulong> simLot = MirroringInterestScope.BuildSimLotIndex(sims);
        return MirroringInterestScope.ShouldMirrorInteractionQueued(interaction, lots, claimed, simLot);
    }

    public bool ShouldBroadcastInteractionReplayForPlayer(int playerId, ServerEvent.InteractionReplayQueued replay)
    {
        InteractionReplayBundle bundle = replay.Bundle;
        ServerEvent.InteractionQueued proxy = new(
            bundle.ActorObjectId,
            bundle.InteractionId,
            bundle.InteractionKey,
            bundle.TargetObjectId,
            bundle.TargetPosition);
        return ShouldBroadcastInteractionForPlayer(playerId, proxy);
    }

    /// <summary>P5.6: wire the broadcaster AFTER construction so AckSnapshot can clear its latch (LP-5).</summary>
    public void SetSyncBroadcaster(SyncBroadcaster? broadcaster) => _syncBroadcaster = broadcaster;

    public long SyncWatermarkTick => _syncWatermarkTick;

    /// <summary>P5.4: read-only view of per-player snapshot ack watermarks (tests + sync lag).</summary>
    public IReadOnlyDictionary<int, long> PlayerAckTicks => _playerAckTicks;

    /// <summary>read authoritative SimClock from server DIAG_STATE (inWorld only).</summary>
    public async Task<WorldTime?> TryGetLiveWorldTimeAsync(CancellationToken cancellationToken)
    {
        if (_host is not HeadlessSims3Host headless)
        {
            return null;
        }

        string report = await headless.DiagStateAsync(cancellationToken);
        return WorldTimeReconcilePolicy.TryParseDiagClock(report, out WorldTime time) ? time : null;
    }

    /// <summary>
    /// periodic mirror correction. Server TS3 is authoritative; client TS3 processes
    /// advance SimClock independently, so re-broadcast WorldTimeChanged when drift exceeds threshold.
    /// </summary>
    public async Task MaybeBroadcastWorldTimeReconcileAsync(
        ClientListenerServer listener,
        CancellationToken cancellationToken)
    {
        if (!HasJoinedPlayers)
        {
            return;
        }

        WorldTime? authoritative = await TryGetLiveWorldTimeAsync(cancellationToken);
        if (authoritative is not WorldTime wt)
        {
            return;
        }

        TimeSpan wallSince = DateTimeOffset.UtcNow - _lastWorldTimeReconcileUtc;
        if (!WorldTimeReconcilePolicy.ShouldReconcile(wt, _lastReconciledWorldTime, wallSince))
        {
            return;
        }

        float driftHours = _lastReconciledWorldTime is WorldTime last
            ? WorldTimeReconcilePolicy.GameHoursDrift(wt, last)
            : 0f;
        _currentWorldTime = wt;
        _lastReconciledWorldTime = wt;
        _lastWorldTimeReconcileUtc = DateTimeOffset.UtcNow;
        await listener.BroadcastEventAsync(new ServerEvent.WorldTimeChanged(wt), cancellationToken);
        Console.WriteLine($"[sync] world_time reconcile day={wt.Day} {wt.Hours:D2}:{wt.Minutes:D2} drift={driftHours:F2}h");
        Console.Out.Flush();
    }

    public AuthoritativeSession(IAuthoritativeSimulationHost host)
    {
        _host = host;
    }

    /// <summary>Read-only view of the current per-Sim claim map (for tests + P3.2 ownership gates).</summary>
    public IReadOnlyDictionary<ulong, int> Claims => _claims;

    /// <summary>the player who made the user pick with this command id, once (the entry is removed).</summary>
    public bool TryTakePickRequester(Guid commandId, out int playerId)
    {
        lock (_pickRequesterLock)
        {
            return _pickRequesters.Remove(commandId, out playerId);
        }
    }

    private void RememberPickRequester(Guid commandId, int playerId)
    {
        lock (_pickRequesterLock)
        {
            if (!_pickRequesters.TryAdd(commandId, playerId))
            {
                return;
            }

            _pickRequesterOrder.Enqueue(commandId);
            while (_pickRequesterOrder.Count > MaxPickRequesters)
            {
                _pickRequesters.Remove(_pickRequesterOrder.Dequeue());
            }
        }
    }

    /// <summary>M4 P4.1.2: read-only view of per-player declared roles.</summary>
    public IReadOnlyDictionary<int, PlayerRole> PlayerRoles => _playerRoles;

    /// <summary>M4 P4.1.2: returns the declared role for a player, or PlayerRole.Server when undeclared / unknown.</summary>
    public PlayerRole GetPlayerRole(int playerId)
    {
        return _playerRoles.TryGetValue(playerId, out PlayerRole role) ? role : PlayerRole.Server;
    }

    // the joiner's game and the server's must walk routes the same way (OnScreenWalk). One line per join, a
    // WARNING when they differ, and then the joiner's SessionJoined carries the notice. It only warns: a game without
    // SimLink (any TS3 build but the Steam 1.67.2.024037 one, where the bridge carries everything) is a supported
    // client, SIMLINK_ONSCREEN=0 is a documented A/B switch, and a difference changes how the copies walk, not the
    // server's state.
    private string? CheckOnScreenWalk(ClientCommand.JoinSession join)
    {
        long step = JoinTrace.Now();
        string server = OnScreenOverride
            ?? SimsOnline.DedicatedServer.HostBridge.OnScreenWalk.ReadProcess((_host as HeadlessSims3Host)?.HostProcessId);
        string? client = SimsOnline.DedicatedServer.HostBridge.OnScreenWalk.Normalize(join.OnScreen);
        string? notice = SimsOnline.DedicatedServer.HostBridge.OnScreenWalk.Mismatch(server, client);
        string states = $"server={server} client={client ?? "unreported"}";
        JoinTrace.Current?.Step("onscreen", step, states);
        Console.WriteLine(notice == null
            ? $"[onscreen] player {join.PlayerId} joined: {states}"
            : $"[onscreen] WARNING: player {join.PlayerId} ({join.DisplayName}) joined with a different on-screen walk: "
                + $"{states}. Their copies will not walk as the server's Sims do.");
        return notice;
    }

    public async Task StartAsync(string saveName, CancellationToken cancellationToken, bool attachExistingHost = false)
    {
        await _host.StartAsync(cancellationToken);
        if (attachExistingHost && _host is HeadlessSims3Host headless)
        {
            string diagReport = await headless.DiagStateAsync(cancellationToken);
            if (SyncBroadcaster.TryParseDiagInWorld(diagReport)
                && SyncBroadcaster.TryParseDiagTick(diagReport, out long tick)
                && tick > 0)
            {
                Console.WriteLine("Re-attach: mod already in-world; skipping LOAD_SAVE.");
                Console.Out.Flush();
                return;
            }
        }

        await _host.LoadSaveAsync(saveName, cancellationToken);
        if (_host is HeadlessSims3Host loaded)
        {
            await WaitUntilInWorldAsync(loaded, cancellationToken);
        }
    }

    // The mod answers LOAD_SAVE when the load finishes, and GameStates.IsInWorld() can still be false for a moment
    // after: a client's world request then got "SAVE_GAME requires an in-world session" and the boot failed (2026-10-21,
    // once the mod's idle bridge poll went to 50 ms; at 250 the next round trip used to cover it). So the session
    // starts, and takes clients, only once DIAG says in-world.
    private static readonly TimeSpan InWorldWait = TimeSpan.FromSeconds(60);

    private static async Task WaitUntilInWorldAsync(HeadlessSims3Host host, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        while (!SyncBroadcaster.TryParseDiagInWorld(await host.DiagStateAsync(cancellationToken)))
        {
            if (DateTime.UtcNow - started > InWorldWait)
            {
                Console.WriteLine($"Session start: the host is still not in-world {InWorldWait.TotalSeconds:F0} s after its load; going on.");
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        TimeSpan waited = DateTime.UtcNow - started;
        if (waited > TimeSpan.FromMilliseconds(150))
        {
            Console.WriteLine($"Session start: in-world {waited.TotalMilliseconds:F0} ms after the load's answer.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _host.StopAsync(cancellationToken);
    }

    public Task SaveGameAsync(string saveName, CancellationToken cancellationToken)
    {
        return _host.SaveGameAsync(saveName, cancellationToken);
    }

    public async Task<CommandResult> HandleCommandAsync(
        ClientCommand command,
        CancellationToken cancellationToken,
        Guid? connectionLease = null)
    {
        long gateWait = JoinTrace.Now();
        await _commandGate.WaitAsync(cancellationToken);
        JoinTrace.Current?.Step("command-gate", gateWait);
        try
        {
        // Supersede guard: only connections that previously joined a session are checked.
        // A never-joined lease is an admin connection and is accepted as before the guard.
        if (connectionLease is Guid lease
            && command is not ClientCommand.JoinSession
            && _joinedConnectionLeases.Contains(lease)
            && (!_players.TryGetValue(command.PlayerId, out PlayerSession? currentSession)
                || currentSession.ConnectionLease != lease))
        {
            return CommandResult.Rejected(command.CommandId, "This connection has been superseded by a newer session.");
        }

        switch (command)
        {
            case ClientCommand.JoinSession join:
            {
                bool replacingExistingConnection = _players.ContainsKey(join.PlayerId);
                _players[join.PlayerId] = new PlayerSession(join.PlayerId, join.DisplayName, connectionLease);
                SetActiveConnectionLease(join.PlayerId, connectionLease);
                if (connectionLease is Guid joinedLease)
                {
                    // Mark this connection as joined so the supersede guard applies to it
                    // from now on (deliberately never removed: a joined connection that
                    // later leaves must not regress to trusted-admin status).
                    _joinedConnectionLeases.Add(joinedLease);
                }
                InvalidateMotiveAuthorityState(expediteNextProbe: replacingExistingConnection);
                // the joiner has none of the streamed animation; it alone gets every
                // target's history on the next presentation tick.
                lock (_presentationJoiners)
                {
                    _presentationJoiners.Add(join.PlayerId);
                }

                // part 2: join-time baseline = current speed (part 1) + authoritative
                // snapshot so a late joiner's mirror world-syncs, not just clock-syncs. The
                // snapshot is reply-only (not broadcast); speed replay remains broadcast-class
                // and idempotent for already-synced clients.
                List<ServerEvent> events = new()
                {
                    new ServerEvent.SessionJoined(
                        join.PlayerId,
                        join.DisplayName,
                        CheckOnScreenWalk(join),
                        PresentationStreamEnabled ? new[] { ServerCapability.PresentationStream } : null)
                };

                if (_currentSpeed is { } speed)
                {
                    events.Add(new ServerEvent.GameSpeedChanged(speed));
                }

                if (_currentWorldTime is { } worldTime)
                {
                    events.Add(new ServerEvent.WorldTimeChanged(worldTime));
                }

                // G14: each step of the join's gather is timed into the join's trace.
                JoinTrace? trace = JoinTrace.Current;
                long step = JoinTrace.Now();
                SimulationSnapshot captured = await _host.GetSnapshotAsync(cancellationToken);
                trace?.Step("snapshot-capture", step, string.Create(CultureInfo.InvariantCulture,
                    $"sims={captured.Sims.Count} objects={captured.Objects.Count} tick={captured.Tick}"));
                step = JoinTrace.Now();
                SimulationSnapshot snapshot = DecorateOwnership(captured);
                trace?.Step("decorate-ownership", step);

                // detect speed state invalid when snapshot reports a different speed than commanded.
                if (_currentSpeed is { } commandedSpeed && snapshot.Speed != commandedSpeed)
                {
                    SimsOnlineTelemetry.Current?.EmitBridgeSpeedStateInvalid(commandedSpeed.ToString(), snapshot.Speed.ToString());
                }
                _syncWatermarkTick = snapshot.Tick;
                step = JoinTrace.Now();
                UpdateInterestLotIndex(snapshot);
                trace?.Step("interest-lot-index", step);
                step = JoinTrace.Now();
                snapshot = ApplyMirrorScopeForPlayer(join.PlayerId, snapshot);
                trace?.Step("mirror-scope", step, string.Create(CultureInfo.InvariantCulture,
                    $"sims={snapshot.Sims.Count} objects={snapshot.Objects.Count}"));
                events.Add(new ServerEvent.FullSnapshot(snapshot));
                step = JoinTrace.Now();
                int modeChanges = 0;
                foreach (ServerEvent.LotLocalAuthorityModeChanged modeChanged in RefreshLotLocalAuthorityModesCore(DateTimeOffset.UtcNow))
                {
                    modeChanges++;
                    if (modeChanged.PlayerId == join.PlayerId)
                    {
                        events.Add(modeChanged);
                    }
                }
                trace?.Step("lot-modes", step, string.Create(CultureInfo.InvariantCulture, $"changes={modeChanges}"));

                return CommandResult.Accepted(command.CommandId, events.ToArray());
            }

            case ClientCommand.SetGameSpeed setSpeed:
            {
                CommandResult result = await _host.ApplyCommandAsync(command, cancellationToken);
                if (result.IsAccepted)
                {
                    _currentSpeed = setSpeed.Speed;
                }
                return result;
            }

            case ClientCommand.SetWorldTime setWorldTime:
            {
                if (!_players.ContainsKey(setWorldTime.PlayerId))
                {
                    return CommandResult.Rejected(command.CommandId, "Player is not joined to this session.");
                }

                if (GetPlayerRole(setWorldTime.PlayerId) == PlayerRole.Client)
                {
                    return CommandResult.Rejected(command.CommandId, "Only server-role players may set world time.");
                }

                if (!TryValidateWorldTime(setWorldTime.Time, out string? timeError))
                {
                    return CommandResult.Rejected(command.CommandId, timeError!);
                }

                CommandResult result = await _host.ApplyCommandAsync(command, cancellationToken);
                if (result.IsAccepted)
                {
                    _currentWorldTime = setWorldTime.Time;
                    _lastReconciledWorldTime = setWorldTime.Time;
                    _lastWorldTimeReconcileUtc = DateTimeOffset.UtcNow;
                }

                return result;
            }

            case ClientCommand.LeaveSession leave:
            {
                // slice 6 trigger 3: best-effort lot reconcile before the claims are
                // released. The canonical save must survive the client process exit; a merge
                // failure must never block the leave itself (the TCP disconnect cleanup path
                // synthesizes this same command).
                if (LotLocalAuthorityMirrorGate.OwnsNativeSimulation(GetStableLotLocalAuthorityMode(leave.PlayerId))
                    || LotLocalAuthorityMirrorGate.OwnsNativeSimulation(DetectLotLocalAuthorityModeForPlayer(leave.PlayerId)))
                {
                    // Best-effort leave merge; no ack event (the client is disconnecting and
                    // will re-handshake on rejoin, picking up the server's current version).
                    await TryMergePendingLotExportByPayloadAsync(leave.PlayerId, "leave_session", cancellationToken);
                }

                if (!HasQuarantinedPendingExport(leave.PlayerId))
                {
                    _lotReconcile.Clear(leave.PlayerId);
                    _serverFieldTicks.Clear(leave.PlayerId);
                    _deepMergeFallback.Remove(leave.PlayerId);
                }
                _pendingModeExitReconciles.Remove(leave.PlayerId);

                // P3.1: leaving the session releases every Sim claim this player held; the
                // SimReleased events are returned alongside the CommandAccepted so the listener
                // can broadcast them to all clients (other players need to know the Sim is free).
                // P4.1.2: also clear any declared role state. Future SetClientRole
                // after a rejoin starts fresh from the default Server role.
                List<ServerEvent> events = new();
                await ReleaseAllClaimsForAsync(leave.PlayerId, events, cancellationToken);
                _playerRoles.Remove(leave.PlayerId);
                _playerAckTicks.Remove(leave.PlayerId);
                _lotLocalAuthorityHysteresis.Remove(leave.PlayerId);
                _lotLocalAuthorityStable.Remove(leave.PlayerId);
                _players.Remove(leave.PlayerId);
                RemoveActiveConnectionLease(leave.PlayerId, connectionLease);
                InvalidateMotiveAuthorityState();
                return CommandResult.Accepted(command.CommandId, events.ToArray());
            }

            case ClientCommand.ClaimSim claim:
                return await HandleClaimSimAsync(claim, cancellationToken);

            case ClientCommand.ReleaseSim release:
                return await HandleReleaseSimAsync(release, cancellationToken);

            case ClientCommand.SetClientRole setRole:
                return HandleSetClientRole(setRole);

            case ClientCommand.SaveGame save:
            {
                if (!_players.ContainsKey(save.PlayerId))
                {
                    return CommandResult.Rejected(command.CommandId, "Player is not joined to this session.");
                }

                // slice 6 trigger 1: the server file is only canonical if every
                // client-native player's owned lot was reconciled first. Checkpointing without
                // the merge is the "owned state lost on reload" FAIL class, so
                // the default policy rejects; AllowWithDataLossRisk is the documented opt-out.
                if (_saveReconcilePolicy == LotReconcileSavePolicy.RejectWhenUnreconciled)
                {
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    List<int> unreconciled = new();
                    List<ServerEvent.LotReconcileAcknowledged> checkpointAcks = new();
                    foreach (int playerId in _players.Keys)
                    {
                        if (!LotLocalAuthorityMirrorGate.OwnsNativeSimulation(DetectLotLocalAuthorityModeForPlayer(playerId)))
                        {
                            continue;
                        }

                        // A stored-but-unmerged export can still be merged inline before the save.
                        if (_lotReconcile.TryGetPendingMerge(playerId, out LotExportBundle? checkpointBundle))
                        {
                            // inline checkpoint merge + emit LotReconcileAcknowledged so
                            // the owning client learns the new version (not just the saver). The
                            // ack is broadcast (IsBroadcastEvent) and the client filters by PlayerId.
                            ulong lotId = checkpointBundle.LotId;
                            (bool merged, long newVersion) = await TryMergePendingLotExportByPayloadAsync(playerId, "checkpoint", cancellationToken);
                            if (merged && newVersion > 0)
                            {
                                checkpointAcks.Add(new ServerEvent.LotReconcileAcknowledged(playerId, lotId, newVersion));
                            }
                        }

                        if (!_lotReconcile.IsReconciledForCheckpoint(playerId, now, LotReconcilePolicy.ExportFreshnessWindow))
                        {
                            unreconciled.Add(playerId);
                        }
                    }

                    if (unreconciled.Count > 0)
                    {
                        // even on rejection, the inline merges that DID succeed already
                        // called BumpVersion on the server. The acks for those merges must reach
                        // the owning clients so their s_lastReconciledVersion stays current —
                        // otherwise the next SubmitLotExport carries a stale version and forces a
                        // permanent false version-mismatch. The listener broadcasts
                        // LotReconcileAcknowledged (IsBroadcastEvent) from result.Events even on
                        // a rejected reply.
                        ServerEvent[] rejectEvents = new ServerEvent[checkpointAcks.Count];
                        for (int i = 0; i < checkpointAcks.Count; i++)
                        {
                            rejectEvents[i] = checkpointAcks[i];
                        }
                        return CommandResult.Rejected(
                            command.CommandId,
                            $"Save checkpoint blocked: lot reconcile pending for client-native player(s) {string.Join(", ", unreconciled)}. Submit a lot export (submit_lot_export) and retry.",
                            rejectEvents);
                    }

                    await _host.SaveGameAsync(save.SaveName, cancellationToken);
                    ServerEvent[] checkpointEvents = new ServerEvent[1 + checkpointAcks.Count];
                    checkpointEvents[0] = new ServerEvent.SaveCheckpointed(save.SaveName, DateTimeOffset.UtcNow);
                    for (int i = 0; i < checkpointAcks.Count; i++)
                    {
                        checkpointEvents[i + 1] = checkpointAcks[i];
                    }
                    return CommandResult.Accepted(command.CommandId, checkpointEvents);
                }

                await _host.SaveGameAsync(save.SaveName, cancellationToken);
                return CommandResult.Accepted(command.CommandId, new ServerEvent.SaveCheckpointed(save.SaveName, DateTimeOffset.UtcNow));
            }

            case ClientCommand.SubmitLotExport export:
            {
                if (!_players.ContainsKey(export.PlayerId))
                {
                    return CommandResult.Rejected(command.CommandId, "Player is not joined to this session.");
                }

                // A lot export merges a client's lot into the server's game. Only a client that simulates its own
                // lot (client-native lot modes) has anything to reconcile; with one simulator (the default) the
                // server's game is the only truth, so no client may write into it this way.
                if (!_clientNativeModes)
                {
                    return CommandResult.Rejected(
                        command.CommandId,
                        "Lot exports are only accepted with client-native lot modes on (SIMSONLINE_CLIENT_NATIVE_MODES=1); every player is a presenter.");
                }

                // slice 6: class B protection — strip anything the player does not own
                // or that is off the declared lot before the bundle can touch the server TS3.
                LotExportBundle declared = export.Bundle with { PlayerId = export.PlayerId };
                LotExportBundle filtered = LotExportValidator.FilterToOwnedLot(
                    declared,
                    _claims,
                    _interestLotIndexSnapshot?.Sims ?? Array.Empty<SimSnapshot>());
                if (filtered.Sims.Count == 0)
                {
                    return CommandResult.Rejected(
                        command.CommandId,
                        "Lot export rejected: no Sims owned by this player on the declared lot.");
                }

                bool serverRequestedDeepExport = _lotReconcile.ConsumeOutstandingExportRequest(export.PlayerId);
                _deepMergeFallback.Remove(export.PlayerId);
                _lotReconcile.Record(export.PlayerId, filtered, DateTimeOffset.UtcNow, export.ClientLastReconciledVersion);
                (bool merged, long newVersion) = serverRequestedDeepExport
                    ? await TryDeepMergePendingLotExportAsync(export.PlayerId, "requested_export_submitted", cancellationToken)
                    : await TryMergePendingLotExportByPayloadAsync(export.PlayerId, "export_submitted", cancellationToken);
                if (!merged)
                {
                    string failureReason = GetLastDeepMergeFallbackReason(export.PlayerId) switch
                    {
                        DeepMergeFallbackReason.ReloadUnsafeLiveImportQuarantined =>
                            "Lot export quarantined: live household import is reload-unsafe; the deep export remains pending.",
                        DeepMergeFallbackReason.DeepPayloadMissingQuarantined =>
                            "Lot export quarantined: required deep household state is missing; the export remains pending.",
                        DeepMergeFallbackReason.ValidationRejected =>
                            "Lot export rejected: deep household payload validation failed.",
                        DeepMergeFallbackReason.TravelMergeQuarantined =>
                            "Lot export quarantined: travel-merge gate not yet passed; the deep export remains pending.",
                        _ => "Lot export stored but the server-side merge failed; it remains pending.",
                    };
                    return CommandResult.Rejected(
                        command.CommandId,
                        failureReason);
                }

                // slice 5 (design §3.1): ack the merge with the new monotonic
                // version so the client echoes it back in its next SubmitLotExport. This closes
                // the P2 gap where ClientLastReconciledVersion was always 0 in production, making
                // versionMismatch permanently true after the first merge and breaking the
                // conflict detector's fast path.
                return CommandResult.Accepted(command.CommandId,
                    new ServerEvent.LotReconcileAcknowledged(export.PlayerId, filtered.LotId, newVersion));
            }

            case ClientCommand.AckSnapshot ack:
            {
                if (!_players.ContainsKey(ack.PlayerId))
                {
                    return CommandResult.Rejected(ack.CommandId, "Player is not joined to this session.");
                }

                // P5.6 LP-5: clear the resync-in-flight latch BEFORE RecordSnapshotAck.
                // RecordSnapshotAck early-returns on stale ticks (lines below), so a backlogged
                // client draining old deltas after a ForceFull would never reach a latch-clear
                // site inside RecordSnapshotAck. We clear here unconditionally when the ack
                // tick >= the resync epoch tick, then let RecordSnapshotAck handle monotonicity.
                if (_syncBroadcaster is { } broadcaster && broadcaster.IsResyncInFlightForPlayer(ack.PlayerId))
                {
                    long resyncFrom = broadcaster.GetLastResyncFromTickForPlayer(ack.PlayerId);
                    if (resyncFrom >= 0 && ack.Tick >= resyncFrom)
                    {
                        broadcaster.ClearResyncLatchForPlayer(ack.PlayerId);
                    }
                }

                RecordSnapshotAck(ack.PlayerId, ack.Tick);
                return CommandResult.Accepted(ack.CommandId);
            }

            case ClientCommand.RequestWorld:
            {
                // A joining client's world: the server's save, checkpointed now, to the asking connection only.
                WorldSave? world = await _host.CheckpointWorldAsync(cancellationToken);
                if (world == null)
                {
                    return CommandResult.Rejected(command.CommandId, "The server has no world save to send.");
                }

                return CommandResult.Accepted(command.CommandId, world.ToChunks().ToArray<ServerEvent>());
            }

            case ClientCommand.RequestSimDescription request:
            {
                // any joined player may ask; descriptions carry nothing secret. The reply goes to the
                // sender only (not a broadcast event).
                SimDescriptionPayload? payload = await _host.ExportSimDescriptionAsync(request.SimDescriptionId, cancellationToken);
                if (payload == null)
                {
                    return CommandResult.Rejected(command.CommandId, $"Sim description {request.SimDescriptionId} not found.");
                }

                return CommandResult.Accepted(
                    command.CommandId,
                    new ServerEvent.SimDescriptionData(payload.SimDescriptionId, payload.PropertyStream, payload.ResKeyTable, payload.ObjectIdTable));
            }

            case ClientCommand.SetActiveSim setActive:
            {
                CommandResult? gate = GateOwnedActorCommand(setActive.PlayerId, setActive.SimObjectId, command.CommandId);
                if (gate != null)
                {
                    return gate;
                }

                return await _host.ApplyCommandAsync(command, cancellationToken);
            }

            case ClientCommand.AnswerDialog answer:
            {
                // only the Sim's owner answers its dialogs.
                CommandResult? gate = GateOwnedActorCommand(answer.PlayerId, answer.SimObjectId, command.CommandId);
                if (gate != null)
                {
                    return gate;
                }

                return await _host.ApplyCommandAsync(command, cancellationToken);
            }

            case ClientCommand.CancelInteraction cancel:
            {
                CommandResult? gate = GateOwnedActorCommand(cancel.PlayerId, cancel.ActorObjectId, command.CommandId);
                if (gate != null)
                {
                    return gate;
                }

                return await _host.ApplyCommandAsync(command, cancellationToken);
            }

            case ClientCommand.LeaveConversation leave:
            {
                // n14: only the Sim's owner leaves its conversation.
                CommandResult? gate = GateOwnedActorCommand(leave.PlayerId, leave.ActorObjectId, command.CommandId);
                if (gate != null)
                {
                    return gate;
                }

                return await _host.ApplyCommandAsync(command, cancellationToken);
            }

            case ClientCommand.QueueInteraction queue:
            {
                // P3.2: cross-ownership rejection. A player can only queue interactions on an
                // actor they own, or on an unclaimed actor. Cross-ownership attempts are
                // rejected before reaching the host (the host has no concept of multiplayer
                // ownership; the session is the authoritative gate).
                if (!_players.ContainsKey(queue.PlayerId))
                {
                    return CommandResult.Rejected(command.CommandId, "Player is not joined to this session.");
                }

                if (_claims.TryGetValue(queue.Interaction.ActorObjectId, out int actorOwner) && actorOwner != queue.PlayerId)
                {
                    return CommandResult.Rejected(
                        command.CommandId,
                        $"Sim 0x{queue.Interaction.ActorObjectId:X16} is claimed by player {actorOwner}; only the owner can queue interactions on it.");
                }

                CommandResult queued = await _host.ApplyCommandAsync(command, cancellationToken);
                if (queued.IsAccepted && UserPick.IsUserPick(queue.Interaction))
                {
                    RememberPickRequester(command.CommandId, queue.PlayerId);
                }

                return queued;
            }

            case ClientCommand.RequestSnapshot reqSnap:
            {
                ///UT-1: snapshots served over the public protocol must carry claim
                // ownership too (simctl status / ut.py position_moved read this path, not
                // GetSnapshotAsync). Decorate the snapshot inside the event envelope.
                //
                // P5.6: server-arbitrated routing. If a ForceFull is already in flight,
                // the client does not need to request another one -- drop the request so
                // the broadcaster's pending ForceFull ack-clears the latch. A client
                // RequestSnapshot during a normal delta cadence is served as-is (the
                // MirrorCoordinator debounces so at most one per cooldown arrives).
                if (_syncBroadcaster is { } b && b.IsResyncInFlightForPlayer(reqSnap.PlayerId))
                {
                    // Server ForceFull in flight for this player: suppress duplicate request (LP-6).
                    return CommandResult.Accepted(reqSnap.CommandId);
                }

                CommandResult result = await _host.ApplyCommandAsync(command, cancellationToken);
                if (!result.IsAccepted || _claims.Count == 0)
                {
                    return result;
                }
                ServerEvent[] events = result.Events.Select(e => e switch
                {
                    ServerEvent.FullSnapshot full => new ServerEvent.FullSnapshot(
                        ApplyInterestScopeForPlayer(reqSnap.PlayerId, DecorateOwnership(full.Snapshot))),
                    ServerEvent.SnapshotDelta delta => new ServerEvent.SnapshotDelta(
                        ApplyInterestScopeForPlayer(reqSnap.PlayerId, DecorateOwnership(delta.Snapshot))),
                    _ => e
                }).ToArray();
                return CommandResult.Accepted(result.CommandId, events);
            }

            default:
                return await _host.ApplyCommandAsync(command, cancellationToken);
        }
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public bool HasJoinedPlayers => _players.Count > 0;

    internal bool IsActiveConnectionLease(int playerId, Guid connectionLease)
    {
        lock (_connectionLeaseLock)
        {
            return _activeConnectionLeases.TryGetValue(playerId, out Guid activeLease)
                && activeLease == connectionLease;
        }
    }

    /// <summary>
    /// periodically probe authoritative motive/fund/skill values and emit change events.
    /// Called from the broadcast cycle. Returns events to include in the broadcast.
    /// </summary>
    public Task<List<ServerEvent>> ProbeMotiveFundsSkillsAsync(CancellationToken cancellationToken) =>
        ProbeMotiveFundsSkillsCoreAsync(enqueueBatchAsync: null, cancellationToken);

    /// <summary>
    /// Production publication seam. Final authority validation and synchronous batch enqueue share
    /// the command gate; socket delivery drains outside it. Watermarks commit only after successful
    /// delivery and a second same-generation authority check.
    /// </summary>
    public async Task PublishMotiveFundsSkillsAsync(
        Func<IReadOnlyList<ServerEvent>, CancellationToken, Task<bool>> enqueueBatchAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enqueueBatchAsync);
        _ = await ProbeMotiveFundsSkillsCoreAsync(enqueueBatchAsync, cancellationToken);
    }

    private async Task<List<ServerEvent>> ProbeMotiveFundsSkillsCoreAsync(
        Func<IReadOnlyList<ServerEvent>, CancellationToken, Task<bool>>? enqueueBatchAsync,
        CancellationToken cancellationToken)
    {
        List<ServerEvent> events = new();
        List<(ulong SimId, int PlayerId)> candidates = new();
        long authorityGeneration;
        List<((ulong SimId, string Motive) Key, float Value)> changedValues = new();
        Task<bool>? deliveryTask = null;
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            _motiveProbeCounter++;
            if (_motiveProbeCounter < MotiveProbeInterval)
            {
                return events;
            }
            _motiveProbeCounter = 0;

            authorityGeneration = _motiveAuthorityGeneration;
            foreach ((ulong simId, int playerId) in _claims)
            {
                if (HasFullPuppetMotiveAuthority(simId, playerId))
                {
                    candidates.Add((simId, playerId));
                }
            }
        }
        finally
        {
            _commandGate.Release();
        }

        List<(ulong SimId, string Motive, float Value)> probedValues = new();
        foreach ((ulong simId, int playerId) in candidates)
        {
            foreach (string motive in TrackedMotives)
            {
                cancellationToken.ThrowIfCancellationRequested();
                float? value = await _host.ProbeSimMotiveAsync(simId, motive, cancellationToken);
                await _commandGate.WaitAsync(cancellationToken);
                try
                {
                    if (authorityGeneration != _motiveAuthorityGeneration
                        || !HasFullPuppetMotiveAuthority(simId, playerId))
                    {
                        return events;
                    }
                }
                finally
                {
                    _commandGate.Release();
                }

                if (value.HasValue)
                {
                    probedValues.Add((simId, motive, value.Value));
                }
            }
        }

        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            if (authorityGeneration != _motiveAuthorityGeneration
                || candidates.Any(candidate => !HasFullPuppetMotiveAuthority(candidate.SimId, candidate.PlayerId)))
            {
                return events;
            }

            foreach ((ulong simId, string motive, float value) in probedValues)
            {
                var key = (simId, motive);
                if (!_lastKnownMotives.TryGetValue(key, out float prev)
                    || Math.Abs(prev - value) > 0.5f)
                {
                    changedValues.Add((key, value));
                    events.Add(new ServerEvent.SimMotiveChanged(simId, motive, value));
                }
            }

            if (enqueueBatchAsync != null && events.Count > 0)
            {
                // The enqueue callback is synchronous up to returning its completion task. Calling
                // it under the command gate establishes outbound ordering before any later claim or
                // mode event can be enqueued. Socket delivery itself drains after this gate releases.
                deliveryTask = enqueueBatchAsync(events, cancellationToken);
            }
            else
            {
                foreach (((ulong SimId, string Motive) key, float value) in changedValues)
                {
                    _lastKnownMotives[key] = value;
                }
            }
        }
        finally
        {
            _commandGate.Release();
        }

        if (deliveryTask == null)
        {
            return events;
        }

        bool delivered = await deliveryTask;
        if (!delivered)
        {
            return events;
        }

        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            if (authorityGeneration != _motiveAuthorityGeneration
                || candidates.Any(candidate => !HasFullPuppetMotiveAuthority(candidate.SimId, candidate.PlayerId)))
            {
                return events;
            }

            foreach (((ulong SimId, string Motive) key, float value) in changedValues)
            {
                _lastKnownMotives[key] = value;
            }

            return events;
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private bool HasFullPuppetMotiveAuthority(ulong simId, int playerId) =>
        _claims.TryGetValue(simId, out int currentOwner)
        && currentOwner == playerId
        && GetStableLotLocalAuthorityMode(playerId) == LotLocalAuthorityMode.FullPuppetFallback
        && DetectLotLocalAuthorityModeCore(playerId) == LotLocalAuthorityMode.FullPuppetFallback;

    private void InvalidateMotiveAuthorityState(bool expediteNextProbe = false)
    {
        unchecked
        {
            _motiveAuthorityGeneration++;
        }
        _lastKnownMotives.Clear();
        if (expediteNextProbe)
        {
            _motiveProbeCounter = MotiveProbeInterval - 1;
        }
    }

    public async Task<SimulationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        return DecorateOwnership(await _host.GetSnapshotAsync(cancellationToken));
    }

    public async Task<SimulationSnapshot> GetSnapshotSinceAsync(long sinceTick, CancellationToken cancellationToken)
    {
        return DecorateOwnership(await _host.GetSnapshotSinceAsync(sinceTick, cancellationToken));
    }

    /// <summary>P5.4 / one joined player's ack tick when that player has ack'd at least once.</summary>
    public bool TryGetPlayerAckTick(int playerId, out long ackTick)
    {
        ackTick = -1;
        if (!_players.ContainsKey(playerId))
        {
            return false;
        }

        return _playerAckTicks.TryGetValue(playerId, out ackTick);
    }

    /// <summary>
    /// P5.4: slowest joined player's ack tick when every joined player has ack'd at least once.
    /// </summary>
    public bool TryGetMinPlayerAckTick(out long minAck)
    {
        minAck = -1;
        if (_players.Count == 0)
        {
            return false;
        }

        minAck = long.MaxValue;
        foreach (int playerId in _players.Keys)
        {
            if (!_playerAckTicks.TryGetValue(playerId, out long ack))
            {
                return false;
            }

            if (ack < minAck)
            {
                minAck = ack;
            }
        }

        return true;
    }

    /// <summary>P5.4: monotonic per-player ack; stale/out-of-order acks are ignored.</summary>
    public void RecordSnapshotAck(int playerId, long tick)
    {
        if (_playerAckTicks.TryGetValue(playerId, out long existing) && tick <= existing)
        {
            return;
        }

        _playerAckTicks[playerId] = tick;
    }

    /// <summary>
    ////UT-1: the mod can't know claims (controller state), so the wire snapshot's
    /// owner slot is always "-". The session is the ownership authority, so it decorates
    /// every snapshot it hands out — both the direct GetSnapshotAsync path (probes,
    /// --snapshot-and-exit) and the public request_snapshot path (simctl, ut.py). The client
    /// mirror ignores this field (its self-owned logic is event-driven via sim_claimed), so
    /// this is purely additive.
    /// </summary>
    private SimulationSnapshot DecorateOwnership(SimulationSnapshot snapshot)
    {
        if (_claims.Count == 0)
        {
            return snapshot;
        }

        List<SimSnapshot> sims = new(snapshot.Sims.Count);
        foreach (SimSnapshot sim in snapshot.Sims)
        {
            sims.Add(_claims.TryGetValue(sim.ObjectId, out int owner)
                ? sim with { OwningPlayerId = owner }
                : sim);
        }
        return snapshot with { Sims = sims };
    }

    /// <summary>
    /// P3.1: called by the listener when a connection disconnects without sending a
    /// LeaveSession (network drop, client crash, etc.). Returns the SimReleased events plus
    /// the synthetic LeaveSession acceptance so the listener can broadcast them. Returns null
    /// when the player was never joined on this session (no-op).
    /// </summary>
    public async Task<CommandResult?> ReleasePlayerClaimsAndLeaveAsync(int playerId, CancellationToken cancellationToken)
    {
        return await ReleasePlayerClaimsAndLeaveCoreAsync(playerId, expectedConnectionLease: null, cancellationToken);
    }

    public async Task<CommandResult?> ReleasePlayerClaimsAndLeaveAsync(
        int playerId,
        Guid expectedConnectionLease,
        CancellationToken cancellationToken)
    {
        return await ReleasePlayerClaimsAndLeaveCoreAsync(playerId, expectedConnectionLease, cancellationToken);
    }

    private async Task<CommandResult?> ReleasePlayerClaimsAndLeaveCoreAsync(
        int playerId,
        Guid? expectedConnectionLease,
        CancellationToken cancellationToken)
    {
        List<ServerEvent> events = new();
        List<ulong> releasedSimIds;
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            if (!_players.TryGetValue(playerId, out PlayerSession? currentSession)
                || (expectedConnectionLease.HasValue
                    && currentSession.ConnectionLease != expectedConnectionLease))
            {
                return null;
            }

            // clear the reconcile + server field-tick state for this player, matching the
            // explicit LeaveSession handler. Without this, stale per-field server mutation ticks
            // persist for the player id; if the same id rejoins, BuildMergePlan reads the stale ticks
            // and may classify fields as ServerWins/Contested based on pre-disconnect mutations.
            if (!HasQuarantinedPendingExport(playerId))
            {
                _lotReconcile.Clear(playerId);
                _serverFieldTicks.Clear(playerId);
                _deepMergeFallback.Remove(playerId);
            }
            _pendingModeExitReconciles.Remove(playerId);

            releasedSimIds = RemoveAllClaimsFor(playerId, events);
            _playerRoles.Remove(playerId);
            _playerAckTicks.Remove(playerId);
            _lotLocalAuthorityHysteresis.Remove(playerId);
            _lotLocalAuthorityStable.Remove(playerId);
            _players.Remove(playerId);
            RemoveActiveConnectionLease(playerId, expectedConnectionLease);
            InvalidateMotiveAuthorityState();
        }
        finally
        {
            _commandGate.Release();
        }

        foreach (ulong simObjectId in releasedSimIds)
        {
            await TryClearAutonomyGateAsync(simObjectId, cancellationToken);
        }

        return CommandResult.Accepted(Guid.NewGuid(), events.ToArray());
    }

    private bool HasQuarantinedPendingExport(int playerId)
    {
        if (!_lotReconcile.TryGetPendingMerge(playerId, out _)
            || !_deepMergeFallback.TryGetValue(playerId, out DeepMergeFallbackReason reason))
        {
            return false;
        }

        return reason is DeepMergeFallbackReason.ReloadUnsafeLiveImportQuarantined
            or DeepMergeFallbackReason.DeepPayloadMissingQuarantined
            or DeepMergeFallbackReason.TravelMergeQuarantined;
    }

    private CommandResult? GateOwnedActorCommand(int playerId, ulong actorObjectId, Guid commandId)
    {
        if (!_players.ContainsKey(playerId))
        {
            return CommandResult.Rejected(commandId, "Player is not joined to this session.");
        }

        if (_claims.TryGetValue(actorObjectId, out int actorOwner) && actorOwner != playerId)
        {
            return CommandResult.Rejected(
                commandId,
                $"Sim 0x{actorObjectId:X16} is claimed by player {actorOwner}; only the owner can command it.");
        }

        return null;
    }

    private async Task<CommandResult> HandleClaimSimAsync(ClientCommand.ClaimSim claim, CancellationToken cancellationToken)
    {
        if (!_players.ContainsKey(claim.PlayerId))
        {
            return CommandResult.Rejected(claim.CommandId, "Player is not joined to this session.");
        }

        if (_claims.TryGetValue(claim.SimObjectId, out int existingOwner))
        {
            if (existingOwner == claim.PlayerId)
            {
                // Idempotent: same player re-claiming the same Sim is a no-op (no extra SimClaimed
                // broadcast, and crucially no second GATE_AUTONOMY — the mod's gate registry is
                // idempotent but the session keeps one register/clear pair per claim lifetime).
                return CommandResult.Accepted(claim.CommandId);
            }

            return CommandResult.Rejected(
                claim.CommandId,
                $"Sim 0x{claim.SimObjectId:X16} is already claimed by player {existingOwner}.");
        }

        _claims[claim.SimObjectId] = claim.PlayerId;
        InvalidateMotiveAuthorityState();

        // claiming a Sim no longer disables its autonomy. The Sim keeps free will until
        // the owning player issues a command. Instead we REGISTER the Sim for the command-lifecycle
        // gate on the authoritative TS3: the mod suppresses autonomy only while a UserDirected
        // interaction is in the Sim's queue and resumes it when that queue drains. This supersedes
        // the earlier claim-time blanket DISABLE_AUTONOMY (which stripped free will for the whole
        // claim). Best-effort: a bridge hiccup must not fail the claim — ownership is session state.
        await TryRegisterAutonomyGateAsync(claim.SimObjectId, cancellationToken);

        // M4 P4.2.6: emit SimAutonomyChanged(disabled=true) so client mirrors suppress
        // autonomy on the puppet of the newly-claimed Sim. Order matters: SimClaimed FIRST so the
        // owning client's MirrorEventFilter sees the updated ownership set BEFORE evaluating the
        // autonomy event (which it then skips per the self-id rule from P4.2.5).
        // scope note: this event still drives the CLIENT-MIRROR puppet, which is
        // unchanged here — the gate governs only the server-authority TS3. Faithful mirroring of a
        // claimed Sim's live autonomy/animation is's scope; keeping this event keeps mirror
        // behavior identical to today (no regression introduced by).
        // lot id on claim lets client detect solo_native before owned Sim is stripped from deltas.
        ulong? claimedLotId = TryGetSimLotFromInterestIndex(claim.SimObjectId);
        if (claimedLotId is null)
        {
            try
            {
                SimulationSnapshot fullSnap = await GetSnapshotSinceAsync(-1L, cancellationToken);
                UpdateInterestLotIndex(fullSnap);
                claimedLotId = TryGetSimLotFromInterestIndex(claim.SimObjectId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Best-effort: claim succeeds even if lot lookup fails.
            }
        }

        return CommandResult.Accepted(claim.CommandId,
            new ServerEvent.SimClaimed(claim.PlayerId, claim.SimObjectId, claimedLotId),
            new ServerEvent.SimAutonomyChanged(claim.SimObjectId, Disabled: true));
    }

    private ulong? TryGetSimLotFromInterestIndex(ulong simObjectId)
    {
        IReadOnlyList<SimSnapshot> sims = _interestLotIndexSnapshot?.Sims ?? Array.Empty<SimSnapshot>();
        for (int i = 0; i < sims.Count; i++)
        {
            SimSnapshot sim = sims[i];
            if (sim.ObjectId == simObjectId && sim.LotId is ulong lotId)
            {
                return lotId;
            }
        }

        return null;
    }

    private async Task TrySetServerAutonomyAsync(ulong simObjectId, bool disabled, CancellationToken cancellationToken)
    {
        try
        {
            await _host.SetServerAutonomyAsync(simObjectId, disabled, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Best-effort by contract (see IAuthoritativeSimulationHost.SetServerAutonomyAsync):
            // claim/release ownership state must not depend on the bridge round-trip succeeding.
            // The mod surfaces its own ERROR line in the server log when a Sim cannot be resolved.
        }
    }

    // best-effort gate register/clear, same contract as TrySetServerAutonomyAsync — the
    // claim/release ownership decision must never depend on the bridge round-trip succeeding.
    private async Task TryRegisterAutonomyGateAsync(ulong simObjectId, CancellationToken cancellationToken)
    {
        try
        {
            await _host.RegisterAutonomyGateAsync(simObjectId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Best-effort: see TrySetServerAutonomyAsync.
        }
    }

    private async Task TryClearAutonomyGateAsync(ulong simObjectId, CancellationToken cancellationToken)
    {
        try
        {
            await _host.ClearAutonomyGateAsync(simObjectId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Best-effort: see TrySetServerAutonomyAsync.
        }
    }

    // M4 P4.1.2: per-player role declaration. Requires JoinSession.
    // Idempotent: re-setting the same role is accepted but emits no second event.
    private CommandResult HandleSetClientRole(ClientCommand.SetClientRole setRole)
    {
        if (!_players.ContainsKey(setRole.PlayerId))
        {
            return CommandResult.Rejected(setRole.CommandId, "Player is not joined to this session.");
        }

        // Idempotent: only treat as no-op when an EXPLICIT existing entry matches the
        // requested role. A first-time SetClientRole always writes the entry + emits the
        // event, even if the requested role equals the default lookup result (Server),
        // so observers can distinguish "explicitly Server" from "undeclared".
        if (_playerRoles.TryGetValue(setRole.PlayerId, out PlayerRole existing) && existing == setRole.Role)
        {
            return CommandResult.Accepted(setRole.CommandId);
        }

        _playerRoles[setRole.PlayerId] = setRole.Role;
        return CommandResult.Accepted(setRole.CommandId, new ServerEvent.ClientRoleChanged(setRole.PlayerId, setRole.Role));
    }

    private async Task<CommandResult> HandleReleaseSimAsync(ClientCommand.ReleaseSim release, CancellationToken cancellationToken)
    {
        if (!_players.ContainsKey(release.PlayerId))
        {
            return CommandResult.Rejected(release.CommandId, "Player is not joined to this session.");
        }

        if (!_claims.TryGetValue(release.SimObjectId, out int owner) || owner != release.PlayerId)
        {
            return CommandResult.Rejected(
                release.CommandId,
                $"Sim 0x{release.SimObjectId:X16} is not claimed by player {release.PlayerId}.");
        }

        _claims.Remove(release.SimObjectId);
        InvalidateMotiveAuthorityState();

        // clear the command-lifecycle gate (best-effort, see claim path). The mod untracks
        // the Sim and unwinds any outstanding mid-command suppression so autonomy is never left
        // disabled past the claim. Replaces's ENABLE_AUTONOMY on release.
        await TryClearAutonomyGateAsync(release.SimObjectId, cancellationToken);

        // M4 P4.2.6: emit SimAutonomyChanged(disabled=false) so all clients re-enable
        // autonomy on the now-unowned Sim. Order matters: SimReleased FIRST so the previously-owning
        // client's MirrorEventFilter no longer treats the Sim as self-owned by the time the autonomy
        // event arrives, so the un-suppress IS applied to their local puppet too.
        return CommandResult.Accepted(release.CommandId,
            new ServerEvent.SimReleased(release.PlayerId, release.SimObjectId),
            new ServerEvent.SimAutonomyChanged(release.SimObjectId, Disabled: false));
    }

    private async Task ReleaseAllClaimsForAsync(int playerId, List<ServerEvent> events, CancellationToken cancellationToken)
    {
        List<ulong> toRelease = RemoveAllClaimsFor(playerId, events);
        foreach (ulong simObjectId in toRelease)
        {
            // clear each released Sim's command-lifecycle gate (best-effort). Covers
            // LeaveSession and the dropped-connection path (ReleasePlayerClaimsAndLeaveAsync).
            await TryClearAutonomyGateAsync(simObjectId, cancellationToken);
        }
    }

    private List<ulong> RemoveAllClaimsFor(int playerId, List<ServerEvent> events)
    {
        List<ulong> toRelease = new();
        foreach (KeyValuePair<ulong, int> kvp in _claims)
        {
            if (kvp.Value == playerId)
            {
                toRelease.Add(kvp.Key);
            }
        }

        foreach (ulong simObjectId in toRelease)
        {
            _claims.Remove(simObjectId);
            events.Add(new ServerEvent.SimReleased(playerId, simObjectId));
            // M4 P4.2.6: pair each SimReleased with SimAutonomyChanged(disabled=false) so
            // the un-suppress is broadcast alongside the release for LeaveSession + dropped-connection
            // paths. Interleaved per-Sim (pair grouping is consecutive; inter-Sim order follows
            // Dictionary iteration which is undefined - tests assert per-pair invariants only).
            events.Add(new ServerEvent.SimAutonomyChanged(simObjectId, Disabled: false));
        }

        return toRelease;
    }

    private static bool TryValidateWorldTime(WorldTime time, out string? error)
    {
        if (time.Day is < 0 or > 6)
        {
            error = "World time day must be 0..6 (Sunday..Saturday).";
            return false;
        }

        if (time.Hours is < 0 or > 23)
        {
            error = "World time hours must be 0..23.";
            return false;
        }

        if (time.Minutes is < 0 or > 59)
        {
            error = "World time minutes must be 0..59.";
            return false;
        }

        error = null;
        return true;
    }

    private void SetActiveConnectionLease(int playerId, Guid? connectionLease)
    {
        lock (_connectionLeaseLock)
        {
            if (connectionLease is Guid lease)
            {
                _activeConnectionLeases[playerId] = lease;
            }
            else
            {
                _activeConnectionLeases.Remove(playerId);
            }
        }
    }

    private void RemoveActiveConnectionLease(int playerId, Guid? expectedConnectionLease)
    {
        lock (_connectionLeaseLock)
        {
            if (!expectedConnectionLease.HasValue
                || (_activeConnectionLeases.TryGetValue(playerId, out Guid activeLease)
                    && activeLease == expectedConnectionLease.Value))
            {
                _activeConnectionLeases.Remove(playerId);
            }
        }
    }

    private sealed record PlayerSession(int PlayerId, string DisplayName, Guid? ConnectionLease);
}
