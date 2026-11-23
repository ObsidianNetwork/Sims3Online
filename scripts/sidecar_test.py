"""Unit tests for sidecar.py's build commands (subprocess replaced: no Visual Studio or CMake needed) and its install,
restore, status and argument handling (a temporary Game\\Bin, never the real one; no process check):
python -m unittest scripts.sidecar_test"""
from __future__ import annotations

import contextlib
import io
import struct
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import sidecar  # noqa: E402


class SidecarTest(unittest.TestCase):
    def setUp(self) -> None:
        self.dir = tempfile.TemporaryDirectory()
        root = Path(self.dir.name)
        self.bin = root / "Bin"
        self.bin.mkdir()
        self.built = root / "SimLink.asi"
        self.games: list[str] = []
        patches = [mock.patch.object(sidecar, "game_bin", return_value=self.bin),
                   mock.patch.object(sidecar, "BUILT", self.built),
                   mock.patch.object(sidecar, "running_games", side_effect=lambda: self.games)]
        for p in patches:
            p.start()
            self.addCleanup(p.stop)

    def tearDown(self) -> None:
        self.dir.cleanup()

    def run_main(self, *args: str) -> str:
        out = io.StringIO()
        with mock.patch.object(sys, "argv", ["sidecar.py", *args]), contextlib.redirect_stdout(out):
            sidecar.main()
        return out.getvalue()

    def asi(self, name: str = "SimLink.asi") -> bytes:
        return (self.bin / name).read_bytes()

    def test_install_keeps_the_build_it_replaces(self) -> None:
        self.built.write_bytes(b"one")
        self.run_main("install")
        self.assertFalse((self.bin / "SimLink.asi.prev").exists())  # nothing was installed before
        self.built.write_bytes(b"two")
        text = self.run_main("install")
        self.assertEqual((self.asi(), self.asi("SimLink.asi.prev")), (b"two", b"one"))
        self.assertIn(f"prev={sidecar.sha(self.bin / 'SimLink.asi.prev')}", text)
        self.run_main("install")  # the same build again: the previous one stays
        self.assertEqual(self.asi("SimLink.asi.prev"), b"one")

    def test_restore_swaps_back(self) -> None:
        self.built.write_bytes(b"one")
        self.run_main("install")
        self.built.write_bytes(b"two")
        self.run_main("install")
        self.run_main("restore")
        self.assertEqual((self.asi(), self.asi("SimLink.asi.prev")), (b"one", b"two"))
        self.assertFalse((self.bin / "SimLink.asi.swap").exists())

    def test_install_and_restore_refuse_while_a_game_runs(self) -> None:
        self.built.write_bytes(b"one")
        self.run_main("install")
        self.built.write_bytes(b"two")
        self.games = ["4242"]
        for action in ("install", "restore"):
            with self.assertRaises(SystemExit) as caught:
                self.run_main(action)
            self.assertIn("TS3W.exe is running (pid 4242)", str(caught.exception))
            self.assertIn("stackctl.py down", str(caught.exception))
        self.assertEqual(self.asi(), b"one")
        self.assertFalse((self.bin / "SimLink.asi.prev").exists())

    def test_status_prints_each_builds_native_version(self) -> None:
        self.built.write_bytes(fake_asi(7))
        self.run_main("install")
        self.built.write_bytes(fake_asi(8))
        text = self.run_main("install")
        self.assertIn(f"installed={sidecar.sha(self.bin / 'SimLink.asi')}(v8)", text)
        self.assertIn(f"prev={sidecar.sha(self.bin / 'SimLink.asi.prev')}(v7)", text)
        self.assertEqual(sidecar.native_version(b"one"), None)
        self.assertEqual(sidecar.native_version(fake_asi(7).replace(b"\xb8\x07", b"\x90\x07")), None)

    def test_help_and_extra_arguments_install_nothing(self) -> None:
        self.built.write_bytes(b"one")
        self.assertIn("Build and switch", self.run_main("install", "--help"))
        with self.assertRaises(SystemExit):
            self.run_main("install", "extra")
        self.assertFalse((self.bin / "SimLink.asi").exists())


def fake_asi(version: int) -> bytes:
    """A one-section PE (image base 0x10000000, the section at RVA 0x1000 from file offset 0) holding the registration
    shape sidecar.native_version reads: the icall name, push fn / push name, and fn's mov eax, version / ret."""
    data = bytearray(0x400)
    pe, base, rva = 0x40, 0x10000000, 0x1000
    struct.pack_into("<I", data, 0x3C, pe)
    struct.pack_into("<H", data, pe + 6, 1)
    struct.pack_into("<H", data, pe + 20, 0xE0)
    struct.pack_into("<I", data, pe + 24 + 28, base)
    struct.pack_into("<8sIIII", data, pe + 24 + 0xE0, b".text", len(data), rva, len(data), 0)
    name, push, fn = 0x300, 0x200, 0x280
    data[name:name + len(sidecar.VERSION_ICALL)] = sidecar.VERSION_ICALL
    data[push:push + 10] = b"\x68" + struct.pack("<I", base + rva + fn) + b"\x68" + struct.pack("<I", base + rva + name)
    data[fn:fn + 6] = b"\xb8" + struct.pack("<i", version) + b"\xc3"
    return bytes(data)


