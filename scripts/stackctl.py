#!/usr/bin/env python3
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

    @staticmethod
    def load_state(*args, **kwargs):
        return {}

    @staticmethod
    def save_state(*args, **kwargs):
        return None

    @staticmethod
    def side_game(*args, **kwargs):
        return None

    @staticmethod
    def detach_trace(*args, **kwargs):
        return False, "excluded in public mirror"

"""stackctl -- SimsOnline dev stack lifecycle (consolidated CLI).

It replaced the earlier PowerShell dev-session scripts. `python scripts/stackctl.py --test` runs its self-tests.
Single-file Python CLI whose first-class output is structured JSON. Python 3.10+ stdlib-only.

Commands:
  stackctl up                      server + client1 (full stack)
  stackctl server                  server only
  stackctl client                   client1 mirror (server must be up)
  stackctl client2                  second mirror client (player 2)
  stackctl client-restart           recycle client1 + its TS3, keep server warm
  stackctl client2-down             tear down ONLY client2
  stackctl client3 / client3-down   player 3's client (Client4Box, bridge 34032), up or down only it
  stackctl server-reattach          rebuild + restart controller against warm TS3
  stackctl reload-mod               rebuild + reinstall mod
  stackctl stop-controller          kill controller only, keep TS3 warm
  stackctl status                   processes + listeners + mod + game state
  stackctl logs                     tail stack logs
  stackctl archive [--prune N [--yes]]  the run archive's folders and size; --prune lists the <stamp>-auto
                                    folders older than the newest N, --yes removes them (nothing is removed otherwise)
  stackctl down                     kill everything, print receipt

  stackctl boot-full                agent-safe: launch up + bounded poll
  stackctl boot-server              launch server + bounded poll
  stackctl boot-client              launch client + bounded poll
  stackctl boot-client2             launch client2 + bounded poll
  stackctl boot-client3             launch client3 (player 3) + bounded poll
  stackctl boot-client-restart      launch client-restart + bounded poll
  stackctl reattach-server          launch server-reattach + bounded poll
  stackctl launch-full              detached launch only, returns immediately
  stackctl launch-server             detached launch only
  stackctl launch-client            detached launch only
  stackctl launch-client2           detached launch only
  stackctl launch-client3           detached launch only
  stackctl launch-client-restart    detached launch only
  stackctl launch-reattach-server   detached launch only
  stackctl wait-ready               bounded poll until stack ready (exit 0/1/2)

Flags (after the verb):
  --json             structured JSON output (streaming for boot verbs)
  --build            rebuild controllers before launch
  --bridge-trace     per-frame bridge protocol trace
  --detached         launch only, skip boot waits
  --no-auto-up       reload-mod: don't auto-up afterwards
  --skip-mod-sync    skip auto make-mod when workspace differs
  --quick            status: skip DIAG_STATE probe
  --probe            status: probe the bridge even when another session holds the stack claim
  --timeout N        wait-ready/boot timeout in seconds (default 600)
  --require-client   wait-ready: require client listener too

Exit codes:
  wait-ready/boot-*: 0=ready, 1=timeout, 2=boot failed
  status: 0 by default, 1 with --strict when stack readiness fails, 3 with --strict when another session's
          stack claim skipped the bridge probes (listeners up, bridge health not checked); logs: 0
  all others: 0=success, 1=failure
"""
import argparse
import json
import ntpath
import os
import re
import shutil
import signal
import socket
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

# ─── Paths ──────────────────────────────────────────────────────────────────

SCRIPTS = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(SCRIPTS)
sys.path.insert(0, REPO)
sys.path.insert(0, SCRIPTS)
import ts3_paths  # scripts/ts3_paths.py
import fork_census
from local_endpoints import CLIENT_BRIDGECTL, PUBLIC_ENDPOINT, SERVER_BRIDGECTL

RUN_DIR = os.environ.get("SIMSONLINE_RUN_DIR") or os.path.join(REPO, ".omo", "run")
# build_slot.py install ( G17): the controllers run that slot's own SimsOnline.DedicatedServer.exe and
# SimsOnline.Client.exe in place of dotnet run --project; build_slot.py release removes it.
BUILD_SLOT_FILE = os.path.join(RUN_DIR, "build-slot.json")
ARCHIVE_DIR = os.path.join(RUN_DIR, "archive")
# <stamp>-auto folders a launch keeps (SIMSONLINE_ARCHIVE_KEEP). 0, the default, keeps every one: they are evidence
# ( read about 140 of 10-19's client.log.err to date a new error line). stackctl archive --prune N removes by hand.
ARCHIVE_KEEP = 0
AUTO_ARCHIVE = re.compile(r"\d{4}-\d{2}-\d{2}-\d{6}-auto")

SERVER_LOG = os.path.join(RUN_DIR, "server.log")
SERVER_STATE_FILE = os.path.join(RUN_DIR, "server.state.json")
SERVER_TS3_PID_FILE = os.path.join(RUN_DIR, "server-ts3.pid")
SERVER_BRIDGE_LOG = os.path.join(RUN_DIR, "server-bridge.log")

CLIENT_LOG = os.path.join(RUN_DIR, "client.log")
CLIENT_STATE_FILE = os.path.join(RUN_DIR, "client.state.json")
CLIENT_TS3_PID_FILE = os.path.join(RUN_DIR, "client-ts3.pid")
CLIENT_BRIDGE_LOG = os.path.join(RUN_DIR, "client-bridge.log")
CLIENT_SNAPSHOT_LOG = os.path.join(RUN_DIR, "client-snapshots.jsonl")

CLIENT2_LOG = os.path.join(RUN_DIR, "client2.log")
CLIENT2_STATE_FILE = os.path.join(RUN_DIR, "client2.state.json")
CLIENT2_TS3_PID_FILE = os.path.join(RUN_DIR, "client2-ts3.pid")
CLIENT2_BRIDGE_LOG = os.path.join(RUN_DIR, "client2-bridge.log")
CLIENT2_SNAPSHOT_LOG = os.path.join(RUN_DIR, "client2-snapshots.jsonl")

MOD_MANIFEST = os.path.join(RUN_DIR, "mod-build.json")

CLIENT2_BRIDGECTL = os.environ.get("SIMSONLINE_CLIENT2_BRIDGECTL") or "127.0.0.1:34031"

# The client players this stack boots (three players at a park). Players 1 and 2 keep their historical file
# names and constants above; player N >= 3 gets client<N>.log, client<N>.state.json, client<N>-ts3.pid, ... and bridge
# port 34029 + N (SIMSONLINE_CLIENT<N>_BRIDGECTL overrides it), with the verbs client<N>, client<N>-down,
# boot-client<N> and launch-client<N>.
CLIENT_PLAYERS = (1, 2, 3)


def client_files(player_id: int) -> dict:
    """One client player's controller role, bridge endpoint and run files."""
    if player_id == 1:
        return {"role": "client", "bridgectl": CLIENT_BRIDGECTL, "log": CLIENT_LOG, "state": CLIENT_STATE_FILE,
                "pid": CLIENT_TS3_PID_FILE, "bridge_log": CLIENT_BRIDGE_LOG, "snapshot": CLIENT_SNAPSHOT_LOG}
    if player_id == 2:
        return {"role": "client2", "bridgectl": CLIENT2_BRIDGECTL, "log": CLIENT2_LOG, "state": CLIENT2_STATE_FILE,
                "pid": CLIENT2_TS3_PID_FILE, "bridge_log": CLIENT2_BRIDGE_LOG, "snapshot": CLIENT2_SNAPSHOT_LOG}
    n = player_id
    return {"role": f"client{n}",
            "bridgectl": os.environ.get(f"SIMSONLINE_CLIENT{n}_BRIDGECTL") or f"127.0.0.1:{34029 + n}",
            "log": os.path.join(RUN_DIR, f"client{n}.log"), "state": os.path.join(RUN_DIR, f"client{n}.state.json"),
            "pid": os.path.join(RUN_DIR, f"client{n}-ts3.pid"), "bridge_log": os.path.join(RUN_DIR, f"client{n}-bridge.log"),
            "snapshot": os.path.join(RUN_DIR, f"client{n}-snapshots.jsonl")}


def client_box(args, player_id: int) -> str:
    """The Sandboxie box a client player's TS3 runs in (--sandboxie-box, --sandboxie-box2, --sandboxie-box3)."""
    return getattr(args, "sandboxie_box" if player_id == 1 else f"sandboxie_box{player_id}", "") or ""


def client_wineprefix(args, player_id: int):
    """A client player's Wine prefix (Linux): --client-wineprefix, --client2-wineprefix, else ~/.sims3-client<N>."""
    if player_id == 1:
        return args.client_wineprefix
    chosen = getattr(args, f"client{player_id}_wineprefix", None)
    if chosen or not sys.platform.startswith("linux"):
        return chosen
    return os.path.join(os.environ.get("HOME", ""), f".sims3-client{player_id}")

# The environment a client game needs: SimLink's client ring (without it SimLinkShadow.NativeVersion stays 0 and every
# native patch reports ready=0) and the server's world. quick_loop.py passes exactly this (its CLIENT_ENV; a test keeps
# the two equal); a client launched from here by hand inherits only what the shell has, so launch_client warns.
CLIENT_ENV = {"SIMSONLINE_SIMLINK_CLIENT_RING": "1", "SIMSONLINE_WORLD_FROM_SERVER": "1"}

# TS3 paths. CONTROLLER_DEFAULT_TS3_PATH is the portable default the server controller uses
# when --ts3 is omitted (SessionConfig.FromArgs); the client controller has no default and
# always gets --client-ts3. On Linux/Wine HeadlessSims3Host maps any Windows-style path onto
# the prefix's drive_c. SIMSONLINE_TS3_PATH overrides it
# for machines where the game lives elsewhere (same pattern as the SIMSONLINE_*_BRIDGECTL
# overrides above).
CONTROLLER_DEFAULT_TS3_PATH = r"C:\Program Files (x86)\The Sims 3\Game\Bin\TS3W.exe"
# On Windows the first existing TS3W.exe (ts3_paths.py: SIMSONLINE_TS3_PATH when that file exists, then the usual
# install folders): a shell can carry a Wine-prefix SIMSONLINE_TS3_PATH that does not exist on this host, and boot-full
# then failed to find the game. Elsewhere SIMSONLINE_TS3_PATH as given, else the portable default.
_FOUND_TS3 = ts3_paths.find_ts3w() if sys.platform == "win32" else None
DEFAULT_TS3_PATH = (str(_FOUND_TS3) if _FOUND_TS3 else None) or os.environ.get("SIMSONLINE_TS3_PATH") or CONTROLLER_DEFAULT_TS3_PATH
DEFAULT_SAVE = "Sunset Valley.sims3"
DEFAULT_CLAIM_SIM = 18031287978496800304  # Jennifer Martinez
DEFAULT_CLIENT_LAUNCH_DELAY = 8
DEFAULT_HOST_TIMEOUT = 600


def _box_wine_user(users_root: str, fallback: str) -> str:
    """The user folder of a box's Wine-style profile: drive_c/users/<unix user of the prefix it was copied from>, not
    this Windows user. The one holding a Sims 3 Saves folder."""
    try:
        for user in sorted(os.listdir(users_root)):
            if os.path.isdir(os.path.join(users_root, user, "Documents", "Electronic Arts", "The Sims 3", "Saves")):
                return user
    except OSError:
        pass
    return fallback


def _box_has_isolated_profile(box_name: str) -> bool:
    r"""Select a prepared, box-local Sims 3 profile instead of OneDrive Documents.

    DefaultBox on this Windows host inherits the real OneDrive Documents folder
    and fails every world load (cold-test-021). Client2Box/Client3Box use
    C:\Sims\.sims3-client only within their own sandbox. Never assume the
    profile exists on a different development machine.
    """
    if sys.platform != "win32":
        return False
    root = os.path.join(
        os.environ.get("SYSTEMDRIVE", "C:") + os.sep, "Sandbox",
        os.environ.get("USERNAME", ""), box_name,
        "drive", "C", "Sims", ".sims3-client", "drive_c", "users",
    )
    try:
        users = os.listdir(root)
    except OSError:
        return False
    for user in users:
        profile = os.path.join(root, user, "Documents", "Electronic Arts", "The Sims 3")
        if (
            os.path.isdir(os.path.join(profile, "Saves"))
            and os.path.isfile(os.path.join(profile, "Mods", "Resource.cfg"))
        ):
            return True
    return False


# CLI flags always override these defaults; environment overrides are explicit.
DEFAULT_SANDBOXIE_BOX = os.environ.get("SIMSONLINE_SANDBOXIE_BOX")
if DEFAULT_SANDBOXIE_BOX is None:
    DEFAULT_SANDBOXIE_BOX = (
        "Client2Box" if _box_has_isolated_profile("Client2Box") else "DefaultBox"
    )
DEFAULT_SANDBOXIE_BOX2 = os.environ.get("SIMSONLINE_SANDBOXIE_BOX2")
if DEFAULT_SANDBOXIE_BOX2 is None:
    DEFAULT_SANDBOXIE_BOX2 = (
        "Client3Box"
        if DEFAULT_SANDBOXIE_BOX == "Client2Box"
        and _box_has_isolated_profile("Client3Box")
        else "Client2Box"
    )
# Player 3: Client4Box, made from Client3Box the way Client3Box was made from Client2Box. No fallback: an
# empty box stops launch-client3 with the empty-box error instead of sharing another player's box.
DEFAULT_SANDBOXIE_BOX3 = os.environ.get("SIMSONLINE_SANDBOXIE_BOX3")
if DEFAULT_SANDBOXIE_BOX3 is None:
    DEFAULT_SANDBOXIE_BOX3 = (
        "Client4Box"
        if DEFAULT_SANDBOXIE_BOX2 == "Client3Box" and _box_has_isolated_profile("Client4Box")
        else ""
    )

FAIL_PATTERN = re.compile(
    r"Failed to bind|TimeoutException|Unhandled exception|FileNotFoundException"
)


# ─── ANSI Colors ────────────────────────────────────────────────────────────

class C:
    """ANSI color codes for human-mode output (non-JSON)."""
    RED = "\033[31m"
    GREEN = "\033[32m"
    YELLOW = "\033[33m"
    BLUE = "\033[34m"
    BOLD = "\033[1m"
    RESET = "\033[0m"

    @staticmethod
    def supports_color():
        return sys.stdout.isatty() and os.environ.get("TERM") != "dumb"


def cprint(msg, color="", end="\n"):
    """Print with color if stdout is a tty and --json is not set."""
    if C.supports_color() and color:
        print(f"{color}{msg}{C.RESET}", end=end)
    else:
        print(msg, end=end)


def utc_now():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.fffZ")


def timestamp_stamp():
    return datetime.now().strftime("%Y%m%d-%H%M%S")


# ─── Platform ───────────────────────────────────────────────────────────────

class Platform:
    """Cross-platform seam: Linux/Wine vs Windows/native."""

    def __init__(self):
        self.is_linux = sys.platform.startswith("linux")
        self.is_windows = sys.platform == "win32"
        self.home_dir = os.environ.get("HOME") if self.is_linux else os.environ.get("USERPROFILE", "")
        # Windows tasklist IMAGENAME filters require the full "TS3W.exe"; a bare
        # "TS3W" matches nothing, which made reattach-server see zero live TS3 pids.
        self.ts3_process_name = "TS3W.exe"

        wineprefix = os.environ.get("WINEPREFIX")
        if not wineprefix and self.is_linux:
            wineprefix = os.path.join(self.home_dir or "", ".sims3")
        self.wineprefix = wineprefix

        # Default Wine prefixes for the three-TS3 stack
        self.server_prefix = os.path.join(self.home_dir, ".sims3") if self.is_linux else None
        self.client_prefix = os.path.join(self.home_dir, ".sims3-client") if self.is_linux else None
        self.client2_prefix = os.path.join(self.home_dir, ".sims3-client2") if self.is_linux else None

        # Sandboxie (Windows-only)
        self.sandboxie_box = DEFAULT_SANDBOXIE_BOX
        self.sandboxie_box2 = DEFAULT_SANDBOXIE_BOX2
        self.sandboxie_start = r"C:\Program Files\Sandboxie-Plus\Start.exe"

    @property
    def win_style(self):
        """Windows: @{WindowStyle='Hidden'}; Linux: empty (no -WindowStyle)."""
        return {} if self.is_linux else {"WindowStyle": "Hidden"}


# ─── StateFile ───────────────────────────────────────────────────────────────

class StateFile:
    """Read and poll .omo/run/*.state.json phase transitions."""

    def __init__(self, file_path: str):
        self.path = file_path
        os.makedirs(RUN_DIR, exist_ok=True)

    def read(self):
        """Return the parsed state JSON or None if the file doesn't exist / is invalid."""
        if not os.path.exists(self.path):
            return None
        try:
            with open(self.path, "r") as f:
                return json.load(f)
        except (json.JSONDecodeError, OSError):
            return None

    def get_phase(self):
        state = self.read()
        return state.get("phase") if state else None

    def get_pid(self):
        state = self.read()
        return state.get("pid") if state else None

    def get_mod_build(self):
        state = self.read()
        return state.get("modBuild") if state else None

    def get_game_pid(self):
        state = self.read()
        return state.get("gamePid") if state else None

    def is_error(self):
        state = self.read()
        if not state:
            return False
        return state.get("phase") in ("error", "stopping", "stopped")

    def is_pid_dead(self):
        """True if the controller pid in the state file no longer exists."""
        state = self.read()
        if not state or not state.get("pid"):
            return False
        return not _pid_alive(state["pid"])

    def wait_phase_ready(self, timeout_sec: int, label: str, json_mode: bool = False,
                         launch_proc=None):
        """Poll the state file until phase=ready, timeout, or error. Returns True on ready."""
        deadline = time.time() + timeout_sec
        last_phase = ""
        if not json_mode:
            cprint(f"waiting for {label} (timeout {timeout_sec}s)...", C.BOLD)

        while time.time() < deadline:
            if launch_proc is not None and test_devsession_launch_failed(launch_proc):
                rc = launch_proc.poll()
                if json_mode:
                    _emit_event("error", "launch_failed", f"launch process exited rc={rc}")
                else:
                    cprint(f"FAILED: launch process for {label} exited rc={rc}", C.RED)
                    _tail_log(self.path.replace(".state.json", ".log"))
                    _tail_log(self.path.replace(".state.json", ".log") + ".err")
                return False

            state = self.read()
            if state and state.get("gamePid"):
                pin_game(self.path)  # once the controller has its TS3, not only at ready: a boot can time out first

            if state:
                phase = state.get("phase", "")
                if phase != last_phase:
                    last_phase = phase
                    detail = state.get("detail", "")
                    if json_mode:
                        _emit_event("phase", phase, detail, state.get("pid"))
                    else:
                        suffix = f" ({detail})" if detail else ""
                        cprint(f"  phase: {phase}{suffix}")

                if phase == "ready":
                    if not json_mode:
                        cprint(f"READY: {label}", C.GREEN)
                    return True

                if phase == "error":
                    if json_mode:
                        _emit_event("error", phase, state.get("detail", ""))
                    else:
                        cprint(f"FAILED while waiting for {label}: {state.get('detail', '')}", C.RED)
                        _tail_log(self.path.replace(".state.json", ".log"))
                    return False

                if phase in ("stopping", "stopped"):
                    if json_mode:
                        _emit_event("error", phase, f"controller went down ({phase})")
                    else:
                        cprint(f"FAILED: controller went down (phase={phase}) while waiting for {label}", C.RED)
                    return False

                # Check if the controller pid died
                pid = state.get("pid")
                if pid and not _pid_alive(pid):
                    if json_mode:
                        _emit_event("error", phase, f"controller pid {pid} exited")
                    else:
                        cprint(f"FAILED: controller (pid {pid}) exited during phase '{phase}'", C.RED)
                    return False

            # Fallback: check the log file for legacy signals (pre-state-file binary)
            log_path = self.path.replace(".state.json", ".log")
            if os.path.exists(log_path) and os.path.getsize(log_path) > 0:
                try:
                    with open(log_path, "r", errors="replace") as f:
                        text = f.read()
                    if "Authoritative session is running" in text or "client_ts3_session_ready" in text:
                        if not json_mode:
                            cprint(f"READY: {label} (log line)", C.GREEN)
                        return True
                    if FAIL_PATTERN.search(text) or "client_ts3_startup_failed" in text:
                        if not json_mode:
                            cprint(f"FAILED while waiting for {label}:", C.RED)
                            _tail_log(log_path)
                        return False
                except OSError:
                    pass

            time.sleep(2)

        if not json_mode:
            phase_note = f" (stuck in phase '{last_phase}')" if last_phase else ""
            cprint(f"TIMEOUT waiting for {label}{phase_note}", C.RED)
            # Print phase history if available
            state = self.read()
            if state and state.get("history"):
                cprint("  phase history:")
                for h in state["history"]:
                    cprint(f"    {h.get('utc', '?')} {h.get('phase', '?')} {h.get('detail', '')}")
            else:
                _tail_log(self.path.replace(".state.json", ".log"))
        return False


# ─── ProcessCtl ─────────────────────────────────────────────────────────────

_run_id = timestamp_stamp()

_launch_procs = {}


def new_role_env(role: str):
    """Environment vars tagging a launched process with its stack role."""
    return {
        "SIMSONLINE_ROLE": role,
        "SIMSONLINE_RUN_ID": _run_id,
    }


def _pid_alive(pid: int) -> bool:
    """Check if a process is alive. Uses os.kill(pid, 0) on Unix, tasklist on Windows."""
    if sys.platform.startswith("linux"):
        try:
            os.kill(pid, 0)
            return True
        except (ProcessLookupError, PermissionError):
            return False
    else:
        result = subprocess.run(
            ["tasklist", "/FI", f"PID eq {pid}", "/FO", "CSV", "/NH"],
            capture_output=True, text=True, timeout=15
        )
        if result.returncode:
            raise OSError(f"tasklist {pid}: {result.stderr.strip()}")
        return any(len(fields := line.split('","')) >= 2 and fields[1].strip('"') == str(pid)
                   for line in result.stdout.splitlines())


