"""Card 55 G0 diagnostics. Compile offline; live commands require an explicit held claim.

Raw bridge reports and native geometry stay in memory. The sole coordinate output is
the explicitly requested scenarios/start/<lot>.json placement, four numbers per Sim.
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
import hashlib
import json
import math
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))
import card55_local  # noqa: E402  (scripts/ is on the path from here)

# The G0 road recipe's nine labels, in order. The addresses are local-only (card55_local.py): None until the recipe
# is installed, and the scoped trace refuses to attach without them.
LABELS = ("search", "goals", "plan", "plan_from", "replan", "replan_from", "icall", "pass1", "pass2")
BREAKPOINTS = card55_local.load(card55_local.G0_RECIPE, LABELS)
DECISIONS = frozenset((
    "off", "fallback.unmappedOption", "fallback.inPump", "fallback.notYielding.road",
    "fallback.notYielding.onFoot", "fallback.noDaemon", "fallback.timeout",
    "fallback.RoadDataMissing", "fallback.LaneKindUnsupported", "fallback.BadRequest", "served", "servedNone",
))
SCALARS = frozenset("""
done verdict cleanup attempted inserted pathsAdded customFailures portalFailures exceptions
doRouteReturned doRouteResult timeout simPaths portalPaths traversalPaths unknownPaths
events throughActions traversalActions animateActions otherActions replans
insertedExitErrorMm exitErrorMm entryErrorMm startTick endTick calls found notFound
selector yielding structuralOnly acceptance teleportExcluded
""".split())


def fields(text: str) -> dict[str, str]:
    if not text or "\n" in text or "\r" in text:
        raise ValueError("invalid scalar receipt")
    result = {}
    for part in text.split(";"):
        key, sep, value = part.partition("=")
        if not sep or not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]*", key) or key in result or not value:
            raise ValueError("invalid or duplicate scalar field")
        result[key] = value
    return result


def natural(value: str) -> int:
    if not re.fullmatch(r"0|[1-9][0-9]*", value):
        raise ValueError("noncanonical count")
    return int(value)


def sanitize(receipt: dict[str, str]) -> dict[str, str]:
    clean = {}
    for key, value in receipt.items():
        if key not in SCALARS:
            continue
        if key in ("verdict", "cleanup"):
            if not re.fullmatch(r"[A-Za-z_]+", value):
                raise ValueError("non-scalar verdict")
        elif value not in ("true", "false", "True", "False"):
            if key.endswith("ErrorMm") and value == "-1":
                pass  # Not measured, never a successful error measurement.
            else:
                natural(value)
        clean[key] = value
    return clean


def portal_verdict(r: dict[str, str], mode: str) -> str:
    if r.get("done") != "true":
        return "BLOCKED"
    verdict = r.get("verdict", "")
    if verdict in ("UNSUPPORTED_STAIR_INSERT_REFUSED", "UNSUPPORTED_STAIR_TYPED_PATH_SHAPE"):
        return verdict
    if r.get("cleanup") != "complete":
        return "BLOCKED"
    if mode == "shape" and verdict == "STRUCTURAL_ONLY":
        n = int(r.get("attempted", "0"))
        return verdict if n > 0 and all(int(r.get(k, "-1")) == n for k in ("inserted", "pathsAdded")) and all(
            r.get(k) == "0" for k in ("customFailures", "portalFailures", "exceptions")) else "BLOCKED"
    if mode == "prepare" and verdict == "PREPARATION_COMPLETE":
        return verdict
    if mode != "traverse" or verdict != "NATIVE_COMPLETION_OBSERVED":
        return "BLOCKED"
    if (r.get("doRouteReturned") != "True" or r.get("doRouteResult") != "True"
            or r.get("timeout") != "False" or r.get("unknownPaths") != "0" or r.get("replans") != "0"
            or not 0 <= int(r.get("exitErrorMm", "-1")) <= 100
            or int(r.get("traversalActions", "0")) + int(r.get("animateActions", "0")) == 0):
        return "BLOCKED"
    return verdict


def census(text: str) -> tuple[str, dict[str, int]]:
    match = re.search(r"(?:^|;)roadPlace:(on|off)((?:,[A-Za-z0-9.]+:[A-Za-z0-9-]+)*)(?:;|$)", text)
    if not match:
        raise ValueError("missing road census")
    counts, seen = {}, set()
    for part in match[2].lstrip(",").split(","):
        if not part:
            continue
        key, value = part.split(":")
        if key in seen:
            raise ValueError("duplicate census counter")
        seen.add(key)
        if key == "lastHash":
            if not re.fullmatch(r"-|[0-9a-f]{16}", value):
                raise ValueError("bad answer hash")
            continue
        counts[key] = natural(value)
    if not DECISIONS <= counts.keys():
        raise ValueError("incomplete fallback census")
    return match[1], counts


def daemon_counts(text: str) -> dict[str, int]:
    counts = {}
    for key in ("road_place_requests", "road_place_served"):
        found = re.findall(r"(?:^|[|;,\s])" + key + r"=(0|[1-9][0-9]*)(?=$|[|;,\s])", text)
        if len(found) != 1:
            raise ValueError("missing or duplicate daemon counter")
        counts[key] = int(found[0])
    return counts


def delta(before: dict[str, int], after: dict[str, int]) -> dict[str, int]:
    if set(before) - set(after):
        raise ValueError("counter disappeared")
    result = {key: value - before.get(key, 0) for key, value in after.items()}
    if any(type(value) is not int or value < 0 for value in result.values()):
        raise ValueError("counter reset")
    return result


def road_verdict(phase: str, selector: int, calls: int, hits: dict, mod: dict, daemon: dict) -> str:
    if set(hits) != set(BREAKPOINTS) or calls <= 0:
        return "BLOCKED"
    if any(hits[k] for k in ("plan", "plan_from", "replan", "replan_from")):
        return "FAIL"  # Unrelated producers contaminate the isolated yielding probe.
    searches = hits["pass1"] + hits["pass2"]
    if phase == "p2":
        if selector == 4:
            return "PASS" if hits["icall"] == calls and searches == hits["search"] == hits["goals"] == 0 else "FAIL"
        # Every call must run at least one search: a call that reached the ICALL without searching did not
        # exercise the Road search this phase measures (the report's "needs a caller that exercises a search").
        if hits["icall"] < calls or searches < calls or hits["search"] < calls or hits["goals"] < calls:
            return "BLOCKED"
        return "PASS" if hits["icall"] == calls and searches == hits["search"] == hits["goals"] else "FAIL"
    if phase != "p3" or selector not in (0, 4):
        return "BLOCKED"
    if any(hits.values()) or any(v for k, v in mod.items() if k.startswith("fallback.")):
        return "FAIL"
    if set(daemon) != {"road_place_requests", "road_place_served"}:
        return "BLOCKED"
    return "PASS" if (mod.get("served", 0) + mod.get("servedNone", 0) == calls
                      and mod.get("off", 0) == 0
                      and daemon["road_place_requests"] == daemon["road_place_served"] == calls) else "FAIL"


def trace_counts(text: str, token: str) -> dict[str, int]:
    found = re.findall(r"^G0-SNAP " + re.escape(token) + r" (.+)$", text, re.M)
    if len(found) != 1:
        raise ValueError("missing or duplicate trace snapshot")
    result = {}
    for part in found[0].split():
        key, sep, value = part.partition("=")
        if not sep or key in result:
            raise ValueError("malformed trace counter")
        result[key] = natural(value)
    if set(result) != set(BREAKPOINTS):
        raise ValueError("incomplete breakpoint coverage")
    return result


def trace_events(text: str, before: str, after: str, counts: dict) -> list[str]:
    begin = re.search(r"^G0-SNAP " + re.escape(before) + r" .+$", text, re.M)
    end = re.search(r"^G0-SNAP " + re.escape(after) + r" .+$", text, re.M)
    if not begin or not end or begin.end() >= end.start():
        raise ValueError("trace window unavailable")
    window = text[begin.end():end.start()]
    events = re.findall(r"^G0-HIT (\S+)$", window, re.M)
    if any(name not in BREAKPOINTS for name in events) or {
            name: events.count(name) for name in BREAKPOINTS} != counts:
        raise ValueError("trace events and counters disagree")
    return events


def road_control_chain(events: list[str]) -> bool:
    # The counter cross-check already excludes unrelated entry 1-4 producers.
    # Require the actual ordered ICALL -> pass -> goals -> worker-search chain.
    return any(events[i:i+4] in (["icall", "pass1", "goals", "search"],
                                ["icall", "pass2", "goals", "search"])
               for i in range(len(events) - 3))


def start_spot(text: str) -> list[float]:
    values = [float(v) for v in text.split(",")]
    if len(values) != 4 or not all(math.isfinite(v) for v in values):
        raise ValueError("start requires x,z,fx,fz")
    if abs(math.hypot(values[2], values[3]) - 1) > 0.01:
        raise ValueError("start facing is not a unit direction")
    return values


def selector_pin(r: dict[str, str]) -> dict:
    if not {"content_hash", "epoch", "generation", "cell", "lot", "room", "actor", "level"} <= r.keys():
        raise ValueError("incomplete capture identity")
    if not re.fullmatch(r"[0-9a-f]{16}", r.get("content_hash", "")):
        raise ValueError("measured content hash required")
    pin = {"content_hash": r["content_hash"]}
    for key in ("epoch", "generation", "cell", "lot", "room", "actor"):
        pin[key] = natural(r[key])
        if pin[key] >= (2**32 if key == "cell" else 2**64):
            raise ValueError("selector identity exceeds wire width")
    if any(pin[k] == 0 for k in ("epoch", "generation", "actor")):
        raise ValueError("uninstalled selector")
    if not re.fullmatch(r"0|-?[1-9][0-9]*", r["level"]):
        raise ValueError("invalid level")
    pin["level"] = int(r["level"])
    if not -32768 <= pin["level"] <= 32767:
        raise ValueError("selector level exceeds wire width")
    return pin


def stable_selector(pin: dict) -> str:
    """Only content_hash and cell are stable selector fields.

    A cell ordinal is meaningful only inside the exact canonical SIMNAV content
    identified by its hash. Encode/Order has no epoch, generation or actor input
    (RustLiveSimNavBuilder.cs:130-215). A later boot must match that hash before
    resolving the ordinal; changed content requires recapture, never rebinding.
    Room/lot/level and all per-boot identities remain diagnostic metadata.
    """
    return f"simnav:{pin['content_hash']}:{pin['cell']}"


def write_capture_pair(start: Path, start_data: bytes, scenario: Path, scenario_data: bytes) -> None:
    """Stage both new files and rollback copies before replacing either destination.

    Each rename is atomic; a handled failure between the renames rolls back the
    first. This is an exception-safe two-file publication, not a filesystem-wide
    transaction across process death.
    """
    paths = (start, scenario)
    originals = [path.read_bytes() if path.exists() else None for path in paths]
    staged, backups, temporary, replaced = [], [], [], []
    try:
        for path, data, original in zip(paths, (start_data, scenario_data), originals):
            path.parent.mkdir(parents=True, exist_ok=True)
            pair = []
            for payload in (data, original):
                if payload is None:
                    pair.append(None)
                    continue
                temp = path.with_name(f".{path.name}.{uuid.uuid4().hex}.tmp")
                temporary.append(temp)
                with temp.open("xb") as out:
                    out.write(payload)
                    out.flush()
                    os.fsync(out.fileno())
                pair.append(temp)
            staged.append(pair[0])
            backups.append(pair[1])
        try:
            for index, path in enumerate(paths):
                os.replace(staged[index], path)
                replaced.append(index)
        except BaseException:
            for index in reversed(replaced):
                if backups[index] is None:
                    paths[index].unlink()
                else:
                    os.replace(backups[index], paths[index])
            raise
    finally:
        for temp in temporary:
            temp.unlink(missing_ok=True)


def publish_capture(root: Path, actor: str, name: str, role: str, receipt: dict[str, str]) -> dict:
    pin = selector_pin(receipt)
    selector = stable_selector(pin)
    start = root / "scenarios/start" / (name + ".json")
    if role == "start" and start.exists():
        raise FileExistsError("start placement already exists")
    scenario_path = root / "scenarios/routed_walk_multiroom_lot.json"
    scenario = json.loads(scenario_path.read_text(encoding="utf-8"))
    walks = [i for i, step in enumerate(scenario["steps"]) if step.startswith("navigate_astar:")]
    if len(walks) != 2:
        raise ValueError("capture needs the two Card 55 scenario walks")
    locations = {
        "start": (walks[0], "start", "measured_library_start_UNAVAILABLE"),
        "upper": (walks[0], "goal", "measured_upper_cell_UNAVAILABLE"),
        "cross-lot": (walks[1], "goal", "measured_cross_lot_cell_UNAVAILABLE"),
    }
    index, key, unavailable = locations[role]
    tokens = scenario["steps"][index].split()
    if tokens.count(f"{key}={unavailable}") != 1:
        raise ValueError("selector already pinned or scenario grammar changed")
    # Existing selectors must refer to the same immutable content. No persisted
    # epoch/generation/actor is needed or allowed to decide this.
    for walk in walks:
        for token in scenario["steps"][walk].split():
            if not token.startswith(("start=", "goal=")):
                continue
            value = token.partition("=")[2]
            if value in {item[2] for item in locations.values()}:
                continue
            match = re.fullmatch(r"simnav:([0-9a-f]{16}):(0|[1-9][0-9]*)", value)
            if not match or match[1] != pin["content_hash"] or int(match[2]) >= 2**32:
                raise ValueError("selector set belongs to different or unknown content")
    if role == "start":
        spot = start_spot(receipt["spot"])
        start_data = (json.dumps({actor: spot}, indent=2, allow_nan=False) + "\n").encode("utf-8")
        scenario["start"] = name
    else:
        if scenario.get("start") != name:
            raise ValueError("capture the matching start before its goals")
        start_data = start.read_bytes()
    tokens[tokens.index(f"{key}={unavailable}")] = f"{key}={selector}"
    scenario["steps"][index] = " ".join(tokens)
    scenario_data = (json.dumps(scenario, indent=2, allow_nan=False) + "\n").encode("utf-8")
    # Boot-specific metadata is observed evidence, never a tracked selector.
    evidence = root / ".omo/run/act" / ("G0Capture_" + uuid.uuid4().hex + ".json")
    evidence.parent.mkdir(parents=True, exist_ok=True)
    evidence.write_text(json.dumps(dict(verdict="MEASURED", role=role, runtime_pin=pin), sort_keys=True) + "\n",
                        encoding="utf-8")
    write_capture_pair(start, start_data, scenario_path, scenario_data)
    return dict(verdict="CAPTURED", role=role, selector=selector, runtime_pin=pin,
                evidence=str(evidence.relative_to(root)), scenario_pinned=True)


def lot_selector(value: str) -> str:
    if value == "world":
        return value
    if natural(value) >= 2**64:
        raise ValueError("lot exceeds wire width")
    return value


def require_claim() -> None:
    import stack_claim
    session = os.environ.get("OMO_SIMSONLINE_SESSION", "")
    if not re.fullmatch(r"[0-9a-f]{8}", session):
        raise ValueError("set the live lane's explicit OMO_SIMSONLINE_SESSION")
    held = stack_claim.parse(stack_claim.CLAIM.read_text(encoding="utf-8"))
    if not held or held[0] != session:
        raise ValueError("the live lane must hold its claim before running a probe")


def source(args, mode: str, selector: int = 0) -> str:
    from card55_g0_probes import COMMON, PORTAL, ROAD, CAPTURE
    template = COMMON + (PORTAL if args.command == "portal" else ROAD if args.command == "road" else CAPTURE)
    lot = json.dumps(str(args.lot))
    entry = (f"Begin({json.dumps(args.actor)}, {lot}, {json.dumps(args.kind)}, "
             f"{args.ordinal}, {args.slot}, {args.seconds}f, {json.dumps(mode)})"
             if args.command == "portal" else
             f"Begin({json.dumps(args.actor)}, {lot}, {selector}, {args.calls}, {args.distance}f)"
             if args.command == "road" else
             f"Capture({json.dumps(args.actor)}, {lot}, {str(args.role == 'start').lower()})")
    return template + """
