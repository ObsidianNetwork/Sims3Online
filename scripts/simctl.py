#!/usr/bin/env python3
"""simctl — admin CLI for the SimsOnline dedicated server public protocol.

Defaults to newline-delimited JSON on the public listener (127.0.0.1:34019).
Stdlib only. JSON mode never sends join_session — the listener only synthesizes a
LeaveSession (releasing all claims) for a connection's *joined* player, so an
un-joined admin connection can claim/command and disconnect without side effects.
Opt-in --wire line performs HELLO and joins public player connections. Use a spare
--player there; joining an existing player supersedes it. --admin-endpoint selects
Card 58's loopback listener for mutations without joining or waiting for broadcasts.
Read-only commands and name lookups use --endpoint, not the admin listener.

Commands:
  simctl sims [FILTER]               list Sims (name, id, owner, position)
  simctl claim NAME_OR_ID [...]      claim Sim(s) for --player (name = substring match)
  simctl release NAME_OR_ID [...]    release Sim(s)
  simctl set-active NAME_OR_ID       select active Sim in server TS3 (PlumbBob)
  simctl cancel SIM INTERACTION_ID   cancel a queued/running interaction by id
  simctl role server|client          declare player role (set_client_role)
  simctl save SAVE_NAME              checkpoint save via public save_game command
  simctl speed pause|normal|double|triple|ultra
  simctl worldtime [get|set --day N --hours H [--minutes M]]
                                       get: print current world clock from broadcast;
                                       set: issue set_world_time, wait for world_time_changed
                                       (set requires --day and --hours; --minutes optional)
  simctl gohere NAME_OR_ID X Y Z     queue Terrain.GoHere on a Sim (server-authoritative path)
  simctl synth-click NAME_OR_ID X Y Z  synthesize a CLIENT player click (INJECT_CLICK via client admin port)
  simctl interact SIM KEY [opts]     queue arbitrary interaction key
  simctl status                      session summary (tick, world, save, speed, claims)
  simctl watch [--filter FILTER]     print broadcast events until Ctrl+C

Snapshot cache: name resolution reads `.omo/run/sims-cache.json` unless --refresh.
"""
import argparse
import base64
from collections import deque
import ipaddress
import json
import os
import re
import socket
import sys
import time
import uuid

if __package__:
    from .local_endpoints import CLIENT_BRIDGECTL, PUBLIC_ENDPOINT
else:
    from local_endpoints import CLIENT_BRIDGECTL, PUBLIC_ENDPOINT

SCRIPTS = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(SCRIPTS)
CACHE_PATH = os.path.join(REPO, ".omo", "run", "sims-cache.json")

SPEEDS = {"pause": "Pause", "normal": "Normal", "double": "Speed2", "speed2": "Speed2",
          "triple": "Speed3", "speed3": "Speed3", "skip": "Ultra", "ultra": "Ultra"}
ROLES = {"server": "Server", "client": "Client"}

PUBLIC_LINE_MAX_BYTES = 4_194_304
PUBLIC_BULK_CHUNK_RAW_BYTES = 3_145_632
PUBLIC_MAX_BULK_BYTES = 256 * 1024 * 1024
PUBLIC_MAX_IN_FLIGHT_BULKS = 16
PUBLIC_MAX_PENDING_EVENTS = 1024
PUBLIC_COMMAND_ID_WINDOW = 32

