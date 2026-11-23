#!/usr/bin/env python3
"""logq — cross-log timeline/verify/errors for SimsOnline dev-session runs (DEV-106).

Merges server.log, client.log, and the lossless bridge records onto ONE clock and answers
"what happened to this interaction/command" in a single query instead of four grep chains.

Commands:
  logq.py timeline --interaction 6537          causal chain for an interaction id
  logq.py timeline --corr 18f37751...          by correlation/command id (dashes optional)
  logq.py timeline --actor 18031287978496800304  everything touching a Sim
  logq.py timeline --grep "GoHere"             free regex across all sources
  logq.py verify ut-1                          marker-chain check from the test card
  logq.py errors                               wedge indicators across all sources
  logq.py --dir .omo/run/archive/<run> ...     query an archived run (old log formats OK)

Clock handling: new-format stamps are UTC with date+ms. OLD logs (pre-DEV-106 archives) mix
local-time bridge stamps, UTC seconds-only client stamps, and untimestamped EVENT lines —
logq converts local→UTC using this machine's offset (--server-offset-hours to override),
infers dates from file mtime with midnight-rollover detection, and lets untimestamped lines
inherit the previous line's stamp (file order is preserved as a tiebreaker).
"""
import argparse
import datetime as dt
import os
import re
import sys

DEFAULT_DIR = os.path.join(".omo", "run")

SOURCES = ["server.log", "client.log", "server-bridge.log", "client-bridge.log"]

WEDGE_PATTERNS = [
    r"Timed out waiting",
    r"unmatched response dropped",
    r"RESPONSE LOST",
    r"mirror_dispatch_failed",
    r"mirror_notify_failed",
    r"Unhandled exception",
    r"command_rejected",
    r"client_ts3_startup_failed",
    r"\bERROR\|",
]

CARDS = {
    "ut-1": {
        "ordered": [("client.log", "intent_forwarded"),
                    ("client.log", "interaction_queued"),
                    ("client.log", "mirror_echo_confirmed")],
        "forbid": WEDGE_PATTERNS,
        "note": "P4.4 input capture round-trip (DEV-62). Sim-walks is human-verified; this checks the wire chain.",
    },
    "ut-2": {
        "ordered": [("client.log", "game_speed_changed")],
        "forbid": [r"mirror_dispatch_failed"],
        "note": "P5.0 speed authority. Both-windows clock check is human-verified.",
    },
}

# [bridge 2026-06-12 01:23:45.678Z] ...   |   [bridge 09:52:34.232] ...
RE_BRIDGE_NEW = re.compile(r"^\[bridge (\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3})Z\] (.*)$")
RE_BRIDGE_OLD = re.compile(r"^\[bridge (\d\d:\d\d:\d\d\.\d{3})\] (.*)$")
# [2026-06-12 01:23:45.678Z] EVENT: ... / kind: ...
RE_STAMP_NEW = re.compile(r"^\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3})Z\] (.*)$")
# [23:52:34] kind: ...
RE_STAMP_OLD = re.compile(r"^\[(\d\d:\d\d:\d\d)\] (.*)$")


class Entry:
    __slots__ = ("ts", "source", "order", "text")

    def __init__(self, ts, source, order, text):
        self.ts = ts
        self.source = source
        self.order = order
        self.text = text


def machine_utc_offset_hours() -> float:
    offset = dt.datetime.now().astimezone().utcoffset() or dt.timedelta(0)
    return offset.total_seconds() / 3600.0


