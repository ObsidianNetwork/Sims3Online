"""Check that a .NET assembly is a reference-only stub: API metadata and no game code.

The proprietary-input guard (scripts/proprietary_input_guard.py) uses this to allow the
Refasmer stubs in refs/sims3/ while still rejecting a real game assembly copied there. Standard
library only, so CI needs no extra packages.

An assembly passes when it is a managed PE with an assembly manifest, carries
System.Runtime.CompilerServices.ReferenceAssemblyAttribute, has no method bodies (every method
either has none or is the `throw null` placeholder reference compilers emit), no field data
(FieldRVA) and no embedded resources.

    python scripts/reference_assembly.py refs/sims3/*.dll
"""

from __future__ import annotations

import argparse
import struct
from dataclasses import dataclass
from pathlib import Path
from typing import Final

REFERENCE_ASSEMBLY_ATTRIBUTE: Final = ("System.Runtime.CompilerServices", "ReferenceAssemblyAttribute")
# A tiny-format IL body of `ldnull; throw`: what Roslyn and Refasmer put in a reference
# assembly's methods so the metadata stays loadable.
THROW_NULL_BODY: Final = bytes((0x0A, 0x14, 0x7A))

_CLI_HEADER_DIRECTORY: Final = 14
_METADATA_SIGNATURE: Final = 0x424A5342

# ECMA-335 II.22 table ids.
MODULE, TYPE_REF, TYPE_DEF, FIELD_PTR, FIELD, METHOD_PTR, METHOD_DEF, PARAM_PTR, PARAM = range(9)
INTERFACE_IMPL, MEMBER_REF, CONSTANT, CUSTOM_ATTRIBUTE = 0x09, 0x0A, 0x0B, 0x0C
EVENT, PROPERTY, MODULE_REF, TYPE_SPEC, FIELD_RVA = 0x14, 0x17, 0x1A, 0x1B, 0x1D
ASSEMBLY, ASSEMBLY_REF, FILE, EXPORTED_TYPE, MANIFEST_RESOURCE = 0x20, 0x23, 0x26, 0x27, 0x28
GENERIC_PARAM, METHOD_SPEC, GENERIC_PARAM_CONSTRAINT = 0x2A, 0x2B, 0x2C
_UNUSED: Final = -1

# Coded index kinds (II.24.2.6): the tables each tag value points into.
_CODED: Final[dict[str, tuple[int, ...]]] = {
    "TypeDefOrRef": (TYPE_DEF, TYPE_REF, TYPE_SPEC),
    "HasConstant": (FIELD, PARAM, PROPERTY),
    "HasCustomAttribute": (
        METHOD_DEF, FIELD, TYPE_REF, TYPE_DEF, PARAM, INTERFACE_IMPL, MEMBER_REF, MODULE, 0x0E,
        PROPERTY, EVENT, 0x11, MODULE_REF, TYPE_SPEC, ASSEMBLY, ASSEMBLY_REF, FILE, EXPORTED_TYPE,
        MANIFEST_RESOURCE, GENERIC_PARAM, GENERIC_PARAM_CONSTRAINT, METHOD_SPEC,
    ),
    "HasFieldMarshal": (FIELD, PARAM),
    "HasDeclSecurity": (TYPE_DEF, METHOD_DEF, ASSEMBLY),
    "MemberRefParent": (TYPE_DEF, TYPE_REF, MODULE_REF, METHOD_DEF, TYPE_SPEC),
    "HasSemantics": (EVENT, PROPERTY),
    "MethodDefOrRef": (METHOD_DEF, MEMBER_REF),
    "MemberForwarded": (FIELD, METHOD_DEF),
    "Implementation": (FILE, ASSEMBLY_REF, EXPORTED_TYPE),
    "CustomAttributeType": (_UNUSED, _UNUSED, METHOD_DEF, MEMBER_REF, _UNUSED),
    "ResolutionScope": (MODULE, MODULE_REF, ASSEMBLY_REF, TYPE_REF),
    "TypeOrMethodDef": (TYPE_DEF, METHOD_DEF),
}

