#!/usr/bin/env python3
"""replay_matrix — DEV-137 interaction replay matrix harness.

For each Sim in the world, captures DIAG_CAPTURE_INTERACTION (the replay bundle)
and DIAG_INTERACTION_STATE (live posture/interaction/book progress) from BOTH
the server and client bridge endpoints, then compares them to produce a
pass/fail matrix showing where server-authoritative interaction state has or
hasn't propagated to the client mirror.

This is the DEV-137 validation harness: it proves the capture→compare loop works
end-to-end and produces evidence bundles for archival.

Usage:
  python scripts/replay_matrix.py                           # all sims, both sides
  python scripts/replay_matrix.py --sim 18031287978496800304 # one sim
  python scripts/replay_matrix.py --sim "Jennifer"           # name match
  python scripts/replay_matrix.py --json                     # machine-readable
  python scripts/replay_matrix.py --poll 5                   # 5 samples, 2s apart
  python scripts/replay_matrix.py --book-id PureLustGrantRodiek  # watch a book
  python scripts/replay_matrix.py --server-only               # skip client side
  python scripts/replay_matrix.py --progress-tolerance 0.10   # looser progress match
  python scripts/replay_matrix.py --archive                   # write JSON to .omo/run/

Requires a live dev-session stack (both TS3 instances in-world) with bridgectl
listeners enabled (stackctl.py enables them automatically).
"""

from __future__ import annotations

import argparse
import json
import os
import socket
import sys
import time
from dataclasses import dataclass, field
from typing import Optional

# Same dir imports
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from diag_state import (
    DiagInteractionState,
    SOCIALIZING_POSTURE,
    compare_interaction_states,
    is_social_route_key,
    parse_interaction_state_report,
)

DEFAULT_SERVER_ENDPOINT = "127.0.0.1:34029"
DEFAULT_CLIENT_ENDPOINT = "127.0.0.1:34030"
DEFAULT_PUBLIC_ENDPOINT = "127.0.0.1:34019"
PROBE_SERVER_ENDPOINT = "127.0.0.1:34031"
PROBE_CLIENT_ENDPOINT = "127.0.0.1:34032"
ARCHIVE_DIR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), ".omo", "run")
PROBE_ARCHIVE_DIR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), ".omo", "probe-run")


# ---------------------------------------------------------------------------
# Bridge connection (lightweight — no simctl dependency)
# ---------------------------------------------------------------------------

def connect(endpoint: str, timeout: float = 20.0) -> socket.socket:
    host, port = endpoint.rsplit(":", 1)
    try:
        return socket.create_connection((host, int(port)), timeout=timeout)
    except OSError:
        return None  # caller handles


def bridge_roundtrip(sock: socket.socket, line: str) -> dict:
    sock.sendall((line + "\n").encode("utf-8"))
    buf = b""
    while not buf.endswith(b"\n"):
        chunk = sock.recv(65536)
        if not chunk:
            return {"ok": False, "error": "connection closed"}
        buf += chunk
    return json.loads(buf.decode("utf-8-sig"))


# ---------------------------------------------------------------------------
# Sim discovery via DIAG_STATE + snapshot probe
# ---------------------------------------------------------------------------

