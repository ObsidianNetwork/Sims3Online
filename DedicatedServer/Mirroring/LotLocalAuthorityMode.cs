using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Mirroring;

// The LotLocalAuthorityMode enum moved to SimsOnline.Protocol/LotLocalAuthorityMode.cs
// (card 57 ruling 4: ServerEvent.LotLocalAuthorityModeChanged carries it on the wire);
// its namespace is unchanged, so every consumer still sees SimsOnline.DedicatedServer.Mirroring.

/// <summary>
/// PR#15: client-side policy for when local mode derivation is allowed.
/// After the server pushes <c>lot_local_authority_mode_changed</c>, local owned-sims-only
/// detection must not run — it cannot see foreign claims and would clobber prox_mirror.
/// </summary>
public static class LotLocalAuthorityClientPolicy
{
    public static bool ShouldDeriveClientModeLocally(bool serverAuthoritativeModeReceived) =>
        !serverAuthoritativeModeReceived;
}

/// <summary>Product constants for co-presence detection and hysteresis.</summary>
public static class LotLocalAuthorityPolicy
{
    /// <summary>World-space radius for prox_mirror co-presence.</summary>
    public const float ProximitySimulationRadius = 50f;

    /// <summary>Stable co-presence required before solo_native → prox_mirror.</summary>
    public static readonly TimeSpan EnterHold = TimeSpan.FromSeconds(2);

    /// <summary>Stable absence required before prox_mirror → solo_native.</summary>
    public static readonly TimeSpan ExitHold = TimeSpan.FromSeconds(5);
}

/// <summary>Inputs for <see cref="LotLocalAuthorityDetector.DetectRaw"/>.</summary>
public readonly record struct LotLocalAuthorityContext(
    int JoinedPlayerCount,
    int PlayerId,
    IReadOnlyDictionary<ulong, int> Claims,
    IReadOnlyList<SimSnapshot> Sims,
    float ProximityRadius = LotLocalAuthorityPolicy.ProximitySimulationRadius);

/// <summary>
/// Pure detection for lot-local authority, including
/// <see cref="LotLocalAuthorityMode.ProxMirror"/> for co-presence on the active lot.
/// </summary>
public static class LotLocalAuthorityDetector
{
    /// <summary>Instantaneous mode from current session signals (no hysteresis).</summary>
    public static LotLocalAuthorityMode DetectRaw(in LotLocalAuthorityContext context) =>
        Detect(in context);

    public static LotLocalAuthorityMode Detect(in LotLocalAuthorityContext context)
    {
        if (context.JoinedPlayerCount < 1)
        {
            return LotLocalAuthorityMode.FullPuppetFallback;
        }

        if (!TryGetPlayerClaimedSim(context.PlayerId, context.Claims, out ulong claimedSimId))
        {
            return LotLocalAuthorityMode.FullPuppetFallback;
        }

        if (!TryGetSimLot(claimedSimId, context.Sims, out ulong activeLotId))
        {
            return LotLocalAuthorityMode.FullPuppetFallback;
        }

        if (HasForeignPlayerClaimOnLot(context.PlayerId, activeLotId, context.Claims, context.Sims))
        {
            if (context.JoinedPlayerCount >= 2
                && AreForeignClaimsInProximity(
                    context.PlayerId,
                    claimedSimId,
                    activeLotId,
                    context.Claims,
                    context.Sims,
                    context.ProximityRadius))
            {
                return LotLocalAuthorityMode.ProxMirror;
            }

            return LotLocalAuthorityMode.FullPuppetFallback;
        }

        if (context.JoinedPlayerCount == 1)
        {
            return LotLocalAuthorityMode.SoloNative;
        }

        return LotLocalAuthorityMode.FullPuppetFallback;
    }

    private static bool TryGetPlayerClaimedSim(
        int playerId,
        IReadOnlyDictionary<ulong, int> claims,
        out ulong claimedSimId)
    {
        foreach (KeyValuePair<ulong, int> entry in claims)
        {
            if (entry.Value == playerId)
            {
                claimedSimId = entry.Key;
                return true;
            }
        }

        claimedSimId = 0;
        return false;
    }

    private static bool TryGetSimLot(ulong simObjectId, IReadOnlyList<SimSnapshot> sims, out ulong lotId)
    {
        for (int i = 0; i < sims.Count; i++)
        {
            if (sims[i].ObjectId == simObjectId && sims[i].LotId is ulong id)
            {
                lotId = id;
                return true;
            }
        }

        lotId = 0;
        return false;
    }

    private static bool TryGetSimPosition(ulong simObjectId, IReadOnlyList<SimSnapshot> sims, out Vector3Dto position)
    {
        for (int i = 0; i < sims.Count; i++)
        {
            if (sims[i].ObjectId == simObjectId)
            {
                position = sims[i].Position;
                return true;
            }
        }

        position = default;
        return false;
    }

    private static bool HasForeignPlayerClaimOnLot(
        int playerId,
        ulong lotId,
        IReadOnlyDictionary<ulong, int> claims,
        IReadOnlyList<SimSnapshot> sims)
    {
        foreach (KeyValuePair<ulong, int> entry in claims)
        {
            if (entry.Value == playerId)
            {
                continue;
            }

            if (TryGetSimLot(entry.Key, sims, out ulong claimLot) && claimLot == lotId)
            {
                return true;
            }
        }

        return false;
    }