# Column layouts (II.22). An int is a fixed width in bytes; "str"/"guid"/"blob" are heap
# indexes; ("t", id) is a simple index into table id; a str key of _CODED is a coded index.
_SCHEMA: Final[dict[int, tuple[object, ...]]] = {
    0x00: (2, "str", "guid", "guid", "guid"),
    0x01: ("ResolutionScope", "str", "str"),
    0x02: (4, "str", "str", "TypeDefOrRef", ("t", FIELD), ("t", METHOD_DEF)),
    0x03: (("t", FIELD),),
    0x04: (2, "str", "blob"),
    0x05: (("t", METHOD_DEF),),
    0x06: (4, 2, 2, "str", "blob", ("t", PARAM)),
    0x07: (("t", PARAM),),
    0x08: (2, 2, "str"),
    0x09: (("t", TYPE_DEF), "TypeDefOrRef"),
    0x0A: ("MemberRefParent", "str", "blob"),
    0x0B: (2, "HasConstant", "blob"),
    0x0C: ("HasCustomAttribute", "CustomAttributeType", "blob"),
    0x0D: ("HasFieldMarshal", "blob"),
    0x0E: (2, "HasDeclSecurity", "blob"),
    0x0F: (2, 4, ("t", TYPE_DEF)),
    0x10: (4, ("t", FIELD)),
    0x11: ("blob",),
    0x12: (("t", TYPE_DEF), ("t", EVENT)),
    0x13: (("t", EVENT),),
    0x14: (2, "str", "TypeDefOrRef"),
    0x15: (("t", TYPE_DEF), ("t", PROPERTY)),
    0x16: (("t", PROPERTY),),
    0x17: (2, "str", "blob"),
    0x18: (2, ("t", METHOD_DEF), "HasSemantics"),
    0x19: (("t", TYPE_DEF), "MethodDefOrRef", "MethodDefOrRef"),
    0x1A: ("str",),
    0x1B: ("blob",),
    0x1C: (2, "MemberForwarded", "str", ("t", MODULE_REF)),
    0x1D: (4, ("t", FIELD)),
    0x1E: (4, 4),
    0x1F: (4,),
    0x20: (4, 2, 2, 2, 2, 4, "blob", "str", "str"),
    0x21: (4,),
    0x22: (4, 4, 4),
    0x23: (2, 2, 2, 2, 4, "blob", "str", "str", "blob"),
    0x24: (4, ("t", ASSEMBLY_REF)),
    0x25: (4, 4, 4, ("t", ASSEMBLY_REF)),
    0x26: (4, "str", "blob"),
    0x27: (4, 4, "str", "str", "Implementation"),
    0x28: (4, 4, "str", "Implementation"),
    0x29: (("t", TYPE_DEF), ("t", TYPE_DEF)),
    0x2A: (2, 2, "TypeOrMethodDef", "str"),
    0x2B: ("MethodDefOrRef", "blob"),
    0x2C: (("t", GENERIC_PARAM), "TypeDefOrRef"),
}


class AssemblyFormatError(ValueError):
    """The file is not a managed assembly this reader can parse."""


@dataclass(frozen=True, slots=True)
class StubReport:
    methods: int
    method_bodies: int
    field_data: int
    resources: int
    has_reference_attribute: bool

    @property
    def problems(self) -> list[str]:
        found = []
        if not self.has_reference_attribute:
            found.append("no ReferenceAssemblyAttribute")
        if self.method_bodies:
            found.append(f"{self.method_bodies} of {self.methods} methods have IL bodies")
        if self.field_data:
            found.append(f"{self.field_data} fields carry data (FieldRVA)")
        if self.resources:
            found.append(f"{self.resources} embedded resources")
        return found


