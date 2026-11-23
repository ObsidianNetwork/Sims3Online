"""Card 59 offline C# differential and controlled-bridge socket evidence.

Fixtures are synthetic. This does not run TS3, attach to a process, or seal live
gates. The C# folder operation explicitly adapts the sealed extensionless-folder
contract; the production controller's private helper only checks suffix folders.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import struct
import subprocess
import sys


ARTIFACTS = (
    "folder_select.txt", "chunk_diff.txt", "writer_safety.txt", "reuse_table.txt",
    "rotation.txt", "rejoin.jsonl", "lease_end.txt", "lot_mode.txt",
    "degradation.txt", "save_write_audit.txt", "scorecard.txt",
)
SCORE_KEYS = (
    "joins", "reuse_hits", "reuse_misses", "bytes_fetched", "bytes_oracle",
    "tick_drops_during_join", "presentation_sent_gap_max_during_join", "keeps",
    "keep_failures_that_failed_a_save", "resume_ok", "resume_fresh",
    "lot_modes_delivered", "lot_modes_required", "save_bytes_written_by_rust",
)


def kernel_fixture() -> bytes:
    """A synthetic one-entry DBPF satisfying the card-11 verifier."""
    payload = bytearray(140)
    payload[:4] = b"DBPF"
    for offset, value in ((4, 2), (0x24, 1), (0x2C, 36), (0x3C, 3), (0x40, 104)):
        struct.pack_into("<I", payload, offset, value)
    payload[96:100] = bytes((1, ord("S"), ord("3"), 0))
    struct.pack_into("<I", payload, 100, 4)
    for offset, value in ((108, 0x03E80CDC), (124, 96), (128, 0x80000008), (132, 8)):
        struct.pack_into("<I", payload, offset, value)
    return bytes(payload)


def fixture_cases(directory: Path) -> list[dict]:
    directory.mkdir(parents=True, exist_ok=True)
    fixtures = directory / "fixtures"
    cases = []

    def folder_case(name: str, candidates: list[tuple[int, str, int]], save="Fixture"):
        base = fixtures / name
        roots = [
            base / "configured",
            base / "profile/Documents/Electronic Arts/The Sims 3/Saves",
            base / "OneDrive/Documents/Electronic Arts/The Sims 3/Saves",
        ]
        for index, suffix, timestamp in candidates:
            folder = roots[index] / suffix
            folder.mkdir(parents=True, exist_ok=True)
            file = folder / "world.nhd"
            file.write_bytes(b"synthetic folder selection fixture")
            os.utime(file, ns=(timestamp * 1_000_000_000,) * 2)
        cases.append({
            "id": name, "op": "card59_folder_select",
            "roots": [root.as_posix() for root in roots], "save": save,
        })

    folder_case("suffix_input", [(0, "Fixture.sims3", 100)], "Fixture.sims3")
    folder_case("bare_input", [(0, "Fixture.sims3", 100)])
    folder_case("newer_extensionless", [(0, "Fixture.sims3", 100), (0, "Fixture", 200)])
    folder_case("newer_suffix", [(0, "Fixture.sims3", 200), (0, "Fixture", 100)])
    folder_case("configured_wins_tie", [(0, "Fixture.sims3", 100), (1, "Fixture.sims3", 100), (2, "Fixture.sims3", 100)])
    folder_case("redirected_documents", [(1, "Fixture.sims3", 100), (2, "Fixture.sims3", 200)])
    folder_case("absent", [])
    transfer = fixtures / "transfer"
    transfer.mkdir(parents=True, exist_ok=True)
    for name, data in (
        ("empty", b""), ("Z.data", bytes(range(256)) * 1024),
        ("_.data", b"_"), ("world.nhd", bytes(range(256)) * 1024 + b"x"),
    ):
        (transfer / name).write_bytes(data)
    cases.append({"id": "chunk_boundaries", "op": "card59_chunks", "folder": transfer.as_posix(), "save": "Transfer"})
    writer = fixtures / "client"
    (writer / "Player.sims3").mkdir(parents=True, exist_ok=True)
    (writer / "Player.sims3/own.nhd").write_bytes(b"the player's own save")
    cases.append({"id": "marker_protection", "op": "card59_writer_safety", "root": writer.as_posix()})
    fingerprint = "scope=h:18;objects=1:o;inventories=1:i;relationships=1:r;ms=1"
    for name, saved, now, error, whole in (
        ("hit", fingerprint, fingerprint.replace("ms=1", "ms=9"), None, True),
        ("restart", None, fingerprint, None, True),
        ("objects", fingerprint, fingerprint.replace("1:o", "2:q"), None, True),
        ("inventories", fingerprint, fingerprint.replace("1:i", "1:j"), None, True),
        ("relationships", fingerprint, fingerprint.replace("1:r", "2:s"), None, True),
        ("scope", fingerprint, fingerprint.replace("h:18", "h:19"), None, True),
        ("empty_scope", fingerprint, fingerprint.replace("h:18", "-"), None, True),
        ("scan_error", fingerprint, None, "scan unavailable", True),
        ("partial", fingerprint, fingerprint, None, False),
        ("no_reply", fingerprint, None, None, True),
    ):
        cases.append({"id": f"reuse_{name}", "op": "card59_reuse_decide", "saved": saved, "now": now,
                      "error": error, "whole": whole})
    whole = fixtures / "server/Fixture.sims3"
    whole.mkdir(parents=True, exist_ok=True)
    (whole / "world.nhd").write_bytes(kernel_fixture())
    (whole / "Meta.data").write_bytes(b"synthetic metadata")
    cases.append({"id": "whole_chunks", "op": "card59_chunks", "folder": whole.as_posix(), "save": "Fixture"})
    cases.append({"id": "last_good", "op": "card59_rotation", "folder": whole.as_posix(),
                  "scratch": (directory / "rotation-probe").as_posix()})
    cases.append({"id": "join_modes", "op": "card59_lot_mode", "players": [7, 8, 9]})
    cases.append({"id": "rejoin_state", "op": "card59_rejoin_state", "presentation": "seq=2;more=0\n2|St|55|fixture"})
    return cases


def parse_oracle(output: str, expected_ids: list[str]) -> list[dict]:
    rows = [json.loads(line.removeprefix("CARD59 ")) for line in output.splitlines() if line.startswith("CARD59 ")]
    if [row.get("id") for row in rows] != expected_ids:
        raise ValueError("C# oracle rows missing, duplicated, reordered, or unexpected")
    return rows


def validate_scorecard(text: str) -> dict[str, int]:
    fields = {}
    for field in text.strip().split("|"):
        key, value = field.split("=", 1)
        if key in fields:
            raise ValueError(f"duplicate scorecard field: {key}")
        fields[key] = value
    try:
        metrics = {key: int(fields[key]) for key in SCORE_KEYS}
    except (KeyError, ValueError) as error:
        raise ValueError("missing or nonnumeric Card 59 metric") from error
    if any(value < 0 for value in metrics.values()):
        raise ValueError("negative Card 59 metric")
    if (
        metrics["bytes_fetched"] != metrics["bytes_oracle"]
        or metrics["tick_drops_during_join"] != 0
        or metrics["presentation_sent_gap_max_during_join"] != 1
        or metrics["keep_failures_that_failed_a_save"] != 0
        or metrics["lot_modes_delivered"] != metrics["lot_modes_required"]
        or metrics["save_bytes_written_by_rust"] != 0
        or metrics["reuse_hits"] == 0
        or metrics["reuse_misses"] == 0
        or metrics["joins"] < 2
        or metrics["bytes_fetched"] == 0
        or metrics["resume_ok"] == 0
        or metrics["resume_fresh"] == 0
    ):
        raise ValueError("Card 59 scorecard invariant failed")
    return metrics


def run(command: list[str], *, cwd: Path, input_text: str | None = None) -> subprocess.CompletedProcess:
    result = subprocess.run(command, cwd=cwd, input=input_text, encoding="utf-8", errors="replace",
                            capture_output=True, timeout=240, check=False)
    if result.returncode:
        raise RuntimeError(f"{command[0]} exited {result.returncode}\n{result.stdout[-3000:]}\n{result.stderr[-5000:]}")
    return result


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifact-dir", required=True, type=Path)
    parser.add_argument("--rust-binary", required=True, type=Path)
    args = parser.parse_args(argv)
    root = Path(__file__).resolve().parents[1]
    directory = args.artifact_dir.resolve()
    binary = args.rust_binary.resolve()
    directory.mkdir(parents=True, exist_ok=True)
    for name in (*ARTIFACTS, "mismatch.json"):
        (directory / name).unlink(missing_ok=True)
    cases = fixture_cases(directory)
    inputs = "".join(json.dumps(row) + "\n" for row in cases)
    (directory / "cases.jsonl").write_text(inputs, encoding="utf-8")
    project = root / "rust/simlink_headless_server/tests/oracle/Card58Oracle.csproj"
    build = run(["dotnet", "build", str(project), "--nologo", "-v:q"], cwd=root)
    (directory / "oracle-build.txt").write_text(build.stdout + build.stderr, encoding="utf-8")
    oracle = run(["dotnet", str(project.parent / "bin/Debug/net8.0/Card58Oracle.dll")],
                 cwd=root, input_text=inputs)
    rows = parse_oracle(oracle.stdout, [row["id"] for row in cases])
    (directory / "oracle.jsonl").write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")
    result = run([str(binary), "--card59-scorecard-dir", str(directory)], cwd=root)
    (directory / "rust-probe-log.txt").write_text(result.stdout + result.stderr, encoding="utf-8")
    if "serve_report|checkpoint_failure|" not in result.stderr or "process_exited" not in result.stderr:
        raise ValueError("missing named mid-checkpoint process-exit serve report")
    for name in ARTIFACTS:
        if not (directory / name).is_file() or not (directory / name).stat().st_size:
            raise ValueError(f"missing Card 59 artifact: {name}")
    metrics = validate_scorecard((directory / "scorecard.txt").read_text(encoding="utf-8"))
    score = (directory / "scorecard.txt").read_text(encoding="utf-8")
    if "resume_identity=opaque_token" not in score or "warm_wire_resume=socket_verified" not in score:
        raise ValueError("missing token-authenticated socket resume evidence")
    rejoin_rows = [json.loads(line) for line in (directory / "rejoin.jsonl").read_text(encoding="utf-8").splitlines()]
    wire = {row["case"]: row for row in rejoin_rows if row.get("scope") == "socket"}
    required = {"warm_player_a", "same_cursor_player_b", "active_token", "missing_token",
                "unknown_token", "second_reconnect_active", "bulk_gap", "expired", "stale_epoch"}
    if not required.issubset(wire) or not all(row.get("claims_correct") for row in wire.values()):
        raise ValueError("incomplete socket token isolation or claim-retirement evidence")
    if sum(row.get("outcome") == "ok" for row in wire.values()) != metrics["resume_ok"]:
        raise ValueError("resume_ok must count socket resumes, not ring-model transitions")
    with (directory / "folder_select.txt").open("a", encoding="utf-8") as artifact:
        artifact.write("oracle_scope=sealed-extensionless-candidate-adapter; production-controller-helper=suffix-only\n")
    print(f"PASS Card59 independent offline: {len(rows)} C# differential cases, {metrics['resume_ok']} socket replays, "
          f"{metrics['resume_fresh']} fresh; opaque token isolation verified; live gates not run")
    print(directory / "scorecard.txt")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, RuntimeError, subprocess.TimeoutExpired) as error:
        print(f"FAIL Card59 offline: {error}", file=sys.stderr)
        raise SystemExit(1) from error
