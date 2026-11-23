"""Quick live loop for presentation work: one boot, a few scripted steps, a compact readout (minutes, not half an hour).

    python scripts/quick_loop.py fork-install cold-test-092     # once per series: the presenter fork on server + boxes
    python scripts/quick_loop.py run --mod --boot --step pick:UserPickProbeLoveseat --step wait:server:Jennifer:SittingPosture:60 \\
        --step snap --step diag
    python scripts/quick_loop.py run --step client:SACS_RECORDER presenter-idle off --step compare:90:off   # same boot
    python scripts/quick_loop.py fork-restore cold-test-092     # once at the end of the series
    python scripts/quick_loop.py fork-cycle --boot              # a fork change: overlay, build, swap attempts, boot
    python scripts/quick_loop.py run --boot --sequential        # the client only once the server is in the world
    python scripts/quick_loop.py run --mod --boot --clients 2   # two players: player 2's game after client 1's claim
    python scripts/quick_loop.py boot-times                     # each boot's phases, per game and mode
    python scripts/quick_loop.py active-household               # the server's family: live, and in its boot save

active-household (also printed after every run --boot / fork-cycle --boot): the server's selected Sim and its
household, read in the game (household_read.py), and the household its boot save was on when it was saved (the save's
Meta.data, read offline), with a WARNING when either is not the Martinezes. Every scenario assumes the Martinezes:
twice on 2026-10-22 the server came up on the Goths after a checkpoint save while Gunther was selected.
Exit 0 on the Martinezes, 1 on another family, 2 when neither could be read.

run options:
    --mod             build the mod and copy it into every profile (needs --boot to take effect)
    --boot            stack down, boot the server, wait until it is in the world, boot the client, wait for its claim
    --parallel        with --boot (the default since 2026-10-18): launch the client as soon as the server's TS3 is
                      up. The client loads to its menu meanwhile and asks for the world once the server's listener is
                      bound, which is after the server's session has started (SimsOnline.Client waits while the listener
                      refuses; built first if its build predates that). About 100 s against 145 s sequential (boot-times)
    --sequential      with --boot: the client only once the server is in the world (the boot before 2026-10-18; also
                      what a client that loads its own save gets). Each boot's phases go to .omo/run/boot-times.jsonl
                      either way (boot-times), with the machine's load over the boot (machine_load.py: system CPU %,
                      free memory, the top 5 processes by CPU) and, for a boot that failed, why; SIMSONLINE_BOOT_TAG=x
                      records the mode as e.g. sequential+x
    --clients 1|2     with --boot: 2 boots player 2's game too (stackctl.py boot-client2 in Client3Box on Pablo, with the
                      client environment client 1 gets) once client 1 has claimed, waits for its claim (client2.log) and
                      dismisses its startup notice; needs are made static after it. Default 2 when the run compares
                      player 2's game (SIMSONLINE_COMPARE_CLIENT=2: scenarios.py --client 2 and its arms' reboots), else 1
    --full           print every line of a server:/client:/pick: step's reply (default: the last line, cut at 240)
    --server-env K=V  extra server controller environment (repeatable); defaults: server ring path, route stream
    --client-env K=V  extra client controller environment (repeatable); defaults: client ring path, world from server
    --step STEP       in order (repeatable):
        pick:<probe>                      bridgectl probe scratch/<probe>.cs on the client (its selected Sim acts)
        wait:<server|client|client1|client2|client3>:<sim>:<regex>:<seconds>   poll PROBE_PRESENTATION until that Sim's row
                                                  matches (client: the compared client game; client1/client2: that one)
        until:<server|client|client1|client2|client3>:<sim>:<regex>:<seconds>  the same, and a timeout fails the step (a
                                                  scenario's check); {server:N} in a client's regex is the server
                                                  row's field N at that poll (until:client1:Pablo:^(?:[^|]*[|]){2}{server:2}[|]:30:
                                                  the copy is on the lot the server's Sim is on)
        sleep:<seconds>
        server:<command ...> / client:<command ...>      one bridge command (e.g. client:SACS_RECORDER presenter-idle off)
        snap[:label]                      each household Sim on both sides: posture, interaction, machines, latest state
        compare:<seconds>[:label]         presentation_compare on the client; check scores and anim_state per Sim
        diag                              client replay counters, skip sites, local state calls, presenter report
        act:<sim_act arguments>           scripts/sim_act.py on the server, arguments space-separated
                                          (act:Jennifer inv:PhoneSmart BrowseTheWeb --clear --hold; act:--release all)
        rejoin[:1|2|3][:paused]           restart only the client (its TS3 too) against the running server: a late join.
                                          paused: every game is paused meanwhile through the session (simctl speed
                                          pause, then normal after the claim: the scene holds still for the ~95 s the
                                          client takes, and the joiner is not left paused). Under scenarios.py --client 2
                                          (SIMSONLINE_COMPARE_CLIENT) it is player 2's game that rejoins; client 1 stays
                                          up. rejoin:2 (rejoin:2:paused) names player 2's game in any run: a two-player
                                          scenario's late join. Player 2 joins on the Sim it was on; rejoin:2:as=Iliana
                                          (rejoin:2:paused:as=Iliana) on that Sim instead, a player of another family
        rejoin:warm                       the same late join in seconds: only the client's controller restarts, attached
                                          to its running TS3; the mod drops what it presented and the history rebuilds it
                                          (client 1 only)
        capture:<Sim>[:pairs[:interval]]  both cameras on that Sim, then server|client frames (scripts/ts3_view.py)
        sims[:<sim_compare arguments>]    server vs client side by side (scripts/sim_compare.py; sims:Pablo --props)
        motion[:<motion_watch arguments>] teleports, slides, jitter, restarts on both games (scripts/motion_watch.py;
                                          motion:--seconds 30 --capture)
        cpick:<1|2|3>:<client_pick arguments>  that player's pick, made the way a click makes it (scripts/client_pick.py;
                                          cpick:2:id:1153934919 Sit, cpick:2:sim:Jennifer social:Chat, cpick:1:--cancel 0)
        hud:<1|2|3>:<seconds>:<regex>       that player's HUD queue (client_pick --hud) matches within the seconds
                                          (hud:2:10:actor=Pablo.*hudQueue=1); the regex may hold colons
        notices:mark                      each client's notice ring position: notice:/nonotice: read what came after
        notice:<1|2|3>:<seconds>:<regex>    that player was shown a matching notice (the mod's NOTICES ring) in the seconds
        nonotice:<1|2|3>:<regex>            that player was shown no matching notice since the mark (nonotice:1:.)
        menudiff:<1|2|3>:<actor>:<target>   that player's pie menu for the actor on the target shows nothing the server
                                          refuses (scripts/menu_diff.py; menudiff:2:Pablo:type:Bookshelf); a different
                                          actor or target on the two sides fails it too; a last :only=<text> counts
                                          only the entries whose menu text contains it (...:type:Bookshelf:only=Read);
                                          a last :exact also fails on an entry missing on the client or client only
        probe:<server|1|2|3>:<file>       a probe (public static string Run()) on that game, e.g. a scenario's setup
                                          (probe:server:scratch/ShelfBooksProbe.cs); a reply without ok=true fails it
        premise:<1|2|3>:borrowed-idle:<Sim> release a mapped idle driver; leave recovery to the stream
        objectdiff:<1|2|3>[:<options>]      that player's game made no object of its own near the Sims and shows a copy of
                                          every server object there (scripts/object_diff.py options, space separated:
                                          objectdiff:2:--types Newspaper --radius 40 --wait-new Newspaper:300)
Sims are named Jennifer and Pablo (household object ids below); --sims overrides with name=id pairs; any other first
name (wait:server:Iliana:...) is found on the server once per run (household_read.sim_id).

Machine-specific paths (TS3, profiles, Sandboxie boxes, the checkout holding the fork corpus) are at the top; each can be
overridden by an environment variable. Run from the simscope worktree root.
"""
from __future__ import annotations

class _MirrorProcFallback:
    GAME_STATE = {"server": "server.state.json", "client": "client.state.json", "client2": "client2.state.json", "client3": "client3.state.json"}

    @staticmethod
    def parse_utc(text):
        from datetime import datetime
        if not text:
            return None
        try:
            return datetime.fromisoformat(str(text).replace("Z", "+00:00")).timestamp()
        except ValueError:
            return None

    @staticmethod
    def attached(state):
        return state.get("mode") == "attach" or any(
            isinstance(h, dict) and h.get("phase") == "host-starting" and h.get("detail") == "attach"
            for h in (state.get("history") or ())
        )

    @classmethod
    def game_refusal(cls, pid, started, identity=None):
        info = identity(pid) if identity else None
        if info is None:
            return "not running"
        image = info[0].replace("\\", "/").rsplit("/", 1)[-1]
        if image.lower() != "ts3w.exe":
            return f"{image}, not TS3W.exe"
        if started is not None and info[1] < started - 5:
            return "created before its controller started (a reused pid)"
        return None

    @classmethod
    def pick_game(cls, side, state, file_pid, identity=None):
        started = None if cls.attached(state) else cls.parse_utc(state.get("startedUtc"))
        refused = []
        for pid, source in ((state.get("gamePid"), f"{cls.GAME_STATE[side]} gamePid"), (file_pid, f"{side}-ts3.pid")):
            if not isinstance(pid, int) or pid <= 0:
                continue
            why = cls.game_refusal(pid, started, identity)
            if why:
                refused.append(f"{source} {pid}: {why}")
            else:
                return {"pid": pid, "source": source, "refused": refused}
        return {"pid": None, "source": None, "refused": refused} if refused else None

    @classmethod
    def live_games(cls, run_dir=None, identity=None):
        import json as _json
        from pathlib import Path as _Path
        run_dir = _Path(run_dir) if run_dir else (_Path(__file__).resolve().parent.parent / ".omo" / "run")
        found = {}
        for side, name in cls.GAME_STATE.items():
            try:
                state = _json.loads((run_dir / name).read_text(encoding="utf-8"))
            except (OSError, ValueError):
                state = {}
            try:
                file_pid = int((run_dir / f"{side}-ts3.pid").read_text(encoding="utf-8").strip())
            except (OSError, ValueError):
                file_pid = None
            game = cls.pick_game(side, state, file_pid, identity)
            if game is not None:
                found[side] = game
        return found

    @staticmethod
    def alive(pid):
        import ctypes
        import os as _os
        import sys as _sys
        if not pid or pid <= 0:
            return False
        if _sys.platform == "win32":
            h = ctypes.windll.kernel32.OpenProcess(0x1000 | 0x00100000, False, int(pid))
            if not h:
                return False
            try:
                return ctypes.windll.kernel32.WaitForSingleObject(h, 0) != 0
            finally:
                ctypes.windll.kernel32.CloseHandle(h)
        try:
            _os.kill(int(pid), 0)
            return True
        except OSError:
            return False

    @classmethod
    def game_pids(cls, run_dir=None):
        return {side: g["pid"] for side, g in cls.live_games(run_dir).items() if g.get("pid") and side in ("server", "client")}

    @staticmethod
    def debugger_kinds():
        return {}

    @staticmethod
    def wait_for_dumps(*args, **kwargs):
        return {}

    @staticmethod
    def writing_dumps(*args, **kwargs):
        return []

    @staticmethod
    def crash_reports(*args, **kwargs):
        return []

    @staticmethod
    def describe_report(*args, **kwargs):
        return ""


