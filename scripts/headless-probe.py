#!/usr/bin/env python3
"""headless-probe — DEV-110 T7: server-TS3 tick-rate measurement across window states.

Measures whether TS3 keeps simulating when its window is minimized / hidden / offscreen —
the "TS3 needs a visible desktop" assumption has been a High/High planning risk since M1
and has never had data. Samples DIAG_STATE over the bridgectl admin listener and computes
TWO rates per window state, because "sim clock paused" and "bridge pump dead" are distinct:

  sim ticks/sec   from tick=     (Simulator.TicksElapsed — does the WORLD advance?)
  pump ticks/sec  from pumpTicks= (BridgeAlarmTick iterations — is the BRIDGE alive?)

  python scripts/headless-probe.py                        full state matrix (~4 min)
  python scripts/headless-probe.py --duration 20          shorter sampling window per state
  python scripts/headless-probe.py --states visible,minimized,restored

Needs: server up with --bridgectl-listen (dev-session enables it), the DEV-110 mod build
(DIAG_STATE reports pumpTicks=), and a loaded save at speed Normal. Server-only stack
recommended (`stackctl.py server`) so a client TS3 doesn't contend for the GPU.
The window is restored to its original placement on exit, even on Ctrl+C.
"""
import argparse
import ctypes
import datetime as dt
import json
import os
import re
import sys
import time
from ctypes import wintypes

SCRIPTS = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(SCRIPTS)
sys.path.insert(0, SCRIPTS)
import bridgectl  # connect/roundtrip to the admin listener  # noqa: E402

RUN_DIR = os.path.join(REPO, ".omo", "run")
PID_FILE = os.path.join(RUN_DIR, "server-ts3.pid")

user32 = ctypes.windll.user32 if sys.platform == "win32" else None  # checked in main()
SW_HIDE, SW_SHOW, SW_MINIMIZE, SW_RESTORE = 0, 5, 6, 9
SWP_NOSIZE, SWP_NOZORDER, SWP_NOACTIVATE = 0x0001, 0x0004, 0x0010
OFFSCREEN_XY = -32000

ALL_STATES = ["visible", "minimized", "hidden", "offscreen", "restored"]


# ---------------------------------------------------------------- win32

def find_main_window(pid):
    """Largest titled top-level window owned by pid (TS3 has exactly one real window)."""
    hits = []
    proc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def cb(hwnd, _):
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value != pid or not user32.IsWindowVisible(hwnd):
            return True
        buf = ctypes.create_unicode_buffer(256)
        user32.GetWindowTextW(hwnd, buf, 256)
        if not buf.value:
            return True
        rect = wintypes.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(rect))
        area = max(0, rect.right - rect.left) * max(0, rect.bottom - rect.top)
        hits.append((area, hwnd, buf.value, (rect.left, rect.top, rect.right, rect.bottom)))
        return True

    user32.EnumWindows(proc(cb), 0)
    if not hits:
        return None
    hits.sort(reverse=True)
    return hits[0][1:]  # (hwnd, title, rect)


def apply_state(hwnd, state, orig_rect):
    if state in ("visible", "restored"):
        user32.ShowWindow(hwnd, SW_SHOW)
        user32.ShowWindow(hwnd, SW_RESTORE)
        user32.SetWindowPos(hwnd, 0, orig_rect[0], orig_rect[1], 0, 0,
                            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE)
    elif state == "minimized":
        user32.ShowWindow(hwnd, SW_MINIMIZE)
    elif state == "hidden":
        user32.ShowWindow(hwnd, SW_HIDE)
    elif state == "offscreen":
        user32.ShowWindow(hwnd, SW_SHOW)
        user32.ShowWindow(hwnd, SW_RESTORE)
        user32.SetWindowPos(hwnd, 0, OFFSCREEN_XY, OFFSCREEN_XY, 0, 0,
                            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE)
    else:
        raise ValueError(state)


