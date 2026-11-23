using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Mirroring;

/// <summary>
/// M4 P4.2.5 - per-client self-id filter for the mirror loop.
///
/// The dedicated server broadcasts <see cref="ServerEvent"/>s to ALL connected clients.
/// For state-write events targeting a specific Sim (currently <see cref="ServerEvent.SimAutonomyChanged"/>
/// and <see cref="ServerEvent.InteractionQueued"/>), the OWNING client should NOT mirror
/// the event to its local TS3:
///
/// * <see cref="ServerEvent.SimAutonomyChanged"/> with <c>disabled=true</c> on a self-owned Sim
///   is NOW mirrored ( Phase 1B): the host is the sole autonomy authority; the client
///   puppet suppresses free local AI on owned Sims too.
/// * <see cref="ServerEvent.InteractionQueued"/> targeting a self-owned actor would re-queue an
///   interaction the owning client just sent (echo of their own command).
///
/// REMOTE clients (those who do NOT own the target Sim) DO mirror these events - that's the whole
/// point of the mirror loop: keep remote puppets' state aligned with the server's authoritative state.
///
/// Pure-function helper. No I/O, no state. Consumed by SimsOnline.Client's TryMirrorDispatchAsync
/// before <see cref="MirrorEngine.DispatchAsync(ServerEvent, System.Threading.CancellationToken)"/>.
///
/// Out-of-scope filter targets (intentional):
/// * <see cref="ServerEvent.FullSnapshot"/> / <see cref="ServerEvent.SnapshotDelta"/> - may contain
///   BOTH self-owned and remote-owned Sims; whole-event skip would incorrectly suppress remote
///   positions. Per-Sim filtering inside snapshots is a future slice.
/// * <see cref="ServerEvent.InteractionStarted"/> / <see cref="ServerEvent.InteractionCompleted"/> /
///   <see cref="ServerEvent.InteractionFailed"/> - status updates, not state writes; the translator
///   returns empty <see cref="HostBridge.BridgeCommand"/> arrays anyway.
/// </summary>
public static class MirrorEventFilter
{
    public sealed record SkipDecision(bool Skip, string Reason);

    private static readonly SkipDecision DoNotSkip = new(false, "");

    // shared empty strip set returned by ComputePositionMirrorStripSet on the common
    // no-overlap path so FilterSnapshotSims short-circuits (count==0) with no allocation.
    private static readonly IReadOnlySet<ulong> EmptyStripSet = new HashSet<ulong>();

    public static SkipDecision ShouldSkip(ServerEvent serverEvent, int selfPlayerId, IReadOnlySet<ulong> ownedSims)
    {
        ArgumentNullException.ThrowIfNull(serverEvent);
        ArgumentNullException.ThrowIfNull(ownedSims);

        return serverEvent switch
        {
            ServerEvent.InteractionQueued iq when ownedSims.Contains(iq.ActorObjectId) =>
                new SkipDecision(true, $"self-owned-actor sim={iq.ActorObjectId} player={selfPlayerId}"),

            _ => DoNotSkip
        };
    }

