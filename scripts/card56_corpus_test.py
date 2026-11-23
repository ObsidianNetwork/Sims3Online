"""card56_corpus builds card 56 gate 6's live corpus from the running game's ids (python -m unittest
scripts.card56_corpus_test).

Every test runs against a fake bridge: nothing here connects to a port. The committed sample
(rust/simlink_bridge/fixtures/live_corpus_sample.txt) is this fake world's output; the crate's tests parse it with
the Rust corpus parser and encode every row.
"""
from __future__ import annotations

import contextlib
import io
import re
import sys
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
REPO = SCRIPTS.parent
sys.path.insert(0, str(SCRIPTS))
import card56_corpus as cc  # noqa: E402

SAMPLE = REPO / "rust" / "simlink_bridge" / "fixtures" / "live_corpus_sample.txt"
NOW = datetime(2026, 11, 1, 9, 30, 15, tzinfo=timezone.utc)

DIAG = ("role=server;inWorld=True;tick=4100;autonomyWatch=1001;commandGate=1002:free;pendingOutbound=0;pumpTicks=9;"
        "probeLoads=0;probeReuses=0;dispatch=61;commandedSpeed={speed};observedSpeed={observed};clockDay={day};"
        "clockHours={hours};clockMinutes={minutes};clockTicks=123;clockSets=0/0;clockLastSetMin=0;hudClockDueMin=0;"
        "swallowed=0")
PRESENTATION = "\n".join([
    "sims=4;tick=4100",
    "1001|501|77|1.5|0|2.25|Standing|0|-|-|-|-|-|-|-|Everyday:0|0",
    "1002|502|77|3|0|4|Standing|0|-|-|-|-|-|-|-|Everyday:0|0",
    "1004|504|77|10.125|0.5|12|Sitting|4444|ChairDining|-|-|-|-|-|-|Everyday:0|0",
    "1003|503|0|20|0|21|Standing|0|-|-|-|-|-|-|-|Everyday:0|0",
])
WALK = "root=Sims3.Gameplay.CAS.Household;path=SimDescription.Household\nmHouseholdId = 5555\nmName = Goth"


class FakeBridge:
    """The listener's JSON answers, keyed by request (name and args); records every request it was sent.

    The default world is a *paused* server (observedSpeed=Pause): gate 6 writes a corpus only on one, because
    `SetWorldTime`'s six rows carry a clock read while the world stands still (card 56, c9a23656, Main's Q1).
    """

    def __init__(self, speed: str = "Pause", observed: str | None = None, role: str = "server",
                 in_world: str = "True", clock: tuple[str, str, str] = ("3", "14", "25")):
        day, hours, minutes = clock
        diag = DIAG.format(speed=speed, observed=observed or speed, day=day, hours=hours, minutes=minutes)
        diag = diag.replace("role=server", f"role={role}")
        diag = diag.replace("inWorld=True", f"inWorld={in_world}")
        self.answers = {
            ("DIAG_STATE",): diag,
            ("PROBE_PRESENTATION",): PRESENTATION,
            ("DIAG_WALK", "1003", cc.hexenc("SimDescription.Household"), "1"): WALK,
        }
        self.sent: list[tuple[str, ...]] = []

    def request(self, name: str, *args: str) -> dict:
        key = (name, *args)
        self.sent.append(key)
        if key not in self.answers:
            return {"ok": True, "accepted": False, "reason": f"unexpected {key}"}
        return {"ok": True, "accepted": True, "report": self.answers[key]}


def counter():
    n = iter(range(1, 10_000))
    return lambda: f"{0x05600000000000000000000000000000 + next(n):032x}"


def run(argv: list[str], bridge: FakeBridge | None = None) -> tuple[int, str, list]:
    connected = []

    def connect(endpoint: str, timeout: float):
        connected.append(endpoint)
        return bridge or FakeBridge()

    err = io.StringIO()
    with contextlib.redirect_stderr(err):
        code = cc.main(argv, connect=connect, now=lambda: NOW, new_corr=counter())
    return code, err.getvalue(), connected


def declared_records() -> list[str]:
    source = (REPO / "DedicatedServer" / "HostBridge" / "BridgeCommand.cs").read_text(encoding="utf-8")
    return [m.group(1) for m in re.finditer(r"public sealed record (\w+)\((?:[^;{])*?\) : BridgeCommand\(CorrelationId\)",
                                            source)]


