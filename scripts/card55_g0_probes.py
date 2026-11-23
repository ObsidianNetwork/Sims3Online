"""Authored diagnostic templates, compiled with check_mod_runtime.runtime_args.

Only generated sources/DLLs go under .omo/run/act/. These call the public engine
API; they contain no decompiled implementation or captured engine data.
"""

COMMON = r'''
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Routing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

public partial class G0Probe {
    private static string report = "done=true;verdict=NOT_STARTED;cleanup=complete";
    private static bool busy;
    public static string Report() { return report; }
    private static Sim ActorFor(string name, string lot) {
        Sim found = null;
        foreach (Sim sim in LotManager.Actors) {
            if (sim == null || (sim.FirstName != name && sim.FullName != name)) continue;
            if (found != null) return null;
            found = sim;
        }
        if (found == null || found.LotCurrent == null) return null;
        bool matchesLot = lot == "world" ? found.LotCurrent.IsWorldLot :
            found.LotCurrent.LotId.ToString(CultureInfo.InvariantCulture) == lot;
        if (!matchesLot ||
            found.InteractionQueue == null || found.InteractionQueue.Count != 0 ||
            found.RoutingComponent.IsRouting || found.Posture != found.Standing ||
            found.SimDescription == null || !found.IsHuman || !Finite(found.Position)) return null;
        return found;
    }
    private static bool Finite(Vector3 p) {
        return !Single.IsNaN(p.x) && !Single.IsNaN(p.y) && !Single.IsNaN(p.z) &&
            !Single.IsInfinity(p.x) && !Single.IsInfinity(p.y) && !Single.IsInfinity(p.z) &&
            p != Vector3.Invalid;
    }
    private static double Distance(Vector3 a, Vector3 b) {
        double x = a.x-b.x, y = a.y-b.y, z = a.z-b.z;
        return Math.Sqrt(x*x+y*y+z*z);
    }
    private static string Mm(double metres) {
        return metres < 0 ? "-1" : Math.Ceiling(metres*1000).ToString("F0", CultureInfo.InvariantCulture);
    }
    private static string Refuse(string verdict) {
        return "done=true;verdict=" + verdict + ";cleanup=complete";
    }
}
'''