def parse_file(path: str, server_offset_hours: float):
    """Yield Entry objects with best-effort UTC timestamps (None inherits later)."""
    name = os.path.basename(path)
    try:
        mtime = dt.datetime.fromtimestamp(os.path.getmtime(path), dt.timezone.utc).replace(tzinfo=None)
    except OSError:
        return
    base_date = mtime.date()
    entries = []
    last_time = {"old_bridge": None, "old_plain": None}
    day_carry = {"old_bridge": 0, "old_plain": 0}

    with open(path, "r", encoding="utf-8", errors="replace") as handle:
        for order, raw in enumerate(handle):
            line = raw.rstrip("\n")
            if not line.strip():
                continue
            ts = None
            body = line

            m = RE_BRIDGE_NEW.match(line) or RE_STAMP_NEW.match(line)
            if m:
                ts = dt.datetime.strptime(m.group(1), "%Y-%m-%d %H:%M:%S.%f")
            else:
                m_old_b = RE_BRIDGE_OLD.match(line)
                m_old_p = RE_STAMP_OLD.match(line) if not m_old_b else None
                if m_old_b:
                    # OLD server bridge stamps are LOCAL time, no date.
                    t = dt.datetime.strptime(m_old_b.group(1), "%H:%M:%S.%f").time()
                    ts = _date_time(base_date, t, last_time, day_carry, "old_bridge")
                    ts -= dt.timedelta(hours=server_offset_hours)
                elif m_old_p:
                    # OLD client stamps are UTC, seconds only, no date.
                    t = dt.datetime.strptime(m_old_p.group(1), "%H:%M:%S").time()
                    ts = _date_time(base_date, t, last_time, day_carry, "old_plain")
            entries.append(Entry(ts, name, order, body))

    # Untimestamped lines (old EVENT:, startup banners) inherit the nearest prior stamp,
    # else the nearest following stamp, so they sort into roughly the right place. A file
    # with NO stamps at all keeps ts=None throughout (callers group those separately).
    prev = None
    for e in entries:
        if e.ts is None and prev is not None:
            e.ts = prev
        prev = e.ts or prev
    nxt = None
    for e in reversed(entries):
        if e.ts is None and nxt is not None:
            e.ts = nxt
        nxt = e.ts or nxt
    return entries


def _date_time(base_date, t, last_time, day_carry, key):
    """Attach a date to a time-only stamp, detecting midnight rollover."""
    if last_time[key] is not None and t < last_time[key]:
        # time went backwards within one file -> crossed midnight
        if (dt.datetime.combine(base_date, last_time[key]) - dt.datetime.combine(base_date, t)).total_seconds() > 3600:
            day_carry[key] += 1
    last_time[key] = t
    return dt.datetime.combine(base_date, t) + dt.timedelta(days=day_carry[key])


def load_entries(run_dir: str, server_offset_hours: float):
    all_entries = []
    found = []
    for source in SOURCES:
        path = os.path.join(run_dir, source)
        if os.path.exists(path):
            found.append(source)
            all_entries.extend(parse_file(path, server_offset_hours) or [])
    if not found:
        sys.exit(f"no log files found in {run_dir} (expected any of: {', '.join(SOURCES)})")
    all_entries.sort(key=lambda e: (e.ts or dt.datetime.min, e.source, e.order))
    return all_entries, found


def number_pattern(value: str) -> re.Pattern:
    return re.compile(r"(?<![0-9])" + re.escape(value) + r"(?![0-9])")


def cmd_timeline(args, entries):
    matchers = []
    if args.interaction:
        matchers.append(number_pattern(args.interaction))
    if args.actor:
        matchers.append(number_pattern(args.actor))
    if args.corr:
        norm = args.corr.replace("-", "").lower()
        # corr appears as 32-hex in bridge lines and dashed in client JSON
        dashed = f"{norm[0:8]}-{norm[8:12]}-{norm[12:16]}-{norm[16:20]}-{norm[20:32]}" if len(norm) == 32 else args.corr
        matchers.append(re.compile(re.escape(norm) + "|" + re.escape(dashed), re.IGNORECASE))
    if args.grep:
        matchers.append(re.compile(args.grep))
    if not matchers:
        sys.exit("timeline needs at least one of --interaction / --actor / --corr / --grep")

    hits = [e for e in entries if any(m.search(e.text) for m in matchers)]
    if not hits:
        print("no matching lines")
        return 1

    stamped = [e for e in hits if e.ts is not None]
    unstamped = [e for e in hits if e.ts is None]
    t0 = stamped[0].ts if stamped else None
    for e in stamped:
        delta = f"+{(e.ts - t0).total_seconds():9.3f}s"
        src = e.source.replace(".log", "")
        print(f"{delta} [{src:13}] {e.text}")
    if unstamped:
        print(f"\n-- {len(unstamped)} line(s) from source(s) with no timestamps (file order only; "
              "pre-DEV-106 server logs without --bridge-trace) --")
        for e in unstamped:
            src = e.source.replace(".log", "")
            print(f"           [{src:13}] {e.text}")
    print(f"\n{len(hits)} lines across {len(set(e.source for e in hits))} source(s)"
          + (f"; t0 = {t0}Z" if t0 else ""))
    return 0


