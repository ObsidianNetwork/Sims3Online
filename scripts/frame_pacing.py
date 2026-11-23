#!/usr/bin/env python3
"""Does each TS3W cost the desktop frame pacing? Sample the compositor and the GPU once a second, read-only.

    python scripts/frame_pacing.py run [--seconds N] [--label NAME] [--interval 1.0] [--quiet]
    python scripts/frame_pacing.py report FILE [FILE ...]      # one summary per log, side by side
    python scripts/frame_pacing.py counts FILE [FILE ...]      # rows grouped by TS3W count (logs merged)
    python scripts/frame_pacing.py games FILE [FILE ...]       # one row per TS3W pid: window state and size, its GPU use

Sits beside scripts/hitch_watch.py (CPU queue, wake latency, process starts); this one watches what the user sees:
the Desktop Window Manager (DWM) and the GPU. `run` needs no admin and changes nothing (no process is touched,
only read: dwmapi, performance counters, the process list). It never launches, kills or suspends a game. Every
second it appends one JSON line to `<run dir>/frame-pacing/<label>-<UTC stamp>.jsonl` (run dir: SIMSONLINE_RUN_DIR,
else the main checkout's .omo/run):

    t, time, dt, n_ts3w (TS3W.exe processes alive),
    flush  {n, p50, p95, max, slow, stall}: a thread calls DwmFlush() in a loop; each return is one compositor present
           as a client sees it. n = presents this second, p50/p95/max = ms between returns, slow = intervals
           >= 33.3 ms (two 60 Hz frames), stall = intervals >= 50 ms. A present is not due while nothing on screen
           changes, so a quiet desktop shows few; compare logs at the same activity, and read stalls with the
           present rate beside them.
    dwm    {frames, refreshes, late, missed, dropped, outstanding}: deltas of DwmGetCompositionTimingInfo (frames
           composed, display refreshes, frames DWM reports late / missed / dropped; outstanding is the current queue).
           With a NULL window (all this tool can pass; another process's window returned nothing) frames and refreshes
           advance by exactly 1 a second whatever the load (2026-11-10, 144 Hz, 0 and 2 TS3W), and late / missed /
           dropped stayed 0: read them as "not measured", never as "no misses". The present cadence above is the signal.
    eng    busiest engine % per class (3D, Copy, Video, Compute, Other): the per-engine sum over every process,
           then the maximum over engines. 100 on 3D is a saturated GPU; the per-process sum can exceed it.
    adapter_ded_mb / adapter_shared_mb: GPU memory in use on the busiest adapter, all processes.
    gpu    one row per process that uses >= 1 % of an engine or >= 100 MB dedicated, plus every TS3W and dwm.exe:
           pid, name, 3D, copy, video, other (percent) and ded_mb / shared_mb (that process's GPU memory). A TS3W row
           also has win (its Canvas window: visible, minimized, hidden or none), fg (it has the keyboard
           focus) and w / h (the window's size in pixels), read with EnumWindows; nothing is sent to the window.

Analysis (`summarize`, `by_ts3w_count`) works on parsed samples and is unit-tested with recorded ones; only
`Collector` touches Windows. Run it while other lanes hold their games (observe only), then read `counts`.
The report's cadence statistic is the median per-second p95, not a percentile over all present intervals.
"""
from __future__ import annotations

import argparse
import collections
import json
import os
import sys
import threading
import time
from datetime import datetime, timezone
from pathlib import Path

SLOW_MS = 33.3            # a present interval of two 60 Hz frames
STALL_MS = 50.0           # three
SATURATED = 90.0          # busiest 3D engine at or above this % is "saturated"
GPU_MIN_PCT = 1.0
GPU_MIN_DED_MB = 100.0
VIDEO_ENGINES = ("VideoDecode", "VideoEncode", "VideoProcessing", "Video")
ENGINE_CLASSES = ("3D", "Copy", "Video", "Compute", "Other")


# --- parsing (pure) -----------------------------------------------------------------------------------------------

