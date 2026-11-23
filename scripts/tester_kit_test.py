"""Unit tests for tester_kit: python -m unittest scripts.tester_kit_test"""
from __future__ import annotations

import hashlib
import json
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import tester_kit as tk  # noqa: E402


class FakeBuild:
    """A fake build tree: every piece a kit takes, as small files."""

    def __init__(self) -> None:
        self.root = Path(tempfile.mkdtemp())
        self.templates = self.root / "templates"
        for name in tk.KIT_SCRIPTS:
            self.write(self.templates / name, f"# {name}\n")
        self.client = self.root / "publish"
        self.write(self.client / "SimsOnline.Client.exe", "exe")
        self.write(self.client / "runtimes" / "win-x64" / "native" / "lib.dll", "native")

    def write(self, path: Path, text: str) -> Path:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        return path

    def sources(self, fork: bool = True, loader: bool = True) -> tk.Sources:
        return tk.Sources(mod=self.write(self.root / "mod.package", "mod"),
                          client_dir=self.client,
                          simlink=self.write(self.root / "SimLink.asi", "simlink"),
                          loader=self.write(self.root / "wininet.dll", "loader") if loader else None,
                          fork=self.write(self.root / "fork.package", "fork") if fork else None,
                          nointro=self.write(self.root / "nointro.package", "nointro"),
                          templates=self.templates)


class KitTest(unittest.TestCase):
    def setUp(self) -> None:
        self.build = FakeBuild()
        self.kit = self.build.root / "out" / "SimsOnline-tester-kit-20261022-120000"

    def test_a_kit_has_the_client_side_layout_and_a_manifest_of_every_file(self) -> None:
        manifest = tk.assemble(self.build.sources(), self.kit, {"feed_commit": "abc", "fork_attempt": "cold-test-109"})
        paths = {row["path"] for row in manifest["files"]}
        self.assertTrue({"game-bin/SimLink.asi", "game-bin/wininet.dll", "mods/Resource.cfg",
                         f"mods/Packages/{tk.MOD_PACKAGE}", f"mods/Packages/{tk.FORK_PACKAGE}",
                         f"mods/Packages/{tk.NOINTRO_PACKAGE}", "client/SimsOnline.Client.exe",
                         "client/runtimes/win-x64/native/lib.dll", "install.ps1", "join.ps1", "README.md"} <= paths)
        self.assertNotIn(tk.MANIFEST, paths)
        self.assertEqual(manifest["feed_commit"], "abc")
        self.assertEqual(manifest["ts3w_sha256"], tk.TS3W_SHA256)
        self.assertEqual(manifest["left_out"], [])
        self.assertEqual(manifest["total_bytes"], sum(row["size"] for row in manifest["files"]))
        on_disk = json.loads((self.kit / tk.MANIFEST).read_text(encoding="utf-8"))
        self.assertEqual(on_disk["files"], manifest["files"])
        self.assertIn(b"PackedFile Packages/*.package", (self.kit / "mods/Resource.cfg").read_bytes())

    def test_the_ea_derived_and_third_party_files_can_be_left_out_and_the_manifest_says_so(self) -> None:
        manifest = tk.assemble(self.build.sources(fork=False, loader=False), self.kit, {})
        paths = {row["path"] for row in manifest["files"]}
        self.assertNotIn(f"mods/Packages/{tk.FORK_PACKAGE}", paths)
        self.assertNotIn("game-bin/wininet.dll", paths)
        self.assertEqual(manifest["left_out"], ["fork", "loader"])

    def test_a_missing_source_stops_the_build_before_anything_is_written(self) -> None:
        sources = self.build.sources()
        sources.simlink = self.build.root / "nope.asi"
        with self.assertRaises(SystemExit):
            tk.assemble(sources, self.kit, {})
        self.assertFalse(self.kit.exists())

    def test_verify_names_missing_changed_and_extra_files(self) -> None:
        tk.assemble(self.build.sources(), self.kit, {})
        self.assertEqual(tk.verify(self.kit), [])
        (self.kit / "join.ps1").write_text("tampered", encoding="utf-8")
        (self.kit / "game-bin" / "wininet.dll").unlink()
        (self.kit / "stray.txt").write_text("x", encoding="utf-8")
        problems = tk.verify(self.kit)
        self.assertIn("changed join.ps1", problems)
        self.assertIn("missing game-bin/wininet.dll", problems)
        self.assertIn("extra stray.txt", problems)
        self.assertEqual(len(problems), 3)

    def test_verify_without_a_manifest_says_so(self) -> None:
        self.kit.mkdir(parents=True)
        self.assertTrue(tk.verify(self.kit)[0].startswith("no readable manifest.json"))

    def test_the_zip_holds_the_kit_under_its_own_folder(self) -> None:
        tk.assemble(self.build.sources(), self.kit, {})
        archive = tk.make_zip(self.kit)
        with zipfile.ZipFile(archive) as z:
            names = z.namelist()
        self.assertIn(f"{self.kit.name}/manifest.json", names)
        self.assertIn(f"{self.kit.name}/client/SimsOnline.Client.exe", names)
        self.assertEqual(tk.kits(self.kit.parent), [self.kit])


