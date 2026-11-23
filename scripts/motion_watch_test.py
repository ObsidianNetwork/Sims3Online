"""Unit tests for motion_watch's detectors on synthetic samples: python -m unittest scripts.motion_watch_test

(ms, x, y, z, fx, fz, walking, state, sequence[, simulator ticks]) as MotionWatch sends them; the tick count is the
11th field from mods built on 2026-10-17 on, and older saved runs (no ticks) must still score.
"""
from __future__ import annotations

import sys
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import motion_watch as mw  # noqa: E402
import ts3_view  # noqa: E402


def walk(steps_cm: list[float], ticks: list[int] | None = None, walking: bool = True, t0: int = 0) -> list[tuple]:
    """One sample a frame (33 ms) moving along x by each step; ticks: the ticks each frame carries (with the 11th field)."""
    samples, x, t, tick = [], 0.0, t0, 1000
    for i, step in enumerate(steps_cm):
        x += step / 100.0
        t += 33
        row = (t, x, 0.0, 0.0, 0.0, 1.0, walking, "-", 0)
        if ticks is not None:
            tick += ticks[i]
            row += (tick,)
        samples.append(row)
    return samples


def kinds(samples: list[tuple]) -> list[str]:
    return [k for _, k, _ in mw.events(samples)]


class TeleportWindowTest(unittest.TestCase):
    def test_a_half_metre_jump_mid_walk_is_seen_without_ticks(self) -> None:
        # 150 ms let a walking step up to 0.9 m pass; at 75 ms a 0.5 m jump after a still frame reads 7.6 m/s
        self.assertIn("teleport", kinds(walk([6] * 20 + [0, 50] + [6] * 20)))

    def test_bursts_after_still_frames_are_not_teleports_without_ticks(self) -> None:
        # 0.28 m after one or two still frames: judged over at most 75 ms, 3.7-4.2 m/s
        self.assertNotIn("teleport", kinds(walk([6] * 10 + [0, 28] * 30)))
        self.assertNotIn("teleport", kinds(walk([6] * 10 + [0, 0, 28] * 20)))

    def test_with_ticks_a_step_is_judged_over_the_ticks_it_spans(self) -> None:
        # 0.3 m in one tick (9 m/s) is a jump even right after a still frame
        self.assertIn("teleport", kinds(walk([9] * 20 + [0, 30] + [9] * 20, ticks=[1] * 20 + [1, 1] + [1] * 20)))
        # a stall (frames without ticks) then 0.54 m over six ticks is a FastWalk: without the ticks it reads 7 m/s
        stall = [9] * 20 + [0] * 5 + [54] + [9] * 20
        self.assertIn("teleport", kinds(walk(stall)))
        self.assertNotIn("teleport", kinds(walk(stall, ticks=[1] * 20 + [0] * 5 + [6] + [1] * 20)))

    def test_samples_with_and_without_ticks_both_score(self) -> None:
        old = walk([6] * 30)
        new = walk([6] * 30, ticks=[1] * 30)
        self.assertEqual(len(old[0]), 9)
        self.assertEqual(len(new[0]), 10)
        self.assertEqual(kinds(old), kinds(new))


class OwnRequestTest(unittest.TestCase):
    """A client's own animation request on a copy (shower watch 002549: a react_generic overlay the server never
    played) is its own event; one the server's game made too is not."""

    OVERLAY = ("state R react_generic:OverlayUpperBody by game via StateMachineClient.RequestState<StateMachineClient."
               "RequestState<OverlayData.PlayOverlayClip<OverlayData.PlayClipInternal<OverlayData.PlayGenericClip")
    WALK = ("state R Idle:Standing by game via StateMachineClient.RequestState<StateMachineClient.RequestState<"
            "BridgeOrigin.MakeRequest<SimScriptAdaptor.OnAboutToStartFollowingRoute")

    def test_a_copys_own_overlay_is_flagged(self) -> None:
        out = mw.own_requests([(1000, self.WALK)], [(5000, self.OVERLAY), (5001, "state R react_generic:2835 by replay")], 0)
        self.assertEqual(len(out), 1)
        self.assertEqual(out[0][1], "own request")
        self.assertIn("react_generic:OverlayUpperBody", out[0][2])
        self.assertIn("OverlayData.PlayOverlayClip", out[0][2])

    def test_a_request_both_games_make_pairs(self) -> None:
        self.assertEqual(mw.own_requests([(1000, self.WALK)], [(1400, self.WALK)], 300), [])
        self.assertEqual(len(mw.own_requests([(1000, self.WALK)], [(4000, self.WALK)], 300)), 1)


