"""Silences in a client's presentation stream, each with what came before it.

    python scripts/stream_gaps.py .omo/run/client2.log                       # every gap of 1.5 s or more
    python scripts/stream_gaps.py .omo/run/client2.log --min 2 --from 19:41:00 --to 19:45:00
    python scripts/stream_gaps.py --motion .omo/run/motion/064112-c2         # each client walk in a watch: its gaps

The controller logs each message it receives with a UTC time (`[2026-10-20 19:41:18.063Z] received: {"$type":
"sim_animation", ...}`). The stream is the `sim_animation` batches; a gap is the time from one batch to the next. A
batch comes every 0.1-0.7 s while anything on the lot moves (the server's idle poses alone send one a second), so a
gap of 1.5 s or more is the stream stalling, not a quiet lot. Each gap names the last notable line before it (a Sim
entering scope, a join, a full snapshot, a forwarded pick: `sim_spawned`, `session_joined`, `full_snapshot`,
`user_intent_forwarded`, ...; the routine acks, deltas and clock lines are left out) and what arrived inside it.

--motion takes a motion watch folder (`motion_watch.py`; a `-c2` folder is client 2's) and, for each walk its client
side noted (`walk start` to `walk end`/`walk stop`), prints the longest silence inside the walk and the nearest gap
of --min or more. The log is the one of that checkout (`.omo/run/client.log` or `client2.log`, or a copy under
`.omo/run/archive/*/`) whose times cover the walk. events.txt times are UTC like the log's; the folder name is local
time (064112 is 19:41:12Z on a +11 h host), so match on the events, never the name.

scenarios.py notes each scenario's silences on its record (report only, no mod call): window_gaps over the watch's
wall-clock window, from the watched client's log in whichever checkout booted the games (covering_log), written as
"stream silent N s at HH:MM:SS (after X)" (silent_text).

Why: the presentation publish stalls the whole stream while it gathers history over the bridge (main, 2026-10-21:
2.8-3.2 s on every sim_spawned, about 6 s on the established client at each late join), and a walk or clip that
spans a stall is cut. A slow walk with no gap is not the stall: two-hud on client 2 (arms 20261021-063735) walked
half the server's speed every run with no gap over 0.7 s.
"""
from __future__ import annotations

import argparse
import re
import sys
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta
from pathlib import Path

LINE = re.compile(r"\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d(?:\.\d+)?)Z\] ([A-Za-z0-9_]+): ?(.*)")
TYPE = re.compile(r'"\$type":"([^"]+)"')
SNAPSHOT = re.compile(r'"type":"([^"]+)"')
STREAM = "sim_animation"
# lines that come all the time and say nothing about a stall
ROUTINE = {"command_accepted", "snapshot_ack_sent", "snapshot_delta", "world_time_changed", "sim_details_changed",
           "sim_motive_changed"}
MIN_GAP = 1.5
LOOKBACK = 10.0  # seconds before a gap in which its preceding line is looked for
WALK = re.compile(r"(\d\d:\d\d:\d\d\.\d+)\s+client (\S+)\s+walk (start|end|stop)\b")


@dataclass
class Line:
    at: datetime
    kind: str  # received:<type> is <type>; received_snapshot:<type> is <type>; anything else its event name


@dataclass
class Gap:
    start: datetime
    end: datetime
    before: Line | None = None
    inside: list[str] = field(default_factory=list)

    @property
    def seconds(self) -> float:
        return (self.end - self.start).total_seconds()


def parse(text: str) -> list[Line]:
    out = []
    for raw in text.splitlines():
        m = LINE.match(raw)
        if not m:
            continue  # the build stamp, shadow-mode lines: no time
        kind = m.group(2)
        if kind == "received":
            t = TYPE.match(m.group(3)) or TYPE.search(m.group(3)[:80])
            kind = t.group(1) if t else "received"
        elif kind == "received_snapshot":
            t = SNAPSHOT.search(m.group(3))
            kind = t.group(1) if t else kind
        out.append(Line(datetime.strptime(m.group(1), "%Y-%m-%d %H:%M:%S.%f" if "." in m.group(1)
                                          else "%Y-%m-%d %H:%M:%S"), kind))
    return out


