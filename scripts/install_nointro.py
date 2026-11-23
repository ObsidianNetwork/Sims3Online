"""skip TS3's intro movies on every dev-stack profile.

TS3 plays its intro movies (VP6 video resources, type 0xB1CC1AF6, in
GameData/Shared/Packages/FullBuild1.package) before the main menu, and nothing can click them away
on a headless stack. Measured on a server-only boot (2026-10-11, `internal documentation`):
launch to main menu 82 s with the movies (warm cache), 31 s without them.

This writes `zz-SimsOnlineNoIntro.package`: one package that overrides each intro movie with a
zero-frame 256x256 VP6 header. It's generated here, so no third-party file is needed. It goes into the
Mods/Packages folder of every TS3 profile the stack boots:

    the real Documents profile   (server; Windows and Linux)
    each Sandboxie box profile   C:/Sandbox/<user>/<box>/drive/C/Sims/<prefix>/drive_c/users/<u>/Documents/...
    each Wine client prefix      ~/.sims3-client*/drive_c/users/<u>/Documents/...

Idempotent (skips identical files). SIMSONLINE_KEEP_INTROS=1 skips the install.

    python scripts/install_nointro.py            # install everywhere
    python scripts/install_nointro.py --list     # show the profiles it would touch
    python scripts/install_nointro.py --remove   # take it out again
    python scripts/install_nointro.py --write out.package   # just write the package
"""

from __future__ import annotations

import argparse
import glob
import hashlib
import os
import struct
import sys

PACKAGE_NAME = "zz-SimsOnlineNoIntro.package"
VIDEO_TYPE = 0xB1CC1AF6

# The intro movie instances in FullBuild1.package (EA logo, Maxis logo, the Sims 3 logo and its
# expansion variants, and the long opening movie).
INTRO_INSTANCES = (
    0x799AE80054CEE156, 0xB50331A5F69E749A, 0x560FB403C71622EE, 0x4E5D8B03C2FF9DFC,
    0x5D8E65E8E531012B, 0x5D9260E8E534E1D0, 0x5D956AE8E53728E9, 0x5D9865E8E5395596,
    0x5DBE74E8E55A3EF0, 0x5D655FE8E50DF26B, 0x5D706AE8E5180C2E, 0xA07ACCBD24B4DB32,
    0xAD46C72AB29ACF48,
)

# EA movie header chunk: "MVhd", chunk size 32, codec "vp60", 256x256, 0 frames, largest frame 0,
# rate 15000/1000.
EMPTY_MOVIE = b"MVhd" + struct.pack("<I", 32) + b"06PV" + struct.pack("<HHIIII", 256, 256, 0, 0, 15000, 1000)

HEADER_SIZE = 96
INDEX_OFFSET = 512


def build_package() -> bytes:
    """A DBPF 2.0 package with one EMPTY_MOVIE per intro instance (type and group constant in the index)."""
    count = len(INTRO_INSTANCES)
    data_end = HEADER_SIZE + count * len(EMPTY_MOVIE)
    if data_end > INDEX_OFFSET:
        raise ValueError("resources overrun the index offset")
    index = bytearray(struct.pack("<III", 0x3, VIDEO_TYPE, 0))
    for i, instance in enumerate(INTRO_INSTANCES):
        offset = HEADER_SIZE + i * len(EMPTY_MOVIE)
        size = len(EMPTY_MOVIE)
        index += struct.pack("<IIIIIHH", instance >> 32, instance & 0xFFFFFFFF, offset, size | 0x80000000, size, 0, 1)
    header = bytearray(HEADER_SIZE)
    header[0:4] = b"DBPF"
    struct.pack_into("<II", header, 4, 2, 0)
    struct.pack_into("<I", header, 36, count)
    struct.pack_into("<I", header, 44, len(index))
    struct.pack_into("<I", header, 60, 3)
    struct.pack_into("<I", header, 64, INDEX_OFFSET)
    body = bytes(header) + EMPTY_MOVIE * count
    return body + bytes(INDEX_OFFSET - len(body)) + bytes(index)


def profile_package_dirs() -> list[str]:
    """Every existing TS3 Mods/Packages folder of a profile the dev stack boots."""
    home = os.path.expanduser("~")
    candidates = [os.path.join(home, "Documents", "Electronic Arts", "The Sims 3", "Mods", "Packages")]
    tail = os.path.join("Documents", "Electronic Arts", "The Sims 3", "Mods", "Packages")
    if os.name == "nt":
        # The box prefixes are dot folders (.sims3-client), which "*" doesn't match.
        for prefix in ("*", ".*"):
            candidates += glob.glob(os.path.join("C:\\Sandbox", "*", "*", "drive", "C", "Sims", prefix, "drive_c", "users", "*", tail))
    candidates += glob.glob(os.path.join(home, ".sims3-client*", "drive_c", "users", "*", tail))
    seen: list[str] = []
    for path in candidates:
        real = os.path.realpath(path)
        if os.path.isdir(path) and real not in [os.path.realpath(p) for p in seen]:
            seen.append(path)
    return seen


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--list", action="store_true", help="print the profile folders and exit")
    parser.add_argument("--remove", action="store_true", help="remove the package from every profile")
    parser.add_argument("--write", metavar="PATH", help="only write the package to PATH")
    args = parser.parse_args(argv)

    package = build_package()
    if args.write:
        with open(args.write, "wb") as handle:
            handle.write(package)
        print(f"wrote {args.write} sha256={sha256(package)}")
        return 0
    if os.environ.get("SIMSONLINE_KEEP_INTROS") == "1" and not (args.list or args.remove):
        print("nointro: SIMSONLINE_KEEP_INTROS=1, skipped")
        return 0

    dirs = profile_package_dirs()
    if not dirs:
        print("nointro: no TS3 profile Mods/Packages folder found", file=sys.stderr)
        return 1
    for folder in dirs:
        target = os.path.join(folder, PACKAGE_NAME)
        if args.list:
            print(f"{'installed' if os.path.exists(target) else 'missing  '} {folder}")
        elif args.remove:
            if os.path.exists(target):
                os.remove(target)
                print(f"nointro: removed {target}")
        else:
            if os.path.exists(target):
                with open(target, "rb") as handle:
                    if handle.read() == package:
                        continue
            with open(target, "wb") as handle:
                handle.write(package)
            print(f"nointro: installed {target}")
    if not (args.list or args.remove):
        print(f"nointro: {len(dirs)} profile(s) current, sha256={sha256(package)[:16]}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
