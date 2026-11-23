using System.Globalization;
using System.Text.RegularExpressions;
using SimsOnline.DedicatedServer.HostBridge;
using SimsOnline.DedicatedServer.Observability;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Mirroring;

/// <summary>
/// M4 P4.2.2.1 — translates incoming <see cref="ServerEvent"/>s into
/// <see cref="BridgeCommand"/>s via <see cref="EventToBridgeCommandTranslator"/>
/// and dispatches each command to an <see cref="IMirrorHost"/> (in production a
/// client-role <c>HeadlessSims3Host</c>).
///
/// Per-command error isolation: an <see cref="InvalidOperationException"/> from
/// the host (e.g. <c>APPLY_POSITION rejected: ...</c>) does NOT abort the remaining
/// commands produced from the same event. The failure is recorded in
/// <see cref="DispatchResult.Errors"/> so callers can log / surface drift.
/// <see cref="OperationCanceledException"/> tied to the caller's cancellation
/// token propagates immediately and aborts the remaining commands.
///
/// wired the event-driven interaction-replay path: a target-bearing
/// <see cref="ServerEvent.InteractionQueued"/> already translates to
/// <see cref="BridgeCommand.ApplyInteractionQueue"/> (dispatched by this engine's switch).
/// adds client-mirror autonomy suppression: for each mirrored Sim in a snapshot the
/// engine emits a first-sight <see cref="BridgeCommand.ApplyAutonomy"/> (disabled=true) so the
/// client's local AI stops fighting the authoritative position stream — see
/// <see cref="MirrorAutonomyController"/> for the policy and the deliberate deferral of
/// snapshot-driven interaction replay.
/// </summary>
public sealed class MirrorEngine
{
    private readonly IMirrorHost _host;
    private readonly Action<string>? _log;
    private readonly TimeSpan _terminalReplayRetryDelay;
    private GameSpeed? _lastMirroredSnapshotSpeed;

    // Speed sync (cold-test-078): GameSpeedChanged is the server's speed now; a snapshot's speed is the server's speed
    // when the snapshot was taken, and snapshots arrive seconds late. After a player pressed Normal, a snapshot taken
    // while the server ran Triple set every client back to Triple. So a snapshot's speed applies only on a full
    // snapshot (join, resync) or when no GameSpeedChanged arrived for SnapshotSpeedFallbackAfter.
    private DateTime _lastSpeedEventUtc = DateTime.MinValue;

    // STOPGAP(boundary-recording): snapshot-speed-fallback a 30 s window stands in for ordering; order a snapshot's speed against speed events by server tick.
    /// <summary>How long without a GameSpeedChanged before a snapshot's speed is trusted again.</summary>
    public static readonly TimeSpan SnapshotSpeedFallbackAfter = TimeSpan.FromSeconds(30);
    // client-mirror autonomy suppression for mirrored Sims (first-sight deduped). The
    // engine owns it because suppression state must persist across snapshots, exactly like
    // _lastMirroredSnapshotSpeed. Self-owned Sims never reach here — MirrorEventFilter.FilterSnapshotSims
    // strips them upstream — so every Sim in a snapshot the engine builds commands for is mirrored.
    private readonly MirrorAutonomyController _autonomy = new();
    private readonly SimAliasTracker _aliases = new();
    private readonly TimeSpan _posturePollInterval;
    // W2: false once the client authority gate enforces on the local TS3 (forked AuthorityHook):
    // a mirrored Sim can then only queue what the server sends, so suppression is redundant.
    private bool _autonomySuppressionEnabled = true;
    private readonly TimeSpan _posturePollTimeout;

    public MirrorEngine(
        IMirrorHost host,
        Action<string>? log = null,
        TimeSpan? terminalReplayRetryDelay = null,
        TimeSpan? posturePollInterval = null,
        TimeSpan? posturePollTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _log = log;
        _terminalReplayRetryDelay = terminalReplayRetryDelay ?? TimeSpan.FromSeconds(1);
        _posturePollInterval = posturePollInterval ?? TimeSpan.FromMilliseconds(250);
        _posturePollTimeout = posturePollTimeout ?? SessionConfig.DefaultPlayerCommandTimeout;
    }

