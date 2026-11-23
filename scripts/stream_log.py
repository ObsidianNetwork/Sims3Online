"""Read the presentation stream a client received, decoded (client.log SimAnimation events).

    python scripts/stream_log.py --sim Jennifer --kind A,E,R,X --since 03:21:40 --limit 40
    python scripts/stream_log.py --machine phone --last           # the latest phone driver's whole life
    python scripts/stream_log.py --driver 538794754               # one driver, plus requests that bridge to it
    python scripts/stream_log.py --drivers --sim Pablo            # table: driver, machine, host, seq span, lines, released
    python scripts/stream_log.py --history                        # what the last late join brought (seq 0 lines)
    python scripts/stream_log.py --around 13001:15                # the 15 lines either side of seq 13001
    python scripts/stream_log.py --grep "|Vx|" --kinds            # count by kind

Lines are decoded with scripts/sacs_names.py when its table is built (machine, state, actor and parameter names,
request flags, bridges): "R 538794754 phone x->StreamVideo [Interrupt|RequestWaitOnBridge|...] bridge=-".
--sim takes Jennifer / Pablo (or any object id) and keeps every line naming that object, plus the lines of drivers it
hosts. Reads .omo/run/client.log by default (--log for another, e.g. client2.log).
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from collections import Counter, OrderedDict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import sacs_names as names  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
SIMS = {"jennifer": "18031287978496800304", "pablo": "18031287978496800272"}
EVENT = re.compile(r'^\[(?P<ts>[^\]]+)\] received: (?P<json>\{"\$type":"sim_animation".*\})\s*$')


def read(path: Path):
    """(time, seq or None, line) for every stream line, in file order."""
    for raw in path.open(encoding="utf-8", errors="replace"):
        m = EVENT.match(raw)
        if not m:
            continue
        try:
            event = json.loads(m.group("json"))
        except json.JSONDecodeError:
            continue
        ts = m.group("ts")[11:23]
        for line in event.get("lines", []):
            head = line.split("|", 1)[0]
            yield ts, (int(head) if head.isdigit() else None), line


def decode(line: str) -> str:
    f = line.split("|")
    if len(f) < 3:
        return line
    seq, kind = f[0], f[1]
    try:
        if kind == "A" and len(f) > 7:
            return f"{seq} A {f[2]} {names.name64(int(f[6]))} host={f[3]} priority={f[7]}"
        if kind in ("E", "R") and len(f) > 5:
            text = f"{seq} {kind} {f[2]} {names.name32(f[3])}->{names.name32(f[4])}"
            if kind == "R":
                text += f" [{names.flags(f[5])}]"
                if len(f) > 10 and f[9] != "0":
                    text += f" bridge={f[9]}/{names.name32(f[10])}"
            return text + f" sim={f[6] if len(f) > 6 else '-'}"
        if kind == "S" and len(f) > 4:
            return f"{seq} S {f[2]} actor {names.name32(f[3])} = {f[4]}"
        if kind == "P" and len(f) > 4:
            return f"{seq} P {f[2]} {names.name32(f[3])} = {names.name32(f[4])}"
        if kind == "Ps" and len(f) > 3:
            return f"{seq} Ps {f[2]} {names.name32(f[3])} = '{f[4] if len(f) > 4 else ''}'"
        if kind in ("I", "Pa", "Co") and len(f) > 3:
            return f"{seq} {kind} {f[2]} {names.name32(f[3])}"
    except (ValueError, IndexError):
        pass
    return line


def machine_of(f: list[str]) -> str:
    """An A| line's machine name from its field 6. A field that is not a number (a truncated or garbled line) keeps its
    raw text instead of raising, so one bad line never ends the whole read."""
    try:
        return names.name64(int(f[6]))
    except ValueError:
        return f[6]


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("--log", default=str(ROOT / ".omo" / "run" / "client.log"))
    p.add_argument("--sim", help="Jennifer / Pablo / an object id: lines naming it, and its drivers' lines")
    p.add_argument("--obj", help="an object id anywhere in the line")
    p.add_argument("--kind", help="comma-separated kinds (A,E,R,S,P,X,Z,T,U,Vx,An,Wd,Ob,Au,Rs,Rx,Po,Pz,Qu,Bf,...)")
    p.add_argument("--driver", help="one driver, plus requests bridging to it")
    p.add_argument("--machine", help="drivers of this machine name (phone, idle, sit_template, bedsleep, ...)")
    p.add_argument("--last", action="store_true", help="with --machine: only its latest driver")
    p.add_argument("--history", action="store_true", help="only late-join history lines (seq 0)")
    p.add_argument("--since", help="HH:MM:SS (log time, UTC)")
    p.add_argument("--until", help="HH:MM:SS")
    p.add_argument("--seq", help="A-B")
    p.add_argument("--around", help="SEQ[:N] lines either side")
    p.add_argument("--grep", help="substring of the raw line")
    p.add_argument("--drivers", action="store_true", help="a table of drivers instead of lines")
    p.add_argument("--kinds", action="store_true", help="counts by kind instead of lines")
    p.add_argument("--raw", action="store_true", help="print raw lines, not decoded")
    p.add_argument("--limit", type=int, default=200)
    a = p.parse_args(argv)

    rows = list(read(Path(a.log)))
    if not rows:
        print(f"no stream lines in {a.log}")
        return 1

    # Drivers: handle -> machine, host, first/last seq, count, released
    drivers: "OrderedDict[str, dict]" = OrderedDict()
    for ts, seq, line in rows:
        f = line.split("|")
        if len(f) < 3:
            continue
        if f[1] == "A" and len(f) > 6:
            drivers[f[2]] = {"machine": machine_of(f), "host": f[3], "first": seq, "ts": ts, "last": seq, "lines": 0, "released": False}
        d = drivers.get(f[2])
        if d is not None and f[1] in ("A", "E", "R", "S", "P", "Ps", "Pn", "Ph", "I", "X", "Pa", "Co"):
            d["lines"] += 1
            d["last"] = seq
            if f[1] == "X":
                d["released"] = True

    sim = SIMS.get(a.sim.lower(), a.sim) if a.sim else None
    keep_drivers: set[str] = set()
    if sim:
        keep_drivers |= {h for h, d in drivers.items() if d["host"] == sim}
    machine_drivers: set[str] = set()
    if a.machine:
        wanted = a.machine.lower()
        matching = [h for h, d in drivers.items() if d["machine"].lower() == wanted or d["machine"].lower().endswith(wanted)]
        machine_drivers = set(matching[-1:] if a.last else matching)
        if not machine_drivers:
            print(f"no driver of machine {a.machine}")
            return 1

    if a.drivers:
        print(f"{'driver':>11} {'machine':<22} {'host':<21} {'from':>8} {'to':>8} {'lines':>5}  released  first seen")
        for h, d in drivers.items():
            if sim and d["host"] != sim:
                continue
            if machine_drivers and h not in machine_drivers:
                continue
            print(f"{h:>11} {d['machine'][:22]:<22} {d['host']:<21} {str(d['first']):>8} {str(d['last']):>8} {d['lines']:>5}  {'yes' if d['released'] else 'no ':<8}  {d['ts']}")
        return 0

    kinds = set(a.kind.split(",")) if a.kind else None
    lo = hi = None
    if a.seq:
        lo, hi = (int(x) for x in a.seq.split("-"))
    if a.around:
        center, _, span = a.around.partition(":")
        width = int(span or 10)
        lo, hi = int(center) - width, int(center) + width

    out = []
    for ts, seq, line in rows:
        f = line.split("|")
        if len(f) < 3:
            continue
        if a.history and seq != 0:
            continue
        if kinds and f[1] not in kinds:
            continue
        if a.since and ts < a.since:
            continue
        if a.until and ts > a.until:
            continue
        if lo is not None and (seq is None or seq == 0 or not lo <= seq <= hi):
            continue
        if a.grep and a.grep not in line:
            continue
        if a.obj and a.obj not in line:
            continue
        if a.driver and not (f[2] == a.driver or (f[1] == "R" and len(f) > 9 and f[9] == a.driver)):
            continue
        if machine_drivers and not (f[2] in machine_drivers or (f[1] == "R" and len(f) > 9 and f[9] in machine_drivers)):
            continue
        if sim and not (sim in f or f[2] in keep_drivers):
            continue
        out.append((ts, line))

    if a.kinds:
        counts = Counter(line.split("|")[1] for _, line in out)
        print(" ".join(f"{k}={v}" for k, v in counts.most_common()))
        return 0

    shown = out[-a.limit:] if a.limit else out
    if len(out) > len(shown):
        print(f"({len(out) - len(shown)} earlier lines not shown; --limit 0 for all)")
    for ts, line in shown:
        print(f"{ts} {line if a.raw else decode(line)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
