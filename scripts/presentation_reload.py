"""Reload the client presentation module in the running client TS3: a second or two instead of a TS3 boot.

    python scripts/presentation_reload.py            # build, swap the module in (the presentation stays)
    python scripts/presentation_reload.py --watch    # the same on every save of SimsOnlineServerHostMod/Presentation/*.cs
    python scripts/presentation_reload.py --full     # also reset what the shell presented, then warm-rejoin to rebuild it

The module (SimsOnlineServerHostMod/Presentation/*.cs, the stream kinds it registers: walks, the position mirror,
moodlets) is compiled straight with the .NET 2 csc against the game's own runtime (about 0.2 s; an API TS3 lacks fails
here, scripts/check_mod_runtime.py) under its own assembly name (.NET 2 cannot unload, and a repeated name aliases the
resident assembly), uploaded in one bulk round trip (BULK_UPLOAD) and swapped in (SACS_RECORDER presentation-load keep).

The default swap resets only the old module's own state (walks running stop, the mirror forgets its targets, moodlet
times wait for the next Bf): right for code whose state lives per walk or per line. --full also resets the shell's
presentation (copies, drivers, effects) and warm-rejoins (quick_loop rejoin:warm, about 13 s), for a change whose effect
only shows when the presentation is built again (late-join history, created copies).

Numbers need no build at all: read them through Tunables in the code and set them live with scripts/tune.py.

The module compiles against bin\\Release\\SimsOnlineServerHostMod.dll. A change to the shell (PresentationApi, the replay
loop, recorders) still needs the mod installed and the TS3s restarted (quick_loop run --mod --boot). Every swap leaves the
old assembly loaded in the client TS3 (a few tens of KB each); restart the TS3 after a few hundred.
"""
from __future__ import annotations

import argparse
import os
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))
CLIENT = "127.0.0.1:34030"
MOD = ROOT / "SimsOnlineServerHostMod"
SOURCES = MOD / "Presentation"
OUTPUT = MOD / "bin" / "Release"


def csc_path() -> Path | None:
    windir = os.environ.get("WINDIR")
    path = Path(windir) / "Microsoft.NET" / "Framework" / "v2.0.50727" / "csc.exe" if windir else None
    return path if path and path.exists() else None


def build(name: str) -> tuple[Path | None, str]:
    """The module as <name>.dll in bin/Release: csc directly on Windows, else build-presentation-module.ps1 (wine)."""
    out = OUTPUT / (name + ".dll")
    shell = OUTPUT / "SimsOnlineServerHostMod.dll"
    if not shell.exists():
        return None, f"build the shell first: {shell} is missing"
    if csc_path() is None:
        r = subprocess.run(["pwsh", "-NoProfile", "-File", "scripts/build-presentation-module.ps1", "-AssemblyName", name],
                           cwd=ROOT, capture_output=True, text=True, timeout=300, check=False)
        return (out if r.returncode == 0 and out.exists() else None), r.stdout + r.stderr
    import check_mod_runtime
    args = check_mod_runtime.runtime_args(out, [shell]) + ["/optimize+"] + sorted(str(p) for p in SOURCES.glob("*.cs"))
    r = subprocess.run(args, cwd=ROOT, capture_output=True, text=True, timeout=120, check=False)
    return (out if r.returncode == 0 and out.exists() else None), r.stdout + r.stderr


def prune(keep: int = 5) -> None:
    """Reload builds pile up in bin/Release: keep the newest few (the loaded ones are copies in the TS3)."""
    builds = sorted(OUTPUT.glob("SimsOnlinePresentation_*.dll"), key=lambda p: p.stat().st_mtime, reverse=True)
    for old in builds[keep:]:
        for path in (old, old.with_suffix(".pdb")):
            try:
                path.unlink()
            except OSError:
                pass


def reload(endpoint: str, full: bool) -> bool:
    started = time.time()
    name = "SimsOnlinePresentation_" + time.strftime("%Y%m%d%H%M%S") + f"{int(time.time() * 1000) % 1000:03d}"
    dll, output = build(name)
    built = time.time() - started
    if dll is None:
        print(output.strip())
        print(f"module build failed ({built:.1f}s)")
        return False

    import contextlib
    import io

    import bridgectl
    sock = bridgectl.connect(endpoint, 60)
    try:
        with bridgectl.blob_lock(sock):  # the mod's one blob slot: no probe may upload between our upload and load
            with contextlib.redirect_stdout(io.StringIO()):  # bridgectl's upload chatter
                uploaded = bridgectl.chunk_upload(sock, str(dll), "presentation")
            if uploaded is None:
                print("upload failed")
                return False
            reply = bridgectl.roundtrip(sock, "SACS_RECORDER presentation-load" + ("" if full else " keep"))
    finally:
        sock.close()
    report = str(reply.get("report") or reply.get("error") or reply.get("reason") or reply).strip()
    ok = "module=" in report and "error=" not in report
    print(f"built {dll.name} in {built:.1f}s; load: {report}")
    prune()
    if ok and full:
        import quick_loop
        quick_loop.rejoin_warm()
    print(f"{'reloaded' if ok else 'reload FAILED'} in {time.time() - started:.1f}s")
    return ok


def snapshot() -> dict[str, float]:
    return {str(p): p.stat().st_mtime for p in SOURCES.glob("*.cs")}


def watch(endpoint: str) -> int:
    """Swap the module in on every save (Ctrl+C stops). Checks the sources' modification times once a second."""
    print(f"watching {SOURCES} (Ctrl+C stops)")
    previous = snapshot()
    while True:
        time.sleep(1.0)
        current = snapshot()
        if current == previous:
            continue
        time.sleep(0.3)  # an editor that writes in several steps
        current = snapshot()
        changed = sorted(Path(p).name for p in set(current) | set(previous) if current.get(p) != previous.get(p))
        previous = current
        print(f"-- {time.strftime('%H:%M:%S')} {', '.join(changed)}")
        try:
            reload(endpoint, full=False)
        except SystemExit as ex:  # bridgectl exits when the client's bridge is down: keep watching
            print(f"reload FAILED: {ex}")


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("--full", action="store_true", help="also reset the shell's presentation and warm-rejoin")
    p.add_argument("--watch", action="store_true", help="reload on every save of Presentation/*.cs")
    p.add_argument("--endpoint", default=CLIENT)
    a = p.parse_args(argv)
    if a.watch:
        try:
            return watch(a.endpoint)
        except KeyboardInterrupt:
            return 0
    return 0 if reload(a.endpoint, a.full) else 1


if __name__ == "__main__":
    sys.exit(main())
