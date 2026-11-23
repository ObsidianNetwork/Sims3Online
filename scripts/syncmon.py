#!/usr/bin/env python3
"""syncmon — live desync dashboard for the SimsOnline two-TS3 sync system (T12 / DEV-117).

The interactive twin of UT-1's `convergence` assertion (DEV-123): it surfaces, in real time,
how far the CLIENT mirror has drifted from the authoritative HOST while the P5 sync system
(DEV-113: SyncBroadcaster periodic SnapshotDelta + client ack/watermark) runs. Read-only, never
joins a session (disconnect-safe like simctl) — it only reads logs and, optionally, probes the
client mod's admin port.

Two layers:

  PROTOCOL MODE (default, ZERO mod interaction — pure log tail):
    - server.log `[sync] snapshot_delta broadcast tick=.. sims=.. objects=.. ack_min=.. lag=..`
      (SyncBroadcaster.cs:159) -> headline metrics: server tick, slowest client ack, lag,
      watermark gap, entity counts.
    - client.log last received `snapshot_delta` JSON -> per-Sim SERVER-AUTHORITATIVE positions
      (what the server SENT) + speed.
    - client.log `snapshot_ack_sent` -> the client's own view of its last ack tick.
    This shows PROTOCOL-LEVEL agreement (did the stream flow, how far behind is the ack), NOT
    the client's rendered world — the client applies positions silently with no per-Sim log line.

  --client-snapshot (stretch, needs T10's client admin port 34030):
    Probes PROBE_SIM_STATUS on the client mod for each Sim the server last broadcast, giving the
    client's ACTUAL rendered position + autonomy flag. Now the drift column is the REAL host-vs-
    client-rendered distance (the same comparison UT-1 convergence makes), and a claimed Sim
    running its own autonomy on the client is flagged. Heavy-ish (one probe per Sim), so it runs
    on a slower cadence than the ~1.5s log repaint.

Usage:
  python scripts/syncmon.py                          # protocol mode, repaint ~1.5s
  python scripts/syncmon.py --client-snapshot        # + true client-rendered drift via :34030
  python scripts/syncmon.py --interval 2 --once       # single snapshot, no loop (for piping)
  python scripts/syncmon.py --metrics-json            # also append NDJSON to .omo/run/syncmon-metrics.ndjson

Stdlib only. Matches simctl/logq conventions.
"""
import argparse
import json
import math
import os
import re
import sys
import time

SCRIPTS = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(SCRIPTS)
sys.path.insert(0, SCRIPTS)

RUN_DIR = os.path.join(REPO, ".omo", "run")
SERVER_LOG = os.path.join(RUN_DIR, "server.log")
CLIENT_LOG = os.path.join(RUN_DIR, "client.log")
METRICS_PATH = os.path.join(RUN_DIR, "syncmon-metrics.ndjson")

# Mirror DriftDetector.DefaultThreshold (DedicatedServer/Mirroring/DriftDetector.cs:14) — the same
# 5.0 world-unit bar the live client uses to decide a position jump is a missed delta.
DRIFT_THRESHOLD = 5.0

RE_SYNC = re.compile(
    r"\[sync\] snapshot_delta broadcast tick=(\d+) sims=(\d+) objects=(\d+)"
    r"(?: ack_min=(-?\d+) lag=(-?\d+))?")


# ----------------------------------------------------------------- log parsing

def tail_lines(path, want_lines=400, block=1 << 20, cap_bytes=64 << 20):
    """Return roughly the last `want_lines` COMPLETE lines of a log, reading backward in blocks.

    A fixed byte-window tail is wrong here: a single full-world `snapshot_delta` line is ~2 MB
    (the un-interest-scoped frame — the very collapse this tool watches), so a small window can
    land entirely inside one line and miss every marker. Reading backward by line count is robust
    to giant lines; `cap_bytes` bounds the worst case (many huge frames) so we never slurp 355 MB.
    """
    try:
        with open(path, "rb") as fh:
            fh.seek(0, os.SEEK_END)
            end = fh.tell()
            pos = end
            data = b""
            newlines = 0
            while pos > 0 and newlines <= want_lines and (end - pos) < cap_bytes:
                step = min(block, pos)
                pos -= step
                fh.seek(pos)
                chunk = fh.read(step)
                data = chunk + data
                newlines += chunk.count(b"\n")
            text = data.decode("utf-8", errors="replace")
            # keep only complete lines (drop a leading partial unless we reached file start)
            lines = text.splitlines()
            if pos > 0 and lines:
                lines = lines[1:]
            return lines[-want_lines:]
    except OSError:
        return []


