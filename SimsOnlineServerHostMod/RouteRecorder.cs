using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ObjectComponents;
using Sims3.SimIFace;

namespace SimsOnlineServerHost
{
    // The motion boundary (docs/architecture-deep-dive/boundary-seams.md). Every walk in the game is a Sims3.SimIFace.Route, built from
    // the constructor the engine leaves in AppDomain data ("RouteCtor", cached in Route.sCtorInfo), and starts moving in
    // Route.Start(RouteAnimationSpec, RouteStartFlags): the path is planned by then, and the spec is the walk style. On the
    // server this subclass of the engine's ScriptCore.Route records, for a route a Sim follows:
    //   Rs|sim|goalX|goalY|goalZ|tag|destSlot|destObj|waypoints|startMs|style|age|gender|species|flags|route   (Start)
    //   Ru|sim|style|age|gender|species|route                                                               (walk style)
    //   Rh|sim|x|y|z|route|stopMs|mode|moved|leftBefore|leftAfter|sinceMs|sinceTicks                          (Stop taken)
    //   Rx|sim|x|y|z|route|endMs|fx|fz|by|stopMode|stopAccepted|stopMs                                       (end)
    // The first fields are the route stream's Rt/Rte, so a client walks the server's path the way it did, now from the
    // moment the server's Sim starts moving and in its walk style; nothing polls the gameplay route actions. A route
    // that does not move the Sim (under RouteMinDistance: a social's turn to face) is not sent.
    internal sealed class RouteRecorder : ScriptCore.Route
    {
        // STOPGAP(boundary-recording): route-min-distance a turn to face is not sent, so the facing mirror turns the copy later; send every route.
        private const float RouteMinDistance = 0.3f;
        internal static long Starts;
        internal static long Stops;
        internal static long StyleChanges;
        internal static long StopCalls;
        internal static long StopsRefused;

        private bool mSent;
        private ulong mSim;
        private int mLeg; // the leg the motion watch notes (route_fields): Starts on this Route before this one (RouteLegIndex)
        private int mStartsOnRoute; // every Start on this Route instance; never reset by a stop or an Rx
        private Vector3 mStartPosition; // where the Sim was when the walk started
        // When it started (ms, the Rs line's startMs; SimClock ticks): Rh's offset into the walk (WalkHaltTiming).
        private long mStartMs;
        private long mStartTicks;
        // The last Stop call on this walk: the game's StopMode (-1: none), whether the route took it, and when (ms).
        private bool mStopping;
        private int mStopMode = -1;
        private bool mStopAccepted;
        private long mStopMs;
        private ulong mPrivateRequest;
        private uint mAgeGenderFlags;
        // A stopped walk still routing this long after its Stop ends where its Sim is (the slowest rest seen: 0.9 s).
        private const long StopRestCapMs = 3000L;

        public RouteRecorder()
        {
        }

        public RouteRecorder(IScriptProxy simObj, uint ageGenderFlags)
            : base(simObj, ageGenderFlags)
        {
            mAgeGenderFlags = ageGenderFlags;
        }

        private static FieldInfo CtorField()
        {
            return typeof(Sims3.SimIFace.Route).GetField("sCtorInfo", BindingFlags.NonPublic | BindingFlags.Static);
        }

        internal static bool Installed
        {
            get
            {
                FieldInfo field = CtorField();
                ConstructorInfo ctor = field == null ? null : field.GetValue(null) as ConstructorInfo;
                return ctor != null && ctor.DeclaringType == typeof(RouteRecorder);
            }
        }

        internal static string Install()
        {
            try
            {
                if (Installed)
                {
                    return "route=on";
                }

                ConstructorInfo ctor = typeof(RouteRecorder).GetConstructor(new Type[] { typeof(IScriptProxy), typeof(uint) });
                FieldInfo field = CtorField();
                if (ctor == null || field == null)
                {
                    return "route=unavailable";
                }

                AppDomain.CurrentDomain.SetData("RouteCtor", ctor);
                field.SetValue(null, ctor);
                return "route=on";
            }
            catch (Exception ex)
            {
                return "route=error:" + ex.GetType().Name;
            }
        }

