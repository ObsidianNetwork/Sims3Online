"""python -m unittest scripts.menu_diff_test"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import menu_diff  # noqa: E402

CLIENT = ("client 2: ok=true; actor=Pablo Martinez; target=Sim:18; candidates=4; list="
          "SeatedSocialInteractionA+SeatedDefinition = Chat [Pass] | Bookshelf+ReadSomething+Definition = Read [Pass]"
          " | Sim+Chat+Definition = Chat [Def_TestFailed] | YouShould+Definition = Chat with...  [Pass]")
SERVER = ("candidates: SeatedSocialInteractionA+SeatedDefinition 'Chat' Pass | Bookshelf+ReadSomething+Definition 'Read'"
          " Def_TestFailed (Too far away) | SeatedSocialInteractionA+SeatedDefinition 'Gossip' Pass"
          " | Sim+Chat+Definition 'Chat' Def_TestFailed | Sim+Hug+Definition 'Ask Someone's Mother' GenericFail")


class Parse(unittest.TestCase):
    def test_client(self):
        actor, entries = menu_diff.parse_client(CLIENT)
        self.assertEqual(actor, "Pablo Martinez")
        self.assertEqual(entries[("SeatedSocialInteractionA+SeatedDefinition", "Chat")], "Pass")
        self.assertEqual(entries[("YouShould+Definition", "Chat with...")], "Pass")
        self.assertEqual(len(entries), 4)

    def test_client_without_results(self):
        _, entries = menu_diff.parse_client("ok=true; actor=J; list=A = Relax | B = Sleep")
        self.assertEqual(entries, {("A", "Relax"): "?", ("B", "Sleep"): "?"})

    def test_client_state_after_the_result(self):
        reply = "ok=true; actor=J; list=A = Relax [Pass] | B = Sleep [Def_TestFailed|hidden] | C = Nap [GenericFail|grey]"
        _, entries = menu_diff.parse_client(reply)
        self.assertEqual(entries, {("A", "Relax"): "Pass", ("B", "Sleep"): "Def_TestFailed", ("C", "Nap"): "GenericFail"})
        self.assertEqual(menu_diff.parse_client_states(reply), {("B", "Sleep"): "hidden", ("C", "Nap"): "grey"})

    def test_server_names_with_quotes(self):
        entries = menu_diff.parse_server(SERVER)
        self.assertEqual(entries[("Bookshelf+ReadSomething+Definition", "Read")], ("Def_TestFailed", "Too far away"))
        self.assertEqual(entries[("Sim+Hug+Definition", "Ask Someone's Mother")], ("GenericFail", ""))


class Targets(unittest.TestCase):
    def test_forms(self):
        self.assertEqual(menu_diff.targets("Jennifer"), ("sim:Jennifer", "sim:Jennifer"))
        self.assertEqual(menu_diff.targets("sim:Jennifer"), ("sim:Jennifer", "sim:Jennifer"))
        self.assertEqual(menu_diff.targets("type:Bookshelf"), ("Bookshelf", "near:Bookshelf"))

    def test_ids(self):
        self.assertEqual(menu_diff.target_id("ok=true; target=BookshelfCountryMed2x1:7787; list="), "7787")
        self.assertEqual(menu_diff.target_id("actor=Pablo target=Sim:18031287978496800304\ncandidates: x"),
                         "18031287978496800304")


class Diff(unittest.TestCase):
    def test_groups(self):
        _, client = menu_diff.parse_client(CLIENT)
        groups = menu_diff.diff(client, menu_diff.parse_server(SERVER))
        self.assertEqual([k[1] for k, _, _ in groups["shown, server refuses"]], ["Read"])
        self.assertEqual([k[1] for k, _, _ in groups["missing on the client"]], ["Gossip"])
        self.assertEqual([k[0] for k, _, _ in groups["client only"]], ["YouShould+Definition"])
        self.assertEqual(len(groups["agree"]), 2)

    def test_filter(self):
        self.assertTrue(menu_diff.keep(("SeatedSocialInteractionA+SeatedDefinition", "Chat"), "social", "chat"))
        self.assertFalse(menu_diff.keep(("Bookshelf+ReadSomething+Definition", "Read"), "social", ""))


class HiddenAndGreyed(unittest.TestCase):
    CL = ("ok=true; actor=J; list=Z+Def = Zombified [Def_TestFailed|hidden] | A+Def = Both [Def_TestFailed|hidden]"
          " | G+Def = Grey [Def_TestFailed|grey] | H+Def = HidSrvPass [Def_TestFailed|hidden] | S+Def = Shown [Pass]")
    SV = ("candidates: A+Def 'Both' Def_TestFailed | G+Def 'Grey' Pass | H+Def 'HidSrvPass' Pass"
          " | X+Def 'SrvHidden' Def_TestFailed | Y+Def 'SrvGrey' Def_TestFailed (Too far)")

    def groups(self):
        _, client = menu_diff.parse_client(self.CL)
        return menu_diff.diff(client, menu_diff.parse_server(self.SV), menu_diff.parse_client_states(self.CL))

    def test_hidden_only_differences_are_not_failures(self):
        g = self.groups()
        self.assertEqual([k[1] for k, _, _ in g["hidden on both sides"]], ["Both"])
        self.assertEqual(sorted(k[1] for k, _, _ in g["hidden on one side, absent on the other"]), ["SrvHidden", "Zombified"])
        self.assertEqual([k[1] for k, _, _ in g["client only"]], ["Shown"])
        fails = menu_diff.exact_failures(g)
        self.assertFalse([f for f in fails if "Zombified" in f or "Both" in f or "SrvHidden" in f])

    def test_greyed_or_hidden_against_a_server_pass_still_fails(self):
        g = self.groups()
        self.assertEqual({k[1]: r for k, r, _ in g["missing on the client"]},
                         {"Grey": "client Def_TestFailed greyed", "HidSrvPass": "client Def_TestFailed hidden"})

    def test_shown_against_absent_still_fails(self):
        self.assertTrue([f for f in menu_diff.exact_failures(self.groups()) if "Shown" in f])

    def test_hidden_client_only_candidate_is_not_an_exact_failure(self):
        reply = ("ok=true; actor=J; list=Sim+ZombifiedSocials+Definition = Argggh "
                 "[Def_TestFailed|hidden]")
        _, client = menu_diff.parse_client(reply)
        groups = menu_diff.diff(client, {}, menu_diff.parse_client_states(reply))
        key = ("Sim+ZombifiedSocials+Definition", "Argggh")

        self.assertEqual([row[0] for row in groups[menu_diff.HIDDEN_ONE]], [key])
        self.assertEqual(groups["client only"], [])
        self.assertEqual(menu_diff.exact_failures(groups), [])

    def test_shown_or_grey_client_only_candidate_fails_exact(self):
        reply = ("ok=true; actor=J; list=Shown+Definition = Shown [Pass] | "
                 "Grey+Definition = Grey [Def_TestFailed|grey]")
        _, client = menu_diff.parse_client(reply)
        groups = menu_diff.diff(client, {}, menu_diff.parse_client_states(reply))

        self.assertEqual([row[0][1] for row in groups["client only"]], ["Grey", "Shown"])
        self.assertEqual(menu_diff.exact_failures(groups),
                         ["client only: Grey+Definition 'Grey' Def_TestFailed",
                          "client only: Shown+Definition 'Shown' Pass"])

    def test_without_states_nothing_changes(self):
        _, client = menu_diff.parse_client(self.CL)
        g = menu_diff.diff(client, menu_diff.parse_server(self.SV))
        self.assertEqual(g["hidden on both sides"], [])
        self.assertEqual(len(g["client only"]), 2)


class Exact(unittest.TestCase):
    def test_failures(self):
        _, client = menu_diff.parse_client(CLIENT)
        groups = menu_diff.diff(client, menu_diff.parse_server(SERVER))
        self.assertEqual(menu_diff.exact_failures(groups),
                         ["missing on the client: SeatedSocialInteractionA+SeatedDefinition 'Gossip' not listed",
                          "client only: YouShould+Definition 'Chat with...' Pass"])

    def test_none_when_menus_match(self):
        groups = {"shown, server refuses": [], "missing on the client": [], "client only": [], "agree": [(("A", "x"), "Pass", "")]}
        self.assertEqual(menu_diff.exact_failures(groups), [])

    def test_step(self):
        self.assertEqual(menu_diff.parse_step("1:Jennifer:Iliana:exact"), ("1", "Jennifer", "Iliana", "", True))
        self.assertEqual(menu_diff.parse_step("1:Jennifer:Iliana"), ("1", "Jennifer", "Iliana", "", False))
        self.assertEqual(menu_diff.parse_step("2:Pablo:type:Bookshelf:only=Read:exact"),
                         ("2", "Pablo", "type:Bookshelf", "Read", True))
        self.assertEqual(menu_diff.parse_step("2:Pablo:type:Bookshelf:only=Read"), ("2", "Pablo", "type:Bookshelf", "Read", False))


class Inputs(unittest.TestCase):
    def test_parse(self):
        out = "-- DIAG_RUN --\n    ok = true\n    aPos = 1.00,2.00,3.00\n    aIsOutside = 1\n"
        self.assertEqual(menu_diff.parse_inputs(out), {"ok": "true", "aPos": "1.00,2.00,3.00", "aIsOutside": "1"})

    def test_diff_marks_differences(self):
        server = {"ok": "true", "aPos": "1.00,2.00,3.00", "aIsOutside": "1", "tRoomOutside": "1"}
        client = {"ok": "true", "aPos": "1.05,2.00,3.00", "aIsOutside": "0", "season": "off"}
        rows = menu_diff.inputs_diff(server, client)
        self.assertEqual(rows, [("aPos", "1.00,2.00,3.00", "1.05,2.00,3.00", False), ("aIsOutside", "1", "0", True),
                                ("tRoomOutside", "1", "-", True), ("season", "-", "off", True)])
        self.assertEqual(menu_diff.inputs_report(rows)[-1], "3 of 4 rows differ")

    def test_position_beyond_tolerance_differs(self):
        rows = menu_diff.inputs_diff({"tPos": "1.00,2.00,3.00"}, {"tPos": "1.50,2.00,3.00"})
        self.assertTrue(rows[0][3])

    def test_source_fills_names(self):
        source = menu_diff.inputs_source("Jennifer", "Iliana")
        self.assertIn('ActorName = "Jennifer"', source)
        self.assertIn('TargetName = "Iliana"', source)
        self.assertNotIn("__", source.split("namespace", 1)[1].replace("__ACTOR__", ""))


class Sweep(unittest.TestCase):
    def test_targets(self):
        entries = [{"object": "BookshelfCountry"}, {"object": "FridgeCheap"}, {"object": "BookshelfCountry"}]
        self.assertEqual(menu_diff.sweep_targets(entries, ["Jennifer"], []),
                         ["Jennifer", "type:BookshelfCountry", "type:FridgeCheap"])
        self.assertEqual(menu_diff.sweep_targets(entries, [], ["Stove"]), ["type:Stove"])

    def test_tally(self):
        _, client = menu_diff.parse_client(CLIENT)
        groups = menu_diff.diff(client, menu_diff.parse_server(SERVER))
        t = menu_diff.tally({"Jennifer": groups, "type:Fridge": None})
        self.assertEqual((t["targets"], t["listed"], t["unlisted"]), (2, 1, ["type:Fridge"]))
        self.assertEqual(t["groups"]["shown, server refuses"], 1)
        self.assertEqual(t["refused"], [{"target": "Jennifer", "type": "Bookshelf+ReadSomething+Definition", "name": "Read",
                                         "result": "Def_TestFailed", "reason": "Too far away"}])
        lines = menu_diff.sweep_report(t)
        self.assertTrue(lines[0].startswith("1 of 2 targets listed"))
        self.assertIn("shown, server refuses, by the server's result: Def_TestFailed 1", lines)


if __name__ == "__main__":
    unittest.main()
