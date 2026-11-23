"""The tester kit: what a friend who owns The Sims 3 installs to join a game hosted on another PC.

    python scripts/tester_kit.py build [--no-mod-build] [--no-publish] [--no-fork | --fork-builder] [--simlink PATH] [--loader PATH]
    python scripts/tester_kit.py verify [KIT]          # a kit folder (default: the newest) against its manifest
    python scripts/tester_kit.py check [KIT]           # verify, then no EA-derived file (the --fork-builder proof)
    python scripts/tester_kit.py install-box --box Client5Box [--kit KIT] [--template Client4Box]
    python scripts/tester_kit.py list                  # the kits built here, newest first

`build` assembles `.omo/run/tester-kit/<stamp>/` and `<stamp>.zip` from the current build: the host mod
(`make-mod.ps1 -SkipInstall`, then `check_mod_runtime.py`), the client controller (`dotnet publish SimsOnline.Client
-c Release -r win-x64 --self-contained true`), SimLink.asi and the ASI loader from the host's Game\\Bin (SimLink from
`--simlink` or the build output when asked), the installed fork attempt's package from the server profile, the
no-intro package (`install_nointro.py --write`), `Resource.cfg`, and the friend-side scripts in `tools/tester_kit/`.
`manifest.json` lists every file's sha256, size and provenance (ours, generated, third-party, ea-derived) with the feed
commit, the mod build, the fork attempt, the SimLink version and the build time. By default the kit holds an
EA-derived file (the fork package); `--fork-builder` ships the builder instead (scripts/fork_builder.py:
build-fork.ps1 makes the package on the friend's PC from their own game), and the build then checks that no file is
EA-derived (`check` repeats it). Third-party files: the loader and, with the builder, the .NET runtime, ILSpy and
Roslyn. A kit is built locally and never committed; internal documentation says what each file is.

`verify` re-hashes a kit folder: missing, changed and extra files. `install-box` proves a kit on this machine: it
installs it into a Sandboxie box the way a friend's install.ps1 would install it into their profile (Windows
PowerShell 5.1, the kit's own install.ps1), with the box's registry taken from a template box so TS3 in the box keeps
its profile under the box (as Client2Box to Client4Box do). The box itself must exist in Sandboxie.ini: making one
is the user's (administrator) step. Then join from the box with the kit's join.ps1 (the command is printed).
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import time
import zipfile
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TEMPLATES = ROOT / "tools" / "tester_kit"
OUT = ROOT / ".omo" / "run" / "tester-kit"
INSTALLS = ROOT / ".omo" / "run" / "tester-kit-installs"
MANIFEST = "manifest.json"
KIT_SCRIPTS = ["README.md", "install.ps1", "join.ps1", "collect-logs.ps1", "uninstall.ps1",
               "install.cmd", "join.cmd", "collect-logs.cmd", "uninstall.cmd"]
BUILDER_SCRIPTS = ["build-fork.ps1", "build-fork.cmd"]  # with --fork-builder: the friend builds the fork
MOD_PACKAGE = "SimsOnlineServerHostMod.package"
FORK_PACKAGE = "SimsOnlineGameplay02Cold.package"
NOINTRO_PACKAGE = "zz-SimsOnlineNoIntro.package"
# The host profile's Resource.cfg: load Mods\Packages to three folder levels.
RESOURCE_CFG = ("Priority 500\r\nPackedFile Packages/*.package\r\nPackedFile Packages/*/*.package\r\n"
                "PackedFile Packages/*/*/*.package\r\n")
TS3W_SHA256 = "10b2423e2368e161774462b0ef1960231ad0af298d564a7bb6a6e78c5e880609"  # 1.67 Steam (scripts/offline analyzer)
SANDBOXIE_INI = Path(os.environ.get("SystemRoot", r"C:\Windows")) / "Sandboxie.ini"
SANDBOXIE_START = r"C:\Program Files\Sandboxie-Plus\Start.exe"
WINDOWS_POWERSHELL = Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32/WindowsPowerShell/v1.0/powershell.exe"


def say(text: str) -> None:
    print(f"[tester_kit] {text}", flush=True)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


@dataclass
class Sources:
    """Where each piece of a kit comes from. fork, loader and nointro may be None (left out, said in the manifest)."""
    mod: Path
    client_dir: Path
    simlink: Path
    loader: Path | None
    fork: Path | None
    nointro: Path | None
    templates: Path = TEMPLATES
    builder: Path | None = None  # scripts/fork_builder.py's payload: the friend builds the fork (--fork-builder)


def layout(src: Sources) -> list[tuple[Path, str]]:
    """(source file, kit path) for every file of the kit but the manifest and Resource.cfg."""
    rows = [(src.mod, f"mods/Packages/{MOD_PACKAGE}"), (src.simlink, "game-bin/SimLink.asi")]
    if src.fork:
        rows.append((src.fork, f"mods/Packages/{FORK_PACKAGE}"))
    if src.nointro:
        rows.append((src.nointro, f"mods/Packages/{NOINTRO_PACKAGE}"))
    if src.loader:
        rows.append((src.loader, "game-bin/wininet.dll"))
    rows += [(src.templates / name, name) for name in KIT_SCRIPTS]
    if src.builder:
        rows += [(src.templates / name, name) for name in BUILDER_SCRIPTS]
        rows += [(path, "fork-builder/" + path.relative_to(src.builder).as_posix())
                 for path in sorted(src.builder.rglob("*")) if path.is_file()]
    rows += [(path, "client/" + path.relative_to(src.client_dir).as_posix())
             for path in sorted(src.client_dir.rglob("*")) if path.is_file()]
    return rows


# Where each kit file comes from, for the manifest's "provenance" (internal documentation "What is EA-derived").
THIRD_PARTY = {
    "game-bin/wininet.dll": "Ultimate ASI Loader (ThirteenAG, MIT)",
    "fork-builder/runtime/": ".NET runtime (Microsoft, MIT)",
    "fork-builder/ilspy/": "ilspycmd 8.2.0.7535 (ICSharpCode, MIT) and its dependencies (MIT/Apache-2.0)",
    "fork-builder/roslyn/": "Roslyn C# compiler of the .NET 8 SDK (Microsoft, MIT)",
    "client/": "SimsOnline.Client (ours) with the .NET runtime it is published with (Microsoft, MIT)",
}
GENERATED_KIT_FILES = {"mods/Resource.cfg", f"mods/Packages/{NOINTRO_PACKAGE}", "fork-builder/fork-patch.json",
             "fork-builder/fork-expect.json"}


def provenance(rel: str) -> str:
    """ours, generated, third-party: <what>, or ea-derived (the fork package only)."""
    if rel == f"mods/Packages/{FORK_PACKAGE}":
        return "ea-derived"
    if rel in GENERATED_KIT_FILES:
        return "generated"
    for prefix, what in THIRD_PARTY.items():
        if rel == prefix or (prefix.endswith("/") and rel.startswith(prefix)):
            return "third-party: " + what
    return "ours"


# The game's script assemblies by name. System, System.Xml and mscorlib share their names with .NET's own, so those
# three (and every one of the ten) are also caught by hash (ea_check's game_hashes).
GAME_ASSEMBLY_NAMES = {"Sims3StoreObjects", "Sims3GameplayObjects", "Sims3GameplaySystems", "UI", "SimIFace",
                       "ScriptCore", "Sims3Metadata"}


def identify(kit: Path) -> dict[str, list[str]]:
    """Kit path -> the assembly names in it: a managed PE's own, a package's S3SA resources' (ForkTool identify)."""
    fork_builder = None
    tool = fork_builder.forktool(OUT / "_forktool")
    done = subprocess.run(["dotnet", str(tool), "identify", str(kit)], capture_output=True, text=True, timeout=600, check=False)
    if done.returncode != 0:
        raise SystemExit("ForkTool identify failed:\n" + (done.stdout + done.stderr)[-2000:])
    names: dict[str, list[str]] = {}
    for line in done.stdout.splitlines():
        rel, _, name = line.partition("\t")
        names.setdefault(rel, []).append(name)
    return names


