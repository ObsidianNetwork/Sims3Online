// C#2 ONLY -- dual-compiled into DedicatedServer.Tests (same pattern as RoutingInteractionFilter).
namespace SimsOnlineServerHost
{
    /// <summary>
    /// seat binding: Sit often runs as InteractionQueue.TransitionInteraction (not the
    /// queue head CurrentInteraction reports), so PollClaimedSimInteractionEmit must watch it.
    /// </summary>
    public static class SitTransitionEmitFilter
    {
        public static bool IsSitTransitionReplay(string interactionKey)
        {
            if (interactionKey == null || interactionKey.Length == 0)
            {
                return false;
            }

            return interactionKey.StartsWith("Sims3.Gameplay.InteractionsShared.Sit+");
        }

        /// <summary>Latest transition Sit id wins per actor; 0 means never emitted.</summary>
        public static bool ShouldEmitTransitionSit(ulong lastEmittedTransitionSitId, ulong transitionSitId)
        {
            if (transitionSitId == 0uL)
            {
                return false;
            }

            return transitionSitId != lastEmittedTransitionSitId;
        }
    }
}