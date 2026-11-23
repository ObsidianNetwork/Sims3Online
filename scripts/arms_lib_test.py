"""Unit tests for arms_lib (expressions, statistics, arm logs): python -m unittest scripts.arms_lib_test"""
from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import arms_lib as al  # noqa: E402

# Boot 5 of (2026-10-18), from Session C's arms6.jsonl: arm, seed-copy, keyed-picks, node-order, gc-pressure,
# whether the process had read seeds before the arm, scenario minutes, run, crashes (game, phase, time).
BOOT5 = [
    ("base-1", 0, 0, 0, 0, False, 4.57, "122158", []),
    ("aoff-1", 0, 0, 1, 0, False, 4.33, "122752", [("server", "pull", "12.42.32")]),
    ("aonly-1", 0, 0, 2, 0, False, 4.42, "124517", [("server", "pull", "12.51.04")]),
    ("aonly-2", 0, 0, 2, 0, False, 3.64, "125344", []),
    ("aoff-2", 0, 0, 1, 0, False, 2.58, "125834", [("server", "run", "13.01.32")]),
    ("t3log-1", 1, 1, 1, 0, False, 4.26, "130538", [("server", "run", "13.09.53")]),
    ("t3a-1", 1, 1, 2, 0, False, 4.36, "131254", []),
    ("base-2", 0, 0, 0, 0, True, 0.81, "131901", [("server", "run", "13.19.43")]),
    ("t3a-2", 1, 1, 2, 0, False, 1.99, "132250", [("server", "run", "13.25.05")]),
    ("base-3", 0, 0, 0, 0, False, 3.55, "132809", []),
    ("t3log-2", 1, 1, 1, 0, False, 2.5, "133241", [("server", "run", "13.35.32")]),
    ("gc-1", 0, 0, 0, 2, False, 3.62, "133907", [("server", "run", "13.42.36")]),
    ("gc-2", 0, 0, 0, 2, False, 2.05, "134541", [("client", "run", "13.47.25")]),
    ("base-4", 0, 0, 0, 0, False, 3.59, "135040", []),
    ("aonly-3", 0, 0, 2, 0, False, 4.11, "135508", []),
    ("t3a-3", 1, 1, 2, 0, False, 1.39, "140007", [("server", "run", "14.01.17")]),
]
# The doc's pooled table (internal documentation, boot 5): boots 2-4 by hand, server crashes only.
EARLIER = ["boots2-4:copy on=52.8/6", "boots2-4:base=40.2/2", "boots2-4:read loop=9.5/1"]
POOLS = ['copy on=on("sacs.seed-copy")', 'read loop=on("sacs.seed-read-loop")', "A' pick=on(\"sacs.node-order\")",
         'GC=on("sacs.gc-pressure")', "base=true"]


def write_boot5(folder: Path) -> Path:
    path = folder / "arms6.jsonl"
    lines = [json.dumps({"step": "smoke", "ok": True})]
    for arm, copy, keyed, node, gc, had, minutes, run, crashes in BOOT5:
        lines.append(json.dumps({
            "arm": arm, "copy": copy, "keyed": keyed, "node_order": node, "gc": gc, "process_had_seeds": had,
            "scenario_min": minutes, "run": "20261018-" + run,
            "crashes": [f"xcpt HOST 26-10-18 {t}.txt" for _, _, t in crashes],
            "crash_where": [w for w, _, _ in crashes], "crash_phase": [p for _, p, _ in crashes]}))
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return path


def pooled(rows: list[dict], pools: list[str], side: str = "server", run_only: bool = False) -> dict[str, dict]:
    compiled = [al.parse_pool(p) for p in pools]
    return al.pool_summary(al.assign(rows, compiled), [label for label, _ in compiled], side, run_only)


class Expressions(unittest.TestCase):
    def test_names_numbers_and_functions(self) -> None:
        names = {"delta.server.spareResent": 25, "end.client.seedCopy": "on"}

        def lookup(name: str) -> object:
            if name not in names:
                raise al.MissingName(name)
            return names[name]

        node = al.compile_expr('delta.server.spareResent > 0 and end.client.seedCopy == "on" and twice(2) == 4')
        self.assertTrue(al.evaluate(node, lookup, {"twice": lambda x: 2 * x}))
        self.assertFalse(al.evaluate(al.compile_expr("not (1 + 2 * 3 == 7)"), lookup))
        self.assertTrue(al.evaluate(al.compile_expr("true and 2 in (1, 2)"), lookup))
        with self.assertRaises(al.MissingName):
            al.evaluate(al.compile_expr("delta.server.nothing > 0"), lookup)

    def test_unsafe_or_broken_expressions_are_refused(self) -> None:
        for text in ("__import__('os')", "x[0]", "(lambda: 1)()", "a.b(1)", "1 +", "f(x=1)"):
            with self.assertRaises(al.ExprError, msg=text):
                al.compile_expr(text)
        with self.assertRaises(al.ExprError):
            al.evaluate(al.compile_expr('"on" > 1'), lambda name: 0)
        with self.assertRaises(al.ExprError):
            al.evaluate(al.compile_expr("1 / 0"), lambda name: 0)
        with self.assertRaises(al.ExprError):
            al.evaluate(al.compile_expr("nope(1)"), lambda name: 0)

    def test_number(self) -> None:
        self.assertEqual([al.number(v) for v in ("39", "-2", "0.5", "on", "1/0")], [39, -2, 0.5, "on", "1/0"])


class Statistics(unittest.TestCase):
    def test_binomial_tail(self) -> None:
        self.assertAlmostEqual(al.binomial_tail(0, 4, 0.3), 1.0)
        self.assertAlmostEqual(al.binomial_tail(4, 4, 0.5), 0.0625)
        self.assertAlmostEqual(al.binomial_tail(1, 4, 0.5, upper=False), 0.3125)

    def test_clopper_pearson(self) -> None:
        low, high = al.clopper_pearson(5, 10)
        self.assertAlmostEqual(low, 0.1871, places=3)  # the textbook interval for 5 of 10
        self.assertAlmostEqual(high, 0.8129, places=3)
        self.assertEqual(al.clopper_pearson(0, 5)[0], 0.0)
        self.assertEqual(al.clopper_pearson(5, 5)[1], 1.0)

    def test_compare_rates(self) -> None:
        c = al.compare_rates(6, 52.8, 2, 40.2)  # the crash chase: copy on against copy off
        self.assertAlmostEqual(c["share"], 0.568, places=3)
        self.assertAlmostEqual(c["p"], 0.25, places=2)  # the doc: "6 or more with probability 0.25"
        self.assertTrue(c["higher"])
        self.assertAlmostEqual(c["ratio"], (6 / 52.8) / (2 / 40.2))
        self.assertLess(c["ci"][0], 1.0)  # cannot rule out equal rates
        self.assertGreater(c["ci"][1], 2.0)  # nor a rise of about 2x
        self.assertIsNone(al.compare_rates(0, 5.0, 0, 5.0)["p"])
        self.assertEqual(al.compare_rates(3, 5.0, 0, 5.0)["ratio"], float("inf"))
        self.assertIn("no crashes", al.describe_comparison("a", "b", al.compare_rates(0, 5.0, 0, 5.0)))


