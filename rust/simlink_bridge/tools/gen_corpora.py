"""Regenerate the card-56 differential corpora in rust/simlink_bridge/fixtures (format: src/corpus.rs).

    python rust/simlink_bridge/tools/gen_corpora.py

Writes verb_corpus.txt (gate 4), reply_corpus.txt (gate 5) and float_corpus.txt (the .NET float formatting
probe under gate 4). The oracle outputs next to them come from rust/simlink_bridge/oracle (run_offline_gates.py).
Every value here is synthetic: no game data.
"""

import itertools
import random
import struct
from pathlib import Path

FIXTURES = Path(__file__).resolve().parent.parent / "fixtures"


def esc(s: str) -> str:
    return s.replace("\\", "\\\\").replace("\t", "\\t").replace("\n", "\\n").replace("\r", "\\r")


def write(name: str, header: list[str], rows: list[str]) -> None:
    text = "\n".join(["# " + h for h in header] + rows) + "\n"
    (FIXTURES / name).write_text(text, encoding="utf-8", newline="\n")
    print(f"{name}: {len(rows)} rows")


def verb_corpus() -> None:
    counter = itertools.count(1)
    rows: list[str] = []

    def r(record: str, *args: object) -> None:
        corr = "%032x" % (0x5600000000000000000000000000000 + next(counter) * 0x1111)
        rows.append("\t".join([record, corr] + [esc(str(a)) for a in args]))

    r("LoadSave", "Martinez.sims3")
    r("LoadSave", "Sa\u00efd \u6f22\u5b57 save|pipe")
    r("LoadSaveViaMenu", "Bridgeport")
    r("SaveGame", "")
    for speed in ["Pause", "Normal", "Speed2", "Speed3", "Ultra"]:
        r("SetGameSpeed", speed)
    r("SetWorldTime", 0, 0, 0)
    r("SetWorldTime", 6, 23, 59)
    r("RequestSnapshot", "~")
    r("RequestSnapshot", 0)
    r("RequestSnapshot", -9223372036854775808)
    r("RequestSnapshot", 9223372036854775807)
    r("ProbeSimLifecycle")
    r("ProbeActorLookup")
    r("ProbeStateInjection", 18446744073709551615, "1.5,-0,123456.789")
    r("ProbeStateInjection", 0, "0.1,1e-7,3.4028235e38")
    r("QueueInteraction", 72057594037927936, "Terrain.GoHere", "~", "~", 0)
    r("QueueInteraction", 5, "Sit", "123", "10.25,0,-3.5", 2, "bookId", "9", "autonomous", "1")
    r("QueueInteraction", 5, "Chat", "77", "~", 1, "ui.name", "Chat")
    r("QueueInteraction", 5, "Chat", "77", "1,2,3", 4, "ui.name", "Be Friendly", "ui.path", "Friendly/More",
      "ui.hit", "2:77:16", "ui.picked", "s77,o12")
    r("SetActiveSim", 42)
    r("CancelInteraction", 42, 9007199254740993)
    r("LeaveConversation", 42)
    r("AnswerDialog", 7, "~")
    r("AnswerDialog", -1, "Yes")
    r("AnswerDialog", 0, "")
    r("ShowDialog", 3, 42, "TwoButton", 0)
    r("ShowDialog", 3, 42, "ThreeButton", 3, "Question?", "A|B", "")
    r("DisableAutonomy", 42, 0)
    r("DisableAutonomy", 42, 1)
    r("EnableAutonomy", 42, 0)
    r("EnableAutonomy", 42, 1)
    r("GateAutonomy", 42)
    r("UngateAutonomy", 42, 0)
    r("UngateAutonomy", 42, 1)
    r("SetSimScope", "", "")
    r("SetSimScope", "1,2,3", "18446744073709551615")
    r("SetAuthority", "Off", "")
    r("SetAuthority", "Observe", "5")
    r("SetAuthority", "Enforce", "5,6,7")
    r("ProbeSimState", "")
    r("ProbeSimState", "1,2,3,4,5,6,7,8")
    r("ApplySimState", 42, "", 0)
    r("ApplySimState", 42, "100,-100,0.125,0.375,-0.005,99.995", 2, 1234, 5, 18446744073709551615, 0)
    r("ApplySimState", 42, "1.5,2.5", 1, 7, 8)
    r("SetSimAliases", 0, 0)
    r("SetSimAliases", 2, 1, 10, 2, 20, 1)
    r("SetSimAliases", 1, 3, 0, 0)
    r("ProbeSimPresence", 0)
    r("ProbeSimPresence", -5)
    r("SpawnSim", 42, 99, "1.5,2,-3.25")
    r("DespawnSim", 42, 99)
    r("ExportSimDescription", 99)
    r("ProbeSimDetails", "")
    r("ProbeSimDetails", "1,2")
    r("ApplySimDetails", 42, 0, 0, "Ignored Branch", -1, "")
    r("ApplySimDetails", 42, 1234567, 5, "a:b;c,d|e+f=g h", 20000, "Cooking=3,Logic=10")
    r("ApplySimDetails", 42, 7, 1, "", 0, "-")
    r("ImportSimDescription", 99, "0102030405", "", "FF")
    r("ImportSimDescription", 99, "", "", "")
    r("SacsRecorder", 0)
    r("SacsRecorder", 1, "status")
    r("SacsRecorder", 3, "dump", "120", "1,2,3")
    r("SacsRecorder", 3, "rust-plan", "42", "1.5,2,3,4,5,6")
    r("SacsReplay", 0)
    r("SacsReplay", 1, "Vp|rt|x")
    r("SacsReplay", 3, "line one", "line|two", "\u00e9")
    r("DiagState")
    r("DiagCaptureInteraction", 42)
    r("QueryInteractionState", 42, "~")
    r("QueryInteractionState", 42, "1234567890123")
    r("InjectReadIntent", 42, 77, "chooser", "~")
    r("InjectReadIntent", 42, 77, "getbook", 0)
    r("InjectReadIntent", 42, 77, "getbook", 3, "seatArea", "2", "unknownKey", "x", "bookId", "77")
    r("InjectReadIntent", 42, 77, "getbook", 1, "autonomous", "")
    r("Ping")
    r("Disconnect")
    r("Raw", "PROBE_PRESENTATION", 0)
    r("Raw", "DIAG_WALK", 3, "42", "-", "3")
    r("Raw", "", 1, "")
    r("SetRole", "Server")
    r("SetRole", "Client")
    r("ApplyPosition", 42, "1,2,3")
    r("ApplyPosition", 42, "-0.0001,1e10,NaN")
    r("ApplyPositions", 0)
    r("ApplyPositions", 3, 1, "1,2,3", 2, "4.5,5.5,6.5", 3, "-1e-5,0,1e9")
    r("ApplyMotive", 42, "Hunger", "-37.5")
    r("ApplyMotive", 42, "Fun", "100")
    r("ApplyMotive", 42, "Energy", "0.1")
    r("ProbeSimMotive", 42, "Bladder")
    r("ApplyInteractionQueue")
    r("ApplyInteractions")
    r("ApplyForceAlign")
    r("ProbeSimInteractionQueue", 42)
    r("ApplyCash", 100, -2147483648)
    r("ApplyCash", 100, 2147483647)
    r("ApplyCash", 100, 0)
    r("ProbeHouseholdCash", 100)
    r("ApplySkill", 42, "Cooking", 10)
    r("ApplySkill", 42, "Logic", -1)
    r("ProbeSimSkill", 42, "Charisma")
    r("ApplyAutonomy", 42, 0)
    r("ApplyAutonomy", 42, 1)
    r("ProbeSimAutonomy", 42)
    r("MergeLotState", 555, 0)
    r("MergeLotState", 555, 2, 1, "1,2,3", 2, "0.5,0.25,0.125")
    r("ImportHouseholdContent", 100, 300, 100, 100, 100, 3735928559, 4, 5, 6)
    r("ImportHouseholdContent", 100, 0, 0, 0, 0, 0, 0, 0, 0)
    r("ExportHouseholdContent", 100)
    r("MergeTravellingHousehold", 100, 1024, 255, 10, 20, 30, 2)
    r("ShowNotification", "Player 2 joined")
    r("ShowNotification", "")
    write("verb_corpus.txt",
          ["card 56 gate 4: one or more records per BridgeCommand.cs record (format: src/corpus.rs).",
           "Generated by tools/gen_corpora.py; synthetic values only."], rows)