PORTAL = r'''
public partial class G0Probe {
    public static string Begin(string name, string lot, string kind, int ordinal, int slot,
        float seconds, string mode) {
        if (busy) return "done=false;verdict=BUSY";
        try {
            Sim actor = ActorFor(name, lot);
            if (actor == null) return Refuse("ACTOR_LOT_OR_IDLE_MISMATCH");
            List<GameObject> portals = new List<GameObject>();
            foreach (GameObject obj in actor.LotCurrent.GetObjects<GameObject>())
                if (obj != null && ((kind == "door" && obj is Door) || (kind == "stairs" && obj is Stairs)))
                    portals.Add(obj);
            portals.Sort(delegate(GameObject a, GameObject b) { return a.ObjectId.Value.CompareTo(b.ObjectId.Value); });
            if (mode == "shape") return Shape(actor, portals, kind);
            if (ordinal >= portals.Count) return Refuse("PORTAL_NOT_FOUND");
            GameObject target = portals[ordinal];
            PortalComponent component = target.PortalComponent;
            if (component == null) return Refuse("NO_PORTAL_COMPONENT");
            bool preferred = false;
            List<Slot> starts = component.GetStartSlotsForApproachingSim(actor, actor.Position, ref preferred);
            if (starts == null || slot >= starts.Count) return Refuse("START_SLOT_NOT_FOUND");
            Slot entrySlot = starts[slot], exitSlot = component.GetPairedSlot(entrySlot);
            if ((uint)exitSlot == 0u) return Refuse("NO_PAIRED_SLOT");
            Vector3 entry = target.GetPositionOfSlot(entrySlot, actor.SimDescription.AgeGenderSpecies, false);
            Vector3 exit = target.GetPositionOfSlot(exitSlot, actor.SimDescription.AgeGenderSpecies, true);
            if (!Finite(entry) || !Finite(exit)) return Refuse("INVALID_ENGINE_ENDPOINT");
            if (mode != "prepare" && Distance(actor.Position, entry) > 0.10)
                return Refuse("PREPARE_ENTRY_OUTSIDE_INTERVAL") + ";entryErrorMm=" + Mm(Distance(actor.Position, entry));
            Traverse action = new PortalDefinition().CreateInstance(target, actor,
                new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true) as Traverse;
            if (action == null) return Refuse("NO_INTERACTION");
            action.Entry = entry; action.Exit = exit; action.Seconds = seconds;
            action.Stairs = kind == "stairs"; action.Prepare = mode == "prepare";
            busy = true; report = "done=false;verdict=QUEUED";
            if (!actor.InteractionQueue.Add(action)) { busy = false; report = Refuse("QUEUE_REFUSED"); }
            return report;
        } catch (ResetException) { throw; }
        catch (Exception) { busy = false; return Refuse("SETUP_EXCEPTION"); }
    }
    private static string Shape(Sim actor, List<GameObject> portals, string kind) {
        int attempted=0, inserted=0, grown=0, custom=0, refused=0, exceptions=0;
        foreach (GameObject obj in portals) {
            if (attempted >= 8) break;
            attempted++; Route route = null;
            try {
                route = actor.CreateRoute(); route.ReplanAllowed = false;
                if (!route.InsertCustomPathAtIndex(0u, new Vector3[] {actor.Position, obj.Position},
                    false, true, PathType.SimPath)) { custom++; continue; }
                uint before = route.GetNumPaths();
                if (route.InsertPortalSubPathAtIndex(before, obj.ObjectId)) {
                    inserted++;
                    if (route.GetNumPaths() > before) grown++;
                } else refused++;
            } catch (ResetException) { throw; }
            catch (Exception) { exceptions++; }
            finally { if (route != null) route.Dispose(); }
        }
        string verdict = kind == "stairs" && refused > 0 ? "UNSUPPORTED_STAIR_INSERT_REFUSED" :
            kind == "stairs" && grown != inserted ? "UNSUPPORTED_STAIR_TYPED_PATH_SHAPE" : "STRUCTURAL_ONLY";
        return Refuse(verdict) + ";structuralOnly=true;acceptance=false;attempted=" + attempted +
            ";inserted=" + inserted + ";pathsAdded=" + grown + ";customFailures=" + custom +
            ";portalFailures=" + refused + ";exceptions=" + exceptions;
    }
    public class PortalDefinition : InteractionDefinition<Sim, GameObject, Traverse> {
        protected override string GetInteractionName(Sim a, GameObject t, InteractionObjectPair p) { return "G0 portal diagnostic"; }
        protected override bool Test(Sim a, GameObject t, bool autonomous, ref GreyedOutTooltipCallback cb) {
            return !autonomous && t != null && t.PortalComponent != null;
        }
    }
    public class Traverse : Interaction<Sim, GameObject> {
        public Vector3 Entry, Exit;
        public float Seconds;
        public bool Stairs, Prepare;
        private Route route;
        private bool timedOut;
        private int events, through, traversal, animate, other;
        private RouteAction last;
        private void Observe(RouteEvent ev) {
            try {
                if (Actor.RoutingComponent.GetCurrentRoute() != route) return;
                events++;
                RouteAction action = Actor.RoutingComponent.GetCurrentRouteAction();
                if (action == null || action == last) return;
                last = action;
                if (action is PortalRouteThroughAction) through++;
                else if (action is PortalTraversalAction) traversal++;
                else if (action is PortalAnimateThroughAction) animate++;
                else other++;
            } catch (ResetException) { throw; }
            catch (Exception) { other++; }
        }
        private void Abort() {
            timedOut = true;
            Actor.AddExitReason(ExitReason.CanceledByScript);
            if (route != null) route.Stop(StopMode.kStop_Hard);
            Actor.RoutingComponent.KillRoute(false);
        }
        protected override bool Run() {
            ObjectGuid timer = ObjectGuid.InvalidObjectGuid;
            bool subscribed=false, returned=false, result=false;
            int sim=0, portal=0, traverse=0, unknown=0;
            uint replans=0; long start=0, end=0;
            double error=-1, insertedExit=-1;
            string verdict="EXECUTION_EXCEPTION", cleanup="required";
            try {
                if (Simulator.CurrentTask != Actor.ObjectId) { verdict="UNSAFE_TASK_CONTEXT"; return false; }
                if ((!Prepare && Distance(Actor.Position, Entry)>0.10) || Actor.RoutingComponent.IsRouting) {
                    verdict="ENTRY_CHANGED"; return false;
                }
                route=Actor.CreateRoute(); route.ReplanAllowed=false; route.DoRouteFail=false;
                if (Prepare) {
                    if (!route.PlanToPoint(Entry).Succeeded()) { verdict="PREPARATION_PLAN_REFUSED"; return false; }
                    timer=Simulator.AddObject(new OneShotFunctionTask(Abort, StopWatch.TickStyles.Seconds, Seconds));
                    result=Actor.DoRoute(route); returned=true; error=Distance(Actor.Position, Entry);
                    verdict=timedOut ? "PREPARATION_TIMEOUT" : result && error<=0.10 ?
                        "PREPARATION_COMPLETE" : "PREPARATION_FAILED";
                    return result && !timedOut;
                }
                if (!route.InsertCustomPathAtIndex(0u, new Vector3[] {Actor.Position, Entry}, false, false, PathType.SimPath)) {
                    verdict="CUSTOM_INSERT_REFUSED"; return false;
                }
                uint before=route.GetNumPaths();
                if (!route.InsertPortalSubPathAtIndex(before, Target.ObjectId)) {
                    verdict=Stairs ? "UNSUPPORTED_STAIR_INSERT_REFUSED" : "PORTAL_INSERT_REFUSED"; return false;
                }
                uint count=route.GetNumPaths();
                for (uint i=0; i<count; i++) {
                    PathData data=route.GetPathData(i);
                    if (data.PathType==PathType.SimPath) sim++;
                    else if (data.PathType==PathType.PortalPath && data.ObjectId==Target.ObjectId) portal++;
                    else if (data.PathType==PathType.PortalTraversalPath && data.ObjectId==Target.ObjectId) traverse++;
                    else unknown++;
                }
                // Native DoPortal can materialize the traversal phase during execution.
                if (count<=before || portal==0 || unknown!=0) {
                    verdict=Stairs ? "UNSUPPORTED_STAIR_TYPED_PATH_SHAPE" : "UNSUPPORTED_TYPED_PATH_SHAPE"; return false;
                }
                Vector3 nativeEntry=Vector3.Invalid, nativeExit=Vector3.Invalid;
                route.GetPathStartPoint(before, ref nativeEntry);
                if (!Finite(nativeEntry) || Distance(nativeEntry, Entry)>0.10) { verdict="INSERTED_ENTRY_MISMATCH"; return false; }
                route.GetPathEndPoint(count-1u, ref nativeExit);
                if (Finite(nativeExit)) insertedExit=Distance(nativeExit, Exit);
                if (traverse>0 && (insertedExit<0 || insertedExit>0.10)) { verdict="INSERTED_EXIT_MISMATCH"; return false; }
                if (!route.InsertCustomPathAtIndex(count, new Vector3[] {Exit, Exit}, false, true, PathType.SimPath)) {
                    verdict="EXIT_INSERT_REFUSED"; return false;
                }
                RoutePlanResult plan=route.PlanResult; plan.mType=RoutePlanResultType.Succeeded; route.PlanResult=plan;
                Actor.RoutingComponent.OnRoutingEvent+=Observe; subscribed=true;
                timer=Simulator.AddObject(new OneShotFunctionTask(Abort, StopWatch.TickStyles.Seconds, Seconds));
                start=SimClock.CurrentTicks; result=Actor.DoRoute(route); end=SimClock.CurrentTicks; returned=true;
                error=Distance(Actor.Position, Exit); replans=route.NumRouteFailReplans;
                verdict=timedOut ? "TIMEOUT" : !result ? "DO_ROUTE_FALSE" : error>0.10 ? "EXIT_MISMATCH" :
                    traversal+animate==0 ? "COMPLETED_WITHOUT_TRAVERSAL_OBSERVATION" : "NATIVE_COMPLETION_OBSERVED";
                return result && !timedOut;
            } catch (ResetException) { verdict="RESET_ABORT"; throw; }
            catch (Exception) { verdict="EXECUTION_EXCEPTION"; return false; }
            finally {
                try {
                    try { if (subscribed) Actor.RoutingComponent.OnRoutingEvent-=Observe; }
                    finally {
                        try { if (!timedOut && timer!=ObjectGuid.InvalidObjectGuid) Simulator.DestroyObject(timer); }
                        finally { if (route!=null) route.Dispose(); }
                    }
                    cleanup="complete";
                } finally {
                    route=null; Entry=Vector3.Zero; Exit=Vector3.Zero; busy=false;
                    report="done=true;verdict="+verdict+";cleanup="+cleanup+
                        ";acceptance=false;teleportExcluded=false;doRouteReturned="+returned+";doRouteResult="+result+
                        ";timeout="+timedOut+";simPaths="+sim+";portalPaths="+portal+";traversalPaths="+traverse+
                        ";unknownPaths="+unknown+";events="+events+";throughActions="+through+";traversalActions="+traversal+
                        ";animateActions="+animate+";otherActions="+other+";replans="+replans+
                        ";insertedExitErrorMm="+Mm(insertedExit)+";exitErrorMm="+Mm(error)+";startTick="+start+";endTick="+end;
                }
            }
        }
    }
}
'''