class Boot5PooledTable(unittest.TestCase):
    """The tool must give the doc's boot 5 numbers from the log (internal documentation, Boot 5)."""

    def setUp(self) -> None:
        self.folder = tempfile.TemporaryDirectory()
        self.rows = al.load_arm_log(write_boot5(Path(self.folder.name)), "arms6")

    def tearDown(self) -> None:
        self.folder.cleanup()

    def test_boot5_pools(self) -> None:
        self.assertEqual(len(self.rows), 16)  # the smoke step is not an arm
        sums = pooled(self.rows, POOLS)
        expect = {"copy on": (5, 14.50, 4, 0), "A' pick": (5, 19.08, 3, 0), "GC": (2, 5.67, 1, 1), "base": (4, 12.52, 1, 0)}
        for label, (arms, minutes, server, client) in expect.items():
            s = sums[label]
            self.assertEqual((s["rows"], round(s["minutes"], 2), s["server"], s["client"]), (arms, minutes, server, client), label)
        self.assertEqual(sums["read loop"]["minutes"], 0)

    def test_never_read_a_seed(self) -> None:
        sums = pooled(self.rows, ['never=not on("sacs.seed-copy") and not had("sacs.seed-copy")', "rest=true"])
        self.assertEqual((round(sums["never"]["minutes"], 2), sums["never"]["server"], sums["never"]["client"]), (36.46, 4, 1))

    def test_run_phase_only(self) -> None:
        sums = pooled(self.rows, POOLS, run_only=True)
        self.assertEqual(sums["A' pick"]["server"], 1)  # its other two came in pulls
        self.assertEqual((sums["copy on"]["server"], sums["base"]["server"], sums["GC"]["server"]), (4, 1, 1))

    def test_pooled_with_boots_2_to_4(self) -> None:
        rows = self.rows + [al.parse_added(t) for t in EARLIER]
        sums = pooled(rows, POOLS)
        self.assertEqual((round(sums["copy on"]["minutes"], 1), sums["copy on"]["server"]), (67.3, 10))
        self.assertEqual((round(sums["base"]["minutes"], 1), sums["base"]["server"]), (52.7, 3))
        self.assertEqual((round(sums["read loop"]["minutes"], 1), sums["read loop"]["server"]), (9.5, 1))
        lines = al.comparison_lines(sums, [("copy on", "base"), ("GC", "base")])
        self.assertIn("10 of 13 crashes against an exposure share of 0.56", lines[0])
        self.assertIn("= 0.106", lines[0])  # the doc: P = 0.11
        self.assertFalse(lines[1].startswith("  !"))  # both pools draw on boot 5 and boots 2-4: no warning
        self.assertIn("= 0.33", lines[1])  # GC: 1 of 4 against 0.10 (the doc rounds it to 0.34)
        self.assertTrue(lines[2].startswith("  ! mixes series: GC from arms6, base from arms6+boots2-4"))

    def test_the_mixed_comparison_is_flagged(self) -> None:
        # The doc's P = 0.047 is A' with or without the copy (33.58 min, 7 crashes) against pooled base: not a finding.
        rows = self.rows + [al.parse_added("boots2-4:base=40.2/2")]
        sums = pooled(rows, ["A' any=on(\"sacs.node-order\")", 'base=not on("sacs.seed-copy") and not on("sacs.gc-pressure")'])
        lines = al.comparison_lines(sums, [("A' any", "base")])
        self.assertIn("7 of 10", lines[0])
        self.assertIn("= 0.047", lines[0])
        self.assertIn("! mixes series", lines[1])

    def test_within_boot5_and_boot5_against_earlier(self) -> None:
        sums = pooled(self.rows, ["A' any=on(\"sacs.node-order\")", 'base=not on("sacs.seed-copy") and not on("sacs.gc-pressure")'])
        line = al.comparison_lines(sums, [("A' any", "base")])[0]
        self.assertIn("7 of 8 crashes against an exposure share of 0.73", line)
        self.assertIn("= 0.31", line)  # the doc rounds it to 0.32
        rows = self.rows + [al.parse_added("boots2-4:earlier=102.5/9")]
        sums = pooled(rows, ['boot5=series == "arms6"', "earlier=true"])
        lines = al.comparison_lines(sums, [("boot5", "earlier")])
        self.assertIn("9 of 18", lines[0])  # boot 5's server rate against boots 2-4's: 9 in 51.8 against 9 in 102.5
        self.assertIn("= 0.11", lines[0])
        self.assertIn("! mixes series", lines[1])

    def test_a_row_takes_the_first_pool_that_matches(self) -> None:
        assigned = al.assign(self.rows, [al.parse_pool(p) for p in ['first=arm == "base"', 'second=arm == "base"']])
        self.assertEqual({p for r, p in assigned if r["arm"] == "base"}, {"first"})
        self.assertIn("other", {p for _, p in assigned})
        with self.assertRaises(SystemExit):
            al.assign(self.rows, [al.parse_pool("bad=nothing > 1")])
        with self.assertRaises(SystemExit):
            al.parse_pool("no expression")


def status_text(spare_resent: int = 0, spare_only: int = 0, borrowed: int = 0, gc_count: int = 39) -> str:
    """A SACS_RECORDER status line as the mod writes it (SacsPicks, SacsSeedCopy.Describe, SacsOrder)."""
    return ("picks=on,ready=1,patch=active/ok,switches=2,count=548,queries=10;"
            f"seedCopy=on,ready=1,sent=12,generated=12,deferred=0,written=12,skipped=0,failed=0,borrowedSpares={borrowed},"
            f"readLoop=0,reads=7,spareResent={spare_resent},reseeded=0,spareOnly={spare_only},gcPressure=0,collections=0,collectMs=0,"
            f"gcCount={gc_count},recent=[spare 7a,seed 9b];order=off,count=3,picks=0")


# Session C's GC gate part (SimLink v7, 2026-10-19 message): a gate arm, and a base arm with the switch installed.
GC_GATE_ON = ("gcGate=on,installed=1,bit=1,calls=905,admitted=120,skipped=785,heldAtEntry=0,intervalMs=6000,"
              "pauseMeanMs=1.25,pauseMaxMs=31.5")
