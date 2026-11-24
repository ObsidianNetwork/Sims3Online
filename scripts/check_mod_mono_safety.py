"""Detect TS3 EA Mono <PrivateImplementationDetails> hazards in C# mod sources and compiled .NET 2 DLLs.

Why this exists:
  In TS3W.exe's embedded Mono runtime, mono_class_value_size returns
  instance_size - 4 bytes, which is 12 bytes instead of 8 bytes for boxed-8-byte-aligned
  primitives such as System.Int64 (int64), System.UInt64 (uint64), and System.Double (float64).
  When mono_class_vtable initializes a static FieldRVA field, it copies
  mono_class_value_size bytes into an 8-byte slot in vtable->data, overrunning the
  next 4 bytes in <PrivateImplementationDetails> static_data with adjacent PE RVA bytes
  (e.g., an ECMA-335 fat method header like 0x0005301b).

  When .NET 2 csc.exe also emits a compiler-generated Dictionary<string, int> static field
  on <PrivateImplementationDetails> for a >=6-case string switch, that clobbered word turns
  into a bogus non-null Dictionary pointer and crashes Dictionary`2::TryGetValue -> GetSlot
  inside TS3W.exe on both client and server.

This module checks:
  1. C# sources under SimsOnlineServerHostMod/ (runs in CI without game assemblies):
     - string switches with >=5 cases (headroom below csc.exe's 6-case Dictionary threshold)
     - constant primitive array initializers of >=3 elements totaling 8 bytes (emitted as int64 HasFieldRVA)
  2. Compiled .NET 2 assemblies (when built via check_mod_runtime.py / check-mod-compile.ps1):
     - ECMA-335 FieldRVA fields typed as int64 (I8), uint64 (U8), or float64 (R8)
     - <PrivateImplementationDetails> static fields holding reference/generic types (Dictionary<string, int>, etc.)

Usage:
    python scripts/check_mod_mono_safety.py [--dll path/to/mod.dll ...] [--sources]
"""

from __future__ import annotations

import argparse
import re
import struct
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MOD_ROOT = ROOT / "SimsOnlineServerHostMod"

# Compiler-generated or packaging-only files not shipped in game mod assemblies (or generated on build).
SKIP_SOURCE_NAMES = frozenset({"BuildStamp.cs", "EmbeddedHandlerAssembly.cs", "Sims3PackageBuilder.cs"})

STRING_SWITCH_CASE_LIMIT = 5

ELEMENT_SIZES = {
    "byte": 1,
    "sbyte": 1,
    "short": 2,
    "ushort": 2,
    "int": 4,
    "uint": 4,
    "long": 8,
    "ulong": 8,
    "float": 4,
    "double": 8,
}

ARRAY_INIT_RE = re.compile(
    r"\bnew\s+(byte|sbyte|short|ushort|int|uint|long|ulong|float|double)\s*\[\s*\]\s*\{([^}]*)\}",
    re.MULTILINE,
)

ELEMENT_TYPE_NAMES = {
    0x02: "void",
    0x03: "bool",
    0x04: "char",
    0x05: "i1",
    0x06: "u1",
    0x07: "i2",
    0x08: "u2",
    0x09: "i4",
    0x0A: "i8",
    0x0B: "u8",
    0x0C: "r4",
    0x0D: "r8",
    0x0E: "string",
    0x0F: "ptr",
    0x10: "byref",
    0x11: "valuetype",
    0x12: "class",
    0x13: "var",
    0x14: "array",
    0x15: "genericinst",
    0x16: "typedbyref",
    0x18: "i",
    0x19: "u",
    0x1B: "fnptr",
    0x1C: "object",
    0x1D: "szarray",
    0x1E: "mvar",
}

UNSAFE_FIELD_RVA_TYPES = frozenset({0x0A, 0x0B, 0x0D})  # int64, uint64, float64
UNSAFE_PRIV_REF_TYPES = frozenset({0x0E, 0x12, 0x14, 0x15, 0x1C, 0x1D})  # string/class/array/genericinst/object/szarray