ROAD = r'''
public partial class G0Probe {
    public static string Begin(string name, string lot, int selector, int calls, float distance) {
        if (busy) return "done=false;verdict=BUSY";
        try {
            Sim actor=ActorFor(name, lot);
            if (actor==null) return Refuse("ACTOR_LOT_OR_IDLE_MISMATCH");
            RoadCall action=new RoadDefinition().CreateInstance(actor, actor,
                new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true) as RoadCall;
            if (action==null) return Refuse("NO_INTERACTION");
            action.Selector=selector; action.Calls=calls; action.Distance=distance;
            busy=true; report="done=false;verdict=QUEUED";
            if (!actor.InteractionQueue.Add(action)) { busy=false; report=Refuse("QUEUE_REFUSED"); }
            return report;
        } catch (ResetException) { throw; }
        catch (Exception) { busy=false; return Refuse("SETUP_EXCEPTION"); }
    }
    public class RoadDefinition : InteractionDefinition<Sim, Sim, RoadCall> {
        protected override string GetInteractionName(Sim a, Sim t, InteractionObjectPair p) { return "G0 road diagnostic"; }
        protected override bool Test(Sim a, Sim t, bool autonomous, ref GreyedOutTooltipCallback cb) { return !autonomous; }
    }
    public class RoadCall : Interaction<Sim, Sim> {
        public int Selector, Calls;
        public float Distance;
        protected override bool Run() {
            int calls=0, found=0; string verdict="EXECUTION_EXCEPTION";
            bool yielding=false;
            try {
                // The IWorld replacement yields; never call it from DIAG_RUN's pump task.
                yielding=Simulator.CurrentTask==Actor.ObjectId && Simulator.CheckYieldingContext(false);
                if (!yielding) { verdict="NOT_YIELDING_ACTOR_TASK"; return false; }
                for (int i=0; i<Calls; i++) {
                    Vector3[] positions; Quaternion[] orientations;
                    bool result=World.FindPlaceOnRoadOffScreen(null, Actor.Position,
                        (FindPlaceOnRoadOption)Selector, Distance, out positions, out orientations);
                    calls++;
                    if (result) found++;
                    // Arrays remain local; neither their values nor ToString output is returned.
                }
                verdict="ROAD_CALLS_COMPLETE"; return true;
            } catch (ResetException) { verdict="RESET_ABORT"; throw; }
            catch (Exception) { return false; }
            finally {
                busy=false;
                report=Refuse(verdict)+";yielding="+yielding.ToString().ToLowerInvariant()+
                    ";calls="+calls+";found="+found+";notFound="+(calls-found)+";selector="+Selector+";acceptance=false";
            }
        }
    }
}
'''

