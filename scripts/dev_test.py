"""dev.py: the no-pytest fallback must see what pytest's pyproject pythonpath gives the tests."""
from __future__ import annotations

import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import dev  # noqa: E402


class FallbackPythonPath(unittest.TestCase):
    def test_pythonpath_matches_pyproject_and_imports_resolve_from_elsewhere(self) -> None:
        entries = dev.pytest_pythonpath().split(os.pathsep)
        self.assertIn(str(dev.ROOT), entries)
        self.assertIn(str(dev.ROOT / "scripts"), entries)
        with tempfile.TemporaryDirectory() as tmp:  # neither "scripts.dev" nor bare "dev" is importable from here alone
            for module in ("dev", "scripts.dev"):
                done = subprocess.run([sys.executable, "-c", f"import {module}"], cwd=tmp, capture_output=True, text=True,
                                      env={**os.environ, "PYTHONPATH": dev.pytest_pythonpath()}, timeout=60, check=False)
                self.assertEqual(done.returncode, 0, done.stderr)


if __name__ == "__main__":
    unittest.main()
