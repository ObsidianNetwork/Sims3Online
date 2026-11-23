#!/usr/bin/env python3
"""Card 57 gate 5: strict scripted steps plus one run-wide broadcast schema check."""
import argparse
from collections import defaultdict
import json
from pathlib import Path
import queue
import re
import socket
import threading
import time
import uuid

try:
    from .simctl import Conn, LineReceiver, _COMMAND_FIELDS, _EVENT_FIELDS, _RECORD_FIELDS, _fields, _snake
except ImportError:
    from simctl import Conn, LineReceiver, _COMMAND_FIELDS, _EVENT_FIELDS, _RECORD_FIELDS, _fields, _snake


STEPS = ("join", "snapshot", "claim", "interact", "dialog", "save", "leave", "rejoin")
COMPARISON_STEPS = (*STEPS, "broadcasts")
VOLATILE = {
    "timestamp", "at", "tick", "hostTick", "clientTick", "sinceTick", "corr", "seq",
    "epoch", "commandId", "connectionId", "connectionLease", "leaseId",
}
LOCAL_IDS = {"interactionId", "currentInteractionId", "requestId"}
# SyncBroadcaster: presentation loop (241-288), clock/delta tick (291-529),
# and sampled motives/state/details (531-568). Claim, speed and lot-mode
# transitions remain strict even when a broadcaster also transports them.
CADENCE_EVENTS = {
    "world_time_changed", "snapshot_delta", "sim_animation",
    "sim_motive_changed", "sim_state_changed", "sim_details_changed",
}
STREAMS = {"full_snapshot", "snapshot_delta", "sim_animation"}
COMMAND_DECLARATIONS = {_snake(name): fields for name, fields in _COMMAND_FIELDS.items()}
EVENT_DECLARATIONS = {_snake(name): fields for name, fields in _EVENT_FIELDS.items()}
REFUSALS = {
    "UnauthorizedClientInsertion", "ClientWorldServiceSuppressed", "native_oracle_owned",
    "SlotAlreadyReserved", "hello_rejected", "bad_line", "ClientReselectForbidden",
    "NotJoined", "WrongPlayer", "Superseded", "ClientNativeModesOff", "IdSpaceFull",
    "TestFailed", "SimNotFound", "TargetNotFound", "NoMatchingInteraction",
    "PickedObjectNotFound", "InteractionNotFound", "queue_rejected", "dialog",
    "ObjectInUse", "RouteFailed", "FailedToStart", "NotRun", "line_too_long", "bulk_malformed",
}
PICK_FAILURES = {"ObjectInUse", "RouteFailed", "FailedToStart", "NotRun"}


class TranscriptReceiver(LineReceiver):
    """Keep EV PickFailed's event identity, which simctl's command facade flattens."""
    def receive(self, line):
        messages = super().receive(line)
        envelope = line.split("|", 5)
        if len(envelope) == 6 and envelope[0] in ("EV", "EVT") and envelope[4] == "PickFailed":
            for message in messages:
                message.pop("corr", None)
                message["$type"] = "pick_failed"
        return messages


def refusal(reason):
    """Only map conditions named by docs/protocol.md; unknown prose remains visible."""
    raw, marker, tooltip = reason.partition("| tooltip:")
    raw = raw.strip()
    if raw in REFUSALS:
        return raw, tooltip.strip() if marker else ""
    for prefix, code in (
        ("Sim not found", "SimNotFound"), ("Target not found", "TargetNotFound"),
        ("No matching interaction", "NoMatchingInteraction"),
        ("Picked object not found", "PickedObjectNotFound"),
        ("Interaction not found in queue or running", "InteractionNotFound"),
        ("Join the session first", "NotJoined"),
        ("Player is not joined", "NotJoined"),
        ("This connection is joined as player", "WrongPlayer"),
        ("This connection has been superseded", "Superseded"),
        ("Only server-role players may set world time", "UnauthorizedClientInsertion"),
    ):
        if raw.startswith(prefix):
            return code, ""
    if raw.startswith("Interaction refused") or marker:
        return "TestFailed", tooltip.strip() if marker else ""
    return raw, ""


