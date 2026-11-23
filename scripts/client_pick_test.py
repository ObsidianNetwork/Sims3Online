"""python -m unittest scripts.client_pick_test"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import client_pick as cp  # noqa: E402


class Source(unittest.TestCase):
    def test_a_sim_target(self) -> None:
        s = cp.source("sim:Jennifer", "Sim+Chat+Definition", "Chat")
        self.assertIn('TargetSim = "Jennifer";', s)
        self.assertIn('TargetTypeFragment = "";', s)
        self.assertIn('NameFragment = "Chat";', s)
        self.assertIn("ListOnly = false;", s)

    def test_list_marks_a_failed_entry_greyed_or_hidden(self) -> None:
        # A failed test with a tooltip callback is greyed, one without is hidden (docs/sim143-menu-honesty-design.md).
        s = cp.source("Chair", "", list_only=True)
        self.assertIn('"|grey"', s)
        self.assertIn('"|hidden"', s)

    def test_an_object_target_ranked_and_listed(self) -> None:
        s = cp.source("Chair", "", rank=1, list_only=True)
        self.assertIn('TargetSim = "";', s)
        self.assertIn('TargetTypeFragment = "Chair";', s)
        self.assertIn("Rank = 1;", s)
        self.assertIn("ListOnly = true;", s)
        self.assertNotRegex(s, r"__[A-Z_]+__")  # every placeholder filled

    def test_an_object_by_id(self) -> None:
        s = cp.source("id:11539349195487923152", "Sit")
        self.assertIn("TargetObjectId = 11539349195487923152uL;", s)
        self.assertIn('TargetTypeFragment = "";', s)
        self.assertIn("TargetObjectId = 0uL;", cp.source("Bed", "Relax"))

    def test_an_inventory_object(self) -> None:
        """inv: the active Sim's own object (the phone), found in its inventory, so a pick works on any lot."""
        s = cp.source("inv:PhoneSmart", "BrowseTheWeb")
        self.assertIn('TargetInventoryFragment = "PhoneSmart";', s)
        self.assertIn('TargetTypeFragment = "";', s)
        self.assertIn('TargetInventoryFragment = "";', cp.source("Bed", "Relax"))
        self.assertNotRegex(s, r"__[A-Z_]+__")

    def test_an_object_nearest_another_sim(self) -> None:
        s = cp.source("ChairDining", "Sit", near="Jennifer")
        self.assertIn('NearSim = "Jennifer";', s)
        self.assertIn('TargetTypeFragment = "ChairDining";', s)
        self.assertIn('NearSim = "";', cp.source("Bed", "Relax"))
        self.assertNotRegex(s, r"__[A-Z_]+__")

    def test_near_ranks_by_object_id(self) -> None:
        """--near: the free objects within --within metres, ranked by object id, so two players and two resets
        resolve the same object; without it the ranking stays by distance to the active Sim."""
        s = cp.source("ChairDining", "Sit", near="Jennifer", rank=1)
        self.assertIn("NearWithin = 5.0f;", s)
        self.assertIn("Rank = 1;", s)
        self.assertIn("NearSim.Length > 0 ? NearWithin : 15f", s)
        self.assertIn("a.ObjectId.Value.CompareTo(b.ObjectId.Value)", s)
        self.assertIn("NearWithin = 2.5f;", cp.source("ChairDining", "Sit", near="Jennifer", within=2.5))
        plain = cp.source("ChairDining", "Sit")
        self.assertIn("(a.Position - from).LengthSqr().CompareTo((b.Position - from).LengthSqr())", plain)
        self.assertNotRegex(plain, r"__[A-Z_]+__")

    def test_a_social_by_its_action_key(self) -> None:
        s = cp.source("sim:Jennifer", "social:Friendly Hug")
        self.assertIn('TypeFragment = "social:Friendly Hug";', s)
        self.assertIn("SocialInteractionA.Definition(", s)

    def test_the_hud_queue_and_a_cancel(self) -> None:
        self.assertIn("HudOnly = false;", cp.source("Bed", "Relax"))
        self.assertIn("CancelIndex = -1;", cp.source("Bed", "Relax"))
        s = cp.source("", "", hud=True, cancel=2)
        self.assertIn("HudOnly = true;", s)
        self.assertIn("CancelIndex = 2;", s)
        self.assertNotRegex(s, r"__[A-Z_]+__")

    def test_quotes_are_escaped(self) -> None:
        self.assertIn(r'NameFragment = "Say \"hi\"";', cp.source("Bed", "Relax", 'Say "hi"'))


class Reply(unittest.TestCase):
    OUT = """probe type=SimsOnline.Scratch.ClientPickProbe
  blobId=4f;state=complete
-- DIAG_RUN --
accepted=True
  ok         = true
  actor      = Jennifer Martinez
  target     = BedDoubleBrass:77
  list       = A = Relax | B = Sleep
"""

    def test_the_report_after_diag_run(self) -> None:
        self.assertEqual(cp.reply_fields(self.OUT),
                         "ok=true; actor=Jennifer Martinez; target=BedDoubleBrass:77; list=A = Relax | B = Sleep")

    def test_an_error_field(self) -> None:
        self.assertIn("err=target not found", cp.reply_fields("-- DIAG_RUN --" + chr(10) + "  err        = target not found" + chr(10)))


if __name__ == "__main__":
    unittest.main()
