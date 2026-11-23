"""Card 55 executors against an authored transport: no game, socket, debugger or geometry on disk."""
from __future__ import annotations

import itertools
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import card55_executors as ce  # noqa: E402

EPOCH, GEN, CORR, WIRE, KERNEL = 3, 5, 42, 18031287978496800304, 2
CONTENT = "0123456789abcdef"
PLAN_HASH = "fedcba9876543210"
FRAMES = [f"Rt|{CORR}|{WIRE}|2|{PLAN_HASH}|3250",
          f"Vp|rt|{CORR}|{WIRE}|0|0|100|0|100",
          f"Vp|rt|{CORR}|{WIRE}|1|0|300|0|100"]
START_NS = 1_000_000_000_000
SPEC = {"name": "authored", "clients": [1, 2], "lot": "7787005354294925872", "start": "library",
        "rust": {"follower": "lay-path"}, "watch": {"seconds": 5},
        "steps": ["claim-sim:Jennifer", "rust-env:SIMSONLINE_RUST_ROUTE=1", "load-simnav:memory",
                  f"navigate_astar:Jennifer start=simnav:{CONTENT}:1 goal=simnav:{CONTENT}:7"],
        "after": ["rust-evidence:E1", "rust-evidence:E2", "rust-evidence:E3", "rust-evidence:E4", "rust-evidence:E5"]}


class Fake:
    """An authored live stack: every answer is a literal the test chose."""

    def __init__(self):
        self.installed = self.planned = False
        self.walks = 0
        self.fallbacks = 0
        self.route_switch = "on"
        self.authority = "authority;Mode=2;Checked=0"
        self.content = CONTENT
        self.kernel = KERNEL
        self.server_mm = [[1000, 0, 2000]]
        self.client_mm = {"client1": [1030, 0, 2000], "client2": [1000, 0, 1960]}
        self.identity = dict(epoch=EPOCH, path_hash=PLAN_HASH, total_cost_mm=3250, leg_count=2)
        self.progress = dict(epoch=EPOCH, legs_total=2, executed_leg_indices=[0, 1], invalid_progressions=0)
        self.phase = "Halted"
        self.hit_ns = START_NS // 2

    def request(self, side, command):
        if command == "FRONT_DOOR_STATE":
            return f"totals|navigate_requests={self.walks}|plans_served={self.walks}|native_oracle_owned=0"
        if command == "RUST_ROUTE_PLANS":
            return "\n".join(FRAMES) if self.planned else ""
        if command == "SACS_RECORDER status":
            return (f"recorder=on;extras=relationships:0/states:0,route:installed:1/starts:0/rustRoute:{self.route_switch}"
                    f"/plan:{self.walks}/rust:{self.walks}/fallbacks:{self.fallbacks},events:x;world=installed:1")
        if command == "SACS_RECORDER rust-navigation-status":
            return (f"rust-navigation-status=ready;epoch={EPOCH};generation={GEN};installed={int(self.installed)};"
                    f"mapping_checked=1;content_hash={self.content};cells=12;portals=3;provenance=measured;"
                    "storage=memory;surfaces_measured=1;portals_measured=1;obstacles_and_locks_measured=1;"
                    "cross_lot_measured=1")
        if command.startswith("SACS_RECORDER rust-simnav-install "):
            self.installed = True
            return (f"rust-simnav-install=accepted;lot=7787005354294925872;actor={WIRE};kernel_sim={KERNEL};"
                    f"epoch={EPOCH};generation={GEN};content_hash={self.content};cells=12;portals=3;keys_held=0;"
                    "actors=1;provenance=measured;storage=memory")
        if command == "SACS_RECORDER authority":
            return self.authority
        raise AssertionError(f"unexpected {side} command {command}")

    def page(self, side, kind, corr, actor):
        def request(cursor, epoch):
            return (f"rust-route-plans=page;epoch={EPOCH};corr={corr};actor={actor};cursor=0;count=3;total=3;"
                    "next=3;more=0\n" + "\n".join(FRAMES))
        return request

    def claim_held(self):
        return True

    def stack(self):
        return 4242, "boot-a"

    def claim(self, sim):
        return WIRE

    def kernel_for(self, wire):
        return self.kernel

    def at_cell(self, wire, selector):
        return True

    def go(self, sim, wire, selector):
        self.planned = True
        self.walks += 1

    def read_mm(self, side, sim):
        if side == "server":
            return self.server_mm.pop(0) if len(self.server_mm) > 1 else self.server_mm[0]
        return self.client_mm[side]

    def e1_trace(self, scope):
        trace = dict(server_pid=scope["server_pid"], boot_id=scope["boot_id"], instrument="cdb-trace-server",
                     configuration="recipe", module_base=0x10000000,
                     breakpoint_va=0x10000000 + ce.evidence.NATIVE_SEARCH_RVA,
                     search_entries=[1, 2, 3, 4, 5], coverage_start_ns=1, coverage_end_ns=10 ** 15,
                     hits=[dict(label="native-search", tid=1, ns=self.hit_ns)])
        return trace, dict(trace, coverage_end_ns=scope["start_ns"] - 2_000_000)

    def continuity(self, run):
        return {m: 0 for m in ce.evidence.METRICS}

    def lay_path_phase(self, side, corr, actor, epoch):
        return self.phase

    def lay_path_identity(self, side, corr, actor, epoch):
        return dict(self.identity)

    def lay_path_progress(self, side, corr, actor, epoch):
        return dict(self.progress)


