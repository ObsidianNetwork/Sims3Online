using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Mirroring;

/// <summary>
/// W1 (docs/server-authoritative-plan.md): what the server TS3 must simulate at full fidelity.
/// Lots = every lot a claimed Sim stands on (the Tier-1 interest lots); Sims = every
/// claimed Sim, so a player's Sim keeps high-LOD autonomy on the street or in transit. Both lists
/// are sorted so equal scopes compare equal regardless of claim order.
/// </summary>
public sealed record SimulationScope(IReadOnlyList<ulong> LotIds, IReadOnlyList<ulong> SimObjectIds)
{
    public static SimulationScope Empty { get; } = new(Array.Empty<ulong>(), Array.Empty<ulong>());

    public bool SameAs(SimulationScope? other) =>
        other is not null
        && LotIds.SequenceEqual(other.LotIds)
        && SimObjectIds.SequenceEqual(other.SimObjectIds);

    public override string ToString() =>
        $"lots=[{string.Join(",", LotIds)}] sims=[{string.Join(",", SimObjectIds)}]";
}

public static class SimulationScopePolicy
{
    /// <summary>A rejected scope is not resent before this; an older mod without SET_SIM_SCOPE
    /// would otherwise be asked on every broadcaster tick.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

    public static SimulationScope Compute(IReadOnlyDictionary<ulong, int> claims, IReadOnlyList<SimSnapshot> sims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(sims);

        List<ulong> lots = MirroringInterestScope.ComputeInterestLots(claims, sims).ToList();
        lots.Sort();
        List<ulong> claimed = claims.Keys.ToList();
        claimed.Sort();
        return new SimulationScope(lots, claimed);
    }

    /// <summary>
    /// Send when the scope differs from the last one the host accepted (null = never sent, so the
    /// first tick always publishes, which also clears a scope left by a previous controller), unless
    /// this exact scope failed less than <paramref name="retryAfter"/> ago.
    /// </summary>
    public static bool ShouldPublish(
        SimulationScope next,
        SimulationScope? published,
        SimulationScope? failed,
        DateTimeOffset failedUtc,
        DateTimeOffset now,
        TimeSpan retryAfter)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (next.SameAs(published))
        {
            return false;
        }

        return !(next.SameAs(failed) && now - failedUtc < retryAfter);
    }
}
