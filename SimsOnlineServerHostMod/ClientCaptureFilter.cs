// C#2 ONLY. This file is compiled two ways and must stay valid under BOTH:
//   1. The Sims 3 script mod, by csc.exe 2.0 via build-server-host-mod.ps1 (non-recursive
//      *.cs glob over this directory — keep this file in the mod ROOT, never a subfolder).
//   2. The .NET 8 test project (DedicatedServer.Tests), source-linked via <Compile Include="..">
//      so the capture-filter tests exercise this exact decision logic.
// Therefore: NO var, NO LINQ, NO lambdas, NO async, NO C#3+ syntax, NO engine types — only
// plain System types + a static method. A C#3+ construct accidentally added here compiles green
// in the test project but silently breaks the csc-2.0 mod build.
//
// NOTE: deliberately NO `#nullable` directive — that pragma is C# 8 and csc 2.0 rejects it
// (CS1024). The method tolerates a null key (treats it as "not forwardable").
namespace SimsOnlineServerHost
{
    /// <summary>
    /// the PURE capture-forward decision core for the client input interceptor
    /// (<c>OnActiveActorQueueChanged</c> in the mod shell). Free of engine types so it is
    /// unit-testable in DedicatedServer.Tests.
    ///
    /// THE BUG: the interceptor used to forward EVERY newly-queued interaction whose priority is
    /// <c>UserDirected</c> as a player intent, on the false premise "UserDirected on the active
    /// actor == the player clicked it." But the engine pushes many interactions onto a directed
    /// Sim's queue at UserDirected priority that the player never clicked — confirmed in the
    /// decompiled source: <c>ReactionInteraction</c> (Sim.PlayReaction caps reaction priority at
    /// UserDirected via GetReactionPriority and queues it with InteractionQueue.AddNext, Sim.cs
    /// 62873/63xxx), plus ~260 other engine call sites that CreateInstance at UserDirected
    /// (BuffExhausted.PassOut, BeGivenSimoleons, WatchRobotDance, Tutoring, VisitCommunityLot, ...).
    /// Any of those forwarded as a fake player click is rejected by the server's GoHere-only
    /// allowlist with "Unsupported interaction key: ..." (e.g. ReactionInteraction+Definition),
    /// which trips UT-1's wedge assertion and fails the run.
    ///
    /// THE FIX (allowlist, fail-closed): forward ONLY interactions whose definition type matches
    /// the server's supported player-command set. Today that set is Terrain.GoHere. This predicate
    /// is the EXACT same string test the server applies before accepting a QUEUE_INTERACTION /
    /// APPLY_INTERACTION_QUEUE command (ServerHostMain.cs HandleQueueInteraction :863 and
    /// HandleApplyInteractionQueue :2476), so client and server agree on "forwardable" and new
    /// reaction-like types fail closed instead of leaking as fake clicks.
    /// </summary>
    public static class ClientCaptureFilter
    {
        /// <summary>
        /// True iff a UserDirected interaction whose definition full-name is
        /// <paramref name="interactionKey"/> is a real player command the server will accept, and
        /// therefore should be forwarded as a captured intent. The interactionKey is the value the
        /// mod sends on the wire: <c>InteractionInstance.InteractionDefinition.GetType().FullName</c>.
        ///
        /// This is the same predicate the server uses to gate QUEUE_INTERACTION /
        /// APPLY_INTERACTION_QUEUE: the key must be the friendly form "Terrain.GoHere" OR contain
        /// the reflected nested-type form "Terrain+GoHere" (e.g.
        /// "Sims3.Gameplay.Core.Terrain+GoHere"). A null/empty key is never forwardable.
        /// </summary>
        public static bool ShouldForwardCapturedInteraction(string interactionKey)
        {
            if (interactionKey == null)
            {
                return false;
            }

            // Mirror the server allowlist EXACTLY (ServerHostMain.cs:863 / :2476):
            //   interactionKey == "Terrain.GoHere" || interactionKey.Contains("Terrain+GoHere")
            // Keep this in sync with those two sites: when the supported player-command set grows,
            // widen this predicate AND the server's checks together.
            return interactionKey == "Terrain.GoHere" || interactionKey.Contains("Terrain+GoHere");
        }
    }
}