class Run(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.fake = Fake()

    def run_spec(self, spec=SPEC, upto=None):
        run = ce.Card55Run(spec, self.fake, Path(self.folder.name),
                           clock=itertools.count(START_NS, 1_000_000_000).__next__, sleep=lambda s: None)
        results = []
        for item in (spec["steps"] + spec["after"])[:upto]:
            kind, _, payload = item.partition(":")
            results.append(run.step(kind, payload, {"motions": {"1": "m1", "2": "m2"}}))
        return run, results

    def test_lay_path_run_passes_every_gate_and_archives_no_geometry(self):
        _, results = self.run_spec()
        self.assertEqual([r["verdict"] for r in results], ["PASS"] * 9, results)
        archived = "".join(p.read_text(encoding="utf-8") for p in (Path(self.folder.name) / "card55").glob("*.json"))
        for leak in ("Vp|rt", "|100|0|100", "1000", "2000", CONTENT):
            self.assertNotIn(leak, archived)
        e5 = json.loads((Path(self.folder.name) / "card55" / "E5.json").read_text(encoding="utf-8"))
        self.assertEqual(e5["errors_mm"], {"walk1_client1": 30, "walk1_client2": 40})

    def test_boundary_route_walk_blocks_on_the_missing_progress_producer(self):
        spec = dict(SPEC, rust={"follower": "boundary-route"})
        run, results = self.run_spec(spec, upto=4)
        self.assertEqual(results[3]["verdict"], "BLOCKED")
        self.assertIn("OPEN-1", results[3]["reason"])
        self.assertEqual(run.walks, [])
        self.assertEqual(run.step("rust-evidence", "E4")["verdict"], "BLOCKED")

    def test_claim_without_a_checked_kernel_id_blocks(self):
        self.fake.kernel = None
        _, results = self.run_spec(upto=1)
        self.assertEqual(results[0]["verdict"], "BLOCKED")
        self.assertIn("kernel id", results[0]["reason"])

    def test_route_switch_off_or_no_fork_blocks_rust_env(self):
        self.fake.route_switch = "off"
        self.assertIn("switch is off", self.run_spec(upto=2)[1][1]["reason"])
        self.fake.route_switch, self.fake.authority = "on", "authority;hook=absent"
        self.assertIn("fork", self.run_spec(upto=2)[1][1]["reason"])

    def test_install_of_other_content_than_the_pinned_selectors_blocks(self):
        self.fake.content = "aaaaaaaaaaaaaaaa"
        _, results = self.run_spec(upto=3)
        self.assertEqual(results[2]["verdict"], "BLOCKED")
        self.assertIn("recapture", results[2]["reason"])

    def test_walk_before_install_blocks(self):
        spec = dict(SPEC, steps=[SPEC["steps"][0], SPEC["steps"][3]])
        self.assertEqual(self.run_spec(spec, upto=2)[1][1]["verdict"], "BLOCKED")

    def test_evidence_with_no_walk_blocks(self):
        run = ce.Card55Run(SPEC, self.fake, Path(self.folder.name))
        for gate in ce.GATES:
            self.assertEqual(run.step("rust-evidence", gate)["verdict"], "BLOCKED")

    def test_different_client_plan_identity_fails_e3(self):
        self.fake.identity["path_hash"] = "0000000000000000"
        _, results = self.run_spec()
        self.assertEqual(results[6]["verdict"], "FAIL")

    def test_skipped_leg_or_invalid_progression_fails_e4(self):
        self.fake.progress["executed_leg_indices"] = [1]
        self.assertEqual(self.run_spec()[1][7]["verdict"], "FAIL")
        self.fake = Fake()
        self.fake.progress["invalid_progressions"] = 1
        self.assertEqual(self.run_spec()[1][7]["verdict"], "FAIL")

    def test_e5_landing_beyond_100_mm_fails_and_a_moving_server_blocks(self):
        self.fake.client_mm["client2"] = [1000, 0, 2101]
        self.assertEqual(self.run_spec()[1][8]["verdict"], "FAIL")
        self.fake = Fake()
        self.fake.server_mm = [[1000, 0, 2000], [1000, 0, 2050]]
        self.assertEqual(self.run_spec()[1][8]["verdict"], "BLOCKED")

    def test_native_search_inside_the_run_fails_e1(self):
        self.fake.fallbacks = 0
        self.fake.hit_ns = START_NS + 1  # in-run debugger hit with zero fork fallbacks
        run_hit, _ = self.run_spec(upto=4)
        self.assertEqual(run_hit.step("rust-evidence", "E1")["verdict"], "FAIL")

        self.fake = Fake()
        self.fake.fallbacks = 0
        run_fb, _ = self.run_spec(upto=4)
        self.fake.fallbacks = 1  # a counted fork fallback during the run
        self.assertEqual(run_fb.step("rust-evidence", "E1")["verdict"], "FAIL")

    def test_collect_e4_receipts_preserves_invalid_fail_when_other_side_missing(self):
        scope = dict(run_id="r", boot_id="b", server_pid=4242, epoch=EPOCH, corr=1, actor=WIRE, generation=GEN,
                     owner="player1", start_ns=START_NS, end_ns=START_NS + 10, bridge_connected=True,
                     claim_held=True, instrument_ready=True, coverage_start_ns=1, coverage_end_ns=START_NS + 20,
                     collector_status="complete")
        receipts = {
            "client1": dict(epoch=EPOCH, legs_total=2, executed_leg_indices=[0, 1], invalid_progressions=1),
            "client2": dict(epoch=EPOCH + 1, legs_total=2, executed_leg_indices=[0, 1], invalid_progressions=0),
        }
        cont = {s: {m: 0 for m in ce.evidence.METRICS} for s in ce.SIDES}
        self.assertEqual(ce.collect_e4_receipts(scope, receipts, cont)["verdict"], "FAIL")


class Preflight(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.root = Path(self.folder.name)
        (self.root / "scenarios" / "start").mkdir(parents=True)
        (self.root / "scenarios" / "start" / "library.json").write_text("{}", encoding="utf-8")

    def test_a_pinned_scenario_passes(self):
        self.assertIsNone(ce.preflight(SPEC, self.root))

    def test_each_missing_input_is_named(self):
        cases = {
            "G0 has not pinned": dict(SPEC, steps=["navigate_astar:Jennifer goal=measured_upper_cell_UNAVAILABLE"]),
            "no measured lot id": dict(SPEC, lot=""),
            "no fixed start file": dict(SPEC, start="home"),
            "unknown rust follower": dict(SPEC, rust={"follower": "rt"}),
            "different SIMNAV": dict(SPEC, steps=[f"navigate_astar:J goal=simnav:{CONTENT}:1",
                                                  "navigate_astar:J goal=simnav:aaaaaaaaaaaaaaaa:2"]),
            "not a measured simnav selector": dict(SPEC, steps=["navigate_astar:J goal=cell7"]),
            "rust-evidence takes one of": dict(SPEC, after=["rust-evidence:E6"]),
            "load-simnav takes memory only": dict(SPEC, steps=["load-simnav:library.simnav"]),
        }
        for expected, spec in cases.items():
            with self.subTest(expected=expected):
                self.assertIn(expected, ce.preflight(spec, self.root))

    def test_ordinary_scenarios_have_no_preflight(self):
        self.assertIsNone(ce.preflight({"steps": ["sleep:1"], "after": ["drivers"]}, self.root))


class Pieces(unittest.TestCase):
    def test_route_census_reads_only_the_extras_route_part(self):
        census = ce.route_census("a=1;extras=x:1,route:installed:1/rustRoute:on/rust:4,events:y;b=2")
        self.assertEqual(census, {"installed": "1", "rustRoute": "on", "rust": "4"})
        for bad in ("a=1", "extras=x:1", "extras=route:a:1/a:2", "extras=a;extras=b"):
            with self.subTest(bad=bad), self.assertRaises(ce.Missing):
                ce.route_census(bad)

    def test_live_transport_names_each_missing_producer(self):
        transport = ce.LiveTransport(endpoints={"server": "127.0.0.1:34029"}, session="")
        self.assertFalse(transport.claim_held())
        for call, args in ((transport.kernel_for, (WIRE,)), (transport.go, ("J", WIRE, "simnav:x:1")),
                           (transport.at_cell, (WIRE, "simnav:x:1")),
                           (transport.lay_path_phase, ("client1", CORR, WIRE, EPOCH)),
                           (transport.lay_path_identity, ("client1", CORR, WIRE, EPOCH)),
                           (transport.lay_path_progress, ("client1", CORR, WIRE, EPOCH))):
            with self.subTest(call=call.__name__), self.assertRaises(ce.Blocked):
                call(*args)


if __name__ == "__main__":
    unittest.main()