class Normalizer:
    def __init__(self):
        self.ids = defaultdict(dict)

    def fields(self, value):
        if isinstance(value, list):
            return [self.fields(item) for item in value]
        if not isinstance(value, dict):
            return value
        result = {}
        for key, item in value.items():
            if key in VOLATILE or key in ("$type", "verb"):
                continue
            if key in LOCAL_IDS and item is not None:
                group = "dialog" if key == "requestId" else "interaction"
                ids = self.ids[group]
                result[key] = ids.setdefault(str(item), f"{group}:{len(ids) + 1}")
            else:
                result[key] = self.fields(item)
        return result


class Session:
    """One shared-codec connection, with a blocking reader and bounded consumer waits.

    A socket.makefile cannot be reused after a read timeout. Deadlines therefore belong
    to the queue consumer, not that buffered reader, so one missing ACK does not poison
    all subsequent commands or hide their events.
    """
    def __init__(self, args, emit):
        self.args, self.emit = args, emit
        self.conn = Conn(args.endpoint, args.timeout, wire=args.wire, player_id=args.player, join=False)
        self.conn.receiver = TranscriptReceiver()
        self.conn.sock.settimeout(None)
        self.incoming = queue.SimpleQueue()
        self.normalizer = Normalizer()
        self.commands = {}
        self.events = []
        self.step = "join"
        self.errors = 0
        self.ended = False
        self.fenced = False
        self.reader = threading.Thread(target=self.read, daemon=True)
        self.reader.start()

    def read(self):
        try:
            while (message := self.conn.recv()) is not None:
                self.incoming.put(message)
            self.incoming.put(ConnectionError("connection closed by server"))
        except (OSError, ValueError) as exc:
            self.incoming.put(exc)

    def receive(self, deadline):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return None
        try:
            message = self.incoming.get(timeout=remaining)
        except queue.Empty:
            return None
        if isinstance(message, Exception):
            self.ended = True
            if isinstance(message, ValueError):
                raise message
            raise ConnectionError(str(message))
        raw = message
        kind = message.get("$type")
        if kind == "server_error" and self.commands:
            # The JSON contract permits an idless named error. The scripted connection
            # has one outstanding command, so it is a reply, not silence or a push.
            message = {"$type": "command_rejected", "commandId": next(reversed(self.commands)),
                       "reason": message.get("message", "")}
            kind = "command_rejected"
        if kind == "command_rejected" and message.get("commandId") in (
                None, "00000000-0000-0000-0000-000000000000"):
            if refusal(message.get("reason", ""))[0] in PICK_FAILURES:
                kind = "pick_failed"
        if kind in ("command_accepted", "command_rejected"):
            command = self.commands.get(message.get("commandId"))
            if command is None:
                raise ValueError("reply has no matching command")
            step, verb = command
            accepted = kind == "command_accepted"
            code, text = ("", "") if accepted else refusal(message.get("reason", ""))
            barrier = step == "_barrier"
            self.emit({"kind": "barrier_reply" if barrier else "reply", "step": step, "command_type": verb,
                       "accepted": accepted, "code": code, "fields": {"text": text}, "raw": raw})
            if barrier and raw.get("$type") in ("command_accepted", "command_rejected"):
                self.fenced = True
        else:
            step = self.step
            if kind in ("dialog_requested", "dialog_answer_captured"):
                step = "dialog"
            elif kind == "save_checkpointed":
                step = "save"
            elif kind and kind.startswith("interaction_"):
                step = "interact"
            elif kind == "pick_failed":
                step = "interact"
            fields = {"stream_class": kind} if kind in STREAMS else self.normalizer.fields(message)
            if kind in ("pick_failed", "interaction_failed"):
                code, text = refusal(message.get("reason", ""))
                fields.pop("reason", None)
                fields.update(code=code, text=text)
            self.emit({"kind": "event", "step": step, "event_type": kind,
                       "fields": fields, "raw": raw})
            self.events.append(message)
        return message

    def command(self, step, verb, expected=(), **fields):
        self.step = step
        cid = str(uuid.uuid4())
        self.commands[cid] = (step, verb)
        self.emit({"kind": "command", "step": step, "command_type": verb,
                   "fields": self.normalizer.fields({"playerId": self.args.player, **fields})})
        self.conn.send(verb, commandId=cid, playerId=self.args.player, **fields)
        deadline = time.monotonic() + self.args.timeout
        pending = set(expected)
        reply = None
        while reply is None or (reply.get("$type") == "command_accepted" and pending):
            try:
                message = self.receive(deadline)
            except ConnectionError:
                if reply is None:
                    self.emit({"kind": "silent_drop", "step": step, "command_type": verb})
                    self.errors += 1
                raise
            if message is None:
                if reply is None:
                    self.emit({"kind": "silent_drop", "step": step, "command_type": verb})
                else:
                    self.emit({"kind": "error", "step": step, "error": "event_timeout",
                               "missing": sorted(pending)})
                self.errors += 1
                break
            if message.get("commandId") == cid and message.get("$type") in ("command_accepted", "command_rejected"):
                reply = message
            pending.discard(message.get("$type"))
        return reply

    def barrier(self):
        # docs/protocol.md:106,256-257: RequestSnapshot is read-only, joined or not.
        # Its correlated reply follows prior command events on this connection;
        # the barrier's own snapshot events follow its reply and are not consumed.
        tick = next(event["snapshot"]["tick"] for event in reversed(self.events)
                    if event.get("$type") == "full_snapshot")
        cid = str(uuid.uuid4())
        self.commands[cid] = ("_barrier", "request_snapshot")
        self.emit({"kind": "barrier", "step": "_barrier", "command_type": "request_snapshot",
                   "fields": {"sinceTick": tick}})
        self.conn.send("request_snapshot", commandId=cid, playerId=self.args.player, sinceTick=tick)
        deadline = time.monotonic() + self.args.timeout
        while not self.fenced:
            if self.receive(deadline) is None:
                raise TimeoutError("ordering barrier got no correlated reply")

    def close(self):
        try:
            self.conn.sock.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        self.reader.join(self.args.timeout)
        self.conn.close()
        if self.reader.is_alive():
            raise RuntimeError("protocol reader did not stop")


