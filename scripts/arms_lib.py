"""Helpers for scenarios.py arms and scenarios.py exposure --pool (no bridge, no games): a small safe expression
language for pools and gates, the exact binomial and rate-ratio statistics behind a pooled crash table, and the loader
that reads arm logs (scenarios.py arms' arms.jsonl, and Session C's hand-run arms4-6.jsonl logs of 2026-10-18).

Tests: python -m unittest scripts.arms_lib_test
"""
from __future__ import annotations

import ast
import json
import operator
import re
from datetime import datetime, timedelta
from math import comb, inf
from pathlib import Path
from typing import Callable

# --- expressions -----------------------------------------------------------------------------------------------------
# Pools (exposure --pool LABEL=EXPR) and gates (arms --gate) are Python-like boolean expressions over dotted names
# (delta.server.spareResent), numbers, strings and a few named functions (on("sacs.seed-copy")). Only these node kinds
# are allowed: no attribute access on objects, no subscripts, no lambdas, so an expression can only read the names the
# caller hands it.

_BINOPS = {ast.Add: operator.add, ast.Sub: operator.sub, ast.Mult: operator.mul, ast.Div: operator.truediv}
_CMPOPS = {ast.Eq: operator.eq, ast.NotEq: operator.ne, ast.Lt: operator.lt, ast.LtE: operator.le, ast.Gt: operator.gt,
           ast.GtE: operator.ge, ast.In: lambda a, b: a in b, ast.NotIn: lambda a, b: a not in b}
_CONSTANTS = {"true": True, "false": False, "True": True, "False": False, "none": None, "None": None}


class ExprError(ValueError):
    """An expression that does not parse, uses something not allowed, or cannot be evaluated."""


class MissingName(ExprError):
    """A name the expression reads that the caller does not have (a status key no sample carried)."""


def dotted(node: ast.AST) -> str | None:
    if isinstance(node, ast.Name):
        return node.id
    if isinstance(node, ast.Attribute):
        base = dotted(node.value)
        return f"{base}.{node.attr}" if base else None
    return None


def _check(node: ast.AST, text: str) -> None:
    allowed = (ast.BoolOp, ast.And, ast.Or, ast.UnaryOp, ast.Not, ast.USub, ast.UAdd, ast.BinOp, ast.Compare,
               ast.Constant, ast.Call, ast.Tuple, ast.List, ast.Load) + tuple(_BINOPS) + tuple(_CMPOPS)
    if isinstance(node, (ast.Name, ast.Attribute)):
        name = dotted(node)
        if name is None or any(part.startswith("_") for part in name.split(".")):
            raise ExprError(f"{text!r}: only dotted names, none starting with _, are allowed")
        return
    if isinstance(node, ast.Call):
        if not isinstance(node.func, ast.Name) or node.func.id.startswith("_") or node.keywords:
            raise ExprError(f"{text!r}: only plain calls such as on(\"name\") are allowed")
        for arg in node.args:
            _check(arg, text)
        return
    if not isinstance(node, allowed):
        raise ExprError(f"{text!r}: {type(node).__name__} is not allowed")
    for child in ast.iter_child_nodes(node):
        _check(child, text)


def compile_expr(text: str) -> ast.AST:
    """Parse and check an expression once (so a typo fails before any game is touched); evaluate() runs it."""
    try:
        tree = ast.parse(text.strip(), mode="eval")
    except SyntaxError as ex:
        raise ExprError(f"{text!r}: {ex.msg}") from None
    _check(tree.body, text)
    return tree.body


def number(value: object) -> object:
    """A status value as an int or float when it is one ("39", "-2", "0.5"), else unchanged ("on", "1/0")."""
    if isinstance(value, str):
        for kind in (int, float):
            try:
                return kind(value)
            except ValueError:
                pass
    return value


def evaluate(node: ast.AST, lookup: Callable[[str], object], functions: dict[str, Callable] | None = None) -> object:
    """Evaluate a compiled expression: names go to lookup (raise MissingName for one it lacks), calls to functions."""
    functions = functions or {}

    def ev(n: ast.AST) -> object:
        if isinstance(n, ast.Constant):
            return n.value
        if isinstance(n, (ast.Name, ast.Attribute)):
            name = dotted(n) or ""
            return _CONSTANTS[name] if name in _CONSTANTS else lookup(name)
        if isinstance(n, ast.BoolOp):
            if isinstance(n.op, ast.And):
                result: object = True
                for value in n.values:
                    result = ev(value)
                    if not result:
                        return result
                return result
            result = False
            for value in n.values:
                result = ev(value)
                if result:
                    return result
            return result
        if isinstance(n, ast.UnaryOp):
            value = ev(n.operand)
            if isinstance(n.op, ast.Not):
                return not value
            try:
                return -value if isinstance(n.op, ast.USub) else +value  # type: ignore[operator]
            except TypeError as ex:
                raise ExprError(str(ex)) from None
        if isinstance(n, ast.BinOp):
            try:
                return _BINOPS[type(n.op)](ev(n.left), ev(n.right))
            except (TypeError, ZeroDivisionError) as ex:
                raise ExprError(str(ex)) from None
        if isinstance(n, ast.Compare):
            left = ev(n.left)
            for op, comparator in zip(n.ops, n.comparators):
                right = ev(comparator)
                try:
                    if not _CMPOPS[type(op)](left, right):
                        return False
                except TypeError as ex:
                    raise ExprError(f"cannot compare {left!r} with {right!r}: {ex}") from None
                left = right
            return True
        if isinstance(n, (ast.Tuple, ast.List)):
            return tuple(ev(e) for e in n.elts)
        if isinstance(n, ast.Call):
            name = n.func.id  # type: ignore[attr-defined]
            if name not in functions:
                raise ExprError(f"no function {name}() here (have: {', '.join(sorted(functions)) or 'none'})")
            return functions[name](*(ev(a) for a in n.args))
        raise ExprError(f"{type(n).__name__} is not allowed")

    return ev(node)


# --- statistics ------------------------------------------------------------------------------------------------------

def binomial_tail(k: int, n: int, p: float, upper: bool = True) -> float:
    """P(X >= k) (upper) or P(X <= k) for X ~ Binomial(n, p)."""
    span = range(k, n + 1) if upper else range(0, k + 1)
    return min(1.0, sum(comb(n, i) * p ** i * (1 - p) ** (n - i) for i in span))


def clopper_pearson(k: int, n: int, alpha: float = 0.05) -> tuple[float, float]:
    """The exact (Clopper-Pearson) two-sided 1 - alpha interval for a binomial proportion k of n."""
    def solve(f: Callable[[float], float], rising: bool) -> float:
        lo, hi = 0.0, 1.0
        for _ in range(80):
            mid = (lo + hi) / 2
            if (f(mid) < alpha / 2) == rising:
                lo = mid
            else:
                hi = mid
        return (lo + hi) / 2

    low = 0.0 if k == 0 else solve(lambda p: binomial_tail(k, n, p, upper=True), rising=True)
    high = 1.0 if k == n else solve(lambda p: binomial_tail(k, n, p, upper=False), rising=False)
    return low, high


def compare_rates(ka: int, ma: float, kb: int, mb: float) -> dict:
    """Two crash rates (ka crashes in ma minutes against kb in mb). Conditional on the total n = ka + kb, A's count is
    Binomial(n, share) with share = ma / (ma + mb) when the rates are equal: p is that test's one-sided P in the
    direction the data lean (P(X >= ka) when A's rate is the higher). The rate ratio's exact 95% interval comes from
    the Clopper-Pearson interval of ka / n, mapped through ratio = q / (1 - q) * mb / ma."""
    n = ka + kb
    share = ma / (ma + mb) if ma + mb > 0 else 0.0
    out: dict = {"ka": ka, "kb": kb, "n": n, "share": share, "p": None, "higher": None, "ratio": None, "ci": None}
    if n == 0 or ma <= 0 or mb <= 0:
        return out
    higher = ka / n >= share
    out["higher"] = higher
    out["p"] = binomial_tail(ka, n, share, upper=higher)
    out["ratio"] = (ka / ma) / (kb / mb) if kb else inf

    def to_ratio(q: float) -> float:
        return inf if q >= 1 else q / (1 - q) * mb / ma

    low, high = clopper_pearson(ka, n)
    out["ci"] = (to_ratio(low), to_ratio(high))
    return out


