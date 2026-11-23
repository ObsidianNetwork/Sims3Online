"""Unit tests for stream_kinds' parsers (no game, no bridge): python -m unittest scripts.stream_kinds_test"""
from __future__ import annotations

import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import stream_kinds as sk  # noqa: E402

PUSH = ("push=on,cursor=13857,sequence=13857,targets=2,pages=369,lines=1013,drops=0,behind=0,errors=0,lastError=-,"
        "runs=4585,every=1,simTick=5314,sentByKind=A:40/AS:12/AW:11/P:300;recorder=on;ring=on")
SKIPS = "skips=9;sites=6900:2,6982:6,7960:3;receivedByKind=A:40/AS:12/P:300\n6982|5865|AS|552120045|1|2\n7960|77|AW|5|1|0:0:0:0"
REPLAY = "applied=0;skipped=0;presentation=module=x;routeLines=held:20,linesReleased:9,linesDropped:0,other=1"


class StreamKindsTest(unittest.TestCase):
    def test_parsers_read_the_mod_replies(self):
        self.assertEqual(sk.parse_kinds(PUSH, "sentByKind"), {"A": 40, "AS": 12, "AW": 11, "P": 300})
        self.assertEqual(sk.parse_kinds(SKIPS, "receivedByKind"), {"A": 40, "AS": 12, "P": 300})
        self.assertEqual(sk.parse_kinds("sentByKind=-;x=1", "sentByKind"), {})
        self.assertEqual(sk.parse_kinds("push=on", "sentByKind"), {}, "an older mod: no field")
        self.assertEqual(sk.parse_sites(SKIPS), {"6900": 2, "6982": 6, "7960": 3})
        self.assertEqual(sk.parse_route(REPLAY), {"held": 20, "released": 9, "dropped": 0})
        self.assertEqual(sk.parse_fields("walkerCopy=on,awLines=11,awApplied=0", ("awLines", "awApplied", "awFailed")),
                         {"awLines": 11, "awApplied": 0})

    def test_rows_flag_kinds_sent_and_not_received_and_show_what_moved(self):
        rows = sk.kind_rows({"AW": 11, "AS": 12}, {"AS": 12}, None, None)
        self.assertEqual(len(rows), 2)
        self.assertIn("not received", rows[1])
        self.assertNotIn("not received", rows[0])
        moved = sk.kind_rows({"AW": 15}, {"AW": 4}, ["AW"], {"sent": {"AW": 11}, "received": {}})
        self.assertIn("+4 sent, +4 received", moved[0])

    def test_render_names_the_walk_hold_and_the_skipped_lines_of_the_asked_kinds(self):
        now = {"time": "t", "sent": {"AW": 11}, "received": {"AW": 11}, "sites": {"7960": 3},
               "route": {"held": 20, "released": 9, "dropped": 0}, "walker_server": {"awLines": 11},
               "walker_client": {"awApplied": 0}, "skip_rows": ["7960|77|AW|5|1|0:0:0:0", "6982|1|AS|2|3|4"]}
        text = sk.render(now, None, ["AW"])
        self.assertIn("at most 11", text)
        self.assertIn("7960:3", text)
        self.assertIn("skipped: 7960|77|AW|", text)
        self.assertNotIn("6982|1|AS", text)

    def test_one_kinds_skips_by_site_replace_the_ring_filter(self):
        reply = "skips=9;sites=6982:6,7960:3;receivedByKind=AW:5;kind=AW;bySite=6982:1,7960:2\n6982|9|AW|9|1|2:0:3:0"
        self.assertEqual(sk.kind_skips(reply), {"sites": {"6982": 1, "7960": 2}, "rows": ["6982|9|AW|9|1|2:0:3:0"]})
        self.assertEqual(sk.kind_skips("skips=0;sites=;receivedByKind=-;kind=AS;bySite=-"), {"sites": {}, "rows": []})
        self.assertIsNone(sk.kind_skips(SKIPS), "a mod before SkipsByKind: no kind= in the head")
        self.assertEqual(sk.parse_sites(reply), {"6982": 6, "7960": 3}, "sites= is not bySite=")
        now = {"time": "t", "sent": {"AW": 5}, "received": {"AW": 5}, "sites": {}, "route": {}, "walker_server": {},
               "walker_client": {}, "skip_rows": ["6982|1|AW|2|3|4"],
               "skips_by_kind": {"AW": {"sites": {"6982": 1}, "rows": ["6982|9|AW|9|1|2:0:3:0"]}}}
        text = sk.render(now, None, ["AW"])
        self.assertIn("AW skipped by site: 6982:1", text)
        self.assertIn("skipped: 6982|9|AW|9", text)
        self.assertNotIn("6982|1|AW|2", text, "the per-kind rows replace the ring filter")

    def test_the_gap_is_taken_on_the_lines_queued_for_this_clients_player(self):
        self.assertEqual(sk.parse_players("p1=AS:2/AW:5;p2=AS:1"), {1: {"AS": 2, "AW": 5}, 2: {"AS": 1}})
        self.assertEqual(sk.parse_players("p2=-"), {2: {}})
        self.assertEqual(sk.parse_players("-"), {}, "nothing queued yet")
        self.assertIsNone(sk.parse_players("unknown command PRESENTATION_SENT"), "an older controller: the mod answers")
        self.assertIsNone(sk.parse_players(""))
        # Client 2 joined late: the mod's total has 2 AS lines it never got, the controller queued none for it.
        rows = sk.kind_rows({"AS": 2}, {}, ["AS"], None, {}, 2)
        self.assertIn("to p2       0", rows[0])
        self.assertIn("gap      0", rows[0])
        self.assertNotIn("not received", rows[0])
        lost = sk.kind_rows({"AS": 4}, {"AS": 1}, ["AS"], {"sent": {"AS": 2}, "queued": {"AS": 1}, "received": {}},
                            {"AS": 3}, 2)
        self.assertIn("gap      2", lost[0])
        self.assertIn("+2 sent, +2 to p2, +1 received", lost[0])
        self.assertIn("not received", lost[0])

    def test_each_client_compares_with_its_own_last_read(self):
        import tempfile
        with tempfile.TemporaryDirectory() as tmp:
            paths = []
            for i, read in enumerate([{"time": "old"}, {"time": "c2", "client": sk.ENDPOINTS["client2"]},
                                      {"time": "c1", "client": sk.ENDPOINTS["client"]},
                                      {"time": "c2b", "client": sk.ENDPOINTS["client2"]}]):
                path = Path(tmp) / f"{i}.json"
                path.write_text(json.dumps(read), encoding="utf-8")
                paths.append(path)
            self.assertEqual(sk.last_read(paths, sk.ENDPOINTS["client"])["time"], "c1")
            self.assertEqual(sk.last_read(paths, sk.ENDPOINTS["client2"])["time"], "c2b")
            self.assertEqual(sk.last_read(paths[:1], sk.ENDPOINTS["client"])["time"], "old", "an unlabelled read is client 1's")
            self.assertIsNone(sk.last_read(paths[:1], sk.ENDPOINTS["client2"]))
        self.assertEqual(sk.client_player("client2", 5), 5)
        self.assertEqual(sk.client_player("127.0.0.1:40000", None), 1)


if __name__ == "__main__":
    unittest.main()
