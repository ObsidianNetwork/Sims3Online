using System.Text.Json.Serialization;

namespace SimsOnline.DedicatedServer.Protocol;

// P3 (M3 Two-Process Coordination) public protocol: ClientCommand subtypes are encoded
// as newline-delimited JSON over the public TCP listener using these polymorphic
// discriminator names. Keep names stable across versions or document the wire bump.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(JoinSession), "join_session")]
[JsonDerivedType(typeof(LeaveSession), "leave_session")]
[JsonDerivedType(typeof(ClaimSim), "claim_sim")]
[JsonDerivedType(typeof(ReleaseSim), "release_sim")]
[JsonDerivedType(typeof(SetActiveSim), "set_active_sim")]
[JsonDerivedType(typeof(QueueInteraction), "queue_interaction")]
[JsonDerivedType(typeof(CancelInteraction), "cancel_interaction")]
[JsonDerivedType(typeof(LeaveConversation), "leave_conversation")]
[JsonDerivedType(typeof(AnswerDialog), "answer_dialog")]
[JsonDerivedType(typeof(RequestSimDescription), "request_sim_description")]
[JsonDerivedType(typeof(RequestWorld), "request_world")]
[JsonDerivedType(typeof(SetGameSpeed), "set_game_speed")]
[JsonDerivedType(typeof(SetWorldTime), "set_world_time")]
[JsonDerivedType(typeof(RequestSnapshot), "request_snapshot")]
[JsonDerivedType(typeof(SetClientRole), "set_client_role")]
[JsonDerivedType(typeof(SaveGame), "save_game")]
[JsonDerivedType(typeof(AckSnapshot), "ack_snapshot")]
[JsonDerivedType(typeof(SubmitLotExport), "submit_lot_export")]
public abstract record ClientCommand(Guid CommandId, int PlayerId)
{
    public string? SentryTrace { get; set; }

    public string? SentryBaggage { get; set; }

    /// <summary>OnScreen is the joining client's game's on-screen walk (OnScreenWalk: on, off or unknown), or
    /// null from a client that does not report one. The server warns when it differs from its own game's.</summary>
    public sealed record JoinSession(Guid CommandId, int PlayerId, string DisplayName, string? OnScreen = null)
        : ClientCommand(CommandId, PlayerId);

    public sealed record LeaveSession(Guid CommandId, int PlayerId)
        : ClientCommand(CommandId, PlayerId);

    public sealed record ClaimSim(Guid CommandId, int PlayerId, ulong SimObjectId)
        : ClientCommand(CommandId, PlayerId);

    public sealed record ReleaseSim(Guid CommandId, int PlayerId, ulong SimObjectId)
        : ClientCommand(CommandId, PlayerId);

    public sealed record SetActiveSim(Guid CommandId, int PlayerId, ulong SimObjectId)
        : ClientCommand(CommandId, PlayerId);

    public sealed record QueueInteraction(Guid CommandId, int PlayerId, InteractionRequest Interaction)
        : ClientCommand(CommandId, PlayerId);

    public sealed record CancelInteraction(Guid CommandId, int PlayerId, ulong ActorObjectId, ulong InteractionId)
        : ClientCommand(CommandId, PlayerId);

    /// <summary> n14: the player left their Sim's mirrored conversation from the HUD. Only the Sim's owner may
    /// send it; the server runs the engine's own conversation cancel for that Sim (server ids).</summary>
    public sealed record LeaveConversation(Guid CommandId, int PlayerId, ulong ActorObjectId)
        : ClientCommand(CommandId, PlayerId);

    /// <summary>the player's answer to a remote dialog of their Sim (null = none: the default).</summary>
    public sealed record AnswerDialog(Guid CommandId, int PlayerId, int RequestId, ulong SimObjectId, string? Answer)
        : ClientCommand(CommandId, PlayerId);

    /// <summary>the client lacks this Sim description (SPAWN_SIM reported missing-description). The
    /// server replies to this player only with SimDescriptionData.</summary>
    public sealed record RequestSimDescription(Guid CommandId, int PlayerId, ulong SimDescriptionId)
        : ClientCommand(CommandId, PlayerId);

    /// <summary>A joining client asks for the server's world: the server checkpoints its save and replies to that
    /// connection only with WorldFileChunk events, which the client writes into its Saves folder and loads.
    /// Sent on its own connection before the client's TS3 loads anything (no JoinSession needed).</summary>
    public sealed record RequestWorld(Guid CommandId, int PlayerId)
        : ClientCommand(CommandId, PlayerId);

    public sealed record SetGameSpeed(Guid CommandId, int PlayerId, GameSpeed Speed)
        : ClientCommand(CommandId, PlayerId);