def get_ts3_pids():
    """Get all TS3 process PIDs by process name."""
    plat = Platform()
    if plat.is_linux:
        import subprocess as sp
        result = sp.run(["pgrep", "-f", "TS3W.exe"], capture_output=True, text=True)
        return [int(p) for p in result.stdout.strip().split("\n") if p.strip()] if result.returncode == 0 else []
    else:
        result = subprocess.run(["tasklist", "/FI", f"IMAGENAME eq {plat.ts3_process_name}", "/FO", "CSV", "/NH"],
                                capture_output=True, text=True)
        pids = []
        for line in result.stdout.strip().split("\n"):
            if line:
                parts = line.split('","')
                if len(parts) >= 2:
                    try:
                        pids.append(int(parts[1].strip('"')))
                    except ValueError:
                        pass
        return pids


def _read_proc_environ(pid: int):
    """Read /proc/PID/environ and return as a string (Linux only)."""
    env_path = f"/proc/{pid}/environ"
    if not os.path.exists(env_path):
        return None
    try:
        with open(env_path, "rb") as f:
            return f.read().decode("utf-8", errors="replace")
    except OSError:
        return None


def get_controller_pids_by_role(role: str, plat):
    """Controller PIDs for one role: state-file pid everywhere, plus SIMSONLINE_ROLE
    environ tag on Linux. Never matches another role's controller."""
    state_file = ({"server": SERVER_STATE_FILE} | {client_files(n)["role"]: client_files(n)["state"]
                                                   for n in CLIENT_PLAYERS})[role]
    pids = set()
    state_pid = StateFile(state_file).get_pid()
    if state_pid and _pid_alive(state_pid):
        pids.add(state_pid)
    if plat.is_linux:
        for pid in get_controller_pids():
            env = _read_proc_environ(pid)
            if env and re.search(rf"SIMSONLINE_ROLE={re.escape(role)}(\x00|$)", env):
                pids.add(pid)
    return sorted(pids)


def get_pids_by_role(role: str):
    """Find TS3 PIDs tagged with a specific SIMSONLINE_ROLE (Linux only)."""
    if not sys.platform.startswith("linux"):
        return get_ts3_pids()
    matched = []
    for pid in get_ts3_pids():
        env = _read_proc_environ(pid)
        if env and re.search(rf"SIMSONLINE_ROLE={re.escape(role)}(\x00|$)", env):
            matched.append(pid)
    return matched


def get_stack_owned_ts3_pids():
    """All TS3 PIDs whose environ carries ANY SIMSONLINE_ROLE tag. Excludes operator-started games."""
    if not sys.platform.startswith("linux"):
        return get_ts3_pids()
    matched = []
    for pid in get_ts3_pids():
        env = _read_proc_environ(pid)
        if env and re.search(r"SIMSONLINE_ROLE=(server|client\d*)(\x00|$)", env):
            matched.append(pid)
    return matched


def _front_door_enabled(args=None):
    return bool(getattr(args, "front_door", False)) or os.environ.get("SIMSONLINE_FRONT_DOOR") == "1"


def get_controller_pids(front_door=None):
    """Get all DedicatedServer + Client controller PIDs (excludes the current process)."""
    import subprocess as sp
    my_pid = os.getpid()
    pids = []
    enabled = _front_door_enabled() if front_door is None else front_door
    if sys.platform.startswith("linux"):
        # Match compiled binaries and `dotnet run --project …` wrapper processes.
        patterns = [
            r"SimsOnline\.DedicatedServer",
            r"dotnet run.*--project DedicatedServer",
            r"SimsOnline\.Client",
            r"dotnet run.*--project SimsOnline\.Client",
        ]
        if enabled:
            patterns.append("simlink_headless_server")
        for pattern in patterns:
            result = sp.run(["pgrep", "-f", pattern], capture_output=True, text=True)
            if result.returncode == 0:
                for p in result.stdout.strip().split("\n"):
                    if p.strip():
                        pid = int(p)
                        if pattern == "simlink_headless_server" and _classify_controller_cmdline(
                                _read_process_cmdline(pid), True) != "simlink_headless_server":
                            continue
                        if pid != my_pid and pid not in pids:
                            pids.append(pid)
    else:
        names = ["SimsOnline.DedicatedServer.exe", "SimsOnline.Client.exe"]
        if enabled:
            names.append("simlink_headless_server.exe")
        for exe_name in names:
            result = sp.run(["tasklist", "/FI", f"IMAGENAME eq {exe_name}", "/FO", "CSV", "/NH"],
                           capture_output=True, text=True)
            for line in result.stdout.strip().split("\n"):
                if line:
                    parts = line.split('","')
                    if len(parts) >= 2:
                        try:
                            pid = int(parts[1].strip('"'))
                            if pid != my_pid and pid not in pids:
                                pids.append(pid)
                        except ValueError:
                            pass
    return pids


def stop_controller(pid: int, graceful: bool = True, grace_seconds: int = 5):
    """Stop a controller process. SIGTERM-first (graceful DISCONNECT), escalate to SIGKILL."""
    if not _pid_alive(pid):
        return False
    if sys.platform.startswith("linux") and graceful:
        try:
            os.kill(pid, signal.SIGTERM)
        except (ProcessLookupError, PermissionError):
            pass
        for _ in range(grace_seconds * 10):
            if not _pid_alive(pid):
                return True
            time.sleep(0.1)
    # SIGKILL / Stop-Process -Force
    if sys.platform.startswith("linux"):
        try:
            os.kill(pid, signal.SIGKILL)
        except (ProcessLookupError, PermissionError):
            pass
    else:
        subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
    return True


def kill_process(pid: int, plat) -> None:
    """Kill the owned process tree; never treat a rejected Windows kill as success."""
    if plat.is_linux:
        os.kill(pid, signal.SIGKILL)
    else:
        result = subprocess.run(["taskkill", "/F", "/PID", str(pid), "/T"],
                                capture_output=True, text=True, timeout=15)
        if result.returncode and _pid_alive(pid):
            raise OSError(f"taskkill {pid}: {result.stderr.strip() or result.stdout.strip()}")


def wait_processes_gone(pids, timeout=10):
    """Bound teardown by observed process absence, not a fixed settling delay."""
    deadline = time.monotonic() + timeout
    while True:
        try:
            remaining = [pid for pid in pids if _pid_alive(pid)]
        except (OSError, subprocess.TimeoutExpired) as exc:
            cprint(f"cleanup failed: {exc}", C.RED)
            return False
        if not remaining:
            return True
        if time.monotonic() >= deadline:
            cprint(f"cleanup failed: processes still alive: {remaining}", C.RED)
            return False
        time.sleep(0.1)


def stop_client_sandbox(args, plat, player_id):
    """Terminate and verify this player's box, including descendants no PID file names."""
    box = client_box(args, player_id)
    if plat.is_linux or not box:
        return True
    start = getattr(args, "sandboxie_start", r"C:\Program Files\Sandboxie-Plus\Start.exe")
    command = [start, f"/box:{box}"]

    def list_pids():
        result = subprocess.run(command + ["/listpids"], capture_output=True, text=True, timeout=15)
        values = result.stdout.split()
        if result.returncode or not values or any(not value.isdecimal() for value in values):
            raise OSError(f"{box}: could not enumerate sandbox processes")
        numbers = [int(value) for value in values]
        if numbers[0] != len(numbers) - 1 or any(pid <= 0 for pid in numbers[1:]):
            raise OSError(f"{box}: incomplete sandbox process list")
        return numbers[1:]

    try:
        before = list_pids()
        if not before:
            return True
        result = subprocess.run(command + ["/terminate"], capture_output=True, text=True, timeout=15)
        if result.returncode:
            raise OSError(f"{box}: termination failed: {result.stderr.strip()}")
        deadline = time.monotonic() + 10
        while True:
            remaining = list_pids()
            if not remaining:
                # Also check the original IDs: do not trust a launcher's exit status alone.
                return wait_processes_gone(before)
            if time.monotonic() >= deadline:
                raise OSError(f"{box}: sandbox processes still alive: {remaining}")
            time.sleep(0.1)
    except (OSError, subprocess.TimeoutExpired) as exc:
        cprint(f"cleanup failed: {exc}", C.RED)
        if isinstance(exc, subprocess.TimeoutExpired):
            for name in ("stdout", "stderr"):
                data = getattr(exc, name)
                if isinstance(data, bytes):
                    data = data.decode("utf-8", errors="replace")
                cprint(f"cleanup {name}={data!r}", C.RED)
        return False


def stop_for_reattach(pid: int):
    """SIGKILL directly (skip graceful path so the warm game survives). Linux only."""
    if not sys.platform.startswith("linux"):
        return stop_controller(pid, graceful=True)
    if not _pid_alive(pid):
        return False
    try:
        os.kill(pid, signal.SIGKILL)
    except (ProcessLookupError, PermissionError):
        pass
    for _ in range(50):
        if not _pid_alive(pid):
            return True
        time.sleep(0.1)
    # Fallback
    return stop_controller(pid, graceful=False)


def launch_controller(args, role: str, log_path: str, working_dir: str, extra_env: dict = None):
    """Launch a dotnet run process with role env tags + stdout redirect."""
    result = launch_fork_census(args)
    fork_census.write_evidence(result, Path(RUN_DIR) / f"{role}-fork-census.state.json")
    fork_census.guard(result)
    env = os.environ.copy()
    env.update(new_role_env(role))
    if extra_env:
        env.update(extra_env)

    os.makedirs(os.path.dirname(log_path), exist_ok=True)
    log_f = open(log_path, "w")
    err_f = open(log_path + ".err", "w")
    windows_console = {}
    if os.name == "nt":
        startup = subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow = subprocess.SW_HIDE
        windows_console = {"creationflags": subprocess.CREATE_NEW_CONSOLE, "startupinfo": startup}

    proc = subprocess.Popen(
        args,
        stdout=log_f,
        stderr=err_f,
        cwd=working_dir,
        env=env,
        # The .NET CLI needs a console, but it must not share the short-lived launcher's.
        start_new_session=os.name != "nt",
        **windows_console,
    )
    log_f.close()
    err_f.close()
    return proc


def launch_fork_census(command, boxes=None, prefixes=None):
    """The last common boundary before legacy or front-door controllers can launch or attach TS3."""
    boxes = list(boxes or [])
    prefixes = list(prefixes or [])
    if "--client-ts3-sandboxie-box" in command:
        boxes.append(command[command.index("--client-ts3-sandboxie-box") + 1])
    if "--client-ts3-wineprefix" in command:
        prefixes.append(command[command.index("--client-ts3-wineprefix") + 1])
    saves = command[command.index("--client-saves-dir") + 1] if "--client-saves-dir" in command else ""
    checkout = fork_census.receipt_checkout()
    return fork_census.census(checkout, boxes=boxes, wineprefixes=prefixes, client_saves=saves)


# ─── Readiness ──────────────────────────────────────────────────────────────

def test_listener(endpoint: str, timeout_ms: int = 600) -> bool:
    """Test if a TCP listener is accepting connections."""
    parts = endpoint.split(":")
    if len(parts) != 2:
        return False
    try:
        host, port = parts[0], int(parts[1])
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.settimeout(timeout_ms / 1000.0)
        result = sock.connect_ex((host, port))
        sock.close()
        return result == 0
    except (OSError, ValueError):
        return False


def invoke_bridge_health(
    include_client: bool,
    timeout_sec: int = 15,
    client_endpoint: str = CLIENT_BRIDGECTL,
):
    """Run bridge-health.py and return its result as a dict."""
    bridge_health = os.path.join(SCRIPTS, "bridge-health.py")
    if not os.path.exists(bridge_health):
        return None
    cmd = [
        sys.executable,
        bridge_health,
        "--server-endpoint",
        SERVER_BRIDGECTL,
        "--timeout",
        str(timeout_sec),
    ]
    if include_client:
        cmd.extend(["--client", "--client-endpoint", client_endpoint])
    result = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout_sec + 5)
    return {
        "exit_code": result.returncode,
        "stdout": result.stdout.strip(),
        "stderr": result.stderr.strip(),
    }


def get_readiness_snapshot(
    require_client: bool,
    probe_timeout: int = 15,
    client_endpoint: str = CLIENT_BRIDGECTL,
    public_endpoint: str = PUBLIC_ENDPOINT,
):
    """Assemble the full readiness picture: listeners + bridge-health."""
    public = test_listener(public_endpoint)
    server_ctl = test_listener(SERVER_BRIDGECTL)
    client_ctl = test_listener(client_endpoint) if require_client else None

    health = None
    health_ok = False
    listeners_ok = public and server_ctl
    if require_client:
        listeners_ok = listeners_ok and client_ctl

    bridge_health_script = os.path.join(SCRIPTS, "bridge-health.py")
    if listeners_ok and os.path.exists(bridge_health_script):
        health = invoke_bridge_health(
            include_client=require_client,
            timeout_sec=probe_timeout,
            client_endpoint=client_endpoint,
        )
        if health:
            health_ok = health["exit_code"] == 0
    elif listeners_ok:
        health_ok = True

    return {
        "ready": listeners_ok and health_ok,
        "public": public,
        "server_ctl": server_ctl,
        "client_ctl": client_ctl,
        "health": health,
        "health_ok": health_ok,
    }


def probe_game_state(endpoint: str, timeout_sec: float = 2.5):
    """DIAG_STATE probe on a bridgectl endpoint. Returns a dict or None."""
    try:
        import bridgectl
        sock = bridgectl.connect(endpoint, timeout_sec)
        diag = bridgectl.roundtrip(sock, f"@{int(timeout_sec)} DIAG_STATE")
        sock.close()
        if not diag.get("ok") or not diag.get("accepted"):
            return None
        report = diag.get("report", "")
        inworld = re.search(r"inWorld=([^;\"]+)", report)
        tick = re.search(r"tick=(\d+)", report)
        dispatch = re.search(r"dispatch=(\d+)", report)
        in_world_val = inworld.group(1).strip() if inworld else None
        return {
            "in_world": in_world_val,
            "loaded": in_world_val == "True",
            "tick": int(tick.group(1)) if tick else 0,
            "dispatch": int(dispatch.group(1)) if dispatch else 0,
        }
    except Exception:
        return None


# ─── Log Management ─────────────────────────────────────────────────────────

_archived = False


def save_run_logs():
    """Archive .log/.err/.state.json artifacts before they're deleted on next launch."""
    global _archived
    if _archived:
        return
    if not os.path.isdir(RUN_DIR):
        return
    artifacts = []
    for f in os.listdir(RUN_DIR):
        path = os.path.join(RUN_DIR, f)
        if os.path.isfile(path) and (f.endswith(".log") or f.endswith(".err") or f.endswith(".state.json")
                                     or f.endswith(".prev.out")):
            artifacts.append(path)
    if not artifacts:
        return
    stamp = datetime.now().strftime("%Y-%m-%d-%H%M%S")
    dest = os.path.join(ARCHIVE_DIR, f"{stamp}-auto")
    os.makedirs(dest, exist_ok=True)
    for f in artifacts:
        try:
            if f.endswith(".prev.out"):  # quick_loop's last boot output (it renames it at its next boot): once, as .out
                shutil.copy2(f, os.path.join(dest, os.path.basename(f)[:-len(".prev.out")] + ".out"))
                os.remove(f)
            else:
                shutil.copy2(f, dest)
        except OSError:
            pass
    _archived = True
    removed = prune_archive()
    cprint(f"archived previous run logs -> .omo/run/archive/{stamp}-auto (archive: {archive_text()}"
           + ("" if removed else "; stackctl archive --prune N lists what it would remove") + ")")


def archive_folders():
    """The archive's folders, oldest name first."""
    try:
        return sorted(f for f in os.listdir(ARCHIVE_DIR) if os.path.isdir(os.path.join(ARCHIVE_DIR, f)))
    except OSError:
        return []


def folder_bytes(path):
    total = 0
    for root, _, files in os.walk(path):
        for f in files:
            try:
                total += os.path.getsize(os.path.join(root, f))
            except OSError:
                pass
    return total


def archive_text(folders=None):
    """The archive in a phrase: "679 folder(s), 742 MB"."""
    folders = archive_folders() if folders is None else folders
    size = sum(folder_bytes(os.path.join(ARCHIVE_DIR, f)) for f in folders)
    return f"{len(folders)} folder(s), {size / 1e6:.0f} MB"


def cmd_archive(prune=None, yes=False):
    """stackctl archive [--prune N [--yes]]: the archive's folder count and size. With --prune N, the <stamp>-auto
    folders older than the newest N and their size; only with --yes are they removed. A folder with any other name
    (a hand-saved run) is never listed. Files only: no game, no bridge."""
    print(f"archive {ARCHIVE_DIR}: {archive_text()}")
    if prune is None:
        return 0
    if prune < 0:
        print("archive --prune: N is the count of newest <stamp>-auto folders to keep (0 or more)")
        return 2
    auto = [f for f in archive_folders() if AUTO_ARCHIVE.fullmatch(f)]
    old = auto[:max(0, len(auto) - prune)]
    if not old:
        print(f"   nothing to remove: {len(auto)} <stamp>-auto folder(s), keeping the newest {prune}")
        return 0
    size = sum(folder_bytes(os.path.join(ARCHIVE_DIR, f)) for f in old)
    print(f"   {'removing' if yes else 'would remove'} {len(old)} of {len(auto)} <stamp>-auto folder(s), "
          f"{size / 1e6:.0f} MB, {old[0]} to {old[-1]}; keeping the newest {prune} and every other folder")
    if not yes:
        print("   dry run: add --yes to remove them")
        return 0
    for f in old:
        shutil.rmtree(os.path.join(ARCHIVE_DIR, f), ignore_errors=True)
    print(f"   archive now: {archive_text()}")
    return 0


def prune_archive(keep=None):
    """Keep the newest keep <stamp>-auto folders of the archive (default SIMSONLINE_ARCHIVE_KEEP, else ARCHIVE_KEEP: 0,
    which keeps every one); a parallel boot makes two (its server's launch and its client's). A folder with any other
    name stays. Returns how many went."""
    if keep is None:
        try:
            keep = int(os.environ.get("SIMSONLINE_ARCHIVE_KEEP", ARCHIVE_KEEP))
        except ValueError:
            keep = ARCHIVE_KEEP
    if keep <= 0:
        return 0
    try:
        auto = sorted(f for f in os.listdir(ARCHIVE_DIR)
                      if AUTO_ARCHIVE.fullmatch(f) and os.path.isdir(os.path.join(ARCHIVE_DIR, f)))
    except OSError:
        return 0
    old = auto[:-keep]
    for f in old:
        shutil.rmtree(os.path.join(ARCHIVE_DIR, f), ignore_errors=True)
    if old:
        cprint(f"archive: removed {len(old)} folder(s) older than the newest {keep} (SIMSONLINE_ARCHIVE_KEEP)")
    return len(old)


def _tail_log(log_path, lines=8):
    """Print last N lines of a log file."""
    if not os.path.exists(log_path) or os.path.getsize(log_path) == 0:
        return
    try:
        with open(log_path, "r", errors="replace") as f:
            all_lines = f.readlines()
        for line in all_lines[-lines:]:
            print(line.rstrip())
    except OSError:
        pass


def _emit_event(event_type, phase, detail="", pid=None):
    """Emit a newline-delimited JSON event for --json streaming mode."""
    event = {
        "event": event_type,
        "phase": phase,
        "detail": detail,
        "utc": utc_now(),
    }
    if pid:
        event["pid"] = pid
    sys.stdout.write(json.dumps(event) + "\n")
    sys.stdout.flush()


# ─── Mod Staleness ──────────────────────────────────────────────────────────

def get_live_mod_stamp(state_file: str, log_path: str):
    """Read the live mod build stamp from the state file first, fall back to log grep."""
    sf = StateFile(state_file)
    stamp = sf.get_mod_build()
    if stamp:
        return stamp
    if os.path.exists(log_path) and os.path.getsize(log_path) > 0:
        try:
            with open(log_path, "r", errors="replace") as f:
                for line in f:
                    m = re.search(r"Host mod build:\s*(.+)", line)
                    if m:
                        return m.group(1).strip()
        except OSError:
            pass
    return None


def parse_mod_stamp(stamp: str):
    """Parse 'sha@utc' into (sha, utc) or None."""
    if not stamp:
        return None
    if "@" in stamp:
        sha, utc = stamp.rsplit("@", 1)
        return (sha, utc)
    return None


def get_workspace_git_sha():
    """Get the short git SHA of the workspace."""
    try:
        result = subprocess.run(
            ["git", "rev-parse", "--short", "HEAD"],
            capture_output=True, text=True, cwd=REPO
        )
        if result.returncode == 0 and result.stdout.strip():
            return result.stdout.strip()
    except (OSError, subprocess.SubprocessError):
        pass
    return "unknown"


def get_mod_manifest():
    """Read .omo/run/mod-build.json."""
    if not os.path.exists(MOD_MANIFEST):
        return None
    try:
        with open(MOD_MANIFEST, "r") as f:
            return json.load(f)
    except (json.JSONDecodeError, OSError):
        return None