def gaps(lines: list[Line], minimum: float = MIN_GAP, lookback: float = LOOKBACK) -> list[Gap]:
    """Every stretch of `minimum` seconds or more between two stream batches, with the last notable line within
    `lookback` seconds before it (or at its start) and the notable kinds that arrived inside it."""
    out: list[Gap] = []
    last: Line | None = None
    notable: list[Line] = []
    inside: list[str] = []
    for line in lines:
        if line.kind == STREAM:
            if last is not None and (line.at - last.at).total_seconds() >= minimum:
                gap = Gap(last.at, line.at, inside=inside)
                window = last.at - timedelta(seconds=lookback)
                gap.before = next((n for n in reversed(notable) if window <= n.at <= last.at), None)
                out.append(gap)
            last, inside = line, []
        elif line.kind not in ROUTINE:
            if last is not None and line.at > last.at:
                inside.append(line.kind)
            notable.append(line)
    return out


def longest(lines: list[Line], start: datetime, end: datetime) -> float:
    """The longest silence between stream batches that overlaps start..end (the batch before start counts)."""
    times = [ln.at for ln in lines if ln.kind == STREAM]
    best = 0.0
    for a, b in zip(times, times[1:]):
        if b >= start and a <= end:
            best = max(best, (b - a).total_seconds())
    return best


def window_gaps(lines: list[Line], start: datetime, end: datetime, minimum: float = MIN_GAP) -> list[Gap]:
    """The gaps of `minimum` seconds or more that overlap start..end (UTC)."""
    return [g for g in gaps(lines, minimum) if g.start < end and g.end > start]


SPAN_HEAD = 65536  # bytes read from each end of a log for its first and last times
SPAN_SLACK = 10.0  # seconds: a live log's last line can come a little before the window's end


def span(path: Path) -> tuple[datetime, datetime] | None:
    """A log's first and last times, from its first and last SPAN_HEAD bytes (logs grow to MBs; archives are copies)."""
    try:
        with path.open("rb") as f:
            head = f.read(SPAN_HEAD).decode("utf-8", errors="replace")
            f.seek(0, 2)
            size = f.tell()
            f.seek(max(0, size - SPAN_HEAD))
            tail = f.read().decode("utf-8", errors="replace")
    except OSError:
        return None
    first, last = parse(head), parse(tail)
    return (first[0].at, last[-1].at) if first and last else None


def covering_log(name: str, start: datetime, end: datetime, runs: list[Path]) -> Path | None:
    """The `name` log (client.log, client2.log) whose times cover start..end (UTC): the live logs of the given
    .omo/run folders first (the checkout that booted the games writes them), then their archive copies, newest first."""
    live = [run / name for run in runs]
    archived = [path for run in runs for path in sorted(run.glob(f"archive/*/{name}"), reverse=True)]
    for path in live + archived:
        times = span(path) if path.exists() else None
        if times and times[0] <= start and end <= times[1] + timedelta(seconds=SPAN_SLACK):
            return path
    return None


def silent_text(gap: Gap) -> str:
    """A scenario record's line for one gap: stream silent 6.2 s at 20:09:01.2 (after sim_state_changed; inside:
    session_joined)."""
    after = gap.before.kind if gap.before is not None else "nothing notable"
    inside = f"; inside: {', '.join(dict.fromkeys(gap.inside))}" if gap.inside else ""
    return f"stream silent {gap.seconds:.1f} s at {clock(gap.start)[:10]} (after {after}{inside})"


def clock(at: datetime) -> str:
    return at.strftime("%H:%M:%S.%f")[:12]


def gap_text(gap: Gap) -> str:
    before = "nothing notable before it"
    if gap.before is not None:
        lead = (gap.start - gap.before.at).total_seconds()
        before = f"after {gap.before.kind} {lead:.1f} s before"
    inside = f"; inside: {', '.join(dict.fromkeys(gap.inside))}" if gap.inside else ""
    return f"{clock(gap.start)} silent {gap.seconds:.1f} s, {before}{inside}"


def walks(events: str) -> list[tuple[str, str, str]]:
    """(sim, start, end) UTC time-of-day strings of each client walk in a motion watch's events.txt. A walk ends at its
    `walk end` line: a `walk stop` (the runner's overrun decision) can come 0.7 s before the copy stops moving
    (two-hud, motion 064112-c2), so it ends a walk only when no end follows before the next start. A walk with
    neither runs to the next start, or stays open (end "")."""
    open_walks: dict[str, str] = {}
    stopped: dict[str, str] = {}
    out = []
    for m in WALK.finditer(events):
        at, sim, what = m.groups()
        if what == "start":
            if sim in open_walks:
                out.append((sim, open_walks[sim], stopped.get(sim, at)))
            open_walks[sim] = at
            stopped.pop(sim, None)
        elif sim in open_walks and what == "stop":
            stopped[sim] = at
        elif sim in open_walks:
            out.append((sim, open_walks.pop(sim), at))
            stopped.pop(sim, None)
    out.extend((sim, start, stopped.get(sim, "")) for sim, start in open_walks.items())
    return sorted(out, key=lambda w: w[1])


