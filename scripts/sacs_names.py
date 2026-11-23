"""Names for the hashes in the presentation stream (machines, states, actors, parameters, request flags).

The game hashes every animation name with FNV-1 of the lowercased text: a state machine's key is the 64-bit hash of its
name ("phone" -> 8c544a4837c2f1ef), states, actors and parameters the 32-bit one ("x" -> 84696423, "BrowseTheWeb" ->
1552495694). Every such name is a string literal somewhere in the game's managed code, so hashing all literals of the
local decompile gives a lookup table:

    python scripts/sacs_names.py build              # once per machine: writes .omo/cache/sacs-names.json (local)
    python scripts/sacs_names.py 8c544a4837c2f1ef 1552495694 98
    #   8c544a4837c2f1ef = phone   1552495694 = BrowseTheWeb   98 = Interrupt|RequestWaitOnBridge|RequestEmulateEdith

The table is derived from EA code, so it stays in .omo/cache like the decompile itself. Tools import name64(), name32()
and flags(); with no table they print the numbers.
"""
from __future__ import annotations

import json
import os
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TABLE = ROOT / ".omo" / "cache" / "sacs-names.json"
_default_main = (
    ROOT
    if (ROOT / ".omo/cache/game-decompile").exists()
    else (Path.home() / "Workspace/Games/SimsOnline")
)
MAIN_CHECKOUT = Path(os.environ.get("SIMSONLINE_MAIN_CHECKOUT", str(_default_main)))
DECOMPILE = Path(os.environ.get("SIMSONLINE_DECOMPILE",
                                str(MAIN_CHECKOUT / ".omo/cache/game-decompile/01a12ad2/managed/phase2-recompile/projects")))
PROJECTS = ("Sims3GameplaySystems", "Sims3GameplayObjects", "Sims3StoreObjects", "SimIFace", "ScriptCore", "UI")
LITERAL = re.compile(r'"((?:[^"\\\n]|\\.){1,80})"')

# Sims3.SimIFace.SACS.DriverRequestFlags
FLAGS = [(1, "ClearQueue"), (2, "Interrupt"), (4, "Shortest"), (8, "Insecure"), (0x10, "Yield"), (0x20, "RequestWaitOnBridge"),
         (0x40, "RequestEmulateEdith"), (0x80, "RequestAllowDuplicateRequest"), (0x100, "RequestAllowRequestForCurrentState"),
         (0x200, "RequestEnsureAnimation"), (0x400, "RequestDoNotWaitForAnimation"), (0x800, "RequestInterruptThisMachineOnly")]


def fnv64(text: str) -> int:
    h = 0xCBF29CE484222325
    for b in text.lower().encode("utf-8"):
        h = (h * 0x100000001B3) & 0xFFFFFFFFFFFFFFFF
        h ^= b
    return h


def fnv32(text: str) -> int:
    h = 0x811C9DC5
    for b in text.lower().encode("utf-8"):
        h = (h * 0x01000193) & 0xFFFFFFFF
        h ^= b
    return h


def build() -> dict:
    n64: dict[str, str] = {}
    n32: dict[str, str] = {}
    files = 0
    for project in DECOMPILE.iterdir() if DECOMPILE.exists() else []:
        if not project.name.startswith(PROJECTS):
            continue
        for path in project.rglob("*.cs"):
            files += 1
            for m in LITERAL.finditer(path.read_text(encoding="utf-8", errors="replace")):
                text = m.group(1)
                if "\\" in text or " " in text.strip() and len(text) > 40:
                    continue
                n64.setdefault(format(fnv64(text), "016x"), text)
                n32.setdefault(str(fnv32(text)), text)
    table = {"source": str(DECOMPILE), "files": files, "64": n64, "32": n32}
    TABLE.parent.mkdir(parents=True, exist_ok=True)
    TABLE.write_text(json.dumps(table), encoding="utf-8")
    return table


_table: dict | None = None


def _load() -> dict:
    global _table
    if _table is None:
        _table = json.loads(TABLE.read_text(encoding="utf-8")) if TABLE.exists() else {"64": {}, "32": {}}
    return _table


def name64(key: str | int) -> str:
    """A machine key (decimal or hex) -> its name, or the hex key."""
    if isinstance(key, int):
        value = key
    elif key[:2] in ("0x", "0X") or (re.fullmatch(r"[0-9a-fA-F]{16}", key) and not key.isdigit()):
        value = int(key, 16)  # hex only when it says so or a letter gives it away; 16 plain digits are decimal
    elif re.fullmatch(r"[+-]?\d+", key):
        value = int(key)
    else:
        return key
    text = format(value, "016x")
    return _load()["64"].get(text, text)


def name32(value: str | int) -> str:
    """A state / actor / parameter hash -> its name, or the number."""
    text = str(int(value)) if str(value).isdigit() else str(value)
    return _load()["32"].get(text, text)


def flags(value: str | int) -> str:
    n = int(value)
    names = [name for bit, name in FLAGS if n & bit]
    if n >> 12:
        names.append(f"Priority{(n >> 12).bit_length() - 1}")
    return "|".join(names) or "0"


if __name__ == "__main__":
    if len(sys.argv) < 2 or sys.argv[1:2] in (["-h"], ["--help"]):
        print(__doc__)
        sys.exit(0 if len(sys.argv) > 1 else 2)
    if len(sys.argv) > 1 and sys.argv[1] == "build":
        t = build()
        print(f"wrote {TABLE} from {t['files']} files: {len(t['64'])} names (64-bit), {len(t['32'])} (32-bit)")
        sys.exit(0)
    for arg in sys.argv[1:]:
        if re.fullmatch(r"[0-9a-fA-F]{16}", arg) or (arg.isdigit() and int(arg) > 0xFFFFFFFF):
            print(f"{arg} = {name64(arg)}")
        elif arg.isdigit() and int(arg) < 0x1000 * 64:
            print(f"{arg} = {name32(arg)}   (as request flags: {flags(arg)})")
        else:
            print(f"{arg} = {name32(arg)}")
