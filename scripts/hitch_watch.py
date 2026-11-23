#!/usr/bin/env python3
"""Why does Windows hitch while the dev stack runs? Sample the machine once a second and name the contenders.

    python scripts/hitch_watch.py run [--seconds N] [--label NAME] [--interval 1.0] [--no-gpu] [--quiet]
    python scripts/hitch_watch.py report FILE [FILE ...]       # summary per log, side by side when several
    python scripts/hitch_watch.py spikes FILE                  # re-list a log's spikes

`run` is read-only and needs no admin: it opens processes with PROCESS_QUERY_LIMITED_INFORMATION and reads
performance counters (PDH). Nothing is changed, suspended or signalled; it never touches a game or the stack. Every
second it appends one JSON line to `<run dir>/hitch/<label>-<UTC stamp>.jsonl` (run dir: SIMSONLINE_RUN_DIR, else the
main checkout's .omo/run, the same file from every worktree):

    t, dt, cpu (% of the machine), queue (processor queue length), ctx (context switches/s), dpc and int (% time),
    dpcs (DPCs queued/s), disk_q, disk_ms (avg sec/transfer, in ms), disk_mb_s, gpu (3D engine %, all processes),
    lat_ms (the worst overshoot of a 5 ms sleep by this tool's own probe thread: how late a Normal-priority thread was
    woken, the closest cheap proxy for a visible hitch), other_cpu (busy CPU the live processes do not account for:
    exited processes, the kernel, interrupts), procs (every process using >= 0.05 core or 1 MB of I/O: pid, name,
    cores, priv_mb, io_mb, prio, gpu), starts ("child<-parent": count) and n_starts/n_exits/nproc.

Process starts and exits come from a 4 Hz scan of the process list, so a process that lives under 250 ms is not named;
its CPU shows up in `other_cpu` and its absence of a name in the start counts. A spike is a second where CPU > 85 %,
the queue is over 2x the logical cores, 8 or more processes started, or the probe thread woke 50 ms late; `run` prints
each with its top contributors, and `report` totals them. Analysis (`find_spikes`, `summarize`) works on parsed
samples and is unit-tested with recorded ones; only `Collector` touches Windows.
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

CPU_SPIKE = 85.0          # % of the machine
QUEUE_FACTOR = 2.0        # x logical cores
START_BURST = 8           # process starts in one sample
LATE_MS = 50.0            # probe-thread wake lateness
PROC_MIN_CORES = 0.05
PROC_MIN_IO_MB = 1.0
SCAN_HZ = 4

COUNTERS = {
    "queue": r"\System\Processor Queue Length",
    "ctx": r"\System\Context Switches/sec",
    "dpc": r"\Processor Information(_Total)\% DPC Time",
    "int": r"\Processor Information(_Total)\% Interrupt Time",
    "dpcs": r"\Processor Information(_Total)\DPCs Queued/sec",
    "disk_q": r"\PhysicalDisk(_Total)\Current Disk Queue Length",
    "disk_s": r"\PhysicalDisk(_Total)\Avg. Disk sec/Transfer",
    "disk_b": r"\PhysicalDisk(_Total)\Disk Bytes/sec",
}
PRIORITY_NAMES = {0x20: "normal", 0x40: "idle", 0x80: "high", 0x100: "realtime", 0x4000: "below", 0x8000: "above"}


# --- analysis (pure: parsed samples in, text and numbers out) ------------------------------------------------------

def by_name(procs: list[dict]) -> dict[str, float]:
    """Cores used per executable name (every pid of one name summed)."""
    out: dict[str, float] = collections.defaultdict(float)
    for p in procs:
        out[p["name"]] += p.get("cores", 0.0)
    return out


def top_contributors(sample: dict, n: int = 5) -> list[tuple[str, float]]:
    return sorted(by_name(sample.get("procs", [])).items(), key=lambda kv: -kv[1])[:n]


def find_spikes(sample: dict, cores: int, cpu: float = CPU_SPIKE, queue_factor: float = QUEUE_FACTOR,
                burst: int = START_BURST, late_ms: float = LATE_MS) -> list[str]:
    """The reasons this second is a spike (empty when it is not)."""
    reasons = []
    if sample.get("cpu", 0.0) > cpu:
        reasons.append(f"cpu {sample['cpu']:.0f}%")
    if sample.get("queue", 0.0) > queue_factor * cores:
        reasons.append(f"queue {sample['queue']:.0f}")
    if sample.get("n_starts", 0) >= burst:
        reasons.append(f"{sample['n_starts']} process starts")
    if sample.get("lat_ms", 0.0) >= late_ms:
        reasons.append(f"wake {sample['lat_ms']:.0f} ms late")
    return reasons


def describe_spike(sample: dict, reasons: list[str]) -> str:
    parts = [f"{sample.get('time', '?')} " + ", ".join(reasons)]
    top = [f"{name} {cores:.2f}c" for name, cores in top_contributors(sample) if cores >= PROC_MIN_CORES]
    if top:
        parts.append("top: " + ", ".join(top))
    if sample.get("other_cpu", 0.0) >= 10:
        parts.append(f"unaccounted {sample['other_cpu']:.0f}%")
    starts = sorted(sample.get("starts", {}).items(), key=lambda kv: -kv[1])[:3]
    if starts:
        parts.append("starts: " + ", ".join(f"{k} x{v}" for k, v in starts))
    io = sorted(sample.get("procs", []), key=lambda p: -p.get("io_mb", 0.0))[:1]
    if io and io[0].get("io_mb", 0.0) >= 20:
        parts.append(f"io: {io[0]['name']} {io[0]['io_mb']:.0f} MB")
    for key, label, floor in (("dpc", "dpc", 5.0), ("int", "irq", 5.0), ("disk_ms", "disk ms", 20.0)):
        if sample.get(key, 0.0) >= floor:
            parts.append(f"{label} {sample[key]:.1f}")
    return "; ".join(parts)


def percentile(values: list[float], q: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, int(round(q * (len(ordered) - 1))))]


def summarize(samples: list[dict], cores: int, **limits) -> dict:
    """Numbers for one log: load, latency, spikes by reason, who used the CPU, who started processes."""
    series = lambda key: [s.get(key, 0.0) for s in samples]  # noqa: E731
    spike_rows = []
    reasons_count: collections.Counter = collections.Counter()
    spike_cores: dict[str, float] = collections.defaultdict(float)
    for s in samples:
        reasons = find_spikes(s, cores, **limits)
        if reasons:
            spike_rows.append((s, reasons))
            for r in reasons:
                reasons_count[r.split(" ")[0] if not r[0].isdigit() else "starts"] += 1
            for name, used in by_name(s.get("procs", [])).items():
                spike_cores[name] += used
    total_cores: dict[str, float] = collections.defaultdict(float)
    peak_priv: dict[str, float] = collections.defaultdict(float)
    priority: dict[str, str] = {}
    gpu: dict[str, float] = collections.defaultdict(float)
    for s in samples:
        for name, used in by_name(s.get("procs", [])).items():
            total_cores[name] += used
        per_name_priv: dict[str, float] = collections.defaultdict(float)
        for p in s.get("procs", []):
            per_name_priv[p["name"]] += p.get("priv_mb", 0.0)
            gpu[p["name"]] += p.get("gpu", 0.0)
            if "prio" in p:
                priority[p["name"]] = p["prio"]
        for name, mb in per_name_priv.items():
            peak_priv[name] = max(peak_priv[name], mb)
    starts: collections.Counter = collections.Counter()
    for s in samples:
        starts.update(s.get("starts", {}))
    n = max(1, len(samples))
    cpu, queue, lat = series("cpu"), series("queue"), series("lat_ms")
    return {
        "samples": len(samples),
        "cores": cores,
        "seconds": round(sum(s.get("dt", 1.0) for s in samples), 1),
        "cpu_mean": round(sum(cpu) / n, 1), "cpu_p95": round(percentile(cpu, 0.95), 1), "cpu_max": round(max(cpu, default=0), 1),
        "queue_mean": round(sum(queue) / n, 2), "queue_p95": round(percentile(queue, 0.95), 1), "queue_max": round(max(queue, default=0), 1),
        "ctx_p95": round(percentile(series("ctx"), 0.95)),
        "dpc_max": round(max(series("dpc"), default=0), 2), "int_max": round(max(series("int"), default=0), 2),
        "dpcs_p95": round(percentile(series("dpcs"), 0.95)),
        "disk_ms_p95": round(percentile(series("disk_ms"), 0.95), 2), "disk_ms_max": round(max(series("disk_ms"), default=0), 1),
        "disk_mb_s_p95": round(percentile(series("disk_mb_s"), 0.95), 1),
        "gpu_mean": round(sum(series("gpu")) / n, 1), "gpu_max": round(max(series("gpu"), default=0), 1),
        "lat_ms_p99": round(percentile(lat, 0.99), 1), "lat_ms_max": round(max(lat, default=0), 1),
        "late_seconds": sum(1 for v in lat if v >= limits.get("late_ms", LATE_MS)),
        "other_cpu_mean": round(sum(series("other_cpu")) / n, 1),
        "starts_per_min": round(sum(s.get("n_starts", 0) for s in samples) * 60.0 / max(1.0, sum(s.get("dt", 1.0) for s in samples)), 1),
        "spike_seconds": len(spike_rows),
        "spike_reasons": dict(reasons_count),
        "mean_cores": {k: round(v / n, 2) for k, v in sorted(total_cores.items(), key=lambda kv: -kv[1])[:12]},
        "spike_cores": {k: round(v, 1) for k, v in sorted(spike_cores.items(), key=lambda kv: -kv[1])[:8]},
        "peak_priv_mb": {k: round(v) for k, v in sorted(peak_priv.items(), key=lambda kv: -kv[1])[:6]},
        "priority": {k: v for k, v in priority.items() if k in dict(sorted(total_cores.items(), key=lambda kv: -kv[1])[:12])},
        "gpu_by_name": {k: round(v / n, 1) for k, v in sorted(gpu.items(), key=lambda kv: -kv[1])[:4] if v},
        "starts": dict(starts.most_common(10)),
        "spikes": [describe_spike(s, r) for s, r in spike_rows],
    }


def format_summary(label: str, s: dict, spike_lines: int = 12) -> str:
    out = [f"== {label}: {s['seconds']} s, {s['samples']} samples, {s['cores']} logical cores",
           f"cpu mean {s['cpu_mean']}% p95 {s['cpu_p95']}% max {s['cpu_max']}% (unaccounted mean {s['other_cpu_mean']}%)",
           f"queue mean {s['queue_mean']} p95 {s['queue_p95']} max {s['queue_max']}; ctx p95 {s['ctx_p95']}/s; "
           f"dpc max {s['dpc_max']}% irq max {s['int_max']}% dpcs p95 {s['dpcs_p95']}/s",
           f"disk p95 {s['disk_ms_p95']} ms (max {s['disk_ms_max']}), p95 {s['disk_mb_s_p95']} MB/s; gpu 3D mean {s['gpu_mean']}% max {s['gpu_max']}%",
           f"wake latency p99 {s['lat_ms_p99']} ms, max {s['lat_ms_max']} ms, {s['late_seconds']} s over {LATE_MS:.0f} ms",
           f"process starts {s['starts_per_min']}/min; spike seconds {s['spike_seconds']} {s['spike_reasons']}",
           "mean cores: " + ", ".join(f"{k} {v}" for k, v in s["mean_cores"].items()),
           "cores summed over spike seconds: " + ", ".join(f"{k} {v}" for k, v in s["spike_cores"].items()),
           "peak private MB: " + ", ".join(f"{k} {v}" for k, v in s["peak_priv_mb"].items()),
           "priority: " + ", ".join(f"{k} {v}" for k, v in s["priority"].items()),
           "gpu 3D % by process (mean): " + ", ".join(f"{k} {v}" for k, v in s["gpu_by_name"].items()),
           "top starts: " + ", ".join(f"{k} x{v}" for k, v in s["starts"].items())]
    out += ["  " + line for line in s["spikes"][:spike_lines]]
    if len(s["spikes"]) > spike_lines:
        out.append(f"  ... {len(s['spikes']) - spike_lines} more spike seconds (hitch_watch.py spikes FILE)")
    return "\n".join(out)


# Executables whose cores count as build load when the TS3W contribution is split from it.
BUILD_NAMES = frozenset({"cl.exe", "link.exe", "rustc.exe", "cargo.exe", "clippy-driver.exe", "rustdoc.exe",
                         "msbuild.exe", "dotnet.exe", "csc.exe", "vbcscompiler.exe", "cc1plus.exe", "ld.exe"})
BUILD_CORES = 1.0


def ts3w_count(sample: dict) -> int:
    """TS3W.exe processes in this second: the collector's own count, else (older logs) the distinct pids seen using CPU."""
    if "n_ts3w" in sample:
        return sample["n_ts3w"]
    return len({p["pid"] for p in sample.get("procs", []) if p["name"].lower() == "ts3w.exe"})


