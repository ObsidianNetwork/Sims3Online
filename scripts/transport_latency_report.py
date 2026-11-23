#!/usr/bin/env python3
"""Summarize opt-in SimsOnline transport timing logs.

Does not change the server/client process, game state, or bridge protocol.
Run on logs collected with SIMSONLINE_TRANSPORT_TRACE=1.

Usage:
  python scripts/transport_latency_report.py server.log client.log
  python scripts/transport_latency_report.py --json logs/*.log
  python scripts/transport_latency_report.py --self-test

The current traces measure per-stage round trips only: they cannot yield
end-to-end event latency without a source timestamp and event identity.
"""
from __future__ import annotations

import argparse
import glob
import json
import math
import re
import sys
from collections import defaultdict
from pathlib import Path
from typing import Any, Iterable

PREFIX = "[transport-latency]"
PAIRS = re.compile(r"([A-Za-z_][A-Za-z_0-9]*)=([^\s]+)")
TIMING_FIELDS = ("elapsed_ms", "age_ms", "queue_ms", "source_to_apply_ms")
COUNT_FIELDS = ("lines", "bytes", "chars", "targets", "page", "more")
STAMP_FIELDS = ("at_ms", "at_qpc", "qpc_freq", "start_qpc", "end_qpc", "at_utc_ticks", "first_seq", "last_seq", "source_seq", "source_utc_ticks", "apply_utc_ticks")
EXPECTED_FOUR_STAGE = (
    "source_event",
    "server_bridge_dump",
    "client_network_receive",
    "client_game_apply",
)


def parse_line(line: str) -> dict[str, Any] | None:
    """Read a trace embedded in ordinary stdout/logging; reject malformed samples."""
    if PREFIX not in line:
        return None
    fields = dict(PAIRS.findall(line.split(PREFIX, 1)[1]))
    if not fields.get("stage"):
        return None
    for name in TIMING_FIELDS:
        if name in fields:
            try:
                value = float(fields[name])
                if not math.isfinite(value) or value < 0:
                    del fields[name]
                else:
                    fields[name] = value
            except ValueError:
                del fields[name]
    for name in COUNT_FIELDS + STAMP_FIELDS:
        if name in fields:
            try:
                fields[name] = int(fields[name])
            except ValueError:
                del fields[name]
    return fields


def percentile(sorted_numbers: list[float], percentile_value: float) -> float | None:
    """Linear-interpolated percentile (type 7), including p99 for short runs."""
    if not sorted_numbers:
        return None
    pos = (len(sorted_numbers) - 1) * percentile_value / 100.0
    lo, hi = math.floor(pos), math.ceil(pos)
    return sorted_numbers[lo] + (sorted_numbers[hi] - sorted_numbers[lo]) * (pos - lo)


def distribution(values: Iterable[float]) -> dict[str, float] | None:
    vals = sorted(float(v) for v in values if math.isfinite(float(v)))
    if not vals:
        return None
    return {
        "min": round(vals[0], 3),
        "mean": round(sum(vals) / len(vals), 3),
        "p50": round(percentile(vals, 50), 3),
        "p95": round(percentile(vals, 95), 3),
        "p99": round(percentile(vals, 99), 3),
        "max": round(vals[-1], 3),
    }