# Card 57 positional declarations, not JSON property order. Keep the public record inventory
# explicit: no engine assemblies, source parsing, or external runtime is needed by this CLI.
_RECORD_FIELDS = {
    "Vector3Dto": "x:float y:float z:float",
    "WorldTime": "day:int hours:int minutes:int",
    "InteractionRequest": "actorObjectId:int interactionKey:str targetObjectId:int? targetPosition:Vector3Dto? parameters:map",
    "SimulationSnapshot": "tick:int worldName:str saveName:str speed:enum sims:SimSnapshot[] objects:ObjectSnapshot[]",
    "SimSnapshot": "objectId:int name:str householdId:int position:Vector3Dto currentInteractionId:int? owningPlayerId:int? currentInteractionKey:str? lotId:int? interactionQueueCount:int? postureContainerObjectId:int? postureType:str? simDescriptionId:int?",
    "ObjectSnapshot": "objectId:int scriptClass:str position:Vector3Dto lotId:int?",
    "SimBuffDto": "guid:int origin:int",
    "InteractionReplayBundle": "version:int actorObjectId:int interactionId:int interactionKey:str interactionInstanceType:str? interactionDefinitionType:str? targetObjectId:int? targetObjectType:str? targetPosition:Vector3Dto? priorityLevel:str? isUserDirected:bool? isAutonomous:bool? isTransitionInteraction:bool queueRole:enum postureType:str? postureContainerObjectId:int? hostTick:int? parameters:map policyHint:enum?",
    "LotSimExport": "objectId:int position:Vector3Dto currentInteractionKey:str?",
    "InventoryExportPayload": "slotIndex:int propertyStreamBytes:bytes resKeyTableBytes:bytes objectIdTableBytes:bytes itemCount:int",
    "HouseholdExportPayload": "householdId:int propertyStreamBytes:bytes resKeyTableBytes:bytes objectIdTableBytes:bytes entryCount:int resKeyCount:int objectIdCount:int simInventories:InventoryExportPayload[]? sharedFridgeInventory:InventoryExportPayload? sharedFamilyInventory:InventoryExportPayload?",
    "LotExportBundle": "playerId:int lotId:int clientTick:int sims:LotSimExport[] deepExport:HouseholdExportPayload?",
}
_COMMAND_FIELDS = {
    "JoinSession": "displayName:str onScreen:str?",
    "LeaveSession": "", "ClaimSim": "simObjectId:int", "ReleaseSim": "simObjectId:int",
    "SetActiveSim": "simObjectId:int", "QueueInteraction": "interaction:InteractionRequest",
    "CancelInteraction": "actorObjectId:int interactionId:int", "LeaveConversation": "actorObjectId:int",
    "AnswerDialog": "requestId:int simObjectId:int answer:str?",
    "RequestSimDescription": "simDescriptionId:int", "RequestWorld": "",
    "SetGameSpeed": "speed:enum", "SetWorldTime": "time:WorldTime",
    "RequestSnapshot": "sinceTick:int?", "SetClientRole": "role:enum", "SaveGame": "saveName:str",
    "AckSnapshot": "tick:int", "SubmitLotExport": "bundle:LotExportBundle clientLastReconciledVersion:int",
}
_EVENT_FIELDS = {
    "SessionJoined": "playerId:int displayName:str notice:str? capabilities:str[]?",
    "CommandAccepted": "commandId:id", "CommandRejected": "commandId:id reason:str",
    "SimClaimed": "playerId:int simObjectId:int lotId:int?", "SimReleased": "playerId:int simObjectId:int",
    "InteractionQueued": "actorObjectId:int interactionId:int interactionKey:str targetObjectId:int? targetPosition:Vector3Dto? parameters:map?",
    "InteractionReplayQueued": "bundle:InteractionReplayBundle",
    "InteractionStarted": "actorObjectId:int interactionId:int",
    "InteractionCompleted": "actorObjectId:int interactionId:int",
    "InteractionFailed": "actorObjectId:int interactionId:int reason:str",
    "SnapshotDelta": "snapshot:SimulationSnapshot forceFull:bool", "FullSnapshot": "snapshot:SimulationSnapshot",
    "GameSpeedChanged": "speed:enum", "WorldTimeChanged": "time:WorldTime",
    "SaveCheckpointed": "saveName:str at:str", "ServerError": "message:str",
    "ClientRoleChanged": "playerId:int role:enum", "SimAutonomyChanged": "simObjectId:int disabled:bool",
    "IntentCaptured": "actorObjectId:int targetObjectId:int interactionKey:str targetPosition:Vector3Dto?",
    "LotLocalAuthorityModeChanged": "playerId:int mode:enum",
    "LotExportRequested": "playerId:int lotId:int? reason:str",
    "LotReconcileAcknowledged": "playerId:int lotId:int version:int",
    "SimSkillChanged": "simObjectId:int skillName:str level:int", "HouseholdFundsChanged": "householdId:int funds:int",
    "SimMotiveChanged": "simObjectId:int motiveName:str value:float",
    "SimStateChanged": "simObjectId:int motives:float[] buffs:SimBuffDto[]",
    "SimSpawned": "simObjectId:int simDescriptionId:int position:Vector3Dto",
    "SimDespawned": "simObjectId:int simDescriptionId:int",
    "SimDescriptionData": "simDescriptionId:int propertyStream:bytes resKeyTable:bytes objectIdTable:bytes",
    "SimDetailsChanged": "simObjectId:int careerGuid:int careerLevel:int careerBranch:str funds:int skills:str",
    "WorldFileChunk": "folderName:str fileIndex:int fileCount:int fileName:str offset:int fileLength:int data:bytes",
    "SimAnimation": "lines:str[]",
    "UserIntentCaptured": "actorObjectId:int targetObjectId:int definitionType:str name:str path:str hit:str hitPoint:Vector3Dto? picked:str",
    "CancelIntentCaptured": "actorObjectId:int interactionId:int",
    "LeaveConversationIntentCaptured": "actorObjectId:int",
    "DialogRequested": "requestId:int simObjectId:int kind:str args:str[]",
    "DialogAnswerCaptured": "requestId:int simObjectId:int answer:str?",
}


def _snake(name):
    return re.sub(r"(?<!^)(?=[A-Z])", "_", name).lower()


def _fields(declaration):
    return [tuple(field.split(":")) for field in declaration.split()]


def _parts(text, opening="(", closing=")", separator=";"):
    if not text.startswith(opening) or not text.endswith(closing):
        raise ValueError("bad composite")
    inner = text[1:-1]
    if not inner:
        return []
    result, start, closers = [], 0, []
    for index, char in enumerate(inner):
        if char in "([":
            closers.append(")" if char == "(" else "]")
        elif char in ")]":
            if not closers or closers.pop() != char:
                raise ValueError("bad composite")
        elif char == separator and not closers:
            result.append(inner[start:index])
            start = index + 1
    if closers:
        raise ValueError("bad composite")
    return result + [inner[start:]]


