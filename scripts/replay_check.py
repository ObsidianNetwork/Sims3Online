"""The replay's health on each client game: its counters, Sims with two live drivers of one machine, and its
failed state requests decoded.

    python scripts/replay_check.py counters                 # each client that answers: replayDrivers ... failures
    python scripts/replay_check.py drivers                  # every game: a Sim with two live drivers of one machine
    python scripts/replay_check.py drivers --sims Pablo     # one Sim (household names, or name=objectId)
    python scripts/replay_check.py failures [--rows 12]     # each client's failed requests: Sim, machine, states, and
                                                            # whether the same server line failed on both clients

counters: a client's `SACS_RECORDER status` begins with the replay's own-driver counters (replayDrivers, borrowedNow,
acquiredOnSims: drivers now; ownRepairs, handovers, idleRepairStream, idleRepairDefault, idleRepairDupDropped: totals since
the mod loaded) and `SACS_RECORDER replay-failures`
gives failures=N (requests the client's game refused, a total). scenarios.py reads them on each watched client before
and after each scenario and its report shows the change (2026-10-21: the first crumple was found by eye).

drivers: from the raw `SACS_RECORDER actor <Sim>` rows (handle|machine|state|seq|cosmetic|alive) on the server and both
clients, every machine with two or more live, non-cosmetic drivers on one Sim. Two live Idle drivers on one body made
the crumpled pose of 2026-10-21 (d70d068); sim_compare --machines shows them as "machine:state x2" when they share a
state. A dead row (alive 0) is a replaced driver the recorder remembers, not a second one.

failures: each row of `SACS_RECORDER replay-failures` is `<result>|client=<client driver>|<server ring line>`, the ring
line `seq|R|serverDriver|actorHash|stateHash|flags|actor|wakeup|timeout|bridgeDriver|bridgeActor`. The decode names the
Sim (the actor's object id), the machine (the server driver's row in the server's `actor` reply, else the client
driver's on that client), the actor and state names (scripts/sacs_names.py) and the flags. The same server seq failing
on both clients puts the cause on the live stream path both share; one client alone, on what that client holds (its
late-join history, its own drivers). A row shows the newest ~10 failures (one bridge frame).

Live, read-only: bridge commands only (no probe, nothing changed on a game).
Tests: python -m unittest scripts.replay_check_test
"""
from __future__ import annotations

