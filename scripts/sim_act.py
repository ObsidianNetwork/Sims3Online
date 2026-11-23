"""Make a Sim do one thing on the server, reliably, and say why when it can't (live testing).

    python scripts/sim_act.py Jennifer inv:PhoneSmart BrowseTheWeb --clear --hold
    python scripts/sim_act.py Pablo near:Bookshelf Read --clear --hold --name "Read Something"
    python scripts/sim_act.py Jennifer sim:Pablo SocialInteractionA --name "Chat"
    python scripts/sim_act.py Pablo self ChangeOutfit --list          # what the target offers, with each test result
    python scripts/sim_act.py --hold Jennifer,Pablo                     # autonomy off for both, nothing picked
    python scripts/sim_act.py --release all                             # autonomy back
    python scripts/sim_act.py Pablo --clear                             # empty his queue, pick nothing
    python scripts/sim_act.py --needs static:Jennifer,Pablo             # needs full and not decaying (tests)
    python scripts/sim_act.py --needs restore:Jennifer,Pablo            # needs decay again
    python scripts/sim_act.py Pablo --move 2,0,90 --endpoint 127.0.0.1:34030   # push the client's copy off (mirror tests)
    python scripts/sim_act.py Jennifer --fire start:2.5                 # a fire 2.5 m in front of her (world-spawn tests)
    python scripts/sim_act.py Pablo --place 1127.4,988.2,0.34,0.94      # put him there, facing (fx, fz) (scenario starts)
    python scripts/sim_act.py Pablo --own-machines --endpoint 127.0.0.1:34030   # the copy's own posture machines, live or dead
    python scripts/sim_act.py Pablo --clear --stand                     # empty the queue, then the game's own stand-up
    python scripts/sim_act.py --fire out                                # every fire out, now
    python scripts/sim_act.py --repair all                              # every broken object repaired, now

Target: self | sim:<FirstName> | near:<TypeFragment> (nearest within 30 m) | inv:<TypeFragment> (the actor's
inventory) | any:<TypeFragment> (near, then inventory) | id:<ObjectId> (one object) | lot:<lot id>|home|nearest (a lot; its candidates add the
game's own walk there, GoToLot: `Pablo lot:nearest GoToLot`). The interaction is a fragment of its definition's type name
(BrowseTheWeb, Fridge_Have, BedSleep), optionally narrowed by --name (the pie-menu text); a social is
social:<action key> on a sim: target ("social:Chat", "social:Friendly Hug" quoted).

--clear cancels the actor's queue first (what keeps a scripted pick from running: autonomy's reading, a social).
--hold turns autonomy off for the actor (or --hold-sims) until --release, with the game's own counter
(Autonomy.IncrementAutonomyDisabled), so nothing it picks for itself takes over the test. Holds survive probe runs
(AppDomain data) and end with the server TS3.

--needs max|static|restore[:FirstName,...] (default: the actor): max fills every motive (Motives.MaxEverything);
static fills them and stops their decay (the "make needs static" cheat: FreezeDecayEverythingExcept), so a long test
run can't starve, exhaust or kill a Sim; restore lets them decay again (RestoreDecays). Static lasts until restore or a
TS3 restart.

--move dx,dz[,degrees] places the actor dx, dz metres away and turns it, in whichever game --endpoint names: on the
client it makes a copy diverge from the server's Sim, to test how the presentation corrects it (motion_watch).

--place x,z[,fx,fz] puts the actor at that point (its floor height kept) and facing, in the --endpoint game: a scenario
starts from the same spots every run (scenarios.py home).

--own-machines lists the actor's own posture machines (the posture chain and its standing posture): each machine's
driver handle (hex, as SACS_RECORDER actor prints in decimal), name, and whether its driver is still alive. A driver the
recorder lists that is not here is the replay's; one listed here dead animates nothing.

--stand queues the game's own stand-up for the actor's posture (Posture.GetStandingTransition) when it is not standing:
a Sim stays seated after its Sit finishes, and --clear does not change that; a scenario reset that only teleported a
seated Sim left the seat taken (phone-seated BLOCKED after late-join-seated). With --clear it waits for the queue to empty.
Any read of an actor that is not standing says posture=<Posture>, with or without --stand, so a read can check what a
stand-up left without queuing another.

--fire start[:metres] starts a fire that far in front of the actor (default 2.5; Fire.CreateFire, as the game's own
fires: the lot's emergency, alarms and spreading follow), --fire out puts every fire out at once (ExtinguishFireNow). For
testing objects the server's game spawns into the world; put it out within seconds, a fire spreads and burns Sims.
--repair all repairs every broken object (RepairableComponent.ForceRepaired, the game's own end of a repair): a shower
that breaks on use (TakeShower's UpdateBreakage) fails TakeShower's test from then on, and every later shower scenario
was BLOCKED (2026-10-15/17).
--booths off stops every DJ booth that plays or has a DJ (StopMusic, TurnOff, no owner): a booth saved mid-set loads
playing, pulls Sims into crowd interactions (sleep-bed BLOCKED behind DJTurntable+BooDJ) and refuses DoPlay
(2026-10-31). The scenario reset runs it with --fire out.

The pick goes through the pie menu's path (UserDirected parameters, the definition's Test, then the queue on a
simulator task). Every candidate's test result and greyed-out reason is reported, and with --wait the script polls
the queue until the pick runs, or reports what it is stuck behind.
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
import time
from pathlib import Path

if __package__:
    from .local_endpoints import SERVER_BRIDGECTL
else:
    from local_endpoints import SERVER_BRIDGECTL

ROOT = Path(__file__).resolve().parent.parent
SERVER = SERVER_BRIDGECTL
PROBE = ROOT / ".omo" / "run" / "act" / "SimActProbe.cs"

TEMPLATE = r'''using System;
using System.Collections.Generic;
using System.Text;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.SimIFace;

namespace SimsOnline.Scratch
{
    // Generated by scripts/sim_act.py (do not edit): one scripted pick on this TS3, with the options asked for.
    public class SimActProbe
    {
        private const string ActorName = "__ACTOR__";
        private const string TargetSpec = "__TARGET__";
        private const string TypeFragment = "__INTERACTION__";
        private const string NameFragment = "__NAME__";
        private const bool Clear = __CLEAR__;
        private const bool List = __LIST__;
        private const string HoldSims = "__HOLD__";       // first names to hold autonomy off for ("" none)
        private const string ReleaseSims = "__RELEASE__"; // first names to release, or "all"
        private const string Needs = "__NEEDS__";         // "max|static|restore[:first names]" or "" (motives)
        private const string Move = "__MOVE__";           // "dx,dz[,degrees]": place the actor off by that much
        private const string FireOp = "__FIRE__";         // "start[:metres]" (in front of the actor) or "out"
        private const string RepairOp = "__REPAIR__";     // "all": every broken object repaired
        private const string BoothsOp = "__BOOTHS__";     // "off": every DJ booth that plays or has a DJ stopped
        private const string Place = "__PLACE__";         // "x,z[,fx,fz]": put the actor there, facing that way
        private const string PlaceAll = "__PLACEALL__";   // "Name=x,z,fx,fz;Name=...": put each there (no actor needed)
        private const bool OwnMachines = __OWN__;         // report the actor's own posture machines
        private const bool Stand = __STAND__;             // queue the posture's own stand-up when not standing
        private const string HoldKey = "SimsOnline.SimActHolds";

        private static InteractionInstance sInstance;
        private static Sim sActor;

        private static void AddLater()
        {
            if (sActor == null || sInstance == null)
            {
                return;
            }

            // --clear: let the cancelled interactions finish exiting first (a phone being put away), or the pick fails its
            // run-time test and drops out of the queue. About 20 s at most.
            for (int i = 0; Clear && i < 600 && sActor.InteractionQueue.Count > 0; i++)
            {
                Sims3.SimIFace.Simulator.Sleep(1u);
            }

            sActor.InteractionQueue.Add(sInstance);
            sInstance = null; // the probe is reused between runs (LoadAndRun): never add one pick twice
        }

        private static Sim FindSim(string first)
        {
            Sim any = null;
            foreach (Sim sim in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                if (sim.SimDescription == null || !string.Equals(sim.FirstName, first, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (sim.Household != null && sim.Household == Household.ActiveHousehold)
                {
                    return sim;
                }

                if (any == null)
                {
                    any = sim;
                }
            }

            return any;
        }

        private static Dictionary<ulong, bool> Holds()
        {
            Dictionary<ulong, bool> holds = AppDomain.CurrentDomain.GetData(HoldKey) as Dictionary<ulong, bool>;
            if (holds == null)
            {
                holds = new Dictionary<ulong, bool>();
                AppDomain.CurrentDomain.SetData(HoldKey, holds);
            }

            return holds;
        }

        private static void Hold(string names, StringBuilder sb)
        {
            List<string> done = new List<string>();
            Dictionary<ulong, bool> holds = Holds();
            foreach (string name in names.Split(','))
            {
                Sim sim = name.Length == 0 ? null : FindSim(name.Trim());
                if (sim == null || sim.Autonomy == null)
                {
                    continue;
                }

                if (!holds.ContainsKey(sim.ObjectId.Value))
                {
                    sim.Autonomy.IncrementAutonomyDisabled();
                    holds[sim.ObjectId.Value] = true;
                }

                done.Add(sim.FirstName);
            }

            sb.Append(";held=").Append(done.Count == 0 ? "-" : string.Join(",", done.ToArray()));
        }

        private static void Release(string names, StringBuilder sb)
        {
            List<string> done = new List<string>();
            Dictionary<ulong, bool> holds = Holds();
            List<ulong> ids = new List<ulong>(holds.Keys);
            foreach (ulong id in ids)
            {
                Sim sim = new ObjectGuid(id).ObjectFromId<Sim>();
                bool named = names == "all";
                if (!named && sim != null)
                {
                    foreach (string name in names.Split(','))
                    {
                        named |= string.Equals(name.Trim(), sim.FirstName, StringComparison.OrdinalIgnoreCase);
                    }
                }

                if (!named)
                {
                    continue;
                }

                if (sim != null && sim.Autonomy != null)
                {
                    sim.Autonomy.DecrementAutonomyDisabled();
                    done.Add(sim.FirstName);
                }

                holds.Remove(id);
            }

            sb.Append(";released=").Append(done.Count == 0 ? "-" : string.Join(",", done.ToArray()));
        }

        private static GameObject FindTarget(Sim actor, StringBuilder sb)
        {
            string spec = TargetSpec;
            int colon = spec.IndexOf(':');
            string kind = colon < 0 ? spec : spec.Substring(0, colon);
            string arg = colon < 0 ? "" : spec.Substring(colon + 1);
            if (kind == "self")
            {
                return actor;
            }

            if (kind == "sim")
            {
                return FindSim(arg);
            }

            if (kind == "id")
            {
                ulong id;
                return ulong.TryParse(arg, out id) ? new ObjectGuid(id).ObjectFromId<GameObject>() : null;
            }

            if (kind == "lot")
            {
                Sims3.Gameplay.Core.Lot lot = FindLot(actor, arg);
                if (lot == null)
                {
                    sb.Append(";err=no lot ").Append(arg);
                }

                return lot;
            }

            if (kind == "near" || kind == "any")
            {
                GameObject best = null;
                float bestDistance = float.MaxValue;
                foreach (GameObject obj in Sims3.Gameplay.Queries.GetObjects<GameObject>(actor.Position, 30f))
                {
                    if (obj == actor || obj.GetType().FullName.IndexOf(arg, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    float d = (obj.Position - actor.Position).LengthSqr();
                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        best = obj;
                    }
                }

                if (best != null || kind == "near")
                {
                    return best;
                }
            }

            if ((kind == "inv" || kind == "any") && actor.Inventory != null)
            {
                foreach (GameObject item in actor.Inventory.FindAll<GameObject>(false))
                {
                    if (item.GetType().FullName.IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return item;
                    }
                }
            }

            sb.Append(";err=no target ").Append(spec);
            return null;
        }

        // lot:<lot id> | lot:home (the actor's home lot) | lot:nearest (the nearest lot with a household other than the
        // one the actor stands on, by the lots' centres): a scenario takes a Sim to another lot with no coordinates.
        private static Sims3.Gameplay.Core.Lot FindLot(Sim actor, string arg)
        {
            if (arg == "home")
            {
                return actor.LotHome;
            }

            ulong lotId;
            if (ulong.TryParse(arg, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out lotId))
            {
                return Sims3.Gameplay.Core.LotManager.GetLot(lotId);
            }

            if (arg != "nearest")
            {
                return null;
            }

            Sims3.Gameplay.Core.Lot best = null;
            float bestDistance = float.MaxValue;
            foreach (object item in Sims3.Gameplay.Core.LotManager.AllLots)
            {
                Sims3.Gameplay.Core.Lot lot = item as Sims3.Gameplay.Core.Lot;
                if (lot == null || lot.IsWorldLot || lot == actor.LotCurrent || lot == actor.LotHome || lot.Household == null)
                {
                    continue;
                }

                float d = (lot.GetCenterPosition() - actor.Position).LengthSqr();
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = lot;
                }
            }

            return best;
        }

        private static string Short(string type)
        {
            int dot = type.LastIndexOf('.');
            return (dot < 0 ? type : type.Substring(dot + 1)).Replace(";", ",");
        }

        // --needs: fill every motive (max), also stop their decay (static), or let them decay again (restore).
        private static void SetNeeds(string spec, StringBuilder sb)
        {
            int colon = spec.IndexOf(':');
            string mode = colon < 0 ? spec : spec.Substring(0, colon);
            string names = colon < 0 ? ActorName : spec.Substring(colon + 1);
            List<string> done = new List<string>();
            foreach (string name in names.Split(','))
            {
                Sim sim = name.Trim().Length == 0 ? null : FindSim(name.Trim());
                if (sim == null || sim.Motives == null)
                {
                    continue;
                }

                if (mode == "restore")
                {
                    sim.Motives.RestoreDecays();
                }
                else
                {
                    sim.Motives.MaxEverything();
                    if (mode == "static")
                    {
                        sim.Motives.FreezeDecayEverythingExcept(true);
                    }
                }

                done.Add(sim.FirstName);
            }

            sb.Append(";needs=").Append(mode).Append(':').Append(done.Count == 0 ? "-" : string.Join(",", done.ToArray()));
        }

        private static string Describe(string label, StateMachineClient machine)
        {
            if (machine == null)
            {
                return label + ":-";
            }

            // The driver handle in decimal, as SACS_RECORDER actor lists it (private field; ToString shows only the name).
            System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            System.Reflection.FieldInfo driver = typeof(StateMachineClient).GetField("mDriver", flags);
            object wrapper = driver == null ? null : driver.GetValue(machine);
            System.Reflection.FieldInfo handle = wrapper == null ? null : wrapper.GetType().GetField("mHandle", flags);
            string id = handle == null ? "?" : ((uint)handle.GetValue(wrapper)).ToString();
            return label + ":" + machine.Name + "@" + id + (machine.IsValid ? ":alive" : ":DEAD");
        }

        public static string Run()
        {
            StringBuilder sb = new StringBuilder("ok=true");
            try
            {
                if (ReleaseSims.Length > 0)
                {
                    Release(ReleaseSims, sb);
                }

                if (HoldSims.Length > 0)
                {
                    Hold(HoldSims, sb);
                }

                if (Needs.Length > 0)
                {
                    SetNeeds(Needs, sb);
                }

                if (PlaceAll.Length > 0)
                {
                    // Every Sim of a scenario start in one call (scenarios.py reset: one call per game, not one per Sim).
                    System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
                    // One field each: the bridge keeps one value per field, so a placed= per Sim showed only the last.
                    string placed = "", missing = "";
                    foreach (string spot in PlaceAll.Split(';'))
                    {
                        string[] kv = spot.Split('=');
                        Sim who = kv.Length == 2 ? FindSim(kv[0]) : null;
                        if (who == null)
                        {
                            missing += (missing.Length > 0 ? "," : "") + kv[0];
                            continue;
                        }

                        string[] q = kv[1].Split(',');
                        float px = float.Parse(q[0], inv), pz = float.Parse(q[1], inv);
                        // The ground floor's height at the spot, not the Sim's own: a Sim back from another lot kept
                        // that lot's height and stood 3.8 m above the home floor on every game (, 2026-10-21).
                        who.SetPosition(new Vector3(px, World.GetLevelHeight(px, pz, 0), pz));
                        if (q.Length > 3)
                        {
                            who.SetForward(new Vector3(float.Parse(q[2], inv), 0f, float.Parse(q[3], inv)));
                        }

                        placed += (placed.Length > 0 ? "," : "") + who.FirstName;
                    }

                    if (placed.Length > 0)
                    {
                        sb.Append(";placed=").Append(placed);
                    }

                    if (missing.Length > 0)
                    {
                        sb.Append(";placeMissing=").Append(missing);
                    }
                }

                if (FireOp == "out")
                {
                    int put = 0;
                    foreach (Sims3.Gameplay.Objects.Fire fire in Sims3.Gameplay.Queries.GetObjects<Sims3.Gameplay.Objects.Fire>())
                    {
                        fire.ExtinguishFireNow(true);
                        put++;
                    }

                    sb.Append(";firesOut=").Append(put);
                }

                if (BoothsOp == "off")
                {
                    int stopped = 0;
                    foreach (Sims3.Gameplay.Objects.PerformanceObjects.DJTurntable booth in Sims3.Gameplay.Queries.GetObjects<Sims3.Gameplay.Objects.PerformanceObjects.DJTurntable>())
                    {
                        if (booth.DJOwnerSim != null || booth.IsPlayingMusic())
                        {
                            booth.StopMusic();
                            booth.TurnOff();
                            booth.DJOwnerSim = null;
                            stopped++;
                        }
                    }

                    sb.Append(";boothsOff=").Append(stopped);
                }

                if (RepairOp == "all")
                {
                    int fixedCount = 0;
                    foreach (Sims3.Gameplay.Abstracts.GameObject obj in Sims3.Gameplay.Queries.GetObjects<Sims3.Gameplay.Abstracts.GameObject>())
                    {
                        if (obj.Repairable != null && obj.Repairable.Broken)
                        {
                            obj.Repairable.ForceRepaired(null);
                            fixedCount++;
                        }
                    }

                    sb.Append(";repaired=").Append(fixedCount);
                }

                if (ActorName.Length == 0)
                {
                    return sb.ToString();
                }

                Sim actor = FindSim(ActorName);
                if (actor == null)
                {
                    return sb.Append(";err=no Sim named ").Append(ActorName).ToString();
                }

                if (OwnMachines)
                {
                    List<string> own = new List<string>();
                    Sims3.Gameplay.ActorSystems.Posture posture = actor.Posture;
                    for (int depth = 0; posture != null && depth < 6; depth++)
                    {
                        own.Add(Describe(posture.GetType().Name, posture.CurrentStateMachine));
                        posture = posture.PreviousPosture;
                    }

                    if (actor.Standing != null)
                    {
                        own.Add(Describe("Standing", actor.Standing.CurrentStateMachine));
                    }

                    sb.Append(";own=").Append(string.Join(" ", own.ToArray()));
                }

                if (Place.Length > 0)
                {
                    string[] q = Place.Split(',');
                    System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
                    float px = float.Parse(q[0], inv), pz = float.Parse(q[1], inv);
                    Vector3 to = new Vector3(px, World.GetLevelHeight(px, pz, 0), pz);  // the ground floor there (as --place-all)
                    actor.SetPosition(to);
                    if (q.Length > 3)
                    {
                        actor.SetForward(new Vector3(float.Parse(q[2], inv), 0f, float.Parse(q[3], inv)));
                    }

                    sb.Append(";placed=").Append(to.x.ToString("F2")).Append(',').Append(to.z.ToString("F2"));
                }

                if (FireOp.StartsWith("start"))
                {
                    float metres = FireOp.Length > 6 ? float.Parse(FireOp.Substring(6), System.Globalization.CultureInfo.InvariantCulture) : 2.5f;
                    Vector3 at = actor.Position + actor.ForwardVector * metres;
                    LotLocation location = LotLocation.Invalid;
                    ulong lotId = World.GetLotLocation(at, ref location);
                    Sims3.Gameplay.Objects.Fire fire = lotId == 0uL ? null : Sims3.Gameplay.Objects.Fire.CreateFire(lotId, location);
                    sb.Append(";fire=").Append(fire == null ? "none (no lot, or the tile cannot burn)" : fire.ObjectId.Value.ToString())
                        .Append('@').Append(at.x.ToString("F2")).Append(',').Append(at.z.ToString("F2"));
                }

                if (Move.Length > 0)
                {
                    string[] m = Move.Split(',');
                    float dx = float.Parse(m[0], System.Globalization.CultureInfo.InvariantCulture);
                    float dz = m.Length > 1 ? float.Parse(m[1], System.Globalization.CultureInfo.InvariantCulture) : 0f;
                    Vector3 to = actor.Position + new Vector3(dx, 0f, dz);
                    actor.SetPosition(to);
                    if (m.Length > 2)
                    {
                        double turn = double.Parse(m[2], System.Globalization.CultureInfo.InvariantCulture) * Math.PI / 180.0;
                        Vector3 f = actor.ForwardVector;
                        actor.SetForward(new Vector3((float)(f.x * Math.Cos(turn) - f.z * Math.Sin(turn)), 0f, (float)(f.x * Math.Sin(turn) + f.z * Math.Cos(turn))));
                    }

                    sb.Append(";moved=").Append(to.x.ToString("F2")).Append(',').Append(to.z.ToString("F2"));
                }

                InteractionInstance running = actor.InteractionQueue.GetCurrentInteraction();
                sb.Append(";actor=").Append(actor.FirstName).Append(";actorId=").Append(actor.ObjectId.Value)
                    .Append(";was=").Append(running == null ? "-" : Short(running.GetType().FullName))
                    .Append(";queued=").Append(actor.InteractionQueue.Count)
                    .Append(";autonomyOff=").Append(actor.Autonomy != null && actor.Autonomy.AutonomyDisabled ? "1" : "0")
                    // Where the actor stands: a walk to a bathroom or bedroom that stops in front of the door leaves it
                    // in the hall's room (2026-10-17).
                    .Append(";room=").Append(actor.RoomId)
                    .Append(";at=").Append(actor.Position.x.ToString("F2")).Append(',').Append(actor.Position.z.ToString("F2"))
                    // How far above its own floor the actor stands: a Sim back from another lot kept that lot's height,
                    // 3.8 m above the home floor, and an x,z check did not see it (, 2026-10-21).
                    .Append(";above=").Append((actor.Position.y - World.GetLevelHeight(actor.Position.x, actor.Position.z,
                        actor.Level)).ToString("F2"));
                if (TypeFragment.Length == 0)
                {
                    if (Clear && actor.InteractionQueue.Count > 0)
                    {
                        actor.InteractionQueue.CancelAllInteractions(); // --clear alone: an empty queue, nothing picked
                        sb.Append(";cleared=1");
                    }

                    if (actor.Posture != null && actor.Posture != actor.Standing)
                    {
                        // The posture alone, with no stand-up asked for: the reset reads what its own stand-up left.
                        sb.Append(";posture=").Append(actor.Posture.GetType().Name);
                    }

                    if (Stand && actor.Posture != null && actor.Posture != actor.Standing)
                    {
                        // The game's own way off a seat, bed or other posture (a walk would route through it too).
                        InteractionInstance stand = actor.Posture.GetStandingTransition();
                        if (stand != null)
                        {
                            sInstance = stand;
                            sActor = actor;
                            Sims3.SimIFace.Simulator.AddObject(new Sims3.Gameplay.OneShotFunctionTask(new Sims3.Gameplay.Function(AddLater)));
                        }

                        sb.Append(";stand=").Append(stand == null ? "none:" : "queued:").Append(actor.Posture.GetType().Name);
                    }

                    return sb.ToString();
                }

                GameObject target = FindTarget(actor, sb);
                if (target == null)
                {
                    return sb.ToString();
                }

                sb.Append(";target=").Append(target.GetType().Name).Append(":").Append(target.ObjectId.Value)
                    .Append(target.InInventory ? "(inventory)" : "");
                List<InteractionObjectPair> iops;
                if (TypeFragment.StartsWith("social:"))
                {
                    // A social is built from its action key, as the game's own code does (Door, careers: new
                    // SocialInteractionA.Definition("Chat", ...)); the pie menu's social items are not in the Sim's list.
                    iops = new List<InteractionObjectPair>();
                    iops.Add(new InteractionObjectPair(new Sims3.Gameplay.Socializing.SocialInteractionA.Definition(
                        TypeFragment.Substring(7), new string[0], null, false), target));
                }
                else
                {
                    iops = target.InInventory ? target.GetAllInventoryInteractionsForActor(actor) : target.GetAllInteractionsForActor(actor);
                    if (target is Sims3.Gameplay.Core.Lot)
                    {
                        // A walk to a lot: GoToLot is a meta definition no pie menu lists; the game queues it itself
                        // (GoToLot.Singleton.CreateInstance in DaycareToddlerTransportSituation, GhostHunter). It routes
                        // to the lot's front door or mailbox (GoToLot.Run).
                        iops.Add(new InteractionObjectPair(Sims3.Gameplay.Core.GoToLot.Singleton, target));
                    }
                }
                List<string> offered = new List<string>();
                List<string> tried = new List<string>();
                InteractionObjectPair chosen = null;
                string chosenName = "";
                InteractionInstanceParameters chosenParameters = new InteractionInstanceParameters();
                foreach (InteractionObjectPair iop in iops)
                {
                    if (iop.InteractionDefinition == null)
                    {
                        continue;
                    }

                    string type = iop.InteractionDefinition.GetType().FullName;
                    if (!offered.Contains(Short(type)))
                    {
                        offered.Add(Short(type));
                    }

                    if (!TypeFragment.StartsWith("social:") && type.IndexOf(TypeFragment, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    string name = "";
                    try
                    {
                        name = iop.InteractionDefinition.GetInteractionName(actor, iop.Target, iop) ?? "";
                    }
                    catch (Exception)
                    {
                    }

                    if (NameFragment.Length > 0 && name.IndexOf(NameFragment, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    InteractionInstanceParameters parameters = new InteractionInstanceParameters(iop, actor,
                        new InteractionPriority(InteractionPriorityLevel.UserDirected), AutonomySearchType.None,
                        InteractionFlags.UserDirected, GameObjectHit.NoHit, null, false);
                    GreyedOutTooltipCallback why = null;
                    InteractionTestResult result = iop.InteractionDefinition.Test(ref parameters, ref why);
                    string reason = "";
                    try
                    {
                        reason = why == null ? "" : (why() ?? "");
                    }
                    catch (Exception)
                    {
                    }

                    tried.Add(Short(type) + " '" + name.Replace(";", ",") + "' " + result + (reason.Length > 0 ? " (" + reason.Replace(";", ",") + ")" : ""));
                    if (chosen == null && result == InteractionTestResult.Pass)
                    {
                        chosen = iop;
                        chosenName = name;
                        chosenParameters = parameters;
                    }
                }

                sb.Append(";offered=").Append(offered.Count);
                if (List || chosen == null)
                {
                    sb.Append("\ncandidates: ").Append(tried.Count == 0 ? "none of type " + TypeFragment : string.Join(" | ", tried.ToArray()));
                    if (tried.Count == 0)
                    {
                        sb.Append("\noffered: ").Append(string.Join(", ", offered.ToArray()));
                    }
                }

                if (List)
                {
                    return sb.ToString();
                }

                if (chosen == null)
                {
                    return sb.Replace("ok=true", "ok=false", 0, 7).Append(tried.Count == 0 ? "\nerr=no such interaction" : "\nerr=no candidate passes its test").ToString();
                }

                if (Clear)
                {
                    actor.InteractionQueue.CancelAllInteractions();
                    sb.Append(";cleared=1");
                }

                sInstance = chosen.InteractionDefinition.CreateInstanceFromParameters(ref chosenParameters);
                sActor = actor;
                // A bridge command runs inside the gate's server-apply window; a real click does not. Add on a simulator
                // task, outside the window, like the pie menu's selection handler.
                Sims3.SimIFace.Simulator.AddObject(new Sims3.Gameplay.OneShotFunctionTask(new Sims3.Gameplay.Function(AddLater)));
                return sb.Append(";picked=").Append(Short(chosen.InteractionDefinition.GetType().FullName)).Append(";name=")
                    .Append(chosenName.Replace(";", ",")).Append(";scheduled=1").ToString();
            }
            catch (Exception ex)
            {
                return "ok=false;err=" + ex.GetType().Name + ":" + (ex.InnerException != null ? ex.InnerException.Message : ex.Message).Replace(";", ",") + ";" + sb;
            }
        }
    }
}
'''


def cs_string(text: str) -> str:
    return text.replace("\\", "\\\\").replace('"', '\\"')


def run_probe(endpoint: str, values: dict[str, str]) -> str:
    source = TEMPLATE
    for key, value in values.items():
        source = source.replace(f"__{key}__", value)
    import probe_source
    probe = probe_source.write(PROBE, source)  # its own file: a concurrent call cannot overwrite it before it compiles
    out = subprocess.run([sys.executable, "scripts/bridgectl.py", "--endpoint", endpoint, "probe", str(probe.relative_to(ROOT))],
                         cwd=ROOT, capture_output=True, text=True, timeout=300)
    text = out.stdout + out.stderr
    body = text.split("-- DIAG_RUN --", 1)[-1]
    return body


def report_fields(body: str) -> dict[str, str]:
    fields: dict[str, str] = {}
    for line in body.splitlines():
        m = re.match(r"\s+(\w+)\s+=\s+(.*)$", line)
        if m:
            fields[m.group(1)] = m.group(2).strip()
    return fields


def queue_head(endpoint: str, sim_id: str) -> tuple[str, str]:
    out = subprocess.run([sys.executable, "scripts/bridgectl.py", "--endpoint", endpoint, "DIAG_QUEUE", sim_id],
                         cwd=ROOT, capture_output=True, text=True, timeout=60).stdout
    head = next((line for line in out.splitlines() if line.startswith("[0]")), "")
    m = re.search(r"type=([^;]+)", head)
    return (m.group(1).split(".")[-1] if m else "-"), ("RUNNING" if "RUNNING" in head else "queued" if head else "empty")


def game_tick(endpoint: str) -> str | None:
    """The simulator's tick (DIAG_STATE tick=) of the game at endpoint, in process; None when it does not answer."""
    import run_watchdog
    return (run_watchdog.probe_state(endpoint)[0] or {}).get("tick")


