"""arm_readout.py: tick rates and GC heap counter changes from a before reading, and the stream silences in a window.
No game, no files."""
from __future__ import annotations

import sys
import unittest
from datetime import datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import arm_readout as ar  # noqa: E402
import stream_gaps  # noqa: E402


class Rates(unittest.TestCase):
    BEFORE = {"arm": "heap352", "repeat": "1", "time": 1000.0, "ticks": {"server": 3000, "client1": 2900}}

    def test_ticks_per_second_since_the_before_reading(self) -> None:
        got = ar.rates(self.BEFORE, {"server": 6000, "client1": 5900}, 1100.0, "heap352", "1")
        self.assertEqual(got, {"server": 30.0, "client1": 30.0})

    def test_another_arm_or_repeat_reads_minus_one(self) -> None:
        self.assertEqual(ar.rates(self.BEFORE, {"server": 6000}, 1100.0, "base", "1"), {"server": -1.0})
        self.assertEqual(ar.rates(self.BEFORE, {"server": 6000}, 1100.0, "heap352", "2"), {"server": -1.0})
        self.assertEqual(ar.rates({}, {"server": 6000}, 1100.0, "heap352", "1"), {"server": -1.0})

    def test_a_game_not_read_or_restarted_reads_minus_one(self) -> None:
        got = ar.rates(self.BEFORE, {"server": -1, "client1": 100, "client2": 500}, 1100.0, "heap352", "1")
        self.assertEqual(got, {"server": -1.0, "client1": -1.0, "client2": -1.0})


class GcHeap(unittest.TestCase):
    REPORT = ("walkerCopy=on,ready=1;gcHeap=on,installed=1,site=0,sites=4,initialMB=352,growthMB=32,heapMB=355,"
              "gcNo=239,initAtLoad=0,timer=1,timerSite=0,forced=4,forcedMs=381,forcedMaxMs=134.517,forcedLastMs=41.895,"
              "forced50=3,forced100=2,commitMB=1464,peakCommitMB=1472;next=1")

    def test_the_field_numbers(self) -> None:
        got = ar.gc_heap(self.REPORT)
        self.assertEqual((got["forced"], got["heapMB"], got["peakCommitMB"]), (4, 355, 1472))
        self.assertEqual(got["forcedMaxMs"], 134.517)
        self.assertNotIn("walkerCopy", got)
        self.assertNotIn("next", got)

    def test_no_field_or_before_version_14(self) -> None:
        self.assertEqual(ar.gc_heap("walkerCopy=on"), {})
        self.assertEqual(ar.gc_heap("gcHeap=off,ready=0"), {"ready": 0})

    def test_counter_changes_over_the_arm(self) -> None:
        before = {"arm": "base", "repeat": "2", "time": 1000.0,
                  "gc": {"server": {"forced": 4, "forcedMs": 381, "forced50": 3, "forced100": 2, "gcNo": 239}}}
        after = {"server": {"forced": 10, "forcedMs": 981, "forced50": 5, "forced100": 2, "gcNo": 300, "heapMB": 64,
                            "commitMB": 1500, "peakCommitMB": 1510},
                 "client2": {"forced": 9, "heapMB": 70}}
        got = ar.gc_changes(before, after, 1300.0, "base", "2")
        self.assertEqual(got["server"], {"forced": 6, "forcedMs": 600, "forced50": 2, "forced100": 0, "gcNo": 61,
                                         "forcedPerMin": 1.2, "heapMB": 64, "commitMB": 1500, "peakCommitMB": 1510})
        self.assertEqual(got["client2"]["forced"], -1)  # no before reading for this game
        self.assertEqual(got["client2"]["forcedPerMin"], -1)
        self.assertEqual(got["client2"]["commitMB"], -1)

    def test_another_arm_or_a_restarted_game_reads_minus_one(self) -> None:
        before = {"arm": "base", "repeat": "1", "time": 1000.0, "gc": {"server": {"forced": 40}}}
        self.assertEqual(ar.gc_changes(before, {"server": {"forced": 50}}, 1300.0, "heap352", "1")["server"]["forced"], -1)
        self.assertEqual(ar.gc_changes(before, {"server": {"forced": 3}}, 1300.0, "base", "1")["server"]["forced"], -1)


class Gaps(unittest.TestCase):
    LOG = "\n".join([
        "[2026-10-22 01:00:00.000Z] received: {\"$type\":\"sim_animation\"}",
        "[2026-10-22 01:00:01.000Z] received: {\"$type\":\"sim_animation\"}",
        "[2026-10-22 01:00:04.000Z] received: {\"$type\":\"sim_animation\"}",  # 3.0 s silent
        "[2026-10-22 01:00:05.000Z] received: {\"$type\":\"sim_animation\"}",
        "[2026-10-22 01:00:07.000Z] received: {\"$type\":\"sim_animation\"}",  # 2.0 s silent
        "[2026-10-22 01:00:20.000Z] received: {\"$type\":\"sim_animation\"}",  # 13 s, after the window
    ])

    def test_silences_in_the_window(self) -> None:
        lines = stream_gaps.parse(self.LOG)
        count, total, worst = ar.gap_values(lines, datetime(2026, 10, 22, 1, 0, 0), datetime(2026, 10, 22, 1, 0, 6))
        self.assertEqual((count, total, worst), (2, 5.0, 3.0))

    def test_an_empty_log_has_none(self) -> None:
        self.assertEqual(ar.gap_values([], datetime(2026, 10, 22), datetime(2026, 10, 23)), (0, 0, 0))

    def test_games_follow_the_client_count(self) -> None:
        self.assertEqual(list(ar.games(1)), ["server", "client1"])
        self.assertEqual(list(ar.games(2)), ["server", "client1", "client2"])


if __name__ == "__main__":
    unittest.main()