def engine_class(engtype: str) -> str:
    """'3D', 'Copy', 'VideoDecode', 'Compute_0', 'Security' -> one of ENGINE_CLASSES."""
    base = engtype.split("_")[0]
    if base == "3D":
        return "3D"
    if base == "Copy":
        return "Copy"
    if base in VIDEO_ENGINES or base.startswith("Video"):
        return "Video"
    if base == "Compute":
        return "Compute"
    return "Other"


def parse_engine_instance(name: str) -> dict | None:
    """'pid_1234_luid_0x00000000_0x0000ABCD_phys_0_eng_2_engtype_VideoDecode' -> pid, engine key, class."""
    try:
        head, engtype = name.split("_engtype_", 1)
        pid = int(head.split("pid_", 1)[1].split("_", 1)[0])
        engine = head.split("_luid_", 1)[1]            # '0x..._0x..._phys_0_eng_2': the physical engine
    except (IndexError, ValueError):
        return None
    return {"pid": pid, "engine": engine, "engtype": engtype, "cls": engine_class(engtype)}


def parse_process_memory_instance(name: str) -> int | None:
    """'pid_1234_luid_0x00000000_0x0000ABCD_phys_0' -> 1234."""
    try:
        return int(name.split("pid_", 1)[1].split("_", 1)[0])
    except (IndexError, ValueError):
        return None


def dwm_delta(prev: dict | None, cur: dict) -> dict:
    """Per-second deltas of two DwmGetCompositionTimingInfo reads; counters that went backwards (DWM restarted) count 0."""
    out = {"outstanding": cur.get("outstanding", 0)}
    for key in ("frames", "refreshes", "late", "missed", "dropped"):
        before = (prev or {}).get(key)
        now = cur.get(key, 0)
        out[key] = max(0, now - before) if before is not None and now >= before else 0
    return out


def percentile(values: list[float], q: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, int(round(q * (len(ordered) - 1))))]


def flush_stats(intervals_ms: list[float]) -> dict:
    """The present intervals of one second as the sample's `flush` block."""
    if not intervals_ms:
        return {"n": 0, "p50": 0.0, "p95": 0.0, "max": 0.0, "slow": 0, "stall": 0}
    return {"n": len(intervals_ms), "p50": round(percentile(intervals_ms, 0.5), 1),
            "p95": round(percentile(intervals_ms, 0.95), 1), "max": round(max(intervals_ms), 1),
            "slow": sum(1 for v in intervals_ms if v >= SLOW_MS), "stall": sum(1 for v in intervals_ms if v >= STALL_MS)}


def aggregate_engines(engine_values: list[tuple[dict, float]]) -> tuple[dict, dict]:
    """(parsed instance, percent) pairs -> (busiest engine % per class, per-pid % per class).

    One physical engine's load is the sum over processes; the machine's saturation per class is its busiest engine."""
    per_engine: dict[tuple[str, str], float] = collections.defaultdict(float)
    per_pid: dict[int, dict[str, float]] = collections.defaultdict(lambda: collections.defaultdict(float))
    for inst, pct in engine_values:
        per_engine[(inst["cls"], inst["engine"])] += pct
        per_pid[inst["pid"]][inst["cls"]] += pct
    busiest = {cls: 0.0 for cls in ENGINE_CLASSES}
    for (cls, _engine), pct in per_engine.items():
        busiest[cls] = max(busiest[cls], pct)
    return {k: round(v, 1) for k, v in busiest.items()}, {pid: dict(v) for pid, v in per_pid.items()}


# --- analysis over samples (pure) ---------------------------------------------------------------------------------

def gpu_rows(sample: dict, name: str) -> list[dict]:
    return [p for p in sample.get("gpu", []) if p.get("name", "").lower() == name.lower()]


def ts3w_count(sample: dict) -> int:
    return sample.get("n_ts3w", len(gpu_rows(sample, "TS3W.exe")))


def _mean(values: list[float]) -> float:
    return sum(values) / len(values) if values else 0.0