def _encode(value, kind):
    if value is None and kind.endswith("?"):
        return "~"
    kind = kind.removesuffix("?")
    if kind.endswith("[]"):
        return "[" + ",".join(_encode(item, kind[:-2]) for item in value) + "]"
    if kind == "str":
        return value.encode("utf-8").hex()
    if kind == "bytes":
        return base64.b64decode(value, validate=True).hex()
    if kind == "map":
        return "[" + ",".join(f"({_encode(k, 'str')};{_encode(v, 'str')})" for k, v in value.items()) + "]"
    if kind == "bool":
        return "1" if value else "0"
    if kind == "enum":
        return value[0].upper() + value[1:]
    if kind in ("int", "float", "id"):
        return str(value)
    return "(" + ";".join(_encode(value.get(name), t) for name, t in _fields(_RECORD_FIELDS[kind])) + ")"


def _decode(text, kind):
    if text == "~" and kind.endswith("?"):
        return None
    kind = kind.removesuffix("?")
    if kind.endswith("[]"):
        return [_decode(item, kind[:-2]) for item in _parts(text, "[", "]", ",")]
    if kind == "str":
        return bytes.fromhex(text).decode("utf-8")
    if kind == "bytes":
        return base64.b64encode(bytes.fromhex(text)).decode()
    if kind == "map":
        return dict((_decode(pair[0], "str"), _decode(pair[1], "str"))
                    for pair in (_parts(item) for item in _parts(text, "[", "]", ",")))
    if kind in ("int", "id"):
        return int(text)
    if kind == "float":
        return float(text)
    if kind == "bool":
        if text not in ("0", "1"):
            raise ValueError("bad bool")
        return text == "1"
    if kind == "enum":
        return text[0].lower() + text[1:]
    return _decode_record(_parts(text), _RECORD_FIELDS[kind])


def _decode_record(values, declaration):
    fields = _fields(declaration)
    if len(values) != len(fields):
        raise ValueError("wrong field count")
    return {name: _decode(value, kind) for value, (name, kind) in zip(values, fields, strict=True)}


def encode_line_command(msg_type, corr, command_id, player_id, fields):
    verb = next((name for name in _COMMAND_FIELDS if _snake(name) == msg_type), None)
    if verb is None:
        raise ValueError("unknown command " + msg_type)
    values = [_encode(fields.get(name, 0 if name == "clientLastReconciledVersion" else None), kind)
              for name, kind in _fields(_COMMAND_FIELDS[verb])]
    return "|".join(["CMD", str(corr), verb, str(command_id), str(player_id), *values])


class LineReceiver:
    """Reassemble introduced fields and retain introducing-event order across interleaved bulks."""
    def __init__(self, max_bulk_bytes=PUBLIC_MAX_BULK_BYTES,
                 max_in_flight_bulks=PUBLIC_MAX_IN_FLIGHT_BULKS, max_pending_events=PUBLIC_MAX_PENDING_EVENTS):
        if max_bulk_bytes < 0 or max_in_flight_bulks <= 0 or max_pending_events <= 0:
            raise ValueError("invalid bulk limits")
        self.pending = deque()
        self.bulks = {}
        self.last_bulk = 0
        self.max_bulk_bytes = max_bulk_bytes
        self.max_in_flight_bulks = max_in_flight_bulks
        self.max_pending_events = max_pending_events
        self.bulk_bytes = 0

    def _drop(self, item):
        self.bulk_bytes -= item["bytes"]
        item["bytes"] = 0
        for bulk_id, owner in list(self.bulks.items()):
            if owner[0] is item:
                del self.bulks[bulk_id]
        self.pending = deque(pending for pending in self.pending if pending is not item)

    def receive(self, line):
        fields = line.split("|")
        if fields[0] == "BULK":
            item = None
            try:
                _, raw_id, raw_index, raw_count, encoded = fields
                bulk_id, index, count = int(raw_id), int(raw_index), int(raw_count)
                item, position, kind, next_index, prior_count, data = self.bulks[bulk_id]
                chunk = base64.b64decode(encoded, validate=True)
                if count < 1 or index != next_index or index >= count or prior_count not in (None, count) \
                        or len(chunk) > PUBLIC_BULK_CHUNK_RAW_BYTES \
                        or len(chunk) > self.max_bulk_bytes - len(data) \
                        or len(chunk) > self.max_bulk_bytes - self.bulk_bytes:
                    raise ValueError("bulk_malformed")
                data.extend(chunk)
                self.bulk_bytes += len(chunk)
                item["bytes"] += len(chunk)
                if index + 1 == count:
                    item["fields"][position] = data.hex() if kind == "bytes" else data.decode("utf-8")
                    del self.bulks[bulk_id]
                    item["remaining"].remove(bulk_id)
                else:
                    self.bulks[bulk_id] = (item, position, kind, index + 1, count, data)
            except (ValueError, KeyError, UnicodeError) as exc:
                if item is not None:
                    self._drop(item)
                raise ValueError("bulk_malformed") from exc
        else:
            tag = fields[0]
            if tag == "ACK":
                item = {"$type": "command_accepted", "corr": int(fields[1]), "verb": fields[2],
                        "fields": fields[3:], "remaining": set(), "bytes": 0}
                types = ["str"] * len(item["fields"])
            elif tag == "ERR":
                if len(fields) != 5:
                    raise ValueError("bad ERR frame")
                reason = fields[2]
                text = _decode(fields[4], "str")
                return [{"$type": "command_rejected", "corr": int(fields[1]), "verb": fields[3],
                         "reason": reason + ("| tooltip: " + text if text else "")}]
            elif tag in ("EV", "EVT"):
                if len(fields) < 5:
                    raise ValueError("bad event frame")
                kind = fields[4]
                if kind == "PickFailed":
                    if len(fields) != 7:
                        raise ValueError("bad PickFailed frame")
                    text = _decode(fields[6], "str")
                    return [{"$type": "command_rejected", "corr": int(fields[3]),
                             "reason": fields[5] + ("| tooltip: " + text if text else "")}]
                declaration = _EVENT_FIELDS[kind]
                types = [t for _, t in _fields(declaration)]
                item = {"$type": _snake(kind), "kind": kind, "fields": fields[5:], "remaining": set(), "bytes": 0}
                if kind in ("CommandRejected", "ServerError"):
                    item["corr"] = int(fields[3])
                if len(item["fields"]) != len(types):
                    raise ValueError("wrong field count")
            else:
                raise ValueError("bad server frame")
            references = []
            previous = self.last_bulk
            for position, (field, kind) in enumerate(zip(item["fields"], types, strict=True)):
                if re.fullmatch(r"b[0-9]+", field):
                    bulk_id = int(field[1:])
                    if bulk_id <= previous or bulk_id > 2**64 - 1:
                        raise ValueError("bulk_malformed")
                    previous = bulk_id
                    references.append((bulk_id, position, kind))
            if len(self.bulks) + len(references) > self.max_in_flight_bulks or len(self.pending) >= self.max_pending_events:
                raise ValueError("bulk_malformed")
            for bulk_id, position, kind in references:
                item["remaining"].add(bulk_id)
                self.bulks[bulk_id] = (item, position, kind, 0, None, bytearray())
            self.last_bulk = previous
            self.pending.append(item)
        ready = []
        while self.pending and not self.pending[0]["remaining"]:
            item = self.pending.popleft()
            self.bulk_bytes -= item["bytes"]
            if "kind" in item:
                message = {"$type": item["$type"], **_decode_record(item["fields"], _EVENT_FIELDS[item["kind"]])}
                if "corr" in item:
                    message["corr"] = item["corr"]
                ready.append(message)
            else:
                ready.append({k: v for k, v in item.items() if k not in ("fields", "remaining", "bytes")})
        return ready


