"""Unit tests for stream_gaps: python -m unittest scripts.stream_gaps_test"""
from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import stream_gaps as sg  # noqa: E402

# client2.log lines as the controller writes them (archive 2026-10-21-065556-auto, trimmed)
LOG = """Host mod build: e1a33be@2026-10-20T19:33:37Z
[2026-10-20 19:41:17.711Z] received: {"$type":"world_time_changed","time":{"day":1}}
[2026-10-20 19:41:18.006Z] received: {"$type":"sim_animation","lines":["17578|P|538911814|3485968198|1668749452"]}
[2026-10-20 19:41:18.063Z] received: {"$type":"sim_animation","lines":["17600|R|553383483|84696423|2689084685"]}
[2026-10-20 19:41:18.361Z] received_snapshot: {"type":"snapshot_delta","tick":10808,"bytes":621}
[2026-10-20 19:41:18.857Z] snapshot_ack_sent: {"commandId":"b44c8d63","tick":10808}
[2026-10-20 19:41:19.000Z] received: {"$type":"sim_spawned","simObjectId":18031287978496800304}
[2026-10-20 19:41:19.100Z] received: {"$type":"sim_animation","lines":["17681|P|553383483|3485968198|1668749452"]}
[2026-10-20 19:41:20.000Z] received: {"$type":"session_joined","playerId":1,"displayName":"dev-session-1"}
[2026-10-20 19:41:22.200Z] received: {"$type":"sim_animation","lines":["17731|P|538911814|3485968198|1668749452"]}
simlink-client-shadow] untimed
[2026-10-20 19:41:22.500Z] received: {"$type":"sim_animation","lines":["17735|P|538911814|2801480393|1668749452"]}
"""

EVENTS = """19:41:18.036  server Jennifer  state R e9a2077ed45629ff:2689084685 by game via ...
19:41:18.653  client Jennifer  walk start from 1128.40,988.00 to 1129.36,988.99 to the server's slot in Walk
19:41:18.655  client Jennifer  walk leg 0 FollowSubPathRouteAction in Walk (steering Walk)
19:41:19.453  client Jennifer  server walk ended at 1129.36,988.99 (by inactive, 22 ms ago) (client still walking)
19:41:20.518  client Jennifer  walk stop: longer than the server's walk (overrun)
19:41:20.754  client Pablo     walk start from 1127.40,988.90 to 1127.36,988.24 to the server's slot in Walk
19:41:21.185  client Jennifer  walk end failed -5652481 (stopped) at 1129.36,988.98
19:41:22.385  client Pablo     walk stop: longer than the server's walk (overrun)
"""


class Parse(unittest.TestCase):
    def test_received_lines_are_their_type_and_untimed_lines_are_left_out(self) -> None:
        kinds = [ln.kind for ln in sg.parse(LOG)]
        self.assertEqual(kinds[:5], ["world_time_changed", "sim_animation", "sim_animation", "snapshot_delta",
                                     "snapshot_ack_sent"])
        self.assertEqual(len(kinds), 10)  # the build stamp and the shadow line have no time
        self.assertEqual(sg.clock(sg.parse(LOG)[1].at), "19:41:18.006")


class Gaps(unittest.TestCase):
    def test_a_gap_names_the_last_notable_line_before_it_and_what_came_inside(self) -> None:
        found = sg.gaps(sg.parse(LOG), minimum=1.5)
        self.assertEqual(len(found), 1)
        gap = found[0]
        self.assertAlmostEqual(gap.seconds, 3.1)
        self.assertEqual(sg.clock(gap.start), "19:41:19.100")
        self.assertEqual(gap.before.kind, "sim_spawned")  # the delta and the ack are routine
        self.assertEqual(gap.inside, ["session_joined"])
        self.assertEqual(sg.gap_text(gap), "19:41:19.100 silent 3.1 s, after sim_spawned 0.1 s before; inside: session_joined")

    def test_a_shorter_minimum_finds_more_and_a_notable_line_too_long_before_is_not_named(self) -> None:
        self.assertEqual(len(sg.gaps(sg.parse(LOG), minimum=0.9)), 2)  # 18.063 -> 19.100 too
        gap = sg.gaps(sg.parse(LOG), minimum=1.5, lookback=0.05)[0]
        self.assertIsNone(gap.before)
        self.assertIn("nothing notable before it", sg.gap_text(gap))

    def test_longest_silence_overlapping_a_window(self) -> None:
        lines = sg.parse(LOG)
        at = lines[1].at
        self.assertAlmostEqual(sg.longest(lines, at.replace(second=18, microsecond=500000),
                                          at.replace(second=18, microsecond=700000)), 1.037)
        self.assertAlmostEqual(sg.longest(lines, at.replace(second=22, microsecond=300000),
                                          at.replace(second=22, microsecond=400000)), 0.3)


