"""python -m unittest scripts.idle_repair_probe_test"""

from __future__ import annotations

import argparse
import contextlib
import io
import re
import sys
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import idle_repair_probe as p  # noqa: E402
import sacs_names  # noqa: E402

IDLE = sacs_names.fnv32("standing_idle")
STANDING = sacs_names.fnv32("standing")
MAKE = {"ok": "true", "entry": "ok", "request": "ok", "requestMs": "3"}
CHECK = {"ok": "true", "valid": "1", "onPosture": "1", "newMapped": "1"}


def check(entered: str, **more: str) -> dict[str, str]:
    return {**CHECK, "entered": entered, **more}


def run_probe(argv: list[str], entry: str, checked: str) -> tuple[int, str]:
    """(exit code, everything main printed) for one run whose two probes reply with exactly this text.

    Unlike Delay.run_main this keeps stdout, so a test can assert the printed verdict line and not only the code."""
    replies = {"IdleRepairEntryProbe": entry, "IdleRepairCheckProbe": checked}

    def bridge(endpoint: str, *args: str, timeout: int = 60) -> str:
        return replies[Path(args[1]).stem.rsplit("_", 1)[0]]  # probe_source adds _<hash>

    out = io.StringIO()
    with mock.patch.object(p, "bridge", bridge), mock.patch.object(p.time, "sleep", lambda s: None), \
            contextlib.redirect_stdout(out):
        return p.main(argv), out.getvalue()


def arm_check_block(via_replay: bool = True) -> str:
    """The generated make probe's `if (ViaReplay) { ... }` arm-check block body (the one that sets `armed = 0`),
    not the pre-check block nor the repair block: anchored on `int armed = 1;`."""
    src = p.render(p.MAKE_TEMPLATE, "Pablo", "standing_idle", "standing_idle", True, "r", via_replay=via_replay)
    start = src.index("int armed = 1;")
    open_at = src.index("{", src.index("if (ViaReplay)", start))
    depth = 0
    for i in range(open_at, len(src)):
        if src[i] == "{":
            depth += 1
        elif src[i] == "}":
            depth -= 1
            if depth == 0:
                return src[open_at + 1:i]
    raise AssertionError("unbalanced braces in the arm-check block")


def arm_conditions(block: str) -> dict[str, str]:
    """armMissing label -> the C# condition that disarms, from every `if (...) { armed = 0; armMissing = ...; }` line.
    The label is the first quoted word, so `"kill:" + killStatus` reads as `kill`."""
    found = {}
    for line in block.splitlines():
        m = re.search(r"if \((?P<cond>.+)\) \{ armed = 0; armMissing = (?P<missing>.+); \}", line)
        if m:
            label = re.match(r'"([A-Za-z]+):?', m["missing"])
            found[label[1] if label else m["missing"]] = m["cond"]
    return found


def disarms(cond: str, **values) -> bool:
    """Whether the C# condition disarms the arm for these field values. The operators read the same in Python,
    except `!`, which the inverted check used - translated, so a wrong direction fails as an assertion, not a
    SyntaxError."""
    py = re.sub(r"!(?!=)", "not ", cond.replace("&&", " and ").replace("||", " or "))
    return bool(eval(py, {"__builtins__": {}}, values))


def with_field(text: str, **fields: str) -> str:
    """Reply text with one field's value replaced; every other byte stays as written."""
    for key, value in fields.items():
        text, n = re.subn(rf"(?<=;){key}=[^;]*", f"{key}={value}", text)
        if n != 1:
            raise AssertionError(f"{key}: {n} matches in the reply text")
    return text


class Render(unittest.TestCase):
    def test_every_placeholder_is_filled(self) -> None:
        for template in (p.MAKE_TEMPLATE, p.CHECK_TEMPLATE):
            src = p.render(template, "Iliana", "standing_idle", "standing_idle", True, "abc123")
            self.assertNotRegex(src, r"__[A-Z]+__")
            self.assertIn('"SimsOnline.IdleRepairProbe.abc123"', src)
        make = p.render(p.MAKE_TEMPLATE, "Iliana", "standing_idle", "", False, "r")
        self.assertIn('SimName = "Iliana"', make)
        self.assertIn('RequestName = ""', make)
        self.assertIn("StopWhenAlive = false;", make)
        self.assertIn('SetContext("repair:probe/stream")', make)  # a label motion_watch reads as a repair entry

    def test_names_are_checked(self) -> None:
        self.assertEqual(p.sim_name("Jennifer"), "Jennifer")
        self.assertEqual(p.state_name("Exit Working"), "Exit Working")
        for bad in ('a"b', "x;y", ""):
            with self.assertRaises(argparse.ArgumentTypeError):
                p.sim_name(bad)
            with self.assertRaises(argparse.ArgumentTypeError):
                p.state_name(bad)


class Reply(unittest.TestCase):
    def test_fields_after_loaded(self) -> None:
        out = "uploading\nloaded=1\nok = true;run=r;entered=12:2083806798,40:1"
        self.assertEqual(p.reply(out), {"ok": "true", "run": "r", "entered": "12:2083806798,40:1"})
        self.assertEqual(p.reply("err=no-sim;sim=X"), {"err": "no-sim", "sim": "X"})
        self.assertEqual(p.entered_events("12:2083806798,40:1,bad"), [(12, 2083806798), (40, 1)])
        self.assertEqual(p.entered_events("-"), [])


