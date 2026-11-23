"""Two arms of an arms run against each other on any per-arm value, not only crashes: each arm's repeats, mean and
median, and an exact permutation P for the difference in means (written for the GC heap lever's A/B, 2026-10-22).

    python scripts/arms_values.py heap352 base                                  # every value, latest arms run
    python scripts/arms_values.py heap352 base --value 'hook.*ForcedPerMin' --value passed --folder 20261022-012205
    python scripts/arms_values.py heap352 base --field 'gcHeap.forced*'          # also these status fields' changes

It reads `scenarios.py arms --table --json` (several --folder are pooled), so a value is one of:
- a row number: wall_min, scenario_min, crashes, crashes_outside, and passed / failed (the P and F verdicts);
- hook.NAME, a NAME=N the arm's --after hook printed (arm_readout.py: hook.serverTps, hook.client2ForcedPerMin,
  hook.client1Gaps, hook.serverChunksZeroed); -1 there means "not read" and is left out;
- S:FIELD or C:FIELD, a status field's change over the arm on the server or the client, for each --field.

--value takes exact names or fnmatch patterns (repeatable); with none, every value that some row has as a number.
The P is two-sided: the share of all splits of the pooled repeats into two groups of the arms' sizes whose means
differ at least as much as the arms' do (100,000 random splits when there are more than 200,000). With two repeats
an arm the smallest P possible is 0.33, with four 0.029: few repeats cannot show a small difference.

--count NAME (same names and patterns, repeatable) treats a value as a count of events instead: each arm's counts
and wall minutes are pooled over its repeats that have the value, and the P is the two-sided exact rate test that
`scenarios.py arms --compare` uses for crashes (arms_lib.two_sided_rate_p), with the rate ratio A/B. That suits
counts that are mostly 0 or that vary with the arm's length (forced collections, stream silences), where the
permutation P is weak. Crash rates themselves stay with `arms --compare A B`.
"""
from __future__ import annotations

import argparse
import fnmatch
import json
import random
import statistics
import subprocess
import sys
from itertools import combinations
from math import comb, inf
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
ROOT = Path(__file__).resolve().parents[1]
ROW_NUMBERS = ("wall_min", "scenario_min", "crashes", "crashes_outside")
EXACT_LIMIT = 200_000
SAMPLES = 100_000


def table_rows(folders: list[str], fields: list[str]) -> list[dict]:
    argv = [sys.executable, str(ROOT / "scripts" / "scenarios.py"), "arms", "--table", "--json"]
    for folder in folders:
        argv += ["--folder", folder]
    for field in fields:
        argv += ["--field", field]
    done = subprocess.run(argv, capture_output=True, text=True)
    rows = []
    for line in done.stdout.splitlines():
        if line.startswith("{"):
            rows.append(json.loads(line))
    if not rows:
        raise SystemExit(f"arms_values: no table rows (scenarios.py exit {done.returncode}: {done.stderr.strip()[-300:]})")
    return rows


def number(value: object) -> float | None:
    if isinstance(value, bool):
        return None
    if isinstance(value, (int, float)):
        return float(value)
    try:
        return float(str(value))
    except ValueError:
        return None


def row_values(row: dict) -> dict[str, float]:
    """One arm row's values by name; a hook value of -1 (not read) is left out."""
    out: dict[str, float] = {}
    for key in ROW_NUMBERS:
        v = number(row.get(key))
        if v is not None:
            out[key] = v
    tally = dict((part[0], int(part[1:])) for part in str(row.get("verdicts") or "").split() if part[1:].isdigit())
    if row.get("run"):
        out["passed"] = float(tally.get("P", 0))
        out["failed"] = float(tally.get("F", 0))
    for name, value in (row.get("hook") or {}).items():
        v = number(value)
        if v is not None and v != -1:
            out[f"hook.{name}"] = v
    for name, value in (row.get("fields") or {}).items():
        v = number(value)
        if v is not None:
            out[name] = v
    return out


def permutation_p(a: list[float], b: list[float], seed: int = 0) -> float:
    """Two-sided P that a split of the pooled values into groups of len(a) and len(b) has means at least as far apart
    as a's and b's: exact over every split up to EXACT_LIMIT of them, else over SAMPLES random splits."""
    pooled = a + b
    n, total = len(a), sum(pooled)
    rest = len(pooled) - n
    observed = abs(sum(a) / n - sum(b) / rest)

    def far(picked: tuple[int, ...]) -> bool:
        s = sum(pooled[i] for i in picked)
        return abs(s / n - (total - s) / rest) >= observed - 1e-9

    if comb(len(pooled), n) <= EXACT_LIMIT:
        splits = list(combinations(range(len(pooled)), n))
        return sum(far(s) for s in splits) / len(splits)
    rng = random.Random(seed)
    hits = sum(far(tuple(rng.sample(range(len(pooled)), n))) for _ in range(SAMPLES))
    return (hits + 1) / (SAMPLES + 1)


