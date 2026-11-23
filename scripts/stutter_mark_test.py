import json
import os
import tempfile
import threading
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch

from scripts import stutter_mark as sm


class StutterMarkTests(unittest.TestCase):
    def test_save_retries_replace_without_exposing_partial_json(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "status.json"
            sm.save(path, {"samples": 1})
            replace = Path.replace
            seen = []

            def denied_once(source, target):
                seen.append(source)
                self.assertEqual(sm.read(target), {"samples": 1})
                if len(seen) == 1:
                    raise PermissionError("injected sharing violation")
                return replace(source, target)

            with patch.object(Path, "replace", denied_once), patch.object(sm.time, "sleep") as sleep:
                sm.save(path, {"samples": 2})
            self.assertEqual(sm.read(path), {"samples": 2})
            self.assertEqual(len(seen), 2)
            sleep.assert_called_once_with(.02)
            self.assertEqual(list(Path(temp).glob("*.tmp")), [])

    def test_save_permission_retry_is_bounded_and_preserves_old_status(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "status.json"
            sm.save(path, {"samples": 1})
            with patch.object(Path, "replace", side_effect=PermissionError("locked")) as replace, \
                    patch.object(sm.time, "sleep") as sleep:
                with self.assertRaises(PermissionError):
                    sm.save(path, {"samples": 2})
            self.assertEqual(replace.call_count, 6)
            self.assertEqual(sleep.call_count, 5)
            self.assertEqual(sm.read(path), {"samples": 1})
            self.assertEqual(list(Path(temp).glob("*.tmp")), [])

    def test_save_uses_separate_temporary_files_for_concurrent_writers(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "status.json"
            replace = Path.replace
            seen = []

            def interleaved(source, target):
                seen.append(source)
                if len(seen) == 1:
                    sm.save(path, {"writer": "inner"})
                return replace(source, target)

            with patch.object(Path, "replace", interleaved):
                sm.save(path, {"writer": "outer"})
            self.assertEqual(len(set(seen)), 2)
            self.assertEqual(sm.read(path), {"writer": "outer"})

    def test_read_retries_a_concurrent_replace(self):
        with patch.object(Path, "read_text", side_effect=[PermissionError("replacing"), '{"samples": 2}']), \
                patch.object(sm.time, "sleep") as sleep:
            self.assertEqual(sm.read(Path("status.json")), {"samples": 2})
        sleep.assert_called_once_with(.02)

    def test_read_permission_retry_is_bounded(self):
        with patch.object(Path, "read_text", side_effect=PermissionError("locked")) as read_text, \
                patch.object(sm.time, "sleep") as sleep:
            with self.assertRaises(PermissionError):
                sm.read(Path("status.json"))
        self.assertEqual(read_text.call_count, 6)
        self.assertEqual(sleep.call_count, 5)

    def test_stale_heartbeat_is_active_when_process_identity_matches(self):
        state = {"running": True, "t": 0, "session": "abc", "pid": 42, "started": 123}
        with patch.object(sm, "read", return_value=state), patch.object(sm, "RecorderProcess") as factory:
            process = factory.return_value.__enter__.return_value
            process.started, process.wait.return_value = 123, False
            self.assertEqual(sm.active(Path("root")), state)
            process.wait.assert_called_once_with(0)
            process.started = 124
            self.assertEqual(sm.active(Path("root")), {})

    def test_stop_terminates_hung_recorder_after_bounded_grace_period(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            sm.save(root / "status.json", {"running": True, "ready": False, "t": 0,
                                          "session": "abc", "pid": 42, "started": 123})
            with patch.object(sm, "RecorderProcess") as factory:
                process = factory.return_value.__enter__.return_value
                process.started, process.wait.return_value = 123, False
                sm.stop(root)
                factory.assert_called_once_with(42, terminate=True)
                process.wait.assert_called_once_with(10)
                process.terminate.assert_called_once_with()
            self.assertEqual(sm.read(root / "stop.json"), {"session": "abc"})
            state = sm.read(root / "status.json")
            self.assertFalse(state["running"])
            self.assertTrue(state["forced"])

    def test_stop_never_terminates_reused_pid(self):
        state = {"running": True, "session": "abc", "pid": 42, "started": 123}
        with patch.object(sm, "read", return_value=state), patch.object(sm, "save") as save, \
                patch.object(sm, "RecorderProcess") as factory:
            process = factory.return_value.__enter__.return_value
            process.started = 124
            with self.assertRaisesRegex(RuntimeError, "PID was reused"):
                sm.stop(Path("root"))
            save.assert_not_called()
            process.wait.assert_not_called()
            process.terminate.assert_not_called()

    def test_stop_requires_recorded_creation_time(self):
        with patch.object(sm, "read", return_value={"running": True, "pid": 42}), \
                patch.object(sm, "RecorderProcess") as factory:
            with self.assertRaisesRegex(RuntimeError, "creation time is missing"):
                sm.stop(Path("root"))
            factory.assert_not_called()

    def test_stop_does_not_terminate_a_graceful_exit(self):
        state = {"running": True, "session": "abc", "pid": 42, "started": 123}
        with patch.object(sm, "read", return_value=state), patch.object(sm, "save") as save, \
                patch.object(sm, "RecorderProcess") as factory:
            process = factory.return_value.__enter__.return_value
            process.started, process.wait.return_value = 123, True
            sm.stop(Path("root"))
            save.assert_called_once_with(Path("root/stop.json"), {"session": "abc"})
            process.terminate.assert_not_called()

    def test_process_termination_uses_checked_handle_and_bounded_wait(self):
        kernel = Mock()
        kernel.OpenProcess.return_value = 456

        def times(_handle, created, *_rest):
            created._obj.value = 123
            return True

        kernel.GetProcessTimes.side_effect = times
        kernel.WaitForSingleObject.side_effect = [258, 0]
        with patch.object(sm.ctypes, "WinDLL", return_value=kernel, create=True):
            with sm.RecorderProcess(42, terminate=True) as process:
                self.assertEqual(process.started, 123)
                self.assertFalse(process.wait(10))
                process.terminate()
        kernel.OpenProcess.assert_called_once_with(0x101001, False, 42)
        kernel.TerminateProcess.assert_called_once_with(456, 1)
        self.assertEqual([call.args for call in kernel.WaitForSingleObject.call_args_list],
                         [(456, 10000), (456, 5000)])
        kernel.CloseHandle.assert_called_once_with(456)

    @unittest.skipUnless(os.name == "nt", "patches os.name to nt; on POSIX pathlib then builds WindowsPath, which it cannot instantiate")
    def test_detached_start_timeout_terminates_exact_child_before_status_exists(self):
        with tempfile.TemporaryDirectory() as temp, \
                patch.object(sm.os, "name", "nt"), \
                patch.object(sm, "active", return_value={}), \
                patch.object(sm.time, "monotonic", side_effect=[0, 0, 16]), \
                patch.object(sm.time, "sleep"), \
                patch.object(sm.subprocess, "DETACHED_PROCESS", 8, create=True), \
                patch.object(sm.subprocess, "CREATE_NEW_PROCESS_GROUP", 512, create=True), \
                patch.object(sm.subprocess, "Popen") as popen:
            child = popen.return_value
            child.pid, child.poll.return_value = 42, None
            self.assertEqual(sm.main(["--root", temp, "record", "--detach"]), 1)
            child.terminate.assert_called_once_with()
            child.wait.assert_called_once_with(timeout=5)

    @unittest.skipUnless(os.name == "nt", "Windows display APIs")
    def test_own_hidden_canvas_window_maps_to_physical_display(self):
        import ctypes
        from ctypes import wintypes as w
        frame = sm.fp.Collector(probe=False)
        user = frame.user32
        callback_type = ctypes.WINFUNCTYPE(ctypes.c_ssize_t, w.HWND, w.UINT, w.WPARAM, w.LPARAM)
        user.DefWindowProcW.restype = ctypes.c_ssize_t
        user.DefWindowProcW.argtypes = [w.HWND, w.UINT, w.WPARAM, w.LPARAM]
        callback = callback_type(lambda *args: user.DefWindowProcW(*args))

        class WindowClass(ctypes.Structure):
            _fields_ = [("style", w.UINT), ("callback", callback_type), ("class_extra", ctypes.c_int),
                        ("window_extra", ctypes.c_int), ("instance", w.HANDLE), ("icon", w.HANDLE),
                        ("cursor", w.HANDLE), ("background", w.HANDLE), ("menu", w.LPCWSTR), ("name", w.LPCWSTR)]

        name = "StutterMarkTest-" + sm.uuid.uuid4().hex + ":Canvas"
        window_class = WindowClass()
        window_class.callback, window_class.name = callback, name
        user.RegisterClassW.argtypes = [ctypes.POINTER(WindowClass)]
        user.CreateWindowExW.restype = w.HWND
        user.CreateWindowExW.argtypes = [w.DWORD, w.LPCWSTR, w.LPCWSTR, w.DWORD,
                                        ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                        w.HWND, w.HANDLE, w.HANDLE, ctypes.c_void_p]
        user.DestroyWindow.argtypes = [w.HWND]
        user.UnregisterClassW.argtypes = [w.LPCWSTR, w.HANDLE]
        hwnd = None
        try:
            self.assertTrue(user.RegisterClassW(ctypes.byref(window_class)))
            hwnd = user.CreateWindowExW(0, name, "stutter unit test", 0, 0, 0, 100, 100,
                                       None, None, None, None)
            self.assertTrue(hwnd)
            displays = sm.Displays(frame)
            inventory = displays.inventory()
            self.assertTrue(inventory)
            self.assertTrue(all(row["display_config_status"] == 0 and row["adapter"] for row in inventory))
            rows = displays.sample({os.getpid()})
            own = next(row for row in rows if row["hwnd"] == hwnd)
            self.assertEqual(own["win"], "hidden")
            self.assertIn(own["adapter"], {row["adapter"] for row in inventory})
            self.assertIn(own["monitor"], {row["monitor"] for row in inventory})
        finally:
            if hwnd:
                user.DestroyWindow(hwnd)
            user.UnregisterClassW(name, None)
            frame.close()
            frame.pdh.PdhCloseQuery(frame.pdh_query)

    def test_adapter_unsigned_and_normalized(self):
        self.assertEqual(sm.adapter_id(-1, 10), "0xFFFFFFFF_0x0000000A")

    def test_gpu_loads_keep_adapters_and_busiest_engine(self):
        def inst(pid, low, engine, kind):
            return f"pid_{pid}_luid_0x00000000_0x{low:08x}_phys_0_eng_{engine}_engtype_{kind}"
        adapters, procs = sm.gpu_loads([
            (inst(1, 10, 0, "3D"), 30), (inst(2, 10, 0, "3D"), 20),
            (inst(1, 10, 1, "3D"), 40), (inst(1, 11, 0, "Copy"), 8),
            (inst(2, 10, 2, "VideoDecode"), 6), ("invalid", 99)], {1: "TS3W.exe", 2: "dwm.exe"})
        self.assertEqual(adapters[0]["3D"], 50)
        self.assertEqual(adapters[0]["Video"], 6)
        self.assertEqual(adapters[1]["Copy"], 8)
        self.assertEqual(next(p for p in procs if p["pid"] == 1 and "3D" in p)["3D"], 70)
        self.assertEqual(len(procs), 3)

    def test_buffer_prunes_disk_and_memory_and_closes(self):
        with tempfile.TemporaryDirectory() as temp:
            buffer = sm.Buffer(Path(temp), "test", retain=2)
            try:
                for at in (0, .25, 1, 2, 3):
                    buffer.append({"t": at})
                self.assertEqual([r["t"] for r in buffer.rows], [1, 2, 3])
                self.assertFalse((Path(temp) / "0.jsonl").exists())
                self.assertEqual(len((Path(temp) / "1.jsonl").read_text().splitlines()), 1)
            finally:
                buffer.close()

    def test_snapshot_exact_inclusive_window_and_incomplete_tail(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            buffer = sm.Buffer(root / "buffer", "test")
            try:
                for at in (69.75, 70, 99.75, 100, 110, 110.25):
                    buffer.append({"t": at, "procs": [], "gpu_processes": []})
                folder = sm.snapshot(root, buffer, {"t": 100, "id": "12345678", "note": "test"}, False)
                rows = [json.loads(line) for line in (folder / "samples.jsonl").read_text().splitlines()]
                self.assertEqual([r["t"] for r in rows], [70, 99.75, 100, 110])
                meta = sm.read(folder / "mark.json")
                self.assertFalse(meta["complete"])
                self.assertEqual(meta["pre_seconds"], 30)
                self.assertEqual(meta["post_seconds"], 10)
            finally:
                buffer.close()

    def test_cross_adapter_requires_observed_render(self):
        row = {"windows": [{"pid": 1, "monitor": "DISPLAY1", "adapter": "A"}],
               "gpu_processes": [{"pid": 1, "adapter": "B", "Copy": 10}]}
        self.assertEqual(sm.cross_adapter(row), [])
        row["gpu_processes"][0]["3D"] = 1
        self.assertEqual(sm.cross_adapter(row)[0]["render_adapters"], ["B"])
        row["windows"][0]["adapter"] = None
        self.assertEqual(sm.cross_adapter(row), [])

    def test_mark_request_and_stopped_state(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "requests").mkdir()
            with self.assertRaises(RuntimeError):
                sm.request_mark(root, "test")
            sm.save(root / "status.json", {"running": True, "t": sm.time.time(), "session": "abc",
                                         "pid": 42, "started": 123})
            with patch.object(sm, "RecorderProcess") as factory:
                process = factory.return_value.__enter__.return_value
                process.started, process.wait.return_value = 123, False
                mark = sm.request_mark(root, "felt stutter", at=100)
            self.assertEqual(sm.read(root / "requests" / (mark["id"] + ".json")), mark)
            self.assertEqual(mark["session"], "abc")

    def test_summary_reports_near_mark_contributors(self):
        row = {"t": 100, "dt": .25, "procs": [{"pid": 1, "name": "test", "cores": .5, "io_mb": 1}],
               "gpu_processes": [], "cpu": 10}
        text = sm.summary([row], {"t": 100, "note": "note"})
        self.assertIn("test[1] 0.50c", text)
        self.assertIn("test[1] 4.00 MB/s", text)
        self.assertIn("cpu: 10.00%", text)
        self.assertLess(len(text.splitlines()), 24)

    def test_cpu_once_per_second_gpu_every_sample_with_cpu_timestamp(self):
        collector = sm.Collector.__new__(sm.Collector)
        collector.cpu = SimpleNamespace(
            sample=Mock(return_value={"t": 100, "dt": 1}), scan=Mock(), scan_names={})
        collector.frame = SimpleNamespace(
            pdh=SimpleNamespace(PdhCollectQueryData=Mock()), pdh_query=None,
            _array=Mock(return_value=[]), lock=threading.Lock(), intervals=[],
            _dwm=Mock(return_value=None), prev_dwm=None, flush_ok=True)
        collector.displays = SimpleNamespace(inventory=Mock(return_value=[]), sample=Mock(return_value=[]))
        collector.started = 0
        collector.cpu_started = 0
        collector.cpu_row = None
        collector.cpu_second = None
        with patch.object(sm.time, "monotonic", side_effect=[10, 10.1, 10.25, 10.3, 11, 11.1]):
            rows = [collector.sample() for _ in range(3)]
        self.assertEqual(collector.cpu.sample.call_count, 2)
        self.assertEqual(collector.cpu.scan.call_count, 1)
        self.assertEqual(collector.frame._array.call_count, 3)
        self.assertEqual([row["cpu_t"] for row in rows], [100, 100, 100])
        self.assertEqual(collector.displays.inventory.call_count, 2)


if __name__ == "__main__":
    unittest.main()
