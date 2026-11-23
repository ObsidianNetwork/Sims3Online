// C#2 ONLY. This file is compiled two ways and must stay valid under BOTH:
//   1. The Sims 3 script mod, by csc.exe 2.0 (top-level *.cs glob over this directory: keep it in the mod ROOT).
//   2. The .NET 8 test project (DedicatedServer.Tests), source-linked via <Compile Include="..">.
// Therefore: NO var, NO LINQ, NO lambdas, NO C#3+ syntax, NO engine types.
using System.Collections.Generic;

namespace SimsOnlineServerHost
{
    // B2+(a): the replay's first request for the state the own-machine repair just entered, dropped while the
    // engine has not taken that entry yet. A repair (ServerHostMain.RepairOwnMachines, any caller) enters a state on a
    // fresh own driver; the entry waits on the SACS manager's pending list until the next tick block's pump
    // (internal documentation). A stream R for the same state with kRequestAllowRequestForCurrentState
    // (0x100) pumped together with that entry is planned as a full loop back to the entered node, so the copy enters it
    // twice in one pump: a client-only restart. Once the pump has run, the same request is served at the loop boundary,
    // as on the server. The pump boundary is read as Simulator.TicksElapsed() unchanged since the entry (the counter
    // advances only after the pump, and never while paused), which the shell passes in; no time, frame count or distance.
    // The rule and its engine basis: internal documentation ("The repair's entry and the replay's first
    // request"); tests: DedicatedServer.Tests/RepairEntryGateTests.cs.
    internal sealed class RepairEntryGate
    {
        // Sims3.SimIFace.SACS.DriverRequestFlags.kRequestAllowRequestForCurrentState.
        internal const uint CurrentStateFlag = 0x100u;
        // Marks kept at once; the oldest goes first. A mark lives until its driver's next stream line, so a handful at most.
        internal const int MaxMarks = 64;

        private struct Entry
        {
            internal uint State;
            internal long Ticks;
        }

        private readonly Dictionary<uint, Entry> mMarks = new Dictionary<uint, Entry>();
        private readonly List<uint> mOrder = new List<uint>();
        private long mDropped;

        // Requests dropped since the mod loaded (idleRepairDupDropped).
        internal long Dropped
        {
            get { return mDropped; }
        }

        internal int Count
        {
            get { return mMarks.Count; }
        }

        // The repair entered a state on a client driver at that tick count. A later entry on the same driver replaces it.
        internal void Mark(uint clientDriver, uint state, long ticks)
        {
            if (mMarks.ContainsKey(clientDriver))
            {
                mOrder.Remove(clientDriver);
            }
            else if (mMarks.Count >= MaxMarks)
            {
                mMarks.Remove(mOrder[0]);
                mOrder.RemoveAt(0);
            }

            Entry entry;
            entry.State = state;
            entry.Ticks = ticks;
            mMarks[clientDriver] = entry;
            mOrder.Add(clientDriver);
        }

        // A replayed R on a client driver: true means drop it (do not send it to the proxy; count it applied). It always
        // consumes the driver's mark, dropped or not: only the first stream request after the entry can repeat it.
        internal bool TakeRequest(uint clientDriver, uint state, uint flags, bool bridged, long ticks)
        {
            Entry entry;
            if (!Consume(clientDriver, out entry))
            {
                return false;
            }

            if (entry.Ticks != ticks || entry.State != state || (flags & CurrentStateFlag) == 0u || bridged)
            {
                return false;
            }

            mDropped++;
            return true;
        }

        // Any other replayed line on a client driver: a state-changing one consumes its mark, setup lines do not (the
        // history sends seeds, actors and parameters between the acquire and the request).
        internal void NoteLine(uint clientDriver, string kind)
        {
            Entry entry;
            if (mMarks.Count > 0 && ConsumesMark(kind))
            {
                Consume(clientDriver, out entry);
            }
        }

        // E (enter), R (request), I (interrupt), X (release) and A (a freshly acquired driver) change what the driver
        // plays; S, P, Ps, Pn, Ph, AS, AW and the rest only set it up.
        internal static bool ConsumesMark(string kind)
        {
            return kind == "E" || kind == "R" || kind == "I" || kind == "X" || kind == "A";
        }

        private bool Consume(uint clientDriver, out Entry entry)
        {
            if (!mMarks.TryGetValue(clientDriver, out entry))
            {
                return false;
            }

            mMarks.Remove(clientDriver);
            mOrder.Remove(clientDriver);
            return true;
        }
    }
}
