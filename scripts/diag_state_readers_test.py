"""The scripts that read DIAG_STATE find its fields by key (python -m unittest scripts.diag_state_readers_test).

REPORT has the mod's fields in HandleDiagState's order, in the world, ending with swallowed= (SwallowedErrors, the
errors the mod's empty catch blocks caught): a field added at the end must leave every reader working. The
controller's readers are tested in DedicatedServer.Tests (SwallowedErrors.CountForDiagState).
"""
from __future__ import annotations

import contextlib
import importlib.util
import io
import subprocess
import sys
import unittest
from pathlib import Path
from unittest import mock

SCRIPTS = Path(__file__).resolve().parent
sys.path.insert(0, str(SCRIPTS))
import bridgectl  # noqa: E402

REPORT = (
    "role=server;inWorld=True;tick=4521;autonomyWatch=(none);commandGate=(none);pendingOutbound=0;pumpTicks=812"
    ";probeLoads=0;probeReuses=0;dispatch=63;commandedSpeed=Normal;observedSpeed=Normal;clockDay=2;clockHours=9"
    ";clockMinutes=41;clockTicks=98765;clockSets=1/0;clockLastSetMin=0.5;hudClockDueMin=0.73"
    ";inventories=2/14/3/0/0/0/polls:40/reads:2.5/ms:0.012,0.3/findMs:0.004,0.1"
    ";uses=1/6/2/0/0/polls:40/ms:0.008,0.2/last:Chair:1,0;refusals=0/sent:0/cleared:0/skipped:0/retests:0/errors:0"
    ";swallowed=17"
)
REPLY = {"ok": True, "accepted": True, "report": REPORT}


def load(name: str, file: str):
    spec = importlib.util.spec_from_file_location(name, SCRIPTS / file)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class DiagStateReadersTests(unittest.TestCase):
    def test_bridge_health(self) -> None:
        bridge_health = load("bridge_health", "bridge-health.py")
        match = bridge_health.DIAG_RE.search(REPORT)
        self.assertEqual((match["in_world"], match["tick"], match["dispatch"]), ("True", "4521", "63"))
        self.assertEqual(bridge_health._role(REPORT), "server")

    def test_stackctl_status(self) -> None:
        import stackctl

        with mock.patch.object(bridgectl, "connect"), mock.patch.object(bridgectl, "roundtrip", return_value=REPLY):
            state = stackctl.probe_game_state("127.0.0.1:34029")
        self.assertEqual(state, {"in_world": "True", "loaded": True, "tick": 4521, "dispatch": 63})

    def test_tick_and_pump_readers(self) -> None:
        import pump_ms

        headless_probe = load("headless_probe", "headless-probe.py")
        with mock.patch.object(bridgectl, "roundtrip", return_value=REPLY):
            self.assertEqual(pump_ms.tick(object()), 4521)
            self.assertEqual(headless_probe.sample(object(), 5)[1:], (4521, 812))

    def test_key_value_readers(self) -> None:
        import transport_latency_sweep


        # transport_latency_sweep reads what bridgectl prints for DIAG_STATE.
        printed = io.StringIO()
        with contextlib.redirect_stdout(printed):
            bridgectl.render(REPLY)
        done = subprocess.CompletedProcess([], 0, stdout=printed.getvalue(), stderr="")
        with mock.patch.object(transport_latency_sweep.subprocess, "run", return_value=done):
            state = transport_latency_sweep.probe_state()["server"]
        self.assertEqual((state["tick"], state["pumpTicks"], state["inWorld"]), (4521, 812, "True"))


if __name__ == "__main__":
    unittest.main()
