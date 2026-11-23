"""Drive one real client terrain click through Rust and score native position convergence.

Requires the existing live-stack claim. Uses INJECT_CLICK on the actual client, not
QUEUE_INTERACTION on the server. Visual animation acceptance remains a separate check.
"""
from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
import json
import math
from pathlib import Path
import re
import subprocess
import time

try:
    from . import front_door_mvp as proof, sim_compare, simctl, stack_claim
except ImportError:
    import front_door_mvp as proof
    import sim_compare
    import simctl
    import stack_claim


# Terrain.GoHere.RouteActorToDestination uses RadialRangeDestination, radius 0..2 m.
# Engine source citation and prior live evidence: internal documentation
GOHERE_ARRIVAL_RADIUS = 2.0


def server_sim(endpoint: str, actor: int) -> dict:
    connection = simctl.Conn(endpoint, 30, wire="line", player_id=9999, join=False)
    try:
        ok, reason, event = connection.command("request_snapshot", wait_for="full_snapshot",
                                             playerId=9999, sinceTick=None)
        if not ok or not event:
            raise ValueError(f"read-only server snapshot refused: {reason}")
        return next(sim for sim in event["snapshot"]["sims"] if sim["objectId"] == actor)
    finally:
        connection.close()


def position(sim: dict) -> list[float]:
    return [float(sim["position"][axis]) for axis in ("x", "y", "z")]


def _is_gohere_intent(row: dict) -> bool:
    kind = row.get("kind")
    payload = row.get("payload") or {}
    if kind == "intent_forwarded":
        key = payload.get("key")
        return key is None or "GoHere" in str(key)
    if kind == "user_intent_forwarded":
        typ = payload.get("type")
        name = payload.get("name")
        return (typ is None and name is None) or "GoHere" in f"{typ or ''} {name or ''}"
    return False


def accepted_command(receipts: list[dict], actor: int) -> str | None:
    forwarded = {row["payload"].get("commandId") for row in receipts
                 if _is_gohere_intent(row) and row["payload"].get("actor") == actor}
    return next((row["payload"]["commandId"] for row in receipts
                 if row["kind"] == "command_accepted" and row["payload"].get("commandId") in forwarded), None)


def failed_interaction(receipts: list[dict], actor: int, command_id: str | None) -> dict | None:
    """Match a native terminal failure to this action's newly queued GoHere."""
    if not command_id:
        return None
    queued = {row["payload"].get("interactionId") for row in receipts
              if row["kind"] == "interaction_queued" and row["payload"].get("actorObjectId") == actor
              and "Terrain+GoHere+" in row["payload"].get("interactionKey", "")}
    return next((row["payload"] for row in receipts if row["kind"] == "interaction_failed"
                 and row["payload"].get("actorObjectId") == actor
                 and row["payload"].get("interactionId") in queued), None)


def native_positions(name: str, endpoints: list[str], probe: Path) -> list[list[float]]:
    with ThreadPoolExecutor(max_workers=3) as pool:
        readings = list(pool.map(lambda endpoint: sim_compare.run_side(endpoint, probe), endpoints))
    positions = []
    for endpoint, reading in zip(endpoints, readings, strict=True):
        raw = reading.get((name, "pos"), [])
        if len(raw) != 1 or reading.get(("-", "error")):
            reason = reading.get(("-", "error")) or reading.get((name, "missing")) or "position missing"
            raise ValueError(f"native comparison probe unavailable at {endpoint}: {str(reason)[:400]}")
        parsed = [float(value) for value in raw[0].split(",")]
        if len(parsed) != 3 or not all(math.isfinite(value) for value in parsed):
            raise ValueError("native comparison returned an invalid position")
        positions.append(parsed)
    return positions


