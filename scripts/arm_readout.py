"""Per-arm read-outs for an arms run's hooks: each game's mean sim tick rate over the arm, its GC heap lever counters'
change over the arm, each client's stream silences in the arm's run window, and gc_watch's zeroed-chunk values for the
arm (written for the GC heap lever's A/B, 2026-10-22, internal documentation).

    python scripts/arm_readout.py before {arm} {repeat} --clients 2            # scenarios.py arms --before
    python scripts/arm_readout.py after {arm} {repeat} --clients 2 [--chunks]  # arms --after

before reads each game's DIAG_STATE tick= and the numbers of its SACS_RECORDER status gcHeap field, and keeps them,
with the arm, the repeat and the time, in the run dir's arm-readout.json. after reads them again and prints NAME=N
values, which the arms row keeps as hook.NAME:

    serverTps=29.71 client1Tps=29.80 client2Tps=29.77         # sim ticks per second from the before hook to now
    serverForced=3 serverForcedMs=290 serverForced50=2 ...    # gcHeap's since-load counters: their change over the arm
    serverForcedPerMin=0.21 serverHeapMB=355 ...              #   forced per minute; heapMB, commitMB, peakCommitMB now
    client1Gaps=2 client1GapS=5.4 client1LongestS=3.1         # stream silences of 1.5 s or more in the run window
    client2Gaps=0 client2GapS=0.0 client2LongestS=0.9         #   (stream_gaps.py over client.log, client2.log)

The arms status readings cover the server and the first client only; these values cover every game, client 2 too. A
tick rate or a counter's change is -1 when the before reading is missing or for another arm or repeat, or the value
went back (a restarted game), and a game that does not answer reads -1 too; a level not read is -1. The counters need
SimLink 14 with SIMLINK_GCFORCED=1 (forced*) in the game's environment. The run window is SIMSONLINE_ARM_START and
SIMSONLINE_ARM_END (epoch seconds), which arms gives its --after hook; with no window the silences are left out.
--chunks also runs `gc_watch.py chunks both --arm` and passes its lines through (it needs the `chunks server --follow`
and `chunks client --follow` readers running for surveys inside the window). The tick rate and the counters span the
arm's before hook to its after hook, a little wider than the run window, so they include the scenarios' setup; game
speed changes inside a scenario change the tick rate too.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import stream_gaps  # noqa: E402

ROOT = Path(__file__).resolve().parents[1]
RUN = Path(os.environ.get("SIMSONLINE_RUN_DIR") or ROOT / ".omo" / "run")  # as stackctl's RUN_DIR
STATE = "arm-readout.json"
ENDPOINTS = {"server": "127.0.0.1:34029", "client1": "127.0.0.1:34030", "client2": "127.0.0.1:34031"}
LOGS = {"client1": "client.log", "client2": "client2.log"}
GC_COUNTERS = ("forced", "forcedMs", "forced50", "forced100", "gcNo")  # since load: after prints the arm's change
GC_LEVELS = ("heapMB", "commitMB", "peakCommitMB")  # after prints the reading
GC_FIELD = re.compile(r"gcHeap=([A-Za-z0-9_.,=-]*)")


def games(clients: int) -> dict[str, str]:
    return {k: v for k, v in ENDPOINTS.items() if k == "server" or int(k[-1]) <= clients}


def gc_heap(report: str) -> dict[str, float]:
    """The numbers in a SACS_RECORDER status report's gcHeap field (SacsGcHeap.Describe); {} when it has none."""
    m = GC_FIELD.search(report)
    out: dict[str, float] = {}
    for part in (m[1].split(",")[1:] if m else []):
        key, _, value = part.partition("=")
        try:
            number = float(value)
        except ValueError:
            continue
        out[key] = int(number) if number.is_integer() else number
    return out


def read_games(endpoints: dict[str, str]) -> dict[str, dict]:
    """Each game's DIAG_STATE tick= and gcHeap numbers; tick -1 and no numbers for a game that does not answer."""
    import pump_ms
    import bridgectl

    out = {}
    for name, endpoint in endpoints.items():
        tick, gc, sock = -1, {}, None
        try:
            sock = bridgectl.connect(endpoint, 20)
            tick = pump_ms.tick(sock)
            gc = gc_heap(str(bridgectl.roundtrip(sock, "SACS_RECORDER status")))
        except (Exception, SystemExit):  # noqa: BLE001 - bridgectl exits on a timeout; a dead game reads -1, the hook goes on
            pass
        finally:
            if sock is not None:
                sock.close()
        out[name] = {"tick": tick, "gc": gc}
    return out


def same_arm(before: dict, arm: str, repeat: str) -> bool:
    return bool(before) and before.get("arm") == arm and str(before.get("repeat")) == str(repeat)


