"""Card 55 section E executors: claim-sim, rust-env, load-simnav, navigate_astar and rust-evidence for scenarios.py.

One Card55Run per scenario run holds the run's scope in memory: the claimed Sim's wire and kernel ids, the installed
navigation epoch and generation, each walk's plan correlation and interval, and the counter readings that bracket the
run. Every reading comes from a Transport call; this module never writes a coordinate, a frame or a topology byte.
The only disk output is card55_live.Archive's sanitized rows (hashes, counts, millimetre errors, verdicts).

The client route follower that E3, E4 and E5's settle phase are read from is a run option (spec "rust": {"follower"}):
  boundary-route  the sealed section B source: each client's Rt/Vp|rt receipt and ClientBoundaryRoute progress
                  (SACS_RECORDER rust-route-plans / rust-route-client pages). ClientBoundaryRoute.CompleteLeg and Settle
                  have no production caller yet (OPEN-1, OPEN-2), so E4 and the settle wait stay BLOCKED on it.
  lay-path        the sync rework's piece 6 follower (copies lay the server's Rs path). The user prefers it (11-19); it
                  needs the referee's agreement and a repin if section B's wording changes, and piece 6 has no receipt
                  yet, so its Transport calls stay BLOCKED until both land.
A missing producer is BLOCKED with its reason, never a PASS by omission and never a FAIL.
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


import hashlib
import os
import re
import subprocess
import sys
import time
import uuid
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))
import card55_live as live  # noqa: E402
import routing_collectors as pages  # noqa: E402
import routing_evidence as evidence  # noqa: E402

Missing = evidence.Missing
FOLLOWERS = ("boundary-route", "lay-path")
SIDES = live.SIDES
GATES = ("E1", "E2", "E3", "E4", "E5")
SELECTOR = re.compile(r"simnav:([0-9a-f]{16}):(0|[1-9][0-9]*)")
SETTLED = ("Halted", "SeatingSettled")
# ClientBoundaryRoute.CompleteLeg/Settle (SimsOnlineServerHostMod/ClientBoundaryRoute.cs) have no production caller:
# the boundary-route receipt never advances. Flip only when a caller lands (OPEN-1 ruling + a live G0 trace).
BOUNDARY_ROUTE_PROGRESS = False
POLL_S = 1.0  # bounded waits poll once a second against a deadline


class Blocked(Exception):
    """A producer this run needs does not exist or did not answer: BLOCKED with the reason."""


def _result(verdict: str, reason: str) -> dict:
    return {"verdict": verdict, "reason": reason}


def fields(report: str) -> dict[str, str]:
    """key=value fields of a ;-separated report; the first field's key names the report (its value is the state)."""
    return live.counter_fields(report)


def natural(text: str, bits: int = 64) -> int:
    if not isinstance(text, str) or not re.fullmatch(r"0|[1-9][0-9]*", text):
        raise Missing("noncanonical count")
    return evidence.uint(int(text), bits)


def route_census(status: str) -> dict[str, str]:
    """The server's RouteRecorder census from SACS_RECORDER status: extras=...,route:installed:1/.../rustRoute:on/..."""
    # Read only the extras field: the status line carries many other fields this census does not depend on.
    found = re.findall(r"(?:^|;)extras=([^;]*)", status) if isinstance(status, str) else []
    if len(found) != 1:
        raise Missing("status has no single extras report")
    extras = found[0]
    route = next((part[len("route:"):] for part in extras.split(",") if part.startswith("route:")), None)
    if route is None:
        raise Missing("status has no route census")
    census = {}
    for item in route.split("/"):
        key, sep, value = item.partition(":")
        if not sep or key in census:
            raise Missing("malformed route census")
        census[key] = value
    return census


def _tokens(payload: str) -> tuple[str, dict[str, str]]:
    words = payload.split()
    named = dict(w.split("=", 1) for w in words[1:] if "=" in w)
    if not words or len(named) != len(words) - 1:
        raise Missing("navigate_astar:<Sim> [start=<selector>] goal=<selector>")
    return words[0], named


