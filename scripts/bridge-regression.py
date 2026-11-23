#!/usr/bin/env python3
"""bridge-regression -- regression matrix for bridge/infra slices (DEV-158+).

Runs layered unit filters plus optional live smoke. Emits a JSON report.
Exit 0 only when every selected tier passes.
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def run_cmd(name: str, argv: list[str], cwd: Path | None = None) -> dict:
    started = time.time()
    proc = subprocess.run(argv, cwd=cwd or ROOT, capture_output=True, text=True)
    elapsed = round(time.time() - started, 2)
    out = (proc.stdout or "") + (proc.stderr or "")
    m = re.search(r"RESULT total=(\d+) failed=(\d+)", out)
    total = int(m.group(1)) if m else None
    failed = int(m.group(2)) if m else None
    ok = proc.returncode == 0 and (failed in (None, 0))
    return {
        "tier": name,
        "command": " ".join(argv),
        "ok": ok,
        "exit_code": proc.returncode,
        "elapsed_s": elapsed,
        "total": total,
        "failed": failed,
        "tail": out.strip()[-1200:],
    }


def dotnet_filter(filter_name: str) -> dict:
    # Release config avoids MSB3027 when dev-session holds Debug\SimsOnline.DedicatedServer.exe
    build = run_cmd(
        "build-release-" + filter_name.lower(),
        ["dotnet", "build", "DedicatedServer.Tests", "-c", "Release", "--nologo", "-v", "q"],
    )
    if not build["ok"]:
        build["tier"] = "unit-" + filter_name.lower()
        return build
    result = run_cmd(
        "unit-" + filter_name.lower(),
        [
            "dotnet", "run", "--project", "DedicatedServer.Tests", "-c", "Release", "--no-build",
            "--", "--filter", filter_name,
        ],
    )
    return result


def live_bridge_health(timeout: float) -> dict:
    script = ROOT / "scripts" / "bridge-health.py"
    return run_cmd("live-bridge-health", [sys.executable, str(script), "--timeout", str(timeout)])


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--profile", choices=["dev158", "full"], default="dev158")
    ap.add_argument("--live", action="store_true")
    ap.add_argument("--json", action="store_true")
    ap.add_argument("--archive", type=Path)
    args = ap.parse_args()

    if args.profile == "dev158":
        filters = ["BridgeLineCodec", "BridgeShared", "Mirror", "PublicProtocolCodec"]
    else:
        filters = [
            "BridgeLineCodec", "BridgeShared", "Mirror", "PublicProtocolCodec",
            "AuthoritativeSession", "ClientListenerServer",
        ]

    tiers = [dotnet_filter(f) for f in filters]
    if args.live:
        tiers.append(live_bridge_health(15.0))

    ok = all(t["ok"] for t in tiers)
    report = {
        "ok": ok,
        "profile": args.profile,
        "live": args.live,
        "tiers": tiers,
        "failed_tiers": [t["tier"] for t in tiers if not t["ok"]],
    }

    if args.archive:
        args.archive.parent.mkdir(parents=True, exist_ok=True)
        args.archive.write_text(json.dumps(report, indent=2), encoding="utf-8")

    if args.json:
        print(json.dumps(report, indent=2))
    else:
        print("=== bridge-regression ===")
        for t in tiers:
            status = "PASS" if t["ok"] else "FAIL"
            counts = ""
            if t.get("total") is not None:
                counts = f" ({t['total']} tests, failed={t.get('failed')})"
            print(f"  {status}  {t['tier']}{counts}  [{t['elapsed_s']}s]")
        print(f"OVERALL: {'PASS' if ok else 'FAIL'}")
        if not ok:
            print("Failed: " + ", ".join(report["failed_tiers"]), file=sys.stderr)

    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())