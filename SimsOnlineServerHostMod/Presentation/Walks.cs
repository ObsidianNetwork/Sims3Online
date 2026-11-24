using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Routing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.Enums;
using SimsOnlineServerHost;

namespace SimsOnline.Presentation
{
    // Walks and places on a client (moved from the shell, ServerHostMain, so they reload with
    // scripts/presentation_reload.py): the route stream (Rt / Rs: a walk on the server's path; Rte / Rx: where it ended;
    // Ru: its walk style) walks this client's copy, and the position stream (Pz) corrects the copy's place and facing
    // once it and the server's Sim are at rest. The shell keeps the stream lines held behind a walk (it asks State) and
    // the posture stream (Po); Tick runs on every replay batch.
    internal static class Walks
    {
        private const float PositionStep = 0.05f;
        private const float ForwardStepCos = 0.99f;

        private static float FacingCos(Vector3 a, Vector3 b)
        {
            float la = (float)Math.Sqrt(a.x * a.x + a.z * a.z);
            float lb = (float)Math.Sqrt(b.x * b.x + b.z * b.z);
            if (la < 0.0001f || lb < 0.0001f)
            {
                return 1f;
            }

            return (a.x * b.x + a.z * b.z) / (la * lb);
        }

        // PresentationModules.OnWalkState: a client walk runs (its lines wait); it holds the copy's place (until
        // ClientRouteMaxHold); the copy moves on it now (not stopping).
        internal static int State(ulong localSim)
        {
            if (Vehicles.HoldsPosition(localSim))
            {
                return 0; // Vehicle ownership never holds animation/posture lines behind a walk.
            }
            ClientRoute entry;
            if (!sClientRoutes.TryGetValue(localSim, out entry) || !entry.Running)
            {
                return 0;
            }

            int state = PresentationModules.WalkRunning;
            if (DateTime.UtcNow - entry.StartedUtc <= ClientRouteMaxHold)
            {
                state |= PresentationModules.WalkHoldsPlace;
            }

            if (entry.Current != null && !entry.StopRequested)
            {
                state |= PresentationModules.WalkMoving;
            }

            return state;
        }

        private static bool IsWalking(ulong localSim)
        {
            ClientRoute entry;
            return sClientRoutes.TryGetValue(localSim, out entry) && entry.Running;
        }

        internal static void Tick()
        {
            ExpireClientRoutes();
            SettleMirror();
        }

        // A reload or a new session: walks that run stop, and the mirror forgets its targets.
        internal static void Reset()
        {
            // Their runners still return normally and release what the walk held (a module swap that keeps the
            // presentation: the copy's waiting lines play; after a presentation reset the shell has dropped them).
            foreach (KeyValuePair<ulong, ClientRoute> pair in sClientRoutes)
            {
                ClientRoute entry = pair.Value;
                if (entry.Running && entry.Current != null)
                {
                    StopClientRoute(entry);
                }

                UnrequestServerStyle(new ObjectGuid(pair.Key).ObjectFromId<Sim>(), entry);
            }

            sClientRoutes.Clear();
            sMirrorTargets.Clear();
            sVehiclePendingRoutes.Clear();
        }

        internal static string Report()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "positions=applied:" + sStreamPositionApplies.ToString(c) + ",deadband:" + sStreamPositionDeadband.ToString(c)
                + ",postured:" + sStreamPositionPostured.ToString(c) + ",postureSnaps:" + sStreamPositionPostureSnaps.ToString(c)
                + ",lastPostureSnap:" + sLastPostureSnap + ",facings:" + sStreamFacings.ToString(c) + ",facingsSettled:" + sFacingsSettled.ToString(c)
                + ",mirrorWaits:" + sMirrorWaits.ToString(c) + ",mirrorWalks:" + sMirrorWalks.ToString(c) + ",mirrorTurns:" + sMirrorTurns.ToString(c) + ",mirrorAnimHolds:" + sMirrorAnimHolds.ToString(c)
                + ";join=settles:" + sJoinSettles.ToString(c) + ",inPlace:" + sJoinInPlace.ToString(c) + ",standing:" + sJoinStanding.ToString(c) + ",seatsLeft:" + sJoinSeatsLeft.ToString(c) + ",waits:" + sJoinWaits.ToString(c) + ",last:" + sLastJoinSettle
                + ";routes=received:" + sRoutesReceived.ToString(c) + ",started:" + sRoutesStarted.ToString(c) + ",planFailed:" + sRoutesPlanFailed.ToString(c)
                + ",fromPath:" + sRoutesFromPath.ToString(c) + ",pathOffGoal:" + sRoutesPathOffGoal.ToString(c) + ",chained:" + sRoutesChained.ToString(c) + ",legs:" + sRoutesLegs.ToString(c)
                + ",lagAvgMs:" + (sRouteLagCount == 0 ? 0 : sRouteLagMsSum / sRouteLagCount).ToString(c) + ",lagMaxMs:" + sRouteLagMsMax.ToString(c)
                + ",lagMaxAt:" + sRouteLagMaxAt
                + ",pathFails:" + (sRouteFailDetails.Length == 0 ? "-" : sRouteFailDetails.Replace(',', '/'))
                + ",done:" + sRoutesDone.ToString(c) + ",failed:" + sRoutesFailed.ToString(c) + ",exceptions:" + sRoutesExceptions.ToString(c)
                + ",held:" + sRoutesHeldApplies.ToString(c) + ",snaps:" + sRoutesSnaps.ToString(c) + ",lastSnap:" + sLastSnap + ",toSlot:" + sRoutesFacings.ToString(c) + ",slotOffGoal:" + sRoutesSlotOffGoal.ToString(c) + ",stopped:" + sRoutesStopped.ToString(c)
                + ",overruns:" + sRoutesOverrun.ToString(c) + ",halts:" + sRoutesHalts.ToString(c) + "/" + sRoutesHaltsReceived.ToString(c)
                + ",haltsTimed:" + sRoutesHaltsTimed.ToString(c) + ",haltsHeld:" + sRoutesHaltsHeld.ToString(c) + ",phaseSplits:" + sRoutesPhaseSplits.ToString(c)
                + ",haltLate:" + (sRoutesHaltsTimed == 0 ? "-" : sHaltLateMin.ToString(c) + "/" + sHaltLateMax.ToString(c)) + (HaltClockTicks ? "t" : "ms")
                + ",lead:" + StartLeadMs.ToString(c) + "ms/" + sRoutesLeadWaits.ToString(c) + ",wedged:" + sRoutesWedged.ToString(c) + ",lateReturns:" + sRoutesLateReturns.ToString(c)
                + ",stylesApplied:" + sRouteStylesApplied.ToString(c) + ",legStyles:" + sRouteLegStyles.ToString(c) + ",styleRequests:" + sRouteStyleRequests.ToString(c) + ",legArms:" + sRouteLegArms.ToString(c) + ",puppetRoutes:" + sPuppetRoutesConfigured.ToString(c) + ",puppetIgnores:" + sPuppetRouteIgnores.ToString(c)
                + ",lastError:" + sRoutesLastError
                // This game's sim clock against the wall clock (two reads give its rate: below real time, walks and clips
                // run slow and a walking Sim moves only on frames that got a tick).
                + ";clock=ticks:" + SimClock.CurrentTicks.ToString(c) + ",utcMs:" + (DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond).ToString(c);
        }

        // Client side of Pz: the copy of a streamed Sim goes to the server's position when it is more than
        // StreamPositionDeadband away (smaller corrections are jitter), once both are at rest (SettleMirror). Each line
        // also tells the shell (MarkStreamPositioned) to keep its slower snapshot position mirror off this Sim.
        private static float StreamPositionDeadband { get { return Tunables.Get("mirror.deadband", 0.25f); } }
        private static long sStreamPositionApplies;
        private static long sStreamPositionDeadband;
        private static long sStreamPositionPostured;
        private static long sStreamPositionPostureSnaps;
        private static long sStreamFacings;
        private static string sLastPostureSnap = "none";
        // In a posture on an object (a chair, a bed, a stool) the copy's place comes from the replayed enter animation
        // and the seat settle, not from the Sim's own position: snapping it there after the enter clip moved copies out
        // of bed (Pablo, 2026-10-14). Only a copy this far away (it never took the posture) still goes there.
        // STOPGAP(boundary-recording): posture-snap a distance stands in for "the copy never took the posture"; place it from the posture's slot instead.
        private static float PostureSnapDistance { get { return Tunables.Get("mirror.posture-snap", 1.5f); } }
        private static float FacingApplyCos { get { return Tunables.Get("mirror.facing-cos", 0.985f); } }

        internal static void ForgetMirror(ulong localSim)
        {
            sMirrorTargets.Remove(localSim);
        }

