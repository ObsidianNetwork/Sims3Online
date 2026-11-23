#!/usr/bin/env python3
"""DEV-144: sample off-lot Sim motives/queue over wall-clock intervals.

Requires dev-session up + simctl speed normal. Archives JSON evidence under .omo/run/.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
BRIDGECTL = REPO / "scripts" / "bridgectl.py"
SIMCTL = REPO / "scripts" / "simctl.py"


def run(cmd: list[str]) -> str:
    proc = subprocess.run(cmd, capture_output=True, text=True, cwd=REPO)
    if proc.returncode != 0:
        raise RuntimeError(f"command failed ({proc.returncode}): {' '.join(cmd)}\n{proc.stderr}")
    return proc.stdout.strip()


def diag_state(endpoint: str, sim_id: int) -> dict[str, str]:
    out = run([sys.executable, str(BRIDGECTL), "--endpoint", endpoint, "state", str(sim_id)])
    fields: dict[str, str] = {}
    for line in out.splitlines():
        if "=" in line:
            key, value = line.split("=", 1)
            fields[key.strip()] = value.strip()
    return fields


def main() -> int:
    parser = argparse.ArgumentParser(description="DEV-144 dormancy live probe")
    parser.add_argument("--server-bridge", default="127.0.0.1:34029")
    parser.add_argument("--sim", type=int, required=True, help="off-lot Sim object id to watch")
    parser.add_argument("--samples", type=int, default=12)
    parser.add_argument("--interval-seconds", type=float, default=25.0)
    parser.add_argument("--archive", type=Path, default=None)
    args = parser.parse_args()

    run([sys.executable, str(SIMCTL), "speed", "normal"])

    samples: list[dict] = []
    for i in range(args.samples):
        stamp = datetime.now(timezone.utc).isoformat()
        state = diag_state(args.server_bridge, args.sim)
        samples.append({"utc": stamp, "fields": state})
        print(f"sample {i + 1}/{args.samples} tick={state.get('tick', '?')} hunger={state.get('motiveHunger', '?')}")
        if i + 1 < args.samples:
            time.sleep(args.interval_seconds)

    report = {
        "probe": "DEV-144-dormancy",
        "simId": args.sim,
        "samples": samples,
        "note": "Off-lot Sims are not frozen; expect motive drift per internal documentation",
    }

    if args.archive:
        args.archive.parent.mkdir(parents=True, exist_ok=True)
        args.archive.write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(f"archived {args.archive}")
    else:
        print(json.dumps(report, indent=2))

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
