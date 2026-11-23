"""Unit tests for dbpf_grep.py (dbpf_dump.py grep): python -m unittest scripts.dbpf_grep_test"""
from __future__ import annotations

import contextlib
import io
import struct
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import dbpf_grep as dg  # noqa: E402
from refpack import refpack  # noqa: E402

SIM = 0x01661233  # a family dbpf_diff names: "SIM (SimDescriptions)"
OTHER = 0x0BADF00D


def packed(header_flags: int, stream: bytes, usize: int) -> bytes:
    """A RefPack record: flags, 0xFB, the 3-byte big-endian length, then the command stream."""
    return bytes([header_flags, 0xFB]) + usize.to_bytes(3, "big") + stream


# "abc" as literals, then a 3-byte copy from 3 back: "abcabc". "cab" is only in the decompressed bytes.
ABCABC = packed(0x10, bytes([0x03, 0x02]) + b"abc" + bytes([0xFC]), 6)


def container(payloads: list[tuple[int, int, bytes, int]]) -> bytes:
    """A DBPF 2.0 file in the .nhd's shape: a 0x60 header, the payloads, then a flags word and 32-byte entries
    (type, group, instance lo, hi, offset, size | 0x80000000, usize, flags)."""
    body, entries, off = b"", [], 0x60
    for t, inst, blob, usize in payloads:
        entries.append(struct.pack("<8I", t, 0, inst & 0xFFFFFFFF, inst >> 32, off, len(blob) | 0x80000000, usize,
                                   0x10000))
        body += blob
        off += len(blob)
    index = struct.pack("<I", 0) + b"".join(entries)
    header = bytearray(0x60)
    header[0:4] = b"DBPF"
    struct.pack_into("<II", header, 4, 2, 0)
    struct.pack_into("<I", header, 0x24, len(payloads))
    struct.pack_into("<I", header, 0x2C, len(index))
    struct.pack_into("<I", header, 0x40, 0x60 + len(body))
    return bytes(header) + body + index


PAYLOADS = [
    (SIM, 0x0123456789ABCDEF, b"\x00\x01" + "Bella Goth".encode("utf-16-le") + b"\x00" * 6, 28),
    (OTHER, 7, ABCABC, 6),
    (SIM, 8, b"no names here, bella in lower case \xef\xcd\xab\x89", 40),
]


def run(save: Path, *needles: str, **kwargs) -> tuple[int, str]:
    lines: list[str] = []
    code = dg.grep(save, list(needles), out=lines.append, **kwargs)
    return code, "\n".join(lines)


class Forms(unittest.TestCase):
    def test_text_is_searched_as_utf8_and_utf16le_and_hex_as_written(self) -> None:
        self.assertEqual(dg.needle_forms("Bella"), [("utf-8", b"Bella"), ("utf-16le", "Bella".encode("utf-16-le"))])
        self.assertEqual(dg.needle_forms("hex:ef cd AB"), [("hex", b"\xef\xcd\xab")])
        for bad in ("hex:abc", "hex:", ""):
            with self.assertRaises(SystemExit):
                dg.needle_forms(bad)

    def test_refpack_records_are_decompressed_and_others_kept(self) -> None:
        self.assertEqual(dg.decompressed(ABCABC), (b"abcabc", "refpack"))
        self.assertEqual(dg.decompressed(b"plain"), (b"plain", "raw"))
        self.assertEqual(bytes(refpack(ABCABC[5:])), b"abcabc")
        broken = packed(0x10, bytes([0x83]), 9)  # a 3-byte copy command cut short
        self.assertEqual(dg.decompressed(broken), (broken, "undecodable"))

    def test_context_shows_the_hit_in_brackets(self) -> None:
        text = "xx Bella Goth".encode("utf-16-le")
        self.assertEqual(dg.context_line(text, 6, 10, "utf-16le", 4), "x [Bella] G")
        self.assertEqual(dg.context_line(b"..Bella..", 2, 5, "utf-8", 2), "..[Bella]..")
        self.assertEqual(dg.context_line(b"\x01\xef\xcd\x02", 1, 2, "hex", 1), "01[ef cd]02")


