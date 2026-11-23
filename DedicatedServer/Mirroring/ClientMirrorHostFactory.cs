using SimsOnline.DedicatedServer.Authority;

namespace SimsOnline.DedicatedServer.Mirroring;

/// <summary>
/// M4 P4.2.2.2.2 — factory that decides whether the client-side mirror loop
/// dispatches into an observability-only <see cref="LoggingMirrorHost"/> or into a real
/// client-role <see cref="HeadlessSims3Host"/> driving a local TS3 instance.
///
/// The factory is the seam that lets SimsOnline.Client stay testable without launching
/// a real TS3 process: tests exercise <see cref="Create"/> with various option combos
/// and inspect the returned <see cref="Result"/> shape; production callers consume the
/// returned <see cref="IMirrorHost"/> and (when <see cref="Result.OwnedHost"/> is non-null)
/// run the StartAsync -> SetRoleAsync(Client) -> LoadSaveAsync sequence + teardown.
///
/// Modes:
/// * "disabled" — <see cref="Options.EnableMirror"/> is false. No host. Engine is null.
/// * "logging" — mirror enabled, no client-TS3 flags. Returns a <see cref="LoggingMirrorHost"/>
///   targeting the supplied <see cref="Options.Writer"/>. This is the default behavior;
///   tests + the P4.2.7 subprocess integration smoke depend on it remaining unchanged.
/// * "client-headless" — mirror enabled AND both <see cref="Options.ClientTs3Path"/> +
///   <see cref="Options.ClientSaveName"/> supplied. Constructs a <see cref="SessionConfig"/>
///   with all probe flags false and the client TS3 path/save/bridge endpoint, then wraps
///   it in a <see cref="HeadlessSims3Host"/>. The caller is responsible for the lifecycle.
///
/// Half-configurations (one of --client-ts3 / --client-save without the other) are rejected
/// with <see cref="ArgumentException"/> at factory time so the failure surfaces before the
/// TCP connect attempt, not during the bridge handshake.
/// </summary>
public static class ClientMirrorHostFactory
{
    public sealed record Options(
        bool EnableMirror,
        bool JsonStdout,
        TextWriter Writer,
        string? ClientTs3Path,
        string? ClientSaveName,
        string? ClientHostBridgeEndpoint,
        int ClientHostTimeoutSeconds,
        string? ClientTs3SandboxieBoxName,
        string? ClientTs3SandboxieStartPath,
        // 2026-07-03 two-client Linux: isolated Wine prefix for the second TS3 (Sandboxie's role on Windows).
        string? ClientTs3WineprefixPath = null,
        // 2026-07-03 dev-tooling: client controller state file (mirrors the server's RunStateFile).
        // dev-session passes --state-file .omo/run/client[2].state.json so wait-stack-ready can poll
        // client readiness without grepping the buffered client.log (which stays empty under Wine's
        // Start-Process redirect).
        string? ClientStateFilePath = null,
        // mirror applies are player-paced commands; null uses
        // SessionConfig.DefaultPlayerCommandTimeout (a wedged echo apply must fail in
        // seconds instead of blocking the client bridge for the full host timeout).
        int? ClientPlayerCommandTimeoutSeconds = null,
        // Warm re-attach: the client TS3 with this process id is already running and in the world (a controller
        // restart against it rejoins the session in seconds instead of a TS3 boot).
        int? ClientAttachPid = null,
        // n40: --client-saves-dir, so the fork census covers the same profiles as stackctl's client census.
        string? ClientSavesDirectory = null);

    public sealed record Result(
        IMirrorHost? Host,
        LoggingMirrorHost? LoggingHost,
        HeadlessSims3Host? OwnedHost,
        string Mode);

    /// <summary>
    /// client-headless host can run native full-fidelity sim when lot-local mode is
    /// <see cref="LotLocalAuthorityMode.SoloNative"/> (mirror puppet idle for owned Sims).
    /// </summary>
    public static bool SupportsSoloNativeBootstrap(Result result) =>
        result.OwnedHost != null && result.Mode == "client-headless";

    public static Result Create(Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Writer);

