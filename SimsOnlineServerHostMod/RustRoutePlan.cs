// C#2 ONLY. This file is compiled two ways and must stay valid under BOTH:
//   1. The Sims 3 script mod, by csc.exe 2.0 (top-level *.cs glob over this directory: keep it in the mod ROOT).
//   2. The .NET 8 test project (DedicatedServer.Tests), source-linked via <Compile Include="..">.
// Therefore: NO var, NO LINQ, NO lambdas, NO C#3+ syntax, NO engine types.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimsOnlineServerHost
{
    // card 55: the server's Sim walks the Rust kernel's NavigateAStar plan, taken at the "RouteCtor" seam
    // (docs/engine/boundary-recording.md, "Walks"). RouteRecorder overrides the four plan methods (Plan, PlanFromPoint,
    // Replan, ReplanFromPoint: the only managed callers of the four route-plan ICALLs, which are the only route entries into
    // the native search) and asks Decide whether to lay a Rust plan down as points (the G1b pattern: a custom path, replans
    // off, the plan result set to Succeeded) or to call the native planner.
    //
    // Off by default: the controller sends SACS_RECORDER rust-route-on for SIMSONLINE_RUST_ROUTE=1. Off, nothing is counted
    // and every plan is native, as before. On, every plan entry is counted (the fork-side planner-entry census card 55 E1
    // asks for as its second instrument) and every native fallback is counted by its reason: each one is a native search.
    //
    // Plans arrive through Offer, one per Sim, in metres, ending at the goal they were planned for; a plan is taken only by a
    // route of that Sim whose point destination is within GoalTolerance of the plan's last point, and only once.
    public static class RustRoutePlan
    {
        // The plan entry RouteRecorder was called through.
        public const int EntryPlan = 0;
        public const int EntryPlanFromPoint = 1;
        public const int EntryReplan = 2;
        public const int EntryReplanFromPoint = 3;
        public const int EntryCount = 4;

        // Decide's answers. UseRust lays the plan down; Native is the switch off (not counted); the rest are named
        // fallbacks to the native planner, counted.
        public const int UseRust = 0;
        public const int Native = 1;
        public const int FallbackNotSim = 2;       // the route's follower is not a Sim
        public const int FallbackNoGoal = 3;       // no point destination (a lot, slot or radial goal) to match a plan to
        public const int FallbackHasPath = 4;      // the route already holds a path (a replan of a planned walk)
        public const int FallbackNoPlan = 5;       // no Rust plan for this Sim and goal
        public const int FallbackInsertFailed = 6; // the engine refused the points (InsertCustomPathAtIndex false)
        public const int DecisionCount = 7;

        // Card 55 END_OF_WALK_SNAP_TOLERANCE_M: how near the route's goal a plan must end to be this route's plan (x/z).
        public const float GoalTolerance = 0.10f;

        private static readonly string[] DecisionNames = new string[]
        {
            "rust", "native", "notSim", "noGoal", "hasPath", "noPlan", "insertFailed",
        };

        private static readonly string[] EntryNames = new string[] { "plan", "planFrom", "replan", "replanFrom" };

        private static bool sEnabled;
        private static readonly long[] sEntries = new long[EntryCount];
        private static readonly long[] sDecisions = new long[DecisionCount];
        private static long sOffered;
        private static long sOffersRefused;
        private static readonly Dictionary<ulong, float[]> sPlans = new Dictionary<ulong, float[]>();
        private static long sRequested;
        private static long sWaitTimeouts;
        private static long sWaitDeclined;
        private static long sWaitNoYield;
        private static long sWaitInPump;

        // True while the mod's bridge pump task runs (ServerHostMain.BridgeTaskTick). That task is Threaded, so
        // CheckYieldingContext is true inside it, but a Plan* waiting there would stall the pump that has to read the answer:
        // RequestAndWait never waits while this is set.
        public static bool InBridgePump;

        // Posts the request line (EVENT|RoutePlanRequest, BridgeCodec.EventRoutePlanRequest) on the memory bridge. The mod sets it
        // when the switch goes on; null (tests, switch off) means no request is made.
        public delegate void LineSender(string line);
        public static LineSender RequestSender;

        // What the wait needs from the engine, so the loop is testable without it: whether this thread may yield
        // (Simulator.CheckYieldingContext(false)), the clock, and one yield (Simulator.Sleep(0)).
        public interface IWaitEnv
        {
            bool CanYield { get; }
            DateTime UtcNow { get; }
            void Yield();
        }

        public interface IWaitCaller
        {
            ulong CallerTask { get; }
        }

        // The switch (SACS_RECORDER rust-route-on / rust-route-off). Turning it either way drops plans not yet taken.
        public static bool Enabled
        {
            get { return sEnabled; }
            set
            {
                sEnabled = value;
                sPlans.Clear();
                RustBridgeWait.Cancel("route");
            }
        }

        public static string NameOf(int decision)
        {
            return decision >= 0 && decision < DecisionCount ? DecisionNames[decision] : "unknown";
        }

        // What a plan call does. plan: the Sim's Rust plan for this route's goal (Take), or null.
        public static int Decide(bool enabled, bool simFollower, bool hasGoal, bool hasPath, float[] plan)
        {
            if (!enabled)
            {
                return Native;
            }

            if (!simFollower)
            {
                return FallbackNotSim;
            }

            if (!hasGoal)
            {
                return FallbackNoGoal;
            }

            if (hasPath)
            {
                return FallbackHasPath;
            }

            return plan == null ? FallbackNoPlan : UseRust;
        }

        // A plan for a Sim: x,y,z triples in metres, at least two points, the last one the goal. It replaces the Sim's plan
        // not yet taken. False (and nothing kept) when the switch is off or the points are not whole, finite triples.
        public static bool Offer(ulong sim, float[] xyz)
        {
            if (!sEnabled || sim == 0uL || !ValidPoints(xyz))
            {
                sOffersRefused++;
                return false;
            }

            float[] copy = new float[xyz.Length];
            Array.Copy(xyz, copy, xyz.Length);
            sPlans[sim] = copy;
            sOffered++;
            return true;
        }

        // The Sim's plan when it ends within GoalTolerance (x/z) of the goal, removed so it is walked once; else null (a plan
        // for another goal stays for its own route).
        public static float[] Take(ulong sim, float goalX, float goalZ)
        {
            float[] plan;
            if (!sPlans.TryGetValue(sim, out plan))
            {
                return null;
            }

            int last = plan.Length - 3;
            float dx = plan[last] - goalX;
            float dz = plan[last + 2] - goalZ;
            if (dx * dx + dz * dz > GoalTolerance * GoalTolerance)
            {
                return null;
            }

            sPlans.Remove(sim);
            return plan;
        }

        public static bool ValidPoints(float[] xyz)
        {
            if (xyz == null || xyz.Length < 6 || xyz.Length % 3 != 0)
            {
                return false;
            }

            for (int i = 0; i < xyz.Length; i++)
            {
                if (float.IsNaN(xyz[i]) || float.IsInfinity(xyz[i]))
                {
                    return false;
                }
            }

            return true;
        }

        // One plan call through the seam (switch on only: Native, the switch off, is not counted).
        public static void Count(int entry, int decision)
        {
            if (decision == Native || entry < 0 || entry >= EntryCount || decision < 0 || decision >= DecisionCount)
            {
                return;
            }

            sEntries[entry]++;
            sDecisions[decision]++;
        }

        // Native searches the seam let through while on: every counted fallback (card 55 E1 counts each).
        public static long Fallbacks()
        {
            long n = 0L;
            for (int i = 0; i < DecisionCount; i++)
            {
                if (i != UseRust && i != Native)
                {
                    n += sDecisions[i];
                }
            }

            return n;
        }

        // rustRoute:on,plan:N,planFrom:N,replan:N,replanFrom:N,rust:N,notSim:N,...,fallbacks:N,offered:N,refused:N,pending:N
        // ("rustRoute:off" alone when off): more fields of RouteRecorder.Report's comma list.
        public static string Report()
        {
            if (!sEnabled)
            {
                return "rustRoute:off";
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            System.Text.StringBuilder b = new System.Text.StringBuilder("rustRoute:on");
            for (int i = 0; i < EntryCount; i++)
            {
                b.Append(',').Append(EntryNames[i]).Append(':').Append(sEntries[i].ToString(c));
            }

            for (int i = 0; i < DecisionCount; i++)
            {
                if (i != Native)
                {
                    b.Append(',').Append(DecisionNames[i]).Append(':').Append(sDecisions[i].ToString(c));
                }
            }

            b.Append(",fallbacks:").Append(Fallbacks().ToString(c));
            b.Append(",offered:").Append(sOffered.ToString(c)).Append(",refused:").Append(sOffersRefused.ToString(c));
            b.Append(",pending:").Append(sPlans.Count.ToString(c));
            b.Append(",asked:").Append(sRequested.ToString(c)).Append(",waitTimeout:").Append(sWaitTimeouts.ToString(c));
            b.Append(",waitDeclined:").Append(sWaitDeclined.ToString(c)).Append(",waitNoYield:").Append(sWaitNoYield.ToString(c)).Append(",waitInPump:").Append(sWaitInPump.ToString(c));
            return b.ToString();
        }

        // Whether a fault caught around a wait must go on up: a Sim reset or a thread abort raised out of Simulator.Sleep is the
        // engine tearing the task down, and the native plan must not run over it. By type name for ResetException (an engine
        // type this PURE file cannot name) and ThreadAbortException (the game's runtime lacks the type), walking the bases.
        public static bool MustRethrow(Exception e)
        {
            for (Type t = e == null ? null : e.GetType(); t != null; t = t.BaseType)
            {
                if (t.Name == "ResetException" || t.Name == "ThreadAbortException")
                {
                    return true;
                }
            }

            return false;
        }

        // Bridge answers carry the request corr, never the Sim id used by pre-offered front-door plans.
        public static bool OfferWait(ulong corr, float[] xyz)
        {
            bool accepted = sEnabled && ValidPoints(xyz) && RustBridgeWait.Offer("route", corr, xyz, null);
            if (accepted) sOffered++;
            else sOffersRefused++;
            return accepted;
        }

        public static bool DeclineWait(ulong corr)
        {
            return sEnabled && RustBridgeWait.Offer("route", corr, null, "noDaemon");
        }

        // Card 55 R3 option b. Posts the request for this Sim's walk and yields (one Sleep(0) per pass) until the plan is
        // offered, the controller declines, or timeoutSeconds pass. Null in every other case, and the caller falls back to the
        // native search, which Decide counts as noPlan: switch off, no sender, a thread that may not yield (Sleep would throw),
        // a sender fault, a decline, a timeout. Never blocks past the timeout; each pass gives the scheduler back.
        public static float[] RequestAndWait(ulong sim, float sx, float sy, float sz, float gx, float gy, float gz,
            double timeoutSeconds, IWaitEnv env)
        {
            if (!sEnabled || sim == 0uL || RequestSender == null || env == null)
            {
                return null;
            }

            if (InBridgePump)
            {
                sWaitInPump++;
                return null;
            }

            if (!env.CanYield)
            {
                sWaitNoYield++;
                return null;
            }

            sPlans.Remove(sim);
            sRequested++;
            string reason;
            float[] answer = RustBridgeWait.Request("route",
                delegate(ulong corr) { return BridgeLine(sim, sx, sy, sz, gx, gy, gz) + "|" + corr.ToString(CultureInfo.InvariantCulture); },
                RequestSender, timeoutSeconds, env, out reason) as float[];
            if (answer != null)
            {
                int last = answer.Length - 3;
                float dx = answer[last] - gx;
                float dz = answer[last + 2] - gz;
                if (dx * dx + dz * dz > GoalTolerance * GoalTolerance)
                {
                    sOffersRefused++;
                    answer = null;
                }
            }
            if (answer == null)
            {
                if (reason == "timeout")
                {
                    sWaitTimeouts++;
                }
                else if (reason == "noDaemon")
                {
                    sWaitDeclined++;
                }
                else
                {
                    sWaitNoYield++;
                }
            }
            return answer;
        }

        // RequestAndWait appends |corr to this payload; pre-offered front-door routes need no wait.
        public static string BridgeLine(ulong sim, float sx, float sy, float sz, float gx, float gy, float gz)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "EVENT|RoutePlanRequest|" + sim.ToString(c) + "|" + sx.ToString("R", c) + "|" + sy.ToString("R", c) + "|"
                + sz.ToString("R", c) + "|" + gx.ToString("R", c) + "|" + gy.ToString("R", c) + "|" + gz.ToString("R", c);
        }

        // The SACS_RECORDER rust-plan verb's point list: x,y,z,x,y,z,... in metres. False (points null) when it is not whole
        // finite triples, at least two.
        public static bool TryParsePoints(string csv, out float[] points)
        {
            points = null;
            if (csv == null || csv.Length == 0)
            {
                return false;
            }

            string[] parts = csv.Split(',');
            float[] values = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                {
                    return false;
                }
            }

            if (!ValidPoints(values))
            {
                return false;
            }

            points = values;
            return true;
        }

        // Tests: back to the state the mod loads in.
        public static void Reset()
        {
            sEnabled = false;
            RequestSender = null;
            RustBridgeWait.Cancel("route");
            sRequested = 0L;
            sWaitTimeouts = 0L;
            sWaitDeclined = 0L;
            sWaitNoYield = 0L;
            sWaitInPump = 0L;
            InBridgePump = false;
            sPlans.Clear();
            Array.Clear(sEntries, 0, sEntries.Length);
            Array.Clear(sDecisions, 0, sDecisions.Length);
            sOffered = 0L;
            sOffersRefused = 0L;
        }
    }

    // One yield loop for route and road placement. Only active, correctly typed correlations can be answered.
    // Removing a completed/expired entry rejects every late or duplicate answer without a growing tombstone set.
    public static class RustBridgeWait
    {
        public delegate string RequestLine(ulong corr);
        private sealed class Pending
        {
            internal string Channel;
            internal bool Done;
            internal object Answer;
            internal string Reason;
            internal DateTime Deadline;
            internal RustRoutePlan.IWaitEnv Env;
        }

        private static readonly Dictionary<ulong, Pending> sPending = new Dictionary<ulong, Pending>();
        private static readonly Dictionary<ulong, bool> sTasks = new Dictionary<ulong, bool>();
        private static ulong sCorr;

        public static double BoundSeconds(double seconds)
        {
            return double.IsNaN(seconds) ? 0.75 : Math.Max(0.05, Math.Min(5.0, seconds));
        }

        public static bool Offer(string channel, ulong corr, object answer, string reason)
        {
            Pending p;
            if (!sPending.TryGetValue(corr, out p) || p.Channel != channel || p.Done || p.Env.UtcNow >= p.Deadline)
            {
                return false;
            }
            p.Answer = answer;
            p.Reason = reason;
            p.Done = true;
            return true;
        }

        public static void Cancel(string channel)
        {
            foreach (Pending p in sPending.Values)
            {
                if (p.Channel == channel)
                {
                    p.Reason = "noDaemon";
                    p.Answer = null;
                    p.Done = true;
                }
            }
        }

        public static object Request(string channel, RequestLine line, RustRoutePlan.LineSender sender,
            double seconds, RustRoutePlan.IWaitEnv env, out string reason)
        {
            reason = "noDaemon";
            if (sender == null || env == null)
            {
                return null;
            }
            RustRoutePlan.IWaitCaller caller = env as RustRoutePlan.IWaitCaller;
            ulong task = caller == null ? 0uL : caller.CallerTask;
            if (task != 0uL && sTasks.ContainsKey(task))
            {
                return null;
            }
            Pending p = new Pending();
            p.Channel = channel;
            p.Env = env;
            p.Deadline = env.UtcNow.AddSeconds(BoundSeconds(seconds));
            ulong corr = ++sCorr; // per boot, shared across both request kinds; never reset by a switch or test reset
            sPending.Add(corr, p);
            if (task != 0uL) sTasks.Add(task, true);
            try
            {
                try
                {
                    sender(line(corr));
                }
                catch (Exception e)
                {
                    if (RustRoutePlan.MustRethrow(e)) throw;
                    return null;
                }
                while (true)
                {
                    if (p.Done)
                    {
                        reason = p.Reason;
                        return p.Answer;
                    }
                    if (env.UtcNow >= p.Deadline)
                    {
                        reason = "timeout";
                        return null;
                    }
                    env.Yield();
                }
            }
            finally
            {
                sPending.Remove(corr);
                if (task != 0uL) sTasks.Remove(task);
            }
        }
    }
}