def last_sync_line(server_lines):
    """Most recent [sync] broadcast metrics, or None."""
    last = None
    for line in server_lines:
        m = RE_SYNC.search(line)
        if m:
            last = m
    if not last:
        return None
    tick, sims, objects, ack_min, lag = last.groups()
    return {
        "tick": int(tick), "sims": int(sims), "objects": int(objects),
        "ack_min": int(ack_min) if ack_min is not None else None,
        "lag": int(lag) if lag is not None else None,
    }


def last_received_snapshot(client_lines):
    """Parse the last `snapshot_delta` (or `full_snapshot`) frame received in client.log into the
    server-authoritative {tick, speed, sims:{id->{name,x,y,z,key}}}. Returns None if none seen."""
    snap = None
    for line in client_lines:
        i = line.find('"$type":"snapshot_delta"')
        if i == -1:
            i = line.find('"$type":"full_snapshot"')
        if i == -1:
            continue
        # the received payload starts at the first '{' after 'received:'
        recv = line.find("received:")
        start = line.find("{", recv if recv != -1 else 0)
        if start == -1:
            continue
        try:
            payload = json.loads(line[start:])
        except ValueError:
            continue
        snap = payload.get("snapshot", payload)
    if not snap:
        return None
    sims = {}
    for s in snap.get("sims", []):
        p = s.get("position") or {}
        sims[str(s["objectId"])] = {
            "name": s.get("name", "?"),
            "x": p.get("x"), "y": p.get("y"), "z": p.get("z"),
            "key": s.get("currentInteractionKey"),
            "owner": s.get("owningPlayerId"),
        }
    return {"tick": snap.get("tick"), "speed": snap.get("speed"), "sims": sims}


def last_client_ack(client_lines):
    """The client's own last `snapshot_ack_sent` tick (its view, vs the server's ack_min)."""
    last = None
    for line in client_lines:
        if "snapshot_ack_sent" not in line:
            continue
        start = line.find("{")
        if start == -1:
            continue
        try:
            last = json.loads(line[start:]).get("tick")
        except ValueError:
            pass
    return last


def last_client_speed(client_lines):
    last = None
    for line in client_lines:
        i = line.find('"$type":"game_speed_changed"')
        if i == -1:
            continue
        start = line.find("{")
        try:
            last = json.loads(line[start:]).get("speed")
        except ValueError:
            pass
    return last


# ------------------------------------------------------ client-mirror probing

def probe_client_positions(client_endpoint, sim_ids, timeout=15):
    """Stretch mode: the client's ACTUAL rendered position + autonomy per Sim, via PROBE_SIM_STATUS
    on the client admin port (34030, from T10). Returns {id->{x,y,z,autonomy}}; best-effort."""
    try:
        import bridgectl
    except ImportError:
        return {}
    out = {}
    try:
        sock = bridgectl.connect(client_endpoint, float(timeout))
    except SystemExit:
        return {}
    try:
        for sid in sim_ids:
            try:
                resp = bridgectl.roundtrip(sock, f"@{int(timeout)} PROBE_SIM_STATUS {sid}")
            except (OSError, SystemExit):
                continue
            if not (resp.get("ok") and resp.get("accepted")):
                continue
            fields = dict(p.split("=", 1) for p in (resp.get("report") or "").split(";") if "=" in p)
            try:
                out[str(sid)] = {
                    "x": float(fields["x"]), "y": float(fields["y"]), "z": float(fields["z"]),
                    "autonomy": fields.get("autonomyEnabled", "").lower() == "true",
                }
            except (KeyError, ValueError):
                continue
    finally:
        try:
            sock.close()
        except OSError:
            pass
    return out


