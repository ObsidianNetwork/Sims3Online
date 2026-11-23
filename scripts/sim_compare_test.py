"""python -m unittest scripts.sim_compare_test"""

from __future__ import annotations

import contextlib
import io
import sys
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import sim_compare  # noqa: E402


class Names:
    @staticmethod
    def name64(h) -> str:
        h = format(h, "016x") if isinstance(h, int) else h  # the caller now hands a hex-parsed int
        return {"e9a2077ed45629ff": "Idle"}.get(h, "#" + h)

    @staticmethod
    def name32(h: str) -> str:
        return {"1": "standing_idle", "2": "Standing"}.get(h, "#" + h)


class MachineRows(unittest.TestCase):
    def test_two_live_drivers_in_one_state_stay_two(self) -> None:
        out = "538082493|e9a2077ed45629ff|1|292|0|1\n539026009|e9a2077ed45629ff|1|300|0|1\n"
        self.assertEqual(sim_compare.machine_rows(out, Names), ["Idle:standing_idle x2"])

    def test_dead_and_cosmetic_rows_left_out(self) -> None:
        out = ("538082493|e9a2077ed45629ff|1|292|0|1\n"
               "539609478|e9a2077ed45629ff|2|100|0|0\n"  # dead: a replaced machine the recorder remembers
               "540000001|e9a2077ed45629ff|2|101|1|1\n"  # cosmetic layer
               "status=ok\n")
        self.assertEqual(sim_compare.machine_rows(out, Names), ["Idle:standing_idle"])

    def test_a_five_column_row_counts_as_live(self) -> None:
        self.assertEqual(sim_compare.machine_rows("538082493|e9a2077ed45629ff|2|292|0\n", Names), ["Idle:Standing"])

    def test_origins_of_live_drivers_only(self) -> None:
        out = ("538082493|e9a2077ed45629ff|1|292|0|1|replay\n"
               "539609478|e9a2077ed45629ff|2|100|0|0|game\n"  # dead
               "540000001|e9a2077ed45629ff|2|101|1|1|replay\n"  # cosmetic
               "539026009|e9a2077ed45629ff|1|300|0|1|game via Sim.Posture\n")
        self.assertEqual(sim_compare.origin_rows(out, Names), ["Idle:game via Sim.Posture", "Idle:replay"])
        self.assertEqual(sim_compare.origin_rows("538082493|e9a2077ed45629ff|1|292|0|1\n", Names), [])  # older mod


class SeveralClients(unittest.TestCase):
    """--clients 1,2,3: every player's game against one server read (the tools paired one client with the server)."""

    SERVER = {("Pablo", "pos"): ["1,0,1"], ("Pablo", "held"): ["-"]}
    READS = {"127.0.0.1:34030": {("Pablo", "pos"): ["1,0,1"], ("Pablo", "held"): ["-"]},
             "127.0.0.1:34031": {("Pablo", "pos"): ["9,0,9"], ("Pablo", "held"): ["Phone"]},
             "127.0.0.1:34032": {("Pablo", "pos"): ["1.1,0,1"], ("Pablo", "held"): ["-"]}}

    def run_main(self, argv: list[str]) -> tuple[int, str, list[str]]:
        read: list[str] = []

        def side(endpoint: str, probe: Path) -> dict:
            read.append(endpoint)
            return dict(self.SERVER if endpoint == sim_compare.ENDPOINTS["server"] else self.READS[endpoint])

        out = io.StringIO()
        with mock.patch.object(sim_compare, "run_side", side_effect=side), \
                mock.patch.dict(sim_compare.ENDPOINTS, {"client": "127.0.0.1:34030"}), \
                mock.patch.dict("os.environ", {"SIMSONLINE_CLIENT2_BRIDGECTL": "", "SIMSONLINE_CLIENT3_BRIDGECTL": ""}), \
                contextlib.redirect_stdout(out):
            code = sim_compare.main(["Pablo", *argv])
        return code, out.getvalue(), read

    def test_one_server_read_and_a_block_and_a_count_per_client(self) -> None:
        code, text, read = self.run_main(["--clients", "1,2,3"])
        self.assertEqual(code, 3)  # client 2 differs
        self.assertEqual(read, ["127.0.0.1:34029", "127.0.0.1:34030", "127.0.0.1:34031", "127.0.0.1:34032"])
        self.assertIn("### client 2 (127.0.0.1:34031)", text)
        self.assertTrue(text.rstrip().endswith("per client: client 1: 0 visible mismatch(es); client 2: 2 visible "
                                               "mismatch(es); client 3: 0 visible mismatch(es)"))

    def test_one_client_prints_as_before(self) -> None:
        code, text, read = self.run_main([])
        self.assertEqual(code, 0)
        self.assertEqual(read, ["127.0.0.1:34029", "127.0.0.1:34030"])
        self.assertNotIn("###", text)
        self.assertNotIn("per client", text)

    def test_client_and_clients_together_are_refused(self) -> None:
        with self.assertRaises(SystemExit), contextlib.redirect_stderr(io.StringIO()):
            sim_compare.main(["--client", "2", "--clients", "1,2"])