class _Metadata:
    def __init__(self, data: bytes) -> None:
        self.data = data
        self._read_pe()
        self._read_streams()
        self._read_tables()

    def _u16(self, offset: int) -> int:
        return struct.unpack_from("<H", self.data, offset)[0]

    def _u32(self, offset: int) -> int:
        return struct.unpack_from("<I", self.data, offset)[0]

    def _read_pe(self) -> None:
        data = self.data
        if len(data) < 0x40 or data[:2] != b"MZ":
            raise AssemblyFormatError("no PE header")
        pe = self._u32(0x3C)
        if data[pe : pe + 4] != b"PE\0\0":
            raise AssemblyFormatError("no PE signature")
        sections = self._u16(pe + 6)
        optional_size = self._u16(pe + 20)
        optional = pe + 24
        magic = self._u16(optional)
        if magic == 0x10B:
            directories = optional + 96
        elif magic == 0x20B:
            directories = optional + 112
        else:
            raise AssemblyFormatError("unknown optional header")
        if self._u32(directories - 4) <= _CLI_HEADER_DIRECTORY:
            raise AssemblyFormatError("not a managed assembly (no CLI header)")
        self._sections = [
            struct.unpack_from("<IIII", data, optional + optional_size + 40 * index + 8)
            for index in range(sections)
        ]
        cli_rva, cli_size = struct.unpack_from("<II", data, directories + 8 * _CLI_HEADER_DIRECTORY)
        if cli_rva == 0 or cli_size < 72:
            raise AssemblyFormatError("not a managed assembly (no CLI header)")
        cli = self.offset(cli_rva)
        metadata_rva = self._u32(cli + 8)
        self.resources_size = self._u32(cli + 28)
        self.metadata = self.offset(metadata_rva)

    def offset(self, rva: int) -> int:
        for virtual_size, virtual_address, raw_size, raw_pointer in self._sections:
            if virtual_address <= rva < virtual_address + max(virtual_size, raw_size):
                offset = raw_pointer + rva - virtual_address
                if offset >= len(self.data):
                    break
                return offset
        raise AssemblyFormatError(f"RVA {rva:#x} is outside every section")

    def _read_streams(self) -> None:
        root = self.metadata
        if self._u32(root) != _METADATA_SIGNATURE:
            raise AssemblyFormatError("bad metadata signature")
        version_length = self._u32(root + 12)
        cursor = root + 16 + version_length + 2
        count = self._u16(cursor)
        cursor += 2
        self.streams: dict[str, tuple[int, int]] = {}
        for _ in range(count):
            offset, size = struct.unpack_from("<II", self.data, cursor)
            name_start = cursor + 8
            end = self.data.index(b"\0", name_start)
            name = self.data[name_start:end].decode("ascii")
            cursor = name_start + ((end - name_start + 4) & ~3)
            self.streams[name] = (root + offset, size)
        if "#~" not in self.streams and "#-" not in self.streams:
            raise AssemblyFormatError("no metadata tables stream")

    def _read_tables(self) -> None:
        start, _ = self.streams.get("#~") or self.streams["#-"]
        heap_sizes = self.data[start + 6]
        valid = struct.unpack_from("<Q", self.data, start + 8)[0]
        if valid >> (max(_SCHEMA) + 1):
            raise AssemblyFormatError("unknown metadata tables")
        cursor = start + 24
        self.rows = [0] * (max(_SCHEMA) + 1)
        for table in range(len(self.rows)):
            if valid >> table & 1:
                self.rows[table] = self._u32(cursor)
                cursor += 4
        if heap_sizes & 0x40:
            cursor += 4
        widths = {"str": 4 if heap_sizes & 1 else 2, "guid": 4 if heap_sizes & 2 else 2,
                  "blob": 4 if heap_sizes & 4 else 2}
        self.layout: dict[int, tuple[int, list[tuple[int, int]]]] = {}
        for table, columns in _SCHEMA.items():
            offsets, column_offset = [], 0
            for column in columns:
                width = self._column_width(column, widths)
                offsets.append((column_offset, width))
                column_offset += width
            self.layout[table] = (cursor, offsets)
            cursor += column_offset * self.rows[table]
        if cursor > len(self.data):
            raise AssemblyFormatError("metadata tables run past the end of the file")
        self.row_size = {table: sum(width for _, width in offsets) for table, (_, offsets) in self.layout.items()}

    def _column_width(self, column: object, widths: dict[str, int]) -> int:
        if isinstance(column, int):
            return column
        if isinstance(column, tuple):
            return 2 if self.rows[column[1]] < 1 << 16 else 4
        if column in widths:
            return widths[column]
        tables = _CODED[column]
        tag_bits = (len(tables) - 1).bit_length()
        largest = max(self.rows[table] if table != _UNUSED else 0 for table in tables)
        return 2 if largest < 1 << (16 - tag_bits) else 4

    def cell(self, table: int, row: int, column: int) -> int:
        start, offsets = self.layout[table]
        column_offset, width = offsets[column]
        offset = start + self.row_size[table] * (row - 1) + column_offset
        return self._u16(offset) if width == 2 else self._u32(offset)

    def coded(self, kind: str, value: int) -> tuple[int, int]:
        tables = _CODED[kind]
        tag_bits = (len(tables) - 1).bit_length()
        return tables[value & ((1 << tag_bits) - 1)], value >> tag_bits

    def string(self, index: int) -> str:
        start, size = self.streams["#Strings"]
        end = self.data.index(b"\0", start + index, start + size)
        return self.data[start + index : end].decode("utf-8", "replace")

    def type_name(self, table: int, row: int) -> tuple[str, str]:
        if table == TYPE_REF:
            return self.string(self.cell(TYPE_REF, row, 2)), self.string(self.cell(TYPE_REF, row, 1))
        if table == TYPE_DEF:
            return self.string(self.cell(TYPE_DEF, row, 2)), self.string(self.cell(TYPE_DEF, row, 1))
        return "", ""

    def method_owner(self, row: int) -> int:
        owner = 0
        for type_row in range(1, self.rows[TYPE_DEF] + 1):
            if self.cell(TYPE_DEF, type_row, 5) > row:
                break
            owner = type_row
        return owner

    def attribute_type(self, row: int) -> tuple[str, str]:
        table, index = self.coded("CustomAttributeType", self.cell(CUSTOM_ATTRIBUTE, row, 1))
        if table == MEMBER_REF:
            parent_table, parent = self.coded("MemberRefParent", self.cell(MEMBER_REF, index, 0))
            return self.type_name(parent_table, parent)
        if table == METHOD_DEF:
            return self.type_name(TYPE_DEF, self.method_owner(index))
        return "", ""


