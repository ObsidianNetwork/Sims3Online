"""Watch Sims move on both games and report what a player would see as wrong: teleports, slides, jitter, snap turns and
animation states restarting mid-animation, and how far the client's path is from the server's.

    python scripts/motion_watch.py --seconds 20                     # Jennifer and Pablo
    python scripts/motion_watch.py Pablo --seconds 30 --capture     # plus frames around each client-only event
    python scripts/motion_watch.py --ids 16261654878872000736 --seconds 15
    python scripts/motion_watch.py --rescore .omo/run/motion/012307 --ticks Shower   # a machine's events in ticks

Both games sample the watched Sims on every simulator update (the mod's MotionWatch: position, facing, walking or not,
and the animation state its recorder last saw), on one wall clock. The tool pulls both, estimates the client's lag
behind the server, and classifies:
  teleport   a jump faster than any walk or run (> 6 m/s over at least 0.25 m)
  slide      moving more than 0.2 m in half a second without a walk (root motion in an animation also does this, so a
             slide counts as client-only when the server didn't move like that at the same moment: no server slide, and
             the server's Sim, lag-aligned, did not make the same move; a sit-down's 0.2 m root move on both games sits
             on the threshold and was flagged on one side only, two-chairs 2026-10-21)
  jitter     back-and-forth: four or more reversals of movement (or of turning) within a second
  stutter    stop-go: three or more drops from walking speed to standing within a second (a walk fighting an animation)
  snap turn  facing changes by more than 45 degrees within one update
  restart    the same animation state entered again within 3 s (or a state going back to the one before), more times on
             the client than on the server at that moment. A client restart with the mod's own repair of that machine
             among its cause lines (`state R/E ... by repair[:<caller>][/<entry>]`, RepairOwnMachines) names it: "(after a
             repair: caller z, entry stream)"; callers z (the batch end after a Z), po (a standing Po), walk (a client
             walk), entries stream/default from B2 on, "-" for what a label leaves out (plain `by repair`, older runs)
  tick cadence  a walking copy that moves on fewer than 75% of the simulator's ticks while the server's Sim moves on
             90% or more (the avoidance-scale 0 walk: a step every second tick, at 0.8 of the speed; 2026-10-17)
  pace       walking more than 15% slower or faster than the server's Sim at the same places, for a second or more
             (FastWalk after a door, a slow copy in a tight room; 2026-10-17)
  own request  an animation state the client's own game requested on the copy (`state R/E ... by game`) that the
             server's game did not request too (same machine and state, lag-aligned, within 1.5 s): a copy's own
             reaction overlay (react_generic, 97 watches of sleep-bed and shower), a route interrupt, a reset. A request
             both games make (the walk's own Idle:Standing bridge) pairs and is not flagged (2026-10-25)
Each client walk also gets a step line (report only): its median step in cm per simulator tick, the server's over the
walk it overlaps, and their ratio, so a walk's pace is one number (two-hud on client 2: 3.2 against 5.7, 0.56x).
Events are listed per side; those with no matching server event (lag-aligned, within 1.5 s) are marked CLIENT-ONLY.
An event whose caller chain runs through a vehicle (GetInVehicleRouteAction, SittingInVehicle, Vehicle.PutIn[Passenger],
Vehicle/Car.GetOut, CarNpcWithNpcDriver, VehicleFollowSubPathRouteAction) is tagged [vehicle] in events.txt and the
report, and each Sim gets a vehicle line per side: the span of those events, the frames seen, and on the client the
client-only events inside it.
Writes .omo/run/motion/<time>/ : report.txt, speed.png (speed over time, server and client, events marked) and with
--capture, frames.png (server|client frames before, at and after each client-only event). --capture also locks both
cameras onto the first named Sim with the game's own follow for the watch and releases them at its end (CameraFollow).
"""
from __future__ import annotations

import argparse
import bisect
import json
import math
import re
import subprocess
import sys
import threading
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import compare_client  # noqa: E402
import sacs_names as names  # noqa: E402
from local_endpoints import SERVER_BRIDGECTL  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
ENDPOINTS = {"server": SERVER_BRIDGECTL, "client": compare_client.endpoint()}  # --client 2: player 2's game
SIMS = {"jennifer": "18031287978496800304", "pablo": "18031287978496800272"}


def recorder(endpoint: str, *args: str) -> list[str]:
    out = subprocess.run([sys.executable, "scripts/bridgectl.py", "--endpoint", endpoint, "SACS_RECORDER", *args],
                         cwd=ROOT, capture_output=True, text=True, timeout=60).stdout
    return out.splitlines()


# (endpoint, object) -> [(ms, what)]: script placements with the mod path that made them, and the client's walks.
EVENTS: dict[tuple[str, str], list[tuple[int, str]]] = {}


def dump(endpoint: str) -> dict[str, list[tuple]]:
    """Every page over one bridge connection (a process per page took minutes). Pages are asked at 200000 chars: a mod
    with bulk replies sends the whole watch through one bulk slot in one round trip; an older one ignores the size and
    pages at 1500 (~20 samples, ~0.3 s a round trip)."""
    import bridgectl
    samples: dict[str, list[tuple]] = {}
    offset = 0
    sock = bridgectl.connect(endpoint, 60)
    for _ in range(5000):
        reply = str(bridgectl.roundtrip(sock, f"SACS_RECORDER watch-dump {offset} 200000").get("report", "")).splitlines()
        head = next((ln for ln in reply if ln.startswith("samples=")), "")
        for ln in reply:
            f = ln.strip().split("|")
            if len(f) >= 4 and f[0].isdigit() and f[2] == "E":
                what = "|".join(f[3:])
                parts = what.split(" ")
                # state <kind> <machine hex>:<state> by <who> (a request); sacs <event> <machine hex>:<state> (the machine's
                # own SACS event: StateEntered, BecameIdle, AnimationDone..., mods from 2026-10-17)
                if len(parts) >= 3 and parts[0] in ("state", "sacs"):
                    # every <machine hex>:<state> token (a catch-up note names the server's node too)
                    what = " ".join(state_name(w) if MACHINE_STATE.match(w) else w for w in parts)
                EVENTS.setdefault((endpoint, f[1]), []).append((int(f[0]), what))
                continue
            if len(f) in (10, 11) and f[0].isdigit():
                # (ms, x, y, z, fx, fz, walking, state, sequence[, simulator ticks]): a mod before 2026-10-17 sends no ticks
                samples.setdefault(f[1], []).append((int(f[0]), float(f[2]), float(f[3]), float(f[4]), float(f[5]), float(f[6]),
                                                     f[7] == "1", f[8], int(f[9])) + ((int(f[10]),) if len(f) == 11 else ()))
        m = re.search(r"next=(-?\d+)", head)
        if not m or int(m.group(1)) < 0:
            break
        offset = int(m.group(1))
    return samples


def heading(fx: float, fz: float) -> float:
    return math.degrees(math.atan2(fx, fz))


def angle_diff(a: float, b: float) -> float:
    d = (a - b + 180) % 360 - 180
    return d


MACHINE_STATE = re.compile(r"^[0-9a-f]{16}:\d+$")


def state_name(state: str) -> str:
    if ":" not in state:
        return state
    machine, st = state.split(":", 1)
    # The motion notes write the machine as 16 hex digits; anything else (a test's name) goes through as it is.
    key = int(machine, 16) if MACHINE_STATE.match(state) else machine
    return f"{names.name64(key)}:{names.name32(st)}"


STUTTER_WINDOW_MS = 120
# A walking Sim moves once a simulator tick (30 a second: about 6 cm in Walk, 9 cm in FastWalk), and a sample is one
# frame, which carries 0, 1 or 2 ticks: so a walking step after a still sample can hold two ticks of movement. With the
# simulator's tick count in the samples (mods from 2026-10-17), a walking step is judged over the ticks it spans; older
# samples judge it over the time since the Sim last moved, at most TELEPORT_BURST_MS. Standing and posture steps keep
# the per-sample check. The 0.1-0.3 m bursts every ~133 ms that once needed 150 ms here were the copy's avoidance
# scale 0 (a step every second tick, 2026-10-17, internal documentation); the worst normal phase is a still
# frame then a two-tick frame (~66 ms). Blind spot without ticks: a walking step under 6 m/s * 75 ms = 0.45 m is never
# a teleport (and slides need walking=0). With ticks, only a step under 0.25 m (the teleport floor) is.
TELEPORT_BURST_MS = 75
TICK_MS = 1000.0 / 30


def windowed_speeds(samples: list[tuple], window_ms: int) -> list[tuple[int, float]]:
    """(ms, m/s) at each sample: the distance from the sample about window_ms earlier, over that time."""
    out = []
    j = 0
    for i in range(len(samples)):
        while samples[i][0] - samples[j][0] > window_ms:
            j += 1
        span = samples[i][0] - samples[j][0]
        if span >= window_ms * 0.6:
            out.append((samples[i][0], math.hypot(samples[i][1] - samples[j][1], samples[i][3] - samples[j][3]) / (span / 1000.0)))
    return out