class _MirrorProcFallback:
    GAME_STATE = {"server": "server.state.json", "client": "client.state.json", "client2": "client2.state.json", "client3": "client3.state.json"}

    @staticmethod
    def parse_utc(text):
        from datetime import datetime
        if not text:
            return None
        try:
            return datetime.fromisoformat(str(text).replace("Z", "+00:00")).timestamp()
        except ValueError:
            return None

    @staticmethod
    def attached(state):
        return state.get("mode") == "attach" or any(
            isinstance(h, dict) and h.get("phase") == "host-starting" and h.get("detail") == "attach"
            for h in (state.get("history") or ())
        )

    @classmethod
    def game_refusal(cls, pid, started, identity=None):
        info = identity(pid) if identity else None
        if info is None:
            return "not running"
        image = info[0].replace("\\", "/").rsplit("/", 1)[-1]
        if image.lower() != "ts3w.exe":
            return f"{image}, not TS3W.exe"
        if started is not None and info[1] < started - 5:
            return "created before its controller started (a reused pid)"
        return None

    @classmethod
    def pick_game(cls, side, state, file_pid, identity=None):
        started = None if cls.attached(state) else cls.parse_utc(state.get("startedUtc"))
        refused = []
        for pid, source in ((state.get("gamePid"), f"{cls.GAME_STATE[side]} gamePid"), (file_pid, f"{side}-ts3.pid")):
            if not isinstance(pid, int) or pid <= 0:
                continue
            why = cls.game_refusal(pid, started, identity)
            if why:
                refused.append(f"{source} {pid}: {why}")
            else:
                return {"pid": pid, "source": source, "refused": refused}
        return {"pid": None, "source": None, "refused": refused} if refused else None

    @classmethod
    def live_games(cls, run_dir=None, identity=None):
        import json as _json
        from pathlib import Path as _Path
        run_dir = _Path(run_dir) if run_dir else (_Path(__file__).resolve().parent.parent / ".omo" / "run")
        found = {}
        for side, name in cls.GAME_STATE.items():
            try:
                state = _json.loads((run_dir / name).read_text(encoding="utf-8"))
            except (OSError, ValueError):
                state = {}
            try:
                file_pid = int((run_dir / f"{side}-ts3.pid").read_text(encoding="utf-8").strip())
            except (OSError, ValueError):
                file_pid = None
            game = cls.pick_game(side, state, file_pid, identity)
            if game is not None:
                found[side] = game
        return found

    @staticmethod
    def alive(pid):
        import ctypes
        import os as _os
        import sys as _sys
        if not pid or pid <= 0:
            return False
        if _sys.platform == "win32":
            h = ctypes.windll.kernel32.OpenProcess(0x1000 | 0x00100000, False, int(pid))
            if not h:
                return False
            try:
                return ctypes.windll.kernel32.WaitForSingleObject(h, 0) != 0
            finally:
                ctypes.windll.kernel32.CloseHandle(h)
        try:
            _os.kill(int(pid), 0)
            return True
        except OSError:
            return False

    @classmethod
    def game_pids(cls, run_dir=None):
        return {side: g["pid"] for side, g in cls.live_games(run_dir).items() if g.get("pid") and side in ("server", "client")}

    @staticmethod
    def debugger_kinds():
        return {}

    @staticmethod
    def wait_for_dumps(*args, **kwargs):
        return {}

    @staticmethod
    def writing_dumps(*args, **kwargs):
        return []

    @staticmethod
    def crash_reports(*args, **kwargs):
        return []

    @staticmethod
    def describe_report(*args, **kwargs):
        return ""

    @staticmethod
    def load_state(*args, **kwargs):
        return {}

    @staticmethod
    def save_state(*args, **kwargs):
        return None

    @staticmethod
    def side_game(*args, **kwargs):
        return None

    @staticmethod
    def detach_trace(*args, **kwargs):
        return False, "excluded in public mirror"


import argparse
import re
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))
import compare_client  # noqa: E402
from local_endpoints import SERVER_BRIDGECTL  # noqa: E402

SERVER = SERVER_BRIDGECTL
GAUGES = ("replayDrivers", "borrowedNow", "acquiredOnSims")  # drivers held now
# B2+(a): idleRepairStream, a puppet's dead standing idle re-created at the stream's state; idleRepairDefault, at
# the engine's creation entry (no streamed idle); idleRepairDupDropped, replayed requests for the state a repair had just
# entered, dropped before the engine's pump took the entry (RepairEntryGate).
TOTALS = ("ownRepairs", "handovers", "failures", "idleRepairStream", "idleRepairDefault", "idleRepairDupDropped")  # since the mod loaded
SESSIONS = ("sessions", "warmResets")  # controller sessions on this game, re-attaches that dropped one (health)
# Late-join resets this client applied (each Sim's seq-0 StopAllAnimation of a join history): it rises on this client's
# own joins only; a rise while another player joins is (a history broadcast to an established client).
JOINS = ("joinResets",)
# The fork's PresenterHook (SACS_RECORDER presenter-idle status): applied rises only on a real posture change, unchanged
# on the 10 s repeats; failures stays 0.
PRESENTER = ("postures.applied", "postures.unchanged", "postures.cleared", "postures.failures", "settles", "settles.skipped")
# The readout's order, what went wrong first. Every counter read stays in the record (records.json); the readout names
# these when they move (authority.Checked rises on every insert: kept, not shown).
COUNTERS = ("failures", "handovers", "ownRepairs", "idleRepairStream", "idleRepairDefault", "idleRepairDupDropped") + GAUGES + ("postures.applied", "postures.failures", "postures.cleared",
            "settles", "authority.Blocked", "authority.HeartbeatSkipped", "authority.FundsBlocked", "authority.LifecycleSkipped",
            "joinResets")


def games() -> dict[str, str]:
    """Each game's label and bridge endpoint: server, client1, client2, client3."""
    return {"server": SERVER} | {f"client{p}": compare_client.client_n(p) for p in compare_client.PLAYERS}


def label_of(endpoint: str) -> str:
    return next((name for name, e in games().items() if e == endpoint), endpoint)


