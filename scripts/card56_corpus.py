#!/usr/bin/env python3
"""card56_corpus - card 56 gate 6's live corpus: every bridge verb, with ids from the running game.

    python scripts/card56_corpus.py --live --half csharp --saves-dir <TS3 Saves folder> \
        --loaded-save <the save the stack booted> --out "$RUN/csharp_corpus.txt"
    python scripts/card56_corpus.py --live --half csharp2 --out "$RUN/csharp2_corpus.txt"

Writes a corpus in the crate's format (rust/simlink_bridge/src/corpus.rs) for one half of gate 6: `--half csharp`
before step (a) (bridgectl.py send-corpus), `--half rust` after it and before stop-controller (it reads through
the controller), for simlinkctl roundtrip, and `--half csharp2` for step (d)'s second server process. It reads the
ids it needs through the server controller's loopback admin listener with read-only verbs only (DIAG_STATE,
PROBE_PRESENTATION, DIAG_WALK), and refuses without --live or with an endpoint that is not loopback. It never sends
a corpus line itself.

World state (card 56 §G gate 6, c9a23656 row 208): each half leaves the world as it found it. Every row is a
read, a set to the value it read (speed, world time), a do/undo pair on a Sim the controller does not watch or
gate (DISABLE/ENABLE_AUTONOMY, GATE/UNGATE_AUTONOMY), a verb the server mod refuses by role (client-only verbs) or
for an absent target, or the session verbs on this half's own scratch save: SAVE_GAME to a fresh
`card56-gate6-<half>-<UTC>.sims3` that is in no save folder and is not the loaded save, then LOAD_SAVE and
LOAD_SAVE_MENU of it. A verb with no safe form is not written: it is a `#@unsent <Record> <reason>` line, and
run_offline_gates.py names it as the reason gates 5 and 6 stay PENDING_LIVE.
`SetWorldTime` is the one verb with more than one row: the ruling sends it as six rows on a paused server (read the
speed, pause, read the time T, set T plus one game minute, set T, restore the speed), so the tool writes them only
when the world it read says `observedSpeed=Pause` and refuses otherwise - a static file cannot read the clock after
the pause, so a running clock would make the T it carries stale by the time the half runs. Pause the server first
(`python scripts/bridgectl.py --timeout 30 SET_GAME_SPEED Pause`) and put the speed back after the half; the run
procedure says so at each half.
`#@blank <WIRE> <fields>` lines mark the command-line fields that carry a value this half chose or read (its
scratch save name, a restored value, its dialog request id); simlinkctl live-diff --corpus blanks them for pairing.
The three verbs no read lets a half undo (AnswerDialog, SetSimScope, SetAuthority) carry the card's literal
arguments, are never undone, and go last: after LOAD_SAVE_MENU and before DISCONNECT in the Rust half's corpus,
first in step (d)'s, and absent from the first C# half's, which writes no save and does not boot a second process.
DISCONNECT is the very last line of every corpus. The per-verb reasoning is in rust/simlink_bridge/README.md,
"Live gates".
"""
from __future__ import annotations

import argparse
import ipaddress
import json
import socket
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Callable, Protocol

