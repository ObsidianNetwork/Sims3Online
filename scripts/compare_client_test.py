"""python -m unittest scripts.compare_client_test"""

from __future__ import annotations

import os
import sys
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import compare_client as cc  # noqa: E402


class CompareClient(unittest.TestCase):
    def test_resolve(self) -> None:
        with mock.patch.dict(os.environ, {}, clear=True):
            self.assertEqual(cc.resolve(""), "127.0.0.1:34030")
            self.assertEqual(cc.resolve("1"), "127.0.0.1:34030")
            self.assertEqual(cc.resolve("2"), "127.0.0.1:34031")
            self.assertEqual(cc.resolve("10.0.0.5:34040"), "10.0.0.5:34040")
            self.assertEqual(cc.resolve("3"), "127.0.0.1:34032")  # player 3
            with self.assertRaises(ValueError):
                cc.resolve("4")
        with mock.patch.dict(os.environ, {"SIMSONLINE_CLIENT2_BRIDGECTL": "127.0.0.1:34041"}, clear=True):
            self.assertEqual(cc.resolve("2"), "127.0.0.1:34041")  # stackctl's own setting for player 2
        with mock.patch.dict(os.environ, {"SIMSONLINE_CLIENT3_BRIDGECTL": "127.0.0.1:44032"}, clear=True):
            self.assertEqual(cc.resolve("3"), "127.0.0.1:44032")
            self.assertEqual(cc.clients(), ["127.0.0.1:34030", "127.0.0.1:34031", "127.0.0.1:44032"])
            self.assertEqual(cc.player_of("127.0.0.1:44032"), "3")

    def test_use_reaches_the_tools_a_run_starts(self) -> None:
        with mock.patch.dict(os.environ, {}, clear=True):
            self.assertEqual(cc.endpoint(), "127.0.0.1:34030")
            self.assertEqual(cc.use("2"), "127.0.0.1:34031")
            self.assertEqual(os.environ[cc.ENV], "127.0.0.1:34031")  # a child process reads it at import
            self.assertEqual(cc.endpoint(), "127.0.0.1:34031")

    def test_label_and_name(self) -> None:
        with mock.patch.dict(os.environ, {}, clear=True):
            self.assertEqual((cc.label("127.0.0.1:34030"), cc.name("127.0.0.1:34030")), ("", "client 1"))
            self.assertEqual((cc.label("127.0.0.1:34031"), cc.name("127.0.0.1:34031")), ("-c2", "client 2"))
            self.assertEqual((cc.label("127.0.0.1:34032"), cc.name("127.0.0.1:34032")), ("-c3", "client 3"))
            self.assertEqual(cc.label("10.0.0.5:34040"), "-c34040")
            self.assertIsNone(cc.player_of("10.0.0.5:34040"))


if __name__ == "__main__":
    unittest.main()
