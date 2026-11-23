"""Unit tests for replay_check: python -m unittest scripts.replay_check_test"""
from __future__ import annotations

import sys
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import replay_check as rc  # noqa: E402
import sacs_names  # noqa: E402

PABLO = "18031287978496800272"
# The 2026-10-21 rows (main session's transcript): Pablo's bed relax, server driver 548318810, the same two server
# requests refused on both clients.
CLIENT1_FAILURES = """accepted=True
failures=2
kGenericFailure|client=544597952|6859|R|548318810|84696423|2707378478|64|18031287978496800272|0|0|0|0
kGenericFailure|client=544597952|6820|R|548318810|84696423|577793763|2117|18031287978496800272|0|0|0|0
"""
CLIENT2_FAILURES = """accepted=True
failures=2
kGenericFailure|client=546211878|6859|R|548318810|84696423|2707378478|64|18031287978496800272|0|0|0|0
kGenericFailure|client=546211878|6820|R|548318810|84696423|577793763|2117|18031287978496800272|0|0|0|0
"""
BED = "a1b2c3d4e5f60718"
IDLE = "00000000000000aa"
SERVER_ACTOR = f"""accepted=True
machines=2
548318810|{BED}|2707378478|6859|0|1
548000001|{IDLE}|111|6000|0|1
"""


class FakeNames:
    """sacs_names with a few names known (the real table lives in the simscope checkout's .omo/cache)."""

    def name64(self, key):
        text = format(key, "016x") if isinstance(key, int) else key  # the caller now hands a hex-parsed int
        return {BED: "BedRelax", IDLE: "Idle"}.get(text, text)

    def name32(self, value):
        return {"84696423": "x", "2707378478": "Relax", "577793763": "Enter", "111": "standing_idle"}.get(str(value), str(value))

    def flags(self, value):
        return sacs_names.flags(value)


