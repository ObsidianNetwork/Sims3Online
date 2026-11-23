using SimsOnline.DedicatedServer.Mirroring;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.Client;

/// <summary>
/// close-out from the Card 45 E4 re-sample (2026-10-30): the active-order strip
/// window keeps an owned puppet off the position mirror while its directed route animates
/// locally. If the server's LAST positional delta for that Sim arrived during the strip
/// window and the Sim then went idle, the delta stream never mentions it again (deltas
/// carry CHANGED Sims only), so the puppet keeps standing ~1 walk step short of the
/// server's final position with an out-of-date heading (the stale Iliana pos/facing rows).
///
/// On order completion — exactly when the strip window closes — one final authoritative
/// snapshot for just that Sim is re-dispatched through the normal mirror path: the
/// position lands via the same batched APPLY_POSITIONS the mirror already uses (no new
/// wire format, no new state). Facing has no mirror channel by design (walk animation
/// drives it on the puppet); the engine's walk to the re-asserted position is not
/// fabricated here — an invented puppet interaction would recreate the E4 class.
///
/// Pure function, unit-tested in DedicatedServer.Tests (linked via its csproj).
/// </summary>
public static class OrderCompletionReassert
{
    /// <summary>
    /// The single-Sim snapshot to re-assert for <paramref name="actorObjectId"/>, or null
    /// when the actor is not in the latest server truth (nothing authoritative to apply).
    /// Everything else about the snapshot (tick, world, save, speed) is carried through so
    /// the engine's speed/drift bookkeeping sees a coherent delta.
    /// </summary>
    public static SimulationSnapshot? BuildFinal(SimulationSnapshot? latest, ulong actorObjectId)
    {
        if (latest is null)
        {
            return null;
        }

        for (int i = 0; i < latest.Sims.Count; i++)
        {
            if (latest.Sims[i].ObjectId == actorObjectId)
            {
                return latest with
                {
                    Sims = new[] { latest.Sims[i] },
                    Objects = Array.Empty<ObjectSnapshot>(),
                };
            }
        }

        return null;
    }

    /// <summary>
    /// The whole order-completion decision the client's receive loop makes in one call, so the
    /// wired path and the tested path are the same code (Wave 10 left this as a caveat: the strip
    /// window closing was unit-tested on <see cref="BuildFinal"/> alone, never end to end).
    ///
    /// <para>Returns false — and applies nothing — unless <paramref name="activeOrders"/> really had
    /// an in-flight order for this actor: an order the client never tracked never stripped anything,
    /// so re-asserting would fight the mirror it is supposed to hand back. On true, the strip window
    /// is closed (the tracker entry is gone) and <paramref name="final"/> is the actor's one-Sim
    /// snapshot from the latest cached server truth.</para>
    /// </summary>
    public static bool TryBuildOnOrderCompleted(
        ActiveOrderTracker activeOrders,
        SimulationSnapshot? latest,
        ulong actorObjectId,
        out SimulationSnapshot? final)
    {
        ArgumentNullException.ThrowIfNull(activeOrders);
        final = null;
        if (!activeOrders.Clear(actorObjectId))
        {
            return false;
        }

        final = BuildFinal(latest, actorObjectId);
        return final is not null;
    }
}