def dist(a, b):
    return math.sqrt((a["x"] - b["x"]) ** 2 + (a["y"] - b["y"]) ** 2 + (a["z"] - b["z"]) ** 2)


# ----------------------------------------------------------------- rendering

def short_key(key):
    if not key:
        return "-"
    tail = key.rsplit(".", 1)[-1]
    return tail[:24]


def build_view(client_endpoint, want_client_snapshot, client_cache):
    server_lines = tail_lines(SERVER_LOG, want_lines=200)
    client_lines = tail_lines(CLIENT_LOG, want_lines=400)
    sync = last_sync_line(server_lines)
    srv_snap = last_received_snapshot(client_lines)
    cli_ack = last_client_ack(client_lines)
    cli_speed = last_client_speed(client_lines)

    rows = []
    drift_max = 0.0
    drifted = []
    if srv_snap:
        sims = srv_snap["sims"]
        # Refresh the (slow) client probe only when asked and on its own cadence (handled by caller
        # via client_cache['data']). Compare server-authoritative pos vs client-rendered pos.
        client_pos = client_cache.get("data", {}) if want_client_snapshot else {}
        # Claimed Sims first, then by drift desc; cap rows so the table stays readable.
        ordered = sorted(sims.items(),
                         key=lambda kv: (kv[1].get("owner") is None, kv[0]))
        for sid, s in ordered:
            cp = client_pos.get(sid)
            d = dist(s, cp) if (cp and s["x"] is not None) else None
            if d is not None:
                drift_max = max(drift_max, d)
                if d > DRIFT_THRESHOLD:
                    drifted.append(sid)
            rows.append({
                "id": sid, "name": s["name"], "owner": s.get("owner"),
                "sx": s["x"], "sz": s["z"], "key": s.get("key"),
                "drift": d, "autonomy": cp.get("autonomy") if cp else None,
            })
    return {
        "sync": sync, "srv_snap": srv_snap, "cli_ack": cli_ack, "cli_speed": cli_speed,
        "rows": rows, "drift_max": drift_max, "drifted": drifted,
    }


def render(view, want_client_snapshot, client_age):
    lines = []
    sync = view["sync"]
    srv = view["srv_snap"]
    if sync:
        wm_gap = (sync["tick"] - sync["ack_min"]) if sync["ack_min"] is not None else None
        lag = sync["lag"]
        lines.append(
            f"server tick={sync['tick']:<8} client ack_min={fmt(sync['ack_min']):<8} "
            f"watermark gap={fmt(wm_gap):<8} lag={fmt(lag)}")
        lines.append(
            f"broadcast: sims={sync['sims']} objects={sync['objects']}"
            + (f"   speed srv={srv['speed']}" if srv else "")
            + (f" cli={view['cli_speed']}" if view['cli_speed'] else ""))
        if lag is not None and lag > 1000:
            lines.append(f"  ** LAG {lag} ticks behind — client is NOT keeping up (see DEV-124 transport / DEV-114 sync) **")
    else:
        lines.append("no [sync] broadcast line in server.log yet "
                     "(is the stack up with a joined client and --sync-broadcast-seconds set?)")
    lines.append(f"client own last ack tick={fmt(view['cli_ack'])}")
    lines.append("")

    # table
    if want_client_snapshot:
        hdr = f"{'SIM':<22}{'OWNER':<7}{'SERVER (x,z)':<22}{'CLIENT drift':<16}{'INTERACTION':<26}"
    else:
        hdr = f"{'SIM':<22}{'OWNER':<7}{'SERVER (x,z)':<22}{'INTERACTION':<26}"
    lines.append(hdr)
    lines.append("-" * len(hdr))
    for r in view["rows"][:24]:
        owner = "P" + str(r["owner"]) if r["owner"] is not None else "-"
        srvpos = f"({r['sx']:.0f},{r['sz']:.0f})" if r["sx"] is not None else "-"
        name = (r["name"] or "?")[:20]
        if want_client_snapshot:
            if r["drift"] is None:
                drift_cell = "no client"
            else:
                # autonomy is only a problem on a CLAIMED Sim — an unclaimed townie running its
                # own AI on the client is correct. Flag A! only when owned + autonomy enabled.
                flag = " A!" if (r["owner"] is not None and r["autonomy"]) else ""
                mark = " DRIFT" if r["drift"] > DRIFT_THRESHOLD else ""
                drift_cell = f"{r['drift']:.1f}m{mark}{flag}"
            lines.append(f"{name:<22}{owner:<7}{srvpos:<22}{drift_cell:<16}{short_key(r['key']):<26}")
        else:
            lines.append(f"{name:<22}{owner:<7}{srvpos:<22}{short_key(r['key']):<26}")

    if want_client_snapshot:
        lines.append("")
        if view["rows"] and any(r["drift"] is not None for r in view["rows"]):
            lines.append(f"worst host/client drift = {view['drift_max']:.1f}m "
                         f"(threshold {DRIFT_THRESHOLD}m); drifted Sims: {len(view['drifted'])}")
        else:
            lines.append(f"client-snapshot pending (refreshes every probe cycle; age={fmt(client_age)}s)")
        lines.append("  A! = claimed Sim running its OWN autonomy on the client (mirror not authoritative)")
    else:
        lines.append("")
        lines.append("protocol mode: SERVER-authoritative positions shown (what the server broadcast).")
        lines.append("  Run with --client-snapshot for true host-vs-client-rendered drift (needs :34030).")
    return "\n".join(lines)


