"""A player's pie-menu pick in a client game, made the way a click makes it.

    python scripts/client_pick.py Bed Relax                           # client 1's active Sim: the nearest free bed
    python scripts/client_pick.py --client 2 Bed Sleep                # player 2's active Sim
    python scripts/client_pick.py --client 2 sim:Jennifer social:Chat   # a social, by its action key
    python scripts/client_pick.py --client 2 Chair Sit --rank 1       # the second-nearest free chair
    python scripts/client_pick.py --client 2 id:11539349195487923152 Sit   # one object by id, for both players at once
    python scripts/client_pick.py --client 2 inv:PhoneSmart BrowseTheWeb  # an object in the active Sim's inventory
                                                                      # (its inventory menu): a pick on any lot
    python scripts/client_pick.py --client 2 ChairDining Sit --near Jennifer  # the first free chair within 5 m of
                                                                      # Jennifer by object id: both players, every reset
    python scripts/client_pick.py --client 2 Bed "" --list            # only list the interactions it offers
    python scripts/client_pick.py --client 2 --hud                    # the HUD's queue for player 2's Sim
    python scripts/client_pick.py --client 2 --cancel 1               # cancel its item 1, as the X button does

For the client's active Sim, the probe does what the pie menu's OnMenuItemSelect does:
1. It finds the target's interactions for that Sim.
2. It picks one by its definition type (and pie-menu text with --name).
3. It makes a UserDirected instance and adds it to the queue from a simulator task, outside the bridge's apply window.

With the authority gate in Enforce, the client's insert is blocked and parked for the server. The server runs the
pick or rejects it (command_rejected, with the game's own reason), and the client shows what the server did.

The target is sim:FirstName, id:ObjectId (the server's id: this client's copy of that object, the same object for world
objects), inv:<type-name fragment> (the first such object in the active Sim's inventory, with the
interactions its inventory menu offers), or a type-name fragment for the nearest free object in 15 m (--rank N: the N-th nearest,
so two players can be sent to different chairs at once). --near <FirstName> takes the free objects within --within
metres (5) of that Sim instead and ranks them by object id, so both players, and every reset in a run, resolve the
same object: ranked by distance, the two dining chairs by Jennifer came in either order from one reset to the next
(2026-10-21). Object ids are the same in every game of a run (they can change per boot), except copies a client made of
server objects (runtime objects such as delivered newspapers): the reply's target= is the server's id (;local= this
client's when it differs), as the pick itself sends it.
The probe is written from a template into .omo/run/act/.
This tool replaces the hand-edited scratch/UserPickProbe*.cs files, one per target.
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))
import compare_client  # noqa: E402
import probe_source  # noqa: E402

PROBE = ROOT / ".omo" / "run" / "act" / "ClientPickProbe.cs"

TEMPLATE = r"""using System;
using System.Collections.Generic;
using System.Text;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.SimIFace;

namespace SimsOnline.Scratch
{
    // Written by scripts/client_pick.py: a pie-menu pick for this client's active Sim.
    public class ClientPickProbe
    {
        public const string TargetSim = "__TARGET_SIM__";
        public const ulong TargetObjectId = __TARGET_ID__uL;
        public const string TargetTypeFragment = "__TARGET_TYPE__";
        public const string TargetInventoryFragment = "__TARGET_INV__";
        public const string TypeFragment = "__TYPE__";
        public const string NameFragment = "__NAME__";
        public const int Rank = __RANK__;
        public const string NearSim = "__NEAR_SIM__";
        public const float NearWithin = __NEAR_WITHIN__f;
        public const bool ListOnly = __LIST__;
        public const bool HudOnly = __HUD__;
        public const int CancelIndex = __CANCEL__;

        private static InteractionInstance sInstance;

        // Object ids in and out are the server's (menu_diff compares them with sim_act's): a copy this client made of a
        // server object (a delivered newspaper) has an id of its own. The shell's own maps, by reflection: the probe
        // compiles without the shell. Without the shell the id is this client's.
        private static Type ServerHost()
        {
            foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type host = asm.GetType("SimsOnlineServerHost.ServerHostMain");
                if (host != null) return host;
            }
            return null;
        }

