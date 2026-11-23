"""Public line tooling contracts, exercised over real loopback sockets."""
import base64
import json
import socket
import threading
import unittest
from contextlib import contextmanager
from pathlib import Path
import re
from unittest.mock import patch

from scripts import simctl


@contextmanager
def peer(handler):
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen()
    listener.settimeout(5)
    errors = []

    def serve():
        try:
            with listener.accept()[0] as sock, sock.makefile("rwb") as stream:
                sock.settimeout(5)
                handler(stream)
        except Exception as exc:
            errors.append(exc)

    worker = threading.Thread(target=serve, daemon=True)
    worker.start()
    try:
        yield f"127.0.0.1:{listener.getsockname()[1]}"
    finally:
        listener.close()
        worker.join(6)
        if worker.is_alive():
            raise AssertionError("mock peer did not finish")
        if errors:
            raise errors[0]


def write(stream, line):
    stream.write((line + "\n").encode())
    stream.flush()


def hello(stream):
    line = stream.readline().decode().strip()
    assert line == "HELLO|1|ring=1,wfs=1,role=client", line
    write(stream, "WELCOME|1|42|")


class LineConnectionTests(unittest.TestCase):
    def test_default_json_bytes_and_no_join(self):
        def server(stream):
            command = json.loads(stream.readline())
            self.assertEqual(command["$type"], "claim_sim")
            write(stream, json.dumps({"$type": "command_accepted", "commandId": command["commandId"]}))
        with peer(server) as endpoint:
            conn = simctl.Conn(endpoint, 2)
            try:
                self.assertEqual(conn.command("claim_sim", playerId=7, simObjectId=2), (True, None, None))
            finally:
                conn.close()

    def test_public_joins_then_ack_preserves_command_identity(self):
        def server(stream):
            hello(stream)
            join = stream.readline().decode().strip().split("|")
            self.assertEqual(join[:5], ["CMD", "1", "JoinSession", "1", "7"])
            write(stream, "ACK|1|JoinSession")
            command = stream.readline().decode().strip()
            self.assertEqual(command, "CMD|2|ClaimSim|2|7|18446744073709551614")
            write(stream, "ERR|2|SlotAlreadyReserved|ClaimSim|")
        with peer(server) as endpoint:
            conn = simctl.Conn(endpoint, 2, wire="line", player_id=7)
            try:
                self.assertEqual(conn.command("claim_sim", playerId=7, simObjectId=18446744073709551614),
                                 (False, "SlotAlreadyReserved", None))
            finally:
                conn.close()

    def test_admin_does_not_join_and_does_not_wait_for_broadcast(self):
        def server(stream):
            hello(stream)
            self.assertEqual(stream.readline().decode().strip(), "CMD|1|SetGameSpeed|1|9|Normal")
            write(stream, "ACK|1|SetGameSpeed")
        with peer(server) as endpoint:
            conn = simctl.Conn(endpoint, 2, wire="line", player_id=9, admin=True)
            try:
                self.assertEqual(conn.command("set_game_speed", wait_for="game_speed_changed",
                                              playerId=9, speed="Normal"), (True, None, None))
            finally:
                conn.close()

    def test_rejection_companion_does_not_fail_the_next_command(self):
        for bulk in (False, True):
            with self.subTest(bulk=bulk):
                def server(stream):
                    hello(stream)
                    self.assertEqual(stream.readline().decode().strip(), "CMD|1|SetGameSpeed|1|7|Normal")
                    detail = "driver failed".encode().hex()
                    write(stream, "EV|1|5|1|CommandRejected|1|" + detail)
                    write(stream, "EV|2|5|1|ServerError|" + ("b1" if bulk else detail))
                    if bulk:
                        write(stream, "BULK|1|0|1|" + base64.b64encode(detail.encode()).decode())
                    self.assertEqual(stream.readline().decode().strip(), "CMD|2|SetGameSpeed|2|7|Pause")
                    write(stream, "ACK|2|SetGameSpeed")
                with peer(server) as endpoint:
                    conn = simctl.Conn(endpoint, 2, wire="line", player_id=7, join=False)
                    try:
                        self.assertEqual(conn.command("set_game_speed", playerId=7, speed="Normal"),
                                         (False, "driver failed", None))
                        self.assertEqual(conn.command("set_game_speed", playerId=7, speed="Pause"),
                                         (True, None, None))
                    finally:
                        conn.close()

    def test_server_errors_after_the_companion_remain_independent_failures(self):
        for corr in (0, 1, 2, 99):
            with self.subTest(corr=corr):
                def server(stream):
                    hello(stream)
                    stream.readline()
                    write(stream, "EV|1|5|1|CommandRejected|1|" + "driver failed".encode().hex())
                    write(stream, "EV|1|5|1|ServerError|" + "driver failed".encode().hex())
                    stream.readline()
                    write(stream, f"EV|2|5|{corr}|ServerError|" + "independent error".encode().hex())
                with peer(server) as endpoint:
                    conn = simctl.Conn(endpoint, 2, wire="line", player_id=7, join=False)
                    try:
                        self.assertEqual(conn.command("set_game_speed", playerId=7, speed="Normal"),
                                         (False, "driver failed", None))
                        self.assertEqual(conn.command("set_game_speed", playerId=7, speed="Pause"),
                                         (False, "independent error", None))
                    finally:
                        conn.close()

    def test_unmatched_server_error_before_companion_is_not_suppressed(self):
        def server(stream):
            hello(stream)
            stream.readline()
            write(stream, "EV|1|5|1|CommandRejected|1|" + "driver failed".encode().hex())
            stream.readline()
            write(stream, "EV|1|5|99|ServerError|" + "independent error".encode().hex())
            write(stream, "EV|2|5|1|ServerError|" + "driver failed".encode().hex())
            stream.readline()
            write(stream, "ACK|3|SetGameSpeed")
        with peer(server) as endpoint:
            conn = simctl.Conn(endpoint, 2, wire="line", player_id=7, join=False)
            try:
                self.assertEqual(conn.command("set_game_speed", playerId=7, speed="Normal"),
                                 (False, "driver failed", None))
                self.assertEqual(conn.command("set_game_speed", playerId=7, speed="Pause"),
                                 (False, "independent error", None))
                self.assertEqual(conn.command("set_game_speed", playerId=7, speed="Normal"),
                                 (True, None, None))
            finally:
                conn.close()

    def test_cached_retry_does_not_arm_a_second_companion(self):
        detail = "driver failed".encode().hex()
        rejection = "EV|1|5|1|CommandRejected|1|" + detail
        def server(stream):
            hello(stream)
            first = stream.readline()
            write(stream, rejection)
            write(stream, "EV|2|5|1|ServerError|" + detail)
            stream.readline()
            write(stream, "ACK|2|SetGameSpeed")
            self.assertEqual(stream.readline(), first, "A retry reuses the exact command bytes.")
            write(stream, rejection)
            stream.readline()
            write(stream, "EV|3|5|1|ServerError|" + detail + "\nACK|3|SetGameSpeed")
        with peer(server) as endpoint:
            conn = simctl.Conn(endpoint, 2, wire="line", player_id=7, join=False)
            try:
                fields = {"commandId": "fixed", "playerId": 7, "speed": "Normal"}
                conn.send("set_game_speed", **fields)
                self.assertEqual(conn.recv()["reason"], "driver failed")
                self.assertEqual(conn.command("set_game_speed", playerId=7, speed="Pause"), (True, None, None))
                conn.send("set_game_speed", **fields)
                self.assertEqual(conn.recv()["reason"], "driver failed")
                self.assertEqual(conn.command("set_game_speed", playerId=7, speed="Pause"),
                                 (False, "driver failed", None), "The cached reply has no new companion.")
            finally:
                conn.close()

    def test_different_reason_same_corr_does_not_consume_the_companion(self):
        def server(stream):
            hello(stream)
            stream.readline()
            write(stream, "EV|1|5|1|CommandRejected|1|" + "driver failed".encode().hex())
            stream.readline()
            write(stream, "EV|2|5|1|ServerError|" + "independent error".encode().hex())
            write(stream, "EV|3|5|1|ServerError|" + "driver failed".encode().hex())
            stream.readline()
            write(stream, "ACK|3|SetGameSpeed")
        with peer(server) as endpoint:
            conn = simctl.Conn(endpoint, 2, wire="line", player_id=7, join=False)
            try:
                self.assertEqual(conn.command("set_game_speed", playerId=7, speed="Normal"),
                                 (False, "driver failed", None))
                self.assertEqual(conn.command("set_game_speed", playerId=7, speed="Pause"),
                                 (False, "independent error", None))
                self.assertEqual(conn.command("set_game_speed", playerId=7, speed="Normal"), (True, None, None))
            finally:
                conn.close()

    def test_refusal_without_companion_preserves_a_later_identical_error(self):
        def server(stream):
            hello(stream)
            stream.readline()
            write(stream, "ERR|1|NotJoined|SetGameSpeed|")
            stream.readline()
            write(stream, "ACK|2|SetGameSpeed")
            stream.readline()
            write(stream, "EV|1|5|1|ServerError|" + "NotJoined".encode().hex() + "\nACK|3|SetGameSpeed")
        with peer(server) as endpoint:
            conn = simctl.Conn(endpoint, 2, wire="line", player_id=7, join=False)
            try:
                self.assertEqual(conn.command("set_game_speed", playerId=7, speed="Normal"),
                                 (False, "NotJoined", None))
                self.assertEqual(conn.command("set_game_speed", playerId=7, speed="Pause"), (True, None, None))
                self.assertEqual(conn.command("set_game_speed", playerId=7, speed="Normal"),
                                 (False, "NotJoined", None))
            finally:
                conn.close()

    def test_error_before_rejection_cannot_arm_a_late_companion(self):
        detail = "driver failed".encode().hex()
        def server(stream):
            hello(stream)
            stream.readline()
            write(stream, "EV|1|5|1|ServerError|" + detail)
            write(stream, "EV|2|5|1|CommandRejected|1|" + detail)
            stream.readline()
            write(stream, "ACK|2|SetGameSpeed")
            stream.readline()
            write(stream, "EV|3|5|1|ServerError|" + detail + "\nACK|3|SetGameSpeed")
        with peer(server) as endpoint:
            conn = simctl.Conn(endpoint, 2, wire="line", player_id=7, join=False)
            try:
                self.assertEqual(conn.command("set_game_speed", playerId=7, speed="Normal"),
                                 (False, "driver failed", None))
                self.assertEqual(conn.command("set_game_speed", playerId=7, speed="Pause"), (True, None, None))
                self.assertEqual(conn.command("set_game_speed", playerId=7, speed="Normal"),
                                 (False, "driver failed", None))
            finally:
                conn.close()

    def test_one_thousand_refusals_do_not_arm_companions(self):
        for checkpoint in (False, True):
            with self.subTest(checkpoint=checkpoint):
                def server(stream):
                    hello(stream)
                    for corr in range(1, 1001):
                        stream.readline()
                        if checkpoint:
                            write(stream, f"ACK|{corr}|SaveGame\nEV|{corr}|5|{corr}|CommandRejected|{corr}|"
                                  + "checkpoint failed".encode().hex())
                        else:
                            write(stream, f"ERR|{corr}|NotJoined|SetGameSpeed|")
                with peer(server) as endpoint:
                    conn = simctl.Conn(endpoint, 2, wire="line", player_id=7, join=False)
                    try:
                        for corr in range(1, 1001):
                            if checkpoint:
                                conn.send("save_game", commandId=str(corr), playerId=7, saveName="Test")
                                self.assertEqual(conn.recv()["$type"], "command_accepted")
                            else:
                                conn.send("set_game_speed", commandId=str(corr), playerId=7, speed="Normal")
                            self.assertEqual(conn.recv()["$type"], "command_rejected")
                        self.assertEqual(len(conn.rejected_corrs), 0,
                                         "Neither ERR nor an asynchronous checkpoint arms pairing.")
                    finally:
                        conn.close()

    def test_missing_driver_companions_have_a_finite_state_bound(self):
        def server(stream):
            hello(stream)
            for corr in range(1, 1001):
                stream.readline()
                write(stream, f"EV|{corr}|5|{corr}|CommandRejected|{corr}|"
                      + "driver failed".encode().hex())
        with peer(server) as endpoint:
            conn = simctl.Conn(endpoint, 2, wire="line", player_id=7, join=False)
            try:
                for corr in range(1, 1001):
                    conn.send("set_game_speed", commandId=str(corr), playerId=7, speed="Normal")
                    self.assertEqual(conn.recv()["$type"], "command_rejected")
                self.assertLessEqual(len(conn.rejected_corrs), 32)
            finally:
                conn.close()

    def test_eof_clears_pending_companions(self):
        def server(stream):
            hello(stream)
            stream.readline()
            write(stream, "EV|1|5|1|CommandRejected|1|" + "driver failed".encode().hex())
        with peer(server) as endpoint:
            conn = simctl.Conn(endpoint, 2, wire="line", player_id=7, join=False)
            try:
                conn.send("set_game_speed", commandId="fixed", playerId=7, speed="Normal")
                self.assertEqual(conn.recv()["$type"], "command_rejected")
                self.assertTrue(conn.rejected_corrs)
                self.assertIsNone(conn.recv())
                self.assertFalse(conn.rejected_corrs)
            finally:
                conn.close()

    def test_close_clears_pending_companions(self):
        def server(stream):
            hello(stream)
            stream.readline()
            write(stream, "EV|1|5|1|CommandRejected|1|" + "driver failed".encode().hex())
            self.assertEqual(stream.readline(), b"")
        with peer(server) as endpoint:
            conn = simctl.Conn(endpoint, 2, wire="line", player_id=7, join=False)
            try:
                conn.send("set_game_speed", commandId="fixed", playerId=7, speed="Normal")
                self.assertEqual(conn.recv()["$type"], "command_rejected")
                self.assertTrue(conn.rejected_corrs)
            finally:
                conn.close()
            self.assertFalse(conn.rejected_corrs)

    def test_snapshot_multichunk_and_interleaved_event(self):
        snapshot = "(5;576f726c64;53617665;Normal;[];[])"
        def server(stream):
            hello(stream)
            stream.readline()
            write(stream, "ACK|1|JoinSession")
            stream.readline()
            write(stream, "ACK|2|RequestSnapshot")
            write(stream, "EV|1|5|2|FullSnapshot|b1")
            write(stream, "BULK|1|0|2|" + base64.b64encode(snapshot[:15].encode()).decode())
            write(stream, "EV|2|5|0|SimAnimation|[517c527c31]")
            write(stream, "BULK|1|1|2|" + base64.b64encode(snapshot[15:].encode()).decode())
        with peer(server) as endpoint:
            conn = simctl.Conn(endpoint, 2, wire="line", player_id=7)
            try:
                ok, _, extra = conn.command("request_snapshot", wait_for="full_snapshot", playerId=7, sinceTick=None)
                self.assertTrue(ok)
                self.assertEqual(extra["snapshot"]["tick"], 5)
                self.assertEqual(extra["snapshot"]["worldName"], "World")
                self.assertEqual(conn.recv(), {"$type": "sim_animation", "lines": ["Q|R|1"]})
            finally:
                conn.close()

    def test_malformed_bulk_is_named(self):
        receiver = simctl.LineReceiver()
        self.assertEqual(receiver.receive("EV|1|1|0|SimAnimation|b1"), [])
        with self.assertRaisesRegex(ValueError, "bulk_malformed"):
            receiver.receive("BULK|1|1|2|")

    def test_hello_rejection_is_not_a_timeout(self):
        def server(stream):
            stream.readline()
            write(stream, "ERR|0|hello_rejected|HELLO|4d697373696e6753696d6c696e6b52696e67")
        with peer(server) as endpoint:
            with self.assertRaisesRegex(ConnectionError, "MissingSimlinkRing"):
                simctl.Conn(endpoint, 2, wire="line", player_id=7)

    def test_schema_covers_all_registered_public_records(self):
        root = Path(__file__).resolve().parents[1] / "SimsOnline.Protocol"
        for file, table in [("ClientCommand.cs", simctl._COMMAND_FIELDS), ("ServerEvent.cs", simctl._EVENT_FIELDS)]:
            names = set(re.findall(r"\[JsonDerivedType\(typeof\((\w+)\)", (root / file).read_text(encoding="utf-8")))
            self.assertEqual(names, set(table))

    def test_admin_name_lookup_uses_public_read_without_superseding_player(self):
        def admin(stream):
            hello(stream)
            # No join or mutation on the admin socket during a name lookup.
            self.assertEqual(stream.readline(), b"")
        def public(stream):
            hello(stream)
            self.assertEqual(stream.readline().decode().strip(), "CMD|1|RequestSnapshot|1|9|~")
            write(stream, "ACK|1|RequestSnapshot")
            write(stream, "EV|1|5|1|FullSnapshot|(5;576f726c64;53617665;Normal;[];[])")
        with peer(admin) as admin_endpoint, peer(public) as public_endpoint:
            conn = simctl.Conn(admin_endpoint, 2, wire="line", player_id=9, admin=True)
            conn.public_endpoint = public_endpoint
            try:
                with patch.object(simctl, "save_cache"):
                    result = simctl.get_snapshot(conn, refresh=True, quiet=True)
                self.assertEqual(result["worldName"], "World")
            finally:
                conn.close()

    def test_raw_world_bulk_and_snapshot_keep_u64_values(self):
        receiver = simctl.LineReceiver()
        snapshot = "(7;57;53;Normal;[(18446744073709551614;416c696365;1;(1;2;3);~;9;~;~;~;~;~;~)];[])"
        message = receiver.receive("EV|1|7|0|FullSnapshot|" + snapshot)[0]
        self.assertEqual(message["snapshot"]["sims"][0]["objectId"], 18446744073709551614)
        self.assertEqual(message["snapshot"]["sims"][0]["name"], "Alice")
        self.assertEqual(receiver.receive("EV|2|7|0|WorldFileChunk|53|0|1|66|0|3|b1"), [])
        raw = bytes([0, 255, 17])
        result = receiver.receive("BULK|1|0|1|" + base64.b64encode(raw).decode())[0]
        self.assertEqual(base64.b64decode(result["data"]), raw)

    def test_command_retry_keeps_corr_and_id(self):
        def server(stream):
            hello(stream)
            first = stream.readline().decode().strip()
            second = stream.readline().decode().strip()
            self.assertEqual(first, second)
            self.assertEqual(first, "CMD|1|SaveGame|1|7|536176657c4e616d65")
        with peer(server) as endpoint:
            conn = simctl.Conn(endpoint, 2, wire="line", player_id=7, admin=True)
            try:
                fields = {"commandId": "fixed", "playerId": 7, "saveName": "Save|Name"}
                conn.send("save_game", **fields)
                conn.send("save_game", **fields)
            finally:
                conn.close()

    def test_bulk_byte_and_inflight_caps_are_transactional(self):
        receiver = simctl.LineReceiver(max_bulk_bytes=4, max_in_flight_bulks=1)
        receiver.receive("EV|1|0|0|SimAnimation|b1")
        with self.assertRaisesRegex(ValueError, "bulk_malformed"):
            receiver.receive("EV|2|0|0|SimAnimation|b2")
        self.assertEqual(set(receiver.bulks), {1}, "Rejected introduction must leave no partial state.")
        receiver.receive("BULK|1|0|2|" + base64.b64encode(b"123").decode())
        with self.assertRaisesRegex(ValueError, "bulk_malformed"):
            receiver.receive("BULK|1|1|2|" + base64.b64encode(b"45").decode())
        self.assertEqual(receiver.bulks, {}, "Bad bulk must free all of its owner's reservations.")
        self.assertEqual(receiver.bulk_bytes, 0)
        self.assertEqual(len(receiver.pending), 0)

    def test_combined_bulk_bytes_and_queued_events_are_bounded(self):
        receiver = simctl.LineReceiver(max_bulk_bytes=4, max_in_flight_bulks=2, max_pending_events=2)
        receiver.receive("EV|1|0|0|SimAnimation|b1")
        receiver.receive("EV|2|0|0|SimAnimation|b2")
        with self.assertRaisesRegex(ValueError, "bulk_malformed"):
            receiver.receive("EV|3|0|0|ServerError|6f6b")
        receiver.receive("BULK|1|0|2|" + base64.b64encode(b"123").decode())
        with self.assertRaisesRegex(ValueError, "bulk_malformed"):
            receiver.receive("BULK|2|0|2|" + base64.b64encode(b"45").decode())
        self.assertEqual(set(receiver.bulks), {1})
        self.assertEqual(receiver.bulk_bytes, 3)

    def test_line_cli_requires_explicit_public_player_before_connecting(self):
        import sys
        import io
        with patch.object(sys, "argv", ["simctl", "--wire", "line", "status"]), \
             patch.object(sys, "stderr", io.StringIO()), patch.object(simctl, "Conn") as connect:
            with self.assertRaises(SystemExit) as result:
                simctl.main()
            self.assertEqual(result.exception.code, 2)
            connect.assert_not_called()

    def test_completed_bulk_queued_behind_another_still_counts_against_budget(self):
        receiver = simctl.LineReceiver(max_bulk_bytes=3)
        receiver.receive("EV|1|0|0|SimAnimation|b1")
        receiver.receive("EV|2|0|0|SimAnimation|b2")
        receiver.receive("BULK|2|0|1|W10=")  # encoded empty list, held behind b1
        self.assertEqual(receiver.bulk_bytes, 2)
        receiver.receive("EV|3|0|0|SimAnimation|b3")
        with self.assertRaisesRegex(ValueError, "bulk_malformed"):
            receiver.receive("BULK|3|0|1|W10=")
        self.assertEqual(receiver.bulk_bytes, 2)


if __name__ == "__main__":
    unittest.main()
