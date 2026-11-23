"""The machine's load over a span: system CPU % (mean and max), available memory, and the processes that
used the most CPU, with names and pids, so a slow or failed boot can be read against what else the machine was doing.

    python scripts/machine_load.py sample [--seconds 10] [--interval 2] [--json]   # the load now, read by hand
    python -m unittest scripts.machine_load_test

quick_loop's boot samples it from the boot's start to the client's claim or the failure and keeps the summary in that
boot's boot-times.jsonl row (`load`); `quick_loop.py boot-times` prints it under each row. The reads are the system's
own counters and process list, the ones Task Manager reads (GetSystemTimes, GlobalMemoryStatusEx and
NtQuerySystemInformation's process list): no process is opened, so no handle is taken on a game. A process's CPU % is
its share of every logical processor's time over the span, as Task Manager shows it (one busy core of 16 is 6.25%).
"""
from __future__ import annotations

import argparse
import json
import sys
import threading
import time

READER = "GetSystemTimes, GlobalMemoryStatusEx, NtQuerySystemInformation process list (no process handles)"
STACK_NAMES = {"ts3w.exe", "ts3.exe", "simsonline.client.exe"}  # the stack's own games and client bridge
TOP = 5


def utc() -> str:
    """Wall-clock UTC in the controllers' state-file format (quick_loop.utc_now)."""
    now = time.time()
    return time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(now)) + f".{int(now * 1000) % 1000:03d}Z"


# --- the reads (Windows) ---------------------------------------------------------------------------------------------

_native = None


def _load_native():
    """The ctypes structures and functions, made once."""
    global _native
    if _native is not None:
        return _native
    import ctypes
    from ctypes import wintypes

    class FILETIME(ctypes.Structure):
        _fields_ = [("low", wintypes.DWORD), ("high", wintypes.DWORD)]

    class MEMORYSTATUSEX(ctypes.Structure):
        _fields_ = [("dwLength", wintypes.DWORD), ("dwMemoryLoad", wintypes.DWORD), ("ullTotalPhys", ctypes.c_ulonglong),
                    ("ullAvailPhys", ctypes.c_ulonglong), ("ullTotalPageFile", ctypes.c_ulonglong),
                    ("ullAvailPageFile", ctypes.c_ulonglong), ("ullTotalVirtual", ctypes.c_ulonglong),
                    ("ullAvailVirtual", ctypes.c_ulonglong), ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]

    class UNICODE_STRING(ctypes.Structure):
        _fields_ = [("Length", ctypes.c_ushort), ("MaximumLength", ctypes.c_ushort), ("Buffer", ctypes.c_void_p)]

    # winternl.h's SYSTEM_PROCESS_INFORMATION, its Reserved1[48] spelled out (the times are 100 ns units).
    class SYSTEM_PROCESS_INFORMATION(ctypes.Structure):
        _fields_ = [("NextEntryOffset", ctypes.c_ulong), ("NumberOfThreads", ctypes.c_ulong),
                    ("WorkingSetPrivateSize", ctypes.c_longlong), ("HardFaultCount", ctypes.c_ulong),
                    ("NumberOfThreadsHighWatermark", ctypes.c_ulong), ("CycleTime", ctypes.c_ulonglong),
                    ("CreateTime", ctypes.c_longlong), ("UserTime", ctypes.c_longlong), ("KernelTime", ctypes.c_longlong),
                    ("ImageName", UNICODE_STRING), ("BasePriority", ctypes.c_long), ("UniqueProcessId", ctypes.c_void_p),
                    ("InheritedFromUniqueProcessId", ctypes.c_void_p), ("HandleCount", ctypes.c_ulong),
                    ("SessionId", ctypes.c_ulong), ("UniqueProcessKey", ctypes.c_void_p),
                    ("PeakVirtualSize", ctypes.c_size_t), ("VirtualSize", ctypes.c_size_t),
                    ("PageFaultCount", ctypes.c_ulong), ("PeakWorkingSetSize", ctypes.c_size_t),
                    ("WorkingSetSize", ctypes.c_size_t)]

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    ntdll = ctypes.WinDLL("ntdll")
    ntdll.NtQuerySystemInformation.argtypes = [ctypes.c_ulong, ctypes.c_void_p, ctypes.c_ulong, ctypes.POINTER(ctypes.c_ulong)]
    ntdll.NtQuerySystemInformation.restype = ctypes.c_long
    _native = {"ctypes": ctypes, "FILETIME": FILETIME, "MEMORYSTATUSEX": MEMORYSTATUSEX, "SPI": SYSTEM_PROCESS_INFORMATION,
               "kernel32": kernel32, "ntdll": ntdll, "size": 1 << 20}
    return _native