def two_sided_rate_p(ka: int, ma: float, kb: int, mb: float) -> float | None:
    """The two-sided exact P that two crash rates are equal (ka crashes in ma minutes against kb in mb). Conditional
    on n = ka + kb, A's count is Binomial(n, ma / (ma + mb)); P sums every count no likelier than ka (the minimum-
    likelihood rule, scipy's binomtest default), so it is not twice compare_rates' one-sided P. None with no crashes
    or no exposure on a side. Session D's figures use it (2026-10-18: 3 in 57.8 min against 8 in 75.1 gives 0.37)."""
    n = ka + kb
    if n == 0 or ma <= 0 or mb <= 0:
        return None
    share = ma / (ma + mb)
    pmf = [comb(n, i) * share ** i * (1 - share) ** (n - i) for i in range(n + 1)]
    return min(1.0, sum(x for x in pmf if x <= pmf[ka] * (1 + 1e-7)))


def stratified_rate_p(strata: list[tuple[int, float, int, float]]) -> float | None:
    """The one-sided exact P of A's total crash count across strata (each ka crashes in ma minutes against kb in mb),
    conditional on each stratum's count: A's count in a stratum is Binomial(ka + kb, ma / (ma + mb)), the strata are
    independent, and P is the tail of their sum on the side A's observed total lies (at or above its expectation: this
    many or more, else this many or fewer). With every crash on A's side it is the product of each stratum's share to
    its count (Session D, 2026-10-19: 4 in 33.03 first-arm minutes against 0 in 25.65, 1 in 28.62 later against 0 in
    51.56 gives 0.036). None with no crashes; a stratum with no exposure on a side is left out."""
    dist, observed, expected = [1.0], 0, 0.0
    for ka, ma, kb, mb in strata:
        n = ka + kb
        if n == 0 or ma <= 0 or mb <= 0:
            continue
        share = ma / (ma + mb)
        pmf = [comb(n, i) * share ** i * (1 - share) ** (n - i) for i in range(n + 1)]
        dist = [sum(dist[j] * pmf[k - j] for j in range(len(dist)) if 0 <= k - j <= n) for k in range(len(dist) + n)]
        observed, expected = observed + ka, expected + n * share
    if len(dist) == 1:
        return None
    tail = dist[observed:] if observed >= expected else dist[:observed + 1]
    return min(1.0, sum(tail))


def _num(value: float) -> str:
    if value == inf:
        return "inf"
    return f"{value:.2f}" if value < 10 else f"{value:.1f}"


def describe_comparison(a: str, b: str, c: dict) -> str:
    if c["p"] is None:
        return f"{a} against {b}: no crashes to compare" if c["n"] == 0 else f"{a} against {b}: one side has no exposure"
    sign = ">=" if c["higher"] else "<="
    ci = c["ci"]
    return (f"{a} against {b}: {c['ka']} of {c['n']} crashes against an exposure share of {c['share']:.2f}; "
            f"P(A {sign} {c['ka']} | equal rates) = {c['p']:.3f} (one-sided binomial); rate ratio {_num(c['ratio'])} "
            f"(exact 95% CI {_num(ci[0])}-{_num(ci[1])})")


# --- arm logs --------------------------------------------------------------------------------------------------------
# A pooled row: {"label": "base-1", "arm": "base", "series": "arms6", "minutes": 4.57, "on": {"server:sacs.x": "1"},
# "had": {"server": {"sacs.x"}, "client": set()}, "crashes": [(side, phase, report)], "debugger": bool, "fixed": None}
# Phases: boot (the arm's own boot, arms --boot-hook), setup (the arm's tunables, settle and before hook), run (the
# suite), after (the after hook: a pull), gap (after the arm ended, up to the next arm's setup or boot hook: charged to
# the arm before, as the crash chase counted).

OFF_VALUES = {"", "0", "0.0", "default", "off", "false", "False", "none"}
SIDES = ("server", "client")
# Session C's hand-run logs (2026-10-18, scratch arms4.py-arms6.py): their columns and the tunables they stood for.
LEGACY_TUNES = {"copy": ("both", "sacs.seed-copy"), "keyed": ("both", "sacs.keyed-picks"),
                "node_order": ("both", "sacs.node-order"), "read_loop": ("server", "sacs.seed-read-loop"),
                "gc": ("server", "sacs.gc-pressure")}
LEGACY_PHASES = {"pull": "after"}


def expand_tune(item: str, default_side: str = "client") -> list[tuple[str, str, str]]:
    """[server:|client:|both:]name=value as (side, name, value) per game."""
    head, _, rest = item.partition(":")
    side, pair = (head, rest) if head in ("server", "client", "both") and "=" in rest else (default_side, item)
    name, _, value = pair.partition("=")
    return [(s, name, value) for s in (SIDES if side == "both" else (side,))]


def args_tunes(args: list[str]) -> list[str]:
    """The --tune and --held items in a scenarios.py run argument list."""
    out = []
    for flag, value in zip(args, args[1:]):
        if flag in ("--tune", "--held"):
            out.append(value)
    return out


def crash_parts(text: str) -> tuple[str, str]:
    """(side, report name) from a runner crash line "server xcpt HOST 26-10-18 12.42.32.txt: triage"."""
    side, _, rest = text.partition(" ")
    if side not in SIDES:
        side, rest = "?", text
    name = rest.split(".txt", 1)[0] + ".txt" if ".txt" in rest else rest
    return side, name


def crash_note(text: str) -> str:
    """The triage the runner logged after a crash's report name ("...12.42.32.txt: triage"), or ""."""
    return text.split(".txt: ", 1)[1].strip() if ".txt: " in text else ""


# A runner row's crashes by phase: the phase and the row's key, in the order an arm runs them.
CRASH_KEYS = (("boot", "crashes_boot"), ("setup", "crashes_setup"), ("setup", "crashes_before"), ("run", "crashes"),
              ("after", "crashes_after"), ("gap", "crashes_gap"))


def crash_family(triage: str) -> str:
    """A triage line's short name: the known entry ("known interp-indirect-load" -> interp-indirect-load), "NEW
    00e51f25" for a new interpreter site, "unknown 00d7084b" for any other, "?" with no triage."""
    known = re.search(r": known ([\w.-]+)", triage)
    if known:
        return known.group(1)
    site = re.search(r" at ([0-9a-f]{8})", triage)
    if "NEW interpreter" in triage:
        return f"NEW {site.group(1)}" if site else "NEW"
    return (f"unknown {site.group(1)}" if site else "unknown") if triage else "?"


def first_arm(row: dict, side: str = "server") -> bool | None:
    """Whether an arm was its game's first: that side's process ran no arm before it (process.SIDE.arms_before empty,
    from the snapshot at the arm's start), the rule Session D's doc counts first and later arms by (2026-10-19). None
    when the row has no snapshot (older logs)."""
    before = ((row.get("process") or {}).get(side) or {}).get("arms_before")
    return None if before is None else not before


def watchdog_hang(record: dict) -> str | None:
    """The run watchdog's line when it cut this run record short (scenarios.py watched(): CRASHED, crashed_over "cut
    short", a failure "watchdog: ...") on a game whose process was still running: a bridge that stopped answering, a
    frozen heartbeat, a still clock. That is a hang, not a crash: arms counts it apart (Main, 2026-10-22). None for
    any other record, and for a "game gone" fire, which is a death."""
    if record.get("verdict") != "CRASHED" or record.get("crashed_over") != "cut short":
        return None
    line = next((str(f) for f in record.get("failures") or [] if str(f).startswith("watchdog: ")), None)
    return None if line is None or ": game gone (" in line else line


