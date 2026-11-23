"""RefPack (QFS 0x?xFB) decoder + save-record string extraction for type attribution.

Command table (TS2/TSO variant, per ModTheSims DBPF_Compression):
  0x00-0x7F (2B): plain=b0&3          copy=((b0&0x1C)>>2)+3  off=((b0&0x60)<<3)+b1+1
  0x80-0xBF (3B): plain=(b1>>6)&3     copy=(b0&0x3F)+4       off=((b1&0x3F)<<8)+b2+1
  0xC0-0xDF (4B): plain=b0&3          copy=((b0&0x0C)<<6)+b3+5  off=((b0&0x10)<<12)+(b1<<8)+b2+1
  0xE0-0xFB (1B): plain=((b0&0x1F)+1)*4
  0xFC-0xFF (1B): plain=b0&3 trailing literals; no STOP (the loop ends on length)
The three copy rows and the literal run up to 0xFB are that spec. The last row is TS3's own: ModTheSims keeps 0xFC in
the run (a 116-byte one) and stops at 0xFD-0xFF, and TS3 does neither -- refpack() records the .nhd evidence.
Record payload = [flags byte][0xFB][3-byte BE usize (4 if flags&1)]; copies read byte-wise
(overlapping runs allowed). Index usize field is the per-record oracle.
"""
import struct
import sys
from collections import Counter, defaultdict

# verified Sunset Valley .nhd layout (32-byte entries, type/group/iid64/off/csize/usize/flags)
START, COUNT, ENTRY, CSIZE_OFF, USIZE_OFF = 0x172C259, 6208, 32, 0x14, 0x18


def refpack(stream):
    """stream = payload AFTER the 5-byte header (flags, 0xFB, 3-byte usize).
    Termination is length-driven: the QFS loop ends when the stream is consumed
    (0xFC-0xFF are 0-3 trailing literals, not a stop code — verified: Meta.data
    record ends ...00 FC before its index entry)."""
    out = bytearray()
    p = 0
    end = len(stream)
    while p < end:
        ctrl = stream[p]
        p += 1
        if ctrl >= 0xFC:  # 0-3 trailing literals, no copy
            sn = ctrl & 0x03
            out += stream[p:p + sn]
            p += sn
            continue
        if ctrl >= 0xE0:  # literal run: ((sn)+1)*4 bytes
            sn = (ctrl & 0x1F) + 1
            out += stream[p:p + 4 * sn]
            p += 4 * sn
        elif ctrl >= 0xC0:  # TS2/TSO long copy
            sn = ctrl & 0x03
            do = ((ctrl & 0x10) << 12) | (stream[p] << 8) | stream[p + 1]
            dn = ((ctrl & 0x0C) << 6) | stream[p + 2]
            p += 3
            out += stream[p:p + sn]
            p += sn
            src = len(out) - do - 1
            for i in range(dn + 5):
                out.append(out[src + i])
        elif ctrl >= 0x80:  # medium copy: plain bits are byte1's top two
            b1, b2 = stream[p], stream[p + 1]
            p += 2
            sn = (b1 >> 6) & 0x03
            do = ((b1 & 0x3F) << 8) | b2
            out += stream[p:p + sn]
            p += sn
            src = len(out) - do - 1
            for i in range((ctrl & 0x3F) + 4):
                out.append(out[src + i])
        else:  # short copy
            sn = ctrl & 0x03
            do = ((ctrl & 0x60) << 3) | stream[p]
            p += 1
            out += stream[p:p + sn]
            p += sn
            src = len(out) - do - 1
            for i in range(((ctrl & 0x1C) >> 2) + 3):
                out.append(out[src + i])
    return bytes(out)


def strings(blob, min_len=6):
    found = []
    run = bytearray()
    for b in blob:
        if 32 <= b < 127:
            run.append(b)
        else:
            if len(run) >= min_len:
                found.append(run.decode("ascii", "replace"))
            run = bytearray()
    if len(run) >= min_len:
        found.append(run.decode("ascii", "replace"))
    run = bytearray()
    for i in range(0, len(blob) - 1, 2):  # UTF-16LE
        if blob[i + 1] == 0 and 32 <= blob[i] < 127:
            run += blob[i:i + 2]
        else:
            if len(run) // 2 >= min_len:
                found.append(run.decode("utf-16-le", "replace"))
            run = bytearray()
    if len(run) // 2 >= min_len:
        found.append(run.decode("utf-16-le", "replace"))
    return found


def main():
    if len(sys.argv) < 2 or sys.argv[1] in ("-h", "--help"):
        print(__doc__)
        print("Usage: python scripts/refpack.py <save.nhd> [start count entry [csize_off usize_off]]")
        sys.exit(0 if len(sys.argv) > 1 else 2)
    path = sys.argv[1]
    global START, COUNT, ENTRY, CSIZE_OFF, USIZE_OFF
    if len(sys.argv) > 3:  # path start count entry [csize_off usize_off]
        START = int(sys.argv[2], 0)
        COUNT = int(sys.argv[3], 0)
        ENTRY = int(sys.argv[4], 0)
        if len(sys.argv) > 5:
            CSIZE_OFF = int(sys.argv[5], 0)
            USIZE_OFF = int(sys.argv[6], 0)
    data = open(path, "rb").read()
    per_type = defaultdict(Counter)
    ok = raw = fail = usize_mismatch = 0
    usize_total = 0
    for i in range(COUNT):
        b = START + i * ENTRY
        t, _g, _ilo, _ihi, off, csw, idx_usz, _fl = struct.unpack_from("<8I", data, b)
        csz = csw & 0x7FFFFFFF
        blob = data[off:off + csz]
        try:
            if len(blob) >= 5 and blob[1] == 0xFB and (blob[0] & 0xFC) in (0x10, 0x50, 0x90):
                dec = refpack(blob[5:])  # 5-byte header: flags, 0xFB, 3-byte BE usize
                if len(dec) != idx_usz:
                    usize_mismatch += 1
                ok += 1
            else:
                dec = blob
                raw += 1
            usize_total += len(dec)
            for s in strings(dec):
                per_type[t][s] += 1
        except Exception as e:  # noqa: BLE001 — report and continue
            fail += 1
            if fail <= 3:
                print(f"  decode fail type={t:#010x} off={off:#x}: {e}")
    print(f"records: {ok} decompressed ({usize_mismatch} length-mismatch vs index usize), "
          f"{raw} raw-stored, {fail} failed; decompressed total {usize_total} bytes")
    for t, strs in sorted(per_type.items(), key=lambda kv: -len(kv[1])):
        if strs:
            top = ", ".join(repr(s) for s, _ in strs.most_common(6))
            print(f"{t:#010x}: {len(strs)} distinct strings; top: {top}")
    silent = [t for t, s in per_type.items() if not s]
    print(f"types with no strings ({len(silent)}): "
          + ", ".join(f"{t:#010x}" for t in sorted(silent)))


if __name__ == "__main__":
    main()