class Counters(unittest.TestCase):
    STATUS = ("accepted=True\nstatus;replayDrivers=14;borrowedNow=1;acquiredOnSims=3;ownRepairs=2;handovers=1;"
              "on=1;calls=9876")
    # PresenterHook.Report through the mod (';' made ','), and the authority verb's line (AuthorityHook field names).
    PRESENTER = ("accepted=True\npresenterIdle=status,on=1,postures=applied:7,unchanged:40,cleared:1,failures:0,"
                 "settles=3,skipped:2,restore=armed:0,reset:0")
    AUTHORITY = "accepted=True\nauthority;Mode=1;Checked=812;Blocked=4;AllowedOwned=700;HeartbeatSkipped=9;FundsBlocked=0"

    def test_parse_both_replies(self) -> None:
        self.assertEqual(rc.parse_counters(self.STATUS, CLIENT1_FAILURES),
                         {"replayDrivers": 14, "borrowedNow": 1, "acquiredOnSims": 3, "ownRepairs": 2, "handovers": 1,
                          "failures": 2})
        self.assertEqual(rc.parse_counters("accepted=True\nstatus;on=1", "ERROR: no reply"), {})  # an older mod

    def test_presenter_authority_and_sessions(self) -> None:
        got = rc.parse_counters(self.STATUS + ";sessions=3;warmResets=2;joinResets=4", "failures=0\n", self.PRESENTER,
                                self.AUTHORITY)
        self.assertEqual({k: got[k] for k in rc.SESSIONS + rc.JOINS + rc.PRESENTER},
                         {"sessions": 3, "warmResets": 2, "joinResets": 4, "postures.applied": 7, "postures.unchanged": 40,
                          "postures.cleared": 1, "postures.failures": 0, "settles": 3, "settles.skipped": 2})
        self.assertEqual((got["authority.Blocked"], got["authority.Checked"], got["authority.Mode"]), (4, 812, 1))
        # the server (no presenter) and a mod without the verb: those counters are left out, not zero
        bare = rc.parse_counters(self.STATUS, "failures=0\n", "ERROR: presenter-idle: client only",
                                 "accepted=True\nusage: SACS_RECORDER status|actor|replay-failures")
        self.assertFalse([k for k in bare if k.startswith(("postures", "settles", "authority", "sessions"))])
        moved = rc.parse_counters(self.STATUS, "failures=0\n", self.PRESENTER.replace("applied:7", "applied:9"),
                                  self.AUTHORITY.replace("Checked=812;Blocked=4", "Checked=900;Blocked=5"))
        change = rc.counter_change(rc.parse_counters(self.STATUS, "failures=0\n", self.PRESENTER, self.AUTHORITY), moved)
        self.assertEqual(rc.change_text(change), "postures.applied 7->9 (+2), authority.Blocked 4->5 (+1)")

    def test_the_idle_repair_counters_are_totals(self) -> None:
        # B2+(a): the repair's entries and RepairEntryGate's drops, beside ownRepairs in SACS_RECORDER status
        got = rc.parse_counters(self.STATUS + ";idleRepairDupDropped=3;idleRepairStream=4;idleRepairDefault=1", "failures=0\n")
        names = ("idleRepairStream", "idleRepairDefault", "idleRepairDupDropped")
        self.assertEqual({k: got[k] for k in names}, {"idleRepairStream": 4, "idleRepairDefault": 1, "idleRepairDupDropped": 3})
        for name in names:
            self.assertIn(name, rc.TOTALS)
            self.assertIn(name, rc.MONOTONIC)
        self.assertTrue(rc.restarted({"idleRepairDupDropped": 3}, {"idleRepairDupDropped": 0}))
        self.assertFalse(any(k in rc.parse_counters(self.STATUS, "failures=0\n") for k in names))  # an older mod

    def test_the_change_names_only_what_moved(self) -> None:
        before = {"replayDrivers": 14, "failures": 2, "handovers": 1}
        after = {"replayDrivers": 16, "failures": 4, "handovers": 1}
        change = rc.counter_change(before, after)
        self.assertEqual(change, {"failures": (2, 4), "replayDrivers": (14, 16)})
        self.assertEqual(rc.change_text(change), "failures 2->4 (+2), replayDrivers 14->16 (+2)")
        self.assertEqual(rc.change_text({}), "no change")
        self.assertEqual(rc.counter_change({"error": "down"}, after), {})

    def test_a_client_that_began_again_says_so_instead_of_negatives(self) -> None:
        # 2026-10-21, late-join-bed under --rejoin cold: client 2's TS3 restarted inside the scenario
        before = {"counters": {"client2": {"failures": 2, "handovers": 1, "authority.Blocked": 164, "replayDrivers": 3},
                               "client1": {"failures": 0, "replayDrivers": 3}}}
        after = {"counters": {"client2": {"failures": 0, "handovers": 0, "authority.Blocked": 16, "replayDrivers": 2},
                              "client1": {"failures": 0, "replayDrivers": 2}}}
        got = rc.boundary_changes(before, after)
        self.assertEqual(got["client2"], "restarted in between (a count fell: its TS3 began again); since then: "
                                         "failures=0 handovers=0 replayDrivers=2 authority.Blocked=16")
        self.assertEqual(got["client1"], "replayDrivers 3->2 (-1)")  # a gauge may fall: no restart
        self.assertFalse(rc.restarted({"error": "down"}, after["counters"]["client2"]))

    def test_read_counters_pairs_each_endpoint_s_replies(self) -> None:
        replies = {("a", "status"): (0, self.STATUS), ("a", "replay-failures"): (0, CLIENT1_FAILURES),
                   ("a", "presenter-idle", "status"): (0, self.PRESENTER), ("a", "authority"): (0, self.AUTHORITY),
                   ("b", "status"): (1, "ERROR: connection refused")}
        with mock.patch.object(rc, "bridge", side_effect=lambda e, _, *cmd: replies.get((e, *cmd), (1, "ERROR: refused"))):
            got = rc.read_counters(["a", "b"])
        self.assertEqual((got["a"]["failures"], got["a"]["postures.applied"], got["a"]["authority.Blocked"]), (2, 7, 4))
        self.assertEqual(got["b"], {"error": "ERROR: connection refused"})


