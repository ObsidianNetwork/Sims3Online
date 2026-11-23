using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.HostBridge;

/// <summary>W2 client authority gate mode (wire: off | observe | enforce).</summary>
public enum AuthorityGateMode
{
    Off,
    Observe,
    Enforce,
}

public abstract record BridgeCommand(Guid CorrelationId)
{
    public sealed record LoadSave(Guid CorrelationId, string SaveName) : BridgeCommand(CorrelationId);

    /// <summary>
    /// P2.5 load-path comparison probe variant. The script DLL handles this by calling
    /// Responder.Instance.MainMenuModel.LoadAndTransitionToSaveGame(saveName, metadata) after
    /// GetSaveFileDetails, mirroring the actual menu's load path (force=false with the
    /// UIManager.GetSaveGameMetadata precondition re-check). Compare against the standard
    /// LoadSave (force=true direct transition) to detect divergence.
    /// </summary>
    public sealed record LoadSaveViaMenu(Guid CorrelationId, string SaveName) : BridgeCommand(CorrelationId);

    public sealed record SaveGame(Guid CorrelationId, string SaveName) : BridgeCommand(CorrelationId);

    public sealed record SetGameSpeed(Guid CorrelationId, GameSpeed Speed) : BridgeCommand(CorrelationId);

    /// <summary>
    /// (P5-ARCH B): absolute world-clock set on server or mirror TS3.
    /// Wire: SET_WORLD_TIME|corrId|day|hours|minutes — day is Sims 3 DaysOfTheWeek (0=Sunday..6).
    /// Handler calls SimClockUtils.SetStartTime(day, hours + minutes/60f) ( probe PASS).
    /// </summary>
    public sealed record SetWorldTime(Guid CorrelationId, WorldTime Time) : BridgeCommand(CorrelationId);

    /// <summary>
    /// P3.3 snapshot delta: when SinceTick is provided, the script DLL filters the response to
    /// only include entities whose lastChangedTick is greater than SinceTick. Backward compatible:
    /// null/missing means "full snapshot" (existing behavior). Wire format is
    /// REQUEST_SNAPSHOT|corrId         (no since)
    /// REQUEST_SNAPSHOT|corrId|sinceTick
    /// </summary>
    public sealed record RequestSnapshot(Guid CorrelationId, long? SinceTick = null) : BridgeCommand(CorrelationId);

    public sealed record ProbeSimLifecycle(Guid CorrelationId) : BridgeCommand(CorrelationId);

    /// <summary>
    /// M4 P4.0.1 actor-lookup parity probe. The script DLL handles this by iterating
    /// LotManager.Actors and calling FindSim(sim.ObjectId.Value) on each. Reports total
    /// enumerated, resolved (FindSim returned non-null), and a list of the first 10
    /// failing ObjectIds for diagnostics. Used to verify the snapshot/lookup parity
    /// invariant established in P4.0.
    /// </summary>
    public sealed record ProbeActorLookup(Guid CorrelationId) : BridgeCommand(CorrelationId);

    public sealed record ProbeStateInjection(Guid CorrelationId, ulong SimObjectId, Vector3Dto TargetPosition) : BridgeCommand(CorrelationId);

    public sealed record QueueInteraction(Guid CorrelationId, InteractionRequest Interaction) : BridgeCommand(CorrelationId);

    /// <summary>select the in-world active Sim via PlumbBob.ForceSelectActor.</summary>
    public sealed record SetActiveSim(Guid CorrelationId, ulong SimObjectId) : BridgeCommand(CorrelationId);

    /// <summary>cancel a queued/running interaction by InteractionInstance.Id.</summary>
    public sealed record CancelInteraction(Guid CorrelationId, ulong ActorObjectId, ulong InteractionId) : BridgeCommand(CorrelationId);

    /// <summary> n14, server role: the Sim leaves its conversation through the engine's own cancel.</summary>
    public sealed record LeaveConversation(Guid CorrelationId, ulong ActorObjectId) : BridgeCommand(CorrelationId);

