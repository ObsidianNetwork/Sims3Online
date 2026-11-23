"""Switch Sims3MonoModder (S3MM) on or off for every TS3 started from this installation.

S3MM is an ASI plugin: the ASI loader (wininet.dll proxy) in Game\\Bin loads every *.asi there into each
TS3W.exe at start, the sandboxed clients included. Off renames Sims3MonoModder.asi to .asi.off; the loader
stays and loads nothing. Running TS3s keep whatever they started with.

    python scripts/s3mm.py status
    python scripts/s3mm.py on|off

Cold-test gates and latency baselines run with S3MM off (it adds Mono detours, a profiler and a vectored
exception handler). See internal documentation
"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import ts3_paths  # scripts/ts3_paths.py


def game_bin() -> Path:
    # SIMSONLINE_TS3_PATH can name a Wine-prefix layout that doesn't exist on this host: only trust it if it does.
    found = ts3_paths.find_ts3w()
    if found:
        return found.parent
    raise SystemExit("TS3W.exe not found; set SIMSONLINE_TS3_PATH to an existing TS3W.exe")


def main() -> int:
    action = sys.argv[1] if len(sys.argv) > 1 else "status"
    if sys.argv[1:2] in (["-h"], ["--help"]):
        print(__doc__)
        return 0
    if action not in ("on", "off", "status"):
        raise SystemExit("usage: s3mm.py status|on|off")
    folder = game_bin()
    on, off = folder / "Sims3MonoModder.asi", folder / "Sims3MonoModder.asi.off"
    loader = (folder / "wininet.dll").is_file()
    if action == "on" and off.is_file() and not on.is_file():
        off.rename(on)
    elif action == "off" and on.is_file():
        if off.is_file():
            off.unlink()
        on.rename(off)
    state = "on" if on.is_file() else ("off" if off.is_file() else "absent")
    print(f"s3mm={state} loader={'present' if loader else 'absent'} bin={folder}")
    return 0 if state != "absent" else 1


if __name__ == "__main__":
    raise SystemExit(main())
