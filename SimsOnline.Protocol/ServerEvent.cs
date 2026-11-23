using System.Text.Json.Serialization;
using SimsOnline.DedicatedServer.Mirroring;

namespace SimsOnline.DedicatedServer.Protocol;

// P3 (M3 Two-Process Coordination) public protocol: ServerEvent subtypes are pushed
// to clients over the public TCP listener as newline-delimited JSON with these
// polymorphic discriminator names. Stable across versions.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(SessionJoined), "session_joined")]
[JsonDerivedType(typeof(CommandAccepted), "command_accepted")]
[JsonDerivedType(typeof(CommandRejected), "command_rejected")]
[JsonDerivedType(typeof(SimClaimed), "sim_claimed")]
[JsonDerivedType(typeof(SimReleased), "sim_released")]
[JsonDerivedType(typeof(InteractionQueued), "interaction_queued")]
[JsonDerivedType(typeof(InteractionReplayQueued), "interaction_replay_queued")]
[JsonDerivedType(typeof(InteractionStarted), "interaction_started")]
[JsonDerivedType(typeof(InteractionCompleted), "interaction_completed")]
[JsonDerivedType(typeof(InteractionFailed), "interaction_failed")]
[JsonDerivedType(typeof(SnapshotDelta), "snapshot_delta")]
[JsonDerivedType(typeof(FullSnapshot), "full_snapshot")]
[JsonDerivedType(typeof(GameSpeedChanged), "game_speed_changed")]
[JsonDerivedType(typeof(WorldTimeChanged), "world_time_changed")]
[JsonDerivedType(typeof(SaveCheckpointed), "save_checkpointed")]
[JsonDerivedType(typeof(ServerError), "server_error")]
[JsonDerivedType(typeof(ClientRoleChanged), "client_role_changed")]
[JsonDerivedType(typeof(SimAutonomyChanged), "sim_autonomy_changed")]
[JsonDerivedType(typeof(IntentCaptured), "intent_captured")]
[JsonDerivedType(typeof(LotLocalAuthorityModeChanged), "lot_local_authority_mode_changed")]
[JsonDerivedType(typeof(LotExportRequested), "lot_export_requested")]
[JsonDerivedType(typeof(LotReconcileAcknowledged), "lot_reconcile_acked")]
[JsonDerivedType(typeof(SimSkillChanged), "sim_skill_changed")]
[JsonDerivedType(typeof(HouseholdFundsChanged), "household_funds_changed")]
[JsonDerivedType(typeof(SimMotiveChanged), "sim_motive_changed")]
[JsonDerivedType(typeof(SimStateChanged), "sim_state_changed")]
[JsonDerivedType(typeof(SimSpawned), "sim_spawned")]
[JsonDerivedType(typeof(SimDespawned), "sim_despawned")]
[JsonDerivedType(typeof(SimDescriptionData), "sim_description_data")]
[JsonDerivedType(typeof(SimDetailsChanged), "sim_details_changed")]
[JsonDerivedType(typeof(WorldFileChunk), "world_file_chunk")]
[JsonDerivedType(typeof(SimAnimation), "sim_animation")]
[JsonDerivedType(typeof(UserIntentCaptured), "user_intent_captured")]
[JsonDerivedType(typeof(CancelIntentCaptured), "cancel_intent_captured")]
[JsonDerivedType(typeof(LeaveConversationIntentCaptured), "leave_conversation_intent_captured")]
[JsonDerivedType(typeof(DialogRequested), "dialog_requested")]
[JsonDerivedType(typeof(DialogAnswerCaptured), "dialog_answer_captured")]
public abstract record ServerEvent
{
    /// <summary>Notice: a line for the joining player's game to show, or null. Set when the joiner's on-screen
    /// walk differs from the server's (OnScreenWalk.Mismatch). Broadcast like the rest: only PlayerId's client shows it.
    /// Capabilities: <see cref="ServerCapability"/> names for what this server does; null from a server that
    /// predates the field. A client ignores a name it does not know.</summary>
    public sealed record SessionJoined(int PlayerId, string DisplayName, string? Notice = null, IReadOnlyList<string>? Capabilities = null) : ServerEvent;

    public sealed record CommandAccepted(Guid CommandId) : ServerEvent;

