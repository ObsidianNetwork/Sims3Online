"""Contracts for the history-liveness probe producer and its remote verdict."""

import re
import subprocess
import tempfile
import unittest
from collections.abc import Iterator
from contextlib import contextmanager
from pathlib import Path
from unittest.mock import patch

from scripts import late_join


@contextmanager
def probe_fixture() -> Iterator[Path]:
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        template = root / "scripts/probes/HistoryLivenessProbe.cs"
        template.parent.mkdir(parents=True)
        template.write_text(
            (late_join.ROOT / "scripts/probes/HistoryLivenessProbe.cs").read_text(encoding="utf-8"),
            encoding="utf-8",
        )
        yield root


class HistoryLivenessToolTests(unittest.TestCase):
    def test_generated_probe_uses_requested_numeric_ids(self) -> None:
        reply = subprocess.CompletedProcess([], 0, stdout="  ok = true\n", stderr="")
        with probe_fixture() as root:
            with patch.object(late_join, "ROOT", root), patch.object(late_join.subprocess, "run", return_value=reply):
                self.assertEqual(late_join.check_liveness(["10", "200"]), 0)
            source = (root / ".omo/run/act/HistoryLivenessProbe.cs").read_text(encoding="utf-8")
        binding = re.search(r'private const string TargetIds = "([^"]*)";', source)
        self.assertIsNotNone(binding)
        assert binding is not None
        self.assertEqual({int(value) for value in binding.group(1).split(",") if value}, {10, 200})

    def test_transport_success_does_not_replace_native_verdict(self) -> None:
        for code, output, expected in [
            (0, "ok = false\nviolations = 2\n", 1),
            (1, "ok = true\n", 1),
            (0, "accepted=True\n", 1),
            (0, "ok = true\nviolations = 0\n", 0),
        ]:
            with self.subTest(code=code, output=output), probe_fixture() as root:
                reply = subprocess.CompletedProcess([], code, stdout=output, stderr="")
                with (
                    patch.object(late_join, "ROOT", root),
                    patch.object(late_join.subprocess, "run", return_value=reply),
                ):
                    self.assertEqual(late_join.check_liveness(["10"]), expected)


if __name__ == "__main__":
    unittest.main()