def get_sim_ids_from_state(endpoint: str) -> list[dict]:
    """Get sims by requesting a snapshot through the public protocol.
    Uses the same $type + commandId shape as simctl.py."""
    import uuid as _uuid
    pub_endpoint = DEFAULT_PUBLIC_ENDPOINT
    sock = connect(pub_endpoint, timeout=30.0)
    if sock is None:
        return []
    try:
        f = sock.makefile("rwb")
        sock.settimeout(30.0)
        cid = str(_uuid.uuid4())
        # $type MUST be first key (System.Text.Json polymorphic discriminator)
        req = json.dumps({"$type": "request_snapshot", "commandId": cid,
                          "playerId": 0, "sinceTick": None}) + "\n"
        f.write(req.encode())
        f.flush()

        accepted = False
        deadline = time.time() + 30.0
        while time.time() < deadline:
            line = f.readline()
            if not line:
                break
            msg = json.loads(line.decode())
            t = msg.get("$type")
            if t == "command_accepted" and msg.get("commandId") == cid:
                accepted = True
            elif t == "command_rejected" and msg.get("commandId") == cid:
                return []
            elif t == "server_error":
                return []
            elif t == "full_snapshot" and accepted:
                sims = msg.get("snapshot", {}).get("sims", [])
                return [{"objectId": str(s["objectId"]), "name": s.get("name", "?"),
                         "owner": s.get("owningPlayerId")}
                        for s in sims]
    except (socket.timeout, json.JSONDecodeError, OSError):
        pass
    finally:
        sock.close()
    return []


# ---------------------------------------------------------------------------
# Capture + state fetch
# ---------------------------------------------------------------------------

@dataclass
class SideResult:
    ok: bool
    capture_report: str = ""
    state: Optional[DiagInteractionState] = None
    error: str = ""
    tick: int = 0

    def to_dict(self) -> dict:
        return {
            "ok": self.ok,
            "captureReport": self.capture_report,
            "state": self.state.to_dict() if self.state else None,
            "error": self.error,
            "tick": self.tick,
        }


def fetch_side(endpoint: str, sim_id: str, book_id: str | None, timeout: int | None) -> SideResult:
    """Fetch DIAG_CAPTURE_INTERACTION + DIAG_INTERACTION_STATE from one bridge endpoint."""
    sock_timeout = (timeout or 15) + 10
    sock = connect(endpoint, sock_timeout)
    if sock is None:
        return SideResult(ok=False, error=f"cannot connect to {endpoint}")

    try:
        # 1. Capture
        cap_req = f"DIAG_CAPTURE_INTERACTION {sim_id}"
        if timeout is not None:
            cap_req = f"@{timeout} {cap_req}"
        cap_resp = bridge_roundtrip(sock, cap_req)
        capture_report = ""
        if cap_resp.get("ok") and cap_resp.get("accepted") is not False:
            capture_report = cap_resp.get("report") or ""
        elif cap_resp.get("accepted") is False:
            # No interaction running — that's fine, still get state
            capture_report = ""
        else:
            return SideResult(ok=False, error=f"capture failed: {cap_resp.get('error', 'unknown')}")

        # 2. State
        state_req = f"DIAG_INTERACTION_STATE {sim_id}"
        if book_id:
            # hex-encode the book id for the wire
            state_req += " " + book_id.encode("utf-8").hex()
        if timeout is not None:
            state_req = f"@{timeout} {state_req}"
        state_resp = bridge_roundtrip(sock, state_req)
        if not state_resp.get("ok") or state_resp.get("accepted") is False:
            return SideResult(
                ok=False,
                capture_report=capture_report,
                error=f"state failed: {state_resp.get('error', state_resp.get('reason', 'unknown'))}",
            )

        state_report = state_resp.get("report") or ""
        state = parse_interaction_state_report(state_report)
        tick_str = state.field("tick", "0")
        try:
            tick = int(tick_str)
        except ValueError:
            tick = 0

        return SideResult(ok=True, capture_report=capture_report, state=state, tick=tick)
    finally:
        sock.close()


# ---------------------------------------------------------------------------
# Matrix entry
# ---------------------------------------------------------------------------

@dataclass
class MatrixEntry:
    sim_id: str
    sim_name: str
    server: SideResult
    client: SideResult
    comparison: Optional[dict] = None  # diag_state comparison.to_dict()
    capture_match: bool = False
    notes: list[str] = field(default_factory=list)
    social_route: bool = False  # host running a social-route interaction (route-only accept)

    def to_dict(self) -> dict:
        return {
            "simId": self.sim_id,
            "simName": self.sim_name,
            "server": self.server.to_dict(),
            "client": self.client.to_dict(),
            "comparison": self.comparison,
            "captureMatch": self.capture_match,
            "socialRoute": self.social_route,
            "notes": self.notes,
        }