def preflight(spec: dict, root: Path = ROOT) -> str | None:
    """Why this scenario cannot start its Rust steps, found before the reset touches the games; None when it can."""
    items = spec.get("steps", []) + spec.get("after", [])
    rust = [i for i in items if i.partition(":")[0] in evidence.KINDS]
    if not rust:
        return None
    follower = (spec.get("rust") or {}).get("follower", "boundary-route")
    if follower not in FOLLOWERS:
        return f"unknown rust follower {follower!r} (one of {', '.join(FOLLOWERS)})"
    unpinned = sorted({w.split("=", 1)[-1] for i in rust for w in i.split() if w.endswith("_UNAVAILABLE")})
    if unpinned:
        return "G0 has not pinned the measured selectors: " + ", ".join(unpinned)
    lot = str(spec.get("lot", ""))
    if not re.fullmatch(r"[1-9][0-9]*", lot) or int(lot) >= 1 << 64:
        return "no measured lot id (spec \"lot\")"
    start = spec.get("start", "")
    if not (root / "scenarios" / "start" / f"{start}.json").is_file():
        return f"no fixed start file scenarios/start/{start}.json (card 55 section C.3; open question Q1)"
    hashes = set()
    for item in rust:
        kind, _, payload = item.partition(":")
        if kind == "navigate_astar":
            try:
                _, named = _tokens(payload)
            except Missing as error:
                return str(error)
            if set(named) - {"start", "goal"} or "goal" not in named:
                return f"navigate_astar takes start= and goal= only: {item}"
            for value in named.values():
                match = SELECTOR.fullmatch(value)
                if not match or int(match[2]) >= 1 << 32:
                    return f"not a measured simnav selector: {value}"
                hashes.add(match[1])
        elif kind == "rust-evidence" and payload not in GATES:
            return f"rust-evidence takes one of {', '.join(GATES)}: {item}"
        elif kind == "rust-env" and payload != "SIMSONLINE_RUST_ROUTE=1":
            return f"rust-env supports SIMSONLINE_RUST_ROUTE=1 only: {item}"
        elif kind == "load-simnav" and payload != "memory":
            return "load-simnav takes memory only (no topology file)"
    if len(hashes) > 1:
        return "selectors name different SIMNAV contents"
    return None