public class G0Launch {
    public static string Run() {
        return G0Probe.%s;
    }
}
public class G0Status {
    public static string Run() { return G0Probe.Report(); }
}
""" % entry


def compile_probe(args, mode: str, selector: int = 0) -> Path:
    import check_mod_runtime
    import probe_source
    import stack_claim
    if stack_claim.refuse_build(False):
        raise ValueError("another session holds a quiet build window")
    path = probe_source.write(ROOT / ".omo/run/act/G0Probe.cs", source(args, mode, selector))
    dll = path.with_suffix(".dll")
    result = subprocess.run(check_mod_runtime.runtime_args(dll) + [str(path)], stdin=subprocess.DEVNULL,
                            capture_output=True, encoding="utf-8", errors="replace", timeout=120)
    if result.returncode:
        # Compiler diagnostics contain authored source/API names, never live data.
        sys.stderr.write(result.stdout + result.stderr)
        raise ValueError("probe TS3 runtime compile failed")
    return dll


def run_probe(args, dll: Path) -> dict[str, str]:
    import bridgectl
    with bridgectl.connect(args.endpoint, args.seconds + 30) as sock, bridgectl.blob_lock(sock):
        if bridgectl.chunk_upload(sock, str(dll), "G0 probe") is None:
            raise ValueError("probe upload refused")
        end = time.monotonic() + args.seconds + 30
        response = bridgectl.roundtrip(sock, "DIAG_RUN " + bridgectl.hexenc("G0Launch"))
        first_status = True
        while True:
            report = response.get("report", "")
            # Parse directly so duplicate fields cannot disappear in a tolerant parser.
            if not response.get("ok") or response.get("accepted") is False or "run=" not in report:
                raise ValueError("unexpected probe envelope; cleanup may be required")
            r = fields(report.partition("run=")[2])
            if r.get("done") == "true":
                return r
            if r.get("done") != "false" or time.monotonic() >= end:
                raise ValueError("probe not complete; cleanup required before another probe")
            # Starting a cached DLL must start fresh work on every invocation, while
            # status reads must never queue it again (P2 and P3 reuse the same DLL).
            response = bridgectl.roundtrip(sock, "DIAG_RUN " + bridgectl.hexenc("G0Status")
                                          if first_status else "DIAG_RERUN")
            first_status = False


def command_report(endpoint: str, command: str) -> str:
    import bridgectl
    with bridgectl.connect(endpoint, 30) as sock:
        response = bridgectl.roundtrip(sock, command)
    if not response.get("ok") or response.get("accepted") is False:
        raise ValueError("bridge command refused")
    return response.get("report") or response.get("raw") or ""


class ScopedTrace:
    """Counter-only breakpoints, one server PID/configuration across P2 and P3.

    Existing crash_catch owns attach/detach. No argument/register dumps or stack
    dumps are requested by these breakpoints. Counters never disable themselves.
    """
    def __init__(self, pid: int):
        self.pid = pid
        self.entry = None
        self.log = None
        self.configuration = hashlib.sha256(json.dumps(BREAKPOINTS, sort_keys=True).encode()).hexdigest()

    def __enter__(self):
        try:
            import crash_catch as cc
        except ImportError:
            cc = _MirrorProcFallback
        state = cc.load_state()
        if cc.side_game("server", state) != self.pid:
            raise ValueError("requested PID is not the current unattached server")
        local = card55_local.recipe(card55_local.G0_RECIPE)
        if None in BREAKPOINTS.values():
            raise ValueError(f"Card 55 addresses not installed ({local})")
        configured = cc.parse_breakpoints(local.read_text(encoding="utf-8"))
        if ({bp["label"]: bp["where"][2:] for bp in configured} != BREAKPOINTS
                or any(bp["commands"] for bp in configured)):
            raise ValueError("local breakpoint recipe and scalar instrument disagree")
        folder = ROOT / ".omo/run/act" / ("g0-trace-" + uuid.uuid4().hex)
        folder.mkdir(parents=True)
        control, self.log = folder / "control.txt", folder / "trace.log"
        cc.write_control(control, ".echo G0-IDLE; g")
        # Unlike the generic catcher, this memory-only instrument never writes a
        # process dump: it would contain live topology/road bytes and coordinates.
        lines = ["sxi ld", "sxi ud", "sxi ct", "sxi et", "sxi out", "sxn *",
                 'sxe -c "gn" -c2 ".echo G0-CRASH; qd" av',
                 f'sxe -c "$$><{control.as_posix()}" bpe', "r $t0=0", "lm m TS3W"]
        for i, address in enumerate(BREAKPOINTS.values(), 1):
            name = list(BREAKPOINTS)[i - 1]
            lines += [f"r $t{i}=0",
                      f'bp{i} 0x{address} "r $t{i}=@$t{i}+1; .echo G0-HIT {name}; gc"']
        lines += [".echo SIMSONLINE-ATTACHED", "g"]
        script = folder / "trace.txt"
        script.write_text("\n".join(lines) + "\n", encoding="ascii")
        proc, ok, text = cc.launch_cdb(self.pid, script, self.log)
        self.entry = dict(game=self.pid, cdb=proc.pid, log=str(self.log), control=str(control), mode="trace")
        state["server"] = dict(self.entry, since=time.strftime("%Y-%m-%d %H:%M:%S"),
                               breakpoints=list(BREAKPOINTS), cap=0)
        cc.save_state(state)
        if not ok or not re.search(r"^00400000\s+[0-9a-fA-F]+\s+TS3W\b", text, re.M):
            self.__exit__(None, None, None)
            raise ValueError("debugger attach or TS3W module base verification failed")
        return self

    def snapshot(self) -> dict:
        try:
            import crash_catch as cc
        except ImportError:
            cc = _MirrorProcFallback
        token = uuid.uuid4().hex
        fmt = " ".join(f"{name}=%u" for name in BREAKPOINTS)
        values = ", ".join(f"@$t{i}" for i in range(1, len(BREAKPOINTS) + 1))
        cc.write_control(Path(self.entry["control"]),
                         f'.printf "G0-SNAP {token} {fmt}\\n", {values}; g')
        if not cc.break_in(self.pid) or not cc.wait_for(
                lambda: bool(re.search(r"^G0-SNAP " + token + " ", self.log.read_text(encoding="utf-8", errors="replace"), re.M)), 15):
            raise ValueError("trace snapshot unavailable")
        if not cc.alive(self.pid) or not cc.alive(self.entry["cdb"]):
            raise ValueError("trace process exited")
        counts = trace_counts(self.log.read_text(encoding="utf-8", errors="replace"), token)
        self.last_token = token
        return counts

    def __exit__(self, *_):
        try:
            import crash_catch as cc
        except ImportError:
            cc = _MirrorProcFallback
        if self.entry:
            gone, _ = cc.detach_trace(self.entry)
            if not gone:
                raise ValueError("G0 debugger still attached; cleanup required")
            state = cc.load_state()
            if state.get("server", {}).get("cdb") == self.entry["cdb"]:
                state.pop("server")
                cc.save_state(state)


def road(args, dlls: dict[int, Path]) -> dict:
    initial, _ = census(command_report(args.endpoint, "SACS_RECORDER road-place-census"))
    rows = []
    try:
        with ScopedTrace(args.server_pid) as trace:
            for phase, enabled in (("p2", "0"), ("p3", "1")):
                switch = command_report(args.endpoint, "SACS_RECORDER rust-road-place-on " + enabled)
                if census(switch)[0] != ("on" if enabled == "1" else "off"):
                    raise ValueError("road switch not applied")
                for selector in args.selectors:
                    _, before = census(command_report(args.endpoint, "SACS_RECORDER road-place-census"))
                    daemon_before = daemon_counts(command_report(args.endpoint, "FRONT_DOOR_STATE"))
                    trace_before = trace.snapshot()
                    before_token = trace.last_token
                    start_ns = time.time_ns()
                    r = run_probe(args, dlls[selector])
                    end_ns = time.time_ns()
                    hits = delta(trace_before, trace.snapshot())
                    events = trace_events(trace.log.read_text(encoding="utf-8", errors="replace"),
                                          before_token, trace.last_token, hits)
                    _, after = census(command_report(args.endpoint, "SACS_RECORDER road-place-census"))
                    daemon_after = daemon_counts(command_report(args.endpoint, "FRONT_DOOR_STATE"))
                    mod, daemon = delta(before, after), delta(daemon_before, daemon_after)
                    clean = sanitize(r)
                    verdict = road_verdict(phase, selector, args.calls, hits, mod, daemon)
                    if phase == "p2" and selector == 0 and not road_control_chain(events):
                        verdict = "BLOCKED"
                    if (r.get("verdict") != "ROAD_CALLS_COMPLETE" or r.get("cleanup") != "complete"
                            or r.get("yielding") != "true" or r.get("calls") != str(args.calls)
                            or r.get("selector") != str(selector)):
                        verdict = "BLOCKED"
                    rows.append(dict(phase=phase, selector=selector, verdict=verdict, hits=hits,
                                     mod=mod, daemon=daemon, receipt=clean, start_ns=start_ns, end_ns=end_ns))
                    if verdict != "PASS":
                        return dict(verdict=verdict, rows=rows, server_pid=args.server_pid,
                                    boot_id=args.boot_id, configuration=trace.configuration)
        return dict(verdict="PASS", rows=rows, server_pid=args.server_pid,
                    boot_id=args.boot_id, configuration=trace.configuration, acceptance=False)
    finally:
        command_report(args.endpoint, "SACS_RECORDER rust-road-place-on " + ("1" if initial == "on" else "0"))


def main(argv=None) -> int:
    p = argparse.ArgumentParser(description=__doc__)
    sub = p.add_subparsers(dest="command", required=True)
    for name in ("portal", "road", "capture"):
        s = sub.add_parser(name)
        s.add_argument("--actor", required=True)
        s.add_argument("--lot", type=lot_selector, default="world" if name == "road" else "7787005354294925872",
                       help="literal lot id, or world (resolved by IsWorldLot in the running game)")
        s.add_argument("--endpoint", default="127.0.0.1:34029")
        s.add_argument("--seconds", type=int, choices=range(5, 121), default=60)
        s.add_argument("--compile-only", action="store_true")
        if name == "portal":
            s.add_argument("--kind", choices=("door", "stairs"), required=True)
            s.add_argument("--mode", choices=("shape", "prepare", "traverse", "diagnose"), default="diagnose",
                           help="diagnose prepares at the native slot, then runs the insertion/traversal diagnostic")
            s.add_argument("--ordinal", type=int, default=0)
            s.add_argument("--slot", type=int, default=0)
        elif name == "road":
            s.add_argument("--selectors", default="0,4", help="0,4 (both required for P2/P3)")
            s.add_argument("--calls", type=int, default=1)
            s.add_argument("--distance", type=int, default=10, help="metres, caller is the selected Sim's current position")
            s.add_argument("--server-pid", type=int)
            s.add_argument("--boot-id")
        else:
            s.add_argument("--role", choices=("start", "upper", "cross-lot"), required=True)
            s.add_argument("--name", default="library", help="placement filename stem; selectors go in the scenario")
    args = p.parse_args(argv)
    if not args.actor or any(c in args.actor for c in "\r\n"):
        p.error("invalid actor or lot")
    if args.command == "portal" and (args.ordinal < 0 or args.slot < 0):
        p.error("ordinal and slot must be nonnegative")
    if args.command == "road":
        if args.selectors not in ("0,4", "4,0") or not 1 <= args.calls <= 100 or not 0 <= args.distance <= 10000:
            p.error("Road probes require selectors 0 and 4, calls 1..100 and distance 0..10000")
        args.selectors = [int(v) for v in args.selectors.split(",")]
        if not args.compile_only and (not args.server_pid or not args.boot_id):
            p.error("live Road probes require --server-pid and --boot-id")
    if args.command == "capture" and not re.fullmatch(r"[a-z0-9][a-z0-9_-]*", args.name):
        p.error("capture name must be a filename stem")
    try:
        if not args.compile_only:
            require_claim()
        if args.command == "portal" and args.mode == "diagnose":
            dlls = {mode: compile_probe(args, mode) for mode in ("prepare", "traverse")}
        else:
            modes = args.selectors if args.command == "road" else [0]
            dlls = {selector: compile_probe(args, getattr(args, "mode", ""), selector) for selector in modes}
        if args.compile_only:
            print(json.dumps(dict(verdict="COMPILED", probes=len(dlls), live=False)))
            return 0
        if args.command == "road":
            output = road(args, dlls)
        else:
            preparation = None
            if args.command == "portal" and args.mode == "diagnose":
                prep = run_probe(args, dlls["prepare"])
                preparation = sanitize(prep)
                if portal_verdict(prep, "prepare") != "PREPARATION_COMPLETE":
                    print(json.dumps(dict(verdict="BLOCKED", preparation=preparation, acceptance=False)))
                    return 2
                r = run_probe(args, dlls["traverse"])
            else:
                r = run_probe(args, dlls[0])
            if args.command == "portal":
                output = dict(verdict=portal_verdict(r, "traverse" if args.mode == "diagnose" else args.mode),
                              receipt=sanitize(r), acceptance=False)
                if preparation is not None:
                    output["preparation"] = preparation
            else:
                if r.get("verdict") != "CAPTURED":
                    raise ValueError("capture unavailable; no unique installed cell at the actor")
                output = publish_capture(ROOT, args.actor, args.name, args.role, r)
        print(json.dumps(output, sort_keys=True))
        return 0 if output["verdict"] in ("PASS", "CAPTURED", "STRUCTURAL_ONLY",
                                        "NATIVE_COMPLETION_OBSERVED", "PREPARATION_COMPLETE") else 2
    except (ValueError, OSError, subprocess.TimeoutExpired):
        # Never print a raw exception/report: it can carry runtime geometry.
        print(json.dumps(dict(verdict="BLOCKED", cleanup="check_required", acceptance=False)))
        return 3


if __name__ == "__main__":
    raise SystemExit(main())