import argparse
import calendar
import contextlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import cpu_budget  # scripts/cpu_budget.py
import machine_load  # scripts/machine_load.py
import stack_claim  # scripts/stack_claim.py
import ts3_paths  # scripts/ts3_paths.py
import fork_census
from local_endpoints import CLIENT_BRIDGECTL, SERVER_BRIDGECTL

ROOT = Path(__file__).resolve().parent.parent
_default_main = (
    ROOT
    if (ROOT / ".omo/cache/game-decompile").exists()
    else (Path.home() / "Workspace/Games/SimsOnline")
)
MAIN = Path(os.environ.get("SIMSONLINE_MAIN_CHECKOUT", str(_default_main)))
# SIMSONLINE_TS3_PATH only when it names a file that exists (a shell can carry a Wine path from another host).
_TS3 = ts3_paths.find_ts3w()
TS3 = str(_TS3) if _TS3 else ""
USER_HOME = Path.home()
REAL = Path(os.environ.get("SIMSONLINE_REAL_PROFILE", str(USER_HOME / "Documents/Electronic Arts/The Sims 3")))
ONEDRIVE_MOD = Path(os.environ.get("SIMSONLINE_ONEDRIVE_MOD",
                                   str(USER_HOME / "OneDrive/Documents/Electronic Arts/The Sims 3/Mods/Packages/SimsOnlineServerHostMod.package")))
CURRENT_USER = os.environ.get("USERNAME", USER_HOME.name)
# The box's profile sits under the Wine prefix's own user (drive_c/users/<unix user> of the prefix it was copied from,
# not this Windows user): {user} is found per box, like install_nointro.py does.
BOX_ROOT = os.environ.get("SIMSONLINE_BOX_PROFILE",
                          f"C:/Sandbox/{CURRENT_USER}/{{box}}/drive/C/Sims/.sims3-client/drive_c/users/{{user}}/Documents/Electronic Arts/The Sims 3")
# Each player's Sandboxie box (player 3:, made from Client3Box). BOXES is every box with a client profile, the
# ones a mod or fork install reaches: Client4Box joins once it exists.
PLAYER_BOXES = {"1": "Client2Box", "2": "Client3Box", "3": "Client4Box"}
BOXES = ["Client2Box", "Client3Box"]
CLIENT_BOX = "Client2Box"
SERVER, CLIENT = SERVER_BRIDGECTL, CLIENT_BRIDGECTL
SIMS = {"Jennifer": "18031287978496800304", "Pablo": "18031287978496800272"}
FORK_PACKAGE = "SimsOnlineGameplay02Cold.package"
SERVER_ENV = {"SIMSONLINE_SIMLINK_SERVER_RING": "1", "SIMSONLINE_ROUTE_STREAM": "1",
              "SIMSONLINE_CHECKPOINT_SAVE": "Option4BookChain-checkpoint.sims3"}
SERVER_UNSET = ["SIMSONLINE_CLIENT_NATIVE_MODES", "SIMSONLINE_PRESENTATION_STREAM"]
CLIENT_ENV = {"SIMSONLINE_SIMLINK_CLIENT_RING": "1", "SIMSONLINE_WORLD_FROM_SERVER": "1"}
CLIENT_UNSET = ["SIMSONLINE_AUTHORITY_GATE", "SIMSONLINE_MIRROR_INTERACTIONS", "SIMSONLINE_SIMLINK_SHADOW", "SIMSONLINE_SIMLINK_SERVER_RING"]
FIELDS = {"posture": 6, "interaction": 9, "posture_machine": 10, "interaction_machine": 11, "sacs": 14}

START = time.monotonic()


def say(text: str) -> None:
    print(f"[{time.monotonic() - START:6.1f}s] {text}", flush=True)


def box(name: str) -> Path:
    if "{user}" not in BOX_ROOT:
        return Path(BOX_ROOT.format(box=name))
    users = Path(BOX_ROOT.format(box=name, user="*").split("*")[0])
    found = sorted(users.glob("*/Documents/Electronic Arts/The Sims 3")) if users.is_dir() else []
    user = found[0].relative_to(users).parts[0] if found else CURRENT_USER
    return Path(BOX_ROOT.format(box=name, user=user))


if (box("Client4Box") / "Mods").is_dir():
    BOXES.append("Client4Box")


def run(cmd: list[str], env: dict[str, str] | None = None, cwd: Path = ROOT, timeout: int = 1200, budget: bool = False) -> str:
    """budget: a build, run at below-normal priority with job caps while a live stack claim is held (cpu_budget.py); never
    for stackctl or anything that starts a game, which would pass the lower priority on to it."""
    extra = {"env": env}
    if budget:
        cmd, extra = cpu_budget.apply(cmd, extra)
    out = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, timeout=timeout, check=False, **extra)
    return (out.stdout or "") + (out.stderr or "")


class DownRefused(SystemExit):
    """stackctl down refused (exit 2): another session holds the stack claim; its words, as stackctl gave them."""


def stack_down() -> str:
    """stackctl down --force --json (through run): its receipt (the last line, up to 80 characters). A refusal
    (another session holds the stack claim: stackctl exits 2 with stack_claim.down_refusal's line) stops quick_loop
    before any boot or install step, with stackctl's own words."""
    text = run([sys.executable, "scripts/stackctl.py", "down", "--force", "--json"]).strip()
    refusal = next((line for line in text.splitlines() if line.startswith(stack_claim.DOWN_REFUSED)), None)
    if refusal is not None:
        raise DownRefused(refusal)
    return text.splitlines()[-1][:80] if text else "no output"


def bridge(endpoint: str, *args: str, timeout: int = 30) -> list[str]:
    text = run([sys.executable, "scripts/bridgectl.py", "--timeout", str(timeout), "--endpoint", endpoint, *args], timeout=timeout + 30)
    return [line for line in text.splitlines() if line and not line.startswith("accepted=")]


SIMCTL_TIMEOUT = 30  # simctl's own deadline, under the 60 s its process gets (as scenarios.py's reset resume)


def simctl_speed(speed: str) -> str:
    """simctl speed <speed> through the session: the controller's speed, which every game follows and a later joiner is
    sent at its join (a bridge SET_GAME_SPEED moves the server's game only). Its last line; "simctl: no answer" when
    its process does not end in 60 s."""
    try:
        text = run([sys.executable, "scripts/simctl.py", "--timeout", str(SIMCTL_TIMEOUT), "speed", speed], timeout=60).strip()
    except subprocess.TimeoutExpired:
        return "simctl: no answer"
    return text.splitlines()[-1][:100] if text else "no output"


def probe_rows(endpoint: str, sims: dict[str, str]) -> dict[str, list[str]]:
    rows = {}
    for line in bridge(endpoint, "PROBE_PRESENTATION", "-", ",".join(sims.values())):
        f = line.split("|")
        for name, sim_id in sims.items():
            if f[0] == sim_id and len(f) >= 15:
                rows[name] = f
    return rows


def stack_env(extra: dict[str, str], unset: list[str]) -> dict[str, str]:
    env = dict(os.environ)
    for key in unset:
        env.pop(key, None)
    env.update(extra)
    env["SIMSONLINE_TS3_PATH"] = TS3
    return env


def mod_targets() -> list[Path]:
    """Every profile's copy of the host mod (the server's, OneDrive's, each box's): --mod and build_slot.py install."""
    return ([ONEDRIVE_MOD, REAL / "Mods/Packages/SimsOnlineServerHostMod.package"]
            + [box(b) / "Mods/Packages/SimsOnlineServerHostMod.package" for b in BOXES])


def install_mod() -> None:
    pointer = slot_pointer()
    if pointer:
        raise SystemExit(f"--mod: build slot {pointer.get('slot')} is installed (build_slot.py): a mod build would "
                         "replace its mod; build_slot.py release first")
    say("mod: build")
    text = run(["pwsh", "-NoProfile", "-File", "scripts/make-mod.ps1", "-SkipInstall"], budget=True)
    if "make-mod OK" not in text:
        raise SystemExit("make-mod failed:\n" + text[-2000:])
    # An API desktop .NET has and the game's runtime lacks compiles, then hangs the mod in-game: stop here instead.
    check = subprocess.run([sys.executable, "scripts/check_mod_runtime.py"], cwd=ROOT, capture_output=True, text=True, check=False)
    if check.returncode != 0:
        raise SystemExit("mod runtime check failed (scripts/check_mod_runtime.py):\n" + check.stdout[-2000:])
    package = ROOT / "SimsOnlineServerHostMod/bin/Release/SimsOnlineServerHostMod.package"
    for target in mod_targets():
        shutil.copyfile(package, target)
    say("mod: installed in every profile")


RUN = ROOT / ".omo" / "run"
BOOT_TIMES = RUN / "boot-times.jsonl"
# build_slot.py install: the controllers run that slot's binaries; where stackctl reads it (its RUN_DIR)
SLOT_POINTER = Path(os.environ.get("SIMSONLINE_RUN_DIR") or RUN) / "build-slot.json"
CLIENT_WAIT_MARKER = "world_request_waiting"  # a string literal of the client's wait for the server's listener


def utc_now() -> str:
    """Wall-clock UTC in the controllers' state-file format, so quick_loop's marks line up with their phases."""
    now = time.time()
    return time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(now)) + f".{int(now * 1000) % 1000:03d}Z"


def parse_utc(text: str | None) -> float | None:
    """Epoch seconds of a state file's UTC stamp (2026-10-17T21:36:34.800Z); None when absent or malformed."""
    m = re.fullmatch(r"(\d{4})-(\d\d)-(\d\d)T(\d\d):(\d\d):(\d\d)(?:\.(\d+))?Z?", text or "")
    if not m:
        return None
    whole = calendar.timegm(tuple(int(g) for g in m.groups()[:6]) + (0, 0, 0))
    return whole + (float("0." + m.group(7)) if m.group(7) else 0.0)


def read_state(path: Path) -> dict | None:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def phase_times(state: dict | None) -> dict[str, float]:
    """When each phase was first entered (epoch seconds), from a controller state file's history."""
    times: dict[str, float] = {}
    for entry in (state or {}).get("history") or []:
        t = parse_utc(entry.get("utc"))
        if t is not None and entry.get("phase") not in times:
            times[entry["phase"]] = t
    return times


# Boundaries (also printed by boot-times): launch = the controller's "starting"; menu = "host-connected" (the mod
# connects from the main menu); server world = "session-started" (its save loaded); client world = "world-received"
# (the server's world arrived: in a parallel boot this includes the wait for the server's listener); ready = the
# controller's "ready"; claimed = quick_loop saw the client's sim_claimed.
BOOT_COLUMNS = [
    ("srv launch>menu", "server", "starting", "server", "host-connected"),
    ("srv menu>world", "server", "host-connected", "server", "session-started"),
    ("cli after srv", "server", "starting", "client", "starting"),
    ("cli launch>menu", "client", "starting", "client", "host-connected"),
    ("cli menu>world", "client", "host-connected", "client", "world-received"),
    ("cli load", "client", "world-received", "client", "ready"),
    ("cli menu>ready", "client", "host-connected", "client", "ready"),
    ("total", "quick_loop", "server_launch", "quick_loop", "client_claimed"),
]


def boot_spans(entry: dict) -> dict[str, float | None]:
    """One boot's spans in seconds (BOOT_COLUMNS), None where a phase is missing (a client built before world-received)."""
    times = {"server": phase_times(entry.get("server")), "client": phase_times(entry.get("client")),
             "quick_loop": {k: parse_utc(v) for k, v in (entry.get("quick_loop") or {}).items()}}
    spans: dict[str, float | None] = {}
    for name, side_a, a, side_b, b in BOOT_COLUMNS:
        start, end = times[side_a].get(a), times[side_b].get(b)
        spans[name] = round(end - start, 1) if start is not None and end is not None else None
    return spans


