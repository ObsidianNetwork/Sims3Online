# /// script
# requires-python = ">=3.10"
# ///
# ─── How to run ───
# python -m unittest scripts.bridgectl_test

import subprocess
import sys
import unittest
from pathlib import Path


BRIDGECTL = Path(__file__).with_name("bridgectl.py")
REPO_ROOT = BRIDGECTL.parent.parent


def _run_bridgectl(*arguments: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, str(BRIDGECTL), *arguments],
        cwd=REPO_ROOT,
        check=False,
        capture_output=True,
        text=True,
        timeout=10,
    )


class BridgectlTopLevelHelpTests(unittest.TestCase):
    def test_long_help_exits_zero_when_requested_at_top_level(self) -> None:
        # Given: the bridgectl executable.
        # When: conventional long help is requested.
        result = _run_bridgectl("--help")

        # Then: argparse renders top-level help successfully on stdout.
        self.assertEqual(result.returncode, 0, msg=result.stderr)
        self.assertIn("usage:", result.stdout)
        self.assertIn("-h, --help", result.stdout)
        self.assertEqual(result.stderr, "")

    def test_short_help_exits_zero_when_requested_at_top_level(self) -> None:
        # Given: the bridgectl executable.
        # When: conventional short help is requested.
        result = _run_bridgectl("-h")

        # Then: argparse renders top-level help successfully on stdout.
        self.assertEqual(result.returncode, 0, msg=result.stderr)
        self.assertIn("usage:", result.stdout)
        self.assertIn("-h, --help", result.stdout)
        self.assertEqual(result.stderr, "")

    def test_help_token_keeps_existing_successful_behavior(self) -> None:
        # Given: the bridgectl executable and its legacy help token.
        # When: help is requested as a command token.
        result = _run_bridgectl("help")

        # Then: the existing command reference is still printed successfully.
        self.assertEqual(result.returncode, 0, msg=result.stderr)
        self.assertIn("Commands:", result.stdout)
        self.assertEqual(result.stderr, "")

    def test_hexenc_subcommand_still_receives_its_argument(self) -> None:
        # Given: an existing bridgectl subcommand with a local-only code path.
        # When: the subcommand is invoked through the top-level parser.
        result = _run_bridgectl("hexenc", "Terrain.GoHere")

        # Then: dispatch remains intact and succeeds without contacting the bridge.
        self.assertEqual(result.returncode, 0, msg=result.stderr)
        self.assertEqual(result.stdout, "5465727261696E2E476F48657265\n")
        self.assertEqual(result.stderr, "")


class JournalTests(unittest.TestCase):
    """Each command a script sends is one JSON line in the journal, before it goes out."""

    def setUp(self) -> None:
        import json
        import os
        import socket
        import tempfile
        from unittest import mock

        sys.path.insert(0, str(BRIDGECTL.parent))
        import bridgectl

        self.json, self.bridgectl, self.mock = json, bridgectl, mock
        self.tmp = tempfile.TemporaryDirectory()
        self.path = Path(self.tmp.name) / "bridge-journal.jsonl"
        self.listener = socket.create_server(("127.0.0.1", 0))
        self.sock = socket.create_connection(self.listener.getsockname())
        self.port = self.listener.getsockname()[1]
        self.env = mock.patch.dict(os.environ, {"SIMSONLINE_BRIDGE_JOURNAL": ""})
        self.env.start()

    def tearDown(self) -> None:
        self.env.stop()
        self.sock.close()
        self.listener.close()
        self.tmp.cleanup()

    def entries(self) -> list[dict]:
        if not self.path.exists():
            return []
        return [self.json.loads(line) for line in self.path.read_text(encoding="utf-8").splitlines()]

    def test_one_line_per_command(self) -> None:
        self.bridgectl.journal(self.sock, "SACS_RECORDER tune sacs.keyed-picks 1", self.path)
        self.bridgectl.journal(self.sock, "@60 DIAG_BLOB|c|b|0|9|00", self.path)  # upload chunks are left out
        self.bridgectl.journal(self.sock, "X" * 300, self.path)
        entries = self.entries()
        self.assertEqual([e["cmd"] for e in entries], ["SACS_RECORDER tune sacs.keyed-picks 1", "X" * 160])
        self.assertEqual(entries[0]["endpoint"], f"127.0.0.1:{self.port}")
        self.assertTrue({"t", "caller", "pid"} <= set(entries[0]))

    def test_off_rotated_and_never_raising(self) -> None:
        with self.mock.patch.dict("os.environ", {"SIMSONLINE_BRIDGE_JOURNAL": "0"}):
            self.bridgectl.journal(self.sock, "DIAG_STATE", self.path)
        self.assertEqual(self.entries(), [])
        self.path.write_text("old\n", encoding="utf-8")
        with self.mock.patch.object(self.bridgectl, "JOURNAL_MAX", 1):
            self.bridgectl.journal(self.sock, "DIAG_STATE", self.path)
        self.assertEqual(self.path.with_name(self.path.name + ".1").read_text(encoding="utf-8"), "old\n")
        self.assertEqual([e["cmd"] for e in self.entries()], ["DIAG_STATE"])
        self.bridgectl.journal(self.sock, "DIAG_STATE", Path(self.tmp.name) / "no" / "such" / "dir.jsonl")  # no raise