def fmt(v):
    return "n/a" if v is None else str(v)


def append_metrics(view):
    sync = view["sync"] or {}
    rec = {
        "server_tick": sync.get("tick"), "ack_min": sync.get("ack_min"), "lag": sync.get("lag"),
        "watermark_gap": (sync["tick"] - sync["ack_min"]) if sync.get("ack_min") is not None else None,
        "sims": sync.get("sims"), "objects": sync.get("objects"),
        "client_ack": view["cli_ack"], "speed_srv": (view["srv_snap"] or {}).get("speed"),
        "speed_cli": view["cli_speed"], "drift_max": round(view["drift_max"], 2),
        "drifted": len(view["drifted"]),
    }
    try:
        with open(METRICS_PATH, "a", encoding="utf-8") as fh:
            fh.write(json.dumps(rec) + "\n")
    except OSError:
        pass


def main():
    ap = argparse.ArgumentParser(prog="syncmon", description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--interval", type=float, default=1.5, help="repaint interval seconds (default 1.5)")
    ap.add_argument("--client-snapshot", action="store_true",
                    help="probe the client mod (:34030) for true client-rendered drift")
    ap.add_argument("--client-endpoint", default="127.0.0.1:34030")
    ap.add_argument("--client-interval", type=float, default=20.0,
                    help="seconds between (slow) client-mirror probe cycles (default 20)")
    ap.add_argument("--metrics-json", action="store_true",
                    help="append one NDJSON line per repaint to .omo/run/syncmon-metrics.ndjson")
    ap.add_argument("--once", action="store_true", help="render a single frame and exit")
    args = ap.parse_args()

    client_cache = {"data": {}, "at": 0.0}
    try:
        while True:
            now = time.time()
            # refresh the slow client probe on its own cadence, off the fast repaint loop
            if args.client_snapshot:
                srv_snap = last_received_snapshot(tail_lines(CLIENT_LOG, want_lines=400))
                if srv_snap and (now - client_cache["at"] >= args.client_interval or not client_cache["data"]):
                    client_cache["data"] = probe_client_positions(args.client_endpoint, list(srv_snap["sims"].keys()))
                    client_cache["at"] = now
            view = build_view(args.client_endpoint, args.client_snapshot, client_cache)
            if args.metrics_json:
                append_metrics(view)
            frame = render(view, args.client_snapshot, int(now - client_cache["at"]) if args.client_snapshot else None)
            if args.once:
                print(frame)
                return 0
            # repaint: clear + reprint (no curses dependency, like simctl watch)
            os.system("cls" if os.name == "nt" else "clear")
            print(f"=== syncmon (T12) — {time.strftime('%H:%M:%S')} — Ctrl+C to stop ===\n")
            print(frame)
            time.sleep(args.interval)
    except KeyboardInterrupt:
        return 0


if __name__ == "__main__":
    main()
