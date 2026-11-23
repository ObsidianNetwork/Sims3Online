#!/usr/bin/env python3
"""Record desktop stutters without admin rights or touching games.

record --detach; mark [note]; status; stop. Ctrl+Alt+F11 marks a 30s-before,
10s-after snapshot. Default storage: this checkout's .omo/run/stutter.
"""
from __future__ import annotations

import argparse
import collections
import ctypes
import hashlib
import json
import os
import subprocess
import sys
import tempfile
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path

try:
    from . import frame_pacing as fp, hitch_watch as hw
except ImportError:
    import frame_pacing as fp
    import hitch_watch as hw

ROOT = Path(__file__).resolve().parents[1] / ".omo/run/stutter"
BEFORE, AFTER, RETAIN = 30, 10, 600


def save(path: Path, value: dict) -> None:
    # Unique sibling files keep concurrent writers separate and replace atomically.
    with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent,
                                     prefix=path.name + ".", suffix=".tmp", delete=False) as stream:
        temporary = Path(stream.name)
        json.dump(value, stream, indent=2)
    try:
        for attempt in range(6):
            try:
                temporary.replace(path)
                return
            except PermissionError:
                if attempt == 5:
                    raise
                time.sleep(.02)
    finally:
        temporary.unlink(missing_ok=True)


def read(path: Path) -> dict:
    for attempt in range(6):
        try:
            return json.loads(path.read_text(encoding="utf-8"))
        except FileNotFoundError:
            return {}
        except PermissionError:
            if attempt == 5:
                raise
            time.sleep(.02)


class RecorderProcess:
    """Hold one process object while checking its creation time and stopping it."""

    def __init__(self, pid: int, terminate: bool = False):
        k = self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        handle, dword = ctypes.c_void_p, ctypes.c_uint32
        k.OpenProcess.argtypes, k.OpenProcess.restype = [dword, ctypes.c_int, dword], handle
        k.GetProcessTimes.argtypes = [handle] + [ctypes.POINTER(ctypes.c_ulonglong)] * 4
        k.WaitForSingleObject.argtypes, k.WaitForSingleObject.restype = [handle, dword], dword
        k.TerminateProcess.argtypes = [handle, dword]
        k.CloseHandle.argtypes = [handle]
        # QUERY_LIMITED_INFORMATION | SYNCHRONIZE; TERMINATE only for stop.
        self.handle = k.OpenProcess(0x1000 | 0x100000 | int(terminate), False, pid)
        if not self.handle:
            raise ctypes.WinError(ctypes.get_last_error())
        times = [ctypes.c_ulonglong() for _ in range(4)]
        if not k.GetProcessTimes(self.handle, *(ctypes.byref(value) for value in times)):
            error = ctypes.WinError(ctypes.get_last_error())
            k.CloseHandle(self.handle)
            raise error
        self.started = times[0].value  # Raw FILETIME; no rounding of process identity.

    def wait(self, seconds: float) -> bool:
        result = self.kernel.WaitForSingleObject(self.handle, int(seconds * 1000))
        if result not in (0, 258):
            raise ctypes.WinError(ctypes.get_last_error())
        return result == 0

    def terminate(self) -> None:
        if not self.kernel.TerminateProcess(self.handle, 1):
            raise ctypes.WinError(ctypes.get_last_error())
        if not self.wait(5):
            raise RuntimeError("recorder termination not confirmed")

    def __enter__(self):
        return self

    def __exit__(self, *_exc):
        self.kernel.CloseHandle(self.handle)


def adapter_id(high: int, low: int) -> str:
    return f"0x{high & 0xffffffff:08X}_0x{low & 0xffffffff:08X}"


def engine_adapter(instance: dict) -> str:
    high, low = instance["engine"].split("_")[:2]
    return adapter_id(int(high, 16), int(low, 16))