class Verdict(unittest.TestCase):
    def test_entries_before_a_delayed_request_are_not_restarts(self) -> None:
        made = {**MAKE, "requestMs": "3033"}
        ok, notes = p.verdict(made, check(f"2:{IDLE},2414:{IDLE}"), "standing_idle", "standing_idle")
        self.assertTrue(ok, notes)

    def test_control_subtracts_natural_entries_in_the_request_window(self) -> None:
        made = {**MAKE, "requestMs": "1532"}
        control = check(f"2:{IDLE},2100:{IDLE},5000:{IDLE}")
        ok, notes = p.verdict(made, check(f"2:{IDLE},2101:{IDLE}"), "standing_idle", "standing_idle",
                              control=control)
        self.assertTrue(ok, notes)
        self.assertIn("natural=1", ";".join(notes))
        # A request re-entry plus the natural entry still fails after subtracting the control.
        self.assertFalse(p.verdict(made, check(f"2:{IDLE},1538:{IDLE},2101:{IDLE}"),
                                   "standing_idle", "standing_idle", control=control)[0])

    def test_control_entries_before_the_request_cannot_hide_a_restart(self) -> None:
        made = {**MAKE, "requestMs": "3033"}
        self.assertFalse(p.verdict(made, check(f"2:{IDLE},2414:{IDLE},3038:{IDLE}"),
                                   "standing_idle", "standing_idle", control=check(f"2:{IDLE},2414:{IDLE}"))[0])

    def test_no_request_observes_natural_loops(self) -> None:
        made = {**MAKE, "request": "-", "requestMs": "-1"}
        self.assertTrue(p.verdict(made, check(f"2:{IDLE},500:{IDLE},2100:{IDLE}"),
                                  "standing_idle", "")[0])

    def test_one_entry_and_a_request_that_does_not_reenter_passes(self) -> None:
        ok, notes = p.verdict(MAKE, check(f"2:{IDLE}"), "standing_idle", "standing_idle")
        self.assertTrue(ok, notes)
        # the entry's own event landing after the request is still one entry
        self.assertTrue(p.verdict(MAKE, check(f"9:{IDLE}"), "standing_idle", "standing_idle")[0])

    def test_reentry_within_a_second_of_the_request_is_a_restart(self) -> None:
        ok, notes = p.verdict(MAKE, check(f"2:{IDLE},700:{IDLE}"), "standing_idle", "standing_idle")
        self.assertFalse(ok)
        self.assertTrue(any(n.startswith("restart: standing_idle entered 2 times") for n in notes), notes)
        # the loop's next cycle, seconds later, is not
        self.assertTrue(p.verdict(MAKE, check(f"2:{IDLE},4200:{IDLE}"), "standing_idle", "standing_idle")[0])

    def test_creation_state_dead_machine_or_no_event_fails(self) -> None:
        self.assertIn("the creation state standing was entered",
                      p.verdict(MAKE, check(f"1:{STANDING},2:{IDLE}"), "standing_idle", "standing_idle")[1])
        self.assertIn("the machine is dead", p.verdict(MAKE, check(f"2:{IDLE}", valid="0"), "standing_idle", "standing_idle")[1])
        self.assertIn("the posture holds another machine",
                      p.verdict(MAKE, check(f"2:{IDLE}", onPosture="0"), "standing_idle", "standing_idle")[1])
        self.assertIn("standing_idle never reported entered (the machine does not animate)",
                      p.verdict(MAKE, check("-"), "standing_idle", "standing_idle")[1])

    def test_failed_entry_request_or_probe(self) -> None:
        bad = {**MAKE, "entry": "err:SacsErrorException:x", "request": "sacs-error:y"}
        notes = p.verdict(bad, check(f"2:{IDLE}"), "standing_idle", "standing_idle")[1]
        self.assertIn("entry err:SacsErrorException:x", notes)
        self.assertIn("request sacs-error:y", notes)
        self.assertEqual(p.verdict({"err": "alive"}, CHECK, "standing_idle", "standing_idle"), (False, ["make: alive"]))
        self.assertEqual(p.verdict(MAKE, {"err": "no-run"}, "standing_idle", "standing_idle"), (False, ["check: no-run"]))

    def test_no_request_judges_the_entry_alone(self) -> None:
        make = {**MAKE, "request": "-", "requestMs": "-1"}
        self.assertTrue(p.verdict(make, check(f"2:{IDLE}"), "standing_idle", "")[0])
        self.assertTrue(p.verdict(make, check(f"2:{IDLE},500:{IDLE}"), "standing_idle", "")[0])


class Delay(unittest.TestCase):
    def test_defaults_are_unchanged(self) -> None:
        check_src = p.render(p.CHECK_TEMPLATE, "Iliana", "standing_idle", "standing_idle", True, "r")
        self.assertIn("FinishRun = true;", check_src)
        self.assertIn('RequestName = "";', check_src)
        self.assertIn("DelayMs = 0;", check_src)
        self.assertIn("WindowMs = 1500;", check_src)
        self.assertEqual(p.parse_args(["Jennifer"]).request_delay, 0)

    def test_delay_and_window_reach_the_generated_probes(self) -> None:
        step = p.render(p.CHECK_TEMPLATE, "Iliana", "standing_idle", "standing_idle", True, "r",
                        delay_ms=150, window_ms=10000, step=True)
        self.assertIn("FinishRun = false;", step)
        self.assertIn('RequestName = "standing_idle";', step)
        self.assertIn("DelayMs = 150;", step)
        self.assertIn("WindowMs = 10000;", step)
        self.assertNotRegex(step, r"__[A-Z]+__")

    def test_delay_is_validated(self) -> None:
        for bad in (["X", "--request-delay", "-1"], ["X", "--request-delay", "5001"],
                    ["X", "--request-delay", "50", "--no-request"]):
            with self.assertRaises(SystemExit), mock.patch("sys.stderr"):
                p.parse_args(bad)
        self.assertEqual(p.parse_args(["X", "--request-delay", "300"]).request_delay, 300)

    def run_main(self, argv: list[str], override: dict[str, str] | None = None, code: int = 0,
                 clock: list[float] | None = None) -> tuple[list[str], list[float], dict[str, str]]:
        calls: list[str] = []
        sleeps: list[float] = []
        sources: dict[str, str] = {}
        replies = {
            "IdleRepairEntryProbe": "loaded=1\nok=true;entry=ok;request=ok;requestMs=3",
            "IdleRepairRequestProbe": "loaded=1\nok=true;request=ok;requestMs=151",
            "IdleRepairCheckProbe": f"loaded=1\nok=true;valid=1;onPosture=1;newMapped=1;entered=2:{IDLE}",
        }

        def bridge(endpoint: str, *args: str, timeout: int = 60) -> str:
            name = Path(args[1]).stem.rsplit("_", 1)[0]  # probe_source adds _<hash>
            calls.append(name)
            sources[name] = (p.ROOT / args[1]).read_text(encoding="utf-8")
            return replies[name]

        replies.update(override or {})
        ticks = iter(clock or [])
        with mock.patch.object(p, "bridge", bridge), mock.patch.object(p.time, "sleep", sleeps.append), \
                mock.patch.object(p.time, "monotonic", lambda: next(ticks)) if clock else mock.patch("os.getcwd"), \
                mock.patch("sys.stdout"):
            self.assertEqual(p.main(argv), code)
        return calls, sleeps, sources

    def test_delayed_request_goes_from_a_step_between_make_and_check(self) -> None:
        for delay in (50, 150, 300):
            calls, sleeps, src = self.run_main(["Iliana", "--request-delay", str(delay)])
            self.assertEqual(calls, ["IdleRepairEntryProbe", "IdleRepairRequestProbe", "IdleRepairCheckProbe"])
            self.assertEqual(sleeps[0], delay / 1000.0)
            self.assertIn('RequestName = "";', src["IdleRepairEntryProbe"])  # the make no longer requests
            self.assertIn(f"DelayMs = {delay};", src["IdleRepairRequestProbe"])
            self.assertIn("FinishRun = true;", src["IdleRepairCheckProbe"])
            self.assertGreaterEqual(sum(sleeps), 1.0 + delay / 1000.0 - 0.01)

    def test_slow_request_step_still_leaves_a_second_after_the_request(self) -> None:
        # the step took 5 s to return (load): the old total-from-start sleep was 0, shrinking the window below 1 s
        _, sleeps, _ = self.run_main(["Iliana", "--request-delay", "150"], clock=[0.0, 5.0])
        self.assertEqual(sleeps[0], 0.15)
        self.assertGreaterEqual(sleeps[-1], 1.0)

    def test_failed_request_step_still_runs_the_check_to_clean_up(self) -> None:
        calls, _, _ = self.run_main(["Iliana", "--request-delay", "150"], code=2,
                                    override={"IdleRepairRequestProbe": "loaded=1\nerr=InvalidCastException;msg=x"})
        self.assertEqual(calls, ["IdleRepairEntryProbe", "IdleRepairRequestProbe", "IdleRepairCheckProbe"])

    def test_no_delay_keeps_the_two_probe_flow(self) -> None:
        calls, sleeps, src = self.run_main(["Iliana"])
        self.assertEqual(calls, ["IdleRepairEntryProbe", "IdleRepairCheckProbe"])
        self.assertEqual(sleeps, [1.5])
        self.assertIn('RequestName = "standing_idle";', src["IdleRepairEntryProbe"])

    def test_long_window_control(self) -> None:
        calls, sleeps, src = self.run_main(["Iliana", "--no-request", "--window", "12"])
        self.assertEqual(calls, ["IdleRepairEntryProbe", "IdleRepairCheckProbe"])
        self.assertEqual(sleeps, [12.0])
        self.assertIn("WindowMs = 12000;", src["IdleRepairCheckProbe"])


