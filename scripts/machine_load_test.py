"""Unit tests for machine_load.py's summary of the machine's load over a span: python -m unittest scripts.machine_load_test"""
from __future__ import annotations

import os
import sys
import threading
import time
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import machine_load as ml  # noqa: E402

UNIT = 100_000  # 10 ms in 100 ns units, so cpu_s reads in seconds below


def sample(t: float, idle: int, kernel: int, user: int, avail: float, procs: dict) -> dict:
    return {"t": t, "idle": idle * UNIT, "kernel": kernel * UNIT, "user": user * UNIT, "avail_mb": avail, "total_mb": 4096.0,
            "procs": {key: (name, cpu * UNIT, ws) for key, (name, cpu, ws) in procs.items()}}


# Three samples 2 s apart; each interval is 1000 units of every processor's time. The first is 40% busy (400 of
# 1000), the second 10%. TS3W uses 250 then 50; vivaldi 50 then exits; dotnet starts in the first interval (100) and
# uses 50 more. Idle (pid 0) holds the idle time.
SAMPLES = [
    sample(0.0, 1000, 1500, 500, 1000, {(0, 0): ("Idle", 1000, 0), (100, 1): ("TS3W.exe", 300, 1 << 30),
                                        (200, 2): ("vivaldi.exe", 200, 0)}),
    sample(2.0, 1600, 2200, 800, 800, {(0, 0): ("Idle", 1600, 0), (100, 1): ("TS3W.exe", 550, 1 << 30),
                                       (200, 2): ("vivaldi.exe", 250, 0), (300, 3): ("dotnet.exe", 100, 0)}),
    sample(4.0, 2500, 3150, 850, 900, {(0, 0): ("Idle", 2500, 0), (100, 1): ("TS3W.exe", 600, 2 << 30),
                                       (300, 3): ("dotnet.exe", 150, 0)}),
]


class Summary(unittest.TestCase):
    def test_cpu_memory_and_the_top_processes_over_the_span(self) -> None:
        load = ml.summarize(SAMPLES)
        self.assertEqual((load["samples"], load["seconds"]), (3, 4.0))
        self.assertEqual(load["cpu_pct"], {"mean": 25.0, "max": 40.0})  # 500 of 2000; the first interval's 40%
        self.assertEqual(load["avail_mb"], {"min": 800, "mean": 900, "start": 1000, "end": 900})
        self.assertEqual(load["total_mb"], 4096)
        # Task Manager's convention: a process's share of every processor's time over the span. Idle is not a process
        # here; dotnet, started within the span, counts all of its time; vivaldi until its last sample.
        self.assertEqual([(p["name"], p["pid"], p["cpu_pct"], p["max_pct"], p["cpu_s"]) for p in load["top"]],
                         [("TS3W.exe", 100, 15.0, 25.0, 3.0), ("dotnet.exe", 300, 7.5, 10.0, 1.5),
                          ("vivaldi.exe", 200, 2.5, 5.0, 0.5)])
        self.assertEqual(load["top"][0]["ws_mb"], 2048)
        self.assertEqual([p["name"] for p in load["others"]], ["dotnet.exe", "vivaldi.exe"])  # less the stack's games
        self.assertEqual(load["series"], [[2.0, 40.0, 800], [4.0, 10.0, 900]])
        self.assertEqual(sum(p["cpu_pct"] for p in load["top"]), load["cpu_pct"]["mean"])

    def test_the_line_names_others_when_the_top_holds_the_stacks_games(self) -> None:
        line = ml.text(ml.summarize(SAMPLES, top=2))
        self.assertIn("4 s, 3 samples; cpu 25.0% mean, 40.0% max; free 0.8 GB min of 4.0 GB", line)
        self.assertIn("top: TS3W.exe 100 15.0% (max 25%), dotnet.exe 300 7.5% (max 10%); others: vivaldi.exe 200 2.5%", line)
        self.assertNotIn("others", ml.text(ml.summarize([SAMPLES[0], {**SAMPLES[1], "procs": {}}])))

    def test_too_few_samples(self) -> None:
        one = ml.summarize(SAMPLES[:1])
        self.assertEqual((one["cpu_pct"], one["avail_mb"]["min"], one["top"]), ({"mean": None, "max": None}, 1000, []))
        self.assertIn("cpu -", ml.text(one))
        self.assertIsNone(ml.summarize([])["avail_mb"])
        self.assertEqual(ml.text(None), "no load recorded")


class Sampling(unittest.TestCase):
    def test_the_sampler_samples_until_stopped_and_notes_a_failed_read(self) -> None:
        calls = []
        enough = threading.Event()

        def read() -> dict:
            calls.append(1)
            if len(calls) == 2:
                raise OSError("boom")
            if len(calls) >= 4:
                enough.set()
            return SAMPLES[min(len(calls) - 1, 2)] if len(calls) < 4 else {**SAMPLES[2], "t": SAMPLES[2]["t"] + len(calls)}

        sampler = ml.Sampler(0.01, read=read).start()
        self.assertTrue(enough.wait(5))
        load = sampler.stop()
        self.assertGreaterEqual(load["samples"], 3)
        self.assertEqual(load["error"], "OSError: boom")
        self.assertEqual((load["interval_s"], load["reader"]), (0.01, ml.READER))
        self.assertLessEqual(load["from"], load["to"])
        self.assertIn("read error: OSError: boom", ml.text(load))
        count = len(calls)
        time.sleep(0.05)
        self.assertEqual(len(calls), count, "stop() ends the sampling thread")


@unittest.skipUnless(sys.platform == "win32", "the reads are Windows calls")
class LiveReads(unittest.TestCase):
    """The structures' layout, checked against this machine: a wrong offset fails each of these."""

    def test_the_process_list_and_the_system_times_agree(self) -> None:
        a = ml.snapshot()
        spin = time.perf_counter() + 1.0
        while time.perf_counter() < spin:
            pass
        b = ml.snapshot()
        mine = [key for key in b["procs"] if key[0] == os.getpid()]
        self.assertEqual(len(mine), 1)
        name, cpu, ws = b["procs"][mine[0]]
        self.assertTrue(name.lower().startswith("python"), name)
        self.assertGreater(ws, 1 << 20)
        self.assertEqual(b["procs"][next(k for k in b["procs"] if k[0] == 0)][0], "Idle")
        self.assertIn("System", [n for (pid, _), (n, _, _) in b["procs"].items() if pid == 4])
        used = cpu - a["procs"][mine[0]][1]
        self.assertGreater(used, 0)  # the process CPU counter advanced during the spin
        self.assertLess(used, (b["t"] - a["t"] + 0.5) * 1e7)
        # Every process's time over the interval, Idle's included, is the processors' time (less interrupt and DPC time,
        # which no process is charged); Idle's is the idle time.
        span = ml.total(b) - ml.total(a)
        moved = sum(c - (a["procs"][k][1] if k in a["procs"] else 0) for k, (_, c, _) in b["procs"].items())
        self.assertLess(abs(moved - span) / span, 0.1)
        idle = b["procs"][(0, next(k for k in b["procs"] if k[0] == 0)[1])][1] - a["procs"][next(k for k in a["procs"] if k[0] == 0)][1]
        self.assertLess(abs(idle - (b["idle"] - a["idle"])) / max(1, b["idle"] - a["idle"]), 0.2)
        self.assertTrue(0 < b["avail_mb"] < b["total_mb"])


if __name__ == "__main__":
    unittest.main()