class Base(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self.tmp.name)
        self.saves = self.dir / "Saves"
        self.saves.mkdir()
        (self.saves / "sim198-aside.sims3").mkdir()
        self.out = self.dir / "csharp_corpus.txt"

    def tearDown(self):
        self.tmp.cleanup()

    def argv(self, *extra: str, half: str = "csharp") -> list[str]:
        return ["--live", "--half", half, "--saves-dir", str(self.saves), "--loaded-save", "sim198-aside.sims3",
                "--out", str(self.out), *extra]

    def build(self, bridge: FakeBridge | None = None, half: str = "csharp") -> list[str]:
        code, err, _ = run(self.argv(half=half), bridge)
        self.assertEqual(code, 0, err)
        return self.out.read_text(encoding="utf-8").splitlines()


def rows(lines: list[str]) -> list[list[str]]:
    return [line.split("\t") for line in lines if line and not line.startswith("#")]


def args_of(lines: list[str], verb: str) -> list[list[str]]:
    """The arguments of every row of one verb, in send order (a verb can have several: the ruling's SetWorldTime)."""
    return [r[2:] for r in rows(lines) if r[0] == verb]


class RefusalTests(Base):
    def test_without_live_it_refuses_before_connecting(self):
        argv = [a for a in self.argv() if a != "--live"]
        code, err, connected = run(argv)
        self.assertEqual(code, 2)
        self.assertIn("--live", err)
        self.assertEqual(connected, [])
        self.assertFalse(self.out.exists())

    def test_a_non_loopback_endpoint_is_refused_before_connecting(self):
        for endpoint in ("10.0.0.5:34029", "example.com:34029"):
            code, err, connected = run(self.argv("--endpoint", endpoint))
            self.assertEqual(code, 2, endpoint)
            self.assertIn("loopback", err)
            self.assertEqual(connected, [])

    def test_an_existing_save_with_the_scratch_name_is_refused(self):
        name = cc.scratch_name("csharp", NOW)
        for existing in (name, name + ".sims3", name.upper() + ".SIMS3"):
            with self.subTest(existing=existing):
                path = self.saves / existing
                path.mkdir()
                code, err, connected = run(self.argv())
                path.rmdir()
                self.assertEqual(code, 2)
                self.assertIn("already exists", err)
                self.assertEqual(connected, [])
                self.assertFalse(self.out.exists())

    def test_the_loaded_save_is_refused(self):
        name = cc.scratch_name("csharp", NOW)
        for loaded in (name, name + ".sims3", name.upper()):
            with self.subTest(loaded=loaded):
                argv = self.argv()
                argv[argv.index("--loaded-save") + 1] = loaded
                code, err, connected = run(argv)
                self.assertEqual(code, 2)
                self.assertIn("loaded save", err)
                self.assertEqual(connected, [])

    def test_a_missing_saves_folder_is_refused(self):
        argv = self.argv()
        argv[argv.index("--saves-dir") + 1] = str(self.dir / "nowhere")
        code, err, _ = run(argv)
        self.assertEqual(code, 2)
        self.assertIn("saves", err)

    def test_a_client_role_or_out_of_world_game_is_refused(self):
        for bridge in (FakeBridge(role="client"), FakeBridge(in_world="False")):
            code, err, _ = run(self.argv(), bridge)
            self.assertEqual(code, 2, err)
            self.assertFalse(self.out.exists())

    def test_a_running_clock_is_refused_with_the_line_that_pauses_it(self):
        # Main's Q1 (2026-11-01): SetWorldTime's six rows carry a T read while the world stands still, so the tool
        # writes a half's corpus only on a paused server, and refuses with the line that pauses it otherwise. It
        # does not fall back to a `#@unsent SetWorldTime` row: the card's rule is that every verb is sent.
        for bridge in (FakeBridge(speed="Normal"), FakeBridge(speed="Normal", observed="Double"),
                       FakeBridge(speed="Pause", observed="Normal")):
            with self.subTest(commanded=bridge.answers[("DIAG_STATE",)].split("commandedSpeed=")[1].split(";")[0]):
                code, err, _ = run(self.argv(), bridge)
                self.assertEqual(code, 2, err)
                self.assertIn("observedSpeed=", err)
                self.assertIn("SET_GAME_SPEED Pause", err)
                self.assertNotIn("#@unsent SetWorldTime", err)
                self.assertFalse(self.out.exists())

    def test_a_paused_server_with_no_clock_read_is_refused_too(self):
        # The clock fields come from the same DIAG_STATE; without them there is no T to write into the rows.
        code, err, _ = run(self.argv(), FakeBridge(clock=("", "", "")))
        self.assertEqual(code, 2, err)
        self.assertIn("clock", err)
        self.assertFalse(self.out.exists())


