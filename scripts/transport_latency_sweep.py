#!/usr/bin/env python3
"""experimental bridge-poll sweep on the live local server and client.

Runs 250 -> 100 -> 50 -> 25 -> 250 millisecond game bridge cadence
with separate traffic traces, tick progression, and restoration in finally.
Controller presentation cadence remains unchanged. No mod installs.
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import re
import subprocess
import sys
import time
from pathlib import Path

import transport_latency_ab as ab

ROOT = Path(__file__).resolve().parents[1]
RUN = ROOT / ".omo" / "run"
ENDPOINTS = {
    "server": "127.0.0.1:34029",
    "client": "127.0.0.1:34030",
}


def probe_state() -> dict[str, dict]:
    result = {}
    for role, endpoint in ENDPOINTS.items():
        command = [sys.executable, str(ROOT / "scripts" / "bridgectl.py"),
                   "--endpoint", endpoint, "--timeout", "20", "DIAG_STATE"]
        outcome = subprocess.run(command, cwd=ROOT, capture_output=True,
                                 text=True, timeout=27)
        data = outcome.stdout + outcome.stderr
        if outcome.returncode or "accepted=True" not in data:
            raise RuntimeError(role + " DIAG_STATE failed: " + data[:400])
        attributes = dict(re.findall(r"([A-Za-z][A-Za-z0-9]*)=([^;\r\n]+)", data))
        if attributes.get("inWorld") != "True":
            raise RuntimeError(role + " is not in-world: " + data[:400])
        result[role] = {
            "tick": int(attributes.get("tick", -1)),
            "pumpTicks": int(attributes.get("pumpTicks", -1)),
            "role": attributes.get("role"),
            "game_speed": attributes.get("observedSpeed"),
            "inWorld": attributes.get("inWorld"),
        }
    return result


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--seconds", type=int, default=90)
    ap.add_argument("--settle", type=int, default=4)
    opt = ap.parse_args()
    if not 30 <= opt.seconds <= 240:
        ap.error("--seconds outside 30-240")
    if not 0 <= opt.settle <= 20:
        ap.error("--settle outside 0-20")
    output = RUN / ("transport-sweep-" + dt.datetime.now().strftime("%Y%m%d-%H%M%S"))
    ab.OUT = output
    output.mkdir(parents=True, exist_ok=True)
    sequence = [250, 100, 50, 25, 250]
    meta = {"scheme": "controlled idle-poll sweep",
            "controller_presentation_ms": 250,
            "period_seconds": opt.seconds,
            "sequence": sequence,
            "started": dt.datetime.now(dt.timezone.utc).isoformat(),
            "git": subprocess.run(["git", "rev-parse", "--short", "HEAD"],
                                  cwd=ROOT, capture_output=True, text=True).stdout.strip(),
            "samples": []}
    status_path = output / "metadata.json"
    try:
        for index, interval in enumerate(sequence):
            print(f"PHASE {index + 1}/{len(sequence)} poll_ms={interval}", flush=True)
            for endpoint in ENDPOINTS.values():
                ab.bridge(endpoint, interval)
            if opt.settle:
                time.sleep(opt.settle)
            start_states = probe_state()
            marks = ab.mark()
            before = time.monotonic()
            time.sleep(opt.seconds)
            elapsed = time.monotonic() - before
            ends = ab.mark()
            end_states = probe_state()
            name = f"{index+1:02d}-poll{interval}"
            ab.capture(name, marks, ends)
            phase = {
                "name": name, "poll_ms": interval, "wall_seconds": round(elapsed, 3),
                "start": start_states, "end": end_states,
                "ticks_per_second": {
                    role: round((end_states[role]["tick"] - start_states[role]["tick"])/elapsed, 3)
                    for role in ENDPOINTS
                },
                "bridge_pumps_per_second": {
                    role: round((end_states[role]["pumpTicks"] - start_states[role]["pumpTicks"])/elapsed, 3)
                    for role in ENDPOINTS
                },
            }
            meta["samples"].append(phase)
            status_path.write_text(json.dumps(meta, indent=2), encoding="utf-8")
            print("PHASE METRICS " + json.dumps({
                "poll_ms": interval, "ticks_per_second": phase["ticks_per_second"],
                "pumps_per_second": phase["bridge_pumps_per_second"]}), flush=True)
        return 0
    finally:
        for role, endpoint in ENDPOINTS.items():
            try:
                ab.bridge(endpoint, 250)
            except Exception as exc:
                print("RESTORE FAILURE " + role + ": " + str(exc), file=sys.stderr, flush=True)
        meta["ended"] = dt.datetime.now(dt.timezone.utc).isoformat()
        status_path.write_text(json.dumps(meta, indent=2), encoding="utf-8")
        print("SWEEP RESULT PATH " + str(output), flush=True)


if __name__ == "__main__":
    raise SystemExit(main())