        internal static bool ReplayStreamPosition(string[] f)
        {
            if (f.Length < 6)
            {
                return false;
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            ObjectGuid simId = PresentationApi.ReplayObject(ulong.Parse(f[2], c));
            if (!simId.IsValid)
            {
                return false;
            }

            if (Vehicles.HoldsPosition(simId.Value))
            {
                sMirrorTargets.Remove(simId.Value);
                return true;
            }

            Sim sim = simId.ObjectFromId<Sim>();
            if (sim == null)
            {
                return false;
            }

            PresentationApi.MarkStreamPositioned(simId.Value);
            Vector3 target = new Vector3(float.Parse(f[3], c), float.Parse(f[4], c), float.Parse(f[5], c));
            // The server's Sim's newest place and facing, always (also mid-walk: a target left from before a walk would pull
            // the copy back to where the walk began). SettleMirror applies them once both are at rest.
            MirrorTarget pending;
            if (!sMirrorTargets.TryGetValue(simId.Value, out pending))
            {
                pending = new MirrorTarget();
                sMirrorTargets[simId.Value] = pending;
            }

            if (!pending.HasPosition || (pending.Position - target).Length() >= PositionStep)
            {
                pending.ServerMovedUtc = DateTime.UtcNow;
            }

            pending.Position = target;
            pending.HasPosition = true;
            if (f.Length > 8)
            {
                Vector3 forward = new Vector3(float.Parse(f[6], c), 0f, float.Parse(f[8], c));
                if (forward.x * forward.x + forward.z * forward.z > 0.01f)
                {
                    if (!pending.HasForward || FacingCos(pending.Forward, forward) < ForwardStepCos)
                    {
                        pending.ServerMovedUtc = DateTime.UtcNow;
                    }

                    pending.Forward = forward;
                    pending.HasForward = true;
                }
            }

            // A client route (route stream) walks this Sim to the server's goal; its end puts the copy where the server's
            // walk ended (ReleaseRouteHold).
            if (IsRouteHeldCore(simId.Value, target))
            {
                return true;
            }

            float distance = (sim.Position - target).Length();
            if (PresentationApi.ServerSimOnObject(simId.Value))
            {
                if (PresentationApi.ServerSimRecentlyPostured(simId.Value) || distance <= PostureSnapDistance)
                {
                    sStreamPositionPostured++;
                    return true;
                }

                string postureContext = PresentationApi.LocalContext;
                PresentationApi.LocalContext = "mirror posture snap";
                sim.SetPosition(target);
                // The facing too: a late joiner's history replays the get-in clip from the pose the join already placed
                // (sleep-bed, client 2, 2026-10-21: one 2.43 m snap, the copy's root left facing the other way; the body in
                // bed looked right, the root shows when the Sim gets up).
                if (pending.HasForward)
                {
                    sim.SetForward(pending.Forward);
                }

                PresentationApi.LocalContext = postureContext;
                sStreamPositionPostureSnaps++;
                sLastPostureSnap = (sim.FullName ?? "?").Replace(' ', '_') + ":" + distance.ToString("F2", c) + "m";
                return true;
            }

            if (!PresentationApi.ServerSimStanding(simId.Value) && PresentationApi.ServerSimRecentlyPostured(simId.Value))
            {
                sStreamPositionPostured++;
                return true;
            }

            if (distance <= StreamPositionDeadband)
            {
                sStreamPositionDeadband++;
            }

            return true;
        }

        // The position mirror's corrections (place: standing; facing: standing, or in a posture without an object, a
        // conversation; seated and lying copies sit and face the way their seat settle and animation put them) wait
        // until a difference lasts MirrorSettle with the copy and the server's Sim both at rest: the server's Sim sent
        // no new place or facing, the copy is not walking and nothing else moved or turned it (its own animation's root
        // motion). The copy's animations move it as the server's moved the Sim; corrected straight away, sitting down
        // after a walk pulled the copy 0.27 m and turned it 101 degrees mid-clip (motion watch, sit-dining, 2026-10-14),
        // and a social's turn snapped round and back every second.
        private sealed class MirrorTarget
        {
            public bool HasPosition;
            public Vector3 Position;
            public bool HasForward;
            public Vector3 Forward;
            public DateTime ServerMovedUtc = DateTime.MinValue;
            // The copy's place and facing when it was last seen moving, and when.
            public Vector3 CopyPosition;
            public Vector3 CopyForward;
            public DateTime CopyMovedUtc = DateTime.MinValue;
            // The mirror is waiting for the stream's interaction clip on the copy to end (mirror.anim-hold).
            public bool AnimHeld;
            // The late-join reset (PresentationApi.JoinResetUtc) the join settle has dealt with; an older one than the
            // shell's newest means the copy's join history has not been settled yet.
            public DateTime JoinSettledUtc = DateTime.MinValue;
            // The newest join reset this target has seen, and the simulator's ticks then. The join settle waits until
            // the game has run mirror.join-ticks since, so the history's clips have played: a joiner's game can sit
            // paused or not yet running while wall time passes (late-join-seated on client 2, 2026-10-21: its simulator
            // ticks stayed 0 for 19 s after the server resumed, the settle found the copy in place, and the replayed
            // sit-down then turned it 150 degrees once the game ran).
            public DateTime JoinSeenUtc = DateTime.MinValue;
            public long JoinSeenTicks;
        }

        private static readonly Dictionary<ulong, MirrorTarget> sMirrorTargets = new Dictionary<ulong, MirrorTarget>();
        private static TimeSpan MirrorSettle { get { return Tunables.Seconds("mirror.settle", 1.5); } }
        private static float MirrorCopyStill { get { return Tunables.Get("mirror.copy-still", 0.05f); } }
        private static long sFacingsSettled;
        private static long sMirrorWaits;
        private static long sMirrorWalks;
        private static long sMirrorTurns;
        // How long after the stream last moved one of the copy's interaction machines the mirror leaves the copy to its clip.
        private static TimeSpan MirrorAnimHold { get { return Tunables.Seconds("mirror.anim-hold", 3.0); } }
        private static long sMirrorAnimHolds;
        // A standing copy this far from the server's Sim walks there; nearer it is placed, farther it jumps.
        private static float MirrorWalkMin { get { return Tunables.Get("mirror.walk-min", 0.5f); } }
        private static float MirrorWalkMax { get { return Tunables.Get("mirror.walk-max", 6f); } }
        // A mirror walk may take distance / speed plus slack before WalkOverrun stops it.
        private static double MirrorWalkSpeed { get { return Tunables.Get("mirror.walk-speed", 1.0); } }
        private static double MirrorWalkSlack { get { return Tunables.Get("mirror.walk-slack", 2.0); } }
        private static bool MirrorTurns { get { return Tunables.Get("mirror.turn", 1.0) > 0.5; } }
        // Simulator ticks (about 30 a second at normal speed) the game must run after a join reset before the join settle.
        private static long MirrorJoinTicks { get { return (long)Tunables.Get("mirror.join-ticks", 150f); } }


        // Every replay batch.
        // STOPGAP(boundary-recording): mirror-settle drift correction (1.5 s at rest, 0.25 m; clip guesses anim-hold 3 s, join-ticks 150); the transform and walk boundaries should leave it nothing to correct.
        internal static void SettleMirror()
        {
            if (sMirrorTargets.Count == 0)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            List<ulong> gone = null;
            foreach (KeyValuePair<ulong, MirrorTarget> pair in sMirrorTargets)
            {
                if (Vehicles.HoldsPosition(pair.Key))
                {
                    (gone = gone ?? new List<ulong>()).Add(pair.Key);
                    continue;
                }

                MirrorTarget pending = pair.Value;
                try
                {
                    Sim sim = new ObjectGuid(pair.Key).ObjectFromId<Sim>();
                    if (sim == null || sim.HasBeenDestroyed)
                    {
                        (gone = gone ?? new List<ulong>()).Add(pair.Key);
                        continue;
                    }

                    DateTime joined = PresentationApi.JoinResetUtc(pair.Key);
                    long ticks = Simulator.TicksElapsed();
                    if (joined > pending.JoinSeenUtc)
                    {
                        pending.JoinSeenUtc = joined;
                        pending.JoinSeenTicks = ticks;
                    }

                    Vector3 position = sim.Position;
                    Vector3 forward = sim.ForwardVector;
                    if (IsWalking(pair.Key) || sim.IsRouting || (position - pending.CopyPosition).Length() > MirrorCopyStill
                        || FacingCos(forward, pending.CopyForward) < ForwardStepCos)
                    {
                        pending.CopyPosition = position;
                        pending.CopyForward = forward;
                        pending.CopyMovedUtc = now;
                        continue;
                    }

                    bool placeOff = pending.HasPosition && (position - pending.Position).Length() > StreamPositionDeadband;
                    bool facingOff = pending.HasForward && FacingCos(forward, pending.Forward) < FacingApplyCos;
                    bool joinDue = joined > pending.JoinSettledUtc;
                    bool joinPlayed = ticks - pending.JoinSeenTicks >= MirrorJoinTicks;
                    bool onObject = PresentationApi.ServerSimOnObject(pair.Key);
                    bool onSeat = onObject && PresentationApi.ServerSimOnSeat(pair.Key);
                    bool joinReady = JoinSettlePlan.ShouldSettle(joinDue, joinPlayed, onObject && !onSeat,
                        placeOff || facingOff, now - pending.CopyMovedUtc, now - pending.ServerMovedUtc,
                        now - PresentationApi.StreamAnimatedUtc(pair.Key), MirrorSettle, MirrorAnimHold);
                    if (!placeOff && !facingOff)
                    {
                        // The join history played out with the copy in place: nothing for the join settle to do.
                        if (joinReady)
                        {
                            pending.JoinSettledUtc = joined;
                            sJoinInPlace++;
                        }

                        continue;
                    }

                    if (now - pending.CopyMovedUtc < MirrorSettle || now - pending.ServerMovedUtc < MirrorSettle)
                    {
                        sMirrorWaits++;
                        continue;
                    }

                    // While the stream plays an interaction machine on the copy, the clip places the Sim, and a mirror turn or
                    // walk is a route that cuts the clip short. In sleep-bed (2026-10-20 saved runs) every run with a mirror
                    // turn 1.13 s after the replayed BedSleep E failed (9 of 9; no passing run had one): the copy reached
                    // BedSleep:Sleep 6.5 s before the server's Sim and was left 0.87 m off the bed, and the server's posture
                    // line (SleepingPosture) came only at the clip's end, 7.9 s after the E. The settle time starts again
                    // when the hold ends, so the mirror acts only once the copy has stood still after the clip.
                    TimeSpan animHold = MirrorAnimHold;
                    if (animHold > TimeSpan.Zero && now - PresentationApi.StreamAnimatedUtc(pair.Key) < animHold)
                    {
                        if (!pending.AnimHeld)
                        {
                            pending.AnimHeld = true;
                            PresentationApi.Note(pair.Key, "mirror held: the stream is animating the copy");
                        }

                        sMirrorAnimHolds++;
                        pending.CopyMovedUtc = now;
                        continue;
                    }

                    pending.AnimHeld = false;
                    // Non-seat object postures have no fork seat settle. A long history clip can move them
                    // after the first join settle, so reconcile again once both copies and the stream rest.
                    if (joinDue || (onObject && !onSeat))
                    {
                        if (!joinReady)
                        {
                            sJoinWaits++; // the game has not yet run the history's clips (mirror.join-ticks)
                            continue;
                        }

                        pending.JoinSettledUtc = joined;
                        if (onObject)
                        {
                            if (!JoinSettleSeats && onSeat)
                            {
                                sJoinSeatsLeft++; // the fork's seat settle owns a seated copy (mirror.join-seats)
                                continue;
                            }

                            JoinSettle(pair.Key, sim, pending, placeOff, facingOff);
                            pending.CopyPosition = sim.Position;
                            pending.CopyForward = sim.ForwardVector;
                            pending.CopyMovedUtc = now;
                            continue;
                        }

                        sJoinStanding++; // not on an object: the standing mirror below places it, as for any copy
                    }

                    if (onObject || (!PresentationApi.ServerSimStanding(pair.Key) && PresentationApi.ServerSimRecentlyPostured(pair.Key)))
                    {
                        continue;
                    }

                    // A standing copy walks or turns to the server's Sim like the game's own Sims do, instead of jumping:
                    // after the shower's exit clip the copy stood 0.78 m from the server's Sim and the mirror teleported it
                    // (motion watch, 2026-10-14). Nearer than mirror.walk-min (a step) it is still placed.
                    bool standingCopy = sim.Posture == null || sim.Posture == sim.Standing;
                    float off = pending.HasPosition ? (position - pending.Position).Length() : 0f;
                    if (standingCopy && placeOff && off >= MirrorWalkMin && off <= MirrorWalkMax
                        && StartMirrorWalk(pair.Key, sim, pending.Position, false, Vector3.Zero))
                    {
                        pending.CopyMovedUtc = now;
                        continue;
                    }

                    if (standingCopy && facingOff && !placeOff && MirrorTurns && StartMirrorWalk(pair.Key, sim, position, true, pending.Forward))
                    {
                        pending.CopyMovedUtc = now;
                        continue;
                    }

                    string context = PresentationApi.LocalContext;
                    try
                    {
                        if (placeOff)
                        {
                            PresentationApi.RepairOrphanedSocializingPosture(sim);
                            PresentationApi.LocalContext = "mirror Pz";
                            sim.SetPosition(pending.Position);
                            sStreamPositionApplies++;
                        }

                        if (facingOff)
                        {
                            PresentationApi.LocalContext = "facing settle";
                            sim.SetForward(pending.Forward);
                            sStreamFacings++;
                            sFacingsSettled++;
                        }
                    }
                    finally
                    {
                        PresentationApi.LocalContext = context;
                    }

                    pending.CopyPosition = sim.Position;
                    pending.CopyForward = sim.ForwardVector;
                    pending.CopyMovedUtc = now;
                }
                catch (Exception)
                {
                    // The copy is gone or would not move: the next tick tries again.
                }
            }

            if (gone != null)
            {
                for (int i = 0; i < gone.Count; i++)
                {
                    sMirrorTargets.Remove(gone[i]);
                }
            }
        }

        // a late joiner's history replays each driver's calls on the copy the join's transform already put where
        // the server's Sim is, and root motion is incremental from where a clip starts (docs/engine/native-animation-
        // update.md), so the entry clips move the copy's root again: a2o_bed_getIn_made_x left it 2.43 m off and turned
        // round on a cold join, 0.94 m and 1.31 m off on warm rejoins (2026-10-21), and the copy then got up from the
        // middle of the bed and walked; the sit-down did the same on a seat. The game itself never replays an entry when it
        // restores a Sim: Sim.OnLoad keeps the saved transform and starts the posture's pose (StartDefaultPose). So once
        // the history has played out (the copy and the server's Sim both at rest, the stream not animating the copy), the
        // copy takes the server's place and facing at any distance: the posture snap (mirror.posture-snap) left
        // errors under 1.5 m, and the seat settle (fork) covers seats only. Non-seat object postures settle again
        // if a long history clip moves them after that first correction.
        private static long sJoinSettles;
        private static long sJoinSeatsLeft;
        private static long sJoinWaits;
        // A copy on a seat with a containment slot also has the fork's seat settle: PresenterHook.SettleSeat puts it on
        // the slot 3 s after the sit and again on each 10 s posture repeat while it is more than 0.1 m or about 10 degrees
        // off. The server's own root can sit off that slot (0.22 m after sit_template:Sit), so a join settle onto the
        // server's place and the fork's onto the slot would move the copy twice. 0 leaves seated copies to the fork.
        private static bool JoinSettleSeats { get { return Tunables.Get("mirror.join-seats", 1.0) > 0.5; } }
        private static long sJoinInPlace;
        private static long sJoinStanding;
        private static string sLastJoinSettle = "none";

        private static void JoinSettle(ulong localSim, Sim sim, MirrorTarget pending, bool placeOff, bool facingOff)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            float off = pending.HasPosition ? (sim.Position - pending.Position).Length() : 0f;
            string context = PresentationApi.LocalContext;
            try
            {
                PresentationApi.LocalContext = "join settle";
                if (placeOff)
                {
                    sim.SetPosition(pending.Position);
                }

                if (facingOff)
                {
                    sim.SetForward(pending.Forward);
                }
            }
            finally
            {
                PresentationApi.LocalContext = context;
            }

            sJoinSettles++;
            sLastJoinSettle = (sim.FullName ?? "?").Replace(' ', '_') + ":" + off.ToString("F2", c) + "m" + (facingOff ? "/turned" : "");
            PresentationApi.Note(localSim, "join settle " + off.ToString("F2", c) + " m" + (facingOff ? ", turned" : ""));
        }

