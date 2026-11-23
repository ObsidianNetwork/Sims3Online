"""persistence (handoff 4, task 6): save the server's world, shut the whole stack down, boot it cold from that
save, reconnect both clients, and check that the players' state survived.

    python scripts/persist_cycle.py setup --outfit Pablo=Formalwear --liking Jennifer+Pablo=15 --move Jennifer:Chair=1,0
    python scripts/persist_cycle.py mark NAME [--sims Jennifer,Pablo] [--objects ID,...]   # the server's state now
    python scripts/persist_cycle.py compare BEFORE AFTER                                  # PASS/FAIL per check
    python scripts/persist_cycle.py cycle NAME [--mod] [--setup-step STEP ...] [--objects ID,...]
    python scripts/persist_cycle.py list                                                  # the last cycles
    python scripts/persist_cycle.py save-state [--reset] [--endpoint HOST:PORT]           # why a game cannot save

save-state reads what keeps the server's game (or --endpoint's) from saving: sub-state, CanSaveGame, the engine's
save-in-progress and state-change-dialog flags, the speed and its lock, any modal window. A game save that throws
(the game's own OptionsModel.SaveGame or the edit-town prompt run from a probe, Session H, 21 October) leaves the two
flags set and the speed locked at Pause: every later SAVE_GAME is refused as "not ready", so every late join's
checkpoint waits 20 s and fails, and no Sim's interaction starts. --reset clears all three (the speed back to --speed).

setup makes state a save must keep, on the server's game (one generated probe): an outfit category, a change of a pair's
liking, the nearest object of a type moved by dx,dz (it prints the moved object's id for --objects). A held object
needs a real interaction (sim_act.py).

mark runs one generated probe on the server's game (bridgectl probe, the way sim_compare does) and keeps, per named
Sim, its position, running interaction, outfit, both hands' objects, inventory and household funds; per pair, the relationship's liking and
long-term state; per --objects id (a moved chair, a held book), its type, position, parent and inventory flag. Marks go
to .omo/run/persist/NAME.json.

compare applies the plan's rules (internal documentation, C1): the same outfit, funds and relationship
(liking within 0.5), each Sim saved idle within 0.5 m (a Sim saved mid-interaction is placed by the load's reset, so
the server's spot after the load counts: its row passes and names the rule, and cycle's sim_compare checks the clients
against the server), each tracked object within 0.1 m, and every object a Sim held or carried
before still on the server (in a hand, an inventory or the world: a load resets interactions, so a carry may end in the
inventory). It prints one PASS/FAIL line per check and exits 0 only when all pass. An object found in neither mark's
hands or inventories counts only if the after mark was asked for its id: cycle asks for every id the before mark
carried; a mark by hand needs them in --objects.

cycle needs the stack claim (scripts/stack_claim.py take <id> <purpose>), both clients up and claimed, and a joined
player 1. In order: the --setup-step steps (quick_loop.py run --step), mark NAME-before, the public save_game
(simctl.py save Option4BookChain: the session's reconcile policy, then the bridge SAVE_GAME), saves.py backup
NAME-saved (both Saves folders, hashed), sim_compare.py --props --moodlets --machines on client 1 and client 2 (the
baseline), stackctl.py down --force, the boot-save check (with the games down, the server
boot save's files are the saved ones, hash by hash: a boot's own client join checkpoints again, so the files after a
boot never prove what it loaded), quick_loop.py run --boot --clients 2 (--mod: this checkout's mod too), mark
NAME-after (the before mark's objects too), compare, and sim_compare on both clients again: a client fails only on a
visible mismatch the baseline did not have. Both sim_compare outputs per client are kept as
.omo/run/persist/NAME-before-clientN.txt and NAME-after-clientN.txt. A step that fails stops the cycle there; the games stay as that step left them. Every cycle appends one row
to .omo/run/persist/persist.jsonl with each check's status. The backup NAME-saved stays: `saves.py restore` puts it
back with the games down.
"""
from __future__ import annotations

import argparse
import json
import math
import re
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))
STORE = ROOT / ".omo" / "run" / "persist"
PROBE = ROOT / ".omo" / "run" / "act" / "PersistMarkProbe.cs"
SERVER = "127.0.0.1:34029"
SAVE_NAME = "Option4BookChain"  # the server's boot save (quick_loop.BOOT_SAVE without .sims3)
SIMS = ["Jennifer", "Pablo"]
SIM_SLACK = 0.5  # m: a reset on load may stand a Sim up
OBJECT_SLACK = 0.1  # m
LIKING_SLACK = 0.5
BOOT_TIMEOUT = 1800
DOWN_WAIT = 90  # s for the last TS3W.exe to exit after stackctl down

