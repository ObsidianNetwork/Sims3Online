"""sacs_names.py: the docstring's hash and flag examples, and the name lookups with _load patched to a small table.
No game names beyond the docstring's examples; no .omo/cache, no decompile, no files."""
from __future__ import annotations

import sys
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import late_join  # noqa: E402  # the other name64 caller: its summary prints "?" for a machine it cannot read
import sacs_names  # noqa: E402
import stream_log  # noqa: E402  # a name64 caller: the stream A| line's machine field is a decimal key

PHONE = 0x8C544A4837C2F1EF  # the docstring's "phone" state-machine key
X_DECIMAL = 84696423  # the docstring's "x" hash, as printed there (decimal)
BROWSE_DECIMAL = 1552495694  # the docstring's "BrowseTheWeb" hash, decimal
DEMO_KEY = 1234567890123456  # 16 decimal digits: also a valid hex literal, which the lookups have to tell apart
TABLE = {
    "64": {format(PHONE, "016x"): "phone", format(DEMO_KEY, "016x"): "demo.machine"},
    "32": {str(X_DECIMAL): "x", str(BROWSE_DECIMAL): "BrowseTheWeb"},
}


class Hashes(unittest.TestCase):
    def test_fnv64_of_the_docstring_example(self) -> None:
        self.assertEqual(sacs_names.fnv64("phone"), PHONE)
        self.assertEqual(format(sacs_names.fnv64("phone"), "016x"), "8c544a4837c2f1ef")

    def test_fnv32_of_the_docstring_examples(self) -> None:
        self.assertEqual(sacs_names.fnv32("x"), X_DECIMAL)
        self.assertEqual(sacs_names.fnv32("BrowseTheWeb"), BROWSE_DECIMAL)

    def test_both_hashes_fold_case(self) -> None:
        self.assertEqual(sacs_names.fnv32("BrowseTheWeb"), sacs_names.fnv32("browsetheweb"))
        self.assertEqual(sacs_names.fnv64("Phone"), sacs_names.fnv64("phone"))

    def test_empty_text_hashes_to_the_offset_basis(self) -> None:
        self.assertEqual(sacs_names.fnv64(""), 0xCBF29CE484222325)
        self.assertEqual(sacs_names.fnv32(""), 0x811C9DC5)

    def test_the_hash_stays_inside_its_width(self) -> None:
        self.assertLess(sacs_names.fnv64("x" * 64), 1 << 64)
        self.assertLess(sacs_names.fnv32("x" * 64), 1 << 32)


class Flags(unittest.TestCase):
    def test_the_docstring_expansion_of_98(self) -> None:
        self.assertEqual(sacs_names.flags(98), "Interrupt|RequestWaitOnBridge|RequestEmulateEdith")

    def test_zero_has_no_name(self) -> None:
        self.assertEqual(sacs_names.flags(0), "0")

    def test_single_bits(self) -> None:
        self.assertEqual(sacs_names.flags(1), "ClearQueue")
        self.assertEqual(sacs_names.flags(0x800), "RequestInterruptThisMachineOnly")

    def test_names_keep_the_order_of_the_flag_table(self) -> None:
        self.assertEqual(sacs_names.flags(0x20 | 0x80), "RequestWaitOnBridge|RequestAllowDuplicateRequest")

    def test_a_string_value_reads_like_the_number(self) -> None:
        self.assertEqual(sacs_names.flags("98"), sacs_names.flags(98))

    def test_request_bits_alone_have_no_priority_name(self) -> None:
        self.assertNotIn("Priority", sacs_names.flags(0xFFF))

    def test_a_bit_above_the_request_bits_is_named(self) -> None:
        self.assertIn("Priority", sacs_names.flags(1 << 12))


