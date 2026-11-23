using global::Sentry;

namespace SimsOnline.DedicatedServer.Observability;

/// <summary>
/// Per-operation traces sampling policy. Critical operations (session lifecycle, bridge connect,
/// process exit, claim, release) are sampled at 100%. Bridge pings/hellos are never sampled.
/// Mirror dispatch is lightly sampled (5%). Failures remain correlated with their error event,
/// but sampling cannot depend on an outcome that is not yet known when a transaction starts.
/// Everything else falls back to 10%.
/// </summary>
internal static class SentryTracesSampler
{
    /// <summary>Constant string keys kept here to avoid magic strings in tests.</summary>
    public const string OpSessionStart = "session.start";
    public const string OpSessionStop = "session.stop";
    public const string OpBridgeConnect = "bridge.connect";
    public const string OpProcessExit = "process.exit";
    public const string OpMirrorDispatch = "mirror.dispatch";
    public const string OpBridgePing = "bridge.ping";
    public const string OpBridgeHello = "bridge.hello";
    public const string OpBridgeLiveness = "bridge.liveness";
    public const string OpClaimPrefix = "claim.";
    public const string OpReleasePrefix = "release.";

    private const double Critical = 1.0;
    private const double Pings = 0.0;
    private const double MirrorNormal = 0.05;
    private const double Fallback = 0.1;

    public static double? Sample(TransactionSamplingContext context)
    {
        string? operation = context.TransactionContext?.Operation;
        if (string.IsNullOrEmpty(operation))
        {
            return Fallback;
        }

        if (IsCriticalOperation(operation))
        {
            return Critical;
        }

        if (IsPingOperation(operation))
        {
            return Pings;
        }

        if (operation == OpMirrorDispatch)
        {
            return MirrorNormal;
        }

        return Fallback;
    }

    internal static bool IsCriticalOperation(string operation) =>
        operation == OpSessionStart
        || operation == OpSessionStop
        || operation == OpBridgeConnect
        || operation == OpProcessExit
        || operation.StartsWith(OpClaimPrefix, StringComparison.Ordinal)
        || operation.StartsWith(OpReleasePrefix, StringComparison.Ordinal);

    internal static bool IsPingOperation(string operation) =>
        operation == OpBridgePing
        || operation == OpBridgeHello
        || operation == OpBridgeLiveness;
}
