"""Card 55's native breakpoint addresses are local-only.

private reverse-engineering notes keeps native addresses out of public material, so the two breakpoint recipes Card 55 uses
(the G1 trace recipe astar_card55_e1.txt and the G0 road recipe g0_card61_road.txt) live in the ignored folder
.omo/local/card55/ of the checkout (SIMSONLINE_CARD55_LOCAL names another folder). The tools keep their breakpoint
labels in code and read the addresses from there; a label whose address is not installed reads None, so a gate that
needs it blocks instead of passing.
"""
from __future__ import annotations

import os
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
E1_RECIPE = "astar_card55_e1.txt"
G0_RECIPE = "g0_card61_road.txt"


def folder() -> Path:
    return Path(os.environ.get("SIMSONLINE_CARD55_LOCAL") or ROOT / ".omo" / "local" / "card55")


def recipe(name: str) -> Path:
    return folder() / name


def load(name: str, labels: tuple[str, ...]) -> dict[str, str | None]:
    """label -> 8-digit lowercase hex address from the local recipe, None for each label it does not give."""
    found: dict[str, str] = {}
    path = recipe(name)
    if path.is_file():
        for line in path.read_text(encoding="utf-8").splitlines():
            match = re.fullmatch(r"\s*([0-9a-fA-F]{8})\s+(\S+)\s*", line)
            if match:
                found[match[2]] = match[1].lower()
    return {label: found.get(label) for label in labels}
