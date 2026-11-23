"""Build slots: a whole build of the stack (the host mod package, SimLink.asi, the DedicatedServer and
SimsOnline.Client controllers) kept as files with their hashes, installed for an arm and checked after it, so an A/B can
run an old build against today's on one save (docs/engine/sim166-rollback-series.plan.json).

    python scripts/build_slot.py make NAME --from CHECKOUT   # from another checkout's built parts (a worktree at 6d41ee2)
    python scripts/build_slot.py make NAME --installed       # from what this checkout's stack runs now (run it from simscope)
    python scripts/build_slot.py list                        # the slots, and which one is installed
    python scripts/build_slot.py install NAME                # games down: NAME's mod and SimLink in, the controllers on its binaries
    python scripts/build_slot.py check [--expect NAME]       # the installed build against the slots: hashOk=1 slot_NAME=1
    python scripts/build_slot.py after ARM REPEAT --build ARM=SLOT ...   # the arms --after hook
    python scripts/build_slot.py release                     # the controllers back on dotnet run --project (the pointer removed)

As an arms pair, every arm booted afresh on the pinned save with its own build (saves.py arm --build installs the arm's
slot after the down and boots --sequential):

    python scripts/saves.py backup day0
    python scripts/scenarios.py arms ... --arm new=... --arm old=--no-repair \\
        --boot-hook "scripts/saves.py arm {arm} {repeat} --base day0 --use new=PINNED --use old=PINNED --build new=new --build old=old" \\
        --after "scripts/build_slot.py after {arm} {repeat} --build new=new --build old=old" \\
        --gate "new,old: hook.hashOk == 1" --gate "new: hook.slot_new == 1" --gate "old: hook.slot_old == 1"
    python scripts/build_slot.py install new && python scripts/build_slot.py release   # today's build back
    python scripts/saves.py restore day0

The parts:
- the mod, SimsOnlineServerHostMod.package: copied into every profile quick_loop's --mod installs it in (the server's,
  OneDrive's and each Sandboxie box's), each copy hash-checked;
- SimLink.asi: copied into Game\\Bin, hash-checked;
- the controllers: not copied. install writes .omo/run/build-slot.json, and stackctl then launches the slot's own
  SimsOnline.DedicatedServer.exe and SimsOnline.Client.exe (from the slot's folder, with the checkout as the working
  directory) in place of dotnet run --project, so a failed arm never leaves this checkout's build changed. While a slot
  is installed, quick_loop never rebuilds the client (a client older than a64d473, as 6d41ee2's, lacks the wait a
  parallel boot needs: saves.py arm --build boots --sequential) and never installs a mod over it.
check, and the after hook, hash every installed part against a slot's manifest. hashOk=1 when the mod in every
profile, SimLink.asi and the pointer are all the arm's slot's and the slot's own files are whole; slot_NAME=1 names
the slot the installed build is, slot_NAME=0 the expected one when it is not. A slot name is letters, digits and _ (it
becomes slot_NAME). Every call appends a row to .omo/run/build-slots/slots.jsonl. Files only, plus the process list
(tasklist) to refuse while a game or a controller runs: no handle on a game.

Run every verb, and the arms pair, from the checkout whose stack runs the games (simscope's): its stackctl launches
the controllers and reads the pointer. The pointer and the slots live in SIMSONLINE_RUN_DIR when it is set, as
stackctl's run files do, else in this checkout's .omo/run. While a slot is installed, stackctl's mod sync runs no
make-mod (its status prints `build slot: NAME`); release first for the workspace's build. The controllers keep the
checkout as their working directory (dotnet run's too), so Sentry's release tag is the checkout's, not the slot's.
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
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

sys.path.insert(0, str(Path(__file__).resolve().parent))
import saves  # noqa: E402  scripts/saves.py: sha256, hashes, arm_after

ROOT = Path(__file__).resolve().parent.parent
RUN = Path(os.environ.get("SIMSONLINE_RUN_DIR") or ROOT / ".omo" / "run")  # as stackctl's RUN_DIR
STORE = RUN / "build-slots"
POINTER = RUN / "build-slot.json"  # stackctl.BUILD_SLOT_FILE and quick_loop.SLOT_POINTER read it
MOD = "SimsOnlineServerHostMod.package"
SIMLINK = "SimLink.asi"
# part: (the project folder, the controller's apphost name)
CONTROLLERS = {"server": ("DedicatedServer", "SimsOnline.DedicatedServer"), "client": ("SimsOnline.Client", "SimsOnline.Client")}
SLOT_NAME = re.compile(r"[A-Za-z0-9_]+")
RUNNING = ("TS3W.exe", "SimsOnline.DedicatedServer.exe", "SimsOnline.Client.exe")


class SlotError(Exception):
    pass


@dataclass
class Layout:
    """Where a build is installed: the mod's copies, SimLink.asi, the pointer and the slot store; checkout is the one
    whose own build make --installed reads."""
    mod_targets: list[Path]
    simlink: Path
    pointer: Path = POINTER
    store: Path = STORE
    checkout: Path = ROOT


def live_layout() -> Layout:
    import quick_loop
    from s3mm import game_bin

    return Layout(quick_loop.mod_targets(), game_bin() / SIMLINK)


def utc() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def log_row(row: dict, layout: Layout) -> None:
    layout.store.mkdir(parents=True, exist_ok=True)
    with open(layout.store / "slots.jsonl", "a", encoding="utf-8") as f:
        f.write(json.dumps({"t": round(time.time(), 3), **row}) + "\n")


def digest_of(manifest: dict) -> str:
    """A short name for a slot's files: the same build gives the same digest."""
    table = {"mod": manifest["mod"]["sha256"], "simlink": manifest["simlink"]["sha256"],
             **{part: manifest[part]["files"] for part in CONTROLLERS}}
    return hashlib.sha256(json.dumps(table, sort_keys=True).encode()).hexdigest()[:12]