def ea_check(kit: Path, game_hashes: set[str]) -> list[str]:
    """Why a kit is not free of EA-derived files ([] when it is): any file the manifest calls ea-derived, any file or
    package resource that is one of the game's script assemblies (by name or by hash), any package but the mod and
    the no-intro one, and the repository's proprietary-input guard on every text file (game-package, private-path,
    credential, bulk decompiler output)."""
    sys.path.insert(0, str(ROOT / "scripts"))
    import proprietary_input_guard as _pguard
    problems = []
    manifest = json.loads((kit / MANIFEST).read_text(encoding="utf-8"))
    problems += [f"manifest: {row['path']} is ea-derived" for row in manifest["files"] if row.get("provenance") == "ea-derived"]
    problems += [f"manifest: {row['path']} has no provenance" for row in manifest["files"] if not row.get("provenance")]
    allowed_packages = {f"mods/Packages/{MOD_PACKAGE}", f"mods/Packages/{NOINTRO_PACKAGE}"}
    names = identify(kit)
    for path in sorted(kit.rglob("*")):
        if not path.is_file():
            continue
        rel = path.relative_to(kit).as_posix()
        data = path.read_bytes()
        if sha256_bytes(data) in game_hashes:
            problems.append(f"{rel} is one of the game's files (sha256)")
        problems += [f"{rel} is or carries the game's assembly {n}" for n in names.get(rel, []) if n in GAME_ASSEMBLY_NAMES]
        if (data[:4] == b"DBPF" or rel.endswith(".package")) and rel not in allowed_packages:
            problems.append(f"{rel}: a package the kit does not ship")
        if path.suffix.lower() in {".cs", ".json", ".md", ".ps1", ".cmd", ".txt", ".cfg"}:
            problems += [f"guard {v.rule}: {rel}: {v.message}" for v in _pguard.scan_file(path, rel)]
    return problems


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def game_file_hashes(bin_dir: Path, assemblies: Path | None = None) -> set[str]:
    """sha256 of the game's script packages and of its ten script assemblies (the extracted copies, when given)."""
    hashes = {sha256(bin_dir / name) for name in ("gameplay.package", "scripts.package", "simcore.package")
              if (bin_dir / name).is_file()}
    if assemblies and assemblies.is_dir():
        hashes |= {sha256(p) for p in assemblies.glob("*.dll")}
    return hashes


