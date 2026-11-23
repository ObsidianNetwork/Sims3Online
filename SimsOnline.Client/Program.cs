using System.Globalization;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SimsOnline.DedicatedServer.Mirroring;
using SimsOnline.DedicatedServer.Protocol;
using SimsOnline.DedicatedServer;
using SimsOnline.DedicatedServer.Authority;
using SimsOnline.DedicatedServer.HostBridge;
using SimsOnline.DedicatedServer.Network;
using SimsOnline.DedicatedServer.Observability;

namespace SimsOnline.Client;

/// <summary>
/// M4 P4.1.3 — Client controller scaffold.
///
/// Minimal TCP client that connects to the dedicated server, declares Client role over
/// the public protocol, and prints the ServerEvent stream. Foundation for the P4.2
/// mirroring engine.
///
/// M4 P4.2.2.1 — adds the <c>--enable-mirror</c> flag. When set, the program
/// instantiates a <see cref="LoggingMirrorHost"/> + <see cref="MirrorEngine"/> and
/// dispatches every decodable <see cref="ServerEvent"/> from the receive loop through
/// the engine. The engine translates snapshot events into <c>Apply*</c> calls on the
/// <see cref="IMirrorHost"/>; the logging host writes a <c>mirror_applied</c> line per
/// call. P4.2.2.2 will swap <see cref="LoggingMirrorHost"/> for a client-role
/// <c>HeadlessSims3Host</c> driving a real local TS3 instance via the memory bridge —
/// the swap is one line at the construction site because <c>HeadlessSims3Host</c> already
/// implements <see cref="IMirrorHost"/>.
///
/// CLI:
///   --endpoint &lt;ip:port&gt;          default 127.0.0.1:34019
///   --player &lt;int&gt;               default 1
///   --display &lt;name&gt;             default "client-scaffold"
///   --role &lt;server|client&gt;       default "client"
///   --connect-timeout &lt;seconds&gt;  default 30
///   --max-events &lt;n&gt;             exit cleanly after receiving N events; -1 = forever (default -1)
///   --exit-after-role-changed    exit cleanly after the ClientRoleChanged event arrives (used by integration tests)
///   --exit-after-mirror-applied &lt;n&gt; exit cleanly after N mirror Apply* commands (used by integration tests)
///   --enable-mirror              (P4.2.2.1) instantiate LoggingMirrorHost + MirrorEngine and dispatch incoming ServerEvents through the engine
///   --json-stdout                emit JSON lines on stdout instead of human-readable log (machine-readable mode)
///   --snapshot-log &lt;path&gt; divert verbatim full_snapshot/snapshot_delta `received:` lines to their own file, leaving a compact stub in the main log so it stays greppable
/// </summary>
internal static class Program
{
    // M4 P4.2.5: per-client self-id state for the mirror filter. _selfPlayerId is set
    // once in Main() after arg parse; _ownedSims is mutated in TryMirrorDispatchAsync as SimClaimed/SimReleased arrive.
    private static int s_selfPlayerId = 1;
    private static readonly HashSet<ulong> s_ownedSims = new();
    private static readonly ClientClaimControl s_claimControl = new();
    // lot-local authority mode (default full_puppet_fallback until predicates hold).
    private static LotLocalAuthorityMode s_lotLocalAuthorityMode = LotLocalAuthorityMode.FullPuppetFallback;
    // W2: client authority gate (forked AuthorityHook on the local TS3). Configured mode from
    // --authority-gate or SIMSONLINE_AUTHORITY_GATE (default observe); the native Sim set follows
    // claims and the lot-local mode. Sent with SET_AUTHORITY only on change (SyncClientAuthorityAsync).
    private static AuthorityGateMode s_authorityGateMode = ClientAuthorityPolicy.DefaultMode;
    private static ClientAuthorityState? s_publishedAuthority;
    private static ClientAuthorityState? s_failedAuthority;
    private static DateTimeOffset s_failedAuthorityUtc;
    // PR#15 review fix 1: once the server pushes an authoritative LotLocalAuthorityModeChanged,
    // it is the source of truth. Local RefreshLotLocalAuthorityMode() (owned-sims-only heuristic) must
    // never downgrade a server-provided client-native mode (prox_mirror/solo_native) back to
    // full_puppet_fallback on a subsequent snapshot; doing so clobbered the server's co-presence mode.
    private static bool s_serverLotLocalAuthorityModeReceived = false;
    // test hook (--stall-first-dispatch-ms): the first event's handling waits this long, as the join's
    // authority publish once did on a slow client TS3, and each handled line is then logged ("dispatched").
    private static int s_stallFirstDispatchMs;
    private static bool s_logDispatched;
    // the receive queue's handlers count as behind (receive_queue_behind) at this many lines queued, or when
    // the oldest has waited this long.
    private const int ReceiveQueueWarnLines = 1000;
    private static readonly TimeSpan ReceiveQueueWarnAge = TimeSpan.FromSeconds(10);
    private static int s_joinedPlayerCount = 0;
    private static IReadOnlyList<SimSnapshot> s_latestSnapshotSims = Array.Empty<SimSnapshot>();
    // close-out: the WHOLE latest server-truth snapshot (tick/world/save/speed kept) so
    // an order-completion re-assert can re-dispatch the actor's final position coherently.
    private static SimulationSnapshot? s_latestSnapshot;
    private static ulong? s_ownedActiveLotId;
    // slice 5 (design §3.1): the last monotonic checkpoint version the server
    // acknowledged for this client's lot (from LotReconcileAcknowledged). Echoed back on the
    // next SubmitLotExport as ClientLastReconciledVersion so the server can detect whether this
    // client saw the last merge (versions match -> no contestable fields) or missed it. 0 until
    // the first ack arrives, which is the safe "first export" value.
    private static long s_lastReconciledVersion = 0L;
    private static bool s_soloNativeAutonomyBootstrapped;
    // the host is the SOLE autonomy authority — the owner renders the host's authoritative
    // state on its OWN claimed Sims (position-mirror + local-autonomy suppressed) and ADDITIONALLY
    // drives them via captured/forwarded intents + the echo. therefore stripped NOTHING
    // from snapshots so every owned Sim was always position-mirrored. narrows that: an owned
    // Sim is now stripped from the position-mirror only WHILE it has an in-flight directed order
    // (s_activeOrders), so its local GoHere route can animate; see the gate at FilterSnapshotSims
    // below. The owner's command echo is unaffected — it is keyed on s_pendingIntents, not on
    // what FilterSnapshotSims strips.
    // M4 P4.5: server-reported positions for drift detection.
    private const float DriftThreshold = DriftDetector.DefaultThreshold;
    // this trace is strictly opt-in; no extra JSON decoding on normal clients.
    private static readonly bool sTraceTransportLatency =
        Environment.GetEnvironmentVariable("SIMSONLINE_TRANSPORT_TRACE") == "1";
    // Experiment C, client side: SIMSONLINE_SIMLINK_SHADOW=1, the SimLink sidecar shadows the replay route;
    // SIMSONLINE_SIMLINK_CLIENT_RING=1, the ring is the real path (the mod applies SimAnimation lines from it and they
    // no longer go over the bridge).
    private static readonly bool sSimLinkClientRing = Environment.GetEnvironmentVariable("SIMSONLINE_SIMLINK_CLIENT_RING") == "1";
    private static readonly bool sSimLinkShadowRequested = sSimLinkClientRing || SimsOnline.DedicatedServer.HostBridge.SimLinkShadowSession.Requested;
    // Ring path ordering: an event goes through the ring only while no SimAnimation sent over the bridge is still
    // being dispatched (the mod drains the ring before every bridge command, so ring-before-bridge holds; this keeps
    // bridge-before-ring). Bridge-routed raw lines are tracked by reference until AfterLineAsync sees them.
    private static long s_bridgeAnimationsInFlight;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, object> s_bridgeAnimationLines = new();
    // Lines that arrived while bridge-routed animations were still in flight: held here (not sent over the bridge) until
    // none is, then put into the ring (FlushRingHold). The bridge's backlog only shrinks, so a steady stream cannot keep
    // the ring out for good.
    private static readonly object s_ringHoldGate = new();
    private static readonly List<string> s_ringHold = new();
    private static readonly string s_emptySimAnimation =
        PublicProtocolCodec.EncodeEvent(new ServerEvent.SimAnimation(Array.Empty<string>()));
    private static HeadlessSims3Host? s_ownedClientHost;
    private static SimsOnline.DedicatedServer.HostBridge.SimLinkClientShadowSession? s_clientShadow;
    private static int s_clientShadowStarting;
    private static readonly TimeSpan InteractionBatchReadAhead = TimeSpan.FromMilliseconds(100);
    private static readonly Dictionary<ulong, Vector3Dto> s_serverPositions = new();
    private static long s_lastSnapshotTick = -1;
    // Dev tooling: mirror host reference for in-game toast feedback (claims etc.). Best-effort only.
    private static IMirrorHost? s_mirrorHost;
    // slice 6: snapshot provider for the lot reconcile export. Set once the owned client
    // TS3 host is running; LotExportRequested (and the pre-LeaveSession best-effort export) snapshot
    // the client TS3 through it and submit the owned active-lot class A bundle to the server.
    private static Func<CancellationToken, Task<SimulationSnapshot>>? s_lotExportSnapshotProvider;
    // the owned client TS3 host, stored so TrySubmitLotExportAsync can call
    // ExportHouseholdContentAsync (deep Household.ExportContent via bridge BULK slot) in
    // addition to the position-only snapshot. Null when the client runs without a local TS3.
    private static HeadlessSims3Host? s_exportHost;
    // forwarded-intent bookkeeping. The capture path DELETES the local interaction, so
    // the authoritative InteractionQueued echo must be applied locally (confirmation), not
    // skipped as a duplicate by the P4.2.5 self-id filter.
    private static readonly PendingIntentTracker s_pendingIntents = new();
    // owned Sims with an in-flight player-directed order. A claimed Sim is added here when
    // its echoed GoHere is confirmed locally (route starts) and removed when the order completes /
    // fails / times out. While a Sim is in this set it is STRIPPED from the position-mirror
    // (ApplyPositions) and from drift detection, so its local GoHere route can play out and animate
    // instead of being teleported out from under itself every ~2s — the freeze. Autonomy
    // stays suppressed; only the position-mirror is paused, and only for the directed Sim,
    // and only while its order is in flight (idle owned Sims still render the host = idle
    // convergence). The echo path is unchanged: it still keys on s_pendingIntents.
    private static readonly ActiveOrderTracker s_activeOrders = new();
    // --claim-sim is sent after SetClientRole; host InteractionQueued can arrive before SimClaimed.
    private static ulong? s_intendedClaimSim;

    // the claimed Sim is this client's active Sim. Set on claim and re-asserted every
    // ActiveSimReassertInterval (the client TS3 drifted back to the save's active Sim during a
    // session); the mod does nothing when it is already active, so re-asserting never moves the camera.
    // STOPGAP(boundary-recording): active-sim-reassert re-selects the claimed Sim every 30 s; find what selects the save's Sim again and stop it there.
    private static readonly TimeSpan ActiveSimReassertInterval = TimeSpan.FromSeconds(30);

    // picks this client forwarded, by command id, so a refusal can tell the player which one failed.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> s_forwardedPicks = new();
    private const int ForwardedPicksCap = 64;
    private static DateTime s_lastActiveSimAssertUtc = DateTime.MinValue;
    // Sit must batch with ReadBook (Apply_INTERACTIONS two-pass); defer lone Sit until ReadBook.
    private static readonly Dictionary<ulong, ServerEvent.InteractionQueued> s_deferredSitForBookChain = new();
    private static readonly Dictionary<ulong, ServerEvent.InteractionQueued> s_deferredGetBookForBookChain = new();
    private static readonly Dictionary<ulong, InteractionReplayBundle> s_deferredSitReplayBundle = new();
    private static readonly Dictionary<ulong, InteractionReplayBundle> s_deferredGetBookReplayBundle = new();
    // STOPGAP(boundary-recording): readbook-await-sit holds a ReadBook up to 3 s for the Sit that seats its reader; the server should send them in the order its game ran them.
    // host often emits ReadBook before trailing ForceAlign Sit; hold ReadBook until Sit arrives.
    private static readonly Dictionary<ulong, InteractionReplayBundle> s_pendingReadBookReplayBundle = new();
    private static readonly Dictionary<ulong, ServerEvent.InteractionQueued> s_pendingReadBookForBookChain = new();
    // The ReadBook flush when no Sit came. Its callback runs on the coordinator's drain loop, like dispatch, so these
    // dictionaries (and MirrorEngine) are only ever touched there. Set in Main before the join.
    private static CoordinatorTimers<ulong>? s_readBookFlushTimers;
    private const int PendingReadBookAwaitSitMs = 3000;
    // after book-chain batch, host may emit a corrected ForceAlign Sit (e.g. loveseat supersedes chair).
    private static readonly HashSet<ulong> s_awaitingSeatCorrectionAfterBookChain = new();
    private static readonly Dictionary<ulong, InteractionReplayBundle> s_readBookPendingSeatCorrection = new();
    private static readonly Dictionary<ulong, ServerEvent.InteractionQueued> s_readBookPendingSeatCorrectionIq = new();
    // debug ergonomics: full_snapshot / snapshot_delta `received:` lines are multi-KB (a
    // ~71-Sim world is ~48 KB) and arrive every sync interval, so logging them verbatim grows
    // client.log into the hundreds of MB and drowns grep/Select-String during debugging. When
    // --snapshot-log is set the full payload is diverted to its OWN file and only a compact stub is
    // left in the main log. Set once from --snapshot-log after arg parse; null = stub-only (the
    // snapshot is still summarized out of the main log, just not persisted).
    private static string? s_snapshotLogPath;
    private static readonly object s_snapshotLogLock = new();
    private static SimsOnlineTelemetry? s_telemetry;

