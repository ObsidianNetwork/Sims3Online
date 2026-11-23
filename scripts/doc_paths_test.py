"""Tests for public documentation path resolution."""

from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from scripts import doc_paths


class DocPathsTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        (self.root / "docs" / "reference").mkdir(parents=True)
        (self.root / "docs" / "reference" / "protocol.md").write_text("# Protocol\n", encoding="utf-8")

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def test_normalizes_repository_relative_references(self) -> None:
        self.assertEqual(doc_paths.normalize("docs/reference/protocol.md"), "reference/protocol.md")
        self.assertEqual(doc_paths.normalize(".\\docs\\reference\\protocol.md"), "reference/protocol.md")
        self.assertEqual(doc_paths.to_canonical("reference/protocol.md"), "docs/reference/protocol.md")

    def test_resolves_existing_and_new_paths_under_docs(self) -> None:
        expected = self.root / "docs" / "reference" / "protocol.md"
        self.assertEqual(doc_paths.resolve(self.root, "reference/protocol.md"), expected)
        self.assertEqual(doc_paths.resolve(self.root, "reference/new.md"),
                         self.root / "docs" / "reference" / "new.md")
        self.assertEqual(doc_paths.live_ref(self.root, "reference/protocol.md"),
                         "docs/reference/protocol.md")
        self.assertEqual(doc_paths.write_ref(self.root, "reference/new.md"),
                         "docs/reference/new.md")

    def test_lists_documentation_files(self) -> None:
        self.assertEqual(doc_paths.doc_root(self.root), "docs")
        self.assertEqual(doc_paths.layouts_present(self.root), ["docs"])
        self.assertEqual(list(doc_paths.iter_note_files(self.root, "protocol.md")),
                         [self.root / "docs" / "reference" / "protocol.md"])

    def test_missing_docs_directory_has_no_layout(self) -> None:
        empty = self.root / "empty"
        empty.mkdir()
        self.assertEqual(doc_paths.layouts_present(empty), [])
        self.assertEqual(list(doc_paths.iter_note_files(empty)), [])


if __name__ == "__main__":
    unittest.main()
