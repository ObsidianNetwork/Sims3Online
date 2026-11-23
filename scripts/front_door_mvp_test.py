import copy
import unittest
from pathlib import Path
import tempfile

try:
    from .front_door_mvp import client_commands, client_game_evidence, evaluate, fields, stream_rows
except ImportError:
    from front_door_mvp import client_commands, client_game_evidence, evaluate, fields, stream_rows


class FrontDoorMvpTests(unittest.TestCase):
    def test_cold_client_requires_matching_ready_runtime_receipt(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "client state.json"
            path.write_text('{"pid":11,"gamePid":22,"phase":"ready"}', encoding="utf-8")
            command = f'client.exe --state-file "{path}"'
            self.assertEqual(client_game_evidence(command, 11, 22)["launch"], "cold")
            for pid, game in [(12, 22), (11, 23)]:
                with self.assertRaises(ValueError):
                    client_game_evidence(command, pid, game)
            path.write_text('{"pid":11,"gamePid":22,"phase":"world-received"}', encoding="utf-8")
            with self.assertRaises(ValueError):
                client_game_evidence(command, 11, 22)

    def test_explicit_warm_attach_must_match_the_selected_game(self):
        self.assertEqual(client_game_evidence('client --client-attach-pid 22', 11, 22)["launch"], "warm_attach")
        with self.assertRaises(ValueError):
            client_game_evidence('client --client-attach-pid 23', 11, 22)

    def setUp(self):
        self.processes = {"clients": [{"player": 1, "remote": "127.0.0.1:10001"},
                                      {"player": 2, "remote": "127.0.0.1:10002"}]}
        self.before = {"stream": "client|1|remote=127.0.0.1:10001|last_seq=10|drops=0\n"
                                 "client|2|remote=127.0.0.1:10002|last_seq=20|drops=0\n"
                                 "totals|joins_completed=2",
                       "games": [{"inWorld": "True", "tick": "100"} for _ in range(3)]}
        self.after = copy.deepcopy(self.before)
        self.after["stream"] = self.after["stream"].replace("last_seq=10", "last_seq=11").replace("last_seq=20", "last_seq=21")
        for game in self.after["games"]:
            game["tick"] = "101"

    def test_two_matching_live_clients_and_three_advancing_games_are_ready(self):
        self.assertEqual(evaluate(self.before, self.after, self.processes), [])

    def test_line_clients_cannot_substitute_for_the_selected_controller_socket(self):
        self.processes["clients"][1]["remote"] = "127.0.0.1:99999"
        self.assertTrue(any("socket" in error for error in evaluate(self.before, self.after, self.processes)))

    def test_missing_second_session_never_passes(self):
        self.after["stream"] = self.after["stream"].splitlines()[0]
        self.assertTrue(evaluate(self.before, self.after, self.processes))

    def test_held_live_fallback_cannot_substitute_for_complete_participant_history(self):
        self.after["stream"] += "|presentation_history_pending=1|presentation_history_failures=1"
        self.assertIn("participant presentation history is still pending", evaluate(self.before, self.after, self.processes))

    def test_each_game_must_advance_and_be_in_world(self):
        for side in range(3):
            after = copy.deepcopy(self.after)
            after["games"][side] = {"inWorld": "False", "tick": "100"}
            errors = evaluate(self.before, after, self.processes)
            self.assertTrue(any("not in world" in error for error in errors))
            self.assertTrue(any("heartbeat" in error for error in errors))

    def test_stalled_stream_or_any_drop_fails(self):
        self.after["stream"] = self.after["stream"].replace("last_seq=21", "last_seq=20").replace("drops=0", "drops=1")
        errors = evaluate(self.before, self.after, self.processes)
        self.assertTrue(any("sequence" in error for error in errors))
        self.assertTrue(any("drop" in error for error in errors))

    def test_duplicate_player_cannot_count_as_two(self):
        self.processes["clients"][1] = self.processes["clients"][0]
        self.assertIn("client players are not distinct", evaluate(self.before, self.after, self.processes))

    def test_parsers_keep_only_real_client_rows_and_named_state_fields(self):
        self.assertEqual(set(stream_rows(self.before["stream"])), {1, 2})
        self.assertEqual(fields("role=client; inWorld=True; tick=123"),
                         {"role": "client", "inWorld": "True", "tick": "123"})

    def test_command_capture_keeps_identity_without_animation_or_save_payloads(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "client.log"
            path.write_text('{"kind":"intent_forwarded","payload":{"commandId":"abc","actor":42,"key":"Terrain.GoHere"}}\n'
                            '{"kind":"received","payload":"{\\"$type\\":\\"command_accepted\\",\\"commandId\\":\\"abc\\"}"}\n'
                            '{"kind":"received","payload":"{\\"$type\\":\\"sim_animation\\",\\"lines\\":[\\"private\\"]}"}\n', encoding="utf-8")
            captured = client_commands(path)
            self.assertFalse(captured["truncated"])
            self.assertEqual([row["payload"]["commandId"] for row in captured["receipts"]], ["abc", "abc"])
            self.assertNotIn("private", str(captured))

    def test_native_interaction_failure_is_retained(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "client.log"
            path.write_text('{"kind":"received","payload":{"$type":"interaction_failed",'
                            '"actorObjectId":42,"interactionId":7,"reason":"native route failed"}}\n', encoding="utf-8")
            captured = client_commands(path)["receipts"]
            self.assertEqual(captured[0]["kind"], "interaction_failed")
            self.assertEqual(captured[0]["payload"], {"actorObjectId": 42, "interactionId": 7, "reason": "native route failed"})

    def test_text_logs_and_action_byte_offset_exclude_earlier_commands(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "client.log"
            path.write_text('[2026-11-13 12:00:00.000Z] intent_forwarded: {"commandId":"old","actor":42}\n', encoding="utf-8")
            start = path.stat().st_size
            with path.open("a", encoding="utf-8") as output:
                output.write('[2026-11-13 12:00:01.000Z] intent_forwarded: {"commandId":"new","actor":42}\n')
                output.write('[2026-11-13 12:00:02.000Z] received: "{\\"$type\\":\\"command_accepted\\",\\"commandId\\":\\"new\\"}"\n')
            captured = client_commands(path, start=start)
            self.assertEqual([row["payload"]["commandId"] for row in captured["receipts"]], ["new", "new"])


if __name__ == "__main__":
    unittest.main()
