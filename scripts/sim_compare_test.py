"""python -m unittest scripts.sim_compare_test"""

from __future__ import annotations

import contextlib
import io
import json
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


class LandingTest(unittest.TestCase):
    """Card 55 E5: whole millimetres, server read around both client reads, 0.10 m."""

    def test_read_mm_takes_the_named_sims_posmm_field(self) -> None:
        side = {("Jennifer", "posmm"): ["1234,-5,678"], ("Jennifer", "pos"): ["1.23,0.00,0.68"]}
        self.assertEqual(sim_compare.read_mm("server", "jennifer", reader=lambda endpoint: side), [1234, -5, 678])
        for bad in ({}, {("Jennifer", "posmm"): ["1.2,3,4"]}, {("Jennifer", "posmm"): ["1,2,3", "1,2,3"]}):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                sim_compare.read_mm("server", "Jennifer", reader=lambda endpoint, side=bad: side)

    def test_landing_passes_within_100_mm_and_fails_beyond(self) -> None:
        spots = {"server": [[0, 0, 0], [0, 0, 0]], "c1": [[60, 0, 80]], "c2": [[0, 0, 101]]}

        def read(endpoint, _sim):
            return spots[endpoint].pop(0)

        result = sim_compare.landing("Jennifer", "server", ["c1", "c2"], read=read)
        self.assertEqual(result, {"server_steady": True, "errors_mm": {"c1": 100, "c2": 101}, "verdict": "FAIL"})

    def test_a_moving_server_scores_nothing(self) -> None:
        spots = {"server": [[0, 0, 0], [0, 0, 40]], "c1": [[0, 0, 10]]}
        result = sim_compare.landing("Jennifer", "server", ["c1"], read=lambda e, s: spots[e].pop(0))
        self.assertEqual(result["verdict"], "BLOCKED")
        self.assertFalse(result["server_steady"])

    def test_posmm_is_not_a_side_by_side_field(self) -> None:
        with contextlib.redirect_stdout(io.StringIO()) as out:
            n = sim_compare.report({("Jennifer", "posmm"): ["1,2,3"]}, {("Jennifer", "posmm"): ["4,5,6"]})
        self.assertEqual(n, 0)
        self.assertNotIn("posmm", out.getvalue())