class Drivers(unittest.TestCase):
    def test_two_live_drivers_of_one_machine_are_a_double(self) -> None:
        text = f"""machines=4
549090747|{IDLE}|111|100|0|1
551000002|{IDLE}|111|120|0|1
548000003|{IDLE}|111|90|0|0
548000004|{BED}|5|130|1|1
"""
        rows = rc.actor_rows(text)
        self.assertEqual(len(rows), 4)
        found = rc.doubles(rows)
        self.assertEqual(list(found), [IDLE])  # the dead row and the cosmetic layer do not count
        self.assertEqual(rc.double_text("client1", "Pablo", IDLE, found[IDLE], FakeNames()),
                         "client1 Pablo: Idle x2 (driver:state 549090747:standing_idle 551000002:standing_idle)")

    def test_the_origin_column_names_who_acquired_each_driver(self) -> None:
        text = f"""machines=2
549090747|{IDLE}|111|100|0|1|replay:SACS_REPLAY
551000002|{IDLE}|111|120|0|1|game via Sim.PostureChanged/Idle.Start
"""
        rows = rc.actor_rows(text)
        self.assertEqual([r["origin"] for r in rows], ["replay:SACS_REPLAY", "game via Sim.PostureChanged/Idle.Start"])
        self.assertEqual(rc.double_text("client1", "Pablo", IDLE, rc.doubles(rows)[IDLE], FakeNames()),
                         "client1 Pablo: Idle x2 (driver:state 549090747:standing_idle (replay:SACS_REPLAY) "
                         "551000002:standing_idle (game via Sim.PostureChanged/Idle.Start))")
        self.assertEqual(rc.actor_rows(f"1|{IDLE}|111|1|0|1\n")[0]["origin"], "")  # a mod before the column

    def test_a_reply_without_the_alive_column_counts_every_row_alive(self) -> None:
        rows = rc.actor_rows(f"1|{IDLE}|111|1|0\n2|{IDLE}|112|2|0\n")
        self.assertEqual(len(rc.doubles(rows)[IDLE]), 2)

    def test_the_check_reads_every_game_and_names_the_silent_ones(self) -> None:
        double = f"machines=2\n1|{IDLE}|111|1|0|1\n2|{IDLE}|111|2|0|1\n"
        single = f"machines=1\n1|{IDLE}|111|1|0|1\n"

        def fake(endpoint, *args):
            if endpoint == "c2":
                return 1, "ERROR: connection refused"
            return 0, double if (endpoint, args[-1]) == ("c1", PABLO) else single

        with mock.patch.object(rc, "bridge", side_effect=fake), mock.patch.object(rc, "_names", return_value=FakeNames()):
            findings, silent = rc.drivers_check({"server": "s", "client1": "c1", "client2": "c2"},
                                                {"Jennifer": "1", "Pablo": PABLO})
        self.assertEqual(findings, ["client1 Pablo: Idle x2 (driver:state 1:standing_idle 2:standing_idle)"])
        self.assertEqual(silent, ["client2"])


class Snapshot(unittest.TestCase):
    def test_counters_and_drivers_in_one_round_trip(self) -> None:
        asked: list[tuple] = []
        double = f"machines=2\n1|{IDLE}|111|1|0|1\n2|{IDLE}|111|2|0|1\n"

        def fake(endpoint, *args):
            asked.append((endpoint, *args))
            if args[1:] == ("status",):
                return 0, "status;replayDrivers=3;borrowedNow=0;acquiredOnSims=1;ownRepairs=0;handovers=2"
            if args[1:] == ("replay-failures",):
                return 0, "failures=1\n"
            if args[1] in ("presenter-idle", "authority"):
                return 0, "accepted=True\nusage: SACS_RECORDER status"  # a mod before them: nothing read
            return 0, double if endpoint == "127.0.0.1:34030" else "machines=0\n"

        with mock.patch.object(rc, "bridge", side_effect=fake), mock.patch.object(rc, "_names", return_value=FakeNames()):
            before = rc.snapshot(["127.0.0.1:34030"], None, {"Pablo": PABLO})
            after = rc.snapshot(["127.0.0.1:34030"], {"server": rc.SERVER, "client1": "127.0.0.1:34030"}, {"Pablo": PABLO})
        self.assertEqual(before, {"counters": {"client1": {"replayDrivers": 3, "borrowedNow": 0, "acquiredOnSims": 1,
                                                           "ownRepairs": 0, "handovers": 2, "failures": 1}}})
        self.assertEqual(after["doubles"], ["client1 Pablo: Idle x2 (driver:state 1:standing_idle 2:standing_idle)"])
        self.assertEqual(len(asked), 4 + 6)  # before: 4 counter reads; after: 4 counter reads + 2 actor reads
        self.assertEqual(rc.boundary_changes(before, after), {"client1": "no change"})


