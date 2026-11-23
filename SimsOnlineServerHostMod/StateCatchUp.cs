using System;
using System.Collections.Generic;
using System.Globalization;
using Sims3.SimIFace;
using Sims3.SimIFace.SACS;

namespace SimsOnlineServerHost
{
    // State catch-up on a client (tunable sacs.catchup, 0 = off). A request reaches the copy at a different point in its clip
    // than the server's: a machine in a loop leaves it at its own Loop clip's end, so the copy of a shower stepped out
    // 0-2.3 s after the server's Sim (2026-10-17, internal documentation). The SACS events say when each game's
    // machine actually entered a state (EventQueueRecorder): the server streams its StateEntered events (Se|driver|state).
    //
    // When the server's machine has passed a node on the way to the destination the client last asked for, and the
    // client's machine has entered neither that node nor the destination since its own request (after a grace for the
    // stream's latency), the copy is still waiting for its clip to end: the request is made again with the mode's flags,
    // once per request. A node equal to the destination is not acted on: the copy is then mid-transition, and cutting it
    // would pop the Sim. A request handed over by a bridge is left alone.
    //   1: kInterrupt
    //   2: kInterrupt | kRequestAllowDuplicateRequest
    //   3: kRequestDoNotWaitForAnimation | kRequestAllowDuplicateRequest
    //   4: kInterrupt | kRequestDoNotWaitForAnimation | kRequestAllowDuplicateRequest
    //   5: kClearQueue | kRequestDoNotWaitForAnimation | kRequestAllowDuplicateRequest
    //   6: kClearQueue | kRequestDoNotWaitForAnimation
    // Never kRequestAllowRequestForCurrentState: it restarts a state the copy is already in.
    //
    // RESULT (2026-10-17, shower runs 20261017-2217..2241, modes 2, 3, 5 and 6): no mode works; keep it at 0. No flag mix
    // cut the copy's loop: in 3 of the 4 forced mode 5/6 runs it still left at its own next Loop boundary, ~800 ms after
    // the re-request (an apparent ~100 ms cut in a mode 3 run was a natural boundary), and every executed re-request made
    // it enter Exit Working twice (motion_watch: restart). ISacsProxy.Interrupt is not implemented in the release engine
    // (ScriptCore.SACS.Interrupt throws). The exit lag has two causes this cannot reach (internal documentation):
    // a per-process Enter clip length (~460 ms more between clip events 747 and 1001 on one game), and a request that
    // reaches the copy just after its own loop's decision point (the copy misses the server's first boundary and leaves a
    // whole cycle later). Kept dormant with its counters; the Se stream and the event recorder measure both causes.
    internal static class StateCatchUp
    {
        private sealed class Request
        {
            public uint Driver;
            public uint Actor;
            public uint State;
            public uint Flags;
            public float Wakeup;
            public uint Timeout;
            public long Ms;
            public bool Bridged;
            public bool Caught;
        }

        // A check is bound to the request current when the server's Se arrived: the ring keeps the server's order of R and
        // Se, so an Se from before a newer request (a loop boundary just before the server asked to exit) is never judged
        // against it (it would force the exit a whole loop before the server's).
        private sealed class Check
        {
            public uint ServerDriver;
            public uint Node;
            public long Due;
            public Request Request;
        }

        // Runs the due checks every simulator update while any is pending (replay batches come only when lines do).
        private sealed class CheckTask : Sims3.SimIFace.Task
        {
            public override void Simulate()
            {
                bool keep = false;
                try
                {
                    keep = Tick();
                }
                catch (Exception)
                {
                    SwallowedErrors.Note();
                }

                if (!keep)
                {
                    sScheduled = false;
                    Simulator.DestroyObject(ObjectId);
                }
            }
        }

        private static readonly Dictionary<uint, Request> sRequests = new Dictionary<uint, Request>();
        private static readonly List<Check> sChecks = new List<Check>();
        private static readonly Dictionary<string, long> sResults = new Dictionary<string, long>();
        private static Dictionary<uint, uint> sReplayDrivers;
        private static bool sScheduled;
        internal static long Armed;
        internal static long Forced;
        internal static long Aligned;
        internal static long Unobserved;

        internal static int Mode
        {
            get { return (int)Tunables.Get("sacs.catchup", 0.0); }
        }

        internal static long NowMs()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        // A request this client replayed (kSuccess) on its driver for the server's.
        internal static void Requested(uint serverDriver, uint driver, uint actor, uint state, uint flags, float wakeup, uint timeout, bool bridged)
        {
            if (Mode <= 0)
            {
                return;
            }

            if (sRequests.Count >= 1024 && !sRequests.ContainsKey(serverDriver))
            {
                sRequests.Clear();
            }

            Request request = new Request();
            request.Driver = driver;
            request.Actor = actor;
            request.State = state;
            request.Flags = flags;
            request.Wakeup = wakeup;
            request.Timeout = timeout;
            request.Ms = NowMs();
            request.Bridged = bridged;
            sRequests[serverDriver] = request;
        }

