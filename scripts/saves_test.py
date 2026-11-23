"""Unit tests for saves.py (an A/B on the server's boot save): python -m unittest scripts.saves_test"""
from __future__ import annotations

import contextlib
import io
import json
import os
import shutil
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import saves  # noqa: E402
import stack_claim  # noqa: E402

BOOT = saves.BOOT_SAVE


def write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def quiet(call, *args, **kwargs) -> tuple[int, str]:
    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        code = call(*args, **kwargs)
    return code, out.getvalue()


class Base(unittest.TestCase):
    """Temp folders only: a fake server and client Saves, a store, and a source save."""

    def setUp(self) -> None:
        guard = mock.patch.object(saves, "live_saves", side_effect=AssertionError("a test reached the real Saves"))
        guard.start()
        self.addCleanup(guard.stop)
        self.tmp = Path(tempfile.mkdtemp(prefix="saves-test-"))
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.live = {"server-Saves": self.tmp / "server" / "Saves", "client-Saves": self.tmp / "client" / "Saves"}
        write(self.live["server-Saves"] / BOOT / "1a5tf1l3.dat", "base world")
        write(self.live["server-Saves"] / BOOT / "Meta.data", "base meta")
        write(self.live["server-Saves"] / f"{BOOT}.backup" / "1a5tf1l3.dat", "older world")
        write(self.live["server-Saves"] / "Sunset Valley.sims3" / "x.nhd", "stock")
        write(self.live["client-Saves"] / BOOT / "1a5tf1l3.dat", "client copy")
        self.store = self.tmp / "store"
        self.source = self.tmp / "day0" / "20261017-150626"
        write(self.source / "1a5tf1l3.dat", "day0 world")
        write(self.source / "Meta.data", "day0 meta")
        write(self.source / saves.STOCK_MARKER, "copied from Sunset Valley.sims3")

    def boot_save(self) -> dict[str, str]:
        root = self.live["server-Saves"] / BOOT
        return {p.name: p.read_text(encoding="utf-8") for p in sorted(root.iterdir())}

    def backed_up(self, name: str = "base") -> None:
        code, _ = quiet(saves.backup, name, self.live, self.store)
        self.assertEqual(code, 0)


class Backup(Base):
    def test_backup_copies_both_trees_with_hashes_and_verifies(self) -> None:
        code, out = quiet(saves.backup, "base", self.live, self.store)
        self.assertEqual(code, 0)
        self.assertIn("identical", out)
        table = saves.read_hashes(self.store / "base" / "hashes.txt")
        self.assertIn(f"server-Saves/{BOOT}/1a5tf1l3.dat", table)
        self.assertIn(f"client-Saves/{BOOT}/1a5tf1l3.dat", table)
        self.assertEqual(len(table), 5)
        self.assertEqual(quiet(saves.verify, "base", self.live, self.store)[0], 0)

    def test_a_backup_never_overwrites_one(self) -> None:
        self.backed_up()
        code, out = quiet(saves.backup, "base", self.live, self.store)
        self.assertEqual(code, 1)
        self.assertIn("pick another name", out)

    def test_verify_names_a_changed_file(self) -> None:
        self.backed_up()
        write(self.live["server-Saves"] / BOOT / "1a5tf1l3.dat", "checkpointed since")
        code, out = quiet(saves.verify, "base", self.live, self.store)
        self.assertEqual(code, 1)
        self.assertIn(f"differs server-Saves/{BOOT}/1a5tf1l3.dat", out)