    /// <summary>, server role: hand the player's answer to a remote dialog back to the waiting Sim.</summary>
    public sealed record AnswerDialog(Guid CorrelationId, int RequestId, string? Answer) : BridgeCommand(CorrelationId);

    /// <summary>, client role: show a remote dialog with the client's own UI.</summary>
    public sealed record ShowDialog(Guid CorrelationId, int RequestId, ulong SimObjectId, string Kind, IReadOnlyList<string> Args) : BridgeCommand(CorrelationId);

    // P2 autonomy probe, promoted to a production primitive by (server-side autonomy
    // suppression on claim). CancelRunning=true additionally cancels the Sim's current
    // interaction queue so the first player command takes effect promptly after a claim.
    public sealed record DisableAutonomy(Guid CorrelationId, ulong SimObjectId, bool CancelRunning = false) : BridgeCommand(CorrelationId);

    // Reset=true decrements the engine's autonomy-disabled counter until autonomy is
    // actually enabled (bounded mod-side) — unwinds counters orphaned by a dead controller
    // session that a re-attached controller has no record of.
    public sealed record EnableAutonomy(Guid CorrelationId, ulong SimObjectId, bool Reset = false) : BridgeCommand(CorrelationId);

    /// <summary>
    /// register a claimed Sim for the command-lifecycle autonomy gate on the server-
    /// authority TS3. Registration does NOT disable autonomy — the Sim keeps free will until a
    /// player command (a UserDirected interaction) enters its queue, at which point the mod's
    /// pump engages the gate (IncrementAutonomyDisabled); when the UserDirected queue drains, the
    /// mod disengages it (DecrementAutonomyDisabled). Supersedes's claim-time
    /// DISABLE_AUTONOMY. Wire: GATE_AUTONOMY|corrId|simObjectId.
    /// </summary>
    public sealed record GateAutonomy(Guid CorrelationId, ulong SimObjectId) : BridgeCommand(CorrelationId);

    /// <summary>
    /// clear the command-lifecycle autonomy gate for a Sim on release/leave/disconnect.
    /// If the gate is currently engaged (a command was mid-flight), the mod unwinds the one
    /// outstanding suppression so no autonomy disable is leaked past the claim. Reset=true asks the
    /// mod to force autonomy back on (-style bounded decrement-until-enabled) — the recovery
    /// path for a gate orphaned by a controller restart. Wire: UNGATE_AUTONOMY|corrId|simObjectId
    /// (3 parts) or UNGATE_AUTONOMY|corrId|simObjectId|reset (4 parts).
    /// </summary>
    public sealed record UngateAutonomy(Guid CorrelationId, ulong SimObjectId, bool Reset = false) : BridgeCommand(CorrelationId);

    /// <summary>
    /// W1 (internal documentation): the server TS3's simulation scope, i.e. the lots
    /// players occupy and the Sims they own. The forked gameplay assembly (SimScopeHook) simulates
    /// those lots like the active lot and runs those Sims at high-LOD autonomy; stock gameplay
    /// answers hook=absent. Replaces the whole scope. Wire: SET_SIM_SCOPE|corrId|lots|sims, each a
    /// SimScopeWire id list ("-" when empty).
    /// </summary>
    public sealed record SetSimScope(Guid CorrelationId, IReadOnlyList<ulong> LotIds, IReadOnlyList<ulong> SimObjectIds) : BridgeCommand(CorrelationId);

    /// <summary>
    /// W2 (internal documentation): the client authority gate on a client TS3 running the
    /// forked gameplay assembly (AuthorityHook). Mode off | observe | enforce; in enforce a Sim the
    /// client does not own only queues interactions a server command inserted. Wire:
    /// SET_AUTHORITY|corrId|mode|owned (owned = SimScopeWire id list, "-" when empty).
    /// </summary>
    public sealed record SetAuthority(Guid CorrelationId, AuthorityGateMode Mode, IReadOnlyList<ulong> OwnedSimObjectIds) : BridgeCommand(CorrelationId);