class FeedTest(unittest.TestCase):
    def test_make_mods_generated_files_do_not_make_the_feed_dirty(self) -> None:
        self.assertFalse(tk.feed_dirty(" M SimsOnlineServerHostMod/BuildStamp.cs\n M SimsOnlineServerHostMod/EmbeddedHandlerAssembly.cs\n"))
        self.assertTrue(tk.feed_dirty(" M SimsOnlineServerHostMod/BuildStamp.cs\n M scripts/stackctl.py\n"))
        self.assertFalse(tk.feed_dirty(""))

    def test_the_hosts_fork_is_named_by_attempt_and_hash_and_absent_without_a_package(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            package = Path(tmp) / tk.FORK_PACKAGE
            self.assertIsNone(tk.host_fork(package, "cold-test-110"))
            package.write_bytes(b"fork")
            self.assertEqual(tk.host_fork(package, "cold-test-110"),
                             {"attempt": "cold-test-110", "package_sha256": hashlib.sha256(b"fork").hexdigest()})


class ForkReceiptTest(unittest.TestCase):
    def test_an_unreadable_receipt_is_recorded_as_unreadable_with_one_warning(self) -> None:
        import contextlib
        import io
        import quick_loop
        with tempfile.TemporaryDirectory() as tmp:
            receipt = Path(tmp) / ".omo/cache/fork/cold-test-1/deployment.json"
            receipt.parent.mkdir(parents=True)
            receipt.write_bytes(bytes([0xff, 0xfe, 0x80]) + b"bad")
            old, quick_loop.MAIN = quick_loop.MAIN, Path(tmp)
            try:
                err = io.StringIO()
                with contextlib.redirect_stderr(err):
                    attempt = tk.fork_attempt("tester-kit")
            finally:
                quick_loop.MAIN = old
            self.assertEqual(attempt, "unreadable")
            self.assertEqual(len(err.getvalue().strip().splitlines()), 1)
            package = Path(tmp) / tk.FORK_PACKAGE
            package.write_bytes(b"fork")
            self.assertEqual(tk.host_fork(package, attempt)["attempt"], "unreadable")

    def test_build_records_an_unreadable_receipt_once_in_the_manifest(self) -> None:
        import argparse
        import contextlib
        import io
        import quick_loop
        from unittest import mock
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            receipt = root / "main/.omo/cache/fork/cold-test-1/deployment.json"
            receipt.parent.mkdir(parents=True)
            receipt.write_bytes(bytes([0xff, 0xfe, 0x80]) + b"bad")
            (root / "real/Mods/Packages").mkdir(parents=True)
            (root / "real/Mods/Packages" / tk.FORK_PACKAGE).write_bytes(b"fork")
            mod = root / "repo/SimsOnlineServerHostMod/bin/Release" / tk.MOD_PACKAGE
            mod.parent.mkdir(parents=True)
            mod.write_bytes(b"mod")
            (root / "out/_publish").mkdir(parents=True)
            (root / "out/_publish/SimsOnline.Client.exe").write_bytes(b"exe")
            zipped = root / "kit.zip"
            zipped.write_bytes(b"zip")
            metas: list[dict] = []

            def fake_run(cmd: list[str], timeout: int = 1800) -> tuple[int, str]:
                if "--write" in cmd:
                    Path(cmd[-1]).write_bytes(b"nointro")
                return 0, "x"

            def fake_assemble(src: tk.Sources, kit: Path, meta: dict) -> dict:
                metas.append(meta)
                return {"files": [], "total_bytes": 0}

            args = argparse.Namespace(no_mod_build=True, no_publish=True, no_fork=False, fork_builder=False,
                                      simlink=None, loader=None, no_loader=True)
            err = io.StringIO()
            with contextlib.ExitStack() as stack:
                for target, value in (("MAIN", root / "main"), ("REAL", root / "real"), ("slot_pointer", lambda: None)):
                    stack.enter_context(mock.patch.object(quick_loop, target, value))
                for name, value in (("ROOT", root / "repo"), ("OUT", root / "out"), ("game_bin", lambda: root),
                                    ("run", fake_run), ("assemble", fake_assemble), ("verify", lambda kit: []),
                                    ("make_zip", lambda kit: zipped), ("simlink_version", lambda path: "v")):
                    stack.enter_context(mock.patch.object(tk, name, value))
                (root / "SimLink.asi").write_bytes(b"simlink")
                stack.enter_context(contextlib.redirect_stdout(io.StringIO()))
                stack.enter_context(contextlib.redirect_stderr(err))
                self.assertEqual(tk.build(args), 0)
            self.assertEqual(metas[0]["host_fork"]["attempt"], "unreadable")
            self.assertEqual(metas[0]["fork_attempt"], "unreadable")
            self.assertEqual(err.getvalue().count("fork receipt unreadable"), 1)

    def test_no_receipt_stays_none(self) -> None:
        import quick_loop
        with tempfile.TemporaryDirectory() as tmp:
            old, quick_loop.MAIN = quick_loop.MAIN, Path(tmp)
            try:
                self.assertIsNone(tk.fork_attempt("tester-kit"))
            finally:
                quick_loop.MAIN = old


class BoxTest(unittest.TestCase):
    def test_a_box_counts_only_when_its_section_is_enabled(self) -> None:
        ini = "\ufeff[GlobalSettings]\nBoxGrouping=:DefaultBox,Client4Box\n\n[Client4Box]\nEnabled=y\n\n[Old]\nEnabled=n\n"
        self.assertTrue(tk.sandboxie_has_box(ini, "Client4Box"))
        self.assertFalse(tk.sandboxie_has_box(ini, "Old"))
        self.assertFalse(tk.sandboxie_has_box(ini, "Client5Box"))

    def test_the_new_box_profile_is_the_templates_with_the_box_swapped(self) -> None:
        template = Path("boxes/Client4Box/drive/C/Sims/.sims3-client/profile/The Sims 3")
        self.assertEqual(tk.box_profile(template, "Client4Box", "Client5Box"),
                         Path("boxes/Client5Box/drive/C/Sims/.sims3-client/profile/The Sims 3"))
        with self.assertRaises(SystemExit):
            tk.box_profile(template, "Client3Box", "Client5Box")

    def test_the_missing_box_text_names_the_users_step(self) -> None:
        text = tk.box_missing_text("Client5Box", "Client4Box")
        self.assertIn("Duplicate Box Config", text)
        self.assertIn("[Client5Box]", text)


if __name__ == "__main__":
    unittest.main()