def dispatch_to_receive(rows: list[dict[str, Any]]) -> dict[str, Any]:
    """Match exact recorder sequence bounds from dispatch to socket receipt.

    Prefer high-resolution QPC timestamps (same host, equal frequencies).
    The low-resolution TickCount64 fallback is retained for historical logs.
    Matching a batch's first/last recorder event is NOT a complete event
    source-to-visual-application measurement.
    """
    dispatches: dict[tuple[int, int], list[dict[str, Any]]] = defaultdict(list)
    receivers: list[tuple[tuple[int, int], dict[str, Any]]] = []
    for row in rows:
        try:
            pair = (int(row["first_seq"]), int(row["last_seq"]))
            at = int(row["at_ms"])
        except (KeyError, TypeError, ValueError):
            continue
        if pair[0] <= 0 or pair[1] < pair[0] or at < 0:
            continue
        if row["stage"] == "server_presentation_dispatch":
            dispatches[pair].append(row)
        elif row["stage"] == "client_network_receive":
            receivers.append((pair, row))
    for values in dispatches.values():
        values.sort(key=lambda row: int(row["at_ms"]))
    latencies: list[float] = []
    unmatched = 0
    high_res_samples = 0
    for pair, receiver in receivers:
        values = dispatches.get(pair, [])
        selected = None
        rx_freq = receiver.get("qpc_freq")
        rx_qpc = receiver.get("at_qpc")
        if isinstance(rx_qpc, int) and isinstance(rx_freq, int) and rx_freq > 0:
            high_res = [
                d for d in values
                if isinstance(d.get("at_qpc"), int)
                and d.get("qpc_freq") == rx_freq
                and int(d["at_qpc"]) <= rx_qpc
            ]
            if high_res:
                selected = max(high_res, key=lambda row: int(row["at_qpc"]))
                delta_ms = 1000.0 * (rx_qpc - int(selected["at_qpc"])) / rx_freq
                high_res_samples += 1
        if selected is None:
            valid = [d for d in values if int(d["at_ms"]) <= int(receiver["at_ms"])]
            if valid:
                selected = valid[-1]
                delta_ms = float(int(receiver["at_ms"]) - int(selected["at_ms"]))
        if selected is None:
            unmatched += 1
        else:
            latencies.append(delta_ms)
    return {
        "samples": len(latencies),
        "high_res_samples": high_res_samples,
        "low_res_samples": len(latencies) - high_res_samples,
        "unmatched_receives": unmatched,
        "dispatch_to_receive_ms": distribution(latencies),
        "definition": "server pre-enqueue timestamp to client socket receipt, same-host only",
    }

def source_to_client_apply(rows: list[dict[str, Any]], source_stage: str = "source_event") -> dict[str, Any]:
    """Correlate sampled source events to the end of SACS replay in the client game.

    The embedded Mono wall clock was measured at ~1ms precision. This is
    an in-game managed apply result, NOT a rendered-animation timestamp.
    """
    sources: dict[int, int] = {}
    applies: list[tuple[int, int, int]] = []
    for row in rows:
        if row["stage"] == source_stage:
            # Pre-origin-tagging traces used source_event for *any* ring row,
            # including records created during polling. Never silently count
            # those old samples as genuine ISacsProxy callback timestamps.
            if source_stage == "source_event" and row.get("source_origin") != "sacs_proxy":
                continue
            seq, stamp = row.get("source_seq"), row.get("source_utc_ticks")
            if isinstance(seq, int) and isinstance(stamp, int) and seq > 0 and stamp > 0:
                sources.setdefault(seq, stamp)
        elif row["stage"] == "client_game_apply":
            first, last, stamp = row.get("first_seq"), row.get("last_seq"), row.get("apply_utc_ticks")
            if all(isinstance(v, int) for v in (first, last, stamp)) and first > 0 and last >= first and stamp > 0:
                applies.append((first, last, stamp))
    latencies: list[float] = []
    for seq, source_stamp in sources.items():
        candidates = [
            (stamp - source_stamp) / 10000.0
            for first, last, stamp in applies
            if first <= seq <= last and 0 <= stamp - source_stamp <= 120 * 10000000
        ]
        if candidates:
            latencies.append(min(candidates))
    return {
        "source_samples": len(sources),
        "matched_samples": len(latencies),
        "unmatched_samples": len(sources) - len(latencies),
        "source_to_apply_ms": distribution(latencies),
    }