def household() -> dict[str, str]:
    import quick_loop  # its household ids (one place)
    return dict(quick_loop.SIMS)


def bridge(endpoint: str, *args: str, timeout: int = 20) -> tuple[int, str]:
    """One bridge command's printed reply (bridgectl.py), and its exit code (non-zero: no reply or an error)."""
    try:
        proc = subprocess.run([sys.executable, "scripts/bridgectl.py", "--timeout", str(timeout), "--endpoint", endpoint, *args],
                              cwd=ROOT, capture_output=True, text=True, timeout=timeout + 20)
    except subprocess.TimeoutExpired:
        return 124, f"timed out after {timeout + 20}s"
    return proc.returncode, (proc.stdout or "") + (proc.stderr or "")


def parallel(calls: list[tuple]) -> list[tuple[int, str]]:
    """bridge() for each (endpoint, *args) at once: a scenario's boundary costs one round trip, not one per call."""
    if not calls:
        return []
    with ThreadPoolExecutor(max_workers=min(8, len(calls))) as pool:
        return list(pool.map(lambda call: bridge(*call), calls))


# --- counters ---------------------------------------------------------------------------------------------------------

def parse_counters(status: str, failures: str, presenter: str = "", authority: str = "") -> dict[str, int]:
    """The counters from a client's `SACS_RECORDER status`, `replay-failures`, `presenter-idle status` (the fork's
    PresenterHook: postures=applied:N,unchanged:N,cleared:N,failures:N, settles=N,skipped:N) and `authority` (the fork's
    AuthorityHook: authority;Mode=..;Checked=..;Blocked=.., as authority.<field>) replies; a counter a reply lacks (a mod
    or fork from before it) is left out."""
    found: dict[str, int] = {}
    for name in GAUGES + tuple(t for t in TOTALS if t != "failures") + SESSIONS + JOINS:
        m = re.search(rf"(?:^|[;\s]){name}=(\d+)", status)
        if m:
            found[name] = int(m.group(1))
    m = re.search(r"(?:^|\s)failures=(\d+)", failures)
    if m:
        found["failures"] = int(m.group(1))
    m = re.search(r"postures=applied:(\d+),unchanged:(\d+),cleared:(\d+),failures:(\d+)", presenter)
    if m:
        found.update(zip(PRESENTER[:4], map(int, m.groups())))
    m = re.search(r"settles=(\d+),skipped:(\d+)", presenter)
    if m:
        found.update(zip(PRESENTER[4:], map(int, m.groups())))
    line = next((ln for ln in authority.splitlines() if ln.startswith("authority;")), "")
    for name, value in re.findall(r";(\w+)=(-?\d+)", line):
        found["authority." + name] = int(value)
    return found


COUNTER_COMMANDS = (("status",), ("replay-failures",), ("presenter-idle", "status"), ("authority",))


def counter_calls(endpoints: list[str]) -> list[tuple]:
    return [(e, "SACS_RECORDER", *c) for e in endpoints for c in COUNTER_COMMANDS]


def read_counters(endpoints: list[str]) -> dict[str, dict]:
    """Each endpoint's counters ({name: n}), or {"error": why} for one that did not answer."""
    return counters_from(endpoints, parallel(counter_calls(endpoints)))


def counters_from(endpoints: list[str], replies: list[tuple[int, str]]) -> dict[str, dict]:
    out: dict[str, dict] = {}
    n = len(COUNTER_COMMANDS)
    for i, endpoint in enumerate(endpoints):
        (code_s, status), (_, failures), (_, presenter), (_, authority) = replies[n * i:n * i + n]
        counters = parse_counters(status, failures, presenter, authority)
        if code_s != 0 and not counters:
            out[endpoint] = {"error": (status.strip().splitlines() or ["no reply"])[-1][:160]}
        else:
            out[endpoint] = counters
    return out


def counter_change(before: dict, after: dict) -> dict[str, tuple[int, int]]:
    """The counters that changed: {name: (before, after)}; an error on either side gives no change."""
    if "error" in before or "error" in after:
        return {}
    return {name: (before[name], after[name]) for name in COUNTERS
            if name in before and name in after and before[name] != after[name]}


def change_text(change: dict[str, tuple[int, int]]) -> str:
    """"failures 5->7 (+2), handovers 0->1 (+1)", or "no change"."""
    if not change:
        return "no change"
    return ", ".join(f"{name} {a}->{b} ({b - a:+d})" for name, (a, b) in change.items())


