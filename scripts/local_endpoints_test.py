"""Offline regression for running the starter scenarios on alternate loopback ports."""

import os
import subprocess
import sys
import unittest
from pathlib import Path


class LocalEndpointTests(unittest.TestCase):
    def test_scenario_tools_follow_the_stack_endpoints(self):
        # The same environment is set before launching each of these tools.
        # Run in a fresh interpreter so imported defaults reflect that environment.
        env = dict(os.environ)
        env.update(
            PYTHONPATH="",
            SIMSONLINE_PUBLIC_ENDPOINT="127.0.0.1:38119",
            SIMSONLINE_SERVER_BRIDGECTL="127.0.0.1:38129",
            SIMSONLINE_CLIENT_BRIDGECTL="127.0.0.1:38130",
            SIMSONLINE_CLIENT2_BRIDGECTL="127.0.0.1:38131",
            SIMSONLINE_COMPARE_CLIENT="",
        )
        result = subprocess.run(
            [sys.executable, "-c", """
import sys
from unittest import mock

from scripts import simctl

sys.path.insert(0, "scripts")
import bridgectl
import compare_client
import household_read
import motion_watch
import quick_loop
import replay_check
import scenarios
import sim_act
import sim_compare
import stackctl
import ts3_view

public, host, client = "127.0.0.1:38119", "127.0.0.1:38129", "127.0.0.1:38130"
assert (stackctl.PUBLIC_ENDPOINT, stackctl.SERVER_BRIDGECTL, stackctl.CLIENT_BRIDGECTL) == (public, host, client)
with mock.patch.object(scenarios, "game_down", return_value=None) as check, mock.patch.object(scenarios, "ledger_record"):
    assert scenarios.health() is None
    assert check.call_args_list == [mock.call(host), mock.call(client)]
assert scenarios.check_endpoint("server", {}, []) == host
assert bridgectl.DEFAULT_ENDPOINT == sim_act.SERVER == household_read.SERVER == replay_check.SERVER == host
assert (quick_loop.SERVER, quick_loop.CLIENT) == (host, client)
assert motion_watch.ENDPOINTS == sim_compare.ENDPOINTS == ts3_view.endpoints() == {"server": host, "client": client}
assert compare_client.resolve("1") == client and compare_client.player_of(client) == "1"
assert compare_client.resolve("2") == "127.0.0.1:38131"
assert compare_client.resolve("127.0.0.1:38999") == "127.0.0.1:38999"

with mock.patch.object(simctl, "Conn") as connect, mock.patch.object(simctl, "cmd_speed"):
    for options, target in (([], public), (["--endpoint", "127.0.0.1:38999"], "127.0.0.1:38999")):
        with mock.patch.object(sys, "argv", ["simctl", *options, "speed", "normal"]):
            simctl.main()
        assert connect.call_args.args[0] == target
"""],
            cwd=Path(__file__).resolve().parent.parent,
            env=env,
            stdin=subprocess.DEVNULL,
            capture_output=True,
            encoding="utf-8",
            errors="replace",
            timeout=20,
        )
        self.assertEqual(result.returncode, 0, result.stderr)


if __name__ == "__main__":
    unittest.main()
