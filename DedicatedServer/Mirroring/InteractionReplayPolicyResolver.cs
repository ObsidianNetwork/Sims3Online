using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Mirroring;

public sealed record InteractionReplayDecision(ReplayPolicy Policy, string Reason);

public static class InteractionReplayPolicyResolver
{
    public static InteractionReplayDecision Resolve(InteractionReplayBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        if (SimsOnlineServerHost.TerminalInteractionFilter.IsNeverReplay(bundle.InteractionKey))
        {
            return Decision(ReplayPolicy.NeverReplay, "interaction is marked never-replay");
        }

        if (bundle.PolicyHint is ReplayPolicy policyHint)
        {
            return Decision(policyHint, "authoritative policy hint");
        }

        if (IsPostureAlignmentCandidate(bundle))
        {
            return Decision(ReplayPolicy.ForceAlign, "transition carries authoritative posture container");
        }

        if (SimsOnlineServerHost.TerminalInteractionFilter.IsRouteReplay(bundle.InteractionKey) && HasReplayTarget(bundle))
        {
            return Decision(ReplayPolicy.ReplayAsRoute, "routing interaction with target");
        }

        if (SimsOnlineServerHost.TerminalInteractionFilter.IsTerminalReplay(bundle.InteractionKey) && HasReplayTarget(bundle))
        {
            return Decision(ReplayPolicy.ReplayExact, "terminal interaction with target");
        }

        if (HasReplayTarget(bundle))
        {
            return Decision(ReplayPolicy.ReplayAsRoute, "unknown interaction with target");
        }

        return Decision(ReplayPolicy.SlideOnly, "missing replay target");
    }

    private static bool HasReplayTarget(InteractionReplayBundle bundle) =>
        bundle.TargetPosition != null || bundle.TargetObjectId is ulong targetObjectId && targetObjectId != 0;

    private static bool IsPostureAlignmentCandidate(InteractionReplayBundle bundle)
    {
        if (bundle.PostureContainerObjectId is not ulong postureContainerObjectId || postureContainerObjectId == 0)
        {
            return false;
        }

        return bundle.IsTransitionInteraction
            || bundle.QueueRole == InteractionQueueRole.Transition
            || IsSitOrStand(bundle.InteractionKey);
    }

    private static bool IsSitOrStand(string interactionKey)
    {
        if (string.IsNullOrWhiteSpace(interactionKey))
        {
            return false;
        }

        return interactionKey.Contains("InteractionsShared.Sit", StringComparison.Ordinal)
            || interactionKey.Contains("InteractionsShared.Stand", StringComparison.Ordinal);
    }

    private static InteractionReplayDecision Decision(ReplayPolicy policy, string reason) => new(policy, reason);
}