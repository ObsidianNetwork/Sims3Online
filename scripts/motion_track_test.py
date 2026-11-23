"""Unit tests for motion_track on a synthetic watch: python -m unittest scripts.motion_track_test"""
from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import motion_track as mt  # noqa: E402

PABLO = "18031287978496800272"
T0 = 62135596800000 + 77_545_000  # 1970-01-01 21:32:25.000 UTC in MotionWatch's ms since year 1


def saved() -> dict:
    """The server's Pablo walks 0.5 m along z after the copy stopped at 0.1 m."""
    server = [[T0 + i * 100, 10.0, 0.0, 20.0 - 0.05 * i, 0, 1, 1 if i < 10 else 0, "Idle:Standing", i] for i in range(12)]
    client = [[T0 + 50 + i * 100, 10.0, 0.0, 20.0 - min(0.05 * i, 0.1), 0, 1, 1 if i < 2 else 0, "Idle:Standing", i]
              for i in range(12)]
    return {"ids": [PABLO, "18031287978496800304"], "data": {"server": {PABLO: server}, "client": {PABLO: client}}}


class SimIdTest(unittest.TestCase):
    def test_name_and_suffix(self) -> None:
        self.assertEqual(mt.sim_id(saved(), "Pablo"), PABLO)
        self.assertEqual(mt.sim_id(saved(), "800272"), PABLO)

    def test_ambiguous_or_missing(self) -> None:
        with self.assertRaises(SystemExit):
            mt.sim_id(saved(), "800")
        with self.assertRaises(SystemExit):
            mt.sim_id(saved(), "Bob")


class RowsTest(unittest.TestCase):
    def test_pairs_the_newest_client_sample_before_each_server_one(self) -> None:
        rows = mt.rows(saved(), PABLO, None, None, 0)
        # The first server sample has no client sample yet; the copy's is 50 ms older from the second on.
        self.assertEqual(len(rows), 11)
        self.assertTrue(rows[0].startswith("21:32:25.100  server 10.00,19.95 w=1"))
        self.assertIn("| client 10.00,20.00 w=1", rows[0])
        self.assertTrue(rows[-1].endswith("| 0.45 m"))
        self.assertIn("w=0", rows[-1].split("|")[0])

    def test_span_and_step(self) -> None:
        rows = mt.rows(saved(), PABLO, "21:32:25.300", "21:32:25.800", 200)
        self.assertEqual([r[:12] for r in rows], ["21:32:25.300", "21:32:25.500", "21:32:25.700"])

    def test_a_bound_in_whole_seconds_keeps_that_second(self) -> None:
        rows = mt.rows(saved(), PABLO, "21:32:25", "21:32:25", 0)
        self.assertEqual(len(rows), 9)
        self.assertTrue(rows[-1].startswith("21:32:25.900"))


class PaceTest(unittest.TestCase):
    def test_each_sides_walk_and_the_rest(self) -> None:
        lines = mt.pace(saved(), PABLO, None, None)
        # The server walks samples 0-9 (0.45 m in 900 ms); the copy samples 0-1 (0.05 m in 100 ms); no tick column.
        self.assertEqual(len(lines), 3)
        self.assertIn("server walk 0.45 m in 900 ms (- ticks), 0.50 m/s; moved on 9 samples", lines[0])
        self.assertIn("client walk 0.05 m in 100 ms (- ticks)", lines[1])
        self.assertEqual(lines[2], "rest: the copy ended 0.45 m from the server's Sim")

    def test_ticks_come_from_the_tenth_column(self) -> None:
        samples = [(T0 + i * 33, 0.0, 0.0, 0.03 * i, 0, 1, 1, "-", i, 100 + i) for i in range(31)]
        (walk,) = mt.walks(samples)
        self.assertEqual((walk["ms"], walk["ticks"], walk["moves"]), (990, 30, 30))
        self.assertAlmostEqual(walk["m"], 0.9)

    def test_a_walk_with_moves_on_alternate_samples_keeps_its_duration(self) -> None:
        # The same 0.9 m in the same time, moving on every second sample: the duration is unchanged.
        samples = [(T0 + i * 33, 0.0, 0.0, 0.06 * (i // 2), 0, 1, 1, "-", i, 100 + i) for i in range(31)]
        (walk,) = mt.walks(samples)
        self.assertEqual((walk["ms"], walk["ticks"], walk["moves"]), (990, 30, 15))


if __name__ == "__main__":
    unittest.main()