def crash_scenario(row: dict) -> str:
    """The scenario a run crash happened in: the one book_minutes found (died_scenario), else the one marked CRASHED,
    else (rows from before that verdict, 2026-10-19) the last one with a time when the arm died in its run, else ""."""
    if row.get("died_scenario") and row["died_scenario"] != "?":
        return row["died_scenario"]
    verdicts = row.get("verdicts") or {}
    crashed = [name for name, verdict in verdicts.items() if verdict == "CRASHED"]
    if crashed:
        return crashed[0]
    if row.get("died_in") != "run":
        return ""
    timed = [name for name in verdicts if (row.get("totals") or {}).get(name) is not None]
    return timed[-1] if timed else ""


def gc_context(gc_rows: list[dict], side: str, when: float, pid: int | None = None) -> dict:
    """What gc_watch saw of the game that crashed at `when` (epoch): its pid (the exit row nearest the crash on that
    side, within 10 s, as Session D picked it; else the pid given), uptime in minutes from the watch's first open row
    of that pid, the last collection's distance before the crash in seconds, the interval before that one, and its
    count. Missing values are None (no watch, or none of that process)."""
    exits = [r for r in gc_rows if r.get("kind") == "exit" and r.get("side") == side and abs(r["t"] - when) <= 10]
    if exits:
        pid = min(exits, key=lambda r: abs(r["t"] - when))["pid"]
    mine = [r for r in gc_rows if r.get("pid") == pid and r.get("side") == side] if pid is not None else []
    opens = [r["t"] for r in mine if r.get("kind") == "open"]
    gcs = [r for r in mine if r.get("kind") == "gc" and r["t"] <= when]
    return {"pid": pid, "uptime_min": (when - min(opens)) / 60 if opens else None,
            "last_gc_s": when - gcs[-1]["t"] if gcs else None,
            "interval_s": gcs[-1]["t"] - gcs[-2]["t"] if len(gcs) > 1 else None,
            "gc": gcs[-1]["gc"] if gcs else None}


def arm_rows(records: list[dict]) -> list[dict]:
    """A runner log's arm rows, skipped arms included (a boot hook's crash can be on one), each with its gap record's
    crashes as crashes_gap (the runner writes the gap after the arm's row). Copies; legacy rows are not handled."""
    rows: list[dict] = []
    for r in records:
        if r.get("kind") == "gap":
            target = next((x for x in reversed(rows) if x.get("arm") == r.get("arm") and x.get("repeat") == r.get("repeat")), None)
            if target is not None:
                target["crashes_gap"] = list(target.get("crashes_gap") or []) + list(r.get("crashes") or [])
        elif "arm" in r and r.get("kind") is None:
            rows.append(dict(r))
    return rows


def crash_rows(records: list[dict], reports: dict[tuple[str, str], Path], describe: Callable[[Path], str],
               gc_rows: list[dict], when: Callable[[Path | None, str], float | None]) -> list[dict]:
    """One row per crash in an arms log (gap records' crashes on the arm before them): its time (when(path, name)),
    side, arm, repeat, phase, scenario, the arm's place in its process (first, and how many arms that game ran before),
    gc_context, the dumps the arm row's catcher wrote for that game with their sizes (dump_sizes: the names start
    side-pid-), and the triage now (describe, when its report is still on disk: reports {(side, name): path}) or else
    the one the runner logged, with where it came from."""
    out = []
    for row in arm_rows(records):
        for phase, key in CRASH_KEYS:
            for text in row.get(key) or []:
                side, name = crash_parts(text)
                path = reports.get((side, name))
                try:
                    triage, source = (describe(path), "report") if path else (crash_note(text), "logged")
                except Exception:
                    triage, source = crash_note(text), "logged"
                t = when(path, name)
                process = (row.get("process") or {}).get(side) or {}
                context = gc_context(gc_rows, side, t, process.get("pid")) if t is not None else {"pid": process.get("pid")}
                before = process.get("arms_before")
                pid = context.get("pid") or process.get("pid")
                dumps = {n: size for n, size in (row.get("dump_sizes") or {}).items()
                         if n.startswith(f"{side}-{pid}-" if pid else f"{side}-")}
                out.append({"t": t, "side": side, "name": name, "arm": row.get("arm"), "repeat": row.get("repeat", 1),
                            "phase": phase, "scenario": crash_scenario(row) if phase == "run" else "",
                            "first": first_arm(row, side), "arms_before": None if before is None else len(before),
                            **context, "dumps": dumps, "triage": triage, "source": source})
    return sorted(out, key=lambda r: (r["t"] is None, r["t"] or 0))


def base_name(arm: str) -> str:
    """base-2 -> base (a legacy label); a runner arm name has no repeat suffix."""
    m = re.fullmatch(r"(.+?)-\d+", arm)
    return m.group(1) if m else arm


def legacy_phases(r: dict) -> list[str]:
    """A legacy row's crash phases: its crash_phase column (arms6), else from died_in_run / died_in_pull (arms5)."""
    if r.get("crash_phase"):
        return list(r["crash_phase"])
    return ["after" if r.get("died_in_pull") and not r.get("died_in_run") else "run"] * len(r.get("crashes", []))


def legacy_notes(r: dict) -> dict[tuple[str, str], str]:
    """Each crash's triage from a legacy row's "triage" (arms5/6.py: crash_catch.py triage over the row's reports,
    whitespace joined and cut at 400 characters): (side, name) -> the text after "NAME (profile): " up to the next
    report's name. A report TS3 has since deleted can still be matched by it (exposure --crash-match)."""
    text = r.get("triage") or ""
    found = sorted((text.find(name), name, where) for name, where in zip(r.get("crashes", []), r.get("crash_where", []))
                   if name and text.find(name) >= 0)
    notes = {}
    for i, (at, name, where) in enumerate(found):
        end = found[i + 1][0] if i + 1 < len(found) else len(text)
        notes[(where, name)] = re.sub(r"^\([^)]*\):\s*", "", text[at + len(name):end].strip())
    return notes


def legacy_row(r: dict, series: str) -> dict:
    on = {}
    for column, (side, name) in LEGACY_TUNES.items():
        if column in r:
            for s in (SIDES if side == "both" else (side,)):
                on[f"{s}:{name}"] = str(r[column])
    had = {s: {"sacs.seed-copy"} if r.get("process_had_seeds") else set() for s in SIDES}
    phases = legacy_phases(r)
    crashes = [(where, LEGACY_PHASES.get(phase, phase), name)
               for name, where, phase in zip(r.get("crashes", []), r.get("crash_where", []), phases)]
    return {"label": r["arm"], "arm": base_name(r["arm"]), "series": series, "minutes": float(r.get("scenario_min") or 0),
            "on": on, "had": had, "crashes": crashes, "debugger": bool(r.get("debugger")), "fixed": None,
            "run": r.get("run"), "notes": legacy_notes(r)}


def runner_row(r: dict, series: str) -> dict:
    on = {}
    for item in list(r.get("sets") or []) + args_tunes(r.get("args") or []):
        for side, name, value in expand_tune(item):
            on[f"{side}:{name}"] = value
    process = r.get("process") or {}
    had = {s: set((process.get(s) or {}).get("had") or []) for s in SIDES}
    crashes, notes = [], {}
    for phase, key in CRASH_KEYS:
        for text in r.get(key) or []:
            side, name = crash_parts(text)
            crashes.append((side, phase, name))
            if crash_note(text):
                notes[(side, name)] = crash_note(text)
    return {"label": f"{r['arm']}#{r.get('repeat', 1)}", "arm": r["arm"], "series": series,
            "minutes": float(r.get("scenario_min") or 0), "on": on, "had": had, "crashes": crashes,
            "debugger": bool(r.get("debugger")), "fixed": None, "run": r.get("run"), "notes": notes}