DEFAULT_ENDPOINT = "127.0.0.1:34029"
HOUSEHOLD_PATH = "SimDescription.Household"
# Never an InteractionInstance id: 2^53 + 1 is no 32-bit id cast to ulong (CANCEL_INTERACTION then finds nothing).
NO_INTERACTION = "9007199254740993"
# No Sim or object has ObjectId 0, so a verb aimed at it is refused or reports not-found, changing nothing.
ABSENT = "0"
SPEED_NAMES = {"Pause": "Pause", "Normal": "Normal", "Double": "Speed2", "Triple": "Speed3", "Skip": "Ultra"}
# The session verbs go last, DISCONNECT the very last: the mod clears every unread ctl frame when it handles
# DISCONNECT (BridgeMemoryBuffer.NoteControllerDisconnected), so a line the Rust driver writes right after it is lost.
SESSION_LAST = ("SaveGame", "LoadSave", "LoadSaveViaMenu", "Disconnect")
# Card 56 gate 6 (c9a23656 row 208): the three verbs no read lets a half undo are never undone, so the Rust half
# sends them after its other verbs and before its DISCONNECT, and the C# half sends them in step (d): the three,
# then its own DISCONNECT. The first C# corpus does not carry them at all.
UNDO_LAST = ("AnswerDialog", "SetSimScope", "SetAuthority")
# BridgeCommand.cs declaration order (rust/simlink_bridge/src/verbs.rs VERB_TABLE), the session verbs moved last
# and the three moved out of it, into UNDO_LAST.
ORDER = (
    "SetGameSpeed", "SetWorldTime", "RequestSnapshot", "ProbeSimLifecycle", "ProbeActorLookup", "ProbeStateInjection",
    "QueueInteraction", "SetActiveSim", "CancelInteraction", "LeaveConversation", "ShowDialog",
    "DisableAutonomy", "EnableAutonomy", "GateAutonomy", "UngateAutonomy",
    "ProbeSimState", "ApplySimState", "SetSimAliases", "ProbeSimPresence", "SpawnSim", "DespawnSim",
    "ExportSimDescription", "ProbeSimDetails", "ApplySimDetails", "ImportSimDescription", "SacsRecorder",
    "SacsReplay", "DiagState", "DiagCaptureInteraction", "QueryInteractionState", "InjectReadIntent", "Ping", "Raw",
    "SetRole", "ApplyPosition", "ApplyPositions", "ApplyMotive", "ProbeSimMotive", "ApplyInteractionQueue",
    "ApplyInteractions", "ApplyForceAlign", "ProbeSimInteractionQueue", "ApplyCash", "ProbeHouseholdCash",
    "ApplySkill", "ProbeSimSkill", "ApplyAutonomy", "ProbeSimAutonomy", "MergeLotState", "ImportHouseholdContent",
    "ExportHouseholdContent", "MergeTravellingHousehold", "ShowNotification", *SESSION_LAST,
)
# The verbs each half sends, in its send order (card 56 gate 6, "Order within a half"). `csharp2` is step (d): the
# second server process, which saves nothing, so its corpus is the three and then DISCONNECT.
HALF_ORDER = {"csharp": ORDER, "rust": (*ORDER[:-1], *UNDO_LAST, "Disconnect"), "csharp2": (*UNDO_LAST, "Disconnect")}
# Verbs with no safe form on the server mod; their reasons cite the handler (SimsOnlineServerHostMod/ServerHostMain.cs).
# None since the c9a23656 ruling: the three go in with the literals below, SetWorldTime with its six paused rows, and
# the speed with the value read. Keep the mechanism and its `#@unsent` line for a verb a later ruling cannot make safe.
NO_SAFE_FORM: dict[str, str] = {}
# The line that stops the server's clock before a corpus is written, and the field of the read that says it stopped.
PAUSE_LINE = "python scripts/bridgectl.py --timeout 30 SET_GAME_SPEED Pause"
# The card's literal arguments for the three (c9a23656 row 208), in the corpus codec's own form (src/corpus.rs: the
# token `~` is null, an empty token is an empty id list): AnswerDialog(RequestId = 0, Answer = null),
# SetSimScope(LotIds = [], SimObjectIds = []), SetAuthority(Mode = Observe, OwnedSimObjectIds = []).
LITERAL_ARGS = {"AnswerDialog": ["0", "~"], "SetSimScope": ["", ""], "SetAuthority": ["Observe", ""]}
# The command-line fields (0 = verb, 1 = correlation id) that carry a value this half chose or read.
BLANKS = {"SetGameSpeed": ("SET_GAME_SPEED", "2"), "SetWorldTime": ("SET_WORLD_TIME", "2,3,4"),
          "ShowDialog": ("SHOW_DIALOG", "2"), "SaveGame": ("SAVE_GAME", "2"), "LoadSave": ("LOAD_SAVE", "2"),
          "LoadSaveViaMenu": ("LOAD_SAVE_MENU", "2")}


class Refusal(Exception):
    """A reason to write nothing (exit 2)."""


class Bridge(Protocol):
    def request(self, name: str, *args: str) -> dict: ...


def hexenc(text: str) -> str:
    """The bridge's hex text (bridgectl.py hexenc)."""
    return "".join(f"{b:02X}" for b in text.encode("utf-8"))


def loopback_endpoint(endpoint: str) -> tuple[str, int]:
    host, _, port = endpoint.rpartition(":")
    try:
        loopback = host == "localhost" or ipaddress.ip_address(host).is_loopback
    except ValueError:
        loopback = False
    if not loopback or not port.isdigit():
        raise Refusal(f"--endpoint {endpoint}: only a loopback address:port (the controller's admin listener)")
    return host, int(port)


class LoopbackBridge:
    """The controller's admin listener (BridgeAdminListener): one JSON answer line per request line."""

    def __init__(self, endpoint: str, timeout: float):
        host, port = loopback_endpoint(endpoint)
        self.timeout = int(timeout)
        self.sock = socket.create_connection((host, port), timeout=2.0)
        self.sock.settimeout(timeout + 10)

    def request(self, name: str, *args: str) -> dict:
        self.sock.sendall((" ".join([f"@{self.timeout}", name, *args]) + "\n").encode("utf-8"))
        buf = b""
        while not buf.endswith(b"\n"):
            chunk = self.sock.recv(65536)
            if not chunk:
                raise Refusal("the admin listener closed the connection")
            buf += chunk
        return json.loads(buf.decode("utf-8-sig"))


