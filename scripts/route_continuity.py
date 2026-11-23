"""Card 55 section D continuity counters and section C sampling rules, offline.

    python scripts/route_continuity.py .omo/run/motion/<run> --sim <object id> [--client client] [--json]

Pure functions over per-update samples of one walk (server and each client window), plus a loader for the motion
watch's run folder (samples.json). Counters (card 25 section C, card 55 section D):
  startup_stutter_events      first second of a walk: the copy's per-tick movement flips between 0 cm and non-zero
  pace_mismatch_events        at each leg boundary: the copy's per-tick delta against the server's, over 20 cm apart
  client_only_motion_events   a client placement event with no server counterpart (per window; equal on both windows)
  door_leg_leg_style_mismatches  a leg through a door whose copy style is not Walk
A counter whose input the samples do not carry (a run saved before the motion watch wrote `fields`) is
None, never 0: the verdict is BLOCKED, so a run cannot pass by omission.
Section C: settle_gate() scores the end-of-walk position only once every window is halted or seated; classify_midwalk()
labels a mid-walk difference `detector-artifact` unless the server's value is the same in two reads bracketing the
client's.
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from dataclasses import dataclass
from pathlib import Path

STARTUP_STUTTER_WINDOW_TICKS = 30  # card 25 section B
PACE_MISMATCH_TOLERANCE_CM = 20
END_OF_WALK_SNAP_TOLERANCE_M = 0.10
TICK_MS = 1000.0 / 30  # the simulator's tick, as scripts/motion_watch.py has it
MOVING_CM = 0.5  # a per-tick movement under this is standing still
SERVER_MATCH_MS = 1500  # a client event is matched to a server event this close (motion_watch's own window)
SETTLED = ("halted", "seated")
WALK_STYLES = ("Walk", "Normal")  # card 55 says Walk; the Rust WalkStyle enum calls it Normal
PLACEMENT_KINDS = ("teleport", "snap", "slide", "back to")


@dataclass
class Sample:
    t_ms: int
    x: float
    z: float
    ticks: int | None = None  # simulator tick count when recorded
    walking: bool = False
    style: str | None = None  # walk style: not recorded by the motion watch
    leg: int | None = None  # leg index: not recorded by the motion watch
    door: bool | None = None  # the leg goes through a door: not recorded by the motion watch
    phase: str | None = None  # Halted / SeatingSettled as "halted" / "seated": not recorded by the motion watch
    event: str | None = None  # a placement event: "teleport ...", "snap ...", "back to ..."


def _cm_per_tick(a: Sample, b: Sample) -> float:
    d = math.hypot(b.x - a.x, b.z - a.z) * 100.0
    if a.ticks is not None and b.ticks is not None and b.ticks > a.ticks:
        return d / (b.ticks - a.ticks)
    return d / max(1.0, (b.t_ms - a.t_ms) / TICK_MS)


def startup_stutter_events(samples: list[Sample]) -> int:
    """Card 25 C.1: after the copy's first movement, each flip between 0 cm and non-zero per tick inside the first
    STARTUP_STUTTER_WINDOW_TICKS ticks of the walk. (The walk's own first step from standing is not a flip.)"""
    # A frame holds 0, 1 or 2 ticks, so a pair of frames with the same tick count says nothing about movement and
    # is skipped (motion_watch.tick_steps); the window opens at the first pair that moves, not at the walking flag.
    moving = None
    start = None
    flips = 0
    for a, b in zip(samples, samples[1:]):
        if a.ticks is not None and b.ticks is not None and b.ticks <= a.ticks:
            continue
        if (a.ticks is None or b.ticks is None) and b.t_ms - a.t_ms < TICK_MS / 2:
            continue  # no tick counts and under half a tick apart: no tick passed, so no movement can be read
        now = _cm_per_tick(a, b) > MOVING_CM
        if moving is None:
            if not now:
                continue
            start = a
        else:
            span = (b.ticks - start.ticks) if b.ticks is not None and start.ticks is not None \
                else (b.t_ms - start.t_ms) / TICK_MS
            if span > STARTUP_STUTTER_WINDOW_TICKS:
                break
            if now != moving:
                flips += 1
        moving = now
    return flips


def _pos_at(samples: list[Sample], t: int) -> tuple[float, float]:
    prev = samples[0]
    for s in samples:
        if s.t_ms >= t:
            if s.t_ms == prev.t_ms:
                return s.x, s.z
            f = (t - prev.t_ms) / (s.t_ms - prev.t_ms)
            return prev.x + (s.x - prev.x) * f, prev.z + (s.z - prev.z) * f
        prev = s
    return prev.x, prev.z


def _walk_family(style: str) -> str:
    return "Walk" if style in WALK_STYLES else style


def pace_mismatch_events(server: list[Sample], client: list[Sample]) -> int | None:
    """Card 25 C.2: at each client leg boundary, the client's per-tick delta over the step that spans it against the
    server's over the same time span; differing by more than PACE_MISMATCH_TOLERANCE_CM is one event. Card 25 C.2.1
    adds the style: the copy's style on the incoming leg must be the server's for that leg, else one more event (as
    route_presentation.rs on_leg_boundary counts it; a stale FastWalk through a door is only ~3 cm/tick off the
    server's Walk, inside the tolerance). None when either side carries no leg index or no style, or no boundary was
    scorable (a boundary whose leg the server has no style for is not scored). The server must cover the span (else that boundary is skipped)."""
    if not any(s.leg is not None for s in client) or not server or not any(s.leg is not None for s in server) \
            or not any(s.style is not None for s in client) or not any(s.style is not None for s in server):
        return None
    count = scored = 0
    for a, b in zip(client, client[1:]):
        if a.leg is None or b.leg is None or a.leg == b.leg:
            continue
        if a.t_ms < server[0].t_ms or b.t_ms > server[-1].t_ms:
            continue
        server_style = next((s.style for s in server if s.leg == b.leg and s.style is not None), None)
        if server_style is None:
            continue  # the server has no style for this leg: nothing to hold the copy's pace against
        scored += 1
        if b.style is not None and _walk_family(b.style) != _walk_family(server_style):
            count += 1
        ticks = max(1.0, (b.t_ms - a.t_ms) / TICK_MS) if a.ticks is None or b.ticks is None else max(1, b.ticks - a.ticks)
        sa, sb = _pos_at(server, a.t_ms), _pos_at(server, b.t_ms)
        server_cm = math.hypot(sb[0] - sa[0], sb[1] - sa[1]) * 100.0 / ticks
        if abs(_cm_per_tick(a, b) - server_cm) > PACE_MISMATCH_TOLERANCE_CM:
            count += 1
    return count if scored else None


def _is_placement(event: str | None) -> bool:
    return bool(event) and any(k in event for k in PLACEMENT_KINDS)


def client_only_motion_events(server: list[Sample], clients: dict[str, list[Sample]],
                              events_loaded: bool = False) -> dict:
    """Card 55 D: per window, the count of client placement events with no server placement event within
    SERVER_MATCH_MS; `symmetric` is true when every window has the same count. `events_loaded` says the samples carry
    the placement events of both sides (`back to`, snap, teleport; load_motion_run re-derives teleports only): when
    False the counter cannot be read and is None, never 0."""
    if not events_loaded:
        return {"per_window": None, "symmetric": None}
    server_times = [s.t_ms for s in server if _is_placement(s.event)]
    per = {name: sum(1 for s in samples if _is_placement(s.event)
                     and not any(abs(s.t_ms - t) <= SERVER_MATCH_MS for t in server_times))
           for name, samples in clients.items()}
    # symmetry needs two windows to compare (card 55 D): one window cannot show it
    return {"per_window": per, "symmetric": len(set(per.values())) <= 1 if len(per) >= 2 else None}


def door_leg_leg_style_mismatches(client: list[Sample]) -> int | None:
    """Card 55 D: legs through a door (door flag) whose copy ever walked in a style other than Walk. None when the
    samples carry no door flag or style."""
    door = [s for s in client if s.door]
    if not any(s.door for s in client) or not any(s.style is not None for s in client):
        return None  # no door crossed (card 55 E needs two door portals) or no style recorded: nothing was scored
    return len({s.leg for s in door if s.style is not None and s.style not in WALK_STYLES})


def settle_gate(server_end: tuple[float, float], windows: dict[str, list[Sample]]) -> dict:
    """Section C.1: E5 is scored only once every window's last sample reports halted or seated. Before that
    `scored` is false and no distance is given (a mid-walk position is never an equality)."""
    unsettled = sorted(n for n, s in windows.items() if not s or s[-1].phase not in SETTLED)
    if unsettled:
        return {"scored": False, "unsettled": unsettled}
    off = {n: math.hypot(s[-1].x - server_end[0], s[-1].z - server_end[1]) for n, s in windows.items()}
    return {"scored": True, "off_m": off, "ok": all(v <= END_OF_WALK_SNAP_TOLERANCE_M for v in off.values())}


def classify_midwalk(server_before, client, server_after, tol: float = 0.0) -> dict:
    """Section C.2: a client/server field difference is a divergence only when the server's value is the same in
    the two reads bracketing the client's. Otherwise `detector-artifact`, both server values kept."""
    def close(a, b):
        return abs(a - b) <= tol if isinstance(a, (int, float)) and isinstance(b, (int, float)) else a == b
    out = {"server_before": server_before, "client": client, "server_after": server_after}
    if not close(server_before, server_after):
        return {**out, "label": "detector-artifact"}
    return {**out, "label": "match" if close(server_before, client) else "divergence"}


def verdict(result: dict) -> str:
    """FAIL on a non-zero counter or asymmetry (even beside a None counter), else BLOCKED when any counter is None
    (input not recorded), else PASS."""
    flat = [result["startup_stutter_events"], result["pace_mismatch_events"], result["door_leg_leg_style_mismatches"]]
    clients = result["client_only_motion_events"]
    per = clients["per_window"] or {}
    if any(v for v in flat if v is not None) or any(per.values()) or clients["symmetric"] is False:
        return "FAIL"  # a measured failure stands even when another counter's input is missing
    if any(v is None for v in flat) or clients["per_window"] is None or clients["symmetric"] is None or not per:
        return "BLOCKED"
    return "PASS"


def score(server: list[Sample], clients: dict[str, list[Sample]], events_loaded: bool = False) -> dict:
    """The four counters over one walk. startup, pace and door counters are summed over the windows (None if any
    window's is None)."""
    def total(values):
        return None if not values or any(v is None for v in values) else sum(values)
    result = {
        "startup_stutter_events": total([startup_stutter_events(c) for c in clients.values()]),
        "pace_mismatch_events": total([pace_mismatch_events(server, c) for c in clients.values()]),
        "client_only_motion_events": client_only_motion_events(server, clients, events_loaded),
        "door_leg_leg_style_mismatches": total([door_leg_leg_style_mismatches(c) for c in clients.values()]),
    }
    result["verdict"] = verdict(result)
    return result


def load_motion_run(run: Path, oid: str, event_lines: dict | None = None) -> dict[str, list[Sample]]:
    """The samples of one Sim per side from a motion watch run folder (samples.json): rows are
    (ms, x, y, z, fx, fz, walking, state, seq[, ticks]). The motion watch's own teleport events are not in
    samples.json, so placement events are re-derived the way motion_watch.events() does (import-free: teleport
    only, a jump over 6 m/s and 0.25 m) and the recorder's `back to` restarts are not available (a gap)."""
    saved = json.loads((run / "samples.json").read_text(encoding="utf-8"))
    out: dict[str, list[Sample]] = {}
    for side, sims in saved["data"].items():
        rows = sims.get(oid, [])
        samples = [Sample(r[0], r[1], r[3], r[9] if len(r) > 9 else None, bool(r[6])) for r in rows]
        for a, b in zip(samples, samples[1:]):
            dt = max(1, b.t_ms - a.t_ms) / 1000.0
            d = math.hypot(b.x - a.x, b.z - a.z)
            if d > 0.25 and d / dt > 6.0:
                b.event = f"teleport {d:.2f} m"
        apply_fields(samples, (saved.get("fields") or {}).get(side, {}).get(oid, []))
        out[side] = samples
    return out


def apply_fields(samples: list[Sample], rows: list[list]) -> None:
    """Lay the motion watch's `fields` rows ([ms, style, leg, door, phase, placement], motion_watch.route_fields) onto
    the samples: style, leg and door hold from the latest row that carries them, phase is `halted` from a halt/end row
    until the next row with a style (a new start), and a placement row marks the first sample at or after it."""
    state: dict = {"style": None, "leg": None, "door": None, "phase": None}
    pending = sorted(rows, key=lambda r: r[0])
    i = 0
    for s in samples:
        while i < len(pending) and pending[i][0] <= s.t_ms:
            _, style, leg, door, phase, placement = pending[i]
            if style is not None:
                state["style"], state["phase"] = style, None
            if leg is not None:
                state["leg"] = leg
            if door is not None:
                state["door"] = door
            if phase is not None:
                state["phase"] = {"halted": "halted"}.get(phase, phase)
            if placement:
                s.event = s.event or placement
            i += 1
        s.style, s.leg, s.door = state["style"], state["leg"], state["door"]
        s.phase = state["phase"]


def has_fields(run: Path) -> bool:
    """True when samples.json carries the motion watch's `fields` key (older runs do not)."""
    return "fields" in json.loads((run / "samples.json").read_text(encoding="utf-8"))


def first_sim_id(run: Path) -> str:
    """The first Sim a motion watch run watched (samples.json `ids`)."""
    return str(json.loads((run / "samples.json").read_text(encoding="utf-8"))["ids"][0])


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("run", help="a motion watch run folder with samples.json")
    p.add_argument("--sim", required=True, help="the Sim's object id")
    p.add_argument("--json", action="store_true")
    a = p.parse_args(argv)
    sides = load_motion_run(Path(a.run), a.sim)
    server = sides.pop("server", [])
    result = score(server, sides, has_fields(Path(a.run)))
    print(json.dumps(result, indent=1) if a.json else "\n".join(f"{k}: {v}" for k, v in result.items()))
    return {"PASS": 0, "FAIL": 3, "BLOCKED": 4}[result["verdict"]]


if __name__ == "__main__":
    sys.exit(main())
