#!/usr/bin/env python3
"""bridge-health -- tier-3 bridge readiness probe (DEV-159 tooling).

Verifies bridgectl TCP accept is not enough: PING + DIAG_STATE must round-trip with
inWorld=True, tick>0, dispatch>0 on the server mod (34029) and optionally client (34030).

Exit 0 = healthy; exit 1 = deaf/wedged/not in-world. Used by dev-session boot gates.
"""
import argparse
import re
import sys

import bridgectl
from local_endpoints import CLIENT_BRIDGECTL

DIAG_RE = re.compile(
    r"inWorld=(?P<in_world>[^;\"]+).*tick=(?P<tick>[0-9]+).*dispatch=(?P<dispatch>[0-9]+)",
    re.DOTALL,
)


def probe_endpoint(endpoint: str, timeout: float, label: str, require_in_world: bool) -> dict:
    sock = bridgectl.connect(endpoint, timeout)
    try:
        ping = bridgectl.roundtrip(sock, f"@{int(timeout)} PING")
        if not ping.get("ok") or not ping.get("accepted"):
            return {"label": label, "ok": False, "error": ping.get("error") or "PING rejected"}

        diag = bridgectl.roundtrip(sock, f"@{int(timeout)} DIAG_STATE")
        if not diag.get("ok") or not diag.get("accepted"):
            return {"label": label, "ok": False, "error": diag.get("error") or "DIAG_STATE rejected"}

        report = diag.get("report") or ""
        match = DIAG_RE.search(report)
        if not match:
            return {"label": label, "ok": False, "error": f"unparseable DIAG_STATE: {report!r}"}

        in_world = match.group("in_world").strip()
        tick = int(match.group("tick"))
        dispatch = int(match.group("dispatch"))
        if require_in_world and in_world != "True":
            return {"label": label, "ok": False, "error": f"inWorld={in_world}", "tick": tick, "dispatch": dispatch}
        if tick <= 0 or dispatch <= 0:
            return {"label": label, "ok": False, "error": "tick/dispatch not ready", "tick": tick, "dispatch": dispatch}
        return {"label": label, "ok": True, "in_world": in_world, "tick": tick, "dispatch": dispatch, "role": _role(report)}
    finally:
        try:
            sock.close()
        except OSError:
            pass


def _role(report: str):
    m = re.search(r"role=([^;\"]+)", report)
    return m.group(1) if m else None


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--server-endpoint", default=bridgectl.DEFAULT_ENDPOINT)
    ap.add_argument("--client-endpoint", default=CLIENT_BRIDGECTL)
    ap.add_argument("--timeout", type=float, default=15.0)
    ap.add_argument("--client", action="store_true", help="also probe the configured client bridge endpoint")
    ap.add_argument("--json", action="store_true")
    args = ap.parse_args()

    results = [probe_endpoint(args.server_endpoint, args.timeout, "server", True)]
    if args.client:
        results.append(probe_endpoint(args.client_endpoint, args.timeout, "client", True))

    ok = all(r.get("ok") for r in results)
    if args.json:
        import json
        print(json.dumps({"ok": ok, "probes": results}))
    else:
        for r in results:
            if r.get("ok"):
                print(f"BRIDGE HEALTHY {r['label']}: role={r.get('role')} tick={r['tick']} dispatch={r['dispatch']}")
            else:
                print(f"BRIDGE DEAF {r['label']}: {r.get('error')}", file=sys.stderr)
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
