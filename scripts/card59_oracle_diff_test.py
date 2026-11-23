"""Tests for the oracle's evidence and scorecard boundaries, without TS3."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location("card59_oracle_diff", Path(__file__).with_name("card59_oracle_diff.py"))
assert SPEC and SPEC.loader
HARNESS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HARNESS)


class Card59OracleDiffTests(unittest.TestCase):
    def valid(self):
        return {key: 1 for key in HARNESS.SCORE_KEYS} | {
            "joins": 2, "lot_modes_delivered": 2, "lot_modes_required": 2,
            "tick_drops_during_join": 0, "keep_failures_that_failed_a_save": 0, "save_bytes_written_by_rust": 0,
        }

    def encode(self, values):
        return "|".join(f"{key}={value}" for key, value in values.items())

    def test_normative_invariants_are_checked_individually(self):
        HARNESS.validate_scorecard(self.encode(self.valid()))
        for key, bad in (
            ("bytes_fetched", 2), ("tick_drops_during_join", 1),
            ("presentation_sent_gap_max_during_join", 0), ("keep_failures_that_failed_a_save", 1),
            ("lot_modes_delivered", 1), ("save_bytes_written_by_rust", 1),
            ("reuse_hits", 0), ("reuse_misses", 0), ("resume_ok", 0), ("resume_fresh", 0), ("joins", 1),
        ):
            with self.subTest(key=key), self.assertRaises(ValueError):
                HARNESS.validate_scorecard(self.encode(self.valid() | {key: bad}))

    def test_missing_nonnumeric_negative_and_duplicate_metrics_fail_closed(self):
        values = self.valid()
        del values["bytes_oracle"]
        for text in (self.encode(values), self.encode(self.valid() | {"resume_ok": "unmeasured"}),
                     self.encode(self.valid() | {"keeps": -1}), self.encode(self.valid()) + "|joins=2"):
            with self.subTest(text=text), self.assertRaises(ValueError):
                HARNESS.validate_scorecard(text)

    def test_oracle_output_requires_exact_case_coverage_and_order(self):
        output = "build chatter\nCARD59 " + json.dumps({"id": "one", "result": {"reuse": True}}) + "\n"
        self.assertEqual(len(HARNESS.parse_oracle(output, ["one"])), 1)
        for expected in ([], ["one", "two"], ["two", "one"]):
            with self.subTest(expected=expected), self.assertRaises(ValueError):
                HARNESS.parse_oracle(output, expected)
        with self.assertRaises(ValueError):
            HARNESS.parse_oracle(output + output, ["one"])

    def test_synthetic_fixtures_cover_boundaries_and_leave_player_save_unmarked(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            rows = HARNESS.fixture_cases(directory)
            self.assertEqual(len({row["id"] for row in rows}), len(rows))
            world = next(row for row in rows if row["op"] == "card59_chunks")
            sizes = sorted(path.stat().st_size for path in Path(world["folder"]).iterdir())
            self.assertEqual(sizes, [0, 1, 262144, 262145])
            own = directory / "fixtures/client/Player.sims3"
            self.assertTrue((own / "own.nhd").is_file())
            self.assertFalse((own / "simsonline-world.txt").exists())
            self.assertEqual(HARNESS.kernel_fixture()[:4], b"DBPF")


if __name__ == "__main__":
    unittest.main()
