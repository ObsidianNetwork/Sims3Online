"""Unit tests for frame_pacing.py (compositor and GPU sampler): python -m unittest scripts.frame_pacing_test"""
from __future__ import annotations

import json
import sys
import tempfile
import threading
import unittest
from pathlib import Path
from unittest.mock import Mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import frame_pacing as fp  # noqa: E402

ENGINE_3D = "pid_43344_luid_0x00000000_0x0000D4B8_phys_0_eng_0_engtype_3D"
ENGINE_COPY = "pid_43344_luid_0x00000000_0x0000D4B8_phys_0_eng_5_engtype_Copy"
ENGINE_DECODE = "pid_1234_luid_0x00000000_0x0000D4B8_phys_0_eng_9_engtype_VideoDecode"


def sample(n_ts3w=0, p95=16.0, stall=0, late=0, missed=0, dropped=0, eng3d=10.0, ts3w_3d=0.0, ded=0, shared=0,
           adapter=1000, presents=60, dt=1.0):
    gpu = [{"pid": 100 + i, "name": "TS3W.exe", "3D": ts3w_3d / max(1, n_ts3w), "ded_mb": ded, "shared_mb": shared}
           for i in range(n_ts3w)]
    gpu.append({"pid": 9, "name": "dwm.exe", "3D": 2.0, "ded_mb": 300, "shared_mb": 20})
    return {"time": "00:00:00", "dt": dt, "n_ts3w": n_ts3w,
            "flush": {"n": presents, "p50": 10.0, "p95": p95, "max": p95 + 5, "slow": stall, "stall": stall},
            "dwm": {"frames": presents, "refreshes": 144, "late": late, "missed": missed, "dropped": dropped, "outstanding": 1},
            "eng": {"3D": eng3d, "Copy": 1.0, "Video": 0.0, "Compute": 0.0, "Other": 0.0},
            "adapter_ded_mb": adapter, "adapter_shared_mb": 100, "gpu": gpu}


class Parsing(unittest.TestCase):
    def test_engine_instance(self):
        got = fp.parse_engine_instance(ENGINE_3D)
        self.assertEqual((got["pid"], got["cls"], got["engtype"]), (43344, "3D", "3D"))
        self.assertEqual(got["engine"], "0x00000000_0x0000D4B8_phys_0_eng_0")

    def test_engine_classes(self):
        self.assertEqual(fp.engine_class("Copy"), "Copy")
        self.assertEqual(fp.engine_class("VideoDecode"), "Video")
        self.assertEqual(fp.engine_class("VideoProcessing"), "Video")
        self.assertEqual(fp.engine_class("Compute_0"), "Compute")
        self.assertEqual(fp.engine_class("Security"), "Other")

    def test_bad_instances_are_none(self):
        self.assertIsNone(fp.parse_engine_instance("_Total"))
        self.assertIsNone(fp.parse_engine_instance("pid_x_luid_0x0_0x1_phys_0_eng_0_engtype_3D"))

    def test_process_memory_instance(self):
        self.assertEqual(fp.parse_process_memory_instance("pid_43344_luid_0x00000000_0x0000D4B8_phys_0"), 43344)
        self.assertIsNone(fp.parse_process_memory_instance("luid_0x00000000_0x0000D4B8_phys_0"))


