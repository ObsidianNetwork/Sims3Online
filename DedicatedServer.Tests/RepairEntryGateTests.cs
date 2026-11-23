using System.Collections.Generic;
using SimsOnlineServerHost;

namespace SimsOnline.DedicatedServer.Tests;

/// <summary>
/// B2+(a): the replay's first request for the state the own-machine repair just entered is dropped while the
/// engine's SACS pump has not taken that entry (Simulator.TicksElapsed() unchanged since it), and only then: same
/// driver, same state, 0x100 in the flags, no bridge, no other state line in between. Fixtures are a few stream lines in
/// the wire format; <see cref="GateReplay"/> applies them in ServerHostMain.ApplyReplayBatch's order, with B2's repair
/// (IdleRepairPlan, ReplayStateCache) choosing the entry.
/// </summary>
internal static partial class Program
{
    private const uint GateStandingIdle = 2083806798u; // standing_idle
    private const uint GateStanding = 2689084685u;     // standing, the engine's own creation entry
    private const string GateZ = "0|Z|1001";
    private const string GateA = "0|A|5|1001|0|0|11|1";
    private const string GateS = "0|S|5|84696423|1001";
    private const string GateP = "0|P|5|123|1";
    private const string GateIdleRequest = "0|R|5|84696423|2083806798|448|1001"; // the server's idle request, 0x1C0

    /// <summary>
    /// ServerHostMain.ApplyReplayBatch's order, for one Sim with an own standing idle machine (handle 7) and the server's
    /// idle driver 5. Lines run in order. A Z kills the own machine and marks the Sim stopped (the Z case). A Po line
    /// stands in for the mid-batch repair:po (ReplayStreamPosture). The stopped Sims are repaired at the batch end
    /// (repair:z), after every line. An A borrows the own machine while it lives (FindOwnDriver refuses a dead one) or
    /// acquires a fresh driver; an A for a mapped driver releases it first (ReleaseReplayDriver). A request on a dead
    /// handle fails kProxyInvalid, and the re-resolve borrows the live own machine and retries it. The repair is B2's
    /// (RepairStandingIdle): IdleRepairPlan over the server drivers borrowed onto the dead machine and ReplayStateCache,
    /// entering on a new own handle and marking the gate the way SacsRecorder.EnterState does under a repair label; then
    /// it hands an acquired driver over to the new own one (HandOverToOwnDrivers).
    /// </summary>
    private sealed class GateReplay
    {
        internal readonly RepairEntryGate Gate = new RepairEntryGate();
        internal readonly ReplayStateCache States = new ReplayStateCache();
        internal readonly List<string> Sent = new List<string>();
        internal readonly List<string> Dropped = new List<string>();
        internal readonly List<int> Plans = new List<int>();
        internal readonly List<uint> Entered = new List<uint>();
        internal int Reresolved;
        internal long Ticks = 1000;
        private readonly Dictionary<uint, uint> mMap = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, bool> mBorrowed = new Dictionary<uint, bool>();
        private readonly HashSet<uint> mDead = new HashSet<uint>();
        private uint mOwnIdle = 7;
        private uint mNext = 7;

        private bool OwnAlive => !mDead.Contains(mOwnIdle);

        // The warm state: the server's idle driver borrowed onto the copy's live own idle and its standing_idle request
        // applied there, a tick before the test's lines.
        internal GateReplay Warm()
        {
            Batch(GateA, GateIdleRequest);
            Sent.Clear();
            Ticks++;
            return this;
        }