def build_cores(sample: dict) -> float:
    return sum(p.get("cores", 0.0) for p in sample.get("procs", []) if p["name"].lower() in BUILD_NAMES)


def by_ts3w_count(samples: list[dict], build_floor: float = BUILD_CORES, late_ms: float = LATE_MS) -> list[dict]:
    """One row per (TS3W count, build or quiet): the seconds whose build cores are >= build_floor against those under it, so
    the games' own contribution shows in the quiet rows. Stutter metrics are the run queue and the wake-latency probe."""
    groups: dict[tuple[int, bool], list[dict]] = collections.defaultdict(list)
    for s in samples:
        groups[(ts3w_count(s), build_cores(s) >= build_floor)].append(s)
    rows = []
    for (count, busy), g in sorted(groups.items()):
        n = len(g)
        mean = lambda key: sum(s.get(key, 0.0) for s in g) / n  # noqa: E731
        games = [by_name(s.get("procs", [])).get("TS3W.exe", 0.0) for s in g]
        gpu = [sum(p.get("gpu", 0.0) for p in s.get("procs", []) if p["name"] == "TS3W.exe") for s in g]
        queue, lat = [s.get("queue", 0.0) for s in g], [s.get("lat_ms", 0.0) for s in g]
        rows.append({
            "ts3w": count, "build": busy, "seconds": n,
            "cpu_mean": round(mean("cpu"), 1), "queue_mean": round(mean("queue"), 2), "queue_p95": round(percentile(queue, 0.95), 1),
            "lat_p99": round(percentile(lat, 0.99), 1), "lat_max": round(max(lat), 1),
            "late_seconds": sum(1 for v in lat if v >= late_ms),
            "ts3w_cores": round(sum(games) / n, 2), "cores_each": round(sum(games) / n / count, 2) if count else 0.0,
            "gpu_ts3w": round(sum(gpu) / n, 1), "gpu_total": round(mean("gpu"), 1),
            "starts_per_min": round(sum(s.get("n_starts", 0) for s in g) * 60.0 / max(1.0, sum(s.get("dt", 1.0) for s in g)), 1),
            "dpc_max": round(max(s.get("dpc", 0.0) for s in g), 2),
        })
    return rows