TEMPLATE = r'''using System;
using System.Collections.Generic;
using System.Text;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace SimsOnline.Scratch
{
    // Generated by scripts/persist_cycle.py: one line per field, "field|key|value", for the named Sims and objects.
    public class PersistMarkProbe
    {
        private const string Names = "__NAMES__";
        private const string Objects = "__OBJECTS__";

        private static string F(float v)
        {
            return v.ToString("F2");
        }

        private static string Pos(Vector3 p)
        {
            return F(p.x) + "," + F(p.y) + "," + F(p.z);
        }

        // The running interaction's type (Book+ReadBook), or "-" when the Sim is idle.
        private static string Act(Sim sim)
        {
            InteractionInstance running = sim.InteractionQueue == null ? null : sim.InteractionQueue.GetCurrentInteraction();
            if (running == null)
            {
                return "-";
            }

            string type = running.GetType().FullName;
            return type.Substring(type.LastIndexOf('.') + 1);
        }

        private static string Item(IGameObject obj)
        {
            return obj == null ? "-" : obj.GetType().Name + "#" + obj.ObjectId.Value.ToString();
        }

        private static string Held(Sim sim, Slot slot)
        {
            ObjectGuid held = Slots.GetContainedObject(sim.ObjectId, (uint)slot);
            if (!held.IsValid)
            {
                return "-";
            }

            GameObject obj = held.ObjectFromId<GameObject>();
            return obj == null ? "scriptless#" + held.Value.ToString() : Item(obj);
        }

        private static Sim Find(string name)
        {
            Sim sim = null;
            foreach (Sim candidate in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                if (candidate.SimDescription != null && string.Equals(candidate.FirstName, name, StringComparison.OrdinalIgnoreCase))
                {
                    sim = candidate;
                    if (candidate.Household != null && candidate.Household == Household.ActiveHousehold)
                    {
                        break;
                    }
                }
            }

            return sim;
        }

        public static string Run()
        {
            StringBuilder sb = new StringBuilder("ok=true");
            try
            {
                LoadSaveFileInfo info = LoadSaveManager.GetCurrentSaveGameInfo();
                try
                {
                    sb.Append("\nsave|-|").Append(info == null || info.ID == null ? "-" : info.ID);
                }
                finally
                {
                    if (info != null)
                    {
                        info.Dispose();
                    }
                }

                sb.Append("\nticks|-|").Append(SimClock.CurrentTicks.ToString());
                List<Sim> found = new List<Sim>();
                foreach (string name in Names.Split(','))
                {
                    Sim sim = Find(name);
                    if (sim == null)
                    {
                        sb.Append("\nmissing|").Append(name).Append("|1");
                        continue;
                    }

                    found.Add(sim);
                    string n = sim.FirstName;
                    sb.Append("\npos|").Append(n).Append("|").Append(Pos(sim.Position));
                    sb.Append("\nact|").Append(n).Append("|").Append(Act(sim));
                    sb.Append("\noutfit|").Append(n).Append("|").Append(sim.CurrentOutfitCategory.ToString()).Append(":").Append(sim.CurrentOutfitIndex.ToString());
                    sb.Append("\nhand.right|").Append(n).Append("|").Append(Held(sim, Sim.ContainmentSlots.RightHand));
                    sb.Append("\nhand.left|").Append(n).Append("|").Append(Held(sim, Sim.ContainmentSlots.LeftHand));
                    if (sim.Inventory != null)
                    {
                        foreach (IGameObject obj in sim.Inventory.FindAllOfType(typeof(IGameObject)))
                        {
                            sb.Append("\ninv|").Append(n).Append("|").Append(Item(obj));
                        }
                    }

                    sb.Append("\nfunds|").Append(n).Append("|").Append(sim.Household == null ? "-" : sim.Household.FamilyFunds.ToString());
                }

                for (int i = 0; i < found.Count; i++)
                {
                    for (int j = i + 1; j < found.Count; j++)
                    {
                        Relationship r = Relationship.Get(found[i].SimDescription, found[j].SimDescription, false);
                        sb.Append("\npair|").Append(found[i].FirstName).Append("+").Append(found[j].FirstName).Append("|")
                            .Append(r == null || r.LTR == null ? "none" : F(r.LTR.Liking) + " " + r.LTR.CurrentLTR.ToString());
                    }
                }

                foreach (string id in Objects.Split(','))
                {
                    if (id.Length == 0)
                    {
                        continue;
                    }

                    GameObject obj = new ObjectGuid(ulong.Parse(id)).ObjectFromId<GameObject>();
                    sb.Append("\nobj|").Append(id).Append("|");
                    if (obj == null)
                    {
                        sb.Append("missing");
                        continue;
                    }

                    IGameObject parent = obj.Parent;
                    sb.Append(obj.GetType().Name).Append(" ").Append(Pos(obj.Position))
                        .Append(" parent=").Append(parent == null ? "-" : (parent is Sim ? ((Sim)parent).FirstName : parent.GetType().Name))
                        .Append(" inv=").Append(obj.InInventory ? "1" : "0");
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "ok=false;err=" + ex.GetType().Name + ":" + ex.Message.Replace(';', ',') + sb;
            }
        }
    }
}
'''