class InGameDelay(unittest.TestCase):
    run_main = Delay.run_main
    ENTRY = "loaded=1\nok=true;entry=ok;request=scheduled;requestMs=-1;enterMs=2;ingameDelayMs=150"

    def check_reply(self, request_ms: int, request: str = "ok") -> str:
        return f"loaded=1\nok=true;valid=1;onPosture=1;newMapped=1;request={request};requestMs={request_ms};frames=9;entered=2:{IDLE}"

    def test_args(self) -> None:
        a = p.parse_args(["X"])
        self.assertEqual((a.ingame_delay_ms, a.delay_early_ms, a.delay_late_ms), (0, 10, 45))
        self.assertEqual((p.DELAY_EARLY_MS, p.DELAY_LATE_MS), (10, 45))
        self.assertEqual(p.parse_args(["X", "--ingame-delay-ms", "150"]).ingame_delay_ms, 150)
        b = p.parse_args(["X", "--ingame-delay-ms", "50", "--delay-early-ms", "5", "--delay-late-ms", "60"])
        self.assertEqual((b.delay_early_ms, b.delay_late_ms), (5, 60))
        for bad in (["X", "--ingame-delay-ms", "-1"], ["X", "--ingame-delay-ms", "5001"],
                    ["X", "--ingame-delay-ms", "50", "--no-request"],
                    ["X", "--ingame-delay-ms", "50", "--request-delay", "50"],
                    ["X", "--ingame-delay-ms", "50", "--delay-early-ms", "-1"],
                    ["X", "--ingame-delay-ms", "50", "--delay-late-ms", "-1"]):
            with self.assertRaises(SystemExit), mock.patch("sys.stderr"):
                p.parse_args(bad)

    def test_delay_is_embedded_in_the_one_make_probe(self) -> None:
        make = p.render(p.MAKE_TEMPLATE, "Iliana", "standing_idle", "standing_idle", True, "r", ingame_delay_ms=150)
        self.assertIn("InGameDelayMs = 150;", make)
        self.assertIn('RequestName = "standing_idle";', make)  # the make keeps the request: it sends it later itself
        self.assertIn("Simulator.AddObject(new DelayedRequest(", make)
        self.assertNotRegex(make, r"__[A-Z]+__")
        self.assertIn("InGameDelayMs = 0;", p.render(p.MAKE_TEMPLATE, "Iliana", "standing_idle", "standing_idle", True, "r"))

    def test_delay_check(self) -> None:
        make = {"enterMs": "2"}
        ok, line = p.delay_check(make, {"requestMs": "153", "frames": "9"}, 150, 10, 45)
        self.assertTrue(ok, line)
        self.assertIn("measured 151 ms", line)
        ok, line = p.delay_check(make, {"requestMs": "252"}, 150, 10, 45)
        self.assertFalse(ok)
        self.assertIn("OFF by +100 ms", line)
        self.assertFalse(p.delay_check(make, {"requestMs": "40"}, 150, 10, 45)[0])  # early is off too
        self.assertFalse(p.delay_check(make, {"requestMs": "-1", "request": "pending"}, 150, 10, 45)[0])  # never sent
        self.assertFalse(p.delay_check({}, {"requestMs": "153"}, 150, 10, 45)[0])  # no entry time

    def test_tolerance_is_asymmetric(self) -> None:
        # the task can only fire late (0 to 1 frame, plus clock granularity): 42 ms late passes, 12 ms early does not
        make, early, late = {"enterMs": "2"}, p.DELAY_EARLY_MS, p.DELAY_LATE_MS
        ok, line = p.delay_check(make, {"requestMs": str(2 + 150 + 42)}, 150, early, late)
        self.assertTrue(ok, line)
        ok, line = p.delay_check(make, {"requestMs": str(2 + 150 - 12)}, 150, early, late)
        self.assertFalse(ok, line)
        self.assertIn("OFF by -12 ms", line)

    def test_one_probe_does_both_steps(self) -> None:
        for delay in (50, 150, 300):
            calls, sleeps, src = self.run_main(["Iliana", "--ingame-delay-ms", str(delay)], override={
                "IdleRepairEntryProbe": self.ENTRY, "IdleRepairCheckProbe": self.check_reply(2 + delay)})
            self.assertEqual(calls, ["IdleRepairEntryProbe", "IdleRepairCheckProbe"])  # no bridge request step
            self.assertIn(f"InGameDelayMs = {delay};", src["IdleRepairEntryProbe"])
            self.assertEqual(sleeps, [max(1.5, delay / 1000.0 + 1.0)])  # a second after the in-game request

    def test_off_delay_exits_5_and_a_pending_request_fails(self) -> None:
        self.run_main(["Iliana", "--ingame-delay-ms", "50"], code=5, override={
            "IdleRepairEntryProbe": self.ENTRY, "IdleRepairCheckProbe": self.check_reply(1199)})
        self.run_main(["Iliana", "--ingame-delay-ms", "50"], code=4, override={
            "IdleRepairEntryProbe": self.ENTRY, "IdleRepairCheckProbe": self.check_reply(-1, "pending")})


