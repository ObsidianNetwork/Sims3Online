using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Authority;

public sealed record CommandResult(
    Guid CommandId,
    bool IsAccepted,
    string? RejectionReason,
    IReadOnlyList<ServerEvent> Events)
{
    public static CommandResult Accepted(Guid commandId, params ServerEvent[] events)
    {
        return new CommandResult(commandId, true, null, events);
    }

    public static CommandResult Rejected(Guid commandId, string reason)
    {
        return new CommandResult(commandId, false, reason, Array.Empty<ServerEvent>());
    }

    /// <summary>
    /// reject with events. Used by the SaveGame checkpoint path when some players are
    /// unreconciled but other players' inline merges already succeeded — the acks for the
    /// successful merges must still reach those clients so their s_lastReconciledVersion stays
    /// current, even though the save itself is rejected.
    /// </summary>
    public static CommandResult Rejected(Guid commandId, string reason, params ServerEvent[] events)
    {
        return new CommandResult(commandId, false, reason, events);
    }
}