SETUP_TEMPLATE = r'''using System;
using System.Text;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Socializing;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace SimsOnline.Scratch
{
    // Generated by scripts/persist_cycle.py setup: state a save must keep, made on the server's game.
    public class PersistSetupProbe
    {
        private const string Outfits = "__OUTFITS__"; // Sim=Category;...
        private const string Likings = "__LIKINGS__"; // Sim+Sim=delta;...
        private const string Moves = "__MOVES__"; // Sim:TypeFragment=dx,dz;... the nearest such object within 15 m

        private static string[] Items(string list)
        {
            return list.Length == 0 ? new string[0] : list.Split(';');
        }

        private static Sim Find(string name)
        {
            Sim sim = null;
            foreach (Sim candidate in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                if (candidate.SimDescription != null && string.Equals(candidate.FirstName, name, StringComparison.OrdinalIgnoreCase))
                {
                    sim = candidate;
                    if (candidate.Household != null && candidate.Household == Household.ActiveHousehold)
                    {
                        break;
                    }
                }
            }

            if (sim == null)
            {
                throw new InvalidOperationException("no Sim " + name);
            }

            return sim;
        }

        public static string Run()
        {
            StringBuilder sb = new StringBuilder("ok=true");
            try
            {
                foreach (string item in Items(Outfits))
                {
                    string[] kv = item.Split('=');
                    Sim sim = Find(kv[0]);
                    sim.SwitchToOutfitWithoutSpin(Sim.ClothesChangeReason.Force, (OutfitCategories)Enum.Parse(typeof(OutfitCategories), kv[1]));
                    sb.Append("\noutfit|").Append(sim.FirstName).Append("|").Append(sim.CurrentOutfitCategory.ToString()).Append(":").Append(sim.CurrentOutfitIndex.ToString());
                }

                foreach (string item in Items(Likings))
                {
                    string[] kv = item.Split('=');
                    string[] pair = kv[0].Split('+');
                    Relationship r = Relationship.Get(Find(pair[0]).SimDescription, Find(pair[1]).SimDescription, true);
                    r.LTR.UpdateLiking(float.Parse(kv[1]));
                    sb.Append("\nliking|").Append(kv[0]).Append("|").Append(r.LTR.Liking.ToString("F2")).Append(" ").Append(r.LTR.CurrentLTR.ToString());
                }

                foreach (string item in Items(Moves))
                {
                    string[] kv = item.Split('=');
                    string[] what = kv[0].Split(':');
                    string[] by = kv[1].Split(',');
                    Sim sim = Find(what[0]);
                    GameObject best = null;
                    float bestDistance = float.MaxValue;
                    foreach (GameObject obj in Sims3.Gameplay.Queries.GetObjects<GameObject>(sim.Position, 15f))
                    {
                        if (obj is Sim || obj.Parent != null || obj.InInventory || obj.GetType().Name.IndexOf(what[1], StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        float d = (obj.Position - sim.Position).Length();
                        if (d < bestDistance)
                        {
                            best = obj;
                            bestDistance = d;
                        }
                    }

                    if (best == null)
                    {
                        sb.Append("\nmove|-|no ").Append(what[1]).Append(" within 15 m of ").Append(sim.FirstName);
                        continue;
                    }

                    Vector3 p = best.Position;
                    best.SetPosition(new Vector3(p.x + float.Parse(by[0]), p.y, p.z + float.Parse(by[1])));
                    sb.Append("\nmove|").Append(best.ObjectId.Value.ToString()).Append("|").Append(best.GetType().Name)
                        .Append(" ").Append(p.x.ToString("F2")).Append(",").Append(p.z.ToString("F2"))
                        .Append(" to ").Append(best.Position.x.ToString("F2")).Append(",").Append(best.Position.z.ToString("F2"));
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "ok=false;err=" + ex.GetType().Name + ":" + ex.Message.Replace(';', ',') + sb;
            }
        }
    }
}
'''


