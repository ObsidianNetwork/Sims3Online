"""Unit tests for scenarios.py's automatic exclusion of polluted runs: python -m unittest scripts.scenarios_test"""
from __future__ import annotations

import contextlib
import io
import json
import os
import re
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path
from types import SimpleNamespace
from typing import Callable
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
try:
    import crash_catch  # noqa: E402
except ImportError:
    crash_catch = None
import machine_load  # noqa: E402
import quick_loop  # noqa: E402
import scenarios as sc  # noqa: E402
import routing_evidence as routing  # noqa: E402

_RUN, _PYTHON, _ENSURE_PLAYER2, _LEDGER_RECORD = subprocess.run, sc.python, sc.ensure_player2, sc.ledger_record
_ENSURE_PLAYER = sc.ensure_player
_RESET_CHECK = sc.reset_check
_SELECT_SERVER_SIM = sc.select_server_sim
_DIAG_STATE = sc.diag_state
_PHYSICAL_FORK_CENSUS = quick_loop.physical_fork_census


def _no_live_bridge(args, *rest, **kwargs):
    """A bridgectl call no test mocked fails the test instead of reaching a live game."""
    if any("bridgectl" in str(arg) for arg in (args if isinstance(args, (list, tuple)) else [args])):
        raise AssertionError(f"a test reached a live game: {args}")
    return _RUN(args, *rest, **kwargs)


def _no_live_python(*args: str, **kwargs) -> tuple[int, str]:
    raise AssertionError(f"a test ran a live script (mock scenarios.python): {args[:3]}")


def setUpModule() -> None:
    # Every test is offline. Until 2026-10-21 three TwoClients tests read the live games' replay counters through
    # replay_check (a failed read is swallowed, so they passed), and a reset test once moved two live cameras.
    subprocess.run = _no_live_bridge
    sc.python = _no_live_python
    # a scenario that watches client 2 reads player 2's Sim first; offline it is already on the one asked for
    sc.ensure_player2 = lambda sim: (True, f"player 2 on {sim}")
    sc.ensure_player = lambda player, sim: (True, f"player {player} on {sim}")
    # the reset's last check reads both games' Sims and conversations: offline everything the reset left is fine
    sc.reset_check = lambda sims, players, clients: ([], ["reset check: offline"])
    sc.select_server_sim = lambda name="Jennifer": f"server selected: {name} (OK)"
    sc.ledger_record = lambda: None  # health() notes the change set in the main checkout's ledger: not from tests
    sc.diag_state = lambda endpoint: None  # the reset's speed check reads DIAG_STATE in process: no game answers here
    sc.WATCHDOG = False  # its probes go over the bridge in process: the Watchdog tests below give it fakes
    quick_loop.physical_fork_census = lambda: {"state": "fork", "selectedAttempt": "offline-test"}


def tearDownModule() -> None:
    subprocess.run, sc.python, sc.ensure_player2, sc.ledger_record = _RUN, _PYTHON, _ENSURE_PLAYER2, _LEDGER_RECORD
    sc.WATCHDOG = True
    sc.ensure_player = _ENSURE_PLAYER
    sc.reset_check = _RESET_CHECK
    sc.select_server_sim = _SELECT_SERVER_SIM
    sc.diag_state = _DIAG_STATE
    quick_loop.physical_fork_census = _PHYSICAL_FORK_CENSUS


REPORT = "report: x/20261018-101500/report.md\n"  # a `scenarios.py run` output naming its run
# machine_load's summary of one run ( G19)
LOAD = {"from": "2026-10-20T01:00:00Z", "to": "2026-10-20T01:10:00Z", "interval_s": 5.0, "samples": 121, "seconds": 600.0,
        "cpu_pct": {"mean": 30.0, "max": 70.0}, "avail_mb": {"min": 3000, "mean": 3500, "start": 4000, "end": 3200},
        "total_mb": 65000, "reader": "test",
        "top": [{"name": "TS3W.exe", "pid": 1, "cpu_pct": 12.0, "max_pct": 30, "cpu_s": 100.0, "ws_mb": 1500}],
        "others": [{"name": "MsMpEng.exe", "pid": 2, "cpu_pct": 4.0, "max_pct": 20, "cpu_s": 40.0, "ws_mb": 300}]}


class FakeSampler:
    def __init__(self, interval: float = 2.0, read=None) -> None:
        self.interval = interval

    def start(self) -> "FakeSampler":
        return self

    def stop(self) -> dict:
        return dict(LOAD, interval_s=self.interval, series=[[0, 30.0, 3000]])


def record(name: str, debugger: list[str] | dict[str, str] | None = None) -> dict:
    r = {"name": name, "failures": [], "verdict": "PASS", "met": True}
    if debugger:
        r["debugger"] = debugger
    return r


class AutoExclude(unittest.TestCase):
    def setUp(self) -> None:
        self.folder = tempfile.TemporaryDirectory()
        self.run = Path(self.folder.name)

    def tearDown(self) -> None:
        self.folder.cleanup()

    def test_clean_run_stays_in(self) -> None:
        self.assertIsNone(sc.auto_exclude(self.run, [record("shower")], None, []))
        self.assertIsNone(sc.excluded(self.run))

    def test_debugger_names_side_and_scenarios(self) -> None:
        records = [record("shower", ["server"]), record("sit", ["client", "server"]), record("sleep")]
        reason = sc.auto_exclude(self.run, records, None, [])
        self.assertEqual(reason, "auto: debugger attached to the client during sit; "
                                 "debugger attached to the server during shower, sit")
        self.assertEqual(sc.excluded(self.run), reason)

    def test_stack_down_and_crash_reports(self) -> None:
        self.assertEqual(sc.auto_exclude(self.run, [], "stack down: server no answer", ["xcpt A.txt: known x"]),
                         "auto: cut short (stack down: server no answer)")  # health's line already names the crash
        (self.run / sc.EXCLUDED).unlink()
        self.assertEqual(sc.auto_exclude(self.run, [record("shower")], None, ["xcpt A.txt: known x"]),
                         "auto: a game crashed (xcpt A.txt: known x)")

    def test_reason_given_by_hand_is_kept(self) -> None:
        (self.run / sc.EXCLUDED).write_text("cdb attached to the server\n", encoding="utf-8")
        self.assertIsNone(sc.auto_exclude(self.run, [record("shower", ["server"])], None, []))
        self.assertEqual(sc.excluded(self.run), "cdb attached to the server")

    def test_auto_reason_grows_and_is_not_rewritten_when_unchanged(self) -> None:
        records = [record("shower", ["server"])]
        first = sc.auto_exclude(self.run, records, None, [])
        self.assertIsNone(sc.auto_exclude(self.run, records, None, []))  # same reason: nothing to write
        records.append(record("sit", ["server"]))
        second = sc.auto_exclude(self.run, records, None, [])
        self.assertNotEqual(first, second)
        self.assertEqual(sc.excluded(self.run), "auto: debugger attached to the server during shower, sit")

    def test_kinds_are_worded_apart(self) -> None:
        records = [record("shower", {"server": "passive", "client": "passive"}), record("sit", {"server": "trace"}),
                   record("sleep", {"client": "other"})]
        reason = sc.auto_exclude(self.run, records, None, [])
        self.assertIn("debugger attached to the client during sleep", reason)
        self.assertIn("passive crash catcher on the client (left out until its overhead is measured", reason)
        self.assertIn("passive crash catcher on the server", reason)
        self.assertIn("cdb trace (logging breakpoints) on the server during sit", reason)

    def test_measured_catcher_runs_count(self) -> None:
        records = [record("shower", {"server": "passive"}), record("sit", {"client": "passive"})]
        with mock.patch.object(sc, "CATCHER_MEASURED", "2026-10-19 arms/x: no difference"):
            self.assertIsNone(sc.auto_exclude(self.run, records, None, []))
            records.append(record("sleep", {"server": "trace"}))
            self.assertEqual(sc.auto_exclude(self.run, records, None, []),
                             "auto: cdb trace (logging breakpoints) on the server during sleep")

    def test_report_says_when_left_out(self) -> None:
        sc.report(self.run, [])
        self.assertNotIn("Left out", (self.run / "report.md").read_text(encoding="utf-8"))
        sc.auto_exclude(self.run, [record("shower", ["server"])], None, [])
        sc.report(self.run, [])
        text = (self.run / "report.md").read_text(encoding="utf-8")
        self.assertIn("**Left out of history and the survey:** auto: debugger attached to the server during shower", text)
        self.assertIn("--undo` keeps it", text)


class Debuggers(unittest.TestCase):
    def test_merge_keeps_the_more_intrusive_kind(self) -> None:
        self.assertEqual(sc.merge_debuggers({"server": "passive"}, {"server": "trace", "client": "passive"}),
                         {"server": "trace", "client": "passive"})
        self.assertEqual(sc.merge_debuggers({"server": "other"}, {"server": "passive"}), {"server": "other"})
        self.assertEqual(sc.merge_debuggers({}, {}), {})

    def test_entries_read_old_and_new_records(self) -> None:
        self.assertEqual(sc.debugger_entries(record("a", ["server", "client"])), [("server", "attached"), ("client", "attached")])
        self.assertEqual(sc.debugger_entries(record("a", {"server": "trace", "client": "passive"})),
                         [("client", "passive"), ("server", "trace")])
        self.assertEqual(sc.debugger_entries(record("a")), [])


def arm_row(arm: str, repeat: int, verdicts: dict[str, str], totals: dict[str, float], crashes: int = 0,
            rates: list[float] | None = None) -> dict:
    return {"arm": arm, "repeat": repeat, "args": [], "run": f"2026101{repeat}-00000{repeat}", "wall_min": 1.0,
            "scenario_min": round(sum(totals.values()) / 60, 2), "verdicts": verdicts, "totals": totals, "debugger": {},
            "crashes": [f"xcpt {i}.txt" for i in range(crashes)], "catcher_exceptions_per_min": rates or [],
            "excluded": None}


