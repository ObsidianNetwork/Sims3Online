"""Unit tests for scripts/proprietary_input_guard.py."""
from __future__ import annotations

from pathlib import Path
import sys
import tempfile
import unittest

SCRIPTS_DIR = Path(__file__).resolve().parent
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))
import proprietary_input_guard as guard  # noqa: E402


class ProprietaryInputGuardTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.root = Path(self._tmp.name)

    def write(self, rel: str, content: str | bytes) -> Path:
        target = self.root / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        if isinstance(content, bytes):
            target.write_bytes(content)
        else:
            target.write_text(content, encoding="utf-8")
        return target

    def test_clean_tree_passes_and_self_scan_passes(self) -> None:
        self.write("README.md", "# Sims3Online\nClean public documentation.\n")
        self.write("DedicatedServer/Program.cs", "// Public controller\n")
        self.assertEqual(guard.scan_tree(self.root), [])

        # Guard modules themselves must stay clean under their own rules.
        self.assertEqual(guard.scan_file(SCRIPTS_DIR / "proprietary_input_guard.py", "scripts/proprietary_input_guard.py"), [])
        self.assertEqual(
            guard.scan_file(SCRIPTS_DIR / "proprietary_input_guard_test.py", "scripts/proprietary_input_guard_test.py"),
            [],
        )

    def test_rejects_excluded_directories_and_files(self) -> None:
        self.write("AGENTS.md", "# internal\n")
        self.write("libr" + "ary/internal.md", "secret plan\n")
        rules = {(v.path, v.rule) for v in guard.scan_tree(self.root)}
        self.assertIn(("AGENTS.md", "excluded-path"), rules)
        self.assertIn(("libr" + "ary", "excluded-path"), rules)

    def test_rejects_binaries_and_non_stub_dlls(self) -> None:
        self.write("DedicatedServer/bad.dll", b"MZ\x90\x00not-a-stub")
        self.write("refs/sims3/real.dll", b"MZ\x90\x00real-game-dll")
        self.write("Assets/save.package", b"DBPF")
        by_path = {v.path: v.rule for v in guard.scan_tree(self.root)}
        self.assertEqual(by_path["DedicatedServer/bad.dll"], "prohibited-binary")
        self.assertEqual(by_path["refs/sims3/real.dll"], "non-stub-dll")
        self.assertEqual(by_path["Assets/save.package"], "prohibited-binary")

    def test_rejects_personal_paths_tracker_ids_disasm_and_re_tools(self) -> None:
        win_path = "C:\\" + "Us" + "ers\\alice\\dev"
        tracker = "SI" + "M-166"
        disasm = "\n".join([
            "00401000: 55 8b              push ebp",
            "00401001: 8b ec              mov ebp, esp",
            "00401003: 83 ec 10           sub esp, 10h",
        ])
        re_word = "ghid" + "ra"
        self.write("notes.txt", f"{win_path}\n{tracker}\n{re_word}\n{disasm}\n")
        rules = {v.rule for v in guard.scan_tree(self.root)}
        self.assertIn("personal-path", rules)
        self.assertIn("tracker-id", rules)
        self.assertIn("excluded-re-or-infra", rules)
        self.assertIn("disassembly-dump", rules)

    def test_allows_generic_placeholder_home_paths(self) -> None:
        self.write("DedicatedServer.Tests/Sample.cs", 'string p = "/home/user/.sims3-client";\n')
        self.assertEqual(guard.scan_tree(self.root), [])


if __name__ == "__main__":
    unittest.main()