class Walks(unittest.TestCase):
    def test_a_walk_runs_to_its_end_and_to_its_stop_only_when_no_end_follows(self) -> None:
        # Jennifer's stop (20.518, the overrun decision) comes before her end (21.185, the copy still moving)
        self.assertEqual(sg.walks(EVENTS), [("Jennifer", "19:41:18.653", "19:41:21.185"),
                                            ("Pablo", "19:41:20.754", "19:41:22.385")])

    def test_a_walk_with_no_end_runs_to_the_next_start_or_stays_open(self) -> None:
        events = ("19:00:01.000  client Pablo     walk start from a to b\n"
                  "19:00:03.000  client Pablo     walk start from b to c\n")
        self.assertEqual(sg.walks(events), [("Pablo", "19:00:01.000", "19:00:03.000"), ("Pablo", "19:00:03.000", "")])


class Motion(unittest.TestCase):
    def test_the_log_whose_times_cover_the_walks_is_used(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            run = Path(tmp) / ".omo" / "run"
            folder = run / "motion" / "064112-c2"
            folder.mkdir(parents=True)
            (folder / "events.txt").write_text(EVENTS, encoding="utf-8")
            (run / "client2.log").write_text('[2026-10-20 20:18:40.100Z] client_start: {}\n', encoding="utf-8")
            (run / "client.log").write_text(LOG, encoding="utf-8")  # client 1's: never read for a -c2 folder
            archived = run / "archive" / "2026-10-21-065556-auto"
            archived.mkdir(parents=True)
            (archived / "client2.log").write_text(LOG, encoding="utf-8")
            out = sg.motion_report(folder, 1.5)
        self.assertIn("2026-10-21-065556-auto", out[0])
        self.assertEqual(len(out), 3)
        self.assertIn("Jennifer walk 19:41:18.653-19:41:21.185: longest silence inside 3.1 s", out[1])
        self.assertIn("overlaps the walk: 19:41:19.100 silent 3.1 s, after sim_spawned", out[1])
        self.assertIn("Pablo walk 19:41:20.754-19:41:22.385", out[2])

    def test_no_covering_log_says_so(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp) / ".omo" / "run" / "motion" / "064112"
            folder.mkdir(parents=True)
            (folder / "events.txt").write_text(EVENTS, encoding="utf-8")
            self.assertIn("no client log of this checkout covers", sg.motion_report(folder, 1.5)[0])


class Window(unittest.TestCase):
    def test_the_gaps_overlapping_a_window_and_their_record_line(self) -> None:
        lines = sg.parse(LOG)
        day = lines[0].at
        inside = sg.window_gaps(lines, day.replace(second=20), day.replace(second=21))
        self.assertEqual([sg.silent_text(g) for g in inside],
                         ["stream silent 3.1 s at 19:41:19.1 (after sim_spawned; inside: session_joined)"])
        self.assertEqual(sg.window_gaps(lines, day.replace(second=22, microsecond=300000), day.replace(second=23)), [])

    def test_the_log_covering_a_window_live_first_then_archives(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            run = Path(tmp) / ".omo" / "run"
            archived = run / "archive" / "2026-10-21-065556-auto"
            archived.mkdir(parents=True)
            (archived / "client2.log").write_text(LOG, encoding="utf-8")
            (run / "client2.log").write_text("[2026-10-20 20:18:40.100Z] client_start: {}\n", encoding="utf-8")
            at = sg.parse(LOG)[2].at
            self.assertEqual(sg.covering_log("client2.log", at, at, [run]), archived / "client2.log")
            self.assertEqual(sg.span(archived / "client2.log")[1], sg.parse(LOG)[-1].at)
            later = at.replace(hour=20, minute=18, second=45)  # a live log covers to just after its last line
            self.assertEqual(sg.covering_log("client2.log", later, later, [run]), run / "client2.log")
            self.assertIsNone(sg.covering_log("client2.log", at.replace(hour=18), at, [run]))


if __name__ == "__main__":
    unittest.main()