def system_times() -> tuple[int, int, int]:
    """Idle, kernel and user time of every logical processor, 100 ns units (kernel includes idle)."""
    n = _load_native()
    ctypes = n["ctypes"]
    idle, kernel, user = n["FILETIME"](), n["FILETIME"](), n["FILETIME"]()
    if not n["kernel32"].GetSystemTimes(ctypes.byref(idle), ctypes.byref(kernel), ctypes.byref(user)):
        raise OSError(ctypes.get_last_error(), "GetSystemTimes failed")
    return tuple(t.high << 32 | t.low for t in (idle, kernel, user))  # type: ignore[return-value]


def memory() -> tuple[int, int]:
    """Available and total physical memory, bytes."""
    n = _load_native()
    ctypes = n["ctypes"]
    status = n["MEMORYSTATUSEX"]()
    status.dwLength = ctypes.sizeof(status)
    if not n["kernel32"].GlobalMemoryStatusEx(ctypes.byref(status)):
        raise OSError(ctypes.get_last_error(), "GlobalMemoryStatusEx failed")
    return status.ullAvailPhys, status.ullTotalPhys


def processes() -> dict[tuple[int, int], tuple[str, int, int]]:
    """Every process: (pid, create time) -> (image name, kernel + user time in 100 ns, working set bytes). pid 0 is the
    idle process (its time is the processors' idle time)."""
    n = _load_native()
    ctypes = n["ctypes"]
    needed = ctypes.c_ulong(0)
    while True:
        buf = ctypes.create_string_buffer(n["size"])
        status = n["ntdll"].NtQuerySystemInformation(5, buf, n["size"], ctypes.byref(needed))  # SystemProcessInformation
        if status & 0xFFFFFFFF != 0xC0000004:  # STATUS_INFO_LENGTH_MISMATCH: the list grew past the buffer
            break
        n["size"] = max(n["size"] * 2, needed.value + (256 << 10))
    if status != 0:
        raise OSError(f"NtQuerySystemInformation(SystemProcessInformation) returned 0x{status & 0xFFFFFFFF:08X}")
    found: dict[tuple[int, int], tuple[str, int, int]] = {}
    offset = 0
    while True:
        info = n["SPI"].from_buffer(buf, offset)
        pid = info.UniqueProcessId or 0
        name = ctypes.wstring_at(info.ImageName.Buffer, info.ImageName.Length // 2) if info.ImageName.Buffer else (
            "Idle" if pid == 0 else "")
        found[(pid, info.CreateTime)] = (name, info.KernelTime + info.UserTime, info.WorkingSetSize)
        if not info.NextEntryOffset:
            return found
        offset += info.NextEntryOffset


def snapshot() -> dict:
    """One sample: the processors' cumulative times, available memory and the process list."""
    procs = processes()
    idle, kernel, user = system_times()
    avail, total = memory()
    return {"t": time.monotonic(), "idle": idle, "kernel": kernel, "user": user, "avail_mb": avail / 2**20,
            "total_mb": total / 2**20, "procs": procs}