def get_mod_build_status(stack_running: bool):
    """Return a human-readable mod staleness string."""
    workspace_sha = get_workspace_git_sha()
    manifest = get_mod_manifest()
    installed_sha = manifest.get("gitSha") if manifest else None
    installed_utc = manifest.get("builtUtc") if manifest else None

    if not stack_running:
        if installed_sha:
            note = "" if installed_sha == workspace_sha else f" (workspace is {workspace_sha})"
            return f"mod={installed_sha} installed; stack not running (or not yet ready){note}"
        return "mod: (unknown - run make-mod and restart stack)"

    live_stamp = get_live_mod_stamp(SERVER_STATE_FILE, SERVER_LOG)
    live = parse_mod_stamp(live_stamp)
    if not live:
        return "mod: running but no build stamp (check server.state.json modBuild or server.log)"

    live_sha, live_utc = live
    if installed_utc and live_utc != installed_utc:
        return f"mod={live_sha} (STALE - running build predates the last make-mod, run reload-mod)"
    if live_sha != workspace_sha:
        return f"mod={live_sha} (STALE - workspace is {workspace_sha}, run reload-mod)"
    return f"mod={live_sha} (current)"


# ─── Ensure Wine Prefixes ───────────────────────────────────────────────────

def ensure_client_wineprefix(prefix: str = None):
    """Auto-bootstrap a client Wine prefix from the server prefix (Linux only)."""
    plat = Platform()
    if not plat.is_linux:
        return
    prefix = prefix or plat.client_prefix
    game_exe = os.path.join(prefix, "drive_c/Program Files (x86)/The Sims 3/Game/Bin/TS3W.exe")
    if os.path.exists(game_exe):
        return
    server_game_exe = os.path.join(plat.server_prefix, "drive_c/Program Files (x86)/The Sims 3/Game/Bin/TS3W.exe")
    if not os.path.exists(server_game_exe):
        cprint("ERROR: server Wine prefix at ~/.sims3 not bootstrapped (run the game once first).", C.RED)
        sys.exit(2)
    cprint(f"bootstrapping client Wine prefix at {prefix} (one-time; copies game install ~22G)...", C.BOLD)
    bootstrap_script = os.path.join(SCRIPTS, "bootstrap-client-prefix.ps1")
    cmd = ["pwsh", "-NoProfile", "-File", bootstrap_script,
           "-Source", plat.server_prefix, "-Dest", prefix]
    result = subprocess.run(cmd)
    if result.returncode != 0:
        cprint("ERROR: client prefix bootstrap failed.", C.RED)
        sys.exit(2)


# ─── Ensure Mod Synced ──────────────────────────────────────────────────────

def ensure_mod_synced(skip: bool = False):
    """Auto-run make-mod when workspace sources differ from installed (never over an installed build slot)."""
    if skip:
        return True
    slot = installed_slot()
    if slot:
        cprint(f"mod sync: build slot {slot.get('slot')} is installed (build_slot.py), so its mod stays: no make-mod "
               "(build_slot.py release first for the workspace's build)", C.YELLOW)
        return True
    if not test_mod_needs_sync():
        return True

    cprint("mod sync: workspace differs from installed/running build - running make-mod...", C.BLUE)
    cmd = ["pwsh", "-NoProfile", "-File", os.path.join(SCRIPTS, "make-mod.ps1")]
    result = subprocess.run(cmd)
    if result.returncode != 0:
        cprint("ERROR: make-mod failed.", C.RED)
        return False
    return True


# ─── Flag Parsing Helpers ───────────────────────────────────────────────────

def quote_args(items):
    """Quote args that contain spaces (mirrors the PowerShell Quote-Args helper)."""
    result = []
    for item in items:
        s = str(item)
        if " " in s or '"' in s:
            result.append('"' + s.replace('"', '\\"') + '"')
        else:
            result.append(s)
    return result


# ─── Globals parsed from args ───────────────────────────────────────────────

class Args:
    """Holds parsed args globally for verb functions."""
    save = DEFAULT_SAVE
    endpoint = PUBLIC_ENDPOINT
    claim_sim = DEFAULT_CLAIM_SIM
    claim_sim2 = 0
    ts3_path = DEFAULT_TS3_PATH
    ts3_path_explicit = False
    host_timeout = DEFAULT_HOST_TIMEOUT
    client_launch_delay = DEFAULT_CLIENT_LAUNCH_DELAY
    player_command_timeout = 0
    bridge_trace = False
    minimize_server_ts3 = False
    build = False
    front_door = False
    front_door_bin = None
    detached = False
    quick = False
    probe = False
    strict = False
    force = False
    skip_mod_sync = False
    no_auto_up = False
    json_mode = False
    timeout = 600
    interval = 0
    probe_timeout = 15
    require_client = False
    client_wineprefix = None  # set in main()
    client2_wineprefix = None  # set in main()
    sandboxie_box = DEFAULT_SANDBOXIE_BOX
    sandboxie_box2 = DEFAULT_SANDBOXIE_BOX2
    sandboxie_box3 = DEFAULT_SANDBOXIE_BOX3
    claim_sim3 = 0
    client3_wineprefix = None
    sandboxie_start = r"C:\Program Files\Sandboxie-Plus\Start.exe"
    follow_logs = False
    # logs-specific
    log_server_only = False
    log_client_only = False
    log_client2_only = False
    log_lines = 40


# ─── Main ───────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    sub = parser.add_subparsers(dest="verb", metavar="<verb>")

    # Global flags (added to each subparser via parent)
    global_flags = argparse.ArgumentParser(add_help=False)
    global_flags.add_argument("--json", action="store_true", dest="json_mode",
                              help="structured JSON output")
    global_flags.add_argument("--build", action="store_true",
                              help="rebuild controllers before launch")
    global_flags.add_argument("--front-door", action="store_true",
                              default=os.environ.get("SIMSONLINE_FRONT_DOOR") == "1",
                              help="opt in to the Rust server attached to the existing server TS3W; clients use --wire line")
    global_flags.add_argument("--front-door-bin", default=os.environ.get("SIMSONLINE_FRONT_DOOR_BIN"),
                              help="Rust binary (otherwise CARGO_TARGET_DIR or crate target, release then debug; never builds implicitly)")
    global_flags.add_argument("--bridge-trace", action="store_true",
                              help="per-frame bridge protocol trace")
    global_flags.add_argument("--detached", action="store_true",
                              help="launch only, skip boot waits")
    global_flags.add_argument("--skip-mod-sync", action="store_true",
                              help="skip auto make-mod when workspace differs")
    global_flags.add_argument("--no-fork-ok", action="store_true",
                              help="permit a stock launch only when the physical fork census is empty")
    global_flags.add_argument("--no-auto-up", action="store_true",
                              help="reload-mod: don't auto-up afterwards")
    global_flags.add_argument("--save", default=DEFAULT_SAVE, help="save name")
    global_flags.add_argument("--endpoint", default=PUBLIC_ENDPOINT, help="public listener endpoint")
    global_flags.add_argument("--claim-sim", type=str, default=str(DEFAULT_CLAIM_SIM),
                              help="auto-claimed Sim objectId")
    global_flags.add_argument("--claim-sim2", type=str, default="0",
                              help="player 2's auto-claimed Sim (0=no claim)")
    global_flags.add_argument("--claim-sim3", type=str, default="0",
                              help="player 3's auto-claimed Sim (0=no claim)")
    global_flags.add_argument("--ts3-path", default=None,
                              help="TS3W.exe path (default: $SIMSONLINE_TS3_PATH, else the portable install path)")
    global_flags.add_argument("--host-timeout", type=int, default=DEFAULT_HOST_TIMEOUT,
                              help="TS3 boot timeout in seconds")
    global_flags.add_argument("--client-launch-delay", type=int, default=DEFAULT_CLIENT_LAUNCH_DELAY,
                              help="seconds between server and client launch")
    global_flags.add_argument("--player-command-timeout", type=int, default=0,
                              help="player-paced command timeout (0=default 10s)")
    global_flags.add_argument("--minimize-server-ts3", action="store_true",
                              help="minimize the server TS3 window once ready")
    global_flags.add_argument("--client-wineprefix", default=None,
                              help="client Wine prefix path (Linux)")
    global_flags.add_argument("--client2-wineprefix", default=None,
                              help="client2 Wine prefix path (Linux)")
    global_flags.add_argument("--sandboxie-box", default=DEFAULT_SANDBOXIE_BOX)
    global_flags.add_argument("--sandboxie-box2", default=DEFAULT_SANDBOXIE_BOX2)
    global_flags.add_argument("--sandboxie-box3", default=DEFAULT_SANDBOXIE_BOX3,
                              help="player 3's box (default Client4Box when it has the client profile, else none)")
    global_flags.add_argument("--client3-wineprefix", default=None,
                              help="client3 Wine prefix path (Linux; default ~/.sims3-client3)")
    global_flags.add_argument("--sandboxie-start", default=r"C:\Program Files\Sandboxie-Plus\Start.exe")

    # Operator verbs
    for verb in ["up", "server", "client", "client2", "client3", "client-restart", "client2-down", "client3-down",
                 "server-reattach", "reload-mod", "stop-controller", "status", "logs", "down", "archive"]:
        sp = sub.add_parser(verb, parents=[global_flags])
        if verb == "archive":
            sp.add_argument("--prune", type=int, metavar="N",
                            help="list the <stamp>-auto folders older than the newest N (removed only with --yes)")
            sp.add_argument("--yes", action="store_true", help="with --prune: remove them")
        if verb == "status":
            sp.add_argument("--quick", "-q", action="store_true", help="skip DIAG_STATE probe")
            sp.add_argument("--probe", action="store_true",
                            help="probe the bridge (DIAG_STATE, --strict's bridge-health) even when another session "
                                 "holds the stack claim (scripts/stack_claim.py)")
            sp.add_argument("--strict", action="store_true",
                              help="exit 1 when required listeners or bridge health are not ready")
        if verb == "down":
            sp.add_argument("--force", action="store_true",
                              help="Windows: sweep ALL TS3W.exe, not just stack-tracked pids")
            sp.add_argument("--ignore-claim", action="store_true",
                            help="go down even while another session holds the stack claim (scripts/stack_claim.py)")
        if verb == "logs":
            sp.add_argument("--follow", action="store_true", help="continuous tail (Ctrl+C)")
            sp.add_argument("--server", action="store_true", help="show only server.log")
            sp.add_argument("--client", action="store_true", help="show only client.log")
            sp.add_argument("--client2", action="store_true", help="show only client2.log")
            sp.add_argument("--client3", action="store_true", help="show only client3.log")
            sp.add_argument("--lines", type=int, default=40, help="number of lines to tail")

    # Agent verbs
    for verb in ["boot-full", "boot-server", "boot-client", "boot-client2", "boot-client3",
                 "boot-client-restart", "reattach-server"]:
        sp = sub.add_parser(verb, parents=[global_flags])
        sp.add_argument("--timeout", type=int, default=600, help="poll timeout in seconds")
        sp.add_argument("--interval", type=int, default=0, help="poll interval in seconds")
        sp.add_argument("--require-client", action="store_true")

    for verb in ["launch-full", "launch-server", "launch-client", "launch-client2", "launch-client3",
                 "launch-client-restart", "launch-reattach-server"]:
        sub.add_parser(verb, parents=[global_flags])

    sp = sub.add_parser("wait-ready", parents=[global_flags])
    sp.add_argument("--timeout", type=int, default=600, help="poll timeout in seconds")
    sp.add_argument("--interval", type=int, default=0, help="poll interval in seconds")
    sp.add_argument("--probe-timeout", type=int, default=15, help="bridge-health probe timeout")
    sp.add_argument("--require-client", action="store_true")

    args = parser.parse_args()

    if not args.verb:
        parser.print_help()
        return 0
    if args.verb == "archive":  # files only: no game, no pid sync
        return cmd_archive(args.prune, args.yes)

    # Populate the global Args struct
    plat = Platform()
    A = Args()
    A.save = args.save
    A.endpoint = args.endpoint
    A.claim_sim = int(args.claim_sim) if args.claim_sim.isdigit() else 0
    A.claim_sim2 = int(args.claim_sim2) if args.claim_sim2.isdigit() else 0
    A.claim_sim3 = int(args.claim_sim3) if args.claim_sim3.isdigit() else 0
    A.ts3_path = args.ts3_path or DEFAULT_TS3_PATH
    A.ts3_path_explicit = args.ts3_path is not None
    A.host_timeout = args.host_timeout
    A.client_launch_delay = args.client_launch_delay
    A.player_command_timeout = args.player_command_timeout
    A.bridge_trace = args.bridge_trace
    A.minimize_server_ts3 = getattr(args, "minimize_server_ts3", False)
    A.build = args.build
    A.front_door = args.front_door
    A.front_door_bin = args.front_door_bin
    A.detached = args.detached
    A.quick = getattr(args, "quick", False)
    A.probe = getattr(args, "probe", False)
    A.strict = getattr(args, "strict", False)
    A.force = getattr(args, "force", False)
    A.ignore_claim = getattr(args, "ignore_claim", False)
    A.skip_mod_sync = args.skip_mod_sync
    A.no_auto_up = args.no_auto_up
    A.json_mode = args.json_mode
    A.client_wineprefix = args.client_wineprefix or plat.client_prefix
    A.client2_wineprefix = args.client2_wineprefix or plat.client2_prefix
    A.sandboxie_box = args.sandboxie_box
    A.sandboxie_box2 = args.sandboxie_box2
    A.sandboxie_box3 = args.sandboxie_box3
    A.client3_wineprefix = args.client3_wineprefix
    A.sandboxie_start = args.sandboxie_start
    A.timeout = getattr(args, "timeout", 600)
    A.interval = getattr(args, "interval", 0)
    A.probe_timeout = getattr(args, "probe_timeout", 15)
    A.require_client = getattr(args, "require_client", False)
    A.follow_logs = getattr(args, "follow", False)
    A.log_server_only = getattr(args, "server", False) if args.verb == "logs" else False
    A.log_client_only = getattr(args, "client", False) if args.verb == "logs" else False
    A.log_client2_only = getattr(args, "client2", False) if args.verb == "logs" else False
    A.log_client3_only = getattr(args, "client3", False) if args.verb == "logs" else False
    A.log_lines = getattr(args, "lines", 40)
    if args.no_fork_ok:
        os.environ["SIMSONLINE_NO_FORK_OK"] = "1"

    # Set WINEPREFIX env if not set (Linux)
    if plat.is_linux and not os.environ.get("WINEPREFIX"):
        os.environ["WINEPREFIX"] = plat.server_prefix

    # Dispatch
    verb = args.verb
    launches = {"up", "server", "client", "client2", "client3", "client-restart", "server-reattach",
                "boot-full", "boot-server", "boot-client", "boot-client2", "boot-client3", "boot-client-restart",
                "launch-full", "launch-server", "launch-client", "launch-client2", "launch-client3",
                "launch-client-restart", "launch-reattach-server", "reattach-server"}
    if verb in launches or (verb == "reload-mod" and not A.no_auto_up):
        role = verb.removeprefix("boot-").removeprefix("launch-")
        players = ((1,) if role in ("up", "full", "reload-mod", "client", "client-restart") else
                   (int(role[-1]),) if role in ("client2", "client3") else ())
        result = launch_fork_census([], boxes=[client_box(A, p) for p in players] if not plat.is_linux else [],
                                    prefixes=[client_wineprefix(A, p) for p in players] if plat.is_linux else [])
        fork_census.write_evidence(result, Path(RUN_DIR) / "fork-census.state.json")
        fork_census.guard(result)
    # Every verb first makes the *-ts3.pid files name the running games: a crashed game's file goes, and a boot that
    # never pinned its game (a timed-out wait, a detached launch) gets it.
    sync_ts3_pid_files()

    if verb == "status":
        return cmd_status(A, plat)
    elif verb == "down":
        return cmd_down(A, plat)
    elif verb == "stop-controller":
        return cmd_stop_controller(A, plat)
    elif verb == "wait-ready":
        return cmd_wait_ready(A, plat)
    elif verb == "up":
        return cmd_up(A, plat)
    elif verb == "server":
        return cmd_server(A, plat)
    elif verb == "client":
        return cmd_client(A, plat)
    elif verb == "client2":
        return cmd_client2(A, plat)
    elif verb == "client3":
        return cmd_client_n(A, plat, 3)
    elif verb == "server-reattach":
        return cmd_server_reattach(A, plat)
    elif verb == "reload-mod":
        return cmd_reload_mod(A, plat)
    elif verb == "logs":
        return cmd_logs(A, plat)
    elif verb == "client-restart":
        return cmd_client_restart(A, plat)
    elif verb == "client2-down":
        return cmd_client2_down(A, plat)
    elif verb == "client3-down":
        return cmd_client_down(A, plat, 3)
    elif verb in ("launch-full", "launch-server", "launch-client", "launch-client2", "launch-client3",
                  "launch-client-restart", "launch-reattach-server"):
        return cmd_launch(verb, A, plat)
    elif verb in ("boot-full", "boot-server", "boot-client", "boot-client2", "boot-client3",
                  "boot-client-restart"):
        return cmd_boot(verb, A, plat)
    elif verb == "reattach-server":
        return cmd_reattach_server(A, plat)
    else:
        cprint(f"stackctl: unknown verb '{verb}'", C.RED)
        return 1


# ─── Implemented Verbs ──────────────────────────────────────────────────────
# (Wave 2+ will add the rest; these four prove the scaffold works end-to-end)

def claim_holder():
    """Who holds the live stack's claim (scripts/stack_claim.py) when it is not this session (own_session), else
    None. status asks (it skips its bridge probes for them) and down asks (it refuses: cmd_down); the boots and
    wait-ready never do, whoever holds it. A boot hook carries its session through CLAUDE_CODE_SESSION_ID, which
    every subprocess of a Claude Code session inherits, or SIMSONLINE_SESSION."""
    try:
        import stack_claim
        return stack_claim.holder(stack_claim.own_session())
    except Exception:
        return None


