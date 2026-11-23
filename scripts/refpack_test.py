"""refpack.py: streams hand-built byte by byte, one case per opcode class in the module docstring's table, and strings().
No game, no files."""
from __future__ import annotations

import string
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import refpack  # noqa: E402


def letters(n: int) -> bytes:
    return (string.ascii_uppercase * (n // 26 + 1))[:n].encode()


def literal_run(payload: bytes) -> bytes:
    """The 1-byte run opcodes that carry payload: 0xE0 covers 4 bytes each."""
    if len(payload) % 4:
        raise ValueError("literal runs are multiples of 4")
    return b"".join(b"\xE0" + payload[i:i + 4] for i in range(0, len(payload), 4))


class LiteralRun(unittest.TestCase):
    """0xE0-0xFB (1B): plain = ((b0 & 0x1F) + 1) * 4 bytes follow the opcode."""

    def test_empty_stream_decodes_to_nothing(self) -> None:
        self.assertEqual(refpack.refpack(b""), b"")

    def test_shortest_run_4_bytes(self) -> None:
        self.assertEqual(refpack.refpack(b"\xE0ABCD"), b"ABCD")

    def test_run_of_8_bytes(self) -> None:
        self.assertEqual(refpack.refpack(b"\xE1ABCDEFGH"), b"ABCDEFGH")

    def test_longest_run_112_bytes(self) -> None:
        payload = letters(112)
        self.assertEqual(refpack.refpack(b"\xFB" + payload), payload)

    def test_two_runs_in_a_row(self) -> None:
        self.assertEqual(refpack.refpack(b"\xE0ABCD\xE1EFGHIJKL"), b"ABCDEFGHIJKL")


class TrailingLiterals(unittest.TestCase):
    """0xFC-0xFF (1B): plain = b0 & 3 bytes follow the opcode; the opcode itself is not a stop."""

    def test_one_trailing_byte(self) -> None:
        self.assertEqual(refpack.refpack(b"\xE0ABCD\xFDH"), b"ABCDH")

    def test_two_trailing_bytes(self) -> None:
        self.assertEqual(refpack.refpack(b"\xE0ABCD\xFE12"), b"ABCD12")

    def test_three_trailing_bytes(self) -> None:
        self.assertEqual(refpack.refpack(b"\xE0ABCD\xFF123"), b"ABCD123")


class ShortCopy(unittest.TestCase):
    """0x00-0x7F (2B): plain = b0 & 3, copy = ((b0 & 0x1C) >> 2) + 3, off = ((b0 & 0x60) << 3) + b1, then + 1 back."""

    def test_copy_of_3_from_4_bytes_back(self) -> None:
        self.assertEqual(refpack.refpack(b"\xE1ABCDEFGH\x00\x03"), b"ABCDEFGHEFG")

    def test_plain_literals_are_appended_before_the_copy(self) -> None:
        self.assertEqual(refpack.refpack(b"\xE1ABCDEFGH\x01\x04Z"), b"ABCDEFGHZEFG")

    def test_overlapping_copy_reads_the_bytes_it_appends(self) -> None:
        # 0x0C: ((0x0C & 0x1C) >> 2) + 3 = 6 bytes from (2) + 1 = 3 back, longer than the gap, so "ABCD" repeats
        self.assertEqual(refpack.refpack(b"\xE0ABCD\x0C\x02"), b"ABCDBCDBCD")

    def test_offset_bit_5(self) -> None:
        # 0x20: ((0x20 & 0x60) << 3) | 0x07 = 0x107 -> 264 back -> the first 3 literals of a 264-byte run
        payload = letters(264)
        self.assertEqual(refpack.refpack(literal_run(payload) + b"\x20\x07"), payload + payload[:3])

    def test_offset_bits_5_and_6(self) -> None:
        # 0x60: ((0x60 & 0x60) << 3) | 0x07 = 0x307 -> 776 back -> the first 3 literals of a 776-byte run
        payload = letters(776)
        self.assertEqual(refpack.refpack(literal_run(payload) + b"\x60\x07"), payload + payload[:3])


class MediumCopy(unittest.TestCase):
    """0x80-0xBF (3B): plain = (b1 >> 6) & 3, copy = (b0 & 0x3F) + 4, off = ((b1 & 0x3F) << 8) + b2, then + 1 back."""

    def test_copy_of_4_from_5_bytes_back(self) -> None:
        self.assertEqual(refpack.refpack(b"\xE1ABCDEFGH\x80\x00\x04"), b"ABCDEFGHDEFG")

    def test_plain_count_lives_in_the_second_byte(self) -> None:
        self.assertEqual(refpack.refpack(b"\xE1ABCDEFGH\x80\x40\x04Z"), b"ABCDEFGHZEFGH")

    def test_longest_copy_67_bytes_overlapping(self) -> None:
        # 0xBF = 63 + 4 = 67 bytes from (0 << 8 | 0) + 1 = 1 back: the last literal repeats
        self.assertEqual(refpack.refpack(b"\xFB" + b"L" * 112 + b"\xBF\x00\x00"), b"L" * 179)

    def test_offset_uses_the_high_bits_of_the_second_byte(self) -> None:
        # b1 = 0x01, b2 = 0x07 -> ((1 << 8) | 7) + 1 = 264 back over a 264-byte run, 4 bytes
        payload = letters(264)
        self.assertEqual(refpack.refpack(literal_run(payload) + b"\x80\x01\x07"), payload + payload[:4])


class LongCopy(unittest.TestCase):
    """0xC0-0xDF (4B): plain = b0 & 3, copy = ((b0 & 0x0C) << 6) + b3 + 5, off = ((b0 & 0x10) << 12) + (b1 << 8) + b2 + 1."""

    def test_copy_of_5_from_4_bytes_back(self) -> None:
        self.assertEqual(refpack.refpack(b"\xE1ABCDEFGH\xC0\x00\x03\x00"), b"ABCDEFGHEFGHE")

    def test_plain_literals_are_appended_before_the_copy(self) -> None:
        self.assertEqual(refpack.refpack(b"\xE1ABCDEFGH\xC3\x00\x03\x00XYZ"), b"ABCDEFGHXYZHXYZH")

    def test_extended_offset_and_length_bits(self) -> None:
        # 0xD8: (0x10 << 12) sets the 64K offset bit and (0x08 << 6) the 512 length bit
        # -> ((0x10 << 12) | (1 << 8) | 3) + 1 = 65796 back, for (0x08 << 6) + 0 + 5 = 517 bytes
        payload = letters(65796)
        self.assertEqual(refpack.refpack(literal_run(payload) + b"\xD8\x01\x03\x00"), payload + payload[:517])


class OneStreamEveryClass(unittest.TestCase):
    def test_the_opcodes_chain(self) -> None:
        stream = (b"\xE0ABCD"          # run: ABCD
                  b"\x0C\x02"          # 2B short copy, 6 bytes from 3 back (overlapping) -> BCDBCD
                  b"\x80\x00\x09"      # 3B medium copy, 4 bytes from 10 back -> the first four literals
                  b"\xC0\x00\x0D\x01"  # 4B long copy, (0 << 6) + 1 + 5 = 6 bytes from 14 back
                  b"\xE0ZZZZ"          # run: ZZZZ
                  b"\xFDQ")            # 1B trailing literals: one literal
        self.assertEqual(refpack.refpack(stream), b"ABCDBCDBCDABCDABCDBCZZZZQ")


class ModuleTableVersusImplementation(unittest.TestCase):
    """The table's two 1-byte rows are what refpack() does: 0xFC-0xFF carry trailing literals,
    and nothing ends the stream but its length."""

    def test_0xfc_is_a_trailing_literal_opcode_and_not_a_116_byte_run(self) -> None:
        self.assertEqual(refpack.refpack(b"\xE0ABCD\xFC"), b"ABCD")

    def test_no_opcode_stops_the_stream(self) -> None:
        self.assertEqual(refpack.refpack(b"\xFD" + b"A" + b"\xE0BCDE"), b"ABCDE")


class Strings(unittest.TestCase):
    def test_empty_blob(self) -> None:
        self.assertEqual(refpack.strings(b""), [])

    def test_ascii_runs_of_at_least_min_len(self) -> None:
        blob = b"hi SimObjectData\x00LootTable00\x00no"
        self.assertEqual(refpack.strings(blob), ["hi SimObjectData", "LootTable00"])

    def test_a_run_at_the_end_of_the_blob_is_kept(self) -> None:
        self.assertEqual(refpack.strings(b"\x00\x00MetadataBlock"), ["MetadataBlock"])

    def test_high_bytes_split_a_run(self) -> None:
        self.assertEqual(refpack.strings(b"Sim\xff\xfeObjectDataMore"), ["ObjectDataMore"])

    def test_utf16le_runs(self) -> None:
        self.assertEqual(refpack.strings("SimDescription".encode("utf-16-le")), ["SimDescription"])

    def test_min_len_counts_utf16_characters_not_bytes(self) -> None:
        blob = "abc".encode("utf-16-le")  # 3 characters, 6 bytes
        self.assertEqual(refpack.strings(blob, min_len=3), ["abc"])
        self.assertEqual(refpack.strings(blob, min_len=4), [])

    def test_min_len_can_be_lowered(self) -> None:
        self.assertEqual(refpack.strings(b"Sim\xffObject", min_len=3), ["Sim", "Object"])


if __name__ == "__main__":
    unittest.main()
