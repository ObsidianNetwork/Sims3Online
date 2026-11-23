"""Unit tests for join_fingerprint.py: python -m unittest scripts.join_fingerprint_test"""
from __future__ import annotations

import contextlib
import io
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import join_fingerprint  # noqa: E402


def fields(**hashes: str) -> dict[tuple[str, str], list[str]]:
    """A probe reply as sim_compare.run_side gives it: {(part, field): [values]}."""
    out: dict[tuple[str, str], list[str]] = {("-", "scope"): ["households=2 lots=2"]}
    for part, count in (("objects", 412), ("inventories", 37), ("relationships", 96), ("world", 18350)):
        out[(part, "count")] = [str(count)]
        out[(part, "hash")] = [hashes.get(part, "00000000000000aa")]
        out[(part, "ms")] = ["3.40 5.10"]
    return out


class Probe(unittest.TestCase):
    def test_the_probe_names_its_sims_and_repeats(self) -> None:
        text = join_fingerprint.probe_text(["Jennifer", "Pablo"], 5)
        self.assertIn('Names = "Jennifer,Pablo"', text)
        self.assertIn("Repeat = 5;", text)
        self.assertNotIn("__NAMES__", text)
        self.assertNotIn("__REPEAT__", text)


class Parse(unittest.TestCase):
    def test_each_part_keeps_its_count_hash_and_times(self) -> None:
        scan = join_fingerprint.parse(fields())
        self.assertEqual(scan["scope"], "households=2 lots=2")
        self.assertEqual(scan["objects"], {"count": 412, "hash": "00000000000000aa", "ms": 3.4, "ms_max": 5.1})
        self.assertEqual(scan["world"]["count"], 18350)
        self.assertEqual(scan["errors"], [])

    def test_a_probe_error_is_kept_and_its_parts_left_out(self) -> None:
        scan = join_fingerprint.parse({("-", "error"): ["ok=false;err=NullReferenceException:x"]})
        self.assertEqual(scan["errors"], ["ok=false;err=NullReferenceException:x"])
        self.assertNotIn("objects", scan)


class Changed(unittest.TestCase):
    def test_only_the_fingerprint_parts_count_and_the_world_never(self) -> None:
        a = join_fingerprint.parse(fields())
        self.assertEqual(join_fingerprint.changed(a, join_fingerprint.parse(fields(world="ff"))), [])
        b = join_fingerprint.parse(fields(inventories="01", relationships="02"))
        self.assertEqual(join_fingerprint.changed(a, b), ["inventories", "relationships"])


LOG = """[world] checkpoint saved in 8901 ms after 0 not-ready refusal(s).
[world] join reuse: MISS (no-fingerprint-yet): the last save has no fingerprint; no scan; saved fresh | hits=0 misses=1 \
(no-fingerprint-yet=1 scope=0 objects=0 inventories=0 relationships=0 error=0) scan-ms last=2.3 max=2.3 save-ms \
last=8901 max=8901 saves-skipped=0 seconds-saved=0.0
[world] join reuse: this save is not kept for the next join (no fingerprint: no fingerprint taken).
[world] join reuse: HIT: nothing a joiner needs changed since the last save; scan 2.0 ms in the game, 51 ms in all; \
the last save served | hits=1 misses=1 (no-fingerprint-yet=1 scope=0 objects=0 inventories=0 relationships=0 error=0) \
scan-ms last=2.0 max=2.3 save-ms last=8901 max=8901 saves-skipped=1 seconds-saved=8.9
[world] join reuse: MISS (objects, inventories): objects 54 -> 55, inventories 28 -> 27; scan 3.1 ms in the game, \
49 ms in all; saved fresh | hits=1 misses=2 (no-fingerprint-yet=1 scope=0 objects=1 inventories=1 relationships=0 \
error=0) scan-ms last=3.1 max=3.1 save-ms last=11539 max=11539 saves-skipped=1 seconds-saved=8.9
"""


class Counters(unittest.TestCase):
    def test_each_join_line_keeps_its_outcome_reasons_and_counters(self) -> None:
        joins = join_fingerprint.join_lines(LOG)
        self.assertEqual([j["outcome"] for j in joins], ["MISS", "HIT", "MISS"])
        self.assertEqual(joins[0]["reasons"], ["no-fingerprint-yet"])
        self.assertEqual(joins[2]["reasons"], ["objects", "inventories"])
        last = joins[-1]
        self.assertEqual((last["hits"], last["misses"], last["saves_skipped"]), (1, 2, 1))
        self.assertEqual(last["by_reason"]["objects"], 1)
        self.assertEqual(last["save_ms"], ("11539", "11539"))
        self.assertAlmostEqual(last["seconds_saved"], 8.9)

    def test_the_command_prints_the_newest_join_or_says_there_is_none(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            log = Path(folder) / "server.log"
            log.write_text(LOG, encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()) as out:
                self.assertEqual(join_fingerprint.counters_cmd(log, every=False), 0)
            text = out.getvalue()
            self.assertIn("3 join(s) logged", text)
            self.assertIn("MISS (objects, inventories): objects 54 -> 55", text)
            self.assertNotIn("HIT", text)
            self.assertIn("seconds saved 8.9", text)
            log.write_text("[world] checkpoint saved in 8901 ms after 0 not-ready refusal(s).\n", encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()) as out:
                self.assertEqual(join_fingerprint.counters_cmd(log, every=False), 1)
            self.assertIn("no join reuse lines", out.getvalue())


class Scan(unittest.TestCase):
    def test_no_claim_blocks_before_the_probe_runs(self) -> None:
        with mock.patch.object(join_fingerprint.persist_cycle, "claim_check", return_value=(False, "no stack claim")), \
                mock.patch.object(join_fingerprint.sim_compare, "run_side", side_effect=AssertionError("the probe ran")), \
                contextlib.redirect_stdout(io.StringIO()) as out:
            code = join_fingerprint.scan_cmd("t", ["Jennifer"], 3)
        self.assertEqual(code, 2)
        self.assertIn("BLOCKED: no stack claim", out.getvalue())


if __name__ == "__main__":
    unittest.main()