def scratch_name(half: str, now: datetime) -> str:
    return f"card56-gate6-{half}-{now.astimezone(timezone.utc):%Y%m%dT%H%M%SZ}"


def strip_sims3(name: str) -> str:
    return name[:-6] if name.lower().endswith(".sims3") else name


def check_scratch(name: str, saves_dir: Path, loaded: str) -> None:
    """The mod's own match (ServerHostMain.CurrentSaveIdFor): case-insensitive, `.sims3` stripped. Saving under the
    loaded save's name would write over it; an existing folder would be overwritten."""
    if not saves_dir.is_dir():
        raise Refusal(f"--saves-dir {saves_dir}: not a folder (the game's saves folder)")
    if strip_sims3(name).lower() == strip_sims3(loaded).lower():
        raise Refusal(f"scratch save {name} is the loaded save {loaded}")
    taken = {strip_sims3(p.name).lower() for p in saves_dir.iterdir()}
    if strip_sims3(name).lower() in taken:
        raise Refusal(f"scratch save {name} already exists in {saves_dir}")


def report(bridge: Bridge, name: str, *args: str) -> str:
    answer = bridge.request(name, *args)
    if not answer.get("ok") or not answer.get("accepted"):
        raise Refusal(f"{name} {' '.join(args)}: {answer.get('error') or answer.get('reason') or answer}")
    return answer.get("report") or ""


def fields(text: str) -> dict[str, str]:
    return dict(f.split("=", 1) for f in text.splitlines()[0].split(";") if "=" in f) if text else {}


def read_world(bridge: Bridge) -> dict:
    """The ids and values the rows need, through read-only verbs."""
    diag = fields(report(bridge, "DIAG_STATE"))
    if diag.get("role") != "server" or diag.get("inWorld") != "True":
        raise Refusal(f"the game must be the server mod in its world: role={diag.get('role')} inWorld={diag.get('inWorld')}")
    busy = {i.split(":")[0] for key in ("autonomyWatch", "commandGate") for i in diag.get(key, "").split(",")}
    sims = []
    for line in report(bridge, "PROBE_PRESENTATION").splitlines()[1:]:
        f = line.split("|")
        if len(f) >= 9 and f[0].isdigit() and f[1].isdigit() and f[1] != "0":
            sims.append({"id": f[0], "desc": f[1], "lot": f[2], "pos": f"{f[3]},{f[4]},{f[5]}", "container": f[7]})
    free = sorted((s for s in sims if s["id"] not in busy), key=lambda s: int(s["id"]))
    if not free:
        raise Refusal("no Sim outside the controller's autonomy watch and command gate (DIAG_STATE)")
    sim = free[0]
    lots = [s["lot"] for s in [sim, *sims] if s["lot"] not in ("", "0")]
    objects = sorted({s["container"] for s in sims if s["container"] not in ("", "0")}, key=int)
    walk = report(bridge, "DIAG_WALK", sim["id"], hexenc(HOUSEHOLD_PATH), "1")
    household = next((line.split(" = ", 1)[1] for line in walk.splitlines() if line.startswith("mHouseholdId = ")), "")
    if not household.isdigit():
        raise Refusal(f"DIAG_WALK {sim['id']} {HOUSEHOLD_PATH}: no mHouseholdId")
    return {"sim": sim, "lot": lots[0] if lots else ABSENT, "object": objects[0] if objects else sim["id"],
            "household": household, "commanded": diag.get("commandedSpeed", ""),
            "observed": diag.get("observedSpeed", ""),
            "clock": [diag.get(k, "") for k in ("clockDay", "clockHours", "clockMinutes")]}


def require_paused(world: dict) -> None:
    """Gate 6 sends `SetWorldTime` as six rows (card 56 §G gate 6, c9a23656 row 208; Main's Q1 ruling of 2026-11-01),
    and a corpus is a static file written before the half runs: nothing carries a reply into a later line
    (`tools/simlinkctl.rs`'s round trip encodes each row on its own, `src/corpus.rs` has no variable). So the `T`
    the file writes is the `T` the half reads after its own pause only if the clock stood still the whole time.
    Refuse, with the line that stops it, rather than send a stale `T` or drop the verb to `#@unsent`."""
    if not all(v.isdigit() for v in world["clock"]):
        raise Refusal(f"DIAG_STATE reported no clock (clock={','.join(world['clock'])}): "
                      "there is no T to write into SetWorldTime's six rows")
    if world["observed"] != "Pause":
        raise Refusal(f"the server's clock is running (observedSpeed={world['observed']}): gate 6 writes "
                      "SetWorldTime's six rows on a paused server, so pause it first with "
                      f"`{PAUSE_LINE}` and put the speed back after the half (rust/simlink_bridge/README.md, "
                      "Live gates)")


