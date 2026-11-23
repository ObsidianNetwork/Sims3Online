"""teardown is verified before a client can rejoin. No real processes."""

from __future__ import annotations

import contextlib
import io
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import stackctl  # noqa: E402

PID_ALIVE = stackctl._pid_alive


class Cleanup(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parents[1] / ".omo")
        self.addCleanup(self.tmp.cleanup)
        self.pid = Path(self.tmp.name) / "client2-ts3.pid"
        self.pid.write_text("103", encoding="utf-8")
        self.args = SimpleNamespace(
            sandboxie_box2="Client3Box",
            sandboxie_start="S:/Start.exe",
            json_mode=False,
            client_wineprefix=None,
            require_client=False,
        )
        self.plat = SimpleNamespace(is_linux=False)
        self.box = [103, 104]  # game and a detached sandbox descendant
        self.calls = []
        self.refuse = False
        self.patches = [
            mock.patch.object(
                stackctl,
                "client_files",
                return_value={"role": "client2", "pid": str(self.pid), "state": str(self.pid) + ".state"},
            ),
            mock.patch.object(stackctl, "get_controller_pids_by_role", return_value=[]),
            mock.patch.object(stackctl, "_pid_alive", side_effect=lambda pid: pid in self.box),
            mock.patch.object(stackctl.subprocess, "run", side_effect=self.fake_run),
            mock.patch.object(stackctl.sys, "platform", "win32"),
            mock.patch.object(stackctl.time, "monotonic", side_effect=range(0, 1000, 20)),
            mock.patch.object(stackctl.time, "sleep", side_effect=AssertionError("no test sleeps")),
        ]
        for patch in self.patches:
            patch.start()
            self.addCleanup(patch.stop)

    def fake_run(self, cmd, **kwargs):
        self.calls.append(cmd)
        if cmd[-1] == "/listpids":
            return SimpleNamespace(returncode=0, stdout="\n".join(map(str, [len(self.box), *self.box])), stderr="")
        if cmd[-1] == "/terminate":
            if not self.refuse:
                self.box.clear()
            return SimpleNamespace(returncode=int(self.refuse), stdout="", stderr="access denied")
        if cmd[0] == "taskkill":
            if not self.refuse:
                self.box[:] = [p for p in self.box if str(p) != cmd[cmd.index("/PID") + 1]]
            return SimpleNamespace(returncode=int(self.refuse), stdout="", stderr="access denied")
        raise AssertionError(f"unexpected real command: {cmd}")

    def test_client2_down_terminates_and_verifies_only_its_box(self):
        with contextlib.redirect_stdout(io.StringIO()):
            result = stackctl.cmd_client_down(self.args, self.plat, 2)
        self.assertEqual(result, 0)
        self.assertEqual(self.box, [])
        self.assertFalse(self.pid.exists())
        self.assertIn(["S:/Start.exe", "/box:Client3Box", "/terminate"], self.calls)
        self.assertEqual(self.calls[-1], ["S:/Start.exe", "/box:Client3Box", "/listpids"])

    def test_refused_cleanup_fails_and_preserves_pid(self):
        self.refuse = True
        with contextlib.redirect_stdout(io.StringIO()):
            result = stackctl.cmd_client_down(self.args, self.plat, 2)
        self.assertEqual(result, 1)
        self.assertTrue(self.pid.exists())
        self.assertEqual(self.box, [103, 104])

    def test_surviving_descendant_fails_even_when_terminate_reports_success(self):
        self.refuse = True
        real_run = self.fake_run

        def lying_terminate(cmd, **kwargs):
            result = real_run(cmd, **kwargs)
            if cmd[-1] == "/terminate":
                result.returncode = 0
            return result

        with mock.patch.object(stackctl.subprocess, "run", side_effect=lying_terminate):
            self.assertFalse(stackctl.stop_client_sandbox(self.args, self.plat, 2))
        self.assertTrue(self.pid.exists())

    def test_bad_enumeration_fails_closed_without_termination(self):
        for stdout in ("", "2\n103", "garbage", "0\n103"):
            with (
                self.subTest(stdout=stdout),
                mock.patch.object(
                    stackctl.subprocess, "run", return_value=SimpleNamespace(returncode=0, stdout=stdout, stderr="")
                ) as run,
            ):
                self.assertFalse(stackctl.stop_client_sandbox(self.args, self.plat, 2))
                self.assertEqual(run.call_count, 1)

    def test_timed_out_enumeration_preserves_partial_output_without_termination(self):
        for stdout, stderr in ((b"0\r\n", b"diagnostic warning"), ("0\r\n", "diagnostic warning")):
            with (
                self.subTest(stdout=stdout),
                mock.patch.object(
                    stackctl.subprocess,
                    "run",
                    side_effect=stackctl.subprocess.TimeoutExpired(
                        ["S:/Start.exe", "/box:Client3Box", "/listpids"],
                        15,
                        output=stdout,
                        stderr=stderr,
                    ),
                ) as run,
                contextlib.redirect_stdout(io.StringIO()) as output,
            ):
                self.assertFalse(stackctl.stop_client_sandbox(self.args, self.plat, 2))
                self.assertIn("stdout='0\\r\\n'", output.getvalue())
                self.assertIn("stderr='diagnostic warning'", output.getvalue())
                self.assertEqual(run.call_count, 1)

    def test_native_game_kill_uses_tree_and_propagates_failure(self):
        self.args.sandboxie_box2 = ""
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(stackctl.cmd_client_down(self.args, self.plat, 2), 0)
        self.assertIn(["taskkill", "/F", "/PID", "103", "/T"], self.calls)
        self.pid.write_text("103", encoding="utf-8")
        self.box.append(103)
        self.refuse = True
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(stackctl.cmd_client_down(self.args, self.plat, 2), 1)
        self.assertTrue(self.pid.exists())

    def test_restart_does_not_launch_after_cleanup_failure(self):
        with (
            mock.patch.object(stackctl, "_stop_client1", return_value=False),
            mock.patch.object(stackctl, "ensure_client_wineprefix"),
            mock.patch.object(stackctl, "launch_client") as launch,
        ):
            self.assertEqual(stackctl.cmd_launch("launch-client-restart", self.args, self.plat), 1)
            launch.assert_not_called()

    def launch_context(self, launch):
        files = {key: str(Path(self.tmp.name) / key) for key in ("state", "log", "snapshot", "bridge_log")}
        files.update(role="client2", pid=str(self.pid), bridgectl="127.0.0.1:34031")
        context = contextlib.ExitStack()
        for patch in (
            mock.patch.object(stackctl, "Platform", return_value=self.plat),
            mock.patch.object(stackctl, "client_files", return_value=files),
            mock.patch.object(stackctl, "client_wineprefix", return_value=None),
            mock.patch.object(stackctl, "stop_zombie_client_controller"),
            mock.patch.object(stackctl, "test_listener", return_value=False),
            mock.patch.object(stackctl, "warn_client_env"),
            mock.patch.object(stackctl, "save_run_logs"),
            mock.patch.object(stackctl, "_build_client_args", return_value=["controller"]),
            mock.patch.object(stackctl, "launch_controller", side_effect=launch),
            mock.patch.dict(stackctl._launch_procs, clear=True),
        ):
            context.enter_context(patch)
        return context

    def test_spawn_cleans_untracked_box_before_launching_controller(self):
        self.pid.unlink()
        identity = Path(str(self.pid) + ".identity.json")
        identity.write_text("old identity", encoding="utf-8")
        self.box[:] = [104]
        launched = []

        def launch(*args):
            self.assertEqual(self.box, [], "An untracked sandbox process must be gone before spawning.")
            self.assertFalse(identity.exists(), "A new launch must discard the old process identity.")
            launched.append(args)

        with self.launch_context(launch), mock.patch.dict(stackctl.os.environ, {}, clear=True):
            stackctl.launch_client(self.args, player_id=2, skip_orphan_kill=True)
        self.assertEqual(len(launched), 1)
        self.assertIn(["S:/Start.exe", "/box:Client3Box", "/terminate"], self.calls)

    def test_spawn_cleanup_failure_preserves_ownership_and_refuses_launch(self):
        self.refuse = True
        launch = mock.Mock()
        with self.launch_context(launch), mock.patch.dict(stackctl.os.environ, {}, clear=True):
            with self.assertRaises(SystemExit) as error:
                stackctl.launch_client(self.args, player_id=2, skip_orphan_kill=True)
        self.assertEqual(error.exception.code, 1)
        launch.assert_not_called()
        self.assertTrue(self.pid.exists())

    def test_warm_attach_does_not_terminate_its_existing_game(self):
        launch = mock.Mock()
        identity = Path(str(self.pid) + ".identity.json")
        identity.write_text("original identity", encoding="utf-8")
        with (
            self.launch_context(launch),
            mock.patch.dict(stackctl.os.environ, {"SIMSONLINE_CLIENT_ATTACH_PID": "103"}, clear=True),
        ):
            stackctl.launch_client(self.args, player_id=2, skip_orphan_kill=True)
        launch.assert_called_once()
        self.assertEqual(self.box, [103, 104])
        self.assertEqual(identity.read_text(encoding="utf-8"), "original identity")
        self.assertNotIn(["S:/Start.exe", "/box:Client3Box", "/terminate"], self.calls)

    def test_boot_propagates_launch_failure_before_readiness(self):
        self.args.skip_mod_sync = True
        with (
            mock.patch.object(stackctl, "ensure_mod_synced", return_value=True),
            mock.patch.object(stackctl, "cmd_launch", return_value=1),
            mock.patch.object(stackctl, "get_readiness_snapshot") as readiness,
        ):
            self.assertEqual(stackctl.cmd_boot("boot-client-restart", self.args, self.plat), 1)
            readiness.assert_not_called()

    def test_process_absence_requires_successful_exact_pid_query(self):
        with mock.patch.object(stackctl.subprocess, "run") as run:
            run.return_value = SimpleNamespace(returncode=0, stdout='"TS3W.exe","1103","Console"\n', stderr="")
            self.assertFalse(PID_ALIVE(103))
            run.return_value.stdout = '"TS3W.exe","103","Console"\n'
            self.assertTrue(PID_ALIVE(103))
            run.return_value.returncode = 1
            run.return_value.stderr = "access denied"
            with self.assertRaises(OSError):
                PID_ALIVE(103)

    def test_failed_absence_query_cannot_pass_cleanup(self):
        with mock.patch.object(stackctl, "_pid_alive", side_effect=OSError("query failed")):
            self.assertFalse(stackctl.wait_processes_gone([103]))