class Swap(Base):
    def test_swap_puts_the_source_in_without_the_stock_marker(self) -> None:
        code, _ = quiet(saves.swap, self.source, self.live, self.store, games_up=lambda: False)
        self.assertEqual(code, 0)
        self.assertEqual(self.boot_save(), {"1a5tf1l3.dat": "day0 world", "Meta.data": "day0 meta"})
        self.assertFalse(list((self.live["server-Saves"]).glob("*.saves-*")))
        self.assertEqual(saves.read_rows(self.store)[-1]["verb"], "swap")

    def test_swap_refuses_while_a_game_runs(self) -> None:
        code, out = quiet(saves.swap, self.source, self.live, self.store, games_up=lambda: True)
        self.assertEqual(code, 1)
        self.assertIn("stop both games first", out)
        self.assertEqual(self.boot_save()["1a5tf1l3.dat"], "base world")

    def test_a_bad_staged_copy_leaves_the_target_untouched(self) -> None:
        real = saves.hashes

        def lying(root: Path, skip: frozenset[str] = frozenset()) -> dict[str, str]:
            table = real(root, skip)
            return {k: "0" * 64 for k in table} if root.name.endswith(".saves-staging") else table

        with mock.patch.object(saves, "hashes", lying):
            code, out = quiet(saves.swap, self.source, self.live, self.store, games_up=lambda: False)
        self.assertEqual(code, 1)
        self.assertIn("is untouched", out)
        self.assertEqual(self.boot_save()["1a5tf1l3.dat"], "base world")
        self.assertFalse(list(self.live["server-Saves"].glob("*.saves-*")))

    def test_a_held_target_stays_as_it_was(self) -> None:
        real = Path.rename

        def held(path: Path, target):
            if path.name == BOOT:
                raise PermissionError("in use")
            return real(path, target)

        with mock.patch.object(Path, "rename", held):
            code, out = quiet(saves.swap, self.source, self.live, self.store, games_up=lambda: False)
        self.assertEqual(code, 1)
        self.assertIn("cannot be moved aside", out)
        self.assertEqual(self.boot_save()["1a5tf1l3.dat"], "base world")
        self.assertFalse(list(self.live["server-Saves"].glob("*.saves-*")))

    def test_a_folder_left_by_a_dead_step_is_kept_and_refused(self) -> None:
        write(self.live["server-Saves"] / f"{BOOT}.saves-aside" / "1a5tf1l3.dat", "the only copy")
        code, out = quiet(saves.swap, self.source, self.live, self.store, games_up=lambda: False)
        self.assertEqual(code, 1)
        self.assertIn("did not finish", out)
        self.assertTrue((self.live["server-Saves"] / f"{BOOT}.saves-aside" / "1a5tf1l3.dat").exists())

    def test_an_empty_source_is_refused(self) -> None:
        (self.tmp / "empty").mkdir()
        code, out = quiet(saves.swap, self.tmp / "empty", self.live, self.store, games_up=lambda: False)
        self.assertEqual(code, 1)
        self.assertIn("has no files", out)


class Restore(Base):
    def test_restore_brings_both_trees_back(self) -> None:
        self.backed_up()
        quiet(saves.swap, self.source, self.live, self.store, games_up=lambda: False)
        write(self.live["client-Saves"] / BOOT / "1a5tf1l3.dat", "client fetched the old world")
        write(self.live["client-Saves"] / "new.sims3" / "x", "made since")
        code, out = quiet(saves.restore, "base", self.live, self.store, games_up=lambda: False)
        self.assertEqual(code, 0)
        self.assertIn("identical", out)
        self.assertEqual(self.boot_save()["1a5tf1l3.dat"], "base world")
        self.assertFalse((self.live["client-Saves"] / "new.sims3").exists())
        self.assertEqual(quiet(saves.verify, "base", self.live, self.store)[0], 0)

    def test_restore_refuses_while_a_game_runs_or_without_a_backup(self) -> None:
        self.backed_up()
        self.assertEqual(quiet(saves.restore, "base", self.live, self.store, games_up=lambda: True)[0], 1)
        code, out = quiet(saves.restore, "nope", self.live, self.store, games_up=lambda: False)
        self.assertEqual(code, 1)
        self.assertIn("no backup", out)


class Stack:
    """Fake stack calls: down, the process list, the boot, the server state file."""

    def __init__(self, boot_ok: bool = True, still_up: bool = False, new_game: bool = True) -> None:
        self.calls: list[str] = []
        self.pid = 100
        self.boot_ok, self.still_up, self.new_game = boot_ok, still_up, new_game

    def down(self) -> str:
        self.calls.append("down")
        return '{"stopped": 2}'

    def games_up(self) -> bool:
        return self.still_up

    def boot(self, words: list[str]) -> tuple[bool, str]:
        self.calls.append("boot")
        self.words = list(words)
        if self.boot_ok and self.new_game:
            self.pid += 1
        return self.boot_ok, "booted" if self.boot_ok else "server never reached the world"

    def game(self) -> tuple:
        return self.pid, f"2026-10-18T12:{self.pid - 100:02d}:00Z"