def boot_table(entries: list[dict]) -> list[str]:
    """A row per boot and a mean per mode."""
    def cell(value: float | None) -> str:
        return f"{value:>16.1f}" if value is not None else f"{'-':>16}"

    names = [c[0] for c in BOOT_COLUMNS]
    lines = [f"{'boot (UTC)':<19} {'mode':<14}" + "".join(f"{n:>16}" for n in names)]
    per_mode: dict[str, list[dict]] = {}
    for entry in entries:
        spans = boot_spans(entry)
        if not entry.get("failed"):  # a failed boot's spans stop where it failed: out of the mean
            per_mode.setdefault(entry.get("mode", "?"), []).append(spans)
        lines.append(f"{(entry.get('when') or '?')[:19]:<19} {entry.get('mode', '?'):<14}" + "".join(cell(spans[n]) for n in names))
        if entry.get("failed"):
            lines.append(f"{'':<19} failed: {entry['failed']}")
        if entry.get("load"):
            lines.append(f"{'':<19} load: {machine_load.text(entry['load'])}")
    lines.append("")
    for mode, rows in per_mode.items():
        means = []
        for n in names:
            values = [r[n] for r in rows if r[n] is not None]
            means.append(round(sum(values) / len(values), 1) if values else None)
        lines.append(f"{'mean of ' + str(len(rows)):<19} {mode:<14}" + "".join(cell(v) for v in means))
    return lines


def record_boot(mode: str, marks: dict[str, str], path: Path | None = None, server_env: dict[str, str] | None = None,
                client_env: dict[str, str] | None = None, load: dict | None = None, failed: str | None = None) -> dict:
    """Append this boot (quick_loop's marks and both controllers' state files) to boot-times.jsonl, with the extra env
    it booted each game with (server_env, client_env: run --boot's --server-env and --client-env, {} for none; not the
    defaults, SERVER_ENV and CLIENT_ENV, nor the environment both games inherit), so a game's env can be told later
    from its gamePid in that row's state. load is the machine's load over the boot (machine_load.Sampler.stop: CPU %
    mean and max, free memory, the top 5 processes by CPU with names and pids; G15), failed why a boot that
    raised stopped (its marks stop there)."""
    path = path or BOOT_TIMES
    entry = {"mode": mode, "when": marks.get("server_launch"), "server_env": dict(server_env or {}),
             "client_env": dict(client_env or {}), "quick_loop": marks,
             "server": read_state(RUN / "server.state.json"), "client": read_state(RUN / "client.state.json")}
    if failed:
        entry["failed"] = failed
    if load is not None:
        entry["load"] = load
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8") as f:
        f.write(json.dumps(entry) + "\n")
    spans = boot_spans(entry)
    say("boot times: " + ", ".join(f"{k} {v:.0f}s" for k, v in spans.items() if v is not None))
    if load is not None:
        say("boot load: " + machine_load.text(load))
    return entry


def failure_text(exc: BaseException) -> str:
    """Why a boot stopped, for its boot-times row."""
    if isinstance(exc, KeyboardInterrupt):
        return "interrupted"
    if isinstance(exc, SystemExit):
        text = exc.code if isinstance(exc.code, str) else f"exit {exc.code}"
    else:
        text = f"{type(exc).__name__}: {exc}"
    return text if len(text) <= 300 else text[:297] + "..."


def boot_times(last: int, path: Path | None = None) -> None:
    path = path or BOOT_TIMES
    entries = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()] if path.exists() else []
    if not entries:
        print(f"no boots recorded yet ({path}): quick_loop run --boot [--parallel] records each")
        return
    print("Seconds per boot. launch = controller starting; menu = host-connected (the mod connects at the main menu);\n"
          "server world = session-started; client world = world-received (a parallel boot's wait for the server is in\n"
          "it); load = world-received to ready; total = server launch to the client's claim.\n")
    print("\n".join(boot_table(entries[-last:])))


def pid_alive(pid: int) -> bool:
    if os.name != "nt":
        try:
            os.kill(pid, 0)
            return True
        except OSError:
            return False
    import ctypes
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    handle = kernel32.OpenProcess(0x1000, False, int(pid))  # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        return ctypes.get_last_error() == 5  # access denied: it exists
    code = ctypes.c_ulong()
    ok = kernel32.GetExitCodeProcess(handle, ctypes.byref(code))
    kernel32.CloseHandle(handle)
    return bool(ok) and code.value == 259  # STILL_ACTIVE


def slot_pointer() -> dict | None:
    """The build slot the controllers run (build_slot.py install), or None (dotnet run --project, this checkout's)."""
    return read_state(SLOT_POINTER)


def client_dll() -> Path | None:
    """The client controller's build: an installed build slot's, else this checkout's newest Debug build."""
    pointer = slot_pointer()
    if pointer and pointer.get("client"):
        return Path(pointer["client"]).with_name("SimsOnline.Client.dll")
    found = sorted((ROOT / "SimsOnline.Client/bin/Debug").glob("*/SimsOnline.Client.dll"), key=lambda p: p.stat().st_mtime)
    return found[-1] if found else None


def client_waits_for_server(dll: Path | None) -> bool:
    """Whether a SimsOnline.Client build retries while the server's listener refuses (commit a64d473)."""
    return bool(dll and dll.exists() and CLIENT_WAIT_MARKER.encode("utf-16-le") in dll.read_bytes())


def ensure_client_waits() -> None:
    """A parallel boot needs a client that waits for the server's listener; build it (stack down) when it predates that."""
    if client_waits_for_server(client_dll()):
        return
    pointer = slot_pointer()
    if pointer:  # a build would replace nothing the slot runs, and an old slot's client cannot wait: never build
        raise SystemExit(f"client: build slot {pointer.get('slot')}'s client predates the wait for the server's "
                         "listener (a64d473), so it cannot boot in parallel: boot --sequential (saves.py arm --build "
                         "does); quick_loop does not build over an installed slot")
    say("client: its build predates the wait for the server's listener; dotnet build SimsOnline.Client (Debug)")
    text = run(["dotnet", "build", "SimsOnline.Client/SimsOnline.Client.csproj", "-c", "Debug", "--nologo", "-v", "q"], timeout=900, budget=True)
    if not client_waits_for_server(client_dll()):
        raise SystemExit("client build failed or still lacks the wait:\n" + text[-1500:])
    say("client: built")


def server_launched(since: float, state: dict | None) -> int | None:
    """The server's TS3 pid once the controller launched after `since` has adopted a live TS3, else None. The client's
    TS3 must not start before that: a Sandboxie launch is adopted as 'a new TS3W with the same path'."""
    started = parse_utc((state or {}).get("startedUtc"))
    pid = (state or {}).get("gamePid")
    if started is None or started < since - 2 or not pid:
        return None
    return int(pid) if pid_alive(int(pid)) else None


def controller_failed(log: str) -> str | None:
    path = ROOT / ".omo/run" / log
    if not path.exists():
        return None
    text = path.read_text(encoding="utf-8", errors="replace")
    for marker in ("Could not find TS3W", "Unhandled exception", "Fatal"):
        if marker in text:
            return next(line for line in text.splitlines() if marker in line)
    return None


# The boot save is the cold-test copy of Sunset Valley that a fork install makes (deploy-g02-cold.ps1) and fork-restore
# moves away. Booted with no fork attempt installed (stock gameplay, an A/B), the server had no such save and the mod's
# load did nothing until the controller's 600 s LoadSave timeout (2026-10-17). A stock boot gets its own copy, marked so
# fork-install moves it aside first (the install refuses an existing save).
BOOT_SAVE = "Option4BookChain.sims3"
STOCK_SAVE_MARKER = "simsonline-stock-boot.txt"


def ensure_boot_save() -> None:
    save = REAL / "Saves" / BOOT_SAVE
    if save.exists():
        return
    # TS3 replaces a save by deleting the folder, then copying (internal documentation, "Commit and
    # backup rotation"), so a save cut off there is missing while its siblings stay. Never put a fresh town in its place.
    kept = save.with_name(save.name + ".lastgood")
    if kept.is_dir():
        say(f"boot save: {BOOT_SAVE} is missing; the server controller restores it from {kept.name} before loading")
        return
    left = [p.name for p in (save.with_name(save.name + ".backup"), save.with_name(save.name + ".bad")) if p.is_dir()]
    if left:
        raise SystemExit(f"no boot save: {save} is missing but {' and '.join(left)} exist (a save TS3 lost part-way); "
                         "put one back by hand or with scripts/saves.py restore <name>: a fresh Sunset Valley would replace "
                         "the server's world")
    source = REAL / "Saves/Sunset Valley.sims3"
    if not source.is_dir():
        raise SystemExit(f"no boot save: {save} is absent and so is {source}")
    shutil.copytree(source, save)
    (save / STOCK_SAVE_MARKER).write_text(f"copied from {source.name} for a boot without a fork attempt\n", encoding="utf-8")
    say(f"boot save: {BOOT_SAVE} copied from {source.name} (no fork attempt installed)")


def set_aside_stock_save() -> None:
    save = REAL / "Saves" / BOOT_SAVE
    if (save / STOCK_SAVE_MARKER).exists():
        aside = ROOT / ".omo/run/stock-boot-saves" / time.strftime("%Y%m%d-%H%M%S")
        aside.parent.mkdir(parents=True, exist_ok=True)
        move_aside(save, aside)
        say(f"stock boot save moved aside to {aside}")


FAMILY = "Martinez"  # the household every scenario assumes the server is on (scenarios.select_server_sim)


def meta_strings(payload: bytes, count: int = 3) -> list[str]:
    """A save's Meta.data record: a version word, then length-prefixed (UTF-16 code units) strings: the world, the
    household's name, its description (what the load menu shows). [] when the record does not read that way."""
    at, found = 4, []
    for _ in range(count):
        if at + 4 > len(payload):
            return []
        size = int.from_bytes(payload[at:at + 4], "little")
        at += 4
        if size > 4096 or at + 2 * size > len(payload):
            return []
        found.append(payload[at:at + 2 * size].decode("utf-16-le", errors="replace"))
        at += 2 * size
    return found


def save_household(save: Path | None = None) -> tuple[str | None, str]:
    """The household the server's boot save was on when it was saved (its Meta.data), or (None, why not)."""
    meta = (save or REAL / "Saves" / BOOT_SAVE) / "Meta.data"
    try:
        import dbpf_grep
        found = dbpf_grep.records(meta.read_bytes())
        record = next(found, None) if found is not None else None
    except Exception as exc:  # noqa: BLE001 - an unreadable save is reported, never fatal
        return None, f"{meta.name}: {type(exc).__name__}: {exc}"[:160]
    strings = meta_strings(record.payload) if record else []
    if len(strings) < 2:
        return None, f"{meta} holds no world and household strings"
    return strings[1], ""