def clock_rows(world: dict) -> list[tuple[str, list[str]]]:
    """The six rows gate 6 gives `SetWorldTime` on the paused server `require_paused` checked for (c9a23656 row 208):
    read the speed, pause, read the time `T`, set `T` plus one game minute (`T` minus one at 23:59, so the day never
    changes and at most one minute's events are crossed; any other :59 rolls the hour), set `T` again (the handler
    skips a set the clock already reads), restore the speed. The `DIAG_STATE` rows are the reads inside the half;
    with the clock stopped they answer the values this file already carries."""
    day, hours, minutes = (int(v) for v in world["clock"])
    if (hours, minutes) == (23, 59):
        moved = [str(day), str(hours), str(minutes - 1)]
    elif minutes == 59:
        moved = [str(day), str(hours + 1), "0"]
    else:
        moved = [str(day), str(hours), str(minutes + 1)]
    read = [str(day), str(hours), str(minutes)]
    speed = SPEED_NAMES[world["observed"]]
    return [("DiagState", []), ("SetGameSpeed", ["Pause"]), ("DiagState", []),
            ("SetWorldTime", moved), ("SetWorldTime", read), ("SetGameSpeed", [speed])]


def build(world: dict, save: str | None, order: tuple[str, ...]) -> tuple[list[tuple[str, list[str]]], dict[str, str]]:
    """The rows of this half's corpus in send order (record name, the args after the correlation id, in
    `src/corpus.rs` parse_command order), and the verbs it leaves unsent. `save` is this half's own scratch save;
    step (d)'s corpus has none, because it writes no session save verb."""
    s, d, pos, o, h = world["sim"]["id"], world["sim"]["desc"], world["sim"]["pos"], world["object"], world["household"]
    unsent = dict(NO_SAFE_FORM)
    args: dict[str, list[str]] = {
        "RequestSnapshot": ["~"], "ProbeSimLifecycle": [], "ProbeActorLookup": [],
        "ProbeStateInjection": [ABSENT, pos],
        "QueueInteraction": [s, "Terrain.GoHere", o, "~", "0"],  # no position: refused before anything is queued
        "SetActiveSim": [ABSENT], "CancelInteraction": [s, NO_INTERACTION], "LeaveConversation": [ABSENT],
        "ShowDialog": ["1", s, "TwoButton", "0"],
        "DisableAutonomy": [s, "0"], "EnableAutonomy": [s, "0"], "GateAutonomy": [s], "UngateAutonomy": [s, "0"],
        "ProbeSimState": [s], "ApplySimState": [s, "0,0,0,0,0,0", "0"], "SetSimAliases": ["1", s, d, "0"],
        "ProbeSimPresence": ["0"], "SpawnSim": [s, d, pos], "DespawnSim": [s, d], "ExportSimDescription": [ABSENT],
        "ProbeSimDetails": [s], "ApplySimDetails": [s, "0", "0", "x", "0", ""],
        "ImportSimDescription": [d, "00", "", ""], "SacsRecorder": ["1", "status"], "SacsReplay": ["1", "x"],
        "DiagState": [], "DiagCaptureInteraction": [s], "QueryInteractionState": [s, "~"],
        "InjectReadIntent": [s, o, "chooser", "~"], "Ping": [], "Raw": ["PROBE_SIM_STATUS", "1", s],
        "SetRole": ["Server"], "ApplyPosition": [s, pos], "ApplyPositions": ["1", s, pos],
        "ApplyMotive": [s, "Hunger", "0"], "ProbeSimMotive": [s, "Hunger"],
        "ApplyInteractionQueue": [], "ApplyInteractions": [], "ApplyForceAlign": [],
        "ProbeSimInteractionQueue": [s], "ApplyCash": [h, "0"], "ProbeHouseholdCash": [h],
        "ApplySkill": [s, "Cooking", "0"], "ProbeSimSkill": [s, "Cooking"], "ApplyAutonomy": [s, "0"],
        "ProbeSimAutonomy": [s], "MergeLotState": [world["lot"], "1", ABSENT, "0,0,0"],
        "ImportHouseholdContent": [h, "0", "0", "0", "0", "0", "0", "0", "0"],
        "ExportHouseholdContent": [ABSENT], "MergeTravellingHousehold": [h, "0", "0", "0", "0", "0", "0"],
        "ShowNotification": ["card56-gate6"], "Disconnect": [],
        **LITERAL_ARGS,
    }
    if save:
        for verb in ("SaveGame", "LoadSave", "LoadSaveViaMenu"):
            args[verb] = [save]
    commanded, observed = world["commanded"], world["observed"]
    if commanded == observed and observed in SPEED_NAMES:
        args["SetGameSpeed"] = [SPEED_NAMES[observed]]
    else:
        unsent["SetGameSpeed"] = (f"commandedSpeed={commanded} differs from observedSpeed={observed}: "
                                  "no one value restores both (HandleSetGameSpeed)")
    if "SetWorldTime" in order:
        require_paused(world)
    rows: list[tuple[str, list[str]]] = []
    for verb in order:
        if verb == "SetWorldTime":
            rows += clock_rows(world)
        elif verb in args:
            rows.append((verb, args[verb]))
    return rows, unsent