def summarize(samples: list[dict]) -> dict:
    """Numbers for a set of seconds: compositor present cadence and stalls, DWM late/missed/dropped, GPU load and memory."""
    n = len(samples)
    seconds = sum(s.get("dt", 1.0) for s in samples) or 1.0
    flush = [s.get("flush", {}) for s in samples]
    dwm = [s.get("dwm", {}) for s in samples]
    eng = lambda cls: [s.get("eng", {}).get(cls, 0.0) for s in samples]  # noqa: E731
    ts3w_3d = [sum(p.get("3D", 0.0) for p in gpu_rows(s, "TS3W.exe")) for s in samples]
    ts3w_ded = [sum(p.get("ded_mb", 0.0) for p in gpu_rows(s, "TS3W.exe")) for s in samples]
    ts3w_shared = [sum(p.get("shared_mb", 0.0) for p in gpu_rows(s, "TS3W.exe")) for s in samples]
    counts = [ts3w_count(s) for s in samples]
    games = max(1.0, _mean([float(c) for c in counts]))
    dwm_3d = [sum(p.get("3D", 0.0) for p in gpu_rows(s, "dwm.exe")) for s in samples]
    per_min = lambda total: round(total * 60.0 / seconds, 2)  # noqa: E731
    return {
        "samples": n, "seconds": round(seconds, 1),
        "presents_per_s": round(sum(f.get("n", 0) for f in flush) / seconds, 1),
        "present_p50_ms": round(percentile([f.get("p50", 0.0) for f in flush if f.get("n")], 0.5), 1),
        "present_median_per_second_p95_ms": round(percentile([f.get("p95", 0.0) for f in flush if f.get("n")], 0.5), 1),
        "present_max_p99_ms": round(percentile([f.get("max", 0.0) for f in flush], 0.99), 1),
        "present_max_ms": round(max([f.get("max", 0.0) for f in flush], default=0.0), 1),
        "slow_per_min": per_min(sum(f.get("slow", 0) for f in flush)),
        "stall_per_min": per_min(sum(f.get("stall", 0) for f in flush)),
        "stall_seconds_pct": round(100.0 * sum(1 for f in flush if f.get("stall", 0)) / max(1, n), 1),
        "dwm_frames_per_s": round(sum(d.get("frames", 0) for d in dwm) / seconds, 1),
        "dwm_refreshes_per_s": round(sum(d.get("refreshes", 0) for d in dwm) / seconds, 1),
        "dwm_late_per_min": per_min(sum(d.get("late", 0) for d in dwm)),
        "dwm_missed_per_min": per_min(sum(d.get("missed", 0) for d in dwm)),
        "dwm_dropped_per_min": per_min(sum(d.get("dropped", 0) for d in dwm)),
        "outstanding_max": max([d.get("outstanding", 0) for d in dwm], default=0),
        "gpu3d_mean": round(_mean(eng("3D")), 1), "gpu3d_p95": round(percentile(eng("3D"), 0.95), 1),
        "gpu3d_max": round(max(eng("3D"), default=0.0), 1),
        "gpu3d_saturated_pct": round(100.0 * sum(1 for v in eng("3D") if v >= SATURATED) / max(1, n), 1),
        "gpu_copy_mean": round(_mean(eng("Copy")), 1), "gpu_video_mean": round(_mean(eng("Video")), 1),
        "ts3w_count_mean": round(_mean([float(c) for c in counts]), 2),
        "ts3w_3d_mean": round(_mean(ts3w_3d), 1), "ts3w_3d_each": round(_mean(ts3w_3d) / games, 1),
        "ts3w_ded_mb_each": round(_mean(ts3w_ded) / games), "ts3w_shared_mb_each": round(_mean(ts3w_shared) / games),
        "dwm_exe_3d_mean": round(_mean(dwm_3d), 1),
        "adapter_ded_mb_mean": round(_mean([s.get("adapter_ded_mb", 0.0) for s in samples])),
        "adapter_ded_mb_max": round(max([s.get("adapter_ded_mb", 0.0) for s in samples], default=0.0)),
        "adapter_shared_mb_mean": round(_mean([s.get("adapter_shared_mb", 0.0) for s in samples])),
    }


def by_ts3w_count(samples: list[dict]) -> list[dict]:
    """One row per TS3W count: the same numbers, over the seconds that had that many games."""
    groups: dict[int, list[dict]] = collections.defaultdict(list)
    for s in samples:
        groups[ts3w_count(s)].append(s)
    return [{"ts3w": count, **summarize(group)} for count, group in sorted(groups.items())]