OWN_REQUEST = re.compile(r"^state ([RE]) (\S+) by game via (.*)$")
OWN_REQUEST_WINDOW_MS = 1500


def own_requests(server_events: list[tuple], client_events: list[tuple], lag: int) -> list[tuple[int, str, str]]:
    """(ms, "own request", detail) for each animation request the client's own game made on the copy that the server's
    game did not make too (the same machine:state by game within OWN_REQUEST_WINDOW_MS, lag-aligned). A copy's states
    are the server's, replayed: a request of its own is the copy's game choosing an animation (a reaction, a reset)."""
    server = [(t, m.group(2)) for t, what in server_events for m in [OWN_REQUEST.match(what)] if m]
    out = []
    for t, what in client_events:
        m = OWN_REQUEST.match(what)
        if not m:
            continue
        state = m.group(2)
        if any(st == state and abs((t - lag) - ts) <= OWN_REQUEST_WINDOW_MS for ts, st in server):
            continue
        frames = [f for f in m.group(3).split("<") if not f.startswith("StateMachineClient.")]
        out.append((t, "own request", f"{state} by the client's game via {'<'.join(frames[:3]) or m.group(3)[:80]}"))
    return out


# the mod's repair of a copy's own machine (RepairOwnMachines under RepairLabel: repair, repair:z, repair:po,
# repair:walk, with B2's /stream or /default after it). The state can hold spaces once named (Shower:Exit Working).
REPAIR_ENTRY = re.compile(r"^state [RE] (.+) by (repair(?::[\w-]+)?(?:/[\w-]+)?)$")
# The window of a client-only event's cause lines (main(): what moved it, just before and at it).
CAUSE_BEFORE_MS = 1200
CAUSE_AFTER_MS = 150


def repair_label(who: str) -> tuple[str, str]:
    """(caller, entry) of a repair label: repair:z/stream -> (z, stream); a part the label leaves out is "-"."""
    rest = who[len("repair"):]
    rest, _, entry = rest.partition("/")
    caller = rest[1:] if rest.startswith(":") else ""
    return caller or "-", entry or "-"


def repair_cause(client_events: list[tuple], t: int, detail: str) -> str | None:
    """"after a repair: caller C, entry K" when the copy's events in a restart's cause window hold the mod's repair of
    the restarted state's machine (the latest such entry), else None. detail: the restart's ("<Machine>:<state> entered
    again after N ms" or "back to <Machine>:<state> N ms after ...")."""
    state = detail[len("back to "):] if detail.startswith("back to ") else detail
    machine = state.split(":", 1)[0]
    found = None
    for et, what in client_events:
        if not t - CAUSE_BEFORE_MS <= et <= t + CAUSE_AFTER_MS:
            continue
        m = REPAIR_ENTRY.match(what)
        if m and m.group(1).split(":", 1)[0] == machine:
            found = m.group(2)
    if found is None:
        return None
    caller, entry = repair_label(found)
    return f"after a repair: caller {caller}, entry {entry}"


def reentries(samples: list[tuple], window_ms: int = 3000) -> list[tuple[int, str]]:
    """(ms, state name) of each state entered again within window_ms, straight away or after other states: what a client
    restart is matched against. The replay delivers the server's requests in batches, which can squeeze the server's
    Standing -> standing_idle -> Standing (1065 ms, under no restart rule) into 892 ms with the middle state 2 ms long
    (never sampled): a client "restart" the server made too (social-chat, 2026-10-15)."""
    out = []
    seen: dict[str, int] = {}
    last_seq = None
    for s in samples:
        if s[8] != last_seq and s[7] != "-":
            last_seq = s[8]
            if s[7] in seen and s[0] - seen[s[7]] < window_ms:
                out.append((s[0], state_name(s[7])))
            seen[s[7]] = s[0]
    return out


