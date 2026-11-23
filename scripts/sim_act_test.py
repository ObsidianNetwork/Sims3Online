"""sim_act.py's "NOT running" line: python -m unittest scripts.sim_act_test"""
from __future__ import annotations

import contextlib
import io
import sys
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import sim_act  # noqa: E402

BODY = "-- DIAG_RUN --\n  ok = true\n  actorId = 7\n  picked = SocialInteractionA\n"


class NotRunning(unittest.TestCase):
    """2026-10-31: the whole stack read Pause and the line blamed the queue ("add --clear, and --hold"). A tick that did
    not move over the wait says the game is paused (the probe, the queue and DIAG_STATE mocked: no game reached)."""

    def run_act(self, ticks: list[str | None]) -> str:
        out = io.StringIO()
        with mock.patch.object(sim_act, "run_probe", return_value=BODY),                 mock.patch.object(sim_act, "queue_head", return_value=("SocialInteractionA", "queued")),                 mock.patch.object(sim_act, "game_tick", side_effect=ticks), mock.patch.object(sim_act.time, "sleep"),                 contextlib.redirect_stdout(out):
            self.assertEqual(sim_act.main(["Pablo", "sim:Jennifer", "Chat", "--wait", "0.001"]), 2)
        return out.getvalue()

    def test_a_still_tick_says_the_game_is_paused(self) -> None:
        text = self.run_act(["500", "500"])
        self.assertIn("the game is paused (tick did not move, 500)", text)
        self.assertNotIn("still behind", text)

    def test_a_moving_tick_still_blames_the_queue(self) -> None:
        self.assertIn("still behind the current interaction", self.run_act(["500", "530"]))

    def test_no_tick_read_keeps_the_old_hints(self) -> None:
        self.assertIn("still behind the current interaction", self.run_act([None]))
        self.assertIn("the pick left the queue", sim_act.not_running_hint("empty", "5", "6"))


if __name__ == "__main__":
    unittest.main()