        internal void Batch(params string[] lines)
        {
            bool stopped = false;
            List<string> work = new List<string>(lines);
            Dictionary<uint, bool> reresolved = new Dictionary<uint, bool>();
            for (int wi = 0; wi < work.Count; wi++)
            {
                string line = work[wi];
                string[] f = line.Split('|');
                string kind = f[1];
                if (kind == "Z")
                {
                    mDead.Add(mOwnIdle);
                    stopped = true;
                    continue;
                }

                if (kind == "Po")
                {
                    Repair();
                    continue;
                }

                uint server = uint.Parse(f[2]);
                if (kind == "A")
                {
                    if (mMap.ContainsKey(server))
                    {
                        Release(server);
                    }

                    if (OwnAlive)
                    {
                        mMap[server] = mOwnIdle;
                        mBorrowed[server] = true;
                    }
                    else
                    {
                        uint fresh = ++mNext;
                        Gate.NoteLine(fresh, kind);
                        mMap[server] = fresh;
                    }

                    continue;
                }

                if (!mMap.TryGetValue(server, out uint driver))
                {
                    continue;
                }

                if (kind == "R")
                {
                    if (mDead.Contains(driver))
                    {
                        // kProxyInvalid: resolve the driver again from its acquire line, once per batch, and retry.
                        if (!reresolved.ContainsKey(server))
                        {
                            reresolved[server] = true;
                            Reresolved++;
                            mBorrowed.Remove(server);
                            mMap.Remove(server);
                            work.Add(GateA);
                            work.Add(line);
                        }

                        continue;
                    }

                    bool bridged = f.Length > 10 && f[9] != "0";
                    if (Gate.TakeRequest(driver, uint.Parse(f[4]), uint.Parse(f[5]), bridged, Ticks))
                    {
                        Dropped.Add(line);
                    }
                    else
                    {
                        Sent.Add(line);
                    }

                    States.Set(server, uint.Parse(f[3]), uint.Parse(f[4])); // applied either way
                    continue;
                }

                Gate.NoteLine(driver, kind);
                if (kind == "E")
                {
                    States.Set(server, uint.Parse(f[3]), uint.Parse(f[4]));
                }
                else if (kind == "X")
                {
                    Release(server);
                }
            }

            if (stopped)
            {
                Repair();
            }
        }

        private void Release(uint server)
        {
            States.Forget(server);
            mBorrowed.Remove(server);
            mMap.Remove(server);
        }

        private void Repair()
        {
            if (OwnAlive)
            {
                return; // RepairOwnMachines re-creates a dead machine only
            }

            List<uint> servers = IdleRepairPlan.ServerDriversOn(mOwnIdle, mMap, mBorrowed);
            bool known = States.Latest(servers, out _, out _, out uint state);
            int plan = IdleRepairPlan.Decide(true, true, true, servers.Count > 0, known);
            uint entered = plan == IdleRepairPlan.ServerState ? state : plan == IdleRepairPlan.StandingIdle ? GateStandingIdle : GateStanding;
            mOwnIdle = ++mNext;
            Plans.Add(plan);
            Entered.Add(entered);
            Gate.Mark(mOwnIdle, entered, Ticks);
            foreach (uint mapped in new List<uint>(mMap.Keys))
            {
                if (!mBorrowed.ContainsKey(mapped) && !mDead.Contains(mMap[mapped]) && mMap[mapped] != mOwnIdle)
                {
                    mMap[mapped] = mOwnIdle;
                    mBorrowed[mapped] = true;
                }
            }
        }
    }

    [Test("RepairEntryGate.B2RepairSameTickDuplicateDropped")]
    private static void RepairEntryGateB2RepairSameTickDuplicateDropped()
    {
        GateReplay replay = new GateReplay().Warm();
        replay.Batch(GateZ);
        Assert(replay.Plans.Count == 1 && replay.Plans[0] == IdleRepairPlan.ServerState && replay.Entered[0] == GateStandingIdle,
            "B2: the repair enters the server idle driver's latest state, standing_idle, not the engine's standing.");
        replay.Batch("1|R|5|84696423|2083806798|448|1001"); // the live stream's next idle request, no tick in between
        Assert(replay.Reresolved == 1, "The request first hits the dead handle and is re-resolved onto the new machine.");
        Assert(replay.Dropped.Count == 1 && replay.Sent.Count == 0 && replay.Gate.Dropped == 1,
            "The duplicate request for the state just entered, before the pump, is dropped.");
    }

    [Test("RepairEntryGate.B2RepairNextTickRequestSent")]
    private static void RepairEntryGateB2RepairNextTickRequestSent()
    {
        GateReplay replay = new GateReplay().Warm();
        replay.Batch(GateZ);
        replay.Ticks++; // the pump took the entry
        replay.Batch("1|R|5|84696423|2083806798|448|1001");
        Assert(replay.Sent.Count == 1 && replay.Dropped.Count == 0 && replay.Gate.Dropped == 0,
            "A tick later the same request is served at the loop boundary: sent.");
    }

