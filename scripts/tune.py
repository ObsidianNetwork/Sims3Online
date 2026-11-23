"""Live-tunable numbers in a running TS3 (SimsOnlineServerHostMod/Tunables.cs): try a threshold or a delay without a build.

    python scripts/tune.py                          # list the client's tunables (value, and the default when set)
    python scripts/tune.py mirror.settle            # show one (reads only: the mod's own "tune <name>" resets it)
    python scripts/tune.py mirror.settle 0.8        # set one on the client
    python scripts/tune.py mirror.settle default    # back to the code's default
    python scripts/tune.py --side server ...        # the server TS3 (--side both: each)
    python scripts/tune.py --side clients ...       # both players' games (client1, client2; all: those and the server)

A tunable shows up in the list once the code has read it (or once set). Values are not saved: a restart is back to the
defaults; change the default in code once a value is settled.
"""
from __future__ import annotations

import argparse
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import bridgectl
import compare_client
from local_endpoints import SERVER_BRIDGECTL

# "client" is the game SIMSONLINE_COMPARE_CLIENT names (compare_client.py): client 1 unless a run chose player 2's.
# client1 and client2 name a player's game whatever the run chose: a presentation tunable such as walk.start-lead-ms
# acts on each copy, so a two-player run sets it on both (--side clients).
ENDPOINTS = {"server": SERVER_BRIDGECTL, "client": compare_client.endpoint(), "client1": compare_client.CLIENT1,
             "client2": compare_client.client2(), "client3": compare_client.client_n(3)}
SIDES = {"client": ["client"], "server": ["server"], "both": ["server", "client"], "client1": ["client1"],
         "client2": ["client2"], "client3": ["client3"], "clients": ["client1", "client2"],
         "all": ["server", "client1", "client2"]}


def float32_problem(value: str) -> str | None:
    """Why a value cannot be a tunable's value, or None. The mod reads values as float32 (24 bits of precision), so an
    integer above 2^24 such as a clip or parameter hash arrives rounded (1176358403 as 1176358400): put an id in the
    tunable's name instead (sacs.steer.<machine>.<variant>=1, sacs.force.<hash>=1)."""
    try:
        number = float(value)
    except ValueError:
        return None
    rounded = struct.unpack("f", struct.pack("f", number))[0] if abs(number) < 3.4e38 else float("inf")
    # An integer must arrive exact (an id off by 3 is another id); a fraction such as 0.1 only close.
    if rounded == number or (not number.is_integer() and abs(rounded - number) <= 1e-6 * max(1.0, abs(number))):
        return None
    arrives = str(int(rounded)) if rounded.is_integer() else f"{rounded:.9g}"
    return (f"{value} would arrive as {arrives}: the mod reads tunable values as float32, so put an id or hash "
            "in the tunable's name (sacs.force.<hash>=1), not in its value")


def tune(endpoint: str, name: str | None = None, value: str | None = None) -> str:
    sock = bridgectl.connect(endpoint, 30)
    try:
        line = "SACS_RECORDER tune" + (" " + name if name else "") + (" " + value if name and value else "")
        reply = bridgectl.roundtrip(sock, line)
    finally:
        sock.close()
    return str(reply.get("report") or reply.get("reason") or reply)


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("name", nargs="?")
    p.add_argument("value", nargs="?")
    p.add_argument("--side", choices=list(SIDES), default="client")
    a = p.parse_args(argv)
    sides = SIDES[a.side]
    if a.value is not None and float32_problem(a.value):
        print(f"not set: {float32_problem(a.value)}")
        return 2
    failed = False
    for side in sides:
        # Each game on its own: with --side both a dead server must not leave the client unset (boot 5's last tune-off
        # failed on the dead server and left the client's sacs.keyed-picks and sacs.node-order on, 2026-10-18).
        try:
            if a.name and a.value is None:
                # The mod reads a bare name as "back to the default" (2026-10-17: a check that read a tunable reset
                # it): show its row from the list instead.
                rows = tune(ENDPOINTS[side]).splitlines()
                report = next((r for r in rows if r.startswith(a.name + "=")), f"{a.name} not read yet (not set)")
                print(f"[{side}] " + report.strip())
                continue
            report = tune(ENDPOINTS[side], a.name, a.value)
        except (OSError, SystemExit) as ex:  # bridgectl.connect exits on a refused connection; a reset or a timeout raises
            failed = True
            reason = str(ex).splitlines()[0] if str(ex) else type(ex).__name__
            print(f"[{side}] error=not answering ({reason})")
            continue
        failed = failed or report.startswith("error=")
        if a.name and not report.startswith("error="):
            try:
                import crash_ledger
                crash_ledger.note_tunable(side, a.name, a.value)
            except ImportError:
                pass
        print(f"[{side}] " + report.strip())
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
