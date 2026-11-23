"""Generated probe sources at paths named by their content.

Tools that fill a probe template per call (sim_act, sim_compare, ts3_view) used one fixed file each, so two calls at
once overwrote each other's source before it compiled: a Jennifer call ran Pablo's probe (2026-10-15). Each source now
gets its own file, <stem>_<hash>.cs next to the template's path, written atomically; bridgectl then builds it once and
the mod loads it once (scripts/bridgectl.py cached_probe, LoadAndRun).
"""
from __future__ import annotations

import hashlib
import os
from pathlib import Path


def write(path: Path, source: str) -> Path:
    digest = hashlib.sha1(source.encode("utf-8")).hexdigest()[:8]
    target = path.with_name(f"{path.stem}_{digest}{path.suffix}")
    if not target.exists():
        target.parent.mkdir(parents=True, exist_ok=True)
        temp = target.with_name(f"{target.name}.{os.getpid()}.tmp")
        temp.write_text(source, encoding="utf-8")
        os.replace(temp, target)
    return target
