using System.Globalization;
using SimsOnline.DedicatedServer.Mirroring;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Observability;

public static class SimsOnlineTelemetryMetadata
{
    public static IReadOnlyDictionary<string, string?> ForMirrorEvent(
        ServerEvent serverEvent,
        bool claimedActor)
    {
        Dictionary<string, string?> attributes = new(StringComparer.Ordinal)
        {
            ["command_type"] = serverEvent.GetType().Name,
            ["validation_gate"] = "mirror-dispatch",
        };

        switch (serverEvent)
        {
            case ServerEvent.InteractionQueued queued:
                AddInteraction(
                    attributes,
                    queued.ActorObjectId,
                    queued.InteractionKey,
                    queued.TargetObjectId,
                    claimedActor);
                break;
            case ServerEvent.InteractionReplayQueued replay:
                AddInteraction(
                    attributes,
                    replay.Bundle.ActorObjectId,
                    replay.Bundle.InteractionKey,
                    replay.Bundle.TargetObjectId,
                    claimedActor);
                attributes["replay_policy"] =
                    InteractionReplayPolicyResolver.Resolve(replay.Bundle).Policy.ToString();
                break;
        }

        return attributes;
    }

    public static bool IsTargetResolutionFailure(
        ServerEvent serverEvent,
        IEnumerable<Exception> failures)
    {
        bool hasTarget = serverEvent switch
        {
            ServerEvent.InteractionQueued queued => queued.TargetObjectId is > 0,
            ServerEvent.InteractionReplayQueued replay => replay.Bundle.TargetObjectId is > 0,
            _ => false,
        };
        return hasTarget && failures.Any(failure =>
            failure.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || failure.Message.Contains("target", StringComparison.OrdinalIgnoreCase));
    }

    private static void AddInteraction(
        IDictionary<string, string?> attributes,
        ulong actorObjectId,
        string interactionKey,
        ulong? targetObjectId,
        bool claimedActor)
    {
        if (claimedActor)
        {
            attributes["claimed_sim_id"] = actorObjectId.ToString(CultureInfo.InvariantCulture);
        }

        attributes["interaction_key"] = interactionKey;
        if (targetObjectId is > 0)
        {
            attributes["target_object_id"] = targetObjectId.Value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
