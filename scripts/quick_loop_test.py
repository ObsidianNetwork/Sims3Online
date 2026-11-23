"""Unit tests for quick_loop.py's boot timing, parallel boot gate and step replies: python -m unittest scripts.quick_loop_test"""
from __future__ import annotations

import contextlib
import io
import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import household_read  # noqa: E402
import quick_loop as ql  # noqa: E402


class FrontDoorSpeedTests(unittest.TestCase):
    def test_line_speed_uses_a_spare_explicit_console_player(self):
        with mock.patch.dict(os.environ, {"SIMSONLINE_FRONT_DOOR": "1"}), \
             mock.patch.object(ql, "run", return_value="accepted\n") as run:
            self.assertEqual(ql.simctl_speed("normal"), "accepted")
            command = run.call_args.args[0]
            self.assertEqual(command[command.index("--wire") + 1], "line")
            self.assertEqual(command[command.index("--player") + 1], "9998")
            self.assertEqual(command[-2:], ["speed", "normal"])

    def test_legacy_speed_keeps_unjoined_json_default(self):
        with mock.patch.dict(os.environ, {"SIMSONLINE_FRONT_DOOR": "0"}), \
             mock.patch.object(ql, "run", return_value="accepted\n") as run:
            ql.simctl_speed("normal")
            command = run.call_args.args[0]
            self.assertNotIn("--wire", command)
            self.assertNotIn("--player", command)


def history(*phases: tuple[str, str]) -> dict:
    return {"startedUtc": phases[0][1] if phases else None, "history": [{"phase": p, "utc": t, "detail": None} for p, t in phases]}


SERVER = history(("starting", "2026-10-17T21:36:34.800Z"), ("session-starting", "2026-10-17T21:36:34.834Z"),
                 ("host-scanning", "2026-10-17T21:36:34.849Z"), ("host-connected", "2026-10-17T21:37:26.321Z"),
                 ("session-started", "2026-10-17T21:37:49.176Z"), ("listener-bound", "2026-10-17T21:37:49.196Z"),
                 ("ready", "2026-10-17T21:37:49.450Z"))
CLIENT = history(("starting", "2026-10-17T21:36:36.800Z"), ("host-connected", "2026-10-17T21:37:16.800Z"),
                 ("server-wait", "2026-10-17T21:37:16.900Z"), ("world-received", "2026-10-17T21:37:52.800Z"),
                 ("ready", "2026-10-17T21:38:20.800Z"))
OLD_CLIENT = history(("starting", "2026-10-17T21:38:00.000Z"), ("host-connected", "2026-10-17T21:38:36.000Z"),
                     ("ready", "2026-10-17T21:39:07.000Z"))


def entry(mode: str, client: dict, marks: dict) -> dict:
    return {"mode": mode, "when": marks.get("server_launch"), "quick_loop": marks, "server": SERVER, "client": client}


class HideClients(unittest.TestCase):
    def setUp(self):
        import ctypes
        from ctypes import wintypes as w
        self.ctl, self.u = mock.Mock(), mock.Mock()
        self.ctl.CLIENT_PLAYERS = (1, 2, 3)
        self.ctl.SERVER_TS3_PID_FILE = "server"
        self.ctl.client_files.side_effect = lambda p: {"pid": str(p)}
        self.pins = {"server": 10, "1": 20, "2": 10, "3": 30}
        self.ctl.get_live_pid_from_file.side_effect = self.pins.get
        self.ctl.get_ts3_pids.return_value = [10, 20, 99]
        self.identities = {20: ("S:/Game/TS3W.exe", 100.0), 30: ("S:/Game/TS3W.exe", 200.0)}
        self.ctl.client_launch_identity.side_effect = lambda path: (
            {"pid": p, "image": self.identities[p][0], "started": self.identities[p][1]}
            if (p := {"1": 20, "3": 30}.get(path)) else None)
        self.ctl.game_identity.side_effect = lambda p: self.identities.get(p)
        owners = {100: 10, 200: 20, 201: 20, 300: 30, 990: 99}
        def pid(hwnd, output):
            ctypes.cast(output, ctypes.POINTER(w.DWORD))[0] = owners[hwnd]
        def enumerate_windows(visit, param):
            for hwnd in owners:
                visit(hwnd, param)
            return 1
        self.u.GetWindowThreadProcessId.side_effect = pid
        self.u.GetWindow.side_effect = lambda hwnd, _flag: 1 if hwnd == 201 else 0
        self.u.IsWindowVisible.return_value = 1
        self.u.ShowWindowAsync.return_value = 1
        self.u.EnumWindows.side_effect = enumerate_windows
        for patch in (
            mock.patch.object(ql.stack_claim, "current", return_value=("mine", None, "test")),
            mock.patch.object(ql.stack_claim, "own_session", return_value="mine"),
            mock.patch.object(ql.os, "name", "nt"),
            mock.patch.dict(sys.modules, {"stackctl": self.ctl}),
            mock.patch.object(ctypes, "WinDLL", return_value=self.u, create=True),
            mock.patch.object(ctypes, "WINFUNCTYPE", return_value=lambda f: f, create=True),
        ):
            patch.start()
            self.addCleanup(patch.stop)
        patch = mock.patch.object(ql, "say")
        self.say = patch.start()
        self.addCleanup(patch.stop)

    def test_requires_the_callers_claim(self):
        for claim in (None, ("other", None, "other task")):
            with self.subTest(claim=claim), mock.patch.object(ql.stack_claim, "current", return_value=claim), \
                    mock.patch.object(ql.stack_claim, "own_session", return_value="mine"):
                with self.assertRaisesRegex(SystemExit, "requires this session"):
                    ql.hide_client_windows()

    def test_windows_are_selected_by_tracked_pid_and_server_is_excluded(self):
        self.assertEqual(ql.hide_client_windows(), 1)
        self.u.ShowWindowAsync.assert_called_once_with(200, 0)

    def test_reused_pid_is_refused_even_when_it_is_another_ts3w(self):
        self.pins["3"] = 99  # Astra's reproduction: the old pin now names the user's live game.
        self.assertEqual(ql.hide_client_windows(), 1)
        self.u.ShowWindowAsync.assert_called_once_with(200, 0)
        self.assertTrue(any("refused" in call.args[0] for call in self.say.call_args_list))

    def test_identity_is_rechecked_immediately_before_hiding(self):
        for identity in (("S:/Game/TS3W.exe", 101.0), ("S:/Other/TS3W.exe", 100.0), None):
            with self.subTest(identity=identity):
                self.ctl.game_identity.side_effect = lambda p: identity if p == 20 else None
                self.assertEqual(ql.hide_client_windows(), 0)
                self.u.ShowWindowAsync.assert_not_called()
                self.assertTrue(any("refused" in call.args[0] for call in self.say.call_args_list))

    def test_missing_launch_identity_refuses_all_clients(self):
        self.ctl.client_launch_identity.side_effect = lambda path: None
        with self.assertRaisesRegex(SystemExit, "no .*client games"):
            ql.hide_client_windows()
        self.u.ShowWindowAsync.assert_not_called()
        self.assertTrue(any("refused" in call.args[0] for call in self.say.call_args_list))

    def test_flag_defaults_off_and_only_runs_when_requested(self):
        for flag in ([], ["--hide-clients"]):
            with self.subTest(flag=flag), mock.patch.object(sys, "argv", ["quick_loop.py", "run", *flag]), \
                    mock.patch.object(ql, "hide_client_windows") as hide, mock.patch.object(ql, "run_steps"), \
                    mock.patch.object(ql, "say"):
                ql.main()
                self.assertEqual(hide.call_count, bool(flag))


