"""python -m unittest scripts.run_watchdog_test (offline: fake bridges, processes and clock)."""
from __future__ import annotations

import os
import sys
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import run_watchdog as rw  # noqa: E402

GAMES = {"server": ("server", "127.0.0.1:34029"), "client 1": ("client", "127.0.0.1:34030")}


def state(tick: int, clock: int, speed: str = "Normal") -> dict[str, str]:
    return {"role": "x", "inWorld": "True", "tick": str(tick), "clockTicks": str(clock), "observedSpeed": speed}


class Stack:
    """Each game's DIAG_STATE replies in order (None: no answer) and process states; a clock that moves 30 s a read."""

    def __init__(self, replies: dict[str, list], alive: dict[str, list] | None = None) -> None:
        self.replies = {k: list(v) for k, v in replies.items()}
        self.alive_states = {k: list(v) for k, v in (alive or {}).items()}
        self.now = 1_760_000_000.0

    def probe(self, endpoint: str):
        queue = self.replies[endpoint]
        reply = queue.pop(0) if len(queue) > 1 else queue[0]
        return (reply, "") if reply is not None else (None, "no answer to DIAG_STATE in 10 s (SystemExit)")

    def alive(self, side: str):
        queue = self.alive_states.get(side) or [True]
        value = queue.pop(0) if len(queue) > 1 else queue[0]
        return (value, "TS3W 4242 alive") if value else (value, "client.state.json gamePid 4242: not running")

    def clock(self) -> float:
        self.now += 30.0
        return self.now

    def dog(self, fired: list | None = None) -> rw.Watchdog:
        return rw.Watchdog(GAMES, on_fire=(fired.append if fired is not None else None), probe=self.probe,
                           alive=self.alive, clock=self.clock, every=0, confirm=0)


SERVER_OK = [state(100, 10), state(200, 20), state(300, 30), state(400, 40), state(500, 50), state(600, 60)]