        internal static string Report()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "installed:" + (Installed ? "1" : "0") + ",starts:" + Starts.ToString(c) + ",stops:" + Stops.ToString(c) + ",styles:" + StyleChanges.ToString(c)
                + ",stopCalls:" + StopCalls.ToString(c) + ",stopsRefused:" + StopsRefused.ToString(c)
                + (RustRoutePlan.Enabled ? "," + RustRoutePlan.Report() : "");
        }

        // card 55: the four plan entries (the only managed callers of the route-plan ICALLs). With the switch off
        // (SACS_RECORDER rust-route-on not sent: the default) each is the engine's own, unchanged. With it on, a Sim's route
        // whose point goal has a Rust plan (RustRoutePlan.Take) is laid down as that plan's points and never searched;
        // anything else is the native search, counted by its reason (RustRoutePlan.Report).
        public override RoutePlanResult Plan()
        {
            RoutePlanResult rust;
            return TryRustPlan(RustRoutePlan.EntryPlan, false, Vector3.Zero, out rust) ? rust : base.Plan();
        }

        public override RoutePlanResult PlanFromPoint(Vector3 startPosition)
        {
            RoutePlanResult rust;
            return TryRustPlan(RustRoutePlan.EntryPlanFromPoint, true, startPosition, out rust) ? rust : base.PlanFromPoint(startPosition);
        }

        public override RoutePlanResult Replan()
        {
            RoutePlanResult rust;
            return TryRustPlan(RustRoutePlan.EntryReplan, false, Vector3.Zero, out rust) ? rust : base.Replan();
        }

        public override RoutePlanResult ReplanFromPoint(Vector3 fromPt)
        {
            RoutePlanResult rust;
            return TryRustPlan(RustRoutePlan.EntryReplanFromPoint, true, fromPt, out rust) ? rust : base.ReplanFromPoint(fromPt);
        }

        // The G1b pattern (card 55 section I.1, scratch/InsertCustomRouteProbe.cs): the plan's points as one SimPath, replans off,
        // the plan result set to Succeeded by hand; the Sim's own DoRoute then walks it without the native search. False:
        // the caller runs the native planner. Never throws (a fault falls back to native and is noted).
        private bool TryRustPlan(int entry, bool hasStart, Vector3 start, out RoutePlanResult result)
        {
            result = PlanResult;
            if (!RustRoutePlan.Enabled)
            {
                return false;
            }
            if (RustLiveSimNavBuilder.Active)
            {
                ulong requestId = 0;
                try
                {
                    ulong sim = SimFollower();
                    Vector3 goal;
                    if (sim == 0 || !GetDestPoint(out goal)) return PrivateFailure(out result);
                    if (mPrivateRequest != 0 && GetNumPaths() > 0) { result = PlanResult; return true; }
                    Vector3 from = hasStart ? start : SimPosition(sim);
                    RustPrivateNavigation.Envelope envelope = RustLiveSimNavBuilder.RequestAndWait(sim, from, goal, out requestId);
                    if (envelope == null) return PrivateFailure(out result);
                    if (!RustPrivateNavigation.BuildTypedRoute(envelope, new PrivateBuilder(this))) return PrivateFailure(out result);
                    if (GetNumPaths() == 0)
                    {
                        int[] target = envelope.Legs[envelope.Legs.Length - 1].To;
                        if (!InsertCustomPathAtIndex(0, new Vector3[] { from, Metres(target) }, false, true, PathType.SimPath))
                            return PrivateFailure(out result);
                    }
                    ReplanAllowed = false;
                    RoutePlanResult planned = PlanResult; planned.mType = RoutePlanResultType.Succeeded;
                    PlanResult = planned; result = planned; mSim = sim; mPrivateRequest = requestId;
                    RustLiveSimNavBuilder.BindRoute(requestId, this);
                    RustRoutePlan.Count(entry, RustRoutePlan.UseRust);
                    requestId = 0;
                    return true;
                }
                catch (Exception e)
                {
                    if (RustRoutePlan.MustRethrow(e)) throw;
                    if (e is FormatException && e.Message.StartsWith("UNSUPPORTED_STAIR_", StringComparison.Ordinal))
                        RustLiveSimNavBuilder.StopUnsupportedStair(e.Message);
                    SwallowedErrors.Note();
                    return PrivateFailure(out result);
                }
                finally { if (requestId != 0) RustLiveSimNavBuilder.Cancel(requestId); }
            }

            try
            {
                ulong sim = SimFollower();
                Vector3 goal = Vector3.Zero;
                bool hasGoal = sim != 0uL && GetDestPoint(out goal);
                bool hasPath = hasGoal && GetNumPaths() > 0u;
                float[] plan = hasGoal && !hasPath ? RustRoutePlan.Take(sim, goal.x, goal.z) : null;
                if (plan == null && hasGoal && !hasPath)
                {
                    // Card 55 R3: ask the controller for this walk's plan and yield until it is offered, declined or the
                    // timeout passes. Off a yielding thread RequestAndWait returns null at once (native search, noPlan).
                    Vector3 from = hasStart ? start : SimPosition(sim);
                    plan = RustRoutePlan.RequestAndWait(sim, from.x, from.y, from.z, goal.x, goal.y, goal.z,
                        Tunables.Get("route.rust.wait.seconds", 0.75), YieldEnv.Instance);
                }

                int decision = RustRoutePlan.Decide(true, sim != 0uL, hasGoal, hasPath, plan);
                if (decision == RustRoutePlan.UseRust)
                {
                    Vector3[] points = new Vector3[plan.Length / 3];
                    for (int i = 0; i < points.Length; i++)
                    {
                        points[i] = new Vector3(plan[i * 3], plan[i * 3 + 1], plan[i * 3 + 2]);
                    }

                    if (InsertCustomPathAtIndex(0u, points, false, true, PathType.SimPath))
                    {
                        ReplanAllowed = false;
                        RoutePlanResult planned = PlanResult;
                        planned.mType = RoutePlanResultType.Succeeded;
                        PlanResult = planned;
                        result = planned;
                    }
                    else
                    {
                        decision = RustRoutePlan.FallbackInsertFailed;
                    }
                }

                RustRoutePlan.Count(entry, decision);
                return decision == RustRoutePlan.UseRust;
            }
            catch (Exception e)
            {
                if (RustRoutePlan.MustRethrow(e))
                {
                    throw; // a Sim reset or abort out of Simulator.Sleep: the engine's, not ours to swallow
                }

                SwallowedErrors.Note();
                return false;
            }
        }