def summarize(rows: list[dict[str, Any]], files: list[str], same_host: bool = False) -> dict[str, Any]:
    grouped: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for row in rows:
        grouped[str(row["stage"])].append(row)
    stages: dict[str, dict[str, Any]] = {}
    for stage, items in sorted(grouped.items()):
        entry: dict[str, Any] = {"samples": len(items)}
        for field in TIMING_FIELDS:
            sampled = [r[field] for r in items if isinstance(r.get(field), (int, float))]
            if sampled:
                entry[field] = distribution(sampled)
        for field in COUNT_FIELDS:
            sampled = [r[field] for r in items if isinstance(r.get(field), int)]
            if sampled:
                entry[f"total_{field}"] = sum(sampled)
                entry[f"max_{field}"] = max(sampled)
        if stage == "server_bridge_dump":
            entry["pages_with_more"] = sum(r.get("more") == 1 for r in items)
            # from_seq/to_seq are ring cursors; gaps in a *filtered* stream
            # do NOT necessarily mean dropped events.
            seq_deltas = []
            for r in items:
                try:
                    delta = int(r["to_seq"]) - int(r["from_seq"])
                    if delta >= 0:
                        seq_deltas.append(delta)
                except (KeyError, ValueError, TypeError):
                    pass
            if seq_deltas:
                entry["ring_sequences_examined"] = sum(seq_deltas)
        stages[stage] = entry

    seen = set(stages)
    missing = [name for name in EXPECTED_FOUR_STAGE if name not in seen]
    source_apply = source_to_client_apply(rows) if same_host else None
    sampled_record_apply = source_to_client_apply(rows, "source_record") if same_host else None
    delivery = dispatch_to_receive(rows) if same_host else None
    has_event_identity = bool(source_apply and source_apply["matched_samples"] > 0)
    four_stages_ready = bool(not missing and has_event_identity and delivery and delivery["high_res_samples"] > 0)
    return {
        "schema": "simsonline.transport-latency-report.v1",
        "same_host": same_host,
        "network_delivery_hop": delivery,
        "source_to_client_apply": source_apply,
        "record_to_client_apply": sampled_record_apply,
        "inputs": files,
        "trace_records": len(rows),
        "stages": stages,
        "four_timestamp_ready": four_stages_ready,
        "missing_four_timestamp_stages": missing,
        "event_identity_available": has_event_identity,
        "warning": (
            "Four correlated stages have not yet been observed; never infer "
            "source-to-client latency from unrelated stage samples."
        ) if not four_stages_ready else (
            "Source-to-managed-game-apply measured on one host; the native "
            "animation/render frame follows the replay handler."
        ),
    }