GC_GATE_BASE = ("gcGate=off,installed=1,bit=0,calls=870,admitted=0,skipped=0,heldAtEntry=0,intervalMs=6000,"
                "pauseMeanMs=0,pauseMaxMs=0")
GC_BASE_GATE = ("base: end.server.gcGate.installed == 1 and end.server.gcGate.bit == 0 and end.server.gcGate.calls > 0 "
                "and end.server.gcGate.admitted == 0")
GC_ON_GATE = ("gate: end.server.gcGate.bit == 1 and end.server.gcGate.admitted > 0 and end.server.gcGate.skipped > 0 "
              "and end.server.gcGate.heldAtEntry == 0")


class Status(unittest.TestCase):
    def test_the_gc_gate_part(self) -> None:
        flat = al.parse_status(status_text() + ";" + GC_GATE_ON)
        self.assertEqual((flat["gcGate"], flat["gcGate.bit"], flat["gcGate.pauseMaxMs"]), ("on", "1", "31.5"))
        self.assertEqual(flat["seedCopy.spareResent"], "0")  # the parts before it are as they were
        self.assertEqual(flat["seedCopy.reseeded"], "0")  # delivery gap 1D, after spareResent
        record = {"start": {"server": None, "client": None},
                  "end": {"server": flat, "client": None},
                  "last": {"client": {"t": 1.0, "status": al.parse_status(GC_GATE_BASE)}}}
        fields = al.gc_gate_fields(record)
        self.assertEqual(fields["server"], {"from": "end", "gcGate": "on", "installed": 1, "bit": 1, "calls": 905,
                                            "admitted": 120, "skipped": 785, "heldAtEntry": 0, "intervalMs": 6000,
                                            "pauseMeanMs": 1.25, "pauseMaxMs": 31.5})
        self.assertEqual((fields["client"]["from"], fields["client"]["bit"]), ("last", 0))  # the end reading is missing
        self.assertEqual(al.gc_gate_text({"server": fields["server"]}),
                         "server on: installed=1 bit=1 calls=905 admitted=120 skipped=785 heldAtEntry=0 intervalMs=6000 "
                         "pauseMeanMs=1.25 pauseMaxMs=31.5")
        self.assertTrue(al.gc_gate_text({"client": fields["client"]}).endswith("pauseMaxMs=0 (the last reading)"))
        self.assertEqual(al.gc_gate_fields({"end": {"server": al.parse_status(status_text())}}), {})  # no gcGate: none

    def test_the_gc_gate_arms_gates(self) -> None:
        def row(part: str) -> dict:
            r = gate_row(0, 0, 0)
            r["status"]["end"]["server"] = al.parse_status(status_text() + (";" + part if part else ""))
            return r

        base, gate = al.parse_gate(GC_BASE_GATE), al.parse_gate(GC_ON_GATE)
        self.assertEqual(al.check_gate(base, row(GC_GATE_BASE), "")["result"], "passed")
        self.assertEqual(al.check_gate(base, row(GC_GATE_ON), "")["result"], "failed")
        self.assertEqual(al.check_gate(gate, row(GC_GATE_ON), "")["result"], "passed")
        self.assertEqual(al.check_gate(gate, row(GC_GATE_ON.replace("heldAtEntry=0", "heldAtEntry=2")), "")["result"],
                         "failed")
        self.assertEqual(al.check_gate(gate, row(GC_GATE_BASE), "")["result"], "failed")
        missing = al.check_gate(gate, row(""), "")  # a build without the gate: the run stops (fails closed)
        self.assertEqual(missing["result"], "error")
        self.assertIn("has no gcGate.bit", missing["why"])

    def test_parse_status_flattens_fields_and_drops_lists(self) -> None:
        flat = al.parse_status(status_text(spare_resent=25))
        self.assertEqual(flat["seedCopy"], "on")
        self.assertEqual(flat["seedCopy.spareResent"], "25")
        self.assertEqual(flat["picks.patch"], "active/ok")
        self.assertEqual(flat["seedCopy.recent"], "")  # the bracketed list, with its commas, is left out
        self.assertNotIn("seedCopy.seed 9b]", flat)
        self.assertEqual(al.parse_status(""), {})

    def test_views_deltas_and_names(self) -> None:
        start = al.parse_status(status_text())
        end = al.parse_status(status_text(spare_resent=25, spare_only=2, borrowed=2, gc_count=41))
        record = {"start": {"server": start, "client": start}, "end": {"server": end, "client": None},
                  "after": None, "last": {"client": {"t": 1.0, "status": start}}, "restarted": []}
        views = al.status_views(record)
        self.assertEqual(al.status_value(views, "delta.server.spareResent"), 25)
        self.assertEqual(al.status_value(views, "delta.server.seedCopy.spareResent"), 25)
        self.assertEqual(al.status_value(views, "delta.server.reads"), 0)  # a counter that did not move reads 0
        self.assertEqual(al.status_value(views, "end.server.patch"), "active/ok")
        self.assertEqual(al.status_value(views, "last.client.gcCount"), 39)
        with self.assertRaises(al.MissingName):
            al.status_value(views, "delta.client.spareOnly")  # the client's end reading is missing
        with self.assertRaises(al.MissingName):
            al.status_value(views, "after.server.spareOnly")
        with self.assertRaises(al.MissingName):
            al.status_value(views, "delta.server.nothing")
        with self.assertRaises(al.MissingName):
            al.status_value(views, "sideways.server.x")
        with self.assertRaises(al.ExprError) as caught:
            al.status_value(views, "end.server.count")  # picks.count and order.count
        self.assertIn("order.count, picks.count", str(caught.exception))
        # a game restarted during the arm: its counters started again, so it has no delta
        views = al.status_views(dict(record, end={"server": end, "client": end}, restarted=["server"]))
        with self.assertRaises(al.MissingName):
            al.status_value(views, "delta.server.spareResent")
        self.assertEqual(al.status_value(views, "delta.client.spareOnly"), 2)


