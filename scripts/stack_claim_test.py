"""Unit tests for stack_claim: python -m unittest scripts.stack_claim_test"""
from __future__ import annotations

import contextlib
import io
import os
import sys
import tempfile
import unittest
from datetime import datetime, timedelta
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import stack_claim as sc  # noqa: E402

NOW = datetime(2026, 10, 18, 12, 0, 0)

# Every variable stack_claim reads to name the calling session, so the suite passes whichever session runs it.
SESSION_ENV = (
    "OMO_SIMSONLINE_SESSION", "SIMSONLINE_SESSION", "SIMSONLINE_LEASE_HOLDER", "PI_SESSION_ID",
    "PI_SESSION_CWD", "PI_SESSION_FILE", "CLAUDE_CODE_SESSION_ID", "ANTIGRAVITY_CONVERSATION_ID", sc.QUIET_OK)
_env_patch = mock.patch.dict(os.environ)


def setUpModule() -> None:
    _env_patch.start()
    for name in SESSION_ENV:
        os.environ.pop(name, None)


def tearDownModule() -> None:
    _env_patch.stop()


class ClaimTest(unittest.TestCase):
    def write(self, text: str) -> Path:
        d = tempfile.mkdtemp()
        path = Path(d) / "stack-claim.txt"
        path.write_text(text, encoding="utf-8")
        return path

    def test_a_hand_written_line_and_a_quoted_session_both_parse(self) -> None:
        self.assertEqual(sc.parse("B 2026-10-18T11:00:00 probe boot"), ("B", datetime(2026, 10, 18, 11), "probe boot"))
        line = sc.format_line("RE", datetime(2026, 10, 18, 11), "breakpoint on 004fa800")
        self.assertEqual(sc.parse(line), ("RE", datetime(2026, 10, 18, 11), "breakpoint on 004fa800"))
        self.assertEqual(sc.parse('"Session C: " 2026-10-18T11:00 RE')[0], "Session C: ")
        self.assertIsNone(sc.parse("not a claim"))

    def test_a_line_that_does_not_parse_still_warns(self) -> None:
        path = self.write("Session C is debugging the server game\n")
        self.assertIn("Session C is debugging", sc.warning("B", path, NOW) or "")

    def test_only_another_session_s_claim_warns(self) -> None:
        path = self.write("A 2026-10-18T11:30:00 loop exit suite\n")
        self.assertIsNone(sc.warning("A", path, NOW))
        self.assertIn("claimed by A since 11:30 (0.5 h)", sc.warning("B", path, NOW) or "")
        self.assertIn("claimed by A", sc.warning(None, path, NOW) or "")
        self.assertIsNone(sc.warning("B", path.with_name("missing.txt"), NOW))

    def test_an_old_claim_is_called_stale(self) -> None:
        path = self.write("A 2026-10-18T06:00:00 overnight\n")
        self.assertIn("stale", sc.warning("B", path, NOW) or "")


    def test_holder_names_another_session_only(self) -> None:
        path = self.write("D 2026-10-18T11:30:00 gc gate series\n")
        self.assertEqual(sc.holder("B", path, NOW), "D since 11:30 (gc gate series)")
        self.assertEqual(sc.holder(None, path, NOW), "D since 11:30 (gc gate series)")
        self.assertIsNone(sc.holder("D", path, NOW))
        self.assertIsNone(sc.holder("B", path.with_name("missing.txt"), NOW))
        self.assertEqual(sc.holder("B", self.write("A 2026-10-18T06:00:00\n"), NOW), "A since 06:00, stale (over 4 h old)")
        self.assertEqual(sc.holder("B", self.write("Session C is debugging\n"), NOW),
                         "someone (the claim file holds 'Session C is debugging')")

    def test_holder_names_a_quiet_window_and_the_line_behind_it(self) -> None:
        """Tooling review 2026-10-21, section 6: the brief showed [quiet] only inside the purpose, the line only in
        stack_claim.py status."""
        path = self.write("D 2026-10-18T11:30:00 [quiet] arms crash-rate series\n")
        self.assertEqual(sc.holder("B", path, NOW), "D since 11:30 (arms crash-rate series), a quiet window: no builds, "
                                                    "csc or probe compiles from other sessions")
        sc.write_queue([("E", datetime(2026, 10, 18, 11, 40), "seat fix"), ("B", datetime(2026, 10, 18, 11, 45), "")],
                       sc.queue_path(path))
        self.assertTrue((sc.holder("B", path, NOW) or "").endswith(
            "; 2 in line: E (since 11:40), B (this session, since 11:45)"), sc.holder("B", path, NOW))
        self.assertIsNone(sc.waiting("B", path))
        self.assertEqual(sc.waiting("D", path), "2 in line behind it: E (since 11:40), B (since 11:45)")
        sc.write_queue([], sc.queue_path(path))
        self.assertIsNone(sc.waiting("D", path))

    def test_an_ended_sessions_claim_stays_and_says_so(self) -> None:
        path = self.write("D 2026-10-18T11:30:00 [quiet] arms series\n")
        self.assertFalse(sc.mark_ended("B", "clear", NOW, path))  # not B's claim: untouched
        self.assertIsNone(sc.ended(path))
        self.assertTrue(sc.mark_ended("D", "prompt_input_exit", NOW, path))
        self.assertEqual(sc.current(path), ("D", datetime(2026, 10, 18, 11, 30), "[quiet] arms series"))
        self.assertEqual(sc.ended(path), (NOW, "prompt_input_exit"))
        self.assertIn(", its session ended at 12:00 (prompt_input_exit): `stack_claim.py release --force` once its "
                      "games are idle", sc.holder("B", path, NOW) or "")
        self.assertIn("its session ended at 12:00", sc.quiet_refusal("B", path, NOW, environ={}) or "")