    [Test("RepairEntryGate.JoinNextBatchSamePumpDropsHistoryRequest")]
    private static void RepairEntryGateJoinNextBatchSamePumpDropsHistoryRequest()
    {
        GateReplay replay = new GateReplay().Warm();
        replay.Batch(GateZ);
        Assert(replay.Gate.Count == 1, "The batch-end repair marks the own idle it entered.");
        replay.Batch(GateA, GateS, GateP, GateIdleRequest); // the next batch of the same ring drain: no tick in between
        Assert(replay.Dropped.Count == 1 && replay.Sent.Count == 0 && replay.Gate.Dropped == 1,
            "The history's idle request on the borrowed, just-entered machine is dropped before the pump.");
        Assert(replay.Gate.Count == 0, "The drop consumes the mark.");
    }

    [Test("RepairEntryGate.ZAndRInOneBatchKeepsRequest")]
    private static void RepairEntryGateZAndRInOneBatchKeepsRequest()
    {
        GateReplay replay = new GateReplay().Warm();
        replay.Batch(GateZ, GateA, GateS, GateIdleRequest);
        Assert(replay.Sent.Count == 1 && replay.Dropped.Count == 0,
            "An R in the Z's own batch reaches an acquired driver before the batch-end repair: sent.");
        replay.Ticks++;
        replay.Batch(GateIdleRequest); // handed over to the repaired machine, after a pump
        Assert(replay.Sent.Count == 2 && replay.Dropped.Count == 0 && replay.Gate.Dropped == 0,
            "The next batch's request on the handed-over machine comes after the pump: sent.");
    }

    [Test("RepairEntryGate.PostureRepairThenRSameBatchDrops")]
    private static void RepairEntryGatePostureRepairThenRSameBatchDrops()
    {
        GateReplay replay = new GateReplay().Warm();
        replay.Batch(GateZ, "0|Po|1001", GateA, GateS, GateIdleRequest);
        Assert(replay.Dropped.Count == 1 && replay.Sent.Count == 0,
            "A request after a mid-batch repair:po, in the same batch, for the entered state: dropped.");
    }

    [Test("RepairEntryGate.NextPumpKeepsRequest")]
    private static void RepairEntryGateNextPumpKeepsRequest()
    {
        GateReplay replay = new GateReplay().Warm();
        replay.Batch(GateZ);
        replay.Ticks++; // the next frame with a tick: the pump took the entry
        replay.Batch(GateA, GateS, GateP, GateIdleRequest);
        Assert(replay.Sent.Count == 1 && replay.Dropped.Count == 0, "A request after the pump is sent (the 1-frame case).");
        Assert(replay.Gate.Count == 0, "And the mark is gone.");
    }

    [Test("RepairEntryGate.PausedFramesStillDrop")]
    private static void RepairEntryGatePausedFramesStillDrop()
    {
        GateReplay replay = new GateReplay().Warm();
        replay.Batch(GateZ);
        for (int frame = 0; frame < 5; frame++)
        {
            replay.Batch(); // paused frames: the Task-type runner runs, no tick, no pump (a frame number would move)
        }

        replay.Batch(GateA, GateS, GateP, GateIdleRequest);
        Assert(replay.Dropped.Count == 1 && replay.Sent.Count == 0,
            "Under pause the entry is still pending, so the request is still dropped: the tick counter, not the frame.");
    }

    [Test("RepairEntryGate.OtherStateKeepsRequest")]
    private static void RepairEntryGateOtherStateKeepsRequest()
    {
        GateReplay replay = new GateReplay(); // no streamed idle on the Sim yet: the engine's standing entry (the 044254 shape)
        replay.Batch(GateZ);
        Assert(replay.Plans[0] == IdleRepairPlan.EngineDefault && replay.Entered[0] == GateStanding, "The engine's creation entry.");
        replay.Batch(GateA, GateS, GateP, GateIdleRequest);
        Assert(replay.Sent.Count == 1 && replay.Dropped.Count == 0,
            "A request for another state than the entered one is a real transition: sent.");
        Assert(replay.Gate.Count == 0, "It consumes the mark.");
    }

    [Test("RepairEntryGate.WithoutCurrentStateFlagKeeps")]
    private static void RepairEntryGateWithoutCurrentStateFlagKeeps()
    {
        GateReplay replay = new GateReplay().Warm();
        replay.Batch(GateZ);
        replay.Batch(GateA, "0|R|5|84696423|2083806798|64|1001");
        Assert(replay.Sent.Count == 1 && replay.Dropped.Count == 0,
            "Without 0x100 the engine's own redundancy rule governs the request: sent.");
    }

