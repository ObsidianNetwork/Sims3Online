"""Unit tests for scripts/check_docs.py (python -m unittest scripts.check_docs_test)."""

from __future__ import annotations

import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from scripts import check_docs
from scripts.check_docs import check_links, check_paths, github_slug, select_docs, split_code


class SplitCodeTests(unittest.TestCase):
    def test_fenced_blocks_and_code_spans_leave_prose(self) -> None:
        lines = ["see [a](a.md) and `scripts/x.py`", "```bash", "python scripts/y.py", "```", "after"]
        prose, code = split_code(lines)
        self.assertIn("[a](a.md)", prose[0])
        self.assertNotIn("scripts/x.py", prose[0])
        self.assertEqual(prose[2], "")
        self.assertEqual(code, [(1, "scripts/x.py"), (3, "python scripts/y.py")])


class SlugTests(unittest.TestCase):
    def test_github_slug_rules(self) -> None:
        self.assertEqual(github_slug("Live check: `Seat.SitDown` from 4 m away (2026-10-08)"),
                         "live-check-seatsitdown-from-4-m-away-2026-10-08")
        self.assertEqual(github_slug("The SACS manager (`0xdd8d40`)"), "the-sacs-manager-0xdd8d40")
        self.assertEqual(github_slug("[Link](x.md) text"), "link-text")


class LinkTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        (self.root / "docs").mkdir()
        (self.root / "docs" / "there.md").write_text("# Title\n\n## The SACS manager (`0xdd8d40`)\n", encoding="utf-8")

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def kinds(self, line: str, anchors: bool = False) -> list[str]:
        prose, _ = split_code([line])
        return [p.kind for p in check_links("docs/here.md", prose, anchors, root=self.root)]

    def test_existing_relative_link_passes(self) -> None:
        self.assertEqual(self.kinds("[ok](there.md)"), [])

    def test_missing_link_is_an_error(self) -> None:
        self.assertEqual(self.kinds("[gone](missing.md)"), ["link"])

    def test_external_and_in_code_links_are_ignored(self) -> None:
        self.assertEqual(self.kinds("[web](https://example.com) `[x](missing.md)`"), [])

    def test_powershell_static_call_is_not_a_reference_definition(self) -> None:
        self.assertEqual(self.kinds("  [System.IO.File]::WriteAllText('path', content)"), [])

    def test_anchor_checked_only_when_asked(self) -> None:
        self.assertEqual(self.kinds("[x](there.md#nope)"), [])
        self.assertEqual(self.kinds("[x](there.md#nope)", anchors=True), ["anchor"])
        self.assertEqual(self.kinds("[x](there.md#the-sacs-manager-0xdd8d40)", anchors=True), [])

    def test_links_to_developer_local_artifacts_are_not_errors(self) -> None:
        # Campaign notes cite run output and build trees that are gitignored: real on the machine that
        # produced them, absent in a clone. check_paths already exempts LOCAL_PREFIXES mentions; links
        # get the same rule -- and a link beside them that is simply broken still errors.
        self.assertEqual(self.kinds("[receipt](../.omo/run/wave8/receipt.json)"), [])
        self.assertEqual(self.kinds("[dll](../SimLink/build/x64/simlink.dll)"), [])
        self.assertEqual(self.kinds("[gone](../some/other/missing.md)"), ["link"])


