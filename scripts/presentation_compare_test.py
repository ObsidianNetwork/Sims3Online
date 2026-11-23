"""Self-test for scripts/presentation_compare.py (no game needed).

    python scripts/presentation_compare_test.py
"""

from __future__ import annotations

import importlib.util
from pathlib import Path

spec = importlib.util.spec_from_file_location("presentation_compare", Path(__file__).resolve().parent / "presentation_compare.py")
assert spec is not None and spec.loader is not None
pc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pc)


def line(obj, desc, lot, x, posture="StandingPosture", interaction="-", machines="idle|-", right="-", container_type="-", container_id=0, sacs="-"):
    return f"{obj}|{desc}|{lot}|{x}|0|0|{posture}|{container_id}|{container_type}|{interaction}|{machines}|{right}|-|{sacs}"


def test_parse_report() -> None:
    sims = pc.parse_report("sims=2;tick=5\n" + line(1, 11, 100, 1.5, machines="sit_template|ReadBook") + "\n" + "garbage|line\n" + line(2, 22, 100, 3))
    assert set(sims) == {11, 22}, sims
    assert sims[11]["posture_machine"] == "sit_template" and sims[11]["interaction_machine"] == "ReadBook" and sims[11]["x"] == 1.5 and sims[11]["lot"] == 100


def test_compare_rules() -> None:
    s = pc.parse_report("h\n" + line(1, 11, 100, 0.0, posture="SittingPosture", interaction="Sit", machines="sit_template|ReadBook", container_type="Chair", container_id=7))[11]
    c = pc.parse_report("h\n" + line(9, 11, 100, 0.2, posture="SittingPosture", interaction="Sit", machines="sit_template|-", container_type="Chair", container_id=8))[11]
    r = pc.compare(s, c, s, c, 0.5)
    assert r["present"] and r["interaction"] and r["posture"] and r["container"], r
    assert r["container_id"] is False, "different furniture ids are reported separately"
    assert r["anim_posture"] and not r["anim_interaction"], r
    assert r["position"] is True, "at rest on both sides and within tolerance"
    moving = dict(s, x=5.0)
    assert pc.compare(moving, c, s, c, 0.5)["position"] is None, "position is only judged at rest"
    assert r["moving"] is None, "a Sim that did not walk is not judged for moving"
    assert pc.compare(moving, c, s, c, 0.5)["moving"] is False, "walked 5 m, the client is 4.8 m behind"
    trailing = dict(c, x=4.4)
    assert pc.compare(moving, trailing, s, c, 0.5)["moving"] is True, "walking, within the 1 m moving tolerance"
    assert pc.compare(s, None, None, None, 0.5)["present"] is False
    assert r["anim_state"] is None, "no recorded state on either side: not judged"
    s2 = pc.parse_report("h\n" + line(1, 11, 100, 0.0, sacs="00aa:0001"))[11]
    c2 = pc.parse_report("h\n" + line(1, 11, 100, 0.0, sacs="00aa:0002"))[11]
    assert pc.compare(s2, c2, None, None, 0.5)["anim_state"] is False
    old = pc.parse_report("h\n" + "1|11|100|0|0|0|StandingPosture|0|-|-|idle|-|-|-")
    assert old[11]["sacs"] == "-", "a reply without the recorder field still parses"


def test_only_persistent_mismatches_count() -> None:
    tally = pc.Tally()
    s = pc.parse_report("h\n" + line(1, 11, 100, 0.0, interaction="Eat"))[11]
    bad = pc.parse_report("h\n" + line(1, 11, 100, 0.0, interaction="-"))[11]
    tally.add(11, s, bad, pc.compare(s, bad, None, None, 0.5))
    assert tally.checks["interaction"]["mismatch"] == 0 and tally.checks["interaction"]["transient"] == 1
    tally.add(11, s, bad, pc.compare(s, bad, None, None, 0.5))
    assert tally.checks["interaction"]["mismatch"] == 1
    summary = tally.summary()
    assert summary["ranked"][0]["category"] == "Eat" and summary["ranked"][0]["interaction"] == 1


def test_shown_compares_hidden_flags() -> None:
    # a client's game may hide a player's Sim on a lot it does not simulate; an older mod sends no flags.
    base = line(1, 11, 100, 0.0) + "|Everyday:0"
    s = pc.parse_report("h\n" + base + "|0|high")[11]
    shown = pc.parse_report("h\n" + base + "|0|low")[11]
    hidden = pc.parse_report("h\n" + base + "|40|low")[11]
    older = pc.parse_report("h\n" + base)[11]
    assert s["hidden"] == "0" and s["lod"] == "high" and older["hidden"] == "-", (s, older)
    assert pc.compare(s, shown, s, shown, 0.5)["shown"] is True
    assert pc.compare(s, hidden, s, hidden, 0.5)["shown"] is False
    assert pc.compare(s, older, s, older, 0.5)["shown"] is None


class FakeProbe:
    def __init__(self, endpoint, readings):
        self.endpoint, self.readings, self.reads = endpoint, list(readings), 0

    def read(self, lots, sims):
        self.reads += 1
        return pc.parse_report("h\n" + self.readings.pop(0)) if self.readings else None


