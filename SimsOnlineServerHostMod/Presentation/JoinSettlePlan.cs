// C#2 ONLY. Engine-free; source-linked into DedicatedServer.Tests.
using System;

namespace SimsOnline.Presentation
{
    internal static class JoinSettlePlan
    {
        // A consumed join does not cover root motion from the rest of its history clip. Non-seat objects
        // reconcile that drift too, but only after the same playback, stillness and animation gates.
        internal static bool ShouldSettle(bool joinDue, bool joinPlayed, bool nonSeatObject, bool transformOff,
            TimeSpan copyStill, TimeSpan serverStill, TimeSpan sinceAnimation, TimeSpan settle, TimeSpan animationHold)
        {
            return (joinDue || (nonSeatObject && transformOff)) && joinPlayed
                && copyStill >= settle && serverStill >= settle && sinceAnimation >= animationHold;
        }
    }
}
