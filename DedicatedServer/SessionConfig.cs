namespace SimsOnline.DedicatedServer;

public sealed record SessionConfig(
    string Ts3ExecutablePath,
    string SaveName,
    string ListenEndpoint,
    string HostBridgeEndpoint,
    TimeSpan HostBridgeTimeout,
    string? Ts3SandboxieBoxName,
    string? Ts3SandboxieStartPath,
    bool AttachExistingHost,
    bool HostProbeOnly,
    bool ProbeOnly,
    bool SnapshotAndExit,
    bool ProbeObjectGuidStability,
    int ObjectGuidSampleSize,
    bool ProbeSimLifecycle,
    bool ProbeStateInjection,
    bool ProbeLoadPathComparison,
    bool ProbeAutonomy,
    bool ProbeActorLookupParity,
    bool ProbeRoleSwitch,
    bool ProbeMotiveRoundtrip,
    bool ProbeInteractionRoundtrip,
    bool ProbeCashRoundtrip,
    bool ProbeSkillRoundtrip,
    bool ProbeAutonomyRoundtrip,
    string? MotiveName,
    float? MotiveValue,
    ulong? ProbeHouseholdId,
    int? CashAmount,
    string? SkillName,
    int? SkillLevel,
    bool GoHere,
    ulong? ProbeSimObjectId,
    float? GoHereX,
    float? GoHereY,
    float? GoHereZ,
    string? ServerModPackagePath,
    bool ProbeClientMirrorStartup,
    bool ProbeClientMirrorPosition,
    string? ClientMirrorTs3Path,
    string? ClientMirrorSaveName,
    string? ClientMirrorBinaryPath,
    string? ClientMirrorSandboxieBoxName,
    string? ClientMirrorSandboxieStartPath,
    // 2026-07-03 two-client Linux: the second TS3 runs in an isolated Wine prefix (Sandboxie's
    // role on Windows). When set, the client controller launches `wine TS3W.exe` with
    // WINEPREFIX=<this path> so the second Sims instance is isolated from the server's.
    string? ClientMirrorWineprefixPath,
    string StartSpeedName = "normal",
    bool ProbeLiveSnapshot = false,
    bool ProbeClaimAutonomy = false,
    TimeSpan? PlayerCommandTimeout = null,
    bool BridgeTrace = false,
    bool ProbeBridgeResponseLoss = false,
    int? AttachPid = null,
    string? BridgectlEndpoint = null,
    string? BridgeRecordPath = null,
    TimeSpan? SyncBroadcastInterval = null,
    // P5.6 resync policy knobs (runtime-tuneable; defaults in ResyncThresholds.Default).
    // --resync-warn-ticks, --resync-hard-ticks, --resync-max-interval-seconds
    long? ResyncWarnTicks = null,
    long? ResyncHardTicks = null,
    double? ResyncMaxIntervalSeconds = null,
    // Client-side knobs (passed to SimsOnline.Client via --max-fold-per-cycle, --resync-cooldown-seconds).
    int? MaxFoldPerCycle = null,
    double? ResyncCooldownSeconds = null,
  // lot-tiered mirror scope (default on). --no-interest-scope disables filtering.
    bool DisableInterestScopedMirroring = false,
    // 2026-07-03 dev-tooling phase 1: machine-readable lifecycle state file (see RunStateFile).
    // dev-session/wait-stack pass --state-file .omo/run/server.state.json; ad-hoc runs omit it.
    string? StateFilePath = null,
    // Sentry configuration (all optional; defaults are dev-friendly).
    string? SentryDsnEnv = null,
    string SentryEnvironment = "development",
    bool SentrySendDefaultPii = false,
    double? SentryTracesSampleRate = null,
    double? SentryProfilesSampleRate = null,
    bool SentryEnableMetrics = true,
    bool SentryDisableProfiling = false,
    string? SentryReleaseOverride = null,
    string? CheckpointSaveName = null,
    // n40: a client mirror's --client-saves-dir; its profile joins the fork census (stackctl.launch_fork_census).
    string? ClientSavesDirectory = null)
{
    /// <summary>
    /// player-issued bridge commands (queue interaction, mirror applies, speed, role,
    /// autonomy, quick probes) must fail in seconds, not monopolize the serialized bridge for
    /// the full <see cref="HostBridgeTimeout"/>. Heavy commands (load/save, snapshots,
    /// long-running probes) keep the long timeout.
    /// </summary>
    public static readonly TimeSpan DefaultPlayerCommandTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// ForceAlign sit/posture transitions after GetBook can exceed the 10s player-paced
    /// default (walk-to-seat + posture bind). Still well below <see cref="HostBridgeTimeout"/>.
    /// </summary>
    public static readonly TimeSpan DefaultForceAlignCommandTimeout = TimeSpan.FromSeconds(30);

    public TimeSpan EffectivePlayerCommandTimeout => PlayerCommandTimeout ?? DefaultPlayerCommandTimeout;

    /// <summary>P5.6: resync policy thresholds assembled from optional CLI overrides, falling back to defaults.</summary>
    public Authority.ResyncThresholds ResyncThresholds => new Authority.ResyncThresholds(
        WarnTicks: ResyncWarnTicks ?? Authority.ResyncThresholds.Default.WarnTicks,
        HardTicks: ResyncHardTicks ?? Authority.ResyncThresholds.Default.HardTicks,
        MaxIntervalMs: ResyncMaxIntervalSeconds.HasValue
            ? ResyncMaxIntervalSeconds.Value * 1000.0
            : Authority.ResyncThresholds.Default.MaxIntervalMs);

    /// <summary>P5.6: effective MaxFoldPerCycle passed to MirrorCoordinator (default 8).</summary>
    public int EffectiveMaxFoldPerCycle => MaxFoldPerCycle ?? 8;

    /// <summary>P5.6: effective debounce cooldown for client RequestSnapshot (default 5 s).</summary>
    public TimeSpan EffectiveResyncCooldown => ResyncCooldownSeconds.HasValue
        ? TimeSpan.FromSeconds(ResyncCooldownSeconds.Value)
        : TimeSpan.FromSeconds(5);

    /// <summary>
    /// Serve-mode start speed. TS3 loads into live mode PAUSED, so the serve path applies
    /// this speed once the session is up (the probe paths set their own speeds). "none"
    /// disables the automatic speed write. Unknown names are reported by
    /// <see cref="ValidateProbeArguments"/> rather than throwing here.
    /// </summary>
    public Protocol.GameSpeed? StartGameSpeed => TryMapStartSpeed(StartSpeedName, out Protocol.GameSpeed? speed) ? speed : null;

    private static bool TryMapStartSpeed(string name, out Protocol.GameSpeed? speed)
    {
        switch (name.ToLowerInvariant())
        {
            case "none": speed = null; return true;
            case "pause": speed = Protocol.GameSpeed.Pause; return true;
            case "normal": speed = Protocol.GameSpeed.Normal; return true;
            case "double": case "speed2": speed = Protocol.GameSpeed.Speed2; return true;
            case "triple": case "speed3": speed = Protocol.GameSpeed.Speed3; return true;
            case "skip": case "ultra": speed = Protocol.GameSpeed.Ultra; return true;
            default: speed = null; return false;
        }
    }

    public string? ValidateProbeArguments()
    {
        if (HostBridge.HostBridgeServer.UnsupportedEndpointError(HostBridgeEndpoint) is { } bridgeError)
        {
            return "--host-bridge: " + bridgeError;
        }

        if (!TryMapStartSpeed(StartSpeedName, out _))
        {
            return $"--start-speed value '{StartSpeedName}' is not recognized. Use pause|normal|double|triple|ultra|none.";
        }

        if (ProbeLiveSnapshot && AttachExistingHost)
        {
            return "--probe-live-snapshot requires launching a fresh TS3 host; remove --attach-existing.";
        }

        if (ProbeClaimAutonomy && ProbeSimObjectId is null)
        {
            return "--probe-claim-autonomy requires --sim <objectId>.";
        }

        if (ProbeClaimAutonomy && AttachExistingHost)
        {
            return "--probe-claim-autonomy requires launching a fresh TS3 host; remove --attach-existing.";
        }

        if (AttachExistingHost && (!string.IsNullOrEmpty(Ts3SandboxieBoxName) || !string.IsNullOrEmpty(Ts3SandboxieStartPath)))
        {
            return "--attach-existing cannot be combined with --ts3-sandboxie-box or --ts3-sandboxie-start.";
        }

        if (AttachPid is not null && !AttachExistingHost)
        {
            return "--attach-pid requires --attach-existing.";
        }

        if (!string.IsNullOrEmpty(Ts3SandboxieStartPath) && string.IsNullOrEmpty(Ts3SandboxieBoxName))
        {
            return "--ts3-sandboxie-start requires --ts3-sandboxie-box <name>.";
        }

        if (!string.IsNullOrEmpty(ClientMirrorSandboxieStartPath) && string.IsNullOrEmpty(ClientMirrorSandboxieBoxName))
        {
            return "--client-mirror-sandboxie-start requires --client-mirror-sandboxie-box <name>.";
        }

        if (ProbeAutonomy && ProbeSimObjectId is null)
        {
            return "--probe-autonomy requires --sim <objectId>.";
        }

        if (ProbeObjectGuidStability && AttachExistingHost)
        {
            return "--probe-object-guid-stability requires launching fresh TS3 hosts; remove --attach-existing.";
        }

        if (ProbeSimLifecycle && AttachExistingHost)
        {
            return "--probe-sim-lifecycle requires launching a fresh TS3 host; remove --attach-existing.";
        }

        if (ProbeStateInjection && AttachExistingHost)
        {
            return "--probe-state-injection requires launching a fresh TS3 host; remove --attach-existing.";
        }

        if (ProbeActorLookupParity && AttachExistingHost)
        {
            return "--probe-actor-lookup-parity requires launching a fresh TS3 host; remove --attach-existing.";
        }

        if (ProbeRoleSwitch && AttachExistingHost)
        {
            return "--probe-role-switch requires launching a fresh TS3 host; remove --attach-existing.";
        }

        if (ProbeRoleSwitch && (ProbeSimObjectId is null || GoHereX is null || GoHereY is null || GoHereZ is null))
        {
            return "--probe-role-switch requires --sim <objectId> --x <x> --y <y> --z <z>.";
        }

        if (ProbeMotiveRoundtrip && AttachExistingHost)
        {
            return "--probe-motive-roundtrip requires launching a fresh TS3 host; remove --attach-existing.";
        }

        if (ProbeMotiveRoundtrip && (ProbeSimObjectId is null || MotiveName is null || MotiveValue is null))
        {
            return "--probe-motive-roundtrip requires --sim <objectId> --motive <name> --motive-value <float>.";
        }

        if (ProbeInteractionRoundtrip && AttachExistingHost)
        {
            return "--probe-interaction-roundtrip requires launching a fresh TS3 host; remove --attach-existing.";
        }

        if (ProbeInteractionRoundtrip && (ProbeSimObjectId is null || GoHereX is null || GoHereY is null || GoHereZ is null))
        {
            return "--probe-interaction-roundtrip requires --sim <objectId> --x <x> --y <y> --z <z>.";
        }

        if (ProbeBridgeResponseLoss && AttachExistingHost)
        {
            return "--probe-bridge-response-loss requires launching a fresh TS3 host; remove --attach-existing.";
        }

        if (ProbeBridgeResponseLoss && (ProbeSimObjectId is null || GoHereX is null || GoHereY is null || GoHereZ is null))
        {
            return "--probe-bridge-response-loss requires --sim <objectId> --x <x> --y <y> --z <z>.";
        }

        if (ProbeCashRoundtrip && AttachExistingHost)
        {
            return "--probe-cash-roundtrip requires launching a fresh TS3 host; remove --attach-existing.";
        }

        if (ProbeCashRoundtrip && (ProbeHouseholdId is null || CashAmount is null))
        {
            return "--probe-cash-roundtrip requires --household <id> --funds <int>.";
        }

        if (ProbeSkillRoundtrip && AttachExistingHost)
        {
            return "--probe-skill-roundtrip requires launching a fresh TS3 host; remove --attach-existing.";
        }

        if (ProbeSkillRoundtrip && (ProbeSimObjectId is null || SkillName is null || SkillLevel is null))
        {
            return "--probe-skill-roundtrip requires --sim <objectId> --skill <name> --skill-level <int>.";
        }

        if (ProbeAutonomyRoundtrip && AttachExistingHost)
        {
            return "--probe-autonomy-roundtrip requires launching a fresh TS3 host; remove --attach-existing.";
        }

        if (ProbeAutonomyRoundtrip && ProbeSimObjectId is null)
        {
            return "--probe-autonomy-roundtrip requires --sim <objectId>.";
        }

        if (ProbeLoadPathComparison && AttachExistingHost)
        {
            return "--probe-load-path-comparison requires launching fresh TS3 hosts; remove --attach-existing.";
        }

        if (ProbeClientMirrorStartup && AttachExistingHost)
        {
            return "--probe-client-mirror-startup requires launching a fresh TS3 host; remove --attach-existing.";
        }

        if (ProbeClientMirrorStartup && string.IsNullOrEmpty(ClientMirrorTs3Path))
        {
            return "--probe-client-mirror-startup requires --client-mirror-ts3 <path>.";
        }

        if (ProbeClientMirrorStartup && string.IsNullOrEmpty(ClientMirrorSaveName))
        {
            return "--probe-client-mirror-startup requires --client-mirror-save <name>.";
        }

        if (ProbeClientMirrorStartup && string.IsNullOrEmpty(ClientMirrorBinaryPath))
        {
            return "--probe-client-mirror-startup requires --client-mirror-binary <path>.";
        }

        if (ProbeClientMirrorPosition && AttachExistingHost)
        {
            return "--probe-client-mirror-position requires launching a fresh TS3 host; remove --attach-existing.";
        }

        if (ProbeClientMirrorPosition && string.IsNullOrEmpty(ClientMirrorTs3Path))
        {
            return "--probe-client-mirror-position requires --client-mirror-ts3 <path>.";
        }

        if (ProbeClientMirrorPosition && string.IsNullOrEmpty(ClientMirrorSaveName))
        {
            return "--probe-client-mirror-position requires --client-mirror-save <name>.";
        }

        if (ProbeClientMirrorPosition && string.IsNullOrEmpty(ClientMirrorBinaryPath))
        {
            return "--probe-client-mirror-position requires --client-mirror-binary <path>.";
        }

        if (ProbeClientMirrorPosition && (ProbeSimObjectId is null || GoHereX is null || GoHereY is null || GoHereZ is null))
        {
            return "--probe-client-mirror-position requires --sim <objectId> --x <x> --y <y> --z <z>.";
        }

        if (ObjectGuidSampleSize <= 0)
        {
            return "--object-guid-sample-size must be greater than zero.";
        }

        if (ProbeStateInjection && (ProbeSimObjectId is null || GoHereX is null || GoHereY is null || GoHereZ is null))
        {
            return "--probe-state-injection requires --sim <objectId> --x <x> --y <y> --z <z>.";
        }

        if (GoHere && (ProbeSimObjectId is null || GoHereX is null || GoHereY is null || GoHereZ is null))
        {
            return "--go-here requires --sim <objectId> --x <x> --y <y> --z <z>.";
        }

        return null;
    }

    public static SessionConfig FromArgs(IReadOnlyList<string> args)
    {
        string ts3Path = @"C:\Program Files (x86)\The Sims 3\Game\Bin\TS3W.exe";
        string saveName = "SimsOnlineModTempWorld.sims3";
        string? checkpointSaveName = Environment.GetEnvironmentVariable("SIMSONLINE_CHECKPOINT_SAVE");
        string listenEndpoint = "127.0.0.1:34019";
        string hostBridgeEndpoint = "memory:TS3W";
        TimeSpan hostBridgeTimeout = TimeSpan.FromSeconds(120);
        string? ts3SandboxieBoxName = null;
        string? ts3SandboxieStartPath = null;
        bool attachExistingHost = false;
        bool hostProbeOnly = false;
        bool probeOnly = false;
        bool snapshotAndExit = false;
        bool probeObjectGuidStability = false;
        int objectGuidSampleSize = 5;
        bool probeSimLifecycle = false;
        bool probeStateInjection = false;
        bool probeLoadPathComparison = false;
        bool probeAutonomy = false;
        bool probeActorLookupParity = false;
        bool probeRoleSwitch = false;
        bool probeMotiveRoundtrip = false;
        bool probeInteractionRoundtrip = false;
        bool probeCashRoundtrip = false;
        bool probeSkillRoundtrip = false;
        bool probeAutonomyRoundtrip = false;
        string? motiveName = null;
        float? motiveValue = null;
        ulong? probeHouseholdId = null;
        int? cashAmount = null;
        string? skillName = null;
        int? skillLevel = null;
        bool goHere = false;
        ulong? probeSimObjectId = null;
        float? goHereX = null;
        float? goHereY = null;
        float? goHereZ = null;
        string? serverModPackagePath = null;
        bool probeClientMirrorStartup = false;
        bool probeClientMirrorPosition = false;
        string? clientMirrorTs3Path = null;
        string? clientMirrorSaveName = null;
        string? clientMirrorBinaryPath = null;
        string? clientMirrorSandboxieBoxName = null;
        string? clientMirrorSandboxieStartPath = null;
        string? clientMirrorWineprefixPath = null;
        string startSpeedName = "normal";
        bool probeLiveSnapshot = false;
        bool probeClaimAutonomy = false;
        TimeSpan? playerCommandTimeout = null;
        bool bridgeTrace = false;
        bool probeBridgeResponseLoss = false;
        int? attachPid = null;
        string? bridgectlEndpoint = null;
        string? bridgeRecordPath = null;
        TimeSpan? syncBroadcastInterval = null;
        // P5.6 knobs
        long? resyncWarnTicks = null;
        long? resyncHardTicks = null;
        double? resyncMaxIntervalSeconds = null;
        int? maxFoldPerCycle = null;
        double? resyncCooldownSeconds = null;
        bool disableInterestScopedMirroring = false;
        string? stateFilePath = null;
        string? sentryDsnEnv = null;
        string sentryEnvironment = "development";
        bool sentrySendDefaultPii = false;
        double? sentryTracesSampleRate = null;
        double? sentryProfilesSampleRate = null;
        bool sentryEnableMetrics = true;
        bool sentryDisableProfiling = false;
        string? sentryReleaseOverride = null;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg == "--ts3" && i + 1 < args.Count)
            {
                ts3Path = args[++i];
            }
            else if (arg == "--save" && i + 1 < args.Count)
            {
                saveName = args[++i];
            }
            else if (arg == "--checkpoint-save" && i + 1 < args.Count)
            {
                checkpointSaveName = args[++i];
            }
            else if (arg == "--listen" && i + 1 < args.Count)
            {
                listenEndpoint = args[++i];
            }
            else if (arg == "--host-bridge" && i + 1 < args.Count)
            {
                hostBridgeEndpoint = args[++i];
            }
            else if (arg == "--host-timeout-seconds" && i + 1 < args.Count)
            {
                hostBridgeTimeout = TimeSpan.FromSeconds(int.Parse(args[++i]));
            }
            else if (arg == "--player-command-timeout-seconds" && i + 1 < args.Count)
            {
                playerCommandTimeout = TimeSpan.FromSeconds(int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (arg == "--bridge-trace")
            {
                bridgeTrace = true;
            }
            else if (arg == "--probe-bridge-response-loss")
            {
                probeBridgeResponseLoss = true;
            }
            else if (arg == "--ts3-sandboxie-box" && i + 1 < args.Count)
            {
                ts3SandboxieBoxName = args[++i];
            }
            else if (arg == "--ts3-sandboxie-start" && i + 1 < args.Count)
            {
                ts3SandboxieStartPath = args[++i];
            }
            else if (arg == "--attach-existing")
            {
                attachExistingHost = true;
            }
            else if (arg == "--attach-pid" && i + 1 < args.Count)
            {
                attachPid = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (arg == "--bridgectl-listen" && i + 1 < args.Count)
            {
                bridgectlEndpoint = args[++i];
            }
            else if (arg == "--bridge-record" && i + 1 < args.Count)
            {
                bridgeRecordPath = args[++i];
            }
            else if (arg == "--host-probe")
            {
                hostProbeOnly = true;
            }
            else if (arg == "--probe")
            {
                probeOnly = true;
            }
            else if (arg == "--snapshot-and-exit")
            {
                snapshotAndExit = true;
            }
            else if (arg == "--probe-object-guid-stability")
            {
                probeObjectGuidStability = true;
            }
            else if (arg == "--object-guid-sample-size" && i + 1 < args.Count)
            {
                objectGuidSampleSize = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (arg == "--probe-sim-lifecycle")
            {
                probeSimLifecycle = true;
            }
            else if (arg == "--probe-state-injection")
            {
                probeStateInjection = true;
            }
            else if (arg == "--probe-actor-lookup-parity")
            {
                probeActorLookupParity = true;
            }
            else if (arg == "--probe-role-switch")
            {
                probeRoleSwitch = true;
            }
            else if (arg == "--probe-motive-roundtrip")
            {
                probeMotiveRoundtrip = true;
            }
            else if (arg == "--probe-interaction-roundtrip")
            {
                probeInteractionRoundtrip = true;
            }
            else if (arg == "--probe-cash-roundtrip")
            {
                probeCashRoundtrip = true;
            }
            else if (arg == "--probe-skill-roundtrip")
            {
                probeSkillRoundtrip = true;
            }
            else if (arg == "--skill" && i + 1 < args.Count)
            {
                skillName = args[++i];
            }
            else if (arg == "--skill-level" && i + 1 < args.Count)
            {
                skillLevel = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (arg == "--household" && i + 1 < args.Count)
            {
                probeHouseholdId = ulong.Parse(args[++i]);
            }
            else if (arg == "--funds" && i + 1 < args.Count)
            {
                cashAmount = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (arg == "--motive")
            {
                motiveName = args[++i];
            }
            else if (arg == "--motive-value")
            {
                motiveValue = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (arg == "--probe-load-path-comparison")
            {
                probeLoadPathComparison = true;
            }
            else if (arg == "--probe-autonomy-roundtrip")
            {
                probeAutonomyRoundtrip = true;
            }
            else if (arg == "--probe-autonomy")
            {
                probeAutonomy = true;
            }
            else if (arg == "--go-here")
            {
                goHere = true;
            }
            else if (arg == "--sim" && i + 1 < args.Count)
            {
                probeSimObjectId = ulong.Parse(args[++i]);
            }
            else if (arg == "--x" && i + 1 < args.Count)
            {
                goHereX = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (arg == "--y" && i + 1 < args.Count)
            {
                goHereY = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (arg == "--z" && i + 1 < args.Count)
            {
                goHereZ = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (arg == "--server-mod" && i + 1 < args.Count)
            {
                serverModPackagePath = args[++i];
            }
            else if (arg == "--probe-client-mirror-startup")
            {
                probeClientMirrorStartup = true;
            }
            else if (arg == "--probe-client-mirror-position")
            {
                probeClientMirrorPosition = true;
            }
            else if (arg == "--client-mirror-ts3" && i + 1 < args.Count)
            {
                clientMirrorTs3Path = args[++i];
            }
            else if (arg == "--client-mirror-save" && i + 1 < args.Count)
            {
                clientMirrorSaveName = args[++i];
            }
            else if (arg == "--client-mirror-binary" && i + 1 < args.Count)
            {
                clientMirrorBinaryPath = args[++i];
            }
            else if (arg == "--client-mirror-sandboxie-box" && i + 1 < args.Count)
            {
                clientMirrorSandboxieBoxName = args[++i];
            }
            else if (arg == "--client-mirror-sandboxie-start" && i + 1 < args.Count)
            {
                clientMirrorSandboxieStartPath = args[++i];
            }
            else if (arg == "--client-mirror-wineprefix" && i + 1 < args.Count)
            {
                clientMirrorWineprefixPath = args[++i];
            }
            else if (arg == "--start-speed" && i + 1 < args.Count)
            {
                startSpeedName = args[++i];
            }
            else if (arg == "--probe-live-snapshot")
            {
                probeLiveSnapshot = true;
            }
            else if (arg == "--probe-claim-autonomy")
            {
                probeClaimAutonomy = true;
            }
            else if (arg == "--sync-broadcast-seconds" && i + 1 < args.Count)
            {
                // P5.3 dev default: 2s. Documented here -- run with --sync-broadcast-seconds 2 for dev.
                int seconds = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                syncBroadcastInterval = seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
            }
            // P5.6 resync policy knobs
            else if (arg == "--resync-warn-ticks" && i + 1 < args.Count)
            {
                resyncWarnTicks = long.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (arg == "--resync-hard-ticks" && i + 1 < args.Count)
            {
                resyncHardTicks = long.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (arg == "--resync-max-interval-seconds" && i + 1 < args.Count)
            {
                resyncMaxIntervalSeconds = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (arg == "--max-fold-per-cycle" && i + 1 < args.Count)
            {
                maxFoldPerCycle = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (arg == "--resync-cooldown-seconds" && i + 1 < args.Count)
            {
                resyncCooldownSeconds = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (arg == "--no-interest-scope")
            {
                disableInterestScopedMirroring = true;
            }
            else if (arg == "--state-file" && i + 1 < args.Count)
            {
                stateFilePath = args[++i];
            }
            else if (arg == "--sentry-dsn-env" && i + 1 < args.Count)
            {
                sentryDsnEnv = args[++i];
            }
            else if (arg == "--sentry-environment" && i + 1 < args.Count)
            {
                sentryEnvironment = args[++i];
            }
            else if (arg == "--sentry-send-default-pii")
            {
                sentrySendDefaultPii = true;
            }
            else if (arg == "--sentry-traces-sample-rate" && i + 1 < args.Count)
            {
                sentryTracesSampleRate = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (arg == "--sentry-profiles-sample-rate" && i + 1 < args.Count)
            {
                sentryProfilesSampleRate = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (arg == "--sentry-disable-metrics")
            {
                sentryEnableMetrics = false;
            }
            else if (arg == "--sentry-disable-profiling")
            {
                sentryDisableProfiling = true;
            }
            else if (arg == "--sentry-release" && i + 1 < args.Count)
            {
                sentryReleaseOverride = args[++i];
            }
        }

        return new SessionConfig(
            ts3Path,
            saveName,
            listenEndpoint,
            hostBridgeEndpoint,
            hostBridgeTimeout,
            ts3SandboxieBoxName,
            ts3SandboxieStartPath,
            attachExistingHost,
            hostProbeOnly,
            probeOnly,
            snapshotAndExit,
            probeObjectGuidStability,
            objectGuidSampleSize,
            probeSimLifecycle,
            probeStateInjection,
            probeLoadPathComparison,
            probeAutonomy,
            probeActorLookupParity,
            probeRoleSwitch,
            probeMotiveRoundtrip,
            probeInteractionRoundtrip,
            probeCashRoundtrip,
            probeSkillRoundtrip,
            probeAutonomyRoundtrip,
            motiveName,
            motiveValue,
            probeHouseholdId,
            cashAmount,
            skillName,
            skillLevel,
            goHere,
            probeSimObjectId,
            goHereX,
            goHereY,
            goHereZ,
            serverModPackagePath,
            probeClientMirrorStartup,
            probeClientMirrorPosition,
            clientMirrorTs3Path,
            clientMirrorSaveName,
            clientMirrorBinaryPath,
            clientMirrorSandboxieBoxName,
            clientMirrorSandboxieStartPath,
            clientMirrorWineprefixPath,
            startSpeedName,
            probeLiveSnapshot,
            probeClaimAutonomy,
            playerCommandTimeout,
            bridgeTrace,
            probeBridgeResponseLoss,
            attachPid,
            bridgectlEndpoint,
            bridgeRecordPath,
            syncBroadcastInterval,
            resyncWarnTicks,
            resyncHardTicks,
            resyncMaxIntervalSeconds,
            maxFoldPerCycle,
            resyncCooldownSeconds,
            disableInterestScopedMirroring,
            stateFilePath,
            sentryDsnEnv,
            sentryEnvironment,
            sentrySendDefaultPii,
            sentryTracesSampleRate,
            sentryProfilesSampleRate,
            sentryEnableMetrics,
            sentryDisableProfiling,
            sentryReleaseOverride,
            checkpointSaveName);
    }
}