@dataclass(frozen=True)
class Hazard:
    location: str
    kind: str
    detail: str

    def format(self) -> str:
        return f"{self.location}: [{self.kind}] {self.detail}"


def _tokenize_csharp(text: str) -> list[tuple[str, str, int]]:
    """Strip comments and tokenize identifiers/strings/punctuation while tracking 1-based line numbers."""
    tokens: list[tuple[str, str, int]] = []
    i = 0
    n = len(text)
    line = 1
    while i < n:
        ch = text[i]
        if ch == "\n":
            line += 1
            i += 1
            continue
        if ch.isspace():
            i += 1
            continue
        # Line comment
        if text.startswith("//", i):
            nl = text.find("\n", i)
            if nl == -1:
                break
            line += 1
            i = nl + 1
            continue
        # Block comment
        if text.startswith("/*", i):
            end = text.find("*/", i + 2)
            if end == -1:
                break
            line += text.count("\n", i, end + 2)
            i = end + 2
            continue
        # Verbatim string @"..."
        if text.startswith('@"', i):
            start_line = line
            i += 2
            buf = ['"']
            while i < n:
                if text[i] == '"':
                    if i + 1 < n and text[i + 1] == '"':
                        buf.append('"')
                        i += 2
                        continue
                    i += 1
                    break
                if text[i] == "\n":
                    line += 1
                buf.append(text[i])
                i += 1
            buf.append('"')
            tokens.append(("STR", "".join(buf), start_line))
            continue
        # Quoted string "..." or char literal '...'
        if ch in ('"', "'"):
            start_line = line
            quote = ch
            kind = "STR" if quote == '"' else "CHR"
            i += 1
            buf = [quote]
            while i < n:
                c = text[i]
                if c == "\\" and i + 1 < n:
                    if text[i + 1] == "\n":
                        line += 1
                    buf.append(c)
                    buf.append(text[i + 1])
                    i += 2
                    continue
                if c == quote:
                    i += 1
                    break
                if c == "\n":
                    line += 1
                buf.append(c)
                i += 1
            buf.append(quote)
            tokens.append((kind, "".join(buf), start_line))
            continue
        # Identifier or keyword
        if ch.isalpha() or ch == "_":
            j = i + 1
            while j < n and (text[j].isalnum() or text[j] == "_"):
                j += 1
            tokens.append(("ID", text[i:j], line))
            i = j
            continue
        # Punctuation
        if ch in "(){}[]:;.":
            tokens.append(("PUNC", ch, line))
            i += 1
            continue
        i += 1
    return tokens


def _collect_string_constants(tokens: list[tuple[str, str, int]]) -> set[str]:
    """Find `const string Foo = "..."` names so constant-based string switches are also caught."""
    names: set[str] = set()
    for idx in range(len(tokens) - 4):
        if (
            tokens[idx][1] == "const"
            and tokens[idx + 1][1] == "string"
            and tokens[idx + 2][0] == "ID"
        ):
            names.add(tokens[idx + 2][1])
    return names


def _strip_comments_preserve_lines(text: str) -> str:
    """Replace // and /* */ comments with spaces (preserving newlines) outside string literals."""
    out: list[str] = []
    i = 0
    n = len(text)
    while i < n:
        ch = text[i]
        if text.startswith("//", i):
            nl = text.find("\n", i)
            if nl == -1:
                break
            out.append("\n")
            i = nl + 1
            continue
        if text.startswith("/*", i):
            end = text.find("*/", i + 2)
            if end == -1:
                break
            out.append("\n" * text.count("\n", i, end + 2))
            i = end + 2
            continue
        if text.startswith('@"', i):
            j = i + 2
            while j < n:
                if text[j] == '"':
                    if j + 1 < n and text[j + 1] == '"':
                        j += 2
                        continue
                    j += 1
                    break
                j += 1
            out.append(text[i:j])
            i = j
            continue
        if ch in ('"', "'"):
            j = i + 1
            while j < n:
                if text[j] == "\\" and j + 1 < n:
                    j += 2
                    continue
                if text[j] == ch:
                    j += 1
                    break
                j += 1
            out.append(text[i:j])
            i = j
            continue
        out.append(ch)
        i += 1
    return "".join(out)