class PathTests(unittest.TestCase):
    def test_missing_path_in_code_is_a_warning(self) -> None:
        problems = check_paths("docs/a.md", [(3, "python scripts/gone.py --help")], {"scripts/here.py"})
        self.assertEqual([(p.kind, p.severity, p.target) for p in problems], [("path", "warning", "scripts/gone.py")])

    def test_known_local_and_placeholder_paths_pass(self) -> None:
        code = [(1, "python scripts/here.py"), (2, "_analysis/sims3_assemblies_fixed/x.dll"),
                (3, ".omo/cache/fork/x"), (4, "docs/engine/<system>.md"), (5, "docs/engine/native-*.md")]
        self.assertEqual(check_paths("docs/a.md", code, {"scripts/here.py", "docs/engine/native-ts3w.md"}), [])

    def test_a_branch_named_after_the_word_branch_is_not_a_path(self) -> None:
        code = [(1, "Work in the worktree on branch tools/friction (from simscope/w1-mod-feed)"),
                (2, "(Branch tools/sim145) then git switch -c tools/gone")]
        self.assertEqual([p.target for p in check_paths("docs/a.md", code, set())], ["tools/gone"])

    def test_a_git_ignored_path_is_local_not_missing(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            subprocess.run(["git", "init", "-q", str(root)], check=True)
            (root / ".gitignore").write_text("scratch/*\n", encoding="utf-8")
            code = [(1, "python scratch/local_tool.py"), (2, "python scripts/gone.py")]
            self.assertEqual([p.target for p in check_paths("docs/a.md", code, set(), root)], ["scripts/gone.py"])

    def test_a_branch_in_this_clone_and_a_ref_are_not_paths(self) -> None:
        # A doc names a branch whose name starts like a folder (tools/seam-map) or a ref (refs/heads/x) without the
        # word "branch" before it; a name that is no branch and no path is still reported.
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            git = ["git", "-C", str(root), "-c", "user.name=t", "-c", "user.email=t@t"]
            subprocess.run(["git", "init", "-q", str(root)], check=True)
            subprocess.run([*git, "commit", "-q", "--allow-empty", "-m", "x"], check=True)
            subprocess.run([*git, "branch", "tools/seam-map"], check=True)
            code = [(1, "merged tools/seam-map and refs/heads/feed-next"), (2, "see tools/gone")]
            self.assertEqual([p.target for p in check_paths("docs/a.md", code, set(), root)], ["tools/gone"])

    def test_the_allowlist_skips_by_path_and_document(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "scripts").mkdir()
            (root / check_docs.ALLOWLIST).write_text(
                "# comment\ntools/list  # MCP method\nscripts/planned.py  docs/plan*.md  # planned\n", encoding="utf-8")
            code = [(1, "tools/list scripts/planned.py")]
            self.assertEqual(check_paths("docs/plan-a.md", code, set(), root), [])
            self.assertEqual([p.target for p in check_paths("docs/other.md", code, set(), root)], ["scripts/planned.py"])

    def test_a_range_after_a_path_is_not_a_missing_file(self) -> None:
        self.assertEqual(check_paths("docs/a.md", [(1, "receipts in docs/engine/specs/31–35")], set()), [])

    def test_only_the_word_branch_before_a_folder_name_skips_it(self) -> None:
        # A word ending in "branch", a $branch placeholder, or a file after "branch" is still a path to check.
        code = [(1, "git checkout my-branch scripts/gone.py"), (2, "git checkout --source=feature-branch docs/gone"),
                (3, "copy it from the branch scripts/gone.py")]
        self.assertEqual([p.target for p in check_paths("docs/a.md", code, set())],
                         ["scripts/gone.py", "docs/gone", "scripts/gone.py"])


class SelectTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        for name in ("docs/a.md", "docs/engine/b.md", "README.md", "docs/new.md", "scripts/x.py"):
            (self.root / name).parent.mkdir(parents=True, exist_ok=True)
            (self.root / name).write_text("# x\n", encoding="utf-8")
        self.tracked = ["README.md", "docs/a.md", "docs/engine/b.md"]

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def select(self, *paths: str, cwd: Path | None = None) -> list[str]:
        return select_docs(list(paths), self.tracked, root=self.root, cwd=cwd or self.root)

    def test_no_paths_is_every_tracked_file(self) -> None:
        self.assertEqual(self.select(), self.tracked)

    def test_files_and_folders_narrow_it(self) -> None:
        self.assertEqual(self.select("docs/engine"), ["docs/engine/b.md"])
        self.assertEqual(self.select("docs/new.md", "README.md", "docs/engine/"), ["README.md", "docs/engine/b.md", "docs/new.md"])
        self.assertEqual(self.select("b.md", cwd=self.root / "docs" / "engine"), ["docs/engine/b.md"])  # relative to cwd
        self.assertEqual(self.select(str(self.root / "docs")), ["docs/a.md", "docs/engine/b.md"])  # tracked only
        self.assertEqual(self.select("."), self.tracked)

    def test_bad_paths_are_refused(self) -> None:
        for bad in ("docs/gone.md", "scripts/x.py", str(self.root.parent)):
            with self.assertRaises(ValueError):
                self.select(bad)

    def test_main_checks_only_the_selection_and_refuses_bad_paths(self) -> None:
        live = "docs/overview.md"
        with mock.patch.object(check_docs, "tracked_markdown", return_value=[live, "README.md"]), \
                mock.patch.object(check_docs, "run", return_value=[]) as run, mock.patch("builtins.print") as out:
            self.assertEqual(check_docs.main(["--anchors", str(check_docs.ROOT / live)]), 0)
            self.assertEqual(run.call_args.args, (False, True, [live]))
            self.assertIn("in 1 Markdown files", out.call_args.args[0])
            self.assertEqual(check_docs.main(["no/such/file.md"]), 2)
if __name__ == "__main__":
    unittest.main()