def format_counts(rows: list[dict]) -> str:
    head = ("ts3w  build  secs  cpu%  queue(mean/p95)  wake p99/max ms  >50ms  ts3w cores (each)  ts3w gpu%  gpu%  starts/min")
    out = [head]
    for r in rows:
        out.append(f"{r['ts3w']:>4}  {'yes' if r['build'] else 'no':>5}  {r['seconds']:>4}  {r['cpu_mean']:>4.0f}  "
                   f"{r['queue_mean']:>6.1f}/{r['queue_p95']:<8.0f}  {r['lat_p99']:>6.0f}/{r['lat_max']:<8.0f}  {r['late_seconds']:>5}  "
                   f"{r['ts3w_cores']:>6.2f} ({r['cores_each']:.2f})   {r['gpu_ts3w']:>8.1f}  {r['gpu_total']:>4.0f}  {r['starts_per_min']:>8.0f}")
    return "\n".join(out)


def load_log(path: Path) -> tuple[int, list[dict]]:
    """(logical cores, samples) from a JSONL log; the first line is the header the run wrote."""
    cores, samples = os.cpu_count() or 1, []
    for line in Path(path).read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        row = json.loads(line)
        if row.get("kind") == "header":
            cores = row.get("cores", cores)
        else:
            samples.append(row)
    return cores, samples


