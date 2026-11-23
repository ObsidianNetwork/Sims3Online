"""Tests for route_continuity (card 55 section D counters, section C rules), on synthetic samples."""
from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import route_continuity as rc  # noqa: E402

TICK = 1000.0 / 30


def walk(step_m=0.06, n=60, leg_at=None, style="Walk", door_legs=(), moves=None, **kw):
    """One tick per sample, a straight walk along x; moves(i) -> bool overrides whether tick i moves."""
    out, x = [], 0.0
    for i in range(n):
        if i and (moves(i) if moves else True):
            x += step_m
        leg = 0 if leg_at is None or i < leg_at else 1
        out.append(rc.Sample(round(i * TICK), x, 0.0, i, True, style, leg, leg in door_legs, **kw))
    return out


class Counters(unittest.TestCase):
    def test_clean_walk_is_zero(self):
        server, client = walk(leg_at=30), walk(leg_at=30)
        client = walk(leg_at=30, door_legs=(1,))
        r = rc.score(server, {"c1": client, "c2": walk(leg_at=30, door_legs=(1,))}, events_loaded=True)
        self.assertEqual(r["startup_stutter_events"], 0)
        self.assertEqual(r["pace_mismatch_events"], 0)
        self.assertEqual(r["door_leg_leg_style_mismatches"], 0)
        self.assertEqual(r["client_only_motion_events"], {"per_window": {"c1": 0, "c2": 0}, "symmetric": True})
        self.assertEqual(r["verdict"], "PASS")

    def test_startup_stutter_alternate_tick_burst(self):
        stutter = walk(moves=lambda i: i % 2 == 0)  # 0 cm / 6 cm every second tick
        self.assertGreaterEqual(rc.startup_stutter_events(stutter), 10)

    def test_stutter_outside_first_second_not_counted(self):
        self.assertEqual(rc.startup_stutter_events(walk(n=90, moves=lambda i: i < 31 or i % 2 == 0)), 0)

    def test_stutter_ignores_zero_tick_frames_and_walking_flag_before_first_move(self):
        # frames at 60 Hz over a 30 Hz tick: every second frame repeats the tick count, and the first 5 frames are
        # flagged walking before the Sim moves
        out, x = [], 0.0
        for i in range(80):
            tick = i // 2
            if i >= 10 and i % 2 == 0:
                x += 0.06
            out.append(rc.Sample(round(i * TICK / 2), x, 0.0, tick, True))
        self.assertEqual(rc.startup_stutter_events(out), 0)

    def test_rows_without_ticks_under_half_a_tick_apart_do_not_make_stutter(self):
        # 10 ms frames, 6 cm every third frame: reading each pair as a whole tick would flip 0 / non-zero all along
        out, x = [], 0.0
        for i in range(120):
            if i and i % 3 == 0:
                x += 0.06
            out.append(rc.Sample(i * 10, x, 0.0, None, True))
        self.assertEqual(rc.startup_stutter_events(out), 0)

    def test_pace_mismatch_at_leg_boundary(self):
        server = walk(leg_at=30)
        client = walk(leg_at=30)
        # the copy jumps 0.5 m in the boundary tick: 50 cm per tick against the server's 6
        for s in client[30:]:
            s.x += 0.5
        self.assertEqual(rc.pace_mismatch_events(server, client), 1)

    def test_pace_stale_fastwalk_through_door_is_one_event(self):
        # card 25 section A: the copy keeps FastWalk into the door leg while the server's leg is Walk; same positions,
        # so only the style check can see it
        server = walk(leg_at=30, style="Walk")
        client = walk(leg_at=30, style="Walk")
        for s in client[30:]:
            s.style = "FastWalk"
        self.assertEqual(rc.pace_mismatch_events(server, client), 1)
        self.assertEqual(rc.pace_mismatch_events(server, walk(leg_at=30, style="Normal")), 0)

    def test_pace_with_no_scorable_boundary_is_none(self):
        self.assertIsNone(rc.pace_mismatch_events(walk(), walk()))  # one leg only: no boundary

    def test_pace_boundary_without_a_server_style_for_that_leg_is_not_scored(self):
        server = walk(leg_at=30)
        for x in server[30:]:
            x.style = None  # the server's leg 1 has no style
        client = walk(leg_at=30)
        for x in client[30:]:
            x.x += 0.5  # would be a 50 cm/tick pace event if scored
        self.assertIsNone(rc.pace_mismatch_events(server, client))

    def test_door_leg_fastwalk_counts_once_per_leg(self):
        client = walk(leg_at=30, style="FastWalk", door_legs=(1,))
        self.assertEqual(rc.door_leg_leg_style_mismatches(client), 1)
        self.assertEqual(rc.door_leg_leg_style_mismatches(walk(leg_at=30, style="Walk", door_legs=(1,))), 0)

    def test_client_only_on_one_window_is_asymmetric(self):
        server = walk()
        c1, c2 = walk(), walk()
        c1[20].event = "teleport 1.00 m"
        r = rc.client_only_motion_events(server, {"c1": c1, "c2": c2}, events_loaded=True)
        self.assertEqual(r["per_window"], {"c1": 1, "c2": 0})
        self.assertFalse(r["symmetric"])

    def test_client_event_with_server_counterpart_is_not_client_only(self):
        server, c1 = walk(), walk()
        server[20].event = c1[20].event = "snap 0.30 m"
        self.assertEqual(rc.client_only_motion_events(server, {"c1": c1}, True)["per_window"], {"c1": 0})

    def test_symmetric_client_only_still_fails(self):
        server, c1, c2 = walk(leg_at=30), walk(leg_at=30, door_legs=(1,)), walk(leg_at=30, door_legs=(1,))
        c1[20].event = c2[20].event = "back to Idle"
        r = rc.score(server, {"c1": c1, "c2": c2}, events_loaded=True)
        self.assertTrue(r["client_only_motion_events"]["symmetric"])
        self.assertEqual(r["verdict"], "FAIL")

    def test_client_only_without_loaded_events_is_none_and_blocked(self):
        server, c1, c2 = walk(leg_at=30), walk(leg_at=30, door_legs=(1,)), walk(leg_at=30, door_legs=(1,))
        self.assertEqual(rc.client_only_motion_events(server, {"c1": c1}), {"per_window": None, "symmetric": None})
        self.assertEqual(rc.score(server, {"c1": c1, "c2": c2})["verdict"], "BLOCKED")

    def test_none_counter_does_not_hide_a_measured_fail(self):
        base = {"startup_stutter_events": 0, "pace_mismatch_events": None, "door_leg_leg_style_mismatches": 0,
                "client_only_motion_events": {"per_window": {"c1": 0}, "symmetric": None}}
        self.assertEqual(rc.verdict(base), "BLOCKED")
        self.assertEqual(rc.verdict({**base, "startup_stutter_events": 3}), "FAIL")
        self.assertEqual(rc.verdict({**base, "client_only_motion_events": {"per_window": {"c1": 2, "c2": 0}, "symmetric": False}}), "FAIL")

    def test_pass_by_omission_edges_are_blocked(self):
        server, c1 = walk(leg_at=30), walk(leg_at=30, door_legs=(1,))
        self.assertEqual(rc.score(server, {}, events_loaded=True)["verdict"], "BLOCKED")  # no windows
        one = rc.score(server, {"c1": c1}, events_loaded=True)  # one window cannot show symmetry
        self.assertIsNone(one["client_only_motion_events"]["symmetric"])
        self.assertEqual(one["verdict"], "BLOCKED")
        self.assertIsNone(rc.door_leg_leg_style_mismatches(walk(leg_at=30)))  # no door crossed

    def test_missing_style_or_leg_is_blocked_not_pass(self):
        bare = [rc.Sample(round(i * TICK), 0.06 * i, 0.0, i, True) for i in range(60)]
        r = rc.score(bare, {"c1": bare})
        self.assertIsNone(r["pace_mismatch_events"])
        self.assertIsNone(r["door_leg_leg_style_mismatches"])
        self.assertEqual(r["verdict"], "BLOCKED")


