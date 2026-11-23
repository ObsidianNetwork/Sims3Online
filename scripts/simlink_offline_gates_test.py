"""Card 56's offline harness reports gate 5 honestly (python -m unittest scripts.simlink_offline_gates_test).

Gate 5 (card 56 §G) is sealed on a lossless `--bridge-record` capture from gate 6's C# half; the synthetic reply
corpus is a supplement and never makes the gate pass on its own. Exit 3 means only live or capture-pending gates
are open.
"""
from __future__ import annotations

import importlib.util
import unittest
from pathlib import Path

HARNESS = Path(__file__).resolve().parent.parent / "rust" / "simlink_bridge" / "tools" / "run_offline_gates.py"


def load():
    spec = importlib.util.spec_from_file_location("run_offline_gates", HARNESS)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


gates = load()


class Gate5Tests(unittest.TestCase):
    def test_a_passing_synthetic_corpus_is_pending_live_not_pass(self):
        status, reason = gates.gate5(synthetic_ok=True)
        self.assertNotIn(status, {"PASS", "VERIFIED"})
        self.assertEqual(status, "PENDING_LIVE")
        self.assertIn("csharp_bridge.record", reason)
        self.assertIn("--bridge-record", reason)

    def test_a_failing_synthetic_corpus_fails(self):
        status, _ = gates.gate5(synthetic_ok=False)
        self.assertEqual(status, "FAIL")

    def test_the_recorded_capture_decides_gate_5_and_the_synthetic_corpus_can_only_fail_it(self):
        self.assertEqual(gates.gate5(synthetic_ok=True, capture_ok=True), ("PASS", ""))
        self.assertEqual(gates.gate5(synthetic_ok=True, capture_ok=False)[0], "FAIL")
        self.assertEqual(gates.gate5(synthetic_ok=False, capture_ok=True)[0], "FAIL")
        self.assertEqual(gates.gate5(synthetic_ok=True, capture_ok=None)[0], "PENDING_LIVE")

    def test_gate_5_evidence_names_the_capture_only_when_there_is_one(self):
        self.assertEqual(gates.gate5_evidence(captured=False), "reply_diff_synthetic.jsonl")
        self.assertEqual(gates.gate5_evidence(captured=True),
                         "reply_diff.jsonl,csharp_replies.txt,csharp_reply_dumps.txt,reply_diff_synthetic.jsonl")


class CaptureArgumentTests(unittest.TestCase):
    """--capture is the run folder's own csharp_bridge.record (card 56 gate 6, ruling f82d9f52)."""

    def test_only_the_run_folders_csharp_bridge_record_is_accepted(self):
        run = Path("C:/runs/card56-x")
        self.assertIsNone(gates.capture_error(run / "csharp_bridge.record", run))
        self.assertIsNotNone(gates.capture_error(run / "server-bridge.log", run))
        self.assertIsNotNone(gates.capture_error(Path("C:/elsewhere/csharp_bridge.record"), run))


class ScorecardGateTests(unittest.TestCase):
    """verbs_roundtripped counts gate 5's capture diff, so without a capture the scorecard is pending, not PASS."""

    def test_without_a_capture_a_short_scorecard_is_pending_live(self):
        status, reason = gates.gate_h(score_ok=False, captured=False)
        self.assertEqual(status, "PENDING_LIVE")
        self.assertIn("reply_diff.jsonl", reason)

    def test_with_a_capture_a_short_scorecard_fails_and_a_full_one_passes(self):
        self.assertEqual(gates.gate_h(score_ok=False, captured=True)[0], "FAIL")
        self.assertEqual(gates.gate_h(score_ok=True, captured=True), ("PASS", ""))

    def test_the_gate_line_names_the_reason(self):
        status, reason = gates.gate5(synthetic_ok=True)
        lines = gates.gate_lines([("1", True, "cargo_test.txt", ""), ("5", status, "reply_diff.jsonl", reason)])
        five = [line for line in lines if line.startswith("gate=5|")]
        self.assertEqual(len(five), 1)
        self.assertTrue(five[0].startswith("gate=5|status=PENDING_LIVE|evidence=reply_diff.jsonl|reason="), five[0])
        self.assertIn("gate=1|status=PASS|evidence=cargo_test.txt", lines)
        self.assertTrue(any(line.startswith("gate=3|status=PENDING_LIVE|") for line in lines))
        self.assertTrue(any(line.startswith("gate=6|status=PENDING_LIVE|") for line in lines))