class Aggregation(unittest.TestCase):
    def test_busiest_engine_sums_processes_on_one_engine(self):
        values = [(fp.parse_engine_instance(ENGINE_3D), 30.0),
                  (fp.parse_engine_instance(ENGINE_3D.replace("43344", "55")), 25.0),     # same engine, other process
                  (fp.parse_engine_instance(ENGINE_3D.replace("eng_0", "eng_1")), 40.0),  # another engine
                  (fp.parse_engine_instance(ENGINE_COPY), 3.0),
                  (fp.parse_engine_instance(ENGINE_DECODE), 7.0)]
        busiest, per_pid = fp.aggregate_engines(values)
        self.assertEqual(busiest["3D"], 55.0)            # 30 + 25, not the other engine's 40
        self.assertEqual((busiest["Copy"], busiest["Video"]), (3.0, 7.0))
        self.assertEqual(per_pid[43344], {"3D": 70.0, "Copy": 3.0})     # a process's load is the sum over its engines

    def test_dwm_delta(self):
        prev = {"frames": 100, "refreshes": 1000, "late": 2, "missed": 0, "dropped": 1}
        cur = {"frames": 160, "refreshes": 1144, "late": 5, "missed": 1, "dropped": 1, "outstanding": 2}
        self.assertEqual(fp.dwm_delta(prev, cur), {"outstanding": 2, "frames": 60, "refreshes": 144, "late": 3, "missed": 1,
                                                    "dropped": 0})

    def test_dwm_delta_first_read_and_restart_count_zero(self):
        cur = {"frames": 5000, "refreshes": 9, "late": 7, "missed": 0, "dropped": 0}
        self.assertEqual(fp.dwm_delta(None, cur)["frames"], 0)
        restarted = {"frames": 3, "refreshes": 1, "late": 0, "missed": 0, "dropped": 0}
        self.assertEqual(fp.dwm_delta(cur, restarted)["frames"], 0)        # went backwards: not a huge negative or wrap

    def test_flush_stats(self):
        stats = fp.flush_stats([16.0, 17.0, 40.0, 60.0, 16.5])
        self.assertEqual((stats["n"], stats["slow"], stats["stall"], stats["max"]), (5, 2, 1, 60.0))
        self.assertEqual(fp.flush_stats([])["n"], 0)

    def test_thresholds_are_inclusive(self):
        stats = fp.flush_stats([fp.SLOW_MS, fp.STALL_MS])
        self.assertEqual((stats["slow"], stats["stall"]), (2, 1))


class Summaries(unittest.TestCase):
    def test_cadence_is_labelled_as_the_median_of_per_second_p95s(self):
        samples = [sample(p95=10), sample(p95=20), sample(p95=90)]
        summary = fp.summarize(samples)
        self.assertEqual(summary["present_median_per_second_p95_ms"], 20)
        self.assertNotIn("present_p95_ms", summary)
        self.assertIn("median per-second p95", fp.format_rows([summary]).splitlines()[0])

    def test_by_count_groups_and_per_game_numbers(self):
        samples = ([sample(0, eng3d=10)] * 4
                   + [sample(1, eng3d=30, ts3w_3d=10.0, ded=1500, shared=40)] * 3
                   + [sample(2, eng3d=50, ts3w_3d=22.0, ded=1500, shared=40, stall=1, late=2)] * 5)
        rows = {r["ts3w"]: r for r in fp.by_ts3w_count(samples)}
        self.assertEqual(sorted(rows), [0, 1, 2])
        self.assertEqual((rows[0]["seconds"], rows[1]["seconds"], rows[2]["seconds"]), (4, 3, 5))
        self.assertEqual(rows[2]["ts3w_3d_each"], 11.0)           # 22 % over two games
        self.assertEqual(rows[2]["ts3w_ded_mb_each"], 1500)       # the per-process figure is each game's own
        self.assertEqual(rows[1]["gpu3d_mean"], 30.0)
        self.assertEqual(rows[0]["dwm_exe_3d_mean"], 2.0)

    def test_rates_per_minute(self):
        s = fp.summarize([sample(2, stall=1, late=2, missed=1)] * 30)
        self.assertEqual(s["stall_per_min"], 60.0)                # 30 stalls in 30 s
        self.assertEqual(s["stall_seconds_pct"], 100.0)
        self.assertEqual((s["dwm_late_per_min"], s["dwm_missed_per_min"], s["dwm_dropped_per_min"]), (120.0, 60.0, 0.0))
        self.assertEqual(s["presents_per_s"], 60.0)

    def test_saturation_counts_seconds_at_the_threshold(self):
        s = fp.summarize([sample(eng3d=95), sample(eng3d=fp.SATURATED), sample(eng3d=50), sample(eng3d=10)])
        self.assertEqual(s["gpu3d_saturated_pct"], 50.0)
        self.assertEqual(s["gpu3d_max"], 95.0)

    def test_empty_log_does_not_divide_by_zero(self):
        s = fp.summarize([])
        self.assertEqual((s["samples"], s["gpu3d_mean"], s["stall_per_min"]), (0, 0.0, 0.0))

    def test_ts3w_count_falls_back_to_gpu_rows(self):
        old = sample(2)
        del old["n_ts3w"]
        self.assertEqual(fp.ts3w_count(old), 2)


