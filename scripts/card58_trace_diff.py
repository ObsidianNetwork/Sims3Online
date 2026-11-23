"""Replay identical recorded inputs through the C# controller oracle and Rust relay.

Optional <record>.bulk.jsonl entries contain {"line": "BULK_REF|...", "body":
"<base64 DMA bytes>"} or {"corr": "...", "part_index": 0, "body": "<base64>"}.
A <record>.bulk.json map may instead key base64 bodies by full BULK_REF or corr
(corr keys are only for unpartitioned payloads). Without bodies, compare metadata
and report unavailable payloads, never synthetic snapshots.
"""

import argparse
import base64
from collections import Counter
from datetime import datetime
import json
from pathlib import Path
import re
import struct
import subprocess
import zlib

ROOT = Path(__file__).resolve().parents[1]


def compare(expected, actual):
    """Compare ordered semantic frames, retaining recipients and per-client sequence."""
    expected_frames = [row for row in expected if "connection" in row]
    actual_frames = [row for row in actual if "connection" in row]
    key = lambda row: json.dumps(row, sort_keys=True, separators=(",", ":"))
    missing = Counter(map(key, expected_frames)) - Counter(map(key, actual_frames))
    extra = Counter(map(key, actual_frames)) - Counter(map(key, expected_frames))
    last = {}
    regressions = 0
    for row in actual_frames:
        connection = row["connection"]
        seq = row["seq"]
        regressions += int(seq <= last.get(connection, 0))
        last[connection] = seq
    def streams(rows):
        grouped = {}
        for row in rows:
            if "connection" in row:
                grouped.setdefault(row["connection"], []).append(row)
        return grouped, [row for row in rows if "connection" not in row]

    return {
        "frames_in": len(expected_frames),
        "frames_relayed": len(actual_frames),
        "frames_dropped": sum(missing.values()),
        "seq_regressions": regressions,
        "leak_events": sum(extra.values()),
        "equal": streams(expected) == streams(actual),
    }


def load_bulk_payloads(record):
    """Read companion DMA dumps; slot numbers alone cannot identify reused slots."""
    payloads = {}
    for path in (Path(str(record) + ".bulk.jsonl"), Path(str(record) + ".bulk.json")):
        if not path.is_file():
            continue
        if path.suffix == ".json":
            entries = [{"line": key, "body": value}
                       for key, value in json.loads(path.read_text(encoding="utf-8")).items()]
        else:
            entries = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]
        for entry in entries:
            key = entry.get("line")
            if key is None:
                key = (entry["corr"], entry["part_index"]) if "part_index" in entry else entry["corr"]
            body = base64.b64decode(entry["body"], validate=True)
            if key in payloads and payloads[key] != body:
                raise ValueError(f"conflicting companion bulk payload: {key}")
            payloads[key] = body
    return payloads


def bulk_reference(line):
    fields = line.split("|")
    corr, kind, slot = fields[1:4]
    if kind == "snapshot-part":
        index, count, length = map(int, fields[4:7])
        crc = int(fields[7], 16)
        if not 0 <= index < count:
            raise ValueError("invalid snapshot-part index/count")
    else:
        index, count, length, crc = 0, 1, int(fields[4]), int(fields[5], 16)
    return corr, kind, int(slot), index, count, length, crc


