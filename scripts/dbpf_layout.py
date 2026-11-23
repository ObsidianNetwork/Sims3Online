"""Shared DBPF-2.0 tail-index locator + record parser for TS3 save containers.

Extracted verbatim from the validated scripts/dbpf_dump.py logic (passes 18-19:
nhd/ExportDB 32-byte entries, TravelDB 24-byte entries, Meta.data 1 record).
No layout is assumed: the index is located by validating (offset,size) slot pairs
and scoring how well compressed spans tile the payload region.
"""
import struct

MASK = 0x7FFFFFFF


def _tiling_score(data, fsize, count, entry_len, start, i_off, i_size, i_csize):
    """(score, all_csize_flagged): union of record spans minus overlaps; the
    true layout tiles the payload region almost exactly. Bit-31-flagged csize
    and usize-ratio sanity are folded in by the caller/scoring."""
    end_index = start + count * entry_len
    spans = []
    flagged = 0
    usize_sum = 0
    for i in range(count):
        base = start + i * entry_len
        off = struct.unpack_from("<I", data, base + i_off * 4)[0]
        raw = struct.unpack_from("<I", data, base + i_csize * 4)[0]
        csz = raw & MASK
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
    total = sum(hi - lo for lo, hi in spans)
    score = 2 * union - total
    if usize_sum > 32 * total:  # usize slot reading junk (type dwords etc.)
        score //= 64
    return score, flagged == count


def locate_index(data, verbose=False):
    """Locate the record index; returns a layout dict or None.

    {count, entry_len, start, type_off, off_off, csize_off, usize_off, end_index}
    — the *_off values are byte offsets of the field within one index entry.
    """
    fsize = len(data)
    counts = [w for off in range(0x10, 0x40, 4)
              for w in [struct.unpack_from("<I", data, off)[0]] if 0 < w < 5_000_000]
    found = None
    best_key = -1
    for count in counts:
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
                            key, all_flagged = _tiling_score(
                                data, fsize, count, entry_len, start,
                                i_off, i_size, i_csize)
                            key = key * 2 if all_flagged else key
                            if key > best_key or (key == best_key and found is not None
                                                  and entry_len > found[1]):
                                best_key = key
                                found = (count, entry_len, start, i_off, i_size, i_csize)
                                if verbose:
                                    print(f"--> candidate: count={count} "
                                          f"entryLen={entry_len} start={start:#x} "
                                          f"off=+{i_off * 4:#x} size=+{i_size * 4:#x} "
                                          f"csize=+{i_csize * 4:#x} "
                                          f"flagged={all_flagged} tiling={key:#x}")
    if not found:
        return None
    count, entry_len, start, i_off, i_size, i_csize = found
    words = entry_len // 4
    usize_off = ((i_csize + 1) % words) * 4
    type_off = ((usize_off // 4) + 2) % words * 4
    if type_off in (i_off * 4, i_csize * 4, usize_off):
        type_off = next(s * 4 for s in range(words)
                        if s * 4 not in (i_off * 4, i_csize * 4, usize_off))
    return {"count": count, "entry_len": entry_len, "start": start,
            "type_off": type_off, "off_off": i_off * 4,
            "csize_off": i_csize * 4, "usize_off": usize_off,
            "end_index": start + count * entry_len}


def parse_records(data, layout):
    """Yield (type, group, iid_lo, iid_hi, off, csz, usz, flags) per index entry.

    id fields (group/instance) are the dwords strictly between the type slot and
    the offset slot (cyclically) — 32-byte entries carry group + u64 instance,
    24-byte TravelDB entries only a u32 instance.
    """
    words = layout["entry_len"] // 4
    t_slot = layout["type_off"] // 4
    o_slot = layout["off_off"] // 4
    id_slots = []
    s = (t_slot + 1) % words
    while s != o_slot:
        id_slots.append(s)
        s = (s + 1) % words
    c_off, u_off = layout["csize_off"], layout["usize_off"]
    t_off = layout["type_off"]
    for i in range(layout["count"]):
        base = layout["start"] + i * layout["entry_len"]
        (t,) = struct.unpack_from("<I", data, base + t_off)
        ids = [struct.unpack_from("<I", data, base + s * 4)[0] for s in id_slots]
        group = ids[0] if len(ids) == 3 else 0
        ilo = ids[1] if len(ids) == 3 else (ids[0] if ids else 0)
        ihi = ids[2] if len(ids) == 3 else 0
        off = struct.unpack_from("<I", data, base + o_slot * 4)[0]
        csz = struct.unpack_from("<I", data, base + c_off)[0] & MASK
        usz = struct.unpack_from("<I", data, base + u_off)[0]
        fl = struct.unpack_from("<I", data, base + (u_off + 4) % layout["entry_len"])[0]
        yield t, group, ilo, ihi, off, csz, usz, fl