def household_check(endpoint: str = SERVER, save: Path | None = None) -> int:
    """The server's family, live (its selected Sim's household, household_read) and in its boot save (Meta.data), with
    a WARNING when either is not the Martinezes. 0 on the Martinezes, 1 on another family, 2 when neither read."""
    import household_read
    try:
        reading = household_read.read(endpoint, [])
        active, why = reading["active"], reading["error"] or ""
    except (Exception, SystemExit) as exc:  # noqa: BLE001 - the game may not answer; the save still reads
        active, why = None, f"{type(exc).__name__}: {exc}"[:160]
    saved, save_why = save_household(save)
    live = (f"{active['household']} (selected {active['name']}, id {active['object']})" if active
            else "no Sim selected" if not why else f"unread ({why})")
    say(f"server household: live {live}; boot save {saved or f'unread ({save_why})'}")
    if not active and why and not saved:
        return 2
    off = ([f"live on {active['household'] if active else 'no household (no Sim selected)'}"]
           if (active or not why) and not (active and active["household"].startswith(FAMILY)) else [])
    off += [f"its boot save on {saved}"] if saved and not saved.startswith(FAMILY) else []
    if not off:
        return 0
    say(f"WARNING: the server is {' and '.join(off)}, not the {FAMILY}es: every scenario assumes the {FAMILY}es (a "
        "checkpoint save with another family's Sim selected leaves the save on that family, ). Under your stack "
        f"claim select Jennifer: python scripts/bridgectl.py --endpoint {SERVER} SET_ACTIVE_SIM {SIMS['Jennifer']}; "
        "the next save then names the Martinezes again (scenarios.py's reset does this select too)")
    return 1


SERVER_BOOT = ["scripts/stackctl.py", "boot-server", "--save", BOOT_SAVE, "--skip-mod-sync", "--timeout", "900", "--json"]
BRIDGE_TRACE = False  # run/fork-cycle --bridge-trace (or SIMSONLINE_BRIDGE_TRACE=1): stackctl's bridge record, .omo/run/server-bridge.log


def trace_flag() -> list[str]:
    """stackctl's --bridge-trace, appended after the verb's own arguments: its shared flags are parents of each
    subparser, so they parse only after the verb. Nothing unless asked for."""
    return ["--bridge-trace"] if BRIDGE_TRACE or os.environ.get("SIMSONLINE_BRIDGE_TRACE") == "1" else []


def server_boot_args() -> list[str]:
    return [*SERVER_BOOT, *trace_flag()]


def hide_client_windows() -> int:
    """Hide only this worktree's tracked client games, under the caller's live claim (Windows)."""
    claim = stack_claim.current()
    if not claim or claim[0] != stack_claim.own_session():
        raise SystemExit("--hide-clients requires this session's live stack claim")
    if os.name != "nt":
        raise SystemExit("--hide-clients requires Windows")
    import ctypes
    import ntpath
    from ctypes import wintypes as w
    import stackctl

    live = set(stackctl.get_ts3_pids())
    server = stackctl.get_live_pid_from_file(stackctl.SERVER_TS3_PID_FILE)
    clients = {}
    for player in stackctl.CLIENT_PLAYERS:
        pin = stackctl.client_files(player)["pid"]
        pid = stackctl.get_live_pid_from_file(pin)
        if not pid or pid == server or pid not in live:
            continue
        identity = stackctl.client_launch_identity(pin)
        if not identity or identity["pid"] != pid:
            say(f"--hide-clients refused client {player} pid {pid}: missing or mismatched launch identity")
            continue
        clients[pid] = identity
    if not clients:
        raise SystemExit("--hide-clients found no tracked live client games")
    u = ctypes.WinDLL("user32")
    callback = ctypes.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
    u.EnumWindows.argtypes = [callback, w.LPARAM]
    u.GetWindowThreadProcessId.argtypes = [w.HWND, ctypes.POINTER(w.DWORD)]
    u.GetWindow.restype, u.GetWindow.argtypes = w.HWND, [w.HWND, w.UINT]
    u.ShowWindowAsync.argtypes = [w.HWND, ctypes.c_int]
    hidden = []

    def visit(hwnd, _param):
        pid = w.DWORD()
        u.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value in clients and not u.GetWindow(hwnd, 4) and u.IsWindowVisible(hwnd):
            expected = clients[pid.value]
            current = stackctl.game_identity(pid.value)
            if (not current or current[1] != expected["started"]
                    or ntpath.normcase(current[0]) != ntpath.normcase(expected["image"])):
                say(f"--hide-clients refused pid {pid.value}: process start time or image differs from launch")
                return True
            if not u.ShowWindowAsync(hwnd, 0):  # SW_HIDE; never a title-based FindWindow
                raise_error.append(pid.value)
            else:
                hidden.append(pid.value)
        return True

    u.IsWindowVisible.argtypes = [w.HWND]
    raise_error = []
    if not u.EnumWindows(callback(visit), 0) or raise_error:
        raise SystemExit("--hide-clients could not hide every selected client window")
    say(f"client windows hidden: {len(hidden)} (pids {','.join(map(str, sorted(set(hidden))))})")
    return len(hidden)


def client_boot_args(restart: bool) -> list[str]:
    return ["scripts/stackctl.py", "boot-client-restart" if restart else "boot-client", "--sandboxie-box", CLIENT_BOX,
            "--save", "W2ClientCheck.sims3", "--skip-mod-sync", "--timeout", "900", "--json", *trace_flag()]


def server_in_world() -> bool:
    return any("inWorld=True" in line for line in bridge(SERVER, "DIAG_STATE", timeout=10))


def boot_mode(parallel: bool | None, client_env: dict[str, str]) -> bool:
    """Whether to boot both games at once: asked for (True), refused (False), or the default (None): at once unless the
    client loads its own save, whose world request does not wait for the server's session."""
    waits = {**CLIENT_ENV, **client_env}.get("SIMSONLINE_WORLD_FROM_SERVER") == "1"
    if parallel and not waits:
        raise SystemExit("--parallel needs the client to take its world from the server (SIMSONLINE_WORLD_FROM_SERVER=1): "
                         "only that request waits for the server's session")
    if parallel is None and not waits:
        say("the client loads its own save: sequential boot")
    return waits if parallel is None else parallel


def mode_name(parallel: bool) -> str:
    """The boot-times mode: parallel or sequential, plus SIMSONLINE_BOOT_TAG (boot_profile: cdb at launch)."""
    tag = os.environ.get("SIMSONLINE_BOOT_TAG", "").strip()
    return ("parallel" if parallel else "sequential") + (f"+{tag}" if tag else "")


def gc_watch_start() -> str:
    """The boot's GC reader (gc_watch.py start, ): the server's collections, marks and frame words go to the
    run dir's gc-watch.jsonl from before its TS3 starts, tagged with the booting session's claim, so a crash in any
    boot has its GC timing; stackctl down stops it. SIMSONLINE_GC_WATCH=0 (run --no-gc-watch) turns it off."""
    lines = run([sys.executable, "scripts/gc_watch.py", "start"], timeout=60).strip().splitlines()
    return lines[-1] if lines else "gc watch: start said nothing"


def boot(server_env: dict[str, str], client_env: dict[str, str], parallel: bool | None = None) -> None:
    if not TS3:
        raise SystemExit("TS3W.exe not found: set SIMSONLINE_TS3_PATH to an existing file")
    parallel = boot_mode(parallel, client_env)
    # The machine's load from here to the claim or the failure goes into the boot's row ( G15): system CPU,
    # free memory, the top processes by CPU. It reads the system's counters and process list, opening no process.
    load = machine_load.Sampler().start()
    marks: dict[str, str] = {}
    try:
        claimed = stack_claim.warning(stack_claim.own_session())
        if claimed:
            say(claimed)
        say("stack down: " + stack_down())
        ensure_boot_save()
        say(gc_watch_start())
        keep_last_boot_output()
        if parallel:
            ensure_client_waits()
            boot_parallel(server_env, client_env, marks)
        else:
            marks["server_launch"] = utc_now()
            run([sys.executable, *server_boot_args()], env=stack_env({**SERVER_ENV, **server_env}, SERVER_UNSET))
            marks["server_ready"] = utc_now()
            for _ in range(120):
                failed = controller_failed("server.log.err")
                if failed:
                    raise SystemExit("server controller failed: " + failed)
                if server_in_world():
                    break
                time.sleep(3)
            else:
                raise SystemExit("server never reached the world")
            marks["server_in_world"] = utc_now()
            say("server in the world")
            boot_client(client_env, restart=False, marks=marks)
    except BaseException as exc:
        stopped = load.stop()
        if marks.get("server_launch"):  # a boot that launched a game gets its row, with why it stopped
            try:
                record_boot(mode_name(parallel), marks, server_env=server_env, client_env=client_env, load=stopped,
                            failed=failure_text(exc))
            except Exception as record_error:  # noqa: BLE001 - the boot's own failure is the one to raise
                say(f"boot times: the failed boot's row was not written: {record_error}")
        raise
    record_boot(mode_name(parallel), marks, server_env=server_env, client_env=client_env, load=load.stop())
    dismiss_startup_dialogs()


def keep_last_boot_output() -> None:
    """The last parallel boot's stackctl output (quick_loop-boot-{server,client}.out) becomes .prev.out before this boot
    writes its own, so stackctl's archive at the server's launch keeps it with that run's logs, as .out."""
    for side in ("server", "client"):
        last = RUN / f"quick_loop-boot-{side}.out"
        if last.exists():
            last.replace(RUN / f"quick_loop-boot-{side}.prev.out")


def boot_parallel(server_env: dict[str, str], client_env: dict[str, str], marks: dict[str, str] | None = None) -> dict[str, str]:
    """Both games boot at once: the client's TS3 starts once the server's controller has its TS3 (so neither adopts the
    other's), loads to its menu, and asks for the world once the server's listener is bound (after its session started).
    Any failure takes the whole stack down. A stackctl boot that exits 1 (its one readiness check at the end saw a
    listener not up yet, or its poll timed out) is not one: the sequential boot ignores that exit too and waits for the
    claim, which has its own bound. Returns quick_loop's marks (UTC), filled into `marks` when given, so a boot that
    fails still has the marks it reached."""
    marks = {} if marks is None else marks
    marks["server_launch"] = utc_now()
    launched = time.time()
    outputs = {side: (RUN / f"quick_loop-boot-{side}.out").open("w", encoding="utf-8") for side in ("server", "client")}
    procs: dict[str, subprocess.Popen] = {}
    lenient: set[str] = set()

    def check() -> None:
        for side, proc in procs.items():
            failed = controller_failed(f"{side}.log.err")
            if failed:
                raise SystemExit(f"{side} controller failed: " + failed)
            code = proc.poll()
            if code == 1 and side not in lenient:
                lenient.add(side)  # live check 2026-10-18: listeners_not_ready at the client's ready, claimed 5 s later
                say(f"stackctl boot-{side} exited 1 (a listener not ready at its last check): waiting for the claim")
            if code not in (None, 0, 1):
                outputs[side].flush()
                tail = (RUN / f"quick_loop-boot-{side}.out").read_text(encoding="utf-8", errors="replace").strip().splitlines()[-3:]
                raise SystemExit(f"stackctl boot-{side} exited {code}: " + " | ".join(tail))

    try:
        procs["server"] = subprocess.Popen([sys.executable, *server_boot_args()], cwd=ROOT, stdout=outputs["server"], stderr=subprocess.STDOUT,
                                           env=stack_env({**SERVER_ENV, **server_env}, SERVER_UNSET))
        deadline = time.time() + 180
        while (pid := server_launched(launched, read_state(RUN / "server.state.json"))) is None:
            check()
            if time.time() > deadline:
                raise SystemExit("parallel boot: the server's controller never reported a live TS3 in 180 s")
            time.sleep(0.5)
        marks["client_launch"] = utc_now()
        say(f"server TS3 {pid} up: client launched alongside (it waits at its menu for the server's session)")
        client_since = time.time()
        procs["client"] = subprocess.Popen([sys.executable, *client_boot_args(False)], cwd=ROOT, stdout=outputs["client"],
                                           stderr=subprocess.STDOUT, env=stack_env({**CLIENT_ENV, **client_env}, CLIENT_UNSET))

        def watch() -> None:
            check()
            if "server_in_world" not in marks and server_in_world():
                marks["server_in_world"] = utc_now()
                say("server in the world")

        wait_claim(client_since, watch, tries=300)
        marks["client_claimed"] = utc_now()
        say("client claimed")
        return marks
    except BaseException as ex:
        if isinstance(ex, DownRefused):
            raise  # nothing was booted: no down to clean up after
        say("parallel boot failed: stack down")
        run([sys.executable, "scripts/stackctl.py", "down", "--force", "--json"])
        raise
    finally:
        for f in outputs.values():
            f.close()