def compare(rows: list[dict], a: str, b: str, patterns: list[str]) -> list[dict]:
    by_arm: dict[str, list[dict[str, float]]] = {a: [], b: []}
    for row in rows:
        if row.get("arm") in by_arm and row.get("run"):
            by_arm[row["arm"]].append(row_values(row))
    names = list(dict.fromkeys(k for arm in (a, b) for values in by_arm[arm] for k in values))
    if patterns:
        names = [n for n in names if any(fnmatch.fnmatchcase(n, p) for p in patterns)]
    out = []
    for name in names:
        va = [v[name] for v in by_arm[a] if name in v]
        vb = [v[name] for v in by_arm[b] if name in v]
        out.append({"value": name, "a": va, "b": vb,
                    "mean_a": statistics.fmean(va) if va else None, "mean_b": statistics.fmean(vb) if vb else None,
                    "median_a": statistics.median(va) if va else None, "median_b": statistics.median(vb) if vb else None,
                    "p": permutation_p(va, vb) if va and vb else None})
    return out


def arm_values(rows: list[dict], a: str, b: str) -> dict[str, list[tuple[dict[str, float], float]]]:
    """Each of the two arms' run rows as (values, wall minutes)."""
    by_arm: dict[str, list[tuple[dict[str, float], float]]] = {a: [], b: []}
    for row in rows:
        if row.get("arm") in by_arm and row.get("run"):
            by_arm[row["arm"]].append((row_values(row), number(row.get("wall_min")) or 0.0))
    return by_arm


def count_compare(rows: list[dict], a: str, b: str, patterns: list[str]) -> list[dict]:
    """Per value matching patterns: each arm's pooled count and wall minutes over its repeats that have the value,
    the rate ratio A/B and the two-sided exact rate P (None with no events or no minutes on a side)."""
    import arms_lib

    by_arm = arm_values(rows, a, b)
    names = list(dict.fromkeys(k for arm in (a, b) for values, _ in by_arm[arm] for k in values))
    names = [n for n in names if any(fnmatch.fnmatchcase(n, p) for p in patterns)]
    out = []
    for name in names:
        pooled = {}
        for arm in (a, b):
            have = [(values[name], minutes) for values, minutes in by_arm[arm] if name in values]
            pooled[arm] = (int(round(sum(v for v, _ in have))), sum(m for _, m in have), len(have))
        (ka, ma, na), (kb, mb, nb) = pooled[a], pooled[b]
        ratio = None if not (ma and mb) else (inf if kb == 0 and ka else ((ka / ma) / (kb / mb) if kb else None))
        out.append({"value": name, "count_a": ka, "min_a": round(ma, 1), "runs_a": na, "count_b": kb,
                    "min_b": round(mb, 1), "runs_b": nb, "ratio": ratio,
                    "p": arms_lib.two_sided_rate_p(ka, ma, kb, mb) if ka >= 0 and kb >= 0 else None})
    return out


def fmt(v: float | None) -> str:
    return "-" if v is None else f"{v:.4g}"


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("a")
    p.add_argument("b")
    p.add_argument("--folder", action="append", default=[], help="an arms run (latest, a folder name or a path); repeatable")
    p.add_argument("--value", action="append", default=[], help="a value name or fnmatch pattern; repeatable")
    p.add_argument("--field", action="append", default=[], help="also this status field's change (arms --table --field)")
    p.add_argument("--count", action="append", default=[],
                   help="a value (name or pattern) to compare as a count of events over the arms' minutes; repeatable")
    p.add_argument("--json", action="store_true", help="one JSON line per value")
    args = p.parse_args(argv)
    rows = table_rows(args.folder or ["latest"], args.field)
    found = compare(rows, args.a, args.b, args.value) if args.value or not args.count else []
    counted = count_compare(rows, args.a, args.b, args.count) if args.count else []
    if not found and not counted:
        wanted = args.value + args.count
        print(f"arms_values: no value for {args.a} or {args.b}" + (f" matching {', '.join(wanted)}" if wanted else ""))
        return 1
    if args.json:
        for r in found + counted:
            print(json.dumps(r))
        return 0
    series = ", ".join(dict.fromkeys(str(r.get("series")) for r in rows))
    if found:
        print(f"arms {series}: {args.a} against {args.b}; P two-sided, permutation of the repeats")
        print(f"{'value':<30} {'n':>3} {'mean ' + args.a:>14} {'median':>9} {'n':>3} {'mean ' + args.b:>14} {'median':>9} {'P':>7}")
        for r in found:
            print(f"{r['value']:<30} {len(r['a']):>3} {fmt(r['mean_a']):>14} {fmt(r['median_a']):>9} {len(r['b']):>3} "
                  f"{fmt(r['mean_b']):>14} {fmt(r['median_b']):>9} {fmt(r['p']):>7}")
    if counted:
        print(f"arms {series}: {args.a} against {args.b} as counts over wall minutes; P two-sided exact rate test")
        print(f"{'value':<30} {'count/min ' + args.a:>22} {'count/min ' + args.b:>22} {'ratio':>7} {'P':>9}")
        for r in counted:
            print(f"{r['value']:<30} {str(r['count_a']) + ' / ' + str(r['min_a']) + ' (' + str(r['runs_a']) + ')':>22} "
                  f"{str(r['count_b']) + ' / ' + str(r['min_b']) + ' (' + str(r['runs_b']) + ')':>22} "
                  f"{fmt(r['ratio']):>7} {fmt(r['p']):>9}")
        print("count / wall minutes (repeats with the value); ratio is A's rate over B's")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