# --- collector (Windows) --------------------------------------------------------------------------------------------

class Collector:
    """Reads one sample per call. Windows only; everything else in this file is platform-free."""

    def __init__(self, gpu: bool = True, probe: bool = True):
        import ctypes
        from ctypes import wintypes as w
        self.ct, self.w = ctypes, w
        self.k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        self.pdh = ctypes.WinDLL("pdh")
        self.cores = os.cpu_count() or 1
        k = self.k32
        k.OpenProcess.restype = ctypes.c_void_p
        k.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]
        k.CloseHandle.argtypes = [ctypes.c_void_p]
        k.CreateToolhelp32Snapshot.restype = ctypes.c_void_p
        k.CreateToolhelp32Snapshot.argtypes = [w.DWORD, w.DWORD]
        k.GetProcessTimes.argtypes = [ctypes.c_void_p] + [ctypes.c_void_p] * 4
        k.GetProcessIoCounters.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
        k.K32GetProcessMemoryInfo.argtypes = [ctypes.c_void_p, ctypes.c_void_p, w.DWORD]
        k.GetPriorityClass.argtypes = [ctypes.c_void_p]
        k.GetSystemTimes.argtypes = [ctypes.c_void_p] * 3
        k.Process32FirstW.argtypes = k.Process32NextW.argtypes = [ctypes.c_void_p, ctypes.c_void_p]

        class PE(ctypes.Structure):
            _fields_ = [("dwSize", w.DWORD), ("cntUsage", w.DWORD), ("pid", w.DWORD), ("heap", ctypes.c_size_t),
                        ("module", w.DWORD), ("threads", w.DWORD), ("ppid", w.DWORD), ("pri", w.LONG),
                        ("flags", w.DWORD), ("exe", w.WCHAR * 260)]

        class FT(ctypes.Structure):
            _fields_ = [("lo", w.DWORD), ("hi", w.DWORD)]

        class IO(ctypes.Structure):
            _fields_ = [(n, ctypes.c_ulonglong) for n in ("rops", "wops", "oops", "rb", "wb", "ob")]

        class MEM(ctypes.Structure):  # PROCESS_MEMORY_COUNTERS_EX
            _fields_ = [("cb", w.DWORD), ("faults", w.DWORD)] + [(n, ctypes.c_size_t) for n in (
                "peak_ws", "ws", "peak_pp", "pp", "peak_np", "np", "pagefile", "peak_pagefile", "private")]

        class VAL(ctypes.Structure):
            _fields_ = [("status", w.DWORD), ("pad", w.DWORD), ("v", ctypes.c_double)]

        self.PE, self.FT, self.IO, self.MEM, self.VAL = PE, FT, IO, MEM, VAL
        self.prev_proc: dict[tuple[int, int], tuple[int, int]] = {}
        self.prev_sys = self._system_times()
        self.prev_t = time.perf_counter()
        self.scan_names: dict[int, tuple[str, int]] = {}
        self.pending_starts: collections.Counter = collections.Counter()
        self.pending_exits = 0
        self._scan(first=True)
        self.pdh_query = ctypes.c_void_p()
        self.pdh.PdhOpenQueryW(None, 0, ctypes.byref(self.pdh_query))
        self.counters: dict[str, ctypes.c_void_p] = {}
        for key, path in COUNTERS.items():
            handle = ctypes.c_void_p()
            if self.pdh.PdhAddEnglishCounterW(self.pdh_query, path, 0, ctypes.byref(handle)) == 0:
                self.counters[key] = handle
        self.gpu_on = gpu
        self.gpu_counters: dict[str, tuple[int, ctypes.c_void_p]] = {}
        self._gpu_refreshed = 0.0
        self._refresh_gpu()
        self.pdh.PdhCollectQueryData(self.pdh_query)
        self.probe_max = 0.0
        self.probe_lock = threading.Lock()
        self.stop = threading.Event()
        self.probe_thread = None
        if probe:
            self.probe_thread = threading.Thread(target=self._probe, daemon=True)
            self.probe_thread.start()

    def close(self) -> None:
        self.stop.set()
        if self.probe_thread:
            self.probe_thread.join(1)

    def _probe(self) -> None:
        while not self.stop.is_set():
            t0 = time.perf_counter()
            time.sleep(0.005)
            late = (time.perf_counter() - t0 - 0.005) * 1000.0
            with self.probe_lock:
                self.probe_max = max(self.probe_max, late)

    def _system_times(self) -> tuple[int, int, int]:
        idle, kernel, user = self.FT(), self.FT(), self.FT()
        self.k32.GetSystemTimes(self.ct.byref(idle), self.ct.byref(kernel), self.ct.byref(user))
        ticks = lambda f: (f.hi << 32) | f.lo  # noqa: E731
        return ticks(idle), ticks(kernel), ticks(user)

    def _refresh_gpu(self) -> None:
        """Add a counter for each 3D-engine instance that appeared (the set changes as processes come and go)."""
        if not self.gpu_on:
            return
        ct, w = self.ct, self.w
        path, size = r"\GPU Engine(*)\Utilization Percentage", w.DWORD(0)
        self.pdh.PdhExpandWildCardPathW(None, path, None, ct.byref(size), 0)
        if not size.value:
            return
        buf = ct.create_unicode_buffer(size.value)
        if self.pdh.PdhExpandWildCardPathW(None, path, buf, ct.byref(size), 0) != 0:
            return
        for item in (i for i in buf[:size.value].split("\0") if i):
            if "engtype_3D" not in item or item in self.gpu_counters:
                continue
            try:
                pid = int(item.split("pid_")[1].split("_")[0])
            except (IndexError, ValueError):
                continue
            handle = ct.c_void_p()
            if self.pdh.PdhAddEnglishCounterW(self.pdh_query, item, 0, ct.byref(handle)) == 0:
                self.gpu_counters[item] = (pid, handle)
        self._gpu_refreshed = time.monotonic()

    def _value(self, handle) -> float:
        value, kind = self.VAL(), self.w.DWORD()
        if self.pdh.PdhGetFormattedCounterValue(handle, 0x8200, self.ct.byref(kind), self.ct.byref(value)) != 0:
            return 0.0
        return value.v if value.status in (0, 1) else 0.0

    def _scan(self, first: bool = False) -> dict[int, tuple[str, int]]:
        """The process list (pid -> name, parent pid); starts and exits since the last scan go to the pending counts."""
        snap = self.k32.CreateToolhelp32Snapshot(2, 0)
        now: dict[int, tuple[str, int]] = {}
        if snap in (None, self.ct.c_void_p(-1).value):
            return self.scan_names
        entry = self.PE()
        entry.dwSize = self.ct.sizeof(entry)
        ok = self.k32.Process32FirstW(snap, self.ct.byref(entry))
        while ok:
            now[entry.pid] = (entry.exe, entry.ppid)
            ok = self.k32.Process32NextW(snap, self.ct.byref(entry))
        self.k32.CloseHandle(snap)
        if not first:
            for pid, (name, ppid) in now.items():
                if pid not in self.scan_names or self.scan_names[pid][0] != name:
                    parent = now.get(ppid, ("?", 0))[0]
                    self.pending_starts[f"{name}<-{parent}"] += 1
            self.pending_exits += sum(1 for pid in self.scan_names if pid not in now)
        self.scan_names = now
        return now

    def scan(self) -> None:
        self._scan()

    def _measure(self, pid: int):
        k, ct = self.k32, self.ct
        handle = k.OpenProcess(0x1000, False, pid)
        if not handle:
            return None
        try:
            created, exited, kernel, user = self.FT(), self.FT(), self.FT(), self.FT()
            if not k.GetProcessTimes(handle, *(ct.byref(f) for f in (created, exited, kernel, user))):
                return None
            io, mem = self.IO(), self.MEM()
            mem.cb = ct.sizeof(mem)
            read_io = k.GetProcessIoCounters(handle, ct.byref(io))
            read_mem = k.K32GetProcessMemoryInfo(handle, ct.byref(mem), mem.cb)
            cpu = ((kernel.hi << 32) | kernel.lo) + ((user.hi << 32) | user.lo)
            return ((created.hi << 32) | created.lo, cpu, (io.rb + io.wb + io.ob) if read_io else 0,
                    mem.private if read_mem else 0, k.GetPriorityClass(handle))
        finally:
            k.CloseHandle(handle)

    def sample(self) -> dict:
        self._scan()
        names = self.scan_names
        t = time.perf_counter()
        dt, self.prev_t = max(1e-3, t - self.prev_t), t
        idle, kernel, user = self._system_times()
        p_idle, p_kernel, p_user = self.prev_sys
        self.prev_sys = (idle, kernel, user)
        total = max(1, (kernel + user) - (p_kernel + p_user))
        cpu = 100.0 * (1.0 - (idle - p_idle) / total)
        self.pdh.PdhCollectQueryData(self.pdh_query)
        if self.gpu_on and time.monotonic() - self._gpu_refreshed > 15:
            self._refresh_gpu()
        gpu_by_pid: dict[int, float] = collections.defaultdict(float)
        for pid, handle in self.gpu_counters.values():
            gpu_by_pid[pid] += self._value(handle)
        procs, seen, accounted = [], {}, 0.0
        for pid, (name, _ppid) in names.items():
            if pid == 0:
                continue
            m = self._measure(pid)
            if m is None:
                continue
            created, ticks, io_bytes, private, prio = m
            key = (pid, created)
            seen[key] = (ticks, io_bytes)
            before = self.prev_proc.get(key)
            if before is None:
                continue
            cores = (ticks - before[0]) / 1e7 / dt
            io_mb = (io_bytes - before[1]) / 1048576.0
            accounted += cores
            if cores >= PROC_MIN_CORES or io_mb >= PROC_MIN_IO_MB:
                row = {"pid": pid, "name": name, "cores": round(cores, 2), "priv_mb": round(private / 1048576.0),
                       "io_mb": round(io_mb, 1), "prio": PRIORITY_NAMES.get(prio, hex(prio))}
                if gpu_by_pid.get(pid, 0.0) >= 0.5:
                    row["gpu"] = round(gpu_by_pid[pid], 1)
                procs.append(row)
        self.prev_proc = seen
        with self.probe_lock:
            lat, self.probe_max = self.probe_max, 0.0
        starts, exits = dict(self.pending_starts), self.pending_exits
        self.pending_starts, self.pending_exits = collections.Counter(), 0
        value = lambda key: self._value(self.counters[key]) if key in self.counters else 0.0  # noqa: E731
        return {
            "t": round(time.time(), 3), "time": datetime.now().strftime("%H:%M:%S"), "dt": round(dt, 3),
            "cpu": round(cpu, 1), "queue": round(value("queue"), 1), "ctx": round(value("ctx")),
            "dpc": round(value("dpc"), 2), "int": round(value("int"), 2), "dpcs": round(value("dpcs")),
            "disk_q": round(value("disk_q"), 2), "disk_ms": round(value("disk_s") * 1000.0, 2),
            "disk_mb_s": round(value("disk_b") / 1048576.0, 1),
            "gpu": round(min(100.0, sum(gpu_by_pid.values())), 1), "lat_ms": round(lat, 1),
            "other_cpu": round(max(0.0, cpu - 100.0 * accounted / self.cores), 1),
            "procs": sorted(procs, key=lambda p: -p["cores"]), "starts": starts,
            "n_starts": sum(starts.values()), "n_exits": exits, "nproc": len(names),
            "n_ts3w": sum(1 for name, _ in names.values() if name.lower() == "ts3w.exe"),
        }


