"""Set or read the games' idle bridge poll (SACS_RECORDER pump-ms), and each game's tick rate.

The mod services bridge commands on a task that waits sBridgeIdlePollMs between ticks when nothing is queued (50 ms by
default since 2026-10-21, 250 before). Every controller request waits for that tick, so one round trip costs about two
waits: about 600 ms at 250, about 100 ms at 50 (2026-10-21, server, in-process bridgectl). A late joiner's history was
about ten round trips that the presentation loop awaited inline, so the established players' stream stopped for 7.2 s
and 7.7 s at 250, and for 2.6 s and 2.3 s at 50, with the server's tick rate unchanged (30.0 against 29.9/s). The suite
A/B (arms 20261021-074634: p250 p50 p50 p250, seven scenarios, server and both clients) failed alike at both values.

    python scripts/pump_ms.py 50                    # server and client 1
    python scripts/pump_ms.py 50 --clients 2        # server and both clients
    python scripts/pump_ms.py p50 --clients 2       # an arm name: its digits (scenarios.py arms --before "... {arm}")
    python scripts/pump_ms.py status --clients 2    # each game's value and tick rate over --seconds, left as it is
    python scripts/pump_ms.py status --expect p50 --clients 2 --seconds 0   # exit 1 unless every game holds 50
    python scripts/pump_ms.py 50 --only server      # one game

status reads the value back with `SACS_RECORDER pump-ms` (no value), which mods from 2026-10-21 on answer; an older
mod's answer is shown as "?".

The value lives in the running game only: a TS3 restart goes back to the mod's default.
"""

from __future__ import annotations

import argparse
import re
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import bridgectl  # noqa: E402

ENDPOINTS = {"server": "127.0.0.1:34029", "client1": "127.0.0.1:34030", "client2": "127.0.0.1:34031"}


def interval(value: str) -> int | None:
    """The interval a value names: 50, "p50", "pump-50"; None for status."""
    if value == "status":
        return None
    digits = re.findall(r"\d+", value)
    if not digits:
        raise ValueError(f"no interval in {value!r}")
    ms = int(digits[-1])
    if not 25 <= ms <= 250:
        raise ValueError(f"{ms} ms is outside the mod's 25-250")
    return ms


def games(clients: int, only: str | None) -> dict[str, str]:
    chosen = {k: v for k, v in ENDPOINTS.items() if k == "server" or int(k[-1]) <= clients}
    return {only: chosen[only]} if only else chosen


def current(sock) -> int | None:
    """The game's interval now, or None when its mod cannot report it."""
    m = re.search(r"pumpMs=(\d+)", str(bridgectl.roundtrip(sock, "SACS_RECORDER pump-ms")))
    return int(m[1]) if m else None


def tick(sock) -> int:
    report = str(bridgectl.roundtrip(sock, "DIAG_STATE"))
    m = re.search(r"\btick=(\d+)", report)
    return int(m[1]) if m else -1


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("value", help="the interval in ms (25-250), an arm name holding it, or status")
    p.add_argument("--clients", type=int, choices=(1, 2), default=1)
    p.add_argument("--only", choices=tuple(ENDPOINTS), help="one game")
    p.add_argument("--seconds", type=float, default=5.0, help="how long to count ticks (0: no tick rate)")
    p.add_argument("--expect", help="with status: exit 1 unless every game holds this interval (an arm name works)")
    a = p.parse_args(argv)
    try:
        ms = interval(a.value)
        expected = interval(a.expect) if a.expect else None
    except ValueError as ex:
        p.error(str(ex))
    socks = {name: bridgectl.connect(ep, 20) for name, ep in games(a.clients, a.only).items()}
    for name, sock in socks.items():
        if ms is not None:
            reply = str(bridgectl.roundtrip(sock, f"SACS_RECORDER pump-ms {ms}"))
            if f"pumpMs={ms}" not in reply:
                print(f"{name}: not set: {reply[:200]}")
                return 1
    if ms is None:
        values = {name: current(sock) for name, sock in socks.items()}
        print("pump-ms " + " ".join(f"{name}={'?' if v is None else v}" for name, v in values.items()))
        if expected is not None and any(v != expected for v in values.values()):
            print(f"not every game holds {expected} ms")
            return 1
    if a.seconds > 0:
        start = {name: tick(sock) for name, sock in socks.items()}
        began = time.monotonic()
        time.sleep(a.seconds)
        elapsed = time.monotonic() - began
        rates = {name: (tick(sock) - start[name]) / elapsed for name, sock in socks.items()}
        print(("pump-ms=" + str(ms) + " " if ms is not None else "")
              + " ".join(f"{name}={rate:.1f}ticks/s" for name, rate in rates.items()))
    elif ms is not None:
        print(f"pump-ms={ms} on " + ",".join(socks))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