    /// <summary>W2 part B: read motives + buffs of up to SimStateWire.MaxSimsPerProbe Sims in one round
    /// trip. Wire: PROBE_SIM_STATE|corrId|ids. Reply: AUTONOMY_PROBE ok with SimStateWire entries.</summary>
    public sealed record ProbeSimState(Guid CorrelationId, IReadOnlyList<ulong> SimObjectIds) : BridgeCommand(CorrelationId);

    /// <summary>W2 part B (client role): set a mirrored Sim's motives and reconcile its buffs to the
    /// server's. Wire: APPLY_SIM_STATE|corrId|entry (one SimStateWire entry).</summary>
    public sealed record ApplySimState(Guid CorrelationId, SimStateSample State) : BridgeCommand(CorrelationId);

    /// <summary>W2 Sim identity (client role, internal documentation): tell the
    /// client TS3 which Sim each server ObjectId names, by SimDescriptionId, so commands reach its own
    /// instance of a Sim it instantiated with a different ObjectId. Reset clears the table first.
    /// Wire: SET_SIM_ALIASES|corrId|pairs[|reset] (SimAliasWire).</summary>
    public sealed record SetSimAliases(Guid CorrelationId, IReadOnlyList<SimAlias> Aliases, bool Reset = false) : BridgeCommand(CorrelationId);

    /// <summary>W2 Sim identity step 3 (server role): the presence journal entries after SinceSeq
    /// (Sims entering/leaving the world). Wire: PROBE_SIM_PRESENCE|corrId|sinceSeq; reply SimPresenceWire.</summary>
    public sealed record ProbeSimPresence(Guid CorrelationId, long SinceSeq) : BridgeCommand(CorrelationId);

    /// <summary>W2 Sim identity step 3 (client role): instantiate this client's copy of the server's Sim.
    /// Wire: SPAWN_SIM|corrId|serverObjectId|simDescriptionId|x|y|z.</summary>
    public sealed record SpawnSim(Guid CorrelationId, ulong SimObjectId, ulong SimDescriptionId, Vector3Dto Position) : BridgeCommand(CorrelationId);

    /// <summary>W2 Sim identity step 3 (client role): remove this client's copy of a Sim the server removed.
    /// Wire: DESPAWN_SIM|corrId|serverObjectId|simDescriptionId.</summary>
    public sealed record DespawnSim(Guid CorrelationId, ulong SimObjectId, ulong SimDescriptionId) : BridgeCommand(CorrelationId);

    /// <summary> (server role): export one Sim description to a host BULK slot.
    /// Wire: EXPORT_SIM_DESCRIPTION|corrId|simDescriptionId.</summary>
    public sealed record ExportSimDescription(Guid CorrelationId, ulong SimDescriptionId) : BridgeCommand(CorrelationId);

    /// <summary> step 5: job, household funds and skills of up to SimDetailsWire.MaxSimsPerProbe Sims.
    /// Wire: PROBE_SIM_DETAILS|corrId|id,id,...</summary>
    public sealed record ProbeSimDetails(Guid CorrelationId, IReadOnlyList<ulong> SimObjectIds) : BridgeCommand(CorrelationId);

    /// <summary> step 5 (client role): bring this client's copy of a Sim to the server's details.
    /// Wire: APPLY_SIM_DETAILS|corrId|entry.</summary>
    public sealed record ApplySimDetails(Guid CorrelationId, SimDetailsSample Details) : BridgeCommand(CorrelationId);

    /// <summary> (client role): import the server's description, keeping its id. The host uploads the
    /// bytes first (BULK_REF). Wire: IMPORT_SIM_DESCRIPTION|corrId|simDescriptionId|totalBytes|props|keys|objects|crcHex.</summary>
    public sealed record ImportSimDescription(Guid CorrelationId, SimDescriptionPayload Payload) : BridgeCommand(CorrelationId);

