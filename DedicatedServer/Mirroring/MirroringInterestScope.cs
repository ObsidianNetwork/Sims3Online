using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Mirroring;

/// <summary>
/// Phase 3: lot-tiered interest scope for mirror traffic. Tier 1 =
/// lots where ANY joined player has a claimed Sim. Unclaimed Sims on those lots are included only
/// when <see cref="IsTier2MirrorActive"/> (non-empty interaction queue / active
/// interaction). Off-lot Sims/objects are omitted from snapshot deltas and interaction broadcasts.
/// When no claims exist yet, filtering is disabled (full-world bootstrap).
/// </summary>
public static class MirroringInterestScope
{
    /// <summary>
    /// Tier-2 (on interest lot, unclaimed) Sims mirror only while
    /// <c>InteractionQueue.Count &gt; 0</c> — the same signal as the dormancy probe
    /// (<c>docs/engine/dormancy-lod.md</c>, <c>docs/archive/p5-sync-architecture.md</c> section C).
    /// Uses wire <see cref="SimSnapshot.InteractionQueueCount"/> when present; legacy 10-field
    /// snapshot items fall back to <see cref="SimSnapshot.CurrentInteractionId"/>.
    /// </summary>
    public static bool IsTier2MirrorActive(SimSnapshot sim)
    {
        if (sim.InteractionQueueCount is int count)
        {
            return count > 0;
        }

        return sim.CurrentInteractionId is not null;
    }

    public static HashSet<ulong> ComputeInterestLots(
        IReadOnlyDictionary<ulong, int> claims,
        IReadOnlyList<SimSnapshot> sims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(sims);

        if (claims.Count == 0)
        {
            return new HashSet<ulong>();
        }

        Dictionary<ulong, ulong> simLotById = BuildSimLotIndex(sims);
        HashSet<ulong> lots = new();
        foreach (ulong simObjectId in claims.Keys)
        {
            if (simLotById.TryGetValue(simObjectId, out ulong mappedLot) && mappedLot != 0)
            {
                lots.Add(mappedLot);
            }
        }

        return lots;
    }

    public static bool ShouldFilter(IReadOnlyDictionary<ulong, int> claims) =>
        claims.Count > 0;

    public static SimulationSnapshot FilterSnapshot(
        SimulationSnapshot snapshot,
        HashSet<ulong> interestLots,
        IReadOnlySet<ulong> claimedSimIds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(interestLots);
        ArgumentNullException.ThrowIfNull(claimedSimIds);

        if (interestLots.Count == 0)
        {
            return snapshot;
        }

        List<SimSnapshot> sims = new(snapshot.Sims.Count);
        foreach (SimSnapshot sim in snapshot.Sims)
        {
            if (claimedSimIds.Contains(sim.ObjectId))
            {
                sims.Add(sim);
                continue;
            }

            if (sim.LotId is ulong lotId && interestLots.Contains(lotId) && IsTier2MirrorActive(sim))
            {
                sims.Add(sim);
            }
        }

        List<ObjectSnapshot> objects = new(snapshot.Objects.Count);
        foreach (ObjectSnapshot obj in snapshot.Objects)
        {
            if (obj.LotId is ulong lotId && interestLots.Contains(lotId))
            {
                objects.Add(obj);
            }
        }

        return snapshot with { Sims = sims, Objects = objects };
    }

    public static bool ShouldMirrorInteractionQueued(
        ServerEvent.InteractionQueued interaction,
        HashSet<ulong> interestLots,
        IReadOnlySet<ulong> claimedSimIds,
        IReadOnlyDictionary<ulong, ulong> simLotById)
    {
        if (claimedSimIds.Contains(interaction.ActorObjectId))
        {
            return true;
        }

        if (interestLots.Count == 0)
        {
            return true;
        }

        if (simLotById.TryGetValue(interaction.ActorObjectId, out ulong lotId) && lotId != 0)
        {
            return interestLots.Contains(lotId);
        }

        return false;
    }

    public static Dictionary<ulong, ulong> BuildSimLotIndex(IReadOnlyList<SimSnapshot> sims)
    {
        Dictionary<ulong, ulong> index = new(sims.Count);
        foreach (SimSnapshot sim in sims)
        {
            if (sim.LotId is ulong lotId && lotId != 0)
            {
                index[sim.ObjectId] = lotId;
            }
        }

        return index;
    }

    public static HashSet<ulong> ClaimedSimIds(IReadOnlyDictionary<ulong, int> claims)
    {
        HashSet<ulong> ids = new(claims.Count);
        foreach (ulong simObjectId in claims.Keys)
        {
            ids.Add(simObjectId);
        }

        return ids;
    }
}