def save_stem(name):
    return name.casefold().removesuffix(".sims3")


def record(args):
    args.out.parent.mkdir(parents=True, exist_ok=True)
    order = 0
    session = None
    errors = 0
    with args.out.open("w", encoding="utf-8", newline="\n") as output:
        def emit(row):
            nonlocal order
            output.write(json.dumps({"order": order, **row}, sort_keys=True) + "\n")
            output.flush()
            order += 1

        emit({"kind": "run", "schema": 1, "wire": args.wire, "steps": list(STEPS)})
        try:
            if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]*(?:\.sims3)?", args.save_name):
                raise ValueError("--save-name must be a disposable save name, not a path")
            if not args.save_name.endswith(".sims3"):
                args.save_name += ".sims3"
            if save_stem(args.save_name) == save_stem(args.boot_save):
                raise ValueError("--save-name must never overwrite the boot save")
            session = Session(args, emit)
            session.command("join", "join_session", ("session_joined", "full_snapshot"),
                            displayName="card57-wire-diff", onScreen=None)
            session.command("snapshot", "request_snapshot", ("full_snapshot",), sinceTick=None)
            snapshots = [event["snapshot"] for event in session.events if event.get("$type") == "full_snapshot"]
            if not snapshots:
                raise ValueError("no snapshot: cannot choose a Sim or protect the boot save")
            snapshot = snapshots[-1]
            if not snapshot.get("saveName") or save_stem(args.save_name) == save_stem(snapshot["saveName"]):
                raise ValueError("scratch save must differ from the snapshot's loaded save")
            candidates = [sim for sim in snapshot["sims"]
                          if sim.get("householdId") and sim.get("owningPlayerId") in (None, args.player)]
            if args.sim:
                candidates = [sim for sim in candidates if str(sim["objectId"]) == args.sim
                              or args.sim.casefold() in (sim.get("name") or "").casefold()]
            if not candidates or (args.sim and len(candidates) != 1):
                raise ValueError("no unique claimable household Sim")
            sim = sorted(candidates, key=lambda item: item["objectId"])[0]
            claimed = session.command("claim", "claim_sim", ("sim_claimed",), simObjectId=sim["objectId"])
            if claimed is None or claimed["$type"] != "command_accepted":
                raise ValueError("claim did not succeed; scripted interaction cannot run")
            interaction = {
                "actorObjectId": sim["objectId"], "interactionKey": args.interaction,
                "targetObjectId": args.target,
                "targetPosition": dict(zip(("x", "y", "z"), args.position, strict=True))
                if args.position else (sim["position"] if args.target is None else None),
                "parameters": {},
            }
            session.command("interact", "queue_interaction", ("interaction_queued",), interaction=interaction)
            session.step = "dialog"
            deadline = time.monotonic() + args.dialog_wait
            dialog = next((event for event in session.events if event.get("$type") == "dialog_requested"), None)
            while dialog is None:
                event = session.receive(deadline)
                if event is None:
                    break
                if event.get("$type") == "dialog_requested":
                    dialog = event
            if dialog is None:
                emit({"kind": "observation", "step": "dialog", "dialog": "none"})
            else:
                emit({"kind": "observation", "step": "dialog", "dialog": "requested"})
                session.command("dialog", "answer_dialog", requestId=dialog["requestId"],
                                simObjectId=dialog["simObjectId"], answer=args.answer)
            session.command("save", "save_game", ("save_checkpointed",), saveName=args.save_name)
            session.command("leave", "leave_session")
            session.command("rejoin", "join_session", ("session_joined", "full_snapshot"),
                            displayName="card57-wire-diff", onScreen=None)
            session.barrier()
        except (OSError, ValueError) as exc:
            errors += 1
            emit({"kind": "error", "step": session.step if session else "join", "error": str(exc)})
        finally:
            if session:
                # Stop the reader first, so draining its decoded events has no empty-queue race.
                session.close()
                while not session.fenced and not session.ended and not session.incoming.empty():
                    try:
                        session.receive(time.monotonic() + args.timeout)
                    except ConnectionError:
                        break
                    except ValueError as exc:
                        emit({"kind": "error", "step": session.step, "error": str(exc)})
                        session.errors += 1
                        break
                errors += session.errors
            emit({"kind": "end", "complete": errors == 0, "errors": errors})
    print(f"recorded {args.wire}: {args.out} errors={errors}")
    return int(errors != 0)