    /// <summary>
    /// (P5-ARCH B): absolute clock authority. Sets the in-game world clock
    /// (day-of-week + hours + minutes) on the authoritative TS3 via the SET_WORLD_TIME
    /// bridge command. Server-role only (the authoritative server is the sole clock authority).
    /// On accept, the session broadcasts <see cref="ServerEvent.WorldTimeChanged"/> to all
    /// clients so mirror TS3 instances apply the same absolute time via SET_WORLD_TIME mirror.
    /// Join-time replay: <see cref="AuthoritativeSession"/> appends WorldTimeChanged to the
    /// JoinSession result events (mirrors the speed-replay pattern) so a late joiner
    /// inherits the exact server clock instead of booting hours behind.
    /// </summary>
    public sealed record SetWorldTime(Guid CommandId, int PlayerId, WorldTime Time)
        : ClientCommand(CommandId, PlayerId);

    public sealed record RequestSnapshot(Guid CommandId, int PlayerId, long? SinceTick)
        : ClientCommand(CommandId, PlayerId);

    /// <summary>
    /// M4 P4.1.2: public-protocol per-player role declaration. A connecting
    /// client tells the dedicated server which role it intends to play:
    /// <see cref="PlayerRole.Server"/> = this client is the "server" player (their inputs
    /// drive authoritative state on the dedicated server's TS3); <see cref="PlayerRole.Client"/>
    /// = this client mirrors authoritative state pushed from the server (their local TS3
    /// receives APPLY_* events). Default if never sent = <see cref="PlayerRole.Server"/>.
    /// Requires JoinSession first; rejected otherwise.
    /// </summary>
    public sealed record SetClientRole(Guid CommandId, int PlayerId, PlayerRole Role)
        : ClientCommand(CommandId, PlayerId);

    /// <summary>checkpoint the current in-world session to disk via the bridge SAVE_GAME path.</summary>
    public sealed record SaveGame(Guid CommandId, int PlayerId, string SaveName)
        : ClientCommand(CommandId, PlayerId);

    /// <summary>
    /// P5.4: client confirms it applied a snapshot/delta through the mirror loop.
    /// Carries the snapshot's simulation tick (monotonic per player; stale acks are ignored).
    /// </summary>
    public sealed record AckSnapshot(Guid CommandId, int PlayerId, long Tick)
        : ClientCommand(CommandId, PlayerId);

    /// <summary>
    /// slice 6: client-native lot reconcile upload. The client controller exports its
    /// owned active-lot class A state from its local TS3 and submits it here; the session
    /// validates ownership/lot scoping and merges it into the server TS3 via MERGE_LOT_STATE.
    /// Sent in response to <see cref="ServerEvent.LotExportRequested"/> and best-effort before
    /// a graceful <see cref="LeaveSession"/> while in a client-native authority mode.
    /// slice 5 (design §3.1): <paramref name="ClientLastReconciledVersion"/> is
    /// the last <see cref="ServerEvent.LotReconcileAcknowledged"/> version the client received;
    /// the controller uses it to detect version mismatch (client missed a server merge) so the
    /// conflict detector can classify contestable fields. 0 on the first export (no prior ack).
    /// </summary>
    public sealed record SubmitLotExport(Guid CommandId, int PlayerId, LotExportBundle Bundle, long ClientLastReconciledVersion = 0L)
        : ClientCommand(CommandId, PlayerId);
}

/// <summary>
/// M4 P4.1.2: the public-protocol per-player role enum. Conceptually parallel
/// to (but distinct from) <c>HostBridge.HostRole</c>, which controls which mode the
/// dedicated server's LOCAL TS3 script DLL is in (always Server). PlayerRole controls
/// per-connected-client behavior. The two enums share the same value names for symmetry.
/// </summary>
public enum PlayerRole
{
    Server,
    Client
}

/// <summary>
/// input forwarding: InteractionRequest parameters that make a QueueInteraction a player's pick
/// (QUEUE_INTERACTION's user-pick fields) instead of the GoHere-only command.
/// </summary>
public static class UserPick
{
    public const string Name = "ui.name";
    public const string Path = "ui.path";
    public const string Hit = "ui.hit";
    public const string Picked = "ui.picked";

    public static bool IsUserPick(InteractionRequest request) =>
        request.Parameters is { } parameters && parameters.ContainsKey(Name);
}

public sealed record InteractionRequest(
    ulong ActorObjectId,
    string InteractionKey,
    ulong? TargetObjectId,
    Vector3Dto? TargetPosition,
    IReadOnlyDictionary<string, string> Parameters);

public enum GameSpeed
{
    Pause,
    Normal,
    Speed2,
    Speed3,
    Ultra
}

/// <summary>
/// (P5-ARCH B): absolute in-game world time. Day is the Sims 3 <c>DaysOfTheWeek</c>
/// enum value (0=Sunday..6=Saturday). Hours is 0..23, Minutes is 0..59. The script-DLL handler
/// calls <c>SimClockUtils.SetStartTime(day, hours + minutes/60f)</c> (verified by probe).
/// </summary>
public readonly record struct WorldTime(int Day, int Hours, int Minutes)
{
    /// <summary>Hours + fractional minutes as the float hours24 SimClockUtils expects.</summary>
    public float Hours24 => Hours + (Minutes / 60f);
}