    public sealed record CommandRejected(Guid CommandId, string Reason) : ServerEvent;

    public sealed record SimClaimed(int PlayerId, ulong SimObjectId, ulong? LotId = null) : ServerEvent;

    public sealed record SimReleased(int PlayerId, ulong SimObjectId) : ServerEvent;

    /// <summary>M4 P4.2.3: InteractionQueued now carries optional target object id and position so the mirror engine can synthesise APPLY_INTERACTION_QUEUE (currently Terrain.GoHere only). Null target means the source-side interaction had no target (autonomy-watcher emit) - translator skips those.</summary>
    /// <summary>
    /// BQ (interaction-parameters-wire-spec.md): optional semantic parameter map (bookId,
    /// progressNormalized, seatArea, seatContainmentSlot, ...) so the public path carries the
    /// same keys the replay-bundle path already parses. Additive and wire-compatible: omitted
    /// or empty on sources that do not stamp parameters; JSON serializes camelCase
    /// <c>parameters</c>; null decodes from old-shape payloads.
    /// </summary>
    public sealed record InteractionQueued(ulong ActorObjectId, ulong InteractionId, string InteractionKey, ulong? TargetObjectId, Vector3Dto? TargetPosition, IReadOnlyDictionary<string, string>? Parameters = null) : ServerEvent;

    public sealed record InteractionReplayQueued(InteractionReplayBundle Bundle) : ServerEvent;

    public sealed record InteractionStarted(ulong ActorObjectId, ulong InteractionId) : ServerEvent;

    public sealed record InteractionCompleted(ulong ActorObjectId, ulong InteractionId) : ServerEvent;

    public sealed record InteractionFailed(ulong ActorObjectId, ulong InteractionId, string Reason) : ServerEvent;

    public sealed record IntentCaptured(ulong ActorObjectId, ulong TargetObjectId, string InteractionKey, Vector3Dto? TargetPosition) : ServerEvent;

    /// <summary>P5 speed-input capture: client-role TS3 UI speed change forwarded to the server.</summary>
    public sealed record SpeedIntentCaptured(GameSpeed Speed) : ServerEvent;

    /// <summary> remote dialogs: a dialog the server TS3 opened in a claimed Sim's interaction
    /// (EVENT|DialogRequest). The session sends it to the claiming player only; its client shows it and
    /// answers with AnswerDialog. Kinds: TwoButton, AcceptCancel, ThreeButton, StringInput, Message.</summary>
    public sealed record DialogRequested(int RequestId, ulong SimObjectId, string Kind, IReadOnlyList<string> Args) : ServerEvent;

    /// <summary>the player's answer from the client's own TS3 (EVENT|DialogAnswer); null = none, the
    /// server keeps the dialog's default. The client controller forwards it as AnswerDialog.</summary>
    public sealed record DialogAnswerCaptured(int RequestId, ulong SimObjectId, string? Answer) : ServerEvent;

    /// <summary> queue mirror: the player cancelled a mirrored queue item in the client's HUD
    /// (EVENT|CancelIntent). The client controller forwards it as CancelInteraction; ids are the server's.</summary>
    public sealed record CancelIntentCaptured(ulong ActorObjectId, ulong InteractionId) : ServerEvent;

    /// <summary> n14: the player left their Sim's mirrored conversation from the client's HUD
    /// (EVENT|LeaveConversationIntent). Captured input, never broadcast: the client controller forwards it as
    /// LeaveConversation; the id is the server's.</summary>
    public sealed record LeaveConversationIntentCaptured(ulong ActorObjectId) : ServerEvent;

    /// <summary>
    /// input forwarding (docs/engine/input-forwarding.md): a player's pick the client
    /// authority gate blocked, raised by the client's own TS3 (EVENT|UserIntent). The client controller
    /// forwards it as a QueueInteraction carrying <see cref="UserPick"/> parameters; the server resolves
    /// the pick the way the pie menu does. Ids are the server's; TargetObjectId 0 is the terrain.
    /// </summary>
    public sealed record UserIntentCaptured(
        ulong ActorObjectId,
        ulong TargetObjectId,
        string DefinitionType,
        string Name,
        string Path,
        string Hit,
        Vector3Dto? HitPoint,
        string Picked) : ServerEvent
    {
        /// <summary>The QueueInteraction request the client controller sends for this pick.</summary>
        public InteractionRequest ToRequest() => new(
            ActorObjectId,
            DefinitionType,
            TargetObjectId == 0uL ? null : TargetObjectId,
            HitPoint,
            new Dictionary<string, string>
            {
                [UserPick.Name] = Name,
                [UserPick.Path] = Path,
                [UserPick.Hit] = Hit,
                [UserPick.Picked] = Picked,
            });
    }