    [Test("RepairEntryGate.BridgedRequestKeeps")]
    private static void RepairEntryGateBridgedRequestKeeps()
    {
        GateReplay replay = new GateReplay().Warm();
        replay.Batch(GateZ);
        replay.Batch(GateA, "0|R|5|84696423|2083806798|448|1001|0|0|9|84696423");
        Assert(replay.Sent.Count == 1 && replay.Dropped.Count == 0, "A bridged request is a hand-off, not an idle repeat: sent.");
    }

    [Test("RepairEntryGate.StreamLineInBetweenConsumes")]
    private static void RepairEntryGateStreamLineInBetweenConsumes()
    {
        GateReplay entered = new GateReplay().Warm();
        entered.Batch(GateZ);
        entered.Batch(GateA, "0|E|5|84696423|2083806798", GateIdleRequest);
        Assert(entered.Sent.Count == 1 && entered.Dropped.Count == 0, "An E on the driver before the R consumes the mark.");

        GateReplay interrupted = new GateReplay().Warm();
        interrupted.Batch(GateZ);
        interrupted.Batch(GateA, "0|I|5|84696423", GateIdleRequest);
        Assert(interrupted.Sent.Count == 1 && interrupted.Dropped.Count == 0, "So does an I.");

        RepairEntryGate gate = new RepairEntryGate();
        gate.Mark(8, GateStandingIdle, 1000);
        gate.NoteLine(8, "X");
        Assert(!gate.TakeRequest(8, GateStandingIdle, 448, false, 1000) && gate.Count == 0, "And an X.");
    }

    [Test("RepairEntryGate.SetupLinesDoNotConsume")]
    private static void RepairEntryGateSetupLinesDoNotConsume()
    {
        GateReplay replay = new GateReplay().Warm();
        replay.Batch(GateZ);
        replay.Batch(GateA, GateS, GateP, "0|Ps|5|124|text", "0|AS|5|1|2", GateIdleRequest);
        Assert(replay.Dropped.Count == 1 && replay.Sent.Count == 0,
            "Seeds, actors and parameters between the acquire and the request leave the mark in place.");
    }

    [Test("RepairEntryGate.OneShot")]
    private static void RepairEntryGateOneShot()
    {
        GateReplay replay = new GateReplay().Warm();
        replay.Batch(GateZ);
        replay.Batch(GateA, GateIdleRequest, GateIdleRequest);
        Assert(replay.Dropped.Count == 1 && replay.Sent.Count == 1, "Only the first request after the entry is dropped.");
    }

    [Test("RepairEntryGate.OtherDriverUnaffected")]
    private static void RepairEntryGateOtherDriverUnaffected()
    {
        RepairEntryGate gate = new RepairEntryGate();
        gate.Mark(8, GateStandingIdle, 1000);
        Assert(!gate.TakeRequest(9, GateStandingIdle, 448, false, 1000), "Another driver's request is sent.");
        gate.NoteLine(9, "E");
        Assert(gate.TakeRequest(8, GateStandingIdle, 448, false, 1000) && gate.Dropped == 1,
            "And leaves the marked driver's mark in place.");
    }

    [Test("RepairEntryGate.BoundedMarks")]
    private static void RepairEntryGateBoundedMarks()
    {
        RepairEntryGate gate = new RepairEntryGate();
        for (uint driver = 1; driver <= RepairEntryGate.MaxMarks + 1; driver++)
        {
            gate.Mark(driver, GateStandingIdle, 1000);
        }

        Assert(gate.Count == RepairEntryGate.MaxMarks, "At most 64 marks.");
        Assert(!gate.TakeRequest(1, GateStandingIdle, 448, false, 1000), "The 65th mark evicts the oldest.");
        Assert(gate.TakeRequest(2, GateStandingIdle, 448, false, 1000), "The rest stay.");
        gate.Mark(3, GateStanding, 1001); // a later entry on a marked driver replaces its mark
        Assert(!gate.TakeRequest(3, GateStandingIdle, 448, false, 1001) && gate.Count == RepairEntryGate.MaxMarks - 2,
            "A re-marked driver keeps one mark, its latest.");
    }
}
