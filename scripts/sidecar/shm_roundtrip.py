"""Two-way shared memory across the Sandboxie boundary, plus a cross-box event for wake-ups.

    python shm_roundtrip.py boxed <name> <status-file>   # inside the box: create section + event, write HELLO,
                                                          # wait for PING at offset 1024, answer PONG
    python shm_roundtrip.py host <box> <name>              # outside: open both by NT path, check HELLO, write PING,
                                                          # signal the event, wait for PONG; prints round-trip time
"""
import ctypes
import ctypes.wintypes as wt
import os
import sys
import time

if __name__ == "__main__":
    if len(sys.argv) != 4 or sys.argv[1] not in ("boxed", "host"):
        print(__doc__)
        raise SystemExit(0 if sys.argv[1:2] in (["-h"], ["--help"]) else 2)
    if sys.platform != "win32":
        raise SystemExit("shm_roundtrip.py needs Windows (Sandboxie and NT sections).")

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
ntdll = ctypes.WinDLL("ntdll")
k32.CreateFileMappingW.restype = wt.HANDLE
k32.CreateFileMappingW.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.DWORD, wt.DWORD, wt.DWORD, wt.LPCWSTR]
k32.MapViewOfFile.restype = ctypes.c_void_p
k32.MapViewOfFile.argtypes = [wt.HANDLE, wt.DWORD, wt.DWORD, wt.DWORD, ctypes.c_size_t]
k32.CreateEventW.restype = wt.HANDLE
k32.CreateEventW.argtypes = [ctypes.c_void_p, wt.BOOL, wt.BOOL, wt.LPCWSTR]
k32.WaitForSingleObject.argtypes = [wt.HANDLE, wt.DWORD]
k32.SetEvent.argtypes = [wt.HANDLE]
k32.ProcessIdToSessionId.argtypes = [wt.DWORD, ctypes.POINTER(wt.DWORD)]
SIZE = 65536


class US(ctypes.Structure):
    _fields_ = [("Length", wt.USHORT), ("MaximumLength", wt.USHORT), ("Buffer", wt.LPWSTR)]


class OA(ctypes.Structure):
    _fields_ = [("Length", wt.ULONG), ("RootDirectory", wt.HANDLE), ("ObjectName", ctypes.POINTER(US)),
                ("Attributes", wt.ULONG), ("SD", ctypes.c_void_p), ("QOS", ctypes.c_void_p)]


def nt_open(kind, path, access):
    us = US(len(path) * 2, len(path) * 2 + 2, path)
    oa = OA(ctypes.sizeof(OA), None, ctypes.pointer(us), 0x40, None, None)
    h = wt.HANDLE()
    fn = ntdll.NtOpenSection if kind == "section" else ntdll.NtOpenEvent
    st = fn(ctypes.byref(h), access, ctypes.byref(oa))
    return (h, 0) if st == 0 else (None, st & 0xFFFFFFFF)


def boxed(name, status):
    sec = k32.CreateFileMappingW(wt.HANDLE(-1).value, None, 0x04, 0, SIZE, "Local\\" + name)
    ev = k32.CreateEventW(None, False, False, "Local\\" + name + "_wake")
    view = k32.MapViewOfFile(sec, 0x000F001F, 0, 0, SIZE)
    ctypes.memmove(view, b"HELLO", 5)
    result = "timeout"
    for _ in range(600):
        k32.WaitForSingleObject(ev, 100)
        if ctypes.string_at(view + 1024, 4) == b"PING":
            ctypes.memmove(view + 2048, b"PONG", 4)
            result = "got PING, wrote PONG"
            break
    with open(status, "w") as f:
        f.write(f"section={bool(sec)} event={bool(ev)} {result}\n")
    time.sleep(2)


def host(box, name):
    session = wt.DWORD()
    k32.ProcessIdToSessionId(os.getpid(), ctypes.byref(session))
    base = rf"\Sandbox\{os.environ.get('USERNAME', '')}\{box}\Session_{session.value}\Sessions\{session.value}\BaseNamedObjects"
    sec, st = nt_open("section", base + "\\" + name, 0x0006)  # SECTION_MAP_READ | SECTION_MAP_WRITE
    print("open section", hex(st) if st else "ok")
    ev, st2 = nt_open("event", base + "\\" + name + "_wake", 0x0002)  # EVENT_MODIFY_STATE
    print("open event", hex(st2) if st2 else "ok")
    if not sec:
        return
    view = k32.MapViewOfFile(sec, 0x0002 | 0x0004, 0, 0, SIZE)  # FILE_MAP_WRITE | FILE_MAP_READ
    print("boxed wrote:", ctypes.string_at(view, 5))
    start = time.perf_counter()
    ctypes.memmove(view + 1024, b"PING", 4)
    if ev:
        k32.SetEvent(ev)
    while ctypes.string_at(view + 2048, 4) != b"PONG" and time.perf_counter() - start < 10:
        pass
    took = (time.perf_counter() - start) * 1000
    print("round trip:", "PONG" if ctypes.string_at(view + 2048, 4) == b"PONG" else "none", f"{took:.3f} ms")


if __name__ == "__main__":
    if sys.argv[1] == "boxed":
        boxed(sys.argv[2], sys.argv[3])
    else:
        host(sys.argv[2], sys.argv[3])