    /// <summary>
    /// P5.6: <see cref="ForceFull"/> is additive and wire-compatible — existing
    /// clients that do not know this field ignore it (JSON deserializer default = false).
    /// When true the client must treat this delta as a full-world replacement (drain + discard
    /// the backlog, apply from this snapshot, set <c>lastAppliedTick</c>). The server sets it
    /// when the lag crosses <see cref="ResyncThresholds.HardTicks"/>; it rides the existing
    /// <c>snapshot_delta</c> discriminator so no ProtocolVersion bump is required.
    /// </summary>
    public sealed record SnapshotDelta(SimulationSnapshot Snapshot, bool ForceFull = false) : ServerEvent;

    public sealed record FullSnapshot(SimulationSnapshot Snapshot) : ServerEvent;

    public sealed record GameSpeedChanged(GameSpeed Speed) : ServerEvent;

    /// <summary>
    /// (P5-ARCH B): broadcast when the authoritative world clock changes (server-role
    /// <see cref="ClientCommand.SetWorldTime"/> accepted, or server-internal clock set). Mirror
    /// clients apply it via <see cref="IMirrorHost.SetWorldTimeAsync"/> -> SET_WORLD_TIME bridge.
    /// Join-time replay appends this so a late joiner inherits the exact server clock.
    /// </summary>
    public sealed record WorldTimeChanged(WorldTime Time) : ServerEvent;

    public sealed record SaveCheckpointed(string SaveName, DateTimeOffset At) : ServerEvent;

    public sealed record ServerError(string Message) : ServerEvent;

    /// <summary>
    /// M4 P4.1.2: emitted when a player declares a new role via
    /// <see cref="ClientCommand.SetClientRole"/>. Emitted exactly once per actual
    /// role change (idempotent re-sets of the same role do not emit a second event).
    /// </summary>
    public sealed record ClientRoleChanged(int PlayerId, PlayerRole Role) : ServerEvent;

    /// <summary>M4 P4.2.4: server tells clients to update a Sim's autonomy state. Translator emits BridgeCommand.ApplyAutonomy; mirror engine dispatches via IMirrorHost.ApplyAutonomyAsync. Used by AuthoritativeSession (future slice) when ownership changes warrant autonomy suppression on remote-owned Sims.</summary>
    public sealed record SimAutonomyChanged(ulong SimObjectId, bool Disabled) : ServerEvent;

    /// <summary>
    /// mirror a Sim's motive value to client mirrors. The server periodically probes
    /// authoritative motive values and emits this event on detected change. The translator
    /// emits BridgeCommand.ApplyMotive; mirror engine dispatches via IMirrorHost.ApplyMotiveAsync.
    /// </summary>
    public sealed record SimMotiveChanged(ulong SimObjectId, string MotiveName, float Value) : ServerEvent;

    /// <summary>
    /// W2 part B: the server's visible state (six motives in SimStateWire.MotiveNames order, buffs) of
    /// an unclaimed Sim on a player-occupied lot. Clients freeze the heartbeat of Sims they do not own
    /// (fork slice 7) and apply this instead (BridgeCommand.ApplySimState).
    /// </summary>
    public sealed record SimStateChanged(ulong SimObjectId, IReadOnlyList<float> Motives, IReadOnlyList<SimBuffDto> Buffs) : ServerEvent;

    /// <summary>
    /// W2 Sim identity step 3: a Sim the server instantiated after load stands on a player's lot.
    /// Clients put their own instance of that description into the world (BridgeCommand.SpawnSim);
    /// idempotent, resent periodically.
    /// </summary>
    public sealed record SimSpawned(ulong SimObjectId, ulong SimDescriptionId, Vector3Dto Position) : ServerEvent;