def escape(token: str) -> str:
    return token.replace("\\", "\\\\").replace("\t", "\\t").replace("\n", "\\n").replace("\r", "\\r")


def render(half: str, save: str | None, world: dict, rows: list[tuple[str, list[str]]], unsent: dict[str, str],
           order: tuple[str, ...], new_corr: Callable[[], str]) -> str:
    sim = world["sim"]
    sent = {verb for verb, _ in rows}
    lines = [
        f"# card 56 gate 6 live corpus, the {half} half (scripts/card56_corpus.py; format: src/corpus.rs).",
        f"# read: sim={sim['id']} desc={sim['desc']} lot={world['lot']} object={world['object']} "
        f"household={world['household']} speed={world['commanded']}/{world['observed']} clock={','.join(world['clock'])}",
        *(["# SetWorldTime, six rows on the paused server above (c9a23656): read the speed, pause, read the time T,",
           "# set T plus one game minute, set T again, restore the speed. Its two reads are DIAG_STATE rows."]
          if "SetWorldTime" in sent else []),
        *([f"# scratch save: {save} (this half's own)"] if save else []),
        *(f"#@blank {BLANKS[v][0]} {BLANKS[v][1]}" for v in order if v in BLANKS and v in sent),
        *(f"#@unsent {v} {unsent[v]}" for v in order if v in unsent),
    ]
    lines += ["\t".join([verb, new_corr(), *map(escape, a)]) for verb, a in rows]
    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None, connect: Callable[[str, float], Bridge] = LoopbackBridge,
         now: Callable[[], datetime] = lambda: datetime.now(timezone.utc),
         new_corr: Callable[[], str] = lambda: uuid.uuid4().hex) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--live", action="store_true", help="required: reads the running game through the controller")
    ap.add_argument("--half", required=True, choices=tuple(HALF_ORDER))
    ap.add_argument("--saves-dir", help="the game's saves folder (every save in it is kept); required for a half "
                                        "that writes its own scratch save (csharp and rust)")
    ap.add_argument("--loaded-save", help="the save the stack booted (stackctl.py status); required with --saves-dir")
    ap.add_argument("--out", required=True)
    ap.add_argument("--endpoint", default=DEFAULT_ENDPOINT, help="the server controller's admin listener (loopback)")
    ap.add_argument("--timeout", type=float, default=30.0)
    a = ap.parse_args(argv)
    try:
        if not a.live:
            raise Refusal("refusing without --live: it reads the running game (card 56 gate 6, under the stack claim)")
        loopback_endpoint(a.endpoint)
        order = HALF_ORDER[a.half]
        save = None
        if "SaveGame" in order:
            if not a.saves_dir or not a.loaded_save:
                raise Refusal(f"--half {a.half} writes its own scratch save: --saves-dir and --loaded-save are required")
            save = scratch_name(a.half, now()) + ".sims3"
            check_scratch(save, Path(a.saves_dir), a.loaded_save)
        world = read_world(connect(a.endpoint, a.timeout))
        rows, unsent = build(world, save, order)
        text = render(a.half, save, world, rows, unsent, order, new_corr)
    except (Refusal, OSError) as ex:
        print(f"card56_corpus: {ex}; nothing written", file=sys.stderr)
        return 2
    Path(a.out).write_text(text, encoding="utf-8", newline="\n")
    print(f"card56_corpus: {len(rows)} rows ({len({verb for verb, _ in rows})} verbs), scratch save {save or '-'}, "
          f"not sent: {', '.join(v for v in order if v in unsent) or '-'}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
