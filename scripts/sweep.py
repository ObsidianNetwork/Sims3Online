"""Interaction sweep: how much of the lot does the client mirror? Every interaction a Sim can pick near it, run as a
scenario, scored by the same checks, and the failures grouped by kind so the next fix goes to a seam, not to one
interaction (AGENTS.md, Design principle).

    python scripts/sweep.py catalog [--sim Pablo] [--radius 12]       # what the Sim could pick near it (server)
    python scripts/sweep.py run [--sim Pablo] [--limit 20] [--only REGEX] [--skip REGEX]
    python scripts/sweep.py summary [RUN]                            # a sweep run's failures grouped by kind

catalog asks the server's game (a probe) for every object within --radius metres of the Sim (not Sims), every
interaction the object offers that Sim, and the game's own test result; it keeps one entry per object type and
interaction type that passes. run turns each entry into a scenario (scenarios.py: the reset, `act:<Sim> near:<Type>
<Interaction> --clear --wait 40`, a motion watch, `sims:<Sim> --machines --props` after) and runs them in one run folder
(.omo/run/scenarios/<time>-sweep): scenarios.py history / explain / rescore work on it. Picks that change the world for
good or leave the lot are skipped by default (--skip). Long: about 1.5 minutes an interaction; run it in the background.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import time
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))
import probe_source  # noqa: E402
import scenarios  # noqa: E402

PROBE = ROOT / ".omo" / "run" / "act" / "SweepCatalogProbe.cs"
CATALOG = ROOT / ".omo" / "run" / "sweep-catalog.json"
# Picks that sell, buy, move or destroy things, leave the lot, call out, or start fires: not for an unattended sweep.
DEFAULT_SKIP = (r"Sell|Buy|Purchase|Delete|Destroy|Move|PickUp|Pickup|Carry|Steal|Disassemble|Repair|Upgrade|Travel|GoToLot|"
                r"Leave|Visit|Call|Phone.*(Invite|Order)|Fire|Kill|Quit|Fight|Rob|Clean|Sweep|Mop|Trash")

TEMPLATE = r"""
using System;
using System.Collections.Generic;
using System.Text;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.SimIFace;
using Sims3.UI;

namespace SimsOnline.Scratch
{
    public static class SweepCatalogProbe
    {
        public static string Run()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                Sim actor = null;
                foreach (Sim sim in Sims3.Gameplay.Queries.GetObjects<Sim>())
                {
                    if (sim.FirstName == "__SIM__")
                    {
                        actor = sim;
                        break;
                    }
                }

                if (actor == null)
                {
                    return "err=no Sim named __SIM__";
                }

