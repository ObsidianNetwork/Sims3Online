"""Where The Sims 3 is installed on this machine.

SIMSONLINE_TS3_PATH (a TS3W.exe path) wins when it names an existing file. Otherwise, on Windows, the common
retail, EA and Steam install folders are tried, including `<drive>:\\Games\\The Sims 3` and Steam libraries on
other drives.
"""
from __future__ import annotations

import os
import string
import sys
from pathlib import Path

BIN = Path("The Sims 3") / "Game" / "Bin" / "TS3W.exe"


def candidates() -> list[Path]:
    found = []
    if os.environ.get("SIMSONLINE_TS3_PATH"):
        found.append(Path(os.environ["SIMSONLINE_TS3_PATH"]))
    if sys.platform == "win32":
        found += [Path(r"C:\Program Files (x86)") / BIN,
                  Path(r"C:\Program Files (x86)\Electronic Arts") / BIN,
                  Path(r"C:\Program Files\EA Games") / BIN,
                  Path(r"C:\Program Files (x86)\Steam\steamapps\common") / BIN]
        for drive in string.ascii_uppercase[2:]:
            root = Path(f"{drive}:\\")
            found += [root / "Games" / BIN, root / "SteamLibrary" / "steamapps" / "common" / BIN]
    return found


def find_ts3w() -> Path | None:
    """The first existing TS3W.exe, or None."""
    return next((path for path in candidates() if path.is_file()), None)