def score(before: list[float], target: list[float], positions: list[list[float]], command_id: str | None) -> dict:
    moved = math.dist(before, positions[0])
    target_error = math.dist(target, positions[0])
    errors = [math.dist(positions[0], client) for client in positions[1:]]
    passed = bool(command_id) and moved >= 0.5 and target_error <= GOHERE_ARRIVAL_RADIUS and len(errors) == 2 and max(errors) <= 0.10
    return {"passed": passed, "command_id": command_id, "native_server_moved_m": moved,
            "native_server_target_error_m": target_error, "native_arrival_radius_m": GOHERE_ARRIVAL_RADIUS,
            "client_position_errors_m": errors}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--player", type=int, choices=(1, 2), required=True)
    parser.add_argument("--actor", type=int, required=True)
    parser.add_argument("--name", required=True, help="unique in-world first name, e.g. Jennifer or Pablo")
    parser.add_argument("--target", type=float, nargs=3, required=True, metavar=("X", "Y", "Z"))
    parser.add_argument("--client-log", type=Path, required=True)
    parser.add_argument("--public", default="127.0.0.1:34019")
    parser.add_argument("--endpoints", nargs=3, default=["127.0.0.1:34029", "127.0.0.1:34030", "127.0.0.1:34031"])
    parser.add_argument("--timeout", type=float, default=90)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args(argv)
    session, claim = stack_claim.own_session(), stack_claim.current()
    if not session or not claim or claim[0] != session:
        parser.error("the running session must own the live-stack claim")
    if not re.fullmatch(r"[A-Za-z][A-Za-z0-9 -]{0,63}", args.name) or not all(map(math.isfinite, args.target)):
        parser.error("name and target must be safe, finite probe values")
    if not 10 <= args.timeout <= 180:
        parser.error("--timeout must be in 10..180")
    result = {"verdict": "BLOCKED", "input_path": "real client INJECT_CLICK -> public Rust command",
              "visual_animation_acceptance": "not_assessed", "native_ts3_simulator_required": True}
    try:
        before = server_sim(args.public, args.actor)
        if before.get("owningPlayerId") != args.player or not before.get("name", "").startswith(args.name):
            raise ValueError("the selected real player does not own this named actor")
        origin = position(before)
        if math.dist(origin, args.target) < GOHERE_ARRIVAL_RADIUS + 0.75:
            raise ValueError("walk target must be at least 2.75 m away, beyond the native 2 m arrival radius")
        # Reuse the generic authored engine comparison probe, generated only under .omo/run/act.
        probe = sim_compare.ROOT / ".omo/run/act/FrontDoorMvpCompare.cs"
        probe.parent.mkdir(parents=True, exist_ok=True)
        probe.write_text(sim_compare.TEMPLATE.replace("__NAMES__", args.name)
                         .replace("__PROPS__", "false").replace("__MOODLETS__", "false"), encoding="utf-8")
        # Compile once on the server before concurrent first loads in the client processes.
        native_positions(args.name, args.endpoints[:1], probe)
        # Prime the existing generic probe before moving, so compilation cannot hide the action.
        native_positions(args.name, args.endpoints, probe)
        start = args.client_log.stat().st_size
        input_reply = proof.admin(args.endpoints[args.player],
                                  f"INJECT_CLICK {args.actor} {' '.join(map(str, args.target))}")
        result["input_accepted"] = input_reply.get("accepted") is True
        print("client input accepted; awaiting correlated Rust acceptance and native movement", flush=True)
        deadline = time.monotonic() + args.timeout
        while time.monotonic() < deadline:
            captured = proof.client_commands(args.client_log, start=start)
            command_id = accepted_command(captured["receipts"], args.actor)
            positions = native_positions(args.name, args.endpoints, probe)
            measured = score(origin, args.target, positions, command_id)
            result.update(measured, command_receipts=captured, before=origin, target=args.target,
                          positions=positions, verdict="PASS_COMMAND_AND_POSITIONS" if measured["passed"] else "FAIL")
            failure = failed_interaction(captured["receipts"], args.actor, command_id)
            if failure:
                result.update(passed=False, verdict="FAIL", native_interaction_failure=failure)
                break
            if measured["passed"]:
                break
            time.sleep(2)
    except (OSError, ValueError, KeyError, StopIteration, TimeoutError, subprocess.SubprocessError) as error:
        result.update(verdict="BLOCKED", error=str(error))
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result))
    return 0 if result["verdict"] == "PASS_COMMAND_AND_POSITIONS" else 2


if __name__ == "__main__":
    raise SystemExit(main())