        private static Vector3[] ParseWaypoints(string field)
        {
            if (field == null || field.Length == 0 || field == "-")
            {
                return null;
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            string[] points = field.Split(',');
            Vector3[] result = new Vector3[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                string[] xyz = points[i].Split(':');
                float x;
                float y;
                float z;
                if (xyz.Length != 3 || !float.TryParse(xyz[0], NumberStyles.Float, c, out x)
                    || !float.TryParse(xyz[1], NumberStyles.Float, c, out y) || !float.TryParse(xyz[2], NumberStyles.Float, c, out z))
                {
                    return null;
                }

                result[i] = new Vector3(x, y, z);
            }

            return result;
        }

        // route stream, client: walk this client's copy of a streamed Sim to the goal of the route
        // the server's Sim just started (Rt), along this client's own native route. One simulator task per
        // Sim runs its routes; a goal that arrives while one runs replaces whatever was queued.
        // ExecutionFromNonSimTaskIsSafe lets DoRoute run off the Sim's own task (RoutingComponent:479), and
        // PlanToPointFromPoint plans from where the copy stands (no start-side validation). While a route
        // runs the position mirrors leave the Sim alone (State: WalkHoldsPlace) and the shell holds the Sim's animation
        // lines (HoldForRoute). The copy walks the server's path in the server's walk style (Rs, RouteRecorder) and finishes
        // the walk; when it ends, the copy snaps to where the server's Sim stopped (if more than ClientRouteSnapDistance
        // away) and the waiting lines play. ExpireClientRoutes stops a copy that walks longer than the server's own walk
        // took (WalkOverrun), and any after ClientRouteMaxHold (a lost end line). Any failure falls back to the mirror.
        private sealed class ClientRoute
        {
            public Vector3 Goal;
            public Vector3[] Waypoints;
            public bool FromPath;
            public bool Pending;
            public bool Running;
            public DateTime StartedUtc;
            public Route Current;
            // A mirror correction (StartMirrorWalk): a turn in place to Facing instead of a walk to Goal.
            public bool TurnOnly;
            public Vector3 Facing;
            public bool StopRequested;
            public DateTime StopRequestedUtc;
            // The route's exit-reason mask before this client cleared it, restored for a stop (-1 = unknown).
            public int ExitMask = -1;
            // Bumped when the watchdog gives up on a wedged route: the runner's late return then releases nothing.
            public int Generation;
            public bool ServerEnded;
            public DateTime ServerEndedUtc;
            public Vector3 ServerEnd;
            public ulong DestObject;   // the server's route destination object and slot (Rs), 0 when it routed to a point
            public uint DestSlot;
            // The server's walk: when it started and ended (ms, the machine clock the dev stack shares; 0 = unknown).
            public long ServerStartMs;
            public long ServerEndMs;
            // The server's Stop call on this walk (Rh) while not yet replayed on the copy: its mode, and how far the
            // server's Sim had walked; and where the copy's route (or the server's newest leg) began.
            public bool HaltPending;
            public StopMode HaltMode;
            public float HaltMoved;
            public Vector3 StartPosition;
            // How far into its walk the server's Sim was at that call (WalkHaltTiming: ms or SimClock ticks by walk.halt-clock;
            // -1 = unknown, or a later leg: the distance rule), and when the copy's own walk began (its DoRoute; 0 = not
            // yet). ReceivedMs: when this walk's Rs reached this client (walk.start-lead-ms counts from it); LeadMs: how
            // long the copy's start waited for it; Legs: the server's legs after the first that this walk took on.
            public long HaltOffset = -1L;
            public long CopyStartMs;
            public long CopyStartTicks;
            public long ReceivedMs;
            public long LeadMs;
            public int Legs;
            // The server's walk style for this walk (Rs / Ru); applied to the client's route once it moves.
            public bool HasSpec;
            public RouteAnimationSpec Spec;
            public bool SpecApplied;
            public object StyledLeg; // the route action (a leg of the walk) the style went onto last
            public int RequestedStyle = -1; // the Sim.WalkStyle this walk requested on the copy (-1: none)
            // The server's walk style leg by leg (its Rs for each leg, in order), and the copy's leg now (-1: none yet).
            public readonly List<uint> LegStyles = new List<uint>();
            public int CopyLeg = -1;
            public readonly Dictionary<string, bool> SourceRoutes = new Dictionary<string, bool>();
        }

        private sealed class ClientRouteRunner
        {
            private readonly ulong mSim;

            public ClientRouteRunner(ulong sim)
            {
                mSim = sim;
            }

            public void Run()
            {
                RunClientRoutes(mSim);
            }
        }

        private static readonly Dictionary<ulong, ClientRoute> sClientRoutes = new Dictionary<ulong, ClientRoute>();
        private static readonly Dictionary<ulong, string[]> sVehiclePendingRoutes = new Dictionary<ulong, string[]>();

        internal static void TakeVehicleOwnership(ulong localSim)
        {
            sMirrorTargets.Remove(localSim);
            ClientRoute entry;
            if (sClientRoutes.TryGetValue(localSim, out entry))
            {
                entry.Pending = false;
                entry.ServerEnded = false;
                if (entry.Current != null)
                {
                    StopClientRoute(entry);
                }
                entry.Generation++; // A late DoRoute return may neither snap nor release another owner's state.
                entry.Running = false;
                sClientRoutes.Remove(localSim);
            }
            PresentationApi.ReleaseHeldLines(localSim);
        }

        internal static void ReleaseVehicleOwnership(ulong localSim)
        {
            string[] pending;
            if (sVehiclePendingRoutes.TryGetValue(localSim, out pending))
            {
                sVehiclePendingRoutes.Remove(localSim);
                ReplayRoute(pending);
            }
        }
        private static readonly TimeSpan ClientRouteMaxHold = TimeSpan.FromSeconds(20);
        // STOPGAP(boundary-recording): walk-end-snap a copy that ended its walk elsewhere jumps to the server's end; it should end there itself.
        private static float ClientRouteSnapDistance { get { return Tunables.Get("walk.end-snap", 0.1f); } }
        private static long sRoutesStopped;

        private static long sRoutesReceived;
        private static long sRoutesStarted;
        private static long sRoutesPlanFailed;
        private static long sRoutesFromPath;
        private static long sRoutesPathOffGoal;
        // STOPGAP(boundary-recording): walk-plan-tolerance the server's path is used only if it ends near the goal; replay the server's final path instead.
        private static float PathGoalTolerance { get { return Tunables.Get("walk.path-goal", 0.5f); } }
        private static long sRoutesChained;
        private static long sRoutesLegs; // starts that were the next leg of the walk the copy was on (kept walking)
        // STOPGAP(boundary-recording): walk-leg-match a door leg is recognised by its goal; RouteRecorder should name the walk each leg belongs to.
        private static float SameWalkGoalTolerance { get { return Tunables.Get("walk.same-goal", 0.15f); } }
        private static string sRouteFailDetails = string.Empty;
        private static long sRouteLagMsSum;
        private static long sRouteLagCount;
        private static long sRouteLagMsMax;
        // When (UTC) and on which Sim the worst lag and the last end snap came, so a spike outside every motion watch can
        // be matched to the scenario step or join that made it (a 6.8 s lag and a snap in the two-* suite, 2026-10-21).
        private static string sRouteLagMaxAt = "-";
        private static string sLastSnap = "-";
        private static long sRoutesDone;
        private static long sRoutesFailed;
        private static long sRoutesExceptions;
        private static long sRoutesHeldApplies;
        private static long sRoutesSnaps;
        private static long sRoutesFacings; // walks planned to the server's destination slot
        private static long sRoutesSlotOffGoal; // slot plans that ended away from the server's goal (server's path used)
        // STOPGAP(boundary-recording): walk-plan-tolerance a slot plan is used only if it ends on the server's goal; replay the server's final path instead.
        private static float SlotGoalTolerance { get { return Tunables.Get("walk.slot-goal", 0.15f); } }
        private static string sRoutesLastError = "-";
        private static string sRoutesLastStack = "-";

        // 0|Vp|rt|corr|actor|index|unsigned_path_type|x_cm|y_cm|z_cm.
        internal static bool ReplayNavEpoch(string[] f)
        {
            return ClientBoundaryRoute.Receipts.NavEpoch(f);
        }

        internal static bool ReplayRoutePoint(string[] f)
        {
            return ClientBoundaryRoute.Receipts.Point(ClientBoundaryRoute.Receipts.CurrentEpoch, f);
        }

        // seq|Rt|sim|x|y|z|tag|destSlot|destObj
        internal static bool ReplayRoute(string[] f)
        {
            if (!ClientBoundaryRoute.IsNativeRoute(f))
            {
                return ClientBoundaryRoute.Receipts.Header(ClientBoundaryRoute.Receipts.CurrentEpoch, f);
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            ObjectGuid simId = PresentationApi.ReplayObject(ulong.Parse(f[2], c));
            if (!simId.IsValid || simId.ObjectFromId<Sim>() == null)
            {
                return false;
            }

            if (Vehicles.HoldsPosition(simId.Value))
            {
                sVehiclePendingRoutes[simId.Value] = f;
                return true;
            }

            string sourceRoute = RouteStreamIdentity.StartHandle(f);
            ClientRoute entry;
            if (!sClientRoutes.TryGetValue(simId.Value, out entry))
            {
                entry = new ClientRoute();
                sClientRoutes[simId.Value] = entry;
            }

            // The next leg of the walk the copy is on: the server's Sim walks through a door as one walk in sub-paths
            // (FollowRouteAction.PerformRoute pushes a FollowSubPathRouteAction for each path after the portal), each its
            // own Route.Start with the whole walk's goal and no waypoints. Restarting the copy's walk for each leg cost
            // 0.7 s a time to stop: the copy overran the server's walk and snapped 1.5-2.3 m (shower, sleep-bed,
            // 2026-10-17, once the fork's door fix let the server's Sim through). It keeps walking its own plan there.
            // Not a copy whose walk is being stopped: the server's Sim stopped too and walks again (two-chairs 2026-10-21:
            // an Emergency stop, a new route to the same chair 0.8 s later; taken as the next leg, the stopped copy never
            // walked again and was snapped 1.33 m into the chair). That one starts the new walk when the stop ends.
            Vector3 goal = new Vector3(float.Parse(f[3], c), float.Parse(f[4], c), float.Parse(f[5], c));
            uint destSlot = f.Length > 8 ? uint.Parse(f[7], c) : 0u;
            ulong destObject = f.Length > 8 ? PresentationApi.ReplayObject(ulong.Parse(f[8], c)).Value : 0uL;
            if (entry.Running && entry.Current != null && !entry.TurnOnly && !entry.StopRequested && destSlot == entry.DestSlot && destObject == entry.DestObject
                && (goal - entry.Goal).Length() <= SameWalkGoalTolerance)
            {
                entry.ServerEnded = false;
                entry.HaltPending = false;
                entry.Legs++;
                if (sourceRoute.Length > 0) entry.SourceRoutes[sourceRoute] = true;
                Sim legSim = simId.ObjectFromId<Sim>();
                if (legSim != null)
                {
                    entry.StartPosition = legSim.Position; // a stop on this leg counts from here (Rh's moved)
                }

                sRoutesReceived++;
                sRoutesLegs++;
                string legStyle = "";
                if (f.Length > 14)
                {
                    uint hash = uint.Parse(f[11], c);
                    entry.LegStyles.Add(hash);
                    try
                    {
                        legStyle = " in " + SimWalkStyleRules.GetWalkStyleForLocomotionState(hash).ToString();
                    }
                    catch (Exception)
                    {
                        legStyle = " in style " + f[11];
                    }

                    // The copy is on this leg already (it got ahead of the server): restyle the leg under way.
                    if (entry.CopyLeg >= entry.LegStyles.Count - 1)
                    {
                        entry.SpecApplied = false;
                    }
                }

                PresentationApi.Note(simId.Value, "walk goes on: the server's next leg (" + (entry.LegStyles.Count - 1).ToString(c) + ") to the same goal" + legStyle);
                ArmLegStyle(simId.Value, simId.ObjectFromId<Sim>(), entry);
                return true;
            }

            entry.Goal = goal;
            entry.SourceRoutes.Clear();
            if (sourceRoute.Length > 0) entry.SourceRoutes[sourceRoute] = true;
            entry.TurnOnly = false;
            Vector3[] waypoints = f.Length > 9 ? ParseWaypoints(f[9]) : null;
            entry.HasSpec = f.Length > 14;
            entry.SpecApplied = false;
            if (entry.HasSpec)
            {
                entry.Spec = new RouteAnimationSpec();
                entry.Spec.mStyleNameHash = uint.Parse(f[11], c);
                entry.Spec.mAge = uint.Parse(f[12], c);
                entry.Spec.mGender = uint.Parse(f[13], c);
                entry.Spec.mSpecies = uint.Parse(f[14], c);
            }

            entry.LegStyles.Clear();
            entry.CopyLeg = -1;
            entry.StyledLeg = null;
            if (entry.HasSpec)
            {
                entry.LegStyles.Add(entry.Spec.mStyleNameHash);
            }

            entry.Waypoints = waypoints;
            entry.DestSlot = destSlot;
            entry.DestObject = destObject;
            entry.Pending = true;
            entry.ServerEnded = false;
            entry.HaltPending = false;
            entry.HaltOffset = -1L;
            entry.Legs = 0;
            entry.ReceivedMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            sRoutesReceived++;
            long serverStartMs;
            entry.ServerStartMs = 0L;
            entry.ServerEndMs = 0L;
            if (f.Length > 10 && long.TryParse(f[10], NumberStyles.Integer, c, out serverStartMs))
            {
                entry.ServerStartMs = serverStartMs;
                // Server and clients share a machine clock in the dev stack: how late the route reached this client.
                long lag = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond - serverStartMs;
                sRouteLagMsSum += lag;
                sRouteLagCount++;
                if (lag > sRouteLagMsMax)
                {
                    sRouteLagMsMax = lag;
                    sRouteLagMaxAt = DateTime.UtcNow.ToString("HH:mm:ss", c) + "/" + simId.Value.ToString(c);
                }
            }

            // A copy on the server's path that is still walking finishes it, then walks the next route (the server's Sim
            // usually starts the next one where the last ended, often a short step into an object's slot); stopping
            // it there left it standing mid-way (cold-test-082). Otherwise the newest goal replaces the route.
            if (entry.Running && entry.Current != null && !(entry.FromPath && waypoints != null))
            {
                PresentationApi.Note(simId.Value, "walk stop: a new walk replaces it");
                StopClientRoute(entry);
            }
            else if (entry.Running && entry.Current != null)
            {
                sRoutesChained++;
            }
            if (!entry.Running)
            {
                entry.Running = true;
                entry.StopRequested = false;
                entry.StartedUtc = DateTime.UtcNow;
                Simulator.AddObject(new OneShotFunctionTask(new Function(new ClientRouteRunner(simId.Value).Run)));
            }

            return true;
        }

        // The mirror's correction done by the copy itself on the walk runner (its lines wait meanwhile, as for a server
        // walk): a walk to goal, or (turnOnly) a turn in place to facing. Its own end: the walk's end snap lands on goal
        // exactly, and WalkOverrun allows distance / mirror.walk-speed + mirror.walk-slack. A server walk replaces it.
        private static bool StartMirrorWalk(ulong id, Sim sim, Vector3 goal, bool turnOnly, Vector3 facing)
        {
            ClientRoute entry;
            if (!sClientRoutes.TryGetValue(id, out entry))
            {
                entry = new ClientRoute();
                sClientRoutes[id] = entry;
            }

            if (entry.Running)
            {
                return false;
            }

            DateTime now = DateTime.UtcNow;
            long nowMs = now.Ticks / TimeSpan.TicksPerMillisecond;
            float distance = (sim.Position - goal).Length();
            entry.Goal = goal;
            entry.Waypoints = null;
            entry.HasSpec = false;
            entry.SpecApplied = false;
            entry.TurnOnly = turnOnly;
            entry.Facing = facing;
            entry.ServerEnded = true;
            entry.ServerEndedUtc = now;
            entry.ServerEnd = goal;
            entry.ServerStartMs = nowMs;
            entry.ServerEndMs = nowMs + (long)(1000.0 * (MirrorWalkSlack + distance / Math.Max(0.1, MirrorWalkSpeed)));
            entry.ReceivedMs = 0L; // the copy's own correction: no lead, and no server stop to replay
            entry.HaltOffset = -1L;
            entry.Legs = 0;
            entry.Pending = true;
            entry.Running = true;
            entry.StopRequested = false;
            entry.StartedUtc = now;
            Simulator.AddObject(new OneShotFunctionTask(new Function(new ClientRouteRunner(id).Run)));
            if (turnOnly)
            {
                sMirrorTurns++;
            }
            else
            {
                sMirrorWalks++;
            }

            PresentationApi.Note(id, turnOnly ? "mirror turn to the server's facing" : "mirror walk " + distance.ToString("F2", CultureInfo.InvariantCulture) + " m to the server's Sim");
            return true;
        }

        private static void RunClientRoutes(ulong id)
        {
            ClientRoute entry;
            if (!sClientRoutes.TryGetValue(id, out entry))
            {
                return;
            }

            int generation = entry.Generation;
            try
            {
                while (entry.Pending)
                {
                    entry.Pending = false;
                    entry.StartedUtc = DateTime.UtcNow;
                    entry.CopyStartMs = 0L;
                    entry.LeadMs = 0L;
                    Sim sim = new ObjectGuid(id).ObjectFromId<Sim>();
                    if (sim == null || sim.HasBeenDestroyed)
                    {
                        break;
                    }

                    // A puppet the server has stood up walks; its Po may still be on the way.
                    if (sim.Posture != null && sim.Posture != sim.Standing && PresentationApi.PresenterOn())
                    {
                        PresentationApi.StandPuppet(id);
                    }

                    PresentationApi.RepairOwnMachines(sim.ObjectId);
                    try
                    {
                        Route route = NewCopyRoute(sim);
                        bool planned = false;
                        string tried = "";
                        Vector3[] waypoints = entry.TurnOnly ? null : entry.Waypoints;
                        if (entry.TurnOnly)
                        {
                            // The mirror's facing correction: a turn in place, animated like the game's own.
                            route = sim.CreateRouteTurnToFace(sim.Position + entry.Facing);
                            route.ExecutionFromNonSimTaskIsSafe = true;
                            if (PresentationApi.PresenterOn())
                            {
                                ConfigurePuppetRoute(sim, route);
                            }

                            planned = true;
                        }
                        // A path that does not end at the goal is not the walk the server's Sim makes: fridge-snack
                        // (2026-10-14) sent the Sim's own start twice for a walk to the fridge's slot 2.55 m away, and the
                        // copy arrived without moving, then snapped there when the server's walk ended. Plan to the goal.
                        if (waypoints != null && waypoints.Length > 0 && (waypoints[waypoints.Length - 1] - entry.Goal).Length() > PathGoalTolerance)
                        {
                            waypoints = null;
                            sRoutesPathOffGoal++;
                        }

                        // A walk to an object's slot is planned to that slot, as the server's was: the engine then ends it
                        // facing the way the slot does, turning as part of the walk. What plays next starts from there, and
                        // root motion is incremental from where a clip starts (native-animation-update.md): on the server's
                        // waypoints the copy side-stepped 0.57 m to a dining chair without turning, entered "Sitting 45"
                        // 45 degrees off and sat 0.5 m off the seat; a SetForward on arrival snapped it mid-clip.
                        // Not a Sim's slot: a social's partner moves (and this client's copy of it stands where the stream
                        // put it), so its slot is no fixed place; those walks keep the server's path (social-chat: a walk to
                        // the partner's slot overran and snapped 0.95 m). The puppet's route options go on before planning:
                        // planned around the other Sim's copy, a walk to the loveseat overran and snapped 0.55 m.
                        bool toSlot = false;
                        Sims3.Gameplay.Abstracts.GameObject dest = !planned && entry.DestObject != 0uL && entry.DestSlot != 0u
                            ? Sims3.Gameplay.Abstracts.GameObject.GetObject<Sims3.Gameplay.Abstracts.GameObject>(new ObjectGuid(entry.DestObject)) : null;
                        if (dest != null && !(dest is Sim))
                        {
                            // Only the server's walk: a slot plan that ends elsewhere than the server's goal (a shower's stall
                            // reached through its door, a bed side) ended 0.3-0.5 m short and snapped round on arrival; those
                            // keep the server's path.
                            RoutePlanResult slotPlan = route.PlanToSlotFromPoint(sim.Position, dest, (Slot)entry.DestSlot);
                            bool slotOnGoal = slotPlan.Succeeded() && (slotPlan.mDestination - entry.Goal).Length() <= SlotGoalTolerance;
                            if (slotPlan.Succeeded() && !slotOnGoal)
                            {
                                sRoutesSlotOffGoal++;
                            }

                            if (slotOnGoal)
                            {
                                planned = true;
                                toSlot = true;
                                waypoints = null;
                                sRoutesFacings++;
                            }
                            else
                            {
                                tried = slotPlan.Succeeded() ? "slot off goal" : "slot " + slotPlan.mType.ToString();
                                route = NewCopyRoute(sim);
                            }
                        }

                        if (!planned && waypoints != null && waypoints.Length > 0)
                        {
                            // The server's path: plan to its first corner, then its straight segments to the goal.
                            RoutePlanResult cornerPlan = route.PlanToPointFromPoint(sim.Position, waypoints[0]);
                            if (cornerPlan.Succeeded())
                            {
                                for (int w = 1; w < waypoints.Length; w++)
                                {
                                    route.Append(waypoints[w]);
                                }

                                // The planned destination is the first waypoint; a mid-walk replan (a push, another Sim in
                                // the way) would turn the copy back to it. Make the goal the destination, keeping the path.
                                route.ClearDestinationList();
                                Sims3.SimIFace.RouteDestinations.PointDestination goal = new Sims3.SimIFace.RouteDestinations.PointDestination();
                                goal.mPoint = entry.Goal;
                                route.AddDestination(goal);
                                planned = true;
                                sRoutesFromPath++;
                            }
                            else
                            {
                                tried += (tried.Length > 0 ? ", " : "") + "path " + cornerPlan.mType.ToString();
                                route = NewCopyRoute(sim);
                            }
                        }

                        entry.FromPath = planned && !entry.TurnOnly;
                        if (!planned)
                        {
                            RoutePlanResult ownPlan = route.PlanToPointFromPoint(sim.Position, entry.Goal);
                            if (!ownPlan.Succeeded())
                            {
                                // Before 2026-10-20 this gave up without a note: the copy stood still and snapped when the
                                // server's walk ended, with nothing in the motion watch to say why (see NewCopyRoute).
                                sRoutesPlanFailed++;
                                PresentationApi.Note(id, "walk plan failed to " + entry.Goal.x.ToString("F2", CultureInfo.InvariantCulture) + "," + entry.Goal.z.ToString("F2", CultureInfo.InvariantCulture)
                                    + ": own " + ownPlan.mType.ToString() + (tried.Length > 0 ? " (" + tried + ")" : ""));
                                continue;
                            }
                        }

                        // The server drives this walk: exit reasons this client sets on its copy (its queue mirror, input
                        // capture) must not abort it. On client 3 half the walks on the server's path ended 1-3 m short
                        // with a successful plan (cold-test-084).
                        entry.ExitMask = route.ExitReasonsInterrupt;
                        route.ExitReasonsInterrupt = 0;

                        // walk.start-lead-ms: a server walk starts that long after its Rs reached this client, so the
                        // copy is behind the server's Sim when an Rh comes and can stop at the same point (WalkHaltTiming).
                        // A newer walk for this Sim (Pending) is planned instead; a given-up runner leaves.
                        if (!entry.TurnOnly && entry.ReceivedMs > 0L && StartLeadMs > 0L)
                        {
                            long waitedFrom = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                            while (!entry.Pending && entry.Generation == generation
                                && WalkHaltTiming.LeadLeft(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond - entry.ReceivedMs, StartLeadMs) > 0L)
                            {
                                Simulator.Sleep(0);
                            }

                            entry.LeadMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond - waitedFrom;
                            if (entry.Pending || entry.Generation != generation)
                            {
                                continue;
                            }

                            sRoutesLeadWaits++;
                        }

                        sRoutesStarted++;
                        entry.Current = route;
                        entry.StopRequested = false;
                        entry.StartPosition = sim.Position;
                        PresentationApi.Note(id, "walk start from " + sim.Position.x.ToString("F2", CultureInfo.InvariantCulture) + "," + sim.Position.z.ToString("F2", CultureInfo.InvariantCulture)
                            + " to " + entry.Goal.x.ToString("F2", CultureInfo.InvariantCulture) + "," + entry.Goal.z.ToString("F2", CultureInfo.InvariantCulture)
                            + (toSlot ? " to the server's slot" : planned ? " on the server's path (" + (waypoints == null ? 0 : waypoints.Length).ToString(CultureInfo.InvariantCulture) + " waypoints)" : " own plan")
                            + (entry.RequestedStyle >= 0 || !entry.HasSpec ? "" : " in " + (ServerStyleName(entry))));
                        RequestServerStyle(sim, entry);
                        bool done;
                        try
                        {
                            entry.CopyStartMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                            entry.CopyStartTicks = SimClock.CurrentTicks;
                            if (entry.HaltPending)
                            {
                                if (entry.HaltOffset >= 0L)
                                {
                                    sRoutesHaltsHeld++;
                                }

                                WatchHalt(id, entry); // an Rh that came before the copy began its walk
                            }

                            done = sim.DoRoute(route);
                        }
                        finally
                        {
                            UnrequestServerStyle(sim, entry);
                        }

                        PresentationApi.Note(id, "walk end " + (done ? "arrived" : "failed " + sim.ExitReason.ToString().Replace(", ", "+")) + (entry.StopRequested ? " (stopped)" : "")
                            + " at " + sim.Position.x.ToString("F2", CultureInfo.InvariantCulture) + "," + sim.Position.z.ToString("F2", CultureInfo.InvariantCulture));
                        entry.Current = null;
                        if (done)
                        {
                            sRoutesDone++;
                        }
                        else
                        {
                            sRoutesFailed++;
                            if (entry.FromPath && !entry.StopRequested)
                            {
                                // Why a walk on the server's path failed: the plan result type and how far from the goal.
                                sRouteFailDetails = sim.FirstName + ":" + route.PlanResult.mType.ToString() + ":" + sim.ExitReason.ToString().Replace(", ", "+") + "@" + (sim.Position - entry.Goal).Length().ToString("F1", CultureInfo.InvariantCulture)
                                    + (sRouteFailDetails.Length > 0 ? " " + sRouteFailDetails : string.Empty);
                                if (sRouteFailDetails.Length > 200)
                                {
                                    sRouteFailDetails = sRouteFailDetails.Substring(0, 200);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        sRoutesExceptions++;
                        // The tail names the machine (actor, state); the stack names the caller (read by probe).
                        string message = (ex.Message ?? "").Replace('|', '/').Replace(';', ',');
                        sRoutesLastError = ex.GetType().Name + ":" + (message.Length > 120 ? message.Substring(message.Length - 120) : message);
                        string stack = ex.StackTrace ?? "";
                        sRoutesLastStack = stack.Length > 1500 ? stack.Substring(0, 1500) : stack;
                        PresentationApi.Note(id, "walk exception " + sRoutesLastError);
                    }
                }
            }
            finally
            {
                if (entry.Generation == generation)
                {
                    entry.Running = false;
                    entry.Pending = false;
                    entry.StopRequested = false;
                    entry.Current = null;
                    ReleaseRouteHold(id, entry);
                    PresentationApi.ReleaseHeldLines(id);
                }
                else
                {
                    sRoutesLateReturns++; // the watchdog already gave this route up
                }
            }
        }

        // seq|Rte|sim|x|y|z: the server's Sim stopped routing, there.
        // Ru|sim|style|age|gender|species|route: the server's Sim changed walk style mid-walk.
        internal static bool ReplayRouteStyle(string[] f)
        {
            if (f.Length < 7)
            {
                return false;
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            ObjectGuid simId = PresentationApi.ReplayObject(ulong.Parse(f[2], c));
            ClientRoute entry;
            if (!simId.IsValid || !sClientRoutes.TryGetValue(simId.Value, out entry))
            {
                return false;
            }

            entry.Spec = new RouteAnimationSpec();
            entry.Spec.mStyleNameHash = uint.Parse(f[3], c);
            entry.Spec.mAge = uint.Parse(f[4], c);
            entry.Spec.mGender = uint.Parse(f[5], c);
            entry.Spec.mSpecies = uint.Parse(f[6], c);
            entry.HasSpec = true;
            entry.SpecApplied = false;
            // The server's leg under way changed style: the copy's same leg takes it (ApplyRouteStyle), and so does the
            // request for its next leg.
            if (entry.LegStyles.Count == 0)
            {
                entry.LegStyles.Add(entry.Spec.mStyleNameHash);
            }
            else
            {
                entry.LegStyles[entry.LegStyles.Count - 1] = entry.Spec.mStyleNameHash;
            }

            return true;
        }

        // A walking copy walks in the server's walk style (Rs / Ru). Each leg of a walk (a FollowSubPathRouteAction
        // after a door's portal) picks its style when it starts (FollowRouteAction.PerformRoute:
        // SimWalkStyleRules.GetAdjustedWalkStyleForRoute), and with no walk-style request on the Sim that is the
        // copy's own choice (AutoSelect): a user-directed server Sim walks fast on a long route, while its copy, whose
        // queue head is not user-directed, walked (shower, sleep-bed, 2026-10-17: 1.2-1.8 m/s against 1.7-2.5, arriving
        // 1.7-3.1 s late). So the runner requests the server's first-leg style on the copy before the walk, each later
        // leg's request is armed while the leg before it runs (ArmLegStyle, from the server's Rs for that leg), and a
        // style the server changes mid-walk (Ru) goes onto the leg under way.
        private static long sRouteStylesApplied;
        private static long sRouteLegStyles;
        private static long sRouteStyleRequests;
        private static FieldInfo sRouteActionsField;

        // The leg the copy walks now: its first route action (RoutingComponent.mRouteActions is protected). A leg's
        // action shares the walk's Route object (GetRouteActionForSubPath(mRoute, n, mRoute)), so the leg is the action.
        private static FollowRouteAction FollowedLeg(Sim sim)
        {
            if (sRouteActionsField == null)
            {
                sRouteActionsField = typeof(RoutingComponent).GetField("mRouteActions", BindingFlags.NonPublic | BindingFlags.Instance);
            }

            RoutingComponent routing = sim == null ? null : sim.RoutingComponent;
            List<RouteAction> actions = sRouteActionsField == null || routing == null ? null : sRouteActionsField.GetValue(routing) as List<RouteAction>;
            return actions != null && actions.Count > 0 ? actions[0] as FollowRouteAction : null;
        }

        // The server's walk style for this walk, from its locomotion state hash (false: none, or a state the game's
        // table does not name).
        private static bool ServerStyle(ClientRoute entry, out Sim.WalkStyle style)
        {
            style = Sim.WalkStyle.Walk;
            if (!entry.HasSpec || entry.TurnOnly)
            {
                return false;
            }

            try
            {
                style = SimWalkStyleRules.GetWalkStyleForLocomotionState(entry.Spec.mStyleNameHash);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string ServerStyleName(ClientRoute entry)
        {
            Sim.WalkStyle style;
            return ServerStyle(entry, out style) ? style.ToString() : "style " + entry.Spec.mStyleNameHash.ToString(CultureInfo.InvariantCulture);
        }

        private static void RequestServerStyle(Sim sim, ClientRoute entry)
        {
            Sim.WalkStyle style;
            // walk.style-request 0 (live tunable): the copy walks in the style its own game picks.
            if (sim == null || entry.RequestedStyle >= 0 || Tunables.Get("walk.style-request", 1.0) < 0.5 || !ServerStyle(entry, out style))
            {
                return;
            }

            try
            {
                if (sim.RequestWalkStyle(style))
                {
                    entry.RequestedStyle = (int)style;
                    sRouteStyleRequests++;
                }
            }
            catch (Exception)
            {
                // The copy keeps the walk style its own game picks.
            }
        }

        private static void UnrequestServerStyle(Sim sim, ClientRoute entry)
        {
            if (entry.RequestedStyle < 0)
            {
                return;
            }

            Sim.WalkStyle style = (Sim.WalkStyle)entry.RequestedStyle;
            entry.RequestedStyle = -1;
            try
            {
                if (sim != null && !sim.HasBeenDestroyed)
                {
                    sim.UnrequestWalkStyle(style);
                }
            }
            catch (Exception)
            {
                // Nothing to undo when the copy is gone; the request is forgotten either way.
            }
        }

        private static void ApplyRouteStyle(ulong id, ClientRoute entry)
        {
            if (!entry.HasSpec || entry.Current == null)
            {
                return;
            }

            FollowRouteAction leg = null;
            try
            {
                Sim sim = new ObjectGuid(id).ObjectFromId<Sim>();
                leg = FollowedLeg(sim);
                Route route = leg != null && leg.RouteToBeFollowed != null ? leg.RouteToBeFollowed : entry.Current;
                object key = leg != null ? (object)leg : route;
                bool newLeg = leg != null && !object.ReferenceEquals(key, entry.StyledLeg);
                if (entry.SpecApplied && !newLeg)
                {
                    return;
                }

                if (!route.IsActive)
                {
                    return;
                }

                bool next = entry.SpecApplied;
                if (newLeg)
                {
                    entry.CopyLeg++;
                }

                entry.SpecApplied = true;
                entry.StyledLeg = key;
                // This leg's style: the server's for the same leg, or the last it sent when the copy is ahead of it.
                int n = Math.Min(Math.Max(entry.CopyLeg, 0), entry.LegStyles.Count - 1);
                if (n >= 0)
                {
                    entry.Spec.mStyleNameHash = entry.LegStyles[n];
                }

                Sim.WalkStyle style;
                bool named = ServerStyle(entry, out style);
                if (named && leg != null && leg.mWalkStyle == style)
                {
                    // the leg started in the server's style (the request armed for it); what its steering runs, for the record
                    PresentationApi.Note(id, "walk leg " + entry.CopyLeg.ToString(CultureInfo.InvariantCulture) + " " + leg.GetType().Name + " in " + style.ToString()
                        + " (steering " + (sim.SimRoutingComponent == null ? "-" : sim.SimRoutingComponent.CurrentWalkStyle.ToString()) + ")");
                }
                else
                {
                    route.UpdateRouteAnimationSpec(new ObjectGuid(id), entry.Spec);
                    sRouteStylesApplied++;
                    if (next)
                    {
                        sRouteLegStyles++;
                    }

                    PresentationApi.Note(id, "walk style: the server's " + (named ? style.ToString() : entry.Spec.mStyleNameHash.ToString(CultureInfo.InvariantCulture))
                        + " on leg " + entry.CopyLeg.ToString(CultureInfo.InvariantCulture) + " (own " + (leg == null ? "-" : leg.mWalkStyle.ToString() + " " + leg.GetType().Name) + ")");
                }

                ArmLegStyle(id, sim, entry);
            }
            catch (Exception)
            {
                entry.SpecApplied = true;
                entry.StyledLeg = leg;
            }
        }

        // A leg picks its walk style when it starts, from the Sim's request (SimRoutingComponent.UpdateWalkStyle only
        // records it), so the request for the copy's next leg goes in while the leg before it runs: the server's style
        // for that leg, or the last one it sent while the copy is ahead of it. Sleep-bed (2026-10-17): the server walks
        // the hallway in FastWalk and the bedroom after the door in Walk; one request for the whole walk kept the copy
        // in FastWalk after the door (2.0-2.5 m/s against 1.8).
        private static long sRouteLegArms;

        private static void ArmLegStyle(ulong id, Sim sim, ClientRoute entry)
        {
            if (sim == null || entry.RequestedStyle < 0 || entry.LegStyles.Count == 0)
            {
                return; // no request on this walk (walk.style-request 0, or the server's style unknown)
            }

            int n = Math.Min(entry.CopyLeg + 1, entry.LegStyles.Count - 1);
            try
            {
                Sim.WalkStyle style = SimWalkStyleRules.GetWalkStyleForLocomotionState(entry.LegStyles[n]);
                if ((int)style == entry.RequestedStyle)
                {
                    return;
                }

                UnrequestServerStyle(sim, entry);
                if (sim.RequestWalkStyle(style))
                {
                    entry.RequestedStyle = (int)style;
                    sRouteStyleRequests++;
                    sRouteLegArms++;
                    PresentationApi.Note(id, "walk style: the server's " + style.ToString() + " armed for leg " + n.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception)
            {
                // The copy keeps the style it walks in.
            }
        }

        // A puppet walks the path the server already solved: it does not stop to wait for other Sims (the engine's
        // stand-and-wait), and its own footprint blocks no one. Its copy waited by chairs and in doorways while the
        // server's Sim had long arrived (2026-10-14). It steers round other Sims as the server's Sim does (below).
        private static long sPuppetRoutesConfigured;
        private static long sPuppetRouteIgnores;

        // The avoidance range stays the game's (scale 1), as on the server's Sim. At exactly 0 the native steering moved the
        // copy on every second tick only (0 cm, then 14 cm; the server's Sim 9 cm every tick) at 0.8 of the server's
        // speed: the micro stutter on walks (2026-10-17, .omo/run/act/TickBurstProbe.cs; motion 163547). At 0.01 it moved
        // every tick, but walked the bedroom at 4.8 cm a frame against the server's 6.1 (motion 165355); at 1 the copy
        // matched the server on every leg and sleep-bed passed (motion 165609).
        // Live tunables: walk.puppet-route 0 turns all three settings and the route options off. Applied on every walk,
        // so a change reaches the next one.
        private static bool PuppetRouteOn { get { return Tunables.Get("walk.puppet-route", 1.0) > 0.5; } }
        private static float PuppetAvoidanceScale { get { return Tunables.Get("walk.puppet-avoid-scale", 1f); } }
        private static bool PuppetObstructionsOff { get { return Tunables.Get("walk.puppet-obstruct-off", 1.0) > 0.5; } }
        private static bool PuppetFootprintOff { get { return Tunables.Get("walk.puppet-footprint-off", 1.0) > 0.5; } }

        // Every route the copy plans, with the puppet's route options set before it is planned: they are planner inputs
        // (who blocks the path, the penalty for Sims at the destination), so set after the plan, as they were for walks on
        // the server's path and the copy's own plans until 2026-10-20, they changed nothing. In social-chat the walks to
        // a social jig's slots reached the client (Rs) but never started: neither copy walked, and each snapped 0.3-1.9 m
        // when the server's walk ended (2026-10-20 runs). With the options set first, the copies walk on the server's path
        // and social-chat passed 8 of 8 (arms 20261021-003353). RunClientRoutes notes a plan that still fails.
        private static Route NewCopyRoute(Sim sim)
        {
            Route route = sim.CreateRoute();
            route.ExecutionFromNonSimTaskIsSafe = true;
            // These routes present on-foot legs only. Vehicle travel has its own authoritative
            // owner; allowing the local planner to hire a taxi runs client vehicle gameplay.
            route.SetOption(Route.RouteOption.EnablePlanningAsCar, false);
            route.SetOption2(Route.RouteOption2.EnablePlanningAsBoat, false);
            if (PresentationApi.PresenterOn())
            {
                ConfigurePuppetRoute(sim, route);
            }

            return route;
        }

        private static void ConfigurePuppetRoute(Sim sim, Route route)
        {
            try
            {
                ObjectGuid id = sim.ObjectId;
                bool on = PuppetRouteOn;
                Route.SetShouldHandleObstructionsEncountered(id, !(on && PuppetObstructionsOff));
                Route.SetAvoidanceFieldRangeScale(id, on ? PuppetAvoidanceScale : 1f);
                if (sim.SimRoutingComponent != null)
                {
                    if (on && PuppetFootprintOff)
                    {
                        sim.SimRoutingComponent.DisableDynamicFootprint();
                    }
                    else
                    {
                        sim.SimRoutingComponent.EnableDynamicFootprint();
                    }
                }

                if (!on)
                {
                    return;
                }

                // The other Sims on the lot are copies the stream places; the server's Sim already went round the real
                // ones, so the copy's route ignores them (walk.puppet-ignore-sims 0 turns it off). Not shown to matter
                // yet: social-chat passed with and without it, phone-seated failed once each way (2026-10-17); whether
                // the native steering's avoidance honours the list, or only the planner, is open.
                if (Tunables.Get("walk.puppet-ignore-sims", 1.0) > 0.5 && sim.LotCurrent != null)
                {
                    foreach (Sim other in Sims3.Gameplay.Queries.GetObjects<Sim>())
                    {
                        if (other != sim && other.LotCurrent == sim.LotCurrent)
                        {
                            route.AddObjectToIgnoreForRoute(other.ObjectId);
                            sPuppetRouteIgnores++;
                        }
                    }
                }

                route.SetOption(Route.RouteOption.BlockedByDynamicFootprints, false);
                route.SetOption(Route.RouteOption.BlockedByPeople, false);
                route.SetOption(Route.RouteOption.PushSimsAtDestination, false);
                route.SetOption(Route.RouteOption.AddPenaltyForSimsAtDestination, false);
                route.SetOption(Route.RouteOption.MakeDynamicObjectAdjustments, false);
                sPuppetRoutesConfigured++;
            }
            catch (Exception)
            {
                // The copy routes with the game's own options.
            }
        }

        internal static bool ReplayRouteEnd(string[] f)
        {
            ObjectGuid simId = PresentationApi.ReplayObject(ulong.Parse(f[2], CultureInfo.InvariantCulture));
            if (simId.IsValid && Vehicles.HoldsPosition(simId.Value))
            {
                string[] pending;
                if (sVehiclePendingRoutes.TryGetValue(simId.Value, out pending)
                    && RouteStreamIdentity.EndMatches(pending, f))
                {
                    sVehiclePendingRoutes.Remove(simId.Value); // This leg finished before the handoff back.
                }
                return true;
            }
            ClientRoute entry;
            if (!simId.IsValid || !sClientRoutes.TryGetValue(simId.Value, out entry))
            {
                return false;
            }
            if (f.Length > 6 && entry.SourceRoutes.Count > 0 && !entry.SourceRoutes.ContainsKey(f[6]))
            {
                return true; // An older route's end cannot end a new post-vehicle leg.
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            entry.ServerEnd = f.Length > 5 ? new Vector3(float.Parse(f[3], c), float.Parse(f[4], c), float.Parse(f[5], c)) : entry.Goal;
            entry.ServerEnded = true;
            entry.ServerEndedUtc = DateTime.UtcNow;
            long serverEndMs;
            entry.ServerEndMs = f.Length > 7 && long.TryParse(f[7], NumberStyles.Integer, c, out serverEndMs) ? serverEndMs : 0L;
            // What ended the server's walk (Rx's by: stop, inactive, arrived) and how long the line took to get here (one
            // wall clock on a dev host): a late "arrived" is the server's end check, a late arrival is the stream
            // (two-chairs 2026-10-21: the end reached both clients 2.7 s after the server's Sim sat, the sit-down cut short).
            string by = f.Length > 10 ? f[10] : "-";
            // The server's Stop call on this walk, if any (Rx's stopMode|accepted|stopMs): the mode, whether the route took
            // it, and how long before the end it came. A stopped Sim walks on to rest, and the end line is sent there.
            string stop = "";
            int stopMode;
            long stopMs;
            if (f.Length > 13 && int.TryParse(f[11], NumberStyles.Integer, c, out stopMode) && stopMode >= 0
                && long.TryParse(f[13], NumberStyles.Integer, c, out stopMs) && stopMs > 0L)
            {
                stop = ", stop " + ((StopMode)stopMode).ToString().Replace("kStop_", "") + (f[12] == "1" ? "" : " refused")
                    + (entry.ServerEndMs > 0L ? " " + (entry.ServerEndMs - stopMs).ToString(c) + " ms before" : "");
            }

            long late = entry.ServerEndMs > 0L ? DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond - entry.ServerEndMs : -1L;
            PresentationApi.Note(simId.Value, "server walk ended at " + entry.ServerEnd.x.ToString("F2", c) + "," + entry.ServerEnd.z.ToString("F2", c)
                + " (by " + by + stop + ", " + (late >= 0L ? late.ToString(c) + " ms ago" : "sent ? ms ago") + ")"
                + (entry.Running ? " (client still walking)" : ""));
            if (!entry.Running)
            {
                ReleaseRouteHold(simId.Value, entry); // this client's route already ended: snap now
                return true;
            }

            // A walking copy finishes the walk; ExpireClientRoutes stops one that takes longer than the server's own walk
            // did (WalkOverrun). Stopping the copy when the server's walk was stopped (38b0fde) was wrong: the end line
            // then came at the Stop call, while the server's Sim walked on to rest (two-hud 2026-10-21: 0.22 m twice,
            // 0.53 m to the goal), so the copy was pulled back to where that Sim never stayed.
            return true;
        }

        // How long a copy may walk past the server's own walk time before it stops, snaps to where the server's Sim
        // stopped and plays what it held: a quarter of that walk, at least WalkOverrunMin. A copy walks the server's path
        // in the server's walk style, so it takes about as long; one that doesn't is off the path (cold-test-055: a goal
        // this client planned differently) and the server's Sim is already on to its next action.
        // STOPGAP(boundary-recording): walk-overrun cuts a copy's late walk short and snaps it (the open 2.7-2.9 m snaps); the copy should keep the server's pace.
        private static TimeSpan WalkOverrunMin { get { return Tunables.Seconds("walk.overrun.min", 0.75); } }
        private static double WalkOverrunShare { get { return Tunables.Get("walk.overrun.share", 0.25); } }
        private static long sRoutesOverrun;

        private static bool WalkOverrun(ClientRoute entry, DateTime now)
        {
            if (!entry.ServerEnded)
            {
                return false;
            }

            if (entry.ServerStartMs <= 0L || entry.ServerEndMs <= entry.ServerStartMs)
            {
                return now - entry.ServerEndedUtc > WalkOverrunMin; // an older server: no walk times
            }

            TimeSpan walk = TimeSpan.FromMilliseconds(entry.ServerEndMs - entry.ServerStartMs);
            TimeSpan allowed = walk + TimeSpan.FromMilliseconds(Math.Max(WalkOverrunMin.TotalMilliseconds, walk.TotalMilliseconds * WalkOverrunShare));
            return now - entry.StartedUtc > allowed + TimeSpan.FromMilliseconds(entry.LeadMs); // the lead is not the walk's
        }

        // Every replay batch: stop client routes that outlived the server's (or the hold cap).
        internal static void ExpireClientRoutes()
        {
            if (sClientRoutes.Count == 0)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            foreach (KeyValuePair<ulong, ClientRoute> pair in sClientRoutes)
            {
                ClientRoute entry = pair.Value;
                if (entry.Running && !entry.StopRequested)
                {
                    ApplyRouteStyle(pair.Key, entry);
                }

                if (entry.Running && entry.StopRequested && entry.Current != null
                    && now - entry.StopRequestedUtc > (entry.ServerEnded ? ClientRouteServerEndedWedgeGrace : ClientRouteWedgeGrace))
                {
                    GiveUpClientRoute(pair.Key, entry);
                    continue;
                }

                if (!entry.Running || entry.Current == null || entry.StopRequested)
                {
                    continue;
                }

                if (entry.HaltPending)
                {
                    ApplyHalt(pair.Key, entry);
                    if (entry.StopRequested)
                    {
                        continue;
                    }
                }

                if (WalkOverrun(entry, now))
                {
                    PresentationApi.Note(pair.Key, "walk stop: longer than the server's walk (overrun)");
                    StopClientRoute(entry);
                    sRoutesOverrun++;
                    continue;
                }

                // Safety net for a lost end line: no walk in a lot takes this long.
                if (now - entry.StartedUtc > ClientRouteMaxHold)
                {
                    StopClientRoute(entry);
                }
            }
        }

        // Rh|sim|x|y|z|route|stopMs|mode|moved: the game stopped the server's walk (a Route.Stop it took). A stopped Sim
        // walks on before it rests, and how far depends on where in its walk the stop came: two-hud 2026-10-21, a Soft
        // stop before the first step played the start clip and stopped 0.22 m on (three runs), one after it walked on to
        // the goal (three runs), so a copy that only learnt of the stop at rest had either walked past or been pulled back.
        // The copy gets the same stop at the same point of its own walk (ApplyHalt). With the server's offset into the walk
        // (Rh's sinceMs / sinceTicks, WalkHaltTiming; the user, 2026-10-21) that point is the same time into the copy's
        // own walk; a copy that is behind holds the stop until then (WatchHalt, checked every frame), one that is past it
        // stops at once and is counted late. Without an offset (an older server, or a later leg: the copy walks every leg
        // as one plan, so it has no leg start to count from) the distance rule stays: at once when the server's Sim had
        // not stepped yet, else once the copy has walked as far. The end line (Rx, at rest) places what is left.
        internal static bool ReplayRouteHalt(string[] f)
        {
            if (f.Length < 10)
            {
                return false;
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            ObjectGuid simId = PresentationApi.ReplayObject(ulong.Parse(f[2], c));
            ClientRoute entry;
            int mode;
            float moved;
            if (!simId.IsValid || !sClientRoutes.TryGetValue(simId.Value, out entry) || !entry.Running
                || !int.TryParse(f[8], NumberStyles.Integer, c, out mode) || !float.TryParse(f[9], NumberStyles.Float, c, out moved))
            {
                return false;
            }

            entry.HaltPending = true;
            entry.HaltMode = (StopMode)mode;
            entry.HaltMoved = moved;
            entry.HaltOffset = entry.Legs == 0 ? WalkHaltTiming.Offset(f, HaltClockTicks) : -1L;
            string timing = "";
            if (entry.HaltOffset >= 0L)
            {
                long late = WalkHaltTiming.Late(CopyElapsed(entry), entry.HaltOffset);
                if (sRoutesHaltsTimed == 0 || late < sHaltLateMin)
                {
                    sHaltLateMin = late;
                }

                if (sRoutesHaltsTimed == 0 || late > sHaltLateMax)
                {
                    sHaltLateMax = late;
                }

                sRoutesHaltsTimed++;
                timing = ", " + entry.HaltOffset.ToString(c) + (HaltClockTicks ? " ticks" : " ms") + " into its walk, the copy "
                    + (late > 0L ? late.ToString(c) + " past it" : (-late).ToString(c) + " behind");
            }

            PresentationApi.Note(simId.Value, "server walk stopped " + entry.HaltMode.ToString().Replace("kStop_", "") + " after "
                + moved.ToString("F2", c) + " m" + (moved < HaltBeforeStep ? " (before its first step)" : "") + timing
                + (f.Length > 11 ? ", GetDistanceRemaining " + f[10] + " m, after the stop " + f[11] + " m" : ""));
            ApplyHalt(simId.Value, entry);
            if (entry.HaltPending && entry.HaltOffset >= 0L && entry.CopyStartMs > 0L)
            {
                sRoutesHaltsHeld++;
                WatchHalt(simId.Value, entry); // before DoRoute, the runner starts the watch
            }

            sRoutesHaltsReceived++;
            return true;
        }

        // Under this the server's Sim had not stepped yet when its walk was stopped (a walk tick moves 1.7-6 cm).
        private const float HaltBeforeStep = 0.01f; // WalkHaltTiming.BeforeStep
        private static long sRoutesHaltsReceived;
        private static long sRoutesHalts;
        // The offset rule (WalkHaltTiming): stops that came with one, held until the copy's walk reached it, applied on the
        // other side of the first step from the server's (phaseSplits), and how late Rh came against the copy's walk
        // (min/max, in the halt clock's unit; above 0 the copy was past the server's point).
        private static long sRoutesHaltsTimed;
        private static long sRoutesHaltsHeld;
        private static long sRoutesPhaseSplits;
        private static long sHaltLateMin;
        private static long sHaltLateMax;
        private static long sRoutesLeadWaits;
        // 0 = off. The lead every server walk's start waits on this client, so a stop comes while the copy is still behind.
        private static long StartLeadMs { get { return (long)Tunables.Get("walk.start-lead-ms", 0f); } }
        // 0 = ms (the machine clock), 1 = SimClock ticks: which of Rh's offsets the copy keeps.
        private static bool HaltClockTicks { get { return Tunables.Get("walk.halt-clock", 0f) > 0f; } }
        // A held stop's watch gives up after this many frames (about 20 s): the walk's own end or the watchdog take over.
        private const int HaltWatchFrames = 600;

        // How far the copy is into its own walk in the halt clock's unit, or -1 before its DoRoute.
        private static long CopyElapsed(ClientRoute entry)
        {
            if (entry.CopyStartMs <= 0L)
            {
                return -1L;
            }

            return HaltClockTicks ? SimClock.CurrentTicks - entry.CopyStartTicks
                : DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond - entry.CopyStartMs;
        }

        private sealed class HaltWatch
        {
            private readonly ulong mSim;
            private readonly int mGeneration;

            public HaltWatch(ulong sim, int generation)
            {
                mSim = sim;
                mGeneration = generation;
            }

            // Every frame until the copy reaches the server's point: the replay batches (ExpireClientRoutes) come about
            // every 50 ms, and a Soft stop's two outcomes are tens of ms apart (two-hud: 530 ms into the walk on the server
            // and 564 ms on one copy rested, 600 ms on the other copy walked on to the goal).
            public void Run()
            {
                for (int frame = 0; frame < HaltWatchFrames; frame++)
                {
                    ClientRoute entry;
                    if (!sClientRoutes.TryGetValue(mSim, out entry) || entry.Generation != mGeneration || !entry.HaltPending
                        || !entry.Running || entry.StopRequested)
                    {
                        return;
                    }

                    ApplyHalt(mSim, entry);
                    if (!entry.HaltPending)
                    {
                        return;
                    }

                    Simulator.Sleep(0);
                }
            }
        }

        private static void WatchHalt(ulong id, ClientRoute entry)
        {
            Simulator.AddObject(new OneShotFunctionTask(new Function(new HaltWatch(id, entry.Generation).Run)));
        }

        private static void ApplyHalt(ulong id, ClientRoute entry)
        {
            if (!entry.HaltPending || !entry.Running || entry.StopRequested || entry.Current == null)
            {
                return;
            }

            Sim sim = new ObjectGuid(id).ObjectFromId<Sim>();
            if (sim == null)
            {
                return;
            }

            float moved = (sim.Position - entry.StartPosition).Length();
            if (entry.HaltOffset >= 0L)
            {
                if (!WalkHaltTiming.Due(CopyElapsed(entry), entry.HaltOffset))
                {
                    return; // the copy is not yet as far into its walk as the server's Sim was at the call
                }
            }
            else if (entry.HaltMoved >= HaltBeforeStep && moved < entry.HaltMoved)
            {
                return; // the server's Sim had walked this far: so does the copy first
            }

            if (WalkHaltTiming.PhaseSplit(entry.HaltMoved, moved))
            {
                sRoutesPhaseSplits++; // the server's Sim stopped before its first step, the copy after its own
            }

            entry.HaltPending = false;
            Route route = entry.Current;
            float before = -1f;
            float after = -1f;
            try
            {
                before = route.GetDistanceRemaining();
            }
            catch (Exception)
            {
                // An unreadable distance stays -1 in the walk-stop note below.
            }

            StopClientRoute(entry, entry.HaltMode);
            try
            {
                after = route.GetDistanceRemaining();
            }
            catch (Exception)
            {
                // An unreadable distance stays -1 in the walk-stop note below.
            }

            PresentationApi.Note(id, "walk stop: the server's walk was stopped (" + entry.HaltMode.ToString().Replace("kStop_", "") + ", the copy "
                + moved.ToString("F2", CultureInfo.InvariantCulture) + " m on, GetDistanceRemaining " + before.ToString("F3", CultureInfo.InvariantCulture)
                + " m, after the stop " + after.ToString("F3", CultureInfo.InvariantCulture) + " m)");
            sRoutesHalts++;
        }

        private static void StopClientRoute(ClientRoute entry)
        {
            StopClientRoute(entry, StopMode.kStop_Hard);
        }

        private static void StopClientRoute(ClientRoute entry, StopMode mode)
        {
            try
            {
                entry.StopRequested = true;
                entry.StopRequestedUtc = DateTime.UtcNow;
                // With the mask cleared (ExitReasonsInterrupt = 0) a hard stop did not end DoRoute: on Client2Box a
                // route stayed running 218 s after its stop with 538 stream lines held behind it (cold-test-088).
                if (entry.ExitMask != -1)
                {
                    entry.Current.ExitReasonsInterrupt = entry.ExitMask;
                }

                entry.Current.Stop(mode);
                sRoutesStopped++;
            }
            catch (Exception)
            {
                // StopRequested is already set; a route that cannot be stopped is left to end.
            }
        }

        // A route still running this long after its stop is given up: its lines are released and the next route
        // starts a new runner. If the old runner ever returns, its generation no longer matches and it releases
        // nothing.
        // STOPGAP(boundary-recording): walk-wedge-grace gives up a walk whose hard stop did not end DoRoute (cold-test-088); find why the stop leaves it running.
        private static readonly TimeSpan ClientRouteWedgeGrace = TimeSpan.FromSeconds(8);
        // The server's Sim is already doing its next thing: a stop that has not ended the walk (a stand-and-wait runs on)
        // is given up sooner.
        private static readonly TimeSpan ClientRouteServerEndedWedgeGrace = TimeSpan.FromSeconds(3);
        private static long sRoutesWedged;
        private static long sRoutesLateReturns;

        private static void GiveUpClientRoute(ulong id, ClientRoute entry)
        {
            entry.Generation++;
            entry.Running = false;
            entry.Pending = false;
            entry.StopRequested = false;
            entry.Current = null;
            sRoutesWedged++;
            ReleaseRouteHold(id, entry);
            PresentationApi.ReleaseHeldLines(id);
        }

        private static void ReleaseRouteHold(ulong id, ClientRoute entry)
        {
            // Where the server's Sim stopped (Rte): the lines held behind the walk play next and do from there what the
            // server's Sim did (sit down, lie down). A place the server sent after its walk ended put the copy in the seat
            // before its sit-down played it there again (and the facing settle passed the copy's own mid-walk place in as
            // the newest). While the server still walks nothing snaps (cold-test-059): its end line does.
            if (!entry.ServerEnded)
            {
                return;
            }

            Vector3 target = entry.ServerEnd;

            try
            {
                Sim sim = new ObjectGuid(id).ObjectFromId<Sim>();
                float off = sim != null ? (sim.Position - target).Length() : 0f;
                if (sim != null && off > ClientRouteSnapDistance)
                {
                    string snapContext = PresentationApi.LocalContext;
                    PresentationApi.LocalContext = "walk end snap";
                    sim.SetPosition(target);
                    PresentationApi.LocalContext = snapContext;
                    sRoutesSnaps++;
                    CultureInfo ic = CultureInfo.InvariantCulture;
                    sLastSnap = DateTime.UtcNow.ToString("HH:mm:ss", ic) + "/" + id.ToString(ic) + "/" + off.ToString("0.00", ic) + "m";
                }
            }
            catch (Exception)
            {
                // The copy is gone or cannot be placed: it stays where it stopped.
            }
        }

        private static bool IsRouteHeldCore(ulong simObjectId, Vector3 target)
        {
            ClientRoute entry;
            if (!sClientRoutes.TryGetValue(simObjectId, out entry))
            {
                return false;
            }

            // Held while the copy walks, also after the server's walk ended: the copy finishes (or WalkOverrun stops
            // it) and its end places it. Moved mid-walk, the copy jumped 2.7 m while still walking (motion watch).
            if (!entry.Running || DateTime.UtcNow - entry.StartedUtc > ClientRouteMaxHold)
            {
                return false; // applies now (no client route runs here)
            }

            sRoutesHeldApplies++;
            return true;
        }
    }
}
