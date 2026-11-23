#!/usr/bin/env python3
"""DEV-143 Step 2: queue UserDirected ReadBookChooser or GetBook on the client bridge."""
from __future__ import annotations

import argparse
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
BRIDGECTL = REPO / "scripts" / "bridgectl.py"
SIMCTL = REPO / "scripts" / "simctl.py"


def main() -> int:
    parser = argparse.ArgumentParser(description="DEV-143 decision-injection probe")
    parser.add_argument("--endpoint", default="127.0.0.1:34030", help="client bridgectl (34030)")
    parser.add_argument("--sim", type=int, required=True)
    parser.add_argument("--book", type=int, required=True, help="book GameObject id")
    parser.add_argument("--mode", choices=["chooser", "getbook"], default="chooser")
    args = parser.parse_args()

    subprocess.run([sys.executable, str(SIMCTL), "speed", "normal"], check=True, cwd=REPO)
    mode_hex = subprocess.check_output(
        [sys.executable, str(BRIDGECTL), "hexenc", args.mode],
        cwd=REPO,
        text=True,
    ).strip()
    cmd = [
        sys.executable,
        str(BRIDGECTL),
        "--endpoint",
        args.endpoint,
        f"INJECT_READ_INTENT {args.sim} {args.book} {mode_hex}",
    ]
    proc = subprocess.run(cmd, cwd=REPO)
    return proc.returncode


if __name__ == "__main__":
    raise SystemExit(main())