    public async Task<DispatchResult> DispatchAsync(ServerEvent serverEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // every InteractionReplayQueued gets an explicit policy decision + telemetered
        // degrade (route/slide/never) before translate — no silent unclassified drops.
        if (serverEvent is ServerEvent.InteractionReplayQueued replayQueued)
        {
            EmitReplayPolicyDecision(replayQueued.Bundle);
        }

        if (serverEvent is ServerEvent.SessionJoined joined
            && InteractionReplayEnabled
            && ServerCapability.Has(joined.Capabilities, ServerCapability.PresentationStream))
        {
            // the join says the server streams, so replay never starts. Waiting for the first sim_animation
            // left it on 5-14 s after a join, and the replayed interactions moved the player's selection.
            InteractionReplayEnabled = false;
            _log?.Invoke("MirrorEngine: the server streams presentation (session_joined); interaction replay off");
        }

        if (serverEvent is ServerEvent.SimAnimation && InteractionReplayEnabled)
        {
            // the server streams animation, so this client presents and stops replaying
            // interactions (one simulator: docs/decision-one-simulator.md).
            InteractionReplayEnabled = false;
            _log?.Invoke("MirrorEngine: presentation stream active; interaction replay off");
        }

        IReadOnlyList<BridgeCommand> commands = BuildCommands(serverEvent);
        if (!InteractionReplayEnabled)
        {
            commands = commands.Where(c => c is not (BridgeCommand.ApplyInteractionQueue or BridgeCommand.ApplyInteractions or BridgeCommand.ApplyForceAlign)).ToList();
        }

        if (commands.Count == 0)
        {
            return new DispatchResult(0, 0, 0, Array.Empty<Exception>());
        }

        int succeeded = 0;
        int failed = 0;
        List<Exception> errors = new();

        foreach (BridgeCommand command in commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DispatchOneAsync(command, cancellationToken);
                if (command is BridgeCommand.SetGameSpeed setSpeed)
                {
                    _lastMirroredSnapshotSpeed = setSpeed.Speed;
                }

                succeeded++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add(ex);
                _log?.Invoke($"MirrorEngine: {command.GetType().Name} failed: {ex.Message}");
                if (command is BridgeCommand.ApplyAutonomy aa
                    && ex.Message.Contains("Sim not found", StringComparison.OrdinalIgnoreCase))
                {
                    _autonomy.MarkAutonomyUnavailable(aa.SimObjectId);
                }

                if (command is BridgeCommand.ApplyInteractionQueue
                    && ex.Message.Contains("Sim not found", StringComparison.OrdinalIgnoreCase))
                {
                    // Transient: the target Sim is not yet loaded on the client (e.g. mid-lot-load).
                    // Remove from the error list so callers do not surface this to Sentry.
                    failed--;
                    errors.RemoveAt(errors.Count - 1);
                    succeeded++;
                }
            }
        }