    private static bool AreForeignClaimsInProximity(
        int playerId,
        ulong claimedSimId,
        ulong activeLotId,
        IReadOnlyDictionary<ulong, int> claims,
        IReadOnlyList<SimSnapshot> sims,
        float radius)
    {
        if (!TryGetSimPosition(claimedSimId, sims, out Vector3Dto ownPosition))
        {
            return true;
        }

        float radiusSq = radius * radius;
        foreach (KeyValuePair<ulong, int> entry in claims)
        {
            if (entry.Value == playerId)
            {
                continue;
            }

            if (!TryGetSimLot(entry.Key, sims, out ulong claimLot) || claimLot != activeLotId)
            {
                continue;
            }

            if (!TryGetSimPosition(entry.Key, sims, out Vector3Dto foreignPosition))
            {
                return true;
            }

            if (DistanceSquared(ownPosition, foreignPosition) <= radiusSq)
            {
                return true;
            }
        }

        return false;
    }

    private static float DistanceSquared(Vector3Dto a, Vector3Dto b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }
}

/// <summary>
/// Anti-flap hysteresis for solo_native ↔ prox_mirror only.
/// </summary>
public sealed class LotLocalAuthorityHysteresis
{
    private LotLocalAuthorityMode _stable = LotLocalAuthorityMode.FullPuppetFallback;
    private LotLocalAuthorityMode? _pending;
    private DateTimeOffset _pendingSince = DateTimeOffset.MinValue;

    public LotLocalAuthorityMode Update(
        LotLocalAuthorityMode raw,
        DateTimeOffset now,
        TimeSpan enterHold,
        TimeSpan exitHold)
    {
        if (raw == _stable)
        {
            _pending = null;
            return _stable;
        }

        if (!IsSoloProxTransition(_stable, raw))
        {
            _stable = raw;
            _pending = null;
            return _stable;
        }

        if (_pending != raw)
        {
            _pending = raw;
            _pendingSince = now;
        }

        TimeSpan hold = raw == LotLocalAuthorityMode.ProxMirror ? enterHold : exitHold;
        if (now - _pendingSince >= hold)
        {
            _stable = raw;
            _pending = null;
        }

        return _stable;
    }

    private static bool IsSoloProxTransition(LotLocalAuthorityMode from, LotLocalAuthorityMode to) =>
        (from == LotLocalAuthorityMode.SoloNative && to == LotLocalAuthorityMode.ProxMirror)
        || (from == LotLocalAuthorityMode.ProxMirror && to == LotLocalAuthorityMode.SoloNative);
}

/// <summary>
/// mirror suppress helpers for controller broadcast and client mirror loop.
/// </summary>
public static class LotLocalAuthorityMirrorGate
{
    public static bool OwnsNativeSimulation(LotLocalAuthorityMode mode) =>
        mode == LotLocalAuthorityMode.SoloNative || mode == LotLocalAuthorityMode.ProxMirror;

    public static bool ShouldProbeServerDerivedSimState(LotLocalAuthorityMode ownerMode) =>
        !OwnsNativeSimulation(ownerMode);

    public static bool ShouldStripOwnedSimsFromMirrorScope(LotLocalAuthorityMode mode) =>
        OwnsNativeSimulation(mode);

    public static HashSet<ulong> OwnedSimIdsForPlayer(int playerId, IReadOnlyDictionary<ulong, int> claims)
    {
        HashSet<ulong> owned = new();
        foreach (KeyValuePair<ulong, int> entry in claims)
        {
            if (entry.Value == playerId)
            {
                owned.Add(entry.Key);
            }
        }

        return owned;
    }

    public static SimulationSnapshot StripOwnedSimsFromSnapshot(
        SimulationSnapshot snapshot,
        IReadOnlySet<ulong> ownedSimIds)
    {
        if (ownedSimIds.Count == 0 || snapshot.Sims.Count == 0)
        {
            return snapshot;
        }

        List<SimSnapshot> kept = new(snapshot.Sims.Count);
        for (int i = 0; i < snapshot.Sims.Count; i++)
        {
            SimSnapshot sim = snapshot.Sims[i];
            if (!ownedSimIds.Contains(sim.ObjectId))
            {
                kept.Add(sim);
            }
        }

        return snapshot with { Sims = kept };
    }

    public static bool ShouldSuppressOwnedMirror(
        LotLocalAuthorityMode mode,
        IReadOnlySet<ulong> ownedSimIds,
        ulong actorObjectId)
    {
        return OwnsNativeSimulation(mode) && ownedSimIds.Contains(actorObjectId);
    }

    /// <summary>
    /// client path: when solo_native or prox_mirror, strip ALL owned Sims from position mirror
    /// (supersedes active-order-only strip).
    /// </summary>
    public static IReadOnlySet<ulong> ComputePositionMirrorStripSet(
        LotLocalAuthorityMode mode,
        IReadOnlySet<ulong> ownedSims,
        IReadOnlySet<ulong> activeOrders)
    {
        if (OwnsNativeSimulation(mode) && ownedSims.Count > 0)
        {
            return ownedSims;
        }

        return MirrorEventFilter.ComputePositionMirrorStripSet(ownedSims, activeOrders);
    }
}
