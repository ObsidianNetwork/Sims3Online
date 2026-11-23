"""Build and switch the SimLink sidecar (SimLink/, an ASI plugin) for every TS3 started from this installation.

    python scripts/sidecar.py build     # Visual Studio 2022 CMake, x86 Release -> SimLink/build/Release/SimLink.asi
    python scripts/sidecar.py build --generator "Visual Studio 18 2026"   # another Visual Studio (CI: simlink-windows)
    python scripts/sidecar.py install   # copy the build into Game\\Bin as SimLink.asi (on)
    python scripts/sidecar.py restore   # swap SimLink.asi and SimLink.asi.prev back
    python scripts/sidecar.py status|on|off

build uses the CMake that ships with Visual Studio 2022 Community, or the cmake on PATH when that isn't installed;
with another --generator it prefers the cmake on PATH, since an older bundled CMake may not know that generator.
A build with another generator than SimLink/build was configured with drops that configuration first (CMakeCache.txt
and CMakeFiles; the built files stay), since CMake refuses to switch generators in place.

An install keeps the build it replaces as Game\\Bin\\SimLink.asi.prev (status prints its hash as prev=), so a wrong
install can be undone with restore. The loader only loads *.asi, so the .prev file is never loaded. Status prints each
build's native version after its hash, read from the file: installed=49b7fa1430966daa(v7); (v?) when it cannot be read.
install and restore refuse while a TS3W.exe runs (it holds SimLink.asi open): stop the games first, stackctl down.

Loaded, the sidecar creates one shared-memory section and two events per TS3 and waits: it registers its internal
calls only when a controller asks (SIMSONLINE_SIMLINK_SHADOW=1), and the mod only calls them after that controller
sends SACS_RECORDER sidecar-on. Stacks that don't opt in get an idle thread and nothing else. Running TS3s keep
whatever they started with. See internal documentation
"""

from __future__ import annotations

import hashlib
import os
import shutil
import struct
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from s3mm import game_bin  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "SimLink"
BUILT = SOURCE / "build" / "Release" / "SimLink.asi"
CMAKE = Path(r"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe")
GENERATOR = "Visual Studio 17 2022"


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()[:16]


VERSION_ICALL = b"SimsOnlineServerHost.SimLinkNative::Version\0"


def native_version(data: bytes) -> int | None:
    """A SimLink build's native version (simlink_layout.h kNativeVersion), from the file alone: the registration's
    `push SimLinkVersion / push "...::Version"` gives the function, whose body is `mov eax, imm32 / ret`. None when the
    file does not have that shape."""
    try:
        pe = struct.unpack_from("<I", data, 0x3C)[0]
        count, optional = struct.unpack_from("<H", data, pe + 6)[0], struct.unpack_from("<H", data, pe + 20)[0]
        base = struct.unpack_from("<I", data, pe + 24 + 28)[0]
        sections = [struct.unpack_from("<8sIIII", data, pe + 24 + optional + 40 * i)[1:] for i in range(count)]

        def va(offset: int) -> int | None:
            return next((base + rva + offset - raw for _, rva, raw_size, raw in sections
                         if raw <= offset < raw + raw_size), None)

        def offset(address: int) -> int | None:
            rva = address - base
            return next((raw + rva - start for size, start, raw_size, raw in sections
                         if start <= rva < start + max(size, raw_size)), None)

        name = data.find(VERSION_ICALL)
        name_va = va(name) if name >= 0 else None
        push = data.find(b"\x68" + struct.pack("<I", name_va)) if name_va is not None else -1
        if push < 5 or data[push - 5] != 0x68:
            return None
        body = offset(struct.unpack_from("<I", data, push - 4)[0])
        if body is None or data[body] != 0xB8 or data[body + 5] != 0xC3:
            return None
        return struct.unpack_from("<i", data, body + 1)[0]
    except (struct.error, IndexError):
        return None


def describe(path: Path) -> str:
    """"<sha>(v<native version>)", (v?) when the version cannot be read."""
    data = path.read_bytes()
    version = native_version(data)
    return f"{hashlib.sha256(data).hexdigest()[:16]}(v{'?' if version is None else version})"


def running_games() -> list[str]:
    """The PIDs of running TS3W.exe processes, sandboxed clients included (tasklist); [] when tasklist fails."""
    try:
        out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq TS3W.exe", "/FO", "CSV", "/NH"], capture_output=True,
                             text=True, timeout=30, check=False).stdout
    except (OSError, subprocess.SubprocessError):
        return []
    rows = [line.strip().strip('"').split('","') for line in out.splitlines()]
    return [r[1] for r in rows if len(r) > 1 and r[0].lower() == "ts3w.exe"]