def load_recording(path):
    with path.open(encoding="utf-8") as source:
        rows = [json.loads(line) for line in source if line.strip()]
    if not rows or any(not isinstance(row, dict) for row in rows):
        raise ValueError(f"{path}: expected nonempty object records")
    if rows[0].get("kind") != "run" or rows[0].get("schema") != 1:
        raise ValueError(f"{path}: missing run header")
    if rows[0].get("steps") != list(STEPS) or rows[-1].get("kind") != "end":
        raise ValueError(f"{path}: incomplete scripted recording")
    if [row.get("order") for row in rows] != list(range(len(rows))):
        raise ValueError(f"{path}: invalid order indexes")
    return rows


def declaration_fields(declaration):
    definition = declaration if ":" in declaration else _RECORD_FIELDS.get(declaration.removesuffix("?"), "")
    return dict(_fields(definition))


def normalize_fields(value, declaration=""):
    """Fill only codec-declared nullable keys; required/unknown missing keys stay missing."""
    if isinstance(value, list):
        kind = declaration.removesuffix("?")
        return [normalize_fields(item, kind[:-2] if kind.endswith("[]") else "") for item in value]
    if not isinstance(value, dict):
        return value
    types = declaration_fields(declaration)
    result = {key: normalize_fields(item, types.get(key, "")) for key, item in value.items()
              if key not in VOLATILE and key not in ("$type", "verb")}
    for key, kind in types.items():
        if kind.endswith("?") and key not in VOLATILE:
            result.setdefault(key, None)
    return result


