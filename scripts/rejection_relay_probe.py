#!/usr/bin/env python3
"""Card 57 gate 6: named refusals, never silent drops, over the selected public wire."""
import argparse
import json
from pathlib import Path
import re
import socket
import time
import uuid

try:
    from .simctl import encode_line_command, LineReceiver, PUBLIC_LINE_MAX_BYTES
except ImportError:
    from simctl import encode_line_command, LineReceiver, PUBLIC_LINE_MAX_BYTES


def run(endpoint, wire="line", timeout=10, player=77, actor=4242):
    host, port = endpoint.rsplit(":", 1)
    cases = []
    with socket.create_connection((host.strip("[]"), int(port)), timeout=timeout) as sock, sock.makefile("rwb") as stream:
        receiver = LineReceiver()
        corr = 0
        if wire == "line":
            stream.write(b"HELLO|1|ring=1,wfs=1,role=client\n")
            stream.flush()
            response = stream.readline(PUBLIC_LINE_MAX_BYTES + 2).decode().rstrip("\r\n")
            if not response.startswith("WELCOME|1|"):
                return {"passed": False, "silent_drops": int(not response), "cases": [],
                        "error": "hello: " + response}

        def request(label, verb, fields=None, expected=None, refuse=True, raw=None):
            nonlocal corr
            corr += 1
            cid = str(uuid.uuid4())
            if raw is not None:
                line = raw.format(corr=corr, player=player, cid=cid)
            elif wire == "line":
                line = encode_line_command(verb, corr, corr, player, fields or {})
            else:
                line = json.dumps({"$type": verb, "commandId": cid, "playerId": player, **(fields or {})})
            stream.write((line + "\n").encode())
            stream.flush()
            deadline = time.monotonic() + timeout
            reply = None
            error = None
            try:
                while time.monotonic() < deadline:
                    sock.settimeout(max(0.001, deadline - time.monotonic()))
                    incoming = stream.readline(PUBLIC_LINE_MAX_BYTES + 2)
                    if not incoming:
                        break
                    if not incoming.endswith(b"\n") or len(incoming.rstrip(b"\r\n")) > PUBLIC_LINE_MAX_BYTES:
                        raise ValueError("line_too_long")
                    messages = receiver.receive(incoming.decode().rstrip("\r\n")) if wire == "line" else [json.loads(incoming)]
                    for message in messages:
                        if message.get("$type") in ("command_accepted", "command_rejected", "server_error") \
                                and (message.get("corr") == corr if wire == "line" else
                                     message.get("commandId") == cid):
                            reply = message
                            break
                    if reply is not None:
                        break
            except (OSError, ValueError) as exc:
                error = str(exc)
            reason = (reply or {}).get("reason") or (reply or {}).get("message") or ""
            code = reason.split("|", 1)[0]
            named = bool(re.fullmatch(r"[A-Za-z][A-Za-z0-9_]{0,63}", code)) if wire == "line" else bool(reason.strip())
            rejected = reply is not None and reply.get("$type") in ("command_rejected", "server_error")
            passed = rejected and named and (expected is None or code == expected) if refuse else \
                reply is not None and reply.get("$type") == "command_accepted"
            cases.append({"name": label, "request": line, "reply": reply, "passed": bool(passed),
                          "silent_drop": reply is None, "error": error})
            return reply is not None

        # Unjoined on the public port must be refused even from loopback in v1.
        alive = request("unjoined", "claim_sim", {"simObjectId": actor}, expected="NotJoined" if wire == "line" else None)
        if alive:
            alive = request("join", "join_session", {"displayName": "rejection-probe", "onScreen": None}, refuse=False)
        if alive:
            # Client is the public spectator/mirror role; no Sim has been claimed.
            alive = request("role", "set_client_role", {"role": "Client"}, refuse=False)
        if alive:
            alive = request("joined_unclaimed", "queue_interaction", {"interaction": {
                "actorObjectId": actor, "interactionKey": "BedSleep", "targetObjectId": None,
                "targetPosition": None, "parameters": {}}})
        if alive:
            alive = request("unknown", "", raw="CMD|{corr}|NoSuchCommand|{corr}|{player}" if wire == "line"
                            else '{{"$type":"no_such_command","commandId":"{cid}","playerId":{player}}}')
        if alive:
            request("malformed", "", raw="CMD|{corr}|QueueInteraction|{corr}|{player}" if wire == "line"
                    else '{{"$type":"queue_interaction","commandId":"{cid}","playerId":{player}}}')
    drops = sum(case["silent_drop"] for case in cases)
    return {"passed": len(cases) == 6 and all(case["passed"] for case in cases), "silent_drops": drops,
            "wire": wire, "endpoint": endpoint, "cases": cases}


def write_report(path, result):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.suffix == ".json":
        text = json.dumps(result, indent=2) + "\n"
    else:
        text = "\n".join(f"{case['name']}={'PASS' if case['passed'] else 'FAIL'} reply={json.dumps(case['reply'])}"
                         for case in result["cases"]) + "\n"
        text += f"silent_drops={result['silent_drops']}\nverdict={'PASS' if result['passed'] else 'FAIL'}\n"
        if result.get("error"):
            text += "error=" + result["error"] + "\n"
    path.write_text(text, encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--wire", choices=("line", "json"), default="line")
    parser.add_argument("--endpoint", default="127.0.0.1:34019")
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--timeout", type=float, default=10)
    parser.add_argument("--player", type=int, default=77)
    parser.add_argument("--actor", type=int, default=4242)
    args = parser.parse_args()
    try:
        result = run(args.endpoint, args.wire, args.timeout, args.player, args.actor)
    except (OSError, ValueError) as exc:
        result = {"passed": False, "silent_drops": 1, "cases": [], "error": str(exc)}
    write_report(args.out, result)
    print(f"verdict={'PASS' if result['passed'] else 'FAIL'} silent_drops={result['silent_drops']}")
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