        private static ObjectGuid LocalFor(ulong serverId)
        {
            try
            {
                Type host = ServerHost();
                System.Reflection.MethodInfo method = host == null ? null : host.GetMethod("ReplayObjectForModules",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (method != null)
                {
                    ObjectGuid local = (ObjectGuid)method.Invoke(null, new object[] { serverId });
                    if (local.IsValid) return local;
                }
            }
            catch (Exception) { }
            return new ObjectGuid(serverId);
        }

        private static ulong WireId(GameObject obj)
        {
            try
            {
                Type host = ServerHost();
                System.Reflection.MethodInfo method = host == null ? null : host.GetMethod("WireObjectId",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (method != null) return (ulong)method.Invoke(null, new object[] { obj });
            }
            catch (Exception) { }
            return obj.ObjectId.Value;
        }

        // PresenterHook.ServerRefused (the fork's menu-refusal slice), by reflection: the probe compiles against the
        // stock gameplay assembly. False without the fork, or when the client holds no refusal for that entry.
        private static bool ServerRefused(InteractionObjectPair iop, Sim actor)
        {
            try
            {
                Type hook = typeof(Sim).Assembly.GetType("SimsOnline.Hooks.PresenterHook");
                System.Reflection.MethodInfo method = hook == null ? null : hook.GetMethod("ServerRefused");
                if (method == null) return false;
                object[] args = new object[] { iop, actor, null };
                return (bool)method.Invoke(null, args);
            }
            catch (Exception) { return false; }
        }

        // The HUD's queue for the active Sim (on a client: the stand-ins for the server's queue), and a cancel the way
        // the queue item's X button makes it (IHudModel.CancelInteraction: on a client it goes to the server).
        private static string Hud(Sim actor)
        {
            Sims3.UI.Hud.IHudModel hud = Sims3.UI.Responder.Instance == null ? null : Sims3.UI.Responder.Instance.HudModel;
            if (hud == null) return "err=no hud model;actor=" + actor.FullName;
            List<Sims3.UI.Hud.IInteractionInstance> items = hud.GetInteractionList();
            StringBuilder sb = new StringBuilder("ok=true;actor=" + actor.FullName + ";hudQueue=" + (items == null ? 0 : items.Count));
            StringBuilder listed = new StringBuilder();
            for (int i = 0; items != null && i < items.Count; i++)
            {
                string name = "";
                try { name = items[i].GetInteractionNameForUI() ?? ""; } catch (Exception) { }
                listed.Append(listed.Length > 0 ? " | " : "").Append(i).Append(": ").Append(name).Append(" #").Append(items[i].Id);
            }
            sb.Append(";items=").Append(listed.Length > 0 ? listed.ToString() : "-");
            if (CancelIndex >= 0)
            {
                if (items == null || CancelIndex >= items.Count) return sb.Append(";err=no item ").Append(CancelIndex).ToString();
                sb.Append(";cancelled=").Append(hud.CancelInteraction(items[CancelIndex].Id)).Append(" #").Append(items[CancelIndex].Id);
            }
            return sb.ToString();
        }
        private static Sim sActor;

        private static Sim SimNamed(string firstName)
        {
            foreach (Sim sim in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                if (sim.SimDescription != null && sim.SimDescription.FirstName == firstName) return sim;
            }
            return null;
        }

        private static void AddLater()
        {
            if (sActor != null && sInstance != null)
            {
                sActor.InteractionQueue.Add(sInstance);
            }
        }

        public static string Run()
        {
            try
            {
                Sim actor = Sim.ActiveActor;
                if (actor == null) return "err=no active actor";
                if (HudOnly || CancelIndex >= 0) return Hud(actor);
                GameObject target = null;
                if (TargetObjectId != 0uL)
                {
                    target = LocalFor(TargetObjectId).ObjectFromId<GameObject>();
                }
                else if (TargetSim.Length > 0)
                {
                    target = SimNamed(TargetSim);
                }
                else if (TargetInventoryFragment.Length > 0)
                {
                    if (actor.Inventory != null)
                    {
                        foreach (GameObject item in actor.Inventory.FindAll<GameObject>(false))
                        {
                            if (item.GetType().FullName.IndexOf(TargetInventoryFragment, StringComparison.Ordinal) >= 0) { target = item; break; }
                        }
                    }
                }
                else
                {
                    Vector3 from = actor.Position;
                    if (NearSim.Length > 0)
                    {
                        Sim near = SimNamed(NearSim);
                        if (near == null) return "err=no Sim named " + NearSim + ";actor=" + actor.FullName;
                        from = near.Position;
                    }
                    List<GameObject> free = new List<GameObject>();
                    foreach (GameObject obj in Sims3.Gameplay.Queries.GetObjects<GameObject>(from, NearSim.Length > 0 ? NearWithin : 15f))
                    {
                        if (obj.GetType().FullName.IndexOf(TargetTypeFragment, StringComparison.Ordinal) < 0) continue;
                        if (obj.InUse && !ListOnly) continue;
                        free.Add(obj);
                    }
                    if (NearSim.Length > 0)
                    {
                        // By object id, not distance: the same object for both players and every reset.
                        free.Sort(delegate(GameObject a, GameObject b) { return a.ObjectId.Value.CompareTo(b.ObjectId.Value); });
                    }
                    else
                    {
                        free.Sort(delegate(GameObject a, GameObject b) { return (a.Position - from).LengthSqr().CompareTo((b.Position - from).LengthSqr()); });
                    }
                    if (free.Count > Rank) target = free[Rank];
                }
                if (target == null) return "err=target not found;actor=" + actor.FullName;

                ulong wireId = WireId(target);
                StringBuilder sb = new StringBuilder("ok=true;actor=" + actor.FullName + ";target=" + target.GetType().Name + ":" + wireId
                    + (wireId != target.ObjectId.Value ? ";local=" + target.ObjectId.Value : ""));
                List<InteractionObjectPair> iops;
                if (TypeFragment.StartsWith("social:"))
                {
                    // A social is built from its action key, as the game's own code does (sim_act.py does the same):
                    // the pie menu's social items are not in the Sim's list.
                    iops = new List<InteractionObjectPair>();
                    iops.Add(new InteractionObjectPair(new Sims3.Gameplay.Socializing.SocialInteractionA.Definition(
                        TypeFragment.Substring(7), new string[0], null, false), target));
                }
                else
                {
                    // An inventory object offers its inventory menu's interactions (the phone's Call, Browse the Web).
                    iops = target.InInventory ? target.GetAllInventoryInteractionsForActor(actor) : target.GetAllInteractionsForActor(actor);
                }
                InteractionObjectPair chosen = null;
                string chosenName = "";
                StringBuilder listed = new StringBuilder();
                foreach (InteractionObjectPair iop in iops)
                {
                    if (iop.InteractionDefinition == null) continue;
                    string type = iop.InteractionDefinition.GetType().FullName;
                    string name = "";
                    try { name = iop.InteractionDefinition.GetInteractionName(actor, iop.Target, iop) ?? ""; } catch (Exception) { }
                    if (ListOnly)
                    {
                        // The client's own test, as its pie menu runs it (menu_diff.py compares it with the server's).
                        string result;
                        string state = "";
                        try
                        {
                            InteractionInstanceParameters tested = new InteractionInstanceParameters(iop, actor,
                                new InteractionPriority(InteractionPriorityLevel.UserDirected), AutonomySearchType.None,
                                InteractionFlags.UserDirected, GameObjectHit.NoHit, null, false);
                            GreyedOutTooltipCallback why = null;
                            InteractionTestResult tested2 = iop.InteractionDefinition.Test(ref tested, ref why);
                            result = tested2.ToString();
                            // The pie menu's rule: a failed test with a tooltip callback shows greyed, one without hides.
                            if (!IUtil.IsPass(tested2)) state = why != null ? "|grey" : "|hidden";
                            // The fork's menu-refusal hook, as PieMenu.TestAndBringUpPieMenu runs it after the test:
                            // an entry the server refused shows greyed until the server's own test passes again.
                            if (IUtil.IsPass(tested2) && ServerRefused(iop, actor)) result = "ServerRefused";
                        }
                        catch (Exception) { result = "TestError"; }
                        listed.Append(listed.Length > 0 ? " | " : "").Append(type.Substring(type.LastIndexOf('.') + 1)).Append(" = ")
                            .Append(name.Replace("|", "/")).Append(" [").Append(result).Append(state).Append("]");
                        continue;
                    }
                    if (!TypeFragment.StartsWith("social:") && type.IndexOf(TypeFragment, StringComparison.Ordinal) < 0) continue;
                    if (NameFragment.Length > 0 && name.IndexOf(NameFragment, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    chosen = iop;
                    chosenName = name;
                    break;
                }
                sb.Append(";candidates=").Append(iops.Count);
                if (ListOnly) return sb.Append(";list=").Append(listed.ToString()).ToString();
                if (chosen == null) return sb.Append(";err=no matching interaction").ToString();

                InteractionInstanceParameters parameters = new InteractionInstanceParameters(chosen, actor,
                    new InteractionPriority(InteractionPriorityLevel.UserDirected), AutonomySearchType.None,
                    InteractionFlags.UserDirected, GameObjectHit.NoHit, null, false);
                sInstance = chosen.InteractionDefinition.CreateInstanceFromParameters(ref parameters);
                sActor = actor;
                // A bridge command runs inside the gate's server-apply window; a real click does not. Add on a
                // simulator task, outside the window, like the pie menu's selection handler.
                Sims3.SimIFace.Simulator.AddObject(new Sims3.Gameplay.OneShotFunctionTask(new Sims3.Gameplay.Function(AddLater)));
                return sb.Append(";picked=").Append(chosen.InteractionDefinition.GetType().FullName).Append(";name=").Append(chosenName)
                    .Append(";scheduled=true").ToString();
            }
            catch (Exception ex)
            {
                return "err=" + ex.GetType().Name + ";msg=" + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
            }
        }
    }
}
"""


def literal(text: str) -> str:
    return text.replace("\\", "\\\\").replace('"', '\\"')


def source(target: str, interaction: str, name: str = "", rank: int = 0, list_only: bool = False, hud: bool = False,
           cancel: int = -1, near: str = "", within: float = 5.0) -> str:
    sim = target[4:] if target.startswith("sim:") else ""
    oid = int(target[3:]) if target.startswith("id:") else 0
    inv = target[4:] if target.startswith("inv:") else ""
    values = {"__TARGET_SIM__": literal(sim), "__TARGET_ID__": str(oid), "__TARGET_INV__": literal(inv),
              "__TARGET_TYPE__": literal("" if sim or oid or inv else target),
              "__TYPE__": literal(interaction), "__NAME__": literal(name), "__RANK__": str(int(rank)), "__NEAR_SIM__": literal(near), "__NEAR_WITHIN__": repr(float(within)),
              "__LIST__": "true" if list_only else "false", "__HUD__": "true" if hud else "false",
              "__CANCEL__": str(int(cancel))}
    text = TEMPLATE
    for key, value in values.items():
        text = text.replace(key, value)
    return text


def main(argv: list[str] | None = None) -> int:
    code, line = reply(argv)
    print(line)
    return code


def reply(argv: list[str] | None = None) -> tuple[int, str]:
    """main's work without printing: (exit code, the "client N: key=value; ..." line). quick_loop's cpick:, hud: steps
    read it."""
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("target", nargs="?", default="",
                   help="sim:FirstName, id:ObjectId, inv:<type fragment> (an object in the active Sim's inventory: inv:PhoneSmart), "
                   "or a type-name fragment of the nearest free object (Bed, Chair, Shower)")
    p.add_argument("interaction", nargs="?", default="", help="a fragment of the interaction definition's type name (Sleep, Sit), or "
                   "social:<action key> on a sim: target (social:Chat); with --list it may be empty")
    p.add_argument("--name", default="", help="a fragment of the pie-menu text")
    p.add_argument("--rank", type=int, default=0,
                   help="the N-th free object: by distance, or by object id with --near (0: the first)")
    p.add_argument("--near", default="", metavar="FIRSTNAME",
                   help="the free objects within --within metres of this Sim, ranked by object id instead of distance "
                        "(both players and every reset resolve the same object)")
    p.add_argument("--within", type=float, default=5.0, metavar="M", help="with --near: the radius (default 5 m)")
    p.add_argument("--list", action="store_true",
                   help="only list the target's interactions for the active Sim, each with the client's test result")
    p.add_argument("--hud", action="store_true", help="only list the HUD's queue for the active Sim (no target)")
    p.add_argument("--cancel", type=int, default=-1, metavar="N",
                   help="cancel the HUD queue's N-th item (from --hud) the way its X button does (no target)")
    p.add_argument("--client", default="", help="the client game: 1 (default), 2 (player 2) or HOST:PORT")
    a = p.parse_args(argv)
    if not a.target and not a.hud and a.cancel < 0:
        p.error("a target is needed unless --hud or --cancel is given")
    endpoint = compare_client.resolve(a.client) if a.client else compare_client.endpoint()
    probe = probe_source.write(PROBE, source(a.target, a.interaction, a.name, a.rank, a.list, a.hud, a.cancel, a.near, a.within))
    done = subprocess.run([sys.executable, str(ROOT / "scripts" / "bridgectl.py"), "--endpoint", endpoint, "probe", str(probe)],
                          capture_output=True, text=True, timeout=90, cwd=ROOT)
    line = f"{compare_client.name(endpoint)}: {reply_fields(done.stdout) or done.stderr.strip()[-300:]}"
    return (0 if done.returncode == 0 and " err=" not in " " + reply_fields(done.stdout) else 1), line


def reply_fields(stdout: str) -> str:
    """The probe's report (bridgectl prints it after DIAG_RUN as indented `key = value` lines) as one line."""
    fields = [ln.strip() for ln in stdout.split("-- DIAG_RUN --")[-1].splitlines() if re.match(r"\s+\w+\s+=", ln)]
    return "; ".join(re.sub(r"\s+=\s+", "=", f, count=1) for f in fields)


if __name__ == "__main__":
    raise SystemExit(main())