def declared_schema(kind):
    """An empty typed array has no samples, but its element shape is still known."""
    kind = kind.removesuffix("?")
    if kind.endswith("[]"):
        return {"[]": [declared_schema(kind[:-2])]}
    fields = declaration_fields(kind)
    return {key: declared_schema(value) for key, value in fields.items() if key not in VOLATILE} if fields else "*"


def field_schema(value, declaration=""):
    if isinstance(value, dict):
        types = declaration_fields(declaration)
        return {key: field_schema(item, types.get(key, "")) for key, item in value.items()}
    if isinstance(value, list):
        kind = declaration.removesuffix("?")
        item_kind = kind[:-2] if kind.endswith("[]") else ""
        schemas = [field_schema(item, item_kind) for item in value] or [declared_schema(item_kind)]
        return {"[]": [json.loads(item) for item in sorted({json.dumps(item, sort_keys=True) for item in schemas})]}
    if value is None and declaration.endswith("?"):
        return declared_schema(declaration)
    return "*"


def broadcast_signature(rows):
    classes = defaultdict(set)
    for row in rows:
        event_type = row.get("event_type")
        if row.get("kind") != "event" or event_type not in CADENCE_EVENTS:
            continue
        fields = row.get("raw", row["fields"])
        if "stream_class" in fields:
            raise ValueError("broadcast schema needs raw decoded fields, not only a stream-class marker")
        declaration = EVENT_DECLARATIONS[event_type]
        fields = normalize_fields(fields, declaration)
        if event_type == "world_time_changed" and isinstance(fields.get("time"), dict):
            # docs/protocol.md:44: Hours24 is computed, not part of the positional v1 record.
            fields["time"].pop("hours24", None)
        schema = field_schema(fields, declaration)
        classes[event_type].add(json.dumps(schema, sort_keys=True))
    return {name: [json.loads(schema) for schema in sorted(schemas)] for name, schemas in classes.items()}


def signature(rows, step):
    groups = {key: [] for key in ("command", "reply", "event", "observation", "error")}
    for row in rows:
        kind = row.get("kind")
        if row.get("step") != step or kind not in groups:
            continue
        if kind == "event" and row.get("event_type") in CADENCE_EVENTS:
            continue
        semantic = {key: value for key, value in row.items() if key not in ("order", "raw", "step", "kind")}
        if "fields" in semantic:
            declaration = COMMAND_DECLARATIONS.get(row.get("command_type"), "") if kind == "command" else \
                EVENT_DECLARATIONS.get(row.get("event_type"), "") if kind == "event" else ""
            semantic["fields"] = normalize_fields(semantic["fields"], declaration)
        if "saveName" in semantic.get("fields", {}):
            semantic["fields"] = {**semantic["fields"], "saveName": save_stem(semantic["fields"]["saveName"])}
        groups[kind].append(semantic)
    return groups


def field_diff(left, right, path=""):
    if isinstance(left, dict) and isinstance(right, dict):
        result = []
        for key in sorted(left.keys() | right.keys()):
            field = f"{path}.{key}".lstrip(".")
            if key not in left or key not in right:
                result.append({"field": field, "json": left.get(key, {"absent": True}),
                               "line": right.get(key, {"absent": True})})
            else:
                result.extend(field_diff(left[key], right[key], field))
        return result
    if isinstance(left, list) and isinstance(right, list) and len(left) == len(right):
        return [difference for i, (a, b) in enumerate(zip(left, right, strict=True))
                for difference in field_diff(a, b, f"{path}[{i}]")]
    return [] if left == right else [{"field": path, "json": left, "line": right}]