def per_game(samples: list[dict]) -> list[dict]:
    """One row per TS3W process (pid) over the seconds it was seen: its window state, size and what it cost the GPU."""
    seen: dict[int, list[dict]] = collections.defaultdict(list)
    for s in samples:
        for row in gpu_rows(s, "TS3W.exe"):
            seen[row["pid"]].append(row)
    out = []
    for pid, rows in sorted(seen.items()):
        states = collections.Counter(r.get("win", "none") for r in rows)
        sizes = collections.Counter((r.get("w", 0), r.get("h", 0)) for r in rows)
        mean = lambda key: round(_mean([r.get(key, 0.0) for r in rows]), 1)  # noqa: E731
        out.append({"pid": pid, "seconds": len(rows), "win": states.most_common(1)[0][0],
                    "size": "%dx%d" % sizes.most_common(1)[0][0], "fg_pct": round(100.0 * sum(1 for r in rows if r.get("fg")) / len(rows)),
                    "gpu3d_mean": mean("3D"), "gpu3d_max": round(max(r.get("3D", 0.0) for r in rows), 1),
                    "copy_mean": mean("copy"), "ded_mb": round(_mean([r.get("ded_mb", 0.0) for r in rows])),
                    "shared_mb": round(_mean([r.get("shared_mb", 0.0) for r in rows]))})
    return out


GAME_COLUMNS = (("pid", "pid", 7), ("seconds", "secs", 5), ("win", "window", 9), ("size", "size", 9), ("fg_pct", "fg%", 4),
                ("gpu3d_mean", "3D%", 5), ("gpu3d_max", "3Dmax", 5), ("copy_mean", "copy%", 5), ("ded_mb", "dedMB", 6),
                ("shared_mb", "shMB", 5))


def format_games(rows: list[dict]) -> str:
    lines = ["  ".join(title.rjust(width) for _key, title, width in GAME_COLUMNS)]
    lines += ["  ".join(str(row[key]).rjust(width) for key, _title, width in GAME_COLUMNS) for row in rows]
    return "\n".join(lines)


COLUMNS = (("ts3w", "ts3w", 4), ("seconds", "secs", 5), ("presents_per_s", "pres/s", 6),
           ("present_median_per_second_p95_ms", "median per-second p95 (ms)", 25),
           ("present_max_p99_ms", "max99", 6), ("stall_per_min", "stall/m", 7), ("stall_seconds_pct", "stall%s", 7),
           ("dwm_late_per_min", "late/m", 6), ("dwm_missed_per_min", "miss/m", 6), ("dwm_dropped_per_min", "drop/m", 6),
           ("gpu3d_mean", "3D%", 5), ("gpu3d_p95", "3Dp95", 5), ("gpu3d_saturated_pct", "sat%s", 5),
           ("ts3w_3d_each", "g3D", 5), ("ts3w_ded_mb_each", "gDedMB", 7), ("ts3w_shared_mb_each", "gShMB", 6),
           ("adapter_ded_mb_mean", "adDedMB", 7), ("adapter_shared_mb_mean", "adShMB", 7))


def format_rows(rows: list[dict], labels: list[str] | None = None) -> str:
    """A fixed-width table. `labels` (one per row) prefixes each row, for report's side-by-side."""
    head = "  ".join(title.rjust(width) for _key, title, width in COLUMNS)
    if labels:
        width = max(len(label) for label in labels)
        head = "log".ljust(width) + "  " + head
    out = [head]
    for i, row in enumerate(rows):
        cells = "  ".join(str(row.get(key, "")).rjust(width) for key, _title, width in COLUMNS)
        out.append((labels[i].ljust(max(len(label) for label in labels)) + "  " if labels else "") + cells)
    return "\n".join(out)


def load_log(path: Path) -> tuple[dict, list[dict]]:
    """(header, samples) from a JSONL log."""
    header, samples = {}, []
    for line in Path(path).read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        row = json.loads(line)
        if row.get("kind") == "header":
            header = row
        else:
            samples.append(row)
    return header, samples


# --- collector (Windows) ------------------------------------------------------------------------------------------

