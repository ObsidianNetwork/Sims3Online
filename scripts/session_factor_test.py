"""Unit tests for session_factor: python -m unittest scripts.session_factor_test"""
from __future__ import annotations

import io
import sys
import unittest
from contextlib import redirect_stdout
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import session_factor as sf  # noqa: E402


class Factor(unittest.TestCase):
    def test_the_arm_name_is_the_factor(self) -> None:
        self.assertEqual(sf.factor("warm"), ("warm", 2))  # the 024712 session's two warm rejoins
        self.assertEqual(sf.factor("warm3"), ("warm", 3))
        self.assertEqual(sf.factor("reload"), ("reload", 1))
        self.assertIsNone(sf.factor("base"))
        self.assertIsNone(sf.factor("warmish"))

    def test_each_time_runs_the_factor_and_a_failure_stops_it(self) -> None:
        calls: list[list[str]] = []
        out = io.StringIO()
        with redirect_stdout(out):
            code = sf.main(["warm"], runner=lambda c: (calls.append(c), (0, "rejoin:warm: claimed in 13s"))[1])
        self.assertEqual((code, len(calls)), (0, 2))
        self.assertEqual(calls[0], ["scripts/quick_loop.py", "run", "--step", "rejoin:warm"])
        self.assertTrue(out.getvalue().strip().endswith("factor=warm factor_done=2"))
        replies = iter([(0, "ok"), (3, "rejoin:warm: the client never claimed again")])
        with redirect_stdout(io.StringIO()) as failed:
            self.assertEqual(sf.main(["warm"], runner=lambda c: next(replies)), 3)
        self.assertIn("factor_done=1", failed.getvalue())

    def test_base_runs_nothing(self) -> None:
        with redirect_stdout(io.StringIO()):
            self.assertEqual(sf.main(["base"], runner=lambda c: self.fail("ran")), 0)


if __name__ == "__main__":
    unittest.main()