class Conn:
    def __init__(self, endpoint: str, timeout: float, wire="json", player_id=1, admin=False, join=True):
        host, port = endpoint.rsplit(":", 1)
        if wire not in ("json", "line"):
            raise ValueError("--wire must be json or line")
        if admin and host != "localhost" and not ipaddress.ip_address(host.strip("[]")).is_loopback:
            raise ValueError("--admin-endpoint must be loopback")
        self.sock = socket.create_connection((host.strip("[]"), int(port)), timeout=timeout)
        self.f = self.sock.makefile("rwb")
        self.command_timeout = timeout
        self.wire, self.admin, self.player_id = wire, admin, player_id
        self.receiver = LineReceiver()
        self.ready = deque()
        self.ids, self.corr_ids = {}, {}
        self.rejected_corrs = {}
        self.last_reply_corr = 0
        self.next_corr = 0
        try:
            if wire == "line":
                self.write_line("HELLO|1|ring=1,wfs=1,role=client")
                welcome = self.read_line()
                if welcome and welcome.startswith("ERR|"):
                    refusal = self.receiver.receive(welcome)[0]
                    raise ConnectionError(refusal["reason"])
                parts = welcome.split("|") if welcome else []
                if len(parts) != 4 or parts[:2] != ["WELCOME", "1"]:
                    raise ConnectionError("expected WELCOME|1")
                self.epoch = int(parts[2])
                if not admin and join:
                    ok, reason, _ = self.command("join_session", playerId=player_id, displayName="simctl", onScreen=None)
                    if not ok:
                        raise ConnectionError("JoinSession refused: " + reason)
        except Exception:
            self.close()
            raise

    def write_line(self, line):
        raw = line.encode("utf-8")
        if len(raw) > PUBLIC_LINE_MAX_BYTES or b"\n" in raw or b"\r" in raw:
            raise ValueError("line_too_long or field separator")
        self.f.write(raw + b"\n")
        self.f.flush()

    def read_line(self):
        raw = self.f.readline(PUBLIC_LINE_MAX_BYTES + 2)
        if not raw:
            self.rejected_corrs.clear()
            self.last_reply_corr = 0
            return None
        line = raw.removesuffix(b"\n").removesuffix(b"\r")
        if not raw.endswith(b"\n") or len(line) > PUBLIC_LINE_MAX_BYTES:
            raise ValueError("line_too_long")
        return line.decode("utf-8")

    def send(self, msg_type: str, **fields):
        if self.wire == "line":
            cid = fields["commandId"]
            if cid not in self.ids:
                self.next_corr += 1
                self.ids[cid] = self.next_corr
                self.corr_ids[self.next_corr] = cid
            corr = self.ids[cid]
            self.write_line(encode_line_command(msg_type, corr, corr, fields["playerId"], fields))
            return
        # "$type" MUST be the first key (System.Text.Json polymorphic discriminator).
        obj = {"$type": msg_type, **fields}
        self.f.write((json.dumps(obj) + "\n").encode())
        self.f.flush()

    def recv(self):
        if self.wire == "line":
            while True:
                while not self.ready:
                    line = self.read_line()
                    if line is None:
                        return None
                    self.ready.extend(self.receiver.receive(line))
                message = self.ready.popleft()
                if "corr" in message:
                    corr = message.pop("corr")
                    if message["$type"] == "server_error":
                        if corr and self.rejected_corrs.get(corr) == message["message"]:
                            del self.rejected_corrs[corr]
                            continue
                        if corr in self.corr_ids:
                            self.last_reply_corr = max(self.last_reply_corr, corr)
                        return message
                    if corr == 0:
                        return {"$type": "server_error", "message": message.get("reason", "bad_line")}
                    if corr in self.corr_ids:
                        # Only an initial EV rejection pairs with a same-text driver error.
                        # Ordered replies exclude cached retries and checkpoint failures after ACK.
                        if message["$type"] == "command_rejected" and message.get("commandId") == corr \
                                and corr > self.last_reply_corr:
                            self.rejected_corrs[corr] = message["reason"]
                            if len(self.rejected_corrs) > PUBLIC_COMMAND_ID_WINDOW:
                                del self.rejected_corrs[next(iter(self.rejected_corrs))]
                        self.last_reply_corr = max(self.last_reply_corr, corr)
                    message["commandId"] = self.corr_ids.get(corr)
                elif message.get("$type") in ("command_accepted", "command_rejected"):
                    if message["commandId"] in self.corr_ids:
                        self.last_reply_corr = max(self.last_reply_corr, message["commandId"])
                    message["commandId"] = self.corr_ids.get(message["commandId"])
                return message
        line = self.f.readline()
        if not line:
            return None
        return json.loads(line.decode())

    def command(self, msg_type: str, wait_for=None, **fields):
        """Send a command, wait for command_accepted/command_rejected (+ optional extra event type)."""
        # Card 58 admins receive only replies, never the broadcast a public command waits for.
        if self.wire == "line" and self.admin:
            wait_for = None
        cid = str(uuid.uuid4())
        self.send(msg_type, commandId=cid, **fields)
        accepted = None
        extra = None
        deadline = time.time() + self.command_timeout
        while time.time() < deadline:
            if self.wire == "line":
                self.sock.settimeout(max(0.001, deadline - time.time()))
            msg = self.recv()
            if msg is None:
                raise ConnectionError("connection closed by server")
            t = msg.get("$type")
            if t == "command_accepted" and msg.get("commandId") == cid:
                accepted = True
            elif t == "command_rejected" and msg.get("commandId") == cid:
                return False, msg.get("reason", "(no reason)"), None
            elif t == "server_error":
                return False, msg.get("message", "(server error)"), None
            elif wait_for and t == wait_for:
                extra = msg
            if accepted and (extra is not None or wait_for is None):
                return True, None, extra
        raise TimeoutError(f"no response to {msg_type} within {self.command_timeout:.0f}s")

    def close(self):
        self.rejected_corrs.clear()
        self.last_reply_corr = 0
        try:
            self.f.close()
            self.sock.close()
        except OSError:
            pass