def log_for(folder: Path, first: str, last: str) -> tuple[Path, list[Line], date] | None:
    """The client log of the folder's checkout whose times cover first..last (UTC time of day), parsed, with the date
    the times fall on."""
    run = folder.resolve().parent.parent  # <checkout>/.omo/run/motion/<folder>
    name = "client2.log" if folder.name.endswith("-c2") else "client.log"
    candidates = [run / name] + sorted(run.glob(f"archive/*/{name}"), reverse=True)
    for path in candidates:
        if not path.exists():
            continue
        lines = parse(path.read_text(encoding="utf-8", errors="replace"))
        if not lines:
            continue
        for day in {lines[0].at.date(), lines[-1].at.date()}:
            a = datetime.combine(day, datetime.strptime(first, "%H:%M:%S.%f").time())
            b = datetime.combine(day, datetime.strptime(last, "%H:%M:%S.%f").time())
            if lines[0].at <= a and b <= lines[-1].at:
                return path, lines, day
    return None


def motion_report(folder: Path, minimum: float) -> list[str]:
    events = folder / "events.txt"
    if not events.exists():
        return [f"{folder}: no events.txt"]
    found = walks(events.read_text(encoding="utf-8", errors="replace"))
    if not found:
        return [f"{folder.name}: no client walk"]
    ends = [w[2] or w[1] for w in found]
    picked = log_for(folder, found[0][1], max(ends))
    if picked is None:
        return [f"{folder.name}: no client log of this checkout covers {found[0][1]}..{max(ends)} (UTC)"]
    path, lines, day = picked
    all_gaps = gaps(lines, minimum)
    out = [f"{folder.name}: log {path}"]
    for sim, first, last in found:
        start = datetime.combine(day, datetime.strptime(first, "%H:%M:%S.%f").time())
        end = datetime.combine(day, datetime.strptime(last or first, "%H:%M:%S.%f").time())
        near = min(all_gaps, key=lambda g: min(abs((g.start - start).total_seconds()),
                                               abs((g.end - start).total_seconds())), default=None)
        where = "none in the log"
        if near is not None:
            offset = (near.start - start).total_seconds()
            overlap = near.start <= end and near.end >= start
            where = ("overlaps the walk: " if overlap else f"{offset:+.0f} s from its start: ") + gap_text(near)
        out.append(f"  {sim} walk {first}-{last or '?'}: longest silence inside {longest(lines, start, end):.1f} s; "
                   f"nearest gap of {minimum:g} s or more {where}")
    return out


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=(__doc__ or "").strip().splitlines()[0])
    ap.add_argument("log", nargs="?", type=Path, help="a controller log (client.log, client2.log)")
    ap.add_argument("--motion", type=Path, help="a motion watch folder: each client walk and its nearest gap")
    ap.add_argument("--min", type=float, default=MIN_GAP, help=f"shortest gap listed, seconds (default {MIN_GAP:g})")
    ap.add_argument("--from", dest="since", help="UTC time of day HH:MM:SS: gaps from here")
    ap.add_argument("--to", dest="until", help="UTC time of day HH:MM:SS: gaps up to here")
    a = ap.parse_args(argv)
    if a.motion:
        print("\n".join(motion_report(a.motion, a.min)))
        return 0
    if a.log is None:
        ap.error("give a log or --motion")
    lines = parse(a.log.read_text(encoding="utf-8", errors="replace"))
    found = gaps(lines, a.min)
    if a.since:
        found = [g for g in found if clock(g.end) >= a.since]
    if a.until:
        found = [g for g in found if clock(g.start) <= a.until]
    batches = sum(1 for ln in lines if ln.kind == STREAM)
    for gap in found:
        print(gap_text(gap))
    print(f"{len(found)} gap(s) of {a.min:g} s or more in {batches} stream batch(es)"
          + (f", {clock(lines[0].at)}-{clock(lines[-1].at)} UTC" if lines else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
