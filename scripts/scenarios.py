"""Presentation scenarios: each bug we fix becomes a scenario that runs unattended and says pass or fail.

    python scripts/scenarios.py list
    python scripts/scenarios.py run                          # every scenario, on the stack that is up
    python scripts/scenarios.py run phone-standing sit-dining
    python scripts/scenarios.py run --boot --mod             # fresh boot first (mod built and installed)
    python scripts/scenarios.py --client 2 run --boot        # two players: the boot starts player 2's game too (--clients)
    python scripts/scenarios.py health                       # both games answering? each game's age, warm rejoins,
                                                             # module loads, replay failures, double drivers: boot due?
    python scripts/scenarios.py history [names] [--last 10]  # each scenario's verdicts over the last runs, one row each
    python scripts/scenarios.py rescore [run]                # a detector changed: score a run's saved watches again
    python scripts/scenarios.py explain shower [--run RUN]   # one scenario's failures and what happened at each moment
    python scripts/scenarios.py during sleep-bed BedSleep:Sleep [--last 40]  # the copy's mod actions inside a server clip
    python scripts/scenarios.py exposure [--last N] [--list] # crashes per scenario-minute by tunables and debugger
    python scripts/scenarios.py arms --arm 'copy=--set both:sacs.seed-copy=1' --arm base= --settle 8 \
        --before 'scripts/sacs_picks.py mark' --after 'scripts/sacs_picks.py pull {arm}-{repeat}'
                                                             # the suite per arm, A B B A; --set held for the whole arm
    python scripts/scenarios.py exposure --pool 'copy=on("sacs.seed-copy")' --pool base=true --side server
                                                             # arm logs pooled by condition, with exact P values
    python scripts/scenarios.py exposure --pool ... --crash-match 00e54af9 --list
                                                             # one crash kind (triage from the report, archive too)

A scenario is a JSON file in scenarios/:
    {
      "name": "phone-standing",
      "summary": "Jennifer browses the web on her phone, standing",
      "steps": ["act:Jennifer inv:PhoneSmart BrowseTheWeb --clear --wait 40", "sleep:5"],
      "watch": {"sims": ["Jennifer"], "seconds": 25, "capture": true},
      "after": ["sims:Jennifer --machines --props", "capture:Jennifer:2"],
      "expect": "pass"                      (or "known-failing: <why>")
    }
Before each scenario every hold is released, both household Sims get empty queues and their autonomy held, so nothing
they pick for themselves takes over, and the reset ends with one check of what the scenario starts on (reset_check):
every player's active Sim, every Sim's queue and posture, and every conversation. What it left behind is RESET-FAILED
with the reason, which is the reset's own failure, never the scenario's verdict. Steps are quick_loop steps; an act: step must get
its pick running (sim_act exit 0). watch runs scripts/motion_watch.py while the steps run (it fails on client-only
teleports, slides, jitter, stutter,
snap turns, animation restarts, a walk off the server's pace or moving on too few of the walking ticks). after runs once the steps are done: sims: (scripts/sim_compare.py) fails on a
visible mismatch, capture: keeps server|client frames as evidence, assert:<server|client>:<bridge command>:<regex> (or assert:act:<sim_act arguments>:<regex>, its report: room=, at=)
fails unless that command's reply matches (a counter: assert:client:SACS_REPLAY 00:standIns:0/), rose:<server|client>:<bridge command>:<counter>
fails unless that counter (key=N or key:N in the reply) is higher than it was right after the reset (a cumulative
counter, counted for this scenario only: rose:server:SACS_RECORDER status:reseeded). A scenario marked known-failing that passes is
reported as FIXED?. A pick the game refuses (its own test, or it drops out when its turn comes) makes the scenario
BLOCKED with the reason: a precondition, not a presentation result. "watch": {"after": true} watches once the steps are
done instead of during them (for a scenario that restarts the client).

Two players: "clients": [1, 2] (or run --watch-clients 1,2 for a scenario that names none) watches both clients: one
motion watch and one sims: compare per client, their failures tagged "client N ", and a verdict per client next to the
overall one (records.json "clients", the report's "Per client" line). cpick:<1|2>:<client_pick arguments> is that
player's pick made the way a click makes it (a probe that finds no target or interaction makes the scenario BLOCKED, like
act:); hud:<1|2>:<seconds>:<regex> asserts that player's HUD queue; notice:<1|2>:<seconds>:<regex> and
nonotice:<1|2>:<regex> assert the notices that player was shown since the scenario started (the client mod's NOTICES
ring; the run marks it after the reset); menudiff:<1|2>:<actor>:<target>[:only=<text>][:exact] asserts that player's pie menu
for the actor on the target shows nothing the server refuses and greys nothing as server-refused that the server's
test passes (menu_diff.py, ; a target is a first name, type:<fragment> or id:<server object id>; only= counts
only the entries whose menu text contains <text>; a last :exact also fails on an entry missing on the client or only on
the client); objectdiff:<1|2>[:<object_diff.py options>] asserts that player's
game made no object of its own near the Sims and has a shown copy of every server object there (object_diff.py, ;
--wait-new Type:seconds first waits for the server to make one). Each can be a step or an after check.
assert:client1:/assert:client2: name a client; assert:client: is the first one watched.

Cameras: each game walks a Sim on its screen with clips and transitions and a Sim off it at a steady speed (native,
internal documentation), so the reset puts every game's camera on the household's spot, and
camera:<games>:<Sim or x,z>[:<distance>] (games: server, 1, 2 or all, comma-separated) points those cameras elsewhere
on purpose (two-hud-server-away: the server's camera on the empty bedroom, as a dedicated server's is).

Writes .omo/run/scenarios/<time>/report.md (a row per scenario, then each one's output and evidence images) and
records.json (the same, structured: history, rescore and explain read it), prints the rows and what changed since each
scenario's previous run. Exit code 0 when every scenario met its expectation, 5 when one did not, 6 when the stack was
down (before the run, or part way: the rest are BLOCKED "stack down" with the newest TS3 crash report since the boot,
and the scenario it went down in reads CRASHED; one more check after the last scenario finds a death there).
RESET-FAILED and BLOCKED scenarios did not run: they take no scenario minutes in exposure, and the arms summary counts
an arm whose every scenario is one of them as not run. Each scenario runs under scripts/run_watchdog.py: a game gone,
a bridge that stops answering, or the simulator or clock
still while unpaused, missed twice 30 s apart, stops the scenario's steps there and it reads CRASHED with the
watchdog's line (SIMSONLINE_RUN_WATCHDOG=0: none).

Cheapest first: explain and history read saved runs; rescore re-scores saved samples (seconds); a run costs minutes.
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


import argparse
import calendar
import contextlib
import functools
import glob
import json
import math
import os
import re
import shlex
import shutil
import subprocess
import sys
import threading
import time
from pathlib import Path
from typing import Callable

sys.path.insert(0, str(Path(__file__).resolve().parent))
import compare_client  # noqa: E402
from local_endpoints import SERVER_BRIDGECTL  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
SCENARIOS = ROOT / "scenarios"
HOUSEHOLD = ["Jennifer", "Pablo"]  # a scenario's "household" names others
PLAYER1 = "Jennifer"  # the Sim player 1's game claims (stackctl's --claim-sim default)
REPAIR = True  # the reset's --repair all (e89f60c); scenarios.py run --no-repair turns it off (an arm on a build before it)
# The client games whose replay counters each scenario records (run sets it: watched_clients); empty: none read
WATCHED: list[str] = []
# scenarios.py run --rejoin MODE: every rejoin step runs as rejoin:MODE (warm: client 1's controller only, in seconds;
# cold: a plain rejoin), so one late-join scenario covers both kinds of join. "" keeps each step as written.
REJOIN = ""
# Where each scenario starts (scenarios.py home --save): x, z, fx, fz per Sim, in both games. Without it a run starts
# wherever the last one left the Sims, and a walk that failed once never came back (shower, 2026-10-14).
HOME = SCENARIOS / "start" / "home.json"
# CLIENT is the client game a run compares: client 1 unless --client (or SIMSONLINE_COMPARE_CLIENT) names player 2's.
SERVER, CLIENT = SERVER_BRIDGECTL, compare_client.endpoint()
RUNS = ROOT / ".omo" / "run" / "scenarios"
# What starts each failure a row joins with "; " (a failure's own text can hold "; " too).
FAILURE_KINDS = ("motion:", "motion watch failed", "compare:", "compare failed", "assert ", "step ", "stack down",
                 "client ", "reset:")
MOTION_KINDS = ("motion:", "motion watch failed")
# A scenario that watches two clients ("clients": [1, 2]) tags each client's watch and compare failures "client N ".
CLIENT_TAG = re.compile(r"^client (\S+) ")
# Health checks these client games too (the second client of a two-client scenario), besides CLIENT.
EXTRA_CLIENTS: list[str] = []
# run --watch-clients: the clients a scenario that names none watches (None: the run's one client).
RUN_CLIENTS: list[str] | None = None
WATCH_TAIL = 6.0  # seconds the motion watch goes on after a scenario's steps: the last clip's end
ARM_LOAD_INTERVAL = 5.0  # seconds between machine_load's samples over an arm's run ( G19; a boot's are 2 s)
VERDICTS = ("PASS", "FAIL", "BLOCKED", "CRASHED", "RESET-FAILED", "FIXED?", "known-failing")
CODES = {"PASS": "P", "FAIL": "F", "BLOCKED": "B", "CRASHED": "C", "RESET-FAILED": "R", "FIXED?": "X",
         "known-failing": "k"}
# The scenario never ran its steps: RESET-FAILED did not pass its own last check, BLOCKED a precondition.
# Neither costs scenario minutes in the crash exposure, neither is rescored, and an arm of only these ran nothing.
NOT_RUN = ("BLOCKED", "RESET-FAILED")
ROW = re.compile(r"^\| ([\w-]+) \| (" + "|".join(v.replace("?", r"\?") for v in VERDICTS) + r")(?: \[NO-FORK\])? \| (.*?) \| (.*) \|$")


# The scripts python() is running now, so a watchdog that fires can stop them (stop_children) instead of letting a hung
# scenario run to its own timeouts. On Windows terminate() ends the child only: a bridgectl it started ends on its own
# socket timeout.
CHILDREN: set[subprocess.Popen] = set()
CHILDREN_LOCK = threading.Lock()


def python(*args: str, timeout: int = 900) -> tuple[int, str]:
    proc = subprocess.Popen([sys.executable, *args], cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    with CHILDREN_LOCK:
        CHILDREN.add(proc)
    try:
        out, err = proc.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        proc.kill()  # as subprocess.run's timeout does
        proc.communicate()
        raise
    finally:
        with CHILDREN_LOCK:
            CHILDREN.discard(proc)
    return proc.returncode, (out + err)


def stop_children() -> None:
    with CHILDREN_LOCK:
        running = list(CHILDREN)
    for proc in running:
        try:
            proc.terminate()
        except OSError:
            pass


def quick_step(step: str) -> tuple[int, str]:
    return python("scripts/quick_loop.py", "run", "--step", step)


STEP_VAR = re.compile(r"\bvar (\w+)=(\S+)")


def step_vars(text: str) -> dict[str, str]:
    """The variables a step printed (a probe's var_<name>=<value> reply field: quick_loop step_probe)."""
    return dict(STEP_VAR.findall(text))


def expand(step: str, variables: dict[str, str]) -> str:
    """A step or check with {name} replaced by a variable an earlier step set; an unknown {name} stays as written.
    For an id that changes per boot: probe:server:scratch/PaperPlaceProbe.cs prints var paper=..., then id:{paper}."""
    return re.sub(r"\{(\w+)\}", lambda m: variables.get(m[1], m[0]), step)


def client_key(endpoint: str) -> str:
    """'1', '2', '3' for a player's client endpoint, else the endpoint itself."""
    return compare_client.player_of(endpoint) or endpoint


def spec_clients(spec: dict, default: list[str] | None = None) -> list[str]:
    """The client games a scenario watches, as '1', '2' or HOST:PORT: its "clients" ([1, 2]), else the run's --clients,
    else the run's one client (--client, client 1 by default)."""
    named = spec.get("clients") or default
    return [str(c) for c in named] if named else [client_key(CLIENT)]


def step_client(step: str) -> str | None:
    """The client a player step or check names (cpick:2:..., hud:1:..., notice:2:..., nonotice:1:..., menudiff:2:...,
    assert:client2:..., until:client2:..., wait:client1:...), else None (a step on the server, or one every watched client
    shares)."""
    m = (re.match(r"(?:cpick|hud|notice|nonotice|menudiff|probe):(\d):", step) or re.match(r"(?:assert|until|wait):client(\d):", step)
         or re.match(r"objectdiff:(\d)(?::|$)", step))
    return m.group(1) if m else None


def failure_client(failure: str) -> str | None:
    """The client a failure belongs to: its "client N " tag, the client its step or assert names, else None (all)."""
    tagged = CLIENT_TAG.match(failure)
    if tagged:
        return tagged.group(1)
    if failure.startswith("step "):
        return step_client(failure[5:])
    m = re.match(r"assert client(\d) ", failure)
    return m.group(1) if m else None


def client_verdicts(record: dict, clients: list[str]) -> dict[str, dict]:
    """A two-client scenario's verdict per client: that client's failures and the ones every client shares."""
    out = {}
    blocked = record.get("verdict") == "BLOCKED"
    for client in clients:
        own = [f for f in record["failures"] if failure_client(f) in (None, client)]
        expect_pass = record["expect"] == "pass"
        if blocked:
            verdict = "BLOCKED"
        else:
            verdict = ("PASS" if not own else "FAIL") if expect_pass else ("FIXED?" if not own else "known-failing")
        out[client] = {"verdict": verdict, "failures": own}
    return out


def uses_notices(spec: dict) -> bool:
    return any(re.match(r"(?:no)?notice:", s) for s in spec.get("steps", []) + spec.get("after", []))


def rejoin_step(step: str) -> str:
    """A rejoin step as --rejoin says (REJOIN); any other step, or no --rejoin, as written."""
    if not REJOIN or step.partition(":")[0] != "rejoin":
        return step
    return "rejoin" if REJOIN == "cold" else "rejoin:" + REJOIN


SETTLE_S = 20.0  # the longest the reset waits for a Sim it left leaving a bed, a seat or an interaction
START_OFF_M = 0.3  # a Sim farther than this from its start after the reset is placed again once, and noted


def act_fields(entry: str) -> dict[str, str]:
    """sim_act.py's summary line as fields (actor=Pablo was=- queued=0 ... at=x,z)."""
    return dict(part.split("=", 1) for part in entry.split() if "=" in part)


def busy(fields: dict[str, str]) -> bool:
    """A Sim still moving on its own: an interaction runs or waits (a cancelled one still plays its exit, a sleeping
    Sim gets out of bed), or the reset's stand-up is queued."""
    return fields.get("was", "-") != "-" or fields.get("queued", "0") != "0" or fields.get("stand", "").startswith("queued")


def settle(name: str, limit: float = SETTLE_S, moving: Callable[[dict[str, str]], bool] = busy) -> dict[str, str]:
    """The server's Sim read once a second until it is no longer moving (busy: nothing runs or waits in its queue), at
    most limit seconds; the last read."""
    deadline = time.time() + limit
    while True:
        fields = act_fields(python("scripts/sim_act.py", name)[1])
        if not moving(fields) or time.time() >= deadline:
            return fields
        time.sleep(1)


def off_spot(fields: dict[str, str], spot: list[float]) -> float | None:
    """How far a read Sim (at=x,z, above=metres over its floor) stands from its start spot (x, z, ...) on its floor;
    None without a position. The height counts: a Sim back from another lot once stood 3.8 m above the home floor at
    the right x and z."""
    try:
        x, z = (float(v) for v in fields.get("at", "").split(","))
    except ValueError:
        return None
    try:
        above = float(fields.get("above", "0"))
    except ValueError:
        above = 0.0
    return math.dist((x, z, above), (spot[0], spot[1], 0.0))


def camera(on: dict[str, str], target: str, distance: float = 1.0, level: str = "") -> list[str]:
    """Point each game's camera (label -> endpoint) at a Sim (first name) or a spot (x,z, on the level of the Sim named
    by level, the household's first by default): ts3_view.focus, one reply line per game."""
    import ts3_view
    if "," in target:
        x, z = (float(v) for v in target.split(","))
        return ts3_view.focus(level or HOUSEHOLD[0], distance, (x, z), on)
    return ts3_view.focus(target, distance, None, on)


def start_spots(start: dict | str | None, household: list[str]) -> dict[str, list[float]]:
    """Where a scenario's Sims start: scenarios/start/home.json, then its "start" (a dict of spots, or the name of
    another file in scenarios/start/, e.g. "langeraks"), for the Sims of its household only."""
    spots = dict(json.loads(HOME.read_text(encoding="utf-8")) if HOME.exists() else {})
    if isinstance(start, str):
        path = HOME.parent / (start if start.endswith(".json") else start + ".json")
        if not path.exists():
            raise SystemExit(f"start {start!r}: no {path}")
        spots.update(json.loads(path.read_text(encoding="utf-8")))
    else:
        spots.update(start or {})
    return {name: spot for name, spot in spots.items() if name in household}


def camera_spots(spots: dict[str, list[float]], clients: list[str], players: dict[str, str] | str
                 ) -> list[tuple[str, str, float, float]]:
    """Each game's camera spot as (game, level Sim, x, z): the server on the middle of every spot; with players on Sims
    of their own (a scenario's "players" or "player2", families on two lots; three at a park) each
    client on its own player's Sim, player 1's Jennifer and player N's players[N]; otherwise every client on the middle
    too, as before. A str is player 2's Sim (the "player2" form)."""
    if isinstance(players, str):
        players = {"2": players} if players else {}
    def middle(names: list[str]) -> tuple[float, float]:
        chosen = [spots[n] for n in names if n in spots] or list(spots.values())
        return sum(s[0] for s in chosen) / len(chosen), sum(s[1] for s in chosen) / len(chosen)

    every = list(spots)
    out = [("server", every[0], *middle(every))]
    for endpoint in clients:
        player = compare_client.player_of(endpoint)
        own = [PLAYER1 if player == "1" else players.get(player or "", "")]
        names = own if players and own[0] in spots else every
        out.append((endpoint, names[0], *middle(names)))
    return out


def spec_players(spec: dict) -> dict[str, str]:
    """The Sims a scenario names for players 2 and later: its "players" ({"2": "Iliana", "3": "Gunther"}, ) with
    "player2" as player 2's short form."""
    players = {str(k): v for k, v in (spec.get("players") or {}).items() if v}
    if spec.get("player2"):
        players.setdefault("2", spec["player2"])
    return players


def players_for(spec: dict, watched: list[str]) -> dict[str, str]:
    """The Sim each later player's game must be on: the scenario's own (spec_players), else Pablo for a watched player
    2; a player 3 or later with no Sim named, and an unwatched player 2, are left as they are."""
    named = spec_players(spec)
    out = {}
    for player in compare_client.PLAYERS[1:]:
        if player in named:
            out[player] = named[player]
        elif player == "2" and (player in watched or compare_client.client_n(player) in watched):
            out[player] = "Pablo"
    return out


def player2_for(spec: dict, watched: list[str]) -> str:
    """The Sim a scenario wants player 2's game on (players_for's "2"); "" when it does not watch client 2."""
    return players_for(spec, watched).get("2", "")


def ensure_player_claim(player: str, sim: str) -> tuple[bool, str]:
    """Read acknowledged ownership, restoring it via the client's public command path without restarting TS3."""
    import household_read

    try:
        actor = household_read.sim_id(sim)
    except SystemExit as error:
        return False, f"player {player} claim lookup failed: {error}"
    endpoint = compare_client.client_n(player)
    expected = f"claims={actor}"
    code, text = python("scripts/bridgectl.py", "--endpoint", endpoint, "CLIENT_CLAIMS", timeout=30)
    if code == 0 and expected in text.split():
        return True, expected
    code, text = python("scripts/bridgectl.py", "--endpoint", endpoint, "CLIENT_CLAIM", actor, timeout=30)
    return code == 0 and expected in text.split(), f"player {player} claim: {text.strip()}"


def reselect_player(player: str, sim: str) -> bool:
    """Restore a claimed actor's selection through the explicit selection boundary, without restarting TS3.
    This does not transfer ownership; a player changing claims still needs the normal rejoin."""
    import household_read

    try:
        actor = household_read.sim_id(sim)
    except SystemExit:
        return False
    code, _ = python(
        "scripts/bridgectl.py", "--endpoint", compare_client.client_n(player), "SET_ACTIVE_SIM", actor, timeout=30
    )
    return code == 0


def ensure_player(player: str, sim: str) -> tuple[bool, str]:
    """Player N's game on sim before the reset: read its selected Sim (household_read), restore selection first, and
    if that fails join again on sim (rejoin:<N>:as=<sim>, about 95 s; the run watchdog holds that game meanwhile, so
    the restart is not read as a bridge that stopped answering) and read it again: a rejoin that answers ok can
    still leave the game on the Sim it was -- run 20261101-035827 came back on player 2 with actor=Sandi French, and
    two-social's steps then ran on a Sim no player has. Tried once more on a read that does not match, and after that it
    is the reset's own failure, not the scenario's. (ok, log text)."""
    endpoint = compare_client.client_n(player)
    claimed, claim_report = ensure_player_claim(player, sim)
    if not claimed:
        return False, claim_report
    now, error = active_sim(endpoint)
    if now.lower() == sim.lower():
        return True, f"player {player} on {sim}"
    if reselect_player(player, sim):
        now, error = active_sim(endpoint)
        if now.lower() == sim.lower():
            return True, f"player {player} on {sim} (selection restored)"
    lines = [f"player {player} on {now or f'(no answer: {error[:80]})'}, joined again as {sim}"]
    for attempt in range(2):
        with WATCH.hold(endpoint) if WATCH else contextlib.nullcontext():
            code, text = quick_step(f"rejoin:{player}:as={sim}")
        lines.append(f"rejoin {'again' if attempt else ''} (exit {code}):")
        lines.append(_tail(text, 3))
        now, error = active_sim(endpoint)
        lines.append(f"player {player} now on {now or f'(no answer: {error[:80]})'}")
        if code != 0:
            return False, "\n".join(lines)  # the rejoin itself failed: its line says why, and a second try costs 95 s
        if now.lower() == sim.lower():
            return True, "\n".join(lines)
    return False, "\n".join(lines)


def ensure_player2(sim: str) -> tuple[bool, str]:
    return ensure_player("2", sim)


def camera_games(sides: str, endpoints: dict[str, str]) -> dict[str, str]:
    """A camera step's games: a comma list of server, 1, 2, 3, or all (the server and the scenario's clients)."""
    chosen: dict[str, str] = {}
    for side in (s.strip() for s in sides.split(",") if s.strip()):
        if side == "all":
            chosen["server"] = SERVER
            chosen.update({compare_client.name(e): e for e in endpoints.values()})
        elif side == "server":
            chosen["server"] = SERVER
        else:
            endpoint = compare_client.resolve(side)
            chosen[compare_client.name(endpoint)] = endpoint
    return chosen


SERVER_SELECTED = "Jennifer"


def select_server_sim(name: str = SERVER_SELECTED) -> str:
    """The server's selected Sim back to Jennifer (SET_ACTIVE_SIM, PlumbBob.ForceSelectActor; a no-op when she already
    is). TS3's active household is the selected Sim's, and every scenario assumes the Martinezes': a checkpoint save
    while Session L had Gunther selected left the save on the Goths, and the game then treated Jennifer and Iliana as
    NPCs, sending them home from the park within a second."""
    import household_read
    try:
        sim = household_read.sim_id(name)
    except SystemExit as exc:
        return f"server selected: {name} not found ({exc})"
    code, text = python("scripts/bridgectl.py", "--endpoint", SERVER, "SET_ACTIVE_SIM", sim, timeout=30)
    lines = text.strip().splitlines()
    return f"server selected: {name} ({lines[-1] if lines else 'exit ' + str(code)})"


CONV_PROBE, CONV_END = "scratch/ConversationProbe.cs", "scratch/ConvEndProbe.cs"
CONV_WAIT_S = 3.0  # how long the gate waits for a client's copy to leave when the server's Sim does
STAND_TRIES = 2  # times the gate asks a Sim off its seat, bed or car, waiting for each ask
# A Sim still on a trip, by the name of the interaction or posture that keeps it there: the trip outlives the scenario
# that started it and the next one begins on the road.
TRIP = re.compile(r"(?:vehicle|carnpc|\bcar\b|driv|taxi)", re.IGNORECASE)


def probe_reply(endpoint: str, path: str, timeout: int = 90) -> dict[str, str]:
    """One scratch probe's reply as fields: bridgectl probe prints the probe's own ';'-delimited header one
    '  key = value' line after 'loaded='; {} when the game does not answer."""
    _, text = python("scripts/bridgectl.py", "--timeout", str(timeout), "--endpoint", endpoint, "probe", path,
                     timeout=timeout + 30)
    lines = text.splitlines()
    loaded = next((i for i, line in enumerate(lines) if line.lstrip().startswith("loaded=")), None)
    fields: dict[str, str] = {}
    for line in (lines[loaded + 1:] if loaded is not None else lines):
        key, sep, value = line.strip().partition(" = ")
        if sep:
            fields[key.strip()] = value.strip()  # bridgectl pads the key to the widest one
    return fields


def active_sim(endpoint: str) -> tuple[str, str]:
    """A game's selected Sim as (first name, error text); "" for the name when the game does not answer or has none
    selected. household_read reads it in the game (Sim.ActiveActor, the active household's Sim)."""
    import household_read
    reading = household_read.read(endpoint, [])
    return (reading["active"]["name"] if reading["active"] else ""), str(reading["error"] or "")


def sim_state(name: str) -> dict[str, str]:
    """The server's read of one Sim, its stand-up asked for on the way (sim_act NAME --stand). The stand field is the
    try and the check in one call: sim_act prints it only when the Sim is not standing."""
    return act_fields(python("scripts/sim_act.py", name, "--stand")[1])


def idle_and_standing(fields: dict[str, str]) -> bool:
    """Nothing running or queued and on its feet: what the reset leaves every Sim on, so the scenario starts from its
    own first step, not from the last one's exit."""
    return not busy(fields) and not fields.get("stand") and not fields.get("posture")


def standing_up(fields: dict[str, str]) -> bool:
    """A plain read of a Sim still on its way off a posture: busy, or not yet out of it (posture=..., which a read
    prints before the stand-up it was given is even in the queue)."""
    return busy(fields) or bool(fields.get("posture"))


def reset_state(name: str, tries: int = STAND_TRIES) -> dict[str, str]:
    """A Sim's server read once it is idle and standing, or once every try is spent: sim_act --stand, each try waited
    for with settle. The read that asks the last time queues a stand-up of its own and still sees the posture, so a
    stand=queued:... there is waited for and the posture read again without asking; a posture
    still there after that is the game not running the stand-up it was given. A stand=none:... is a posture it cannot
    leave on its own (a pool, a car)."""
    fields = sim_state(name)
    for _ in range(tries - 1):
        if idle_and_standing(fields):
            break
        settle(name)
        fields = sim_state(name)
    if fields.get("stand", "").startswith("queued"):
        settle(name, moving=standing_up)
        fields = act_fields(python("scripts/sim_act.py", name)[1])
    return fields


def left_behind(name: str, fields: dict[str, str]) -> str:
    """What the reset left on that Sim, as the reason says it; "" when it is idle and standing. A trip that outlived
    its own interaction shows only as the posture it never left, so both are named here."""
    if not fields or "actor" not in fields:
        return ""
    parts = []
    running = fields.get("was", "-")
    if running != "-":
        parts.append(("a trip (" + running + ")" if TRIP.search(running) else running) + " still running")
    elif fields.get("queued", "0") != "0":
        parts.append(fields["queued"] + " interaction(s) queued")
    stand = fields.get("stand", "")
    posture = stand.partition(":")[2] if stand else fields.get("posture", "")
    if posture:
        why = f"stand={stand}" if stand else "its stand-up ran and it did not get up"
        parts.append(("a trip in " if TRIP.search(posture) else "left in ") + posture + f" ({why})")
    return f"{name}: " + ", ".join(parts) if parts else ""


def conversations_on(endpoint: str) -> dict[str, str]:
    """Who holds what conversation on that game (scratch/ConversationProbe.cs): Sim first name -> its conversation's
    members joined with +, or none; {} when the game does not answer. The probe reads four Sims (Gunther, Iliana,
    Jennifer, Pablo), today every Sim a scenario's household or players names."""
    out: dict[str, str] = {}
    for part in probe_reply(endpoint, CONV_PROBE).get("convs", "").split(","):
        name, _, members = part.partition(":")
        if name:
            out[name] = members or "none"
    return out


def held_conversations(convs: dict[str, str]) -> dict[str, str]:
    """The Sims of that read that hold a conversation, and who is in it with them."""
    return {name: members for name, members in convs.items() if members not in ("", "none")}


def end_conversations() -> str:
    """Everyone of the four leaves any conversation (scratch/ConvEndProbe.cs, the server-side driver the conversation
    scenarios use); the clients' copies follow when the server's Sims do. Its reply as one log line: it wants all four
    Sims in the world, so an err there is said, not a failure of this check."""
    reply = probe_reply(SERVER, CONV_END, timeout=60)
    if reply.get("ok", "").lower() == "true":
        return "conversations: the server ended them"
    return "conversations: " + (f"{reply.get('err')} ({reply.get('msg', '')})" if reply.get("err") else "no answer")


def reset_check(sims: list[str], players: dict[str, str], clients: list[str]) -> tuple[list[str], list[str]]:
    # One check of what the scenario starts on, after the reset placed everyone: every player's game on its
    # own Sim, the server's selected Sim, every Sim with nothing running or queued and on its feet, and nobody left in a
    # conversation the scenario did not start. In the nine runs before it about 14 of the 30 failures came from the
    # reset, and a scenario that started on the last one's state was then judged for it; these reasons are the reset's
    # own, and the verdict is RESET-FAILED. What cannot be read is written in the log and not counted: a game that died
    # is the run's health check to name, as CRASHED or BLOCKED, never as a reset that failed.
    problems: list[str] = []
    log: list[str] = []
    server, error = active_sim(SERVER)
    log.append("server selected: " + (server or f"(no answer: {error[:80]})"))
    if server and server.lower() != SERVER_SELECTED.lower():
        log.append(select_server_sim())  # the reset asked for it; ask once more before calling it a failure
        server, _ = active_sim(SERVER)
        if server and server.lower() != SERVER_SELECTED.lower():
            problems.append(f"reset: the server plays {server}, not {SERVER_SELECTED}")
    for player, sim in sorted(players.items()):
        now, error = active_sim(compare_client.client_n(player))
        log.append(f"player {player} selected: " + (now or f"(no answer: {error[:80]})"))
        if now and now.lower() != sim.lower():
            problems.append(f"reset: player {player}'s game plays {now}, not {sim}")
    games = {"server": SERVER} | {compare_client.name(endpoint): endpoint for endpoint in clients}
    held = {}
    for game, endpoint in games.items():
        convs = conversations_on(endpoint)
        if not convs:
            log.append(f"conversations {game}: not read")
            continue
        held[game] = held_conversations(convs)
        log.append(f"conversations {game}: "
                   + (", ".join(f"{name} in {members}" for name, members in sorted(held[game].items())) or "none held"))
    if any(held.values()):
        # a bystander conversation the last scenario left is what broke conv-client-leave's strict assert in run
        # 20261101-035827: Gunther was still talking with Sue, which this scenario never started
        log.append(end_conversations())
        time.sleep(CONV_WAIT_S)
        # Only the scenario's own Sims (its household and players) fail the reset; another of the probe's four that
        # still talks is logged. Run 20261101-073915: client 1's Gunther kept Gunther+VJ after the server's went to
        # work, and social-chat, park-menu-parity and two-park-meet, which never name Gunther, RESET-FAILED on it.
        own = {name.lower() for name in [*sims, *players.values()]}
        for game, endpoint in games.items():
            now = held_conversations(conversations_on(endpoint))
            outside = sorted(name for name in now if name.lower() not in own)
            if outside:
                log.append(f"conversations {game} after the end: "
                           + ", ".join(f"{name} in {now[name]}" for name in outside) + " (not this scenario's Sims)")
            if mine := sorted(name for name in now if name.lower() in own):
                problems.append("reset: " + ", ".join(
                    f"{name} still holds a conversation ({now[name]}) on {game}" for name in mine))
    for name in sims:
        fields = reset_state(name)
        if "actor" not in fields:
            log.append(f"{name}: not read")
            continue
        left = left_behind(name, fields)
        log.append(left or f"{name}: idle and standing")
        if left:
            problems.append(f"reset: {left}")
    return problems, log


def reset(start: dict | str | None = None, clients: list[str] | None = None, note: dict | None = None,
          household: list[str] | None = None, player2: str | dict[str, str] = "") -> str:
    # household: the scenario's Sims (its "household"; Jennifer and Pablo by default), the ones held, fed and placed;
    # player2: the Sims later players' games are on when the scenario names them ({"2": ..., "3": ...}, or player 2's
    # Sim alone): each client's camera on its own player's Sim.
    # Four probe calls, not one per flag and Sim (a reset was much of the idle time between scenarios): the first Sim's
    # call also releases every hold, holds the household, freezes needs full (a long run can't starve a Sim) and puts
    # out any fire left from a scenario that stopped half way, stops any DJ booth playing (one saved mid-set loads playing
    # and pulls the Sims into crowd interactions: sleep-bed BLOCKED behind BooDJ, 2026-10-31), and repairs anything broken (a shower that broke on use
    # fails TakeShower's test from then on: every later shower run was BLOCKED). --stand: a Sim stays seated after its Sit finishes, and a
    # teleport home keeps the seat taken (phone-seated BLOCKED after late-join-seated): the game's own stand-up first.
    log = []
    sims = household or HOUSEHOLD
    joined = ",".join(sims)
    # The server's Sims only: a clear on a client would hide a copy that runs an interaction of its own, which is what the
    # two-families checks look for (, 2026-10-24). The fork's presenter slices stop a copy starting one, but a
    # client that joins late can still load a copy holding the interaction the server's Sim had at the save:
    # late-join-bed's client Pablo held EnterRelaxing after the server's ran none (runs 20261024-084930 and
    # 20261031-173532, compare `! interaction - | EnterRelaxing`), open.
    for index, name in enumerate(sims):
        extra = (
            ["--release", "all", "--hold", joined, "--needs", "static:" + joined, "--fire", "out", "--booths", "off"]
            + (["--repair", "all"] if REPAIR else [])
            if index == 0
            else []
        )
        log.append(python("scripts/sim_act.py", name, "--clear", "--stand", *extra)[1].strip())
    if any(
        act_fields(entry).get("posture") == "SocializingPosture"
        or act_fields(entry).get("stand", "").endswith(":SocializingPosture")
        for entry in log
    ):
        # The standing transition only pops the posture; its caller must end the conversation first
        # (docs/engine/socializing-posture.md). Do that before waiting for the queued exit or placing the Sims.
        log.append(end_conversations())
    # cancelled interactions exit (a sleeping Sim gets up); a queued stand-up takes a few seconds more
    time.sleep(8 if any("stand=queued" in entry for entry in log) else 4)
    # A Sim placed while it still leaves a bed or a seat ends that exit where the exit ends, not on its spot: Pablo
    # started social-chat 0.3-0.8 m off after sleep-bed in 121 of 255 runs (BedSleep:ExitStanding still playing when
    # the watch began), and those failed 72% against 38% from the spot; Jennifer started two-hud or two-same-chair
    # 0.8-2.5 m off in 11 of 27, each after a scenario that left her seated (saved runs, 2026-10-21). So each Sim the
    # first calls found busy is waited for before the place, and read again after it.
    moving = [name for name, entry in zip(sims, log) if busy(act_fields(entry))]
    for name in moving:
        settle(name)
    log.append(select_server_sim())
    spots = start_spots(start, sims)
    if spots:
        place_all = ";".join(f"{name}=" + ",".join(f"{v:.3f}" for v in spot) for name, spot in spots.items())

        def place() -> None:
            # every game: each client's copy starts where the server's Sim does (clients: the endpoints a scenario watches)
            for endpoint in [SERVER, *(clients or [CLIENT])]:
                log.append(python("scripts/sim_act.py", "--place-all", place_all, "--endpoint", endpoint)[1].strip())
            time.sleep(1)

        place()
        off = {}
        for name in moving:
            distance = off_spot(act_fields(python("scripts/sim_act.py", name)[1]), spots[name]) if name in spots else None
            if distance is not None and distance > START_OFF_M:
                off[name] = distance
        if off:
            for name in off:
                settle(name)
            place()
            texts = []
            for name, distance in off.items():
                again = off_spot(act_fields(python("scripts/sim_act.py", name)[1]), spots[name])
                texts.append(f"{name} {distance:.2f} m off its start after the reset, placed again: "
                             + ("no position read" if again is None else f"{again:.2f} m"))
            log.append("start: " + "; ".join(texts))
            if note is not None:
                note["start"] = texts
        # Every game's camera on the household's spot. A game walks a Sim out of its camera's view at a steady full
        # speed from the first step (native: internal documentation), so a camera the last scenario
        # left elsewhere changed that game's walks: a --capture watch follows its Sim, and after sleep-bed's (Pablo, the
        # bedroom) two-hud failed 2 of 2 on the client whose camera had stayed; with the cameras back it passed 2 of 2
        # (2026-10-21). A scenario's camera: step moves one on purpose. Families on two lots: each client's
        # camera on its own player's Sim (camera_spots), as a player's would be.
        placed = camera_spots(spots, clients or [CLIENT], player2)
        if len({(x, z) for _, _, x, z in placed}) == 1:
            on = {"server": SERVER} | {compare_client.name(e): e for e in clients or [CLIENT]}
            log.append("camera: " + "; ".join(camera(on, f"{placed[0][2]:.2f},{placed[0][3]:.2f}")))
        else:
            replies = []
            for game, level, x, z in placed:
                label = "server" if game == "server" else compare_client.name(game)
                replies += camera({label: SERVER if game == "server" else game}, f"{x:.2f},{z:.2f}", level=level)
            log.append("camera: " + "; ".join(replies))
    return "\n".join(log)


def save_home() -> int:
    """The household's server positions and facings now, as every scenario's start (scenarios/home.json)."""
    _, text = python("scripts/sim_compare.py")
    spots: dict[str, list[float]] = {}
    sim = ""
    for line in text.splitlines():
        if line.startswith("== "):
            sim = line[3:].strip()
            continue
        f = line.split()
        if f and f[0] in ("!", "~", "="):  # sim_compare's mismatch marks
            f = f[1:]
        if sim in HOUSEHOLD and len(f) >= 2 and f[0] in ("pos", "facing"):
            field = f[0]
            nums = [float(v) for v in f[1].split(",")]  # the server's column
            entry = spots.setdefault(sim, [0.0, 0.0, 0.0, 1.0])
            if field == "pos":
                entry[0], entry[1] = nums[0], nums[2]
            else:
                entry[2], entry[3] = nums[0], nums[1]
    if set(spots) != set(HOUSEHOLD):
        print(text[-1500:])
        print("home: could not read every Sim's position from sim_compare")
        return 1
    HOME.write_text(json.dumps(spots, indent=2) + "\n", encoding="utf-8")
    print(f"home: {HOME} " + json.dumps(spots))
    return 0


def tune(endpoint: str, name: str, value: str | None = None) -> str:
    """One tunable on one game (scripts/tune.py): its reply, "name=value[ (default d)]" or an error."""
    _, text = python("scripts/tune.py", "--side", side_of(endpoint), name,
                     *([value] if value is not None else []), timeout=60)
    return text.strip()


def tune_endpoints(side: str) -> list[str] | None:
    """A --tune side's games: client (the one the run compares, default), server, both (those two), client2, client3,
    clients (players 1 and 2's games: a presentation tunable acts on each copy) or all; None for another word."""
    return {"server": [SERVER], "client": [CLIENT], "both": [SERVER, CLIENT], "client2": [compare_client.client2()],
            "client3": [compare_client.client_n(3)],
            "clients": [compare_client.CLIENT1, compare_client.client2()],
            "all": [SERVER, compare_client.CLIENT1, compare_client.client2()]}.get(side)


def apply_tunes(tunes: list[str]) -> tuple[list[tuple[str, str, str, str]], list[str]]:
    """Set each [SIDE:]name=value (client by default; sides in tune_endpoints) for one scenario. Returns what to put back
    (endpoint, name, the value it had or "default", the value set) and the problems. Set before every scenario: a client restart (a
    late-join scenario, a boot) is back to the code's defaults, and so is a scenario's own restart part way through."""
    restore, problems = [], []
    for item in tunes:
        side, _, pair = item.partition(":") if ":" in item.split("=", 1)[0] else ("client", "", item)
        name, _, value = pair.partition("=")
        import tune as tune_tool  # its float32 check: a value the mod would round is refused, not run
        if tune_tool.float32_problem(value):
            problems.append(f"step tune {item}: {tune_tool.float32_problem(value)}")
            continue
        for endpoint in tune_endpoints(side) or []:
            before = tune(endpoint, name)
            m = re.search(re.escape(name) + r"=(\S+)( \(default)?", before)
            restore.append((endpoint, name, m.group(1) if m and m.group(2) else "default", value))
            reply = tune(endpoint, name, value)
            if f"{name}=" not in reply or "error" in reply.lower():
                problems.append(f"step tune {item}: {reply.splitlines()[-1] if reply else 'no reply'}")
        if tune_endpoints(side) is None or not name or not value:
            problems.append(f"step tune {item}: not [server:|client:|both:|client2:|clients:|all:]name=value")
    return restore, problems


def same_value(now: str | None, value: str) -> bool:
    """A tunable read back (a float, as the mod keeps it) against the value set."""
    if now is None:
        return False
    try:
        return abs(float(now) - float(value)) <= 1e-6 * max(1.0, abs(float(value)))
    except ValueError:
        return now == value


TUNABLE_ROW = re.compile(r"(?m)^(?:\[\w+\] )?([\w.-]+)=(\S+)")


def tunable_values(endpoint: str) -> dict[str, str]:
    """Every tunable one game lists now (tune.py with no name: each read or set one, name=value), in one call; {} when
    the game does not answer."""
    try:
        _, text = python("scripts/tune.py", "--side", side_of(endpoint), timeout=60)
    except subprocess.TimeoutExpired:
        return {}
    return {} if "error=" in text else dict(TUNABLE_ROW.findall(text))


def lost_tunes(restore: list[tuple[str, str, str, str]]) -> list[str]:
    """The tunables no longer at the value set (a client restart part way through a scenario: the rest of it ran on
    the code's defaults). One list read per game."""
    lost = []
    values = {endpoint: tunable_values(endpoint) for endpoint in dict.fromkeys(e for e, _, _, _ in restore)}
    for endpoint, name, _, value in restore:
        now = values[endpoint].get(name)
        if not same_value(now, value):
            lost.append(f"step tune {name}={value} on the {'server' if endpoint == SERVER else 'client'} was "
                        f"{now or 'gone'} by the scenario's end (a restart): it ran on the defaults after it")
    return lost


def known_process(then: dict | None, now: dict | None) -> bool | None:
    """True when a game's TS3 is the process it was (pid and start time both read), False when it is another one,
    None when that cannot be told (no pid file, or no start time: a process gone, or one the OS will not open)."""
    if not then or not now or not then.get("started") or not now.get("started"):
        return None
    return same_process(then, now)


def hold_tunes(held: list[str], known: dict[str, dict] | None = None
               ) -> tuple[list[tuple[str, str, str, str]], list[str], list[str]]:
    """Tunables the caller holds for the whole run (scenarios.py arms --set): at each scenario's start each must be at
    its value; one that is not (a client restart put it back to the code's default) is set again, and never put back
    afterwards. Returns what to check at the scenario's end (lost_tunes, lost_held), the problems, and a note per one
    set again. known: each game's TS3 process when the holds were set (arms passes it with --held-process). While a
    game is still that process its holds are trusted without reading them: a list read is a mod call that builds a
    string, and boot 5's arms made none per scenario. A game that is another process gets them set again without a
    read (known is updated); one that cannot be told is read, as without known."""
    checks, problems, notes = [], [], []
    values: dict[str, dict[str, str]] = {}
    current = game_identity() if known is not None else {}
    for item in held:
        side, _, pair = item.partition(":") if ":" in item.split("=", 1)[0] else ("client", "", item)
        name, _, value = pair.partition("=")
        if side not in ("server", "client", "both") or not name or not value:
            problems.append(f"held {item}: not [server:|client:|both:]name=value")
            continue
        for endpoint in {"server": [SERVER], "client": [CLIENT], "both": [SERVER, CLIENT]}[side]:
            where = side_of(endpoint)
            same = known_process((known or {}).get(where), current.get(where)) if known is not None else None
            if same is False:
                reply = tune(endpoint, name, value)
                if f"{name}=" not in reply or "error" in reply.lower():
                    problems.append(f"held {name}={value} on the {where}: {reply.splitlines()[-1] if reply else 'no reply'}")
                    continue
                notes.append(f"held {name}={value}: the {where}'s TS3 is another process (pid "
                             f"{known[where]['pid']} -> {current[where]['pid']}): set again")
            if same is not None:
                checks.append((endpoint, name, "held-process", value))
                continue
            if endpoint not in values:
                values[endpoint] = tunable_values(endpoint)  # one list read per game
            now = values[endpoint].get(name)
            if not same_value(now, value):
                reply = tune(endpoint, name, value)
                where = "server" if endpoint == SERVER else "client"
                if f"{name}=" not in reply or "error" in reply.lower():
                    problems.append(f"held {name}={value} on the {where}: {reply.splitlines()[-1] if reply else 'no reply'}")
                    continue
                notes.append(f"held {name}={value} was {now or 'unset'} on the {where}: set again (a restart?)")
            checks.append((endpoint, name, "held", value))
    if known is not None:
        for where, identity in current.items():
            if known_process(known.get(where), identity) is False:
                known[where] = identity  # set again above: the new process holds them now
    return checks, problems, notes


def set_check_of(checks: list[tuple[str, str, str, str]]) -> dict[str, str]:
    """How a scenario checked each game's holds: "process" (its TS3 process unchanged: no mod call) or "tunables" (the
    tunable list read at the scenario's start and end: two mod calls, the fallback when its start time is unreadable)."""
    out: dict[str, str] = {}
    for endpoint, _, mode, _ in checks:
        side = side_of(endpoint)
        if mode == "held" or out.get(side) != "tunables":
            out[side] = "tunables" if mode == "held" else "process"
    return out


def lost_held(checks: list[tuple[str, str, str, str]], known: dict[str, dict]) -> list[str]:
    """The holds trusted by process (hold_tunes, "held-process") whose game is another TS3 process by the scenario's
    end, or one that cannot be told any more: the rest of the scenario ran on the defaults. No mod call."""
    if not checks:
        return []
    now = game_identity()
    lost = []
    for endpoint, name, _, value in checks:
        where = side_of(endpoint)
        if not known_process(known.get(where), now.get(where)):
            lost.append(f"held {name}={value} on the {where}: its TS3 changed during the scenario (pid "
                        f"{(known.get(where) or {}).get('pid')} -> {(now.get(where) or {}).get('pid')}): it ran on "
                        f"the defaults after that")
    return lost


def restore_tunes(restore: list[tuple[str, str, str, str]]) -> None:
    for endpoint, name, value, _ in reversed(restore):
        tune(endpoint, name, value)


def client_module() -> str:
    """The presentation module the client runs now (SACS_REPLAY 00: presentation=module=...): a client restart loads the
    installed one and drops a hot reload (presentation_reload.py), so a run that changes module part way through
    scored two builds."""
    try:
        _, text = python("scripts/bridgectl.py", "--endpoint", CLIENT, "SACS_REPLAY", "00", timeout=30)  # an empty batch: the report
    except subprocess.TimeoutExpired:
        return "?"
    m = re.search(r"presentation=module=([^/;\s]+)", text)
    return m.group(1) if m else "?"


def game_down(endpoint: str) -> str | None:
    """None when the game at endpoint answers DIAG_STATE and is in the world; else the reply's last line."""
    try:
        _, text = python("scripts/bridgectl.py", "--endpoint", endpoint, "DIAG_STATE", timeout=30)
    except subprocess.TimeoutExpired:
        text = "no answer in 30 s"
    if "accepted=True" in text and "inWorld=True" in text:
        import run_watchdog
        STATES[endpoint] = run_watchdog.parse_state(text)  # health()'s speed note
        return None
    return text.strip().splitlines()[-1] if text.strip() else "no answer"


def down_sides() -> list[str]:
    """The games that are down now ("server", "client"), one check each, no retries: an after hook's {down}."""
    return [side for side, endpoint in (("server", SERVER), ("client", CLIENT)) if game_down(endpoint) is not None]


def health() -> str | None:
    """None when both games answer and are in the world; else one line: what is down, and the newest TS3 crash report
    since the stack started. A dead game turns every later scenario into a BLOCKED or FAIL that costs minutes and says
    nothing (the server died twice on 2026-10-14)."""
    # Three tries 10 s apart: a client that a late-join scenario just restarted is still loading its world for a while.
    games = [("server", SERVER), ("client", CLIENT)] + [(compare_client.name(e), e) for e in EXTRA_CLIENTS if e != CLIENT]
    for attempt in range(3):
        down = [f"{side} {last[:100]}" for side, endpoint in games if (last := game_down(endpoint)) is not None]
        if not down:
            note = speed_note(games, STATES, HEALTH_TICKS)
            HEALTH_TICKS.update({e: STATES[e]["tick"] for _, e in games if (STATES.get(e) or {}).get("tick")})
            if note:
                print("   health: " + note, flush=True)
            ledger_record()
            return None
        if attempt < 2:
            time.sleep(10)
    crash = newest_crash()
    return "stack down: " + "; ".join(down) + (f" (crash report {crash}: {crash_name(crash)}; {ledger_line(crash)})"
                                               if crash else "")


# Each game's DIAG_STATE fields as game_down last read them in the world, and its tick at the last healthy health():
# a paused stack answers DIAG_STATE in the world, so health() calls it up and notes the speed (speed_note).
STATES: dict[str, dict[str, str]] = {}
HEALTH_TICKS: dict[str, str] = {}
TICK_POLLS = 10  # one-second reads of the server's tick after the reset resumes a paused stack
SIMCTL_TIMEOUT = 30  # simctl's own deadline for the resume, under the 60 s its process gets


def speed_note(games: list[tuple[str, str]], states: dict[str, dict[str, str]], ticks: dict[str, str]) -> str:
    """health()'s note, non-fatal: each game that reads Pause (commandedSpeed or observedSpeed) or whose simulator tick
    has not moved since the last health check; "" when every game runs. The next scenario's reset resumes a paused
    stack (resume_paused)."""
    notes = []
    for side, endpoint in games:
        fields = states.get(endpoint)
        if not fields:
            continue
        commanded, observed, tick = fields.get("commandedSpeed", "?"), fields.get("observedSpeed", "?"), fields.get("tick")
        still = tick is not None and ticks.get(endpoint) == tick
        if "Pause" in (commanded, observed) or still:
            notes.append(f"{side} commanded {commanded}, observed {observed}, tick {tick or '?'}"
                         + (" (not moved since the last check)" if still else ""))
    return ("speed: " + "; ".join(notes) + " (not fatal; the reset resumes a paused stack)") if notes else ""


def diag_state(endpoint: str) -> dict[str, str] | None:
    """One game's DIAG_STATE fields over its bridge, in process (run_watchdog.probe_state); None when it does not answer."""
    import run_watchdog
    return run_watchdog.probe_state(endpoint)[0]


def asks_for_pause(spec: dict) -> bool:
    """A scenario that wants the stack paused at its start ("speed": "Pause"). A rejoin:paused step pauses every game
    itself and resumes it (quick_loop's simctl_speed), so it does not count."""
    return str(spec.get("speed", "")).lower() == "pause"


def resume_paused(spec: dict, endpoints: list[str]) -> str:
    """The reset's first step, before a later player's join (so the joiner gets Normal): every game's DIAG_STATE
    commandedSpeed and observedSpeed (the server, client 1, the run's other clients and the scenario's). When any reads
    Pause and the scenario does not ask for it: Normal through the session (simctl speed normal: the controller's speed,
    which every game follows and a later joiner is sent at its join), then the server's tick read until it moves. One
    line for the record and the report; "" when no game was paused. On 2026-10-31 14:17-15:27Z the whole stack read
    Pause, no log names who paused it, and two runs BLOCKED five scenarios: queued picks never ran, and the high-LOD
    reads (Autonomy.IsSufficientlyOnScreenForHighLODSimulation) wait on simulator ticks."""
    if asks_for_pause(spec):
        return ""
    games = [("server", SERVER)] + [(compare_client.name(e), e)
                                    for e in dict.fromkeys([compare_client.CLIENT1, CLIENT, *EXTRA_CLIENTS, *endpoints])]
    states = {side: diag_state(endpoint) for side, endpoint in games}
    paused = [f"{side} {s.get('commandedSpeed', '?')}/{s.get('observedSpeed', '?')}" for side, s in states.items()
              if s and "Pause" in (s.get("commandedSpeed"), s.get("observedSpeed"))]
    if not paused:
        return ""
    seen = f"{', '.join(paused)}: commanded/observed" + "".join(f"; {side}: no answer"
                                                                 for side, s in states.items() if s is None)
    try:
        code, text = python("scripts/simctl.py", "--timeout", str(SIMCTL_TIMEOUT), "speed", "normal", timeout=60)
    except subprocess.TimeoutExpired:
        return f"stack was paused ({seen}); resume failed: simctl: no answer"
    reply = text.strip().splitlines()[-1][:100] if text.strip() else f"exit {code}"
    # Moved: two answered reads, the second higher (a server that did not answer the first read starts from its next).
    before = (states.get("server") or {}).get("tick")
    now = None
    for _ in range(TICK_POLLS):
        time.sleep(1)
        read = (diag_state(SERVER) or {}).get("tick")
        if read is None:
            continue
        if before is None:
            before = read
            continue
        now = read
        if tick_after(before, now):
            break
    return (f"stack was paused ({seen}); resumed: simctl speed normal: {reply}; "
            + (f"server tick {before} -> {now}" if before is not None and now is not None and tick_after(before, now)
               else f"the server's tick did not move in {TICK_POLLS} s (tick {now or before or '?'})"))


def tick_after(before: str, now: str) -> bool:
    """Whether the tick read now is higher than the one read before (False for one that is not a number)."""
    try:
        return int(now) > int(before)
    except ValueError:
        return False


def ledger_record() -> None:
    """Note the change set the games run (crash_ledger.py: git, mod build, SimLink, fork, packages, tunables), a row
    only when it changed, so a later crash reads against what was live. Never raises: a ledger never fails a run."""
    try:
        try:
            import crash_ledger
        except ImportError:
            crash_ledger = None

        crash_ledger.record("scenarios.py health", ROOT)
    except Exception:  # noqa: BLE001
        pass


def ledger_line(crash: str) -> str:
    """The change set live before a crash report and what changed from the one before (crash_ledger.crash_line)."""
    try:
        try:
            import crash_ledger
        except ImportError:
            crash_ledger = None

        return crash_ledger.crash_line(crash)
    except Exception as error:  # noqa: BLE001 - never hide that the stack is down
        return f"no change set ({error})"


def crash_name(path: str) -> str:
    """A crash report's exception and, when it is one already explained, its name (crash_catch.py triage)."""
    try:
        try:
            import crash_catch
        except ImportError:
            crash_catch = _MirrorProcFallback

        return crash_catch.describe_report(path, short=True)
    except Exception as error:  # a parse problem must never hide that the stack is down
        return f"not triaged ({error})"


def newest_crash() -> str:
    """The newest TS3 crash report (xcpt*.txt) written since the stack started: the server's profile or a client box."""
    sys.path.insert(0, str(ROOT / "scripts"))
    import quick_loop  # its machine paths (profiles, boxes)
    try:
        started = json.loads((ROOT / ".omo" / "run" / "server.state.json").read_text(encoding="utf-8"))["startedUtc"]
        since = calendar.timegm(time.strptime(started[:19], "%Y-%m-%dT%H:%M:%S"))
    except (OSError, KeyError, ValueError):
        since = time.time() - 6 * 3600
    paths = glob.glob(str(quick_loop.REAL / "xcpt*.txt"))
    for name in quick_loop.BOXES:
        paths += glob.glob(str(quick_loop.box(name) / "xcpt*.txt"))
    fresh = [path for path in paths if os.path.getmtime(path) >= since]
    return max(fresh, key=os.path.getmtime) if fresh else ""


def motion_dir(text: str) -> str:
    """The motion watch's run folder (samples.json, events.txt, report.txt) named in its output."""
    found = re.findall(r"all events \(\d+\): (.+?)[\\/]events\.txt", text)
    return found[-1].strip() if found else ""


def motion_failure(code: int | None, text: str) -> str | None:
    if code == 4:
        only = [ln.strip() for ln in text.splitlines() if "CLIENT-ONLY" in ln]
        return f"motion: {len(only)} client-only event(s): " + "; ".join(o.split("  ")[-2].strip() if "  " in o else o for o in only[:4])
    if code != 0:
        return f"motion watch failed (exit {code})"
    return None


def motion_states(folder: str | None) -> list[str]:
    """The states a motion watch found longer on one game (its summary.json, motion_watch.state_lengths): report only."""
    try:
        rows = json.loads((Path(folder) / "summary.json").read_text(encoding="utf-8")).get("state_lengths", []) if folder else []
    except (OSError, ValueError):
        return []
    return [f"{who} {name} server {s} ms, client {c} ms" for who, name, s, c in rows]


def images_in(text: str) -> list[str]:
    return re.findall(r"([A-Za-z]:\\[^\s]+?\.png|/[^\s]+?\.png)", text)


SOAK_SPEEDS = ("Normal", "Double", "Triple")
SOAK_CHECK = 30.0  # seconds between a soak's health checks: a dead game is seen within this, then confirmed (health())


def soak_spec(minutes: float, speed: str = "Normal", sims: str = "free") -> dict:
    """The one scenario a soak run is: both games in the world for MINUTES at SPEED with no scenario steps. Its minutes count as soak minutes, apart from scenario minutes (exposure)."""
    return {"name": "soak", "soak": minutes, "speed": speed, "sims": sims, "expect": "pass",
            "summary": f"{minutes:g} min in the world at {speed}, Sims {sims}, no scenarios"}


def set_speed(speed: str) -> list[str]:
    """SET_GAME_SPEED on both games' bridges, each reply's last line. Straight to each TS3, not through the session's
    SetGameSpeed command: the client would otherwise run at Normal against a faster server and only catch up at each
    world-time reconcile."""
    replies = []
    for side, endpoint in (("server", SERVER), ("client", CLIENT)):
        try:
            _, text = python("scripts/bridgectl.py", "--endpoint", endpoint, "SET_GAME_SPEED", speed, timeout=30)
        except subprocess.TimeoutExpired:
            text = "no answer in 30 s"
        replies.append(f"{side} {text.strip().splitlines()[-1][:100] if text.strip() else 'no reply'}")
    return replies


def soak_scenario(spec: dict, clock=time.time, sleep=time.sleep, check=None, speed_to=None, sims_to=None) -> dict:
    """Both games left in the world for spec["soak"] minutes at spec["speed"]; a health check every SOAK_CHECK
    seconds, and the soak stops at the first dead game (FAIL, with what health() says). Sims "free": every hold
    released, so autonomy runs the household; "held": the scenarios' reset (held, needs static). The speed is put back
    to Normal at the end. The record's timings are reset (the setup) and soak (the seconds the games stayed up), with
    no total, so the soak's minutes never count as scenario minutes."""
    check = check or health
    speed_to = speed_to or set_speed
    sims_to = sims_to or (lambda mode: reset() if mode == "held" else python("scripts/sim_act.py", "--release", "all")[1].strip())
    record = {"name": spec["name"], "kind": "soak", "summary": spec.get("summary", ""), "expect": "pass", "failures": [],
              "log": [], "images": [], "timings": {}, "soak_min": spec["soak"], "speed": spec.get("speed", "Normal"),
              "sims": spec.get("sims", "free"), "started": round(clock(), 1)}
    began = clock()
    record["log"].append(f"## sims {record['sims']}\n{sims_to(record['sims'])}")
    replies = speed_to(record["speed"])
    record["log"].append(f"## speed {record['speed']}\n" + "\n".join(replies))
    record["timings"]["reset"] = round(clock() - began, 1)
    if any("error" in reply.lower() or "no answer" in reply or "no reply" in reply for reply in replies):
        # a precondition, not an outcome: the soak would not run at the speed it is labelled with
        speed_to("Normal")
        record.update(failures=[f"speed {record['speed']} not set: {'; '.join(replies)}"], verdict="BLOCKED", met=False)
        return record
    began = clock()
    end = began + spec["soak"] * 60
    try:
        while not record["failures"] and clock() < end:
            sleep(min(SOAK_CHECK, max(0.0, end - clock())))
            at = clock()
            problem = check()
            if problem:
                record["timings"]["soak"] = round(at - began, 1)  # to the check that found it down, not its retries
                record["failures"].append(problem)
                record["log"].append(f"## down by {(at - began) / 60:.1f} min\n{problem}")
    finally:
        record["timings"].setdefault("soak", round(clock() - began, 1))
        if record["speed"] != "Normal":
            record["log"].append("## speed Normal\n" + "\n".join(speed_to("Normal")))
    verdict_of(record)
    return record


def watched_clients() -> list[str]:
    """The client games whose replay counters each scenario records: the compared one, and the other when it is in the
    world too (a two-player boot)."""
    others = [e for e in compare_client.clients() if e != CLIENT]
    return [CLIENT] + [e for e in others if game_down(e) is None]


def drivers_games() -> dict[str, str]:
    """The games the drivers check reads: the server and the watched clients (the compared one when none are set)."""
    import replay_check
    return {"server": SERVER, **{replay_check.label_of(e): e for e in (WATCHED or [CLIENT])}}


def replay_snapshot(drivers: bool) -> dict | None:
    """The watched clients' replay counters now and, with drivers, the drivers check on every game (one round trip,
    scripts/replay_check.py); None when no client is watched."""
    if not WATCHED:
        return None
    import replay_check
    try:
        return replay_check.snapshot(WATCHED, drivers_games() if drivers else None)
    except Exception as error:  # a reading problem must not cost the scenario its verdict
        return {"error": f"{type(error).__name__}: {error}"}


def note_replay(record: dict, before: dict | None) -> None:
    """record["replay"]: each watched client's counters before (after the reset) and after the scenario and their
    change; record["doubles"]: the drivers check after it (report only: an after entry `drivers` fails the scenario on
    one). The 2026-10-21 crumple was found by eye."""
    if before is None:
        return
    import replay_check
    after = replay_snapshot(True) or {}
    record["replay"] = {"before": before.get("counters"), "after": after.get("counters"),
                        "change": replay_check.boundary_changes(before, after)}
    if before.get("error") or after.get("error"):
        record["replay"]["error"] = before.get("error") or after.get("error")
    if after.get("doubles"):
        record["doubles"] = after["doubles"]


def client_log_name(endpoint: str) -> str | None:
    """The controller log a client game's controller writes in the checkout that booted it."""
    player = compare_client.player_of(endpoint)
    return None if player is None else "client.log" if player == "1" else f"client{player}.log"


def note_stream(record: dict, endpoints: dict[str, str], since: float, until: float, runs: list[Path] | None = None) -> None:
    """record["stream"]: each watched client's stream silences of 1.5 s or more inside the watch's wall-clock window
    (report only), read from its controller log in whichever checkout booted the games (scripts/stream_gaps.py; no
    mod call). A history gather blocks the whole stream (2026-10-21: 6.2-8.0 s on the established client at each
    warm rejoin), and a walk or clip that spans one is cut, so a failure next to a silence reads differently."""
    try:
        from datetime import datetime, timezone
        import stream_gaps
        if runs is None:
            try:
                import stack_who
            except ImportError:
                stack_who = None
            runs = [tree / ".omo" / "run" for tree in stack_who.worktrees()]
        start = datetime.fromtimestamp(since, timezone.utc).replace(tzinfo=None)
        end = datetime.fromtimestamp(until, timezone.utc).replace(tzinfo=None)
        notes = {}
        for endpoint in endpoints.values():
            name = client_log_name(endpoint)
            path = stream_gaps.covering_log(name, start, end, runs) if name else None
            if path is None:
                continue
            lines = stream_gaps.parse(path.read_text(encoding="utf-8", errors="replace"))
            silent = [stream_gaps.silent_text(g) for g in stream_gaps.window_gaps(lines, start, end)]
            if silent:
                notes[compare_client.name(endpoint)] = silent
        if notes:
            record["stream"] = notes
    except Exception as error:  # a reading problem must not cost the scenario its verdict
        record["stream_error"] = f"{type(error).__name__}: {error}"


def stream_line(record: dict) -> str:
    return "; ".join(f"{who} {text}" for who, texts in record.get("stream", {}).items() for text in texts)


def counter_value(text: str, key: str) -> int | None:
    """A counter in a bridge reply: the number after `key=` or `key:` (status fields such as seedCopy=...,reseeded=3
    or params=12/.../resent:7), where key is not the tail of a longer name; None when the reply has none."""
    found = re.search(rf"(?<![A-Za-z0-9]){re.escape(key)}[=:](-?\d+)", text)
    return int(found.group(1)) if found else None


def rose_failure(key: str, before: int | None, after: int | None) -> str | None:
    """Why a rose: check fails (the counter did not go up during the scenario), or None when it rose."""
    if before is None or after is None:
        return f"no {key} counter {'before the steps' if before is None else 'after them'}"
    return None if after > before else f"{key} did not rise ({before} -> {after})"


def check_endpoint(side: str, endpoints: dict[str, str], watched: list[str]) -> str:
    """The bridge an assert: or rose: check reads: the server, the scenario's first watched client, or client1/client2."""
    return (SERVER if side == "server" else endpoints[watched[0]] if side == "client"
            else compare_client.resolve(side[len("client"):]))


def read_counter(check: str, endpoints: dict[str, str], watched: list[str]) -> tuple[int | None, str]:
    """rose:<side>:<bridge command>:<counter>: the counter's value now and the reply it came from."""
    side, _, rest = check.partition(":")[2].partition(":")
    command, _, key = rest.partition(":")
    _, text = python("scripts/bridgectl.py", "--endpoint", check_endpoint(side, endpoints, watched), *command.split())
    return counter_value(text, key), text


def household_diffs(readings: dict[str, dict], names: list[str], pairs: list[str]) -> list[str]:
    """Where a client's reading (household_read.parse) differs from the server's: a named household's funds, a pair's
    relationship (liking and state), or a read that failed."""
    server = readings.get("server") or {}
    diffs = []
    for game, reading in readings.items():
        if reading.get("error"):
            diffs.append(f"{game}: read failed: {reading['error'][:120]}")
            continue
        if game == "server" or server.get("error"):
            continue
        for name in names:
            mine = reading["households"].get(name, {}).get("funds")
            theirs = server.get("households", {}).get(name, {}).get("funds")
            if mine != theirs:
                diffs.append(f"{game}: {name} funds {mine} (server {theirs})")
        for pair in pairs:
            mine, theirs = reading["pairs"].get(pair), server.get("pairs", {}).get(pair)
            if mine != theirs:
                diffs.append(f"{game}: {pair} {mine} (server {theirs})")
    return diffs


def households_check(rest: str, endpoints: dict[str, str], watched: list[str]) -> tuple[list[str], str]:
    """households:<household ...> [--pair A+B ...]: the named households (funds, members) and pairs (relationship) read
    on the server and each watched client (scripts/household_read.py); the differences from the server, and the
    readings as text."""
    import household_read
    args = _split(rest)
    pairs = [args[i + 1] for i, a in enumerate(args[:-1]) if a == "--pair"]
    names = [a for i, a in enumerate(args) if a != "--pair" and (i == 0 or args[i - 1] != "--pair")]
    games = {"server": SERVER} | {f"client {c}": endpoints[c] for c in watched}
    readings = {game: household_read.read(endpoint, names, None, pairs) for game, endpoint in games.items()}
    text = "\n".join(line for game, reading in readings.items() for line in household_read.summary(game, reading))
    return household_diffs(readings, names, pairs), text


def sampled(run: Callable[..., dict], *args) -> dict:
    """run(*args) with the machine's load sampled over it (machine_load, every ARM_LOAD_INTERVAL s) into the record's
    "load", which the report prints per scenario: P measured three clients' load by hand on 2026-10-21 (82% mean, 99%
    max CPU). The sampler never fails the scenario."""
    try:
        import machine_load
        sampler = machine_load.Sampler(interval=ARM_LOAD_INTERVAL).start()
    except Exception:  # noqa: BLE001 - the load is evidence beside a scenario
        sampler = None
    try:
        record = run(*args)
    finally:
        load = sampler.stop() if sampler else None
    if load:
        record["load"] = load
    return record


WATCHDOG = os.environ.get("SIMSONLINE_RUN_WATCHDOG", "1") != "0"
WATCH = None  # the running scenario's run_watchdog.Watchdog (watched()): the step loop reads it, rejoin steps hold it


def watched(run: Callable[..., dict], *args) -> dict:
    """run(*args) under scripts/run_watchdog.py on the games health() checks. Two misses 30 s apart (a game gone, a
    bridge that stopped answering, the simulator or the clock still while unpaused) stop the scenario's running steps
    (stop_children), and it reads CRASHED with the watchdog's line, the newest crash report and the change set, as
    health() words a dead stack; the next scenario's health() then finds the stack down. SIMSONLINE_RUN_WATCHDOG=0:
    no watchdog."""
    global WATCH
    if not WATCHDOG:
        return run(*args)
    import run_watchdog

    def fire(found: str) -> None:
        print(f"   watchdog: {found}; stopping the scenario's steps", flush=True)
        stop_children()

    dog = run_watchdog.Watchdog(run_watchdog.stack_games(SERVER, [CLIENT] + EXTRA_CLIENTS), on_fire=fire)
    WATCH = dog
    dog.start()
    try:
        record = run(*args)
    finally:
        dog.stop()
        WATCH = None
    if dog.errors:
        print(f"   watchdog: {len(dog.errors)} check(s) raised (the watch went on): {dog.errors[0]}", flush=True)
    if dog.found:
        ledger_record()
        crash = newest_crash()
        mark_crashed(record, "watchdog: " + dog.found + (f" (crash report {crash}: {crash_name(crash)}; {ledger_line(crash)})"
                                                         if crash else ""), over="cut short")
    return record


def rejoined(step: str) -> list[str]:
    """The client endpoints a rejoin step restarts on purpose (quick_loop's rejoin[:1|2|3][:paused][:as=...]; rejoin:warm
    restarts client 1's controller): the watchdog holds them while it runs."""
    kind, _, rest = step.partition(":")
    if kind != "rejoin":
        return []
    parts = rest.split(":") if rest else []
    if parts[:1] == ["warm"]:
        return [compare_client.CLIENT1]
    player = next((p for p in parts if p in compare_client.PLAYERS), None)
    return [compare_client.client_n(player)] if player else [CLIENT]


# Card 55 section E: step and check kinds that only the Rust headless server's route mechanism can run. None has an
# executor yet (add a kind to RUST_ONLY_EXECUTORS when its executor lands). Without this gate an unknown check kind
# falls to quick_step and its exit code is ignored, so the scenario would PASS by omission.
RUST_ONLY_KINDS = ("claim-sim", "rust-env", "load-simnav", "navigate_astar", "rust-evidence")
RUST_ONLY_EXECUTORS: frozenset[str] = frozenset()
OFFLINE_STEP_KINDS = (
    "set_sim_pose", "route_and_wait", "route_to_object",
    "cancel_interaction", "teleport_sim", "switch_active_household", "reset_sim",
)
SIM_WIRE_IDS = {"jennifer": 18031287978496800304, "pablo": 18031287978496800272}
DEFAULT_LIBRARY_LOT = 7787005354294925872
SELECTOR_RE = re.compile(r"^simnav:([0-9a-f]{16}):(0|[1-9][0-9]*)$")


def uses_rust_only(spec: dict) -> bool:
    kinds = {item.partition(":")[0] for item in spec.get("steps", []) + spec.get("after", [])}
    return bool(kinds & set(RUST_ONLY_KINDS))


def is_card55_scenario(spec: dict) -> bool:
    if uses_rust_only(spec) or bool(spec.get("_card55_scope")):
        return True
    for item in spec.get("after", []):
        kind, _, rest = item.partition(":")
        if kind == "sims" and any(flag in rest.split() for flag in ("--e5", "--settle-bracket", "--settled-mm", "--mm", "--landing")):
            return True
    return False


_COORD_AT_RE = re.compile(
    r"\b(?P<key>at|to|from|pos|posmm|goal|target|dest|spot)=\s*[\[(]?-?\d+(?:\.\d+)?(?:\s*,\s*-?\d+(?:\.\d+)?)+[\])]?"
)
_COORD_POS_RE = re.compile(
    r"\b(?P<kind>pos|posmm|facing)\|[^|\r\n]+\|-?\d+(?:\.\d+)?(?:\s*,\s*-?\d+(?:\.\d+)?)+"
)
_COORD_VP_RT_RE = re.compile(r"\bVp\|rt\|[^\r\n]+")
_COORD_TUPLE_RE = re.compile(
    r"(?:\[\s*-?\d+(?:\.\d+)?\s*,\s*-?\d+(?:\.\d+)?(?:\s*,\s*-?\d+(?:\.\d+)?)*\s*\]"
    r"|\(\s*-?\d+(?:\.\d+)?\s*,\s*-?\d+(?:\.\d+)?(?:\s*,\s*-?\d+(?:\.\d+)?)*\s*\)"
    r"|(?<![A-Za-z0-9_.-])(?:-?\d+\.\d+\s*,\s*-?\d+(?:\.\d+)?(?:\s*,\s*-?\d+(?:\.\d+)?)*"
    r"|-?\d+\s*,\s*-?\d+\.\d+(?:\s*,\s*-?\d+(?:\.\d+)?)*"
    r"|-?\d+(?:\.\d+)?\s*,\s*-?\d+(?:\.\d+)?\s*,\s*-?\d+(?:\.\d+)?(?:\s*,\s*-?\d+(?:\.\d+)?)*)(?![A-Za-z0-9_.-]))"
)


def sanitize_card55_text(text: str) -> str:
    """Redact any coordinate-bearing tokens (at=..., pos|..., posmm|..., Vp|rt|..., float/int coordinate tuples) from Card 55 logs."""
    if not isinstance(text, str) or not text:
        return ""
    out = _COORD_AT_RE.sub(lambda m: f"{m.group('key')}=<redacted>", text)
    out = _COORD_POS_RE.sub("pos|<redacted>", out)
    out = _COORD_VP_RT_RE.sub("Vp|rt|<redacted>", out)
    clean_lines = []
    for line in out.splitlines():
        s = line.strip()
        if re.match(r"^[!~=k]?\s*(?:pos|posmm|facing)\b", s):
            field = re.sub(r"^[!~=k]?\s*", "", s).split()[0]
            mark = s[0] + " " if s and s[0] in "!~=k" else "  "
            clean_lines.append(f" {mark}{field:<26} <redacted>")
        else:
            clean_lines.append(_COORD_TUPLE_RE.sub("<coords-redacted>", line))
    return "\n".join(clean_lines)


def _parse_selector(value: str, expected_hash: str | None = None) -> tuple[str, int]:
    if not isinstance(value, str) or "_UNAVAILABLE" in value:
        raise ValueError(f"unpinned selector {value!r}")
    m = SELECTOR_RE.fullmatch(value.strip())
    if not m:
        raise ValueError(f"invalid selector syntax {value!r}")
    content_hash, cell_str = m.group(1), m.group(2)
    cell = int(cell_str)
    if cell >= (1 << 32):
        raise ValueError(f"selector cell ordinal out of range: {cell}")
    if expected_hash is not None and content_hash != expected_hash:
        raise ValueError(f"selector content_hash mismatch: {content_hash} != {expected_hash}")
    return content_hash, cell


def parse_navigate_astar_args(payload: str, expected_hash: str | None = None) -> dict:
    if not isinstance(payload, str) or not payload.strip():
        raise ValueError("navigate_astar requires '<Sim> [start=simnav:<16hex>:<cell>] goal=simnav:<16hex>:<cell>'")
    tokens = payload.strip().split()
    sim = tokens[0]
    if "=" in sim or not re.fullmatch(r"[A-Za-z][A-Za-z0-9_-]*", sim):
        raise ValueError(f"invalid navigate_astar Sim name {sim!r}")
    kv: dict[str, str] = {}
    for tok in tokens[1:]:
        key, sep, val = tok.partition("=")
        if not sep or key not in ("start", "goal") or key in kv or not val:
            raise ValueError(f"invalid navigate_astar token {tok!r}")
        kv[key] = val
    if "goal" not in kv:
        raise ValueError("navigate_astar requires goal=simnav:<16hex>:<cell>")
    goal_hash, goal_cell = _parse_selector(kv["goal"], expected_hash)
    start_cell = None
    if "start" in kv:
        _, start_cell = _parse_selector(kv["start"], goal_hash)
    return {"sim": sim, "content_hash": goal_hash, "start_cell": start_cell, "goal_cell": goal_cell}


def card55_preflight_gap(spec: dict) -> str | None:
    """Validate Card 55 scenario prerequisites before reset/ensure_player ever touch a game."""
    items = list(spec.get("steps", [])) + list(spec.get("after", []))
    used = [item for item in items if item.partition(":")[0] in RUST_ONLY_KINDS]
    if not used:
        return None
    problems: list[str] = []
    start = spec.get("start")
    has_unpinned_nav = any("_UNAVAILABLE" in s for s in spec.get("steps", []) if s.startswith("navigate_astar:"))
    if isinstance(start, str):
        start_path = HOME.parent / (start if start.endswith(".json") else start + ".json")
        if not start_path.exists():
            problems.append(f"missing start file {start_path.name}")
        elif start == "home" and (spec.get("name") == "routed_walk_multiroom_lot" or has_unpinned_nav):
            problems.append("start 'home' is not a measured library start")
    elif "start" in spec and not start:
        problems.append("missing measured start spot")

    seen_hash: str | None = None
    for item in used:
        kind, _, payload = item.partition(":")
        if kind == "load-simnav" and payload != "memory":
            problems.append(f"load-simnav requires 'memory', got {payload!r}")
        elif kind == "rust-env" and payload not in ("SIMSONLINE_RUST_ROUTE=1", "SIMSONLINE_RUST_ROAD_PLACE=1"):
            problems.append(f"unsupported rust-env payload {payload!r}")
        elif kind == "navigate_astar" and ("=" in payload or "_UNAVAILABLE" in payload or spec.get("name") == "routed_walk_multiroom_lot"):
            try:
                parsed = parse_navigate_astar_args(payload, seen_hash)
                seen_hash = parsed["content_hash"]
            except ValueError as exc:
                problems.append(str(exc))
        elif kind == "rust-evidence" and payload not in ("E1", "E2", "E3", "E4", "E5"):
            problems.append(f"unknown rust-evidence gate {payload!r}")

    if problems:
        return "card 55 preflight: " + "; ".join(problems)
    return None


class Card55Scope:
    """In-memory Card 55 scenario runner scope and fail-closed step/evidence adapters (zero coordinates persisted)."""

    def __init__(self, *, run_id: str = "card55-run", boot_id: str = "boot-1", server_pid: int = 1,
                 epoch: int = 1, generation: int = 1, owner: str = "player1",
                 player_id: int | None = None, conn_id: int | None = None,
                 lot_id: int = DEFAULT_LIBRARY_LOT, admin_factory=None,
                 server_endpoint: str = SERVER,
                 client_endpoints: dict[str, str] | None = None,
                 e1_sources: dict | None = None,
                 continuity_metrics: dict | None = None,
                 e5_sampler=None,
                 archive_dir: Path | None = None) -> None:
        self.run_id = run_id
        self.boot_id = boot_id
        self.server_pid = server_pid
        self.epoch = epoch
        self.generation = generation
        self.corr = 1
        self.actor = 0
        self.sim_name = ""
        self.kernel_actor = 0
        self.owner = owner
        self._explicit_player_id = player_id
        if isinstance(owner, str):
            m_owner = re.fullmatch(r"player([1-9]\d*)", owner.strip())
            self.player_id = int(m_owner.group(1)) if m_owner else None
        else:
            self.player_id = None
        if player_id is not None:
            if type(player_id) is int and player_id >= 1:
                if owner is None:
                    self.owner = f"player{player_id}"
                    self.player_id = player_id
                elif isinstance(owner, str) and owner.strip() == f"player{player_id}":
                    self.player_id = player_id
                else:
                    self.player_id = None
            else:
                self.player_id = None
        self.conn_id = conn_id
        self.lot_id = lot_id
        self.start_ns = 100
        self.end_ns = 200
        self.bridge_connected = True
        self.claim_held = False
        self.instrument_ready = True
        self.content_hash: str | None = None
        self.e2_before: str | None = None
        self.attempted_walks: list[dict] = []
        self.executed_walks: list[dict] = []
        self.walks = self.attempted_walks
        self.executed_routes = 0
        self.admin_factory = admin_factory
        self.server_endpoint = server_endpoint
        self.client_endpoints = client_endpoints or {
            "client1": compare_client.resolve("1"),
            "client2": compare_client.resolve("2"),
        }
        self.e1_sources = e1_sources
        self.continuity_metrics = continuity_metrics
        self.e5_sampler = e5_sampler
        self.archive_dir = archive_dir

    def _admin(self, endpoint: str):
        import card55_live as live
        factory = self.admin_factory or live.Admin
        return factory(endpoint)

    def scope_dict(self, *, corr: int | None = None) -> dict:
        return {
            "run_id": self.run_id,
            "boot_id": self.boot_id,
            "server_pid": self.server_pid,
            "epoch": self.epoch,
            "corr": self.corr if corr is None else corr,
            "actor": self.actor if self.actor > 0 else 1,
            "generation": self.generation,
            "owner": self.owner,
            "start_ns": self.start_ns,
            "end_ns": self.end_ns,
            "bridge_connected": self.bridge_connected,
            "claim_held": self.claim_held,
            "instrument_ready": self.instrument_ready,
            "coverage_start_ns": self.start_ns,
            "coverage_end_ns": self.end_ns,
            "collector_status": "complete",
        }

    def _observation(self, extra: dict, *, corr: int | None = None) -> dict:
        s = self.scope_dict(corr=corr)
        base = {
            "schema_version": 1,
            "scope": {k: s[k] for k in ("run_id", "boot_id", "server_pid", "epoch", "corr",
                                        "actor", "generation", "owner", "start_ns", "end_ns")},
            "owner": self.owner,
            "bridge_connected": self.bridge_connected,
            "claim_held": self.claim_held,
            "instrument_ready": self.instrument_ready,
            "coverage_start_ns": self.start_ns,
            "coverage_end_ns": self.end_ns,
            "collector_status": "complete",
        }
        base.update(extra)
        return base

    def execute(self, kind: str, payload: str) -> dict:
        import card55_live as live
        import routing_evidence as evidence
        if kind == "claim-sim":
            return self._exec_claim_sim(payload, live, evidence)
        if kind == "rust-env":
            return self._exec_rust_env(payload, live, evidence)
        if kind == "load-simnav":
            return self._exec_load_simnav(payload, live, evidence)
        if kind == "navigate_astar":
            return self._exec_navigate_astar(payload, live, evidence)
        if kind == "rust-evidence":
            return self._exec_rust_evidence(payload, live, evidence)
        raise ValueError(f"unknown Rust step {kind}")

    def _resolve_expected_player_id(self) -> tuple[int | None, str | None]:
        raw_owner = self.owner
        if not isinstance(raw_owner, str) or not raw_owner.strip():
            return None, "claim-sim: expected player_id cannot be established from owner"
        m_owner = re.fullmatch(r"player([1-9]\d*)", raw_owner.strip())
        if not m_owner:
            return None, f"claim-sim: expected player_id cannot be established from owner {raw_owner!r}"
        owner_pid = int(m_owner.group(1))
        if self._explicit_player_id is not None:
            if type(self._explicit_player_id) is not int or self._explicit_player_id < 1:
                return None, "claim-sim: invalid explicit player_id"
            if self._explicit_player_id != owner_pid:
                return None, "claim-sim: explicit player_id conflicts with owner"
        if self.player_id is not None:
            if type(self.player_id) is not int or self.player_id < 1 or self.player_id != owner_pid:
                return None, "claim-sim: player_id conflicts with owner"
        return owner_pid, None

    def _exec_claim_sim(self, payload: str, live, evidence) -> dict:
        sim_name = payload.strip()
        if not sim_name:
            return evidence.result("BLOCKED", "claim-sim: missing Sim name")
        expected_player_id, owner_err = self._resolve_expected_player_id()
        if owner_err or expected_player_id is None:
            self.kernel_actor = 0
            self.claim_held = False
            return evidence.result("BLOCKED", owner_err or "claim-sim: expected player_id cannot be established from owner")
        expected_conn_id = self.conn_id
        if expected_conn_id is not None and (type(expected_conn_id) is not int or expected_conn_id <= 0 or expected_conn_id >= (1 << 64)):
            self.kernel_actor = 0
            self.claim_held = False
            return evidence.result("BLOCKED", "claim-sim: invalid expected conn_id")
        try:
            server_admin = self._admin(self.server_endpoint)
            status_raw = server_admin.request("SACS_RECORDER rust-navigation-status")
            raw_claims = [
                field.partition("=")[2].strip()
                for field in re.split(r"[|;\n]", status_raw if isinstance(status_raw, str) else "")
                if field.partition("=")[0].strip() == "actor_claim"
            ]
            fields = live.counter_fields(status_raw)
        except Exception as exc:
            self.kernel_actor = 0
            self.claim_held = False
            return evidence.result("BLOCKED", f"claim-sim: {exc}")

        if "epoch" in fields and fields["epoch"].isdigit() and int(fields["epoch"]) > 0:
            self.epoch = int(fields["epoch"])
        if "generation" in fields and fields["generation"].isdigit() and int(fields["generation"]) > 0:
            self.generation = int(fields["generation"])

        expected_wire = SIM_WIRE_IDS.get(sim_name.lower(), 0)
        reported_sim = fields.get("sim", "").strip()
        status_actor = int(fields["actor"]) if re.fullmatch(r"[1-9][0-9]*", fields.get("actor", "")) else 0
        if expected_wire > 0:
            target_wire = expected_wire
        elif reported_sim.lower() == sim_name.lower() and status_actor > 0:
            target_wire = status_actor
        else:
            target_wire = 0

        if target_wire > 0:
            self.actor = target_wire
        self.sim_name = sim_name

        # Strict per-actor claim check: global mapping_checked=1 or actor+kernel_sim without a complete
        # Sim-bound, owner-matching per-actor (conn_id, player_id, wire_u64, kernel_u32) tuple is NEVER sufficient.
        sim_binding_ok = (
            (bool(reported_sim) and reported_sim.lower() == sim_name.lower() and (status_actor == 0 or status_actor == target_wire))
            or (not reported_sim and expected_wire > 0 and status_actor == expected_wire)
        )
        claim_tokens: list[str] = []
        for raw_claim in (raw_claims or ([fields["actor_claim"]] if "actor_claim" in fields else [])):
            for tok in re.split(r"[,\s]+", raw_claim.strip()):
                if tok:
                    claim_tokens.append(tok)

        has_verified_pair = False
        kernel_sim = 0
        if sim_binding_ok and target_wire > 0 and claim_tokens:
            matching_target_tuples: list[tuple[int, int, int, int]] = []
            malformed_claim = False
            for tok in claim_tokens:
                parts = tok.split(":")
                if len(parts) != 4 or not all(re.fullmatch(r"[1-9][0-9]*", p) for p in parts):
                    malformed_claim = True
                    break
                c_conn, c_player, c_wire, c_kernel = (int(p) for p in parts)
                if c_wire == target_wire:
                    matching_target_tuples.append((c_conn, c_player, c_wire, c_kernel))

            if not malformed_claim and len(set(matching_target_tuples)) > 1:
                self.kernel_actor = 0
                self.claim_held = False
                return evidence.result("BLOCKED", f"claim-sim: conflicting actor_claim tuples for wire_u64 {target_wire}")

            if not malformed_claim and len(set(matching_target_tuples)) == 1:
                c_conn, c_player, c_wire, c_kernel = matching_target_tuples[0]
                owner_ok = (expected_player_id is not None
                            and c_player == expected_player_id
                            and (expected_conn_id is None or c_conn == expected_conn_id)
                            and (not fields.get("actor") or status_actor == c_wire)
                            and (not fields.get("kernel_sim") or fields.get("kernel_sim") == str(c_kernel)))
                if (0 < c_conn < (1 << 64) and c_player > 0 and c_wire == target_wire
                        and c_wire < (1 << 64) and 2 <= c_kernel < (1 << 32) and owner_ok):
                    kernel_sim = c_kernel
                    has_verified_pair = True

        self.kernel_actor = kernel_sim if has_verified_pair else 0
        self.claim_held = has_verified_pair
        obs = self._observation({
            "sim_name": sim_name,
            "kernel_actor": self.kernel_actor,
            "mapping_checked": bool(has_verified_pair),
        })
        return evidence.step_observation("claim-sim", sim_name, self.scope_dict(), obs)

    def _exec_rust_env(self, payload: str, live, evidence) -> dict:
        if payload not in ("SIMSONLINE_RUST_ROUTE=1", "SIMSONLINE_RUST_ROAD_PLACE=1"):
            return evidence.result("BLOCKED", "unsupported route switch requirement")
        try:
            try:
                from . import card55_g0
            except ImportError:
                import card55_g0
            server_admin = self._admin(self.server_endpoint)
            sacs_status = server_admin.request("SACS_RECORDER status")
            road_status = server_admin.request("SACS_RECORDER road-place-census")
            front_door = server_admin.request("FRONT_DOOR_STATE")
            fd_fields = live.counter_fields(front_door)
            for key in ("navigate_requests", "plans_served", "native_oracle_owned"):
                val = fd_fields.get(key)
                if not isinstance(val, str) or not re.fullmatch(r"[0-9]+", val):
                    return evidence.result("BLOCKED", f"unmeasured or missing {key} in FRONT_DOOR_STATE")
            route_on = "rustRoute:on" in sacs_status or "rust-route=on" in sacs_status or "rustRoute=on" in sacs_status
            try:
                road_mode, _ = card55_g0.census(road_status)
                road_on = road_mode == "on"
                census_complete = True
            except ValueError:
                road_on = False
                census_complete = False
            suppression_ok = True
            for side, ep in self.client_endpoints.items():
                c_reply = self._admin(ep).request("SACS_RECORDER authority")
                c_fields = live.counter_fields(c_reply)
                if not (c_fields.get("suppression_complete") in ("1", "true", "True")
                        or (c_fields.get("role") == "client"
                            and c_fields.get("presenter_hook") == "present"
                            and c_fields.get("authority_hook") == "present"
                            and c_fields.get("puppet_suppressed") in ("1", "true", "True"))):
                    suppression_ok = False
            self.e2_before = front_door
        except Exception as exc:
            return evidence.result("BLOCKED", f"rust-env: {exc}")

        obs = self._observation({
            "route_enabled": bool(route_on and road_on),
            "census_complete": bool(census_complete),
            "suppression_complete": bool(suppression_ok),
        })
        return evidence.step_observation("rust-env", "SIMSONLINE_RUST_ROUTE=1", self.scope_dict(), obs)

    def _exec_load_simnav(self, payload: str, live, evidence) -> dict:
        if payload != "memory":
            return evidence.step_observation("load-simnav", payload, self.scope_dict(), self._observation({"topology": {}}))
        if not self.claim_held:
            return evidence.result("BLOCKED", "load-simnav requires claimed Sim (claim-sim first)")
        try:
            server_admin = self._admin(self.server_endpoint)
            actor_arg = f" {self.actor}" if self.actor > 0 else ""
            reply = server_admin.request(f"SACS_RECORDER rust-simnav-install {self.lot_id}{actor_arg}")
            fields = live.counter_fields(reply)
            if fields.get("rust-simnav-install") != "accepted":
                reason = fields.get("reason", "install not accepted")
                return evidence.result("BLOCKED", f"load-simnav refused: {reason}")
            status_reply = server_admin.request("SACS_RECORDER rust-navigation-status")
            s_fields = live.counter_fields(status_reply)
            if "epoch" in fields and fields["epoch"].isdigit():
                self.epoch = int(fields["epoch"])
            if "generation" in fields and fields["generation"].isdigit():
                self.generation = int(fields["generation"])
            if "actor" in fields and fields["actor"].isdigit() and int(fields["actor"]) > 0:
                self.actor = int(fields["actor"])
            if "kernel_sim" in fields and fields["kernel_sim"].isdigit() and int(fields["kernel_sim"]) >= 2:
                self.kernel_actor = int(fields["kernel_sim"])
            content_hash = fields.get("content_hash") or s_fields.get("content_hash", "")
            cells = int(fields["cells"]) if fields.get("cells", "").isdigit() else 0
            portals = int(fields["portals"]) if fields.get("portals", "").isdigit() else 0
            topology = {
                "provenance": fields.get("provenance", s_fields.get("provenance", "")),
                "storage": fields.get("storage", s_fields.get("storage", "")),
                "inferred_partition": s_fields.get("inferred_partition", "0") in ("1", "true", "True"),
                "inferred_stairs": s_fields.get("inferred_stairs", "0") in ("1", "true", "True"),
                "surfaces_measured": s_fields.get("surfaces_measured", "1" if fields.get("provenance") == "measured" else "0") in ("1", "true", "True"),
                "portals_measured": s_fields.get("portals_measured", "1" if fields.get("provenance") == "measured" else "0") in ("1", "true", "True"),
                "obstacles_and_locks_measured": s_fields.get("obstacles_and_locks_measured", "1" if fields.get("provenance") == "measured" else "0") in ("1", "true", "True"),
                "cross_lot_measured": s_fields.get("cross_lot_measured", "1" if fields.get("provenance") == "measured" else "0") in ("1", "true", "True"),
                "cells": cells,
                "portals": portals,
                "content_hash": content_hash,
            }
        except Exception as exc:
            return evidence.result("BLOCKED", f"load-simnav: {exc}")

        out = evidence.step_observation("load-simnav", "memory", self.scope_dict(), self._observation({"topology": topology}))
        if out["verdict"] == "PASS":
            self.content_hash = content_hash
        return out

    def _exec_navigate_astar(self, payload: str, live, evidence) -> dict:
        try:
            parsed = parse_navigate_astar_args(payload, self.content_hash)
        except ValueError as exc:
            return evidence.result("BLOCKED", f"navigate_astar: {exc}")
        walk_corr = len(self.attempted_walks) + 1
        self.corr = walk_corr
        walk_entry = {"corr": walk_corr, **parsed}
        self.attempted_walks.append(walk_entry)
        # Delegate to routing_evidence.step_observation("navigate_astar", ...) which honestly blocks
        # until owner-gated gameplay trigger and OPEN-1/OPEN-2 execution adapters land.
        outcome = evidence.step_observation("navigate_astar", payload, self.scope_dict(corr=walk_corr), None)
        if outcome["verdict"] == "PASS":
            self.executed_walks.append(walk_entry)
            self.executed_routes += 1
        return outcome

    def _archive_put(self, name: str, row: dict) -> None:
        if self.archive_dir is not None:
            import card55_live as live
            live.Archive(self.archive_dir).put(name, row)

    def _exec_rust_evidence(self, gate: str, live, evidence) -> dict:
        gate = gate.strip()
        if gate not in ("E1", "E2", "E3", "E4", "E5"):
            return evidence.result("BLOCKED", "unknown evidence selector; use exactly E1, E2, E3, E4 or E5")
        try:
            if gate == "E1":
                if not isinstance(self.e1_sources, dict):
                    row = live._row("E1")
                else:
                    row = live.collect_e1(
                        self.scope_dict(),
                        self.e1_sources.get("trace", {}),
                        self.e1_sources.get("control", {}),
                        self.e1_sources.get("fork", {}),
                    )
                self._archive_put("E1", row)
                return evidence.result(row["verdict"], f"E1 {row['verdict']}: counts={json.dumps(row['counts'], sort_keys=True)}")

            if gate == "E2":
                routes = self.executed_routes if self.executed_routes > 0 else len(self.executed_walks)
                if not self.e2_before or routes <= 0:
                    row = live._row("E2")
                else:
                    after = self._admin(self.server_endpoint).request("FRONT_DOOR_STATE")
                    # Never substitute attempted/blocked walk count for independently observed executed_routes.
                    row = live.collect_e2(self.scope_dict(), self.e2_before, after, routes)
                self._archive_put("E2", row)
                return evidence.result(row["verdict"], f"E2 {row['verdict']}: counts={json.dumps(row['counts'], sort_keys=True)}")

            walks = self.executed_walks or self.attempted_walks or [{"corr": self.corr}]
            walk_rows = []
            for idx, w in enumerate(walks, 1):
                w_corr = w["corr"]
                w_scope = self.scope_dict(corr=w_corr)
                if gate == "E3":
                    reqs = {
                        "daemon": self._admin(self.server_endpoint).plan_page(w_corr, w_scope["actor"], client=False),
                        "client1": self._admin(self.client_endpoints["client1"]).plan_page(w_corr, w_scope["actor"], client=True, progress=False),
                        "client2": self._admin(self.client_endpoints["client2"]).plan_page(w_corr, w_scope["actor"], client=True, progress=False),
                    }
                    r = live.collect_e3(w_scope, reqs)
                elif gate == "E4":
                    reqs = {
                        "client1": self._admin(self.client_endpoints["client1"]).plan_page(w_corr, w_scope["actor"], client=True, progress=True),
                        "client2": self._admin(self.client_endpoints["client2"]).plan_page(w_corr, w_scope["actor"], client=True, progress=True),
                    }
                    cont = self.continuity_metrics.get(w_corr, self.continuity_metrics) if isinstance(self.continuity_metrics, dict) else {}
                    r = live.collect_e4(w_scope, reqs, cont)
                else:  # E5
                    if self.e5_sampler is not None:
                        r = self.e5_sampler(w_scope)
                    else:
                        import sim_compare
                        sampled = sim_compare.sample_e5_bracket(
                            self.sim_name or "Jennifer",
                            scope=w_scope,
                            server_endpoint=self.server_endpoint,
                            client_endpoints=self.client_endpoints,
                            admin_factory=self.admin_factory,
                        )
                        r = {k: sampled[k] for k in ("gate", "verdict", "hashes", "counts", "errors_mm")}
                self._archive_put(f"walk-{idx}-{gate}", r)
                walk_rows.append(r)

            combined = walk_rows[0] if len(walk_rows) == 1 else live.combine_walks(gate, walk_rows)
            self._archive_put(gate, combined)
            summary = f"{gate} {combined['verdict']}: counts={json.dumps(combined['counts'], sort_keys=True)} errors_mm={json.dumps(combined['errors_mm'], sort_keys=True)}"
            return evidence.result(combined["verdict"], summary)
        except Exception as exc:
            return evidence.result("BLOCKED", f"{gate}: {exc}")


# Offline fail-closed step adapters (never touch live games; validate state/targets strictly in memory).
def _offline_state(state: dict | None) -> dict:
    if not isinstance(state, dict) or not state.get("offline", True):
        raise ValueError("offline state dictionary required (live game execution is forbidden)")
    return state


def _require_sim_entry(state: dict, sim: str) -> dict:
    if not isinstance(sim, str) or not sim.strip():
        raise ValueError("missing Sim name")
    sims = state.get("sims")
    if not isinstance(sims, dict) or sim not in sims or not isinstance(sims[sim], dict):
        raise ValueError(f"Sim {sim!r} not found in offline state")
    return sims[sim]


def set_sim_pose(sim: str, pose: str, *, state: dict | None = None) -> dict:
    try:
        st = _offline_state(state)
        entry = _require_sim_entry(st, sim)
        if not isinstance(pose, str) or not pose.strip():
            raise ValueError("missing pose name")
        allowed = st.get("valid_poses")
        if isinstance(allowed, (set, list, tuple)) and pose not in allowed:
            raise ValueError(f"unsupported pose {pose!r}")
        entry["pose"] = pose.strip()
        return {"verdict": "PASS", "reason": f"set_sim_pose {sim} -> {pose.strip()}"}
    except Exception as exc:
        return {"verdict": "BLOCKED", "reason": f"set_sim_pose: {exc}"}


def route_and_wait(sim: str, goal_cell: str | int, *, state: dict | None = None) -> dict:
    try:
        st = _offline_state(state)
        entry = _require_sim_entry(st, sim)
        cells = st.get("cells")
        if not isinstance(cells, (dict, set, list, tuple)):
            raise ValueError("no measured navigation cells in offline state")
        if isinstance(goal_cell, str) and goal_cell.startswith("simnav:"):
            content_hash, ordinal = _parse_selector(goal_cell, st.get("content_hash"))
            key = ordinal if ordinal in cells else goal_cell
        else:
            key = int(goal_cell) if isinstance(goal_cell, str) and goal_cell.isdigit() else goal_cell
        if key not in cells:
            raise ValueError(f"goal cell {goal_cell!r} not reachable in offline topology")
        if isinstance(cells, dict) and isinstance(cells[key], dict) and cells[key].get("reachable") is False:
            return {"verdict": "FAIL", "reason": f"route_and_wait: cell {goal_cell!r} unreachable"}
        entry["cell"] = key
        entry["WalkPhase"] = "Halted"
        return {"verdict": "PASS", "reason": f"route_and_wait {sim} -> cell {key}"}
    except Exception as exc:
        return {"verdict": "BLOCKED", "reason": f"route_and_wait: {exc}"}


def route_to_object(sim: str, target_object: str, *, state: dict | None = None) -> dict:
    try:
        st = _offline_state(state)
        entry = _require_sim_entry(st, sim)
        if not isinstance(target_object, str) or not target_object.strip():
            raise ValueError("missing target object")
        objects = st.get("objects")
        if not isinstance(objects, dict) or target_object not in objects or not isinstance(objects[target_object], dict):
            raise ValueError(f"object {target_object!r} not found in offline state")
        obj = objects[target_object]
        if obj.get("routable", True) is False:
            return {"verdict": "FAIL", "reason": f"route_to_object: object {target_object!r} not routable"}
        if "cell" in obj:
            entry["cell"] = obj["cell"]
        entry["target_object"] = target_object
        entry["WalkPhase"] = "Halted"
        return {"verdict": "PASS", "reason": f"route_to_object {sim} -> {target_object}"}
    except Exception as exc:
        return {"verdict": "BLOCKED", "reason": f"route_to_object: {exc}"}


def cancel_interaction(sim: str, interaction: str = "", *, state: dict | None = None) -> dict:
    try:
        st = _offline_state(state)
        entry = _require_sim_entry(st, sim)
        queue = entry.get("queue")
        if not isinstance(queue, list) or not queue:
            raise ValueError(f"no active interaction to cancel on {sim}")
        target = interaction.strip()
        if target:
            if target not in queue:
                raise ValueError(f"interaction {target!r} not in {sim}'s queue")
            queue.remove(target)
        else:
            target = str(queue.pop(0))
        return {"verdict": "PASS", "reason": f"cancel_interaction {sim}: cancelled {target}"}
    except Exception as exc:
        return {"verdict": "BLOCKED", "reason": f"cancel_interaction: {exc}"}


def teleport_sim(sim: str, destination: str | int, *, state: dict | None = None) -> dict:
    try:
        st = _offline_state(state)
        if st.get("card55_mode"):
            raise ValueError("teleport_sim is forbidden in Card 55 routed-walk acceptance")
        entry = _require_sim_entry(st, sim)
        spots = st.get("spots") or st.get("cells")
        if not isinstance(spots, (dict, set, list, tuple)) or destination not in spots:
            raise ValueError(f"destination {destination!r} not found in offline state")
        entry["cell"] = destination
        return {"verdict": "PASS", "reason": f"teleport_sim {sim} -> {destination}"}
    except Exception as exc:
        return {"verdict": "BLOCKED", "reason": f"teleport_sim: {exc}"}


def switch_active_household(household: str, *, state: dict | None = None) -> dict:
    try:
        st = _offline_state(state)
        if not isinstance(household, str) or not household.strip():
            raise ValueError("missing household name")
        households = st.get("households")
        if not isinstance(households, (dict, set, list, tuple)) or household not in households:
            raise ValueError(f"household {household!r} not found in offline state")
        st["active_household"] = household
        return {"verdict": "PASS", "reason": f"switch_active_household -> {household}"}
    except Exception as exc:
        return {"verdict": "BLOCKED", "reason": f"switch_active_household: {exc}"}


def reset_sim(sim: str, *, state: dict | None = None) -> dict:
    try:
        st = _offline_state(state)
        entry = _require_sim_entry(st, sim)
        entry["queue"] = []
        entry["pose"] = "standing"
        entry["WalkPhase"] = "Halted"
        if "home_cell" in entry:
            entry["cell"] = entry["home_cell"]
        return {"verdict": "PASS", "reason": f"reset_sim {sim}"}
    except Exception as exc:
        return {"verdict": "BLOCKED", "reason": f"reset_sim: {exc}"}


OFFLINE_STEP_ADAPTERS = {
    "set_sim_pose": lambda payload, state: set_sim_pose(*([p.strip() for p in payload.split(":", 1)] + [""])[:2], state=state),
    "route_and_wait": lambda payload, state: route_and_wait(*([p.strip() for p in payload.split(":", 1)] + [""])[:2], state=state),
    "route_to_object": lambda payload, state: route_to_object(*([p.strip() for p in payload.split(":", 1)] + [""])[:2], state=state),
    "cancel_interaction": lambda payload, state: cancel_interaction(*([p.strip() for p in payload.split(":", 1)] + [""])[:2], state=state),
    "teleport_sim": lambda payload, state: teleport_sim(*([p.strip() for p in payload.split(":", 1)] + [""])[:2], state=state),
    "switch_active_household": lambda payload, state: switch_active_household(payload.strip(), state=state),
    "reset_sim": lambda payload, state: reset_sim(payload.strip(), state=state),
}


def rust_step_result(kind: str, payload: str, scope=None, observation=None) -> dict:
    """Validate or execute a Card 55 step/check through Card55Scope or routing_evidence.step_observation."""
    import routing_evidence
    if isinstance(scope, Card55Scope) and observation is None:
        return scope.execute(kind, payload)
    return routing_evidence.step_observation(kind, payload, scope, observation)


def rust_only_gap(spec: dict) -> str | None:
    """BLOCKED reason when the scenario uses a Rust-only kind that has no executor or fails Card 55 preflight, else None."""
    kinds = sorted({item.partition(":")[0] for item in spec.get("steps", []) + spec.get("after", [])}
                   & (set(RUST_ONLY_KINDS) - RUST_ONLY_EXECUTORS))
    if kinds:
        details = [rust_step_result(kind, "")["reason"] for kind in kinds]
        return f"card 55 mechanism not built: no executor for {', '.join(kinds)}; " + "; ".join(details)
    return card55_preflight_gap(spec)


def late_blocked(record: dict, blocked_late: bool) -> bool:
    """A check's input was never recorded: BLOCKED, not a presentation result. A real failure from another check keeps
    the scenario a FAIL."""
    return blocked_late and all(f.startswith(("continuity: BLOCKED", "rust-evidence: BLOCKED"))
                               for f in record["failures"])


def run_scenario(spec: dict, out: Path) -> dict:
    if spec.get("soak"):
        return soak_scenario(spec)
    name = spec["name"]
    record = {"name": name, "summary": spec.get("summary", ""), "expect": spec.get("expect", "pass"), "failures": [], "log": [], "images": [],
              "timings": {}}
    gap = rust_only_gap(spec)
    if gap:  # a precondition, found before the reset touches the games
        record.update(verdict="BLOCKED", met=False, failures=[gap])
        return record
    clock = [time.time()]
    watched = spec_clients(spec, RUN_CLIENTS)
    multi = len(watched) > 1
    endpoints = {c: compare_client.resolve(c) for c in watched}

    def tag(client: str) -> str:
        # a two-client scenario's watch and compare failures say whose they are; a one-client one's read as before
        return f"client {client} " if multi else ""

    def lap(phase: str) -> None:
        now = time.time()
        record["timings"][phase] = round(now - clock[0], 1)
        clock[0] = now

    card55_mode = is_card55_scenario(spec)

    def scrub(text: str) -> str:
        return sanitize_card55_text(text) if card55_mode else text

    household = spec.get("household") or HOUSEHOLD
    paused = resume_paused(spec, list(endpoints.values()))
    if paused:
        record["paused"] = scrub(paused)
        record["log"].append("## speed\n" + scrub(paused))
    players = players_for(spec, watched)
    for player, sim in players.items():
        ok, text = ensure_player(player, sim)
        record["log"].append(f"## player {player}\n" + scrub(text))
        if not ok:
            # a precondition: the scenario is about that player on that Sim, and the rejoin never landed there
            reason = next((line for line in reversed(text.splitlines()) if line.startswith(f"player {player} now on")),
                          text.splitlines()[-1])
            record.update(verdict="RESET-FAILED", met=False,
                          failures=[f"reset: player {player} not on {sim}: {scrub(reason)[:160]}"])
            return record
    reset_log = reset(spec.get("start"), list(endpoints.values()), note=record, household=household,
                      player2=spec_players(spec))
    if card55_mode and record.get("start"):
        record["start"] = [scrub(s) for s in record["start"]]
    record["log"].append("## reset\n" + scrub(reset_log))
    problems, gate = reset_check(household, players, list(endpoints.values()))
    record["log"].append("## reset check\n" + scrub("\n".join(gate)))
    if uses_notices(spec):
        _, text = quick_step("notices:mark")  # notice:/nonotice: read only what this scenario showed
        record["log"].append("## notices:mark\n" + scrub(_tail(text, 2)))
    lap("reset")
    if problems:
        # the reset's own failure, and nothing about the code under test: the scenario never started
        record.update(verdict="RESET-FAILED", met=False, failures=[scrub(p) for p in problems])
        return record
    replay_before = replay_snapshot(False)  # after the reset: its repairs are not the scenario's
    if replay_before and ("error" in replay_before or any(
        "error" in counters for counters in replay_before.get("counters", {}).values()
    )):
        # A client can die during reset while the server still accepts actions. Confirm the missing replay read with
        # the normal health check, since a replay diagnostic can fail while the game's bridge is still responsive.
        if problem := health():
            record["log"].append("## stack health after reset\n" + problem)
            record["timings"]["total"] = record["timings"]["reset"]
            mark_crashed(record, problem, over="cut short")
            return record
    # rose: excludes reset and premise effects; a successful premise refreshes this baseline after its fault injection.
    rose_before = {c: read_counter(c, endpoints, watched)[0] for c in spec.get("after", []) if c.startswith("rose:")}

    def watch_args(client: str, during: bool) -> list[str]:
        args = ["scripts/motion_watch.py", *watch.get("sims", household), "--seconds", str(watch.get("seconds", 20))]
        if multi:
            args += ["--client", client]
        if during:
            args += ["--stop-file", str(stop_files[client])]
        if watch.get("capture"):
            args.append("--capture")
        if watch.get("events"):
            args.append("--events")  # every placement, walk and state event in the report, with who made it
        return args

    stop_files = {c: out / f"{name}{'' if c == watched[0] else '-c' + c}.watch-stop" for c in watched}
    for stop_file in stop_files.values():
        if stop_file.exists():
            stop_file.unlink()

    watch = None if card55_mode else spec.get("watch")
    card55_scope = spec.get("_card55_scope")
    offline_state = spec.get("_offline_state")
    watch_results: dict[str, dict] = {c: {} for c in watched}
    watchers = []
    watch_since = [0.0]  # when the watch started (wall clock): the window note_stream reads the logs over
    watch_after = bool(watch and watch.get("after"))  # a client restart in the steps would reset the client's sampler
    # "from": the watch starts just before the first step that begins with this text (after a rejoin, the get-up it
    # leads to: late-join-bed); until then the steps run unwatched.
    watch_from = (watch or {}).get("from") or ""
    def run_watch(result: dict, args: list[str]) -> None:
        result["code"], result["out"] = python(*args, timeout=900)

    def start_watch() -> None:
        watch_since[0] = time.time()
        for client in watched:
            watchers.append(threading.Thread(target=run_watch, args=(watch_results[client], watch_args(client, True)), daemon=True))
            watchers[-1].start()
        time.sleep(3)  # sampling has started on both games

    if watch and not watch_after and not watch_from:
        start_watch()

    blocked = None
    variables: dict[str, str] = {}
    for step in spec.get("steps", []):
        if WATCH is not None and WATCH.found:
            blocked = f"step {step} not run: watchdog: {WATCH.found}"  # watched() marks it CRASHED
            break
        if watch and watch_from and not watchers and step.startswith(watch_from):
            start_watch()
        step = rejoin_step(expand(step, variables))
        kind, _, rest = step.partition(":")
        if kind in RUST_ONLY_KINDS:
            outcome = rust_step_result(kind, rest, scope=card55_scope)
            record["log"].append(f"## step {step}\n{outcome['verdict']}: {outcome['reason']}")
            if outcome["verdict"] == "BLOCKED":
                blocked = f"step {step}: {outcome['reason']}"
                break
            if outcome["verdict"] == "FAIL":
                record["failures"].append(f"step {step}: {outcome['reason']}")
                break
        elif kind in OFFLINE_STEP_ADAPTERS:
            outcome = OFFLINE_STEP_ADAPTERS[kind](rest, offline_state)
            record["log"].append(f"## step {step}\n{outcome['verdict']}: {outcome['reason']}")
            if outcome["verdict"] == "BLOCKED":
                blocked = f"step {step}: {outcome['reason']}"
                break
            if outcome["verdict"] == "FAIL":
                record["failures"].append(f"step {step}: {outcome['reason']}")
                break
        elif kind == "act":
            code, text = python("scripts/sim_act.py", *_split(rest))
            record["log"].append(f"## step {step}\n{scrub(text.strip())}")
            if code != 0:
                # The game would not run the pick (its test, or it dropped out when its turn came): a precondition of
                # this scenario, not a presentation result. The rest is skipped.
                why = next((ln.strip() for ln in text.splitlines() if "candidates:" in ln or "NOT running" in ln or "err" in ln), f"exit {code}")
                blocked = f"step {step}: {scrub(why)[:160]}"
                break
        elif kind == "camera":
            # camera:<games>:<Sim or x,z>[:<distance>]: point those games' cameras (server, 1, 2, or all) there. A Sim
            # out of a game's view walks differently there, so this sets the view a scenario tests; the reset puts
            # every camera back on the household. A game that did not answer is a precondition, like an act.
            sides, _, where = rest.partition(":")
            target, _, distance = where.partition(":")
            lines = camera(camera_games(sides, endpoints), target, float(distance) if distance else 1.0)
            record["log"].append(f"## step {step}\n" + scrub("\n".join(lines)))
            failed = [line for line in lines if "focused" not in line]
            if failed:
                blocked = f"step {step}: {scrub(failed[0])[:160]}"
                break
        elif kind == "cpick":
            # A player's pick, made the way a click makes it. A probe that finds no target or no matching interaction
            # is a precondition, like an act: the game refused nothing yet. Whether the server ran it is a later step.
            client, _, args = rest.partition(":")
            code, text = python("scripts/client_pick.py", "--client", client, *_split(args))
            record["log"].append(f"## step {step}\n{scrub(text.strip())}")
            if code != 0:
                blocked = f"step {step}: {scrub(text.strip().splitlines()[-1])[:160] if text.strip() else f'exit {code}'}"
                break
        elif kind == "households":
            # households:<household ...> [--pair A+B] as a step: the games agree before what the scenario changes
            # (two-families-buy reads both families' funds before the charge), as the after check reads them after it
            diffs, text = households_check(rest, endpoints, watched)
            record["log"].append(f"## step {step}\n{scrub(text)}" + ("\n" + scrub("\n".join(diffs)) if diffs else ""))
            if diffs:
                record["failures"].append(f"step {step}: " + scrub("; ".join(diffs))[:200])
        else:
            restarts = rejoined(step) if WATCH is not None else []
            with WATCH.hold(*restarts) if restarts else contextlib.nullcontext():
                code, text = quick_step(step)
            variables.update(step_vars(text))
            record["log"].append(f"## step {step}\n{scrub(_tail(text))}")
            if code != 0:
                why = next((ln.strip() for ln in reversed(text.splitlines()) if ln.strip()), "")
                # a player step's own reason (hud:, notice:, until:) says what was not seen; others keep the exit code
                failure = f"step {step}: " + (scrub(why)[:200] if kind in ("hud", "notice", "nonotice", "menudiff", "objectdiff", "until", "premise") and why
                                              else f"exit {code}")
                if kind in ("until", "premise"):
                    # The server never got there, so the later steps test nothing: BLOCKED at this step (P, 2026-10-21:
                    # the failed until steps were logged, and the run went on to fail or block at a later one).
                    blocked = failure
                    break
                record["failures"].append(failure)
                if kind == "hud":
                    # the scenario's own assert failed: FAIL at this step; the watch and the after checks still run
                    record["log"].append(f"## steps stopped at {step}: the rest were not run")
                    break
            elif kind == "premise":
                rose_before = {check: read_counter(check, endpoints, watched)[0] for check in rose_before}
                record["log"].append("## counter baseline after premise\n"
                                     + "\n".join(f"{check}: {value}" for check, value in rose_before.items()))

    lap("steps")
    for stop_file in stop_files.values() if watchers else ():
        # The steps are done: the watch sees the last clip end (WATCH_TAIL) and stops, instead of its full seconds.
        stop_file.write_text(str(WATCH_TAIL), encoding="utf-8")
    if blocked:
        for watcher in watchers:
            watcher.join(timeout=900)
        if record["failures"]:
            verdict_of(record)
            record["log"].append(f"## blocked\n{scrub(blocked)}")
        else:
            record.update(verdict="BLOCKED", met=False, failures=[scrub(blocked)])
        if multi:
            record["clients"] = client_verdicts(record, watched)
        note_replay(record, replay_before)
        return record

    if watch and watch_after:
        watch_since[0] = time.time()
        for client in watched:
            watchers.append(threading.Thread(target=run_watch, args=(watch_results[client], watch_args(client, False)), daemon=True))
            watchers[-1].start()
    for watcher in watchers:
        watcher.join(timeout=900)
    lap("watch")
    if watch and watch_since[0]:
        note_stream(record, endpoints, watch_since[0], time.time())
    if watch:
        motions = {}
        for client in watched:
            text = watch_results[client].get("out", "")
            record["log"].append(f"## watch{' ' + compare_client.name(endpoints[client]) if multi else ''}\n{text.strip()}")
            record["images"] += images_in(text)
            motions[client] = motion_dir(text)
            failure = motion_failure(watch_results[client].get("code", 1), text)
            if failure:
                record["failures"].append(tag(client) + failure)
        record["motion"] = motions[watched[0]]
        record["states"] = motion_states(record["motion"])
        if multi:
            record["motions"] = motions
            record["states"] += [f"client {c}: {s}" for c in watched[1:] for s in motion_states(motions[c])]

    # Captures are evidence for failures, and a later check can still fail (fire-near: its capture was skipped as
    # passed, then the assert after it failed): every other check runs first, in order, then the captures if any failed.
    checks = spec.get("after", [])
    ordered = [c for c in checks if not c.startswith("capture:")] + [c for c in checks if c.startswith("capture:")]
    blocked_late = False
    for check in ordered:
        check = expand(check, variables)
        kind, _, rest = check.partition(":")
        if kind in RUST_ONLY_KINDS:
            outcome = rust_step_result(kind, rest, scope=card55_scope)
            record["log"].append(f"## after {check}\n{outcome['verdict']}: {outcome['reason']}")
            if outcome["verdict"] != "PASS":
                record["failures"].append(f"rust-evidence: {outcome['verdict']} {outcome['reason']}")
                blocked_late = blocked_late or outcome["verdict"] == "BLOCKED"
        elif kind == "sims":
            for client in watched:  # a two-client scenario compares the server with each client
                code, text = python("scripts/sim_compare.py", *_split(rest), *(["--client", client] if multi else []))
                clean_text = scrub(text.strip())
                record["log"].append(f"## after {check}{' (client ' + client + ')' if multi else ''}\n{clean_text}")
                if code == 3:
                    bad = [ln.strip() for ln in clean_text.splitlines() if ln.strip().startswith("!")]
                    record["failures"].append(tag(client) + "compare: " + "; ".join(re.sub(r"\s+", " ", b)[:90] for b in bad[:4]))
                elif code != 0:
                    record["failures"].append(tag(client) + f"compare failed (exit {code})")
        elif kind in ("notice", "nonotice", "hud", "menudiff", "objectdiff"):
            # a player's notices, HUD, pie menu or objects once the steps are done (quick_loop's notice:/nonotice:/hud:/
            # menudiff:/objectdiff:)
            code, text = quick_step(check)
            record["log"].append(f"## after {check}\n{scrub(_tail(text, 4))}")
            if code != 0:
                why = next((ln.strip() for ln in reversed(text.splitlines()) if ln.strip()), f"exit {code}")
                record["failures"].append(f"step {check}: {scrub(why)[:200]}")
        elif kind == "assert":
            # assert:<server|client>:<bridge command>:<regex>: one bridge command whose reply must match (a counter, a
            # state). The regex is everything after the third colon. client is the scenario's first watched client;
            # client1 or client2 names one.
            # assert:act:<sim_act arguments>:<regex> matches sim_act's report on the server instead (room=, at=: where a
            # Sim ended up, e.g. assert:act:Pablo:room=2; for a walk through the bathroom door).
            side, _, rest2 = rest.partition(":")
            command, _, pattern = rest2.partition(":")
            if side == "act":
                _, text = python("scripts/sim_act.py", *command.split())
            else:
                _, text = python("scripts/bridgectl.py", "--endpoint", check_endpoint(side, endpoints, watched), *command.split())
            found = re.search(pattern, text)
            record["log"].append(f"## after {check}\n" + scrub(found.group(0) if found else "NO MATCH\n" + _tail(text)))
            if not found:
                record["failures"].append(f"assert {side} {command}: no /{pattern}/")
        elif kind == "rose":
            # rose:<server|client>:<bridge command>:<counter>: the counter (key=N or key:N in the reply) must be higher
            # than after the reset or last successful premise: setup repairs cannot satisfy a stream assertion.
            # copy-flip-resend: rose:server:SACS_RECORDER status:reseeded.
            key = check.rsplit(":", 1)[1]
            after, text = read_counter(check, endpoints, watched)
            why = rose_failure(key, rose_before.get(check), after)
            record["log"].append(f"## after {check}\n{key}: {rose_before.get(check)} -> {after}"
                                 + (f"\n{scrub(_tail(text))}" if after is None else ""))
            if why:
                record["failures"].append(f"rose {rest.split(':', 1)[0]}: {why}")
        elif kind == "households":
            # households:<household ...> [--pair A+B]: fails where a watched client's funds or relationship differ from
            # the server's (scripts/household_read.py)
            diffs, text = households_check(rest, endpoints, watched)
            record["log"].append(f"## after {check}\n{scrub(text)}" + ("\n" + scrub("\n".join(diffs)) if diffs else ""))
            if diffs:
                record["failures"].append("households: " + scrub("; ".join(diffs))[:300])
        elif kind == "drivers":
            # drivers[:Sim,Sim]: fails when a Sim has two live drivers of one machine on any game read (the crumpled
            # pose of 2026-10-21, d70d068); scripts/replay_check.py drivers
            import replay_check
            findings, silent = replay_check.drivers_check(drivers_games(), replay_check.pick_sims(rest) if rest else None)
            record["log"].append(f"## after {check}\n" + ("\n".join(findings) or "no Sim has two live drivers of one machine")
                                 + (f"\nno reply from {', '.join(silent)}" if silent else ""))
            if findings:
                record["failures"].append("drivers: " + "; ".join(findings)[:300])
        elif kind == "continuity":
            # continuity: card 55 section D counters (route_continuity) over each watched client's motion run; BLOCKED
            # when the motion watch did not record an input a counter needs (walk style, leg index, door flag)
            import route_continuity
            sides = {c: route_continuity.load_motion_run(Path(motions[c]), route_continuity.first_sim_id(Path(motions[c])))
                     for c in watched if watch and motions.get(c)}
            if not sides:
                record["failures"].append("continuity: no motion watch to read")
            else:
                server = next(iter(sides.values())).get("server", [])
                result = route_continuity.score(server, {c: s["client"] for c, s in sides.items()},
                                                  all(route_continuity.has_fields(Path(motions[c])) for c in sides))
                record["log"].append(f"## after {check}\n{json.dumps(result)}")
                if result["verdict"] != "PASS":
                    record["failures"].append(f"continuity: {result['verdict']} {json.dumps(result)[:200]}")
                    blocked_late = blocked_late or result["verdict"] == "BLOCKED"
        elif kind == "capture" and not record["failures"]:
            record["log"].append(f"## after {check}\nskipped: the scenario passed (captures are evidence for failures)")
        else:
            code, text = quick_step(check)
            record["log"].append(f"## after {check}\n{scrub(_tail(text, 40))}")  # diag: its presenter line sits above the last 12
            record["images"] += images_in(text)

    lap("after")
    note_replay(record, replay_before)
    record["timings"]["total"] = round(sum(v for k, v in record["timings"].items() if k != "total"), 1)
    verdict_of(record)  # the overall verdict: every client's failures (history, rescore and arms read it)
    if late_blocked(record, blocked_late):
        record.update(verdict="BLOCKED", met=False)
    if multi:
        record["clients"] = client_verdicts(record, watched)
    # evidence next to the report
    folder = out / (f"{name}-sample{spec['sample']}" if spec.get("sample", 1) > 1 else name)  # a --repeat sample's own
    folder.mkdir(parents=True, exist_ok=True)
    kept = []
    for image in record["images"]:
        src = Path(image)
        if src.exists():
            dst = folder / f"{len(kept):02d}-{src.parent.name}-{src.name}"
            shutil.copyfile(src, dst)
            kept.append(dst.relative_to(out).as_posix())
    record["images"] = kept
    return record


def _split(text: str) -> list[str]:
    import shlex
    return shlex.split(text, posix=True)


def _tail(text: str, lines: int = 12) -> str:
    return "\n".join(text.strip().splitlines()[-lines:])


def load(names: list[str]) -> list[dict]:
    twice = sorted({n for n in names if names.count(n) > 1})
    if twice:  # 2026-10-21: "two-chairs two-chairs" ran once, the second silently dropped
        raise SystemExit(f"scenario named more than once: {', '.join(twice)}; for repeat samples use run --repeat N "
                         "(the suite N times in order, each sample its own record)")
    specs = []
    for path in sorted(SCENARIOS.glob("*.json")):
        spec = json.loads(path.read_text(encoding="utf-8"))
        if not names or spec["name"] in names:
            specs.append(spec)
    missing = set(names) - {s["name"] for s in specs}
    if missing:
        raise SystemExit("no such scenario: " + ", ".join(sorted(missing)))
    return specs


def repeat_specs(specs: list[dict], repeat: int) -> list[dict]:
    """run --repeat N: the suite N times in order (a b a b, so a drift over the run does not fall on one sample), each
    spec carrying its sample number (1..N); one pass leaves the specs as they are."""
    if repeat < 1:
        raise SystemExit("--repeat wants 1 or more")
    return specs if repeat == 1 else [dict(s, sample=k) for k in range(1, repeat + 1) for s in specs]


def record_key(rec: dict) -> str:
    """A record's name in a run: its scenario, with #k for a --repeat sample after the first (history's rows, explain
    and the arms row's verdicts)."""
    return rec["name"] if rec.get("sample", 1) <= 1 else f"{rec['name']}#{rec['sample']}"


def table(records: list[dict]) -> list[str]:
    rows = ["| scenario | verdict | expected | why |", "|---|---|---|---|"]
    for r in records:
        why = "; ".join(r["failures"]) or "-"
        rows.append(f"| {record_key(r)} | {r['verdict']}{NOFORK_TAG if r.get('nofork') else ''} | {r['expect']} | {why.replace('|', '/')} |")
    return rows


def client_line(clients: dict[str, dict]) -> str:
    """'client 1 PASS; client 2 FAIL (motion: ...)': a two-client scenario's verdict per client."""
    return "; ".join(f"client {c} {v['verdict']}" + (f" ({'; '.join(v['failures'])[:160]})" if v["failures"] else "")
                     for c, v in clients.items())


def verdict_of(record: dict) -> None:
    passed = not record["failures"]
    expect_pass = record["expect"] == "pass"
    record["verdict"] = ("PASS" if passed else "FAIL") if expect_pass else ("FIXED?" if passed else "known-failing")
    record["met"] = passed == expect_pass


def mark_crashed(record: dict, problem: str, over: str | None = None) -> None:
    """The scenario a game went down in, or right after (the next scenario's health check, or the one after the last
    scenario, found it down): CRASHED, with the stack-down line (it names the crash report, or none) added to its
    failures and its own verdict kept as crashed_over. Its time still counts as crash exposure; rescore leaves it
    alone and the arms summary leaves it out of the scenario's median time (cut short by the death). Marked a second
    time (the watchdog in it, then the next health() check): the new line is added and the rest kept. One the run
    watchdog cut short on a game still running (arms_lib.watchdog_hang) is a hang: arms counts it apart, not as a
    crash."""
    if record.get("verdict") == "CRASHED":
        if problem not in record["failures"]:
            record["failures"].append(problem)
        return
    record["crashed_over"] = over or record.get("verdict")
    record["failures"] = list(record.get("failures") or []) + [problem]
    record.update(verdict="CRASHED", met=False)


def report(out: Path, records: list[dict]) -> str:
    rows = table(records)
    (out / "records.json").write_text(json.dumps([{k: v for k, v in r.items() if k != "log"} for r in records], indent=1),
                                      encoding="utf-8")
    body = [f"# Scenario run {out.name}", ""]
    if any(r.get("nofork") for r in records):
        body += [f"**{NOFORK_BANNER}**", ""]
    left_out = excluded(out)
    if left_out:
        body += [f"**Left out of history and the survey:** {left_out} (`scenarios.py exclude {out.name} --undo` keeps it)", ""]
    tunes = sorted({t for r in records for t in r.get("tunes", [])})
    modules = list(dict.fromkeys(r["module"] for r in records if r.get("module")))
    if tunes:
        body += [f"Tunables: {', '.join(tunes)}", ""]
    if modules:
        body += [f"Client presentation module: {' then '.join(modules)}", ""]
    body += [*rows, ""]
    for r in records:
        body += [f"## {r['name']}: {r['verdict']}", "", r["summary"], ""]
        if r.get("clients"):
            body += ["Per client: " + client_line(r["clients"]), ""]
        if r.get("states"):
            body += ["States longer on one game (report only: a native pick, /): " + "; ".join(r["states"]), ""]
        if r.get("replay"):
            body += ["Replay counters over the scenario: " + ("; ".join(f"{label} {text}" for label, text in r["replay"]["change"].items())
                                                             or "not read: " + str(r["replay"].get("error"))), ""]
        if r.get("doubles"):
            body += ["Two live drivers of one machine after it (report only; an after entry `drivers` fails on it): "
                     + "; ".join(r["doubles"]), ""]
        if r.get("stream"):
            body += ["Stream silences in the watch (report only, scripts/stream_gaps.py): " + stream_line(r), ""]
        if r.get("paused"):
            body += ["Before the reset (report only): " + r["paused"], ""]
        if r.get("start"):
            body += ["Off its start after the reset (report only): " + "; ".join(r["start"]), ""]
        if r.get("load"):
            import machine_load
            body += ["Machine load over it (report only, scripts/machine_load.py): " + machine_load.text(r["load"]), ""]
        body += [f"![{img}]({img})" for img in r["images"]]
        body += ["", "<details><summary>output</summary>", "", "```", *[ln for block in r["log"] for ln in block.splitlines()], "```", "</details>", ""]
    (out / "report.md").write_text("\n".join(body), encoding="utf-8")
    return "\n".join(rows)


def split_failures(why: str) -> list[str]:
    """The failures a report row joined with "; " (some hold "; " themselves)."""
    if why.strip() in ("", "-"):
        return []
    out: list[str] = []
    for part in why.split("; "):
        if part.startswith(FAILURE_KINDS) or not out:
            out.append(part)
        else:
            out[-1] += "; " + part
    return out


NOFORK = "authority-gate.txt"
NOFORK_TAG = " [NO-FORK]"
NOFORK_BANNER = ("NO-FORK: the games ran without the gameplay fork (authority_gate hook=absent): client picks stayed local, so "
                 "a BLOCKED or FAIL here is not a gate result")
GATE_LOGS = ("server.log", "client.log", "client2.log", "client3.log")


def gate_snapshot(out: Path, since: float, log_dir: Path | None = None) -> None:
    """Copy the `hook=absent` lines of the games' logs into the run folder (authority-gate.txt, only when there are
    any). The logs are the stack's, not the run's: they live in .omo/run of the checkout that booted the games
    (SIMSONLINE_RUN_DIR when set) and the next boot overwrites them, so this runs once at the run's end, and a log not
    written since `since` (the run's start) is some earlier run's and is not read."""
    base = log_dir or Path(os.environ.get("SIMSONLINE_RUN_DIR") or ROOT / ".omo" / "run")
    lines = []
    for name in GATE_LOGS:
        log = base / name
        try:
            if log.stat().st_mtime < since:
                continue
            lines += [f"{name}: {ln.strip()[:300]}" for ln in log.read_text(encoding="utf-8", errors="replace").splitlines()
                      if "hook=absent" in ln]
        except OSError:
            continue
    if lines:
        (out / NOFORK).write_text("\n".join(lines) + "\n", encoding="utf-8")


@contextlib.contextmanager
def snapshot_on_exit(out: Path, since: float):
    """gate_snapshot when the block ends, however it ends. The block's own error or interrupt propagates unchanged; a
    failure of the snapshot itself is one stderr line and never replaces it. Writing twice writes the same file."""
    try:
        yield
    finally:
        try:
            gate_snapshot(out, since)
        except Exception as e:  # noqa: BLE001 - must not mask the original error
            print(f"   gate snapshot failed: {e}", file=sys.stderr, flush=True)


def run_nofork(run: Path) -> bool:
    """Whether a run's own folder holds `hook=absent` (its authority-gate.txt, or a game log copied into it)."""
    for path in [run / NOFORK, *run.glob("*.log")]:
        try:
            if path.is_file() and "hook=absent" in path.read_text(encoding="utf-8", errors="replace"):
                return True
        except OSError:
            continue
    return False


def runs() -> list[Path]:
    return sorted(d for d in RUNS.iterdir() if d.is_dir() and (d / "report.md").exists()) if RUNS.exists() else []


EXCLUDED = "excluded.txt"


def excluded(run: Path) -> str | None:
    """Why a run is left out of history and the sacs_timing survey (its excluded.txt), or None."""
    try:
        return (run / EXCLUDED).read_text(encoding="utf-8").strip() or "excluded"
    except OSError:
        return None


def exclude(run_name: str, reason: str, undo: bool) -> int:
    """Mark a run whose timings are not the games' own (a debugger attached, a broken boot): history and
    `sacs_timing.py` over many runs leave it out; explain and rescore still read it when named."""
    run = resolve_run(run_name)
    if undo:
        (run / EXCLUDED).unlink(missing_ok=True)
        print(f"{run.name}: back in history and the survey")
        return 0
    if not reason:
        raise SystemExit("say why (for example: cdb attached to the server)")
    (run / EXCLUDED).write_text(reason + "\n", encoding="utf-8")
    print(f"{run.name}: left out of history and the survey ({reason})")
    return 0


AUTO = "auto: "

# Set to "<date> <arms folder>: <verdict>" once an A/B (scenarios.py arms --catcher-ab) shows the passive crash catcher
# (crash_catch.py attach: no breakpoints) leaves verdicts and timings alone. Until then a run with it attached is left out
# like any debugged run (user, 2026-10-18). Setting it is the user's call; the A/B prints the line to put here.
CATCHER_MEASURED: str | None = None
DEBUGGER_WORDS = {
    "passive": "passive crash catcher on the {side} (left out until its overhead is measured: scenarios.py arms --catcher-ab)",
    "trace": "cdb trace (logging breakpoints) on the {side}",
}
DEBUGGER_RANK = {"passive": 0, "trace": 1, "other": 2}


def debugged_now() -> dict[str, str]:
    """The games with a debugger attached right now and its kind (crash_catch.debugger_kinds: passive, trace, or other:
    disassembler, x64dbg, a scratch tracer); {} when that cannot be told, so a probe problem never stops a run."""
    try:
        try:
            import crash_catch
        except ImportError:
            crash_catch = _MirrorProcFallback

        return crash_catch.debugger_kinds()
    except Exception:
        return {}


def merge_debuggers(seen: dict[str, str], now: dict[str, str]) -> dict[str, str]:
    """The debuggers seen at a scenario's start and end: per side the more intrusive kind."""
    merged = dict(seen)
    for side, kind in now.items():
        if side not in merged or DEBUGGER_RANK.get(kind, 2) > DEBUGGER_RANK.get(merged[side], 2):
            merged[side] = kind
    return merged


def debugger_entries(record: dict) -> list[tuple[str, str]]:
    """(side, kind) for each debugger a record saw; records from before kinds were kept list sides only."""
    seen = record.get("debugger") or {}
    if isinstance(seen, dict):
        return sorted(seen.items())
    return [(side, "attached") for side in seen]


def crash_side(path: Path, server_profile: Path) -> str:
    """server when a crash report is in the server's profile or its archive folder, else client (a Sandboxie box)."""
    try:
        import crash_catch
    except ImportError:
        crash_catch = _MirrorProcFallback

    return crash_catch.report_side(Path(path), Path(server_profile))


def crashes_since(since: float, sides: bool = False) -> list[str]:
    """TS3 crash reports written since an epoch time, each with its triage line (crash_catch.py triage); sides: each
    led by the game that wrote it."""
    try:
        try:
            import crash_catch
        except ImportError:
            crash_catch = _MirrorProcFallback
        import quick_loop

        return [(f"{crash_side(p, quick_loop.REAL)} " if sides else "") + f"{p.name}: {crash_catch.describe_report(p, short=True)}"
                for p in crash_catch.crash_reports(since)]
    except Exception:
        return []


def auto_exclude(out: Path, records: list[dict], problem: str | None, crashes: list[str]) -> str | None:
    """Leave a run out of history and the sacs_timing survey when its timings are not the games' own: a debugger was
    attached to a game during a scenario (each record's "debugger"), the stack went down part way (problem), or a game
    wrote a crash report during the run. Returns the reason when it writes one. A reason given by hand (scenarios.py
    exclude) is kept; `scenarios.py exclude <run> --undo` takes the run back."""
    parts = []
    by_side: dict[tuple[str, str], list[str]] = {}
    for record in records:
        for side, kind in debugger_entries(record):
            if kind == "passive" and CATCHER_MEASURED:
                continue  # measured: the passive catcher leaves the games' timings alone
            by_side.setdefault((side, kind), []).append(record["name"])
    parts += [DEBUGGER_WORDS.get(kind, "debugger attached to the {side}").format(side=side) + f" during {', '.join(names)}"
              for (side, kind), names in sorted(by_side.items())]
    if problem:
        parts.append(f"cut short ({problem})")
    elif crashes:
        parts.append("a game crashed (" + "; ".join(crashes) + ")")
    if not parts:
        return None
    reason = AUTO + "; ".join(parts)
    current = excluded(out)
    if current == reason or (current and not current.startswith(AUTO)):
        return None
    (out / EXCLUDED).write_text(reason + "\n", encoding="utf-8")
    return reason


ARMS = ROOT / ".omo" / "run" / "arms"
DUMP_DIR = ROOT / ".omo" / "run" / "dumps"  # crash_catch.py's dumps
MIN_FREE_GB = 6.0  # before a catcher arm: a full dump is about 1.3 GB, the death adds a minidump; two and a margin
MAX_DUMPS = 2  # catches per arms run: two full dumps already name the managed method, each about 1.3 GB
DUMP_SESSION = re.compile(r"^(.+-\d{8}-\d{6})-")  # server-63212-20261018-083016-interp-av.dmp: one attach's dumps
REPORT_LINE = re.compile(r"report: .*[\\/](\d{8}-\d{6})[\\/]report\.md")
CATCHER_RATE = re.compile(r"first-chance exceptions (\d+) \(([\d.]+)/min\)")


def parse_arm(text: str) -> tuple[str, list[str]]:
    """NAME=ARGS: an arm's name and the extra `scenarios.py run` arguments it adds (shell-split), for example
    `t3on=--tune both:sacs.keyed-picks=1` or `catcher=--catcher`."""
    name, _, args = text.partition("=")
    if not re.fullmatch(r"[\w.-]+", name):
        raise SystemExit(f"arm {text!r}: want NAME=ARGS with a NAME of letters, digits, . - _")
    return name, shlex.split(args)


def arm_order(arms: list[tuple[str, list[str]]], repeat: int) -> list[tuple[int, str, list[str]]]:
    """Every arm once per repeat, every other repeat in reverse (A B B A ...), so a drift over the session (a warming
    disk cache, a game that slows down, the crash-prone hour) does not fall on one arm (arm order was a confound in
    's boots 3 and 4)."""
    order = []
    for r in range(repeat):
        for name, args in (arms if r % 2 == 0 else arms[::-1]):
            order.append((r + 1, name, args))
    return order


REBOOT = "reboot"  # an --order item: a planned reboot of both games between two arms


def planned_order(arms: list[tuple[str, list[str]]], repeat: int, order: str = "") -> list[tuple[int, str, list[str]]]:
    """The arms in run order: --order's names as given (space or comma separated; "reboot" for a planned reboot), each
    arm's repeat number its count so far; without --order, arm_order's A B B A over --repeat."""
    if not order.strip():
        return arm_order(arms, repeat)
    defined = dict(arms)
    out, seen = [], {}
    for name in re.split(r"[\s,]+", order.strip()):
        if name == REBOOT:
            out.append((0, REBOOT, []))
            continue
        if name not in defined:
            raise SystemExit(f"--order {name}: no such arm (have: {', '.join(defined)}, {REBOOT})")
        seen[name] = seen.get(name, 0) + 1
        out.append((seen[name], name, defined[name]))
    return out


def game_identity() -> dict[str, dict]:
    """Each game's TS3 process now: its pid (the launcher's pid file) and, when it can be read, its start time, so a
    reused pid is not taken for the same process. {} when that cannot be told."""
    try:
        try:
            import crash_catch
        except ImportError:
            crash_catch = _MirrorProcFallback
        try:
            import stack_who
        except ImportError:
            stack_who = None

        out = {}
        for side, pid in crash_catch.game_pids().items():
            _, started = stack_who.process_info(pid)
            out[side] = {"pid": pid, "started": round(started, 1) if started else None}
        return out
    except Exception:
        return {}


def same_process(a: dict | None, b: dict | None) -> bool:
    if not a and not b:
        return True  # neither read (no pid files): only the runner's own reboots reset the history
    if not a or not b or a.get("pid") != b.get("pid"):
        return False
    return a.get("started") is None or b.get("started") is None or abs(a["started"] - b["started"]) < 2


def process_before(history: dict[str, dict], now: dict[str, dict]) -> dict[str, dict]:
    """Each game's process history before an arm: the arms it already ran and the tunables it had on in them (had(),
    e.g. whether it has read seeds). A process the history does not know (a reboot, a restart) starts empty."""
    for side in ("server", "client"):
        if not same_process(history.get(side, {}).get("identity"), now.get(side)):
            history[side] = {"identity": now.get(side), "arms": [], "had": []}
    return {side: {"pid": (h["identity"] or {}).get("pid"), "arms_before": list(h["arms"]), "had": sorted(h["had"])}
            for side, h in history.items()}


def note_process(history: dict[str, dict], label: str, on_names: dict[str, set[str]], now: dict[str, dict]) -> list[str]:
    """After an arm: each game still in the same process adds the arm and the tunables it had on; the sides whose
    process changed during the arm (a client restart by a late-join scenario, a crash and reboot) are returned."""
    restarted = []
    for side, h in history.items():
        if same_process(h.get("identity"), now.get(side)):
            h["arms"].append(label)
            h["had"] = sorted(set(h["had"]) | on_names.get(side, set()))
        else:
            restarted.append(side)
            history[side] = {"identity": now.get(side), "arms": [], "had": []}
    return restarted


def arm_on_names(sets: list[str], run_args: list[str]) -> dict[str, set[str]]:
    """The tunables an arm has on (non-default) per game: its --set items and its run's --tune items."""
    import arms_lib

    on: dict[str, set[str]] = {"server": set(), "client": set()}
    for item in list(sets) + arms_lib.args_tunes(run_args):
        for side, name, value in arms_lib.expand_tune(item):
            if arms_lib.is_on(value):
                on[side].add(name)
    return on


def hook_argv(command: str, arm: str, repeat: int, down: str = "none",
              boot_env: dict[str, dict[str, str]] | None = None) -> list[str]:
    """A --boot-hook/--before/--after command for one arm, {arm}, {repeat}, {down} (the games down after the run,
    "server", "client" or "server,client"; "none" when both are up, never empty), {server_env} and {client_env} (the
    arm's boot env as quick_loop.py run --boot's words, --server-env K=V ... and --client-env K=V ...; nothing when it
    has none) filled in; a .py first word runs with this Python."""
    for side in ("server", "client"):
        command = command.replace("{" + side + "_env}", shlex.join(boot_env_args({side: (boot_env or {}).get(side) or {}})))
    argv = shlex.split(command.replace("{arm}", arm).replace("{repeat}", str(repeat)).replace("{down}", down))
    return [sys.executable, *argv] if argv and argv[0].endswith(".py") else argv


def is_catcher(args: list[str]) -> bool:
    return any(arg.startswith("--catcher") for arg in args)


def without_catcher(args: list[str]) -> list[str]:
    """An arm's run arguments with --catcher and --catcher-side [SIDE] taken out."""
    out, skip = [], False
    for arg in args:
        if skip:
            skip = False
        elif arg == "--catcher-side":
            skip = True
        elif not arg.startswith("--catcher"):
            out.append(arg)
    return out


def free_gb(path: Path = ROOT) -> float:
    return shutil.disk_usage(path).free / 2 ** 30


def dumps_since(since: float, folder: Path | None = None) -> list[str]:
    """crash_catch dumps (.dmp) written since an epoch time."""
    folder = folder or DUMP_DIR
    return sorted(p.name for p in folder.glob("*.dmp") if p.stat().st_mtime >= since) if folder.is_dir() else []


def wait_dumps() -> dict:
    """G21: after an arm's run, before its detach and the next arm's down, wait (up to 5 min) while the catcher
    writes a dump (crash_catch.wait_for_dumps)."""
    try:
        import crash_catch
    except ImportError:
        crash_catch = _MirrorProcFallback

    return crash_catch.wait_for_dumps(DUMP_DIR, say=lambda line: print("   " + line, flush=True))


def dump_sizes(names: list[str], folder: Path | None = None) -> dict[str, int | None]:
    """Each named dump's size in bytes as it is now (None when it is gone)."""
    folder = folder or DUMP_DIR
    sizes: dict[str, int | None] = {}
    for name in names:
        try:
            sizes[name] = (folder / name).stat().st_size
        except OSError:
            sizes[name] = None
    return sizes


def catches(dumps: list[str]) -> int:
    """How many crashes the dumps caught: one attach writes a full dump and the death's minidump under one prefix."""
    return len({(m.group(1) if (m := DUMP_SESSION.match(name)) else name) for name in dumps})


def catcher_refusal(free: float, caught: int, min_free: float, max_dumps: int) -> str | None:
    """Why a catcher arm must not attach now, or None."""
    if free < min_free:
        return f"{free:.1f} GB free on the dumps' drive, below {min_free:g} GB (a full dump is about 1.3 GB)"
    if caught >= max_dumps:
        return f"{caught} crash(es) dumped in this arms run, the cap is {max_dumps}"
    return None


def hook_result(command: str, arm: str, repeat: int, keep: Path | None = None, down: str = "none",
                kind: str = "", env_extra: dict[str, str] | None = None,
                boot_env: dict[str, dict[str, str]] | None = None) -> tuple[int | None, str]:
    """Run a hook: its exit code (None when it could not run or ran out of time) and the line arms prints and keeps
    (the exit code and the hook's last line). keep: write its full output there (a gate reads the after hook's). kind
    ("boot", "before", "after") reaches the hook as SIMSONLINE_ARMS_HOOK, so a hook can refuse the wrong slot;
    env_extra too (the after hook's run window and folder, ARM_ENV). boot_env (the arm's) fills {server_env} and
    {client_env} (hook_argv)."""
    env = {**os.environ, **({"SIMSONLINE_ARMS_HOOK": kind} if kind else {}), **(env_extra or {})} if kind or env_extra else None
    try:
        proc = subprocess.run(hook_argv(command, arm, repeat, down, boot_env), cwd=ROOT, capture_output=True, text=True,
                              timeout=1800, env=env)
        text = (proc.stdout + proc.stderr).strip()
        if keep is not None:
            try:
                keep.write_text(text + "\n", encoding="utf-8")
            except OSError:
                pass
        return proc.returncode, f"exit {proc.returncode}" + (f": {text.splitlines()[-1][:160]}" if text else "")
    except (OSError, subprocess.TimeoutExpired) as ex:
        return None, f"failed: {ex}"


def after_values(hook_file: Path) -> dict:
    """The NAME=N values in an after hook's output (arms_lib.hook_values, what a gate's hook.NAME reads), kept in the
    arm's row: gc_watch.py frames' violations and rates, a pull's counters."""
    import arms_lib

    try:
        return arms_lib.hook_values(hook_file.read_text(encoding="utf-8"))
    except OSError:
        return {}


def run_hook(command: str, arm: str, repeat: int, keep: Path | None = None, down: str = "none", kind: str = "",
             env_extra: dict[str, str] | None = None, boot_env: dict[str, dict[str, str]] | None = None) -> str:
    """Run a hook; its exit code and last line (hook_result's line)."""
    return hook_result(command, arm, repeat, keep, down, kind, env_extra, boot_env)[1]


def arm_env(started: float, ended: float, out: Path) -> dict[str, str]:
    """What an after hook is told of the arm it follows (gc_watch.py frames --arm reads it): the run's start and end
    (epoch seconds) and the arms run's folder."""
    return {"SIMSONLINE_ARM_START": f"{started:.3f}", "SIMSONLINE_ARM_END": f"{ended:.3f}",
            "SIMSONLINE_ARMS_FOLDER": str(out)}


def split_sets(args: list[str]) -> tuple[list[str], list[str]]:
    """An arm's --set [SIDE:]NAME=VALUE items (the runner holds them for the whole arm) and the rest of its arguments
    (scenarios.py run's)."""
    sets, rest = [], []
    items = iter(args)
    for arg in items:
        if arg == "--set":
            value = next(items, "")
            if not value:
                raise SystemExit("--set wants [server:|client:|both:]NAME=VALUE")
            sets.append(value)
        elif arg.startswith("--set="):
            sets.append(arg[len("--set="):])
        else:
            rest.append(arg)
    return sets, rest


ENV_FLAGS = {"--server-env": "server", "--client-env": "client"}  # an arm's boot env items, quick_loop.py run's flags


def env_items(items: list[str], flag: str) -> dict[str, str]:
    """K=V items (--server-env, --client-env) as a dict, a later item over an earlier one; a bad item stops here,
    before any game (quick_loop.py run would only fail on it at the boot)."""
    out: dict[str, str] = {}
    for item in items:
        key, eq, value = item.partition("=")
        if not eq or not re.fullmatch(r"[A-Za-z_]\w*", key):
            raise SystemExit(f"{flag} {item!r}: want NAME=VALUE, a NAME of letters, digits and _")
        out[key] = value
    return out


def wide_env(server_env: list[str] | None, client_env: list[str] | None) -> dict[str, dict[str, str]]:
    """arms --server-env and --client-env: the run's own boot env, every arm's unless the arm names its own."""
    return {"server": env_items(server_env or [], "--server-env"),
            "client": env_items(client_env or [], "--client-env")}


def split_boot_env(args: list[str]) -> tuple[dict[str, dict[str, str]], list[str]]:
    """An arm's own boot env, its --server-env K=V and --client-env K=V items ({"server": {K: V}, "client": {...}}),
    and the rest of its arguments: the runner's boots pass the env on, so `scenarios.py run` never sees it."""
    found: dict[str, list[str]] = {"server": [], "client": []}
    rest = []
    items = iter(args)
    for arg in items:
        flag, eq, value = arg.partition("=")
        if flag in ENV_FLAGS:
            found[ENV_FLAGS[flag]].append(value if eq else next(items, ""))
        else:
            rest.append(arg)
    return {side: env_items(values, f"--{side}-env") for side, values in found.items()}, rest


def arm_boot_envs(arm_list: list[tuple[str, list[str]]], wide: dict[str, dict[str, str]]) -> dict[str, dict]:
    """Each arm's boot env: the run's own (wide_env) with the arm's own items over it."""
    out = {}
    for name, args in arm_list:
        own, rest = split_boot_env(split_sets(args)[1])
        if (own["server"] or own["client"]) and "--boot" in rest:
            raise SystemExit(f"arm {name}: its --server-env/--client-env is its boot env, which the runner's boots pass "
                             "on; drop --boot from its ARGS (a boot inside the run would undo the runner's --set)")
        out[name] = {side: {**wide[side], **own[side]} for side in ("server", "client")}
    return out


def boot_clients_args(clients: int | None) -> list[str]:
    """quick_loop.py run --boot's --clients for the arms runner's boots (arms --clients): none leaves quick_loop's
    default (2 under --client 2, else 1)."""
    return ["--clients", str(clients)] if clients else []


def client2_warning(names: list[str], clients: int | None) -> str | None:
    """A line when a scenario of the run watches client N but the runner's boots would start fewer clients: after a
    reboot that scenario would find that player's game down (two-hud, two-same-chair; 2026-10-21, the session probe)."""
    booted = clients or (2 if CLIENT == compare_client.client2() else 1)  # under --client 2 quick_loop boots both already
    try:
        short = [(s["name"], max(int(c) for c in s["clients"])) for s in load(names)
                 if not s.get("soak") and any(str(c).isdigit() and int(c) > booted for c in s.get("clients") or [])]
    except SystemExit:
        return None
    if not short:
        return None
    need = max(n for _, n in short)
    return (f"   warning: {', '.join(n for n, _ in short)} watch client {need}, but the runner's boots start "
            f"{booted if clients else 'one'} client{'s' if booted > 1 else ''}{'' if clients else ' (quick_loop default)'}: "
            f"give --clients {need}")


def boot_env_args(env: dict[str, dict[str, str]]) -> list[str]:
    """quick_loop.py run --boot's words for a boot env: --server-env K=V and --client-env K=V for each item."""
    return [word for side in ("server", "client") for key, value in (env.get(side) or {}).items()
            for word in (f"--{side}-env", f"{key}={value}")]


def env_text(env: dict | None) -> str:
    """A boot env in a phrase, "server SIMLINK_GCGATE=1, client none"; a side the run does not know, "unknown"."""
    return ", ".join(f"{side} " + ("unknown" if (env or {}).get(side) is None
                                   else " ".join(f"{k}={v}" for k, v in env[side].items()) or "none")
                     for side in ("server", "client"))


def env_off(want: dict[str, dict[str, str]], now: dict | None) -> list[str]:
    """The sides whose boot env in effect (now; a side's None when the run does not know it: the stack as found, a
    restarted game) is not the arm's own (want); an unknown one counts only against an arm that wants an env."""
    return [side for side in ("server", "client")
            if (want[side] if (now or {}).get(side) is None else now[side] != want[side])]


def boot_hook_refusal(boot: str, envs: dict[str, dict]) -> str | None:
    """Why a --boot-hook cannot boot these arms: a side some arm has a boot env for and the hook has no placeholder
    for ({server_env}, {client_env}), so that env would never reach a game."""
    missing = [side for side in ("server", "client")
               if any(env[side] for env in envs.values()) and "{" + side + "_env}" not in boot]
    if not missing:
        return None
    return (f"--boot-hook names no {' or '.join('{' + side + '_env}' for side in missing)}, so an arm's "
            f"{' and '.join(missing)} boot env would never reach its boot; e.g. --boot-hook "
            "'python scripts/quick_loop.py run --boot {server_env} {client_env}'")


def runner_boot(by: str, env: dict[str, dict[str, str]]) -> dict:
    """state.json's booted: a boot the runner made (by), its env, and the games' processes after it (identity), which
    the next arm's snapshot fills in, the first one after the boot."""
    return {"by": by, "server": dict(env["server"]), "client": dict(env["client"]), "identity": None}


def boot_env_walk(order: list[tuple[int, str, list[str]]], envs: dict[str, dict], wide: dict[str, dict[str, str]],
                  boot: str) -> list[str]:
    """arms --check's walk of the order for the boot env each arm runs on, if no game restarts and no reboot-when-dead
    comes in: the start is the stack as found (unknown), a planned reboot boots the next arm's env (the run's own when
    no arm follows), a boot hook each arm's own. A line for the arms whose games would not run on their own, one per
    wanted env, env in effect and where that came from (an order with no reboot is one line, not one per arm)."""
    now: dict | None = None
    origin = "the stack as found"
    groups: dict[tuple[str, str, str], list[str]] = {}
    for index, (repeat, name, _) in enumerate(order):
        if name == REBOOT:
            now = next((envs[n] for _, n, _ in order[index + 1:] if n != REBOOT), wide)
            origin = f"the planned reboot at item {index + 1}"
            continue
        if boot:
            now, origin = envs[name], "its boot hook"
        if env_off(envs[name], now):
            groups.setdefault((env_text(envs[name]), env_text(now), origin), []).append(f"{name} #{repeat}")
    out = []
    for (want, have, origin), labels in groups.items():
        shown = ", ".join(labels[:6]) + (f" and {len(labels) - 6} more" if len(labels) > 6 else "")
        out.append(f"{shown} boots with {want} but runs on {have} ({origin}); a reboot before it in --order boots its own"
                   if len(labels) == 1 else f"{shown} boot with {want} but run on {have} ({origin}); a reboot before "
                   "the first in --order boots theirs")
    return out


def expand_sets(sets: list[str]) -> list[tuple[str, str, str]]:
    """(endpoint, name, value) for each game an arm's --set items name (client by default, as --tune)."""
    import arms_lib
    import tune as tune_tool

    out = []
    for item in sets:
        for side, name, value in arms_lib.expand_tune(item):
            if not name or not value:
                raise SystemExit(f"--set {item}: not [server:|client:|both:]name=value")
            if tune_tool.float32_problem(value):
                raise SystemExit(f"--set {item}: {tune_tool.float32_problem(value)}")
            out.append((SERVER if side == "server" else CLIENT, name, value))
    return out


def managed_tunables(arm_args: list[list[str]]) -> list[tuple[str, str]]:
    """Every (endpoint, name) that any arm sets. Each arm sets all of them, to its own value or the default, so one
    arm's tunable never carries into the next (Session C's runners set all four each arm)."""
    seen: dict[tuple[str, str], None] = {}
    for args in arm_args:
        for endpoint, name, _ in expand_sets(split_sets(args)[0]):
            seen.setdefault((endpoint, name), None)
    return list(seen)


def arm_targets(sets: list[str], managed: list[tuple[str, str]]) -> list[tuple[str, str, str]]:
    own = {(endpoint, name): value for endpoint, name, value in expand_sets(sets)}
    return [(endpoint, name, own.get((endpoint, name), "default")) for endpoint, name in managed]


def side_of(endpoint: str) -> str:
    """The tune.py --side for one game, and its label: the compared client is "client"; the other player's game is
    client1 or client2."""
    if endpoint == SERVER:
        return "server"
    if endpoint != CLIENT and compare_client.player_of(endpoint):
        return "client" + compare_client.player_of(endpoint)
    return "client"


def tune_ok(reply: str, name: str, value: str) -> bool:
    """A tune.py reply for a set: no error, and (unless back to the default, which may drop an unread name from the
    mod's list) the name in it."""
    return "error=" not in reply and (value == "default" or f"{name}=" in reply)


def tune_unread(reply: str, name: str, value: str) -> bool:
    """Whether a set's reply says the mod has never read that tunable: its row reads "(default ?)" (set, no default
    known), or after a reset to the default it is not listed at all. A mod built before the tunable (an old build slot,
    G17) takes any name and never reads it; a tunable read only later in a run looks the same until then."""
    rows = [row.split("] ", 1)[-1] for row in reply.splitlines()]
    row = next((r for r in rows if r.startswith(f"{name}=")), None)
    return row is None if value == "default" else row is not None and "(default ?)" in row


def set_tunables(targets: list[tuple[str, str, str]]) -> tuple[list[str], list[str]]:
    """Set each (endpoint, name, value). Returns the problems, one per tunable that did not take (an error or no
    reply: the arm cannot run as named), and the sets the mod has never read (tune_unread: recorded, not a problem)."""
    problems, unread = [], []
    for endpoint, name, value in targets:
        try:
            reply = tune(endpoint, name, value)
        except subprocess.TimeoutExpired:
            reply = "error=no reply in 60 s"
        if not tune_ok(reply, name, value):
            problems.append(f"{side_of(endpoint)} {name}={value}: {reply.splitlines()[-1][:120] if reply else 'no reply'}")
        elif tune_unread(reply, name, value):
            unread.append(f"{side_of(endpoint)} {name}={value}")
    return problems, unread


def release_tunables(managed: list[tuple[str, str]]) -> list[str]:
    """Every managed tunable back to its default, each game on its own: a game that does not answer is reported and
    left (a restart is back to the defaults anyway) and the other is still reset. Boot 5's last tune-off stopped at the
    dead server and left the client's sacs.keyed-picks and sacs.node-order on (2026-10-18)."""
    lines = []
    for endpoint in (SERVER, CLIENT):
        names = [name for e, name in managed if e == endpoint]
        if not names:
            continue
        done, failed, why = [], [], ""
        for name in names:
            try:
                reply = tune(endpoint, name, "default")
            except subprocess.TimeoutExpired:
                reply = "error=not answering (no reply in 60 s)"
            if tune_ok(reply, name, "default"):
                done.append(name)
                continue
            why = reply.splitlines()[-1][:120] if reply else "no reply"
            if "not answering" in reply:  # a dead game: the rest would only wait the same
                failed += [n for n in names if n not in done]
                break
            failed.append(name)
        lines.append(f"{side_of(endpoint)}: {len(done)} of {len(names)} back to default"
                     + (f"; not reset: {', '.join(failed)} ({why})" if failed else ""))
    return lines


def catcher_logs(run_id: str, dumps: Path) -> dict[str, dict]:
    """The passive catcher's cdb logs for one scenario run (crash_catch names them cdb-SIDE-PID-RUN.log), per side:
    first-chance exceptions other than break-ins, second-chance ones, break-ins (the attach and the detach make 2),
    access violations, and the minutes the log spans (created at the attach, last written at the detach). A game
    restarted in the run has a log each: they add up."""
    try:
        import crash_catch
    except ImportError:
        crash_catch = _MirrorProcFallback

    out: dict[str, dict] = {}
    for log in sorted(dumps.glob(f"cdb-*-*-{run_id}.log")):
        try:
            text, st = log.read_text(encoding="utf-8", errors="replace"), log.stat()
        except OSError:
            continue
        counts = crash_catch.log_counts(text)
        minutes = max(0.0, st.st_mtime - min(getattr(st, "st_birthtime", st.st_mtime), st.st_mtime)) / 60
        entry = {"logs": [log.name], "first_chance": counts["first_chance"], "second_chance": counts["second_chance"],
                 "breaks": counts["breaks"], "avs": len(counts["avs"]), "minutes": minutes}
        side = log.name.split("-")[1]
        if side in out:
            entry = {k: out[side][k] + v for k, v in entry.items()}
        out[side] = entry
    for entry in out.values():
        entry["minutes"] = round(entry["minutes"], 2)
    return out


def arm_record(name: str, repeat: int, args: list[str], started: float, ended: float, text: str,
               crashes: list[str], runs_dir: Path = RUNS) -> dict:
    """What one arm's run gave: its run folder, verdicts and timings per scenario, scenario minutes (the crash
    exposure: BLOCKED and RESET-FAILED scenarios did not run), crash reports written meanwhile, the passive catcher's
    exception rate: from the run's catcher line when it printed one, else from the run's cdb logs beside runs_dir
    (catcher_logs, the counts kept as `catcher`), and the scenarios the run watchdog cut short on a hang (hangs:
    {scenario: its line}, arms_lib.watchdog_hang; none in a run with a crash report, whose cut-short scenario is the
    crash)."""
    import arms_lib

    found = REPORT_LINE.findall(text)
    run_id = found[-1] if found else None
    records = load_run(runs_dir / run_id) if run_id and (runs_dir / run_id).exists() else []
    scenario_s = sum((r.get("timings") or {}).get("total", 0) for r in records if r.get("verdict") not in NOT_RUN)
    rate = [float(per_min) for _, per_min in CATCHER_RATE.findall(text)]
    catcher = catcher_logs(run_id, runs_dir.parent / "dumps") if run_id else {}
    if not rate:
        rate = [round(c["first_chance"] / c["minutes"], 2) for c in catcher.values() if c["minutes"] > 0]
    # How the run checked an arm's --set holds: "process" (no mod call) unless a scenario fell back to reading the
    # tunable list on a game ("tunables", with the scenarios per game), which a comparison should see.
    checks = [r["set_check"] for r in records if r.get("set_check")]
    read: dict[str, int] = {}
    for check in checks:
        for side, how in check.items():
            if how == "tunables":
                read[side] = read.get(side, 0) + 1
    set_check = {"set_check": "tunables" if read else "process", **({"set_check_tunables": read} if read else {})} if checks else {}
    return {**set_check,
        "arm": name, "repeat": repeat, "args": args, "run": run_id,
        "started": time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(started)),
        "wall_min": round((ended - started) / 60, 2), "scenario_min": round(scenario_s / 60, 2),
        "soak_min": round(sum((r.get("timings") or {}).get("soak", 0) for r in records if r.get("kind") == "soak") / 60, 2),
        "verdicts": {record_key(r): r.get("verdict") for r in records},
        "totals": {record_key(r): (r.get("timings") or {}).get("total") for r in records},
        "debugger": {record_key(r): r["debugger"] for r in records if r.get("debugger")},
        "crashes": crashes,
        "hangs": {} if crashes else {record_key(r): line for r in records if (line := arms_lib.watchdog_hang(r))},
        "catcher_exceptions_per_min": rate,
        **({"catcher": catcher} if catcher else {}),
        "excluded": excluded(runs_dir / run_id) if run_id else None,
    }


def _median(values: list[float]) -> float | None:
    values = sorted(v for v in values if v is not None)
    if not values:
        return None
    mid = len(values) // 2
    return values[mid] if len(values) % 2 else (values[mid - 1] + values[mid]) / 2


def verdict_code(row: dict, name: str) -> str:
    """An arm row's letter for one scenario (CODES): H for one the run watchdog cut short on a hang (row["hangs"]),
    CRASHED in its record but not a crash."""
    if name in (row.get("hangs") or {}):
        return "H"
    return CODES.get((row.get("verdicts") or {}).get(name), "?")


def summarize_arms(rows: list[dict], triage: Callable[[str], str] | None = None, odds: float | None = None,
                   hangs_as_crashes: bool = False) -> list[str]:
    """Per arm: runs, scenario minutes (and soak minutes, when a run soaked), crashes and crashes per exposure hour, the
    two-sided P of its rate against the first arm's, verdict tally; each arm's run crashes by site (triage: a runner
    crash line's triage now, default the one logged); when every run row has its server process snapshot, the
    exposure and crashes split by first and later arm in that process (arms_lib.first_arm, Session D's rule), and by
    series too when the rows carry one (arms --summary A B: each row's arms run), with a stratified one-sided P against
    the first arm, and for the first arms only that P and the unstratified one; with odds (minutes), each arm's Poisson
    chance of more crashes in that many more minutes at its rate so far; per scenario and arm: the verdicts in run
    order and the median scenario time. Each P names its method. An arm's exposure is its scenario minutes or its soak
    minutes; P compares only arms of the same kind, and an arm with both kinds gets none. A hang (row["hangs"]: the
    run watchdog cut a scenario short on a game still running) is not a crash: the arm's line counts it apart and
    the tallies show it as H; hangs_as_crashes (--hangs-as-crashes) counts it in the crashes too."""
    import arms_lib

    triage = triage or arms_lib.crash_note

    done = [r for r in rows if r.get("run")]
    arms = list(dict.fromkeys(r["arm"] for r in rows))
    soaks = any(r.get("soak_min") for r in done)
    lines = [f"{'arm':<14} {'runs':>4} {'scen-min':>8}" + (f" {'soak-min':>8}" if soaks else "")
             + f" {'crashes':>7} {'per hour':>8} {'P':>6}  verdicts"]
    first: tuple[int, float, str | None] | None = None  # the first arm's crashes, exposure and kind: P's reference
    kinds: dict[str, str | None] = {}
    totals: dict[str, tuple[int, float]] = {}
    for arm in arms:
        mine = [r for r in done if r["arm"] == arm]
        blocked = sum(1 for r in mine if all_blocked(r))
        minutes = sum(r["scenario_min"] for r in mine)
        soaked = sum(r.get("soak_min") or 0 for r in mine)
        hung = sum(len(r.get("hangs") or {}) for r in mine)
        crashes = sum(len(r["crashes"]) for r in mine) + (hung if hangs_as_crashes else 0)
        kind = ("mixed" if minutes and soaked else "soak" if soaked else "scenario" if minutes else None)
        exposure = minutes + soaked
        kinds[arm] = kind
        totals[arm] = (crashes, exposure)
        if first is None:
            first, p = (crashes, exposure, kind), None
        else:
            same = kind in ("soak", "scenario") and kind == first[2]
            p = arms_lib.two_sided_rate_p(crashes, exposure, first[0], first[1]) if same else None
        p_text = "-" if p is None else f"{p:.3f}" if p < 0.1 else f"{p:.2f}"
        outside = sum(len(r.get(key) or []) for r in mine
                      for key in ("crashes_boot", "crashes_before", "crashes_setup", "crashes_after", "crashes_gap"))
        tally: dict[str, int] = {}
        for r in mine:
            if all_blocked(r):
                continue  # not run: its B's are in the scenario table below
            for scenario in r["verdicts"]:
                code = verdict_code(r, scenario)
                tally[code] = tally.get(code, 0) + 1
        per_hour = f"{crashes / (exposure / 60):.1f}" if exposure else "-"
        skipped = sum(1 for r in rows if r["arm"] == arm and not r.get("run"))
        lines.append(f"{arm:<14} {len(mine) - blocked:>4} {minutes:>8.1f}" + (f" {soaked:>8.1f}" if soaks else "")
                     + f" {crashes:>7} {per_hour:>8} {p_text:>6}  "
                     + " ".join(f"{k}{v}" for k, v in sorted(tally.items())) + (f"  ({skipped} not run)" if skipped else "")
                     + (f"  ({blocked} not run (blocked))" if blocked else "")
                     + (f"  ({hung} hang(s) counted as crashes: --hangs-as-crashes)" if hung and hangs_as_crashes
                        else f"  ({hung} hang(s): the run watchdog cut a scenario short on a live game; not crashes)"
                        if hung else "")
                     + (f"  (+{outside} crash(es) outside the runs: "
                        + ("the boot hook, " if any(r.get("crashes_boot") for r in mine) else "")
                        + "setup, after hook, or the gap to the next arm)"
                        if outside else ""))
    if len(arms) > 1:
        lines.append(f"P: two-sided exact test of each arm's crash rate against {arms[0]}'s (the run crashes, "
                     "conditional on both arms' count, by exposure share), same exposure kind only; "
                     "scenarios.py exposure --pool prints a one-sided P")
    sites = []
    for arm in arms:
        tally = {}
        for r in done:
            if r["arm"] == arm:
                for text in r["crashes"]:
                    family = arms_lib.crash_family(triage(text))
                    tally[family] = tally.get(family, 0) + 1
        if tally:
            sites.append(f"{arm}: " + ", ".join(f"{k} {v}" for k, v in sorted(tally.items(), key=lambda kv: (-kv[1], kv[0]))))
    if sites:
        lines.append("run crashes by site: " + "; ".join(sites))
    places = [arms_lib.first_arm(r) for r in done]
    if done and len(arms) > 1 and None not in places:
        lines += first_later_lines(arms, done, places, kinds)
    if odds:
        lines += odds_lines(arms, totals, odds)
    lines += gc_gate_lines(arms, done)
    lines += machine_lines(arms, done)
    if any(r.get("catcher_dropped") for r in rows):
        lines.append("NAME:bare: a catcher arm run without the catcher (the disk or dump cap; --bare-when-capped)")
    scenarios = list(dict.fromkeys(name for r in done for name in r["verdicts"]))
    if scenarios:
        lines.append("")
        lines.append(f"{'scenario':<20} " + " ".join(f"{arm:<18}" for arm in arms))
        for name in scenarios:
            cells = []
            for arm in arms:
                mine = [r for r in done if r["arm"] == arm and name in r["verdicts"]]
                letters = "".join(verdict_code(r, name) for r in mine)
                median = _median([r["totals"].get(name) for r in mine if r["verdicts"][name] != "CRASHED"])  # cut short
                cells.append(f"{letters or '-'} {median:.1f}s".ljust(18) if median is not None else f"{letters or '-'}".ljust(18))
            lines.append(f"{name:<20} " + " ".join(cells))
    return lines


def gc_gate_lines(arms: list[str], done: list[dict]) -> list[str]:
    """Per arm with C's GC gate counters on its server (row["gc_gate"]): the runs, their bits, the mean of their
    pauseMeanMs and the largest pauseMaxMs. A long pause is what would block scenarios."""
    parts = []
    for arm in arms:
        gates = [r["gc_gate"]["server"] for r in done if r["arm"] == arm and (r.get("gc_gate") or {}).get("server")]
        if not gates:
            continue
        means = [g["pauseMeanMs"] for g in gates if isinstance(g.get("pauseMeanMs"), (int, float))]
        maxes = [g["pauseMaxMs"] for g in gates if isinstance(g.get("pauseMaxMs"), (int, float))]
        bits = "/".join(str(b) for b in sorted({g.get("bit") for g in gates}, key=str))
        parts.append(f"{arm} {len(gates)} run(s), bit {bits}"
                     + (f", pause mean {sum(means) / len(means):.2f} ms ({min(means):g}..{max(means):g})" if means else "")
                     + (f", max {max(maxes):g} ms" if maxes else ""))
    return ["GC gate pauses (the server's end reading per run, counted from its process start): " + "; ".join(parts)] \
        if parts else []


def machine_lines(arms: list[str], done: list[dict]) -> list[str]:
    """Per arm, from its run rows: the build slot it ran and the sets its mod never read, and the
    machine's load over its runs (G19, row["load"]): CPU (each run's mean weighted by its seconds, and the busiest
    interval), the least free memory, and the processes other than the stack's own games with the most CPU (the largest
    of their per-run means, and in how many of the arm's sampled runs they were in the top)."""
    lines = []
    for arm in arms:
        mine = [r for r in done if r["arm"] == arm]
        parts = []
        slots = [f"{r['build_slot'].get('slot')} ({r['build_slot'].get('digest')})" for r in mine if r.get("build_slot")]
        if slots:
            parts.append("build slot " + ", ".join(dict.fromkeys(slots)) + f" in {len(slots)}/{len(mine)} run(s)")
        unread: dict[str, int] = {}
        for r in mine:
            for item in r.get("sets_unread") or []:
                unread[item] = unread.get(item, 0) + 1
        if unread:
            parts.append("not read by its mod: " + ", ".join(f"{k} ({n}/{len(mine)})" for k, n in unread.items()))
        loads = [r["load"] for r in mine if (r.get("load") or {}).get("samples")]
        if loads:
            means = [(lo["cpu_pct"]["mean"], lo.get("seconds") or 0) for lo in loads
                     if (lo.get("cpu_pct") or {}).get("mean") is not None]
            weight = sum(w for _, w in means)
            mean = (sum(m * w for m, w in means) / weight if weight else sum(m for m, _ in means) / len(means)) if means else None
            peak = max((lo["cpu_pct"]["max"] for lo in loads if (lo.get("cpu_pct") or {}).get("max") is not None), default=None)
            free = min((lo["avail_mb"]["min"] for lo in loads if (lo.get("avail_mb") or {}).get("min") is not None), default=None)
            others: dict[str, tuple[float, int]] = {}
            for lo in loads:
                for proc in lo.get("others") or []:
                    best, seen = others.get(proc["name"], (0.0, 0))
                    others[proc["name"]] = (max(best, proc["cpu_pct"]), seen + 1)
            text = (f"load over {len(loads)} run(s): cpu " + (f"{mean:.1f}% mean" if mean is not None else "-")
                    + (f", {peak:.0f}% max" if peak is not None else "")
                    + (f"; free {free / 1024:.1f} GB min" if free is not None else ""))
            busiest = sorted(others.items(), key=lambda item: -item[1][0])[:3]
            if busiest:
                text += "; others: " + ", ".join(f"{name} {pct:.1f}% ({seen}/{len(loads)})" for name, (pct, seen) in busiest)
            parts.append(text)
        if parts:
            lines.append(f"  {arm}: " + "; ".join(parts))
    return (["build and machine by arm (each run row's build slot, the sets its mod never read, machine_load over the "
             "run):"] + lines) if lines else []


def arm_notes_text(row: dict) -> str:
    """An arm row's build slot, the sets its mod never read and its run's machine load, in one line (arms --readings)."""
    import machine_load

    parts = []
    if row.get("build_slot"):
        parts.append(f"build slot {row['build_slot'].get('slot')} ({row['build_slot'].get('digest')})")
    if row.get("sets_unread"):
        parts.append("not read by its mod: " + ", ".join(row["sets_unread"]))
    if row.get("load"):
        parts.append("load: " + machine_load.text(row["load"]))
    return "; ".join(parts)


def installed_build() -> dict | None:
    """The build slot the controllers run now (build_slot.py install's pointer, G17): its slot and digest."""
    import quick_loop

    pointer = quick_loop.slot_pointer()
    return {"slot": pointer.get("slot"), "digest": pointer.get("digest")} if pointer else None


def run_load(summary: dict) -> dict:
    """machine_load's summary of an arm's run, as its row keeps it: all but the per-sample series (hours of samples)."""
    return {key: value for key, value in summary.items() if key != "series"}


def all_blocked(row: dict) -> bool:
    """An arm whose every scenario did not run (BLOCKED on a precondition, e.g. the head Sim's Stand queued, or
    RESET-FAILED on its own last check, ) and that has no exposure: it ran nothing, so the summary counts it as
    not run (blocked), and --rerun-blocked runs it again."""
    verdicts = row.get("verdicts") or {}
    return (bool(row.get("run")) and bool(verdicts) and all(v in NOT_RUN for v in verdicts.values())
            and not row.get("scenario_min") and not row.get("soak_min"))


def place_text(kf: int, mf: float, kl: int, ml: float) -> str:
    """One arm's first and later strata in a series; a stratum with no runs is named, not printed as 0 in 0.00 min."""
    if not ml and not kl:
        return f"first {kf} in {mf:.2f} min, every arm first in its process" if mf or kf else "no runs"
    if not mf and not kf:
        return f"later {kl} in {ml:.2f} min, every arm later in its process"
    return f"first {kf} in {mf:.2f} min, later {kl} in {ml:.2f} min"


def counted(n: int) -> str:
    return f"{n} stratum" if n == 1 else f"{n} strata"


def first_later_lines(arms: list[str], done: list[dict], places: list[bool], kinds: dict[str, str | None]) -> list[str]:
    """The first/later split (arms_lib.first_arm), by series when the rows carry one, with each arm's P against the
    first arm: stratified one-sided exact over every stratum (arms_lib.stratified_rate_p), then over the first-arm
    strata only and, pooling those, the one-sided conditional binomial (arms_lib.compare_rates) with no strata."""
    import arms_lib

    series = list(dict.fromkeys(r.get("series") for r in done))
    pooled = series != [None]
    strata = [(s, want) for s in series for want in (True, False)]
    of: dict[str, dict] = {}
    for arm in arms:
        mine = [(r, place) for r, place in zip(done, places) if r["arm"] == arm]
        of[arm] = {(s, want): (sum(len(r["crashes"]) for r, place in mine if r.get("series") == s and place is want),
                               sum(r["scenario_min"] + (r.get("soak_min") or 0) for r, place in mine
                                   if r.get("series") == s and place is want))
                   for s, want in strata}
    lines = ["first/later arm in its server process (first: no arm before it in that game; exposure and run crashes as "
             "above)" + (f", by series ({len(series)} arms runs, the strata series x first/later)" if pooled else "") + ":"]
    ref = arms[0]
    for arm in arms:
        cells = [place_text(*of[arm][(s, True)], *of[arm][(s, False)]) for s in series]
        text = f"{arm:<14} " + ("; ".join(f"{s}: {c}" for s, c in zip(series, cells)) if pooled else cells[0])
        same = arm != ref and kinds.get(arm) in ("soak", "scenario") and kinds.get(arm) == kinds.get(ref)
        firsts = None
        if same:
            def pairs(keys):
                return [(*of[ref][key], *of[arm][key]) for key in keys]

            def used(keys):
                return sum(1 for k0, m0, k, m in pairs(keys) if m0 > 0 and m > 0)

            p = arms_lib.stratified_rate_p(pairs(strata))
            if p is not None:
                text += (f"; P {p:.3f} stratified one-sided exact, {ref} against it stratum by stratum "
                         f"({counted(used(strata))}: {'series x first/later' if pooled else 'first/later'})")
            first_keys = [(s, True) for s in series]
            k0, m0 = sum(of[ref][key][0] for key in first_keys), sum(of[ref][key][1] for key in first_keys)
            k, m = sum(of[arm][key][0] for key in first_keys), sum(of[arm][key][1] for key in first_keys)
            c = arms_lib.compare_rates(k0, m0, k, m)
            if c["p"] is not None:
                firsts = f"{'':<14} first arms only: "
                if pooled:
                    p_first = arms_lib.stratified_rate_p(pairs(first_keys))
                    firsts += (f"P {p_first:.3f} stratified one-sided exact ({counted(used(first_keys))}: series); "
                               if p_first is not None else "")
                firsts += (f"{ref} {k0} in {m0:.2f} min against {k} in {m:.2f}" + (" pooled" if pooled else "")
                           + f": P {c['p']:.3f} one-sided conditional binomial" + (" (no strata)" if pooled else ""))
        lines.append(text)
        if firsts:
            lines.append(firsts)
    return lines


def odds_lines(arms: list[str], totals: dict[str, tuple[int, float]], minutes: float) -> list[str]:
    """arms --summary --odds MIN: for each arm, the Poisson chance of at least j more run crashes in MIN more minutes
    of its exposure at its rate so far (crashes / exposure), and the total each would reach, j = 1 up to the first
    chance under 0.05."""
    lines = [f"odds over {minutes:g} more min at each arm's rate so far (Poisson; exposure as above):"]
    for arm in arms:
        k, m = totals.get(arm, (0, 0.0))
        if not k or m <= 0:
            lines.append(f"{arm:<14} {k} in {m:.2f} min: no rate to project")
            continue
        mean = k / m * minutes
        parts, below, term = [], 0.0, math.exp(-mean)
        for j in range(1, 11):
            below += term  # P(X <= j - 1)
            term *= mean / j
            chance = max(0.0, 1.0 - below)
            parts.append(f"P(>={j} more) {chance:.2f} (total >= {k + j})")
            if chance < 0.05:
                break
        lines.append(f"{arm:<14} {k} in {m:.2f} min ({k / m:.4f}/min, mean {mean:.2f} more): " + ", ".join(parts))
    return lines


def catcher_verdict(rows: list[dict], folder: str) -> list[str]:
    """The passive catcher's A/B: per scenario, catcher against bare (verdicts, median time), set against how much two
    bare runs of the same scenario already differ; then the line that would count catcher runs from now on."""
    done = [r for r in rows if r.get("run")]
    bare = [r for r in done if r["arm"] == "bare"]
    caught = [r for r in done if r["arm"] == "catcher" and not r.get("catcher_dropped")]
    if not bare or not caught:
        return ["catcher A/B: needs at least one bare and one catcher run"]
    names = [n for n in dict.fromkeys(n for r in done for n in r["verdicts"]) if any(n in r["verdicts"] for r in bare)
             and any(n in r["verdicts"] for r in caught)]
    flips, deltas, noise = [], [], []
    for name in names:
        b = [r for r in bare if name in r["verdicts"]]
        c = [r for r in caught if name in r["verdicts"]]
        if {r["verdicts"][name] for r in c} - {r["verdicts"][name] for r in b}:
            flips.append(name)
        mb, mc = _median([r["totals"].get(name) for r in b]), _median([r["totals"].get(name) for r in c])
        if mb and mc:
            deltas.append((mc - mb) / mb)
        totals = [r["totals"].get(name) for r in b if r["totals"].get(name)]
        if len(totals) > 1:
            noise.append((max(totals) - min(totals)) / min(totals))
    delta = _median(deltas)
    spread = _median(noise)
    crashes_bare = sum(len(r["crashes"]) for r in bare)
    crashes_caught = sum(len(r["crashes"]) for r in caught)
    rates = [v for r in caught for v in r.get("catcher_exceptions_per_min", [])]
    lines = ["catcher A/B (passive cdb on both games against none):",
             f"  scenario time, catcher against bare: median {delta * 100:+.1f}%" if delta is not None else "  scenario time: no pairs",
             f"  two bare runs of one scenario already differ by a median {spread * 100:.1f}%" if spread is not None
             else "  bare runs: one each, so no noise floor (use --repeat 2 or more)",
             f"  verdicts only seen with the catcher: {', '.join(flips) if flips else 'none'}",
             f"  crashes: bare {crashes_bare}, catcher {crashes_caught}",
             f"  first-chance exceptions the catcher round-tripped: {', '.join(f'{v:.1f}/min' for v in rates) or 'not read'}"]
    same = not flips and delta is not None and abs(delta) <= max(0.05, spread or 0.0)
    verdict = "no difference beyond run-to-run noise" if same else "the catcher changes verdicts or timings"
    lines.append(f"  verdict: {verdict}")
    if same:
        lines.append(f'  to count passive-catcher runs from now on (the user\'s call): CATCHER_MEASURED = '
                     f'"{time.strftime("%Y-%m-%d")} {folder}: {verdict}" in scripts/scenarios.py')
    return lines


def append_log(log: Path, record: dict) -> None:
    with log.open("a", encoding="utf-8") as f:
        f.write(json.dumps(record) + "\n")


STATUS_COMMAND = "SACS_RECORDER status"
STATUS_ENDPOINTS = {"server": SERVER, "client": CLIENT}


def read_status(endpoint: str, timeout: float = 10.0) -> str | None:
    """One game's SACS_RECORDER status line, or None when it does not answer. bridgectl exits (SystemExit) on a refused
    connection or a timeout, which in the sampler's thread would end the thread without a word: caught here."""
    import bridgectl

    try:
        sock = bridgectl.connect(endpoint, timeout)
        try:
            reply = bridgectl.roundtrip(sock, STATUS_COMMAND)
        finally:
            sock.close()
    except (OSError, SystemExit, ValueError):
        return None
    return str(reply.get("report") or "") if reply.get("ok", True) else None


PATCH_FIELDS = ("onScreen", "chunkReserve")  # SimLink's at-load patches every game must share


def patch_line(statuses: dict[str, str | None]) -> str:
    """One line from each game's SACS_RECORDER status: its SimLink patch states, and a warning when the games disagree.
    Games whose on-screen walk differs walk the same route two ways (internal documentation), so a
    run's walk verdicts mean little until every game says the same.
    A field with ready=0 (SimLinkShadow.NativeVersion 0: the game has no SimLink ring, as a client booted without
    quick_loop's CLIENT_ENV, or a SimLink older than the patch) is not a patch that is off: it is printed as not ready
    and left out of the comparison."""
    import arms_lib

    parts, seen = [], {field: set() for field in PATCH_FIELDS}
    for side, text in statuses.items():
        if text is None:
            parts.append(f"{side} no answer")
            continue
        flat = arms_lib.parse_status(text)
        states = []
        for field in PATCH_FIELDS:
            if flat.get(f"{field}.ready") == "0":
                states.append(f"{field}=not ready (no client ring?)")
                continue
            value = flat.get(field, "-")
            seen[field].add(value)
            states.append(f"{field}={value}")
        parts.append(f"{side} " + " ".join(states))
    differ = [field for field, values in seen.items() if len(values) > 1]
    return "native patches: " + "; ".join(parts) + (f" -- WARNING: {', '.join(differ)} differ between games"
                                                    if differ else "")


class ArmStatus:
    """One arm's SACS_RECORDER status readings from both games (arms --status, --status-every): start (before the
    arm's sets, as boot 5's arms read it), end (after the run), after (after the after hook) and, with every > 0, one
    every `every` seconds on a thread from the start reading to the after reading, so an arm that crashes keeps its
    last counters (last: the newest answer per game). Each reading is appended to path as it arrives. Opt-in: each
    reading is one more mod call that builds strings, and a timer adds them during the arm, where they could change
    the rate of a GC use-after-free."""

    def __init__(self, path: Path, arm: str, repeat: int, every: float = 0.0):
        self.path, self.arm, self.repeat, self.every = path, arm, repeat, every
        self.readings: dict[str, dict] = {}
        self.last: dict[str, dict] = {}
        self.samples = 0
        self.misses = {side: 0 for side in STATUS_ENDPOINTS}
        self._lock = threading.Lock()
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None

    def read(self, kind: str) -> dict:
        import arms_lib

        when = round(time.time(), 1)
        texts = {side: read_status(endpoint) for side, endpoint in STATUS_ENDPOINTS.items()}
        snap = {side: arms_lib.parse_status(text) if text else None for side, text in texts.items()}
        with self._lock:
            for side, values in snap.items():
                if values:
                    self.last[side] = {"t": when, "status": values}
                else:
                    self.misses[side] += 1
            if kind == "timer":
                self.samples += 1
            else:
                self.readings[kind] = snap
            try:
                append_log(self.path, {"arm": self.arm, "repeat": self.repeat, "kind": kind, "t": when, **snap})
            except OSError:
                pass
        return snap

    def start_timer(self) -> "ArmStatus":
        if self.every > 0 and self._thread is None:
            self._thread = threading.Thread(target=self._loop, name=f"status-{self.arm}", daemon=True)
            self._thread.start()
        return self

    def _loop(self) -> None:
        while not self._stop.wait(self.every):
            try:
                self.read("timer")
            except (Exception, SystemExit):  # never let the timer die: the last counters matter most when a game does
                continue

    def stop(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=30)

    def record(self, restarted: list[str] | None = None) -> dict:
        with self._lock:
            return {**{kind: self.readings.get(kind) for kind in ("start", "end", "after")}, "last": dict(self.last),
                    "samples": self.samples, "every": self.every, "misses": dict(self.misses),
                    "restarted": list(restarted or [])}


def status_line(record: dict) -> str:
    """The arm's status in one line: what was read and the counters that moved from start to end (delta)."""
    import arms_lib

    views = arms_lib.status_views(record)
    read = [kind for kind in ("start", "end", "after") if any((record.get(kind) or {}).values())]
    parts = [f"read {', '.join(read) or 'nothing'}"]
    if record["every"]:
        parts.append(f"{record['samples']} timer sample(s) every {record['every']:g} s")
    missed = [f"{side} {n}" for side, n in record["misses"].items() if n]
    if missed:
        parts.append("no answer: " + ", ".join(missed))
    for side in ("server", "client"):
        delta = views["delta"][side]
        if delta:
            moved = [f"{key}+{value:g}" if value > 0 else f"{key}{value:g}" for key, value in sorted(delta.items()) if value]
            parts.append(f"{side} moved: " + (", ".join(moved[:12]) + (f" (+{len(moved) - 12} more)" if len(moved) > 12 else "")
                                             if moved else "nothing"))
    return "; ".join(parts)


def check_arm_gates(gates: list[dict], row: dict, hook_file: Path) -> str | None:
    """Check each gate that names this arm, on its finished row; row["gates"] gets the results. Returns why the run
    stops (a gate failed or could not be evaluated), else None. An arm that died, or whose TS3 restarted, is not
    evaluated: the gate waits for the next arm it names (a dead arm's counters say nothing about the copy)."""
    import arms_lib

    stop = None
    for gate in gates:
        if not arms_lib.gate_applies(gate, row["arm"]):
            continue
        if row.get("died_in") or row.get("process_restarted"):
            result = {"result": "not evaluated", "values": {}, "why": f"the arm died ({row.get('died_in') or 'restart'})"}
        else:
            try:
                text = hook_file.read_text(encoding="utf-8") if hook_file.exists() else ""
            except OSError:
                text = ""
            result = arms_lib.check_gate(gate, row, text)
            gate["done"] = gate["once"]
        result = {"gate": gate["text"], **result}
        gate["results"].append({"arm": row["arm"], "repeat": row.get("repeat", 1), "result": result["result"]})
        row.setdefault("gates", []).append(result)
        values = ", ".join(f"{k}={v}" for k, v in result["values"].items())
        print(f"   gate {gate['text']!r}: {result['result']}" + (f" ({values})" if values else "")
              + (f"; {result['why']}" if result["why"] else ""), flush=True)
        if result["result"] in ("failed", "error") and stop is None:
            stop = f"gate {gate['text']!r} {result['result']} after {row['arm']} #{row.get('repeat', 1)}"
    return stop


def gate_summary(gates: list[dict]) -> list[str]:
    lines = []
    for gate in gates:
        done = [r for r in gate["results"] if r["result"] != "not evaluated"]
        waited = [f"{r['arm']} #{r['repeat']}" for r in gate["results"] if r["result"] == "not evaluated"]
        told = (", ".join(f"{r['result']} after {r['arm']} #{r['repeat']}" for r in done) if done
                else "never evaluated (no arm it names finished alive)")
        lines.append(f"gate {gate['text']!r}: {told}" + (f"; not evaluated after {', '.join(waited)} (died)" if waited else ""))
    return [""] + lines if lines else []


def arms_folder(name: str, base: Path | None = None) -> Path:
    """An arms run's folder: latest (the newest), a folder name under base (.omo/run/arms), or a path."""
    base = base or ARMS
    folders = sorted(f for f in base.iterdir() if f.is_dir()) if base.exists() else []
    if name in ("", "latest"):
        if not folders:
            raise SystemExit(f"no arms run in {base}")
        return folders[-1]
    path = Path(name)
    if path.is_dir():
        return path
    if (base / name).is_dir():
        return base / name
    raise SystemExit(f"no arms run {name} in {base} (have: {', '.join(f.name for f in folders[-5:]) or 'none'})")


def read_arms_base() -> Path:
    """Where the read-only arms views (--crashes, --summary) look a folder name up: SIMSONLINE_RUN_DIR's arms folder
    when that is set (the live worktree's .omo/run, read from another), else this checkout's."""
    run_dir = os.environ.get("SIMSONLINE_RUN_DIR")
    return Path(run_dir) / "arms" if run_dir else ARMS


def report_paths(run_dir: Path) -> dict[tuple[str, str], Path]:
    """Every crash report on disk by (side, report name), read only (crash_catch.crash_reports archives first, so it
    writes): the run folder's crash archive (.omo/run/crash-reports/LABEL, copies with their times kept), then the
    profiles TS3 writes to, whose copy wins. {} for what cannot be read."""
    try:
        import crash_catch
    except ImportError:
        crash_catch = _MirrorProcFallback

    found: dict[tuple[str, str], Path] = {}
    for path in sorted((run_dir / "crash-reports").glob("*/xcpt*.txt")):
        found[("server" if path.parent.name == crash_catch.SERVER_LABEL else "client", path.name)] = path
    try:
        folders = crash_catch.report_folders()
    except Exception:
        folders = {}
    for label, folder in folders.items():
        for path in Path(folder).glob("xcpt*.txt"):
            found[("server" if label == crash_catch.SERVER_LABEL else "client", path.name)] = path
    return found


def report_when(path: Path | None, name: str) -> float | None:
    """A crash's time: its report file's time (TS3 writes it at the crash; the archive keeps it), else the time in the
    report's name (xcpt HOST 26-10-19 03.23.20.txt, to the second), else None."""
    if path is not None:
        try:
            return path.stat().st_mtime
        except OSError:
            pass
    m = re.search(r"(\d\d-\d\d-\d\d \d\d\.\d\d\.\d\d)\.txt$", name)
    return time.mktime(time.strptime(m.group(1), "%y-%m-%d %H.%M.%S")) if m else None


def safe_triage(reports: Callable[[], dict[tuple[str, str], Path]]) -> Callable[[str], str]:
    """A runner crash line's triage now: crash_catch's line for its report when that is still on disk (reports() is
    read once, on first use), else the triage the runner logged. Never raises (a summary at a run's end uses it)."""
    import arms_lib

    found: list[dict] = []

    def triage(text: str) -> str:
        try:
            if not found:
                found.append(reports())
            path = found[0].get(arms_lib.crash_parts(text))
            return describe_crash(path) if path else arms_lib.crash_note(text)
        except Exception:
            return arms_lib.crash_note(text)

    return triage


def read_records(folder: Path) -> list[dict]:
    """An arms run's log, its arm rows with their minutes booked (arms_lib.book_minutes: a died scenario's minutes up
    to the crash, the same for every arm; the runner's own value kept as completed_min)."""
    import arms_lib

    path = folder / "arms.jsonl"
    if not path.exists():
        raise SystemExit(f"no arms.jsonl in {folder}")
    return arms_lib.book_log([json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()],
                             folder)


def arms_crashes(name: str, rows_path: Path | None = None) -> int:
    """arms --crashes: one line per crash of an arms run, read only (no game, nothing written): time, side, arm, phase,
    scenario, the arm's place in its game's process, the game's uptime and last collection (the run folder's
    gc-watch.jsonl, or rows_path), and the triage now."""
    import arms_lib

    folder = arms_folder(name, read_arms_base())
    run_dir = folder.parent.parent
    gc_path = rows_path or run_dir / "gc-watch.jsonl"
    gc_rows = []
    if gc_path.exists():
        for line in gc_path.read_text(encoding="utf-8", errors="replace").splitlines():
            try:
                gc_rows.append(json.loads(line))
            except ValueError:
                continue  # a row cut by a watch that was killed mid-write
    crashes = arms_lib.crash_rows(read_records(folder), report_paths(run_dir), describe_crash, gc_rows, report_when)
    print(f"arms {folder.name}: {len(crashes)} crash(es); collections from "
          + (f"{gc_path} ({len(gc_rows)} rows)" if gc_rows else f"no gc-watch rows ({gc_path})"))
    if not crashes:
        return 0

    def num(value: float | None, unit: str, digits: int = 2) -> str:
        return "-" if value is None else f"{value:.{digits}f} {unit}"

    print(f"{'time':<14} {'side':<6} {'arm':<10} {'phase':<5} {'scenario':<16} {'pid':>6} {'place':<10} "
          f"{'uptime':>8} {'last gc':>8} {'interval':>8} {'gc':>5}  triage")
    for c in crashes:
        place = "-" if c["first"] is None else "first" if c["first"] else f"later ({c['arms_before']})"
        family = arms_lib.crash_family(c["triage"])
        detail = c["triage"].split(family, 1)[1].strip() if family in c["triage"] else ""
        when = time.strftime("%m-%d %H:%M:%S", time.localtime(c["t"])) if c["t"] is not None else "?"
        print(f"{when:<14} {c['side']:<6} {c['arm'] + '#' + str(c['repeat']):<10} {c['phase']:<5} "
              f"{c['scenario'] or '-':<16} {c['pid'] or '-':>6} {place:<10} {num(c['uptime_min'], 'min', 1):>8} "
              f"{num(c['last_gc_s'], 's'):>8} {num(c['interval_s'], 's'):>8} {c['gc'] if c['gc'] is not None else '-':>5}  "
              + family + (f" {detail}" if detail else "") + ("" if c["source"] == "report" else " [as logged]"))
        if c.get("dumps"):
            print("    dumps: " + ", ".join(f"{n} {'?' if b is None else f'{b / 2**20:.0f} MB'}" for n, b in c["dumps"].items()))
    print("place: first = no arm before it in that game's process (the arm's start snapshot), later (N arms before); "
          "uptime from the watch's first look at the pid; last gc: seconds from the last collection to the report, "
          "interval: the gap before that one")
    return 0


def arms_readings(name: str, fields: list[str], kinds: list[str]) -> int:
    """arms --readings: an arms run's status readings (status.jsonl: each game's SACS_RECORDER status at each arm's
    start, end, after and timer reads), one line per reading with the fields asked (exact names or fnmatch patterns,
    e.g. eventRecorder.*); with none, the field names the readings hold. Read only."""
    import fnmatch

    folder = arms_folder(name, read_arms_base())
    path = folder / "status.jsonl"
    if not path.exists():
        raise SystemExit(f"no status.jsonl in {folder} (an arms run with --status, --status-every or --gate writes one)")
    readings = []
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        try:
            readings.append(json.loads(line))
        except ValueError:
            continue
    names = list(dict.fromkeys(k for r in readings for side in ("server", "client") for k in (r.get(side) or {})))
    if not fields:
        print(f"arms {folder.name}: {len(readings)} reading(s); fields: " + ", ".join(names))
        return 0
    wanted = [n for n in names if any(fnmatch.fnmatchcase(n, f) for f in fields)]
    missing = [f for f in fields if not any(fnmatch.fnmatchcase(n, f) for n in names)]
    if missing:
        print(f"no reading has {', '.join(missing)} (scenarios.py arms --readings {folder.name} lists the fields)")
    if not wanted:
        return 1
    print(f"arms {folder.name}: " + ", ".join(wanted))
    import arms_lib

    notes = {(row.get("arm"), row.get("repeat", 1)): arm_notes_text(row)
             for row in (arms_lib.arm_rows(read_records(folder)) if (folder / "arms.jsonl").exists() else [])}
    shown = set()
    for r in readings:
        if kinds and r.get("kind") not in kinds:
            continue
        key = (r.get("arm"), r.get("repeat", 1))
        if key not in shown and notes.get(key):  # the arm row's build, unread sets and load, once ( G19)
            shown.add(key)
            print(f"{str(key[0]) + '#' + str(key[1]):<9} row     {notes[key]}")
        sides = []
        for side in ("server", "client"):
            status = r.get(side)
            sides.append(f"{side}: " + (" ".join(f"{n}={status[n]}" for n in wanted if n in status) or "-" if status
                                        else "down (no answer)"))
        when = time.strftime("%H:%M:%S", time.localtime(r["t"])) if isinstance(r.get("t"), (int, float)) else "?"
        print(f"{str(r.get('arm')) + '#' + str(r.get('repeat')):<9} {r.get('kind', '?'):<6} {when}  " + " | ".join(sides))
    return 0


READING_RANK = {"timer": 0, "end": 1, "after": 2}  # which reading is an arm's last: after, else end, else the last timer read


def arm_status_ends(folder: Path) -> dict[tuple[str, int], dict[str, dict[str, dict]]]:
    """An arms run's status readings (status.jsonl) by (arm, repeat): each game's start reading and its last one
    (after, else end, else the last timer read; a game that gave no answer at one reading keeps its earlier one), as
    {"start": {side: status}, "last": {side: status}}; {} without a status.jsonl."""
    path = folder / "status.jsonl"
    if not path.exists():
        return {}
    out: dict[tuple[str, int], dict[str, dict[str, dict]]] = {}
    ranks: dict[tuple[str, int, str], int] = {}
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        try:
            r = json.loads(line)
        except ValueError:
            continue
        key = (r.get("arm"), r.get("repeat", 1))
        ends = out.setdefault(key, {"start": {}, "last": {}})
        kind = r.get("kind")
        for side in ("server", "client"):
            status = r.get(side)
            if not isinstance(status, dict):
                continue
            if kind == "start":
                ends["start"][side] = status
            elif kind in READING_RANK and READING_RANK[kind] >= ranks.get((*key, side), -1):
                ends["last"][side] = status
                ranks[(*key, side)] = READING_RANK[kind]
    return out


def reading_delta(ends: dict[str, dict[str, dict]] | None, side: str, field: str) -> str:
    """One --table cell: the field's change on that game from the arm's start reading to its last (last - start when
    both are numbers), else the last value as is, else - (no reading, or the field missing)."""
    if not ends:
        return "-"
    last = ends["last"].get(side) or {}
    start = ends["start"].get(side) or {}
    value = last.get(field)
    if value is None:
        return "-"
    try:
        return f"{float(value) - float(start[field]):g}" if field in start else str(value)
    except (TypeError, ValueError):
        return str(value)


def arms_table(names: list[str], fields: list[str], as_json: bool = False) -> int:
    """arms --table [--folder FOLDER ...]: one row per arm run of an arms run (several folders pooled, each row with
    its series), read only: arm#repeat, run, start time, wall and scenario (and soak) minutes, run crashes and crashes
    outside the run (boot, setup, after hook, gap), the verdict tally, where it died; with --field (exact names or
    fnmatch patterns), each field's change from the arm's start status reading to its last, per game (S server, C
    client; a value that is not a number shows as is). --json prints the same rows as JSON lines, with the --after
    hook's NAME=N values (hook), for a script to read instead of parsing the text or arms.jsonl itself
    (arms_values.py does, to compare two arms on any of them)."""
    import arms_lib
    import fnmatch

    folders = [arms_folder(name, read_arms_base()) for name in names or ["latest"]]
    rows: list[dict] = []
    for folder in folders:
        ends = arm_status_ends(folder)
        names_seen = list(dict.fromkeys(k for e in ends.values() for part in ("start", "last") for status in e[part].values()
                                        for k in status))
        wanted = [n for n in names_seen if any(fnmatch.fnmatchcase(n, f) for f in fields)]
        for r in arms_lib.arm_rows(read_records(folder)):
            key = (r.get("arm"), r.get("repeat", 1))
            tally: dict[str, int] = {}
            for scenario in r.get("verdicts") or {}:
                code = verdict_code(r, scenario)
                tally[code] = tally.get(code, 0) + 1
            row = {"series": folder.name, "arm": r.get("arm"), "repeat": r.get("repeat", 1), "run": r.get("run"),
                   "started": r.get("started"), "wall_min": r.get("wall_min"), "scenario_min": r.get("scenario_min") or 0,
                   "soak_min": r.get("soak_min") or 0, "crashes": len(r.get("crashes") or []),
                   "hangs": len(r.get("hangs") or {}),
                   "crashes_outside": sum(len(r.get(k) or []) for phase, k in arms_lib.CRASH_KEYS if phase != "run"),
                   "verdicts": " ".join(f"{k}{v}" for k, v in sorted(tally.items())) or ("not run" if not r.get("run") else "-"),
                   "died_in": r.get("died_in") or "",
                   "hook": r.get("hook") or {},
                   "fields": {f"{side[0].upper()}:{n}": reading_delta(ends.get(key), side, n)
                              for side in ("server", "client") for n in wanted}}
            rows.append(row)
    if as_json:
        for row in rows:
            print(json.dumps(row))
        return 0
    columns = list(dict.fromkeys(c for row in rows for c in row["fields"]))
    pooled = len(folders) > 1
    soaks = any(row["soak_min"] for row in rows)
    print(f"arms {', '.join(f.name for f in folders)}: {len(rows)} arm row(s)"
          + ("; fields: each game's last status reading minus its start reading (S server, C client)" if columns else ""))
    print((f"{'series':<16} " if pooled else "") + f"{'arm':<10} {'run':<16} {'started':<19} {'wall':>6} {'scen-min':>8}"
          + (f" {'soak-min':>8}" if soaks else "") + f" {'crash':>5} {'out':>4}  {'verdicts':<14} {'died in':<12}"
          + "".join(f" {c:>14}" for c in columns))
    for row in rows:
        wall = f"{row['wall_min']:.1f}" if isinstance(row["wall_min"], (int, float)) else "-"
        print((f"{row['series']:<16} " if pooled else "")
              + f"{str(row['arm']) + '#' + str(row['repeat']):<10} {row['run'] or '-':<16} {row['started'] or '-':<19} "
              f"{wall:>6} {row['scenario_min']:>8.1f}" + (f" {row['soak_min']:>8.1f}" if soaks else "")
              + f" {row['crashes']:>5} {row['crashes_outside']:>4}  {row['verdicts']:<14} {row['died_in'] or '-':<12}"
              + "".join(f" {row['fields'].get(c, '-'):>14}" for c in columns))
    print("crash: in the run; out: in the arm's boot, setup, after hook or the gap to the next arm"
          + ("; H, died in hang: the run watchdog cut a scenario short on a live game (not a crash)"
             if any(row["hangs"] for row in rows) else ""))
    if fields and not columns:
        print(f"no reading has {', '.join(fields)}" + (" (no status.jsonl)" if not any(arm_status_ends(f) for f in folders)
                                                        else " (scenarios.py arms --readings lists the fields)"))
        return 1
    return 0


def arms_compare(a: str, b: str, names: list[str]) -> int:
    """arms --compare A B [--folder FOLDER ...]: arm A's run crashes and exposure minutes against arm B's over the
    arms run(s), read only: the one-sided conditional binomial P in the direction the data lean, the rate ratio and
    its exact 95% interval (arms_lib.compare_rates, the test the crash chase used), after the counts it read. An
    arm's exposure is its scenario minutes plus its soak minutes; an arm with both kinds is said so."""
    import arms_lib

    folders = [arms_folder(name, read_arms_base()) for name in names or ["latest"]]
    rows = [r for folder in folders for r in arms_lib.arm_rows(read_records(folder))]
    have = list(dict.fromkeys(r.get("arm") for r in rows))
    missing = [arm for arm in (a, b) if arm not in have]
    if missing:
        raise SystemExit(f"--compare: no arm {', '.join(missing)} in {', '.join(f.name for f in folders)} "
                         f"(have: {', '.join(str(x) for x in have) or 'none'})")
    counts: dict[str, tuple[int, float, int, bool]] = {}
    for arm in (a, b):
        mine = [r for r in rows if r.get("arm") == arm and r.get("run")]
        minutes = sum(r.get("scenario_min") or 0 for r in mine)
        soak = sum(r.get("soak_min") or 0 for r in mine)
        counts[arm] = (sum(len(r.get("crashes") or []) for r in mine), minutes + soak, len(mine), bool(minutes and soak))
        k, m, n, mixed = counts[arm]
        print(f"{arm}: {k} run crash(es) in {m:.1f} min over {n} run(s)" + (f", {k / (m / 60):.1f} per hour" if m else "")
              + (" (scenario and soak minutes mixed)" if mixed else ""))
    print(arms_lib.describe_comparison(a, b, arms_lib.compare_rates(counts[a][0], counts[a][1], counts[b][0], counts[b][1])))
    print("arms: " + ", ".join(str(f) for f in folders) + "; run crashes only (arms --table shows those outside the "
          "run); scenarios.py exposure --pool compares by tunable or debugger across saved runs")
    return 0


def arms_check(names: list[str], specs: list[str], repeat: int, order_text: str, gate_texts: list[str], boot: str,
               catcher_ab: bool, against: str | None, stop_texts: list[str] | None = None,
               server_env: list[str] | None = None, client_env: list[str] | None = None,
               clients: int | None = None) -> int:
    """arms --check: what an arms run would do, with no game touched and nothing written: the arms and their arguments,
    the order, each arm's boot env (with a warning for each arm the order leaves on another env: boot_env_walk), the
    tunables each arm sets and the gates, after the checks the run itself makes first (a bad --set, --server-env,
    gate or order stops here the same way). against (a finished arms run's folder) evaluates each gate on that run's
    status readings, per arm and repeat, as the run would after each arm that finished alive (an arm that died there,
    died_in in its arms.jsonl row, is not evaluated). Exit 0, or 1 when a gate failed, erred or read a missing name on
    them."""
    import arms_lib

    if boot and REBOOT in order_text.split():
        raise SystemExit("--boot-hook boots every arm already: drop 'reboot' from --order")
    if catcher_ab:
        specs = ["bare=", "catcher=--catcher"] + list(specs)
    arm_list = [parse_arm(spec) for spec in specs]
    if not arm_list:
        raise SystemExit("name at least one --arm NAME=ARGS (or --catcher-ab)")
    managed = managed_tunables([args for _, args in arm_list])
    wide = wide_env(server_env, client_env)
    envs = arm_boot_envs(arm_list, wide)
    refusal = boot_hook_refusal(boot, envs) if boot else None
    if refusal:
        raise SystemExit(refusal)
    gates = [arms_lib.parse_gate(text) for text in gate_texts]
    unknown = sorted({a for g in gates for a in g["arms"] if a != "*"} - {n for n, _ in arm_list})
    if unknown:
        raise SystemExit(f"--gate names arm(s) no --arm defines: {', '.join(unknown)}")
    stops = [arms_lib.parse_stop(text, {n for n, _ in arm_list}) for text in stop_texts or []]
    order = planned_order(arm_list, repeat, order_text)
    runs = [n for _, n, _ in order if n != REBOOT]
    print(f"arms check: {len(runs)} run(s) of {', '.join(names) or 'every scenario'}"
          + (f" and {len(order) - len(runs)} planned reboot(s)" if len(order) > len(runs) else "")
          + "; nothing run, no game touched")
    for name, args in arm_list:
        print(f"   {name}: {' '.join(args) or '(no arguments)'} ({runs.count(name)} run(s))")
    text = " ".join(n for _, n, _ in order)
    print(f"   order: {text if len(text) <= 240 else text[:240] + ' ...'}")
    if clients:
        print(f"   boots: --clients {clients} (every quick_loop boot the runner makes)")
    warning = None if boot else client2_warning(names, clients)
    if warning:
        print(warning)
    if boot:
        print(f"   boot hook before each arm: {boot}")
    if any(env["server"] or env["client"] for env in envs.values()):
        print("   boot env (every boot the runner makes passes it): "
              + "; ".join(f"{name} {env_text(env)}" for name, env in envs.items()))
        for line in boot_env_walk(order, envs, wide, boot):
            print(f"   warning: {line}")
    for stop in stops:
        print(f"   stop when (after each arm, over every row so far): {stop['text']}")
    if managed:
        print(f"   tunables each arm sets (its own value or the default): {', '.join(f'{side_of(e)} {n}' for e, n in managed)}")
    failed = 0
    readings: dict[tuple[str, int], dict] = {}
    if against is not None:
        folder = arms_folder(against, read_arms_base())
        path = folder / "status.jsonl"
        if not path.exists():
            raise SystemExit(f"--check-against: no status.jsonl in {folder}")
        for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
            try:
                r = json.loads(line)
            except ValueError:
                continue
            status = readings.setdefault((r.get("arm"), r.get("repeat", 1)), {})
            status[r.get("kind")] = {side: r.get(side) for side in ("server", "client")}
        died = {(r.get("arm"), r.get("repeat", 1)) for r in (read_records(folder) if (folder / "arms.jsonl").exists() else [])
                if r.get("died_in") or (r.get("kind") is None and r.get("run") is None)}
        print(f"   against {folder.name}: {len(readings)} arm(s) with status readings")
    for gate in gates:
        print(f"   gate {gate['text']!r}: after {', '.join(gate['arms'])}; reads {', '.join(gate['names']) or 'nothing'}")
        if against is None:
            continue
        keys = [k for k in readings if arms_lib.gate_applies(gate, k[0])]
        if not keys:
            keys = list(readings)
            print(f"      ({folder.name} has none of its arms: every arm's readings)")
        tally: dict[str, int] = {}
        first_bad = None
        for arm, rep in keys:
            if (arm, rep) in died:
                tally["not evaluated (died)"] = tally.get("not evaluated (died)", 0) + 1
                continue
            result = arms_lib.check_gate(gate, {"arm": arm, "repeat": rep, "status": readings[(arm, rep)]}, "")
            missing = any(str(v).startswith("missing") for v in result["values"].values())
            kind = "missing" if missing else result["result"]
            tally[kind] = tally.get(kind, 0) + 1
            if kind != "passed" and first_bad is None:
                shown = ", ".join(f"{k}={v}" for k, v in result["values"].items())
                first_bad = f"{arm}#{rep}: {kind}" + (f" ({shown})" if shown else "") + (
                    f": {result['why']}" if result["why"] and kind != "missing" else "")
        failed += sum(v for k, v in tally.items() if k in ("failed", "error", "missing"))
        print("      " + ", ".join(f"{k} {v}" for k, v in sorted(tally.items())) + (f"; first: {first_bad}" if first_bad else ""))
    if against is not None and failed:
        print("   in a run, a failed or erring gate, or one that reads a missing name, stops it (exit 8)")
    return 1 if failed else 0


def arms_summary_again(names: list[str] | str, odds: float | None = None, exclude_texts: list[str] | None = None,
                       exclude_invalid: bool = False, hangs_as_crashes: bool = False) -> int:
    """arms --summary [FOLDER ...]: an arms run's summary printed again from its folder (arms.jsonl, the gates in
    state.json), with each crash's triage as crash_catch reads its report now; read only. Several folders are pooled:
    each row carries its run's folder as its series, the first/later split becomes series x first/later, and no gates
    are printed (each run's own --summary has them). odds: summarize_arms' --odds minutes. exclude_texts (--exclude
    ARM#N, FOLDER/ARM#N when pooled) and exclude_invalid (every row a gate failed or erred on) leave those rows and
    their gap records out of every count, minute, rate and P, and say what they left out (arms_exclusion); the gate
    lines stay as the run checked them. hangs_as_crashes: summarize_arms' --hangs-as-crashes."""
    import arms_lib

    names = [names] if isinstance(names, str) else list(names or ["latest"])
    folders = [arms_folder(name, read_arms_base()) for name in names]
    if len(set(folders)) != len(folders):
        raise SystemExit("--summary: a folder named twice")
    items = arms_lib.parse_excludes(exclude_texts or [])
    unknown = sorted({where for where, _, _ in items if where is not None} - {f.name for f in folders})
    if unknown:
        raise SystemExit(f"--exclude names folder(s) not in --summary: {', '.join(unknown)}")
    rows, gates, left_out, died = [], [], [], []
    for folder in folders:
        records = read_records(folder)
        keys = arms_exclusion(folder, records, items, exclude_invalid, pooled=len(folders) > 1)
        records, dropped = arms_lib.exclude_records(records, keys)
        left_out += [(f"{folder.name}/" if len(folders) > 1 else "") + line for line in arms_lib.excluded_lines(dropped)]
        died += [(f"{folder.name}/" if len(folders) > 1 else "") + line for line in arms_lib.died_lines(records)]
        found = arms_lib.arm_rows(records)
        rows += [{**r, "series": folder.name} for r in found] if len(folders) > 1 else found
    if items or exclude_invalid:
        print(f"excluded ({len(left_out)} row(s), with their gap records): " + ("; ".join(left_out) or "none"))
    if died:
        print("died-scenario minutes (booked to the crash): " + "; ".join(died))
    if len(folders) == 1:
        try:
            gates = json.loads((folders[0] / "state.json").read_text(encoding="utf-8")).get("gates") or []
        except (OSError, ValueError):
            gates = []
        for gate in gates:
            gate.setdefault("results", [])
    else:
        print("pooled: " + ", ".join(f"{f.name} ({sum(1 for r in rows if r['series'] == f.name)} arm row(s))"
                                     for f in folders))
    print("\n".join(summarize_arms(rows, safe_triage(lambda: report_paths(folders[0].parent.parent)), odds,
                                   hangs_as_crashes)
                    + gate_summary(gates)))
    print("arms: " + ", ".join(str(f) for f in folders))
    return 0


def runner_identity(pid: int) -> dict:
    try:
        try:
            import stack_who
        except ImportError:
            stack_who = None

        _, started = stack_who.process_info(pid)
    except Exception:
        started = None
    return {"pid": pid, "started": round(started, 1) if started else None}


def runner_alive(lock: dict) -> bool:
    """Whether the arms runner that wrote this lock still runs: its pid is up and started when the lock says (Windows
    reuses pids). A pid whose start cannot be read counts as alive only when the lock has no start either."""
    now = runner_identity(int(lock.get("pid") or 0))
    if now["started"] is None:
        return False if lock.get("started") is not None or not lock.get("pid") else _pid_up(int(lock["pid"]))
    return same_process(lock, now)


def _pid_up(pid: int) -> bool:
    try:
        try:
            import crash_catch
        except ImportError:
            crash_catch = _MirrorProcFallback

        return crash_catch.alive(pid)
    except Exception:
        return False


def write_json(path: Path, data: dict) -> None:
    temp = path.with_name(path.name + ".tmp")
    temp.write_text(json.dumps(data, indent=1) + "\n", encoding="utf-8")
    os.replace(temp, path)


def stop_arms(folder_name: str) -> int:
    """Ask a running arms run to stop at its next arm boundary (it saves its state; --resume goes on from there)."""
    folder = arms_folder(folder_name)
    lock = json.loads((folder / "runner.json").read_text(encoding="utf-8")) if (folder / "runner.json").exists() else None
    if lock and runner_alive(lock):
        (folder / "stop").write_text(time.strftime("%Y-%m-%d %H:%M:%S") + "\n", encoding="utf-8")
        print(f"{folder.name}: runner pid {lock['pid']} stops before its next arm (the current one finishes, its after "
              f"hook too); then: scenarios.py arms --resume {folder.name} [--order '...']")
        return 0
    print(f"{folder.name}: no runner is running it now, so nothing to stop (scenarios.py arms --resume {folder.name} "
          f"goes on from its next arm)")
    return 1


WAIT_EVERY = 5.0  # arms --wait: seconds between looks at the folder (an arm runs for minutes)


def gc_reader_line() -> str:
    """The stack's GC reader as an arms run starts, for an after hook that runs gc_watch.py frames: the boot's reader
    (quick_loop's boot starts one, a planned reboot or a boot hook's boot the next), so the series starts no second
    one, or that none runs yet."""
    import gc_watch

    entry = gc_watch.reader_now()
    if entry:
        return gc_watch.reader_line(entry) + "; the after hook's frames reads its rows, and each boot starts the next"
    return (f"gc watch: no reader running; a planned reboot or a boot hook's quick_loop boot starts one "
            f"({gc_watch.WATCH_ENV}), else frames reads 'not known' and a gate on it stops the run")


def arms_exclusion(folder: Path, records: list[dict], items: list[tuple[str | None, str, int]], invalid: bool,
                   pooled: bool = False) -> set[tuple[str, int]]:
    """The (arm, repeat) rows --exclude and --exclude-invalid leave out of one arms run's records: the items for this
    folder (FOLDER/ARM#N, or a bare ARM#N for a single run; each must name one of its arm rows, as logged, before
    any --as), and with invalid every row a gate failed or erred on (arms_lib.invalid_rows)."""
    import arms_lib

    have = [(r["arm"], r.get("repeat", 1)) for r in records if r.get("kind") is None and "arm" in r]
    keys: set[tuple[str, int]] = set()
    for where, arm, repeat in items:
        if where is None and pooled:
            raise SystemExit(f"--exclude {arm}#{repeat}: with several folders, name its folder (FOLDER/{arm}#{repeat})")
        if where not in (None, folder.name):
            continue
        if (arm, repeat) not in have:
            raise SystemExit(f"--exclude {arm}#{repeat}: no such arm row in {folder.name} (rows: "
                             + ", ".join(f"{a}#{r}" for a, r in have) + ")")
        keys.add((arm, repeat))
    return keys | (set(arms_lib.invalid_rows(records)) if invalid else set())


def log_records(log: Path) -> list[dict]:
    """Every record of an arms log so far, gap records included (a line cut mid-write is left out), the arm rows booked
    (arms_lib.book_minutes: rows a runner from before G16 wrote)."""
    import arms_lib

    try:
        lines = log.read_text(encoding="utf-8").splitlines()
    except OSError:
        return []
    out = []
    for line in lines:
        try:
            out.append(json.loads(line))
        except ValueError:
            continue
    return arms_lib.book_log(out, log.parent)


def stop_reason(by: dict) -> str:
    """state.json's stopped_by in one line, as the summary and arms --wait say it."""
    import arms_lib

    where = f"after {by['after'][0]} #{by['after'][1]} (row {by['row']})"
    values = arms_lib.stop_text({"fired": None, "error": None, "values": by.get("values") or {}})
    values = values.removeprefix("goes on").strip()
    if by.get("error"):
        text = f"a --stop-when could not be evaluated {where}: {by['error']}"
    else:
        text = f"--stop-when held {where}: {by['text']}" + (f" {values}" if values else "")
    return text + (" (the last arm: the run ended there anyway)" if by.get("last") else "")


def arms_stop_check(name: str, stop_texts: list[str] | None, rename_text: str = "",
                    exclude_texts: list[str] | None = None, exclude_invalid: bool = False) -> int:
    """arms --stop-check FOLDER: the run's stop conditions (--stop-when, or its plan's) over its log as the runner
    checks them, read only: the first row where one holds (exit 9) or cannot be evaluated (exit 2), or that none does
    (exit 0), with the values at that row. --as OLD=NEW,... reads arm OLD as NEW (another series' names).
    exclude_texts (--exclude ARM#N,...) and exclude_invalid (every row a gate failed or erred on) leave those rows and
    their gap records out of the counts, minutes, p and rates: it prints what it left out, the check without them
    (which sets the exit code) and the same check with every row, and each arm's counts both ways where they differ."""
    import arms_lib

    folder = arms_folder(name, read_arms_base())
    records = read_records(folder)
    excluding = bool(exclude_texts) or exclude_invalid
    keys = arms_exclusion(folder, records, arms_lib.parse_excludes(exclude_texts or []), exclude_invalid)
    every, (records, dropped) = records, arms_lib.exclude_records(records, keys)
    try:
        plan = json.loads((folder / "plan.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        plan = {}
    texts = stop_texts or plan.get("stop") or []
    if not texts:
        raise SystemExit(f"{folder.name}: no --stop-when given and its plan has no stop")
    rename = {}
    for pair in (p.strip() for p in rename_text.split(",") if p.strip()):
        old, sep, new = pair.partition("=")
        if not sep or not old.strip() or not new.strip():
            raise SystemExit(f"--as {rename_text!r}: want OLD=NEW[,OLD=NEW...]")
        rename[old.strip()] = new.strip()
    records, every = ([dict(r, arm=rename.get(r["arm"], r["arm"])) if "arm" in r else r for r in recs]
                      for recs in (records, every))
    arms = {r["arm"] for r in every if "arm" in r} | {rename.get(n, n) for n, _ in
                                                       (parse_arm(spec) for spec in plan.get("specs") or [])}
    stops = [arms_lib.parse_stop(text, arms) for text in texts]

    def replay(recs: list[dict]) -> tuple[int | None, dict, str]:
        number, check, total = arms_lib.stop_replay(stops, recs)
        if number is None:
            return number, check, f"does not fire in {total} row(s); at the end: {arms_lib.stop_text(check)}"
        row = [r for r in recs if not r.get("kind") and "arm" in r][number - 1]
        return number, check, (f"row {number} of {total} ({row['arm']}#{row.get('repeat', 1)}): "
                               f"{arms_lib.stop_text(check)}" + (" (the last row)" if number == total else
                               f"; the runner would have stopped there, {total - number} row(s) before the end"))

    total = sum(1 for r in every if not r.get("kind") and "arm" in r)
    left_out = arms_lib.excluded_lines(dropped)
    print(f"stop-check {folder.name}: {total} arm row(s)" + (f", {len(left_out)} excluded" if excluding else "")
          + (f", arms read as {rename_text}" if rename else "") + "; " + "; ".join(stop["text"] for stop in stops))
    if excluding:
        print("   excluded (with their gap records): " + ("; ".join(left_out) or "none"))
    number, check, text = replay(records)
    print(f"   {text}")
    # G16: until the read is done, the check with the runner's own minutes too, and what changed.
    died = arms_lib.died_lines(every)
    if died:
        print(f"   with completed_min (the runner's minutes): {replay(arms_lib.as_completed(records))[2]}")
        for line in died:
            print(f"   died: {line}")
        booked, completed = arms_lib.stop_counts(records), arms_lib.stop_counts(arms_lib.as_completed(records))
        for arm in booked:
            if booked[arm] != completed.get(arm):
                print(f"   {arm}: {arms_lib.count_text(booked[arm])}; with completed_min: "
                      f"{arms_lib.count_text(completed.get(arm))}")
    if excluding:
        print(f"   with every row: {replay(every)[2]}")
        with_rows, without = arms_lib.stop_counts(every), arms_lib.stop_counts(records)
        for arm in with_rows:
            if with_rows[arm] != without.get(arm):
                print(f"   {arm}: {arms_lib.count_text(without.get(arm))}; with every row: "
                      f"{arms_lib.count_text(with_rows[arm])}")
    if number is None:
        return 0
    return 2 if check["error"] else 9


def logged_arms(folder: Path) -> list[dict]:
    """The arm rows in an arms run's arms.jsonl so far (a line being written is left for the next look), booked
    (arms_lib.book_minutes)."""
    import arms_lib

    try:
        lines = (folder / "arms.jsonl").read_text(encoding="utf-8").splitlines()
    except OSError:
        return []
    rows = []
    for line in lines:
        try:
            row = json.loads(line)
        except ValueError:
            continue
        if "kind" not in row and "arm" in row:
            rows.append(row)
    return arms_lib.book_log(rows, folder)


def arm_result_line(row: dict, number: int, total: int | None = None) -> str:
    """One arm's result in one line, as arms --wait prints it: its row number (for --seen), the run's verdicts,
    scenario minutes, crashes, where it died, each gate's result, the after hook's values (after_values) and C's GC
    gate counters (arms_lib.gc_gate_fields) and its --stop-when check."""
    verdicts = "".join(verdict_code(row, name) for name in row.get("verdicts") or {})
    crashes = row.get("crashes") or []
    text = (f"{row['arm']}#{row.get('repeat', 1)} (row {number}" + (f" of {total}" if total else "")
            + f") {row.get('started', '?')}: run {row.get('run')}: {verdicts or 'no verdicts'}, "
              f"{row.get('scenario_min', 0)} scenario-min, {len(crashes)} crash(es)"
            + (f" ({'; '.join(crashes)})" if crashes else "")
            + (f", {len(row['hangs'])} hang(s)" if row.get("hangs") else "")
            + (f", died in {row['died_in']}" if row.get("died_in") else ""))
    for gate in row.get("gates") or []:
        values = ", ".join(f"{k}={v}" for k, v in (gate.get("values") or {}).items())
        text += f"; gate {gate.get('result')}" + (f" ({values})" if values else "")
    if row.get("hook"):
        text += "; hook " + " ".join(f"{k}={v}" for k, v in row["hook"].items())
    if row.get("gc_gate") or row.get("stop_check"):
        import arms_lib

        if row.get("gc_gate"):
            text += "; gcGate " + arms_lib.gc_gate_text(row["gc_gate"])
        if row.get("stop_check"):
            text += "; stop-when " + arms_lib.stop_text(row["stop_check"])
    return text


def run_end(folder: Path, alive: Callable[[dict], bool] = None) -> str | None:
    """Why an arms run is over, or None while its runner still runs: runner.json gone (the runner removes it as it
    ends), or its runner no longer running (killed)."""
    alive = alive or runner_alive
    try:
        lock = json.loads((folder / "runner.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        lock = None
    if lock and alive(lock):
        return None
    try:
        state = json.loads((folder / "state.json").read_text(encoding="utf-8"))
        plan = json.loads((folder / "plan.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return "no runner and no state.json (not an arms run, or one from before --resume)"
    items = len(plan.get("order") or [])
    how = "finished" if state.get("finished") else f"stopped before item {state.get('next')} of {items} (--resume goes on)"
    if state.get("stopped_by"):
        how += "; " + stop_reason(state["stopped_by"])
    return how + ("" if lock is None else f"; runner pid {lock.get('pid')} no longer runs and left runner.json (killed?)")


def arms_wait(name: str, seen: int | None = None, every: float = WAIT_EVERY, sleep: Callable[[float], None] = time.sleep,
              alive: Callable[[dict], bool] | None = None, timeout: float | None = None,
              now: Callable[[], float] = time.time) -> int:
    """arms --wait FOLDER: wait for an arms run's next arm row, reading only its folder (no game, nothing written).
    Exit 0 with each new row's one-line result (arm_result_line; a row is logged after the arm's after hook and
    gates), or 9 once the run has ended, with how. seen: the rows already seen (--seen N; default the rows there now),
    so a monitor re-arms with the last row number it printed and misses none. One call returns at each arm, well
    inside a Monitor's 30-minute end (pb-048). timeout (scenarios.py wait): exit 3 with the run's status line after
    that many seconds, so one shell call stays inside the Bash tool's 10 minutes."""
    folder = arms_folder(name, read_arms_base())
    seen = len(logged_arms(folder)) if seen is None else seen
    deadline = None if timeout is None else now() + timeout
    while True:
        ended = run_end(folder, alive)  # before the rows: a row logged just before the end is printed first
        rows = logged_arms(folder)
        if len(rows) > seen:
            try:
                plan = json.loads((folder / "plan.json").read_text(encoding="utf-8"))
                total = sum(1 for _, n, _ in plan.get("order") or [] if n != REBOOT) or None
            except (OSError, ValueError):
                total = None
            for number, row in enumerate(rows[seen:], seen + 1):
                print(arm_result_line(row, number, total), flush=True)
            return 0
        if ended:
            print(f"{folder.name}: the run has ended: {ended}; {len(rows)} arm row(s); scenarios.py arms --summary "
                  f"{folder.name}", flush=True)
            return 9
        if deadline is not None and now() >= deadline:
            print(arms_status_line(folder, now(), alive) + f"; still running after {timeout:.0f} s (wait again)",
                  flush=True)
            return 3
        sleep(every)


# events.txt: "HH:MM:SS == [HH:MM:SS ]NAME #R: boot hook | run ... | not run ..." starts each arm's part; "   run ID:"
# is the run's result line
ARM_HEADER = re.compile(r"^(\d\d:\d\d:\d\d) == (?:\d\d:\d\d:\d\d )?(\S+) #(\d+): (.*)$")
RUN_RESULT = re.compile(r"^(\d\d:\d\d:\d\d)    run \S+: ")


def clock_seconds(text: str) -> int:
    hours, minutes, seconds = (int(part) for part in text.split(":"))
    return hours * 3600 + minutes * 60 + seconds


def since_clock(text: str, now: float) -> float:
    """Seconds from an events.txt time (local HH:MM:SS, no date) to now, across midnight."""
    local = time.localtime(now)
    return (local.tm_hour * 3600 + local.tm_min * 60 + local.tm_sec - clock_seconds(text)) % 86400


def run_phases(lines: list[str]) -> list[dict]:
    """Each arm's run in events.txt, in order: arm, repeat, began (its run line's time) and ended (its "   run ID:"
    line's, None while it runs, or when it never printed one)."""
    runs: list[dict] = []
    current = None
    for line in lines:
        m = ARM_HEADER.match(line)
        if m:
            current = None
            if m.group(4).startswith("run "):
                current = {"arm": m.group(2), "repeat": int(m.group(3)), "began": m.group(1), "ended": None}
                runs.append(current)
            continue
        m = RUN_RESULT.match(line)
        if m and current is not None and current["ended"] is None:
            current["ended"] = m.group(1)
    return runs


def run_phase(folder: Path, now: float, typical: float | None = None) -> dict:
    """Where an arms run's current item is, from its live state.json (the item it is on: plan.json's order[next]) and
    that item's newest event in events.txt, never another item's: phase "run" only when the newest arm line of
    events.txt is this item's run line and no result line follows it. With the run's elapsed seconds, its typical
    length (typical, else the median of this arm's finished runs in this arms run, else of every arm's) and the
    seconds left by it."""
    try:
        state = json.loads((folder / "state.json").read_text(encoding="utf-8"))
        order = json.loads((folder / "plan.json").read_text(encoding="utf-8")).get("order") or []
    except (OSError, ValueError):
        return {"phase": "unknown", "why": "no state.json or plan.json yet"}
    index = state.get("next")
    if state.get("finished") or not isinstance(index, int) or not 0 <= index < len(order):
        return {"phase": "unknown", "why": f"state.json names no item (next {index}, {len(order)} in the order)"}
    repeat, name = order[index][0], order[index][1]
    item = f"{name}#{repeat}"
    try:
        lines = (folder / "events.txt").read_text(encoding="utf-8", errors="replace").splitlines()
    except OSError:
        lines = []
    runs = run_phases(lines)
    lengths = [(r["arm"].split(":", 1)[0], (clock_seconds(r["ended"]) - clock_seconds(r["began"])) % 86400)
               for r in runs if r["ended"]]
    same = [length for arm, length in lengths if arm == name]
    pool = same or [length for _, length in lengths]
    typical_s = typical if typical is not None else _median(pool)
    out = {"item": item, "index": index, "typical_s": typical_s, "typical_from": "--typical" if typical is not None
           else f"the median of {len(pool)} finished run(s) of {name if same else 'every arm'}" if pool else None}
    if name == REBOOT:
        return {**out, "phase": "reboot"}
    header = next((m for m in (ARM_HEADER.match(line) for line in reversed(lines)) if m), None)
    mine = header is not None and header.group(2).split(":", 1)[0] == name and int(header.group(3)) == repeat
    if not mine:
        return {**out, "phase": "before"}
    newest = runs[-1] if runs else None
    if not header.group(4).startswith("run ") or newest is None or newest["ended"] is not None:
        what = header.group(4)
        return {**out, "phase": "after" if what.startswith("run ") else "boot" if what.startswith("boot hook")
                else "skipped" if what.startswith("not run") else what.split(" ", 1)[0]}
    elapsed = since_clock(newest["began"], now)
    return {**out, "phase": "run", "began": newest["began"], "elapsed_s": elapsed,
            "left_s": None if typical_s is None else typical_s - elapsed}


def arms_wait_run(name: str, min_left: float = 0.0, typical: float | None = None, every: float = WAIT_EVERY,
                  sleep: Callable[[float], None] = time.sleep, now: Callable[[], float] = time.time,
                  alive: Callable[[dict], bool] | None = None,
                  writing: Callable[[Path], list[str]] | None = None) -> int:
    """arms --wait-run FOLDER [--min-left S] [--typical S]: wait until the run's current arm is in its run phase
    (run_phase: the live state.json's item and its own newest event), with at least min_left seconds left by its typical
    run length, so another session can time a short build inside an arm's run and keep load off the boots. Read only.
    Exit 0 with one line, or 9 once the run has ended; while it waits, one line each time the phase changes. With
    min_left and no finished run to time it by (and no --typical), a run phase counts only once it has a typical.
    It also holds while the catcher writes a dump (writing: crash_catch.writing_dumps on the dumps folder of the same
    .omo/run), so a build never competes with a full dump (about 1.4 GB, the game suspended) for the disk."""
    folder = arms_folder(name, read_arms_base())
    dumps = folder.parent.parent / "dumps"  # crash_catch's folder beside arms/ (SIMSONLINE_RUN_DIR's when that is set)
    if writing is None:
        try:
            import crash_catch
        except ImportError:
            crash_catch = _MirrorProcFallback

        writing = crash_catch.writing_dumps
    said = None
    while True:
        ended = run_end(folder, alive)
        if ended:
            print(f"{folder.name}: the run has ended: {ended}", flush=True)
            return 9
        at = run_phase(folder, now(), typical)
        typical_text = (f"; typical run {at['typical_s'] / 60:.1f} min ({at['typical_from']})"
                        if at.get("typical_s") is not None else "; no finished run to time it by yet")
        if at["phase"] == "run":
            left = at["left_s"]
            enough = min_left <= 0 or (left is not None and left >= min_left)
            busy = writing(dumps) if enough else []
            if enough and not busy:
                print(f"{folder.name}: {at['item']} is in its run since {at['began']} ({at['elapsed_s'] / 60:.1f} min)"
                      + typical_text + (f": about {left / 60:.1f} min left" if left is not None else ""), flush=True)
                return 0
            if busy:
                note = (f"{at['item']} is in its run, but the catcher is writing a dump ({', '.join(busy)}): waiting "
                        "until it is written")
            else:
                note = (f"{at['item']} is in its run, but "
                        + (f"about {max(left, 0) / 60:.1f} min left" if left is not None else "its length is not known yet")
                        + f" (under {min_left / 60:.1f} min): waiting for the next arm's run")
        else:
            note = {"unknown": at.get("why"), "reboot": f"{at.get('item')} is a planned reboot",
                    "before": f"{at.get('item')} has not started (no event of its own in events.txt yet)",
                    "boot": f"{at.get('item')} is in its boot hook",
                    "after": f"{at.get('item')}'s run has ended (its after hook, gates and row)",
                    "skipped": f"{at.get('item')} was not run"}.get(at["phase"], f"{at.get('item')}: {at['phase']}")
        if note != said:
            print(f"{folder.name}: {note}{typical_text if at['phase'] != 'unknown' else ''}", flush=True)
            said = note
        sleep(every)


PROGRESS = "progress.json"  # a suite run's live state: its runner, the planned scenarios, the one on now (status, wait)
WAIT_TIMEOUT = 540.0  # scenarios.py wait: seconds before it gives up with exit 3, inside the Bash tool's 10 minutes
WAIT_UNTIL = ("scenario", "end")


def suite_base() -> Path:
    """Where status and wait look a suite run up: SIMSONLINE_RUN_DIR's scenarios folder when that is set (the live
    worktree's .omo/run, read from another), else this checkout's, as read_arms_base does for arms runs."""
    run_dir = os.environ.get("SIMSONLINE_RUN_DIR")
    return Path(run_dir) / "scenarios" if run_dir else RUNS


def suite_folders(base: Path) -> list[Path]:
    """Every suite run folder under base, oldest first: one with a progress.json (a run since status) or a report."""
    if not base.exists():
        return []
    return sorted(d for d in base.iterdir() if d.is_dir() and ((d / PROGRESS).exists() or (d / "report.md").exists()))


def suite_folder(name: str, base: Path | None = None) -> Path:
    """A suite run's folder: latest (the newest), a folder name under base, or a path."""
    base = base or suite_base()
    folders = suite_folders(base)
    if name in ("", "latest"):
        if not folders:
            raise SystemExit(f"no suite run in {base}")
        return folders[-1]
    path = Path(name)
    if path.is_dir():
        return path
    if (base / name).is_dir():
        return base / name
    raise SystemExit(f"no suite run {name} in {base} (have: {', '.join(f.name for f in folders[-5:]) or 'none'})")


def suite_state(folder: Path, alive: Callable[[dict], bool] | None = None) -> dict:
    """A suite run's state from its folder alone: the records so far (records.json), its progress.json, and why it is
    over (finished, or its runner no longer runs), None while it runs."""
    alive = alive or runner_alive
    try:
        progress = json.loads((folder / PROGRESS).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        progress = None
    try:
        records = json.loads((folder / "records.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        records = []
    planned = (progress or {}).get("planned") or []
    runner = (progress or {}).get("runner") or {}
    if progress is None:
        ended = "no progress.json (a run from before scenarios.py status)"
    elif progress.get("finished") is not None:
        ended = f"finished, exit {progress['finished']}"
    elif not runner.get("pid") or not alive(runner):
        ended = (f"its runner (pid {runner.get('pid')}) no longer runs, {len(records)} of {len(planned)} scenario(s) "
                 "done (killed?)")
    else:
        ended = None
    return {"records": records, "progress": progress or {}, "planned": planned, "ended": ended}


def typical_seconds(name: str, folder: Path, last: int = 20) -> tuple[float | None, int]:
    """A scenario's typical length: the median of its records' total seconds in the last runs before this one, and
    how many there were."""
    totals = []
    for run in [r for r in suite_folders(folder.parent) if r.name < folder.name][-last:]:
        try:
            records = load_run(run)
        except (OSError, ValueError):
            continue
        totals += [(r.get("timings") or {}).get("total") for r in records if record_key(r) == name]
    totals = [t for t in totals if t]
    return _median(totals), len(totals)


def suite_status_line(folder: Path, now: float, alive: Callable[[dict], bool] | None = None,
                      state: dict | None = None) -> str:
    """A suite run in one line: done of planned with their verdicts, then the scenario on now (since when, and its
    typical length) or why the run is over."""
    state = state or suite_state(folder, alive)
    records, planned = state["records"], state["planned"]
    verdicts = "".join(CODES.get(r.get("verdict"), "?") for r in records)
    head = f"run {folder.name}{' NO-FORK' if run_nofork(folder) else ''}: {len(records)} of {len(planned) or '?'} done" + (f" ({verdicts})" if verdicts else "")
    if state["ended"]:
        return f"{head}; ended: {state['ended']}"
    current = state["progress"].get("current")
    if not current:
        return f"{head}; between scenarios"
    text = (f"{head}; on {current['name']} ({current['index'] + 1} of {len(planned)}) since {current['began']}, "
            f"{(now - current['at']) / 60:.1f} min")
    typical, count = typical_seconds(current["name"], folder)
    return text + (f", typical {typical / 60:.1f} min (median of {count} earlier)" if typical else "")


def arms_status_line(folder: Path, now: float, alive: Callable[[dict], bool] | None = None) -> str:
    """An arms run in one line: its rows so far, then the item it is on and that item's phase (run_phase), or how it
    ended."""
    rows = logged_arms(folder)
    ended = run_end(folder, alive)
    if ended:
        return f"arms {folder.name}: {len(rows)} arm row(s); ended: {ended}"
    at = run_phase(folder, now)
    try:
        items = len(json.loads((folder / "plan.json").read_text(encoding="utf-8")).get("order") or [])
    except (OSError, ValueError):
        items = 0
    if at["phase"] == "unknown":
        return f"arms {folder.name}: {len(rows)} arm row(s); {at.get('why')}"
    text = f"arms {folder.name}: {len(rows)} arm row(s); on {at['item']} (item {at['index'] + 1} of {items}), {at['phase']}"
    if at["phase"] == "run":
        text += f" since {at['began']}, {at['elapsed_s'] / 60:.1f} min"
        if at.get("left_s") is not None:
            text += f", about {max(at['left_s'], 0) / 60:.1f} min left ({at['typical_from']})"
    return text


def is_arms_name(name: str) -> bool:
    """Whether a status or wait argument names an arms run (an arms folder name or path) rather than a suite run."""
    path = Path(name)
    if name not in ("", "latest") and (path / "plan.json").exists():
        return True
    return name not in ("", "latest") and (read_arms_base() / name).is_dir()


def status(name: str = "latest", now: Callable[[], float] = time.time,
           alive: Callable[[dict], bool] | None = None) -> int:
    """scenarios.py status [RUN]: where a run is, from its folder alone (no game, nothing written): a suite run's
    done, verdicts and the scenario on now with its typical length; an arms run's rows, item and phase. With no RUN,
    the newest arms run while it runs, then the newest suite run (an arms run's own suite run is that one)."""
    if is_arms_name(name):
        print(arms_status_line(arms_folder(name, read_arms_base()), now(), alive))
        return 0
    if name in ("", "latest"):
        base = read_arms_base()
        folders = sorted(f for f in base.iterdir() if f.is_dir()) if base.exists() else []
        if folders and run_end(folders[-1], alive) is None:
            print(arms_status_line(folders[-1], now(), alive))
        if not suite_folders(suite_base()):
            print(f"no suite run in {suite_base()}")
            return 0
    print(suite_status_line(suite_folder(name), now(), alive))
    return 0


def verdict_line(record: dict, number: int, planned: int) -> str:
    """One scenario's result as wait prints it: its number in the run, verdict, length and failures."""
    total = (record.get("timings") or {}).get("total")
    return (f"{record_key(record)} ({number} of {planned or '?'}): {record.get('verdict')}"
            + (f" in {total:.0f}s" if total else "") + (f": {'; '.join(record['failures'])}" if record.get("failures") else ""))


def wait(name: str = "latest", until: str = "scenario", seen: int | None = None, timeout: float = WAIT_TIMEOUT,
         every: float = WAIT_EVERY, sleep: Callable[[float], None] = time.sleep, now: Callable[[], float] = time.time,
         alive: Callable[[dict], bool] | None = None) -> int:
    """scenarios.py wait [RUN] [--until scenario|end]: one call in place of a sleep-and-tail loop, reading only the
    run's folder. until scenario: exit 0 with each new verdict (an arms run: each new arm row, arms --wait); until end:
    only once the run is over. Exit 9 once the run has ended (with its status line), 3 at the timeout (still running:
    call again; --seen N carries the verdicts already printed)."""
    if until not in WAIT_UNTIL:
        raise SystemExit(f"--until {until}: one of {', '.join(WAIT_UNTIL)}")
    if is_arms_name(name) or (name in ("", "latest") and _arms_running(alive)):
        start = now()
        while True:
            code = arms_wait(name, seen, every, sleep, alive, max(timeout - (now() - start), 0.0), now)
            if code != 0 or until == "scenario":
                return code
            seen = None  # until end: the rows just printed are seen; wait for the next
    folder = suite_folder(name)
    seen = len(suite_state(folder, alive)["records"]) if seen is None else seen
    deadline = now() + timeout
    while True:
        state = suite_state(folder, alive)  # the end before the records: a verdict written just before it prints first
        records = state["records"]
        if until == "scenario" and len(records) > seen:
            for number, record in enumerate(records[seen:], seen + 1):
                print(verdict_line(record, number, len(state["planned"])), flush=True)
            return 0
        if state["ended"]:
            print(suite_status_line(folder, now(), alive, state), flush=True)
            return 9
        if now() >= deadline:
            print(suite_status_line(folder, now(), alive, state) + f"; still running after {timeout:.0f} s (wait again)",
                  flush=True)
            return 3
        sleep(every)


def _arms_running(alive: Callable[[dict], bool] | None = None) -> bool:
    """Whether the newest arms run's runner still runs (wait with no RUN then follows it, not its current suite run)."""
    base = read_arms_base()
    folders = sorted(f for f in base.iterdir() if f.is_dir()) if base.exists() else []
    return bool(folders) and run_end(folders[-1], alive) is None


def resume_arms(folder: Path) -> tuple[dict, dict, list[dict]]:
    """An arms run's plan, state and logged rows, to go on from its next arm. Refuses while its runner still runs."""
    for name in ("plan.json", "state.json"):
        if not (folder / name).exists():
            raise SystemExit(f"{folder}: no {name} (not an arms run, or one from before --resume)")
    if (folder / "runner.json").exists():
        lock = json.loads((folder / "runner.json").read_text(encoding="utf-8"))
        if runner_alive(lock):
            raise SystemExit(f"{folder.name}: runner pid {lock['pid']} still runs it: scenarios.py arms --stop "
                             f"{folder.name}, then --resume once it has stopped")
    plan = json.loads((folder / "plan.json").read_text(encoding="utf-8"))
    state = json.loads((folder / "state.json").read_text(encoding="utf-8"))
    log = folder / "arms.jsonl"
    records = [json.loads(line) for line in log.read_text(encoding="utf-8").splitlines() if line.strip()] if log.exists() else []
    import arms_lib

    return plan, state, arms_lib.book_log([r for r in records if "kind" not in r and "arm" in r], folder)


# The lines of arms' own output a monitor watches: each item's start, a reboot, the boot hook, a run's result and its
# crashes, the after hook, a process change, the status and gate lines, a stop, the tunables put back, and the arms:
# lines (the start, a resume, the folder at the end).
EVENT_LINE = re.compile(r"(== |arms: |   run |   crash\(es\) |   a game died|   TS3 process changed|   status: |"
                        r"   gate |   boot: |   after|   before: |   warning: |   tunables back)")


class EventTee:
    """arms' stdout while it runs: every line goes on to the console as before, and each event line (EVENT_LINE) is
    also appended, with the time, to the run folder's events.txt, so a monitor follows that one file (tail -n 0 -F;
    a re-armed one picks up where the last stopped by the times) with no line-number anchor in a longer log. A resume
    appends to the same file; arms_events writes the end."""

    def __init__(self, path: Path, real) -> None:
        self.path, self.real, self.partial, self.lock = path, real, "", threading.Lock()

    def write(self, text: str) -> int:
        self.real.write(text)
        with self.lock:
            lines = (self.partial + text).split("\n")
            self.partial = lines.pop()
            for line in lines:
                if EVENT_LINE.match(line):
                    self.event(line)
        return len(text)

    def event(self, line: str) -> None:
        try:
            with self.path.open("a", encoding="utf-8") as f:
                f.write(f"{time.strftime('%H:%M:%S')} {line}\n")
        except OSError:
            pass  # the console has the line; a full disk must not stop the run

    def flush(self) -> None:
        self.real.flush()

    def __getattr__(self, name: str):
        return getattr(self.real, name)


def arms_events(run: Callable[..., int]) -> Callable[..., int]:
    """arms' wrapper: the run's end in its events.txt ("arms: exit N", or what stopped it), and stdout put back."""

    @functools.wraps(run)
    def wrapper(*args, **kwargs) -> int:
        real = sys.stdout
        code = None
        try:
            code = run(*args, **kwargs)
            return code
        except BaseException as ex:
            if isinstance(sys.stdout, EventTee):
                sys.stdout.event(f"arms: stopped by {type(ex).__name__}" + (f": {ex}" if str(ex) else ""))
            raise
        finally:
            if isinstance(sys.stdout, EventTee):
                if code is not None:
                    sys.stdout.event(f"arms: exit {code}")
                sys.stdout = real

    return wrapper


@arms_events
def arms(names: list[str], specs: list[str], repeat: int, before: str, after: str, reboot: bool, catcher_ab: bool,
         min_free: float = MIN_FREE_GB, max_dumps: int = MAX_DUMPS, bare_when_capped: bool = False,
         settle: float = 0.0, order_text: str = "", status: bool = False, status_every: float = 0.0,
         gate_texts: list[str] | None = None, resume_from: Path | None = None, boot: str = "",
         rerun_blocked: bool = False, stop_texts: list[str] | None = None,
         server_env: list[str] | None = None, client_env: list[str] | None = None, clients: int | None = None) -> int:
    """Run the suite once per arm and repeat, interleaved, each arm a `scenarios.py run` with its own arguments; boot
    again when a game is dead (--reboot); log each arm's run, verdicts, scenario minutes and crashes to arms.jsonl.
    An arm's --set tunables are the runner's: set once, before the arm's before hook, and held through its run and its
    after hook (a pull reads them); every arm sets every tunable any arm sets, to its own value or the default, and the
    end puts them all back to the defaults on each game separately. A catcher arm attaches only with min_free GB free
    and fewer than max_dumps crashes dumped in this arms run; else it is skipped (or, bare_when_capped, runs without the
    catcher as arm NAME:bare, so the catcher's side of an A/B never counts it). Each crash is put in its phase: setup
    (the sets, the settle wait, the before hook), run, after (the after hook), or gap (after the arm, up to the next
    arm's setup, a reboot included), which counts against the arm before, as the crash chase counted. Each
    row says, per game, what its TS3 process ran before the arm (arms_before, had: the tunables it had on), reset by
    a reboot or a restart; order_text runs the arms in that order instead, "reboot" being a planned reboot. status
    reads both games' SACS_RECORDER status before an arm's sets, after its run and after its after hook (the calls
    boot 5's arms made), status_every also on a timer during the arm (ArmStatus); both are off unless asked for, so
    the default makes no status calls at all. Each gate_texts item ("[once] ARMS: EXPR", arms_lib.parse_gate) is
    checked after each named arm that finished alive, on its status readings (a gate turns status on) and the after
    hook's full output (after-ARM-REPEAT.txt); a false gate, or one that reads a missing name, stops the run (exit 8).
    The folder keeps plan.json (the arguments and the whole order), state.json (written before each item: the next
    one, the process history, the gates, the last arm and when it ended), events.txt (the event lines, each with its
    time, and the end: EventTee) and runner.json while it runs; a "stop" file
    there (arms --stop) ends the run before its next item. resume_from goes on from such a folder's next item, with
    its arguments and carried state: order_text, if given, replaces what was left (the job of Session C's handoff6.py,
    boot 5); a crash between the two runners is the last arm's gap, as within one run. boot (--boot-hook CMD) boots
    every arm instead of the reboot-when-dead: after the last arm's gap is taken and before the arm's sets and process
    snapshot, so the arm keeps both (saves.py arm, each arm on its own save); its crashes are the arm's crashes_boot,
    and a hook that exits non-zero, or a game down after it, stops the run (exit 6). With it, a planned "reboot" in
    the order is refused (every arm boots already). Hooks get SIMSONLINE_ARMS_HOOK=boot, before or after. With
    rerun_blocked, an arm whose every scenario did not run (BLOCKED or RESET-FAILED, all_blocked) with no exposure runs
    again once, right after
    it, behind a planned reboot (none with a boot hook, which boots it anyway): the order and plan.json get both items,
    and state.json the arms already run again, so --resume keeps to once. stop_texts (--stop-when, kept in plan.json
    as "stop"; given with --resume, they replace the plan's) are checked after each arm's row, over the log so far
    (arms_lib.stop_check): when one holds, or cannot be evaluated, the run stops there with state saved (exit 9, as
    --stop) and state.json's stopped_by and summary.txt say why; each row keeps its check as stop_check.
    server_env and client_env (--server-env, --client-env K=V items) are the games' boot env for every arm, an arm's
    own --server-env/--client-env items in its ARGS over them (never in its run's arguments): every boot the runner
    makes passes the arm's to quick_loop.py run --boot (a planned reboot the next arm's, the run's own when none
    follows; a reboot-when-dead the arm's own), and a boot hook gets it through {server_env} and {client_env} (a hook
    that names no placeholder for a side some arm has an env for is refused). Not the runner's own shell: both games
    would inherit that (quick_loop's stack_env). plan.json keeps them as server_env and client_env ({"*": the run's,
    ARM: the arm's}), state.json the last boot the runner made (booted: by, its env, the processes after it), and each
    row its boot_env and booted_env (the env its games run on; a side's None when no boot of this run started that
    process: the stack as found, a restarted game), with a warning and boot_env_off for the sides that differ."""
    resumed = None
    if resume_from is not None:
        resumed = resume_arms(Path(resume_from))
        plan = resumed[0]
        names, specs, repeat, before, after = plan["names"], plan["specs"], plan["repeat"], plan["before"], plan["after"]
        reboot, min_free, max_dumps, bare_when_capped = plan["reboot"], plan["min_free"], plan["max_dumps"], plan["bare_when_capped"]
        settle, status, status_every, gate_texts = plan["settle"], plan["status"], plan["status_every"], plan["gates"]
        catcher_ab = plan["catcher_ab"]
        boot = plan.get("boot", "")  # a plan from before --boot-hook has none
        clients = plan.get("clients")  # a plan from before arms --clients has none: quick_loop's default
        rerun_blocked = bool(plan.get("rerun_blocked"))
        if stop_texts is None:
            stop_texts = plan.get("stop") or []
    if boot and REBOOT in order_text.split():
        raise SystemExit("--boot-hook boots every arm already: drop 'reboot' from --order")
    if catcher_ab and resumed is None:  # a plan keeps the specs with these already in
        specs = ["bare=", "catcher=--catcher"] + list(specs)
    arm_list = [parse_arm(spec) for spec in specs]
    if not arm_list:
        raise SystemExit("name at least one --arm NAME=ARGS (or --catcher-ab)")
    managed = managed_tunables([args for _, args in arm_list])  # checks every --set before any game is touched
    wide = ({side: dict((plan.get(f"{side}_env") or {}).get("*") or {}) for side in ("server", "client")} if resumed
            else wide_env(server_env, client_env))  # a plan keeps the run's own as server_env["*"], client_env["*"]
    envs = arm_boot_envs(arm_list, wide)  # a bad --server-env stops here too, before any game
    uses_env = any(env["server"] or env["client"] for env in envs.values())
    refusal = boot_hook_refusal(boot, envs) if boot else None
    if refusal:
        raise SystemExit(refusal)
    import arms_lib

    gates = [arms_lib.parse_gate(text) for text in gate_texts or []]  # a bad gate fails here too, before any game
    unknown = sorted({a for g in gates for a in g["arms"] if a != "*"} - {n for n, _ in arm_list})
    if unknown:
        raise SystemExit(f"--gate names arm(s) no --arm defines: {', '.join(unknown)}")
    stops = [arms_lib.parse_stop(text, {n for n, _ in arm_list}) for text in stop_texts or []]  # before any game too
    stopped_by: dict | None = None  # the --stop-when that stopped the run, for state.json and the summary
    history: dict[str, dict] = {}  # per game: its TS3 process, the arms it ran, the tunables it had on
    previous: dict | None = None  # the last arm that ran: a gap crash counts against it
    booted: dict | None = None  # the last boot the runner made (runner_boot): its env, the games' processes after it
    reran: list[list] = []  # the [arm, repeat] items --rerun-blocked ran again (each once)
    first = 0
    if resumed is None:
        out = ARMS / time.strftime("%Y%m%d-%H%M%S")
        out.mkdir(parents=True, exist_ok=True)
        rows: list[dict] = []
        order = planned_order(arm_list, repeat, order_text)
        began = last_end = time.time()
    else:
        out, (plan, state, rows) = Path(resume_from), resumed
        order = [(r, n, list(a)) for r, n, a in plan["order"]]
        first = int(state["next"])
        if order_text:  # the rest replaced; repeats count on from the arms already run (copy #3 after copy #1, #2)
            done: dict[str, int] = {}
            for _, n, _ in order[:first]:
                done[n] = done.get(n, 0) + (n != REBOOT)
            order = order[:first] + [(r + done.get(n, 0) if n != REBOOT else r, n, a)
                                     for r, n, a in planned_order(arm_list, 1, order_text)]
        elif first >= len(order):
            raise SystemExit(f"{out.name}: every arm has run; give --order to run more")
        began, last_end, history = state["began"], state["last_end"], state.get("history") or {}
        reran = state.get("reran") or []
        booted = state.get("booted")
        previous = next((r for r in reversed(rows) if (r["arm"], r.get("repeat")) == tuple(state.get("previous") or ())), None)
        for gate in gates:
            saved = next((g for g in state.get("gates") or [] if g["text"] == gate["text"]), None)
            if saved:
                gate["done"], gate["results"] = saved["done"], saved["results"]
        (out / "stop").unlink(missing_ok=True)
    sys.stdout = EventTee(out / "events.txt", sys.stdout)  # arms_events puts it back
    if resumed is not None:
        print(f"arms: resuming {out.name} at item {first + 1} of {len(order)}"
              + (" with a new order for the rest" if order_text else "") + "; the TS3 history carries only for a game "
              "whose process has not changed", flush=True)
    log = out / "arms.jsonl"

    def write_plan() -> None:
        write_json(out / "plan.json", {"names": names, "specs": specs, "repeat": repeat, "before": before, "after": after,
                                       "reboot": reboot, "catcher_ab": catcher_ab, "min_free": min_free,
                                       "max_dumps": max_dumps, "bare_when_capped": bare_when_capped, "settle": settle,
                                       "status": status, "status_every": status_every, "gates": list(gate_texts or []),
                                       "boot": boot, "rerun_blocked": rerun_blocked,
                                       "server_env": {"*": wide["server"], **{n: e["server"] for n, e in envs.items()}},
                                       "client_env": {"*": wide["client"], **{n: e["client"] for n, e in envs.items()}},
                                       "stop": [stop["text"] for stop in stops], "order": order, "clients": clients})

    write_plan()
    write_json(out / "runner.json", runner_identity(os.getpid()))

    def save_state(index: int, finished: bool = False) -> None:
        write_json(out / "state.json", {
            "next": index, "finished": finished, "began": began, "last_end": last_end,
            "previous": [previous["arm"], previous.get("repeat")] if previous else None, "history": history,
            "gates": [{"text": g["text"], "done": g["done"], "results": g["results"]} for g in gates],
            "reran": reran, "stopped_by": stopped_by, "booted": booted, "updated": time.strftime("%Y-%m-%d %H:%M:%S")})

    print(f"arms: {sum(1 for _, n, _ in order[first:] if n != REBOOT)} run(s) of {', '.join(names) or 'every scenario'}; "
          f"log {log}", flush=True)
    if stops:
        print("   --stop-when, after each arm over every row so far (exit 9 when one holds): "
              + "; ".join(stop["text"] for stop in stops), flush=True)
    if rerun_blocked:
        print("   --rerun-blocked: an arm whose every scenario did not run (BLOCKED or RESET-FAILED, no exposure)"
              " runs again once, right after it"
              + ("" if boot else ", behind a planned reboot"), flush=True)
    if uses_env:
        print("   boot env, passed to every boot the runner makes (a planned reboot boots the next arm's, a boot hook or "
              "a reboot-when-dead the arm's own): " + "; ".join(f"{n} {env_text(e)}" for n, e in envs.items()), flush=True)
    if managed:
        print(f"   tunables each arm sets (its own value or the default): "
              f"{', '.join(f'{side_of(e)} {n}' for e, n in managed)}", flush=True)
    status = status or status_every > 0 or bool(gates)
    if status:
        print("   status: both games' SACS_RECORDER status before the sets, after the run and after the after hook"
              + (f", and every {status_every:g} s during the arm" if status_every > 0 else "")
              + f"; {out / 'status.jsonl'}", flush=True)
    if "gc_watch" in (after or ""):
        print("   " + gc_reader_line(), flush=True)  # the after hook's frames reads the stack reader's rows
    if any(is_catcher(args) for _, args in arm_list):
        print(f"   catcher arms attach only with {min_free:g} GB free (now {free_gb():.1f} GB) and fewer than {max_dumps} "
              f"crash(es) dumped in this arms run; otherwise {'they run bare' if bare_when_capped else 'they are skipped'}",
              flush=True)
    stopped = None
    reading: ArmStatus | None = None  # the arm's status readings, stopped in the finally when an arm breaks off
    try:
        index = first - 1
        while index + 1 < len(order):  # a while: --rerun-blocked puts items in after this one
            index += 1
            repeat_no, name, args = order[index]
            save_state(index)  # before the item: a runner killed during it runs it again on --resume
            if (out / "stop").exists():
                print(f"== stopping before {name} #{repeat_no}, as asked (arms --stop); go on with: "
                      f"scenarios.py arms --resume {out.name} [--order '...']", flush=True)
                stopped = "asked"
                (out / "stop").unlink(missing_ok=True)  # honoured: a later resume must not trip on it
                break
            if name == REBOOT:
                # a planned reboot: its crashes fall in the gap after the last arm (the next arm's gap check finds them)
                env = next((envs[n] for _, n, _ in order[index + 1:] if n != REBOOT), wide)  # the next arm's
                print(f"== {time.strftime('%H:%M:%S')} planned reboot"
                      + (f", boot env {env_text(env)}" if uses_env else ""), flush=True)
                code, text = python("scripts/quick_loop.py", "run", "--boot", *boot_clients_args(clients),
                                    *boot_env_args(env), timeout=1800)
                print("   " + _tail(text, 2).replace("\n", "\n   "), flush=True)
                history.clear()
                booted = runner_boot("planned reboot", env)
                continue
            sets, run_args = split_sets(args)
            run_args = split_boot_env(run_args)[1]  # the arm's boot env goes to the runner's boots, not to its run
            wanted = envs[name]
            dropped = None
            refusal = catcher_refusal(free_gb(), catches(dumps_since(began)), min_free, max_dumps) if is_catcher(run_args) else None
            if refusal and not bare_when_capped:
                rows.append({"arm": name, "repeat": repeat_no, "args": args, "run": None, "skipped": f"catcher refused: {refusal}"})
                print(f"== {name} #{repeat_no}: not run, catcher refused ({refusal})", flush=True)
                continue
            if refusal:
                dropped, run_args, args, name = refusal, without_catcher(run_args), without_catcher(args), f"{name}:bare"
                print(f"== {name} #{repeat_no}: runs without the catcher ({refusal})", flush=True)
            boot_crashes: list[str] = []
            boot_line = None
            if not boot:
                problem = health()
                if problem and reboot:
                    print(f"== {problem}: booting again" + (f", boot env {env_text(wanted)}" if uses_env else ""),
                          flush=True)
                    code, text = python("scripts/quick_loop.py", "run", "--boot", *boot_clients_args(clients),
                                        *boot_env_args(wanted), timeout=1800)
                    print("   " + _tail(text, 2).replace("\n", "\n   "), flush=True)
                    history.clear()
                    booted = runner_boot("reboot-when-dead", wanted)
                    problem = health()
            gap = crashes_since(last_end, sides=True)  # the last arm's tail and any reboot: charged to that arm
            if gap:
                append_log(log, {"kind": "gap", "arm": previous["arm"] if previous else None,
                                 "repeat": previous["repeat"] if previous else None, "crashes": gap})
                if previous:
                    previous.setdefault("crashes_gap", []).extend(gap)
                print(f"   crash(es) after {previous['arm'] + ' #' + str(previous['repeat']) if previous else 'the start'}, "
                      f"before this arm (the gap): {'; '.join(gap)}", flush=True)
            if boot:
                # After the gap, before the sets and the process snapshot: the boot is the arm's own, and so are its
                # crashes; the arm keeps its sets and starts on a fresh process.
                boot_started = time.time()
                print(f"== {time.strftime('%H:%M:%S')} {name} #{repeat_no}: boot hook", flush=True)
                code, boot_line = hook_result(boot, name, repeat_no, kind="boot", boot_env=wanted)
                print(f"   boot: {boot_line}", flush=True)
                history.clear()
                booted = runner_boot("boot hook", wanted)
                boot_crashes = crashes_since(boot_started, sides=True)
                if boot_crashes:
                    print(f"   crash(es) during the boot hook: {'; '.join(boot_crashes)}", flush=True)
                problem = f"boot hook {boot_line}" if code != 0 else health()
            if problem:
                rows.append({"arm": name, "repeat": repeat_no, "args": args, "run": None, "skipped": problem,
                             **({"crashes_boot": boot_crashes} if boot_crashes else {})})
                print(f"== {name} #{repeat_no}: not run ({problem}); stopping", flush=True)
                stopped = "stack down"
                break
            setup_started = time.time()  # the crash snapshot comes before the sets: a crash while they take is setup's
            reading = None
            if status:  # before the sets too: the server's spare-only resend goes out within the settle wait
                reading = ArmStatus(out / "status.jsonl", name, repeat_no, status_every)
                reading.read("start")
                reading.start_timer()
            identity = game_identity()
            process = process_before(history, identity)
            if booted is not None and booted.get("identity") is None:
                booted["identity"] = identity  # the first snapshot after the runner's boot: that boot's processes
            on_env = {side: dict(booted[side]) if booted and same_process((booted["identity"] or {}).get(side),
                                                                           identity.get(side)) else None
                      for side in ("server", "client")}  # the env the games run on; None: no boot of this run's
            env_wrong = env_off(wanted, on_env)
            if uses_env:
                print(f"   boot env: {env_text(wanted)}", flush=True)
                if env_wrong:
                    origin = ("the stack as found" if booted is None else f"since the {booted['by']}"
                              + ("; a game restarted since" if None in on_env.values() else ""))
                    print(f"   warning: the {' and '.join(env_wrong)} boot env is not this arm's: the games run on "
                          f"{env_text(on_env)} ({origin})", flush=True)
            print("   process: " + "; ".join(
                f"{side} pid {p['pid']}, " + (f"ran {len(p['arms_before'])} arm(s) before"
                                              + (f", had {', '.join(p['had'])} on" if p["had"] else "") if p["arms_before"]
                                              else "fresh") for side, p in process.items()), flush=True)
            targets = arm_targets(sets, managed)
            problems, unread = set_tunables(targets)
            if unread:
                print(f"   not read by this mod (recorded, not fatal: a build without them takes any name): "
                      f"{', '.join(unread)}", flush=True)
            if problems:
                rows.append({"arm": name, "repeat": repeat_no, "args": args, "run": None,
                             "skipped": "tunables not set: " + "; ".join(problems)})
                print(f"== {name} #{repeat_no}: not run, tunables not set ({'; '.join(problems)}); stopping", flush=True)
                stopped = "tunables not set"
                break
            if targets:
                print("   set: " + ", ".join(f"{side_of(e)} {n}={v}" for e, n, v in targets), flush=True)
            if settle and targets:
                time.sleep(settle)  # e.g. the server's spare-only resend goes out 5 s after its copy turns on
            if before:
                print(f"   before: {run_hook(before, name, repeat_no, kind='before', boot_env=wanted)}", flush=True)
            setup_crashes = crashes_since(setup_started, sides=True)
            held = [arg for item in sets for arg in ("--held", item)]
            if held and identity:  # the run trusts an unchanged process instead of reading the holds per scenario
                held += ["--held-process", json.dumps(identity)]
            print(f"== {time.strftime('%H:%M:%S')} {name} #{repeat_no}: run {' '.join(names)} {' '.join(run_args)}".rstrip(),
                  flush=True)
            build = installed_build()
            if build:
                print(f"   build slot: {build['slot']} ({build['digest']})", flush=True)
            import machine_load  # scripts/machine_load.py: the machine's load over the run ( G19)

            sampler = machine_load.Sampler(interval=ARM_LOAD_INTERVAL).start()
            started = time.time()
            try:
                code, text = python("scripts/scenarios.py", "run", *names, *run_args, *held, timeout=6 * 3600)
            finally:
                load = run_load(sampler.stop())
            ended = time.time()
            dump_wait = wait_dumps()  # a dump being written is not cut by the detach or the next arm's down (G21)
            if is_catcher(run_args):
                python("scripts/crash_catch.py", "detach", "both", timeout=420)  # in case the run stopped before its own detach
            if reading:
                reading.read("end")
            row = arm_record(name, repeat_no, args, started, ended, text,
                             [c for c in crashes_since(started, sides=True) if c not in setup_crashes])
            row["sets"] = [f"{side_of(e)}:{n}={v}" for e, n, v in targets if v != "default"]
            if unread:
                row["sets_unread"] = unread  # set, but this mod never read them (tune_unread): the record, not a stop
            if build:
                row["build_slot"] = build
            row["load"] = load
            print(f"   load: {machine_load.text(load)}", flush=True)
            if uses_env:
                row["boot_env"], row["booted_env"] = wanted, on_env
                if env_wrong:
                    row["boot_env_off"] = env_wrong
            if row.get("set_check") == "tunables":
                print(f"   warning: the --set check read the tunable list (a mod call at each scenario's start and end) "
                      f"on {', '.join(f'the {side} in {n} scenario(s)' for side, n in row['set_check_tunables'].items())}: "
                      f"its TS3 start time could not be read; boot 5's arms made no such calls", flush=True)
            row["crashes_setup"] = setup_crashes
            if boot:
                row["crashes_boot"], row["boot_hook"] = boot_crashes, boot_line
            row["dumps"] = dumps_since(setup_started)
            row["dump_sizes"] = dump_sizes(row["dumps"])  # the final sizes, after the wait ( G21)
            if dump_wait.get("dumps"):
                row["dump_wait"] = dump_wait
            if dropped:
                row["catcher_dropped"] = dropped
            down = health()
            # A hang (the run watchdog cut a scenario short on a game still running, arms_lib.watchdog_hang) is not a
            # death in the run, even when the stack reads down after it (a bridge that stopped answering).
            row["died_in"] = ("setup" if setup_crashes else "run" if row["crashes"] or (down and not row.get("hangs"))
                              else "hang" if row.get("hangs") else None)
            # G16: a died scenario's minutes up to the crash; a run that never printed its report, linked.
            row.update(arms_lib.book_minutes(row, RUNS, names))
            verdicts = "".join(verdict_code(row, scenario) for scenario in row["verdicts"])
            print(f"   run {row['run']}: {verdicts or 'no verdicts'}, {row['scenario_min']} scenario-min, "
                  f"{len(row['crashes'])} crash(es) in the run" + (f" ({'; '.join(row['crashes'])})" if row["crashes"] else "")
                  + (f", {len(row['hangs'])} hang(s), not crashes ({'; '.join(row['hangs'].values())})"
                     if row.get("hangs") else "")
                  + (f", {len(setup_crashes)} in setup ({'; '.join(setup_crashes)})" if setup_crashes else "")
                  + (f", {len(row['dumps'])} dump(s) ("
                     + ", ".join(f"{n} {'?' if b is None else f'{b / 2**20:.0f} MB'}" for n, b in row["dump_sizes"].items())
                     + ")" if row["dumps"] else ""), flush=True)
            row["crashes_after"] = []
            hook_file = out / ("after-" + re.sub(r"[^\w.-]", "_", f"{name}-{repeat_no}") + ".txt")
            if after:
                # A game is down: a hook that names {down} still runs (a pull from the game that is up), any other is
                # skipped, as it would only wait on the dead game.
                dead = ",".join(down_sides()) or "none" if down and "{down}" in after else "none"
                if not down or "{down}" in after:
                    print(f"   after{f' ({dead} down)' if down else ''}: "
                          f"{run_hook(after, name, repeat_no, keep=hook_file, down=dead, kind='after', env_extra=arm_env(started, ended, out), boot_env=wanted)}",
                          flush=True)
                    row["crashes_after"] = [c for c in crashes_since(ended, sides=True)
                                            if c not in row["crashes"] and c not in setup_crashes]
                    if down:
                        row["after_down"] = dead
                else:
                    print("   after: skipped (a game is down; a hook that names {down} runs anyway)", flush=True)
                if hook_file.exists():
                    row["after_output"] = hook_file.name
                    row["hook"] = after_values(hook_file)
                if not down:
                    if row["crashes_after"] or health():
                        row["died_in"] = "after"
                        print(f"   a game died in the after hook: {'; '.join(row['crashes_after']) or 'no crash report'}",
                              flush=True)
            if reading:
                if after and not down:
                    reading.read("after")
                reading.stop()
            row["process"] = process
            restarted = note_process(history, f"{name}#{repeat_no}", arm_on_names(sets, run_args), game_identity())
            if restarted:
                row["process_restarted"] = restarted  # its history after the restart is unknown: the arm counts as fresh
                print(f"   TS3 process changed during the arm: {', '.join(restarted)}", flush=True)
            if reading:
                row["status"] = reading.record(restarted)
                print(f"   status: {status_line(row['status'])}", flush=True)
                gc_gate = arms_lib.gc_gate_fields(row["status"])
                if gc_gate:  # C's GC gate counters with its pauses: a long pause is what would block scenarios
                    row["gc_gate"] = gc_gate
                    print(f"   gcGate: {arms_lib.gc_gate_text(gc_gate)}", flush=True)
                reading = None
            failed_gate = check_arm_gates(gates, row, hook_file)
            rows.append(row)
            item = [order[index][1], order[index][0]]
            rerun = not failed_gate and rerun_blocked and all_blocked(row) and item not in reran
            if rerun:
                reran.append(item)
                order[index + 1:index + 1] = ([] if boot else [(0, REBOOT, [])]) + [order[index]]
            records = log_records(log) + [row]
            if stops and not failed_gate:
                row["stop_check"] = arms_lib.stop_check(stops, records)
            append_log(log, row)
            previous = row
            last_end = time.time()
            if failed_gate:
                print(f"== stopping: {failed_gate}", flush=True)
                stopped = "gate"
                save_state(index + 1)
                break
            if rerun:
                write_plan()
                print(f"   every scenario did not run (BLOCKED or RESET-FAILED), no exposure: {item[0]} #{item[1]}"
                      " runs again next"
                      + ("" if boot else ", behind a reboot") + " (--rerun-blocked, once)", flush=True)
            check = row.get("stop_check")
            if check:
                print(f"   stop-when: {arms_lib.stop_text(check)}", flush=True)
            if check and (check["fired"] or check["error"]):
                stopped_by = {"text": check["fired"], "error": check["error"], "values": check["values"],
                              "after": item, "row": sum(1 for r in records if "kind" not in r and "arm" in r),
                              "last": index + 1 >= len(order), "t": time.strftime("%Y-%m-%d %H:%M:%S")}
                if not stopped_by["last"]:
                    print(f"== stopping: {stop_reason(stopped_by)}; go on with: scenarios.py arms --resume {out.name} "
                          f"[--stop-when ...]", flush=True)
                    stopped = "stop-when"
                    save_state(index + 1)
                    break
        else:
            save_state(len(order), finished=True)
    finally:
        try:
            if json.loads((out / "runner.json").read_text(encoding="utf-8")).get("pid") == os.getpid():
                (out / "runner.json").unlink()
        except (OSError, ValueError):
            pass
        if reading:
            reading.stop()
        if managed:
            print("   tunables back to default: " + "; ".join(release_tunables(managed)), flush=True)
    summary = (summarize_arms(rows, safe_triage(lambda: report_paths(ROOT / ".omo" / "run"))) + gate_summary(gates)
               + ([""] + catcher_verdict(rows, out.name) if catcher_ab else []))
    if stopped_by:
        summary += ["", stop_reason(stopped_by)]
    (out / "summary.txt").write_text("\n".join(summary) + "\n", encoding="utf-8")
    print("\n".join(summary))
    print(f"arms: {out}")
    if stopped == "tunables not set":
        return 7
    if stopped == "gate":
        return 8
    if stopped in ("asked", "stop-when"):
        return 9  # arms --stop or --stop-when: state saved, --resume goes on (0 is a run that finished)
    # 6 means the stack went down; an arm the disk or dump guard refused is by design (the summary says "not run")
    return 0 if all(r.get("run") or str(r.get("skipped", "")).startswith("catcher refused") for r in rows) else 6


def load_run(run: Path) -> list[dict]:
    """A run's records, each marked nofork when the run's folder holds hook=absent (run_nofork). Every view (history,
    explain, rescore) reads through here."""
    records = saved_records(run)
    if run_nofork(run):
        for r in records:
            r["nofork"] = True
    return records


def saved_records(run: Path) -> list[dict]:
    """A run's records: its records.json, or for older runs the report's rows and each scenario's motion folder."""
    if (run / "rescored.json").exists():
        return [dict(r, rescored=True) for r in json.loads((run / "rescored.json").read_text(encoding="utf-8"))]
    data = run / "records.json"
    if data.exists():
        return json.loads(data.read_text(encoding="utf-8"))
    text = (run / "report.md").read_text(encoding="utf-8")
    records = []
    for line in text.splitlines():
        m = ROW.match(line)
        if m:
            name, verdict, expect, why = m.groups()
            section = text.split(f"\n## {name}: ", 1)
            body = section[1].split("</details>", 1)[0] if len(section) > 1 else ""
            records.append({"name": name, "verdict": verdict, "expect": expect, "failures": split_failures(why),
                            "met": None, "motion": motion_dir(body), "images": []})
    return records


def resolve_run(name: str, scenario: str = "") -> Path:
    candidates = [r for r in runs() if not scenario or any(record_key(rec) == scenario for rec in load_run(r))]
    if not candidates:
        raise SystemExit("no saved run" + (f" with {scenario}" if scenario else ""))
    if name in ("", "latest"):
        return candidates[-1]
    path = Path(name)
    if path.is_dir():
        return path
    match = [r for r in candidates if r.name == name or r.name.endswith(name)]
    if not match:
        raise SystemExit(f"no run {name}")
    return match[-1]


def changes(out: Path, records: list[dict]) -> list[str]:
    """What changed since each scenario's previous run (one line each, then a count of the unchanged)."""
    previous: dict[str, tuple[str, str]] = {}
    for run in reversed([r for r in runs() if r.name < out.name]):
        for rec in load_run(run):
            previous.setdefault(rec["name"], (run.name, rec["verdict"]))
    lines = []
    for r in records:
        before = previous.get(r["name"])
        if before is None:
            lines.append(f"  {record_key(r)}: first run, {r['verdict']}")
        elif before[1] != r["verdict"]:
            lines.append(f"  {record_key(r)}: {before[1]} -> {r['verdict']} (previous run {before[0]})")
    return ["changes since each scenario's previous run:", *lines, f"  {len(records) - len(lines)} unchanged"]


def history(names: list[str], last: int) -> int:
    """Each scenario's verdicts over the last runs (oldest first), one row each: P pass, F fail, B blocked, C crashed (a
    game went down in it; runs before 's CRASHED show such a scenario as F), R RESET-FAILED (its own last check
    found the previous scenario's state, so it never started, ), X fixed?, k known-failing, . not run; * after a
    mark: the verdict of a rescore (rescored.json), not the run's own."""
    kept = [r for r in runs() if not excluded(r)]
    chosen = kept[-last:]
    left_out = [r.name for r in runs() if excluded(r) and chosen and r.name >= chosen[0].name]
    loaded = [{record_key(rec): rec for rec in load_run(r)} for r in chosen]
    every = sorted({n for run in loaded for n in run})
    print("runs (oldest first): " + " ".join(r.name for r in chosen))
    if left_out:
        print("left out (scenarios.py exclude): " + " ".join(left_out))
    print("P pass, F fail, B blocked, R reset-failed, X fixed?, k known-failing, . not run; * rescored (rescored.json)")
    nofork = [r.name for r in chosen if run_nofork(r)]
    if nofork:
        print("NO-FORK runs (hook=absent, not gate results): " + " ".join(nofork))
    for name in names or every:
        marks = " ".join((CODES.get(run[name]["verdict"], "?") + ("*" if run[name].get("rescored") else " ")) if name in run
                         else ". " for run in loaded)
        latest = next((run[name] for run in reversed(loaded) if name in run), None)
        why = "; ".join(latest["failures"])[:110] if latest else ""
        if latest and latest.get("states"):
            why += f"  [states longer on one game: {len(latest['states'])}]"
        print(f"{name:<18} {marks}   {why}")
    return 0


WINDOW_SLACK = 120.0  # a crash report can land after the run's last write (the death, the final detach)


def run_window(run: Path, slack: float = WINDOW_SLACK) -> tuple[float, float] | None:
    """A saved run's span as epoch times: its folder name (local time) to its last records.json / report.md write."""
    try:
        start = time.mktime(time.strptime(run.name, "%Y%m%d-%H%M%S"))
    except ValueError:
        return None
    ends = [p.stat().st_mtime for p in (run / "records.json", run / "report.md") if p.exists()]
    return start, max(ends + [start]) + slack


def exposure_rows(run_dirs: list[Path], reports: list[tuple[float, str]]) -> tuple[list[dict], list[str]]:
    """One row per saved run: its tunable set, debugger kind, scenario minutes (BLOCKED scenarios did not run), the
    crash reports written in its span, and whether it is held out. A crash report goes to the latest run whose span
    holds it; the second value is the reports inside the runs' overall span that no run holds (between runs).
    Held out: excluded by hand, or automatically for a debugger. A run left out only because a game crashed or the
    stack went down stays in: that is the outcome being counted. A run with a CRASHED scenario and a crash report
    books that scenario to the crash, as an arm's is (arms_lib.book_minutes; completed_minutes keeps the old sum)."""
    import arms_lib

    rows = []
    for run in run_dirs:
        window = run_window(run)
        if not window:
            continue
        records = load_run(run)
        kinds = sorted({f"{side}:{kind}" for r in records for side, kind in debugger_entries(r)})
        counted = [k for k in kinds if not (k.endswith(":passive") and CATCHER_MEASURED)]
        reason = excluded(run)
        rows.append({
            "run": run.name, "start": window[0], "end": window[1],
            "tunes": ", ".join(sorted({t for r in records for t in (r.get("tunes") or [])})) or "defaults",
            "debugger": ",".join(kinds) or "none",
            "minutes": sum((r.get("timings") or {}).get("total", 0)
                           for r in records if r.get("verdict") not in NOT_RUN) / 60,
            "crashes": [], "excluded": bool(reason) and (not reason.startswith(AUTO) or bool(counted)), "reason": reason,
            "untimed": not any(r.get("timings") for r in records),  # saved before timings were recorded
            "soak": any(r.get("kind") == "soak" for r in records),  # run --soak: soak minutes, no scenario minutes
            "soak_minutes": sum((r.get("timings") or {}).get("soak", 0) for r in records if r.get("kind") == "soak") / 60,
            "crashed": any(r.get("verdict") == "CRASHED" for r in records), "dir": str(run.parent),
        })
    rows.sort(key=lambda r: r["start"])
    for row, after in zip(rows, rows[1:]):
        row["end"] = min(row["end"], after["start"])  # runs on one stack never overlap; a late rewrite cannot stretch one
    loose = []
    first = min((r["start"] for r in rows), default=None)
    last = max((r["end"] for r in rows), default=None)
    for when, name in reports:
        home = next((r for r in sorted(rows, key=lambda r: r["start"], reverse=True) if r["start"] <= when <= r["end"]), None)
        if home:
            home["crashes"].append(name)
        elif first is not None and first <= when <= last:
            loose.append(name)
    for row in rows:
        if row["crashed"] and row["crashes"]:
            booked = arms_lib.book_minutes({
                "arm": row["run"], "run": row["run"], "died_in": "run", "scenario_min": row["minutes"],
                "started": time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(row["start"])),
                "wall_min": (row["end"] - row["start"]) / 60, "crashes": row["crashes"]}, row["dir"])
            row["completed_minutes"], row["minutes"] = row["minutes"], booked["scenario_min"]
    return rows, loose


def _rate(crashes: int, minutes: float) -> str:
    return f"{crashes / minutes:.3f}" if minutes else "-"


def exposure_table(rows: list[dict], loose: list[str], unit: str = "scen-min") -> list[str]:
    """Per tunable set and debugger kind: runs, scenario minutes, crashes and crashes per scenario-minute, the runs held
    out (excluded by hand or for a debugger) counted in their own columns. unit names the minutes (soak-min for the
    soak table, whose rows carry their soak minutes as minutes)."""
    groups: dict[tuple[str, str], dict[bool, list[dict]]] = {}
    untimed = [r for r in rows if r.get("untimed")]
    rows = [r for r in rows if not r.get("untimed")]
    for row in rows:
        groups.setdefault((row["tunes"], row["debugger"]), {False: [], True: []})[row["excluded"]].append(row)
    numbers = f"{'runs':>4} {unit:>8} {'crashes':>7} {'per min':>7}"
    lines = [f"{'debugger':<16} {numbers}  | excluded: {numbers}  | tunables"]

    def cells(chosen: list[dict]) -> str:
        minutes = sum(r["minutes"] for r in chosen)
        crashes = sum(len(r["crashes"]) for r in chosen)
        return f"{len(chosen):>4} {minutes:>8.1f} {crashes:>7} {_rate(crashes, minutes):>7}"

    for (tunes, debugger), split in sorted(groups.items(), key=lambda kv: (-sum(r["minutes"] for r in kv[1][False]), kv[0])):
        lines.append(f"{debugger:<16} {cells(split[False])}  |           {cells(split[True])}  | {tunes}")
    kept = [r for r in rows if not r["excluded"]]
    lines.append(f"{'all':<16} {cells(kept)}  |           {cells([r for r in rows if r['excluded']])}  |")
    if untimed:
        lines.append(f"runs saved before timings were recorded (no scenario minutes, left out above): {len(untimed)}, "
                     f"{sum(len(r['crashes']) for r in untimed)} crash report(s) in them")
    if loose:
        lines.append(f"crash reports between runs (no run's span holds them): {len(loose)}")
    lines.append("excluded: by hand, or automatically for a debugger; a run left out only for a crash or a dead stack is "
                 "counted (that is the outcome)")
    return lines


def exposure(last: int, listing: bool) -> int:
    """Crash exposure across saved runs: crash reports per scenario-minute, by tunable set and debugger kind."""
    chosen = runs()[-last:] if last else runs()
    try:
        try:
            import crash_catch
        except ImportError:
            crash_catch = _MirrorProcFallback
        import quick_loop

        paths = crash_catch.crash_reports(None)
        boots, known = (crash_catch.game_boots(), crash_catch.crashes_known(paths)) if listing else ({}, {})
        reports = [(p.stat().st_mtime, f"{crash_side(p, quick_loop.REAL)} {p.name}"  # --list: each crash's uptime
                    + (f" ({crash_catch.uptime_of(p, boots, known)})" if listing else "")) for p in paths]
    except Exception as ex:  # no profile on this machine: the minutes still count
        print(f"crash reports not read: {ex}")
        reports = []
    rows, loose = exposure_rows(chosen, reports)
    if not rows:
        print("no saved runs")
        return 0
    oldest = time.strftime("%Y-%m-%d %H:%M", time.localtime(min(t for t, _ in reports))) if reports else None
    print(f"{len(rows)} run(s) {rows[0]['run']} .. {rows[-1]['run']}; "
          + (f"crash reports on disk from {oldest}" if oldest else "no crash reports on disk"))
    if listing:
        for r in rows:
            minutes = f"{r['soak_minutes']:>6.1f} soak-min" if r["soak"] else f"{r['minutes']:>6.1f} min"
            print(f"  {r['run']} {minutes} {len(r['crashes'])} crash(es) {r['debugger']:<16} "
                  f"{'EXCLUDED ' if r['excluded'] else ''}{r['tunes']}" + (f"  [{'; '.join(r['crashes'])}]" if r["crashes"] else ""))
    print("\n".join(exposure_table([r for r in rows if not r["soak"]], loose)))
    soaks = [dict(r, minutes=r["soak_minutes"]) for r in rows if r["soak"]]
    if soaks:  # run --soak: the games in the world with no scenarios, counted apart
        print(f"\nsoaks (run --soak, no scenarios): {len(soaks)} run(s), per soak minute")
        print("\n".join(exposure_table(soaks, [], unit="soak-min")[:-1]))
    return 0


def log_source(text: str) -> tuple[Path, str]:
    """--log PATH[=SERIES]: an arm log and the series its rows belong to (default: the arms folder's name for an
    arms.jsonl, else the file's stem, e.g. arms6)."""
    path, sep, series = text.rpartition("=")
    if not sep or not path or "/" in series or "\\" in series:
        path, series = text, ""
    p = Path(path)
    return p, series or (p.parent.name if p.name == "arms.jsonl" else p.stem)


def report_index() -> dict[tuple[str, str], tuple[Path, bool]]:
    """Every crash report still on disk, by (side, report name) -> (path, archived): the profiles and crash_catch's
    archive (.omo/run/crash-reports, filled on every read), so a report TS3 has rotated out of its profile (it keeps
    the 10 newest xcpt files, 5 pairs, at start) is still found. {} when the profiles cannot be read."""
    try:
        try:
            import crash_catch
        except ImportError:
            crash_catch = _MirrorProcFallback

        profile = crash_catch.report_folders()[crash_catch.SERVER_LABEL]
        index: dict[tuple[str, str], tuple[Path, bool]] = {}
        for path in crash_catch.crash_reports(None):
            index.setdefault((crash_catch.report_side(path, profile), path.name), (path, crash_catch.is_archived(path)))
        return index
    except Exception:
        return {}


def describe_crash(path: Path) -> str:
    try:
        import crash_catch
    except ImportError:
        crash_catch = _MirrorProcFallback

    return crash_catch.describe_report(path, short=True)


def exposure_pool(pools: list[str], logs: list[str], added: list[str], pairs: list[str], side: str, run_only: bool,
                  listing: bool, crash_match: str | None = None) -> int:
    """Crashes pooled by conditions defined from each arm's tunables, across arm logs (scenarios.py arms' arms.jsonl,
    or --log files, Session C's arms4-6.jsonl included), with exact P values and a warning on every comparison that
    mixes series (boots on another build). The counts come from the logs (each crash's report name, recorded when it
    happened), so a report TS3 later deletes still counts; crash_match (a regex) keeps only the crashes whose triage
    matches, read from the report in its profile or in crash_catch's archive, else from the triage logged then."""
    import arms_lib as al

    compiled = [al.parse_pool(text) for text in pools]
    labels = [label for label, _ in compiled]
    if logs:
        sources = [log_source(text) for text in logs]
    else:
        folders = sorted(ARMS.iterdir()) if ARMS.exists() else []
        sources = [(folder / "arms.jsonl", folder.name) for folder in folders if (folder / "arms.jsonl").exists()]
    rows = []
    for path, series in sources:
        if not path.exists():
            raise SystemExit(f"--log {path}: no such file")
        rows += al.load_arm_log(path, series)
    extra = [al.parse_added(text) for text in added]
    for row in extra:
        if row["fixed"] not in labels:
            raise SystemExit(f"--add {row['label']}: no --pool named {row['fixed']!r} (have: {', '.join(labels)})")
    if not rows and not extra:
        print("no arm logs (scenarios.py arms writes .omo/run/arms/<time>/arms.jsonl; --log FILE reads another)")
        return 0
    if crash_match is not None and extra:
        raise SystemExit("--crash-match cannot filter --add rows: they are counts, with no reports to read")
    tally = None
    if crash_match is not None or listing:
        tally = al.match_crashes(rows, crash_match, report_index(), describe_crash)
    assigned = al.assign(rows + extra, compiled)
    sums = al.pool_summary(assigned, labels, side, run_only)
    series = sorted({r["series"] for r in rows + extra})
    print(f"{len(rows)} arm row(s) from {len(sources)} log(s), {len(extra)} by hand; series {', '.join(series)}")
    if listing:
        for row, pool in assigned:
            crashes = ", ".join(f"{s} {phase}" for s, phase, _ in row["crashes"]) or "-"
            print(f"  {row['series']:<12} {row['label']:<22} {row['minutes']:>6.2f} min  {pool:<24} crashes: {crashes}")
            for line in row.get("seen") or []:
                print(f"      {line}")
    if tally is not None:
        total = tally["matched"] + tally["other"]
        print((f"--crash-match {crash_match!r}: {tally['matched']} of {total} crash(es) match"
               if crash_match is not None else f"{total} crash(es) in the logs")
              + f"; reports: {total - tally['gone']} on disk ({tally['archived']} only in the archive),"
              f" {tally['gone']} gone" + (f" ({tally['unknown']} with no triage logged either, so not counted)"
                                          if tally["unknown"] and crash_match is not None else ""))
    print("\n".join(al.pooled_table(sums, side)))
    chosen = [al.parse_pair(text) for text in pairs] or [(label, labels[-1]) for label in labels[:-1]]
    lines = al.comparison_lines(sums, chosen)
    if lines:
        print("\n".join(lines))
    if run_only:
        print("run phase only: crashes in setup, the after hook (a pull) and the gap to the next arm are left out"
              + ("; hand-entered rows (--add) count every phase" if extra else ""))
    return 0


def rescore(run_name: str) -> int:
    """Score a saved run's motion watches again with the detectors as they are now (scripts/motion_watch.py --rescore)
    and recompute the verdicts: seconds, instead of running the scenarios again. Writes rescored.json next to the run."""
    run = resolve_run(run_name)
    records = load_run(run)
    moved = []
    for r in records:
        if not r.get("motion"):
            continue
        if r["verdict"] in NOT_RUN or r["verdict"] == "CRASHED":  # it ran nothing, or a game died in it: no result to re-score
            r["states"] = motion_states(r["motion"])  # report only: a blocked or crashed verdict is not rescored
            continue
        before = r["verdict"]
        # a two-client scenario has a watch per client ("motions"), its failures tagged "client N "
        folders = r.get("motions") or {"": r["motion"]}
        tags = {c: (f"client {c} " if c else "") for c in folders}
        r["failures"] = [f for f in r["failures"] if not any(f.startswith(t + k) for t in tags.values() for k in MOTION_KINDS)]
        for client, folder in folders.items():
            code, text = python("scripts/motion_watch.py", "--rescore", folder)
            failure = motion_failure(code, text)
            if failure:
                r["failures"].append(tags[client] + failure)
        r["states"] = motion_states(r["motion"])
        verdict_of(r)
        if r.get("motions"):
            r["clients"] = client_verdicts(r, list(r["motions"]))
        if r["verdict"] != before:
            moved.append(f"  {r['name']}: {before} -> {r['verdict']}")
    (run / "rescored.json").write_text(json.dumps(records, indent=1), encoding="utf-8")
    print(f"run {run.name}, rescored:")
    if any(r.get("nofork") for r in records):
        print(NOFORK_BANNER)
    print("\n".join(table(records)))
    print("\n".join(["verdicts the detectors changed:", *moved] if moved else ["no verdict changed"]))
    return 0 if all(r.get("met", True) is not False for r in records) else 5


def _seconds(clock: str) -> int:
    h, m, s = clock.split(":")[:3]
    return int(h) * 3600 + int(m) * 60 + int(float(s))


def explain(name: str, run_name: str) -> int:
    """One scenario of a saved run: its failures, then for each client-only moment the watch's context lines and both
    games' state, placement and walk events from 3 s before to 1 s after. One call instead of a grep through report.md
    and events.txt."""
    run = resolve_run(run_name, name)
    rec = next(r for r in load_run(run) if record_key(r) == name)
    if rec.get("nofork"):
        print(NOFORK_BANNER)
    print(f"{name} in {run.name}: {rec['verdict']} (expected {rec['expect'][:60]})")
    for failure in rec["failures"]:
        print(f"  - {failure[:300]}")
    for state in rec.get("states", []):
        print(f"  ~ longer on one game (report only): {state}")
    for who, texts in rec.get("stream", {}).items():
        for text in texts:
            print(f"  ~ {who}: {text} (report only)")
    for text in rec.get("start", []):
        print(f"  ~ start: {text} (report only)")
    if rec.get("clients"):
        print("  per client: " + client_line(rec["clients"]))
    folders = rec.get("motions") or {"": rec.get("motion")}
    for client, folder in folders.items():
        if client:
            print(f"\nclient {client}'s watch:")
        explain_motion(Path(folder) if folder else None, folder)
    for image in rec.get("images", []):
        print(f"  evidence: {run / image}")
    print(f"  report: {run / 'report.md'}")
    return 0


def explain_motion(motion: Path | None, folder: str | None) -> None:
    """explain's part for one motion watch: each client-only moment with its context lines and the events near it."""
    if motion and (motion / "report.txt").exists():
        lines = (motion / "report.txt").read_text(encoding="utf-8").splitlines()
        events_file = motion / "events.txt"
        events = events_file.read_text(encoding="utf-8").splitlines() if events_file.exists() else []
        who = ""
        for line in lines:  # each client walk's step against the server's (motion_watch.walk_steps)
            if line.startswith("== "):
                who = line[3:].split(":", 1)[0]
            elif line.startswith("   walk    "):
                print(f"  {who} walk {line.strip()[len('walk'):].strip()}")
        for i, line in enumerate(lines):
            if "CLIENT-ONLY" not in line:
                continue
            print("\n" + line.strip())
            j = i + 1
            while j < len(lines) and lines[j].startswith("            "):
                print("    " + lines[j].strip())
                j += 1
            at = _seconds(line.split()[1])
            near = [e for e in events if e[:2].isdigit() and at - 3 <= _seconds(e.split()[0]) <= at + 1]
            print(f"  events {len(near)} (3 s before to 1 s after):")
            for e in near[:30]:
                print("    " + e[:220])
    elif folder:
        print(f"  motion folder {folder} has no report.txt")


DURING_ACTIONS = r"mirror (turn|walk)|mirror Pz|facing settle|walk start|by walk end snap|by repair|stand-up"


def _clock(text: str) -> float | None:
    try:
        h, m, sec = text.split(":")[:3]
        return int(h) * 3600 + int(m) * 60 + float(sec)
    except ValueError:
        return None


def during_run(events: list[str], clip: str, window: float, actions: str) -> dict | None:
    """One motion watch's events.txt lines, for a server clip MACHINE[:STATE]: the span from the server's first E on that
    machine to its StateEntered STATE (or window seconds without STATE, or when it never enters), the client's mod lines
    inside it (not sacs or state events), those matching actions, and when each game entered STATE."""
    machine, _, state = clip.partition(":")
    rows = []
    for line in events:
        parts = line.split(None, 3)
        at = _clock(parts[0]) if len(parts) == 4 else None
        if at is not None:
            rows.append((at, parts[1], parts[3]))
    start = next((at for at, side, rest in rows if side == "server" and rest.startswith(f"state E {machine}:")), None)
    if start is None:
        return None
    entered = f"StateEntered {machine}:{state}" if state else None
    server_in = next((at for at, side, rest in rows if entered and side == "server" and at >= start and entered in rest), None)
    client_in = next((at for at, side, rest in rows if entered and side == "client" and at >= start and entered in rest), None)
    end = server_in if server_in is not None else start + window
    mod = [(at - start, rest) for at, side, rest in rows if side == "client" and start <= at <= end
           and not rest.startswith(("sacs ", "state ", "server walk ended"))]
    return {"start": start, "span": end - start, "server_in": server_in, "client_in": client_in, "mod": mod,
            "hits": [m for m in mod if re.search(actions, m[1])]}


def during(name: str, clip: str, last: int, window: float, actions: str) -> int:
    """For one scenario over its saved runs: the copy's mod actions (mirror, walk runner, repair) while the server's Sim
    plays a clip, and how far apart the two games entered its state, with each run's verdict; then the verdicts split by
    whether such an action fell inside. Found the sleep-bed cause (2026-10-20): a mirror turn inside the bed's get-in
    clip in 9 failed runs and in no passing one. Reads saved runs only."""
    seen = []
    for run in [r for r in runs() if not excluded(r)]:
        rec = next((r for r in load_run(run) if r["name"] == name), None)
        events = Path(rec["motion"]) / "events.txt" if rec and rec.get("motion") else None
        if events and events.exists():
            seen.append((run, rec, events))
    split = {True: {}, False: {}}
    print(f"{name}: client mod actions inside the server's {clip} clip; last {last} runs with a motion watch")
    for run, rec, events in seen[-last:]:
        got = during_run(events.read_text(encoding="utf-8").splitlines(), clip, window, actions)
        mark = CODES.get(rec["verdict"], "?")
        if got is None:
            print(f"  {run.name} {mark}  no server E on {clip.partition(':')[0]}")
            continue
        gap = ""
        if got["server_in"] is not None and got["client_in"] is not None:
            gap = f"  client entered {got['client_in'] - got['server_in']:+.2f} s vs server"
        elif got["server_in"] is not None:
            gap = "  client never entered"
        inside = "; ".join(f"+{off:.2f} {what[:60]}" for off, what in got["hits"][:3]) or "-"
        print(f"  {run.name} {mark}  span {got['span']:.2f} s{gap}  inside: {inside}")
        split[bool(got["hits"])][mark] = split[bool(got["hits"])].get(mark, 0) + 1
    for hit, label in ((True, "with an action inside"), (False, "without")):
        print(f"{label}: " + (", ".join(f"{k} {v}" for k, v in sorted(split[hit].items())) or "none"))
    return 0


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("--client", default="", help="the client game to compare with the server: 1 (default), 2 (player 2's, "
                   "Client3Box) or HOST:PORT; arms passes it to each run (compare_client.py)")
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("list")
    h = sub.add_parser("home", help="show the scenarios' start spots; --save takes the Sims' server positions now")
    h.add_argument("--save", action="store_true")
    r = sub.add_parser("run")
    r.add_argument("names", nargs="*")
    r.add_argument("--repeat", type=int, default=1, metavar="N",
                   help="the suite N times in order (a b a b); each sample is its own record (sample k, name#k in "
                        "history and explain) with its own evidence folder (NAME-samplek); a name given twice is refused")
    r.add_argument("--boot", action="store_true", help="boot the stack first (quick_loop run --boot)")
    r.add_argument("--mod", action="store_true", help="with --boot: build and install the mod first")
    r.add_argument("--no-fork-ok", action="store_true", help="with --boot: boot although no gameplay fork is installed (quick_loop refuses otherwise)")
    r.add_argument("--bridge-trace", action="store_true", help="with --boot: stackctl boots with --bridge-trace (quick_loop run --bridge-trace)")
    r.add_argument("--clients", type=int, choices=(1, 2, 3), default=None,
                   help="with --boot: 2 boots player 2's game too, 3 player 3's as well (quick_loop run --clients); "
                        "default 2 under --client 2")
    r.add_argument("--watch-clients", default="", metavar="1,2",
                   help="watch and compare these clients in every scenario that names none (\"clients\" in its file "
                        "wins): one motion watch and one sim_compare per client, and a verdict per client")
    r.add_argument("--tune", action="append", default=[], metavar="[SIDE:]NAME=VALUE",
                   help="set a tunable (scripts/tune.py) for each scenario and put it back after; side client (default), "
                        "server, both, client2, clients (both players' games) or all; repeatable. Recorded in "
                        "records.json and report.md")
    r.add_argument("--held", action="append", default=[], metavar="[SIDE:]NAME=VALUE",
                   help="a tunable the caller set for the whole run (scenarios.py arms --set): set again at a scenario's "
                        "start only if a restart lost it, checked at its end, never put back; recorded like --tune")
    r.add_argument("--held-process", default="", metavar="JSON",
                   help="with --held: each game's TS3 process when the holds were set ({side: {pid, started}}, as "
                        "arms passes it); while a game is still that process its holds are not read again")
    r.add_argument("--no-repair", action="store_true",
                   help="the reset between scenarios repairs nothing (no sim_act --repair all, e89f60c): an arm on a "
                        "build from before it, e.g. --arm 'old=--no-repair'. Each record says no_repair")
    r.add_argument("--rejoin", choices=("warm", "paused", "cold"), default="",
                   help="every rejoin step runs as rejoin:MODE (warm: client 1's controller only; cold: a plain rejoin), "
                        "so a late-join scenario runs as either kind of join. Each record says rejoin")
    r.add_argument("--catcher", action="store_true", help="attach the passive crash catcher to both games for this run "
                   "(crash_catch.py attach) and detach it at the end; the run is left out until its overhead is measured")
    r.add_argument("--catcher-side", choices=("both", "server", "client"), help="--catcher on one game only")
    r.add_argument("--soak", type=float, default=0.0, metavar="MIN",
                   help="no scenarios: both games in the world for MIN minutes (soak minutes in exposure, apart from "
                        "scenario minutes); stops at the first dead game. With arms: --arm 'idle=--soak 20'")
    r.add_argument("--speed", choices=SOAK_SPEEDS, default="Normal", help="with --soak: the game speed (both games)")
    r.add_argument("--soak-sims", choices=("free", "held"), default="free",
                   help="with --soak: free (every hold released: autonomy runs) or held (the scenarios' reset: held, "
                        "needs static)")
    r.add_argument("--server-env", action="append", default=[], metavar="K=V",
                   help="with --boot: extra server controller environment (quick_loop run --server-env), e.g. "
                        "SIMLINK_NOPATCH=1; a later run without --boot stays in that process")
    sub.add_parser("health", help="both games answering and in the world? (a run checks this before each scenario); "
                   "then each game's boot age, warm rejoins and module loads since its boot, replay failures and the "
                   "drivers check, and whether a fresh boot is due (replay_check.health_lines; its last line's NAME=N "
                   "tokens suit an arms --after hook)")
    hist = sub.add_parser("history", help="each scenario's verdicts over the last runs, one row each")
    hist.add_argument("names", nargs="*")
    hist.add_argument("--last", type=int, default=10)
    rs = sub.add_parser("rescore", help="score a saved run's motion watches again with the current detectors")
    rs.add_argument("run", nargs="?", default="latest")
    xc = sub.add_parser("exclude", help="leave a run out of history and the sacs_timing survey (a debugger attached)")
    xc.add_argument("run")
    xc.add_argument("reason", nargs="*")
    xc.add_argument("--undo", action="store_true")
    ar = sub.add_parser("arms", help="the suite once per arm and repeat, interleaved (A B B A), with crash exposure")
    ar.add_argument("names", nargs="*")
    ar.add_argument("--arm", action="append", default=[], metavar="NAME=ARGS",
                    help="an arm: its name and extra `run` arguments, e.g. 'dbg=--catcher-side server'; --set [SIDE:]N=V "
                         "in ARGS is held for the whole arm (before hook, run, after hook), e.g. 't3on=--set both:sacs."
                         "seed-copy=1 --set both:sacs.keyed-picks=1', where --tune would be set and put back per "
                         "scenario; repeatable")
    ar.add_argument("--settle", type=float, default=0.0, metavar="SECONDS",
                    help="wait this long after an arm's --set tunables take, before its before hook (e.g. 8 for the "
                         "copy-on resend)")
    ar.add_argument("--status", action="store_true",
                    help="read both games' SACS_RECORDER status before each arm's sets, after its run and after its "
                         "after hook (boot 5's calls); the row gets start/end/after and their deltas (default off)")
    ar.add_argument("--status-every", type=float, default=0.0, metavar="SECONDS",
                    help="with it, also read status every SECONDS during the arm, so a crashed arm keeps its last "
                         "counters (implies --status; default 0, off: each reading is a mod call during the arm)")
    ar.add_argument("--resume", default=None, metavar="FOLDER",
                    help="go on with an arms run (latest, or its folder) from its next arm, its arguments and carried "
                         "state (process history, gates, the last arm's gap); --order replaces what was left")
    ar.add_argument("--stop", nargs="?", const="latest", default=None, metavar="FOLDER",
                    help="ask a running arms run (latest, or its folder) to stop before its next arm, state saved; "
                         "that run exits 9 (finished 0, gate 8, tunables not set 7, stack down 6)")
    ar.add_argument("--stop-when", action="append", default=None, metavar="EXPR",
                    help="after each arm, stop the run (exit 9, state saved) when EXPR holds over every row so far: "
                         "ARM.min, ARM.runs, ARM[.server|.client].crashes[.PHASE] (run by default; boot, setup, after, "
                         "gap, any), p(A, B[, SIDE[, PHASE]]) (one-sided conditional binomial, A's rate above B's), "
                         "e.g. 'base.server.crashes >= 5 or base.min >= 150'; repeatable; kept in plan.json, and "
                         "with --resume it replaces the plan's")
    ar.add_argument("--stop-check", nargs="?", const="latest", default=None, metavar="FOLDER",
                    help="replay a run's stop conditions (--stop-when, or its plan's) over its log, as the runner "
                         "checks them: the first row where one holds (exit 9), or none (exit 0); read only")
    ar.add_argument("--as", dest="rename", default="", metavar="OLD=NEW,...",
                    help="with --stop-check: read arm OLD as NEW, e.g. off=base,on=gate")
    ar.add_argument("--exclude", action="append", default=[], metavar="ARM#N[,ARM#N...]",
                    help="with --stop-check or --summary: leave these arm rows, and the gap records after them, out of "
                         "the counts, minutes, p and rates, and say what was left out (--stop-check also gives the "
                         "values with every row); FOLDER/ARM#N with several --summary folders; repeatable")
    ar.add_argument("--exclude-invalid", action="store_true",
                    help="with --stop-check or --summary: leave out every row a gate failed or erred on (not a row "
                         "that died: its gates were not evaluated), as --exclude")
    ar.add_argument("--rerun-blocked", action="store_true",
                    help="run an arm whose every scenario did not run (BLOCKED or RESET-FAILED, no exposure) again "
                         "once, right after it, behind a "
                         "planned reboot (none with --boot-hook); off by default")
    ar.add_argument("--wait", nargs="?", const="latest", default=None, metavar="FOLDER",
                    help="wait for an arms run's next arm: print each new row's one-line result and exit 0, or exit 9 "
                         "once the run has ended; read only (a monitor's command, one call per arm)")
    ar.add_argument("--seen", type=int, default=None, metavar="N",
                    help="with --wait: the rows already seen (the last row number it printed); default the rows there now")
    ar.add_argument("--wait-run", nargs="?", const="latest", default=None, metavar="FOLDER",
                    help="wait until the run's current arm is in its run phase (its live state.json item and that "
                         "item's newest event, never an older arm's line), with --min-left seconds left by its typical "
                         "run and no catcher dump being written: exit 0 with one line, or 9 once the run has ended; "
                         "read only (time a short build mid-arm)")
    ar.add_argument("--min-left", type=float, default=0.0, metavar="S",
                    help="with --wait-run: at least S seconds of the run left by the typical run length (the median of "
                         "this arms run's finished runs, or --typical)")
    ar.add_argument("--typical", type=float, default=None, metavar="S",
                    help="with --wait-run: the typical run length in seconds, in place of the median of finished runs")
    ar.add_argument("--crashes", nargs="?", const="latest", default=None, metavar="FOLDER",
                    help="print each crash of an arms run (latest, a folder name, or a path; a name is looked up in "
                         "SIMSONLINE_RUN_DIR's arms folder when set): time, arm, phase, scenario, first or later arm in "
                         "its process, the game's uptime and last collection (gc-watch.jsonl), the triage now; read only")
    ar.add_argument("--summary", nargs="*", default=None, metavar="FOLDER",
                    help="print an arms run's summary again (latest, or each FOLDER), with each crash's triage as its "
                         "report reads now; several are pooled, their first/later split by series; read only")
    ar.add_argument("--odds", type=float, default=None, metavar="MIN",
                    help="with --summary: each arm's Poisson chance of more crashes in MIN more minutes at its rate")
    ar.add_argument("--hangs-as-crashes", action="store_true",
                    help="with --summary: count the run watchdog's hangs (H: a scenario cut short on a live game) "
                         "as crashes too; by default they are counted apart")
    ar.add_argument("--check", action="store_true",
                    help="print what the run would do (arms, order, tunables, gates) after its own checks, then exit; "
                         "no game touched, nothing written")
    ar.add_argument("--check-against", default=None, metavar="FOLDER",
                    help="with --check (implied): evaluate each gate on this finished arms run's status readings")
    ar.add_argument("--readings", nargs="?", const="latest", default=None, metavar="FOLDER",
                    help="print an arms run's status readings (status.jsonl), one line per reading with each --field; "
                         "with no --field, the field names; read only")
    ar.add_argument("--field", action="append", default=[], metavar="NAME[,NAME]",
                    help="with --readings or --table: a status field, or an fnmatch pattern (eventRecorder.*); repeatable")
    ar.add_argument("--kind", default="", metavar="KIND[,KIND]",
                    help="with --readings: only these readings (start, end, after, timer)")
    ar.add_argument("--table", action="store_true",
                    help="print one row per arm run of an arms run (latest, or each --folder; several are pooled): its "
                         "run, minutes, run crashes and those outside the run, verdicts, where it died, and each "
                         "--field's change from the arm's start status reading to its last, per game; read only")
    ar.add_argument("--json", action="store_true", help="with --table: the rows as JSON lines instead of text")
    ar.add_argument("--compare", nargs=2, default=None, metavar=("A", "B"),
                    help="arm A's run crashes and exposure against arm B's over an arms run (latest, or each --folder): "
                         "the one-sided conditional binomial P, the rate ratio and its exact 95%% CI; read only")
    ar.add_argument("--folder", action="append", default=[], metavar="FOLDER",
                    help="with --table or --compare: the arms run(s) to read (latest, a folder name, or a path; "
                         "repeatable)")
    ar.add_argument("--rows", type=Path, default=None, metavar="PATH",
                    help="with --crashes: this gc-watch.jsonl, not the run folder's (a watch that wrote elsewhere)")
    ar.add_argument("--gate", action="append", default=[], metavar="'[once] ARMS: EXPR'",
                    help="after each arm in ARMS (comma separated, or *) that finished alive, stop the run (exit 8) "
                         "unless EXPR holds; names VIEW.SIDE.KEY (VIEW start, end, after, last, delta, delta_after; "
                         "e.g. delta.server.spareResent > 0), hook.NAME (NAME=N or NAME:N in the after hook's output), "
                         "arm, repeat; on()/had()/val(); a missing name stops too. Turns --status on")
    ar.add_argument("--catcher-ab", action="store_true", help="arms bare and catcher (run --catcher), then the verdict")
    ar.add_argument("--repeat", type=int, default=2)
    ar.add_argument("--order", default="", metavar="'A B B A reboot C'",
                    help="run the arms in this order instead of --repeat's A B B A (names space or comma separated; "
                         "'reboot' is a planned reboot, e.g. so the next arms run in a process that never read seeds)")
    ar.add_argument("--boot-hook", default="", metavar="CMD",
                    help="command that boots each arm in place of the reboot-when-dead, before its --set and process "
                         "snapshot ({arm}, {repeat}; {server_env} and {client_env}, the arm's boot env as quick_loop "
                         "run --boot's words; e.g. scripts/saves.py arm ...); non-zero exit stops the run")
    ar.add_argument("--server-env", action="append", default=[], metavar="K=V",
                    help="the server's boot env for every arm (repeatable; an arm's own --server-env K=V in its ARGS goes "
                         "over it): each boot the runner makes passes it to quick_loop run --boot (a planned reboot the "
                         "next arm's, a reboot-when-dead the arm's own) and a --boot-hook gets it as {server_env}; kept "
                         "in plan.json, each row and state.json. Never set a server-only key in the runner's own shell: "
                         "both games inherit it")
    ar.add_argument("--clients", type=int, choices=(1, 2, 3), default=None,
                    help="how many client games every boot the runner makes starts (quick_loop run --boot --clients: a "
                         "planned reboot, a reboot-when-dead); default quick_loop's (2 under --client 2, else 1). Give 2 "
                         "for two-player scenarios (two-hud, two-same-chair): --check warns when one is named without it; "
                         "kept in plan.json")
    ar.add_argument("--client-env", action="append", default=[], metavar="K=V",
                    help="the client's boot env for every arm, as --server-env ({client_env} in a --boot-hook)")
    ar.add_argument("--before", default="", help="command before each arm ({arm}, {repeat}); a .py runs with this Python")
    ar.add_argument("--after", default="", help="command after each arm while both games are up (e.g. a pull); with a "
                    "game down it runs only if it names {down} (server, client, server,client; none when both are up); "
                    "it gets the arm's run window and the arms run's folder as SIMSONLINE_ARM_START, SIMSONLINE_ARM_END "
                    "and SIMSONLINE_ARMS_FOLDER (gc_watch.py frames --arm)")
    ar.add_argument("--no-reboot", action="store_true", help="stop when a game is dead instead of booting again")
    ar.add_argument("--min-free-gb", type=float, default=MIN_FREE_GB, help="a catcher arm needs this much free disk")
    ar.add_argument("--max-dumps", type=int, default=MAX_DUMPS,
                    help="catcher arms stop after this many crashes are dumped (one attach's dumps count once)")
    ar.add_argument("--bare-when-capped", action="store_true", help="run a refused catcher arm without the catcher "
                    "(recorded as catcher_dropped) instead of skipping it")
    xp = sub.add_parser("exposure", help="crashes per scenario-minute across saved runs, by tunables and debugger kind")
    xp.add_argument("--last", type=int, default=0, help="only the last N runs (default every saved run)")
    xp.add_argument("--list", action="store_true", help="one line per run first: its minutes, crashes and group")
    xp.add_argument("--pool", action="append", default=[], metavar="LABEL=EXPR",
                    help="pool arm rows instead (arm logs, not saved runs): the first pool whose expression is true takes "
                         "the row; on(\"name\"), val(\"name\"), had(\"name\") (on earlier in the same TS3 process), arm, "
                         "series, debugger; e.g. 'copy on=on(\"sacs.seed-copy\")' 'base=true'; repeatable, in order")
    xp.add_argument("--log", action="append", default=[], metavar="PATH[=SERIES]",
                    help="with --pool: an arm log to read (default every .omo/run/arms/*/arms.jsonl, one series each)")
    xp.add_argument("--add", action="append", default=[], metavar="SERIES:POOL=MIN/SERVER[/CLIENT]",
                    help="with --pool: a pre-counted row, e.g. from a doc table ('boots2-4:copy on=52.8/6')")
    xp.add_argument("--compare", action="append", default=[], metavar="A:B",
                    help="with --pool: compare two pools (default: each pool against the last one)")
    xp.add_argument("--side", choices=("both", "server", "client"), default="both",
                    help="with --pool: whose crashes the rates and comparisons count")
    xp.add_argument("--run-only", action="store_true",
                    help="with --pool: count only crashes in the run phase (not setup, the after hook or the gap)")
    xp.add_argument("--crash-match", default=None, metavar="REGEX",
                    help="with --pool: count only crashes whose triage matches (e.g. 00e54af9 or 'interp'), read from "
                         "the report in its profile or crash_catch's archive, else the triage logged at the time")
    ex = sub.add_parser("explain", help="one scenario of a saved run: failures and what happened at each moment")
    ex.add_argument("name")
    ex.add_argument("--run", default="latest")
    st = sub.add_parser("status", help="where a run is, from its folder alone: a suite run's verdicts so far and the "
                        "scenario on now (since when, typical length); an arms run's rows, item and phase; no RUN: the "
                        "newest arms run while it runs, then the newest suite run; read only")
    st.add_argument("run", nargs="?", default="latest", help="latest (default), a suite or arms run folder, or a path")
    wt = sub.add_parser("wait", help="wait for a run's next verdict (an arms run: its next arm row) or, --until end, its "
                        "end, in one call: exit 0 with the new verdict(s), 9 once the run has ended, 3 at --timeout "
                        "(still running: call again); read only")
    wt.add_argument("run", nargs="?", default="latest", help="latest (default: the running arms run, else the newest "
                    "suite run), a suite or arms run folder, or a path")
    wt.add_argument("--until", choices=WAIT_UNTIL, default="scenario")
    wt.add_argument("--seen", type=int, default=None, metavar="N",
                    help="the verdicts (arm rows) already seen; default the ones there now")
    wt.add_argument("--timeout", type=float, default=WAIT_TIMEOUT, metavar="S",
                    help=f"give up after S seconds with exit 3 (default {WAIT_TIMEOUT:.0f}, inside the Bash tool's 10 "
                    "minutes: give the tool call a timeout above it)")
    du = sub.add_parser("during", help="one scenario over saved runs: the copy's mod actions inside a server clip, and "
                        "the state-entry gap between the games, by verdict")
    du.add_argument("name")
    du.add_argument("clip", help="MACHINE[:STATE], e.g. BedSleep:Sleep: from the server's first E on MACHINE to its "
                    "StateEntered STATE")
    du.add_argument("--last", type=int, default=40)
    du.add_argument("--window", type=float, default=8.0, help="the span in seconds without STATE, or when it never enters")
    du.add_argument("--actions", default=DURING_ACTIONS, help="regex: the client lines that count as an action inside")
    a = p.parse_args(argv)
    if a.client:
        global CLIENT
        CLIENT = compare_client.use(a.client)  # the tools a run starts (motion_watch, tune, sim_compare) follow it
        print(f"   comparing the server with {compare_client.name(CLIENT)} ({CLIENT})", flush=True)
    if a.cmd == "health":
        problem = health()
        print(problem or "ok: both games answer, in the world")
        import quick_loop
        print(quick_loop.fork_line())
        try:
            import replay_check
            lines, _ = replay_check.health_lines(replay_check.health_facts())
        except Exception as error:  # noqa: BLE001 - the answer above still stands
            lines = [f"long-session health not read ({type(error).__name__}: {error})"]
        print("\n".join(lines))
        return 6 if problem else 0
    if a.cmd == "status":
        return status(a.run)
    if a.cmd == "wait":
        return wait(a.run, a.until, a.seen, a.timeout)
    if a.cmd == "history":
        return history(a.names, a.last)
    if a.cmd == "exclude":
        return exclude(a.run, " ".join(a.reason), a.undo)
    if a.cmd == "rescore":
        return rescore(a.run)
    if a.cmd == "explain":
        return explain(a.name, a.run)
    if a.cmd == "during":
        return during(a.name, a.clip, a.last, a.window, a.actions)
    if a.cmd == "exposure":
        if a.pool:
            return exposure_pool(a.pool, a.log, a.add, a.compare, a.side, a.run_only, a.list, a.crash_match)
        if a.log or a.add or a.compare or a.run_only or a.crash_match is not None:
            raise SystemExit("--log, --add, --compare, --run-only and --crash-match go with --pool")
        return exposure(a.last, a.list)
    if a.cmd == "arms":
        if a.rows is not None and a.crashes is None:
            raise SystemExit("--rows goes with --crashes")
        if a.seen is not None and a.wait is None:
            raise SystemExit("--seen goes with --wait")
        if a.wait is not None:
            return arms_wait(a.wait, a.seen)
        if (a.min_left or a.typical is not None) and a.wait_run is None:
            raise SystemExit("--min-left and --typical go with --wait-run")
        if a.wait_run is not None:
            return arms_wait_run(a.wait_run, a.min_left, a.typical)
        if a.kind and a.readings is None:
            raise SystemExit("--kind goes with --readings")
        if a.field and a.readings is None and not a.table:
            raise SystemExit("--field goes with --readings or --table")
        if a.json and not a.table:
            raise SystemExit("--json goes with --table")
        if a.folder and not (a.table or a.compare):
            raise SystemExit("--folder goes with --table or --compare")
        if (a.exclude or a.exclude_invalid) and (a.table or a.compare):
            raise SystemExit("--exclude and --exclude-invalid go with --summary or --stop-check; --table and --compare "
                             "count every row")
        if a.table:
            return arms_table(a.folder, [f for text in a.field for f in text.split(",") if f], a.json)
        if a.compare:
            return arms_compare(a.compare[0], a.compare[1], a.folder)
        if a.crashes is not None:
            return arms_crashes(a.crashes, a.rows)
        if a.readings is not None:
            return arms_readings(a.readings, [f for text in a.field for f in text.split(",") if f],
                                 [k for k in a.kind.split(",") if k])
        if a.hangs_as_crashes and a.summary is None:
            raise SystemExit("--hangs-as-crashes goes with --summary")
        if a.odds is not None and a.summary is None:
            raise SystemExit("--odds goes with --summary")
        if (a.exclude or a.exclude_invalid) and a.summary is None and a.stop_check is None:
            raise SystemExit("--exclude and --exclude-invalid go with --stop-check or --summary")
        if a.summary is not None:
            return arms_summary_again(a.summary, a.odds, a.exclude, a.exclude_invalid, a.hangs_as_crashes)
        if a.rename and a.stop_check is None:
            raise SystemExit("--as goes with --stop-check")
        if a.stop_check is not None:
            return arms_stop_check(a.stop_check, a.stop_when, a.rename, a.exclude, a.exclude_invalid)
        if a.stop is not None:
            return stop_arms(a.stop)
        if a.check or a.check_against is not None:
            if a.resume is not None:
                raise SystemExit("--check checks a new run's arguments, not --resume")
            return arms_check(a.names, a.arm, a.repeat, a.order, a.gate, a.boot_hook, a.catcher_ab, a.check_against,
                              a.stop_when, a.server_env, a.client_env, a.clients)
        if a.resume is not None:
            if (a.arm or a.names or a.gate or a.before or a.after or a.boot_hook or a.catcher_ab or a.rerun_blocked
                    or a.server_env or a.client_env or a.clients):
                raise SystemExit("--resume takes the arms, names, hooks, gates and boot env from the run's plan.json; "
                                 "give only --order (what is left) and --stop-when (replacing the plan's) with it")
            return arms([], [], 0, "", "", True, False, order_text=a.order, resume_from=arms_folder(a.resume),
                        stop_texts=a.stop_when)
        return arms(a.names, a.arm, a.repeat, a.before, a.after, not a.no_reboot, a.catcher_ab, a.min_free_gb, a.max_dumps,
                    a.bare_when_capped, settle=a.settle, order_text=a.order, status=a.status,
                    status_every=a.status_every, gate_texts=a.gate, boot=a.boot_hook, rerun_blocked=a.rerun_blocked,
                    stop_texts=a.stop_when, server_env=a.server_env, client_env=a.client_env, clients=a.clients)
    if a.cmd == "home":
        if a.save:
            return save_home()
        print(HOME.read_text(encoding="utf-8") if HOME.exists() else "no home yet (scenarios.py home --save)")
        return 0
    if a.cmd == "list":
        for spec in load([]):
            print(f"{spec['name']:<22} {spec.get('expect', 'pass').split(':')[0]:<14} {spec.get('summary', '')}")
        return 0

    if a.soak and a.names:  # a soak arm in an arms run gets the scenario arms' names too
        print(f"--soak runs no scenarios: {' '.join(a.names)} not run", flush=True)
    if a.server_env and not a.boot:
        raise SystemExit("--server-env needs --boot (it is the server controller's environment at launch)")
    if a.clients and not a.boot:
        raise SystemExit("--clients needs --boot (it is how many client games the boot starts)")
    specs = repeat_specs([soak_spec(a.soak, a.speed, a.soak_sims)] if a.soak else load(a.names), a.repeat)
    global RUN_CLIENTS
    RUN_CLIENTS = [c.strip() for c in a.watch_clients.split(",") if c.strip()] or None
    # health() checks every client a scenario of this run watches, not only --client's
    EXTRA_CLIENTS[:] = sorted({compare_client.resolve(c) for s in specs if not s.get("soak")
                               for c in spec_clients(s, RUN_CLIENTS)} - {CLIENT})
    import stack_claim  # warn-only: another session's claim on the games this run drives

    claimed = stack_claim.warning(stack_claim.own_session())
    if claimed:
        print(claimed)
    if not a.boot:
        import quick_loop
        fork = quick_loop.physical_fork_census()
        if fork["state"] != "fork":
            line = quick_loop.fork_census.summary(fork)
            print("FORK-MISSING: " + line + "; this run's games have no gameplay fork unless booted "
                  "with one (hook=absent: client picks stay local, two-player scenarios BLOCKED)", file=sys.stderr, flush=True)
    if a.boot:
        code, text = python("scripts/quick_loop.py", "run", *(["--mod"] if a.mod else []), "--boot",
                            *(["--no-fork-ok"] if a.no_fork_ok else []),
                            *(["--bridge-trace"] if a.bridge_trace else []),
                            *(["--clients", str(a.clients)] if a.clients else []),
                            *[arg for env in a.server_env for arg in ("--server-env", env)], timeout=1800)
        print(_tail(text, 6))
        if code != 0:
            return code
    problem = health()
    if problem:
        print(problem)
        return 6
    out = RUNS / time.strftime("%Y%m%d-%H%M%S")
    out.mkdir(parents=True, exist_ok=True)
    run_started = time.time()
    progress = {"runner": runner_identity(os.getpid()), "planned": [record_key(s) for s in specs], "current": None,
                "finished": None}
    write_json(out / PROGRESS, progress)
    print(f"   follow it: scenarios.py status {out.name} | scenarios.py wait {out.name} [--until end]", flush=True)
    records = []
    module = None
    global REPAIR, WATCHED, REJOIN
    REPAIR = not a.no_repair
    WATCHED = [] if a.soak else watched_clients()
    if len(WATCHED) > 1:
        print(f"   replay counters and drivers check on {len(WATCHED)} clients: {', '.join(WATCHED)}", flush=True)
    if not a.held and not a.soak:  # under arms (--held) the in-game calls stay fixed; arms --status reads these fields
        games = [("server", SERVER), ("client", CLIENT)] + [(compare_client.name(e), e) for e in EXTRA_CLIENTS
                                                            if e != CLIENT]
        patches = patch_line({side: read_status(endpoint) for side, endpoint in games})
        print("   " + patches, flush=True)
        (out / "patches.txt").write_text(patches + "\n", encoding="utf-8")
    if a.no_repair:
        print("   reset: no --repair all between scenarios (--no-repair)", flush=True)
    REJOIN = a.rejoin
    if a.rejoin:
        print(f"   rejoin steps run as {rejoin_step('rejoin')} (--rejoin)", flush=True)

    def leave_out_if_polluted(problem: str | None) -> None:
        # a debugger on a game, a dead game or a crash report: the timings are not the games' own
        reason = auto_exclude(out, records, problem, crashes_since(run_started))
        if reason:
            report(out, records)  # report.md says so too
            print(f"   left out of history and the survey: {reason} (scenarios.py exclude {out.name} --undo keeps it)",
                  flush=True)

    catcher = a.catcher_side or ("both" if a.catcher else None)
    if catcher:
        # A passive catcher (no breakpoints) on both games or one for this run, detached at its end (arms --catcher-ab)
        code, text = python("scripts/crash_catch.py", "attach", catcher, timeout=120)
        print("   catcher: " + " | ".join(ln.strip() for ln in text.splitlines() if ln.strip())[:300], flush=True)

    held_known = None  # each game's TS3 process when the arms runner set the holds (--held-process)
    warned_set_check = False
    if a.held and a.held_process:
        try:
            held_known = json.loads(a.held_process)
        except ValueError:
            raise SystemExit(f"--held-process {a.held_process!r}: not JSON") from None
    died = None  # a game found down after the last scenario
    with snapshot_on_exit(out, run_started):  # also on Ctrl-C or an error: an aborted no-fork run needs the mark
        for index, spec in enumerate(specs):
            problem = health() if index else None
            if problem:
                mark_crashed(records[-1], problem)  # the game died in the scenario before, or right after it
                # The stack went down part way: the rest would only cost minutes each and say nothing.
                for rest in specs[index:]:
                    records.append({"name": rest["name"], "summary": rest.get("summary", ""), "expect": rest.get("expect", "pass"),
                                    "failures": [problem], "verdict": "BLOCKED", "met": False, "log": [], "images": [],
                                    **({"sample": rest["sample"]} if rest.get("sample") else {})})
                print(f"   {problem}: {len(specs) - index} scenario(s) not run", flush=True)
                break
            started = time.time()
            progress["current"] = {"name": record_key(spec), "index": index, "began": time.strftime("%H:%M:%S"), "at": started}
            write_json(out / PROGRESS, progress)
            print(f"-- {record_key(spec)}: {spec.get('summary', '')}", flush=True)
            debugged = debugged_now()
            now = client_module()
            if module is not None and now != module:
                print(f"   client presentation module changed: {module} -> {now} (a client restart loads the installed module;"
                      " presentation_reload.py again if this run is for a hot reload)", flush=True)
            module = now
            restore, problems = apply_tunes(a.tune)
            held, held_problems, held_notes = hold_tunes(a.held, held_known) if a.held else ([], [], [])
            problems += held_problems
            for note in held_notes:
                print(f"   {note}", flush=True)
            if problems:
                # a tunable that did not take: the scenario would score the code's defaults
                restore_tunes(restore)
                record = {"name": spec["name"], "summary": spec.get("summary", ""), "expect": spec.get("expect", "pass"),
                          "failures": problems, "verdict": "BLOCKED", "met": False, "log": [], "images": []}
            else:
                try:
                    record = sampled(watched, run_scenario, spec, out)
                    lost = (lost_tunes(restore + [c for c in held if c[2] == "held"])
                            + lost_held([c for c in held if c[2] == "held-process"], held_known or {}))
                    if lost and record.get("verdict") != "CRASHED":  # a dead game lost its tunables: still CRASHED
                        # scored partly on the defaults: not a result for the tunables the run is labelled with
                        record.update(failures=record["failures"] + lost, verdict="BLOCKED", met=False)
                finally:
                    restore_tunes(restore)
            record["module"] = now
            if spec.get("sample"):
                record["sample"] = spec["sample"]  # run --repeat: which pass of the suite
            if CLIENT != compare_client.CLIENT1:
                record["client"] = CLIENT
            if a.no_repair:
                record["no_repair"] = True
            if a.rejoin and any(s.partition(":")[0] == "rejoin" for s in spec.get("steps", [])):
                record["rejoin"] = a.rejoin
            if a.tune or a.held:
                record["tunes"] = list(a.tune) + list(a.held)
            if held:
                record["set_check"] = set_check_of(held)
                read = sorted(side for side, how in record["set_check"].items() if how == "tunables")
                if read and not warned_set_check:
                    warned_set_check = True
                    print(f"   warning: --held read the tunable list on the {' and '.join(read)} (a mod call at each "
                          f"scenario's start and end): {'no --held-process' if held_known is None else 'its TS3 start time cannot be read'}",
                          flush=True)
            if held_notes:
                record["held_reset"] = held_notes
            debugged = merge_debuggers(debugged, debugged_now())  # sampled at the scenario's start and end
            if debugged:
                record["debugger"] = dict(sorted(debugged.items()))
            records.append(record)
            phases = " ".join(f"{k} {v:.0f}" for k, v in record.get("timings", {}).items() if k != "total")
            print(f"   {record['verdict']} in {time.time() - started:.0f}s ({phases})" + (f": {'; '.join(record['failures'])}" if record["failures"] else ""), flush=True)
            if record.get("clients"):
                print("   per client: " + client_line(record["clients"]), flush=True)
            moved = {k: v for k, v in ((record.get("replay") or {}).get("change") or {}).items() if v != "no change"}
            if moved:
                print("   replay: " + "; ".join(f"{k} {v}" for k, v in moved.items()), flush=True)
            if record.get("doubles"):
                print("   two live drivers of one machine: " + "; ".join(record["doubles"]), flush=True)
            if record.get("stream"):
                print("   stream: " + stream_line(record), flush=True)
            if record.get("paused"):
                print("   " + record["paused"], flush=True)
            if record.get("start"):
                print("   start: " + "; ".join(record["start"]), flush=True)
            report(out, records)  # written after every scenario, so a stopped run still leaves one
            progress["current"] = None
            write_json(out / PROGRESS, progress)
            leave_out_if_polluted(None)
        else:
            died = health() if records else None  # a game that died in the last scenario: no next one would find it
            if died:
                mark_crashed(records[-1], died)
                print(f"   {died}: after the last scenario, which reads CRASHED", flush=True)
                report(out, records)
    leave_out_if_polluted(problem)
    if catcher:
        code, text = python("scripts/crash_catch.py", "status", timeout=120)
        print("   catcher: " + " | ".join(ln.strip() for ln in text.splitlines() if ln.strip())[:400], flush=True)
        # detach waits up to 5 min for a dump being written first ( G21): a shorter timeout would end this run
        # before its report
        code, text = python("scripts/crash_catch.py", "detach", catcher, timeout=420)
        print("   catcher: " + " | ".join(ln.strip() for ln in text.splitlines() if ln.strip())[:300], flush=True)
    python("scripts/sim_act.py", "--release", "all")
    if run_nofork(out):
        for r in records:
            r["nofork"] = True
        print(NOFORK_BANNER, flush=True)
    print(report(out, records))
    print("\n".join(changes(out, records)))
    print(f"report: {out / 'report.md'}")
    code = 6 if problem or died else 0 if all(r["met"] for r in records) else 5
    write_json(out / PROGRESS, dict(progress, current=None, finished=code))
    return code


if __name__ == "__main__":
    sys.exit(main())