class KnownFieldTest(unittest.TestCase):
    """--known: a mismatch the scenario names as a known defect prints as k and is not counted (dj-booth, 2026-10-31:
    the client copy's stale BrowseTheWeb)."""

    SERVER = {("Jennifer", "interaction"): ["DoPlay"], ("Jennifer", "pos"): ["1,2,3"]}
    CLIENT = {("Jennifer", "interaction"): ["BrowseTheWeb"], ("Jennifer", "pos"): ["1,2,3"]}

    def report(self, known: set[str] | None) -> tuple[int, str]:
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            n = sim_compare.report(self.SERVER, self.CLIENT, known)
        return n, out.getvalue()

    def test_a_known_field_is_shown_and_not_counted(self) -> None:
        n, text = self.report({"interaction"})
        self.assertEqual(n, 0)
        self.assertIn(" k interaction", text)

    def test_without_it_the_field_counts(self) -> None:
        n, text = self.report(None)
        self.assertEqual(n, 1)
        self.assertIn(" ! interaction", text)


class MachinesConfirmTest(unittest.TestCase):
    """A machines difference is read again after CONFIRM_S: one gone by then (a release in flight between the games,
    shower 2026-10-31) is shown and not counted; one in both reads is compared and fails."""

    SERVER, CLIENT = "127.0.0.1:34029", "127.0.0.1:34030"

    def fields(self, reads: list[dict]) -> tuple[dict, dict, int]:
        server, client = {}, {}
        calls = iter(reads)
        sim_compare.machine_fields("Pablo", "1", [(server, self.SERVER), (client, self.CLIENT)],
                                   reader=lambda sim_id, endpoints: next(calls), wait=lambda s: None)
        with contextlib.redirect_stdout(io.StringIO()):
            n = sim_compare.report(server, client)
        return server, client, n

    def read(self, server_rows: list[str], client_rows: list[str]) -> dict:
        return {self.SERVER: (server_rows, []), self.CLIENT: (client_rows, [])}

    def test_a_difference_gone_on_the_second_read_is_shown_not_counted(self) -> None:
        server, client, n = self.fields([self.read(["Shower:Loop", "react:Exit"], ["Shower:Loop"]),
                                         self.read(["Shower:Loop"], ["Shower:Loop"])])
        self.assertEqual(n, 0)
        self.assertEqual(server[("Pablo", "machines (first read)")], ["Shower:Loop react:Exit"])
        self.assertEqual(client[("Pablo", "machines")], ["Shower:Loop"])

    def test_a_difference_in_both_reads_fails(self) -> None:
        _, _, n = self.fields([self.read(["Shower:Loop", "react:Exit"], ["Shower:Loop"]),
                               self.read(["Shower:Loop", "react:Exit"], ["Shower:Loop"])])
        self.assertEqual(n, 1)

    def test_equal_reads_are_read_once(self) -> None:
        server, _, n = self.fields([self.read(["Shower:Loop"], ["Shower:Loop"])])
        self.assertEqual(n, 0)
        self.assertNotIn(("Pablo", "machines (first read)"), server)

if __name__ == "__main__":
    unittest.main()
