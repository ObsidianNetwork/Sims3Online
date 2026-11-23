"""bridge_record_replies turns a --bridge-record capture into gate 6's reply file
(python -m unittest scripts.bridge_record_replies_test).

The fixture is a synthetic capture in the controller's form (DedicatedServer/Program.cs:509-526,
ProcessMemoryBridge.cs:42-46): a background controller command, an event, a reply that arrives after another
command's, a payload with a raw newline, a command with no reply, and a slot-less frame line.
"""
from __future__ import annotations

import contextlib
import io
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
sys.path.insert(0, str(SCRIPTS))
import bridge_record_replies as brr  # noqa: E402

A = "0123456789abcdef0123456789abcdef"
B = "fedcba9876543210fedcba9876543210"
C = "00112233445566778899aabbccddeeff"
D = "ffeeddccbbaa99887766554433221100"
E = "0f1e2d3c4b5a69788796a5b4c3d2e1f0"

RECORD = "\n".join([
    f"[bridge 2026-11-01 12:00:00.000Z] ctl->host seq=1100 slot=8 DIAG_STATE|{A}",
    f"[bridge 2026-11-01 12:00:00.001Z] ctl->host seq=1101 slot=9 REQUEST_SNAPSHOT|{B}|42",
    "[bridge 2026-11-01 12:00:00.002Z] host->ctl seq=7 EVENT|InteractionStarted|1|2",
    f"[bridge 2026-11-01 12:00:00.003Z] host->ctl seq=1101 BULK_REF|{B}|snapshot|2|0|1|500|00ABCDEF",
    f"[bridge 2026-11-01 12:00:00.004Z] host->ctl seq=1100 DIAG_STATE_REPORT|{A}|ok|role=server;tick=4",
    "second line of the report",
    f"[bridge 2026-11-01 12:00:00.005Z] ctl->host seq=1102 slot=compat0 DISCONNECT|{C}",
    f"[bridge 2026-11-01 12:00:00.006Z] ctl->host seq=1103 PING|{D}",
    f"[bridge 2026-11-01 12:00:00.007Z] host->ctl seq=1103 OK|{D.upper()}",
    "[bridge 2026-11-01 12:00:00.008Z] host->ctl seq=8 EVENT|InteractionReplayQueued|5|626F6F6B",
    f"[bridge 2026-11-01 12:00:00.009Z] host->ctl seq=1099 OK|{E}",
    "",
])


# A copy of stackctl's server-bridge.log starts at the controller's CONNECT (ProcessMemoryBridge.cs:570, :773).
HANDSHAKE = "\n".join([
    "ctl->host seq=1099 CONNECT|00000000000000000000000000000000|23",
    "host->ctl seq=1099 HELLO|6|57|10",
    "",
])

# Gate 6 step (d)'s second server process (card 56, c9a23656 row 208): its own CONNECT, the three verbs no read lets
# a half undo with the card's literal arguments, then its DISCONNECT. Copied to `csharp_bridge.2.record`.
RECORD2 = "\n".join([
    "ctl->host seq=2100 CONNECT|00000000000000000000000000000000|23",
    "host->ctl seq=2100 HELLO|6|57|10",
    f"ctl->host seq=2101 slot=8 DIALOG_ANSWER|{A}|0|-",
    f"host->ctl seq=2101 OK|{A}",
    f"ctl->host seq=2102 slot=9 SET_SIM_SCOPE|{B}|-|-",
    f"host->ctl seq=2102 AUTONOMY_PROBE|{B}|ok|686F6F6B3D616273656E74",
    f"ctl->host seq=2103 slot=8 SET_AUTHORITY|{C}|observe|-",
    f"host->ctl seq=2103 AUTONOMY_PROBE|{C}|ok|686F6F6B3D616273656E74",
    f"ctl->host seq=2104 slot=compat0 DISCONNECT|{D}",
    "",
])


