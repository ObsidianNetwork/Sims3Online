"""The runbook only prints commands; artifact checks fail closed."""
import contextlib
import io
import json
from pathlib import Path
import re
import tempfile
import unittest
from unittest.mock import patch

from scripts import card57_live_gates as gates
from scripts.card57_wire_diff import STEPS


class LiveGatesTests(unittest.TestCase):
    def fixture(self, folder, *, mismatches=0, drops=0, probe="PASS"):
        root = Path(folder)
        rows = [{"step": step, "status": "mismatch" if mismatches and step == "join" else "match",
                 "diff": []} for step in (*STEPS, "broadcasts")]
        rows.append({"summary": True, "steps": len(rows), "mismatches": mismatches, "silent_drops": drops})
        (root / "wire_diff.jsonl").write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")
        (root / "rejection_relay.txt").write_text(
            f"unjoined=PASS reply={{}}\nsilent_drops=0\nverdict={probe}\n", encoding="utf-8")
        return root

    def check(self, root):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            code = gates.main(["check", str(root)])
        return code, output.getvalue()

    def test_valid_artifacts_pass(self):
        with tempfile.TemporaryDirectory() as folder:
            code, output = self.check(self.fixture(folder))
        self.assertEqual(code, 0, output)
        self.assertTrue(output.startswith("PASS"))

    def test_missing_or_failed_run_wide_broadcast_check_cannot_pass(self):
        for absent in (False, True):
            with self.subTest(absent=absent), tempfile.TemporaryDirectory() as folder:
                root = self.fixture(folder)
                path = root / "wire_diff.jsonl"
                rows = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]
                if absent:
                    del rows[-2]
                    rows[-1]["steps"] -= 1
                else:
                    rows[-2]["status"] = "mismatch"
                path.write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")
                self.assertEqual(self.check(root)[0], 1)

    def test_mismatch_silence_and_probe_failure_each_fail(self):
        for options in ({"mismatches": 1}, {"drops": 1}, {"probe": "FAIL"}):
            with self.subTest(options=options), tempfile.TemporaryDirectory() as folder:
                code, output = self.check(self.fixture(folder, **options))
            self.assertEqual(code, 1, output)
            self.assertTrue(output.startswith("FAIL"))

    def test_missing_empty_malformed_and_forged_artifacts_fail(self):
        for text in ("", "{", "[]\n", "null\n",
                     '{"summary":true,"steps":0,"mismatches":0,"silent_drops":0}\n',
                     '{"step":"join","status":"mismatch"}\n'
                     '{"summary":true,"steps":1,"mismatches":0,"silent_drops":0}\n'):
            with self.subTest(text=text), tempfile.TemporaryDirectory() as folder:
                root = self.fixture(folder)
                (root / "wire_diff.jsonl").write_text(text, encoding="utf-8")
                self.assertEqual(self.check(root)[0], 1)
        with tempfile.TemporaryDirectory() as folder:
            self.assertEqual(self.check(Path(folder))[0], 1)

    def test_probe_needs_exact_zero_and_pass(self):
        for text in ("silent_drops=01\nverdict=PASS\n", "silent_drops=0\n",
                     "silent_drops=0\nsilent_drops=1\nverdict=PASS\n"):
            with self.subTest(text=text), tempfile.TemporaryDirectory() as folder:
                root = self.fixture(folder)
                (root / "rejection_relay.txt").write_text(text, encoding="utf-8")
                self.assertEqual(self.check(root)[0], 1)

    def test_plan_is_ordered_and_never_invokes_a_tool(self):
        output = io.StringIO()
        with contextlib.redirect_stdout(output), patch("subprocess.run") as run:
            code = gates.main(["plan", "--run-dir", ".omo/run/card57-test", "--base", "card57-base",
                               "--shell", "posix"])
        run.assert_not_called()
        self.assertEqual(code, 0)
        commands = [line for line in output.getvalue().splitlines() if line and not line.startswith("#")]
        self.assertTrue(any('stack_claim.py take "card57 wire differential"' in command for command in commands))
        restores = [i for i, command in enumerate(commands) if "saves.py restore card57-base" in command]
        self.assertEqual(len(restores), 2)
        json_record = next(i for i, command in enumerate(commands) if "record --wire json" in command)
        line_record = next(i for i, command in enumerate(commands) if "record --wire line" in command)
        self.assertLess(restores[0], json_record)
        self.assertLess(json_record, restores[1])
        self.assertLess(restores[1], line_record)
        self.assertTrue(any("boot-server --front-door" in command for command in commands))
        self.assertTrue(any("stop-controller" in command for command in commands))
        self.assertTrue(any("rejection_relay_probe.py --wire line" in command for command in commands))
        self.assertTrue(any("SIMSONLINE_RUST_SIDECAR_PORT=34039" in command for command in commands))
        self.assertTrue(any("python scripts/stack_claim.py status" in command for command in commands))

    def plan(self, *args):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            self.assertEqual(gates.main(["plan", *args]), 0)
        return output.getvalue()

    def test_baseline_creation_is_one_positional_name_before_both_restores(self):
        text = self.plan("--shell", "posix")
        backup = "python scripts/saves.py backup card57-cold-base"
        self.assertEqual(text.count(backup), 1)
        self.assertLess(text.index(backup), text.index("python scripts/saves.py restore"))
        self.assertEqual(text.count("python scripts/saves.py restore"), 2)

    def test_json_phase_requires_resident_sidecar_and_controller_hello_environment(self):
        text = self.plan("--phase", "json", "--shell", "powershell")
        self.assertIn("simlink_authority_daemon", text)
        self.assertIn("--resident --port 34039 --connections 32 --ticks 4000000", text)
        for name, value in (("RUST_SIDECAR", "1"), ("RUST_SIDECAR_PORT", "34039"),
                            ("SIMLINK_CLIENT_RING", "1"), ("WORLD_FROM_SERVER", "1")):
            self.assertIn(f"$env:SIMSONLINE_{name}='{value}'", text)
        self.assertIn("PING|1", text)
        self.assertLess(text.index("PING|1"), text.index("record --wire json"))
        self.assertNotIn("record --wire line", text)

    def test_powershell_cleanup_is_finally_and_resets_all_set_environment(self):
        text = self.plan("--shell", "powershell")
        cleanup = text[text.index("} finally {"):]
        names = set(re.findall(r"\$env:([A-Z_]+)\s*=", text))
        self.assertGreaterEqual(len(names), 5)
        for name in names:
            self.assertIn("Env:" + name, cleanup)
        self.assertLess(cleanup.index("Remove-Item"), cleanup.index("stackctl.py down"))
        self.assertLess(cleanup.index("stackctl.py down"), cleanup.index("stack_claim.py release"))
        self.assertLess(cleanup.index("stack_claim.py release"), cleanup.index("stack_claim.py status"))
        self.assertIn("QUIT|1", cleanup)
        self.assertIn("$PSNativeCommandUseErrorActionPreference=$true", text)

    def test_line_phase_checks_external_target_binary_and_prints_build_command(self):
        text = self.plan("--phase", "line", "--shell", "powershell")
        self.assertIn("Test-Path", text)
        self.assertIn("cargo build --manifest-path rust/simlink_headless_server/Cargo.toml", text)
        self.assertRegex(text, r"--target-dir '?E:/")
        self.assertIn("--front-door-bin", text)
        self.assertIn("card57-wire-scratch.sims3", text)
        self.assertNotIn("record --wire json", text)
        self.assertLess(text.index("Test-Path"), text.index("boot-server --front-door"))

    def test_front_door_down_and_dual_cleanup_flags(self):
        plain = "python scripts/stackctl.py down --force"
        front_door = plain + " --front-door"
        for shell in ("powershell", "posix"):
            for phase in ("both", "json", "line"):
                with self.subTest(shell=shell, phase=phase):
                    text = self.plan("--shell", shell, "--phase", phase)
                    if shell == "powershell":
                        body, cleanup = text.split("  } finally {", 1)
                    else:
                        cleanup = text[text.index("  cleanup() {"):text.index("  trap cleanup EXIT")]
                        body = text[text.index("  export "):]
                    downs = re.findall(r"python scripts/stackctl\.py down --force(?: --front-door)?", cleanup)
                    self.assertEqual(downs, [front_door, plain])
                    boot = "boot-server --front-door"
                    before, _, after = body.partition(boot)
                    self.assertTrue(all("--front-door" not in line for line in before.splitlines() if plain in line))
                    if phase != "json":
                        downs = [line for line in after.splitlines() if plain in line]
                        self.assertTrue(downs)
                        self.assertTrue(all(front_door in line for line in downs), downs)


if __name__ == "__main__":
    unittest.main()
