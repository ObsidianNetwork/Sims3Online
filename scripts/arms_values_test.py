"""arms_values.py: per-arm values from arms --table --json rows and the permutation P. No game, no files."""
from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import arms_values as av  # noqa: E402


def row(arm: str, repeat: int, verdicts: str = "P6", hook: dict | None = None, fields: dict | None = None) -> dict:
    return {"series": "s", "arm": arm, "repeat": repeat, "run": f"r{arm}{repeat}", "wall_min": 5.5, "scenario_min": 5.2,
            "crashes": 0, "crashes_outside": 0, "verdicts": verdicts, "hook": hook or {}, "fields": fields or {}}


class Values(unittest.TestCase):
    def test_row_numbers_verdicts_hook_and_fields(self) -> None:
        got = av.row_values(row("base", 1, "F1 P5", {"serverTps": 29.9, "client2Forced": -1, "x": "on"},
                                {"S:gcHeap.forced": "3", "C:gcHeap.commitMB": "-14"}))
        self.assertEqual((got["passed"], got["failed"]), (5, 1))
        self.assertEqual(got["hook.serverTps"], 29.9)
        self.assertNotIn("hook.client2Forced", got)  # -1: not read
        self.assertNotIn("hook.x", got)
        self.assertEqual((got["S:gcHeap.forced"], got["C:gcHeap.commitMB"]), (3, -14))

    def test_an_arm_not_run_has_no_verdict_values(self) -> None:
        r = row("base", 1, "not run")
        r["run"] = None
        self.assertNotIn("passed", av.row_values(r))


class Permutation(unittest.TestCase):
    def test_fully_separated_groups_of_four(self) -> None:
        self.assertAlmostEqual(av.permutation_p([10, 11, 12, 13], [1, 2, 3, 4]), 2 / 70)

    def test_equal_groups(self) -> None:
        self.assertEqual(av.permutation_p([1, 1], [1, 1]), 1.0)

    def test_two_repeats_cannot_go_below_a_third(self) -> None:
        self.assertAlmostEqual(av.permutation_p([10, 11], [1, 2]), 2 / 6)

    def test_sampled_when_large(self) -> None:
        a, b = [float(i) for i in range(12)], [float(i) + 100 for i in range(12)]
        old = av.SAMPLES
        av.SAMPLES = 2000
        try:
            self.assertLess(av.permutation_p(a, b), 0.01)
        finally:
            av.SAMPLES = old


class Compare(unittest.TestCase):
    def test_names_patterns_and_means(self) -> None:
        rows = [row("heap352", 1, hook={"serverForcedPerMin": 0.0}), row("heap352", 2, hook={"serverForcedPerMin": 0.1}),
                row("base", 1, hook={"serverForcedPerMin": 1.0}), row("base", 2, hook={"serverForcedPerMin": 1.2}),
                row("other", 1, hook={"serverForcedPerMin": 9.0})]
        got = av.compare(rows, "heap352", "base", ["hook.*ForcedPerMin"])
        self.assertEqual(len(got), 1)
        self.assertEqual((got[0]["a"], got[0]["b"]), ([0.0, 0.1], [1.0, 1.2]))
        self.assertAlmostEqual(got[0]["mean_b"], 1.1)
        self.assertAlmostEqual(got[0]["p"], 1 / 3)


class Counts(unittest.TestCase):
    def test_pooled_counts_over_minutes(self) -> None:
        rows = [row("heap352", 1, hook={"client2Forced": -1}), row("heap352", 2, hook={"client2Forced": 0}),
                row("heap352", 3, hook={"client2Forced": 0}),
                row("base", 1, hook={"client2Forced": 5}), row("base", 2, hook={"client2Forced": 5})]
        got = av.count_compare(rows, "heap352", "base", ["hook.client2Forced"])
        self.assertEqual(len(got), 1)
        r = got[0]
        self.assertEqual((r["count_a"], r["min_a"], r["runs_a"]), (0, 11.0, 2))  # the -1 repeat is left out
        self.assertEqual((r["count_b"], r["min_b"], r["runs_b"]), (10, 11.0, 2))
        self.assertEqual(r["ratio"], 0.0)
        self.assertAlmostEqual(r["p"], 2 * 0.5 ** 10)

    def test_no_events_gives_no_p(self) -> None:
        rows = [row("heap352", 1, hook={"serverForced": 0}), row("base", 1, hook={"serverForced": 0})]
        got = av.count_compare(rows, "heap352", "base", ["hook.serverForced"])
        self.assertIsNone(got[0]["p"])
        self.assertIsNone(got[0]["ratio"])


if __name__ == "__main__":
    unittest.main()
