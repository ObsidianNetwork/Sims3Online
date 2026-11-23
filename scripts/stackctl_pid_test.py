"""Unit tests for stackctl's *-ts3.pid files: every boot path pins its game, a game that exits loses its
file. python -m unittest scripts.stackctl_pid_test

Nothing here reaches a real process: subprocess.run, kills, controller stops, the process list and process opens are
fakes, and every run file is in a temp folder, the telemetry sink too (TelemetryShim's default is .omo/run under the
working directory, which left scripts/.omo/run/telemetry-events.jsonl behind a run from scripts/).
"""
from __future__ import annotations

import contextlib
import io
import json
import os
import sys
import tempfile
import time
import unittest
from datetime import datetime, timezone
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import stack_claim  # noqa: E402
import stackctl  # noqa: E402
from scripts.telemetry import TelemetryShim  # noqa: E402  (the module stackctl imports it from)

REAL_SLEEP = time.sleep
REAL_CLAIM_HOLDER = stackctl.claim_holder
TS3W = "S:/Games/The Sims 3/Game/Bin/TS3W.exe"
CONTROLLER = 7000


def iso(epoch: float) -> str:
    return datetime.fromtimestamp(epoch, timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


class Stack(unittest.TestCase):
    """A fake machine: TS3W processes with start times, controllers that write their state files when launched."""

    def setUp(self) -> None:
        patcher = mock.patch.dict(os.environ, {
            "OMO_SIMSONLINE_SESSION": "", "SIMSONLINE_SESSION": "", "PI_SESSION_ID": "",
            "CLAUDE_CODE_SESSION_ID": "", "ANTIGRAVITY_CONVERSATION_ID": "",
        })
        patcher.start()
        self.addCleanup(patcher.stop)
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        run = self.tmp.name
        self.run_dir = Path(run)
        self.ts3: dict[int, tuple[str, float]] = {}  # pid -> (image, created)
        self.others: set[int] = set()  # running pids that are no TS3W
        self.kills: list[int] = []
        self.games = {"server": 101, "client": 102, "client2": 103}
        self.other_runs: list[str] = []  # other worktrees' .omo/run folders
        paths = {"RUN_DIR": run}
        for role, prefix in (("server", "SERVER"), ("client", "CLIENT"), ("client2", "CLIENT2")):
            paths[f"{prefix}_STATE_FILE"] = os.path.join(run, f"{role}.state.json")
            paths[f"{prefix}_TS3_PID_FILE"] = os.path.join(run, f"{role}-ts3.pid")
            paths[f"{prefix}_LOG"] = os.path.join(run, f"{role}.log")
        fakes = {
            "get_ts3_pids": lambda: sorted(self.ts3),
            "other_checkout_run_dirs": lambda: list(self.other_runs),
            "claim_holder": lambda: None,  # a free stack; Claim reads a fake claim file instead
            "_pid_alive": lambda pid: pid in self.ts3 or pid in self.others or pid == CONTROLLER,
            "game_identity": lambda pid: self.ts3.get(pid) or ((r"C:\Windows\notepad.exe", 0.0) if pid in self.others else None),
            "kill_process": lambda pid, plat: self.kill(pid),
            "stop_controller": lambda pid, graceful=True, grace_seconds=5: None,
            "stop_client_sandbox": lambda args, plat, player_id: True,
            "get_controller_pids": lambda: [],
            "get_controller_pids_by_role": lambda role, plat: [],
            "test_listener": lambda endpoint, timeout_ms=600: endpoint == stackctl.PUBLIC_ENDPOINT and self.server_up,
            "ensure_mod_synced": lambda skip=False: True,
            "ensure_client_wineprefix": lambda prefix=None: None,
            "save_run_logs": lambda: None,
            "wait_for_dumps_before_down": lambda: None,
            "minimize_server_ts3": lambda: None,
            "get_readiness_snapshot": lambda **kw: {"ready": False, "public": False, "server_ctl": False,
                                                    "client_ctl": False, "health": None, "health_ok": False},
            "launch_server": lambda args: self.launch("server"),
            "launch_client": lambda args, player_id=1, skip_orphan_kill=False: self.launch(
                "client" if player_id == 1 else "client2"),
            "cprint": lambda *a, **kw: None,
            "_emit_event": lambda *a, **kw: None,
            "_launch_procs": {},
        }
        for name, value in {**paths, **fakes}.items():
            patcher = mock.patch.object(stackctl, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        # The machine is Windows on every host: the pid files are Windows-only (sync_ts3_pid_files; Linux tags its
        # games by role), so on a Linux runner the real Linux branch ran and the files kept an older boot's pid.
        patcher = mock.patch.object(stackctl.sys, "platform", "win32")
        patcher.start()
        self.addCleanup(patcher.stop)
        self.sink = self.run_dir / "telemetry-events.jsonl"
        patcher = mock.patch.object(TelemetryShim, "from_env", lambda: TelemetryShim("stackctl-pid-test", sink_path=self.sink))
        patcher.start()
        self.addCleanup(patcher.stop)
        for target, value in ((stackctl.subprocess, "run"), (stackctl.time, "sleep")):
            replacement = self.fake_run if value == "run" else (lambda seconds: REAL_SLEEP(0.005))
            patcher = mock.patch.object(target, value, replacement)
            patcher.start()
            self.addCleanup(patcher.stop)
        self.server_up = False
        self.args = SimpleNamespace(
            skip_mod_sync=True, detached=False, host_timeout=1, timeout=1, json_mode=False, minimize_server_ts3=False,
            endpoint=stackctl.PUBLIC_ENDPOINT, client_wineprefix=None, client2_wineprefix=None, claim_sim2="0",
            client_launch_delay=0, require_client=False, probe_timeout=1, interval=0.01, sandboxie_box="Client2Box",
            sandboxie_box2="", force=False)

    def fake_run(self, cmd, *args, **kwargs):
        if len(cmd) >= 3 and cmd[1].endswith("gc_watch.py"):
            self.gc_calls = getattr(self, "gc_calls", []) + [cmd[2:]]
            return SimpleNamespace(returncode=0, stdout="gc watch: no reader running\n", stderr="")
        if cmd[:3] == ["taskkill", "/F", "/PID"]:
            self.kill(int(cmd[3]))
            return SimpleNamespace(returncode=0, stdout="", stderr="")
        raise AssertionError(f"a test reached a real subprocess: {cmd}")

    def kill(self, pid: int) -> None:
        self.kills.append(pid)
        self.ts3.pop(pid, None)

    def launch(self, role: str, mode: str = "launch") -> None:
        """What a controller does: its old pid file goes (launch_client removes it), it starts its TS3 and writes
        gamePid into its state file before it is ready."""
        pid_file = self.pid_file(role)
        if role != "server" and os.path.exists(pid_file):
            os.remove(pid_file)
        now = time.time()
        game = self.games[role]
        self.ts3[game] = (TS3W, now + 0.4)
        self.state(role, game, now, phase="host-scanning", mode=mode)

    def state(self, role: str, game: int | None, started: float, phase: str = "ready", mode: str = "launch") -> None:
        body = {"pid": CONTROLLER, "phase": phase, "startedUtc": iso(started), "gamePid": game,
                "history": [{"phase": "host-starting", "detail": mode}]}
        Path(self.run_dir / f"{role}.state.json").write_text(json.dumps(body), encoding="utf-8")

    def pid_file(self, role: str) -> str:
        return str(self.run_dir / f"{role}-ts3.pid")

    def pinned(self, role: str) -> int | None:
        return stackctl._read_pid_file(self.pid_file(role))

    def write_pid(self, role: str, pid: int) -> None:
        Path(self.pid_file(role)).write_text(str(pid), encoding="utf-8")


class BootPaths(Stack):
    """Each boot path pins its game while it waits, so a wait that never sees ready (2026-10-18 22:13's boot left
    server-ts3.pid on 22:04's game and no client-ts3.pid) still leaves the right files."""

    def boot(self, call, *roles: str) -> None:
        for role in roles:
            self.write_pid(role, 42348)  # an older boot's game, gone
        call()
        for role in roles:
            self.assertEqual(self.pinned(role), self.games[role], f"{role} after {call}")

    def test_server_client_and_client2_verbs(self) -> None:
        plat = stackctl.Platform()
        self.boot(lambda: stackctl.cmd_server(self.args, plat), "server")
        self.boot(lambda: stackctl.cmd_client(self.args, plat), "client")  # also quick_loop rejoin:warm's call
        self.server_up = True
        self.boot(lambda: stackctl.cmd_client2(self.args, plat), "client2")

    def test_up(self) -> None:
        self.boot(lambda: stackctl.cmd_up(self.args, stackctl.Platform()), "server")

    def test_every_boot_verb(self) -> None:
        # quick_loop's sequential and parallel boots (and so the arms runner's reboots) run boot-server and
        # boot-client; quick_loop rejoin runs boot-client-restart.
        for verb, roles in (("boot-server", ("server",)), ("boot-client", ("client",)), ("boot-client2", ("client2",)),
                            ("boot-client-restart", ("client",)), ("boot-full", ("server", "client"))):
            with self.subTest(verb=verb):
                self.gc_calls = []
                self.boot(lambda: stackctl.cmd_boot(verb, self.args, stackctl.Platform()), *roles)
                # a server boot starts the GC reader unless one runs (start --keep); a client boot leaves it
                self.assertEqual(self.gc_calls, [["start", "--keep"]] if "server" in roles else [])

    def test_a_timed_out_wait_writes_its_telemetry_in_the_temp_run_dir(self) -> None:
        with tempfile.TemporaryDirectory() as cwd:
            here = os.getcwd()
            os.chdir(cwd)  # any working directory: nothing may land under it
            try:
                self.assertEqual(stackctl.cmd_wait_ready(self.args, stackctl.Platform()), 1)  # nothing is up
            finally:
                os.chdir(here)
            self.assertEqual(os.listdir(cwd), [])
        events = [json.loads(line) for line in self.sink.read_text(encoding="utf-8").splitlines()]
        self.assertEqual([(e["signal"], e["attributes"]["reason"]) for e in events],
                         [("dev_session.stack_incomplete", "timeout")])

    def test_a_detached_launch_then_wait_ready(self) -> None:
        self.args.require_client = True
        stackctl.cmd_launch("launch-full", self.args, stackctl.Platform())
        self.assertIsNone(self.pinned("server"))
        stackctl.cmd_wait_ready(self.args, stackctl.Platform())
        self.assertEqual((self.pinned("server"), self.pinned("client")), (101, 102))

    def test_a_detached_launch_then_any_verb(self) -> None:
        stackctl.cmd_launch("launch-server", self.args, stackctl.Platform())
        with mock.patch.object(stackctl, "cmd_status", return_value=0) as status, \
                mock.patch.object(sys, "argv", ["stackctl.py", "status", "--quick"]):
            self.assertEqual(stackctl.main(), 0)
        status.assert_called_once()
        self.assertEqual(self.pinned("server"), 101)

    def test_client_restart_kills_the_pinned_game_and_pins_the_new_one(self) -> None:
        self.ts3[90] = (TS3W, time.time() - 50)
        self.state("client", 90, time.time() - 60)
        stackctl.sync_ts3_pid_files()
        self.assertEqual(self.pinned("client"), 90)
        stackctl.cmd_client_restart(self.args, stackctl.Platform())
        self.assertEqual(self.kills, [90])
        self.assertEqual(self.pinned("client"), 102)

    def test_down_fails_when_box_cleanup_fails_even_with_no_games(self) -> None:
        with mock.patch.object(stackctl, "stop_client_sandbox", return_value=False) as stop:
            self.assertEqual(stackctl.cmd_down(self.args, stackctl.Platform()), 1)
        self.assertEqual([call.args[2] for call in stop.call_args_list], list(stackctl.CLIENT_PLAYERS))

    def test_a_launched_client_restart_leaves_client2_running(self) -> None:
        # boot-client-restart goes through launch-client-restart; it once stopped every Client controller.
        controllers = {"client": [7001], "client2": [7002], "server": [7003]}
        stopped = []
        for role in ("server", "client", "client2"):
            self.launch(role)
        stackctl.sync_ts3_pid_files()
        with mock.patch.object(stackctl, "get_controller_pids", lambda: [7001, 7002, 7003]),                 mock.patch.object(stackctl, "get_controller_pids_by_role", lambda role, plat: controllers.get(role, [])),                 mock.patch.object(stackctl, "_get_process_name", lambda pid, plat: "SimsOnline.Client.exe"),                 mock.patch.object(stackctl, "stop_controller", lambda pid, graceful=True, grace_seconds=5: stopped.append(pid)),                 mock.patch.object(stackctl, "_print_launch_result", lambda *a, **kw: None):
            stackctl.cmd_launch("launch-client-restart", self.args, stackctl.Platform())
        self.assertEqual(stopped, [7001])
        self.assertEqual(self.kills, [102])
        self.assertIn(101, self.ts3)
        self.assertIn(103, self.ts3)

    def test_a_reattached_controller_keeps_its_warm_game(self) -> None:
        self.ts3[77] = (TS3W, time.time() - 9000)  # the warm server TS3 predates the reattached controller
        self.write_pid("server", 77)
        self.state("server", 77, time.time() - 5, mode="attach")
        self.assertEqual(stackctl.sync_ts3_pid_files(), [])
        self.assertEqual(self.pinned("server"), 77)
        self.state("server", 77, time.time() - 5, mode="launch")  # a launched controller's game cannot predate it
        changes = stackctl.sync_ts3_pid_files()
        self.assertIsNone(self.pinned("server"))
        self.assertIn("created before its controller started", changes[0])


class Exits(Stack):
    def test_down_waits_for_a_catcher_dump_first(self) -> None:
        calls = []
        waited = {"waited_s": 12.0, "timed_out": False, "dumps": {"server-101-20261020-010000-interp-av.dmp": 1500000000}}

        def guard() -> dict:
            calls.append(sorted(self.ts3))
            return waited

        self.launch("server")
        stackctl.sync_ts3_pid_files()
        args = SimpleNamespace(**{**vars(self.args), "json_mode": True})
        with mock.patch.object(stackctl, "wait_for_dumps_before_down", guard), \
                contextlib.redirect_stdout(io.StringIO()) as out:
            self.assertEqual(stackctl.cmd_down(args, stackctl.Platform()), 0)
        self.assertEqual(calls, [[101]])  # before any game went
        self.assertEqual(self.kills, [101])
        self.assertEqual(json.loads(out.getvalue().splitlines()[-1])["dumpWait"], waited)

    def test_down_clears_the_swept_games_files(self) -> None:
        for role in ("server", "client"):
            self.launch(role)
        stackctl.sync_ts3_pid_files()
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(stackctl.cmd_down(self.args, stackctl.Platform()), 0)
        self.assertEqual(sorted(self.kills), [101, 102])
        self.assertEqual((self.pinned("server"), self.pinned("client")), (None, None))
        self.assertEqual(self.gc_calls, [["stop"]])  # the boot's GC reader goes down with the games

    def test_a_game_that_exited_loses_its_file(self) -> None:
        self.state("client2", 4212, time.time() - 5 * 86400)  # 10-13's client2, long gone
        self.write_pid("client2", 4212)
        changes = stackctl.sync_ts3_pid_files()
        self.assertFalse(os.path.exists(self.pid_file("client2")))
        self.assertEqual(changes, ["client2-ts3.pid: 4212 removed (client2.state.json gamePid 4212: not running; "
                                   "client2-ts3.pid 4212: not running)"])

    def test_one_game_one_role_the_newer_controller_keeps_it(self) -> None:
        now = time.time()
        self.ts3[58848] = (TS3W, now - 10)
        self.state("client2", 58848, now - 5 * 86400)  # an old state whose gamePid the new client game reused
        self.state("client", 58848, now - 12)
        stackctl.sync_ts3_pid_files()
        self.assertEqual((self.pinned("client"), self.pinned("client2")), (58848, None))

    def other_checkout_game(self, role: str, pid: int) -> str:
        """A game another worktree's controller launched: its state file in that worktree's run folder."""
        other = tempfile.TemporaryDirectory()
        self.addCleanup(other.cleanup)
        now = time.time()
        self.ts3[pid] = (TS3W, now - 5)
        body = {"pid": CONTROLLER, "phase": "ready", "startedUtc": iso(now - 10), "gamePid": pid,
                "history": [{"phase": "host-starting", "detail": "launch"}]}
        Path(other.name, f"{role}.state.json").write_text(json.dumps(body), encoding="utf-8")
        self.other_runs.append(other.name)
        return other.name

    def test_down_sweeps_the_games_another_worktree_launched(self) -> None:
        """2026-10-22: down from simscope left Session U's three games (booted from U's worktree) and read 0."""
        self.launch("server")
        other = self.other_checkout_game("client", 201)
        self.assertEqual(stackctl.get_other_checkout_ts3_pids(), {201: other})
        args = SimpleNamespace(**{**vars(self.args), "json_mode": True})
        with contextlib.redirect_stdout(io.StringIO()) as out:
            self.assertEqual(stackctl.cmd_down(args, stackctl.Platform()), 0)
        self.assertEqual(sorted(self.kills), [101, 201])
        self.assertEqual(json.loads(out.getvalue().splitlines()[-1])["receipt"], "TS3W remaining = 0")

    def test_down_counts_a_game_no_files_name_and_leaves_it(self) -> None:
        self.launch("server")
        self.ts3[999] = (TS3W, time.time())  # someone's own game: no checkout's files name it
        args = SimpleNamespace(**{**vars(self.args), "json_mode": True})
        with contextlib.redirect_stdout(io.StringIO()) as out:
            self.assertEqual(stackctl.cmd_down(args, stackctl.Platform()), 1)
        self.assertEqual(self.kills, [101])
        result = json.loads(out.getvalue().splitlines()[-1])
        self.assertEqual(result["receipt"], "TS3W remaining = 1 (1 no checkout's files name: 999; down --force "
                                            "sweeps them)")
        self.assertEqual((result["ok"], result["unowned"]), (False, [999]))

    def test_down_never_kills_a_stale_pid_that_is_no_game(self) -> None:
        self.others.add(4212)  # 10-13's client2 gamePid, now another program's pid
        self.state("client2", 4212, time.time() - 5 * 86400)
        self.ts3[101] = (TS3W, time.time())
        self.state("server", 101, time.time() - 1)
        self.assertEqual(stackctl.get_tracked_windows_ts3_pids(), [101])

    def test_linux_keeps_its_own_way(self) -> None:
        self.launch("server")
        with mock.patch.object(stackctl.sys, "platform", "linux"):
            self.assertEqual(stackctl.sync_ts3_pid_files(), [])
        self.assertIsNone(self.pinned("server"))


class ClientLaunchIdentity(Stack):
    def launch_receipt(self):
        # Fix the clock at the fractional second that used to put a rounded receipt before creation.
        with mock.patch.object(time, "time", return_value=1_700_000_000.75):
            self.launch("client")
        state_file = self.run_dir / "client.state.json"
        state = json.loads(state_file.read_text(encoding="utf-8"))
        state["clientTs3"] = TS3W
        state["history"].append({"phase": "host-scanning", "utc": iso(self.ts3[102][1] + 0.5),
                                 "detail": "gamePid=102"})
        state_file.write_text(json.dumps(state), encoding="utf-8")

    def test_first_pin_records_image_and_exact_creation_time(self):
        self.launch_receipt()
        stackctl.pin_game(str(self.run_dir / "client.state.json"))
        identity = json.loads(Path(self.pid_file("client") + ".identity.json").read_text(encoding="utf-8"))
        self.assertEqual(identity, {"pid": 102, "image": TS3W, "started": self.ts3[102][1]})

    def test_later_sync_never_rebinds_a_reused_pid(self):
        self.launch_receipt()
        stackctl.sync_ts3_pid_files()
        path = Path(self.pid_file("client") + ".identity.json")
        original = path.read_bytes()
        self.ts3[102] = (TS3W, self.ts3[102][1] + 60)
        stackctl.sync_ts3_pid_files()
        self.assertEqual(path.read_bytes(), original)

    def test_stale_launch_receipt_cannot_adopt_a_reused_ts3w(self):
        self.launch_receipt()
        self.ts3[102] = (TS3W, self.ts3[102][1] + 60)
        stackctl.sync_ts3_pid_files()
        self.assertFalse(Path(self.pid_file("client") + ".identity.json").exists())

    def test_creation_after_the_receipt_is_refused_even_within_one_millisecond(self):
        self.launch_receipt()
        state = json.loads((self.run_dir / "client.state.json").read_text(encoding="utf-8"))
        observed = datetime.fromisoformat(state["history"][-1]["utc"]).timestamp()
        self.ts3[102] = (TS3W, observed + 0.0005)
        stackctl.sync_ts3_pid_files()
        self.assertFalse(Path(self.pid_file("client") + ".identity.json").exists())

    def test_missing_receipt_or_wrong_image_cannot_prove_ownership(self):
        self.launch("client")
        stackctl.sync_ts3_pid_files()
        path = Path(self.pid_file("client") + ".identity.json")
        self.assertFalse(path.exists())
        self.launch_receipt()
        self.ts3[102] = ("S:/Unrelated/TS3W.exe", self.ts3[102][1])
        stackctl.sync_ts3_pid_files()
        self.assertFalse(path.exists())

    def test_reader_refuses_missing_corrupt_or_incomplete_identity(self):
        path = Path(self.pid_file("client") + ".identity.json")
        self.assertIsNone(stackctl.client_launch_identity(self.pid_file("client")))
        for text in ("garbage", "[]", "{}", '{"pid": 102, "image": "TS3W.exe", "started": 100}'):
            with self.subTest(text=text):
                path.write_text(text, encoding="utf-8")
                self.assertIsNone(stackctl.client_launch_identity(self.pid_file("client")))


REAL_CLAIM_HOLDER = stackctl.claim_holder  # the fixture fakes a free stack; Claim reads a fake claim file


class SessionEnvironmentIsolation(unittest.TestCase):
    def test_claim_fixtures_ignore_and_restore_inherited_session(self) -> None:
        with mock.patch.dict(os.environ, {"OMO_SIMSONLINE_SESSION": "inherited-main",
                                          "ANTIGRAVITY_CONVERSATION_ID": "inherited-conversation"}):
            for case in (Claim("test_the_holder_and_a_free_stack_go_down"),
                         ClaimGate("test_the_holder_is_matched_by_simsonline_session"),
                         BootUnderClaim("test_a_boot_and_wait_ready_under_another_sessions_claim_still_probe")):
                with self.subTest(case=case.id()):
                    result = unittest.TestResult()
                    case.run(result)
                    self.assertTrue(result.wasSuccessful(), result.errors + result.failures)
                    self.assertEqual(os.environ["OMO_SIMSONLINE_SESSION"], "inherited-main")
                    self.assertEqual(os.environ["ANTIGRAVITY_CONVERSATION_ID"], "inherited-conversation")


class Claim(Stack):
    """Item 11 (Main, 2026-10-22): down refuses while another session holds the stack claim, before touching any
    process, --force included, unless --ignore-claim. The holder and a free stack go down as before."""

    def setUp(self) -> None:
        super().setUp()
        self.claim_file = self.run_dir / "stack-claim.txt"
        for target, name, value in ((stack_claim, "CLAIM", self.claim_file),
                                    (stackctl, "claim_holder", REAL_CLAIM_HOLDER)):
            patcher = mock.patch.object(target, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        patcher = mock.patch.dict(os.environ, {"SIMSONLINE_SESSION": "814f5dec"})
        patcher.start()
        self.addCleanup(patcher.stop)
        self.launch("server")
        stackctl.sync_ts3_pid_files()

    def claim(self, session: str) -> None:
        self.claim_file.write_text(stack_claim.format_line(session, datetime.now(), "boot") + "\n",
                                   encoding="utf-8")

    def test_another_session_s_claim_refuses_before_any_process_even_with_force(self) -> None:
        self.claim("30a1c8d1")
        args = SimpleNamespace(**{**vars(self.args), "force": True, "json_mode": True})
        err = io.StringIO()
        with mock.patch.object(stackctl, "wait_for_dumps_before_down", side_effect=AssertionError("it waited")), \
                contextlib.redirect_stdout(io.StringIO()) as out, contextlib.redirect_stderr(err):
            self.assertEqual(stackctl.cmd_down(args, stackctl.Platform()), 2)
        self.assertEqual(self.kills, [])
        self.assertIn(101, self.ts3)
        refusal = err.getvalue().strip()
        self.assertTrue(refusal.startswith("stackctl down refused: the live stack is claimed by 30a1c8d1 since "), refusal)
        self.assertTrue(refusal.endswith("; no process touched. Take the claim first (python scripts/stack_claim.py "
                                         "take <purpose>, or queue <purpose>), or pass --ignore-claim"), refusal)
        self.assertEqual(json.loads(out.getvalue()), {"ok": False, "verb": "down", "refused": refusal})

    def test_the_holder_and_a_free_stack_go_down(self) -> None:
        self.claim("814f5dec")  # this session's own claim
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(stackctl.cmd_down(self.args, stackctl.Platform()), 0)
        self.claim_file.unlink()
        self.launch("client")
        stackctl.sync_ts3_pid_files()
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(stackctl.cmd_down(self.args, stackctl.Platform()), 0)
        self.assertEqual(self.kills, [101, 102])

    def test_ignore_claim_goes_down_from_the_command_line(self) -> None:
        self.claim("30a1c8d1")
        with mock.patch.object(sys, "argv", ["stackctl.py", "down", "--ignore-claim"]), \
                contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(stackctl.main(), 0)
        self.assertEqual(self.kills, [101])


class BootUnderClaim(Stack):
    """G12: a boot never reads the stack claim, so one under another session's claim with no SIMSONLINE_SESSION
    (a boot hook may not carry the holder's name) still runs its readiness probes."""

    def test_a_boot_and_wait_ready_under_another_sessions_claim_still_probe(self) -> None:
        holder = mock.Mock(return_value="D since 21:30 (gc gate series)")
        snapshot = mock.Mock(return_value={"ready": False, "public": False, "server_ctl": False, "client_ctl": False,
                                           "health": None, "health_ok": False})

        def ready(args) -> None:  # the server reaches ready, so the boot gets to its final readiness check
            self.launch("server")
            self.state("server", self.games["server"], time.time())

        with mock.patch.object(stackctl, "claim_holder", holder), mock.patch.object(stackctl, "get_readiness_snapshot", snapshot), \
                mock.patch.object(stackctl, "launch_server", ready), mock.patch.dict(os.environ, {"SIMSONLINE_SESSION": ""}):
            stackctl.cmd_boot("boot-server", self.args, stackctl.Platform())
            boot_probes = snapshot.call_count
            stackctl.cmd_wait_ready(self.args, stackctl.Platform())
        holder.assert_not_called()
        self.assertGreaterEqual(boot_probes, 1)
        self.assertGreater(snapshot.call_count, boot_probes)


class ClaimGate(unittest.TestCase):
    """G12: status leaves the bridge alone (listeners only) under another session's stack claim; the holder,
    --probe and --quick go on as before."""

    def setUp(self) -> None:
        patcher = mock.patch.dict(os.environ, {
            "OMO_SIMSONLINE_SESSION": "", "SIMSONLINE_SESSION": "", "PI_SESSION_ID": "",
            "CLAUDE_CODE_SESSION_ID": "", "ANTIGRAVITY_CONVERSATION_ID": "",
        })
        patcher.start()
        self.addCleanup(patcher.stop)
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.probed: list[str] = []
        self.printed: list[str] = []
        self.holder: str | None = None
        self.asked = 0
        paths = {name: os.path.join(tmp.name, name.lower()) for name in (
            "SERVER_STATE_FILE", "CLIENT_STATE_FILE", "CLIENT2_STATE_FILE", "SERVER_LOG", "CLIENT_LOG", "CLIENT2_LOG",
            "CLIENT2_TS3_PID_FILE")}
        fakes = {
            "get_ts3_pids": lambda: [101],
            # the server and client 1 up; no later player's client (2 or 3)
            "test_listener": lambda endpoint, timeout_ms=600: endpoint not in (
                stackctl.client_files(n)["bridgectl"] for n in stackctl.CLIENT_PLAYERS[1:]),
            "probe_game_state": lambda endpoint, timeout_sec=2.5: self.probed.append(endpoint) or {
                "in_world": "True", "loaded": True, "tick": 5, "dispatch": 5},
            "get_readiness_snapshot": lambda **kw: self.probed.append("bridge-health") or {"ready": True},
            "get_mod_build_status": lambda running: "mod=abc (current)",
            "get_live_mod_stamp": lambda *a: None,
            "get_workspace_git_sha": lambda: "abc",
            "get_mod_manifest": lambda: {},
            "get_client_mod_build_status": lambda ready: None,
            "test_mod_needs_sync": lambda: False,
            "cprint": lambda text="", *a, **kw: self.printed.append(str(text)),
            "claim_holder": self.ask,
        }
        for name, value in {**paths, **fakes}.items():
            patcher = mock.patch.object(stackctl, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)

    def ask(self) -> str | None:
        self.asked += 1
        return self.holder

    def status(self, **flags) -> tuple[int, dict | None]:
        args = SimpleNamespace(**{"strict": False, "quick": False, "probe": False, "json_mode": True,
                                  "endpoint": stackctl.PUBLIC_ENDPOINT, "skip_mod_sync": True, **flags})
        self.probed.clear()
        self.printed.clear()
        with contextlib.redirect_stdout(io.StringIO()) as out:
            code = stackctl.cmd_status(args, stackctl.Platform())
        return code, (json.loads(out.getvalue()) if args.json_mode else None)

    def test_no_claim_probes_as_before(self) -> None:
        code, result = self.status()
        self.assertEqual((code, result["claim"]), (0, None))
        self.assertEqual(self.probed, [stackctl.SERVER_BRIDGECTL, stackctl.CLIENT_BRIDGECTL])
        self.assertTrue(result["game_state"]["server"]["loaded"])

    def test_another_sessions_claim_leaves_the_bridge_alone(self) -> None:
        self.holder = "D since 21:30 (gc gate series)"
        line = "claimed by D since 21:30 (gc gate series): bridge probes skipped (--probe to force)"
        code, result = self.status()
        self.assertEqual((code, result["claim"], result["game_state"], self.probed), (0, line, {}, []))
        self.assertTrue(result["listeners"]["server_ctl"])  # the listeners are still read
        code, _ = self.status(strict=True)
        self.assertEqual((code, self.probed), (3, []))  # listeners up, bridge health not checked: neither 0 nor 1
        code, _ = self.status(json_mode=False)
        self.assertEqual((code, self.probed), (0, []))
        self.assertIn(line, self.printed)
        self.assertFalse(any("game state" in p for p in self.printed))

    def test_probe_and_quick_go_on_as_before(self) -> None:
        self.holder = "D since 21:30"
        self.status(probe=True)
        self.assertEqual(self.probed, [stackctl.SERVER_BRIDGECTL, stackctl.CLIENT_BRIDGECTL])
        code, _ = self.status(strict=True, probe=True)
        self.assertEqual((code, self.probed), (0, ["bridge-health", stackctl.SERVER_BRIDGECTL, stackctl.CLIENT_BRIDGECTL]))
        asked = self.asked
        code, result = self.status(quick=True)  # nothing to skip: the claim is not read
        self.assertEqual((code, result["claim"], self.asked, self.probed), (0, None, asked, []))

    def test_the_holder_is_matched_by_simsonline_session(self) -> None:
        import stack_claim

        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "stack-claim.txt"
            path.write_text(f"D {datetime.now().isoformat(timespec='seconds')} gc gate series\n", encoding="utf-8")
            with mock.patch.object(stack_claim, "CLAIM", path), mock.patch.object(stackctl, "claim_holder", REAL_CLAIM_HOLDER):
                with mock.patch.dict(os.environ, {"SIMSONLINE_SESSION": "D"}):
                    self.assertIsNone(stackctl.claim_holder())
                    self.status()
                    self.assertEqual(self.probed, [stackctl.SERVER_BRIDGECTL, stackctl.CLIENT_BRIDGECTL])
                for session in ("B", ""):  # another session, or none named: someone else's
                    with mock.patch.dict(os.environ, {"SIMSONLINE_SESSION": session}):
                        self.assertTrue(stackctl.claim_holder().startswith("D since "))


class Archive(unittest.TestCase):
    """G13: quick_loop's last boot output goes into the archive with that run's logs. A launch removes nothing
    by default (the folders are evidence); SIMSONLINE_ARCHIVE_KEEP opts in, and stackctl archive --prune N --yes removes
    by hand."""

    def setUp(self) -> None:
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.run_dir = Path(tmp.name)
        self.archive = self.run_dir / "archive"
        for name, value in {"RUN_DIR": str(self.run_dir), "ARCHIVE_DIR": str(self.archive), "_archived": False,
                            "cprint": lambda *a, **kw: None}.items():
            patcher = mock.patch.object(stackctl, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)

    def test_the_last_boot_output_goes_with_its_logs_once(self) -> None:
        for name in ("client.log.err", "server.log", "quick_loop-boot-client.prev.out", "quick_loop-boot-server.out"):
            (self.run_dir / name).write_text(name, encoding="utf-8")
        with mock.patch.dict(os.environ, {"SIMSONLINE_ARCHIVE_KEEP": "0"}):
            stackctl.save_run_logs()
        (dest,) = self.archive.iterdir()
        self.assertTrue(stackctl.AUTO_ARCHIVE.fullmatch(dest.name))
        self.assertEqual(sorted(p.name for p in dest.iterdir()), ["client.log.err", "quick_loop-boot-client.out", "server.log"])
        self.assertEqual((dest / "quick_loop-boot-client.out").read_text(encoding="utf-8"), "quick_loop-boot-client.prev.out")
        self.assertFalse((self.run_dir / "quick_loop-boot-client.prev.out").exists())  # archived once
        self.assertTrue((self.run_dir / "quick_loop-boot-server.out").exists())  # this boot's own output stays

    def test_the_archive_keeps_its_newest_folders(self) -> None:
        for day in range(1, 6):
            (self.archive / f"2026-10-0{day}-120000-auto").mkdir(parents=True)
        (self.archive / "d-failed-boot").mkdir()  # a hand-named folder is never pruned
        (self.run_dir / "server.log").write_text("x", encoding="utf-8")
        with mock.patch.dict(os.environ, {"SIMSONLINE_ARCHIVE_KEEP": "3"}):
            stackctl.save_run_logs()
        names = sorted(p.name for p in self.archive.iterdir())
        self.assertEqual(len(names), 4)
        self.assertEqual(names[:2], ["2026-10-04-120000-auto", "2026-10-05-120000-auto"])
        self.assertEqual(names[3], "d-failed-boot")
        self.assertEqual(stackctl.prune_archive(0), 0)  # 0 keeps every one
        self.assertEqual(stackctl.prune_archive(1), 2)
        self.assertEqual(len(list(self.archive.iterdir())), 2)

    def test_a_boot_with_the_default_removes_nothing_and_counts_the_archive(self) -> None:
        for day in range(1, 6):
            folder = self.archive / f"2026-10-0{day}-120000-auto"
            folder.mkdir(parents=True)
            (folder / "server.log").write_bytes(b"x" * 400_000)
        (self.run_dir / "server.log").write_text("x", encoding="utf-8")
        printed = []
        env = {k: v for k, v in os.environ.items() if k != "SIMSONLINE_ARCHIVE_KEEP"}
        with mock.patch.dict(os.environ, env, clear=True), mock.patch.object(stackctl, "cprint", printed.append):
            stackctl.save_run_logs()
        self.assertEqual(len(list(self.archive.iterdir())), 6)
        self.assertRegex(printed[-1], r"-> \.omo/run/archive/\S+-auto \(archive: 6 folder\(s\), 2 MB; stackctl archive "
                                      r"--prune N lists what it would remove\)$")

    def test_archive_prune_removes_only_with_yes(self) -> None:
        for day in range(1, 6):
            (self.archive / f"2026-10-0{day}-120000-auto").mkdir(parents=True)
        (self.archive / "d-failed-boot").mkdir()
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(stackctl.cmd_archive(2), 0)
        self.assertIn("would remove 3 of 5 <stamp>-auto folder(s), 0 MB, 2026-10-01-120000-auto to "
                      "2026-10-03-120000-auto", out.getvalue())
        self.assertIn("dry run: add --yes", out.getvalue())
        self.assertEqual(len(list(self.archive.iterdir())), 6)
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(stackctl.cmd_archive(2, yes=True), 0)
            self.assertEqual(stackctl.cmd_archive(-1), 2)
        self.assertEqual(sorted(p.name for p in self.archive.iterdir()),
                         ["2026-10-04-120000-auto", "2026-10-05-120000-auto", "d-failed-boot"])
        with contextlib.redirect_stdout(io.StringIO()) as listing:
            stackctl.cmd_archive()
        self.assertIn("3 folder(s)", listing.getvalue())


class SlotBinary(unittest.TestCase):
    """build_slot.py install's pointer: the controllers launch the slot's own exes."""

    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.pointer = Path(self.tmp.name) / "build-slot.json"
        for patcher in (mock.patch.object(stackctl, "BUILD_SLOT_FILE", str(self.pointer)),
                        mock.patch.dict(os.environ, {"SIMSONLINE_WORLD_FROM_SERVER": "0"})):
            patcher.start()
            self.addCleanup(patcher.stop)
        self.args = SimpleNamespace(
            build=False, save="day0", host_timeout=60, endpoint=stackctl.PUBLIC_ENDPOINT, ts3_path_explicit=False,
            ts3_path=stackctl.CONTROLLER_DEFAULT_TS3_PATH, player_command_timeout=0, bridge_trace=False,
            claim_sim="1", claim_sim2="0", sandboxie_box="", sandboxie_box2="", sandboxie_start="")

    def exe(self, part: str) -> str:
        path = Path(self.tmp.name) / part / f"{part}.exe"
        path.parent.mkdir(exist_ok=True)
        path.write_bytes(b"MZ")
        return str(path)

    def test_no_pointer_runs_the_projects(self) -> None:
        self.assertIsNone(stackctl.slot_binary("server", self.args))
        self.assertEqual(stackctl._build_server_args(self.args)[:5], ["dotnet", "run", "--project", "DedicatedServer", "--no-build"])
        self.assertEqual(stackctl._build_client_args(self.args)[:5], ["dotnet", "run", "--project", "SimsOnline.Client", "--no-build"])

    def test_a_slot_runs_its_own_exes_with_the_same_arguments(self) -> None:
        plain_server, plain_client = stackctl._build_server_args(self.args), stackctl._build_client_args(self.args)
        server, client = self.exe("server"), self.exe("client")
        self.pointer.write_text(json.dumps({"slot": "old", "server": server, "client": client}), encoding="utf-8")
        self.assertEqual(stackctl._build_server_args(self.args), [server] + plain_server[plain_server.index("--") + 1:])
        self.assertEqual(stackctl._build_client_args(self.args), [client] + plain_client[plain_client.index("--") + 1:])

    def test_a_missing_exe_or_a_build_stops(self) -> None:
        self.pointer.write_text(json.dumps({"slot": "old", "server": str(Path(self.tmp.name) / "gone.exe")}), encoding="utf-8")
        with self.assertRaises(SystemExit) as stop:
            stackctl._build_server_args(self.args)
        self.assertIn("is not there", str(stop.exception))
        self.pointer.write_text(json.dumps({"slot": "old", "server": self.exe("server")}), encoding="utf-8")
        with self.assertRaises(SystemExit) as stop:
            stackctl._build_server_args(SimpleNamespace(**{**vars(self.args), "build": True}))
        self.assertIn("build_slot.py release", str(stop.exception))

    def test_no_make_mod_over_an_installed_slot(self) -> None:
        said = []
        with mock.patch.object(stackctl, "test_mod_needs_sync", return_value=True), \
                mock.patch.object(stackctl, "cprint", lambda text, *a, **kw: said.append(text)), \
                mock.patch.object(stackctl.subprocess, "run", side_effect=AssertionError("make-mod ran")):
            self.pointer.write_text(json.dumps({"slot": "old", "server": self.exe("server")}), encoding="utf-8")
            self.assertTrue(stackctl.ensure_mod_synced())
            self.assertIn("build slot old is installed", said[-1])
            self.pointer.unlink()
            with self.assertRaises(AssertionError):  # no slot: the workspace's make-mod runs as before
                stackctl.ensure_mod_synced()


class ClientEnv(unittest.TestCase):
    """A client launched without quick_loop's CLIENT_ENV (a bare `stackctl boot-client2`) has no SimLink client ring:
    launch_client warns, naming the missing keys."""

    def test_same_keys_as_quick_loop(self):
        import quick_loop
        self.assertEqual(stackctl.CLIENT_ENV, quick_loop.CLIENT_ENV)

    def test_missing_keys(self):
        self.assertEqual(stackctl.missing_client_env({}), list(stackctl.CLIENT_ENV))
        self.assertEqual(stackctl.missing_client_env({"SIMSONLINE_SIMLINK_CLIENT_RING": "1"}),
                         ["SIMSONLINE_WORLD_FROM_SERVER"])
        self.assertEqual(stackctl.missing_client_env(dict(stackctl.CLIENT_ENV)), [])
        # A run that turns one off on purpose (--client-env SIMSONLINE_WORLD_FROM_SERVER=0) is not warned.
        self.assertEqual(stackctl.missing_client_env({"SIMSONLINE_SIMLINK_CLIENT_RING": "1",
                                                      "SIMSONLINE_WORLD_FROM_SERVER": "0"}), [])

    def test_warning_names_the_keys_on_stderr(self):
        err = io.StringIO()
        with mock.patch.dict(os.environ, {"SIMSONLINE_WORLD_FROM_SERVER": "1"}), contextlib.redirect_stderr(err):
            os.environ.pop("SIMSONLINE_SIMLINK_CLIENT_RING", None)
            stackctl.warn_client_env(2)
        self.assertIn("client2 launches without SIMSONLINE_SIMLINK_CLIENT_RING=1", err.getvalue())
        self.assertNotIn("SIMSONLINE_WORLD_FROM_SERVER", err.getvalue())
        quiet = io.StringIO()
        with mock.patch.dict(os.environ, stackctl.CLIENT_ENV), contextlib.redirect_stderr(quiet):
            stackctl.warn_client_env(2)
        self.assertEqual(quiet.getvalue(), "")


class PlayerThree(unittest.TestCase):
    """player 3's client gets its own files, bridge port, box, claim and role, and players 1 and 2 keep theirs."""

    def args(self, **kw) -> SimpleNamespace:
        base = dict(build=False, endpoint=stackctl.PUBLIC_ENDPOINT, claim_sim=11, claim_sim2=22, claim_sim3=33,
                    save="S.sims3", host_timeout=900, ts3_path="T:/TS3W.exe", sandboxie_box="Client2Box",
                    sandboxie_box2="Client3Box", sandboxie_box3="Client4Box", sandboxie_start="S:/Start.exe",
                    player_command_timeout=0, bridge_trace=False, client_wineprefix=None, client2_wineprefix=None,
                    client3_wineprefix=None)
        return SimpleNamespace(**(base | kw))

    def test_each_player_has_its_own_files_and_port(self) -> None:
        one, two, three = (stackctl.client_files(n) for n in (1, 2, 3))
        self.assertEqual((one["role"], one["bridgectl"], one["state"]),
                         ("client", stackctl.CLIENT_BRIDGECTL, stackctl.CLIENT_STATE_FILE))
        self.assertEqual((two["role"], two["bridgectl"], two["pid"]),
                         ("client2", stackctl.CLIENT2_BRIDGECTL, stackctl.CLIENT2_TS3_PID_FILE))
        self.assertEqual(three["role"], "client3")
        self.assertTrue(three["bridgectl"].endswith(":34032"))
        self.assertEqual(os.path.basename(three["state"]), "client3.state.json")
        self.assertEqual(os.path.basename(three["pid"]), "client3-ts3.pid")
        self.assertEqual(len({f["log"] for f in (one, two, three)}), 3)

    def test_the_bridge_port_follows_the_environment(self) -> None:
        with mock.patch.dict(os.environ, {"SIMSONLINE_CLIENT3_BRIDGECTL": "127.0.0.1:44032"}):
            self.assertEqual(stackctl.client_files(3)["bridgectl"], "127.0.0.1:44032")

    def test_player_three_launches_in_its_box_on_its_sim(self) -> None:
        with mock.patch.object(stackctl, "slot_binary", lambda *a: None), \
                mock.patch.object(stackctl.Platform, "__init__", lambda self: setattr(self, "is_linux", False) or None):
            line = stackctl._build_client_args(self.args(), 3, stackctl.client_files(3)["bridgectl"])
        self.assertEqual(line[line.index("--player") + 1], "3")
        self.assertEqual(line[line.index("--display") + 1], "dev-session-3")
        self.assertEqual(line[line.index("--claim-sim") + 1], "33")
        self.assertEqual(line[line.index("--client-ts3-sandboxie-box") + 1], "Client4Box")
        self.assertEqual(line[line.index("--client-bridgectl-listen") + 1], stackctl.client_files(3)["bridgectl"])
        self.assertEqual(line[line.index("--state-file") + 1], stackctl.client_files(3)["state"])

    def test_player_two_is_unchanged(self) -> None:
        with mock.patch.object(stackctl, "slot_binary", lambda *a: None), \
                mock.patch.object(stackctl.Platform, "__init__", lambda self: setattr(self, "is_linux", False) or None):
            line = stackctl._build_client_args(self.args(), 2, stackctl.CLIENT2_BRIDGECTL)
        self.assertEqual(line[line.index("--display") + 1], "dev-session-2")
        self.assertEqual(line[line.index("--claim-sim") + 1], "22")
        self.assertEqual(line[line.index("--client-ts3-sandboxie-box") + 1], "Client3Box")
        self.assertEqual(line[line.index("--state-file") + 1], stackctl.CLIENT2_STATE_FILE)

    def test_each_role_finds_its_own_controller_state_file(self) -> None:
        plat = SimpleNamespace(is_linux=False)
        read: list[str] = []

        class Recorder:
            def __init__(self, path: str) -> None:
                read.append(path)

            def get_pid(self) -> None:
                return None

        with mock.patch.object(stackctl, "StateFile", Recorder):
            for role in ("server", "client", "client2", "client3"):
                self.assertEqual(stackctl.get_controller_pids_by_role(role, plat), [])
        self.assertEqual(read, [stackctl.SERVER_STATE_FILE, stackctl.CLIENT_STATE_FILE, stackctl.CLIENT2_STATE_FILE,
                                stackctl.client_files(3)["state"]])

    def test_the_pid_files_and_roles_cover_player_three(self) -> None:
        self.assertIn("client3", stackctl.ts3_pid_files())
        self.assertEqual(stackctl.client_box(self.args(), 3), "Client4Box")
        self.assertEqual(stackctl.client_box(self.args(sandboxie_box3=""), 3), "")


if __name__ == "__main__":
    unittest.main()