class Card55Run:
    """One scenario run's Rust route state. step() runs one item and returns {"verdict", "reason"}."""

    def __init__(self, spec: dict, transport, out: Path | None = None, clock=time.time_ns, sleep=time.sleep):
        self.t = transport
        self.clock, self.sleep = clock, sleep
        self.follower = (spec.get("rust") or {}).get("follower", "boundary-route")
        self.owner = (spec.get("rust") or {}).get("owner", "player1")
        self.lot = int(spec.get("lot", 0))
        self.wait_s = float((spec.get("watch") or {}).get("seconds", 120))
        self.archive = live.Archive(Path(out) / "card55") if out is not None else live.Archive()
        self.content_hash = next((SELECTOR.fullmatch(w.split("=", 1)[1])[1] for i in spec.get("steps", [])
                                  for w in i.split() if w.startswith(("start=simnav:", "goal=simnav:"))), None)
        self.run_id = uuid.uuid4().hex
        self.sim = self.wire = self.kernel = None
        self.epoch = self.generation = 0
        self.server_pid, self.boot_id = 0, ""
        self.start_ns = 0
        self.daemon_before = self.census_before = None
        self.census_before_ns = 0
        self.walks: list[dict] = []

    # ---- scope -------------------------------------------------------------------------------------------------------
    def scope(self, walk: dict | None = None, end_ns: int | None = None) -> dict:
        """The routing_evidence scope for one walk (or the whole run, keyed on the first walk's plan)."""
        first = walk or (self.walks[0] if self.walks else None)
        if first is None:
            raise Blocked("no navigate_astar walk ran")
        start = first["start_ns"] if walk else self.start_ns
        end = first["end_ns"] if walk else (end_ns or self.clock())
        return dict(run_id=self.run_id, boot_id=self.boot_id, server_pid=self.server_pid, epoch=self.epoch,
                    corr=first["corr"], actor=self.wire, generation=self.generation, owner=self.owner,
                    start_ns=start, end_ns=end, bridge_connected=True, claim_held=self.t.claim_held(),
                    instrument_ready=True, coverage_start_ns=start, coverage_end_ns=end, collector_status="complete")

    # ---- dispatch ----------------------------------------------------------------------------------------------------
    def step(self, kind: str, payload: str, context: dict | None = None) -> dict:
        handler = {"claim-sim": self.claim_sim, "rust-env": self.rust_env, "load-simnav": self.load_simnav,
                   "navigate_astar": self.navigate_astar, "rust-evidence": self.rust_evidence}.get(kind)
        if handler is None:
            raise ValueError(f"unknown Rust step {kind}")
        try:
            return handler(payload, context or {})
        except (Blocked, Missing, KeyError, TypeError, ValueError, OSError, subprocess.SubprocessError) as error:
            return _result("BLOCKED", f"{kind}: {error}")

    def _server(self, command: str) -> str:
        return self.t.request("server", command)

    def _status(self) -> dict[str, str]:
        status = fields(self._server("SACS_RECORDER rust-navigation-status"))
        if "rust-navigation-status" not in status:
            raise Missing("not a navigation status report")
        return status

    # ---- claim-sim:<Sim> ---------------------------------------------------------------------------------------------
    def claim_sim(self, sim: str, _context: dict) -> dict:
        if not sim or self.sim is not None:
            raise Blocked("claim-sim takes one Sim, once per run")
        if not self.t.claim_held():
            raise Blocked("the live stack claim is not held by this session")
        self.server_pid, self.boot_id = self.t.stack()
        evidence.uint(self.server_pid)
        if not self.server_pid or not self.boot_id:
            raise Blocked("server process identity unavailable")
        # The counters are read first and the interval starts after them: anything counted between the read and the
        # stamp falls inside the run's delta (counts more, never less).
        self.daemon_before = self.t.request("daemon", "FRONT_DOOR_STATE")
        self.census_before = route_census(self._server("SACS_RECORDER status"))
        self.census_before_ns = self.start_ns = self.clock()
        wire = self.t.claim(sim)
        if type(wire) is not int or not 0 < wire < 1 << 64:
            raise Blocked("claim returned no wire actor id")
        if self._status().get("mapping_checked") != "1":
            raise Blocked("the front door has not synced the ownership table (rust-navigation-sync)")
        kernel = self.t.kernel_for(wire)
        if type(kernel) is not int or not 0 < kernel < 1 << 32:
            raise Blocked("no checked kernel id for the claimed Sim")
        self.sim, self.wire, self.kernel = sim, wire, kernel
        return _result("PASS", f"claimed {sim}; ownership table synced; kernel id checked")

    # ---- rust-env:SIMSONLINE_RUST_ROUTE=1 ----------------------------------------------------------------------------
    def rust_env(self, payload: str, _context: dict) -> dict:
        if payload != "SIMSONLINE_RUST_ROUTE=1":
            raise Missing("unsupported route switch requirement")
        census = route_census(self._server("SACS_RECORDER status"))
        if census.get("installed") != "1":
            raise Blocked("the server's route seam (RouteCtor) is not installed")
        if census.get("rustRoute") != "on":
            raise Blocked("the server mod's Rust route switch is off (the front door sends rust-route-on)")
        totals = fields(self.t.request("daemon", "FRONT_DOOR_STATE"))
        for key in ("navigate_requests", "plans_served", "native_oracle_owned"):
            natural(totals.get(key, "unmeasured"))  # unmeasured blocks: no refusal census attached
        for side in SIDES:
            report = self.t.request(side, "SACS_RECORDER authority")
            if not report.startswith("authority;") or "hook=absent" in report:
                raise Blocked(f"{side}: no gameplay fork authority census (fork not installed)")
        return _result("PASS", "Rust route on; seam installed; daemon counters measured; both clients' fork census present")

    # ---- load-simnav:memory ------------------------------------------------------------------------------------------
    def load_simnav(self, payload: str, _context: dict) -> dict:
        if payload != "memory":
            raise Missing("load-simnav accepts no live topology filename")
        if self.wire is None:
            raise Blocked("claim-sim has not run")
        reply = fields(self._server(f"SACS_RECORDER rust-simnav-install {self.lot} {self.wire}"))
        if reply.get("rust-simnav-install") != "accepted":
            raise Blocked("install refused: " + reply.get("reason", "no reason"))
        if natural(reply["lot"]) != self.lot or natural(reply["actor"]) != self.wire:
            raise Missing("install answered for another lot or actor")
        if natural(reply["kernel_sim"], 32) != self.kernel:
            raise Missing("install's kernel id differs from the claim's")
        status = self._status()
        if status.get("installed") != "1" or status.get("mapping_checked") != "1":
            raise Blocked("snapshot not installed or ownership not synced")
        for key in ("epoch", "generation", "content_hash"):
            if status.get(key) != reply.get(key):
                raise Missing(f"status and install disagree on {key}")
        measured = status.get("provenance") == "measured"
        topology = dict(provenance=status.get("provenance"), storage=status.get("storage"),
                        # The builder is measured-only (RustLiveSimNavBuilder): no inferred partition or stairs exist
                        # there. A non-measured provenance reads as inferred.
                        inferred_partition=not measured, inferred_stairs=not measured,
                        cells=natural(status.get("cells", "")), portals=natural(status.get("portals", "")),
                        content_hash=status.get("content_hash"))
        for key in ("surfaces_measured", "portals_measured", "obstacles_and_locks_measured", "cross_lot_measured"):
            topology[key] = status.get(key) == "1"
        evidence.live_topology(topology)
        if self.content_hash is not None and topology["content_hash"] != self.content_hash:
            raise Blocked("installed SIMNAV content differs from the pinned selectors' (recapture, never rebind)")
        self.epoch, self.generation = natural(status["epoch"]), natural(status["generation"])
        if not self.epoch or not self.generation:
            raise Missing("uninstalled epoch or generation")
        return _result("PASS", f"measured memory-only SIMNAV installed: cells={topology['cells']} "
                               f"portals={topology['portals']} hash={topology['content_hash']}")

    # ---- navigate_astar:<Sim> [start=<selector>] goal=<selector> -----------------------------------------------------
    def plan_corrs(self) -> set[int]:
        """Correlations of the daemon's retained plans for the claimed Sim (Rt|<corr>|<actor>|...)."""
        dump = self.t.request("daemon", "RUST_ROUTE_PLANS")
        corrs = set()
        for line in dump.splitlines():
            parts = line.split("|")
            if len(parts) == 6 and parts[0] == "Rt" and parts[2] == str(self.wire):
                corrs.add(natural(parts[1]))
        return corrs

    def _until(self, what: str, probe, seconds: float):
        end = time.monotonic() + seconds
        while True:
            value = probe()
            if value:
                return value
            if time.monotonic() >= end:
                raise Blocked(f"{what} not seen within {seconds:g} s")
            self.sleep(POLL_S)

    def phase(self, side: str, corr: int) -> str:
        """The follower's WalkPhase for this walk on one client."""
        if self.follower == "lay-path":
            return self.t.lay_path_phase(side, corr, self.wire, self.epoch)
        receipt = pages.collect_pages(self.t.page(side, pages.CLIENT, corr, self.wire), pages.CLIENT,
                                      corr, self.wire, daemon_epoch=self.epoch)
        return receipt["WalkPhase"]

    def navigate_astar(self, payload: str, _context: dict) -> dict:
        sim, named = _tokens(payload)
        if sim != self.sim or not self.epoch:
            raise Blocked("claim-sim and load-simnav must run first for this Sim")
        if "start" in named and not self.t.at_cell(self.wire, named["start"]):
            raise Blocked("the Sim is not in the measured start cell")
        before = self.plan_corrs()
        start_ns = self.clock()
        self.t.go(sim, self.wire, named["goal"])
        new = self._until("a new Rust plan for the walk", lambda: self.plan_corrs() - before, 10.0)
        if len(new) != 1:
            raise Missing("more than one new plan for one walk")
        corr = new.pop()
        if not BOUNDARY_ROUTE_PROGRESS and self.follower == "boundary-route":
            raise Blocked(f"plan {corr} served, but the boundary-route follower has no progress producer "
                          "(ClientBoundaryRoute.CompleteLeg/Settle uncalled; OPEN-1, OPEN-2)")
        self._until("both clients settled", lambda: all(self.phase(s, corr) in SETTLED for s in SIDES), self.wait_s)
        walk = dict(corr=corr, start_ns=start_ns, end_ns=self.clock())
        self.walks.append(walk)
        # E5 is sampled now, after this walk settled and before the next starts (section C.1).
        walk["e5"] = live.collect_e5(self.scope(walk), lambda: {"position_mm": self.t.read_mm("server", self.sim)},
                                     lambda: self.read_clients(corr))
        return _result("PASS", f"walk {len(self.walks)} plan {corr} served and settled on both clients")

    def read_clients(self, corr: int) -> dict:
        """Both clients' millimetre positions and phases, read at once (threads), after both reads complete."""
        def one(side):
            return dict(position_mm=self.t.read_mm(side, self.sim), phase=self.phase(side, corr))
        with ThreadPoolExecutor(max_workers=len(SIDES)) as pool:
            return dict(zip(SIDES, pool.map(one, SIDES)))

    # ---- rust-evidence:E1..E5 ----------------------------------------------------------------------------------------
    def rust_evidence(self, gate: str, context: dict) -> dict:
        if gate not in GATES:
            raise Missing("unknown evidence selector; use exactly E1, E2, E3, E4 or E5")
        if gate in ("E1", "E2"):
            row = getattr(self, "e" + gate[1])()
        else:
            if not self.walks:
                raise Blocked("no navigate_astar walk ran")
            rows = [getattr(self, "e" + gate[1])(walk, context) for walk in self.walks]
            row = live.combine_walks(gate, rows)
        self.archive.put(gate, row)
        summary = ", ".join(f"{k}={v}" for k, v in sorted(row["counts"].items())[:8])
        return _result(row["verdict"], f"{gate} {row['verdict']}" + (f" ({summary})" if summary else ""))

    def _census_delta(self, key: str) -> int:
        after = route_census(self._server("SACS_RECORDER status"))
        return evidence.uint(natural(after[key]) - natural(self.census_before[key]))

    def e1(self) -> dict:
        scope = self.scope()
        trace, control = self.t.e1_trace(scope)
        fork = dict(native_hits=self._census_delta("fallbacks"), coverage_start_ns=self.census_before_ns,
                    coverage_end_ns=self.clock())
        return live.collect_e1(scope, trace, control, fork)

    def e2(self) -> dict:
        scope = self.scope()
        return live.collect_e2(scope, self.daemon_before, self.t.request("daemon", "FRONT_DOOR_STATE"),
                               self._census_delta("rust"))

    def e3(self, walk: dict, _context: dict) -> dict:
        scope = self.scope(walk)
        daemon = self.t.page("daemon", pages.PLAN, walk["corr"], self.wire)
        if self.follower == "lay-path":
            identities = {s: self.t.lay_path_identity(s, walk["corr"], self.wire, self.epoch) for s in SIDES}
            return collect_e3_identity(scope, daemon, identities)
        return live.collect_e3(scope, dict(daemon=daemon, **{s: self.t.page(s, pages.PLAN, walk["corr"], self.wire)
                                                             for s in SIDES}))

    def e4(self, walk: dict, context: dict) -> dict:
        scope = self.scope(walk)
        continuity = continuity_by_client(context.get("motions") or {}, self.t.continuity)
        if self.follower == "lay-path":
            receipts = {s: self.t.lay_path_progress(s, walk["corr"], self.wire, self.epoch) for s in SIDES}
            return collect_e4_receipts(scope, receipts, continuity)
        if not BOUNDARY_ROUTE_PROGRESS:
            return live._row("E4")  # no producer: BLOCKED, never a FAIL on an empty receipt
        return live.collect_e4(scope, {s: self.t.page(s, pages.CLIENT, walk["corr"], self.wire) for s in SIDES},
                               continuity)

    def e5(self, walk: dict, _context: dict) -> dict:
        return walk.get("e5") or live._row("E5")


