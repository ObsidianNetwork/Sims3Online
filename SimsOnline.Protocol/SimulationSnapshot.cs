namespace SimsOnline.DedicatedServer.Protocol;

public sealed record SimulationSnapshot(
    long Tick,
    string WorldName,
    string SaveName,
    GameSpeed Speed,
    IReadOnlyList<SimSnapshot> Sims,
    IReadOnlyList<ObjectSnapshot> Objects);

public sealed record SimSnapshot(
    ulong ObjectId,
    string Name,
    ulong HouseholdId,
    Vector3Dto Position,
    ulong? CurrentInteractionId,
    int? OwningPlayerId,
    string? CurrentInteractionKey = null,
    ulong? LotId = null,
    /// <summary>
    /// authoritative <c>InteractionQueue.Count</c> from the server host snapshot.
    /// Activity-gated interest scope treats <c>&gt; 0</c> as mirror-active for tier-2 townies
    /// (see <c>library/engine/dormancy-lod.md</c>).
    /// </summary>
    int? InteractionQueueCount = null,
    ulong? PostureContainerObjectId = null,
    string? PostureType = null,
    /// <summary>
    /// W2 Sim identity (<c>library/engine/sim-identity-and-npc-creation.md</c>): who the Sim is. Same on
    /// every TS3 for save-born descriptions, whereas <see cref="ObjectId"/> differs for Sims
    /// instantiated after load. Null from an older mod.
    /// </summary>
    ulong? SimDescriptionId = null);

public sealed record ObjectSnapshot(
    ulong ObjectId,
    string ScriptClass,
    Vector3Dto Position,
    ulong? LotId);

public readonly record struct Vector3Dto(float X, float Y, float Z);