class TickCadenceTest(unittest.TestCase):
    def test_a_copy_moving_every_second_tick_is_flagged(self) -> None:
        server = walk([6] * 90, ticks=[1] * 90)
        client = walk([0, 12] * 45, ticks=[1] * 90)
        found = mw.tick_cadence(client, server)
        self.assertEqual([k for _, k, _ in found], ["tick cadence"])
        self.assertLess(mw.tick_share(client) or 1, 0.6)

    def test_frames_without_a_tick_and_pauses_are_not_cadence(self) -> None:
        server = walk([6] * 90, ticks=[1] * 90)
        # a frame now and then without a tick (no movement possible), a two-tick frame after it, and a door pause
        client = walk(([6] * 8 + [0, 12]) * 5 + [0] * 10 + [6] * 30, ticks=([1] * 8 + [0, 2]) * 5 + [1] * 10 + [1] * 30)
        self.assertEqual(mw.tick_cadence(client, server), [])
        self.assertGreater(mw.tick_share(client) or 0, 0.95)

    def test_samples_without_ticks_have_no_cadence(self) -> None:
        self.assertEqual(mw.tick_cadence(walk([0, 12] * 45), walk([6] * 90)), [])
        self.assertIsNone(mw.tick_share(walk([6] * 90)))


class PaceTest(unittest.TestCase):
    def line(self, speed: float, seconds: float, t0: int = 0) -> list[tuple]:
        return walk([speed * 3.3] * int(seconds * 30), t0=t0)

    def test_the_same_pace_at_the_same_places_is_clean(self) -> None:
        server = self.line(1.8, 6)
        client = self.line(1.8, 6, t0=300)  # late by the lag: places, not times, are compared
        self.assertEqual(mw.walk_pace(client, server, 300), [])

    def test_fastwalk_against_walk_is_flagged(self) -> None:
        server = self.line(1.8, 8)
        client = self.line(2.4, 6)
        found = mw.walk_pace(client, server, 0)
        self.assertEqual([k for _, k, _ in found], ["pace"])
        self.assertIn("1.33x", found[0][2])

    def test_a_copy_at_0_8_of_the_speed_is_flagged(self) -> None:
        found = mw.walk_pace(self.line(1.44, 7), self.line(1.8, 6), 0)
        self.assertEqual([k for _, k, _ in found], ["pace"])

    def test_one_second_a_little_off_is_not_a_pace(self) -> None:
        # FastWalk's speed changes with the step: a lone window at 0.82 is noise; 2 s of it is not
        server = self.line(2.4, 8)
        one = walk([2.4 * 3.3] * 90 + [1.97 * 3.3] * 30 + [2.4 * 3.3] * 90)
        self.assertEqual(mw.walk_pace(one, server, 0), [])

    def test_where_the_server_walked_slowly_is_not_compared(self) -> None:
        server = self.line(0.8, 6)
        self.assertEqual(mw.walk_pace(self.line(1.6, 3), server, 0), [])


