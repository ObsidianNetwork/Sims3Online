"""Tests for hitch_watch: spike rules, aggregation and log reading on recorded samples; one live Collector read on Windows."""
from __future__ import annotations

import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hitch_watch as hw  # noqa: E402

CORES = 16


def sample(**kw) -> dict:
    base = {"time": "20:13:54", "dt": 1.0, "cpu": 20.0, "queue": 1.0, "ctx": 50000, "dpc": 0.4, "int": 0.9,
            "dpcs": 5000, "disk_ms": 1.0, "disk_mb_s": 5.0, "gpu": 10.0, "lat_ms": 2.0, "other_cpu": 3.0,
            "procs": [], "starts": {}, "n_starts": 0, "n_exits": 0}
    base.update(kw)
    return base


# a second recorded during a live window: two games, an agent shell storm and a cargo build
STORM = sample(cpu=91.0, queue=40.0, n_starts=29, lat_ms=64.0, other_cpu=82.0,
               procs=[{"pid": 10, "name": "TS3W.exe", "cores": 1.2, "priv_mb": 1400, "io_mb": 0.2, "prio": "normal", "gpu": 20.0},
                      {"pid": 11, "name": "TS3W.exe", "cores": 1.1, "priv_mb": 1380, "io_mb": 60.0, "prio": "normal", "gpu": 18.0},
                      {"pid": 12, "name": "cl.exe", "cores": 1.0, "priv_mb": 200, "io_mb": 0.0, "prio": "normal"},
                      {"pid": 13, "name": "bash.exe", "cores": 0.4, "priv_mb": 10, "io_mb": 0.0, "prio": "normal"}],
               starts={"clippy-driver.exe<-cargo.exe": 16, "bash.exe<-bash.exe": 7})
QUIET = sample()


class SpikeRules(unittest.TestCase):
    def test_quiet_second_is_not_a_spike(self):
        self.assertEqual(hw.find_spikes(QUIET, CORES), [])

    def test_each_rule_fires_alone(self):
        self.assertEqual(len(hw.find_spikes(sample(cpu=85.1), CORES)), 1)
        self.assertEqual(hw.find_spikes(sample(cpu=85.0), CORES), [])
        self.assertEqual(len(hw.find_spikes(sample(queue=33.0), CORES)), 1)
        self.assertEqual(hw.find_spikes(sample(queue=32.0), CORES), [])
        self.assertEqual(len(hw.find_spikes(sample(n_starts=8), CORES)), 1)
        self.assertEqual(hw.find_spikes(sample(n_starts=7), CORES), [])
        self.assertEqual(len(hw.find_spikes(sample(lat_ms=50.0), CORES)), 1)

    def test_queue_limit_scales_with_cores(self):
        self.assertEqual(hw.find_spikes(sample(queue=16.0), 8), [])
        self.assertEqual(len(hw.find_spikes(sample(queue=16.0), 4)), 1)

    def test_storm_names_every_reason(self):
        self.assertEqual(len(hw.find_spikes(STORM, CORES)), 4)


class Contributors(unittest.TestCase):
    def test_same_name_is_summed_across_pids(self):
        top = hw.top_contributors(STORM)
        self.assertEqual(top[0][0], "TS3W.exe")
        self.assertAlmostEqual(top[0][1], 2.3)
        self.assertEqual([name for name, _ in top], ["TS3W.exe", "cl.exe", "bash.exe"])

    def test_description_carries_the_spike_evidence(self):
        text = hw.describe_spike(STORM, hw.find_spikes(STORM, CORES))
        for needle in ("TS3W.exe 2.30c", "clippy-driver.exe<-cargo.exe x16", "unaccounted 82%", "io: TS3W.exe 60 MB"):
            self.assertIn(needle, text)


class Summary(unittest.TestCase):
    def test_summary_counts_spikes_and_totals(self):
        s = hw.summarize([QUIET, STORM, QUIET, QUIET], CORES)
        self.assertEqual(s["samples"], 4)
        self.assertEqual(s["spike_seconds"], 1)
        self.assertEqual(s["cpu_max"], 91.0)
        self.assertEqual(s["late_seconds"], 1)
        self.assertEqual(s["peak_priv_mb"]["TS3W.exe"], 2780)
        self.assertEqual(s["priority"]["TS3W.exe"], "normal")
        self.assertEqual(s["starts"]["clippy-driver.exe<-cargo.exe"], 16)
        self.assertAlmostEqual(s["starts_per_min"], 29 * 60.0 / 4)
        self.assertAlmostEqual(s["gpu_by_name"]["TS3W.exe"], 9.5)

    def test_empty_log_does_not_divide_by_zero(self):
        s = hw.summarize([], CORES)
        self.assertEqual(s["samples"], 0)
        self.assertEqual(s["spike_seconds"], 0)
        hw.format_summary("empty", s)

    def test_percentile(self):
        self.assertEqual(hw.percentile([], 0.95), 0.0)
        self.assertEqual(hw.percentile(list(range(101)), 0.95), 95)


