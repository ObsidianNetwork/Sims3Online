// C#2 ONLY. This file is compiled two ways and must stay valid under BOTH:
//   1. The Sims 3 script mod, by csc.exe 2.0 via build-server-host-mod.ps1 (non-recursive
//      *.cs glob over this directory -- keep this file in the mod ROOT, never a subfolder).
//   2. The .NET 8 test project (DedicatedServer.Tests), source-linked via <Compile Include="..">
//      so the emit-membership tests exercise this exact decision logic.
// Therefore: NO var, NO LINQ, NO lambdas, NO async, NO C#3+ syntax, NO engine types -- only
// plain System types + a static method. A C#3+ construct accidentally added here compiles green
// in the test project but silently breaks the csc-2.0 mod build.
//
// NOTE: deliberately NO `#nullable` directive -- that pragma is C# 8 and csc 2.0 rejects it
// (CS1024). The method takes only bool inputs, so nullable-reference analysis is moot.
namespace SimsOnlineServerHost
{
    /// <summary>
    /// (pure-puppet Phase 2): the PURE membership decision for emitting an UNCLAIMED
    /// Sim's autonomous interaction to the mirror (<c>PollUnclaimedRoutingEmit</c> in the mod shell).
    /// Free of engine types so it is unit-testable in DedicatedServer.Tests, modelled on the
    /// <see cref="RoutingInteractionFilter"/> and <see cref="ClientCaptureFilter"/>.
    ///
    /// WHY THIS EXISTS: the two existing host emits cover disjoint sets -- the legacy
    /// DISABLE_AUTONOMY watch set (<c>PollAutonomyWatchQueues</c>) and CLAIMED Sims under the
    /// command gate (<c>PollClaimedSimInteractionEmit</c>). Neither emits an UNCLAIMED townie's
    /// host-run autonomous interaction, so a townie that autonomously walks (GoHome off the visible
    /// lot, VisitCommunityLot, an intra-lot Terrain.GoHere) only teleport-slides on the
    /// position stream on the client puppet -- it never walks. This filter gates the emit that closes
    /// that gap for the autonomous-townie case.
    ///
    /// THE FOUR GATES (all must hold to emit):
    ///   * ON AN INTEREST LOT (tier-1): the Sim is on a lot where some player has a claimed
    ///     Sim. A townie acting across an empty corner of the world is invisible to every player, so
    ///     emitting it only floods the single-frame host->controller bridge (the throughput
    ///     floor). Tier-1 scoping here mirrors the controller's per-client interest gate
    ///     (MirroringInterestScope.ShouldMirrorInteractionQueued) so the two never disagree.
    ///   * NOT ALREADY EMITTED ELSEWHERE: a Sim that is claimed (covered by PollClaimedSimInteractionEmit)
    ///     or in the legacy autonomy-watch set (covered by PollAutonomyWatchQueues) must be skipped here
    ///     or it would be emitted twice in one tick.
    ///   * REPLAYABLE: the interaction carries a replayable target. ROUTING interactions
    ///     (per <see cref="RoutingInteractionFilter"/>) carry a route destination, and STATIONARY
    ///     interactions that target a world object carry the object id + its position
    ///     (<c>BuildInteractionQueuedLineForInstance</c> step B emits both). Either is replayable on
    ///     the client: the translator gates on <c>TargetPosition != null</c> OR <c>TargetObjectId != 0</c>
    ///     and the client <c>TryApplyInteractionFields</c> walk-to-target arm resolves
    ///     <c>GameObject.Position</c> at apply time. A stationary interaction with NO target object
    ///     (idle socialize, reactions) has nothing to replay -- the snapshot stream already carries
    ///     position, so emitting it would only flood the bridge with no replay benefit.
    /// </summary>
    public static class UnclaimedRoutingEmitFilter
    {
        /// <summary>
        /// True iff the mod should emit this Sim's current autonomous interaction as an unclaimed
        /// tier-1 replay. Fails closed: any gate not satisfied returns false.
        /// </summary>
        /// <param name="isOnInterestLot">The Sim is currently on a tier-1 (claimed) lot.</param>
        /// <param name="isClaimed">The Sim is claimed (already covered by PollClaimedSimInteractionEmit).</param>
        /// <param name="isLegacyAutonomyWatched">The Sim is in the legacy DISABLE_AUTONOMY watch set
        /// (already covered by PollAutonomyWatchQueues).</param>
        /// <param name="isRoutingInteraction">The Sim's current interaction is a routing interaction
        /// (per <see cref="RoutingInteractionFilter.IsRoutingInteraction"/>).</param>
        /// <param name="hasTargetObject">the Sim's current interaction targets a world object
        /// (<c>interaction.Target != null</c>). Stationary interactions with a target are replayable
        /// as a walk-to-target via the existing GoHere apply arm.</param>
        public static bool ShouldEmit(
            bool isOnInterestLot,
            bool isClaimed,
            bool isLegacyAutonomyWatched,
            bool isRoutingInteraction,
            bool hasTargetObject)
        {
            // Disjoint-by-design from the other two emits: never double-emit a Sim.
            if (isClaimed || isLegacyAutonomyWatched)
            {
                return false;
            }

            // Interest scope: spare the host->controller bridge for Sims no player can see.
            if (!isOnInterestLot)
            {
                return false;
            }

            // a routing interaction OR a stationary interaction with a target object is
            // replayable on the client. A stationary interaction with no target (idle socialize,
            // reactions) has nothing to replay -- the snapshot stream carries position.
            return isRoutingInteraction || hasTargetObject;
        }
    }
}
