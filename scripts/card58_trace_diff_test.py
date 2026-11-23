"""Regression tests for the offline controller/relay comparison."""

import base64
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import zlib

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from scripts import card58_trace_diff as diff


class Card58TraceDiffTests(unittest.TestCase):
    @staticmethod
    def record(payload, direction="host->ctl", ms=0):
        return f"[bridge 2026-11-01 17:33:03.{ms:03d}Z] {direction} seq=1 {payload}"

    @staticmethod
    def reference(body, corr="abc", kind="snapshot", index=None, count=1, total=None):
        tail = f"{len(body)}" if index is None else f"{index}|{count}|{total}"
        return f"BULK_REF|{corr}|{kind}|0|{tail}|{zlib.crc32(body):08X}"

    def test_equal_stream_has_no_drops_or_regressions(self):
        rows = [{"connection": 1, "seq": 1, "kind": "SimAnimation", "fields": ["[61]"]},
                {"connection": 2, "seq": 1, "kind": "SimAnimation", "fields": ["[61]"]},
                {"connection": 1, "seq": 2, "kind": "WorldTimeChanged", "fields": ["(0;1;0)"]}]
        self.assertEqual(diff.compare(rows, rows)["frames_dropped"], 0)
        self.assertEqual(diff.compare(rows, rows)["seq_regressions"], 0)

    def test_missing_extra_wrong_recipient_and_reordered_frames_fail(self):
        rows = [{"connection": 1, "seq": 1, "kind": "DialogRequested", "fields": []},
                {"connection": 1, "seq": 2, "kind": "SimAnimation", "fields": []}]
        self.assertEqual(diff.compare(rows, rows[:1])["frames_dropped"], 1)
        self.assertGreater(diff.compare(rows, list(reversed(rows)))["seq_regressions"], 0)
        leak = [*rows, {"connection": 3, "seq": 1, "kind": "DialogRequested", "fields": []}]
        self.assertEqual(diff.compare(rows, leak)["leak_events"], 1)
        self.assertFalse(diff.compare(rows, leak)["equal"])
        self.assertFalse(diff.compare(rows, [{**rows[0], "fields": ["changed"]}, rows[1]])["equal"])

    def test_record_parser_preserves_bulk_metadata_without_inventing_payload(self):
        rows = diff.recorded_rows([
            "[bridge 2026-11-01 17:33:03.701Z] host->ctl seq=1 BULK_REF|abc|snapshot-part|0|0|2|941756|1A262355",
            "[bridge 2026-11-01 17:33:04.000Z] host->ctl seq=2 EVENT|SpeedIntentCaptured|Normal",
        ])
        self.assertEqual(rows[0]["op"], "bulk_metadata")
        self.assertTrue(rows[0]["payload_missing"])
        self.assertEqual(rows[1]["line"], "EVENT|SpeedIntentCaptured|Normal")

    def test_companion_snapshot_body_materializes_tick_and_forced_delta(self):
        body = diff.snapshot_bytes(42)
        reference = self.reference(body)
        with tempfile.TemporaryDirectory() as tmp:
            record = Path(tmp) / "csharp_bridge.record"
            companion = Path(str(record) + ".bulk.jsonl")
            companion.write_text(json.dumps({"line": reference, "body": base64.b64encode(body).decode()}) + "\n", encoding="utf-8")
            payloads = diff.load_bulk_payloads(record)
            rows = diff.recorded_rows([self.record("REQUEST_SNAPSHOT|abc|-1", "ctl->host"),
                                       self.record(reference, ms=200)], payloads)
        self.assertEqual(rows[1], {"op": "snapshot", "body": base64.b64encode(body).decode(),
                                   "tick": 42, "force_full": True, "now": 200})
        self.assertEqual(sum(row.get("payload_missing", False) for row in rows), 0)

    def test_multipart_is_assembled_in_index_order_at_final_arrival(self):
        body = diff.snapshot_bytes(84)
        parts = [body[:64], body[64:]]
        refs = [self.reference(part, kind="snapshot-part", index=i, count=2, total=len(body))
                for i, part in enumerate(parts)]
        rows = diff.recorded_rows([self.record(refs[1]), self.record("EVENT|SpeedIntentCaptured|Normal", ms=1),
                                   self.record(refs[0], ms=2)], dict(zip(refs, parts)))
        self.assertEqual([row["op"] for row in rows], ["event", "snapshot"])
        self.assertEqual(base64.b64decode(rows[-1]["body"]), body)
        self.assertEqual(rows[-1]["tick"], 84)
        self.assertEqual(rows[-1]["now"], 2)
        self.assertFalse(rows[-1]["force_full"])

    def test_partial_companion_preserves_unavailable_metadata_for_entire_snapshot(self):
        body = diff.snapshot_bytes(1)
        parts = [body[:64], body[64:]]
        refs = [self.reference(part, kind="snapshot-part", index=i, count=2, total=len(body))
                for i, part in enumerate(parts)]
        rows = diff.recorded_rows(map(self.record, refs), {refs[0]: parts[0]})
        self.assertEqual([row["op"] for row in rows], ["bulk_metadata", "bulk_metadata"])
        self.assertTrue(all(row["payload_missing"] for row in rows))
        rows = diff.recorded_rows([self.record(refs[0])], dict(zip(refs, parts)))
        self.assertEqual(rows[0]["op"], "bulk_metadata")

    def test_companion_reply_map_and_correlation_part_keys(self):
        body = b"DIAG_STATE_REPORT|abc|ok|636c6f636b"
        reference = self.reference(body, kind="diag")
        with tempfile.TemporaryDirectory() as tmp:
            record = Path(tmp) / "csharp_bridge.record"
            Path(str(record) + ".bulk.json").write_text(json.dumps({"abc": base64.b64encode(body).decode()}), encoding="utf-8")
            rows = diff.recorded_rows([self.record(reference)], diff.load_bulk_payloads(record))
        self.assertEqual(rows, [{"op": "bridge_reply", "line": body.decode(), "now": 0}])
        rows = diff.recorded_rows([self.record(reference)], {("abc", 0): body})
        self.assertEqual(rows[0]["op"], "bridge_reply")

    def test_corrupt_or_mismatched_bulk_bodies_fail_instead_of_claiming_parity(self):
        body = diff.snapshot_bytes(1)
        reference = self.reference(body)
        with self.assertRaisesRegex(ValueError, "CRC mismatch"):
            diff.recorded_rows([self.record(reference)], {reference: b"corrupt"})
        reference = self.reference(body).replace(f"|{len(body)}|", f"|{len(body) + 1}|")
        with self.assertRaisesRegex(ValueError, "length mismatch"):
            diff.recorded_rows([self.record(reference)], {reference: body})

    def test_main_reports_full_parity_only_when_companion_bodies_and_streams_match(self):
        for companion, equal in [(False, True), (True, True), (True, False)]:
            with self.subTest(companion=companion, equal=equal), tempfile.TemporaryDirectory() as tmp:
                record = Path(tmp) / "csharp_bridge.record"
                body = diff.snapshot_bytes(7)
                reference = self.reference(body)
                record.write_text(self.record(reference) + "\n", encoding="utf-8")
                if companion:
                    Path(str(record) + ".bulk.jsonl").write_text(json.dumps({
                        "corr": "abc", "body": base64.b64encode(body).decode(),
                    }) + "\n", encoding="utf-8")
                frames = [{"connection": 1, "seq": 1, "kind": "SnapshotDelta"}]
                replies = [frames, frames, frames, frames, frames, frames if equal else []]
                with patch.object(diff, "execute", side_effect=replies) as execute, \
                        patch.object(diff.subprocess, "run") as run:
                    run.return_value.returncode = 0
                    result = diff.main(["--rust-binary", "offline.exe", "--artifact-dir", tmp,
                                        "--record", str(record), "--require-bulk-bodies"])
                summary = json.loads((Path(tmp) / "trace_diff.jsonl").read_text(encoding="utf-8").splitlines()[-1])
                self.assertEqual(summary["unavailable_bulk_parts"], 0 if companion else 1)
                self.assertEqual(summary["full_recorded_snapshot_parity"], companion and equal)
                self.assertEqual(result, int(not companion or not equal))
                rows = execute.call_args_list[-1].args[1]
                self.assertEqual(rows[-1]["op"], "snapshot" if companion else "bulk_metadata")


if __name__ == "__main__":
    unittest.main()