        if (!options.EnableMirror)
        {
            return new Result(Host: null, LoggingHost: null, OwnedHost: null, Mode: "disabled");
        }

        bool clientModeRequested =
            !string.IsNullOrEmpty(options.ClientTs3Path)
            || !string.IsNullOrEmpty(options.ClientSaveName);

        if (clientModeRequested)
        {
            if (string.IsNullOrEmpty(options.ClientTs3Path))
            {
                throw new ArgumentException(
                    "Client-mode mirroring requires both --client-ts3 and --client-save; --client-save was supplied but --client-ts3 was not.",
                    nameof(options));
            }
            if (string.IsNullOrEmpty(options.ClientSaveName))
            {
                throw new ArgumentException(
                    "Client-mode mirroring requires both --client-ts3 and --client-save; --client-ts3 was supplied but --client-save was not.",
                    nameof(options));
            }

            SessionConfig clientConfig = BuildClientSessionConfig(options);
            HeadlessSims3Host headless = new(clientConfig);
            return new Result(Host: headless, LoggingHost: null, OwnedHost: headless, Mode: "client-headless");
        }

        LoggingMirrorHost logging = new(options.Writer, options.JsonStdout);
        return new Result(Host: logging, LoggingHost: logging, OwnedHost: null, Mode: "logging");
    }

    public static SessionConfig BuildClientSessionConfig(Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrEmpty(options.ClientTs3Path))
        {
            throw new ArgumentException("ClientTs3Path must be non-empty.", nameof(options));
        }
        if (string.IsNullOrEmpty(options.ClientSaveName))
        {
            throw new ArgumentException("ClientSaveName must be non-empty.", nameof(options));
        }

        return new SessionConfig(
            Ts3ExecutablePath: options.ClientTs3Path!,
            SaveName: options.ClientSaveName!,
            ListenEndpoint: "127.0.0.1:0",
            HostBridgeEndpoint: string.IsNullOrEmpty(options.ClientHostBridgeEndpoint) ? "memory:TS3W" : options.ClientHostBridgeEndpoint!,
            HostBridgeTimeout: TimeSpan.FromSeconds(options.ClientHostTimeoutSeconds),
            Ts3SandboxieBoxName: options.ClientTs3SandboxieBoxName,
            Ts3SandboxieStartPath: options.ClientTs3SandboxieStartPath,
            AttachExistingHost: options.ClientAttachPid is not null,
            HostProbeOnly: false,
            ProbeOnly: false,
            SnapshotAndExit: false,
            ProbeObjectGuidStability: false,
            ObjectGuidSampleSize: 5,
            ProbeSimLifecycle: false,
            ProbeStateInjection: false,
            ProbeLoadPathComparison: false,
            ProbeAutonomy: false,
            ProbeActorLookupParity: false,
            ProbeRoleSwitch: false,
            ProbeMotiveRoundtrip: false,
            ProbeInteractionRoundtrip: false,
            ProbeCashRoundtrip: false,
            ProbeSkillRoundtrip: false,
            ProbeAutonomyRoundtrip: false,
            MotiveName: null,
            MotiveValue: null,
            ProbeHouseholdId: null,
            CashAmount: null,
            SkillName: null,
            SkillLevel: null,
            GoHere: false,
            ProbeSimObjectId: null,
            GoHereX: null,
            GoHereY: null,
            GoHereZ: null,
            ServerModPackagePath: null,
            ProbeClientMirrorStartup: false,
            ProbeClientMirrorPosition: false,
            ClientMirrorTs3Path: null,
            ClientMirrorSaveName: null,
            ClientMirrorBinaryPath: null,
            ClientMirrorSandboxieBoxName: null,
            ClientMirrorSandboxieStartPath: null,
            ClientMirrorWineprefixPath: options.ClientTs3WineprefixPath,
            StateFilePath: options.ClientStateFilePath,
            PlayerCommandTimeout: options.ClientPlayerCommandTimeoutSeconds is int seconds
                ? TimeSpan.FromSeconds(seconds)
                : null,
            AttachPid: options.ClientAttachPid,
            ClientSavesDirectory: options.ClientSavesDirectory);
    }
}