class ViaReplay(unittest.TestCase):
    run_main = Delay.run_main
    ENTRY = ("loaded=1\nok=true;entry=ok;request=ok;requestMs=3;enterMs=2;enterTicks=100;requestTicks=100;"
             "viaReplay=1;viaDriver=77;dropBefore=4;oldValid=0;idleRepairStreamDelta=1;postureValid=1;gateMark=1;"
             "armed=1;armMissing=-;reresolvedBefore=17")

    def check_reply(self, after: str = "5", **more: str) -> str:
        fields = {"ok": "true", "valid": "1", "onPosture": "1", "newMapped": "1", "dropAfter": after,
                  "reresolvedAfter": "18", "entered": f"2:{IDLE}", **more}
        return "loaded=1\n" + ";".join(f"{k}={v}" for k, v in fields.items())

    def test_probe_text_for_each_arm(self) -> None:
        on = p.render(p.MAKE_TEMPLATE, "Iliana", "standing_idle", "standing_idle", True, "r", via_replay=True)
        off = p.render(p.MAKE_TEMPLATE, "Iliana", "standing_idle", "standing_idle", True, "r")
        self.assertIn("ViaReplay = true;", on)
        self.assertIn("ViaReplay = false;", off)
        self.assertIn(f"0|R|\" + serverDriver + \"|{sacs_names.fnv32('x')}|{IDLE}|448", on)
        self.assertIn('GetMethod("ApplyReplayBatch"', on)
        for src in (on, off):
            self.assertNotRegex(src, r"__[A-Z]+__")
            self.assertIn("Simulator.TicksElapsed()", src)
            self.assertIn("enterTicks=", src)
        check = p.render(p.CHECK_TEMPLATE, "Iliana", "standing_idle", "standing_idle", True, "r", via_replay=True)
        self.assertIn("dropAfter=", check)
        self.assertIn("requestTicks=", check)

    def test_args(self) -> None:
        self.assertTrue(p.parse_args(["X", "--via-replay"]).via_replay)
        self.assertFalse(p.parse_args(["X"]).via_replay)
        self.assertTrue(p.parse_args(["X", "--via-replay", "--no-request"]).no_request)
        for bad in (["X", "--via-replay", "--request-delay", "50"],):
            with self.assertRaises(SystemExit), mock.patch("sys.stderr"):
                p.parse_args(bad)

    def test_pumped_and_drop_delta(self) -> None:
        self.assertEqual(p.pumped("100", "100"), "no")
        self.assertEqual(p.pumped("100", "101"), "yes")
        self.assertEqual(p.pumped("100", "-1"), "-")
        self.assertEqual(p.drop_delta({"dropBefore": "4"}, {"dropAfter": "5"}), 1)
        self.assertIsNone(p.drop_delta({"dropBefore": "-1"}, {"dropAfter": "5"}))
        self.assertIsNone(p.drop_delta({}, {}))

    def test_one_frame_rule_is_a_different_tick(self) -> None:
        make = {"enterTicks": "100"}
        self.assertTrue(p.delay_check(make, {"requestTicks": "101", "request": "ok"}, 1, 10, 45)[0])
        ok, line = p.delay_check(make, {"requestTicks": "100", "request": "ok"}, 1, 10, 45)
        self.assertFalse(ok)
        self.assertIn("delay=OFF", line)
        self.assertFalse(p.delay_check(make, {"request": "pending"}, 1, 10, 45)[0])

        # only the NEXT tick is the 1-frame case: two or more ticks after the entry is not
        ok, line = p.delay_check({"enterTicks": "10"}, {"requestTicks": "11", "request": "ok"}, 1, 10, 45)
        self.assertTrue(ok, line)
        self.assertIn("1 tick after", line)
        ok, line = p.delay_check({"enterTicks": "10"}, {"requestTicks": "12", "request": "ok"}, 1, 10, 45)
        self.assertFalse(ok)
        self.assertIn("delay=OFF", line)
        self.assertIn("landed 2 ticks after the entry", line)
        # above the 1-frame cap the ms window still applies
        self.assertFalse(p.delay_check({"enterMs": "2", "enterTicks": "1"}, {"requestMs": "40", "requestTicks": "9"}, 50, 10, 45)[0])

    def test_same_pump_via_replay_run(self) -> None:
        calls, sleeps, src = self.run_main(["Iliana", "--via-replay"], override={
            "IdleRepairEntryProbe": self.ENTRY, "IdleRepairCheckProbe": self.check_reply()})
        self.assertEqual(calls, ["IdleRepairEntryProbe", "IdleRepairCheckProbe"])
        self.assertIn("ViaReplay = true;", src["IdleRepairEntryProbe"])

    def test_one_frame_via_replay_run(self) -> None:
        entry = ("loaded=1\nok=true;entry=ok;request=scheduled;requestMs=-1;enterMs=2;enterTicks=100;viaReplay=1;"
                 "viaDriver=77;dropBefore=4;oldValid=0;idleRepairStreamDelta=1;postureValid=1;gateMark=1;"
                 "armed=1;armMissing=-;reresolvedBefore=17")
        self.run_main(["Iliana", "--via-replay", "--ingame-delay-ms", "1"], override={
            "IdleRepairEntryProbe": entry,
            "IdleRepairCheckProbe": self.check_reply(request="ok", requestMs="35", frames="1", requestTicks="101")})
        # the same tick: the arm did not test the next frame
        self.run_main(["Iliana", "--via-replay", "--ingame-delay-ms", "1"], code=5, override={
            "IdleRepairEntryProbe": entry,
            "IdleRepairCheckProbe": self.check_reply(request="ok", requestMs="2", frames="1", requestTicks="100")})

    def test_old_mod_and_missing_driver_exit_2(self) -> None:
        for err in ("no idleRepairDupDropped counter: mod without B2+(a)",
                    "no server idle driver mapped onto the Sim's idle machine"):
            self.run_main(["Iliana", "--via-replay"], code=2, override={
                "IdleRepairEntryProbe": f"loaded=1\nerr={err}", "IdleRepairCheckProbe": "loaded=1\nerr=no-run"})
        # a check with no counter reading is exit 2 too
        self.run_main(["Iliana", "--via-replay"], code=2, override={
            "IdleRepairEntryProbe": self.ENTRY, "IdleRepairCheckProbe": self.check_reply(after="-1")})

    def test_in_game_task_restores_the_context_on_every_path(self) -> None:
        src = p.render(p.MAKE_TEMPLATE, "Iliana", "standing_idle", "standing_idle", True, "r", via_replay=True)
        sim = src[src.index("public override void Simulate()"):]
        sim = sim[:sim.index("Simulator.DestroyObject")]
        label, replay = sim.index('SetContext("repair:probe/stream")'), sim.index("ReplayRequest(mServerDriver)")
        restore = sim.index("finally { SetContext(context); }")
        # the label is set, then a try opens before every exit (the replay call included), and its finally restores
        opens = sim.index("try", label)
        self.assertLess(opens, replay)
        self.assertLess(replay, restore)
        self.assertNotIn("return;", sim[opens:restore])  # no exit between the try and its finally can skip it

    def test_counter_lookup_is_ordinal_and_refuses_an_ambiguous_match(self) -> None:
        src = p.render(p.MAKE_TEMPLATE, "Iliana", "standing_idle", "standing_idle", True, "r", via_replay=True)
        self.assertIn("ToLowerInvariant()", src)
        self.assertNotIn(".ToLower()", src)
        self.assertIn("return -2;", src)


class Args(unittest.TestCase):
    def test_control_receipt_options(self) -> None:
        a = p.parse_args(["Jennifer", "--via-replay", "--control", "control.json", "--output", "arm.json"])
        self.assertEqual(a.control, Path("control.json"))
        self.assertEqual(a.output, Path("arm.json"))

    def test_invalidate_only_requires_a_replay_control(self) -> None:
        for args in (["X", "--invalidate-only"], ["X", "--via-replay", "--invalidate-only"]):
            with self.assertRaises(SystemExit), mock.patch("sys.stderr"):
                p.parse_args(args)
        a = p.parse_args(["X", "--via-replay", "--no-request", "--invalidate-only"])
        self.assertTrue(a.invalidate_only)

    def test_invalidation_returns_before_repair_and_preserves_other_drivers(self) -> None:
        src = p.render(p.MAKE_TEMPLATE, "Pablo", "standing_idle", "", True, "r",
                       via_replay=True, invalidate_only=True)
        self.assertIn("InvalidateOnly = true;", src)
        self.assertIn("proxy.ReleaseDriver(Handle(sim.Standing.CurrentStateMachine))", src)
        self.assertLess(src.index('return "ok=" + (!oldValid'), src.index("long t0 = Now();"))

    def test_defaults(self) -> None:
        a = p.parse_args(["Jennifer"])
        self.assertEqual((a.sim, a.client, a.enter, a.request, a.window), ("Jennifer", "127.0.0.1:34030", "standing_idle",
                                                                           "standing_idle", 1.5))
        self.assertFalse(a.no_stop or a.no_request or a.dry_run)