def gpu_loads(values: list[tuple[str, float]], names: dict[int, str]) -> tuple[list, list]:
    """Keep adapter identity; saturation is max(engine sums), not sum(all engines)."""
    engines = collections.defaultdict(float)
    processes = collections.defaultdict(lambda: collections.defaultdict(float))
    for name, value in values:
        instance = fp.parse_engine_instance(name)
        if instance is None:
            continue
        luid = engine_adapter(instance)
        engines[(luid, instance["cls"], instance["engine"])] += value
        processes[(instance["pid"], luid)][instance["cls"]] += value
    adapters = collections.defaultdict(lambda: {key: 0.0 for key in fp.ENGINE_CLASSES})
    for (luid, kind, _engine), value in engines.items():
        adapters[luid][kind] = max(adapters[luid][kind], round(value, 2))
    rows = [{"pid": pid, "name": names.get(pid, "?"), "adapter": luid,
             **{key: round(value, 2) for key, value in loads.items()}}
            for (pid, luid), loads in processes.items() if sum(loads.values()) > 0]
    return [{"adapter": luid, **loads} for luid, loads in sorted(adapters.items())], rows


def cross_adapter(sample: dict) -> list[dict]:
    """Evidence of render/display mismatch, not proof of cross-adapter copies."""
    found = []
    for window in sample.get("windows", []):
        display = window.get("adapter")
        renders = sorted({p["adapter"] for p in sample.get("gpu_processes", [])
                          if p["pid"] == window["pid"] and p.get("3D", 0) > 0})
        if display and renders and any(adapter != display for adapter in renders):
            found.append({"pid": window["pid"], "monitor": window["monitor"],
                          "display_adapter": display, "render_adapters": renders})
    return found


class Buffer:
    """Append only second buckets, prune on second boundaries, retain 10 minutes."""

    def __init__(self, root: Path, session: str, retain: int = RETAIN):
        self.root, self.session, self.retain = root, session, retain
        self.rows: collections.deque = collections.deque()
        self.bucket = None
        self.stream = None
        root.mkdir(parents=True, exist_ok=True)

    def append(self, row: dict) -> None:
        second = int(row["t"])
        if second != self.bucket:
            if self.stream:
                self.stream.close()
            self.bucket = second
            for path in self.root.glob("*.jsonl"):
                if int(path.stem) < second - self.retain:
                    path.unlink()
            self.stream = (self.root / f"{second}.jsonl").open("a", encoding="utf-8")
        row = {**row, "session": self.session}
        self.rows.append(row)
        while self.rows and self.rows[0]["t"] < row["t"] - self.retain:
            self.rows.popleft()
        self.stream.write(json.dumps(row, separators=(",", ":")) + "\n")
        self.stream.flush()

    def window(self, at: float) -> list[dict]:
        return [row for row in self.rows if at - BEFORE <= row["t"] <= at + AFTER]

    def close(self) -> None:
        if self.stream:
            self.stream.close()