class TakeWordsTest(unittest.TestCase):
    """2026-10-21: `take "crumple fix: ..."` claimed the stack for a session named "crumple fix: ..."."""

    def test_the_first_word_is_the_session(self) -> None:
        self.assertEqual(sc.take_words(["f7f5a276", "seated", "socials"], None), ("f7f5a276", "seated socials"))
        self.assertEqual(sc.take_words(["D"], None), ("D", ""))

    def test_a_purpose_without_its_session_is_refused(self) -> None:
        refused = sc.take_words(["crumple fix: the copy's own Idle"], None)
        self.assertIsInstance(refused, str)
        self.assertIn("looks like a purpose", str(refused))
        self.assertIsInstance(sc.take_words([], None), str)
        self.assertIsInstance(sc.take_words(["2026-10-21T04:10:10", "boot"], None), str)

    def test_the_env_session_makes_every_word_the_purpose(self) -> None:
        self.assertEqual(sc.take_words(["crumple fix: x"], "f7f5a276"), ("f7f5a276", "crumple fix: x"))
        self.assertEqual(sc.take_words(["f7f5a276", "crumple", "fix"], "f7f5a276"), ("f7f5a276", "crumple fix"))
        self.assertEqual(sc.take_words([], "f7f5a276"), ("f7f5a276", ""))

    def test_another_sessions_id_first_is_refused(self) -> None:
        refused = sc.take_words(["f7f5a276", "seated", "socials"], "19c050d2")  # the docstring's example id copied
        self.assertIsInstance(refused, str)
        self.assertIn("SIMSONLINE_SESSION=f7f5a276", str(refused))
        self.assertEqual(sc.take_words(["deadline", "fix"], "19c050d2"), ("19c050d2", "deadline fix"))  # not 8 hex