def counters_text(counters: dict) -> str:
    if "error" in counters:
        return "no reply: " + counters["error"]
    return " ".join(f"{name}={counters[name]}" for name in COUNTERS if name in counters) or "(no replay counters: an older mod)"


# --- drivers ----------------------------------------------------------------------------------------------------------

def actor_rows(text: str) -> list[dict]:
    """The rows of a `SACS_RECORDER actor` reply: handle|machine x16|state|seq|cosmetic[|alive[|origin]] (a mod from
    before the alive column: every row alive; origin, who acquired the driver: replay, a mod path such as repair, game
    via its callers, adopted; "" from a mod before it)."""
    rows = []
    for line in text.splitlines():
        f = line.strip().split("|", 6)
        if len(f) in (5, 6, 7) and f[0].isdigit() and re.fullmatch(r"[0-9a-fA-F]{16}", f[1]):
            rows.append({"handle": f[0], "machine": f[1].lower(), "state": f[2], "seq": f[3], "cosmetic": f[4] == "1",
                         "alive": len(f) == 5 or f[5] == "1", "origin": f[6] if len(f) == 7 else ""})
    return rows


def doubles(rows: list[dict]) -> dict[str, list[dict]]:
    """The machines with two or more live, non-cosmetic drivers: {machine: [rows]}."""
    live: dict[str, dict[str, dict]] = {}
    for row in rows:
        if row["alive"] and not row["cosmetic"]:
            live.setdefault(row["machine"], {})[row["handle"]] = row
    return {machine: list(by_handle.values()) for machine, by_handle in live.items() if len(by_handle) > 1}


def double_text(game: str, sim: str, machine: str, rows: list[dict], names=None) -> str:
    names = names or _names()
    states = " ".join(f"{r['handle']}:{names.name32(r['state'])}" + (f" ({r['origin']})" if r.get("origin") else "")
                      for r in rows)
    return f"{game} {sim}: {names.name64(int(machine, 16))} x{len(rows)} (driver:state {states})"


def actor_calls(endpoints: dict[str, str], sims: dict[str, str]) -> list[tuple]:
    return [(endpoint, "SACS_RECORDER", "actor", sim_id) for endpoint in endpoints.values() for sim_id in sims.values()]


def drivers_check(endpoints: dict[str, str] | None = None, sims: dict[str, str] | None = None) -> tuple[list[str], list[str]]:
    """(findings, games that did not answer): every Sim with two live drivers of one machine on each game."""
    endpoints = endpoints or games()
    sims = sims or household()
    return doubles_from(endpoints, sims, parallel(actor_calls(endpoints, sims)))


def doubles_from(endpoints: dict[str, str], sims: dict[str, str], replies_list: list[tuple[int, str]]) -> tuple[list[str], list[str]]:
    replies = iter(replies_list)
    findings: list[str] = []
    silent: list[str] = []
    for game in endpoints:
        answered = False
        for sim, _ in sims.items():
            code, text = next(replies)
            if code != 0 and "machines=" not in text:
                continue
            answered = True
            for machine, rows in sorted(doubles(actor_rows(text)).items()):
                findings.append(double_text(game, sim, machine, rows))
        if not answered:
            silent.append(game)
    return findings, silent


# --- failures ---------------------------------------------------------------------------------------------------------

def parse_failures(text: str) -> tuple[int | None, list[dict]]:
    """(failures=N, the rows) of a `replay-failures` reply. A row: result, client driver, and the server ring line's
    seq, kind, server driver, actor hash, state hash, flags, actor object id and, for a request, its bridge driver."""
    m = re.search(r"(?:^|\s)failures=(\d+)", text)
    total = int(m.group(1)) if m else None
    rows = []
    for line in text.splitlines():
        f = line.strip().split("|")
        if len(f) < 9 or not f[1].startswith("client=") or not f[2].isdigit():
            continue
        rows.append({"result": f[0], "client_driver": f[1][len("client="):], "seq": f[2], "kind": f[3],
                     "server_driver": f[4], "actor_hash": f[5], "state": f[6], "flags": f[7], "actor": f[8],
                     "bridge_driver": f[11] if len(f) > 11 else "0", "line": "|".join(f[2:])})
    return total, rows