                Dictionary<string, bool> seen = new Dictionary<string, bool>();
                int objects = 0;
                foreach (GameObject obj in Sims3.Gameplay.Queries.GetObjects<GameObject>(actor.Position, __RADIUS__f))
                {
                    if (obj is Sim || obj.InInventory)
                    {
                        continue;
                    }

                    objects++;
                    List<InteractionObjectPair> iops;
                    try
                    {
                        iops = obj.GetAllInteractionsForActor(actor);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    foreach (InteractionObjectPair iop in iops)
                    {
                        if (iop.InteractionDefinition == null)
                        {
                            continue;
                        }

                        string kind = iop.InteractionDefinition.GetType().FullName;
                        int plus = kind.LastIndexOf('+');
                        string shortKind = (plus > 0 ? kind.Substring(kind.LastIndexOf('.', plus) + 1) : kind).Replace("+Definition", "");
                        string key = obj.GetType().Name + "|" + shortKind;
                        if (seen.ContainsKey(key))
                        {
                            continue;
                        }

                        InteractionInstanceParameters parameters = new InteractionInstanceParameters(iop, actor,
                            new InteractionPriority(InteractionPriorityLevel.UserDirected), AutonomySearchType.None,
                            InteractionFlags.UserDirected, GameObjectHit.NoHit, null, false);
                        GreyedOutTooltipCallback why = null;
                        InteractionTestResult result;
                        try
                        {
                            result = iop.InteractionDefinition.Test(ref parameters, ref why);
                        }
                        catch (Exception)
                        {
                            continue;
                        }

                        if (result != InteractionTestResult.Pass)
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

                        seen[key] = true;
                        float distance = (obj.Position - actor.Position).Length();
                        sb.Append("\nentry=").Append(obj.GetType().Name).Append('|').Append(shortKind).Append('|')
                            .Append(name.Replace("|", "/").Replace(";", ",").Replace("\n", " ")).Append('|')
                            .Append(distance.ToString("F1"));
                    }
                }

                return "objects=" + objects + sb.ToString();
            }
            catch (Exception ex)
            {
                return "err=" + ex.GetType().Name + ": " + ex.Message;
            }
        }
    }
}
"""


def catalog(sim: str, radius: float) -> list[dict]:
    source = TEMPLATE.replace("__SIM__", sim).replace("__RADIUS__", f"{radius:.1f}")
    probe = probe_source.write(PROBE, source)
    code, text = scenarios.python("scripts/bridgectl.py", "--endpoint", scenarios.SERVER, "probe", str(probe.relative_to(ROOT)), timeout=300)
    entries = []
    for line in text.splitlines():
        line = line.strip()
        if line.startswith("entry="):
            f = line[len("entry="):].split("|")
            if len(f) >= 4:
                entries.append({"object": f[0], "interaction": f[1], "name": f[2], "distance": float(f[3] or 0)})
    if not entries:
        err = next((ln.strip() for ln in text.splitlines() if "err=" in ln or "error" in ln.lower()), text.strip()[-300:])
        print("catalog: no entries; " + err)
    entries.sort(key=lambda e: (e["distance"], e["object"], e["interaction"]))
    CATALOG.parent.mkdir(parents=True, exist_ok=True)
    CATALOG.write_text(json.dumps({"sim": sim, "radius": radius, "entries": entries}, indent=1), encoding="utf-8")
    return entries


def spec_for(sim: str, entry: dict) -> dict:
    name = re.sub(r"[^A-Za-z0-9]+", "-", f"sweep-{entry['object']}-{entry['interaction']}").strip("-").lower()[:60]
    return {
        "name": name,
        "summary": f"{sim}: {entry['name'] or entry['interaction']} on {entry['object']} (sweep)",
        "steps": [f"act:{sim} near:{entry['object']} {entry['interaction']} --clear --wait 40", "sleep:8"],
        "watch": {"sims": [sim], "seconds": 30},
        "after": [f"sims:{sim} --machines --props"],
        "expect": "pass",
    }


# A motion failure lists the events' details (motion_watch.events), not their kinds: name each by its shape.
MOTION_SHAPES = (
    ("slide", re.compile(r"without a walk")),
    ("stutter", re.compile(r"stopped and started")),
    ("restart", re.compile(r"entered again|back to ")),
    ("jitter", re.compile(r"movement reversed")),
    ("turn jitter", re.compile(r"turning reversed")),
    ("snap turn", re.compile(r"\d+ deg in \d+ ms")),
    ("tick cadence", re.compile(r"walking ticks")),
    ("pace", re.compile(r"the server's speed at the same places")),
    ("teleport", re.compile(r"[\d.]+ m in \d+ ms")),
)


def kinds(record: dict) -> list[str]:
    """The failure kinds of one record: motion event kinds, the compared fields that differ, or blocked."""
    if record["verdict"] == "BLOCKED":
        return ["blocked"]
    if record["verdict"] == "CRASHED":
        return ["crashed"]  # a game went down in it: its other failures say nothing about the interaction
    out = []
    for failure in record.get("failures", []):
        if failure.startswith("motion:"):
            details = failure.split(":", 2)[-1].split("; ")
            for detail in details:
                kind = next((k for k, pattern in MOTION_SHAPES if pattern.search(detail)), None)
                if kind:
                    out.append("motion:" + kind)
        elif failure.startswith("compare:"):
            out += ["compare:" + m for m in re.findall(r"! (\w+)", failure)]
        else:
            out.append(failure.split(":")[0])
    return sorted(set(out))


def summary(run: Path) -> int:
    records = scenarios.load_run(run)
    verdicts = Counter(r["verdict"] for r in records)
    print(f"sweep {run.name}: {len(records)} interactions; " + ", ".join(f"{v} {n}" for v, n in verdicts.most_common()))
    by_kind: dict[str, list[str]] = {}
    for r in records:
        for k in kinds(r) if r["verdict"] != "PASS" else []:
            by_kind.setdefault(k, []).append(r["name"].replace("sweep-", ""))
    for k, names in sorted(by_kind.items(), key=lambda e: -len(e[1])):
        print(f"  {k:<22} {len(names):>3}  {', '.join(names[:6])}{' ...' if len(names) > 6 else ''}")
    print(f"report: {run / 'report.md'}  (scenarios.py explain <name> --run {run.name})")
    return 0


def run_sweep(sim: str, limit: int, only: str, skip: str, radius: float) -> int:
    problem = scenarios.health()
    if problem:
        print(problem)
        return 6
    entries = catalog(sim, radius)
    pick = [e for e in entries if not re.search(skip, e["interaction"] + " " + e["name"], re.I)
            and (not only or re.search(only, e["object"] + " " + e["interaction"] + " " + e["name"], re.I))][:limit]
    print(f"catalog: {len(entries)} interactions near {sim}; sweeping {len(pick)}", flush=True)
    out = scenarios.RUNS / (time.strftime("%Y%m%d-%H%M%S") + "-sweep")
    out.mkdir(parents=True, exist_ok=True)
    records = []
    for index, entry in enumerate(pick):
        problem = scenarios.health() if index else None
        if problem:
            print(f"   {problem}: stopping", flush=True)
            break
        spec = spec_for(sim, entry)
        started = time.time()
        record = scenarios.run_scenario(spec, out)
        records.append(record)
        print(f"-- {spec['name']}: {record['verdict']} in {time.time() - started:.0f}s"
              + (f": {'; '.join(record['failures'])[:200]}" if record["failures"] else ""), flush=True)
        scenarios.report(out, records)
    scenarios.python("scripts/sim_act.py", "--release", "all")
    scenarios.report(out, records)
    return summary(out)


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = p.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("catalog")
    c.add_argument("--sim", default="Pablo")
    c.add_argument("--radius", type=float, default=12.0)
    r = sub.add_parser("run")
    r.add_argument("--sim", default="Pablo")
    r.add_argument("--radius", type=float, default=12.0)
    r.add_argument("--limit", type=int, default=20)
    r.add_argument("--only", default="")
    r.add_argument("--skip", default=DEFAULT_SKIP)
    s = sub.add_parser("summary")
    s.add_argument("run", nargs="?", default="")
    a = p.parse_args(argv)
    if a.cmd == "catalog":
        entries = catalog(a.sim, a.radius)
        skipped = [e for e in entries if re.search(DEFAULT_SKIP, e["interaction"] + " " + e["name"], re.I)]
        for e in entries:
            mark = "skip" if e in skipped else "    "
            print(f"{mark} {e['distance']:5.1f} m  {e['object']:<28} {e['interaction']:<32} {e['name'][:40]}")
        print(f"{len(entries)} interactions ({len(skipped)} skipped by default) -> {CATALOG}")
        return 0 if entries else 1
    if a.cmd == "summary":
        sweeps = [d for d in scenarios.runs() if d.name.endswith("-sweep")]
        run = Path(a.run) if a.run and Path(a.run).is_dir() else next((d for d in reversed(sweeps) if not a.run or d.name.startswith(a.run)), None)
        if run is None:
            print("no sweep run yet")
            return 1
        return summary(run)
    return run_sweep(a.sim, a.limit, a.only, a.skip, a.radius)


if __name__ == "__main__":
    sys.exit(main())