def recorded_rows(lines, bulk_payloads=None):
    """Materialize complete DMA snapshots at their final part's arrival, or keep metadata."""
    bulk_payloads = bulk_payloads or {}
    result = []
    first = None
    pattern = re.compile(r"\[bridge ([^]]+)] (ctl->host|host->ctl) seq=\d+(?: slot=\d+)? (.*)")
    records = []
    for line in lines:
        match = pattern.fullmatch(line.rstrip("\n"))
        if not match:
            raise ValueError(f"unrecognized bridge record: {line[:100]}")
        records.append(match.groups())
    # Pre-index parts so an incomplete capture never masquerades as a snapshot.
    # Keep every metadata row when any member of its group is unavailable.
    groups = {}
    for _, direction, payload in records:
        if direction != "host->ctl" or not payload.startswith("BULK_REF|"):
            continue
        corr, kind, _, index, count, length, crc = bulk_reference(payload)
        body = bulk_payloads.get(payload, bulk_payloads.get((corr, index)))
        if body is None and kind != "snapshot-part":
            body = bulk_payloads.get(corr)
        if body is not None:
            if zlib.crc32(body) != crc:
                raise ValueError(f"bulk CRC mismatch: {payload}")
            if kind != "snapshot-part" and len(body) != length:
                raise ValueError(f"bulk length mismatch: {payload}")
        group = groups.setdefault((corr, kind), {"count": count, "length": length, "parts": {}, "seen": set()})
        if (group["count"], group["length"]) != (count, length):
            raise ValueError(f"inconsistent bulk parts: {corr}")
        if index in group["parts"] and group["parts"][index] != body:
            raise ValueError(f"conflicting bulk part: {corr}/{index}")
        group["parts"][index] = body
    forced = {}
    for timestamp, direction, payload in records:
        moment = datetime.strptime(timestamp, "%Y-%m-%d %H:%M:%S.%fZ")
        first = first or moment
        now = int((moment - first).total_seconds() * 1000)
        if direction == "ctl->host":
            result.append({"op": "bridge_command", "line": payload, "now": now})
            fields = payload.split("|")
            if fields[0] == "REQUEST_SNAPSHOT":
                forced[fields[1]] = len(fields) > 2 and int(fields[2]) < 0
        elif payload.startswith("BULK_REF|"):
            corr, kind, _, index, count, _, _ = bulk_reference(payload)
            group = groups[(corr, kind)]
            parts = group["parts"]
            if len(parts) != count or any(body is None for body in parts.values()):
                result.append({"op": "bulk_metadata", "line": payload, "now": now, "payload_missing": True})
                continue
            already_complete = len(group["seen"]) == count
            group["seen"].add(index)
            if len(group["seen"]) != count or already_complete:
                continue
            body = b"".join(parts[i] for i in range(count))
            if len(body) != group["length"]:
                raise ValueError(f"assembled bulk length mismatch: {corr}")
            if body[:4] == b"SSP2":
                if len(body) < 16:
                    raise ValueError(f"truncated SNAPSHOT2 header: {corr}")
                result.append({"op": "snapshot", "body": base64.b64encode(body).decode(),
                               "tick": struct.unpack_from("<q", body, 8)[0],
                               "force_full": forced.get(corr, False), "now": now})
            else:
                result.append({"op": "bridge_reply", "line": body.decode("utf-8-sig"), "now": now})
        elif payload.startswith("EVENT|"):
            result.append({"op": "event", "line": payload, "now": now})
        elif payload.startswith("DIAG_STATE_REPORT|"):
            result.append({"op": "diag", "report": bytes.fromhex(payload.split("|")[3]).decode("utf-8"), "now": now})
        else:
            result.append({"op": "bridge_reply", "line": payload, "now": now})
    return result


def snapshot_bytes(tick, version=2, queue=1):
    """Authored fixture, not a payload copied from a game."""
    text = lambda value: struct.pack("<I", len(value.encode())) + value.encode()
    data = struct.pack("<IIq", 0x32505353, version, tick)
    data += text("Fixture World") + text("Fixture Save") + struct.pack("<II", 1, 1)
    data += struct.pack("<Q", 0xFFFFFFFFFFFFFFFE) + text("Fixture Sim")
    data += struct.pack("<QfffQ", 18, 1.25, -2.0, 3.0, 99) + text("Fixture Interaction")
    data += struct.pack("<IQIQ", 10, 44, queue, 55) + text("Fixture Posture")
    if version == 2:
        data += struct.pack("<Q", 66)
    data += struct.pack("<I", 0)
    return data