def machines_by_handle(text: str) -> dict[str, str]:
    """handle -> machine key from an `actor` reply's rows, live or dead."""
    return {row["handle"]: row["machine"] for row in actor_rows(text)}


def decode_failures(per_client: dict[str, str], server_actor: dict[str, str] | None = None,
                    client_actor: dict[tuple[str, str], str] | None = None, sims: dict[str, str] | None = None,
                    names=None) -> list[str]:
    """Lines for the failed requests of each client ({label: replay-failures reply}), newest first, with a header per
    client. server_actor: {actor id: the server's `actor` reply}; client_actor: {(label, actor id): that client's}: the
    machine of each driver. A server seq that failed on two clients is said once, as both."""
    names = names or _names()
    by_id = {v: k for k, v in (sims or {}).items()}
    server_machines = {}
    for text in (server_actor or {}).values():
        server_machines.update(machines_by_handle(text))
    parsed = {label: parse_failures(text) for label, text in per_client.items()}
    groups: dict[str, dict[str, dict]] = {}  # server seq|driver -> {client label: its row}, newest first
    for label, (_, rows) in parsed.items():
        for row in rows:
            groups.setdefault(row["seq"] + "|" + row["server_driver"], {}).setdefault(label, row)
    lines = [f"{label}: failures={'?' if total is None else total}, {len(rows)} shown" for label, (total, rows) in parsed.items()]
    for on in groups.values():
        label, row = next(iter(on.items()))
        machine = server_machines.get(row["server_driver"])
        for other, other_row in on.items():  # the server's rows lack it: a client's own row for its driver
            if machine is None:
                machine = machines_by_handle((client_actor or {}).get((other, row["actor"]), "")).get(other_row["client_driver"])
        machine_name = names.name64(int(machine, 16)) if machine else f"driver {row['server_driver']}"
        sim = by_id.get(row["actor"], row["actor"])
        where = f"on {' and '.join(on)}: the live path they share" if len(on) > 1 else f"on {label} only"
        results = sorted({r["result"] for r in on.values()})
        bridge_text = f" bridge {row['bridge_driver']}" if row["bridge_driver"] not in ("", "0") else ""
        flag_text = names.flags(row["flags"]) if row["flags"].isdigit() else row["flags"]
        drivers = " ".join(f"{other}:{r['client_driver']}" for other, r in on.items())
        lines.append(f"  seq {row['seq']} {'/'.join(results)} {sim} {machine_name} {names.name32(row['actor_hash'])}"
                     f"->{names.name32(row['state'])} [{flag_text}]{bridge_text}, server driver {row['server_driver']}, "
                     f"client drivers {drivers}, {where}")
    return lines


def failures_report(clients: list[str], sims: dict[str, str] | None = None) -> list[str]:
    """Read each client's failures, the server's and the clients' actor rows for the failing Sims, and decode."""
    sims = sims or household()
    replies = parallel([(e, "SACS_RECORDER", "replay-failures") for e in clients])
    per_client = {}
    for endpoint, (code, text) in zip(clients, replies):
        if code != 0 and "failures=" not in text:
            per_client[label_of(endpoint)] = ""
            continue
        per_client[label_of(endpoint)] = text
    actors = sorted({row["actor"] for text in per_client.values() for row in parse_failures(text)[1]})
    endpoints = {label_of(e): e for e in clients}
    calls = [(SERVER, "SACS_RECORDER", "actor", a) for a in actors]
    calls += [(endpoints[label], "SACS_RECORDER", "actor", a) for label in per_client for a in actors]
    replies = parallel(calls)
    server_actor = {a: text for a, (_, text) in zip(actors, replies[:len(actors)])}
    client_actor = {}
    rest = iter(replies[len(actors):])
    for label in per_client:
        for a in actors:
            client_actor[(label, a)] = next(rest)[1]
    return decode_failures(per_client, server_actor, client_actor, sims)


def _names():
    import sacs_names
    return sacs_names


