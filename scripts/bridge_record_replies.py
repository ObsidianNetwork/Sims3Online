"""Turn a bridge frame record into the reply file gates 5 and 6 read (card 56 §G gates 5 and 6, 1b8fbc27).

    python scripts/bridge_record_replies.py <run>/csharp_bridge.record --out <run>/csharp_replies.txt

Reads the lossless capture the controller writes with --bridge-record (DedicatedServer/Program.cs:509-526; the
frames are ProcessMemoryBridge.cs:42-46's `ctl->host seq=N slot=S <line>` and `host->ctl seq=N <line>`): a copy of
stackctl's .omo/run/server-bridge.log (card 56 ruling f82d9f52). It refuses a record that is not a whole capture:
the first frame must be the controller's CONNECT and the file must end with a newline (a copy cut short fails). A file line that is not a
frame header continues the previous frame's payload (a raw newline in a line). Offline; reads one or two files.

Gate 6 step (d) (card 56, c9a23656 row 208): when a second capture sits beside the first
(`csharp_bridge.2.record`, or the path `--and-record` names), its rows are appended after the first record's, so one
reply file carries both C# halves. `--records-out` writes one line per output row: the record file that row came
from, which is what `simlinkctl reply-diff --row-records` puts in the `record` field of the gate 5 rows.

Writes, in record order, with the corpus escapes (`\\`, `\t`, `\n`, `\r`; rust/simlink_bridge/src/corpus.rs):
- one row per command frame, `<wire verb>\t<command line>\t<reply line>`. The reply is the first host line after
  the command whose second `|` field is the command's correlation id (case-insensitive), each host line claimed
  once; an empty field when none came. CONNECT, the handshake, is not a verb and gets no row;
- one row per host frame no command claimed (the unsolicited EVENT pushes, the HELLO, a late host line),
  `<the line's first | field>\t\t<host line>`, with an empty command column. `EVENT|InteractionReplayQueued` is
  left out (card 56 §E.3: the legacy replay bundle is not ported).
The file keeps the controller's own commands too: `simlinkctl live-diff` aligns each Rust command to the next C#
row with the same command line, correlation ids blanked, skipping empty-command rows. Gate 5 decodes every reply
column of it (`simlinkctl reply-diff --record`), the EVENT pushes included.
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

FRAME = re.compile(r"^(?:\[bridge [^\]]*\] )?(ctl->host|host->ctl) seq=(-?\d+)(?: slot=\S+)? (.*)$")
CORR = re.compile(r"^[0-9a-fA-F]{32}$")


def read_record(path: Path) -> list[str]:
    """The record's lines, split on "\n" only (the controller's WriteLine; a `\r` before it is dropped by
    `frames`). Characters `str.splitlines` would also split on (\x0b, \x1c, U+2028, ...) stay in the payload.
    Bytes that are not UTF-8 raise UnicodeDecodeError: the capture is lossless and must stay so."""
    return path.read_bytes().decode("utf-8-sig").split("\n")


def frames(lines: list[str]) -> list[tuple[str, int, str]]:
    """`(direction, seq, full line)` per frame; header-less lines join the previous payload."""
    out: list[tuple[str, int, str]] = []
    for raw in lines:
        line = raw.rstrip("\r")
        match = FRAME.match(line)
        if match:
            out.append((match.group(1), int(match.group(2)), match.group(3)))
        elif out:
            direction, seq, payload = out[-1]
            out[-1] = (direction, seq, payload + "\n" + line)
    # A trailing newline at the end of the file is not part of the last payload.
    while out and out[-1][2].endswith("\n"):
        out[-1] = (out[-1][0], out[-1][1], out[-1][2][:-1])
    return out


def corr_of(line: str) -> str | None:
    fields = line.split("|")
    return fields[1].lower() if len(fields) > 1 and CORR.match(fields[1]) else None


def pairs(frame_list: list[tuple[str, int, str]]) -> list[tuple[int, int | None]]:
    """`(command frame index, index of the host frame that answers it or None)` per command frame, in send order:
    the first later host line whose correlation id matches, each host line claimed once. CONNECT is skipped."""
    out: list[tuple[int, int | None]] = []
    claimed: set[int] = set()
    for i, (direction, _, line) in enumerate(frame_list):
        if direction != "ctl->host" or line.split("|", 1)[0] == "CONNECT":
            continue
        corr, answer = corr_of(line), None
        if corr is not None:
            for j in range(i + 1, len(frame_list)):
                d, _, host_line = frame_list[j]
                if d == "host->ctl" and j not in claimed and corr_of(host_line) == corr:
                    claimed.add(j)
                    answer = j
                    break
        out.append((i, answer))
    return out


def replies(frame_list: list[tuple[str, int, str]]) -> list[tuple[str, str, str]]:
    """`(wire verb, command line, reply line or "")` per command frame, in send order: the command rows of
    `reply_rows`."""
    return [row for row in reply_rows(frame_list) if row[1]]


# Card 56 §E.3: the legacy replay bundle's push, which the crate does not decode.
LEFT_OUT = ("EVENT|InteractionReplayQueued",)


def reply_rows(frame_list: list[tuple[str, int, str]]) -> list[tuple[str, str, str]]:
    """The reply file's rows in record order: a command row at each command frame, and an empty-command row
    `(first | field, "", host line)` at each host frame no command claimed (LEFT_OUT excepted)."""
    by_command = dict(pairs(frame_list))
    claimed = {j for j in by_command.values() if j is not None}
    rows: list[tuple[str, str, str]] = []
    for i, (direction, _, line) in enumerate(frame_list):
        if i in by_command:
            j = by_command[i]
            rows.append((line.split("|", 1)[0], line, frame_list[j][2] if j is not None else ""))
        elif direction == "host->ctl" and i not in claimed and not line.startswith(LEFT_OUT):
            rows.append((line.split("|", 1)[0], "", line))
    return rows


def escape(field: str) -> str:
    return field.replace("\\", "\\\\").replace("\t", "\\t").replace("\n", "\\n").replace("\r", "\\r")


def format_row(row: tuple[str, str, str]) -> str:
    return "\t".join(escape(f) for f in row)


class CutCapture(Exception):
    """A record that is not a whole capture (exit 1): no CONNECT, a copy cut short, or no command frame."""


def whole_capture(path: Path) -> list[tuple[str, int, str]]:
    """The record's frames, refusing a file that is not a whole capture of one controller's half (card 56 ruling
    f82d9f52): it starts at the controller's CONNECT, ends with a newline, and holds at least one command frame.
    read_record's UnicodeDecodeError means the capture is not lossless; the caller reports it as exit 2."""
    lines = read_record(path)
    frame_list = frames(lines)
    if not frame_list or frame_list[0][0] != "ctl->host" or not frame_list[0][2].startswith("CONNECT|"):
        raise CutCapture(f"{path}: the first frame is not the controller's CONNECT: not a whole capture")
    if lines[-1] != "":
        raise CutCapture(f"{path}: no final newline: the copy is cut short")
    if not any(row[1] for row in reply_rows(frame_list)):
        raise CutCapture(f"{path}: no command frames")
    return frame_list