# ---- follower-neutral collectors ---------------------------------------------------------------------------------
def collect_e3_identity(scope: dict, daemon_request, identities: dict) -> dict:
    """E3 from a follower that carries the plan identity with the walk instead of the Rt/Vp|rt frames (piece 6's
    lay-path): each client's {epoch, path_hash, total_cost_mm, leg_count} against the daemon's own Rt frame. Section B
    asks for the same path_hash and total_cost_mm on both clients and the daemon; this is that comparison."""
    failed, hashes = False, {}
    try:
        evidence.scope_check(scope, live._observation(scope, {}))
        receipt = pages.collect_pages(daemon_request, pages.PLAN, scope["corr"], scope["actor"],
                                      daemon_epoch=scope["epoch"])
        digest, cost, points = evidence.parse_plan(receipt["frames"], scope["corr"], scope["actor"])
        reference = (digest, cost, len(points))
        hashes["daemon"] = digest
        for side in SIDES:
            item = identities[side]
            if evidence.uint(item["epoch"]) != scope["epoch"]:
                raise Missing(f"{side} identity from another epoch")
            if not re.fullmatch(r"[0-9a-f]{16}", item["path_hash"]):
                raise Missing(f"{side} invalid path hash")
            hashes[side] = item["path_hash"]
            failed |= (item["path_hash"], evidence.uint(item["total_cost_mm"]),
                       evidence.uint(item["leg_count"], 32)) != reference
        return live._row("E3", "FAIL" if failed else "PASS", hashes=hashes, counts=dict(legs=reference[2]))
    except (Missing, KeyError, TypeError, ValueError):
        return live._row("E3", "FAIL" if failed else "BLOCKED", hashes=hashes)