def snapshot(clients: list[str], drivers_on: dict[str, str] | None = None, sims: dict[str, str] | None = None) -> dict:
    """One scenario boundary in one round trip: each client's counters ({label: counters}) and, with drivers_on
    ({game: endpoint}), the drivers check ("doubles": findings, "silent": games that did not answer)."""
    sims = sims or household()
    calls = counter_calls(clients)
    if drivers_on:
        calls += actor_calls(drivers_on, sims)
    replies = parallel(calls)
    n = len(counter_calls(clients))
    out: dict = {"counters": {label_of(e): c for e, c in counters_from(clients, replies[:n]).items()}}
    if drivers_on:
        out["doubles"], out["silent"] = doubles_from(drivers_on, sims, replies[n:])
    return out


# Counts that only rise while one TS3 runs the mod: one that fell means the game began again in between (a cold rejoin,
# a reboot), and a change from before would read as negatives (2026-10-21, late-join-bed under --rejoin cold on client
# 2: "failures 2->0 (-2), ..., authority.Blocked 164->16 (-148)").
MONOTONIC = ("sessions", "ownRepairs", "handovers", "idleRepairStream", "idleRepairDefault", "idleRepairDupDropped", "failures", "postures.applied", "postures.unchanged", "settles",
             "authority.Checked", "authority.Blocked", "authority.HeartbeatSkipped")


def restarted(before: dict, after: dict) -> bool:
    """Whether a client's counters began again between two readings (a count of MONOTONIC fell)."""
    if "error" in before or "error" in after:
        return False
    return any(name in before and name in after and after[name] < before[name] for name in MONOTONIC)


def boundary_changes(before: dict, after: dict) -> dict[str, str]:
    """{client label: change_text} of two snapshots' counters, for the clients both read; a client whose counters began
    again in between says so, with its counts since then."""
    b, a = before.get("counters", {}), after.get("counters", {})
    return {label: ("restarted in between (a count fell: its TS3 began again); since then: " + counters_text(a[label])
                    if restarted(b[label], a[label]) else change_text(counter_change(b[label], a[label])))
            for label in a if label in b}


def clients_up(candidates: list[str] | None = None) -> list[str]:
    """The client games that answer DIAG_STATE in the world now (client 1, client 2, client 3)."""
    candidates = candidates or compare_client.clients()
    replies = parallel([(e, "DIAG_STATE") for e in candidates])
    return [e for e, (code, text) in zip(candidates, replies) if "accepted=True" in text and "inWorld=True" in text]


def pick_sims(text: str) -> dict[str, str]:
    """--sims: household names (Jennifer,Pablo) or name=objectId pairs."""
    known = household()
    out = {}
    for item in filter(None, (s.strip() for s in text.split(","))):
        name, _, sim_id = item.partition("=")
        if sim_id:
            out[name] = sim_id
        elif name in known:
            out[name] = known[name]
        else:
            raise SystemExit(f"--sims {name}: not a household name ({', '.join(known)}); give name=objectId")
    return out


# Long-session health (scenarios.py health). A fresh boot is due at the values of the session that failed four scenarios
# (phone-seated, phone-standing, shower, sleep-bed) after 2.5 h and passed them on a fresh boot on both clients (run
# 20261021-024712: client 1 rejoined warm twice and a presentation module was hot-loaded; fresh: 20261021-033640,
# 20261021-033942). Which of them, if any, degraded it is not known (the session probe, internal documentation, tells them
# apart): each limit is that session's value until the probe says otherwise. pb-062: re-run a late fail on a fresh boot
# before logging it.
DUE_AGE_H = 2.5
DUE_WARM = 2
DUE_HOT_LOADS = 1
SIDES = {"server": "server", "client": "client1", "client2": "client2", "client3": "client3"}  # crash_catch.live_games' sides, our labels


def module_loads(text: str) -> tuple[str, int] | None:
    """(module, loads) from a client's SACS_REPLAY 00 report (presentation=module=NAME/loads:N/...); None from a mod
    before it. loads counts the packaged module's startup load too."""
    m = re.search(r"presentation=module=([^/;\s]*)/loads:(\d+)", text)
    return (m.group(1), int(m.group(2))) if m else None


