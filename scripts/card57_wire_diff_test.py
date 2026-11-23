"""Card 57's two recordings exercise the shared codecs over real TCP sockets."""
import contextlib
import copy
import io
import json
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch

from scripts import card57_wire_diff as diff
from scripts import simctl
from scripts.simctl_test import hello, peer, write


class WireDiffTests(unittest.TestCase):
    def record(self, folder, wire, *, drop=False, refusal=None, volatile=1, dialog=True,
               world="Test", failure=None, extra_delta=False, server_error=False,
               streams=(), save_name="card57-scratch.sims3", bare_checkpoint=False,
               trailing=False, barrier_drop=False, buffer_barrier=False):
        snapshot = {
            "tick": volatile, "worldName": world, "saveName": "DisposableBoot",
            "speed": "normal", "objects": [],
            "sims": [{
                "objectId": 101, "name": "Test Sim", "householdId": 100,
                "position": {"x": 1.0, "y": 2.0, "z": 3.0},
                "currentInteractionId": None, "owningPlayerId": None,
                "currentInteractionKey": None, "lotId": 10, "interactionQueueCount": 0,
                "postureContainerObjectId": None, "postureType": None, "simDescriptionId": 12,
            }],
        }
        seen = []

        def server(stream):
            if wire == "line":
                hello(stream)
            seq = 0
            delayed = None
            batching = False
            pending = []

            def send(line):
                if batching:
                    pending.append(line)
                else:
                    write(stream, line)

            def event(name, **fields):
                nonlocal seq
                seq += 1
                if wire == "json":
                    send(json.dumps({"$type": simctl._snake(name), **fields,
                                     "tick": volatile, "epoch": volatile, "seq": seq}))
                else:
                    encoded = [simctl._encode(fields.get(key), kind)
                               for key, kind in simctl._fields(simctl._EVENT_FIELDS[name])]
                    send("|".join(["EV", str(seq), str(volatile), "0", name, *encoded]))

            while raw := stream.readline():
                if wire == "json":
                    command = json.loads(raw)
                    name = next(k for k in simctl._COMMAND_FIELDS if simctl._snake(k) == command["$type"])
                    cid = command["commandId"]
                    corr = None
                else:
                    parts = raw.decode().strip().split("|")
                    corr, name = parts[1:3]
                    cid = parts[3]
                    command = simctl._decode_record(parts[5:], simctl._COMMAND_FIELDS[name])
                seen.append(name)
                if name == "RequestSnapshot" and command.get("sinceTick") is not None:
                    self.assertEqual(seen[-2], "JoinSession")
                    self.assertEqual(command["sinceTick"], snapshot["tick"])
                    batching = True
                    # The tail is released only after observing the ordering barrier,
                    # so the test cannot pass by a lucky read before shutdown.
                    if trailing:
                        event("LotLocalAuthorityModeChanged", playerId=77, mode="fullPuppetFallback")
                    if not barrier_drop:
                        send(json.dumps({"$type": "command_accepted", "commandId": cid})
                             if wire == "json" else f"ACK|{corr}|{name}")
                        event("SnapshotDelta", snapshot={**snapshot, "worldName": "BarrierOnly"}, forceFull=False)
                    if pending:
                        stream.write(("\n".join(pending) + "\n").encode())
                        stream.flush()
                    pending.clear()
                    batching = False
                    continue
                if delayed is not None and name == "LeaveSession":
                    write(stream, delayed)
                if drop == "late" and name == "SaveGame":
                    delayed = json.dumps({"$type": "command_accepted", "commandId": cid}) \
                        if wire == "json" else f"ACK|{corr}|{name}"
                if not (drop and name == "SaveGame"):
                    if name == "QueueInteraction" and server_error:
                        write(stream, json.dumps({"$type": "server_error", "message": "bad_line"})
                              if wire == "json" else "ERR|0|bad_line|-|")
                    elif name == "QueueInteraction" and refusal:
                        reason = refusal if wire == "line" else "Target not found: 500"
                        if wire == "json":
                            write(stream, json.dumps({"$type": "command_rejected", "commandId": cid,
                                                      "reason": reason}))
                        else:
                            write(stream, f"ERR|{corr}|{reason}|{name}|")
                    elif wire == "json":
                        write(stream, json.dumps({"$type": "command_accepted", "commandId": cid}))
                    else:
                        write(stream, f"ACK|{corr}|{name}")
                if name == "JoinSession":
                    event("SessionJoined", playerId=77, displayName="card57-wire-diff",
                          notice=None, capabilities=[])
                    event("FullSnapshot", snapshot=snapshot)
                elif name == "RequestSnapshot":
                    if extra_delta:
                        event("SnapshotDelta", snapshot=snapshot, forceFull=False)
                    for stream_type in streams:
                        if stream_type == "SnapshotDelta":
                            event(stream_type, snapshot=snapshot, forceFull=False)
                        else:
                            event(stream_type, lines=[f"volatile animation batch {volatile}"])
                    event("FullSnapshot", snapshot=snapshot)
                elif name == "ClaimSim":
                    self.assertEqual(command["simObjectId"], 101)
                    event("SimClaimed", playerId=77, simObjectId=101, lotId=10)
                elif name == "QueueInteraction":
                    if dialog:
                        event("DialogRequested", requestId=volatile, simObjectId=101,
                              kind="Confirm", args=["Continue?"])
                    if failure:
                        if wire == "json":
                            write(stream, json.dumps({"$type": "command_rejected",
                                                      "commandId": "00000000-0000-0000-0000-000000000000",
                                                      "reason": failure}))
                        else:
                            write(stream, f"EV|{seq + 1}|{volatile}|{corr}|PickFailed|{failure}|")
                            seq += 1
                    if not refusal:
                        event("InteractionQueued", actorObjectId=101, interactionId=volatile,
                              interactionKey=command["interaction"]["interactionKey"],
                              targetObjectId=None, targetPosition=snapshot["sims"][0]["position"],
                              parameters={})
                elif name == "AnswerDialog":
                    self.assertEqual(command["requestId"], volatile)
                    event("DialogAnswerCaptured", requestId=volatile, simObjectId=101, answer="yes")
                elif name == "SaveGame":
                    self.assertEqual(command["saveName"], "card57-scratch.sims3")
                    event("SaveCheckpointed", saveName=command["saveName"].removesuffix(".sims3")
                          if bare_checkpoint else command["saveName"], at=f"time-{volatile}")

        path = Path(folder) / f"{wire}_run.jsonl"
        decoded = threading.Event()
        original_close = diff.Session.close

        class BarrierQueue(diff.queue.SimpleQueue):
            def put(self, message):
                super().put(message)
                if isinstance(message, dict) and message.get("$type") == "snapshot_delta" \
                        and message.get("snapshot", {}).get("worldName") == "BarrierOnly":
                    decoded.set()

        def close_after_payload(session):
            self.assertTrue(decoded.wait(2), "barrier payload was not queued before shutdown")
            original_close(session)

        queue_hook = patch.object(diff.queue, "SimpleQueue", BarrierQueue) if buffer_barrier else contextlib.nullcontext()
        close_hook = patch.object(diff.Session, "close", close_after_payload) if buffer_barrier else contextlib.nullcontext()
        with peer(server) as endpoint, contextlib.redirect_stdout(io.StringIO()), queue_hook, close_hook:
            code = diff.main(["record", "--wire", wire, "--endpoint", endpoint,
                              "--out", str(path), "--player", "77", "--save-name", save_name,
                              "--timeout", "0.1" if drop or barrier_drop else "2",
                              "--dialog-wait", "0.05", "--answer", "yes"])
        return path, code, seen

    def compare(self, folder, left, right):
        out = Path(folder) / "wire_diff.jsonl"
        with contextlib.redirect_stdout(io.StringIO()):
            code = diff.main(["compare", str(left), str(right), "--out", str(out)])
        rows = [json.loads(line) for line in out.read_text(encoding="utf-8").splitlines()]
        return code, rows

    def test_identical_session_matches_with_dialog_and_rejoin(self):
        with tempfile.TemporaryDirectory() as folder:
            left, left_code, seen = self.record(folder, "json")
            right, right_code, _ = self.record(folder, "line")
            code, rows = self.compare(folder, left, right)
        self.assertEqual((left_code, right_code, code), (0, 0, 0), rows)
        self.assertEqual(seen, ["JoinSession", "RequestSnapshot", "ClaimSim", "QueueInteraction",
                                "AnswerDialog", "SaveGame", "LeaveSession", "JoinSession", "RequestSnapshot"])
        self.assertEqual(rows[-1], {"summary": True, "steps": 9, "mismatches": 0, "silent_drops": 0})

    def test_final_barrier_captures_trailing_event_and_excludes_its_own_traffic(self):
        with tempfile.TemporaryDirectory() as folder:
            left, a, _ = self.record(folder, "json", trailing=True, buffer_barrier=True)
            right, b, _ = self.record(folder, "line", trailing=True, buffer_barrier=True)
            for path in (left, right):
                records = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]
                tail = [row for row in records if row.get("event_type") == "lot_local_authority_mode_changed"]
                self.assertEqual(len(tail), 1)
                self.assertEqual(tail[0]["step"], "rejoin")
                self.assertFalse(any(row.get("event_type") == "snapshot_delta" for row in records))
                self.assertEqual(sum(row["kind"] == "barrier" for row in records), 1)
                self.assertEqual(sum(row["kind"] == "barrier_reply" for row in records), 1)
            code, rows = self.compare(folder, left, right)
        self.assertEqual((a, b, code), (0, 0, 0), rows)

    def test_missing_barrier_reply_cannot_certify_a_complete_recording(self):
        with tempfile.TemporaryDirectory() as folder:
            for wire in ("json", "line"):
                with self.subTest(wire=wire):
                    path, code, _ = self.record(folder, wire, barrier_drop=True)
                    rows = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]
                    self.assertEqual(code, 1)
                    self.assertFalse(rows[-1]["complete"])
                    self.assertTrue(any(row["kind"] == "error" for row in rows))

    def test_one_dropped_reply_is_one_silent_drop_and_exit_one(self):
        with tempfile.TemporaryDirectory() as folder:
            left, _, _ = self.record(folder, "json")
            right, record_code, seen = self.record(folder, "line", drop=True)
            code, rows = self.compare(folder, left, right)
        self.assertEqual(record_code, 1)
        self.assertEqual(code, 1)
        self.assertEqual(rows[-1]["silent_drops"], 1, rows)
        self.assertIn("LeaveSession", seen)
        self.assertTrue(any(row.get("status") == "silent_drop" for row in rows))

    def test_refusal_code_difference_is_a_field_mismatch(self):
        with tempfile.TemporaryDirectory() as folder:
            left, _, _ = self.record(folder, "json", refusal="TargetNotFound")
            right, _, _ = self.record(folder, "line", refusal="SimNotFound")
            code, rows = self.compare(folder, left, right)
        self.assertEqual(code, 1)
        self.assertGreater(rows[-1]["mismatches"], 0)
        self.assertIn("TargetNotFound", json.dumps(rows))
        self.assertIn("SimNotFound", json.dumps(rows))

    def test_prose_refusal_maps_to_same_code_and_keeps_raw(self):
        with tempfile.TemporaryDirectory() as folder:
            left, _, _ = self.record(folder, "json", refusal="TargetNotFound")
            right, _, _ = self.record(folder, "line", refusal="TargetNotFound")
            code, rows = self.compare(folder, left, right)
            transcript = left.read_text(encoding="utf-8")
        self.assertEqual(code, 0, rows)
        self.assertIn("Target not found: 500", transcript)

    def test_client_role_world_time_prose_matches_rust_refusal_code(self):
        self.assertEqual(diff.refusal("Only server-role players may set world time."),
                         ("UnauthorizedClientInsertion", ""))

    def test_volatile_fields_and_snapshot_bytes_do_not_mismatch(self):
        with tempfile.TemporaryDirectory() as folder:
            left, _, _ = self.record(folder, "json", volatile=10)
            right, _, _ = self.record(folder, "line", volatile=999, world="Different snapshot payload")
            code, rows = self.compare(folder, left, right)
        self.assertEqual(code, 0, rows)

    def test_stream_type_presence_difference_is_not_masked(self):
        with tempfile.TemporaryDirectory() as folder:
            left, _, _ = self.record(folder, "json")
            right, _, _ = self.record(folder, "line", extra_delta=True)
            code, rows = self.compare(folder, left, right)
        self.assertEqual(code, 1)
        self.assertGreater(rows[-1]["mismatches"], 0)

    def test_cadence_counts_two_vs_five_match_by_presence_and_ordered_class(self):
        with tempfile.TemporaryDirectory() as folder:
            left, _, _ = self.record(folder, "json", streams=("SnapshotDelta", "SimAnimation") * 2)
            right, _, _ = self.record(folder, "line", streams=("SnapshotDelta", "SimAnimation") * 5,
                                      volatile=999)
            code, rows = self.compare(folder, left, right)
        self.assertEqual(code, 0, rows)

    def test_cadence_first_type_order_is_not_a_semantic_order(self):
        with tempfile.TemporaryDirectory() as folder:
            left, _, _ = self.record(folder, "json", streams=("SnapshotDelta", "SimAnimation") * 2)
            right, _, _ = self.record(folder, "line", streams=("SimAnimation", "SnapshotDelta") * 5)
            code, rows = self.compare(folder, left, right)
        self.assertEqual(code, 0, rows)

    def test_scratch_extension_is_sent_on_both_wires_and_checkpoint_extension_is_neutral(self):
        with tempfile.TemporaryDirectory() as folder:
            left, _, _ = self.record(folder, "json", save_name="card57-scratch", bare_checkpoint=True)
            right, _, _ = self.record(folder, "line")
            code, rows = self.compare(folder, left, right)
        self.assertEqual(code, 0, rows)

    def test_pick_failed_is_an_event_not_a_second_command_reply(self):
        with tempfile.TemporaryDirectory() as folder:
            left, _, _ = self.record(folder, "json", failure="RouteFailed")
            right, _, _ = self.record(folder, "line", failure="RouteFailed")
            code, rows = self.compare(folder, left, right)
            records = [json.loads(line) for line in right.read_text(encoding="utf-8").splitlines()]
        self.assertEqual(code, 0, rows)
        failed = [row for row in records if row.get("event_type") == "pick_failed"]
        self.assertEqual(len(failed), 1)
        self.assertEqual(failed[0]["fields"]["code"], "RouteFailed")

    def test_same_missing_reply_on_both_wires_still_fails(self):
        with tempfile.TemporaryDirectory() as folder:
            left, _, _ = self.record(folder, "json", drop=True)
            right, _, _ = self.record(folder, "line", drop=True)
            code, rows = self.compare(folder, left, right)
        self.assertEqual(code, 1)
        self.assertEqual(rows[-1]["silent_drops"], 2)

    def test_named_server_error_is_not_a_silent_drop(self):
        with tempfile.TemporaryDirectory() as folder:
            left, _, _ = self.record(folder, "json", server_error=True)
            right, _, _ = self.record(folder, "line", server_error=True)
            code, rows = self.compare(folder, left, right)
        self.assertEqual(code, 0, rows)
        self.assertEqual(rows[-1]["silent_drops"], 0)

    def test_late_reply_cannot_erase_a_recorded_deadline_failure(self):
        with tempfile.TemporaryDirectory() as folder:
            left, _, _ = self.record(folder, "json")
            right, record_code, _ = self.record(folder, "line", drop="late")
            code, rows = self.compare(folder, left, right)
        self.assertEqual(record_code, 1)
        self.assertEqual(code, 1, rows)
        self.assertEqual(rows[-1]["silent_drops"], 1)

    def test_no_dialog_is_explicit(self):
        with tempfile.TemporaryDirectory() as folder:
            left, _, _ = self.record(folder, "json", dialog=False)
            right, _, _ = self.record(folder, "line", dialog=False)
            code, rows = self.compare(folder, left, right)
            records = [json.loads(line) for line in left.read_text(encoding="utf-8").splitlines()]
        self.assertEqual(code, 0, rows)
        self.assertTrue(any(row.get("dialog") == "none" for row in records))

    def test_empty_or_truncated_recording_cannot_pass(self):
        with tempfile.TemporaryDirectory() as folder:
            left = Path(folder) / "json_run.jsonl"
            right = Path(folder) / "line_run.jsonl"
            left.write_text("", encoding="utf-8")
            right.write_text("", encoding="utf-8")
            code, rows = self.compare(folder, left, right)
        self.assertEqual(code, 1)
        self.assertGreater(rows[-1]["mismatches"], 0)

    def test_boot_save_name_is_refused_before_connect(self):
        with tempfile.TemporaryDirectory() as folder, contextlib.redirect_stdout(io.StringIO()):
            code = diff.main(["record", "--wire", "json", "--endpoint", "127.0.0.1:1",
                              "--save-name", "Option4BookChain", "--out", str(Path(folder) / "run.jsonl")])
        self.assertEqual(code, 1)