def window_flags(hwnd):
    return {
        "visible": bool(user32.IsWindowVisible(hwnd)),
        "iconic": bool(user32.IsIconic(hwnd)),
        "foreground": user32.GetForegroundWindow() == hwnd,
    }


# ---------------------------------------------------------------- sampling

def sample(sock, per_sample_timeout):
    """One DIAG_STATE round-trip -> (wall_seconds, sim_tick, pump_ticks) or None on timeout."""
    started = time.monotonic()
    try:
        response = bridgectl.roundtrip(sock, f"@{per_sample_timeout} DIAG_STATE")
    except SystemExit:
        raise
    except OSError:
        return None
    if not response.get("ok") or not response.get("accepted"):
        return None
    report = response.get("report") or ""
    tick = re.search(r"\btick=(\d+)", report)
    pump = re.search(r"pumpTicks=(\d+)", report)
    if not tick or not pump:
        sys.exit(f"DIAG_STATE report lacks tick/pumpTicks — is the DEV-110 mod build installed? report: {report}")
    return (started, int(tick.group(1)), int(pump.group(1)))


def measure_state(endpoint, duration, interval, per_sample_timeout):
    samples, timeouts, consecutive = [], 0, 0
    sock = bridgectl.connect(endpoint, per_sample_timeout + 10)
    try:
        deadline = time.monotonic() + duration
        while time.monotonic() < deadline:
            got = sample(sock, per_sample_timeout)
            if got is None:
                timeouts += 1
                consecutive += 1
                # Socket state after a timeout is unknowable (a late reply would desync
                # every later sample) — reconnect.
                try:
                    sock.close()
                except OSError:
                    pass
                sock = bridgectl.connect(endpoint, per_sample_timeout + 10)
                if consecutive >= 3:
                    break  # pump effectively dead in this state; stop burning the window
            else:
                samples.append(got)
                consecutive = 0
            time.sleep(interval)
    finally:
        try:
            sock.close()
        except OSError:
            pass
    return samples, timeouts


def rates(samples):
    if len(samples) < 2:
        return None, None
    wall = samples[-1][0] - samples[0][0]
    if wall <= 0:
        return None, None
    return ((samples[-1][1] - samples[0][1]) / wall,
            (samples[-1][2] - samples[0][2]) / wall)


def ensure_sim_clock_running(endpoint, per_sample_timeout):
    """Pin speed Normal and verify the sim clock actually advances (run 1 lesson: the world
    can silently re-pause after serve-mode's start-speed write — a paused baseline makes the
    whole matrix meaningless)."""
    sock = bridgectl.connect(endpoint, per_sample_timeout + 10)
    try:
        response = bridgectl.roundtrip(sock, f"@{per_sample_timeout} SET_GAME_SPEED Normal")
        if not response.get("ok") or not response.get("accepted"):
            sys.exit(f"preflight SET_GAME_SPEED Normal failed: {response.get('error') or response.get('reason')}")
        first = sample(sock, per_sample_timeout)
        time.sleep(3)
        second = sample(sock, per_sample_timeout)
    finally:
        try:
            sock.close()
        except OSError:
            pass
    if first is None or second is None:
        sys.exit("preflight DIAG_STATE timed out — bridge not healthy, aborting")
    delta = second[1] - first[1]
    if delta <= 0:
        sys.exit(f"sim clock NOT advancing after SET_GAME_SPEED Normal (tick stuck at {second[1]}) — "
                 "the world is paused (in-game dialog? pause-on-focus-loss?). Unpause and re-run.")
    print(f"preflight: sim clock advancing ({delta} ticks / 3s) at speed Normal\n")