def scan_csharp_source(path: Path, text: str, known_string_consts: set[str] | None = None) -> list[Hazard]:
    """Scan a single C# source string for EA Mono <PrivateImplementationDetails> hazards."""
    hazards: list[Hazard] = []
    tokens = _tokenize_csharp(text)
    const_names = set(known_string_consts or ()) | _collect_string_constants(tokens)

    # 1. String switches with >= STRING_SWITCH_CASE_LIMIT case labels
    i = 0
    ntok = len(tokens)
    while i < ntok:
        kind, val, sw_line = tokens[i]
        if kind == "ID" and val == "switch" and i + 1 < ntok and tokens[i + 1][1] == "(":
            # Balance switch (...)
            pdepth = 0
            j = i + 1
            while j < ntok:
                if tokens[j][1] == "(":
                    pdepth += 1
                elif tokens[j][1] == ")":
                    pdepth -= 1
                    if pdepth == 0:
                        j += 1
                        break
                j += 1
            if j < ntok and tokens[j][1] == "{":
                bdepth = 0
                k = j
                string_cases = 0
                while k < ntok:
                    tk, tv, _ = tokens[k]
                    if tk == "PUNC" and tv == "{":
                        bdepth += 1
                    elif tk == "PUNC" and tv == "}":
                        bdepth -= 1
                        if bdepth == 0:
                            break
                    elif bdepth == 1 and tk == "ID" and tv == "case" and k + 1 < ntok:
                        # Scan label tokens up to ':'
                        lbl: list[tuple[str, str]] = []
                        m = k + 1
                        while m < ntok and tokens[m][1] != ":":
                            lbl.append((tokens[m][0], tokens[m][1]))
                            m += 1
                        if any(lk == "STR" for lk, _ in lbl) or (lbl and lbl[-1][0] == "ID" and lbl[-1][1] in const_names):
                            string_cases += 1
                    k += 1
                if string_cases >= STRING_SWITCH_CASE_LIMIT:
                    hazards.append(
                        Hazard(
                            f"{path}:{sw_line}",
                            "string-switch",
                            f"string switch has {string_cases} cases (>= {STRING_SWITCH_CASE_LIMIT}); "
                            ".NET 2 csc.exe generates <PrivateImplementationDetails> Dictionary<string, int> at >= 6 cases",
                        )
                    )
        i += 1

    # 2. Inline primitive array initializers of >= 3 elements whose byte payload equals 8 bytes
    cleaned = _strip_comments_preserve_lines(text)
    for m in ARRAY_INIT_RE.finditer(cleaned):
        elem_type = m.group(1)
        body = m.group(2).strip()
        if not body:
            continue
        items = [part.strip() for part in body.split(",") if part.strip()]
        elem_count = len(items)
        byte_size = elem_count * ELEMENT_SIZES[elem_type]
        if elem_count >= 3 and byte_size == 8:
            line_no = cleaned.count("\n", 0, m.start()) + 1
            hazards.append(
                Hazard(
                    f"{path}:{line_no}",
                    "int64-field-rva-array",
                    f"`new {elem_type}[] {{ ... }}` has {elem_count} elements (8 bytes); "
                    ".NET 2 csc.exe emits an int64 HasFieldRVA field that overruns static_data by 4 bytes in TS3 Mono",
                )
            )

    return hazards


def scan_mod_sources(mod_root: Path = MOD_ROOT) -> list[Hazard]:
    """Scan all shipped C# sources in SimsOnlineServerHostMod/ (shell, Handlers, Presentation)."""
    files: list[tuple[Path, str]] = []
    global_consts: set[str] = set()
    for cs in sorted(mod_root.rglob("*.cs")):
        if cs.name in SKIP_SOURCE_NAMES:
            continue
        txt = cs.read_text(encoding="utf-8", errors="replace")
        files.append((cs, txt))
        global_consts.update(_collect_string_constants(_tokenize_csharp(txt)))

    hazards: list[Hazard] = []
    for cs, txt in files:
        try:
            rel = cs.relative_to(ROOT)
        except ValueError:
            rel = cs
        hazards.extend(scan_csharp_source(rel, txt, global_consts))
    return hazards