def collect_e4_receipts(scope: dict, receipts: dict, continuity: dict) -> dict:
    """E4 from normalized per-client progress receipts {epoch, legs_total, executed_leg_indices,
    invalid_progressions}: the shape a lay-path follower reports; the boundary-route pages reduce to the same."""
    invalid = False
    try:
        data, legs = {}, None
        for side in SIDES:
            receipt = receipts[side]
            if evidence.uint(receipt["epoch"]) != scope["epoch"]:
                raise Missing("receipt from another epoch")
            total = evidence.uint(receipt["legs_total"], 32)
            if legs is not None and total != legs:
                raise Missing("different declared leg counts")
            legs = total
            indices = receipt["executed_leg_indices"]
            if not isinstance(indices, list) or any(type(i) is not int for i in indices):
                raise Missing("invalid executed leg indices")
            data[side] = dict(executed_leg_indices=indices)
            for metric in evidence.METRICS:
                data[side][metric] = evidence.uint(continuity[side][metric])
            invalid |= evidence.uint(receipt["invalid_progressions"]) > 0
        data["leg_count"] = legs
        row = live._score("E4", scope, data, counts={
            f"{side}_{metric}": data[side][metric] for side in SIDES for metric in evidence.METRICS} | {"legs": legs})
        if invalid:
            row["verdict"] = "FAIL"
        return row
    except (Missing, KeyError, TypeError, ValueError):
        return live._row("E4", "FAIL") if invalid else live._row("E4")