SAVE_STATE_TEMPLATE = r'''using System;
using System.Reflection;
using System.Text;
using Sims3.Gameplay;
using Sims3.Gameplay.UI;
using Sims3.SimIFace;

namespace SimsOnline.Scratch
{
    // Generated by scripts/persist_cycle.py save-state: what keeps an in-world game from saving, and (Reset) a reset
    // of what a thrown game save leaves behind. OptionsModel.SaveGame sets mSaveInProgress, PromptedSave sets
    // InWorldState.sStateChangeDialogUp (IsCurrentlySwitchingSubStates, so CanSaveGame is false) and
    // SaveGameSynchronous locks the speed at Pause before its first yield; none is undone when they throw.
    public class PersistSaveStateProbe
    {
        private const bool Reset = __RESET__;
        private const string Speed = "__SPEED__"; // the speed an unlocked game gets back

        private static void Add(StringBuilder sb, string field, object value)
        {
            sb.Append("\n").Append(field).Append("|-|").Append(value == null ? "none" : value.ToString());
        }

        public static string Run()
        {
            StringBuilder sb = new StringBuilder("ok=true");
            try
            {
                OptionsModel options = Responder.Instance.OptionsModel as OptionsModel;
                FieldInfo dialogUp = typeof(InWorldState).GetField("sStateChangeDialogUp", BindingFlags.Static | BindingFlags.NonPublic);
                FieldInfo inProgress = typeof(OptionsModel).GetField("mSaveInProgress", BindingFlags.Instance | BindingFlags.NonPublic);
                if (Reset)
                {
                    dialogUp.SetValue(null, false);
                    inProgress.SetValue(options, false);
                    if (Responder.Instance.ClockSpeedModel.GameSpeedLocked)
                    {
                        Responder.Instance.ClockSpeedModel.UnlockGameSpeed();
                        Responder.Instance.ClockSpeedModel.CurrentGameSpeed = (Sims3.SimIFace.Gameflow.GameSpeed)Enum.Parse(typeof(Sims3.SimIFace.Gameflow.GameSpeed), Speed);
                    }

                    Add(sb, "reset", "1");
                }

                object subState = GameStates.GetInWorldSubState();
                Sims3.UI.WindowBase modal = Sims3.UI.UIManager.GetModalWindow();
                Add(sb, "subState", subState == null ? "none" : subState.GetType().Name);
                Add(sb, "canSave", options.CanSaveGame());
                Add(sb, "saveInProgress", inProgress.GetValue(options));
                Add(sb, "stateChangeDialogUp", dialogUp.GetValue(null));
                Add(sb, "speed", Responder.Instance.ClockSpeedModel.CurrentGameSpeed);
                Add(sb, "speedLocked", Responder.Instance.ClockSpeedModel.GameSpeedLocked);
                Add(sb, "modal", modal == null ? "none" : modal.GetType().FullName);
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "ok=false;err=" + ex.GetType().Name + ":" + ex.Message.Replace(';', ',') + sb;
            }
        }
    }
}
'''


def probe_text(sims: list[str], objects: list[str]) -> str:
    return TEMPLATE.replace("__NAMES__", ",".join(sims)).replace("__OBJECTS__", ",".join(objects))


def setup_text(outfits: list[str], likings: list[str], moves: list[str]) -> str:
    return (SETUP_TEMPLATE.replace("__OUTFITS__", ";".join(outfits)).replace("__LIKINGS__", ";".join(likings))
            .replace("__MOVES__", ";".join(moves)))


def setup(outfits: list[str], likings: list[str], moves: list[str]) -> int:
    """Make the state a save must keep on the server's game; print what it made (a moved object's id goes to
    cycle --objects)."""
    import probe_source
    import sim_compare

    probe = probe_source.write(PROBE.with_name("PersistSetupProbe.cs"), setup_text(outfits, likings, moves))
    fields = sim_compare.run_side(SERVER, probe)
    for (key, field), values in fields.items():
        for value in values:
            print(f"{field:<8} {key:<22} {value}")
    return 1 if ("-", "error") in fields else 0


