using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.HostBridge;

public sealed record BridgeResponse(
    Guid CorrelationId,
    bool IsAccepted,
    string? RejectionReason,
    SimulationSnapshot? Snapshot,
    string? ProbeReport = null)
{
    public static BridgeResponse Accepted(Guid correlationId, SimulationSnapshot? snapshot = null)
    {
        return new BridgeResponse(correlationId, true, null, snapshot);
    }

    public static BridgeResponse Rejected(Guid correlationId, string reason)
    {
        return new BridgeResponse(correlationId, false, reason, null);
    }

    /// <summary>Attach a probe report (e.g. autonomy probe strategy outcome) to an accepted response.</summary>
    public BridgeResponse WithProbeReport(string report) =>
        this with { ProbeReport = report };
}