def games(count: int, each: float = 1.0, **kw) -> dict:
    procs = [{"pid": 100 + i, "name": "TS3W.exe", "cores": each, "priv_mb": 1400, "io_mb": 0.0, "prio": "normal", "gpu": 8.0}
             for i in range(count)]
    return sample(n_ts3w=count, procs=procs + kw.pop("extra", []), **kw)


CL = {"pid": 9, "name": "cl.exe", "cores": 3.0, "priv_mb": 100, "io_mb": 0.0, "prio": "normal"}


class PerCount(unittest.TestCase):
    def test_seconds_group_by_ts3w_count_and_build_load(self):
        rows = hw.by_ts3w_count([games(1), games(1), games(2, queue=6.0), games(2, extra=[CL], queue=40.0, lat_ms=70.0)])
        key = {(r["ts3w"], r["build"]): r for r in rows}
        self.assertEqual(sorted(key), [(1, False), (2, False), (2, True)])
        self.assertEqual(key[(1, False)]["seconds"], 2)
        self.assertEqual(key[(2, True)]["queue_mean"], 40.0)
        self.assertEqual(key[(2, True)]["late_seconds"], 1)
        self.assertEqual(key[(2, False)]["queue_mean"], 6.0)

    def test_per_instance_cores_and_gpu(self):
        row = hw.by_ts3w_count([games(2, each=1.25)])[0]
        self.assertEqual((row["ts3w_cores"], row["cores_each"], row["gpu_ts3w"]), (2.5, 1.25, 16.0))

    def test_build_floor_is_inclusive_and_applies_to_summed_build_cores(self):
        small = {**CL, "cores": 0.5}
        self.assertFalse(hw.by_ts3w_count([games(1, extra=[small])])[0]["build"])
        self.assertTrue(hw.by_ts3w_count([games(1, extra=[small, {**small, "pid": 10, "name": "rustc.exe"}])])[0]["build"])

    def test_older_logs_fall_back_to_distinct_ts3w_pids(self):
        old = sample(procs=[{"pid": 1, "name": "TS3W.exe", "cores": 1.0}, {"pid": 2, "name": "TS3W.exe", "cores": 1.0}])
        self.assertEqual(hw.ts3w_count(old), 2)
        self.assertEqual(hw.ts3w_count(sample()), 0)

    def test_zero_games_has_no_per_instance_division(self):
        row = hw.by_ts3w_count([sample(n_ts3w=0)])[0]
        self.assertEqual((row["ts3w"], row["cores_each"]), (0, 0.0))
        self.assertIn("ts3w  build", hw.format_counts([row]))


class LogFile(unittest.TestCase):
    def test_load_log_reads_header_cores_and_samples(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "log.jsonl"
            path.write_text(json.dumps({"kind": "header", "cores": 8}) + "\n" + json.dumps(STORM) + "\n\n", encoding="utf-8")
            cores, samples = hw.load_log(path)
        self.assertEqual(cores, 8)
        self.assertEqual(samples, [STORM])

    def test_report_prints_each_log_and_a_comparison(self):
        with tempfile.TemporaryDirectory() as tmp:
            paths = []
            for name, rows in (("a.jsonl", [STORM]), ("b.jsonl", [QUIET])):
                path = Path(tmp) / name
                path.write_text(json.dumps({"kind": "header", "cores": CORES}) + "\n" + "\n".join(json.dumps(r) for r in rows) + "\n", encoding="utf-8")
                paths.append(str(path))
            import io
            from contextlib import redirect_stdout
            out = io.StringIO()
            with redirect_stdout(out):
                self.assertEqual(hw.main(["report", *paths]), 0)
        text = out.getvalue()
        self.assertIn("== a.jsonl", text)
        self.assertIn("spike_seconds", text)


@unittest.skipUnless(os.name == "nt", "performance counters and Win32 process APIs")
class LiveCollector(unittest.TestCase):
    def test_one_read_has_every_field_and_sees_this_process(self):
        collector = hw.Collector(gpu=False, probe=False)
        try:
            collector.sample()
            first = collector.sample()
        finally:
            collector.close()
        for key in ("cpu", "queue", "ctx", "dpc", "int", "dpcs", "disk_q", "disk_ms", "gpu", "lat_ms", "other_cpu",
                    "procs", "starts", "n_starts", "n_exits", "nproc", "n_ts3w"):
            self.assertIn(key, first)
        self.assertGreater(first["nproc"], 10)
        self.assertGreaterEqual(first["cpu"], 0.0)
        self.assertLessEqual(first["cpu"], 100.0)


if __name__ == "__main__":
    unittest.main()