# The first lines and the coverage line of boot 5's t3a-1 pull (scripts/sacs_picks.py pull, 2026-10-18 13:18).
T3A1_PULL = """20261018-131829-t3a-1: server 404..2900, copy 195..2263
switches server 7, copy 7; patch server 1/0, copy 1/0
counters since mark: server queries 24383, invalid 0, draws 2615, keyed 2615, requests 2291, full 0, outside 177
                     copy   queries 13481, invalid 0, draws 2064, keyed 2064, requests 353, full 0, outside 11
all drivers:
records: server 2496 (keyed 2496, n>1 2133), copy 2068 (keyed 2068); spares server 85, copy 26, shared 3

coverage: seedCoverage=active:65,seeded:60,spareOnly:2,borrowed:3,borrowedSpare:2,unseeded:0
"""
# Session C's resend gate (arms6.py, boot 5): on the first copy arm in a process that had not read seeds, the server
# must have resent spares, the copy must have taken some, and the pull's coverage must show a borrowed spare.
RESEND_GATE = ('once t3a, t3log: had("sacs.seed-copy") or (delta.server.spareResent > 0 and '
               'delta.client.spareOnly + delta.client.borrowedSpares > 0 and hook.borrowedSpare > 0)')


def gate_row(resent: int, spare_only: int, borrowed: int, had: bool = False) -> dict:
    start = al.parse_status(status_text())
    server_end = al.parse_status(status_text(spare_resent=resent))
    client_end = al.parse_status(status_text(spare_only=spare_only, borrowed=borrowed))
    return {"arm": "t3a", "repeat": 1, "args": [], "sets": ["both:sacs.seed-copy=1"],
            "process": {"server": {"had": ["sacs.seed-copy"] if had else []}, "client": {"had": []}},
            "status": {"start": {"server": start, "client": start}, "end": {"server": server_end, "client": client_end}}}


class Gates(unittest.TestCase):
    def test_parse_gate(self) -> None:
        gate = al.parse_gate(RESEND_GATE)
        self.assertTrue(gate["once"])
        self.assertEqual(gate["arms"], ["t3a", "t3log"])
        self.assertEqual(gate["names"], ["delta.server.spareResent", "delta.client.spareOnly", "delta.client.borrowedSpares",
                                         "hook.borrowedSpare"])
        self.assertTrue(al.gate_applies(gate, "t3log") and not al.gate_applies(gate, "base"))
        self.assertEqual(al.parse_gate('*: on("server:sacs.seed-copy") or arm == "base"')["arms"], ["*"])
        for bad in ('on("server:x")', "t3a: delta.server", "t3a: foo.bar > 1", "t3a: __import__('os')", "t3a: delta.copy.x"):
            with self.assertRaises(SystemExit, msg=bad):
                al.parse_gate(bad)

    def test_hook_values_from_a_real_pull(self) -> None:
        values = al.hook_values(T3A1_PULL)
        self.assertEqual((values["borrowedSpare"], values["spareOnly"], values["active"]), (2, 2, 65))
        self.assertNotIn("seedCoverage", values)
        self.assertEqual(al.hook_values("a=1 b:2. a=3")["a"], [1, 3])
        self.assertEqual(al.hook_values("b:2.")["b"], 2)

    def test_resend_gate_on_boot5_numbers_and_on_zeros(self) -> None:
        gate = al.parse_gate(RESEND_GATE)
        # t3a-1: server spareResent +25, copy spareOnly+borrowedSpares +4, coverage borrowedSpare 2: passes
        result = al.check_gate(gate, gate_row(25, 2, 2), T3A1_PULL)
        self.assertEqual(result["result"], "passed")
        self.assertEqual(result["values"], {"delta.server.spareResent": 25, "delta.client.spareOnly": 2,
                                            "delta.client.borrowedSpares": 2, "hook.borrowedSpare": 2})
        self.assertEqual(al.check_gate(gate, gate_row(0, 2, 2), T3A1_PULL)["result"], "failed")
        self.assertEqual(al.check_gate(gate, gate_row(25, 0, 0), T3A1_PULL)["result"], "failed")
        self.assertEqual(al.check_gate(gate, gate_row(25, 2, 2), T3A1_PULL.replace("borrowedSpare:2", "borrowedSpare:0"))
                         ["result"], "failed")
        # a process that had read seeds already: the resend is not expected, so the gate holds without it
        self.assertEqual(al.check_gate(gate, gate_row(0, 0, 0, had=True), "")["result"], "passed")
        # fails closed: no pull output, or no status reading from the copy
        missing = al.check_gate(gate, gate_row(25, 2, 2), "")
        self.assertEqual(missing["result"], "error")
        self.assertIn("hook.borrowedSpare", missing["why"])
        row = gate_row(25, 2, 2)
        row["status"]["end"]["client"] = None
        self.assertEqual(al.check_gate(gate, row, T3A1_PULL)["result"], "error")