# route-only social acceptance: capture fields that legitimately diverge when the host
# runs a social and the client only routes (GoHere). Posture fields stay strict (catch).
SOCIAL_ROUTE_DEGRADE_CAPTURE_KEYS = frozenset(
    {"interactionKey", "targetObjectId", "targetObjectType", "queueRole",
     "isTransitionInteraction", "hasTargetPosition"}
)

# directional posture-degrade (re-run 2026-06-22, owner-approved DIRECTIONAL only): posture
# capture fields that diverge during an ACTIVE host social. Degraded to an expected route-only PASS
# ONLY when the host postureType is SocializingPosture and the client's is not; the reverse (client
# wrongly in SocializingPosture) stays STRICT ( guard). These MUST NOT be in
# SOCIAL_ROUTE_DEGRADE_CAPTURE_KEYS (that degrades unconditionally in both directions).
SOCIAL_POSTURE_DEGRADE_CAPTURE_KEYS = frozenset({"postureType", "postureContainerObjectId"})


def compare_capture_reports(
    server_report: str, client_report: str, social_route: bool = False
) -> tuple[bool, list[str]]:
    """Compare key fields from two DIAG_CAPTURE_INTERACTION reports.
    Returns (match, divergences).

    When social_route (host running a SocializeWith/SocialInteractionA/B), the client legitimately
    has no matching social instance -- it routed to target and idled. interactionKey/target/queueRole
    divergence (and "client has none") is recorded as an EXPECTED route-only degrade, not a failure
    (DEV-139 owner decision A; body-sync deferred to DEV-283). Posture fields stay strict."""
    if not server_report and not client_report:
        return True, ["both empty (no interaction on either side)"]
    if not server_report:
        return False, ["server has interaction, client has none"]
    if not client_report:
        if social_route:
            return True, ["social route-only: client routed/idle, no social instance (expected, DEV-139)"]
        return False, ["client has no interaction, server has one"]

    # Parse both into key=value dicts
    def parse_kv(report: str) -> dict[str, str]:
        fields = {}
        for piece in report.split(";"):
            piece = piece.strip()
            if not piece:
                continue
            key, sep, val = piece.partition("=")
            if sep:
                fields[key.strip()] = val.strip()
        return fields

    # Hex-text fields that need decoding
    hex_fields = {"interactionKey", "interactionInstanceType", "interactionDefinitionType",
                  "targetObjectType", "postureType", "parameters", "policyHint"}

    def decode_fields(fields: dict[str, str]) -> dict[str, str]:
        decoded = {}
        for k, v in fields.items():
            if k in hex_fields and v and len(v) % 2 == 0:
                try:
                    decoded[k] = bytes.fromhex(v).decode("utf-8")
                except (ValueError, UnicodeDecodeError):
                    decoded[k] = v
            else:
                decoded[k] = v
        return decoded

    s = decode_fields(parse_kv(server_report))
    c = decode_fields(parse_kv(client_report))

    # Key fields to compare
    compare_keys = ["interactionKey", "targetObjectId", "targetObjectType",
                    "queueRole", "isTransitionInteraction", "postureType",
                    "postureContainerObjectId", "hasTargetPosition"]
    # directional posture-degrade (re-run 2026-06-22, directional only): degrade
    # postureType/postureContainerObjectId ONLY when the host is in SocializingPosture and the client
    # is not (the correct route-only state). The reverse -- client wrongly SocializingPosture -- stays
    # STRICT ( guard). Keys off the host postureType in the capture report.
    social_posture_degrade = (
        social_route
        and s.get("postureType", "") == SOCIALIZING_POSTURE
        and c.get("postureType", "") != SOCIALIZING_POSTURE
    )
    divergences = []
    match = True
    for key in compare_keys:
        sv = s.get(key, "")
        cv = c.get(key, "")
        if not sv and not cv:
            continue
        if sv != cv:
            if social_route and key in SOCIAL_ROUTE_DEGRADE_CAPTURE_KEYS:
                # expected route-only degrade -- record but do not fail
                divergences.append(f"[route-only degrade] {key}: server={sv} client={cv}")
                continue
            if social_posture_degrade and key in SOCIAL_POSTURE_DEGRADE_CAPTURE_KEYS:
                # expected route-only posture degrade ( directional) -- host socializing,
                # client route-only Standing. Reverse (client orphan posture) is NOT degraded here.
                divergences.append(f"[route-only posture degrade] {key}: server={sv} client={cv}")
                continue
            match = False
            divergences.append(f"{key}: server={sv} client={cv}")

    return match, divergences