        private bool PrivateFailure(out RoutePlanResult result)
        {
            RetirePrivatePlan();
            ClearPaths();
            result = PlanResult; result.mType = RoutePlanResultType.Failed; PlanResult = result;
            return true; // An active trusted attachment never falls through to native Plan*.
        }
        private static Vector3 Metres(int[] cm) { return new Vector3(cm[0] / 100f, cm[1] / 100f, cm[2] / 100f); }
        private sealed class PrivateBuilder : RustPrivateNavigation.IRouteBuilder
        {
            private readonly RouteRecorder mRoute;
            internal PrivateBuilder(RouteRecorder route) { mRoute = route; }
            public bool SubPath(RustPrivateNavigation.Leg leg)
            {
                return mRoute.InsertCustomPathAtIndex(mRoute.GetNumPaths(), new Vector3[] { Metres(leg.From), Metres(leg.To) },
                    false, true, PathType.SimPath);
            }
            public bool Portal(RustPrivateNavigation.Leg approach, RustPrivateNavigation.Leg through, RustPrivateNavigation.Leg exit)
            {
                ObjectGuid id = new ObjectGuid(through.Object);
                Sims3.Gameplay.Abstracts.GameObject obj = id.ObjectFromId<Sims3.Gameplay.Abstracts.GameObject>();
                PortalComponent portal = obj == null ? null : obj.PortalComponent;
                if (portal == null) throw new FormatException("portal object unavailable");
                Slot[] pair = portal.GetSlotsForLaneIndex(0);
                if (pair == null || pair.Length != 2 ||
                    !(((uint)pair[0] == through.EntrySlot && (uint)pair[1] == through.ExitSlot) ||
                    ((uint)pair[1] == through.EntrySlot && (uint)pair[0] == through.ExitSlot)))
                    throw new FormatException("portal slots changed");
                Vector3 entry = Slots.GetPositionOfSlot(id, through.EntrySlot, mRoute.mAgeGenderFlags, false);
                Vector3 landing = Slots.GetPositionOfSlot(id, through.ExitSlot, mRoute.mAgeGenderFlags, true);
                if (!mRoute.InsertCustomPathAtIndex(mRoute.GetNumPaths(), new Vector3[] { Metres(approach.From), entry },
                    false, true, PathType.SimPath)) return false;
                uint before = mRoute.GetNumPaths();
                if (!mRoute.InsertPortalSubPathAtIndex(before, id) || mRoute.GetNumPaths() <= before)
                    throw new FormatException(through.PathType == 0x80000001u ? "UNSUPPORTED_STAIR_INSERT_REFUSED" : "portal insertion refused");
                bool typed = false;
                string unsupported = through.PathType == 0x80000001u ? "UNSUPPORTED_STAIR_" : "portal ";
                for (uint i = before; i < mRoute.GetNumPaths(); i++)
                {
                    PathData path = mRoute.GetPathData(i);
                    if (path.PathType == PathType.PortalPath || path.PathType == PathType.PortalTraversalPath)
                    {
                        if (path.ObjectId != id) throw new FormatException(unsupported + "IDENTITY_MISMATCH");
                        typed = true;
                    }
                    else if (path.PathType != PathType.SimPath)
                        throw new FormatException(unsupported + "INSERTED_PATH_TYPE");
                }
                if (!typed) throw new FormatException(through.PathType == 0x80000001u ?
                    "UNSUPPORTED_STAIR_TYPED_PATH_SHAPE" : "portal typed path missing");
                Vector3 insertedEntry = Vector3.Invalid;
                mRoute.GetPathStartPoint(before, ref insertedEntry);
                if ((insertedEntry - entry).Length() > 0.10f) throw new FormatException(unsupported + "ENTRY_MISMATCH");
                return mRoute.InsertCustomPathAtIndex(mRoute.GetNumPaths(), new Vector3[] { landing, Metres(exit.To) },
                    false, true, PathType.SimPath);
            }
        }
        internal bool PrivateFinished { get { return !IsActive || !StillFollowing(mSim); } }
        internal void RevokePrivatePlan()
        {
            mPrivateRequest = 0;
            Stop(StopMode.kStop_Hard);
        }
        internal void RetirePrivatePlan()
        {
            ulong id = mPrivateRequest; mPrivateRequest = 0;
            RustLiveSimNavBuilder.Cancel(id);
        }
        public override void Dispose()
        {
            try { RetirePrivatePlan(); }
            finally { base.Dispose(); }
        }

