"""Card 55 in-memory observation validation, not a runtime transport.

No files, sockets, topology capture or gameplay commands belong here. Observations
must come from an instrument adapter; no such live adapter is installed yet.
Normalized observation dictionaries below are a collector-local contract, not a
new public command or a claim that K/C/F/M already emit this schema.
"""
from __future__ import annotations

import math
import re


KINDS = ("claim-sim", "rust-env", "load-simnav", "navigate_astar", "rust-evidence")
DEPENDENCIES = {
    "claim-sim": "existing player connection ownership and checked kernel-id observation",
    "rust-env": "process-bound switch, bridge, claim and complete suppression census observation",
    "load-simnav": "trusted measured in-memory topology installation and generation observation",
    "navigate_astar": "owner-gated gameplay trigger and correlated daemon/both-client execution adapters",
    "rust-evidence": "scoped debugger, fork census, daemon and both-client measurement collectors",
}
SCOPE = ("run_id", "boot_id", "server_pid", "epoch", "corr", "actor", "generation",
         "owner", "start_ns", "end_ns")
METRICS = ("startup_stutter_events", "pace_mismatch_events",
           "client_only_motion_events", "door_leg_leg_style_mismatches")


# E1's native search breakpoint, as an offset from the TS3W module base.
NATIVE_SEARCH_RVA = 0x41B2A0


class Missing(ValueError):
    """Absent, malformed or unscoped observation: no behavioral result."""


def result(verdict, reason):
    return {"verdict": verdict, "reason": reason}


def uint(value, bits=64):
    if type(value) is not int or not 0 <= value < 1 << bits:
        raise Missing("invalid unsigned measurement")
    return value


def required(data, key):
    if not isinstance(data, dict) or key not in data:
        raise Missing(f"missing {key}")
    return data[key]


def flag(data, key):
    value = required(data, key)
    if type(value) is not bool:
        raise Missing(f"invalid {key}")
    return value


def count(data, key):
    return uint(required(data, key))


def scope_check(scope, observation):
    if type(required(observation, "schema_version")) is not int or observation["schema_version"] != 1:
        raise Missing("unsupported collector schema")
    for key in SCOPE:
        value = required(scope, key)
        if key in ("run_id", "boot_id", "owner"):
            if not isinstance(value, str) or not value:
                raise Missing(f"invalid scope {key}")
        else:
            uint(value)
        if required(required(observation, "scope"), key) != value:
            raise Missing(f"foreign or stale {key}")
        # Equality alone would accept True for integer 1.
        if type(observation["scope"][key]) is not type(value):
            raise Missing(f"invalid scope type {key}")
    if scope["end_ns"] <= scope["start_ns"] or any(
            scope[key] == 0 for key in ("server_pid", "corr", "actor", "generation")):
        raise Missing("invalid scope interval or identity")
    if not flag(observation, "bridge_connected") or not flag(observation, "claim_held"):
        raise Missing("bridge or claim lost")
    if required(observation, "owner") != scope["owner"]:
        raise Missing("wrong owner")


def live_topology(data):
    """Validate metadata only. Geometry must never enter an evidence file."""
    if required(data, "provenance") != "measured" or required(data, "storage") != "memory":
        raise Missing("live topology must be measured and memory-only")
    for field in ("inferred_partition", "inferred_stairs"):
        if flag(data, field):
            raise Missing("inferred topology is not live input")
    for field in ("surfaces_measured", "portals_measured", "obstacles_and_locks_measured",
                  "cross_lot_measured"):
        if not flag(data, field):
            raise Missing(f"unmeasured {field}")
    if count(data, "cells") == 0 or count(data, "portals") == 0:
        raise Missing("empty topology")
    if not re.fullmatch(r"[0-9a-f]{16}", required(data, "content_hash")):
        raise Missing("invalid topology digest")


