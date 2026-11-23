using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Mirroring;

/// <summary>
/// Which Sims' visible state (motives, buffs) the server mirrors to clients, and when. Clients
/// freeze the heartbeat of Sims they do not own, so the server sends their state instead. Candidates
/// are the unclaimed Sims on the lots
/// players occupy ( Tier-1 interest lots); claimed Sims keep the per-motive mirror.
/// A sample is sent when any motive moved more than <see cref="MotiveThreshold"/> or the buff set
/// (guid + origin) changed.
/// </summary>
public static class VisibleSimStatePolicy
{
    /// <summary>Probe every Nth broadcaster tick (~10 s at the 2 s cadence).</summary>
    public const int ProbeInterval = 5;

    /// <summary>Upper bound on Sims probed per cycle (the rest wait for the next one).</summary>
    public const int MaxSimsPerCycle = 48;

    public const float MotiveThreshold = 0.5f;

    public static IReadOnlyList<ulong> Candidates(IReadOnlyDictionary<ulong, int> claims, IReadOnlyList<SimSnapshot> sims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(sims);
        HashSet<ulong> lots = MirroringInterestScope.ComputeInterestLots(claims, sims);
        if (lots.Count == 0)
        {
            return Array.Empty<ulong>();
        }

        List<ulong> candidates = new();
        foreach (SimSnapshot sim in sims)
        {
            if (sim.LotId is ulong lotId && lots.Contains(lotId) && !claims.ContainsKey(sim.ObjectId))
            {
                candidates.Add(sim.ObjectId);
            }
        }

        candidates.Sort();
        return candidates.Count > MaxSimsPerCycle ? candidates.GetRange(0, MaxSimsPerCycle) : candidates;
    }

    public static bool HasChanged(SimStateSample next, SimStateSample? last)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (last is null || last.Motives.Count != next.Motives.Count)
        {
            return true;
        }

        for (int i = 0; i < next.Motives.Count; i++)
        {
            if (Math.Abs(next.Motives[i] - last.Motives[i]) > MotiveThreshold)
            {
                return true;
            }
        }

        if (last.Buffs.Count != next.Buffs.Count)
        {
            return true;
        }

        HashSet<(ulong, ulong)> lastBuffs = new(last.Buffs.Select(b => (b.Guid, b.Origin)));
        return next.Buffs.Any(b => !lastBuffs.Contains((b.Guid, b.Origin)));
    }
}