def load_cache():
    try:
        with open(CACHE_PATH, encoding="utf-8") as f:
            data = json.load(f)
        if data.get("snapshot"):
            return data["snapshot"]
    except (OSError, json.JSONDecodeError, TypeError):
        pass
    return None


def save_cache(snapshot):
    os.makedirs(os.path.dirname(CACHE_PATH), exist_ok=True)
    with open(CACHE_PATH, "w", encoding="utf-8") as f:
        json.dump({"fetchedAt": time.time(), "snapshot": snapshot}, f)


def get_snapshot(conn, refresh=False, quiet=False):
    if not refresh:
        cached = load_cache()
        if cached is not None:
            if not quiet:
                print(f"using cached snapshot ({CACHE_PATH})", file=sys.stderr)
            return cached
    if not quiet:
        print("requesting snapshot (takes ~15s over the chunked bridge)...", file=sys.stderr)
    if getattr(conn, "wire", "json") == "line" and conn.admin:
        # Read-only requests are public/unjoined exemptions (§E). Do not supersede the real
        # player's session just to resolve an admin command's Sim name.
        lookup = Conn(conn.public_endpoint, conn.command_timeout, wire="line", player_id=conn.player_id, join=False)
        try:
            return get_snapshot(lookup, refresh=True, quiet=True)
        finally:
            lookup.close()
    ok, reason, snap = conn.command("request_snapshot", wait_for="full_snapshot",
                                    playerId=conn.player_id, sinceTick=None)
    if not ok:
        sys.exit(f"snapshot rejected: {reason}")
    snapshot = snap["snapshot"]
    save_cache(snapshot)
    return snapshot


def resolve_sims(conn, tokens, sims=None, refresh=False):
    """Resolve each token to one or more Sims. Numeric token = exact ObjectId; text = substring match."""
    resolved = {}
    text_tokens = [t for t in tokens if not t.isdigit()]
    if text_tokens or sims is not None:
        sims = sims if sims is not None else get_snapshot(conn, refresh=refresh)["sims"]
    for tok in tokens:
        if tok.isdigit():
            resolved[int(tok)] = {"objectId": int(tok), "name": f"(id {tok})"}
            continue
        matches = [s for s in sims if tok.lower() in (s.get("name") or "").lower()]
        if not matches:
            sys.exit(f"no Sim matches '{tok}'")
        for m in matches:
            resolved[m["objectId"]] = m
    return list(resolved.values())