    /// <summary>
    /// compute the set of owned Sims to STRIP from the position-mirror (ApplyPositions +
    /// drift detection) for one snapshot. A Sim is stripped exactly when it is BOTH owned AND has an
    /// in-flight player-directed order — letting its local <c>Terrain.GoHere</c> route play out and
    /// animate instead of being teleported out from under itself by the per-2s position-mirror.
    ///
    /// This is the gate that fixes the freeze without re-enabling free local AI: autonomy
    /// stays suppressed; only the position-mirror is paused, and only for the directed Sim,
    /// and only while its order is in flight. An idle owned Sim (not in <paramref name="activeOrders"/>)
    /// keeps rendering the host position, preserving idle-Sim convergence. Remote-owned Sims
    /// are never in either input set, so they are never stripped.
    ///
    /// Returns an empty set when there is no overlap (the common case), so the caller hands
    /// <see cref="FilterSnapshotSims"/> an empty strip set and pays no allocation in
    /// <see cref="TryFilterSnapshot"/> (which short-circuits on count==0).
    /// </summary>
    public static IReadOnlySet<ulong> ComputePositionMirrorStripSet(
        IReadOnlySet<ulong> ownedSims,
        IReadOnlySet<ulong> activeOrders)
    {
        ArgumentNullException.ThrowIfNull(ownedSims);
        ArgumentNullException.ThrowIfNull(activeOrders);

        if (ownedSims.Count == 0 || activeOrders.Count == 0)
        {
            return EmptyStripSet;
        }

        // Intersect the smaller set against the larger for fewer probes.
        IReadOnlySet<ulong> smaller = ownedSims.Count <= activeOrders.Count ? ownedSims : activeOrders;
        IReadOnlySet<ulong> larger = ReferenceEquals(smaller, ownedSims) ? activeOrders : ownedSims;

        HashSet<ulong>? strip = null;
        foreach (ulong id in smaller)
        {
            if (larger.Contains(id))
            {
                (strip ??= new HashSet<ulong>()).Add(id);
            }
        }

        return strip ?? EmptyStripSet;
    }

    /// <summary>
    /// M4 P4.2.8: per-Sim filter for snapshot events. Strips self-owned Sims from
    /// <see cref="ServerEvent.FullSnapshot.Snapshot.Sims"/> / <see cref="ServerEvent.SnapshotDelta.Snapshot.Sims"/>
    /// while preserving remote-owned positions. P4.2.5 <see cref="ShouldSkip"/> intentionally left
    /// snapshot events alone because whole-event skip would erroneously drop remote-owned positions
    /// when a snapshot mixes self- and remote-owned Sims. This method does per-Sim filtering instead.
    ///
    /// Returns the SAME event instance when no filtering is needed (common case: zero self-owned Sims
    /// in the snapshot OR a non-snapshot event) to avoid the allocation cost of constructing a new
    /// record + snapshot when the result is identical. Callers can use reference equality to detect
    /// "no-change" cheaply.
    ///
    /// Non-snapshot events pass through unchanged. Object/lot enumerations are not filtered (P4.2.5
    /// ownership semantics are per-Sim, not per-object).
    /// </summary>
    public static ServerEvent FilterSnapshotSims(ServerEvent serverEvent, IReadOnlySet<ulong> ownedSims)
    {
        ArgumentNullException.ThrowIfNull(serverEvent);
        ArgumentNullException.ThrowIfNull(ownedSims);

        switch (serverEvent)
        {
            case ServerEvent.FullSnapshot full:
                return TryFilterSnapshot(full.Snapshot, ownedSims, out SimulationSnapshot? filteredFull)
                    ? new ServerEvent.FullSnapshot(filteredFull!)
                    : serverEvent;

            case ServerEvent.SnapshotDelta delta:
                return TryFilterSnapshot(delta.Snapshot, ownedSims, out SimulationSnapshot? filteredDelta)
                    ? new ServerEvent.SnapshotDelta(filteredDelta!)
                    : serverEvent;

            default:
                return serverEvent;
        }
    }

    private static bool TryFilterSnapshot(SimulationSnapshot snapshot, IReadOnlySet<ulong> ownedSims, out SimulationSnapshot? filtered)
    {
        if (ownedSims.Count == 0 || snapshot.Sims.Count == 0)
        {
            filtered = null;
            return false;
        }

        bool anyOwned = false;
        for (int i = 0; i < snapshot.Sims.Count; i++)
        {
            if (ownedSims.Contains(snapshot.Sims[i].ObjectId))
            {
                anyOwned = true;
                break;
            }
        }

        if (!anyOwned)
        {
            filtered = null;
            return false;
        }

        List<SimSnapshot> kept = new(snapshot.Sims.Count);
        foreach (SimSnapshot sim in snapshot.Sims)
        {
            if (!ownedSims.Contains(sim.ObjectId))
            {
                kept.Add(sim);
            }
        }

        filtered = snapshot with { Sims = kept };
        return true;
    }
}