def markdown_report(summary: dict[str, Any]) -> str:
    lines = [
        "# SimsOnline transport latency report",
        "",
        f"Trace records: **{summary['trace_records']}**",
        "",
        "| Stage | Samples | mean ms | p50 ms | p95 ms | p99 ms | max ms |",
        "|---|---:|---:|---:|---:|---:|---:|",
    ]
    for stage, values in summary["stages"].items():
        timings = values.get("elapsed_ms") or values.get("source_to_apply_ms") or {}
        def number(key: str) -> str:
            return str(timings[key]) if key in timings else "—"
        lines.append(
            f"| {stage} | {values['samples']} | {number('mean')} | "
            f"{number('p50')} | {number('p95')} | {number('p99')} | {number('max')} |"
        )
    if not summary["stages"]:
        lines.append("| *(no trace records found)* | 0 | — | — | — | — | — |")
    lines.extend(["", "## Coverage", ""])
    for stage in EXPECTED_FOUR_STAGE:
        lines.append(f"- {stage}: {'observed' if stage in summary['stages'] else 'missing'}")
    if summary["warning"]:
        lines.extend(["", f"**Caution:** {summary['warning']}"])
    hop = summary.get("network_delivery_hop")
    if hop is not None:
        timing = hop["dispatch_to_receive_ms"]
        lines.extend(["", "## Same-host network delivery hop", ""])
        if timing:
            lines.append(
                f"Exact-sequence matched samples: {hop['samples']}; "
                f"p50={timing['p50']}ms, p95={timing['p95']}ms, "
                f"p99={timing['p99']}ms. "
                f"Unmatched receives: {hop['unmatched_receives']} "
                f"(high-res QPC {hop['high_res_samples']}, "
                f"low-res fallback {hop['low_res_samples']})."
            )
        else:
            lines.append("No exact-sequence dispatch/receive matches.")
        lines.append("Includes queueing and TCP, not source-event or in-game apply latency.")
    source = summary.get("source_to_client_apply")
    if source is not None:
        lines.extend(["", "## Genuine SACS proxy callback to client game replay completion", ""])
        dist = source["source_to_apply_ms"]
        if dist:
            lines.append(
                f"Matched source events: {source['matched_samples']}/{source['source_samples']}; "
                f"p50={dist['p50']}ms, p95={dist['p95']}ms, p99={dist['p99']}ms."
            )
        else:
            lines.append("No matched source/replay-application samples yet.")
        lines.append(
            "The embedded Mono UTC clock resolves ~1ms; this measures replay "
            "handler completion, not the final displayed animation frame."
        )
    record = summary.get("record_to_client_apply")
    if record is not None and record["source_samples"] > 0:
        lines.extend(["", "## Recorder-generated state record to replay completion", ""])
        record_latency = record["source_to_apply_ms"]
        if record_latency:
            lines.append(
                f"Matched records: {record['matched_samples']}/{record['source_samples']}; "
                f"p50={record_latency['p50']}ms, p95={record_latency['p95']}ms, "
                f"p99={record_latency['p99']}ms."
            )
        else:
            lines.append("No matched recorder-state events.")
        lines.append(
            "Includes synthetic/polled presentation records and other observers; "
            "this is NOT a genuine SACS callback-to-client latency metric."
        )
    if "server_bridge_dump" in summary["stages"]:
        s = summary["stages"]["server_bridge_dump"]
        lines.extend([
            "",
            f"Dump pages: {s['samples']}; pages with more=1: "
            f"{s.get('pages_with_more', 0)}; total reported chars: "
            f"{s.get('total_chars', 0)}.",
            "Filtered ring cursor advances do not measure event loss.",
        ])
    return "\n".join(lines) + "\n"


def expand_inputs(patterns: list[str]) -> list[Path]:
    results: list[Path] = []
    seen: set[str] = set()
    for pattern in patterns:
        paths = glob.glob(pattern, recursive=True) or [pattern]
        for value in paths:
            p = Path(value)
            if p.is_file() and str(p.resolve()).lower() not in seen:
                results.append(p)
                seen.add(str(p.resolve()).lower())
            elif not p.is_file():
                print(f"warning: missing input {p}", file=sys.stderr)
    return results


