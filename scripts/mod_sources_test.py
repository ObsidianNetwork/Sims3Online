"""Unit tests for mod_sources.py (the mod sources' hash and build ledger): python -m unittest scripts.mod_sources_test"""
from __future__ import annotations

import contextlib
import hashlib
import io
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import mod_sources as ms  # noqa: E402


class Sources(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        mod = self.root / ms.MOD_DIR
        (mod / "Handlers").mkdir(parents=True)
        (mod / "Presentation").mkdir()
        (mod / "Main.cs").write_text("class Main {}\n", encoding="utf-8")
        (mod / "Handlers" / "H.cs").write_text("class H {}\n", encoding="utf-8")
        (mod / "Presentation" / "P.cs").write_text("class P {}\n", encoding="utf-8")
        for name in ("BuildStamp.cs", "EmbeddedHandlerAssembly.cs", "Sims3PackageBuilder.cs"):
            (mod / name).write_text("// " + name + "\n", encoding="utf-8")
        (mod / "notes.txt").write_text("not a source\n", encoding="utf-8")
        for args in (["init", "-q"], ["config", "user.email", "t@example.com"], ["config", "user.name", "t"],
                     ["config", "core.autocrlf", "false"], ["add", "."], ["commit", "-q", "-m", "base"]):
            subprocess.run(["git", *args], cwd=self.root, check=True, capture_output=True)

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def test_which_files_and_a_line_ending_free_hash(self) -> None:
        files = [p.relative_to(self.root).as_posix() for p in ms.source_files(self.root)]
        self.assertEqual(files, [f"{ms.MOD_DIR}/Handlers/H.cs", f"{ms.MOD_DIR}/Main.cs", f"{ms.MOD_DIR}/Presentation/P.cs"])
        before = ms.fingerprint(self.root)
        self.assertEqual((before["dirty"], before["files"], ms.stamp_word(before)), ([], 3, before["sources"]))
        main = self.root / ms.MOD_DIR / "Main.cs"
        main.write_bytes(b"class Main {}\r\n")  # a CRLF checkout of the same commit
        self.assertEqual(ms.source_hash(ms.source_files(self.root), self.root), before["sources"])
        main.write_text("class Main { }\n", encoding="utf-8")
        self.assertNotEqual(ms.source_hash(ms.source_files(self.root), self.root), before["sources"])
        (self.root / ms.MOD_DIR / "BuildStamp.cs").write_text("// rewritten by every build\n", encoding="utf-8")
        self.assertEqual(ms.dirty_files(ms.source_files(self.root), self.root), [("M", f"{ms.MOD_DIR}/Main.cs")])

    def test_dirty_build_keeps_its_changes(self) -> None:
        clean = ms.record("abc1234@2026-10-18T01:00:00Z", root=self.root)
        self.assertEqual((clean["dirty"], clean.get("kept")), ([], None))
        (self.root / ms.MOD_DIR / "Handlers" / "H.cs").write_text("class H { int x; }\n", encoding="utf-8")
        (self.root / ms.MOD_DIR / "Presentation" / "New.cs").write_text("class New {}\n", encoding="utf-8")
        (self.root / ms.MOD_DIR / "Main.cs").unlink()
        word = ms.stamp_word(ms.fingerprint(self.root))
        self.assertTrue(word.endswith("+dirty"))
        row = ms.record("abc1234@2026-10-18T02:00:00Z", sources=word, dll="d", package="p", root=self.root)
        self.assertEqual(row["dirty"], [f"{ms.MOD_DIR}/Handlers/H.cs", f"{ms.MOD_DIR}/Main.cs",
                                        f"{ms.MOD_DIR}/Presentation/New.cs"])
        kept = self.root / row["kept"]
        self.assertEqual(kept.name, "20261018T020000Z")
        diff = (kept / "changes.diff").read_text(encoding="utf-8")
        self.assertIn("+class H { int x; }", diff)
        self.assertIn("deleted file", diff)
        self.assertEqual((kept / "untracked" / ms.MOD_DIR / "Presentation" / "New.cs").read_text(encoding="utf-8"),
                         "class New {}\n")
        self.assertNotIn("changedDuringBuild", row)
        builds = ms.ledger(self.root)
        self.assertEqual(sorted(builds), ["abc1234@2026-10-18T01:00:00Z", "abc1234@2026-10-18T02:00:00Z"])
        self.assertEqual(ms.find("abc1234", builds)["builtUtc"], "2026-10-18T02:00:00Z")  # the newest
        self.assertEqual(ms.find("2026-10-18T01:00:00Z", builds)["dirty"], [])
        late = ms.record("abc1234@2026-10-18T03:00:00Z", sources="000000000000+dirty", root=self.root)
        self.assertEqual(late["changedDuringBuild"], "000000000000+dirty")  # an edit between the stamp and the record
        self.assertEqual(json.loads(ms.ledger_path(self.root).read_text(encoding="utf-8").splitlines()[1])["dllSha256"], "d")

    def test_a_reader_follows_the_run_dir(self) -> None:
        ms.record("abc1234@2026-10-18T01:00:00Z", root=self.root)
        with mock.patch.dict(os.environ, {"SIMSONLINE_RUN_DIR": str(ms.ledger_path(self.root).parent)}):
            self.assertEqual(ms.read_path(), ms.ledger_path(self.root))
            self.assertEqual(sorted(ms.ledger()), ["abc1234@2026-10-18T01:00:00Z"])
        with mock.patch.dict(os.environ, {"SIMSONLINE_RUN_DIR": ""}):
            self.assertEqual(ms.read_path(), ms.ledger_path())

    def test_no_git_is_unknown_not_an_error(self) -> None:
        subprocess.run(["git", "init", "-q", str(self.root / "elsewhere")], check=True, capture_output=True)
        info = ms.fingerprint(self.root / "elsewhere")  # no commit there: git rev-parse fails
        self.assertIsNone(info["dirty"])
        self.assertTrue(ms.stamp_word(info).endswith("+unknown"))


class Installed(unittest.TestCase):
    def test_show_with_no_stamp_names_each_profiles_build(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            run = Path(tmp) / "run"
            run.mkdir()
            old, new = b"package one", b"package two"
            rows = [{"stamp": "aaa1111@2026-10-18T01:00:00Z", "builtUtc": "2026-10-18T01:00:00Z", "sources": "s1",
                     "dirty": [], "packageSha256": hashlib.sha256(old).hexdigest()},
                    {"stamp": "bbb2222@2026-10-18T02:00:00Z", "builtUtc": "2026-10-18T02:00:00Z", "sources": "s2",
                     "dirty": ["SimsOnlineServerHostMod/A.cs"], "kept": ".omo/run/mod-builds/x",
                     "packageSha256": hashlib.sha256(new).hexdigest()}]
            (run / "mod-builds.jsonl").write_text("".join(json.dumps(r) + "\n" for r in rows), encoding="utf-8")
            places = []
            for name, content in (("server", new), ("onedrive", new), ("Client2Box", old), ("Client3Box", None)):
                path = Path(tmp) / name / ms.PACKAGE
                path.parent.mkdir()
                if content is not None:
                    path.write_bytes(content)
                places.append((name, path))

            def show(where: list) -> tuple[int, str]:
                out = io.StringIO()
                with mock.patch.dict(os.environ, {"SIMSONLINE_RUN_DIR": str(run)}), \
                        mock.patch.object(ms, "installed_packages", return_value=where), contextlib.redirect_stdout(out):
                    code = ms.main(["--show"])
                return code, out.getvalue()

            code, text = show(places)
            self.assertEqual(code, 0)
            lines = text.splitlines()
            self.assertRegex(lines[1], r"^  server\s+bbb2222@2026-10-18T02:00:00Z  sources s2, 1 dirty, kept in "
                                       r"\.omo/run/mod-builds/x; file \d{4}-")
            self.assertRegex(lines[3], r"^  Client2Box\s+aaa1111@2026-10-18T01:00:00Z  sources s1, clean; file ")
            self.assertTrue(lines[4].startswith("  Client3Box  no package ("))
            self.assertEqual(lines[5], "  the profiles hold different builds")
            self.assertIn('"stamp": "aaa1111@2026-10-18T01:00:00Z"', text)
            self.assertIn('"stamp": "bbb2222@2026-10-18T02:00:00Z"', text)
            # a build the ledger does not have, or no package anywhere: exit 1
            places[1][1].write_bytes(b"a local build")
            code, text = show(places)
            self.assertEqual(code, 1)
            self.assertRegex(text, r"\n  onedrive\s+not in the ledger: sha256 [0-9a-f]{16}, file ")
            self.assertEqual(show([places[3]])[0], 1)
