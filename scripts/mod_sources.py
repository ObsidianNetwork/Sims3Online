"""The mod's source fingerprint: a hash of every source file a mod build compiles, and which of them differ
from the commit, so a crash's build (modBuild, sha@utc, in the controller state files) traces to exact sources,
uncommitted edits included.

    python scripts/mod_sources.py                  # the sources now: hash, files, the ones that differ from HEAD
    python scripts/mod_sources.py --stamp          # one word for BuildStamp.Sources: <hash>, or <hash>+dirty
    python scripts/mod_sources.py --record STAMP [--sources H] [--dll-sha S] [--package-sha S]   # make-mod, at its end
    python scripts/mod_sources.py --show STAMP     # one build's ledger row (a prefix of sha@utc, or its utc)
    python scripts/mod_sources.py --show           # the build installed in each profile, by its package's sha256

The sources are SimsOnlineServerHostMod/*.cs (less Sims3PackageBuilder.cs, which the build leaves out, and the files
the build writes itself: BuildStamp.cs, EmbeddedHandlerAssembly.cs, which embeds the handlers below), Handlers/*.cs
and Presentation/*.cs. The hash is over each file's path and content with line endings made LF, so two checkouts of
one commit agree. It does not cover the engine reference DLLs or the build scripts' compiler flags.

--record appends a row to .omo/run/mod-builds.jsonl (stamp, git sha, built utc, source hash, the dirty files) and,
for a dirty build, keeps its changes in .omo/run/mod-builds/<utc>/: changes.diff (git diff HEAD of the tracked files)
and untracked/ (copies of new source files). The commit plus that folder is the build's exact source.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MOD_DIR = "SimsOnlineServerHostMod"
GENERATED = {"BuildStamp.cs", "EmbeddedHandlerAssembly.cs"}  # the build writes these itself
LEFT_OUT = {"Sims3PackageBuilder.cs"}  # build-server-host-mod.ps1 does not compile it into the mod
SUBDIRS = ("Handlers", "Presentation")  # make-handlers.ps1, build-presentation-module.ps1


def source_files(root: Path = ROOT) -> list[Path]:
    mod = root / MOD_DIR
    files = [p for p in mod.glob("*.cs") if p.name not in GENERATED | LEFT_OUT]
    for sub in SUBDIRS:
        files += list((mod / sub).glob("*.cs"))
    return sorted(files, key=lambda p: p.relative_to(root).as_posix())


def source_hash(files: list[Path], root: Path = ROOT) -> str:
    """12 hex digits over each file's repo path and its content with CRLF made LF."""
    total = hashlib.sha256()
    for path in files:
        content = path.read_bytes().replace(b"\r\n", b"\n")
        total.update(path.relative_to(root).as_posix().encode() + b"\0" + hashlib.sha256(content).digest())
    return total.hexdigest()[:12]


