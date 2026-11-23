"""Collect bounded, read-only live Rust-front-door evidence without joining a player.

STREAM_READY proves process/connection/heartbeat evidence only. Command execution,
visual presentation and the Card 55/60 acceptance gates remain separate evidence.
"""
from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import time

try:
    from . import rejection_relay_probe, stack_claim
except ImportError:
    import rejection_relay_probe
    import stack_claim


def admin(endpoint: str, command: str) -> dict:
    host, port = endpoint.rsplit(":", 1)
    with socket.create_connection((host, int(port)), timeout=8) as connection:
        connection.settimeout(8)
        connection.sendall((command + "\n").encode("ascii"))
        with connection.makefile("rb") as reader:
            raw = reader.readline(4_194_306)
    if not raw.endswith(b"\n") or len(raw) > 4_194_305:
        raise ValueError("unbounded or incomplete admin response")
    answer = json.loads(raw)
    if not answer.get("ok") or not answer.get("accepted"):
        raise ValueError(f"{command} refused: {answer.get('error') or answer.get('reason')}")
    return answer


def fields(report: str) -> dict[str, str]:
    return dict(re.findall(r"(?:^|[\s;|])([A-Za-z_]+)=([^\s;|]+)", report))


def stream_rows(report: str) -> dict[int, dict[str, str]]:
    return {int(parts[1]): fields(line) for line in report.splitlines()
            if (parts := line.split("|")) and parts[0] == "client" and len(parts) > 2}


def sample(endpoints: list[str]) -> dict:
    requests = [(endpoints[0], "FRONT_DOOR_STATE")] + [(endpoint, "DIAG_STATE") for endpoint in endpoints]
    with ThreadPoolExecutor(max_workers=4) as pool:
        answers = list(pool.map(lambda request: admin(*request), requests))
    return {"stream": answers[0]["report"],
            "games": [fields(answer["report"]) for answer in answers[1:]]}


def client_game_evidence(command: str, controller_pid: int, game_pid: int) -> dict:
    attached = re.search(r"--client-attach-pid\s+([0-9]+)(?:\s|$)", command)
    if attached:
        if int(attached[1]) != game_pid:
            raise ValueError("client attach PID differs from the selected game")
        return {"launch": "warm_attach", "game_pid": game_pid}
    receipt = re.search(r'--state-file\s+(?:"([^"]+)"|(\S+))', command)
    if not receipt:
        raise ValueError("cold client requires its actual runtime state receipt")
    state = json.loads(Path(receipt[1] or receipt[2]).read_text(encoding="utf-8"))
    if state.get("pid") != controller_pid or state.get("gamePid") != game_pid or state.get("phase") != "ready":
        raise ValueError("cold client runtime receipt does not bind the selected ready controller/game")
    return {"launch": "cold", "game_pid": game_pid, "phase": state["phase"]}


def process_evidence(server: int, games: list[int], controllers: list[int], public_port: int) -> dict:
    if os.name != "nt":
        raise ValueError("live TS3 process evidence requires the Windows host")
    wanted = [server, *games, *controllers]
    if len(set(wanted)) != len(wanted) or any(pid <= 0 for pid in wanted):
        raise ValueError("server, three game and two client-controller PIDs must be distinct and positive")
    # Only validated integers enter this fixed PowerShell program. Full command lines stay in memory.
    program = (
        "$ErrorActionPreference='Stop';"
        f"$wanted=@({','.join(map(str, wanted))});"
        "$processes=@(Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -in $wanted } | "
        "Select-Object ProcessId,Name,CommandLine,ExecutablePath);"
        f"$connections=@(Get-NetTCPConnection -State Established | Where-Object {{ $_.RemotePort -eq {public_port} }} | "
        "Select-Object OwningProcess,LocalAddress,LocalPort);"
        "@{processes=$processes;connections=$connections}|ConvertTo-Json -Depth 4 -Compress"
    )
    result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", program],
                            stdin=subprocess.DEVNULL, capture_output=True, encoding="utf-8",
                            errors="replace", timeout=20, check=True)
    data = json.loads(result.stdout)
    processes = {int(row["ProcessId"]): row for row in data["processes"]}
    if any(pid not in processes for pid in wanted):
        raise ValueError("a selected live process disappeared")
    binary = processes[server].get("ExecutablePath")
    if not binary or Path(binary).name.lower() != "simlink_headless_server.exe":
        raise ValueError("selected server PID is not simlink_headless_server.exe")
    if any(processes[pid]["Name"].lower() != "ts3w.exe" for pid in games):
        raise ValueError("all three game PIDs must name TS3W.exe")
    clients = []
    for pid, game_pid in zip(controllers, games[1:], strict=True):
        row = processes[pid]
        command = row.get("CommandLine") or ""
        player = re.search(r"--player\s+([0-9]+)(?:\s|$)", command)
        if not re.search(r"--wire\s+line(?:\s|$)", command) or not player:
            raise ValueError("client controller must explicitly select --wire line and --player")
        game = client_game_evidence(command, pid, game_pid)
        connections = [connection for connection in data["connections"] if connection["OwningProcess"] == pid]
        if len(connections) != 1:
            raise ValueError("each selected client controller needs one established public-server socket")
        connection = connections[0]
        clients.append({"pid": pid, "player": int(player[1]), **game,
                        "remote": f"{connection['LocalAddress']}:{connection['LocalPort']}"})
    return {"server_pid": server, "server_sha256": hashlib.sha256(Path(binary).read_bytes()).hexdigest(),
            "game_pids": games, "clients": clients}