    /// <summary> presentation stream (server role): the animation-call recorder. Args are the
    /// verb's own fields (ring-on | dump sinceSeq [targets] | snapshot targets | snapshot-driver handle offset).
    /// Wire: SACS_RECORDER|corrId|args...</summary>
    public sealed record SacsRecorder(Guid CorrelationId, IReadOnlyList<string> Args) : BridgeCommand(CorrelationId);

    /// <summary> presentation stream (client role): replay recorded animation calls on this
    /// client's instances. Wire: SACS_REPLAY|corrId|hex(UTF-8 lines joined by '\n').</summary>
    public sealed record SacsReplay(Guid CorrelationId, IReadOnlyList<string> Lines) : BridgeCommand(CorrelationId);

    /// <summary>
    /// read-only mod-state report for controller re-attach. Returns DIAG_STATE_REPORT
    /// with role/inWorld/tick/autonomyWatch/pendingOutbound. Accepted in any role.
    /// </summary>
    public sealed record DiagState(Guid CorrelationId) : BridgeCommand(CorrelationId);

    public sealed record DiagCaptureInteraction(Guid CorrelationId, ulong SimObjectId) : BridgeCommand(CorrelationId);

    /// <summary>read posture/container/active interaction via DIAG_INTERACTION_STATE.</summary>
    public sealed record QueryInteractionState(Guid CorrelationId, ulong SimObjectId, ulong? WatchBookObjectId = null) : BridgeCommand(CorrelationId);

    /// <summary>
    /// Step 2: client-role ReadBookChooser or GetBook decision injection.
    /// Mode is <c>chooser</c> or <c>getbook</c> (wire hex-encoded).
    /// </summary>
    public sealed record InjectReadIntent(
        Guid CorrelationId,
        ulong SimObjectId,
        ulong BookObjectId,
        string Mode,
        IReadOnlyDictionary<string, string>? Parameters = null) : BridgeCommand(CorrelationId);

    /// <summary>
    /// liveness refutation. The mod re-arms its discovery beacon after ~10s without
    /// controller evidence; a live controller's reader answers the beacon with PING so the
    /// mod's queued outbound lines resume. Replies OK.
    /// </summary>
    public sealed record Ping(Guid CorrelationId) : BridgeCommand(CorrelationId);

    /// <summary>
    /// follow-up (2026-07-03): graceful controller shutdown. Sent fire-and-forget just
    /// before the controller exits — the mod resets its inbound dedupe cursor, drops queued
    /// outbound, and re-arms the discovery beacon immediately (no 10s evidence wait). No
    /// response is written or awaited. Older mod builds answer with "Unknown bridge command",
    /// which the exiting controller never reads — harmless, so no ProtocolVersion bump.
    /// </summary>
    public sealed record Disconnect(Guid CorrelationId) : BridgeCommand(CorrelationId);

    /// <summary>
    /// bridgectl: arbitrary wire command (NAME|corrId|args...). Dev-tooling escape
    /// hatch for the loopback admin listener — unknown names fail safely with the mod's
    /// "Unknown bridge command" ERROR. Callers must supply their own timeout.
    /// </summary>
    public sealed record Raw(Guid CorrelationId, string Name, string[] Args) : BridgeCommand(CorrelationId);

    /// <summary>
    /// M4 P4.1.0.3: controller-driven role switching on the single-mod architecture.
    /// Default role on script-DLL init is Server (preserves all existing behavior).
    /// In Client role the script DLL accepts state-write commands like `APPLY_POSITION`.
    /// See `internal documentation` P4.1.0.2 architectural recommendation.
    /// </summary>
    public sealed record SetRole(Guid CorrelationId, HostRole Role) : BridgeCommand(CorrelationId);

