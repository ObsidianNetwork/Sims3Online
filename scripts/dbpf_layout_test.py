"""dbpf_layout.py: a synthetic DBPF-2.0 container written here word by word, then read back by the locator and parser.
No game files, no saves, no real containers."""
from __future__ import annotations

import struct
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import dbpf_layout  # noqa: E402

COMPRESSED = 0x80000000  # bit 31 of the compressed-size slot
FLAGS_WORD = 0x20000000  # any large dword here: a wrong slot read as a size then fails the file-size check


def container(index_start: int, records: list[tuple[int, ...]], entry_len: int, fsize: int) -> bytes:
    """Header + index at index_start + payload. The unwritten payload is 0xFF, so no dword in it passes as a span."""
    data = bytearray(b"\xff" * fsize)
    for off in range(0x00, 0x40, 4):
        data[off:off + 4] = b"\x00\x00\x00\x00"
    data[0x00:0x04] = b"DBPF"
    data[0x10:0x14] = struct.pack("<I", len(records))
    for i, record in enumerate(records):
        base = index_start + i * entry_len
        for j, word in enumerate(record):
            data[base + 4 * j:base + 4 * j + 4] = struct.pack("<I", word)
    return bytes(data)


# Sunset Valley .nhd / ExportDB order: type, group, iid_lo, iid_hi, off, csize, usize, flags
NHD = [
    (0x01A0E800, 0, 1, 0xD1C0FFEE, 0x100, COMPRESSED | 0x80, 0x100, FLAGS_WORD),
    (0x01A0E800, 0, 2, 0xBEEFB002, 0x180, COMPRESSED | 0x40, 0x80, FLAGS_WORD),
    (0x02A40A32, 0, 3, 0xABCD0003, 0x1C0, COMPRESSED | 0x40, 0x80, FLAGS_WORD),
]
NHD_BLOB = container(0x40, NHD, 32, 0x200)
NHD_LAYOUT = {"count": 3, "entry_len": 32, "start": 0x40, "type_off": 0,
              "off_off": 16, "csize_off": 20, "usize_off": 24, "end_index": 0xA0}

# TravelDB order: type, iid, off, csize, usize, flags
TRAVEL = [
    (0x04A9BEEF, 7, 0x100, COMPRESSED | 0x60, 0xC0, FLAGS_WORD),
    (0x04A9BEEF, 8, 0x160, COMPRESSED | 0x40, 0x80, FLAGS_WORD),
]
TRAVEL_BLOB = container(0x40, TRAVEL, 24, 0x1A0)
TRAVEL_LAYOUT = {"count": 2, "entry_len": 24, "start": 0x40, "type_off": 0,
                 "off_off": 8, "csize_off": 12, "usize_off": 16, "end_index": 0x70}


class LocateIndex(unittest.TestCase):
    def test_the_32_byte_index_is_found_where_it_was_written(self) -> None:
        self.assertEqual(dbpf_layout.locate_index(NHD_BLOB), NHD_LAYOUT)

    def test_the_24_byte_traveldb_index_is_found(self) -> None:
        self.assertEqual(dbpf_layout.locate_index(TRAVEL_BLOB), TRAVEL_LAYOUT)

    def test_a_container_without_an_index_returns_none(self) -> None:
        self.assertIsNone(dbpf_layout.locate_index(bytes(0x100)))
        self.assertIsNone(dbpf_layout.locate_index(b"\xff" * 0x100))


class ParseRecords(unittest.TestCase):
    def test_the_32_byte_entries_read_back_every_field(self) -> None:
        self.assertEqual(list(dbpf_layout.parse_records(NHD_BLOB, NHD_LAYOUT)), [
            (0x01A0E800, 0, 1, 0xD1C0FFEE, 0x100, 0x80, 0x100, FLAGS_WORD),
            (0x01A0E800, 0, 2, 0xBEEFB002, 0x180, 0x40, 0x80, FLAGS_WORD),
            (0x02A40A32, 0, 3, 0xABCD0003, 0x1C0, 0x40, 0x80, FLAGS_WORD),
        ])

    def test_the_24_byte_entries_carry_only_a_32_bit_instance(self) -> None:
        self.assertEqual(list(dbpf_layout.parse_records(TRAVEL_BLOB, TRAVEL_LAYOUT)), [
            (0x04A9BEEF, 0, 7, 0, 0x100, 0x60, 0xC0, FLAGS_WORD),
            (0x04A9BEEF, 0, 8, 0, 0x160, 0x40, 0x80, FLAGS_WORD),
        ])

    def test_bit_31_is_masked_out_of_the_compressed_size(self) -> None:
        layout = dbpf_layout.locate_index(NHD_BLOB)
        self.assertEqual([r[5] for r in dbpf_layout.parse_records(NHD_BLOB, layout)], [0x80, 0x40, 0x40])

    def test_the_locator_and_the_parser_agree_on_the_record_spans(self) -> None:
        layout = dbpf_layout.locate_index(TRAVEL_BLOB)
        for _t, _g, _lo, _hi, off, csz, _usz, _fl in dbpf_layout.parse_records(TRAVEL_BLOB, layout):
            self.assertLessEqual(off + csz, len(TRAVEL_BLOB))
            self.assertGreaterEqual(off, 0x60)


if __name__ == "__main__":
    unittest.main()