class ArmHook(Base):
    def setUp(self) -> None:
        super().setUp()
        self.backed_up()
        self.arms = self.tmp / "arms" / "20261018-230000"
        self.arms.mkdir(parents=True)

    def before(self, arm: str, stack: Stack, folder: Path | None = None, base: str = "base") -> tuple[int, str]:
        return quiet(saves.arm_before, arm, 1, base, {"old": self.source}, self.live, self.store, down=stack.down,
                     boot=stack.boot, games_up=stack.games_up, game=stack.game, arms_folder=lambda: folder)

    def test_an_arm_in_use_boots_on_its_source(self) -> None:
        stack = Stack()
        code, out = self.before("old", stack)
        self.assertEqual(code, 0)
        self.assertEqual(stack.calls, ["down", "boot"])
        self.assertEqual(self.boot_save(), {"1a5tf1l3.dat": "day0 world", "Meta.data": "day0 meta"})
        row = saves.read_rows(self.store)[-1]
        self.assertEqual((row["verb"], row["want"], row["action"], row["game"][0]), ("boot", "old", "booted", 101))
        self.assertIn("booted on old", out)

    def test_a_refused_down_is_passed_on_and_no_game_is_touched(self) -> None:
        """Item 11 (Main, 2026-10-22): under another session's claim the down refuses; the hook says so in its words."""
        refusal = stack_claim.down_refusal("30a1c8d1 since 06:16")
        self.assertTrue(refusal.startswith("stackctl down refused"))  # what arm_before looks for
        stack = Stack()
        stack.down = lambda: stack.calls.append("down") or refusal
        code, out = self.before("old", stack)
        self.assertEqual(code, 1)
        self.assertEqual(stack.calls, ["down"])  # no second down, no boot
        self.assertIn(f"saves: old #1 wants old: {refusal}; no game touched", out)
        row = saves.read_rows(self.store)[-1]
        self.assertEqual((row["action"], row["why"], row["down"]), ("refused", refusal, refusal))

    def test_the_arms_boot_env_reaches_its_boot(self) -> None:
        stack = Stack()
        code, out = quiet(saves.arm_before, "old", 1, "base", {"old": self.source}, self.live, self.store,
                          down=stack.down, boot=stack.boot, games_up=stack.games_up, game=stack.game,
                          arms_folder=lambda: None, env=saves.boot_env(["SIMLINK_GCGATE=1"], []))
        self.assertEqual(code, 0)
        self.assertEqual(stack.words, ["--server-env", "SIMLINK_GCGATE=1"])
        self.assertEqual(saves.read_rows(self.store)[-1]["boot_env"], {"server": {"SIMLINK_GCGATE": "1"}, "client": {}})
        self.assertIn("server TS3 101, with --server-env SIMLINK_GCGATE=1", out)
        self.assertEqual(self.before("old", stack)[0], 0)  # no env: no words, no boot_env in the row
        self.assertEqual(stack.words, [])
        self.assertNotIn("boot_env", saves.read_rows(self.store)[-1])
        for bad in (["NO_VALUE"], ["=1"], ["1A=1"]):
            with self.assertRaises(SystemExit):
                saves.boot_env(bad, [])

    def test_a_build_ab_installs_the_arms_slot_after_the_down_and_boots_sequential(self) -> None:
        stack = Stack()

        def install(slot: str) -> tuple[bool, list[str]]:
            stack.calls.append(f"install {slot}")
            return slot != "broken", [f"install: slot {slot}", "build: hashOk=1"]

        def before(builds: dict[str, str]) -> tuple[int, str]:
            return quiet(saves.arm_before, "old", 1, "base", {"old": self.source}, self.live, self.store,
                         down=stack.down, boot=stack.boot, games_up=stack.games_up, game=stack.game,
                         arms_folder=lambda: None, builds=builds, install=install)

        code, out = before({"old": "old6d41", "new": "new"})
        self.assertEqual(code, 0)
        self.assertEqual(stack.calls, ["down", "install old6d41", "boot"])
        self.assertEqual(stack.words, ["--sequential"])  # an old client has no wait for the server's listener
        self.assertEqual(saves.read_rows(self.store)[-1]["build"]["slot"], "old6d41")
        self.assertIn("build slot old6d41", out)
        stack.calls.clear()
        code, out = before({"new": "new"})  # this arm names no slot: refused, nothing installed or booted
        self.assertEqual((code, stack.calls), (1, ["down"]))
        self.assertIn("no --build for arm old", out)
        stack.calls.clear()
        code, out = before({"old": "broken"})
        self.assertEqual((code, stack.calls), (1, ["down", "install broken", "down"]))  # no boot on a half install
        self.assertIn("build slot broken was not installed", out)

    def test_the_arms_placeholders_reach_the_cli_and_the_boot(self) -> None:
        import scenarios

        argv = scenarios.hook_argv("scripts/saves.py arm {arm} {repeat} --base base {server_env} {client_env}", "gate", 2,
                                   boot_env={"server": {"SIMLINK_GCGATE": "1"}, "client": {"B": "x y"}})
        with mock.patch.object(saves, "arm_before", return_value=0) as before, \
                mock.patch.object(saves, "live_saves", return_value={}):
            self.assertEqual(saves.main(argv[2:]), 0)  # the hook's words after `python scripts/saves.py`
        self.assertEqual(before.call_args.args[:3], ("gate", 2, "base"))
        self.assertEqual(before.call_args.kwargs["env"], {"server": {"SIMLINK_GCGATE": "1"}, "client": {"B": "x y"}})
        done = saves.subprocess.CompletedProcess([], 0, stdout="stack up\nboot times: total 150s\n", stderr="")
        with mock.patch.object(saves.subprocess, "run", return_value=done) as run:
            self.assertEqual(saves.stack_boot(["--server-env", "SIMLINK_GCGATE=1"]), (True, "boot times: total 150s"))
        self.assertEqual(run.call_args.args[0][1:], ["scripts/quick_loop.py", "run", "--boot", "--server-env",
                                                     "SIMLINK_GCGATE=1"])

    def test_every_other_arm_boots_on_the_backups_boot_save_even_after_a_checkpoint(self) -> None:
        quiet(saves.swap, self.source, self.live, self.store, games_up=lambda: False)
        write(self.live["server-Saves"] / BOOT / "1a5tf1l3.dat", "old world, checkpointed at the client's join")
        stack = Stack()
        code, _ = self.before("bare", stack)
        self.assertEqual(code, 0)
        self.assertEqual(self.boot_save(), {"1a5tf1l3.dat": "base world", "Meta.data": "base meta"})
        self.assertEqual(saves.read_rows(self.store)[-1]["want"], "base")

    def test_the_same_arm_twice_boots_twice_on_the_untouched_save(self) -> None:
        stack = Stack()
        self.before("old", stack)
        write(self.live["server-Saves"] / BOOT / "1a5tf1l3.dat", "checkpointed")
        self.before("old", stack)
        self.assertEqual(stack.calls, ["down", "boot", "down", "boot"])
        self.assertEqual(self.boot_save()["1a5tf1l3.dat"], "day0 world")

    def test_a_capped_catcher_arm_counts_as_its_name(self) -> None:
        code, _ = self.before("old:bare", Stack())
        self.assertEqual(code, 0)
        self.assertEqual(self.boot_save()["1a5tf1l3.dat"], "day0 world")

    def test_no_backup_stops_the_games_and_the_run(self) -> None:
        stack = Stack()
        code, out = self.before("bare", stack, folder=self.arms, base="missing")
        self.assertEqual(code, 1)
        self.assertEqual(stack.calls, ["down"])
        self.assertIn("saves.py backup missing first", (self.arms / "stop").read_text(encoding="utf-8"))
        self.assertIn("stops before its next item", out)
        self.assertEqual(saves.read_rows(self.store)[-1]["stopped"], self.arms.name)

    def test_a_game_still_up_after_down_is_refused_before_any_copy(self) -> None:
        stack = Stack(still_up=True)
        code, out = self.before("old", stack)
        self.assertEqual(code, 1)
        self.assertNotIn("boot", stack.calls)
        self.assertEqual(self.boot_save()["1a5tf1l3.dat"], "base world")
        self.assertIn("still runs", out)

    def test_a_failed_boot_leaves_the_games_down(self) -> None:
        stack = Stack(boot_ok=False)
        code, out = self.before("old", stack, folder=self.arms)
        self.assertEqual(code, 1)
        self.assertEqual(stack.calls, ["down", "boot", "down"])
        self.assertIn("the boot failed", out)
        self.assertTrue((self.arms / "stop").exists())

    def test_a_boot_that_names_no_new_game_is_refused(self) -> None:
        code, out = self.before("old", Stack(new_game=False))
        self.assertEqual(code, 1)
        self.assertIn("named no new server game", out)

    def test_after_says_whether_the_arm_ran_on_the_hooks_boot(self) -> None:
        stack = Stack()
        self.before("old", stack)
        code, out = quiet(saves.arm_after, "old", 1, self.store, game=stack.game)
        self.assertEqual(code, 0)
        self.assertIn("on the boot hook's boot (old,", out)
        stack.pid += 1  # a reboot meanwhile
        _, out = quiet(saves.arm_after, "old", 1, self.store, game=stack.game)
        self.assertIn("on another boot than the boot hook's (server TS3 101 then, 102 now)", out)
        _, out = quiet(saves.arm_after, "bare", 2, self.store, game=stack.game)
        self.assertIn("no boot from the boot hook", out)

    def test_after_reads_rows_from_before_the_boot_hook(self) -> None:
        stack = Stack()
        self.before("old", stack)
        rows = saves.read_rows(self.store)
        rows[-1]["verb"] = "before"  # a row the before-hook version wrote
        (self.store / "saves.jsonl").write_text("".join(json.dumps(r) + "\n" for r in rows), encoding="utf-8")
        _, out = quiet(saves.arm_after, "old", 1, self.store, game=stack.game)
        self.assertIn("on the boot hook's boot (old,", out)

    def test_as_a_before_or_after_hook_it_refuses_and_stops_the_run(self) -> None:
        for slot in ("before", "after"):
            with self.subTest(slot=slot), mock.patch.dict(os.environ, {"SIMSONLINE_ARMS_HOOK": slot}):
                stack = Stack()
                code, out = self.before("old", stack, folder=self.arms)
                self.assertEqual(code, 1)
                self.assertEqual(stack.calls, ["down"])  # the arm's run then finds the stack down: no mislabelled run
                self.assertIn(f"run as arms --{slot}: give it as --boot-hook", out)
                self.assertTrue((self.arms / "stop").exists())
                self.assertEqual(self.boot_save()["1a5tf1l3.dat"], "base world")
        with mock.patch.dict(os.environ, {"SIMSONLINE_ARMS_HOOK": "boot"}):
            self.assertEqual(self.before("old", Stack())[0], 0)

    def test_list_shows_the_backups_and_rows(self) -> None:
        self.before("old", Stack())
        code, out = quiet(saves.listing, self.store)
        self.assertEqual(code, 0)
        self.assertIn("1 backup(s)", out)
        self.assertIn("boot old #1: booted", out)


class Runner(unittest.TestCase):
    def test_the_runner_folder_is_the_one_whose_pid_started_the_hook(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            arms = Path(tmp)
            for name, pid in (("20261018-1", 11), ("20261018-2", 22)):
                write(arms / name / "runner.json", json.dumps({"pid": pid, "started": 1.0}))
            self.assertEqual(saves.running_arms(arms, parent=22), arms / "20261018-2")
            self.assertIsNone(saves.running_arms(arms, parent=33))
            self.assertIsNone(saves.running_arms(arms / "absent", parent=22))

    def test_use_wants_arm_equals_folder(self) -> None:
        self.assertEqual(saves.parse_uses(["old=C:/x"]), {"old": Path("C:/x")})
        with self.assertRaises(SystemExit):
            saves.parse_uses(["old"])


if __name__ == "__main__":
    unittest.main()