class SecondRecordTests(unittest.TestCase):
    """Card 56 gate 6 (c9a23656 row 208): step (d)'s rows are appended to `csharp_replies.txt` after the first
    record's, and gate 5 names the `.2` record as their source."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self.tmp.name)
        self.record = self.dir / "csharp_bridge.record"
        self.out = self.dir / "csharp_replies.txt"
        self.records = self.dir / "csharp_replies.records.txt"

    def tearDown(self):
        self.tmp.cleanup()

    def run_main(self, first: str, argv: list[str]) -> tuple[int, str]:
        self.record.write_bytes(first.encode("utf-8"))
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = brr.main(argv)
        return code, err.getvalue()

    def extract(self, records_out: bool = False) -> list[str]:
        argv = [str(self.record), "--out", str(self.out)]
        if records_out:
            argv += ["--records-out", str(self.records)]
        code, err = self.run_main(HANDSHAKE + RECORD, argv)
        self.assertEqual(code, 0, err)
        return self.out.read_text(encoding="utf-8").split("\n")[:-1]

    def with_second(self, second: str = RECORD2) -> list[str]:
        (self.dir / "csharp_bridge.2.record").write_bytes(second.encode("utf-8"))
        return self.extract(records_out=True)

    def test_the_second_record_is_appended_after_the_first(self):
        own = self.extract()
        self.assertEqual(len(own), 7, "record 1 alone writes its own rows")
        lines = self.with_second()
        self.assertEqual(lines[:len(own)], own)
        self.assertEqual([line.split("\t")[0] for line in lines[len(own):]],
                         ["HELLO", "DIALOG_ANSWER", "SET_SIM_SCOPE", "SET_AUTHORITY", "DISCONNECT"])
        self.assertEqual(lines[len(own) + 1], f"DIALOG_ANSWER\tDIALOG_ANSWER|{A}|0|-\tOK|{A}")

    def test_the_records_file_names_each_rows_record(self):
        lines = self.with_second()
        sources = [s.replace("\\", "/") for s in self.records.read_text(encoding="utf-8").split("\n")[:-1]]
        self.assertEqual(len(sources), len(lines))
        first, second = str(self.record).replace("\\", "/"), str(self.dir / "csharp_bridge.2.record").replace("\\", "/")
        self.assertEqual(set(sources[:7]), {first})
        self.assertEqual(set(sources[7:]), {second})

    def test_a_second_record_without_a_connect_is_refused(self):
        (self.dir / "other.record").write_bytes(RECORD.encode("utf-8"))  # a copy cut before its CONNECT
        code, err = self.run_main(HANDSHAKE + RECORD, [str(self.record), "--out", str(self.out),
                                                       "--and-record", str(self.dir / "other.record")])
        self.assertEqual(code, 1)
        self.assertIn("CONNECT", err)
        self.assertIn("other.record", err)
        self.assertFalse(self.out.exists(), "a refused second record writes no reply file")

    def test_and_record_reads_a_second_record_of_any_name(self):
        (self.dir / "step_d.record").write_bytes(RECORD2.encode("utf-8"))
        code, err = self.run_main(HANDSHAKE + RECORD, [str(self.record), "--out", str(self.out),
                                                       "--and-record", str(self.dir / "step_d.record")])
        self.assertEqual(code, 0, err)
        self.assertEqual(len(self.out.read_text(encoding="utf-8").split("\n")[:-1]), 12)

    def test_a_missing_second_record_is_not_an_error(self):
        self.assertEqual(len(self.extract()), 7)

    def test_no_second_record_is_derived_from_a_record_without_the_record_suffix(self):
        (self.dir / "log").write_bytes((HANDSHAKE + RECORD).encode("utf-8"))
        code, err = self.run_main(HANDSHAKE + RECORD, [str(self.dir / "log"), "--out", str(self.out)])
        self.assertEqual(code, 0, err)
        self.assertEqual(len(self.out.read_text(encoding="utf-8").split("\n")[:-1]), 7)


class RecordCheckTests(unittest.TestCase):
    """The extractor refuses a record that is not a whole capture (card 56 ruling f82d9f52)."""

    def run_main(self, text: str) -> tuple[int, str]:
        with tempfile.TemporaryDirectory() as tmp:
            record = Path(tmp) / "csharp_bridge.record"
            record.write_bytes(text.encode("utf-8"))
            err = io.StringIO()
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(err):
                code = brr.main([str(record), "--out", str(Path(tmp) / "o.txt")])
            return code, err.getvalue()

    def test_a_record_without_a_leading_connect_is_refused(self):
        code, err = self.run_main(RECORD)
        self.assertEqual(code, 1)
        self.assertIn("CONNECT", err)

    def test_a_record_without_a_final_newline_is_refused(self):
        code, err = self.run_main((HANDSHAKE + RECORD).rstrip("\n"))
        self.assertEqual(code, 1)
        self.assertIn("newline", err)

    def test_a_whole_capture_is_accepted(self):
        self.assertEqual(self.run_main(HANDSHAKE + RECORD)[0], 0)


class FramesTests(unittest.TestCase):
    def test_frames_parse_with_and_without_the_stamp_and_slot(self):
        frames = brr.frames(RECORD.splitlines())
        self.assertEqual(frames[0], ("ctl->host", 1100, f"DIAG_STATE|{A}"))
        self.assertEqual(len(frames), 10, "the continuation line is not a frame of its own")
        self.assertEqual(frames[6], ("ctl->host", 1103, f"PING|{D}"))
        self.assertEqual(brr.frames([f"host->ctl seq=-3 OK|{A}"]), [("host->ctl", -3, f"OK|{A}")])

    def test_a_line_without_a_frame_header_continues_the_previous_payload(self):
        frames = brr.frames(RECORD.splitlines())
        self.assertEqual(frames[4][2], f"DIAG_STATE_REPORT|{A}|ok|role=server;tick=4\nsecond line of the report")

    def test_lines_before_any_frame_are_ignored(self):
        self.assertEqual(brr.frames(["Bridge frame recording to x.", f"host->ctl seq=1 OK|{A}"]),
                         [("host->ctl", 1, f"OK|{A}")])


class RepliesTests(unittest.TestCase):
    def test_each_command_gets_the_first_host_line_with_its_correlation_id(self):
        rows = brr.replies(brr.frames(RECORD.splitlines()))
        self.assertEqual([r[0] for r in rows], ["DIAG_STATE", "REQUEST_SNAPSHOT", "DISCONNECT", "PING"])
        self.assertEqual(rows[0][2], f"DIAG_STATE_REPORT|{A}|ok|role=server;tick=4\nsecond line of the report")
        self.assertEqual(rows[1][2], f"BULK_REF|{B}|snapshot|2|0|1|500|00ABCDEF")
        self.assertEqual(rows[2], ("DISCONNECT", f"DISCONNECT|{C}", ""), "no reply: an empty field")
        self.assertEqual(rows[3][2], f"OK|{D.upper()}", "the id matches case-insensitively")

    def test_connect_is_the_handshake_not_a_verb(self):
        rows = brr.replies([("ctl->host", 5, "CONNECT|" + "0" * 32 + "|23"), ("host->ctl", 5, "HELLO|6|57|10")])
        self.assertEqual(rows, [])

    def test_a_reply_is_claimed_once(self):
        frames = [("ctl->host", 1, f"PING|{A}"), ("ctl->host", 2, f"PING|{A}"), ("host->ctl", 1, f"OK|{A}")]
        self.assertEqual([r[2] for r in brr.replies(frames)], [f"OK|{A}", ""])


class UnclaimedRowsTests(unittest.TestCase):
    """Card 56 gate 6 (1b8fbc27): a host frame no command claimed is a row of its own, `<first | field>\t\t<line>`,
    in record order, EVENT pushes included; only EVENT|InteractionReplayQueued is left out (E.3)."""

    def test_rows_follow_record_order_with_the_unclaimed_host_frames(self):
        rows = brr.reply_rows(brr.frames(RECORD.splitlines()))
        self.assertEqual([r[0] for r in rows], ["DIAG_STATE", "REQUEST_SNAPSHOT", "EVENT", "DISCONNECT", "PING", "OK"])
        self.assertEqual(rows[2], ("EVENT", "", "EVENT|InteractionStarted|1|2"))
        self.assertEqual(rows[5], ("OK", "", f"OK|{E}"), "a late host line no command claims")
        self.assertEqual(rows[1], ("REQUEST_SNAPSHOT", f"REQUEST_SNAPSHOT|{B}|42", f"BULK_REF|{B}|snapshot|2|0|1|500|00ABCDEF"))
        self.assertFalse(any("InteractionReplayQueued" in r[2] for r in rows))

    def test_the_handshake_reply_is_an_unclaimed_row(self):
        rows = brr.reply_rows([("ctl->host", 5, "CONNECT|" + "0" * 32 + "|23"), ("host->ctl", 5, "HELLO|6|57|10")])
        self.assertEqual(rows, [("HELLO", "", "HELLO|6|57|10")])


class FileFormatTests(unittest.TestCase):
    def test_rows_are_tab_separated_with_the_corpus_escapes(self):
        line = brr.format_row(("DIAG_STATE", f"DIAG_STATE|{A}", "R|a\tb\\c\nd\re"))
        self.assertEqual(line, f"DIAG_STATE\tDIAG_STATE|{A}\tR|a\\tb\\\\c\\nd\\re")

    def test_main_writes_the_reply_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            record, out = Path(tmp) / "csharp_bridge.record", Path(tmp) / "csharp_replies.txt"
            record.write_bytes((HANDSHAKE + RECORD).encode("utf-8"))
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(brr.main([str(record), "--out", str(out)]), 0)
            lines = out.read_text(encoding="utf-8").split("\n")[:-1]
            self.assertEqual(len(lines), 7)
            self.assertEqual(lines[0], "HELLO\t\tHELLO|6|57|10")
            self.assertEqual(lines[3], "EVENT\t\tEVENT|InteractionStarted|1|2")
            self.assertEqual(lines[5], f"PING\tPING|{D}\tOK|{D.upper()}")
            self.assertEqual(lines[6], f"OK\t\tOK|{E}")

    def test_a_payload_keeps_characters_python_would_split_lines_on(self):
        # Only "\n" ends a record line (the controller's WriteLine); \x0b, \x1c and U+2028 belong to the payload.
        payload = f"DIAG_STATE_REPORT|{A}|ok|a\x0bb\x1cc\u2028d\u0085e"
        text = f"ctl->host seq=1 slot=8 DIAG_STATE|{A}\r\nhost->ctl seq=1 {payload}\r\n"
        with tempfile.TemporaryDirectory() as tmp:
            record = Path(tmp) / "csharp_bridge.record"
            record.write_bytes(text.encode("utf-8"))
            rows = brr.replies(brr.frames(brr.read_record(record)))
        self.assertEqual(rows, [("DIAG_STATE", f"DIAG_STATE|{A}", payload)])

    def test_main_fails_on_undecodable_bytes(self):
        with tempfile.TemporaryDirectory() as tmp:
            record = Path(tmp) / "csharp_bridge.record"
            record.write_bytes(f"ctl->host seq=1 slot=8 PING|{A}\nhost->ctl seq=1 OK|".encode() + b"\xff\xfe\n")
            err = io.StringIO()
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(err):
                self.assertEqual(brr.main([str(record), "--out", str(Path(tmp) / "o.txt")]), 2)
            self.assertIn("UTF-8", err.getvalue())
            self.assertFalse((Path(tmp) / "o.txt").exists())

    def test_main_fails_on_a_record_without_commands(self):
        with tempfile.TemporaryDirectory() as tmp:
            record = Path(tmp) / "empty.record"
            record.write_text("", encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(brr.main([str(record), "--out", str(Path(tmp) / "o.txt")]), 1)


if __name__ == "__main__":
    unittest.main()