class ControllerLaunch(unittest.TestCase):
    def launch(self, platform):
        with (
            mock.patch.object(stackctl.os, "name", platform),
            mock.patch.object(stackctl.os, "makedirs"),
            mock.patch.object(stackctl, "Path"),
            mock.patch.object(stackctl, "launch_fork_census", return_value={"state": "fork"}),
            mock.patch.object(stackctl.fork_census, "write_evidence"),
            mock.patch("builtins.open", mock.mock_open()),
            mock.patch.object(stackctl, "new_role_env", return_value={"SIMSONLINE_ROLE": "client2"}),
            mock.patch.object(stackctl.subprocess, "CREATE_NEW_CONSOLE", 16, create=True),
            mock.patch.object(stackctl.subprocess, "STARTF_USESHOWWINDOW", 1, create=True),
            mock.patch.object(stackctl.subprocess, "SW_HIDE", 0, create=True),
            mock.patch.object(
                stackctl.subprocess,
                "STARTUPINFO",
                side_effect=lambda: SimpleNamespace(dwFlags=0, wShowWindow=None),
                create=True,
            ),
            mock.patch.object(stackctl.subprocess, "Popen") as process,
        ):
            launched = stackctl.launch_controller(
                ["controller"], "client2", "run/client2.log", "repo", {"EXPLICIT": "value"}
            )
        self.assertIs(launched, process.return_value)
        self.assertEqual(process.call_args.kwargs["env"]["SIMSONLINE_ROLE"], "client2")
        self.assertEqual(process.call_args.kwargs["env"]["EXPLICIT"], "value")
        return process.call_args.kwargs

    def test_windows_controller_has_its_own_hidden_console(self):
        kwargs = self.launch("nt")
        self.assertEqual(kwargs.get("creationflags", 0) & 16, 16)
        self.assertEqual(kwargs["startupinfo"].wShowWindow, 0)
        self.assertEqual(kwargs["startupinfo"].dwFlags & 1, 1)
        self.assertFalse(kwargs.get("start_new_session", False))

    def test_posix_controller_keeps_its_independent_session(self):
        kwargs = self.launch("posix")
        self.assertTrue(kwargs.get("start_new_session", False))
        self.assertEqual(kwargs.get("creationflags", 0), 0)


if __name__ == "__main__":
    unittest.main()
