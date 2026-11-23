"""The server's Sim and its copy side by side, from a saved motion watch: where each was, whether it walked, its state.

    python scripts/motion_track.py 083220 Pablo                          # the whole watch, a row every 250 ms
    python scripts/motion_track.py 083220-c2 Pablo --from 21:32:25.5 --to 21:32:29 --step 100
    python scripts/motion_track.py .omo/run/motion/091022 Pablo --events  # with the Sim's walk notes in between
    python scripts/motion_track.py 145545 Jennifer --pace                # each side's walks: how long, how far, how fast

Rows are the server's samples (UTC); the copy's is the newest client sample at or before each one, with no lag shift,
so a copy behind its Sim reads as a distance. This is what showed that the server's walk end line came at a Stop call
while its Pablo walked on 0.53 m (two-hud 2026-10-21, motion 083220): the end line's place was not where he rested.
motion_watch's rest line says the same per walk; this shows the whole path around it.

--pace lists each side's walks instead: duration (ms, and simulator ticks when the mod recorded them), distance and
speed, then how far the copy ended from the server's Sim. Duration is the measure to compare. The number of samples
on which the Sim moved varies with frame cadence: one 1.44 m walk moved on 34, 45, 47 and 51 samples in four
identical runs. This showed the server walking at the in-view pace with its camera away once SimLink's on-screen walk
was in (two-hud-server-away 2026-10-21, motions 145452 and 145545: 1.44 m in about 3.2 s, as in view).
"""

from __future__ import annotations

import argparse
import bisect
import json
import math
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import motion_watch as mw  # noqa: E402

ROOT = Path(__file__).resolve().parents[1]
NOTE_WORDS = ("walk ", "server walk", "SetPosition", "mirror")


def folder(name: str) -> Path:
    path = Path(name)
    return path if path.is_dir() else ROOT / ".omo" / "run" / "motion" / name


def sim_id(saved: dict, sim: str) -> str:
    """A Sim name (motion_watch.SIMS), an id, or an id's last digits, among the watch's ids."""
    ids = saved.get("ids", [])
    wanted = mw.SIMS.get(sim.lower(), sim)
    found = [i for i in ids if i == wanted or i.endswith(wanted)]
    if len(found) != 1:
        raise SystemExit(f"{sim}: {'not among' if not found else 'more than one of'} the watch's ids {ids}")
    return found[0]


def clock(sample_ms: int) -> str:
    unix = mw.epoch_ms(sample_ms)
    return time.strftime("%H:%M:%S", time.gmtime(unix / 1000)) + f".{unix % 1000:03d}"


def within(at: str, start: str | None, end: str | None) -> bool:
    """A bound as precise as it is written: --to 21:32:28 keeps 21:32:28.500, --from 21:32:26.5 drops 21:32:26.400."""
    return not (start and at[:len(start)] < start) and not (end and at[:len(end)] > end)


def rows(saved: dict, oid: str, start: str | None, end: str | None, step_ms: int) -> list[str]:
    server = [tuple(s) for s in saved["data"]["server"].get(oid, [])]
    client = [tuple(s) for s in saved["data"]["client"].get(oid, [])]
    client_times = [s[0] for s in client]
    out, last = [], None
    for s in server:
        at = clock(s[0])
        if not within(at, start, end):
            continue
        if last is not None and s[0] - last < step_ms:
            continue
        last = s[0]
        i = bisect.bisect_right(client_times, s[0]) - 1
        if i < 0:
            continue
        c = client[i]
        apart = math.hypot(s[1] - c[1], s[3] - c[3])
        out.append(f"{at}  server {s[1]:.2f},{s[3]:.2f} w={int(bool(s[6]))} {mw.state_name(s[7]) if len(s) > 7 else '-':<28}"
                   f" | client {c[1]:.2f},{c[3]:.2f} w={int(bool(c[6]))} {mw.state_name(c[7]) if len(c) > 7 else '-':<28}"
                   f" | {apart:.2f} m")
    return out