def git(root: Path, *args: str) -> str:
    proc = subprocess.run(["git", *args], cwd=root, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if proc.returncode != 0:
        raise RuntimeError(f"git {' '.join(args[:2])}: {proc.stderr.strip()[:200]}")
    return proc.stdout


def dirty_files(files: list[Path], root: Path = ROOT) -> list[tuple[str, str]]:
    """(status, repo path) of each source that differs from HEAD: M modified, A added, D deleted, ?? untracked."""
    wanted = {p.relative_to(root).as_posix() for p in files}
    out = git(root, "status", "--porcelain=v1", "-z", "--untracked-files=all", "--", MOD_DIR)
    found = []
    entries = iter(out.split("\0"))
    for entry in entries:
        if len(entry) < 4:
            continue
        status, path = entry[:2].strip(), entry[3:]
        if status[:1] in ("R", "C"):
            next(entries, None)  # -z gives a rename's old path as the next entry
        name = path.rsplit("/", 1)[-1]
        in_scope = path in wanted or (status.startswith("D") and path.endswith(".cs") and name not in GENERATED | LEFT_OUT
                                      and (path.count("/") == 1 or path.split("/")[1] in SUBDIRS))
        if in_scope:
            found.append((status, path))
    return sorted(found, key=lambda f: f[1])


def fingerprint(root: Path = ROOT) -> dict:
    files = source_files(root)
    try:
        dirty = dirty_files(files, root)
        sha = git(root, "rev-parse", "--short", "HEAD").strip()
    except (OSError, RuntimeError) as error:
        dirty, sha = None, f"unknown ({error})"
    return {"sources": source_hash(files, root), "files": len(files), "dirty": dirty, "git": sha}


def stamp_word(info: dict) -> str:
    """BuildStamp.Sources: the hash, +dirty when a source differs from the commit (+unknown when git could not say)."""
    return info["sources"] + ("+unknown" if info["dirty"] is None else "+dirty" if info["dirty"] else "")


def ledger_path(root: Path = ROOT) -> Path:
    return root / ".omo" / "run" / "mod-builds.jsonl"


def safe_utc(utc: str) -> str:
    return re.sub(r"[^0-9TZ]", "", utc)


def record(stamp: str, sources: str | None = None, dll: str | None = None, package: str | None = None,
           root: Path = ROOT) -> dict:
    """Append one build's row to the ledger; for a dirty build keep its diff and untracked sources beside it."""
    info = fingerprint(root)
    sha, _, utc = stamp.rpartition("@")
    row = {"stamp": stamp, "gitSha": sha or stamp, "builtUtc": utc, "sources": info["sources"], "files": info["files"],
           "dirty": [path for _, path in info["dirty"]] if info["dirty"] is not None else None,
           "recordedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "dllSha256": dll, "packageSha256": package}
    if sources and sources != "unknown" and sources.split("+")[0] != info["sources"]:
        row["changedDuringBuild"] = sources  # the stamp's hash, taken before the compile: an edit landed meanwhile
    if info["dirty"]:
        keep = ledger_path(root).parent / "mod-builds" / safe_utc(utc or row["recordedUtc"])
        keep.mkdir(parents=True, exist_ok=True)
        tracked = [path for status, path in info["dirty"] if status != "??"]
        if tracked:
            (keep / "changes.diff").write_text(git(root, "diff", "HEAD", "--", *tracked), encoding="utf-8")
        for status, path in info["dirty"]:
            if status == "??":
                target = keep / "untracked" / path
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(root / path, target)
        row["kept"] = keep.relative_to(root).as_posix()
    ledger = ledger_path(root)
    ledger.parent.mkdir(parents=True, exist_ok=True)
    with ledger.open("a", encoding="utf-8") as f:
        f.write(json.dumps(row) + "\n")
    return row


def read_path() -> Path:
    """The ledger a reader wants: the live worktree's when SIMSONLINE_RUN_DIR names its .omo/run, else this one's (a
    build records into its own worktree's ledger: record() and --record never read this)."""
    run = os.environ.get("SIMSONLINE_RUN_DIR")
    return Path(run) / "mod-builds.jsonl" if run else ledger_path()


def ledger(root: Path | None = None, path: Path | None = None) -> dict[str, dict]:
    """Every recorded build by its stamp (sha@utc); {} when there is no ledger."""
    path = path or (ledger_path(root) if root else read_path())
    try:
        rows = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]
    except (OSError, ValueError):
        return {}
    return {row["stamp"]: row for row in rows if row.get("stamp")}


def find(text: str, builds: dict[str, dict]) -> dict | None:
    """The newest build whose stamp starts with text, or whose built utc is text."""
    found = [row for stamp, row in builds.items() if stamp.startswith(text) or row.get("builtUtc") == text]
    return max(found, key=lambda r: r.get("builtUtc", "")) if found else None


PACKAGE = "SimsOnlineServerHostMod.package"


def installed_packages() -> list[tuple[str, Path]]:
    """Where quick_loop's install_mod puts the package: the server profile, its OneDrive copy and each client box."""
    import quick_loop as ql

    return ([("server", ql.REAL / "Mods" / "Packages" / PACKAGE), ("onedrive", ql.ONEDRIVE_MOD)]
            + [(name, ql.box(name) / "Mods" / "Packages" / PACKAGE) for name in ql.BOXES])


def installed(builds: dict[str, dict], places: list[tuple[str, Path]]) -> list[dict]:
    """Each place's package: its sha256 and file time, and the ledger row with that packageSha256 (None for a build
    the ledger does not have). A place with no package has sha None. Reads the files only."""
    by_sha = {}
    for row in sorted(builds.values(), key=lambda r: r.get("builtUtc", "")):
        if row.get("packageSha256"):
            by_sha[row["packageSha256"]] = row  # a rebuild of the same bytes: the newest row
    out = []
    for name, path in places:
        try:
            digest = hashlib.sha256(path.read_bytes()).hexdigest()
            mtime = path.stat().st_mtime
        except OSError:
            digest = mtime = None
        out.append({"place": name, "path": path, "sha": digest, "mtime": mtime, "row": by_sha.get(digest) if digest else None})
    return out


def show_installed(places: list[tuple[str, Path]] | None = None) -> int:
    """--show with no stamp: the build in each profile, by its package's sha256 against the ledger. Exit 1 when no
    profile has the package, or one holds a build the ledger does not have."""
    found = installed(ledger(), places if places is not None else installed_packages())
    print(f"installed {PACKAGE}, by sha256 in {read_path()}:")
    for f in found:
        row = f["row"]
        when = time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(f["mtime"])) if f["mtime"] else ""
        if f["sha"] is None:
            text = f"no package ({f['path']})"
        elif row is None:
            text = f"not in the ledger: sha256 {f['sha'][:16]}, file {when}"
        else:
            dirty = row.get("dirty")
            text = (f"{row['stamp']}  sources {row.get('sources')}, "
                    + ("dirty unknown" if dirty is None else f"{len(dirty)} dirty, kept in {row.get('kept')}" if dirty
                       else "clean") + f"; file {when}")
        print(f"  {f['place']:<11} {text}")
    stamps = {f["row"]["stamp"] for f in found if f["row"]}
    if len({f["sha"] for f in found if f["sha"]}) > 1:
        print("  the profiles hold different builds")
    for stamp in sorted(stamps):
        print(json.dumps(next(f["row"] for f in found if f["row"] and f["row"]["stamp"] == stamp), indent=1))
    present = [f for f in found if f["sha"]]
    return 0 if present and all(f["row"] for f in present) else 1


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0], formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--stamp", action="store_true", help="print the word for BuildStamp.Sources")
    p.add_argument("--record", metavar="STAMP", help="append this build (sha@utc) to .omo/run/mod-builds.jsonl")
    p.add_argument("--sources", help="with --record: the hash the stamp was written with")
    p.add_argument("--dll-sha", help="with --record: the built dll's sha256")
    p.add_argument("--package-sha", help="with --record: the package's sha256")
    p.add_argument("--show", metavar="STAMP", nargs="?", const="", default=None,
                   help="one recorded build: a prefix of sha@utc, or its utc; with no STAMP, the build installed in each "
                        "profile (its package's sha256 looked up in the ledger)")
    a = p.parse_args(argv)
    if a.stamp:
        try:
            print(stamp_word(fingerprint()))
        except OSError:
            print("unknown")
        return 0
    if a.record:
        row = record(a.record, a.sources, a.dll_sha, a.package_sha)
        print(f"mod sources {row['sources']}" + (f", {len(row['dirty'])} differ from {row['gitSha']}: kept in {row['kept']}"
                                                 if row.get("dirty") else f", clean at {row['gitSha']}")
              + (f" (changed during the build: the stamp says {row['changedDuringBuild']})" if row.get("changedDuringBuild") else ""))
        return 0
    if a.show == "":
        return show_installed()
    if a.show:
        row = find(a.show, ledger())
        if not row:
            print(f"no recorded build matches {a.show!r} ({read_path()})")
            return 1
        print(json.dumps(row, indent=1))
        return 0
    info = fingerprint()
    print(f"mod sources {info['sources']} over {info['files']} files at {info['git']}"
          + ("" if info["dirty"] is None else f"; {len(info['dirty'])} differ from HEAD"))
    for status, path in info["dirty"] or []:
        print(f"  {status:>2} {path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