def _is_stub_body(metadata: _Metadata, rva: int) -> bool:
    offset = metadata.offset(rva)
    return metadata.data[offset : offset + len(THROW_NULL_BODY)] == THROW_NULL_BODY


def inspect(data: bytes) -> StubReport:
    """Read an assembly's metadata. Raises AssemblyFormatError when it is not one."""
    try:
        metadata = _Metadata(data)
        if metadata.rows[ASSEMBLY] != 1:
            raise AssemblyFormatError("no assembly manifest")
        methods = metadata.rows[METHOD_DEF]
        bodies = sum(
            1
            for row in range(1, methods + 1)
            if (rva := metadata.cell(METHOD_DEF, row, 0)) and not _is_stub_body(metadata, rva)
        )
        has_attribute = any(
            metadata.coded("HasCustomAttribute", metadata.cell(CUSTOM_ATTRIBUTE, row, 0)) == (ASSEMBLY, 1)
            and metadata.attribute_type(row) == REFERENCE_ASSEMBLY_ATTRIBUTE
            for row in range(1, metadata.rows[CUSTOM_ATTRIBUTE] + 1)
        )
        resources = metadata.rows[MANIFEST_RESOURCE] + (1 if metadata.resources_size else 0)
        return StubReport(methods, bodies, metadata.rows[FIELD_RVA], resources, has_attribute)
    except AssemblyFormatError:
        raise
    except (struct.error, IndexError, KeyError, ValueError) as error:
        raise AssemblyFormatError(f"malformed metadata ({type(error).__name__})") from error


def reference_stub_problems(data: bytes) -> list[str]:
    """Why these bytes are not a reference-only stub; empty when they are one."""
    try:
        return inspect(data).problems
    except AssemblyFormatError as error:
        return [str(error)]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("assemblies", nargs="+", type=Path)
    args = parser.parse_args()
    failed = 0
    for path in args.assemblies:
        try:
            report = inspect(path.read_bytes())
        except AssemblyFormatError as error:
            print(f"FAIL {path}: {error}")
            failed += 1
            continue
        verdict = "FAIL" if report.problems else "OK  "
        detail = "; ".join(report.problems) or "reference-only"
        print(
            f"{verdict} {path}: {report.methods} methods, {report.method_bodies} bodies, "
            f"attribute={'yes' if report.has_reference_attribute else 'no'} ({detail})"
        )
        failed += bool(report.problems)
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
