namespace SimsOnline.DedicatedServer.Authority;

/// <summary>
/// P5.6: pure, stateless resync-policy decision function for
/// <see cref="SyncBroadcaster"/>. Mirrors the <c>ShouldPublishDelta</c> /
/// <c>ComputeAckLag</c> pattern: all branching is in one pure method so the
/// decision table is unit-tested without any I/O or async state.
///
/// Decision table (from design §3):
///   lag &lt;= WarnTicks              → PublishDelta at base interval
///   WarnTicks &lt; lag &lt;= HardTicks  → SlowAndPublish (stretched interval)
///   lag &gt; HardTicks &amp;&amp; free        → ForceFull
///   lag &gt; HardTicks &amp;&amp; in-flight   → Suppress
///   minAckValid == false           → PublishDelta (never ForceFull without valid baseline)
/// </summary>
public static class ResyncPolicy
{
    /// <summary>
    /// Evaluate the current lag and resync-in-flight state and return what the broadcaster
    /// should do this tick.
    /// </summary>
    /// <param name="broadcastTick">The tick the snapshot was taken at.</param>
    /// <param name="minAckTick">This player's last-acked tick (per-player, not global min).</param>
    /// <param name="minAckValid">True when this player has ever acked (TryGetPlayerAckTick returned true).</param>
    /// <param name="resyncInFlight">True when a ForceFull has been sent but not yet acked.</param>
    /// <param name="baseInterval">The configured base broadcast interval (ms).</param>
    /// <param name="thresholds">Tunable threshold knobs.</param>
    /// <returns>What action to take and what interval + fromTick to use next.</returns>
    public static ResyncDecision Decide(
        long broadcastTick,
        long minAckTick,
        bool minAckValid,
        bool resyncInFlight,
        double baseIntervalMs,
        ResyncThresholds thresholds)
    {
        // No valid ack baseline: publish normally, never force-full (would send at tick=0).
        if (!minAckValid)
        {
            return new ResyncDecision(ResyncAction.PublishDelta, baseIntervalMs, broadcastTick);
        }

        long lag = SyncBroadcaster.ComputeAckLag(broadcastTick, minAckTick);

        if (lag > thresholds.HardTicks)
        {
            if (resyncInFlight)
            {
                // Already have one ForceFull in flight; suppress until ack clears the latch.
                return new ResyncDecision(ResyncAction.Suppress, baseIntervalMs, broadcastTick);
            }

            // Lag past M — trigger a forced full resync.
            return new ResyncDecision(ResyncAction.ForceFull, baseIntervalMs, broadcastTick);
        }

        if (lag > thresholds.WarnTicks)
        {
            // Warning band: stretch the interval proportionally to lag, clamped at MaxIntervalMs.
            // Linear interpolation: at lag=Warn interval=base, at lag=Hard interval=Max.
            double t = (double)(lag - thresholds.WarnTicks) / (thresholds.HardTicks - thresholds.WarnTicks);
            double stretched = baseIntervalMs + t * (thresholds.MaxIntervalMs - baseIntervalMs);
            double clamped = Math.Min(stretched, thresholds.MaxIntervalMs);
            return new ResyncDecision(ResyncAction.SlowAndPublish, clamped, broadcastTick);
        }

        // Steady state.
        return new ResyncDecision(ResyncAction.PublishDelta, baseIntervalMs, broadcastTick);
    }
}

/// <summary>P5.6: the action the broadcaster should take this tick.</summary>
public enum ResyncAction
{
    /// <summary>Broadcast a normal SnapshotDelta at the base interval.</summary>
    PublishDelta,

    /// <summary>Broadcast a SnapshotDelta at the stretched interval (warning band).</summary>
    SlowAndPublish,

    /// <summary>Trigger a ForceFull resync: send full snapshot as SnapshotDelta{ForceFull=true}.</summary>
    ForceFull,

    /// <summary>A ForceFull is already in flight; suppress this tick's broadcast.</summary>
    Suppress,
}

/// <summary>P5.6: returned by <see cref="ResyncPolicy.Decide"/>.</summary>
public sealed record ResyncDecision(
    ResyncAction Action,
    double NextIntervalMs,
    long ResyncFromTick);

/// <summary>P5.6: tunable thresholds passed to <see cref="ResyncPolicy.Decide"/>.</summary>
public sealed record ResyncThresholds(
    long WarnTicks,
    long HardTicks,
    double MaxIntervalMs)
{
    /// <summary>
    /// Defaults calibrated to the P5.5 finding (lag=6901 at 2s cadence):
    /// Warn=1500 (~3s of ticks), Hard=6000 (~12s), MaxInterval=10s.
    /// These are runtime-tuneable via <see cref="SessionConfig"/> / CLI.
    /// </summary>
    public static readonly ResyncThresholds Default = new ResyncThresholds(
        WarnTicks: 1500,
        HardTicks: 6000,
        MaxIntervalMs: 10_000);
}
