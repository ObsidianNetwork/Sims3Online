using System.Collections.Generic;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Mirroring;

/// <summary>
/// M4 P4.5 (DEV-N): Threshold-based drift detector for the client mirror loop.
/// Compares newly-received server Sim positions against the last-applied values.
/// A jump &gt; <see cref="DefaultThreshold"/> on a <em>contiguous</em> tick signals the client failed
/// to apply a delta locally. Sparse periodic sync (large tick gaps) is excluded — see the
/// tick-aware <see cref="HasDrift(IReadOnlyDictionary{ulong, Vector3Dto}, IReadOnlyList{SimSnapshot}, long, long, float)"/> overload.
/// </summary>
public static class DriftDetector
{
    public const float DefaultThreshold = 5.0f;

    /// <summary>
    /// Returns true if any Sim in <paramref name="incoming"/> whose ObjectId is
    /// already in <paramref name="tracked"/> has moved more than
    /// <paramref name="threshold"/> world units from its tracked position.
    /// Sims not yet in <paramref name="tracked"/> are skipped (no prior position).
    /// Assumes a contiguous tick step (unit tests / legacy callers).
    /// </summary>
    public static bool HasDrift(
        IReadOnlyDictionary<ulong, Vector3Dto> tracked,
        IReadOnlyList<SimSnapshot> incoming,
        float threshold = DefaultThreshold)
        => HasDrift(tracked, incoming, lastAppliedTick: -1, incomingTick: 0, threshold);

    /// <summary>
    /// P5.6 LP-4 / resync-storm fix: periodic <c>SnapshotDelta</c> broadcasts arrive with large
    /// simulation tick gaps (adaptive cadence). A Sim can legitimately move &gt; threshold between
    /// those sparse updates — that is the correction, not missed-client state. Only treat a position
    /// jump as drift when the incoming tick is contiguous with the last applied tick
    /// (<c>incomingTick &lt;= lastAppliedTick + 1</c>). When <paramref name="lastAppliedTick"/> is
    /// &lt; 0, the tick gate is skipped (legacy / test callers).
    /// </summary>
    public static bool HasDrift(
        IReadOnlyDictionary<ulong, Vector3Dto> tracked,
        IReadOnlyList<SimSnapshot> incoming,
        long lastAppliedTick,
        long incomingTick,
        float threshold = DefaultThreshold)
    {
        if (lastAppliedTick >= 0 && incomingTick > lastAppliedTick + 1)
        {
            return false;
        }

        return HasPositionDrift(tracked, incoming, threshold);
    }

    private static bool HasPositionDrift(
        IReadOnlyDictionary<ulong, Vector3Dto> tracked,
        IReadOnlyList<SimSnapshot> incoming,
        float threshold)
    {
        foreach (SimSnapshot sim in incoming)
        {
            if (!tracked.TryGetValue(sim.ObjectId, out Vector3Dto known)) continue;
            float dx = sim.Position.X - known.X;
            float dy = sim.Position.Y - known.Y;
            float dz = sim.Position.Z - known.Z;
            if (MathF.Sqrt(dx * dx + dy * dy + dz * dz) > threshold)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Updates <paramref name="tracked"/> with the positions from
    /// <paramref name="incoming"/>, overwriting any prior values.
    /// Call this AFTER <see cref="HasDrift"/> so the old values are
    /// still available for comparison.
    /// </summary>
    public static void UpdatePositions(
        Dictionary<ulong, Vector3Dto> tracked,
        IReadOnlyList<SimSnapshot> incoming)
    {
        foreach (SimSnapshot sim in incoming)
            tracked[sim.ObjectId] = sim.Position;
    }
}