# --- folding samples into the summary --------------------------------------------------------------------------------

class Load:
    """Folds samples (snapshot()'s shape) into the summary one at a time, holding only the running figures, so a long
    boot does not keep every process list."""

    def __init__(self) -> None:
        self.first: dict | None = None
        self.last: dict | None = None
        self.count = 0
        self.cpu_max: float | None = None
        self.avail: dict[str, float] = {}
        self.avail_sum = 0.0
        self.total_mb: float | None = None
        self.procs: dict[tuple[int, int], dict] = {}
        self.series: list[list[float]] = []

    def add(self, sample: dict) -> None:
        first = self.last is None
        span = 0
        if not first:
            assert self.last is not None
            span = total(sample) - total(self.last)
            if span > 0:
                pct = 100 * (busy(sample) - busy(self.last)) / span
                self.cpu_max = pct if self.cpu_max is None else max(self.cpu_max, pct)
                self.series.append([round(sample["t"] - self.first["t"], 1), round(pct, 1), round(sample["avail_mb"])])
        for key, (name, cpu, ws) in sample["procs"].items():
            proc = self.procs.get(key)
            if proc is None:  # in the first sample, its time so far is not the span's; one that started since, all of it is
                proc = self.procs[key] = {"name": name, "pid": key[0], "base": cpu if first else 0, "cpu": cpu if first else 0,
                                          "max": 0.0, "ws": ws}
            if span > 0:
                proc["max"] = max(proc["max"], 100 * (cpu - proc["cpu"]) / span)
            proc["cpu"], proc["ws"] = cpu, ws
        avail = sample["avail_mb"]
        self.avail = {"min": min(self.avail.get("min", avail), avail), "start": self.avail.get("start", avail), "end": avail}
        self.avail_sum += avail
        self.total_mb = sample.get("total_mb", self.total_mb)
        self.count += 1
        self.last = {k: v for k, v in sample.items() if k != "procs"}
        if first:
            self.first = self.last

    def summary(self, top: int = TOP) -> dict:
        """The summary: seconds, samples, cpu_pct {mean, max}, avail_mb {min, mean, start, end}, total_mb, top (by CPU
        over the span) and others (the top that are not the stack's own games), series [[s, cpu %, avail MB], ...]."""
        out: dict = {"samples": self.count, "seconds": 0.0, "cpu_pct": {"mean": None, "max": None}, "avail_mb": None,
                     "total_mb": round(self.total_mb) if self.total_mb else None, "top": [], "others": [], "series": self.series}
        if self.count:
            out["avail_mb"] = {"min": round(self.avail["min"]), "mean": round(self.avail_sum / self.count),
                               "start": round(self.avail["start"]), "end": round(self.avail["end"])}
        if self.first is None or self.last is None or self.count < 2:
            return out
        span = total(self.last) - total(self.first)
        out["seconds"] = round(self.last["t"] - self.first["t"], 1)
        if span <= 0:
            return out
        out["cpu_pct"] = {"mean": round(100 * (busy(self.last) - busy(self.first)) / span, 1), "max": round(self.cpu_max or 0, 1)}
        ranked = sorted((p for p in self.procs.values() if p["pid"] != 0 and p["cpu"] > p["base"]),
                        key=lambda p: p["cpu"] - p["base"], reverse=True)

        def row(p: dict) -> dict:
            used = p["cpu"] - p["base"]
            return {"name": p["name"], "pid": p["pid"], "cpu_pct": round(100 * used / span, 1), "max_pct": round(p["max"], 1),
                    "cpu_s": round(used / 1e7, 1), "ws_mb": round(p["ws"] / 2**20)}

        out["top"] = [row(p) for p in ranked[:top]]
        out["others"] = [row(p) for p in ranked if p["name"].lower() not in STACK_NAMES][:top]
        return out


def busy(sample: dict) -> int:
    return sample["kernel"] + sample["user"] - sample["idle"]