def _read_c_string(buf: bytes, offset: int) -> str:
    if offset <= 0 or offset >= len(buf):
        return ""
    end = buf.find(b"\x00", offset)
    if end == -1:
        end = len(buf)
    return buf[offset:end].decode("utf-8", errors="replace")


def _read_compressed_uint(buf: bytes, offset: int) -> tuple[int, int]:
    if offset >= len(buf):
        return 0, offset
    b0 = buf[offset]
    if (b0 & 0x80) == 0:
        return b0, offset + 1
    if (b0 & 0xC0) == 0x80 and offset + 1 < len(buf):
        return ((b0 & 0x3F) << 8) | buf[offset + 1], offset + 2
    if (b0 & 0xE0) == 0xC0 and offset + 3 < len(buf):
        return (
            ((b0 & 0x1F) << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3],
            offset + 4,
        )
    return 0, offset + 1


def _decode_field_sig_elem_type(blob_heap: bytes, sig_idx: int) -> int | None:
    """Return the underlying ECMA-335 ELEMENT_TYPE_* byte of a FieldSig blob."""
    if sig_idx <= 0 or sig_idx >= len(blob_heap):
        return None
    length, pos = _read_compressed_uint(blob_heap, sig_idx)
    end = min(len(blob_heap), pos + length)
    if pos >= end or blob_heap[pos] != 0x06:  # FIELD calling convention
        return None
    pos += 1
    while pos < end and blob_heap[pos] in (0x1F, 0x20):  # CMOD_REQD / CMOD_OPT
        pos += 1
        _, pos = _read_compressed_uint(blob_heap, pos)
    if pos >= end:
        return None
    return blob_heap[pos]


