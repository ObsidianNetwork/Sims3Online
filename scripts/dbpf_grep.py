"""Search a TS3 save's records for text or bytes inside their decompressed payloads.

    python scripts/dbpf_dump.py grep SAVE NEEDLE... [--type T] [--context N] [--max N] [-i]

SAVE is one container (a .nhd, a .package, Meta.data) or a .sims3 save folder, whose every DBPF file is searched (the
26-byte .dat and simsonline-*.txt markers are not DBPF). A NEEDLE is text, searched both as UTF-8 and as UTF-16LE (the
game's own strings), or hex:BYTES, the bytes as stored (spaces allowed; an id is little-endian:
hex:efcdab8967452301 for 0x0123456789abcdef). A record stored RefPack-compressed is searched after decompression
(scripts/refpack.py), so a name split by a back-reference on disk is still found; one that does not decode is searched
as stored and counted "undecodable". The record index is located without assuming a layout (scripts/dbpf_layout.py:
about 13 s for a 24 MB .nhd).

Per container: its records, then per needle and form the hits and records holding them, and one line per record (type,
its family from dbpf_diff.py, group:instance, the hit count and first offsets in the decompressed payload). --context N
prints N bytes either side of each listed hit (UTF-16LE hits decoded as text, hex: hits as hex, the hit in brackets).
--type T (hex, repeatable) searches only those resource types; --max N lists at most N records per needle and form
(the counts stay complete); -i folds ASCII letters in text needles (a hex: needle stays exact). Exit 0 when a needle was found, 1 when none was, 2 when the save
has no container with a readable index.
"""
from __future__ import annotations

import argparse
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Iterator

sys.path.insert(0, str(Path(__file__).resolve().parent))
from dbpf_diff import FAMILIES  # noqa: E402
from dbpf_layout import locate_index, parse_records  # noqa: E402
from refpack import refpack  # noqa: E402

MAGIC = b"DBPF"


@dataclass
class Record:
    type: int
    group: int
    instance: int
    payload: bytes
    stored: str  # "refpack", "raw" or "undecodable"
    length_ok: bool = True  # the decompressed length matches the index's


def needle_forms(text: str) -> list[tuple[str, bytes]]:
    """A needle's byte forms: hex:BYTES as written, text as UTF-8 and UTF-16LE."""
    if text.lower().startswith("hex:"):
        try:
            raw = bytes.fromhex(text[4:].replace(" ", ""))
        except ValueError as error:
            raise SystemExit(f"{text!r}: hex: wants an even number of hex digits ({error})") from error
        if not raw:
            raise SystemExit(f"{text!r}: no bytes")
        return [("hex", raw)]
    if not text:
        raise SystemExit("an empty needle matches everything")
    return [("utf-8", text.encode("utf-8")), ("utf-16le", text.encode("utf-16-le"))]


def is_refpack(blob: bytes) -> bool:
    return len(blob) >= 5 and blob[1] == 0xFB and (blob[0] & 0xFC) in (0x10, 0x50, 0x90)


def decompressed(blob: bytes) -> tuple[bytes, str]:
    if not is_refpack(blob):
        return blob, "raw"
    try:
        return bytes(refpack(blob[5:])), "refpack"  # after the 5-byte header: flags, 0xFB, 3-byte usize
    except Exception:  # noqa: BLE001 - a record that does not decode is searched as stored
        return blob, "undecodable"


def containers(save: Path) -> list[Path]:
    """The DBPF files to search: the file itself, or every DBPF file in a save folder."""
    if save.is_file():
        return [save]
    found = []
    for path in sorted(p for p in save.rglob("*") if p.is_file()):
        with open(path, "rb") as f:
            if f.read(4) == MAGIC:
                found.append(path)
    return found


def records(data: bytes, locate: Callable = locate_index) -> Iterator[Record] | None:
    layout = locate(data) if data[:4] == MAGIC and len(data) >= 0x60 else None
    if layout is None:
        return None

    def each() -> Iterator[Record]:
        for t, group, ilo, ihi, off, csz, usz, _flags in parse_records(data, layout):
            payload, stored = decompressed(data[off:off + csz])
            yield Record(t, group, (ihi << 32) | ilo, payload, stored, stored != "refpack" or len(payload) == usz)

    return each()


def find_all(hay: bytes, needle: bytes) -> list[int]:
    hits, at = [], hay.find(needle)
    while at >= 0:
        hits.append(at)
        at = hay.find(needle, at + 1)
    return hits


def printable(text: str) -> str:
    return "".join(c if c.isprintable() else "." for c in text)