# --- CLI ---------------------------------------------------------------------------------------------------------------

def run_dir() -> Path:
    env = os.environ.get("SIMSONLINE_RUN_DIR")
    if env:
        return Path(env)
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    from stack_claim import main_checkout
    return Path(main_checkout()) / ".omo" / "run"


def cmd_run(args) -> int:
    if os.name != "nt":
        print("hitch_watch run: Windows only (performance counters + Win32 process APIs)", file=sys.stderr)
        return 2
    out = Path(args.out) if args.out else run_dir() / "hitch" / f"{args.label}-{datetime.now(timezone.utc):%Y%m%dT%H%M%SZ}.jsonl"
    out.parent.mkdir(parents=True, exist_ok=True)
    collector = Collector(gpu=not args.no_gpu, probe=not args.no_probe)
    own = os.getpid()
    header = {"kind": "header", "label": args.label, "cores": collector.cores, "interval": args.interval,
              "started": datetime.now(timezone.utc).isoformat(timespec="seconds"), "pid": own,
              "limits": {"cpu": CPU_SPIKE, "queue_factor": QUEUE_FACTOR, "burst": START_BURST, "late_ms": LATE_MS}}
    samples: list[dict] = []
    print(f"hitch_watch: {out} ({collector.cores} logical cores, every {args.interval} s"
          f"{', ' + str(args.seconds) + ' s' if args.seconds else ', until Ctrl+C'})", flush=True)
    start = time.monotonic()
    tick = 1.0 / SCAN_HZ
    scans_per_sample = max(1, round(args.interval * SCAN_HZ))
    try:
        with out.open("w", encoding="utf-8", newline="\n") as handle:
            handle.write(json.dumps(header) + "\n")
            deadline = start
            while not args.seconds or time.monotonic() - start < args.seconds:
                for _ in range(scans_per_sample - 1):
                    deadline += tick
                    time.sleep(max(0.0, deadline - time.monotonic()))
                    collector.scan()
                deadline += tick
                time.sleep(max(0.0, deadline - time.monotonic()))
                sample = collector.sample()
                samples.append(sample)
                handle.write(json.dumps(sample, separators=(",", ":")) + "\n")
                handle.flush()
                reasons = find_spikes(sample, collector.cores)
                if reasons and not args.quiet:
                    print(describe_spike(sample, reasons), flush=True)
    except KeyboardInterrupt:
        pass
    finally:
        collector.close()
    own_cores = [p["cores"] for s in samples for p in s["procs"] if p["pid"] == own]
    print(format_summary(args.label, summarize(samples, collector.cores)))
    print(f"hitch_watch itself: {sum(own_cores) / max(1, len(samples)):.3f} cores mean (rows under {PROC_MIN_CORES} not logged)")
    return 0