class OwnSessionTest(unittest.TestCase):
    """2026-10-21: a rejoin subprocess under Session E's own claim warned "claimed by 19c050d2 ... shares their games"
    until E set SIMSONLINE_SESSION by hand; every command a Claude Code session runs has CLAUDE_CODE_SESSION_ID."""

    def test_the_env_then_the_claude_session(self) -> None:
        self.assertEqual(sc.own_session({"CLAUDE_CODE_SESSION_ID": "19c050d2-46f3-42b4-a6b0-38079f9d4912"}), "19c050d2")
        self.assertEqual(sc.own_session({"ANTIGRAVITY_CONVERSATION_ID": "52d4a65b-0011-48b1-9c85-daa2508a0362"}), "52d4a65b")
        self.assertEqual(sc.own_session({"SIMSONLINE_SESSION": "D", "CLAUDE_CODE_SESSION_ID": "19c050d2-46f3-42b4-a6b0-38079f9d4912"}), "D")
        self.assertEqual(sc.own_session({"SIMSONLINE_SESSION": "", "CLAUDE_CODE_SESSION_ID": "19c050d2-46f3-42b4-a6b0-38079f9d4912"}), "19c050d2")
        self.assertEqual(sc.own_session({"SIMSONLINE_SESSION": "", "CLAUDE_CODE_SESSION_ID": "", "ANTIGRAVITY_CONVERSATION_ID": "52d4a65b-0011"}), "52d4a65b")
        self.assertIsNone(sc.own_session({}))

    def test_omo_sessions(self) -> None:
        """OmO ids are UUIDv7 (time first): two sessions a minute apart share the first 8, so the claim id is the last 8."""
        pi = "01a115b5-84d1-7af1-b847-1bc8b8cdc6d7"
        self.assertEqual(sc.own_session({"PI_SESSION_ID": pi}), "b8cdc6d7")
        self.assertEqual(sc.own_session({"PI_SESSION_ID": pi, "ANTIGRAVITY_CONVERSATION_ID": "52d4a65b-0011"}), "b8cdc6d7")
        self.assertEqual(sc.own_session({"OMO_SIMSONLINE_SESSION": "lane-a", "PI_SESSION_ID": pi}), "lane-a")
        self.assertEqual(sc.own_session({"PI_SESSION_ID": "short"}), None)
        both = {"PI_SESSION_ID": pi, "CLAUDE_CODE_SESSION_ID": "19c050d2-46f3-42b4-a6b0-38079f9d4912"}
        self.assertIsNone(sc.own_session(both))
        self.assertIn("SIMSONLINE_SESSION", sc.session_conflict(both) or "")
        self.assertEqual(sc.own_session({**both, "SIMSONLINE_SESSION": "D"}), "D")
        self.assertIsNone(sc.session_conflict({**both, "OMO_SIMSONLINE_SESSION": "D"}))
        with mock.patch.dict(os.environ, {**both, "SIMSONLINE_SESSION": "", "OMO_SIMSONLINE_SESSION": ""}), \
                mock.patch("sys.stdout", new_callable=io.StringIO) as out:
            self.assertEqual(sc.main(["take", "seated", "socials"]), 2)  # not a claim for a session named "seated"
        self.assertIn("both PI_SESSION_ID", out.getvalue())

    def test_a_subprocess_under_its_own_sessions_claim_does_not_warn(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "stack-claim.txt"
            path.write_text("19c050d2 2026-10-21T04:47:00 Session E two-player scenarios\n", encoding="utf-8")
            with mock.patch.dict(os.environ, {"SIMSONLINE_SESSION": "", "CLAUDE_CODE_SESSION_ID": "19c050d2-46f3-42b4-a6b0-38079f9d4912", "ANTIGRAVITY_CONVERSATION_ID": ""}):
                self.assertIsNone(sc.warning(sc.own_session(), path))
            with mock.patch.dict(os.environ, {"SIMSONLINE_SESSION": "", "CLAUDE_CODE_SESSION_ID": "caa546bd-97c7", "ANTIGRAVITY_CONVERSATION_ID": ""}):
                self.assertIn("shares their games", sc.warning(sc.own_session(), path) or "")


class MainTest(unittest.TestCase):
    def run_main(self, argv: list[str], session: str = "", existing: str | None = None,
                 claude: str = "", antigravity: str = "") -> tuple[int, str, str]:
        d = tempfile.mkdtemp()
        path = Path(d) / "stack-claim.txt"
        if existing is not None:
            path.write_text(existing, encoding="utf-8")
        out = io.StringIO()
        with mock.patch.object(sc, "CLAIM", path), mock.patch.object(sc, "read", lambda: sc.parse(path.read_text(encoding="utf-8"))
                                                                       if path.exists() else None), \
                mock.patch.dict(os.environ, {"SIMSONLINE_SESSION": session, "CLAUDE_CODE_SESSION_ID": claude, "ANTIGRAVITY_CONVERSATION_ID": antigravity}), \
                contextlib.redirect_stdout(out):
            code = sc.main(argv)
        return code, out.getvalue(), path.read_text(encoding="utf-8") if path.exists() else ""

    def test_a_refused_take_writes_no_claim(self) -> None:
        code, out, written = self.run_main(["take", "crumple fix: the copy's own Idle"])
        self.assertEqual((code, written), (2, ""))
        self.assertIn("looks like a purpose", out)

    def test_take_and_release_with_the_env_session(self) -> None:
        code, out, written = self.run_main(["take", "crumple", "fix"], session="f7f5a276")
        self.assertEqual(code, 0)
        self.assertEqual(sc.parse(written)[0::2], ("f7f5a276", "crumple fix"))
        code, out, written = self.run_main(["release"], session="f7f5a276", existing=written)
        self.assertEqual((code, written), (0, ""))

    def test_take_and_release_in_a_claude_session_need_no_id(self) -> None:
        code, out, written = self.run_main(["take", "crumple fix: the copy's own Idle"], claude="19c050d2-46f3-42b4-a6b0-38079f9d4912")
        self.assertEqual((code, sc.parse(written)[0::2]), (0, ("19c050d2", "crumple fix: the copy's own Idle")))
        code, out, written = self.run_main(["release"], claude="19c050d2-46f3-42b4-a6b0-38079f9d4912", existing=written)
        self.assertEqual((code, written), (0, ""))

    def test_take_and_release_in_an_antigravity_session_need_no_id(self) -> None:
        code, out, written = self.run_main(["take", "decomp sync: the copy's own Idle"], antigravity="52d4a65b-0011-48b1-9c85-daa2508a0362")
        self.assertEqual((code, sc.parse(written)[0::2]), (0, ("52d4a65b", "decomp sync: the copy's own Idle")))
        code, out, written = self.run_main(["release"], antigravity="52d4a65b-0011-48b1-9c85-daa2508a0362", existing=written)
        self.assertEqual((code, written), (0, ""))

    def test_release_needs_a_session(self) -> None:
        code, out, written = self.run_main(["release"], existing="A 2026-10-18T11:30:00 x\n")
        self.assertEqual(code, 2)
        self.assertTrue(written)  # still claimed


class LineAndQuietTest(unittest.TestCase):
    """Tooling review 2026-10-21 #6: stack and build slots were brokered by message through Main."""

    def setUp(self) -> None:
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.claim = Path(folder.name) / "stack-claim.txt"
        for patch in (mock.patch.object(sc, "CLAIM", self.claim), mock.patch.object(sc, "read", lambda: sc.current()),
                      mock.patch.dict(os.environ, {"SIMSONLINE_SESSION": "", "CLAUDE_CODE_SESSION_ID": "", "ANTIGRAVITY_CONVERSATION_ID": "",
                                                   sc.QUIET_OK: ""})):
            patch.start()
            self.addCleanup(patch.stop)

    def main(self, argv: list[str], session: str) -> tuple[int, str]:
        out = io.StringIO()
        with mock.patch.dict(os.environ, {"SIMSONLINE_SESSION": session}), contextlib.redirect_stdout(out):
            code = sc.main(argv)
        return code, out.getvalue()

    def line(self) -> list[str]:
        return [entry[0] for entry in sc.read_queue()]

    def test_the_line_keeps_its_order_through_a_release(self) -> None:
        self.assertEqual(self.main(["take", "arms", "series"], "aaaaaaaa")[0], 0)
        code, out = self.main(["queue", "boot", "fix"], "bbbbbbbb")
        self.assertEqual(code, 0)
        self.assertIn("bbbbbbbb is 1 of 1 in line; the live stack is claimed by aaaaaaaa", out)
        self.assertIn("2 of 2 in line", self.main(["queue", "dotnet", "tests"], "cccccccc")[1])
        self.assertIn("1 of 2 in line", self.main(["queue"], "bbbbbbbb")[1])  # queueing again keeps the place
        code, out = self.main(["release"], "aaaaaaaa")
        self.assertIn("released; next in line: bbbbbbbb (in line since", out)
        code, out = self.main(["take"], "cccccccc")
        self.assertEqual(code, 3)
        self.assertIn("bbbbbbbb (in line since", out)
        self.assertIsNone(sc.current())
        self.assertEqual(self.main(["take"], "bbbbbbbb")[0], 0)
        self.assertEqual(sc.current()[0::2], ("bbbbbbbb", "boot fix"))  # the queued purpose
        self.assertEqual(self.line(), ["cccccccc"])
        code, out = self.main(["status"], "cccccccc")
        self.assertIn("  1 in line: cccccccc", out)

    def test_wait_takes_the_stack_when_its_turn_comes(self) -> None:
        self.main(["take", "arms"], "aaaaaaaa")
        self.main(["queue", "--quiet", "gc", "series"], "bbbbbbbb")
        looks = []

        def sleep(_: float) -> None:
            looks.append(1)
            if len(looks) == 2:
                self.claim.unlink()

        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = sc.wait_turn("bbbbbbbb", sleep=sleep, clock=lambda: 0.0)
        self.assertEqual((code, len(looks)), (0, 2))
        self.assertEqual(sc.current()[0::2], ("bbbbbbbb", "[quiet] gc series"))
        self.assertEqual(self.line(), [])

    def test_wait_gives_up_at_its_timeout_and_needs_a_place(self) -> None:
        self.main(["take", "arms"], "aaaaaaaa")
        self.main(["queue", "boot"], "bbbbbbbb")
        clock = iter(range(0, 10000, 100))
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = sc.wait_turn("bbbbbbbb", timeout=250, sleep=lambda s: None, clock=lambda: float(next(clock)))
        self.assertEqual(code, 3)
        self.assertIn("bbbbbbbb is 1 in line; still waiting after 250 s (wait again)", out.getvalue())
        self.assertEqual(self.main(["wait"], "cccccccc")[0], 2)

    def test_wait_without_a_timeout_keeps_waiting(self) -> None:
        """2026-10-21: wait gave up at 540 s while the holder's series ran on."""
        self.main(["take", "arms"], "aaaaaaaa")
        self.main(["queue", "boot"], "bbbbbbbb")
        clock = iter(range(0, 100000, 100))
        looks = []

        def sleep(_: float) -> None:
            looks.append(1)
            if len(looks) == 30:  # 3,000 s later the holder releases
                self.claim.unlink()

        with contextlib.redirect_stdout(io.StringIO()):
            code = sc.wait_turn("bbbbbbbb", sleep=sleep, clock=lambda: float(next(clock)))
        self.assertEqual((code, len(looks)), (0, 30))

    def test_a_first_in_line_that_does_not_take_its_turn_is_dropped(self) -> None:
        """Q lost 30 minutes behind a waiter whose session had gone (2026-10-21)."""
        self.main(["take", "arms"], "aaaaaaaa")
        self.main(["queue", "boot"], "bbbbbbbb")
        self.main(["queue", "build"], "cccccccc")
        self.main(["release"], "aaaaaaaa")
        self.assertEqual(sc.turn_path().read_text(encoding="utf-8").split()[0], "bbbbbbbb")  # its turn began
        start = sc.parse(sc.turn_path().read_text(encoding="utf-8"))[1]
        self.assertEqual(sc.advance(start + timedelta(minutes=9)), [])
        notes = sc.advance(start + timedelta(minutes=11))
        self.assertEqual(notes, [f"bbbbbbbb was dropped from the stack's line at {start + timedelta(minutes=11):%H:%M}: "
                                 f"its turn came at {start:%H:%M} and it did not take the stack within 10 min (queue "
                                 "again to get back in line)"])
        self.assertEqual(self.line(), ["cccccccc"])
        self.assertEqual(sc.parse(sc.turn_path().read_text(encoding="utf-8"))[0], "cccccccc")  # the next turn starts
        self.assertEqual(sc.recent_drops(start + timedelta(minutes=12)), notes)
        self.assertEqual(sc.recent_drops(start + timedelta(hours=5)), [])
        with contextlib.redirect_stdout(io.StringIO()) as out:
            self.assertEqual(sc.wait_turn("bbbbbbbb", timeout=0, sleep=lambda s: None,
                                          today=lambda: start + timedelta(minutes=12)), 2)
        self.assertIn("not in line (stack_claim.py queue <purpose> first); bbbbbbbb was dropped", out.getvalue())
        self.assertEqual(sc.advance(start + timedelta(minutes=30)), [notes[0].replace("bbbbbbbb", "cccccccc")
                         .replace(f"at {start + timedelta(minutes=11):%H:%M}", f"at {start + timedelta(minutes=30):%H:%M}")
                         .replace(f"came at {start:%H:%M}", f"came at {start + timedelta(minutes=11):%H:%M}")])
        self.assertFalse(sc.turn_path().exists())  # nobody left in line

    def test_queue_drop_leaves_only_its_own_entry_without_force(self) -> None:
        self.main(["queue", "boot"], "bbbbbbbb")
        self.main(["queue", "build"], "cccccccc")
        self.assertEqual(self.main(["queue", "--drop", "bbbbbbbb"], "cccccccc")[0], 3)
        self.assertEqual(self.main(["queue", "--drop"], "cccccccc")[0], 0)
        self.assertEqual(self.main(["queue", "--drop", "bbbbbbbb", "--force"], "cccccccc")[0], 0)
        self.assertFalse(sc.queue_path().exists())

    def test_a_quiet_window_refuses_other_sessions_builds_only(self) -> None:
        self.main(["take", "--quiet", "arms", "series"], "aaaaaaaa")
        self.assertEqual(sc.current()[2], "[quiet] arms series")
        self.assertIsNone(sc.quiet_refusal("aaaaaaaa"))
        refusal = sc.quiet_refusal("bbbbbbbb", now=NOW)
        self.assertTrue(refusal and refusal.startswith("REFUSED: the live stack is claimed by aaaaaaaa"))
        self.assertIsNone(sc.quiet_refusal("bbbbbbbb", environ={sc.QUIET_OK: "1"}))
        self.assertEqual(self.main(["check-quiet"], "bbbbbbbb")[0], 3)
        self.assertEqual(self.main(["check-quiet", "--force"], "bbbbbbbb")[0], 0)
        self.assertEqual(self.main(["check-quiet"], "aaaaaaaa")[0], 0)
        self.main(["take", "arms", "series"], "aaaaaaaa")  # taken again without --quiet: the window ends
        self.assertIsNone(sc.quiet_refusal("bbbbbbbb"))

    def test_the_build_tools_refuse_in_a_quiet_window(self) -> None:
        import bridgectl
        import check_mod_runtime
        self.claim.write_text("aaaaaaaa 2026-10-21T22:00:00 [quiet] arms\n", encoding="utf-8")
        out = io.StringIO()
        with mock.patch.dict(os.environ, {"SIMSONLINE_SESSION": "bbbbbbbb"}), \
                mock.patch.object(check_mod_runtime, "check", side_effect=AssertionError("compiled")), \
                mock.patch.object(sys, "argv", ["check_mod_runtime.py"]), contextlib.redirect_stdout(out):
            self.assertEqual(check_mod_runtime.main(), 3)
        self.assertIn("REFUSED", out.getvalue())
        source = self.claim.with_name("Probe.cs")
        source.write_text("class P {}", encoding="utf-8")
        with mock.patch.dict(os.environ, {"SIMSONLINE_SESSION": "bbbbbbbb"}), \
                mock.patch.object(bridgectl.subprocess, "run", side_effect=AssertionError("compiled")), \
                self.assertRaises(SystemExit) as stopped:
            bridgectl.compile_probe(str(source), "stamp")
        self.assertIn("quiet window", str(stopped.exception.code))
        import argparse

        import dev
        steps: list[str] = []
        with mock.patch.dict(os.environ, {"SIMSONLINE_SESSION": "bbbbbbbb"}), \
                mock.patch.object(dev, "run", side_effect=lambda label, argv: steps.append(label) or True), \
                mock.patch.object(dev, "help_check", return_value=True), \
                mock.patch.object(dev, "module_present", return_value=True), \
                mock.patch.object(dev.shutil, "which", return_value=None), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(dev.test(argparse.Namespace(all=True, dotnet=True, force=False, pytest_args=[])), 1)
            self.assertFalse([s for s in steps if "NET" in s or s.startswith("build ")], steps)
            dev.test(argparse.Namespace(all=True, dotnet=True, force=True, pytest_args=[]))
        self.assertIn(".NET tests", steps)  # --force runs them


if __name__ == "__main__":
    unittest.main()