CAPTURE = r'''
public partial class G0Probe {
    private static object Field(object obj, string name) {
        return obj.GetType().GetField(name, BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetValue(obj);
    }
    public static string Capture(string name, string lot, bool saveSpot) {
        try {
            Sim actor=ActorFor(name, lot);
            if (actor==null) return Refuse("ACTOR_LOT_OR_IDLE_MISMATCH");
            Type builder=null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                Type type=assembly.GetType("SimsOnlineServerHost.RustLiveSimNavBuilder", false);
                if (type!=null) { if (builder!=null) return Refuse("AMBIGUOUS_BUILDER"); builder=type; }
            }
            if (builder==null) return Refuse("BUILDER_UNAVAILABLE");
            BindingFlags flags=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
            if (!(bool)builder.GetField("sReady", flags).GetValue(null)) return Refuse("SNAPSHOT_NOT_INSTALLED");
            object snapshot=builder.GetField("sSnapshot", flags).GetValue(null);
            object sync=builder.GetField("Sync", flags).GetValue(null);
            object identity=builder.GetMethod("Identity", flags).Invoke(null, new object[] {actor.Position});
            int[] cm=(int[])builder.GetMethod("Cm", flags).Invoke(null, new object[] {actor.Position});
            object match=null;
            foreach (object cell in (IEnumerable)Field(snapshot, "Cells")) {
                if (!Field(cell,"Lot").Equals(Field(identity,"Lot")) || !Field(cell,"Room").Equals(Field(identity,"Room")) ||
                    !Field(cell,"Level").Equals(Field(identity,"Level"))) continue;
                bool contains=(bool)snapshot.GetType().GetMethod("Contains").Invoke(null, new object[] {cell, cm});
                if (!contains) continue;
                if (match!=null) return Refuse("AMBIGUOUS_MEASURED_CELL");
                match=cell;
            }
            if (match==null) return Refuse("NO_EXACT_MEASURED_CELL");
            CultureInfo c=CultureInfo.InvariantCulture;
            string result=Refuse("CAPTURED")+";content_hash="+((ulong)Field(snapshot,"ContentHash")).ToString("x16",c)+
                ";cell="+Field(match,"Id")+";lot="+Field(match,"Lot")+";room="+Field(match,"Room")+
                ";level="+Field(match,"Level")+";actor="+actor.ObjectId.Value+
                ";epoch="+Field(sync,"Epoch")+";generation="+Field(sync,"Generation");
            if (saveSpot) {
                Vector3 position=actor.Position, forward=actor.ForwardVector;
                result+=";spot="+position.x.ToString("R",c)+","+position.z.ToString("R",c)+","+
                    forward.x.ToString("R",c)+","+forward.z.ToString("R",c);
            }
            return result;
        } catch (ResetException) { throw; }
        catch (Exception) { return Refuse("CAPTURE_UNAVAILABLE"); }
    }
}
'''