def wait_claim(since: float, check, tries: int = 200, log_name: str = "client.log") -> None:
    """Poll every 3 s until the client's log (client.log, or client2.log for player 2; written after `since`) shows its
    sim_claimed; check() raises on failure."""
    log = RUN / log_name
    for _ in range(tries):
        check()
        if log.exists() and log.stat().st_mtime >= since and '"$type":"sim_claimed"' in log.read_text(encoding="utf-8", errors="replace"):
            return
        time.sleep(3)
    raise SystemExit("client never claimed")


def boot_client(client_env: dict[str, str], restart: bool, marks: dict[str, str] | None = None) -> None:
    """Boot the client (restart: its controller and TS3 go down first, the server keeps running: a late join) and wait
    for its claim."""
    claimed = stack_claim.warning(stack_claim.own_session()) if restart else None
    if claimed:
        say(claimed)
    claimed_before = time.time()
    if marks is not None:
        marks["client_launch"] = utc_now()
    run([sys.executable, *client_boot_args(restart)], env=stack_env({**CLIENT_ENV, **client_env}, CLIENT_UNSET))

    def check() -> None:
        failed = controller_failed("client.log.err")
        if failed:
            raise SystemExit("client controller failed: " + failed)

    wait_claim(claimed_before, check)
    if marks is not None:
        marks["client_claimed"] = utc_now()
    log = RUN / "client.log"
    gate = [line for line in log.read_text(encoding="utf-8", errors="replace").splitlines() if "authority_gate_published" in line]
    state = re.search(r'"state":"([^"]*)"', gate[-1]) if gate else None
    say("client claimed" + (f" ({state.group(1)})" if state else ""))
    if marks is None:
        dismiss_startup_dialogs()  # a first boot dismisses after recording its times


def rejoin_player(rest: str) -> tuple[str, str]:
    """rejoin[:1|2|3][:paused|warm] -> (the player whose game restarts, mode). Without a client the run's client
    restarts (player 2's under scenarios.py --client 2); a multi-player scenario names it (rejoin:2: player 2 late-joins,
    the others run on)."""
    import compare_client
    who, _, mode = rest.partition(":") if rest[:1] in compare_client.PLAYERS else ("", "", rest)
    return who or ("2" if player2() else compare_client.player_of(compare_client.endpoint()) or "1"), mode


def rejoin_target(rest: str) -> tuple[bool, str]:
    """rejoin[:1|2][:paused|warm] -> (player 2's game?, mode) (rejoin_player for player 2)."""
    player, mode = rejoin_player(rest)
    return player == "2", mode


def rejoin_as(rest: str) -> tuple[str, str]:
    """A rejoin step's as=<FirstName> part, taken out: '2:paused:as=Iliana' -> ('2:paused', 'Iliana'); '' without one."""
    parts = rest.split(":") if rest else []
    sims = [p[3:] for p in parts if p.startswith("as=")]
    return ":".join(p for p in parts if not p.startswith("as=")), (sims[-1] if sims else "")


def sim_ref(sims: dict[str, str], name: str) -> str:
    """A Sim's server object id: the run's --sims and the Martinezes first, else found by first name on the server
    (household_read.sim_id, the Langeraks' ids are not fixed), and kept in sims for the rest of the run."""
    if name not in sims:
        import household_read
        sims[name] = household_read.sim_id(name)
    return sims[name]


def client2_sim(player: str = "2") -> str:
    """The Sim player N's game is on now (its selected Sim), so a late join without as= keeps that player's family;
    Pablo for player 2 when the game does not answer (a later player has no default: SystemExit)."""
    import compare_client
    import household_read
    try:
        active = household_read.read(compare_client.client_n(player), [])["active"]
    except (OSError, ValueError, KeyError, subprocess.SubprocessError):
        active = None
    if active:
        return active["name"]
    if player == "2":
        return "Pablo"
    raise SystemExit(f"player {player}'s game did not say its Sim: rejoin:{player}:as=<Sim>")


def player2() -> bool:
    """Whether the run's client game is player 2's (scenarios.py --client 2 sets SIMSONLINE_COMPARE_CLIENT)."""
    import compare_client
    return compare_client.endpoint() == compare_client.client2()


def boot_clients(clients: int | None) -> int:
    """How many client games run --boot boots: 1, 2 or 3 as asked (--clients), else the compared player's number when
    the run compares a later player's game (scenarios.py --client 2, SIMSONLINE_COMPARE_CLIENT), whose health check
    needs it up, else 1."""
    import compare_client
    if clients is None:
        return int(compare_client.player_of(compare_client.endpoint()) or "1")
    if str(clients) not in compare_client.PLAYERS:
        raise SystemExit(f"--clients {clients}: {', '.join(compare_client.PLAYERS)}")
    return clients


def boot_client2(client_env: dict[str, str], restart: bool, sim: str = "", player: str = "2") -> None:
    """Player 2's game (or player N's: player 3 in Client4Box, ) on its own Sim (Pablo for player 2 unless sim
    names another, Client3Box), as `stackctl.py boot-client2` (boot-client3) does by hand, with the client environment
    client 1 gets (CLIENT_ENV and the run's --client-env), and wait for its claim (client2.log, client3.log). restart: a
    late join, its controller and TS3 go down first while the server and the other clients keep running, and without a
    sim it joins on the Sim it was on (client2_sim); else a later client of a fresh boot (run --boot --clients 2 or 3),
    which starts once the clients before it have claimed. Its startup notice is dismissed after the claim."""
    claimed = stack_claim.warning(stack_claim.own_session()) if restart else None
    if claimed:
        say(claimed)
    if not sim and not restart and player != "2":
        raise SystemExit(f"client {player}: name its Sim (run --player{player} <FirstName>)")
    sim = sim or (client2_sim(player) if restart else "Pablo")
    claim = sim_ref(dict(SIMS), sim)  # the id before the game goes down: a name the server has no Sim for stops here
    role, box_name = f"client{player}", PLAYER_BOXES[player]
    if restart:
        run([sys.executable, "scripts/stackctl.py", f"{role}-down"])
        say(f"client {player}: joining again on {sim}")
    else:
        say(f"client {player}: booting in {box_name} on {sim}")
    claimed_before = time.time()
    run([sys.executable, "scripts/stackctl.py", f"boot-{role}", f"--sandboxie-box{player}", box_name,
         f"--claim-sim{player}", claim, "--skip-mod-sync", "--timeout", "900", "--json", *trace_flag()],
        env=stack_env({**CLIENT_ENV, **client_env}, CLIENT_UNSET))

    def check() -> None:
        failed = controller_failed(f"{role}.log.err")
        if failed:
            raise SystemExit(f"{role} controller failed: " + failed)

    wait_claim(claimed_before, check, log_name=f"{role}.log")
    say(f"client {player} claimed in {time.time() - claimed_before:.0f}s")
    dismiss_startup_dialogs(player)


def boot_client2_restart(sim: str = "", player: str = "2") -> None:
    """A later player's late join: its controller and TS3 go down, the server and the other clients keep running, and it
    boots again on its own Sim (the one it was on, or sim; Client3Box for player 2, Client4Box for player 3)."""
    boot_client2({}, restart=True, sim=sim, player=player)


def client1_controller_pid() -> int | None:
    """Client 1's controller from its own state file: never every SimsOnline.Client.exe, which takes client 2's too."""
    try:
        return int(json.loads((ROOT / ".omo/run/client.state.json").read_text(encoding="utf-8"))["pid"])
    except (OSError, ValueError, KeyError, TypeError):
        return None


def rejoin_warm() -> None:
    """A late join without a TS3 boot: the client controller goes down and comes back attached to its running TS3
    (SIMSONLINE_CLIENT_ATTACH_PID). The server sends this player the late-join history again; the mod drops what it
    presented for the last session first (ResetPresentation)."""
    try:
        import crash_catch  # the client's running TS3W by the readers' rule (state gamePid, then client-ts3.pid)
    except ImportError:
        crash_catch = _MirrorProcFallback

    game = crash_catch.live_games().get("client") or {}
    if not game.get("pid"):
        raise SystemExit("rejoin:warm: no running client TS3 (" + ("; ".join(game.get("refused") or [])
                         or "no client.state.json or client-ts3.pid") + "): boot the client once first")
    pid = str(game["pid"])
    controller = client1_controller_pid()
    if not controller:
        raise SystemExit("rejoin:warm: no controller pid in client.state.json")
    started = time.time()
    # By pid: taskkill /IM stopped client 2's controller too (2026-10-21).
    run(["taskkill", "/F", "/PID", str(controller)])
    import socket
    host, port = CLIENT.rsplit(":", 1)
    for _ in range(40):  # the controller's bridge listener goes with it
        try:
            socket.create_connection((host, int(port)), timeout=0.5).close()
        except OSError:
            break
        time.sleep(0.5)
    claimed_before = time.time()
    env = stack_env({**CLIENT_ENV, "SIMSONLINE_CLIENT_ATTACH_PID": pid}, CLIENT_UNSET)
    run([sys.executable, "scripts/stackctl.py", "client", "--sandboxie-box", CLIENT_BOX, "--save", "W2ClientCheck.sims3", "--skip-mod-sync",
         "--host-timeout", "120", "--json", *trace_flag()], env=env)
    log = ROOT / ".omo/run/client.log"
    for _ in range(80):
        if log.exists() and log.stat().st_mtime >= claimed_before and '"$type":"sim_claimed"' in log.read_text(encoding="utf-8", errors="replace"):
            break
        time.sleep(1)
    else:
        raise SystemExit("rejoin:warm: the client never claimed again")
    say(f"rejoin:warm: client controller back on TS3 {pid} and claimed in {time.time() - started:.0f}s")


@contextlib.contextmanager
def paired_client(client: str | None):
    """Inside the block the comparison tools and ts3_view pair the server with that client game ("1" or "2";
    SIMSONLINE_COMPARE_CLIENT); after it the run's own choice is back, so player2() and later steps keep it. None: the
    run's choice throughout."""
    import compare_client
    before = os.environ.get(compare_client.ENV)
    if client is not None:
        compare_client.use(client)
    try:
        yield
    finally:
        if before is None:
            os.environ.pop(compare_client.ENV, None)
        else:
            os.environ[compare_client.ENV] = before