        // The walk's own events for the motion watch (scripts/motion_watch.py route_fields): a no-op unless a watch is on.
        private void WatchNote(string what)
        {
            try
            {
                MotionWatch.Note(mSim, what);
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        private static Vector3 SimPosition(ulong sim)
        {
            Sim s = new ObjectGuid(sim).ObjectFromId<Sim>();
            return s != null ? s.Position : Vector3.Zero;
        }

        // The engine side of RustRoutePlan.RequestAndWait: yielding is allowed only inside a Sim task (CheckYieldingContext
        // false never throws), and one yield is Simulator.Sleep(0): run again next scheduler pass.
        internal sealed class YieldEnv : RustRoutePlan.IWaitEnv, RustRoutePlan.IWaitCaller
        {
            public static readonly YieldEnv Instance = new YieldEnv();

            public ulong CallerTask
            {
                get { return Simulator.CurrentTask.Value; }
            }

            public bool CanYield
            {
                get { return Simulator.CheckYieldingContext(false); }
            }

            public DateTime UtcNow
            {
                get { return DateTime.UtcNow; }
            }

            public void Yield()
            {
                Simulator.Sleep(0u);
            }
        }

        private static string Spec(RouteAnimationSpec spec)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return spec.mStyleNameHash.ToString(c) + "|" + spec.mAge.ToString(c) + "|" + spec.mGender.ToString(c) + "|" + spec.mSpecies.ToString(c);
        }

        private ulong SimFollower()
        {
            try
            {
                IScriptProxy follower = Follower;
                if (follower == null)
                {
                    return 0uL;
                }

                ObjectGuid id = follower.ObjectId;
                return id.IsValid && id.ObjectFromId<Sim>() != null ? id.Value : 0uL;
            }
            catch (Exception)
            {
                return 0uL;
            }
        }

        private void OnStart(RouteAnimationSpec animSpec, RouteStartFlags flags)
        {
            if (mPrivateRequest != 0) RustLiveSimNavBuilder.Started(mPrivateRequest);
            try
            {
                if (!SacsRecorder.RingEnabled || !ServerHostMain.RouteStreamOn)
                {
                    return;
                }

                ulong sim = SimFollower();
                if (sim == 0uL)
                {
                    VehicleRecorder.Start(this);
                    return;
                }

                EndStoppedWalk(sim); // a stopped walk still open ends before the Sim's next walk starts

                RoutePlanResult plan = PlanResult;
                Sim follower = new ObjectGuid(sim).ObjectFromId<Sim>();
                Vector3 goal = plan.mDestination;
                if (!plan.Succeeded() || follower == null || (goal - follower.Position).Length() < RouteMinDistance)
                {
                    return;
                }

                CultureInfo c = CultureInfo.InvariantCulture;
                ulong destObj = 0uL;
                if (DestObj != null && DestObj.Proxy != null)
                {
                    destObj = DestObj.Proxy.ObjectId.Value;
                }

                mSim = sim;
                mSent = true;
                mStartPosition = follower.Position;
                mStartMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                mStartTicks = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
                mStopping = false;
                mStopMode = -1;
                mStopAccepted = false;
                mStopMs = 0L;
                Starts++;
                Open[sim] = new WeakReference(this);
                string line = "Rs|" + sim.ToString(c) + "|" + goal.x.ToString("F3", c) + "|" + goal.y.ToString("F3", c) + "|" + goal.z.ToString("F3", c)
                    + "|" + plan.mDestinationTag.ToString(c) + "|" + plan.mDestSlotNameHash.ToString(c) + "|" + destObj.ToString(c)
                    + "|" + ServerHostMain.RouteWaypointsForStream(this) + "|" + mStartMs.ToString(c)
                    + "|" + Spec(animSpec) + "|" + ((uint)flags).ToString(c) + "|" + Handle.ToString(c);
                SacsRecorder.RingLine(line);
                mLeg = RouteLegIndex.OnStart(mStartsOnRoute);
                mStartsOnRoute++;
                WatchNote("route start leg=" + mLeg.ToString(c) + " style=" + Spec(animSpec) + " paths=" + GetNumPaths().ToString(c));
                BoundaryState.Remember(line, 2); // the walk under way, for a late joiner, until it ends
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        public override bool Start(RouteAnimationSpec animSpec, RouteStartFlags routeStartFlags)
        {
            OnStart(animSpec, routeStartFlags);
            return base.Start(animSpec, routeStartFlags);
        }

        public override bool Start(RouteAnimationSpec animSpec, uint[] slaveAnimSpecsPacked, ulong[] slaveRoutingObjects, bool bSlavesRoute, RouteStartFlags routeStartFlags)
        {
            OnStart(animSpec, routeStartFlags);
            return base.Start(animSpec, slaveAnimSpecsPacked, slaveRoutingObjects, bSlavesRoute, routeStartFlags);
        }

        public override void UpdateRouteAnimationSpec(ObjectGuid simObjId, RouteAnimationSpec rSpec)
        {
            base.UpdateRouteAnimationSpec(simObjId, rSpec);
            OnStyle(rSpec);
        }

        public override void UpdateRouteAnimationSpec(ObjectGuid simObjId, RouteAnimationSpec rSpec, uint[] slaveAnimSpecsPacked, ulong[] slaveRoutingObjects)
        {
            base.UpdateRouteAnimationSpec(simObjId, rSpec, slaveAnimSpecsPacked, slaveRoutingObjects);
            OnStyle(rSpec);
        }

        private void OnStyle(RouteAnimationSpec spec)
        {
            try
            {
                if (mSent && SacsRecorder.RingEnabled)
                {
                    StyleChanges++;
                    WatchNote("route style leg=" + mLeg.ToString(CultureInfo.InvariantCulture) + " style=" + Spec(spec));
                    SacsRecorder.RingLine("Ru|" + mSim.ToString(CultureInfo.InvariantCulture) + "|" + Spec(spec) + "|" + Handle.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        public override bool Stop(StopMode stopMode)
        {
            float before = Remaining();
            bool stopped = base.Stop(stopMode);
            mRemainingBefore = before;
            mRemainingAfter = Remaining();
            OnStopCall(stopMode, stopped);
            return stopped;
        }

        // The route's distance left (Route.GetDistanceRemaining), -1 when it cannot say, sent around a Stop call. It does
        // not say where a stopped Sim rests: two-hud 2026-10-21 read 0.667 m before and after every Stop, also 0.29 m
        // into the 0.667 m walk (the planned length, it seems).
        private float Remaining()
        {
            try
            {
                return GetDistanceRemaining();
            }
            catch (Exception)
            {
                return -1f;
            }
        }

        private float mRemainingBefore = -1f;
        private float mRemainingAfter = -1f;

        // A Sim the game stops does not stand still at the call: two-hud 2026-10-21, the server's Pablo walked on 0.22 m
        // (twice) and 0.53 m to his goal after Stop, so an end line sent at the call put both copies where he never
        // stayed, and they were snapped back about 0.5 m. A stopped walk stays open (held here, so it is not collected
        // first) and PublishEnds sends its end where the Sim comes to rest, with the stop's mode, whether the route took
        // it, and when it came. A refused stop leaves the walk running; its end line still carries the call.
        // How far the Sim walks on depends on where in its walk the stop came (two-hud, a Soft stop from the HUD cancel's
        // exit reason, ScriptAdaptor.RouteExitReasonsAddedCallback): before the first step it played the start clip and
        // stopped 0.22 m on (three runs), after it the Sim walked on to the goal (three runs). So an accepted stop is sent
        // at the call too, for the copy to stop the same way at the same point of its own walk:
        //   Rh|sim|x|y|z|route|stopMs|mode|moved|leftBefore|leftAfter|sinceMs|sinceTicks   (moved: metres from where the walk
        //   began; left: GetDistanceRemaining just before and after the call; since: how far into the walk the call came,
        //   in ms and SimClock ticks, for the copy to stop at the same point of its own walk: WalkHaltTiming)
        private void OnStopCall(StopMode stopMode, bool stopped)
        {
            if (stopped) RetirePrivatePlan();
            try
            {
                if (!mSent)
                {
                    return;
                }

                StopCalls++;
                if (!stopped)
                {
                    StopsRefused++;
                }

                mStopMode = (int)stopMode;
                mStopAccepted = stopped;
                mStopMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                if (stopped)
                {
                    mStopping = true;
                    Stopping[mSim] = this;
                    if (SacsRecorder.RingEnabled)
                    {
                        Sim follower = new ObjectGuid(mSim).ObjectFromId<Sim>();
                        Vector3 at = follower == null ? mStartPosition : follower.Position;
                        CultureInfo c = CultureInfo.InvariantCulture;
                        SacsRecorder.RingLine("Rh|" + mSim.ToString(c) + "|" + at.x.ToString("F3", c) + "|" + at.y.ToString("F3", c) + "|" + at.z.ToString("F3", c)
                            + "|" + Handle.ToString(c) + "|" + mStopMs.ToString(c) + "|" + mStopMode.ToString(c) + "|" + (at - mStartPosition).Length().ToString("F3", c)
                            + "|" + mRemainingBefore.ToString("F3", c) + "|" + mRemainingAfter.ToString("F3", c)
                            + WalkHaltTiming.Fields(mStopMs - mStartMs, Sims3.Gameplay.Utilities.SimClock.CurrentTicks - mStartTicks));
                        WatchNote("route halt leg=" + mLeg.ToString(c) + " mode=" + mStopMode.ToString(c));
                    }
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        // The Sim's stopped walk, if one is still open, ends now (where the Sim is): before its next walk's start line.
        private static void EndStoppedWalk(ulong sim)
        {
            RouteRecorder stopped;
            if (Stopping.TryGetValue(sim, out stopped))
            {
                stopped.OnStopped("stop");
            }
        }

        // A walk that ended: stopped and at rest, or arrived (no Stop call). PublishEnds sees both; by: what ended it
        // (stop, inactive, arrived).
        internal void OnStopped(string by)
        {
            RetirePrivatePlan();
            try
            {
                RouteRecorder held;
                if (Stopping.TryGetValue(mSim, out held) && ReferenceEquals(held, this))
                {
                    Stopping.Remove(mSim);
                }

                if (!mSent || !SacsRecorder.RingEnabled)
                {
                    return;
                }

                mSent = false;
                mStopping = false;
                Stops++;
                BoundaryState.ForgetKey("Rs|" + mSim.ToString(CultureInfo.InvariantCulture));
                WeakReference open;
                if (Open.TryGetValue(mSim, out open) && ReferenceEquals(open.Target, this))
                {
                    Open.Remove(mSim);
                }

                Sim follower = new ObjectGuid(mSim).ObjectFromId<Sim>();
                Vector3 end = follower == null ? Vector3.Zero : follower.Position;
                Vector3 forward = follower == null ? Vector3.Zero : follower.ForwardVector;
                CultureInfo c = CultureInfo.InvariantCulture;
                // Rx|sim|x|y|z|route|ms|fx|fz|by|stopMode|stopAccepted|stopMs: where and facing which way the server's Sim
                // stopped, what ended it, and the walk's last Stop call (-1|0|0: none).
                SacsRecorder.RingLine("Rx|" + mSim.ToString(c) + "|" + end.x.ToString("F3", c) + "|" + end.y.ToString("F3", c) + "|" + end.z.ToString("F3", c)
                    + "|" + Handle.ToString(c) + "|" + (DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond).ToString(c)
                    + "|" + forward.x.ToString("F4", c) + "|" + forward.z.ToString("F4", c) + "|" + by
                    + "|" + mStopMode.ToString(c) + "|" + (mStopAccepted ? "1" : "0") + "|" + mStopMs.ToString(c));
                WatchNote("route end leg=" + mLeg.ToString(c) + " by=" + by);
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        // Stopped walks not yet at rest, by Sim (strong: a stopped route can be dropped by the game before its Sim rests).
        private static readonly Dictionary<ulong, RouteRecorder> Stopping = new Dictionary<ulong, RouteRecorder>();

        // Whether the Sim still has a route action (RoutingComponent.IsRouting): false once its FollowRouteAction finished.
        private static bool StillFollowing(ulong simId)
        {
            Sim sim = new ObjectGuid(simId).ObjectFromId<Sim>();
            RoutingComponent routing = sim == null ? null : sim.RoutingComponent;
            return routing == null || routing.IsRouting;
        }

        // Sims whose recorded walk is still open (Start sent, no end yet; a stopped walk until its Sim rests), by Sim.
        private static readonly Dictionary<ulong, WeakReference> Open = new Dictionary<ulong, WeakReference>();

        // Every push observe (~250 ms): a walk that arrived ends without a Stop call. It has ended when its route goes
        // inactive, or when its Sim no longer follows a route: the script's FollowRouteAction wakes on arrival, runs
        // PostRoute and is popped, while the native route can stay active for seconds (a dining chair: 4 s into the sit,
        // so a client held the sit behind a walk that was over and then played it 4 s late, compressed).
        // A route collected while open ends too.
        internal static void PublishEnds()
        {
            if (Open.Count == 0)
            {
                return;
            }

            List<RouteRecorder> ended = new List<RouteRecorder>();
            List<string> endedBy = new List<string>();
            List<ulong> lost = new List<ulong>();
            long nowMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            foreach (KeyValuePair<ulong, WeakReference> pair in Open)
            {
                RouteRecorder route = pair.Value.Target as RouteRecorder;
                if (route == null)
                {
                    lost.Add(pair.Key);
                    continue;
                }

                string by = null;
                try
                {
                    if (route.mStopping)
                    {
                        // Stopped: at rest once the Sim no longer routes (the motion watch's walking flag drops then,
                        // not at the Stop call; the native route's own active flag is not trusted to say when it moved
                        // last). One still routing long after the Stop ends where it is.
                        if (!StillFollowing(pair.Key) || nowMs - route.mStopMs > StopRestCapMs)
                        {
                            by = "stop";
                        }
                    }
                    else if (!route.IsActive)
                    {
                        by = "inactive";
                    }
                    else if (!StillFollowing(pair.Key))
                    {
                        by = "arrived";
                    }
                }
                catch (Exception)
                {
                    by = "inactive";
                }

                if (by != null)
                {
                    ended.Add(route);
                    endedBy.Add(by);
                }
            }

            for (int i = 0; i < ended.Count; i++)
            {
                ended[i].OnStopped(endedBy[i]);
            }

            for (int i = 0; i < lost.Count; i++)
            {
                Open.Remove(lost[i]);
            }
        }
    }
}