class SamplingRules(unittest.TestCase):
    def test_e5_not_scored_before_settle(self):
        w = {"c1": walk(phase="halted"), "c2": walk()}
        gate = rc.settle_gate((3.0, 0.0), w)
        self.assertEqual(gate, {"scored": False, "unsettled": ["c2"]})

    def test_e5_scored_after_settle(self):
        end = walk(n=3)
        w = {"c1": walk(n=3, phase="halted"), "c2": walk(n=3, phase="seated")}
        gate = rc.settle_gate((end[-1].x, 0.0), w)
        self.assertTrue(gate["scored"])
        self.assertTrue(gate["ok"])
        self.assertFalse(rc.settle_gate((end[-1].x + 0.5, 0.0), w)["ok"])

    def test_midwalk_moving_server_is_detector_artifact(self):
        r = rc.classify_midwalk(1.00, 1.20, 1.25, tol=0.01)
        self.assertEqual(r["label"], "detector-artifact")
        self.assertEqual((r["server_before"], r["server_after"]), (1.00, 1.25))

    def test_midwalk_stable_server_difference_is_divergence(self):
        self.assertEqual(rc.classify_midwalk(1.0, 1.3, 1.0, tol=0.01)["label"], "divergence")
        self.assertEqual(rc.classify_midwalk(1.0, 1.0, 1.0, tol=0.01)["label"], "match")