def dismiss_startup_dialogs(client: str | None = "1") -> None:
    """The game's native "Mod Scripts Found" notice can stay up over a client's view, and can appear after the claim:
    scripts/ts3_view.py looks for it in the server's TS3 window and that client's ("1" or "2"; None: the run's
    SIMSONLINE_COMPARE_CLIENT) and posts Enter until none shows (no focus change, nothing else on the desktop
    touched)."""
    if os.name != "nt":
        return
    try:
        import ts3_view
        time.sleep(3)
        with paired_client(client):
            say(ts3_view.dismiss(tries=30, wait=2.0))  # a notice up while the game still loads ignores Enter for a while
        return
    except Exception as ex:  # fall back to one blind Enter
        say(f"startup notice check failed ({ex.__class__.__name__}); posting Enter once")
    import ctypes
    import ctypes.wintypes as wt
    user32 = ctypes.windll.user32
    windows = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def collect(hwnd, _):
        n = user32.GetWindowTextLengthW(hwnd)
        if n:
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, buf, n + 1)
            if "The Sims" in buf.value and user32.IsWindowVisible(hwnd):
                windows.append(hwnd)
        return True

    user32.EnumWindows(collect, 0)
    for hwnd in windows:
        user32.PostMessageW(hwnd, 0x100, 0x0D, 0x001C0001)  # WM_KEYDOWN Enter
        time.sleep(0.1)
        user32.PostMessageW(hwnd, 0x101, 0x0D, 0xC01C0001)  # WM_KEYUP
    say(f"startup dialogs: Enter posted to {len(windows)} TS3 window(s)")


def step_snap(label: str, sims: dict[str, str]) -> None:
    server, client = probe_rows(SERVER, sims), probe_rows(CLIENT, sims)
    say(f"snap {label}".rstrip())
    for name in sims:
        s, c = server.get(name), client.get(name)
        if s is None or c is None:
            print(f"    {name}: server={'-' if s is None else 'ok'} client={'-' if c is None else 'ok'}")
            continue
        cells = []
        for field, index in FIELDS.items():
            mark = "=" if s[index] == c[index] else "!"
            cells.append(f"{field}{mark}{s[index] if s[index] == c[index] else s[index] + '/' + c[index]}")
        print(f"    {name}: " + " ".join(cells))


def step_compare(seconds: int, label: str) -> None:
    # A folder per run: presentation_compare appends to an existing samples file.
    out = ROOT / f".omo/run/presentation-quick-{label + '-' if label else ''}{int(time.time())}"
    run([sys.executable, "scripts/presentation_compare.py", "--client", CLIENT, "--duration", str(seconds), "--interval", "3",
         "--report-every", "1000", "--out", str(out)], timeout=seconds + 300)
    report = (out / "report.md").read_text(encoding="utf-8", errors="replace") if (out / "report.md").exists() else ""
    scores = {m.group(1): m.group(2) for m in re.finditer(r"^\| (posture|anim_posture|anim_state|held|moving|position) \| ([\d.]+) \|", report, re.MULTILINE)}
    per = {}
    samples = out / "samples.jsonl"
    if samples.exists():
        for raw in samples.read_text(encoding="utf-8").splitlines():
            if not raw.strip():
                continue
            for sim in json.loads(raw)["sims"].values():
                value = sim["result"].get("anim_state")
                key = str((sim.get("server") or {}).get("description"))
                ok, bad = per.get(key, (0, 0))
                per[key] = (ok + (value is True), bad + (value is False))
    say(f"compare {label} {seconds}s: " + " ".join(f"{k}={v}" for k, v in scores.items()))
    print("    anim_state per Sim (description id): " + " ".join(f"{k}:{ok}/{ok + bad}" for k, (ok, bad) in sorted(per.items()) if ok + bad))


def step_diag() -> None:
    replay = next((line for line in bridge(CLIENT, "SACS_REPLAY", "00") if "totalApplied" in line), "")
    keep = [p for p in replay.split(";") if p.split("=")[0] in ("requestFailures", "pending", "totalApplied", "totalSkipped", "drivers", "presenter",
                                                                "joinResets", "join")]
    say("diag")
    for part in keep:
        print("    " + part[:300])
    skips = bridge(CLIENT, "SACS_RECORDER", "replay-skips")
    if skips:
        print("    " + skips[0][:300])
    for line in bridge(CLIENT, "SACS_RECORDER", "local-contexts")[:12]:
        print("    local " + line[:200])


def player_step(rest: str, timed: bool) -> tuple[str, float, str]:
    """A player step's '<client>:<seconds>:<regex>' (timed) or '<client>:<regex>' as (client, seconds, regex). The regex
    is everything after the fixed fields, so it may hold colons."""
    if timed:
        client, _, more = rest.partition(":")
        seconds, _, pattern = more.partition(":")
    else:
        client, _, pattern = rest.partition(":")
        seconds = "0"
    if client not in PLAYER_BOXES or not pattern:
        raise SystemExit(f"player step {rest!r}: give <1|2|3>:{'<seconds>:' if timed else ''}<regex>")
    try:
        return client, float(seconds), pattern
    except ValueError:
        raise SystemExit(f"player step {rest!r}: seconds {seconds!r} is not a number") from None


NOTICE_MARKS = "notice-marks.json"  # in RUN: each client's notice seq at notices:mark


def parse_notices(lines: list[str]) -> tuple[int, list[tuple[int, int, str]]]:
    """A NOTICES reply (the mod's notice ring): 'seq=<last>;lost=<n>;count=<k>' then one 'seq|ms|text' line per notice.
    Returns (last seq, [(seq, ms, text)]); (-1, []) when the reply has no seq (a game without the verb)."""
    last = -1
    notices = []
    for line in lines:
        head = re.match(r"\s*seq=(\d+);", line)
        if head:
            last = int(head.group(1))
            continue
        row = re.match(r"\s*(\d+)\|(\d+)\|(.*)$", line)
        if row:
            notices.append((int(row.group(1)), int(row.group(2)), row.group(3)))
    return last, notices


def client_notices(client: str, since: int) -> tuple[int, list[tuple[int, int, str]]]:
    import compare_client
    return parse_notices(bridge(compare_client.resolve(client), "NOTICES", str(since)))


def notice_mark(client: str) -> int:
    try:
        return int(json.loads((RUN / NOTICE_MARKS).read_text(encoding="utf-8")).get(client, 0))
    except (OSError, ValueError, TypeError):
        return 0


SERVER_FIELD = re.compile(r"\{server:(\d+)\}")


def with_server_fields(pattern: str, name: str, sims: dict[str, str]) -> str | None:
    """pattern with each {server:N} replaced by the server's PROBE_PRESENTATION field N for that Sim now (escaped), so a
    client check can match whatever the server shows (its lot, its interaction) without the scenario knowing it; None
    while the server has no such row or field."""
    if not SERVER_FIELD.search(pattern):
        return pattern
    row = probe_rows(SERVER, {name: sim_ref(sims, name)}).get(name)
    if not row:
        return None
    if any(int(n) >= len(row) for n in SERVER_FIELD.findall(pattern)):
        return None
    return SERVER_FIELD.sub(lambda m: re.escape(row[int(m.group(1))]), pattern)


def step_wait(rest: str, sims: dict[str, str], must: bool) -> None:
    """wait:<server|client|client1|client2|client3>:<sim>:<regex>:<seconds>: poll that Sim's PROBE_PRESENTATION row (its
    fields joined by '|') until it matches; a timeout is only reported. until: the same, and a timeout fails the step
    (a scenario's check). client is the compared client game (scenarios.py --client, SIMSONLINE_COMPARE_CLIENT),
    client1 and client2 that player's game. The regex may hold colons; {server:N} in it is the server row's field N
    at each poll (with_server_fields)."""
    import compare_client
    side, name, tail = rest.split(":", 2)
    pattern, _, seconds = tail.rpartition(":")
    if side == "server":
        endpoint = SERVER
    elif side == "client":
        endpoint = compare_client.endpoint()
    elif side in tuple("client" + p for p in compare_client.PLAYERS):
        endpoint = compare_client.resolve(side[len("client"):])
    else:
        raise SystemExit(f"{'until' if must else 'wait'}: no side {side!r}: give server, client, client1, client2 or "
                         "client3")
    deadline = time.monotonic() + float(seconds)
    row = None
    want = None
    while True:
        row = probe_rows(endpoint, {name: sim_ref(sims, name)}).get(name)
        want = with_server_fields(pattern, name, sims)
        matched = bool(row and want is not None and re.search(want, "|".join(row)))
        if matched or time.monotonic() >= deadline:
            break
        time.sleep(2)
    kind = "until" if must else "wait"
    shown = pattern if want in (None, pattern) else f"{pattern} = {want}"
    if must and not matched:
        raise SystemExit(f"until: {side} {name} never matched /{shown}/ in {seconds}s (row: {'|'.join(row or [])[:200]})")
    say(f"{kind} {side} {name} /{shown}/: " + ("matched" if matched else "TIMED OUT"))


def step_notices_mark() -> None:
    """Each client's notice seq now, so notice:/nonotice: read only the notices shown after it (a scenario's start)."""
    marks = {}
    for client in PLAYER_BOXES:
        last, _ = client_notices(client, 0)
        if last >= 0:
            marks[client] = last
    (RUN / NOTICE_MARKS).write_text(json.dumps(marks), encoding="utf-8")
    say("notices mark: " + (" ".join(f"client {k} seq {v}" for k, v in marks.items()) or "no client answered NOTICES"))


def step_notice(rest: str, expected: bool) -> None:
    """notice:<client>:<seconds>:<regex>: that player is shown a matching notice (since notices:mark) within the seconds.
    nonotice:<client>:<regex>: no notice shown since the mark matches."""
    client, seconds, pattern = player_step(rest, timed=expected)
    since = notice_mark(client)
    deadline = time.monotonic() + seconds
    while True:
        _, shown = client_notices(client, since)
        hit = next((text for _, _, text in shown if re.search(pattern, text)), None)
        if hit is not None or not expected or time.monotonic() >= deadline:
            break
        time.sleep(1)
    seen = "; ".join(text for _, _, text in shown)[:200] or "none"
    if expected and hit is None:
        raise SystemExit(f"notice: client {client} showed no notice /{pattern}/ in {seconds:.0f}s (shown: {seen})")
    if not expected and hit is not None:
        raise SystemExit(f"nonotice: client {client} showed {hit!r}")
    say(f"{'notice' if expected else 'nonotice'} client {client} /{pattern}/: " + (repr(hit) if hit else f"none (shown: {seen})"))


def step_hud(rest: str) -> None:
    """hud:<client>:<seconds>:<regex>: that player's HUD queue (client_pick --hud) matches within the seconds."""
    import client_pick
    client, seconds, pattern = player_step(rest, timed=True)
    deadline = time.monotonic() + seconds
    while True:
        _, line = client_pick.reply(["--client", client, "--hud"])
        if re.search(pattern, line) or time.monotonic() >= deadline:
            break
        time.sleep(1)
    if not re.search(pattern, line):
        raise SystemExit(f"hud: client {client} /{pattern}/ not matched in {seconds:.0f}s: {line[:240]}")
    say(f"hud client {client} /{pattern}/: {line[:200]}")


def step_cpick(rest: str) -> None:
    """cpick:<client>:<client_pick arguments>: that player's pick, the way a click makes it (scripts/client_pick.py)."""
    import client_pick
    import shlex
    client, _, args = rest.partition(":")
    if client not in PLAYER_BOXES:
        raise SystemExit(f"cpick {rest!r}: give cpick:<1|2|3>:<client_pick arguments>")
    code, line = client_pick.reply(["--client", client, *shlex.split(args)])
    say(f"cpick {rest}: {line[:240]}")
    if code != 0:
        raise SystemExit(f"cpick failed: {line[:240]}")