class BuildTests(unittest.TestCase):
    def run_build(self, argv: list[str], cmake: str | None = "cmake") -> list[list[str]]:
        calls: list[list[str]] = []

        def fake_run(command: list[str], **kwargs) -> subprocess.CompletedProcess:
            calls.append(command)
            self.assertNotIn("CMAKE_GENERATOR", kwargs["env"])
            return subprocess.CompletedProcess(command, 0, "", "")

        with mock.patch.object(sidecar, "find_cmake", return_value=cmake), \
                mock.patch.object(sidecar.subprocess, "run", side_effect=fake_run), \
                mock.patch.object(sidecar, "sha", return_value="0" * 16), \
                mock.patch.object(sidecar, "forget_other_generator", return_value=None), \
                mock.patch.dict(sidecar.os.environ, {"CMAKE_GENERATOR": "Ninja"}), \
                mock.patch.object(sidecar.sys, "argv", ["sidecar.py", *argv]):
            self.assertEqual(sidecar.main(), 0)
        return calls

    def test_default_is_visual_studio_2022_x86_release(self) -> None:
        source, build = str(sidecar.SOURCE), str(sidecar.SOURCE / "build")
        self.assertEqual(self.run_build(["build"]), [
            ["cmake", "-S", source, "-B", build, "-G", "Visual Studio 17 2022", "-A", "Win32"],
            ["cmake", "--build", build, "--config", "Release"],
        ])

    def test_generator_option_changes_only_the_generator(self) -> None:
        default = self.run_build(["build"])
        other = self.run_build(["build", "--generator", "Visual Studio 18 2026"])
        self.assertEqual(other[0][other[0].index("-G") + 1], "Visual Studio 18 2026")
        other[0][other[0].index("-G") + 1] = "Visual Studio 17 2022"
        self.assertEqual(other, default)

    def test_bad_build_options_are_refused(self) -> None:
        # --generator belongs to build only; the other verbs still take no arguments.
        for argv in (["build", "--generator"], ["build", "--config", "Debug"],
                     ["install", "--generator", "Visual Studio 18 2026"]):
            with mock.patch.object(sidecar.sys, "argv", ["sidecar.py", *argv]), \
                    mock.patch.object(sidecar, "game_bin", side_effect=AssertionError("reached Game\\Bin")), \
                    mock.patch.object(sidecar.subprocess, "run") as run:
                with self.assertRaises(SystemExit) as caught:
                    sidecar.main()
                self.assertTrue(str(caught.exception).startswith("usage: sidecar.py build"), str(caught.exception))
                run.assert_not_called()

    def test_another_generator_drops_only_the_old_configuration(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp)
            built = folder / "Release" / "SimLink.asi"
            built.parent.mkdir()
            built.write_bytes(b"asi")

            def configure(generator: str) -> None:
                (folder / "CMakeCache.txt").write_text(f"CMAKE_GENERATOR:INTERNAL={generator}\n", encoding="utf-8")
                (folder / "CMakeFiles").mkdir(exist_ok=True)
                (folder / "CMakeFiles" / "rules").write_text("x", encoding="utf-8")

            self.assertIsNone(sidecar.forget_other_generator(folder, "Visual Studio 17 2022"))  # nothing configured
            configure("Visual Studio 17 2022")
            self.assertIsNone(sidecar.forget_other_generator(folder, "Visual Studio 17 2022"))
            self.assertTrue((folder / "CMakeCache.txt").exists() and (folder / "CMakeFiles").exists())
            self.assertEqual(sidecar.forget_other_generator(folder, "Visual Studio 18 2026"), "Visual Studio 17 2022")
            self.assertFalse((folder / "CMakeCache.txt").exists() or (folder / "CMakeFiles").exists())
            self.assertEqual(built.read_bytes(), b"asi")

    def test_missing_cmake_stops_before_running_anything(self) -> None:
        with mock.patch.object(sidecar, "find_cmake", return_value=None), \
                mock.patch.object(sidecar.subprocess, "run") as run:
            with self.assertRaises(SystemExit):
                sidecar.build()
            run.assert_not_called()

    def test_find_cmake_prefers_visual_studio_then_path(self) -> None:
        with mock.patch.object(sidecar.shutil, "which", return_value="/usr/bin/cmake"):
            with mock.patch.object(Path, "is_file", return_value=True):
                self.assertEqual(sidecar.find_cmake(), str(sidecar.CMAKE))
            with mock.patch.object(Path, "is_file", return_value=False):
                self.assertEqual(sidecar.find_cmake(), "/usr/bin/cmake")

    def test_another_generator_prefers_the_cmake_on_path(self) -> None:
        # Visual Studio 2022's bundled CMake may predate a newer Visual Studio's generator.
        other = "Visual Studio 18 2026"
        with mock.patch.object(Path, "is_file", return_value=True):
            with mock.patch.object(sidecar.shutil, "which", return_value="/usr/bin/cmake"):
                self.assertEqual(sidecar.find_cmake(other), "/usr/bin/cmake")
            with mock.patch.object(sidecar.shutil, "which", return_value=None):
                self.assertEqual(sidecar.find_cmake(other), str(sidecar.CMAKE))


if __name__ == "__main__":
    unittest.main()