# ---------------------------------------------------------------------------
# Matrix runner
# ---------------------------------------------------------------------------

def run_matrix(
    server_endpoint: str,
    client_endpoint: str,
    sim_ids: list[dict],
    book_id: str | None,
    progress_tolerance: float,
    timeout: int | None,
    server_only: bool,
) -> list[MatrixEntry]:
    entries: list[MatrixEntry] = []

    for sim in sim_ids:
        sim_id = sim["objectId"]
        sim_name = sim.get("name", "?")

        # Server side
        server = fetch_side(server_endpoint, sim_id, book_id, timeout)

        # route-only social acceptance keys off the HOST (authoritative) interaction key.
        social_route = bool(server.state) and is_social_route_key(server.state.field("activeInteractionKey"))

        # Client side
        if server_only:
            client = SideResult(ok=True, error="skipped (--server-only)")
            comparison = None
            capture_match = False
            notes = ["server-only mode"]
        else:
            client = fetch_side(client_endpoint, sim_id, book_id, timeout)

            # Compare states (social-route degrade applied inside compare_interaction_states)
            if server.state and client.state:
                cmp = compare_interaction_states(server.state, client.state, progress_tolerance)
                comparison = cmp.to_dict()
            else:
                comparison = None

            # Compare capture reports (route-only degrade for social keys)
            capture_match, cap_divergences = compare_capture_reports(
                server.capture_report, client.capture_report, social_route
            )

        notes = []
        if not server.ok:
            notes.append(f"server error: {server.error}")
        if not client.ok:
            notes.append(f"client error: {client.error}")
        if server_only:
            notes.append("server-only mode")
        elif social_route:
            notes.append("social route-only acceptance (DEV-139): key/queueRole divergence expected; "
                         "position via convergence assertion; body-sync deferred to DEV-283")
            if server.capture_report and client.capture_report and cap_divergences:
                notes.extend(cap_divergences)
        elif server.capture_report and not client.capture_report:
            notes.append("server has active interaction, client has none")
        elif not server.capture_report and client.capture_report:
            notes.append("client has active interaction, server has none")
        elif server.capture_report and client.capture_report and not capture_match:
            notes.extend(cap_divergences)

        entry = MatrixEntry(
            sim_id=sim_id,
            sim_name=sim_name,
            server=server,
            client=client,
            comparison=comparison if not server_only else None,
            capture_match=capture_match if not server_only else False,
            notes=notes,
            social_route=social_route,
        )
        entries.append(entry)

    return entries


