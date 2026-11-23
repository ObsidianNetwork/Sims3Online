"""Unit tests for persist_cycle.py: python -m unittest scripts.persist_cycle_test"""
from __future__ import annotations

import contextlib
import copy
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import persist_cycle  # noqa: E402


def fields(**sims: dict) -> dict[tuple[str, str], list[str]]:
    """A probe reply as sim_compare.run_side gives it: {(key, field): [values]}."""
    out: dict[tuple[str, str], list[str]] = {("-", "save"): ["Option4BookChain.sims3"], ("-", "ticks"): ["1234"]}
    for name, f in sims.items():
        out[(name, "pos")] = [f.get("pos", "1.00,0.00,2.00")]
        out[(name, "outfit")] = [f.get("outfit", "Everyday:0")]
        out[(name, "hand.right")] = [f.get("right", "-")]
        out[(name, "hand.left")] = ["-"]
        out[(name, "funds")] = [f.get("funds", "20000")]
        out[(name, "act")] = [f.get("act", "-")]
        if f.get("inv"):
            out[(name, "inv")] = f["inv"]
    out[("Jennifer+Pablo", "pair")] = ["35.00 Friend"]
    return out


BEFORE = fields(Jennifer={"right": "BookGeneral#101", "inv": ["Phone#7"], "act": "Book+ReadBook"}, Pablo={"outfit": "Formalwear:1"})


class ParseMark(unittest.TestCase):
    def test_fields_become_sims_pairs_and_objects(self) -> None:
        f = dict(BEFORE)
        f[("202", "obj")] = ["ChairDiningWood 3.00,0.00,4.50 parent=- inv=0"]
        f[("303", "obj")] = ["missing"]
        mark = persist_cycle.parse_mark(f)
        self.assertEqual(mark["save"], "Option4BookChain.sims3")
        self.assertEqual(mark["ticks"], 1234)
        jen = mark["sims"]["Jennifer"]
        self.assertEqual(jen["pos"], [1.0, 0.0, 2.0])
        self.assertEqual(jen["hands"]["right"], {"type": "BookGeneral", "id": "101"})
        self.assertIsNone(jen["hands"]["left"])
        self.assertEqual(jen["inventory"], [{"type": "Phone", "id": "7"}])
        self.assertEqual(jen["funds"], 20000)
        self.assertEqual(jen["act"], "Book+ReadBook")
        self.assertIsNone(mark["sims"]["Pablo"]["act"])
        self.assertEqual(mark["sims"]["Pablo"]["outfit"], "Formalwear:1")
        self.assertEqual(mark["pairs"]["Jennifer+Pablo"], {"liking": 35.0, "ltr": "Friend"})
        self.assertEqual(mark["objects"]["202"], {"type": "ChairDiningWood", "pos": [3.0, 0.0, 4.5], "parent": "-", "in_inventory": False})
        self.assertIsNone(mark["objects"]["303"])

    def test_probe_errors_and_missing_sims_are_kept(self) -> None:
        mark = persist_cycle.parse_mark({("-", "error"): ["ok=false;err=NullReferenceException:x"], ("Pablo", "missing"): ["1"]})
        self.assertEqual(mark["errors"], ["ok=false;err=NullReferenceException:x"])
        self.assertEqual(mark["missing"], ["Pablo"])

    def test_the_probe_names_its_sims_and_objects(self) -> None:
        text = persist_cycle.probe_text(["Jennifer", "Pablo"], ["202", "303"])
        self.assertIn('Names = "Jennifer,Pablo"', text)
        self.assertIn('Objects = "202,303"', text)
        self.assertNotIn("__NAMES__", text)
        self.assertNotIn("__OBJECTS__", text)

    def test_the_setup_probe_takes_each_list_and_an_empty_one(self) -> None:
        text = persist_cycle.setup_text(["Pablo=Formalwear"], ["Jennifer+Pablo=15", "Jennifer+Pablo=-3"], [])
        self.assertIn('Outfits = "Pablo=Formalwear"', text)
        self.assertIn('Likings = "Jennifer+Pablo=15;Jennifer+Pablo=-3"', text)
        self.assertIn('Moves = ""', text)