class CrashContext(unittest.TestCase):
    def test_stratified_rate_p(self) -> None:
        # Session D, 2026-10-19: off 4 in 33.03 first-arm min against on's 0 in 25.65; 1 in 28.62 later against 0 in 51.56
        strata = [(4, 33.03, 0, 25.65), (1, 28.62, 0, 51.56)]
        self.assertAlmostEqual(al.stratified_rate_p(strata), (33.03 / 58.68) ** 4 * (28.62 / 80.18), places=6)
        self.assertAlmostEqual(al.stratified_rate_p(strata), 0.0358, places=3)
        # one stratum: the binomial tail on the side the count lies
        self.assertAlmostEqual(al.stratified_rate_p([(3, 10.0, 1, 10.0)]), al.binomial_tail(3, 4, 0.5))
        self.assertAlmostEqual(al.stratified_rate_p([(0, 10.0, 4, 10.0)]), 0.0625)  # fewer than expected: the lower tail
        self.assertIsNone(al.stratified_rate_p([(0, 10.0, 0, 10.0)]))
        self.assertAlmostEqual(al.stratified_rate_p([(2, 0.0, 0, 5.0), (1, 5.0, 0, 5.0)]), 0.5)  # no exposure: left out

    def test_crash_family(self) -> None:
        self.assertEqual(al.crash_family("ACCESS_VIOLATION at 00e55997 (address 0x1d): known interp-indirect-load"),
                         "interp-indirect-load")
        self.assertEqual(al.crash_family("ACCESS_VIOLATION at 00e51f25 (address 0x15): known interp-runtime-method "
                                         "(from get_virtual_method: table 70af0168, its slot held 0x1)"), "interp-runtime-method")
        self.assertEqual(al.crash_family("ACCESS_VIOLATION at 00e51f25 (address 0x15): NEW interpreter crash site"),
                         "NEW 00e51f25")
        self.assertEqual(al.crash_family("ACCESS_VIOLATION at 00d7084b (address 0xc248): not a known crash"),
                         "unknown 00d7084b")
        self.assertEqual(al.crash_family(""), "?")

    def test_first_arm_and_crash_scenario(self) -> None:
        self.assertIs(al.first_arm({"process": {"server": {"arms_before": []}}}), True)
        self.assertIs(al.first_arm({"process": {"server": {"arms_before": ["off#3"]}}}), False)
        self.assertIsNone(al.first_arm({"arm": "off"}))
        self.assertIs(al.first_arm({"process": {"client": {"arms_before": []}}}, "client"), True)
        row = {"died_in": "run", "verdicts": {"a": "PASS", "b": "FAIL", "c": "BLOCKED"}, "totals": {"a": 40.0, "b": 120.0, "c": None}}
        self.assertEqual(al.crash_scenario(row), "b")  # before CRASHED: the last one with a time
        self.assertEqual(al.crash_scenario({**row, "verdicts": {"a": "PASS", "b": "CRASHED", "c": "BLOCKED"}}), "b")
        self.assertEqual(al.crash_scenario({**row, "died_in": None}), "")

    def test_watchdog_hang(self) -> None:
        cut = {"verdict": "CRASHED", "crashed_over": "cut short"}
        line = "watchdog: server: heartbeat frozen while unpaused (simulator tick 100 since 06:00:00)"
        self.assertEqual(al.watchdog_hang({**cut, "failures": ["sit: off by 2 m", line]}), line)
        self.assertIsNone(al.watchdog_hang({**cut, "failures": ["watchdog: client 1: game gone (not running)"]}))
        self.assertIsNone(al.watchdog_hang({"verdict": "CRASHED", "crashed_over": "PASS", "failures": [line]}))
        self.assertIsNone(al.watchdog_hang({**cut, "verdict": "PASS", "failures": [line]}))
        self.assertIsNone(al.watchdog_hang({**cut, "failures": ["stack down: server no answer"]}))

    def test_gc_context(self) -> None:
        t = 1000.0
        rows = [{"t": t - 300, "kind": "open", "side": "server", "pid": 7, "gc": 1},
                {"t": t - 9, "kind": "gc", "side": "server", "pid": 7, "gc": 40},
                {"t": t - 4, "kind": "gc", "side": "server", "pid": 7, "gc": 41},
                {"t": t + 2, "kind": "gc", "side": "server", "pid": 7, "gc": 42},  # after the crash: not the last
                {"t": t + 0.5, "kind": "exit", "side": "server", "pid": 7, "gc": 41},
                {"t": t - 100, "kind": "open", "side": "client", "pid": 8, "gc": 1},
                # gc_watch's mark rows (with MARK set) carry a gc too: they are neither collections nor the open
                {"t": t - 3, "kind": "mark-start", "side": "server", "pid": 7, "gc": 41, "state": 1},
                {"t": t - 2.5, "kind": "mark-end", "side": "server", "pid": 7, "gc": 42, "state": 0}]
        self.assertEqual(al.gc_context(rows, "server", t, pid=99),  # the exit row's pid wins over the snapshot's
                         {"pid": 7, "uptime_min": 5.0, "last_gc_s": 4.0, "interval_s": 5.0, "gc": 41})
        self.assertEqual(al.gc_context([], "server", t, pid=99),
                         {"pid": 99, "uptime_min": None, "last_gc_s": None, "interval_s": None, "gc": None})

    def test_arm_rows_and_crash_rows(self) -> None:
        note = "ACCESS_VIOLATION at 00e55997 (address 0x1d): known interp-indirect-load"
        records = [
            {"arm": "off", "repeat": 1, "run": "r1", "died_in": "run", "verdicts": {"a": "CRASHED"}, "totals": {"a": 9.0},
             "crashes": [f"server xcpt B.txt: {note}"], "process": {"server": {"pid": 7, "arms_before": []}},
             "dump_sizes": {"server-7-20261020-010000-interp-av.dmp": 1500000000, "server-70-x.dmp": 1,
                            "client-9-20261020-010000-unhandled.dmp": None}},
            {"kind": "gap", "arm": "off", "repeat": 1, "crashes": ["client xcpt C.txt: old"]},
            {"arm": "on", "repeat": 1, "run": None, "skipped": "boot hook failed", "crashes_boot": ["server xcpt A.txt: old"]},
        ]
        rows = al.arm_rows(records)
        self.assertEqual([(r["arm"], r.get("crashes_gap")) for r in rows], [("off", ["client xcpt C.txt: old"]), ("on", None)])
        self.assertNotIn("crashes_gap", records[0])  # copies
        times = {"xcpt A.txt": 30.0, "xcpt B.txt": 10.0, "xcpt C.txt": 20.0}
        out = al.crash_rows(records, {("client", "xcpt C.txt"): Path("C")}, lambda p: f"now {p}", [],
                            lambda path, name: times[name])
        self.assertEqual([(c["name"], c["phase"], c["scenario"], c["first"], c["triage"], c["source"]) for c in out],
                         [("xcpt B.txt", "run", "a", True, note, "logged"),
                          ("xcpt C.txt", "gap", "", None, "now C", "report"),
                          ("xcpt A.txt", "boot", "", None, "old", "logged")])
        self.assertEqual((out[0]["pid"], out[0]["arms_before"], out[1]["arm"]), (7, 0, "off"))
        self.assertEqual(out[0]["dumps"], {"server-7-20261020-010000-interp-av.dmp": 1500000000})  # its game's, by pid
        self.assertEqual(out[1]["dumps"], {"client-9-20261020-010000-unhandled.dmp": None})  # no pid: the side's
        self.assertEqual(out[2]["dumps"], {})



