"""python -m unittest scripts.pump_ms_test"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import pump_ms  # noqa: E402


class Interval(unittest.TestCase):
    def test_forms(self):
        self.assertEqual(pump_ms.interval("50"), 50)
        self.assertEqual(pump_ms.interval("p50"), 50)
        self.assertEqual(pump_ms.interval("pump-250"), 250)
        self.assertIsNone(pump_ms.interval("status"))

    def test_out_of_range_and_empty(self):
        for value in ("10", "300", "fast"):
            with self.assertRaises(ValueError):
                pump_ms.interval(value)


class Games(unittest.TestCase):
    def test_clients_and_only(self):
        self.assertEqual(list(pump_ms.games(1, None)), ["server", "client1"])
        self.assertEqual(list(pump_ms.games(2, None)), ["server", "client1", "client2"])
        self.assertEqual(pump_ms.games(2, "client2"), {"client2": "127.0.0.1:34031"})


if __name__ == "__main__":
    unittest.main()
