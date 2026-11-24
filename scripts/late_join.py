"""What a client joining now would receive for these Sims: the late-join history, per driver, decoded.

    python scripts/late_join.py                    # Jennifer and Pablo
    python scripts/late_join.py Pablo --full       # every line, not just each driver's summary
    python scripts/late_join.py --state            # only the engine-boundary state (pseudo driver 4294967280)

Asks the server's recorder the way the dedicated server does at a join (SACS_RECORDER snapshot <sims>, then
snapshot-driver <handle> <offset> page by page) and prints, per driver: its machine, its host, how many lines and the
states it passes through, with any request that hands the Sim to another machine (a bridge). The first "driver"
(4294967280) is BoundaryState: the joining Sims' reset (Z), their transforms, object state, live sounds and effects.
Side effect: like a real join, the drivers it lists count as forwarded, so their next lines also go to clients (they
already do for Sims on a player's lot).
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import sacs_names as names  # noqa: E402
from stream_log import SIMS, decode  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
SERVER = "127.0.0.1:34029"
STATE = "4294967280"


def check_liveness(ids: list[str]) -> int:
    """Verify native completion and live posture/interaction ownership against actual history selection."""
    wanted = "," + ",".join(str(int(value)) for value in ids) + ","
    template = ROOT / "scripts/probes/HistoryLivenessProbe.cs"
    source = template.read_text(encoding="utf-8").replace(
        'private const string TargetIds = "";',
        f'private const string TargetIds = "{wanted}";',
    )
    generated = ROOT / ".omo/run/act/HistoryLivenessProbe.cs"
    generated.parent.mkdir(parents=True, exist_ok=True)
    generated.write_text(source, encoding="utf-8")
    result = subprocess.run(
        [sys.executable, "scripts/bridgectl.py", "--endpoint", SERVER, "probe", str(generated)],
        cwd=ROOT,
        capture_output=True,
        text=True,
        timeout=90,
        check=False,
    )
    print(result.stdout, end="")
    if result.stderr:
        print(result.stderr, file=sys.stderr, end="")
    return 0 if result.returncode == 0 and re.search(r"\bok\s*=\s*true\b", result.stdout) else 1


def recorder(*args: str) -> list[str]:
    out = subprocess.run(
        [sys.executable, "scripts/bridgectl.py", "--endpoint", SERVER, "SACS_RECORDER", *args],
        cwd=ROOT,
        capture_output=True,
        text=True,
        timeout=60,
    ).stdout
    return out.splitlines()


def driver_lines(handle: str) -> list[str]:
    lines: list[str] = []
    offset = 0
    for _ in range(64):
        reply = recorder("snapshot-driver", handle, str(offset))
        head = next((ln for ln in reply if ln.startswith("lines=")), "")
        lines.extend(ln for ln in reply if re.match(r"^0\|", ln))
        m = re.search(r"next=(-?\d+)", head)
        if not m or int(m.group(1)) < 0:
            break
        offset = int(m.group(1))
    return lines


def machine_of(f: list[str]) -> str:
    """The A| line's machine name from its field 6; a missing field or one that is not a number prints as "?" instead of
    raising and ending the read of every driver."""
    if len(f) <= 6:
        return "?"
    try:
        return names.name64(int(f[6]))
    except ValueError:
        return "?"


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("sims", nargs="*", default=["Jennifer", "Pablo"])
    p.add_argument("--full", action="store_true")
    p.add_argument("--state", action="store_true")
    p.add_argument(
        "--check-liveness",
        action="store_true",
        help="fail if history loses an owned/active driver or includes a finished unowned driver (SimLink v16)",
    )
    a = p.parse_args(argv)
    ids = [SIMS.get(s.lower()) or s for s in a.sims]
    if a.check_liveness:
        try:
            return check_liveness(ids)
        except ValueError:
            p.error("--check-liveness needs numeric object IDs or known Sim names")
    reply = recorder("snapshot", ",".join(ids))
    handles = next((ln.split("=", 1)[1] for ln in reply if ln.startswith("drivers=")), "-")
    if handles == "-":
        print("no drivers (is the recorder on, and are these Sims known?)")
        return 1
    total = 0
    for handle in handles.split(","):
        if a.state and handle != STATE:
            continue
        lines = driver_lines(handle)
        total += len(lines)
        if handle == STATE:
            kinds = Counter(ln.split("|")[1] for ln in lines)
            print(
                f"== {handle} boundary state: {len(lines)} lines ({' '.join(f'{k}={v}' for k, v in kinds.most_common())})"
            )
        else:
            first = next((ln for ln in lines if ln.split("|")[1] == "A"), "")
            f = first.split("|")
            machine = machine_of(f)
            states = [
                names.name32(ln.split("|")[4])
                for ln in lines
                if ln.split("|")[1] in ("E", "R") and len(ln.split("|")) > 4
            ]
            bridges = [
                ln for ln in lines if ln.split("|")[1] == "R" and len(ln.split("|")) > 10 and ln.split("|")[9] != "0"
            ]
            print(
                f"== {handle} {machine} host={f[3] if len(f) > 3 else '?'}: {len(lines)} lines; states {' > '.join(states[-8:]) or '-'}"
                + (f"; hands over to {', '.join(ln.split('|')[9] for ln in bridges)}" if bridges else "")
            )
        if a.full or a.state:
            for ln in lines:
                print("   " + decode(ln))
    print(f"{total} lines in all")
    return 0


if __name__ == "__main__":
    sys.exit(main())