    /// <summary>
    /// M4 P4.1.0.3: client-role state write. Sets a Sim's world position
    /// using the same `Sim.SetPosition(Vector3)` primitive proven in the P2
    /// state-injection probe. Only valid when the script DLL is in Client role.
    /// </summary>
    public sealed record ApplyPosition(Guid CorrelationId, ulong SimObjectId, Vector3Dto TargetPosition) : BridgeCommand(CorrelationId);

    /// <summary>
    /// batched client-role position write. Carries N Sim positions in a single bridge
    /// frame so a ~71-Sim snapshot collapses from N serial <see cref="ApplyPosition"/> round-trips
    /// to one (or two) framed commands the mod applies in a single <c>BridgeAlarmTick</c>. The
    /// translator chunks the snapshot to the single-frame budget via
    /// <see cref="BridgeLineCodec.ChunkApplyPositions"/>; each chunk is one <see cref="ApplyPositions"/>.
    /// Wire: <c>APPLY_POSITIONS|&lt;corr&gt;|&lt;simId:x:y:z&gt;;&lt;simId:x:y:z&gt;;...</c>. Only valid
    /// in Client role. The single <see cref="ApplyPosition"/> command stays in use for non-batch paths.
    /// </summary>
    public sealed record ApplyPositions(Guid CorrelationId, IReadOnlyList<(ulong SimObjectId, Vector3Dto Position)> Positions) : BridgeCommand(CorrelationId);

    /// <summary>
    /// M4 P4.1.1: client-role state write for Sim motive values (Hunger, Bladder,
    /// Social, Fun, Energy, Hygiene, etc.). MotiveName is a Sims 3 CommodityKind
    /// enum value name (e.g. `"Hunger"`). Value range is typically -100..+100.
    /// Only accepted when sCurrentRole == "client".
    /// </summary>
    public sealed record ApplyMotive(Guid CorrelationId, ulong SimObjectId, string MotiveName, float Value) : BridgeCommand(CorrelationId);

    /// <summary>
    /// M4 P4.1.1: debug probe that reads a Sim's current motive value back from
    /// the script DLL. Used by --probe-motive-roundtrip to verify APPLY_MOTIVE.
    /// </summary>
    public sealed record ProbeSimMotive(Guid CorrelationId, ulong SimObjectId, string MotiveName) : BridgeCommand(CorrelationId);

    /// <summary>
    /// M4 P4.1.1.1: client-role state write that mirrors the existing public-protocol
    /// QueueInteraction handler. Same payload shape (actor + key + target object/position
    /// + parameters). The only difference: the script DLL gates this on sCurrentRole=="client"
    /// rather than the public protocol's ownership rule. Used for two-process Sim queue
    /// mirroring per internal documentation
    /// </summary>
    public sealed record ApplyInteractionQueue(Guid CorrelationId, InteractionRequest Interaction) : BridgeCommand(CorrelationId);

    /// <summary>
    /// batched client-role interaction replay. N replays in one bridge frame / one
    /// simulator tick, mirroring <see cref="ApplyPositions"/>. Wire:
    /// <c>APPLY_INTERACTIONS|&lt;corr&gt;|&lt;actor:hexKey:targetId:hasPos:x:y:z&gt;;...</c>
    /// </summary>
    public sealed record ApplyInteractions(Guid CorrelationId, IReadOnlyList<InteractionRequest> Interactions) : BridgeCommand(CorrelationId);

    /// <summary>
    /// client-role posture/container alignment from an authoritative
    /// <see cref="InteractionReplayBundle"/>. First family: Sit transition against
    /// <c>postureContainerObjectId</c> (PushAsContinuation on the queue head). Wire:
    /// <c>APPLY_FORCE_ALIGN|&lt;corr&gt;|&lt;actorId&gt;|&lt;hexKey&gt;|&lt;postureContainerId&gt;|&lt;isTransition:0|1&gt;</c>
    /// </summary>
    public sealed record ApplyForceAlign(Guid CorrelationId, InteractionReplayBundle Bundle) : BridgeCommand(CorrelationId);