class CorpusTests(Base):
    def test_every_declared_verb_has_a_row_on_a_paused_world(self):
        # Each half's corpus carries every verb that half sends. The Rust half sends all 60 on a paused world; the
        # C# half splits them over its two corpora, step (a)'s and step (d)'s (card 56 gate 6, c9a23656 row 208).
        # SetWorldTime is one verb in six rows, so the count is rows, not verbs.
        sent = [r[0] for r in rows(self.build())]
        declared = declared_records()
        self.assertEqual(len(declared), 60)
        rust = [r[0] for r in rows(self.build(half="rust"))]
        self.assertEqual(set(rust), set(declared), "a paused Rust half sends every declared verb")
        self.assertEqual(len(rust), 65, "60 verbs, six rows where SetWorldTime's one row was")
        step_d = [r[0] for r in rows(self.build(half="csharp2"))]
        # Both C# corpora end with their own DISCONNECT (two processes), so the union is what the Rust half sends.
        self.assertEqual(sorted(set(sent) | set(step_d)), sorted(set(rust)),
                         "the two C# corpora together send what the Rust half does")
        self.assertEqual([line for line in self.build(half="rust") if line.startswith("#@unsent ")], [])

    def test_the_three_undone_verbs_carry_the_cards_literal_arguments(self):
        # Card 56 gate 6 (c9a23656 row 208): AnswerDialog(RequestId = 0, Answer = null), SetSimScope(LotIds = [],
        # SimObjectIds = []) and SetAuthority(Mode = Observe, OwnedSimObjectIds = []), in the corpus codec's own
        # form (src/corpus.rs: the token `~` is null, an empty token is an empty id list).
        found = {r[0]: r[2:] for r in rows(self.build(half="rust"))}
        self.assertEqual(found["AnswerDialog"], ["0", "~"])
        self.assertEqual(found["SetSimScope"], ["", ""])
        self.assertEqual(found["SetAuthority"], ["Observe", ""])

    def test_the_rust_half_sends_the_three_after_load_save_menu_and_before_disconnect(self):
        sent = [r[0] for r in rows(self.build(half="rust"))]
        self.assertEqual(sent[-7:], ["SaveGame", "LoadSave", "LoadSaveViaMenu",
                                     "AnswerDialog", "SetSimScope", "SetAuthority", "Disconnect"])

    def test_the_first_csharp_corpus_does_not_carry_the_three(self):
        # The C# half sends them in step (d) (card 56 gate 6), so record 1 holds no row for them.
        sent = [r[0] for r in rows(self.build())]
        self.assertEqual(sent[-4:], ["SaveGame", "LoadSave", "LoadSaveViaMenu", "Disconnect"])
        for verb in ("AnswerDialog", "SetSimScope", "SetAuthority"):
            self.assertNotIn(verb, sent)

    def test_the_ruling_moves_the_three_off_the_no_safe_form_list(self):
        # Card 56 gate 6 (c9a23656): no verb is unsendable by its own nature any more, and a running clock is a
        # refusal now, not an unsent line: only the split commanded/observed speed leaves a verb unsent.
        def unsent(lines: list[str]) -> set[str]:
            return {line.split(" ")[1] for line in lines if line.startswith("#@unsent ")}

        self.assertEqual(cc.NO_SAFE_FORM, {})
        self.assertEqual(unsent(self.build()), set())
        self.assertEqual(unsent(self.build(half="rust")), set())
        self.assertEqual(unsent(self.build(FakeBridge(speed="Normal", observed="Pause"))), {"SetGameSpeed"})

    def test_setworldtime_is_the_six_rows_the_ruling_gives_it(self):
        # Card 56 gate 6 (c9a23656 row 208), Main's Q1: read the speed, pause, read the time T, set T plus one
        # game minute, set T again, restore the speed. Six rows, in the place the single SetWorldTime row had.
        sent = [r[0] for r in rows(self.build())]
        self.assertEqual(sent[:7], ["SetGameSpeed", "DiagState", "SetGameSpeed", "DiagState", "SetWorldTime",
                                    "SetWorldTime", "SetGameSpeed"])
        self.assertEqual(sent[7], "RequestSnapshot", "the rest of the half keeps its order")
        self.assertEqual(sent.count("SetWorldTime"), 2, "T plus one minute, then T")
        self.assertEqual(sent.count("DiagState"), 3, "the two reads inside the sequence and the verb's own row")
        self.assertEqual(sent.count("SetGameSpeed"), 3, "the verb's own restore, the pause, the sequence's restore")
        self.assertEqual([r[0] for r in rows(self.build(half="rust"))][:7], sent[:7],
                         "both halves send the same six rows in the same place")

    def test_the_load_verbs_come_after_the_scratch_save_and_disconnect_is_last(self):
        sent = [r[0] for r in rows(self.build())]
        self.assertEqual(sent[-4:], ["SaveGame", "LoadSave", "LoadSaveViaMenu", "Disconnect"])
        self.assertLess(sent.index("DisableAutonomy"), sent.index("EnableAutonomy"))
        self.assertLess(sent.index("GateAutonomy"), sent.index("UngateAutonomy"))

    def test_the_save_and_load_verbs_name_this_halfs_own_scratch_save(self):
        name = cc.scratch_name("rust", NOW) + ".sims3"
        self.assertTrue(name.startswith("card56-gate6-rust-20261101T093015Z"))
        found = {r[0]: r[2:] for r in rows(self.build(half="rust"))}
        for verb in ("SaveGame", "LoadSave", "LoadSaveViaMenu"):
            self.assertEqual(found[verb], [name], verb)

    def test_the_halfs_chosen_and_read_values_are_marked_for_pairing(self):
        lines = self.build()
        marks = {line for line in lines if line.startswith("#@blank ")}
        self.assertEqual(marks, {"#@blank SAVE_GAME 2", "#@blank LOAD_SAVE 2", "#@blank LOAD_SAVE_MENU 2",
                                 "#@blank SET_GAME_SPEED 2", "#@blank SET_WORLD_TIME 2,3,4", "#@blank SHOW_DIALOG 2"})
        # The six rows need no new mark: both verbs' fields are already marked, per verb.
        self.assertEqual(len([line for line in lines if line.startswith("#@blank ")]), 6)

    def test_speed_and_clock_restore_the_values_read(self):
        lines = self.build()
        self.assertEqual(args_of(lines, "SetGameSpeed"), [["Pause"], ["Pause"], ["Pause"]],
                         "the verb's own restore, the sequence's pause, the sequence's restore of what it read")
        self.assertEqual(args_of(lines, "SetWorldTime"), [["3", "14", "26"], ["3", "14", "25"]],
                         "T plus one game minute, then T again")
        # A split speed has no row of its own (no one value restores both fields), but the clock sequence still
        # pauses and restores the observed speed the read reported.
        split = self.build(FakeBridge(speed="Normal", observed="Pause"))
        self.assertEqual(args_of(split, "SetGameSpeed"), [["Pause"], ["Pause"]])
        self.assertTrue(any(line.startswith("#@unsent SetGameSpeed ") and "observedSpeed=Pause" in line
                            for line in split))

    def test_at_23_59_the_sequence_moves_the_minute_back_and_the_day_never_changes(self):
        # Card 56 gate 6 (c9a23656 row 208): "at 23:59, T minus one minute instead, so the day never changes and
        # at most one minute's events are crossed". Any other :59 rolls the hour forward, still in the same day.
        self.assertEqual(args_of(self.build(FakeBridge(clock=("5", "23", "59"))), "SetWorldTime"),
                         [["5", "23", "58"], ["5", "23", "59"]])
        self.assertEqual(args_of(self.build(FakeBridge(clock=("5", "14", "59"))), "SetWorldTime"),
                         [["5", "15", "0"], ["5", "14", "59"]])
        self.assertEqual(args_of(self.build(FakeBridge(clock=("0", "0", "0"))), "SetWorldTime"),
                         [["0", "0", "1"], ["0", "0", "0"]])
        for lines in (self.build(FakeBridge(clock=("5", "23", "59"))), self.build(FakeBridge(clock=("5", "14", "59")))):
            for row in rows(lines):
                if row[0] == "SetWorldTime":
                    self.assertEqual(row[2], "5", "the day the read reported, never moved")
                    self.assertLessEqual(int(row[4]), 59, "a minute the mod would refuse (SET_WORLD_TIME 0..59)")

    def test_the_autonomy_pairs_use_a_sim_outside_the_controllers_watch_and_gate(self):
        found = {r[0]: r[2:] for r in rows(self.build())}
        self.assertEqual(found["DisableAutonomy"], ["1003", "0"])
        self.assertEqual(found["EnableAutonomy"], ["1003", "0"])
        self.assertEqual(found["GateAutonomy"], ["1003"])
        self.assertEqual(found["UngateAutonomy"], ["1003", "0"])
        self.assertEqual(found["ProbeHouseholdCash"], ["5555"])
        self.assertEqual(found["MergeLotState"][0], "77", "a lot read from the game")

    def test_no_row_carries_a_value_the_listener_cannot(self):
        for row in rows(self.build()):
            for token in row:
                self.assertNotEqual(token, "-", row)
                self.assertTrue(token.isascii(), row)

    def test_the_reads_go_through_read_only_verbs_only(self):
        for half in ("csharp", "rust", "csharp2"):
            with self.subTest(half=half):
                bridge = FakeBridge()
                self.build(bridge, half=half)
                self.assertEqual({key[0] for key in bridge.sent}, {"DIAG_STATE", "PROBE_PRESENTATION", "DIAG_WALK"})

    def test_the_fake_worlds_corpus_is_the_committed_sample_the_crate_parses(self):
        # The sample is the Rust half's corpus on a paused world: every declared verb has a row, no `#@unsent`.
        self.assertEqual(self.out.parent, self.dir)
        produced = "\n".join(self.build(FakeBridge(speed="Pause"), half="rust")) + "\n"
        self.assertEqual(produced, SAMPLE.read_text(encoding="utf-8").replace("\r\n", "\n"))