def parse_plan(frames, corr, actor):
    """Strict sealed Card 54 Rt/Vp parser. Returned points stay in memory."""
    if not isinstance(frames, list) or not frames or any(not isinstance(f, str) for f in frames):
        raise Missing("missing plan frames")

    def number(text, bits=64, signed=False):
        if not re.fullmatch(r"-?[0-9]+" if signed else r"[0-9]+", text):
            raise Missing("bad_line numeric field")
        value = int(text)
        low, high = (-(1 << (bits - 1)), 1 << (bits - 1)) if signed else (0, 1 << bits)
        if not low <= value < high:
            raise Missing("bad_line numeric range")
        return value

    header = frames[0].split("|")
    if len(header) != 6 or header[0] != "Rt":
        raise Missing("bad_line Rt")
    if (number(header[1]), number(header[2])) != (corr, actor):
        raise Missing("wrong corr or actor")
    legs = number(header[3], 32)
    if not legs or len(frames) != legs + 1 or not re.fullmatch(r"[0-9a-f]{16}", header[4]):
        raise Missing("incomplete plan or invalid digest")
    cost = number(header[5])
    points = []
    for index, frame in enumerate(frames[1:]):
        fields = frame.split("|")
        if len(fields) != 9 or fields[:2] != ["Vp", "rt"]:
            raise Missing("bad_line route point")
        if (number(fields[2]), number(fields[3]), number(fields[4], 32)) != (corr, actor, index):
            raise Missing("wrong route point identity or order")
        points.append((number(fields[5], 32), *(number(v, 32, True) for v in fields[6:])))
    return header[4], cost, tuple(points)


def step_observation(kind, payload, scope=None, observation=None):
    """Parse actual observations when supplied; never performs or invents an action."""
    if kind not in KINDS:
        raise ValueError(f"unknown Rust step {kind}")
    if observation is None:
        return result("BLOCKED", f"{kind}: unavailable {DEPENDENCIES[kind]}")
    try:
        scope_check(scope, observation)
        if kind == "claim-sim":
            if required(observation, "sim_name") != payload:
                raise Missing("wrong claimed Sim")
            if count(observation, "kernel_actor") == 0 or not flag(observation, "mapping_checked"):
                raise Missing("unchecked kernel actor mapping")
        elif kind == "rust-env":
            if payload != "SIMSONLINE_RUST_ROUTE=1":
                raise Missing("unsupported route switch requirement")
            for key in ("route_enabled", "census_complete", "suppression_complete"):
                if not flag(observation, key):
                    raise Missing(f"unavailable {key}")
        elif kind == "load-simnav":
            if payload != "memory":
                raise Missing("load-simnav accepts no live topology filename")
            live_topology(required(observation, "topology"))
        elif kind == "navigate_astar":
            # Parsing a sent plan cannot establish that gameplay was triggered.
            parse_plan(required(observation, "frames"), scope["corr"], scope["actor"])
            return result("BLOCKED", "normal gameplay trigger and execution adapters unavailable")
        else:
            return score(payload, scope, observation)
        return result("PASS", f"{kind} observation validated")
    except (Missing, TypeError, ValueError, KeyError) as error:
        return result("BLOCKED", str(error))


