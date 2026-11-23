"""A watchdog for a scenario in flight: ends a hung or dead run early instead of at its own timeouts.

    python scripts/run_watchdog.py --seconds 300               # watch the stack now, read-only; print what it finds
    python scripts/run_watchdog.py --seconds 120 --clients 1,2 --every 20

By hand it refuses (exit 3) while another session holds the stack claim (scripts/stack_claim.py); exit 1 when it
fired, 0 when it found nothing.

After sims3-modernization's tools/harness/watchdog.ts and game-alive.ts (MIT). scenarios.py health() runs only between
scenarios, so a game that died or stopped answering in one was found at the next, after the hung scenario had run to
its own timeouts (a step's 900 s, a watch's join). Every EVERY seconds the watchdog reads each game's DIAG_STATE over
its bridge (in process, PROBE_TIMEOUT s) and finds a miss when:

  game gone   the game's TS3W.exe is not running (crash_catch.live_games, the readers' rule)
  no answer   the listener refuses, or the bridge does not answer DIAG_STATE: the line gives the time since the game
              last answered (2026-10-22: all three client bridges timed out with their TS3W processes alive)
  heartbeat   the simulator's tick (Simulator.TicksElapsed) has not moved since the last good reading while the game
              is not paused (observedSpeed=Pause; the bridge pump still answers while paused, the simulator does not)
  clock       the server's game clock (clockTicks) has not moved while unpaused over at least RECOVERY seconds; a
              client's clock only with --client-clock (SIMSONLINE_WATCHDOG_CLIENT_CLOCK=1): no quiet client's clock
              has been watched yet, and a client's heartbeat and bridge are checked anyway (Main, 2026-10-22)

A miss is read again CONFIRM seconds later; a second miss fires: `found` holds the line and on_fire runs. scenarios.py
then stops the scenario's running steps and marks it CRASHED with that line, the newest crash report and the change
set (crash_ledger.py), as health() words a dead stack. A held side (a rejoin step restarting that client on purpose)
is skipped and gets a fresh baseline afterwards. SIMSONLINE_RUN_WATCHDOG=0 runs scenarios without it.
Tests: python -m unittest scripts.run_watchdog_test
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
import contextlib
import os
import sys
import threading
import time
from pathlib import Path
from typing import Callable

sys.path.insert(0, str(Path(__file__).resolve().parent))
import compare_client  # noqa: E402

EVERY = 60.0  # seconds between checks
CONFIRM = 30.0  # a miss is read again this much later; the second miss fires
RECOVERY = 20.0  # the shortest span over which a still game clock counts as stalled
PROBE_TIMEOUT = 10.0  # seconds for one DIAG_STATE
SERVER = "127.0.0.1:34029"

Probe = Callable[[str], "tuple[dict[str, str] | None, str]"]
Alive = Callable[[str], "tuple[bool | None, str]"]


def hms(when: float) -> str:
    return time.strftime("%H:%M:%S", time.localtime(when))


def parse_state(report: str) -> dict[str, str]:
    """DIAG_STATE's reply (role=server;inWorld=True;tick=...;observedSpeed=Normal;clockTicks=...) as fields."""
    fields = {}
    for part in report.replace("\n", ";").split(";"):
        key, sep, value = part.partition("=")
        if sep:
            fields[key.strip()] = value.strip()
    return fields


def probe_state(endpoint: str, timeout: float = PROBE_TIMEOUT) -> tuple[dict[str, str] | None, str]:
    """One DIAG_STATE over the bridge: (its fields, "") or (None, why not)."""
    import bridgectl
    try:
        sock = bridgectl.connect(endpoint, timeout)
    except (SystemExit, OSError) as ex:
        return None, f"listener refused ({str(ex)[:80]})"
    try:
        response = bridgectl.roundtrip(sock, "DIAG_STATE")
    except (SystemExit, OSError, ValueError) as ex:
        return None, f"no answer to DIAG_STATE in {timeout:.0f} s ({type(ex).__name__})"
    finally:
        with contextlib.suppress(OSError):
            sock.close()
    report = str(response.get("report") or "")
    if "tick=" not in report:
        return None, "DIAG_STATE refused: " + str(response.get("error") or response.get("reason") or report)[:80]
    return parse_state(report), ""


def game_alive(side: str) -> tuple[bool | None, str]:
    """Whether a side's TS3W.exe runs (crash_catch.live_games): (True, "TS3W <pid> alive"), (False, why), or (None, ...)
    when no state or pid file names one."""
    try:
        import crash_catch
    except ImportError:
        crash_catch = _MirrorProcFallback
    game = crash_catch.live_games().get(side)
    if game is None:
        return None, "no pid recorded"
    if game["pid"]:
        return True, f"TS3W {game['pid']} alive"
    return False, "; ".join(game["refused"]) or "not running"


def stack_games(server: str, clients: list[str]) -> dict[str, tuple[str, str]]:
    """{name: (crash_catch side, endpoint)}: the server and each client endpoint once ("client 1" is side client,
    player N's is clientN; an endpoint that is no player's has no side, so only its bridge is read)."""
    games = {"server": ("server", server)}
    for endpoint in dict.fromkeys(clients):
        player = compare_client.player_of(endpoint)
        games[compare_client.name(endpoint)] = ("client" if player == "1" else f"client{player}" if player else "", endpoint)
    return games