def fold_gaps(records: list[dict]) -> list[dict]:
    """A log's arm rows with each gap record's crashes put on the arm before it (runner: kind gap with arm and repeat;
    legacy: gap_crashes with the arm's label). Other records (steps, skipped arms) are dropped."""
    rows: list[dict] = []
    for r in records:
        if r.get("kind") == "gap":
            target = next((x for x in reversed(rows) if x.get("arm") == r.get("arm") and x.get("repeat") == r.get("repeat")), None)
            if target is not None:
                target.setdefault("crashes_gap", []).extend(r.get("crashes") or [])
            continue
        if "gap_crashes" in r:
            target = next((x for x in reversed(rows) if x.get("arm") == r.get("arm")), None)
            if target is not None:
                target["crash_phase"] = legacy_phases(target) + ["gap"] * len(r["gap_crashes"])
                target["crashes"] = list(target.get("crashes") or []) + list(r["gap_crashes"])
                target["crash_where"] = list(target.get("crash_where") or []) + list(
                    r.get("crash_where") or ["?"] * len(r["gap_crashes"]))
            continue
        if "copy" in r or ("arm" in r and r.get("run") is not None):
            rows.append(dict(r))
    return rows


def load_arm_log(path: Path, series: str) -> list[dict]:
    """A log's arm rows, pooled form, in log order; a runner log's (arms.jsonl) with their minutes booked
    (book_log)."""
    path = Path(path)
    records = []
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line:
            records.append(json.loads(line))
    if path.name == "arms.jsonl":
        records = book_log(records, path.parent)
    return [legacy_row(r, series) if "copy" in r else runner_row(r, series) for r in fold_gaps(records)]


# --- scenario minutes ------------------------------------------------------------------------------------------------
# G16: an arm that dies in its run books its crash, so its minutes must hold the scenario it died in, from that
# scenario's own start to the crash, the same way for every arm (else the arm that crashes more looks worse). The
# runner's scenario_min summed its run's records: a CRASHED scenario's record runs to the runner noticing (after the
# crash), and a run that died before printing its report line left the row unlinked, with none of its minutes
# (base #2 of 20261019-213631 booked 0.0: two finished scenarios and part of shower were in its run folder).

REPORT_TIME = re.compile(r"xcpt .*?(\d{2})-(\d{2})-(\d{2}) (\d{2})\.(\d{2})\.(\d{2})\.txt")
RUN_STAMP = "%Y%m%d-%H%M%S"


def crash_time(text: str) -> datetime | None:
    """A crash line's report time, from its name ("xcpt HOST 26-10-19 23.28.53.txt": local, yy-mm-dd), or None."""
    m = REPORT_TIME.search(text)
    if not m:
        return None
    yy, mo, dd, hh, mi, ss = (int(x) for x in m.groups())
    try:
        return datetime(2000 + yy, mo, dd, hh, mi, ss)
    except ValueError:
        return None