class SendCorpusTests(unittest.TestCase):
    """send-corpus: gate 6 step (a), the C# half sending the corpus lines the Rust roundtrip sends (card 56)."""

    CORR = "0123456789abcdef0123456789abcdef"

    def setUp(self) -> None:
        sys.path.insert(0, str(BRIDGECTL.parent))
        import bridgectl

        self.bridgectl = bridgectl

    def test_only_disconnect_may_go_unanswered(self) -> None:
        # DISCONNECT has no reply by design (ServerHostMain.cs:708): its listener timeout is expected; any other
        # verb without an answer still fails the run.
        timeout = {"ok": False, "error": "timeout after 30s"}
        self.assertFalse(self.bridgectl.unanswered("Disconnect", timeout))
        self.assertTrue(self.bridgectl.unanswered("Ping", timeout))
        self.assertTrue(self.bridgectl.unanswered("LoadSave", timeout))
        self.assertFalse(self.bridgectl.unanswered("Ping", {"ok": True, "accepted": True}))
        # Only the no-reply timeout is exempt: another DISCONNECT error still fails.
        self.assertTrue(self.bridgectl.unanswered("Disconnect", {"ok": False, "error": "bridge is not connected"}))

    def test_it_refuses_to_run_without_live_and_opens_no_connection(self) -> None:
        result = _run_bridgectl("--endpoint", "127.0.0.1:1", "send-corpus", "no-such-corpus.txt")
        self.assertEqual(result.returncode, 2, result.stderr)
        self.assertIn("--live", result.stderr)
        self.assertNotIn("connect to bridgectl listener", result.stderr)

    def test_a_wire_line_becomes_the_admin_request_that_encodes_back_to_it(self) -> None:
        # The listener splits on spaces, maps "-" to an empty part and "\-" to a literal "-" part, and joins
        # NAME|<new id>|args with "|".
        self.assertEqual(self.bridgectl.admin_request(f"DIAG_WALK|{self.CORR}|123||2"), "DIAG_WALK 123 - 2")
        self.assertEqual(self.bridgectl.admin_request(f"PING|{self.CORR}"), "PING")
        self.assertEqual(self.bridgectl.admin_request(f"LOAD_SAVE|{self.CORR}|a|b"), "LOAD_SAVE a b")

    def test_the_cards_three_literal_lines_cross_with_the_dash_escape(self) -> None:
        # Card 56 gate 6 step (d) (c9a23656, Main's Q2 ruling of 2026-11-01): the three verbs whose arguments are a
        # null or an empty id list encode to a lone "-" field, which the codec writes and the listener would
        # otherwise turn into an empty part. They encode now instead of raising, and the controller records the
        # Rust half's line byte for byte (BridgeAdminListener.HandleRequestAsync, EncodeRawCommand).
        self.assertEqual(self.bridgectl.admin_request(f"DIALOG_ANSWER|{self.CORR}|0|-"),
                         f"DIALOG_ANSWER 0 {self.bridgectl.LISTENER_DASH}")
        self.assertEqual(self.bridgectl.admin_request(f"SET_SIM_SCOPE|{self.CORR}|-|-"),
                         f"SET_SIM_SCOPE {self.bridgectl.LISTENER_DASH} {self.bridgectl.LISTENER_DASH}")
        self.assertEqual(self.bridgectl.admin_request(f"SET_AUTHORITY|{self.CORR}|observe|-"),
                         f"SET_AUTHORITY observe {self.bridgectl.LISTENER_DASH}")
        # An empty part still goes as a bare "-", and only the whole token is the escape.
        self.assertEqual(self.bridgectl.admin_request(f"DIAG_WALK|{self.CORR}|123||2"), "DIAG_WALK 123 - 2")
        self.assertEqual(self.bridgectl.admin_request(f"DIAG_WALK|{self.CORR}|123|\\x|2"), "DIAG_WALK 123 \\x 2")

    def test_a_line_the_admin_protocol_cannot_carry_is_refused(self) -> None:
        # A part that is the escape token itself cannot be sent: the listener would read it as its own escape.
        for line in [f"LOAD_SAVE|{self.CORR}|my save.sims3", "PING",
                     f"PING|{self.CORR[:-1]}", f"ping|{self.CORR}", f"EVENTS_DROPPED|{self.CORR}",
                     f"LOAD_SAVE|{self.CORR}|Saïd.sims3", f"PÏNG|{self.CORR}", f"LOAD_SAVE|{self.CORR}|\\-"]:
            with self.assertRaises(ValueError, msg=line):
                self.bridgectl.admin_request(line)

    def test_refused_verbs_are_listed_and_never_sent(self) -> None:
        encoded = [{"verb": "Ping", "line": f"PING|{self.CORR}"},
                   {"verb": "ApplyInteractions", "refused": "refused_legacy_verb:APPLY_INTERACTIONS"}]
        sends, refused = self.bridgectl.corpus_requests(encoded)
        self.assertEqual(sends, [("Ping", f"PING|{self.CORR}", "PING")])
        self.assertEqual(refused, ["ApplyInteractions"])


if __name__ == "__main__":
    unittest.main()