class SaveState(unittest.TestCase):
    def test_the_probe_takes_reset_and_speed(self) -> None:
        text = persist_cycle.save_state_text(True, "Normal")
        self.assertIn("private const bool Reset = true;", text)
        self.assertIn('private const string Speed = "Normal";', text)
        self.assertIn("private const bool Reset = false;", persist_cycle.save_state_text(False, "Normal"))

    def test_a_thrown_save_leaves_three_things_set(self) -> None:
        clean = {"subState": "LiveModeState", "canSave": "True", "saveInProgress": "False", "stateChangeDialogUp": "False",
                 "speed": "Normal", "speedLocked": "False", "modal": "none"}
        self.assertEqual(persist_cycle.wedged(clean), [])
        stuck = dict(clean, canSave="False", saveInProgress="True", stateChangeDialogUp="True", speed="Pause", speedLocked="True")
        self.assertEqual(persist_cycle.wedged(stuck), ["OptionsModel.mSaveInProgress set", "InWorldState.sStateChangeDialogUp set",
                                                       "game speed locked at Pause"])


class VisibleRows(unittest.TestCase):
    def test_only_visible_rows_count_each_with_its_sim(self) -> None:
        text = """== game
  = selected                   Pablo                                        | Jennifer
== Jennifer
  ! machines                   Idle:standing_idle                           | Idle:Standing
  ~ prop Book                  hidden=0                                     | hidden=1
  ! prop PlumbBob              (none)                                       | hidden=0 parent=this Sim
== Pablo
1 visible mismatch(es)  (! visible, ~ logic layer only, = each player's own)"""
        self.assertEqual(persist_cycle.visible_rows(text), ["Jennifer machines", "Jennifer prop PlumbBob"])


class Compare(unittest.TestCase):
    def setUp(self) -> None:
        self.before = persist_cycle.parse_mark({**BEFORE, ("202", "obj"): ["ChairDiningWood 3.00,0.00,4.50 parent=- inv=0"]})

    def after(self, change=None) -> dict:
        mark = copy.deepcopy(self.before)
        if change:
            change(mark)
        return mark

    def failed(self, after: dict) -> list[str]:
        return [check for check, passed, _ in persist_cycle.compare(self.before, after) if not passed]

    def test_the_same_state_passes(self) -> None:
        self.assertEqual(self.failed(self.after()), [])

    def test_an_outfit_funds_or_relationship_change_fails(self) -> None:
        def change(m: dict) -> None:
            m["sims"]["Pablo"]["outfit"] = "Everyday:0"
            m["sims"]["Jennifer"]["funds"] = 19000
            m["pairs"]["Jennifer+Pablo"] = {"liking": 10.0, "ltr": "Friend"}
        self.assertEqual(sorted(self.failed(self.after(change))), ["Jennifer funds", "Jennifer+Pablo relationship", "Pablo outfit"])

    def test_a_small_liking_drift_and_a_stood_up_sim_pass(self) -> None:
        def change(m: dict) -> None:
            m["pairs"]["Jennifer+Pablo"]["liking"] += 0.3
            m["sims"]["Jennifer"]["pos"] = [1.3, 0.0, 2.2]
        self.assertEqual(self.failed(self.after(change)), [])

    def test_a_sim_saved_mid_interaction_is_placed_by_the_load_and_an_idle_one_must_stay(self) -> None:
        def moved(m: dict) -> None:
            m["sims"]["Jennifer"]["pos"] = [2.0, 0.0, 2.9]  # 1.23 m: the load reset her ReadBook
            m["sims"]["Pablo"]["pos"] = [2.0, 0.0, 2.9]
        rows = {check: (passed, detail) for check, passed, detail in persist_cycle.compare(self.before, self.after(moved))}
        self.assertTrue(rows["Jennifer position"][0])
        self.assertIn("saved mid-interaction (Book+ReadBook)", rows["Jennifer position"][1])
        self.assertFalse(rows["Pablo position"][0])
        self.assertIn("saved idle: within 0.5 m", rows["Pablo position"][1])

    def test_a_carry_that_ends_in_the_inventory_passes(self) -> None:
        def change(m: dict) -> None:
            m["sims"]["Jennifer"]["hands"]["right"] = None
            m["sims"]["Jennifer"]["inventory"].append({"type": "BookGeneral", "id": "101"})
        self.assertEqual(self.failed(self.after(change)), [])

    def test_a_carry_put_down_in_the_world_passes_and_one_gone_fails(self) -> None:
        def down(m: dict) -> None:
            m["sims"]["Jennifer"]["hands"]["right"] = None
            m["objects"]["101"] = {"type": "BookGeneral", "pos": [1.0, 0.0, 2.5], "parent": "-", "in_inventory": False}
        self.assertEqual(self.failed(self.after(down)), [])

        def gone(m: dict) -> None:
            m["sims"]["Jennifer"]["hands"]["right"] = None
        self.assertEqual(self.failed(self.after(gone)), ["BookGeneral#101 kept"])

    def test_a_moved_or_gone_object_and_a_missing_sim_fail(self) -> None:
        def change(m: dict) -> None:
            m["objects"]["202"]["pos"] = [3.5, 0.0, 4.5]
            del m["sims"]["Pablo"]
        self.assertEqual(sorted(self.failed(self.after(change))), ["ChairDiningWood#202 position", "Pablo present"])
        self.assertEqual(self.failed(self.after(lambda m: m["objects"].update({"202": None}))), ["ChairDiningWood#202 position"])

    def test_a_probe_error_fails(self) -> None:
        self.assertEqual(self.failed(self.after(lambda m: m["errors"].append("ok=false"))), ["probe"])