def cmd_status(args, plat):
    """Print process + listener + mod + game-state status."""
    strict_status = 1 if args.strict else 0
    try:
        # Gather processes
        import subprocess as sp
        def _get_pids(pattern):
            if plat.is_linux:
                result = sp.run(["pgrep", "-f", pattern], capture_output=True, text=True)
                if result.returncode == 0:
                    my_pid = os.getpid()
                    return [int(p) for p in result.stdout.strip().split("\n") if p.strip() and int(p) != my_pid]
            return []

        server_pids = _get_pids("SimsOnline.DedicatedServer")
        if _front_door_enabled(args):
            server_pids += [pid for pid in get_controller_pids(True)
                            if _get_process_name(pid, plat) in ("simlink_headless_server", "simlink_headless_server.exe")]
        client_pids = _get_pids("SimsOnline.Client")
        ts3_pids = get_ts3_pids()

        # Listeners
        public_ready = test_listener(args.endpoint)
        server_ctl = test_listener(SERVER_BRIDGECTL)
        client_ctl = test_listener(CLIENT_BRIDGECTL)
        client2_ctl = test_listener(CLIENT2_BRIDGECTL)
        client3 = client_files(3)
        client3_ctl = test_listener(client3["bridgectl"])

        # Another session's stack claim: listeners only, no DIAG_STATE or bridge-health PING in its series ( G12)
        claimed = None
        if (server_ctl or client_ctl or client2_ctl or client3_ctl) and (not args.quick or args.strict) and not getattr(args, "probe", False):
            holder = claim_holder()
            if holder:
                claimed = f"claimed by {holder}: bridge probes skipped (--probe to force)"
                args.quick = True

        if args.strict and claimed:
            strict_status = 3 if public_ready and server_ctl else 1  # a listener down is known; health is not
        elif args.strict:
            strict_snapshot = get_readiness_snapshot(
                require_client=client_ctl,
                probe_timeout=15,
                public_endpoint=args.endpoint,
            )
            strict_status = 0 if strict_snapshot["ready"] else 1

        # Mod status
        mod_status_str = get_mod_build_status(public_ready)
        mod_live_stamp = get_live_mod_stamp(SERVER_STATE_FILE, SERVER_LOG)
        mod_parsed = parse_mod_stamp(mod_live_stamp)
        workspace_sha = get_workspace_git_sha()
        manifest = get_mod_manifest()

        # Server state file
        server_state = StateFile(SERVER_STATE_FILE)
        s_state = server_state.read()

        # Client state files
        client_sf = StateFile(CLIENT_STATE_FILE)
        c_state = client_sf.read()
        client2_sf = StateFile(CLIENT2_STATE_FILE)
        c2_state = client2_sf.read()

        # Game state (DIAG_STATE probe unless --quick)
        game_state = {}
        server_loaded = False
        client_loaded = False
        if not args.quick:
            if server_ctl:
                gs = probe_game_state(SERVER_BRIDGECTL)
                if gs:
                    game_state["server"] = gs
                    server_loaded = gs.get("loaded", False)
            if client_ctl:
                gc = probe_game_state(CLIENT_BRIDGECTL)
                if gc:
                    game_state["client"] = gc
                    client_loaded = gc.get("loaded", False)
            if client2_ctl:
                gc2 = probe_game_state(CLIENT2_BRIDGECTL)
                if gc2:
                    game_state["client2"] = gc2
            if client3_ctl:
                gc3 = probe_game_state(client3["bridgectl"])
                if gc3:
                    game_state["client3"] = gc3

        if args.json_mode:
            result = {
                "controllers": {
                    "server": {"pid": server_pids[0] if server_pids else None, "running": bool(server_pids)},
                    "client": {"pids": client_pids, "running": bool(client_pids)},
                },
                "ts3": {"pids": ts3_pids},
                "listeners": {
                    "public": public_ready,
                    "server_ctl": server_ctl,
                    "client_ctl": client_ctl,
                    "client2_ctl": client2_ctl,
                    "client3_ctl": client3_ctl,
                },
                "mod": {
                    "stamp": mod_live_stamp,
                    "sha": mod_parsed[0] if mod_parsed else None,
                    "utc": mod_parsed[1] if mod_parsed else None,
                    "stale": "STALE" in mod_status_str,
                    "installed_sha": manifest.get("gitSha") if manifest else None,
                    "workspace_sha": workspace_sha,
                    "status": mod_status_str,
                },
                "state_files": {
                    "server": {"phase": s_state.get("phase") if s_state else None,
                               "pid": s_state.get("pid") if s_state else None,
                               "gamePid": s_state.get("gamePid") if s_state else None,
                               "modBuild": s_state.get("modBuild") if s_state else None},
                    "client": {"phase": c_state.get("phase") if c_state else None} if c_state else None,
                    "client2": {"phase": c2_state.get("phase") if c2_state else None} if c2_state else None,
                },
                "game_state": game_state,
                "claim": claimed,
            }
            print(json.dumps(result, indent=2))
            return strict_status

        # Human-mode output
        def pid_str(pids):
            return ", ".join(str(p) for p in pids) if pids else "not running"

        cprint(f"{'SimsOnline.DedicatedServer':<28} {pid_str(server_pids)}")
        cprint(f"{'SimsOnline.Client':<28} {pid_str(client_pids)}")
        cprint(f"{plat.ts3_process_name:<28} {pid_str(ts3_pids)}")

        cprint(f"{'public listener (' + args.endpoint + ')':<28} {'READY (accepting)' if public_ready else 'not accepting'}")
        def port(endpoint):
            return endpoint.rsplit(":", 1)[-1]

        cprint(f"{'server bridgectl (' + port(SERVER_BRIDGECTL) + ')':<28} {'READY (accepting)' if server_ctl else 'not accepting'}")
        cprint(f"{'client bridgectl (' + port(CLIENT_BRIDGECTL) + ')':<28} {'READY (accepting)' if client_ctl else 'not accepting'}")
        if client2_ctl or os.path.exists(CLIENT2_TS3_PID_FILE):
            cprint(f"{'client2 bridgectl (' + port(CLIENT2_BRIDGECTL) + ')':<28} {'READY (accepting)' if client2_ctl else 'not accepting'}")
        if client3_ctl or os.path.exists(client3["pid"]):
            cprint(f"{'client3 bridgectl (' + port(client3['bridgectl']) + ')':<28} {'READY (accepting)' if client3_ctl else 'not accepting'}")

        if server_pids and not public_ready:
            cprint("  -> server controller process is alive but NOT accepting: still booting, or wedged in the bridge handshake (check server.log tail below).", C.YELLOW)
        if claimed:
            cprint(claimed, C.YELLOW)

        if not args.quick:
            def _print_game_state(label, ctl_ready, gs):
                if not ctl_ready:
                    return False
                if gs and gs.get("in_world") is not None:
                    loaded = gs.get("loaded", False)
                    tag = "LOADED (in-world)" if loaded else f"NOT in-world ({gs['in_world']})"
                    dispatch = gs.get("dispatch", 0)
                    cprint(f"{label:<28} {tag}  tick={gs.get('tick', 0)}  dispatch={dispatch}")
                    return loaded
                cprint(f"{label:<28} controller up but no DIAG_STATE answer (TS3 still loading?)", C.YELLOW)
                return False

            server_loaded = _print_game_state("server game state", server_ctl, game_state.get("server"))
            client_loaded = _print_game_state("client game state", client_ctl, game_state.get("client"))
            if client2_ctl:
                _print_game_state("client2 game state", client2_ctl, game_state.get("client2"))
            if client3_ctl:
                _print_game_state("client3 game state", client3_ctl, game_state.get("client3"))

            if server_loaded and client_loaded:
                cprint(f"{'stack':<28} FULLY LOADED (server + client in-world)", C.GREEN)
            elif server_loaded and not client_ctl:
                cprint(f"{'stack':<28} SERVER LOADED (no client controller -- server-only stack)")
            elif server_loaded:
                cprint(f"{'stack':<28} server in-world; client NOT yet (still loading)", C.YELLOW)

        cprint(mod_status_str)
        client_mod = get_client_mod_build_status(client_ctl)
        if client_mod:
            cprint(client_mod)

        slot = installed_slot()
        if slot:
            cprint(f"build slot: {slot.get('slot')} ({slot.get('digest')}) installed: the mod, SimLink.asi and both "
                   "controllers are the slot's (build_slot.py check)", C.YELLOW)
        elif not args.skip_mod_sync and test_mod_needs_sync():
            cprint("mod sync: workspace differs from installed/running build (run up/server to auto-sync, or reload-mod)", C.YELLOW)

        if s_state and s_state.get("phase"):
            cprint(f"\nserver state: {s_state['phase']}")
            if s_state.get("gamePid"):
                cprint(f"server game pid: {s_state['gamePid']}")

        log_paths = [SERVER_LOG, CLIENT_LOG]
        if client2_ctl or os.path.exists(CLIENT2_LOG):
            log_paths.append(CLIENT2_LOG)
        if client3_ctl or os.path.exists(client3["log"]):
            log_paths.append(client3["log"])
        for log_path in log_paths:
            if os.path.exists(log_path):
                cprint(f"\n--- {os.path.basename(log_path)} (last 3) ---")
                _tail_log(log_path, lines=3)

    except Exception as e:
        cprint(f"status: diagnostic error (non-fatal): {e}", C.YELLOW)

    return strict_status


def gc_watch(*verb: str) -> str:
    """gc_watch.py VERB: stop, after down's sweep (the games' exits are in its rows first), or start --keep,
    before a server boot (a reader that runs, as quick_loop's boot starts one, is kept; SIMSONLINE_GC_WATCH=0 is none).
    Its last line, or why it could not run."""
    script = os.path.join(os.path.dirname(os.path.abspath(__file__)), "gc_watch.py")
    try:
        done = subprocess.run([sys.executable, script, *verb], capture_output=True, text=True, timeout=60)
    except (OSError, subprocess.SubprocessError) as ex:
        return f"gc watch: {' '.join(verb)} did not run ({ex})"
    lines = ((done.stdout or "") + (done.stderr or "")).strip().splitlines()
    return lines[-1] if lines else f"gc watch: {' '.join(verb)} exited {done.returncode}"


def wait_for_dumps_before_down():
    """G21: a down never cuts a catcher's full dump: wait (crash_catch.wait_for_dumps, up to 5 min) while one
    is being written in the run folder's dumps. Never blocks a down on its own failure."""
    try:
        from pathlib import Path

        try:
            import crash_catch
        except ImportError:
            crash_catch = _MirrorProcFallback

        return crash_catch.wait_for_dumps(Path(RUN_DIR) / "dumps", say=lambda line: cprint(line, C.YELLOW))
    except Exception as error:  # the guard must not stop a down
        cprint(f"dump guard: not run ({error.__class__.__name__}: {error})", C.YELLOW)
        return None


def cmd_down(args, plat):
    """Stop controllers (SIGTERM graceful) + sweep stack-owned TS3, then the boot's GC reader. Print receipt. A dump
    being written is waited for first (wait_for_dumps_before_down). While another session holds the stack claim it
    refuses (exit 2, stack_claim.down_refusal) before touching any process, --force included, unless --ignore-claim
    (Main, 2026-10-22). Windows: stack-owned is every TS3W.exe this
    checkout's or another worktree's files name (the controllers go machine-wide, so another worktree's games would
    be left without theirs), and the receipt counts every TS3W.exe left, the ones no files name too."""
    held = None if getattr(args, "ignore_claim", False) else claim_holder()
    if held:
        import stack_claim
        refusal = stack_claim.down_refusal(held)
        if getattr(args, "json_mode", False):
            print(json.dumps({"ok": False, "verb": "down", "refused": refusal}))
        print(refusal, file=sys.stderr, flush=True)
        return 2
    dump_wait = wait_for_dumps_before_down()
    stopped = []
    cleanup_ok = True

    # Stop client controllers
    for pid in get_controller_pids():
        if "Client" in _get_process_name(pid, plat):
            stop_controller(pid, graceful=True)
            cleanup_ok = wait_processes_gone([pid]) and cleanup_ok
            stopped.append(f"SimsOnline.Client({pid})")

    # Stop server controller
    for pid in _server_controller_pids(args, plat):
        name = _get_process_name(pid, plat)
        stop_controller(pid, graceful=True)
        cleanup_ok = wait_processes_gone([pid]) and cleanup_ok
        label = "simlink_headless_server" if name.startswith("simlink_headless_server") else "SimsOnline.DedicatedServer"
        stopped.append(f"{label}({pid})")

    # Sweep leftover stack-owned TS3. Linux: environ role tags (never touches operator
    # games). Windows: ONLY the pids this stack's controllers recorded (state-file gamePid
    # / *-ts3.pid pin files) unless --force opts into the unfiltered sweep.
    if plat.is_linux:
        leftover = get_stack_owned_ts3_pids()
        count_ts3 = get_stack_owned_ts3_pids
    elif args.force:
        cprint("WARNING: down --force kills EVERY TS3W.exe on this machine, including games you started by hand.", C.YELLOW)
        leftover = get_ts3_pids()
        count_ts3 = get_ts3_pids
    else:
        tracked = get_tracked_windows_ts3_pids()
        others = {pid: run for pid, run in get_other_checkout_ts3_pids().items() if pid not in tracked}
        if others:
            cprint("also sweeping the games another worktree's controllers launched: "
                   + ", ".join(f"{pid} ({run})" for pid, run in sorted(others.items())), C.YELLOW)
        leftover = sorted(set(tracked) | set(others))
        swept = set(leftover)

        def count_ts3():
            return [pid for pid in get_ts3_pids() if pid in swept]

        untracked = [pid for pid in get_ts3_pids() if pid not in swept]
        if untracked:
            cprint(f"NOT killing untracked TS3W pids ({', '.join(str(p) for p in untracked)}) -- "
                   "no stack run-state claims them; use 'down --force' to sweep everything.", C.YELLOW)
    if leftover:
        for pid in leftover:
            try:
                if plat.is_linux:
                    os.kill(pid, signal.SIGKILL)
                else:
                    # Windows: os.kill has no SIGKILL; use taskkill /F (the documented
                    # stackctl Windows teardown path).
                    kill_process(pid, plat)
            except (ProcessLookupError, PermissionError, OSError):
                cleanup_ok = False
        cprint(f"swept leftover TS3W: {', '.join(str(p) for p in leftover)}")

    for player_id in CLIENT_PLAYERS:
        cleanup_ok = stop_client_sandbox(args, plat, player_id) and cleanup_ok

    # Wait for survival count to reach zero
    deadline = time.time() + 10
    while time.time() < deadline:
        if len(count_ts3()) == 0:
            break
        time.sleep(0.3)

    remaining = len(count_ts3())
    # Windows: a TS3W.exe no checkout's files name is not swept without --force, but it is counted: a receipt of 0
    # with games up sent O's games-down window on (2026-10-22).
    unowned = [] if plat.is_linux or args.force else [pid for pid in get_ts3_pids() if pid not in swept]
    remaining += len(unowned)
    receipt = f"TS3W remaining = {remaining}" + (
        f" ({len(unowned)} no checkout's files name: {', '.join(str(p) for p in unowned)}; down --force sweeps them)"
        if unowned else "")
    sync_ts3_pid_files()  # the swept games' pid files go with them
    reader = gc_watch("stop")
    if not reader.endswith("no reader running"):
        cprint(reader)
    cprint(f"receipt: {receipt}")
    if remaining > 0:
        cprint(f"  -> WARNING: {remaining} TS3W still alive after 10s -- kill manually.", C.YELLOW)

    if stopped:
        cprint(f"stopped: {', '.join(stopped)}")

    if args.json_mode:
        result = {"ok": remaining == 0 and cleanup_ok, "verb": "down", "stopped": stopped,
                  "receipt": receipt, "gcWatch": reader, **({"unowned": unowned} if unowned else {})}
        if dump_wait and dump_wait.get("dumps"):
            result["dumpWait"] = dump_wait
        print(json.dumps(result))

    return 0 if remaining == 0 and cleanup_ok else 1


def cmd_stop_controller(args, plat):
    """Kill ONLY the DedicatedServer controller (SIGTERM-first, keep TS3 warm)."""
    stopped = []
    for pid in _server_controller_pids(args, plat):
        name = _get_process_name(pid, plat)
        stop_controller(pid, graceful=True)
        label = "simlink_headless_server" if name.startswith("simlink_headless_server") else "SimsOnline.DedicatedServer"
        stopped.append(f"{label}({pid})")
        cprint(f"stopped controller ({pid}); TS3 stays warm")

    if not stopped:
        cprint("no controller running")

    if args.json_mode:
        print(json.dumps({"ok": True, "verb": "stop-controller", "stopped": stopped}))

    return 0


def cmd_wait_ready(args, plat):
    """Bounded poll: state-file phase + listeners + bridge-health. Exit 0/1/2."""
    if _front_door_enabled(args):
        return _wait_front_door_ready(args)
    started = time.time()
    deadline = started + args.timeout
    poll = 0
    poll_interval = args.interval if args.interval > 0 else (5 if args.require_client else 15)
    last_phase = ""

    while time.time() < deadline:
        poll += 1

        # Check for boot failure (server state file error or dead pid)
        server_state = StateFile(SERVER_STATE_FILE)
        if server_state.is_error():
            if not args.json_mode:
                cprint("BOOT FAILED: server controller reported error", C.RED)
                state = server_state.read()
                if state and state.get("detail"):
                    cprint(f"  detail: {state['detail']}")
            if args.json_mode:
                print(json.dumps({"ok": False, "reason": "boot_failed", "polls": poll,
                                   "elapsed_seconds": int(time.time() - started)}))
            return 2

        # Check if the controller pid died (stale state file from a previous run)
        state = server_state.read()
        pin_game(SERVER_STATE_FILE)
        if args.require_client:
            pin_game(CLIENT_STATE_FILE)
        if state and state.get("phase") not in (None, "stopped") and server_state.is_pid_dead():
            if not args.json_mode:
                pid = state.get("pid")
                cprint(f"BOOT FAILED: server controller (pid {pid}) exited during phase '{state.get('phase')}'", C.RED)
            if args.json_mode:
                print(json.dumps({"ok": False, "reason": "controller_dead", "polls": poll,
                                   "elapsed_seconds": int(time.time() - started)}))
            return 2

        # Surface phase transitions
        if state and state.get("phase") != last_phase:
            last_phase = state.get("phase", "")
            if not args.json_mode:
                detail = state.get("detail", "")
                suffix = f" ({detail})" if detail else ""
                cprint(f"poll {poll}: controller phase -> {last_phase}{suffix}")

        # Readiness snapshot
        snap = get_readiness_snapshot(
            require_client=args.require_client,
            probe_timeout=args.probe_timeout,
            public_endpoint=args.endpoint,
        )
        if not args.json_mode:
            client_part = f" client_ctl={'yes' if snap['client_ctl'] else 'no'}" if args.require_client else ""
            health_part = " health=ok" if snap["health_ok"] else (" health=no" if snap["health"] else " health=skip")
            cprint(f"poll {poll}: public={'yes' if snap['public'] else 'no'}"
                   f" server_ctl={'yes' if snap['server_ctl'] else 'no'}{client_part}{health_part}")

        if snap["ready"]:
            pin_ts3_pid_from_state(SERVER_STATE_FILE, SERVER_TS3_PID_FILE)
            if args.require_client:
                pin_ts3_pid_from_state(CLIENT_STATE_FILE, CLIENT_TS3_PID_FILE)
            elapsed = int(time.time() - started)
            if args.json_mode:
                payload = {
                    "ok": True, "polls": poll, "elapsed_seconds": elapsed,
                    "public": snap["public"], "server_ctl": snap["server_ctl"],
                    "client_ctl": snap["client_ctl"], "require_client": bool(args.require_client),
                }
                if snap["health"] and snap["health"]["stdout"]:
                    try:
                        payload["health_json"] = json.loads(snap["health"]["stdout"])
                    except json.JSONDecodeError:
                        payload["health_raw"] = snap["health"]["stdout"]
                print(json.dumps(payload))
            else:
                cprint(f"STACK READY after {elapsed}s ({poll} polls)", C.GREEN)
                if snap["health"] and snap["health"]["stdout"]:
                    cprint(snap["health"]["stdout"])
            return 0

        time.sleep(poll_interval)

    elapsed = int(time.time() - started)
    # emit stack_incomplete telemetry on timeout
    try:
        from scripts.telemetry import TelemetryShim
        TelemetryShim.from_env().emit("dev_session.stack_incomplete", {"reason": "timeout", "elapsed_seconds": elapsed, "polls": poll, "client_role": "client" if getattr(args, "require_client", False) else "server"})
    except Exception:
        pass
    if args.json_mode:
        print(json.dumps({"ok": False, "reason": "timeout", "polls": poll, "elapsed_seconds": elapsed}))
    else:
        cprint(f"TIMEOUT after {elapsed}s ({poll} polls)", C.RED)
    return 1


# ─── Helpers ────────────────────────────────────────────────────────────────

def _classify_controller_cmdline(cmdline: str, front_door=None):
    """Return a controller-style name from a full cmdline (Linux /proc/PID/cmdline)."""
    if not cmdline or not cmdline.strip():
        return None
    enabled = _front_door_enabled() if front_door is None else front_door
    if enabled:
        stripped = cmdline.lstrip()
        executable = stripped.split('"', 2)[1] if stripped.startswith('"') else stripped.split()[0]
        if re.split(r"[\\/]", executable)[-1] in ("simlink_headless_server", "simlink_headless_server.exe"):
            return "simlink_headless_server"
    if "SimsOnline.DedicatedServer" in cmdline or re.search(
            r"dotnet run.*--project DedicatedServer\b", cmdline):
        return "SimsOnline.DedicatedServer"
    if "SimsOnline.Client" in cmdline or re.search(
            r"dotnet run.*--project SimsOnline\.Client\b", cmdline):
        return "SimsOnline.Client"
    return None


def _server_controller_pids(args, plat):
    enabled = _front_door_enabled(args)
    pids = get_controller_pids(True) if enabled else get_controller_pids()
    return [pid for pid in pids if "DedicatedServer" in _get_process_name(pid, plat)
            or (enabled and _get_process_name(pid, plat) in ("simlink_headless_server", "simlink_headless_server.exe"))]


def _read_process_cmdline(pid: int):
    """Read /proc/PID/cmdline as a single space-separated string."""
    try:
        with open(f"/proc/{pid}/cmdline", "rb") as f:
            raw = f.read()
        if not raw:
            return ""
        return raw.decode("utf-8", errors="replace").replace("\x00", " ").strip()
    except OSError:
        return ""


def _get_process_name(pid: int, plat: Platform):
    """Get the process name for a PID (used to distinguish controller types)."""
    if plat.is_linux:
        cmdline = _read_process_cmdline(pid)
        classified = _classify_controller_cmdline(cmdline)
        if classified:
            return classified
        if cmdline:
            return os.path.basename(cmdline.split()[0])
        try:
            with open(f"/proc/{pid}/comm", "r") as f:
                return f.read().strip()
        except OSError:
            return ""
    else:
        result = subprocess.run(["tasklist", "/FI", f"PID eq {pid}", "/NH", "/FO", "CSV"],
                                capture_output=True, text=True)
        for line in result.stdout.strip().split("\n"):
            if line and str(pid) in line:
                parts = line.split('","')
                if parts:
                    return parts[0].strip('"')
        return ""


# ─── Wave 2: Operator Verbs ──────────────────────────────────────────────────

