// C#2 ONLY. This file is compiled two ways and must stay valid under BOTH:
//   1. The Sims 3 script mod, by csc.exe 2.0 (top-level *.cs glob over this directory: keep it in the mod ROOT).
//   2. The .NET 8 test project (DedicatedServer.Tests), source-linked via <Compile Include="..">.
// Therefore: NO var, NO LINQ, NO lambdas, NO C#3+ syntax, NO engine types.
using System.Collections.Generic;

namespace SimsOnlineServerHost
{
    // B2: what ServerHostMain.RepairOwnMachines does with a puppet's dead standing idle machine (a replayed Z
    // killed it). The engine's own re-creation (StandingPosture.StartHighLODIdle) enters "standing", the creation state,
    // while the server's idle stays where it was: the copy played its own idle entry over the stream's. When the stream
    // owns the idle (presenter on, PresenterHook.StreamOwnsIdle) and a server idle driver was borrowed onto the dead
    // machine, the repair enters the state the stream last gave that driver (ReplayStateCache), or standing_idle (the
    // engine's own recovery state) when it gave none. With no streamed idle on the Sim, the engine's creation stays.
    // Plan: internal documentation section 3.2.
    internal static class IdleRepairPlan
    {
        internal const int Nothing = 0;        // the machine lives, or the server's Sim does not stand
        internal const int EngineDefault = 1;  // StartHighLODIdle, the engine's creation entry (today's repair)
        internal const int ServerState = 2;    // enter the server idle driver's latest state
        internal const int StandingIdle = 3;   // enter standing_idle

        internal const string StandingIdleState = "standing_idle";

        internal static int Decide(bool machineDead, bool serverStanding, bool streamOwnsIdle, bool serverIdleMapped, bool stateKnown)
        {
            if (!machineDead || !serverStanding)
            {
                return Nothing;
            }

            if (!streamOwnsIdle || !serverIdleMapped)
            {
                return EngineDefault;
            }

            return stateKnown ? ServerState : StandingIdle;
        }

        // The repair label's suffix for the entry it made (repair:z/stream, repair:po/default).
        internal static string Kind(int plan)
        {
            return plan == ServerState || plan == StandingIdle ? "/stream" : "/default";
        }

        // The server drivers the replay borrowed onto a client handle (sReplayDrivers: server -> client; sReplayBorrowed:
        // the server drivers mapped onto the copy's own drivers), in the map's order.
        internal static List<uint> ServerDriversOn(uint clientHandle, Dictionary<uint, uint> drivers, Dictionary<uint, bool> borrowed)
        {
            List<uint> found = new List<uint>();
            if (clientHandle == 0u)
            {
                return found;
            }

            foreach (KeyValuePair<uint, uint> mapped in drivers)
            {
                if (mapped.Value == clientHandle && borrowed.ContainsKey(mapped.Key))
                {
                    found.Add(mapped.Key);
                }
            }

            return found;
        }
    }
}
