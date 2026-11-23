"""Unit tests for build_slot.py:
python -m unittest scripts.build_slot_test"""
from __future__ import annotations

import json
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import arms_lib  # noqa: E402
import build_slot  # noqa: E402


def write(path: Path, data: str) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data.encode())
    return path


def fake_checkout(root: Path, tag: str) -> Path:
    """A checkout's built parts, as checkout_parts finds them; tag makes each build's files its own."""
    write(root / "Directory.Build.props", "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework>"
                                          "</PropertyGroup></Project>")
    write(root / "SimsOnlineServerHostMod/bin/Release" / build_slot.MOD, f"mod {tag}")
    write(root / "SimLink/build/Release" / build_slot.SIMLINK, f"simlink {tag}")
    for part, (project, exe) in build_slot.CONTROLLERS.items():
        write(root / project / "bin/Debug/net8.0" / (exe + ".exe"), f"{part} exe {tag}")
        write(root / project / "bin/Debug/net8.0" / (exe + ".dll"), f"{part} dll {tag}")
        write(root / project / "bin/Debug/net8.0/runtimes/win/native.dll", f"{part} native {tag}")
    return root


class Base(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.layout = build_slot.Layout(
            mod_targets=[self.tmp / "profiles" / p / "Mods/Packages" / build_slot.MOD for p in ("server", "box1")],
            simlink=self.tmp / "Game/Bin" / build_slot.SIMLINK,
            pointer=self.tmp / "run/build-slot.json", store=self.tmp / "run/build-slots",
            checkout=self.tmp / "here")
        self.layout.simlink.parent.mkdir(parents=True)

    def make(self, name: str, tag: str | None = None) -> tuple[bool, list[str]]:
        checkout = fake_checkout(self.tmp / f"checkout-{tag or name}", tag or name)
        return build_slot.make(name, build_slot.checkout_parts(checkout), {"kind": "checkout", "commit": tag or name},
                               self.layout)

    def install(self, name: str, up: list[str] | None = None) -> tuple[bool, list[str]]:
        return build_slot.install(name, self.layout, up=lambda: list(up or []))

    def rows(self) -> list[dict]:
        text = (self.layout.store / "slots.jsonl").read_text(encoding="utf-8")
        return [json.loads(line) for line in text.splitlines()]


class MakeInstallCheck(Base):
    def test_a_slot_installs_checks_and_releases(self) -> None:
        ok, lines = self.make("old")
        self.assertTrue(ok, lines)
        manifest = json.loads((self.layout.store / "old/manifest.json").read_text(encoding="utf-8"))
        self.assertEqual(set(manifest["server"]["files"]),
                         {"SimsOnline.DedicatedServer.exe", "SimsOnline.DedicatedServer.dll", "runtimes/win/native.dll"})
        self.assertIn(manifest["digest"], lines[0])

        ok, lines = self.install("old")
        self.assertTrue(ok, lines)
        for target in self.layout.mod_targets:
            self.assertEqual(target.read_text(), "mod old")
        self.assertEqual(self.layout.simlink.read_text(), "simlink old")
        pointer = json.loads(self.layout.pointer.read_text(encoding="utf-8"))
        self.assertEqual(pointer["slot"], "old")
        self.assertEqual(Path(pointer["server"]), self.layout.store / "old/server/SimsOnline.DedicatedServer.exe")
        self.assertEqual(Path(pointer["client"]), self.layout.store / "old/client/SimsOnline.Client.exe")
        self.assertEqual(arms_lib.hook_values(lines[-1]), {"hashOk": 1, "slot_old": 1})

        ok, found, lines = build_slot.check(None, self.layout)
        self.assertEqual((ok, found), (True, "old"))
        ok, lines = build_slot.release(self.layout)
        self.assertFalse(self.layout.pointer.exists())
        ok, found, lines = build_slot.check("old", self.layout)
        self.assertFalse(ok)
        self.assertEqual(arms_lib.hook_values("\n".join(lines)), {"hashOk": 0, "slot_old": 0})
        self.assertIn("no slot pointer", "\n".join(lines))
        self.assertEqual([r["verb"] for r in self.rows()], ["make", "install", "release"])

    def test_the_same_build_has_the_same_digest(self) -> None:
        self.make("a", "x")
        self.make("b", "x")
        self.make("c", "y")
        digest = {n: build_slot.load_slot(n, self.layout)["digest"] for n in "abc"}
        self.assertEqual(digest["a"], digest["b"])
        self.assertNotEqual(digest["a"], digest["c"])

    def test_check_names_what_is_not_the_arms_slot(self) -> None:
        self.make("new")
        self.make("old")
        self.assertTrue(self.install("new")[0])
        ok, found, lines = build_slot.check("old", self.layout)
        text = "\n".join(lines)
        self.assertFalse(ok)
        self.assertIn("hashOk=0 slot_old=0", text)
        self.assertIn("another build", text)
        self.assertIn("pointed at slot new", text)
        write(self.layout.mod_targets[1], "a mod built by hand")  # one profile's copy replaced after the install
        ok, found, lines = build_slot.check("new", self.layout)
        self.assertFalse(ok)
        self.assertIn(str(self.layout.mod_targets[1]), "\n".join(lines))
        self.assertEqual(build_slot.check(None, self.layout)[:2], (False, None))

    def test_a_slot_whose_files_changed_is_not_installed(self) -> None:
        self.make("old")
        write(self.layout.store / "old/client/SimsOnline.Client.dll", "changed")
        ok, lines = self.install("old")
        self.assertFalse(ok)
        self.assertIn("not whole", lines[0])
        self.assertFalse(self.layout.pointer.exists())
        self.assertFalse(self.layout.mod_targets[0].exists())

    def test_install_refuses_while_the_stack_runs_or_without_the_slot(self) -> None:
        self.make("old")
        ok, lines = self.install("old", up=["TS3W.exe"])
        self.assertFalse(ok)
        self.assertIn("TS3W.exe running", lines[0])
        self.assertFalse(self.layout.simlink.exists())
        ok, lines = self.install("nope")
        self.assertFalse(ok)
        self.assertIn("no slot nope", lines[0])
        self.layout.simlink.parent.rmdir()  # a wrong game path: refused before any copy
        ok, lines = self.install("old")
        self.assertFalse(ok)
        self.assertIn("nothing installed", lines[0])
        self.assertFalse(self.layout.mod_targets[0].exists())

    def test_make_refuses(self) -> None:
        self.assertFalse(build_slot.make("a-b", {}, {}, self.layout)[0])  # it becomes slot_NAME in a gate
        self.assertTrue(self.make("old")[0])
        ok, lines = self.make("old")
        self.assertFalse(ok)
        self.assertIn("exists", lines[0])
        checkout = fake_checkout(self.tmp / "unbuilt", "u")
        (checkout / "SimLink/build/Release" / build_slot.SIMLINK).unlink()
        (checkout / "SimsOnline.Client/bin/Debug/net8.0/SimsOnline.Client.exe").unlink()
        ok, lines = build_slot.make("u", build_slot.checkout_parts(checkout), {}, self.layout)
        self.assertFalse(ok)
        self.assertIn("simlink", lines[0])
        self.assertIn("SimsOnline.Client.exe", lines[0])
        (self.layout.store / "half.making").mkdir()
        self.assertIn("did not finish", self.make("half")[1][0])
        self.assertEqual(build_slot.slot_names(self.layout), ["old"])


class InstalledParts(Base):
    def test_what_the_stack_runs_now(self) -> None:
        fake_checkout(self.layout.checkout, "here")
        with self.assertRaises(build_slot.SlotError):  # no mod installed yet
            build_slot.installed_parts(self.layout)
        for target in self.layout.mod_targets:
            write(target, "mod today")
        write(self.layout.simlink, "simlink today")
        parts = build_slot.installed_parts(self.layout)
        self.assertEqual(parts["mod"], self.layout.mod_targets[0])
        self.assertEqual(parts["server"], self.layout.checkout / "DedicatedServer/bin/Debug/net8.0")
        ok, lines = build_slot.make("new", parts, {"kind": "installed"}, self.layout)
        self.assertTrue(ok, lines)
        write(self.layout.mod_targets[1], "another mod")
        with self.assertRaises(build_slot.SlotError) as caught:
            build_slot.installed_parts(self.layout)
        self.assertIn("differ", str(caught.exception))

    def test_refused_while_a_slot_is_installed(self) -> None:
        self.make("old")
        self.install("old")
        with self.assertRaises(build_slot.SlotError) as caught:
            build_slot.installed_parts(self.layout)
        self.assertIn("slot old is installed", str(caught.exception))


class AfterHook(Base):
    def test_the_after_hook_checks_the_arms_slot(self) -> None:
        self.make("new")
        self.make("old")
        self.install("old")
        ran = []
        builds = build_slot.parse_builds(["new=new", "old=old"])
        ok, lines = build_slot.after("old:bare", 2, builds, self.layout, ran=lambda arm, repeat: ran.append((arm, repeat)))
        self.assertTrue(ok)
        self.assertEqual(arms_lib.hook_values("\n".join(lines)), {"hashOk": 1, "slot_old": 1})
        ok, lines = build_slot.after("new", 3, builds, self.layout, ran=lambda arm, repeat: ran.append((arm, repeat)))
        self.assertFalse(ok)
        self.assertEqual(arms_lib.hook_values("\n".join(lines)), {"hashOk": 0, "slot_new": 0})
        ok, lines = build_slot.after("third", 1, builds, self.layout, ran=lambda arm, repeat: ran.append((arm, repeat)))
        self.assertEqual((ok, arms_lib.hook_values(lines[0])), (False, {"hashOk": 0}))
        self.assertEqual(ran, [("old:bare", 2), ("new", 3), ("third", 1)])  # saves.py's verdict runs every time
        self.assertEqual([r.get("want") for r in self.rows() if r["verb"] == "after"], ["old", "new", None])

    def test_parse_builds(self) -> None:
        self.assertEqual(build_slot.parse_builds(["a=x", "b=y_2"]), {"a": "x", "b": "y_2"})
        for bad in (["a"], ["=x"], ["a=x-y"], ["a="]):
            with self.assertRaises(SystemExit):
                build_slot.parse_builds(bad)


if __name__ == "__main__":
    unittest.main()