        return new DispatchResult(commands.Count, succeeded, failed, errors);
    }

    /// <summary>
    /// dispatch a coalesced batch of interaction replays through one or more
    /// <see cref="BridgeCommand.ApplyInteractions"/> frames (or a single
    /// <see cref="BridgeCommand.ApplyInteractionQueue"/> when count == 1).
    /// book chains with a ReadBook terminal use <c>INJECT_READ_INTENT</c> instead.
    /// </summary>
    public async Task<DispatchResult> DispatchInteractionBatchAsync(
        IReadOnlyList<ServerEvent.InteractionQueued> interactions,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // this batch sends past DispatchAsync's filter, so it checks the switch itself.
        if (!InteractionReplayEnabled)
        {
            return new DispatchResult(0, 0, 0, Array.Empty<Exception>());
        }

        if (BookChainReadIntentResolver.TryResolveFromInteractions(interactions, out ulong actorObjectId, out ulong bookObjectId, out IReadOnlyDictionary<string, string> parameters))
        {
            return await DispatchReadIntentAsync(actorObjectId, bookObjectId, parameters, cancellationToken);
        }

        IReadOnlyList<BridgeCommand> commands = EventToBridgeCommandTranslator.TranslateInteractionBatch(interactions);
        if (commands.Count == 0)
        {
            return new DispatchResult(0, 0, 0, Array.Empty<Exception>());
        }

        int succeeded = 0;
        int failed = 0;
        List<Exception> errors = new();

        foreach (BridgeCommand command in commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DispatchOneAsync(command, cancellationToken);
                succeeded++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add(ex);
                _log?.Invoke($"MirrorEngine: {command.GetType().Name} failed: {ex.Message}");
            }
        }

        return new DispatchResult(commands.Count, succeeded, failed, errors);
    }

    /// <summary>
    /// dispatch an ordered batch of replay bundles: ForceAlign Sit first, then terminal
    /// replay requests (including GetBook precursors per PATH 2 — see getbook-replay.md).
    /// when the batch includes a ReadBook terminal, dispatch <c>INJECT_READ_INTENT</c>
    /// instead of ForceAlign + terminal replay.
    /// </summary>
    public async Task<DispatchResult> DispatchReplayBundleBatchAsync(
        IReadOnlyList<InteractionReplayBundle> bundles,
        CancellationToken cancellationToken)
    {
        if (bundles == null || bundles.Count == 0)
        {
            return new DispatchResult(0, 0, 0, Array.Empty<Exception>());
        }

        cancellationToken.ThrowIfCancellationRequested();

        // the batch sends its terminal and INJECT_READ_INTENT itself, past DispatchAsync's
        // filter, so it checks the switch here (and again before each terminal send). Otherwise a
        // batch reaching the engine after the stream started (a delayed ReadBook flush) would poll for
        // a seat the dropped ForceAlign never took, then replay the terminal anyway.
        if (!InteractionReplayEnabled)
        {
            return new DispatchResult(0, 0, 0, Array.Empty<Exception>());
        }

        if (BookChainReadIntentResolver.TryResolveFromBundles(bundles, out ulong actorObjectId, out ulong bookObjectId, out IReadOnlyDictionary<string, string> parameters))
        {
            return await DispatchReadIntentAsync(actorObjectId, bookObjectId, parameters, cancellationToken);
        }

        int translated = 0;
        int succeeded = 0;
        int failed = 0;
        List<Exception> errors = new();
        List<InteractionReplayBundle> forceAlignBundles = new();
        List<InteractionReplayBundle> terminalBundles = new();
        List<InteractionRequest> terminalRequests = new();

        foreach (InteractionReplayBundle bundle in bundles)
        {
            // route every bundle through the resolver decision table (no silent drop).
            EmitReplayPolicyDecision(bundle);

            if (EventToBridgeCommandTranslator.IsForceAlignCandidate(bundle))
            {
                // Keep the seat-normalized bundle: it is both what APPLY_FORCE_ALIGN carries and
                // the seat the posture poll waits on. A transition Sit captured while still
                // standing has the actor's own id as its container, which the Sim never sits in.
                forceAlignBundles.Add(EventToBridgeCommandTranslator.NormalizeForceAlignBundleForDispatch(bundle));
                continue;
            }

            if (EventToBridgeCommandTranslator.IsInteractionReplayCandidate(bundle))
            {
                terminalBundles.Add(bundle);
                continue;
            }

            // SlideOnly / NeverReplay / ForceAlign-without-seat: explicit no-op after telemetry above.
        }

        foreach (InteractionReplayBundle bundle in terminalBundles)
        {
            // ReplayAsRoute synthesizes Terrain.GoHere inside ToInteractionRequest.
            terminalRequests.Add(EventToBridgeCommandTranslator.ToInteractionRequest(bundle));
        }

        foreach (InteractionReplayBundle bundle in forceAlignBundles)
        {
            DispatchResult one = await DispatchAsync(new ServerEvent.InteractionReplayQueued(bundle), cancellationToken);
            translated += one.Translated;
            succeeded += one.Succeeded;
            failed += one.Failed;
            if (one.Errors.Count > 0)
            {
                errors.AddRange(one.Errors);
            }
        }

        if (terminalRequests.Count == 0)
        {
            return new DispatchResult(translated, succeeded, failed, errors);
        }

        // Step 0: poll DIAG_INTERACTION_STATE until seated in the dispatched ForceAlign seat
        // (zero wait when exact-seat SitDown already assigned posture on the bridge pump tick).
        if (forceAlignBundles.Count > 0)
        {
            InteractionReplayBundle lastForceAlign = forceAlignBundles[forceAlignBundles.Count - 1];
            await WaitForPostureSettleAsync(lastForceAlign, cancellationToken);
        }

        IReadOnlyList<BridgeCommand> terminalCommands =
            EventToBridgeCommandTranslator.TranslateInteractionRequestBatch(terminalRequests);

        foreach (BridgeCommand command in terminalCommands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!await DispatchTerminalReplayCommandAsync(command, cancellationToken))
                {
                    // Dropped like DispatchAsync drops a filtered command: not counted as translated.
                    continue;
                }

                translated++;
                succeeded++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                translated++;
                failed++;
                errors.Add(ex);
                _log?.Invoke($"MirrorEngine: {command.GetType().Name} failed: {ex.Message}");
            }
        }

        return new DispatchResult(translated, succeeded, failed, errors);
    }

    /// <summary>
    /// surface the InteractionReplayPolicyResolver decision for every bundle.
    /// Route-degrade and SlideOnly/NeverReplay emit <c>replay.policy_degraded</c> telemetry so
    /// incomplete family coverage is observable instead of a silent drop or Sentry-suppressed wedge.
    /// </summary>
    private void EmitReplayPolicyDecision(InteractionReplayBundle bundle)
    {
        InteractionReplayDecision decision = InteractionReplayPolicyResolver.Resolve(bundle);
        bool emitDegrade = decision.Policy switch
        {
            ReplayPolicy.SlideOnly => true,
            ReplayPolicy.NeverReplay => true,
            ReplayPolicy.ReplayAsRoute => EventToBridgeCommandTranslator.IsRouteDegraded(bundle),
            _ => false
        };

        if (!emitDegrade)
        {
            return;
        }

        _log?.Invoke(
            $"MirrorEngine: replay policy degrade actor={bundle.ActorObjectId} key={bundle.InteractionKey} policy={decision.Policy} reason={decision.Reason}");
        SimsOnlineTelemetry.Current?.EmitReplayPolicyDegraded(
            bundle.ActorObjectId,
            bundle.InteractionKey,
            decision.Policy.ToString(),
            decision.Reason);
    }

    /// <summary>
    /// Poll until the actor sits in <paramref name="lastForceAlign"/>'s container. Pass the bundle as
    /// dispatched (seat-normalized by <see cref="EventToBridgeCommandTranslator.NormalizeForceAlignBundleForDispatch"/>),
    /// not the raw capture, or a Sit captured while standing waits on the actor's own id until the timeout.
    /// </summary>
    private async Task WaitForPostureSettleAsync(InteractionReplayBundle lastForceAlign, CancellationToken cancellationToken)
    {
        ulong? containerObjectId = lastForceAlign.PostureContainerObjectId;
        if (!containerObjectId.HasValue || containerObjectId.Value == 0uL)
        {
            return;
        }

        ulong actorObjectId = lastForceAlign.ActorObjectId;
        DateTime deadline = DateTime.UtcNow + _posturePollTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MirroredInteractionState? state = await _host.QueryInteractionStateAsync(actorObjectId, cancellationToken);
            if (state == null)
            {
                return;
            }

            if (state.IsSeatedInContainer(containerObjectId.Value))
            {
                _log?.Invoke($"MirrorEngine: posture poll seated actor={actorObjectId} container={containerObjectId.Value}");
                return;
            }

            try
            {
                await Task.Delay(_posturePollInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        _log?.Invoke($"MirrorEngine: posture poll timed out after {_posturePollTimeout.TotalMilliseconds:F0}ms actor={actorObjectId} container={containerObjectId.Value}; proceeding to terminal dispatch");
    }

    private async Task<DispatchResult> DispatchReadIntentAsync(
        ulong actorObjectId,
        ulong bookObjectId,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _host.InjectReadIntentAsync(actorObjectId, bookObjectId, "chooser", parameters, cancellationToken);
            _log?.Invoke($"MirrorEngine: book chain via INJECT_READ_INTENT actor={actorObjectId} book={bookObjectId}");
            return new DispatchResult(1, 1, 0, Array.Empty<Exception>());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsBookTargetNotFound(ex))
        {
            // 2nd mirror client's TS3 instance lacks the host-local book
            // ObjectId (and possibly any book in the Sim's inventory). Per ReplayPolicy
            // doctrine, degrade slide-only instead of hard-failing
            // mirror_dispatch_partial_failure. The Sim's position stream continues; only
            // the book-read interaction is dropped on this client.
            _log?.Invoke($"MirrorEngine: INJECT_READ_INTENT book target not found on client actor={actorObjectId} book={bookObjectId}; degrading slide-only: {ex.Message}");
            SimsOnlineTelemetry.Current?.EmitReplayPolicyDegraded(
                actorObjectId,
                "Sims3.Gameplay.Objects.ReadBook+Definition",
                ReplayPolicy.SlideOnly.ToString(),
                "book-target-not-found-on-client");
            return new DispatchResult(1, 1, 0, Array.Empty<Exception>());
        }
        catch (Exception ex)
        {
            _log?.Invoke($"MirrorEngine: INJECT_READ_INTENT failed: {ex.Message}");
            return new DispatchResult(1, 0, 1, new[] { ex });
        }
    }

    /// <summary>
    /// detect the mod-side rejection that fires when the host-local book
    /// ObjectId does not resolve on a 2nd mirror client (and inventory fallback also
    /// misses). The mod sends "Book target not found: ..." which HeadlessSims3Host
    /// wraps as "INJECT_READ_INTENT rejected: Book target not found: ...".
    /// </summary>
    private static bool IsBookTargetNotFound(Exception ex)
    {
        return ex is InvalidOperationException
            && ex.Message.Contains("Book target not found", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Send one terminal replay command, retrying a ReadBook terminal the host could not queue yet.
    /// Returns false, without sending, once interaction replay is off: the stream can start
    /// while the batch waits on the posture poll or a retry delay, and terminals bypass
    /// <see cref="DispatchAsync"/>'s filter, so the switch is read right before each send.
    /// </summary>
    private async Task<bool> DispatchTerminalReplayCommandAsync(BridgeCommand command, CancellationToken cancellationToken)
    {
        int maxRetries = 3;
        TimeSpan[] retryDelays = new TimeSpan[]
        {
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(6),
        };

        for (int attempt = 0; ; attempt++)
        {
            if (!InteractionReplayEnabled)
            {
                _log?.Invoke($"MirrorEngine: interaction replay off; terminal {command.GetType().Name} not sent");
                return false;
            }

            try
            {
                await DispatchOneAsync(command, cancellationToken);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ShouldRetryTerminalReplay(command, ex) && attempt < maxRetries)
            {
                TimeSpan delay = retryDelays[attempt];
                _log?.Invoke($"MirrorEngine: terminal replay failed (attempt {attempt + 1}/{maxRetries}), retrying after {delay.TotalSeconds:F0}s ForceAlign settle: {ex.Message}");
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static bool ShouldRetryTerminalReplay(BridgeCommand command, Exception ex)
    {
        return ex is not OperationCanceledException
            && command is BridgeCommand.ApplyInteractionQueue applyInteractionQueue
            && SimsOnlineServerHost.InteractionReplayApplySorter.IsReadBookTerminalHead(applyInteractionQueue.Interaction.InteractionKey)
            && ex.Message.Contains("Terminal interaction could not be queued", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// P5.5: snapshot events also carry authoritative speed; dedupe against the last mirrored
    /// speed so periodic deltas do not spam SET_GAME_SPEED when unchanged.
    /// </summary>
    private IReadOnlyList<BridgeCommand> BuildCommands(ServerEvent serverEvent)
    {
        SimulationSnapshot? snapshot = serverEvent switch
        {
            ServerEvent.FullSnapshot full => full.Snapshot,
            ServerEvent.SnapshotDelta delta => delta.Snapshot,
            _ => null
        };

        if (snapshot == null)
        {
            if (serverEvent is ServerEvent.GameSpeedChanged)
            {
                _lastSpeedEventUtc = DateTime.UtcNow;
            }

            if (serverEvent is ServerEvent.SimDespawned despawned)
            {
                // The mod drops the alias with the Sim; a later reuse of the id is sent again.
                _aliases.Forget(despawned.SimObjectId);
            }

            return EventToBridgeCommandTranslator.Translate(serverEvent);
        }

        // W2 Sim identity: aliases go first so every later command in this batch (and the next)
        // resolves Sims the client instantiated under a different ObjectId.
        List<BridgeCommand> commands = new(_aliases.OnSnapshot(snapshot.Sims, serverEvent is ServerEvent.FullSnapshot));
        if (_lastMirroredSnapshotSpeed != snapshot.Speed
            && (serverEvent is ServerEvent.FullSnapshot || DateTime.UtcNow - _lastSpeedEventUtc > SnapshotSpeedFallbackAfter))
        {
            commands.Add(new BridgeCommand.SetGameSpeed(Guid.NewGuid(), snapshot.Speed));
        }

        IReadOnlyList<BridgeCommand> translated = EventToBridgeCommandTranslator.Translate(serverEvent);
        if (translated.Count > 0)
        {
            commands.AddRange(translated);
        }

        // every Sim in this snapshot is a mirrored Sim (self-owned ones were stripped by
        // MirrorEventFilter.FilterSnapshotSims before dispatch). Suppress the client's local autonomy
        // for each on first sight so its local AI stops fighting the authoritative position stream;
        // first-sight dedupe keeps the per-snapshot cost flat (the mod's APPLY_AUTONOMY is idempotent).
        if (_autonomySuppressionEnabled)
        {
            IReadOnlyList<BridgeCommand> suppress = _autonomy.OnMirroredSnapshotSims(snapshot.Sims);
            if (suppress.Count > 0)
            {
                commands.AddRange(suppress);
            }
        }

        return commands;
    }

    public bool AutonomySuppressionEnabled => _autonomySuppressionEnabled;

    /// <summary>
    /// MVP 3: when false the engine drops interaction replay (APPLY_INTERACTION_QUEUE,
    /// APPLY_INTERACTIONS, APPLY_FORCE_ALIGN, and the batch methods' INJECT_READ_INTENT) and keeps
    /// everything else (positions, aliases, spawns, state), so a presentation replay spike can drive
    /// the client's Sims alone. Default true.
    /// </summary>
    public bool InteractionReplayEnabled { get; set; } = true;

    /// <summary>W2 Sim identity: server Sims whose description alias was sent to the client TS3.</summary>
    public int SimAliasCount => _aliases.Count;

    /// <summary>
    /// W2 step 3: turn first-sight autonomy suppression off when the client authority gate
    /// enforces (and back on when it does not, e.g. stock gameplay with no hook). Turning it off
    /// releases every suppression the engine holds (best-effort per Sim); turning it on lets the next
    /// snapshot re-suppress on first sight. Returns the number of release commands that failed.
    /// </summary>
    public async Task<int> SetAutonomySuppressionAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (_autonomySuppressionEnabled == enabled)
        {
            return 0;
        }

        _autonomySuppressionEnabled = enabled;
        if (enabled)
        {
            return 0;
        }

        int failed = 0;
        foreach (BridgeCommand command in _autonomy.ReleaseAll())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DispatchOneAsync(command, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                _log?.Invoke($"MirrorEngine: release suppression failed: {ex.Message}");
            }
        }

        return failed;
    }

    /// <summary>
    /// a Sim has left the client's mirrored set — this client claimed it (mirrored ->
    /// self-owned) or a claim was released. If the engine had suppressed its autonomy, dispatch the
    /// balancing <c>ApplyAutonomy(disabled=false)</c> so no client-side suppression leaks onto a
    /// now-owned Sim, and clear the first-sight flag so a released Sim re-suppresses on its next delta
    /// sight. No-op (and no bridge round-trip) when the Sim was not suppressed.
    /// </summary>
    public async Task UnsuppressMirroredSimAsync(ulong simObjectId, CancellationToken cancellationToken)
    {
        BridgeCommand? command = _autonomy.OnSimNoLongerMirrored(simObjectId);
        if (command == null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await DispatchOneAsync(command, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"MirrorEngine: {command.GetType().Name} (un-suppress) failed: {ex.Message}");
        }
    }

    /// <summary>asks the server for a Sim description this client lacks (the client controller sends
    /// RequestSimDescription). Null: spawns of such Sims just log the gap.</summary>
    public Func<ulong, Task>? RequestSimDescription { get; set; }

    /// <summary>how long before the same missing description is asked for again.</summary>
    public static readonly TimeSpan SimDescriptionRequestInterval = TimeSpan.FromSeconds(60);

    // spawns waiting for their description (by description id), and when each was last requested.
    private readonly Dictionary<ulong, BridgeCommand.SpawnSim> _spawnsAwaitingDescription = new();
    private readonly Dictionary<ulong, DateTime> _descriptionRequestedAt = new();

    /// <summary>descriptions this client imported from the server.</summary>
    public int ImportedSimDescriptions { get; private set; }

    // W2 Sim identity step 3: a description this client does not have (created at runtime on the server)
    // cannot be spawned yet. ask the server for it once (per interval); the import retries the spawn.
    private async Task SpawnSimAsync(BridgeCommand.SpawnSim spawn, CancellationToken cancellationToken)
    {
        string? report = await _host.SpawnSimAsync(spawn.SimObjectId, spawn.SimDescriptionId, spawn.Position, cancellationToken);
        if (report != null && report.Contains("result=missing-description", StringComparison.Ordinal) && RequestSimDescription != null)
        {
            _spawnsAwaitingDescription[spawn.SimDescriptionId] = spawn;
            DateTime now = DateTime.UtcNow;
            if (_descriptionRequestedAt.TryGetValue(spawn.SimDescriptionId, out DateTime last) && now - last < SimDescriptionRequestInterval)
            {
                return;
            }

            _descriptionRequestedAt[spawn.SimDescriptionId] = now;
            _log?.Invoke($"MirrorEngine: SPAWN_SIM {spawn.SimObjectId} description={spawn.SimDescriptionId}: missing; requesting it from the server");
            await RequestSimDescription(spawn.SimDescriptionId);
            return;
        }

        if (report != null && !report.Contains("result=spawned", StringComparison.Ordinal) && !report.Contains("result=existed", StringComparison.Ordinal))
        {
            _log?.Invoke($"MirrorEngine: SPAWN_SIM {spawn.SimObjectId} description={spawn.SimDescriptionId}: {report}");
        }
    }

    /// <summary> step 5: APPLY_SIM_DETAILS calls that changed something on this client.</summary>
    public int SimDetailsApplied { get; private set; }

    // step 5: log what changed (a job, a level, skills) or why nothing could be applied.
    private async Task ApplySimDetailsAsync(BridgeCommand.ApplySimDetails apply, CancellationToken cancellationToken)
    {
        string? report = await _host.ApplySimDetailsAsync(apply.Details, cancellationToken);
        if (report != null && report.Contains("result=same", StringComparison.Ordinal))
        {
            return;
        }

        if (report != null && report.Contains("result=applied", StringComparison.Ordinal))
        {
            SimDetailsApplied++;
        }

        if (report != null)
        {
            _log?.Invoke($"MirrorEngine: APPLY_SIM_DETAILS {apply.Details.SimObjectId} ({apply.Details.ToWireEntry()}): {report}");
        }
    }

    // import the server's description, then retry the spawn that was waiting for it.
    private async Task ImportSimDescriptionAsync(BridgeCommand.ImportSimDescription import, CancellationToken cancellationToken)
    {
        ulong descriptionId = import.Payload.SimDescriptionId;
        string? report = await _host.ImportSimDescriptionAsync(import.Payload, cancellationToken);
        _log?.Invoke($"MirrorEngine: IMPORT_SIM_DESCRIPTION {descriptionId}: {report}");
        if (report != null && report.Contains("result=imported", StringComparison.Ordinal))
        {
            ImportedSimDescriptions++;
        }

        if (_spawnsAwaitingDescription.Remove(descriptionId, out BridgeCommand.SpawnSim? spawn))
        {
            await SpawnSimAsync(spawn, cancellationToken);
        }
    }

    // count replayed / skipped lines; log the mod's report when a line threw.
    private long _replayErrors;

    /// <summary>SACS_REPLAY batches sent to the client TS3.</summary>
    public long SacsReplayBatches { get; private set; }

    private async Task SacsReplayAsync(BridgeCommand.SacsReplay replay, CancellationToken cancellationToken)
    {
        // the client bridge round trip is a distinct latency stage.
        bool trace = Environment.GetEnvironmentVariable("SIMSONLINE_TRANSPORT_TRACE") == "1";
        long started = trace ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
        string? report = await _host.SacsReplayAsync(replay.Lines, cancellationToken);
        if (trace)
        {
            long finished = System.Diagnostics.Stopwatch.GetTimestamp();
            double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(started, finished).TotalMilliseconds;
            var span = PresentationStream.EventSequenceBounds(replay.Lines);
            _log?.Invoke(
                $"[transport-latency] stage=client_bridge_replay at_ms={Environment.TickCount64} " +
                $"start_qpc={started} at_qpc={finished} qpc_freq={System.Diagnostics.Stopwatch.Frequency} " +
                $"at_utc_ticks={DateTime.UtcNow.Ticks} " +
                $"first_seq={span.First} last_seq={span.Last} elapsed_ms={elapsedMs:F3} " +
                $"lines={replay.Lines.Count} bytes={System.Text.Encoding.UTF8.GetByteCount(string.Join("\n", replay.Lines))}");
            // The game-side report is stamped AFTER HandleSacsReplay executes,
            // before the engine's next animation frame. Older mods omit this field.
            Match appliedStamp = Regex.Match(report ?? string.Empty, @"(?:^|;)applyUtcTicks=(\d+)");
            if (appliedStamp.Success)
            {
                _log?.Invoke(
                    $"[transport-latency] stage=client_game_apply first_seq={span.First} last_seq={span.Last} " +
                    $"apply_utc_ticks={appliedStamp.Groups[1].Value} " +
                    $"at_utc_ticks={DateTime.UtcNow.Ticks} at_qpc={finished} " +
                    $"qpc_freq={System.Diagnostics.Stopwatch.Frequency}");
            }
        }
        SacsReplayBatches++;
        Match errors = report == null ? Match.Empty : Regex.Match(report, @"errors=(\d+)");
        if (errors.Success && long.Parse(errors.Groups[1].Value, CultureInfo.InvariantCulture) > _replayErrors)
        {
            _replayErrors = long.Parse(errors.Groups[1].Value, CultureInfo.InvariantCulture);
            _log?.Invoke($"MirrorEngine: SACS_REPLAY {report}");
        }
    }

    private Task DispatchOneAsync(BridgeCommand command, CancellationToken cancellationToken) => command switch
    {
        BridgeCommand.ApplyPosition ap => _host.ApplyPositionAsync(ap.SimObjectId, ap.TargetPosition, cancellationToken),
        BridgeCommand.ApplyPositions aps => _host.ApplyPositionsAsync(aps.Positions, cancellationToken),
        BridgeCommand.ApplyMotive am => _host.ApplyMotiveAsync(am.SimObjectId, am.MotiveName, am.Value, cancellationToken),
        BridgeCommand.ApplySimState ass => _host.ApplySimStateAsync(ass.State, cancellationToken),
        BridgeCommand.SetSimAliases ssa => _host.SetSimAliasesAsync(ssa.Aliases, ssa.Reset, cancellationToken),
        BridgeCommand.SpawnSim spawn => SpawnSimAsync(spawn, cancellationToken),
        BridgeCommand.ImportSimDescription import => ImportSimDescriptionAsync(import, cancellationToken),
        BridgeCommand.ApplySimDetails details => ApplySimDetailsAsync(details, cancellationToken),
        BridgeCommand.DespawnSim despawn => _host.DespawnSimAsync(despawn.SimObjectId, despawn.SimDescriptionId, cancellationToken),
        BridgeCommand.SacsReplay replay => SacsReplayAsync(replay, cancellationToken),
        BridgeCommand.ApplyCash ac => _host.ApplyCashAsync(ac.HouseholdId, ac.Funds, cancellationToken),
        BridgeCommand.ApplySkill askill => _host.ApplySkillAsync(askill.SimObjectId, askill.SkillName, askill.Level, cancellationToken),
        BridgeCommand.ApplyAutonomy aa => _host.ApplyAutonomyAsync(aa.SimObjectId, aa.Disabled, cancellationToken),
        BridgeCommand.ApplyInteractionQueue aiq => _host.ApplyInteractionQueueAsync(aiq.Interaction, cancellationToken),
        BridgeCommand.ApplyInteractions ais => _host.ApplyInteractionsAsync(ais.Interactions, cancellationToken),
        BridgeCommand.ApplyForceAlign afa => _host.ApplyForceAlignAsync(afa.Bundle, cancellationToken),
        BridgeCommand.SetGameSpeed sgs => _host.SetGameSpeedAsync(sgs.Speed, cancellationToken),
        BridgeCommand.SetWorldTime swt => _host.SetWorldTimeAsync(swt.Time, cancellationToken),
        _ => throw new InvalidOperationException(
            $"MirrorEngine has no dispatch handler for {command.GetType().Name}. " +
            "EventToBridgeCommandTranslator emitted a command type the engine cannot route — " +
            "either the translator was extended without updating the engine, or a new ServerEvent " +
            "subtype now produces this command.")
    };
}

/// <summary>M4 P4.2.2.1 per-event dispatch outcome.</summary>
public sealed record DispatchResult(int Translated, int Succeeded, int Failed, IReadOnlyList<Exception> Errors)
{
    public bool AnyFailed => Failed > 0;
}