class Failures(unittest.TestCase):
    def test_the_2026_10_21_rows_parse(self) -> None:
        total, rows = rc.parse_failures(CLIENT1_FAILURES)
        self.assertEqual(total, 2)
        self.assertEqual([(r["seq"], r["server_driver"], r["client_driver"], r["state"], r["flags"], r["actor"]) for r in rows],
                         [("6859", "548318810", "544597952", "2707378478", "64", PABLO),
                          ("6820", "548318810", "544597952", "577793763", "2117", PABLO)])

    def test_the_same_server_line_on_both_clients_is_said_once_as_the_live_path(self) -> None:
        lines = rc.decode_failures({"client1": CLIENT1_FAILURES, "client2": CLIENT2_FAILURES}, {PABLO: SERVER_ACTOR},
                                   sims={"Pablo": PABLO}, names=FakeNames())
        self.assertEqual(lines[:2], ["client1: failures=2, 2 shown", "client2: failures=2, 2 shown"])
        self.assertEqual(len(lines), 4)
        self.assertEqual(lines[2], "  seq 6859 kGenericFailure Pablo BedRelax x->Relax [RequestEmulateEdith], server driver "
                                   "548318810, client drivers client1:544597952 client2:546211878, on client1 and client2: "
                                   "the live path they share")
        self.assertIn("seq 6820 kGenericFailure Pablo BedRelax x->Enter [", lines[3])

    def test_one_client_alone_and_the_client_s_own_machine_when_the_server_lacks_it(self) -> None:
        client_actor = {("client1", PABLO): f"machines=1\n544597952|{BED}|2707378478|10|0|1\n"}
        lines = rc.decode_failures({"client1": CLIENT1_FAILURES, "client2": "failures=0\n"}, {PABLO: "machines=0\n"},
                                   client_actor, {"Pablo": PABLO}, FakeNames())
        self.assertEqual(lines[1], "client2: failures=0, 0 shown")
        self.assertTrue(lines[2].endswith("on client1 only"), lines[2])
        self.assertIn(" BedRelax ", lines[2])
        unknown = rc.decode_failures({"client1": CLIENT1_FAILURES}, {}, {}, {}, FakeNames())
        self.assertIn(f"{PABLO} driver 548318810 x->Relax", unknown[1])  # no actor rows anywhere: the driver, the id


NOW = 1_800_000_000.0


def degraded_facts() -> dict:
    """The 024712 session's shape: 2.6 h up, client 1 rejoined warm twice, one hot module load, a double on client 1."""
    return {"now": NOW, "down": ["client2"],
            "processes": {"server": {"pid": 11, "started": NOW - 2.6 * 3600, "attached": False},
                          "client1": {"pid": 22, "started": NOW - 2.5 * 3600, "attached": True}},
            "games": {"server": {"counters": {"sessions": 1, "warmResets": 0, "replayDrivers": 0}, "loads": None},
                      "client1": {"counters": {"sessions": 3, "warmResets": 2, "failures": 2, "replayDrivers": 14},
                                  "loads": ("SimsOnlinePresentation_r7", 2)}},
            "doubles": ["client1 Pablo: Idle x2 (driver:state 1:standing_idle 2:standing_idle)"], "silent": []}


