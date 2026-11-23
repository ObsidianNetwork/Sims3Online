"""Offline tests for ts3_view's camera follow: python -m unittest scripts.ts3_view_test

The probe's text per mode, the reply parser over bridgectl --json output, the follow decision, and the per-side runner
with bridgectl stubbed out (no game, no bridge).
"""
from __future__ import annotations

import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import ts3_view  # noqa: E402


def bridge_out(run: str, accepted: bool = True) -> str:
    """What `bridgectl --json probe` prints for a probe whose Run() returned `run`."""
    fields = dict(piece.split("=", 1) for piece in run.split(";"))
    report = {"accepted": accepted, "probe": {"envelope": {"loaded": "x"}, "fields": fields, "detail": [], "error": False,
                                             "run": None}}
    return "probe type=SimsOnline.Scratch.CameraFollowSimProbe\n-- DIAG_RUN --\n" + json.dumps(report) + "\n"


class FollowSourceTest(unittest.TestCase):
    def test_modes_fill_the_template(self):
        lock = ts3_view.follow_source("lock", "Pablo")
        self.assertIn('string mode = "lock";', lock)
        self.assertIn("Sims3.Gameplay.Core.Camera.LockToSim(found);", lock)
        self.assertIn('"Pablo", StringComparison.OrdinalIgnoreCase', lock)
        self.assertIn("CameraController.GetObjectFollowId()", lock)
        check = ts3_view.follow_source("check", "Pablo")
        self.assertIn('string mode = "check";', check)
        unlock = ts3_view.follow_source("unlock", "Pablo")
        self.assertIn("Sims3.Gameplay.Core.Camera.UnlockFromSim();", unlock)
        for source in (lock, check, unlock):
            self.assertNotIn("__", source)
            self.assertIn("public class CameraFollowSimProbe", source)

    def test_unlock_needs_no_name_so_it_compiles_once(self):
        self.assertEqual(ts3_view.follow_source("unlock", "Pablo"), ts3_view.follow_source("unlock"))

    def test_lock_is_skipped_while_away(self):
        source = ts3_view.follow_source("lock", "Pablo")
        self.assertIn('if (mode == "lock" && !away)', source)
        self.assertIn("found.RabbitHoleCurrent != null", source)
        self.assertIn("!found.InWorld", source)

    def test_unknown_mode(self):
        with self.assertRaises(ValueError):
            ts3_view.follow_source("orbit", "Pablo")


class ParseReplyTest(unittest.TestCase):
    def test_fields(self):
        out = bridge_out("ok=true;mode=check;sim=Pablo_Goth;simId=42;followId=42;away=0")
        self.assertEqual(ts3_view.parse_reply(out), {"ok": "true", "mode": "check", "sim": "Pablo_Goth", "simId": "42",
                                                     "followId": "42", "away": "0"})

    def test_failures(self):
        self.assertEqual(ts3_view.parse_reply(""), {"err": "no reply"})
        self.assertEqual(ts3_view.parse_reply("ERROR: not connected\n"), {"err": "no reply"})
        self.assertEqual(ts3_view.parse_reply(json.dumps({"ok": False, "error": "timeout"})), {"err": "timeout"})
        crashed = json.dumps({"accepted": True, "probe": {"envelope": {}, "fields": {}, "detail": ["!NullReference"],
                                                          "error": True, "run": "!NullReference"}})
        self.assertEqual(ts3_view.parse_reply(crashed), {"err": "!NullReference"})
        self.assertEqual(ts3_view.parse_reply(json.dumps({"accepted": True, "probe": {"raw": "freeform"}})), {"err": "freeform"})
        self.assertIn("err", ts3_view.parse_reply(json.dumps({"accepted": False})))
        self.assertIn("err", ts3_view.parse_reply("{not json"))

    def test_probe_miss_keeps_its_err(self):
        fields = ts3_view.parse_reply(bridge_out("ok=false;mode=check;err=no Sim named Pablo;followId=0"))
        self.assertEqual(fields["err"], "no Sim named Pablo")

    def test_round_trip_through_bridgectl_render(self):
        """The parser against bridgectl's own --json printing of a DIAG_RUN reply (its LoadAndRun wire shape), so a
        change there shows here instead of every live check silently reading error."""
        import contextlib
        import io

        import bridgectl

        def rendered(run: str) -> str:
            report = f"loaded=x;asm=CameraFollowSimProbe_ab;type=SimsOnline.Scratch.CameraFollowSimProbe;run={run}"
            buf = io.StringIO()
            with contextlib.redirect_stdout(buf):
                bridgectl.render({"ok": True, "accepted": True, "report": report}, as_json=True)
            return "probe type=SimsOnline.Scratch.CameraFollowSimProbe\n-- DIAG_RUN --\n" + buf.getvalue()

        fields = ts3_view.parse_reply(rendered("ok=true;mode=check;sim=Pablo_Goth;simId=42;followId=42;away=0"))
        self.assertEqual(ts3_view.decide(fields), "held")
        self.assertEqual(fields["sim"], "Pablo_Goth")
        self.assertEqual(ts3_view.parse_reply(rendered("!NullReferenceException at Run")), {"err": "!NullReferenceException at Run"})
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            bridgectl.render({"ok": False, "error": "not connected"}, as_json=True)
        self.assertEqual(ts3_view.parse_reply(buf.getvalue()), {"err": "not connected"})


