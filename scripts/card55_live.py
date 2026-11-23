"""Card 55/61 memory-only collectors and bounded loopback page transport.

These functions consume actual instrument readings, not assertions or filenames.
Raw frames, positions and road cases never enter Archive. Runtime producer
bindings and the G0 selector/progression decisions remain separate prerequisites.
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
import ipaddress
import json
import math
import re
import socket
import time
import uuid
from pathlib import Path

try:
    from . import card55_local, routing_collectors as pages, routing_evidence as evidence
except ImportError:
    import card55_local
    import routing_collectors as pages
    import routing_evidence as evidence


Missing = evidence.Missing
SIDES = ("client1", "client2")
# The instruments plan's seven address pointers. The addresses are local-only (card55_local.py): a label whose
# address is not installed reads None, and the enabled-breakpoint census below then blocks.
LABELS = ("native-search", "search-wrapper", "entry-1", "entry-2", "entry-3", "entry-4", "entry-5")
BREAKPOINTS = {label: None if va is None else int(va, 16)
               for label, va in card55_local.load(card55_local.E1_RECIPE, LABELS).items()}
DISAGREEMENTS = ("visible_fallback", "dynamic_obstacle", "score_order",
                 "caller_start_check", "stale_frustum")
ROW_KEYS = {"gate", "verdict", "hashes", "counts", "errors_mm"}
COUNT_KEYS = {
    "legs", "navigate_requests", "plans_served", "native_oracle_owned", "executed_routes",
    "server_native_hits", "fork_native_hits", "positive_control_hits", "unknown_producers",
    "search_entry_count", "native_search", "road_search", "served_icall", "requests", "served",
    "road_calls", "selector4_calls", "refusals", "fallbacks", "census_requests", "census_served",
    "invalid_answers", "agree", *DISAGREEMENTS,
    *(f"{side}_{metric}" for side in SIDES for metric in evidence.METRICS),
    *(f"selector{i}_{suffix}" for i in range(6) for suffix in ("cases", "found_agreement")),
}
HASH_KEYS = {"plan", "daemon", *SIDES, "configuration", "road_set", "heading_distances", "trace"}


def _digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode("ascii")).hexdigest()


def _row(gate, verdict="BLOCKED", *, hashes=None, counts=None, errors=None):
    return dict(gate=gate, verdict=verdict, hashes=hashes or {},
                counts=counts or {}, errors_mm=errors or {})


def _observation(scope, measurement):
    return dict(schema_version=1, scope=dict(scope), owner=scope["owner"],
                **{key: evidence.required(scope, key) for key in (
                    "bridge_connected", "claim_held", "instrument_ready", "coverage_start_ns",
                    "coverage_end_ns", "collector_status")}, measurement=measurement)


def _score(gate, scope, measurement, **summary):
    result = evidence.score(gate, scope, _observation(scope, measurement))
    return _row(gate, result["verdict"], **summary)


def counter_fields(report):
    """Read diagnostic scalar fields; duplicate or unmeasured counters block."""
    if not isinstance(report, str):
        raise Missing("missing diagnostic report")
    values = {}
    for field in re.split(r"[|;\n]", report):
        key, sep, value = field.partition("=")
        if not sep:
            continue
        if key in values:
            raise Missing("duplicate diagnostic field")
        values[key] = value
    return values


def _counter(values, key):
    text = values[key]
    if not isinstance(text, str) or not re.fullmatch(r"[0-9]+", text):
        raise Missing("unmeasured counter")
    return evidence.uint(int(text))


def collect_e2(scope, before, after, executed_routes):
    """Counters must bracket the entire scenario, not just the final walk."""
    try:
        old, new = counter_fields(before), counter_fields(after)
        data = {}
        for key in ("navigate_requests", "plans_served", "native_oracle_owned"):
            data[key] = evidence.uint(_counter(new, key) - _counter(old, key))
        data["executed_routes"] = evidence.uint(executed_routes)
        return _score("E2", scope, data, counts=data)
    except (Missing, KeyError, TypeError, ValueError):
        return _row("E2")


def collect_e3(scope, requests):
    """Each callback receives (cursor, epoch); all point comparisons are in RAM."""
    failed = False
    hashes = {}
    try:
        receipts = {}
        reference = None
        for side in ("daemon", *SIDES):
            receipts[side] = pages.collect_pages(requests[side], pages.PLAN,
                scope["corr"], scope["actor"], daemon_epoch=scope["epoch"])
            if receipts[side]["epoch"] != scope["epoch"]:
                raise Missing("scope epoch changed")
            parsed = evidence.parse_plan(receipts[side]["frames"], scope["corr"], scope["actor"])
            hashes[side] = _digest(receipts[side]["frames"])
            if side == "daemon":
                reference = parsed
            else:
                failed |= parsed != reference
        plans = {side: receipt["frames"] for side, receipt in receipts.items()}
        parsed = evidence.parse_plan(plans["daemon"], scope["corr"], scope["actor"])
        return _score("E3", scope, dict(plans=plans),
                      hashes={side: _digest(frames) for side, frames in plans.items()},
                      counts=dict(legs=len(parsed[2])))
    except (Missing, KeyError, TypeError, ValueError):
        return _row("E3", "FAIL" if failed else "BLOCKED", hashes=hashes)


def collect_e4(scope, requests, continuity):
    """Progress is read from execution receipts; received packets never count."""
    invalid_progressions = False
    try:
        data = {}
        legs = None
        for side in SIDES:
            receipt = pages.collect_pages(requests[side], pages.CLIENT,
                scope["corr"], scope["actor"], daemon_epoch=scope["epoch"])
            if (receipt["epoch"] != scope["epoch"]
                    or receipt["received_legs"] != receipt["legs_total"]):
                raise Missing("incomplete current-epoch receipt")
            if legs is not None and legs != receipt["legs_total"]:
                raise Missing("different declared leg counts")
            legs = receipt["legs_total"]
            data[side] = dict(executed_leg_indices=receipt["executed_slice"])
            for metric in evidence.METRICS:
                data[side][metric] = evidence.uint(continuity[side][metric])
            # Stable invalid-progressions counts are observed failures even
            # when the reported indices match; keep those indices verbatim.
            invalid_progressions |= receipt["invalid_progressions"] > 0
        data["leg_count"] = legs
        result = _score("E4", scope, data, counts={
            f"{side}_{metric}": data[side][metric] for side in SIDES for metric in evidence.METRICS
        } | {"legs": legs})
        if invalid_progressions:
            result["verdict"] = "FAIL"
        return result
    except (Missing, KeyError, TypeError, ValueError):
        # A client's invalid progressions already measured stay a failure when later evidence is missing.
        return _row("E4", "FAIL") if invalid_progressions else _row("E4")


def _xyz(value):
    if not isinstance(value, list) or len(value) != 3 or any(
            type(v) is not int or not -(1 << 31) <= v < 1 << 31 for v in value):
        raise Missing("invalid millimetre sample")
    return value


def collect_e5(scope, read_server, read_clients):
    """Server read -> concurrent client reads -> server read, without sleeps.

    read_clients must return both samples only after both reads have completed.
    The settled server position is the comparison reference. This does not
    resolve OPEN-2's client SeatHandoff completion reference.
    """
    try:
        before = _xyz(read_server()["position_mm"])
        clients = read_clients()
        after = _xyz(read_server()["position_mm"])
        data = dict(final_cell_mm=before, server_before_mm=before, server_after_mm=after)
        errors = {}
        for side in SIDES:
            data[side] = clients[side]
            errors[side] = math.ceil(math.dist(before, _xyz(clients[side]["position_mm"])))
        return _score("E5", scope, data, errors=errors)
    except (Missing, KeyError, TypeError, ValueError):
        return _row("E5")


def combine_walks(gate, rows):
    """Both walks remain visible; failure outranks missing later evidence."""
    counts, errors, hashes = {}, {}, {}
    for index, row in enumerate(rows):
        for source, destination in (("counts", counts), ("errors_mm", errors), ("hashes", hashes)):
            destination.update({f"walk{index + 1}_{key}": value for key, value in row[source].items()})
    return _row(gate, evidence.combine(rows), counts=counts, errors=errors, hashes=hashes)


def parse_cdb_trace(text):
    """Require lm base, enabled bl entries and timestamped attach/clear markers.

    Debugger clocks have millisecond resolution. Coverage bounds shrink by
    one millisecond; hit bounds expand by one millisecond when selecting a run.
    No stack, command output or address is returned in the persisted summary.
    """
    try:
        from . import crash_catch
    except ImportError:
        try:
            import crash_catch
        except ImportError:
            crash_catch = _MirrorProcFallback
    if isinstance(text, bytes):
        text = text.decode("utf-8", errors="strict")
    bases = re.findall(r"(?im)^\s*([0-9a-f]{8})\s+[0-9a-f]{8}\s+TS3W\b", text)
    if not bases or {int(base, 16) for base in bases} != {0x400000}:
        raise Missing("unverified TS3W image base")
    enabled = {int(va, 16) for va in re.findall(
        r"(?im)^\s*\d+\s+e\s+([0-9a-f]{8})\s+[0-9a-f]{4}\s+\([0-9a-f]{4}\)\s+\d+:\*{4}(?:\s|$)", text)}
    if None in BREAKPOINTS.values():
        raise Missing("Card 55 addresses not installed (" + str(card55_local.recipe(card55_local.E1_RECIPE)) + ")")
    if not set(BREAKPOINTS.values()) <= enabled:
        raise Missing("incomplete enabled breakpoint census")
    if re.search(r"(?m)^SIMSONLINE-(?:CAPPED|AV|UNHANDLED|DUMPED)\b", text):
        raise Missing("trace capped or interrupted")
    marks = {}
    pending = None
    for line in text.splitlines():
        if line in ("SIMSONLINE-ATTACHED", "SIMSONLINE-CLEARED"):
            if line in marks or pending is not None:
                raise Missing("ambiguous trace interval")
            pending = line
        elif pending is not None:
            if re.fullmatch(r"\d+:\d+> \.echotime", line):
                continue
            stamp = crash_catch.echotime(line)
            if stamp is None:
                raise Missing("missing trace boundary clock")
            marks[pending] = round(stamp * 1000) * 1_000_000
            pending = None
    if set(marks) != {"SIMSONLINE-ATTACHED", "SIMSONLINE-CLEARED"}:
        raise Missing("trace interval unavailable")
    start, end = marks["SIMSONLINE-ATTACHED"] + 1_000_000, marks["SIMSONLINE-CLEARED"] - 1_000_000
    if end <= start:
        raise Missing("empty trace interval")
    hits = []
    for hit in crash_catch.parse_trace(text):
        if hit["t"] is None:
            raise Missing("untimestamped native hit")
        hits.append(dict(label=hit["label"], tid=hit["tid"], ns=round(hit["t"] * 1000) * 1_000_000))
    return dict(module_base=0x400000, breakpoint_va=BREAKPOINTS["native-search"],
                coverage_start_ns=start, coverage_end_ns=end, search_entries=[1, 2, 3, 4, 5], hits=hits)


def collect_e1(scope, trace, control, fork):
    """Trace/control carry process, boot, instrument and configuration identity."""
    counts = {}
    try:
        if trace["server_pid"] != scope["server_pid"] or trace["boot_id"] != scope["boot_id"]:
            raise Missing("foreign debugger trace")
        hits = [h for h in trace["hits"] if scope["start_ns"] - 1_000_000 <= h["ns"]
                <= scope["end_ns"] + 1_000_000 and h["label"] == "native-search"]
        data = dict(server_native_hits=len(hits), fork_native_hits=evidence.uint(fork["native_hits"]))
        counts.update(data)
        data.update(positive_control_hits=sum(
                    h["label"] == "native-search"
                    and control["coverage_start_ns"] <= h["ns"] <= control["coverage_end_ns"]
                    and h["ns"] + 1_000_000 < scope["start_ns"] for h in control["hits"]),
                    control_ns=control["coverage_end_ns"], control_instrument=control["instrument"],
                    instrument=trace["instrument"], control_server_pid=control["server_pid"],
                    control_boot_id=control["boot_id"], module_base=trace["module_base"],
                    breakpoint_va=trace["breakpoint_va"], search_entries=trace["search_entries"],
                    configuration=trace["configuration"], control_configuration=control["configuration"],
                    unknown_producers=0 if not hits else len(hits),
                    fork_coverage_start_ns=fork["coverage_start_ns"],
                    fork_coverage_end_ns=fork["coverage_end_ns"])
        observation = _observation(scope, data)
        observation.update(coverage_start_ns=trace["coverage_start_ns"], coverage_end_ns=trace["coverage_end_ns"])
        result = evidence.score("E1", scope, observation)
        return _row("E1", result["verdict"], counts={
            key: data[key] for key in ("server_native_hits", "fork_native_hits", "positive_control_hits", "unknown_producers")
        }, hashes={"configuration": _digest(trace["configuration"])})
    except (Missing, KeyError, TypeError, ValueError):
        return _row("E1", "FAIL" if any(counts.values()) else "BLOCKED", counts=counts)


def collect_p2_p3(scope, control, enabled):
    """Normalize scoped Road and selector-4 probe receipts before archival."""
    try:
        for key in ("server_pid", "boot_id"):
            if enabled[key] != scope[key] or type(enabled[key]) is not type(scope[key]):
                raise Missing("foreign road probe")
        if any(control[key] != enabled[key] for key in ("server_pid", "boot_id", "instrument", "configuration")):
            raise Missing("different positive control")
        if not control["end_ns"] < scope["start_ns"] <= enabled["start_ns"] < enabled["end_ns"] <= scope["end_ns"]:
            raise Missing("road probe outside run interval")
        for key in ("road_icall", "road_search", "road_wrapper", "selector4_icall"):
            if not evidence.uint(control[key]):
                raise Missing("positive control did not exercise producer")
        if evidence.uint(control["selector4_search"]):
            raise Missing("selector 4 control unexpectedly searched")
        counts = {key: evidence.uint(enabled[key]) for key in (
            "native_search", "road_search", "served_icall", "requests", "served", "road_calls",
            "selector4_calls", "refusals", "fallbacks", "census_requests", "census_served")}
        if not counts["road_calls"] or not counts["selector4_calls"]:
            raise Missing("empty dedicated yielding probe")
        failure = any(counts[key] for key in ("native_search", "road_search", "served_icall", "refusals", "fallbacks"))
        failure |= not (counts["requests"] == counts["served"] == counts["census_requests"]
                        == counts["census_served"] == counts["road_calls"] + counts["selector4_calls"])
        return _row("P2-P3", "FAIL" if failure else "PASS", counts=counts,
                    hashes={"configuration": _digest(enabled["configuration"])})
    except (Missing, KeyError, TypeError, ValueError):
        return _row("P2-P3")


def collect_p4(cases, road_hash, validate_answer):
    """validate_answer independently checks each answer against this RAM road set.

    Cases and geometry are never serialized. Counts pin the 95% found threshold
    per selector. Position errors are integer mm; heading distances remain in
    RAM and only their digest and disagreement counts are persisted.
    """
    counts, errors, headings = {}, {}, []
    totals, agreements = [0] * 6, [0] * 6
    failures = 0
    try:
        if not re.fullmatch(r"[0-9a-f]{16}", road_hash):
            raise Missing("unverified same-run road hash")
        for case in cases:
            selector = evidence.uint(case["selector"], 8)
            if selector > 5 or case["road_hash"] != road_hash:
                raise Missing("foreign road set or selector")
            if type(case["native_found"]) is not bool or type(case["kernel_found"]) is not bool:
                raise Missing("invalid found flag")
            valid = validate_answer(case)
            if type(valid) is not bool:
                raise Missing("validity oracle unavailable")
            failures += not valid
            totals[selector] += 1
            agreements[selector] += case["native_found"] == case["kernel_found"]
            if case["native_found"] and case["kernel_found"]:
                error = evidence.uint(case["position_error_mm"])
                errors[f"case{sum(totals)}"] = error
                headings.append(evidence.uint(case["heading_error_mdeg"]))
                classification = case["disagreement"]
                if classification not in DISAGREEMENTS and classification != "agree":
                    raise Missing("unclassified road disagreement")
                counts[classification] = counts.get(classification, 0) + 1
        for selector in range(6):
            counts[f"selector{selector}_cases"] = totals[selector]
            counts[f"selector{selector}_found_agreement"] = agreements[selector]
        counts["invalid_answers"] = failures
        verdict = ("FAIL" if failures or any(a * 100 < n * 95 for a, n in zip(agreements, totals))
                   else "BLOCKED" if any(n < 100 for n in totals) else "PASS")
        return _row("P4", verdict, counts=counts, errors=errors,
                    hashes=dict(road_set=road_hash, heading_distances=_digest(headings)))
    except (Missing, KeyError, TypeError, ValueError):
        # An invalid answer already proven stays a failure when a later case is malformed.
        return _row("P4", "FAIL", counts=dict(invalid_answers=failures)) if failures else _row("P4")


class Archive:
    """An allowlist at the only disk boundary, before opening any output file."""

    def __init__(self, folder=None):
        self.folder = Path(folder) if folder is not None else Path(__file__).resolve().parents[1] / ".omo/run/card55-live"

    def put(self, name, row):
        if not re.fullmatch(r"[A-Za-z0-9_-]+", name) or set(row) != ROW_KEYS:
            raise Missing("invalid sanitized archive record")
        if row["gate"] not in ("E1", "E2", "E3", "E4", "E5", "P2-P3", "P4"):
            raise Missing("unknown gate")
        if row["verdict"] not in ("PASS", "FAIL", "BLOCKED"):
            raise Missing("unknown verdict")
        for section in ("hashes", "counts", "errors_mm"):
            values = row[section]
            if not isinstance(values, dict):
                raise Missing("invalid summary section")
            for key, value in values.items():
                bare = re.sub(r"^walk[1-9][0-9]*_", "", key)
                if section == "hashes":
                    if (bare not in HASH_KEYS or not isinstance(value, str)
                            or not re.fullmatch(r"(?:[0-9a-f]{16}|[0-9a-f]{64})", value)):
                        raise Missing("invalid hash")
                else:
                    if section == "counts" and bare not in COUNT_KEYS:
                        raise Missing("unknown count field")
                    if section == "errors_mm" and bare not in SIDES and not re.fullmatch(r"case[1-9][0-9]*", bare):
                        raise Missing("unknown millimetre error field")
                    evidence.uint(value)
        serialized = json.dumps(row, sort_keys=True, indent=2) + "\n"
        self.folder.mkdir(parents=True, exist_ok=True)
        (self.folder / f"{name}.json").write_text(serialized, encoding="utf-8")


def decode_admin(raw):
    try:
        response = json.loads(raw)
        if (not isinstance(response, dict) or response.get("ok") is not True
                or response.get("accepted") is not True or not isinstance(response.get("report"), str)):
            raise Missing("admin report unavailable")
        return response["report"]
    except (ValueError, TypeError) as error:
        raise Missing("invalid admin response") from error


class Admin:
    """Bounded loopback JSON transport; no bridgectl geometry journal or output."""

    def __init__(self, endpoint, timeout=15.0):
        try:
            host, port = endpoint.rsplit(":", 1)
            host = host.strip("[]")
            if not ipaddress.ip_address(host).is_loopback or not 1 <= int(port) <= 65535:
                raise ValueError("loopback endpoint required")
            if not 0 < timeout <= 240:
                raise ValueError("bounded timeout required")
        except (ValueError, AttributeError) as error:
            raise Missing("invalid loopback endpoint or timeout") from error
        self.address, self.timeout = (host, int(port)), timeout

    def request(self, command):
        if not isinstance(command, str) or not command.isascii() or any(c in command for c in "\r\n"):
            raise Missing("invalid admin command")
        wire = command.encode("ascii") + b"\n"
        if len(wire) > 4081:
            raise Missing("command exceeds bridge carrier")
        end = time.monotonic() + self.timeout
        try:
            with socket.create_connection(self.address, timeout=self.timeout) as sock:
                sock.sendall(wire)
                body = bytearray()
                while not body.endswith(b"\n"):
                    remaining = end - time.monotonic()
                    if remaining <= 0:
                        raise Missing("admin response deadline")
                    sock.settimeout(remaining)
                    chunk = sock.recv(min(65536, 1_048_577 - len(body)))
                    if not chunk or len(body) + len(chunk) > 1_048_576:
                        raise Missing("closed or oversized admin response")
                    body.extend(chunk)
                return decode_admin(body)
        except OSError as error:
            raise Missing("admin transport unavailable") from error

    def plan_page(self, corr, actor, *, client=False, progress=False):
        def request(cursor, epoch):
            if not client:
                return self.request(pages.daemon_plan_request(corr, cursor, 16, epoch))
            kind = pages.CLIENT if progress else pages.PLAN
            line = pages.client_page_request(uuid.uuid4().hex, kind, corr, actor,
                cursor, 128 if progress else 16, epoch)
            # Admin listeners replace the correlation and space-split arguments.
            fields = line.split("|")
            return self.request(" ".join([fields[0], *fields[2:]]))
        return request