class Arms(unittest.TestCase):
    def test_parse_arm(self) -> None:
        self.assertEqual(sc.parse_arm("t3on=--tune both:sacs.keyed-picks=1 --module Foo"),
                         ("t3on", ["--tune", "both:sacs.keyed-picks=1", "--module", "Foo"]))
        self.assertEqual(sc.parse_arm("bare="), ("bare", []))
        self.assertEqual(sc.parse_arm("q='a b'"), ("q", ["a b"]))
        with self.assertRaises(SystemExit):
            sc.parse_arm("two words=--x")

    def test_order_alternates(self) -> None:
        arms = [("a", ["-1"]), ("b", [])]
        self.assertEqual([(r, n) for r, n, _ in sc.arm_order(arms, 3)], [(1, "a"), (1, "b"), (2, "b"), (2, "a"), (3, "a"), (3, "b")])
        self.assertEqual(sc.arm_order(arms, 1)[0], (1, "a", ["-1"]))

    def test_hook_argv(self) -> None:
        self.assertEqual(sc.hook_argv("scripts/sacs_picks.py mark {arm}-{repeat}", "t3on", 2),
                         [sys.executable, "scripts/sacs_picks.py", "mark", "t3on-2"])
        self.assertEqual(sc.hook_argv("git log -1", "a", 1), ["git", "log", "-1"])
        self.assertEqual(sc.hook_argv("", "a", 1), [])
        self.assertEqual(sc.hook_argv("pull.py {arm} --down {down}", "a", 1, "server"),
                         [sys.executable, "pull.py", "a", "--down", "server"])
        self.assertEqual(sc.hook_argv("pull.py --down {down}", "a", 1)[-2:], ["--down", "none"])  # never an empty word

    def test_hook_argv_fills_the_boot_env(self) -> None:
        env = {"server": {"SIMLINK_GCGATE": "1", "A": "x y"}, "client": {}}
        self.assertEqual(sc.hook_argv("quick_loop.py run --boot {server_env} {client_env}", "a", 1, boot_env=env)[1:],
                         ["quick_loop.py", "run", "--boot", "--server-env", "SIMLINK_GCGATE=1", "--server-env", "A=x y"])
        self.assertEqual(sc.hook_argv("boot {server_env} {client_env}", "a", 1), ["boot"])  # no env: no words

    def test_split_boot_env(self) -> None:
        env, rest = sc.split_boot_env(["--tune", "both:x=1", "--server-env", "A=1", "--client-env=B=2 3",
                                       "--server-env=A=4"])
        self.assertEqual((env, rest), ({"server": {"A": "4"}, "client": {"B": "2 3"}}, ["--tune", "both:x=1"]))
        for bad in (["--server-env"], ["--server-env", "NO_VALUE"], ["--client-env==1"], ["--client-env", "1A=1"]):
            with self.assertRaises(SystemExit):
                sc.split_boot_env(bad)
        with self.assertRaises(SystemExit):  # a boot inside the run would undo the runner's --set
            sc.arm_boot_envs([("gate", ["--boot", "--server-env", "A=1"])], sc.wide_env([], []))

    def after_on_a_crash(self, after: str, sides: list[str], dumps: tuple[str, ...] = (),
                         wait: dict | None = None) -> tuple[list[dict], list, list[str]]:
        """One arm whose run leaves the server down, with this after hook: (the logged rows, the hook calls, prints)."""
        printed: list[str] = []
        healths = iter([None, "stack down: server no answer (crash report xcpt 1.txt: x)"])
        crashes = iter([[], [], ["server xcpt 1.txt: x"], ["server xcpt 1.txt: x", "client xcpt 2.txt: y"]])
        with tempfile.TemporaryDirectory() as folder, \
                mock.patch.object(sc, "ARMS", Path(folder)), mock.patch.object(sc, "python", return_value=(0, REPORT)), \
                mock.patch.object(sc, "health", side_effect=lambda: next(healths)), \
                mock.patch.object(sc, "down_sides", return_value=sides), \
                mock.patch.object(sc, "crashes_since", side_effect=lambda since, sides=False: next(crashes)), \
                mock.patch.object(sc, "free_gb", return_value=50.0), mock.patch.object(sc, "dumps_since", return_value=list(dumps)), \
                mock.patch.object(sc, "wait_dumps", return_value=wait or {"waited_s": 0.0, "timed_out": False, "dumps": {}}), \
                mock.patch.object(sc, "run_hook", return_value="exit 0") as hook, \
                mock.patch("builtins.print", side_effect=lambda *a, **k: printed.append(" ".join(map(str, a)))):
            sc.arms(["sit"], ["a="], 1, "", after, False, False)
            out = next(Path(folder).iterdir())
            logged = [json.loads(line) for line in (out / "arms.jsonl").read_text(encoding="utf-8").splitlines()]
        return logged, hook.call_args_list, printed

    def test_an_after_hook_that_names_down_runs_on_a_crash(self) -> None:
        logged, calls, printed = self.after_on_a_crash("scripts/pull.py {arm}-{repeat} --down {down}", ["server"])
        self.assertEqual(len(calls), 1)
        self.assertEqual(calls[0].kwargs["down"], "server")
        row = logged[-1]
        self.assertEqual((row["died_in"], row["after_down"]), ("run", "server"))
        self.assertEqual(row["crashes_after"], ["client xcpt 2.txt: y"])  # the run's own crash is not the hook's
        self.assertTrue(any(line.startswith("   after (server down): exit 0") for line in printed))

    def test_an_after_hook_without_down_is_skipped_on_a_crash(self) -> None:
        logged, calls, printed = self.after_on_a_crash("scripts/pull.py {arm}", ["server"])
        self.assertEqual(calls, [])
        self.assertNotIn("after_down", logged[-1])
        self.assertTrue(any("after: skipped (a game is down" in line for line in printed))

    def test_each_arm_row_keeps_its_build_slot_and_the_machines_load(self) -> None:
        with mock.patch.object(machine_load, "Sampler", FakeSampler), \
                mock.patch.object(sc, "installed_build", return_value={"slot": "old", "digest": "abc123"}):
            logged, _, printed = self.after_on_a_crash("", ["server"])
        row = logged[-1]
        self.assertEqual(row["build_slot"], {"slot": "old", "digest": "abc123"})
        self.assertEqual(row["load"], dict(LOAD, interval_s=sc.ARM_LOAD_INTERVAL))  # the series is left out
        self.assertIn("   build slot: old (abc123)", printed)
        self.assertTrue(any(line.startswith("   load: 600 s, 121 samples; cpu 30.0% mean") for line in printed))
        with tempfile.TemporaryDirectory() as folder:
            pointer = Path(folder) / "build-slot.json"
            with mock.patch.object(quick_loop, "SLOT_POINTER", pointer):
                self.assertIsNone(sc.installed_build())
                pointer.write_text(json.dumps({"slot": "new", "digest": "d1", "server": "x.exe"}), encoding="utf-8")
                self.assertEqual(sc.installed_build(), {"slot": "new", "digest": "d1"})

    def test_a_runs_dumps_keep_their_final_sizes(self) -> None:
        name = "server-101-20261020-010000-interp-av.dmp"
        wait = {"waited_s": 21.0, "timed_out": False, "dumps": {name: 3 << 20}}
        with tempfile.TemporaryDirectory() as folder:
            (Path(folder) / name).write_bytes(b"x" * (3 << 20))
            with mock.patch.object(sc, "DUMP_DIR", Path(folder)):
                logged, _, printed = self.after_on_a_crash("", ["server"], dumps=(name,), wait=wait)
        row = logged[-1]
        self.assertEqual((row["dump_sizes"], row["dump_wait"]), ({name: 3 << 20}, wait))
        self.assertTrue(any(f"1 dump(s) ({name} 3 MB)" in line for line in printed))
        self.assertEqual(sc.dump_sizes(["gone.dmp"], Path(tempfile.gettempdir())), {"gone.dmp": None})

    def test_down_sides_checks_each_game_once(self) -> None:
        replies = {sc.SERVER: "accepted=False", sc.CLIENT: "accepted=True inWorld=True"}
        with mock.patch.object(sc, "python", side_effect=lambda *a, timeout=0: (0, replies[a[2]])) as call:
            self.assertEqual(sc.down_sides(), ["server"])
        self.assertEqual(call.call_count, 2)

    def test_arm_record_reads_the_run(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            runs = Path(folder)
            run = runs / "20261018-101500"
            run.mkdir()
            (run / "records.json").write_text(json.dumps([
                {"name": "shower", "verdict": "PASS", "timings": {"total": 90.0}, "debugger": {"server": "passive"}},
                {"name": "sit", "verdict": "BLOCKED", "timings": {"total": 30.0}},
                {"name": "sleep", "verdict": "FAIL", "timings": {"total": 150.0}}]), encoding="utf-8")
            (run / sc.EXCLUDED).write_text("auto: passive crash catcher on the server\n", encoding="utf-8")
            text = ("   catcher: server: cdb 1 on TS3W 2 (passive), up, since x; first-chance exceptions 12 (3.5/min)\n"
                    f"report: {run / 'report.md'}\n")
            row = sc.arm_record("catcher", 1, ["--catcher"], 1000.0, 1300.0, text, ["xcpt A.txt: known x"], runs)
        self.assertEqual(row["run"], "20261018-101500")
        self.assertEqual(row["wall_min"], 5.0)
        self.assertEqual(row["scenario_min"], 4.0)  # BLOCKED did not run
        self.assertEqual(row["verdicts"], {"shower": "PASS", "sit": "BLOCKED", "sleep": "FAIL"})
        self.assertEqual(row["debugger"], {"shower": {"server": "passive"}})
        self.assertEqual(row["catcher_exceptions_per_min"], [3.5])
        self.assertEqual(row["excluded"], "auto: passive crash catcher on the server")
        self.assertEqual(row["crashes"], ["xcpt A.txt: known x"])

    @unittest.skipIf(crash_catch is None, 'internal crash_catch/gc_watch excluded in mirror')
    def test_arm_record_reads_the_catchers_cdb_log(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            runs = Path(folder) / "scenarios"
            run = runs / "20261020-050014"
            run.mkdir(parents=True)
            (run / "records.json").write_text(json.dumps([{"name": "shower", "verdict": "PASS",
                                                           "timings": {"total": 90.0}}]), encoding="utf-8")
            dumps = Path(folder) / "dumps"
            dumps.mkdir()
            log = dumps / "cdb-server-35984-20261020-050014.log"
            log.write_text("(8c90.d750): Break instruction exception - code 80000003 (first chance)\n"
                           "(8c90.1111): Access violation - code c0000005 (first chance)\n"
                           "SIMSONLINE-AV\neip=00e574c6 esp=1\n"
                           "(8c90.1111): Access violation - code c0000005 (!!! second chance !!!)\n"
                           "(8c90.67f8): Break instruction exception - code 80000003 (first chance)\n", encoding="utf-8")
            born = time.time()
            os.utime(log, (born + 120, born + 120))  # written 2 min after its creation
            (dumps / "cdb-server-35984-20261020-040000.log").write_text("another run's\n", encoding="utf-8")
            real_stat = Path.stat

            def stat(path: Path, *args, **kwargs):
                # the log's creation time as Windows keeps it (st_birthtime): os.stat on Linux has none
                st = real_stat(path, *args, **kwargs)
                return SimpleNamespace(st_mode=st.st_mode, st_mtime=st.st_mtime, st_birthtime=born) if path == log else st

            with mock.patch.object(Path, "stat", stat):
                row = sc.arm_record("ring", 1, [], 1000.0, 1300.0, f"report: {run / 'report.md'}\n", [], runs)
        self.assertEqual({k: v for k, v in row["catcher"]["server"].items() if k != "minutes"},
                         {"logs": ["cdb-server-35984-20261020-050014.log"], "first_chance": 1, "second_chance": 1,
                          "breaks": 2, "avs": 1})
        self.assertAlmostEqual(row["catcher"]["server"]["minutes"], 2.0, delta=0.05)
        self.assertAlmostEqual(row["catcher_exceptions_per_min"][0], 0.5, delta=0.02)

    def test_arm_record_counts_soak_minutes_apart(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            runs = Path(folder)
            run = runs / "20261018-101500"
            run.mkdir()
            (run / "records.json").write_text(json.dumps([{"name": "soak", "kind": "soak", "verdict": "PASS",
                                                           "timings": {"reset": 6.0, "soak": 1200.0}}]), encoding="utf-8")
            row = sc.arm_record("idle", 1, ["--soak", "20"], 1000.0, 2300.0, f"report: {run / 'report.md'}\n", [], runs)
        self.assertEqual((row["scenario_min"], row["soak_min"]), (0, 20.0))

    def test_arm_record_without_a_run(self) -> None:
        row = sc.arm_record("a", 1, [], 0.0, 60.0, "stack down: server no answer", [], Path(tempfile.gettempdir()))
        self.assertIsNone(row["run"])
        self.assertEqual((row["verdicts"], row["scenario_min"], row["excluded"]), ({}, 0, None))

    def test_arm_record_counts_watchdog_hangs_apart(self) -> None:
        """Main, 2026-10-22: a scenario the run watchdog cut short on a live game is a hang, not a crash."""
        hang = "watchdog: client 1 bridge: no answer to DIAG_STATE in 10 s (" \
            "SystemExit); no answer for 90 s (last DIAG_STATE answered 06:00:00); TS3W 4242 alive"
        records = [{"name": "sit", "verdict": "PASS", "timings": {"total": 60.0}},
                   {"name": "chat", "verdict": "CRASHED", "crashed_over": "cut short", "failures": ["x", hang],
                    "timings": {"total": 200.0}},
                   {"name": "gone", "verdict": "CRASHED", "crashed_over": "cut short",
                    "failures": ["watchdog: client 1: game gone (gamePid 4242: not running)"], "timings": {"total": 30.0}},
                   {"name": "down", "verdict": "CRASHED", "crashed_over": "PASS",
                    "failures": ["stack down: server no answer"], "timings": {"total": 30.0}}]
        with tempfile.TemporaryDirectory() as folder:
            runs = Path(folder)
            run = runs / "20261022-101500"
            run.mkdir()
            (run / "records.json").write_text(json.dumps(records), encoding="utf-8")
            text = f"report: {run / 'report.md'}\n"
            row = sc.arm_record("off", 1, [], 1000.0, 1300.0, text, [], runs)
            crashed = sc.arm_record("off", 1, [], 1000.0, 1300.0, text, ["server xcpt.txt: x"], runs)
        self.assertEqual(row["hangs"], {"chat": hang})  # a game gone and a stack found down stay crashes
        self.assertEqual(row["scenario_min"], 5.33)  # the hung scenario's minutes up to the cut stay booked
        self.assertEqual("".join(sc.verdict_code(row, name) for name in row["verdicts"]), "PHCC")
        self.assertEqual(crashed["hangs"], {})  # a crash report in the run: its cut-short scenario is the crash

    def test_summary_counts_hangs_apart_unless_asked(self) -> None:
        hung = arm_row("a", 1, {"sit": "PASS", "chat": "CRASHED"}, {"sit": 60.0, "chat": 120.0})
        hung.update(hangs={"chat": "watchdog: client 1 bridge: no answer"}, died_in="hang")
        rows = [hung, arm_row("b", 1, {"sit": "PASS", "chat": "PASS"}, {"sit": 60.0, "chat": 120.0}, crashes=1)]
        lines = sc.summarize_arms(rows)
        self.assertRegex(lines[1], r"^a\s+1\s+3\.0\s+0\s+0\.0\s+-\s+H1 P1  \(1 hang\(s\): the run watchdog cut a "
                                   r"scenario short on a live game; not crashes\)$")
        self.assertRegex(next(line for line in lines if line.startswith("chat")), r"^chat\s+H\s+P 120\.0s")
        counted = sc.summarize_arms(rows, hangs_as_crashes=True)
        self.assertRegex(counted[1], r"^a\s+1\s+3\.0\s+1\s+20\.0\s+-\s+H1 P1  \(1 hang\(s\) counted as crashes")

    def test_summary_table(self) -> None:
        rows = [arm_row("a", 1, {"shower": "PASS", "sit": "FAIL"}, {"shower": 60.0, "sit": 120.0}, crashes=1),
                arm_row("b", 1, {"shower": "PASS", "sit": "PASS"}, {"shower": 80.0, "sit": 100.0}),
                arm_row("b", 2, {"shower": "PASS", "sit": "PASS"}, {"shower": 100.0, "sit": 100.0}),
                {"arm": "a", "repeat": 2, "args": [], "run": None, "skipped": "stack down"}]
        lines = sc.summarize_arms(rows)
        self.assertIn("P", lines[0].split())
        self.assertRegex(lines[1], r"^a\s+1\s+3\.0\s+1\s+20\.0\s+-\s+F1 P1\s+\(1 not run\)$")  # the reference
        self.assertRegex(lines[2], r"^b\s+2\s+6\.3\s+0\s+0\.0\s+0\.32\s+P4$")  # 0 in 6.3 min against 1 in 3.0
        self.assertTrue(lines[3].startswith("P: two-sided exact test of each arm's crash rate against a's"))
        self.assertEqual(lines[4], "run crashes by site: a: ? 1")  # no triage logged
        self.assertRegex(lines[7], r"^shower\s+P 60\.0s\s+PP 90\.0s\s*$")
        self.assertRegex(lines[8], r"^sit\s+F 120\.0s\s+PP 100\.0s\s*$")

    def test_summary_soak_minutes_and_p_by_kind(self) -> None:
        def soak(arm: str, repeat: int, minutes: float, crashes: int) -> dict:
            row = arm_row(arm, repeat, {"soak": "PASS"}, {}, crashes=crashes)
            row.update(scenario_min=0, soak_min=minutes)
            return row

        rows = [soak("base", 1, 30.0, 4), soak("nopatch", 1, 30.0, 0), soak("nopatch", 2, 30.0, 0),
                arm_row("scen", 1, {"sit": "PASS"}, {"sit": 600.0}, crashes=1)]
        lines = sc.summarize_arms(rows)
        self.assertEqual(lines[0].split()[:5], ["arm", "runs", "scen-min", "soak-min", "crashes"])
        self.assertRegex(lines[1], r"^base\s+1\s+0\.0\s+30\.0\s+4\s+8\.0\s+-\s")
        self.assertRegex(lines[2], r"^nopatch\s+2\s+0\.0\s+60\.0\s+0\s+0\.0\s+0\.012\s")  # soak against soak: (1/3)^4
        self.assertRegex(lines[3], r"^scen\s+1\s+10\.0\s+0\.0\s+1\s+6\.0\s+-\s")  # scenario minutes: no P against a soak
        no_soak = sc.summarize_arms([arm_row("a", 1, {"sit": "PASS"}, {"sit": 60.0})])
        self.assertNotIn("soak-min", no_soak[0])
        self.assertFalse(any(line.startswith("P: ") for line in no_soak))  # one arm: nothing to compare

    def test_two_sided_rate_p(self) -> None:
        import arms_lib
        # Session D's 2026-10-18 figures: the old save 3 in 57.8 min against today's 8 in 75.1, and against 10-17's 0 in 108.7
        self.assertAlmostEqual(arms_lib.two_sided_rate_p(3, 57.8, 8, 75.1), 0.3685, places=3)
        self.assertAlmostEqual(arms_lib.two_sided_rate_p(3, 57.8, 0, 108.7), 0.0418, places=3)
        self.assertAlmostEqual(arms_lib.two_sided_rate_p(8, 75.1, 3, 57.8), arms_lib.two_sided_rate_p(3, 57.8, 8, 75.1))
        self.assertEqual(arms_lib.two_sided_rate_p(2, 10.0, 2, 10.0), 1.0)
        self.assertIsNone(arms_lib.two_sided_rate_p(0, 10.0, 0, 10.0))
        self.assertIsNone(arms_lib.two_sided_rate_p(1, 0.0, 1, 10.0))

    def test_catcher_verdict_within_noise(self) -> None:
        rows = [arm_row("bare", 1, {"shower": "PASS"}, {"shower": 100.0}),
                arm_row("catcher", 1, {"shower": "PASS"}, {"shower": 103.0}, rates=[2.0]),
                arm_row("catcher", 2, {"shower": "PASS"}, {"shower": 105.0}, rates=[4.0]),
                arm_row("bare", 2, {"shower": "PASS"}, {"shower": 110.0})]
        text = "\n".join(sc.catcher_verdict(rows, "20261019-000000"))
        self.assertIn("median -1.0%", text)  # 104 against 105
        self.assertIn("differ by a median 10.0%", text)
        self.assertIn("verdict: no difference beyond run-to-run noise", text)
        self.assertIn('CATCHER_MEASURED = "', text)
        self.assertIn("2.0/min, 4.0/min", text)

    def test_catcher_verdict_flags_a_flip(self) -> None:
        rows = [arm_row("bare", 1, {"shower": "PASS"}, {"shower": 100.0}),
                arm_row("catcher", 1, {"shower": "FAIL"}, {"shower": 100.0})]
        text = "\n".join(sc.catcher_verdict(rows, "x"))
        self.assertIn("verdicts only seen with the catcher: shower", text)
        self.assertIn("no noise floor", text)
        self.assertIn("verdict: the catcher changes verdicts or timings", text)
        self.assertNotIn("CATCHER_MEASURED =", text)
        self.assertIn("needs at least one", sc.catcher_verdict(rows[:1], "x")[0])

    @unittest.skipIf(crash_catch is None, 'internal crash_catch/gc_watch excluded in mirror')
    def test_arms_runs_interleaved_reboots_and_detaches(self) -> None:
        calls: list[tuple] = []
        healths = iter([None, None, "stack down: server no answer", None, None, None])  # before and after each run

        def fake_python(*args: str, timeout: int = 900) -> tuple[int, str]:
            calls.append(args)
            if args[:2] == ("scripts/scenarios.py", "run"):
                return 0, "report: x/20261018-1015%02d/report.md\n" % len(calls)
            return 0, "ok"

        with tempfile.TemporaryDirectory() as folder, \
                mock.patch.object(sc, "ARMS", Path(folder)), mock.patch.object(sc, "python", side_effect=fake_python), \
                mock.patch.object(sc, "health", side_effect=lambda: next(healths)), \
                mock.patch.object(sc, "crashes_since", return_value=[]), mock.patch.object(sc, "free_gb", return_value=50.0), \
                mock.patch.object(sc, "dumps_since", return_value=[]), \
                mock.patch.object(sc, "run_hook", return_value="exit 0") as hook, \
                mock.patch("builtins.print"):
            code = sc.arms(["shower"], [], 1, "scripts/x.py {arm}", "", True, True)
            out = next(Path(folder).iterdir())
            logged = [json.loads(line) for line in (out / "arms.jsonl").read_text(encoding="utf-8").splitlines()]
            summary = (out / "summary.txt").read_text(encoding="utf-8")
        self.assertEqual(code, 0)
        runs = [c for c in calls if c[:2] == ("scripts/scenarios.py", "run")]
        self.assertEqual(runs, [("scripts/scenarios.py", "run", "shower"), ("scripts/scenarios.py", "run", "shower", "--catcher")])
        self.assertIn(("scripts/quick_loop.py", "run", "--boot"), calls)  # the second arm found the server down
        self.assertEqual(calls[-1], ("scripts/crash_catch.py", "detach", "both"))  # after the catcher arm
        self.assertEqual([r["arm"] for r in logged], ["bare", "catcher"])
        self.assertEqual(hook.call_count, 2)
        self.assertIn("catcher A/B", summary)

    @unittest.skipIf(crash_catch is None, 'internal crash_catch/gc_watch excluded in mirror')
    def test_a_one_side_catcher_arm_is_detached_too(self) -> None:
        with tempfile.TemporaryDirectory() as folder, \
                mock.patch.object(sc, "ARMS", Path(folder)), mock.patch.object(sc, "python", return_value=(0, "")) as py, \
                mock.patch.object(sc, "health", return_value=None), mock.patch.object(sc, "crashes_since", return_value=[]), \
                mock.patch.object(sc, "free_gb", return_value=50.0), mock.patch.object(sc, "dumps_since", return_value=[]), \
                mock.patch("builtins.print"):
            sc.arms(["shower"], ["srv=--catcher-side server"], 1, "", "", False, False)
        calls = [c.args for c in py.call_args_list]
        self.assertIn(("scripts/scenarios.py", "run", "shower", "--catcher-side", "server"), calls)
        self.assertEqual(calls[-1], ("scripts/crash_catch.py", "detach", "both"))

    def test_arms_stops_when_the_stack_stays_down(self) -> None:
        with tempfile.TemporaryDirectory() as folder, \
                mock.patch.object(sc, "ARMS", Path(folder)), mock.patch.object(sc, "python", return_value=(0, "")) as py, \
                mock.patch.object(sc, "health", return_value="stack down: client"), mock.patch("builtins.print"):
            self.assertEqual(sc.arms([], ["a=", "b="], 2, "", "", False, False), 6)
            summary = (next(Path(folder).iterdir()) / "summary.txt").read_text(encoding="utf-8")
        py.assert_not_called()  # no reboot asked, no run started
        self.assertIn("(1 not run)", summary)
        with self.assertRaises(SystemExit):
            sc.arms([], [], 1, "", "", False, False)

    def run_with_events(self, python) -> tuple[str, list[str], object]:
        """One arm a=, run with this python stand-in and the console captured: (console, events.txt lines, the result
        or the exception)."""
        healths = iter([None, "stack down: server no answer (crash report xcpt 1.txt: x)"])
        crashes = iter([[], [], ["server xcpt 1.txt: x"]])
        console = io.StringIO()
        with tempfile.TemporaryDirectory() as folder, \
                mock.patch.object(sc, "ARMS", Path(folder)), mock.patch.object(sc, "python", side_effect=python), \
                mock.patch.object(sc, "health", side_effect=lambda: next(healths, None)), \
                mock.patch.object(sc, "crashes_since", side_effect=lambda since, sides=False: next(crashes, [])), \
                mock.patch.object(sc, "free_gb", return_value=50.0), mock.patch.object(sc, "dumps_since", return_value=[]), \
                contextlib.redirect_stdout(console):
            try:
                result: object = sc.arms(["sit"], ["a="], 1, "", "", False, False)
            except RuntimeError as ex:
                result = ex
            self.assertIs(sys.stdout, console)  # put back
            events = (next(Path(folder).iterdir()) / "events.txt").read_text(encoding="utf-8").splitlines()
        return console.getvalue(), events, result

    def test_arms_writes_its_events_to_the_run_folder(self) -> None:
        console, events, code = self.run_with_events(lambda *a, **k: (0, REPORT))
        self.assertTrue(all(re.match(r"\d\d:\d\d:\d\d ", e) for e in events), events)
        lines = [e[9:] for e in events]
        self.assertTrue(lines[0].startswith("arms: 1 run(s) of sit; log "))
        self.assertRegex(lines[1], r"^== \d\d:\d\d:\d\d a #1: run sit$")
        self.assertTrue(lines[2].startswith("   run 20261018-101500: ") and "1 crash(es) in the run (server xcpt 1.txt: x)"
                        in lines[2], lines[2])
        self.assertTrue(lines[-2].startswith("arms: ") and lines[-2].endswith(lines[0].split("log ")[1][:-len("arms.jsonl") - 1]))
        self.assertEqual(lines[-1], f"arms: exit {code}")
        for line in lines[:-1]:  # every event is on the console too, and only the event lines are in the file
            self.assertIn(line, console)
        self.assertFalse(any(line.startswith("   process: ") for line in lines))
        self.assertIn("   process: ", console)

    def test_arms_events_name_what_stopped_a_run(self) -> None:
        def python(*args, **kwargs):
            raise RuntimeError("controller gone")

        console, events, result = self.run_with_events(python)
        self.assertIsInstance(result, RuntimeError)
        self.assertRegex(events[-1], r"^\d\d:\d\d:\d\d arms: stopped by RuntimeError: controller gone$")
        self.assertTrue(any("== " in e and "a #1: run sit" in e for e in events))


    def test_catcher_helpers(self) -> None:
        self.assertTrue(sc.is_catcher(["--catcher-side", "server"]))
        self.assertFalse(sc.is_catcher(["--quick"]))
        self.assertEqual(sc.without_catcher(["--catcher-side", "client", "--quick", "--catcher"]), ["--quick"])
        dumps = ["server-1-20261018-083016-interp-av.dmp", "server-1-20261018-083016-unhandled.dmp",
                 "client-2-20261018-090000-interp-av.dmp", "odd.dmp"]
        self.assertEqual(sc.catches(dumps), 3)
        self.assertIsNone(sc.catcher_refusal(20.0, 1, 6.0, 2))
        self.assertIn("below 6 GB", sc.catcher_refusal(5.5, 0, 6.0, 2) or "")
        self.assertIn("the cap is 2", sc.catcher_refusal(20.0, 2, 6.0, 2) or "")
        self.assertGreater(sc.free_gb(), 0.0)

    def test_dumps_since_reads_only_new_dumps(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            old, new = Path(folder) / "a.dmp", Path(folder) / "b.dmp"
            old.write_bytes(b"x")
            new.write_bytes(b"x")
            (Path(folder) / "c.log").write_text("x", encoding="utf-8")
            os.utime(old, (1000.0, 1000.0))
            self.assertEqual(sc.dumps_since(2000.0, Path(folder)), ["b.dmp"])
        self.assertEqual(sc.dumps_since(0.0, Path(folder) / "gone"), [])

    @unittest.skipIf(crash_catch is None, 'internal crash_catch/gc_watch excluded in mirror')
    def test_crash_side_by_profile(self) -> None:
        real = Path("C:/Users/user/Documents/Electronic Arts/The Sims 3")
        self.assertEqual(sc.crash_side(real / "xcpt 1.txt", real), "server")
        self.assertEqual(sc.crash_side(Path("C:/Sandbox/user/Client2Box/drive/xcpt 1.txt"), real), "client")
        # the archived copy of a report TS3 deleted (crash_catch.ARCHIVE): its folder names the profile
        self.assertEqual(sc.crash_side(crash_catch.ARCHIVE / "server" / "xcpt 1.txt", real), "server")
        self.assertEqual(sc.crash_side(crash_catch.ARCHIVE / "Client2Box" / "xcpt 1.txt", real), "client")

    def test_a_capped_catcher_arm_is_skipped_or_runs_bare(self) -> None:
        dumps = ["server-1-20261018-083016-interp-av.dmp", "client-2-20261018-090000-interp-av.dmp"]
        for bare, expect_runs in ((False, [("scripts/scenarios.py", "run", "sit")]),
                                  (True, [("scripts/scenarios.py", "run", "sit")] * 2)):
            with tempfile.TemporaryDirectory() as folder, \
                    mock.patch.object(sc, "ARMS", Path(folder)), mock.patch.object(sc, "python", return_value=(0, REPORT)) as py, \
                    mock.patch.object(sc, "health", return_value=None), mock.patch.object(sc, "crashes_since", return_value=[]), \
                    mock.patch.object(sc, "free_gb", return_value=50.0), mock.patch.object(sc, "dumps_since", return_value=dumps), \
                    mock.patch("builtins.print"):
                code = sc.arms(["sit"], ["bare=", "catcher=--catcher"], 1, "", "", False, False, bare_when_capped=bare)
                logged = [json.loads(line) for line in (next(Path(folder).iterdir()) / "arms.jsonl").read_text(
                    encoding="utf-8").splitlines()]
            runs = [c.args for c in py.call_args_list if c.args[:2] == ("scripts/scenarios.py", "run")]
            self.assertEqual(runs, expect_runs)  # never with --catcher: two crashes are dumped already
            self.assertEqual(code, 0)  # a refused catcher arm is not a dead stack
            if bare:
                self.assertIn("the cap is 2", logged[1]["catcher_dropped"])
                self.assertEqual((logged[1]["arm"], logged[1]["args"]), ("catcher:bare", []))

    def test_a_bare_run_never_counts_as_the_catcher(self) -> None:
        rows = [arm_row("bare", 1, {"sit": "PASS"}, {"sit": 60.0}), arm_row("catcher", 1, {"sit": "PASS"}, {"sit": 90.0}),
                dict(arm_row("catcher", 2, {"sit": "FAIL"}, {"sit": 60.0}), catcher_dropped="cap")]
        lines = sc.catcher_verdict(rows, "x")
        self.assertIn("median +50.0%", " ".join(lines))  # the dropped row (60 s, FAIL) is not the catcher's
        self.assertIn("verdicts only seen with the catcher: none", " ".join(lines))
        self.assertIn("NAME:bare", "\n".join(sc.summarize_arms(rows)))

    def test_low_disk_refuses_a_catcher_arm(self) -> None:
        with tempfile.TemporaryDirectory() as folder, \
                mock.patch.object(sc, "ARMS", Path(folder)), mock.patch.object(sc, "python", return_value=(0, "")) as py, \
                mock.patch.object(sc, "health", return_value=None), mock.patch.object(sc, "crashes_since", return_value=[]), \
                mock.patch.object(sc, "free_gb", return_value=2.0), mock.patch.object(sc, "dumps_since", return_value=[]), \
                mock.patch("builtins.print") as out:
            sc.arms(["sit"], ["catcher=--catcher"], 1, "", "", False, False)
            summary = (next(Path(folder).iterdir()) / "summary.txt").read_text(encoding="utf-8")
        py.assert_not_called()
        self.assertIn("(1 not run)", summary)
        printed = " ".join(str(c.args[0]) for c in out.call_args_list if c.args)
        self.assertIn("attach only with 6 GB free", printed)  # the threshold is printed up front

    def test_crashes_are_put_in_their_phase(self) -> None:
        # Per arm the runner asks for crashes since: the last arm's end (the gap), the setup's start, the run's start
        # and the run's end (the after hook).
        answers = [["client xcpt 0.txt: before any arm"], [], [], ["server xcpt 2.txt: y"],  # arm a
                   ["client xcpt 3.txt: z"], ["server xcpt 4.txt: w"], [], []]  # arm b

        with tempfile.TemporaryDirectory() as folder, \
                mock.patch.object(sc, "ARMS", Path(folder)), mock.patch.object(sc, "python", return_value=(0, REPORT)), \
                mock.patch.object(sc, "health", return_value=None), \
                mock.patch.object(sc, "crashes_since", side_effect=lambda since, sides=False: answers.pop(0)), \
                mock.patch.object(sc, "free_gb", return_value=50.0), mock.patch.object(sc, "dumps_since", return_value=[]), \
                mock.patch.object(sc, "run_hook", return_value="exit 0"), mock.patch("builtins.print"):
            sc.arms(["sit"], ["a=", "b="], 1, "", "scripts/pull.py", False, False)
            out = next(Path(folder).iterdir())
            logged = [json.loads(line) for line in (out / "arms.jsonl").read_text(encoding="utf-8").splitlines()]
            summary = (out / "summary.txt").read_text(encoding="utf-8")
            import arms_lib
            pooled = arms_lib.load_arm_log(out / "arms.jsonl", "x")
        self.assertEqual([r.get("kind", r.get("arm")) for r in logged], ["gap", "a", "gap", "b"])
        self.assertIsNone(logged[0]["arm"])  # before the first arm: charged to no arm
        a, b = logged[1], logged[3]
        self.assertEqual((a["crashes_setup"], a["crashes"], a["crashes_after"]), ([], [], ["server xcpt 2.txt: y"]))
        self.assertEqual(a["died_in"], "after")
        self.assertEqual((logged[2]["arm"], logged[2]["crashes"]), ("a", ["client xcpt 3.txt: z"]))  # the gap: a's
        self.assertEqual((b["crashes_setup"], b["died_in"]), (["server xcpt 4.txt: w"], "setup"))
        self.assertEqual([c for c in pooled[0]["crashes"]], [("server", "after", "xcpt 2.txt"), ("client", "gap", "xcpt 3.txt")])
        self.assertEqual(pooled[1]["crashes"], [("server", "setup", "xcpt 4.txt")])
        self.assertIn("+2 crash(es) outside the runs", summary)  # a: the after hook and the gap


class ArmSets(unittest.TestCase):
    def test_split_expand_and_targets(self) -> None:
        sets, rest = sc.split_sets(["--set", "both:sacs.seed-copy=1", "--quick", "--set=server:sacs.gc-pressure=2"])
        self.assertEqual((sets, rest), (["both:sacs.seed-copy=1", "server:sacs.gc-pressure=2"], ["--quick"]))
        managed = sc.managed_tunables([["--set", "both:sacs.seed-copy=1"], ["--set", "server:sacs.gc-pressure=2"], []])
        self.assertEqual(managed, [(sc.SERVER, "sacs.seed-copy"), (sc.CLIENT, "sacs.seed-copy"), (sc.SERVER, "sacs.gc-pressure")])
        self.assertEqual(sc.arm_targets(["server:sacs.gc-pressure=2"], managed),
                         [(sc.SERVER, "sacs.seed-copy", "default"), (sc.CLIENT, "sacs.seed-copy", "default"),
                          (sc.SERVER, "sacs.gc-pressure", "2")])
        self.assertEqual(sc.expand_sets(["x=1"]), [(sc.CLIENT, "x", "1")])  # client by default, as --tune
        for bad in (["--set"], ["--set", ""]):
            with self.assertRaises(SystemExit):
                sc.split_sets(bad)
        for bad in (["both:x"], ["sacs.force=1176358403"]):
            with self.assertRaises(SystemExit):
                sc.expand_sets(bad)
        self.assertTrue(sc.tune_ok("[client] a=1 (default 0)", "a", "1"))
        self.assertTrue(sc.tune_ok("[client] b=0", "a", "default"))  # an unread name drops out of the list
        self.assertFalse(sc.tune_ok("[server] error=not answering (refused)", "a", "default"))
        self.assertFalse(sc.tune_ok("[client] b=0", "a", "1"))

    def test_a_set_the_mod_never_read_is_recorded_not_fatal(self) -> None:
        # Tunables.Report: "tunables=N" then one name=value row each, "(default ?)" when set but never read
        unread = "[server] tunables=2\nsacs.event-recorder=1 (default ?)\nmirror.settle=0.5"
        read = "[server] tunables=1\nsacs.event-recorder=1 (default 0)"
        self.assertTrue(sc.tune_unread(unread, "sacs.event-recorder", "1"))
        self.assertFalse(sc.tune_unread(read, "sacs.event-recorder", "1"))
        self.assertTrue(sc.tune_unread("[server] tunables=1\nmirror.settle=0.5", "sacs.event-recorder", "default"))
        self.assertFalse(sc.tune_unread("[server] tunables=1\nsacs.event-recorder=0", "sacs.event-recorder", "default"))
        self.assertFalse(sc.tune_unread("[server] tunables=1\nsacs.event-recorder.x=1 (default ?)", "sacs.event-recorder", "1"))
        replies = {"sacs.event-recorder": unread, "mirror.settle": "[client] tunables=1\nmirror.settle=0.7 (default 0.5)",
                   "gone": "[server] error=not answering (refused)"}
        with mock.patch.object(sc, "tune", side_effect=lambda endpoint, name, value: replies[name]):
            problems, unread_sets = sc.set_tunables([(sc.SERVER, "sacs.event-recorder", "1"),
                                                     (sc.CLIENT, "mirror.settle", "0.7")])
            self.assertEqual((problems, unread_sets), ([], ["server sacs.event-recorder=1"]))
            problems, unread_sets = sc.set_tunables([(sc.SERVER, "gone", "1")])
            self.assertEqual((len(problems), unread_sets), (1, []))  # an error still stops the arm

    def test_no_repair_leaves_the_resets_repair_out(self) -> None:
        calls = []

        def fake_python(script: str, *args: str, **kwargs) -> tuple[int, str]:
            calls.append(list(args))
            return 0, "ok"

        with mock.patch.object(sc, "python", fake_python), mock.patch.object(sc.time, "sleep"), \
                mock.patch.object(sc, "HOME", Path(tempfile.gettempdir()) / "no-such-home.json"):
            sc.reset()
            self.assertIn("--repair", calls[0])
            calls.clear()
            with mock.patch.object(sc, "REPAIR", False):
                sc.reset()
        self.assertNotIn("--repair", calls[0])
        self.assertIn("--fire", calls[0])  # the rest of the first Sim's reset is unchanged

    def test_release_resets_each_game_on_its_own(self) -> None:
        def fake_tune(endpoint: str, name: str, value: str | None = None) -> str:
            if endpoint == sc.SERVER:
                return "[server] error=not answering (connect to bridgectl listener 127.0.0.1:34029 failed)"
            return f"[client] {name}=0 (default 0)"

        managed = [(sc.SERVER, "a"), (sc.SERVER, "b"), (sc.CLIENT, "a"), (sc.CLIENT, "b")]
        with mock.patch.object(sc, "tune", side_effect=fake_tune) as calls:
            lines = sc.release_tunables(managed)
        self.assertEqual([c.args for c in calls.call_args_list],
                         [(sc.SERVER, "a", "default"), (sc.CLIENT, "a", "default"), (sc.CLIENT, "b", "default")])
        self.assertTrue(lines[0].startswith("server: 0 of 2 back to default; not reset: a, b ([server] error=not answering"))
        self.assertEqual(lines[1], "client: 2 of 2 back to default")

    def test_hold_tunes_sets_only_what_a_restart_lost(self) -> None:
        listed = {sc.SERVER: "[server] a=1 (default 0)\nb=2", sc.CLIENT: "[client] b=0 (default 0)"}  # a unread on the client

        def fake_python(*args: str, timeout: int = 900) -> tuple[int, str]:
            self.assertEqual(args[:2], ("scripts/tune.py", "--side"))
            return 0, listed[sc.SERVER if args[2] == "server" else sc.CLIENT]

        def fake_tune(endpoint: str, name: str, value: str | None = None) -> str:
            listed[endpoint] += f"\n{name}={value} (default 0)"
            return f"[x] {name}={value} (default 0)"

        with mock.patch.object(sc, "python", side_effect=fake_python) as reads, \
                mock.patch.object(sc, "tune", side_effect=fake_tune) as calls:
            checks, problems, notes = sc.hold_tunes(["both:a=1"])
            self.assertEqual(sc.lost_tunes(checks), [])
        self.assertEqual(reads.call_count, 4)  # one list read per game at the start, one at the end
        self.assertEqual([c.args for c in calls.call_args_list], [(sc.CLIENT, "a", "1")])  # the server had it already
        self.assertEqual(problems, [])
        self.assertEqual(notes, ["held a=1 was unset on the client: set again (a restart?)"])
        self.assertEqual(checks, [(sc.SERVER, "a", "held", "1"), (sc.CLIENT, "a", "held", "1")])
        self.assertIn("not [server:|client:|both:]name=value", sc.hold_tunes(["x"])[1][0])

    def test_holds_on_an_unchanged_process_make_no_mod_call(self) -> None:
        then = {"server": {"pid": 1, "started": 10.0}, "client": {"pid": 2, "started": 20.0}}
        restarted = {"server": {"pid": 1, "started": 10.0}, "client": {"pid": 7, "started": 90.0}}
        boxed = {"server": {"pid": 1, "started": 10.0}, "client": {"pid": 2, "started": None}}  # start not readable
        tunes = lambda e, n, v=None: f"[x] {n}={v} (default 0)"  # noqa: E731
        listed = lambda *args, timeout=900: (0, "a=1 (default 0)")  # noqa: E731
        # unchanged: no list read, no set, at the start or the end of a scenario
        known = dict(then)
        with mock.patch.object(sc, "game_identity", return_value=then), \
                mock.patch.object(sc, "python", side_effect=listed) as reads, mock.patch.object(sc, "tune", side_effect=tunes) as sets:
            checks, problems, notes = sc.hold_tunes(["both:a=1"], known)
            self.assertEqual(sc.lost_held(checks, known), [])
        self.assertEqual((reads.call_count, sets.call_count, problems, notes), (0, 0, [], []))
        self.assertEqual({c[2] for c in checks}, {"held-process"})
        # the client restarted since: set again there, without a read; the new process is known from then on
        with mock.patch.object(sc, "game_identity", return_value=restarted), \
                mock.patch.object(sc, "python", side_effect=listed) as reads, mock.patch.object(sc, "tune", side_effect=tunes) as sets:
            checks, problems, notes = sc.hold_tunes(["both:a=1"], known)
        self.assertEqual(reads.call_count, 0)
        self.assertEqual([c.args for c in sets.call_args_list], [(sc.CLIENT, "a", "1")])
        self.assertIn("the client's TS3 is another process (pid 2 -> 7): set again", notes[0])
        self.assertEqual(known["client"], {"pid": 7, "started": 90.0})
        # a restart during the scenario: lost, with no mod call
        with mock.patch.object(sc, "game_identity", return_value=then):
            self.assertIn("its TS3 changed during the scenario (pid 7 -> 2)", sc.lost_held(checks, known)[0])
        # a process that cannot be told: read, as without known
        with mock.patch.object(sc, "game_identity", return_value=boxed), \
                mock.patch.object(sc, "python", side_effect=listed) as reads, mock.patch.object(sc, "tune", side_effect=tunes):
            checks, _, _ = sc.hold_tunes(["both:a=1"], dict(then))
        self.assertEqual(reads.call_count, 1)  # the client's list only
        self.assertEqual([c[2] for c in checks], ["held-process", "held"])

    def test_the_set_check_fallback_is_recorded_and_warned(self) -> None:
        by_process = [(sc.SERVER, "a", "held-process", "1"), (sc.CLIENT, "a", "held-process", "1")]
        read = [(sc.SERVER, "a", "held-process", "1"), (sc.CLIENT, "a", "held", "1"), (sc.CLIENT, "b", "held-process", "1")]
        self.assertEqual(sc.set_check_of(by_process), {"server": "process", "client": "process"})
        self.assertEqual(sc.set_check_of(read), {"server": "process", "client": "tunables"})
        with tempfile.TemporaryDirectory() as folder:
            run = Path(folder) / "20261019-100000"
            run.mkdir()
            records = [{"name": "sit-dining", "verdict": "PASS", "timings": {"total": 60}, "set_check": sc.set_check_of(read)},
                       {"name": "shower", "verdict": "PASS", "timings": {"total": 60}, "set_check": sc.set_check_of(by_process)}]
            (run / "records.json").write_text(json.dumps(records), encoding="utf-8")
            text = f"report: {run / 'report.md'}"
            row = sc.arm_record("copy", 1, [], 0.0, 60.0, text, [], runs_dir=Path(folder))
            self.assertEqual((row["set_check"], row["set_check_tunables"]), ("tunables", {"client": 1}))
            (run / "records.json").write_text(json.dumps(records[1:]), encoding="utf-8")
            row = sc.arm_record("copy", 1, [], 0.0, 60.0, text, [], runs_dir=Path(folder))
            self.assertEqual(row["set_check"], "process")
            self.assertNotIn("set_check_tunables", row)
        # the runner says so in one line when an arm's row has it
        row = {"arm": "copy", "repeat": 1, "args": [], "run": "r", "verdicts": {}, "scenario_min": 1.0, "crashes": [],
               "set_check": "tunables", "set_check_tunables": {"client": 2}}
        with tempfile.TemporaryDirectory() as folder, \
                mock.patch.object(sc, "ARMS", Path(folder)), mock.patch.object(sc, "python", return_value=(0, REPORT)), \
                mock.patch.object(sc, "tune", side_effect=lambda e, n, v=None: f"[x] {n}={v}"), \
                mock.patch.object(sc, "health", return_value=None), mock.patch.object(sc, "crashes_since", return_value=[]), \
                mock.patch.object(sc, "free_gb", return_value=50.0), mock.patch.object(sc, "dumps_since", return_value=[]), \
                mock.patch.object(sc, "arm_record", side_effect=lambda *a, **k: dict(row)), \
                mock.patch.object(sc, "game_identity", return_value={}), mock.patch("builtins.print") as out:
            sc.arms(["sit"], ["copy=--set both:a=1"], 1, "", "", False, False)
        warnings = [str(c.args[0]) for c in out.call_args_list if c.args and "warning: the --set check" in str(c.args[0])]
        self.assertEqual(len(warnings), 1)
        self.assertIn("on the client in 2 scenario(s)", warnings[0])

    def test_arms_passes_the_process_its_sets_went_to(self) -> None:
        one = {"server": {"pid": 1, "started": 10.0}, "client": {"pid": 2, "started": 20.0}}
        _, events, _ = self.run_arms(["copy=--set both:sacs.seed-copy=1"], identities=[one, one])
        run = next(e for e in events if e[:3] == ("python", "scripts/scenarios.py", "run"))
        self.assertEqual(run[-2:], ("--held-process", json.dumps(one)))

    def test_a_watchdog_hang_is_not_a_death_in_the_run(self) -> None:
        """Main, 2026-10-22: a hang reads died_in "hang", even with the stack found down after it."""
        made = {"row": False}
        real = sc.arm_record

        def hung(*args, **kw) -> dict:
            made["row"] = True
            return {**real(*args, **kw), "hangs": {"sit": "watchdog: client 1 bridge: no answer"}}

        with mock.patch.object(sc, "arm_record", side_effect=hung):
            _, _, rows = self.run_arms(["off="], health=lambda: "stack down: client no answer" if made["row"] else None)
        self.assertEqual(next(r for r in rows if r.get("arm") == "off")["died_in"], "hang")

    def run_arms(self, specs: list[str], tune_reply=None, order: str = "", identities=None, reboot: bool = False,
                 health=None, **arms_kw) -> tuple[int, list[tuple], list[dict]]:
        events: list[tuple] = []
        identity = (lambda: identities.pop(0)) if identities is not None else (lambda: {})

        def fake_tune(endpoint: str, name: str, value: str | None = None) -> str:
            events.append(("tune", sc.side_of(endpoint), name, value))
            return tune_reply(name, value) if tune_reply else f"[x] {name}={value}"

        def fake_python(*args: str, timeout: int = 900) -> tuple[int, str]:
            events.append(("python", *args))
            return 0, REPORT

        self.hook_env: list[tuple[str, dict | None]] = []

        def fake_hook(command: str, arm: str, repeat: int, keep: Path | None = None, **kw: str) -> str:
            events.append(("hook", command, arm))
            self.hook_env.append((command, kw.get("env_extra")))
            return "exit 0"

        with tempfile.TemporaryDirectory() as folder, \
                mock.patch.object(sc, "ARMS", Path(folder)), mock.patch.object(sc, "python", side_effect=fake_python), \
                mock.patch.object(sc, "tune", side_effect=fake_tune), mock.patch.object(sc, "health", side_effect=health or (lambda: None)), \
                mock.patch.object(sc, "crashes_since", return_value=[]), mock.patch.object(sc, "free_gb", return_value=50.0), \
                mock.patch.object(sc, "dumps_since", return_value=[]), mock.patch.object(sc, "run_hook", side_effect=fake_hook), \
                mock.patch.object(sc, "game_identity", side_effect=identity), mock.patch("builtins.print"):
            code = sc.arms(["sit"], specs, 1, "mark", "pull", reboot, False, order_text=order, **arms_kw)
            log = next(Path(folder).iterdir()) / "arms.jsonl"
            rows = [json.loads(line) for line in log.read_text(encoding="utf-8").splitlines()] if log.exists() else []
            self.plan, self.state = (json.loads((log.parent / name).read_text(encoding="utf-8"))
                                     for name in ("plan.json", "state.json"))
        return code, events, rows

    def test_arm_sets_are_held_through_the_arm_and_released_at_the_end(self) -> None:
        code, events, rows = self.run_arms(["copy=--set both:sacs.seed-copy=1", "base="])
        self.assertEqual(code, 0)
        self.assertEqual(events, [
            ("tune", "server", "sacs.seed-copy", "1"), ("tune", "client", "sacs.seed-copy", "1"), ("hook", "mark", "copy"),
            ("python", "scripts/scenarios.py", "run", "sit", "--held", "both:sacs.seed-copy=1"), ("hook", "pull", "copy"),
            ("tune", "server", "sacs.seed-copy", "default"), ("tune", "client", "sacs.seed-copy", "default"),
            ("hook", "mark", "base"), ("python", "scripts/scenarios.py", "run", "sit"), ("hook", "pull", "base"),
            ("tune", "server", "sacs.seed-copy", "default"), ("tune", "client", "sacs.seed-copy", "default")])  # the end
        self.assertEqual([r["sets"] for r in rows], [["server:sacs.seed-copy=1", "client:sacs.seed-copy=1"], []])

    def test_arms_clients_reaches_every_boot_the_runner_makes_and_the_plan(self) -> None:
        # 2026-10-21, the session probe: a planned reboot booted one client, and two-hud needs player 2's game
        code, events, _ = self.run_arms(["off=", "on="], order="off reboot on", clients=2)
        self.assertEqual(code, 0)
        boots = [e[1:] for e in events if e[0] == "python" and e[1] == "scripts/quick_loop.py"]
        self.assertEqual(boots, [("scripts/quick_loop.py", "run", "--boot", "--clients", "2")])
        self.assertEqual(self.plan["clients"], 2)
        self.assertEqual(sc.boot_clients_args(None), [])

    def test_check_warns_when_a_two_player_scenario_would_boot_one_client(self) -> None:
        specs = [{"name": "sit"}, {"name": "two-hud", "clients": [1, 2]}]
        with mock.patch.object(sc, "load", return_value=specs), mock.patch.object(sc, "CLIENT", "127.0.0.1:34030"):
            self.assertIn("two-hud watch client 2", sc.client2_warning(["sit", "two-hud"], None) or "")
            self.assertIsNone(sc.client2_warning(["sit", "two-hud"], 2))
        with mock.patch.object(sc, "load", return_value=specs[:1]):
            self.assertIsNone(sc.client2_warning(["sit"], None))

    def blocked_runs(self, blocked: Callable[[str, int], bool], **arms_kw) -> tuple[int, list[tuple], list[dict]]:
        """run_arms with each arm's verdicts all BLOCKED (no exposure) when blocked(arm, call) says so."""
        calls: dict[str, int] = {}

        def fake_record(name, repeat, args, started, ended, text, crashes, runs_dir=None):
            calls[name] = calls.get(name, 0) + 1
            if blocked(name, calls[name]):
                return arm_row(name, repeat, {"sit": "BLOCKED", "chat": "BLOCKED"}, {"sit": 0.0, "chat": 0.0})
            return arm_row(name, repeat, {"sit": "PASS", "chat": "PASS"}, {"sit": 60.0, "chat": 60.0})

        with mock.patch.object(sc, "arm_record", side_effect=fake_record):
            return self.run_arms(["off=", "on="], order="off on", **arms_kw)

    def test_rerun_blocked_runs_a_blocked_arm_again_once_behind_a_reboot(self) -> None:
        code, events, rows = self.blocked_runs(lambda arm, call: arm == "on" and call == 1, rerun_blocked=True)
        self.assertEqual(code, 0)
        runs = [e[1:] if e[1] == "scripts/quick_loop.py" else e[3] for e in events if e[0] == "python"]
        self.assertEqual(runs, ["sit", "sit", ("scripts/quick_loop.py", "run", "--boot"), "sit"])
        self.assertEqual([(r["arm"], r["repeat"], sc.all_blocked(r)) for r in rows],
                         [("off", 1, False), ("on", 1, True), ("on", 1, False)])
        self.assertEqual(self.plan["order"], [[1, "off", []], [1, "on", []], [0, "reboot", []], [1, "on", []]])
        self.assertEqual((self.plan["rerun_blocked"], self.state["reran"], self.state["finished"]), (True, [["on", 1]], True))
        line = next(line for line in sc.summarize_arms(rows) if line.startswith("on "))
        self.assertRegex(line, r"^on\s+1\s+2\.0 .*  \(1 not run \(blocked\)\)$")  # the blocked one is no run
        # once: blocked again, it is not run a third time; and off by default
        _, _, rows = self.blocked_runs(lambda arm, call: arm == "on", rerun_blocked=True)
        self.assertEqual([r["arm"] for r in rows], ["off", "on", "on"])
        _, _, rows = self.blocked_runs(lambda arm, call: arm == "on")
        self.assertEqual([r["arm"] for r in rows], ["off", "on"])
        self.assertNotIn("reran", [k for k, v in self.state.items() if v])

    GATE = ["base=", "gate=--server-env SIMLINK_GCGATE=1"]

    def test_the_boot_env_reaches_each_reboot_and_each_row(self) -> None:
        code, events, rows = self.run_arms(self.GATE, order="base reboot gate base reboot", client_env=["SIMSONLINE_X=1"])
        self.assertEqual(code, 0)
        self.assertEqual([e[2:] for e in events if e[:2] == ("python", "scripts/quick_loop.py")], [
            ("run", "--boot", "--server-env", "SIMLINK_GCGATE=1", "--client-env", "SIMSONLINE_X=1"),  # the next arm's
            ("run", "--boot", "--client-env", "SIMSONLINE_X=1")])  # no arm after it: the run's own
        runs = [e for e in events if e[:3] == ("python", "scripts/scenarios.py", "run")]
        self.assertEqual(len(runs), 3)
        self.assertFalse(any("--server-env" in e or "--client-env" in e for e in runs))  # the boots' env, not the run's
        gate_env = {"server": {"SIMLINK_GCGATE": "1"}, "client": {"SIMSONLINE_X": "1"}}
        base_env = {"server": {}, "client": {"SIMSONLINE_X": "1"}}
        self.assertEqual([(r["arm"], r["boot_env"], r["booted_env"], r.get("boot_env_off")) for r in rows], [
            ("base", base_env, {"server": None, "client": None}, ["client"]),  # the stack as found: unknown
            ("gate", gate_env, gate_env, None),
            ("base", base_env, gate_env, ["server"])])  # no reboot between: still on gate's env
        self.assertEqual((self.plan["server_env"], self.plan["client_env"]["*"]),
                         ({"*": {}, "base": {}, "gate": {"SIMLINK_GCGATE": "1"}}, {"SIMSONLINE_X": "1"}))
        self.assertEqual(self.state["booted"], {"by": "planned reboot", "server": {}, "client": {"SIMSONLINE_X": "1"},
                                                "identity": None})  # the last reboot: no arm read its processes

    def test_a_reboot_when_dead_boots_the_arms_own_env(self) -> None:
        problems = ["server not answering"]
        code, events, rows = self.run_arms(self.GATE, order="gate", reboot=True,
                                           health=lambda: problems.pop(0) if problems else None)
        self.assertEqual(code, 0)
        self.assertEqual([e[2:] for e in events if e[:2] == ("python", "scripts/quick_loop.py")],
                         [("run", "--boot", "--server-env", "SIMLINK_GCGATE=1")])
        self.assertEqual((rows[0]["booted_env"], rows[0].get("boot_env_off")),
                         ({"server": {"SIMLINK_GCGATE": "1"}, "client": {}}, None))
        self.assertEqual(self.state["booted"]["by"], "reboot-when-dead")

    def test_a_run_with_no_boot_env_keeps_its_rows_and_reboots_as_they_were(self) -> None:
        _, events, rows = self.run_arms(["base="], order="reboot base")
        self.assertIn(("python", "scripts/quick_loop.py", "run", "--boot"), events)
        self.assertNotIn("boot_env", rows[0])
        self.assertEqual((self.plan["server_env"], self.plan["client_env"]), ({"*": {}, "base": {}}, {"*": {}, "base": {}}))

    @unittest.skipIf(crash_catch is None, 'internal crash_catch/gc_watch excluded in mirror')
    def test_the_after_hook_is_told_its_arms_run(self) -> None:
        # gc_watch.py frames --arm reads the run's window and the arms run's folder from these
        self.run_arms(["base="])
        (before, none), (after, env) = self.hook_env
        self.assertEqual((before, none, after, sorted(env)), ("mark", None, "pull", sorted(
            ["SIMSONLINE_ARM_START", "SIMSONLINE_ARM_END", "SIMSONLINE_ARMS_FOLDER"])))
        self.assertLessEqual(float(env["SIMSONLINE_ARM_START"]), float(env["SIMSONLINE_ARM_END"]))
        self.assertRegex(Path(env["SIMSONLINE_ARMS_FOLDER"]).name, r"^\d{8}-\d{6}")

    def test_a_set_that_does_not_take_stops_the_run_and_still_releases(self) -> None:
        code, events, rows = self.run_arms(["copy=--set both:sacs.seed-copy=1"],
                                           tune_reply=lambda name, value: "error=not a number" if value == "1" else f"{name}=0")
        self.assertEqual(code, 7)
        self.assertFalse(any(e[0] == "python" for e in events))  # no run on the defaults
        self.assertEqual(events[-2:], [("tune", "server", "sacs.seed-copy", "default"), ("tune", "client", "sacs.seed-copy", "default")])
        self.assertEqual(rows, [])  # a skipped arm is in the summary, not the log


class ArmStatusTest(unittest.TestCase):
    def run_status_arms(self, status: bool, every: float = 0.0, run_seconds: float = 0.0, dead_at_end: bool = False,
                        gc_gate: str = ""):
        import arms_lib_test

        events: list[tuple] = []
        resent = {"n": 0}

        def fake_read(endpoint: str, timeout: float = 10.0) -> str | None:
            side = sc.side_of(endpoint)
            events.append(("status", side))
            if dead_at_end and side == "client" and resent.get("ran"):
                return None
            return (arms_lib_test.status_text(spare_resent=resent["n"] if side == "server" else 0)
                    + (";" + gc_gate if gc_gate else ""))

        def fake_tune(endpoint: str, name: str, value: str | None = None) -> str:
            events.append(("tune", sc.side_of(endpoint), name, value))
            if value == "1" and sc.side_of(endpoint) == "server":
                resent["n"] = 25  # the server's spare-only resend: it goes out while the sets take and settle
            return f"[x] {name}={value}"

        def fake_python(*args: str, timeout: int = 900) -> tuple[int, str]:
            events.append(("python", *args[:2]))
            time.sleep(run_seconds)
            resent["ran"] = True
            return 0, REPORT

        with tempfile.TemporaryDirectory() as folder, \
                mock.patch.object(sc, "ARMS", Path(folder)), mock.patch.object(sc, "python", side_effect=fake_python), \
                mock.patch.object(sc, "tune", side_effect=fake_tune), mock.patch.object(sc, "health", return_value=None), \
                mock.patch.object(sc, "crashes_since", return_value=[]), mock.patch.object(sc, "free_gb", return_value=50.0), \
                mock.patch.object(sc, "dumps_since", return_value=[]), \
                mock.patch.object(sc, "run_hook", side_effect=lambda c, a, r, keep=None, **kw: events.append(("hook", c)) or "exit 0"), \
                mock.patch.object(sc, "game_identity", return_value={}), mock.patch.object(sc, "read_status", side_effect=fake_read), \
                mock.patch("builtins.print"):
            code = sc.arms(["sit"], ["copy=--set server:sacs.seed-copy=1"], 1, "mark", "pull", False, False,
                           status=status, status_every=every)
            out = next(Path(folder).iterdir())
            rows = [json.loads(line) for line in (out / "arms.jsonl").read_text(encoding="utf-8").splitlines()]
            samples = ([json.loads(line) for line in (out / "status.jsonl").read_text(encoding="utf-8").splitlines()]
                       if (out / "status.jsonl").exists() else [])
        return code, events, rows, samples

    def test_off_by_default_no_status_calls(self) -> None:
        code, events, rows, samples = self.run_status_arms(status=False)
        self.assertEqual(code, 0)
        self.assertNotIn("status", [e[0] for e in events])
        self.assertNotIn("status", rows[0])
        self.assertEqual(samples, [])

    def test_boundary_readings_start_before_the_sets(self) -> None:
        import arms_lib

        _, events, rows, samples = self.run_status_arms(status=True)
        kinds = [e[0] if e[0] != "hook" else e[1] for e in events]
        self.assertEqual(kinds[:4], ["status", "status", "tune", "mark"])  # start: both games, then the sets
        self.assertEqual(kinds[4:], ["python", "status", "status", "pull", "status", "status", "tune"])  # end, after
        self.assertEqual([s["kind"] for s in samples], ["start", "end", "after"])
        record = rows[0]["status"]
        self.assertEqual((record["samples"], record["every"]), (0, 0.0))
        views = arms_lib.status_views(record)
        self.assertEqual(arms_lib.status_value(views, "delta.server.spareResent"), 25)  # the resend in setup counts

    def test_the_gc_gate_counters_go_into_the_row(self) -> None:
        import arms_lib_test

        _, _, rows, _ = self.run_status_arms(status=True, gc_gate=arms_lib_test.GC_GATE_ON)
        self.assertEqual(rows[0]["gc_gate"]["server"]["pauseMaxMs"], 31.5)
        self.assertEqual(rows[0]["gc_gate"]["client"]["from"], "end")
        _, _, rows, _ = self.run_status_arms(status=True)
        self.assertNotIn("gc_gate", rows[0])  # a build without the gate: no field
        on = dict(arm_row("on", 1, {"sit": "PASS"}, {"sit": 60.0}),
                  gc_gate={"server": {"bit": 1, "pauseMeanMs": 1.5, "pauseMaxMs": 31.5}})
        on2 = dict(on, repeat=2, gc_gate={"server": {"bit": 1, "pauseMeanMs": 0.5, "pauseMaxMs": 12}})
        off = dict(arm_row("off", 1, {"sit": "PASS"}, {"sit": 60.0}),
                   gc_gate={"server": {"bit": 0, "pauseMeanMs": 0, "pauseMaxMs": 0}})
        line = next(line for line in sc.summarize_arms([off, on, on2]) if line.startswith("GC gate pauses"))
        self.assertEqual(line, "GC gate pauses (the server's end reading per run, counted from its process start): "
                               "off 1 run(s), bit 0, pause mean 0.00 ms (0..0), max 0 ms; "
                               "on 2 run(s), bit 1, pause mean 1.00 ms (0.5..1.5), max 31.5 ms")

    def test_timer_samples_and_a_dead_game_keeps_its_last_reading(self) -> None:
        import arms_lib

        _, _, rows, samples = self.run_status_arms(status=False, every=0.02, run_seconds=0.3, dead_at_end=True)
        record = rows[0]["status"]
        self.assertGreater(record["samples"], 2)  # --status-every implies the boundary readings too
        self.assertEqual([s["kind"] for s in samples if s["kind"] != "timer"], ["start", "end", "after"])
        self.assertIsNone(record["end"]["client"])
        self.assertGreater(record["misses"]["client"], 0)
        self.assertLessEqual(record["last"]["client"]["t"], next(s["t"] for s in samples if s["kind"] == "end"))
        views = arms_lib.status_views(record)
        self.assertEqual(arms_lib.status_value(views, "last.client.gcCount"), 39)
        with self.assertRaises(arms_lib.MissingName):
            arms_lib.status_value(views, "delta.client.gcCount")

    def test_read_status_and_the_timer_survive_bridge_exits(self) -> None:
        import bridgectl

        with mock.patch.object(bridgectl, "connect", side_effect=SystemExit("connect failed")):
            self.assertIsNone(sc.read_status(sc.SERVER))
        with tempfile.TemporaryDirectory() as folder:
            calls = {"n": 0}

            def flaky(endpoint: str, timeout: float = 10.0) -> str:
                calls["n"] += 1
                if calls["n"] <= 2:
                    raise RuntimeError("parse trouble")
                return "seedCopy=on,reads=1"

            with mock.patch.object(sc, "read_status", side_effect=flaky):
                status = sc.ArmStatus(Path(folder) / "status.jsonl", "a", 1, every=0.01).start_timer()
                deadline = time.time() + 5
                while status.samples < 2 and time.time() < deadline:
                    time.sleep(0.01)
                status.stop()
            self.assertGreaterEqual(status.samples, 2)  # the thread lived through the exception
            self.assertFalse(status._thread.is_alive())


class GatesTest(unittest.TestCase):
    def run_gated(self, resent: int, borrowed_spare: int, order: str, crash_on_call: int = 0):
        import arms_lib_test

        events: list[tuple] = []
        state = {"resent": 0, "crash_calls": 0}

        def fake_read(endpoint: str, timeout: float = 10.0) -> str:
            if sc.side_of(endpoint) == "server":
                return arms_lib_test.status_text(spare_resent=state["resent"])
            resends = state["resent"] // resent if resent else 0  # the copy takes 2 spares per resend
            return arms_lib_test.status_text(spare_only=2 * resends, borrowed=2 * resends)

        def fake_tune(endpoint: str, name: str, value: str | None = None) -> str:
            events.append(("tune", sc.side_of(endpoint), name, value))
            if value == "1" and sc.side_of(endpoint) == "server":
                state["resent"] += resent
            return f"[x] {name}={value}"

        def fake_python(*args: str, timeout: int = 900) -> tuple[int, str]:
            events.append(("run",))
            return 0, REPORT

        def fake_hook(command: str, arm: str, repeat: int, keep: Path | None = None, **kw: str) -> str:
            if keep is not None and command == "pull":
                keep.write_text(arms_lib_test.T3A1_PULL.replace("borrowedSpare:2", f"borrowedSpare:{borrowed_spare}"),
                                encoding="utf-8")
            return "exit 0"

        def fake_crashes(since: float, sides: bool = False) -> list[str]:
            state["crash_calls"] += 1
            return ["server xcpt A.txt: x"] if state["crash_calls"] == crash_on_call else []

        gate = 'once copy: delta.server.spareResent > 0 and delta.client.spareOnly > 0 and hook.borrowedSpare > 0'
        with tempfile.TemporaryDirectory() as folder, \
                mock.patch.object(sc, "ARMS", Path(folder)), mock.patch.object(sc, "python", side_effect=fake_python), \
                mock.patch.object(sc, "tune", side_effect=fake_tune), mock.patch.object(sc, "health", return_value=None), \
                mock.patch.object(sc, "crashes_since", side_effect=fake_crashes), \
                mock.patch.object(sc, "free_gb", return_value=50.0), mock.patch.object(sc, "dumps_since", return_value=[]), \
                mock.patch.object(sc, "run_hook", side_effect=fake_hook), mock.patch.object(sc, "game_identity", return_value={}), \
                mock.patch.object(sc, "read_status", side_effect=fake_read), mock.patch("builtins.print"):
            code = sc.arms(["sit"], ["copy=--set server:sacs.seed-copy=1", "base="], 1, "mark", "pull", False, False,
                           order_text=order, gate_texts=[gate])
            out = next(Path(folder).iterdir())
            rows = [json.loads(line) for line in (out / "arms.jsonl").read_text(encoding="utf-8").splitlines()]
            summary = (out / "summary.txt").read_text(encoding="utf-8")
            kept = sorted(p.name for p in out.glob("after-*.txt"))
        return code, events, [r for r in rows if "kind" not in r], summary, kept

    def test_a_gate_that_holds_lets_the_run_go_on(self) -> None:
        code, events, rows, summary, kept = self.run_gated(25, 2, "copy base copy")
        self.assertEqual(code, 0)
        self.assertEqual([r["arm"] for r in rows], ["copy", "base", "copy"])
        self.assertEqual(rows[0]["gates"][0]["result"], "passed")
        self.assertNotIn("gates", rows[2])  # once: done after its first evaluation
        self.assertEqual(kept, ["after-base-1.txt", "after-copy-1.txt", "after-copy-2.txt"])
        self.assertIn("passed after copy #1", summary)

    def test_a_gate_that_fails_stops_the_run_and_releases_the_tunables(self) -> None:
        code, events, rows, summary, _ = self.run_gated(0, 0, "copy base")
        self.assertEqual(code, 8)
        self.assertEqual([r["arm"] for r in rows], ["copy"])
        self.assertEqual(rows[0]["gates"][0]["values"]["delta.server.spareResent"], 0)
        self.assertEqual(events.count(("run",)), 1)
        self.assertEqual(events[-1], ("tune", "server", "sacs.seed-copy", "default"))  # released at the end
        self.assertIn("failed after copy #1", summary)

    def test_a_dead_arm_is_not_evaluated_and_the_gate_waits(self) -> None:
        # crashes_since calls: the gap, setup and run checks of the first arm: its run crashes
        code, _, rows, summary, _ = self.run_gated(25, 2, "copy copy", crash_on_call=3)
        self.assertEqual(code, 0)
        self.assertEqual(rows[0]["died_in"], "run")
        self.assertEqual(rows[0]["gates"][0]["result"], "not evaluated")
        self.assertEqual(rows[1]["gates"][0]["result"], "passed")
        self.assertIn("passed after copy #2; not evaluated after copy #1 (died)", summary)

    def test_a_gate_on_an_unknown_arm_or_a_bad_name_fails_before_any_game(self) -> None:
        with mock.patch.object(sc, "tune") as tune:
            with self.assertRaises(SystemExit):
                sc.arms(["sit"], ["copy="], 1, "", "", False, False, gate_texts=["cpy: delta.server.x > 0"])
            with self.assertRaises(SystemExit):
                sc.arms(["sit"], ["copy="], 1, "", "", False, False, gate_texts=["copy: delta.copy.x > 0"])
        tune.assert_not_called()


class BootHookTest(unittest.TestCase):
    """arms --boot-hook: the arm's own boot (saves.py arm) after the last arm's gap and before its sets and process
    snapshot, in place of the reboot-when-dead."""
    IDENTITY = {"server": {"pid": 1, "started": 10.0}, "client": {"pid": 2, "started": 20.0}}
    SPECS = ["copy=--set server:sacs.seed-copy=1", "base="]
    HOOK = "scripts/saves.py arm {arm} {repeat} --base b"

    def setUp(self) -> None:
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.arms_dir = Path(self.folder.name)
        self.events: list[tuple] = []
        self.boot_codes: dict[str, int] = {}  # arm -> the boot hook's exit code (0 when not named)
        self.boot_crash: list[str] = []  # crashes the next crashes_since call after a boot reports
        self.boot_envs: list = []  # the boot env each boot hook call was given
        self.stop_on_run = 0
        self.runs = 0

    def call(self, *args, **kwargs) -> int:
        def fake_python(*argv: str, timeout: int = 900) -> tuple[int, str]:
            self.events.append(("python", *argv[:3]))
            if argv[:2] == ("scripts/scenarios.py", "run"):
                self.runs += 1
                if self.runs == self.stop_on_run:
                    self.assertEqual(sc.stop_arms("latest"), 0)
            return 0, REPORT

        def fake_boot(command: str, arm: str, repeat: int, keep=None, down: str = "none", kind: str = "",
                      boot_env=None) -> tuple:
            self.events.append(("boot", arm, repeat, kind))
            self.boot_envs.append(boot_env)
            self.boot_crash_pending = list(self.boot_crash)
            code = self.boot_codes.get(arm, 0)
            return code, f"exit {code}: saves: {arm} #{repeat}: " + ("booted" if code == 0 else "refused")

        def fake_crashes(since: float, sides: bool = False) -> list[str]:
            pending, self.boot_crash_pending = getattr(self, "boot_crash_pending", []), []
            return pending

        with mock.patch.object(sc, "ARMS", self.arms_dir), mock.patch.object(sc, "python", side_effect=fake_python), \
                mock.patch.object(sc, "tune", side_effect=lambda e, n, v=None: self.events.append(("tune", n, v)) or f"[x] {n}={v}"), \
                mock.patch.object(sc, "health", return_value=None), mock.patch.object(sc, "crashes_since", side_effect=fake_crashes), \
                mock.patch.object(sc, "free_gb", return_value=50.0), mock.patch.object(sc, "dumps_since", return_value=[]), \
                mock.patch.object(sc, "run_hook", return_value="exit 0"), mock.patch.object(sc, "hook_result", side_effect=fake_boot), \
                mock.patch.object(sc, "game_identity", side_effect=lambda: self.events.append(("identity",)) or self.IDENTITY), \
                mock.patch("builtins.print"):
            return sc.main(list(args)) if args and isinstance(args[0], str) and not kwargs else sc.arms(*args, **kwargs)

    def out(self) -> Path:
        return next(self.arms_dir.iterdir())

    def logged(self) -> list[dict]:
        return [json.loads(line) for line in (self.out() / "arms.jsonl").read_text(encoding="utf-8").splitlines()]

    def test_each_arm_boots_before_its_sets_and_snapshot(self) -> None:
        code = self.call(["sit"], self.SPECS, 1, "", "", True, False, boot=self.HOOK)
        self.assertEqual(code, 0)
        steps = [e[0] if e[0] != "tune" or e[2] is None else "set" for e in self.events
                 if e[0] in ("boot", "identity", "tune") or e[:3] == ("python", "scripts/scenarios.py", "run")]
        # per arm: the boot, the snapshot, the set, the run (the snapshot after the run notes a restart)
        self.assertEqual(steps[:5], ["boot", "identity", "set", "python", "identity"])
        self.assertEqual([e for e in self.events if e[0] == "boot"], [("boot", "copy", 1, "boot"), ("boot", "base", 1, "boot")])
        self.assertNotIn(("python", "scripts/quick_loop.py", "run"), self.events)  # no reboot of its own
        copy, base = self.logged()
        self.assertEqual((copy["boot_hook"], copy["crashes_boot"]), ("exit 0: saves: copy #1: booted", []))
        self.assertEqual(copy["sets"], ["server:sacs.seed-copy=1"])  # the set survives: it comes after the boot
        self.assertEqual(base["process"]["server"]["arms_before"], [])  # a fresh process: the boot cleared the history
        self.assertEqual(json.loads((self.out() / "plan.json").read_text(encoding="utf-8"))["boot"], self.HOOK)

    def test_a_failed_boot_stops_the_run(self) -> None:
        self.boot_codes["base"] = 1
        code = self.call(["sit"], self.SPECS, 1, "", "", True, False, boot=self.HOOK)
        self.assertEqual(code, 6)
        rows = self.logged()
        self.assertEqual(len(rows), 1)  # the skipped arm is in the summary, not the log
        summary = (self.out() / "summary.txt").read_text(encoding="utf-8")
        self.assertIn("(1 not run)", summary)
        self.assertEqual(self.runs, 1)

    def test_boot_crashes_are_the_arms_own(self) -> None:
        self.boot_crash = ["server xcpt B.txt: known interp-virtual-method"]
        self.call(["sit"], ["base="], 1, "", "", True, False, boot=self.HOOK)
        row = self.logged()[0]
        self.assertEqual((row["crashes_boot"], row["crashes"], row["died_in"]),
                         (["server xcpt B.txt: known interp-virtual-method"], [], None))
        summary = (self.out() / "summary.txt").read_text(encoding="utf-8")
        self.assertIn("+1 crash(es) outside the runs: the boot hook, setup", summary)
        import arms_lib
        self.assertEqual(arms_lib.load_arm_log(self.out() / "arms.jsonl", "x")[0]["crashes"],
                         [("server", "boot", "xcpt B.txt")])

    def test_resume_keeps_the_boot_hook(self) -> None:
        self.stop_on_run = 1
        self.assertEqual(self.call(["sit"], self.SPECS, 1, "", "", True, False, boot=self.HOOK), 9)
        self.stop_on_run = 0
        self.assertEqual(self.call("arms", "--resume", "latest"), 0)
        self.assertEqual([e[1] for e in self.events if e[0] == "boot"], ["copy", "base"])

    def test_a_plan_from_before_the_boot_hook_resumes_without_one(self) -> None:
        self.stop_on_run = 1
        self.call(["sit"], self.SPECS, 1, "", "", True, False)
        plan = json.loads((self.out() / "plan.json").read_text(encoding="utf-8"))
        del plan["boot"]
        (self.out() / "plan.json").write_text(json.dumps(plan), encoding="utf-8")
        self.stop_on_run = 0
        self.assertEqual(self.call("arms", "--resume", "latest"), 0)
        self.assertEqual([e for e in self.events if e[0] == "boot"], [])

    def test_refusals_before_any_game(self) -> None:
        with self.assertRaises(SystemExit) as refused:
            self.call(["sit"], self.SPECS, 1, "", "", True, False, order_text="copy reboot base", boot=self.HOOK)
        self.assertIn("drop 'reboot'", str(refused.exception))
        self.assertEqual(list(self.arms_dir.iterdir()), [])
        with self.assertRaises(SystemExit):
            self.call("arms", "--resume", "latest", "--boot-hook", self.HOOK)
        self.assertEqual(self.events, [])

    def test_the_boot_hook_gets_the_arms_boot_env(self) -> None:
        specs = ["base=", "gate=--server-env SIMLINK_GCGATE=1"]
        with self.assertRaises(SystemExit) as refused:  # a hook with no {server_env} would boot gate without it
            self.call(["sit"], specs, 1, "", "", True, False, boot=self.HOOK)
        self.assertIn("names no {server_env}", str(refused.exception))
        self.assertEqual(list(self.arms_dir.iterdir()), [])
        hook = "scripts/quick_loop.py run --boot {server_env}"
        self.assertEqual(self.call(["sit"], specs, 1, "", "", True, False, boot=hook), 0)
        self.assertEqual(self.boot_envs, [{"server": {}, "client": {}}, {"server": {"SIMLINK_GCGATE": "1"}, "client": {}}])
        gate = next(r for r in self.logged() if r["arm"] == "gate")
        self.assertEqual((gate["booted_env"], gate.get("boot_env_off")), (gate["boot_env"], None))
        state = json.loads((self.out() / "state.json").read_text(encoding="utf-8"))
        self.assertEqual((state["booted"]["by"], state["booted"]["identity"]), ("boot hook", self.IDENTITY))

    def test_hook_result_names_the_slot(self) -> None:
        seen: dict = {}

        def fake_run(argv, **kw):
            seen.update(kw)
            return sc.subprocess.CompletedProcess(argv, 3, stdout="first\nsaves: refused\n", stderr="")

        with mock.patch.object(sc.subprocess, "run", side_effect=fake_run):
            self.assertEqual(sc.hook_result("scripts/saves.py arm {arm} 1", "a", 1, kind="boot"), (3, "exit 3: saves: refused"))
            self.assertEqual(seen["env"]["SIMSONLINE_ARMS_HOOK"], "boot")
            sc.run_hook("x.py", "a", 1)
            self.assertIsNone(seen["env"])  # no slot named: the hook gets the runner's own environment
            sc.run_hook("x.py", "a", 1, kind="after", env_extra={"SIMSONLINE_ARM_START": "1.000"})
            self.assertEqual((seen["env"]["SIMSONLINE_ARMS_HOOK"], seen["env"]["SIMSONLINE_ARM_START"]), ("after", "1.000"))


class WaitTest(unittest.TestCase):
    """arms --wait: one call per arm for a monitor, from the folder only."""

    ROW = {"arm": "off", "repeat": 1, "run": "20261020-010101", "started": "2026-10-20 01:01:01", "scenario_min": 4.2,
           "verdicts": {"sit": "PASS", "chat": "BLOCKED"}, "crashes": [], "died_in": None,
           "gates": [{"result": "passed", "values": {"hook.marksAcrossFrames": 0}}]}

    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.folder = Path(self.tmp.name) / "20261020-010000"
        self.folder.mkdir()
        sc.write_json(self.folder / "plan.json", {"order": [[1, "off", []], [0, sc.REBOOT, []], [1, "on", []]]})
        sc.write_json(self.folder / "state.json", {"next": 0, "finished": False})
        sc.write_json(self.folder / "runner.json", {"pid": 4242, "started": 1.0})
        self.log = self.folder / "arms.jsonl"

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def add(self, row: dict) -> None:
        with open(self.log, "a", encoding="utf-8") as out:
            out.write(json.dumps(row) + "\n")

    def wait(self, seen: int | None = None, on_sleep=None, alive: bool = True) -> tuple[int, list[str], int]:
        lines: list[str] = []
        sleeps = []

        def sleep(_: float) -> None:
            sleeps.append(1)
            if on_sleep:
                on_sleep(len(sleeps))

        with mock.patch("builtins.print", side_effect=lambda *a, **k: lines.append(" ".join(map(str, a)))):
            code = sc.arms_wait(str(self.folder), seen, sleep=sleep, alive=lambda lock: alive and (self.folder / "runner.json").exists())
        return code, lines, len(sleeps)

    def test_the_next_arm_row(self) -> None:
        self.add({"kind": "boot", "arm": "x"})  # not an arm row
        code, lines, sleeps = self.wait(on_sleep=lambda n: n == 2 and self.add(self.ROW))
        self.assertEqual((code, sleeps), (0, 2))
        self.assertEqual(lines, ["off#1 (row 1 of 2) 2026-10-20 01:01:01: run 20261020-010101: PB, 4.2 scenario-min, "
                                 "0 crash(es); gate passed (hook.marksAcrossFrames=0)"])
        code, lines, _ = self.wait(seen=0)  # a monitor that re-arms with the rows it saw misses none
        self.assertEqual((code, lines[0][:20]), (0, "off#1 (row 1 of 2) 2"))

    def test_the_run_ends(self) -> None:
        self.add(self.ROW)
        # the last row lands as the runner goes: printed first (0); the next call sees the end (9)
        (self.folder / "runner.json").unlink()
        sc.write_json(self.folder / "state.json", {"next": 3, "finished": True})
        code, lines, _ = self.wait(seen=0)
        self.assertEqual(code, 0)
        code, lines, sleeps = self.wait(seen=1)
        self.assertEqual((code, sleeps), (9, 0))
        self.assertEqual(lines, ["20261020-010000: the run has ended: finished; 1 arm row(s); scenarios.py arms "
                                 "--summary 20261020-010000"])

    def test_a_killed_or_stopped_runner(self) -> None:
        code, lines, _ = self.wait(alive=False)
        self.assertEqual(code, 9)
        self.assertIn("stopped before item 0 of 3 (--resume goes on); runner pid 4242 no longer runs and left "
                      "runner.json (killed?)", lines[0])

    @unittest.skipIf(crash_catch is None, 'internal crash_catch/gc_watch excluded in mirror')
    def test_the_result_line(self) -> None:
        row = {**self.ROW, "crashes": ["server xcpt.txt 01:05:00"], "died_in": "run", "gates": []}
        self.assertEqual(sc.arm_result_line(row, 7), "off#1 (row 7) 2026-10-20 01:01:01: run 20261020-010101: PB, "
                                                     "4.2 scenario-min, 1 crash(es) (server xcpt.txt 01:05:00), died in run")
        row = {**self.ROW, "gates": [], "hook": {"violations": 0, "gcRateInBand": 1}}  # gc_watch.py frames, kept
        self.assertTrue(sc.arm_result_line(row, 1).endswith("0 crash(es); hook violations=0 gcRateInBand=1"))
        row["gc_gate"] = {"server": {"from": "end", "gcGate": "on", "bit": 1, "admitted": 120, "pauseMaxMs": 31.5}}
        self.assertTrue(sc.arm_result_line(row, 1).endswith(
            "; hook violations=0 gcRateInBand=1; gcGate server on: bit=1 admitted=120 pauseMaxMs=31.5"))
        row = {**self.ROW, "verdicts": {"sit": "PASS", "chat": "CRASHED"}, "gates": [], "died_in": "hang",
               "hangs": {"chat": "watchdog: client 1 bridge: no answer"}}
        line = sc.arm_result_line(row, 1)
        self.assertIn(": PH, ", line)
        self.assertTrue(line.endswith("0 crash(es), 1 hang(s), died in hang"), line)

    @unittest.skipIf(crash_catch is None, 'internal crash_catch/gc_watch excluded in mirror')
    def test_the_runner_names_the_stack_reader_for_frames(self) -> None:
        import gc_watch

        entry = {"pid": 4242, "by": "boot", "sides": "server", "every": 0.05, "claim": "D", "rows": "gc-watch.jsonl"}
        with mock.patch.object(gc_watch, "reader_now", return_value=entry):
            self.assertTrue(sc.gc_reader_line().startswith("gc watch: reader pid 4242 (boot, server, every 0.05 s, "
                                                           "claim D); rows gc-watch.jsonl; the after hook's frames"))
        with mock.patch.object(gc_watch, "reader_now", return_value=None):
            self.assertTrue(sc.gc_reader_line().startswith("gc watch: no reader running; a planned reboot"))

    def test_the_after_hooks_values_are_kept(self) -> None:
        hook = self.folder / "after-on-1.txt"
        hook.write_text("server: 4.5 min watched\nviolations=0 marksSeen=12 gcPerMin=10.40 gcRateInBand=1\n",
                        encoding="utf-8")
        self.assertEqual(sc.after_values(hook), {"violations": 0, "marksSeen": 12, "gcPerMin": 10.4, "gcRateInBand": 1})
        self.assertEqual(sc.after_values(self.folder / "none.txt"), {})


# Series B, the replication (arms run 20261019-142515, each arm in a fresh process): each arm row in log order with its
# scenario minutes, x for a server crash in the run. Session D's evaluator: off 2 in 63.87 min against on 0 in 64.5,
# "go on".
SERIES_B = ("off 4.37, on 4.53, on 4.32, off 2.53x, off 4.34, on 4.4, on 0.0, off 3.54, off 4.42, on 4.42, on 4.6, "
            "off 3.71, off 4.39, on 4.29, on 4.2, off 4.32, off 4.44, on 3.68, on 4.4, off 4.74, off 4.83, on 4.58, "
            "on 3.66, off 4.48, off 4.35, on 4.34, on 4.37, off 4.54, off 1.29x, on 4.33, on 4.38, off 3.58")
D_STOP = ["base.server.crashes >= 5 or base.min >= 150", "p(base, gate) <= 0.05"]


def series_b() -> list[dict]:
    rows, repeats = [], {}
    for i, item in enumerate(SERIES_B.split(", ")):
        arm, minutes = item.split()
        repeats[arm] = repeats.get(arm, 0) + 1
        crashed = minutes.endswith("x")
        rows.append({"arm": arm, "repeat": repeats[arm], "run": f"b{i}", "scenario_min": float(minutes.rstrip("x")),
                     "verdicts": {"sit": "CRASHED" if crashed else "PASS"},
                     "crashes": [f"server xcpt HOST B{i}.txt: known interp-indirect-load"] if crashed else []})
    return rows


def stop_rows() -> list[dict]:
    """A synthetic arms log for each stop term: base and gate in turn, 5 min each; base's first row has a client crash
    in the run and a server crash in its after hook, every later base row a server crash in the run; a gap crash
    after gate #2 (logged at base #3's start, so it lands on gate #2 at that boundary)."""
    rows: list[dict] = []
    for i in range(1, 12):
        arm = "base" if i % 2 else "gate"
        row = {"arm": arm, "repeat": (i + 1) // 2, "run": f"s{i}", "scenario_min": 5.0, "verdicts": {"sit": "PASS"},
               "crashes": [], "crashes_after": []}
        if i == 1:
            row["crashes"], row["crashes_after"] = ["client xcpt C1.txt: x"], ["server xcpt A1.txt: x"]
        elif arm == "base":
            row["crashes"] = [f"server xcpt S{i}.txt: x"]
        if i == 5:
            rows.append({"kind": "gap", "arm": "gate", "repeat": 2, "crashes": ["server xcpt G4.txt: x"]})
        rows.append(row)
    return rows


class StopWhen(unittest.TestCase):
    def stops(self, *texts: str) -> list[dict]:
        import arms_lib

        return [arms_lib.parse_stop(text, {"base", "gate"}) for text in texts]

    def test_the_replication_does_not_fire(self) -> None:
        import arms_lib

        records = [dict(r, arm={"off": "base", "on": "gate"}[r["arm"]]) for r in series_b()]
        number, check, total = arms_lib.stop_replay(self.stops(*D_STOP, "gate.min < 0"), records)
        self.assertEqual((number, total), (None, 32))
        self.assertEqual(check, {"fired": None, "error": None, "values": {
            "base.server.crashes": 2, "base.min": 63.87, "p(base, gate)": 0.2476, "gate.min": 64.5}})
        with tempfile.TemporaryDirectory() as folder:  # the CLI, on that folder, its arms read as the plan's
            (Path(folder) / "arms.jsonl").write_text("".join(json.dumps(r) + "\n" for r in series_b()), encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()) as out:
                code = sc.arms_stop_check(folder, D_STOP, "off=base,on=gate")
        self.assertEqual(code, 0)
        self.assertEqual(out.getvalue().splitlines()[1], "   does not fire in 32 row(s); at the end: goes on "
                                                         "(base.server.crashes=2 base.min=63.87 p(base, gate)=0.2476)")

    def test_stop_check_leaves_excluded_rows_and_their_gaps_out(self) -> None:
        rows = stop_rows()
        rows[3]["gates"] = [{"gate": "x", "result": "failed"}]  # gate #2, the one the gap after it is charged to
        with tempfile.TemporaryDirectory() as folder:
            (Path(folder) / "arms.jsonl").write_text("".join(json.dumps(r) + "\n" for r in rows), encoding="utf-8")
            for kw in ({"exclude_texts": ["gate#2"]}, {"exclude_invalid": True}):
                with contextlib.redirect_stdout(io.StringIO()) as out:
                    code = sc.arms_stop_check(folder, ["gate.crashes.gap >= 1"], **kw)
                self.assertEqual(code, 0)  # without gate #2's gap crash it never fires
                self.assertEqual(out.getvalue().splitlines()[1:], [
                    "   excluded (with their gap records): gate#2: 5 scenario-min, 1 crash(es) (1 in its gap); gate failed",
                    "   does not fire in 10 row(s); at the end: goes on (gate.crashes.gap=0)",
                    "   with every row: row 5 of 11 (base#3): FIRED: gate.crashes.gap >= 1 (gate.crashes.gap=1); the "
                    "runner would have stopped there, 6 row(s) before the end",
                    "   gate: 20.00 scenario-min, 4 run(s), 0 run crash(es), 0.00 per hour; with every row: 25.00 "
                    "scenario-min, 5 run(s), 0 run crash(es), 0.00 per hour"])
                self.assertTrue(out.getvalue().startswith(f"stop-check {Path(folder).name}: 11 arm row(s), 1 excluded; "))
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(sc.arms_stop_check(folder, ["gate.crashes.gap >= 1"]), 9)  # no exclusion: as before
            with self.assertRaises(SystemExit) as refused:
                sc.arms_stop_check(folder, ["gate.crashes.gap >= 1"], exclude_texts=["gate#9"])
            self.assertIn("no such arm row", str(refused.exception))
        with self.assertRaises(SystemExit):  # only with --stop-check or --summary
            sc.main(["arms", "--exclude", "gate#1"])

    def test_each_term_fires_where_it_should(self) -> None:
        import arms_lib

        expect = {
            "base.client.crashes >= 1": 1,  # the client's run crash
            "base.crashes.any >= 3": 3,  # every side and phase: the client's, the after hook's, base #2's
            "base.crashes >= 3": 5,  # the run only, both sides: the after hook's does not count
            "base.server.crashes >= 3": 7,  # the server only: the client's does not count
            "gate.crashes.gap >= 1": 5,  # the gap after gate #2 is logged at base #3's start
            "gate.crashes.any >= 1": 5,
            "base.min >= 25": 9,
            "base.runs >= 6": 11,
            "p(base, gate, server) <= 0.05": 11,  # (6/11)^5 = 0.0483; row 9's (5/9)^4 = 0.0953 goes on
        }
        for text, row in expect.items():
            number, check, _ = arms_lib.stop_replay(self.stops(text), stop_rows())
            self.assertEqual((number, check["fired"]), (row, text), (text, check))
        _, check, _ = arms_lib.stop_replay(self.stops("gate.crashes >= 1"), stop_rows())
        self.assertIsNone(check["fired"])  # a gap crash is no run crash
        values = arms_lib.stop_check(self.stops("p(base, gate, server) <= 0.05"), stop_rows()[:10])["values"]
        self.assertEqual(values, {"p(base, gate, server)": 0.0953})
        # every phase: base's after-hook crash and gate's gap crash count too, 6 against 1, and it goes on
        _, check, _ = arms_lib.stop_replay(self.stops("p('base', 'gate', 'server', 'any') <= 0.05"), stop_rows())
        self.assertEqual((check["fired"], check["values"]), (None, {"p('base', 'gate', 'server', 'any')": 0.0982}))
        broken = arms_lib.stop_check(self.stops("p(base, gate) < 'x'"), stop_rows())  # fails closed: the run stops
        self.assertTrue(broken["error"].startswith("p(base, gate) < 'x': cannot compare"), broken)

    def test_names_are_checked_before_any_game(self) -> None:
        for text, why in (("base.mins > 1", "not a stop name"), ("other.min > 1", "no arm 'other'"),
                          ("base or gate.min > 1", "only an argument to p()"), ("p(base) < 1", "the one call is p("),
                          ("p(base, gate, sideways) < 1", "'sideways' is not one of"), ("on('x')", "the one call")):
            with self.assertRaises(SystemExit) as caught:
                self.stops(text)
            self.assertIn(why, str(caught.exception), text)

    def test_the_runner_stops_at_the_boundary_and_resume_replaces_the_stop(self) -> None:
        def fake_record(name, repeat, args, started, ended, text, crashes, runs_dir=None):
            row = arm_row(name, repeat, {"sit": "PASS"}, {"sit": 300.0})
            row["crashes"] = [f"server xcpt {name}{repeat}.txt: x"] if name == "base" else []
            return row

        with tempfile.TemporaryDirectory() as folder, \
                mock.patch.object(sc, "ARMS", Path(folder)), mock.patch.object(sc, "python", return_value=(0, REPORT)), \
                mock.patch.object(sc, "tune", return_value="[x]"), mock.patch.object(sc, "health", return_value=None), \
                mock.patch.object(sc, "crashes_since", return_value=[]), mock.patch.object(sc, "free_gb", return_value=50.0), \
                mock.patch.object(sc, "dumps_since", return_value=[]), mock.patch.object(sc, "run_hook", return_value="exit 0"), \
                mock.patch.object(sc, "game_identity", return_value={}), mock.patch.object(sc, "arm_record", side_effect=fake_record), \
                mock.patch("builtins.print"):
            code = sc.arms(["sit"], ["base=", "gate="], 1, "", "", False, False, order_text="base gate base gate base",
                           stop_texts=["base.server.crashes >= 2"])
            out = next(Path(folder).iterdir())
            rows = sc.logged_arms(out)
            state = json.loads((out / "state.json").read_text(encoding="utf-8"))
            plan = json.loads((out / "plan.json").read_text(encoding="utf-8"))
            summary = (out / "summary.txt").read_text(encoding="utf-8")
            end = sc.run_end(out, alive=lambda lock: False)
            line = sc.arm_result_line(rows[-1], 3)
            again = sc.arms([], [], 0, "", "", True, False, resume_from=out, stop_texts=["base.server.crashes >= 9"])
            rows_after = sc.logged_arms(out)
            state_after = json.loads((out / "state.json").read_text(encoding="utf-8"))
            plan_after = json.loads((out / "plan.json").read_text(encoding="utf-8"))
        self.assertEqual(code, 9)
        self.assertEqual([(r["arm"], r["repeat"]) for r in rows], [("base", 1), ("gate", 1), ("base", 2)])
        self.assertEqual(rows[0]["stop_check"], {"fired": None, "error": None, "values": {"base.server.crashes": 1}})
        self.assertEqual(plan["stop"], ["base.server.crashes >= 2"])
        self.assertEqual((state["next"], state["finished"]), (3, False))
        self.assertEqual({k: state["stopped_by"][k] for k in ("text", "after", "row", "last", "values")},
                         {"text": "base.server.crashes >= 2", "after": ["base", 2], "row": 3, "last": False,
                          "values": {"base.server.crashes": 2}})
        reason = "--stop-when held after base #2 (row 3): base.server.crashes >= 2 (base.server.crashes=2)"
        self.assertEqual(summary.splitlines()[-1], reason)
        self.assertEqual(end, "stopped before item 3 of 5 (--resume goes on); " + reason)
        self.assertTrue(line.endswith("; stop-when FIRED: base.server.crashes >= 2 (base.server.crashes=2)"), line)
        # --resume with --stop-when: the new stop replaces the plan's, and the run goes on to the end
        self.assertEqual(again, 0)
        self.assertEqual(len(rows_after), 5)
        self.assertEqual(rows_after[-1]["stop_check"]["values"], {"base.server.crashes": 3})
        self.assertEqual((plan_after["stop"], state_after["finished"], state_after["stopped_by"]),
                         (["base.server.crashes >= 9"], True, None))


class ResumeTest(unittest.TestCase):
    IDENTITY = {"server": {"pid": 1, "started": 10.0}, "client": {"pid": 2, "started": 20.0}}

    def setUp(self) -> None:
        self.folder = tempfile.TemporaryDirectory()
        self.arms_dir = Path(self.folder.name)
        self.events: list[tuple] = []
        self.stop_on_run = 0
        self.gap_crash = False
        self.runs = 0

    def tearDown(self) -> None:
        self.folder.cleanup()

    def call(self, *args, **kwargs) -> int:
        def fake_python(*argv: str, timeout: int = 900) -> tuple[int, str]:
            self.events.append(("python", *argv[:4]))
            if argv[:2] == ("scripts/scenarios.py", "run"):
                self.runs += 1
                if self.runs == self.stop_on_run:  # someone runs arms --stop during this arm
                    self.assertEqual(sc.stop_arms("latest"), 0)
            return 0, REPORT

        def fake_crashes(since: float, sides: bool = False) -> list[str]:
            if self.gap_crash:
                self.gap_crash = False
                return ["server xcpt G.txt: x"]
            return []

        with mock.patch.object(sc, "ARMS", self.arms_dir), mock.patch.object(sc, "python", side_effect=fake_python), \
                mock.patch.object(sc, "tune", side_effect=lambda e, n, v=None: f"[x] {n}={v}"), \
                mock.patch.object(sc, "health", return_value=None), mock.patch.object(sc, "crashes_since", side_effect=fake_crashes), \
                mock.patch.object(sc, "free_gb", return_value=50.0), mock.patch.object(sc, "dumps_since", return_value=[]), \
                mock.patch.object(sc, "run_hook", return_value="exit 0"), \
                mock.patch.object(sc, "game_identity", return_value=self.IDENTITY), mock.patch("builtins.print"):
            return sc.main(list(args)) if args and isinstance(args[0], str) and not kwargs else sc.arms(*args, **kwargs)

    def logged(self) -> list[dict]:
        out = next(self.arms_dir.iterdir())
        return [json.loads(line) for line in (out / "arms.jsonl").read_text(encoding="utf-8").splitlines()]

    def state(self) -> dict:
        return json.loads((next(self.arms_dir.iterdir()) / "state.json").read_text(encoding="utf-8"))

    def start_and_stop(self) -> None:
        self.stop_on_run = 1
        code = self.call(["sit"], ["copy=--set server:sacs.seed-copy=1", "base="], 1, "", "", False, False,
                         order_text="copy base copy")
        self.assertEqual(code, 9)  # stopped as asked: not 0 (finished) nor 8 (a gate)
        self.assertEqual([r["arm"] for r in self.logged()], ["copy"])  # stopped before base
        out = next(self.arms_dir.iterdir())
        self.assertFalse((out / "runner.json").exists())
        self.assertEqual(self.state()["next"], 1)
        self.assertEqual(self.state()["previous"], ["copy", 1])
        self.stop_on_run = 0

    def test_stop_then_resume_carries_the_process_history(self) -> None:
        self.start_and_stop()
        self.gap_crash = True  # a crash between the two runners: the last arm's gap
        self.assertEqual(self.call("arms", "--resume", "latest"), 0)
        rows = self.logged()
        self.assertEqual([(r.get("kind"), r["arm"], r.get("repeat")) for r in rows],
                         [(None, "copy", 1), ("gap", "copy", 1), (None, "base", 1), (None, "copy", 2)])
        base = rows[2]
        self.assertEqual(base["process"]["server"], {"pid": 1, "arms_before": ["copy#1"], "had": ["sacs.seed-copy"]})
        self.assertTrue(self.state()["finished"])
        summary = (next(self.arms_dir.iterdir()) / "summary.txt").read_text(encoding="utf-8")
        self.assertRegex(summary, r"copy\s+2\s")  # the summary covers both runners' arms
        with self.assertRaises(SystemExit):  # every arm has run
            self.call("arms", "--resume", "latest")

    def test_resume_with_a_new_order_counts_repeats_on(self) -> None:
        self.start_and_stop()
        self.assertEqual(self.call("arms", "--resume", "latest", "--order", "base reboot copy"), 0)
        self.assertEqual([(r["arm"], r["repeat"]) for r in self.logged()], [("copy", 1), ("base", 1), ("copy", 2)])
        self.assertIn(("python", "scripts/quick_loop.py", "run", "--boot"), self.events)
        copy2 = self.logged()[2]
        self.assertEqual(copy2["process"]["server"]["arms_before"], [])  # the planned reboot reset it
        plan = json.loads((next(self.arms_dir.iterdir()) / "plan.json").read_text(encoding="utf-8"))
        self.assertEqual([n for _, n, _ in plan["order"]], ["copy", "base", "reboot", "copy"])

    def test_resume_keeps_the_boot_env(self) -> None:
        self.stop_on_run = 1
        self.assertEqual(self.call(["sit"], ["copy=", "base="], 1, "", "", False, False, order_text="copy reboot base",
                                   server_env=["SIMLINK_GCGATE=1"]), 9)
        self.stop_on_run = 0
        with self.assertRaises(SystemExit) as refused:  # the plan has it
            self.call("arms", "--resume", "latest", "--server-env", "A=1")
        self.assertIn("boot env", str(refused.exception))
        self.assertEqual(self.call("arms", "--resume", "latest"), 0)
        self.assertIn(("python", "scripts/quick_loop.py", "run", "--boot", "--server-env"), self.events)
        base = self.logged()[-1]
        self.assertEqual((base["arm"], base["booted_env"]), ("base", {"server": {"SIMLINK_GCGATE": "1"}, "client": {}}))

    def test_resume_is_refused_while_the_runner_runs(self) -> None:
        self.start_and_stop()
        out = next(self.arms_dir.iterdir())
        sc.write_json(out / "runner.json", sc.runner_identity(os.getpid()))  # this process: alive
        with self.assertRaises(SystemExit) as caught:
            self.call("arms", "--resume", out.name)
        self.assertIn("still runs it", str(caught.exception))
        sc.write_json(out / "runner.json", {"pid": os.getpid(), "started": 1.0})  # a reused pid: a dead runner
        self.assertFalse(sc.runner_alive(json.loads((out / "runner.json").read_text(encoding="utf-8"))))
        with self.assertRaises(SystemExit):
            self.call("arms", "--resume", out.name, "--arm", "x=")  # the plan has the arms

    def test_stop_with_no_runner_says_so(self) -> None:
        self.start_and_stop()
        with mock.patch.object(sc, "ARMS", self.arms_dir), mock.patch("builtins.print"):
            self.assertEqual(sc.stop_arms("latest"), 1)
            self.assertFalse((next(self.arms_dir.iterdir()) / "stop").exists())  # no flag for a later resume to trip on
            with self.assertRaises(SystemExit):
                sc.arms_folder("nope")


class ProcessHistory(unittest.TestCase):
    def test_planned_order(self) -> None:
        arms = [("a", ["-x"]), ("b", [])]
        self.assertEqual(sc.planned_order(arms, 1, "a b, a reboot b"),
                         [(1, "a", ["-x"]), (1, "b", []), (2, "a", ["-x"]), (0, "reboot", []), (2, "b", [])])
        self.assertEqual(sc.planned_order(arms, 2, ""), sc.arm_order(arms, 2))
        with self.assertRaises(SystemExit):
            sc.planned_order(arms, 1, "a c")

    def test_same_process(self) -> None:
        self.assertTrue(sc.same_process({"pid": 1, "started": 100.0}, {"pid": 1, "started": 100.4}))
        self.assertFalse(sc.same_process({"pid": 1, "started": 100.0}, {"pid": 1, "started": 900.0}))  # a reused pid
        self.assertTrue(sc.same_process({"pid": 1, "started": None}, {"pid": 1, "started": 900.0}))  # a box: no time
        self.assertFalse(sc.same_process({"pid": 1}, {"pid": 2}))
        self.assertFalse(sc.same_process(None, {"pid": 2}))
        self.assertTrue(sc.same_process(None, None))  # no pid files: only the runner's reboots reset it

    def test_history_carries_within_a_process_and_resets_at_a_reboot(self) -> None:
        one = {"server": {"pid": 1, "started": 10.0}, "client": {"pid": 2, "started": None}}
        two = {"server": {"pid": 3, "started": 50.0}, "client": {"pid": 4, "started": None}}
        # an identity at each arm's start and end
        code, events, rows = ArmSets().run_arms(
            ["copy=--set both:sacs.seed-copy=1 --tune server:sacs.gc-pressure=2", "base="], order="copy base reboot base",
            identities=[one, one, one, one, two, two])
        self.assertEqual(code, 0)
        self.assertIn(("python", "scripts/quick_loop.py", "run", "--boot"), events)
        copy, base1, base2 = rows
        self.assertEqual(copy["process"]["server"], {"pid": 1, "arms_before": [], "had": []})
        self.assertEqual(base1["process"]["server"], {"pid": 1, "arms_before": ["copy#1"], "had": ["sacs.gc-pressure", "sacs.seed-copy"]})
        self.assertEqual(base1["process"]["client"]["had"], ["sacs.seed-copy"])  # the --tune was the server's only
        self.assertEqual(base2["process"]["server"], {"pid": 3, "arms_before": [], "had": []})  # after the planned reboot
        import arms_lib

        f = arms_lib.row_functions(arms_lib.runner_row(base1, "x"))
        self.assertTrue(f["had"]("sacs.seed-copy") and not f["on"]("sacs.seed-copy"))

    def test_a_restart_during_an_arm_is_flagged(self) -> None:
        one = {"server": {"pid": 1, "started": 10.0}, "client": {"pid": 2, "started": None}}
        rejoined = {"server": {"pid": 1, "started": 10.0}, "client": {"pid": 9, "started": None}}
        _, _, rows = ArmSets().run_arms(["copy=--set both:sacs.seed-copy=1", "base="], order="copy base",
                                        identities=[one, rejoined, rejoined, rejoined])
        self.assertEqual(rows[0]["process_restarted"], ["client"])
        self.assertEqual(rows[1]["process"]["client"], {"pid": 9, "arms_before": [], "had": []})
        self.assertEqual(rows[1]["process"]["server"]["had"], ["sacs.seed-copy"])


class Soak(unittest.TestCase):
    """run --soak: the games left in the world for N minutes, checked every SOAK_CHECK seconds (a fake clock here)."""

    def setUp(self) -> None:
        self.now = [1000.0]
        self.speeds: list[str] = []
        self.checks: list[float] = []

    def clock(self) -> float:
        return self.now[0]

    def sleep(self, seconds: float) -> None:
        self.now[0] += seconds

    def speed_to(self, speed: str) -> list[str]:
        self.speeds.append(speed)
        return ["server ok", "client ok"]

    def soak(self, deaths: dict[int, str] | None = None, speed_to=None, minutes: float = 2.0) -> dict:
        def check() -> str | None:
            self.checks.append(self.now[0])
            self.now[0] += 25.0  # health()'s retries before it calls a game down
            return (deaths or {}).get(len(self.checks))

        spec = sc.soak_spec(minutes, "Triple", "free")
        return sc.soak_scenario(spec, clock=self.clock, sleep=self.sleep, check=check, speed_to=speed_to or self.speed_to,
                                sims_to=lambda mode: f"sims {mode}")

    def test_the_whole_soak_then_normal_speed(self) -> None:
        record = self.soak()
        self.assertEqual((record["verdict"], record["kind"], record["soak_min"]), ("PASS", "soak", 2.0))
        self.assertEqual(self.speeds, ["Triple", "Normal"])
        self.assertNotIn("total", record["timings"])  # never scenario minutes
        self.assertGreaterEqual(record["timings"]["soak"], 120.0)
        self.assertEqual(record["started"], 1000.0)

    def test_stops_at_the_first_dead_game(self) -> None:
        record = self.soak({2: "stack down: server no answer"}, minutes=20.0)
        self.assertEqual((record["verdict"], record["failures"]), ("FAIL", ["stack down: server no answer"]))
        self.assertEqual(len(self.checks), 2)
        self.assertEqual(record["timings"]["soak"], 85.0)  # 30 + 25 + 30: to the check that found it down
        self.assertEqual(self.speeds, ["Triple", "Normal"])

    def test_blocked_when_the_speed_does_not_take(self) -> None:
        def refused(speed: str) -> list[str]:
            self.speeds.append(speed)
            return ["server error: unknown speed", "client ok"] if speed == "Triple" else ["server ok", "client ok"]

        record = self.soak(speed_to=refused)
        self.assertEqual(record["verdict"], "BLOCKED")
        self.assertEqual(self.checks, [])
        self.assertNotIn("soak", record["timings"])
        self.assertEqual(self.speeds, ["Triple", "Normal"])

    def test_run_soak_arguments(self) -> None:
        with self.assertRaises(SystemExit):
            sc.main(["run", "--soak", "5", "--server-env", "SIMLINK_NOPATCH=1"])  # the environment needs --boot
        with mock.patch.object(sc, "soak_scenario", return_value={"name": "soak"}) as soaked:
            self.assertEqual(sc.run_scenario(sc.soak_spec(5), Path(".")), {"name": "soak"})
        soaked.assert_called_once()


class Exposure(unittest.TestCase):
    def make_run(self, root: Path, name: str, records: list[dict], end: float, reason: str | None = None) -> Path:
        run = root / name
        run.mkdir()
        (run / "records.json").write_text(json.dumps(records), encoding="utf-8")
        (run / "report.md").write_text("# run\n", encoding="utf-8")
        for p in (run / "records.json", run / "report.md"):
            os.utime(p, (end, end))
        if reason:
            (run / sc.EXCLUDED).write_text(reason, encoding="utf-8")
        return run

    @unittest.skipIf(crash_catch is None, 'internal crash_catch/gc_watch excluded in mirror')
    def test_soak_minutes_apart_from_scenario_minutes(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            t0 = time.mktime(time.strptime("20261018-100000", "%Y%m%d-%H%M%S"))
            self.make_run(root, "20261018-100000", [{"name": "a", "verdict": "PASS", "timings": {"total": 120}}], t0 + 200)
            self.make_run(root, "20261018-101000", [{"name": "soak", "kind": "soak", "verdict": "FAIL",
                                                      "timings": {"reset": 5, "soak": 1500}}], t0 + 2200)
            rows, _ = sc.exposure_rows(sorted(root.iterdir()), [(t0 + 2100, "server xcpt 1.txt")])
            by_run = {r["run"]: r for r in rows}
            self.assertEqual((by_run["20261018-100000"]["soak"], by_run["20261018-100000"]["minutes"]), (False, 2.0))
            self.assertEqual((by_run["20261018-101000"]["soak"], by_run["20261018-101000"]["minutes"],
                              by_run["20261018-101000"]["soak_minutes"]), (True, 0, 25.0))
            with mock.patch.object(sc, "RUNS", root), mock.patch.object(crash_catch, "crash_reports", return_value=[]), \
                    mock.patch("builtins.print") as out:
                self.assertEqual(sc.main(["exposure"]), 0)
        printed = "\n".join(str(c.args[0]) for c in out.call_args_list if c.args)
        scenario_table, soak_table = printed.split("soaks (run --soak, no scenarios): 1 run(s)")
        self.assertIn("   1      2.0       0   0.000", scenario_table)  # the soak's crash-free 0 min is not in it
        self.assertIn("soak-min", soak_table)
        self.assertIn("   1     25.0       0   0.000", soak_table)  # crash_reports is mocked empty for main()

    def test_run_window(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            start = time.mktime(time.strptime("20261018-100000", "%Y%m%d-%H%M%S"))
            run = self.make_run(Path(folder), "20261018-100000", [], start + 600)
            self.assertEqual(sc.run_window(run, slack=60.0), (start, start + 660))
            self.assertIsNone(sc.run_window(Path(folder) / "not-a-run"))
            late = self.make_run(Path(folder), "20261018-100500", [], start + 3600)  # rewritten an hour on
            os.utime(run / "report.md", (start + 3000, start + 3000))
            rows, _ = sc.exposure_rows([run, late], [])
        self.assertEqual(rows[0]["end"], start + 300)  # cut at the next run's start

    def test_a_crashed_scenario_counts_to_the_crash(self) -> None:
        # G16: as arms_lib.book_minutes books an arm, from the CRASHED scenario's start (its watch folder's
        # time less its reset) to the crash report's time, not to its record's end.
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            t0 = time.mktime(time.strptime("20261020-100000", "%Y%m%d-%H%M%S"))
            run = self.make_run(root, "20261020-100000", [
                {"name": "a", "verdict": "PASS", "motion": "C:\\m\\100010", "timings": {"reset": 10, "total": 60}},
                {"name": "b", "verdict": "CRASHED", "motion": "C:\\m\\100115", "timings": {"reset": 5, "total": 90}},
                {"name": "c", "verdict": "BLOCKED"}], t0 + 200)
            rows, _ = sc.exposure_rows([run], [(t0 + 105, "server xcpt BOX 26-10-20 10.01.40.txt")])
        self.assertEqual((rows[0]["minutes"], rows[0]["completed_minutes"]), (1.5, 2.5))

    def test_rows_group_and_hold_out(self) -> None:
        def rec(name: str, total: float, verdict: str = "PASS", **extra: object) -> dict:
            return dict({"name": name, "verdict": verdict, "failures": [], "timings": {"total": total}}, **extra)

        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            t0 = time.mktime(time.strptime("20261018-100000", "%Y%m%d-%H%M%S"))
            runs = [
                self.make_run(root, "20261018-100000", [rec("a", 60), rec("b", 600, "BLOCKED")], t0 + 300,
                              "auto: a game crashed (xcpt 1.txt: x)"),  # an outcome: counted
                self.make_run(root, "20261018-101000", [rec("a", 120, tunes=["both:x=1"])], t0 + 900),
                self.make_run(root, "20261018-102000", [rec("a", 60, debugger={"server": "passive"})], t0 + 1500,
                              "auto: passive crash catcher on the server during a"),  # a debugger: held out
                self.make_run(root, "20261018-103000", [rec("a", 60)], t0 + 2100, "trace by hand"),
            ]
            reports = [(t0 + 100, "server xcpt 1.txt"), (t0 + 1450, "client xcpt 2.txt"), (t0 + 1700, "server xcpt 3.txt"),
                       (t0 - 50, "server xcpt 0.txt")]
            rows, loose = sc.exposure_rows(runs, reports)
        by_run = {r["run"]: r for r in rows}
        self.assertEqual(by_run["20261018-100000"]["minutes"], 1.0)  # the BLOCKED scenario did not run
        self.assertEqual(by_run["20261018-100000"]["crashes"], ["server xcpt 1.txt"])
        self.assertFalse(by_run["20261018-100000"]["excluded"])
        self.assertEqual(by_run["20261018-101000"]["tunes"], "both:x=1")
        self.assertEqual(by_run["20261018-102000"]["debugger"], "server:passive")
        self.assertTrue(by_run["20261018-102000"]["excluded"])
        self.assertEqual(by_run["20261018-102000"]["crashes"], ["client xcpt 2.txt"])
        self.assertTrue(by_run["20261018-103000"]["excluded"])
        self.assertEqual(loose, ["server xcpt 3.txt"])  # between run 3 (ends +1500 s, slack 120) and run 4; xcpt 0 is before any
        table = sc.exposure_table(rows, loose)
        self.assertTrue(table[0].startswith("debugger"))
        defaults = next(line for line in table if line.startswith("none ") and line.endswith("| defaults"))
        self.assertIn("   1      1.0       1   1.000", defaults)  # kept: one run, 1 min, 1 crash
        self.assertIn("   1      1.0       0   0.000", defaults)  # held out: the by-hand run
        self.assertTrue(any("crash reports between runs" in line and line.endswith(": 1") for line in table))
        old = dict(rows[0], run="20261013-000000", untimed=True, crashes=["server xcpt 9.txt"])
        table = sc.exposure_table(rows + [old], loose)
        self.assertIn("   1      1.0       1   1.000", next(line for line in table if line.endswith("| defaults")))
        self.assertTrue(any(line.startswith("runs saved before timings") and ": 1, 1 crash" in line for line in table))

    @unittest.skipIf(crash_catch is None, 'internal crash_catch/gc_watch excluded in mirror')
    def test_exposure_command_reads_the_reports(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            self.make_run(root, "20261018-100000", [{"name": "a", "verdict": "PASS", "timings": {"total": 30}}],
                          time.mktime(time.strptime("20261018-100100", "%Y%m%d-%H%M%S")))
            with mock.patch.object(sc, "RUNS", root), mock.patch.object(crash_catch, "crash_reports", return_value=[]), \
                    mock.patch("builtins.print") as out:
                self.assertEqual(sc.main(["exposure", "--list"]), 0)
        printed = "\n".join(str(c.args[0]) for c in out.call_args_list if c.args)
        self.assertIn("1 run(s) 20261018-100000 .. 20261018-100000; no crash reports on disk", printed)
        self.assertIn("0.000", printed)

    def test_pooled_exposure_reads_arm_logs(self) -> None:
        import arms_lib_test

        with tempfile.TemporaryDirectory() as folder:
            log = arms_lib_test.write_boot5(Path(folder))
            argv = ["exposure", "--log", str(log), "--side", "server", "--run-only", "--list"]
            for pool in arms_lib_test.POOLS:
                argv += ["--pool", pool]
            for added in arms_lib_test.EARLIER:
                argv += ["--add", added]
            with mock.patch.object(sc, "report_index", return_value={}) as index, mock.patch("builtins.print") as out:
                self.assertEqual(sc.main(argv), 0)
            index.assert_called_once()  # --list reads the reports (profiles and archive) for each crash's triage
            printed = "\n".join(str(c.args[0]) for c in out.call_args_list if c.args)
            self.assertIn("16 arm row(s) from 1 log(s), 3 by hand; series arms6, boots2-4", printed)
            self.assertRegex(printed, r"A' pick\s+5\s+19\.08\s+1\s+0\s+1")  # run phase only: the two pull crashes out
            self.assertIn("hand-entered rows (--add) count every phase", printed)
            self.assertIn("! mixes series: A' pick from arms6", printed)
            self.assertIn("10 crash(es) in the logs; reports: 0 on disk (0 only in the archive), 10 gone", printed)
            with self.assertRaises(SystemExit):
                sc.main(["exposure", "--log", str(log), "--pool", "a=true", "--add", "x:nope=1/1"])
            with self.assertRaises(SystemExit):
                sc.main(["exposure", "--run-only"])  # pooled options without --pool
            with self.assertRaises(SystemExit):
                sc.main(["exposure", "--crash-match", "x"])
            with self.assertRaises(SystemExit):  # a hand-entered count has no reports to match
                sc.main(["exposure", "--log", str(log), "--pool", "base=true", "--add", "x:base=1/1", "--crash-match", "x"])
            # one crash kind: the server's boot 5 reports, two still on disk (one only in the archive)
            names = sorted({name for row in arms_lib_test.al.load_arm_log(log, "a") for s, _, name in row["crashes"]
                            if s == "server"})
            reports = {("server", names[0]): (Path("p") / names[0], False), ("server", names[1]): (Path("a") / names[1], True)}
            with mock.patch.object(sc, "report_index", return_value=reports), \
                    mock.patch.object(sc, "describe_crash", side_effect=lambda p: f"AV at 00e54af9 ({p.parent})"), \
                    mock.patch("builtins.print") as out:
                sc.main(["exposure", "--log", str(log), "--pool", "base=true", "--side", "server", "--crash-match", "00e54af9"])
            printed = "\n".join(str(c.args[0]) for c in out.call_args_list if c.args)
            self.assertIn("--crash-match '00e54af9': 2 of 10 crash(es) match; reports: 2 on disk (1 only in the archive), "
                          "8 gone (8 with no triage logged either, so not counted)", printed)
            self.assertRegex(printed, r"base\s+16\s+\S+\s+2\s+0")
        self.assertEqual(sc.log_source("C:/x/arms/20261019-1000/arms.jsonl"), (Path("C:/x/arms/20261019-1000/arms.jsonl"), "20261019-1000"))
        self.assertEqual(sc.log_source("C:/x/arms6.jsonl=boot5"), (Path("C:/x/arms6.jsonl"), "boot5"))


class CrashedVerdict(unittest.TestCase):
    """The scenario a game went down in reads CRASHED (C), not FAIL: D's old-save window (2026-10-18) had all three
    of its crashes inside a scenario that read F."""

    def run_suite(self, healths: list[str | None]) -> tuple[int, list[dict], str]:
        """`scenarios.py run a b c` on fakes: health() answers healths in turn (the check before the run, before b, before
        c, then after the last); each scenario passes in 30 s."""
        answers = iter(healths)
        specs = [{"name": n, "summary": "", "expect": "pass"} for n in ("a", "b", "c")]

        def scenario(spec: dict, out: Path) -> dict:
            return {"name": spec["name"], "summary": "", "expect": "pass", "failures": [], "verdict": "PASS", "met": True,
                    "log": [], "images": [], "timings": {"total": 30.0}}

        with tempfile.TemporaryDirectory() as folder, \
                mock.patch.object(sc, "RUNS", Path(folder)), mock.patch.object(sc, "load", return_value=specs), \
                mock.patch.object(sc, "health", side_effect=lambda: next(answers)), \
                mock.patch.object(sc, "run_scenario", side_effect=scenario), mock.patch.object(sc, "python", return_value=(0, "")), \
                mock.patch.object(sc, "crashes_since", return_value=[]), mock.patch.object(sc, "debugged_now", return_value={}), \
                mock.patch.object(sc, "client_module", return_value="m"), mock.patch.object(sc, "apply_tunes", return_value=([], [])), \
                mock.patch.object(sc, "restore_tunes"), mock.patch("stack_claim.warning", return_value=None), \
                mock.patch("builtins.print"):
            code = sc.main(["run", "a", "b", "c"])
            out = next(Path(folder).iterdir())
            records = json.loads((out / "records.json").read_text(encoding="utf-8"))
            table = (out / "report.md").read_text(encoding="utf-8")
        self.assertRaises(StopIteration, next, answers)  # every health answer was asked for, and no more
        return code, records, table

    def test_the_scenario_before_a_dead_stack_reads_crashed(self) -> None:
        down = "stack down: server no answer (crash report xcpt 1.txt: known interp-virtual-method)"
        code, records, table = self.run_suite([None, None, down])
        self.assertEqual(code, 6)
        self.assertEqual([r["verdict"] for r in records], ["PASS", "CRASHED", "BLOCKED"])
        b = records[1]
        self.assertEqual((b["crashed_over"], b["failures"], b["met"]), ("PASS", [down], False))
        self.assertIn("| b | CRASHED | pass |", table)
        rows = [m.group(2) for m in map(sc.ROW.match, table.splitlines()) if m]  # load_run's reader for old runs
        self.assertEqual(rows, ["PASS", "CRASHED", "BLOCKED"])

    def test_a_death_in_the_last_scenario_is_found_after_it(self) -> None:
        code, records, _ = self.run_suite([None, None, None, "stack down: client no answer"])
        self.assertEqual((code, [r["verdict"] for r in records]), (6, ["PASS", "PASS", "CRASHED"]))
        self.assertEqual(records[2]["failures"], ["stack down: client no answer"])  # no crash report named: says so

    def test_a_run_that_stays_up_has_no_crashed(self) -> None:
        code, records, _ = self.run_suite([None, None, None, None])
        self.assertEqual((code, [r["verdict"] for r in records]), (0, ["PASS", "PASS", "PASS"]))

    @unittest.skipIf(crash_catch is None, 'internal crash_catch/gc_watch excluded in mirror')
    def test_the_arms_summary_counts_c_apart_and_leaves_it_out_of_the_median(self) -> None:
        rows = [arm_row("a", 1, {"sleep": "PASS"}, {"sleep": 60.0}),
                arm_row("a", 2, {"sleep": "CRASHED"}, {"sleep": 26.0}, crashes=1),
                arm_row("a", 3, {"sleep": "FAIL"}, {"sleep": 62.0})]
        lines = sc.summarize_arms(rows)
        self.assertIn("C1 F1 P1", lines[1])
        self.assertTrue(lines[-1].startswith("sleep") and "PCF 61.0s" in lines[-1])
        self.assertEqual(sc.CODES["CRASHED"], "C")
        self.assertEqual(crash_catch.CODES_SHORT["CRASHED"], "C")

    def test_rescore_leaves_a_crashed_scenario_alone(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            run = Path(folder) / "20261019-010000"
            run.mkdir()
            rec = {"name": "sleep", "summary": "", "expect": "pass", "failures": ["stack down: server"], "verdict": "CRASHED",
                   "met": False, "images": [], "motion": "x/231410", "crashed_over": "FAIL"}
            (run / "records.json").write_text(json.dumps([rec]), encoding="utf-8")
            with mock.patch.object(sc, "resolve_run", return_value=run), mock.patch.object(sc, "python") as call, \
                    mock.patch.object(sc, "motion_states", return_value=[]), mock.patch("builtins.print"):
                sc.rescore(run.name)
            call.assert_not_called()
            self.assertEqual(json.loads((run / "rescored.json").read_text(encoding="utf-8"))[0]["verdict"], "CRASHED")


INDIRECT = "server xcpt HOST 26-10-19 04.44.52.txt: ACCESS_VIOLATION at 00e55997 (address 0x1d): known interp-indirect-load"


def placed(arm: str, repeat: int, minutes: float, crashes: int, before: int) -> dict:
    """An arm row with its server process snapshot: `before` arms ran in that game before it."""
    row = arm_row(arm, repeat, {"sit": "PASS"}, {"sit": minutes * 60})
    row.update(scenario_min=minutes, crashes=[INDIRECT] * crashes,
               process={"server": {"pid": 100 + repeat, "arms_before": ["x"] * before, "had": []}})
    return row


class WaitRun(unittest.TestCase):
    """G22: arms --wait-run times a short build inside an arm's run, from the live state.json and the item's
    own newest event."""

    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.folder = Path(self.tmp.name) / "arms" / "20261020-000000"
        self.folder.mkdir(parents=True)
        order = [[1, "base", []], [1, "gate", []], [2, "gate", []], [2, "base", []]]
        (self.folder / "plan.json").write_text(json.dumps({"order": order}), encoding="utf-8")
        (self.folder / "runner.json").write_text(json.dumps({"pid": 1}), encoding="utf-8")
        self.now = 1_792_420_000.0
        self.lines: list[str] = []
        # base #1 ran 300 s and gate #1 240 s
        self.event(1000, "== {t} base #1: boot hook")
        self.event(900, "== {t} base #1: run sit shower")
        self.event(600, "   run 20261020-000100: PP, 4.0 scenario-min, 0 crash(es) in the run")
        self.event(590, "== {t} gate #1: boot hook")
        self.event(500, "== {t} gate #1: run sit shower")
        self.event(260, "   run 20261020-000200: PP, 3.0 scenario-min, 0 crash(es) in the run")

    def at(self, ago: float) -> str:
        return time.strftime("%H:%M:%S", time.localtime(self.now - ago))

    def event(self, ago: float, text: str) -> None:
        self.lines.append(f"{self.at(ago)} {text.format(t=self.at(ago))}")
        (self.folder / "events.txt").write_text("\n".join(self.lines) + "\n", encoding="utf-8")

    def item(self, index: int) -> None:
        (self.folder / "state.json").write_text(json.dumps({"next": index, "finished": False}), encoding="utf-8")

    def wait(self, *args, sleep=None) -> tuple[int, list[str]]:
        out = io.StringIO()
        with mock.patch.dict(os.environ, {"SIMSONLINE_RUN_DIR": self.tmp.name}), contextlib.redirect_stdout(out):
            code = sc.arms_wait_run("20261020-000000", *args, sleep=sleep or (lambda s: None), now=lambda: self.now,
                                    alive=lambda lock: True)
        return code, out.getvalue().splitlines()

    def test_an_arm_in_its_run_with_time_left(self) -> None:
        self.item(2)
        self.event(250, "== {t} gate #2: boot hook")
        self.event(60, "== {t} gate #2: run sit shower")
        code, lines = self.wait(120)
        self.assertEqual((code, lines), (0, [f"20261020-000000: gate#2 is in its run since {self.at(60)} (1.0 min); "
                                             "typical run 4.0 min (the median of 1 finished run(s) of gate): about 3.0 "
                                             "min left"]))

    def test_never_an_older_arms_line(self) -> None:
        self.item(3)  # base #2 is next; the newest arm line is still gate #1's finished run
        steps = iter([lambda: self.event(0, "== {t} base #2: boot hook"),
                      lambda: self.event(0, "== {t} base #2: run sit shower")])
        code, lines = self.wait(120, sleep=lambda seconds: next(steps)())
        self.assertEqual(code, 0)
        self.assertEqual(lines[0], "20261020-000000: base#2 has not started (no event of its own in events.txt yet); "
                                   "typical run 5.0 min (the median of 1 finished run(s) of base)")
        self.assertTrue(lines[1].startswith("20261020-000000: base#2 is in its boot hook"))
        self.assertTrue(lines[2].startswith(f"20261020-000000: base#2 is in its run since {self.at(0)} (0.0 min)"))
        self.item(1)  # state.json still on gate #1, whose run has ended: not a run phase
        self.lines = self.lines[:6]
        self.event(0, "   after: exit 0")
        self.assertEqual(sc.run_phase(self.folder, self.now)["phase"], "after")

    def test_too_little_left_waits_for_the_next_arms_run(self) -> None:
        self.item(2)
        self.event(250, "== {t} gate #2: boot hook")
        self.event(200, "== {t} gate #2: run sit shower")

        def next_arm(seconds: float) -> None:
            self.event(0, "   run 20261020-000300: PP, 3.0 scenario-min, 0 crash(es) in the run")
            self.item(3)
            self.event(0, "== {t} base #2: boot hook")
            self.event(0, "== {t} base #2: run sit shower")

        code, lines = self.wait(120, sleep=next_arm)
        self.assertEqual(code, 0)
        self.assertTrue(lines[0].startswith("20261020-000000: gate#2 is in its run, but about 0.7 min left (under 2.0 "
                                            "min): waiting for the next arm's run"))
        self.assertIn("base#2 is in its run since", lines[1])

    @unittest.skipIf(crash_catch is None, 'internal crash_catch/gc_watch excluded in mirror')
    def test_a_catcher_dump_being_written_holds_it(self) -> None:
        self.item(2)
        self.event(250, "== {t} gate #2: boot hook")
        self.event(60, "== {t} gate #2: run sit shower")
        dumps = Path(self.tmp.name) / "dumps"  # crash_catch's folder in the same .omo/run as the arms folder
        dumps.mkdir()
        log = dumps / "attach-server.log"
        log.write_text("Creating C:\\run\\dumps\\server-7-20261020-000000-interp-av.dmp - mini user dump\n",
                       encoding="utf-8")
        catch = {"server": {"log": str(log), "cdb": os.getpid()}}  # a live cdb: this process, by the process list
        (dumps / "catch.json").write_text(json.dumps(catch), encoding="utf-8")

        def written(seconds: float) -> None:
            log.write_text(log.read_text(encoding="utf-8") + "Dump successfully written\n", encoding="utf-8")

        code, lines = self.wait(120, sleep=written)
        self.assertEqual(code, 0)
        self.assertTrue(lines[0].startswith("20261020-000000: gate#2 is in its run, but the catcher is writing a dump "
                                            "(server server-7-20261020-000000-interp-av.dmp): waiting until it is "
                                            "written; typical run 4.0 min"), lines[0])
        self.assertTrue(lines[1].startswith(f"20261020-000000: gate#2 is in its run since {self.at(60)} (1.0 min)"))
        self.assertEqual(len(lines), 2)

    def test_with_no_finished_run_only_typical_times_it(self) -> None:
        self.lines = []
        self.item(0)
        self.event(30, "== {t} base #1: run sit shower")
        self.assertEqual(self.wait()[0], 0)  # no --min-left: any run phase
        code, lines = self.wait(60, 300.0)
        self.assertEqual(code, 0)
        self.assertIn("typical run 5.0 min (--typical): about 4.5 min left", lines[0])
        calls = []
        code, lines = self.wait(60, sleep=lambda s: (calls.append(s), (self.folder / "runner.json").unlink()))
        self.assertEqual(code, 9)
        self.assertIn("its length is not known yet", lines[0])
        self.assertIn("the run has ended", lines[-1])


class ArmsViews(unittest.TestCase):
    def test_summary_leaves_excluded_rows_out(self) -> None:
        gate1 = {**placed("gate", 1, 10.0, 0, 0), "gates": [{"gate": "x", "result": "error"}]}
        records = [placed("base", 1, 10.0, 1, 0), gate1, {"kind": "gap", "arm": "gate", "repeat": 1, "crashes": [INDIRECT]},
                   placed("base", 2, 10.0, 1, 0), placed("gate", 2, 10.0, 0, 0)]
        with tempfile.TemporaryDirectory() as tmp:
            folders = [Path(tmp) / "20261019-000001", Path(tmp) / "20261019-000002"]
            for f in folders:
                f.mkdir()
                (f / "arms.jsonl").write_text("".join(json.dumps(r) + "\n" for r in records), encoding="utf-8")
            with mock.patch.object(sc, "report_paths", return_value={}), contextlib.redirect_stdout(io.StringIO()) as out:
                self.assertEqual(sc.arms_summary_again(str(folders[0]), exclude_invalid=True), 0)
            lines = out.getvalue().splitlines()
            self.assertEqual(lines[0], "excluded (1 row(s), with their gap records): gate#1: 10 scenario-min, "
                                       "1 crash(es) (1 in its gap); gate error")
            self.assertRegex(next(x for x in lines if x.startswith("gate ")), r"^gate\s+1\s+10\.0\s+0\s")
            self.assertRegex(next(x for x in lines if x.startswith("base ")), r"^base\s+2\s+20\.0\s+2\s")
            with mock.patch.object(sc, "report_paths", return_value={}), contextlib.redirect_stdout(io.StringIO()) as out:
                sc.arms_summary_again([str(f) for f in folders], exclude_texts=["20261019-000002/gate#1"])
            self.assertTrue(out.getvalue().startswith("excluded (1 row(s), with their gap records): "
                                                      "20261019-000002/gate#1: 10 scenario-min"))
            self.assertRegex(out.getvalue(), r"\ngate\s+3\s+30\.0\s+0\s")  # pooled: one of the four gate rows left out
            for bad in (["gate#1"], ["20261019-999999/gate#1"]):  # pooled needs the folder; and one --summary names
                with self.assertRaises(SystemExit):
                    sc.arms_summary_again([str(f) for f in folders], exclude_texts=bad)

    def test_summary_splits_first_and_later_arms_as_session_d_does(self) -> None:
        # 2026-10-19's EventQueueRecorder series: off 4 in 33.03 first-arm min and 1 in 28.62 later, on 0 in 25.65 and
        # 0 in 51.56 (Session D's doc: P = 0.017 two-sided, P ~ 0.036 taken by first and later arms)
        rows = [placed("off", 1, 33.03, 4, 0), placed("off", 2, 28.62, 1, 1),
                placed("on", 1, 25.65, 0, 0), placed("on", 2, 51.56, 0, 2)]
        lines = sc.summarize_arms(rows)
        self.assertRegex(lines[2], r"^on\s+2\s+77\.2\s+0\s+0\.0\s+0\.017\s")
        self.assertEqual(lines[4], "run crashes by site: off: interp-indirect-load 5")
        self.assertTrue(lines[5].startswith("first/later arm in its server process"))
        self.assertEqual(lines[6], "off            first 4 in 33.03 min, later 1 in 28.62 min")
        self.assertEqual(lines[7], "on             first 0 in 25.65 min, later 0 in 51.56 min; P 0.036 stratified "
                                   "one-sided exact, off against it stratum by stratum (2 strata: first/later)")
        self.assertEqual(lines[8], "               first arms only: off 4 in 33.03 min against 0 in 25.65: P 0.100 "
                                   "one-sided conditional binomial")
        # the triage now, not the one logged
        retold = sc.summarize_arms(rows, lambda text: "ACCESS_VIOLATION at 00d7084b (address 0xc248): not a known crash")
        self.assertEqual(retold[4], "run crashes by site: off: unknown 00d7084b 5")
        # a row without the snapshot (an older log): no split at all, not a guess
        old = [dict(r) for r in rows]
        del old[3]["process"]
        self.assertFalse(any(line.startswith("first/later") for line in sc.summarize_arms(old)))

    def series(self) -> list[dict]:
        """Session D's two EventQueueRecorder series (2026-10-19): A, 20261019-023100, with first and later arms; B,
        20261019-142515, every arm first (a reboot before each)."""
        a = [placed("off", 1, 33.03, 4, 0), placed("off", 2, 28.62, 1, 1),
             placed("on", 1, 25.65, 0, 0), placed("on", 2, 51.56, 0, 2)]
        b = [placed("off", 1, 63.87, 2, 0), placed("on", 1, 64.50, 0, 0)]
        return [{**r, "series": "A"} for r in a] + [{**r, "series": "B"} for r in b]

    def test_a_pooled_summary_takes_series_and_first_or_later_as_strata(self) -> None:
        lines = sc.summarize_arms(self.series())
        at = next(i for i, line in enumerate(lines) if line.startswith("first/later"))
        self.assertTrue(lines[at].endswith(", by series (2 arms runs, the strata series x first/later):"))
        self.assertEqual(lines[at + 1], "off            A: first 4 in 33.03 min, later 1 in 28.62 min; "
                                        "B: first 2 in 63.87 min, every arm first in its process")
        # Session D's figures: 0.009 over every stratum, 0.025 over the first arms, 0.019 pooling those
        self.assertTrue(lines[at + 2].endswith("; P 0.009 stratified one-sided exact, off against it stratum by stratum "
                                               "(3 strata: series x first/later)"), lines[at + 2])
        self.assertEqual(lines[at + 3], "               first arms only: P 0.025 stratified one-sided exact (2 strata: "
                                        "series); off 6 in 96.90 min against 0 in 90.15 pooled: P 0.019 one-sided "
                                        "conditional binomial (no strata)")

    def test_every_arm_first_is_said_so(self) -> None:
        b = [r for r in self.series() if r["series"] == "B"]
        lines = sc.summarize_arms([{k: v for k, v in r.items() if k != "series"} for r in b])
        self.assertIn("off            first 2 in 63.87 min, every arm first in its process", lines)
        self.assertNotIn("0.00 min", "\n".join(lines))
        self.assertEqual(sc.place_text(0, 0.0, 1, 5.0), "later 1 in 5.00 min, every arm later in its process")
        self.assertEqual(sc.place_text(0, 0.0, 0, 0.0), "no runs")

    def test_the_odds_of_more_crashes(self) -> None:
        # Session D's 10-19 figure: at B's off rate (2 in 63.9 min), 64 more minutes give P(>= 2 more) 0.59, a total
        # of 4, and P(>= 3 more) 0.32
        rows = [placed("off", 1, 63.9, 2, 0), placed("on", 1, 64.5, 0, 0)]
        lines = sc.summarize_arms(rows, odds=64)
        at = lines.index("odds over 64 more min at each arm's rate so far (Poisson; exposure as above):")
        self.assertTrue(lines[at + 1].startswith("off            2 in 63.90 min (0.0313/min, mean 2.00 more): "
                                                 "P(>=1 more) 0.87 (total >= 3), P(>=2 more) 0.59 (total >= 4), "
                                                 "P(>=3 more) 0.32 (total >= 5), "), lines[at + 1])
        self.assertTrue(lines[at + 1].endswith("P(>=6 more) 0.02 (total >= 8)"))  # up to the first under 0.05
        self.assertEqual(lines[at + 2], "on             0 in 64.50 min: no rate to project")

    def test_summary_pools_folders(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp) / "arms"
            for name, series in (("20261019-023100", "A"), ("20261019-142515", "B")):
                (base / name).mkdir(parents=True)
                rows = [{k: v for k, v in r.items() if k != "series"} for r in self.series() if r["series"] == series]
                (base / name / "arms.jsonl").write_text("".join(json.dumps(r) + "\n" for r in rows), encoding="utf-8")
            lines: list[str] = []
            with mock.patch.dict(os.environ, {"SIMSONLINE_RUN_DIR": tmp}), \
                    mock.patch("builtins.print", side_effect=lambda text="": lines.extend(str(text).split("\n"))):
                self.assertEqual(sc.arms_summary_again(["20261019-023100", "20261019-142515"], odds=64), 0)
                with self.assertRaises(SystemExit):
                    sc.arms_summary_again(["20261019-142515", "20261019-142515"])
        self.assertEqual(lines[0], "pooled: 20261019-023100 (4 arm row(s)), 20261019-142515 (2 arm row(s))")
        text = "\n".join(lines)
        self.assertIn("P 0.009 stratified one-sided exact", text)
        self.assertIn("off            7 in 125.52 min", text)

    def test_read_only_views_find_folders_in_the_run_dir(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            (Path(tmp) / "arms" / "20261019-023100").mkdir(parents=True)
            with mock.patch.dict(os.environ, {"SIMSONLINE_RUN_DIR": tmp}):
                base = sc.read_arms_base()
                self.assertEqual(base, Path(tmp) / "arms")
                self.assertEqual(sc.arms_folder("20261019-023100", base).name, "20261019-023100")
                self.assertEqual(sc.arms_folder("latest", base).name, "20261019-023100")
            with mock.patch.dict(os.environ, {"SIMSONLINE_RUN_DIR": ""}):
                self.assertEqual(sc.read_arms_base(), sc.ARMS)
            with self.assertRaises(SystemExit):
                sc.arms_folder("20261019-023100", Path(tmp) / "nowhere")

    def test_report_when(self) -> None:
        name = "xcpt HOST 26-10-19 03.23.20.txt"
        self.assertEqual(sc.report_when(None, name), time.mktime(time.strptime("26-10-19 03.23.20", "%y-%m-%d %H.%M.%S")))
        self.assertIsNone(sc.report_when(None, "xcpt 0.txt"))
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / name
            path.write_text("x", encoding="utf-8")
            os.utime(path, (1792340600.035, 1792340600.035))
            self.assertAlmostEqual(sc.report_when(path, name), 1792340600.035, places=2)  # the file's time, to the ms

    def test_safe_triage_falls_back_to_the_logged_line(self) -> None:
        def broken() -> dict:
            raise OSError("profiles unreadable")

        self.assertEqual(sc.safe_triage(broken)(INDIRECT), "ACCESS_VIOLATION at 00e55997 (address 0x1d): known interp-indirect-load")
        with mock.patch.object(sc, "describe_crash", side_effect=lambda p: "now"):
            self.assertEqual(sc.safe_triage(lambda: {("server", "xcpt HOST 26-10-19 04.44.52.txt"): Path("x")})(INDIRECT), "now")

    def test_build_and_machine_lines(self) -> None:
        busy = dict(LOAD, seconds=200.0, cpu_pct={"mean": 50.0, "max": 90.0}, avail_mb={"min": 2500},
                    others=[{"name": "MsMpEng.exe", "pid": 2, "cpu_pct": 6.5, "max_pct": 30, "cpu_s": 13.0, "ws_mb": 300},
                            {"name": "SearchIndexer.exe", "pid": 3, "cpu_pct": 1.0, "max_pct": 5, "cpu_s": 2.0, "ws_mb": 90}])
        old = {"build_slot": {"slot": "old", "digest": "abc123"}, "sets_unread": ["server sacs.event-recorder=1"]}
        rows = [{**placed("new", 1, 10.0, 0, 0), "load": LOAD}, {**placed("old", 1, 10.0, 0, 0), **old, "load": LOAD},
                {**placed("old", 2, 10.0, 0, 0), **old, "load": busy}, placed("base", 1, 10.0, 0, 0)]
        lines = sc.machine_lines(["new", "old", "base"], rows)
        self.assertEqual(lines[1:], [
            "  new: load over 1 run(s): cpu 30.0% mean, 70% max; free 2.9 GB min; others: MsMpEng.exe 4.0% (1/1)",
            "  old: build slot old (abc123) in 2/2 run(s); not read by its mod: server sacs.event-recorder=1 (2/2); "
            "load over 2 run(s): cpu 35.0% mean, 90% max; free 2.4 GB min; others: MsMpEng.exe 6.5% (2/2), "
            "SearchIndexer.exe 1.0% (1/2)"])  # the mean weighs each run by its seconds; base has nothing to say
        self.assertIn(lines[0], sc.summarize_arms(rows))
        self.assertEqual(sc.machine_lines(["base"], rows), [])
        self.assertEqual(sc.arm_notes_text(rows[1]), "build slot old (abc123); not read by its mod: server "
                                                     "sacs.event-recorder=1; load: " + machine_load.text(LOAD))
        self.assertEqual(sc.run_load(dict(LOAD, series=[[0, 1.0, 2]])), LOAD)

    def test_readings_view(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp) / "arms" / "20261019-023100"
            folder.mkdir(parents=True)
            on = {"eventRecorder": "on", "eventRecorder.ever": "1", "eventRecorder.events": "6951", "calls": "7"}
            readings = [{"arm": "on", "repeat": 1, "kind": "start", "t": 1792340000.0, "server": {**on, "eventRecorder.ever": "0"},
                         "client": on},
                        {"arm": "off", "repeat": 5, "kind": "end", "t": 1792340100.0, "server": None, "client": {"calls": "9"}}]
            (folder / "status.jsonl").write_text("".join(json.dumps(r) + "\n" for r in readings) + "{cut", encoding="utf-8")

            def run(*args: str) -> tuple[int, list[str]]:
                out = io.StringIO()
                with mock.patch.dict(os.environ, {"SIMSONLINE_RUN_DIR": tmp}), contextlib.redirect_stdout(out):
                    code = sc.main(["arms", "--readings", "20261019-023100", *args])
                return code, out.getvalue().splitlines()

            code, lines = run()
            self.assertEqual((code, lines), (0, ["arms 20261019-023100: 2 reading(s); fields: eventRecorder, "
                                                 "eventRecorder.ever, eventRecorder.events, calls"]))
            code, lines = run("--field", "eventRecorder.e*")
            self.assertEqual(lines[0], "arms 20261019-023100: eventRecorder.ever, eventRecorder.events")
            self.assertTrue(lines[1].startswith("on#1      start "))
            self.assertTrue(lines[1].endswith("server: eventRecorder.ever=0 eventRecorder.events=6951 | "
                                              "client: eventRecorder.ever=1 eventRecorder.events=6951"))
            self.assertTrue(lines[2].endswith("server: down (no answer) | client: -"))
            code, lines = run("--field", "calls", "--kind", "end")
            self.assertEqual(len(lines), 2)
            self.assertTrue(lines[1].endswith("client: calls=9"))
            row = {**placed("on", 1, 10.0, 0, 0), "build_slot": {"slot": "old", "digest": "abc123"}, "load": LOAD}
            (folder / "arms.jsonl").write_text(json.dumps(row) + "\n", encoding="utf-8")
            code, lines = run("--field", "calls")  # each arm row's notes once, before its first reading
            self.assertEqual(lines[1], "on#1      row     build slot old (abc123); load: " + machine_load.text(LOAD))
            self.assertTrue(lines[2].startswith("on#1      start "))
            self.assertTrue(lines[3].startswith("off#5     end "))
            code, lines = run("--field", "nosuch")
            self.assertEqual(code, 1)
            self.assertIn("no reading has nosuch", lines[0])
        with self.assertRaises(SystemExit):
            sc.main(["arms", "--field", "calls"])

    def test_table_and_compare_views(self) -> None:
        """arms --table: one row per arm run with each --field's last-minus-start reading per game (a game that stopped
        answering keeps its earlier reading); --json the same rows; arms --compare A B: the one-sided binomial."""
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp) / "arms" / "20261019-023100"
            folder.mkdir(parents=True)
            rows = [placed("on", 1, 10.0, 1, 0), placed("off", 1, 20.0, 0, 1), placed("on", 2, 10.0, 1, 2)]
            rows[0]["crashes_gap"] = [INDIRECT]
            (folder / "arms.jsonl").write_text("".join(json.dumps(r) + "\n" for r in rows), encoding="utf-8")
            readings = [{"arm": "on", "repeat": 1, "kind": "start", "t": 1.0, "server": {"calls": "10", "ring": "on"},
                         "client": {"calls": "5"}},
                        {"arm": "on", "repeat": 1, "kind": "timer", "t": 2.0, "server": {"calls": "12", "ring": "on"},
                         "client": {"calls": "6"}},
                        {"arm": "on", "repeat": 1, "kind": "end", "t": 3.0, "server": {"calls": "15", "ring": "off"},
                         "client": None}]
            (folder / "status.jsonl").write_text("".join(json.dumps(r) + "\n" for r in readings), encoding="utf-8")

            def run(*args: str) -> tuple[int, list[str]]:
                out = io.StringIO()
                with mock.patch.dict(os.environ, {"SIMSONLINE_RUN_DIR": tmp}), contextlib.redirect_stdout(out):
                    code = sc.main(["arms", *args])
                return code, out.getvalue().splitlines()

            code, lines = run("--table", "--field", "calls,ring")
            self.assertEqual(code, 0)
            self.assertTrue(lines[0].startswith("arms 20261019-023100: 3 arm row(s); fields:"))
            self.assertEqual(lines[1].split()[-4:], ["S:calls", "S:ring", "C:calls", "C:ring"])
            self.assertTrue(lines[2].startswith("on#1"))
            # 1 run crash, 1 outside (its gap), P1; server calls 15 - 10, ring's last value; the client's last answer
            # is the timer read (the end gave none): calls 6 - 5; no client ring
            self.assertRegex(lines[2], r"\s1\s+1\s+P1\s+-\s+5\s+off\s+1\s+-$")
            self.assertRegex(lines[3], r"^off#1\s.*\s0\s+0\s+P1\s+-\s+-\s+-\s+-\s+-$")  # no readings
            code, lines = run("--table", "--json")
            self.assertEqual(code, 0)
            self.assertEqual([json.loads(line)["arm"] for line in lines], ["on", "off", "on"])
            self.assertEqual(json.loads(lines[0])["crashes_outside"], 1)
            code, lines = run("--table", "--field", "nosuch")
            self.assertEqual(code, 1)
            self.assertIn("no reading has nosuch", lines[-1])
            code, lines = run("--compare", "on", "off")
            self.assertEqual(code, 0)
            self.assertEqual(lines[0], "on: 2 run crash(es) in 20.0 min over 2 run(s), 6.0 per hour")
            self.assertEqual(lines[1], "off: 0 run crash(es) in 20.0 min over 1 run(s), 0.0 per hour")
            self.assertIn("P(A >= 2 | equal rates) = 0.250 (one-sided binomial)", lines[2])
            with self.assertRaises(SystemExit):
                run("--compare", "on", "nosuch")
            with self.assertRaises(SystemExit):
                run("--json")
            with self.assertRaises(SystemExit):
                run("--folder", "20261019-023100")
            with self.assertRaises(SystemExit):  # not honoured here, so refused rather than silently counted in
                run("--compare", "on", "off", "--exclude", "on#2")

    @unittest.skipIf(crash_catch is None, 'internal crash_catch/gc_watch excluded in mirror')
    def test_crashes_view_reads_the_folder_only(self) -> None:
        name = "xcpt HOST 26-10-19 04.44.52.txt"
        with tempfile.TemporaryDirectory() as tmp:
            run_dir = Path(tmp)
            folder = run_dir / "arms" / "20261019-023100"
            folder.mkdir(parents=True)
            row = placed("off", 14, 6.0, 1, 1)
            row.update(died_in="run", verdicts={"phone-seated": "FAIL", "shower": "BLOCKED"},
                       totals={"phone-seated": 44.0, "shower": None})
            (folder / "arms.jsonl").write_text(json.dumps(row) + "\n", encoding="utf-8")
            report = run_dir / "crash-reports" / "server" / name
            report.parent.mkdir(parents=True)
            report.write_text("x", encoding="utf-8")
            t = time.mktime(time.strptime("26-10-19 04.44.52", "%y-%m-%d %H.%M.%S"))  # the name's local time, any zone
            os.utime(report, (t, t))
            gc = [{"t": t - 474, "kind": "open", "side": "server", "pid": 4336, "gc": 300},
                  {"t": t - 8.249, "kind": "gc", "side": "server", "pid": 4336, "gc": 366},
                  {"t": t - 3.057, "kind": "gc", "side": "server", "pid": 4336, "gc": 367},
                  {"t": t + 0.01, "kind": "exit", "side": "server", "pid": 4336, "gc": 367}]
            (run_dir / "gc-watch.jsonl").write_text("".join(json.dumps(r) + "\n" for r in gc) + '{"t": 1', encoding="utf-8")
            out = io.StringIO()
            with (mock.patch.dict(os.environ, {"SIMSONLINE_RUN_DIR": tmp}),
                  mock.patch.object(crash_catch, "report_folders", return_value={}),
                  mock.patch.object(crash_catch, "archive_reports", side_effect=AssertionError("writes")),
                  mock.patch.object(sc, "describe_crash", return_value="ACCESS_VIOLATION at 00e55997 (address 0x1d): "
                                                                       "known interp-indirect-load"),
                  contextlib.redirect_stdout(out)):
                self.assertEqual(sc.main(["arms", "--crashes", "20261019-023100"]), 0)
        text = out.getvalue()
        self.assertIn("1 crash(es); collections from", text)
        line = next(x for x in text.splitlines() if x.startswith("10-19 "))
        self.assertRegex(line, r"server\s+off#14\s+run\s+phone-seated\s+4336\s+later \(1\)\s+7\.9 min\s+3\.06 s\s+"
                               r"5\.19 s\s+367\s+interp-indirect-load$")

    CHECK = ["arms", "phone-seated", "--arm", "off=--set both:sacs.event-recorder=0", "--arm", "on=",
             "--gate", "off: end.server.eventRecorder.enabled == 0", "--gate", "on: end.server.eventRecorder.events > 7000"]

    def check(self, argv: list[str], run_dir: str = "") -> tuple[int, str]:
        out = io.StringIO()
        with (mock.patch.dict(os.environ, {"SIMSONLINE_RUN_DIR": run_dir}),
              mock.patch.object(sc, "arms", side_effect=AssertionError("ran the arms")),
              mock.patch.object(sc, "health", side_effect=AssertionError("asked a game")),
              contextlib.redirect_stdout(out)):
            code = sc.main(argv)
        return code, out.getvalue()

    def test_check_prints_the_plan_and_touches_no_game(self) -> None:
        code, text = self.check(self.CHECK + ["--check"])
        self.assertEqual(code, 0)
        lines = text.splitlines()
        self.assertEqual(lines[0], "arms check: 4 run(s) of phone-seated; nothing run, no game touched")
        self.assertEqual(lines[1], "   off: --set both:sacs.event-recorder=0 (2 run(s))")
        self.assertEqual(lines[2], "   on: (no arguments) (2 run(s))")
        self.assertEqual(lines[3], "   order: off on on off")
        self.assertIn("tunables each arm sets", lines[4])
        self.assertIn("sacs.event-recorder", lines[4])
        self.assertIn("   gate 'off: end.server.eventRecorder.enabled == 0': after off; reads end.server.eventRecorder.enabled",
                      lines)
        # the run's own checks stop it the same way
        for bad in (["--gate", "x: arm == 1"], ["--order", "off reboot on"] + ["--boot-hook", "boot {arm}"],
                    ["--resume", "latest"]):
            with self.assertRaises(SystemExit):
                self.check(self.CHECK + ["--check"] + bad)

    def test_check_names_each_arms_boot_env(self) -> None:
        argv = ["arms", "sit", "--arm", "base=", "--arm", "gate=--server-env SIMLINK_GCGATE=1", "--check"]
        code, text = self.check(argv + ["--order", "gate base reboot gate base"])
        self.assertEqual(code, 0)
        self.assertIn("   boot env (every boot the runner makes passes it): base server none, client none; "
                      "gate server SIMLINK_GCGATE=1, client none\n", text)
        self.assertEqual([line for line in text.splitlines() if line.startswith("   warning: ")], [
            "   warning: gate #1 boots with server SIMLINK_GCGATE=1, client none but runs on server unknown, client "
            "unknown (the stack as found); a reboot before it in --order boots its own",
            "   warning: base #2 boots with server none, client none but runs on server SIMLINK_GCGATE=1, client none "
            "(the planned reboot at item 3); a reboot before it in --order boots its own"])
        with self.assertRaises(SystemExit) as refused:
            self.check(argv + ["--boot-hook", "boot {arm}"])
        self.assertIn("names no {server_env}", str(refused.exception))
        code, text = self.check(argv + ["--boot-hook", "boot {arm} {server_env}"])
        self.assertEqual(code, 0)
        self.assertNotIn("warning:", text)  # a boot hook boots each arm's own
        with self.assertRaises(SystemExit):
            self.check(argv + ["--server-env", "NO_VALUE"])
        _, text = self.check(argv + ["--server-env", "SIMLINK_GCGATE=1", "--repeat", "4"])  # never rebooted: one line
        self.assertEqual([line for line in text.splitlines() if line.startswith("   warning: ")], [
            "   warning: base #1, gate #1, gate #2, base #2, base #3, gate #3 and 2 more boot with server "
            "SIMLINK_GCGATE=1, client none but run on server unknown, client unknown (the stack as found); a reboot "
            "before the first in --order boots theirs"])

    def test_check_against_evaluates_each_gate_on_a_finished_run(self) -> None:
        def reading(arm: str, repeat: int, kind: str, **server: str) -> dict:
            return {"arm": arm, "repeat": repeat, "kind": kind, "t": 1792337571.4,
                    "server": {f"eventRecorder.{k}": v for k, v in server.items()}, "client": None}

        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp) / "arms" / "20261019-023100"
            folder.mkdir(parents=True)
            rows = [reading("off", 1, "start", enabled="0"), reading("off", 1, "end", enabled="0"),
                    reading("off", 2, "end", enabled="1"),  # failed
                    reading("off", 3, "start", enabled="0"),  # died in its run: no gate after it
                    reading("on", 1, "end", events="8000"),
                    reading("on", 2, "start", events="10")]  # no end reading: missing
            (folder / "status.jsonl").write_text("".join(json.dumps(r) + "\n" for r in rows) + '{"arm', encoding="utf-8")
            (folder / "arms.jsonl").write_text(json.dumps({**placed("off", 3, 6.0, 1, 0), "died_in": "run"}) + "\n",
                                               encoding="utf-8")
            code, text = self.check(self.CHECK + ["--check-against", "20261019-023100"], tmp)
        self.assertEqual(code, 1)
        self.assertIn("   against 20261019-023100: 5 arm(s) with status readings", text)
        self.assertIn("      failed 1, not evaluated (died) 1, passed 1; first: off#2: failed "
                      "(end.server.eventRecorder.enabled=1)", text)
        self.assertRegex(text, r"\n      missing 1, passed 1; first: on#2: missing \(end\.server\.eventRecorder\.events=missing")
        self.assertIn("stops it (exit 8)", text)


class Probes(unittest.TestCase):
    @unittest.skipIf(crash_catch is None, 'internal crash_catch/gc_watch excluded in mirror')
    def test_probe_failures_never_stop_a_run(self) -> None:
        with mock.patch.object(crash_catch, "debugger_kinds", side_effect=OSError("no access")):
            self.assertEqual(sc.debugged_now(), {})
        with mock.patch.object(crash_catch, "crash_reports", side_effect=OSError("no profile")):
            self.assertEqual(sc.crashes_since(0.0), [])

    @unittest.skipIf(crash_catch is None, 'internal crash_catch/gc_watch excluded in mirror')
    def test_crashes_since_triages_each_report(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "xcpt X.txt"
            path.write_text("[Exception info]\ntype: BREAKPOINT\naddress: 0x007f9f46\n", encoding="utf-8")
            with mock.patch.object(crash_catch, "crash_reports", return_value=[path]):
                lines = sc.crashes_since(0.0)
        self.assertEqual(len(lines), 1)
        self.assertEqual(lines, ["xcpt X.txt: BREAKPOINT at 007f9f46: known debugger-breakpoint"])  # short form
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "xcpt X.txt"
            path.write_text("[Exception info]\ntype: BREAKPOINT\naddress: 0x007f9f46\n", encoding="utf-8")
            with mock.patch.object(crash_catch, "crash_reports", return_value=[path]), \
                    mock.patch.object(quick_loop, "REAL", Path(folder)):
                self.assertTrue(sc.crashes_since(0.0, sides=True)[0].startswith("server xcpt X.txt: BREAKPOINT"))



class During(unittest.TestCase):
    EVENTS = [
        "08:38:20.550  client Pablo     walk end arrived at 1139.11,983.93",
        "08:38:20.693  server Pablo     state E BedSleep:3112858048 by game via StateMachineClient.EnterState",
        "08:38:20.720  client Pablo     state E BedSleep:3112858048 by replay",
        "08:38:20.883  client Pablo     server walk ended at 1139.11,983.93",
        "08:38:21.850  client Pablo     mirror turn to the server's facing",
        "08:38:21.951  client Pablo     sacs StateEntered BedSleep:Sleep",
        "08:38:28.460  server Pablo     sacs StateEntered BedSleep:Sleep",
        "08:38:29.000  client Pablo     mirror walk 0.87 m to the server's Sim",
    ]

    def test_a_mirror_turn_inside_the_clip_and_the_entry_gap(self) -> None:
        got = sc.during_run(self.EVENTS, "BedSleep:Sleep", 8.0, sc.DURING_ACTIONS)
        self.assertAlmostEqual(got["span"], 7.767, places=3)
        self.assertAlmostEqual(got["client_in"] - got["server_in"], -6.509, places=3)
        # the mirror walk after the server's entry is outside; state, sacs and "server walk ended" lines are not mod actions
        self.assertEqual([what for _, what in got["hits"]], ["mirror turn to the server's facing"])
        self.assertEqual(len(got["mod"]), 1)

    def test_no_server_E_and_no_state(self) -> None:
        self.assertIsNone(sc.during_run(self.EVENTS, "Shower:Wash", 8.0, sc.DURING_ACTIONS))
        got = sc.during_run(self.EVENTS, "BedSleep", 2.0, sc.DURING_ACTIONS)
        self.assertIsNone(got["server_in"])
        self.assertEqual(got["span"], 2.0)
        self.assertEqual(len(got["hits"]), 1)


class ReplayCounters(unittest.TestCase):
    """Each scenario records the watched clients' replay counters before and after it, and the drivers check after it
    (2026-10-21: the first crumple was found by eye)."""

    def test_the_change_and_the_doubles_reach_the_record_and_the_report(self) -> None:
        import replay_check
        before = {"counters": {"client1": {"failures": 2, "handovers": 0}, "client2": {"failures": 2, "handovers": 0}}}
        after = {"counters": {"client1": {"failures": 4, "handovers": 1}, "client2": {"failures": 2, "handovers": 0}},
                 "doubles": ["client1 Pablo: Idle x2 (driver:state 1:standing_idle 2:standing_idle)"], "silent": []}
        snaps = iter([before, after])
        with mock.patch.object(sc, "WATCHED", ["127.0.0.1:34030", "127.0.0.1:34031"]), \
                mock.patch.object(replay_check, "snapshot", side_effect=lambda clients, games=None: next(snaps)) as snap:
            record: dict = {"name": "sit", "summary": "", "verdict": "PASS", "failures": [], "images": [], "log": []}
            sc.note_replay(record, sc.replay_snapshot(False))
        self.assertIsNone(snap.call_args_list[0].args[1])  # before: counters only
        self.assertEqual(snap.call_args_list[1].args[1], {"server": "127.0.0.1:34029", "client1": "127.0.0.1:34030",
                                                          "client2": "127.0.0.1:34031"})
        self.assertEqual(record["replay"]["change"], {"client1": "failures 2->4 (+2), handovers 0->1 (+1)",
                                                      "client2": "no change"})
        self.assertEqual(record["doubles"], after["doubles"])
        self.assertEqual(record["failures"], [])  # report only
        with tempfile.TemporaryDirectory() as folder:
            sc.report(Path(folder), [{**record, "expect": "pass"}])
            body = (Path(folder) / "report.md").read_text(encoding="utf-8")
        self.assertIn("Replay counters over the scenario: client1 failures 2->4 (+2), handovers 0->1 (+1); client2 no change", body)
        self.assertIn("Two live drivers of one machine after it", body)

    def test_nothing_is_read_without_a_watched_client(self) -> None:
        import replay_check
        with mock.patch.object(sc, "WATCHED", []), mock.patch.object(replay_check, "snapshot") as snap:
            record: dict = {}
            sc.note_replay(record, sc.replay_snapshot(False))
        snap.assert_not_called()
        self.assertEqual(record, {})

    def test_the_other_client_is_watched_when_it_is_in_the_world(self) -> None:
        with mock.patch.object(sc, "CLIENT", "127.0.0.1:34030"), \
                mock.patch.object(sc, "game_down", side_effect=lambda e: None if e == "127.0.0.1:34031" else "down"):
            self.assertEqual(sc.watched_clients(), ["127.0.0.1:34030", "127.0.0.1:34031"])
        with mock.patch.object(sc, "CLIENT", "127.0.0.1:34031"), mock.patch.object(sc, "game_down", return_value="refused"):
            self.assertEqual(sc.watched_clients(), ["127.0.0.1:34031"])


class RepeatSamples(unittest.TestCase):
    """2026-10-21 (Session E): "two-chairs two-chairs" ran once; repeat samples needed separate calls."""

    def test_a_name_given_twice_is_refused_not_dropped(self) -> None:
        with self.assertRaises(SystemExit) as raised:
            sc.load(["two-chairs", "sit", "two-chairs"])
        self.assertIn("--repeat", str(raised.exception))

    def test_the_suite_repeats_in_order_and_each_sample_has_its_key(self) -> None:
        specs = sc.repeat_specs([{"name": "a"}, {"name": "b"}], 2)
        self.assertEqual([(s["name"], s["sample"]) for s in specs], [("a", 1), ("b", 1), ("a", 2), ("b", 2)])
        self.assertEqual(sc.repeat_specs([{"name": "a"}], 1), [{"name": "a"}])  # one pass: the specs as they were
        self.assertEqual([sc.record_key(s) for s in specs], ["a", "b", "a#2", "b#2"])
        with self.assertRaises(SystemExit):
            sc.repeat_specs([{"name": "a"}], 0)

    def test_samples_are_rows_of_their_own_in_the_table_and_history(self) -> None:
        records = [{"name": "a", "verdict": "PASS", "expect": "pass", "failures": [], "sample": 1},
                   {"name": "a", "verdict": "FAIL", "expect": "pass", "failures": ["motion: slide"], "sample": 2}]
        self.assertEqual(sc.table(records)[2:], ["| a | PASS | pass | - |", "| a#2 | FAIL | pass | motion: slide |"])
        with tempfile.TemporaryDirectory() as folder:
            run = Path(folder) / "20261021-060000"
            run.mkdir()
            out = io.StringIO()
            with mock.patch.object(sc, "runs", return_value=[run]), mock.patch.object(sc, "excluded", return_value=False), \
                    mock.patch.object(sc, "load_run", return_value=records), contextlib.redirect_stdout(out):
                sc.history([], 10)
        rows = [line.split()[:2] for line in out.getvalue().splitlines() if line.startswith("a")]
        self.assertEqual(rows, [["a", "P"], ["a#2", "F"]])


class HealthCommand(unittest.TestCase):
    """scenarios.py health: the up check, then the long-session report (replay_check.health_lines); a report that
    cannot be read never hides the up check's answer or changes its exit code."""

    def test_the_report_follows_the_up_check(self) -> None:
        import replay_check
        out = io.StringIO()
        with mock.patch.object(sc, "health", return_value=None), \
                mock.patch.object(replay_check, "health_facts", return_value={"now": 0, "games": {}, "processes": {}}), \
                contextlib.redirect_stdout(out):
            self.assertEqual(sc.main(["health"]), 0)
        lines = out.getvalue().splitlines()
        self.assertEqual(lines[0], "ok: both games answer, in the world")
        self.assertTrue(lines[-1].startswith("health: ") and "boot_due=0" in lines[-1], lines[-1])

    def test_an_unreadable_report_keeps_the_answer(self) -> None:
        import replay_check
        out = io.StringIO()
        with mock.patch.object(sc, "health", return_value="stack down: client refused"), \
                mock.patch.object(quick_loop, "fork_line", return_value="fork: x installed"), \
                mock.patch.object(replay_check, "health_facts", side_effect=OSError("no bridge")), \
                contextlib.redirect_stdout(out):
            self.assertEqual(sc.main(["health"]), 6)
        self.assertEqual(out.getvalue().splitlines(), ["stack down: client refused", "fork: x installed",
                                                       "long-session health not read (OSError: no bridge)"])


class RunBootClients(unittest.TestCase):
    """run --boot --clients 2 passes the count to quick_loop's boot; without --boot it is refused."""

    def boot_words(self, *argv: str) -> list[tuple[str, ...]]:
        calls: list[tuple[str, ...]] = []

        def fake_python(*args: str, timeout: int = 900) -> tuple[int, str]:
            calls.append(args)
            return 1, "boot failed"  # stop the run at its boot

        with mock.patch.object(sc, "python", side_effect=fake_python), \
                mock.patch("stack_claim.warning", return_value=None), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(sc.main(list(argv)), 1)
        return calls

    def test_the_count_reaches_the_boot(self) -> None:
        self.assertEqual(self.boot_words("run", "--boot", "--clients", "2")[0][:5],
                         ("scripts/quick_loop.py", "run", "--boot", "--clients", "2"))
        self.assertNotIn("--clients", self.boot_words("run", "--boot")[0])  # quick_loop's default: SIMSONLINE_COMPARE_CLIENT
        with self.assertRaises(SystemExit):
            sc.main(["run", "--clients", "2"])


class TwoClients(unittest.TestCase):
    """a scenario that watches both clients ("clients": [1, 2]) and drives a player's pick (cpick:)."""

    def setUp(self) -> None:
        # the replay counters before and after a scenario come from the live games: none here
        patcher = mock.patch.object(sc, "replay_snapshot", return_value=None)
        patcher.start()
        self.addCleanup(patcher.stop)

    SPEC = {"name": "two", "summary": "s", "clients": [1, 2], "steps": ["cpick:2:Chair Sit", "hud:2:5:hudQueue=1"],
            "watch": {"sims": ["Pablo"], "seconds": 10}, "after": ["sims:Pablo --machines", "notice:2:5:Can't do"],
            "expect": "pass"}

    def test_which_clients(self) -> None:
        self.assertEqual(sc.spec_clients({"clients": [1, 2]}), ["1", "2"])
        self.assertEqual(sc.spec_clients({}, ["1", "2"]), ["1", "2"])
        self.assertEqual(sc.spec_clients({"clients": [2]}, ["1", "2"]), ["2"])  # the file wins over --watch-clients
        self.assertEqual(sc.spec_clients({}), [sc.client_key(sc.CLIENT)])

    def test_whose_failure(self) -> None:
        self.assertEqual(sc.failure_client("client 2 motion: 1 client-only event(s): x"), "2")
        self.assertEqual(sc.failure_client("step hud:1:5:hudQueue=1: hud: client 1 ..."), "1")
        self.assertEqual(sc.failure_client("step nonotice:2:.: exit 1"), "2")
        self.assertEqual(sc.failure_client("step menudiff:2:Pablo:type:Bookshelf: menudiff: shown, server refuses: x"), "2")
        self.assertEqual(sc.failure_client("assert client2 SACS_REPLAY: no /x/"), "2")
        self.assertIsNone(sc.failure_client("step wait:server:Pablo:Sit:30: exit 1"))
        self.assertEqual(sc.failure_client("step until:client2:Pablo:Sit:30: until: client2 Pablo never matched"), "2")
        self.assertEqual(sc.failure_client("step wait:client1:Pablo:Sit:30: exit 1"), "1")
        self.assertIsNone(sc.failure_client("step until:client:Pablo:Sit:30: exit 1"))  # the compared client: every watched one
        self.assertEqual(sc.split_failures("client 1 motion: a; b; client 2 compare: c"), ["client 1 motion: a; b", "client 2 compare: c"])

    def test_verdict_per_client(self) -> None:
        record = {"expect": "pass", "failures": ["client 2 motion: 1 client-only event(s): slide", "step wait:server:P:x:5: exit 1"]}
        got = sc.client_verdicts(record, ["1", "2"])
        self.assertEqual(got["1"]["verdict"], "FAIL")  # a shared step failure counts for both
        self.assertEqual(len(got["2"]["failures"]), 2)
        known = sc.client_verdicts({"expect": "known-failing: x", "failures": ["client 2 compare: !"]}, ["1", "2"])
        self.assertEqual((known["1"]["verdict"], known["2"]["verdict"]), ("FIXED?", "known-failing"))

    def run_two(self, motion_codes: dict[str, int], pick_code: int = 0) -> tuple[dict, list[tuple[str, ...]]]:
        calls: list[tuple[str, ...]] = []

        def fake_python(*args: str, timeout: int = 900) -> tuple[int, str]:
            calls.append(args)
            if args[0] == "scripts/motion_watch.py":
                client = args[args.index("--client") + 1]
                out = f"all events (1): C:\\motion\\w{client}\\events.txt\n"
                if motion_codes[client] == 4:
                    out += "  12:00:01  CLIENT-ONLY  Pablo  slide 0.4 m  x\n"
                return motion_codes[client], out
            if args[0] == "scripts/client_pick.py":
                return pick_code, "client 2: ok=true" if pick_code == 0 else "client 2: err=target not found"
            return 0, ""

        with tempfile.TemporaryDirectory() as folder, mock.patch.object(sc, "python", side_effect=fake_python), \
                mock.patch.object(sc, "quick_step", side_effect=lambda step: calls.append(("quick", step)) or (0, "")), \
                mock.patch.object(sc, "reset", return_value="") as reset, mock.patch.object(sc.time, "sleep"), \
                mock.patch.object(sc, "note_stream") as stream:
            record = sc.run_scenario(self.SPEC, Path(folder))
        self.reset_clients = reset.call_args[0][1]
        self.stream_call = stream.call_args
        return record, calls

    def test_a_watch_and_a_compare_per_client(self) -> None:
        record, calls = self.run_two({"1": 0, "2": 4})
        watches = [c for c in calls if c[0] == "scripts/motion_watch.py"]
        self.assertEqual(sorted(c[c.index("--client") + 1] for c in watches), ["1", "2"])
        self.assertEqual(len({c[c.index("--stop-file") + 1] for c in watches}), 2)  # a stop file each
        compares = [c for c in calls if c[0] == "scripts/sim_compare.py"]
        self.assertEqual([c[-1] for c in compares], ["1", "2"])
        self.assertIn(("quick", "notices:mark"), calls)  # the scenario asserts a notice: marked after the reset
        self.assertEqual(self.reset_clients, [sc.compare_client.resolve("1"), sc.compare_client.resolve("2")])
        self.assertEqual(len(record["failures"]), 1)
        self.assertTrue(record["failures"][0].startswith("client 2 motion: 1 client-only event(s)"))
        self.assertEqual(record["verdict"], "FAIL")
        self.assertEqual((record["clients"]["1"]["verdict"], record["clients"]["2"]["verdict"]), ("PASS", "FAIL"))
        self.assertEqual(record["motions"], {"1": "C:\\motion\\w1", "2": "C:\\motion\\w2"})
        self.assertEqual(record["motion"], "C:\\motion\\w1")
        self.assertIn("client 1 PASS; client 2 FAIL", sc.client_line(record["clients"]))
        # the stream silences are read over the watch's window from both watched clients' logs
        _, endpoints, since, until = self.stream_call.args
        self.assertEqual(endpoints, {"1": sc.compare_client.resolve("1"), "2": sc.compare_client.resolve("2")})
        self.assertLessEqual(since, until)

    def test_a_pick_the_probe_cannot_make_blocks(self) -> None:
        record, calls = self.run_two({"1": 0, "2": 0}, pick_code=1)
        self.assertEqual(record["verdict"], "BLOCKED")
        self.assertIn("err=target not found", record["failures"][0])
        self.assertNotIn(("quick", "hud:2:5:hudQueue=1"), calls)  # the rest is skipped

    def run_steps(self, failing: str) -> tuple[dict, list[str]]:
        """A scenario whose step `failing` fails; the quick steps it ran."""
        spec = {"name": "stops", "expect": "pass", "after": ["sims:Pablo"],
                "steps": ["notice:1:5:Sit", "until:server:Pablo:Sit:5", "hud:1:5:hudQueue=1", "nonotice:1:5:Stand"]}
        ran: list[str] = []

        def fake_step(step: str) -> tuple[int, str]:
            ran.append(step)
            return (1, f"{step.split(':')[0]}: not seen in 5 s") if step == failing else (0, "")

        with tempfile.TemporaryDirectory() as folder, mock.patch.object(sc, "python", return_value=(0, "")), \
                mock.patch.object(sc, "quick_step", side_effect=fake_step), mock.patch.object(sc, "reset", return_value=""), \
                mock.patch.object(sc.time, "sleep"), mock.patch.object(sc, "note_stream"):
            record = sc.run_scenario(spec, Path(folder))
        return record, ran

    def test_a_failed_until_blocks_at_that_step(self) -> None:
        """P, 2026-10-21: the failed server until steps were logged, but the run went on."""
        record, ran = self.run_steps("until:server:Pablo:Sit:5")
        self.assertEqual(record["verdict"], "BLOCKED")
        self.assertEqual(record["failures"], ["step until:server:Pablo:Sit:5: until: not seen in 5 s"])
        self.assertEqual(ran, ["notices:mark", "notice:1:5:Sit", "until:server:Pablo:Sit:5"])  # nothing after it

    def test_failed_premise_blocks_before_the_action(self) -> None:
        spec = {"name": "premise", "expect": "pass", "steps": ["premise:1:borrowed-idle:Jennifer", "sleep:4"]}
        calls = []
        with tempfile.TemporaryDirectory() as folder, mock.patch.object(sc, "python", return_value=(0, "")), \
                mock.patch.object(sc, "quick_step", side_effect=lambda step: calls.append(step) or
                                  (2, "premise: borrowed idle was not invalidated")), \
                mock.patch.object(sc, "reset", return_value=""), mock.patch.object(sc.time, "sleep"), \
                mock.patch.object(sc, "note_stream"):
            result = sc.run_scenario(spec, Path(folder))
        self.assertEqual(result["verdict"], "BLOCKED")
        self.assertEqual(calls, ["premise:1:borrowed-idle:Jennifer"])
        self.assertIn("not invalidated", result["failures"][0])

    def test_rose_excludes_repairs_from_the_premise(self) -> None:
        check = "rose:client:SACS_RECORDER status:idleRepairStream"
        spec = {"name": "premise-repair", "steps": ["premise:1:borrowed-idle:Jennifer", "sleep:4"],
                "after": [check], "expect": "pass"}
        for stream_repairs in (0, 1):
            with self.subTest(stream_repairs=stream_repairs):
                counter = [0]
                reads = []

                def step(value):
                    counter[0] += 1 if value.startswith("premise:") else stream_repairs
                    return 0, ""

                def read(*args):
                    reads.append(counter[0])
                    return counter[0], ""

                with tempfile.TemporaryDirectory() as folder, \
                        mock.patch.object(sc, "quick_step", side_effect=step), \
                        mock.patch.object(sc, "read_counter", side_effect=read), \
                        mock.patch.object(sc, "reset", return_value=""):
                    result = sc.run_scenario(spec, Path(folder))
                self.assertEqual(result["verdict"], "PASS" if stream_repairs else "FAIL")
                self.assertEqual(reads[-2:], [1, 1 + stream_repairs])
                if not stream_repairs:
                    self.assertIn("idleRepairStream did not rise (1 -> 1)", result["failures"][0])

    def test_a_failed_hud_assert_fails_there_and_the_after_checks_run(self) -> None:
        record, ran = self.run_steps("hud:1:5:hudQueue=1")
        self.assertEqual(record["verdict"], "FAIL")
        self.assertEqual(record["failures"], ["step hud:1:5:hudQueue=1: hud: not seen in 5 s"])
        self.assertNotIn("nonotice:1:5:Stand", ran)
        self.assertTrue(any(entry.startswith("## after sims:Pablo") for entry in record["log"]))  # the after check still ran
        self.assertIn("## steps stopped at hud:1:5:hudQueue=1: the rest were not run", record["log"])

    def test_the_machine_load_rides_each_scenario_into_the_report(self) -> None:
        class FakeSampler:
            def __init__(self, interval: float) -> None:
                self.interval = interval

            def start(self):
                return self

            def stop(self) -> dict:
                return {"seconds": 60, "samples": 12, "cpu_pct": {"mean": 82.0, "max": 99.0}, "top": []}

        import machine_load
        with mock.patch.object(machine_load, "Sampler", FakeSampler):
            record = sc.sampled(lambda spec: {"name": spec, "verdict": "PASS", "summary": "", "images": [], "log": []}, "one")
        self.assertEqual(record["load"]["cpu_pct"], {"mean": 82.0, "max": 99.0})
        with tempfile.TemporaryDirectory() as folder:
            sc.report(Path(folder), [record | {"expect": "pass", "met": True, "failures": []}])
            text = (Path(folder) / "report.md").read_text(encoding="utf-8")
        self.assertIn("Machine load over it (report only, scripts/machine_load.py): 60 s, 12 samples; cpu 82.0% mean, "
                      "99.0% max", text)

    def test_one_client_scenarios_read_as_before(self) -> None:
        spec = {"name": "one", "steps": [], "watch": {"sims": ["Pablo"], "seconds": 5}, "after": ["sims:Pablo"], "expect": "pass"}
        seen: list[tuple[str, ...]] = []

        def fake_python(*args: str, timeout: int = 900) -> tuple[int, str]:
            seen.append(args)
            return (4, "all events (1): C:\\m\\events.txt\n  12:00:01  CLIENT-ONLY  Pablo  slide  x\n") \
                if args[0] == "scripts/motion_watch.py" else (0, "")

        with tempfile.TemporaryDirectory() as folder, mock.patch.object(sc, "python", side_effect=fake_python), \
                mock.patch.object(sc, "reset", return_value=""), mock.patch.object(sc.time, "sleep"), \
                mock.patch.object(sc, "note_stream"):
            record = sc.run_scenario(spec, Path(folder))
        self.assertTrue(all("--client" not in a for a in seen))
        self.assertTrue(record["failures"][0].startswith("motion: "))
        self.assertNotIn("clients", record)
        self.assertNotIn("motions", record)

    def test_rescore_rescores_each_clients_watch(self) -> None:
        rec = {"name": "two", "summary": "", "expect": "pass", "verdict": "FAIL", "met": False, "images": [],
               "failures": ["client 2 motion: 1 client-only event(s): x", "step hud:2:5:q: no"],
               "motion": "w1", "motions": {"1": "w1", "2": "w2"}}
        with tempfile.TemporaryDirectory() as folder:
            run = Path(folder) / "20261021-120000"
            run.mkdir()
            with mock.patch.object(sc, "resolve_run", return_value=run), mock.patch.object(sc, "load_run", return_value=[rec]), \
                    mock.patch.object(sc, "python", return_value=(0, "")) as py, contextlib.redirect_stdout(io.StringIO()):
                sc.rescore("latest")
        self.assertEqual([c.args[2] for c in py.call_args_list], ["w1", "w2"])
        self.assertEqual(rec["failures"], ["step hud:2:5:q: no"])  # the motion failure went, the step's stayed
        self.assertEqual(rec["clients"]["1"]["verdict"], "PASS")


class NoForkMark(unittest.TestCase):
    """A run whose games logged hook=absent (booted without the gameplay fork) is marked NO-FORK in every view."""

    GATE = "server.log: 12:00:01 authority_gate_published state=on hook=absent"

    def fixture(self, root: Path, name: str, nofork: bool) -> Path:
        run = root / name
        run.mkdir()
        rec = {"name": "two-chairs", "summary": "", "expect": "pass", "failures": ["client 2 pick stayed local"],
               "verdict": "BLOCKED", "met": False, "images": []}
        (run / "records.json").write_text(json.dumps([rec]), encoding="utf-8")
        (run / "report.md").write_text("# run\n", encoding="utf-8")
        if nofork:
            (run / sc.NOFORK).write_text(self.GATE + "\n", encoding="utf-8")
        return run

    def test_rescore_of_an_old_run_shows_the_mark(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            run = self.fixture(Path(folder), "20261101-100000", True)
            out = io.StringIO()
            with mock.patch.object(sc, "resolve_run", return_value=run), contextlib.redirect_stdout(out):
                sc.rescore(run.name)
            self.assertIn(sc.NOFORK_BANNER, out.getvalue())
            self.assertIn("| two-chairs | BLOCKED [NO-FORK] | pass |", out.getvalue())
            saved = json.loads((run / "rescored.json").read_text(encoding="utf-8"))
            self.assertEqual((saved[0]["verdict"], saved[0]["nofork"]), ("BLOCKED", True))  # the verdict itself is left

    def test_a_run_with_the_fork_is_not_marked(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            run = self.fixture(Path(folder), "20261101-100000", False)
            self.assertFalse(sc.run_nofork(run))
            self.assertFalse(sc.load_run(run)[0].get("nofork"))
            self.assertNotIn("NO-FORK", "\n".join(sc.table(sc.load_run(run))))

    def test_a_game_log_copied_into_the_run_folder_counts(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            run = self.fixture(Path(folder), "20261101-100000", False)
            (run / "client.log").write_text("x\nauthority_gate_published hook=absent\n", encoding="utf-8")
            self.assertTrue(sc.load_run(run)[0]["nofork"])

    def test_history_and_status_show_it_per_run(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            self.fixture(root, "20261101-100000", True)
            self.fixture(root, "20261101-110000", False)
            out = io.StringIO()
            with mock.patch.object(sc, "RUNS", root), contextlib.redirect_stdout(out):
                sc.history([], 5)
            self.assertIn("NO-FORK runs (hook=absent, not gate results): 20261101-100000\n", out.getvalue())
            self.assertNotIn("20261101-110000\n", out.getvalue().split("NO-FORK runs", 1)[1])
            state = {"records": [], "planned": ["a"], "ended": "done", "progress": {}}
            self.assertIn("NO-FORK", sc.suite_status_line(root / "20261101-100000", 0.0, state=state))
            self.assertNotIn("NO-FORK", sc.suite_status_line(root / "20261101-110000", 0.0, state=state))

    def test_the_report_and_its_rows_carry_the_mark_and_still_parse(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            run = Path(folder) / "20261101-100000"
            run.mkdir()
            rec = {"name": "two-chairs", "summary": "s", "expect": "pass", "failures": ["f"], "verdict": "FAIL", "met": False,
                   "images": [], "log": [], "nofork": True}
            rows = sc.report(run, [rec])
            self.assertIn("| two-chairs | FAIL [NO-FORK] | pass | f |", rows)
            self.assertIn(sc.NOFORK_BANNER, (run / "report.md").read_text(encoding="utf-8"))
            match = sc.ROW.match(rows.splitlines()[2])
            self.assertEqual(match.group(2), "FAIL")

    def test_the_snapshot_copies_this_runs_lines_and_skips_an_earlier_runs_log(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            logs, run = Path(folder) / "logs", Path(folder) / "run"
            logs.mkdir()
            run.mkdir()
            (logs / "server.log").write_text("a\nauthority_gate_published hook=absent\n", encoding="utf-8")
            (logs / "client.log").write_text("authority_gate_published hook=absent\n", encoding="utf-8")
            os.utime(logs / "client.log", (1000.0, 1000.0))  # written before the run began: an earlier run's
            sc.gate_snapshot(run, time.time() - 60, logs)
            text = (run / sc.NOFORK).read_text(encoding="utf-8")
            self.assertIn("server.log: ", text)
            self.assertNotIn("client.log", text)
            clean = Path(folder) / "clean"
            clean.mkdir()
            sc.gate_snapshot(clean, 0.0, Path(folder) / "none")
            self.assertFalse((clean / sc.NOFORK).exists())

    def _aborted_run(self, error: BaseException) -> tuple[str, str]:
        """Run a scenario loop that dies with `error`, over a fixture run folder and logs; return the snapshot and stderr."""
        with tempfile.TemporaryDirectory() as folder:
            logs, run = Path(folder) / "logs", Path(folder) / "run"
            logs.mkdir()
            run.mkdir()
            (logs / "server.log").write_text("authority_gate_published hook=absent\n", encoding="utf-8")
            err = io.StringIO()
            with mock.patch.dict(os.environ, {"SIMSONLINE_RUN_DIR": str(logs)}), contextlib.redirect_stderr(err):
                with self.assertRaises(type(error)) as caught:
                    with sc.snapshot_on_exit(run, time.time() - 60):
                        raise error
            self.assertIs(caught.exception, error)  # the original propagates unchanged
            return (run / sc.NOFORK).read_text(encoding="utf-8"), err.getvalue()

    def test_an_exception_mid_loop_still_leaves_the_snapshot(self) -> None:
        text, _ = self._aborted_run(RuntimeError("scenario blew up"))
        self.assertIn("server.log: ", text)

    def test_ctrl_c_mid_loop_still_leaves_the_snapshot(self) -> None:
        text, _ = self._aborted_run(KeyboardInterrupt())
        self.assertIn("server.log: ", text)

    def test_a_snapshot_failure_does_not_mask_the_original_error(self) -> None:
        err = io.StringIO()
        with mock.patch.object(sc, "gate_snapshot", side_effect=PermissionError("denied")), contextlib.redirect_stderr(err):
            with self.assertRaises(KeyboardInterrupt):
                with sc.snapshot_on_exit(Path("."), 0.0):
                    raise KeyboardInterrupt
        self.assertIn("gate snapshot failed: denied", err.getvalue())


class LateJoinSteps(unittest.TestCase):
    """--rejoin runs a scenario's rejoin steps as another kind of join; watch.from starts the watch before a
    step (late-join-bed: after the join, before the get-up)."""

    def test_rejoin_step(self) -> None:
        with mock.patch.object(sc, "REJOIN", ""):
            self.assertEqual(sc.rejoin_step("rejoin:paused"), "rejoin:paused")
        with mock.patch.object(sc, "REJOIN", "warm"):
            self.assertEqual(sc.rejoin_step("rejoin:paused"), "rejoin:warm")
            self.assertEqual(sc.rejoin_step("rejoin"), "rejoin:warm")
            self.assertEqual(sc.rejoin_step("sleep:5"), "sleep:5")
        with mock.patch.object(sc, "REJOIN", "cold"):
            self.assertEqual(sc.rejoin_step("rejoin:paused"), "rejoin")

    def test_watch_from_starts_before_that_step(self) -> None:
        order: list[str] = []

        class InlineThread:
            def __init__(self, target, args=(), daemon=False):
                self.target, self.args = target, args

            def start(self):
                self.target(*self.args)

            def join(self, timeout=None):
                pass

        def fake_python(script: str, *args: str, **kwargs) -> tuple[int, str]:
            order.append(Path(script).stem + (" " + args[0] if script.endswith("sim_act.py") else ""))
            return 0, ""

        def fake_step(step: str) -> tuple[int, str]:
            order.append(step)
            return 0, ""

        spec = {"name": "t", "steps": ["sleep:1", "rejoin:paused", "act:Pablo --clear --stand", "sleep:2"],
                "watch": {"sims": ["Pablo"], "seconds": 5, "from": "act:Pablo --clear --stand"}, "after": []}
        with tempfile.TemporaryDirectory() as tmp, mock.patch.object(sc, "REJOIN", "warm"), \
                mock.patch.object(sc, "reset", return_value=""), mock.patch.object(sc, "python", fake_python), \
                mock.patch.object(sc, "quick_step", fake_step), mock.patch.object(sc.time, "sleep"), \
                mock.patch.object(sc.threading, "Thread", InlineThread), mock.patch.object(sc, "note_stream"):
            sc.run_scenario(spec, Path(tmp))
        self.assertEqual(order[:5], ["sleep:1", "rejoin:warm", "motion_watch", "sim_act Pablo", "sleep:2"])



class StreamNote(unittest.TestCase):
    """each scenario's stream silences from the watched clients' controller logs (scripts/stream_gaps.py)."""

    LOG = ("[2026-10-20 20:09:00.600Z] received: {\"$type\":\"sim_state_changed\"}\n"
           "[2026-10-20 20:09:01.210Z] received: {\"$type\":\"sim_animation\",\"lines\":[]}\n"
           "[2026-10-20 20:09:03.000Z] received: {\"$type\":\"session_joined\",\"playerId\":1}\n"
           "[2026-10-20 20:09:07.410Z] received: {\"$type\":\"sim_animation\",\"lines\":[]}\n"
           "[2026-10-20 20:09:07.900Z] received: {\"$type\":\"sim_animation\",\"lines\":[]}\n")

    @staticmethod
    def at(clock: str) -> float:
        from datetime import datetime, timezone
        return datetime.strptime("2026-10-20 " + clock, "%Y-%m-%d %H:%M:%S").replace(tzinfo=timezone.utc).timestamp()

    def note(self, files: dict[str, str], since: str, until: str, endpoints: dict[str, str] | None = None) -> dict:
        record: dict = {}
        with tempfile.TemporaryDirectory() as tmp:
            booted, other = Path(tmp) / "simscope" / ".omo" / "run", Path(tmp) / "tools" / ".omo" / "run"
            for rel, text in files.items():
                path = Path(tmp) / rel
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(text, encoding="utf-8")
            booted.mkdir(parents=True, exist_ok=True)
            other.mkdir(parents=True, exist_ok=True)
            sc.note_stream(record, endpoints or {"2": sc.compare_client.resolve("2")}, self.at(since), self.at(until),
                           runs=[other, booted])
        return record

    def test_a_silence_in_the_watch_is_noted_from_the_log_of_the_checkout_that_booted_the_games(self) -> None:
        record = self.note({"simscope/.omo/run/client2.log": self.LOG}, "20:09:01", "20:09:07")
        self.assertEqual(record["stream"], {"client 2": [
            "stream silent 6.2 s at 20:09:01.2 (after sim_state_changed; inside: session_joined)"]})
        self.assertIn("client 2 stream silent 6.2 s", sc.stream_line(record))

    def test_an_archive_copy_serves_a_window_the_live_log_no_longer_covers(self) -> None:
        later = "[2026-10-20 21:00:00.000Z] client_start: {}\n"
        record = self.note({"simscope/.omo/run/client2.log": later,
                            "simscope/.omo/run/archive/2026-10-21-071612-auto/client2.log": self.LOG}, "20:09:01", "20:09:07")
        self.assertIn("client 2", record["stream"])

    def test_nothing_is_noted_without_a_silence_a_log_or_a_known_client(self) -> None:
        self.assertEqual(self.note({"simscope/.omo/run/client2.log": self.LOG}, "20:09:08", "20:09:08"), {})  # no gap
        self.assertEqual(self.note({}, "20:09:01", "20:09:07"), {})  # no log covers it
        self.assertEqual(self.note({"simscope/.omo/run/client2.log": self.LOG}, "20:09:01", "20:09:07",
                                   endpoints={"x": "127.0.0.1:39999"}), {})  # not client 1 or 2: no log name
        self.assertEqual(self.note({"simscope/.omo/run/client.log": self.LOG}, "20:09:01", "20:09:07"), {})  # client 1's

    def test_a_reading_problem_is_kept_beside_the_verdict(self) -> None:
        record: dict = {}
        with mock.patch("stream_gaps.covering_log", side_effect=OSError("locked")):
            sc.note_stream(record, {"1": sc.compare_client.resolve("1")}, 0.0, 1.0, runs=[])
        self.assertEqual(record, {"stream_error": "OSError: locked"})


class ResetStart(unittest.TestCase):
    """A Sim placed while it still leaves a bed or a seat ends the exit off its start: the reset waits for each Sim its
    first calls found busy, reads it after the place, and places again once (2026-10-21)."""

    HOME = {"Jennifer": [1128.4, 988.0, 0.34, 0.94], "Pablo": [1127.4, 988.9, 0.0, 1.0]}
    IDLE = {
        "Jennifer": "actor=Jennifer was=- queued=0 autonomyOff=1 room=3 at=1128.40,988.00",
        "Pablo": "actor=Pablo was=- queued=0 autonomyOff=1 room=3 at=1127.40,988.90",
    }

    def test_social_reset_leaves_conversation_before_waiting_or_placing(self) -> None:
        events = []
        talking = {"Jennifer", "Pablo"}

        def end():
            events.append("leave")
            talking.clear()
            return "conversations: the server ended them"

        def invoke(script, *args, **kwargs):
            if "--clear" in args:
                events.append("stand")
                return 0, self.IDLE[args[0]] + " posture=SocializingPosture stand=queued:SocializingPosture"
            self.assertFalse(talking, "social posture cannot unwind while its conversation remains")
            return 0, "placed=Jennifer,Pablo"

        with (
            mock.patch.object(sc, "python", invoke),
            mock.patch.object(sc, "end_conversations", end),
            mock.patch.object(sc.time, "sleep", side_effect=lambda _: self.assertFalse(talking)),
            mock.patch.object(sc, "start_spots", return_value={}),
            mock.patch.object(sc, "settle", side_effect=lambda _: self.assertFalse(talking)),
        ):
            sc.reset()
        self.assertEqual(events, ["stand", "stand", "leave"])

    def run_reset(self, first: dict[str, str], reads: dict[str, list[str]]) -> tuple[list[list[str]], dict, str]:
        calls: list[list[str]] = []
        self.cameras: list[tuple[dict[str, str], str]] = []

        def fake_python(script: str, *args: str, **kwargs) -> tuple[int, str]:
            calls.append(list(args))
            if "--place-all" in args:
                return 0, "placed=Jennifer,Pablo"
            if "--clear" in args:
                return 0, ("released=- held=Jennifer,Pablo " if args[0] == "Jennifer" else "") + first[args[0]]
            return 0, reads[args[0]].pop(0)

        note: dict = {}
        with tempfile.TemporaryDirectory() as tmp:
            home = Path(tmp) / "home.json"
            home.write_text(json.dumps(self.HOME), encoding="utf-8")
            with mock.patch.object(sc, "python", fake_python), mock.patch.object(sc.time, "sleep"), \
                    mock.patch.object(sc, "HOME", home), \
                    mock.patch.object(sc, "camera", lambda on, target, distance=1.0: self.cameras.append((on, target))
                                      or [f"{side}: focused = {target}" for side in on]):
                log = sc.reset(note=note)
        return calls, note, log

    @staticmethod
    def places(calls: list[list[str]]) -> list[int]:
        return [i for i, c in enumerate(calls) if "--place-all" in c]

    def test_an_idle_household_is_placed_without_a_read(self) -> None:
        calls, note, log = self.run_reset(self.IDLE, {})
        self.assertEqual(len(calls), 4)  # the two first calls, then one place on the server and one on the client
        self.assertEqual(self.places(calls), [2, 3])
        self.assertEqual(note, {})
        self.assertNotIn("start:", log)
        # every game's camera on the household's spot (the middle of the start spots), after the place
        self.assertEqual(self.cameras, [({"server": sc.SERVER, sc.compare_client.name(sc.CLIENT): sc.CLIENT}, "1127.90,988.45")])
        self.assertTrue(log.endswith(f"camera: server: focused = 1127.90,988.45; "
                                     f"{sc.compare_client.name(sc.CLIENT)}: focused = 1127.90,988.45"))

    def test_a_sim_still_getting_out_of_bed_is_placed_once_it_is_idle(self) -> None:
        first = dict(self.IDLE, Pablo="actor=Pablo was=BedSleep queued=1 autonomyOff=1 room=1 at=1139.26,984.84 "
                                      "cleared=1 stand=queued:SleepingPosture")
        reads = {"Pablo": ["actor=Pablo was=BedSleep queued=0 at=1139.20,984.90",  # the exit still plays
                           "actor=Pablo was=- queued=0 at=1139.00,985.00",  # idle by the bed
                           self.IDLE["Pablo"]]}  # on its spot after the place
        calls, note, _ = self.run_reset(first, reads)
        reads_before = [i for i, c in enumerate(calls) if c == ["Pablo"]]
        self.assertEqual(len(reads_before), 3)
        self.assertGreater(self.places(calls)[0], reads_before[1])  # placed only after the idle read
        self.assertEqual(len(self.places(calls)), 2)  # on its spot: no second place
        self.assertEqual(note, {})

    def test_a_sim_off_its_start_after_the_place_is_placed_again_and_noted(self) -> None:
        first = dict(self.IDLE, Jennifer="actor=Jennifer was=- queued=0 autonomyOff=1 room=3 at=1130.01,989.07 "
                                         "stand=queued:SittingPosture")
        reads = {"Jennifer": ["actor=Jennifer was=- queued=0 at=1130.01,989.07",  # stood up by the chair
                              "actor=Jennifer was=- queued=0 at=1128.20,988.79",  # the place did not hold
                              "actor=Jennifer was=- queued=0 at=1128.20,988.79",  # idle there
                              "actor=Jennifer was=- queued=0 at=1128.40,988.00"]}  # placed again: on its spot
        calls, note, log = self.run_reset(first, reads)
        self.assertEqual(len(self.places(calls)), 4)  # server and client, twice
        self.assertEqual(note["start"], ["Jennifer 0.81 m off its start after the reset, placed again: 0.00 m"])
        self.assertIn("\nstart: Jennifer 0.81 m off its start after the reset, placed again: 0.00 m\ncamera: ", log)
        self.assertEqual(reads["Jennifer"], [])

    def test_a_camera_steps_games(self) -> None:
        endpoints = {"1": sc.compare_client.resolve("1"), "2": sc.compare_client.resolve("2")}
        self.assertEqual(sc.camera_games("server", endpoints), {"server": sc.SERVER})
        self.assertEqual(sc.camera_games("server,2", endpoints), {"server": sc.SERVER, "client 2": endpoints["2"]})
        self.assertEqual(list(sc.camera_games("all", endpoints)), ["server", "client 1", "client 2"])

    def test_the_fields_of_a_read_and_who_is_busy(self) -> None:
        self.assertAlmostEqual(sc.off_spot(sc.act_fields("actor=Pablo at=1127.40,989.70"), [1127.4, 988.9]), 0.8)
        self.assertIsNone(sc.off_spot(sc.act_fields("err=no Sim"), [0.0, 0.0]))
        # the right x and z, 3.83 m above the floor (Pablo back from Sierra Tango Street,): off its start
        self.assertAlmostEqual(sc.off_spot(sc.act_fields("actor=Pablo at=1127.40,988.90 above=3.83"), [1127.4, 988.9]), 3.83)
        self.assertTrue(sc.busy(sc.act_fields("was=Sit queued=0")))
        self.assertTrue(sc.busy(sc.act_fields("was=- queued=1")))
        self.assertTrue(sc.busy(sc.act_fields("was=- queued=0 stand=queued:SittingPosture")))
        self.assertFalse(sc.busy(sc.act_fields("was=- queued=0 stand=none:SittingPosture")))
        self.assertFalse(sc.busy(sc.act_fields("was=- queued=0 at=1,2")))


class PausedStack(unittest.TestCase):
    """2026-10-31 14:17-15:27Z the whole stack read Pause and two runs BLOCKED five scenarios: the reset reads every
    game's speed and resumes a paused stack through the session (DIAG_STATE and simctl mocked: no test reaches a game)."""

    PAUSED = {"commandedSpeed": "Pause", "observedSpeed": "Pause", "tick": "100"}
    RUNNING = {"commandedSpeed": "Normal", "observedSpeed": "Normal", "tick": "100"}
    C1, C2 = sc.compare_client.CLIENT1, sc.compare_client.client2()

    SIMCTL = ["scripts/simctl.py", "--timeout", "30", "speed", "normal"]

    def fakes(self, states: dict[str, dict | None], server_ticks: list[str | None], hang: bool = False):
        """diag_state answering states per endpoint (the server's later reads with server_ticks in turn, None: no
        answer), and python recording each script it is asked to run (hang: simctl runs past its process limit)."""
        self.calls: list[list[str]] = []
        self.reads: list[str] = []
        ticks = iter(server_ticks)

        def fake_state(endpoint: str) -> dict | None:
            self.reads.append(endpoint)
            if endpoint == sc.SERVER and self.reads.count(sc.SERVER) > 1:
                tick = next(ticks)
                return None if tick is None else dict(states[endpoint] or {}, tick=tick)
            return states.get(endpoint)

        def fake_python(*args: str, **kwargs) -> tuple[int, str]:
            self.calls.append(list(args))
            if hang and args[0] == "scripts/simctl.py":
                raise subprocess.TimeoutExpired([sys.executable, *args], kwargs.get("timeout"))
            return 0, "game speed set: Normal\n" if args[0] == "scripts/simctl.py" else ""

        return fake_state, fake_python

    def resume(self, states: dict[str, dict | None], server_ticks: list[str | None], spec: dict | None = None) -> str:
        fake_state, fake_python = self.fakes(states, server_ticks)
        with mock.patch.object(sc, "diag_state", fake_state), mock.patch.object(sc, "python", fake_python), \
                mock.patch.object(sc, "EXTRA_CLIENTS", []), mock.patch.object(sc, "CLIENT", self.C1), \
                mock.patch.object(sc.time, "sleep"):
            return sc.resume_paused(spec or {}, [self.C1, self.C2])

    def test_a_paused_stack_is_resumed_through_the_session_with_one_line(self) -> None:
        states = {sc.SERVER: self.PAUSED, self.C1: dict(self.RUNNING, observedSpeed="Pause"), self.C2: self.RUNNING}
        line = self.resume(states, ["100", "100", "160"])
        self.assertEqual(self.calls, [self.SIMCTL])  # once, through the session, its deadline under the process's
        self.assertEqual(line, "stack was paused (server Pause/Pause, client 1 Normal/Pause: commanded/observed); resumed: "
                               "simctl speed normal: game speed set: Normal; server tick 100 -> 160")
        self.assertEqual(self.reads, [sc.SERVER, self.C1, self.C2, sc.SERVER, sc.SERVER, sc.SERVER])

    def test_a_running_stack_gets_no_command(self) -> None:
        line = self.resume({sc.SERVER: self.RUNNING, self.C1: self.RUNNING, self.C2: None}, [])
        self.assertEqual(line, "")
        self.assertEqual(self.calls, [])
        self.assertEqual(self.reads, [sc.SERVER, self.C1, self.C2])

    def test_a_scenario_that_asks_for_pause_is_left_alone(self) -> None:
        line = self.resume({sc.SERVER: self.PAUSED, self.C1: self.PAUSED, self.C2: self.PAUSED}, [], {"speed": "Pause"})
        self.assertEqual((line, self.calls, self.reads), ("", [], []))

    def test_a_tick_that_does_not_move_after_the_resume_is_reported(self) -> None:
        line = self.resume({sc.SERVER: self.PAUSED, self.C1: self.PAUSED, self.C2: self.PAUSED}, ["100"] * sc.TICK_POLLS)
        self.assertEqual(self.calls, [self.SIMCTL])
        self.assertTrue(line.endswith("; the server's tick did not move in 10 s (tick 100)"), line)

    def test_a_game_that_does_not_answer_is_named(self) -> None:
        line = self.resume({sc.SERVER: self.PAUSED, self.C1: self.PAUSED, self.C2: None}, ["100", "130"])
        self.assertTrue(line.startswith("stack was paused (server Pause/Pause, client 1 Pause/Pause: commanded/observed; "
                                        "client 2: no answer); resumed: "), line)

    def test_a_server_tick_first_read_after_the_resume_is_not_a_move(self) -> None:
        # the server did not answer the speed read: its first answered tick is the start, the next must be higher
        states = {sc.SERVER: None, self.C1: self.PAUSED, self.C2: self.RUNNING}
        line = self.resume(states, [None, "200", "200", "260"])
        self.assertIn("; server: no answer)", line)
        self.assertTrue(line.endswith("; server tick 200 -> 260"), line)
        line = self.resume(states, ["200"] + [None] * (sc.TICK_POLLS - 1))
        self.assertTrue(line.endswith("; the server's tick did not move in 10 s (tick 200)"), line)

    def run_one(self, states: dict[str, dict | None], server_ticks: list[str | None], hang: bool = False
                ) -> tuple[dict, str, mock.MagicMock]:
        """One scenario through run_scenario on the fakes, and its report.md."""
        fake_state, fake_python = self.fakes(states, server_ticks, hang)
        spec = {"name": "chat", "summary": "", "steps": ["sleep:1"], "expect": "pass"}
        with tempfile.TemporaryDirectory() as folder, mock.patch.object(sc, "diag_state", fake_state), \
                mock.patch.object(sc, "python", fake_python), mock.patch.object(sc, "EXTRA_CLIENTS", []), \
                mock.patch.object(sc, "CLIENT", self.C1), mock.patch.object(sc, "reset", return_value="") as reset, \
                mock.patch.object(sc.time, "sleep"), mock.patch.object(sc, "quick_step", return_value=(0, "")), \
                mock.patch.object(sc, "note_stream"), mock.patch.object(sc, "WATCHED", []):
            record = sc.run_scenario(spec, Path(folder))
            sc.report(Path(folder), [record])
            body = (Path(folder) / "report.md").read_text(encoding="utf-8")
        return record, body, reset

    def test_the_line_goes_in_the_record_and_the_report_once(self) -> None:
        record, body, reset = self.run_one({sc.SERVER: self.PAUSED, self.C1: self.PAUSED, self.C2: self.RUNNING},
                                           ["100", "130"])
        reset.assert_called_once()
        self.assertTrue(record["paused"].startswith("stack was paused (server Pause/Pause, client 1 Pause/Pause"))
        self.assertTrue(record["paused"].endswith("server tick 100 -> 130"))
        self.assertIn("## speed\n" + record["paused"], record["log"])
        self.assertEqual(body.count("Before the reset (report only): " + record["paused"]), 1, body)
        self.assertEqual([c for c in self.calls if c[0] == "scripts/simctl.py"], [self.SIMCTL])

    def test_a_simctl_that_never_answers_is_recorded_and_the_scenario_goes_on(self) -> None:
        record, body, reset = self.run_one({sc.SERVER: self.PAUSED, self.C1: self.PAUSED, self.C2: self.RUNNING}, [],
                                           hang=True)
        self.assertEqual(record["paused"], "stack was paused (server Pause/Pause, client 1 Pause/Pause: "
                                           "commanded/observed); resume failed: simctl: no answer")
        reset.assert_called_once()  # the reset and the steps still ran
        self.assertIn("## step sleep:1", "\n".join(record["log"]))
        self.assertEqual(body.count("Before the reset (report only): " + record["paused"]), 1, body)

    def test_health_calls_a_paused_stack_up_with_a_speed_note(self) -> None:
        out = io.StringIO()
        with mock.patch.object(sc, "game_down", return_value=None), mock.patch.object(sc, "EXTRA_CLIENTS", []), \
                mock.patch.dict(sc.STATES, {sc.SERVER: self.PAUSED, sc.CLIENT: self.RUNNING}, clear=True), \
                mock.patch.dict(sc.HEALTH_TICKS, {sc.CLIENT: "100"}, clear=True), contextlib.redirect_stdout(out):
            self.assertIsNone(sc.health())
            self.assertEqual(sc.HEALTH_TICKS, {sc.SERVER: "100", sc.CLIENT: "100"})
        self.assertEqual(out.getvalue().strip(),
                         "health: speed: server commanded Pause, observed Pause, tick 100; client commanded Normal, "
                         "observed Normal, tick 100 (not moved since the last check) (not fatal; the reset resumes a "
                         "paused stack)")
        self.assertEqual(sc.speed_note([("server", sc.SERVER)], {sc.SERVER: self.RUNNING}, {sc.SERVER: "99"}), "")


class Households(unittest.TestCase):
    """players in separate families: a scenario's "household" (the Sims the reset holds, feeds and places), a
    "start" file for another family's spots, and "player2" (the Sim player 2's game is on, each client's camera on its
    own player's Sim)."""

    HOME = {"Jennifer": [1128.4, 988.0, 0.34, 0.94], "Pablo": [1127.4, 988.9, 0.0, 1.0]}
    LANGERAKS = {"Iliana": [1257.3, 1206.0, 0.0, 1.0]}
    C1, C2 = sc.compare_client.CLIENT1, sc.compare_client.client2()

    def files(self, tmp: str) -> Path:
        home = Path(tmp) / "home.json"
        home.write_text(json.dumps(self.HOME), encoding="utf-8")
        (Path(tmp) / "langeraks.json").write_text(json.dumps(self.LANGERAKS), encoding="utf-8")
        return home

    def test_selected_pablo_does_not_hide_wrong_authoritative_claim(self) -> None:
        import household_read

        with (
            mock.patch.object(household_read, "read", return_value={"active": {"name": "Pablo"}, "error": None}),
            mock.patch.object(household_read, "sim_id", return_value="123"),
            mock.patch.object(sc, "python", side_effect=[(0, "claims=999"), (0, "claim_error=refused")]),
        ):
            ok, _ = _ENSURE_PLAYER("2", "Pablo")
        self.assertFalse(ok, "Selection alone must not satisfy the reset's ownership gate.")

    def test_start_spots(self) -> None:
        with tempfile.TemporaryDirectory() as tmp, mock.patch.object(sc, "HOME", self.files(tmp)):
            self.assertEqual(sc.start_spots(None, sc.HOUSEHOLD), self.HOME)
            self.assertEqual(
                sc.start_spots("langeraks", ["Jennifer", "Iliana"]),
                {"Jennifer": self.HOME["Jennifer"], "Iliana": self.LANGERAKS["Iliana"]},
            )
            self.assertEqual(
                sc.start_spots({"Pablo": [1.0, 2.0, 0.0, 1.0]}, sc.HOUSEHOLD)["Pablo"], [1.0, 2.0, 0.0, 1.0]
            )
            with self.assertRaises(SystemExit):
                sc.start_spots("nowhere", sc.HOUSEHOLD)

    def test_camera_spots(self) -> None:
        both = dict(self.HOME)
        # no player 2 of its own: every game on the middle, as before
        self.assertEqual({(x, z) for _, _, x, z in sc.camera_spots(both, [self.C1, self.C2], "")}, {(1127.9, 988.45)})
        two_lots = {"Jennifer": self.HOME["Jennifer"], "Iliana": self.LANGERAKS["Iliana"]}
        spots = sc.camera_spots(two_lots, [self.C1, self.C2], "Iliana")
        self.assertEqual(spots[0], ("server", "Jennifer", 1192.85, 1097.0))
        self.assertEqual(spots[1], (self.C1, "Jennifer", 1128.4, 988.0))
        self.assertEqual(spots[2], (self.C2, "Iliana", 1257.3, 1206.0))

    def test_player2_for(self) -> None:
        self.assertEqual(sc.player2_for({"player2": "Iliana"}, ["1"]), "Iliana")
        self.assertEqual(sc.player2_for({}, ["1", "2"]), "Pablo")
        self.assertEqual(sc.player2_for({}, ["1"]), "")

    def test_players_for_three_players(self) -> None:
        """"players" names each later player's Sim; "player2" is player 2's short form; player 3 has no
        default Sim."""
        spec = {"players": {"2": "Iliana", "3": "Gunther"}}
        self.assertEqual(sc.players_for(spec, ["1", "2", "3"]), {"2": "Iliana", "3": "Gunther"})
        self.assertEqual(sc.players_for({"player2": "Iliana"}, ["1", "2", "3"]), {"2": "Iliana"})
        self.assertEqual(sc.players_for({}, ["1", "2", "3"]), {"2": "Pablo"})
        self.assertEqual(
            sc.spec_players({"player2": "Iliana", "players": {"3": "Gunther"}}), {"3": "Gunther", "2": "Iliana"}
        )

    def test_cameras_on_three_players_own_sims(self) -> None:
        spots = {"Jennifer": [10.0, 20.0, 0, 1], "Iliana": [14.0, 20.0, 0, 1], "Gunther": [12.0, 24.0, 0, 1]}
        with mock.patch.dict(os.environ, {}, clear=False):
            for key in ("SIMSONLINE_CLIENT2_BRIDGECTL", "SIMSONLINE_CLIENT3_BRIDGECTL"):
                os.environ.pop(key, None)
            placed = sc.camera_spots(
                spots, ["127.0.0.1:34030", "127.0.0.1:34031", "127.0.0.1:34032"], {"2": "Iliana", "3": "Gunther"}
            )
        self.assertEqual(
            [(g, sim) for g, sim, _, _ in placed],
            [
                ("server", "Jennifer"),
                ("127.0.0.1:34030", "Jennifer"),
                ("127.0.0.1:34031", "Iliana"),
                ("127.0.0.1:34032", "Gunther"),
            ],
        )
        self.assertEqual(placed[3][2:], (12.0, 24.0))

    @mock.patch.object(sc, "ensure_player_claim", return_value=(True, "claims=123"))
    def test_player2_selection_drift_restores_pablo_without_rejoining(self, _claim) -> None:
        import household_read

        reads = [{"active": {"name": name}, "error": None} for name in ("Alex", "Pablo")]
        with (
            mock.patch.object(household_read, "read", side_effect=reads),
            mock.patch.object(household_read, "sim_id", return_value="123"),
            mock.patch.object(sc, "python", return_value=(0, "OK")) as bridge,
            mock.patch.object(sc, "quick_step", return_value=(1, "must not reboot")) as rejoin,
        ):
            ok, _ = _ENSURE_PLAYER("2", "Pablo")
        self.assertTrue(ok)
        bridge.assert_called_once_with(
            "scripts/bridgectl.py", "--endpoint", self.C2, "SET_ACTIVE_SIM", "123", timeout=30
        )
        rejoin.assert_not_called()

    @mock.patch.object(sc, "ensure_player_claim", return_value=(True, "claims=123"))
    @mock.patch.object(sc, "reselect_player", return_value=False)
    def test_ensure_player2_reads_the_sim_it_joined_and_tries_once_more(self, _reselect, _claim) -> None:
        # a rejoin that answers ok can still leave the game on the Sim it was (run 20261101-035827: two-social
        # came back BLOCKED on player 2 with actor=Sandi French), so the read after the rejoin decides, twice at most.
        import household_read

        on = {"active": {"name": "Pablo"}, "error": None}
        joined = {"active": {"name": "Iliana"}, "error": None}
        with (
            mock.patch.object(household_read, "read", return_value=on),
            mock.patch.object(sc, "quick_step", return_value=(0, "client 2 claimed in 95s")) as step,
        ):
            self.assertEqual(_ENSURE_PLAYER("2", "Pablo"), (True, "player 2 on Pablo"))
            step.assert_not_called()
        with (
            mock.patch.object(household_read, "read", side_effect=[on, joined]),
            mock.patch.object(sc, "quick_step", return_value=(0, "client 2 claimed in 95s")) as step,
        ):
            ok, text = _ENSURE_PLAYER("2", "Iliana")
        step.assert_called_once_with("rejoin:2:as=Iliana")
        self.assertTrue(ok)
        self.assertIn("player 2 on Pablo, joined again as Iliana", text)
        self.assertIn("player 2 now on Iliana", text)
        # still another Sim after the second rejoin: the reset fails, and with it the scenario never runs
        with (
            mock.patch.object(household_read, "read", return_value=on),
            mock.patch.object(sc, "quick_step", return_value=(0, "client 2 claimed in 95s")) as step,
        ):
            ok, text = _ENSURE_PLAYER("2", "Nobody")
        self.assertEqual(step.call_count, 2)
        self.assertFalse(ok)
        self.assertTrue(text.endswith("player 2 now on Pablo"))
        # a rejoin that fails is not retried: the same error twice says the game will not take that Sim
        with (
            mock.patch.object(household_read, "read", return_value=on),
            mock.patch.object(sc, "quick_step", return_value=(1, "no Sim named Nobody in the server's world")) as step,
        ):
            ok, text = _ENSURE_PLAYER("2", "Nobody")
        self.assertEqual(step.call_count, 1)
        self.assertFalse(ok)
        self.assertTrue(text.endswith("player 2 now on Pablo"))

    @mock.patch.object(sc, "ensure_player_claim", return_value=(True, "claims=123"))
    @mock.patch.object(sc, "reselect_player", return_value=False)
    def test_ensure_player_holds_that_game_in_the_watchdog_while_it_joins_again(self, _reselect, _claim) -> None:
        # 2026-11-01 (n14 live run 20261101-001044): player 2's rejoin as Iliana before park-menu-parity took longer than
        # the bridge check's 94 s and the run read CRASHED with the game alive; a rejoin step was held, this join was not.
        import household_read

        held: list[tuple[str, ...]] = []

        class Dog:
            @contextlib.contextmanager
            def hold(self, *endpoints):
                held.append(endpoints)
                yield

        on = {"active": {"name": "Pablo"}, "error": None}
        joined = {"active": {"name": "Iliana"}, "error": None}
        endpoint = sc.compare_client.client_n("2")
        with mock.patch.object(household_read, "read", return_value=on), mock.patch.object(sc, "WATCH", Dog()), \
                mock.patch.object(sc, "quick_step", return_value=(0, "client 2 claimed in 95s")):
            self.assertEqual(_ENSURE_PLAYER("2", "Pablo"), (True, "player 2 on Pablo"))
        self.assertEqual(held, [])  # already on that Sim: nothing restarts, nothing held
        # re-reads the join, so a read that still says Pablo costs a second try, and every rejoin holds the game
        held.clear()
        with mock.patch.object(household_read, "read", side_effect=[on, joined]), mock.patch.object(sc, "WATCH", Dog()), \
                mock.patch.object(sc, "quick_step", return_value=(0, "client 2 claimed in 95s")):
            self.assertTrue(_ENSURE_PLAYER("2", "Iliana")[0])
        self.assertEqual(held, [(endpoint,)])
        held.clear()
        with mock.patch.object(household_read, "read", side_effect=[on, on, joined]), mock.patch.object(sc, "WATCH", Dog()), \
                mock.patch.object(sc, "quick_step", return_value=(0, "client 2 claimed in 95s")):
            self.assertTrue(_ENSURE_PLAYER("2", "Iliana")[0])
        self.assertEqual(held, [(endpoint,)] * 2)  # the retry holds too
        held.clear()
        with mock.patch.object(household_read, "read", side_effect=[on, joined]), mock.patch.object(sc, "WATCH", None), \
                mock.patch.object(sc, "quick_step", return_value=(0, "client 2 claimed in 95s")):
            self.assertTrue(_ENSURE_PLAYER("2", "Iliana")[0])  # no watchdog (SIMSONLINE_RUN_WATCHDOG=0): no hold
        self.assertEqual(held, [])

    def test_the_server_s_selected_sim_goes_back_to_jennifer(self) -> None:
        # a save left on another family made Jennifer an NPC on the server, sent home from a community lot.
        import household_read
        calls = []

        def fake_python(script, *args, timeout=None):
            calls.append([script, *args])
            return 0, "OK"

        with mock.patch.object(sc, "python", fake_python), mock.patch.object(household_read, "sim_id", return_value="180"):
            self.assertEqual(_SELECT_SERVER_SIM(), "server selected: Jennifer (OK)")
        self.assertEqual(calls, [["scripts/bridgectl.py", "--endpoint", sc.SERVER, "SET_ACTIVE_SIM", "180"]])
        with mock.patch.object(household_read, "sim_id", side_effect=SystemExit("no Sim named Jennifer")):
            self.assertIn("not found", _SELECT_SERVER_SIM())

    def test_the_reset_holds_the_scenario_s_household_and_each_camera_follows_its_player(self) -> None:
        calls: list[list[str]] = []
        cameras: list[tuple] = []

        def fake_python(script: str, *args: str, **kwargs) -> tuple[int, str]:
            calls.append(list(args))
            if "--place-all" in args:
                return 0, "placed=Jennifer,Iliana"
            return 0, f"actor={args[0]} was=- queued=0 autonomyOff=1 at=0,0"

        def fake_camera(on: dict[str, str], target: str, distance: float = 1.0, level: str = "") -> list[str]:
            cameras.append((list(on), target, level))
            return [f"{side}: focused = {target}" for side in on]

        with tempfile.TemporaryDirectory() as tmp, mock.patch.object(sc, "HOME", self.files(tmp)), \
                mock.patch.object(sc, "python", fake_python), mock.patch.object(sc.time, "sleep"), \
                mock.patch.object(sc, "camera", fake_camera):
            sc.reset("langeraks", [self.C1, self.C2], household=["Jennifer", "Iliana"], player2="Iliana")
        self.assertEqual(calls[0][:2], ["Jennifer", "--clear"])
        self.assertEqual(calls[0][calls[0].index("--hold") + 1], "Jennifer,Iliana")
        self.assertEqual(calls[0][calls[0].index("--needs") + 1], "static:Jennifer,Iliana")
        self.assertEqual(calls[1][:2], ["Iliana", "--clear"])
        place = calls[2][calls[2].index("--place-all") + 1]
        self.assertIn("Iliana=1257.300,1206.000", place)
        self.assertNotIn("Pablo", place)
        self.assertEqual(cameras, [(["server"], "1192.85,1097.00", "Jennifer"), (["client 1"], "1128.40,988.00", "Jennifer"),
                                   (["client 2"], "1257.30,1206.00", "Iliana")])

    def test_a_player_2_that_never_lands_on_its_sim_fails_the_reset_before_it(self) -> None:
        # a player that is not on the Sim the scenario is about is the reset's failure, not the scenario's
        spec = {"name": "fam", "summary": "", "clients": [1, 2], "player2": "Iliana", "steps": ["sleep:1"], "expect": "pass"}
        with tempfile.TemporaryDirectory() as folder, \
                mock.patch.object(sc, "ensure_player", return_value=(False, "player 2 on Pablo, joined again as Iliana\n"
                                                                             "client2 controller failed: x\n"
                                                                             "player 2 now on Pablo")), \
                mock.patch.object(sc, "reset") as reset:
            record = sc.run_scenario(spec, Path(folder))
        reset.assert_not_called()
        self.assertEqual(record["verdict"], "RESET-FAILED")
        self.assertEqual(record["failures"], ["reset: player 2 not on Iliana: player 2 now on Pablo"])

    def test_households_check_names_what_a_client_reads_differently(self) -> None:
        def reading(funds: str, pair: str, error: str | None = None) -> dict:
            return {"active": None, "error": error, "sims": {},
                    "households": {"Langerak": {"funds": funds, "members": [], "active": False}},
                    "pairs": {"Jennifer+Iliana": pair}}

        by_endpoint = {sc.SERVER: reading("4056", "14.0 Acquaintance"), "c1": reading("4056", "none"),
                       "c2": reading("3400", "14.0 Acquaintance")}
        with mock.patch("household_read.read", side_effect=lambda e, names, sims, pairs: by_endpoint[e]) as read:
            diffs, text = sc.households_check("Langerak --pair Jennifer+Iliana", {"1": "c1", "2": "c2"}, ["1", "2"])
        self.assertEqual(read.call_args.args[1:], (["Langerak"], None, ["Jennifer+Iliana"]))
        self.assertEqual(diffs, ["client 1: Jennifer+Iliana none (server 14.0 Acquaintance)",
                                 "client 2: Langerak funds 3400 (server 4056)"])
        self.assertIn("client 2: selected -", text)
        self.assertEqual(sc.household_diffs({"server": reading("1", "x"), "client 1": reading("1", "x", "err=y")},
                                            ["Langerak"], []), ["client 1: read failed: err=y"])

    def test_households_as_a_step_and_an_after_check(self) -> None:
        spec = {"name": "buy", "summary": "", "clients": [1, 2], "steps": ["households:Langerak", "sleep:1"],
                "after": ["households:Langerak"], "expect": "pass"}
        answers = iter([([], "server: selected -"), (["client 2: Langerak funds 3400 (server 3906)"], "server: selected -")])
        with tempfile.TemporaryDirectory() as folder, mock.patch.object(sc, "python", return_value=(0, "")), \
                mock.patch.object(sc, "reset", return_value=""), mock.patch.object(sc.time, "sleep"), \
                mock.patch.object(sc, "quick_step", return_value=(0, "")), mock.patch.object(sc, "note_stream"), \
                mock.patch.object(sc, "households_check", side_effect=lambda rest, endpoints, watched: next(answers)) as check:
            record = sc.run_scenario(spec, Path(folder))
        self.assertEqual([c.args[0] for c in check.call_args_list], ["Langerak", "Langerak"])
        self.assertEqual(check.call_args.args[2], ["1", "2"])
        self.assertEqual(record["failures"], ["households: client 2: Langerak funds 3400 (server 3906)"])


class ResetGate(unittest.TestCase):
    """the reset's own last check before a scenario starts, and the RESET-FAILED verdict it gives. About 14 of
    the 30 failures in the nine runs before it came from what the previous scenario left (a rejoin that landed the game
    on another Sim, a trip still running, a bystander conversation nobody ended, a queue the reset never cleared), and
    the scenario was judged for it. One check now names it, the steps never run, and the reason is the reset's."""

    NAMES = ["Gunther", "Iliana", "Jennifer", "Pablo"]  # what scratch/ConversationProbe.cs reads
    C1, C2 = sc.compare_client.CLIENT1, sc.compare_client.client2()

    @staticmethod
    def probe(fields: dict[str, str]) -> str:
        """bridgectl's print of a probe reply: a loaded= line, then one 'key = value' line per field, the keys padded to
        the widest one."""
        width = max(len(key) for key in fields)
        return "loaded=Probe  type=SimsOnline.Scratch.Probe\n" + "".join(
            f"  {key.ljust(width)} = {value}\n" for key, value in fields.items())

    @classmethod
    def conversations(cls, held: dict[str, str]) -> str:
        """scratch/ConversationProbe.cs's reply: Sim -> the members of the conversation it holds, none for a Sim that
        holds nothing."""
        return cls.probe({"ok": "true", "stale": "0", "streamBuilt": "0",
                          "convs": ",".join(f"{n}:{held.get(n, 'none')}" for n in cls.NAMES)})

    @staticmethod
    def state(name: str, was: str = "-", queued: str = "0", stand: str = "") -> str:
        """sim_act.py NAME --stand's summary line; stand is only there when the Sim is not on its feet."""
        return (f"actor={name} was={was} queued={queued} autonomyOff=1 at=1128.4,988.0"
                + (f" stand={stand}" if stand else ""))

    def gate(self, sims: dict[str, list[str]], active: dict[str, list[dict]], convs: dict[str, list[str]],
             household: list[str] | None = None, players: dict[str, str] | None = None,
             clients: list[str] | None = None) -> tuple[list[str], list[str], list[str], list[str]]:
        """reset_check over those reads (the last reply of a queue repeats): (problems, log, settles, server re-asks).
        setUpModule stubs scenarios.reset_check out for the other tests, so this one calls the saved real function."""
        import household_read
        reads, by_endpoint, selected = ({n: list(r) for n, r in sims.items()},
                                        {e: list(r) for e, r in convs.items()},
                                        {e: list(r) for e, r in active.items()})
        settled: list[str] = []
        asked: list[str] = []
        self.sim_reads: list[tuple[str, ...]] = []

        def fake_python(script: str, *args: str, timeout=None) -> tuple[int, str]:
            if script == "scripts/sim_act.py":
                self.sim_reads.append(args)
                queue = reads[args[0]]
                return 0, queue.pop(0) if len(queue) > 1 else queue[0]
            if script == "scripts/bridgectl.py":
                endpoint, path = args[args.index("--endpoint") + 1], args[-1]
                if path == sc.CONV_END:  # the server-side driver: it always answers ok here
                    return 0, self.probe({"ok": "true"})
                queue = by_endpoint.get(endpoint, [])
                return 0, (queue.pop(0) if len(queue) > 1 else queue[0]) if queue else ""
            raise AssertionError(f"the reset gate read {script} {args[:2]}")

        def read(endpoint, names, sims_arg=None, pairs=None):
            queue = selected.get(endpoint, [])
            return {"active": None, "error": "no answer"} if not queue else (queue.pop(0) if len(queue) > 1 else queue[0])

        def select(name: str = sc.SERVER_SELECTED) -> str:
            asked.append(name)
            return f"server selected: {name} (OK)"

        with mock.patch.object(sc, "python", fake_python), mock.patch.object(household_read, "read", side_effect=read), \
                mock.patch.object(sc, "settle", side_effect=lambda name, limit=None, moving=None: settled.append(name)), \
                mock.patch.object(sc, "select_server_sim", select), mock.patch.object(sc.time, "sleep"):
            problems, log = _RESET_CHECK(household or ["Jennifer", "Pablo"], players or {"2": "Iliana"},
                                         clients if clients is not None else [self.C1, self.C2])
        return problems, log, settled, asked

    def clean(self, **kwargs) -> tuple[list[str], list[str], list[str], list[str]]:
        idle = {n: [self.state(n)] for n in ("Jennifer", "Pablo")}
        on = {"active": {"name": "Jennifer"}, "error": None}
        iliana = {"active": {"name": "Iliana"}, "error": None}
        args = {"sims": idle, "active": {sc.SERVER: [on], self.C2: [iliana]},
                "convs": {e: [self.conversations({})] for e in (sc.SERVER, self.C1, self.C2)}}
        args.update(kwargs)
        return self.gate(**args)

    def test_a_reset_that_left_nothing_says_so_and_fails_nothing(self) -> None:
        problems, log, settled, asked = self.clean()
        self.assertEqual(problems, [])
        self.assertEqual((settled, asked), ([], []))  # nobody had to be waited for, the server was already on Jennifer
        for line in ("server selected: Jennifer", "player 2 selected: Iliana", "Jennifer: idle and standing",
                     "Pablo: idle and standing", "conversations client 1: none held"):
            self.assertIn(line, log)

    def test_a_trip_the_scenario_left_running_is_named_as_a_trip(self) -> None:
        # the vehicle trip outlived the interaction that started it: the next scenario would begin on the road
        problems, log, settled, _ = self.clean(
            sims={"Jennifer": [self.state("Jennifer")],
                  "Pablo": [self.state("Pablo", was="DriveToCommunityLot"), self.state("Pablo", was="DriveToCommunityLot")]})
        self.assertEqual(problems, ["reset: Pablo: a trip (DriveToCommunityLot) still running"])
        self.assertEqual(settled, ["Pablo"])  # asked off once, waited for, read again
        self.assertIn("Pablo: a trip (DriveToCommunityLot) still running", log)

    def test_a_sim_still_on_its_seat_bed_or_pool_is_named_with_the_posture(self) -> None:
        # a posture with no interaction left: sim_act asks the Sim to stand and reports what it is still in
        problems, _, _, _ = self.clean(sims={"Jennifer": [self.state("Jennifer")],
                                              "Pablo": [self.state("Pablo", stand="none:SittingInVehicle")]})
        self.assertEqual(problems, ["reset: Pablo: a trip in SittingInVehicle (stand=none:SittingInVehicle)"])
        # SwimmingInPool is what makes the engine hand out Social_TargetInPool to the next interaction
        problems, _, _, _ = self.clean(sims={"Jennifer": [self.state("Jennifer")],
                                              "Pablo": [self.state("Pablo", stand="none:SwimmingInPool")]})
        self.assertEqual(problems, ["reset: Pablo: left in SwimmingInPool (stand=none:SwimmingInPool)"])
        # stand=queued:... is the game never running the stand-up it was given
        problems, _, _, _ = self.clean(sims={"Jennifer": [self.state("Jennifer", stand="queued:Standing")],
                                              "Pablo": [self.state("Pablo")]})
        self.assertEqual(problems, ["reset: Jennifer: left in Standing (stand=queued:Standing)"])

    def test_the_last_stand_up_is_waited_for_and_the_posture_read_again_without_asking(self) -> None:
        #, run 20261101-081111 park-menu-parity: the read that asks the last time queues its own stand-up and
        # still sees the posture; judged on that read, Jennifer was 'left in SocializingPosture' with her stand-up queued
        queued = self.state("Jennifer", stand="queued:SocializingPosture") + " posture=SocializingPosture"
        problems, log, settled, _ = self.clean(sims={"Jennifer": [queued, queued, self.state("Jennifer")],
                                                     "Pablo": [self.state("Pablo")]})
        self.assertEqual(problems, [])
        self.assertIn("Jennifer: idle and standing", log)
        self.assertEqual(settled, ["Jennifer", "Jennifer"])  # the first try, then the last try's own stand-up
        jennifer = [args for args in self.sim_reads if args[0] == "Jennifer"]
        self.assertEqual(["--stand" in args for args in jennifer], [True, True, False])  # the check read asks nothing

    def test_a_posture_the_last_stand_up_did_not_leave_still_fails_the_reset(self) -> None:
        queued = self.state("Jennifer", stand="queued:SocializingPosture") + " posture=SocializingPosture"
        still = self.state("Jennifer") + " posture=SocializingPosture"
        problems, _, _, _ = self.clean(sims={"Jennifer": [queued, queued, still], "Pablo": [self.state("Pablo")]})
        self.assertEqual(problems, ["reset: Jennifer: left in SocializingPosture (its stand-up ran and it did not get up)"])

    def test_a_queue_the_reset_never_cleared_blocks_the_scenario_from_its_first_step(self) -> None:
        problems, _, settled, _ = self.clean(
            sims={"Jennifer": [self.state("Jennifer", queued="2"), self.state("Jennifer", queued="2")],
                  "Pablo": [self.state("Pablo")]})
        self.assertEqual(problems, ["reset: Jennifer: 2 interaction(s) queued"])
        self.assertEqual(settled, ["Jennifer"])

    def test_a_bystander_conversation_is_ended_before_the_scenario_starts(self) -> None:
        # run 20261101-035827: Gunther was still talking with Sue and conv-client-leave asserts Gunther:none
        held, none = self.conversations({"Gunther": "Gunther+Sue"}), self.conversations({})
        problems, log, _, _ = self.clean(convs={sc.SERVER: [held, none], self.C1: [none], self.C2: [none]})
        self.assertEqual(problems, [])
        self.assertIn("conversations server: Gunther in Gunther+Sue", log)
        self.assertIn("conversations: the server ended them", log)  # the clients' copies follow the server's Sims

    def test_a_conversation_the_end_left_behind_fails_the_reset(self) -> None:
        held, none = self.conversations({"Gunther": "Gunther+Sue"}), self.conversations({})
        household = ["Jennifer", "Pablo", "Gunther"]  # conv-client-leave's own Gunther
        problems, log, _, _ = self.clean(convs={sc.SERVER: [held, none], self.C1: [none], self.C2: [held]},
                                         sims={n: [self.state(n)] for n in household}, household=household)
        self.assertEqual(problems, ["reset: Gunther still holds a conversation (Gunther+Sue) on client 2"])
        self.assertIn("conversations server: Gunther in Gunther+Sue", log)

    def test_a_conversation_left_on_a_sim_the_scenario_never_names_is_logged_not_failed(self) -> None:
        # run 20261101-073915: client 1's Gunther kept Gunther+VJ after the server's went to work; social-chat
        # (Jennifer and Pablo) RESET-FAILED on it
        held, none = self.conversations({"Gunther": "Gunther+VJ"}), self.conversations({})
        problems, log, _, _ = self.clean(convs={sc.SERVER: [none], self.C1: [held], self.C2: [none]})
        self.assertEqual(problems, [])
        self.assertIn("conversations client 1 after the end: Gunther in Gunther+VJ (not this scenario's Sims)", log)
        # a player's Sim counts as the scenario's own
        problems, _, _, _ = self.clean(convs={sc.SERVER: [none], self.C1: [held], self.C2: [none]},
                                       players={"2": "Gunther"})
        self.assertIn("reset: Gunther still holds a conversation (Gunther+VJ) on client 1", problems)

    def test_the_server_on_another_sim_is_asked_once_more_before_it_fails_the_reset(self) -> None:
        goth, martinez = {"active": {"name": "Gunther"}, "error": None}, {"active": {"name": "Jennifer"}, "error": None}
        problems, _, _, asked = self.clean(active={sc.SERVER: [goth, martinez], self.C2: [{"active": {"name": "Iliana"},
                                                                                          "error": None}]})
        self.assertEqual(problems, [])
        self.assertEqual(asked, ["Jennifer"])  # select_server_sim is the retry; a second read decides
        problems, _, _, asked = self.clean(active={sc.SERVER: [goth], self.C2: [{"active": {"name": "Iliana"},
                                                                                "error": None}]})
        self.assertEqual(asked, ["Jennifer"])
        self.assertEqual(problems, ["reset: the server plays Gunther, not Jennifer"])

    def test_a_player_s_game_on_another_sim_is_the_reset_s_failure(self) -> None:
        # the case the brief names: two-social BLOCKED with actor=Sandi French after a rejoin that answered ok
        problems, log, _, asked = self.clean(active={sc.SERVER: [{"active": {"name": "Jennifer"}, "error": None}],
                                                     self.C2: [{"active": {"name": "Sandi"}, "error": None}]})
        self.assertEqual(problems, ["reset: player 2's game plays Sandi, not Iliana"])
        self.assertEqual(asked, [])  # that game is the player's own; the server's selection is not the problem
        self.assertIn("player 2 selected: Sandi", log)

    def test_a_game_that_does_not_answer_is_logged_and_left_to_the_health_check(self) -> None:
        # a dead stack is CRASHED or BLOCKED, never a reset that failed: an unread read says so and costs nothing
        problems, log, _, _ = self.clean(sims={"Jennifer": [""], "Pablo": [""]}, active={}, convs={})
        self.assertEqual(problems, [])
        self.assertIn("server selected: (no answer: no answer)", log)
        self.assertIn("conversations server: not read", log)
        self.assertIn("Jennifer: not read", log)

    def test_the_new_verdict_reads_back_from_a_report_row(self) -> None:
        row = sc.ROW.match("| two-social | RESET-FAILED | pass | reset: Gunther still holds a conversation "
                           "(Gunther+Sue) on client 2 |")
        self.assertIsNotNone(row)
        self.assertEqual(row.group(2), "RESET-FAILED")
        self.assertEqual(sc.CODES["RESET-FAILED"], "R")
        self.assertIn("RESET-FAILED", sc.NOT_RUN)  # it never started: no exposure minutes, nothing to rescore

    def test_a_scenario_whose_reset_check_failed_never_starts(self) -> None:
        spec = {"name": "fam", "summary": "", "clients": [1, 2], "steps": ["sleep:1"], "expect": "pass"}
        why = ["reset: Pablo: a trip in SittingInVehicle (stand=none:SittingInVehicle)"]
        with tempfile.TemporaryDirectory() as folder, mock.patch.object(sc, "reset", return_value="reset ok"), \
                mock.patch.object(sc, "reset_check", return_value=(why, ["Pablo: a trip in SittingInVehicle"])) as check, \
                mock.patch.object(sc, "quick_step") as step:
            record = sc.run_scenario(spec, Path(folder))
        step.assert_not_called()
        self.assertEqual(check.call_args.args, (["Jennifer", "Pablo"], {"2": "Pablo"}, [self.C1, self.C2]))
        self.assertEqual(record["verdict"], "RESET-FAILED")
        self.assertEqual(record["failures"], why)  # the reset's reasons, not the scenario's
        self.assertIn("## reset check\nPablo: a trip in SittingInVehicle", "\n".join(record["log"]))
        self.assertNotIn("total", record["timings"])  # no scenario minutes in the crash exposure
        self.assertIn("reset", record["timings"])  # its own phase is still booked


class CameraStep(unittest.TestCase):
    """camera:<games>:<Sim or x,z>[:<distance>] points those games' cameras there; a game that did not answer blocks."""

    def run_steps(self, steps: list[str], replies: list[str]) -> tuple[dict, list[tuple]]:
        spec = {"name": "cam", "summary": "", "clients": [1, 2], "steps": steps, "expect": "pass"}
        calls: list[tuple] = []

        def fake_camera(on: dict[str, str], target: str, distance: float = 1.0) -> list[str]:
            calls.append((sorted(on), target, distance))
            return replies

        with tempfile.TemporaryDirectory() as folder, mock.patch.object(sc, "python", return_value=(0, "")), \
                mock.patch.object(sc, "reset", return_value=""), mock.patch.object(sc.time, "sleep"), \
                mock.patch.object(sc, "camera", fake_camera), mock.patch.object(sc, "quick_step", return_value=(0, "")), \
                mock.patch.object(sc, "note_stream"):
            record = sc.run_scenario(spec, Path(folder))
        return record, calls

    def test_the_server_away_from_the_household(self) -> None:
        record, calls = self.run_steps(["camera:server:1139.3,984.8", "sleep:1"], ["server: focused = 1139.30,984.80"])
        self.assertEqual(calls, [(["server"], "1139.3,984.8", 1.0)])
        self.assertNotEqual(record["verdict"], "BLOCKED")
        self.assertIn("## step camera:server:1139.3,984.8\nserver: focused = 1139.30,984.80", record["log"])

    def test_a_distance_and_a_game_that_did_not_answer(self) -> None:
        record, calls = self.run_steps(["camera:all:Jennifer:6", "sleep:1"], ["server: focused = Jennifer", "client 2: no reply"])
        self.assertEqual(calls, [(["client 1", "client 2", "server"], "Jennifer", 6.0)])
        self.assertEqual(record["verdict"], "BLOCKED")
        self.assertEqual(record["failures"], ["step camera:all:Jennifer:6: client 2: no reply"])

    def test_the_out_of_view_scenario_is_two_hud_with_the_server_away(self) -> None:
        away = json.loads((sc.SCENARIOS / "two-hud-server-away.json").read_text(encoding="utf-8"))
        hud = json.loads((sc.SCENARIOS / "two-hud.json").read_text(encoding="utf-8"))
        self.assertEqual(away["steps"][0], "camera:server:1139.3,984.8")
        self.assertEqual(away["steps"][1:], hud["steps"])
        # Known-failing until SimLink's on-screen walk (native version 12, 2b80e37a): now it expects what two-hud does.
        self.assertEqual(away["expect"], hud["expect"])


class PatchLine(unittest.TestCase):
    """The run's first line on SimLink's patches: every game must report the same on-screen walk."""

    ON = "replayDrivers=2;chunkReserve=on,installed=1,site=0,sites=3;onScreen=on,installed=1,site=0,sites=1;x=1"

    def test_games_that_agree(self) -> None:
        line = sc.patch_line({"server": self.ON, "client1": self.ON})
        self.assertEqual(line, "native patches: server onScreen=on chunkReserve=on; client1 onScreen=on chunkReserve=on")

    def test_a_game_without_the_walk_is_flagged(self) -> None:
        off = "replayDrivers=2;chunkReserve=on,installed=1,site=0,sites=3;onScreen=off,installed=2,site=0,sites=0"
        line = sc.patch_line({"server": self.ON, "client1": off, "client2": None})
        self.assertIn("client1 onScreen=off chunkReserve=on", line)
        self.assertIn("client2 no answer", line)
        self.assertTrue(line.endswith("-- WARNING: onScreen differ between games"))

    def test_a_game_with_no_ring_is_not_ready_rather_than_off(self) -> None:
        # stackctl boot-client2 without quick_loop's CLIENT_ENV: no SimLink ring, NativeVersion 0, every patch ready=0.
        bare = "replayDrivers=2;chunkReserve=off,ready=0;onScreen=off,ready=0;x=1"
        line = sc.patch_line({"server": self.ON, "client1": self.ON, "client2": bare})
        self.assertIn("client2 onScreen=not ready (no client ring?) chunkReserve=not ready (no client ring?)", line)
        self.assertNotIn("WARNING", line)


class RoseCheck(unittest.TestCase):
    """rose:<side>:<bridge command>:<counter> (copy-flip-resend): a cumulative counter must rise during the scenario."""

    STATUS = ("picks=on,ready=1;params=12/adopt:3/empty:0/failed:0/resent:7;authority=x;"
              "seedCopy=on,ready=1,spareResent=4,reseeded=2,spareOnly=0")

    def test_counter_value_reads_key_equals_and_key_colon(self) -> None:
        self.assertEqual(sc.counter_value(self.STATUS, "reseeded"), 2)
        self.assertEqual(sc.counter_value(self.STATUS, "resent"), 7)  # not spareResent's 4
        self.assertEqual(sc.counter_value(self.STATUS, "spareResent"), 4)
        self.assertIsNone(sc.counter_value(self.STATUS, "missing"))

    def test_rose_failure_wants_a_rise(self) -> None:
        self.assertIsNone(sc.rose_failure("reseeded", 0, 3))
        self.assertEqual(sc.rose_failure("reseeded", 3, 3), "reseeded did not rise (3 -> 3)")
        self.assertEqual(sc.rose_failure("reseeded", None, 3), "no reseeded counter before the steps")
        self.assertEqual(sc.rose_failure("reseeded", 1, None), "no reseeded counter after them")

    def test_the_scenario_parses(self) -> None:
        spec = sc.load(["copy-flip-resend"])[0]
        roses = [c for c in spec["after"] if c.startswith("rose:")]
        self.assertEqual([c.rsplit(":", 1)[1] for c in roses], ["reseeded", "resent"])
        pattern = next(c for c in spec["after"] if c.startswith("assert:")).split(":", 3)[3]
        self.assertTrue(re.search(pattern, "seedCoverage=active:9,seeded:4,spareOnly:0,borrowed:5,borrowedSpare:5,unseeded:0"))
        self.assertFalse(re.search(pattern, "seedCoverage=active:9,unseeded:01"))

    def test_stand_walk_keeps_the_unrepaired_driver_finding_open(self) -> None:
        spec = sc.load(["idle-repair-stand-walk"])[0]
        self.assertEqual(spec["steps"][0], "premise:1:borrowed-idle:Jennifer")
        self.assertIn("rose:client:SACS_RECORDER status:idleRepairStream", spec["after"])
        self.assertTrue(spec["expect"].startswith("known-failing:"))
        self.assertIn("20261110-204629", spec["expect"])
        self.assertIn("stream never repaired", spec["expect"])


class TuneSides(unittest.TestCase):
    def test_clients_sets_each_players_game_and_puts_both_back(self) -> None:
        # walk.start-lead-ms acts on each copy: a two-player run sets it on both clients, each through its own side.
        sides = []

        def fake_python(script, *args, timeout=None):
            sides.append(args[1])
            return 0, "walk.start-lead-ms=0 (default 0)" if len(args) == 3 else "walk.start-lead-ms=40 (default 0)"

        with mock.patch.object(sc, "python", side_effect=fake_python):
            restore, problems = sc.apply_tunes(["clients:walk.start-lead-ms=40"])
        self.assertEqual(problems, [])
        self.assertEqual([r[0] for r in restore], [sc.compare_client.CLIENT1, sc.compare_client.client2()])
        self.assertEqual(set(sides), {sc.side_of(sc.compare_client.CLIENT1), sc.side_of(sc.compare_client.client2())})
        self.assertIn("client2", sides)

    def test_an_unknown_side_is_a_problem(self) -> None:
        with mock.patch.object(sc, "python") as calls:
            _, problems = sc.apply_tunes(["players:walk.start-lead-ms=40"])
        self.assertEqual(calls.call_count, 0)
        self.assertIn("not [server:|client:|both:|client2:|clients:|all:]name=value", problems[0])


class StepVariables(unittest.TestCase):
    """A probe's var_<name>= reply field sets {name} for the later steps and checks (two-paper-refused's {paper})."""

    def test_reads_what_a_probe_step_printed(self):
        text = ("[   0.4s] probe server scratch/PaperPlaceProbe.cs: ok=true;var_paper=11539349195621275120;made=True\n"
                "[   0.4s] var paper=11539349195621275120\n[   0.4s] done\n")
        self.assertEqual(sc.step_vars(text), {"paper": "11539349195621275120"})
        self.assertEqual(sc.step_vars("[   0.3s] done\n"), {})

    def test_expands_known_names_only(self):
        variables = {"paper": "1153"}
        self.assertEqual(sc.expand("cpick:2:id:{paper} GetNewspaperChooser --name Weather", variables),
                         "cpick:2:id:1153 GetNewspaperChooser --name Weather")
        self.assertEqual(sc.expand("menudiff:2:Pablo:id:{paper}:only={other}", variables), "menudiff:2:Pablo:id:1153:only={other}")
        self.assertEqual(sc.expand("sleep:3", {}), "sleep:3")


class RunStatus(unittest.TestCase):
    """status and wait read a run's folder alone: progress.json (written by run), records.json, an arms run's files."""

    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        (self.root / "scenarios").mkdir()
        (self.root / "arms").mkdir()
        env = mock.patch.dict(os.environ, {"SIMSONLINE_RUN_DIR": str(self.root)})
        env.start()
        self.addCleanup(env.stop)
        self.addCleanup(self.tmp.cleanup)

    def suite(self, name: str, records: list[dict], current: dict | None = None, finished: int | None = None,
              planned: list[str] | None = None) -> Path:
        folder = self.root / "scenarios" / name
        folder.mkdir()
        progress = {"runner": {"pid": 4242, "started": 100.0}, "planned": planned or ["a", "b", "c"],
                    "current": current, "finished": finished}
        (folder / sc.PROGRESS).write_text(json.dumps(progress), encoding="utf-8")
        (folder / "records.json").write_text(json.dumps(records), encoding="utf-8")
        return folder

    @staticmethod
    def rec(name: str, verdict: str = "PASS", total: float = 60.0, failures: list[str] | None = None) -> dict:
        return {"name": name, "verdict": verdict, "failures": failures or [], "met": verdict == "PASS",
                "timings": {"total": total}}

    def out(self, fn: Callable[[], int]) -> tuple[int, str]:
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            code = fn()
        return code, buf.getvalue()

    def test_a_running_scenario_shows_its_place_and_typical_length(self) -> None:
        old = self.suite("20261021-100000", [self.rec("b", total=120.0), self.rec("b", total=180.0)], finished=0)
        (old / "report.md").write_text("# old\n", encoding="utf-8")
        self.suite("20261021-110000", [self.rec("a")], current={"name": "b", "index": 1, "began": "11:02:00", "at": 1000.0})
        code, text = self.out(lambda: sc.status("latest", now=lambda: 1090.0, alive=lambda lock: True))
        self.assertEqual(code, 0)
        self.assertIn("run 20261021-110000: 1 of 3 done (P); on b (2 of 3) since 11:02:00, 1.5 min", text)
        self.assertIn("typical 2.5 min (median of 2 earlier)", text)

    def test_a_dead_runner_or_a_finish_ends_the_run(self) -> None:
        folder = self.suite("20261021-110000", [self.rec("a")], current={"name": "b", "index": 1, "began": "x", "at": 0.0})
        self.assertIn("no longer runs, 1 of 3 scenario(s) done", sc.suite_state(folder, alive=lambda lock: False)["ended"])
        self.assertIsNone(sc.suite_state(folder, alive=lambda lock: True)["ended"])
        done = self.suite("20261021-120000", [self.rec("a")], finished=5)
        self.assertEqual(sc.suite_state(done, alive=lambda lock: True)["ended"], "finished, exit 5")
        bare = self.root / "scenarios" / "20261021-130000"
        bare.mkdir()
        self.assertIn("no progress.json", sc.suite_state(bare)["ended"])

    def test_wait_returns_at_the_next_verdict(self) -> None:
        folder = self.suite("20261021-110000", [self.rec("a")], current={"name": "b", "index": 1, "began": "x", "at": 0.0})

        def sleep(_: float) -> None:
            (folder / "records.json").write_text(json.dumps([self.rec("a"), self.rec("b", "FAIL", 75.0, ["motion: x"])]),
                                                 encoding="utf-8")

        code, text = self.out(lambda: sc.wait("latest", sleep=sleep, alive=lambda lock: True))
        self.assertEqual(code, 0)
        self.assertEqual(text.strip(), "b (2 of 3): FAIL in 75s: motion: x")

    def test_wait_until_end_skips_verdicts_and_exits_9(self) -> None:
        folder = self.suite("20261021-110000", [self.rec("a")], current={"name": "b", "index": 1, "began": "x", "at": 0.0})
        looks = []

        def sleep(_: float) -> None:
            looks.append(1)
            records = [self.rec("a"), self.rec("b")] + ([self.rec("c")] if len(looks) > 1 else [])
            (folder / "records.json").write_text(json.dumps(records), encoding="utf-8")
            if len(looks) > 1:
                progress = json.loads((folder / sc.PROGRESS).read_text(encoding="utf-8"))
                (folder / sc.PROGRESS).write_text(json.dumps(dict(progress, current=None, finished=0)), encoding="utf-8")

        code, text = self.out(lambda: sc.wait("latest", "end", sleep=sleep, alive=lambda lock: True))
        self.assertEqual((code, len(looks)), (9, 2))
        self.assertIn("3 of 3 done (PPP); ended: finished, exit 0", text)

    def test_wait_gives_up_at_its_timeout_with_exit_3(self) -> None:
        self.suite("20261021-110000", [], current={"name": "a", "index": 0, "began": "x", "at": 0.0})
        clock = iter(range(0, 10000, 100))
        code, text = self.out(lambda: sc.wait("latest", timeout=250, sleep=lambda s: None, now=lambda: float(next(clock)),
                                              alive=lambda lock: True))
        self.assertEqual(code, 3)
        self.assertIn("still running after 250 s (wait again)", text)

    def arms_run(self, name: str = "20261021-090000") -> Path:
        folder = self.root / "arms" / name
        folder.mkdir()
        (folder / "runner.json").write_text(json.dumps({"pid": 7, "started": 1.0}), encoding="utf-8")
        (folder / "plan.json").write_text(json.dumps({"order": [[1, "base", []], [1, "on", []]]}), encoding="utf-8")
        (folder / "state.json").write_text(json.dumps({"next": 0}), encoding="utf-8")
        (folder / "events.txt").write_text("10:00:00 == base #1: run scenarios.py run a\n", encoding="utf-8")
        return folder

    def test_status_shows_a_running_arms_run_before_its_suite_run(self) -> None:
        self.arms_run()
        self.suite("20261021-100100", [], current={"name": "a", "index": 0, "began": "10:01:00", "at": 0.0})
        with mock.patch.object(sc.time, "localtime", return_value=time.struct_time((2026, 10, 21, 10, 3, 0, 1, 294, 0))):
            code, text = self.out(lambda: sc.status(now=lambda: time.mktime((2026, 10, 21, 10, 3, 0, 1, 294, -1)),
                                                     alive=lambda lock: True))
        lines = text.strip().splitlines()
        self.assertEqual(code, 0)
        self.assertTrue(lines[0].startswith("arms 20261021-090000: 0 arm row(s); on base#1 (item 1 of 2), run since 10:00:00"),
                        lines[0])
        self.assertTrue(lines[1].startswith("run 20261021-100100: 0 of 3 done; on a (1 of 3)"), lines[1])

    def test_wait_on_an_arms_run_times_out_through_arms_wait(self) -> None:
        self.arms_run()
        clock = iter(range(0, 10000, 100))
        code, text = self.out(lambda: sc.wait("20261021-090000", timeout=150, sleep=lambda s: None,
                                              now=lambda: float(next(clock)), alive=lambda lock: True))
        self.assertEqual(code, 3)
        self.assertIn("arms 20261021-090000: 0 arm row(s); on base#1", text)

    def test_the_cli_takes_status_and_wait(self) -> None:
        with mock.patch.object(sc, "status", return_value=0) as st, mock.patch.object(sc, "wait", return_value=9) as wt:
            self.assertEqual(sc.main(["status"]), 0)
            self.assertEqual(sc.main(["wait", "20261021-110000", "--until", "end", "--timeout", "60"]), 9)
        st.assert_called_once_with("latest")
        wt.assert_called_once_with("20261021-110000", "end", None, 60.0)


class Watchdog(unittest.TestCase):
    """scripts/run_watchdog.py in a run: a hung or dead scenario ends at the watchdog, not at its own timeouts."""

    class FakeDog:
        """Fires (found set) as soon as it starts when found is given; records the endpoints held."""
        made: list = []

        def __init__(self, games, on_fire=None, **kw) -> None:
            self.games, self.on_fire, self.found, self.held, self.errors = games, on_fire, None, [], []
            Watchdog.FakeDog.made.append(self)

        def start(self) -> None:
            if Watchdog.FIRE:
                self.found = Watchdog.FIRE
                self.on_fire(self.found)

        def stop(self) -> None:
            pass

        @contextlib.contextmanager
        def hold(self, *endpoints):
            self.held.append(endpoints)
            yield

    FIRE = ""

    def watched(self, run, fire: str = "") -> tuple[dict, str]:
        import run_watchdog
        Watchdog.FIRE = fire
        out = io.StringIO()
        with mock.patch.object(sc, "WATCHDOG", True), mock.patch.object(run_watchdog, "Watchdog", Watchdog.FakeDog), \
                mock.patch.object(sc, "stop_children") as stopped, mock.patch.object(sc, "newest_crash", return_value=None), \
                mock.patch.object(sc, "ledger_record") as ledger, contextlib.redirect_stdout(out):
            record = sc.watched(run, {"name": "x"}, Path("."))
        self.stopped, self.ledger = stopped, ledger
        return record, out.getvalue()

    def test_a_fired_watchdog_stops_the_steps_and_the_scenario_reads_crashed(self) -> None:
        def run(spec, out):
            self.assertIs(sc.WATCH, Watchdog.FakeDog.made[-1])  # the step loop and the rejoin holds read it
            return {"name": "x", "verdict": "BLOCKED", "met": False, "failures": ["step b not run: watchdog: ..."]}

        record, said = self.watched(run, fire="client 1 bridge: no answer to DIAG_STATE in 10 s; no answer for 62 s")
        self.assertIsNone(sc.WATCH)
        self.assertEqual((record["verdict"], record["crashed_over"]), ("CRASHED", "cut short"))
        self.assertEqual(record["failures"][-1], "watchdog: client 1 bridge: no answer to DIAG_STATE in 10 s; no answer for 62 s")
        self.stopped.assert_called_once()
        self.ledger.assert_called_once()  # the change set at the death (crash_ledger.py)
        self.assertIn("watchdog: client 1 bridge", said)
        # the next scenario's health() finds the stack down too: added once, the first marking kept
        sc.mark_crashed(record, "stack down: client no answer")
        sc.mark_crashed(record, "stack down: client no answer")
        self.assertEqual(record["crashed_over"], "cut short")
        self.assertEqual(record["failures"][-2:], [record["failures"][-2], "stack down: client no answer"])
        self.assertEqual(record["failures"].count("stack down: client no answer"), 1)

    def test_a_quiet_watchdog_leaves_the_record_alone(self) -> None:
        record, _ = self.watched(lambda spec, out: {"name": "x", "verdict": "PASS", "met": True, "failures": []})
        self.assertEqual(record["verdict"], "PASS")
        self.stopped.assert_not_called()
        self.ledger.assert_not_called()

    def test_the_step_loop_stops_at_a_fired_watchdog_and_holds_a_rejoined_client(self) -> None:
        ran: list[str] = []
        spec = {"name": "late", "expect": "pass", "steps": ["sleep:1", "rejoin:2:paused", "sleep:2", "sleep:3"]}

        def step(s: str) -> tuple[int, str]:
            ran.append(s)
            if s == "sleep:2":
                dog.found = "server: game gone (not running)"
            return 0, ""

        dog = Watchdog.FakeDog({})
        with tempfile.TemporaryDirectory() as folder, mock.patch.object(sc, "WATCH", dog), \
                mock.patch.object(sc, "quick_step", side_effect=step), mock.patch.object(sc, "reset", return_value=""), \
                mock.patch.object(sc, "python", return_value=(0, "")), mock.patch.object(sc.time, "sleep"), \
                mock.patch.object(sc, "note_stream"), \
                mock.patch.dict(os.environ, {"SIMSONLINE_CLIENT2_BRIDGECTL": ""}):
            record = sc.run_scenario(spec, Path(folder))
        self.assertEqual([s for s in ran if s.startswith(("sleep", "rejoin"))], ["sleep:1", "rejoin:2:paused", "sleep:2"])
        self.assertEqual(dog.held, [("127.0.0.1:34031",)])
        self.assertEqual(record["verdict"], "BLOCKED")
        self.assertEqual(record["failures"], ["step sleep:3 not run: watchdog: server: game gone (not running)"])

    def test_rejoined_names_the_client_a_rejoin_restarts(self) -> None:
        with mock.patch.dict(os.environ, {"SIMSONLINE_CLIENT2_BRIDGECTL": "", "SIMSONLINE_CLIENT3_BRIDGECTL": ""}), \
                mock.patch.object(sc, "CLIENT", "127.0.0.1:34030"):
            self.assertEqual(sc.rejoined("rejoin"), ["127.0.0.1:34030"])
            self.assertEqual(sc.rejoined("rejoin:paused"), ["127.0.0.1:34030"])
            self.assertEqual(sc.rejoined("rejoin:3:as=Gunther"), ["127.0.0.1:34032"])
            self.assertEqual(sc.rejoined("rejoin:warm"), ["127.0.0.1:34030"])
            self.assertEqual(sc.rejoined("sleep:3"), [])

    def test_python_keeps_its_running_child_for_the_watchdog_to_stop(self) -> None:
        seen: list = []

        class Proc:
            returncode = 0

            def communicate(self, timeout=None):
                seen.append(set(sc.CHILDREN))
                return "out\n", "err\n"

            def terminate(self) -> None:
                seen.append("terminated")

        with mock.patch.object(sc.subprocess, "Popen", return_value=Proc()):
            self.assertEqual(_PYTHON("scripts/x.py"), (0, "out\nerr\n"))
        self.assertEqual(len(seen[0]), 1)  # running: known
        self.assertEqual(sc.CHILDREN, set())  # done: forgotten
        proc = Proc()
        sc.CHILDREN.add(proc)
        try:
            sc.stop_children()
        finally:
            sc.CHILDREN.discard(proc)
        self.assertEqual(seen[-1], "terminated")


class RustOnlyGate(unittest.TestCase):
    """Card 55 section E: a Rust-only item with no executor makes the scenario BLOCKED, never PASS."""

    def test_routed_walk_is_blocked_before_the_reset(self):
        spec = json.loads((sc.SCENARIOS / "routed_walk_multiroom_lot.json").read_text(encoding="utf-8"))
        with mock.patch.object(sc, "reset", side_effect=AssertionError("touched the games")):
            record = sc.run_scenario(spec, Path(tempfile.gettempdir()))
        self.assertEqual(record["verdict"], "BLOCKED")
        self.assertIn("card 55 mechanism not built", record["failures"][0])

    def test_routed_walk_claims_the_sim_and_scores_e5_on_the_settle_gate(self):
        spec = json.loads((sc.SCENARIOS / "routed_walk_multiroom_lot.json").read_text(encoding="utf-8"))
        self.assertEqual(spec["steps"][0], "claim-sim:Jennifer")
        self.assertIn("rust-evidence:E5", spec["after"])
        self.assertNotIn("sims:Jennifer", spec["after"])
        self.assertIn("claim-sim", sc.rust_only_gap(spec))

    def test_continuity_blocked_does_not_hide_a_real_fail(self):
        self.assertTrue(sc.late_blocked({"failures": ["continuity: BLOCKED {}"]}, True))
        self.assertFalse(sc.late_blocked({"failures": ["continuity: BLOCKED {}", "drivers: x"]}, True))
        self.assertFalse(sc.late_blocked({"failures": []}, False))
        self.assertFalse(sc.late_blocked({"failures": ["continuity: FAIL {}"]}, True))
        self.assertFalse(sc.late_blocked({"failures": ["continuity: no motion watch to read"]}, True))

    def test_gap_names_each_missing_kind_and_ignores_ordinary_scenarios(self):
        self.assertIn("navigate_astar", sc.rust_only_gap({"steps": ["navigate_astar:x"], "after": ["rust-evidence:E1"]}))
        self.assertIsNone(sc.rust_only_gap({"steps": ["sleep:1"], "after": ["drivers"]}))
        with mock.patch.object(sc, "RUST_ONLY_EXECUTORS", frozenset({"navigate_astar", "rust-evidence"})):
            self.assertIsNone(sc.rust_only_gap({"steps": ["navigate_astar:x"], "after": ["rust-evidence:E1"]}))


class RoutingEvidence(unittest.TestCase):
    """Authored observations only; no live commands, geometry captures or timers."""

    def setUp(self):
        self.scope = dict(run_id="authored-run", boot_id="authored-boot", server_pid=42,
                          epoch=7, corr=9, actor=2**40, generation=3, owner="player1",
                          start_ns=100, end_ns=200)
        self.base = dict(schema_version=1, scope=dict(self.scope), owner="player1", bridge_connected=True,
                         claim_held=True, instrument_ready=True, coverage_start_ns=100,
                         coverage_end_ns=200, collector_status="complete")
        self.frames = [f"Rt|9|{2**40}|2|1234567890abcdef|123",
                       f"Vp|rt|9|{2**40}|0|2147483648|-10|0|0",
                       f"Vp|rt|9|{2**40}|1|0|20|0|0"]
        self.e1 = dict(server_native_hits=0, fork_native_hits=0, positive_control_hits=1,
                       control_ns=99, control_instrument="authored-breakpoints",
                       instrument="authored-breakpoints", control_server_pid=42,
                       control_boot_id="authored-boot", module_base=0x400000,
                       breakpoint_va=0x81B2A0, search_entries=[1, 2, 3, 4, 5], unknown_producers=0,
                       configuration="authored-config", control_configuration="authored-config",
                       fork_coverage_start_ns=100, fork_coverage_end_ns=200)
        self.e2 = dict(navigate_requests=2, plans_served=2, executed_routes=2, native_oracle_owned=0)

    def score(self, gate, data, **extra):
        return routing.score(gate, self.scope, dict(self.base, measurement=data, **extra))["verdict"]

    def test_each_reserved_step_blocks_without_runtime_adapter(self):
        self.assertEqual(sc.RUST_ONLY_EXECUTORS, frozenset())
        for kind in sc.RUST_ONLY_KINDS:
            with self.subTest(kind=kind):
                self.assertEqual(sc.rust_step_result(kind, "")["verdict"], "BLOCKED")

    def test_scope_missing_malformed_stale_foreign_and_lost_claim(self):
        self.assertEqual(self.score("E2", self.e2), "PASS")
        for key in routing.SCOPE:
            different = self.scope[key] + 1 if type(self.scope[key]) is int else self.scope[key] + "-other"
            for value in (None, "foreign", True, different):
                with self.subTest(key=key, value=value):
                    scope = dict(self.scope, **{key: value})
                    self.assertEqual(self.score("E2", self.e2, scope=scope), "BLOCKED")
            scope = dict(self.scope)
            del scope[key]
            self.assertEqual(self.score("E2", self.e2, scope=scope), "BLOCKED")
        for key in ("bridge_connected", "claim_held", "instrument_ready"):
            self.assertEqual(self.score("E2", self.e2, **{key: False}), "BLOCKED")
        for key, value in (("owner", "player2"), ("coverage_start_ns", 101),
                           ("coverage_end_ns", 199), ("collector_status", "running"),
                           ("schema_version", True), ("schema_version", 2)):
            self.assertEqual(self.score("E2", self.e2, **{key: value}), "BLOCKED")
        for data in (None, {}, "plans_served == navigate_requests", {"navigate_requests": True}):
            self.assertEqual(self.score("E2", data), "BLOCKED")

    def test_e1_requires_same_process_positive_control_all_producers_and_coverage(self):
        self.assertEqual(self.score("E1", self.e1), "PASS")
        for key, value in (("positive_control_hits", 0), ("control_ns", 100),
                           ("control_instrument", "other"), ("control_server_pid", 43),
                           ("control_boot_id", "old-boot"), ("breakpoint_va", 0x41B2A0),
                           ("control_configuration", "other"), ("fork_coverage_end_ns", 199),
                           ("search_entries", [1, 2, 3, 4]), ("search_entries", [1, 2, 3, 4, 4, 5]),
                           ("unknown_producers", 1)):
            with self.subTest(key=key, value=value):
                self.assertEqual(self.score("E1", dict(self.e1, **{key: value})), "BLOCKED")
        self.assertEqual(self.score("E1", dict(self.e1, server_native_hits=1)), "FAIL")
        self.assertEqual(self.score("E1", dict(self.e1, fork_native_hits=1)), "FAIL")

    def test_e2_zero_work_never_passes_and_counters_reconcile_exactly(self):
        self.assertEqual(self.score("E2", dict(self.e2, navigate_requests=0,
                                             plans_served=0, executed_routes=0)), "BLOCKED")
        for key in ("plans_served", "executed_routes", "native_oracle_owned"):
            self.assertEqual(self.score("E2", dict(self.e2, **{key: 1})), "FAIL")
        for key in self.e2:
            data = dict(self.e2)
            del data[key]
            self.assertEqual(self.score("E2", data), "BLOCKED")

    def test_sealed_frames_full_width_actor_order_unsigned_tags_and_no_vehicle_substitute(self):
        parsed = routing.parse_plan(self.frames, 9, 2**40)
        self.assertEqual(parsed[0:2], ("1234567890abcdef", 123))
        self.assertEqual(parsed[2][0], (2147483648, -10, 0, 0))
        invalid = [
            [], self.frames[:-1], self.frames + [self.frames[-1]],
            [self.frames[0], self.frames[2], self.frames[1]],
            [self.frames[0].replace("|9|", "|8|"), *self.frames[1:]],
            [self.frames[0].replace(str(2**40), "1"), *self.frames[1:]],
            [self.frames[0] + "|tick", *self.frames[1:]],
            [self.frames[0], self.frames[1].replace("2147483648", "-2147483648"), self.frames[2]],
            [self.frames[0], self.frames[1] + "|extra", self.frames[2]],
            [self.frames[0], "Vp|7|120|1000|0|2000|0|1000", self.frames[2]],
            [self.frames[0], self.frames[1].replace("|-10|", "|2147483648|"), self.frames[2]],
        ]
        for frames in invalid:
            with self.subTest(frames=frames), self.assertRaises(routing.Missing):
                routing.parse_plan(frames, 9, 2**40)

    def test_e3_full_point_sequence_not_only_hash_and_cost(self):
        plans = {side: list(self.frames) for side in ("daemon", "client1", "client2")}
        self.assertEqual(self.score("E3", dict(plans=plans)), "PASS")
        plans["client2"][-1] = plans["client2"][-1].replace("|20|", "|21|")
        self.assertEqual(self.score("E3", dict(plans=plans)), "FAIL")
        del plans["client2"]
        self.assertEqual(self.score("E3", dict(plans=plans)), "BLOCKED")

    def test_e4_requires_executed_not_sent_legs_and_all_continuity_metrics(self):
        client = dict(executed_leg_indices=[0, 1], **dict.fromkeys(routing.METRICS, 0))
        data = dict(leg_count=2, client1=dict(client), client2=dict(client))
        self.assertEqual(self.score("E4", data), "PASS")
        for indices in ([0], [0, 0], [1, 0], [0, 1, 1]):
            self.assertEqual(self.score("E4", dict(data, client2=dict(client, executed_leg_indices=indices))), "FAIL")
        for metric in routing.METRICS:
            self.assertEqual(self.score("E4", dict(data, client2=dict(client, **{metric: 1}))), "FAIL")
        self.assertEqual(self.score("E4", dict(data, client2=dict(sent_leg_indices=[0, 1]))), "BLOCKED")
        self.assertEqual(self.score("E4", dict(data, leg_count=0)), "FAIL")

    def test_e5_halt_then_stable_bracketing_samples_then_100mm_tolerance(self):
        client = dict(phase="Halted", position_mm=[100, 0, 0])
        data = dict(final_cell_mm=[0, 0, 0], server_before_mm=[0, 0, 0],
                    server_after_mm=[0, 0, 0], client1=dict(client), client2=dict(client))
        self.assertEqual(self.score("E5", data), "PASS")
        self.assertEqual(self.score("E5", dict(data, client2=dict(client, position_mm=[101, 0, 0]))), "FAIL")
        self.assertEqual(self.score("E5", dict(data, client2=dict(client, phase="Walking",
                                                                position_mm=[999, 0, 0]))), "BLOCKED")
        self.assertEqual(self.score("E5", dict(data, server_after_mm=[1, 0, 0])), "BLOCKED")
        self.assertEqual(self.score("E5", dict(data, client1=dict(client, position_mm=[float("nan"), 0, 0]))), "BLOCKED")

    def test_missing_later_evidence_cannot_erase_real_failure(self):
        data = dict(self.e1, server_native_hits=1)
        del data["positive_control_hits"]
        self.assertEqual(self.score("E1", data), "FAIL")
        self.assertEqual(routing.combine([{"verdict": "FAIL"}, {"verdict": "BLOCKED"}]), "FAIL")
        self.assertEqual(routing.combine([]), "BLOCKED")
        self.assertEqual(routing.combine([{"verdict": "unknown"}]), "BLOCKED")
        self.assertFalse(sc.late_blocked({"failures": ["rust-evidence: FAIL hits",
                                                      "rust-evidence: BLOCKED no data"]}, True))
        self.assertTrue(sc.late_blocked({"failures": ["rust-evidence: BLOCKED no data"]}, True))

    def test_evidence_labels_and_file_paths_cannot_be_measurements(self):
        for label in ("E1 server native A* hits == 0", "E2 plans_served == navigate_requests",
                      "capture.json", "E6"):
            self.assertEqual(self.score(label, self.e2), "BLOCKED")
        self.assertEqual(routing.score("E1", self.scope, None)["verdict"], "BLOCKED")

    def test_claim_and_switch_require_process_observations_not_launcher_text(self):
        observation = dict(self.base, sim_name="Jennifer", kernel_actor=7, mapping_checked=True)
        self.assertEqual(sc.rust_step_result("claim-sim", "Jennifer", self.scope, observation)["verdict"], "PASS")
        for fields in (dict(mapping_checked=False), dict(kernel_actor=0), dict(sim_name="Other"),
                       dict(owner="player2")):
            self.assertEqual(sc.rust_step_result("claim-sim", "Jennifer", self.scope,
                                                dict(observation, **fields))["verdict"], "BLOCKED")
        self.assertEqual(sc.rust_step_result("rust-env", "SIMSONLINE_RUST_ROUTE=1", self.scope,
                                            dict(self.base, launcher="SIMSONLINE_RUST_ROUTE=1"))["verdict"], "BLOCKED")
        self.assertEqual(sc.rust_step_result("navigate_astar", "Jennifer", self.scope,
                                            dict(self.base, frames=self.frames))["verdict"], "BLOCKED")

    def test_reserved_dispatch_cannot_fall_through_even_if_registry_is_misconfigured(self):
        for where in ("steps", "after"):
            spec = {"name": "reserved", "expect": "pass", where: ["rust-evidence:E1"]}
            ran = []

            def quick(step):
                ran.append(step)
                return 0, ""

            with tempfile.TemporaryDirectory() as folder, \
                    mock.patch.object(sc, "RUST_ONLY_EXECUTORS", frozenset({"rust-evidence"})), \
                    mock.patch.object(sc, "reset", return_value=""), \
                    mock.patch.object(sc, "python", return_value=(0, "")), \
                    mock.patch.object(sc, "quick_step", side_effect=quick), \
                    mock.patch.object(sc.time, "sleep"), mock.patch.object(sc, "note_stream"):
                record = sc.run_scenario(spec, Path(folder))
            self.assertEqual(record["verdict"], "BLOCKED")
            self.assertNotIn("rust-evidence:E1", ran)

    def test_earlier_step_failure_survives_later_missing_route_evidence(self):
        spec = {"name": "reserved", "expect": "pass",
                "steps": ["notice:1:5:Missing", "rust-evidence:E1"]}

        def quick(step):
            return (1, "not observed") if step.startswith("notice:") else (0, "")

        with tempfile.TemporaryDirectory() as folder, \
                mock.patch.object(sc, "RUST_ONLY_EXECUTORS", frozenset({"rust-evidence"})), \
                mock.patch.object(sc, "reset", return_value=""), \
                mock.patch.object(sc, "python", return_value=(0, "")), \
                mock.patch.object(sc, "quick_step", side_effect=quick), \
                mock.patch.object(sc.time, "sleep"), mock.patch.object(sc, "note_stream"):
            record = sc.run_scenario(spec, Path(folder))
        self.assertEqual(record["verdict"], "FAIL")
        self.assertTrue(any(f.startswith("step notice:") for f in record["failures"]))


class ForkLine(unittest.TestCase):
    def test_health_prints_the_installed_fork(self):
        for attempt, want in (("cold-test-118", "fork: cold-test-118 installed"), (None, "fork: none installed")):
            out = io.StringIO()
            with mock.patch.object(quick_loop, "physical_fork_census", return_value={
                    "state": "fork" if attempt else "stock", "selectedAttempt": attempt}), \
                    mock.patch.object(sc, "health", return_value=None), contextlib.redirect_stdout(out):
                sc.main(["health"])
            self.assertIn(want, out.getvalue())



class BridgeTraceForward(unittest.TestCase):
    def boot_argv(self, *extra: str) -> list[str]:
        seen: list[tuple] = []

        def python(*args, **kwargs):
            seen.append(args)
            return 1, ""  # the boot fails: run ends here

        with mock.patch.object(sc, "python", side_effect=python), contextlib.redirect_stdout(io.StringIO()):
            sc.main(["run", "--boot", *extra])
        return [list(a) for a in seen if a[:2] == ("scripts/quick_loop.py", "run")][0]

    def test_run_boot_forwards_bridge_trace_only_when_given(self) -> None:
        self.assertNotIn("--bridge-trace", self.boot_argv())
        self.assertIn("--bridge-trace", self.boot_argv("--bridge-trace"))


if __name__ == "__main__":
    unittest.main()