class StepDCorpusTests(Base):
    """Gate 6 step (d)'s C# corpus (card 56, c9a23656 row 208): the three verbs, then DISCONNECT, for the second
    server process, which saves nothing and so names no scratch save."""

    def argv(self, *extra: str, half: str = "csharp2") -> list[str]:
        return ["--live", "--half", half, "--out", str(self.out), *extra]

    def build(self, bridge: FakeBridge | None = None, half: str = "csharp2") -> list[str]:
        code, err, _ = run(self.argv(half=half), bridge)
        self.assertEqual(code, 0, err)
        return self.out.read_text(encoding="utf-8").splitlines()

    def test_the_step_d_corpus_is_the_three_and_disconnect(self):
        lines = self.build()
        self.assertEqual([r[0] for r in rows(lines)], ["AnswerDialog", "SetSimScope", "SetAuthority", "Disconnect"])
        self.assertEqual({r[0]: r[2:] for r in rows(lines)}["SetAuthority"], ["Observe", ""])
        self.assertEqual([line for line in lines if line.startswith(("#@unsent", "#@blank"))], [])

    def test_the_step_d_corpus_needs_no_saves_dir(self):
        # It writes no SAVE_GAME, so it names no scratch save and asks for no saves folder.
        lines = self.build()
        self.assertFalse(any("scratch save" in line for line in lines))
        self.assertFalse(any(r[0] in ("SaveGame", "LoadSave", "LoadSaveViaMenu") for r in rows(lines)))
        for half in ("csharp", "rust"):
            code, err, connected = run(["--live", "--half", half, "--out", str(self.out)])
            self.assertEqual(code, 2, half)
            self.assertIn("--saves-dir", err)
            self.assertEqual(connected, [])

    def test_the_step_d_corpus_needs_no_paused_clock(self):
        # It carries no SetWorldTime row, so a running clock costs step (d) nothing: the second process sends the
        # three literals and its own DISCONNECT and saves nothing, leaving the time where it found it.
        code, err, _ = run(self.argv(), FakeBridge(speed="Normal"))
        self.assertEqual(code, 0, err)
        self.assertEqual([r[0] for r in rows(self.out.read_text(encoding="utf-8").splitlines())],
                         ["AnswerDialog", "SetSimScope", "SetAuthority", "Disconnect"])

    def test_the_step_d_corpus_still_refuses_without_live(self):
        code, err, connected = run(["--half", "csharp2", "--out", str(self.out)])
        self.assertEqual(code, 2)
        self.assertEqual(connected, [])
        self.assertFalse(self.out.exists())


if __name__ == "__main__":
    unittest.main()