def total(sample: dict) -> int:
    return sample["kernel"] + sample["user"]


def summarize(samples: list[dict], top: int = TOP) -> dict:
    load = Load()
    for sample in samples:
        load.add(sample)
    return load.summary(top)


class Sampler:
    """Samples every `interval` seconds on a daemon thread from start() to stop(), which returns the summary with the
    span's UTC ends (from, to). A read that fails is noted (`error`) and skipped; the sampler never fails its caller."""

    def __init__(self, interval: float = 2.0, read=snapshot) -> None:
        self.interval = interval
        self.read = read
        self.load = Load()
        self.error: str | None = None
        self.started: str | None = None
        self._stop = threading.Event()
        self._lock = threading.Lock()
        self._thread: threading.Thread | None = None

    def start(self) -> Sampler:
        self.started = utc()
        self._take()
        self._thread = threading.Thread(target=self._run, name="machine-load", daemon=True)
        self._thread.start()
        return self

    def _take(self) -> None:
        try:
            sample = self.read()
        except Exception as exc:  # noqa: BLE001 - the load is evidence beside a boot, never a reason to fail it
            self.error = self.error or f"{type(exc).__name__}: {exc}"
            return
        with self._lock:
            self.load.add(sample)

    def _run(self) -> None:
        while not self._stop.wait(self.interval):
            self._take()

    def stop(self) -> dict:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=10)
        self._take()
        with self._lock:
            out = {"from": self.started, "to": utc(), "interval_s": self.interval, **self.load.summary(), "reader": READER}
        if self.error:
            out["error"] = self.error
        return out


# --- reading it ------------------------------------------------------------------------------------------------------

def proc_text(p: dict) -> str:
    return f"{p['name']} {p['pid']} {p['cpu_pct']:.1f}% (max {p['max_pct']:.0f}%)"


def text(load: dict | None) -> str:
    """One line: "cpu 32.1% mean, 78.0% max; free 51.2 GB min of 63.9 GB; top: TS3W.exe 23592 6.4% (max 12%), ...",
    with "others:" (the top less the stack's own games) when the top holds any of them."""
    if not load:
        return "no load recorded"
    cpu = load.get("cpu_pct") or {}
    parts = [f"{load.get('seconds', 0):.0f} s, {load.get('samples', 0)} samples"]
    parts.append(f"cpu {cpu['mean']:.1f}% mean, {cpu['max']:.1f}% max" if cpu.get("mean") is not None else "cpu -")
    avail = load.get("avail_mb")
    if avail:
        of = f" of {load['total_mb'] / 1024:.1f} GB" if load.get("total_mb") else ""
        parts.append(f"free {avail['min'] / 1024:.1f} GB min{of}")
    line = "; ".join(parts)
    top = load.get("top") or []
    if top:
        line += "; top: " + ", ".join(proc_text(p) for p in top)
    if any(p["name"].lower() in STACK_NAMES for p in top):
        others = [p for p in load.get("others") or [] if p not in top]
        if others:
            line += "; others: " + ", ".join(proc_text(p) for p in others)
    if load.get("error"):
        line += f"; read error: {load['error']}"
    return line


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0], formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="verb", required=True)
    sample = sub.add_parser("sample", help="sample the machine for --seconds and print the summary")
    sample.add_argument("--seconds", type=float, default=10.0)
    sample.add_argument("--interval", type=float, default=2.0)
    sample.add_argument("--json", action="store_true", help="the whole summary as JSON (what a boot row's load holds)")
    args = parser.parse_args(argv)
    if sys.platform != "win32":
        print("machine_load: Windows only")
        return 2
    sampler = Sampler(args.interval).start()
    time.sleep(max(0.0, args.seconds))
    load = sampler.stop()
    print(json.dumps(load) if args.json else text(load))
    return 0


if __name__ == "__main__":
    sys.exit(main())
