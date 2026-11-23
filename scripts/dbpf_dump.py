"""Empirical DBPF (TS3 save .nhd / .package) header dump + index locator + record enumerator.

No layout is assumed: prints every nonzero header word, then locates the record
index by brute-force-validating (offset,size) slot pairs against the real file,
scoring candidates by how well the compressed record spans tile the payload
region, and picking the best. Aggregates records by resource type.
Usage: python scripts/dbpf_dump.py <file>
       python scripts/dbpf_dump.py grep <save> <text|hex:BYTES>...   (text or bytes in the decompressed
       records; scripts/dbpf_grep.py)
"""
import os
import struct
import sys
from collections import defaultdict

if sys.argv[1:2] == ["grep"]:
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import dbpf_grep

    sys.exit(dbpf_grep.main(sys.argv[2:]))
if len(sys.argv) != 2 or sys.argv[1] in ("-h", "--help"):
    print(__doc__)
    sys.exit(0 if sys.argv[1:2] in (["-h"], ["--help"]) else 2)
path = sys.argv[1]
fsize = os.path.getsize(path)
with open(path, "rb") as f:
    data = f.read()

magic, ver_major, ver_minor = struct.unpack_from("<4sII", data, 0)
print(f"magic={magic} verMajor={ver_major} verMinor={ver_minor} fileLen={fsize:#x}")
for off in range(0x0C, 0x60, 4):
    (w,) = struct.unpack_from("<I", data, off)
    if w:
        print(f"  u32@{off:#04x} = {w:#x}")

# candidate record counts: plausible u32s in the fixed header region
counts = [w for off in range(0x10, 0x40, 4)
          for w in [struct.unpack_from("<I", data, off)[0]] if 0 < w < 5_000_000]
print(f"count candidates: {[hex(c) for c in counts]}")


def tiling_score(count, entry_len, start, i_off, i_size, i_csize, csize_mask):
    """Union of on-disk record spans (compressed size) — the true layout tiles
    the payload region almost exactly; wrong alignments overlap grossly.
    Returns (score, all_csize_flagged): in every verified framing the csize dword
    carries bit 31 set; the caller doubles the score of fully-flagged framings
    (multiplicative bonus — a boolean-dominant key could let a small bogus
    all-flagged framing beat the true high-tiling one)."""
    end_index = start + count * entry_len
    spans = []
    flagged = 0
    usize_sum = 0
    for i in range(count):
        base = start + i * entry_len
        off = struct.unpack_from("<I", data, base + i_off * 4)[0]
        raw = struct.unpack_from("<I", data, base + i_csize * 4)[0]
        csz = raw & csize_mask
        usize_sum += struct.unpack_from("<I", data, base + i_size * 4)[0]
        if csz == 0 or off < 0x60 or start <= off < end_index or off + csz > fsize:
            return 0, False
        if raw & 0x80000000:
            flagged += 1
        spans.append((off, off + csz))
    spans.sort()
    union = 0
    cur_end = -1
    for lo, hi in spans:
        if hi <= cur_end:
            continue
        union += hi - max(lo, cur_end)
        cur_end = hi
    # true layouts tile with (near) zero overlap; overlapping framings are junk,
    # so count every overlapped byte against the score
    total = sum(hi - lo for lo, hi in spans)
    score = 2 * union - total
    # usize-slot sanity: real containers compress a few-fold; a framing whose
    # "uncompressed" total dwarfs the on-disk bytes is reading the wrong dword
    if usize_sum > 32 * total:
        score //= 64
    return score, flagged == count


