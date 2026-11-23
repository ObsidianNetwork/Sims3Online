"""python -m unittest scripts.object_diff_test"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import object_diff  # noqa: E402

# The server: its own delivered paper (runtime), an old paper, a chair, a paper out of the client's range.
SERVER = ("ok=true;sims=2;objects=4;ids=0\nobj=Newspaper|100|100|10.0,5.0|1|0|-\nobj=Newspaper|500|500|11.0,5.0|1|0|-\n"
          "obj=ChairDining|200|200|3.0,4.0|0|0|-\nobj=Newspaper|600|600|30.0,2.0|1|0|-")
# The client: the copy of 100 (by the server's id), its own paper 300, a lamp 400 the server has out of its range; it
# answers for the server's ids: 100 its copy, 200 the save object, 500 a hidden copy, 600 nothing.
CLIENT = ("ok=true;sims=2;objects=4;ids=4\nobj=Newspaper|100|9001|10.0,5.0|1|0|-\nobj=Newspaper|300|300|10.5,5.0|1|0|-\n"
          "obj=FloorLamp|400|400|20.0,1.0|0|0|-\nobj=ChairDining|200|200|3.0,4.0|0|0|-\n"
          "id=100|Newspaper|9001|0|1\nid=200|ChairDining|200|0|1\nid=500|Newspaper|9002|4294967295|0\nid=600|-|0|0|0")
# The server's answer for the client's candidates 300 and 400.
SERVER_HAS = "ok=true;sims=2;objects=0;ids=2\nid=300|-|0|0|0\nid=400|FloorLamp|400|0|1"


class ObjectDiff(unittest.TestCase):
    def test_parse(self):
        objects, ids = object_diff.parse(CLIENT)
        self.assertEqual(objects["100"]["local"], "9001")
        self.assertTrue(objects["300"]["runtime"])
        self.assertEqual(ids["600"]["type"], None)
        self.assertTrue(ids["500"]["hidden"])
        self.assertIsNone(object_diff.parse("probe failed: compile error"))

    def test_parse_reads_bridgectls_printed_reply(self):
        # bridgectl probe prints the first reply line as aligned fields and the detail lines indented below them.
        printed = ("loaded=64e05ea1  type=SimsOnline.Scratch.ObjectListProbe\n  ok      = true\n  sims    = 2\n"
                   "  objects = 1\n  ids     = 0\n    obj=Newspaper|100|100|1.0,1.0|1|0|-\n")
        objects, ids = object_diff.parse(printed)
        self.assertEqual(["100"], list(objects))
        self.assertTrue(objects["100"]["runtime"])
        self.assertEqual({}, ids)
        self.assertIsNone(object_diff.parse("loaded=1  type=T\n  ok = false\n  err = boom\n"))

    def test_diff_finds_the_clients_own_the_missing_copy_and_the_hidden_one(self):
        server, _ = object_diff.parse(SERVER)
        client, client_has = object_diff.parse(CLIENT)
        _, server_has = object_diff.parse(SERVER_HAS)
        found = object_diff.diff(server, client, server_has, client_has)
        self.assertEqual([w for w, _ in found["client only"]], ["300"])  # 400: the server has it, out of its range only
        self.assertEqual([w for w, _ in found["no client copy"]], ["600"])
        self.assertEqual([w for w, _ in found["hidden on the client"]], ["500"])
        lines = object_diff.report(found, server, client)
        self.assertEqual(lines[0], "server 4 objects near, client 4; client only: 1, no client copy: 1, hidden on the client: 1")
        self.assertIn("  client only: Newspaper 300 at 10.5,5.0 runtime", lines)

    def test_nothing_differs(self):
        server, _ = object_diff.parse("ok=true\nobj=Newspaper|100|100|1.0,1.0|1|0|-")
        client, client_has = object_diff.parse("ok=true\nobj=Newspaper|100|9001|1.0,1.0|1|0|-\nid=100|Newspaper|9001|0|1")
        self.assertFalse(any(object_diff.diff(server, client, {}, client_has).values()))

    def test_probe_source_fills_every_placeholder(self):
        source = object_diff.source("Jennifer,Pablo", 40.0, "Newspaper", ["100", "600"])
        self.assertNotIn("__", source)
        self.assertIn('Ids = "100,600"', source)
        self.assertIn('Types = "Newspaper"', source)
        self.assertIn("Radius = 40.0f", source)


if __name__ == "__main__":
    unittest.main()