def hx(s: str) -> str:
    return s.encode("utf-8").hex().upper()


def reply_corpus() -> None:
    c = "0123456789abcdef0123456789abcdef"
    u = c.upper()
    d = "01234567-89ab-cdef-0123-456789abcdef"
    rows: list[str] = []

    def r(tag: str, line: str) -> None:
        rows.append(tag + "\t" + esc(line))

    r("OK", "OK|" + c)
    r("OK", "OK|" + u)
    r("OK", "\ufeffOK|" + c)
    r("OK", "OK|" + c + "|extra")
    r("OK", "OK|short")
    r("OK", "OK|")
    r("OK", "OK")
    r("ERROR", "ERROR|" + c + "|" + hx("Sim not found: 42"))
    r("ERROR", "ERROR|" + c + "|")
    r("ERROR", "ERROR|" + c + "|ABC")
    r("ERROR", "ERROR|" + c + "|ZZ")
    r("ERROR", "ERROR|" + c)
    r("ERROR", "ERROR|00000000000000000000000000000000|" + hx("Bridge response is too large."))
    r("ERROR", "ERROR|" + c + "|" + hx("caf\u00e9 \u6f22"))
    r("ERROR", "ERROR|" + c + "|C3")
    r("ERROR", "ERROR|" + c + "|" + hx("a") + "|tail")
    r("ERROR", "ERR\x01OR|" + c + "|41")
    r("ERROR", "ERROR|" + c + "|41\x7f")

    r("SESSION_READY", "SESSION_READY|" + c + "|" + hx("Martinez.sims3") + "|" + hx("Sunset Valley") + "|123456")
    r("SESSION_READY", "SESSION_READY|" + c + "|" + hx("s") + "|" + hx("w") + "|notanumber")
    r("SESSION_READY", "SESSION_READY|" + c + "|" + hx("s") + "|" + hx("w") + "| -7 ")
    r("SESSION_READY", "SESSION_READY|" + c + "|" + hx("s") + "|" + hx("w"))

    sim12 = "7:" + hx("Bella Goth") + ":9:1.5:0:-2.25:55:" + hx("Sit") + ":2:300:4:9001"
    sim11 = "8:" + hx("Mortimer") + ":9:10:20:30:-:-:-:-:0"
    sim9 = "9:" + hx("Old") + ":9:1:2:3:-:-:-"
    sim8 = "10:" + hx("Legacy") + ":9:1:2:3:66:5"
    simbad = "11:ZZ:9:1:2:3:-:-:-"
    simshort = "12:41:9"
    simws = "13:" + hx("Ws") + ": 9 :1,000.5: +2 :3e2:-:-:-"
    obj = "100:" + hx("Sims3.Gameplay.Objects.Chair") + ":4.5:0:1:300"
    obj2 = "101:" + hx("Door") + ":1e3:-0:0.1:-"
    head = "SNAPSHOT|" + c + "|42|" + hx("W") + "|" + hx("S") + "|"
    r("SNAPSHOT", "SNAPSHOT|" + c + "|42|" + hx("Sunset Valley") + "|" + hx("Martinez") + "|Normal")
    r("SNAPSHOT", head + "Double|7|" + "|".join([sim12, sim11, sim9, sim8, simbad, simshort, simws]) + "|2|" + obj + "|" + obj2)
    r("SNAPSHOT", head + "Speed3|0|0")
    r("SNAPSHOT", head + "Skip|3|" + sim12)
    r("SNAPSHOT", head + "Warp|0|0")
    r("SNAPSHOT", "SNAPSHOT|" + c + "|x|" + hx("W") + "|" + hx("S") + "|Pause")
    r("SNAPSHOT", head + "Pause|-1|1|" + obj)
    r("SNAPSHOT", head + "Pause|1,0|0")
    r("SNAPSHOT", "SNAPSHOT|" + c + "|1|2|3")
    r("SNAPSHOT", "SNAPSHOT|" + d + "|42|" + hx("W") + "|" + hx("S") + "|Pause")
    r("SNAPSHOT_CHUNK", "SNAPSHOT_CHUNK|" + c + "|0|3|SNAPSHOT|" + c + "|42|57")
    r("SNAPSHOT_CHUNK", "SNAPSHOT_CHUNK|" + c + "|2|3|")
    r("SNAPSHOT_CHUNK", "SNAPSHOT_CHUNK|" + c + "|a|3|x")
    r("SNAPSHOT_CHUNK", "SNAPSHOT_CHUNK|" + c + "|1|3")
    r("BULK_REF", "BULK_REF|" + c + "|snapshot|0|2048|00ABCDEF")
    r("BULK_REF", "BULK_REF|" + c + "|snapshot-part|3|1|4|524256|DEADBEEF")
    r("BULK_REF", "BULK_REF|" + c + "|snapshot-part|3|1|4|524256")
    r("BULK_REF", "BULK_REF|" + c + "|diag|1|10|deadbeef")
    r("BULK_REF", "BULK_REF|" + c + "|snapshot|1|10|1DEADBEEF")
    r("BULK_REF", "BULK_REF|" + c + "|snapshot|x|10|0")
    r("BULK_REF", "BULK_REF|" + d + "|snapshot|0|1|0")
    r("BULK_REF", "BULK_REF|" + c + "|snapshot|0")

    reports = [
        "AUTONOMY_PROBE", "SIM_LIFECYCLE_PROBE", "ACTOR_LOOKUP_PROBE", "SIM_MOTIVE_PROBE",
        "SIM_INTERACTION_QUEUE_PROBE", "HOUSEHOLD_CASH_PROBE", "SIM_SKILL_PROBE", "SIM_AUTONOMY_PROBE",
        "SIM_STATUS_PROBE", "DIAG_STATE_REPORT", "DIAG_QUEUE_REPORT", "DIAG_INTERACTION_BUNDLE_REPORT",
        "DIAG_INTERACTION_STATE_REPORT", "DIAG_RESOLVE_REPORT", "DIAG_WALK_REPORT", "DIAG_BLOB_REPORT",
        "DIAG_LOADTEST_REPORT", "DIAG_RUN_REPORT", "STATE_INJECTION_PROBE", "IMPORT_HOUSEHOLD_CONTENT_REPORT",
        "MERGE_TRAVELLING_HOUSEHOLD_REPORT",
    ]
    for name in reports:
        r(name, name + "|" + c + "|ok|" + hx("sim=42;enabled=true;disabledCount=0"))
        r(name, name + "|" + c + "|none|" + hx("not found"))
        r(name, name + "|" + c + "|partial|")
        r(name, name + "|" + c + "|ok|ABC")
        r(name, name + "|bad|ok|41")
        r(name, name + "|" + c + "|ok")
    r("DIAG_STATE_REPORT", "DIAG_STATE_REPORT|" + c + "|ok|" + hx(
        "role=server;inWorld=true;tick=123;autonomyWatch=-;commandGate=-;pendingOutbound=0;swallowed=0"))
    r("DIAG_BLOB_REPORT", "DIAG_BLOB_REPORT|" + c + "|ok|" + hx(
        "exported=true;householdId=100;slotIndex=1;totalBytes=300;propertyStreamBytes=100;resKeyTableBytes=100;"
        "objectIdTableBytes=100;crc32=DEADBEEF;entryCount=4;resKeyCount=5;objectIdCount=6;inventoryCount=0"))
    r("AUTONOMY_PROBE", "AUTONOMY_PROBE|" + c + "|ok|" + hx(
        "42:50.00,-20.00,0.00,0.00,0.00,0.00:-;43:1.00,2.00,3.00,4.00,5.00,6.00:1/2+3/4"))

    caps = hx("multi-slot=16,bulk=4x512k,layout=11,snapshot2=1")
    w = hx("W")
    r("HELLO", "HELLO|6|" + hx("Sunset Valley") + "|123|" + hx("abc1234@2026-11-01T00:00:00Z") + "|" + caps)
    r("HELLO", "HELLO|6|" + w + "|0|" + hx("s") + "|" + hx("layout=10"))
    r("HELLO", "HELLO|6|" + w + "|0|" + hx("s") + "|" + hx(" LAYOUT=12 "))
    r("HELLO", "HELLO|6|" + w + "|0|" + hx("s") + "|" + hx("bulk=4x512k, Multi-Slot=8"))
    r("HELLO", "HELLO|6|" + w + "|0|" + hx("s") + "|")
    r("HELLO", "HELLO|5|" + w + "|10|" + hx("stamp"))
    r("HELLO", "HELLO|4|" + w + "|10")
    r("HELLO", "HELLO|7|" + w + "|x|" + hx("s"))
    r("HELLO", "HELLO|6|" + w + "|10|ZZ|" + caps)
    r("HELLO", "HELLO|x|" + w + "|10")
    r("HELLO", "HELLO|6|")
    r("HELLO", "HELLO|6|W")
    r("HELLO", "\ufeffHELLO|6|" + w + "|10|" + hx("s") + "|" + caps)

    sit = hx("Sit")
    r("EVENT", "EVENT|InteractionQueued|42|9|" + sit + "|77|1|1.5|2|3")
    r("EVENT", "EVENT|InteractionQueued|42|9|" + sit + "||0|0|0|0|bookId=7,bogus=1,seatArea=2,=x,y=,bookId=8")
    r("EVENT", "EVENT|InteractionQueued|42|9|" + sit + "||0|0|0")
    r("EVENT", "EVENT|InteractionQueued|42|x|" + sit + "||0|0|0|0")
    r("EVENT", "EVENT|InteractionStarted|42|9")
    r("EVENT", "EVENT|InteractionCompleted|42|9")
    r("EVENT", "EVENT|InteractionFailed|42|9|" + hx("Route failed"))
    r("EVENT", "EVENT|InteractionFailed|42|9")
    r("EVENT", "EVENT|IntentCaptured|42|77|" + hx("Terrain.GoHere") + "|1|1|2|3")
    r("EVENT", "EVENT|IntentCaptured|42|77|" + hx("Chat") + "|0|0|0|0")
    r("EVENT", "EVENT|UserIntent|42|77|" + hx("Type") + "|" + hx("Name") + "|" + hx("A/B") + "|2:77:16|1|2|3|s77")
    r("EVENT", "EVENT|UserIntent|42|77|" + hx("T") + "|" + hx("N") + "|" + hx("P") + "|0:0:0|-|x|y|-")
    r("EVENT", "EVENT|DialogRequest|3|42|" + hx("TwoButton") + "|2|" + hx("Q?") + "|" + hx("Yes"))
    r("EVENT", "EVENT|DialogRequest|3|42|" + hx("K") + "|3|" + hx("only one"))
    r("EVENT", "EVENT|DialogRequest|3|42|" + hx("K") + "|-1")
    r("EVENT", "EVENT|DialogAnswer|3|42|" + hx("Yes"))
    r("EVENT", "EVENT|DialogAnswer|3|42|-")
    r("EVENT", "EVENT|PickFailed|" + c + "|42|9|" + hx("RouteFailed"))
    r("EVENT", "EVENT|PickFailed|" + d + "|42|9|" + hx("NotRun"))
    r("EVENT", "EVENT|PickFailed|nope|42|9|41")
    r("EVENT", "EVENT|CancelIntent|42|9")
    r("EVENT", "EVENT|LeaveConversationIntent|42")
    r("EVENT", "EVENT|SpeedIntentCaptured|Triple")
    r("EVENT", "EVENT|SpeedIntentCaptured|Speed2")
    r("EVENT", "EVENT|SpeedIntentCaptured|Warp")
    r("EVENT", "EVENT|ServerError|" + hx("boom"))
    r("EVENT", "EVENT|ServerError|0")
    r("EVENT", "EVENT|CaptureFilterDropped|42|" + sit)
    r("EVENT", "EVENT|RoutePlanRequest|42|0|0|0|1|1|1")
    r("EVENT", "EVENT")

    r("MISC", "")
    r("MISC", "NOPIPE")
    r("MISC", "|lead")
    r("MISC", "BULK_NAK|nothex|x")
    r("MISC", "UNKNOWN_REPLY|" + c + "|ok|41")
    r("MISC", "PONG|" + c)
    write("reply_corpus.txt",
          ["card 56 gate 5: host lines by reply family (TAG<TAB>line; format: src/corpus.rs). Reply shapes",
           "follow SimsOnlineServerHostMod/BridgeCodec.cs; edge cases follow BridgeLineCodec.TryDecodeResponse.",
           "Generated by tools/gen_corpora.py; synthetic values only. EVENT|InteractionReplayQueued is left out:",
           "its capture-report bundle is the legacy replay path, which card 56 does not port (src/reply.rs)."], rows)