class SettledMmAndE5BracketTest(unittest.TestCase):
    """Card 55 E5 settled-bracket millimetre mode, concurrency ordering, fail-closed missing Sim, and Wave 13 parity."""

    ACTOR = 18031287978496800304

    def _scope(self) -> dict:
        return dict(run_id="r1", boot_id="b1", server_pid=42, epoch=2, corr=1,
                    actor=self.ACTOR, generation=3, owner="player1", start_ns=100, end_ns=200,
                    bridge_connected=True, claim_held=True, instrument_ready=True,
                    coverage_start_ns=100, coverage_end_ns=200, collector_status="complete")

    def _side(self, pos_mm: str, *, actor: int | None = ACTOR, missing: bool = False, error: str = "") -> dict:
        if error:
            return {("-", "error"): [error]}
        if missing:
            return {("Jennifer", "missing"): ["1"]}
        out = {("Jennifer", "posmm"): [pos_mm], ("Jennifer", "pos"): ["0.00,0.00,0.00"], ("Jennifer", "interaction"): ["-"]}
        if actor is not None:
            out[("Jennifer", "simid")] = [str(actor)]
        return out

    def _client_page(self, *, epoch: int = 2, corr: int = 1, actor: int = ACTOR, phase: str = "Halted") -> bytes:
        return (
            f"rust-route-client=page;epoch={epoch};corr={corr};actor={actor};"
            f"legs_total=2;received_legs=2;executed_count=2;cursor=0;count=2;next=2;more=0;"
            f"WalkPhase={phase};invalid_progressions=0;executed_slice=[0,1]"
        ).encode("ascii")

    def _admin_factory(self, phases: dict[str, str] | str = "Halted", *,
                       epoch: int = 2, corr: int = 1, actor: int = ACTOR):
        def factory(ep: str):
            ph = phases[ep] if isinstance(phases, dict) else phases
            adm = mock.Mock()
            body = (b"rust-route-client=missing" if ph == "missing"
                    else self._client_page(epoch=epoch, corr=corr, actor=actor, phase=ph))
            adm.plan_page.return_value = lambda cursor, req_epoch, b=body: b
            adm.request.return_value = body.decode("ascii")
            return adm
        return factory

    def test_pass_at_0mm_and_exact_100mm_boundary(self) -> None:
        for c1, c2, expected_err in (("0,0,0", "0,0,0", 0), ("60,0,80", "-60,0,-80", 100)):
            reads = {
                "127.0.0.1:34029": [self._side("0,0,0"), self._side("0,0,0")],
                "127.0.0.1:34030": [self._side(c1)],
                "127.0.0.1:34031": [self._side(c2)],
            }
            res = sim_compare.sample_e5_bracket(
                "Jennifer",
                scope=self._scope(),
                side_reader=lambda ep, _probe, r=reads: r[ep].pop(0),
                admin_factory=self._admin_factory({"127.0.0.1:34030": "Halted", "127.0.0.1:34031": "SeatingSettled"}),
            )
            self.assertEqual(res["verdict"], "PASS")
            self.assertEqual(res["errors_mm"]["client1"], expected_err)
            self.assertEqual(res["errors_mm"]["client2"], expected_err)

    def test_fail_just_above_100mm_and_vertical_only_101mm(self) -> None:
        # [60, 1, 80] -> 100.004999875 mm -> ceil == 101 mm -> FAIL; vertical [0, 101, 0] -> 101 mm -> FAIL
        for pos in ("60,1,80", "0,101,0"):
            reads = {
                "127.0.0.1:34029": [self._side("0,0,0"), self._side("0,0,0")],
                "127.0.0.1:34030": [self._side(pos)],
                "127.0.0.1:34031": [self._side("0,0,0")],
            }
            res = sim_compare.sample_e5_bracket(
                "Jennifer",
                scope=self._scope(),
                side_reader=lambda ep, _probe, r=reads: r[ep].pop(0),
                admin_factory=self._admin_factory("Halted"),
            )
            self.assertEqual(res["verdict"], "FAIL")
            self.assertEqual(res["errors_mm"]["client1"], 101)

    def test_failure_precedence_settled_fail_outranks_unsettled_second_client(self) -> None:
        reads = {
            "127.0.0.1:34029": [self._side("0,0,0"), self._side("0,0,0")],
            "127.0.0.1:34030": [self._side("0,101,0")],
            "127.0.0.1:34031": [self._side("0,0,0")],
        }
        res = sim_compare.sample_e5_bracket(
            "Jennifer",
            scope=self._scope(),
            side_reader=lambda ep, _probe, r=reads: r[ep].pop(0),
            admin_factory=self._admin_factory({"127.0.0.1:34030": "Halted", "127.0.0.1:34031": "missing"}),
        )
        self.assertEqual(res["verdict"], "FAIL")

    def test_blocked_when_client_unsettled_or_missing_receipt_never_fabricates_halted(self) -> None:
        # Empty InteractionQueue ('-') on client must NOT fabricate 'Halted' when ClientBoundaryRoute has no settled receipt.
        fake_admin = mock.Mock()
        fake_admin.plan_page.return_value = lambda cursor, epoch: b"rust-route-client=missing"
        fake_admin.request.return_value = "rust-route-client=missing;epoch=2;fenced=0;open=0"
        self.assertEqual(
            sim_compare.read_client_walk_phase("127.0.0.1:34030", corr=1, actor=self.ACTOR, epoch=2,
                                               admin_factory=lambda _ep: fake_admin),
            "Unmeasured",
        )
        reads = {
            "127.0.0.1:34029": [self._side("0,0,0"), self._side("0,0,0")],
            "127.0.0.1:34030": [self._side("0,0,0")],
            "127.0.0.1:34031": [self._side("0,0,0")],
        }
        res = sim_compare.sample_e5_bracket(
            "Jennifer",
            scope=self._scope(),
            side_reader=lambda ep, _probe, r=reads: r[ep].pop(0),
            admin_factory=lambda _ep: fake_admin,
        )
        self.assertEqual(res["verdict"], "BLOCKED")

    def test_blocked_when_server_moving_even_if_client_exceeds_100mm(self) -> None:
        reads = {
            "127.0.0.1:34029": [self._side("0,0,0"), self._side("0,0,5")],
            "127.0.0.1:34030": [self._side("500,0,0")],
            "127.0.0.1:34031": [self._side("0,0,0")],
        }
        res = sim_compare.sample_e5_bracket(
            "Jennifer",
            scope=self._scope(),
            side_reader=lambda ep, _probe, r=reads: r[ep].pop(0),
            phase_reader=lambda _ep: "Halted",
        )
        self.assertEqual(res["verdict"], "BLOCKED")
        self.assertFalse(res["server_steady"])

    def test_concurrency_and_bracket_order_without_sleeps(self) -> None:
        import threading
        events: list[str] = []
        server_before_done = threading.Event()
        both_clients_barrier = threading.Barrier(2, timeout=5.0)
        clients_finished = threading.Event()
        c_done_count = [0]
        lock = threading.Lock()

        def side_reader(ep: str, _probe: Path) -> dict:
            if ep == "127.0.0.1:34029":
                if not server_before_done.is_set():
                    events.append("server_before")
                    server_before_done.set()
                    return self._side("1000,2000,3000")
                self.assertTrue(clients_finished.is_set(), "server_after started before both clients finished")
                events.append("server_after")
                return self._side("1000,2000,3000")
            self.assertTrue(server_before_done.is_set(), "client read started before server_before finished")
            both_clients_barrier.wait()  # proves client1 and client2 execute concurrently
            with lock:
                events.append(ep)
                c_done_count[0] += 1
                if c_done_count[0] == 2:
                    clients_finished.set()
            return self._side("1060,2000,3080")

        res = sim_compare.sample_e5_bracket(
            "Jennifer",
            scope=self._scope(),
            side_reader=side_reader,
            phase_reader=lambda _ep: "Halted",
        )
        self.assertEqual(res["verdict"], "PASS")
        self.assertEqual(events[0], "server_before")
        self.assertEqual(set(events[1:3]), {"127.0.0.1:34030", "127.0.0.1:34031"})
        self.assertEqual(events[3], "server_after")

    def test_fail_closed_on_missing_sim_probe_error_actor_mismatch_and_non_int(self) -> None:
        # Missing Sim on all 3 endpoints must be BLOCKED, never PASS!
        all_missing = {
            "127.0.0.1:34029": [self._side("0,0,0", missing=True), self._side("0,0,0", missing=True)],
            "127.0.0.1:34030": [self._side("0,0,0", missing=True)],
            "127.0.0.1:34031": [self._side("0,0,0", missing=True)],
        }
        self.assertEqual(
            sim_compare.sample_e5_bracket("Jennifer", scope=self._scope(),
                                          side_reader=lambda ep, _p, r=all_missing: r[ep].pop(0),
                                          phase_reader=lambda _ep: "Halted")["verdict"],
            "BLOCKED",
        )
        # Probe error
        err_reads = {
            "127.0.0.1:34029": [self._side("0,0,0", error="ok=false;err=Crash"), self._side("0,0,0")],
            "127.0.0.1:34030": [self._side("0,0,0")],
            "127.0.0.1:34031": [self._side("0,0,0")],
        }
        self.assertEqual(
            sim_compare.sample_e5_bracket("Jennifer", scope=self._scope(),
                                          side_reader=lambda ep, _p, r=err_reads: r[ep].pop(0),
                                          phase_reader=lambda _ep: "Halted")["verdict"],
            "BLOCKED",
        )
        # Actor ID mismatch
        mismatch_reads = {
            "127.0.0.1:34029": [self._side("0,0,0"), self._side("0,0,0")],
            "127.0.0.1:34030": [self._side("0,0,0", actor=999)],
            "127.0.0.1:34031": [self._side("0,0,0")],
        }
        self.assertEqual(
            sim_compare.sample_e5_bracket("Jennifer", scope=self._scope(),
                                          side_reader=lambda ep, _p, r=mismatch_reads: r[ep].pop(0),
                                          phase_reader=lambda _ep: "Halted")["verdict"],
            "BLOCKED",
        )
        # Non-int (bool / float) coordinate
        for bad_coord in ([True, 0, 0], [1.5, 0, 0], "1.5,0,0"):
            with self.assertRaises(ValueError):
                sim_compare.parse_sim_mm({("Jennifer", "posmm"): [bad_coord]}, "Jennifer")

    def test_blocked_on_endpoint_actor_mismatch_and_unattributed_receipts_without_actor_id(self) -> None:
        # (a) Server simid=123 vs clients simid=456 when --actor-id / scope actor is omitted -> BLOCKED
        mismatched_endpoints = {
            "127.0.0.1:34029": [self._side("0,0,0", actor=123), self._side("0,0,0", actor=123)],
            "127.0.0.1:34030": [self._side("0,0,0", actor=456)],
            "127.0.0.1:34031": [self._side("0,0,0", actor=456)],
        }
        res_ep = sim_compare.sample_e5_bracket(
            "Jennifer",
            nav_epoch=2,
            corr=1,
            side_reader=lambda ep, _p, r=mismatched_endpoints: r[ep].pop(0),
            admin_factory=self._admin_factory("Halted", epoch=2, corr=99, actor=999),
        )
        self.assertEqual(res_ep["verdict"], "BLOCKED")
        self.assertIn("actor id mismatch", res_ep["reason"])

        # Missing simid on any endpoint when --actor-id is omitted -> BLOCKED
        missing_simid = {
            "127.0.0.1:34029": [self._side("0,0,0", actor=None), self._side("0,0,0", actor=None)],
            "127.0.0.1:34030": [self._side("0,0,0", actor=123)],
            "127.0.0.1:34031": [self._side("0,0,0", actor=123)],
        }
        res_no_simid = sim_compare.sample_e5_bracket(
            "Jennifer",
            nav_epoch=2,
            corr=1,
            side_reader=lambda ep, _p, r=missing_simid: r[ep].pop(0),
            admin_factory=self._admin_factory("Halted", epoch=2, corr=1, actor=123),
        )
        self.assertEqual(res_no_simid["verdict"], "BLOCKED")

        # (b) Admin reply with mismatched corr (99 vs 1), mismatched actor (999 vs 123), or mismatched epoch (1 vs 2) -> BLOCKED
        for bad_kwargs in ({"epoch": 2, "corr": 99, "actor": 123},
                           {"epoch": 2, "corr": 1, "actor": 999},
                           {"epoch": 1, "corr": 1, "actor": 123}):
            with self.subTest(bad_kwargs=bad_kwargs):
                reads = {
                    "127.0.0.1:34029": [self._side("0,0,0", actor=123), self._side("0,0,0", actor=123)],
                    "127.0.0.1:34030": [self._side("0,0,0", actor=123)],
                    "127.0.0.1:34031": [self._side("0,0,0", actor=123)],
                }
                res_bad_receipt = sim_compare.sample_e5_bracket(
                    "Jennifer",
                    nav_epoch=2,
                    corr=1,
                    side_reader=lambda ep, _p, r=reads: r[ep].pop(0),
                    admin_factory=self._admin_factory("Halted", **bad_kwargs),
                )
                self.assertEqual(res_bad_receipt["verdict"], "BLOCKED")

        # (c) Missing --corr or missing --nav-epoch even when a single Halted receipt is returned by Admin -> BLOCKED
        for nav_ep, corr_val in ((None, 1), (2, None), (None, None), (0, 1), (2, 0)):
            with self.subTest(nav_epoch=nav_ep, corr=corr_val):
                self.assertEqual(
                    sim_compare.read_client_walk_phase(
                        "127.0.0.1:34030",
                        corr=corr_val,
                        actor=123,
                        nav_epoch=nav_ep,
                        admin_factory=self._admin_factory("Halted", epoch=2, corr=1, actor=123),
                    ),
                    "unattributed",
                )
                reads = {
                    "127.0.0.1:34029": [self._side("0,0,0", actor=123), self._side("0,0,0", actor=123)],
                    "127.0.0.1:34030": [self._side("0,0,0", actor=123)],
                    "127.0.0.1:34031": [self._side("0,0,0", actor=123)],
                }
                res_missing_id = sim_compare.sample_e5_bracket(
                    "Jennifer",
                    nav_epoch=nav_ep,
                    corr=corr_val,
                    side_reader=lambda ep, _p, r=reads: r[ep].pop(0),
                    admin_factory=self._admin_factory("Halted", epoch=2, corr=1, actor=123),
                )
                self.assertEqual(res_missing_id["verdict"], "BLOCKED")
                self.assertEqual(res_missing_id["phases"]["client1"], "unattributed")

    def test_compare_positions_settled_and_wave13_modes_and_archive_zero_coordinates(self) -> None:
        import tempfile
        from scripts import card55_live as live
        # Wave 13 default mode (0.25m XZ / 0.30m Y)
        self.assertTrue(sim_compare.compare_positions("1.00,2.00,3.00", "1.20,2.25,3.00")["matches"])
        self.assertFalse(sim_compare.compare_positions("1.00,2.00,3.00", "1.30,2.00,3.00")["matches"])
        # Settled-mm mode with ticks & spread
        ok = sim_compare.compare_positions(
            [1000, 0, 1000], [1060, 0, 1080],
            settled_mm=True, server_after_pos=[1000, 0, 1000], client_phase="Halted",
            settled_ticks=2, settled_spread_mm=5,
            server_samples=[[1000, 0, 1000], [1000, 0, 1000]],
            client_samples=[[1060, 0, 1078], [1060, 0, 1080]],
        )
        self.assertEqual(ok["verdict"], "PASS")
        self.assertEqual(ok["error_mm"], 100)
        for forbidden in ("position_mm", "server_before_mm", "server_after_mm", "final_cell_mm"):
            self.assertNotIn(forbidden, json.dumps(ok))
        # Spread exceeded -> BLOCKED
        bad_spread = sim_compare.compare_positions(
            [1000, 0, 1000], [1060, 0, 1080],
            settled_mm=True, server_after_pos=[1000, 0, 1000], client_phase="Halted",
            settled_ticks=2, settled_spread_mm=2,
            client_samples=[[1000, 0, 1000], [1060, 0, 1080]],
        )
        self.assertEqual(bad_spread["verdict"], "BLOCKED")
        # Archive integration strips all coordinates
        reads = {
            "127.0.0.1:34029": [self._side("1000,2000,3000"), self._side("1000,2000,3000")],
            "127.0.0.1:34030": [self._side("1060,2000,3080")],
            "127.0.0.1:34031": [self._side("1000,2000,3000")],
        }
        res = sim_compare.sample_e5_bracket(
            "Jennifer",
            scope=self._scope(),
            side_reader=lambda ep, _p, r=reads: r[ep].pop(0),
            phase_reader=lambda _ep: "Halted",
        )
        row = {k: res[k] for k in ("gate", "verdict", "hashes", "counts", "errors_mm")}
        with tempfile.TemporaryDirectory() as tmp:
            live.Archive(Path(tmp)).put("E5", row)
            persisted = (Path(tmp) / "E5.json").read_text(encoding="utf-8")
        for forbidden in ("1000", "2000", "3000", "1060", "3080", "position_mm", "server_before_mm", "server_after_mm", "final_cell_mm"):
            self.assertNotIn(forbidden, persisted)

    def test_identity_override_conflicts_against_scope_fail_closed_and_non_conflicting_fill_in_passes(self) -> None:
        def settled_reads(actor: int = self.ACTOR):
            return {
                "127.0.0.1:34029": [self._side("0,0,0", actor=actor), self._side("0,0,0", actor=actor)],
                "127.0.0.1:34030": [self._side("60,0,80", actor=actor)],
                "127.0.0.1:34031": [self._side("0,0,0", actor=actor)],
            }

        # 1) Exact reviewer repro: scope has epoch=2, corr=1, explicit overrides pass nav_epoch=99, corr=77
        # with fake client pages at epoch=99, corr=77 -> MUST fail closed (BLOCKED), zero coordinates in output.
        reads = settled_reads()
        repro = sim_compare.sample_e5_bracket(
            "Jennifer",
            scope=self._scope(),
            nav_epoch=99,
            corr=77,
            side_reader=lambda ep, _p, r=reads: r[ep].pop(0),
            admin_factory=self._admin_factory("Halted", epoch=99, corr=77, actor=self.ACTOR),
        )
        self.assertEqual(repro["verdict"], "BLOCKED")
        self.assertIn("conflict", repro["reason"])
        for forbidden in ("position_mm", "server_before_mm", "server_after_mm", "final_cell_mm", "60", "80"):
            self.assertNotIn(forbidden, json.dumps(repro))

        # 2) Individual conflicts: nav_epoch vs scope["epoch"], corr vs scope["corr"],
        # actor_id vs scope["actor"], sim vs scope["sim"], internal scope epoch vs nav_epoch,
        # internal scope actor vs actor_id, and explicit nav_epoch vs epoch.
        conflict_cases = [
            (self._scope(), {"nav_epoch": 99}),
            (self._scope(), {"epoch": 99}),
            (self._scope(), {"corr": 77}),
            (self._scope(), {"actor_id": 12345}),
            (dict(self._scope(), sim="Pablo"), {}),
            (dict(self._scope(), nav_epoch=99), {}),
            (dict(self._scope(), actor_id=12345), {}),
            (self._scope(), {"nav_epoch": 2, "epoch": 3}),
        ]
        for sc_dict, kwargs in conflict_cases:
            with self.subTest(scope=sc_dict, kwargs=kwargs):
                r_map = settled_reads()
                res = sim_compare.sample_e5_bracket(
                    "Jennifer",
                    scope=sc_dict,
                    side_reader=lambda ep, _p, r=r_map: r[ep].pop(0),
                    admin_factory=self._admin_factory("Halted", epoch=2, corr=1, actor=self.ACTOR),
                    **kwargs,
                )
                self.assertEqual(res["verdict"], "BLOCKED")

        # 3) Malformed / non-positive overrides or scope fields -> BLOCKED without raising uncaught ValueError/TypeError.
        malformed_cases = [
            (self._scope(), {"nav_epoch": "bad"}),
            (self._scope(), {"nav_epoch": 0}),
            (self._scope(), {"nav_epoch": -1}),
            (self._scope(), {"nav_epoch": True}),
            (self._scope(), {"corr": "bad"}),
            (self._scope(), {"corr": 0}),
            (self._scope(), {"actor_id": "not-an-int"}),
            (self._scope(), {"actor_id": -5}),
            (dict(self._scope(), epoch="bad"), {}),
            (dict(self._scope(), corr=0), {}),
            (dict(self._scope(), actor="bad"), {}),
            ("not-a-dict", {}),
        ]
        for sc_dict, kwargs in malformed_cases:
            with self.subTest(malformed_scope=sc_dict, kwargs=kwargs):
                r_map = settled_reads()
                res = sim_compare.sample_e5_bracket(
                    "Jennifer",
                    scope=sc_dict,
                    side_reader=lambda ep, _p, r=r_map: r[ep].pop(0),
                    admin_factory=self._admin_factory("Halted", epoch=2, corr=1, actor=self.ACTOR),
                    **kwargs,
                )
                self.assertEqual(res["verdict"], "BLOCKED")

        # 4) Non-conflicting fill-in: scope omits epoch & corr while nav_epoch=2, corr=1 supplies them -> PASS.
        partial_scope = dict(self._scope())
        del partial_scope["epoch"]
        del partial_scope["corr"]
        r_fill = settled_reads()
        res_fill = sim_compare.sample_e5_bracket(
            "Jennifer",
            scope=partial_scope,
            nav_epoch=2,
            corr=1,
            side_reader=lambda ep, _p, r=r_fill: r[ep].pop(0),
            admin_factory=self._admin_factory("Halted", epoch=2, corr=1, actor=self.ACTOR),
        )
        self.assertEqual(res_fill["verdict"], "PASS")
        self.assertEqual(res_fill["errors_mm"], {"client1": 100, "client2": 0})

        # 5) Matching explicit + scope (epoch=2, corr=1, actor_id=ACTOR on both) -> PASS.
        r_match = settled_reads()
        res_match = sim_compare.sample_e5_bracket(
            "Jennifer",
            scope=dict(self._scope(), sim="Jennifer"),
            nav_epoch=2,
            corr=1,
            actor_id=self.ACTOR,
            side_reader=lambda ep, _p, r=r_match: r[ep].pop(0),
            admin_factory=self._admin_factory("Halted", epoch=2, corr=1, actor=self.ACTOR),
        )
        self.assertEqual(res_match["verdict"], "PASS")

    def test_blocked_when_missing_or_single_client_never_passes(self) -> None:
        empty = sim_compare.sample_e5_bracket(
            "Jennifer",
            scope=self._scope(),
            client_endpoints=[],
            side_reader=lambda _ep, _p: self._side("0,0,0"),
            phase_reader=lambda _ep: "Halted",
        )
        self.assertEqual(empty["verdict"], "BLOCKED")
        single_reads = {
            "127.0.0.1:34029": [self._side("0,0,0"), self._side("0,0,0")],
            "127.0.0.1:34030": [self._side("0,0,0")],
        }
        single = sim_compare.sample_e5_bracket(
            "Jennifer",
            scope=self._scope(),
            client_endpoints={"client1": "127.0.0.1:34030"},
            side_reader=lambda ep, _p, r=single_reads: r[ep].pop(0),
            phase_reader=lambda _ep: "Halted",
        )
        self.assertEqual(single["verdict"], "BLOCKED")
        self.assertIn("missing client2", single["reason"])


if __name__ == "__main__":
    unittest.main()