class BootSave(unittest.TestCase):
    def test_only_the_server_boot_save_entries_count(self) -> None:
        table = {"server-Saves/Option4BookChain.sims3/a.nhd": "1", "server-Saves/Option4BookChain.sims3.backup/a.nhd": "2",
                 "client-Saves/Option4BookChain.sims3/a.nhd": "3", "server-Saves/Sunset Valley.sims3/a.nhd": "4"}
        self.assertEqual(persist_cycle.boot_save_files(table), {"a.nhd": "1"})


class Cycle(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = Path(tempfile.mkdtemp(prefix="persist-test-"))
        store = mock.patch.object(persist_cycle, "STORE", self.tmp)
        store.start()
        self.addCleanup(store.stop)

    def test_no_claim_blocks_before_anything_runs(self) -> None:
        with mock.patch.object(persist_cycle, "claim_check", return_value=(False, "no stack claim")), \
                mock.patch.object(persist_cycle, "run", side_effect=AssertionError("a step ran")), \
                mock.patch.object(persist_cycle, "take_mark", side_effect=AssertionError("a mark ran")):
            with contextlib.redirect_stdout(io.StringIO()):
                code = persist_cycle.cycle("t", False, [], [])
        self.assertEqual(code, 2)
        row = json.loads((self.tmp / "persist.jsonl").read_text(encoding="utf-8").splitlines()[-1])
        self.assertEqual(row["checks"], {"claim": "BLOCKED"})
        self.assertEqual(row["stopped_at"], "claim")

    def test_a_refused_save_stops_before_the_games_go_down(self) -> None:
        before = persist_cycle.parse_mark(BEFORE)
        with mock.patch.object(persist_cycle, "claim_check", return_value=(True, "mine")), \
                mock.patch.object(persist_cycle, "take_mark", return_value=before), \
                mock.patch.object(persist_cycle, "run", return_value=(1, "save_game REJECTED: Save checkpoint blocked")) as run:
            with contextlib.redirect_stdout(io.StringIO()):
                code = persist_cycle.cycle("t", False, [], [])
        self.assertEqual(code, 1)
        self.assertEqual(run.call_count, 1)  # simctl save only: no backup, down or boot
        row = json.loads((self.tmp / "persist.jsonl").read_text(encoding="utf-8").splitlines()[-1])
        self.assertEqual(row["stopped_at"], "save")


if __name__ == "__main__":
    unittest.main()
