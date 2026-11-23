"""Review regressions for 942bfa68; no real profiles, live games or cargo."""
from __future__ import annotations

import contextlib
import hashlib
import io
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import fork_census
import quick_loop
import scenarios
import stackctl


class ReviewRegressions(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.server = self.root / "server"
        env = mock.patch.dict(os.environ, {
            "HOME": str(self.root), "USERPROFILE": str(self.root), "USER": "test-user", "USERNAME": "test-user",
            "SIMSONLINE_TS3_DOCUMENTS": str(self.server), "SIMSONLINE_REAL_PROFILE": str(self.server),
            "SIMSONLINE_ONEDRIVE_MOD": str(self.server / "Mods/Packages/mod.package"),
            "SIMSONLINE_BOX_PROFILE": str(self.root / "boxes/{box}"),
            "SIMSONLINE_MAIN_CHECKOUT": str(self.root), "SIMSONLINE_RUN_DIR": str(self.root / "run"),
            "SIMSONLINE_CLIENT_SAVES_DIR": "", "SIMSONLINE_NO_FORK_OK": "",
            "SIMSONLINE_WINE_USER": "test-user", "WINEPREFIX": "",
        })
        env.start()
        self.addCleanup(env.stop)
        home = mock.patch.object(Path, "home", return_value=self.root)
        home.start()
        self.addCleanup(home.stop)

    def install(self, *profiles):
        digest = hashlib.sha256(b"selected build").hexdigest()
        for profile in profiles:
            package = profile / "Mods/Packages" / fork_census.PACKAGE
            package.parent.mkdir(parents=True, exist_ok=True)
            package.write_bytes(b"selected build")
        receipt = self.root / ".omo/cache/fork/cold-test-001/deployment.json"
        receipt.parent.mkdir(parents=True)
        receipt.write_text(json.dumps({"status": "installed", "preparedSha256": digest,
                                      "deploymentPackage": str(self.server / "Mods/Packages" / fork_census.PACKAGE),
                                      "overridePackage": True}), encoding="utf-8")

    def test_stackctl_selftests_never_resolve_profiles_or_write_evidence(self):
        with mock.patch.object(fork_census, "resolved_profiles", side_effect=AssertionError("profile read by self-test")), \
                mock.patch.object(fork_census, "write_evidence", side_effect=AssertionError("real evidence write")), \
                contextlib.redirect_stdout(io.StringIO()) as out, contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(stackctl.run_script_tests(), (27, 0), out.getvalue())

    def test_absent_targeted_wine_prefix_is_classified_stock(self):
        prefix = self.root / ".sims3-client"
        with mock.patch.object(sys, "platform", "linux"), contextlib.redirect_stderr(io.StringIO()):
            result = stackctl.launch_fork_census(["client", "--client-ts3-wineprefix", str(prefix)])
            self.assertEqual(result["state"], "stock")
            row = next(p for p in result["packages"] if str(prefix) in p["path"])
            self.assertFalse(row["present"])
            self.assertTrue(row["required"])
            fork_census.guard(result, True)

    def test_absent_targeted_wine_prefix_is_a_fork_mismatch(self):
        server = self.root / ".sims3/drive_c/users/user" / fork_census.TAIL
        self.install(self.server, server)
        prefix = self.root / ".sims3-client2"
        with mock.patch.object(sys, "platform", "linux"):
            result = stackctl.launch_fork_census(["client", "--client-ts3-wineprefix", str(prefix)])
        self.assertEqual(result["state"], "mismatch")
        self.assertTrue(any(str(prefix) in p for p in result["mismatches"]))

    def test_unset_wineprefix_includes_default_server_prefix(self):
        profile = self.root / ".sims3/drive_c/users/user" / fork_census.TAIL
        package = profile / "Mods/Packages" / fork_census.PACKAGE
        package.parent.mkdir(parents=True)
        package.write_bytes(b"unregistered server fork")
        with mock.patch.object(sys, "platform", "linux"):
            result = fork_census.census(self.root)
        self.assertEqual(result["state"], "unknown")
        self.assertTrue(any(p["path"] == str(package.resolve()) and p["required"] for p in result["packages"]))

    def test_resolution_permission_error_is_classified_before_guard(self):
        with mock.patch.object(fork_census, "resolved_profiles", side_effect=PermissionError("profile denied")):
            result = stackctl.launch_fork_census(["server"])
        self.assertEqual(result["state"], "unreadable")
        self.assertIn("profile denied", result["errors"][0])

    def test_controller_census_uses_custom_box_without_default_boxes(self):
        custom = self.root / "boxes/CustomBox"
        self.install(self.server, custom)
        with mock.patch.object(sys, "platform", "win32"), \
                mock.patch.object(stackctl, "DEFAULT_SANDBOXIE_BOX", "Unused1"), \
                mock.patch.object(stackctl, "DEFAULT_SANDBOXIE_BOX2", "Unused2"), \
                mock.patch.object(stackctl, "DEFAULT_SANDBOXIE_BOX3", "Unprepared3"):
            result = stackctl.launch_fork_census(["client", "--client-ts3-sandboxie-box", "CustomBox"])
        self.assertEqual(result["state"], "fork")
        required = [p["path"] for p in result["packages"] if p["required"]]
        self.assertTrue(any("CustomBox" in p for p in required))
        self.assertFalse(any("Unused" in p or "Unprepared" in p for p in required))

    def test_server_and_unboxed_controller_do_not_require_unprepared_boxes(self):
        self.install(self.server)
        with mock.patch.object(sys, "platform", "win32"), \
                mock.patch.dict(os.environ, {"SIMSONLINE_SANDBOXIE_BOX3": "Unprepared3"}), \
                mock.patch.object(stackctl, "DEFAULT_SANDBOXIE_BOX", "DefaultBox"), \
                mock.patch.object(stackctl, "DEFAULT_SANDBOXIE_BOX2", "Unprepared2"), \
                mock.patch.object(stackctl, "DEFAULT_SANDBOXIE_BOX3", "Unprepared3"):
            for command in (["server"], ["client", "--client-ts3-sandboxie-box", ""]):
                with self.subTest(command=command):
                    self.assertEqual(stackctl.launch_fork_census(command)["state"], "fork")

    def test_cli_preflight_selects_only_the_clients_in_the_launch_verb(self):
        class StopBeforeDispatch(Exception):
            pass
        cases = [("boot-server", []), ("launch-server", []), ("boot-full", ["Chosen1"]),
                 ("boot-client", ["Chosen1"]), ("boot-client2", ["Chosen2"]), ("boot-client3", ["Chosen3"])]
        for verb, want in cases:
            with self.subTest(verb=verb), mock.patch.object(sys, "platform", "win32"), \
                    mock.patch.object(sys, "argv", ["stackctl.py", verb, "--sandboxie-box", "Chosen1",
                                                  "--sandboxie-box2", "Chosen2", "--sandboxie-box3", "Chosen3"]), \
                    mock.patch.object(stackctl, "launch_fork_census", return_value={"state": "fork"}) as census, \
                    mock.patch.object(fork_census, "write_evidence"), \
                    mock.patch.object(stackctl, "sync_ts3_pid_files", side_effect=StopBeforeDispatch):
                with self.assertRaises(StopBeforeDispatch):
                    stackctl.main()
                self.assertEqual(census.call_args.kwargs["boxes"], want)

    def test_no_boot_scenario_does_not_warn_for_a_verified_fork(self):
        with mock.patch.object(quick_loop, "physical_fork_census",
                               return_value={"state": "fork", "selectedAttempt": "cold-test-001"}), \
                mock.patch.object(scenarios, "load", return_value=[]), \
                mock.patch.object(scenarios, "health", return_value="offline test: stop before live work"), \
                mock.patch("stack_claim.warning", return_value=None), \
                contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()) as err:
            self.assertEqual(scenarios.main(["run"]), 6)
        self.assertNotIn("FORK-MISSING", err.getvalue())

    def test_configuration_rows_describe_the_physical_census(self):
        text = (Path(__file__).resolve().parents[1] / "docs/guides/environment-variables.md").read_text(encoding="utf-8")
        rows = [line for line in text.splitlines() if line.startswith("| `SIMSONLINE_NO_FORK_OK`")
                or line.startswith("| `SIMSONLINE_TS3_DOCUMENTS`")]
        self.assertEqual(len(rows), 2)
        for row in rows:
            with self.subTest(row=row.split("|")[1]):
                self.assertIn("fork_census", row)
        self.assertIn("stackctl", rows[0])
        self.assertNotIn("installed_attempt()", rows[0])


if __name__ == "__main__":
    unittest.main()