def pick_processes(files: dict[str, dict], identity) -> dict[str, dict]:
    """Each game's TS3 from every checkout's stack files ({checkout: stack_who.checkout_files}): per role
    crash_catch.pick_game's pick (the state's gamePid, else the pid file, a running TS3W created after its controller),
    the newest controller's when several checkouts name a live one: {label: {pid, started, attached, checkout}}."""
    try:
        import crash_catch
    except ImportError:
        crash_catch = _MirrorProcFallback
    out: dict[str, dict] = {}
    for side, label in SIDES.items():
        best = None
        for tree, f in files.items():
            state = f["states"].get(side) or {}
            game = crash_catch.pick_game(side, state, f["pids"].get(side), identity)
            if not game or not game.get("pid"):
                continue
            began = crash_catch.parse_utc(state.get("startedUtc")) or 0.0
            if best is None or began > best[0]:
                found = identity(game["pid"])
                best = (began, {"pid": game["pid"], "started": found[1] if found else None,
                                "attached": crash_catch.attached(state), "checkout": tree})
        if best:
            out[label] = best[1]
    return out


def processes() -> dict[str, dict]:
    """Each game's TS3 now ({label: {pid, started, attached, checkout}}), whichever checkout booted it (stack_who's
    worktrees: a stack another session booted from its own worktree writes its state there, not here; 2026-10-21, the
    first live health showed every age as ?), with its creation time (process_identity, which a boxed client answers
    too) and whether its controller attached to a running game (a warm rejoin, or a server reattach). {} when that
    cannot be told."""
    try:
        try:
            import crash_catch
        except ImportError:
            crash_catch = _MirrorProcFallback
        try:
            import stack_who
        except ImportError:
            stack_who = None
        files = {tree.name: stack_who.checkout_files(tree) for tree in stack_who.worktrees()}
        return pick_processes(files, crash_catch.process_identity)
    except Exception:  # noqa: BLE001 - health still reads the bridges
        return {}


def health_facts(sims: dict[str, str] | None = None) -> dict:
    """What scenarios.py health reports, read in two parallel rounds: which games are in the world (DIAG_STATE), then
    each up game's counters (status: sessions, warmResets; replay-failures), each up client's module loads (SACS_REPLAY
    00) and the drivers check over every up game."""
    sims = sims or household()
    every = games()
    replies = parallel([(e, "DIAG_STATE") for e in every.values()])
    up = {label: e for (label, e), (_, text) in zip(every.items(), replies)
          if "accepted=True" in text and "inWorld=True" in text}
    clients = [e for label, e in up.items() if label != "server"]
    calls = counter_calls(list(up.values())) + [(e, "SACS_REPLAY", "00") for e in clients]
    replies = parallel(calls + actor_calls(up, sims))
    n = len(counter_calls(list(up.values())))
    counters = counters_from(list(up.values()), replies[:n])
    loads = {label_of(e): module_loads(text) for e, (_, text) in zip(clients, replies[n:n + len(clients)])}
    found, silent = doubles_from(up, sims, replies[n + len(clients):])
    return {"now": time.time(), "processes": processes(), "down": [label for label in every if label not in up],
            "games": {label: {"counters": counters.get(e, {}), "loads": loads.get(label)} for label, e in up.items()},
            "doubles": found, "silent": silent}