class WalkStepTest(unittest.TestCase):
    def test_a_half_speed_walk_reads_as_its_step_against_the_servers(self) -> None:
        # two-hud on client 2 (motion 064112-c2): 3.2 cm a tick against the server's 5.7, the copy 300 ms late
        server = walk([5.7] * 22, ticks=[1] * 22)
        client = walk([3.2] * 49, ticks=[1] * 49, t0=300)
        found = mw.walk_steps(client, server, 300)
        self.assertEqual(len(found), 1)
        self.assertAlmostEqual(found[0][1], 3.2)
        self.assertAlmostEqual(found[0][3] or 0, 5.7)
        self.assertEqual(mw.step_text(found[0]), "step 3.2 cm/tick on the client, 5.7 on the server (0.56x; 48 and 21 ticks)")

    def test_a_two_tick_frame_counts_twice_and_a_turn_in_place_not_at_all(self) -> None:
        run = walk([0] * 10 + [12] * 10 + [6] * 10, ticks=[1] * 10 + [2] * 10 + [1] * 10)
        step = mw.step_cm(run)
        self.assertIsNotNone(step)
        self.assertAlmostEqual(step[0] if step else 0, 6.0)
        self.assertEqual(step[1] if step else 0, 30)  # 10 two-tick frames and 10 one-tick ones

    def test_each_walk_is_matched_with_the_server_walk_it_overlaps(self) -> None:
        def walks(first: float, second: float, t0: int = 0) -> list[tuple]:
            a = walk([first] * 30, ticks=[1] * 30, t0=t0)
            still = walk([0] * 60, ticks=[1] * 60, walking=False, t0=a[-1][0])
            b = walk([second] * 30, ticks=[1] * 30, t0=still[-1][0])
            return a + still + b

        found = mw.walk_steps(walks(6.0, 2.0, t0=500), walks(6.0, 4.0), 500)
        self.assertEqual([(round(f[1], 1), round(f[3] or 0, 1)) for f in found], [(6.0, 6.0), (2.0, 4.0)])

    def test_a_copy_stopped_short_of_where_the_server_walked_on_to_is_off(self) -> None:
        # two-hud 2026-10-21 (motion 083220): the server's Sim walked on 0.53 m past where its copy was stopped
        server = walk([6.0] * 30) + walk([0] * 60, walking=False, t0=990)
        server = [(s[0], s[1] + (server[29][1] if i >= 30 else 0.0)) + s[2:] for i, s in enumerate(server)]
        client = walk([6.0] * 21, t0=100) + walk([0] * 60, walking=False, t0=100 + 21 * 33)
        client = [(s[0], s[1] + (client[20][1] if i >= 21 else 0.0)) + s[2:] for i, s in enumerate(client)]
        found = mw.walk_rests(client, server, 100)
        self.assertEqual(len(found), 1)
        self.assertAlmostEqual(found[0][1], 0.54, places=2)
        self.assertEqual(mw.rest_text(found[0]), "rest 0.54 m from the server's Sim (which walked 165 ms longer)   <- off")

    def test_a_copy_that_drifts_after_its_walk_says_so_and_one_that_matches_does_not(self) -> None:
        server = walk([6.0] * 30) + walk([0] * 60, walking=False, t0=990)
        server = [(s[0], s[1] + (server[29][1] if i >= 30 else 0.0)) + s[2:] for i, s in enumerate(server)]
        same = mw.walk_rests(server, server, 0)
        self.assertEqual(mw.rest_text(same[0]), "rest 0.00 m from the server's Sim")
        drifting = walk([6.0] * 30) + walk([1.0] * 40, walking=False, t0=990)
        drifting = [(s[0], s[1] + (drifting[29][1] if i >= 30 else 0.0)) + s[2:] for i, s in enumerate(drifting)]
        found = mw.walk_rests(drifting, server, 0)
        self.assertGreater(found[0][2], mw.REST_DRIFT)
        self.assertTrue(mw.rest_text(found[0]).endswith("further apart without a walk   <- off"))

    def test_no_ticks_no_steps_and_a_walk_the_server_did_not_make_says_so(self) -> None:
        self.assertEqual(mw.walk_steps(walk([6] * 30), walk([6] * 30), 0), [])
        alone = mw.walk_steps(walk([6] * 30, ticks=[1] * 30), walk([6] * 30, ticks=[1] * 30, walking=False), 0)
        self.assertEqual(mw.step_text(alone[0]), "step 6.0 cm/tick on the client (29 ticks), no server walk with it")


class StateEntriesTest(unittest.TestCase):
    def test_each_server_entry_is_matched_to_the_nearest_client_entry_after_the_lag(self) -> None:
        server = [(1000, "sacs StateEntered Shower:Enter"), (5000, "sacs StateEntered Shower:Exit Working"),
                  (5100, "state R Shower:Exit Working by game")]
        client = [(1300, "sacs StateEntered Shower:Enter"), (6200, "sacs StateEntered Shower:Exit Working"),
                  (6000, "sacs BecameIdle Shower:Loop")]
        found = mw.state_entries(server, client, 200)
        self.assertEqual([(n, d) for _, n, d in found], [("Shower:Enter", 100), ("Shower:Exit Working", 1000)])

    def test_a_state_the_client_never_entered_is_missing(self) -> None:
        found = mw.state_entries([(1000, "sacs StateEntered Shower:Loop")], [(20000, "sacs StateEntered Shower:Loop")], 0)
        self.assertEqual(found[0][2], None)