class ArmLogs(unittest.TestCase):
    def test_runner_rows_gaps_and_phases(self) -> None:
        records = [
            {"arm": "t3a", "repeat": 1, "args": ["--tune", "both:sacs.node-order=2"], "sets": ["server:sacs.seed-copy=1"],
             "run": "20261019-100000", "scenario_min": 4.0, "crashes_setup": ["server xcpt A.txt: x"], "crashes": [],
             "crashes_after": ["client xcpt B.txt: y"], "process": {"server": {"had": ["sacs.seed-copy"]}}},
            {"kind": "gap", "arm": "t3a", "repeat": 1, "crashes": ["server xcpt C.txt: z"]},
            {"arm": "base", "repeat": 1, "args": [], "run": None, "skipped": "stack down"},
        ]
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "arms.jsonl"
            path.write_text("\n".join(json.dumps(r) for r in records) + "\n", encoding="utf-8")
            rows = al.load_arm_log(path, "x")
        self.assertEqual(len(rows), 1)  # the skipped arm has no exposure
        row = rows[0]
        self.assertEqual(row["label"], "t3a#1")
        self.assertEqual(row["crashes"], [("server", "setup", "xcpt A.txt"), ("client", "after", "xcpt B.txt"),
                                          ("server", "gap", "xcpt C.txt")])
        f = al.row_functions(row)
        self.assertTrue(f["on"]("sacs.seed-copy") and f["on"]("server:sacs.seed-copy"))
        self.assertFalse(f["on"]("client:sacs.seed-copy"))
        self.assertEqual(f["val"]("sacs.node-order"), 2.0)
        self.assertTrue(f["had"]("sacs.seed-copy") and f["had"]("server:sacs.seed-copy"))
        self.assertFalse(f["had"]("client:sacs.seed-copy"))
        self.assertEqual(al.counted(row, "both", run_only=True), 0)
        self.assertEqual(al.counted(row, "server", run_only=False), 2)

    def test_legacy_gap_record_and_arms5_phases(self) -> None:
        records = [
            {"arm": "read-2", "copy": 0, "keyed": 0, "read_loop": 4, "debugger": False, "scenario_min": 0.78,
             "run": "20261018-104656", "died_in_run": True, "crashes": ["xcpt 10.47.31.txt"], "crash_where": ["server"]},
            {"arm": "read-2", "gap_crashes": ["xcpt 10.50.00.txt"], "crash_where": ["client"]},
        ]
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "arms5.jsonl"
            path.write_text("\n".join(json.dumps(r) for r in records) + "\n", encoding="utf-8")
            row = al.load_arm_log(path, "arms5")[0]
        self.assertEqual(row["crashes"], [("server", "run", "xcpt 10.47.31.txt"), ("client", "gap", "xcpt 10.50.00.txt")])
        self.assertTrue(al.row_functions(row)["on"]("server:sacs.seed-read-loop"))
        self.assertFalse(al.row_functions(row)["on"]("client:sacs.seed-read-loop"))

    def test_helpers(self) -> None:
        self.assertEqual(al.expand_tune("both:a=1"), [("server", "a", "1"), ("client", "a", "1")])
        self.assertEqual(al.expand_tune("a=1"), [("client", "a", "1")])
        self.assertEqual(al.args_tunes(["--tune", "x=1", "--quick", "--held", "server:y=2"]), ["x=1", "server:y=2"])
        self.assertEqual(al.crash_parts("client xcpt HOST 26-10-18 13.47.25.txt: known x"),
                         ("client", "xcpt HOST 26-10-18 13.47.25.txt"))
        self.assertEqual(al.base_name("t3log-12"), "t3log")
        self.assertEqual(al.base_name("t3log"), "t3log")
        self.assertEqual(al.parse_added("b:base=40.2/2/1")["crashes"].count(("client", "all", "by hand")), 1)
        with self.assertRaises(SystemExit):
            al.parse_added("base=40")
        self.assertEqual(al.parse_pair("a:b"), ("a", "b"))
        self.assertEqual(al.crash_note("server xcpt A.txt: AV at 00e54af9: NEW"), "AV at 00e54af9: NEW")
        self.assertEqual(al.crash_note("xcpt A.txt"), "")

    def test_legacy_triage_split_per_report(self) -> None:
        # arms6.jsonl's aoff-1 triage (boot 5), and a two-report row made of two real ones
        one = ("xcpt HOST 26-10-18 12.42.32.txt (server profile): ACCESS_VIOLATION at 00e54af9 (address 0x1): NEW "
               "interpreter crash site: attach crash_catch.py before the next run to name the managed method, then add "
               "it to KNOWN_CRASHES")
        two = ("xcpt HOST 26-10-18 13.47.25.txt (Client2Box): ACCESS_VIOLATION at 00e5201f (address 0x30): known "
               "interp-virtual-method: the interpreter's get_virtual_method")
        row = {"arm": "gc-2", "crashes": ["xcpt HOST 26-10-18 12.42.32.txt", "xcpt HOST 26-10-18 13.47.25.txt"],
               "crash_where": ["server", "client"], "triage": one + " " + two}
        notes = al.legacy_notes(row)
        self.assertTrue(notes[("server", "xcpt HOST 26-10-18 12.42.32.txt")].startswith("ACCESS_VIOLATION at 00e54af9"))
        self.assertTrue(notes[("server", "xcpt HOST 26-10-18 12.42.32.txt")].endswith("KNOWN_CRASHES"))
        self.assertTrue(notes[("client", "xcpt HOST 26-10-18 13.47.25.txt")].startswith("ACCESS_VIOLATION at 00e5201f"))
        legacy = al.legacy_row(dict(row, scenario_min=2.0, crash_phase=["run", "run"]), "arms6")
        # both reports gone from disk (TS3 rotated them before the archive existed): matched on the logged triage
        tally = al.match_crashes([legacy], "00e54af9", {}, str)
        self.assertEqual((tally["matched"], tally["gone"], tally["unknown"]), (1, 2, 0))
        self.assertEqual(legacy["crashes"], [("server", "run", "xcpt HOST 26-10-18 12.42.32.txt")])
        self.assertEqual(al.legacy_notes({"crashes": ["x.txt"], "crash_where": ["server"]}), {})

    def test_crashes_matched_by_triage_from_the_report_or_the_log(self) -> None:
        runner = al.runner_row({"arm": "copy", "repeat": 1, "args": [], "run": "r", "scenario_min": 4.0,
                                "crashes": ["server xcpt A.txt: AV at 00e54af9: NEW interpreter crash site",
                                            "client xcpt B.txt: BREAKPOINT at 007f9f46: known debugger-breakpoint"]},
                               "x")
        legacy = al.legacy_row({"arm": "base-1", "scenario_min": 4.0, "crashes": ["xcpt C.txt"], "crash_where": ["server"],
                                "crash_phase": ["run"]}, "arms6")
        fixed = al.parse_added("b:base=40.2/2")
        # A is in the archive with today's triage (its name added since); B and C are gone, C never had a triage
        reports = {("server", "xcpt A.txt"): (Path("archive/server/xcpt A.txt"), True)}
        tally = al.match_crashes([runner, legacy, fixed], "interp-uaf|00e54af9", reports, lambda p: "AV at 00e54af9: known interp-uaf")
        self.assertEqual(tally, {"matched": 1, "other": 2, "archived": 1, "gone": 2, "unknown": 1})
        self.assertEqual(runner["crashes"], [("server", "run", "xcpt A.txt")])
        self.assertEqual(legacy["crashes"], [])
        self.assertEqual(len(fixed["crashes"]), 2)  # a hand-entered count is left alone
        self.assertEqual(runner["seen"][0], "server run xcpt A.txt [archive]: AV at 00e54af9: known interp-uaf")
        self.assertTrue(runner["seen"][1].endswith("known debugger-breakpoint  (not counted)"))
        self.assertEqual(legacy["seen"], ["server run xcpt C.txt: no report left, no triage logged  (not counted)"])
        # no pattern: the listing only, every crash kept
        legacy = al.legacy_row({"arm": "base-1", "scenario_min": 4.0, "crashes": ["xcpt C.txt"], "crash_where": ["server"]}, "a")
        al.match_crashes([legacy], None, {}, str)
        self.assertEqual(len(legacy["crashes"]), 1)
        with self.assertRaises(SystemExit):
            al.match_crashes([legacy], "(", {}, str)