def cmd_sims(conn, args):
    snap = get_snapshot(conn, refresh=args.refresh)
    sims = snap["sims"]
    if args.filter:
        sims = [s for s in sims if args.filter.lower() in (s.get("name") or "").lower()]
    sims.sort(key=lambda s: s.get("name") or "")
    print(f"{'name':32} {'objectId':22} {'owner':6} position")
    for s in sims:
        p = s["position"]
        owner = s.get("owningPlayerId")
        print(f"{(s.get('name') or '?'):32} {s['objectId']:<22} {str(owner) if owner is not None else '-':6} "
              f"({p['x']:.1f}, {p['y']:.1f}, {p['z']:.1f})")
    print(f"\n{len(sims)} sims (world={snap.get('worldName')}, save={snap.get('saveName')}, "
          f"speed={snap.get('gameSpeed')}, tick={snap.get('tick')})")


def cmd_claim(conn, args, release=False):
    verb = "release_sim" if release else "claim_sim"
    for sim in resolve_sims(conn, args.sims, refresh=args.refresh):
        ok, reason, _ = conn.command(verb, playerId=conn.player_id, simObjectId=sim["objectId"])
        word = "released" if release else "claimed"
        if ok:
            print(f"{word} for player {conn.player_id}: {sim['name']} ({sim['objectId']})")
        else:
            print(f"{verb} REJECTED for {sim['name']}: {reason}")


def cmd_set_active(conn, args):
    sims = resolve_sims(conn, [args.sim], refresh=args.refresh)
    if len(sims) != 1:
        sys.exit(f"'{args.sim}' matched {len(sims)} Sims — be more specific")
    sim = sims[0]
    ok, reason, _ = conn.command("set_active_sim", playerId=conn.player_id, simObjectId=sim["objectId"])
    if ok:
        print(f"active sim set: {sim['name']} ({sim['objectId']})")
    else:
        sys.exit(f"set_active_sim REJECTED: {reason}")


def cmd_cancel(conn, args):
    sims = resolve_sims(conn, [args.sim], refresh=args.refresh)
    if len(sims) != 1:
        sys.exit(f"'{args.sim}' matched {len(sims)} Sims — be more specific")
    sim = sims[0]
    ok, reason, _ = conn.command(
        "cancel_interaction",
        playerId=conn.player_id,
        actorObjectId=sim["objectId"],
        interactionId=int(args.interaction_id),
    )
    if ok:
        print(f"cancelled interaction {args.interaction_id} on {sim['name']} ({sim['objectId']})")
    else:
        sys.exit(f"cancel_interaction REJECTED: {reason}")


def cmd_role(conn, args):
    role = ROLES.get(args.value.lower())
    if role is None:
        sys.exit(f"unknown role '{args.value}' (use: server, client)")
    ok, reason, evt = conn.command("set_client_role", playerId=conn.player_id, role=role)
    if ok:
        print(f"player {conn.player_id} role set: {role}" + (f" (event={evt['role']})" if evt else ""))
    else:
        sys.exit(f"set_client_role REJECTED: {reason}")


def cmd_save(conn, args):
    ok, reason, evt = conn.command("save_game", playerId=conn.player_id, saveName=args.save_name)
    if ok:
        print(f"save accepted: {args.save_name}" + (f" (checkpoint={evt['saveName']})" if evt else ""))
    else:
        sys.exit(f"save_game REJECTED: {reason}")


def cmd_speed(conn, args):
    speed = SPEEDS.get(args.value.lower())
    if speed is None:
        sys.exit(f"unknown speed '{args.value}' (use: {', '.join(sorted(set(SPEEDS)))})")
    ok, reason, evt = conn.command("set_game_speed", wait_for="game_speed_changed",
                                   playerId=conn.player_id, speed=speed)
    if ok:
        print(f"game speed set: {evt['speed'] if evt else speed}")
    else:
        sys.exit(f"set_game_speed REJECTED: {reason}")


def cmd_worldtime(conn, args):
    """get (print current world clock from broadcast) or set (issue set_world_time)."""
    if args.action == "set":
        if args.day is None or args.hours is None:
            sys.exit("worldtime set requires --day N --hours H [--minutes M] "
                     "(--day is mandatory: omitting it would silently rewind the "
                     "save to Sunday/day 0)")
        day = args.day
        hours = args.hours
        minutes = args.minutes if args.minutes is not None else 0
        ok, reason, evt = conn.command(
            "set_world_time", wait_for="world_time_changed",
            playerId=conn.player_id,
            time={"day": day, "hours": hours, "minutes": minutes})
        if ok:
            t = evt["time"] if evt else {"day": day, "hours": hours, "minutes": minutes}
            print(f"world time set: day {t['day']} {t['hours']:02d}:{t['minutes']:02d}")
        else:
            sys.exit(f"set_world_time REJECTED: {reason}")
    else:
        # get: listen for a world_time_changed broadcast. The session emits one on set/join and
        # periodically (every ~15 game-min via WorldTimeReconcilePolicy), so a fresh connection
        # usually catches one within a few seconds. If none arrives (no world time set yet, or
        # the periodic broadcast hasn't fired), exit with a hint rather than hanging. Bounded to
        # 2s so it matches the status command and never blocks the operator.
        conn.send("request_snapshot", commandId=str(uuid.uuid4()),
                  playerId=conn.player_id, sinceTick=None)
        old_timeout = conn.sock.gettimeout()
        conn.sock.settimeout(2.0)
        deadline = time.time() + 2.0
        try:
            while time.time() < deadline:
                msg = conn.recv()
                if msg is None:
                    raise ConnectionError("connection closed by server")
                if msg.get("$type") == "world_time_changed":
                    t = msg["time"]
                    print(f"world time: day {t['day']} {t['hours']:02d}:{t['minutes']:02d}")
                    return
        except (socket.timeout, OSError):
            pass
        finally:
            try:
                conn.sock.settimeout(old_timeout)
            except OSError:
                pass
        sys.exit("no world_time_changed broadcast received within 2s "
                 "(the session may not have a world time set yet; use "
                 "`simctl worldtime set --day N --hours H` to issue one, or check "
                 "via `bridgectl DIAG_STATE` for the live clock)")