def cmd_errors(args, entries):
    patterns = [re.compile(p) for p in WEDGE_PATTERNS]
    hits = [e for e in entries if any(p.search(e.text) for p in patterns)]
    if not hits:
        print("no wedge indicators found")
        return 0
    for e in hits:
        src = e.source.replace(".log", "")
        stamp = e.ts.strftime("%H:%M:%S.%f")[:-3] if e.ts else "?"
        print(f"{stamp} [{src:13}] {e.text[:240]}")
    print(f"\n{len(hits)} indicator line(s)")
    return 1


def cmd_verify(args, entries):
    card = CARDS.get(args.card.lower())
    if not card:
        sys.exit(f"unknown card '{args.card}' (have: {', '.join(CARDS)})")
    print(f"verify {args.card}: {card['note']}")
    failed = False

    cursor = -1
    for source, marker in card["ordered"]:
        found = None
        for e in entries:
            if e.source == source and marker in e.text and e.order > (cursor if e.source == source else -1):
                found = e
                break
        if found:
            cursor = found.order
            stamp = found.ts.strftime("%H:%M:%S.%f")[:-3] if found.ts else "?"
            print(f"  PASS  {marker}  ({stamp} {source})")
        else:
            failed = True
            print(f"  FAIL  {marker}  (not found in {source} after previous marker)")

    forbid = [re.compile(p) for p in card["forbid"]]
    bad = [e for e in entries if any(p.search(e.text) for p in forbid)]
    if bad:
        failed = True
        print(f"  FAIL  {len(bad)} forbidden indicator line(s):")
        for e in bad[:8]:
            print(f"          [{e.source}] {e.text[:160]}")
    else:
        print("  PASS  no forbidden indicators")

    print("RESULT: " + ("FAIL" if failed else "PASS"))
    return 1 if failed else 0


def main() -> int:
    parser = argparse.ArgumentParser(prog="logq", add_help=True)
    parser.add_argument("--dir", default=DEFAULT_DIR, help="run dir or archive dir (default .omo/run)")
    parser.add_argument("--server-offset-hours", type=float, default=None,
                        help="UTC offset of OLD-format server bridge stamps (default: this machine's)")
    sub = parser.add_subparsers(dest="command", required=True)

    p_timeline = sub.add_parser("timeline")
    p_timeline.add_argument("--interaction")
    p_timeline.add_argument("--actor")
    p_timeline.add_argument("--corr")
    p_timeline.add_argument("--grep")

    sub.add_parser("errors")

    p_verify = sub.add_parser("verify")
    p_verify.add_argument("card")

    args = parser.parse_args()
    offset = args.server_offset_hours if args.server_offset_hours is not None else machine_utc_offset_hours()
    entries, found = load_entries(args.dir, offset)
    print(f"# {args.dir}: {', '.join(found)} ({len(entries)} lines)", file=sys.stderr)

    if args.command == "timeline":
        return cmd_timeline(args, entries)
    if args.command == "errors":
        return cmd_errors(args, entries)
    if args.command == "verify":
        return cmd_verify(args, entries)
    return 2


if __name__ == "__main__":
    sys.exit(main())