class WatchdogTest(unittest.TestCase):
    def test_a_live_process_whose_bridge_stops_answering_fires_with_the_time_since_its_last_answer(self) -> None:
        """Main, 2026-10-22: the three client bridges timed out on DIAG_STATE with their TS3W processes alive."""
        stack = Stack({"127.0.0.1:34029": SERVER_OK, "127.0.0.1:34030": [state(100, 10), None, None]})
        dog = stack.dog()
        self.assertIsNone(dog.check())  # the baseline: both answer
        found = dog.check()
        self.assertTrue(found.startswith("client 1 bridge: no answer to DIAG_STATE in 10 s (SystemExit); no answer for "))
        self.assertRegex(found, r"no answer for (60|90) s \(last DIAG_STATE answered \d\d:\d\d:\d\d\); TS3W 4242 alive$")

    def test_one_miss_then_an_answer_is_not_a_finding(self) -> None:
        stack = Stack({"127.0.0.1:34029": SERVER_OK, "127.0.0.1:34030": [state(100, 10), None, state(300, 30), state(400, 40)]})
        dog = stack.dog()
        self.assertIsNone(dog.check())
        self.assertIsNone(dog.check())  # missed once, answered 30 s later
        self.assertEqual(dog.base["client 1"][1]["tick"], "300")  # the answer is the new baseline

    def test_a_game_gone_fires(self) -> None:
        stack = Stack({"127.0.0.1:34029": SERVER_OK, "127.0.0.1:34030": [state(100, 10)]},
                      alive={"client": [True, True, False, False]})
        dog = stack.dog()
        self.assertIsNone(dog.check())
        self.assertEqual(dog.check(), "client 1: game gone (client.state.json gamePid 4242: not running)")

    def test_a_frozen_simulator_fires_unless_the_game_is_paused(self) -> None:
        stack = Stack({"127.0.0.1:34029": [state(100, 10), state(100, 10)], "127.0.0.1:34030": [state(1, 1), state(2, 2)]})
        dog = stack.dog()
        self.assertIsNone(dog.check())
        self.assertRegex(dog.check(), r"^server: heartbeat frozen while unpaused \(simulator tick 100 since ")
        paused = Stack({"127.0.0.1:34029": [state(100, 10, "Pause")], "127.0.0.1:34030": [state(1, 1), state(2, 2)]})
        dog = paused.dog()
        self.assertIsNone(dog.check())
        self.assertIsNone(dog.check())  # rejoin:paused holds the server still on purpose

    def test_a_still_clock_with_the_simulator_ticking_fires(self) -> None:
        stack = Stack({"127.0.0.1:34029": [state(100, 10), state(200, 10), state(300, 10)],
                       "127.0.0.1:34030": [state(1, 1), state(2, 2), state(3, 3)]})
        dog = stack.dog()
        self.assertIsNone(dog.check())
        self.assertRegex(dog.check(), r"^server: game clock stalled while unpaused \(clockTicks 10 since .*the simulator ticking\)$")

    def test_a_client_s_still_clock_fires_only_behind_the_flag(self) -> None:
        """Main, 2026-10-22: the server's clock only, until a quiet client has been watched; its heartbeat still counts."""
        def client_still() -> Stack:
            return Stack({"127.0.0.1:34029": SERVER_OK, "127.0.0.1:34030": [state(100, 10), state(200, 10), state(300, 10)]})

        dog = client_still().dog()
        self.assertFalse(dog.client_clock)
        self.assertIsNone(dog.check())
        self.assertIsNone(dog.check())  # the client's simulator ticks: no finding
        stack = client_still()
        flagged = rw.Watchdog(GAMES, probe=stack.probe, alive=stack.alive, clock=stack.clock, every=0, confirm=0,
                              client_clock=True)
        self.assertIsNone(flagged.check())
        self.assertRegex(flagged.check(), r"^client 1: game clock stalled while unpaused \(clockTicks 10 since ")
        with mock.patch.dict(os.environ, {"SIMSONLINE_WATCHDOG_CLIENT_CLOCK": "1"}):
            self.assertTrue(client_still().dog().client_clock)
        frozen = Stack({"127.0.0.1:34029": SERVER_OK, "127.0.0.1:34030": [state(100, 10), state(100, 10)]})
        dog = frozen.dog()
        self.assertIsNone(dog.check())
        self.assertRegex(dog.check(), r"^client 1: heartbeat frozen while unpaused")  # not behind the flag

    def test_a_held_client_is_skipped_and_starts_again_from_a_fresh_baseline(self) -> None:
        stack = Stack({"127.0.0.1:34029": SERVER_OK, "127.0.0.1:34030": [state(100, 10), None, None, state(5, 1), state(6, 2)]})
        dog = stack.dog()
        self.assertIsNone(dog.check())
        with dog.hold("127.0.0.1:34030"):
            self.assertIsNone(dog.check())  # a rejoin restarts it: not read at all
            self.assertEqual(stack.replies["127.0.0.1:34030"][0], None)
        self.assertNotIn("client 1", dog.base)
        stack.replies["127.0.0.1:34030"] = [state(5, 1), state(6, 2)]
        self.assertIsNone(dog.check())  # the restarted game's lower tick is a new baseline, not a frozen one
        self.assertIsNone(dog.check())

    def test_the_thread_fires_once_and_stops(self) -> None:
        stack = Stack({"127.0.0.1:34029": SERVER_OK, "127.0.0.1:34030": [state(100, 10), None]})
        fired: list[str] = []
        dog = stack.dog(fired)
        dog.start()
        dog.join(timeout=5)
        self.assertFalse(dog.is_alive())
        self.assertEqual(len(fired), 1)
        self.assertEqual(dog.found, fired[0])
        quiet = Stack({"127.0.0.1:34029": SERVER_OK, "127.0.0.1:34030": [state(i, i) for i in range(1, 400)]})
        dog = rw.Watchdog(GAMES, probe=quiet.probe, alive=quiet.alive, clock=quiet.clock, every=0.01, confirm=0)
        dog.start()
        dog.stop()
        self.assertFalse(dog.is_alive())
        self.assertIsNone(dog.found)

    def test_a_check_that_raises_is_noted_and_the_watch_goes_on(self) -> None:
        stack = Stack({"127.0.0.1:34029": SERVER_OK, "127.0.0.1:34030": [state(100, 10), None]})
        calls = {"n": 0}

        def alive(side: str):
            calls["n"] += 1
            if calls["n"] == 1:
                raise ValueError("client.state.json half written")
            return stack.alive(side)

        dog = rw.Watchdog(GAMES, probe=stack.probe, alive=alive, clock=stack.clock, every=0, confirm=0)
        dog.start()
        dog.join(timeout=5)
        self.assertEqual(dog.errors, ["ValueError: client.state.json half written"])
        self.assertIsNotNone(dog.found)  # it kept checking and found the silent bridge

    def test_parse_state_and_the_probe_s_refusals(self) -> None:
        fields = rw.parse_state("role=server;inWorld=True;tick=109029;observedSpeed=Normal;clockTicks=307809")
        self.assertEqual((fields["tick"], fields["observedSpeed"], fields["clockTicks"]), ("109029", "Normal", "307809"))
        import bridgectl
        with mock.patch.object(bridgectl, "connect", side_effect=SystemExit("connect to bridgectl listener x failed")):
            self.assertEqual(rw.probe_state("127.0.0.1:1")[0], None)
            self.assertIn("listener refused", rw.probe_state("127.0.0.1:1")[1])
        sock = mock.Mock()
        with mock.patch.object(bridgectl, "connect", return_value=sock), \
                mock.patch.object(bridgectl, "roundtrip", side_effect=SystemExit("timed out waiting")):
            self.assertIn("no answer to DIAG_STATE in 10 s", rw.probe_state("127.0.0.1:1")[1])
        sock.close.assert_called()
        with mock.patch.object(bridgectl, "connect", return_value=sock), \
                mock.patch.object(bridgectl, "roundtrip", return_value={"report": "role=server;tick=5;clockTicks=7"}):
            self.assertEqual(rw.probe_state("127.0.0.1:1"), ({"role": "server", "tick": "5", "clockTicks": "7"}, ""))

    def test_by_hand_it_refuses_inside_another_session_s_claim(self) -> None:
        import stack_claim
        with mock.patch.object(stack_claim, "holder", return_value="30a1c8d1 since 06:16"), \
                mock.patch.object(rw, "Watchdog") as made, mock.patch("sys.stderr"):
            self.assertEqual(rw.main(["--seconds", "1"]), 3)
        made.assert_not_called()

    def test_stack_games_names_each_player_and_its_side(self) -> None:
        with mock.patch.dict(os.environ, {"SIMSONLINE_CLIENT2_BRIDGECTL": "", "SIMSONLINE_CLIENT3_BRIDGECTL": ""}):
            games = rw.stack_games("127.0.0.1:34029", ["127.0.0.1:34030", "127.0.0.1:34031", "127.0.0.1:34031", "10.0.0.9:1"])
        self.assertEqual(games, {"server": ("server", "127.0.0.1:34029"), "client 1": ("client", "127.0.0.1:34030"),
                                 "client 2": ("client2", "127.0.0.1:34031"), "client 10.0.0.9:1": ("", "10.0.0.9:1")})


if __name__ == "__main__":
    unittest.main()
