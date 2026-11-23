#!/usr/bin/env python3
"""/ live same-PC transport A/B, no restart or mod install.

Requires a healthy server + Client2Box, updated mod build, and
SIMSONLINE_TRANSPORT_TRACE=1 in the running controllers.
Restores default bridge idle cadence (250 ms) in a finally block.
"""
from __future__ import annotations
import argparse
import datetime as dt
import json
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RUN = ROOT / ".omo" / "run"
FILES = ["server.log", "client.log", "client.log.err"]
OUT = RUN / ("transport-ab-" + dt.datetime.now().strftime("%Y%m%d-%H%M%S"))


def bridge(endpoint: str, millis: int) -> None:
    cmd = [sys.executable, str(ROOT / "scripts" / "bridgectl.py"),
           "--endpoint", endpoint, "--timeout", "20",
           "SACS_RECORDER", "pump-ms", str(millis)]
    result = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True,
                            timeout=28, check=False)
    output = (result.stdout + result.stderr).strip()
    print(f"bridge={endpoint} requested_ms={millis} exit={result.returncode}: {output}", flush=True)
    if result.returncode or "accepted=True" not in output or f"pumpMs={millis}" not in output:
        raise RuntimeError("Game rejected bridge cadence change.")


def mark() -> dict[str, int]:
    return {name: (RUN / name).stat().st_size for name in FILES}


def capture(name: str, first: dict[str, int], last: dict[str, int]) -> None:
    directory = OUT / name
    directory.mkdir(parents=True, exist_ok=True)
    names = []
    for file in FILES:
        with (RUN / file).open("rb") as stream:
            stream.seek(first[file])
            data = stream.read(max(0, last[file] - first[file]))
        # The marker may be inside a concurrent append.
        if first[file] and b"\n" in data:
            data = data.partition(b"\n")[2]
        target = directory / file
        target.write_bytes(data)
        names.append(str(target))
    result = subprocess.run(
        [sys.executable, str(ROOT / "scripts" / "transport_latency_report.py"),
         "--same-host", *names, "--output", str(directory / "report.md")],
        capture_output=True, text=True, timeout=45, cwd=ROOT
    )
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)
    text = (directory / "report.md").read_text(encoding="utf-8")
    print(f"=== {name} captured ===", flush=True)
    for row in text.splitlines():
        if ("client_bridge_replay" in row or "server_bridge_dump" in row
                or "Matched source events:" in row or "Exact-sequence matched samples:" in row
                or "presentation_pump" in row):
            print(row, flush=True)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--seconds", type=int, default=50)
    parser.add_argument("--fast-ms", type=int, default=50)
    options = parser.parse_args()
    if not 20 <= options.seconds <= 180:
        parser.error("--seconds must be 20..180")
    if not 25 <= options.fast_ms <= 250:
        parser.error("--fast-ms must be 25..250")
    OUT.mkdir(parents=True, exist_ok=True)
    info = {"fast_ms": options.fast_ms, "seconds": options.seconds,
            "start": dt.datetime.now(dt.timezone.utc).isoformat(),
            "git": subprocess.run(["git", "rev-parse", "--short", "HEAD"],
                                  cwd=ROOT, text=True, capture_output=True).stdout.strip()}
    (OUT / "metadata.json").write_text(json.dumps(info, indent=2), encoding="utf-8")
    endpoints = ["127.0.0.1:34029", "127.0.0.1:34030"]
    # Validate new commands before collecting any samples.
    try:
        for endpoint in endpoints:
            bridge(endpoint, 250)
        first = mark()
        print(f"BASELINE: {options.seconds} seconds", flush=True)
        time.sleep(options.seconds)
        last = mark()
        capture("baseline250", first, last)

        for endpoint in endpoints:
            bridge(endpoint, options.fast_ms)
        first = mark()
        print(f"FAST: {options.fast_ms}ms for {options.seconds} seconds", flush=True)
        time.sleep(options.seconds)
        last = mark()
        capture(f"fast{options.fast_ms}", first, last)
        return 0
    finally:
        for endpoint in endpoints:
            try:
                bridge(endpoint, 250)
            except Exception as exc:
                print(f"RESTORE FAILED endpoint={endpoint}: {exc}", file=sys.stderr, flush=True)
        print(f"RESULTS {OUT}", flush=True)


if __name__ == "__main__":
    raise SystemExit(main())
