"""Gate 6 must distinguish named refusal from silence on a real TCP connection."""
import tempfile
import unittest
from pathlib import Path

from scripts import rejection_relay_probe as probe
from scripts.simctl_test import hello, peer, write


class RejectionRelayTests(unittest.TestCase):
    def run_probe(self, drop=False):
        def server(stream):
            hello(stream)
            while line := stream.readline().decode().strip():
                fields = line.split("|")
                corr = fields[1] if fields[0] == "CMD" and len(fields) > 1 else "0"
                verb = fields[2] if len(fields) > 2 else "-"
                if verb == "JoinSession":
                    write(stream, f"ACK|{corr}|{verb}")
                elif verb == "SetClientRole":
                    write(stream, f"ACK|{corr}|{verb}")
                elif verb == "ClaimSim":
                    write(stream, f"ERR|{corr}|NotJoined|{verb}|")
                elif verb == "QueueInteraction":
                    write(stream, f"ERR|{corr}|UnauthorizedClientInsertion|{verb}|")
                elif drop:
                    # EOF is an exact observable silent drop, not a timing-dependent delay.
                    if drop == "open":
                        # Keep the socket open without a reply until the probe's own deadline.
                        self.assertEqual(stream.readline(), b"")
                    return
                else:
                    write(stream, f"ERR|{corr}|bad_line|{verb}|")
        with peer(server) as endpoint:
            return probe.run(endpoint, wire="line", timeout=2, player=77, actor=4242)

    def test_named_refusals_pass_and_artifact_has_required_summary(self):
        result = self.run_probe()
        self.assertTrue(result["passed"], result)
        self.assertEqual(result["silent_drops"], 0)
        self.assertGreaterEqual(len(result["cases"]), 4)
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "rejection_relay.txt"
            probe.write_report(path, result)
            self.assertIn("silent_drops=0\n", path.read_text(encoding="utf-8"))

    def test_silent_close_fails(self):
        result = self.run_probe(drop=True)
        self.assertFalse(result["passed"], result)
        self.assertGreater(result["silent_drops"], 0)

    def test_silent_open_socket_fails_at_command_deadline(self):
        result = self.run_probe(drop="open")
        self.assertFalse(result["passed"], result)
        self.assertEqual(result["silent_drops"], 1)

    def test_json_refusals_and_json_artifact(self):
        import json
        def server(stream):
            while raw := stream.readline():
                command = json.loads(raw)
                msg_type = command.get("$type")
                accepted = msg_type in ("join_session", "set_client_role")
                write(stream, json.dumps({"$type": "command_accepted" if accepted else "command_rejected",
                                          "commandId": command["commandId"], "reason": "NotJoined"}))
        with peer(server) as endpoint:
            result = probe.run(endpoint, wire="json", timeout=2)
        self.assertTrue(result["passed"], result)
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "rejection_relay.json"
            probe.write_report(path, result)
            self.assertEqual(json.loads(path.read_text())["silent_drops"], 0)

    def test_unrelated_corr_zero_cannot_cover_a_dropped_command(self):
        def server(stream):
            hello(stream)
            while stream.readline():
                write(stream, "ERR|0|NotJoined|-|")
        with peer(server) as endpoint:
            result = probe.run(endpoint, timeout=2)
        self.assertFalse(result["passed"])
        self.assertEqual(result["silent_drops"], 1, result)
        self.assertIsNone(result["cases"][0]["reply"])

    def test_unrelated_json_server_error_cannot_cover_a_dropped_command(self):
        def server(stream):
            while stream.readline():
                write(stream, '{"$type":"server_error","message":"NotJoined"}')
        with peer(server) as endpoint:
            result = probe.run(endpoint, wire="json", timeout=2)
        self.assertFalse(result["passed"])
        self.assertEqual(result["silent_drops"], 1, result)
        self.assertIsNone(result["cases"][0]["reply"])

    def test_probe_uses_the_shared_inflight_bulk_limit(self):
        def server(stream):
            hello(stream)
            stream.readline()
            for bulk_id in range(1, 18):
                write(stream, f"EV|{bulk_id}|0|0|SimAnimation|b{bulk_id}")
        with peer(server) as endpoint:
            result = probe.run(endpoint, timeout=2)
        self.assertFalse(result["passed"])
        self.assertEqual(result["cases"][0]["error"], "bulk_malformed")


if __name__ == "__main__":
    unittest.main()