def scan_clr_hazards(pe_path: Path | None = None, pe_bytes: bytes | None = None, label: str | None = None) -> list[Hazard]:
    """Parse a .NET assembly's CLR #~ tables directly and report TS3 Mono <PrivateImplementationDetails> hazards."""
    import pefile

    display = label or (str(pe_path) if pe_path is not None else "<memory>")
    pe = pefile.PE(str(pe_path), fast_load=True) if pe_path is not None else pefile.PE(data=pe_bytes, fast_load=True)
    try:
        dirs = pe.OPTIONAL_HEADER.DATA_DIRECTORY
        if len(dirs) <= 14 or dirs[14].VirtualAddress == 0:
            return []
        cli_rva = dirs[14].VirtualAddress
        cli = pe.get_data(cli_rva, 72)
        if len(cli) < 16:
            return []
        md_rva, md_size = struct.unpack_from("<II", cli, 8)
        if md_rva == 0 or md_size < 16:
            return []
        md = pe.get_data(md_rva, md_size)
        if md[:4] != b"BSJB":
            return []
        ver_len = struct.unpack_from("<I", md, 12)[0]
        pos = 16 + ((ver_len + 3) & ~3) + 2
        stream_count = struct.unpack_from("<H", md, pos)[0]
        pos += 2
        streams: dict[str, bytes] = {}
        for _ in range(stream_count):
            s_off, s_size = struct.unpack_from("<II", md, pos)
            pos += 8
            end = md.find(b"\x00", pos)
            name = md[pos:end].decode("ascii", errors="replace")
            pos = ((end + 1) + 3) & ~3
            streams[name] = md[s_off : s_off + s_size]

        tables_stream = streams.get("#~") or streams.get("#-")
        strings_heap = streams.get("#Strings", b"")
        blob_heap = streams.get("#Blob", b"")
        if not tables_stream or len(tables_stream) < 24:
            return []

        heap_sizes = tables_stream[6]
        valid_mask = struct.unpack_from("<Q", tables_stream, 8)[0]
        tpos = 24
        row_counts: dict[int, int] = {}
        for tid in range(64):
            if (valid_mask >> tid) & 1:
                row_counts[tid] = struct.unpack_from("<I", tables_stream, tpos)[0]
                tpos += 4

        str_sz = 4 if (heap_sizes & 0x01) else 2
        guid_sz = 4 if (heap_sizes & 0x02) else 2
        blob_sz = 4 if (heap_sizes & 0x04) else 2

        def idx_sz(tid: int) -> int:
            return 4 if row_counts.get(tid, 0) >= 65536 else 2

        def coded_sz(tag_bits: int, tids: tuple[int, ...]) -> int:
            threshold = 1 << (16 - tag_bits)
            return 4 if any(row_counts.get(t, 0) >= threshold for t in tids) else 2

        # ECMA-335 II.22 table row widths up to 0x1D (FieldRVA)
        row_widths = {
            0x00: 2 + str_sz + guid_sz * 3,
            0x01: coded_sz(2, (0x00, 0x1A, 0x23, 0x01)) + str_sz + str_sz,
            0x02: 4 + str_sz + str_sz + coded_sz(2, (0x02, 0x01, 0x1B)) + idx_sz(0x04) + idx_sz(0x06),
            0x03: idx_sz(0x04),
            0x04: 2 + str_sz + blob_sz,
            0x05: idx_sz(0x06),
            0x06: 4 + 2 + 2 + str_sz + blob_sz + idx_sz(0x08),
            0x07: idx_sz(0x08),
            0x08: 2 + 2 + str_sz,
            0x09: idx_sz(0x02) + coded_sz(2, (0x02, 0x01, 0x1B)),
            0x0A: coded_sz(3, (0x02, 0x01, 0x1A, 0x06, 0x1B)) + str_sz + blob_sz,
            0x0B: 2 + coded_sz(2, (0x04, 0x08, 0x17)) + blob_sz,
            0x0C: coded_sz(5, (0x06, 0x04, 0x01, 0x02, 0x08, 0x09, 0x0A, 0x00, 0x0E, 0x17, 0x14, 0x11, 0x1A, 0x1B, 0x20, 0x23, 0x26, 0x27, 0x28))
            + coded_sz(3, (0x06, 0x0A))
            + blob_sz,
            0x0D: coded_sz(1, (0x04, 0x08)) + blob_sz,
            0x0E: 2 + coded_sz(2, (0x02, 0x06, 0x20)) + blob_sz,
            0x0F: 2 + 4 + idx_sz(0x02),
            0x10: 4 + idx_sz(0x04),
            0x11: blob_sz,
            0x12: idx_sz(0x02) + idx_sz(0x14),
            0x13: idx_sz(0x14),
            0x14: 2 + str_sz + coded_sz(2, (0x02, 0x01, 0x1B)),
            0x15: idx_sz(0x02) + idx_sz(0x17),
            0x16: idx_sz(0x17),
            0x17: 2 + str_sz + blob_sz,
            0x18: 2 + idx_sz(0x06) + coded_sz(1, (0x14, 0x17)),
            0x19: idx_sz(0x02) + coded_sz(1, (0x06, 0x0A)) + coded_sz(1, (0x06, 0x0A)),
            0x1A: str_sz,
            0x1B: blob_sz,
            0x1C: 2 + coded_sz(1, (0x04, 0x06)) + str_sz + idx_sz(0x1A),
            0x1D: 4 + idx_sz(0x04),
        }

        table_offsets: dict[int, int] = {}
        cur = tpos
        for tid in range(0x1E):
            cnt = row_counts.get(tid, 0)
            table_offsets[tid] = cur
            cur += cnt * row_widths[tid]

        def read_u(offset: int, width: int) -> int:
            return struct.unpack_from("<I", tables_stream, offset)[0] if width == 4 else struct.unpack_from("<H", tables_stream, offset)[0]

        # Read TypeDef rows to map Field RID ranges to owning type names
        td_count = row_counts.get(0x02, 0)
        td_off = table_offsets[0x02]
        td_width = row_widths[0x02]
        td_or_ref_sz = coded_sz(2, (0x02, 0x01, 0x1B))
        fld_idx_sz = idx_sz(0x04)

        priv_field_ids: dict[int, str] = {}
        type_field_starts: list[tuple[str, int]] = []
        for r in range(td_count):
            row = td_off + r * td_width
            name_idx = read_u(row + 4, str_sz)
            tname = _read_c_string(strings_heap, name_idx)
            flist = read_u(row + 4 + str_sz * 2 + td_or_ref_sz, fld_idx_sz)
            type_field_starts.append((tname, flist))

        total_fields = row_counts.get(0x04, 0)
        for idx, (tname, start) in enumerate(type_field_starts):
            end = type_field_starts[idx + 1][1] if idx + 1 < len(type_field_starts) else total_fields + 1
            if "<PrivateImplementationDetails>" in tname:
                for fid in range(start, end):
                    priv_field_ids[fid] = tname

        # Collect FieldRVA rows
        rva_fields: dict[int, int] = {}
        frva_count = row_counts.get(0x1D, 0)
        frva_off = table_offsets[0x1D]
        frva_width = row_widths[0x1D]
        for r in range(frva_count):
            row = frva_off + r * frva_width
            rva = struct.unpack_from("<I", tables_stream, row)[0]
            fid = read_u(row + 4, fld_idx_sz)
            rva_fields[fid] = rva

        # Scan Field table
        hazards: list[Hazard] = []
        f_off = table_offsets[0x04]
        f_width = row_widths[0x04]
        for r in range(total_fields):
            fid = r + 1
            row = f_off + r * f_width
            flags = struct.unpack_from("<H", tables_stream, row)[0]
            fname = _read_c_string(strings_heap, read_u(row + 2, str_sz))
            sig_idx = read_u(row + 2 + str_sz, blob_sz)
            etype = _decode_field_sig_elem_type(blob_heap, sig_idx)
            etype_name = ELEMENT_TYPE_NAMES.get(etype or -1, f"0x{etype:02x}" if etype is not None else "unknown")

            if (flags & 0x0100) or (fid in rva_fields):
                if etype in UNSAFE_FIELD_RVA_TYPES:
                    rva = rva_fields.get(fid, 0)
                    owner = priv_field_ids.get(fid, "<type>")
                    hazards.append(
                        Hazard(
                            f"{display} ({owner}::{fname})",
                            "clr-int64-field-rva",
                            f"HasFieldRVA field at RVA 0x{rva:08X} has type {etype_name}; "
                            "TS3 Mono mono_class_value_size returns +4 bytes and overruns static_data",
                        )
                    )

            if fid in priv_field_ids and etype in UNSAFE_PRIV_REF_TYPES:
                owner = priv_field_ids[fid]
                hazards.append(
                    Hazard(
                        f"{display} ({owner}::{fname})",
                        "clr-priv-ref-field",
                        f"<PrivateImplementationDetails> holds managed reference static field ({etype_name}), "
                        "matching a compiler-generated string-switch Dictionary<string, int>",
                    )
                )

        return hazards
    finally:
        pe.close()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--sources", action="store_true", help="Scan SimsOnlineServerHostMod C# sources")
    parser.add_argument("--dll", type=Path, action="append", default=[], help="Scan a compiled .NET assembly (repeatable)")
    args = parser.parse_args(argv)

    run_sources = args.sources or not args.dll
    hazards: list[Hazard] = []
    if run_sources:
        hazards.extend(scan_mod_sources(MOD_ROOT))
    for dll in args.dll:
        hazards.extend(scan_clr_hazards(pe_path=dll))

    for h in hazards:
        print("MONO HAZARD: " + h.format())
    print(f"mod mono safety check: {'ok' if not hazards else str(len(hazards)) + ' hazard(s)'}")
    return 1 if hazards else 0


if __name__ == "__main__":
    raise SystemExit(main())