class GameRuntimeTest(unittest.TestCase):
    def test_the_probe_compiles_against_the_games_own_runtime(self):
        """Every mode's source holds every call (LockToSim, UnlockFromSim, GetObjectFollowId), so one compile with
        /nostdlib against the game's assemblies (check_mod_runtime) covers all three; Windows with the assemblies only."""
        import check_mod_runtime
        try:
            check_mod_runtime.assemblies()
            check_mod_runtime.csc()
        except SystemExit as ex:
            self.skipTest(str(ex))
        with tempfile.TemporaryDirectory() as tmp:
            source = Path(tmp) / "CameraFollowSimProbe.cs"
            source.write_text(ts3_view.follow_source("lock", "Pablo"), encoding="utf-8")
            self.assertEqual(check_mod_runtime.check("ts3_view_test_follow", [source]), [])


class DecideTest(unittest.TestCase):
    def state(self, **fields) -> str:
        return ts3_view.decide({"ok": "true", "simId": "42", "followId": "42", "away": "0", **fields})

    def test_states(self):
        self.assertEqual(self.state(), "held")
        self.assertEqual(self.state(followId="0"), "relock")  # nothing followed: the follow was dropped
        self.assertEqual(self.state(followId="77"), "relock")  # another object: another portrait, a respawned copy
        self.assertEqual(self.state(followId="0", away="1"), "away")  # rabbit hole: no relock churn
        self.assertEqual(self.state(simId="0", followId="0"), "relock")
        self.assertEqual(ts3_view.decide({"ok": "false", "err": "no Sim named Pablo"}), "error")
        self.assertEqual(ts3_view.decide({"err": "no reply"}), "error")


class CameraRunnerTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        probe = Path(self.tmp.name) / "act" / "CameraFollowSimProbe.cs"
        patch_probe = mock.patch.object(ts3_view, "FOLLOW_PROBE", probe)
        patch_root = mock.patch.object(ts3_view, "ROOT", Path(self.tmp.name))
        patch_probe.start()
        patch_root.start()
        self.addCleanup(patch_probe.stop)
        self.addCleanup(patch_root.stop)
        self.addCleanup(self.tmp.cleanup)

    def test_runs_named_sides_through_bridgectl_json(self):
        calls = []

        def run(argv, **kwargs):
            calls.append(argv)
            return subprocess.CompletedProcess(argv, 0, bridge_out("ok=true;mode=lock;sim=Pablo_Goth;simId=42;followId=42;away=0"), "")

        with mock.patch.object(ts3_view.subprocess, "run", side_effect=run):
            results = ts3_view.follow("Pablo", ["client"], timeout=5)
        self.assertEqual(list(results), ["client"])
        self.assertEqual(ts3_view.decide(results["client"]), "held")
        self.assertEqual(len(calls), 1)
        argv = calls[0]
        self.assertEqual(argv[argv.index("--endpoint") + 1], ts3_view.ENDPOINTS["client"])
        self.assertIn("--json", argv)
        self.assertTrue(Path(self.tmp.name, argv[-1]).read_text(encoding="utf-8").count("Camera.LockToSim(found)") == 1)

    def test_timeout_and_compile_failure_are_errors(self):
        def run(argv, **kwargs):
            if ts3_view.ENDPOINTS["server"] in argv:
                raise subprocess.TimeoutExpired(argv, 5)
            return subprocess.CompletedProcess(argv, 1, "", "probe compile failed (exit 1)\n")

        with mock.patch.object(ts3_view.subprocess, "run", side_effect=run):
            results = ts3_view.follow_state("Pablo", timeout=5)
        self.assertEqual(results["server"], {"err": "no reply in 5s"})
        self.assertEqual(results["client"], {"err": "probe compile failed (exit 1)"})
        self.assertEqual([ts3_view.decide(f) for f in results.values()], ["error", "error"])

    def test_describe(self):
        lines = ts3_view.describe_follow({
            "server": {"ok": "true", "mode": "check", "sim": "Pablo_Goth", "simId": "42", "followId": "42", "away": "0"},
            "client": {"ok": "true", "mode": "unlock", "was": "42", "followId": "0"}})
        self.assertEqual(lines, ["server: held (Pablo_Goth id 42, camera follows 42)",
                                 "client: released (was following 42, now 0)"])