def file_rows(kit: Path) -> list[dict]:
    """Every file of a kit folder but the manifest: path (posix, relative), size, sha256."""
    return [{"path": path.relative_to(kit).as_posix(), "size": path.stat().st_size, "sha256": sha256(path)}
            for path in sorted(kit.rglob("*")) if path.is_file() and path.relative_to(kit).as_posix() != MANIFEST]


def assemble(src: Sources, kit: Path, meta: dict) -> dict:
    """Copy the kit's files into kit (a new folder), write Resource.cfg and the manifest; returns the manifest."""
    missing = [str(source) for source, _ in layout(src) if not source.is_file()]
    if missing:
        raise SystemExit("missing kit sources: " + "; ".join(missing[:6]))
    kit.mkdir(parents=True)
    for source, rel in layout(src):
        target = kit / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
    (kit / "mods" / "Resource.cfg").write_bytes(RESOURCE_CFG.encode("ascii"))
    rows = [{**row, "provenance": provenance(row["path"])} for row in file_rows(kit)]
    manifest = {"kit": kit.name, **meta, "ts3w_sha256": TS3W_SHA256,
                "left_out": [name for name, source in (("fork", src.fork), ("loader", src.loader),
                                                       ("nointro", src.nointro)) if source is None],
                "total_bytes": sum(row["size"] for row in rows), "files": rows}
    (kit / MANIFEST).write_text(json.dumps(manifest, indent=1) + "\n", encoding="utf-8")
    return manifest


