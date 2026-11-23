"""Offline tests for scripts/household_read.py (no game needed): python -m unittest scripts.household_read_test"""
from __future__ import annotations

import sys
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import household_read  # noqa: E402

MARTINEZ = ("id=379;funds=7325;home=7787005350115303920;address=Pre-fabulous;active=1;members="
            "Jennifer:29206:18031287978496800304:7787005350115303920:YoungAdult,"
            "Pablo:29207:18031287978496800272:7787005350115303920:YoungAdult")
LANGERAK = ("id=102;funds=4056;home=7787005346371421056;address=14 Sierra Tango Street;active=0;members="
            "Iliana:5001:14167198646267834864:7787005346371421056:Adult,Lars:5002:0:0:Child")


class ParseTest(unittest.TestCase):
    def test_probe_text_fills_both_lists(self):
        text = household_read.probe_text(["Langerak", "Martinez"], ["Iliana"])
        self.assertIn('Households = "Langerak,Martinez"', text)
        self.assertIn('Sims = "Iliana"', text)
        self.assertNotIn("__HOUSEHOLDS__", text)
        self.assertNotIn("__SIMS__", text)

    def test_parse_reads_active_households_and_members(self):
        fields = {
            ("-", "active"): ["Jennifer:29206:18031287978496800304:7787005350115303920:YoungAdult:Martinez"],
            ("Martinez", "household"): [MARTINEZ],
            ("Langerak", "household"): [LANGERAK],
        }
        reading = household_read.parse(fields)
        self.assertIsNone(reading["error"])
        self.assertEqual(reading["active"]["name"], "Jennifer")
        self.assertEqual(reading["active"]["household"], "Martinez")
        self.assertTrue(reading["households"]["Martinez"]["active"])
        lang = reading["households"]["Langerak"]
        self.assertFalse(lang["active"])
        self.assertEqual(lang["funds"], "4056")
        self.assertEqual(lang["address"], "14 Sierra Tango Street")
        self.assertEqual([m["name"] for m in lang["members"]], ["Iliana", "Lars"])
        self.assertEqual(lang["members"][1]["object"], "0")
        lines = household_read.summary("server", reading)
        self.assertIn("selected Jennifer", lines[0])
        self.assertIn("Martinez (active): funds 7325", lines[1])
        self.assertIn("Lars (Child, sd 5002, not in the world)", lines[2])

    def test_parse_reports_a_probe_error(self):
        reading = household_read.parse({("-", "error"): ["ok=false;err=NullReferenceException:x"]})
        self.assertIn("NullReferenceException", reading["error"])
        self.assertIsNone(reading["active"])
        self.assertEqual(household_read.summary("client 2", reading), [f"client 2: error: {reading['error']}"])

    def test_no_selected_sim(self):
        self.assertIsNone(household_read.parse({("-", "active"): ["-"]})["active"])

    def test_pairs_are_read_and_summarised(self):
        text = household_read.probe_text([], None, ["Jennifer+Iliana", "Pablo+Zelda"])
        self.assertIn('Pairs = "Jennifer+Iliana,Pablo+Zelda"', text)
        self.assertNotIn("__PAIRS__", household_read.probe_text([]))
        reading = household_read.parse({("Jennifer+Iliana", "pair"): ["12.5 Acquaintance"],
                                        ("Pablo+Zelda", "pair"): ["none"]})
        self.assertEqual(reading["pairs"], {"Jennifer+Iliana": "12.5 Acquaintance", "Pablo+Zelda": "none"})
        lines = household_read.summary("client 1", reading)
        self.assertIn("  Jennifer+Iliana: 12.5 Acquaintance", lines)
        self.assertIn("  Pablo+Zelda: none", lines)


class SimIdTest(unittest.TestCase):
    def test_pick_prefers_a_family_sim_in_the_world(self):
        found = [
            {"name": "Iliana", "object": "0", "has_home": True},
            {"name": "Iliana", "object": "111", "has_home": False},
            {"name": "Iliana", "object": "222", "has_home": True},
        ]
        self.assertEqual(household_read.pick_sim(found)["object"], "222")
        self.assertIsNone(household_read.pick_sim([{"name": "X", "object": "0", "has_home": True}]))
        self.assertIsNone(household_read.pick_sim([]))

    def test_known_and_numeric_names_need_no_game(self):
        with mock.patch.object(household_read, "read", side_effect=AssertionError("no probe")):
            self.assertEqual(household_read.sim_id("Pablo"), household_read.KNOWN["pablo"])
            self.assertEqual(household_read.sim_id("123"), "123")

    def test_other_names_are_read_once(self):
        household_read._IDS.clear()
        reading = household_read.parse({("Iliana", "sim"): ["Iliana:5001:14167198646267834864:7787005346371421056:Adult:Langerak:1"]})
        with mock.patch.object(household_read, "read", return_value=reading) as read:
            self.assertEqual(household_read.sim_id("Iliana"), "14167198646267834864")
            self.assertEqual(household_read.sim_id("iliana"), "14167198646267834864")
            self.assertEqual(read.call_count, 1)
        with mock.patch.object(household_read, "read", return_value=household_read.parse({})):
            with self.assertRaises(SystemExit):
                household_read.sim_id("Nobody")

    def test_games(self):
        self.assertEqual(list(household_read.games("all")), ["server", "client 1", "client 2"])
        self.assertEqual(household_read.games("server"), {"server": household_read.SERVER})
        self.assertEqual(list(household_read.games("client2")), ["client 2"])


if __name__ == "__main__":
    unittest.main()