class StreamLagTest(unittest.TestCase):
    # two-hud on client 2 (motion 064112-c2): the walk's request reached the copy in 49 ms, the sit-down's lines were
    # held behind the copy's slow walk for 1.9 s; the path fit said 950 ms
    SERVER = [(18036, "state R e9a2077ed45629ff:2689084685 by game via StateMachineClient.RequestState<..."),
              (19370, "state E 446c9ce1af1b84b7:3409766971 by game via StateMachineClient.EnterState<..."),
              (19370, "state R 446c9ce1af1b84b7:2939433680 by game via StateMachineClient.RequestState<..."),
              (21902, "state R 446c9ce1af1b84b7:3948043036 by game via StateMachineClient.RequestState<...")]
    CLIENT = [(18085, "state R e9a2077ed45629ff:2689084685 by replay"),
              (21319, "state E 446c9ce1af1b84b7:3409766971 by replay"),
              (21320, "state R 446c9ce1af1b84b7:2939433680 by replay"),
              (21951, "state R 446c9ce1af1b84b7:3948043036 by replay")]

    def test_the_stream_lag_is_the_lowest_request_delay_not_the_held_lines(self) -> None:
        self.assertEqual(mw.stream_lag(self.SERVER, self.CLIENT), (49, 4))

    def test_a_request_pairs_only_with_the_same_kind_after_it_and_two_pairs_are_needed(self) -> None:
        early = [(18000, "state R e9a2077ed45629ff:2689084685 by replay")]  # before the server's: not its replay
        self.assertIsNone(mw.stream_lag(self.SERVER[:1], early + self.CLIENT[:1]))  # one pair only
        entered = [(19400, "state R 446c9ce1af1b84b7:3409766971 by replay")]  # an R of the state the server entered (E)
        self.assertIsNone(mw.stream_lag(self.SERVER[:2], entered + self.CLIENT[:1]))

    def test_late_entries_measured_from_the_stream_lag_read_the_real_delay(self) -> None:
        server = [(19398, "sacs StateEntered 446c9ce1af1b84b7:3409766971")]
        client = [(21323, "sacs StateEntered 446c9ce1af1b84b7:3409766971")]
        self.assertEqual(mw.state_entries(server, client, 49)[0][2], 1876)
        self.assertEqual(mw.state_entries(server, client, 950)[0][2], 975)  # the path lag hid most of it


def shower_states(loop_ms: int, extra_loop: bool = False) -> list[tuple[int, str]]:
    """One game's Shower entries: Enter, Loop Shower, its boundary back into Loop Shower after loop_ms, Exit Working."""
    evs = [(0, "sacs StateEntered Shower:Enter"), (2150, "sacs StateEntered Shower:Loop Shower")]
    if extra_loop:
        evs.append((2150 + 1000, "sacs StateEntered Shower:Loop Shower"))
    start = evs[-1][0]
    return evs + [(start + loop_ms, "sacs StateEntered Shower:Loop Shower"),
                  (start + loop_ms + 2900, "sacs StateEntered Shower:Exit Working")]


class StateLengthTest(unittest.TestCase):
    def test_a_loop_variant_longer_on_the_copy_is_reported_with_where_it_went(self) -> None:
        found = mw.state_lengths(shower_states(2266), shower_states(3234), 0)
        self.assertEqual(found, [("Shower:Loop Shower -> Loop Shower", 2266, 3234)])

    def test_matching_lengths_and_a_machine_s_last_entry_are_not_reported(self) -> None:
        self.assertEqual(mw.state_lengths(shower_states(2266), shower_states(2300), 0), [])
        spans = mw.entry_spans(mw.entered_states(shower_states(2266)))
        self.assertEqual(spans[-1], None)
        self.assertEqual(spans[0], (2150, "Loop Shower"))

    def test_an_extra_entry_on_the_copy_pairs_the_rest_by_time(self) -> None:
        # The copy enters Loop Shower once more: pairing by order would compare every later state with the wrong one.
        server, client = mw.entered_states(shower_states(2266)), mw.entered_states(shower_states(2266, extra_loop=True))
        pairs = mw.pair_states(server, client, 0)
        # The copy's entries: Enter 0, Loop 2150, its extra Loop 3150, boundary 5416, Exit Working 8316.
        self.assertEqual([client[j][0] if j is not None else None for j in pairs], [0, 2150, 5416, 8316])

    def test_lengths_to_different_next_states_are_not_compared(self) -> None:
        server = [(0, "sacs StateEntered Shower:Loop Shower"), (2266, "sacs StateEntered Shower:Exit Working")]
        client = [(0, "sacs StateEntered Shower:Loop Shower"), (3300, "sacs StateEntered Shower:Exit Shower")]
        self.assertEqual(mw.state_lengths(server, client, 0), [])


class ClipHoldTest(unittest.TestCase):
    @staticmethod
    def held(still_frames: int) -> list[tuple]:
        """Still from t=0 for still_frames frames (one tick each), then moving 2 cm a frame."""
        return walk([0] * still_frames + [2] * 10, ticks=[1] * (still_frames + 10), walking=False, t0=-33)

    def test_the_hold_after_a_clip_event_is_timed_in_ms_and_ticks(self) -> None:
        evs = [(0, "sacs ScriptEvent Shower:Loop Shower id=747")]
        self.assertEqual(mw.event_holds(self.held(5), evs), [("Shower:747", 165, 5)])

    def test_a_hold_longer_on_one_game_is_reported_and_a_matching_one_is_not(self) -> None:
        evs = [(0, "sacs ScriptEvent Shower:Loop Shower id=747")]
        server, client = mw.event_holds(self.held(5), evs), mw.event_holds(self.held(19), evs)
        self.assertEqual(mw.clip_holds(server, client), [("Shower:747", (165, 5), (627, 19))])
        self.assertEqual(mw.clip_holds(server, mw.event_holds(self.held(6), evs)), [])

    def test_a_move_after_the_clip_went_idle_is_not_a_hold(self) -> None:
        evs = [(0, "sacs ScriptEvent Shower:Exit Working id=747"), (33, "sacs BecameIdle Shower:Exit Working")]
        self.assertEqual(mw.event_holds(self.held(19), evs), [])