def target_framework(checkout: Path) -> str:
    for path in (checkout / "Directory.Build.props", checkout / "DedicatedServer" / "SimsOnline.DedicatedServer.csproj"):
        try:
            found = re.search(r"<TargetFramework>([^<]+)</TargetFramework>", path.read_text(encoding="utf-8"))
        except OSError:
            continue
        if found:
            return found.group(1).strip()
    raise SlotError(f"no <TargetFramework> in {checkout}'s Directory.Build.props")


def git_origin(checkout: Path) -> dict:
    def git(*args: str) -> str:
        try:
            return subprocess.run(["git", "-C", str(checkout), *args], capture_output=True, text=True, timeout=30).stdout.strip()
        except (OSError, subprocess.TimeoutExpired):
            return ""

    dirty = [line for line in git("status", "--porcelain", "--untracked-files=no").splitlines() if line.strip()]
    return {"checkout": str(checkout), "commit": git("rev-parse", "HEAD") or None, "dirty": len(dirty)}


def read_pointer(layout: Layout) -> dict | None:
    try:
        return json.loads(layout.pointer.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def running() -> list[str]:
    """The games and controllers running now, by the process list (no handle on a game)."""
    if sys.platform != "win32":
        return []
    out = subprocess.run(["tasklist", "/FO", "CSV", "/NH"], capture_output=True, text=True).stdout
    names = {line.split('","')[0].strip('"').lower() for line in out.splitlines() if line.strip()}
    return [name for name in RUNNING if name.lower() in names]


# --- making a slot ---------------------------------------------------------------------------------------------------

def checkout_parts(checkout: Path) -> dict[str, Path]:
    """Another checkout's built parts: the mod's Release package, SimLink's Release build, both controllers' Debug
    output (the configuration stackctl runs)."""
    tfm = target_framework(checkout)
    return {"mod": checkout / "SimsOnlineServerHostMod" / "bin" / "Release" / MOD,
            "simlink": checkout / "SimLink" / "build" / "Release" / SIMLINK,
            **{part: checkout / project / "bin" / "Debug" / tfm for part, (project, _) in CONTROLLERS.items()}}


def installed_parts(layout: Layout) -> dict[str, Path]:
    """What this checkout's stack runs now: the mod (every profile's copy the same), SimLink.asi, and this checkout's
    own controller builds. Refused while a slot is installed: the controllers then run the slot's."""
    pointer = read_pointer(layout)
    if pointer:
        raise SlotError(f"slot {pointer.get('slot')} is installed ({layout.pointer}): make --installed reads this "
                        "checkout's own build; build_slot.py install the slot you want, then release, first")
    missing = [str(t) for t in layout.mod_targets if not t.is_file()]
    if missing:
        raise SlotError("the mod is not installed at " + ", ".join(missing))
    copies = {saves.sha256(t) for t in layout.mod_targets}
    if len(copies) > 1:
        raise SlotError(f"the mod's {len(layout.mod_targets)} copies differ ({len(copies)} builds): install one first "
                        "(quick_loop.py run --mod)")
    tfm = target_framework(layout.checkout)
    return {"mod": layout.mod_targets[0], "simlink": layout.simlink,
            **{part: layout.checkout / project / "bin" / "Debug" / tfm for part, (project, _) in CONTROLLERS.items()}}


def make(name: str, parts: dict[str, Path], origin: dict, layout: Layout) -> tuple[bool, list[str]]:
    if not SLOT_NAME.fullmatch(name):
        return False, [f"make: {name!r}: a slot name is letters, digits and _ (it becomes the hook's slot_{name})"]
    dest, stage = layout.store / name, layout.store / f"{name}.making"
    if dest.exists():
        return False, [f"make: slot {name} exists ({dest}): pick another name"]
    if stage.exists():
        return False, [f"make: {stage} is left from a make that did not finish: look at it, then remove it"]
    missing = [f"{part} {path}" for part, path in parts.items() if not path.exists()]
    missing += [f"{part} {parts[part] / (exe + '.exe')}" for part, (_, exe) in CONTROLLERS.items()
                if parts[part].exists() and not (parts[part] / (exe + ".exe")).is_file()]
    if missing:
        return False, ["make: not built: " + "; ".join(missing)]
    want = {"mod": saves.sha256(parts["mod"]), "simlink": saves.sha256(parts["simlink"]),
            **{part: saves.hashes(parts[part]) for part in CONTROLLERS}}
    (stage / "mod").mkdir(parents=True)
    (stage / "simlink").mkdir()
    shutil.copyfile(parts["mod"], stage / "mod" / MOD)
    shutil.copyfile(parts["simlink"], stage / "simlink" / SIMLINK)
    for part in CONTROLLERS:
        shutil.copytree(parts[part], stage / part)
    manifest = {"name": name, "made": utc(), "origin": origin, "sources": {k: str(v) for k, v in parts.items()},
                "mod": {"file": f"mod/{MOD}", "sha256": want["mod"]},
                "simlink": {"file": f"simlink/{SIMLINK}", "sha256": want["simlink"]},
                **{part: {"folder": part, "exe": CONTROLLERS[part][1] + ".exe", "files": want[part]} for part in CONTROLLERS}}
    manifest["digest"] = digest_of(manifest)
    problems = slot_problems(stage, manifest)
    if problems:
        shutil.rmtree(stage)
        return False, [f"make: the copy differs from its sources ({'; '.join(problems[:4])}); nothing kept"]
    (stage / "manifest.json").write_text(json.dumps(manifest, indent=1) + "\n", encoding="utf-8")
    stage.rename(dest)
    log_row({"verb": "make", "slot": name, "digest": manifest["digest"], "origin": origin}, layout)
    files = sum(len(manifest[part]["files"]) for part in CONTROLLERS)
    return True, [f"make: slot {name} ({manifest['digest']}) in {dest}: the mod, SimLink.asi and {files} controller "
                  f"file(s), from {origin.get('commit') or origin.get('kind')}"
                  + (f" ({origin['dirty']} file(s) changed in that checkout)" if origin.get("dirty") else "")]


# --- a slot's files and the installed build --------------------------------------------------------------------------

def slot_problems(folder: Path, manifest: dict) -> list[str]:
    """Where a slot's own files differ from its manifest."""
    problems = []
    for part in ("mod", "simlink"):
        path = folder / manifest[part]["file"]
        if not path.is_file() or saves.sha256(path) != manifest[part]["sha256"]:
            problems.append(f"the slot's {manifest[part]['file']} is {'missing' if not path.is_file() else 'changed'}")
    for part in CONTROLLERS:
        found = saves.differences(manifest[part]["files"], saves.hashes(folder / manifest[part]["folder"]))
        problems += [f"the slot's {part} {line}" for line in found[:4]]
    return problems


def load_slot(name: str, layout: Layout) -> dict:
    try:
        return json.loads((layout.store / name / "manifest.json").read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        raise SlotError(f"no slot {name} ({layout.store / name}: {error.__class__.__name__})") from None


def slot_names(layout: Layout) -> list[str]:
    return sorted(p.parent.name for p in layout.store.glob("*/manifest.json")) if layout.store.is_dir() else []


def exes(name: str, manifest: dict, layout: Layout) -> dict[str, str]:
    return {part: str(layout.store / name / manifest[part]["folder"] / manifest[part]["exe"]) for part in CONTROLLERS}


def put_file(source: Path, target: Path, want: str) -> None:
    """Copy beside the target, check, rename over it, check again."""
    temp = target.with_name(target.name + ".slot-copy")
    shutil.copyfile(source, temp)
    if saves.sha256(temp) != want:
        temp.unlink()
        raise SlotError(f"the copy for {target} differs from the slot's; {target} is untouched")
    os.replace(temp, target)
    if saves.sha256(target) != want:
        raise SlotError(f"{target} differs from the slot's after the copy")


def install(name: str, layout: Layout, up: Callable[[], list[str]] = running) -> tuple[bool, list[str]]:
    """Games and controllers down: the slot's mod into every profile, its SimLink.asi into Game\\Bin, and the
    controllers pointed at its binaries; then check."""
    try:
        manifest = load_slot(name, layout)
    except SlotError as error:
        return False, [f"install: {error}"]
    problems = slot_problems(layout.store / name, manifest)
    if problems:
        return False, [f"install: slot {name} is not whole: " + "; ".join(problems[:6])]
    live = up()
    if live:
        return False, [f"install: {', '.join(live)} running: stop the stack first (stackctl.py down --force)"]
    if not layout.simlink.parent.is_dir():
        return False, [f"install: no {layout.simlink.parent} (the game's Bin folder): nothing installed"]
    notes = []
    if not layout.simlink.exists() and layout.simlink.with_name(SIMLINK + ".off").exists():
        notes.append(f"{SIMLINK}.off was there (SimLink off): the slot's {SIMLINK} is on now")
    try:
        for target in layout.mod_targets:
            target.parent.mkdir(parents=True, exist_ok=True)
            put_file(layout.store / name / manifest["mod"]["file"], target, manifest["mod"]["sha256"])
        put_file(layout.store / name / manifest["simlink"]["file"], layout.simlink, manifest["simlink"]["sha256"])
    except (SlotError, OSError) as error:
        log_row({"verb": "install", "slot": name, "ok": False, "why": str(error)}, layout)
        return False, [f"install: stopped part way ({error}); build_slot.py check says what is in place"]
    pointer = {"slot": name, "digest": manifest["digest"], **exes(name, manifest, layout), "installed": utc()}
    temp = layout.pointer.with_name(layout.pointer.name + ".tmp")
    layout.pointer.parent.mkdir(parents=True, exist_ok=True)
    temp.write_text(json.dumps(pointer, indent=1) + "\n", encoding="utf-8")
    os.replace(temp, layout.pointer)
    ok, found, lines = check(name, layout)
    log_row({"verb": "install", "slot": name, "digest": manifest["digest"], "ok": ok, "notes": notes}, layout)
    return ok, [f"install: slot {name} ({manifest['digest']}): the mod in {len(layout.mod_targets)} profile(s), "
                f"{SIMLINK}, the controllers on {layout.store / name}"] + [f"  {n}" for n in notes] + lines


def check(expect: str | None, layout: Layout) -> tuple[bool, str | None, list[str]]:
    """The installed build against the slots (only `expect` when given): (whether it is that slot, the slot it is,
    the lines to print, with hashOk= and slot_NAME= for the gates)."""
    pointer = read_pointer(layout)
    mods = {t: saves.sha256(t) if t.is_file() else None for t in layout.mod_targets}
    simlink = saves.sha256(layout.simlink) if layout.simlink.is_file() else None
    found, diffs = None, []
    for name in ([expect] if expect else slot_names(layout)):
        try:
            manifest = load_slot(name, layout)
        except SlotError as error:
            diffs = [str(error)]
            continue
        diffs = slot_problems(layout.store / name, manifest)
        diffs += [f"the mod at {t}: {'missing' if sha is None else 'another build'}" for t, sha in mods.items()
                  if sha != manifest["mod"]["sha256"]]
        if simlink != manifest["simlink"]["sha256"]:
            diffs.append(f"{layout.simlink}: {'missing' if simlink is None else 'another build'}")
        if not pointer:
            diffs.append("the controllers: dotnet run --project (no slot pointer)")
        elif pointer.get("slot") != name or {p: pointer.get(p) for p in CONTROLLERS} != exes(name, manifest, layout):
            diffs.append(f"the controllers: pointed at slot {pointer.get('slot')}")
        if not diffs:
            found = name
            break
    if found:
        lines = [f"build: installed is slot {found} ({pointer['digest'] if pointer else '?'}; the mod in "
                 f"{len(mods)} profile(s), {SIMLINK}, both controllers): hashOk=1 slot_{found}=1"]
    elif expect:
        lines = [f"build: installed is not slot {expect}: hashOk=0 slot_{expect}=0"] + [f"  {d}" for d in diffs[:8]]
    else:
        lines = ["build: installed is no slot: hashOk=0" + (f" (pointer names {pointer.get('slot')})" if pointer else "")]
    return found is not None, found, lines


def after(arm: str, repeat: int, builds: dict[str, str], layout: Layout,
          ran: Callable[[str, int], int] = saves.arm_after) -> tuple[bool, list[str]]:
    """The arms --after hook: whether the installed build is the arm's slot (its gate reads hashOk and slot_NAME),
    and saves.py's verdict on whether the arm ran on the boot its boot hook made."""
    name = arm.split(":", 1)[0]  # a capped catcher arm runs as NAME:bare
    expect = builds.get(name)
    if expect is None:
        lines = [f"build: no --build for arm {name}: hashOk=0"]
        ok = False
    else:
        ok, _, lines = check(expect, layout)
    ran(arm, repeat)
    log_row({"verb": "after", "arm": arm, "repeat": repeat, "want": expect, "ok": ok}, layout)
    return ok, lines


def release(layout: Layout) -> tuple[bool, list[str]]:
    pointer = read_pointer(layout)
    if not pointer:
        return True, ["release: no slot is installed: the controllers run dotnet run --project"]
    layout.pointer.unlink()
    log_row({"verb": "release", "slot": pointer.get("slot")}, layout)
    return True, [f"release: the controllers run dotnet run --project again; slot {pointer.get('slot')}'s mod and "
                  f"{SIMLINK} stay in place (build_slot.py install new first, for today's)"]


def listing(layout: Layout) -> list[str]:
    names = slot_names(layout)
    lines = [f"{len(names)} slot(s) in {layout.store}"]
    for name in names:
        manifest = load_slot(name, layout)
        origin = manifest.get("origin") or {}
        lines.append(f"  {name} ({manifest['digest']}): made {manifest['made']} from "
                     f"{origin.get('commit') or origin.get('kind')} ({origin.get('checkout', '')})")
    return lines + check(None, layout)[2]


def parse_builds(items: list[str]) -> dict[str, str]:
    builds = {}
    for item in items:
        arm, sep, slot = item.partition("=")
        if not sep or not arm or not SLOT_NAME.fullmatch(slot):
            raise SystemExit(f"--build wants ARM=SLOT (a slot name of letters, digits and _), not {item!r}")
        builds[arm] = slot
    return builds


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="verb", required=True)
    p = sub.add_parser("make", help="a slot from a checkout's built parts, or from what this checkout's stack runs")
    p.add_argument("name")
    source = p.add_mutually_exclusive_group(required=True)
    source.add_argument("--from", dest="checkout", type=Path, metavar="CHECKOUT")
    source.add_argument("--installed", action="store_true")
    sub.add_parser("list")
    sub.add_parser("install").add_argument("name")
    sub.add_parser("check").add_argument("--expect", metavar="NAME")
    p = sub.add_parser("after", help="the arms --after hook")
    p.add_argument("arm")
    p.add_argument("repeat", type=int)
    p.add_argument("--build", action="append", default=[], metavar="ARM=SLOT")
    sub.add_parser("release")
    a = parser.parse_args(argv)
    layout = live_layout()
    try:
        if a.verb == "make":
            if a.installed:
                ok, lines = make(a.name, installed_parts(layout), {"kind": "installed", **git_origin(layout.checkout)}, layout)
            else:
                ok, lines = make(a.name, checkout_parts(a.checkout.resolve()),
                                 {"kind": "checkout", **git_origin(a.checkout.resolve())}, layout)
        elif a.verb == "list":
            ok, lines = True, listing(layout)
        elif a.verb == "install":
            ok, lines = install(a.name, layout)
        elif a.verb == "check":
            ok, _, lines = check(a.expect, layout)
        elif a.verb == "after":
            ok, lines = after(a.arm, a.repeat, parse_builds(a.build), layout)
            ok = True  # the gates decide: the hook itself never fails an arm
        else:
            ok, lines = release(layout)
    except SlotError as error:
        ok, lines = False, [f"{a.verb}: {error}"]
    print("\n".join(lines))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