class PerGame(unittest.TestCase):
    def test_hidden_canvas_is_not_replaced_by_the_larger_clipboard_window(self):
        import ctypes
        from ctypes import wintypes as w
        collector = fp.Collector.__new__(fp.Collector)
        collector.ct, collector.w, collector.enum_proc = ctypes, w, lambda f: f
        u = collector.user32 = Mock()
        u.GetForegroundWindow.return_value = 99
        u.GetWindow.return_value = 0
        u.IsIconic.return_value = u.IsWindowVisible.return_value = 0
        def pid(hwnd, output):
            ctypes.cast(output, ctypes.POINTER(w.DWORD))[0] = 100
        def rect(hwnd, output):
            target = ctypes.cast(output, ctypes.POINTER(w.RECT)).contents
            target.right, target.bottom = (1024, 768) if hwnd == 1 else (2574, 994)
        def name(hwnd, output, size):
            output.value = "Sandbox:Client3Box:Canvas" if hwnd == 1 else "Sandbox:Client3Box:EAClipboardClass"
        def enumerate_windows(visit, param):
            visit(1, param)
            visit(2, param)
            return 1
        u.GetWindowThreadProcessId.side_effect = pid
        u.GetClientRect.side_effect = rect
        u.GetClassNameW.side_effect = name
        u.EnumWindows.side_effect = enumerate_windows
        self.assertEqual(collector._windows({100}), {100: {"win": "hidden", "fg": False, "w": 1024, "h": 768}})

    def test_collector_attaches_windows_even_without_gpu_counters(self):
        collector = fp.Collector.__new__(fp.Collector)
        collector.prev_t = 0
        collector.lock = threading.Lock()
        collector.intervals = []
        collector.prev_dwm = None
        collector.pdh = Mock()
        collector.pdh_query = None
        collector._dwm = Mock(return_value=None)
        collector._array = Mock(return_value=[])
        collector._processes = Mock(return_value={100: "TS3W.exe", 9: "dwm.exe"})
        collector._windows = Mock(return_value={100: {"win": "hidden", "fg": False, "w": 1024, "h": 768}})
        got = collector.sample()
        self.assertEqual(got["n_ts3w"], 1)
        self.assertEqual(len(got["gpu"]), 1)
        self.assertEqual(got["gpu"][0]["win"], "hidden")
        self.assertEqual(got["gpu"][0]["w"], 1024)
        self.assertEqual(got["gpu"][0]["3D"], 0)
        collector._windows.assert_called_once_with({100})

    def test_one_row_per_pid_with_its_window_state(self):
        def with_windows(a_state, b_state):
            s = sample(2, ts3w_3d=20.0)
            s["gpu"][0].update({"win": a_state, "fg": True, "w": 1024, "h": 768, "3D": 15.0, "ded_mb": 1600})
            s["gpu"][1].update({"win": b_state, "fg": False, "w": 800, "h": 600, "3D": 5.0, "ded_mb": 900})
            return s
        rows = {r["pid"]: r for r in fp.per_game([with_windows("visible", "hidden")] * 3 + [with_windows("visible", "minimized")])}
        self.assertEqual(sorted(rows), [100, 101])
        self.assertEqual((rows[100]["win"], rows[100]["size"], rows[100]["fg_pct"]), ("visible", "1024x768", 100))
        self.assertEqual((rows[101]["win"], rows[101]["size"], rows[101]["fg_pct"]), ("hidden", "800x600", 0))  # the commonest state
        self.assertEqual((rows[100]["seconds"], rows[100]["gpu3d_mean"], rows[101]["ded_mb"]), (4, 15.0, 900))
        self.assertIn("800x600", fp.format_games(list(rows.values())))

    def test_a_row_without_window_fields_reads_as_none(self):
        rows = fp.per_game([sample(1, ts3w_3d=4.0)])
        self.assertEqual((rows[0]["win"], rows[0]["size"]), ("none", "0x0"))


class Logs(unittest.TestCase):
    def test_load_log_and_tables(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "a.jsonl"
            lines = [json.dumps({"kind": "header", "label": "a"})] + [json.dumps(sample(1, ts3w_3d=9.0)) for _ in range(3)]
            path.write_text("\n".join(lines) + "\n\n", encoding="utf-8")
            header, samples = fp.load_log(path)
        self.assertEqual((header["label"], len(samples)), ("a", 3))
        table = fp.format_rows(fp.by_ts3w_count(samples))
        self.assertEqual(len(table.splitlines()), 2)
        self.assertIn("stall/m", table.splitlines()[0])
        labelled = fp.format_rows([{"ts3w": "all", **fp.summarize(samples)}], ["a"])
        self.assertTrue(labelled.splitlines()[1].startswith("a  "))


if __name__ == "__main__":
    unittest.main()
