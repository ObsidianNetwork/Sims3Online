namespace SimsOnline.DedicatedServer.Mirroring;

/// <summary>
/// slice 2: per-player lot-local authority mode. Default
/// <see cref="FullPuppetFallback"/> until live spike proves safe cutover.
/// Card 57 ruling 4 moved this enum into SimsOnline.Protocol (it is carried on the wire by
/// <c>ServerEvent.LotLocalAuthorityModeChanged</c>); its namespace stays
/// <c>SimsOnline.DedicatedServer.Mirroring</c> so no consumer's usings changed.
/// </summary>
public enum LotLocalAuthorityMode
{
    /// <summary>Client mirrors owned Sims via APPLY_* (today's P5 puppet path).</summary>
    FullPuppetFallback = 0,

    /// <summary>Player alone on active lot — client TS3 runs native sim, zero mirror for owned Sims.</summary>
    SoloNative = 1,

    /// <summary>Proximity co-presence — bounded mirror for foreign player Sims; owned stay native.</summary>
    ProxMirror = 2,
}