class Times(unittest.TestCase):
    def test_parse_utc(self) -> None:
        self.assertAlmostEqual(ql.parse_utc("2026-10-17T21:36:34.800Z") - ql.parse_utc("2026-10-17T21:36:34Z"), 0.8)
        self.assertEqual(ql.parse_utc("1970-01-01T00:01:00Z"), 60.0)
        self.assertIsNone(ql.parse_utc("yesterday"))
        self.assertIsNone(ql.parse_utc(None))
        self.assertAlmostEqual(ql.parse_utc(ql.utc_now()), time.time(), delta=2)

    def test_phase_times_keep_the_first_entry(self) -> None:
        state = history(("a", "1970-01-01T00:00:01Z"), ("b", "1970-01-01T00:00:02Z"), ("a", "1970-01-01T00:00:03Z"))
        self.assertEqual(ql.phase_times(state), {"a": 1.0, "b": 2.0})
        self.assertEqual(ql.phase_times(None), {})

    def test_spans_of_a_parallel_boot(self) -> None:
        spans = ql.boot_spans(entry("parallel", CLIENT, {"server_launch": "2026-10-17T21:36:34.000Z",
                                                         "client_claimed": "2026-10-17T21:38:30.000Z"}))
        self.assertEqual(spans, {"srv launch>menu": 51.5, "srv menu>world": 22.9, "cli after srv": 2.0, "cli launch>menu": 40.0,
                                 "cli menu>world": 36.0, "cli load": 28.0, "cli menu>ready": 64.0, "total": 116.0})

    def test_spans_missing_phases_are_none(self) -> None:
        spans = ql.boot_spans(entry("sequential", OLD_CLIENT, {}))
        self.assertIsNone(spans["cli menu>world"])
        self.assertIsNone(spans["total"])
        self.assertEqual(spans["cli menu>ready"], 31.0)

    def test_table_has_a_row_per_boot_and_a_mean_per_mode(self) -> None:
        marks = {"server_launch": "2026-10-17T21:36:34.000Z", "client_claimed": "2026-10-17T21:38:30.000Z"}
        lines = ql.boot_table([entry("parallel", CLIENT, marks), entry("sequential", OLD_CLIENT, {}),
                               entry("parallel", CLIENT, dict(marks, client_claimed="2026-10-17T21:38:40.000Z"))])
        self.assertTrue(lines[0].startswith("boot (UTC)"))
        self.assertEqual(len(lines), 1 + 3 + 1 + 2)
        self.assertTrue(lines[1].startswith("2026-10-17T21:36:34 parallel"))
        means = [ln for ln in lines if ln.startswith("mean of 2")]
        self.assertEqual(len(means), 1)
        self.assertTrue(means[0].rstrip().endswith("121.0"))  # total: 116 and 126
        self.assertIn("-", [ln for ln in lines if ln.startswith("mean of 1")][0])

    def test_record_and_print(self) -> None:
        with tempfile.TemporaryDirectory() as folder, mock.patch.object(ql, "RUN", Path(folder)), \
                contextlib.redirect_stdout(io.StringIO()) as out:
            run = Path(folder)
            (run / "server.state.json").write_text(json.dumps(SERVER), encoding="utf-8")
            (run / "client.state.json").write_text(json.dumps(CLIENT), encoding="utf-8")
            path = run / "boot-times.jsonl"
            ql.boot_times(5, path)
            self.assertIn("no boots recorded yet", out.getvalue())
            ql.record_boot("parallel", {"server_launch": "2026-10-17T21:36:34.000Z", "client_claimed": "2026-10-17T21:38:30.000Z"}, path)
            ql.record_boot("sequential", {"server_launch": "2026-10-17T22:00:00.000Z"}, path)
            saved = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]
            ql.boot_times(1, path)
        self.assertEqual([e["mode"] for e in saved], ["parallel", "sequential"])
        self.assertEqual(saved[0]["client"]["history"][2]["phase"], "server-wait")
        self.assertIn("boot times: srv launch>menu 52s", out.getvalue())
        self.assertIn("menu = host-connected", out.getvalue())
        self.assertIn("2026-10-17T22:00:00 sequential", out.getvalue())
        self.assertNotIn("2026-10-17T21:36:34 parallel ", out.getvalue().split("menu = host-connected")[1])  # --last 1
        self.assertEqual([(e["server_env"], e["client_env"]) for e in saved], [({}, {}), ({}, {})])  # none given

    def test_a_boot_row_keeps_the_extra_env(self) -> None:
        with tempfile.TemporaryDirectory() as folder, mock.patch.object(ql, "RUN", Path(folder)), \
                contextlib.redirect_stdout(io.StringIO()):
            path = Path(folder) / "boot-times.jsonl"
            ql.record_boot("parallel", {"server_launch": "2026-10-19T22:00:00.000Z"}, path,
                           server_env={"SIMLINK_GCGATE": "1"}, client_env={"B": "2"})
            saved = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual((saved["server_env"], saved["client_env"]), ({"SIMLINK_GCGATE": "1"}, {"B": "2"}))

    def test_a_boot_row_keeps_the_machine_load_and_a_failure(self) -> None:
        with tempfile.TemporaryDirectory() as folder, mock.patch.object(ql, "RUN", Path(folder)), \
                mock.patch.object(ql, "say") as say:
            path = Path(folder) / "boot-times.jsonl"
            ql.record_boot("parallel", {"server_launch": "2026-10-20T01:00:00.000Z"}, path, load=FAKE_LOAD,
                           failed="client never claimed")
            ql.record_boot("parallel", {"server_launch": "2026-10-20T02:00:00.000Z"}, path)
            saved = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]
        self.assertEqual((saved[0]["load"], saved[0]["failed"]), (FAKE_LOAD, "client never claimed"))
        self.assertNotIn("load", saved[1])
        self.assertNotIn("failed", saved[1])
        self.assertIn("boot load: 4 s, 3 samples; cpu 25.0% mean, 40.0% max; free 0.8 GB min of 4.0 GB; "
                      "top: TS3W.exe 100 15.0% (max 25%)", [c.args[0] for c in say.call_args_list])

    def test_the_table_prints_the_load_and_leaves_a_failed_boot_out_of_the_mean(self) -> None:
        marks = {"server_launch": "2026-10-17T21:36:34.000Z", "client_claimed": "2026-10-17T21:38:30.000Z"}
        failed = dict(entry("parallel", CLIENT, dict(marks, client_claimed="2026-10-17T21:46:34.000Z")),
                      failed="client never claimed", load=FAKE_LOAD)
        lines = ql.boot_table([dict(entry("parallel", CLIENT, marks), load=FAKE_LOAD), failed])
        self.assertEqual(sum(ln.strip().startswith("load: 4 s") for ln in lines), 2)
        self.assertIn(" " * 19 + " failed: client never claimed", lines)
        mean = next(ln for ln in lines if ln.startswith("mean of"))
        self.assertTrue(mean.startswith("mean of 1"))
        self.assertTrue(mean.rstrip().endswith("116.0"))  # the failed boot's 600 s left out


class EnsureBootSave(unittest.TestCase):
    """a missing boot save is never replaced by the stock town while a copy of the server's world is left."""

    def test_join_checkpoints_use_a_separate_server_slot(self) -> None:
        self.assertEqual(ql.SERVER_ENV.get("SIMSONLINE_CHECKPOINT_SAVE"), "Option4BookChain-checkpoint.sims3")
        self.assertNotEqual(ql.SERVER_ENV["SIMSONLINE_CHECKPOINT_SAVE"], ql.BOOT_SAVE)
        self.assertEqual(ql.SERVER_BOOT[ql.SERVER_BOOT.index("--save") + 1], ql.BOOT_SAVE)

    def run_it(self, folders: list[str]) -> tuple[Path, list[str]]:
        root = Path(tempfile.mkdtemp(prefix="boot-save-test-"))
        self.addCleanup(shutil.rmtree, root, True)
        (root / "Saves/Sunset Valley.sims3").mkdir(parents=True)
        (root / "Saves/Sunset Valley.sims3/Meta.data").write_bytes(b"DBPF")
        for name in folders:
            (root / "Saves" / name).mkdir()
        said: list[str] = []
        with mock.patch.object(ql, "REAL", root), mock.patch.object(ql, "say", said.append):
            ql.ensure_boot_save()
        return root / "Saves" / ql.BOOT_SAVE, said

    def test_no_save_and_no_copy_takes_the_stock_town(self) -> None:
        save, _ = self.run_it([])
        self.assertTrue((save / "Meta.data").exists())
        self.assertTrue((save / ql.STOCK_SAVE_MARKER).exists())

    def test_a_last_good_copy_is_left_for_the_controller(self) -> None:
        save, said = self.run_it([ql.BOOT_SAVE + ".lastgood", ql.BOOT_SAVE + ".backup"])
        self.assertFalse(save.exists())
        self.assertIn("restores it from Option4BookChain.sims3.lastgood", said[0])

    def test_a_backup_or_bad_folder_refuses_with_the_reason(self) -> None:
        for left in ([ql.BOOT_SAVE + ".backup"], [ql.BOOT_SAVE + ".bad"], [ql.BOOT_SAVE + ".backup", ql.BOOT_SAVE + ".bad"]):
            with self.subTest(left=left), self.assertRaises(SystemExit) as stop:
                self.run_it(left)
            self.assertIn("is missing but " + " and ".join(left) + " exist", str(stop.exception.code))
            self.assertIn("scripts/saves.py restore", str(stop.exception.code))