def not_running_hint(state: str, tick_before: str | None, tick_after: str | None) -> str:
    """Why a pick did not start over the wait. A paused game runs no queue at all: on 2026-10-31 the whole stack read
    Pause for an hour and this said "still behind the current interaction", which sent the debugging the wrong way."""
    if tick_before is not None and tick_before == tick_after:
        return f"the game is paused (tick did not move, {tick_before}): resume it (simctl.py speed normal)"
    if state == "empty":
        return "the pick left the queue: it failed when it came to run (the target busy or in use); see --list"
    return "still behind the current interaction: add --clear, and --hold so autonomy can't take over"


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("actor", nargs="?", default="", help="first name of the Sim that acts")
    p.add_argument("target", nargs="?", default="self", help="self | sim:Name | near:Type | inv:Type | any:Type | id:ObjectId | lot:<id>|home|nearest")
    p.add_argument("interaction", nargs="?", default="", help="fragment of the interaction definition's type name")
    p.add_argument("--name", default="", help="fragment of the pie-menu text")
    p.add_argument("--clear", action="store_true", help="cancel the actor's queue first")
    p.add_argument("--hold", nargs="?", const="@actor", default="", help="autonomy off for the actor, or these first names (comma)")
    p.add_argument("--release", default="", help="autonomy back for these first names (comma), or 'all'")
    p.add_argument("--needs", default="", help="max|static|restore[:first names]: fill motives, also freeze them, or let them decay")
    p.add_argument("--move", default="", help="dx,dz[,degrees]: place the actor off by that much (in the --endpoint game)")
    p.add_argument("--fire", default="", help="start[:metres] in front of the actor, or out (every fire)")
    p.add_argument("--booths", default="", help="off: every playing DJ booth stopped")
    p.add_argument("--repair", default="", choices=["", "all"], help="all: repair every broken object")
    p.add_argument("--place-all", default="", help="Name=x,z,fx,fz;Name=...: put each Sim there, facing so (one call; no actor)")
    p.add_argument("--place", default="", help="x,z[,fx,fz]: put the actor there, facing (fx, fz), in the --endpoint game")
    p.add_argument("--own-machines", action="store_true", help="list the actor's own posture machines (handle, alive or dead)")
    p.add_argument("--stand", action="store_true", help="queue the game's own stand-up when the actor is not standing")
    p.add_argument("--list", action="store_true", help="only list the candidates and their test results")
    p.add_argument("--wait", type=float, default=45.0, help="seconds to wait for the pick to run (0: don't wait)")
    p.add_argument("--endpoint", default=SERVER)
    a = p.parse_args(argv)

    hold = a.actor if a.hold == "@actor" else a.hold
    # No interaction: only the actor's state (current interaction, queue, autonomy) and any hold/release.
    values = {
        "ACTOR": cs_string(a.actor), "TARGET": cs_string(a.target), "INTERACTION": cs_string(a.interaction),
        "NAME": cs_string(a.name), "CLEAR": "true" if a.clear else "false", "LIST": "true" if a.list else "false",
        "HOLD": cs_string(hold), "RELEASE": cs_string(a.release), "NEEDS": cs_string(a.needs), "MOVE": cs_string(a.move), "FIRE": cs_string(a.fire), "REPAIR": cs_string(a.repair), "BOOTHS": cs_string(a.booths), "PLACE": cs_string(a.place), "PLACEALL": cs_string(a.place_all), "OWN": "true" if a.own_machines else "false", "STAND": "true" if a.stand else "false",
    }
    body = run_probe(a.endpoint, values)
    fields = report_fields(body)
    summary = " ".join(f"{k}={v}" for k, v in fields.items() if k in ("actor", "was", "queued", "autonomyOff", "room", "at", "target", "picked", "name", "cleared", "held", "released", "needs", "moved", "fire", "firesOut", "boothsOff", "repaired", "placed", "placeMissing", "own", "posture", "stand", "err"))
    print(summary or body.strip()[-800:])
    for line in body.splitlines():
        if line.strip().startswith(("candidates:", "offered:", "err=")):
            print("  " + line.strip())
    if fields.get("ok") != "true" or not fields.get("picked") or a.wait <= 0:
        return 0 if fields.get("ok") == "true" else 1

    fragment = "socialinteraction" if a.interaction.lower().startswith("social:") else a.interaction.lower()
    tick_before = game_tick(a.endpoint)
    deadline = time.time() + a.wait
    started = time.time()
    head, state = "-", "empty"
    while time.time() < deadline:
        head, state = queue_head(a.endpoint, fields["actorId"])
        if state == "RUNNING" and fragment in head.lower():
            print(f"  running after {time.time() - started:.1f}s: {head}")
            return 0
        time.sleep(1.5)
    hint = not_running_hint(state, tick_before, game_tick(a.endpoint) if tick_before is not None else None)
    print(f"  NOT running after {a.wait:.0f}s: head={head} ({state}): {hint}")
    return 2


if __name__ == "__main__":
    sys.exit(main())