def score(gate, scope, observation):
    """Score one scoped collector result, never human labels or file existence."""
    failures = []
    missing = []

    def need(condition, why):
        if not condition:
            missing.append(why)

    def check(condition, why):
        if not condition:
            failures.append(why)

    try:
        scope_check(scope, observation)
        data = required(observation, "measurement")
        if gate == "E1":
            # Preserve observed native hits even if another instrument is absent.
            check(count(data, "server_native_hits") == 0, "server native search observed")
            check(count(data, "fork_native_hits") == 0, "fork native search observed")
            need(count(data, "positive_control_hits") > 0, "no observed positive control")
            need(count(data, "control_ns") < scope["start_ns"], "control not before interval")
            need(required(data, "control_instrument") == required(data, "instrument"),
                 "different positive-control instrument")
            need(count(data, "control_server_pid") == scope["server_pid"]
                 and required(data, "control_boot_id") == scope["boot_id"],
                 "positive control from different process or boot")
            instrument = required(data, "instrument")
            need(isinstance(instrument, str) and bool(instrument), "missing instrument identity")
            configuration = required(data, "configuration")
            need(isinstance(configuration, str) and bool(configuration)
                 and required(data, "control_configuration") == configuration,
                 "different positive-control breakpoint configuration")
            need(count(data, "module_base") + NATIVE_SEARCH_RVA == count(data, "breakpoint_va"),
                 "incorrect search breakpoint")
            entries = required(data, "search_entries")
            need(isinstance(entries, list) and all(type(e) is int for e in entries)
                 and sorted(entries) == [1, 2, 3, 4, 5], "incomplete search-entry coverage")
            need(count(data, "unknown_producers") == 0, "unknown native-search producers")
            need(count(data, "fork_coverage_start_ns") <= scope["start_ns"]
                 and count(data, "fork_coverage_end_ns") >= scope["end_ns"], "fork census coverage gap")
        elif gate == "E2":
            requests = count(data, "navigate_requests")
            served = count(data, "plans_served")
            executed = count(data, "executed_routes")
            need(requests > 0 and executed > 0, "zero routing work")
            check(served == requests == executed, "plan/request/execution mismatch")
            check(count(data, "native_oracle_owned") == 0, "native oracle refusal")
        elif gate == "E3":
            plans = required(data, "plans")
            parsed = [parse_plan(required(plans, side), scope["corr"], scope["actor"])
                      for side in ("daemon", "client1", "client2")]
            check(parsed[0] == parsed[1] == parsed[2], "plan hash/cost/point stream mismatch")
        elif gate == "E4":
            legs = count(data, "leg_count")
            need(legs > 0, "no declared legs")
            for side in ("client1", "client2"):
                client = required(data, side)
                indices = required(client, "executed_leg_indices")
                if not isinstance(indices, list) or any(type(i) is not int for i in indices):
                    raise Missing("invalid executed leg indices")
                check(indices == list(range(legs)), f"{side} skipped or replayed execution")
                for metric in METRICS:
                    check(count(client, metric) == 0, f"{side} {metric}")
        elif gate == "E5":
            def xyz(value):
                if not isinstance(value, list) or len(value) != 3 or any(
                        type(v) is not int or not -(1 << 31) <= v < 1 << 31 for v in value):
                    raise Missing("invalid measured millimetre position")
                return value

            goal = xyz(required(data, "final_cell_mm"))
            before = xyz(required(data, "server_before_mm"))
            after = xyz(required(data, "server_after_mm"))
            need(before == after, "server moving: retain bracketing samples for classification")
            for side in ("client1", "client2"):
                client = required(data, side)
                settled = required(client, "phase") in ("Halted", "SeatingSettled")
                need(settled, f"{side} not settled")
                position = xyz(required(client, "position_mm"))
                if settled and before == after:
                    check(math.dist(position, goal) <= 100, f"{side} landing outside 100mm")
        else:
            raise Missing("unknown evidence selector; use exactly E1, E2, E3, E4 or E5")
        need(flag(observation, "instrument_ready"), "instrument not ready")
        need(count(observation, "coverage_start_ns") <= scope["start_ns"]
             and count(observation, "coverage_end_ns") >= scope["end_ns"], "coverage gap")
        need(required(observation, "collector_status") == "complete", "incomplete collector")
    except (Missing, TypeError, ValueError, KeyError) as error:
        missing.append(str(error))
    return result("FAIL" if failures else "BLOCKED" if missing else "PASS",
                  "; ".join(failures + missing) or f"{gate} scoped measurements agree")


def combine(results):
    """Missing later evidence must never erase an earlier behavioral failure."""
    verdicts = [item["verdict"] for item in results]
    return "FAIL" if "FAIL" in verdicts else "PASS" if verdicts and all(v == "PASS" for v in verdicts) else "BLOCKED"