def save_state_text(reset: bool, speed: str) -> str:
    return SAVE_STATE_TEMPLATE.replace("__RESET__", "true" if reset else "false").replace("__SPEED__", speed)


def wedged(state: dict[str, str]) -> list[str]:
    """What a thrown game save (OptionsModel.SaveGame, InWorldState.PromptedSave) left set: each refuses every later
    save (SAVE_GAME's CanSaveGame / SaveGameInProgress refusals) or keeps the game paused."""
    out = []
    if state.get("saveInProgress") == "True":
        out.append("OptionsModel.mSaveInProgress set")
    if state.get("stateChangeDialogUp") == "True":
        out.append("InWorldState.sStateChangeDialogUp set")
    if state.get("speedLocked") == "True":
        out.append(f"game speed locked at {state.get('speed')}")
    return out


def save_state(reset: bool, speed: str, endpoint: str) -> int:
    """Print what keeps the game at endpoint from saving; with reset, clear what a thrown game save left set first.
    Exits 1 when something is still set (or the probe failed)."""
    import probe_source
    import sim_compare

    probe = probe_source.write(PROBE.with_name("PersistSaveStateProbe.cs"), save_state_text(reset, speed))
    fields = sim_compare.run_side(endpoint, probe)
    state = {field: values[0] for (_, field), values in fields.items() if values}
    for field, value in state.items():
        print(f"{field:<20} {value}")
    stuck = wedged(state)
    if stuck:
        print("wedged: " + "; ".join(stuck) + " (save-state --reset clears them, with nothing saving)")
    return 1 if ("-", "error") in fields or stuck else 0


def item(text: str) -> dict | None:
    """'Type#id' as {"type", "id"}; '-' as None."""
    if text in ("-", ""):
        return None
    kind, _, oid = text.rpartition("#")
    return {"type": kind, "id": oid}


def vec(text: str) -> list[float]:
    return [float(v) for v in text.split(",")]


def parse_mark(fields: dict[tuple[str, str], list[str]]) -> dict:
    """sim_compare.run_side's {(key, field): [values]} as a mark: sims, pairs, objects, the save id and the clock."""
    mark: dict = {"save": None, "ticks": None, "sims": {}, "missing": [], "pairs": {}, "objects": {}, "errors": []}
    for (key, field), values in fields.items():
        value = values[0] if values else ""
        if field == "error":
            mark["errors"] += values
        elif field == "save":
            mark["save"] = value
        elif field == "ticks":
            mark["ticks"] = int(value)
        elif field == "missing":
            mark["missing"].append(key)
        elif field == "pair":
            if value == "none":
                mark["pairs"][key] = None
            else:
                liking, _, ltr = value.partition(" ")
                mark["pairs"][key] = {"liking": float(liking), "ltr": ltr}
        elif field == "obj":
            if value == "missing":
                mark["objects"][key] = None
            else:
                kind, pos, parent, inv = value.split(" ")
                mark["objects"][key] = {"type": kind, "pos": vec(pos), "parent": parent.split("=", 1)[1],
                                        "in_inventory": inv == "inv=1"}
        else:
            sim = mark["sims"].setdefault(key, {"hands": {}, "inventory": []})
            if field == "pos":
                sim["pos"] = vec(value)
            elif field == "act":
                sim["act"] = None if value == "-" else value
            elif field == "outfit":
                sim["outfit"] = value
            elif field == "funds":
                sim["funds"] = None if value == "-" else int(value)
            elif field.startswith("hand."):
                sim["hands"][field[5:]] = item(value)
            elif field == "inv":
                sim["inventory"] = sorted((i for i in map(item, values) if i), key=lambda i: (i["type"], i["id"]))
    return mark


def carried(mark: dict) -> dict[str, str]:
    """Every object a named Sim holds or has in its inventory, by id: its type."""
    out = {}
    for sim in mark["sims"].values():
        for held in list(sim["hands"].values()) + sim["inventory"]:
            if held and held["id"].isdigit():
                out[held["id"]] = held["type"]
    return out


def distance(a: list[float], b: list[float]) -> float:
    return math.dist(a, b)


