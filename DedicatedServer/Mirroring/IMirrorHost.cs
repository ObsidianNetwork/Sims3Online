using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Mirroring;

/// <summary>
/// M4 P4.2.2.1 — client-side mirror dispatch surface.
///
/// The <see cref="MirrorEngine"/> translates incoming <see cref="ServerEvent"/>s via
/// <see cref="EventToBridgeCommandTranslator"/> and dispatches each emitted
/// <see cref="HostBridge.BridgeCommand"/> to an implementation of this interface —
/// in production a client-role <c>HeadlessSims3Host</c> driving a local TS3.
///
/// Signatures match <c>HeadlessSims3Host</c>'s existing typed APIs verbatim so the
/// production host implements this contract with no adapter or method-body changes.
/// </summary>
public interface IMirrorHost
{
    Task ApplyPositionAsync(ulong simObjectId, Vector3Dto targetPosition, CancellationToken cancellationToken);

    /// <summary>batched position write — applies all (simId, position) pairs in one bridge
    /// round-trip (one APPLY_POSITIONS frame, one simulator tick). Signature matches
    /// <c>HeadlessSims3Host.ApplyPositionsAsync</c> verbatim so the production swap is constructor-only.</summary>
    Task ApplyPositionsAsync(IReadOnlyList<(ulong SimObjectId, Vector3Dto Position)> positions, CancellationToken cancellationToken);

    Task ApplyMotiveAsync(ulong simObjectId, string motiveName, float value, CancellationToken cancellationToken);

    Task ApplyCashAsync(ulong householdId, int funds, CancellationToken cancellationToken);

    Task ApplySkillAsync(ulong simObjectId, string skillName, int level, CancellationToken cancellationToken);

    /// <summary>M4 P4.3: client-role state write for Sim autonomy (idempotent absolute-set; disabled=true wraps Sim.Autonomy.IncrementAutonomyDisabled when currently enabled).</summary>
    Task ApplyAutonomyAsync(ulong simObjectId, bool disabled, CancellationToken cancellationToken);

    Task ApplyInteractionQueueAsync(InteractionRequest interaction, CancellationToken cancellationToken);

    /// <summary>batched interaction replay — one or more APPLY_INTERACTIONS bridge round-trips.</summary>
    Task ApplyInteractionsAsync(IReadOnlyList<InteractionRequest> interactions, CancellationToken cancellationToken);

    /// <summary>posture/container alignment (Sit transition on authoritative seat first).</summary>
    Task ApplyForceAlignAsync(InteractionReplayBundle bundle, CancellationToken cancellationToken);

    /// <summary>P5.0: time/speed authority — apply the server-decided game speed to the local client TS3. Reuses the existing ungated SET_GAME_SPEED bridge command; no role gate because the command is controller-driven, not player-driven.</summary>
    Task SetGameSpeedAsync(GameSpeed speed, CancellationToken cancellationToken);

    /// <summary>absolute clock authority — apply server world time to the local client TS3 via SET_WORLD_TIME.</summary>
    Task SetWorldTimeAsync(WorldTime time, CancellationToken cancellationToken);

    /// <summary>Dev tooling: show an in-game toast in the local TS3 (claims, role changes, forwarding feedback during manual tests). Best-effort — callers should swallow failures.</summary>
    Task ShowNotificationAsync(string text, CancellationToken cancellationToken);

    /// <summary>make a Sim the local TS3's active Sim (SET_ACTIVE_SIM; the mod resolves a server-aliased id and does nothing when it is already active).</summary>
    Task SetActiveSimAsync(ulong simObjectId, CancellationToken cancellationToken);

    /// <summary>show a remote dialog of the player's Sim with the local TS3's own UI (SHOW_DIALOG). The
    /// local mod answers asynchronously with EVENT|DialogAnswer.</summary>
    Task ShowDialogAsync(int requestId, ulong simObjectId, string kind, IReadOnlyList<string> args, CancellationToken cancellationToken);

    /// <summary>
    /// Step 0: poll <c>DIAG_INTERACTION_STATE</c> for posture/container settle after ForceAlign.
    /// Return null when the host cannot query (logging-only mirror); <see cref="MirrorEngine"/> skips poll.
    /// </summary>
    Task<MirroredInteractionState?> QueryInteractionStateAsync(ulong simObjectId, CancellationToken cancellationToken);

    /// <summary>
    /// queue ReadBookChooser/GetBook on the client bridge so the engine runs
    /// the native book chain instead of terminal ReadBook replay.
    /// </summary>
    Task InjectReadIntentAsync(
        ulong simObjectId,
        ulong bookObjectId,
        string mode,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// W2: set the client authority gate on the local TS3 (SET_AUTHORITY; forked AuthorityHook).
    /// Returns the mod's report (<c>hook=present</c> / <c>hook=absent</c>), or null when the host has
    /// no bridge (logging-only mirror).
    /// </summary>
    Task<string?> SetAuthorityAsync(
        HostBridge.AuthorityGateMode mode,
        IReadOnlyList<ulong> nativeSimObjectIds,
        CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    /// <summary>W2 part B: apply the server's visible state of a Sim this client does not own
    /// (APPLY_SIM_STATE). No-op on hosts without a bridge.</summary>
    Task ApplySimStateAsync(SimStateSample state, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>W2 Sim identity step 3: instantiate this client's copy of the server's Sim (SPAWN_SIM).
    /// Returns the mod's report, or null on hosts without a bridge.</summary>
    Task<string?> SpawnSimAsync(ulong simObjectId, ulong simDescriptionId, Vector3Dto position, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    /// <summary>import the server's description of a Sim this client lacks (IMPORT_SIM_DESCRIPTION), so
    /// SPAWN_SIM can instantiate it. Returns the mod's report, or null on hosts without a bridge.</summary>
    Task<string?> ImportSimDescriptionAsync(SimDescriptionPayload payload, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    /// <summary> step 5: bring this client's copy of a Sim to the server's job, funds and skills
    /// (APPLY_SIM_DETAILS). Returns the mod's report, or null on hosts without a bridge.</summary>
    Task<string?> ApplySimDetailsAsync(SimDetailsSample details, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    /// <summary>W2 Sim identity step 3: remove this client's copy of a Sim the server removed (DESPAWN_SIM).</summary>
    Task<string?> DespawnSimAsync(ulong simObjectId, ulong simDescriptionId, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    /// <summary>W2 Sim identity: record server ObjectId -> SimDescriptionId aliases on the client TS3
    /// (SET_SIM_ALIASES). Returns the mod's report, or null on hosts without a bridge.</summary>
    Task<string?> SetSimAliasesAsync(
        IReadOnlyList<HostBridge.SimAlias> aliases,
        bool reset,
        CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    /// <summary> presentation stream: replay the server's recorded animation calls on this
    /// client's instances (SACS_REPLAY, one bridge frame). Returns the mod's report, or null on hosts
    /// without a bridge.</summary>
    Task<string?> SacsReplayAsync(IReadOnlyList<string> lines, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
}