class Loader(unittest.TestCase):
    def test_reads_motion_watch_samples_json(self):
        rows = [[round(i * TICK), 10.0 + 0.06 * i, 0.0, 5.0, 0, 1, True, "-", i, i] for i in range(40)]
        with tempfile.TemporaryDirectory() as d:
            (Path(d) / "samples.json").write_text(json.dumps({"data": {"server": {"1": rows}, "client": {"1": rows}}}))
            sides = rc.load_motion_run(Path(d), "1")
            self.assertEqual(rc.main([d, "--sim", "1"]), 4)  # no leg/style recorded: BLOCKED
        self.assertEqual(len(sides["server"]), 40)
        self.assertEqual(sides["client"][5].ticks, 5)
        self.assertIsNone(sides["client"][5].style)

    def test_fields_key_makes_the_walk_counters_scorable_and_absence_keeps_them_none(self):
        def rows(door_leg1):
            return [[0, "Walk", 0, False, None, None], [round(30 * TICK), "Walk", 1, door_leg1, None, None],
                    [round(59 * TICK), None, None, None, "halted", None]]
        data = [[round(i * TICK), 10.0 + 0.06 * i, 0.0, 5.0, 0, 1, True, "-", i, i] for i in range(60)]
        with tempfile.TemporaryDirectory() as d:
            (Path(d) / "samples.json").write_text(json.dumps({
                "data": {"server": {"1": data}, "c1": {"1": data}, "c2": {"1": data}},
                "fields": {"server": {"1": rows(True)}, "c1": {"1": rows(True)}, "c2": {"1": rows(True)}}}))
            sides = rc.load_motion_run(Path(d), "1")
            self.assertTrue(rc.has_fields(Path(d)))
            self.assertEqual(rc.main([d, "--sim", "1"]), 0)
        self.assertEqual((sides["c1"][40].style, sides["c1"][40].leg, sides["c1"][40].door), ("Walk", 1, True))
        self.assertEqual(sides["c1"][59].phase, "halted")
        self.assertIsNone(sides["c1"][10].phase)
        self.assertIsNone(rc.pace_mismatch_events(sides["server"], [rc.Sample(s.t_ms, s.x, s.z, s.ticks) for s in sides["c1"]]))


if __name__ == "__main__":
    unittest.main()
