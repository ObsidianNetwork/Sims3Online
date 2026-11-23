"""python -m unittest scripts.join_race_watch_test"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import join_race_watch as w  # noqa: E402


class JoinRaceWatch(unittest.TestCase):
    def test_sims(self):
        self.assertEqual(w.parse_sims("Pablo, Iliana,Pablo"), ["Pablo", "Iliana"])
        for bad in ("", " , ", 'a"b', "x;y"):
            with self.assertRaises(ValueError):
                w.parse_sims(bad)

    def test_render(self):
        self.assertEqual(w.cs_array(["A", "B"]), 'new string[] { "A", "B" }')
        for t in (w.VIEW_TEMPLATE, w.END_TEMPLATE):
            src = w.render(t, ["Pablo"])
            self.assertNotIn("__NAMES__", src)
            self.assertIn('"Pablo"', src)

    def test_reply(self):
        out = "x\nloaded=1\nok = true\nstale=0"
        self.assertEqual(w.reply(out), "ok=true;stale=0")
        self.assertEqual(w.reply("no marker here"), "no marker here")

    def test_absence_trigger_done(self):
        self.assertEqual(w.absence("a absenceDetached:2/absenceLast:Pablo/b"), (2, "Pablo"))
        self.assertIsNone(w.absence("nothing"))
        self.assertEqual(w.find_trigger("a\n x last=send:WorldFileChunk y\nb", w.DEFAULT_TRIGGER), "x last=send:WorldFileChunk y")
        self.assertIsNone(w.find_trigger("a\nb", "zzz"))
        self.assertFalse(w.done(None, 100, 45))
        self.assertFalse(w.done(10, 55, 45))
        self.assertTrue(w.done(10, 56, 45))

    def test_args(self):
        a = w.parse_args(["--sims", "Pablo,Jennifer", "--out", "o", "--no-end", "--settle", "5"])
        self.assertEqual(a.sims, ["Pablo", "Jennifer"])
        self.assertTrue(a.no_end)
        self.assertEqual(a.settle, 5)
        self.assertEqual(a.client, "127.0.0.1:34030")


if __name__ == "__main__":
    unittest.main()