class EventTicksTest(unittest.TestCase):
    def test_events_are_counted_in_ticks_from_the_first_state_and_from_each_later_request(self) -> None:
        samples = walk([0] * 120, ticks=[1] * 120, walking=False)  # ticks 1001.. at 33, 66, ... ms
        evs = [(0, "state E Shower:Enter by game"), (0, "state R Shower:Loop Shower by game via X"),
               (33, "sacs StateEntered Shower:Enter"), (462, "sacs ScriptEvent Shower:Loop Shower id=747"),
               (500, "sacs AnimationDone Idle:Loop"), (2112, "sacs StateEntered Shower:Loop Shower"),
               (2475, "state R Shower:Exit Working by replay"), (2970, "sacs BecameIdle Shower:Exit Working")]
        self.assertEqual(mw.event_ticks(samples, evs, "Shower"), [
            ("in:Enter", [("in:Enter", 0), ("ev747", 13), ("in:Loop Shower", 63)]),
            ("R Exit Working", [("idle", 15)]),
        ])

    def test_without_ticks_the_events_are_listed_without_counts(self) -> None:
        evs = [(33, "sacs StateEntered Shower:Enter"), (66, "sacs AnimationDone Shower:Enter")]
        self.assertEqual(mw.event_ticks(walk([0] * 5, walking=False), evs, "Shower"),
                         [("in:Enter", [("in:Enter", None), ("done", None)])])


class SlideMatchTest(unittest.TestCase):
    """two-chairs 2026-10-21 (motion 051411): a sit-down's root move of about 0.2 m on both games crossed the slide
    threshold on the client (0.21 m) and not on the server (0.19 m), so the shared move was reported client-only."""

    def test_the_same_root_move_on_both_games_matches(self) -> None:
        server = walk([1.25] * 30, walking=False)               # 0.19 m in each half-second window
        client = walk([1.45] * 30, walking=False, t0=100)       # 0.22 m: a slide on the client only
        self.assertEqual(kinds(server), [])
        self.assertIn("slide", kinds(client))
        t = next(t for t, k, _ in mw.events(client) if k == "slide")
        self.assertTrue(mw.moved_alike(server, client, t, 100))

    def test_a_slide_with_the_server_still_stays_client_only(self) -> None:
        server = walk([0.0] * 30, walking=False)
        client = walk([2.0] * 30, walking=False, t0=100)
        t = next(t for t, k, _ in mw.events(client) if k == "slide")
        self.assertFalse(mw.moved_alike(server, client, t, 100))

    def test_a_move_the_other_way_is_not_the_same_move(self) -> None:
        server = walk([-2.0] * 30, walking=False)
        client = walk([2.0] * 30, walking=False, t0=100)
        t = next(t for t, k, _ in mw.events(client) if k == "slide")
        self.assertFalse(mw.moved_alike(server, client, t, 100))

    def test_outside_the_server_samples_is_no_match(self) -> None:
        client = walk([2.0] * 30, walking=False)
        self.assertFalse(mw.moved_alike([], client, client[0][0], 0))


def follow_fields(state: str) -> dict[str, str]:
    """A follow probe's fields for a game in this state (ts3_view.decide reads them back)."""
    if state == "error":
        return {"err": "no reply in 30s"}
    return {"ok": "true", "simId": "42", "followId": "42" if state == "held" else "0", "away": "1" if state == "away" else "0"}


class FakeView:
    """ts3_view's follow calls without a game: each check returns the next scripted {side: state}; a hook may run on
    a call (stopping the watch mid-check or mid-relock)."""

    def __init__(self, checks: list[dict[str, str]], on_check=None, on_relock=None):
        self.checks, self.on_check, self.on_relock = list(checks), on_check, on_relock
        self.calls: list[tuple] = []

    def follow(self, name, sides=None, timeout=180):
        self.calls.append(("lock", name, sides))
        if sides is not None and self.on_relock:
            self.on_relock()
        return {side: follow_fields("held") for side in (sides or ["server", "client"])}

    def follow_state(self, name, sides=None, timeout=180):
        self.calls.append(("check", name, sides))
        if self.on_check:
            self.on_check(len([c for c in self.calls if c[0] == "check"]))
        states = self.checks.pop(0) if self.checks else {"server": "held", "client": "held"}
        return {side: follow_fields(state) for side, state in states.items()}

    def unfollow(self, sides=None, timeout=180):
        self.calls.append(("unlock", None, sides))
        return {side: {"ok": "true", "mode": "unlock", "was": "42", "followId": "0"} for side in ("server", "client")}

    @staticmethod
    def decide(fields):
        return ts3_view.decide(fields)