    private static async Task<int> Main(string[] args)
    {
        // Force auto-flush on stdout + stderr. Default behavior when stdio is redirected is block
        // buffering, which loses output when the subprocess exits faster than the pipe drains.
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });

        string endpoint = "127.0.0.1:34019";
        string wire = "json";
        int playerId = 1;
        string displayName = "client-scaffold";
        string roleName = "client";
        int connectTimeoutSeconds = 30;
        int maxEvents = -1;
        bool exitAfterRoleChanged = false;
        bool jsonStdout = false;
        bool enableMirror = false;
        // M4 P4.2.7: subprocess integration test for mirror filter loop
        ulong? claimSimId = null;
        bool exitAfterClaim = false;
        int? exitAfterMirrorApplied = null;
        // M4 P4.2.2.2.2: client-side TS3 lifecycle flags. When --client-ts3 + --client-save are both set,
        // the program launches a local TS3 and drives it as a puppet via the mirror engine.
        string? clientTs3Path = null;
        string? clientSaveName = null;
        // Join with the server's world: fetch its checkpointed save into this folder (the client TS3's Saves) and load it.
        bool clientWorldFromServer = false;
        // Warm re-attach (scripts/quick_loop.py rejoin:warm): SIMSONLINE_CLIENT_ATTACH_PID or --client-attach-pid.
        int? clientAttachPid = int.TryParse(Environment.GetEnvironmentVariable("SIMSONLINE_CLIENT_ATTACH_PID"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int attachFromEnv) && attachFromEnv > 0 ? attachFromEnv : null;
        string? clientSavesDir = null;
        string? clientHostBridgeEndpoint = null;
        string? clientBridgectlEndpoint = null;
        int clientHostTimeoutSeconds = 120;
        string? clientTs3SandboxieBoxName = null;
        string? clientTs3SandboxieStartPath = null;
        string? clientTs3WineprefixPath = null;
        string? clientStateFilePath = null;
        // short timeout for player-paced bridge commands (mirror applies); null uses
        // SessionConfig.DefaultPlayerCommandTimeout. --bridge-trace logs per-frame bridge traffic.
        int? clientPlayerCommandTimeoutSeconds = null;
        bool bridgeTrace = false;
        string? bridgeRecordPath = null;
        // when set, full snapshot/delta received: lines go here instead of bloating the main log.
        string? snapshotLogPath = null;
        // M4 P4.2.2.2.4.b: exit after position verification on puppet Sim
        ulong? exitPositionSimId = null;
        Vector3Dto? exitTargetPosition = null;
        // P5.6: coordinator knobs. Defaults match SessionConfig.EffectiveMaxFoldPerCycle / EffectiveResyncCooldown.
        int maxFoldPerCycle = 8;
        double resyncCooldownSeconds = 5.0;

        if (ClientAuthorityPolicy.TryParseMode(Environment.GetEnvironmentVariable(ClientAuthorityPolicy.ModeEnvironmentVariable), out AuthorityGateMode envAuthorityMode))
        {
            s_authorityGateMode = envAuthorityMode;
        }

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--endpoint" && i + 1 < args.Length) endpoint = args[++i];
            else if (arg == "--wire" && i + 1 < args.Length) wire = PublicWireConnection.ValidateMode(args[++i]);
            else if (arg == "--player" && i + 1 < args.Length) playerId = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (arg == "--display" && i + 1 < args.Length) displayName = args[++i];
            else if (arg == "--role" && i + 1 < args.Length) roleName = args[++i];
            else if (arg == "--authority-gate" && i + 1 < args.Length)
            {
                if (!ClientAuthorityPolicy.TryParseMode(args[++i], out s_authorityGateMode))
                    throw new ArgumentException("--authority-gate must be off, observe or enforce.");
            }
            else if (arg == "--connect-timeout" && i + 1 < args.Length) connectTimeoutSeconds = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (arg == "--max-events" && i + 1 < args.Length) maxEvents = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (arg == "--stall-first-dispatch-ms" && i + 1 < args.Length)
            {
                s_stallFirstDispatchMs = int.Parse(args[++i], CultureInfo.InvariantCulture);
                s_logDispatched = true;
            }
            else if (arg == "--exit-after-role-changed") exitAfterRoleChanged = true;
            else if (arg == "--enable-mirror") enableMirror = true;
            else if (arg == "--claim-sim" && i + 1 < args.Length) claimSimId = ulong.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (arg == "--exit-after-claim") exitAfterClaim = true;
            else if (arg == "--exit-after-mirror-applied" && i + 1 < args.Length) exitAfterMirrorApplied = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (arg == "--json-stdout") jsonStdout = true;
            else if (arg == "--client-ts3" && i + 1 < args.Length) clientTs3Path = args[++i];
            else if (arg == "--client-save" && i + 1 < args.Length) clientSaveName = args[++i];
            else if (arg == "--client-world-from-server") clientWorldFromServer = true;
            else if (arg == "--client-attach-pid" && i + 1 < args.Length) clientAttachPid = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (arg == "--client-saves-dir" && i + 1 < args.Length) clientSavesDir = args[++i];
            else if (arg == "--client-host-bridge" && i + 1 < args.Length) clientHostBridgeEndpoint = args[++i];
            else if (arg == "--client-bridgectl-listen" && i + 1 < args.Length) clientBridgectlEndpoint = args[++i];
            else if (arg == "--client-host-timeout" && i + 1 < args.Length) clientHostTimeoutSeconds = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (arg == "--client-ts3-sandboxie-box" && i + 1 < args.Length) clientTs3SandboxieBoxName = args[++i];
            else if (arg == "--client-ts3-sandboxie-start" && i + 1 < args.Length) clientTs3SandboxieStartPath = args[++i];
            else if (arg == "--client-ts3-wineprefix" && i + 1 < args.Length) clientTs3WineprefixPath = args[++i];
            else if (arg == "--state-file" && i + 1 < args.Length) clientStateFilePath = args[++i];
            else if (arg == "--client-player-command-timeout" && i + 1 < args.Length) clientPlayerCommandTimeoutSeconds = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (arg == "--bridge-trace") bridgeTrace = true;
            else if (arg == "--bridge-record" && i + 1 < args.Length) bridgeRecordPath = args[++i];
            else if (arg == "--snapshot-log" && i + 1 < args.Length) snapshotLogPath = args[++i];
            else if (arg == "--exit-after-position-mirror" && i + 1 < args.Length)
            {
                string val = args[++i];
                int colon = val.IndexOf(':');
                if (colon > 0)
                {
                    exitPositionSimId = ulong.Parse(val.Substring(0, colon), CultureInfo.InvariantCulture);
                    string[] coords = val.Substring(colon + 1).Split(',');
                    if (coords.Length == 3)
                    {
                        float x = float.Parse(coords[0], CultureInfo.InvariantCulture);
                        float y = float.Parse(coords[1], CultureInfo.InvariantCulture);
                        float z = float.Parse(coords[2], CultureInfo.InvariantCulture);
                        exitTargetPosition = new Vector3Dto(x, y, z);
                    }
                }
            }
            // P5.6: coordinator knobs
            else if (arg == "--max-fold-per-cycle" && i + 1 < args.Length)
                maxFoldPerCycle = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (arg == "--resync-cooldown-seconds" && i + 1 < args.Length)
                resyncCooldownSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
        }

        if (claimSimId.HasValue)
        {
            s_intendedClaimSim = claimSimId.Value;
        }

        PlayerRole role = roleName.Equals("server", StringComparison.OrdinalIgnoreCase) ? PlayerRole.Server : PlayerRole.Client;
        string[] parts = endpoint.Split(':');
        if (parts.Length != 2)
        {
            Console.Error.WriteLine($"--endpoint must be ip:port. Got: {endpoint}");
            return 1;
        }
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int port))
        {
            Console.Error.WriteLine($"--endpoint port must be numeric. Got: {parts[1]}");
            return 1;
        }
        if (clientHostBridgeEndpoint != null && HostBridgeServer.UnsupportedEndpointError(clientHostBridgeEndpoint) is { } clientBridgeError)
        {
            Console.Error.WriteLine("--client-host-bridge: " + clientBridgeError);
            return 1;
        }

        await using SimsOnlineTelemetry telemetry =
            SentryConfigurator.ConfigureClient(roleName, clientSaveName);
        s_telemetry = telemetry;
        telemetry.AddBreadcrumb("client.starting");
        telemetry.LogInfo("client.starting");
        telemetry.RecordRuntimeActiveMetric(active: true);

        using CancellationTokenSource shutdown = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };
        using PosixSignalRegistration? sigtermRegistration = OperatingSystem.IsWindows()
            ? null
            : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                shutdown.Cancel();
            });

        LoggingMirrorHost? mirrorHost = null;
        HeadlessSims3Host? ownedClientHost = null;
        RunStateFile? clientState = null;
        BridgeAdminListener? clientAdminListener = null;
        Action<ServerEvent>? intentHandler = null;
        try
        {

        // M4 P4.2.2.1: mirror loop wiring. LoggingMirrorHost is the observability-only
        // default for this slice; HeadlessSims3Host in client role swaps in for P4.2.2.2.
        // The engine's log callback surfaces per-command failures via the same JSON envelope
        // SimsOnline.Client uses for everything else.
        // M4 P4.2.2.2.2: factory selects observability-only LoggingMirrorHost OR client-role
        // HeadlessSims3Host driving a local TS3. The owned host (when present) is started + role-set +
        // save-loaded BELOW (before TCP connect) so the local TS3 is ready to receive APPLY_* commands
        // by the time ServerEvents start arriving from the dedicated server.
        ClientMirrorHostFactory.Result mirrorFactory = ClientMirrorHostFactory.Create(new ClientMirrorHostFactory.Options(
            EnableMirror: enableMirror,
            JsonStdout: jsonStdout,
            Writer: Console.Out,
            ClientTs3Path: clientTs3Path,
            ClientSaveName: clientSaveName,
            ClientHostBridgeEndpoint: clientHostBridgeEndpoint,
            ClientHostTimeoutSeconds: clientHostTimeoutSeconds,
            ClientTs3SandboxieBoxName: clientTs3SandboxieBoxName,
            ClientTs3SandboxieStartPath: clientTs3SandboxieStartPath,
            ClientTs3WineprefixPath: clientTs3WineprefixPath,
            ClientStateFilePath: clientStateFilePath,
            ClientPlayerCommandTimeoutSeconds: clientPlayerCommandTimeoutSeconds,
            ClientAttachPid: clientAttachPid,
            ClientSavesDirectory: clientSavesDir));
        mirrorHost = mirrorFactory.LoggingHost;
        ownedClientHost = mirrorFactory.OwnedHost;
        s_ownedClientHost = ownedClientHost;
        // 2026-07-03 dev-tooling: client controller state file (mirrors the server's RunStateFile).
        // wait-stack-ready polls this instead of grepping the buffered client.log, which stays
        // empty under Wine's Start-Process redirect. Null when --state-file is not passed.
        clientState = clientStateFilePath != null ? new RunStateFile(clientStateFilePath) : null;
        clientState?.Set("role", "client");
        clientState?.Set("player", playerId);
        clientState?.Set("bridgectl", clientBridgectlEndpoint);
        clientState?.Transition("starting");
        if (ownedClientHost != null && clientState != null)
        {
            HeadlessSims3Host hostForState = ownedClientHost;
            clientState.Set("clientTs3", clientTs3Path);
            hostForState.PhaseSink = (phase, detail) =>
            {
                if (hostForState.HostProcessId is { } gamePid)
                {
                    clientState.Set("gamePid", gamePid);
                    telemetry.SetTs3ProcessId(gamePid);
                }
                if (hostForState.Bridge.ModBuildStamp is { } stamp)
                {
                    clientState.Set("modBuild", stamp);
                }
                clientState.Transition(phase, detail);
            };
        }
        MirrorEngine? mirrorEngine = mirrorFactory.Host != null
            ? new MirrorEngine(mirrorFactory.Host, msg =>
            {
                if (msg.Contains("Sim not found", StringComparison.OrdinalIgnoreCase)
                    && (msg.Contains("ApplyAutonomy", StringComparison.OrdinalIgnoreCase)
                        || msg.Contains("APPLY_AUTONOMY", StringComparison.OrdinalIgnoreCase)
                        || msg.Contains("ApplyInteractionQueue", StringComparison.OrdinalIgnoreCase)
                        || msg.Contains("APPLY_INTERACTION_QUEUE", StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                if (msg.Contains("ApplyAutonomy", StringComparison.OrdinalIgnoreCase)
                    && msg.Contains("Timed out waiting", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                Log(jsonStdout, "mirror_engine_log", new { message = msg });
            })
            : null;
        s_mirrorHost = mirrorFactory.Host;
        // MVP 3: SIMSONLINE_MIRROR_INTERACTIONS=0 turns interaction replay off (positions,
        // aliases, spawns and state keep flowing) for the presentation replay spike.
        if (mirrorEngine != null && Environment.GetEnvironmentVariable("SIMSONLINE_MIRROR_INTERACTIONS") == "0")
        {
            mirrorEngine.InteractionReplayEnabled = false;
            Log(jsonStdout, "mirror_interaction_replay", new { enabled = false });
        }

        // M4 P4.2.5: hoist self player id to static so the filter can access it.
        s_selfPlayerId = playerId;
        // hoist the snapshot side-file path so LogReceived can divert snapshot bodies.
        s_snapshotLogPath = snapshotLogPath;
        Log(jsonStdout, "client_start", new { endpoint, playerId, displayName, role = role.ToString(), mirror_enabled = enableMirror });

        // T10: loopback admin route to the client's own host bridge, so synthetic
        // player input (INJECT_CLICK) can reach the client-role mod the same way bridgectl
        // reaches the server. Declared here so the outer finally can dispose it on every exit.
        // M4 P4.2.2.2.2: start local TS3 BEFORE TCP connect when client-headless mode.
        // Ensures the puppet is ready to receive APPLY_* commands the moment the receive loop
        // starts dispatching ServerEvents. Startup failure -> early exit 1 with explicit cleanup.
        if (ownedClientHost != null)
        {
            // --bridge-trace: per-frame client-bridge protocol trace through the
            // standard log envelope (subscribe before StartAsync to catch connect-phase frames).
            if (bridgeTrace)
            {
                ownedClientHost.Bridge.Diagnostic += line => Log(jsonStdout, "bridge_trace", new { line });
            }

            // --bridge-record: lossless client-bridge frame capture.
            if (bridgeRecordPath != null)
            {
                StreamWriter recorder = new(
                    new FileStream(bridgeRecordPath, FileMode.Append, FileAccess.Write, FileShare.Read),
                    new UTF8Encoding(false))
                { AutoFlush = true };
                object recorderLock = new();
                ownedClientHost.Bridge.FrameRecord += line =>
                {
                    lock (recorderLock)
                    {
                        recorder.WriteLine($"[bridge {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z] {line}");
                    }
                };
                Log(jsonStdout, "bridge_record_started", new { path = bridgeRecordPath });
            }

            Log(jsonStdout, "client_ts3_starting", new { ts3Path = clientTs3Path, saveName = clientSaveName, sandboxieBox = clientTs3SandboxieBoxName });
            telemetry.AddBreadcrumb("client.host.starting");
            using SimsOnlineTelemetryTransaction hostStartTransaction =
                telemetry.StartTransaction("client.host.start", "bridge.connect");
            try
            {
                await ownedClientHost.StartAsync(shutdown.Token);
                if (ownedClientHost.HostProcessId is { } gamePid)
                {
                    telemetry.SetTs3ProcessId(gamePid);
                }
                await ownedClientHost.SetRoleAsync(HostRole.Client, shutdown.Token);
                if (clientAttachPid is int attached)
                {
                    // Warm re-attach: that TS3 is already in the world; this controller only rejoins the session (the
                    // server sends this player the late-join history again, and the mod resets its copies first).
                    Log(jsonStdout, "client_ts3_attached", new { pid = attached });
                }
                else
                {
                    if (clientWorldFromServer)
                    {
                        clientSaveName = await FetchWorldFromServerAsync(parts[0], port, playerId, clientSavesDir, jsonStdout,
                            phase => clientState?.Transition(phase), shutdown.Token, wire);
                    }

                    await ownedClientHost.LoadSaveAsync(clientSaveName!, shutdown.Token);
                }
                hostStartTransaction.Complete(succeeded: true);
                Log(jsonStdout, "client_ts3_session_ready", new { });
                telemetry.AddBreadcrumb("client.host.ready");

                // T10: the bridge is connected now — open the admin listener on it.
                // BridgeAdminListener rejects non-loopback binds; the ctor does not self-start.
                if (clientBridgectlEndpoint != null)
                {
                    clientAdminListener = new BridgeAdminListener(ownedClientHost.Bridge, clientBridgectlEndpoint,
                        (name, args) => s_claimControl.Handle(name, args, s_selfPlayerId));
                    await clientAdminListener.StartAsync(shutdown.Token);
                    Log(jsonStdout, "client_bridgectl_listening", new { endpoint = clientAdminListener.BoundEndpoint });
                }

                // 2026-07-03: the host PhaseSink fires on host-internal milestones (host-scanning,
                // host-connected), but the CLIENT's own readiness (save loaded + bridgectl bound +
                // about to enter the mirror receive loop) is a client-specific milestone the host
                // doesn't know about. Mark it explicitly so wait-stack-ready / dev-session /
                // agents polling the state file see phase=ready (not stuck at host-connected).
                clientState?.Transition("ready");

                // slice 6: the client TS3 is live -- lot reconcile exports can now snapshot it.
                HeadlessSims3Host exportHost = ownedClientHost;
                s_lotExportSnapshotProvider = ct => exportHost.GetSnapshotAsync(ct);
                // store the host so the deep export path (Household.ExportContent
                // via bridge BULK slot) can be invoked from TrySubmitLotExportAsync.
                s_exportHost = exportHost;
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                hostStartTransaction.Dispose();
                return 0;
            }
            catch (Exception ex)
            {
                Log(jsonStdout, "client_ts3_startup_failed", new { error = ex.Message });
                clientState?.Transition("error", ex.Message);
                WriteSentryEventId(telemetry.ReportFailure(
                    ex,
                    "client.ts3_startup_failed",
                    new Dictionary<string, string?> { ["validation_gate"] = "client-host-start" }));
                hostStartTransaction.Complete(succeeded: false);
                return 1;
            }
        }

        // M4 P4.2.2.2.2: outer try/finally guarantees local TS3 teardown on every exit path
        // (normal return, OperationCanceledException, IOException, unhandled exception). Indentation of
        // the inner block is intentionally NOT bumped to minimise diff churn; brace nesting is correct.
        using TcpClient tcp = new();
        using SimsOnlineTelemetryTransaction connectTransaction =
            telemetry.StartTransaction("client.connect", "bridge.connect");
        try
        {
            using CancellationTokenSource connectCts = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            connectCts.CancelAfter(TimeSpan.FromSeconds(connectTimeoutSeconds));
            await tcp.ConnectAsync(parts[0], port, connectCts.Token);
            connectTransaction.Complete(succeeded: true);
        }
        catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
        {
            Console.Error.WriteLine($"Connect timed out after {connectTimeoutSeconds}s.");
            WriteSentryEventId(telemetry.ReportFailure(
                new TimeoutException("Client connection timed out."),
                "client.connect_timeout",
                new Dictionary<string, string?>
                {
                    ["public_port"] = port.ToString(CultureInfo.InvariantCulture),
                    ["validation_gate"] = "public-connect",
                }));
            connectTransaction.Complete(succeeded: false);
            return 1;
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine($"Connect failed: {ex.Message}");
            WriteSentryEventId(telemetry.ReportFailure(
                ex,
                "client.connect_failed",
                new Dictionary<string, string?>
                {
                    ["public_port"] = port.ToString(CultureInfo.InvariantCulture),
                    ["validation_gate"] = "public-connect",
                }));
            connectTransaction.Complete(succeeded: false);
            return 1;
        }

        Log(jsonStdout, "tcp_connected", new { endpoint });
        telemetry.AddBreadcrumb("client.connected");

        await using NetworkStream stream = tcp.GetStream();
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        PublicWireConnection? lineWire = wire == "line" ? new(failure =>
        {
            Log(jsonStdout, "wire_error", new { code = failure });
            clientState?.Set("wireError", failure);
        }) : null;
        if (lineWire != null)
        {
            using CancellationTokenSource helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            helloTimeout.CancelAfter(TimeSpan.FromSeconds(connectTimeoutSeconds));
            await lineWire.ConnectAsync(stream, reader, roleName, helloTimeout.Token);
        }

        // The bridge thread forwards the player's picks while the dispatch loop sends replies: one writer at a time,
        // or two command lines can interleave on the socket.
        using SemaphoreSlim writeLock = new(1, 1);
        async Task WriteAsync(ClientCommand command)
        {
            await writeLock.WaitAsync(shutdown.Token);
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes((lineWire?.EncodeCommand(command)
                    ?? PublicProtocolCodec.EncodeCommand(command)) + "\n");
                await stream.WriteAsync(bytes, shutdown.Token);
                await stream.FlushAsync(shutdown.Token);
            }
            finally
            {
                writeLock.Release();
            }
        }

        // Forwarded picks are sent without waiting; log a failed send instead of losing it silently.
        void ForwardInBackground(ClientCommand command)
        {
            _ = WriteCommandAsync(command).ContinueWith(
                failed => Log(jsonStdout, "command_send_failed", new { commandId = command.CommandId, command = command.GetType().Name, error = failed.Exception?.GetBaseException().Message }),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        async Task WriteCommandAsync(ClientCommand command)
        {
            using SimsOnlineTelemetryTransaction commandTransaction =
                telemetry.StartTransaction("client.command", "client.command");
            try
            {
                telemetry.AttachTrace(command);
                await WriteAsync(command);
                commandTransaction.Complete(succeeded: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                commandTransaction.Complete(succeeded: false);
                throw;
            }
        }

        s_claimControl.Send = WriteCommandAsync;

        if (mirrorEngine != null)
        {
            // a Sim the server created at runtime: ask for its description.
            mirrorEngine.RequestSimDescription = descriptionId =>
                WriteCommandAsync(new ClientCommand.RequestSimDescription(Guid.NewGuid(), s_selfPlayerId, descriptionId));
        }

        // the client always drains its socket, whatever its handlers do. One receive task reads every line
        // from the join's first reply on into an in-order queue; the join drain below and then the MirrorCoordinator
        // consume it in turn. The join drain used to read the socket itself, so a handler waiting on the
        // client TS3 (the authority publish on the join's command_accepted took 5.6 s on a slow boot) left it unread
        // until a server write hit its 5 s broadcast timeout (bad-join-20261020).
        Guid joinId = Guid.NewGuid();
        ReceiveQueue dispatchQueue = new();
        // P5.6: build coordinator from parsed knobs (or defaults). It exists before the join: a read-book flush timer
        // posts its work into the queue, and the join drain (ReadLineOrEofAsync) runs it as the drain loop would.
        MirrorCoordinator coordinator = new MirrorCoordinator(new CoordinatorConfig(
            SelfPlayerId: playerId,
            MaxFoldPerCycle: maxFoldPerCycle,
            ResyncCooldown: TimeSpan.FromSeconds(resyncCooldownSeconds),
            InteractionBatchReadAhead: InteractionBatchReadAhead));
        s_readBookFlushTimers = new CoordinatorTimers<ulong>(work => coordinator.Post(dispatchQueue.TryWrite, work));
        using CancellationTokenSource receiveCts = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        Task receiveTask = Task.Run(async () =>
        {
            // The join's replies go to the join drain as read; each later line gets the event loop's receive-time
            // handling, as when its receive task started after the join.
            JoinReplies joinReplies = new(joinId, lineWire != null);
            try
            {
                while (!receiveCts.Token.IsCancellationRequested)
                {
                    string? received = (lineWire != null
                        ? await lineWire.ReadJsonAsync(reader, receiveCts.Token)
                        : await reader.ReadLineAsync(receiveCts.Token))
                        ?? throw new IOException("Server closed the connection.");
                    if (!joinReplies.Complete)
                    {
                        joinReplies.Observe(received);
                        LogReceived(jsonStdout, received);
                        dispatchQueue.TryWrite(received);
                        continue;
                    }

                    // timestamp the *socket read*, before the
                    // MirrorCoordinator queues/coalesces or applies anything.
                    if (sTraceTransportLatency &&
                        received.Contains("\"sim_animation\"", StringComparison.Ordinal))
                    {
                        try
                        {
                            if (PublicProtocolCodec.DecodeEvent(received) is ServerEvent.SimAnimation animation)
                            {
                                var span = PresentationStream.EventSequenceBounds(animation.Lines);
                                Console.Error.WriteLine(
                                    $"[transport-latency] stage=client_network_receive " +
                                    $"at_ms={Environment.TickCount64} at_qpc={System.Diagnostics.Stopwatch.GetTimestamp()} " +
                                    $"qpc_freq={System.Diagnostics.Stopwatch.Frequency} at_utc_ticks={DateTime.UtcNow.Ticks} " +
                                    $"first_seq={span.First} " +
                                    $"last_seq={span.Last} lines={animation.Lines.Count} " +
                                    $"bytes={Encoding.UTF8.GetByteCount(received)}");
                            }
                        }
                        catch (Exception)
                        {
                            // Tracing must never change the network reader's behavior.
                        }
                    }
                    // Logged as received: in ring mode the dispatched copy of a routed or held event is empty.
                    string logged = received;
                    if (sSimLinkShadowRequested &&
                        received.Contains("\"sim_animation\"", StringComparison.Ordinal))
                    {
                        received = SimLinkClientShadowObserve(received);
                    }
                    LogReceived(jsonStdout, logged);
                    dispatchQueue.TryWrite(received);
                }

                dispatchQueue.TryComplete();
            }
            catch (Exception ex)
            {
                dispatchQueue.TryComplete(ex);
            }
        }, CancellationToken.None);
        // A handler stuck behind the queue shows in the log instead of staying silent.
        Task receiveWatch = dispatchQueue.WatchAsync(ReceiveQueueWarnLines, ReceiveQueueWarnAge, lag => Log(jsonStdout,
            lag.Behind ? "receive_queue_behind" : "receive_queue_caught_up",
            new { depth = lag.Depth, oldestSeconds = lag.OldestSeconds, maxDepth = lag.MaxDepth, maxOldestSeconds = lag.MaxOldestSeconds,
                warnLines = ReceiveQueueWarnLines, warnSeconds = ReceiveQueueWarnAge.TotalSeconds }), receiveCts.Token);

        async Task StopReceivingAsync()
        {
            receiveCts.Cancel();
            try { await receiveTask; } catch { }
            await receiveWatch;
        }

        // The join drain's lines come off the same queue (posted work runs on the way); once it is done, the receive
        // task's error (or end of stream).
        async Task<string> ReadLineOrEofAsync() =>
            await coordinator.ReadLineAsync(dispatchQueue.Reader, jsonStdout, shutdown.Token)
            ?? throw new IOException("Server closed the connection.");

        if (ownedClientHost != null)
        {
            intentHandler = (ServerEvent ev) =>
            {
                if (ev is ServerEvent.IntentCaptured intent)
                {
                    ulong? targetId = intent.TargetObjectId == 0uL ? null : intent.TargetObjectId;
                    InteractionRequest req = new InteractionRequest(
                        intent.ActorObjectId,
                        intent.InteractionKey,
                        targetId,
                        intent.TargetPosition,
                        new Dictionary<string, string>());
                    ClientCommand.QueueInteraction cmd = new ClientCommand.QueueInteraction(Guid.NewGuid(), s_selfPlayerId, req);
                    // remember the forwarded intent so the authoritative echo is applied
                    // locally as confirmation instead of being skipped as a self-owned duplicate.
                    s_pendingIntents.RecordForwarded(cmd.CommandId, intent.ActorObjectId, intent.InteractionKey, DateTime.UtcNow);
                    ForwardInBackground(cmd);
                    Log(jsonStdout, "intent_forwarded", new { commandId = cmd.CommandId, actor = intent.ActorObjectId, key = intent.InteractionKey });
                }
                else if (ev is ServerEvent.UserIntentCaptured pick)
                {
                    // input forwarding: the gate blocked the player's pick locally; the server runs
                    // it and the client sees it through the presentation stream.
                    ClientCommand.QueueInteraction cmd = new ClientCommand.QueueInteraction(Guid.NewGuid(), s_selfPlayerId, pick.ToRequest());
                    if (s_forwardedPicks.Count >= ForwardedPicksCap)
                    {
                        s_forwardedPicks.Clear();
                    }

                    s_forwardedPicks[cmd.CommandId] = pick.Name;
                    ForwardInBackground(cmd);
                    Log(jsonStdout, "user_intent_forwarded", new { commandId = cmd.CommandId, actor = pick.ActorObjectId, target = pick.TargetObjectId, type = pick.DefinitionType, name = pick.Name });
                }
                else if (ev is ServerEvent.DialogAnswerCaptured dialogAnswer)
                {
                    // the player answered a remote dialog of their Sim; the server's waiting Sim resumes.
                    ClientCommand.AnswerDialog cmd = new ClientCommand.AnswerDialog(Guid.NewGuid(), s_selfPlayerId, dialogAnswer.RequestId, dialogAnswer.SimObjectId, dialogAnswer.Answer);
                    ForwardInBackground(cmd);
                    Log(jsonStdout, "dialog_answer_forwarded", new { commandId = cmd.CommandId, request = dialogAnswer.RequestId, sim = dialogAnswer.SimObjectId, answered = dialogAnswer.Answer != null });
                }
                else if (ev is ServerEvent.CancelIntentCaptured cancel)
                {
                    // queue mirror: a cancel from the HUD's mirrored queue; the server cancels.
                    ClientCommand.CancelInteraction cmd = new ClientCommand.CancelInteraction(Guid.NewGuid(), s_selfPlayerId, cancel.ActorObjectId, cancel.InteractionId);
                    ForwardInBackground(cmd);
                    Log(jsonStdout, "cancel_intent_forwarded", new { commandId = cmd.CommandId, actor = cancel.ActorObjectId, interaction = cancel.InteractionId });
                }
                else if (ev is ServerEvent.LeaveConversationIntentCaptured leave)
                {
                    // n14: the player left their Sim's mirrored conversation from the HUD; the server leaves it.
                    ClientCommand.LeaveConversation cmd = new ClientCommand.LeaveConversation(Guid.NewGuid(), s_selfPlayerId, leave.ActorObjectId);
                    ForwardInBackground(cmd);
                    Log(jsonStdout, "leave_intent_forwarded", new { commandId = cmd.CommandId, actor = leave.ActorObjectId });
                }
                else if (ev is ServerEvent.SpeedIntentCaptured speedIntent)
                {
                    ClientCommand.SetGameSpeed cmd = new ClientCommand.SetGameSpeed(Guid.NewGuid(), s_selfPlayerId, speedIntent.Speed);
                    ForwardInBackground(cmd);
                    Log(jsonStdout, "speed_intent_forwarded", new { commandId = cmd.CommandId, speed = speedIntent.Speed.ToString() });
                }
            };
        }

        try
        {
            async Task SendCommandAsync(ClientCommand cmd)
            {
                await WriteCommandAsync(cmd);
            }

            // Step 1: JoinSession. with this game's on-screen walk, read from its code (the mod's status field
            // reads off until the SimLink handshake, which comes after the join); the server warns when it differs.
            string? onScreen = ownedClientHost != null
                ? SimsOnline.DedicatedServer.HostBridge.OnScreenWalk.ReadProcess(ownedClientHost.HostProcessId)
                : null;
            // A fresh join rebuilds presentation and may restart the server's navigation epoch.
            // A socket/bridge resume alone never runs this reset.
            if (ownedClientHost != null)
            {
                await ownedClientHost.SacsRecorderAsync(new[] { "presentation-reset" }, shutdown.Token);
            }
            await WriteCommandAsync(new ClientCommand.JoinSession(joinId, playerId, displayName, onScreen));
            Log(jsonStdout, "sent", new { command = "join_session", commandId = joinId, playerId, displayName, onScreen });

            // Drain JoinSession responses (command_accepted + session_joined + join baseline)
            // before sending the next command. part 2 adds a reply-only FullSnapshot
            // (~15s over the chunked bridge) so the mirror world-syncs at join time.
            // from the receive queue, which logged each line as it arrived.
            JoinReplies joinReplies = new(joinId, lineWire != null);
            while (!joinReplies.Complete)
            {
                string line = await ReadLineOrEofAsync();
                _ = await TryMirrorDispatchAsync(mirrorEngine, line, jsonStdout, shutdown.Token, SendCommandAsync);
                joinReplies.Observe(line);
            }
            telemetry.AddBreadcrumb("client.session_joined");

            // P5.2: arm capture forwarding only after join baseline mirror completes so
            // SET_GAME_SPEED / APPLY_POSITION churn does not emit spurious speed intents.
            if (ownedClientHost != null && intentHandler != null)
            {
                ownedClientHost.Bridge.AuthoritativeEvent += intentHandler;
            }

            // Step 2: SetClientRole
            Guid roleId = Guid.NewGuid();
            await WriteCommandAsync(new ClientCommand.SetClientRole(roleId, playerId, role));
            Log(jsonStdout, "sent", new { command = "set_client_role", commandId = roleId, role = role.ToString() });
            telemetry.AddBreadcrumb(
                "client.role_requested",
                new Dictionary<string, string?> { ["client_role"] = role.ToString() });

            // M4 P4.2.7: if --claim-sim was provided, send a ClaimSim after SetClientRole.
            // This triggers the server-side P4.2.6 SimClaimed + SimAutonomyChanged broadcast pair,
            // which exercises the P4.2.5 self-id filter end-to-end via the receive loop below.
            if (claimSimId.HasValue)
            {
                s_intendedClaimSim = claimSimId.Value;
                Guid claimId = Guid.NewGuid();
                await WriteCommandAsync(new ClientCommand.ClaimSim(claimId, playerId, claimSimId.Value));
                Log(jsonStdout, "sent", new { command = "claim_sim", commandId = claimId, simObjectId = claimSimId.Value });
                telemetry.AddBreadcrumb(
                    "client.claim_requested",
                    new Dictionary<string, string?>
                    {
                        ["claimed_sim_id"] = claimSimId.Value.ToString(CultureInfo.InvariantCulture),
                    });
            }

            // Step 3: event read loop.
            // socket reads are decoupled from mirror dispatch. The old loop awaited
            // TryMirrorDispatchAsync inline, so one wedged bridge apply froze ALL event
            // processing for its full timeout (an interaction_failed broadcast once sat unread
            // for 10 minutes). The receive task now drains the socket into an in-order queue
            // (arrival is logged at arrival time); this loop dispatches sequentially from it.
            // the receive task and its queue start before the join (above), with the coordinator.
            // P5.6: MirrorCoordinator owns the drain+dispatch loop, adding cross-delta coalescing
            // (LP-2), event ordering (LP-3), per-raw-delta drift detection (LP-4), and a single
            // debounced RequestSnapshot arbiter (LP-6). The old inline drift block is deleted.

            // Integration-test exit conditions (formerly inlined in the drain loop).
            // afterLine is called after each raw line has been fully dispatched; return true to break.
            int eventCount = 0;
            async Task<bool> AfterLineAsync(string line, int count)
            {
                eventCount = count;
                if (s_bridgeAnimationLines.TryGetValue(line, out _))
                {
                    s_bridgeAnimationLines.Remove(line);
                    long inFlight = Interlocked.Decrement(ref s_bridgeAnimationsInFlight);
                    if (s_clientShadow is { } shadowSession)
                    {
                        Interlocked.Exchange(ref shadowSession.BridgeInFlight, inFlight);
                    }

                    if (inFlight == 0 && FlushRingHold() is string flushed)
                    {
                        dispatchQueue.TryWrite(flushed);
                    }
                }

                // M4 P4.2.7: subprocess integration test trigger.
                if (exitAfterClaim && line.Contains("\"sim_autonomy_changed\"", StringComparison.Ordinal))
                {
                    Log(jsonStdout, "exit_after_claim", new { eventCount, mirror_applied = mirrorHost?.AppliedCount ?? 0 });
                    return true;
                }

                if (exitAfterMirrorApplied is int mirrorAppliedTarget
                    && mirrorHost != null
                    && mirrorHost.AppliedCount >= mirrorAppliedTarget)
                {
                    Log(jsonStdout, "exit_after_mirror_applied", new { eventCount, mirror_applied = mirrorHost.AppliedCount, target = mirrorAppliedTarget });
                    return true;
                }

                // M4 P4.2.2.2.4.b: exit after position verification on puppet Sim.
                if (exitPositionSimId.HasValue && exitTargetPosition.HasValue && ownedClientHost != null && line.Contains("\"snapshot_delta\"", StringComparison.Ordinal))
                {
                    Log(jsonStdout, "client_mirror_position_check_started", new { simId = exitPositionSimId.Value });
                    try
                    {
                        var snapshot = await ownedClientHost.GetSnapshotAsync(shutdown.Token);
                        Log(jsonStdout, "client_mirror_position_snapshot_received", new { simCount = snapshot.Sims.Count });
                        var sim = snapshot.Sims.FirstOrDefault(s => s.ObjectId == exitPositionSimId.Value);
                        if (sim != null)
                        {
                            double dx = sim.Position.X - exitTargetPosition.Value.X;
                            double dy = sim.Position.Y - exitTargetPosition.Value.Y;
                            double dz = sim.Position.Z - exitTargetPosition.Value.Z;
                            double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                            Log(jsonStdout, "client_mirror_position_check", new { simId = exitPositionSimId.Value, currentPos = sim.Position, targetPos = exitTargetPosition.Value, distance });
                            if (distance < 3.0)
                            {
                                Log(jsonStdout, "client_mirror_position_verified", new { simId = exitPositionSimId.Value, pos = sim.Position });
                                return true;
                            }
                        }
                        else
                        {
                            Log(jsonStdout, "client_mirror_position_check_sim_not_found", new { simId = exitPositionSimId.Value });
                        }
                    }
                    catch (Exception ex)
                    {
                        Log(jsonStdout, "client_mirror_position_check_error", new { error = ex.Message });
                    }
                }

                if (line.Contains("\"client_role_changed\"", StringComparison.Ordinal) && exitAfterRoleChanged)
                {
                    Log(jsonStdout, "exit_after_role_changed", new { eventCount, mirror_applied = mirrorHost?.AppliedCount ?? 0 });
                    return true;
                }

                if (maxEvents >= 0 && count >= maxEvents)
                {
                    Log(jsonStdout, "max_events_reached", new { eventCount, maxEvents, mirror_applied = mirrorHost?.AppliedCount ?? 0 });
                    return true;
                }

                return false;
            }

            await coordinator.RunAsync(
                dispatchQueue.Reader,
                mirrorEngine,
                jsonStdout,
                shutdown.Token,
                SendCommandAsync,
                TryMirrorDispatchAsync,
                AfterLineAsync,
                TryMirrorDispatchInteractionBatchAsync,
                TryMirrorDispatchInteractionReplayBatchAsync);

            // stop the socket receive task before the teardown writes below.
            await StopReceivingAsync();

            // Step 4: graceful LeaveSession. Skipped when --exit-after-role-changed is set
            // because integration tests need to inspect the post-SetClientRole session state,
            // and LeaveSession clears the per-player role entry as part of cleanup.
            if (!exitAfterRoleChanged && !exitAfterClaim && !exitAfterMirrorApplied.HasValue && !exitPositionSimId.HasValue)
            {
                // slice 6 trigger 3: best-effort lot export BEFORE LeaveSession so the
                // server can reconcile our client-native lot state into its canonical save while
                // the claims (needed by its ownership validator) still exist. Failures fall
                // through to the plain leave — the server treats an unmerged leave as accepted
                // data loss for the delta since the last checkpoint.
                await TrySubmitLotExportAsync(
                    jsonStdout,
                    "leave_session",
                    WriteCommandAsync,
                    CancellationToken.None);

                try
                {
                    Guid leaveId = Guid.NewGuid();
                    await WriteCommandAsync(new ClientCommand.LeaveSession(leaveId, playerId));
                    Log(jsonStdout, "sent", new { command = "leave_session", commandId = leaveId });
                }
                catch
                {
                    // Best-effort - the stream may already be torn down on Ctrl+C.
                }
            }

            // M4 P4.2.2.1: final mirror summary line for observability.
            if (mirrorHost != null)
            {
                Log(jsonStdout, "mirror_summary", new { applied = mirrorHost.AppliedCount });
            }

            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            Log(jsonStdout, "shutdown_requested", new { });
            return 0;
        }
        catch (OperationCanceledException ex)
        {
            Console.Error.WriteLine("Client operation canceled before shutdown was requested.");
            WriteSentryEventId(telemetry.ReportFailureOnce(
                new InvalidOperationException("Unexpected client cancellation.", ex),
                "client.unexpected_cancellation",
                new Dictionary<string, string?> { ["validation_gate"] = "receive-loop" }));
            return 1;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"IO error: {ex.Message}");
            WriteSentryEventId(telemetry.ReportFailureOnce(
                ex,
                "client.connection_lost",
                new Dictionary<string, string?> { ["validation_gate"] = "receive-loop" }));
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Client error: {ex.Message}");
            WriteSentryEventId(telemetry.ReportFailureOnce(
                ex,
                "client.unhandled",
                new Dictionary<string, string?> { ["validation_gate"] = "receive-loop" }));
            return 1;
        }
        finally
        {
            // every exit stops the receive task, a join that failed included (a second stop is a no-op).
            await StopReceivingAsync();
        }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
        catch (OperationCanceledException ex) when (!shutdown.IsCancellationRequested)
        {
            Console.Error.WriteLine("Client initialization canceled before shutdown was requested.");
            WriteSentryEventId(telemetry.ReportFailureOnce(
                new InvalidOperationException("Unexpected client initialization cancellation.", ex),
                "client.unexpected_cancellation",
                new Dictionary<string, string?> { ["validation_gate"] = "client-initialization" }));
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Client initialization failed: {ex.Message}");
            WriteSentryEventId(telemetry.ReportFailureOnce(
                ex,
                "client.initialization_failed",
                new Dictionary<string, string?> { ["validation_gate"] = "client-initialization" }));
            return 1;
        }
        finally
        {
            // T10: stop accepting admin commands before tearing down the bridge it wraps.
            clientState?.Transition("stopping");
            telemetry.AddBreadcrumb("client.stopping");
            if (clientAdminListener != null)
            {
                try { await clientAdminListener.DisposeAsync(); } catch { }
            }
            if (ownedClientHost != null)
            {
                if (intentHandler != null)
                {
                    ownedClientHost.Bridge.AuthoritativeEvent -= intentHandler;
                }
                try { await ownedClientHost.StopAsync(CancellationToken.None); } catch { }
                try { await ownedClientHost.DisposeAsync(); } catch { }
            }
            clientState?.Transition("stopped");
            telemetry.AddBreadcrumb("client.stopped");
            s_telemetry = null;
        }
    }

    /// <summary>
    /// slice 6 + snapshot the local client TS3, build the owned
    /// active-lot class A bundle (LotExportBuilder), attempt a deep household export
    /// (Household.ExportContent via bridge BULK slot) and submit it to the server
    /// (SubmitLotExport). No-ops when not in a client-native authority mode, when the owned
    /// TS3 host is not running, or when there is nothing exportable; failures are logged and
    /// swallowed (every trigger is best-effort on the client -- the server side enforces the
    /// checkpoint gate). When a server-requested deep export fails or produces no payload,
    /// <c>DeepExport</c> stays null and the server quarantines that request without merging or
    /// saving. Only unsolicited legacy bundles may use the position-only merge path.
    /// </summary>
    private static async Task TrySubmitLotExportAsync(
        bool jsonStdout,
        string reason,
        Func<ClientCommand, Task>? sendCommandAsync,
        CancellationToken cancellationToken)
    {
        if (sendCommandAsync == null)
        {
            return;
        }

        if (!LotLocalAuthorityMirrorGate.OwnsNativeSimulation(s_lotLocalAuthorityMode))
        {
            return;
        }

        Func<CancellationToken, Task<SimulationSnapshot>>? provider = s_lotExportSnapshotProvider;
        if (provider == null)
        {
            return;
        }

        try
        {
            SimulationSnapshot snapshot = await provider(cancellationToken);
            LotExportBundle? bundle = LotExportBuilder.BuildFromSnapshot(s_selfPlayerId, s_ownedSims, s_ownedActiveLotId, snapshot);
            if (bundle == null)
            {
                Log(jsonStdout, "lot_export_skipped", new { reason, ownedSims = s_ownedSims.Count });
                return;
            }

            HouseholdExportPayload? deepExport = null;
            HeadlessSims3Host? exportHost = s_exportHost;
            ulong? householdId = TryGetOwnedHouseholdId(snapshot, s_ownedSims);
            if (exportHost != null && householdId is ulong hhId)
            {
                try
                {
                    deepExport = await exportHost.ExportHouseholdContentAsync(hhId, cancellationToken);
                    if (deepExport != null)
                    {
                        Log(jsonStdout, "lot_export_deep_ok", new { reason, householdId = hhId, totalBytes = deepExport.PropertyStreamBytes.Length + deepExport.ResKeyTableBytes.Length + deepExport.ObjectIdTableBytes.Length, entryCount = deepExport.EntryCount });
                    }
                    else
                    {
                        Log(jsonStdout, "lot_export_deep_empty", new { reason, householdId = hhId });
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception deepEx)
                {
                    Log(jsonStdout, "lot_export_deep_failed", new { reason, householdId = hhId, error = deepEx.Message });
                }
            }

            LotExportBundle bundleWithDeep = deepExport != null
                ? bundle with { DeepExport = deepExport }
                : bundle;

            await sendCommandAsync(new ClientCommand.SubmitLotExport(Guid.NewGuid(), s_selfPlayerId, bundleWithDeep, s_lastReconciledVersion));
            Log(jsonStdout, "lot_export_submitted", new { reason, lotId = bundleWithDeep.LotId, sims = bundleWithDeep.Sims.Count, clientTick = bundleWithDeep.ClientTick, lastReconciledVersion = s_lastReconciledVersion, deepExport = deepExport != null });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log(jsonStdout, "lot_export_failed", new { reason, error = ex.Message });
        }
    }

    /// <summary>
    /// extract the household ID from the first owned Sim in the snapshot.
    /// Returns null if no owned Sim has a non-zero household ID.
    /// </summary>
    private static ulong? TryGetOwnedHouseholdId(SimulationSnapshot snapshot, IReadOnlySet<ulong> ownedSims)
    {
        foreach (SimSnapshot sim in snapshot.Sims)
        {
            if (ownedSims.Contains(sim.ObjectId) && sim.HouseholdId != 0)
            {
                return sim.HouseholdId;
            }
        }

        return null;
    }

    /// <summary>
    /// close-out (E4 re-sample 2026-10-30): when an owned Sim's in-flight order ends,
    /// deltas carrying its FINAL position may have arrived (and been stripped) inside the strip
    /// window, and an idle Sim is never re-listed by a changed-Sims delta again — leaving the
    /// puppet ~1 walk step behind (the stale Iliana pos rows). The DECISION (did this client track
    /// an order for this actor, and what is its final server truth) lives in
    /// <see cref="OrderCompletionReassert.TryBuildOnOrderCompleted"/>, one function the test suite
    /// drives end to end; this is only its dispatch + logging half, so a re-assert that reaches the
    /// game is always the one the tested path asked for.
    /// </summary>
    private static async Task DispatchFinalReassertAsync(
        MirrorEngine? mirrorEngine,
        SimulationSnapshot final,
        ulong actorObjectId,
        string reason,
        bool jsonStdout,
        CancellationToken cancellationToken)
    {
        if (mirrorEngine is null)
        {
            return;
        }

        try
        {
            await mirrorEngine.DispatchAsync(new ServerEvent.SnapshotDelta(final), cancellationToken);
            Log(jsonStdout, "owned_order_final_reassert", new { actor = actorObjectId, reason });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log(jsonStdout, "owned_order_final_reassert_failed", new { actor = actorObjectId, reason, error = ex.Message });
        }
    }

    /// <summary>
    /// M4 P4.2.2.1: best-effort dispatch of an incoming wire line through the
    /// mirror engine. Decode failures are silently swallowed (the line may not be a valid
    /// <see cref="ServerEvent"/> — e.g. unknown discriminator or non-JSON frame during
    /// reconnect). Per-command host failures inside <see cref="MirrorEngine"/> are
    /// already isolated by <c>DispatchResult.Errors</c>; we log a summary when any failed.
    /// Engine-level exceptions (e.g. <see cref="InvalidOperationException"/> for an
    /// unknown command type) are caught and logged but do NOT abort the receive loop —
    /// observability is best-effort by design for this slice. Cancellation propagates.
    /// </summary>
    private static async Task<bool> TryMirrorDispatchAsync(
        MirrorEngine? mirrorEngine,
        string line,
        bool jsonStdout,
        CancellationToken cancellationToken,
        Func<ClientCommand, Task>? sendCommandAsync = null)
    {
        if (s_logDispatched)
        {
            int stall = Interlocked.Exchange(ref s_stallFirstDispatchMs, 0);
            if (stall > 0)
            {
                await Task.Delay(stall, cancellationToken);
            }

            Log(jsonStdout, "dispatched", new { head = line.Length > 120 ? line[..120] : line });
        }

        if (mirrorEngine == null) return false;
        ServerEvent? decoded;
        try
        {
            decoded = PublicProtocolCodec.DecodeEvent(line);
        }
        catch
        {
            // Not a valid ServerEvent (unknown discriminator, malformed JSON, etc.). Skip.
            return false;
        }
        if (decoded == null) return false;
        s_claimControl.Observe(decoded, s_selfPlayerId);

        if (decoded is ServerEvent.FullSnapshot or ServerEvent.SnapshotDelta
            && DateTime.UtcNow - s_lastActiveSimAssertUtc >= ActiveSimReassertInterval)
        {
            await TryAssertActiveSimAsync(jsonStdout, cancellationToken);
        }

        long? ackTick = decoded switch
        {
            ServerEvent.FullSnapshot fs => fs.Snapshot.Tick,
            ServerEvent.SnapshotDelta sd => sd.Snapshot.Tick,
            _ => null
        };

        // M4 P4.2.5: maintain ownership state from SimClaimed/SimReleased broadcasts
        // so the self-id filter can identify self-owned Sims.
        switch (decoded)
        {
            case ServerEvent.SessionJoined sj:
                s_joinedPlayerCount++;
                RefreshLotLocalAuthorityMode();
                if (sj.PlayerId == s_selfPlayerId)
                {
                    // what the server said about itself. MirrorEngine.DispatchAsync, further down, turns
                    // interaction replay off when it lists presentation_stream.
                    Log(jsonStdout, "server_capabilities", new
                    {
                        capabilities = sj.Capabilities,
                        presentation_stream = ServerCapability.Has(sj.Capabilities, ServerCapability.PresentationStream)
                    });
                }

                if (sj.Notice != null && sj.PlayerId == s_selfPlayerId)
                {
                    // the server found this game's on-screen walk differs from its own.
                    Log(jsonStdout, "join_notice", new { notice = sj.Notice });
                    await TryNotifyAsync(sj.Notice, jsonStdout, cancellationToken);
                }
                break;
            case ServerEvent.LotLocalAuthorityModeChanged modeChanged when modeChanged.PlayerId == s_selfPlayerId:
                // slice 4: server pushes solo_native / prox_mirror; prox_mirror resumes foreign mirror only.
                ApplyLotLocalAuthorityModeFromServer(modeChanged.Mode);
                break;
            case ServerEvent.LotExportRequested exportRequested when exportRequested.PlayerId == s_selfPlayerId:
                // slice 6: server wants our owned active-lot class A bundle so a pending or
                // upcoming SAVE_GAME checkpoint (or mode exit) can reconcile the server TS3 first.
                await TrySubmitLotExportAsync(jsonStdout, exportRequested.Reason, sendCommandAsync, cancellationToken);
                break;
            case ServerEvent.LotReconcileAcknowledged ack when ack.PlayerId == s_selfPlayerId:
                // slice 5 (design §3.1): server confirmed a lot merge and assigned the
                // new monotonic checkpoint version. Track it so the next SubmitLotExport echoes it
                // back as ClientLastReconciledVersion, letting the server classify whether this client
                // saw the last merge (versions match -> no contestable fields) or missed it.
                s_lastReconciledVersion = ack.Version;
                Log(jsonStdout, "lot_reconcile_acked", new { lotId = ack.LotId, version = ack.Version });
                break;
            case ServerEvent.SimClaimed sc when sc.PlayerId == s_selfPlayerId:
                s_ownedSims.Add(sc.SimObjectId);
                s_telemetry?.AddBreadcrumb(
                    "client.claim_accepted",
                    new Dictionary<string, string?>
                    {
                        ["claimed_sim_id"] = sc.SimObjectId.ToString(CultureInfo.InvariantCulture),
                    });
                if (sc.LotId is ulong lotId)
                {
                    s_ownedActiveLotId = lotId;
                }

                if (s_intendedClaimSim == sc.SimObjectId)
                {
                    s_intendedClaimSim = null;
                }
                // under (claimed == run-local), claiming a Sim LEFT the mirrored set,
                // so the engine un-suppressed it here. Now the host is the sole autonomy authority and
                // a claimed Sim STAYS a suppressed render target (the owner renders the host, it does
                // not run free local AI). Un-suppressing on claim would briefly re-enable the owner's
                // local AI for one snapshot interval (the flicker) before the controller
                // re-suppressed it on the next delta. So we DO NOT un-suppress: the Sim's first-sight
                // suppression (and its single outstanding client-side autonomy-disable) simply persists
                // across the claim, leak-free.
                await TryNotifyAsync($"You claimed Sim {sc.SimObjectId}", jsonStdout, cancellationToken);
                await TryAssertActiveSimAsync(jsonStdout, cancellationToken);
                RefreshLotLocalAuthorityMode();
                await MaybeBootstrapSoloNativeAsync(mirrorEngine, jsonStdout, cancellationToken);
                break;
            case ServerEvent.SimReleased sr when sr.PlayerId == s_selfPlayerId:
                s_ownedSims.Remove(sr.SimObjectId);
                s_telemetry?.AddBreadcrumb(
                    "client.claim_released",
                    new Dictionary<string, string?>
                    {
                        ["claimed_sim_id"] = sr.SimObjectId.ToString(CultureInfo.InvariantCulture),
                    });
                RefreshLotLocalAuthorityMode();
                s_soloNativeAutonomyBootstrapped = false;
                if (s_ownedSims.Count == 0)
                {
                    s_ownedActiveLotId = null;
                }
                // a released Sim becomes an unclaimed mirrored townie — which the owner ALSO
                // renders from the host with local autonomy suppressed. So, symmetrically with claim,
                // we DO NOT un-suppress on release: the Sim is already first-sight-suppressed and stays
                // that way seamlessly (no wake/re-suppress flicker, no leaked autonomy-disable).
                await TryNotifyAsync($"You released Sim {sr.SimObjectId}", jsonStdout, cancellationToken);
                break;
            case ServerEvent.DialogRequested dialog:
                // a dialog of this player's Sim on the server: show it here; the answer comes back
                // through the local mod (EVENT|DialogAnswer).
                await TryShowRemoteDialogAsync(dialog, jsonStdout, cancellationToken);
                return true;
            case ServerEvent.ClientRoleChanged roleChanged when roleChanged.PlayerId == s_selfPlayerId:
                s_telemetry?.AddBreadcrumb(
                    "client.role_accepted",
                    new Dictionary<string, string?> { ["client_role"] = roleChanged.Role.ToString() });
                break;
            case ServerEvent.CommandRejected rejected:
                // a rejected forwarded intent must not linger and match a later echo.
                s_pendingIntents.RemoveByCommandId(rejected.CommandId);
                if (s_forwardedPicks.TryRemove(rejected.CommandId, out string? pickName))
                {
                    // tell the player their pick was refused.
                    string text = RejectionText.ForPick(pickName, rejected.Reason);
                    await TryNotifyAsync(text, jsonStdout, cancellationToken);
                    Log(jsonStdout, "pick_refused_shown", new { commandId = rejected.CommandId, text });
                }
                break;
            case ServerEvent.InteractionCompleted completed when s_ownedSims.Contains(completed.ActorObjectId):
                // the directed order finished — resume position-mirror for this Sim so a
                // post-walk divergence still corrects (don't strand it permanently unmirrored), and
                // re-assert the final position the strip window may have swallowed.
                if (OrderCompletionReassert.TryBuildOnOrderCompleted(
                        s_activeOrders, s_latestSnapshot, completed.ActorObjectId, out SimulationSnapshot? completedFinal))
                {
                    Log(jsonStdout, "owned_order_mirror_resumed", new { actor = completed.ActorObjectId, reason = "completed" });
                    await DispatchFinalReassertAsync(
                        mirrorEngine, completedFinal!, completed.ActorObjectId, "completed", jsonStdout, cancellationToken);
                }
                break;
            case ServerEvent.InteractionFailed failed when s_ownedSims.Contains(failed.ActorObjectId):
                if (OrderCompletionReassert.TryBuildOnOrderCompleted(
                        s_activeOrders, s_latestSnapshot, failed.ActorObjectId, out SimulationSnapshot? failedFinal))
                {
                    Log(jsonStdout, "owned_order_mirror_resumed", new { actor = failed.ActorObjectId, reason = "failed" });
                    await DispatchFinalReassertAsync(
                        mirrorEngine, failedFinal!, failed.ActorObjectId, "failed", jsonStdout, cancellationToken);
                }
                break;
        }

        if (decoded is ServerEvent.FullSnapshot fsModeCache)
        {
            CacheOwnedLotIdsFromSnapshot(fsModeCache.Snapshot.Sims);
            s_latestSnapshotSims = fsModeCache.Snapshot.Sims;
            s_latestSnapshot = fsModeCache.Snapshot;
            RefreshLotLocalAuthorityMode();
            await MaybeBootstrapSoloNativeAsync(mirrorEngine, jsonStdout, cancellationToken);
        }
        else if (decoded is ServerEvent.SnapshotDelta sdModeCache)
        {
            CacheOwnedLotIdsFromSnapshot(sdModeCache.Snapshot.Sims);
            s_latestSnapshotSims = sdModeCache.Snapshot.Sims;
            s_latestSnapshot = sdModeCache.Snapshot;
            RefreshLotLocalAuthorityMode();
            await MaybeBootstrapSoloNativeAsync(mirrorEngine, jsonStdout, cancellationToken);
        }

        // W2: claims and the lot-local mode are now current for this event; keep the local TS3's
        // authority gate in step (sends only on change).
        await SyncClientAuthorityAsync(mirrorEngine, jsonStdout, cancellationToken);

        // M4 P4.2.8: per-Sim snapshot filter. Strips self-owned Sims from
        // FullSnapshot/SnapshotDelta before dispatch.
        //
        // solo_native: strip ALL owned Sims (zero mirror). Otherwise active-order strip.
        // with the presentation stream this client simulates nothing, its own Sim included, so
        // no Sim is stripped: the server's positions apply to every puppet (an owned Sim stripped while
        // the server ran its interactions drifted 2.5 m, cold-test-045).
        IReadOnlySet<ulong> positionMirrorStripSet = !mirrorEngine.InteractionReplayEnabled
            ? new HashSet<ulong>()
            : LotLocalAuthorityMirrorGate.ComputePositionMirrorStripSet(
                s_lotLocalAuthorityMode,
                s_ownedSims,
                s_activeOrders.ActiveActorsSnapshot(DateTime.UtcNow));
        decoded = MirrorEventFilter.FilterSnapshotSims(decoded, positionMirrorStripSet);

        // M4 P4.5 / P5.6 LP-4: drift detection — compare new positions against last-seen before updating.
        // FullSnapshot is an authoritative reset; sparse SnapshotDelta tick gaps are normal sync.
        bool driftDetected = false;
        if (decoded is ServerEvent.FullSnapshot fsD)
        {
            DriftDetector.UpdatePositions(s_serverPositions, fsD.Snapshot.Sims);
            s_lastSnapshotTick = fsD.Snapshot.Tick;
        }
        else if (decoded is ServerEvent.SnapshotDelta sdD)
        {
            driftDetected = DriftDetector.HasDrift(
                s_serverPositions,
                sdD.Snapshot.Sims,
                s_lastSnapshotTick,
                sdD.Snapshot.Tick,
                DriftThreshold);
            DriftDetector.UpdatePositions(s_serverPositions, sdD.Snapshot.Sims);
            s_lastSnapshotTick = sdD.Snapshot.Tick;
            if (driftDetected)
            {
                s_telemetry?.AddBreadcrumb(
                    "mirror.cadence_drift",
                    new Dictionary<string, string?> { ["validation_gate"] = "mirror-cadence" },
                    global::Sentry.BreadcrumbLevel.Warning);
            }
        }

        // M4 P4.2.5: per-client self-id filter. Skip events that target self-owned Sims
        // (SimAutonomyChanged suppressing autonomy on own Sim; InteractionQueued echo of own command).
        MirrorEventFilter.SkipDecision skipDecision = MirrorEventFilter.ShouldSkip(decoded, s_selfPlayerId, s_ownedSims);

        // the presentation stream replaces interaction replay entirely. The owned-Sim and
        // book-chain replay paths below send bridge commands of their own (around the MirrorEngine
        // switch) and mark owned Sims as having an order in flight; skip them.
        if (!mirrorEngine.InteractionReplayEnabled
            && decoded is ServerEvent.InteractionQueued or ServerEvent.InteractionReplayQueued)
        {
            return driftDetected;
        }

        // (pure-puppet Phase 0): an InteractionQueued for our OWN claimed Sim must be REPLAYED,
        // not dropped. The P4.2.5 owned-actor skip assumed every such event was the client's own command
        // echoing back (a duplicate to drop). That is only half true:
        //   * OWN ECHO — P4.4 capture DELETED the local interaction, so the echo that matches a forwarded
        //     intent is the player's directed order coming back as the confirmation to re-apply.
        //   * HOST-ORIGIN — an autonomous (or otherwise host-authoritative) interaction the host ran on
        //     our claimed Sim has NO matching pending intent. The old code dropped it, so the puppet
        //     converged by position but ran a divergent interaction (the ReadBook-vs-RouteAway
        //     split). Replay it too: faithfully mirroring the host's chosen interaction is sync, not
        //     divergence — it never re-enables free local autonomy.
        // ShouldSkip stays the pure source of truth (it still returns Skip); the client owns this override.
        // Also gate pre-claim (--claim-sim) host interactions before SimClaimed arrives ( boot race).
        if (decoded is ServerEvent.InteractionQueued iqOwned && IsOwnedOrClaimPending(iqOwned.ActorObjectId))
        {
            if (LotLocalAuthorityMirrorGate.ShouldSuppressOwnedMirror(
                    s_lotLocalAuthorityMode,
                    s_ownedSims,
                    iqOwned.ActorObjectId))
            {
                Log(jsonStdout, "mirror_skipped", new { event_type = decoded.GetType().Name, reason = "lot_local-owned-sim" });
                return driftDetected;
            }

            OwnedInteractionReplayDecision replay = EvaluateOwnedInteractionReplay(iqOwned, jsonStdout);
            switch (replay)
            {
                case OwnedInteractionReplayDecision.NoReplay:
                    return driftDetected;
                case OwnedInteractionReplayDecision.DeferredSit:
                    return driftDetected;
                case OwnedInteractionReplayDecision.AwaitingSitForBookChainBatch:
                    SchedulePendingReadBookIqFlush(mirrorEngine, iqOwned.ActorObjectId, jsonStdout, cancellationToken);
                    return driftDetected;
                case OwnedInteractionReplayDecision.FlushPendingBookChainReplayBatch:
                    await FlushPendingBookChainAsync(mirrorEngine, iqOwned, jsonStdout, cancellationToken);
                    return driftDetected;
                case OwnedInteractionReplayDecision.BatchReadBookWithSit:
                    await DispatchOwnedBookChainBatchAsync(
                        mirrorEngine, iqOwned, jsonStdout, cancellationToken);
                    return driftDetected;
                case OwnedInteractionReplayDecision.SeatCorrectionAfterBookChain:
                    await DispatchPostBookChainSeatCorrectionAsync(
                        mirrorEngine, iqOwned, jsonStdout, cancellationToken);
                    return driftDetected;
                case OwnedInteractionReplayDecision.ReplaySingle:
                    if (SimsOnlineServerHost.InteractionReplayApplySorter.IsReadBookTerminalHead(iqOwned.InteractionKey))
                    {
                        await DispatchOwnedReadBookBatchAsync(mirrorEngine, iqOwned, jsonStdout, cancellationToken);
                        return driftDetected;
                    }

                    skipDecision = new MirrorEventFilter.SkipDecision(false, "");
                    break;
            }
        }

        if (decoded is ServerEvent.InteractionReplayQueued irqOwned
            && IsOwnedOrClaimPending(irqOwned.Bundle.ActorObjectId))
        {
            if (LotLocalAuthorityMirrorGate.ShouldSuppressOwnedMirror(
                    s_lotLocalAuthorityMode,
                    s_ownedSims,
                    irqOwned.Bundle.ActorObjectId))
            {
                Log(jsonStdout, "mirror_skipped", new { event_type = decoded.GetType().Name, reason = "lot_local-owned-sim" });
                return driftDetected;
            }

            OwnedInteractionReplayDecision replay = EvaluateOwnedInteractionReplayBundle(irqOwned.Bundle, jsonStdout);
            switch (replay)
            {
                case OwnedInteractionReplayDecision.NoReplay:
                    return driftDetected;
                case OwnedInteractionReplayDecision.DeferredSit:
                    return driftDetected;
                case OwnedInteractionReplayDecision.AwaitingSitForBookChainBatch:
                    SchedulePendingReadBookReplayFlush(mirrorEngine, irqOwned.Bundle.ActorObjectId, jsonStdout, cancellationToken);
                    return driftDetected;
                case OwnedInteractionReplayDecision.FlushPendingBookChainReplayBatch:
                    if (s_pendingReadBookForBookChain.ContainsKey(irqOwned.Bundle.ActorObjectId))
                    {
                        await FlushPendingBookChainMixedAsync(mirrorEngine, irqOwned.Bundle, jsonStdout, cancellationToken);
                    }
                    else
                    {
                        await FlushPendingReplayBookChainAsync(mirrorEngine, irqOwned.Bundle, jsonStdout, cancellationToken);
                    }
                    return driftDetected;
                case OwnedInteractionReplayDecision.BatchReadBookWithSit:
                    await DispatchOwnedReplayBookChainBatchAsync(
                        mirrorEngine, irqOwned.Bundle, jsonStdout, cancellationToken);
                    return driftDetected;
                case OwnedInteractionReplayDecision.SeatCorrectionAfterBookChain:
                    await DispatchPostBookChainSeatCorrectionAsync(
                        mirrorEngine, irqOwned.Bundle, jsonStdout, cancellationToken);
                    return driftDetected;
                case OwnedInteractionReplayDecision.ReplaySingle:
                    if (SimsOnlineServerHost.InteractionReplayApplySorter.IsReadBookTerminalHead(irqOwned.Bundle.InteractionKey))
                    {
                        await DispatchOwnedReplayReadBookBatchAsync(mirrorEngine, irqOwned.Bundle, jsonStdout, cancellationToken);
                        return driftDetected;
                    }

                    skipDecision = new MirrorEventFilter.SkipDecision(false, "");
                    break;
            }
        }

        if (skipDecision.Skip)
        {
            Log(jsonStdout, "mirror_skipped", new { event_type = decoded.GetType().Name, reason = skipDecision.Reason });
            return driftDetected;
        }

        if (decoded is ServerEvent.InteractionReplayQueued irqForeign
            && !IsOwnedOrClaimPending(irqForeign.Bundle.ActorObjectId))
        {
            InteractionReplayBundle foreignBundle = irqForeign.Bundle;
            if (SimsOnlineServerHost.InteractionReplayApplySorter.IsGetBookPrecursor(foreignBundle.InteractionKey))
            {
                s_deferredGetBookReplayBundle[foreignBundle.ActorObjectId] = foreignBundle;
                Log(jsonStdout, "foreign_replay_deferred",
                    new { actor = foreignBundle.ActorObjectId, key = foreignBundle.InteractionKey, reason = "getbook-deferred-for-readbook-replay" });
                return driftDetected;
            }

            if (SimsOnlineServerHost.InteractionReplayApplySorter.IsReadBookTerminalHead(foreignBundle.InteractionKey)
                && (s_deferredSitReplayBundle.ContainsKey(foreignBundle.ActorObjectId)
                    || s_deferredGetBookReplayBundle.ContainsKey(foreignBundle.ActorObjectId)))
            {
                await DispatchOwnedReplayBookChainBatchAsync(mirrorEngine!, foreignBundle, jsonStdout, cancellationToken);
                return driftDetected;
            }

            if (IsBookChainSitDeferCandidate(foreignBundle))
            {
                s_deferredSitReplayBundle[foreignBundle.ActorObjectId] = foreignBundle;
                Log(jsonStdout, "foreign_replay_deferred",
                    new { actor = foreignBundle.ActorObjectId, key = foreignBundle.InteractionKey, reason = "sit-deferred-for-readbook-replay" });
                return driftDetected;
            }
        }

        string commandType = decoded.GetType().Name;
        bool claimedActor = decoded switch
        {
            ServerEvent.InteractionQueued queued => IsOwnedOrClaimPending(queued.ActorObjectId),
            ServerEvent.InteractionReplayQueued replay => IsOwnedOrClaimPending(replay.Bundle.ActorObjectId),
            _ => false,
        };
        IReadOnlyDictionary<string, string?> telemetryAttributes =
            SimsOnlineTelemetryMetadata.ForMirrorEvent(decoded, claimedActor);
        using SimsOnlineTelemetryTransaction? mirrorTransaction =
            s_telemetry?.StartTransaction("mirror.dispatch", "mirror.dispatch");
        using SimsOnlineTelemetrySpan? mirrorSpan =
            mirrorTransaction?.StartChild("mirror.apply", commandType);
        try
        {
            DispatchResult result = await mirrorEngine.DispatchAsync(decoded, cancellationToken);
            bool actionableFailure = result.AnyFailed && !IsEphemeralAutonomyOnlyFailure(decoded, result);
            if (actionableFailure)
            {
                Log(jsonStdout, "mirror_dispatch_partial_failure", new
                {
                    command_type = commandType,
                    translated = result.Translated,
                    succeeded = result.Succeeded,
                    failed = result.Failed,
                    first_error = result.Errors.Count > 0 ? result.Errors[0].Message : null
                });
                ReportMirrorPartialFailureOnce(
                    result,
                    decoded,
                    telemetryAttributes);
            }
            mirrorSpan?.Complete(succeeded: !actionableFailure);
            mirrorTransaction?.Complete(succeeded: !actionableFailure);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log(jsonStdout, "mirror_dispatch_failed", new { error = ex.Message, command_type = commandType });
            ReportMirrorFailureOnce(
                ex,
                SimsOnlineTelemetryMetadata.IsTargetResolutionFailure(decoded, [ex])
                    ? "mirror.target_resolution_failed"
                    : "mirror.dispatch_failed",
                telemetryAttributes);
            mirrorSpan?.Complete(succeeded: false);
            mirrorTransaction?.Complete(succeeded: false);
        }

        // P5.4: confirm snapshot/delta consumption to the server (fire-and-forget on wire).
        if (ackTick is { } tick && sendCommandAsync != null)
        {
            try
            {
                Guid ackId = Guid.NewGuid();
                await sendCommandAsync(new ClientCommand.AckSnapshot(ackId, s_selfPlayerId, tick));
                Log(jsonStdout, "snapshot_ack_sent", new { commandId = ackId, tick });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log(jsonStdout, "snapshot_ack_failed", new { error = ex.Message, tick });
            }
        }

        return driftDetected;
    }

    /// <summary>
    /// filter and dispatch a coalesced InteractionQueued batch through one or more
    /// APPLY_INTERACTIONS bridge frames instead of N serial round-trips.
    /// </summary>
    private static async Task<bool> TryMirrorDispatchInteractionBatchAsync(
        MirrorEngine? mirrorEngine,
        IReadOnlyList<ServerEvent.InteractionQueued> interactions,
        bool jsonStdout,
        CancellationToken cancellationToken,
        Func<ClientCommand, Task>? sendCommandAsync = null)
    {
        // with the presentation stream active, interactions are not replayed (one simulator); the
        // coordinator's batches would send replay commands of their own around the MirrorEngine switch.
        if (mirrorEngine == null || interactions.Count == 0 || !mirrorEngine.InteractionReplayEnabled)
        {
            return false;
        }

        HashSet<ulong> bookChainHandled = new();

        // When Sit+ReadBook land in the same coordinator flush, batch immediately (no lone-Sit apply).
        Dictionary<ulong, ServerEvent.InteractionQueued> getBookInBatch = new();
        Dictionary<ulong, ServerEvent.InteractionQueued> sitInBatch = new();
        Dictionary<ulong, ServerEvent.InteractionQueued> readInBatch = new();
        foreach (ServerEvent.InteractionQueued iq in interactions)
        {
            if (!IsOwnedOrClaimPending(iq.ActorObjectId))
            {
                continue;
            }

            if (SimsOnlineServerHost.InteractionReplayApplySorter.IsGetBookPrecursor(iq.InteractionKey))
            {
                getBookInBatch[iq.ActorObjectId] = iq;
            }

            if (SimsOnlineServerHost.InteractionReplayApplySorter.IsSitTransitionDeferred(iq.InteractionKey))
            {
                sitInBatch[iq.ActorObjectId] = iq;
            }

            if (SimsOnlineServerHost.InteractionReplayApplySorter.IsReadBookTerminalHead(iq.InteractionKey))
            {
                readInBatch[iq.ActorObjectId] = iq;
            }
        }

        foreach (KeyValuePair<ulong, ServerEvent.InteractionQueued> readPair in readInBatch)
        {
            List<ServerEvent.InteractionQueued> chainItems = new(3);
            if (getBookInBatch.TryGetValue(readPair.Key, out ServerEvent.InteractionQueued? getBook))
            {
                s_deferredGetBookForBookChain.Remove(readPair.Key);
                chainItems.Add(getBook);
            }
            else if (s_deferredGetBookForBookChain.Remove(readPair.Key, out ServerEvent.InteractionQueued? deferredGetBook))
            {
                chainItems.Add(deferredGetBook);
            }

            if (sitInBatch.TryGetValue(readPair.Key, out ServerEvent.InteractionQueued? sit))
            {
                s_deferredSitForBookChain.Remove(readPair.Key);
                chainItems.Add(sit);
            }
            else if (s_deferredSitForBookChain.Remove(readPair.Key, out ServerEvent.InteractionQueued? deferredSit))
            {
                chainItems.Add(deferredSit);
            }

            chainItems.Add(readPair.Value);
            IReadOnlyList<ServerEvent.InteractionQueued> chainBatch =
                InteractionQueuedCoalescer.CoalesceForBatch(chainItems);
            Log(jsonStdout, "book_chain_batch_dispatch", new { actor = readPair.Key, count = chainBatch.Count, source = "coordinator-flush" });
            await DispatchBookChainBatchAsync(mirrorEngine, chainBatch, jsonStdout, cancellationToken, "InteractionQueuedBookChain");

            bookChainHandled.Add(readPair.Key);
        }

        List<ServerEvent.InteractionQueued> replayable = new(interactions.Count);
        foreach (ServerEvent.InteractionQueued iq in interactions)
        {
            if (bookChainHandled.Contains(iq.ActorObjectId)
                && (SimsOnlineServerHost.InteractionReplayApplySorter.IsGetBookPrecursor(iq.InteractionKey)
                    || SimsOnlineServerHost.InteractionReplayApplySorter.IsSitTransitionDeferred(iq.InteractionKey)
                    || SimsOnlineServerHost.InteractionReplayApplySorter.IsReadBookTerminalHead(iq.InteractionKey)))
            {
                continue;
            }

            if (IsOwnedOrClaimPending(iq.ActorObjectId))
            {
                OwnedInteractionReplayDecision replay = EvaluateOwnedInteractionReplay(iq, jsonStdout);
                switch (replay)
                {
                    case OwnedInteractionReplayDecision.ReplaySingle:
                        replayable.Add(iq);
                        break;
                    case OwnedInteractionReplayDecision.AwaitingSitForBookChainBatch:
                        SchedulePendingReadBookIqFlush(mirrorEngine, iq.ActorObjectId, jsonStdout, cancellationToken);
                        break;
                    case OwnedInteractionReplayDecision.FlushPendingBookChainReplayBatch:
                        await FlushPendingBookChainAsync(mirrorEngine, iq, jsonStdout, cancellationToken);
                        break;
                    case OwnedInteractionReplayDecision.BatchReadBookWithSit:
                        await DispatchOwnedBookChainBatchAsync(mirrorEngine, iq, jsonStdout, cancellationToken);
                        break;
                    case OwnedInteractionReplayDecision.SeatCorrectionAfterBookChain:
                        await DispatchPostBookChainSeatCorrectionAsync(mirrorEngine, iq, jsonStdout, cancellationToken);
                        break;
                }

                continue;
            }

            MirrorEventFilter.SkipDecision skipDecision = MirrorEventFilter.ShouldSkip(iq, s_selfPlayerId, s_ownedSims);
            if (skipDecision.Skip || !EventToBridgeCommandTranslator.IsInteractionReplayCandidate(iq))
            {
                continue;
            }

            replayable.Add(iq);
        }

        if (replayable.Count == 0)
        {
            return false;
        }

        if (replayable.Count == 1)
        {
            string json = JsonSerializer.Serialize((ServerEvent)replayable[0], PublicProtocolCodec.JsonOptions);
            return await TryMirrorDispatchAsync(mirrorEngine, json, jsonStdout, cancellationToken, sendCommandAsync);
        }

        const string commandType = "InteractionQueuedBatch";
        using SimsOnlineTelemetryTransaction? mirrorTransaction =
            s_telemetry?.StartTransaction("mirror.dispatch", "mirror.dispatch");
        using SimsOnlineTelemetrySpan? mirrorSpan =
            mirrorTransaction?.StartChild("mirror.apply", commandType);
        try
        {
            DispatchResult result = await mirrorEngine.DispatchInteractionBatchAsync(replayable, cancellationToken);
            if (result.AnyFailed)
            {
                Log(jsonStdout, "mirror_dispatch_partial_failure", new
                {
                    command_type = commandType,
                    translated = result.Translated,
                    succeeded = result.Succeeded,
                    failed = result.Failed,
                    first_error = result.Errors.Count > 0 ? result.Errors[0].Message : null
                });
                ReportMirrorPartialFailureOnce(result, commandType);
            }
            mirrorSpan?.Complete(succeeded: !result.AnyFailed);
            mirrorTransaction?.Complete(succeeded: !result.AnyFailed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log(jsonStdout, "mirror_dispatch_failed", new { error = ex.Message, command_type = commandType });
            ReportMirrorFailureOnce(ex, "mirror.dispatch_failed", commandType);
            mirrorSpan?.Complete(succeeded: false);
            mirrorTransaction?.Complete(succeeded: false);
        }

        return false;
    }

    private static async Task<bool> TryMirrorDispatchInteractionReplayBatchAsync(
        MirrorEngine? mirrorEngine,
        IReadOnlyList<ServerEvent.InteractionReplayQueued> interactions,
        bool jsonStdout,
        CancellationToken cancellationToken,
        Func<ClientCommand, Task>? sendCommandAsync = null)
    {
        // with the presentation stream active, interactions are not replayed (one simulator); the
        // coordinator's batches would send replay commands of their own around the MirrorEngine switch.
        if (mirrorEngine == null || interactions.Count == 0 || !mirrorEngine.InteractionReplayEnabled)
        {
            return false;
        }

        HashSet<ulong> bookChainHandled = new();
        Dictionary<ulong, InteractionReplayBundle> getBookInBatch = new();
        Dictionary<ulong, InteractionReplayBundle> sitInBatch = new();
        Dictionary<ulong, InteractionReplayBundle> readInBatch = new();
        foreach (ServerEvent.InteractionReplayQueued irq in interactions)
        {
            InteractionReplayBundle bundle = irq.Bundle;

            if (SimsOnlineServerHost.InteractionReplayApplySorter.IsGetBookPrecursor(bundle.InteractionKey))
            {
                getBookInBatch[bundle.ActorObjectId] = bundle;
            }

            if (SimsOnlineServerHost.InteractionReplayApplySorter.IsSitTransitionDeferred(bundle.InteractionKey)
                || EventToBridgeCommandTranslator.IsForceAlignCandidate(bundle))
            {
                sitInBatch[bundle.ActorObjectId] = bundle;
            }

            if (SimsOnlineServerHost.InteractionReplayApplySorter.IsReadBookTerminalHead(bundle.InteractionKey))
            {
                readInBatch[bundle.ActorObjectId] = bundle;
            }
        }

        foreach (KeyValuePair<ulong, InteractionReplayBundle> readPair in readInBatch)
        {
            List<InteractionReplayBundle> chainItems = new(3);
            if (getBookInBatch.TryGetValue(readPair.Key, out InteractionReplayBundle? getBook))
            {
                s_deferredGetBookReplayBundle.Remove(readPair.Key);
                chainItems.Add(getBook);
            }
            else if (s_deferredGetBookReplayBundle.Remove(readPair.Key, out InteractionReplayBundle? deferredGetBook))
            {
                chainItems.Add(deferredGetBook);
            }

            if (sitInBatch.TryGetValue(readPair.Key, out InteractionReplayBundle? sit))
            {
                s_deferredSitReplayBundle.Remove(readPair.Key);
                chainItems.Add(sit);
            }
            else if (s_deferredSitReplayBundle.Remove(readPair.Key, out InteractionReplayBundle? deferredSit))
            {
                chainItems.Add(deferredSit);
            }

            chainItems.Add(readPair.Value);
            Log(jsonStdout, "book_chain_replay_batch_dispatch", new { actor = readPair.Key, count = chainItems.Count, source = "coordinator-flush" });
            await DispatchReplayBookChainBatchAsync(mirrorEngine, chainItems, jsonStdout, cancellationToken, "InteractionReplayQueuedBookChain");
            bookChainHandled.Add(readPair.Key);
        }

        bool driftDetected = false;
        foreach (ServerEvent.InteractionReplayQueued irq in interactions)
        {
            InteractionReplayBundle bundle = irq.Bundle;
            if (bookChainHandled.Contains(bundle.ActorObjectId)
                && (SimsOnlineServerHost.InteractionReplayApplySorter.IsGetBookPrecursor(bundle.InteractionKey)
                    || SimsOnlineServerHost.InteractionReplayApplySorter.IsSitTransitionDeferred(bundle.InteractionKey)
                    || SimsOnlineServerHost.InteractionReplayApplySorter.IsReadBookTerminalHead(bundle.InteractionKey)))
            {
                continue;
            }

            string json = JsonSerializer.Serialize((ServerEvent)irq, PublicProtocolCodec.JsonOptions);
            driftDetected |= await TryMirrorDispatchAsync(mirrorEngine, json, jsonStdout, cancellationToken, sendCommandAsync);
        }

        return driftDetected;
    }

    private enum OwnedInteractionReplayDecision
    {
        NoReplay,
        DeferredSit,
        BatchReadBookWithSit,
        AwaitingSitForBookChainBatch,
        FlushPendingBookChainReplayBatch,
        SeatCorrectionAfterBookChain,
        ReplaySingle
    }

    private static void ApplyLotLocalAuthorityModeFromServer(LotLocalAuthorityMode mode)
    {
        // PR#15 review fix 1: server mode is authoritative from this point on, even if it
        // matches the current local value — mark it received so later snapshots do not re-derive
        // (and downgrade) it via the owned-sims-only heuristic.
        s_serverLotLocalAuthorityModeReceived = true;

        if (s_lotLocalAuthorityMode == mode)
        {
            return;
        }

        if (!LotLocalAuthorityMirrorGate.OwnsNativeSimulation(mode))
        {
            s_soloNativeAutonomyBootstrapped = false;
        }

        s_lotLocalAuthorityMode = mode;
    }

    private static void RefreshLotLocalAuthorityMode()
    {
        // PR#15 review fix 1: once the server has pushed an authoritative mode, it owns the
        // value. The local owned-sims-only heuristic below cannot see foreign players' claims (a
        // co-presence prox_mirror looks like a solo full_puppet_fallback from here), so re-running it
        // on every FullSnapshot/SnapshotDelta clobbered the server's prox_mirror/solo_native. Skip
        // the local derivation entirely and defer to the server-provided mode.
        if (!LotLocalAuthorityClientPolicy.ShouldDeriveClientModeLocally(s_serverLotLocalAuthorityModeReceived))
        {
            return;
        }

        if (s_ownedSims.Count == 0)
        {
            s_lotLocalAuthorityMode = LotLocalAuthorityMode.FullPuppetFallback;
            return;
        }

        Dictionary<ulong, int> claims = new();
        foreach (ulong simId in s_ownedSims)
        {
            claims[simId] = s_selfPlayerId;
        }

        LotLocalAuthorityContext context = new(
            JoinedPlayerCount: Math.Max(1, s_joinedPlayerCount),
            PlayerId: s_selfPlayerId,
            Claims: claims,
            Sims: BuildSimsForAuthorityDetection());
        s_lotLocalAuthorityMode = LotLocalAuthorityDetector.Detect(context);
    }

    private static void CacheOwnedLotIdsFromSnapshot(IReadOnlyList<SimSnapshot> sims)
    {
        foreach (ulong ownedId in s_ownedSims)
        {
            for (int i = 0; i < sims.Count; i++)
            {
                SimSnapshot sim = sims[i];
                if (sim.ObjectId == ownedId && sim.LotId is ulong lotId)
                {
                    s_ownedActiveLotId = lotId;
                    return;
                }
            }
        }
    }

    /// <summary>
    /// wire snapshots strip owned Sims when solo_native; keep lot id for mode detection.
    /// </summary>
    private static IReadOnlyList<SimSnapshot> BuildSimsForAuthorityDetection()
    {
        if (s_ownedSims.Count == 0 || s_ownedActiveLotId is not ulong lotId)
        {
            return s_latestSnapshotSims;
        }

        foreach (ulong ownedId in s_ownedSims)
        {
            for (int i = 0; i < s_latestSnapshotSims.Count; i++)
            {
                if (s_latestSnapshotSims[i].ObjectId == ownedId)
                {
                    return s_latestSnapshotSims;
                }
            }
        }

        List<SimSnapshot> merged = new(s_latestSnapshotSims.Count + s_ownedSims.Count);
        merged.AddRange(s_latestSnapshotSims);
        foreach (ulong ownedId in s_ownedSims)
        {
            merged.Add(new SimSnapshot(
                ownedId,
                string.Empty,
                0,
                new Vector3Dto(0, 0, 0),
                CurrentInteractionId: null,
                OwningPlayerId: s_selfPlayerId,
                LotId: lotId));
        }

        return merged;
    }

    /// <summary>
    /// re-enable client TS3 native autonomy for owned Sims when solo_native.
    /// </summary>
    private static async Task MaybeBootstrapSoloNativeAsync(
        MirrorEngine? mirrorEngine,
        bool jsonStdout,
        CancellationToken cancellationToken)
    {
        if (mirrorEngine == null
            || !LotLocalAuthorityMirrorGate.OwnsNativeSimulation(s_lotLocalAuthorityMode)
            || s_ownedSims.Count == 0
            || s_soloNativeAutonomyBootstrapped)
        {
            return;
        }

        IMirrorHost? host = s_mirrorHost;
        if (host == null)
        {
            return;
        }

        foreach (ulong simId in s_ownedSims)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await host.ApplyAutonomyAsync(simId, disabled: false, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log(jsonStdout, "solo_native_autonomy_bootstrap_failed", new { simId, error = ex.Message });
            }
        }

        s_soloNativeAutonomyBootstrapped = true;
        Log(jsonStdout, "lot_local_native_bootstrap", new { mode = s_lotLocalAuthorityMode.ToString(), owned = s_ownedSims.Count });
    }

    /// <summary>
    /// W2: send SET_AUTHORITY to the local TS3 when the gate state (configured mode + Sims this
    /// client runs natively) changed. Best-effort: a failure is logged and retried after
    /// ClientAuthorityPolicy.RetryAfterFailure; mirroring never depends on it.
    /// </summary>
    private static async Task SyncClientAuthorityAsync(MirrorEngine? mirrorEngine, bool jsonStdout, CancellationToken cancellationToken)
    {
        IMirrorHost? host = s_mirrorHost;
        if (host == null)
        {
            return;
        }

        ClientAuthorityState state = ClientAuthorityPolicy.Compute(s_authorityGateMode, s_lotLocalAuthorityMode, s_ownedSims);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!ClientAuthorityPolicy.ShouldPublish(state, s_publishedAuthority, s_failedAuthority, s_failedAuthorityUtc, now, ClientAuthorityPolicy.RetryAfterFailure))
        {
            return;
        }

        try
        {
            string? report = await host.SetAuthorityAsync(state.Mode, state.NativeSimObjectIds, cancellationToken);
            s_publishedAuthority = state;
            s_failedAuthority = null;
            Log(jsonStdout, "authority_gate_published", new { state = state.ToString(), lotMode = s_lotLocalAuthorityMode.ToString(), report });

            // W2 step 3 (reversed by the Card 45 E4 forensics, 2026-10-30): the enforcing gate
            // replaces nothing — its hook only gates script-level queue inserts while the engine's
            // own autonomous director keeps running on the puppet, so suppression stays on
            // (GateReplacesAutonomySuppression is now constant false; this block is a no-op kept
            // as the single audit point, and re-enables suppression after any manual retirement).
            if (mirrorEngine != null)
            {
                bool gateEnforcing = ClientAuthorityPolicy.GateReplacesAutonomySuppression(state.Mode, report);
                if (mirrorEngine.AutonomySuppressionEnabled == gateEnforcing)
                {
                    int failed = await mirrorEngine.SetAutonomySuppressionAsync(!gateEnforcing, cancellationToken);
                    Log(jsonStdout, "mirror_autonomy_suppression", new { enabled = !gateEnforcing, releaseFailures = failed });
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            s_failedAuthority = state;
            s_failedAuthorityUtc = now;
            Log(jsonStdout, "authority_gate_failed", new { state = state.ToString(), error = ex.Message });
        }
    }

    private static bool IsOwnedOrClaimPending(ulong actorObjectId) =>
        s_ownedSims.Contains(actorObjectId)
        || (s_intendedClaimSim.HasValue && s_intendedClaimSim.Value == actorObjectId);

    private static bool IsBookChainSitDeferCandidate(InteractionReplayBundle bundle) =>
        SimsOnlineServerHost.InteractionReplayApplySorter.IsSitTransitionDeferred(bundle.InteractionKey)
        || EventToBridgeCommandTranslator.IsForceAlignCandidate(bundle);

    private static void CancelPendingReadBookFlush(ulong actorObjectId)
    {
        s_readBookFlushTimers?.Cancel(actorObjectId);
    }

    private static void ClearPendingReadBookReplay(ulong actorObjectId)
    {
        CancelPendingReadBookFlush(actorObjectId);
        s_pendingReadBookReplayBundle.Remove(actorObjectId);
        s_pendingReadBookForBookChain.Remove(actorObjectId);
    }

    private static void SchedulePendingReadBookReplayFlush(
        MirrorEngine mirrorEngine,
        ulong actorObjectId,
        bool jsonStdout,
        CancellationToken cancellationToken)
    {
        s_readBookFlushTimers!.Schedule(
            actorObjectId,
            TimeSpan.FromMilliseconds(PendingReadBookAwaitSitMs),
            token => FlushPendingReadBookReplayOnTimeoutAsync(mirrorEngine, actorObjectId, jsonStdout, token),
            cancellationToken);
    }

    private static void SchedulePendingReadBookIqFlush(
        MirrorEngine mirrorEngine,
        ulong actorObjectId,
        bool jsonStdout,
        CancellationToken cancellationToken)
    {
        s_readBookFlushTimers!.Schedule(
            actorObjectId,
            TimeSpan.FromMilliseconds(PendingReadBookAwaitSitMs),
            token => FlushPendingReadBookIqOnTimeoutAsync(mirrorEngine, actorObjectId, jsonStdout, token),
            cancellationToken);
    }

    // On the drain loop, PendingReadBookAwaitSitMs after the ReadBook, when no Sit flushed it first.
    private static async Task FlushPendingReadBookReplayOnTimeoutAsync(
        MirrorEngine mirrorEngine,
        ulong actorObjectId,
        bool jsonStdout,
        CancellationToken cancellationToken)
    {
        if (!s_pendingReadBookReplayBundle.Remove(actorObjectId, out InteractionReplayBundle? readBook))
        {
            return;
        }

        Log(jsonStdout, "readbook_pending_flush_timeout", new { actor = actorObjectId, wait_ms = PendingReadBookAwaitSitMs });
        await DispatchOwnedReplayReadBookBatchAsync(mirrorEngine, readBook, jsonStdout, cancellationToken);
    }

    // On the drain loop, PendingReadBookAwaitSitMs after the ReadBook, when no Sit flushed it first.
    private static async Task FlushPendingReadBookIqOnTimeoutAsync(
        MirrorEngine mirrorEngine,
        ulong actorObjectId,
        bool jsonStdout,
        CancellationToken cancellationToken)
    {
        if (!s_pendingReadBookForBookChain.Remove(actorObjectId, out ServerEvent.InteractionQueued? readBook))
        {
            return;
        }

        Log(jsonStdout, "readbook_pending_flush_timeout", new { actor = actorObjectId, wait_ms = PendingReadBookAwaitSitMs });
        await DispatchOwnedReadBookBatchAsync(mirrorEngine, readBook, jsonStdout, cancellationToken);
    }

    private static async Task FlushPendingReplayBookChainAsync(
        MirrorEngine mirrorEngine,
        InteractionReplayBundle sit,
        bool jsonStdout,
        CancellationToken cancellationToken)
    {
        CancelPendingReadBookFlush(sit.ActorObjectId);
        if (!s_pendingReadBookReplayBundle.Remove(sit.ActorObjectId, out InteractionReplayBundle? readBook))
        {
            return;
        }

        if (s_deferredSitReplayBundle.TryGetValue(sit.ActorObjectId, out InteractionReplayBundle? prevSit)
            && prevSit.TargetObjectId != sit.TargetObjectId)
        {
            Log(jsonStdout, "sit_defer_seat_superseded",
                new { actor = sit.ActorObjectId, old_seat = prevSit.TargetObjectId, new_seat = sit.TargetObjectId });
        }

        s_deferredSitReplayBundle[sit.ActorObjectId] = sit;
        Log(jsonStdout, "readbook_pending_flush_on_sit", new { actor = sit.ActorObjectId, seat = sit.TargetObjectId });
        await DispatchOwnedReplayBookChainBatchAsync(mirrorEngine, readBook, jsonStdout, cancellationToken);
    }

    private static async Task FlushPendingBookChainAsync(
        MirrorEngine mirrorEngine,
        ServerEvent.InteractionQueued sit,
        bool jsonStdout,
        CancellationToken cancellationToken)
    {
        CancelPendingReadBookFlush(sit.ActorObjectId);
        if (!s_pendingReadBookForBookChain.Remove(sit.ActorObjectId, out ServerEvent.InteractionQueued? readBook))
        {
            return;
        }

        if (s_deferredSitForBookChain.TryGetValue(sit.ActorObjectId, out ServerEvent.InteractionQueued? prevSit)
            && prevSit.TargetObjectId != sit.TargetObjectId)
        {
            Log(jsonStdout, "sit_defer_seat_superseded",
                new { actor = sit.ActorObjectId, old_seat = prevSit.TargetObjectId, new_seat = sit.TargetObjectId });
        }

        s_deferredSitForBookChain[sit.ActorObjectId] = sit;
        Log(jsonStdout, "readbook_pending_flush_on_sit", new { actor = sit.ActorObjectId, seat = sit.TargetObjectId });
        await DispatchOwnedBookChainBatchAsync(mirrorEngine, readBook, jsonStdout, cancellationToken);
    }

    private static async Task FlushPendingBookChainMixedAsync(
        MirrorEngine mirrorEngine,
        InteractionReplayBundle sit,
        bool jsonStdout,
        CancellationToken cancellationToken)
    {
        CancelPendingReadBookFlush(sit.ActorObjectId);
        if (!s_pendingReadBookForBookChain.Remove(sit.ActorObjectId, out ServerEvent.InteractionQueued? readBook))
        {
            return;
        }

        List<ServerEvent.InteractionQueued> terminalBatch = new(2);
        if (s_deferredGetBookForBookChain.Remove(sit.ActorObjectId, out ServerEvent.InteractionQueued? getBook))
        {
            terminalBatch.Add(getBook);
        }

        terminalBatch.Add(readBook);
        Log(jsonStdout, "readbook_pending_flush_on_sit_mixed",
            new { actor = sit.ActorObjectId, seat = sit.TargetObjectId, terminal_count = terminalBatch.Count });

        s_activeOrders.MarkActive(sit.ActorObjectId, DateTime.UtcNow);
        try
        {
            await mirrorEngine.DispatchAsync(new ServerEvent.InteractionReplayQueued(sit), cancellationToken);
            IReadOnlyList<ServerEvent.InteractionQueued> coalesced =
                InteractionQueuedCoalescer.CoalesceForBatch(terminalBatch);
            await DispatchBookChainBatchAsync(
                mirrorEngine,
                coalesced,
                jsonStdout,
                cancellationToken,
                "InteractionQueuedBookChainMixed");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log(jsonStdout, "mirror_dispatch_failed", new { error = ex.Message, command_type = "InteractionQueuedBookChainMixed" });
            ReportMirrorFailureOnce(ex, "mirror.dispatch_failed", "InteractionQueuedBookChainMixed");
        }
    }

    private static bool IsEphemeralAutonomyOnlyFailure(ServerEvent decoded, DispatchResult result) =>
        decoded is ServerEvent.FullSnapshot or ServerEvent.SnapshotDelta or ServerEvent.InteractionQueued
        && result.Failed > 0
        && result.Succeeded > 0
        && result.Errors.All(static e =>
            ((e.Message.Contains("ApplyAutonomy", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("APPLY_AUTONOMY", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("ApplyPositions", StringComparison.OrdinalIgnoreCase))
            && e.Message.Contains("Timed out waiting", StringComparison.OrdinalIgnoreCase))
            || ((e.Message.Contains("ApplyAutonomy", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("APPLY_AUTONOMY", StringComparison.OrdinalIgnoreCase))
            && e.Message.Contains("Sim not found", StringComparison.OrdinalIgnoreCase))
            || ((e.Message.Contains("ApplyInteractionQueue", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("APPLY_INTERACTION_QUEUE", StringComparison.OrdinalIgnoreCase))
            && e.Message.Contains("Sim not found", StringComparison.OrdinalIgnoreCase)));

    private static IReadOnlyList<ServerEvent.InteractionQueued> BuildBookChainBatch(
        ulong actorObjectId,
        ServerEvent.InteractionQueued readBook)
    {
        List<ServerEvent.InteractionQueued> chainItems = new(3);
        if (s_deferredGetBookForBookChain.Remove(actorObjectId, out ServerEvent.InteractionQueued? getBook))
        {
            chainItems.Add(getBook);
        }

        if (s_deferredSitForBookChain.Remove(actorObjectId, out ServerEvent.InteractionQueued? sit))
        {
            chainItems.Add(sit);
        }

        chainItems.Add(readBook);
        return InteractionQueuedCoalescer.CoalesceForBatch(chainItems);
    }

    private static async Task DispatchOwnedReadBookBatchAsync(
        MirrorEngine mirrorEngine,
        ServerEvent.InteractionQueued readBook,
        bool jsonStdout,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ServerEvent.InteractionQueued> batch = BuildBookChainBatch(readBook.ActorObjectId, readBook);
        Log(jsonStdout, "book_chain_batch_dispatch", new { actor = readBook.ActorObjectId, count = batch.Count, source = "readbook-only" });
        await DispatchBookChainBatchAsync(mirrorEngine, batch, jsonStdout, cancellationToken, "InteractionQueuedReadBookOnly");
    }

    private static async Task DispatchOwnedBookChainBatchAsync(
        MirrorEngine mirrorEngine,
        ServerEvent.InteractionQueued readBook,
        bool jsonStdout,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ServerEvent.InteractionQueued> batch = BuildBookChainBatch(readBook.ActorObjectId, readBook);
        Log(jsonStdout, "book_chain_batch_dispatch", new { actor = readBook.ActorObjectId, count = batch.Count });
        await DispatchBookChainBatchAsync(mirrorEngine, batch, jsonStdout, cancellationToken, "InteractionQueuedBookChain");
    }

    private static async Task DispatchBookChainBatchAsync(
        MirrorEngine mirrorEngine,
        IReadOnlyList<ServerEvent.InteractionQueued> batch,
        bool jsonStdout,
        CancellationToken cancellationToken,
        string commandType)
    {
        for (int i = 0; i < batch.Count; i++)
        {
            s_activeOrders.MarkActive(batch[i].ActorObjectId, DateTime.UtcNow);
        }

        try
        {
            DispatchResult result = await mirrorEngine.DispatchInteractionBatchAsync(batch, cancellationToken);
            if (result.AnyFailed)
            {
                Log(jsonStdout, "mirror_dispatch_partial_failure", new
                {
                    command_type = commandType,
                    translated = result.Translated,
                    succeeded = result.Succeeded,
                    failed = result.Failed,
                    first_error = result.Errors.Count > 0 ? result.Errors[0].Message : null
                });
                ReportMirrorPartialFailureOnce(result, commandType);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log(jsonStdout, "mirror_dispatch_failed", new { error = ex.Message, command_type = commandType });
            ReportMirrorFailureOnce(ex, "mirror.dispatch_failed", commandType);
        }
    }

    /// <summary>
    /// owned-actor InteractionQueued override shared by single- and batch-dispatch paths.
    /// </summary>
    private static OwnedInteractionReplayDecision EvaluateOwnedInteractionReplay(
        ServerEvent.InteractionQueued iq,
        bool jsonStdout)
    {
        if (!SimsOnlineServerHost.InteractionReplayApplySorter.IsSitTransitionDeferred(iq.InteractionKey)
            && !SimsOnlineServerHost.InteractionReplayApplySorter.IsReadBookTerminalHead(iq.InteractionKey)
            && !SimsOnlineServerHost.InteractionReplayApplySorter.IsGetBookPrecursor(iq.InteractionKey))
        {
            if (s_deferredSitForBookChain.Remove(iq.ActorObjectId))
            {
                Log(jsonStdout, "sit_defer_cleared",
                    new { actor = iq.ActorObjectId, reason = "superseded-by-other-interaction", key = iq.InteractionKey });
            }

            if (s_deferredGetBookForBookChain.Remove(iq.ActorObjectId))
            {
                Log(jsonStdout, "getbook_defer_cleared",
                    new { actor = iq.ActorObjectId, reason = "superseded-by-other-interaction", key = iq.InteractionKey });
            }

            ClearPendingReadBookReplay(iq.ActorObjectId);
        }

        if (SimsOnlineServerHost.TerminalInteractionFilter.IsNeverReplay(iq.InteractionKey))
        {
            Log(jsonStdout, "owned_host_interaction_observed_no_bridge",
                new { actor = iq.ActorObjectId, interaction_id = iq.InteractionId, key = iq.InteractionKey, reason = "never-replay" });
            return OwnedInteractionReplayDecision.NoReplay;
        }

        bool ownEcho = s_pendingIntents.TryConsumeMatch(iq.ActorObjectId, iq.InteractionKey, DateTime.UtcNow);
        if (!ownEcho && SimsOnlineServerHost.OwnedHostDetourFilter.IsOwnedHostDetourSkip(iq.InteractionKey))
        {
            Log(jsonStdout, "owned_host_interaction_observed_no_bridge",
                new { actor = iq.ActorObjectId, interaction_id = iq.InteractionId, key = iq.InteractionKey, reason = "host-detour-skipped" });
            return OwnedInteractionReplayDecision.NoReplay;
        }

        if (!ownEcho && SimsOnlineServerHost.InteractionReplayApplySorter.IsBookChainPrecursor(iq.InteractionKey))
        {
            Log(jsonStdout, "owned_host_interaction_observed_no_bridge",
                new { actor = iq.ActorObjectId, interaction_id = iq.InteractionId, key = iq.InteractionKey, reason = "book-precursor-skipped" });
            return OwnedInteractionReplayDecision.NoReplay;
        }

        bool routeReplay = ownEcho || SimsOnlineServerHost.TerminalInteractionFilter.IsRouteReplay(iq.InteractionKey);
        bool terminalReplay = SimsOnlineServerHost.TerminalInteractionFilter.IsTerminalReplay(iq.InteractionKey)
            && iq.TargetObjectId is ulong terminalTargetId && terminalTargetId != 0;

        if ((routeReplay || terminalReplay) && (ownEcho || iq.TargetPosition != null
            || (iq.TargetObjectId is ulong routeTargetId && routeTargetId != 0)))
        {
            s_activeOrders.MarkActive(iq.ActorObjectId, DateTime.UtcNow);
            Log(jsonStdout, "owned_order_mirror_suppressed", new { actor = iq.ActorObjectId, interaction_id = iq.InteractionId });
        }

        bool hostReplayable = routeReplay && (iq.TargetPosition != null
            || (iq.TargetObjectId is ulong replayTargetId && replayTargetId != 0))
            || terminalReplay;

        if (!hostReplayable || !EventToBridgeCommandTranslator.IsInteractionReplayCandidate(iq))
        {
            Log(jsonStdout, ownEcho ? "mirror_echo_confirmed" : "owned_host_interaction_observed_no_bridge",
                new { actor = iq.ActorObjectId, interaction_id = iq.InteractionId, key = iq.InteractionKey });
            return OwnedInteractionReplayDecision.NoReplay;
        }

        if (!ownEcho
            && SimsOnlineServerHost.InteractionReplayApplySorter.IsReadBookTerminalHead(iq.InteractionKey)
            && (s_deferredSitForBookChain.ContainsKey(iq.ActorObjectId)
                || s_deferredGetBookForBookChain.ContainsKey(iq.ActorObjectId)))
        {
            if (s_deferredGetBookForBookChain.ContainsKey(iq.ActorObjectId)
                && !s_deferredSitForBookChain.ContainsKey(iq.ActorObjectId))
            {
                s_pendingReadBookForBookChain[iq.ActorObjectId] = iq;
                ClearBookChainSeatCorrectionState(iq.ActorObjectId);
                Log(jsonStdout, "owned_host_interaction_observed_no_bridge",
                    new { actor = iq.ActorObjectId, interaction_id = iq.InteractionId, key = iq.InteractionKey, reason = "readbook-deferred-awaiting-sit" });
                return OwnedInteractionReplayDecision.AwaitingSitForBookChainBatch;
            }

            Log(jsonStdout, "owned_host_interaction_replayed",
                new { actor = iq.ActorObjectId, interaction_id = iq.InteractionId, key = iq.InteractionKey, mode = "book-chain-batch" });
            return OwnedInteractionReplayDecision.BatchReadBookWithSit;
        }

        if (!ownEcho
            && SimsOnlineServerHost.InteractionReplayApplySorter.IsGetBookPrecursor(iq.InteractionKey))
        {
            s_deferredGetBookForBookChain[iq.ActorObjectId] = iq;
            Log(jsonStdout, "owned_host_interaction_observed_no_bridge",
                new { actor = iq.ActorObjectId, interaction_id = iq.InteractionId, key = iq.InteractionKey, reason = "getbook-deferred-for-readbook" });
            return OwnedInteractionReplayDecision.DeferredSit;
        }

        if (!ownEcho
            && SimsOnlineServerHost.InteractionReplayApplySorter.IsSitTransitionDeferred(iq.InteractionKey))
        {
            if (s_pendingReadBookForBookChain.ContainsKey(iq.ActorObjectId))
            {
                Log(jsonStdout, "owned_host_interaction_observed_no_bridge",
                    new { actor = iq.ActorObjectId, interaction_id = iq.InteractionId, key = iq.InteractionKey, reason = "sit-flushes-pending-readbook" });
                return OwnedInteractionReplayDecision.FlushPendingBookChainReplayBatch;
            }

            if (TrySeatCorrectionAfterBookChain(iq.ActorObjectId, iq.TargetObjectId, jsonStdout))
            {
                return OwnedInteractionReplayDecision.SeatCorrectionAfterBookChain;
            }

            if (s_deferredSitForBookChain.TryGetValue(iq.ActorObjectId, out ServerEvent.InteractionQueued? prevSit)
                && prevSit.TargetObjectId != iq.TargetObjectId)
            {
                Log(jsonStdout, "sit_defer_seat_superseded",
                    new { actor = iq.ActorObjectId, old_seat = prevSit.TargetObjectId, new_seat = iq.TargetObjectId });
            }

            s_deferredSitForBookChain[iq.ActorObjectId] = iq;
            Log(jsonStdout, "owned_host_interaction_observed_no_bridge",
                new { actor = iq.ActorObjectId, interaction_id = iq.InteractionId, key = iq.InteractionKey, reason = "sit-deferred-for-readbook" });
            return OwnedInteractionReplayDecision.DeferredSit;
        }

        Log(jsonStdout, ownEcho ? "mirror_echo_confirmed" : "owned_host_interaction_replayed",
            new { actor = iq.ActorObjectId, interaction_id = iq.InteractionId, key = iq.InteractionKey });

        return OwnedInteractionReplayDecision.ReplaySingle;
    }

    private static IReadOnlyList<InteractionReplayBundle> BuildBookChainReplayBatch(
        ulong actorObjectId,
        InteractionReplayBundle readBook)
    {
        List<InteractionReplayBundle> chainItems = new(3);
        if (s_deferredGetBookReplayBundle.Remove(actorObjectId, out InteractionReplayBundle? getBook))
        {
            chainItems.Add(getBook);
        }

        if (s_deferredSitReplayBundle.Remove(actorObjectId, out InteractionReplayBundle? sit))
        {
            chainItems.Add(sit);
        }

        chainItems.Add(readBook);
        return chainItems;
    }

    private static async Task DispatchOwnedReplayReadBookBatchAsync(
        MirrorEngine mirrorEngine,
        InteractionReplayBundle readBook,
        bool jsonStdout,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<InteractionReplayBundle> batch = BuildBookChainReplayBatch(readBook.ActorObjectId, readBook);
        Log(jsonStdout, "book_chain_replay_batch_dispatch", new { actor = readBook.ActorObjectId, count = batch.Count, source = "readbook-only" });
        await DispatchReplayBookChainBatchAsync(mirrorEngine, batch, jsonStdout, cancellationToken, "InteractionReplayQueuedReadBookOnly");
    }

    private static async Task DispatchOwnedReplayBookChainBatchAsync(
        MirrorEngine mirrorEngine,
        InteractionReplayBundle readBook,
        bool jsonStdout,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<InteractionReplayBundle> batch = BuildBookChainReplayBatch(readBook.ActorObjectId, readBook);
        Log(jsonStdout, "book_chain_replay_batch_dispatch", new { actor = readBook.ActorObjectId, count = batch.Count });
        await DispatchReplayBookChainBatchAsync(mirrorEngine, batch, jsonStdout, cancellationToken, "InteractionReplayQueuedBookChain");
    }

    private static async Task DispatchReplayBookChainBatchAsync(
        MirrorEngine mirrorEngine,
        IReadOnlyList<InteractionReplayBundle> batch,
        bool jsonStdout,
        CancellationToken cancellationToken,
        string commandType)
    {
        for (int i = 0; i < batch.Count; i++)
        {
            s_activeOrders.MarkActive(batch[i].ActorObjectId, DateTime.UtcNow);
        }

        try
        {
            DispatchResult result = await mirrorEngine.DispatchReplayBundleBatchAsync(batch, cancellationToken);
            if (result.AnyFailed)
            {
                Log(jsonStdout, "mirror_dispatch_partial_failure", new
                {
                    command_type = commandType,
                    translated = result.Translated,
                    succeeded = result.Succeeded,
                    failed = result.Failed,
                    first_error = result.Errors.Count > 0 ? result.Errors[0].Message : null
                });
                ReportMirrorPartialFailureOnce(result, commandType);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log(jsonStdout, "mirror_dispatch_failed", new { error = ex.Message, command_type = commandType });
            ReportMirrorFailureOnce(ex, "mirror.dispatch_failed", commandType);
        }
    }

    private static void ClearBookChainSeatCorrectionState(ulong actorObjectId)
    {
        s_awaitingSeatCorrectionAfterBookChain.Remove(actorObjectId);
        s_readBookPendingSeatCorrection.Remove(actorObjectId);
        s_readBookPendingSeatCorrectionIq.Remove(actorObjectId);
    }

    private static bool TrySeatCorrectionAfterBookChain(ulong actorObjectId, ulong? seatObjectId, bool jsonStdout)
    {
        if (!s_awaitingSeatCorrectionAfterBookChain.Remove(actorObjectId))
        {
            return false;
        }

        Log(jsonStdout, "sit_post_book_chain_correction", new { actor = actorObjectId, seat = seatObjectId });
        return true;
    }

    private static async Task DispatchPostBookChainSeatCorrectionAsync(
        MirrorEngine mirrorEngine,
        ServerEvent.InteractionQueued sit,
        bool jsonStdout,
        CancellationToken cancellationToken)
    {
        s_activeOrders.MarkActive(sit.ActorObjectId, DateTime.UtcNow);
        try
        {
            await mirrorEngine.DispatchAsync(sit, cancellationToken);

            if (s_readBookPendingSeatCorrectionIq.Remove(sit.ActorObjectId, out ServerEvent.InteractionQueued? readBook))
            {
                await mirrorEngine.DispatchAsync(readBook, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log(jsonStdout, "mirror_dispatch_failed", new { error = ex.Message, command_type = "SeatCorrectionAfterBookChain" });
            ReportMirrorFailureOnce(ex, "mirror.dispatch_failed", "SeatCorrectionAfterBookChain");
        }
    }

    private static async Task DispatchPostBookChainSeatCorrectionAsync(
        MirrorEngine mirrorEngine,
        InteractionReplayBundle sit,
        bool jsonStdout,
        CancellationToken cancellationToken)
    {
        s_activeOrders.MarkActive(sit.ActorObjectId, DateTime.UtcNow);
        try
        {
            await mirrorEngine.DispatchAsync(
                new ServerEvent.InteractionReplayQueued(
                    EventToBridgeCommandTranslator.NormalizeForceAlignBundleForDispatch(sit)),
                cancellationToken);

            if (s_readBookPendingSeatCorrection.Remove(sit.ActorObjectId, out InteractionReplayBundle? readBook))
            {
                await mirrorEngine.DispatchAsync(new ServerEvent.InteractionReplayQueued(readBook), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log(jsonStdout, "mirror_dispatch_failed", new { error = ex.Message, command_type = "SeatCorrectionAfterBookChain" });
            ReportMirrorFailureOnce(ex, "mirror.dispatch_failed", "SeatCorrectionAfterBookChain");
        }
    }

    private static OwnedInteractionReplayDecision EvaluateOwnedInteractionReplayBundle(
        InteractionReplayBundle bundle,
        bool jsonStdout)
    {
        if (!IsBookChainSitDeferCandidate(bundle)
            && !SimsOnlineServerHost.InteractionReplayApplySorter.IsReadBookTerminalHead(bundle.InteractionKey)
            && !SimsOnlineServerHost.InteractionReplayApplySorter.IsGetBookPrecursor(bundle.InteractionKey))
        {
            if (s_deferredSitReplayBundle.Remove(bundle.ActorObjectId))
            {
                Log(jsonStdout, "sit_replay_defer_cleared",
                    new { actor = bundle.ActorObjectId, reason = "superseded-by-other-interaction", key = bundle.InteractionKey });
            }

            if (s_deferredGetBookReplayBundle.Remove(bundle.ActorObjectId))
            {
                Log(jsonStdout, "getbook_replay_defer_cleared",
                    new { actor = bundle.ActorObjectId, reason = "superseded-by-other-interaction", key = bundle.InteractionKey });
            }

            ClearPendingReadBookReplay(bundle.ActorObjectId);
        }

        if (SimsOnlineServerHost.TerminalInteractionFilter.IsNeverReplay(bundle.InteractionKey))
        {
            Log(jsonStdout, "owned_host_interaction_observed_no_bridge",
                new { actor = bundle.ActorObjectId, interaction_id = bundle.InteractionId, key = bundle.InteractionKey, reason = "never-replay" });
            return OwnedInteractionReplayDecision.NoReplay;
        }

        if (SimsOnlineServerHost.OwnedHostDetourFilter.IsOwnedHostDetourSkip(bundle.InteractionKey))
        {
            Log(jsonStdout, "owned_host_interaction_observed_no_bridge",
                new { actor = bundle.ActorObjectId, interaction_id = bundle.InteractionId, key = bundle.InteractionKey, reason = "host-detour-skipped" });
            return OwnedInteractionReplayDecision.NoReplay;
        }

        if (SimsOnlineServerHost.InteractionReplayApplySorter.IsBookChainPrecursor(bundle.InteractionKey))
        {
            Log(jsonStdout, "owned_host_interaction_observed_no_bridge",
                new { actor = bundle.ActorObjectId, interaction_id = bundle.InteractionId, key = bundle.InteractionKey, reason = "book-precursor-skipped" });
            return OwnedInteractionReplayDecision.NoReplay;
        }

        bool replayable = EventToBridgeCommandTranslator.IsForceAlignCandidate(bundle)
            || EventToBridgeCommandTranslator.IsInteractionReplayCandidate(bundle);

        if (!replayable)
        {
            Log(jsonStdout, "owned_host_interaction_observed_no_bridge",
                new { actor = bundle.ActorObjectId, interaction_id = bundle.InteractionId, key = bundle.InteractionKey });
            return OwnedInteractionReplayDecision.NoReplay;
        }

        if (SimsOnlineServerHost.InteractionReplayApplySorter.IsReadBookTerminalHead(bundle.InteractionKey)
            && (s_deferredSitReplayBundle.ContainsKey(bundle.ActorObjectId)
                || s_deferredGetBookReplayBundle.ContainsKey(bundle.ActorObjectId)))
        {
            if (s_deferredGetBookReplayBundle.ContainsKey(bundle.ActorObjectId)
                && !s_deferredSitReplayBundle.ContainsKey(bundle.ActorObjectId))
            {
                s_pendingReadBookReplayBundle[bundle.ActorObjectId] = bundle;
                ClearBookChainSeatCorrectionState(bundle.ActorObjectId);
                Log(jsonStdout, "owned_host_interaction_observed_no_bridge",
                    new { actor = bundle.ActorObjectId, interaction_id = bundle.InteractionId, key = bundle.InteractionKey, reason = "readbook-deferred-awaiting-sit-replay" });
                return OwnedInteractionReplayDecision.AwaitingSitForBookChainBatch;
            }

            Log(jsonStdout, "owned_host_interaction_replayed",
                new { actor = bundle.ActorObjectId, interaction_id = bundle.InteractionId, key = bundle.InteractionKey, mode = "book-chain-replay-batch" });
            return OwnedInteractionReplayDecision.BatchReadBookWithSit;
        }

        if (SimsOnlineServerHost.InteractionReplayApplySorter.IsGetBookPrecursor(bundle.InteractionKey))
        {
            s_deferredGetBookReplayBundle[bundle.ActorObjectId] = bundle;
            Log(jsonStdout, "owned_host_interaction_observed_no_bridge",
                new { actor = bundle.ActorObjectId, interaction_id = bundle.InteractionId, key = bundle.InteractionKey, reason = "getbook-deferred-for-readbook-replay" });
            return OwnedInteractionReplayDecision.DeferredSit;
        }

        if (IsBookChainSitDeferCandidate(bundle))
        {
            if (s_pendingReadBookForBookChain.ContainsKey(bundle.ActorObjectId))
            {
                Log(jsonStdout, "owned_host_interaction_observed_no_bridge",
                    new { actor = bundle.ActorObjectId, interaction_id = bundle.InteractionId, key = bundle.InteractionKey, reason = "sit-flushes-pending-readbook-mixed" });
                return OwnedInteractionReplayDecision.FlushPendingBookChainReplayBatch;
            }

            if (s_pendingReadBookReplayBundle.ContainsKey(bundle.ActorObjectId))
            {
                Log(jsonStdout, "owned_host_interaction_observed_no_bridge",
                    new { actor = bundle.ActorObjectId, interaction_id = bundle.InteractionId, key = bundle.InteractionKey, reason = "sit-flushes-pending-readbook-replay" });
                return OwnedInteractionReplayDecision.FlushPendingBookChainReplayBatch;
            }

            if (TrySeatCorrectionAfterBookChain(bundle.ActorObjectId, bundle.TargetObjectId, jsonStdout))
            {
                return OwnedInteractionReplayDecision.SeatCorrectionAfterBookChain;
            }

            if (s_deferredSitReplayBundle.TryGetValue(bundle.ActorObjectId, out InteractionReplayBundle? prevSit)
                && prevSit.TargetObjectId != bundle.TargetObjectId)
            {
                Log(jsonStdout, "sit_defer_seat_superseded",
                    new { actor = bundle.ActorObjectId, old_seat = prevSit.TargetObjectId, new_seat = bundle.TargetObjectId });
            }

            s_deferredSitReplayBundle[bundle.ActorObjectId] = bundle;
            Log(jsonStdout, "owned_host_interaction_observed_no_bridge",
                new { actor = bundle.ActorObjectId, interaction_id = bundle.InteractionId, key = bundle.InteractionKey, reason = "sit-deferred-for-readbook-replay" });
            return OwnedInteractionReplayDecision.DeferredSit;
        }

        s_activeOrders.MarkActive(bundle.ActorObjectId, DateTime.UtcNow);
        Log(jsonStdout, "owned_host_interaction_replayed",
            new { actor = bundle.ActorObjectId, interaction_id = bundle.InteractionId, key = bundle.InteractionKey, mode = "interaction-replay-queued" });

        return OwnedInteractionReplayDecision.ReplaySingle;
    }

    /// <summary>Dev tooling: fire an in-game toast through the mirror host. Best-effort — a toast
    /// failure must never disturb the receive loop, so every exception is swallowed into a log line.</summary>
    private static async Task TryShowRemoteDialogAsync(ServerEvent.DialogRequested dialog, bool jsonStdout, CancellationToken cancellationToken)
    {
        if (s_mirrorHost == null)
        {
            return;
        }

        try
        {
            await s_mirrorHost.ShowDialogAsync(dialog.RequestId, dialog.SimObjectId, dialog.Kind, dialog.Args, cancellationToken);
            Log(jsonStdout, "dialog_shown", new { request = dialog.RequestId, sim = dialog.SimObjectId, kind = dialog.Kind });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log(jsonStdout, "dialog_show_failed", new { request = dialog.RequestId, error = ex.Message });
        }
    }

    // make the (first) claimed Sim the local TS3's active Sim. Best-effort, like TryNotifyAsync.
    private static async Task TryAssertActiveSimAsync(bool jsonStdout, CancellationToken cancellationToken)
    {
        s_lastActiveSimAssertUtc = DateTime.UtcNow;
        if (s_mirrorHost == null || s_ownedSims.Count == 0)
        {
            return;
        }

        ulong simObjectId = s_ownedSims.Min();
        try
        {
            await s_mirrorHost.SetActiveSimAsync(simObjectId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log(jsonStdout, "active_sim_failed", new { simObjectId, error = ex.Message });
        }
    }

    private static async Task TryNotifyAsync(string text, bool jsonStdout, CancellationToken cancellationToken)
    {
        if (s_mirrorHost == null)
        {
            return;
        }

        try
        {
            await s_mirrorHost.ShowNotificationAsync(text, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log(jsonStdout, "mirror_notify_failed", new { error = ex.Message });
        }
    }

    private static void ReportMirrorPartialFailureOnce(
        DispatchResult result,
        ServerEvent serverEvent,
        IReadOnlyDictionary<string, string?> attributes)
    {
        Exception failure = result.Errors.Count > 0
            ? result.Errors[0]
            : new InvalidOperationException("Mirror dispatch reported a failed command.");
        bool isTargetResolution = SimsOnlineTelemetryMetadata.IsTargetResolutionFailure(serverEvent, result.Errors);
        string signal = isTargetResolution
            ? "mirror.target_resolution_failed"
            : "mirror.dispatch_partial_failure";
        
        // emit named failure-class breadcrumb through the wrapper (diagnostic, not unhandled error).
        if (s_telemetry != null)
        {
            string failureMsg = result.Errors.Count > 0 ? result.Errors[0].Message : "dispatch failed";
            if (isTargetResolution)
            {
                s_telemetry.EmitReplayTargetResolutionFailed(0, failureMsg);
            }
            else
            {
                s_telemetry.EmitReplayDispatchPartialFailure(0, failureMsg, failureMsg);
            }
        }
        
        ReportMirrorFailureOnce(failure, signal, attributes);
    }

    private static void ReportMirrorPartialFailureOnce(
        DispatchResult result,
        string commandType)
    {
        Exception failure = result.Errors.Count > 0
            ? result.Errors[0]
            : new InvalidOperationException("Mirror dispatch reported a failed command.");
        ReportMirrorFailureOnce(failure, "mirror.dispatch_partial_failure", commandType);
        // emit named failure-class breadcrumb through the wrapper.
        s_telemetry?.EmitReplayDispatchPartialFailure(0, commandType, failure.Message);
    }

    private static void ReportMirrorFailureOnce(
        Exception exception,
        string signal,
        string commandType) =>
        ReportMirrorFailureOnce(
            exception,
            signal,
            new Dictionary<string, string?>
            {
                ["command_type"] = commandType,
                ["validation_gate"] = "mirror-dispatch",
            });

    private static void ReportMirrorFailureOnce(
        Exception exception,
        string signal,
        IReadOnlyDictionary<string, string?> attributes)
    {
        if (s_telemetry == null)
        {
            return;
        }

        WriteSentryEventId(s_telemetry.ReportFailureOnce(
            exception,
            signal,
            attributes));
    }

    private static void WriteSentryEventId(global::Sentry.SentryId eventId)
    {
        if (eventId != global::Sentry.SentryId.Empty)
        {
            Console.Error.WriteLine($"Sentry event ID: {eventId}");
        }
    }

    /// <summary>
    /// Asks the server for its world (RequestWorld) on a connection of its own and writes the reply's
    /// WorldFileChunk events into <paramref name="savesDirectory"/>. Returns the save folder to load ("Name.sims3").
    /// Waits while the server's public listener refuses: the server binds it only once its own save has loaded, and a
    /// client booted alongside the server (quick_loop run --boot --parallel) reaches its menu before that.
    /// </summary>
    private static async Task<string> FetchWorldFromServerAsync(string host, int port, int playerId, string? savesDirectory, bool jsonStdout,
        Action<string>? phase, CancellationToken cancellationToken, string wire = "json")
    {
        if (string.IsNullOrWhiteSpace(savesDirectory) || !Directory.Exists(savesDirectory))
        {
            throw new InvalidOperationException($"--client-world-from-server needs --client-saves-dir naming the client TS3's Saves folder (got '{savesDirectory}').");
        }

        Stopwatch watch = Stopwatch.StartNew();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using TcpClient tcp = await ConnectWhenListeningAsync(host, port, jsonStdout, phase, timeout.Token);
        using NetworkStream stream = tcp.GetStream();
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 65536, leaveOpen: true);
        PublicWireConnection? lineWire = wire == "line" ? new() : null;
        if (lineWire != null) await PublicWireConnection.HandshakeAsync(stream, reader, "client", timeout.Token);
        ClientCommand.RequestWorld request = new(Guid.NewGuid(), playerId);
        byte[] line = Encoding.UTF8.GetBytes((lineWire?.EncodeCommand(request) ?? PublicProtocolCodec.EncodeCommand(request)) + "\n");
        await stream.WriteAsync(line, timeout.Token);
        Log(jsonStdout, "world_requested", new { savesDirectory });

        WorldSaveWriter writer = new(savesDirectory);
        while (!writer.IsComplete)
        {
            string received = lineWire != null ? await lineWire.ReadJsonAsync(reader, timeout.Token)
                : await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("The server closed the connection before the world arrived.");
            switch (PublicProtocolCodec.DecodeEvent(received))
            {
                case ServerEvent.CommandRejected rejected when rejected.CommandId == request.CommandId:
                    throw new InvalidOperationException($"The server refused the world: {rejected.Reason}");
                case ServerEvent.WorldFileChunk chunk:
                    writer.Add(chunk);
                    break;
            }
        }

        Log(jsonStdout, "world_received", new { folder = writer.FolderName, bytes = writer.BytesWritten, seconds = Math.Round(watch.Elapsed.TotalSeconds, 1) });
        phase?.Invoke("world-received");
        return writer.FolderName!;
    }

    /// <summary>
    /// A connection to the server's public listener, retried each second while it is refused (the server's session has
    /// not started yet); the state file shows phase server-wait meanwhile. Any other failure, and the caller's deadline,
    /// end the wait.
    /// </summary>
    private static async Task<TcpClient> ConnectWhenListeningAsync(string host, int port, bool jsonStdout, Action<string>? phase, CancellationToken cancellationToken)
    {
        Stopwatch watch = Stopwatch.StartNew();
        bool waiting = false;
        while (true)
        {
            TcpClient tcp = new();
            try
            {
                await tcp.ConnectAsync(host, port, cancellationToken);
                if (waiting)
                {
                    Log(jsonStdout, "world_request_listening", new { seconds = Math.Round(watch.Elapsed.TotalSeconds, 1) });
                }

                return tcp;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                tcp.Dispose();
                if (!waiting)
                {
                    waiting = true;
                    Log(jsonStdout, "world_request_waiting", new { endpoint = $"{host}:{port}" });
                    phase?.Invoke("server-wait");
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }
    }

    private static void Log(bool jsonStdout, string kind, object payload)
    {
        if (jsonStdout)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { kind, ts = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), payload }));
        }
        else
        {
            // UTC with date + ms — the old seconds-only-no-date stamp could not be
            // correlated with server bridge traces and rolled past midnight ambiguously.
            Console.WriteLine($"[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z] {kind}: {JsonSerializer.Serialize(payload)}");
        }
    }

    // Experiment C, client side: start the session lazily (the first SimAnimation means the bridge is up and the client
    // is in the world). Shadow: copy each event's lines into the inbound ring; the event goes on unchanged. Ring:
    // route the whole event through the ring and hand the dispatcher an empty SimAnimation instead (MirrorEngine still
    // sees one, and has nothing to send over the bridge). Returns what the reader enqueues. A failed start retries
    // after 30 s.
    private static string SimLinkClientShadowObserve(string received)
    {
        try
        {
            SimsOnline.DedicatedServer.HostBridge.SimLinkClientShadowSession? shadow = s_clientShadow;
            if (shadow == null)
            {
                HeadlessSims3Host? host = s_ownedClientHost;
                if (host != null && Interlocked.CompareExchange(ref s_clientShadowStarting, 1, 0) == 0)
                {
                    _ = Task.Run(async () =>
                    {
                        s_clientShadow = await SimsOnline.DedicatedServer.HostBridge.SimLinkClientShadowSession.TryStartAsync(
                            host.HostProcessId,
                            host.SandboxieBoxName,
                            (subcommand, token) => host.SacsRecorderAsync(new[] { subcommand }, token),
                            Console.WriteLine,
                            CancellationToken.None,
                            sSimLinkClientRing
                                ? SimsOnline.DedicatedServer.HostBridge.SimLinkClientMode.Ring
                                : SimsOnline.DedicatedServer.HostBridge.SimLinkClientMode.Shadow);
                        if (s_clientShadow == null)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(30));
                            Interlocked.Exchange(ref s_clientShadowStarting, 0);
                        }
                    });
                }

                return TrackBridgeAnimation(received);
            }

            if (shadow.Mode == SimsOnline.DedicatedServer.HostBridge.SimLinkClientMode.Ring)
            {
                // Stuck: the lines the game never read from the ring go first, in front of this event (which takes the
                // bridge, as every later one does).
                if (shadow.Stuck && PublicProtocolCodec.DecodeEvent(received) is ServerEvent.SimAnimation late)
                {
                    List<string>? reclaimed = shadow.TakeReclaimedLines();
                    if (reclaimed != null)
                    {
                        reclaimed.AddRange(late.Lines);
                        received = PublicProtocolCodec.EncodeEvent(new ServerEvent.SimAnimation(reclaimed));
                        Console.WriteLine($"[simlink-client-shadow] sent {reclaimed.Count - late.Lines.Count} reclaimed line(s) over the bridge ahead of an animation event");
                    }
                }

                lock (s_ringHoldGate)
                {
                    // Bridge-routed animations still being dispatched go first. A newer event waits in the hold rather
                    // than joining them on the bridge (which kept a busy stream on the bridge for good, minutes behind).
                    if (s_ringHold.Count > 0 || Interlocked.Read(ref s_bridgeAnimationsInFlight) > 0)
                    {
                        if (PublicProtocolCodec.DecodeEvent(received) is ServerEvent.SimAnimation held)
                        {
                            Interlocked.Increment(ref shadow.OrderingWaits);
                            s_ringHold.AddRange(held.Lines);
                            return s_emptySimAnimation;
                        }

                        return TrackBridgeAnimation(received);
                    }

                    if (PublicProtocolCodec.DecodeEvent(received) is ServerEvent.SimAnimation routed && shadow.TryRoute(routed.Lines))
                    {
                        return s_emptySimAnimation;
                    }

                    return TrackBridgeAnimation(received);
                }
            }

            if (PublicProtocolCodec.DecodeEvent(received) is ServerEvent.SimAnimation animation)
            {
                shadow.ObserveIncoming(animation.Lines);
            }
        }
        catch (Exception)
        {
            // The shadow must never change the network reader's behavior.
        }

        return received;
    }

    // Ring mode, once no bridge-routed animation is in flight: the held lines go into the ring, in order. When the ring
    // cannot take them (stuck, or no room) they come back as one bridge event for the dispatch queue. Null when nothing
    // was held or the ring took it.
    private static string? FlushRingHold()
    {
        lock (s_ringHoldGate)
        {
            if (s_ringHold.Count == 0 || Interlocked.Read(ref s_bridgeAnimationsInFlight) > 0)
            {
                return null;
            }

            List<string> lines = new(s_ringHold);
            s_ringHold.Clear();
            if (s_clientShadow is { } shadow && shadow.TryRoute(lines))
            {
                return null;
            }

            return TrackBridgeAnimation(PublicProtocolCodec.EncodeEvent(new ServerEvent.SimAnimation(lines)));
        }
    }

    // A SimAnimation that goes over the bridge (ring mode not routing it): in flight until AfterLineAsync sees it.
    private static string TrackBridgeAnimation(string received)
    {
        if (sSimLinkClientRing)
        {
            s_bridgeAnimationLines.AddOrUpdate(received, s_emptySimAnimation);
            long inFlight = Interlocked.Increment(ref s_bridgeAnimationsInFlight);
            if (s_clientShadow is { } shadow)
            {
                Interlocked.Exchange(ref shadow.BridgeInFlight, inFlight);
            }
        }

        return received;
    }

    /// <summary>
    /// The join's three replies as the join drain waits for them: its command_accepted, session_joined and the join
    /// baseline's full_snapshot. The receive task tracks them too, to know where the join's lines end.
    /// </summary>
    private sealed class JoinReplies
    {
        private readonly string _joinId;
        private bool _accepted;
        private bool _joined;
        private bool _snapshot;
        private readonly bool _failOnRejection;

        public JoinReplies(Guid joinId, bool failOnRejection = false)
        {
            _joinId = joinId.ToString("D");
            _failOnRejection = failOnRejection;
        }

        public bool Complete => _accepted && _joined && _snapshot;

        public void Observe(string line)
        {
            if (_failOnRejection && line.Contains("\"command_rejected\"", StringComparison.Ordinal)
                && line.Contains(_joinId, StringComparison.OrdinalIgnoreCase))
                throw new IOException("JoinSession refused: " + line);
            if (line.Contains("\"command_accepted\"", StringComparison.Ordinal) && line.Contains(_joinId, StringComparison.OrdinalIgnoreCase)) _accepted = true;
            if (line.Contains("\"session_joined\"", StringComparison.Ordinal)) _joined = true;
            if (line.Contains("\"full_snapshot\"", StringComparison.Ordinal)) _snapshot = true;
        }
    }

    private static void LogReceived(bool jsonStdout, string line)
    {
        // full_snapshot / snapshot_delta lines are the dominant log volume (a ~71-Sim world
        // is ~48 KB and one lands every sync interval — client.log was observed at 269 MB, almost
        // entirely these). Divert the verbatim body to its own file (--snapshot-log) and emit only a
        // compact, greppable stub in the main log. Every other received line (interaction_queued,
        // command_accepted, session_joined, ... — the ones ut.py/logq key on) is logged unchanged.
        string? snapshotType = SnapshotLineType(line);
        if (snapshotType != null)
        {
            WriteSnapshotToSideFile(line);
            long tick = TryExtractLong(line, "\"tick\":");
            if (jsonStdout)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { kind = "received_snapshot", ts = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), payload = new { type = snapshotType, tick, bytes = line.Length } }));
            }
            else
            {
                Console.WriteLine(FormattableString.Invariant($"[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z] received_snapshot: {{\"type\":\"{snapshotType}\",\"tick\":{tick},\"bytes\":{line.Length}}}"));
            }
            return;
        }

        if (jsonStdout)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "received", ts = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), payload = line }));
        }
        else
        {
            Console.WriteLine($"[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z] received: {line}");
        }
    }

    // identify the two bulky snapshot event types by their stable snake-case $type
    // discriminator (PublicProtocolCodec). Cheap ordinal substring scan on the hot receive path.
    private static string? SnapshotLineType(string line)
    {
        if (line.Contains("\"$type\":\"full_snapshot\"", StringComparison.Ordinal)) return "full_snapshot";
        if (line.Contains("\"$type\":\"snapshot_delta\"", StringComparison.Ordinal)) return "snapshot_delta";
        return null;
    }

    // cheap first-occurrence integer extractor so the snapshot stub can still carry the tick
    // (the one field worth correlating in a timeline) without deserializing the whole payload.
    private static long TryExtractLong(string line, string key)
    {
        int idx = line.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return -1;
        int p = idx + key.Length;
        while (p < line.Length && line[p] == ' ') p++;
        int start = p;
        while (p < line.Length && char.IsDigit(line[p])) p++;
        return p > start && long.TryParse(line.AsSpan(start, p - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            ? value
            : -1;
    }

    // append the verbatim snapshot line to its own file (timestamped to match the main log).
    // Best-effort: a side-file IO error must never break the receive loop — the main-log stub already
    // preserves the timeline; the full body is simply not persisted that tick. Open/close per line is
    // fine at the ~0.5 Hz snapshot cadence and avoids a long-lived handle to manage across shutdown.
    private static void WriteSnapshotToSideFile(string line)
    {
        string? path = s_snapshotLogPath;
        if (path == null) return;
        try
        {
            lock (s_snapshotLogLock)
            {
                File.AppendAllText(path, FormattableString.Invariant($"[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z] {line}{Environment.NewLine}"));
            }
        }
        catch
        {
            // Intentionally swallowed — see method remarks.
        }
    }
}