found = None
best_key = -1
for count in counts:
    # candidate index starts: header-adjacent sweep, every header word, and
    # fsize-word (tail index) with a variable-size marker/prefix (0..20 bytes:
    # 4 in the .nhd, 12 in TravelDB)
    starts = set(range(0x40, min(0x800, fsize), 4))
    for hoff in range(0x0C, 0x60, 4):
        (w,) = struct.unpack_from("<I", data, hoff)
        for s in (w, fsize - w):
            if 0x40 <= s < fsize:
                starts.update(range(s, s + 5))
                starts.update(range(s + 8, s + 21, 4))
    for entry_len in (16, 20, 24, 28, 32, 36):
        if entry_len * count > fsize:
            continue
        words = entry_len // 4
        for start in sorted(starts):
            if start + count * entry_len > fsize:
                continue
            for i_off in range(words):
                for i_size in range(words):
                    if i_size == i_off:
                        continue
                    for i_csize in range(words):
                        if i_csize in (i_off, i_size):
                            continue
                        key, all_flagged = tiling_score(count, entry_len, start,
                                                        i_off, i_size, i_csize,
                                                        0x7FFFFFFF)
                        key = key * 2 if all_flagged else key
                        if key > best_key or (key == best_key and found is not None
                                              and entry_len > found[1]):
                            # equal scores: prefer the larger entry_len — it
                            # captures every field; smaller lens that tile the
                            # same spans are degenerate projections
                            best_key = key
                            found = (count, entry_len, start, i_off, i_size, i_csize)
                            print(f"--> candidate: count={count} entryLen={entry_len} "
                                  f"start={start:#x} off=+{i_off * 4:#x} size=+{i_size * 4:#x} "
                                  f"csize=+{i_csize * 4:#x} flagged={all_flagged} tiling={key:#x}")

if not found:
    print("NO INDEX FOUND — layout unrecognized")
    sys.exit(1)

count, entry_len, start, i_off, i_size, i_csize = found
end_index = start + count * entry_len
print(f"index chosen: records={count} entryLen={entry_len} range={start:#x}..{end_index:#x} "
      f"(payload 0x60..{start:#x} = {start - 0x60} bytes)")
print("first 3 records (all dwords):")
for i in range(min(3, count)):
    dwords = struct.unpack_from(f"<{entry_len // 4}I", data, start + i * entry_len)
    print(f"  [{i}] {[hex(d) for d in dwords]}")

usize_slot = (i_csize + 1) % (entry_len // 4)
type_slot = (usize_slot + 2) % (entry_len // 4)
if type_slot in (i_off, i_csize, usize_slot):
    type_slot = next(s for s in range(entry_len // 4)
                     if s not in (i_off, i_csize, usize_slot))
print(f"slots: type=+{type_slot * 4:#x} off=+{i_off * 4:#x} csize=+{i_csize * 4:#x} "
      f"usize=+{usize_slot * 4:#x}")
stats = defaultdict(lambda: [0, 0, 0, 0])  # type -> [count, csize_total, usize_total, max_usize]
csize_sum = usize_sum = 0
for i in range(count):
    base = start + i * entry_len
    (t,) = struct.unpack_from("<I", data, base + type_slot * 4)
    csz = struct.unpack_from("<I", data, base + i_csize * 4)[0] & 0x7FFFFFFF
    usz = struct.unpack_from("<I", data, base + usize_slot * 4)[0]
    s = stats[t]
    s[0] += 1
    s[1] += csz
    s[2] += usz
    s[3] = max(s[3], usz)
    csize_sum += csz
    usize_sum += usz

print(f"parsed {count} records, {len(stats)} distinct types, typeSlot=+{type_slot * 4:#x}")
print(f"on-disk compressed {csize_sum} bytes ({csize_sum / (start - 0x60):.1%} of payload region), "
      f"uncompressed total {usize_sum} bytes (ratio {usize_sum / max(csize_sum, 1):.2f}x)")
print("top types by compressed size: type  count  csize  usize  maxUsize")
for t, (c, cs, us, mx) in sorted(stats.items(), key=lambda kv: -kv[1][1])[:18]:
    print(f"  {t:#010x} {c:6d} {cs:9d} {us:10d} {mx:9d}")
print("top types by record count:")
for t, (c, cs, us, mx) in sorted(stats.items(), key=lambda kv: -kv[1][0])[:10]:
    print(f"  {t:#010x} {c:6d} {cs:9d} {us:10d} {mx:9d}")