def rates(before: dict, after_ticks: dict[str, int], now: float, arm: str, repeat: str) -> dict[str, float]:
    """Ticks per second per game since the before reading; -1 when it is missing, for another arm or repeat, or a
    game's tick was not read (or went back: a restarted game)."""
    out = {}
    same = same_arm(before, arm, repeat)
    elapsed = now - float(before.get("time", now)) if same else 0.0
    for name, tick in after_ticks.items():
        start = before.get("ticks", {}).get(name, -1) if same else -1
        out[name] = round((tick - start) / elapsed, 2) if elapsed > 0 and start >= 0 and tick >= start else -1.0
    return out


def gc_changes(before: dict, after_gc: dict[str, dict], now: float, arm: str, repeat: str) -> dict[str, dict]:
    """Per game: each since-load gcHeap counter's change since the before reading (-1 when that reading is missing,
    for another arm or repeat, or the counter went back), forcedPerMin over the same span, and the levels now (-1
    when not read)."""
    same = same_arm(before, arm, repeat)
    minutes = (now - float(before.get("time", now))) / 60.0 if same else 0.0
    out = {}
    for name, gc in after_gc.items():
        start = before.get("gc", {}).get(name, {}) if same else {}
        row: dict[str, float] = {}
        for key in GC_COUNTERS:
            a, b = start.get(key), gc.get(key)
            row[key] = b - a if a is not None and b is not None and b >= a >= 0 else -1
        row["forcedPerMin"] = round(row["forced"] / minutes, 3) if minutes > 0 and row["forced"] >= 0 else -1
        for key in GC_LEVELS:
            row[key] = gc.get(key, -1)
        out[name] = row
    return out


def gap_values(lines: list, start: datetime, end: datetime) -> tuple[int, float, float]:
    """The silences of stream_gaps.MIN_GAP or more overlapping start..end: their count, their total seconds, and the
    longest silence overlapping the window (any length)."""
    found = stream_gaps.window_gaps(lines, start, end)
    return len(found), round(sum(g.seconds for g in found), 1), round(stream_gaps.longest(lines, start, end), 1)


def utc(epoch: float) -> datetime:
    return datetime.fromtimestamp(epoch, tz=timezone.utc).replace(tzinfo=None)  # the logs' times are naive UTC


def cmd_before(arm: str, repeat: str, clients: int) -> int:
    read = read_games(games(clients))
    ticks = {name: g["tick"] for name, g in read.items()}
    gc = {name: g["gc"] for name, g in read.items()}
    RUN.mkdir(parents=True, exist_ok=True)
    (RUN / STATE).write_text(json.dumps({"arm": arm, "repeat": repeat, "time": time.time(), "ticks": ticks, "gc": gc}),
                             encoding="utf-8")
    print("arm-readout before " + " ".join(f"{name}Tick={tick}" for name, tick in ticks.items()))
    return 0


def cmd_after(arm: str, repeat: str, clients: int, chunks: bool) -> int:
    endpoints = games(clients)
    read = read_games(endpoints)
    now = time.time()
    try:
        before = json.loads((RUN / STATE).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        before = {}
    ticks = {name: g["tick"] for name, g in read.items()}
    print(" ".join(f"{name}Tps={rate}" for name, rate in rates(before, ticks, now, arm, repeat).items()))
    for name, row in gc_changes(before, {n: g["gc"] for n, g in read.items()}, now, arm, repeat).items():
        print(" ".join(f"{name}{key[0].upper()}{key[1:]}={value}" for key, value in row.items()))
    start, end = os.environ.get("SIMSONLINE_ARM_START"), os.environ.get("SIMSONLINE_ARM_END")
    if start and end:
        for name, log in LOGS.items():
            if name not in endpoints:
                continue
            path = RUN / log
            lines = stream_gaps.parse(path.read_text(encoding="utf-8", errors="replace")) if path.exists() else []
            count, total, worst = gap_values(lines, utc(float(start)), utc(float(end)))
            print(f"{name}Gaps={count} {name}GapS={total} {name}LongestS={worst}")
    else:
        print("arm-readout: no run window (SIMSONLINE_ARM_START/END): silences left out")
    if chunks:
        done = subprocess.run([sys.executable, str(ROOT / "scripts" / "gc_watch.py"), "chunks", "both", "--arm"],
                              capture_output=True, text=True, timeout=300)
        sys.stdout.write(done.stdout)
        if done.returncode != 0:
            print(f"arm-readout: gc_watch chunks exit {done.returncode}: {done.stderr.strip()[-300:]}")
    return 0


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("verb", choices=("before", "after"))
    p.add_argument("arm")
    p.add_argument("repeat")
    p.add_argument("--clients", type=int, choices=(1, 2), default=1)
    p.add_argument("--chunks", action="store_true", help="after: also gc_watch.py chunks both --arm")
    a = p.parse_args(argv)
    if a.verb == "before":
        return cmd_before(a.arm, a.repeat, a.clients)
    return cmd_after(a.arm, a.repeat, a.clients, a.chunks)


if __name__ == "__main__":
    raise SystemExit(main())