def run_records(run_dir: Path) -> list[dict]:
    """A scenario run's records (rescored.json, else records.json, which the run rewrites after each scenario), or []."""
    for name in ("rescored.json", "records.json"):
        path = Path(run_dir) / name
        if path.exists():
            try:
                return json.loads(path.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                return []
    return []


def find_run(runs_dir: Path, started: datetime, ended: datetime) -> str | None:
    """The scenario run an arm's run started: the first run folder (named by its start time) from the arm's run start
    to its end."""
    best: tuple[datetime, str] | None = None
    for path in Path(runs_dir).glob("????????-??????"):
        try:
            at = datetime.strptime(path.name, RUN_STAMP)
        except ValueError:
            continue
        if started - timedelta(seconds=2) <= at <= ended and (best is None or at < best[0]):
            best = (at, path.name)
    return best[1] if best else None


def scenario_spans(records: list[dict], run_start: datetime) -> list[dict]:
    """Each run record's start and end. The start is its motion watch folder's time (HHMMSS) less its reset (the watch
    starts when the reset ends), else the end of the one before (else the run's start); the end is the start plus its
    total. A record with no total (BLOCKED or RESET-FAILED: it did not run) takes no time."""
    spans, at = [], run_start
    for r in records:
        timings = r.get("timings") or {}
        total = timings.get("total")
        start = at
        folder = re.split(r"[\\/]", str(r.get("motion") or ""))[-1]
        if total is not None and re.fullmatch(r"\d{6}", folder):
            watch = run_start.replace(hour=int(folder[:2]), minute=int(folder[2:4]), second=int(folder[4:]), microsecond=0)
            if watch < run_start - timedelta(hours=1):
                watch += timedelta(days=1)  # a run over midnight
            start = watch - timedelta(seconds=float(timings.get("reset") or 0))
        end = start + timedelta(seconds=float(total)) if total is not None else start
        spans.append({"name": r.get("name"), "verdict": r.get("verdict"), "start": start, "end": end, "total": total})
        if total is not None:
            at = end
    return spans


def book_minutes(row: dict, runs_dir: Path | None, names: list[str] | None = None) -> dict:
    """An arm row with its scenario minutes booked the same way for every arm (a copy; other records as they are).
    Every arm row gets completed_min, what the runner booked (kept for comparison). A row that died in its run also
    gets finished_min (its run's recorded scenarios but BLOCKED, CRASHED and RESET-FAILED), died_min (the scenario it died in, from
    that scenario's own start to the crash: not the arm's wall time, which holds its setup, nor the run's start),
    scenario_min = finished_min + died_min, died_scenario (names: the run's scenarios in order, for one the run never
    recorded), died_from and crash_at (HH:MM:SS), and died_how (where the crash time came from: the crash report's
    name, else the run's end). A run folder the runner never linked (it died before printing its report line) is found
    by its start time: run, with run_linked "by time", and its verdicts and totals. With no run folder to read, the
    runner's minutes stay (died_how says so). A booked row is returned as it is."""
    if "arm" not in row or row.get("kind") or "completed_min" in row:
        return row
    out = dict(row, completed_min=row.get("scenario_min"))
    if row.get("died_in") != "run" or runs_dir is None:
        return out
    try:
        started = datetime.strptime(str(row.get("started")), "%Y-%m-%d %H:%M:%S")
    except ValueError:
        return out
    ended = started + timedelta(minutes=float(row.get("wall_min") or 0))
    run = row.get("run")
    if not run:
        run = find_run(Path(runs_dir), started, ended)
        if run:
            out["run"], out["run_linked"] = run, "by time"
    if not run or not (Path(runs_dir) / run).is_dir():
        out["died_how"] = "no run folder to read: the runner's minutes kept"
        return out
    try:
        run_start = datetime.strptime(run, RUN_STAMP)
    except ValueError:
        run_start = started
    records = run_records(Path(runs_dir) / run)
    if out.get("run_linked") and records:
        out["verdicts"] = {r.get("name"): r.get("verdict") for r in records}
        out["totals"] = {r.get("name"): (r.get("timings") or {}).get("total") for r in records}
    spans = scenario_spans(records, run_start)
    finished = sum(float(s["total"]) for s in spans if s["total"] is not None
                   and s["verdict"] not in ("BLOCKED", "CRASHED", "RESET-FAILED"))
    crashed = next((s for s in spans if s["verdict"] == "CRASHED"), None)
    if crashed:
        died_name, died_from = crashed["name"], crashed["start"]
    else:
        timed = [s for s in spans if s["total"] is not None]
        died_from = timed[-1]["end"] if timed else run_start
        recorded = {s["name"] for s in spans}
        died_name = next((n for n in names or [] if n not in recorded), "?")
        if died_name != "?":
            out["verdicts"] = {**(out.get("verdicts") or {}), died_name: "CRASHED"}
    times = sorted(t for t in (crash_time(text) for text in row.get("crashes") or []) if t is not None)
    crash = next((t for t in times if t >= died_from - timedelta(seconds=5)), times[0] if times else None)
    how = "crash report"
    if crash is None:
        crash, how = ended, "the run's end (no crash report)"
    died = max(0.0, (min(crash, ended) - died_from).total_seconds())
    out.update(finished_min=round(finished / 60, 2), died_min=round(died / 60, 2),
               scenario_min=round((finished + died) / 60, 2), died_scenario=died_name,
               died_from=died_from.strftime("%H:%M:%S"), crash_at=crash.strftime("%H:%M:%S"), died_how=how)
    return out


def book_log(records: list[dict], folder: Path) -> list[dict]:
    """An arms run's log (its folder: <run dir>/arms/<time>) with every arm row booked (book_minutes), reading the
    runs from <run dir>/scenarios and the scenario order from its plan."""
    folder = Path(folder)
    try:
        names = json.loads((folder / "plan.json").read_text(encoding="utf-8")).get("names") or []
    except (OSError, ValueError):
        names = []
    runs = folder.parent.parent / "scenarios"
    return [book_minutes(r, runs, names) for r in records]


def as_completed(records: list[dict]) -> list[dict]:
    """The log with each arm row's minutes as the runner booked them (completed_min), for comparison."""
    return [dict(r, scenario_min=r["completed_min"]) if "completed_min" in r else r for r in records]


def died_lines(records: list[dict]) -> list[str]:
    """What book_minutes changed, one line per arm row that died in its run."""
    out = []
    for row in arm_rows(records):
        if "died_min" not in row:
            if row.get("died_how"):
                out.append(f"{row['arm']}#{row.get('repeat', 1)}: {row['died_how']} ({row.get('scenario_min') or 0:.2f})")
            continue
        linked = (f"; run {row['run']} linked by time ({row.get('finished_min', 0):.2f} finished)"
                  if row.get("run_linked") else "")
        out.append(f"{row['arm']}#{row.get('repeat', 1)}: {row['scenario_min']:.2f} scenario-min = "
                   f"{row['finished_min']:.2f} finished + {row['died_min']:.2f} in {row['died_scenario']} "
                   f"({row['died_from']} to {row['crash_at']}, {row['died_how']}); booked {row.get('completed_min') or 0:.2f}"
                   + linked)
    return out


def parse_added(text: str) -> dict:
    """--add SERIES:POOL=MINUTES/SERVER[/CLIENT]: a pre-counted row (crash counts from a doc table, all phases)."""
    m = re.fullmatch(r"([^:=]+):([^=]+)=([\d.]+)/(\d+)(?:/(\d+))?", text.strip())
    if not m:
        raise SystemExit(f"--add {text!r}: want SERIES:POOL=MINUTES/SERVER_CRASHES[/CLIENT_CRASHES]")
    series, pool, minutes, server, client = m.groups()
    crashes = [("server", "all", "by hand")] * int(server) + [("client", "all", "by hand")] * int(client or 0)
    return {"label": f"{pool.strip()} ({series.strip()}, by hand)", "arm": "", "series": series.strip(),
            "minutes": float(minutes), "on": {}, "had": {s: set() for s in SIDES}, "crashes": crashes,
            "debugger": False, "fixed": pool.strip(), "run": None}


def is_on(value: object) -> bool:
    return str(value).strip() not in OFF_VALUES


def row_functions(row: dict) -> dict[str, Callable]:
    """on("name") / on("server:name"): the tunable was set to a non-default value during the arm (any game, or that
    one); val("name"): its largest numeric value (0 when off); had("name"): on in an earlier arm of the same TS3
    process (any game, or "server:name")."""
    def matches(name: str) -> list[str]:
        if ":" in name:
            return [v for k, v in row["on"].items() if k == name]
        return [v for k, v in row["on"].items() if k.split(":", 1)[1] == name]

    def on(name: str) -> bool:
        return any(is_on(v) for v in matches(name))

    def val(name: str) -> float:
        values = [number(v) for v in matches(name) if is_on(v)]
        return max((float(v) for v in values if isinstance(v, (int, float))), default=0.0)

    def had(name: str) -> bool:
        side, _, bare = name.rpartition(":")
        return any(bare in row["had"].get(s, set()) for s in ((side,) if side else SIDES))

    return {"on": on, "val": val, "had": had}


def row_lookup(row: dict) -> Callable[[str], object]:
    names = {"arm": row["arm"], "series": row["series"], "debugger": row["debugger"], "minutes": row["minutes"],
             "label": row["label"]}

    def lookup(name: str) -> object:
        if name not in names:
            raise MissingName(f"no name {name!r} for a pool (have: {', '.join(sorted(names))}, on(), val(), had())")
        return names[name]

    return lookup


def parse_pool(text: str) -> tuple[str, ast.AST]:
    label, sep, expr = text.partition("=")
    if not sep or not label.strip() or not expr.strip():
        raise SystemExit(f"--pool {text!r}: want LABEL=EXPRESSION, e.g. 'copy on=on(\"sacs.seed-copy\")'")
    try:
        return label.strip(), compile_expr(expr)
    except ExprError as ex:
        raise SystemExit(f"--pool {label.strip()}: {ex}") from None


def assign(rows: list[dict], pools: list[tuple[str, ast.AST]]) -> list[tuple[dict, str]]:
    """Each row with the first pool whose expression is true ("other" when none is); a hand-entered row keeps its own."""
    out = []
    for row in rows:
        if row["fixed"] is not None:
            out.append((row, row["fixed"]))
            continue
        chosen = "other"
        for label, node in pools:
            try:
                if evaluate(node, row_lookup(row), row_functions(row)):
                    chosen = label
                    break
            except ExprError as ex:
                raise SystemExit(f"pool {label} on {row['label']} ({row['series']}): {ex}") from None
        out.append((row, chosen))
    return out


def match_crashes(rows: list[dict], pattern: str | None, reports: dict, describe: Callable[[Path], str]) -> dict[str, int]:
    """Keep in each row only the crashes whose triage matches pattern (a case-insensitive regex), and give each crash a
    "seen" line for the listing. A crash's triage is the current one (describe) when its report is still on disk, in a
    profile or in crash_catch's archive (reports: {(side, name): (path, archived)}), else the triage the runner logged
    at the time; with neither (a legacy row whose report TS3 rotated out before the archive existed) it cannot match.
    pattern None keeps every crash (the listing only). Returns a tally: matched, other, archived (found only in the
    archive), gone (no report left), unknown (no triage at all)."""
    try:
        regex = re.compile(pattern, re.IGNORECASE) if pattern is not None else None
    except re.error as ex:
        raise SystemExit(f"--crash-match {pattern!r}: {ex}") from None
    tally = {"matched": 0, "other": 0, "archived": 0, "gone": 0, "unknown": 0}
    for row in rows:
        if row["fixed"] is not None:
            continue
        kept, seen = [], []
        for side, phase, name in row["crashes"]:
            found = reports.get((side, name))
            if found:
                text = describe(found[0])
                tally["archived"] += bool(found[1])
            else:
                text = (row.get("notes") or {}).get((side, name), "")
                tally["gone"] += 1
            tally["unknown"] += not text
            hit = regex is None or (bool(text) and bool(regex.search(text)))
            tally["matched" if hit else "other"] += 1
            if hit:
                kept.append((side, phase, name))
            source = "" if not found else " [archive]" if found[1] else ""
            seen.append(f"{side} {phase} {name}{source}: {text or 'no report left, no triage logged'}"
                        + ("" if hit else "  (not counted)"))
        row["crashes"], row["seen"] = kept, seen
    return tally


def counted(row: dict, side: str, run_only: bool) -> int:
    return sum(1 for s, phase, _ in row["crashes"]
               if (side == "both" or s == side) and (not run_only or phase in ("run", "all")))


def pool_summary(assigned: list[tuple[dict, str]], order: list[str], side: str, run_only: bool) -> dict[str, dict]:
    sums: dict[str, dict] = {}
    for label in order + sorted({p for _, p in assigned} - set(order)):
        mine = [r for r, p in assigned if p == label]
        if not mine and label == "other":
            continue
        sums[label] = {
            "rows": len([r for r in mine if r["fixed"] is None]), "minutes": sum(r["minutes"] for r in mine),
            "server": sum(counted(r, "server", run_only) for r in mine),
            "client": sum(counted(r, "client", run_only) for r in mine),
            "counted": sum(counted(r, side, run_only) for r in mine),
            "series": sorted({r["series"] for r in mine if r["minutes"] > 0}),
        }
    return sums


def pooled_table(sums: dict[str, dict], side: str) -> list[str]:
    width = max([len(label) for label in sums] + [8])
    lines = [f"{'pool':<{width}}  {'arms':>4} {'scen-min':>8} {'server':>6} {'client':>6} {'counted':>7} {'one per':>10}  series"]
    for label, s in sums.items():
        one = f"{s['minutes'] / s['counted']:.1f} min" if s["counted"] else "-"
        lines.append(f"{label:<{width}}  {s['rows']:>4} {s['minutes']:>8.2f} {s['server']:>6} {s['client']:>6} "
                     f"{s['counted']:>7} {one:>10}  {'+'.join(s['series']) or '-'}")
    lines.append(f"counted: {'server and client' if side == 'both' else side} crashes")
    return lines


def comparison_lines(sums: dict[str, dict], pairs: list[tuple[str, str]]) -> list[str]:
    """Each A:B comparison, with a warning when the two pools draw on different series: a difference between the
    series (another build, other binaries, another day) then shows up as one between the conditions."""
    lines = []
    for a, b in pairs:
        if a not in sums or b not in sums:
            lines.append(f"{a} against {b}: no such pool (have: {', '.join(sums)})")
            continue
        sa, sb = sums[a], sums[b]
        lines.append(describe_comparison(a, b, compare_rates(sa["counted"], sa["minutes"], sb["counted"], sb["minutes"])))
        if sa["series"] != sb["series"]:
            lines.append(f"  ! mixes series: {a} from {'+'.join(sa['series']) or '-'}, {b} from "
                         f"{'+'.join(sb['series']) or '-'}; a difference between series shows up as one between pools")
    return lines


def parse_pair(text: str) -> tuple[str, str]:
    a, sep, b = text.partition(":")
    if not sep or not a.strip() or not b.strip():
        raise SystemExit(f"--compare {text!r}: want POOL_A:POOL_B")
    return a.strip(), b.strip()


# --- game status -----------------------------------------------------------------------------------------------------
# scenarios.py arms --status reads SACS_RECORDER status from both games at an arm's boundaries (before its sets, after
# its run, after its after hook) and, with --status-every, on a timer during it. Each reading is kept flat: a ";" field
# "seedCopy=on,sent=12,recent=[...]" gives seedCopy=on and seedCopy.sent=12 (bracketed lists left out). Gates and
# pools read them as VIEW.SIDE.KEY: start, end, after, last (the newest reading, e.g. just before a crash), delta (end
# minus start: C's deltas) or delta_after (after minus start); SIDE server or client; KEY a flat key or a unique
# suffix of one (delta.server.spareResent is delta.server.seedCopy.spareResent).

STATUS_VIEWS = ("start", "end", "after", "last", "delta", "delta_after")


def parse_status(text: str) -> dict[str, str]:
    flat: dict[str, str] = {}
    for part in re.sub(r"\[[^\]]*\]", "", text or "").split(";"):
        items = [kv.split("=", 1) for kv in part.split(",") if "=" in kv]
        if not items:
            continue
        field = items[0][0].strip()
        flat[field] = items[0][1].strip()
        for key, value in items[1:]:
            flat[f"{field}.{key.strip()}"] = value.strip()
    return flat


def status_delta(start: dict | None, end: dict | None) -> dict[str, int | float] | None:
    """end minus start for every key numeric in both (zero included, so a gate on a counter that did not move reads
    0, not a missing name); None when either reading is missing."""
    if not start or not end:
        return None
    out: dict[str, int | float] = {}
    for key, value in end.items():
        a, b = number(start.get(key)), number(value)
        if isinstance(a, (int, float)) and isinstance(b, (int, float)) and not isinstance(a, bool):
            out[key] = b - a
    return out


def status_views(status: dict) -> dict[str, dict]:
    """A row's "status" record ({start, end, after, last: {side: {"t", "status"}}}) as views by side, deltas added; a
    side whose TS3 process restarted during the arm (status["restarted"]) has no delta."""
    views: dict[str, dict] = {}
    for view in ("start", "end", "after"):
        views[view] = {side: (status.get(view) or {}).get(side) for side in SIDES}
    views["last"] = {side: ((status.get("last") or {}).get(side) or {}).get("status") for side in SIDES}
    restarted = set(status.get("restarted") or [])
    for view, later in (("delta", "end"), ("delta_after", "after")):
        views[view] = {side: None if side in restarted else status_delta(views["start"][side], views[later][side])
                       for side in SIDES}
    return views


def status_value(views: dict, name: str) -> object:
    """VIEW.SIDE.KEY from status_views; MissingName when that reading is missing or has no such key (a gate fails
    closed on it), ExprError when the key names more than one."""
    view, _, rest = name.partition(".")
    side, _, key = rest.partition(".")
    if view not in STATUS_VIEWS or side not in SIDES or not key:
        raise MissingName(f"no name {name!r} (status names are VIEW.SIDE.KEY: {', '.join(STATUS_VIEWS)}; server or client)")
    values = views.get(view, {}).get(side)
    if values is None:
        raise MissingName(f"{name}: no {view} reading from the {side}")
    if key in values:
        return number(values[key])
    hits = [k for k in values if k.endswith("." + key)]
    if len(hits) > 1:
        raise ExprError(f"{name}: {key} names {', '.join(sorted(hits))}: give the field too ({view}.{side}.{hits[0]})")
    if not hits:
        raise MissingName(f"{name}: the {side}'s {view} reading has no {key}")
    return number(values[hits[0]])


GC_GATE_KEYS = ("installed", "bit", "calls", "admitted", "skipped", "heldAtEntry", "intervalMs", "pauseMeanMs",
                "pauseMaxMs")


def gc_gate_fields(status: dict) -> dict[str, dict]:
    """Session C's GC gate counters (SimLink v7's SACS_RECORDER part gcGate=on|off,installed=N,bit=0|1,calls=N,
    admitted=N,skipped=N,heldAtEntry=N,intervalMs=N,pauseMeanMs=F,pauseMaxMs=F) from an arm row's status record, per
    side that has them: the end reading's, or the newest reading's when the end is missing (a game that died), "from"
    naming which. They count from the process start (admitted, skipped and the pauses only while the bit is on,
    heldAtEntry only after the first admitted call, calls every entry), so they are the arm's own only when its TS3
    started with it (a reboot or boot hook per arm); otherwise delta.SIDE.gcGate.KEY in a gate is the arm's share."""
    out: dict[str, dict] = {}
    for side in SIDES:
        values, source = (status.get("end") or {}).get(side), "end"
        if not values or "gcGate" not in values:
            values, source = ((status.get("last") or {}).get(side) or {}).get("status"), "last"
        if not values or "gcGate" not in values:
            continue
        fields: dict[str, object] = {"from": source, "gcGate": values["gcGate"]}
        fields.update({key: number(values[f"gcGate.{key}"]) for key in GC_GATE_KEYS if f"gcGate.{key}" in values})
        out[side] = fields
    return out


def gc_gate_text(fields: dict[str, dict]) -> str:
    """gc_gate_fields in one line: "server on: installed=1 bit=1 calls=905 ... pauseMaxMs=31.5" per side."""
    parts = []
    for side, values in fields.items():
        counters = " ".join(f"{key}={values[key]}" for key in GC_GATE_KEYS if key in values)
        parts.append(f"{side} {values.get('gcGate')}: {counters}"
                     + (f" (the {values['from']} reading)" if values.get("from") != "end" else ""))
    return "; ".join(parts)


# --- gates -----------------------------------------------------------------------------------------------------------
# scenarios.py arms --gate "[once] ARMS: EXPR" stops the run when EXPR is false after an arm named in ARMS (comma
# separated, * for every arm) that finished alive. EXPR reads the status names above, hook.NAME (a NAME=N or NAME:N
# in the after hook's full output, e.g. the pull's "coverage: ...,borrowedSpare:2"), arm and repeat, and calls on(),
# val() and had() as a pool does. A name that is missing stops the run too (a gate fails closed); an arm that died is
# not evaluated, and the gate waits for the next matching arm. once: after its first evaluation it is done.

GATE = re.compile(r"^\s*(?:(once)\s+)?([\w.*-]+(?:\s*,\s*[\w.*-]+)*)\s*:\s*(\S.*)$", re.S)
HOOK_VALUE = re.compile(r"(?<![\w.])([A-Za-z_]\w*)[=:](-?\d+(?:\.\d+)?)(?!\w)(?!\.\d)")
GATE_NAMES = ("arm", "repeat")


def expr_names(node: ast.AST) -> list[str]:
    """The dotted names an expression reads (not the functions it calls), in order, once each."""
    found: list[str] = []

    def walk(n: ast.AST) -> None:
        if isinstance(n, (ast.Name, ast.Attribute)):
            name = dotted(n)
            if name and name not in _CONSTANTS and name not in found:
                found.append(name)
            return
        children = n.args if isinstance(n, ast.Call) else list(ast.iter_child_nodes(n))
        for child in children:
            walk(child)

    walk(node)
    return found


def parse_gate(text: str) -> dict:
    m = GATE.match(text)
    if not m:
        raise SystemExit(f"--gate {text!r}: want '[once] ARMS: EXPR' (ARMS: arm names, comma separated, or *)")
    try:
        node = compile_expr(m.group(3))
    except ExprError as ex:
        raise SystemExit(f"--gate {text!r}: {ex}") from None
    names = expr_names(node)
    for name in names:
        view, _, rest = name.partition(".")
        side = rest.partition(".")[0]
        if not (name in GATE_NAMES or (view == "hook" and rest) or (view in STATUS_VIEWS and side in SIDES and "." in rest)):
            raise SystemExit(f"--gate {text!r}: {name!r} is not a gate name (VIEW.SIDE.KEY with VIEW one of "
                             f"{', '.join(STATUS_VIEWS)}; hook.NAME; arm; repeat)")
    return {"text": text.strip(), "once": bool(m.group(1)), "arms": [a.strip() for a in m.group(2).split(",")],
            "node": node, "names": names, "done": False, "results": []}


def gate_applies(gate: dict, arm: str) -> bool:
    return not gate["done"] and ("*" in gate["arms"] or arm in gate["arms"])


def hook_values(text: str) -> dict[str, object]:
    """NAME -> number for each NAME=N or NAME:N in a hook's output; a name given different values maps to the list of
    them (a gate reading it stops with that list, not one of them)."""
    seen: dict[str, list] = {}
    for name, value in HOOK_VALUE.findall(text or ""):
        values = seen.setdefault(name, [])
        if number(value) not in values:
            values.append(number(value))
    return {name: values[0] if len(values) == 1 else values for name, values in seen.items()}


def gate_lookup(views: dict, hook: dict, extra: dict) -> Callable[[str], object]:
    def lookup(name: str) -> object:
        if name in extra:
            return extra[name]
        if name.startswith("hook."):
            key = name[5:]
            if key not in hook:
                raise MissingName(f"{name}: the after hook's output has no {key}=N or {key}:N")
            if isinstance(hook[key], list):
                raise ExprError(f"{name}: the after hook's output gives {key} {len(hook[key])} values "
                                f"({', '.join(str(v) for v in hook[key])})")
            return hook[key]
        return status_value(views, name)

    return lookup


def check_gate(gate: dict, row: dict, hook_text: str) -> dict:
    """Evaluate one gate on a finished arm row (scenarios.py arms): {"result": passed | failed | error, "values",
    "why"}. Values shows each name the expression reads, "missing" for one it could not."""
    views = status_views(row.get("status") or {})
    lookup = gate_lookup(views, hook_values(hook_text), {"arm": row["arm"], "repeat": row.get("repeat", 1)})
    values: dict[str, object] = {}
    for name in gate["names"]:
        try:
            values[name] = lookup(name)
        except ExprError as ex:
            values[name] = f"missing ({ex})" if isinstance(ex, MissingName) else f"error ({ex})"
    try:
        ok = evaluate(gate["node"], lookup, row_functions(runner_row(row, "")))
    except ExprError as ex:
        return {"result": "error", "values": values, "why": str(ex)}
    return {"result": "passed" if ok else "failed", "values": values, "why": ""}


# --- stop conditions -------------------------------------------------------------------------------------------------
# scenarios.py arms --stop-when EXPR (repeatable; kept as the plan's "stop") stops an arms run at an arm boundary, once
# an arm's row is logged and never during an arm, when EXPR holds over the run's log so far: exit 9, as --stop does,
# with the reason in state.json's stopped_by and summary.txt. EXPR reads, per arm name of the run: ARM.min (its
# scenario minutes), ARM.runs (its rows that ran with exposure), ARM.crashes (its crashes on both sides), by side
# ARM.server.crashes or ARM.client.crashes, and by phase ARM.crashes.PHASE or ARM.server.crashes.PHASE (run by
# default; boot, setup, after, gap, or any for every phase); and p(A, B[, SIDE[, PHASE]]): the one-sided conditional
# binomial P that A's crash rate per scenario minute is above B's (A's count given the total, Binomial with A's share
# of the two arms' minutes; 1 while neither has a crash). A stop that cannot be evaluated stops the run too (it fails
# closed, as a gate does). Gap crashes land on an arm only at the next arm's start, as in arms --crashes.

STOP_PHASES = ("run", "boot", "setup", "after", "gap", "any")
STOP_SIDES = ("both",) + SIDES


def stop_term(name: str, arms: set[str]) -> tuple:
    """A stop name as ("word", text) for a bare arm name, side or phase (p()'s arguments), ("min" | "runs", arm) or
    ("crashes", arm, side, phase); ExprError for anything else, or for an arm the run does not have."""
    parts = name.split(".")
    if len(parts) == 1 and (name in arms or name in STOP_SIDES or name in STOP_PHASES):
        return ("word", name)
    arm, rest = parts[0], parts[1:]
    if arm not in arms:
        raise ExprError(f"{name}: no arm {arm!r} in this run (its arms: {', '.join(sorted(arms))})")
    if rest in (["min"], ["runs"]):
        return (rest[0], arm)
    side = "both"
    if rest and rest[0] in SIDES:
        side, rest = rest[0], rest[1:]
    if rest[:1] == ["crashes"] and (len(rest) == 1 or (len(rest) == 2 and rest[1] in STOP_PHASES)):
        return ("crashes", arm, side, rest[1] if len(rest) == 2 else "run")
    raise ExprError(f"{name}: not a stop name (ARM.min, ARM.runs, ARM[.server|.client].crashes[.PHASE] with PHASE one "
                    f"of {', '.join(STOP_PHASES)}; p(A, B[, SIDE[, PHASE]]))")


def parse_stop(text: str, arms: set[str]) -> dict:
    """One --stop-when expression, checked against the run's arm names before any game is touched."""
    want = (arms, arms, set(STOP_SIDES), set(STOP_PHASES))

    def walk(n: ast.AST, in_call: bool) -> None:
        if isinstance(n, (ast.Name, ast.Attribute)):
            name = dotted(n) or ""
            if name in _CONSTANTS:
                return
            if (stop_term(name, arms)[0] == "word") != in_call:
                raise ExprError(f"{name}: " + ("p() takes arm names, a side and a phase" if in_call
                                               else "a bare arm, side or phase name is only an argument to p()"))
            return
        if isinstance(n, ast.Call):
            if n.func.id != "p" or not 2 <= len(n.args) <= 4:  # type: ignore[attr-defined]
                raise ExprError(f"{ast.unparse(n)}: the one call is p(A, B[, SIDE[, PHASE]])")
            for arg, allowed in zip(n.args, want):
                value = arg.value if isinstance(arg, ast.Constant) else dotted(arg)
                if value not in allowed:
                    raise ExprError(f"{ast.unparse(n)}: {value!r} is not one of {', '.join(sorted(allowed))}")
            return
        for child in ast.iter_child_nodes(n):
            walk(child, in_call)

    try:
        node = compile_expr(text)
        walk(node, False)
    except ExprError as ex:
        raise SystemExit(f"--stop-when {text!r}: {ex}") from None
    return {"text": text.strip(), "node": node, "arms": set(arms),
            "names": [name for name in expr_names(node) if name not in _CONSTANTS and stop_term(name, arms)[0] != "word"],
            "calls": [n for n in ast.walk(node) if isinstance(n, ast.Call)]}


def stop_counts(records: list[dict]) -> dict[str, dict]:
    """Per arm of an arms log (arm_rows: a gap record's crashes on the arm before it): scenario minutes, the runs that
    had exposure, and crashes by (side, phase)."""
    out: dict[str, dict] = {}
    for row in arm_rows(records):
        mine = out.setdefault(row.get("arm"), {"min": 0.0, "runs": 0, "crashes": {}})
        mine["min"] += row.get("scenario_min") or 0
        mine["runs"] += bool(row.get("run") and (row.get("scenario_min") or row.get("soak_min")))
        for phase, key in CRASH_KEYS:
            for text in row.get(key) or []:
                where = (crash_parts(text)[0], phase)
                mine["crashes"][where] = mine["crashes"].get(where, 0) + 1
    return out


def stop_crashes(counts: dict[str, dict], arm: str, side: str = "both", phase: str = "run") -> int:
    return sum(n for (s, ph), n in (counts.get(arm) or {}).get("crashes", {}).items()
               if side in ("both", s) and phase in ("any", ph))


def stop_p(counts: dict[str, dict], a: str, b: str, side: str = "both", phase: str = "run") -> float:
    """p(A, B): P(X >= A's crashes) for X ~ Binomial(A's + B's crashes, A's share of their scenario minutes); 1 while
    neither has a crash or either has no minutes."""
    ka, kb = stop_crashes(counts, a, side, phase), stop_crashes(counts, b, side, phase)
    ma, mb = ((counts.get(x) or {}).get("min", 0.0) for x in (a, b))
    if ka + kb == 0 or ma <= 0 or mb <= 0:
        return 1.0
    return binomial_tail(ka, ka + kb, ma / (ma + mb), upper=True)


def stop_check(stops: list[dict], records: list[dict]) -> dict:
    """Every stop expression over an arms log so far: {"fired": the first that holds, or None; "values": each name and
    p() call they read; "error": why one could not be evaluated (the run stops on that too), or None}."""
    counts = stop_counts(records)
    arms = set().union(*(stop["arms"] for stop in stops)) if stops else set()

    def lookup(name: str) -> object:
        term = stop_term(name, arms)
        if term[0] == "word":
            return term[1]
        if term[0] == "min":
            return round((counts.get(term[1]) or {}).get("min", 0.0), 2)
        if term[0] == "runs":
            return (counts.get(term[1]) or {}).get("runs", 0)
        return stop_crashes(counts, *term[1:])

    functions = {"p": lambda a, b, side="both", phase="run": round(stop_p(counts, a, b, side, phase), 4)}
    values: dict[str, object] = {}
    fired = error = None
    for stop in stops:
        try:
            for name in stop["names"]:
                values[name] = lookup(name)
            for call in stop["calls"]:
                values[ast.unparse(call)] = evaluate(call, lookup, functions)
            if evaluate(stop["node"], lookup, functions) and fired is None:
                fired = stop["text"]
        except ExprError as ex:
            error = error or f"{stop['text']}: {ex}"
    return {"fired": fired, "values": values, "error": error}


def stop_text(check: dict) -> str:
    """A stop check in one line: what it says, then each value it read."""
    values = " ".join(f"{k}={v}" for k, v in check["values"].items())
    head = (f"FIRED: {check['fired']}" if check["fired"] else f"ERROR: {check['error']}" if check["error"]
            else "goes on")
    return head + (f" ({values})" if values else "")


def stop_replay(stops: list[dict], records: list[dict]) -> tuple[int | None, dict, int]:
    """The stops over a logged arms run as the runner checks them, after each arm row that ran, over the log up to it:
    (the first row number that fires or errs, 1-based over the arm rows as arms --wait numbers them, or None; that
    check, or the one at the end; the arm rows)."""
    check = stop_check(stops, [])
    number = 0
    for i, record in enumerate(records):
        if record.get("kind") or "arm" not in record:
            continue
        number += 1
        if record.get("skipped"):
            continue  # the runner checks only an arm that ran
        check = stop_check(stops, records[:i + 1])
        if check["fired"] or check["error"]:
            return number, check, sum(1 for r in records if not r.get("kind") and "arm" in r)
    return None, check, number


EXCLUDE = re.compile(r"(?:([^/\s]+)/)?([\w.:-]+)#([1-9]\d*)")


def parse_excludes(texts: list[str]) -> list[tuple[str | None, str, int]]:
    """arms --exclude items, [FOLDER/]ARM#N, comma separated or repeated, as (folder or None, arm, repeat)."""
    out = []
    for item in (part.strip() for text in texts for part in text.split(",")):
        if not item:
            continue
        m = EXCLUDE.fullmatch(item)
        if not m:
            raise SystemExit(f"--exclude {item!r}: want ARM#N (e.g. gate#1), or FOLDER/ARM#N with several folders")
        out.append((m.group(1), m.group(2), int(m.group(3))))
    return out


def invalid_rows(records: list[dict]) -> list[tuple[str, int]]:
    """The arm rows a gate failed or erred on (row["gates"]), in log order: what --exclude-invalid leaves out. An arm
    that died is not one: its gates were not evaluated."""
    return [(r["arm"], r.get("repeat", 1)) for r in records if r.get("kind") is None and "arm" in r
            and any(g.get("result") in ("failed", "error") for g in r.get("gates") or [])]


def exclude_records(records: list[dict], keys: set[tuple[str, int]]) -> tuple[list[dict], list[dict]]:
    """An arms log without the arm rows keys names ((arm, repeat)) and their gap records (the crashes after an arm,
    up to the next one's setup, are charged to it): (kept, dropped), both in log order."""
    kept, dropped = [], []
    for r in records:
        named = "arm" in r and r.get("kind") in (None, "gap") and (r["arm"], r.get("repeat", 1)) in keys
        (dropped if named else kept).append(r)
    return kept, dropped


def excluded_lines(dropped: list[dict]) -> list[str]:
    """What an exclusion left out, one line per arm row: its scenario minutes, its crashes in every phase (its gap's
    among them) and the gates that failed or erred on it."""
    out = []
    for row in arm_rows(dropped):
        crashes = sum(len(row.get(key) or []) for _, key in CRASH_KEYS)
        gap = len(row.get("crashes_gap") or [])
        bad = [g for g in row.get("gates") or [] if g.get("result") in ("failed", "error")]
        out.append(f"{row['arm']}#{row.get('repeat', 1)}: {row.get('scenario_min') or 0:g} scenario-min, "
                   f"{crashes} crash(es)" + (f" ({gap} in its gap)" if gap else "")
                   + (f"; gate {', '.join(g['result'] for g in bad)}" if bad else "")
                   + ("; not run" if row.get("run") is None else ""))
    return out


def count_text(counts: dict | None) -> str:
    """An arm's stop counts (stop_counts) in a phrase: minutes, runs, run crashes and their rate per scenario hour."""
    counts = counts or {"min": 0.0, "runs": 0, "crashes": {}}
    run = sum(n for (_, phase), n in counts["crashes"].items() if phase == "run")
    rate = f"{run / counts['min'] * 60:.2f}" if counts["min"] else "-"
    return f"{counts['min']:.2f} scenario-min, {counts['runs']} run(s), {run} run crash(es), {rate} per hour"