class Exclude(unittest.TestCase):
    """arms --exclude / --exclude-invalid: rows and their gap records left out of the stop counts and the summary."""

    RECORDS = [
        {"arm": "base", "repeat": 1, "run": "r1", "scenario_min": 3.87, "crashes": ["server xcpt A.txt: x"],
         "died_in": "run", "gates": [{"gate": "g", "result": "not evaluated"}]},
        {"arm": "gate", "repeat": 1, "run": "r2", "scenario_min": 4.45, "crashes": [],
         "gates": [{"gate": "g", "result": "passed"}, {"gate": "h", "result": "error"}]},
        {"kind": "gap", "arm": "gate", "repeat": 1, "crashes": ["server xcpt G.txt: x"]},
        {"arm": "gate", "repeat": 2, "run": "r3", "scenario_min": 4.47, "crashes": [],
         "gates": [{"gate": "g", "result": "passed"}]},
    ]

    def test_parse_excludes(self) -> None:
        self.assertEqual(al.parse_excludes(["gate#1, base#2", "20261019-213631/catcher:bare#3"]),
                         [(None, "gate", 1), (None, "base", 2), ("20261019-213631", "catcher:bare", 3)])
        for bad in ("gate", "gate#0", "#1", "gate#x", "a/b/c#1"):
            with self.assertRaises(SystemExit):
                al.parse_excludes([bad])

    def test_a_row_goes_with_its_gap_record(self) -> None:
        kept, dropped = al.exclude_records(self.RECORDS, {("gate", 1)})
        self.assertEqual(kept, [self.RECORDS[0], self.RECORDS[3]])
        self.assertEqual(dropped, self.RECORDS[1:3])
        self.assertEqual(al.excluded_lines(dropped), ["gate#1: 4.45 scenario-min, 1 crash(es) (1 in its gap); gate error"])
        self.assertEqual(al.count_text(al.stop_counts(kept)["gate"]),
                         "4.47 scenario-min, 1 run(s), 0 run crash(es), 0.00 per hour")
        self.assertEqual(al.count_text(al.stop_counts(self.RECORDS)["base"]),
                         "3.87 scenario-min, 1 run(s), 1 run crash(es), 15.50 per hour")
        self.assertEqual(al.count_text(None), "0.00 scenario-min, 0 run(s), 0 run crash(es), - per hour")
        self.assertEqual(al.stop_counts(kept)["gate"]["crashes"], {})  # the gap crash went with gate #1

    def test_invalid_rows_are_failed_or_erring_gates_not_deaths(self) -> None:
        self.assertEqual(al.invalid_rows(self.RECORDS), [("gate", 1)])  # base #1 died: its gate was not evaluated



def write_run(runs: Path, run: str, records: list[dict]) -> None:
    (runs / run).mkdir(parents=True)
    (runs / run / "records.json").write_text(json.dumps(records), encoding="utf-8")


def scenario(name: str, verdict: str, motion: str | None, reset: float, total: float | None) -> dict:
    return {"name": name, "verdict": verdict, "motion": motion and f"C:\\run\\motion\\{motion}",
            "timings": None if total is None else {"reset": reset, "total": total}}


# Session D's arms run 20261019-213631, its arm rows 1 and 4 (base #1, base #2) and their scenario runs, trimmed.
D_PLAN = {"names": ["phone-seated", "phone-standing", "shower", "sit-dining", "sleep-bed", "social-chat"]}
D_ROWS = [
    {"arm": "base", "repeat": 1, "run": "20261019-214012", "started": "2026-10-19 21:40:11", "died_in": "run",
     "scenario_min": 3.87, "wall_min": 4.34, "crashes": ["server xcpt HOST 26-10-19 21.44.04.txt: ACCESS_VIOLATION at 00e51f25"]},
    {"arm": "gate", "repeat": 1, "run": "20261019-214750", "started": "2026-10-19 21:47:49", "died_in": None,
     "scenario_min": 4.45, "wall_min": 4.65, "crashes": []},
    {"arm": "base", "repeat": 2, "run": None, "started": "2026-10-19 23:27:16", "died_in": "run",
     "scenario_min": 0.0, "wall_min": 1.68, "verdicts": {}, "totals": {},
     "crashes": ["server xcpt HOST 26-10-19 23.28.53.txt: ACCESS_VIOLATION at 00e5201f"]},
]
D_RUNS = {
    "20261019-214012": [scenario("phone-seated", "PASS", "214026", 13.6, 49.1),
                        scenario("phone-standing", "PASS", "214117", 13.6, 36.6),
                        scenario("shower", "PASS", "214151", 10.0, 45.1),
                        scenario("sit-dining", "PASS", "214236", 8.5, 38.4),
                        scenario("sleep-bed", "CRASHED", "214320", 13.6, 62.8),
                        scenario("social-chat", "BLOCKED", None, 0, None)],
    "20261019-232717": [scenario("phone-seated", "FAIL", "232730", 13.1, 53.9),
                        scenario("phone-standing", "PASS", "232822", 9.4, 30.7)],
}