def continuity_by_client(motions: dict, score) -> dict:
    """Section D's four counters per client window from the scenario's motion runs ({"1": dir, "2": dir}); score is
    route_continuity-shaped (server samples, {client: samples}) -> counters. A missing window is Missing."""
    result = {}
    for side, player in zip(SIDES, ("1", "2")):
        if player not in motions:
            raise Missing(f"no motion watch for {side}")
        counters = score(Path(motions[player]))
        if any(counters.get(m) is None for m in evidence.METRICS):
            raise Missing(f"{side} continuity counters unmeasured")
        result[side] = counters
    return result


# ---- production transport ----------------------------------------------------------------------------------------
class LiveTransport:
    """Reads and actions on the live stack. Construct only under a held stack claim; nothing here boots anything.

    Missing producers raise Blocked with what is missing (see internal documentation, 2026-11-19 update).
    """

    def __init__(self, endpoints: dict | None = None, session: str | None = None):
        import compare_client
        self.endpoints = endpoints or {"server": "127.0.0.1:34029", "daemon": "127.0.0.1:34029",
                                       "client1": compare_client.client_n("1"), "client2": compare_client.client_n("2")}
        self.session = session or os.environ.get("OMO_SIMSONLINE_SESSION", "")

    def request(self, side: str, command: str) -> str:
        return live.Admin(self.endpoints[side]).request(command)

    def page(self, side: str, kind: str, corr: int, actor: int):
        admin = live.Admin(self.endpoints[side])
        return admin.plan_page(corr, actor, client=side != "daemon", progress=kind == pages.CLIENT)

    def claim_held(self) -> bool:
        import stack_claim
        held = stack_claim.current()
        return bool(re.fullmatch(r"[0-9a-f]{8}", self.session)) and bool(held) and held[0] == self.session

    def stack(self) -> tuple[int, str]:
        try:
            import crash_catch as cc
        except ImportError:
            cc = _MirrorProcFallback
        entry = cc.load_state().get("server") or {}
        pid = entry.get("game")
        if not pid or entry.get("mode") != "trace" or not cc.alive(pid):
            raise Blocked("no cdb trace attached to the server TS3W (crash_catch trace server, positive control first)")
        return int(pid), f"pid{pid}-since-{entry.get('since', '?')}"

    def claim(self, sim: str) -> int:
        out = subprocess.run([sys.executable, "scripts/simctl.py", "claim", sim], cwd=ROOT, capture_output=True,
                             text=True, timeout=60, stdin=subprocess.DEVNULL).stdout
        found = re.findall(r"^claimed for player \S+: .+ \(([0-9]+)\)$", out, re.M)
        if len(found) != 1:
            raise Blocked("simctl claim did not claim exactly one Sim")
        return int(found[0])

    def kernel_for(self, wire: int) -> int:
        raise Blocked("no read-only verb reports the claimed Sim's kernel id (the mod's Sync.KernelFor is internal)")

    def at_cell(self, wire: int, selector: str) -> bool:
        raise Blocked("no in-memory verb tests the Sim's measured start cell")

    def go(self, sim: str, wire: int, selector: str) -> None:
        raise Blocked("no in-memory verb resolves a simnav goal selector to an owner-gated GoHere")

    def read_mm(self, side: str, sim: str) -> list[int]:
        import sim_compare
        return sim_compare.read_mm(self.endpoints[side], sim)

    def e1_trace(self, scope: dict) -> tuple[dict, dict]:
        """The whole-run cdb trace and its positive-control window (the same trace before the run's first step)."""
        try:
            import crash_catch as cc
        except ImportError:
            cc = _MirrorProcFallback
        entry = cc.load_state().get("server") or {}
        if entry.get("game") != scope["server_pid"] or not entry.get("log"):
            raise Blocked("the server trace is gone or on another process")
        log = Path(entry["log"])
        if "SIMSONLINE-CLEARED" not in log.read_text(encoding="utf-8", errors="replace"):
            # The whole-run window ends here: clear the breakpoints (the CLEARED mark closes the interval), then detach.
            gone, why = cc.detach_trace(entry)
            if not gone:
                raise Blocked("server trace did not detach: " + why)
            state = cc.load_state()
            if (state.get("server") or {}).get("cdb") == entry.get("cdb"):
                state.pop("server")
                cc.save_state(state)
        trace = live.parse_cdb_trace(log.read_text(encoding="utf-8"))
        import card55_local
        _recipe_path = card55_local.recipe(card55_local.E1_RECIPE)
        if not _recipe_path.is_file():
            raise Blocked(f"Card 55 recipe missing: {_recipe_path}")
        recipe = _recipe_path.read_bytes()
        trace.update(server_pid=scope["server_pid"], boot_id=scope["boot_id"], instrument="cdb-trace-server",
                     configuration=hashlib.sha256(recipe).hexdigest())
        control = dict(trace, coverage_end_ns=scope["start_ns"] - 2_000_000)
        return trace, control

    def continuity(self, run: Path) -> dict:
        import route_continuity as rc
        sides = rc.load_motion_run(run, rc.first_sim_id(run))
        return rc.score(sides.get("server", []), {"client": sides.get("client", [])}, rc.has_fields(run))

    def lay_path_phase(self, side: str, corr: int, actor: int, epoch: int) -> str:
        raise Blocked("piece 6's lay-path follower has no phase receipt yet (referee question R1 pending)")

    def lay_path_identity(self, side: str, corr: int, actor: int, epoch: int) -> dict:
        raise Blocked("piece 6's lay-path follower carries no plan identity yet (referee question R1 pending)")

    def lay_path_progress(self, side: str, corr: int, actor: int, epoch: int) -> dict:
        raise Blocked("piece 6's lay-path follower has no leg receipt yet (referee question R1 pending)")

