"""Read-only cleanup probes never terminate a box or a process they did not start."""
import contextlib
import importlib.util
import io
from pathlib import Path
import subprocess
import threading
import unittest
from unittest import mock

SPEC = importlib.util.spec_from_file_location(
    "sandboxie_cleanup_diag", Path(__file__).with_name("sandboxie_cleanup_diag.py"))
diag = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(diag)


class CleanupDiagnostics(unittest.TestCase):
    @contextlib.contextmanager
    def windows(self):
        kernel, advapi, user = mock.Mock(), mock.Mock(), mock.Mock()
        kernel.CreateToolhelp32Snapshot.return_value = 1
        kernel.Thread32First.return_value = False
        kernel.Thread32Next.return_value = False
        advapi.OpenThreadWaitChainSession.return_value = 1
        with (
            mock.patch.object(diag.sys, "platform", "win32"),
            mock.patch.object(diag.ctypes, "WinDLL", create=True,
                              side_effect=lambda name, **_: {
                                  "kernel32": kernel, "advapi32": advapi, "user32": user}[name]),
            mock.patch.object(diag.ctypes, "WINFUNCTYPE", diag.ctypes.CFUNCTYPE, create=True),
        ):
            yield kernel, advapi, user

    @contextlib.contextmanager
    def launcher(self, stdout="0\n", stderr="", returncode=0):
        with mock.patch.object(diag.subprocess, "Popen") as popen:
            process = popen.return_value
            process.pid, process.returncode = 103, returncode
            process.poll.return_value = returncode

            def launch(command, **kwargs):
                kwargs["stdout"].write(stdout.encode("utf-8"))
                kwargs["stderr"].write(stderr.encode("utf-8"))
                return process

            popen.side_effect = launch
            yield popen, process

    def test_normal_probe_captures_output_and_uses_no_input_or_termination(self):
        with self.launcher() as (popen, process), mock.patch.object(diag.threading, "Timer"):
            data = diag.probe("S:/Start.exe", "Client3Box", 15, 5)
            self.assertEqual(popen.call_args.args[0], ["S:/Start.exe", "/box:Client3Box", "/listpids"])
            self.assertEqual(popen.call_args.kwargs["stdin"], subprocess.DEVNULL)
            self.assertEqual(data["stdout"], "0\n")
            self.assertFalse(data["timed_out"])
            process.kill.assert_not_called()
            process.wait.assert_called_once_with(timeout=15)
            process.__exit__.assert_not_called()
            process.communicate.assert_not_called()

    def test_timeout_captures_wait_chain_and_kills_only_the_owned_launcher(self):
        with (
            self.launcher(stderr="warning", returncode=-1) as (_, process),
            mock.patch.object(diag.threading, "Timer") as timer,
            mock.patch.object(diag, "wait_chain", return_value={"pid": 103, "threads": []}) as inspect,
        ):
            process.poll.return_value = None
            process.wait.side_effect = [subprocess.TimeoutExpired("Start.exe", 15), -1]
            inspector = mock.Mock()
            inspector.is_alive.return_value = False

            def start_timer(_, capture):
                inspector.start.side_effect = capture
                return inspector

            timer.side_effect = start_timer
            data = diag.probe("S:/Start.exe", "Client3Box", 15, 5)
            inspect.assert_called_once_with(103, cancelled=mock.ANY)
            process.kill.assert_called_once_with()
            self.assertEqual(process.wait.call_args_list, [
                mock.call(timeout=15), mock.call(timeout=diag.EXIT_TIMEOUT)])
            inspector.join.assert_called_once_with(timeout=diag.INSPECTOR_JOIN_TIMEOUT)
            self.assertTrue(inspector.daemon)
            process.__exit__.assert_not_called()
            process.communicate.assert_not_called()
            self.assertEqual(data["wait_chain"]["pid"], 103)
            self.assertEqual(data["stderr"], "warning")
            self.assertTrue(data["timed_out"])

    def test_wait_chain_requires_windows(self):
        with mock.patch.object(diag.sys, "platform", "linux"):
            with self.assertRaisesRegex(OSError, "requires Windows"):
                diag.wait_chain(103)

    def test_unheld_claim_refuses_before_starting_a_launcher(self):
        for holder in (None, ("foreign", None, "another session")):
            claim = mock.Mock()
            claim.current.return_value = holder
            claim.own_session.return_value = "ours"
            with (
                self.subTest(holder=holder),
                mock.patch.dict(diag.sys.modules, {"stack_claim": claim}),
                mock.patch.object(diag.subprocess, "Popen") as popen,
                mock.patch.object(diag.Path, "mkdir") as mkdir,
                contextlib.redirect_stderr(io.StringIO()),
                self.assertRaises(SystemExit) as error,
            ):
                diag.main(["--box", "Client3Box", "--out", "unused"])
            self.assertEqual(error.exception.code, 2)
            popen.assert_not_called()
            mkdir.assert_not_called()

    def test_kill_exit_wait_is_bounded_and_preserves_partial_output(self):
        with (
            self.launcher(stderr="warning", returncode=None) as (_, process),
            mock.patch.object(diag.threading, "Timer") as timer,
        ):
            process.wait.side_effect = subprocess.TimeoutExpired("Start.exe", 15)
            timer.return_value.is_alive.return_value = False
            data = diag.probe("S:/Start.exe", "Client3Box", 15, 5)
            self.assertEqual(process.wait.call_args_list, [
                mock.call(timeout=15), mock.call(timeout=diag.EXIT_TIMEOUT)])
            process.kill.assert_called_once_with()
            process.__exit__.assert_not_called()
            self.assertTrue(data["exit_incomplete"])
            self.assertTrue(data["timed_out"])
            self.assertEqual((data["stdout"], data["stderr"]), ("0\n", "warning"))
            timer.return_value.join.assert_called_once_with(timeout=diag.INSPECTOR_JOIN_TIMEOUT)

    def test_infinite_deadline_is_rejected_before_claim_or_launch(self):
        claim = mock.Mock()
        with (
            mock.patch.dict(diag.sys.modules, {"stack_claim": claim}),
            mock.patch.object(diag.subprocess, "Popen") as popen,
            contextlib.redirect_stderr(io.StringIO()),
            self.assertRaises(SystemExit) as error,
        ):
            diag.main(["--box", "Client3Box", "--out", "unused", "--timeout", "inf"])
        self.assertEqual(error.exception.code, 2)
        claim.current.assert_not_called()
        popen.assert_not_called()

    def test_claim_handoff_refuses_the_next_batch_probe(self):
        claim = mock.Mock()
        claim.current.side_effect = [("ours",), ("ours",), ("foreign",)]
        claim.own_session.return_value = "ours"
        with (
            mock.patch.dict(diag.sys.modules, {"stack_claim": claim}),
            mock.patch.object(diag, "probe", return_value={
                "returncode": 0, "timed_out": False, "stdout": "0\n", "stderr": ""}) as probe,
            mock.patch.object(diag.Path, "mkdir"),
            mock.patch.object(diag.Path, "write_text") as write,
            contextlib.redirect_stderr(io.StringIO()),
            contextlib.redirect_stdout(io.StringIO()),
            self.assertRaises(SystemExit) as error,
        ):
            diag.main(["--box", "Client3Box", "--out", "unused", "--count", "2"])
        self.assertEqual(error.exception.code, 2)
        probe.assert_called_once()
        write.assert_called_once()

    def test_running_inspector_cannot_delay_raw_output_or_cli_exit(self):
        entered, release, returned = threading.Event(), threading.Event(), threading.Event()
        inspectors, outcome = [], {}
        process = mock.MagicMock(pid=103, returncode=-1)
        process.__enter__.return_value = process
        process.poll.return_value = None

        def launch(command, **kwargs):
            if kwargs["stdout"] != subprocess.PIPE:
                kwargs["stdout"].write(b"0\n")
                kwargs["stderr"].write(b"warning")
            return process

        def inspect(pid, **kwargs):
            inspectors.append(threading.current_thread())
            entered.set()
            release.wait(timeout=10)
            return {"pid": pid, "threads": []}

        def communicate(timeout):
            if not process.kill.called:
                if not entered.wait(timeout=2):
                    raise AssertionError("The real Timer never entered inspection")
                raise subprocess.TimeoutExpired("Start.exe", timeout)
            return "0\n", "warning"

        def wait(timeout):
            communicate(timeout)
            return -1

        process.communicate.side_effect = communicate
        process.wait.side_effect = wait

        def run():
            try:
                outcome["data"] = diag.probe("S:/Start.exe", "Client3Box", 0.01, 0.001)
            except BaseException as exc:
                outcome["error"] = exc
            finally:
                returned.set()

        with (
            mock.patch.object(diag.subprocess, "Popen", side_effect=launch),
            mock.patch.object(diag, "wait_chain", side_effect=inspect),
        ):
            worker = threading.Thread(target=run)
            worker.start()
            try:
                self.assertTrue(entered.wait(timeout=2))
                self.assertTrue(returned.wait(timeout=3), "probe blocked on the running inspector")
                self.assertNotIn("error", outcome)
                data = outcome["data"]
                self.assertEqual(data["stdout"], "0\n")
                self.assertEqual(data["stderr"], "warning")
                self.assertTrue(data["timed_out"])
                self.assertTrue(data["inspection_incomplete"])
                self.assertTrue(inspectors[0].daemon, "The inspector would keep the CLI alive")
                process.kill.assert_called_once_with()
                process.__exit__.assert_not_called()
                snapshot = dict(data)
                release.set()
                inspectors[0].join(timeout=2)
                self.assertFalse(inspectors[0].is_alive())
                self.assertEqual(data, snapshot, "A late inspection mutated returned evidence")
            finally:
                release.set()
                worker.join(timeout=2)
                for inspector in inspectors:
                    inspector.join(timeout=2)
            self.assertFalse(worker.is_alive())

    def test_foreign_windows_and_embedded_children_are_not_read(self):
        with self.windows() as (_, _, user):
            owners = {1: 103, 2: 999, 3: 103, 4: 777, 5: 999}
            titles = {1: "owned launcher", 2: "Sandboxie foreign session",
                      3: "owned child", 4: "foreign embedded child", 5: "foreign child"}

            def owner(hwnd, target):
                target._obj.value = owners[hwnd]
                return 1

            def text(hwnd, buffer, size):
                buffer.value = titles[hwnd]
                return len(buffer.value)

            user.GetWindowThreadProcessId.side_effect = owner
            user.GetWindowTextW.side_effect = text
            user.EnumWindows.side_effect = lambda callback, _: (callback(1, 0), callback(2, 0))
            user.EnumChildWindows.side_effect = lambda hwnd, callback, _: [
                callback(child, 0) for child in ((3, 4) if hwnd == 1 else (5,))]
            data = diag.wait_chain(103)
            self.assertEqual([window["pid"] for window in data["windows"]], [103])
            self.assertEqual(data["windows"][0]["children"], ["owned child"])
            self.assertEqual([call.args[0] for call in user.GetWindowTextW.call_args_list], [1, 3])
            self.assertEqual([call.args[0] for call in user.EnumChildWindows.call_args_list], [1])

    def test_wait_chain_does_not_retain_foreign_thread_nodes(self):
        with self.windows() as (kernel, advapi, _):
            def first(snapshot, entry):
                entry._obj.pid, entry._obj.tid = 103, 104
                return True

            def chain(session, context, flags, tid, count, nodes, cycle):
                count._obj.value = 3
                nodes[0].type = nodes[1].type = 8
                nodes[0].info.thread.pid, nodes[0].info.thread.tid = 103, 104
                nodes[1].info.thread.pid, nodes[1].info.thread.tid = 999, 1000
                nodes[2].type, nodes[2].info.lock.name = 3, "foreign lock"
                return True

            kernel.Thread32First.side_effect = first
            advapi.GetThreadWaitChain.side_effect = chain
            data = diag.wait_chain(103)
            self.assertEqual(data["threads"][0]["chain"], [
                {"type": 8, "status": 0, "pid": 103, "tid": 104}])
            self.assertEqual(advapi.GetThreadWaitChain.call_args.args[2], 0)


if __name__ == "__main__":
    unittest.main()