class CameraFollowTest(unittest.TestCase):
    def run_follow(self, view: FakeView, follow: "mw.CameraFollow") -> list[tuple]:
        follow.start()
        follow.join(timeout=10)
        self.assertFalse(follow.is_alive())
        return view.calls

    def test_locks_once_relocks_only_the_lost_game_and_unlocks_at_the_end(self) -> None:
        follow = None

        def stop_after(n):
            if n == 4:
                follow.halt.set()

        view = FakeView([{"server": "held", "client": "relock"},   # the client's follow was dropped
                         {"server": "held", "client": "away"},     # a rabbit hole: no relock churn
                         {"server": "error", "client": "held"}], on_check=stop_after)
        follow = mw.CameraFollow("Pablo", every=0.01, view=view)
        calls = self.run_follow(view, follow)
        self.assertEqual(calls[0], ("lock", "Pablo", None))
        self.assertEqual([c for c in calls if c[0] == "lock"][1:], [("lock", "Pablo", ["client"])])
        self.assertEqual(calls[-1], ("unlock", None, None))
        self.assertEqual(sum(1 for c in calls if c[0] == "unlock"), 1)
        log = follow.summary()
        self.assertIn("lock client: held", log)
        self.assertIn("client: relock", log)
        self.assertIn("relock client: held", log)
        self.assertIn("client: away", log)
        self.assertIn("server: error (no reply in 30s)", log)
        self.assertIn("unlock server: released", log)

    def test_a_stop_during_a_relock_unlocks_after_it(self) -> None:
        follow = None
        view = FakeView([{"server": "relock", "client": "held"}], on_relock=lambda: follow.halt.set())
        follow = mw.CameraFollow("Pablo", every=0.01, view=view)
        calls = self.run_follow(view, follow)
        self.assertEqual(calls[-2:], [("lock", "Pablo", ["server"]), ("unlock", None, None)])
        self.assertEqual(sum(1 for c in calls if c[0] == "unlock"), 1)

    def test_a_stop_during_a_check_skips_the_relock(self) -> None:
        follow = None
        view = FakeView([{"server": "relock", "client": "relock"}], on_check=lambda n: follow.halt.set())
        follow = mw.CameraFollow("Pablo", every=0.01, view=view)
        calls = self.run_follow(view, follow)
        self.assertEqual([c[0] for c in calls], ["lock", "check", "unlock"])

    def test_a_failing_view_never_ends_the_watch_early_and_still_unlocks(self) -> None:
        class Broken(FakeView):
            def follow(self, name, sides=None, timeout=180):
                self.calls.append(("lock", name, sides))
                raise OSError("bridge down")

        follow = None
        view = Broken([], on_check=lambda n: follow.halt.set() if n == 2 else None)
        follow = mw.CameraFollow("Pablo", every=0.01, view=view)
        calls = self.run_follow(view, follow)
        self.assertEqual([c[0] for c in calls], ["lock", "check", "check", "unlock"])
        self.assertIn("lock server: error (OSError: bridge down)", follow.summary())

    def test_repeated_errors_slow_the_checks(self) -> None:
        waits = []
        follow = None
        view = FakeView([{"server": "error", "client": "error"}] * 5, on_check=lambda n: follow.halt.set() if n == 5 else None)
        follow = mw.CameraFollow("Pablo", every=0.01, view=view)
        real_wait = follow.halt.wait

        def wait(seconds):
            waits.append(seconds)
            return real_wait(0)

        follow.halt.wait = wait
        self.run_follow(view, follow)
        self.assertEqual(waits[:5], [0.01, 0.01, 0.01, 0.04, 0.04])  # after the third miss in a row