def second_record(record: Path, explicit: str | None) -> Path | None:
    """Gate 6 step (d)'s capture: the path `--and-record` names, else the `<name>.2.record` sibling of the first
    record, else None (a run folder with no second process yet; an `--and-record` that does not exist is no second
    record either, so the first half alone still extracts)."""
    if explicit is not None:
        return Path(explicit) if Path(explicit).is_file() else None
    if not record.name.endswith(".record"):
        return None
    sibling = record.with_name(f"{record.name[: -len('.record')]}.2.record")
    return sibling if sibling.is_file() else None


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("record", help="a whole --bridge-record capture from its CONNECT (csharp_bridge.record)")
    ap.add_argument("--out", required=True, help="the reply file to write (csharp_replies.txt)")
    ap.add_argument("--and-record", help="gate 6 step (d)'s capture (default: the <record>.2.record sibling, when it "
                                         "exists); its rows are appended after the first record's")
    ap.add_argument("--records-out", help="write one line per output row: the record file that row came from "
                                          "(csharp_replies.records.txt, read back by simlinkctl reply-diff --row-records)")
    args = ap.parse_args(argv)
    targets = [Path(args.record)]
    second = second_record(targets[0], args.and_record)
    if second is not None:
        targets.append(second)
    rows: list[tuple[str, str, str]] = []
    sources: list[str] = []
    for path in targets:
        try:
            found = reply_rows(whole_capture(path))
        except UnicodeDecodeError as ex:
            print(f"{path}: not UTF-8 at byte {ex.start}: the capture is not lossless", file=sys.stderr)
            return 2
        except (CutCapture, OSError) as ex:
            print(f"{ex}; nothing written", file=sys.stderr)
            return 1
        rows += found
        sources += [str(path)] * len(found)
    Path(args.out).write_text("".join(format_row(r) + "\n" for r in rows), encoding="utf-8", newline="\n")
    if args.records_out:
        Path(args.records_out).write_text("".join(s + "\n" for s in sources), encoding="utf-8", newline="\n")
    commands = [r for r in rows if r[1]]
    answered = sum(1 for r in commands if r[2])
    events = sum(1 for r in rows if not r[1] and r[0] == "EVENT")
    per = "; ".join(f"{p.name} {sum(1 for s in sources if s == str(p))} rows" for p in targets)
    print(f"{len(commands)} commands, {answered} with a reply; {len(rows) - len(commands)} unclaimed host lines, "
          f"{events} of them EVENT pushes -> {args.out} ({per})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