def compare(before: dict, after: dict) -> list[tuple[str, bool, str]]:
    """The C1 rules as (check, passed, detail) rows."""
    rows: list[tuple[str, bool, str]] = []
    if before.get("errors") or after.get("errors"):
        rows.append(("probe", False, "; ".join(before.get("errors", []) + after.get("errors", []))[:200]))
    rows.append(("save", before["save"] == after["save"], f"{before['save']} | {after['save']}"))
    for name, was in before["sims"].items():
        now = after["sims"].get(name)
        if now is None:
            rows.append((f"{name} present", False, "missing after the reload"))
            continue
        moved = distance(was["pos"], now["pos"])
        if was.get("act"):
            # A load resets a running interaction and the server places the Sim: its spot after the load is the truth,
            # and the clients are checked against it (sim_compare after the boot), not against the saved spot.
            rows.append((f"{name} position", True, f"{moved:.2f} m; saved mid-interaction ({was['act']}): "
                         "the server's placement on load counts, clients compared with the server"))
        else:
            rows.append((f"{name} position", moved <= SIM_SLACK, f"{moved:.2f} m; saved idle: within {SIM_SLACK} m"))
        rows.append((f"{name} outfit", was.get("outfit") == now.get("outfit"), f"{was.get('outfit')} | {now.get('outfit')}"))
        rows.append((f"{name} funds", was.get("funds") == now.get("funds"), f"{was.get('funds')} | {now.get('funds')}"))
    for pair, was in before["pairs"].items():
        now = after["pairs"].get(pair)
        if was is None or now is None:
            rows.append((f"{pair} relationship", was == now, f"{was} | {now}"))
            continue
        same = abs(was["liking"] - now["liking"]) <= LIKING_SLACK and was["ltr"] == now["ltr"]
        rows.append((f"{pair} relationship", same, f"{was['liking']:.2f} {was['ltr']} | {now['liking']:.2f} {now['ltr']}"))
    kept = carried(after)
    for oid, kind in carried(before).items():
        found = after["objects"].get(oid)
        where = ("carried" if oid in kept else None if found is None
                 else "in another inventory" if found["in_inventory"] else "in the world")
        rows.append((f"{kind}#{oid} kept", where is not None, where or "gone from the server"))
    for oid, was in before["objects"].items():
        if was is None or oid in carried(before):
            continue  # missing before, or a carried object (checked above; it moves with its Sim)
        now = after["objects"].get(oid)
        if now is None:
            rows.append((f"{was['type']}#{oid} position", False, "gone from the server"))
            continue
        moved = distance(was["pos"], now["pos"])
        rows.append((f"{was['type']}#{oid} position", moved <= OBJECT_SLACK and was["type"] == now["type"], f"{moved:.2f} m"))
    return rows


def print_rows(rows: list[tuple[str, bool, str]]) -> bool:
    for check, passed, detail in rows:
        print(f"  {'PASS' if passed else 'FAIL'}  {check:<40} {detail}")
    failed = sum(1 for _, passed, _ in rows if not passed)
    print(f"{len(rows) - failed}/{len(rows)} pass")
    return failed == 0


def take_mark(name: str, sims: list[str], objects: list[str]) -> dict:
    import probe_source
    import sim_compare  # its probe runner and line parser

    probe = probe_source.write(PROBE, probe_text(sims, objects))
    mark = parse_mark(sim_compare.run_side(SERVER, probe))
    mark.update({"name": name, "t": round(time.time(), 3), "objects_asked": objects})
    STORE.mkdir(parents=True, exist_ok=True)
    (STORE / f"{name}.json").write_text(json.dumps(mark, indent=1) + "\n", encoding="utf-8")
    return mark


def load_mark(name: str) -> dict:
    path = Path(name) if name.endswith(".json") else STORE / f"{name}.json"
    return json.loads(path.read_text(encoding="utf-8"))


def log_row(row: dict) -> None:
    STORE.mkdir(parents=True, exist_ok=True)
    with open(STORE / "persist.jsonl", "a", encoding="utf-8") as f:
        f.write(json.dumps({"t": round(time.time(), 3), **row}) + "\n")


def run(args: list[str], timeout: float, keep: Path | None = None) -> tuple[int, str]:
    """A repo script's exit code and its output's last lines (keep: the whole output to that file too)."""
    try:
        out = subprocess.run([sys.executable, *args], cwd=ROOT, capture_output=True, text=True, timeout=timeout)
    except subprocess.TimeoutExpired:
        return 124, f"{args[0]} took over {timeout:.0f} s"
    text = ((out.stdout or "") + (out.stderr or "")).strip()
    if keep is not None:
        keep.parent.mkdir(parents=True, exist_ok=True)
        keep.write_text(text + "\n", encoding="utf-8")
    return out.returncode, "\n".join(text.splitlines()[-6:])