class Health(unittest.TestCase):
    def test_module_loads(self) -> None:
        self.assertEqual(rc.module_loads("accepted=True\nreplay;presentation=module=SimsOnlinePresentation/loads:1/kinds:7"),
                         ("SimsOnlinePresentation", 1))
        self.assertIsNone(rc.module_loads("accepted=True\nreplay;batch=0"))

    def test_the_degraded_session_says_a_fresh_boot_is_due_and_why(self) -> None:
        lines, due = rc.health_lines(degraded_facts())
        self.assertEqual(due, ["server up 2.6 h (limit 2.5 h)", "client1 up 2.5 h (limit 2.5 h)",
                               "client1 2 warm rejoins (limit 2)", "client1 1 hot module load (limit 1)",
                               "1 Sim machine(s) with two live drivers"])
        self.assertIn("module SimsOnlinePresentation_r7 loads 2 (1 hot)", lines[1])
        self.assertIn("sessions 3, warm rejoins 2", lines[1])
        self.assertTrue(lines[-2].startswith("fresh boot due"), lines[-2])
        # the tokens line is what an arms --after hook keeps on the row (arms_lib.hook_values)
        import arms_lib
        self.assertEqual(arms_lib.hook_values("\n".join(lines)),
                         {"server_ageMin": 156, "server_warmRejoins": 0, "server_warmResets": 0, "client1_ageMin": 150,
                          "client1_warmRejoins": 2, "client1_warmResets": 2, "client1_hotLoads": 1, "client1_failures": 2,
                          "doubles": 1, "boot_due": 1})

    def test_warm_rejoins_are_the_sessions_after_the_first_not_the_resets(self) -> None:
        # the session probe (arms 20261021-063733): two warm rejoins back to back made sessions 3, warmResets 1
        facts = degraded_facts()
        facts["games"]["client1"]["counters"].update(sessions=3, warmResets=1)
        lines, due = rc.health_lines(facts)
        self.assertIn("sessions 3, warm rejoins 2 (1 reset what the last one presented)", lines[1])
        self.assertIn("client1 2 warm rejoins (limit 2)", due)

    def test_a_fresh_boot_and_an_older_mod(self) -> None:
        facts = degraded_facts()
        facts["processes"]["server"]["started"] = facts["processes"]["client1"]["started"] = NOW - 600
        facts["games"]["client1"] = {"counters": {"failures": 0}, "loads": None}  # a mod before warmResets and loads
        facts["doubles"] = []
        lines, due = rc.health_lines(facts)
        self.assertEqual(due, [])
        self.assertIn("warm rejoins ? (>=1: its controller attached) (a mod before the counter)", lines[1])
        self.assertIn("module loads ?", lines[1])
        self.assertTrue(lines[-2].startswith("no fresh boot due"), lines[-2])
        self.assertIn("drivers: no Sim has two live drivers of one machine on server, client1", lines)

    def test_a_game_down_still_shows_its_process(self) -> None:
        facts = degraded_facts()
        facts["processes"]["client2"] = {"pid": 33, "started": None, "attached": False}
        lines, _ = rc.health_lines(facts)
        self.assertIn("client2   down (no DIAG_STATE in the world)  up ? (pid 33)", lines)

    def test_the_games_come_from_whichever_checkout_booted_them(self) -> None:
        # 2026-10-21: Session F booted from SimsOnline-sim162, so this checkout's run folder named no game (ages ?)
        files = {"SimsOnline-tools-e": {"states": {}, "pids": {}},
                 "SimsOnline-sim162": {"states": {"server": {"gamePid": 32204, "startedUtc": "2026-10-21T05:40:00Z"},
                                                  "client": {"gamePid": 85896, "startedUtc": "2026-10-21T05:41:00Z"}},
                                       "pids": {"client2": 59604}},
                 "SimsOnline-old": {"states": {"server": {"gamePid": 777, "startedUtc": "2026-10-20T01:00:00Z"}},
                                    "pids": {}}}
        live = {32204: NOW - 870, 85896: NOW - 860, 59604: NOW - 250}  # 777 has exited
        got = rc.pick_processes(files, lambda pid: ("TS3W.exe", live[pid]) if pid in live else None)
        self.assertEqual({k: (v["pid"], v["checkout"]) for k, v in got.items()},
                         {"server": (32204, "SimsOnline-sim162"), "client1": (85896, "SimsOnline-sim162"),
                          "client2": (59604, "SimsOnline-sim162")})
        self.assertEqual(got["client2"]["started"], NOW - 250)

    def test_facts_read_only_the_games_in_the_world(self) -> None:
        asked: list[tuple] = []

        def fake(endpoint, *args):
            asked.append((endpoint, *args))
            if args == ("DIAG_STATE",):
                return (0, "accepted=True\ninWorld=True") if endpoint != rc.games()["client2"] else (1, "ERROR: refused")
            if args[1:] == ("status",):
                return 0, "status;replayDrivers=3;sessions=2;warmResets=1"
            if args == ("SACS_REPLAY", "00"):
                return 0, "presentation=module=M/loads:1/kinds:2"
            return 0, "failures=0\n" if args[1:] == ("replay-failures",) else "machines=0\n"

        with mock.patch.object(rc, "bridge", side_effect=fake), mock.patch.object(rc, "processes", return_value={}):
            facts = rc.health_facts({"Pablo": PABLO})
        self.assertEqual(facts["down"], ["client2"])
        self.assertEqual(facts["games"]["client1"]["counters"]["warmResets"], 1)
        self.assertEqual(facts["games"]["client1"]["loads"], ("M", 1))
        self.assertIsNone(facts["games"]["server"]["loads"])
        self.assertFalse([a for a in asked if a[0] == rc.games()["client2"] and a[1:] != ("DIAG_STATE",)])
        self.assertEqual(facts["doubles"], [])


if __name__ == "__main__":
    unittest.main()