def verify(kit: Path) -> list[str]:
    """What differs between a kit folder and its manifest: missing, changed and extra files ([] when whole)."""
    try:
        manifest = json.loads((kit / MANIFEST).read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        return [f"no readable {MANIFEST} in {kit}: {error}"]
    listed = {row["path"]: row for row in manifest.get("files", [])}
    present = {row["path"]: row for row in file_rows(kit)}
    problems = [f"missing {path}" for path in listed if path not in present]
    problems += [f"changed {path}" for path, row in listed.items()
                 if path in present and (present[path]["size"], present[path]["sha256"]) != (row["size"], row["sha256"])]
    problems += [f"extra {path}" for path in present if path not in listed]
    return problems


def make_zip(kit: Path) -> Path:
    archive = kit.with_suffix(".zip")
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
        for path in sorted(kit.rglob("*")):
            if path.is_file():
                z.write(path, f"{kit.name}/{path.relative_to(kit).as_posix()}")
    return archive


def kits(out: Path = OUT) -> list[Path]:
    """The kit folders built here, newest first."""
    return sorted((p for p in out.glob("*") if p.is_dir() and (p / MANIFEST).is_file()), key=lambda p: p.name, reverse=True)


def sandboxie_has_box(ini_text: str, box: str) -> bool:
    """True when Sandboxie.ini has an enabled [box] section."""
    section, enabled = None, {}
    for raw in ini_text.splitlines():
        line = raw.strip().lstrip("\ufeff")
        if line.startswith("[") and line.endswith("]"):
            section = line[1:-1]
            enabled.setdefault(section, False)
        elif section and line.lower() == "enabled=y":
            enabled[section] = True
    return enabled.get(box, False)


def box_profile(template_profile: Path, template: str, box: str) -> Path:
    """The new box's Sims 3 profile: the template's path with the box name swapped (its registry, copied from the
    template, points TS3 at the same path inside the box)."""
    parts = list(template_profile.parts)
    if template not in parts:
        raise SystemExit(f"{template_profile} is not under a {template} folder")
    parts[parts.index(template)] = box
    return Path(*parts)


def box_missing_text(box: str, template: str) -> str:
    return (f"Sandboxie has no enabled box {box} ({SANDBOXIE_INI}). Making one is the user's step (administrator): "
            f"Sandboxie-Plus, right-click {template}, Duplicate Box Config, name it {box}; or copy the [{template}] "
            f"block of {SANDBOXIE_INI} as [{box}], add {box} to BoxGrouping, then reload the ini (Start.exe /reload). "
            "Copy no profile into it: install-box fills it.")


# ---- build --------------------------------------------------------------------------------------------------------

def run(cmd: list[str], timeout: int = 1800) -> tuple[int, str]:
    out = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True, timeout=timeout, check=False)
    return out.returncode, (out.stdout or "") + (out.stderr or "")


def git(*args: str) -> str:
    return run(["git", *args], timeout=60)[1].strip()


GENERATED = {"SimsOnlineServerHostMod/BuildStamp.cs", "SimsOnlineServerHostMod/EmbeddedHandlerAssembly.cs"}


def feed_dirty(porcelain: str) -> bool:
    """A tracked file differs from the commit, beyond the two files every make-mod rewrites."""
    return any(line[3:].strip() not in GENERATED for line in porcelain.splitlines() if line.strip())


def game_bin() -> Path:
    import quick_loop  # scripts/quick_loop.py: the host's TS3 install
    if not quick_loop.TS3:
        raise SystemExit("no TS3W.exe found on this machine (SIMSONLINE_TS3_PATH)")
    return Path(quick_loop.TS3).parent


UNREADABLE = "unreadable"  # the manifest's attempt when the fork receipt could not be read ("none"/null means no fork installed)


def fork_attempt(verb: str) -> str | None:
    """The host's installed fork attempt, read once per build: None when no fork is installed, UNREADABLE (one stderr
    warning naming the receipt) when the receipt cannot be read."""
    import quick_loop
    try:
        return quick_loop.installed_attempt()
    except quick_loop.ForkReceiptError as error:
        print(f"{verb}: fork receipt unreadable, recording it as such in the manifest ({error}); delete or repair that receipt",
              file=sys.stderr, flush=True)
        return UNREADABLE


def host_fork(package: Path, attempt: str | None) -> dict | None:
    """The fork attempt installed on the host and its package's sha256, kept in every manifest (--no-fork too: a hash
    holds none of the game's code), so install.ps1 can name a fork package it finds on the tester's PC."""
    if not package.is_file():
        return None
    return {"attempt": attempt, "package_sha256": sha256(package)}


def simlink_version(path: Path) -> str:
    import sidecar  # scripts/sidecar.py
    return sidecar.describe(path)