def visible_rows(text: str) -> list[str]:
    """The visible ("!") mismatch rows of a sim_compare output as "<section> <field>" (e.g. "Jennifer machines")."""
    section, out = "-", []
    for line in text.splitlines():
        if line.startswith("== "):
            section = line[3:].strip()
            continue
        found = re.match(r"\s*!\s+(.*?)\s{2,}", line)
        if found:
            out.append(f"{section} {found.group(1)}")
    return out


def client_compare(name: str, phase: str, client: str) -> tuple[int, str, list[str]]:
    """sim_compare.py --props --moodlets --machines on one client, its whole output kept as
    .omo/run/persist/NAME-PHASE-clientN.txt: the exit code, the summary line and the visible mismatch rows."""
    keep = STORE / f"{name}-{phase}-client{client}.txt"
    code, text = run(["scripts/sim_compare.py", "--props", "--moodlets", "--machines", "--client", client, *SIMS], 600, keep)
    full = keep.read_text(encoding="utf-8") if keep.exists() else text
    return code, text.splitlines()[-1] if text else f"exit {code}", visible_rows(full)


def boot_save_files(table: dict[str, str]) -> dict[str, str]:
    """A saves.py hash table's server boot save entries, by their path inside the save folder."""
    prefix = f"server-Saves/{SAVE_NAME}.sims3/"
    return {k[len(prefix):]: v for k, v in table.items() if k.startswith(prefix)}


def boot_save_check(backup: str) -> tuple[bool, str]:
    """With the games down: the server boot save's files are the backup's, hash by hash."""
    import saves

    want = boot_save_files(saves.read_hashes(saves.STORE / backup / "hashes.txt"))
    have = saves.hashes(saves.live_saves()["server-Saves"] / f"{SAVE_NAME}.sims3")
    if not want:
        return False, f"the backup {backup} has no {SAVE_NAME}.sims3"
    found = saves.differences(want, have)
    return not found, f"{len(want)} file(s): " + ("identical" if not found else "; ".join(found[:4]))


def claim_check() -> tuple[bool, str]:
    import stack_claim

    mine, claim = stack_claim.own_session(), stack_claim.read()
    if claim is None:
        return False, "no stack claim: take it first (scripts/stack_claim.py take <id> <purpose>)"
    if not mine or claim[0] != mine:
        return False, f"the stack claim is {claim[0]}'s, not this session's ({mine})"
    return True, f"claimed by {mine} ({claim[2][:60]})"


def cycle(name: str, mod: bool, setup_steps: list[str], objects: list[str]) -> int:
    import saves

    checks: dict[str, str] = {}
    row: dict = {"verb": "cycle", "name": name, "checks": checks}

    def step(check: str, passed: bool, detail: str) -> bool:
        checks[check] = "PASS" if passed else "FAIL"
        print(f"{'PASS' if passed else 'FAIL'}  {check}: {detail}", flush=True)
        if not passed:
            row["stopped_at"] = check
        return passed

    def finish() -> int:
        log_row(row)
        failed = [k for k, v in checks.items() if v != "PASS"]
        print(f"cycle {name}: " + ("PASS" if not failed else "stopped" if row.get("stopped_at") else "FAIL")
              + (f" ({', '.join(failed)})" if failed else ""))
        return 0 if not failed else 1

    ok, text = claim_check()
    if not ok:
        checks["claim"] = "BLOCKED"
        row["stopped_at"] = "claim"
        print(f"BLOCKED  claim: {text}")
        finish()
        return 2
    print(f"PASS  claim: {text}")
    if setup_steps:
        code, text = run(["scripts/quick_loop.py", "run", *[w for s in setup_steps for w in ("--step", s)]], 900)
        if not step("setup", code == 0, text.splitlines()[-1] if text else f"exit {code}"):
            return finish()
    before = take_mark(f"{name}-before", SIMS, objects)
    problems = before["errors"] + [f"{s} missing" for s in before["missing"]]
    if not step("mark before", not problems, "; ".join(problems)[:200] if problems
                else f"{len(before['sims'])} Sim(s), {len(before['objects'])} object(s)"):
        return finish()
    code, text = run(["scripts/simctl.py", "--player", "1", "save", SAVE_NAME], 300)
    if not step("save", code == 0 and "save accepted" in text, text.splitlines()[-1] if text else f"exit {code}"):
        return finish()
    backup = f"{name}-saved"
    code = saves.backup(backup, saves.live_saves())
    if not step("backup", code == 0, f".omo/run/saves/{backup}"):
        return finish()
    # Each client against the server before the games go down: a mismatch after the boot counts only when it is new.
    baseline: dict[str, list[str]] = {}
    for client in ("1", "2"):
        code, last, baseline[client] = client_compare(name, "before", client)
        print(f"{'PASS' if code == 0 else 'NOTE'}  client {client} before the save: {last}", flush=True)
    row["baseline"] = baseline
    down = saves.stack_down()
    for _ in range(DOWN_WAIT):
        if not saves.ts3_running():
            break
        time.sleep(1)
    if not step("down", not saves.ts3_running(), down):
        return finish()
    if not step("boot save", *boot_save_check(backup)):
        return finish()
    code, text = run(["scripts/quick_loop.py", "run", *(["--mod"] if mod else []), "--boot", "--clients", "2"], BOOT_TIMEOUT)
    if not step("boot", code == 0, text.splitlines()[-1] if text else f"exit {code}"):
        return finish()
    after = take_mark(f"{name}-after", SIMS, sorted(set(objects) | set(carried(before))))
    rows = compare(before, after)
    print("compare:")
    step("compare", print_rows(rows), f"{name}-before against {name}-after")
    row["compare"] = [[c, "PASS" if p else "FAIL", d] for c, p, d in rows]
    for client in ("1", "2"):
        code, last, rows_after = client_compare(name, "after", client)
        new = [r for r in rows_after if r not in baseline[client]]
        row[f"client {client} new"] = new
        step(f"client {client}", code == 0 or not new,
             last + (f"; new since the save: {', '.join(new)}" if new else "; none new since the save" if rows_after else ""))
    return finish()