def step_probe(rest: str) -> None:
    """probe:<server|1|2|3>:<file>: one probe on that game (bridgectl probe); its reply must hold ok=true. A reply field
    var_<name>=<value> prints `var <name>=<value>`, which a scenario uses as {name} in its later steps."""
    import compare_client
    game, _, path = rest.partition(":")
    if game not in ("server", *compare_client.PLAYERS) or not path:
        raise SystemExit(f"probe {rest!r}: give probe:<server|1|2|3>:<file>")
    endpoint = SERVER if game == "server" else compare_client.resolve(game)
    lines = bridge(endpoint, "probe", path, timeout=90)
    # bridgectl prints the upload, then "loaded=...", then the probe's reply one "  key = value" field per line.
    loaded = next((i for i, line in enumerate(lines) if line.lstrip().startswith("loaded=")), -1)
    reply = ";".join(re.sub(r"\s*=\s*", "=", line.strip(), count=1) for line in lines[loaded + 1:] if line.strip())
    say(f"probe {game} {path}: {reply[:240]}")
    if not re.search(r"(?:^|;)ok=true(?:;|$)", reply, re.IGNORECASE):  # C#'s bool prints True
        raise SystemExit(f"probe failed: {reply[:240]}")
    # A reply field var_<name>=<value> is a scenario variable: scenarios.py puts it in later steps' {name}.
    for name, value in re.findall(r"(?:^|;)var_(\w+)=([^;]*)", reply):
        say(f"var {name}={value.strip()}")


def step_premise(rest: str) -> None:
    """premise:<1|2|3>:borrowed-idle:<Sim>: invalidate a mapped idle at ISacsProxy; leave recovery to the stream."""
    import compare_client
    import idle_repair_probe
    client, _, rest = rest.partition(":")
    kind, _, sim = rest.partition(":")
    if client not in compare_client.PLAYERS or kind != "borrowed-idle" or not sim:
        raise SystemExit("premise: give premise:<1|2|3>:borrowed-idle:<Sim>")
    code = idle_repair_probe.main([sim, "--client", compare_client.resolve(client), "--via-replay",
                                   "--no-request", "--invalidate-only"])
    if code:
        raise SystemExit(f"premise: borrowed idle was not invalidated (exit {code})")


def step_objectdiff(rest: str) -> None:
    """objectdiff:<1|2|3>[:<object_diff.py options, space separated>]: that player's game against the server's near the
    Sims (scripts/object_diff.py, ): fails when the client's own world made an object there, a server object has
    no client copy, or a copy the server shows is hidden. --wait-new Type:seconds first waits for the server to make
    one (a delivery)."""
    import object_diff
    client, _, options = rest.partition(":")
    if client not in PLAYER_BOXES:
        raise SystemExit(f"objectdiff {rest!r}: give objectdiff:<1|2|3>[:<options>]")
    code = object_diff.main(["--client", client, *options.split()])
    if code:
        raise SystemExit(f"objectdiff client {client}: exit {code} (3 differ, 4 no new server object, 1 a side did not answer)")


def step_menudiff(rest: str) -> None:
    """menudiff:<client>:<actor>:<target>[:only=<text>][:exact]: that player's pie menu for the actor on the target (a
    Sim's first name or type:<fragment>) shows no entry the server's test refuses (scripts/menu_diff.py, part A)
    and greys none as server-refused that the server's test passes (a stale menu-refusal grey); with only=, only the
    entries whose menu text contains <text> count; with :exact (last), an entry missing on the client or only on the
    client fails it too (the menus match)."""
    import menu_diff
    client, actor, target, only, exact = menu_diff.parse_step(rest)
    if client not in PLAYER_BOXES or not actor or not target:
        raise SystemExit(f"menudiff {rest!r}: give menudiff:<1|2|3>:<actor>:<target>[:only=<text>][:exact]")
    groups, notes = menu_diff.compare(actor, target, client)
    for note in notes:
        say("menudiff " + note.replace("\n", " ")[:240])
    if groups is None:
        raise SystemExit(f"menudiff: no listing for {actor} on {target}, client {client}")
    say(f"menudiff client {client} {actor} on {target}: " + ", ".join(f"{k}: {len(v)}" for k, v in groups.items()))
    warnings = [n for n in notes if n.startswith("warning:")]
    if warnings:
        raise SystemExit("menudiff: " + warnings[0][9:].strip()[:200])
    if only:
        groups = {k: [e for e in v if only.lower() in str(e).lower()] for k, v in groups.items()}
        say(f"menudiff only '{only}': " + (", ".join(f"{k}: {len(v)}" for k, v in groups.items() if v) or "on neither menu"))
    refused = groups["shown, server refuses"]
    if refused:
        raise SystemExit("menudiff: shown, server refuses: " + "; ".join(
            f"{t} '{n}' {r}" + (f" ({why})" if why else "") for (t, n), r, why in refused[:4]))
    # A grey the server no longer backs: the fork's menu-refusal hook still greys an entry the server's test passes.
    stale = [e for e in groups["missing on the client"] if e[1] == "client ServerRefused"]
    if stale:
        raise SystemExit("menudiff: greyed as refused, server passes: " + "; ".join(f"{t} '{n}'" for (t, n), _, _ in stale[:4]))
    differ = menu_diff.exact_failures(groups) if exact else []
    if differ:
        raise SystemExit(f"menudiff exact: {len(differ)} differ: " + "; ".join(differ[:4]))


def say_reply(head: str, lines: list[str], full: bool, cut: int = 240) -> None:
    """A step's bridge reply: its last line cut at `cut` characters, or (full) every line as it came."""
    if not full:
        say(f"{head}: " + (lines[-1][:cut] if lines else "(no reply)"))
        return
    say(f"{head}: " + ("" if lines else "(no reply)") + (f"{len(lines)} line(s)" if lines else ""))
    for line in lines:
        print("    " + line, flush=True)


def run_steps(steps: list[str], sims: dict[str, str], full: bool = False) -> None:
    for step in steps:
        kind, _, rest = step.partition(":")
        if kind == "pick":
            lines = bridge(CLIENT, "probe", f"scratch/{rest}.cs", timeout=60)
            if full:
                say_reply(f"pick {rest}", lines, True)
            else:
                say(f"pick {rest}: " + re.sub(r"\s+", " ", " ".join(lines))[:200])
        elif kind in ("wait", "until"):
            step_wait(rest, sims, must=kind == "until")
        elif kind == "sleep":
            time.sleep(float(rest))
            say(f"slept {rest}s")
        elif kind in ("server", "client"):
            say_reply(f"{kind} {rest}", bridge(SERVER if kind == "server" else CLIENT, *rest.split()), full)
        elif kind == "snap":
            step_snap(rest, sims)
        elif kind == "compare":
            seconds, _, label = rest.partition(":")
            step_compare(int(seconds), label)
        elif kind == "diag":
            step_diag()
        elif kind == "act":
            import sim_act  # scripts/ is on sys.path when quick_loop runs as a script
            say("act " + rest)
            code = sim_act.main(rest.split())
            if code == 1:
                raise SystemExit("act failed: " + rest)
        elif kind == "rejoin":
            rest, sim = rejoin_as(rest)
            player, mode = rejoin_player(rest)
            later = player != "1"
            if sim and not later:
                raise SystemExit("rejoin as=<Sim> is a later player's only: rejoin:2:as=" + sim)
            if mode == "warm":
                if later:
                    raise SystemExit(f"rejoin:warm is client 1's only: use rejoin for player {player}")
                rejoin_warm()
                continue
            paused = mode == "paused"
            if paused:
                say("rejoin: pause every game, simctl speed pause: " + simctl_speed("pause"))
            say((f"rejoin: client {player} down and up, server and the other clients running" if later else
                 "rejoin: client down and up, server running") + (" (paused)" if paused else ""))
            try:
                if later:
                    boot_client2_restart(sim, player=player)
                else:
                    boot_client({}, restart=True)
            finally:
                if paused:
                    time.sleep(5)  # the late-join history lands before the scene moves on
                    say("rejoin: resume every game, simctl speed normal: " + simctl_speed("normal"))
        elif kind == "capture":
            import ts3_view
            name, _, more = rest.partition(":")
            pairs, _, interval = more.partition(":")
            for line in ts3_view.focus(name):
                say("focus " + line)
            out = ROOT / ".omo" / "run" / "captures" / f"{time.strftime('%H%M%S')}-{name}.png"
            say("capture " + str(ts3_view.capture(int(pairs or 2), float(interval or 1.5), "centre", out, name)))
        elif kind == "sims":
            import sim_compare
            say("sims " + rest)
            sim_compare.main(rest.split())
        elif kind == "motion":
            import motion_watch
            say("motion " + rest)
            motion_watch.main(rest.split())
        elif kind == "cpick":
            step_cpick(rest)
        elif kind == "hud":
            step_hud(rest)
        elif kind == "notices" and rest == "mark":
            step_notices_mark()
        elif kind in ("notice", "nonotice"):
            step_notice(rest, expected=kind == "notice")
        elif kind == "menudiff":
            step_menudiff(rest)
        elif kind == "probe":
            step_probe(rest)
        elif kind == "premise":
            step_premise(rest)
        elif kind == "objectdiff":
            step_objectdiff(rest)
        else:
            raise SystemExit(f"unknown step: {step}")


def fork_install(attempt: str) -> None:
    say("stack down: " + stack_down())
    set_aside_stock_save()
    game_package = ["-GamePackage", str(Path(TS3).parent / "gameplay.package")] if TS3 else []
    for script, extra in (("prepare-g02-cold.ps1", ["-Attempt", attempt, "-OverridePackage", *game_package]),
                          ("deploy-g02-cold.ps1", ["-Action", "Install", "-Attempt", attempt])):
        text = run(["pwsh", "-NoProfile", "-File", f"scratch/{script}", *extra], cwd=MAIN)
        say(f"{script}: " + text.strip().splitlines()[-1][:160])
    receipt = MAIN / f".omo/cache/fork/{attempt}/deployment.json"
    if '"installed"' not in receipt.read_text(encoding="utf-8-sig"):
        raise SystemExit("fork install failed: " + str(receipt))
    for name in BOXES:
        shutil.copyfile(REAL / "Mods/Packages" / FORK_PACKAGE, box(name) / "Mods/Packages" / FORK_PACKAGE)
        cache = box(name) / "scriptCache.package"
        if cache.exists():
            cache.replace(box(name) / f"scriptCache.{attempt}-aside.package")
    say(f"fork {attempt} installed on the server profile and {', '.join(BOXES)}")


class ForkReceiptError(Exception):
    """A fork-install receipt that cannot be read (corrupt bytes, or gone between the listing and the read)."""


def installed_attempt() -> str | None:
    """The fork attempt installed now: the newest cold-test-* receipt whose status is installed."""
    try:
        receipts = sorted((MAIN / ".omo/cache/fork").glob("cold-test-*/deployment.json"), key=lambda r: r.stat().st_mtime, reverse=True)
    except OSError as error:
        raise ForkReceiptError(f"{MAIN / '.omo/cache/fork'}: {type(error).__name__}: {error}") from error
    for receipt in receipts:
        try:
            text = receipt.read_text(encoding="utf-8-sig")
        except (OSError, ValueError) as error:
            raise ForkReceiptError(f"{receipt}: {type(error).__name__}: {error}") from error
        if '"installed"' in text:
            return receipt.parent.name
    return None