class Names(unittest.TestCase):
    def setUp(self) -> None:
        patcher = mock.patch.object(sacs_names, "_load", return_value=TABLE)
        self.addCleanup(patcher.stop)
        patcher.start()

    def test_name64_by_hex_key(self) -> None:
        self.assertEqual(sacs_names.name64("8c544a4837c2f1ef"), "phone")

    def test_name64_takes_uppercase_hex_and_the_number(self) -> None:
        self.assertEqual(sacs_names.name64("8C544A4837C2F1EF"), "phone")
        self.assertEqual(sacs_names.name64(PHONE), "phone")

    def test_an_unknown_name64_is_the_zero_padded_hex_key(self) -> None:
        self.assertEqual(sacs_names.name64(255), "00000000000000ff")

    def test_text_that_is_not_a_number_comes_back_unchanged(self) -> None:
        self.assertEqual(sacs_names.name64("phone"), "phone")

    def test_name32_by_decimal_hash(self) -> None:
        self.assertEqual(sacs_names.name32(X_DECIMAL), "x")
        self.assertEqual(sacs_names.name32(str(BROWSE_DECIMAL)), "BrowseTheWeb")

    def test_name32_of_the_32_bit_number_in_hex_form(self) -> None:
        self.assertEqual(sacs_names.name32(sacs_names.fnv32("BrowseTheWeb")), "BrowseTheWeb")

    def test_an_unknown_name32_is_the_number(self) -> None:
        self.assertEqual(sacs_names.name32(1), "1")

    def test_name32_passes_through_text(self) -> None:
        self.assertEqual(sacs_names.name32("BrowseTheWeb"), "BrowseTheWeb")

    def test_name64_reads_a_16_digit_decimal_key_as_a_decimal(self) -> None:
        self.assertEqual(sacs_names.name64(str(DEMO_KEY)), "demo.machine")


class StubNames:
    """A sacs_names stand-in for the two callers, recording every key handed to name64()."""

    def __init__(self, seen: dict | None = None) -> None:
        self.seen = {} if seen is None else seen

    def name64(self, key):
        self.seen["machine"] = key
        return "demo.machine"

    def name32(self, value):
        return str(value)

    def flags(self, value):
        return str(value)


class MachineFieldBase(unittest.TestCase):
    def test_decode_hands_the_decimal_machine_to_name64_as_an_int(self) -> None:
        seen = {}

        # The stream A| line's machine field f[6] is SacsRecorder's U(ulong) (decimal), so decode has to hand name64()
        # the decimal int; the raw string is also a valid hex literal, which name64() would otherwise read as decimal.
        line = f"0|A|538794754|18031287978496800272|1|2|{DEMO_KEY}|7"
        with mock.patch.object(stream_log, "names", StubNames(seen)):
            stream_log.decode(line)
        self.assertEqual(seen["machine"], DEMO_KEY)

    def test_a_garbled_machine_field_does_not_end_the_stream_read(self) -> None:
        # A truncated A| line leaves a hex-typed key in field 6, where int() raises ValueError and used to kill the
        # whole driver table; the raw text now stands in for the name.
        line = "0|A|538794754|18031287978496800272|1|2|8C544A|7"
        seen: dict = {}
        with mock.patch.object(stream_log, "names", StubNames(seen)):
            self.assertEqual(stream_log.machine_of(line.split("|")), "8C544A")
            self.assertEqual(stream_log.machine_of(f"0|A|538794754|18|1|2|{DEMO_KEY}|7".split("|")), "demo.machine")
        self.assertEqual(seen, {"machine": DEMO_KEY}, "the decimal key is what the good line hands name64()")

    def test_a_garbled_machine_field_prints_a_question_mark_in_late_join(self) -> None:
        good = f"0|A|538794754|18031287978496800272|1|2|{DEMO_KEY}|7"
        with mock.patch.object(late_join, "names", StubNames()):
            self.assertEqual(late_join.machine_of(good.split("|")), "demo.machine")
            self.assertEqual(late_join.machine_of(good.replace(str(DEMO_KEY), "8C544A").split("|")), "?")
            self.assertEqual(late_join.machine_of(good.split("|")[:6]), "?")


if __name__ == "__main__":
    unittest.main()