def print_matrix_human(entries: list[MatrixEntry], sample_num: int | None = None) -> None:
    if sample_num is not None:
        print(f"\n{'='*60}")
        print(f"  SAMPLE {sample_num}  {time.strftime('%H:%M:%S')}")
        print(f"{'='*60}")
    else:
        print(f"\n{'='*60}")
        print(f"  REPLAY MATRIX  {time.strftime('%H:%M:%S')}")
        print(f"{'='*60}")

    total = len(entries)
    state_pass = 0
    capture_pass = 0
    both_pass = 0

    for entry in entries:
        sim_label = f"{entry.sim_name} ({entry.sim_id})"
        state_ok = entry.comparison is not None and entry.comparison.get("ok", False)
        cap_ok = entry.capture_match

        if state_ok:
            state_pass += 1
        if cap_ok:
            capture_pass += 1
        if state_ok and cap_ok:
            both_pass += 1

        # One-line verdict
        if entry.server.ok and entry.client.ok and state_ok and cap_ok:
            status = "PASS (route-only)" if entry.social_route else "PASS"
        elif entry.server.ok and not entry.client.ok:
            status = "SKIP (client unavailable)"
        elif not entry.server.ok:
            status = "FAIL (server error)"
        else:
            status = "FAIL"

        print(f"\n  {status}  {sim_label}")
        print(f"    server: tick={entry.server.tick}  interaction={'yes' if entry.server.capture_report else 'none'}  posture={entry.server.state.field('postureType', '-') if entry.server.state else '-'}")
        if not entry.client.error.startswith("skipped"):
            print(f"    client: tick={entry.client.tick}  interaction={'yes' if entry.client.capture_report else 'none'}  posture={entry.client.state.field('postureType', '-') if entry.client.state else '-'}")
        else:
            print(f"    client: {entry.client.error}")

        if entry.comparison:
            for check in entry.comparison.get("checks", []):
                flag = "OK" if check.get("ok") else "FAIL"
                print(f"    {flag:4} {check['field']}: srv={check['server']} cli={check['client']}")
            if entry.comparison.get("progressField"):
                print(f"    progress: {entry.comparison['progressField']} delta={entry.comparison.get('progressDelta')}")

        for note in entry.notes:
            print(f"    note: {note}")

    print(f"\n{'-'*60}")
    print(f"  SUMMARY: {total} sims  state-compare: {state_pass}/{total}  capture-match: {capture_pass}/{total}  both: {both_pass}/{total}")
    print(f"{'-'*60}")


def print_matrix_json(entries: list[MatrixEntry], sample_num: int | None = None) -> None:
    output = {
        "timestamp": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "sample": sample_num,
        "total": len(entries),
        "statePass": sum(1 for e in entries if e.comparison and e.comparison.get("ok")),
        "capturePass": sum(1 for e in entries if e.capture_match),
        "entries": [e.to_dict() for e in entries],
    }
    print(json.dumps(output, indent=2))


