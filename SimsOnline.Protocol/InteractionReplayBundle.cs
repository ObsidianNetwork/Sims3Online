namespace SimsOnline.DedicatedServer.Protocol;

public sealed record InteractionReplayBundle(
    int Version,
    ulong ActorObjectId,
    ulong InteractionId,
    string InteractionKey,
    string? InteractionInstanceType,
    string? InteractionDefinitionType,
    ulong? TargetObjectId,
    string? TargetObjectType,
    Vector3Dto? TargetPosition,
    string? PriorityLevel,
    bool? IsUserDirected,
    bool? IsAutonomous,
    bool IsTransitionInteraction,
    InteractionQueueRole QueueRole,
    string? PostureType,
    ulong? PostureContainerObjectId,
    long? HostTick,
    IReadOnlyDictionary<string, string> Parameters,
    ReplayPolicy? PolicyHint)
{
    public const int CurrentVersion = 1;
}

public enum ReplayPolicy
{
    NeverReplay = 0,
    ReplayExact = 1,
    ReplayAsRoute = 2,
    ForceAlign = 3,
    SlideOnly = 4
}

public enum InteractionQueueRole
{
    Unknown = 0,
    Head = 1,
    Transition = 2,
    Continuation = 3,
    Current = 4,
    Running = 5
}