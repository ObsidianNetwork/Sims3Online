"""Physical fork guard regressions. All package and receipt fixtures are temporary."""
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
import quick_loop as ql
import fork_census
import stackctl


class PhysicalForkGuard(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.server = self.root / "server"
        self.client = self.root / "client"
        self.package = "SimsOnlineGameplay02Cold.package"
        for profile in (self.server, self.client):
            (profile / "Mods/Packages").mkdir(parents=True)
        patches = [
            mock.patch.object(ql, "MAIN", self.root),
            mock.patch.object(ql, "RUN", self.root / "run"),
            mock.patch.object(ql, "REAL", self.server),
            mock.patch.object(ql, "ONEDRIVE_MOD", self.server / "Mods/Packages/SimsOnlineServerHostMod.package"),
            mock.patch.object(ql, "BOXES", ["Client2Box"]),
            mock.patch.object(ql, "box", return_value=self.client),
            mock.patch.object(fork_census, "resolved_profiles",
                              return_value=([self.server, self.client], [self.server, self.client])),
            mock.patch.dict(os.environ, {"SIMSONLINE_NO_FORK_OK": "", "SIMSONLINE_TS3_DOCUMENTS": str(self.server)}),
        ]
        for patch in patches:
            patch.start()
            self.addCleanup(patch.stop)

    def put(self, profile, data=b"build A"):
        path = profile / "Mods/Packages" / self.package
        path.write_bytes(data)
        return path

    def receipt(self, status="installed"):
        path = self.root / ".omo/cache/fork/cold-test-001/deployment.json"
        path.parent.mkdir(parents=True)
        path.write_text(json.dumps({
            "status": status, "preparedSha256": hashlib.sha256(b"build A").hexdigest(),
            "deploymentPackage": str(self.server / "Mods/Packages" / self.package),
            "overridePackage": True,
        }), encoding="utf-8")

    def guard(self, allow=True):
        with contextlib.redirect_stderr(io.StringIO()) as err:
            try:
                ql.fork_guard(allow)
            except SystemExit as error:
                return error.code, err.getvalue()
        return None, err.getvalue()

    def test_private_worktree_receipt_loss_refuses_even_stock_override(self):
        package = self.put(self.server)
        code, err = self.guard()
        self.assertEqual(code, 2)
        self.assertIn(str(package.resolve()), err)
        self.assertIn(hashlib.sha256(b"build A").hexdigest(), err)
        self.assertIn("unknown provenance", ql.fork_line())

    def test_restored_receipt_cannot_hide_a_live_package(self):
        self.receipt("restored")
        self.put(self.server)
        self.assertEqual(self.guard()[0], 2)
        self.assertIn("unknown provenance", ql.fork_line())

    def test_each_required_profile_must_hold_the_selected_build(self):
        self.receipt()
        self.put(self.server)
        for data in (None, b"build B"):
            with self.subTest(client=data):
                if data:
                    self.put(self.client, data)
                self.assertEqual(self.guard()[0], 2)

    def test_empty_profiles_allow_deliberate_stock(self):
        self.assertIsNone(self.guard()[0])

    def test_matching_profiles_report_the_attempt(self):
        self.receipt()
        self.put(self.server)
        self.put(self.client)
        self.assertIsNone(self.guard(False)[0])
        self.assertIn("cold-test-001", ql.fork_line())

    def test_installed_receipt_without_any_packages_is_not_stock(self):
        self.receipt()
        self.assertEqual(self.guard()[0], 2)
        self.assertNotIn("stock;", ql.fork_line())

    def test_direct_and_front_door_launches_refuse_before_spawning(self):
        self.put(self.server)
        for command in (["dotnet", "run", "--project", "DedicatedServer"],
                        ["front-door.exe", "--pid", "123", "--serve"]):
            with self.subTest(command=command), \
                    mock.patch.object(stackctl, "RUN_DIR", str(self.root / "run")), \
                    mock.patch.dict(os.environ, {"SIMSONLINE_MAIN_CHECKOUT": str(self.root),
                                                 "SIMSONLINE_NO_FORK_OK": "1"}), \
                    mock.patch.object(stackctl.subprocess, "Popen") as spawn, \
                    contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit):
                    stackctl.launch_controller(command, "server", str(self.root / "server.log"), str(self.root))
                spawn.assert_not_called()
                evidence = json.loads((self.root / "run/server-fork-census.state.json").read_text(encoding="utf-8"))
                self.assertEqual(evidence["state"], "unknown")

    def test_all_stackctl_launch_verbs_guard_before_process_or_mod_changes(self):
        self.put(self.server)
        verbs = ["up", "server", "client", "client2", "client3", "client-restart", "server-reattach",
                 "boot-full", "boot-server", "boot-client", "boot-client2", "boot-client3", "boot-client-restart",
                 "launch-full", "launch-server", "launch-client", "launch-client2", "launch-client3",
                 "launch-client-restart", "launch-reattach-server", "reattach-server", "reload-mod"]
        for verb in verbs:
            with self.subTest(verb=verb), \
                    mock.patch.object(sys, "argv", ["stackctl.py", verb, "--no-fork-ok", "--front-door"]), \
                    mock.patch.object(stackctl, "RUN_DIR", str(self.root / "run")), \
                    mock.patch.dict(os.environ, {"SIMSONLINE_MAIN_CHECKOUT": str(self.root)}), \
                    mock.patch.object(stackctl, "sync_ts3_pid_files") as sync, \
                    mock.patch.object(stackctl, "ensure_mod_synced") as mods, \
                    contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit) as stop:
                    stackctl.main()
                self.assertEqual(stop.exception.code, 2)
                sync.assert_not_called()
                mods.assert_not_called()

    def test_census_and_guard_leave_profile_files_and_receipts_untouched(self):
        self.receipt("restored")
        self.put(self.server)
        (self.server / "scriptCache.package").write_bytes(b"saved cache")
        paths = [p for p in self.root.rglob("*") if p.is_file()]
        before = {p: (p.read_bytes(), p.stat().st_mtime_ns) for p in paths}
        self.assertEqual(self.guard()[0], 2)
        self.assertEqual(before, {p: (p.read_bytes(), p.stat().st_mtime_ns) for p in paths})

    def test_restored_installation_package_with_original_hash_is_stock(self):
        original = self.root / "game/gameplay.package"
        original.parent.mkdir()
        original.write_bytes(b"stock original")
        self.receipt("restored")
        receipt = self.root / ".omo/cache/fork/cold-test-001/deployment.json"
        record = json.loads(receipt.read_text(encoding="utf-8"))
        record.update(overridePackage=False, deploymentPackage=str(original),
                      originalSha256=hashlib.sha256(b"stock original").hexdigest())
        receipt.write_text(json.dumps(record), encoding="utf-8")
        result = ql.physical_fork_census()
        self.assertEqual(result["state"], "stock")
        row = next(r for r in result["packages"] if r["present"])
        self.assertTrue(row["stockOriginal"])
        self.assertIn("stock original", row["provenance"])
        self.assertIsNone(self.guard()[0])