class ParallelGate(unittest.TestCase):
    def test_pid_alive(self) -> None:
        self.assertTrue(ql.pid_alive(os.getpid()))
        proc = subprocess.Popen([sys.executable, "-c", "pass"])
        proc.wait()
        self.assertFalse(ql.pid_alive(proc.pid))

    def test_server_launched_needs_a_fresh_state_and_a_live_game(self) -> None:
        since = ql.parse_utc("2026-10-18T10:00:00Z")
        fresh = {"startedUtc": "2026-10-18T10:00:01.000Z", "gamePid": os.getpid()}
        self.assertEqual(ql.server_launched(since, fresh), os.getpid())
        self.assertIsNone(ql.server_launched(since, dict(fresh, startedUtc="2026-10-18T09:50:00.000Z")))  # the last boot's file
        self.assertIsNone(ql.server_launched(since, dict(fresh, gamePid=None)))
        self.assertIsNone(ql.server_launched(since, None))
        with mock.patch.object(ql, "pid_alive", return_value=False):
            self.assertIsNone(ql.server_launched(since, fresh))

    def test_client_build_marker(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            dll = Path(folder) / "SimsOnline.Client.dll"
            dll.write_bytes(b"MZ" + "world_received".encode("utf-16-le"))
            self.assertFalse(ql.client_waits_for_server(dll))
            dll.write_bytes(b"MZ" + ql.CLIENT_WAIT_MARKER.encode("utf-16-le"))
            self.assertTrue(ql.client_waits_for_server(dll))
        self.assertFalse(ql.client_waits_for_server(None))

    def test_an_installed_build_slot_is_never_built_over(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            client = Path(folder) / "client" / "SimsOnline.Client.exe"
            client.parent.mkdir()
            (client.parent / "SimsOnline.Client.dll").write_bytes(b"MZ" + "world_received".encode("utf-16-le"))
            pointer = Path(folder) / "build-slot.json"
            with mock.patch.object(ql, "SLOT_POINTER", pointer), mock.patch.object(ql, "run") as run, \
                    mock.patch.object(ql, "say"):
                self.assertNotEqual(ql.client_dll(), client.parent / "SimsOnline.Client.dll")  # no slot: this checkout's
                pointer.write_text(json.dumps({"slot": "old", "client": str(client)}), encoding="utf-8")
                self.assertEqual(ql.client_dll(), client.parent / "SimsOnline.Client.dll")
                with self.assertRaises(SystemExit) as stop:
                    ql.ensure_client_waits()
                self.assertIn("boot --sequential", str(stop.exception))
                with self.assertRaises(SystemExit) as stop:
                    ql.install_mod()
                self.assertIn("build_slot.py release", str(stop.exception))
            run.assert_not_called()  # neither a client build nor a mod build

    def test_boot_mode_defaults_to_parallel(self) -> None:
        with mock.patch.object(ql, "say") as said:
            self.assertTrue(ql.boot_mode(None, {}))
            self.assertFalse(ql.boot_mode(False, {}))
            self.assertFalse(ql.boot_mode(None, {"SIMSONLINE_WORLD_FROM_SERVER": "0"}))  # its own save: sequential
        self.assertIn("sequential boot", said.call_args.args[0])
        with mock.patch.dict(os.environ, {"SIMSONLINE_BOOT_TAG": "cdb"}):
            self.assertEqual(ql.mode_name(False), "sequential+cdb")
        with mock.patch.dict(os.environ, {"SIMSONLINE_BOOT_TAG": ""}):
            self.assertEqual(ql.mode_name(True), "parallel")

    def test_parallel_refuses_a_client_that_loads_its_own_save(self) -> None:
        with mock.patch.object(ql, "TS3", "TS3W.exe"), mock.patch.object(ql, "run") as run:
            with self.assertRaises(SystemExit) as stop:
                ql.boot({}, {"SIMSONLINE_WORLD_FROM_SERVER": "0"}, parallel=True)
        self.assertIn("SIMSONLINE_WORLD_FROM_SERVER=1", str(stop.exception))
        run.assert_not_called()  # refused before the stack goes down


class GcWatch(unittest.TestCase):
    def test_fork_restore_archives_marked_worlds_and_both_server_backups(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            cache = root / ".omo/cache/fork/cold-test-108"
            cache.mkdir(parents=True)
            (cache / "deployment.json").write_text('{"status": "restored"}\n', encoding="utf-8")
            slots = ("Option4BookChain.sims3", "Option4BookChain-checkpoint.sims3")
            for slot in slots:
                backup = root / "profile/Saves" / (slot + ".backup")
                backup.mkdir(parents=True)
                (backup / "Meta.data").write_bytes(b"server-backup")
                for name in ("marked", "unmarked"):
                    world = root / "boxes" / name / "Saves" / slot
                    world.mkdir(parents=True)
                    (world / "Meta.data").write_bytes(b"client-world")
                    if name == "marked":
                        (world / "simsonline-world.txt").write_text("joined", encoding="utf-8")
            with (mock.patch.object(ql, "stack_down", return_value="ok"), mock.patch.object(ql, "run", return_value="Restore: ok\n"),
                  mock.patch.object(ql, "say"), mock.patch.object(ql, "MAIN", root),
                  mock.patch.object(ql, "REAL", root / "profile"), mock.patch.object(ql, "BOXES", ("marked", "unmarked")),
                  mock.patch.object(ql, "box", lambda name: root / "boxes" / name)):
                ql.fork_restore("cold-test-108")
            for slot, archived in zip(slots, ("marked-world.sims3", "marked-world-checkpoint.sims3")):
                self.assertFalse((root / "boxes/marked/Saves" / slot).exists())
                self.assertEqual((cache / archived / "Meta.data").read_bytes(), b"client-world")
                self.assertEqual((root / "boxes/unmarked/Saves" / slot / "Meta.data").read_bytes(), b"client-world")
                self.assertFalse((root / "profile/Saves" / (slot + ".backup")).exists())
                self.assertEqual((cache / ("server-" + slot + ".backup") / "Meta.data").read_bytes(), b"server-backup")

    def test_the_boot_line_and_the_opt_out(self) -> None:
        with mock.patch.object(ql, "run", return_value="gc watch: no reader running\ngc watch: reader pid 9 (boot, ...)\n") as run:
            self.assertEqual(ql.gc_watch_start(), "gc watch: reader pid 9 (boot, ...)")
        self.assertEqual(run.call_args.args[0][1:], ["scripts/gc_watch.py", "start"])
        with mock.patch.dict(os.environ, {"SIMSONLINE_GC_WATCH": "server"}), mock.patch.object(ql, "run_steps"), \
                mock.patch.object(ql, "say"), mock.patch.object(sys, "argv", ["quick_loop.py", "run", "--no-gc-watch"]):
            ql.main()
            self.assertEqual(os.environ["SIMSONLINE_GC_WATCH"], "0")  # what gc_watch.py start, under the boot, reads

    def test_fork_restore_stops_the_boot_reader_through_the_down(self) -> None:
        """Main's note 3 (2026-11-01): a boot's reader must not outlive a fork-restore. Its teardown is stackctl's
        down --force --json, the verb whose cmd_down runs gc_watch.py stop (stackctl_pid_test.py keeps that link).
        Every process call is mocked: no reader and no game is started."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            cache = root / ".omo" / "cache" / "fork" / "cold-test-108"
            cache.mkdir(parents=True)
            (cache / "deployment.json").write_text('{"status": "restored"}\n', encoding="utf-8")
            calls: list[list[str]] = []

            def run(cmd: list[str], **kw: object) -> str:
                calls.append(cmd)
                if "down" not in cmd:
                    return "Restore: ok\n"
                return ('{"ok": true, "verb": "down", "receipt": "TS3W remaining = 0", '
                        '"gcWatch": "gc watch: stopped: reader pid 9 (boot, server)"}\n')

            with (mock.patch.object(ql, "run", side_effect=run), mock.patch.object(ql, "say"),
                  mock.patch.object(ql, "MAIN", root), mock.patch.object(ql, "REAL", root / "profile"),
                  mock.patch.object(ql, "box", lambda name: root / "boxes" / name)):
                ql.fork_restore("cold-test-108")
        self.assertEqual(calls[0][1:], ["scripts/stackctl.py", "down", "--force", "--json"])  # it goes down first
        stackctl = [c for c in calls if "scripts/stackctl.py" in c]
        self.assertEqual(stackctl, [calls[0]])  # and no other stackctl verb


FAKE_LOAD = {"samples": 3, "seconds": 4.0, "cpu_pct": {"mean": 25.0, "max": 40.0},
             "avail_mb": {"min": 800, "mean": 900, "start": 1000, "end": 900}, "total_mb": 4096,
             "top": [{"name": "TS3W.exe", "pid": 100, "cpu_pct": 15.0, "max_pct": 25.0, "cpu_s": 3.0, "ws_mb": 2048}],
             "others": [], "series": []}


class FakeSampler:
    """machine_load.Sampler without its thread or the system reads."""

    def __init__(self, *args: object, **kwargs: object) -> None:
        self.stopped = 0

    def start(self) -> FakeSampler:
        return self

    def stop(self) -> dict:
        self.stopped += 1
        return json.loads(json.dumps(FAKE_LOAD))


class DownRefusal(unittest.TestCase):
    """Item 11 (Main, 2026-10-22): a down refused under another session's claim stops quick_loop with stackctl's own
    words, before any install, restore or boot step."""

    REFUSAL = ql.stack_claim.down_refusal("30a1c8d1 since 06:16")

    def refused(self, cmd: list[str], **kw: object) -> str:
        self.calls.append(cmd)
        return json.dumps({"ok": False, "verb": "down", "refused": self.REFUSAL}) + "\n" + self.REFUSAL + "\n"

    def setUp(self) -> None:
        self.calls: list[list[str]] = []

    def test_a_refused_down_stops_a_fork_restore(self) -> None:
        with mock.patch.object(ql, "run", side_effect=self.refused), mock.patch.object(ql, "say"):
            with self.assertRaises(ql.DownRefused) as stop:
                ql.fork_restore("cold-test-108")
        self.assertEqual(str(stop.exception), self.REFUSAL)
        self.assertEqual(len(self.calls), 1)  # the down only: no deploy script ran

    def test_a_refused_down_stops_a_boot_with_no_cleanup_down(self) -> None:
        with (mock.patch.object(ql, "TS3", "TS3W.exe"), mock.patch.object(ql, "say") as say,
              mock.patch.object(ql.stack_claim, "warning", return_value=None), mock.patch.object(ql, "run", side_effect=self.refused),
              mock.patch.object(ql.machine_load, "Sampler", FakeSampler), mock.patch.object(ql, "record_boot"),
              tempfile.TemporaryDirectory() as folder, mock.patch.object(ql, "RUN", Path(folder))):
            with self.assertRaises(ql.DownRefused) as stop:
                ql.boot({}, {}, parallel=False)
        self.assertEqual(str(stop.exception), self.REFUSAL)
        self.assertEqual(len(self.calls), 1)
        self.assertNotIn(mock.call("parallel boot failed: stack down"), say.call_args_list)

    def test_a_down_gives_its_receipt(self) -> None:
        with mock.patch.object(ql, "run", return_value='{"ok": true, "receipt": "TS3W remaining = 0"}\n'):
            self.assertEqual(ql.stack_down(), '{"ok": true, "receipt": "TS3W remaining = 0"}')


class SequentialBoot(unittest.TestCase):
    def test_a_failed_boot_still_records_its_row_with_the_load_and_why(self) -> None:
        with (mock.patch.object(ql, "TS3", "TS3W.exe"), mock.patch.object(ql, "say"), mock.patch.object(ql, "ensure_boot_save"),
              mock.patch.object(ql.stack_claim, "warning", return_value=None), mock.patch.object(ql, "run", return_value="{}\n"),
              mock.patch.object(ql, "controller_failed", return_value="Fatal: no world"),
              mock.patch.object(ql.machine_load, "Sampler", FakeSampler), mock.patch.object(ql, "record_boot") as record,
              mock.patch.object(ql, "dismiss_startup_dialogs") as dismiss, tempfile.TemporaryDirectory() as folder,
              mock.patch.object(ql, "RUN", Path(folder))):
            with self.assertRaises(SystemExit) as stop:
                ql.boot({}, {}, parallel=False)
        self.assertEqual(str(stop.exception), "server controller failed: Fatal: no world")
        mode, marks = record.call_args[0]
        self.assertEqual((mode, list(marks)), ("sequential", ["server_launch", "server_ready"]))
        self.assertEqual(record.call_args.kwargs["failed"], "server controller failed: Fatal: no world")
        self.assertEqual(record.call_args.kwargs["load"], FAKE_LOAD)
        dismiss.assert_not_called()
        # a boot that stopped before any game launched has no row
        with (mock.patch.object(ql, "TS3", "TS3W.exe"), mock.patch.object(ql, "say"),
              mock.patch.object(ql.stack_claim, "warning", side_effect=KeyboardInterrupt),
              mock.patch.object(ql.machine_load, "Sampler", FakeSampler), mock.patch.object(ql, "record_boot") as record):
            with self.assertRaises(KeyboardInterrupt):
                ql.boot({}, {}, parallel=False)
        record.assert_not_called()
        self.assertEqual((ql.failure_text(KeyboardInterrupt()), ql.failure_text(SystemExit(3)), ql.failure_text(ValueError("x"))),
                         ("interrupted", "exit 3", "ValueError: x"))

    def test_boot_order_marks_record_and_one_dismiss(self) -> None:
        calls: list[list[str]] = []
        environments: list[dict | None] = []
        with (mock.patch.object(ql, "TS3", "TS3W.exe"), mock.patch.object(ql, "say"), mock.patch.object(ql, "ensure_boot_save"),
              mock.patch.object(ql.stack_claim, "warning", return_value=None), mock.patch.object(ql.machine_load, "Sampler", FakeSampler),
              mock.patch.object(ql, "run", side_effect=lambda cmd, **kw: calls.append(cmd) or environments.append(kw.get("env")) or "{}\n"),
              mock.patch.object(ql, "controller_failed", return_value=None), mock.patch.object(ql, "server_in_world", return_value=True),
              mock.patch.object(ql, "wait_claim"), mock.patch.object(ql, "record_boot") as record,
              mock.patch.object(ql, "dismiss_startup_dialogs") as dismiss, tempfile.TemporaryDirectory() as folder,
              mock.patch.object(ql, "RUN", Path(folder))):
            (Path(folder) / "client.log").write_text("", encoding="utf-8")
            (Path(folder) / "quick_loop-boot-client.out").write_text("last boot", encoding="utf-8")
            ql.boot({"SIMLINK_GCGATE": "1"}, {}, parallel=False)  # --sequential (the default boots in parallel)
            # the last parallel boot's output waits as .prev.out for stackctl's archive at the server's launch (G13)
            self.assertEqual((Path(folder) / "quick_loop-boot-client.prev.out").read_text(encoding="utf-8"), "last boot")
            self.assertFalse((Path(folder) / "quick_loop-boot-client.out").exists())
        # the GC reader (gc_watch.py start) after the down, before the server's TS3 starts
        self.assertEqual([c[2] for c in calls], ["down", "start", "boot-server", "boot-client"])
        self.assertEqual(environments[2]["SIMSONLINE_CHECKPOINT_SAVE"], "Option4BookChain-checkpoint.sims3")
        self.assertEqual(calls[1][1], "scripts/gc_watch.py")
        mode, marks = record.call_args[0]
        self.assertEqual(mode, "sequential")
        self.assertEqual(record.call_args.kwargs, {"server_env": {"SIMLINK_GCGATE": "1"}, "client_env": {}, "load": FAKE_LOAD})
        self.assertEqual(list(marks), ["server_launch", "server_ready", "server_in_world", "client_launch", "client_claimed"])
        dismiss.assert_called_once()


@unittest.skip('internal crash_catch/gc_watch helper excluded in public mirror')
class RejoinWarm(unittest.TestCase):
    """rejoin:warm takes the client's running TS3W from crash_catch.live_games, not client-ts3.pid alone."""

    def rejoin(self, games: dict, controller: int | None = 7001) -> list[tuple[list[str], dict | None]]:
        import crash_catch

        calls: list[tuple[list[str], dict | None]] = []
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / ".omo" / "run").mkdir(parents=True)
            if controller:
                (root / ".omo/run/client.state.json").write_text(json.dumps({"pid": controller}), encoding="utf-8")

            def fake_run(cmd: list[str], env: dict | None = None, **kw: object) -> str:
                calls.append((cmd, env))
                if "stackctl.py" in " ".join(cmd):
                    log = root / ".omo/run/client.log"
                    log.write_text('{"$type":"sim_claimed"}\n', encoding="utf-8")
                    # Windows stamps a file from its coarse clock, up to a tick behind time.time(): the claim is new.
                    os.utime(log, (time.time() + 1, time.time() + 1))
                return ""

            with (mock.patch.object(crash_catch, "live_games", return_value=games), mock.patch.object(ql, "ROOT", root),
                  mock.patch.object(ql, "run", side_effect=fake_run), mock.patch.object(ql, "say"),
                  mock.patch("socket.create_connection", side_effect=OSError("gone")),
                  mock.patch.object(ql.time, "sleep")):
                ql.rejoin_warm()
        return calls

    def test_the_client_game_comes_from_live_games(self) -> None:
        calls = self.rejoin({"client": {"pid": 58848, "source": "client.state.json gamePid", "refused": []}})
        self.assertEqual(calls[0][0], ["taskkill", "/F", "/PID", "7001"])  # client 1's controller only
        cmd, env = calls[1]
        self.assertIn("client", cmd)
        self.assertEqual(env["SIMSONLINE_CLIENT_ATTACH_PID"], "58848")

    def test_no_running_client_game_names_why(self) -> None:
        refused = ["client.state.json gamePid 99999: not running", "client-ts3.pid 4212: not running"]
        with self.assertRaises(SystemExit) as raised:
            self.rejoin({"client": {"pid": None, "source": None, "refused": refused}})
        self.assertIn("gamePid 99999: not running; client-ts3.pid 4212: not running", str(raised.exception))
        with self.assertRaises(SystemExit) as raised:
            self.rejoin({})
        self.assertIn("no client.state.json or client-ts3.pid", str(raised.exception))

    def test_no_controller_pid_kills_nothing(self) -> None:
        with self.assertRaises(SystemExit) as raised:
            self.rejoin({"client": {"pid": 58848, "source": "client.state.json gamePid", "refused": []}}, controller=None)
        self.assertIn("no controller pid", str(raised.exception))


class RejoinPaused(unittest.TestCase):
    """rejoin:paused pauses and resumes every game through the session (simctl speed), not the server's bridge alone:
    a player joining during the step was left paused until a later snapshot set the speed again."""

    def step(self, simctl=None, boot=None) -> tuple[list[list[str]], list[int], list[str], mock.Mock]:
        calls: list[list[str]] = []
        timeouts: list[int] = []
        said: list[str] = []

        def fake_run(cmd: list[str], env: dict | None = None, timeout: int = 1200, **kw: object) -> str:
            calls.append(cmd)
            timeouts.append(timeout)
            return simctl(cmd) if simctl else "game speed set: x\n"

        with mock.patch.dict(os.environ, {"SIMSONLINE_COMPARE_CLIENT": ""}), \
                mock.patch.object(ql, "run", side_effect=fake_run), mock.patch.object(ql, "bridge") as bridge, \
                mock.patch.object(ql, "boot_client", side_effect=boot), mock.patch.object(ql.time, "sleep"), \
                mock.patch.object(ql, "say", side_effect=said.append):
            try:
                ql.run_steps(["rejoin:paused"], dict(ql.SIMS))
            except RuntimeError:
                pass
        return calls, timeouts, said, bridge

    def test_pause_and_resume_go_through_the_session(self) -> None:
        calls, timeouts, said, bridge = self.step()
        self.assertEqual([c[1:] for c in calls], [
            ["scripts/simctl.py", "--timeout", str(ql.SIMCTL_TIMEOUT), "speed", "pause"],
            ["scripts/simctl.py", "--timeout", str(ql.SIMCTL_TIMEOUT), "speed", "normal"]])
        self.assertTrue(all(ql.SIMCTL_TIMEOUT < t for t in timeouts))  # simctl's deadline under its process's
        bridge.assert_not_called()  # SET_GAME_SPEED on the server's bridge moved the server's game only
        self.assertIn("rejoin: resume every game, simctl speed normal: game speed set: x", said)

    def test_a_pause_without_an_answer_is_reported_and_still_resumes(self) -> None:
        def simctl(cmd: list[str]) -> str:
            if cmd[-1] == "pause":
                raise subprocess.TimeoutExpired(cmd, 60)
            return "game speed set: Normal\n"

        calls, _, said, _ = self.step(simctl)
        self.assertIn("rejoin: pause every game, simctl speed pause: simctl: no answer", said)
        self.assertEqual(calls[-1][-2:], ["speed", "normal"])

    def test_a_resume_without_an_answer_is_reported_after_a_failed_boot(self) -> None:
        def simctl(cmd: list[str]) -> str:
            if cmd[-1] == "normal":
                raise subprocess.TimeoutExpired(cmd, 60)
            return "game speed set: Pause\n"

        def boot(*a: object, **kw: object) -> None:
            raise RuntimeError("client boot failed")

        calls, _, said, _ = self.step(simctl, boot)
        self.assertEqual([c[-1] for c in calls], ["pause", "normal"])
        self.assertIn("rejoin: resume every game, simctl speed normal: simctl: no answer", said)


class RejoinPlayer2(unittest.TestCase):
    """Under scenarios.py --client 2 a rejoin restarts player 2's game, not client 1's."""

    def restart(self, sim: str = "", now_on: str = "Pablo") -> tuple[list[tuple[list[str], dict | None]], mock.Mock]:
        calls: list[tuple[list[str], dict | None]] = []

        def fake_run(cmd: list[str], env: dict | None = None, **kw: object) -> str:
            calls.append((cmd, env))
            if "boot-client2" in cmd:
                log = ql.RUN / "client2.log"
                log.write_text('{"$type":"sim_claimed"}\n', encoding="utf-8")
                os.utime(log, (time.time() + 1, time.time() + 1))
            return ""

        with tempfile.TemporaryDirectory() as folder, \
                mock.patch.object(ql, "RUN", Path(folder)), mock.patch.object(ql, "ROOT", Path(folder)), \
                mock.patch.object(ql, "run", side_effect=fake_run), mock.patch.object(ql, "say"), \
                mock.patch.object(ql.stack_claim, "warning", return_value=None),                 mock.patch.object(ql, "dismiss_startup_dialogs") as dismiss, \
                mock.patch.object(ql, "client2_sim", return_value=now_on), \
                mock.patch.object(household_read, "sim_id", side_effect=lambda n: {"Iliana": "14167198646267834864"}[n]), \
                mock.patch.dict(os.environ, {"SIMSONLINE_COMPARE_CLIENT": "2"}), mock.patch.object(ql.time, "sleep"):
            self.assertTrue(ql.player2())
            ql.boot_client2_restart(sim)
        return calls, dismiss

    def test_client2_goes_down_and_boots_on_pablo(self) -> None:
        calls, dismiss = self.restart()
        self.assertEqual(calls[0][0][1:], ["scripts/stackctl.py", "client2-down"])
        boot, env = calls[1]
        self.assertIn("boot-client2", boot)
        self.assertEqual(boot[boot.index("--claim-sim2") + 1], ql.SIMS["Pablo"])
        self.assertEqual(boot[boot.index("--sandboxie-box2") + 1], "Client3Box")
        self.assertEqual(env["SIMSONLINE_WORLD_FROM_SERVER"], "1")
        # its "Mod Scripts Found" notice stayed up over a suite (run 20261021-024712)
        dismiss.assert_called_once_with("2")

    def test_a_late_join_keeps_the_sim_player_2_was_on(self) -> None:
        # player 2 of the Langeraks joins again as Iliana, not as Pablo
        calls, _ = self.restart(now_on="Iliana")
        boot = calls[1][0]
        self.assertEqual(boot[boot.index("--claim-sim2") + 1], "14167198646267834864")

    def test_as_names_the_sim(self) -> None:
        calls, _ = self.restart(sim="Iliana")
        boot = calls[1][0]
        self.assertEqual(boot[boot.index("--claim-sim2") + 1], "14167198646267834864")
        calls, _ = self.restart(sim="Pablo", now_on="Iliana")
        boot = calls[1][0]
        self.assertEqual(boot[boot.index("--claim-sim2") + 1], ql.SIMS["Pablo"])

    def test_an_unknown_sim_stops_before_the_game_goes_down(self) -> None:
        with self.assertRaises(KeyError):
            self.restart(sim="Nobody")

    def test_rejoin_as_is_taken_out_of_the_step(self) -> None:
        self.assertEqual(ql.rejoin_as("2:paused:as=Iliana"), ("2:paused", "Iliana"))
        self.assertEqual(ql.rejoin_as("2:as=Iliana"), ("2", "Iliana"))
        self.assertEqual(ql.rejoin_as("2:paused"), ("2:paused", ""))
        self.assertEqual(ql.rejoin_as(""), ("", ""))
        self.assertEqual(ql.rejoin_target(ql.rejoin_as("2:paused:as=Iliana")[0]), (True, "paused"))

    def test_as_on_client_1_is_refused(self) -> None:
        with mock.patch.dict(os.environ, {"SIMSONLINE_COMPARE_CLIENT": ""}), \
                mock.patch.object(ql, "boot_client") as boot, mock.patch.object(ql, "say"):
            with self.assertRaises(SystemExit):
                ql.run_steps(["rejoin:1:as=Iliana"], dict(ql.SIMS))
        boot.assert_not_called()

    def test_sim_ref_finds_other_names_once(self) -> None:
        sims = dict(ql.SIMS)
        with mock.patch.object(household_read, "sim_id", return_value="14167198646267834864") as find:
            self.assertEqual(ql.sim_ref(sims, "Iliana"), "14167198646267834864")
            self.assertEqual(ql.sim_ref(sims, "Iliana"), "14167198646267834864")
            self.assertEqual(ql.sim_ref(sims, "Pablo"), ql.SIMS["Pablo"])
        find.assert_called_once_with("Iliana")

    def test_client_1_by_default(self) -> None:
        with mock.patch.dict(os.environ, {"SIMSONLINE_COMPARE_CLIENT": ""}):
            self.assertFalse(ql.player2())


class TwoClientBoot(unittest.TestCase):
    """run --boot --clients 2: player 2's game boots after client 1's claim, in one command (2026-10-21 it was typed by
    hand after every boot)."""

    def main(self, argv: list[str], env: dict[str, str] | None = None) -> list[tuple]:
        events: list[tuple] = []
        self.checked: list[tuple | None] = []  # what came last before each household_check
        with mock.patch.object(ql, "boot", side_effect=lambda s, c, parallel=None: events.append(("boot", c))), \
                mock.patch.object(ql, "household_check", side_effect=lambda: self.checked.append(events[-1] if events else None)), \
                mock.patch.object(ql, "boot_client2", side_effect=lambda c, restart, sim="", player="2": events.append(
                    (f"client{player}", c, restart) if sim == "Pablo" else (f"client{player}", c, restart, sim))), \
                mock.patch.object(ql, "test_setup", side_effect=lambda extra=None: events.append(
                    ("needs",) if extra in (None, [], ["Pablo"]) else ("needs", extra))), \
                mock.patch.object(ql, "run_steps"), mock.patch.object(ql, "say"), \
                mock.patch.object(ql, "physical_fork_census", return_value={"state": "fork"}), \
                mock.patch.object(ql.fork_census, "write_evidence"), \
                mock.patch.dict(os.environ, env or {"SIMSONLINE_COMPARE_CLIENT": ""}), \
                mock.patch.object(sys, "argv", ["quick_loop.py", *argv]):
            ql.main()
        return events

    def test_client_2_after_client_1_and_needs_static_last(self) -> None:
        events = self.main(["run", "--boot", "--clients", "2", "--client-env", "A=1"])
        self.assertEqual(events, [("boot", {"A": "1"}), ("client2", {"A": "1"}, False), ("needs",)])

    def test_one_client_by_default_and_two_under_compare_client_2(self) -> None:
        self.assertEqual(self.main(["run", "--boot"]), [("boot", {}), ("needs",)])
        events = self.main(["run", "--boot"], {"SIMSONLINE_COMPARE_CLIENT": "2"})
        self.assertEqual(events, [("boot", {}), ("client2", {}, False), ("needs",)])
        events = self.main(["run", "--boot", "--clients", "1"], {"SIMSONLINE_COMPARE_CLIENT": "2"})
        self.assertEqual(events, [("boot", {}), ("needs",)])

    def test_the_server_s_household_is_checked_after_the_boot(self) -> None:
        self.main(["run", "--boot", "--clients", "2"])
        self.assertEqual(self.checked, [("needs",)])  # once, last
        self.main(["run", "--boot", "--live-needs"])
        self.assertEqual(self.checked, [("boot", {})])
        self.main(["run"])
        self.assertEqual(self.checked, [])  # no boot, no check

    def test_player2_names_player_2_s_sim_and_its_needs(self) -> None:
        events = self.main(["run", "--boot", "--clients", "2", "--player2", "Iliana"])
        self.assertEqual(events, [("boot", {}), ("client2", {}, False, "Iliana"), ("needs", ["Iliana"])])

    def test_clients_needs_a_boot_and_is_1_2_or_3(self) -> None:
        with self.assertRaises(SystemExit):
            self.main(["run", "--clients", "2"])
        with self.assertRaises(SystemExit):
            self.main(["run", "--boot", "--clients", "4"])

    def test_three_clients_boot_in_order_and_player_3_is_gunther(self) -> None:
        """--clients 3 boots player 2's game, then player 3's (Gunther unless --player3 names another)."""
        events = self.main(["run", "--boot", "--clients", "3"])
        self.assertEqual(events, [("boot", {}), ("client2", {}, False), ("client3", {}, False, "Gunther"),
                                  ("needs", ["Pablo", "Gunther"])])
        events = self.main(["run", "--boot", "--clients", "3", "--player2", "Iliana", "--player3", "Morgana"])
        self.assertEqual(events[1:], [("client2", {}, False, "Iliana"), ("client3", {}, False, "Morgana"),
                                      ("needs", ["Iliana", "Morgana"])])

    def test_player_3_boots_in_its_own_box_on_its_own_verbs(self) -> None:
        calls: list[list[str]] = []
        with mock.patch.object(ql, "run", side_effect=lambda cmd, env=None, **kw: calls.append(cmd) or ""), \
                mock.patch.object(ql, "sim_ref", return_value="999"), mock.patch.object(ql, "wait_claim"), \
                mock.patch.object(ql, "controller_failed", return_value=""), mock.patch.object(ql, "say"), \
                mock.patch.object(ql, "dismiss_startup_dialogs") as dismissed:
            ql.boot_client2({}, restart=False, sim="Gunther", player="3")
            with self.assertRaises(SystemExit):
                ql.boot_client2({}, restart=False, player="3")  # player 3 has no default Sim
        boot = calls[0]
        self.assertEqual(boot[2], "boot-client3")
        self.assertEqual(boot[boot.index("--sandboxie-box3") + 1], "Client4Box")
        self.assertEqual(boot[boot.index("--claim-sim3") + 1], "999")
        dismissed.assert_called_once_with("3")

    def test_a_fresh_client_2_boots_without_a_down_and_with_the_run_s_client_env(self) -> None:
        calls: list[tuple[list[str], dict | None]] = []

        def fake_run(cmd: list[str], env: dict | None = None, **kw: object) -> str:
            calls.append((cmd, env))
            log = ql.RUN / "client2.log"
            log.write_text('{"$type":"sim_claimed"}\n', encoding="utf-8")
            os.utime(log, (time.time() + 1, time.time() + 1))
            return ""

        with tempfile.TemporaryDirectory() as folder, \
                mock.patch.object(ql, "RUN", Path(folder)), mock.patch.object(ql, "run", side_effect=fake_run), \
                mock.patch.object(ql, "say"), mock.patch.object(ql.stack_claim, "warning") as warning, \
                mock.patch.object(ql, "dismiss_startup_dialogs") as dismiss, mock.patch.object(ql.time, "sleep"):
            ql.boot_client2({"SIMSONLINE_PRESENTER": "1"}, restart=False)
        self.assertEqual(len(calls), 1)  # no client2-down: the boot's own down stopped every game
        boot, env = calls[0]
        self.assertIn("boot-client2", boot)
        self.assertEqual(env["SIMSONLINE_PRESENTER"], "1")
        self.assertEqual(env["SIMSONLINE_SIMLINK_CLIENT_RING"], "1")
        warning.assert_not_called()  # the boot warned already
        dismiss.assert_called_once_with("2")

    def test_the_dismiss_pairs_its_client_and_puts_the_run_s_choice_back(self) -> None:
        seen: list[str] = []
        fake_view = mock.Mock()
        fake_view.dismiss.side_effect = lambda **kw: seen.append(os.environ.get("SIMSONLINE_COMPARE_CLIENT", "")) or "ok"
        with mock.patch.dict(sys.modules, {"ts3_view": fake_view}), mock.patch.object(ql, "say"), \
                mock.patch.object(ql.time, "sleep"), mock.patch.object(ql.os, "name", "nt"), \
                mock.patch.dict(os.environ, {"SIMSONLINE_COMPARE_CLIENT": "127.0.0.1:34031"}):
            ql.dismiss_startup_dialogs("1")  # a --client 2 boot: client 1's notice first
            ql.dismiss_startup_dialogs("2")
            self.assertEqual(os.environ["SIMSONLINE_COMPARE_CLIENT"], "127.0.0.1:34031")
        self.assertEqual(seen, ["127.0.0.1:34030", "127.0.0.1:34031"])
        with mock.patch.dict(os.environ, {}, clear=False):
            os.environ.pop("SIMSONLINE_COMPARE_CLIENT", None)
            with ql.paired_client("2"):
                self.assertEqual(os.environ["SIMSONLINE_COMPARE_CLIENT"], "127.0.0.1:34031")
            self.assertNotIn("SIMSONLINE_COMPARE_CLIENT", os.environ)


def meta_payload(*strings: str) -> bytes:
    """A Meta.data record as the game writes it: a version word, then length-prefixed UTF-16 strings."""
    out = (3).to_bytes(4, "little")
    for s in strings:
        out += len(s).to_bytes(4, "little") + s.encode("utf-16-le")
    return out + bytes(8)


class ActiveHousehold(unittest.TestCase):
    """quick_loop active-household: twice on 2026-10-22 the server came up on the Goths."""

    def test_meta_strings_reads_the_world_household_and_description(self) -> None:
        payload = meta_payload("Sunset Valley", "Martinez", "Lovebirds Pablo and Jennifer Martinez...")
        self.assertEqual(ql.meta_strings(payload), ["Sunset Valley", "Martinez", "Lovebirds Pablo and Jennifer Martinez..."])
        self.assertEqual(ql.meta_strings(payload[:20]), [])  # cut short
        self.assertEqual(ql.meta_strings((3).to_bytes(4, "little") + (99999).to_bytes(4, "little")), [])

    def test_save_household_reads_the_save_s_meta_data(self) -> None:
        import dbpf_grep
        with tempfile.TemporaryDirectory() as folder:
            save = Path(folder)
            (save / "Meta.data").write_bytes(b"DBPF")
            record = dbpf_grep.Record(7, 0, 0x628A788F, meta_payload("Sunset Valley", "Goth", "-"), "refpack")
            with mock.patch.object(dbpf_grep, "records", return_value=iter([record])):
                self.assertEqual(ql.save_household(save), ("Goth", ""))
            with mock.patch.object(dbpf_grep, "records", return_value=None):
                self.assertIsNone(ql.save_household(save)[0])
            (save / "Meta.data").unlink()
            household, why = ql.save_household(save)
        self.assertIsNone(household)
        self.assertIn("FileNotFoundError", why)

    def check(self, active: dict | None, saved: str | None, error: str | None = None) -> tuple[int, list[str]]:
        said: list[str] = []
        reading = {"active": active, "households": {}, "sims": {}, "pairs": {}, "error": error}
        with mock.patch.object(household_read, "read", return_value=reading), \
                mock.patch.object(ql, "save_household", return_value=(saved, "" if saved else "no Meta.data")), \
                mock.patch.object(ql, "say", side_effect=said.append):
            return ql.household_check(), said

    JENNIFER = {"name": "Jennifer", "sdid": "1", "object": "18031287978496800304", "lot": "5", "age": "YA",
                "household": "Martinez"}
    GUNTHER = {"name": "Gunther", "sdid": "2", "object": "77", "lot": "9", "age": "Adult", "household": "Goth"}

    def test_the_martinezes_live_and_in_the_save(self) -> None:
        code, said = self.check(self.JENNIFER, "Martinez")
        self.assertEqual(code, 0)
        self.assertEqual(said, ["server household: live Martinez (selected Jennifer, id 18031287978496800304); "
                                "boot save Martinez"])

    def test_another_family_live_or_in_the_save_warns(self) -> None:
        code, said = self.check(self.GUNTHER, "Martinez")
        self.assertEqual(code, 1)
        self.assertTrue(said[-1].startswith("WARNING: the server is live on Goth, not the Martinezes"))
        self.assertIn("SET_ACTIVE_SIM 18031287978496800304", said[-1])
        code, said = self.check(self.JENNIFER, "Goth")
        self.assertEqual(code, 1)
        self.assertIn("WARNING: the server is its boot save on Goth, not", said[-1])
        code, said = self.check(None, "Goth")  # no Sim selected: no active household at all
        self.assertEqual(code, 1)
        self.assertIn("live on no household (no Sim selected) and its boot save on Goth", said[-1])

    def test_a_game_that_does_not_answer_still_reads_the_save(self) -> None:
        code, said = self.check(None, "Martinez", error="no reply from 127.0.0.1:34029")
        self.assertEqual(code, 0)
        self.assertEqual(said, ["server household: live unread (no reply from 127.0.0.1:34029); boot save Martinez"])
        code, said = self.check(None, None, error="no reply")
        self.assertEqual(code, 2)
        self.assertEqual(len(said), 1)  # nothing to warn about: nothing was read

    def test_the_verb_exits_with_the_check_s_code(self) -> None:
        with mock.patch.object(ql, "household_check", return_value=1), \
                mock.patch.object(sys, "argv", ["quick_loop.py", "active-household"]), self.assertRaises(SystemExit) as stop:
            ql.main()
        self.assertEqual(stop.exception.code, 1)


class FakeProc:
    def __init__(self, code: int | None = None) -> None:
        self.code = code

    def poll(self) -> int | None:
        return self.code


class BridgeTrace(unittest.TestCase):
    """--bridge-trace: stackctl's shared flags are parents of each verb's subparser, so they parse only after the verb.
    Every launch's argv goes through stackctl's real parser (its main(), stopped at parse_args)."""

    def setUp(self) -> None:
        self.env = mock.patch.dict(os.environ)
        self.env.start()
        os.environ.pop("SIMSONLINE_BRIDGE_TRACE", None)

    def tearDown(self) -> None:
        self.env.stop()

    @staticmethod
    def parsed(argv: list[str]):
        """stackctl's parse of a launch's argv (script and all): its namespace, or SystemExit(2) on a flag it rejects."""
        import argparse
        import stackctl

        class Parsed(Exception):
            pass

        real = argparse.ArgumentParser.parse_args

        def capture(self, args=None, namespace=None):
            raise Parsed(real(self, args, namespace))

        with (mock.patch.object(sys, "argv", ["stackctl.py", *argv[1:]]),
              mock.patch.object(argparse.ArgumentParser, "parse_args", capture), contextlib.redirect_stderr(io.StringIO())):
            try:
                stackctl.main()
            except Parsed as done:
                return done.args[0]
        raise AssertionError("stackctl.main() never parsed")

    def test_the_real_parser_takes_the_flag_after_the_verb_and_not_before(self) -> None:
        self.assertTrue(self.parsed(["scripts/stackctl.py", "boot-server", "--bridge-trace"]).bridge_trace)
        with self.assertRaises(SystemExit) as stop:
            self.parsed(["scripts/stackctl.py", "--bridge-trace", "boot-server"])
        self.assertEqual(stop.exception.code, 2)

    def test_off_by_default_the_argv_is_unchanged(self) -> None:
        with mock.patch.object(ql, "BRIDGE_TRACE", False):
            self.assertEqual(ql.server_boot_args(), ql.SERVER_BOOT)
            for argv in (ql.server_boot_args(), ql.client_boot_args(False), ql.client_boot_args(True)):
                self.assertNotIn("--bridge-trace", argv)
                self.assertFalse(self.parsed(["scripts/stackctl.py", *argv[1:]]).bridge_trace)

    def test_each_boot_argv_parses_with_the_flag(self) -> None:
        with mock.patch.object(ql, "BRIDGE_TRACE", True):
            launches = {"boot-server": ql.server_boot_args(), "boot-client": ql.client_boot_args(False),
                        "boot-client-restart": ql.client_boot_args(True)}
        for verb, argv in launches.items():
            args = self.parsed(argv)
            self.assertEqual((args.verb, args.bridge_trace), (verb, True), argv)

    def test_the_env_switch_does_the_same(self) -> None:
        os.environ["SIMSONLINE_BRIDGE_TRACE"] = "1"
        with mock.patch.object(ql, "BRIDGE_TRACE", False):
            self.assertTrue(self.parsed(ql.server_boot_args()).bridge_trace)

    def test_player_two_boot_and_client_attach_parse_with_it(self) -> None:
        calls: list[list[str]] = []
        with (mock.patch.object(ql, "BRIDGE_TRACE", True), mock.patch.object(ql, "say"), mock.patch.object(ql, "wait_claim"),
              mock.patch.object(ql, "controller_failed", return_value=None), mock.patch.object(ql, "sim_ref", return_value="1"),
              mock.patch.object(ql.stack_claim, "warning", return_value=None),
              mock.patch.object(ql, "run", side_effect=lambda cmd, **kw: calls.append(cmd) or "{}"),
              mock.patch.object(ql, "dismiss_startup_dialogs"), mock.patch.object(socket, "create_connection", side_effect=OSError)):
            ql.boot_client2({}, restart=False, sim="Pablo")
        boot = [c for c in calls if "boot-client2" in c]
        self.assertEqual(len(boot), 1)
        args = self.parsed(boot[0][1:])
        self.assertEqual((args.verb, args.bridge_trace), ("boot-client2", True))

    @unittest.skip('internal crash_catch/gc_watch helper excluded in public mirror')
    def test_warm_rejoin_attach_parses_with_it(self) -> None:
        games = {"client": {"pid": 58848, "source": "client.state.json gamePid", "refused": []}}
        with mock.patch.object(ql, "BRIDGE_TRACE", True):
            calls = RejoinWarm.rejoin(self, games)  # type: ignore[arg-type]  # the helper uses no state of its own
        attach = [cmd for cmd, _env in calls if "stackctl.py" in " ".join(cmd)]
        self.assertEqual(len(attach), 1)
        args = self.parsed(attach[0][1:])
        self.assertEqual((args.verb, args.bridge_trace), ("client", True))

    def test_parallel_boot_popen_argv_parses_with_it(self) -> None:
        launched: list[list[str]] = []

        def popen(cmd, **kwargs):
            launched.append(cmd)
            return FakeProc(0)

        with tempfile.TemporaryDirectory() as folder, mock.patch.object(ql, "RUN", Path(folder)), mock.patch.object(ql, "say"), \
                mock.patch.object(ql, "BRIDGE_TRACE", True), mock.patch.object(ql.subprocess, "Popen", side_effect=popen), \
                mock.patch.object(ql, "server_launched", return_value=1), mock.patch.object(ql, "read_state", return_value={}), \
                mock.patch.object(ql, "wait_claim"), mock.patch.object(ql, "controller_failed", return_value=None), \
                mock.patch.object(ql, "server_in_world", return_value=True):
            ql.boot_parallel({}, {})
        self.assertEqual([(a.verb, a.bridge_trace) for a in map(self.parsed, (c[1:] for c in launched))],
                         [("boot-server", True), ("boot-client", True)])

    def test_run_and_fork_cycle_parse_the_flag(self) -> None:
        for argv in (["run", "--bridge-trace"], ["fork-cycle", "--bridge-trace"]):
            seen: list[bool] = []
            with mock.patch.object(sys, "argv", ["quick_loop.py", *argv]), mock.patch.object(ql, "BRIDGE_TRACE", False), \
                    mock.patch.object(ql, "run_steps", side_effect=lambda *a, **k: seen.append(ql.BRIDGE_TRACE)), \
                    mock.patch.object(ql, "fork_cycle", side_effect=lambda *a: seen.append(ql.BRIDGE_TRACE)), mock.patch.object(ql, "say"):
                ql.main()
            self.assertEqual(seen, [True], argv)


class ParallelBoot(unittest.TestCase):
    def setUp(self) -> None:
        self.folder = tempfile.TemporaryDirectory()
        self.run_dir = Path(self.folder.name)
        self.patches = [mock.patch.object(ql, "RUN", self.run_dir), mock.patch.object(ql, "say"),
                        mock.patch.object(ql.time, "sleep"), mock.patch.object(ql, "controller_failed", return_value=None),
                        mock.patch.object(ql, "server_in_world", return_value=True)]
        for p in self.patches:
            p.start()
        self.launched: list[list[str]] = []

    def tearDown(self) -> None:
        for p in self.patches:
            p.stop()
        self.folder.cleanup()

    def test_parallel_boot_forwards_checkpoint_default_and_override(self) -> None:
        for overrides, expected in (({}, "Option4BookChain-checkpoint.sims3"),
                                    ({"SIMSONLINE_CHECKPOINT_SAVE": "Other.sims3"}, "Other.sims3")):
            environments: list[dict] = []
            with (mock.patch.object(ql.subprocess, "Popen",
                                    side_effect=lambda cmd, **kw: environments.append(kw["env"]) or FakeProc(0)),
                  mock.patch.object(ql, "server_launched", return_value=1), mock.patch.object(ql, "read_state", return_value={}),
                  mock.patch.object(ql, "wait_claim")):
                ql.boot_parallel(overrides, {})
            self.assertEqual(environments[0]["SIMSONLINE_CHECKPOINT_SAVE"], expected)

    def popen(self, codes: dict[str, int | None]):
        def make(cmd, **kwargs):
            side = "server" if "boot-server" in cmd else "client"
            self.launched.append(cmd)
            if side == "client":
                log = self.run_dir / "client.log"
                log.write_text('{"$type":"sim_claimed"}\n', encoding="utf-8")
                os.utime(log, (time.time() + 5, time.time() + 5))  # a file time can trail time.time() by a tick
            return FakeProc(codes.get(side))
        return make

    def test_client_starts_after_the_servers_game_and_both_finish(self) -> None:
        states = iter([None, {"startedUtc": ql.utc_now(), "gamePid": os.getpid()}])
        with mock.patch.object(ql.subprocess, "Popen", side_effect=self.popen({})), \
                mock.patch.object(ql, "read_state", side_effect=lambda _p: next(states)), mock.patch.object(ql, "run") as run:
            marks = ql.boot_parallel({}, {})
        self.assertEqual(["boot-server" in c[2] for c in self.launched], [True, False])
        self.assertIn("boot-client", self.launched[1][2])
        self.assertEqual(set(marks), {"server_launch", "client_launch", "server_in_world", "client_claimed"})
        run.assert_not_called()  # no stack down

    def test_a_listener_not_ready_exit_still_waits_for_the_claim(self) -> None:
        states = iter([None, {"startedUtc": ql.utc_now(), "gamePid": os.getpid()}])
        with mock.patch.object(ql.subprocess, "Popen", side_effect=self.popen({"client": 1})), \
                mock.patch.object(ql, "read_state", side_effect=lambda _p: next(states)), mock.patch.object(ql, "run") as run:
            marks = ql.boot_parallel({}, {})
        self.assertIn("client_claimed", marks)
        run.assert_not_called()  # no stack down

    def test_a_failed_server_takes_the_stack_down(self) -> None:
        (self.run_dir / "quick_loop-boot-server.out").write_text("", encoding="utf-8")
        with mock.patch.object(ql.subprocess, "Popen", side_effect=self.popen({"server": 2})), \
                mock.patch.object(ql, "read_state", return_value=None), mock.patch.object(ql, "run") as run:
            marks: dict[str, str] = {}
            with self.assertRaises(SystemExit) as stop:
                ql.boot_parallel({}, {}, marks)
        self.assertEqual(list(marks), ["server_launch"])  # what boot() records the failed boot's row with
        self.assertIn("stackctl boot-server exited 2", str(stop.exception))
        self.assertEqual(len(self.launched), 1)  # the client never started
        self.assertIn("down", run.call_args[0][0])


class StepReplies(unittest.TestCase):
    def test_last_line_cut_or_every_line(self) -> None:
        lines = ["first", "x" * 300]
        with mock.patch.object(ql, "say") as say, contextlib.redirect_stdout(io.StringIO()) as out:
            ql.say_reply("client SACS_RECORDER", lines, False)
            self.assertEqual(say.call_args[0][0], "client SACS_RECORDER: " + "x" * 240)
            ql.say_reply("client SACS_RECORDER", lines, True)
            self.assertEqual(say.call_args[0][0], "client SACS_RECORDER: 2 line(s)")
            ql.say_reply("server X", [], True)
            self.assertEqual(say.call_args[0][0], "server X: (no reply)")
        self.assertEqual(out.getvalue(), "    first\n    " + "x" * 300 + "\n")


class PlayerSteps(unittest.TestCase):
    """cpick:, hud:, notices:mark, notice:, nonotice: (a player's pick, HUD queue and notices)."""

    def test_the_regex_keeps_its_colons(self) -> None:
        self.assertEqual(ql.player_step("2:10:actor=Pablo.*items=0: Sit", timed=True), ("2", 10.0, "actor=Pablo.*items=0: Sit"))
        self.assertEqual(ql.player_step("1:Can't do: x", timed=False), ("1", 0.0, "Can't do: x"))
        self.assertEqual(ql.player_step("3:5:x", timed=True), ("3", 5.0, "x"))  # player 3
        for bad in ("4:10:x", "2:ten:x", "2:10:"):
            with self.assertRaises(SystemExit):
                ql.player_step(bad, timed=True)

    def test_until_fails_where_wait_reports(self) -> None:
        rows = {"Pablo": ["1", "2", "3", "x", "y", "z", "SittingPosture", "-", "-", "Sims3.Sit+Definition"]}
        with mock.patch.object(ql, "probe_rows", return_value=rows), mock.patch.object(ql, "say") as said, \
                mock.patch.object(ql.time, "sleep"), mock.patch.object(ql.time, "monotonic", side_effect=[0, 1, 99, 0, 1, 99]):
            ql.step_wait("server:Pablo:Sleep:30", {"Pablo": "1"}, must=False)
            self.assertIn("TIMED OUT", said.call_args[0][0])
            with self.assertRaises(SystemExit) as stop:
                ql.step_wait(r"server:Pablo:^(?:[^|]*\|){9}(?![^|]*Sit):30", {"Pablo": "1"}, must=True)
        self.assertIn("never matched", str(stop.exception))

    def test_until_keeps_the_regex_colons(self) -> None:
        rows = {"Pablo": ["a:b", "Sit"]}
        with mock.patch.object(ql, "probe_rows", return_value=rows), mock.patch.object(ql, "say") as said:
            ql.step_wait(r"server:Pablo:a:b\|Sit:5", {"Pablo": "1"}, must=True)
        self.assertIn("matched", said.call_args[0][0])

    def test_until_takes_a_client(self) -> None:
        """until:/wait: client is the compared client game; client1 and client2 name a player's game."""
        rows = {"Pablo": ["Sit"]}
        env = {"SIMSONLINE_COMPARE_CLIENT": "2", "SIMSONLINE_CLIENT2_BRIDGECTL": "127.0.0.1:34041"}
        with mock.patch.object(ql, "probe_rows", return_value=rows) as probe, mock.patch.object(ql, "say"), \
                mock.patch.dict(os.environ, env):
            for side, endpoint in (("server", ql.SERVER), ("client", "127.0.0.1:34041"), ("client1", "127.0.0.1:34030"),
                                   ("client2", "127.0.0.1:34041")):
                ql.step_wait(f"{side}:Pablo:Sit:5", {"Pablo": "1"}, must=True)
                self.assertEqual(probe.call_args[0][0], endpoint, side)
            with mock.patch.dict(os.environ, {"SIMSONLINE_COMPARE_CLIENT": ""}):
                ql.step_wait("client:Pablo:Sit:5", {"Pablo": "1"}, must=False)
                self.assertEqual(probe.call_args[0][0], "127.0.0.1:34030")
            with mock.patch.dict(os.environ, {"SIMSONLINE_CLIENT3_BRIDGECTL": "127.0.0.1:34042"}):
                ql.step_wait("client3:Pablo:Sit:5", {"Pablo": "1"}, must=True)
                self.assertEqual(probe.call_args[0][0], "127.0.0.1:34042")
            for bad in ("client4", "both"):
                with self.assertRaises(SystemExit) as stop:
                    ql.step_wait(f"{bad}:Pablo:Sit:5", {"Pablo": "1"}, must=True)
                self.assertIn("no side", str(stop.exception))

    def test_until_matches_a_server_field(self) -> None:
        """{server:N} is the server row's field N at each poll: the copy on the lot the server's Sim is on."""
        server = {"Pablo": ["1", "2", "778", "x"]}
        def rows(endpoint, sims):
            return server if endpoint == ql.SERVER else client
        client = {"Pablo": ["1", "2", "778", "x"]}
        with mock.patch.object(ql, "probe_rows", side_effect=rows), mock.patch.object(ql, "say") as said:
            ql.step_wait(r"client1:Pablo:^(?:[^|]*\|){2}{server:2}\|:5", {"Pablo": "1"}, must=True)
            self.assertIn("matched", said.call_args[0][0])
        client = {"Pablo": ["1", "2", "999", "x"]}
        with mock.patch.object(ql, "probe_rows", side_effect=rows), mock.patch.object(ql, "say"), \
                mock.patch.object(ql.time, "sleep"), mock.patch.object(ql.time, "monotonic", side_effect=[0, 1, 99]):
            with self.assertRaises(SystemExit) as stop:
                ql.step_wait(r"client1:Pablo:^(?:[^|]*\|){2}{server:2}\|:30", {"Pablo": "1"}, must=True)
        self.assertIn("= ^(?:[^|]*\\|){2}778\\|", str(stop.exception))
        with mock.patch.object(ql, "probe_rows", return_value=server):
            self.assertIsNone(ql.with_server_fields("{server:9}", "Pablo", {"Pablo": "1"}), "no field 9: not judged yet")
            self.assertEqual(ql.with_server_fields("a.b", "Pablo", {"Pablo": "1"}), "a.b", "no placeholder: unchanged")

    def test_rejoin_names_its_client(self) -> None:
        with mock.patch.object(ql, "player2", return_value=False):
            self.assertEqual(ql.rejoin_target("2:paused"), (True, "paused"))
            self.assertEqual(ql.rejoin_target("2"), (True, ""))
            self.assertEqual(ql.rejoin_target("1:warm"), (False, "warm"))
            self.assertEqual(ql.rejoin_target("paused"), (False, "paused"))
        with mock.patch.object(ql, "player2", return_value=True):
            self.assertEqual(ql.rejoin_target(""), (True, ""))  # scenarios.py --client 2
        with mock.patch.object(ql, "player2", return_value=False):
            self.assertEqual(ql.rejoin_player("3:paused"), ("3", "paused"))  # player 3
            self.assertEqual(ql.rejoin_target("3"), (False, ""))

    def test_notices_reply(self) -> None:
        lines = ["seq=7;lost=0;count=2", "6|1200|You claimed Sim 1", "7|1900|Can't do \"Sit\": someone | else"]
        self.assertEqual(ql.parse_notices(lines), (7, [(6, 1200, "You claimed Sim 1"), (7, 1900, "Can't do \"Sit\": someone | else")]))
        self.assertEqual(ql.parse_notices(["ERROR: unknown command NOTICES"]), (-1, []))

    def notices(self, shown: list[str]):
        return mock.patch.object(ql, "client_notices", return_value=(len(shown), [(i, 0, t) for i, t in enumerate(shown)]))

    def test_notice_waits_for_a_match(self) -> None:
        with self.notices(["You claimed Sim 1", "Can't do \"Sit\" right now."]) as read, \
                mock.patch.object(ql, "notice_mark", return_value=4), mock.patch.object(ql, "say"):
            ql.step_notice("2:5:Can't do .Sit", expected=True)
        read.assert_called_with("2", 4)  # only what came after the scenario's mark

    def test_notice_times_out_with_what_was_shown(self) -> None:
        with self.notices(["You claimed Sim 1"]), mock.patch.object(ql, "notice_mark", return_value=0), \
                mock.patch.object(ql.time, "sleep"), mock.patch.object(ql.time, "monotonic", side_effect=[0, 1, 99]):
            with self.assertRaises(SystemExit) as stop:
                ql.step_notice("1:5:Can't do", expected=True)
        self.assertIn("shown: You claimed Sim 1", str(stop.exception))

    def test_nonotice_fails_on_a_match_and_passes_without(self) -> None:
        with self.notices(["Can't do \"Sit\" right now."]), mock.patch.object(ql, "notice_mark", return_value=0):
            with self.assertRaises(SystemExit):
                ql.step_notice("1:Can't do", expected=False)
        with self.notices([]), mock.patch.object(ql, "notice_mark", return_value=0), mock.patch.object(ql, "say") as say:
            ql.step_notice("1:.", expected=False)
        self.assertIn("none", say.call_args[0][0])

    def test_mark_records_each_answering_client(self) -> None:
        with tempfile.TemporaryDirectory() as folder, mock.patch.object(ql, "RUN", Path(folder)), mock.patch.object(ql, "say"), \
                mock.patch.object(ql, "client_notices", side_effect=lambda c, s: (12, []) if c == "1" else (-1, [])):
            ql.step_notices_mark()
            self.assertEqual(json.loads((Path(folder) / ql.NOTICE_MARKS).read_text(encoding="utf-8")), {"1": 12})
            self.assertEqual(ql.notice_mark("1"), 12)
            self.assertEqual(ql.notice_mark("2"), 0)

    def test_cpick_names_the_client_and_fails_on_a_probe_error(self) -> None:
        import client_pick
        with mock.patch.object(client_pick, "reply", return_value=(0, "client 2: ok=true; picked=Sit")) as reply, \
                mock.patch.object(ql, "say"):
            ql.run_steps(["cpick:2:id:115 Sit --name \"Sit Down\""], {})
        self.assertEqual(reply.call_args[0][0], ["--client", "2", "id:115", "Sit", "--name", "Sit Down"])
        with mock.patch.object(client_pick, "reply", return_value=(1, "client 2: err=target not found")), mock.patch.object(ql, "say"):
            with self.assertRaises(SystemExit):
                ql.run_steps(["cpick:2:Chair Sit"], {})

    def test_hud_polls_until_its_queue_matches(self) -> None:
        import client_pick
        replies = iter([(0, "client 1: ok=true; actor=Jennifer; hudQueue=0; items=-"),
                        (0, "client 1: ok=true; actor=Jennifer; hudQueue=1; items=0: Sit #9")])
        with mock.patch.object(client_pick, "reply", side_effect=lambda argv: next(replies)), mock.patch.object(ql, "say"), \
                mock.patch.object(ql.time, "sleep"):
            ql.run_steps(["hud:1:10:hudQueue=1;? items=0: Sit"], {})


class BorrowedIdlePremise(unittest.TestCase):
    def test_premise_only_invalidates_a_borrowed_driver(self):
        import idle_repair_probe
        with mock.patch.object(idle_repair_probe, "main", return_value=0) as run:
            ql.run_steps(["premise:2:borrowed-idle:Pablo"], {})
        self.assertEqual(run.call_args.args[0], ["Pablo", "--client", "127.0.0.1:34031", "--via-replay",
                                                "--no-request", "--invalidate-only"])

    def test_failed_premise_stops_the_steps(self):
        import idle_repair_probe
        with mock.patch.object(idle_repair_probe, "main", return_value=2), mock.patch.object(ql.time, "sleep") as sleep:
            with self.assertRaises(SystemExit):
                ql.run_steps(["premise:1:borrowed-idle:Jennifer", "sleep:4"], {})
        sleep.assert_not_called()

    def test_invalid_premise_never_reaches_a_game(self):
        for rest in ("server:borrowed-idle:Jennifer", "1:borrowed-idle:", "1:unknown:Jennifer"):
            with self.assertRaises(SystemExit):
                ql.run_steps(["premise:" + rest], {})


class ForkGuard(unittest.TestCase):
    """run --boot refuses a boot with no gameplay fork installed (2026-11-01: two gate runs booted without it, hook=absent)."""

    def guard(self, attempt, allow=False, env=None):
        err = io.StringIO()
        env = {"SIMSONLINE_NO_FORK_OK": "", **(env or {})}
        with mock.patch.object(ql, "physical_fork_census", return_value={
                "state": "fork" if attempt else "stock", "selectedAttempt": attempt}), \
                mock.patch.object(ql.fork_census, "write_evidence"), \
                mock.patch.dict(os.environ, env), contextlib.redirect_stderr(err):
            try:
                ql.fork_guard(allow)
            except SystemExit as stop:
                return stop.code, err.getvalue()
        return None, err.getvalue()

    def test_no_fork_refuses_and_names_the_fix(self):
        code, err = self.guard(None)
        self.assertEqual(code, 2)
        self.assertIn("FORK-MISSING", err)
        self.assertIn("fork-install", err)
        self.assertIn("fork-cycle", err)
        self.assertIn("--no-fork-ok", err)

    def test_flag_and_env_allow_with_a_warning(self):
        for allow, env in ((True, None), (False, {"SIMSONLINE_NO_FORK_OK": "1"})):
            code, err = self.guard(None, allow, env)
            self.assertIsNone(code)
            self.assertIn("FORK-MISSING", err)

    def test_installed_fork_is_silent(self):
        self.assertEqual(self.guard("cold-test-118"), (None, ""))

    def test_fork_line_says_the_attempt_or_none(self):
        with mock.patch.object(ql, "physical_fork_census", return_value={
                "state": "fork", "selectedAttempt": "cold-test-118"}):
            self.assertEqual(ql.fork_line(), "fork: cold-test-118 installed (physical packages verified)")
        with mock.patch.object(ql, "physical_fork_census", return_value={"state": "stock"}):
            self.assertTrue(ql.fork_line().startswith("fork: none installed"))


class ForkReceiptUnreadable(unittest.TestCase):
    """A corrupt receipt is a FORK-MISSING refusal naming it, not a traceback."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        receipt = Path(self.tmp.name) / ".omo/cache/fork/cold-test-001/deployment.json"
        receipt.parent.mkdir(parents=True)
        receipt.write_bytes(bytes([0xFF, 0xFE, 0x00, 0x80]) + b" not utf-8")
        patch = mock.patch.object(ql, "MAIN", Path(self.tmp.name))
        patch.start()
        self.addCleanup(patch.stop)
        patch = mock.patch.object(ql.fork_census, "documents_profile", return_value=Path(self.tmp.name) / "profile")
        patch.start()
        self.addCleanup(patch.stop)
        patch = mock.patch.object(ql, "mod_targets", return_value=[])
        patch.start()
        self.addCleanup(patch.stop)
        patch = mock.patch.object(ql, "BOXES", [])
        patch.start()
        self.addCleanup(patch.stop)
        patch = mock.patch.object(ql, "RUN", Path(self.tmp.name) / "run")
        patch.start()
        self.addCleanup(patch.stop)

    def run_guard(self, allow):
        err = io.StringIO()
        with mock.patch.dict(os.environ, {"SIMSONLINE_NO_FORK_OK": ""}), contextlib.redirect_stderr(err):
            try:
                ql.fork_guard(allow)
            except SystemExit as stop:
                return stop.code, err.getvalue()
        return None, err.getvalue()

    def test_guard_refuses_naming_the_receipt_and_error(self):
        code, err = self.run_guard(False)
        self.assertEqual(code, 2)
        self.assertIn("FORK-UNSAFE", err)
        self.assertIn("cold-test-001", err)
        self.assertIn("UnicodeDecodeError", err)

    def test_flag_cannot_override_unreadable_state(self):
        code, err = self.run_guard(True)
        self.assertEqual(code, 2)
        self.assertIn("FORK-UNSAFE", err)

    def test_fork_line_reports_unreadable(self):
        self.assertTrue(ql.fork_line().startswith("fork: state unreadable"))

    def test_installed_or_none_names_the_receipt_and_returns_none(self):
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            self.assertIsNone(ql.installed_attempt_or_none("fork-cycle"))
        self.assertIn("fork-cycle", err.getvalue())
        self.assertIn("cold-test-001", err.getvalue())

    def test_fork_cycle_skips_restore_and_installs(self):
        err = io.StringIO()
        with mock.patch.object(ql, "run", return_value="out: x\n"), mock.patch.object(ql, "fork_restore") as restore, \
                mock.patch.object(ql, "fork_install") as install, mock.patch.object(ql, "say"), \
                contextlib.redirect_stderr(err):
            ql.fork_cycle(None)
        restore.assert_not_called()
        install.assert_called_once_with("cold-test-002")
        self.assertIn("cold-test-001", err.getvalue())

if __name__ == "__main__":
    unittest.main()


class MoveAside(unittest.TestCase):
    def test_a_move_across_drives_copies_then_deletes(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            source, target = Path(tmp) / "a.package", Path(tmp) / "b.package"
            source.write_text("fork", encoding="utf-8")
            target.write_text("old", encoding="utf-8")
            cross = OSError(18, "Invalid cross-device link")
            with mock.patch.object(Path, "replace", side_effect=cross):
                ql.move_aside(source, target)
            self.assertFalse(source.exists())
            self.assertEqual(target.read_text(encoding="utf-8"), "fork")

    def test_another_error_is_raised(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            source = Path(tmp) / "a.package"
            source.write_text("fork", encoding="utf-8")
            with mock.patch.object(Path, "replace", side_effect=PermissionError(13, "denied")):
                with self.assertRaises(PermissionError):
                    ql.move_aside(source, Path(tmp) / "b.package")
