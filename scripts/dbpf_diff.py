"""Record-granularity checkpoint diff for TS3 save containers (.nhd / .package).

Compares two DBPF-2.0 tail-index containers TGI-keyed (type + group + instance)
and names divergence per record family (families attributed 2026-10-07 via
scripts/refpack.py payload strings — see internal documentation).
Exit 0 = identical, 1 = differences.
Usage: python scripts/dbpf_diff.py <a.nhd> <b.nhd>
"""
import os
import sys
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from dbpf_layout import locate_index, parse_records  # noqa: E402

FAMILIES = {
    0x03E80CDC: "native stream header",
    0x06B981ED: "managed blob (ScriptObjectGroup)",
    0x01661233: "SIM (SimDescriptions)",
    0x342779A7: "sim visual/CAS state",
    0x4D1A5589: "object material overrides",
    0x0563919E: "wall/floor material state",
    0x04A4D951: "lighting objects",
    0xD063545B: "lot/house name string keys",
    0x03D86EA4: "object layer registry",
    0xCF84EC98: "door/window state",
    0x092E36EA: "per-object guid state (8B)",
    0x0A0CE7A9: "per-object guid state (8B)",
    0x0553EAD4: "binary family (276 recs)",
    0x05ED1226: "binary family (227 recs)",
    0x05FF6BA4: "binary family (184 recs)",
    0xF12E5E12: "binary family (368 recs)",
    0x06DC847E: "binary family (433 recs)",
    0x312E7545: "binary family (552 recs)",
    0xB1422971: "binary family (460 recs)",
    0xB125533A: "binary family (1012 recs)",
    0x046A7235: "binary family (92 recs)",
    0x0580A2CE: "binary family (7 recs)",
    0x01C06880: "binary family (90 recs)",
    0x913381F2: "binary family (92 recs)",
    0xD84E7FC6: "binary family (single)",
    0x6B6D837E: "binary family (single)",
}


def family(t):
    return FAMILIES.get(t, f"unnamed {t:#010x}")


def load(path):
    with open(path, "rb") as f:
        data = f.read()
    layout = locate_index(data)
    if layout is None:
        print(f"{path}: NO INDEX FOUND — not a recognized DBPF 2.0 tail-index container")
        sys.exit(2)
    recs = {}
    for t, g, ilo, ihi, off, csz, usz, fl in parse_records(data, layout):
        recs[(t, g, ihi, ilo)] = (off, csz, usz, fl, bytes(data[off:off + csz]))
    return layout, recs


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(0 if sys.argv[1:2] in (["-h"], ["--help"]) else 2)
    la, ra = load(sys.argv[1])
    lb, rb = load(sys.argv[2])
    print(f"A: {sys.argv[1]}: {la['count']} records, entryLen={la['entry_len']}, "
          f"index {la['start']:#x}..{la['end_index']:#x}")
    print(f"B: {sys.argv[2]}: {lb['count']} records, entryLen={lb['entry_len']}, "
          f"index {lb['start']:#x}..{lb['end_index']:#x}")

    added = sorted(rb.keys() - ra.keys())
    removed = sorted(ra.keys() - rb.keys())
    changed = []
    for k in ra.keys() & rb.keys():
        a, b = ra[k], rb[k]
        if a[1] != b[1] or a[2] != b[2] or a[4] != b[4]:
            kind = "resized" if (a[1], a[2]) != (b[1], b[2]) else "content"
            changed.append((k, kind, a, b))

    if not (added or removed or changed):
        print(f"IDENTICAL — all {len(ra)} records match byte-for-byte")
        sys.exit(0)

    per_family = Counter()
    for k in added:
        per_family[f"added   {family(k[0])}"] += 1
    for k in removed:
        per_family[f"removed {family(k[0])}"] += 1
    for k, kind, _a, _b in changed:
        per_family[f"{kind}  {family(k[0])}"] += 1
    print(f"\nDIFFERENCES: {len(added)} added, {len(removed)} removed, {len(changed)} changed")
    for line, n in per_family.most_common():
        print(f"  {n:5d}  {line}")

    def tgi(k):
        return f"type={k[0]:#010x} group={k[1]:#x} iid={k[3]:#018x}"

    shown = 0
    for k in added[:10]:
        print(f"  + {tgi(k)} csize={rb[k][1]} usize={rb[k][2]}")
        shown += 1
    for k in removed[:10]:
        print(f"  - {tgi(k)} csize={ra[k][1]} usize={ra[k][2]}")
        shown += 1
    for k, kind, a, b in changed[:15]:
        print(f"  ~ {tgi(k)} [{kind}] csize {a[1]}->{b[1]} usize {a[2]}->{b[2]}")
        shown += 1
    total = len(added) + len(removed) + len(changed)
    if shown < total:
        print(f"  ... {total - shown} more (summary above is complete)")
    sys.exit(1)


if __name__ == "__main__":
    main()