def cmd_report(args) -> int:
    rows = []
    for path in args.files:
        cores, samples = load_log(Path(path))
        summary = summarize(samples, cores)
        rows.append((Path(path).name, summary))
        print(format_summary(Path(path).name, summary, args.spike_lines))
        print()
    if len(rows) > 1:
        keys = ("seconds", "cpu_mean", "cpu_p95", "queue_p95", "ctx_p95", "dpc_max", "disk_ms_p95", "gpu_mean",
                "lat_ms_p99", "lat_ms_max", "late_seconds", "starts_per_min", "spike_seconds", "other_cpu_mean")
        print("metric".ljust(16) + "".join(name[:28].rjust(30) for name, _ in rows))
        for key in keys:
            print(key.ljust(16) + "".join(str(s[key]).rjust(30) for _, s in rows))
    return 0


def cmd_counts(args) -> int:
    samples: list[dict] = []
    for path in args.files:
        samples += load_log(Path(path))[1]
    print(f"{len(samples)} seconds from {len(args.files)} log(s); build = >= {args.build_floor:g} build core(s)")
    print(format_counts(by_ts3w_count(samples, args.build_floor)))
    return 0


def cmd_spikes(args) -> int:
    cores, samples = load_log(Path(args.file))
    for sample in samples:
        reasons = find_spikes(sample, cores)
        if reasons:
            print(describe_spike(sample, reasons))
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Sample CPU, queue, DPC, disk, GPU and process churn once a second.")
    sub = parser.add_subparsers(dest="cmd", required=True)
    run = sub.add_parser("run", help="sample until --seconds or Ctrl+C")
    run.add_argument("--seconds", type=float, default=0.0)
    run.add_argument("--label", default="watch")
    run.add_argument("--interval", type=float, default=1.0)
    run.add_argument("--out", help="log path (default <run dir>/hitch/<label>-<stamp>.jsonl)")
    run.add_argument("--no-gpu", action="store_true")
    run.add_argument("--no-probe", action="store_true", help="skip the wake-latency probe thread")
    run.add_argument("--quiet", action="store_true", help="do not print spikes as they happen")
    run.set_defaults(func=cmd_run)
    report = sub.add_parser("report", help="summarize one or more logs")
    report.add_argument("files", nargs="+")
    report.add_argument("--spike-lines", type=int, default=12)
    report.set_defaults(func=cmd_report)
    counts = sub.add_parser("counts", help="stutter metrics by TS3W count, build seconds apart from quiet ones")
    counts.add_argument("files", nargs="+")
    counts.add_argument("--build-floor", type=float, default=BUILD_CORES)
    counts.set_defaults(func=cmd_counts)
    spikes = sub.add_parser("spikes", help="list a log's spike seconds")
    spikes.add_argument("file")
    spikes.set_defaults(func=cmd_spikes)
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