def installed_slot():
    """The build slot build_slot.py install put in (its pointer: slot, digest, server, client), or None."""
    try:
        with open(BUILD_SLOT_FILE, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return None


def slot_binary(part, args=None):
    """The installed build slot's controller exe for part ("server" or "client"), or None: dotnet run --project. A
    pointer that names a missing exe, or --build with a slot installed, stops here."""
    pointer = installed_slot()
    if not pointer:
        return None
    exe = pointer.get(part)
    if not exe:
        return None
    if not os.path.isfile(exe):
        raise SystemExit(f"{BUILD_SLOT_FILE} names {exe} for the {part}, which is not there: "
                         "build_slot.py install the slot again, or build_slot.py release")
    if args is not None and getattr(args, "build", False):
        raise SystemExit(f"--build with build slot {pointer.get('slot')} installed: the {part} runs the slot's binaries; "
                         "build_slot.py release first")
    return exe


def _build_server_args(args, attach_pid=None):
    """Build the dotnet run args for the server controller (an installed build slot's exe instead, build_slot.py)."""
    if _front_door_enabled(args):
        override = getattr(args, "front_door_bin", None) or os.environ.get("SIMSONLINE_FRONT_DOOR_BIN")
        executable = "simlink_headless_server.exe" if os.name == "nt" else "simlink_headless_server"
        target = os.environ.get("CARGO_TARGET_DIR") or os.path.join(REPO, "rust", "simlink_headless_server", "target")
        if not os.path.isabs(target):
            target = os.path.join(REPO, target)
        candidates = [override] if override else [os.path.join(target, profile, executable) for profile in ("release", "debug")]
        binary = next((path for path in candidates if os.path.isfile(
            path if os.path.isabs(path) else os.path.join(REPO, path))), None)
        if binary is None:
            manifest = os.path.join(REPO, "rust", "simlink_headless_server", "Cargo.toml")
            raise SystemExit("front_door_binary_missing: " + ", ".join(candidates)
                             + f'. Build explicitly: cargo build --manifest-path "{manifest}" --release --bin simlink_headless_server'
                             + "; point --front-door-bin at the built binary.")
        pid = attach_pid if attach_pid is not None else resolve_server_ts3_pid_for_reattach()
        if not pid:
            raise SystemExit("--front-door requires an identifiable, already-running server TS3W PID")
        return [binary, "--pid", str(pid), "--serve", "--public", args.endpoint]
    build_arg = [] if args.build else ["--no-build"]
    exe = slot_binary("server", args)
    cmd_args = ["run", "--project", "DedicatedServer"] + build_arg + ["-c", "Debug", "--",
        "--save", args.save, "--host-timeout-seconds", str(args.host_timeout),
        "--listen", args.endpoint, "--bridgectl-listen", SERVER_BRIDGECTL,
        "--sync-broadcast-seconds", "2", "--state-file", SERVER_STATE_FILE]
    # Only forward --ts3 when it differs from the controller's portable default (or was
    # explicitly provided) so Linux/Wine hosts keep their prefix-remapped default.
    if args.ts3_path_explicit or args.ts3_path != CONTROLLER_DEFAULT_TS3_PATH:
        cmd_args += ["--ts3", args.ts3_path]
    if args.player_command_timeout > 0:
        cmd_args += ["--player-command-timeout-seconds", str(args.player_command_timeout)]
    if args.bridge_trace:
        cmd_args += ["--bridge-trace", "--bridge-record", SERVER_BRIDGE_LOG]
    return [exe] + cmd_args[cmd_args.index("--") + 1:] if exe else ["dotnet"] + cmd_args


def _build_client_args(args, player_id=1, bridgectl=CLIENT_BRIDGECTL, wineprefix=None):
    """Build the dotnet run args for a client controller (an installed build slot's exe instead, build_slot.py)."""
    plat = Platform()
    build_arg = [] if args.build else ["--no-build"]
    exe = slot_binary("client", args)
    display = "dev-session" if player_id == 1 else f"dev-session-{player_id}"
    files = client_files(player_id)
    snapshot_log = files["snapshot"]
    cmd_args = ["run", "--project", "SimsOnline.Client"] + build_arg + ["-c", "Debug", "--",
        "--endpoint", args.endpoint, "--player", str(player_id), "--display", display,
        "--role", "client", "--enable-mirror"]
    if _front_door_enabled(args):
        cmd_args += ["--wire", "line"]
    if player_id == 1 and args.claim_sim:
        cmd_args += ["--claim-sim", str(args.claim_sim)]
    elif player_id > 1 and getattr(args, f"claim_sim{player_id}", 0):
        cmd_args += ["--claim-sim", str(getattr(args, f"claim_sim{player_id}"))]
    cmd_args += ["--client-save", args.save,
        "--client-host-timeout", str(args.host_timeout),
        "--client-bridgectl-listen", bridgectl,
        "--snapshot-log", snapshot_log,
        "--state-file", files["state"],
        "--connect-timeout", "60"]
    # Always forward --client-ts3: the client controller has no default, and with --client-save
    # alone ClientMirrorHostFactory refuses to start. On Linux/Wine the client maps a Windows-style
    # path into its own prefix (HeadlessSims3Host.ResolveHostExecutablePath).
    cmd_args += ["--client-ts3", args.ts3_path]
    if plat.is_linux and wineprefix:
        cmd_args += ["--client-ts3-wineprefix", wineprefix]
    else:
        box = client_box(args, player_id)
        if box:
            cmd_args += ["--client-ts3-sandboxie-box", box, "--client-ts3-sandboxie-start", args.sandboxie_start]
        # Empty box name (e.g. --sandboxie-box "") -> native TS3 launch, no Sandboxie isolation.
        # Join with the server's world (SIMSONLINE_WORLD_FROM_SERVER=1): the client fetches the server's
        # checkpointed save into its TS3's Saves folder and loads that instead of --save.
        # SIMSONLINE_CLIENT_SAVES_DIR overrides the box profile's Saves folder.
        if os.environ.get("SIMSONLINE_WORLD_FROM_SERVER") == "1":
            cur_user = os.environ.get("USERNAME", "")
            users_root = os.path.join("C:\\Sandbox", cur_user, box or "", "drive", "C", "Sims", ".sims3-client",
                                      "drive_c", "users")
            saves = os.environ.get("SIMSONLINE_CLIENT_SAVES_DIR") or (box and os.path.join(
                users_root, os.environ.get("SIMSONLINE_WINE_USER") or _box_wine_user(users_root, cur_user),
                "Documents", "Electronic Arts", "The Sims 3", "Saves"))
            if saves:
                cmd_args += ["--client-world-from-server", "--client-saves-dir", saves]
    if args.player_command_timeout > 0:
        cmd_args += ["--client-player-command-timeout", str(args.player_command_timeout)]
    if args.bridge_trace:
        cmd_args += ["--bridge-trace", "--bridge-record", files["bridge_log"]]
    return [exe] + cmd_args[cmd_args.index("--") + 1:] if exe else ["dotnet"] + cmd_args


def launch_server(args):
    """Launch the server controller. Kills any stale controller first (recycles if port in use)."""
    plat = Platform()
    # Validate the resident binary before stopping a controller or clearing its logs.
    cmd_args = _build_server_args(args) if _front_door_enabled(args) else None
    # Recycle stale server controller: if a DedicatedServer process is alive but
    # the public listener isn't accepting (wedged, or port held by a dead process),
    # stop it before launching. If the listener IS accepting, the server is already up.
    server_pids = _server_controller_pids(args, plat)
    if server_pids:
        if test_listener(args.endpoint):
            cprint("server already running")
            return
        cprint("WARN: SimsOnline.DedicatedServer alive but public listener not accepting -- recycling", C.YELLOW)
        for pid in server_pids:
            stop_controller(pid, graceful=True)

    save_run_logs()
    for f in [SERVER_LOG, SERVER_BRIDGE_LOG, SERVER_STATE_FILE]:
        try:
            if os.path.exists(f):
                os.remove(f)
        except OSError:
            pass
    if cmd_args is None:
        cmd_args = _build_server_args(args)
    _launch_procs["server"] = launch_controller(cmd_args, "server", SERVER_LOG, REPO)
    if args.json_mode:
        _emit_event("launched", "launching", "server", None)


def missing_client_env(env=None):
    """The CLIENT_ENV keys env (default os.environ) does not set at all. A key set to another value is a choice (a run's
    --client-env SIMSONLINE_WORLD_FROM_SERVER=0, quick_loop.py), not the bare shell this guards against."""
    env = os.environ if env is None else env
    return [key for key in CLIENT_ENV if key not in env]


def warn_client_env(player_id):
    """Warn (stderr, so --json output stays clean) when a client launches without CLIENT_ENV: a bare
    `stackctl boot-client2` boots a game with no client ring, whose patch states read ready=0, and no claimed Sim."""
    missing = missing_client_env()
    if missing:
        print(f"WARNING: client{player_id} launches without {', '.join(f'{k}=1' for k in missing)} (quick_loop's "
              "CLIENT_ENV): no SimLink client ring, so its native patches read ready=0. Boot clients with "
              "python scripts/quick_loop.py run --mod --boot --clients 2 (it also claims player 2's Sim).",
              file=sys.stderr)


def launch_client(args, player_id=1, skip_orphan_kill=False):
    """Launch a client controller. Recycles stale/zombie client controllers first."""
    plat = Platform()
    files = client_files(player_id)
    bridgectl = files["bridgectl"]
    # Recycle stale client controller: if this role's Client process is alive but
    # its bridgectl isn't accepting, it's a zombie — kill it before launching.
    stop_zombie_client_controller(plat, player_id)
    if test_listener(bridgectl):
        cprint(f"{files['role']} already running")
        return
    warn_client_env(player_id)

    # Empty-box redirect guard (Windows): with no Sandboxie box there is no isolation, so a
    # second native TS3W would be single-instance-redirected into the already-running one.
    # Fail fast instead of launching a doomed second instance.
    if not plat.is_linux:
        box = client_box(args, player_id)
        if not box and get_ts3_pids():
            flag = "--sandboxie-box" if player_id == 1 else f"--sandboxie-box{player_id}"
            cprint(f"ERROR: {flag} is empty and TS3W is already running -- a native second "
                   "instance would be single-instance-redirected into it. Stop the stack first "
                   "or launch inside a Sandboxie box.", C.RED)
            if args.json_mode:
                print(json.dumps({"ok": False, "verb": f"launch-client{player_id}",
                                  "reason": "empty_box_redirect_hazard"}))
            raise SystemExit(1)

    attach_pid = os.environ.get("SIMSONLINE_CLIENT_ATTACH_PID", "")
    attaching = attach_pid.isdecimal() and int(attach_pid) > 0
    # An interrupted boot can leave a box-owned game absent from every PID/state file.
    # All fresh launch producers verify that box before deleting ownership or spawning.
    if not attaching and not stop_client_sandbox(args, plat, player_id):
        if args.json_mode:
            print(json.dumps({"ok": False, "verb": f"launch-client{player_id}",
                              "reason": "sandbox_cleanup_failed"}))
        raise SystemExit(1)

    if not skip_orphan_kill:
        # Orphan guard: kill any TS3 that isn't a tracked or stack-owned TS3
        protected = set(get_protected_server_ts3_pids())
        for other in CLIENT_PLAYERS[1:]:  # the other players' games (only client 1's launch runs the orphan guard)
            other_ts3 = get_live_pid_from_file(client_files(other)["pid"])
            if other_ts3:
                protected.add(other_ts3)
        if plat.is_linux:
            protected.update(get_stack_owned_ts3_pids())
        for orphan in get_ts3_pids():
            if orphan not in protected:
                try:
                    if plat.is_linux:
                        os.kill(orphan, signal.SIGKILL)
                    else:
                        # Windows: os.kill(..., SIGKILL) raises ValueError; taskkill /F is
                        # the documented Windows kill path.
                        subprocess.run(["taskkill", "/F", "/PID", str(orphan)],
                                       capture_output=True)
                    cprint(f"killed orphaned TS3 ({orphan})")
                    time.sleep(2)
                except (ProcessLookupError, PermissionError, OSError):
                    pass

    state_file, log_file, snapshot_log = files["state"], files["log"], files["snapshot"]
    bridge_log, pid_file = files["bridge_log"], files["pid"]
    wineprefix = client_wineprefix(args, player_id)

    for path in [pid_file] + ([] if attaching else [pid_file + ".identity.json"]):
        try:
            if os.path.exists(path):
                os.remove(path)
        except OSError:
            pass
    save_run_logs()
    for f in [log_file, snapshot_log, bridge_log, state_file]:
        try:
            if os.path.exists(f):
                os.remove(f)
        except OSError:
            pass

    cmd_args = _build_client_args(args, player_id, bridgectl, wineprefix)
    role = files["role"]
    _launch_procs[role] = launch_controller(cmd_args, role, log_file, REPO)
    if args.json_mode:
        _emit_event("launched", "launching", f"client{player_id}", None)


def cmd_server(args, plat):
    """Launch server only."""
    if _front_door_enabled(args):
        if not ensure_mod_synced(args.skip_mod_sync):
            return 1
        launch_server(args)
        if args.detached:
            cprint(f"DETACHED: front-door server launched (log -> {SERVER_LOG})")
            return 0
        return _wait_front_door_ready(args)
    server_pids = _server_controller_pids(args, plat)
    if server_pids:
        if test_listener(args.endpoint):
            cprint("server already running")
            return 0
        cprint("WARN: SimsOnline.DedicatedServer alive but public listener not accepting -- recycling", C.YELLOW)
        for pid in server_pids:
            stop_controller(pid, graceful=True)

    if not ensure_mod_synced(args.skip_mod_sync):
        return 1

    launch_server(args)
    if args.detached:
        cprint(f"DETACHED: server controller launched (log -> {SERVER_LOG})")
        cprint("  poll: python scripts/stackctl.py wait-ready")
        return 0

    sf = StateFile(SERVER_STATE_FILE)
    ok = sf.wait_phase_ready(args.host_timeout, "server (TS3 boot + save load)", args.json_mode,
                             launch_proc=_launch_procs.get("server"))
    if ok:
        pin_ts3_pid_from_state(SERVER_STATE_FILE, SERVER_TS3_PID_FILE)
        if args.minimize_server_ts3:
            minimize_server_ts3()
    return 0 if ok else 1


def cmd_client(args, plat):
    """Launch client1 mirror."""
    ensure_client_wineprefix(args.client_wineprefix)

    if test_listener(CLIENT_BRIDGECTL):
        cprint("client already running")
        return 0

    launch_client(args, player_id=1, skip_orphan_kill=True)
    if args.detached:
        cprint(f"DETACHED: client controller launched (log -> {CLIENT_LOG})")
        cprint("  poll: python scripts/stackctl.py wait-ready --require-client")
        return 0

    sf = StateFile(CLIENT_STATE_FILE)
    ok = sf.wait_phase_ready(args.host_timeout, "client (sandboxed TS3 boot + save load)", args.json_mode,
                             launch_proc=_launch_procs.get("client"))
    if ok:
        pin_ts3_pid_from_state(CLIENT_STATE_FILE, CLIENT_TS3_PID_FILE)
    return 0 if ok else 1


def cmd_client2(args, plat):
    """Launch second mirror client (player 2)."""
    return cmd_client_n(args, plat, 2)


def cmd_client_n(args, plat, player_id):
    """Launch a later player's mirror client (2, 3, ...) against the running server."""
    files = client_files(player_id)
    role, bridgectl = files["role"], files["bridgectl"]
    ensure_client_wineprefix(client_wineprefix(args, player_id))

    if not test_listener(args.endpoint):
        cprint(f"server public listener ({args.endpoint}) not accepting -- bring the stack up first", C.RED)
        return 1

    if test_listener(bridgectl):
        cprint(f"{role} already running (:{bridgectl.rsplit(':', 1)[-1]} accepting)")
        return 0

    launch_client(args, player_id=player_id, skip_orphan_kill=True)
    if args.detached:
        cprint(f"DETACHED: {role} controller launched (log -> {files['log']})")
        return 0

    sf = StateFile(files["state"])
    ok = sf.wait_phase_ready(args.host_timeout, f"{role} (sandboxed TS3 boot + save load)", args.json_mode,
                             launch_proc=_launch_procs.get(role))
    if ok:
        pin_ts3_pid_from_state(files["state"], files["pid"])
        cprint(f"{role.upper()} UP (player {player_id}, bridgectl {bridgectl})", C.GREEN)
        if not getattr(args, f"claim_sim{player_id}", 0):
            cprint(f"claim player {player_id}'s Sim: python scripts/simctl.py --player {player_id} claim <name>")
    return 0 if ok else 1


def cmd_up(args, plat):
    """Bring up the full stack: server + client1."""
    if not ensure_mod_synced(args.skip_mod_sync):
        return 1
    if cmd_server(args, plat) != 0:
        return 1
    if args.minimize_server_ts3:
        minimize_server_ts3()
    ensure_client_wineprefix(args.client_wineprefix)
    time.sleep(args.client_launch_delay)
    if cmd_client(args, plat) != 0:
        cprint("WARN: client bring-up failed -- server is up; inspect .omo/run/client.log", C.YELLOW)
    else:
        cprint("")
        cprint("STACK UP. Logs: .omo/run/server.log / client.log", C.GREEN)
        cprint("Admin: python scripts/simctl.py sims | claim [name] | speed normal | status")
    return 0


def resolve_server_ts3_pid_for_reattach():
    """Identify the warm server TS3 pid, excluding the client mirror (DEV-192)."""
    tracked_pid = None
    tracked_alive = False
    if os.path.exists(SERVER_TS3_PID_FILE):
        try:
            with open(SERVER_TS3_PID_FILE) as f:
                tracked_pid = int(f.read().strip())
            tracked_alive = _pid_alive(tracked_pid)
        except (OSError, ValueError):
            cprint("WARN: server TS3 pid file is unreadable; refreshing from live TS3 processes", C.YELLOW)

    client_pid = get_live_pid_from_file(CLIENT_TS3_PID_FILE)
    live = get_ts3_pids()
    selected = select_server_ts3_pid_candidate(tracked_pid, tracked_alive, live, client_pid)

    if selected is not None:
        if not tracked_alive or selected != tracked_pid:
            old = f"stale pid {tracked_pid}" if tracked_pid else "missing pid file"
            cprint(f"refreshed server TS3 pid from live process list: {old} -> {selected}", C.YELLOW)
        with open(SERVER_TS3_PID_FILE, "w") as f:
            f.write(str(selected))
        return selected

    if tracked_pid and not tracked_alive:
        cprint(f"server TS3 pid file is stale (pid {tracked_pid} is not running).", C.RED)
    elif not tracked_pid:
        cprint(f"no server TS3 pid file ({SERVER_TS3_PID_FILE}).", C.RED)
    client_note = f" (client pid {client_pid} excluded)" if client_pid else ""
    cprint(f"could not identify exactly one live server TS3 from live pids: {', '.join(map(str, live))}{client_note}", C.RED)
    cprint("Use 'server' for a fresh launch or repair .omo/run/server-ts3.pid after confirming the server TS3.", C.RED)
    return None


def cmd_server_reattach(args, plat):
    """Rebuild + restart controller against the warm server TS3."""
    if not args.skip_mod_sync and not assert_mod_current_for_reattach():
        return 1

    ts3_pid = resolve_server_ts3_pid_for_reattach()
    if not ts3_pid:
        return 1
    front_door_args = _build_server_args(args, ts3_pid) if _front_door_enabled(args) else None
    for pid in _server_controller_pids(args, plat):
        stop_for_reattach(pid)
        cprint(f"stopped controller ({pid}); server TS3 ({ts3_pid}) stays warm")

    # Rebuild
    if args.build and not _front_door_enabled(args):
        cprint("building DedicatedServer...")
        result = subprocess.run(
            ["dotnet", "build", os.path.join(REPO, "DedicatedServer", "SimsOnline.DedicatedServer.csproj"),
             "-c", "Debug", "--nologo", "-v", "q"],
            capture_output=False
        )
        if result.returncode != 0:
            cprint("build FAILED -- controller is down; fix and re-run server-reattach", C.RED)
            return 1

    save_run_logs()
    for f in [SERVER_LOG, SERVER_BRIDGE_LOG, SERVER_STATE_FILE]:
        try:
            if os.path.exists(f):
                os.remove(f)
        except OSError:
            pass

    # Relaunch with --attach-existing --attach-pid
    build_arg = [] if args.build else ["--no-build"]
    cmd_args = ["dotnet", "run", "--project", "DedicatedServer"] + build_arg + ["-c", "Debug", "--",
        "--save", args.save, "--host-timeout-seconds", str(args.host_timeout),
        "--listen", args.endpoint, "--bridgectl-listen", SERVER_BRIDGECTL,
        "--sync-broadcast-seconds", "2",
        "--attach-existing", "--attach-pid", str(ts3_pid),
        "--state-file", SERVER_STATE_FILE]
    if args.player_command_timeout > 0:
        cmd_args += ["--player-command-timeout-seconds", str(args.player_command_timeout)]
    if args.bridge_trace:
        cmd_args += ["--bridge-trace", "--bridge-record", SERVER_BRIDGE_LOG]

    if front_door_args is not None:
        cmd_args = front_door_args
    _launch_procs["server"] = launch_controller(cmd_args, "server", SERVER_LOG, REPO)

    if args.detached:
        cprint(f"DETACHED: server controller re-attach launched (log -> {SERVER_LOG})")
        cprint("  poll: python scripts/stackctl.py wait-ready")
        return 0

    if _front_door_enabled(args):
        return _wait_front_door_ready(args)
    sf = StateFile(SERVER_STATE_FILE)
    ok = sf.wait_phase_ready(120, "controller re-attach (beacon scan + bridge handshake)", args.json_mode,
                             launch_proc=_launch_procs.get("server"))
    if ok:
        cprint("REATTACHED. Claims were reset -- re-claim via: python scripts/simctl.py claim <id> ...", C.GREEN)
    else:
        write_reattach_failure_diagnostics(args)
    return 0 if ok else 1


def cmd_reload_mod(args, plat):
    """Rebuild + reinstall the mod."""
    reload_args = ["pwsh", "-NoProfile", "-File", os.path.join(SCRIPTS, "make-mod.ps1"), "-Reload"]
    if args.no_auto_up:
        reload_args.append("-NoAutoUp")
    result = subprocess.run(reload_args)
    if result.returncode != 0:
        return 1
    return 0


def cmd_logs(args, plat):
    """Tail stack logs."""
    logs = []
    later = {2: args.log_client2_only, 3: getattr(args, "log_client3_only", False)}
    if args.log_server_only or (not args.log_client_only and not any(later.values())):
        logs.append(("server", SERVER_LOG))
    if args.log_client_only or (not args.log_server_only and not any(later.values())):
        logs.append(("client", CLIENT_LOG))
    for n, only in later.items():
        if only:
            logs.append((client_files(n)["role"], client_files(n)["log"]))

    for label, path in logs:
        if not os.path.exists(path) or os.path.getsize(path) == 0:
            cprint(f"--- {label}.log (empty or missing) ---")
            continue
        cprint(f"--- {label}.log (last {args.log_lines}) ---")
        try:
            with open(path, "r", errors="replace") as f:
                lines = f.readlines()
            for line in lines[-args.log_lines:]:
                print(line.rstrip())
        except OSError:
            pass

    if args.follow_logs:
        cprint("(following -- Ctrl+C to stop)")
        try:
            follow_paths = {path: 0 for _, path in logs if os.path.exists(path)}
            while True:
                for path, offset in list(follow_paths.items()):
                    try:
                        with open(path, "r", errors="replace") as f:
                            f.seek(offset)
                            new_lines = f.readlines()
                            for line in new_lines:
                                print(line.rstrip())
                            follow_paths[path] = f.tell()
                    except OSError:
                        pass
                time.sleep(1)
        except KeyboardInterrupt:
            pass
    return 0


def _tracked_client_ts3_pid_for_restart():
    """Prefer the pin; use controller state when the client pin was never written.

    Only a currently running TS3 process that is neither the server nor
    client2 may be selected. Never sweep arbitrary game processes.
    """
    pid = get_live_pid_from_file(CLIENT_TS3_PID_FILE)
    if not pid:
        pid = StateFile(CLIENT_STATE_FILE).get_game_pid()
    try:
        pid = int(pid)
    except (TypeError, ValueError):
        return None
    protected = {
        StateFile(SERVER_STATE_FILE).get_game_pid(),
        get_live_pid_from_file(SERVER_TS3_PID_FILE),
    } | {StateFile(client_files(n)["state"]).get_game_pid() for n in CLIENT_PLAYERS[1:]} \
      | {get_live_pid_from_file(client_files(n)["pid"]) for n in CLIENT_PLAYERS[1:]}
    return pid if pid not in protected and pid in get_ts3_pids() else None


def _stop_client1(plat, args=None):
    """Stop only client1's controller and its tracked TS3; the server and client2 keep running."""
    # Capture the game PID before the controller/state file can disappear.
    client_ts3 = _tracked_client_ts3_pid_for_restart()
    controllers = []
    for pid in get_controller_pids_by_role("client", plat):
        if "Client" in _get_process_name(pid, plat):
            controllers.append(pid)
            stop_controller(pid, graceful=True)
    if not wait_processes_gone(controllers) or not stop_client_sandbox(args, plat, 1):
        return False
    if client_ts3 and _pid_alive(client_ts3):
        try:
            kill_process(client_ts3, plat)
            cprint(f"stopped client TS3 ({client_ts3})")
        except (ProcessLookupError, PermissionError, OSError) as exc:
            cprint(f"cleanup failed: {exc}", C.RED)
            return False
    return wait_processes_gone([client_ts3] if client_ts3 else [])


def cmd_client_restart(args, plat):
    """Recycle only client1 and its TS3, preserving the authoritative server."""
    ensure_client_wineprefix(args.client_wineprefix)
    if not _stop_client1(plat, args):
        return 1
    return cmd_client(args, plat)


def cmd_client2_down(args, plat):
    """Tear down ONLY client2 (keeps server + client1)."""
    return cmd_client_down(args, plat, 2)


def cmd_client_down(args, plat, player_id):
    """Tear down ONLY one later player's client (2, 3, ...); the server and the other clients keep running."""
    files = client_files(player_id)
    role, pid_file = files["role"], files["pid"]
    stopped = []
    # Capture ownership before stopping a controller that may remove its state.
    ts3_pids = get_pids_by_role(role) if plat.is_linux else []
    tracked = _read_pid_file(pid_file) or (StateFile(files["state"]).read() or {}).get("gamePid")
    if tracked and tracked not in ts3_pids:
        ts3_pids.append(tracked)
    controllers = []
    # Kill this player's controller only: state-file pid everywhere plus
    # SIMSONLINE_ROLE=client<N> /proc environ tag on Linux (the compiled exe doesn't
    # have "34031" in argv, only --bridgectl-listen 127.0.0.1:34031). Never sweep
    # all Client controllers — that would take down client1 too.
    for pid in get_controller_pids_by_role(role, plat):
        if "Client" in _get_process_name(pid, plat):
            controllers.append(pid)
            stop_controller(pid, graceful=True)
            stopped.append(f"{role} controller ({pid})")

    if not wait_processes_gone(controllers) or not stop_client_sandbox(args, plat, player_id):
        return 1

    for ts3_pid in ts3_pids:
        if _pid_alive(ts3_pid):
            try:
                kill_process(ts3_pid, plat)
                stopped.append(f"{role} TS3 ({ts3_pid})")
            except (ProcessLookupError, PermissionError, OSError) as exc:
                cprint(f"cleanup failed: {exc}", C.RED)
                return 1

    if not wait_processes_gone(ts3_pids):
        return 1

    if os.path.exists(pid_file):
        try:
            os.remove(pid_file)
        except OSError:
            pass

    if stopped:
        cprint(f"stopped: {', '.join(stopped)}")
    else:
        cprint(f"{role} not running")
    return 0


def minimize_server_ts3():
    """Minimize the server TS3 window (best-effort)."""
    if not os.path.exists(SERVER_TS3_PID_FILE):
        cprint("minimize: no server TS3 pid file", C.YELLOW)
        return
    try:
        with open(SERVER_TS3_PID_FILE) as f:
            ts3_pid = int(f.read().strip())
    except (OSError, ValueError):
        return

    plat = Platform()
    if plat.is_linux:
        import shutil as sh
        if not sh.which("xdotool"):
            cprint("minimize: xdotool not installed (skipping)", C.YELLOW)
            return
        for _ in range(10):
            result = subprocess.run(["xdotool", "search", "--pid", str(ts3_pid)],
                                    capture_output=True, text=True)
            if result.stdout.strip():
                for wid in result.stdout.strip().split("\n"):
                    if wid.strip():
                        subprocess.run(["xdotool", "windowminimize", wid.strip()],
                                      capture_output=True)
                cprint(f"server TS3 ({ts3_pid}) minimized via xdotool")
                return
            time.sleep(1)
        cprint("minimize: server TS3 X window not found after 10s", C.YELLOW)
    else:
        # Windows: user32 ShowWindowAsync
        import ctypes
        SW_MINIMIZE = 6
        for _ in range(10):
            hwnd = ctypes.windll.user32.FindWindowW(None, "The Sims 3")
            if hwnd:
                ctypes.windll.user32.ShowWindowAsync(hwnd, SW_MINIMIZE)
                cprint(f"server TS3 ({ts3_pid}) minimized")
                return
            time.sleep(1)
        cprint("minimize: server TS3 window not found after 10s", C.YELLOW)


# ─── Wave 3: Agent Verbs + JSON Output ───────────────────────────────────────

def cmd_launch(verb, args, plat):
    """Detached launch only — returns immediately with pid + log info."""
    # Map launch-* verbs to their operator-verb equivalents (all --detached)
    if verb == "launch-full":
        launch_server(args)
        ensure_client_wineprefix(args.client_wineprefix)
        time.sleep(2)  # brief delay so server starts first
        launch_client(args, player_id=1, skip_orphan_kill=True)
        _print_launch_result("up", args, include_client=True)
    elif verb == "launch-server":
        launch_server(args)
        _print_launch_result("server", args)
    elif verb == "launch-client":
        ensure_client_wineprefix(args.client_wineprefix)
        launch_client(args, player_id=1, skip_orphan_kill=True)
        _print_launch_result("client", args, include_client=True)
    elif verb in ("launch-client2", "launch-client3"):
        player_id = int(verb[-1])
        ensure_client_wineprefix(client_wineprefix(args, player_id))
        launch_client(args, player_id=player_id, skip_orphan_kill=True)
        _print_launch_result(f"client{player_id}", args)
    elif verb == "launch-client-restart":
        ensure_client_wineprefix(args.client_wineprefix)
        # By role, as client-restart does: sweeping every Client controller also stopped client2's.
        if not _stop_client1(plat, args):
            return 1
        launch_client(args, player_id=1, skip_orphan_kill=True)
        _print_launch_result("client-restart", args, include_client=True)
    elif verb == "launch-reattach-server":
        # Delegate to the reattach launch path (already has its own launch logic)
        cmd_server_reattach(args, plat)
    return 0


def _print_launch_result(verb_name, args, include_client=False):
    """Print launch result (JSON or human) after a detached launch."""
    if args.json_mode:
        _emit_event("launched", verb_name, "detached launch started", None)
    else:
        cprint(f"LAUNCHED {verb_name}; poll with wait-ready", C.GREEN)
        if include_client:
            cprint("  poll: python scripts/stackctl.py wait-ready --require-client")
        else:
            cprint("  poll: python scripts/stackctl.py wait-ready")


def _front_door_public_ready(endpoint, timeout):
    """A bound socket is not enough: bind-only and the C# JSON listener must not pass."""
    host, port = endpoint.rsplit(":", 1)
    try:
        with socket.create_connection((host.strip("[]"), int(port)), timeout=timeout) as sock:
            sock.sendall(b"HELLO|1|ring=1,wfs=1,role=client\n")
            with sock.makefile("rb") as reader:
                raw = reader.readline(4_194_306)
            if not raw.endswith(b"\n") or len(raw.rstrip(b"\r\n")) > 4_194_304:
                return False
            fields = raw.decode("utf-8").rstrip("\r\n").split("|", 3)
            return (len(fields) == 4 and fields[:2] == ["WELCOME", "1"]
                    and fields[2].isdigit() and 0 <= int(fields[2]) <= 0xFFFFFFFF)
    except (OSError, ValueError, UnicodeError):
        return False


def _wait_front_door_ready(args, client_endpoint=CLIENT_BRIDGECTL):
    """Bounded public-wire readiness, without C# state files or server bridgectl health."""
    started = time.monotonic()
    deadline = started + args.timeout
    interval = args.interval if args.interval > 0 else 0.25
    polls = 0
    while time.monotonic() < deadline:
        process = _launch_procs.get("server")
        if process is not None and process.poll() is not None:
            if args.json_mode:
                print(json.dumps({"ok": False, "reason": "front_door_exited", "exit_code": process.poll()}))
            else:
                cprint(f"front_door_exited: code={process.poll()} (log -> {SERVER_LOG})", C.RED)
            return 2
        polls += 1
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            break
        public_ready = _front_door_public_ready(args.endpoint, min(1.0, remaining))
        remaining = deadline - time.monotonic()
        client_ready = not args.require_client or (remaining > 0 and test_listener(
            client_endpoint, timeout_ms=max(1, int(min(1.0, remaining) * 1000))))
        if public_ready and client_ready:
            if args.json_mode:
                print(json.dumps({"ok": True, "front_door": True, "public": True,
                                  "polls": polls, "elapsed_seconds": time.monotonic() - started}))
            else:
                cprint(f"FRONT DOOR READY after {time.monotonic() - started:.1f}s ({polls} polls)", C.GREEN)
            return 0
        remaining = deadline - time.monotonic()
        if remaining > 0:
            time.sleep(min(interval, remaining))
    if args.json_mode:
        print(json.dumps({"ok": False, "reason": "front_door_timeout", "polls": polls}))
    else:
        cprint(f"front_door_timeout: no public WELCOME within {args.timeout}s (log -> {SERVER_LOG})", C.RED)
    return 1


def cmd_boot(verb, args, plat):
    """Agent-safe bounded launch + wait. Combines launch-* + wait-ready in one process."""
    # Ensure the mod package is current before launching. boot-full --build rebuilds the
    # .NET controller, but the mod (a .NET 2 assembly built by make-mod.ps1 via wine
    # csc.exe) is a separate artifact. Without this, boot-full boots TS3 with a STALE
    # mod package that predates code changes. make-mod also mirrors the package into the
    # client Wine prefixes (~/Documents only covers the server prefix via symlink).
    if not ensure_mod_synced(args.skip_mod_sync):
        return 1
    if verb in ("boot-full", "boot-server"):
        cprint(gc_watch("start", "--keep"))  # the server's GC reader from before its TS3 starts
    # First: detached launch
    launch_verb = verb.replace("boot-", "launch-")
    launch_result = cmd_launch(launch_verb, args, plat)
    if launch_result:
        return launch_result

    # Then: bounded poll
    require_client = "client" in verb and verb != "boot-server" or verb == "boot-full"
    boot_files = client_files(int(verb[-1]) if verb in ("boot-client2", "boot-client3") else 1)
    args.require_client = require_client or args.require_client

    if _front_door_enabled(args):
        return _wait_front_door_ready(args, boot_files["bridgectl"])
    # Wait for server first (it must be ready before client matters)
    sf = StateFile(SERVER_STATE_FILE)
    if verb in ("boot-full", "boot-server"):
        ok = sf.wait_phase_ready(args.timeout, "server (TS3 boot + save load)", args.json_mode,
                                 launch_proc=_launch_procs.get("server"))
        if not ok:
            launch_failed = test_devsession_launch_failed(_launch_procs.get("server"))
            return 2 if sf.is_error() or sf.is_pid_dead() or launch_failed else 1
        pin_ts3_pid_from_state(SERVER_STATE_FILE, SERVER_TS3_PID_FILE)
        if args.minimize_server_ts3:
            minimize_server_ts3()

    # Wait for client if needed
    if require_client:
        client_role = boot_files["role"]
        client_state_file = boot_files["state"]
        client_pid_file = boot_files["pid"]
        client_sf = StateFile(client_state_file)
        ok = client_sf.wait_phase_ready(
            args.timeout, f"{verb} (TS3 boot + save load)", args.json_mode,
            launch_proc=_launch_procs.get(client_role)
        )
        if not ok:
            launch_failed = test_devsession_launch_failed(_launch_procs.get(client_role))
            return 2 if client_sf.is_error() or client_sf.is_pid_dead() or launch_failed else 1
        pin_ts3_pid_from_state(client_state_file, client_pid_file)

    # Final readiness check via listeners + bridge-health
    client_endpoint = boot_files["bridgectl"]
    snap = get_readiness_snapshot(
        require_client=require_client,
        probe_timeout=args.probe_timeout,
        client_endpoint=client_endpoint,
        public_endpoint=args.endpoint,
    )
    if snap["ready"]:
        if args.json_mode:
            payload = {
                "ok": True, "verb": verb, "polls": 1,
                "elapsed_seconds": 0,
                "public": snap["public"], "server_ctl": snap["server_ctl"],
                "client_ctl": snap["client_ctl"], "require_client": require_client,
            }
            if snap["health"] and snap["health"]["stdout"]:
                try:
                    payload["health_json"] = json.loads(snap["health"]["stdout"])
                except json.JSONDecodeError:
                    payload["health_raw"] = snap["health"]["stdout"]
            print(json.dumps(payload))
        else:
            cprint(f"STACK READY ({verb})", C.GREEN)
            if snap["health"] and snap["health"]["stdout"]:
                cprint(snap["health"]["stdout"])
        return 0

    if args.json_mode:
        print(json.dumps({"ok": False, "reason": "listeners_not_ready", "verb": verb}))
    else:
        cprint(f"TIMEOUT: stack launched but listeners not all ready yet ({verb})", C.RED)
    return 1


def cmd_reattach_server(args, plat):
    """Agent-safe: launch server-reattach + bounded wait-ready."""
    # Delegate to the reattach verb (it already handles launch + detached)
    if args.build:
        args.build = True  # reattach-server defaults to building
    launch_status = cmd_server_reattach(args, plat)
    if launch_status != 0:
        return launch_status
    if args.detached or _front_door_enabled(args):
        return 0
    # Bounded wait
    sf = StateFile(SERVER_STATE_FILE)
    ok = sf.wait_phase_ready(args.timeout, "controller re-attach (beacon scan + bridge handshake)", args.json_mode,
                             launch_proc=_launch_procs.get("server"))
    if ok:
        cprint("REATTACHED. Claims were reset -- re-claim via: python scripts/simctl.py claim <id> ...", C.GREEN)
    else:
        write_reattach_failure_diagnostics(args)
    return 0 if ok else (2 if sf.is_error() or sf.is_pid_dead() else 1)


# ─── Script-Level Test Functions ──────────────────────────────────────────────
# Ported from dev-session-script-tests.ps1 to test stackctl.py's core logic.

def select_server_ts3_pid_candidate(tracked_pid, tracked_pid_alive, live_ts3_pids, client_ts3_pid):
    """Select the server TS3 pid from live candidates, mirroring the old Select-ServerTs3PidCandidate.
    - If tracked pid is alive, keep it (unless it's the client pid).
    - If tracked pid is stale, replace with the only live candidate that isn't the client.
    - If multiple untracked candidates, reject (ambiguous).
    """
    if tracked_pid and tracked_pid_alive and tracked_pid in live_ts3_pids:
        if client_ts3_pid and tracked_pid == client_ts3_pid:
            # tracked pid is actually the client — fall through to candidate selection
            pass
        else:
            return tracked_pid

    # Filter out the client pid from candidates
    candidates = [p for p in live_ts3_pids if p != client_ts3_pid]
    if len(candidates) == 1:
        return candidates[0]
    if len(candidates) == 0:
        return None
    # Ambiguous — reject
    return None


def test_devsession_launch_failed(process):
    """True if a detached launch process exited non-zero."""
    if process is None:
        return False
    if not hasattr(process, "poll"):
        return False
    rc = process.poll()
    if rc is None:
        return False  # still running
    return rc != 0


def test_devsession_launch_pending(process):
    """True if a detached launch process is still running."""
    if process is None:
        return False
    if not hasattr(process, "poll"):
        return False
    return process.poll() is None


def get_reattach_bridge_trace_diagnostic(bridge_record_path):
    """Parse a bridge-record log and return diagnostic lines for reattach failures.
    Matches the old Get-ReattachBridgeTraceDiagnostic logic.
    """
    if not os.path.exists(bridge_record_path):
        return ["DIAG: BridgeTrace requested but bridge-record file was not written."]

    try:
        with open(bridge_record_path, "r", errors="replace") as f:
            trace = f.read()
    except OSError:
        return ["DIAG: could not read bridge-record file."]

    connect_count = len(re.findall(r"CONNECT\|", trace))
    hello_count = len(re.findall(r"HELLO\|", trace))

    if connect_count > 0 and hello_count == 0:
        return [
            "DIAG: BridgeTrace saw CONNECT| but no HELLO| before reattach timeout.",
            "DIAG: warm TS3 may not have re-armed the bridge marker or the mod pump may be wedged; inspect .omo/run/server-bridge.log and server.log.",
        ]
    if connect_count == 0:
        return ["DIAG: BridgeTrace did not record CONNECT|; attach scan likely never found the bridge marker."]
    return ["DIAG: BridgeTrace recorded CONNECT| and HELLO|; timeout occurred after handshake."]


def run_script_tests():
    """Run the script-level regression tests. Returns (total, failed)."""
    import subprocess as sp
    total = 0
    failed = 0

    def test(name, body):
        nonlocal total, failed
        total += 1
        try:
            body()
            print(f"PASS {name}")
        except AssertionError as e:
            print(f"FAIL {name}: {e}")
            failed += 1
        except Exception as e:
            print(f"FAIL {name}: {e}")
            failed += 1

    def assert_eq(expected, actual, msg=""):
        if expected != actual:
            raise AssertionError(f"{msg} expected={expected!r} actual={actual!r}")

    def assert_true(val, msg=""):
        if not val:
            raise AssertionError(msg)

    def assert_false(val, msg=""):
        if val:
            raise AssertionError(msg)

    def front_door_args():
        from unittest.mock import patch
        args = Args()
        with patch.dict(os.environ, {"SIMSONLINE_FRONT_DOOR": "0"}), \
             patch(__name__ + ".installed_slot", return_value=None), \
             patch(__name__ + ".os.path.isfile", return_value=True):
            server_off = _build_server_args(args)
            client_off = _build_client_args(args)
            args.front_door = False
            assert_eq(server_off, _build_server_args(args), "Explicit off must be byte-identical.")
            assert_eq(client_off, _build_client_args(args), "Explicit off client must be byte-identical.")
            args.front_door = True
            args.front_door_bin = "test-front-door.exe"
            with patch(__name__ + ".resolve_server_ts3_pid_for_reattach", return_value=123):
                assert_eq(["test-front-door.exe", "--pid", "123", "--serve", "--public", args.endpoint],
                          _build_server_args(args))
            assert_true("--wire" in _build_client_args(args))
            assert_eq("line", _build_client_args(args)[_build_client_args(args).index("--wire") + 1])
        assert_eq("simlink_headless_server", _classify_controller_cmdline("simlink_headless_server.exe --attach-pid 123", True))
    test("FrontDoor.OptInArgsAndFlagOffIdentity", front_door_args)

    def flag_off_golden_argv():
        from unittest.mock import patch
        args = Args()
        args.front_door = False
        args.ts3_path = CONTROLLER_DEFAULT_TS3_PATH
        args.ts3_path_explicit, args.bridge_trace, args.build = False, False, False
        args.player_command_timeout, args.claim_sim, args.sandboxie_box = 0, 0, ""
        files = client_files(1)
        # Pre-card-57 argument vectors. Comparing two calls to the new builder alone
        # would miss an accidental unconditional --wire or front-door argument.
        expected_server = [
            "dotnet", "run", "--project", "DedicatedServer", "--no-build", "-c", "Debug", "--",
            "--save", args.save, "--host-timeout-seconds", str(args.host_timeout),
            "--listen", args.endpoint, "--bridgectl-listen", SERVER_BRIDGECTL,
            "--sync-broadcast-seconds", "2", "--state-file", SERVER_STATE_FILE,
        ]
        expected_client = [
            "dotnet", "run", "--project", "SimsOnline.Client", "--no-build", "-c", "Debug", "--",
            "--endpoint", args.endpoint, "--player", "1", "--display", "dev-session",
            "--role", "client", "--enable-mirror", "--client-save", args.save,
            "--client-host-timeout", str(args.host_timeout), "--client-bridgectl-listen", CLIENT_BRIDGECTL,
            "--snapshot-log", files["snapshot"], "--state-file", files["state"],
            "--connect-timeout", "60", "--client-ts3", args.ts3_path,
        ]
        with patch(__name__ + ".installed_slot", return_value=None), \
             patch.dict(os.environ, {"SIMSONLINE_WORLD_FROM_SERVER": "0", "SIMSONLINE_FRONT_DOOR": "0"}), \
             patch(__name__ + ".Platform") as platform:
            platform.return_value.is_linux = False
            assert_eq(expected_server, _build_server_args(args))
            assert_eq(expected_client, _build_client_args(args))
    test("FrontDoor.FlagOffMatchesLegacyArgvBytes", flag_off_golden_argv)

    def front_door_launch():
        from unittest.mock import patch
        args = Args()
        args.front_door, args.front_door_bin = True, "test-front-door.exe"
        with patch(__name__ + ".get_controller_pids", return_value=[]), \
             patch.dict(_launch_procs, {}, clear=True), \
             patch(__name__ + ".resolve_server_ts3_pid_for_reattach", return_value=456), \
             patch(__name__ + ".save_run_logs"), \
             patch(__name__ + ".os.path.isfile", return_value=True), \
             patch(__name__ + ".os.path.exists", return_value=False), \
             patch(__name__ + ".launch_controller") as launch:
            launch_server(args)
            assert_eq(["test-front-door.exe", "--pid", "456", "--serve", "--public", args.endpoint],
                      launch.call_args.args[0])
    test("FrontDoor.LaunchesAttachedRustBinary", front_door_launch)

    def front_door_env():
        from unittest.mock import patch
        for value, expected in [("", False), ("0", False), ("true", False), ("1", True)]:
            with patch.dict(os.environ, {"SIMSONLINE_FRONT_DOOR": value, "SIMSONLINE_FRONT_DOOR_BIN": "env-server.exe"}), \
                 patch.object(sys, "argv", ["stackctl.py", "launch-server"]), \
                 patch(__name__ + ".launch_fork_census", return_value={"state": "fork"}), \
                 patch.object(fork_census, "write_evidence"), \
                 patch(__name__ + ".sync_ts3_pid_files"), \
                 patch(__name__ + ".cmd_launch", return_value=0) as launch:
                assert_eq(0, main())
                assert_eq(expected, launch.call_args.args[1].front_door)
                assert_eq("env-server.exe", launch.call_args.args[1].front_door_bin)
        with patch.dict(os.environ, {"SIMSONLINE_FRONT_DOOR": "0"}), \
             patch.object(sys, "argv", ["stackctl.py", "launch-server", "--front-door", "--front-door-bin", "cli.exe"]), \
             patch(__name__ + ".launch_fork_census", return_value={"state": "fork"}), \
             patch.object(fork_census, "write_evidence"), \
             patch(__name__ + ".sync_ts3_pid_files"), \
             patch(__name__ + ".cmd_launch", return_value=0) as launch:
            main()
            assert_true(launch.call_args.args[1].front_door)
            assert_eq("cli.exe", launch.call_args.args[1].front_door_bin)
    test("FrontDoor.EnvRequiresExactOptInAndCliWins", front_door_env)

    def front_door_process_isolation():
        from types import SimpleNamespace
        from unittest.mock import patch
        plat = SimpleNamespace(is_linux=True)
        for enabled in (False, True):
            for accepting in (False, True):
                args = Args()
                args.front_door, args.detached, args.skip_mod_sync = enabled, True, True
                with patch.dict(os.environ, {"SIMSONLINE_FRONT_DOOR": "0"}), \
                     patch.dict(_launch_procs, {}, clear=True), \
                     patch(__name__ + ".Platform", return_value=plat), \
                     patch(__name__ + ".get_controller_pids", return_value=[431]), \
                     patch(__name__ + "._get_process_name", return_value="simlink_headless_server"), \
                     patch(__name__ + ".test_listener", return_value=accepting), \
                     patch(__name__ + ".save_run_logs"), \
                     patch(__name__ + ".os.path.exists", return_value=False), \
                     patch(__name__ + "._build_server_args", return_value=["selected-server"]), \
                     patch(__name__ + ".ensure_mod_synced", return_value=True), \
                     patch(__name__ + ".stop_controller") as stop, \
                     patch(__name__ + ".launch_controller") as launch:
                    launch_server(args)
                    assert_eq(enabled and not accepting, stop.called, "Rust recycle must be opt-in.")
                    assert_eq(not (enabled and accepting), launch.called, "Off must not adopt a Rust listener.")
                    stop.reset_mock()
                    launch.reset_mock()
                    assert_eq(0, cmd_server(args, plat))
                    assert_eq(enabled and not accepting, stop.called)
                    assert_eq(not (enabled and accepting), launch.called)
            for command in (cmd_down, cmd_stop_controller):
                args.front_door = enabled
                with patch.dict(os.environ, {"SIMSONLINE_FRONT_DOOR": "0"}), \
                     patch(__name__ + ".get_controller_pids", return_value=[431]), \
                     patch(__name__ + "._get_process_name", return_value="simlink_headless_server"), \
                     patch(__name__ + ".claim_holder", return_value=None), \
                     patch(__name__ + ".wait_for_dumps_before_down", return_value=None), \
                     patch(__name__ + ".get_stack_owned_ts3_pids", return_value=[]), \
                     patch(__name__ + ".sync_ts3_pid_files"), \
                     patch(__name__ + ".gc_watch", return_value="no reader running"), \
                     patch(__name__ + ".stop_controller") as stop:
                    assert_eq(0, command(args, plat))
                    assert_eq(enabled, stop.called, command.__name__ + " must respect the opt-in.")
    test("FrontDoor.RustProcessesAreNotAdoptedOrStoppedWhenOff", front_door_process_isolation)

    def front_door_classification_isolation():
        from unittest.mock import patch
        with patch.dict(os.environ, {"SIMSONLINE_FRONT_DOOR": "0"}):
            assert_eq(None, _classify_controller_cmdline("simlink_headless_server.exe --attach-pid 1"))
            assert_eq("simlink_headless_server",
                      _classify_controller_cmdline("/tmp/simlink_headless_server --attach-pid 1", True))
            assert_eq(None, _classify_controller_cmdline("cargo run --bin simlink_headless_server", True))
    test("FrontDoor.ClassificationRequiresOptInAndAnExecutable", front_door_classification_isolation)

    def front_door_discovery_isolation():
        from types import SimpleNamespace
        from unittest.mock import patch
        for enabled in (False, True):
            def windows_list(command, **kwargs):
                return SimpleNamespace(returncode=0, stdout='"simlink_headless_server.exe","431"\n'
                                       if "IMAGENAME eq simlink_headless_server.exe" in command else "")
            with patch.object(sys, "platform", "win32"), patch("subprocess.run", side_effect=windows_list) as listing:
                assert_eq([431] if enabled else [], get_controller_pids(enabled))
                assert_eq(enabled, any("IMAGENAME eq simlink_headless_server.exe" in call.args[0]
                                       for call in listing.call_args_list))
        def linux_list(command, **kwargs):
            return SimpleNamespace(returncode=0, stdout="431\n" if command[-1] == "simlink_headless_server" else "")
        with patch.object(sys, "platform", "linux"), patch("subprocess.run", side_effect=linux_list), \
             patch(__name__ + "._read_process_cmdline", return_value="cargo run --bin simlink_headless_server"):
            assert_eq([], get_controller_pids(True), "The cargo parent is not a controller.")
    test("FrontDoor.DiscoveryExcludesRustWhenOffAndCargoParents", front_door_discovery_isolation)

    def front_door_reattach_restarts():
        from types import SimpleNamespace
        from unittest.mock import patch
        args = Args()
        args.front_door, args.detached, args.skip_mod_sync = True, True, False
        plat = SimpleNamespace(is_linux=True)
        with patch.dict(_launch_procs, {}, clear=True), \
             patch(__name__ + ".Platform", return_value=plat), \
             patch(__name__ + ".assert_mod_current_for_reattach", return_value=False) as gate, \
             patch(__name__ + ".resolve_server_ts3_pid_for_reattach", return_value=123), \
             patch(__name__ + ".get_controller_pids", return_value=[431]), \
             patch(__name__ + "._get_process_name", return_value="simlink_headless_server"), \
             patch(__name__ + ".test_listener", return_value=True), \
             patch(__name__ + ".save_run_logs"), \
             patch(__name__ + ".os.path.isfile", return_value=True), \
             patch(__name__ + ".os.path.exists", return_value=False), \
             patch(__name__ + ".stop_for_reattach") as stop, \
             patch(__name__ + ".launch_controller") as launch:
            assert_eq(1, cmd_server_reattach(args, plat), "Front-door reattach must run the warm-mod gate.")
            gate.assert_called_once()
            launch.assert_not_called()
            args.skip_mod_sync = True
            assert_eq(0, cmd_server_reattach(args, plat))
            stop.assert_called_once_with(431)
            launch.assert_called_once()
            assert_true("--pid" in launch.call_args.args[0])
    test("FrontDoor.ReattachChecksModAndRestartsAnAcceptingServer", front_door_reattach_restarts)

    def front_door_resident_argv():
        from unittest.mock import patch
        args = Args()
        args.front_door = True
        executable = "simlink_headless_server.exe" if os.name == "nt" else "simlink_headless_server"
        target = os.path.join(REPO, "test-cargo-output")
        release = os.path.join(target, "release", executable)
        debug = os.path.join(target, "debug", executable)
        with patch.dict(os.environ, {"SIMSONLINE_FRONT_DOOR_BIN": "", "CARGO_TARGET_DIR": target}), \
             patch(__name__ + ".resolve_server_ts3_pid_for_reattach", return_value=123), \
             patch(__name__ + ".os.path.isfile", side_effect=lambda path: path in (release, debug)):
            assert_eq([release, "--pid", "123", "--serve", "--public", args.endpoint], _build_server_args(args))
        with patch.dict(os.environ, {"SIMSONLINE_FRONT_DOOR_BIN": "", "CARGO_TARGET_DIR": target}), \
             patch(__name__ + ".os.path.isfile", side_effect=lambda path: path == debug):
            assert_eq(debug, _build_server_args(args, 123)[0])
        args.front_door_bin = "explicit-front-door.exe"
        with patch.dict(os.environ, {"SIMSONLINE_FRONT_DOOR_BIN": "env-front-door.exe"}), \
             patch(__name__ + ".os.path.isfile", return_value=True):
            assert_eq(["explicit-front-door.exe", "--pid", "123", "--serve", "--public", args.endpoint],
                      _build_server_args(args, 123))
            args.front_door_bin = None
            assert_eq("env-front-door.exe", _build_server_args(args, 123)[0])
    test("FrontDoor.ResidentArgvUsesActualPidFlagAndBuiltTarget", front_door_resident_argv)

    def front_door_missing_binary():
        from unittest.mock import patch
        args = Args()
        args.front_door, args.front_door_bin = True, "missing-front-door.exe"
        with patch(__name__ + ".os.path.isfile", return_value=False), \
             patch(__name__ + ".resolve_server_ts3_pid_for_reattach") as resolve, \
             patch("subprocess.run") as build:
            try:
                _build_server_args(args)
            except SystemExit as error:
                assert_true("front_door_binary_missing" in str(error))
                assert_true("cargo build --manifest-path" in str(error))
            else:
                raise AssertionError("Missing front-door binary must fail before launch.")
            resolve.assert_not_called()
            build.assert_not_called()
    test("FrontDoor.MissingBinaryNamesExplicitBuildCommand", front_door_missing_binary)

    def front_door_readiness_poll():
        from types import SimpleNamespace
        from unittest.mock import patch
        args = Args()
        args.front_door, args.timeout, args.interval = True, 3, 1
        for replies, expected in [([False, False, True], 0), ([False, False, False], 1)]:
            clock = [0.0]
            def advance(seconds):
                assert_true(0 < seconds <= 1, "Poll delay is bounded by the remaining deadline.")
                clock[0] += seconds
            with patch.dict(_launch_procs, {"server": SimpleNamespace(poll=lambda: None)}, clear=True), \
                 patch(__name__ + ".time.monotonic", side_effect=lambda: clock[0]), \
                 patch(__name__ + ".time.sleep", side_effect=advance), \
                 patch(__name__ + "._front_door_public_ready", side_effect=replies) as probe, \
                 patch(__name__ + ".StateFile", side_effect=AssertionError("Rust has no C# state file")), \
                 patch(__name__ + ".invoke_bridge_health", side_effect=AssertionError("Rust has no C# bridge health")):
                assert_eq(expected, _wait_front_door_ready(args))
                assert_eq(3, probe.call_count)
        with patch.dict(_launch_procs, {"server": SimpleNamespace(poll=lambda: 0)}, clear=True), \
             patch(__name__ + "._front_door_public_ready") as probe:
            assert_eq(2, _wait_front_door_ready(args), "A resident process exiting 0 is still a boot failure.")
            probe.assert_not_called()
    test("FrontDoor.ReadinessPollsPublicWireWithinDeadline", front_door_readiness_poll)

    def front_door_readiness_routes():
        from types import SimpleNamespace
        from unittest.mock import patch
        args = Args()
        args.front_door, args.skip_mod_sync, args.detached = True, True, False
        plat = SimpleNamespace(is_linux=True)
        with patch.dict(_launch_procs, {}, clear=True), \
             patch(__name__ + ".ensure_mod_synced", return_value=True), \
             patch(__name__ + ".gc_watch", return_value="no reader running"), \
             patch(__name__ + ".cmd_launch", return_value=0), \
             patch(__name__ + ".launch_server"), \
             patch(__name__ + ".get_controller_pids", return_value=[]), \
             patch(__name__ + "._wait_front_door_ready", return_value=0) as ready, \
             patch(__name__ + ".StateFile", side_effect=AssertionError("Front-door readiness must not read C# state")), \
             patch(__name__ + ".get_readiness_snapshot", side_effect=AssertionError("No C# readiness for Rust")):
            assert_eq(0, cmd_boot("boot-server", args, plat))
            assert_eq(0, cmd_server(args, plat))
            assert_eq(0, cmd_wait_ready(args, plat))
            assert_eq(3, ready.call_count)
            with patch(__name__ + ".cmd_server_reattach", return_value=0):
                assert_eq(0, cmd_reattach_server(args, plat))
    test("FrontDoor.BootServerAndWaitReadyBypassCSharpState", front_door_readiness_routes)

    def front_door_hello_probe():
        import io
        from unittest.mock import patch
        class Peer:
            def __init__(self, response):
                self.response, self.sent = response, None
            def __enter__(self):
                return self
            def __exit__(self, *args):
                return False
            def sendall(self, data):
                self.sent = data
            def makefile(self, mode):
                return io.BytesIO(self.response)
        for response, expected in [(b"WELCOME|1|42|\n", True), (b'{"$type":"server_error"}\n', False),
                                   (b"ERR|0|hello_rejected|HELLO|\n", False)]:
            peer = Peer(response)
            with patch(__name__ + ".socket.create_connection", return_value=peer):
                assert_eq(expected, _front_door_public_ready("127.0.0.1:34019", 1))
                assert_eq(b"HELLO|1|ring=1,wfs=1,role=client\n", peer.sent)
    test("FrontDoor.ReadinessRequiresWelcomeOnPublicPort", front_door_hello_probe)

    # Test 1: SelectServerTs3Pid.RefreshesStalePidFromOnlyLiveCandidate
    def t1():
        selected = select_server_ts3_pid_candidate(
            tracked_pid=87852, tracked_pid_alive=False,
            live_ts3_pids=[111000], client_ts3_pid=None)
        assert_eq(111000, selected, "Stale tracked pid should be replaced by the only live TS3 candidate.")
    test("SelectServerTs3Pid.RefreshesStalePidFromOnlyLiveCandidate", t1)

    # Test 2: SelectServerTs3Pid.ExcludesTrackedClientPid
    def t2():
        selected = select_server_ts3_pid_candidate(
            tracked_pid=87852, tracked_pid_alive=False,
            live_ts3_pids=[111000, 222000], client_ts3_pid=222000)
        assert_eq(111000, selected, "Client TS3 pid must not be selected as the server host.")
    test("SelectServerTs3Pid.ExcludesTrackedClientPid", t2)

    # Test 3: SelectServerTs3Pid.RejectsAmbiguousFallback
    def t3():
        selected = select_server_ts3_pid_candidate(
            tracked_pid=87852, tracked_pid_alive=False,
            live_ts3_pids=[111000, 222000], client_ts3_pid=None)
        assert_eq(None, selected, "Ambiguous live TS3 candidates should be rejected.")
    test("SelectServerTs3Pid.RejectsAmbiguousFallback", t3)

    # Test 4: SelectServerTs3Pid.KeepsLiveTrackedPid
    def t4():
        selected = select_server_ts3_pid_candidate(
            tracked_pid=87852, tracked_pid_alive=True,
            live_ts3_pids=[87852, 111000], client_ts3_pid=111000)
        assert_eq(87852, selected, "A live tracked server pid should remain selected.")
    test("SelectServerTs3Pid.KeepsLiveTrackedPid", t4)

    # Test 5: DevSessionLaunchFailureIsFatal
    class FakeProc:
        def __init__(self, has_exited, exit_code):
            self._rc = exit_code if has_exited else None
        def poll(self):
            return self._rc

    def t5():
        assert_true(test_devsession_launch_failed(FakeProc(True, 1)), "Exited nonzero launch process should be fatal.")
        assert_false(test_devsession_launch_failed(FakeProc(True, 0)), "Exited zero launch process should not be fatal.")
        assert_false(test_devsession_launch_failed(FakeProc(False, None)), "Running launch process should not be fatal.")
        assert_false(test_devsession_launch_failed(None), "Missing launch process should not be fatal.")
    test("DevSessionLaunchFailureIsFatal", t5)

    # Test 6: DevSessionLaunchPendingBlocksReadiness
    def t6():
        assert_true(test_devsession_launch_pending(FakeProc(False, None)), "Running launch process should block readiness checks.")
        assert_false(test_devsession_launch_pending(FakeProc(True, 0)), "Exited zero launch process should allow readiness checks.")
        assert_false(test_devsession_launch_pending(FakeProc(True, 1)), "Exited nonzero launch process should be handled by launch-failed gate.")
        assert_false(test_devsession_launch_pending(None), "No launch process should not block status-only readiness checks.")
    test("DevSessionLaunchPendingBlocksReadiness", t6)

    # Test 7: BridgeTraceDiagnosesConnectWithoutHello
    def t7():
        import tempfile
        with tempfile.NamedTemporaryFile(mode="w", suffix=".log", delete=False) as tmp:
            tmp.write("ctl->host seq=19 CONNECT|00000000000000000000000000000000|19\n")
            tmp_path = tmp.name
        try:
            diagnostic = get_reattach_bridge_trace_diagnostic(tmp_path)
            assert_true("CONNECT| but no HELLO|" in diagnostic[0], "CONNECT without HELLO should be diagnosed explicitly.")
        finally:
            os.remove(tmp_path)
    test("BridgeTraceDiagnosesConnectWithoutHello", t7)

    # Test 8: Linux comm truncation — /proc/PID/comm is 15 chars ("SimsOnline.Dedi")
    def t8():
        dotnet_server = "dotnet run --project DedicatedServer --no-build -c Debug -- --save test.sims3"
        binary_server = "/path/SimsOnline.DedicatedServer --save test.sims3"
        dotnet_client = "dotnet run --project SimsOnline.Client --no-build -c Debug -- --endpoint 127.0.0.1:34019"
        binary_client = "/path/SimsOnline.Client --endpoint 127.0.0.1:34019"
        assert_eq("SimsOnline.DedicatedServer", _classify_controller_cmdline(dotnet_server))
        assert_eq("SimsOnline.DedicatedServer", _classify_controller_cmdline(binary_server))
        assert_eq("SimsOnline.Client", _classify_controller_cmdline(dotnet_client))
        assert_eq("SimsOnline.Client", _classify_controller_cmdline(binary_client))
        # Truncated comm alone must NOT match — cmdline classification is required.
        assert_eq(None, _classify_controller_cmdline("SimsOnline.Dedi"))
        truncated_comm = "SimsOnline.Dedi"
        assert_false("DedicatedServer" in truncated_comm)
    test("ControllerCmdlineClassifiesTruncatedComm", t8)

    def t9():
        global assert_mod_current_for_reattach, resolve_server_ts3_pid_for_reattach
        global cmd_server_reattach, StateFile
        from types import SimpleNamespace

        original_gate = assert_mod_current_for_reattach
        original_resolve = resolve_server_ts3_pid_for_reattach
        original_launch = cmd_server_reattach
        original_state_file = StateFile
        calls = {"gate": 0, "resolve": 0}

        def reject_warm_mod():
            calls["gate"] += 1
            return False

        def no_server_pid():
            calls["resolve"] += 1
            return None

        class UnexpectedStateFile(StateFile):
            def __init__(self, path):
                super().__init__(path)
                raise AssertionError("Failed launch must return before polling controller state.")

        try:
            assert_mod_current_for_reattach = reject_warm_mod
            resolve_server_ts3_pid_for_reattach = no_server_pid
            default_args = SimpleNamespace(skip_mod_sync=False)
            assert_eq(1, cmd_server_reattach(default_args, "linux"))
            assert_eq(1, calls["gate"], "Default reattach must call the warm-mod gate once.")
            assert_eq(0, calls["resolve"], "Rejected warm mod must stop before pid resolution.")

            skipped_args = SimpleNamespace(skip_mod_sync=True)
            assert_eq(1, cmd_server_reattach(skipped_args, "linux"))
            assert_eq(1, calls["gate"], "--skip-mod-sync must bypass the warm-mod gate.")
            assert_eq(1, calls["resolve"], "Skipped warm-mod gate must continue to pid resolution.")

            def failed_reattach(args, plat):
                return 1

            cmd_server_reattach = failed_reattach
            StateFile = UnexpectedStateFile
            wrapper_args = SimpleNamespace(build=False, detached=False)
            assert_eq(1, cmd_reattach_server(wrapper_args, "linux"),
                      "A failed reattach launch must return before polling stale controller state.")
        finally:
            assert_mod_current_for_reattach = original_gate
            resolve_server_ts3_pid_for_reattach = original_resolve
            cmd_server_reattach = original_launch
            StateFile = original_state_file
    test("ReattachServer.EnforcesSkipAndLaunchFailureAtCommandBoundary", t9)

    def t10():
        global test_listener, invoke_bridge_health

        original_listener = test_listener
        original_health = invoke_bridge_health
        calls = []

        def accepting_listener(endpoint, timeout_ms=600):
            calls.append(("listener", endpoint))
            return True

        def healthy_bridge(include_client, timeout_sec=15, client_endpoint=CLIENT_BRIDGECTL):
            calls.append(("health", client_endpoint))
            return {"exit_code": 0, "stdout": ""}

        try:
            # Given: every listener is healthy and client2 is the selected mirror endpoint.
            test_listener = accepting_listener
            invoke_bridge_health = healthy_bridge

            # When: the readiness snapshot is requested for client2.
            snapshot = get_readiness_snapshot(
                require_client=True,
                probe_timeout=7,
                client_endpoint=CLIENT2_BRIDGECTL,
                public_endpoint="127.0.0.1:34999",
            )

            # Then: both the listener and health probe target client2, never client1.
            assert_true(snapshot["ready"], "Healthy selected endpoints should be ready.")
            assert_true(("listener", CLIENT2_BRIDGECTL) in calls,
                        "Readiness must probe the selected client listener.")
            assert_true(("health", CLIENT2_BRIDGECTL) in calls,
                        "Bridge health must probe the selected client endpoint.")
            assert_true(("listener", "127.0.0.1:34999") in calls,
                        "Readiness must probe the selected public endpoint.")
            assert_false(("listener", CLIENT_BRIDGECTL) in calls,
                         "Client1 must not satisfy client2 readiness.")
        finally:
            test_listener = original_listener
            invoke_bridge_health = original_health
    test("ReadinessSnapshot.UsesSelectedClientEndpoint", t10)

    def t11():
        global ensure_mod_synced, cmd_launch, StateFile
        global pin_ts3_pid_from_state, get_readiness_snapshot
        from types import SimpleNamespace

        original_sync = ensure_mod_synced
        original_launch = cmd_launch
        original_state_file = StateFile
        original_pin = pin_ts3_pid_from_state
        original_snapshot = get_readiness_snapshot
        selected_endpoints = []

        class ReadyStateFile:
            def __init__(self, path):
                self.path = path

            def wait_phase_ready(self, timeout, label, json_mode, launch_proc=None):
                return True

        def ready_snapshot(
            require_client,
            probe_timeout=15,
            client_endpoint=CLIENT_BRIDGECTL,
            public_endpoint=PUBLIC_ENDPOINT,
        ):
            selected_endpoints.append(client_endpoint)
            return {
                "ready": True,
                "public": True,
                "server_ctl": True,
                "client_ctl": True,
                "health": None,
                "health_ok": True,
            }

        try:
            # Given: client2 launches and reaches its controller-ready state.
            def synced_mod(skip_mod_sync):
                return True

            def successful_launch(verb, args, plat):
                return 0

            def pinned_ts3(state_file, pid_file):
                return 1

            ensure_mod_synced = synced_mod
            cmd_launch = successful_launch
            StateFile = ReadyStateFile
            pin_ts3_pid_from_state = pinned_ts3
            get_readiness_snapshot = ready_snapshot
            args = SimpleNamespace(
                skip_mod_sync=True,
                require_client=False,
                timeout=1,
                json_mode=False,
                probe_timeout=1,
                minimize_server_ts3=False,
                endpoint=PUBLIC_ENDPOINT,
            )

            # When: boot-client2 performs its final readiness gate.
            result = cmd_boot("boot-client2", args, None)

            # Then: only the configured client2 endpoint can satisfy that gate.
            assert_eq(0, result, "A healthy client2 boot should succeed.")
            assert_eq([CLIENT2_BRIDGECTL], selected_endpoints,
                      "boot-client2 must select the client2 bridgectl endpoint.")
        finally:
            ensure_mod_synced = original_sync
            cmd_launch = original_launch
            StateFile = original_state_file
            pin_ts3_pid_from_state = original_pin
            get_readiness_snapshot = original_snapshot
    test("BootClient2.FinalReadinessSelectsClient2Endpoint", t11)

    def t12():
        import tempfile

        # Given: all configured stack endpoints are definitively non-listening.
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as unavailable:
            unavailable.bind(("127.0.0.1", 0))
            endpoint = f"127.0.0.1:{unavailable.getsockname()[1]}"
            with tempfile.TemporaryDirectory() as run_dir:
                env = dict(os.environ)
                env["SIMSONLINE_RUN_DIR"] = run_dir
                env["SIMSONLINE_SERVER_BRIDGECTL"] = endpoint
                env["SIMSONLINE_CLIENT_BRIDGECTL"] = endpoint
                env["SIMSONLINE_CLIENT2_BRIDGECTL"] = endpoint

                # When: default and strict JSON status check the absent stack.
                default_result = sp.run(
                    [sys.executable, __file__, "status", "--quick", "--json",
                     "--endpoint", endpoint],
                    capture_output=True,
                    text=True,
                    timeout=30,
                    env=env,
                )
                strict_result = sp.run(
                    [sys.executable, __file__, "status", "--strict", "--quick", "--json",
                     "--endpoint", endpoint],
                    capture_output=True,
                    text=True,
                    timeout=30,
                    env=env,
                )

        # Then: default status remains observational while strict status fails readiness.
        assert_eq(0, default_result.returncode,
                  f"Default status must remain successful when the stack is absent. stdout={default_result.stdout!r}")
        json.loads(default_result.stdout)
        assert_eq(1, strict_result.returncode,
                  f"Strict status must fail when the server is absent. stdout={strict_result.stdout!r}")
        json.loads(strict_result.stdout)
    test("StatusStrict.FailsWhenServerAbsent", t12)

    def t13():
        from types import SimpleNamespace

        original_run = subprocess.run
        commands = []

        def capture_run(command, capture_output, text, timeout):
            commands.append(command)
            return SimpleNamespace(returncode=0, stdout="", stderr="")

        try:
            # Given: the health subprocess is captured without executing it.
            subprocess.run = capture_run

            # When: health is requested for the configured client2 bridge.
            invoke_bridge_health(
                include_client=True,
                timeout_sec=7,
                client_endpoint=CLIENT2_BRIDGECTL,
            )

            # Then: bridge-health receives client2 as its machine-consumed endpoint.
            command = commands[0]
            assert_true("--client-endpoint" in command,
                        "Bridge health must receive an explicit selected client endpoint.")
            endpoint_index = command.index("--client-endpoint") + 1
            assert_eq(CLIENT2_BRIDGECTL, command[endpoint_index])
            assert_true("--server-endpoint" in command,
                        "Bridge health must receive the configured server endpoint.")
            server_index = command.index("--server-endpoint") + 1
            assert_eq(SERVER_BRIDGECTL, command[server_index])
        finally:
            subprocess.run = original_run
    test("BridgeHealth.ForwardsSelectedClientEndpoint", t13)

    def t14():
        from types import SimpleNamespace
        args = SimpleNamespace(
            build=False, claim_sim=None, claim_sim2=None, endpoint=PUBLIC_ENDPOINT, save=DEFAULT_SAVE,
            host_timeout=180, player_command_timeout=0, bridge_trace=False, sandboxie_box="",
            sandboxie_box2="", sandboxie_start="", ts3_path=CONTROLLER_DEFAULT_TS3_PATH, ts3_path_explicit=False)
        command = _build_client_args(args)
        assert_true("--client-save" in command, "The client launch must name its save.")
        assert_true("--client-ts3" in command,
                    "--client-save without --client-ts3 makes the client refuse to start, even at the default path.")
        assert_eq(CONTROLLER_DEFAULT_TS3_PATH, command[command.index("--client-ts3") + 1])
    test("ClientArgs.AlwaysForwardTs3PathWithSave", t14)

    print(f"RESULT total={total} failed={failed}")
    return total, failed


# ─── Missing Functions (ported from dev-session.ps1) ─────────────────────────

def get_live_pid_from_file(pid_file: str):
    """Read a pid file and return the pid if the process is alive, else None."""
    if not os.path.exists(pid_file):
        return None
    try:
        with open(pid_file) as f:
            pid = int(f.read().strip())
        if _pid_alive(pid):
            return pid
    except (OSError, ValueError):
        pass
    return None


def get_tracked_windows_ts3_pids():
    """Windows: live TS3 pids recorded by this stack's controllers -- the state-file
    gamePid fields plus the *-ts3.pid pin files. The authoritative set for scoped
    teardown: `down` kills only these unless --force is passed. Only running TS3W.exe pids: an old state file's
    gamePid can be another program's pid by now (client2.state.json kept 10-13's 4212)."""
    pids = set()
    for state_file, _ in ts3_pid_files().values():
        pid = StateFile(state_file).get_game_pid()
        if pid and _pid_alive(int(pid)):
            pids.add(int(pid))
    for _, pid_file in ts3_pid_files().values():
        pid = get_live_pid_from_file(pid_file)
        if pid:
            pids.add(pid)
    return sorted(pids & set(get_ts3_pids()))


def other_checkout_run_dirs():
    """The .omo/run folders of this repository's other worktrees (stack_who.worktrees): a session that boots from
    its own worktree keeps its games' state and pid files there, not in RUN_DIR."""
    try:
        try:
            import stack_who
        except ImportError:
            stack_who = None
        from pathlib import Path
        trees = stack_who.worktrees(Path(REPO))
    except Exception:  # noqa: BLE001 - no worktree list: this checkout's own files only, as before
        return []
    mine = os.path.normcase(os.path.abspath(RUN_DIR))
    runs = [str(tree / ".omo" / "run") for tree in trees]
    return [run for run in runs if os.path.normcase(os.path.abspath(run)) != mine]


def get_other_checkout_ts3_pids():
    """Windows: {pid: run folder} for each running TS3W.exe another worktree's controllers recorded, by
    crash_catch.live_games' rule (the state file's gamePid, else its pid file, and only a TS3W.exe created after that
    controller started, so a reused pid is refused). 2026-10-22: `down` from simscope left the three games Session U
    booted from its own worktree, and its receipt counted 0: only RUN_DIR's files named games."""
    try:
        import crash_catch
    except ImportError:
        crash_catch = _MirrorProcFallback
    from pathlib import Path
    running = set(get_ts3_pids())
    found = {}
    for run in other_checkout_run_dirs():
        try:
            games = crash_catch.live_games(Path(run), game_identity)
        except Exception:  # noqa: BLE001 - one unreadable checkout never stops a down
            continue
        for game in games.values():
            if game.get("pid") in running:
                found.setdefault(int(game["pid"]), run)
    return found


def get_protected_server_ts3_pids():
    """Get the tracked server TS3 pid (from pid file or diff of live TS3 pids).
    Used by Launch-Client orphan guard to know which TS3 NOT to kill."""
    # Check tracked pid file first
    tracked = get_live_pid_from_file(SERVER_TS3_PID_FILE)
    if tracked:
        return [tracked]
    # Fall back to diffing live TS3 pids (any that appeared after server launch)
    # This is a best-effort — in the .ps1 it used $script:ServerTs3PidsBefore
    return get_ts3_pids()


def stop_zombie_client_controller(plat, player_id=1):
    """Kill this role's SimsOnline.Client if alive but its bridgectl isn't accepting.
    Role-scoped so recycling client2 can never take down a booting client1 (and
    vice versa)."""
    bridgectl, role = client_files(player_id)["bridgectl"], client_files(player_id)["role"]
    if test_listener(bridgectl):
        return
    for pid in get_controller_pids_by_role(role, plat):
        if "Client" in _get_process_name(pid, plat):
            cprint(f"WARN: SimsOnline.Client ({pid}) alive but {bridgectl} not accepting — recycling", C.YELLOW)
            try:
                from scripts.telemetry import TelemetryShim
                TelemetryShim.from_env().emit("client.stale_process_detected", {"process_id": pid, "bridge_endpoint": bridgectl, "client_role": role})
            except Exception:
                pass
            stop_controller(pid, graceful=True)


def ts3_pid_files():
    """Each role's controller state file and the pid file its TS3W pid is kept in (read at call time)."""
    return {"server": (SERVER_STATE_FILE, SERVER_TS3_PID_FILE)} | {
        client_files(n)["role"]: (client_files(n)["state"], client_files(n)["pid"]) for n in CLIENT_PLAYERS}


def game_identity(pid: int):
    """A process's (image path, creation epoch), None when it is not running (crash_catch.process_identity: query
    rights only)."""
    try:
        import crash_catch
    except ImportError:
        crash_catch = _MirrorProcFallback
    return crash_catch.process_identity(pid)


def _read_pid_file(pid_file: str):
    try:
        with open(pid_file) as f:
            return int(f.read().strip())
    except (OSError, ValueError):
        return None


def client_launch_identity(pid_file: str):
    """The immutable launch identity beside a client pid pin, or None when ownership cannot be proved."""
    try:
        with open(pid_file + ".identity.json", encoding="utf-8") as f:
            identity = json.load(f)
        if (isinstance(identity, dict) and isinstance(identity.get("pid"), int) and identity["pid"] > 0
                and isinstance(identity.get("image"), str) and ntpath.isabs(identity["image"])
                and ntpath.basename(identity["image"]).lower() == "ts3w.exe"
                and isinstance(identity.get("started"), (int, float)) and identity["started"] > 0):
            return identity
    except (OSError, ValueError):
        pass
    return None


def sync_ts3_pid_files():
    """Make each *-ts3.pid name its role's running TS3W.exe, or remove it. The game is crash_catch.live_games' pick,
    the rule its readers use: the state file's gamePid, else the pid file, and only a running TS3W.exe created after
    its controller started (a reused pid is refused; the boxed client's TS3W counts). Two roles never keep one pid: a
    dead controller's old gamePid can name another role's new game, and the newer controller keeps it. It runs at
    every verb, after down, and while a boot waits (once gamePid shows), so a boot that times out before ready, a
    detached launch, quick_loop's boots, the arms runner's reboots and a rejoin all leave the files right; a game that
    exits loses its file at the next stackctl call. Windows only (Linux tags its processes by role). Returns the
    changes, one line each."""
    if sys.platform.startswith("linux"):
        return []
    try:
        try:
            import crash_catch
        except ImportError:
            crash_catch = _MirrorProcFallback
        from pathlib import Path
        files = ts3_pid_files()
        games = crash_catch.live_games(Path(os.path.dirname(SERVER_STATE_FILE)), game_identity)
    except Exception as ex:  # noqa: BLE001 - the pid files never stop a verb
        return [f"pid files not synced ({ex})"]
    started = {role: (StateFile(state).read() or {}).get("startedUtc") or "" for role, (state, _) in files.items()}
    chosen = {role: game["pid"] for role, game in games.items() if game.get("pid")}
    for role in sorted(chosen, key=lambda r: started[r]):
        if any(other != role and pid == chosen[role] and started[other] > started[role]
               for other, pid in chosen.items()):
            del chosen[role]
    changes = []
    for role, (state_file, pid_file) in files.items():
        want, have = chosen.get(role), _read_pid_file(pid_file)
        name = os.path.basename(pid_file)
        try:
            if want and want != have:
                with open(pid_file, "w") as f:
                    f.write(str(want))
                changes.append(f"{name}: {have or 'none'} -> {want} ({games[role]['source']})")
            elif not want and os.path.exists(pid_file):
                os.remove(pid_file)
                why = "; ".join(games.get(role, {}).get("refused") or []) or "another role's newer game"
                changes.append(f"{name}: {have} removed ({why})")
            if role != "server" and want and not os.path.exists(pid_file + ".identity.json"):
                state = StateFile(state_file).read() or {}
                # Bound creation by the controller's first receipt of this gamePid. Merely being newer than the
                # controller cannot prove ownership: a later unrelated TS3W can reuse a stale pin's pid.
                observed = next((crash_catch.parse_utc(h.get("utc")) for h in state.get("history") or []
                                 if re.search(rf"\bgamePid={want}\b", h.get("detail") or "")), None)
                launched = crash_catch.parse_utc(state.get("startedUtc"))
                info = game_identity(want)
                if (not crash_catch.attached(state) and state.get("gamePid") == want and info
                        and launched is not None and observed is not None
                        and launched <= info[1] <= observed
                        and ntpath.isabs(info[0]) and ntpath.basename(info[0]).lower() == "ts3w.exe"
                        and ntpath.normcase(info[0]) == ntpath.normcase(state.get("clientTs3") or "")):
                    with open(pid_file + ".identity.json", "x", encoding="utf-8") as f:
                        json.dump({"pid": want, "image": info[0], "started": info[1]}, f)
        except OSError:
            pass
    return changes


def pin_game(state_file: str):
    """While a boot waits: sync the pid files when the controller's gamePid is not in its pid file yet."""
    pid_file = {state: pid for state, pid in ts3_pid_files().values()}.get(state_file)
    game = StateFile(state_file).get_game_pid()
    if pid_file and game and _read_pid_file(pid_file) != game:
        return sync_ts3_pid_files()
    return []


def pin_ts3_pid_from_state(state_file: str, pid_file: str):
    """Record a controller's TS3 pid (state-file gamePid) in its pid file so
    server-reattach, client-restart, client2-down, and minimize can target the
    right TS3. Replaces the old process-list diffing in Wait-ForServerTs3Spawn.
    Windows: through sync_ts3_pid_files (only that controller's running TS3W.exe)."""
    if not sys.platform.startswith("linux"):
        sync_ts3_pid_files()
        return _read_pid_file(pid_file)
    pid = StateFile(state_file).get_game_pid()
    if not pid:
        return None
    try:
        with open(pid_file, "w") as f:
            f.write(str(pid))
    except OSError:
        return None
    return pid


def write_reattach_failure_diagnostics(args):
    """Print diagnostics when server-reattach fails to reach ready.
    Mirrors Write-ReattachFailureDagnostics: check bridge-trace if available."""
    if args.bridge_trace:
        bridge_record = SERVER_BRIDGE_LOG
        if os.path.exists(bridge_record):
            diagnostics = get_reattach_bridge_trace_diagnostic(bridge_record)
            for d in diagnostics:
                cprint(d, C.YELLOW)
            # Print last 8 lines of bridge record
            _tail_log(bridge_record, lines=8)
    cprint("Hint: check .omo/run/server.state.json for phase history")
    cprint("Hint: check .omo/run/server.log for controller output")


def get_client_mod_build_status(client_bridgectl_ready: bool):
    """Return client mod staleness string (mirrors Get-ClientModBuildStatus)."""
    if not client_bridgectl_ready:
        return None
    manifest = get_mod_manifest()
    installed_utc = manifest.get("builtUtc") if manifest else None
    live_stamp = get_live_mod_stamp(CLIENT_STATE_FILE, CLIENT_LOG)
    live = parse_mod_stamp(live_stamp)
    if not live:
        return "client mod: no build stamp (check client.state.json modBuild or client.log)"
    live_sha, live_utc = live
    if installed_utc and live_utc != installed_utc:
        return f"client mod={live_sha} (STALE - predates last make-mod, run reload-mod)"
    return f"client mod={live_sha} (current)"


def test_mod_sources_stale():
    """Check if mod source files changed since last build (mirrors Test-ModSourcesStale)."""
    mod_root = os.path.join(REPO, "SimsOnlineServerHostMod")
    manifest = get_mod_manifest()
    if not manifest:
        return True
    built_utc = manifest.get("builtUtc")
    if not built_utc:
        return True
    try:
        built_dt = datetime.strptime(built_utc, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
    except (ValueError, TypeError):
        return True
    # Check if any .cs file in mod root is newer than the build.
    # Generated files (BuildStamp.cs, EmbeddedHandlerAssembly.cs) are written by
    # make-mod itself at build time and must not re-trigger the staleness check.
    generated = {"BuildStamp.cs", "EmbeddedHandlerAssembly.cs"}
    for root, dirs, files in os.walk(mod_root):
        if "bin" in root or "obj" in root:
            continue
        for fname in files:
            if fname.endswith(".cs") and fname not in generated:
                fpath = os.path.join(root, fname)
                try:
                    mtime = datetime.fromtimestamp(os.path.getmtime(fpath), tz=timezone.utc)
                    if mtime > built_dt:
                        return True
                except OSError:
                    pass
    return False


def test_mod_needs_sync():
    """Determine if make-mod should run (workspace sha != installed sha, or sources stale)."""
    workspace_sha = get_workspace_git_sha()
    manifest = get_mod_manifest()
    installed_sha = manifest.get("gitSha") if manifest else None
    if not installed_sha or installed_sha != workspace_sha:
        return True
    if test_mod_sources_stale():
        return True
    return False


def test_mod_artifact_matches_manifest():
    """Verify the built DLL/package exist and match the manifest hashes."""
    manifest = get_mod_manifest()
    if not manifest:
        return False
    dll_path = os.path.join(REPO, "SimsOnlineServerHostMod", "bin", "Release", "SimsOnlineServerHostMod.dll")
    if not os.path.exists(dll_path):
        return False
    # Check package
    pkg_path = os.path.join(REPO, "SimsOnlineServerHostMod", "bin", "Release", "SimsOnlineServerHostMod.package")
    if not os.path.exists(pkg_path):
        return False
    return True


def test_mod_package_installed():
    """Check if the mod package is installed in the Mods/Packages directory."""
    plat = Platform()
    installed_path = os.path.join(plat.home_dir, "Documents", "Electronic Arts", "The Sims 3",
                                   "Mods", "Packages", "SimsOnlineServerHostMod.package")
    return os.path.exists(installed_path)


def test_mod_running_stale():
    """Compare the running mod build to what make-mod last installed."""
    mod_status = get_mod_build_status(True)
    return "STALE" in mod_status


def assert_mod_current_for_reattach():
    """Gate: refuse reattach if the warm mod is stale. Returns True if OK."""
    mod_status = get_mod_build_status(True)
    if "STALE" in mod_status:
        cprint(f"ERROR: warm TS3 mod is stale ({mod_status})", C.RED)
        cprint("  Run: python3 scripts/stackctl.py reload-mod")
        cprint("  Then re-run server-reattach if you still need a controller-only restart.")
        return False
    return True


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--test":
        total, failed = run_script_tests()
        sys.exit(failed)
    sys.exit(main())