class Collector:
    """Reads one sample per call. Windows only; everything else in this file is platform-free."""

    DWM_FIELDS = ("frames", "refreshes", "late", "missed", "dropped")

    def __init__(self, probe: bool = True):
        import ctypes
        from ctypes import wintypes as w
        self.ct, self.w = ctypes, w
        self.k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        self.pdh = ctypes.WinDLL("pdh")
        self.dwmapi = ctypes.WinDLL("dwmapi")
        u32, u64 = ctypes.c_uint32, ctypes.c_uint64

        class Ratio(ctypes.Structure):
            _fields_ = [("n", u32), ("d", u32)]

        class Timing(ctypes.Structure):   # DWM_TIMING_INFO; dwmapi.h declares it under pshpack1, hence 292 bytes
            _pack_ = 1
            _fields_ = [("cbSize", u32), ("rateRefresh", Ratio), ("qpcRefreshPeriod", u64), ("rateCompose", Ratio),
                        ("qpcVBlank", u64), ("cRefresh", u64), ("cDXRefresh", u32), ("qpcCompose", u64), ("cFrame", u64),
                        ("cDXPresent", u32), ("cRefreshFrame", u64), ("cFrameSubmitted", u64), ("cDXPresentSubmitted", u32),
                        ("cFrameConfirmed", u64), ("cDXPresentConfirmed", u32), ("cRefreshConfirmed", u64),
                        ("cDXRefreshConfirmed", u32), ("cFramesLate", u64), ("cFramesOutstanding", u32),
                        ("cFrameDisplayed", u64), ("qpcFrameDisplayed", u64), ("cRefreshFrameDisplayed", u64),
                        ("cFrameComplete", u64), ("qpcFrameComplete", u64), ("cFramePending", u64),
                        ("qpcFramePending", u64), ("cFramesDisplayed", u64), ("cFramesComplete", u64),
                        ("cFramesPending", u64), ("cFramesAvailable", u64), ("cFramesDropped", u64),
                        ("cFramesMissed", u64), ("cRefreshNextDisplayed", u64), ("cRefreshNextPresented", u64),
                        ("cRefreshesDisplayed", u64), ("cRefreshesPresented", u64), ("cRefreshStarted", u64),
                        ("cPixelsReceived", u64), ("cPixelsDrawn", u64), ("cBuffersEmpty", u64)]

        class PE(ctypes.Structure):
            _fields_ = [("dwSize", w.DWORD), ("cntUsage", w.DWORD), ("pid", w.DWORD), ("heap", ctypes.c_size_t),
                        ("module", w.DWORD), ("threads", w.DWORD), ("ppid", w.DWORD), ("pri", w.LONG),
                        ("flags", w.DWORD), ("exe", w.WCHAR * 260)]

        class Item(ctypes.Structure):     # PDH_FMT_COUNTERVALUE_ITEM_W with a double value
            _fields_ = [("name", ctypes.c_wchar_p), ("status", w.DWORD), ("pad", w.DWORD), ("value", ctypes.c_double)]

        self.Timing, self.PE, self.Item = Timing, PE, Item
        self.user32 = ctypes.WinDLL("user32")
        u = self.user32
        u.GetForegroundWindow.restype = w.HWND
        u.IsWindowVisible.argtypes = u.IsIconic.argtypes = [w.HWND]
        u.GetWindow.restype, u.GetWindow.argtypes = w.HWND, [w.HWND, w.UINT]
        u.GetWindowThreadProcessId.argtypes = [w.HWND, ctypes.POINTER(w.DWORD)]
        u.GetClientRect.argtypes = [w.HWND, ctypes.POINTER(w.RECT)]
        u.GetClassNameW.argtypes = [w.HWND, w.LPWSTR, ctypes.c_int]
        self.enum_proc = ctypes.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
        u.EnumWindows.argtypes = [self.enum_proc, w.LPARAM]
        self.dwmapi.DwmGetCompositionTimingInfo.argtypes = [w.HWND, ctypes.c_void_p]
        k = self.k32
        k.CreateToolhelp32Snapshot.restype = ctypes.c_void_p
        k.CreateToolhelp32Snapshot.argtypes = [w.DWORD, w.DWORD]
        k.CloseHandle.argtypes = [ctypes.c_void_p]
        k.Process32FirstW.argtypes = k.Process32NextW.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
        self.pdh_query = ctypes.c_void_p()
        self.pdh.PdhOpenQueryW(None, 0, ctypes.byref(self.pdh_query))
        self.counters: dict[str, ctypes.c_void_p] = {}
        for key, path in (("engine", r"\GPU Engine(*)\Utilization Percentage"),
                          ("proc_ded", r"\GPU Process Memory(*)\Dedicated Usage"),
                          ("proc_shared", r"\GPU Process Memory(*)\Shared Usage"),
                          ("ad_ded", r"\GPU Adapter Memory(*)\Dedicated Usage"),
                          ("ad_shared", r"\GPU Adapter Memory(*)\Shared Usage")):
            handle = ctypes.c_void_p()
            if self.pdh.PdhAddEnglishCounterW(self.pdh_query, path, 0, ctypes.byref(handle)) == 0:
                self.counters[key] = handle
        self.pdh.PdhCollectQueryData(self.pdh_query)
        self.prev_dwm: dict | None = None
        self.prev_t = time.perf_counter()
        self.intervals: list[float] = []
        self.lock = threading.Lock()
        self.stop = threading.Event()
        self.thread = None
        self.flush_ok = True
        if probe:
            self.thread = threading.Thread(target=self._flush_loop, daemon=True)
            self.thread.start()

    def close(self) -> None:
        self.stop.set()
        if self.thread:
            self.thread.join(1.5)

    def _flush_loop(self) -> None:
        last = time.perf_counter()
        while not self.stop.is_set():
            if self.dwmapi.DwmFlush() != 0:       # composition off or the call refused: do not spin
                self.flush_ok = False
                time.sleep(0.25)
                last = time.perf_counter()
                continue
            now = time.perf_counter()
            with self.lock:
                self.intervals.append((now - last) * 1000.0)
            last = now

    def _dwm(self) -> dict | None:
        info = self.Timing()
        info.cbSize = self.ct.sizeof(info)
        if self.dwmapi.DwmGetCompositionTimingInfo(None, self.ct.byref(info)) != 0:
            return None
        return {"frames": info.cFrame, "refreshes": info.cRefresh, "late": info.cFramesLate,
                "missed": info.cFramesMissed, "dropped": info.cFramesDropped, "outstanding": info.cFramesOutstanding}

    def _array(self, key: str) -> list[tuple[str, float]]:
        """Every instance of a wildcard counter with its value."""
        handle = self.counters.get(key)
        if handle is None:
            return []
        ct = self.ct
        size, count = self.w.DWORD(0), self.w.DWORD(0)
        status = self.pdh.PdhGetFormattedCounterArrayW(handle, 0x8200, ct.byref(size), ct.byref(count), None)
        if (status & 0xFFFFFFFF) != 0x800007D2 or not size.value:
            return []
        buf = ct.create_string_buffer(size.value)
        if self.pdh.PdhGetFormattedCounterArrayW(handle, 0x8200, ct.byref(size), ct.byref(count), buf) != 0:
            return []
        items = ct.cast(buf, ct.POINTER(self.Item))
        return [(items[i].name, items[i].value) for i in range(count.value) if items[i].status in (0, 1)]

    def _processes(self) -> dict[int, str]:
        k, ct = self.k32, self.ct
        snap = k.CreateToolhelp32Snapshot(2, 0)
        names: dict[int, str] = {}
        if snap in (None, ct.c_void_p(-1).value):
            return names
        entry = self.PE()
        entry.dwSize = ct.sizeof(entry)
        ok = k.Process32FirstW(snap, ct.byref(entry))
        while ok:
            names[entry.pid] = entry.exe
            ok = k.Process32NextW(snap, ct.byref(entry))
        k.CloseHandle(snap)
        return names

    def _windows(self, pids: set[int]) -> dict[int, dict]:
        """Per pid, its Canvas window: state, focus and client size. Read-only (no message is sent)."""
        u, w = self.user32, self.w
        best: dict[int, tuple[int, dict]] = {}
        foreground = u.GetForegroundWindow()

        def visit(hwnd, _param):
            pid = w.DWORD()
            u.GetWindowThreadProcessId(hwnd, self.ct.byref(pid))
            if pid.value in pids and not u.GetWindow(hwnd, 4):          # GW_OWNER
                name = self.ct.create_unicode_buffer(256)
                u.GetClassNameW(hwnd, name, len(name))
                if name.value.rsplit(":", 1)[-1] != "Canvas":  # Sandboxie prefixes the game's window class
                    return True
                rect = w.RECT()
                u.GetClientRect(hwnd, self.ct.byref(rect))
                width, height = rect.right - rect.left, rect.bottom - rect.top
                state = "minimized" if u.IsIconic(hwnd) else ("visible" if u.IsWindowVisible(hwnd) else "hidden")
                score = 1 + width * height + (1 << 40 if state != "hidden" else 0)
                if pid.value not in best or score > best[pid.value][0]:
                    best[pid.value] = (score, {"win": state, "fg": hwnd == foreground, "w": width, "h": height})
            return True

        u.EnumWindows(self.enum_proc(visit), 0)
        return {pid: info for pid, (_score, info) in best.items()}

    def sample(self) -> dict:
        t = time.perf_counter()
        dt, self.prev_t = max(1e-3, t - self.prev_t), t
        with self.lock:
            intervals, self.intervals = self.intervals, []
        dwm_now = self._dwm()
        dwm = dwm_delta(self.prev_dwm, dwm_now) if dwm_now else {"frames": 0, "refreshes": 0, "late": 0, "missed": 0,
                                                                 "dropped": 0, "outstanding": 0}
        self.prev_dwm = dwm_now or self.prev_dwm
        self.pdh.PdhCollectQueryData(self.pdh_query)
        parsed = [(inst, pct) for name, pct in self._array("engine") if (inst := parse_engine_instance(name))]
        busiest, per_pid = aggregate_engines(parsed)
        ded, shared = collections.defaultdict(float), collections.defaultdict(float)
        for name, value in self._array("proc_ded"):
            pid = parse_process_memory_instance(name)
            if pid is not None:
                ded[pid] += value / 1048576.0
        for name, value in self._array("proc_shared"):
            pid = parse_process_memory_instance(name)
            if pid is not None:
                shared[pid] += value / 1048576.0
        adapters = collections.defaultdict(lambda: [0.0, 0.0])
        for name, value in self._array("ad_ded"):
            adapters[name][0] = value / 1048576.0
        for name, value in self._array("ad_shared"):
            adapters[name][1] = value / 1048576.0
        main = max(adapters.values(), key=lambda v: v[0], default=[0.0, 0.0])
        names = self._processes()
        game_pids = {pid for pid, name in names.items() if name.lower() == "ts3w.exe"}
        windows = self._windows(game_pids)
        gpu = []
        for pid in set(per_pid) | set(ded) | set(shared) | game_pids:
            name = names.get(pid, "?")
            loads = per_pid.get(pid, {})
            busy = sum(loads.values()) >= GPU_MIN_PCT
            if not (busy or ded.get(pid, 0.0) >= GPU_MIN_DED_MB or name.lower() in ("ts3w.exe", "dwm.exe")):
                continue
            gpu.append({"pid": pid, "name": name, "3D": round(loads.get("3D", 0.0), 1), "copy": round(loads.get("Copy", 0.0), 1),
                        "video": round(loads.get("Video", 0.0), 1),
                        "other": round(loads.get("Compute", 0.0) + loads.get("Other", 0.0), 1),
                        "ded_mb": round(ded.get(pid, 0.0)), "shared_mb": round(shared.get(pid, 0.0)),
                        **windows.get(pid, {})})
        return {"t": round(time.time(), 3), "time": datetime.now().strftime("%H:%M:%S"), "dt": round(dt, 3),
                "n_ts3w": sum(1 for name in names.values() if name.lower() == "ts3w.exe"),
                "flush": flush_stats(intervals), "dwm": dwm, "eng": busiest,
                "adapter_ded_mb": round(main[0]), "adapter_shared_mb": round(main[1]),
                "gpu": sorted(gpu, key=lambda p: -(p["3D"] + p["copy"] + p["video"] + p["other"]))}