def walks(samples: list[tuple]) -> list[dict]:
    """Each run of walking samples: when it began, how long it lasted (ms; simulator ticks when the samples carry
    them), how far it went, and on how many samples the Sim moved."""
    out, run = [], []
    for s in samples + [None]:
        if s is not None and s[6]:
            run.append(s)
            continue
        if len(run) > 1:
            steps = [math.hypot(b[1] - a[1], b[3] - a[3]) for a, b in zip(run, run[1:])]
            ticks = run[-1][9] - run[0][9] if len(run[0]) > 9 and len(run[-1]) > 9 else None
            out.append({"start": run[0][0], "ms": run[-1][0] - run[0][0], "ticks": ticks, "m": sum(steps),
                        "moves": sum(1 for d in steps if d > 0.001)})
        run = []
    return out


def pace(saved: dict, oid: str, start: str | None, end: str | None) -> list[str]:
    """Each side's walks of at least 2 cm, then the copy's final distance from the server's Sim."""
    out = []
    for side in ("server", "client"):
        for w in walks([tuple(s) for s in saved["data"][side].get(oid, [])]):
            if w["m"] < 0.02 or not within(clock(w["start"]), start, end):
                continue
            ticks = "-" if w["ticks"] is None else str(w["ticks"])
            speed = w["m"] / (w["ms"] / 1000) if w["ms"] else 0.0
            out.append(f"{clock(w['start'])}  {side:<6} walk {w['m']:.2f} m in {w['ms']} ms ({ticks} ticks), "
                       f"{speed:.2f} m/s; moved on {w['moves']} samples")
    server, client = saved["data"]["server"].get(oid), saved["data"]["client"].get(oid)
    if server and client:
        s, c = server[-1], client[-1]
        out.append(f"rest: the copy ended {math.hypot(s[1] - c[1], s[3] - c[3]):.2f} m from the server's Sim")
    return out


def notes(path: Path, sim: str, start: str | None, end: str | None) -> list[str]:
    """The Sim's walk and placement lines from events.txt, both games."""
    events = path / "events.txt"
    if not events.exists():
        return []
    out = []
    for line in events.read_text(encoding="utf-8", errors="replace").splitlines():
        at = line[:12]
        if not at[:2].isdigit() or not within(at, start, end):
            continue
        if f" {sim} " in line and any(w in line for w in NOTE_WORDS):
            out.append(line.rstrip())
    return out


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("watch", help="a motion folder (its name under .omo/run/motion, or a path)")
    p.add_argument("sim", help="Jennifer, Pablo, or a Sim id (or its last digits)")
    p.add_argument("--from", dest="start", help="HH:MM:SS[.mmm] UTC, as events.txt")
    p.add_argument("--to", dest="end")
    p.add_argument("--step", type=int, default=250, help="ms between rows (default 250)")
    p.add_argument("--events", action="store_true", help="the Sim's walk and placement notes between the rows")
    p.add_argument("--pace", action="store_true", help="each side's walks (duration, ticks, distance, speed) instead")
    a = p.parse_args(argv)
    path = folder(a.watch)
    saved = json.loads((path / "samples.json").read_text(encoding="utf-8"))
    oid = sim_id(saved, a.sim)
    if a.pace:
        lines = pace(saved, oid, a.start, a.end)
        print("\n".join(lines) if lines else "no walks in that span")
        return 0
    table = rows(saved, oid, a.start, a.end, a.step)
    if a.events:
        name = next((n.capitalize() for n, i in mw.SIMS.items() if i == oid), a.sim)
        table = sorted(table + ["             " + n for n in notes(path, name, a.start, a.end)],
                       key=lambda r: r.strip()[:12])
    print("\n".join(table) if table else "no samples in that span")
    return 0


if __name__ == "__main__":
    sys.exit(main())