class VehicleTest(unittest.TestCase):
    """/176: a drive (GoToLot to a far lot) was found by grepping events.txt; the copy jumped 145 m in it."""

    GET_IN = ("T 11539349195638397392/2820733090/1/0 by game via Slots.AttachToSlot<SittingInVehicle.Idle<Vehicle.PutIn"
              "<Vehicle.PutInPassenger<GetInVehicleRouteAction.PerformAction<RoutingComponent.DoRoute")
    GET_OUT = ("Ob SetPosition:v/1142.8~45.9~1011.6 by game via Objects.SetPosition<GameObject.SetPosition<Car.GetOut"
               "<Car.GetOut<Vehicle.GetOut<CarNpcWithNpcDriver.OnRouteFinished")

    def test_the_frames_and_the_tag(self) -> None:
        self.assertEqual(mw.vehicle_frames(self.GET_IN),
                         ["SittingInVehicle", "Vehicle.PutIn", "Vehicle.PutInPassenger", "GetInVehicleRouteAction"])
        self.assertEqual(mw.vehicle_frames(self.GET_OUT), ["Car.GetOut", "Vehicle.GetOut", "CarNpcWithNpcDriver.OnRouteFinished"])
        self.assertTrue(mw.tagged(self.GET_IN).startswith("[vehicle] T 1153"))
        plain = "state E Sit:Standing by game via StateMachineClient.EnterState<SitDown.Run"
        self.assertEqual(mw.tagged(plain), plain)

    def test_a_vehicle_line_per_side_with_the_client_only_events_in_the_drive(self) -> None:
        start = 62135596800000 + 1_760_000_000_000  # MotionWatch's .NET ms (epoch_ms)
        server = [(start, self.GET_IN), (start + 5000, "state E Walk:Standing by game via X.Y"), (start + 12000, self.GET_OUT)]
        client = [(start + 300, self.GET_IN), (start + 12400, self.GET_OUT)]
        marked = [(start + 6000, "teleport", "145.00 m in 33 ms", True), (start + 6100, "teleport", "x", True),
                  (start + 7000, "slide", "y", False), (start + 30000, "snap turn", "z", True)]
        lines = mw.vehicle_lines({"server": server, "client": client}, marked)
        self.assertEqual(len(lines), 2)
        self.assertIn("vehicle server", lines[0])
        self.assertIn("2 event(s): SittingInVehicle, Vehicle.PutIn, Vehicle.PutInPassenger, GetInVehicleRouteAction, "
                      "Car.GetOut, Vehicle.GetOut, CarNpcWithNpcDriver.OnRouteFinished", lines[0])
        self.assertNotIn("client-only", lines[0])
        self.assertTrue(lines[1].endswith("; 2 client-only event(s) in it (teleport)"))  # the snap turn is after it
        self.assertEqual(mw.vehicle_lines({"server": [(start, "state E Sit:Standing")], "client": []}, marked), [])


class MachineStateNameBase(unittest.TestCase):
    def test_state_name_hands_the_machine_to_name64_as_a_hex_int(self) -> None:
        seen = {}

        class Names:
            def name64(self, key):
                seen["machine"] = key
                return "Idle"

            def name32(self, value):
                return str(value)

        # A 16-hex machine key that is also all decimal digits: name64() alone reads it as decimal now, so state_name has
        # to hand it over as the hex int the note is written in (SacsRecorder.cs: driver.Machine.ToString("x16")).
        with mock.patch.object(mw, "names", Names()):
            mw.state_name("1234567890123456:2707378478")
        self.assertEqual(seen["machine"], int("1234567890123456", 16))


class PassThroughNames:
    def name64(self, key):
        return str(key)

    def name32(self, value):
        return str(value)