def test_several_clients_against_one_server_read() -> None:
    """--clients 1,2,3: the server is read once a sample; each client keeps its own tally and last reading."""
    server_lines = [line(11, 1, 5, 1.0, interaction="Sit"), line(11, 1, 5, 1.0, interaction="Sit")]
    server = FakeProbe("s", server_lines)
    one = FakeProbe("c1", [line(21, 1, 5, 1.0, interaction="Sit"), line(21, 1, 5, 1.0, interaction="Sit")])
    two = FakeProbe("c2", [line(31, 1, 5, 1.0, interaction="Chat"), line(31, 1, 5, 1.0, interaction="Chat")])
    gone = FakeProbe("c3", [])  # does not answer
    clients = [pc.Client("client 1", one, Path(".")), pc.Client("client 2", two, Path(".")), pc.Client("client 3", gone, Path("."))]
    lots: list[int] = []
    for _ in range(2):
        lots, records = pc.sample_once(server, clients, lots, [11], 0.5, 1.0)
        assert set(records) == {"client 1", "client 2"}, records
    assert server.reads == 2 and lots == [5], (server.reads, lots)
    assert clients[0].tally.checks["interaction"] == {"match": 2, "mismatch": 0, "transient": 0}
    assert clients[1].tally.checks["interaction"] == {"match": 0, "mismatch": 1, "transient": 1}
    assert clients[2].tally.samples == 0
    text = pc.scores_line(clients)
    assert text.startswith("client 1: samples=2 ") and "; client 2: samples=2 " in text and "client 3: samples=0" in text, text
    assert pc.scores_line(clients[:1]).startswith("samples=2 sim_samples=2 score="), pc.scores_line(clients[:1])


def test_a_player_s_claims_come_from_its_own_log() -> None:
    import os
    saved = {k: os.environ.pop(k, None) for k in ("SIMSONLINE_CLIENT2_BRIDGECTL", "SIMSONLINE_CLIENT3_BRIDGECTL")}
    try:
        assert pc.client_log("127.0.0.1:34030").name == "client.log"
        assert pc.client_log("127.0.0.1:34031").name == "client2.log"
        assert pc.client_log("127.0.0.1:34032").name == "client3.log"
        assert pc.client_log("10.0.0.5:34030").name == "client.log"
    finally:
        os.environ.update({k: v for k, v in saved.items() if v is not None})


CLAIM = '{{"$type":"sim_claimed","playerId":{},"simObjectId":{}}}'


def test_claims_default_to_the_booting_checkout_s_logs() -> None:
    import importlib.util
    if importlib.util.find_spec('stack_who') is None:
        return
    """Item 12 (Main, 2026-10-22): a stack another worktree booted keeps its claims in that worktree's client logs
    (stack_who.game_checkouts); a client no checkout names, or no answer at all, reads this checkout's."""
    import os
    import tempfile
    from unittest import mock

    import stack_who

    saved = {k: os.environ.pop(k, None) for k in ("SIMSONLINE_CLIENT2_BRIDGECTL", "SIMSONLINE_CLIENT3_BRIDGECTL")}
    try:
        with tempfile.TemporaryDirectory() as folder:
            own, booted = Path(folder) / "own", Path(folder) / "booted"
            for tree, logs in ((own, {"client.log": [(1, 5)]}),
                               (booted, {"client.log": [(1, 11), (2, 22)], "client2.log": [(2, 22)]})):
                (tree / ".omo" / "run").mkdir(parents=True)
                for name, claims in logs.items():
                    text = "\n".join("[ctl] " + CLAIM.format(*c) for c in claims)
                    (tree / ".omo" / "run" / name).write_text(text, encoding="utf-8")
            run_own, run_booted = own / ".omo" / "run", booted / ".omo" / "run"
            ends = ["127.0.0.1:34030", "127.0.0.1:34031"]
            answer = {"server": booted, "client": booted, "client2": booted}
            with mock.patch.object(pc, "ROOT", own), mock.patch.object(stack_who, "game_checkouts", return_value=answer) as asked:
                got = pc.default_claims(ends)
            assert asked.call_args == mock.call(own), asked.call_args
            assert got == ([11, 22], [run_booted / "client.log", run_booted / "client2.log"]), got
            with mock.patch.object(pc, "ROOT", own), mock.patch.object(stack_who, "game_checkouts", return_value={"client2": booted}):
                got = pc.default_claims(ends)  # client 1's game is not that boot's: this checkout's log
            assert got == ([5, 22], [run_own / "client.log", run_booted / "client2.log"]), got
            with mock.patch.object(pc, "ROOT", own), mock.patch.object(stack_who, "game_checkouts", side_effect=OSError("no table")):
                got = pc.default_claims(ends)
            assert got == ([5], [run_own / "client.log", run_own / "client2.log"]), got
    finally:
        os.environ.update({k: v for k, v in saved.items() if v is not None})


def test_claimed_overrides_and_no_claims_name_the_logs_read() -> None:
    import contextlib
    import io
    from unittest import mock

    class PastClaims(Exception):
        pass

    with mock.patch.object(pc, "default_claims", side_effect=AssertionError("asked stack_who")), \
            mock.patch.object(pc, "load_bridgectl", side_effect=PastClaims):
        try:
            pc.main(["--claimed", "7", "--duration", "1"])
            raise AssertionError("main went on without a bridge")
        except PastClaims:
            pass  # --claimed reached the probes without asking stack_who
    log = Path("booted") / ".omo" / "run" / "client.log"
    err = io.StringIO()
    with mock.patch.object(pc, "default_claims", return_value=([], [log])), contextlib.redirect_stderr(err):
        assert pc.main(["--duration", "1"]) == 2
    assert err.getvalue().strip() == f"no player Sims in {log}: pass --claimed or start a client that claims a Sim", err.getvalue()


if __name__ == "__main__":
    for name, fn in list(globals().items()):
        if name.startswith("test_") and callable(fn):
            fn()
            print(f"PASS {name}")