class Watchdog(threading.Thread):
    """Checks the games every `every` s until stop(); see the module docstring. probe, alive and clock are injectable
    (tests drive it with fakes, and with every=confirm=0). client_clock: check the clients' game clocks too (None:
    SIMSONLINE_WATCHDOG_CLIENT_CLOCK=1)."""

    def __init__(self, games: dict[str, tuple[str, str]], on_fire: Callable[[str], None] | None = None,
                 probe: Probe = probe_state, alive: Alive = game_alive, clock: Callable[[], float] = time.time,
                 every: float = EVERY, confirm: float = CONFIRM, recovery: float = RECOVERY,
                 client_clock: bool | None = None) -> None:
        super().__init__(daemon=True, name="run-watchdog")
        self.games = dict(games)
        self.on_fire, self.probe, self.alive, self.clock = on_fire, probe, alive, clock
        self.every, self.confirm, self.recovery = every, confirm, recovery
        self.client_clock = (os.environ.get("SIMSONLINE_WATCHDOG_CLIENT_CLOCK") == "1" if client_clock is None
                             else client_clock)
        self.found: str | None = None
        self.stopped = threading.Event()
        self.lock = threading.Lock()
        self.held: set[str] = set()  # endpoints a step restarts on purpose
        self.base: dict[str, tuple[float, dict[str, str]]] = {}  # name -> the last good reading (when, fields)
        self.last_ok: dict[str, float] = {}  # name -> when its bridge last answered
        self.errors: list[str] = []  # checks that raised (a half-written state file): the watch goes on

    def is_held(self, name: str) -> bool:
        with self.lock:
            return self.games[name][1] in self.held

    @contextlib.contextmanager
    def hold(self, *endpoints: str):
        """Skip these games while a step restarts them; they get a fresh baseline after."""
        with self.lock:
            self.held.update(endpoints)
        try:
            yield
        finally:
            with self.lock:
                self.held.difference_update(endpoints)
                for name, (_, endpoint) in self.games.items():
                    if endpoint in endpoints:
                        self.base.pop(name, None)
                        self.last_ok.pop(name, None)

    def miss(self, name: str) -> str | None:
        """Why this game's reading now is a miss, or None (and the reading becomes its baseline)."""
        side, endpoint = self.games[name]
        alive, alive_text = self.alive(side) if side else (None, "")
        if alive is False:
            return f"{name}: game gone ({alive_text})"
        fields, why = self.probe(endpoint)
        now = self.clock()
        if fields is None:
            since = self.last_ok.get(name)
            return (f"{name} bridge: {why}; " + (f"no answer for {now - since:.0f} s (last DIAG_STATE answered {hms(since)})"
                                                 if since is not None else "no answer since the watch began")
                    + (f"; {alive_text}" if alive else ""))
        self.last_ok[name] = now
        base = self.base.get(name)
        if base is not None and fields.get("observedSpeed") != "Pause":
            when, old = base
            if fields.get("tick") and fields.get("tick") == old.get("tick"):
                return f"{name}: heartbeat frozen while unpaused (simulator tick {fields['tick']} since {hms(when)})"
            if ((side == "server" or self.client_clock) and now - when >= self.recovery and fields.get("clockTicks")
                    and fields.get("clockTicks") == old.get("clockTicks")):
                return (f"{name}: game clock stalled while unpaused (clockTicks {fields['clockTicks']} since {hms(when)}, "
                        "the simulator ticking)")
        self.base[name] = (now, fields)
        return None

    def check(self) -> str | None:
        """One round over the games: the line of a game that missed twice, CONFIRM s apart, or None."""
        for name in list(self.games):
            if self.is_held(name) or self.miss(name) is None:
                continue
            if self.stopped.wait(self.confirm) or self.is_held(name):
                continue
            again = self.miss(name)
            if again is not None:
                return again
        return None

    def run(self) -> None:
        while not self.stopped.is_set():
            try:
                found = self.check()
            except Exception as ex:  # noqa: BLE001 - one bad read must not leave the rest of the run unwatched
                self.errors.append(f"{type(ex).__name__}: {ex}"[:200])
                found = None
            if found is not None and not self.stopped.is_set():
                self.found = found
                if self.on_fire is not None:
                    self.on_fire(found)
                return
            if self.stopped.wait(self.every):
                return

    def stop(self) -> None:
        self.stopped.set()
        if self.is_alive():
            self.join(timeout=PROBE_TIMEOUT + 5)


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--seconds", type=float, default=300.0, help="how long to watch (default 300)")
    p.add_argument("--clients", default="1", help="the clients to watch, comma-separated (1,2,3 or HOST:PORT)")
    p.add_argument("--every", type=float, default=EVERY, help=f"seconds between checks (default {EVERY:.0f})")
    p.add_argument("--client-clock", action="store_true",
                   help="check the clients' game clocks too (default: the server's only)")
    a = p.parse_args(argv)
    try:
        clients = [compare_client.resolve(c) for c in a.clients.split(",") if c.strip()]
    except ValueError as ex:
        p.error(str(ex))
    import stack_claim
    held = stack_claim.holder(stack_claim.own_session())
    if held:
        # Read-only, but still a probe every check: another session's claim window is its own (2026-10-22, Session K
        # watched for two minutes inside 30a1c8d1's boot window after reading only the queue line of the status).
        print(f"refused: the live stack is claimed by {held}; watch it from inside your own claim", file=sys.stderr)
        return 3
    games = stack_games(SERVER, clients)
    print(f"watching {', '.join(games)} for {a.seconds:.0f} s, a check every {a.every:.0f} s (read-only)", flush=True)
    dog = Watchdog(games, on_fire=lambda found: print(f"FIRED: {found}", flush=True), every=a.every,
                   client_clock=a.client_clock or None)
    dog.start()
    dog.join(timeout=a.seconds)
    dog.stop()
    if dog.found is None:
        print(f"nothing found in {a.seconds:.0f} s")
    return 1 if dog.found else 0


if __name__ == "__main__":
    sys.exit(main())