def archive_matrix(entries: list[MatrixEntry], sample_num: int | None = None, archive_dir: str = ARCHIVE_DIR) -> str:
    """Write JSON evidence to archive_dir/replay-matrix-<timestamp>.json"""
    os.makedirs(archive_dir, exist_ok=True)
    ts = time.strftime("%Y%m%d-%H%M%S")
    path = os.path.join(archive_dir, f"replay-matrix-{ts}.json")
    output = {
        "timestamp": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "sample": sample_num,
        "total": len(entries),
        "statePass": sum(1 for e in entries if e.comparison and e.comparison.get("ok")),
        "capturePass": sum(1 for e in entries if e.capture_match),
        "entries": [e.to_dict() for e in entries],
    }
    with open(path, "w", encoding="utf-8") as f:
        json.dump(output, f, indent=2)
    return path


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def main() -> int:
    parser = argparse.ArgumentParser(
        description="DEV-137 interaction replay matrix harness",
        add_help=True,
    )
    parser.add_argument("--profile", choices=("dev", "probe"), default="dev",
                        help="endpoint preset: dev=34029/34030 (default), probe=34031/34032")
    parser.add_argument("--server-endpoint", default=None,
                        help="server bridgectl endpoint (default by --profile)")
    parser.add_argument("--client-endpoint", default=None,
                        help="client bridgectl endpoint (default by --profile)")
    parser.add_argument("--sim", action="append", default=[],
                        help="Sim objectId or name substring (repeatable; default: all)")
    parser.add_argument("--book-id", default=None,
                        help="book data id to watch for progress comparison")
    parser.add_argument("--progress-tolerance", type=float, default=0.05,
                        help="tolerance for progress normalized float (default 0.05)")
    parser.add_argument("--timeout", type=int, default=None,
                        help="bridge command timeout seconds")
    parser.add_argument("--server-only", action="store_true",
                        help="skip client side (server-only validation)")
    parser.add_argument("--poll", type=int, default=1,
                        help="number of samples to take (default 1; >1 waits 2s between)")
    parser.add_argument("--interval", type=float, default=2.0,
                        help="seconds between poll samples (default 2.0)")
    parser.add_argument("--json", action="store_true",
                        help="emit machine-readable JSON")
    parser.add_argument("--archive", action="store_true",
                        help="write JSON evidence to .omo/run/")
    args = parser.parse_args()

    if args.profile == "probe":
        if args.server_endpoint is None:
            args.server_endpoint = PROBE_SERVER_ENDPOINT
        if args.client_endpoint is None:
            args.client_endpoint = PROBE_CLIENT_ENDPOINT
        archive_base = PROBE_ARCHIVE_DIR
    else:
        if args.server_endpoint is None:
            args.server_endpoint = DEFAULT_SERVER_ENDPOINT
        if args.client_endpoint is None:
            args.client_endpoint = DEFAULT_CLIENT_ENDPOINT
        archive_base = ARCHIVE_DIR

    # Discover sims
    if args.sim:
        # Resolve sim ids from arguments
        sim_ids = []
        for tok in args.sim:
            if tok.isdigit():
                sim_ids.append({"objectId": tok, "name": f"(id {tok})"})
            else:
                # Name match — try to get snapshot
                all_sims = get_sim_ids_from_state(args.server_endpoint)
                matches = [s for s in all_sims if tok.lower() in s.get("name", "").lower()]
                if not matches:
                    print(f"no Sim matches '{tok}'", file=sys.stderr)
                    return 1
                sim_ids.extend(matches)
        if not sim_ids:
            print("no sims resolved from --sim arguments", file=sys.stderr)
            return 1
    else:
        # Auto-discover all sims
        sim_ids = get_sim_ids_from_state(args.server_endpoint)
        if not sim_ids:
            print("could not get sim list (public listener down? try --sim <objectId>)",
                  file=sys.stderr)
            return 1

    all_entries: list[list[MatrixEntry]] = []

    for sample in range(1, args.poll + 1):
        entries = run_matrix(
            server_endpoint=args.server_endpoint,
            client_endpoint=args.client_endpoint,
            sim_ids=sim_ids,
            book_id=args.book_id,
            progress_tolerance=args.progress_tolerance,
            timeout=args.timeout,
            server_only=args.server_only,
        )
        all_entries.append(entries)

        if args.json:
            print_matrix_json(entries, sample if args.poll > 1 else None)
        else:
            print_matrix_human(entries, sample if args.poll > 1 else None)

        if args.archive:
            path = archive_matrix(entries, sample if args.poll > 1 else None, archive_base)
            if not args.json:
                print(f"  archived: {path}")

        if sample < args.poll:
            time.sleep(args.interval)

    # Final summary for multi-sample
    if args.poll > 1 and not args.json:
        print(f"\n{'='*60}")
        print(f"  MULTI-SAMPLE SUMMARY  {args.poll} samples")
        print(f"{'='*60}")
        for i, entries in enumerate(all_entries, 1):
            sp = sum(1 for e in entries if e.comparison and e.comparison.get("ok"))
            cp = sum(1 for e in entries if e.capture_match)
            print(f"  sample {i}: state-compare {sp}/{len(entries)}  capture-match {cp}/{len(entries)}")

    return 0


if __name__ == "__main__":
    sys.exit(main())