class RunWideComparisonTests(unittest.TestCase):
    """Small synthetic records with the live run's actual nullable/cadence shapes."""
    def recording(self, wire):
        nullable = wire == "line"
        snapshot = {
            "tick": 10 if nullable else 1, "worldName": "Test", "saveName": "Boot.sims3", "speed": "normal",
            "sims": [{"objectId": 101, "name": "Test Sim", "householdId": 100,
                      "position": {"x": 1, "y": 2, "z": 3}}],
            "objects": [{"objectId": 202, "scriptClass": "TestObject",
                         "position": {"x": 1, "y": 2, "z": 3}}],
        }
        if nullable:
            snapshot["sims"][0].update(currentInteractionId=None, owningPlayerId=None,
                                       currentInteractionKey=None, lotId=None, interactionQueueCount=None,
                                       postureContainerObjectId=None, postureType=None, simDescriptionId=None)
            snapshot["objects"][0]["lotId"] = None
        rows = [{"kind": "run", "schema": 1, "wire": wire, "steps": list(diff.STEPS)}]
        commands = {
            "join": ("join_session", {"playerId": 77, "displayName": "card57-wire-diff", "onScreen": None}),
            "snapshot": ("request_snapshot", {"playerId": 77}),
            "claim": ("claim_sim", {"playerId": 77, "simObjectId": 101}),
            "interact": ("queue_interaction", {"playerId": 77, "interaction": {
                "actorObjectId": 101, "interactionKey": "Terrain.GoHere", "targetObjectId": None,
                "targetPosition": {"x": 1, "y": 2, "z": 3}, "parameters": {}}}),
            "save": ("save_game", {"playerId": 77, "saveName": "card57-wire-scratch.sims3"}),
            "leave": ("leave_session", {"playerId": 77}),
            "rejoin": ("join_session", {"playerId": 77, "displayName": "card57-wire-diff", "onScreen": None}),
        }

        def event(step, kind, fields):
            rows.append({"kind": "event", "step": step, "event_type": kind,
                         "fields": {"stream_class": kind} if kind in ("snapshot_delta", "sim_animation")
                         else fields, "raw": {"$type": kind, **copy.deepcopy(fields)}})

        for step in diff.STEPS:
            if step in commands:
                verb, fields = commands[step]
                rows.extend([{"kind": "command", "step": step, "command_type": verb, "fields": fields},
                             {"kind": "reply", "step": step, "command_type": verb,
                              "accepted": True, "code": "", "fields": {"text": ""}}])
            else:
                rows.append({"kind": "observation", "step": step, "dialog": "none"})
            if step == "rejoin":
                event(step, "sim_released", {"playerId": 77, "simObjectId": 101})
                event(step, "sim_autonomy_changed", {"disabled": False, "simObjectId": 101})
            if step in ("join", "rejoin"):
                joined = {"playerId": 77, "displayName": "card57-wire-diff", "capabilities": ["presentation_stream"]}
                if nullable:
                    joined["notice"] = None
                event(step, "session_joined", joined)
                event(step, "game_speed_changed", {"speed": "normal"})
                event(step, "full_snapshot", {"stream_class": "full_snapshot"})
            if step in ("snapshot", "rejoin"):
                event(step, "lot_local_authority_mode_changed", {"mode": "fullPuppetFallback", "playerId": 77})
            if step == "claim":
                event(step, "sim_claimed", {"playerId": 77, "simObjectId": 101, "lotId": 303})
            if step == "interact":
                event(step, "sim_autonomy_changed", {"disabled": True, "simObjectId": 101})
                queued = {"actorObjectId": 101, "interactionId": "interaction:1",
                          "interactionKey": "Terrain.GoHere", "targetPosition": {"x": 1, "y": 2, "z": 3}}
                if nullable:
                    queued.update(targetObjectId=None, parameters=None)
                event(step, "interaction_queued", queued)
                event(step, "interaction_started", {"actorObjectId": 101, "interactionId": "interaction:1"})
            if step == "save":
                event(step, "save_checkpointed", {"saveName": "card57-wire-scratch.sims3"})
            # Different counts, step placement, ordering, minutes and snapshot contents.
            if step == ("dialog" if nullable else "claim"):
                event(step, "world_time_changed", {"time": {
                    "day": 3, "hours": 7, "minutes": 58 if nullable else 40,
                    "hours24": 7.9666667 if nullable else 7.6666665}})
                delta = copy.deepcopy(snapshot)
                if not nullable:
                    delta["objects"] = []
                for _ in range(5 if nullable else 2):
                    event(step, "sim_animation", {"lines": ["different opaque batch"]})
                    event(step, "snapshot_delta", {"snapshot": delta, "forceFull": False})
                event(step, "sim_motive_changed", {"simObjectId": 101, "motiveName": "Energy",
                                                 "value": 50 if nullable else 40})
        rows.append({"kind": "end", "complete": True, "errors": 0})
        return rows

    def compare(self, left, right):
        with tempfile.TemporaryDirectory() as folder, contextlib.redirect_stdout(io.StringIO()):
            paths = [Path(folder) / name for name in ("json_run.jsonl", "line_run.jsonl", "wire_diff.jsonl")]
            for path, rows in zip(paths, (left, right)):
                for index, row in enumerate(rows):
                    row["order"] = index
                path.write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")
            code = diff.main(["compare", str(paths[0]), str(paths[1]), "--out", str(paths[2])])
            result = [json.loads(line) for line in paths[2].read_text(encoding="utf-8").splitlines()]
        return code, result

    def test_live_shaped_timing_and_nullable_differences_match(self):
        code, rows = self.compare(self.recording("json"), self.recording("line"))
        self.assertEqual(code, 0, rows)
        self.assertEqual(rows[-1], {"summary": True, "steps": 9, "mismatches": 0, "silent_drops": 0})
        self.assertEqual(rows[-2]["step"], "broadcasts")

    def test_missing_join_speed_and_lot_mode_remain_strict_even_if_rejoin_has_them(self):
        for kind, step in (("game_speed_changed", "join"), ("lot_local_authority_mode_changed", "snapshot"),
                           ("sim_released", "rejoin"), ("sim_autonomy_changed", "rejoin")):
            with self.subTest(kind=kind, step=step):
                right = [row for row in self.recording("line")
                         if not (row.get("event_type") == kind and row.get("step") == step)]
                code, rows = self.compare(self.recording("json"), right)
                self.assertEqual(code, 1)
                failed = [row["step"] for row in rows[:-1] if row["status"] == "mismatch"]
                self.assertEqual(failed, [step], rows)

    def test_computed_hours24_omission_matches_the_v1_positional_wire(self):
        right = self.recording("line")
        clock = next(row for row in right if row.get("event_type") == "world_time_changed")
        del clock["fields"]["time"]["hours24"]
        del clock["raw"]["time"]["hours24"]
        code, rows = self.compare(self.recording("json"), right)
        self.assertEqual(code, 0, rows)
        self.assertEqual(rows[-2]["status"], "match")

    def test_missing_positional_clock_key_is_still_a_schema_mismatch(self):
        right = self.recording("line")
        clock = next(row for row in right if row.get("event_type") == "world_time_changed")
        del clock["fields"]["time"]["minutes"]
        del clock["raw"]["time"]["minutes"]
        code, rows = self.compare(self.recording("json"), right)
        self.assertEqual(code, 1)
        self.assertEqual([row["step"] for row in rows[:-1] if row["status"] == "mismatch"], ["broadcasts"], rows)
        self.assertIn("minutes", json.dumps(rows[-2]["diff"]))

    def test_missing_whole_cadence_class_and_field_schema_still_fail(self):
        for missing in ("class", "field"):
            with self.subTest(missing=missing):
                right = self.recording("line")
                if missing == "class":
                    right = [row for row in right if row.get("event_type") != "sim_animation"]
                else:
                    for row in right:
                        if row.get("event_type") == "sim_animation":
                            row["raw"] = {"$type": "sim_animation"}
                code, rows = self.compare(self.recording("json"), right)
                self.assertEqual(code, 1)
                self.assertEqual(rows[-2]["status"], "mismatch")

    def test_causal_event_order_and_values_remain_strict(self):
        for change in ("order", "value", "step"):
            with self.subTest(change=change):
                right = self.recording("line")
                queued = next(i for i, row in enumerate(right) if row.get("event_type") == "interaction_queued")
                if change == "order":
                    right[queued], right[queued + 1] = right[queued + 1], right[queued]
                elif change == "value":
                    right[queued]["fields"]["targetPosition"]["x"] = 999
                else:
                    right[queued]["step"] = "save"
                code, rows = self.compare(self.recording("json"), right)
                self.assertEqual(code, 1, rows)

    def test_only_codec_nullable_fields_equate_null_with_absent(self):
        for field, allowed in (("notice", True), ("displayName", False), ("extensionField", False),
                               ("hours24", False)):
            with self.subTest(field=field):
                left, right = self.recording("json"), self.recording("json")
                right[0]["wire"] = "line"
                a = next(row for row in left if row.get("event_type") == "session_joined")
                b = next(row for row in right if row.get("event_type") == "session_joined")
                a["fields"][field] = None
                b["fields"].pop(field, None)
                code, rows = self.compare(left, right)
                self.assertEqual(code, 0 if allowed else 1, rows)

    def test_nullable_notice_non_null_value_is_still_strict(self):
        right = self.recording("line")
        joined = next(row for row in right if row.get("event_type") == "session_joined")
        joined["fields"]["notice"] = "A real notice"
        code, rows = self.compare(self.recording("json"), right)
        self.assertEqual(code, 1)
        self.assertEqual([row["step"] for row in rows[:-1] if row["status"] == "mismatch"], ["join"], rows)

    def test_nested_nullable_command_field_equivalence(self):
        left, right = self.recording("json"), self.recording("line")
        command = next(row for row in left if row.get("command_type") == "queue_interaction"
                       and row["kind"] == "command")
        del command["fields"]["interaction"]["targetObjectId"]
        code, rows = self.compare(left, right)
        self.assertEqual(code, 0, rows)


if __name__ == "__main__":
    unittest.main()