# ---------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(prog="headless-probe", description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--endpoint", default=bridgectl.DEFAULT_ENDPOINT)
    ap.add_argument("--pid", type=int, help="server TS3 pid (default: .omo/run/server-ts3.pid)")
    ap.add_argument("--duration", type=float, default=30.0, help="sampling seconds per state")
    ap.add_argument("--interval", type=float, default=2.0, help="seconds between samples")
    ap.add_argument("--sample-timeout", type=int, default=15, help="per-DIAG_STATE deadline (s)")
    ap.add_argument("--settle", type=float, default=5.0, help="seconds after a state change before sampling")
    ap.add_argument("--states", default=",".join(ALL_STATES))
    args = ap.parse_args()
    if user32 is None:
        raise SystemExit("headless-probe.py needs Windows: it moves the TS3 window through user32.")

    states = [s.strip() for s in args.states.split(",") if s.strip()]
    unknown = [s for s in states if s not in ALL_STATES]
    if unknown:
        sys.exit(f"unknown state(s) {unknown}; have {ALL_STATES}")

    pid = args.pid
    if pid is None:
        if not os.path.exists(PID_FILE):
            sys.exit(f"no {PID_FILE} — is the server stack up? (stackctl.py server)")
        pid = int(open(PID_FILE, encoding="utf-8").read().strip())

    found = find_main_window(pid)
    if not found:
        sys.exit(f"no visible titled window for TS3 pid {pid}")
    hwnd, title, orig_rect = found
    print(f"server TS3 pid={pid} window='{title}' rect={orig_rect}")
    ensure_sim_clock_running(args.endpoint, args.sample_timeout)
    print(f"states: {states}  ({args.duration:.0f}s @ {args.interval:.0f}s cadence each, settle {args.settle:.0f}s)\n")

    results = []
    try:
        for state in states:
            apply_state(hwnd, state, orig_rect)
            time.sleep(args.settle)
            flags = window_flags(hwnd)
            print(f"[{state}] flags={flags} sampling...")
            samples, timeouts = measure_state(args.endpoint, args.duration, args.interval, args.sample_timeout)
            sim_rate, pump_rate = rates(samples)
            results.append({
                "state": state, "flags": flags, "samples": len(samples), "timeouts": timeouts,
                "sim_ticks_per_sec": sim_rate, "pump_ticks_per_sec": pump_rate,
                "raw": [{"t": round(s[0], 3), "tick": s[1], "pump": s[2]} for s in samples],
            })
            sim_text = f"{sim_rate:8.2f}" if sim_rate is not None else "       ?"
            pump_text = f"{pump_rate:6.2f}" if pump_rate is not None else "     ?"
            print(f"[{state}] sim={sim_text} ticks/s  pump={pump_text} ticks/s  "
                  f"samples={len(samples)} timeouts={timeouts}\n")
    finally:
        apply_state(hwnd, "restored", orig_rect)
        print("window restored to original placement")

    baseline = next((r for r in results if r["state"] == "visible" and r["sim_ticks_per_sec"]), None)
    print("\nstate       sim t/s   pump t/s   vs baseline   samples  timeouts")
    for r in results:
        sim, pump = r["sim_ticks_per_sec"], r["pump_ticks_per_sec"]
        rel = (f"{100 * sim / baseline['sim_ticks_per_sec']:9.1f}%"
               if baseline and sim is not None and baseline["sim_ticks_per_sec"] else "        --")
        print(f"{r['state']:<10}{(f'{sim:9.2f}' if sim is not None else '        ?')}"
              f"{(f'{pump:11.2f}' if pump is not None else '          ?')}"
              f"  {rel}{r['samples']:10d}{r['timeouts']:10d}")

    stamp = dt.datetime.now().strftime("%Y-%m-%d-%H%M%S")
    dest = os.path.join(RUN_DIR, "archive", f"{stamp}-headless-probe")
    os.makedirs(dest, exist_ok=True)
    out = os.path.join(dest, "results.json")
    with open(out, "w", encoding="utf-8") as handle:
        json.dump({"pid": pid, "window": title, "started": stamp,
                   "duration_per_state": args.duration, "interval": args.interval,
                   "results": results}, handle, indent=2)
    print(f"\nevidence: {os.path.relpath(out, REPO)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