# --- CLI ----------------------------------------------------------------------------------------------------------

def run_dir() -> Path:
    env = os.environ.get("SIMSONLINE_RUN_DIR")
    if env:
        return Path(env)
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    from stack_claim import main_checkout
    return Path(main_checkout()) / ".omo" / "run"


def sample_line(s: dict) -> str:
    f, d = s["flush"], s["dwm"]
    return (f"{s['time']} ts3w {s['n_ts3w']}  present {f['n']}/s per-second p95 {f['p95']} max {f['max']} ms stall {f['stall']}  "
            f"dwm late {d['late']} missed {d['missed']} dropped {d['dropped']}  3D {s['eng']['3D']}% "
            f"ded {s['adapter_ded_mb']} MB")


def cmd_run(args) -> int:
    if os.name != "nt":
        print("frame_pacing run: Windows only (dwmapi, performance counters)", file=sys.stderr)
        return 2
    out = Path(args.out) if args.out else run_dir() / "frame-pacing" / f"{args.label}-{datetime.now(timezone.utc):%Y%m%dT%H%M%SZ}.jsonl"
    out.parent.mkdir(parents=True, exist_ok=True)
    collector = Collector(probe=not args.no_probe)
    header = {"kind": "header", "label": args.label, "interval": args.interval, "pid": os.getpid(),
              "started": datetime.now(timezone.utc).isoformat(timespec="seconds"),
              "limits": {"slow_ms": SLOW_MS, "stall_ms": STALL_MS, "saturated": SATURATED}}
    print(f"frame_pacing: {out} (every {args.interval} s"
          f"{', ' + str(args.seconds) + ' s' if args.seconds else ', until Ctrl+C'})", flush=True)
    samples: list[dict] = []
    start = time.monotonic()
    deadline = start
    try:
        with out.open("w", encoding="utf-8", newline="\n") as handle:
            handle.write(json.dumps(header) + "\n")
            while not args.seconds or time.monotonic() - start < args.seconds:
                deadline += args.interval
                time.sleep(max(0.0, deadline - time.monotonic()))
                sample = collector.sample()
                samples.append(sample)
                handle.write(json.dumps(sample, separators=(",", ":")) + "\n")
                handle.flush()
                if not args.quiet:
                    print(sample_line(sample), flush=True)
    except KeyboardInterrupt:
        pass
    finally:
        collector.close()
    if not collector.flush_ok:
        print("warning: DwmFlush failed at least once (composition off?); flush stats are incomplete", file=sys.stderr)
    print()
    print(format_rows(by_ts3w_count(samples)))
    return 0