def evaluate(before: dict, after: dict, processes: dict) -> list[str]:
    failures = []
    if fields(after["stream"]).get("presentation_history_pending", "0") != "0":
        failures.append("participant presentation history is still pending")
    rows_before, rows_after = stream_rows(before["stream"]), stream_rows(after["stream"])
    if len({client["player"] for client in processes["clients"]}) != 2:
        failures.append("client players are not distinct")
    for client in processes["clients"]:
        player = client["player"]
        first, last = rows_before.get(player, {}), rows_after.get(player, {})
        if first.get("remote") != client["remote"] or last.get("remote") != client["remote"]:
            failures.append(f"player {player}: live controller socket does not match the Rust session")
        if int(last.get("last_seq", "-1")) <= int(first.get("last_seq", "-1")):
            failures.append(f"player {player}: stream sequence did not advance")
        if last.get("drops") != "0":
            failures.append(f"player {player}: outbound drop count is not zero")
    for side, first, last in zip(("server", "client1", "client2"), before["games"], after["games"], strict=True):
        if first.get("inWorld") != "True" or last.get("inWorld") != "True":
            failures.append(f"{side}: game is not in world")
        if int(last.get("tick", "-1")) <= int(first.get("tick", "-1")):
            failures.append(f"{side}: simulation heartbeat did not advance")
    return failures


def client_commands(path: Path, limit: int = 4_194_304, start: int = 0) -> dict:
    """Retain command identities and short lifecycle receipts, never animation/save bodies."""
    with path.open("rb") as source:
        size = source.seek(0, 2)
        offset = max(start, size - limit)
        source.seek(offset)
        if offset > start:
            source.readline()  # skip a potentially partial leading row
        lines = source.read(limit).decode("utf-8", errors="replace").splitlines()
    rows = []
    for line in lines:
        try:
            try:
                item = json.loads(line)
            except ValueError:
                text_row = re.match(r"^\[([^\]]+)\]\s+([A-Za-z_]+):\s+(.*)$", line)
                if not text_row:
                    continue
                item = {"ts": text_row[1], "kind": text_row[2], "payload": json.loads(text_row[3])}
            kind, payload = item.get("kind"), item.get("payload")
            if kind == "received":
                if isinstance(payload, str):
                    payload = json.loads(payload)
                kind = payload.get("$type")
            if isinstance(payload, dict) and kind in {
                "sent", "intent_forwarded", "user_intent_forwarded", "pick_refused_shown",
                "command_accepted", "command_rejected", "interaction_queued", "interaction_started",
                "interaction_completed", "interaction_failed", "order_completed", "order_failed", "command_send_failed",
            }:
                selected = {key: value for key, value in payload.items() if key in {
                    "commandId", "actor", "actorObjectId", "interactionId", "interactionKey", "key",
                    "command", "playerId", "simObjectId", "reason", "message", "text",
                }}
                rows.append({"kind": kind, "ts": item.get("ts"), "payload": selected})
        except (ValueError, AttributeError):
            continue
    return {"truncated": size - start > limit, "receipts": rows}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server-pid", type=int, required=True, help="Rust front-door PID")
    parser.add_argument("--game-pids", type=int, nargs=3, required=True, metavar=("SERVER", "CLIENT1", "CLIENT2"))
    parser.add_argument("--client-controller-pids", type=int, nargs=2, required=True)
    parser.add_argument("--expected-sha256", required=True)
    parser.add_argument("--public-port", type=int, default=34019)
    parser.add_argument("--endpoints", nargs=3, default=["127.0.0.1:34029", "127.0.0.1:34030", "127.0.0.1:34031"])
    parser.add_argument("--seconds", type=float, default=5)
    parser.add_argument("--refusal-player", type=int, help="optional unused spare player for six named-refusal cases")
    parser.add_argument("--refusal-actor", type=int, default=4242)
    parser.add_argument("--client-logs", type=Path, nargs=2, help="bounded command-ID/lifecycle capture from the real clients")
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args(argv)
    session, claim = stack_claim.own_session(), stack_claim.current()
    if not session or not claim or claim[0] != session:
        parser.error("the running session must own the live-stack claim")
    if not 1 <= args.seconds <= 30:
        parser.error("--seconds must be in 1..30")
    result = {"verdict": "BLOCKED", "native_ts3_simulator_required": True,
              "command_execution": "not_assessed", "visual_acceptance": "not_assessed",
              "card55_card60_acceptance": "not_assessed"}
    try:
        processes = process_evidence(args.server_pid, args.game_pids, args.client_controller_pids, args.public_port)
        if processes["server_sha256"] != args.expected_sha256.lower():
            raise ValueError("running Rust server binary hash differs from the selected release")
        before = sample(args.endpoints)
        time.sleep(args.seconds)
        after = sample(args.endpoints)
        failures = evaluate(before, after, processes)
        result.update(processes=processes, samples=[before, after], failures=failures,
                      verdict="FAIL" if failures else "STREAM_READY")
        if args.refusal_player is not None:
            if args.refusal_player in stream_rows(after["stream"]):
                raise ValueError("refusal player is already joined; refusing to supersede it")
            refusals = rejection_relay_probe.run(f"127.0.0.1:{args.public_port}", wire="line", timeout=8,
                                                player=args.refusal_player, actor=args.refusal_actor)
            result["refusals"] = refusals
            if not refusals["passed"]:
                result["verdict"] = "FAIL"
        if args.client_logs:
            result["client_commands"] = [client_commands(path) for path in args.client_logs]
            result["command_execution"] = "receipts_captured_not_scored"
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        result["error"] = str(error)
        result["verdict"] = "BLOCKED"
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result))
    return 0 if result["verdict"] == "STREAM_READY" else 2


if __name__ == "__main__":
    raise SystemExit(main())
