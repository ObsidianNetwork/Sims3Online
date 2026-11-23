import io
import subprocess
import unittest
from unittest import mock

import scripts.route_points as route_points
from scripts.route_points import source


class RoutePointsTest(unittest.TestCase):
    def test_rejects_invalid_native_query_coordinates(self):
        for points in ([], [[float("nan"), 0, 1]], [[0, float("inf"), 1]], [[0, 0, 1e7]], [[0, 1]], [[0, 0, 0]] * 65):
            with self.subTest(points=points), self.assertRaises(ValueError):
                source("Pablo", points)

    def test_name_is_a_string_literal_and_route_is_disposed(self):
        text = source('Name";throw new Exception(); //', [[1.25, 50, -2]])
        self.assertIn('string requested = "Name\\";throw new Exception(); //";', text)
        self.assertIn("new Vector3(1.25f,50f,-2f)", text)
        self.assertIn("finally { route.Dispose(); }", text)
        self.assertNotIn("DoRoute(", text)
        self.assertNotIn(".Plan(", text)

    def test_main_uses_server_bridgectl_default_and_fails_on_probe_error(self):
        fake = subprocess.CompletedProcess([], 0, "error=Sim not found\n", "")
        with mock.patch("subprocess.run", return_value=fake) as run, \
                mock.patch("contextlib.redirect_stdout", return_value=io.StringIO()):
            rc = route_points.main(["Jennifer", "--point", "1", "0", "2"])
        self.assertEqual(rc, 1)
        cmd = run.call_args.args[0]
        self.assertEqual(cmd[cmd.index("--endpoint") + 1], route_points.SERVER_BRIDGECTL)
        ok = subprocess.CompletedProcess([], 0, "actor=10|position=(1,0,2)|room=0\n", "")
        with mock.patch("subprocess.run", return_value=ok), \
                mock.patch("contextlib.redirect_stdout", return_value=io.StringIO()):
            self.assertEqual(route_points.main(["Jennifer", "--point", "1", "0", "2"]), 0)