def cmd_gohere(conn, args):
    sims = resolve_sims(conn, [args.sim], refresh=args.refresh)
    if len(sims) != 1:
        sys.exit(f"'{args.sim}' matched {len(sims)} Sims — be more specific for gohere")
    sim = sims[0]
    interaction = {"actorObjectId": sim["objectId"], "interactionKey": "Terrain.GoHere",
                   "targetObjectId": None,
                   "targetPosition": {"x": args.x, "y": args.y, "z": args.z},
                   "parameters": {}}
    ok, reason, _ = conn.command("queue_interaction", playerId=conn.player_id, interaction=interaction)
    if ok:
        print(f"queued Terrain.GoHere on {sim['name']} -> ({args.x}, {args.y}, {args.z})")
    else:
        sys.exit(f"queue_interaction REJECTED: {reason}")


def cmd_synth_click(conn, args):
    """T10 (DEV-115): synthesize a player terrain-click on a claimed Sim in the CLIENT TS3.

    Unlike `gohere` (public queue_interaction → server-authoritative queue), this drives the
    client mod's INJECT_CLICK, which enters the same P4.4 capture interceptor a human click
    does — capture → forward → queue → echo. Name is resolved on the public `conn` (ids are
    shared across both worlds); the INJECT_CLICK line goes to the CLIENT admin port (bridgectl
    line protocol, NOT the public protocol), since only the client mod owns the interceptor.
    """
    import bridgectl  # reuse the admin line protocol (connect/roundtrip) in one place
    sims = resolve_sims(conn, [args.sim], refresh=args.refresh)
    if len(sims) != 1:
        sys.exit(f"'{args.sim}' matched {len(sims)} Sims — be more specific for synth-click")
    sim = sims[0]
    line = f"INJECT_CLICK {sim['objectId']} {args.x} {args.y} {args.z}"
    sock = bridgectl.connect(args.client_endpoint, 25.0)
    try:
        resp = bridgectl.roundtrip(sock, line)
    finally:
        sock.close()
    if resp.get("ok") and resp.get("accepted"):
        print(f"synthetic click on {sim['name']} ({sim['objectId']}) -> ({args.x}, {args.y}, {args.z})")
    else:
        reason = resp.get("reason") or resp.get("error") or "unknown"
        sys.exit(f"INJECT_CLICK REJECTED: {reason}")


def cmd_interact(conn, args):
    sims = resolve_sims(conn, [args.sim], refresh=args.refresh)
    if len(sims) != 1:
        sys.exit(f"'{args.sim}' matched {len(sims)} Sims — be more specific")
    sim = sims[0]
    has_pos = args.x is not None and args.y is not None and args.z is not None
    interaction = {
        "actorObjectId": sim["objectId"],
        "interactionKey": args.key,
        "targetObjectId": int(args.target) if args.target else None,
        "targetPosition": {"x": args.x, "y": args.y, "z": args.z} if has_pos else None,
        "parameters": {},
    }
    ok, reason, _ = conn.command("queue_interaction", playerId=conn.player_id, interaction=interaction)
    if ok:
        target = f" target={args.target}" if args.target else ""
        pos = f" -> ({args.x}, {args.y}, {args.z})" if has_pos else ""
        print(f"queued {args.key} on {sim['name']}{target}{pos}")
    else:
        sys.exit(f"queue_interaction REJECTED: {reason}")


def cmd_status(conn, args):
    snap = get_snapshot(conn, refresh=args.refresh)
    claimed = [s for s in snap["sims"] if s.get("owningPlayerId") is not None]
    print(f"world={snap.get('worldName')}  save={snap.get('saveName')}  "
          f"speed={snap.get('gameSpeed')}  tick={snap.get('tick')}")
    # listen briefly for a world_time_changed broadcast (session emits one periodically
    # and on join). 2s non-blocking window; if none arrives, skip the time line (not fatal).
    old_timeout = conn.sock.gettimeout()
    conn.sock.settimeout(2.0)
    try:
        deadline = time.time() + 2.0
        while time.time() < deadline:
            msg = conn.recv()
            if msg is None:
                break
            if msg.get("$type") == "world_time_changed":
                t = msg["time"]
                print(f"world time: day {t['day']} {t['hours']:02d}:{t['minutes']:02d}")
                break
    except (socket.timeout, OSError):
        pass
    finally:
        conn.sock.settimeout(old_timeout)
    print(f"sims={len(snap['sims'])}  objects={len(snap.get('objects', []))}  claimed={len(claimed)}")
    for s in claimed:
        print(f"  player {s['owningPlayerId']}: {s.get('name')} ({s['objectId']})")


