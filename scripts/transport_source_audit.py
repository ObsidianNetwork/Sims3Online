#!/usr/bin/env python3
"""Classify timestamped ring samples as true SACS callbacks or synthetic/polled presentation events.

Usage: python scripts/transport_source_audit.py [run-folder]
The folder holds one subfolder per run with a client.log (scripts/transport_latency_ab.py writes them).
"""
from __future__ import annotations
import collections
import json
import re
import sys
from pathlib import Path

if sys.argv[1:2] in (["-h"], ["--help"]):
    print(__doc__)
    sys.exit(0)
base = (Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else
    Path(__file__).resolve().parents[1] / ".omo" / "run" / "transport-ab-20261013-140110")
if not base.is_dir():
    sys.exit(f"run folder not found: {base}")
for folder in sorted(base.iterdir()):
    if not folder.is_dir() or not (folder / "client.log").exists():
        continue
    suffix=folder.name
    types={}
    for raw in (folder/"client.log").read_text(encoding="utf-8",errors="replace").splitlines():
        marker='received: '
        if marker not in raw:
            continue
        try:
            obj=json.loads(raw.split(marker,1)[1])
        except (ValueError,IndexError):
            continue
        if obj.get("$type")!="sim_animation":
            continue
        for line in obj.get("lines",[]):
            bits=line.split("|",2)
            if len(bits)>=2 and bits[0].isdigit() and int(bits[0])>0:
                types[int(bits[0])]=bits[1]
    matched=collections.Counter()
    missing=[]
    source_seqs=[]
    for raw in (folder/"server.log").read_text(encoding="utf-8",errors="replace").splitlines():
        if "[transport-latency] stage=source_event " not in raw:
            continue
        seq=re.search(r"source_seq=(\d+)",raw)
        if not seq:continue
        number=int(seq.group(1))
        source_seqs.append(number)
        if number in types:
            matched[types[number]]+=1
        else:
            missing.append(number)
    print(suffix, "sampled_source_entries=",len(source_seqs),"matched_kinds=",dict(matched),
          "unknown=",missing[:12],flush=True)
