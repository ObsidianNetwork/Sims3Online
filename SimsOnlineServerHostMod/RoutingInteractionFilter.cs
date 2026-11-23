// C#2 ONLY. This file is compiled two ways and must stay valid under BOTH:
//   1. The Sims 3 script mod, by csc.exe 2.0 via build-server-host-mod.ps1 (non-recursive
//      *.cs glob over this directory -- keep this file in the mod ROOT, never a subfolder).
//   2. The .NET 8 test project (DedicatedServer.Tests), source-linked via <Compile Include="..">
//      so the routing-classifier tests exercise this exact decision logic.
// Therefore: NO var, NO LINQ, NO lambdas, NO async, NO C#3+ syntax, NO engine types -- only
// plain System types + a static method. A C#3+ construct accidentally added here compiles green
// in the test project but silently breaks the csc-2.0 mod build.
//
// NOTE: deliberately NO `#nullable` directive -- that pragma is C# 8 and csc 2.0 rejects it
// (CS1024). The method tolerates a null key (treats it as "not a routing interaction").
namespace SimsOnlineServerHost
{
    /// <summary>
    /// the PURE routing-interaction allowlist for the autonomy-watch emit
    /// (<c>PollAutonomyWatchQueues</c> in the mod shell) and the client replay gate
    /// (<c>HandleApplyInteractionQueue</c>). Free of engine types so it is unit-testable in
    /// DedicatedServer.Tests, modelled on the <see cref="ClientCaptureFilter"/>.
    ///
    /// WHY: the autonomy-watch emit (<c>BuildInteractionQueuedLineForInstance</c>) puts the
    /// interaction target on the wire for ROUTING interactions (this allowlist) AND for any
    /// interaction that carries a <c>Target</c> object (step B, follow-up). The translator
    /// gates on <c>TargetPosition != null</c> OR <c>TargetObjectId != 0</c>, so both classes are
    /// replayable on the client. This predicate owns the routing-only subset used by
    /// <c>UnclaimedRoutingEmitFilter</c> (the unclaimed-townie emit gate) and the client
    /// <c>TryApplyInteractionFields</c> route arm. Generic re-queue of arbitrary interaction types
    /// (Eat/ReadBook/cook) is the multi-team-month interaction-registry problem and is OUT OF SCOPE
    /// for the routing allowlist -- but those interactions ARE replayable via the walk-to-target arm
    /// when they carry a target object, not via this routing classification.
    ///
    /// THE ALLOWLIST (fail-closed): the mod sends the interaction-definition's
    /// <c>GetType().FullName</c> on the wire (e.g. <c>Sims3.Gameplay.Core.GoHome+Definition</c>, the
    /// nested-Definition reflected form). This predicate matches only the four routing interactions
    /// seen live on/near a visible lot, by namespace-qualified prefix so it CANNOT collide with the
    /// like-named vehicle/situation classes (e.g. <c>...Objects.Vehicles.GoHome</c>,
    /// <c>...Situations.GoHome</c>). Any unknown key fails closed (returns false), exactly as the
    /// capture-forward allowlist does for engine-pushed UserDirected interactions.
    /// </summary>
    public static class RoutingInteractionFilter
    {
        // The four routing interactions in scope, matched by the namespace-qualified type-name
        // prefix of their definition FullName. The mod emits the nested Definition's FullName
        // (e.g. "Sims3.Gameplay.Core.GoHome+Definition"); a prefix that stops before the '+'
        // matches every nested definition variant (GoHome+Definition, LeaderGoHereWith+Definition
        // AND +MovingDefinition) while excluding the unrelated same-leaf-name classes in other
        // namespaces. Keep this list and the client GoHere replay arm in sync: when a routing key
        // is added here it becomes replayable as a GoHere on the client.
        private static readonly string[] RoutingDefinitionPrefixes = new string[]
        {
            "Sims3.Gameplay.Core.GoHome",                          // townie / Sim heads home
            "Sims3.Gameplay.Core.VisitCommunityLot",               // townie visits a community lot
            "Sims3.Gameplay.Objects.Vehicles.FollowerMeetUpAtPoint", // group-route follower leg
            "Sims3.Gameplay.Objects.Vehicles.LeaderGoHereWith",      // group-route leader leg
            // (added 2026-06-15 after the first live walk): the active Sim's INTRA-LOT
            // autonomous walk is a standalone Terrain.GoHere (Definition + SameLotDefinition). The
            // directed GoHere already replays as a walk live; classifying the AUTONOMOUS one here
            // attaches its destination on emit so the puppet walks it too. Safe vs the directed path:
            // PollClaimedSimInteractionEmit skips UserDirected heads (directed orders flow via the
            // session echo), and the client GoHere replay arm already matches "Terrain+GoHere".
            "Sims3.Gameplay.Core.Terrain+GoHere"                     // intra-lot autonomous walk
        };

        /// <summary>
        /// True iff <paramref name="interactionKey"/> (the interaction definition's
        /// <c>GetType().FullName</c> as the mod puts it on the wire) is one of the routing
        /// interactions this slice replays as a GoHere-to-destination. Null/empty -> false.
        ///
        /// Matching: the key must equal a routing prefix exactly OR continue with a type-boundary
        /// character ('+' for the reflected nested-Definition form, '.' for a friendly form). This
        /// avoids a bare <c>Contains</c> false-matching a longer unrelated type that merely embeds
        /// the prefix as a substring.
        /// </summary>
        public static bool IsRoutingInteraction(string interactionKey)
        {
            if (interactionKey == null || interactionKey.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < RoutingDefinitionPrefixes.Length; i++)
            {
                string prefix = RoutingDefinitionPrefixes[i];
                if (interactionKey == prefix)
                {
                    return true;
                }

                if (interactionKey.Length > prefix.Length && interactionKey.StartsWith(prefix))
                {
                    char boundary = interactionKey[prefix.Length];
                    if (boundary == '+' || boundary == '.')
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
