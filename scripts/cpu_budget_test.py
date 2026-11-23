"""Tests for cpu_budget: when it is on, what it changes in a build's argv, environment and priority, and what it leaves alone."""
from __future__ import annotations

import os
import subprocess
import sys
import tempfile
import unittest
from datetime import datetime
from unittest import mock
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import cpu_budget as cb  # noqa: E402


class WhenActive(unittest.TestCase):
    def test_off_without_a_claim_on_with_one(self):
        self.assertFalse(cb.active({}, held=False))
        self.assertEqual(cb.active({}, held=True), os.name == "nt")

    def test_env_can_turn_it_off_but_never_on_without_a_claim(self):
        self.assertFalse(cb.active({"SIMSONLINE_CPU_BUDGET": "1"}, held=False))
        self.assertFalse(cb.active({"SIMSONLINE_CPU_BUDGET": "0"}, held=True))

    def test_jobs_default_is_a_quarter_of_the_cores_and_overridable(self):
        self.assertGreaterEqual(cb.jobs({}), 2)
        self.assertEqual(cb.jobs({"SIMSONLINE_CPU_JOBS": "3"}), 3)
        self.assertGreaterEqual(cb.jobs({"SIMSONLINE_CPU_JOBS": "x"}), 2)


class Apply(unittest.TestCase):
    ON = {"SIMSONLINE_CPU_JOBS": "4"}

    def apply(self, argv, kwargs=None, environ=None):
        with mock.patch.object(cb.os, "name", "nt"):
            return cb.apply(argv, kwargs, environ=self.ON if environ is None else environ, held=True)

    def test_inactive_changes_nothing(self):
        argv, kwargs = self.apply(["dotnet", "build", "x.csproj"], {"env": None}, environ={"SIMSONLINE_CPU_BUDGET": "0"})
        self.assertEqual(argv, ["dotnet", "build", "x.csproj"])
        self.assertEqual(kwargs, {"env": None})

    def test_dotnet_build_gets_a_cap_after_the_verb(self):
        for verb in ("build", "test", "publish", "pack"):
            argv, _ = self.apply(["dotnet", verb, "x.csproj", "-c", "Release"])
            self.assertEqual(argv, ["dotnet", verb, "-m:4", "x.csproj", "-c", "Release"])

    def test_dotnet_run_and_other_tools_keep_their_argv(self):
        for command in (["dotnet", "run", "--project", "p"], ["cargo", "test"], ["pwsh", "-File", "make-mod.ps1"]):
            self.assertEqual(self.apply(command)[0], command)

    def test_an_existing_cap_is_left_alone(self):
        for flag in ("-m", "-m:8", "/m:2", "--maxcpucount:3", "-maxcpucount"):
            command = ["dotnet", "build", flag, "x"]
            self.assertEqual(self.apply(command)[0], command)

    def test_cargo_jobs_set_but_never_over_the_callers(self):
        _, kwargs = self.apply(["cargo", "test"], {"env": {"PATH": "p"}})
        self.assertEqual(kwargs["env"]["CARGO_BUILD_JOBS"], "4")
        self.assertEqual(kwargs["env"]["PATH"], "p")
        _, kwargs = self.apply(["cargo", "test"], {"env": {"CARGO_BUILD_JOBS": "12"}})
        self.assertEqual(kwargs["env"]["CARGO_BUILD_JOBS"], "12")

    @unittest.skipUnless(os.name == "nt", "priority classes are a Windows creation flag")
    def test_priority_is_below_normal_and_keeps_other_flags(self):
        _, kwargs = self.apply(["cargo", "build"], {"creationflags": 0x08000000})
        self.assertEqual(kwargs["creationflags"], 0x08000000 | cb.BELOW_NORMAL_PRIORITY_CLASS)

    def test_the_callers_kwargs_are_not_mutated(self):
        original = {"env": {"A": "1"}}
        self.apply(["cargo", "build"], original)
        self.assertEqual(original, {"env": {"A": "1"}})


class ClaimHeld(unittest.TestCase):
    NOW = datetime(2026, 11, 10, 12, 0, 0)

    def held(self, text, now=None):
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "stack-claim.txt"
            if text is not None:
                path.write_text(text, encoding="utf-8")
            return cb.claim_held(path, now or self.NOW)

    def test_fresh_claim_is_held(self):
        self.assertTrue(self.held("w3 2026-11-10T11:00:00 booting"))

    def test_expired_claim_is_not_held(self):
        self.assertFalse(self.held("w3 2026-11-10T06:00:00 booting"))

    def test_absent_or_empty_claim_is_not_held(self):
        self.assertFalse(self.held(None))
        self.assertFalse(self.held(""))

    def test_the_word_stale_in_a_fresh_purpose_does_not_matter(self):
        self.assertTrue(self.held("w3 2026-11-10T11:00:00 investigate stale snapshots"))
        self.assertFalse(self.held("w3 2026-11-10T01:00:00 investigate stale snapshots"))

    def test_a_hand_written_non_claim_line_counts_as_held(self):
        self.assertTrue(self.held("working on it"))


class Help(unittest.TestCase):
    def test_help_exits_zero(self):
        for flag in ("--help", "-h"):
            self.assertEqual(cb.main([flag]), 0)

    def test_help_runs_as_a_script(self):
        out = subprocess.run([sys.executable, str(Path(cb.__file__)), "--help"], capture_output=True, text=True,
                             timeout=60, stdin=subprocess.DEVNULL)
        self.assertEqual(out.returncode, 0, out.stderr)


@unittest.skipUnless(os.name == "nt", "reads the process's priority class")
class Real(unittest.TestCase):
    def test_a_child_really_runs_below_normal_and_sees_the_job_cap(self):
        import ctypes
        code = ("import ctypes,os;k=ctypes.windll.kernel32;k.GetCurrentProcess.restype=ctypes.c_void_p;"
                "k.GetPriorityClass.argtypes=[ctypes.c_void_p];"
                "print(hex(k.GetPriorityClass(k.GetCurrentProcess())), os.environ.get('CARGO_BUILD_JOBS'))")
        env = {**os.environ, "SIMSONLINE_CPU_JOBS": "3"}
        env.pop("CARGO_BUILD_JOBS", None)
        argv, kwargs = cb.apply([sys.executable, "-c", code], {"env": env}, environ=env, held=True)
        out = subprocess.run(argv, capture_output=True, text=True, timeout=60, stdin=subprocess.DEVNULL, **kwargs)
        self.assertEqual(out.stdout.split(), [hex(cb.BELOW_NORMAL_PRIORITY_CLASS), "3"], out.stderr)
        self.assertTrue(ctypes)


if __name__ == "__main__":
    unittest.main()
