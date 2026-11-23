using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Mirroring;

/// <summary>
/// W2 Sim identity step 3 (<c>library/engine/sim-identity-and-npc-creation.md</c>): which Sims the server
/// tells clients to put into their world. Clients only have the Sims that were in the world at load
/// (their spawn gate stops them inventing more), so a Sim the server instantiated after load is
/// announced (SimSpawned) once it stands on a lot a player occupies, and stays announced until it
/// leaves the server's world (SimDespawned). Announced Sims are resent every
/// <see cref="ResendInterval"/> calls so late joiners and failed spawns catch up (spawning is
/// idempotent on the client).
/// </summary>
public static class SimPresencePolicy
{
    /// <summary>Resend the announced set every Nth broadcaster tick (~30 s at the 2 s cadence).</summary>
    public const int ResendInterval = 15;

    /// <summary>Upper bound on presence journal round trips per tick.</summary>
    public const int MaxProbeRounds = 4;

    /// <summary>
    /// SimSpawned events for post-load Sims that should be announced now: unannounced ones standing on
    /// an interest lot, plus every announced one when <paramref name="resend"/>. Claimed Sims are
    /// never announced (their owner runs them). Lot and position come from the interest index when it
    /// has the Sim, else from the journal entry.
    /// </summary>
    public static IReadOnlyList<ServerEvent.SimSpawned> Announcements(
        IReadOnlyDictionary<ulong, int> claims,
        IReadOnlyList<SimSnapshot> indexSims,
        IEnumerable<SimPresenceEntry> postLoadSims,
        IReadOnlySet<ulong> announced,
        bool resend)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(indexSims);
        ArgumentNullException.ThrowIfNull(postLoadSims);
        ArgumentNullException.ThrowIfNull(announced);
        HashSet<ulong> interestLots = MirroringInterestScope.ComputeInterestLots(claims, indexSims);
        Dictionary<ulong, SimSnapshot> index = new();
        foreach (SimSnapshot sim in indexSims)
        {
            index[sim.ObjectId] = sim;
        }

        List<ServerEvent.SimSpawned> events = new();
        foreach (SimPresenceEntry sim in postLoadSims.OrderBy(s => s.ObjectId))
        {
            if (claims.ContainsKey(sim.ObjectId))
            {
                continue;
            }

            index.TryGetValue(sim.ObjectId, out SimSnapshot? current);
            ulong lotId = current?.LotId ?? sim.LotId;
            Vector3Dto position = current?.Position ?? sim.Position;
            bool wasAnnounced = announced.Contains(sim.ObjectId);
            if ((wasAnnounced && resend) || (!wasAnnounced && interestLots.Contains(lotId)))
            {
                events.Add(new ServerEvent.SimSpawned(sim.ObjectId, sim.SimDescriptionId, position));
            }
        }

        return events;
    }

    /// <summary>
    /// Whether a Sim that left the server's world is announced as SimDespawned: clients can only hold
    /// Sims that were there at load (not post-load) or that were announced, and never despawn a
    /// claimed Sim.
    /// </summary>
    public static bool ShouldAnnounceDespawn(ulong objectId, IReadOnlyDictionary<ulong, int> claims, bool wasPostLoad, bool wasAnnounced) =>
        !claims.ContainsKey(objectId) && (!wasPostLoad || wasAnnounced);
}
