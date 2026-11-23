"""python -m unittest scripts.inventory_diff_test"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import inventory_diff  # noqa: E402

SERVER = "ok=true;sims=2;owners=3\ninv=Sim:18|1|501\ninv=BookshelfCountryMed2x1:7787|0|-\ninv=FridgeCheap:900|2|11,12"
CLIENT = "ok=true;sims=2;owners=3\ninv=Sim:18|1|501\ninv=BookshelfCountryMed2x1:7787|1|640\ninv=Dresser:950|0|-"


class InventoryDiff(unittest.TestCase):
    def test_parse(self):
        sets = inventory_diff.parse(SERVER)
        self.assertEqual(sets["7787"], ("BookshelfCountryMed2x1", []))
        self.assertEqual(sets["900"], ("FridgeCheap", ["11", "12"]))
        self.assertEqual(len(sets), 3)

    def test_diff_and_report(self):
        rows = inventory_diff.diff(inventory_diff.parse(SERVER), inventory_diff.parse(CLIENT))
        by_owner = {r["owner"]: r for r in rows}
        self.assertTrue(by_owner["18"]["agree"])
        self.assertEqual(by_owner["7787"]["client_only"], ["640"])
        self.assertEqual(by_owner["900"]["missing_owner"], "client")
        self.assertEqual(by_owner["950"]["missing_owner"], "server")
        lines = inventory_diff.report(rows, False)
        self.assertEqual(lines[0], "4 owners, 3 differ")
        self.assertIn("  BookshelfCountryMed2x1:7787: server only -; client only 640", lines)

    def test_probe_source_fills_every_placeholder(self):
        source = (inventory_diff.TEMPLATE.replace("__SIMS__", "Jennifer").replace("__RADIUS__", "25.0")
                  .replace("__WATCHED__", "true").replace("__OWNERS__", "7787,18"))
        self.assertNotIn("__", source.replace("__RADIUS__", ""))
        self.assertIn('Owners = "7787,18"', source)
        self.assertIn("Watched = true", source)


if __name__ == "__main__":
    unittest.main()