def watch_matches(msg, filt):
    if not filt:
        return True
    t = msg.get("$type") or ""
    if filt.lower() in t.lower():
        return True
    blob = json.dumps({k: v for k, v in msg.items() if k != "$type"})
    if filt.isdigit():
        return filt in blob
    return filt.lower() in blob.lower()


def cmd_watch(conn, args):
    print("watching broadcast events (Ctrl+C to stop)...", file=sys.stderr)
    conn.sock.settimeout(None)
    while True:
        msg = conn.recv()
        if msg is None:
            sys.exit("connection closed by server")
        if not watch_matches(msg, args.filter):
            continue
        print(f"[{time.strftime('%H:%M:%S')}] {msg.get('$type')}: "
              f"{json.dumps({k: v for k, v in msg.items() if k != '$type'})}")


def add_refresh_flag(parser):
    parser.add_argument("--refresh", action="store_true",
                        help="force a fresh bridge snapshot (ignore sims-cache.json)")


def main():
    ap = argparse.ArgumentParser(prog="simctl", description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--endpoint", default=PUBLIC_ENDPOINT)
    ap.add_argument("--wire", choices=("json", "line"), default="json")
    ap.add_argument("--admin-endpoint", help="Card 58 loopback admin listener; no public JoinSession")
    ap.add_argument("--player", type=int,
                    help="playerId to act as (required for line/public joins; JSON/admin defaults to 1)")
    ap.add_argument("--timeout", type=float, default=120.0,
                    help="socket connect timeout AND per-command response deadline (default 120s)")
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("sims"); p.add_argument("filter", nargs="?"); add_refresh_flag(p)
    p = sub.add_parser("claim"); p.add_argument("sims", nargs="+"); add_refresh_flag(p)
    p = sub.add_parser("release"); p.add_argument("sims", nargs="+"); add_refresh_flag(p)
    p = sub.add_parser("set-active"); p.add_argument("sim"); add_refresh_flag(p)
    p = sub.add_parser("cancel"); p.add_argument("sim"); p.add_argument("interaction_id"); add_refresh_flag(p)
    p = sub.add_parser("role"); p.add_argument("value", choices=["server", "client"])
    p = sub.add_parser("save"); p.add_argument("save_name")
    p = sub.add_parser("speed"); p.add_argument("value")
    p = sub.add_parser("worldtime", help="get (default) or set the world clock via set_world_time")
    p.add_argument("action", nargs="?", default="get", choices=["get", "set"])
    p.add_argument("--day", type=int, default=None, help="day of week 0-6 (required for set)")
    p.add_argument("--hours", type=int, default=None, help="hours 0-23 (required for set)")
    p.add_argument("--minutes", type=int, default=None, help="minutes 0-59 (optional for set)")
    p = sub.add_parser("gohere")
    p.add_argument("sim"); p.add_argument("x", type=float); p.add_argument("y", type=float); p.add_argument("z", type=float)
    add_refresh_flag(p)
    p = sub.add_parser("synth-click", help="synthesize a client-side player click (INJECT_CLICK via client admin port)")
    p.add_argument("sim"); p.add_argument("x", type=float); p.add_argument("y", type=float); p.add_argument("z", type=float)
    p.add_argument("--client-endpoint", default=CLIENT_BRIDGECTL,
                   help="client mod admin endpoint (defaults to SIMSONLINE_CLIENT_BRIDGECTL or 127.0.0.1:34030)")
    add_refresh_flag(p)
    p = sub.add_parser("interact")
    p.add_argument("sim"); p.add_argument("key")
    p.add_argument("--target", help="target object id")
    p.add_argument("--x", type=float); p.add_argument("--y", type=float); p.add_argument("--z", type=float)
    add_refresh_flag(p)
    p = sub.add_parser("status"); add_refresh_flag(p)
    p = sub.add_parser("watch"); p.add_argument("--filter", help="event $type substring or sim object id")

    args = ap.parse_args()
    admin = bool(args.admin_endpoint) and args.cmd not in ("sims", "status", "watch", "synth-click") \
        and not (args.cmd == "worldtime" and args.action == "get")
    if args.wire == "line" and not admin and args.player is None:
        ap.error("--wire line public commands require an explicit --player (joining supersedes that player's connection)")
    if args.player is None:
        args.player = 1
    conn = Conn(args.admin_endpoint if admin else args.endpoint, args.timeout, wire=args.wire,
                player_id=args.player, admin=admin)
    conn.public_endpoint = args.endpoint
    conn.player_id = args.player
    try:
        {"sims": cmd_sims, "claim": cmd_claim,
         "release": lambda c, a: cmd_claim(c, a, release=True),
         "set-active": cmd_set_active, "cancel": cmd_cancel, "role": cmd_role, "save": cmd_save,
         "speed": cmd_speed, "worldtime": cmd_worldtime, "gohere": cmd_gohere, "synth-click": cmd_synth_click, "interact": cmd_interact,
         "status": cmd_status, "watch": cmd_watch}[args.cmd](conn, args)
    except KeyboardInterrupt:
        pass
    finally:
        conn.close()


if __name__ == "__main__":
    main()