    /// <summary>W2 Sim identity step 3: a Sim left the server's world; clients remove their instance
    /// (BridgeCommand.DespawnSim), never one they run natively.</summary>
    public sealed record SimDespawned(ulong SimObjectId, ulong SimDescriptionId) : ServerEvent;

    /// <summary>the server's export of a Sim description a client asked for (RequestSimDescription),
    /// sent to that client only. The client imports it under the same id (BridgeCommand.ImportSimDescription)
    /// and retries the spawn that was missing it.</summary>
    public sealed record SimDescriptionData(ulong SimDescriptionId, byte[] PropertyStream, byte[] ResKeyTable, byte[] ObjectIdTable) : ServerEvent;

    /// <summary> step 5: a claimed Sim's job, household funds or skills changed on the server (or a client
    /// joined). Clients bring their copy to it (BridgeCommand.ApplySimDetails). Skills is the SimDetailsWire form
    /// ("-" or "Name=level+...").</summary>
    /// <summary>Part of the server's world save for a joining client (RequestWorld): file <paramref name="FileIndex"/>
    /// of <paramref name="FileCount"/> in save folder <paramref name="FolderName"/>, bytes from <paramref name="Offset"/>.
    /// Sent in order, to the requesting connection only.</summary>
    public sealed record WorldFileChunk(string FolderName, int FileIndex, int FileCount, string FileName, long Offset, long FileLength, byte[] Data) : ServerEvent;

    public sealed record SimDetailsChanged(ulong SimObjectId, ulong CareerGuid, int CareerLevel, string CareerBranch, int Funds, string Skills) : ServerEvent;

    /// <summary>
    /// presentation stream (Mirroring.PresentationStream): the server TS3's animation calls for
    /// Sims on player lots, as SacsRecorder ring lines ("seq|kind|..."; seq 0 for history snapshots).
    /// Clients replay them on their own instances (BridgeCommand.SacsReplay) and stop replaying
    /// interactions.
    /// </summary>
    public sealed record SimAnimation(IReadOnlyList<string> Lines) : ServerEvent;

    /// <summary>
    /// mirror a household's FamilyFunds value to client mirrors.
    /// Translator emits BridgeCommand.ApplyCash; mirror engine dispatches via IMirrorHost.ApplyCashAsync.
    /// </summary>
    public sealed record HouseholdFundsChanged(ulong HouseholdId, int Funds) : ServerEvent;

    /// <summary>
    /// mirror a Sim's skill level to client mirrors.
    /// Translator emits BridgeCommand.ApplySkill; mirror engine dispatches via IMirrorHost.ApplySkillAsync.
    /// </summary>
    public sealed record SimSkillChanged(ulong SimObjectId, string SkillName, int Level) : ServerEvent;

    /// <summary>
    /// slice 4: per-player lot-local authority mode pushed on transition so the client
    /// mirror loop can suppress owned APPLY_* and resume foreign player Sims in prox_mirror.
    /// </summary>
    public sealed record LotLocalAuthorityModeChanged(int PlayerId, LotLocalAuthorityMode Mode) : ServerEvent;

    /// <summary>
    /// slice 6: controller asks one client in a client-native authority mode to export
    /// its owned active-lot class A bundle (<see cref="ClientCommand.SubmitLotExport"/>) so a
    /// pending or upcoming SAVE_GAME checkpoint can reconcile before the server file is written.
    /// <paramref name="Reason"/> is a diagnostics tag ("checkpoint_freshness", "mode_exit", ...).
    /// </summary>
    public sealed record LotExportRequested(int PlayerId, ulong? LotId, string Reason) : ServerEvent;

    /// <summary>
    /// slice 5 (design §3.1): emitted to the owning client after a lot reconcile
    /// merge is accepted, carrying the new monotonic checkpoint version the controller assigned.
    /// The client stores it as <c>lastReconciledVersion</c> and echoes it back in the next
    /// <see cref="ClientCommand.SubmitLotExport"/> so the server can classify whether the client
    /// saw the last merge (versions match -> no contestable fields) or missed it (version
    /// mismatch -> the server's competing mutation makes a field contestable).
    /// </summary>
    public sealed record LotReconcileAcknowledged(int PlayerId, ulong LotId, long Version) : ServerEvent;
}