def float_corpus() -> None:
    values = [
        0.0, -0.0, 1.0, -1.0, 1.5, 0.1, 0.125, 0.375, 0.625, 0.005, 0.015, -0.001, -0.005, 99.999, 99.995, 2.675,
        1e7, 9999999.0, 12345678.0, 1e6, 1234567.9, 1e-5, 1e-4, 0.0001234, 0.001, 3.4028235e38, 1e-45,
        1.17549435e-38, 123.456, -2.5, 0.5, 0.25, 0.045, 1.005, 100.0, -100.0, 16777216.0, 16777217.0, 0.3, 0.7,
        1e15, 1e16, 1e9, 999999900.0, 4294967296.0, 123456789.0, 1e8, 0.000999, 0.00099999, 1e10,
    ]
    seen: set[int] = set()
    rows: list[str] = []

    def add(bits: int) -> None:
        if bits not in seen:
            seen.add(bits)
            rows.append("%08X" % bits)

    for v in values:
        add(struct.unpack("<I", struct.pack("<f", v))[0])
    for bits in [0x7FC00000, 0x7F800000, 0xFF800000, 0x00000001, 0x80000001, 0x7F7FFFFF, 0x00800000]:
        add(bits)
    rng = random.Random(56)
    for _ in range(200):
        bits = rng.getrandbits(32)
        if (bits >> 23) & 0xFF != 0xFF:
            add(bits)
    for _ in range(150):
        add(struct.unpack("<I", struct.pack("<f", rng.randint(-100000, 100000) / 1000.0))[0])
    for k in range(-40, 41):
        add(struct.unpack("<I", struct.pack("<f", k * 0.125))[0])
    write("float_corpus.txt", ["card 56 gate 4: float bit patterns (hex) for the .NET R and F2 formatting differential.",
                               "Generated by tools/gen_corpora.py."], rows)


if __name__ == "__main__":
    verb_corpus()
    reply_corpus()
    float_corpus()