class Grep(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = Path(tempfile.mkdtemp(prefix="dbpf-grep-test-"))
        self.addCleanup(__import__("shutil").rmtree, self.tmp, True)
        self.save = self.tmp / "Option4BookChain.sims3"
        self.save.mkdir()
        (self.save / "Sunset Valley_0x0859db3c.nhd").write_bytes(container(PAYLOADS))
        (self.save / "1a5tf1l3.dat").write_bytes(b"Sunset Valley 0x0859db3c\r\n")
        (self.save / "simsonline-stock-boot.txt").write_text("Bella", encoding="utf-8")

    def test_the_real_locator_reads_the_synthetic_index(self) -> None:
        data = (self.save / "Sunset Valley_0x0859db3c.nhd").read_bytes()
        found = list(dg.records(data))
        self.assertEqual([(r.type, r.instance) for r in found], [(SIM, 0x0123456789ABCDEF), (OTHER, 7), (SIM, 8)])
        self.assertEqual(found[1].payload, b"abcabc")
        self.assertTrue(all(r.length_ok for r in found))

    def test_a_folder_searches_its_dbpf_files_only(self) -> None:
        self.assertEqual([p.name for p in dg.containers(self.save)], ["Sunset Valley_0x0859db3c.nhd"])

    def test_utf16_hits_name_the_record_and_its_family(self) -> None:
        code, out = run(self.save, "Bella")
        self.assertEqual(code, 0)
        self.assertIn("3 records (1 refpack, 2 raw, 0 undecodable)", out)
        self.assertIn("'Bella' utf-16le: 1 hit(s) in 1 record(s)", out)
        self.assertIn("0x01661233 SIM (SimDescriptions) 00000000:0123456789abcdef: 1 at 0x2", out)
        self.assertNotIn("'Bella' utf-8", out)  # the marker file holds it, but is not DBPF

    def test_a_back_reference_is_found_after_decompression(self) -> None:
        code, out = run(self.save, "cab")
        self.assertEqual(code, 0)
        self.assertIn("0x0badf00d 00000000:0000000000000007: 1 at 0x2", out)

    def test_ignore_case_hex_type_and_context(self) -> None:
        _, out = run(self.save, "BELLA", ignore_case=True, context=4)
        self.assertIn("'BELLA' utf-8: 1 hit(s)", out)
        self.assertIn("'BELLA' utf-16le: 1 hit(s)", out)
        self.assertIn("[bella]", out)
        _, out = run(self.save, "hex:efcdab89", context=1)
        self.assertIn("'hex:efcdab89' hex: 1 hit(s) in 1 record(s)", out)
        self.assertIn("20[ef cd ab 89]", out)
        _, out = run(self.save, "hex:EFCDAB89", "hex:efcdab89", ignore_case=True)
        self.assertIn("'hex:efcdab89' hex: 1 hit(s)", out)  # -i folds text only: hex bytes stay exact
        _, out = run(self.save, "hex:42", ignore_case=True)  # "B" of Bella: never matches the "b" of bella
        self.assertIn("'hex:42' hex: 1 hit(s) in 1 record(s)", out)
        code, out = run(self.save, "cab", types={SIM})
        self.assertEqual(code, 1)
        self.assertIn("2 of type 0x01661233", out)
        self.assertIn("no hits", out)

    def test_max_lists_fewer_records_but_counts_all(self) -> None:
        _, out = run(self.save, "e", max_records=1)
        self.assertIn("hit(s) in 2 record(s)", out)
        self.assertIn("... 1 more record(s) (--max)", out)

    def test_no_container_exits_2(self) -> None:
        empty = self.tmp / "empty.sims3"
        empty.mkdir()
        code, out = run(empty, "Bella")
        self.assertEqual(code, 2)
        self.assertIn("no DBPF file in it", out)

    def test_dbpf_dump_grep_runs_it(self) -> None:
        proc = subprocess.run([sys.executable, str(Path(dg.__file__).with_name("dbpf_dump.py")), "grep", str(self.save),
                               "cab"], capture_output=True, text=True, timeout=120)
        self.assertEqual(proc.returncode, 0, proc.stderr)
        self.assertIn("0x0badf00d", proc.stdout)
        with contextlib.redirect_stdout(io.StringIO()), self.assertRaises(SystemExit):
            dg.main([str(self.save), "x", "--type", "zz"])


if __name__ == "__main__":
    unittest.main()