def summary(rows: list[dict], mark: dict) -> str:
    near = [row for row in rows if abs(row["t"] - mark["t"]) <= 1]
    baseline = [row for row in rows if row["t"] < mark["t"] - 1]
    lines = [f"STUTTER {mark['note']} ({len(rows)} samples, {len(near)} within +/-1s)",
             "Resource peaks near mark; baseline is mean of earlier samples."]
    for key, unit in (("cpu", "%"), ("queue", " runnable"), ("dpc", "% DPC"), ("int", "% IRQ"),
                      ("lat_ms", " ms wake"), ("flush_max_ms", " ms compositor")):
        peak = max((r.get(key, 0) for r in near), default=0)
        mean = sum(r.get(key, 0) for r in baseline) / max(1, len(baseline))
        lines.append(f"  {key}: {peak:.2f}{unit}; baseline {mean:.2f}")
    cpu, io = {}, {}
    gpu = {kind: {} for kind in ("3D", "Copy", "Video")}
    for row in near:
        for proc in row["procs"]:
            label = f"{proc['name']}[{proc['pid']}]"
            cpu[label] = max(cpu.get(label, 0), proc["cores"])
            io[label] = max(io.get(label, 0), proc.get("io_mb", 0) / row["dt"])
        for proc in row["gpu_processes"]:
            for kind in ("3D", "Copy", "Video"):
                label = f"{proc['name']}[{proc['pid']}] {proc['adapter']} {kind}"
                gpu[kind][label] = max(gpu[kind].get(label, 0), proc.get(kind, 0))
    resources = [("CPU", cpu, "c"), ("IO", io, " MB/s")]
    resources += [("GPU " + kind, values, "%") for kind, values in gpu.items()]
    for title, values, unit in resources:
        values = {name: value for name, value in values.items() if value > 0}
        top = sorted(values.items(), key=lambda item: -item[1])[:3]
        lines.append(title + ": " + (", ".join(f"{name} {value:.2f}{unit}" for name, value in top) or "none observed"))
    adapter_peaks = collections.defaultdict(lambda: collections.defaultdict(float))
    for row in near:
        for adapter in row.get("gpu_adapters", []):
            for kind in ("3D", "Copy", "Video"):
                adapter_peaks[adapter["adapter"]][kind] = max(adapter_peaks[adapter["adapter"]][kind], adapter[kind])
    for luid, loads in sorted(adapter_peaks.items())[:3]:
        lines.append(luid + " busiest engines: " + ", ".join(f"{kind} {value:.1f}%" for kind, value in loads.items()))
    mismatches = {json.dumps(hit, sort_keys=True) for row in near for hit in cross_adapter(row)}
    lines.append("Render/display mismatch: " + ("; ".join(sorted(mismatches)) if mismatches else
                 "none observed (unknown/idle render adapters cannot be cleared)"))
    lines.append("DWM NULL-window counters are not reliable missed-frame measurements; flush is a desktop proxy.")
    lines.append("GPU engine use and mismatches suggest contributors; they do not prove the cause of a stutter.")
    return "\n".join(lines) + "\n"