class LabelWindowsTest(unittest.TestCase):
    WINDOWS = [(11, 101, "The Sims 3"), (12, 102, "[#] The Sims 3 [#]"), (13, 103, "[#] The Sims 3 [#]")]
    GAMES = {"server": {"pid": 101}, "client": {"pid": 102}, "client2": {"pid": 103}}

    def test_each_client_by_its_game_pid(self):
        self.assertEqual(ts3_view.label_windows(self.WINDOWS, self.GAMES, "client"), {"server": 11, "client": 12})
        self.assertEqual(ts3_view.label_windows(self.WINDOWS, self.GAMES, "client2"), {"server": 11, "client": 13})

    def test_without_pids_the_title_for_client_1_only(self):
        self.assertEqual(ts3_view.label_windows(self.WINDOWS[:2], {}, "client"), {"server": 11, "client": 12})
        self.assertEqual(ts3_view.label_windows(self.WINDOWS, {}, "client2"), {"server": 11})

    def test_a_known_pid_with_no_window_is_missing(self):
        self.assertEqual(ts3_view.label_windows(self.WINDOWS[:1], self.GAMES, "client"), {"server": 11})


class FocusSource(unittest.TestCase):
    def test_a_sim_or_a_spot_on_its_level(self) -> None:
        on_sim = ts3_view.focus_source("Jennifer")
        self.assertIn("Vector3 at = found.Position;", on_sim)
        self.assertIn('"__NAME__"'.replace("__NAME__", "Jennifer"), on_sim)
        self.assertIn("at.x - 4.0f, at.y + 6.0f, at.z - 4.0f", on_sim)
        on_spot = ts3_view.focus_source("Jennifer", 2.0, (1139.3, 984.8))
        self.assertIn("Vector3 at = new Vector3(1139.30f, found.Position.y, 984.80f);", on_spot)
        self.assertIn('"ok=true;focused=" + "1139.30,984.80"', on_spot)
        self.assertIn("at.x - 8.0f, at.y + 12.0f, at.z - 8.0f", on_spot)
        self.assertNotIn("__", on_spot.replace("__NAME__", ""))  # every placeholder filled


class CameraRead(unittest.TestCase):
    def test_the_games_a_camera_command_acts_on(self) -> None:
        with mock.patch.object(ts3_view, "endpoints", return_value={"server": "s", "client": "c"}):
            self.assertEqual(ts3_view.games(None), {"server": "s", "client": "c"})
        chosen = ts3_view.games("server,1,2")
        self.assertEqual(list(chosen), ["server", "client 1", "client 2"])
        self.assertEqual(chosen["server"], ts3_view.ENDPOINTS["server"])

    def test_the_read_probe_and_its_line(self) -> None:
        source = ts3_view.camera_source(["Jennifer", "Pablo"])
        self.assertIn('new string[] { "Jennifer", "Pablo" }', source)
        self.assertIn("CameraController.GetLODInterestPosition()", source)
        self.assertNotIn("__", source)
        lines = ts3_view.describe_camera({
            "server": {"ok": "true", "position": "1123.90,51.90,984.45", "target": "1127.90,45.90,988.45",
                       "lod": "1127.90,45.90,988.45", "zoom": "8.25", "sims": "Jennifer@0.6/8.6 Pablo@0.6/8.1"},
            "client 2": {"err": "no reply"}})
        self.assertEqual(lines, [
            "server: camera at 1123.90,51.90,984.45 looking at 1127.90,45.90,988.45, LOD point 1127.90,45.90,988.45, "
            "zoom 8.25; Jennifer 0.6 m from the LOD point, 8.6 m from the camera; Pablo 0.6 m from the LOD point, 8.1 m "
            "from the camera",
            "client 2: error (no reply)"])


if __name__ == "__main__":
    unittest.main()