class UnsentVerbTests(unittest.TestCase):
    """Card 56 gate 6 (4c970a49 row 208): a verb that cannot be made safe is not sent, so gates 5 and 6 stay
    PENDING_LIVE and name it. The run folder's corpora (scripts/card56_corpus.py) list it as `#@unsent`."""

    def setUp(self):
        import tempfile
        self.tmp = tempfile.TemporaryDirectory()
        self.run = Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def write(self, name: str, *unsent: str):
        lines = ["# corpus", *(f"#@unsent {u}" for u in unsent), "Ping\t0123456789abcdef0123456789abcdef"]
        (self.run / name).write_text("\n".join(lines) + "\n", encoding="utf-8")

    def test_the_corpora_unsent_verbs_are_read_once_each(self):
        self.write("csharp_corpus.txt", "SetSimScope no restore value", "AnswerDialog no remove verb")
        self.write("rust_corpus.txt", "SetSimScope no restore value")
        self.assertEqual(gates.unsent_verbs(self.run),
                         [("AnswerDialog", "no remove verb"), ("SetSimScope", "no restore value")])
        self.assertEqual(gates.unsent_verbs(None), [])

    def test_reply_diff_is_told_the_run_folders_unsent_verbs(self):
        # reply-diff then marks an unsent verb missing from the capture `unsent` instead of failing it (simlinkctl's
        # capture-fixture tests run that end to end); gate5 turns its pass into PENDING_LIVE naming the verbs.
        self.assertEqual(gates.unsent_args(self.run), [])
        self.write("csharp_corpus.txt", "SetSimScope no restore value")
        self.write("rust_corpus.txt", "SetSimScope no restore value")
        self.assertEqual(gates.unsent_args(self.run), ["--unsent-from", str(self.run / "csharp_corpus.txt"),
                                                       "--unsent-from", str(self.run / "rust_corpus.txt")])
        unsent = gates.unsent_verbs(self.run)
        status, reason = gates.gate5(synthetic_ok=True, capture_ok=True, unsent=unsent)
        self.assertEqual(status, "PENDING_LIVE")
        self.assertIn("SetSimScope", reason)
        self.assertEqual(gates.gate5(synthetic_ok=True, capture_ok=False, unsent=unsent)[0], "FAIL")

    def test_a_short_scorecard_with_unsent_verbs_is_pending_naming_them(self):
        status, reason = gates.gate_h(score_ok=False, captured=True, unsent=[("SetAuthority", "no read verb")])
        self.assertEqual(status, "PENDING_LIVE")
        self.assertIn("SetAuthority", reason)

    def test_gate_6s_reason_names_the_unsent_verbs(self):
        self.write("csharp_corpus.txt", "SetAuthority no read verb")
        six = [g for g in gates.live_gates(self.run) if g[0] == "6"][0]
        self.assertEqual(six[1], "PENDING_LIVE")
        self.assertIn("SetAuthority (no read verb)", six[3])
        line = [l for l in gates.gate_lines([], gates.live_gates(self.run)) if l.startswith("gate=6|")][0]
        self.assertIn("SetAuthority", line)


class ExitCodeTests(unittest.TestCase):
    def test_only_live_or_capture_pending_gates_open_is_exit_3(self):
        self.assertEqual(gates.exit_code(["PASS", "PENDING_LIVE"]), 3)
        self.assertEqual(gates.exit_code(["PASS", "PENDING_LIVE", "PENDING_APPLY_RO"]), 3)

    def test_any_failure_is_exit_1(self):
        self.assertEqual(gates.exit_code(["PASS", "PENDING_LIVE", "FAIL"]), 1)

    def test_every_gate_passing_is_exit_0(self):
        self.assertEqual(gates.exit_code(["PASS"]), 0)


if __name__ == "__main__":
    unittest.main()