def build(args: argparse.Namespace) -> int:
    import quick_loop
    import stack_claim

    pointer = quick_loop.slot_pointer()
    if pointer and not args.no_mod_build:
        raise SystemExit(f"build slot {pointer.get('slot')} is installed (build_slot.py): a mod build would not be what "
                         "the stack runs; build_slot.py release first, or --no-mod-build")
    if (not args.no_mod_build or not args.no_publish) and stack_claim.refuse_build():
        return 2
    started = time.time()
    stamp = time.strftime("%Y%m%d-%H%M%S")
    work = OUT / f"_work-{stamp}"
    work.mkdir(parents=True)

    mod = ROOT / "SimsOnlineServerHostMod/bin/Release" / MOD_PACKAGE
    if not args.no_mod_build:
        say("mod: make-mod.ps1 -SkipInstall")
        code, text = run(["pwsh", "-NoProfile", "-File", "scripts/make-mod.ps1", "-SkipInstall"])
        if "make-mod OK" not in text:
            raise SystemExit("make-mod failed:\n" + text[-2000:])
        code, text = run([sys.executable, "scripts/check_mod_runtime.py"])
        if code != 0:
            raise SystemExit("mod runtime check failed (scripts/check_mod_runtime.py):\n" + text[-2000:])
    if not mod.is_file():
        raise SystemExit(f"no {mod}: build the mod (drop --no-mod-build)")

    client_dir = OUT / "_publish"
    if not args.no_publish:
        say("client: dotnet publish SimsOnline.Client -c Release -r win-x64 --self-contained true")
        if client_dir.exists():
            shutil.rmtree(client_dir)
        code, text = run(["dotnet", "publish", "SimsOnline.Client/SimsOnline.Client.csproj", "-c", "Release", "-r",
                          "win-x64", "--self-contained", "true", "-o", str(client_dir), "--nologo", "-v", "q"])
        if code != 0:
            raise SystemExit("dotnet publish failed:\n" + text[-2000:])
    if not (client_dir / "SimsOnline.Client.exe").is_file():
        raise SystemExit(f"no {client_dir / 'SimsOnline.Client.exe'}: publish the client (drop --no-publish)")

    bin_dir = game_bin()
    simlink = Path(args.simlink) if args.simlink else bin_dir / "SimLink.asi"
    loader = None if args.no_loader else (Path(args.loader) if args.loader else bin_dir / "wininet.dll")
    fork = None if args.no_fork or args.fork_builder else quick_loop.REAL / "Mods/Packages" / FORK_PACKAGE
    host_attempt = fork_attempt("tester-kit")  # once per build: --no-fork keeps host_fork too
    attempt = None if args.no_fork or args.fork_builder else host_attempt
    builder, builder_record = None, None
    if args.fork_builder:
        fork_builder = None
        builder = work / "fork-builder"
        say("fork builder: tools, hooks, and the patch from a fresh decompile of this host's game (a few minutes)")
        if fork_builder is None:
            raise SystemExit('--fork-builder requires fork_builder tooling')
        builder_record = fork_builder.payload(builder, bin_dir, OUT / "_fork-builder-work")
    nointro = work / NOINTRO_PACKAGE
    code, text = run([sys.executable, "scripts/install_nointro.py", "--write", str(nointro)])
    if code != 0 or not nointro.is_file():
        raise SystemExit("install_nointro.py --write failed:\n" + text[-1000:])

    mod_build = None
    try:
        mod_build = json.loads((ROOT / ".omo/run/mod-build.json").read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        pass
    meta = {"built_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "feed_commit": git("rev-parse", "HEAD"), "feed_branch": git("rev-parse", "--abbrev-ref", "HEAD"),
            "feed_dirty": feed_dirty(run(["git", "status", "--porcelain", "--untracked-files=no"], timeout=60)[1]),
            "mod_build": mod_build, "fork_attempt": attempt, "simlink": simlink_version(simlink),
            "simlink_source": "--simlink" if args.simlink else "host Game\\Bin",
            "loader": "Ultimate ASI Loader (wininet.dll), third-party" if loader else None,
            "client_publish": "dotnet publish -c Release -r win-x64 --self-contained true",
            "fork": "builder (build-fork.ps1 on the friend's PC)" if builder else ("package" if fork else None),
            "fork_builder": builder_record,
            "distribution": "local proof only: holds the EA-derived fork package" if fork else "may be sent to a tester"}
    meta["host_fork"] = host_fork(quick_loop.REAL / "Mods/Packages" / FORK_PACKAGE, host_attempt)
    kit = OUT / f"SimsOnline-tester-kit-{stamp}"
    manifest = assemble(Sources(mod=mod, client_dir=client_dir, simlink=simlink, loader=loader, fork=fork,
                                nointro=nointro, builder=builder), kit, meta)
    shutil.rmtree(work, ignore_errors=True)
    problems = verify(kit)
    if problems:
        raise SystemExit("the new kit does not verify: " + "; ".join(problems[:6]))
    if builder:
        problems = ea_check(kit, game_file_hashes(bin_dir, OUT / "_fork-builder-work/assemblies"))
        if problems:
            raise SystemExit("the fork-builder kit is not free of EA-derived files: " + "; ".join(problems[:8]))
        say("EA check: no EA-derived file (manifest provenance, assembly identities, the proprietary-input guard)")
    archive = make_zip(kit)
    say(f"kit {kit} ({len(manifest['files'])} files, {manifest['total_bytes'] / 1e6:.1f} MB), verified")
    say(f"zip {archive} ({archive.stat().st_size / 1e6:.1f} MB) in {time.time() - started:.0f}s")
    say(f"feed {meta['feed_commit'][:10]}{' (dirty)' if meta['feed_dirty'] else ''}, "
        f"fork {'builder' if builder else attempt or '-'}, SimLink {meta['simlink']}; EA-derived: "
        f"{FORK_PACKAGE if fork else 'none'}; third-party: {'wininet.dll ' if loader else ''}"
        f"{'.NET runtime, ILSpy, Roslyn (fork-builder/)' if builder else ''}")
    if fork:
        say("LOCAL PROOF ONLY: this kit holds the EA-derived fork package; never send it to anyone (internal documentation). "
            "A kit for a friend: build --fork-builder (or --no-fork)")
    return 0


# ---- install-box --------------------------------------------------------------------------------------------------

def install_box(args: argparse.Namespace) -> int:
    import quick_loop

    kit = Path(args.kit) if args.kit else next(iter(kits()), None)
    if kit is None:
        raise SystemExit("no kit built here: tester_kit.py build first")
    problems = verify(kit)
    if problems:
        raise SystemExit(f"{kit} does not verify: " + "; ".join(problems[:6]))
    box, template = args.box, args.template
    if box in quick_loop.PLAYER_BOXES.values() or box == template:
        raise SystemExit(f"{box} is a dev-stack box: install-box takes a fresh one (Client5Box)")
    try:
        ini = SANDBOXIE_INI.read_text(encoding="utf-16") if SANDBOXIE_INI.read_bytes()[:2] == b"\xff\xfe" else SANDBOXIE_INI.read_text(encoding="utf-8", errors="replace")
    except OSError as error:
        raise SystemExit(f"cannot read {SANDBOXIE_INI}: {error}") from None
    if not sandboxie_has_box(ini, box):
        say(box_missing_text(box, template))
        return 2

    template_root = Path(f"C:/Sandbox/{quick_loop.CURRENT_USER}/{template}")
    box_root = template_root.parent / box
    template_profile = quick_loop.box(template)
    profile = box_profile(template_profile, template, box)
    box_root.mkdir(parents=True, exist_ok=True)
    hive = box_root / "RegHive"
    if not hive.exists():
        # Only the registry hive: TS3 in the box then keeps its profile under C:\Sims\.sims3-client inside the box,
        # as in the template. Copied while nothing runs in the new box (it has no hive yet).
        shutil.copy2(template_root / "RegHive", hive)
        say(f"{box}: registry hive from {template} (the box's Documents folder)")
    profile.mkdir(parents=True, exist_ok=True)
    for name in ("Options.ini",):  # the template's display options, so the box's game opens windowed like the others
        if not (profile / name).exists() and (template_profile / name).is_file():
            shutil.copy2(template_profile / name, profile / name)
            say(f"{box}: {name} from {template} (box only; a friend keeps their own)")

    # Game\Bin is shared with the host's games: install-box never writes it. It only checks the kit's copies match.
    bin_dir = game_bin()
    for name in ("SimLink.asi", "wininet.dll"):
        if (kit / "game-bin" / name).is_file():
            same = (bin_dir / name).is_file() and sha256(bin_dir / name) == sha256(kit / "game-bin" / name)
            say(f"Game\\Bin\\{name}: {'same as the kit' if same else 'DIFFERS from the kit (the box runs the host one)'}")

    install_dir = INSTALLS / box
    cmd = [str(WINDOWS_POWERSHELL), "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(kit / "install.ps1"),
           "-GameDir", str(bin_dir.parent.parent), "-ProfileDir", str(profile), "-InstallDir", str(install_dir),
           "-NoShortcut", "-SkipGameBin", "-AllowRunningGame", "-Player", str(args.player)]
    if args.sim:
        cmd += ["-Sim", args.sim]
    if args.server:
        cmd += ["-Server", args.server]
    say(f"install.ps1 (Windows PowerShell 5.1) into {box}")
    # Without PowerShell 7's module path, as when a friend double-clicks install.cmd.
    env = {k: v for k, v in os.environ.items() if k.upper() != "PSMODULEPATH"}
    out = subprocess.run(cmd, cwd=kit, env=env, capture_output=True, text=True, timeout=600, check=False)
    print((out.stdout or "") + (out.stderr or ""), end="", flush=True)
    if out.returncode != 0:
        return out.returncode
    bridgectl = f"127.0.0.1:{34029 + args.player}"
    say("installed. Join from the box (dev options: the box, its Saves, the bridgectl and state file scenarios read):")
    print(f'  powershell -NoProfile -ExecutionPolicy Bypass -File "{install_dir / "join.ps1"}" -Server {args.server or "127.0.0.1:34019"} '
          f'-Sim {args.sim or "<name>"} -Player {args.player} -SandboxieBox {box} -SavesDir "{profile / "Saves"}" '
          f'-BridgectlListen {bridgectl} -StateFile "{ROOT / ".omo/run" / f"client{args.player}.state.json"}"')
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="verb", required=True)
    b = sub.add_parser("build", help="assemble a kit folder and zip from the current build")
    b.add_argument("--no-mod-build", action="store_true", help="take the mod package already built (bin/Release)")
    b.add_argument("--no-publish", action="store_true", help="take the client already published (.omo/run/tester-kit/_publish)")
    b.add_argument("--no-fork", action="store_true", help="leave the gameplay fork package out (EA-derived)")
    b.add_argument("--fork-builder", action="store_true",
                   help="ship the fork builder (build-fork.ps1, fork-builder/) in place of the fork package: no EA-derived file")
    b.add_argument("--no-loader", action="store_true", help="leave the ASI loader out (third-party)")
    b.add_argument("--simlink", help="SimLink.asi to ship (default: the host's Game\\Bin copy)")
    b.add_argument("--loader", help="wininet.dll to ship (default: the host's Game\\Bin copy)")
    v = sub.add_parser("verify", help="check a kit folder against its manifest")
    v.add_argument("kit", nargs="?", help="kit folder (default: the newest)")
    i = sub.add_parser("install-box", help="install a kit into a fresh Sandboxie box on this machine (the proof)")
    i.add_argument("--box", required=True)
    i.add_argument("--kit", help="kit folder (default: the newest)")
    i.add_argument("--template", default="Client4Box", help="box whose registry hive and Options.ini the new box takes")
    i.add_argument("--player", type=int, default=2)
    i.add_argument("--sim", default="", help="the Sim's first name, kept in the install's config.json")
    i.add_argument("--server", default="127.0.0.1:34019")
    sub.add_parser("list", help="the kits built here, newest first")
    c = sub.add_parser("check", help="verify a kit and check it holds no EA-derived file (the --fork-builder proof)")
    c.add_argument("kit", nargs="?", help="kit folder (default: the newest)")
    args = parser.parse_args(argv)

    if args.verb == "build":
        return build(args)
    if args.verb == "verify":
        kit = Path(args.kit) if args.kit else next(iter(kits()), None)
        if kit is None:
            raise SystemExit("no kit built here")
        problems = verify(kit)
        for problem in problems:
            print(problem)
        say(f"{kit}: {'whole' if not problems else f'{len(problems)} problem(s)'}")
        return 1 if problems else 0
    if args.verb == "install-box":
        return install_box(args)
    if args.verb == "check":
        kit = Path(args.kit) if args.kit else next(iter(kits()), None)
        if kit is None:
            raise SystemExit("no kit built here")
        problems = verify(kit) + ea_check(kit, game_file_hashes(game_bin(), OUT / "_fork-builder-work/assemblies"))
        for problem in problems:
            print(problem)
        say(f"{kit}: {'whole, no EA-derived file' if not problems else f'{len(problems)} problem(s)'}")
        return 1 if problems else 0
    for kit in kits():
        manifest = json.loads((kit / MANIFEST).read_text(encoding="utf-8"))
        print(f"{kit.name}  feed {str(manifest.get('feed_commit'))[:10]}  fork {manifest.get('fork_attempt') or manifest.get('fork')}  "
              f"SimLink {manifest.get('simlink')}  {manifest.get('total_bytes', 0) / 1e6:.1f} MB")
    return 0


if __name__ == "__main__":
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    raise SystemExit(main())