def list_rows() -> int:
    try:
        lines = (STORE / "persist.jsonl").read_text(encoding="utf-8").splitlines()
    except OSError:
        print(f"no cycles in {STORE}")
        return 0
    for line in lines[-10:]:
        r = json.loads(line)
        print(time.strftime("%m-%d %H:%M", time.localtime(r["t"])), r.get("name"),
              " ".join(f"{k}={v}" for k, v in r.get("checks", {}).items()))
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="verb", required=True)
    m = sub.add_parser("mark")
    m.add_argument("name")
    m.add_argument("--sims", default=",".join(SIMS))
    m.add_argument("--objects", default="", help="object ids to track, comma-separated")
    c = sub.add_parser("compare")
    c.add_argument("before")
    c.add_argument("after")
    y = sub.add_parser("cycle")
    y.add_argument("name")
    y.add_argument("--mod", action="store_true", help="the boot installs this checkout's mod (quick_loop run --mod)")
    y.add_argument("--setup-step", action="append", default=[], help="a quick_loop.py run --step before the first mark (repeatable)")
    y.add_argument("--objects", default="", help="object ids to track, comma-separated")
    s = sub.add_parser("setup")
    s.add_argument("--outfit", action="append", default=[], help="Sim=OutfitCategory (e.g. Pablo=Formalwear)")
    s.add_argument("--liking", action="append", default=[], help="Sim+Sim=delta (e.g. Jennifer+Pablo=15)")
    s.add_argument("--move", action="append", default=[], help="Sim:TypeFragment=dx,dz: the nearest such object within 15 m")
    sub.add_parser("list")
    st = sub.add_parser("save-state")
    st.add_argument("--reset", action="store_true", help="clear what a thrown game save left set (nothing may be saving)")
    st.add_argument("--speed", default="Normal", help="with --reset: the speed a locked game gets back (Gameflow.GameSpeed)")
    st.add_argument("--endpoint", default=SERVER)
    a = parser.parse_args(argv)
    if a.verb == "setup":
        return setup(a.outfit, a.liking, a.move)
    if a.verb == "save-state":
        return save_state(a.reset, a.speed, a.endpoint)
    if a.verb == "mark":
        mark = take_mark(a.name, [s for s in a.sims.split(",") if s], [o for o in a.objects.split(",") if o])
        print(json.dumps(mark, indent=1))
        return 0 if not mark["errors"] else 1
    if a.verb == "compare":
        return 0 if print_rows(compare(load_mark(a.before), load_mark(a.after))) else 1
    if a.verb == "cycle":
        return cycle(a.name, a.mod, a.setup_step, [o for o in a.objects.split(",") if o])
    return list_rows()


if __name__ == "__main__":
    sys.exit(main())
