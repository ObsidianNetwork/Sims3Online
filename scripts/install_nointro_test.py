"""Self-test for scripts/install_nointro.py (no game needed).

    python scripts/install_nointro_test.py
"""

from __future__ import annotations

import importlib.util
import struct
from pathlib import Path

spec = importlib.util.spec_from_file_location("install_nointro", Path(__file__).resolve().parent / "install_nointro.py")
assert spec is not None and spec.loader is not None
ni = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ni)


def read_index(data: bytes) -> list[tuple[int, int, int, int, int, int]]:
    """(type, group, instance, offset, size, memsize) per entry, the way TS3 reads a DBPF 2.0 index."""
    count = struct.unpack_from("<I", data, 36)[0]
    size = struct.unpack_from("<I", data, 44)[0]
    offset = struct.unpack_from("<I", data, 64)[0]
    assert offset + size == len(data), (offset, size, len(data))
    flags, pos = struct.unpack_from("<I", data, offset)[0], offset + 4
    constant = []
    for bit in range(3):
        if flags & (1 << bit):
            constant.append(struct.unpack_from("<I", data, pos)[0])
            pos += 4
        else:
            constant.append(None)
    entries = []
    for _ in range(count):
        values = []
        for bit in range(3):
            if constant[bit] is None:
                values.append(struct.unpack_from("<I", data, pos)[0])
                pos += 4
            else:
                values.append(constant[bit])
        lo, res_offset, res_size, memsize = struct.unpack_from("<IIII", data, pos)
        pos += 20
        entries.append((values[0], values[1], (values[2] << 32) | lo, res_offset, res_size & 0x7FFFFFFF, memsize))
    return entries


def test_package_overrides_every_intro_with_an_empty_movie() -> None:
    data = ni.build_package()
    assert data[:4] == b"DBPF" and struct.unpack_from("<II", data, 4) == (2, 0)
    entries = read_index(data)
    assert sorted(e[2] for e in entries) == sorted(ni.INTRO_INSTANCES)
    for type_id, group, _, offset, size, memsize in entries:
        assert type_id == ni.VIDEO_TYPE and group == 0 and size == memsize == 32
        chunk = data[offset:offset + size]
        assert chunk[:4] == b"MVhd" and chunk[8:12] == b"06PV"
        frames = struct.unpack_from("<I", chunk, 16)[0]
        assert frames == 0, frames


def test_package_is_deterministic() -> None:
    assert ni.build_package() == ni.build_package()
    assert len(ni.build_package()) == 836


if __name__ == "__main__":
    for name, fn in list(globals().items()):
        if name.startswith("test_") and callable(fn):
            fn()
            print(f"PASS {name}")