class ProfileResolution(unittest.TestCase):
    def test_configured_server_onedrive_and_each_box_user_are_included(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            template = str(root / "{box}/users/{user}/Documents/Electronic Arts/The Sims 3")
            box = root / "Client4Box/users/copied-linux-user/Documents/Electronic Arts/The Sims 3"
            (box / "Mods").mkdir(parents=True)
            with mock.patch.dict(os.environ, {
                    "SIMSONLINE_TS3_DOCUMENTS": str(root / "redirected-documents"),
                    "SIMSONLINE_REAL_PROFILE": str(root / "native-documents"),
                    "SIMSONLINE_ONEDRIVE_MOD": str(root / "cloud/Mods/Packages/mod.package"),
                    "SIMSONLINE_BOX_PROFILE": template,
                    "SIMSONLINE_SANDBOXIE_BOX": "Client2Box",
                    "SIMSONLINE_SANDBOXIE_BOX2": "Client3Box",
                    "SIMSONLINE_SANDBOXIE_BOX3": "Client4Box",
                    "SIMSONLINE_CLIENT_SAVES_DIR": str(root / "explicit-client/Saves"),
            }), mock.patch.object(sys, "platform", "win32"):
                profiles, required = fork_census.resolved_profiles()
            self.assertIn(root / "redirected-documents", required)
            self.assertIn(root / "native-documents", profiles)
            self.assertIn(root / "cloud", profiles)
            self.assertIn(root / "explicit-client", required)
            self.assertIn(box, required)
            self.assertTrue(any("Client2Box" in str(p) for p in required))
            self.assertTrue(any("Client3Box" in str(p) for p in required))

    def test_windows_server_uses_redirected_documents_registry(self):
        if sys.platform != "win32":
            self.skipTest("Windows registry adapter")
        import winreg
        with mock.patch.dict(os.environ):
            os.environ.pop("SIMSONLINE_TS3_DOCUMENTS", None)
            with mock.patch.object(winreg, "OpenKey"), \
                    mock.patch.object(winreg, "QueryValueEx", return_value=("Z:/redirected/Documents", winreg.REG_SZ)):
                self.assertEqual(fork_census.documents_profile(), Path("Z:/redirected/Documents/Electronic Arts/The Sims 3"))

    def test_unreadable_profile_resolution_fails_closed(self):
        with mock.patch.object(fork_census, "resolved_profiles", side_effect=PermissionError("profile denied")):
            with tempfile.TemporaryDirectory() as folder:
                result = fork_census.census(Path(folder))
        self.assertEqual(result["state"], "unreadable")
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            fork_census.guard(result, True)


if __name__ == "__main__":
    unittest.main()
