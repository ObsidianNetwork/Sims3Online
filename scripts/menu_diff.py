"""A player's pie menu against the server's, for one actor and target.

A client builds its pie menu from its own game: its copies' postures and its own interaction tests. The server runs
the pick with its own tests. This lists both sides for the same actor and target, and prints where they differ:

    python scripts/menu_diff.py --client 2 Pablo Jennifer                 # Pablo's menu on Jennifer, player 2
    python scripts/menu_diff.py --client 1 Jennifer Pablo --type Social   # only definitions whose type has "Social"
    python scripts/menu_diff.py --client 2 Pablo Jennifer --all           # every difference, not the first 12
    python scripts/menu_diff.py --client 2 Pablo type:Bookshelf           # the nearest free bookshelf
    python scripts/menu_diff.py --client 2 Pablo --sweep --sims Jennifer --label idle   # every object type near Pablo
    python scripts/menu_diff.py --client 1 Jennifer Iliana --inputs        # what the tests read, server and client
--inputs prints, for the actor and a target Sim, what a social's tests read on the server and on the client side by
side, with `*` on the rows that differ: position, level, room, lot, parent and posture, the outside answers
(World.IsPositionOutside, IsOutside, LotManager.RoomIdIsOutside), the season, buffs, the traits each knows of the
other, and the lot's active topics (a probe; n15). Positions within 0.1 m count as the same.
--sweep diffs every object type the server's catalog finds within --radius of the actor (scripts/sweep.py catalog, a
probe; or --types A,B) and each --sims Sim, prints each target's counts, then every "shown, server refuses" entry and
their count by the server's result, and saves the tally to .omo/run/menu-sweep/<time>-c<client>-<actor>.json. Run it
once per state (idle, one Sim reading, one seated, an object in use) with --label naming the state.

Groups (an entry is its definition type and its menu text):
  - shown, server refuses: the client's test passes, the server's does not (the player sees what cannot run)
  - missing on the client: the server's test passes, the client has no such entry or its test fails
  - client only: an entry the server does not list at all (its test result on the client is shown)
  - hidden on both sides / hidden on one side, absent on the other: not differences. client_pick marks a failed entry
    `[Result|grey]` (the test left a tooltip: shown, disabled) or `[Result|hidden]` (no tooltip: not shown); the server
    side is hidden when its failure has no reason. A greyed or hidden client entry against a server Pass is still
    missing on the client (tagged greyed or hidden), and a shown client entry the server lacks is still client only.
A client entry the fork's menu-refusal hook greys (the server refused that pick and has not cleared it) lists as
ServerRefused: it agrees with a server refusal, and against a server Pass it is missing on the client (a stale grey).

The client side is `client_pick.py --list` (the active Sim of that client must be the actor); the server side is
`sim_act.py <actor> <target> . --list`. Both are read-only. A target is a Sim's first name, `type:<fragment>` (the
nearest free object of that type: client_pick's nearest to the active Sim, sim_act's `near:` to the actor; the two
object ids are printed and a difference is a warning) or `id:<ObjectId>` (one object, when the nearest differs: each
game has papers of its own).
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PASS = "Pass"
_SERVER_ENTRY = re.compile(r"^(?P<type>\S+) '(?P<name>.*)' (?P<result>\w+)(?: \((?P<reason>.*)\))?$")
_CLIENT_ENTRY = re.compile(r"^(?P<type>\S+) = (?P<name>.*?)(?: \[(?P<result>\w+)(?:\|(?P<state>grey|hidden))?\])?$")


def parse_client(reply: str) -> tuple[str, dict[tuple[str, str], str]]:
    """client_pick's reply: (the client's actor, {(type, name): the client's test result or "?"})."""
    actor = (re.search(r"actor=([^;]*)", reply) or [None, ""])[1].strip()
    listed = reply.split("list=", 1)[1] if "list=" in reply else ""
    entries: dict[tuple[str, str], str] = {}
    for item in listed.split(" | "):
        m = _CLIENT_ENTRY.match(item.strip())
        if m:
            entries[(m["type"], m["name"].strip())] = m["result"] or "?"
    return actor, entries


def parse_client_states(reply: str) -> dict[tuple[str, str], str]:
    """client_pick's `[Result|grey]` or `[Result|hidden]` (a failed test with a tooltip is greyed, without one hidden):
    {(type, name): "grey" or "hidden"} for the entries that failed; a passing entry has no state."""
    listed = reply.split("list=", 1)[1] if "list=" in reply else ""
    states: dict[tuple[str, str], str] = {}
    for item in listed.split(" | "):
        m = _CLIENT_ENTRY.match(item.strip())
        if m and m["state"]:
            states[(m["type"], m["name"].strip())] = m["state"]
    return states


def parse_server(line: str) -> dict[tuple[str, str], tuple[str, str]]:
    """sim_act's `candidates:` line: {(type, name): (result, reason)}."""
    body = line.split("candidates:", 1)[1] if "candidates:" in line else line
    entries: dict[tuple[str, str], tuple[str, str]] = {}
    for item in body.split(" | "):
        m = _SERVER_ENTRY.match(item.strip())
        if m:
            entries[(m["type"], m["name"].strip())] = (m["result"], m["reason"] or "")
    return entries


def targets(target: str) -> tuple[str, str]:
    """The target as (client_pick's form, sim_act's form)."""
    if target.startswith("type:"):
        return target[5:], "near:" + target[5:]
    if target.startswith("id:"):
        return target, target
    name = target[4:] if target.startswith("sim:") else target
    return f"sim:{name}", f"sim:{name}"


def target_id(reply: str) -> str:
    m = re.search(r"target=[^;:\s]*:(\d+)", reply)
    return m[1] if m else "?"


def keep(key: tuple[str, str], type_fragment: str, name_fragment: str) -> bool:
    return type_fragment.lower() in key[0].lower() and name_fragment.lower() in key[1].lower()


HIDDEN_BOTH = "hidden on both sides"
HIDDEN_ONE = "hidden on one side, absent on the other"


def diff(client: dict[tuple[str, str], str], server: dict[tuple[str, str], tuple[str, str]],
         states: dict[tuple[str, str], str] | None = None) -> dict[str, list]:
    """The groups. `states` is parse_client_states: with it, an entry the client hides (a failed test without a tooltip)
    and the server also fails without a reason (hidden there too), or the server does not list, is reported apart as
    hidden, not as a difference. A hidden client-only candidate is therefore not a visible "client only" difference.
    A server result with no reason counts as hidden; one with a reason is greyed."""
    states = states or {}
    groups: dict[str, list] = {"shown, server refuses": [], "missing on the client": [], "client only": [],
                               HIDDEN_BOTH: [], HIDDEN_ONE: [], "agree": []}
    for key, result in sorted(client.items()):
        hidden = states.get(key) == "hidden"
        if key not in server:
            groups[HIDDEN_ONE if hidden else "client only"].append((key, result, ""))
            continue
        s_result, reason = server[key]
        if result == PASS and s_result != PASS:
            groups["shown, server refuses"].append((key, s_result, reason))
        elif result != PASS and s_result == PASS and result != "?":
            groups["missing on the client"].append((key, f"client {result}" + _state_word(states.get(key)), ""))
        elif hidden and s_result != PASS and not reason:
            groups[HIDDEN_BOTH].append((key, result, ""))
        else:
            groups["agree"].append((key, result, ""))
    for key, (s_result, reason) in sorted(server.items()):
        if key in client:
            continue
        if s_result == PASS:
            groups["missing on the client"].append((key, "not listed", ""))
        elif not reason and states:
            groups[HIDDEN_ONE].append((key, f"server {s_result}", ""))
    return groups


def _state_word(state: str | None) -> str:
    return {"grey": " greyed", "hidden": " hidden"}.get(state or "", "")


def report(groups: dict[str, list], limit: int) -> list[str]:
    lines = [", ".join(f"{name}: {len(items)}" for name, items in groups.items())]
    for name, items in groups.items():
        if name == "agree" or not items:
            continue
        lines.append(f"{name}:")
        for (type_name, text), result, reason in items[:limit]:
            lines.append(f"  {type_name} '{text}' {result}" + (f" ({reason})" if reason else ""))
        if len(items) > limit:
            lines.append(f"  ... {len(items) - limit} more (--all)")
    return lines


def exact_failures(groups: dict[str, list]) -> list[str]:
    """The menudiff step's `:exact` check: visible entries missing on the client or only on the client."""
    return [f"{name}: {t} '{n}' {r}" for name in ("missing on the client", "client only") for (t, n), r, _ in groups[name]]


def parse_step(rest: str) -> tuple[str, str, str, str, bool]:
    """menudiff:<client>:<actor>:<target>[:only=<text>][:exact] after `menudiff:` as (client, actor, target, only,
    exact). `:exact` comes last (also after only=)."""
    exact = rest.endswith(":exact")
    if exact:
        rest = rest[:-len(":exact")]
    client, _, more = rest.partition(":")
    actor, _, target = more.partition(":")
    target, _, only = target.partition(":only=")
    return client, actor, target, only, exact


# --inputs: what a social's tests read for an actor and a target Sim, on one game ( n15).
INPUTS_PROBE = ROOT / ".omo" / "run" / "act" / "MenuInputsProbe.cs"

INPUTS_TEMPLATE = r"""using System;
using System.Collections.Generic;
using System.Text;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Seasons;
using Sims3.Gameplay.Socializing;
using Sims3.SimIFace;

namespace SimsOnline.Scratch
{
    // Written by scripts/menu_diff.py --inputs: what a social's tests read for the actor and the target, on this game.
    public class MenuInputsProbe
    {
        public const string ActorName = "__ACTOR__";
        public const string TargetName = "__TARGET__";

        public static string Run()
        {
            try
            {
                Sim actor = Find(ActorName);
                Sim target = Find(TargetName);
                StringBuilder sb = new StringBuilder("ok=true");
                Describe(sb, "a", actor);
                Describe(sb, "t", target);
                Add(sb, "season", !SeasonsManager.Enabled ? "off" : SeasonsManager.CurrentSeason.ToString());
                if (actor != null && target != null)
                {
                    Relationship rel = Relationship.Get(actor, target, false);
                    Add(sb, "rel", rel == null ? "none" : "yes");
                    if (rel != null)
                    {
                        Add(sb, "aKnowsTraits", Count(rel.InformationAbout(target).Traits));
                        Add(sb, "tKnowsTraits", Count(rel.InformationAbout(actor).Traits));
                    }
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "err=" + ex.GetType().Name + ";msg=" + ex.Message;
            }
        }

        private static Sim Find(string firstName)
        {
            foreach (Sim sim in LotManager.Actors)
            {
                if (sim != null && sim.FirstName == firstName)
                {
                    return sim;
                }
            }
            return null;
        }

        private static void Describe(StringBuilder sb, string p, Sim sim)
        {
            if (sim == null)
            {
                Add(sb, p + "Sim", "none");
                return;
            }
            Vector3 pos = sim.Position;
            Add(sb, p + "Pos", F(pos.x) + "," + F(pos.y) + "," + F(pos.z));
            Add(sb, p + "Level", sim.Level.ToString());
            Add(sb, p + "Room", sim.RoomId.ToString());
            Add(sb, p + "Lot", sim.LotCurrent == null ? "none" : sim.LotCurrent.LotId.ToString());
            Add(sb, p + "Parent", sim.Parent == null ? "none" : sim.Parent.GetType().Name);
            Add(sb, p + "Posture", sim.Posture == null ? "none" : sim.Posture.GetType().Name);
            Add(sb, p + "WorldOutside", World.IsPositionOutside(pos) ? "1" : "0");
            Add(sb, p + "IsOutside", sim.IsOutside ? "1" : "0");
            Add(sb, p + "RoomOutside", LotManager.RoomIdIsOutside(sim.RoomId) ? "1" : "0");
            List<string> buffs = new List<string>();
            if (sim.BuffManager != null)
            {
                foreach (BuffInstance buff in sim.BuffManager.Buffs)
                {
                    buffs.Add(buff.Guid.ToString());
                }
            }
            buffs.Sort();
            Add(sb, p + "Buffs", buffs.Count == 0 ? "none" : string.Join(",", buffs.ToArray()));
            List<string> topics = new List<string>();
            if (sim.LotCurrent != null)
            {
                foreach (ActiveTopic topic in sim.LotCurrent.GetActiveTopics())
                {
                    topics.Add(topic.Type);
                }
            }
            topics.Sort();
            Add(sb, p + "LotTopics", topics.Count == 0 ? "none" : string.Join(",", topics.ToArray()));
        }

        private static string Count<T>(IEnumerable<T> items)
        {
            int n = 0;
            foreach (T item in items)
            {
                n++;
            }
            return n.ToString();
        }

        private static string F(float value)
        {
            return value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void Add(StringBuilder sb, string key, string value)
        {
            sb.Append(';').Append(key).Append('=').Append(value.Replace(';', ','));
        }
    }
}
"""


def inputs_source(actor: str, target: str) -> str:
    return INPUTS_TEMPLATE.replace("__ACTOR__", actor.replace('"', "")).replace("__TARGET__", target.replace('"', ""))


def parse_inputs(stdout: str) -> dict[str, str]:
    """The inputs probe's report (bridgectl prints it after DIAG_RUN as indented `key = value` lines)."""
    fields: dict[str, str] = {}
    for line in stdout.split("-- DIAG_RUN --")[-1].splitlines():
        m = re.match(r"\s+(\w+)\s+=\s+(.*)$", line)
        if m:
            fields[m[1]] = m[2].strip()
    return fields


def _same(key: str, server: str, client: str) -> bool:
    if key.endswith("Pos") and server != client:
        try:
            a = [float(v) for v in server.split(",")]
            b = [float(v) for v in client.split(",")]
        except ValueError:
            return False
        return len(a) == len(b) and all(abs(x - y) <= 0.1 for x, y in zip(a, b))
    return server == client


def inputs_diff(server: dict[str, str], client: dict[str, str]) -> list[tuple[str, str, str, bool]]:
    """(key, server value, client value, differs) for every key either side reported, in the server's order first."""
    keys = [k for k in server if k != "ok"] + [k for k in client if k not in server and k != "ok"]
    rows = []
    for key in keys:
        s, c = server.get(key, "-"), client.get(key, "-")
        rows.append((key, s, c, not _same(key, s, c)))
    return rows


def inputs_report(rows: list[tuple[str, str, str, bool]]) -> list[str]:
    width = max([len(k) for k, _, _, _ in rows] + [3])
    lines = [f"  {'key':<{width}}  server | client"]
    lines += [f"{'*' if differs else ' '} {key:<{width}}  {s} | {c}" for key, s, c, differs in rows]
    differing = sum(1 for row in rows if row[3])
    lines.append(f"{differing} of {len(rows)} rows differ")
    return lines


def run_inputs(actor: str, target: str, client_game: str) -> int:
    import compare_client
    import probe_source

    probe = probe_source.write(INPUTS_PROBE, inputs_source(actor, target))
    sides = {}
    for side, endpoint in (("server", "127.0.0.1:34029"), ("client", compare_client.resolve(client_game))):
        out = run(["scripts/bridgectl.py", "--endpoint", endpoint, "probe", str(probe.relative_to(ROOT))])
        sides[side] = parse_inputs(out)
        if sides[side].get("ok") != "true":
            print(f"{side}: no report: " + out.strip()[-300:])
            return 1
    print(f"{actor} and {target}: server against client {client_game}")
    for line in inputs_report(inputs_diff(sides["server"], sides["client"])):
        print(line)
    return 0


def run(args: list[str]) -> str:
    done = subprocess.run([sys.executable, *args], capture_output=True, text=True, timeout=150, cwd=ROOT)
    return done.stdout + done.stderr


def compare(actor: str, target: str, client_game: str) -> tuple[dict | None, list[str]]:
    """Both menus for one target: (diff groups, or None when a side listed nothing; warnings and errors)."""
    client_target, server_target = targets(target)
    client_reply = run(["scripts/client_pick.py", "--client", client_game, client_target, "", "--list"])
    server_reply = run(["scripts/sim_act.py", actor, server_target, ".", "--list"])
    client_actor, client = parse_client(client_reply)
    server_line = next((ln for ln in server_reply.splitlines() if ln.strip().startswith("candidates:")), "")
    server = parse_server(server_line)
    if not client or not server:
        return None, [f"no listing: client {len(client)} entries, server {len(server)}",
                      (client_reply if not client else server_reply).strip()[-400:]]
    notes = []
    if client_actor and not client_actor.lower().startswith(actor.lower()):
        notes.append(f"warning: client {client_game}'s active Sim is {client_actor}, not {actor}: the menus are for different actors")
    if target_id(client_reply) != target_id(server_reply):
        notes.append(f"warning: different targets: client {target_id(client_reply)}, server {target_id(server_reply)}")
    return diff(client, server, parse_client_states(client_reply)), notes


# --sweep: every object type near the actor (sweep.py's catalog of the server's game) and the named Sims, one menu
# diff each, and the server's refusals of entries the client shows counted by definition and by the server's result.

def sweep_targets(entries: list[dict], sims: list[str], types: list[str]) -> list[str]:
    """The named Sims, then the given types or else each object type of the catalog once, nearest first."""
    out = [s for s in sims if s]
    seen: set[str] = set()
    for name in types or [e["object"] for e in entries]:
        if name and name not in seen:
            seen.add(name)
            out.append("type:" + name)
    return out


def tally(per_target: dict[str, dict | None]) -> dict:
    """{targets, listed, groups: {group: count}, refused: [{target, type, name, result, reason}], unlisted: [target]}"""
    out: dict = {"targets": len(per_target), "listed": 0, "groups": {}, "refused": [], "unlisted": []}
    for target, groups in per_target.items():
        if groups is None:
            out["unlisted"].append(target)
            continue
        out["listed"] += 1
        for name, items in groups.items():
            out["groups"][name] = out["groups"].get(name, 0) + len(items)
        for (type_name, text), result, reason in groups["shown, server refuses"]:
            out["refused"].append({"target": target, "type": type_name, "name": text, "result": result, "reason": reason})
    return out


def sweep_report(t: dict) -> list[str]:
    lines = [f"{t['listed']} of {t['targets']} targets listed; " + ", ".join(f"{k}: {v}" for k, v in t["groups"].items())]
    if t["unlisted"]:
        lines.append("not listed: " + ", ".join(t["unlisted"]))
    by_result: dict[str, int] = {}
    for r in t["refused"]:
        by_result[r["result"]] = by_result.get(r["result"], 0) + 1
    if by_result:
        lines.append("shown, server refuses, by the server's result: " + ", ".join(f"{k} {v}" for k, v in sorted(by_result.items())))
    for r in t["refused"]:
        lines.append(f"  {r['target']}: {r['type']} '{r['name']}' {r['result']}" + (f" ({r['reason']})" if r["reason"] else ""))
    return lines


def run_sweep(a: argparse.Namespace) -> int:
    import json
    import time

    entries: list[dict] = []
    types = [t.strip() for t in a.types.split(",") if t.strip()]
    if not types:
        import sweep  # the server's catalog of objects near the actor (a probe)

        entries = sweep.catalog(a.actor, a.radius)
    sims = [s.strip() for s in a.sims.split(",") if s.strip()]
    per_target: dict[str, dict | None] = {}
    for target in sweep_targets(entries, sims, types):
        groups, notes = compare(a.actor, target, a.client)
        per_target[target] = groups
        counts = "no listing" if groups is None else ", ".join(f"{k}: {len(v)}" for k, v in groups.items())
        print(f"{target}: {counts}")
        for note in notes:
            print("  " + note.replace("\n", " ")[:300])
    t = tally(per_target)
    for line in sweep_report(t):
        print(line)
    out = ROOT / ".omo" / "run" / "menu-sweep" / f"{time.strftime('%Y%m%d-%H%M%S')}-c{a.client}-{a.actor}.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps({"actor": a.actor, "client": a.client, "label": a.label, **t}, indent=1), encoding="utf-8")
    print(f"saved {out.relative_to(ROOT)}")
    return 0 if t["listed"] else 1


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("actor", help="first name of the acting Sim (the client's active Sim)")
    p.add_argument("target", nargs="?", default="",
                   help="first name of the target Sim, type:<fragment> for the nearest free object, or id:<ObjectId> (not with --sweep)")
    p.add_argument("--client", default="1", help="the client game: 1 (default), 2 or HOST:PORT")
    p.add_argument("--type", default="", help="only definitions whose type name contains this")
    p.add_argument("--name", default="", help="only entries whose menu text contains this")
    p.add_argument("--all", action="store_true", help="every difference (default: the first 12 of each group)")
    p.add_argument("--sweep", action="store_true",
                   help="every object type near the actor (or --types) and the --sims, one diff each, refusals counted")
    p.add_argument("--sims", default="", help="--sweep: other Sims to include, comma separated first names")
    p.add_argument("--types", default="", help="--sweep: object type fragments instead of the server's catalog")
    p.add_argument("--radius", type=float, default=12.0, help="--sweep: the catalog's radius in metres (default 12)")
    p.add_argument("--label", default="", help="--sweep: a note saved with the result (the state, e.g. 'Jennifer reading')")
    p.add_argument("--inputs", action="store_true",
                   help="what the tests read for the actor and the target Sim, server and client side by side")
    a = p.parse_args(argv)
    if a.sweep:
        return run_sweep(a)
    if not a.target:
        p.error("a target, or --sweep")
    if a.inputs:
        return run_inputs(a.actor, a.target[4:] if a.target.startswith("sim:") else a.target, a.client)

    groups, notes = compare(a.actor, a.target, a.client)
    for note in notes:
        print(note)
    if groups is None:
        return 1
    groups = {name: [item for item in items if keep(item[0], a.type, a.name)] for name, items in groups.items()}
    print(f"{a.actor} on {a.target}, client {a.client} against the server")
    for line in report(groups, 10_000 if a.all else 12):
        print(line)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