def context_line(payload: bytes, at: int, size: int, form: str, width: int) -> str:
    """width bytes either side of a hit, the hit in brackets: hex for hex: needles, else text."""
    if form == "utf-16le":
        lo = max(at - 2 * (width // 2), at % 2)
        hi = min(len(payload), at + size + 2 * (width // 2))
        hi -= (hi - lo) % 2

        def show(chunk: bytes) -> str:
            return printable(chunk.decode("utf-16-le", errors="replace"))
    else:
        lo, hi = max(0, at - width), min(len(payload), at + size + width)
        if form == "hex":
            def show(chunk: bytes) -> str:
                return chunk.hex(" ")
        else:
            def show(chunk: bytes) -> str:
                return printable(chunk.decode("latin-1"))
    return f"{show(payload[lo:at])}[{show(payload[at:at + size])}]{show(payload[at + size:hi])}"


def grep(save: Path, needles: list[str], types: set[int] | None = None, context: int = 0, max_records: int = 20,
         ignore_case: bool = False, out: Callable[[str], None] = print, locate: Callable = locate_index) -> int:
    forms = [(text, form, raw) for text in needles for form, raw in needle_forms(text)]
    fold = [ignore_case and form != "hex" for _, form, _ in forms]  # -i folds text needles only
    forms = [(text, form, raw.lower() if folded else raw) for (text, form, raw), folded in zip(forms, fold)]
    files = containers(save)
    readable = found_any = 0
    for path in files:
        data = path.read_bytes()
        found = records(data, locate)
        if found is None:
            out(f"{path.name}: no record index found (not a DBPF container this reads)")
            continue
        readable += 1
        stats = {"refpack": 0, "raw": 0, "undecodable": 0}
        mismatched = searched = 0
        hits: dict[int, list[tuple[Record, list[int]]]] = {i: [] for i in range(len(forms))}
        for rec in found:
            stats[rec.stored] += 1
            mismatched += not rec.length_ok
            if types and rec.type not in types:
                continue
            searched += 1
            folded = rec.payload.lower() if ignore_case else rec.payload
            for i, (_, _, raw) in enumerate(forms):
                at = find_all(folded if fold[i] else rec.payload, raw)
                if at:
                    hits[i].append((rec, at))
        total = sum(stats.values())
        out(f"{path.name}: {total} records ({stats['refpack']} refpack, {stats['raw']} raw, "
            f"{stats['undecodable']} undecodable" + (f", {mismatched} decoded to another length than the index's"
                                                      if mismatched else "") + ")"
            + (f", {searched} of type {', '.join(f'{t:#010x}' for t in sorted(types))}" if types else ""))
        for i, (text, form, raw) in enumerate(forms):
            rows = hits[i]
            if not rows:
                continue
            found_any += 1
            out(f"  {text!r} {form}: {sum(len(at) for _, at in rows)} hit(s) in {len(rows)} record(s)")
            for rec, at in rows[:max_records]:
                name = FAMILIES.get(rec.type)
                out(f"    {rec.type:#010x}" + (f" {name}" if name else "") + f" {rec.group:08x}:{rec.instance:016x}: {len(at)} at "
                    + ", ".join(f"{a:#x}" for a in at[:6]) + (", ..." if len(at) > 6 else ""))
                if context:
                    for a in at[:6]:
                        out(f"      {a:#x}: {context_line(rec.payload, a, len(raw), form, context)}")
            if len(rows) > max_records:
                out(f"    ... {len(rows) - max_records} more record(s) (--max)")
    if not readable:
        out(f"{save}: no DBPF container with a readable index" + ("" if files else " (no DBPF file in it)"))
        return 2
    if not found_any:
        out("no hits")
    return 0 if found_any else 1


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="dbpf_dump.py grep", description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("save", type=Path, help="a .nhd/.package/Meta.data, or a .sims3 save folder")
    parser.add_argument("needles", nargs="+", metavar="NEEDLE", help="text, or hex:BYTES as stored")
    parser.add_argument("--type", action="append", default=[], metavar="T", help="only this resource type (hex)")
    parser.add_argument("--context", type=int, default=0, metavar="N", help="N bytes either side of each listed hit")
    parser.add_argument("--max", type=int, default=20, metavar="N", help="records listed per needle and form")
    parser.add_argument("-i", action="store_true", dest="ignore_case", help="fold ASCII letters in text needles")
    a = parser.parse_args(argv)
    if not a.save.exists():
        raise SystemExit(f"{a.save}: no such file or folder")
    try:
        types = {int(t, 16) for t in a.type}
    except ValueError as error:
        raise SystemExit(f"--type wants a hex resource type ({error})") from error
    return grep(a.save, a.needles, types or None, a.context, a.max, a.ignore_case)


if __name__ == "__main__":
    sys.exit(main())