def self_test() -> None:
    samples = [
        "noise\n",
        "[transport-latency] stage=server_bridge_dump elapsed_ms=2.5 page=0 chars=1480 lines=8 from_seq=12 to_seq=22 more=1 targets=2\n",
        "2026 INFO [transport-latency] stage=server_bridge_dump elapsed_ms=7.5 page=1 chars=700 lines=4 from_seq=22 to_seq=27 more=0 targets=2\n",
        "[transport-latency] stage=client_bridge_replay elapsed_ms=4 lines=3 bytes=340\n",
        "[transport-latency] stage=server_bridge_dump elapsed_ms=not-a-number\n",
    ]
    rows = [parsed for line in samples if (parsed := parse_line(line)) is not None]
    assert len(rows) == 4
    assert parse_line("[transport-latency] elapsed_ms=2") is None
    assert parse_line("[transport-latency] stage=bad elapsed_ms=nan") == {"stage": "bad"}
    report = summarize(rows, ["fixture"])
    assert report["stages"]["server_bridge_dump"]["samples"] == 3
    assert report["stages"]["server_bridge_dump"]["elapsed_ms"]["p50"] == 5.0
    assert report["stages"]["server_bridge_dump"]["pages_with_more"] == 1
    assert report["stages"]["server_bridge_dump"]["ring_sequences_examined"] == 15
    assert not report["four_timestamp_ready"]
    assert "Four correlated stages" in report["warning"]
    assert "| client_bridge_replay | 1 |" in markdown_report(report)
    assert percentile([1.0], 99) == 1.0
    assert distribution([]) is None
    matched = [
        {"stage": "server_presentation_dispatch", "at_ms": 1000, "first_seq": 40, "last_seq": 42},
        {"stage": "client_network_receive", "at_ms": 1042, "first_seq": 40, "last_seq": 42},
        {"stage": "client_network_receive", "at_ms": 1099, "first_seq": 40, "last_seq": 42},
        {"stage": "client_network_receive", "at_ms": 1100, "first_seq": 50, "last_seq": 51},
    ]
    hop = dispatch_to_receive(matched)
    assert hop["samples"] == 2 and hop["unmatched_receives"] == 1
    assert hop["dispatch_to_receive_ms"]["p50"] == 70.5
    assert summarize(matched, ["fixture"])["network_delivery_hop"] is None
    assert summarize(matched, ["fixture"], same_host=True)["network_delivery_hop"]["samples"] == 2
    qpc = [
        {"stage": "server_presentation_dispatch", "at_ms": 1000, "at_qpc": 10000000,
         "qpc_freq": 10000000, "first_seq": 40, "last_seq": 42},
        {"stage": "client_network_receive", "at_ms": 1042, "at_qpc": 10420500,
         "qpc_freq": 10000000, "first_seq": 40, "last_seq": 42},
    ]
    hr = dispatch_to_receive(qpc)
    assert hr["high_res_samples"] == 1
    assert hr["low_res_samples"] == 0
    assert hr["dispatch_to_receive_ms"]["p50"] == 42.05
    assert parse_line("[transport-latency] stage=client_network_receive "
                      "at_qpc=10420500 qpc_freq=10000000")["at_qpc"] == 10420500
    source_fixture = [
        {"stage": "source_event", "source_origin": "sacs_proxy", "source_seq": 41, "source_utc_ticks": 100000000},
        {"stage": "client_game_apply", "first_seq": 40, "last_seq": 42,
         "apply_utc_ticks": 102500000},
    ]
    metric = source_to_client_apply(source_fixture)
    assert metric["matched_samples"] == 1
    assert metric["source_to_apply_ms"]["p50"] == 250.0
    legacy = [dict(source_fixture[0]), dict(source_fixture[1])]
    legacy[0].pop("source_origin")
    assert source_to_client_apply(legacy)["matched_samples"] == 0, (
        "Legacy untagged recorder entries must not be treated as SACS callbacks"
    )
    legacy[0]["stage"] = "source_record"
    assert source_to_client_apply(legacy, "source_record")["matched_samples"] == 1
    assert summarize(source_fixture, ["fixture"], same_host=True)["four_timestamp_ready"] is False
    print("PASS: transport latency report self-tests")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("logs", nargs="*", help="Log files or glob patterns")
    parser.add_argument("--json", action="store_true", help="Emit machine-readable JSON")
    parser.add_argument("--output", type=Path, help="Write report to this file")
    parser.add_argument("--self-test", action="store_true", help="Run deterministic unit fixture")
    parser.add_argument("--same-host", action="store_true",
                        help="Correlate dispatch->receive by monotonic tick; ONLY when server and client ran on one PC")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return 0
    inputs = expand_inputs(args.logs)
    if not inputs:
        parser.error("provide a log file or glob (or run --self-test)")
    rows: list[dict[str, Any]] = []
    for path in inputs:
        with path.open("r", encoding="utf-8-sig", errors="replace") as stream:
            for line in stream:
                parsed = parse_line(line)
                if parsed is not None:
                    rows.append(parsed)
    summary = summarize(rows, [str(p) for p in inputs], same_host=args.same_host)
    result = (
        json.dumps(summary, indent=2, sort_keys=True) + "\n"
        if args.json else markdown_report(summary)
    )
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(result, encoding="utf-8")
        print(f"Wrote {args.output}")
    else:
        sys.stdout.write(result)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