def snapshot(root: Path, buffer: Buffer, mark: dict, complete: bool) -> Path:
    stamp = datetime.fromtimestamp(mark["t"], timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
    folder = root / (stamp + "-" + mark["id"][:8])
    folder.mkdir()
    rows = buffer.window(mark["t"])
    (folder / "samples.jsonl").write_text("".join(json.dumps(r) + "\n" for r in rows), encoding="utf-8")
    (folder / "summary.txt").write_text(summary(rows, mark), encoding="utf-8")
    save(folder / "mark.json", {**mark, "complete": complete,
                               "pre_seconds": max(0, mark["t"] - rows[0]["t"]) if rows else 0,
                               "post_seconds": max(0, rows[-1]["t"] - mark["t"]) if rows else 0})
    return folder


class Displays:
    """Map each Canvas HWND to its monitor and the display adapter's actual LUID."""

    def __init__(self, frame):
        from ctypes import wintypes as w
        self.frame, self.w = frame, w
        self.u = frame.user32
        self.gdi = ctypes.WinDLL("gdi32")

        class Monitor(ctypes.Structure):
            _fields_ = [("size", w.DWORD), ("rect", w.RECT), ("work", w.RECT),
                        ("flags", w.DWORD), ("device", w.WCHAR * 32)]

        class Open(ctypes.Structure):
            _fields_ = [("device", w.WCHAR * 32), ("handle", w.UINT),
                        ("low", w.DWORD), ("high", w.LONG), ("source", w.UINT)]

        self.Monitor, self.Open = Monitor, Open
        self.u.MonitorFromWindow.argtypes = [w.HWND, w.DWORD]
        self.u.MonitorFromWindow.restype = w.HANDLE
        self.u.GetMonitorInfoW.argtypes = [w.HANDLE, ctypes.c_void_p]
        self.gdi.D3DKMTOpenAdapterFromGdiDisplayName.argtypes = [ctypes.POINTER(Open)]
        self.gdi.D3DKMTCloseAdapter.argtypes = [ctypes.POINTER(w.UINT)]
        self.monitor_proc = ctypes.WINFUNCTYPE(w.BOOL, w.HANDLE, w.HDC, ctypes.POINTER(w.RECT), w.LPARAM)
        self.u.EnumDisplayMonitors.argtypes = [w.HDC, ctypes.c_void_p, self.monitor_proc, w.LPARAM]
        self.paths = {}
        self.path_status = None
        self._configure_paths()

    def _configure_paths(self) -> None:
        w = self.w

        class Luid(ctypes.Structure):
            _fields_ = [("low", w.DWORD), ("high", w.LONG)]

        class Source(ctypes.Structure):
            _fields_ = [("adapter", Luid), ("id", w.UINT), ("mode", w.UINT), ("status", w.UINT)]

        class Target(ctypes.Structure):
            _fields_ = [("adapter", Luid), ("id", w.UINT), ("mode", w.UINT),
                        ("technology", w.UINT), ("rotation", w.UINT), ("scaling", w.UINT),
                        ("numerator", w.UINT), ("denominator", w.UINT),
                        ("scanline", w.UINT), ("available", w.BOOL), ("status", w.UINT)]

        class DisplayPath(ctypes.Structure):
            _fields_ = [("source", Source), ("target", Target), ("flags", w.UINT)]

        class Mode(ctypes.Structure):
            _fields_ = [("kind", w.UINT), ("id", w.UINT), ("adapter", Luid), ("data", ctypes.c_byte * 48)]

        class SourceName(ctypes.Structure):
            _fields_ = [("kind", w.UINT), ("size", w.UINT), ("adapter", Luid), ("id", w.UINT),
                        ("name", w.WCHAR * 32)]

        u = self.u
        u.GetDisplayConfigBufferSizes.argtypes = [w.UINT, ctypes.POINTER(w.UINT), ctypes.POINTER(w.UINT)]
        u.QueryDisplayConfig.argtypes = [w.UINT, ctypes.POINTER(w.UINT), ctypes.POINTER(DisplayPath),
                                        ctypes.POINTER(w.UINT), ctypes.POINTER(Mode), ctypes.c_void_p]
        u.DisplayConfigGetDeviceInfo.argtypes = [ctypes.POINTER(SourceName)]
        self.path_types = DisplayPath, Mode, SourceName

    def display_paths(self) -> tuple[int, dict]:
        """Query the output adapter, which can differ from GDI's rendering adapter."""
        w, u = self.w, self.u
        DisplayPath, Mode, SourceName = self.path_types
        count, modes = w.UINT(), w.UINT()
        status = u.GetDisplayConfigBufferSizes(2, ctypes.byref(count), ctypes.byref(modes))
        if status:
            return status, {}
        paths, mode_info = (DisplayPath * count.value)(), (Mode * modes.value)()
        status = u.QueryDisplayConfig(2, ctypes.byref(count), paths, ctypes.byref(modes), mode_info, None)
        if status:
            return status, {}
        rows = {}
        for path in paths[:count.value]:
            name = SourceName()
            name.kind, name.size = 1, ctypes.sizeof(name)
            name.adapter, name.id = path.source.adapter, path.source.id
            if u.DisplayConfigGetDeviceInfo(ctypes.byref(name)) == 0:
                rows[name.name] = {"adapter": adapter_id(path.target.adapter.high, path.target.adapter.low),
                                   "source_adapter": adapter_id(path.source.adapter.high, path.source.adapter.low)}
        return 0, rows

    def identity(self, monitor) -> dict:
        info = self.Monitor()
        info.size = ctypes.sizeof(info)
        row = {"monitor": None, "adapter": None, "display_config_status": self.path_status}
        if self.u.GetMonitorInfoW(monitor, ctypes.byref(info)):
            row["monitor"] = info.device
            row["rect"] = [info.rect.left, info.rect.top, info.rect.right, info.rect.bottom]
            opened = self.Open()
            opened.device = info.device
            status = self.gdi.D3DKMTOpenAdapterFromGdiDisplayName(ctypes.byref(opened))
            row["adapter_status"] = status
            if status == 0:
                row["gdi_adapter"] = adapter_id(opened.high, opened.low)
                handle = self.w.UINT(opened.handle)
                self.gdi.D3DKMTCloseAdapter(ctypes.byref(handle))
            row.update(self.paths.get(info.device, {}))
        return row

    def inventory(self) -> list[dict]:
        self.path_status, self.paths = self.display_paths()
        rows = []

        def visit(monitor, _dc, _rect, _param):
            rows.append(self.identity(monitor))
            return True

        self.u.EnumDisplayMonitors(None, None, self.monitor_proc(visit), 0)
        return rows

    def sample(self, pids: set[int]) -> list[dict]:
        rows = []

        def visit(hwnd, _param):
            pid = self.w.DWORD()
            self.u.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
            if pid.value not in pids:
                return True
            name = ctypes.create_unicode_buffer(256)
            self.u.GetClassNameW(hwnd, name, len(name))
            if name.value.rsplit(":", 1)[-1] != "Canvas":
                return True
            monitor = self.u.MonitorFromWindow(hwnd, 2)
            rows.append({"pid": pid.value, "hwnd": hwnd, **self.identity(monitor)})
            return True

        self.u.EnumWindows(self.frame.enum_proc(visit), 0)
        states = self.frame._windows(pids)
        for row in rows:
            row.update(states.get(row["pid"], {}))
        return rows


class Collector:
    def __init__(self):
        self.started, self.cpu_started = time.monotonic(), time.process_time()
        self.cpu = hw.Collector(gpu=False)
        self.frame = fp.Collector()
        self.displays = Displays(self.frame)
        self.cpu_row = None
        self.cpu_second = None
        self.monitors = []

    def sample(self) -> dict:
        now = time.monotonic()
        # Enumerating/querying every process dominated measured cost at 4 Hz.
        # Keep detailed CPU/PDH reads at 1 Hz, with timestamps; GPU and DWM stay at 4-10 Hz.
        if int(now) != self.cpu_second:
            self.cpu_row = self.cpu.sample()
            self.cpu_second = int(now)
            self.monitors = self.displays.inventory()
        else:
            self.cpu.scan()
        row = self.cpu_row
        frame = self.frame
        frame.pdh.PdhCollectQueryData(frame.pdh_query)
        names = {pid: value[0] for pid, value in self.cpu.scan_names.items()}
        adapters, processes = gpu_loads(frame._array("engine"), names)
        with frame.lock:
            intervals, frame.intervals = frame.intervals, []
        dwm_now = frame._dwm()
        dwm = fp.dwm_delta(frame.prev_dwm, dwm_now) if dwm_now else None
        frame.prev_dwm = dwm_now
        elapsed = time.monotonic() - self.started
        return {**row, "t": round(time.time(), 3), "cpu_t": row["t"],
                "gpu_adapters": adapters, "gpu_processes": processes, "monitors": self.monitors,
                "windows": self.displays.sample({pid for pid, name in names.items() if name.lower() == "ts3w.exe"}),
                "flush": fp.flush_stats(intervals), "flush_max_ms": max(intervals, default=0),
                "flush_ok": frame.flush_ok, "dwm": dwm,
                "recorder_cores": (time.process_time() - self.cpu_started) / max(elapsed, .001)}

    def close(self):
        self.cpu.close()
        self.frame.close()
        self.cpu.pdh.PdhCloseQuery(self.cpu.pdh_query)
        self.frame.pdh.PdhCloseQuery(self.frame.pdh_query)


def active(root: Path) -> dict:
    state = read(root / "status.json")
    if not state.get("running") or not state.get("started"):
        return {}
    try:
        with RecorderProcess(state["pid"]) as process:
            return state if process.started == state["started"] and not process.wait(0) else {}
    except OSError:
        return {}


def stop(root: Path) -> None:
    state = read(root / "status.json")
    if not state.get("running"):
        raise RuntimeError("recorder is not running")
    if not state.get("started"):
        raise RuntimeError("recorder creation time is missing; refusing to stop an unidentified process")
    with RecorderProcess(state["pid"], terminate=True) as process:
        if process.started != state["started"]:
            raise RuntimeError("recorder PID was reused; refusing to stop another process")
        save(root / "stop.json", {"session": state["session"]})
        if not process.wait(10):
            process.terminate()
            # A forced exit cannot publish its final state. Leave a truthful receipt.
            current = read(root / "status.json")
            if (current.get("pid"), current.get("started")) == (state["pid"], state["started"]):
                save(root / "status.json", {**current, "running": False, "forced": True, "t": time.time()})


def request_mark(root: Path, note: str, at: float | None = None) -> dict:
    state = active(root)
    if not state:
        raise RuntimeError("recorder is not running")
    mark = {"id": uuid.uuid4().hex, "t": time.time() if at is None else at,
            "note": note, "session": state["session"]}
    save(root / "requests" / (mark["id"] + ".json"), mark)
    return mark


def record(args) -> int:
    from ctypes import wintypes as w
    root = args.root
    root.mkdir(parents=True, exist_ok=True)
    (root / "requests").mkdir(exist_ok=True)
    # OS-owned mutex excludes both detached and foreground recorders, including stale status files.
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateMutexW.argtypes = [ctypes.c_void_p, w.BOOL, w.LPCWSTR]
    kernel.CreateMutexW.restype = w.HANDLE
    kernel.CloseHandle.argtypes = [w.HANDLE]
    kernel.GetTickCount64.restype = ctypes.c_uint64
    digest = hashlib.sha256(str(root.resolve()).lower().encode()).hexdigest()[:16]
    mutex = kernel.CreateMutexW(None, False, "Local\\SimsOnlineStutter-" + digest)
    if not mutex:
        raise ctypes.WinError(ctypes.get_last_error())
    if ctypes.get_last_error() == 183:
        kernel.CloseHandle(mutex)
        raise RuntimeError("recorder already running")
    session = uuid.uuid4().hex
    buffer = Buffer(root / "buffer", session)
    collector = None
    user = ctypes.WinDLL("user32", use_last_error=True)
    user.RegisterHotKey.argtypes = [w.HWND, ctypes.c_int, w.UINT, w.UINT]
    user.UnregisterHotKey.argtypes = [w.HWND, ctypes.c_int]
    user.PeekMessageW.argtypes = [ctypes.POINTER(w.MSG), w.HWND, w.UINT, w.UINT, w.UINT]
    hotkey = False
    pending = []
    state = {"running": True, "session": session, "pid": os.getpid(),
             "thread": kernel.GetCurrentThreadId(), "hz": args.hz, "hotkey": "Ctrl+Alt+F11", "ready": False}
    with RecorderProcess(state["pid"]) as process:
        state["started"] = process.started
    start = time.monotonic()
    try:
        # Publish ownership before initializing collectors, which can stall.
        state["t"] = time.time()
        save(root / "status.json", state)
        hotkey = bool(user.RegisterHotKey(None, 1, 0x4003, 0x7A))
        state["hotkey_registered"] = hotkey
        state["hotkey_error"] = 0 if hotkey else ctypes.get_last_error()
        collector = Collector()
        state["ready"] = True
        print("recording; Ctrl+Alt+F11 marks; " + str(root), flush=True)
        if not hotkey:
            print("Hotkey unavailable; use mark. Win32 error " + str(state["hotkey_error"]), flush=True)
        deadline = time.monotonic()
        while not args.seconds or time.monotonic() - start < args.seconds:
            row = collector.sample()
            buffer.append(row)
            state.update(t=time.time(), samples=len(buffer.rows), recorder_cores=row["recorder_cores"],
                         n_ts3w=row["n_ts3w"], monitors=row["monitors"])
            save(root / "status.json", state)
            message = w.MSG()
            while user.PeekMessageW(ctypes.byref(message), None, 0x312, 0x312, 1):
                age_ms = (kernel.GetTickCount64() - message.time) & 0xffffffff
                request_mark(root, "hotkey", time.time() - age_ms / 1000)
            for path in (root / "requests").glob("*.json"):
                mark = read(path)
                if mark.get("session") == session:
                    pending.append(mark)
                path.unlink()
            for mark in pending[:]:
                if row["t"] >= mark["t"] + AFTER:
                    folder = snapshot(root, buffer, mark, True)
                    save(root / (mark["id"] + ".result.json"), {"folder": str(folder)})
                    pending.remove(mark)
                    print(str(folder), flush=True)
            if read(root / "stop.json").get("session") == session:
                break
            deadline += 1 / args.hz
            time.sleep(max(0, deadline - time.monotonic()))
    except KeyboardInterrupt:
        pass
    finally:
        for mark in pending:
            folder = snapshot(root, buffer, mark, False)
            save(root / (mark["id"] + ".result.json"), {"folder": str(folder)})
        if collector:
            collector.close()
        buffer.close()
        if hotkey:
            user.UnregisterHotKey(None, 1)
        state.update(running=False, t=time.time())
        save(root / "status.json", state)
        kernel.CloseHandle(mutex)
    return 0


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=ROOT, help="storage directory (use same path for every command)")
    sub = parser.add_subparsers(dest="command", required=True)
    rec = sub.add_parser("record")
    rec.add_argument("--detach", action="store_true")
    rec.add_argument("--hz", type=int, choices=range(4, 11), default=4)
    rec.add_argument("--seconds", type=float, default=0, help="bounded observation; 0 runs until stop")
    mark = sub.add_parser("mark")
    mark.add_argument("note", nargs="*", default=[])
    sub.add_parser("status")
    sub.add_parser("stop")
    args = parser.parse_args(argv)
    args.root = args.root.resolve()
    if os.name != "nt":
        parser.error("Windows only")
    try:
        if args.command == "status":
            print(json.dumps(active(args.root) or {"running": False, "last": read(args.root / "status.json")}, indent=2))
        elif args.command == "record" and args.detach:
            if active(args.root):
                raise RuntimeError("recorder already running")
            args.root.mkdir(parents=True, exist_ok=True)
            with (args.root / "recorder.log").open("a", encoding="utf-8") as log:
                process = subprocess.Popen([sys.executable, str(Path(__file__).resolve()), "--root", str(args.root),
                                            "record", "--hz", str(args.hz), "--seconds", str(args.seconds)],
                                           cwd=Path(__file__).resolve().parents[1], stdin=subprocess.DEVNULL,
                                           stdout=log, stderr=log, encoding="utf-8", errors="replace",
                                           creationflags=subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP)
            confirmed = False
            try:
                end = time.monotonic() + 15
                while time.monotonic() < end:
                    state = active(args.root)
                    if state.get("pid") == process.pid and state.get("ready"):
                        confirmed = True
                        print(json.dumps(state, indent=2))
                        return 0
                    if process.poll() is not None:
                        raise RuntimeError("recorder failed; see recorder.log")
                    time.sleep(.1)
                raise RuntimeError("recorder startup not confirmed; inspect recorder.log")
            finally:
                if not confirmed:
                    # Popen retains the exact child's OS handle, even before status exists.
                    if process.poll() is None:
                        process.terminate()
                    process.wait(timeout=5)
        elif args.command == "record":
            return record(args)
        elif args.command == "stop":
            stop(args.root)
            print("stopped")
        else:
            mark = request_mark(args.root, " ".join(args.note))
            print("mark queued; collecting 10s after", flush=True)
            end = time.monotonic() + AFTER + 10
            result = args.root / (mark["id"] + ".result.json")
            while time.monotonic() < end:
                if result.exists():
                    folder = Path(read(result)["folder"])
                    print((folder / "summary.txt").read_text(encoding="utf-8"))
                    print(folder)
                    return 0
                time.sleep(.1)
            raise RuntimeError("snapshot not confirmed; inspect recorder.log")
        return 0
    except (RuntimeError, OSError, subprocess.TimeoutExpired) as error:
        print(str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
