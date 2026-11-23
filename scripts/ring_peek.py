"""Page a game's recorder ring for lines containing a text (SACS_RECORDER peek, which returns one ~1,400-char page).

    python scripts/ring_peek.py "|AW|"                        # the server's whole ring (its last 4,096 lines)
    python scripts/ring_peek.py 544617567 --since 39660       # one driver's or object's lines since a sequence
    python scripts/ring_peek.py "|AS|" --endpoint client --max 20

No cursor moves (works while push is on). The ring holds 4,096 lines (SacsRecorder.RingCapacity), about 40 s of a
busy stack, so look right after the event. Prints the matches (seq first), then one summary line.
"""
from __future__ import annotations

import argparse
import re
import sys

import bridgectl

ENDPOINTS = {"server": "127.0.0.1:34029", "client": "127.0.0.1:34030", "client2": "127.0.0.1:34031"}
RING_LINES = 4096


def ask(sock, *parts: str) -> str:
    resp = bridgectl.roundtrip(sock, " ".join(parts))
    if not resp.get("ok"):
        raise SystemExit(f"bridge error: {resp.get('error')}")
    return resp.get("report") or ""


def sequence(sock) -> int:
    m = re.search(r"sequence=(\d+)", ask(sock, "SACS_RECORDER", "push-status"))
    return int(m.group(1)) if m else 0


def peek(sock, text: str, since: int, max_pages: int = 2000) -> tuple[list[str], int, int]:
    """All ring lines after `since` containing `text`: (lines, last sequence read, pages). The bridge splits a command
    on '|', so the game filters on the longest '|'-free part of the text and the whole text is matched here."""
    server_text = max(text.split("|"), key=len)
    found: list[str] = []
    pages = 0
    while pages < max_pages:
        parts = ["SACS_RECORDER", "peek", str(since)] + ([server_text] if server_text else [])
        rows = ask(sock, *parts).split("\n")
        pages += 1
        m = re.search(r"seq=(\d+)", rows[0])
        last = int(m.group(1)) if m else since
        found.extend(r for r in rows[1:] if r and text in r)
        if last <= since:
            break
        since = last
    return found, since, pages


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("text", help="substring to keep (an id, '|AW|', '|R|<driver>|')")
    ap.add_argument("--endpoint", default="server", help="server, client, client2 or host:port")
    ap.add_argument("--since", type=int, help="ring sequence to start after (default: the oldest line the ring holds)")
    ap.add_argument("--max", type=int, default=40, help="matches to print (newest last)")
    ap.add_argument("--width", type=int, default=220)
    args = ap.parse_args()
    sock = bridgectl.connect(ENDPOINTS.get(args.endpoint, args.endpoint), 30.0)
    since = args.since if args.since is not None else max(0, sequence(sock) - RING_LINES)
    found, last, pages = peek(sock, args.text, since)
    for line in found[-args.max:]:
        print(line[: args.width])
    print(f"{len(found)} line(s) with {args.text!r} in {args.endpoint} ring {since}..{last} ({pages} pages)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