def health_lines(facts: dict) -> tuple[list[str], list[str]]:
    """(lines to print, why a fresh boot is due): per game its age, controller sessions and warm rejoins since its TS3
    started, its module loads (clients), replay failures and drivers held; the drivers check; the verdict; and a
    tokens line (NAME=N, underscores only) that an arms --after hook keeps on each row (row["hook"], a --gate or
    --stop-when hook.NAME). A value a game cannot give (an older mod, an unreadable process) shows as ?."""
    lines, due, tokens = [], [], []
    processes = facts.get("processes") or {}
    for label in ("server", "client1", "client2", "client3"):
        proc, game = processes.get(label) or {}, (facts.get("games") or {}).get(label)
        if game is None and not proc:
            continue
        parts = [f"{label:<8}"]
        if game is None:
            parts.append("down (no DIAG_STATE in the world)")
        started = proc.get("started")
        if started:
            age_h = (facts["now"] - started) / 3600
            parts.append(f"up {age_h:.1f} h (pid {proc['pid']})")
            tokens.append(f"{label}_ageMin={round(age_h * 60)}")
            if age_h >= DUE_AGE_H:
                due.append(f"{label} up {age_h:.1f} h (limit {DUE_AGE_H} h)")
        else:
            parts.append(f"up ? (pid {proc.get('pid', '?')})")
        if game is not None:
            c = game["counters"]
            if "sessions" in c:
                # Each controller session after the first on this TS3 re-attached to it (a warm rejoin; a cold one
                # restarts the TS3 and its counters). warmResets counts only the re-attaches that found something
                # presented to reset: two rejoins back to back made sessions 3 and warmResets 1 (the session probe,
                # arms 20261021-063733).
                rejoins = max(c["sessions"] - 1, 0)
                parts.append(f"sessions {c['sessions']}, warm rejoins {rejoins}"
                             + (f" ({c['warmResets']} reset what the last one presented)" if "warmResets" in c else ""))
                tokens.append(f"{label}_warmRejoins={rejoins}")
                if "warmResets" in c:
                    tokens.append(f"{label}_warmResets={c['warmResets']}")
                if rejoins >= DUE_WARM:
                    due.append(f"{label} {rejoins} warm rejoins (limit {DUE_WARM})")
            else:
                parts.append("warm rejoins ?" + (" (>=1: its controller attached)" if proc.get("attached") else "")
                             + " (a mod before the counter)")
            if label != "server":
                loads = game.get("loads")
                if loads:
                    hot = max(loads[1] - 1, 0)
                    parts.append(f"module {loads[0] or '-'} loads {loads[1]} ({hot} hot)")
                    tokens.append(f"{label}_hotLoads={hot}")
                    if hot >= DUE_HOT_LOADS:
                        due.append(f"{label} {hot} hot module load{'s' if hot != 1 else ''} (limit {DUE_HOT_LOADS})")
                else:
                    parts.append("module loads ?")
            if "failures" in c:
                parts.append(f"replay failures {c['failures']}")
                tokens.append(f"{label}_failures={c['failures']}")
            if "replayDrivers" in c:
                parts.append(f"replay drivers {c['replayDrivers']}")
        lines.append("  ".join(parts))
    found = facts.get("doubles") or []
    checked = [g for g in (facts.get("games") or {}) if g not in (facts.get("silent") or [])]
    if found:
        lines += ["two live drivers of one machine:"] + [f"  {line}" for line in found]
        due.append(f"{len(found)} Sim machine(s) with two live drivers")
    else:
        lines.append(f"drivers: no Sim has two live drivers of one machine on {', '.join(checked) or 'no game'}")
    if facts.get("silent"):
        lines.append(f"drivers: no actor reply from {', '.join(facts['silent'])}")
    tokens.append(f"doubles={len(found)}")
    if due:
        lines.append("fresh boot due (the values of run 20261021-024712's degraded session; re-run a fail on a fresh "
                     "boot before logging it, pb-062): " + "; ".join(due))
    else:
        lines.append(f"no fresh boot due (limits: {DUE_AGE_H} h up, {DUE_WARM} warm rejoins, {DUE_HOT_LOADS} hot "
                     "module load, a double driver)")
    tokens.append(f"boot_due={1 if due else 0}")
    lines.append("health: " + " ".join(tokens))
    return lines, due


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = p.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("counters", help="each client's replay counters")
    c.add_argument("--client", default="", help="1, 2 or HOST:PORT (default: each client that answers)")
    d = sub.add_parser("drivers", help="a Sim with two live drivers of one machine, on every game")
    d.add_argument("--sims", default="", help="household names or name=objectId pairs (default: the household)")
    f = sub.add_parser("failures", help="each client's failed state requests, decoded")
    f.add_argument("--client", default="", help="1, 2 or HOST:PORT (default: each client that answers)")
    a = p.parse_args(argv)
    if a.cmd == "drivers":
        sims = pick_sims(a.sims) if a.sims else None
        findings, silent = drivers_check(sims=sims)
        for line in findings:
            print(line)
        if silent:
            print("no reply from " + ", ".join(silent))
        if not findings:
            print("drivers: no Sim has two live drivers of one machine" + (" on the games that answered" if silent else ""))
        return 3 if findings else 0
    clients = [compare_client.resolve(a.client)] if a.client else clients_up()
    if not clients:
        print("no client game answers")
        return 2
    if a.cmd == "counters":
        for endpoint, counters in read_counters(clients).items():
            print(f"{label_of(endpoint)}: {counters_text(counters)}")
        return 0
    print("\n".join(failures_report(clients)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
