"""Unit tests for tune.py's value check (no bridge): python -m unittest scripts.tune_test"""
from __future__ import annotations

import sys
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import tune  # noqa: E402



class Float32Test(unittest.TestCase):
    def test_a_hash_that_float32_would_round_is_refused(self) -> None:
        self.assertIn("1176358400", tune.float32_problem("1176358403") or "")
        self.assertEqual(tune.main(["sacs.steer.16f06ed7", "1176358403"]), 2)  # refused before any bridge call

    def test_fractions_small_integers_and_names_pass(self) -> None:
        for value in ("0.1", "0.05", "5", "16777216", "-3", "on"):
            self.assertIsNone(tune.float32_problem(value), value)


class SidesTest(unittest.TestCase):
    def test_a_dead_server_does_not_stop_the_client(self) -> None:
        def fake(endpoint: str, name: str | None = None, value: str | None = None) -> str:
            if endpoint == tune.ENDPOINTS["server"]:
                raise failure
            return f"{name}={value} (default 0)"

        # bridgectl.connect exits on a refused connection; a reset or timeout mid-reply raises an OSError
        for failure in (SystemExit("connect to bridgectl listener 127.0.0.1:34029 failed: refused\n(is the server up"),
                        TimeoutError("timed out")):
            with mock.patch.object(tune, "tune", side_effect=fake) as calls, mock.patch("builtins.print") as out:
                self.assertEqual(tune.main(["--side", "both", "sacs.node-order", "0"]), 1)
            self.assertEqual([c.args[0] for c in calls.call_args_list], [tune.ENDPOINTS["server"], tune.ENDPOINTS["client"]])
            printed = [str(c.args[0]) for c in out.call_args_list]
            self.assertTrue(printed[0].startswith("[server] error=not answering ("), printed[0])
            self.assertNotIn("\n", printed[0])
            self.assertEqual(printed[1], "[client] sacs.node-order=0 (default 0)")

    def test_clients_sets_both_players_games(self) -> None:
        with mock.patch.object(tune, "tune", return_value="walk.start-lead-ms=40 (default 0)") as calls, \
                mock.patch("builtins.print") as out:
            self.assertEqual(tune.main(["--side", "clients", "walk.start-lead-ms", "40"]), 0)
        self.assertEqual([c.args[0] for c in calls.call_args_list], [tune.ENDPOINTS["client1"], tune.ENDPOINTS["client2"]])
        self.assertEqual([str(c.args[0]).split(" ")[0] for c in out.call_args_list], ["[client1]", "[client2]"])

if __name__ == "__main__":
    unittest.main()