def installed_attempt_or_none(verb: str) -> str | None:
    """installed_attempt() for a verb that can go on without it: an unreadable receipt reads as no fork known installed,
    with one stderr line naming the receipt (and so the repair), not a traceback."""
    try:
        return installed_attempt()
    except ForkReceiptError as error:
        print(f"{verb}: fork receipt unreadable, treating as no fork known installed ({error}); delete or repair that receipt",
              file=sys.stderr, flush=True)
        return None


def fork_line() -> str:
    """Physical package state, including forks whose private-worktree receipts have disappeared."""
    return fork_census.summary(physical_fork_census())


def physical_fork_census() -> dict:
    profiles = [target.parent.parent.parent for target in mod_targets()]
    required = [fork_census.documents_profile(), *[box(name) for name in BOXES]]
    return fork_census.census(MAIN, [*profiles, *required], required)


def fork_guard(allow_no_fork: bool = False) -> None:
    """A stock override permits only physically empty profiles; unknown or mismatched forks always refuse."""
    result = physical_fork_census()
    fork_census.write_evidence(result, RUN / "fork-census.state.json")
    fork_census.guard(result, allow_no_fork)
    if allow_no_fork:
        os.environ["SIMSONLINE_NO_FORK_OK"] = "1"  # the guarded stackctl subprocesses must share the choice


def next_attempt(current: str | None) -> str:
    numbers = [int(d.name.rsplit("-", 1)[1]) for d in (MAIN / ".omo/cache/fork").glob("cold-test-*") if d.name.rsplit("-", 1)[1].isdigit()]
    return f"cold-test-{max(numbers + [0]) + 1:03d}"


def fork_cycle(attempt: str | None) -> None:
    """A gameplay-fork change in one go: the overlay (scratch/g02_overlay.py) onto the main checkout's fork tree, the
    fork build, the installed attempt restored and the next one installed (on the server profile and every box)."""
    started = time.time()
    proj = MAIN / ".omo/cache/fork/gameplay_02_cs73"
    text = run([sys.executable, "scratch/g02_overlay.py", "apply"], env={**os.environ, "SIMSONLINE_FORK_PROJ": str(proj)})
    say("overlay: " + text.strip().splitlines()[-1][:120])
    # This checkout's build script on the main checkout's fork tree; the game's assemblies from SIMSONLINE_GAME_ASSEMBLIES
    # when set (else the main checkout's _analysis/sims3_assemblies_fixed).
    text = run(["pwsh", "-NoProfile", "-File", str(ROOT / "scratch/build-g02-cs73.ps1"), "-AssemblyName", "Sims3GameplaySystems",
                "-Root", str(MAIN)], cwd=MAIN, timeout=1200)
    built = next((ln for ln in reversed(text.splitlines()) if ln.startswith("out:")), "")
    if not built:
        raise SystemExit("fork build failed:\n" + text[-2000:])
    say("fork build: " + built)
    current = installed_attempt_or_none("fork-cycle")  # unreadable: nothing known to restore, go on to install
    if current:
        fork_restore(current)
    attempt = attempt or next_attempt(current)
    fork_install(attempt)
    say(f"fork cycle {current or '-'} -> {attempt} in {time.time() - started:.0f}s (restore at the end: fork-restore {attempt})")


def test_setup(extra: list[str] | None = None) -> None:
    """After a boot: the household's needs full and static (a TS3 restart drops the freeze; Sims starve or pass out
    otherwise during long test series), and those of extra (player 2's Sim of another family, --player2)."""
    names = list(SIMS) + [n for n in extra or [] if n not in SIMS]
    text = run([sys.executable, "scripts/sim_act.py", "--needs", "static:" + ",".join(names)])
    say("test setup: " + (text.strip().splitlines() or ["-"])[-1][:120])


def move_aside(source: Path, target: Path) -> None:
    """Path.replace, or a copy and delete when the two sit on different drives: the fork cache under the main checkout
    is a junction to E: since 2026-10-22, the box profiles stay on C:, and the restore failed there with WinError 17
    after the server's half had already been restored (Session T)."""
    try:
        source.replace(target)
    except OSError as error:
        if getattr(error, "winerror", None) != 17 and error.errno != 18:  # ERROR_NOT_SAME_DEVICE / EXDEV
            raise
        if target.is_dir():
            shutil.rmtree(target)
        shutil.move(str(source), str(target))


def fork_restore(attempt: str) -> None:
    say("stack down: " + stack_down())
    folder = MAIN / f".omo/cache/fork/{attempt}"
    text = run(["pwsh", "-NoProfile", "-File", "scratch/deploy-g02-cold.ps1", "-Action", "Restore", "-Attempt", attempt], cwd=MAIN)
    say("deploy-g02-cold.ps1 Restore: " + text.strip().splitlines()[-1][:160])
    for name in BOXES:
        profile = box(name)
        override = profile / "Mods/Packages" / FORK_PACKAGE
        if override.exists():
            move_aside(override, folder / f"{name}-fork-override.package")
        cache = profile / "scriptCache.package"
        if cache.exists():
            move_aside(cache, folder / f"{name}-profile-scriptCache.fork")
        aside = profile / f"scriptCache.{attempt}-aside.package"
        if aside.exists():
            aside.replace(cache)
        for slot, archived in (("Option4BookChain.sims3", f"{name}-world.sims3"),
                               ("Option4BookChain-checkpoint.sims3", f"{name}-world-checkpoint.sims3")):
            world = profile / "Saves" / slot
            if (world / "simsonline-world.txt").exists():
                move_aside(world, folder / archived)
    for slot in ("Option4BookChain.sims3", "Option4BookChain-checkpoint.sims3"):
        backup = REAL / "Saves" / (slot + ".backup")
        if backup.exists():
            move_aside(backup, folder / ("server-" + slot + ".backup"))
    say(f"fork {attempt} restored (receipt: {json.loads((folder / 'deployment.json').read_text(encoding='utf-8-sig')).get('status')})")


BRIDGE_TRACE_HELP = ("with --boot: boot stackctl with --bridge-trace (the controller's bridge record to "
                     ".omo/run/server-bridge.log); also SIMSONLINE_BRIDGE_TRACE=1")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="verb", required=True)
    for verb in ("fork-install", "fork-restore"):
        sub.add_parser(verb).add_argument("attempt")
    cycle = sub.add_parser("fork-cycle")
    cycle.add_argument("--attempt", default=None, help="the attempt to install (default: the next cold-test number)")
    cycle.add_argument("--boot", action="store_true", help="then install the mod and boot (run --mod --boot)")
    cycle.add_argument("--parallel", action="store_true", default=None, help="with --boot: both games load at once (default)")
    cycle.add_argument("--sequential", action="store_false", dest="parallel", help="with --boot: the client after the server")
    cycle.add_argument("--no-gc-watch", action="store_true", help="with --boot: no GC reader (SIMSONLINE_GC_WATCH=0)")
    cycle.add_argument("--bridge-trace", action="store_true", help=BRIDGE_TRACE_HELP)
    cycle.add_argument("--clients", type=int, default=None, help="with --boot: 2 also boots player 2's game (as run --clients)")
    times = sub.add_parser("boot-times", help="each recorded boot's phases per game, and the mean per mode")
    times.add_argument("--last", type=int, default=12)
    sub.add_parser("active-household", help="the server's family, live and in its boot save; WARNING if not the Martinezes")
    r = sub.add_parser("run")
    r.add_argument("--mod", action="store_true")
    r.add_argument("--boot", action="store_true")
    r.add_argument("--hide-clients", action="store_true",
                   help="Windows: hide tracked client game windows after boot, or in the current claimed stack")
    r.add_argument("--no-fork-ok", action="store_true", help="with --boot: boot although no gameplay fork is installed (refused otherwise)")
    r.add_argument("--bridge-trace", action="store_true", help=BRIDGE_TRACE_HELP)
    r.add_argument("--server-env", action="append", default=[])
    r.add_argument("--client-env", action="append", default=[])
    r.add_argument("--parallel", action="store_true", default=None, help="with --boot: both games load at once (default)")
    r.add_argument("--sequential", action="store_false", dest="parallel", help="with --boot: the client after the server")
    r.add_argument("--full", action="store_true")
    r.add_argument("--step", action="append", default=[])
    r.add_argument("--sims", default="")
    r.add_argument("--live-needs", action="store_true", help="after --boot, leave needs decaying (default: static)")
    r.add_argument("--no-gc-watch", action="store_true",
                   help="with --boot: start no GC reader (gc_watch.py start; the same as SIMSONLINE_GC_WATCH=0)")
    r.add_argument("--clients", type=int, default=None,
                   help="with --boot: 2 also boots player 2's game (Pablo, Client3Box) once client 1 has claimed, 3 "
                        "player 3's too (--player3, Client4Box); default 2 under SIMSONLINE_COMPARE_CLIENT=2 "
                        "(scenarios.py --client 2), else 1")
    r.add_argument("--player2", default="Pablo",
                   help="with --boot --clients 2: the Sim player 2's game claims, by first name; default Pablo")
    r.add_argument("--player3", default="Gunther",
                   help="with --boot --clients 3: the Sim player 3's game claims (Client4Box, bridge 34032; ); "
                        "default Gunther (the Goths)")
    args = parser.parse_args()
    if getattr(args, "bridge_trace", False):
        global BRIDGE_TRACE
        BRIDGE_TRACE = True
    if getattr(args, "clients", None) is not None and not args.boot:
        raise SystemExit("--clients is a boot option: use it with --boot")
    if getattr(args, "no_gc_watch", False):
        os.environ["SIMSONLINE_GC_WATCH"] = "0"  # gc_watch.py start, run by the boot, reads it
    if args.verb == "fork-install":
        fork_install(args.attempt)
    elif args.verb == "fork-restore":
        fork_restore(args.attempt)
    elif args.verb == "boot-times":
        boot_times(args.last)
        return
    elif args.verb == "active-household":
        sys.exit(household_check())
    elif args.verb == "fork-cycle":
        fork_cycle(args.attempt)
        if args.boot:
            say("stack down: " + stack_down())
            install_mod()
            clients = boot_clients(args.clients)
            boot({}, {}, parallel=args.parallel)
            if clients >= 2:
                boot_client2({}, restart=False)
            if clients >= 3:
                boot_client2({}, restart=False, sim="Gunther", player="3")
            test_setup(["Gunther"] if clients >= 3 else [])
            household_check()
    else:
        sims = dict(SIMS)
        for pair in filter(None, args.sims.split(",")):
            name, _, sim_id = pair.partition("=")
            sims[name] = sim_id
        if args.parallel is not None and not args.boot:
            raise SystemExit("--parallel / --sequential are boot modes: use them with --boot")
        if args.boot:
            fork_guard(args.no_fork_ok)
        if args.mod:
            if args.boot:
                # A running TS3 can hold the package open: down first, then install, then boot.
                say("stack down: " + stack_down())
            install_mod()
        if args.boot:
            clients = boot_clients(args.clients)
            client_env = dict(p.split("=", 1) for p in args.client_env)
            boot(dict(p.split("=", 1) for p in args.server_env), client_env, parallel=args.parallel)
            later = {"2": args.player2, "3": args.player3}
            for player in [p for p in later if int(p) <= clients]:
                boot_client2(client_env, restart=False, sim=later[player], player=player)
            if not args.live_needs:
                test_setup([later[p] for p in later if int(p) <= clients])
            household_check()
        if args.hide_clients:
            hide_client_windows()
        run_steps(args.step, sims, full=args.full)
    say("done")


if __name__ == "__main__":
    main()