def fixture_rows():
    hx = lambda value: value.encode().hex()
    rows = [
        {"op": "join", "connection": 1, "player": 10},
        {"op": "join", "connection": 2, "player": 20},
        {"op": "admin", "connection": 3},
        {"op": "claim", "player": 10, "sim": 55},
    ]
    for tick in [1, 2, 3]:
        rows += [
            {"op": "snapshot", "body": base64.b64encode(snapshot_bytes(tick, 1 + tick % 2, tick)).decode(), "tick": tick, "force_full": tick == 3},
            {"op": "presentation", "report": f"seq={tick};more=0\n{tick}|opaque|payload\n", "tick": tick},
            {"op": "event", "line": f"EVENT|DialogRequest|{tick}|55|{hx('TwoButton')}|3|{hx('Question')}|{hx('Yes')}|{hx('No')}", "tick": tick},
            {"op": "event", "line": f"EVENT|DialogRequest|1000000000|55|{hx('Sting')}|2|{hx('sound')}|{hx('55')}", "tick": tick},
        ]
    rows += [
        {"op": "event", "line": "EVENT|SpeedIntentCaptured|Double", "tick": 4},
        {"op": "event", "line": f"EVENT|PickFailed|00000000000000000000000000000001|55|9|{hx('RouteFailed | tooltip: no route')}", "requester": 2, "tick": 4},
        {"op": "routed", "kind": "SimDescriptionData", "fields": ["66", "[1,2]", "[3]", "[]"], "requester": 2, "tick": 4},
        {"op": "routed", "kind": "WorldFileChunk", "fields": [hx("World"), "0", "1", hx("file"), "0", "2", "[1,2]"], "requester": 1, "tick": 4},
        {"op": "diag", "report": "clockDay=0;clockHours=12;clockMinutes=0", "now": 0},
        {"op": "diag", "report": "clockDay=0;clockHours=12;clockMinutes=2", "now": 29999},
        {"op": "diag", "report": "clockDay=0;clockHours=12;clockMinutes=2", "now": 30000},
        {"op": "history", "connection": 2, "drivers": "drivers=1,2",
         "pages": [{"report": f"lines=1;next={i + 1}\n0|opaque|{i}"} for i in range(16)]
                  + [{"report": "lines=1;next=-1\n0|opaque|end"}]},
        {"op": "presentation", "report": "seq=999;more=0\n" + "\n".join(f"{i}|opaque|line" for i in range(401)), "tick": 5},
    ]
    for report in ["line1\nline2", "quoted \" & < > + ' ` \u00e9 \U0001f642", ""]:
        rows.append({"op": "admin_reply", "line": f"DIAG_STATE_REPORT|00000000000000000000000000000001|ok|{hx(report)}"})
    rows.append({"op": "admin_reply", "line": f"ERROR|00000000000000000000000000000001|{hx('rejected')}"})
    for warn, hard, maximum in [(1500, 6000, 10000), (20, 60, 800)]:
        for lag, valid, flight in [(hard + 1, False, False), (warn, True, False), ((warn + hard) // 2, True, False),
                                   (hard, True, False), (hard + 1, True, False), (hard + 1, True, True)]:
            rows.append({"op": "cadence", "tick": lag + 100, "ack": 100 if valid else None,
                         "in_flight": flight, "base": 200, "warn": warn, "hard": hard, "maximum": maximum})
    return rows


def execute(command, rows, prefix=""):
    result = subprocess.run(command, input="".join(json.dumps(row) + "\n" for row in rows),
                            text=True, encoding="utf-8", capture_output=True, cwd=ROOT, check=False)
    if result.returncode:
        raise RuntimeError(f"oracle process failed ({result.returncode}):\n{result.stdout}\n{result.stderr}")
    return [json.loads(line[len(prefix):]) for line in result.stdout.splitlines() if line.startswith(prefix)]


def pump_fixture_rows():
    """Exercise the actual presentation pump, not just its parsing helpers."""
    cursor = str((2**63 - 1) // 2)
    report = lambda args, text: {"args": args, "report": text}
    history = lambda sim: [
        report(["snapshot", str(sim)], f"drivers={sim}"),
        report(["snapshot-driver", str(sim), "0"], f"lines=1;next=-1\n0|history|{sim}"),
    ]
    # The live lines are play requests on the fresh Sim's own driver: the one kind of live line both servers must
    # deliver after that Sim's history. (The Rust server asks for the live lines while the history is still being
    # read and lets lines that are not the fresh Sim's driver through ahead of it, so an untyped live line is not
    # ordered the same way by the two.)
    return [
        {"op": "join", "connection": 1, "player": 10}, {"op": "admin", "connection": 4},
        {"op": "pump", "now": 0, "recorder": [report(["ring-on"], "enabled=1"),
         report(["dump", cursor], "seq=10;more=0"), report(["dump", cursor], "seq=11;more=0")]},
        {"op": "claim", "player": 10, "sim": 55},
        {"op": "pump", "now": 250, "recorder": [*history(55), report(["dump", "11", "55"], "seq=12;more=0\n0|P|55|live")]},
        {"op": "join", "connection": 2, "player": 20},
        {"op": "claim", "player": 10, "sim": 66},
        {"op": "pump", "now": 500, "recorder": [*history(66), *history(55),
         report(["dump", "12", "55,66"], "seq=13;more=0\n0|P|66|live")]},
        {"op": "release", "player": 10, "sim": 55}, {"op": "release", "player": 10, "sim": 66},
        {"op": "pump", "now": 750, "recorder": [report(["dump", cursor], "seq=20;more=0")]},
        {"op": "join", "connection": 3, "player": 30},
        {"op": "pump", "now": 1000, "recorder": [report(["dump", cursor], "seq=21;more=0")]},
        {"op": "claim", "player": 20, "sim": 55},
        {"op": "pump", "now": 1250, "recorder": [*history(55), report(["dump", "21", "55"], "seq=22;more=0")]},
    ]


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rust-binary", required=True, type=Path)
    parser.add_argument("--artifact-dir", required=True, type=Path)
    parser.add_argument("--record", action="append", type=Path)
    parser.add_argument("--require-bulk-bodies", action="store_true")
    args = parser.parse_args(argv)
    args.artifact_dir.mkdir(parents=True, exist_ok=True)
    oracle = ROOT / "rust/simlink_headless_server/tests/oracle/Card58Oracle.csproj"
    traces = [("authored-multitick", fixture_rows()), ("presentation-pump", pump_fixture_rows())]
    records = args.record
    if records is None:
        capture = ROOT.parent / "SimsOnline/.omo/run/card56-20261101-173207-gate6/csharp_bridge.record"
        records = [capture] if capture.is_file() else []
    for record in records:
        traces.append((record.name, [
            {"op": "join", "connection": 1, "player": 10}, {"op": "join", "connection": 2, "player": 20},
            {"op": "admin", "connection": 3},
            *recorded_rows(record.read_text(encoding="utf-8").splitlines(), load_bulk_payloads(record)),
        ]))
    evidence = []
    all_expected, all_actual = [], []
    unavailable = 0
    for name, rows in traces:
        expected = execute(["dotnet", "run", "--project", str(oracle), "--configuration", "Release",
                            "--no-launch-profile"], rows, "CARD58 ")
        actual = execute([str(args.rust_binary), "--trace-stdio"], rows)
        result = compare(expected, actual)
        missing = sum(row.get("payload_missing", False) for row in rows)
        unavailable += missing
        evidence.append({"trace": name, "inputs": len(rows), "unavailable_bulk_parts": missing, **result})
        all_expected += expected
        all_actual += actual
        if not result["equal"]:
            (args.artifact_dir / f"{name}.oracle.jsonl").write_text(
                "".join(json.dumps(row) + "\n" for row in expected), encoding="utf-8")
            (args.artifact_dir / f"{name}.rust.jsonl").write_text(
                "".join(json.dumps(row) + "\n" for row in actual), encoding="utf-8")
    # Each trace starts a fresh epoch; seq is checked within each trace, never across epochs.
    summary = {"summary": True, "frames_in": sum(row["frames_in"] for row in evidence),
               "frames_relayed": sum(row["frames_relayed"] for row in evidence),
               "frames_dropped": sum(row["frames_dropped"] for row in evidence),
               "seq_regressions": sum(row["seq_regressions"] for row in evidence),
               "leak_events": sum(row["leak_events"] for row in evidence),
               "equal": all(row["equal"] for row in evidence), "unavailable_bulk_parts": unavailable,
               "history_trimmed": sum(row.get("history_trimmed", 0) for row in all_actual),
               "history_trimmed_oracle": sum(row.get("history_trimmed", 0) for row in all_expected),
               "real_captures": len(records),
               "full_recorded_snapshot_parity": unavailable == 0 and bool(records) and all(row["equal"] for row in evidence)}
    (args.artifact_dir / "trace_diff.jsonl").write_text(
        "".join(json.dumps(row, separators=(",", ":")) + "\n" for row in [*evidence, summary]), encoding="utf-8")
    print(json.dumps(summary))
    (args.artifact_dir / "history_diff.txt").write_text(
        f"history_trimmed={summary['history_trimmed']}|history_trimmed_oracle={summary['history_trimmed_oracle']}|"
        f"oracle=AuthoritativeSession.PresentationHistoryAsync|equal={summary['equal']}\n", encoding="utf-8")
    if summary["equal"]:
        result = subprocess.run([str(args.rust_binary), "--card58-scorecard-dir", str(args.artifact_dir)], cwd=ROOT, check=False)
        if result.returncode:
            return result.returncode
    return int(not summary["equal"] or (args.require_bulk_bodies and unavailable > 0))


if __name__ == "__main__":
    raise SystemExit(main())
