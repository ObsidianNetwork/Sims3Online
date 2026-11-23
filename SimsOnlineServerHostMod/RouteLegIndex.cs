// C#2 ONLY. Compiled by the Sims 3 script mod (csc 2.0, top-level glob) and source-linked into DedicatedServer.Tests:
// NO var, NO LINQ, NO lambdas, NO engine types.
namespace SimsOnlineServerHost
{
    // The server's leg index of a walk, for the motion watch (scripts/motion_watch.py route_fields): a Sim walks through a
    // door as one walk in sub-paths, each its own Route.Start on the same Route (Presentation/Walks.cs, "The next leg of the
    // walk"). So the leg is the number of Starts this Route has had before this one; a stop or an Rx between legs does
    // not reset it, and a style update (Ru) is inside a leg and keeps the index.
    public static class RouteLegIndex
    {
        public static int OnStart(int startsBefore)
        {
            return startsBefore < 0 ? 0 : startsBefore;
        }
    }
}