class DiedMinutes(unittest.TestCase):
    """G16: an arm that died in its run books the scenario it died in, from that scenario's start to the crash."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.runs = Path(self.tmp.name) / "scenarios"
        self.runs.mkdir()

    def tearDown(self):
        self.tmp.cleanup()

    def test_a_crashed_scenario_counts_to_the_crash_not_to_its_record_end(self):
        write_run(self.runs, "20261020-100000", [scenario("a", "PASS", "100010", 10.0, 60.0),
                                                  scenario("b", "CRASHED", "100115", 5.0, 90.0)])
        row = {"arm": "x", "repeat": 1, "run": "20261020-100000", "started": "2026-10-20 09:59:59", "died_in": "run",
               "scenario_min": 2.5, "wall_min": 3.0, "crashes": ["server xcpt BOX 26-10-20 10.01.40.txt: AV"]}
        booked = al.book_minutes(row, self.runs)
        # b began at 10:01:10 (its watch at 10:01:15 less its 5 s reset) and the game died at 10:01:40: 30 s, not 90.
        self.assertEqual((booked["completed_min"], booked["finished_min"], booked["died_min"], booked["scenario_min"]),
                         (2.5, 1.0, 0.5, 1.5))
        self.assertEqual((booked["died_scenario"], booked["died_from"], booked["crash_at"], booked["died_how"]),
                         ("b", "10:01:10", "10:01:40", "crash report"))
        self.assertIs(al.book_minutes(booked, self.runs), booked, "A booked row is left as it is.")
        self.assertEqual(al.crash_scenario(booked), "b")

    def test_an_unlinked_run_is_found_by_time_and_the_unrecorded_scenario_named(self):
        write_run(self.runs, "20261020-120001", [scenario("a", "PASS", "120011", 10.0, 50.0)])
        write_run(self.runs, "20261020-123000", [])  # a later arm's run: outside this arm's window
        row = {"arm": "x", "repeat": 2, "run": None, "started": "2026-10-20 12:00:00", "died_in": "run",
               "scenario_min": 0.0, "wall_min": 1.5, "verdicts": {}, "totals": {},
               "crashes": ["client xcpt BOX 26-10-20 12.01.11.txt: AV"]}
        booked = al.book_minutes(row, self.runs, ["a", "b", "c"])
        self.assertEqual((booked["run"], booked["run_linked"]), ("20261020-120001", "by time"))
        # a ran 12:00:01-12:00:51; b, never recorded, from then to the crash at 12:01:11.
        self.assertEqual((booked["finished_min"], booked["died_min"], booked["scenario_min"], booked["died_scenario"]),
                         (0.83, 0.33, 1.17, "b"))
        self.assertEqual(booked["verdicts"], {"a": "PASS", "b": "CRASHED"})
        self.assertEqual(booked["totals"], {"a": 50.0})

    def test_a_death_with_no_report_counts_to_the_run_end(self):
        write_run(self.runs, "20261020-140000", [])
        row = {"arm": "x", "repeat": 1, "run": "20261020-140000", "started": "2026-10-20 14:00:00", "died_in": "run",
               "scenario_min": 0.0, "wall_min": 0.5, "crashes": []}
        booked = al.book_minutes(row, self.runs, ["a"])
        self.assertEqual((booked["died_scenario"], booked["died_min"], booked["died_how"]),
                         ("a", 0.5, "the run's end (no crash report)"))

    def test_rows_that_did_not_die_in_their_run_keep_their_minutes(self):
        for died in (None, "setup", "after"):
            row = {"arm": "x", "repeat": 1, "run": "r", "started": "2026-10-20 14:00:00", "died_in": died,
                   "scenario_min": 4.2, "wall_min": 5.0, "crashes": ["server xcpt BOX 26-10-20 14.01.00.txt: AV"]}
            booked = al.book_minutes(row, self.runs)
            self.assertEqual((booked["scenario_min"], booked["completed_min"]), (4.2, 4.2))
            self.assertNotIn("died_min", booked)
        gap = {"arm": "x", "repeat": 1, "kind": "gap", "crashes": []}
        self.assertIs(al.book_minutes(gap, self.runs), gap)

    def test_no_run_folder_keeps_the_runners_minutes(self):
        row = {"arm": "x", "repeat": 1, "run": "20261020-150000", "started": "2026-10-20 15:00:00", "died_in": "run",
               "scenario_min": 2.0, "wall_min": 3.0, "crashes": ["server xcpt BOX 26-10-20 15.01.00.txt: AV"]}
        booked = al.book_minutes(row, self.runs)
        self.assertEqual((booked["scenario_min"], booked["completed_min"]), (2.0, 2.0))
        self.assertEqual(al.died_lines([booked]), ["x#1: no run folder to read: the runner's minutes kept (2.00)"])

    def test_session_ds_rows_1_and_4_read_back(self):
        folder = Path(self.tmp.name) / "arms" / "20261019-213631"
        folder.mkdir(parents=True)
        (folder / "plan.json").write_text(json.dumps(D_PLAN), encoding="utf-8")
        for run, records in D_RUNS.items():
            write_run(self.runs, run, records)
        (folder / "arms.jsonl").write_text("".join(json.dumps(r) + "\n" for r in D_ROWS), encoding="utf-8")
        booked = al.book_log(D_ROWS, folder)
        one, four = booked[0], booked[2]
        # base #1: four finished (169.2 s) and sleep-bed from 21:43:06 (its watch at 21:43:20 less 13.6 s) to the
        # crash at 21:44:04, not its record's 62.8 s, which ran past the crash to the runner noticing.
        self.assertEqual((one["scenario_min"], one["finished_min"], one["died_min"], one["completed_min"]),
                         (3.78, 2.82, 0.96, 3.87))
        self.assertEqual((one["died_scenario"], one["died_from"], one["crash_at"]), ("sleep-bed", "21:43:06", "21:44:04"))
        # base #2: the runner never linked its run (it died before printing its report line): 0.0 booked. Its run
        # folder held two finished scenarios (84.6 s); shower ran from 23:28:43 to the crash at 23:28:53.
        self.assertEqual((four["run"], four["run_linked"]), ("20261019-232717", "by time"))
        self.assertEqual((four["scenario_min"], four["finished_min"], four["died_min"], four["completed_min"]),
                         (1.57, 1.41, 0.16, 0.0))
        self.assertEqual((four["died_scenario"], four["died_from"], four["crash_at"]), ("shower", "23:28:43", "23:28:53"))
        self.assertEqual(booked[1]["scenario_min"], 4.45)
        counts, completed = al.stop_counts(booked), al.stop_counts(al.as_completed(booked))
        self.assertEqual((round(counts["base"]["min"], 2), counts["base"]["runs"]), (5.35, 2))
        self.assertEqual((round(completed["base"]["min"], 2), completed["base"]["runs"]), (3.87, 1))
        stops = [al.parse_stop("base.min >= 5", {"base", "gate"})]
        self.assertEqual(al.stop_check(stops, booked)["fired"], "base.min >= 5")
        self.assertIsNone(al.stop_check(stops, al.as_completed(booked))["fired"])
        self.assertTrue(al.died_lines(booked)[1].startswith(
            "base#2: 1.57 scenario-min = 1.41 finished + 0.16 in shower (23:28:43 to 23:28:53, crash report); booked 0.00; "
            "run 20261019-232717 linked by time"))
        pooled = al.load_arm_log(folder / "arms.jsonl", "d")
        self.assertEqual([(r["label"], r["minutes"]) for r in pooled], [("base#1", 3.78), ("gate#1", 4.45), ("base#2", 1.57)])


if __name__ == "__main__":
    unittest.main()
