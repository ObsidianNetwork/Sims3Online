"""Read-only Sandboxie /listpids probes with bounded, owned-process wait-chain capture."""
from __future__ import annotations

import argparse
import ctypes
from ctypes import wintypes
import json
import math
from pathlib import Path
import subprocess
import sys
import tempfile
import threading

EXIT_TIMEOUT = 5.0
INSPECTOR_JOIN_TIMEOUT = 1.0


def wait_chain(pid: int, *, cancelled: threading.Event | None = None) -> dict:
    """Inspect the PID recorded from the probe's own Popen; never follow foreign owners."""
    if sys.platform != "win32":
        raise OSError("Windows wait-chain inspection requires Windows")
    c, w = ctypes, wintypes
    kernel = c.WinDLL("kernel32", use_last_error=True)
    advapi = c.WinDLL("advapi32", use_last_error=True)
    user = c.WinDLL("user32", use_last_error=True)

    class ThreadEntry(c.Structure):
        _fields_ = [(name, w.DWORD) for name in (
            "size", "usage", "tid", "pid", "base_priority", "delta_priority", "flags")]

    class Lock(c.Structure):
        _fields_ = [("name", w.WCHAR * 128), ("timeout", c.c_int64), ("alertable", w.BOOL)]

    class Thread(c.Structure):
        _fields_ = [(name, w.DWORD) for name in ("pid", "tid", "wait_ms", "context_switches")]

    class Info(c.Union):
        _fields_ = [("lock", Lock), ("thread", Thread)]

    class Node(c.Structure):
        _fields_ = [("type", c.c_int), ("status", c.c_int), ("info", Info)]

    kernel.CreateToolhelp32Snapshot.argtypes = [w.DWORD, w.DWORD]
    kernel.CreateToolhelp32Snapshot.restype = w.HANDLE
    kernel.Thread32First.argtypes = [w.HANDLE, c.POINTER(ThreadEntry)]
    kernel.Thread32Next.argtypes = [w.HANDLE, c.POINTER(ThreadEntry)]
    kernel.CloseHandle.argtypes = [w.HANDLE]
    advapi.OpenThreadWaitChainSession.argtypes = [w.DWORD, c.c_void_p]
    advapi.OpenThreadWaitChainSession.restype = w.HANDLE
    advapi.GetThreadWaitChain.argtypes = [
        w.HANDLE, c.c_size_t, w.DWORD, w.DWORD, c.POINTER(w.DWORD),
        c.POINTER(Node), c.POINTER(w.BOOL)]
    advapi.CloseThreadWaitChainSession.argtypes = [w.HANDLE]
    user.GetWindowTextW.argtypes = [w.HWND, w.LPWSTR, c.c_int]
    user.GetWindowThreadProcessId.argtypes = [w.HWND, c.POINTER(w.DWORD)]
    callback = c.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
    user.EnumWindows.argtypes = [callback, w.LPARAM]
    user.EnumChildWindows.argtypes = [w.HWND, callback, w.LPARAM]
    rows, windows = [], []
    snapshot = kernel.CreateToolhelp32Snapshot(4, 0)
    session = advapi.OpenThreadWaitChainSession(0, None)
    try:
        if not session:
            raise c.WinError(c.get_last_error())
        entry = ThreadEntry()
        entry.size = c.sizeof(entry)
        more = kernel.Thread32First(snapshot, c.byref(entry))
        while more:
            if cancelled is not None and cancelled.is_set():
                break
            if entry.pid == pid:
                nodes, count, cycle = (Node * 16)(), w.DWORD(16), w.BOOL()
                ok = advapi.GetThreadWaitChain(session, 0, 0, entry.tid,
                                               c.byref(count), nodes, c.byref(cycle))
                if cancelled is not None and cancelled.is_set():
                    break
                chain = []
                if ok:
                    for node in nodes[:count.value]:
                        if node.type == 8 and node.info.thread.pid != pid:
                            break
                        detail = ({"pid": node.info.thread.pid, "tid": node.info.thread.tid}
                                  if node.type == 8 else {"name": node.info.lock.name})
                        chain.append({"type": node.type, "status": node.status, **detail})
                rows.append({"tid": entry.tid, "ok": bool(ok),
                             "error": c.get_last_error() if not ok else 0,
                             "cycle": bool(cycle.value) and len(chain) == count.value,
                             "scope_limited": bool(ok) and len(chain) < count.value, "chain": chain})
            more = kernel.Thread32Next(snapshot, c.byref(entry))
    finally:
        if session:
            advapi.CloseThreadWaitChainSession(session)
        kernel.CloseHandle(snapshot)

    @callback
    def window(hwnd, _):
        if cancelled is not None and cancelled.is_set():
            return False
        owner = w.DWORD()
        user.GetWindowThreadProcessId(hwnd, c.byref(owner))
        if owner.value != pid:
            return True
        title = c.create_unicode_buffer(2048)
        user.GetWindowTextW(hwnd, title, len(title))
        children = []

        @callback
        def child(child_hwnd, _):
            if cancelled is not None and cancelled.is_set():
                return False
            child_owner = w.DWORD()
            user.GetWindowThreadProcessId(child_hwnd, c.byref(child_owner))
            if child_owner.value != pid:
                return True
            text = c.create_unicode_buffer(2048)
            user.GetWindowTextW(child_hwnd, text, len(text))
            if text.value:
                children.append(text.value)
            return True

        user.EnumChildWindows(hwnd, child, 0)
        windows.append({"pid": pid, "owned": True, "title": title.value, "children": children})
        return True

    if cancelled is None or not cancelled.is_set():
        user.EnumWindows(window, 0)
    return {"pid": pid, "threads": rows, "windows": windows}