        // Se|driver|state: the server's machine entered a state; checked after the grace (sacs.catchup.grace, seconds).
        internal static void ServerEntered(uint serverDriver, uint node, Dictionary<uint, uint> replayDrivers)
        {
            Request request;
            if (Mode <= 0 || !sRequests.TryGetValue(serverDriver, out request) || request.Caught || request.Bridged || request.State == node
                || sChecks.Count >= 256)
            {
                return;
            }

            sReplayDrivers = replayDrivers;
            Check check = new Check();
            check.ServerDriver = serverDriver;
            check.Node = node;
            check.Due = NowMs() + (long)(Tunables.Get("sacs.catchup.grace", 0.3) * 1000.0);
            check.Request = request;
            sChecks.Add(check);
            Armed++;
            if (!sScheduled)
            {
                sScheduled = true;
                Simulator.AddObject(new CheckTask());
            }
        }

        // The driver was released, or a replayed request on it failed: nothing to catch up to.
        internal static void Forget(uint serverDriver)
        {
            sRequests.Remove(serverDriver);
        }

        // Each simulator update while checks are pending; false when none is left.
        private static bool Tick()
        {
            if (sChecks.Count == 0)
            {
                return false;
            }

            SacsRecorder.Replaying = true;
            try
            {
                Run(SacsRecorder.Current, sReplayDrivers);
            }
            finally
            {
                SacsRecorder.Replaying = false;
            }

            return sChecks.Count > 0;
        }

        private static void Run(ISacsProxy proxy, Dictionary<uint, uint> replayDrivers)
        {
            int mode = Mode;
            if (mode <= 0 || proxy == null || replayDrivers == null)
            {
                sChecks.Clear();
                return;
            }

            long now = NowMs();
            for (int i = 0; i < sChecks.Count; i++)
            {
                Check check = sChecks[i];
                if (check.Due > now)
                {
                    continue;
                }

                sChecks.RemoveAt(i);
                i--;
                try
                {
                    Act(proxy, replayDrivers, check, mode);
                }
                catch (Exception)
                {
                    SwallowedErrors.Note();
                }
            }
        }

        private static void Act(ISacsProxy proxy, Dictionary<uint, uint> replayDrivers, Check check, int mode)
        {
            Request request;
            uint driver;
            if (!sRequests.TryGetValue(check.ServerDriver, out request) || !object.ReferenceEquals(request, check.Request) || request.Caught
                || !replayDrivers.TryGetValue(check.ServerDriver, out driver) || driver != request.Driver)
            {
                return;
            }

            if (!SacsRecorder.Observed(driver))
            {
                Unobserved++; // no queue of ours on this driver: its entries are unknown
                return;
            }

            if (SacsRecorder.EnteredSince(driver, check.Node, request.Ms) || SacsRecorder.EnteredSince(driver, request.State, request.Ms))
            {
                Aligned++;
                return;
            }

            request.Caught = true;
            Forced++;
            string result;
            uint extra = mode == 1 ? 0x2u : mode == 2 ? 0x82u : mode == 3 ? 0x480u : mode == 4 ? 0x482u : mode == 5 ? 0x481u : 0x401u;
            try
            {
                result = proxy.RequestState(false, driver, request.Actor, request.State, (DriverRequestFlags)(request.Flags | extra),
                    request.Wakeup, request.Timeout, 0u, 0u).ToString();
            }
            catch (Exception ex)
            {
                result = "error:" + ex.GetType().Name;
            }

            long seen;
            sResults.TryGetValue(result, out seen);
            sResults[result] = seen + 1;
            ulong actor = SacsRecorder.ActorOf(driver, request.Actor);
            if (actor != 0uL)
            {
                string machine = SacsRecorder.MachineOf(driver).ToString("x16", CultureInfo.InvariantCulture);
                MotionWatch.Note(actor, "sacs CatchUp " + machine + ":" + request.State.ToString(CultureInfo.InvariantCulture)
                    + " mode=" + mode.ToString(CultureInfo.InvariantCulture) + " result=" + result + " after the server entered "
                    + machine + ":" + check.Node.ToString(CultureInfo.InvariantCulture));
            }
        }

        internal static string Report()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            string results = string.Empty;
            foreach (KeyValuePair<string, long> entry in sResults)
            {
                results += "/" + entry.Key + "=" + entry.Value.ToString(c);
            }

            return "mode:" + Mode.ToString(c) + ",armed:" + Armed.ToString(c) + ",forced:" + Forced.ToString(c) + ",aligned:" + Aligned.ToString(c)
                + ",unobserved:" + Unobserved.ToString(c) + ",requests:" + sRequests.Count.ToString(c) + results;
        }
    }
}