def cadence(samples: list[tuple]) -> float:
    """Median samples per position change while walking (1 = moves every sample). Samples are frames, so phase alone
    gives a still frame now and then (a frame without a tick); tick_share() is the exact measure when the samples carry
    the simulator's ticks."""
    gaps, n = [], 0
    for a, b in zip(samples, samples[1:]):
        if not (a[6] and b[6]):
            continue
        n += 1
        if abs(b[1] - a[1]) + abs(b[3] - a[3]) > 0.001:
            gaps.append(n)
            n = 0
    gaps.sort()
    return float(gaps[len(gaps) // 2]) if gaps else 0.0


def events(samples: list[tuple]) -> list[tuple[int, str, str]]:
    """(ms, kind, detail) for one side's samples of one Sim."""
    out: list[tuple[int, str, str]] = []
    if len(samples) < 3:
        return out
    # teleports and snap turns, step by step (a walking step over the time since the Sim last moved: TELEPORT_BURST_MS)
    still_since = samples[0][0]
    for a, b in zip(samples, samples[1:]):
        dt = max(1, b[0] - a[0]) / 1000.0
        d = math.hypot(b[1] - a[1], b[3] - a[3])
        span = dt
        if a[6] and b[6]:
            if len(a) > 9 and len(b) > 9:
                span = max(dt, max(1, b[9] - a[9]) * TICK_MS / 1000.0)
            else:
                span = max(dt, min(b[0] - still_since, TELEPORT_BURST_MS) / 1000.0)
        if d > 0.25 and d / span > 6.0:
            out.append((b[0], "teleport", f"{d:.2f} m in {span * 1000:.0f} ms ({state_name(b[7])}, walking={int(b[6])})"))
        if d > 0.001:
            still_since = b[0]
        turn = abs(angle_diff(heading(b[4], b[5]), heading(a[4], a[5])))
        if turn > 45 and dt < 0.1:
            out.append((b[0], "snap turn", f"{turn:.0f} deg in {dt * 1000:.0f} ms ({state_name(b[7])})"))
    # slides: half-second windows without a walk
    i = 0
    while i < len(samples):
        j = i
        while j < len(samples) and samples[j][0] - samples[i][0] < 500:
            j += 1
        window = samples[i:j]
        if len(window) > 2 and not any(s[6] for s in window):
            moved = math.hypot(window[-1][1] - window[0][1], window[-1][3] - window[0][3])
            if moved > 0.2:
                out.append((window[0][0], "slide", f"{moved:.2f} m in {(window[-1][0] - window[0][0])} ms without a walk ({state_name(window[-1][7])})"))
                i = j
                continue
        i = max(i + 1, j // 2 if j > i + 1 else i + 1)
    # jitter: reversals of movement / turning within a second
    steps = []
    for a, b in zip(samples, samples[1:]):
        dx, dz = b[1] - a[1], b[3] - a[3]
        if math.hypot(dx, dz) > 0.01:
            steps.append((b[0], dx, dz))
    reversals = [s2[0] for s1, s2 in zip(steps, steps[1:]) if s1[1] * s2[1] + s1[2] * s2[2] < 0]
    out += _bursts(reversals, "jitter", "movement reversed {n} times in 1 s")
    turns = []
    for a, b in zip(samples, samples[1:]):
        d = angle_diff(heading(b[4], b[5]), heading(a[4], a[5]))
        if abs(d) > 2:
            turns.append((b[0], d))
    flips = [t2[0] for t1, t2 in zip(turns, turns[1:]) if t1[1] * t2[1] < 0]
    out += _bursts(flips, "turn jitter", "turning reversed {n} times in 1 s")
    # stutter: speed dropping from a walk (> 0.8 m/s) to standing (< 0.15 m/s) again and again. Speed over a
    # STUTTER_WINDOW_MS window, not sample to sample: the game moves a walking Sim once a simulator tick (about 6 cm in
    # Walk, 9 cm in FastWalk) and a sample (a frame) holds 0, 1 or 2 ticks, so a smooth walk read sample to sample
    # showed 8 stops a second (2026-10-14). A real stop holds still for longer than the window.
    drops = []
    moving = False
    for t, v in windowed_speeds(samples, STUTTER_WINDOW_MS):
        if v > 0.8:
            moving = True
        elif v < 0.15 and moving:
            drops.append(t)
            moving = False
    out += _bursts(drops, "stutter", "stopped and started {n} times in 1 s", minimum=3)
    # animation restarts: a state entered again within 3 s, or going back to the one before
    entries = []
    last_seq = None
    for s in samples:
        if s[8] != last_seq and s[7] != "-":
            entries.append((s[0], s[7]))
            last_seq = s[8]
    for (t0, s0), (t1, s1) in zip(entries, entries[1:]):
        if s1 == s0 and t1 - t0 < 3000:
            out.append((t1, "restart", f"{state_name(s1)} entered again after {t1 - t0} ms"))
    for (t0, s0), (t1, s1), (t2, s2) in zip(entries, entries[1:], entries[2:]):
        if s2 == s0 and s1 != s0 and t2 - t0 < 1000:
            out.append((t2, "restart", f"back to {state_name(s2)} {t2 - t1} ms after {state_name(s1)}"))
    return sorted(out)


# A vehicle in an event's caller chain ("by game via A<B<...", callee first): a drive is a named cause, not a grep.
VEHICLE = re.compile(r"\b(GetInVehicleRouteAction|SittingInVehicle|Vehicle\.PutInPassenger|Vehicle\.PutIn|Vehicle\.GetOut"
                     r"|Car\.GetOut|CarNpcWithNpcDriver\.\w+|VehicleFollowSubPathRouteAction)\b")


PORTAL_LEG = re.compile(r"Portal\w*Action$")


def route_fields(evs: list[tuple]) -> list[list]:
    """The walk fields scripts/route_continuity.py scores, from a Sim's events (EVENTS rows (ms, what)): [ms, style, leg,
    door, phase, placement] rows, a field None where the event does not carry it. Server: the RouteRecorder's `route start|style|
    halt|end leg=N ...` notes (the style is its spec `hash|age|gender|species`); client: Walks.cs's `walk leg N <Type> in
    <Style>`, `walk style: the server's <Style> on leg N`, `walk end`. door: client, a portal action leg (PortalRouteThrough/PortalTraversal; every walk's first leg is a FollowSubPath, a hallway leg too); server, leg > 0 (unverified live). phase "halted" on a halt/end. placement: a snap, teleport or `back to` event."""
    out: list[list] = []
    for ms, what in evs:
        m = re.match(r"route (start|style) leg=(\d+) style=(\S+)", what)
        if m:
            leg = int(m.group(2))
            out.append([ms, m.group(3), leg, leg > 0, None, None])  # a Start after the first is a leg past a door portal
            continue
        m = re.match(r"route (halt|end) leg=(\d+)", what)
        if m:
            out.append([ms, None, int(m.group(2)), None, "halted", None])
            continue
        m = re.match(r"walk leg (\d+) (\w+) in (\w+)", what)
        if m:
            out.append([ms, m.group(3), int(m.group(1)), bool(PORTAL_LEG.match(m.group(2))), None, None])
            continue
        m = re.match(r"walk style: the server's (\S+) on leg (\d+) \(own (?:\S+ (\w+)|-)\)", what)
        if m:
            out.append([ms, m.group(1), int(m.group(2)), bool(PORTAL_LEG.match(m.group(3))) if m.group(3) else None, None, None])
            continue
        if what.startswith("walk end"):
            out.append([ms, None, None, None, "halted", None])
            continue
        if re.search(r"snap|teleport|back to", what):
            out.append([ms, None, None, None, None, what[:80]])
    return out


def saved_run(ids: list[str], data: dict, events: dict, endpoints: dict) -> dict:
    """What samples.json holds. `fields` (route_fields per side and Sim, 2026-11-01) is absent from older runs, so a
    reader takes it as optional. events: side -> the EVENTS mapping ((endpoint, oid) -> rows)."""
    ev = {side: {oid: events[side].get((endpoint, oid), []) for oid in ids} for side, endpoint in endpoints.items()}
    return {"ids": ids, "data": data, "events": ev,
            "fields": {side: {oid: route_fields(rows) for oid, rows in sims.items()} for side, sims in ev.items()}}


def vehicle_frames(what: str) -> list[str]:
    """The vehicle frames an event names, each once, in the order they appear."""
    return list(dict.fromkeys(VEHICLE.findall(what)))


def tagged(what: str) -> str:
    return "[vehicle] " + what if VEHICLE.search(what) else what


def vehicle_lines(sides: dict[str, list[tuple[int, str]]], marked: list[tuple]) -> list[str]:
    """One line per side with vehicle events: their span, count and frames; on the client, the client-only events
    (marked: (ms, kind, detail, only)) inside the span, 1.5 s either side."""
    lines = []
    for side, evs in sides.items():
        hits = [(t, vehicle_frames(what)) for t, what in evs if VEHICLE.search(what)]
        if not hits:
            continue
        first, last = hits[0][0], hits[-1][0]
        frames = list(dict.fromkeys(f for _, fs in hits for f in fs))
        inside = [kind for t, kind, _, only in marked if only and first - 1500 <= t <= last + 1500] if side == "client" else []
        lines.append(f"   vehicle {side:<6} {time.strftime('%H:%M:%S', time.gmtime(epoch_ms(first) / 1000))}-"
                     f"{time.strftime('%H:%M:%S', time.gmtime(epoch_ms(last) / 1000))}  {len(hits)} event(s): "
                     + ", ".join(frames)
                     + (f"; {len(inside)} client-only event(s) in it ({', '.join(dict.fromkeys(inside))})" if inside else ""))
    return lines


def _bursts(times: list[int], kind: str, text: str, minimum: int = 4) -> list[tuple[int, str, str]]:
    out = []
    i = 0
    while i < len(times):
        j = i
        while j < len(times) and times[j] - times[i] <= 1000:
            j += 1
        if j - i >= minimum:
            out.append((times[i], kind, text.format(n=j - i)))
            i = j
        else:
            i += 1
    return out


# A walking copy that moves on fewer of the ticks than this (while the server's Sim moves on SERVER_TICK_SHARE or more)
# walks in steps every second tick or so: avoidance scale 0 gave 50% at 0.8 of the speed (2026-10-17). Pauses of
# TICK_PAUSE ticks or more (a door, a turn in place) are not cadence and are left out.
TICK_SHARE_MIN = 0.75
SERVER_TICK_SHARE = 0.9
TICK_PAUSE = 3
TICK_MIN_STEPS = 30


def tick_steps(samples: list[tuple]) -> list[tuple[int, bool]]:
    """(ms, moved) for each one-tick step inside walking runs, between each run's first and last movement, without
    pauses of TICK_PAUSE ticks or more. Empty for samples without ticks."""
    out: list[tuple[int, bool]] = []
    if not samples or len(samples[0]) < 10:
        return out
    run: list[tuple[int, bool]] = []

    def close() -> None:
        moved = [k for k, (_, m) in enumerate(run) if m]
        if moved:
            kept = run[moved[0]:moved[-1] + 1]
            still = 0
            for k, (t, m) in enumerate(kept):
                if m:
                    if still < TICK_PAUSE:
                        out.extend(kept[k - still:k])
                    out.append((t, m))
                    still = 0
                else:
                    still += 1
        run.clear()

    for a, b in zip(samples, samples[1:]):
        if not (a[6] and b[6]):
            close()
            continue
        if b[9] - a[9] == 1:
            run.append((b[0], math.hypot(b[1] - a[1], b[3] - a[3]) > 0.001))
    close()
    return out


def tick_share(samples: list[tuple]) -> float | None:
    """Share of walking ticks on which the Sim moved (tick_steps), None without enough of them."""
    steps = tick_steps(samples)
    if len(steps) < TICK_MIN_STEPS:
        return None
    return sum(1 for _, m in steps if m) / len(steps)


def tick_cadence(client: list[tuple], server: list[tuple]) -> list[tuple[int, str, str]]:
    """(ms, kind, detail) for each client walking run that moved on too few ticks, against the server's whole share."""
    server_share = tick_share(server)
    if server_share is not None and server_share < SERVER_TICK_SHARE:
        return []
    out = []
    # a walking run: steps less than a second apart
    runs: list[list[tuple[int, bool]]] = []
    for step in tick_steps(client):
        if runs and step[0] - runs[-1][-1][0] < 1000:
            runs[-1].append(step)
        else:
            runs.append([step])
    for run in runs:
        if len(run) >= TICK_MIN_STEPS:
            share = sum(1 for _, m in run if m) / len(run)
            if share < TICK_SHARE_MIN:
                theirs = "-" if server_share is None else f"{server_share * 100:.0f}%"
                out.append((run[0][0], "tick cadence", f"moved on {share * 100:.0f}% of {len(run)} walking ticks (server {theirs})"))
    return out


# Walking speed against the server's at the same places: each client walking sample's speed (over +-PACE_HALF_MS) next
# to the server's speed at its nearest walking sample within PACE_RADIUS (and PACE_TIME_MS of the lag-shifted time),
# over one-second windows of the client's walk (its median speed against the server's median there). Two windows or
# more in a row more than PACE_TOLERANCE off, or any more than PACE_TOLERANCE_SHORT off, are a different pace: FastWalk
# after a door (1.33-1.38x for 1-3 s), the copy at avoidance scale 0 (0.75x for 4 s) or 0.01 in a tight bedroom
# (0.80x for 4 s). Clean walks read 0.9-1.1 with a lone window down to 0.82 now and then (FastWalk's speed changes
# with the step: 2.2-2.7 m/s) (2026-10-17). By place, not by time, so the lag and a copy that starts late do not
# matter; places the paths do not share are not compared.
PACE_TOLERANCE = 0.15
PACE_TOLERANCE_SHORT = 0.25
PACE_HALF_MS = 150
PACE_RADIUS = 0.4
PACE_TIME_MS = 4000
PACE_WINDOW_MS = 1000
PACE_MIN_SERVER_SPEED = 1.0


def walking_speeds(samples: list[tuple]) -> list[tuple[int, float, float, float]]:
    """(ms, x, z, m/s) at each walking sample: the distance over the samples within +-PACE_HALF_MS, all of them walking."""
    out = []
    j = k = 0
    n = len(samples)
    for i in range(n):
        t = samples[i][0]
        while samples[j][0] < t - PACE_HALF_MS:
            j += 1
        while k + 1 < n and samples[k + 1][0] <= t + PACE_HALF_MS:
            k += 1
        if k <= j or samples[k][0] - samples[j][0] < PACE_HALF_MS * 1.5 or not all(s[6] for s in samples[j:k + 1]):
            continue
        a, b = samples[j], samples[k]
        out.append((t, samples[i][1], samples[i][3], math.hypot(b[1] - a[1], b[3] - a[3]) / ((b[0] - a[0]) / 1000.0)))
    return out


def pace_windows(client: list[tuple], server: list[tuple], lag: int) -> list[tuple[int, float, float]]:
    """(client ms, client m/s, server m/s): the medians of each one-second window of client walking samples matched to
    the server's speed at the same place."""
    cells: dict[tuple[int, int], list] = {}
    for q in walking_speeds(server):
        cells.setdefault((int(q[1] // PACE_RADIUS), int(q[2] // PACE_RADIUS)), []).append(q)
    matched = []
    for t, x, z, v in walking_speeds(client):
        best = None
        cx, cz = int(x // PACE_RADIUS), int(z // PACE_RADIUS)
        for dx in (-1, 0, 1):
            for dz in (-1, 0, 1):
                for q in cells.get((cx + dx, cz + dz), ()):
                    d = math.hypot(q[1] - x, q[2] - z)
                    if abs((t - lag) - q[0]) <= PACE_TIME_MS and d <= PACE_RADIUS and (best is None or d < best[0]):
                        best = (d, q[3])
        # where the server's Sim was slow (starting, stopping, at a door) the speed changes too fast to compare by place
        if best is not None and best[1] >= PACE_MIN_SERVER_SPEED:
            matched.append((t, v, best[1]))
    out = []
    i = 0
    while i < len(matched):
        j = i
        while j < len(matched) and matched[j][0] - matched[i][0] < PACE_WINDOW_MS:
            j += 1
        window = matched[i:j]
        if len(window) >= 15 and window[-1][0] - window[0][0] >= PACE_WINDOW_MS * 0.7:
            mine = sorted(c for _, c, _ in window)[len(window) // 2]
            theirs = sorted(s for _, _, s in window)[len(window) // 2]
            out.append((window[0][0], mine, theirs))
        i = max(j, i + 1)
    return out


def walk_pace(client: list[tuple], server: list[tuple], lag: int) -> list[tuple[int, str, str]]:
    """(ms, kind, detail) for each stretch of windows walked slower or faster than the server at the same places."""
    out = []
    stretch: list[tuple[int, float, float]] = []

    def close() -> None:
        if stretch:
            mine = sum(w[1] for w in stretch) / len(stretch)
            theirs = sum(w[2] for w in stretch) / len(stretch)
            if len(stretch) >= 2 or abs(mine / theirs - 1) > PACE_TOLERANCE_SHORT:
                out.append((stretch[0][0], "pace", f"walked {mine / theirs:.2f}x the server's speed at the same places "
                                                   f"for {len(stretch)} s ({mine:.2f} m/s against {theirs:.2f})"))
            stretch.clear()

    for w in pace_windows(client, server, lag):
        off = w[2] >= PACE_MIN_SERVER_SPEED and abs(w[1] / w[2] - 1) > PACE_TOLERANCE
        if not off or (stretch and (w[1] > w[2]) != (stretch[-1][1] > stretch[-1][2])):
            close()
        if off:
            stretch.append(w)
    close()
    return out


# A walk's pace as one number: the median distance a walking Sim moves per simulator tick (a frame can carry two
# ticks, so a step is divided by the ticks it spans), over the ticks it moved on (a turn in place before a walk does
# not count). About 6 cm in Walk at full speed, 9 in FastWalk; a short walk that speeds up and slows down reads lower
# (2.2-3.2 cm for a sit-down's 1 m walk). In two-hud (arms 20261021-063735, all six runs) client 2 stepped 3.2 cm (Jennifer)
# and 2.2 (Pablo) against the server's 5.7 and 6.2, with client 1 at 5.8 and 6.2: pace said "0.51x for 2 s", which
# hid that the whole walk was half speed.
WALK_STEP_MIN_TICKS = 5
WALK_STEP_MOVED = 0.005  # metres a tick: less is standing (a turn in place)


def walk_runs(samples: list[tuple]) -> list[list[tuple]]:
    """Each stretch of consecutive walking samples."""
    runs: list[list[tuple]] = []
    walking = False
    for s in samples:
        if s[6]:
            if walking:
                runs[-1].append(s)
            else:
                runs.append([s])
        walking = bool(s[6])
    return runs


def step_cm(run: list[tuple]) -> tuple[float, int] | None:
    """(median cm a tick, ticks moved on) over a walking run, None without ticks or with fewer than
    WALK_STEP_MIN_TICKS ticks moved on."""
    if not run or len(run[0]) < 10:
        return None
    steps = []
    for a, b in zip(run, run[1:]):
        ticks = b[9] - a[9]
        if ticks > 0:
            per = math.hypot(b[1] - a[1], b[3] - a[3]) / ticks
            if per > WALK_STEP_MOVED:
                steps.extend([per] * ticks)  # a two-tick frame is two steps of the median
    if len(steps) < WALK_STEP_MIN_TICKS:
        return None
    steps.sort()
    return steps[len(steps) // 2] * 100, len(steps)


def walk_steps(client: list[tuple], server: list[tuple], lag: int) -> list[tuple[int, float, int, float | None, int]]:
    """(client ms, client cm/tick, its steps, server cm/tick or None, the server's steps) for each client walk, against
    the server walk its lag-shifted span overlaps most (else the nearest start within PACE_TIME_MS)."""
    theirs = [(r[0][0], r[-1][0], st) for r in walk_runs(server) if (st := step_cm(r))]
    out = []
    for run in walk_runs(client):
        mine = step_cm(run)
        if mine is None:
            continue
        a, b = run[0][0] - lag, run[-1][0] - lag
        best, overlap = None, 0
        for s0, s1, st in theirs:
            shared = min(b, s1) - max(a, s0)
            if shared > overlap:
                best, overlap = st, shared
        if best is None:
            near = min(theirs, key=lambda w: abs(w[0] - a), default=None)
            if near is not None and abs(near[0] - a) <= PACE_TIME_MS:
                best = near[2]
        out.append((run[0][0], mine[0], mine[1], best[0] if best else None, best[1] if best else 0))
    return out


def step_text(step: tuple[int, float, int, float | None, int]) -> str:
    _, mine, n, theirs, m = step
    if theirs is None:
        return f"step {mine:.1f} cm/tick on the client ({n} ticks), no server walk with it"
    return f"step {mine:.1f} cm/tick on the client, {theirs:.1f} on the server ({mine / theirs:.2f}x; {n} and {m} ticks)"


def sample_at(samples: list[tuple], t: int) -> tuple | None:
    """The first sample at or after t, or None outside the samples' span."""
    if not samples or t < samples[0][0] or t > samples[-1][0]:
        return None
    lo, hi = 0, len(samples) - 1
    while lo < hi:
        mid = (lo + hi) // 2
        if samples[mid][0] < t:
            lo = mid + 1
        else:
            hi = mid
    return samples[lo]


# Where each client walk left the copy against where the server's Sim came to rest: the step line says how fast a copy
# walked, this says where it ended up. two-hud 2026-10-21 (run 20261021-082909): the server's walk end line came at its
# Stop call while its Pablo walked on 0.53 m to his goal, so both copies were stopped and put back 0.54 m short; after a
# later overrun snap a copy then slid 0.47 m on with no walk and no recorded event.
REST_SETTLE_MS = 2000  # how long after its walk the copy has to settle where the server's Sim is
REST_OFF = 0.3  # metres from the server's rest that the line marks
REST_DRIFT = 0.25  # metres further apart once settled than at the copy's end that the line reports (a seated copy
# 2 s after its sit-down walk read 0.18 m, two-hud 080402: timing, not a drift)
REST_STILL_MS = 500  # the server's Sim moved under REST_STILL_MOVE metres over this long before the drift is judged
REST_STILL_MOVE = 0.05


def _at_or_before(samples: list[tuple], times: list[int], t: int) -> tuple | None:
    i = bisect.bisect_right(times, t) - 1
    return samples[i] if i >= 0 else None


def walk_rests(client: list[tuple], server: list[tuple], lag: int = 0) -> list[tuple[int, float, float, int | None]]:
    """(client walk start ms, metres from the server's rest, metres further apart once settled, ms the server's Sim
    walked on past the copy's end or None) for each client walk. A copy lags its Sim, so the server's rest is that Sim's
    place at the moment the copy's walk ends or, while it still walks then, where that walk ends: the lag estimate is
    not used (two-hud 2026-10-21 put Pablo's at 2950 ms). Settled is REST_SETTLE_MS later or at the copy's next walk:
    a move both games made (a sit-down's slide into the chair) is not counted, one only the copy made is."""
    out = []
    runs = walk_runs(client)
    times = [s[0] for s in server]
    client_times = [s[0] for s in client]
    for i, run in enumerate(runs):
        end = sample_at(client, run[-1][0] + 1)
        if end is None or end[6] or not times or end[0] < times[0]:
            continue
        j = bisect.bisect_left(times, end[0])
        if j >= len(server):
            continue
        walked_on = None
        if server[j][6]:
            k = j
            while k < len(server) and server[k][6]:
                k += 1
            if k >= len(server):
                continue
            walked_on, j = server[k][0] - server[j][0], k
        rest = server[j]
        off = math.hypot(end[1] - rest[1], end[3] - rest[3])
        until = end[0] + REST_SETTLE_MS
        if i + 1 < len(runs):
            until = min(until, runs[i + 1][0][0] - 1)
        mine, theirs = _at_or_before(client, client_times, until), _at_or_before(server, times, max(until, rest[0]))
        apart = math.hypot(mine[1] - theirs[1], mine[3] - theirs[3]) if mine and theirs else off
        # Only against a server Sim at rest: one still moving then (a shower loop's root slides) is a clip the copy
        # plays a little later, not a drift (shower 2026-10-21, motion 090726: 0.27 m).
        before = _at_or_before(server, times, max(until, rest[0]) - REST_STILL_MS)
        if theirs and before and math.hypot(theirs[1] - before[1], theirs[3] - before[3]) > REST_STILL_MOVE:
            apart = off
        out.append((run[0][0], off, max(0.0, apart - off), walked_on))
    return out


def rest_text(rest: tuple[int, float, float, int | None]) -> str:
    _, off, drift, walked_on = rest
    text = f"rest {off:.2f} m from the server's Sim"
    if walked_on is not None:
        text += f" (which walked {walked_on} ms longer)"
    if drift > REST_DRIFT:
        text += f", then {drift:.2f} m further apart without a walk"
    return text + ("   <- off" if off > REST_OFF or drift > REST_DRIFT else "")


SLIDE_WINDOW_MS = 500
SAME_MOVE_SLACK = 0.05  # metres, or a quarter of the move when larger


def moved_alike(server: list[tuple], client: list[tuple], t: int, lag: int, window_ms: int = SLIDE_WINDOW_MS) -> bool:
    """The server's Sim made the client's move from t (lag-aligned): the two displacements over the window differ by
    less than SAME_MOVE_SLACK (or a quarter of the client's move). One game's move can cross the slide threshold while the
    other's stays just under it, so matching only server slide events left a shared root move client-only."""
    c0, c1 = sample_at(client, t), sample_at(client, t + window_ms)
    s0, s1 = sample_at(server, t - lag), sample_at(server, t - lag + window_ms)
    if None in (c0, c1, s0, s1):
        return False
    cdx, cdz = c1[1] - c0[1], c1[3] - c0[3]
    sdx, sdz = s1[1] - s0[1], s1[3] - s0[3]
    return math.hypot(cdx - sdx, cdz - sdz) <= max(SAME_MOVE_SLACK, 0.25 * math.hypot(cdx, cdz))


# Two lags. best_lag is the path fit: how late the client's path best matches the server's. A copy that walks slower
# or repeats a walk later trails, and its path lag says so: 950-1350 ms in two-hud on client 2 (arms 20261021-063735),
# 2950-3000 ms in 083220 (the copy repeated the server's walk 3 s later). The stream lag is how long a server request
# takes to reach the copy: each "state R/E <machine>:<state> by game" paired with the copy's "by replay" of the same
# state, and the lowest of those delays, since the runner holds some lines behind a walk (two-hud's sit-down lines,
# 1.9 s) and only the unheld ones are the transport. Request lines alone gave a median of 45 ms (p90 65) over 346
# Sim-watches on 2026-10-21, more than 500 ms from the path lag in 45 of them. Late state entries are measured from the
# stream lag (a trailing copy's lateness read 975 ms "beyond the lag" of 950 for a 1.9 s delay); the distance, slide
# and pace comparisons keep the path lag.
STREAM_REQUEST = re.compile(r"state ([RE]) (\S+) by (game|replay)\b")
STREAM_LAG_MAX_MS = 3000


def stream_lag(server_events: list[tuple], client_events: list[tuple]) -> tuple[int, int] | None:
    """(lowest ms, pairs): each server request (state R/E by game) to the copy's first replay of the same request
    within STREAM_LAG_MAX_MS after it, each used once; None with fewer than two pairs."""
    def requests(evs: list[tuple], by: str) -> list[tuple[int, str]]:
        out = []
        for t, what in evs:
            m = STREAM_REQUEST.match(what)
            if m and m.group(3) == by:
                out.append((t, m.group(1) + " " + m.group(2)))
        return out

    theirs, mine = requests(server_events, "game"), requests(client_events, "replay")
    used: set[int] = set()
    deltas = []
    for ts, key in theirs:
        best = None
        for i, (tc, kc) in enumerate(mine):
            d = tc - ts
            if kc == key and i not in used and 0 <= d <= STREAM_LAG_MAX_MS and (best is None or d < best[1]):
                best = (i, d)
        if best is not None:
            used.add(best[0])
            deltas.append(best[1])
    if len(deltas) < 2:
        return None
    return min(deltas), len(deltas)


def best_lag(server: list[tuple], client: list[tuple]) -> tuple[int, float, float, float]:
    """(lag ms, mean distance, max distance, share of time > 0.3 m) with the client compared to the server lag ms earlier."""
    if not server or not client:
        return 0, 0.0, 0.0, 0.0
    st = [s[0] for s in server]

    def at(t: int) -> tuple | None:
        lo, hi = 0, len(st) - 1
        if t < st[0] or t > st[-1]:
            return None
        while lo < hi:
            mid = (lo + hi) // 2
            if st[mid] < t:
                lo = mid + 1
            else:
                hi = mid
        return server[lo]

    best = (0, float("inf"), 0.0, 0.0)
    for lag in range(0, 3001, 50):
        dists = []
        for c in client[::3]:
            s = at(c[0] - lag)
            if s is not None:
                dists.append(math.hypot(c[1] - s[1], c[3] - s[3]))
        if len(dists) > 10:
            mean = sum(dists) / len(dists)
            if mean < best[1]:
                best = (lag, mean, max(dists), sum(1 for d in dists if d > 0.3) / len(dists))
    return best if best[1] != float("inf") else (0, 0.0, 0.0, 0.0)


class CameraFollow(threading.Thread):
    """The game's own camera follow on a Sim, on both games, for a whole watch (--capture).

    Locks once at the start (ts3_view.follow: Camera.LockToSim, the portrait's "follow this Sim"), then every few
    seconds reads each game's follow (ts3_view.follow_state: CameraController.GetObjectFollowId against the Sim's id)
    and locks again only a game whose follow was lost (a lot change, a respawned copy, someone moving the camera). A
    Sim away (in a rabbit hole, out of the world) is left until it is back. Unlocks at the end in this same thread,
    after any lock still in flight, so both cameras are free afterwards. It replaced a camera lerp to the Sim every 3 s
    (ts3_view.focus), which made the captured frames choppy (2026-10-18). Each check is a probe upload on each game
    (bridgectl's blob slot), so a scenario step's own probe can wait behind one; after three checks in a row with no
    reply from either game the checks come four times less often."""

    LOCK_WORDS = {"held": "held", "relock": "not held", "away": "away, not locked", "error": "error"}

    def __init__(self, name: str, every: float = 5.0, timeout: float = 30.0, view=None):
        super().__init__(daemon=True)
        self.name, self.every, self.timeout, self.view = name, every, timeout, view
        self.halt = threading.Event()
        self.started = time.time()
        self.log: list[str] = []
        self.last: dict[str, str] = {}

    def call(self, fn, *args, sides: list[str] | None = None) -> dict[str, dict[str, str]]:
        try:
            return fn(*args, timeout=self.timeout) if sides is None else fn(*args, sides, timeout=self.timeout)
        except Exception as ex:  # a failed probe must not end the watch
            return {side: {"err": f"{type(ex).__name__}: {ex}"} for side in (sides or list(ENDPOINTS))}

    def note(self, action: str, results: dict[str, dict[str, str]]) -> None:
        """Log the lock, every change of a game's state (held, relock, away, error), each relock and the unlock."""
        at = f"+{time.time() - self.started:.1f}s"
        for side, fields in results.items():
            err = f" ({fields['err']})" if "err" in fields else ""
            if action == "unlock":
                self.log.append(f"{at} unlock {side}: {'error' if err else 'released'}{err}")
                continue
            state = self.view.decide(fields)
            if action == "check":
                if state != self.last.get(side):
                    self.log.append(f"{at} {side}: {state}{err}")
            else:
                self.log.append(f"{at} {action} {side}: {self.LOCK_WORDS[state]}{err}")
            self.last[side] = state

    def run(self) -> None:
        if self.view is None:
            import ts3_view
            self.view = ts3_view
        view = self.view
        misses = 0
        try:
            self.note("lock", self.call(view.follow, self.name))
            while not self.halt.wait(self.every * (4 if misses >= 3 else 1)):
                states = self.call(view.follow_state, self.name)
                self.note("check", states)
                decided = [view.decide(fields) for fields in states.values()]
                misses = misses + 1 if decided and all(d == "error" for d in decided) else 0
                lost = [side for side, fields in states.items() if view.decide(fields) == "relock"]
                if lost and not self.halt.is_set():
                    self.note("relock", self.call(view.follow, self.name, sides=lost))
        finally:
            self.note("unlock", self.call(view.unfollow))

    def summary(self) -> str:
        return "; ".join(self.log) if self.log else "nothing logged"


class FrameGrabber(threading.Thread):
    def __init__(self, fps: float):
        super().__init__(daemon=True)
        self.fps, self.frames, self.stop = fps, [], False

    def run(self):
        import ts3_view
        wins = ts3_view.windows()
        if "server" not in wins or "client" not in wins:
            return
        while not self.stop:
            t0 = time.time()
            try:
                a = ts3_view._crop(ts3_view.grab(wins["server"]), "centre")
                b = ts3_view._crop(ts3_view.grab(wins["client"]), "centre")
                self.frames.append((int(time.time() * 1000), a.resize((a.width // 2, a.height // 2)), b.resize((b.width // 2, b.height // 2))))
            except Exception:
                pass
            time.sleep(max(0.0, 1.0 / self.fps - (time.time() - t0)))


def epoch_ms(sample_ms: int) -> int:
    """MotionWatch stamps .NET ticks/10000 (ms since year 1); the frame grabber uses Unix ms."""
    return sample_ms - 62135596800000


def speed_plot(path: Path, sims: dict, results: dict) -> None:
    from PIL import Image, ImageDraw
    lanes = []
    for name, (server, client) in sims.items():
        if not server and not client:
            continue
        t0 = min(s[0] for s in (server or client) + (client or server))
        t1 = max(s[0] for s in (server or client) + (client or server))
        width, height = 1400, 180
        img = Image.new("RGB", (width, height + 22), (25, 25, 28))
        draw = ImageDraw.Draw(img)
        draw.text((6, 4), f"{name}: speed m/s (server cyan, client orange); events: client-only red, both grey", fill=(230, 230, 230))
        span = max(1, t1 - t0)

        def x(t):
            return int((t - t0) / span * (width - 20)) + 10

        def y(v):
            return height + 18 - int(min(v, 8.0) / 8.0 * (height - 10))

        for samples, colour in ((server, (80, 200, 230)), (client, (240, 150, 60))):
            pts = []
            for a, b in zip(samples, samples[1:]):
                dt = max(1, b[0] - a[0]) / 1000.0
                pts.append((x(b[0]), y(math.hypot(b[1] - a[1], b[3] - a[3]) / dt)))
            if len(pts) > 1:
                draw.line(pts, fill=colour, width=1)
        for t, kind, _, only in results.get(name, []):
            draw.line([(x(t), 20), (x(t), height + 18)], fill=(220, 60, 60) if only else (120, 120, 120))
            draw.text((x(t) + 2, 22), kind, fill=(220, 60, 60) if only else (150, 150, 150))
        lanes.append(img)
    if lanes:
        sheet = Image.new("RGB", (lanes[0].width, sum(l.height for l in lanes)), (0, 0, 0))
        y0 = 0
        for lane in lanes:
            sheet.paste(lane, (0, y0))
            y0 += lane.height
        sheet.save(path)


def frame_sheet(path: Path, frames: list, moments: list[tuple[int, str]]) -> None:
    from PIL import Image, ImageDraw
    if not frames or not moments:
        return
    rows = []
    for t, label in moments[:8]:
        picks = []
        for offset in (-700, 0, 700):
            best = min(frames, key=lambda f: abs(f[0] - (t + offset)))
            picks.append((offset, best))
        w, h = picks[0][1][1].width, picks[0][1][1].height
        row = Image.new("RGB", ((w * 2 + 4) * 3 + 12, h + 16), (20, 20, 20))
        draw = ImageDraw.Draw(row)
        for k, (offset, (ft, a, b)) in enumerate(picks):
            x0 = k * (w * 2 + 10)
            row.paste(a, (x0, 16))
            row.paste(b, (x0 + w + 4, 16))
            draw.text((x0 + 2, 2), f"{label}  {offset:+d} ms  (server | client)", fill=(235, 235, 235))
        rows.append(row)
    sheet = Image.new("RGB", (rows[0].width, sum(r.height + 6 for r in rows)), (50, 50, 50))
    y0 = 0
    for r in rows:
        sheet.paste(r, (0, y0))
        y0 += r.height + 6
    sheet.save(path)


STATE_MATCH_MS = (-2000, 8000)


def entered_states(evs: list[tuple]) -> list[tuple[int, str]]:
    """(ms, machine:state) for each SACS StateEntered a game noted (sacs StateEntered <machine>:<state>)."""
    return [(t, w.split(" ", 2)[2]) for t, w in evs if w.startswith("sacs StateEntered ") and w.count(" ") >= 2]


def pair_states(server: list[tuple[int, str]], client: list[tuple[int, str]], lag: int) -> list[int | None]:
    """For each server entry, the index of the client's entry of the same machine:state nearest it (after the lag)
    within STATE_MATCH_MS, or None. Each client entry is used once, so a copy with an entry more or less pairs the rest
    by time, not by order."""
    used: set[int] = set()
    out: list[int | None] = []
    for ts, name in server:
        best = None
        for i, (tc, cn) in enumerate(client):
            d = tc - lag - ts
            if cn == name and i not in used and STATE_MATCH_MS[0] <= d <= STATE_MATCH_MS[1] and (best is None or abs(d) < abs(best[1])):
                best = (i, d)
        if best is not None:
            used.add(best[0])
        out.append(best[0] if best else None)
    return out


def state_entries(server_events: list[tuple], client_events: list[tuple], lag: int) -> list[tuple[int, str, int | None]]:
    """(server ms, machine:state, client delay beyond the motion lag or None) for each state the server's machine entered.

    From the SACS StateEntered events both games note (sacs StateEntered <machine>:<state>, mods from 2026-10-17): when
    each game's machine actually entered a state, not when it was asked to. A request reaches a copy at a different
    point in its clip than the server's (the shower's exit waited for the copy's own Loop clip to end: 0-1.8 s late), so
    a late entry here names the state and the time a copy fell behind. Each client entry is used once, the nearest within
    STATE_MATCH_MS of the server's (after the lag)."""
    server, client = entered_states(server_events), entered_states(client_events)
    return [(ts, name, None if j is None else client[j][0] - lag - ts)
            for (ts, name), j in zip(server, pair_states(server, client, lag))]


STATE_LENGTH_REPORT_MS = 250


def entry_spans(entries: list[tuple[int, str]]) -> list[tuple[int, str] | None]:
    """Each entry's (length, next state): ms to the same machine's next StateEntered and the state it names. None for a
    machine's last entry (a watch that ends mid-state does not know how long that state ran). A state's length depends
    on where it goes (Loop Shower back to itself is a loop variant; to Exit Working, the way out), so lengths are only
    compared between entries that go to the same state."""
    out: list[tuple[int, str] | None] = []
    for i, (t, name) in enumerate(entries):
        machine = name.split(":", 1)[0]
        nxt = next(((u, n) for u, n in entries[i + 1:] if n.split(":", 1)[0] == machine), None)
        out.append(None if nxt is None else (nxt[0] - t, nxt[1].split(":", 1)[1]))
    return out


def state_lengths(server_events: list[tuple], client_events: list[tuple], lag: int) -> list[tuple[str, int, int]]:
    """("machine:state -> next state", server ms, client ms) for each state both games entered (paired as
    state_entries pairs them) and left for the same state, whose length differs by STATE_LENGTH_REPORT_MS or more.

    The same requests at nearly the same times give a state the same length on both games unless a game chooses
    something on its own. Two native choices do this (2026-10-18): the loop variant a machine plays and the clip that times a state
    with parallel clips. Report only: the
    scenario verdict does not use it yet. `sacs_timing.py states` surveys it over saved runs."""
    server, client = entered_states(server_events), entered_states(client_events)
    ss, cs = entry_spans(server), entry_spans(client)
    out: list[tuple[str, int, int]] = []
    for k, j in enumerate(pair_states(server, client, lag)):
        if j is None or ss[k] is None or cs[j] is None or ss[k][1] != cs[j][1]:
            continue
        if abs(cs[j][0] - ss[k][0]) >= STATE_LENGTH_REPORT_MS:
            out.append((f"{server[k][1]} -> {ss[k][1]}", ss[k][0], cs[j][0]))
    return out


HOLD_LIMIT_MS = 3000
HOLD_REPORT_MS = 150
HOLD_MOVE_MS = 330


def event_holds(samples: list[tuple], evs: list[tuple]) -> list[tuple[str, int, int | None]]:
    """(machine:id, ms, simulator ticks or None) for each clip event a game noted (sacs ScriptEvent <machine>:<state>
    id=N): how long the Sim's root stayed still after it, until its first move (over 0.5 mm, or a turn), at most
    HOLD_LIMIT_MS. A clip that pauses on one game shows here as a longer hold; the clip after it then plays the
    same path on both games."""
    out: list[tuple[str, int, int | None]] = []
    for t, w in evs:
        if not w.startswith("sacs ScriptEvent ") or "id=" not in w:
            continue
        key = w.split(" ", 2)[2].split(":", 1)[0] + ":" + w.rsplit("id=", 1)[1].strip()
        after = [s for s in samples if t - 40 <= s[0] <= t + HOLD_LIMIT_MS]
        if len(after) < 2:
            continue
        first, prev, moved = after[0], after[0], None
        for i, s in enumerate(after[1:], 1):
            if math.hypot(s[1] - prev[1], s[3] - prev[3]) > 0.0005 or abs(s[4] - prev[4]) + abs(s[5] - prev[5]) > 0.001:
                # A real move, not a settle: 1 cm or 2 degrees within HOLD_MOVE_MS (a Sim idle at a clip's end twitches).
                ahead = [u for u in after[i:] if u[0] - s[0] <= HOLD_MOVE_MS]
                if any(math.hypot(u[1] - prev[1], u[3] - prev[3]) >= 0.01
                       or abs(angle_diff(heading(u[4], u[5]), heading(prev[4], prev[5]))) >= 2 for u in ahead):
                    moved = s
                    break
            prev = s
        machine = w.split(" ", 2)[2].split(":", 1)[0]
        if moved is None or any(t <= u < moved[0] and x.startswith(f"sacs BecameIdle {machine}:") for u, x in evs):
            continue  # no move, or the clip ended first: what moved the Sim then (a route, a turn) is not this clip
        ticks = moved[9] - first[9] if len(moved) > 9 and len(first) > 9 else None
        out.append((key, moved[0] - t, ticks))
    return out


def clip_holds(server: list[tuple[str, int, int | None]], client: list[tuple[str, int, int | None]]) -> list[tuple[str, tuple, tuple]]:
    """(machine:id, server hold, client hold) for each clip event both games noted, paired by order per machine:id,
    where the holds differ by HOLD_REPORT_MS or more."""
    def by_key(rows: list) -> dict[str, list]:
        out: dict[str, list] = {}
        for row in rows:
            out.setdefault(row[0], []).append(row[1:])
        return out

    s, c = by_key(server), by_key(client)
    return [(k, sh, ch) for k in s if k in c for sh, ch in zip(s[k], c[k]) if abs(ch[0] - sh[0]) >= HOLD_REPORT_MS]


def event_ticks(samples: list[tuple], evs: list[tuple], machine: str) -> list[tuple[str, list[tuple[str, int | None]]]]:
    """A machine's SACS events in simulator ticks, in segments: from its first StateEntered, then from each request
    after that (state R <machine>:<state>). Each event is (label, ticks from the segment's start, or None without
    ticks): in:<state>, done (AnimationDone), ev<id> (ScriptEvent), int (Interrupted), idle (BecameIdle). Which node
    of a state holds it shows as the tick where the next state starts."""
    times = [s[0] for s in samples]

    def tick(t: int) -> int | None:
        if not samples or len(samples[0]) < 10:
            return None
        return samples[min(bisect.bisect_left(times, t), len(samples) - 1)][9]

    segments: list[tuple[str, list[tuple[str, int | None]]]] = []
    start: int | None = None
    for t, w in evs:
        if w.startswith(f"state R {machine}:") and segments:
            segments.append(("R " + w.split(":", 1)[1].split(" by ", 1)[0], []))
            start = tick(t)
            continue
        if not w.startswith("sacs ") or f" {machine}:" not in w:
            continue
        kind = w.split(" ", 2)[1]
        if kind == "StateEntered" and not segments:
            segments.append(("in:" + w.split(":", 1)[1].strip(), []))
            start = tick(t)
        if not segments:
            continue
        label = {"AnimationDone": "done", "Interrupted": "int", "BecameIdle": "idle"}.get(kind, kind)
        if kind == "ScriptEvent":
            label = "ev" + w.rsplit("id=", 1)[1].strip()
        elif kind == "StateEntered":
            label = "in:" + w.split(":", 1)[1].strip()
        now = tick(t)
        segments[-1][1].append((label, None if now is None or start is None else now - start))
    return segments


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("sims", nargs="*", default=["Jennifer", "Pablo"])
    p.add_argument("--ids", default="", help="object ids to watch instead (comma-separated)")
    p.add_argument("--seconds", type=int, default=20)
    p.add_argument("--stop-file", default="", help="stop early: once this file exists, watch the seconds it holds more, then end"
                   " (scenarios.py writes it when its steps are done)")
    p.add_argument("--capture", action="store_true", help="grab server|client frames meanwhile (about 4 a second)")
    p.add_argument("--events", action="store_true", help="list every placement and walk event on both games")
    p.add_argument("--rescore", default="", help="a run directory: score its saved samples again (no watching)")
    p.add_argument("--client", default="", help="the client game to compare: 1 (default), 2 (player 2) or HOST:PORT"
                   " (compare_client.py; SIMSONLINE_COMPARE_CLIENT)")
    p.add_argument("--ticks", default="", metavar="MACHINE", help="list this SACS machine's events on both games in"
                   " simulator ticks, from its first state and from each request (event_ticks)")
    a = p.parse_args(argv)
    if a.client:
        ENDPOINTS["client"] = compare_client.use(a.client)
    known = dict(SIMS)
    if not a.rescore:
        # any other first name (a Langerak:) is found on the server, its id not fixed across boots
        import household_read
        for s in a.sims:
            if s.lower() not in known and not s.isdigit():
                known[s.lower()] = household_read.sim_id(s)
    ids = [s for s in a.ids.split(",") if s] or [known.get(s.lower(), s) for s in a.sims]
    label = {v: k.capitalize() for k, v in known.items()}
    grabber = None
    if a.rescore:
        # A detector changed: score a run again from its saved samples and events (samples.json), offline.
        out = Path(a.rescore)
        saved = json.loads((out / "samples.json").read_text(encoding="utf-8"))
        data = {side: {oid: [tuple(s) for s in rows] for oid, rows in sims.items()} for side, sims in saved["data"].items()}
        for side, sims in saved["events"].items():
            for oid, rows in sims.items():
                EVENTS[(ENDPOINTS[side], oid)] = [tuple(r) for r in rows]
        ids = [oid for oid in saved.get("ids", ids)]
    else:
        out = ROOT / ".omo" / "run" / "motion" / (time.strftime("%H%M%S") + compare_client.label(ENDPOINTS["client"]))
        out.mkdir(parents=True, exist_ok=True)
        for side, endpoint in ENDPOINTS.items():
            reply = recorder(endpoint, "watch", ",".join(ids), str(a.seconds))
            print(f"{side}: " + next((ln.strip() for ln in reply if "watching" in ln or "error" in ln.lower()), "no reply"))
        grabber = FrameGrabber(4.0) if a.capture else None
        follow = CameraFollow(a.sims[0]) if a.capture and a.sims and not a.ids else None
        for thread in (follow, grabber):
            if thread:
                thread.start()
        try:
            # The whole --seconds, or until the stop file appears plus the tail it names (the scenario's steps are done: only
            # the last clip's end is left to see). A suite spent most of each scenario watching Sims stand idle.
            deadline = time.time() + a.seconds + 1.5
            stop = Path(a.stop_file) if a.stop_file else None
            while time.time() < deadline:
                if stop is not None and stop.exists():
                    try:
                        tail = float(stop.read_text(encoding="utf-8").strip() or "0")
                    except (OSError, ValueError):
                        tail = 0.0
                    deadline = min(deadline, time.time() + tail)
                    stop = None
                time.sleep(0.2)
        finally:
            # Also on Ctrl+C: the follow thread is a daemon, so without this its unlock would never run.
            if grabber:
                grabber.stop = True
                grabber.join(timeout=10)
            if follow:
                # The follow thread unlocks both cameras itself once any check or lock in flight is done.
                follow.halt.set()
                follow.join(timeout=4 * follow.timeout + 10)
                busy = "; still busy: a camera may stay locked (scripts/ts3_view.py unfollow)" if follow.is_alive() else ""
                print(f"camera follow on {follow.name}: {follow.summary()}{busy}")
        # Both games at once: the mod pages about 20 samples per bridge round trip, so a 45 s watch is ~70 round trips
        # a side, and fetching the sides one after the other was most of a scenario's watch phase.
        from concurrent.futures import ThreadPoolExecutor
        with ThreadPoolExecutor(max_workers=len(ENDPOINTS)) as pool:
            futures = {side: pool.submit(dump, endpoint) for side, endpoint in ENDPOINTS.items()}
        data = {side: future.result() for side, future in futures.items()}
        # Everything the run saw, so a changed detector can score it again (--rescore).
        (out / "samples.json").write_text(json.dumps(saved_run(ids, data, {side: EVENTS for side in ENDPOINTS}, ENDPOINTS)), encoding="utf-8")

    lines = []
    results: dict[str, list] = {}
    lengths: dict[str, list] = {}
    steps: dict[str, list] = {}
    rests: dict[str, list] = {}
    sims_samples: dict[str, tuple] = {}
    only_moments = []
    repair_restarts = 0  # client-only restarts after the mod's repair of that machine (repair_cause)
    for oid in ids:
        name = label.get(oid, oid)
        server, client = data["server"].get(oid, []), data["client"].get(oid, [])
        sims_samples[name] = (server, client)
        lag, mean, worst, share = best_lag(server, client)
        rate = (len(client) / max(1, (client[-1][0] - client[0][0]) / 1000.0)) if len(client) > 1 else 0
        ticks = ""
        shares = (("server", tick_share(server)), ("client", tick_share(client)))
        if any(v is not None for _, v in shares):
            ticks = "; moved on " + ", ".join(f"{v * 100:.0f}% of walking ticks on the {side}" for side, v in shares if v is not None)
        client_events = EVENTS.get((ENDPOINTS["client"], oid), [])
        stream = stream_lag(EVENTS.get((ENDPOINTS["server"], oid), []), client_events)
        entries = state_entries(EVENTS.get((ENDPOINTS["server"], oid), []), client_events, stream[0] if stream else lag)
        entry_text = ", ".join(f"{n} {'missing' if d is None else f'{d:+d}'}" for _, n, d in entries[:16])
        holds = clip_holds(event_holds(server, EVENTS.get((ENDPOINTS["server"], oid), [])), event_holds(client, client_events))
        hold_text = ", ".join(f"{k} server {sh[0]} ms{f' ({sh[1]} ticks)' if sh[1] is not None else ''}, client {ch[0]} ms"
                              f"{f' ({ch[1]} ticks)' if ch[1] is not None else ''}" for k, sh, ch in holds)
        paces = " ".join(f"{c / s:.2f}" for _, c, s in pace_windows(client, server, lag) if s >= PACE_MIN_SERVER_SPEED)
        lengths[name] = state_lengths(EVENTS.get((ENDPOINTS["server"], oid), []), client_events, lag)
        length_text = ", ".join(f"{n} server {s}, client {c}" for n, s, c in lengths[name])
        lag_text = f"client lag {lag} ms (path)" + (f", stream {stream[0]} ms (n{stream[1]})" if stream else "")
        lines.append(f"== {name}: server {len(server)} samples, client {len(client)} ({rate:.0f}/s); {lag_text}; "
                     f"distance after lag: mean {mean:.2f} m, max {worst:.2f} m, {share * 100:.0f}% of the time over 0.3 m; "
                     f"walking: a move every {cadence(server):g} sample(s) on the server, {cadence(client):g} on the client{ticks}"
                     + (f"; walking speed against the server's at the same places, a second each: {paces}" if paces else "")
                     + (f"; states entered on the client after the server (ms beyond the {'stream' if stream else 'path'} lag): "
                        f"{entry_text}" if entry_text else "")
                     + (f"; states longer on one game (ms): {length_text}" if length_text else "")
                     + (f"; still after a clip event, longer on one game: {hold_text}" if hold_text else ""))
        steps[name] = walk_steps(client, server, lag)
        rests[name] = walk_rests(client, server)
        walk_lines = [(st[0], step_text(st)) for st in steps[name]] + [(r[0], rest_text(r)) for r in rests[name]]
        for t, text in sorted(walk_lines, key=lambda w: w[0]):
            lines.append(f"   walk    {time.strftime('%H:%M:%S', time.gmtime(epoch_ms(t) / 1000))}  {text}")
        if a.ticks:
            for side, rows in (("server", server), ("client", client)):
                for head, evs_ in event_ticks(rows, EVENTS.get((ENDPOINTS[side], oid), []), a.ticks):
                    lines.append(f"   ticks {side:<6} from {head}: " + " ".join(f"{k}@{'?' if n is None else n}" for k, n in evs_))
        sev = events(server)
        # tick cadence and pace compare the two games themselves: never on the server's list, so always client-only
        cev = sorted(events(client) + tick_cadence(client, server) + walk_pace(client, server, lag)
                     + own_requests(EVENTS.get((ENDPOINTS["server"], oid), []), client_events, stream[0] if stream else lag))
        server_reentries = reentries(server)
        marked = []
        for t, kind, detail in cev:
            matched = any(k == kind and abs((t - lag) - ts) <= 1500 for ts, k, _ in sev)
            if not matched and kind == "restart":
                matched = any(abs((t - lag) - ts) <= 1500 and f"{st} " in detail + " " for ts, st in server_reentries)
            if not matched and kind == "slide":
                matched = moved_alike(server, client, t, lag)
            cause = repair_cause(client_events, t, detail) if kind == "restart" else None
            if cause:
                detail = f"{detail} ({cause})"
                if not matched:
                    repair_restarts += 1
            marked.append((t, kind, detail, not matched))
        for t, kind, detail in sev:
            lines.append(f"   server  {time.strftime('%H:%M:%S', time.gmtime(epoch_ms(t) / 1000))}  {kind:<11} {detail}")
        for t, kind, detail, only in marked:
            lines.append(f"   client  {time.strftime('%H:%M:%S', time.gmtime(epoch_ms(t) / 1000))}  {kind:<11} {detail}"
                         + ("   <- CLIENT-ONLY" if only else ""))
            if only:
                only_moments.append((epoch_ms(t), f"{name} {kind}"))
                # What moved it: the client's placements and walk events just before (and at) the anomaly.
                for et, what in client_events:
                    if t - 1200 <= et <= t + 150:
                        lines.append(f"            {et - t:+5d} ms  {tagged(what)}")
        lines += vehicle_lines({"server": EVENTS.get((ENDPOINTS["server"], oid), []), "client": client_events}, marked)
        if a.events:
            for side in ("server", "client"):
                for et, what in EVENTS.get((ENDPOINTS[side], oid), []):
                    lines.append(f"   {side} event {time.strftime('%H:%M:%S', time.gmtime(epoch_ms(et) / 1000))}.{epoch_ms(et) % 1000:03d}  {tagged(what)}")
        results[name] = marked
        if not sev and not cev:
            lines.append("   no events on either side")
    total_only = sum(1 for r in results.values() for e in r if e[3])
    lines.append(f"{total_only} client-only event(s)")
    if repair_restarts:
        lines.append(f"{repair_restarts} client-only restart(s) after a repair (RepairOwnMachines)")
    # Every event on both games in time order (placements with who made them, walks, animation states): the whole
    # story around an anomaly, not just the second before it.
    timeline = sorted((et, side, label.get(oid, oid), what) for oid in ids for side in ("server", "client")
                      for et, what in EVENTS.get((ENDPOINTS[side], oid), []))
    (out / "events.txt").write_text("\n".join(
        f"{time.strftime('%H:%M:%S', time.gmtime(epoch_ms(et) / 1000))}.{epoch_ms(et) % 1000:03d}  {side:<6} {who:<9} {tagged(what)}"
        for et, side, who, what in timeline), encoding="utf-8")
    lines.append(f"all events ({len(timeline)}): {out / 'events.txt'}")
    report = "\n".join(lines)
    print(report)
    (out / "report.txt").write_text(report, encoding="utf-8")
    # The structured part scenarios.py records (records.json): history, explain and rescore read it.
    (out / "summary.json").write_text(json.dumps({
        "client_only": total_only,
        "repair_restarts": repair_restarts,
        "state_lengths": [[who, n, s, c] for who, rows in lengths.items() for n, s, c in rows],
        # each client walk's step (walk_steps): [who, Unix ms, client cm/tick, server cm/tick or null]
        "walk_steps": [[who, epoch_ms(t), round(c, 2), None if v is None else round(v, 2)]
                       for who, rows in steps.items() for t, c, _, v, _ in rows],
        # each client walk's rest (walk_rests): [who, Unix ms, metres from the server's rest, metres moved after]
        "walk_rests": [[who, epoch_ms(t), round(off, 2), round(drift, 2)] for who, rows in rests.items() for t, off, drift, _ in rows],
    }), encoding="utf-8")
    speed_plot(out / "speed.png", sims_samples, results)
    print(f"speed timeline: {out / 'speed.png'}")
    if grabber and grabber.frames and only_moments:
        frame_sheet(out / "frames.png", grabber.frames, only_moments)
        print(f"frames around client-only events: {out / 'frames.png'}")
    return 0 if total_only == 0 else 4


if __name__ == "__main__":
    sys.exit(main())