def probe(start: str, box: str, timeout: float, capture_after: float) -> dict:
    command = [start, f"/box:{box}", "/listpids"]
    data = {"command": command, "timed_out": False, "wait_chain": None}
    # File-backed output avoids pipe-reader joins and Popen.__exit__'s implicit unbounded wait.
    with tempfile.TemporaryFile() as stdout, tempfile.TemporaryFile() as stderr:
        process = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=stdout,
                                   stderr=stderr, encoding="utf-8", errors="replace")
        data["pid"] = process.pid
        cancelled, inspection = threading.Event(), {}

        def capture():
            if not cancelled.is_set() and process.poll() is None:
                try:
                    inspection["wait_chain"] = wait_chain(process.pid, cancelled=cancelled)
                except OSError as exc:
                    inspection["inspection_error"] = str(exc)

        timer = threading.Timer(capture_after, capture)
        timer.daemon = True
        timer.start()
        try:
            try:
                process.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                data["timed_out"] = True
        finally:
            cancelled.set()
            try:
                if process.poll() is None:
                    process.kill()  # Only the Start.exe this probe created.
                    try:
                        process.wait(timeout=EXIT_TIMEOUT)
                    except subprocess.TimeoutExpired:
                        data["exit_incomplete"] = True
            finally:
                timer.cancel()
                timer.join(timeout=INSPECTOR_JOIN_TIMEOUT)
                if timer.is_alive():
                    data["inspection_incomplete"] = True
                else:
                    data.update(inspection)
        stdout.seek(0)
        stderr.seek(0)
        data.update(returncode=process.returncode,
                    stdout=stdout.read().decode("utf-8", errors="replace"),
                    stderr=stderr.read().decode("utf-8", errors="replace"))
    return data


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--box", required=True)
    parser.add_argument("--start", default=r"C:\Program Files\Sandboxie-Plus\Start.exe")
    parser.add_argument("--count", type=int, default=1)
    parser.add_argument("--timeout", type=float, default=15)
    parser.add_argument("--capture-after", type=float, default=5)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args(argv)
    if args.count < 1 or not math.isfinite(args.timeout) or not 0 < args.capture_after < args.timeout:
        parser.error("count must be positive and capture-after must fall inside a finite timeout")
    import stack_claim

    def require_claim():
        claim = stack_claim.current()
        if not claim or claim[0] != stack_claim.own_session():
            parser.error("a held stack claim belonging to this session is required")

    require_claim()
    args.out.mkdir(parents=True, exist_ok=True)
    for index in range(args.count):
        require_claim()
        data = probe(args.start, args.box, args.timeout, args.capture_after)
        path = args.out / f"listpids-{index + 1:03d}.json"
        path.write_text(json.dumps(data, indent=2), encoding="utf-8")
        print(f"{path.name}: exit={data['returncode']} timeout={data['timed_out']} "
              f"stdout={data['stdout']!r} stderr={data['stderr']!r}", flush=True)
        if data["timed_out"] or data["returncode"]:
            return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