class RepairCauseTest(unittest.TestCase):
    """a client restart right after the mod's repair of that machine (run 044254: `state E Idle:standing by
    repair`, the replay's standing_idle 55 ms later, then a client-only restart) names the repair and its caller."""

    def test_the_label_gives_caller_and_entry(self) -> None:
        self.assertEqual(mw.repair_label("repair"), ("-", "-"))
        self.assertEqual(mw.repair_label("repair:z"), ("z", "-"))
        self.assertEqual(mw.repair_label("repair:po/stream"), ("po", "stream"))
        self.assertEqual(mw.repair_label("repair/default"), ("-", "default"))

    def test_a_repair_of_the_restarted_machine_is_named(self) -> None:
        restart = "Idle:standing_idle entered again after 1031 ms"
        evs = [(17988, "state E Idle:standing by repair:walk/stream"), (18043, "state R Idle:standing_idle by replay")]
        self.assertEqual(mw.repair_cause(evs, 18100, restart), "after a repair: caller walk, entry stream")
        self.assertEqual(mw.repair_cause(evs, 18100, "back to Idle:standing_idle 300 ms after Idle:x"),
                         "after a repair: caller walk, entry stream")

    def test_older_runs_plain_label_still_parses(self) -> None:
        evs = [(17988, "state E Idle:standing by repair")]
        self.assertEqual(mw.repair_cause(evs, 18100, "Idle:standing_idle entered again after 1031 ms"),
                         "after a repair: caller -, entry -")

    def test_other_machines_other_producers_and_old_repairs_are_not_causes(self) -> None:
        restart = "Idle:standing_idle entered again after 1031 ms"
        self.assertIsNone(mw.repair_cause([(17988, "state E Sit:standing by repair:z")], 18100, restart))
        self.assertIsNone(mw.repair_cause([(17988, "state E Idle:standing by replay"),
                                           (17990, "state E Idle:standing by game via X.Y"),
                                           (17991, "state E Idle:standing by repairs")], 18100, restart))
        self.assertIsNone(mw.repair_cause([(18100 - mw.CAUSE_BEFORE_MS - 1, "state E Idle:standing by repair:z")], 18100, restart))

    def test_the_report_line_and_summary_name_it(self) -> None:
        import contextlib
        import io
        import json
        import tempfile
        oid = "1"
        start = 62135596800000 + 1_760_000_000_000  # MotionWatch's .NET ms (epoch_ms)
        still = [(start + 33 * i, 0.0, 0.0, 0.0, 0.0, 1.0, False, "Idle:standing_idle", 1) for i in range(60)]
        client = [s if i < 31 else s[:8] + (2,) for i, s in enumerate(still)]
        at = client[31][0]
        events = {"server": {oid: []},
                  "client": {oid: [[at - 60, "state E Idle:standing by repair:z"], [at - 5, "state R Idle:standing_idle by replay"]]}}
        with tempfile.TemporaryDirectory() as tmp, mock.patch.object(mw, "names", PassThroughNames()), \
                mock.patch.object(mw, "speed_plot"), mock.patch.dict(mw.EVENTS, clear=True):
            Path(tmp, "samples.json").write_text(json.dumps({"ids": [oid], "data": {"server": {oid: still}, "client": {oid: client}},
                                                              "events": events}), encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()):
                code = mw.main(["--rescore", tmp, "--ids", oid])
            report = Path(tmp, "report.txt").read_text(encoding="utf-8")
            summary = json.loads(Path(tmp, "summary.json").read_text(encoding="utf-8"))
        self.assertEqual(code, 4)
        self.assertIn("restart     Idle:standing_idle entered again after 1023 ms (after a repair: caller z, entry -)   <- CLIENT-ONLY", report)
        self.assertIn("1 client-only restart(s) after a repair", report)
        self.assertEqual(summary["repair_restarts"], 1)


class RouteFieldsTest(unittest.TestCase):
    def test_server_route_notes_give_style_leg_and_halted_phase(self) -> None:
        rows = mw.route_fields([(10, "route start leg=0 style=77|1|0|0 paths=2"), (20, "route style leg=1 style=88|1|0|0"),
                                (30, "route halt leg=1 mode=2"), (40, "route end leg=1 by=x")])
        self.assertEqual([r[1:4] for r in rows[:2]], [["77|1|0|0", 0, False], ["88|1|0|0", 1, True]])
        self.assertEqual([r[4] for r in rows[2:]], ["halted", "halted"])

    def test_client_walk_notes_give_style_leg_door_and_placement(self) -> None:
        rows = mw.route_fields([(1, "walk leg 2 PortalRouteThroughAction in Walk (steering Walk)"), (2, "walk leg 3 FollowSubPathRouteAction in FastWalk (steering -)"),
                                (3, "walk style: the server's Walk on leg 4 (own FastWalk PortalTraversalAction)"), (4, "restart back to Idle"), (5, "walk end arrived at 1,2"),
                                (6, "walk end snap")])
        self.assertEqual(rows[0][1:4], ["Walk", 2, True])
        self.assertEqual(rows[1][1:4], ["FastWalk", 3, False])
        self.assertEqual(rows[2][1:4], ["Walk", 4, True])
        self.assertEqual(rows[3][5], "restart back to Idle")
        self.assertEqual(rows[4][4], "halted")

    def test_samples_json_carries_fields_per_side_and_sim(self) -> None:
        ends = {"server": "s", "client": "c"}
        ev = {"server": {("s", "1"): [(5, "route end leg=0 by=x")]}, "client": {}}
        saved = mw.saved_run(["1"], {"server": {}, "client": {}}, ev, ends)
        self.assertEqual(saved["fields"]["server"]["1"][0][4], "halted")
        self.assertEqual(saved["fields"]["client"]["1"], [])
        self.assertEqual(saved["events"]["server"]["1"], [(5, "route end leg=0 by=x")])

    def test_unrelated_events_add_no_rows(self) -> None:
        self.assertEqual(mw.route_fields([(1, "state R 1:2 by game")]), [])


if __name__ == "__main__":
    unittest.main()