def cmd_report(args) -> int:
    rows, labels = [], []
    for path in args.files:
        _header, samples = load_log(Path(path))
        rows.append({"ts3w": "all", **summarize(samples)})
        labels.append(Path(path).stem[:40])
    print(format_rows(rows, labels))
    return 0


def cmd_counts(args) -> int:
    samples: list[dict] = []
    for path in args.files:
        samples.extend(load_log(Path(path))[1])
    print(format_rows(by_ts3w_count(samples)))
    return 0


def cmd_games(args) -> int:
    samples: list[dict] = []
    for path in args.files:
        samples.extend(load_log(Path(path))[1])
    print(format_games(per_game(samples)))
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    sub = parser.add_subparsers(dest="cmd", required=True)
    run = sub.add_parser("run", help="sample until --seconds or Ctrl+C")
    run.add_argument("--seconds", type=float, default=0)
    run.add_argument("--label", default="frame")
    run.add_argument("--interval", type=float, default=1.0)
    run.add_argument("--out", help="log path (default: <run dir>/frame-pacing/<label>-<stamp>.jsonl)")
    run.add_argument("--quiet", action="store_true", help="no per-second line")
    run.add_argument("--no-probe", action="store_true", help="skip the DwmFlush thread")
    run.set_defaults(fn=cmd_run)
    for name, fn, text in (("report", cmd_report, "one summary row per log"), ("counts", cmd_counts, "rows by TS3W count, logs merged"),
                           ("games", cmd_games, "one row per TS3W pid: window state and size, GPU use")):
        p = sub.add_parser(name, help=text)
        p.add_argument("files", nargs="+")
        p.set_defaults(fn=fn)
    args = parser.parse_args(argv)
    return args.fn(args)


if __name__ == "__main__":
    sys.exit(main())