    /// <summary>
    /// M4 P4.1.1.1: debug probe that reads a Sim's current interaction queue state
    /// back from the script DLL. Used by --probe-interaction-roundtrip to verify
    /// APPLY_INTERACTION_QUEUE landed (queueCount>=1 + current key matches expected).
    /// </summary>
    public sealed record ProbeSimInteractionQueue(Guid CorrelationId, ulong SimObjectId) : BridgeCommand(CorrelationId);

    /// <summary>
    /// M4 P4.1.1.2: client-role state write for household funds. Sets
    /// Household.FamilyFunds via the script DLL. Only accepted when sCurrentRole == "client".
    /// </summary>
    public sealed record ApplyCash(Guid CorrelationId, ulong HouseholdId, int Funds) : BridgeCommand(CorrelationId);

    /// <summary>
    /// M4 P4.1.1.2: debug probe that reads a household's current FamilyFunds
    /// value back. Used by --probe-cash-roundtrip to verify APPLY_CASH landed.
    /// </summary>
    public sealed record ProbeHouseholdCash(Guid CorrelationId, ulong HouseholdId) : BridgeCommand(CorrelationId);

    /// <summary>M4 P4.1.1.3: client-role state write for Sim skill level. Adds the skill via SkillManager.AddElement if absent then ForceSkillLevelUp loop to reach target level.</summary>
    public sealed record ApplySkill(Guid CorrelationId, ulong SimObjectId, string SkillName, int Level) : BridgeCommand(CorrelationId);

    /// <summary>M4 P4.1.1.3: debug probe that reads a Sim's current skill level + points back.</summary>
    public sealed record ProbeSimSkill(Guid CorrelationId, ulong SimObjectId, string SkillName) : BridgeCommand(CorrelationId);
    /// <summary>M4 P4.3: client-role state write for Sim autonomy (idempotent absolute-set semantics). Disabled=true wraps Sim.Autonomy.IncrementAutonomyDisabled when currently enabled; Disabled=false wraps DecrementAutonomyDisabled when currently disabled.</summary>
    public sealed record ApplyAutonomy(Guid CorrelationId, ulong SimObjectId, bool Disabled) : BridgeCommand(CorrelationId);

    /// <summary>M4 P4.3: read a Sim's current autonomy state. Returns SIM_AUTONOMY_PROBE with sim=&lt;id&gt;;enabled=&lt;bool&gt;;disabledCount=&lt;int&gt;.</summary>
    public sealed record ProbeSimAutonomy(Guid CorrelationId, ulong SimObjectId) : BridgeCommand(CorrelationId);

    /// <summary>
    /// slice 6: partial save reconciliation — apply a validated client-native owned-lot
    /// class A bundle into the SERVER TS3 before SAVE_GAME (docs/archive/dev-149-slice5-partial-
    /// save-reconciliation.md, merge mechanism "lot patch bridge command"). Server-role only: this is
    /// the reconcile transport INTO the authoritative TS3, not a mirror APPLY_* stream. Bundles
    /// exceeding the frame budget are chunked via <see cref="BridgeLineCodec.ChunkMergeLotState"/>.
    /// Wire: <c>MERGE_LOT_STATE|&lt;corr&gt;|&lt;lotId&gt;|&lt;simId:x:y:z&gt;;&lt;simId:x:y:z&gt;;...</c>
    /// </summary>
    public sealed record MergeLotState(Guid CorrelationId, LotExportBundle Bundle) : BridgeCommand(CorrelationId);