def find_cmake(generator: str = GENERATOR) -> str | None:
    bundled = str(CMAKE) if CMAKE.is_file() else None
    on_path = shutil.which("cmake")
    return (bundled or on_path) if generator == GENERATOR else (on_path or bundled)


def build_commands(cmake: str, generator: str = GENERATOR) -> list[list[str]]:
    return [[cmake, "-S", str(SOURCE), "-B", str(SOURCE / "build"), "-G", generator, "-A", "Win32"],
            [cmake, "--build", str(SOURCE / "build"), "--config", "Release"]]


def forget_other_generator(folder: Path, generator: str) -> str | None:
    """Drop a build folder's configuration made with another generator, and return that generator's name."""
    cache = folder / "CMakeCache.txt"
    try:
        lines = cache.read_text(encoding="utf-8", errors="replace").splitlines()
    except FileNotFoundError:
        return None
    old = next((line.split("=", 1)[1] for line in lines if line.startswith("CMAKE_GENERATOR:INTERNAL=")), None)
    if old is None or old == generator:
        return None
    cache.unlink()
    shutil.rmtree(folder / "CMakeFiles", ignore_errors=True)
    return old


def build(generator: str = GENERATOR) -> int:
    cmake = find_cmake(generator)
    if cmake is None:
        raise SystemExit(f"CMake not found at {CMAKE} or on PATH")
    old = forget_other_generator(SOURCE / "build", generator)
    if old:
        print(f"SimLink/build was configured with {old}; configuring it again with {generator}")
    env = {k: v for k, v in os.environ.items() if k != "CMAKE_GENERATOR"}
    for command in build_commands(cmake, generator):
        result = subprocess.run(command, env=env, capture_output=True, text=True, check=False)
        if result.returncode != 0:
            print(result.stdout[-3000:], result.stderr[-3000:])
            return result.returncode
    print(f"built {BUILT} sha256={sha(BUILT)}")
    return 0


def main() -> int:
    action = sys.argv[1] if len(sys.argv) > 1 else "status"
    if any(a in ("-h", "--help") for a in sys.argv[1:]):  # anywhere: `install --help` must not install
        print(__doc__)
        return 0
    if action not in ("build", "install", "restore", "on", "off", "status") or (action != "build" and len(sys.argv) > 2):
        raise SystemExit("usage: sidecar.py build|install|restore|status|on|off")
    if action == "build":
        options = sys.argv[2:]
        if options and (len(options) != 2 or options[0] != "--generator"):
            raise SystemExit('usage: sidecar.py build [--generator "Visual Studio 18 2026"]')
        return build(options[1] if options else GENERATOR)
    folder = game_bin()
    on, off = folder / "SimLink.asi", folder / "SimLink.asi.off"
    prev = folder / "SimLink.asi.prev"  # the build an install replaced (not an .asi: the loader skips it)
    if action in ("install", "restore"):
        games = running_games()
        if games:  # a running TS3 holds SimLink.asi open: the copy would fail half way (PermissionError)
            raise SystemExit(f"{action}: TS3W.exe is running (pid {', '.join(games)}) and holds SimLink.asi open. "
                             f"Stop the games first: python scripts/stackctl.py down. Nothing changed in {folder}.")
    if action == "install":
        if not BUILT.is_file():
            raise SystemExit("not built: python scripts/sidecar.py build")
        replaced = on if on.is_file() else (off if off.is_file() else None)
        if replaced is not None and sha(replaced) != sha(BUILT):
            shutil.copyfile(replaced, prev)
            print(f"kept the installed {sha(prev)} as {prev.name}")
        if off.is_file():
            off.unlink()
        shutil.copyfile(BUILT, on)
    elif action == "restore":
        if not prev.is_file():
            raise SystemExit(f"no {prev.name}: nothing to restore")
        target = off if off.is_file() and not on.is_file() else on
        swap = folder / "SimLink.asi.swap"
        if target.is_file():
            shutil.copyfile(target, swap)
        shutil.copyfile(prev, target)
        if swap.is_file():
            swap.replace(prev)
        else:
            prev.unlink()
    elif action == "on" and off.is_file() and not on.is_file():
        off.rename(on)
    elif action == "off" and on.is_file():
        if off.is_file():
            off.unlink()
        on.rename(off)
    current = on if on.is_file() else (off if off.is_file() else None)
    state = "on" if on.is_file() else ("off" if off.is_file() else "absent")
    built = f" built={describe(BUILT)}" if BUILT.is_file() else ""
    installed = f" installed={describe(current)}" if current else ""
    previous = f" prev={describe(prev)}" if prev.is_file() else ""
    loader = (folder / "wininet.dll").is_file()
    print(f"sidecar={state} loader={'present' if loader else 'absent'}{installed}{built}{previous} bin={folder}")
    return 0 if state != "absent" else 1


if __name__ == "__main__":
    raise SystemExit(main())
