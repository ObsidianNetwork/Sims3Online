"""TS3 saves for an A/B on the save itself: back up both games' Saves folders, put another save in the
server's boot save, restore, every step hash-verified; and an arms --boot-hook/--after pair that boots each arm on its
own untouched save.

    python scripts/saves.py backup NAME          # both Saves folders to .omo/run/saves/NAME, with hashes.txt
    python scripts/saves.py verify NAME          # the live Saves folders against that backup
    python scripts/saves.py swap SOURCE          # games down: the boot save's files become SOURCE's (a .sims3 folder)
    python scripts/saves.py restore NAME         # games down: both Saves folders back from NAME, every hash checked
    python scripts/saves.py list                 # the backups, and the last rows of saves.jsonl

As an arms pair (an old-save arm "old" against "bare", which boots on the backup's own boot save):

    python scripts/saves.py backup base
    python scripts/scenarios.py arms --arm bare= --arm old= \\
        --boot-hook "scripts/saves.py arm {arm} {repeat} --base base --use old=<a .sims3 folder>" \\
        --after "scripts/saves.py arm-after {arm} {repeat}"
    python scripts/saves.py restore base         # after the arms: both Saves folders as they were

The server rewrites its boot save at every boot: a joining client's world is the server's session, saved to
Option4BookChain.sims3 (SAVE_GAME) when the client asks, and TS3 rotates the last one into .sims3.backup. So no arm can
reuse the last boot's save, and the boot hook boots every arm afresh: it stops both games (stackctl.py down --force),
puts the arm's untouched save in (an arm named in --use gets SOURCE less simsonline-stock-boot.txt, every other arm the
base backup's boot save; a capped catcher arm's NAME:bare counts as NAME), and boots (quick_loop.py run --boot, the boot
arms itself uses: 1.5-3.6 min on 2026-10-18). Each arm then starts on the same save as every other run of it, with a
game of the same age. The after hook records whether the arm ran on the boot the boot hook made. arm's
--server-env K=V and --client-env K=V (repeatable) go to its quick_loop.py run --boot, so an arms run with a boot env
(arms --server-env, or an arm's own) gives them through its placeholders: --boot-hook "scripts/saves.py arm {arm}
{repeat} --base base {server_env} {client_env}" boots each arm on its save with its env (saves.jsonl's boot row keeps
it as boot_env). arm's --build ARM=SLOT (repeatable, G17) also installs the arm's build slot after the down
(build_slot.py install: the slot's mod and SimLink.asi in place, the controllers on its binaries) and boots
--sequential, since an old slot's client cannot wait for the server's listener; an arm with no --build is refused.
build_slot.py after is its --after hook.

arms runs the boot hook in place of its own reboot, after the last arm's gap and before the arm's --set tunables and
process snapshot, so a save arm keeps its --set and its row's process note names the game the hook booted; crashes
during the boot are the arm's crashes_boot. When a step fails, the hook leaves both games down and exits 1, and arms
stops the run (exit 6, the arm "not run"; saves.jsonl's "refused" row says why). The hook also writes the run's "stop"
file (runner.json names the runner), which scenarios.py arms --resume clears.

As --before or --after (SIMSONLINE_ARMS_HOOK says which slot arms runs it in) it refuses, the same way: a before hook
runs after the arm's sets and process snapshot, so its boot would lose both. Run by hand it boots as asked.

Every swap and restore copies into a staging folder beside the target and checks its hashes. Then it renames the live
folder aside, renames the staged one in, checks again, and deletes the old one only then. A folder left from a step
that died is never deleted: the step refuses until someone looks. Every call appends a row to .omo/run/saves/saves.jsonl
(arms.jsonl keeps only a hook's last line).
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
from typing import Callable

ROOT = Path(__file__).resolve().parent.parent
RUN = ROOT / ".omo" / "run"
STORE = RUN / "saves"
ARMS = RUN / "arms"  # scenarios.py arms' folders: pass 3's runner keeps runner.json there while it runs
BOOT_SAVE = "Option4BookChain.sims3"
STOCK_MARKER = "simsonline-stock-boot.txt"  # quick_loop's stock boot save: fork-install moves a marked save aside
DOWN_TIMEOUT = 480  # stackctl down waits up to 5 min for a catcher dump being written first ( G21)
BOOT_TIMEOUT = 1300  # with the down and the copies, inside run_hook's 1800 s: a killed hook would orphan the boot


class SavesError(Exception):
    pass


def live_saves() -> dict[str, Path]:
    """The two Saves folders as quick_loop and stackctl find them: the server profile's (SIMSONLINE_REAL_PROFILE) and
    the client box's (SIMSONLINE_CLIENT_SAVES_DIR, else Client2Box's profile)."""
    sys.path.insert(0, str(ROOT / "scripts"))
    import quick_loop

    client = os.environ.get("SIMSONLINE_CLIENT_SAVES_DIR")
    return {"server-Saves": quick_loop.REAL / "Saves",
            "client-Saves": Path(client) if client else quick_loop.box(quick_loop.CLIENT_BOX) / "Saves"}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def hashes(root: Path, skip: frozenset[str] = frozenset()) -> dict[str, str]:
    """Each file under root by its relative path; top-level names in skip are left out."""
    if not root.is_dir():
        return {}
    return {p.relative_to(root).as_posix(): sha256(p) for p in sorted(root.rglob("*"))
            if p.is_file() and p.relative_to(root).parts[0] not in skip}


def digest_of(table: dict[str, str]) -> str:
    """A short name for a set of files: the same save content gives the same digest."""
    return hashlib.sha256(json.dumps(table, sort_keys=True).encode()).hexdigest()[:12]


def tree_hashes(trees: dict[str, Path]) -> dict[str, str]:
    return {f"{name}/{rel}": digest for name, root in trees.items() for rel, digest in hashes(root).items()}


def write_hashes(path: Path, table: dict[str, str]) -> None:
    path.write_text("".join(f"{digest}  {name}\n" for name, digest in sorted(table.items())), encoding="utf-8")


def read_hashes(path: Path) -> dict[str, str]:
    """hashes.txt (sha256sum's layout: `sha256sum -c` checks it from the backup folder)."""
    table = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.strip():
            digest, name = line.split("  ", 1)
            table[name.strip()] = digest
    return table


def differences(want: dict[str, str], have: dict[str, str]) -> list[str]:
    missing = sorted(set(want) - set(have))
    extra = sorted(set(have) - set(want))
    differ = sorted(k for k in set(want) & set(have) if want[k] != have[k])
    return [f"missing {k}" for k in missing] + [f"extra {k}" for k in extra] + [f"differs {k}" for k in differ]


def compare_text(want: dict[str, str], have: dict[str, str]) -> tuple[bool, str]:
    found = differences(want, have)
    text = f"{len(want)} file(s) expected, {len(have)} found: " + ("identical" if not found else f"{len(found)} differ")
    return not found, "\n".join([text] + [f"  {line}" for line in found[:12]])


def replace_folder(target: Path, source: Path, skip: frozenset[str] = frozenset()) -> dict[str, str]:
    """Make target hold source's files (top-level names in skip left out), never without a full copy on disk: stage,
    check, rename the live folder aside, rename the stage in, check, then delete the old folder."""
    want = hashes(source, skip)
    if not want:
        raise SavesError(f"{source} has no files")
    stage = target.with_name(f"{target.name}.saves-staging")
    aside = target.with_name(f"{target.name}.saves-aside")
    for left in (stage, aside):
        if left.exists():
            raise SavesError(f"{left} is left from a step that did not finish: look at it, then move it away")
    shutil.copytree(source, stage, ignore=lambda folder, names: [n for n in names if Path(folder) == source and n in skip])
    if hashes(stage) != want:
        shutil.rmtree(stage)
        raise SavesError(f"the staged copy of {source} differs from it; {target} is untouched")
    if target.exists():
        try:
            target.rename(aside)
        except OSError as error:
            shutil.rmtree(stage)
            raise SavesError(f"{target} cannot be moved aside ({error}): something holds it; it is untouched") from error
    try:
        stage.rename(target)
    except OSError as error:
        if aside.exists():
            aside.rename(target)
        raise SavesError(f"the staged copy cannot take {target}'s place ({error}); {target} is back as it was") from error
    if hashes(target) != want:
        raise SavesError(f"{target} differs from {source} after the rename; the old folder is kept in {aside}")
    if aside.exists():
        shutil.rmtree(aside)
    return want


def ts3_running() -> bool:
    """Whether any TS3W.exe runs (the process list only: no handle on a game)."""
    if sys.platform != "win32":
        return False
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq TS3W.exe", "/NH"], capture_output=True, text=True).stdout
    return "TS3W.exe" in out


def log_row(row: dict, store: Path = STORE) -> None:
    store.mkdir(parents=True, exist_ok=True)
    with open(store / "saves.jsonl", "a", encoding="utf-8") as f:
        f.write(json.dumps({"t": round(time.time(), 3), **row}) + "\n")


def read_rows(store: Path = STORE) -> list[dict]:
    try:
        lines = (store / "saves.jsonl").read_text(encoding="utf-8").splitlines()
    except OSError:
        return []
    rows = []
    for line in lines:
        try:
            rows.append(json.loads(line))
        except ValueError:
            pass
    return rows


def backup(name: str, live: dict[str, Path], store: Path = STORE) -> int:
    """Copy both Saves folders, write hashes.txt from the copy, and check the copy against the live folders (a game
    that wrote meanwhile shows as a difference)."""
    dest = store / name
    if dest.exists():
        print(f"{dest} exists: pick another name")
        return 1
    for tree, root in live.items():
        if not root.is_dir():
            print(f"no {tree} folder at {root}")
            return 1
    dest.mkdir(parents=True)
    for tree, root in live.items():
        shutil.copytree(root, dest / tree)
    table = tree_hashes({tree: dest / tree for tree in live})
    write_hashes(dest / "hashes.txt", table)
    (dest / "sources.json").write_text(json.dumps({t: str(p) for t, p in live.items()}, indent=1) + "\n", encoding="utf-8")
    same, text = compare_text(table, tree_hashes(live))
    log_row({"verb": "backup", "name": name, "files": len(table), "same": same}, store)
    print(f"backup {dest}: {text}")
    return 0 if same else 1


def verify(name: str, live: dict[str, Path], store: Path = STORE) -> int:
    same, text = compare_text(read_hashes(store / name / "hashes.txt"), tree_hashes(live))
    print(f"{name} against the live Saves folders: {text}")
    return 0 if same else 1


def put_boot_save(source: Path, live: dict[str, Path], skip: frozenset[str]) -> dict[str, str]:
    return replace_folder(live["server-Saves"] / BOOT_SAVE, source, skip)


def swap(source: Path, live: dict[str, Path], store: Path = STORE, games_up: Callable[[], bool] = ts3_running) -> int:
    if games_up():
        print("a TS3W.exe runs: stop both games first (stackctl.py down --force)")
        return 1
    try:
        files = put_boot_save(source, live, frozenset({STOCK_MARKER}))
    except SavesError as error:
        print(f"swap refused: {error}")
        return 1
    log_row({"verb": "swap", "source": str(source), "save": digest_of(files)}, store)
    print(f"{live['server-Saves'] / BOOT_SAVE} now holds {source}'s {len(files)} file(s), hash-checked")
    return 0


def restore(name: str, live: dict[str, Path], store: Path = STORE, games_up: Callable[[], bool] = ts3_running) -> int:
    if games_up():
        print("a TS3W.exe runs: stop both games first (stackctl.py down --force)")
        return 1
    folder = store / name
    if not (folder / "hashes.txt").exists():
        print(f"no backup {folder}")
        return 1
    try:
        for tree, root in live.items():
            replace_folder(root, folder / tree)
    except SavesError as error:
        print(f"restore stopped: {error}")
        return 1
    same, text = compare_text(read_hashes(folder / "hashes.txt"), tree_hashes(live))
    log_row({"verb": "restore", "name": name, "same": same}, store)
    print(f"restored from {folder}: {text}")
    return 0 if same else 1


def tail(text: str) -> str:
    lines = text.strip().splitlines()
    return lines[-1][:120] if lines else ""


def stack_down() -> str:
    """stackctl down --force --json's last line, or its refusal whole (exit 2: another session holds the stack claim,
    stack_claim.down_refusal), which arm_before passes on as it is."""
    try:
        out = subprocess.run([sys.executable, "scripts/stackctl.py", "down", "--force", "--json"], cwd=ROOT,
                             capture_output=True, text=True, timeout=DOWN_TIMEOUT)
    except subprocess.TimeoutExpired:
        return f"stackctl down took over {DOWN_TIMEOUT} s"
    if out.returncode == 2 and (out.stderr or "").strip():
        return out.stderr.strip()
    return tail((out.stdout or "") + (out.stderr or "")) or f"exit {out.returncode}"


def server_game(run: Path = RUN) -> tuple[int | None, str | None]:
    """The server state file's gamePid and startedUtc (a run file: no handle on the game)."""
    try:
        state = json.loads((run / "server.state.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None, None
    return state.get("gamePid"), state.get("startedUtc")


def stack_boot(env_words: list[str] | None = None) -> tuple[bool, str]:
    """arms' own reboot: quick_loop.py run --boot (it stops the stack first, then boots both games), with the arm's
    boot env as its --server-env K=V and --client-env K=V words (env_words)."""
    try:
        out = subprocess.run([sys.executable, "scripts/quick_loop.py", "run", "--boot", *(env_words or [])], cwd=ROOT,
                             capture_output=True, text=True, timeout=BOOT_TIMEOUT)
    except subprocess.TimeoutExpired:
        return False, f"quick_loop run --boot took over {BOOT_TIMEOUT} s"
    return out.returncode == 0, tail((out.stdout or "") + (out.stderr or "")) or f"exit {out.returncode}"


def boot_env(server: list[str], client: list[str]) -> dict[str, dict[str, str]]:
    """arm --server-env and --client-env K=V items as {"server": {K: V}, "client": {...}}, a later item over an earlier
    one; a bad item stops here, before the games go down."""
    out: dict[str, dict[str, str]] = {}
    for side, items in (("server", server), ("client", client)):
        out[side] = {}
        for item in items:
            key, eq, value = item.partition("=")
            if not eq or not re.fullmatch(r"[A-Za-z_]\w*", key):
                raise SystemExit(f"--{side}-env wants NAME=VALUE, a NAME of letters, digits and _, not {item!r}")
            out[side][key] = value
    return out


def env_words(env: dict[str, dict[str, str]] | None) -> list[str]:
    """quick_loop.py run --boot's words for a boot env: --server-env K=V and --client-env K=V for each item."""
    return [word for side in ("server", "client") for key, value in ((env or {}).get(side) or {}).items()
            for word in (f"--{side}-env", f"{key}={value}")]


def running_arms(arms: Path = ARMS, parent: int | None = None) -> Path | None:
    """The arms folder whose runner started this hook: pass 3's runner.json names its pid."""
    parent = os.getppid() if parent is None else parent
    for lock in sorted(arms.glob("*/runner.json")) if arms.is_dir() else []:
        try:
            if json.loads(lock.read_text(encoding="utf-8")).get("pid") == parent:
                return lock.parent
        except (OSError, ValueError):
            pass
    return None


def arm_before(arm: str, repeat: int, base: str, uses: dict[str, Path], live: dict[str, Path], store: Path = STORE,
               down: Callable[[], str] = stack_down, boot: Callable[[list[str]], tuple[bool, str]] = stack_boot,
               games_up: Callable[[], bool] = ts3_running, game: Callable[[], tuple] = server_game,
               arms_folder: Callable[[], Path | None] = running_arms, env: dict[str, dict[str, str]] | None = None,
               builds: dict[str, str] | None = None,
               install: Callable[[str], tuple[bool, list[str]]] | None = None) -> int:
    """The boot hook (arms --boot-hook): boot this arm on its untouched save (see the module's notes), with its boot
    env (env: arm --server-env, --client-env) passed to the boot. builds (arm --build ARM=SLOT): install the arm's
    build slot after the down (install, build_slot.install) and boot --sequential."""
    name = arm.split(":", 1)[0]  # a capped catcher arm runs as NAME:bare
    want = name if name in uses else "base"
    source = uses[want] if want in uses else store / base / "server-Saves" / BOOT_SAVE
    skip = frozenset({STOCK_MARKER}) if want in uses else frozenset()
    row = {"verb": "boot", "arm": arm, "repeat": repeat, "want": want, "source": str(source)}
    words = env_words(env)
    if words:
        row["boot_env"] = env

    def refuse(reason: str, touched: bool = True) -> int:
        row.update(action="refused", why=reason, down=down() if touched else reason)
        folder = arms_folder()
        if folder is not None:
            (folder / "stop").write_text(time.strftime("%Y-%m-%d %H:%M:%S") + f" saves.py: {reason}\n", encoding="utf-8")
            row["stopped"] = folder.name
        log_row(row, store)
        print(f"saves: {arm} #{repeat} wants {want}: {reason}; " + ("both games left down" if touched else "no game touched")
              + (f", and {folder.name} stops before its next item" if folder is not None else ""))
        return 1

    slot = os.environ.get("SIMSONLINE_ARMS_HOOK", "")
    if slot in ("before", "after"):
        return refuse(f"run as arms --{slot}: give it as --boot-hook, which boots before the arm's --set and process "
                      "snapshot (a before hook's boot loses both)")
    if not (store / base / "hashes.txt").exists():
        return refuse(f"no backup {store / base} (saves.py backup {base} first)")
    if not hashes(source, skip):
        return refuse(f"{source} has no save files")
    build = (builds or {}).get(name)
    if builds and build is None:
        return refuse(f"no --build for arm {name}: every arm of a build A/B names its slot")
    row["down"] = down()
    if row["down"].startswith("stackctl down refused"):  # stack_claim.DOWN_REFUSED: another session's claim
        return refuse(row["down"], touched=False)
    if games_up():
        return refuse("a TS3W.exe still runs after stackctl down")
    if build is not None:
        ok, lines = (install or default_install)(build)
        row["build"] = {"slot": build, "ok": ok, "lines": lines[:6]}
        if not ok:
            return refuse(f"build slot {build} was not installed: " + " | ".join(lines[:3]))
        words = words + ["--sequential"]  # an old slot's client has no wait for the server's listener
    try:
        files = put_boot_save(source, live, skip)
    except SavesError as error:
        return refuse(f"the save was not put in: {error}")
    row["save"] = digest_of(files)
    before = game()
    ok, text = boot(words)
    row.update(boot=text, game=game())
    if not ok:
        return refuse(f"the boot failed: {text}")
    if row["game"][0] is None or row["game"] == before:
        return refuse(f"the boot named no new server game in server.state.json ({text})")
    row["action"] = "booted"
    log_row(row, store)
    print(f"saves: {arm} #{repeat}: booted on {want} ({row['save']}, {len(files)} file(s)), server TS3 {row['game'][0]}"
          + (f", build slot {build}" if build is not None else "") + (f", with {' '.join(words)}" if words else ""))
    return 0


def default_install(slot: str) -> tuple[bool, list[str]]:
    import build_slot  # scripts/build_slot.py (it imports this module)

    return build_slot.install(slot, build_slot.live_layout())


def arm_after(arm: str, repeat: int, store: Path = STORE, game: Callable[[], tuple] = server_game) -> int:
    """The after hook: whether the arm ran on the boot its boot hook made (nothing changes). Rows from before
    --boot-hook have the verb "before"."""
    made = next((r for r in reversed(read_rows(store)) if r.get("verb") in ("boot", "before") and r.get("arm") == arm
                 and r.get("repeat") == repeat), None)
    now = list(game())
    if made is None or made.get("action") != "booted":
        verdict = "no boot from the boot hook" + (f" ({made['why']})" if made else "")
    elif list(made["game"]) == now:
        verdict = f"on the boot hook's boot ({made['want']}, {made['save']})"
    else:
        verdict = f"on another boot than the boot hook's (server TS3 {made['game'][0]} then, {now[0]} now)"
    log_row({"verb": "after", "arm": arm, "repeat": repeat, "game": now, "ran": verdict}, store)
    print(f"saves: after {arm} #{repeat}: {verdict}")
    return 0


def listing(store: Path = STORE) -> int:
    names = sorted(p.name for p in store.iterdir() if (p / "hashes.txt").exists()) if store.is_dir() else []
    print(f"{len(names)} backup(s) in {store}: " + (", ".join(names) or "none"))
    for row in read_rows(store)[-8:]:
        what = f"{row['arm']} #{row['repeat']}" if "arm" in row else row.get("name") or row.get("source") or ""
        print(f"  {time.strftime('%m-%d %H:%M:%S', time.localtime(row['t']))} {row['verb']} {what}: "
              + (row.get("ran") or row.get("action") or ("same" if row.get("same") else row.get("save", "differs")))
              + (f" ({row['why']})" if row.get("why") else ""))
    return 0


def parse_uses(items: list[str]) -> dict[str, Path]:
    uses = {}
    for item in items:
        arm, sep, path = item.partition("=")
        if not sep or not arm or not path:
            raise SystemExit(f"--use wants ARM=FOLDER, not {item!r}")
        uses[arm] = Path(path)
    return uses


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    for verb in ("backup", "verify", "restore"):
        sub.add_parser(verb).add_argument("name")
    sub.add_parser("swap").add_argument("source", type=Path)
    sub.add_parser("list")
    p = sub.add_parser("arm", help="the arms --boot-hook")
    p.add_argument("arm")
    p.add_argument("repeat", type=int)
    p.add_argument("--base", required=True, help="the backup whose boot save every arm not in --use boots on")
    p.add_argument("--use", action="append", default=[], metavar="ARM=FOLDER", help="this arm boots on this save")
    p.add_argument("--server-env", action="append", default=[], metavar="K=V",
                   help="the server's boot env for quick_loop run --boot (repeatable; arms' {server_env} gives the arm's)")
    p.add_argument("--client-env", action="append", default=[], metavar="K=V",
                   help="the client's boot env, as --server-env (arms' {client_env})")
    p.add_argument("--build", action="append", default=[], metavar="ARM=SLOT",
                   help="install this arm's build slot after the down (build_slot.py) and boot --sequential (repeatable)")
    p = sub.add_parser("arm-after", help="the arms --after hook")
    p.add_argument("arm")
    p.add_argument("repeat", type=int)
    a = parser.parse_args(argv)
    if a.command == "list":
        return listing()
    if a.command == "arm-after":
        return arm_after(a.arm, a.repeat)
    if a.command == "arm":
        builds = None
        if a.build:
            import build_slot

            builds = build_slot.parse_builds(a.build)
        return arm_before(a.arm, a.repeat, a.base, parse_uses(a.use), live_saves(),
                          env=boot_env(a.server_env, a.client_env), builds=builds)
    live = live_saves()
    if a.command == "backup":
        return backup(a.name, live)
    if a.command == "verify":
        return verify(a.name, live)
    if a.command == "restore":
        return restore(a.name, live)
    return swap(a.source, live)


if __name__ == "__main__":
    sys.exit(main())