class ControlReceipt(unittest.TestCase):
    def test_premise_requires_the_shared_stream_repair_to_have_run(self):
        for delta, valid, gate, expected in (("1", "1", "1", 0), ("0", "1", "1", 2),
                                           ("1", "0", "1", 2), ("1", "1", "0", 2)):
            entry = f"ok=true;oldValid=0;oldMapped=1;kill=ok;idleRepairStreamDelta={delta};postureValid={valid};gateMark={gate}"
            with mock.patch.object(p, "bridge", return_value=entry), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(p.main(["Jennifer", "--via-replay", "--no-request", "--invalidate-only",
                                         "--repair-after-invalidate"]), expected)

    def test_premise_repairs_before_returning_and_reports_the_delta(self):
        src = p.render(p.MAKE_TEMPLATE, "Jennifer", "standing_idle", "", True, "r",
                       via_replay=True, invalidate_only=True, repair_after_invalidate=True)
        block = src[src.index("if (InvalidateOnly)\n"):src.index("long t0 = Now();")]
        self.assertIn("RepairOwn(sim)", block)
        self.assertIn('Counter("sIdleRepairStream")', block)
        self.assertIn("idleRepairStreamDelta=", block)
        self.assertIn("postureValid=", block)

    def test_warm_control_waits_for_the_late_join_borrow_before_killing(self):
        pending = ("loaded=1\nerr=pre-check refusal: no server idle driver mapped onto the Sim's idle machine;"
                   "pending=borrowed-idle;oldHandle=549298817;oldMapped=0")
        entry = ViaReplay.ENTRY.replace("request=ok", "request=-").replace("requestMs=3", "requestMs=-1")
        replies = iter([pending, entry, ViaReplay().check_reply(reresolvedAfter="17", newMapped="0")])
        with mock.patch.object(p, "bridge", side_effect=lambda *args, **kwargs: next(replies)) as bridge, \
                mock.patch.object(p.time, "sleep") as sleep, contextlib.redirect_stdout(io.StringIO()) as output:
            code = p.main(["Jennifer", "--via-replay", "--no-request", "--wait-mapped", "30"])
        self.assertEqual(code, 0, output.getvalue())
        self.assertEqual(bridge.call_count, 3)  # read-only refusal, actual make, check
        self.assertIn("waiting for borrowed idle mapping", output.getvalue())
        self.assertIn(mock.call(0.25), sleep.call_args_list)

    def test_waiting_for_a_borrowed_idle_is_bounded(self):
        pending = "loaded=1\nerr=no borrowed idle;pending=borrowed-idle;oldMapped=0"
        with mock.patch.object(p, "bridge", return_value=pending) as bridge, \
                mock.patch.object(p.time, "monotonic", side_effect=[0, 31]), \
                mock.patch.object(p.time, "sleep") as sleep, contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(p.main(["Jennifer", "--via-replay", "--no-request", "--wait-mapped", "30"]), 2)
        self.assertEqual(bridge.call_count, 1)
        sleep.assert_not_called()
        self.assertIn("verdict=ERROR", output.getvalue())

    def test_other_preconditions_are_not_retried(self):
        with mock.patch.object(p, "bridge", return_value="loaded=1\nerr=not-standing;posture=Sitting") as bridge, \
                contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(p.main(["Jennifer", "--via-replay", "--no-request", "--wait-mapped", "30"]), 2)
        self.assertEqual(bridge.call_count, 1)

    def test_mapping_pending_return_precedes_every_kill(self):
        src = p.render(p.MAKE_TEMPLATE, "Jennifer", "standing_idle", "", True, "r", via_replay=True)
        self.assertLess(src.index('pending=borrowed-idle'), src.index("killStatus = KillIdleDrivers(sim)"))

    def test_wait_mapped_budget_validation(self):
        for budget in ("-1", "121", "nan", "inf"):
            with self.assertRaises(SystemExit), mock.patch("sys.stderr"):
                p.parse_args(["Jennifer", "--via-replay", "--wait-mapped", budget])

    def test_replay_control_needs_no_reresolve_and_saves_its_events(self):
        import json
        import tempfile
        entry = ViaReplay.ENTRY.replace("request=ok", "request=-").replace("requestMs=3", "requestMs=-1")
        checked = ViaReplay().check_reply(reresolvedAfter="17", newMapped="0", entered=f"2:{IDLE},2100:{IDLE}")
        with tempfile.TemporaryDirectory() as folder:
            output = Path(folder) / "control.json"
            code, text = run_probe(["Jennifer", "--via-replay", "--no-request", "--window", "6",
                                    "--output", str(output)], entry, checked)
            self.assertEqual(code, 0, text)
            receipt = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual(receipt["request"], "")
            self.assertEqual(receipt["check"]["entered"], f"2:{IDLE},2100:{IDLE}")
            code, text = run_probe(["Jennifer", "--via-replay", "--ingame-delay-ms", "1500",
                                    "--control", str(output)], ViaReplay.ENTRY.replace("request=ok", "request=scheduled"),
                                   ViaReplay().check_reply(request="ok", requestMs="1532", entered=f"2:{IDLE},2101:{IDLE}"))
            self.assertEqual(code, 0, text)
            self.assertIn("post=1 natural=1 excess=0", text)

    def test_mismatched_short_or_failed_control_stops_before_the_game(self):
        import json
        import tempfile
        receipt = {"sim": "Jennifer", "client": "127.0.0.1:34030", "enter": "standing_idle",
                   "request": "", "viaReplay": True, "passed": True, "windowMs": 6000,
                   "check": check(f"2:{IDLE},2100:{IDLE}")}
        with tempfile.TemporaryDirectory() as folder, mock.patch.object(p, "bridge") as bridge:
            output = Path(folder) / "control.json"
            for bad in ({"sim": "Pablo"}, {"passed": False}, {"windowMs": 1000}, {"request": "standing_idle"},
                        {"check": {}}, {"check": {"entered": []}}, {"viaReplay": False}):
                output.write_text(json.dumps(receipt | bad), encoding="utf-8")
                with contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(p.main(["Jennifer", "--via-replay", "--control", str(output),
                                             "--ingame-delay-ms", "1500"]), 2)
            bridge.assert_not_called()

    def test_non_object_control_receipt_is_an_error(self):
        import tempfile
        with tempfile.TemporaryDirectory() as folder, mock.patch.object(p, "bridge") as bridge:
            output = Path(folder) / "control.json"
            output.write_text("[]", encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(p.main(["Jennifer", "--via-replay", "--control", str(output)]), 2)
            bridge.assert_not_called()

    def test_invalidate_only_does_not_run_the_check_or_sleep(self):
        with mock.patch.object(p, "bridge", return_value="ok=true;oldValid=0;oldMapped=1;kill=ok") as bridge, \
                mock.patch.object(p.time, "sleep") as sleep, contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(p.main(["Jennifer", "--via-replay", "--no-request", "--invalidate-only"]), 0)
        self.assertEqual(bridge.call_count, 1)
        sleep.assert_not_called()


class KillCall(unittest.TestCase):
    """The kill uses the same engine call the replay's Z handler uses on a client (`ISacsProxy.StopAllAnimation` via
    `SacsRecorder.Current`), which invalidates the driver handle. `Sims3.SimIFace.Animation.StopAllAnimation` (the
    clip-level IAnimation static) does not, so run 1 read oldValid=1 and the gate was never consulted."""

    def test_kill_uses_the_sacs_proxy_not_the_animation_static(self) -> None:
        for src in (p.render(p.MAKE_TEMPLATE, "Iliana", "standing_idle", "standing_idle", True, "r"),
                    p.render(p.MAKE_TEMPLATE, "Iliana", "standing_idle", "standing_idle", True, "r", via_replay=True)):
            self.assertIn('Host("SimsOnlineServerHost.SacsRecorder")', src)
            self.assertIn('GetProperty("Current"', src)
            self.assertIn("as ISacsProxy", src)
            self.assertIn("proxy.StopAllAnimation(sim.ObjectId)", src)
            self.assertNotIn("Animation.StopAllAnimation(sim.ObjectId)", src)  # the clip-level call the brief rules out

    def test_kill_call_present_on_both_paths(self) -> None:
        # the kill is universal (brief item 1); only the machine-making (item 2) differs by --via-replay
        for flag in (True, False):
            src = p.render(p.MAKE_TEMPLATE, "Iliana", "standing_idle", "standing_idle", True, "r", via_replay=flag)
            self.assertIn("KillIdleDrivers(sim)", src)


class ViaReplayNoOwnMachine(unittest.TestCase):
    """With --via-replay, the probe makes no machine of its own (brief item 2): it lets B2 (via RepairOwn) acquire,
    enter and mark. The non-via-replay arms keep today's shape. Both branches live in the source; the runtime flag
    picks one, so the test extracts the via-replay branch (between the `if (ViaReplay)` open and the `else`) and
    asserts there."""

    def via_replay_branch(self) -> str:
        src = p.render(p.MAKE_TEMPLATE, "Iliana", "standing_idle", "standing_idle", True, "r", via_replay=True)
        start = src.index("if (ViaReplay)")
        end = src.index("else\n", start)  # the sibling else branch begins here
        return src[start:end]

    def non_via_replay_branch(self) -> str:
        src = p.render(p.MAKE_TEMPLATE, "Iliana", "standing_idle", "standing_idle", True, "r", via_replay=False)
        start = src.index("else\n")
        end = src.index("long enterMs = Now() - t0;", start)  # where the branches rejoin
        return src[start:end]

    def test_via_replay_branch_has_no_own_acquire_or_enter(self) -> None:
        block = self.via_replay_branch()
        self.assertNotIn("StateMachineClient.Acquire", block)
        self.assertNotIn('sm.EnterState("x", EnterName)', block)
        self.assertNotIn("standing.SetDefaultIdleAnim()", block)
        self.assertNotIn("standing.SetIdleCallbacks()", block)
        self.assertIn("RepairOwn(sim)", block)  # the probe calls the repair, which does the making
        self.assertIn("sm = standing.CurrentStateMachine", block)  # reads B2's new machine, not one it made

    def test_non_via_replay_branch_keeps_today_shape(self) -> None:
        block = self.non_via_replay_branch()
        self.assertIn('StateMachineClient.Acquire(sim.ObjectId, "idle", (AnimationPriority)(-2), false)', block)
        self.assertIn('sm.EnterState("x", EnterName)', block)
        self.assertIn("standing.SetDefaultIdleAnim()", block)
        self.assertIn("standing.SetIdleCallbacks()", block)


class ArmFieldsInProbe(unittest.TestCase):
    """Brief item 3: the probe reports each arm check as a field; a missing one is the verdict's reason."""

    def test_make_reports_pre_r_arm_fields(self) -> None:
        src = p.render(p.MAKE_TEMPLATE, "Iliana", "standing_idle", "standing_idle", True, "r", via_replay=True)
        for field in ("oldValid=", "idleRepairStreamBefore=", "idleRepairStreamAfter=", "idleRepairStreamDelta=",
                      "postureValid=", "gateMark=", "armed=", "armMissing=", "reresolvedBefore=", "kill="):
            self.assertIn(field, src, field)

    def test_make_reads_the_gate_mark_by_reflection(self) -> None:
        src = p.render(p.MAKE_TEMPLATE, "Iliana", "standing_idle", "standing_idle", True, "r", via_replay=True)
        # sRepairEntries -> mMarks -> ContainsKey(handle), not a raw Count: the brief names the mark for the handle.
        self.assertIn('GetField("sRepairEntries"', src)
        self.assertIn('GetField("mMarks"', src)
        self.assertIn('GetMethod("ContainsKey"', src)

    def test_make_reads_the_stream_and_reresolve_counters(self) -> None:
        src = p.render(p.MAKE_TEMPLATE, "Iliana", "standing_idle", "standing_idle", True, "r", via_replay=True)
        self.assertIn('Counter("sIdleRepairStream")', src)
        self.assertIn('Counter("sReplayReresolved")', src)

    def test_check_reports_reresolved_after(self) -> None:
        src = p.render(p.CHECK_TEMPLATE, "Iliana", "standing_idle", "standing_idle", True, "r")
        self.assertIn('Counter("sReplayReresolved")', src)
        self.assertIn("reresolvedAfter=", src)
        self.assertIn("newMapped=", src)


class ArmCheckFunctions(unittest.TestCase):
    ARMED = {"armed": "1", "armMissing": "-"}

    def test_pre_passes_when_armed_1(self) -> None:
        self.assertEqual(p.arm_check_pre({**self.ARMED}), (True, ""))

    def test_pre_names_the_arm_missing_field(self) -> None:
        self.assertEqual(p.arm_check_pre({"armed": "0", "armMissing": "gateMark"}), (False, "gateMark"))
        self.assertEqual(p.arm_check_pre({"armed": "0", "armMissing": "kill:no-proxy"}), (False, "kill:no-proxy"))

    def test_pre_names_armed_when_arm_missing_empty(self) -> None:
        # an older probe (or one that crashed before setting the reason): name the field we did read
        self.assertEqual(p.arm_check_pre({"armed": "0"}), (False, "armed"))

    def test_post_passes_when_reresolved_plus_one_and_new_mapped(self) -> None:
        make = {"reresolvedBefore": "17"}
        checked = {"ok": "true", "reresolvedAfter": "18", "newMapped": "1"}
        self.assertEqual(p.arm_check_post(make, checked), (True, ""))

    def test_post_names_reresolved_when_the_delta_is_wrong(self) -> None:
        # the re-resolve never ran (arm not armed): delta 0; a mod that runs it twice: delta 2
        for before, after in (("17", "17"), ("17", "19"), ("-1", "18")):
            ok, name = p.arm_check_post({"reresolvedBefore": before},
                                        {"ok": "true", "reresolvedAfter": after, "newMapped": "1"})
            self.assertFalse(ok, (before, after))
            self.assertEqual(name, "reresolved", (before, after))

    def test_post_names_new_mapped_when_mapping_never_moved(self) -> None:
        ok, name = p.arm_check_post({"reresolvedBefore": "17"},
                                    {"ok": "true", "reresolvedAfter": "18", "newMapped": "0"})
        self.assertFalse(ok)
        self.assertEqual(name, "newMapped")

    def test_reresolved_delta_helper(self) -> None:
        self.assertEqual(p.reresolved_delta({"reresolvedBefore": "5"}, {"reresolvedAfter": "6"}), 1)
        self.assertEqual(p.reresolved_delta({"reresolvedBefore": "5"}, {"reresolvedAfter": "5"}), 0)
        self.assertIsNone(p.reresolved_delta({}, {}))
        self.assertIsNone(p.reresolved_delta({"reresolvedBefore": "-1"}, {"reresolvedAfter": "3"}))


class ArmNotArmedExit(unittest.TestCase):
    run_main = Delay.run_main
    BASE_ENTRY = ("loaded=1\nok=true;entry=ok;request=ok;requestMs=3;enterMs=2;enterTicks=100;requestTicks=100;"
                  "viaReplay=1;viaDriver=77;dropBefore=4")
    OK_CHECK = ("loaded=1\nok=true;valid=1;onPosture=1;newMapped=1;dropAfter=5;reresolvedAfter=18;"
                f"entered=2:{IDLE}")

    def armed_entry(self, **more: str) -> str:
        fields = {"oldValid": "0", "idleRepairStreamDelta": "1", "postureValid": "1", "gateMark": "1",
                  "armed": "1", "armMissing": "-", "reresolvedBefore": "17", **more}
        return self.BASE_ENTRY + ";" + ";".join(f"{k}={v}" for k, v in fields.items())

    def test_pre_r_check_fails_with_arm_not_armed_exit_2(self) -> None:
        # the kill left the machine valid (today's b2-live run 1 symptom): oldValid=1, armed=0
        for missing in ("oldValid", "idleRepairStream", "postureValid", "gateMark", "kill:no-proxy"):
            entry = self.armed_entry(armed="0", armMissing=missing,
                                     oldValid="1" if missing == "oldValid" else "0")
            out = self.run_main(["Iliana", "--via-replay"], code=2, override={
                "IdleRepairEntryProbe": entry, "IdleRepairCheckProbe": self.OK_CHECK})
            # main() captured the fields; exit 2 is enough for the test, and the arm reason is on stdout (mocked).
            self.assertEqual(out[0], ["IdleRepairEntryProbe", "IdleRepairCheckProbe"])

    def test_post_r_check_fails_when_new_mapped_stays_0(self) -> None:
        # the shape the b2-live report saw: no kProxyInvalid, so no re-resolve, no new mapping
        entry = self.armed_entry()
        bad_check = ("loaded=1\nok=true;valid=1;onPosture=1;newMapped=0;dropAfter=4;reresolvedAfter=17;"
                     f"entered=2:{IDLE}")
        self.run_main(["Iliana", "--via-replay"], code=2, override={
            "IdleRepairEntryProbe": entry, "IdleRepairCheckProbe": bad_check})

    def test_armed_1_with_all_post_r_fields_passes(self) -> None:
        # the full shape the plan's A1 (drop-on 0 ms arm) expects: +1 drop, one entry, all checks present
        entry = self.armed_entry()
        self.run_main(["Iliana", "--via-replay"], code=0, override={
            "IdleRepairEntryProbe": entry, "IdleRepairCheckProbe": self.OK_CHECK})


class ArmCheckBlockDirection(unittest.TestCase):
    """The b2-arms stop rule: the arm check read `else if (!oldValid)`, which disarmed exactly the runs whose kill
    worked, so both arms came back `ERROR (arm not armed: oldValid)` at exit 2. Nothing in the suite failed, because
    every test mocked `armed=1;armMissing=-` and only probe_check.py looked at the block - and it only compiles it.
    These tests read the block's direction: each pre-R check must disarm on the failing value and not on the good one."""

    # (armMissing label, values that must disarm the arm, values that must leave it armed)
    PRE_R = (
        ("kill", {"stopped": True, "killStatus": "no-proxy"}, {"stopped": True, "killStatus": "ok"}),
        ("oldValid", {"oldValid": True}, {"oldValid": False}),  # the kill left the posture's machine valid
        ("idleRepairStream", {"streamDelta": 0}, {"streamDelta": 1}),  # B2 did not enter the stream's state
        ("postureValid", {"postureValid": 0}, {"postureValid": 1}),  # the posture holds no new, valid machine
        ("gateMark", {"gateMark": 0}, {"gateMark": 1}),  # RepairEntryGate has no mark on the new handle
    )

    def test_the_block_holds_every_pre_r_check_the_python_side_names(self) -> None:
        self.assertEqual(set(arm_conditions(arm_check_block())), set(p.ARM_PRE_R))

    def test_each_pre_r_check_disarms_on_its_failing_value_only(self) -> None:
        conds = arm_conditions(arm_check_block())
        for label, bad, good in self.PRE_R:
            with self.subTest(check=label):
                self.assertTrue(disarms(conds[label], **bad), (label, conds[label], bad))
                self.assertFalse(disarms(conds[label], **good), (label, conds[label], good))

    def test_the_old_valid_check_demands_a_kill_that_worked(self) -> None:
        block = arm_check_block()
        self.assertNotIn("!oldValid", block)  # the inversion that stopped the arms
        self.assertIn('else if (oldValid) { armed = 0; armMissing = "oldValid"; }', block)

    def test_a_kill_that_did_not_run_is_not_a_kill_failure(self) -> None:
        # the machine was already dead: stopped=0, kill="-" - the kill check must not disarm that arm
        conds = arm_conditions(arm_check_block())
        self.assertFalse(disarms(conds["kill"], stopped=False, killStatus="-"), conds["kill"])

    def test_a_mark_reading_that_failed_is_not_a_mark(self) -> None:
        # HasRepairMark answers -1 when the reflection shape does not fit: unknown is not armed
        conds = arm_conditions(arm_check_block())
        self.assertTrue(disarms(conds["gateMark"], gateMark=-1), conds["gateMark"])

    def test_a_stream_counter_that_is_missing_is_not_a_run(self) -> None:
        conds = arm_conditions(arm_check_block())
        self.assertTrue(disarms(conds["idleRepairStream"], streamDelta=-1), conds["idleRepairStream"])


class LiveArmReplies(unittest.TestCase):
    """The b2-arms lane's two real replies, verbatim but for `armed`/`armMissing` (as the fixed probe would have
    reported them: armed=1, armMissing=-). Everything else - the run ids, handles, counters, ticks and the raw
    `entered` lists - is exactly what the two games answered in arm-a1-run1.txt and arm-a2-control-run1.txt.
    A1 (drop on) is the PASS row; A2 (the control, drop off) is the FAIL restart row: `standing_idle` entered twice,
    1 ms after the request."""

    A1_MAKE = ("ok=true;run=eee6d45a9296;sim=Pablo;stopped=1;kill=ok;oldHandle=538319341;oldValid=0;oldMapped=1;"
               "newHandle=549344938;idleRepairStreamBefore=0;idleRepairStreamAfter=1;idleRepairStreamDelta=1;"
               "postureValid=1;gateMark=1;armed=0;armMissing=oldValid;entry=ok;enterMs=2;request=ok;requestMs=2;"
               "enterTicks=3547;requestTicks=3547;viaReplay=1;viaDriver=548784535;dropBefore=0;reresolvedBefore=0;"
               "ingameDelayMs=0;repair=ok;onPosture=1")
    A1_CHECK = ("ok=true;run=eee6d45a9296;dropAfter=1;elapsedMs=3398;windowMs=1500;valid=1;onPosture=1;"
                "posture=StandingPosture;newMapped=1;reresolvedAfter=1;"
                "entered=6:2083806798,65:3388689373,2366:2904452163,2366:2083806798,2399:3388689373")
    A2_MAKE = ("ok=true;run=d9d14ee51c26;sim=Pablo;stopped=1;kill=ok;oldHandle=549344938;oldValid=0;oldMapped=1;"
               "newHandle=549116898;idleRepairStreamBefore=1;idleRepairStreamAfter=2;idleRepairStreamDelta=1;"
               "postureValid=1;gateMark=1;armed=0;armMissing=oldValid;entry=ok;enterMs=1;request=ok;requestMs=1;"
               "enterTicks=5878;requestTicks=5878;viaReplay=1;viaDriver=548784535;dropBefore=1;reresolvedBefore=2;"
               "ingameDelayMs=0;repair=ok;onPosture=1")
    A2_CHECK = ("ok=true;run=d9d14ee51c26;dropAfter=1;elapsedMs=2900;windowMs=1500;valid=1;onPosture=1;"
                "posture=StandingPosture;newMapped=1;reresolvedAfter=3;"
                "entered=6:2083806798,7:3388689373,7:2904452163,7:2083806798")
    AS_REPORTED = "armed=0;armMissing=oldValid"  # what the inverted check printed on both arms
    AS_ARMED = "armed=1;armMissing=-"

    def armed(self, make: str) -> str:
        return make.replace(self.AS_REPORTED, self.AS_ARMED)

    def entry(self, make: str) -> str:
        return "loaded=1\n" + self.armed(make)

    def test_the_entered_hash_is_the_state_under_test(self) -> None:
        # guards the constants above against a name-table change
        self.assertIn(str(IDLE), self.A1_CHECK)
        self.assertNotIn(str(STANDING), self.A1_CHECK + self.A2_CHECK)

    def test_a1_the_drop_on_arm_passes(self) -> None:
        code, out = run_probe(["Pablo", "--via-replay"], self.entry(self.A1_MAKE), "loaded=1\n" + self.A1_CHECK)
        self.assertEqual(code, 0, out)
        self.assertIn("verdict=PASS", out)
        self.assertNotIn("arm not armed", out)
        self.assertIn("idleRepairDupDropped: +1", out)

    def test_a2_the_control_arm_reads_a_restart(self) -> None:
        code, out = run_probe(["Pablo", "--via-replay"], self.entry(self.A2_MAKE), "loaded=1\n" + self.A2_CHECK)
        self.assertEqual(code, 4, out)
        self.assertIn("verdict=FAIL", out)
        self.assertIn("restart: standing_idle entered 2 times", out)
        self.assertIn("idleRepairDupDropped: +0", out)

    def test_the_verdict_path_alone_on_the_live_fields(self) -> None:
        passed, notes = p.verdict(p.reply(self.armed(self.A1_MAKE)), p.reply(self.A1_CHECK),
                                  "standing_idle", "standing_idle")
        self.assertTrue(passed, notes)
        passed, notes = p.verdict(p.reply(self.armed(self.A2_MAKE)), p.reply(self.A2_CHECK),
                                  "standing_idle", "standing_idle")
        self.assertFalse(passed)
        self.assertEqual([n for n in notes if n.startswith("restart:")],
                         ["restart: standing_idle entered 2 times by 1001 ms (6, 7)"])

    def test_two_entries_in_the_window_is_the_restart_rule(self) -> None:
        # A1's second standing_idle is 2360 ms after its first - outside the window, so not a restart;
        # A2's two entries are 1 ms apart. The window is RESTART_MS from each run's own requestMs.
        def idle_times(make: str, checked: str) -> list[int]:
            until = int(p.reply(self.armed(make))["requestMs"]) + p.RESTART_MS
            return [ms for ms, sid in p.entered_events(p.reply(checked)["entered"]) if sid == IDLE and ms <= until]

        self.assertEqual(idle_times(self.A1_MAKE, self.A1_CHECK), [6])
        self.assertEqual(idle_times(self.A2_MAKE, self.A2_CHECK), [6, 7])

    def test_as_reported_both_arms_are_the_exit_2_error(self) -> None:
        # the live shape with the inverted check still in the template: armed=0;armMissing=oldValid as it came back
        for make, checked in ((self.A1_MAKE, self.A1_CHECK), (self.A2_MAKE, self.A2_CHECK)):
            code, out = run_probe(["Pablo", "--via-replay"], "loaded=1\n" + make, "loaded=1\n" + checked)
            self.assertEqual(code, 2, out)
            self.assertIn("verdict=ERROR (arm not armed: oldValid)", out)
            self.assertNotIn("verdict=PASS", out)
            self.assertNotIn("verdict=FAIL", out)


class PostRArmChecks(unittest.TestCase):
    """The two checks the script runs after the R, on reply text: each names itself in
    `verdict=ERROR (arm not armed: <check>)` at exit 2, and stays quiet on the value the live arms reported."""

    ENTRY = "loaded=1\n" + LiveArmReplies.A1_MAKE.replace(LiveArmReplies.AS_REPORTED, LiveArmReplies.AS_ARMED)
    # the make read reresolvedBefore=0 and dropBefore=0; the check as A1 answered it gives +1 and newMapped=1
    CHECK = "loaded=1\n" + LiveArmReplies.A1_CHECK

    def test_reresolved_delta_zero_names_reresolved(self) -> None:
        code, out = run_probe(["Pablo", "--via-replay"], self.ENTRY, with_field(self.CHECK, reresolvedAfter="0"))
        self.assertEqual(code, 2, out)
        self.assertIn("verdict=ERROR (arm not armed: reresolved)", out)

    def test_reresolved_delta_two_is_not_one_resolve(self) -> None:
        code, out = run_probe(["Pablo", "--via-replay"], self.ENTRY, with_field(self.CHECK, reresolvedAfter="2"))
        self.assertEqual(code, 2, out)
        self.assertIn("verdict=ERROR (arm not armed: reresolved)", out)

    def test_new_mapped_zero_names_new_mapped(self) -> None:
        # the re-resolve fired but not onto the new machine (the b2-live shape): the label must be newMapped, not reresolved
        code, out = run_probe(["Pablo", "--via-replay"], self.ENTRY, with_field(self.CHECK, newMapped="0"))
        self.assertEqual(code, 2, out)
        self.assertIn("verdict=ERROR (arm not armed: newMapped)", out)

    def test_the_correct_values_do_not_error(self) -> None:
        for checked in (self.CHECK, with_field(self.CHECK, reresolvedAfter="1", newMapped="1")):
            code, out = run_probe(["Pablo", "--via-replay"], self.ENTRY, checked)
            self.assertEqual(code, 0, out)
            self.assertNotIn("arm not armed", out)


if __name__ == "__main__":
    unittest.main()