def compare(args):
    mismatches = drops = 0
    results = []
    try:
        left, right = load_recording(args.json_run), load_recording(args.line_run)
        if left[0].get("wire") != "json" or right[0].get("wire") != "line":
            raise ValueError("compare expects JSON recording first, line recording second")
        for step in STEPS:
            a, b = signature(left, step), signature(right, step)
            differences = field_diff(a, b)
            silent = []
            for wire, rows in (("json", left), ("line", right)):
                commands = [row for row in rows if row.get("step") == step and row.get("kind") == "command"]
                replies = [row for row in rows if row.get("step") == step and row.get("kind") == "reply"]
                for command in commands:
                    timed_out = any(row.get("step") == step and row.get("kind") == "silent_drop"
                                    and row.get("command_type") == command["command_type"] for row in rows)
                    if timed_out or not any(reply["command_type"] == command["command_type"] for reply in replies):
                        silent.append({"wire": wire, "command_type": command["command_type"]})
            drops += len(silent)
            invalid = bool(a["error"] or b["error"])
            # Dialog can legitimately contain no command, but never no observation.
            if not a["command"] or not b["command"]:
                invalid |= step != "dialog" or not a["observation"] or not b["observation"]
            mismatched = bool(differences or invalid)
            mismatches += int(mismatched)
            results.append({"step": step, "status": "silent_drop" if silent else
                            ("mismatch" if mismatched else "match"), "diff": differences,
                            "silent_drop": silent, "invalid": invalid})
        differences = field_diff(broadcast_signature(left), broadcast_signature(right), "broadcasts")
        mismatches += int(bool(differences))
        results.append({"step": "broadcasts", "status": "mismatch" if differences else "match",
                        "diff": differences, "silent_drop": [], "invalid": False})
        if any(not rows[-1].get("complete") and not any(row.get("kind") in ("error", "silent_drop")
                                                       for row in rows) for rows in (left, right)):
            raise ValueError("recording ended unsuccessfully without diagnostic evidence")
    except (OSError, ValueError, KeyError, TypeError) as exc:
        results = [{"step": "recording", "status": "mismatch",
                    "diff": [{"field": "artifact", "error": str(exc)}]}]
        mismatches = 1
        drops = 0
    summary = {"summary": True, "steps": len(results), "mismatches": mismatches, "silent_drops": drops}
    args.out.parent.mkdir(parents=True, exist_ok=True)
    with args.out.open("w", encoding="utf-8", newline="\n") as output:
        for row in [*results, summary]:
            output.write(json.dumps(row, sort_keys=True) + "\n")
    print(json.dumps(summary, sort_keys=True))
    return int(mismatches != 0 or drops != 0)


def positive(text):
    value = float(text)
    if not 0 < value < float("inf"):
        raise argparse.ArgumentTypeError("must be finite and greater than zero")
    return value


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="action", required=True)
    rec = sub.add_parser("record", help="drive one already-running disposable server")
    rec.add_argument("--wire", choices=("json", "line"), required=True)
    rec.add_argument("--endpoint", default="127.0.0.1:34019")
    rec.add_argument("--out", type=Path, required=True)
    rec.add_argument("--player", type=int, default=77)
    rec.add_argument("--sim", help="household Sim id or unique name substring; default first claimable id")
    rec.add_argument("--interaction", default="Terrain.GoHere")
    rec.add_argument("--target", type=int, help="target object id; default terrain at the actor's safe current spot")
    rec.add_argument("--position", type=float, nargs=3, metavar=("X", "Y", "Z"))
    rec.add_argument("--answer", default="yes", help="answer for a requested dialog")
    rec.add_argument("--dialog-wait", type=positive, default=5)
    rec.add_argument("--timeout", type=positive, default=60, help="per-command reply/event deadline in seconds")
    rec.add_argument("--save-name", default="card57-wire-scratch.sims3")
    rec.add_argument("--boot-save", default="Option4BookChain.sims3")
    cmp = sub.add_parser("compare", help="eight strict steps plus run-wide cadence presence/field schemas")
    cmp.add_argument("json_run", type=Path)
    cmp.add_argument("line_run", type=Path)
    cmp.add_argument("--out", type=Path, required=True)
    args = parser.parse_args(argv)
    return record(args) if args.action == "record" else compare(args)


if __name__ == "__main__":
    raise SystemExit(main())