    /// <summary>
    /// diagnostic command for the now-quarantined live deep-state import path.
    /// household's <c>ExportContent</c> bytes into the live server household via
    /// <c>Household.ImportContent</c>, run <c>PostImport</c> fixups, and prepare for SAVE_GAME.
    /// The heavy payload travels via the BULK slot (uploaded before this control frame); this
    /// command carries only the header metadata so the mod can fetch the blob and run the import.
    /// Wire: <c>IMPORT_HOUSEHOLD_CONTENT|&lt;corr&gt;|&lt;householdId&gt;|&lt;propertyStreamBytes&gt;|&lt;resKeyTableBytes&gt;|&lt;objectIdTableBytes&gt;|&lt;crc32Hex&gt;|&lt;entryCount&gt;|&lt;resKeyCount&gt;|&lt;objectIdCount&gt;</c>
    /// Response carries OK/ERROR; the mod reports import success/failure + a summary of
    /// remapped SimDescriptionIds so the controller can verify the remap.
    /// </summary>
    public sealed record ImportHouseholdContent(
        Guid CorrelationId,
        ulong HouseholdId,
        int TotalBytes,
        int PropertyStreamBytes,
        int ResKeyTableBytes,
        int ObjectIdTableBytes,
        uint Crc32,
        int EntryCount,
        int ResKeyCount,
        int ObjectIdCount) : BridgeCommand(CorrelationId);

    /// <summary>
    /// EXPORT_HOUSEHOLD_CONTENT -- the mirror of IMPORT_HOUSEHOLD_CONTENT.
    /// Requests <c>Household.ExportContent</c> on the TS3 bridge to generate deep household state.
    /// The mod serializes the export bytes, writes them to the 512 KiB BULK slot, and responds
    /// via DIAG_BLOB_REPORT carrying the header metadata (slotIndex, totalBytes, crc32, table
    /// sizes). The controller reads the BULK slot and constructs a HouseholdExportPayload.
    /// Wire: EXPORT_HOUSEHOLD_CONTENT|corrId|householdId
    /// </summary>
    public sealed record ExportHouseholdContent(
        Guid CorrelationId,
        ulong HouseholdId) : BridgeCommand(CorrelationId);

    /// <summary>
    /// MERGE_TRAVELLING_HOUSEHOLD -- the reload-safe replacement for
    /// IMPORT_HOUSEHOLD_CONTENT. Imports into a FRESH temp household, then merges into
    /// existing identities via MergeTravelInformation + MergeTravelData. Never calls
    /// ImportContent on the existing live household.
    /// Wire: MERGE_TRAVELLING_HOUSEHOLD|corrId|householdId|totalBytes|crc32Hex|householdPropBytes|householdResKeyBytes|householdObjIdBytes|inventoryCount
    /// Response carries OK/ERROR with a merge report.
    /// The BULK blob carries: [householdPropStream][householdResKeyTable][householdObjIdTable][inv0PropStream][inv0ResKeyTable][inv0ObjIdTable]...
    /// </summary>
    public sealed record MergeTravellingHousehold(
        Guid CorrelationId,
        ulong HouseholdId,
        int TotalBytes,
        uint Crc32,
        int HouseholdPropBytes,
        int HouseholdResKeyBytes,
        int HouseholdObjIdBytes,
        int InventoryCount) : BridgeCommand(CorrelationId);

    /// <summary>
    /// Dev tooling: show an in-game StyledNotification toast in the target TS3 instance.
    /// Best-effort UI sugar (script DLL swallows toast failures); accepted in any role.
    /// Used to surface multiplayer state (claims, role, forwarding) to the player during manual tests.
    /// </summary>
    public sealed record ShowNotification(Guid CorrelationId, string Text) : BridgeCommand(CorrelationId);
}

/// <summary>M4 P4.1.0.3 host role for single-mod role switching.</summary>
public enum HostRole
{
    Server = 0,
    Client = 1
}

/// <summary>W2 Sim identity: a server Sim ObjectId and the SimDescriptionId of the Sim it names.</summary>
public readonly record struct SimAlias(ulong ServerObjectId, ulong SimDescriptionId);
