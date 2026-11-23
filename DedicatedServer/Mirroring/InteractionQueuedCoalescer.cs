using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Mirroring;

/// <summary>
/// fold redundant <see cref="ServerEvent.InteractionQueued"/> bursts before batch
/// bridge dispatch. Route-replay churn for one actor collapses to the latest; distinct keys
/// (e.g. Sit then ReadBook) stay in arrival order.
/// </summary>
public static class InteractionQueuedCoalescer
{
    /// <summary>
    /// Ordered coalesce: latest route-replay wins per actor; terminal/chain keys are preserved.
    /// </summary>
    public static IReadOnlyList<ServerEvent.InteractionQueued> CoalesceForBatch(
        IEnumerable<ServerEvent.InteractionQueued> interactions)
    {
        ArgumentNullException.ThrowIfNull(interactions);

        List<ServerEvent.InteractionQueued> result = new();
        foreach (ServerEvent.InteractionQueued iq in interactions)
        {
            for (int i = result.Count - 1; i >= 0; i--)
            {
                ServerEvent.InteractionQueued prior = result[i];
                if (prior.ActorObjectId != iq.ActorObjectId)
                {
                    continue;
                }

                if (ShouldReplacePrior(prior, iq))
                {
                    result.RemoveAt(i);
                }
            }

            if (IsDuplicateTail(result, iq))
            {
                continue;
            }

            result.Add(iq);
        }

        return StripRedundantBookPrecursors(StripRedundantSocialRoutes(result));
    }

    /// <summary>
    /// When SocializeWith is in the batch, drop SocialInteractionA/B route churn for that actor —
    /// the terminal approach already runs FindBestSocial on the client.
    /// </summary>
    private static List<ServerEvent.InteractionQueued> StripRedundantSocialRoutes(
        List<ServerEvent.InteractionQueued> result)
    {
        HashSet<ulong> actorsWithSocializeWith = new();
        foreach (ServerEvent.InteractionQueued iq in result)
        {
            if (iq.InteractionKey.StartsWith("Sims3.Gameplay.Actors.Sim+SocializeWith+"))
            {
                actorsWithSocializeWith.Add(iq.ActorObjectId);
            }
        }

        if (actorsWithSocializeWith.Count == 0)
        {
            return result;
        }

        List<ServerEvent.InteractionQueued> filtered = new(result.Count);
        foreach (ServerEvent.InteractionQueued iq in result)
        {
            if (actorsWithSocializeWith.Contains(iq.ActorObjectId)
                && (iq.InteractionKey.StartsWith("Sims3.Gameplay.Socializing.SocialInteractionA+")
                    || iq.InteractionKey.StartsWith("Sims3.Gameplay.Socializing.SocialInteractionB+")))
            {
                continue;
            }

            filtered.Add(iq);
        }

        return filtered;
    }

    /// <summary>
    /// When Sit+ReadBook are batched together, drop ReadBookChooser/GetBook precursors for that actor.
    /// </summary>
    private static IReadOnlyList<ServerEvent.InteractionQueued> StripRedundantBookPrecursors(
        List<ServerEvent.InteractionQueued> result)
    {
        HashSet<ulong> actorsWithReadBook = new();
        foreach (ServerEvent.InteractionQueued iq in result)
        {
            if (SimsOnlineServerHost.InteractionReplayApplySorter.IsReadBookTerminalHead(iq.InteractionKey))
            {
                actorsWithReadBook.Add(iq.ActorObjectId);
            }
        }

        if (actorsWithReadBook.Count == 0)
        {
            return result;
        }

        List<ServerEvent.InteractionQueued> filtered = new(result.Count);
        foreach (ServerEvent.InteractionQueued iq in result)
        {
            if (actorsWithReadBook.Contains(iq.ActorObjectId)
                && iq.InteractionKey.StartsWith("Sims3.Gameplay.Objects.ReadBookChooser+"))
            {
                continue;
            }

            filtered.Add(iq);
        }

        return filtered;
    }

    /// <summary>Backward-compatible name used by tests.</summary>
    public static IReadOnlyList<ServerEvent.InteractionQueued> CoalesceLatestPerActor(
        IEnumerable<ServerEvent.InteractionQueued> interactions)
        => CoalesceForBatch(interactions);

    private static bool IsDuplicateTail(List<ServerEvent.InteractionQueued> result, ServerEvent.InteractionQueued iq)
    {
        if (result.Count == 0)
        {
            return false;
        }

        ServerEvent.InteractionQueued last = result[result.Count - 1];
        return last.ActorObjectId == iq.ActorObjectId
            && last.InteractionKey == iq.InteractionKey
            && last.InteractionId == iq.InteractionId;
    }

    private static bool ShouldReplacePrior(ServerEvent.InteractionQueued prior, ServerEvent.InteractionQueued next)
    {
        if (prior.ActorObjectId != next.ActorObjectId)
        {
            return false;
        }

        if (prior.InteractionKey == next.InteractionKey)
        {
            return next.InteractionId >= prior.InteractionId;
        }

        return SimsOnlineServerHost.TerminalInteractionFilter.IsRouteReplay(prior.InteractionKey)
            && SimsOnlineServerHost.TerminalInteractionFilter.IsRouteReplay(next.InteractionKey);
    }
}
