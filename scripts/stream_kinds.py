"""Where a presentation line kind went: the server's lines handed to its controller, the lines its controller queued
for this client's player, and the client's received lines, by kind, with the client's walk holds, skip sites and the
walker copy's counters.

    python scripts/stream_kinds.py                         # every kind; saved, and compared with this client's last read
    python scripts/stream_kinds.py --kinds AW,AS,P,Pn,Ph   # only these kinds
    python scripts/stream_kinds.py --client client2 --label after-1min

Reads (one bridge call each, nothing changes in either game):
  server SACS_RECORDER push-status   sentByKind: push pages, dumps and snapshot-driver history pages (every player's)
  server PRESENTATION_SENT           the server controller's own count (not the mod's): the lines it queued for each
                                     player, live pages for every player joined at the time, a joiner's history for it
                                     alone. The client's player is --player, else its controller's state file
                                     (client.state.json / client2.state.json `player`), else 1 or 2.
  client SACS_RECORDER replay-skips  receivedByKind (every line a replay batch carried, before any filter) and skip sites
  client SACS_RECORDER replay-skips K  for each --kinds kind: its skips by site and each site's last skipped line of it
  client SACS_REPLAY 00              an empty batch: its report's routeLines (walk holds: held, released, dropped)
  both   SACS_RECORDER walker-status option C's awLines / awApplied / awSkipped / ...

Reading it: the gap is queued-for-this-player minus received when the server controller answers PRESENTATION_SENT
(exact with two players: lines sent before a player joined never reach it), else the mod's sent total minus received.
Queued but not received is the client controller (or the client has not read them yet); sent by the mod and queued
for nobody is the server controller (no audience, discarded pages); received with no apply,
skip or failure is the client, and `held - released` lines still waiting in a client walk's hold say where. Site 7960
is lines a presentation reset dropped while they waited, 7961 a walk hold past its cap. A mod from before
LineKindCounts reports no sentByKind / receivedByKind ("-"); one from before SkipsByKind no skips by kind (the kind's
rows are then filtered from the latest 64 skips, which a busy join turns over in a minute).

Each read is saved under .omo/run/stream-kinds/ and the next read of the same client prints what moved since (a walk
that ended releases its held lines, so read right after a join and again a minute later, before any rejoin: a warm
reset is what erases).
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import time
from pathlib import Path

import bridgectl

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / ".omo" / "run" / "stream-kinds"
ENDPOINTS = {"server": "127.0.0.1:34029", "client": "127.0.0.1:34030", "client2": "127.0.0.1:34031",
             "client3": "127.0.0.1:34032"}  # client3: player 3
WALKER_KEYS = ("awMarked", "awLines", "awWalkers", "awApplied", "awWritten", "awPending", "awSpareDiffers", "awFailed",
               "awMalformed", "awSkipped", "awCarries")


def parse_kinds(text: str, field: str) -> dict[str, int]:
    """'...field=AS:12/AW:5...' -> {'AS': 12, 'AW': 5}; {} when the field is missing or '-'."""
    m = re.search(re.escape(field) + r"=([^;,\s]*)", text or "")
    out: dict[str, int] = {}
    if not m or m.group(1) in ("", "-"):
        return out
    for part in m.group(1).split("/"):
        kind, _, count = part.rpartition(":")
        if kind and count.isdigit():
            out[kind] = int(count)
    return out


def parse_sites(text: str, field: str = "sites") -> dict[str, int]:
    """replay-skips' 'sites=6982:6,7960:3' -> {'6982': 6, '7960': 3}; field='bySite' for one kind's ('-': {})."""
    m = re.search(r"(?<![A-Za-z])" + re.escape(field) + r"=([0-9:,]*)", text or "")
    out: dict[str, int] = {}
    for part in (m.group(1).split(",") if m else []):
        site, _, count = part.partition(":")
        if site and count.isdigit():
            out[site] = int(count)
    return out


def parse_route(text: str) -> dict[str, int]:
    """SACS_REPLAY's 'routeLines=held:H,linesReleased:R,linesDropped:D' -> held/released/dropped (waiting = H - R - D
    is an upper bound: lines a reset dropped count as held and never released)."""
    m = re.search(r"routeLines=held:(\d+),linesReleased:(\d+),linesDropped:(\d+)", text or "")
    if not m:
        return {}
    held, released, dropped = (int(g) for g in m.groups())
    return {"held": held, "released": released, "dropped": dropped}


def parse_fields(text: str, keys: tuple[str, ...]) -> dict[str, int]:
    out = {}
    for key in keys:
        m = re.search(r"\b" + re.escape(key) + r"=(\d+)", text or "")
        if m:
            out[key] = int(m.group(1))
    return out


def parse_players(text: str) -> dict[int, dict[str, int]] | None:
    """PRESENTATION_SENT's 'p1=AS:2/AW:5;p2=AS:1' -> {1: {'AS': 2, 'AW': 5}, 2: {'AS': 1}}; '-' (nothing queued yet)
    -> {}; None when the controller does not answer it (one from before PresentationSentCounts: the mod's error)."""
    text = (text or "").strip()
    if text == "-":
        return {}
    parts = text.split(";")
    if not text or not all(re.fullmatch(r"p\d+=[^;]*", part) for part in parts):
        return None
    out: dict[int, dict[str, int]] = {}
    for part in parts:
        player, _, kinds = part.partition("=")
        out[int(player[1:])] = parse_kinds("q=" + kinds, "q")
    return out


def kind_rows(sent: dict[str, int], received: dict[str, int], kinds: list[str] | None,
              last: dict | None, queued: dict[str, int] | None = None, player: int | None = None) -> list[str]:
    """One row per kind: the mod's sent total, the lines the server controller queued for this client's player (when it
    answers PRESENTATION_SENT: the gap is then taken on them), received, the gap, and the change since the last read."""
    names = kinds or sorted(set(sent) | set(received) | set(queued or {}))
    prev_s = (last or {}).get("sent", {})
    prev_q = (last or {}).get("queued") or {}
    prev_r = (last or {}).get("received", {})
    rows = []
    for k in names:
        s, r = sent.get(k, 0), received.get(k, 0)
        q = None if queued is None else queued.get(k, 0)
        expected = s if q is None else q
        moved = ""
        if last is not None:
            to_moved = "" if q is None else f", +{q - prev_q.get(k, 0)} to p{player}"
            moved = f"  (+{s - prev_s.get(k, 0)} sent{to_moved}, +{r - prev_r.get(k, 0)} received)"
        to = "" if q is None else f"  to p{player} {q:>7}"
        flag = "  <- not received" if expected > r else ""
        rows.append(f"  {k:<4} sent {s:>7}{to}  received {r:>7}  gap {expected - r:>6}{moved}{flag}")
    return rows


def kind_skips(text: str) -> dict | None:
    """`replay-skips <kind>`: {'sites': {site: count}, 'rows': [...]}; None from a mod before SkipsByKind (no 'kind=')."""
    head, _, body = (text or "").partition("\n")
    if ";kind=" not in head:
        return None
    return {"sites": parse_sites(head, "bySite"), "rows": [r for r in body.split("\n") if r][:8]}


def client_player(client: str, given: int | None) -> int:
    """The server's player id for this client: --player, else its controller's state file (`player`, the --player it
    joined with), else N for clientN and 1 otherwise."""
    if given is not None:
        return given
    if client in ("client", "client2", "client3"):
        try:
            state = json.loads((ROOT / ".omo" / "run" / f"{client}.state.json").read_text(encoding="utf-8"))
            return int(state["player"])
        except (OSError, ValueError, KeyError, TypeError):
            pass
    return int(client[6:]) if client in ("client2", "client3") else 1


def read(server: str, client: str, kinds: list[str] | None = None, player: int = 1) -> dict:
    s = bridgectl.connect(server, 30.0)
    c = bridgectl.connect(client, 30.0)

    def ask(sock, line: str) -> str:
        resp = bridgectl.roundtrip(sock, line)
        return (resp.get("report") or resp.get("error") or "") if isinstance(resp, dict) else ""

    push = ask(s, "SACS_RECORDER push-status")
    players = parse_players(ask(s, "PRESENTATION_SENT"))
    skips = ask(c, "SACS_RECORDER replay-skips")
    replay = ask(c, "SACS_REPLAY 00")
    return {
        "time": time.strftime("%Y-%m-%d %H:%M:%S"),
        "client": client,
        "player": player,
        "sent": parse_kinds(push, "sentByKind"),
        "queued": None if players is None else players.get(player, {}),
        "queued_players": None if players is None else sorted(players),
        "received": parse_kinds(skips, "receivedByKind"),
        "sites": parse_sites(skips),
        "route": parse_route(replay),
        "walker_server": parse_fields(ask(s, "SACS_RECORDER walker-status"), WALKER_KEYS),
        "walker_client": parse_fields(ask(c, "SACS_RECORDER walker-status"), WALKER_KEYS),
        "skip_rows": [r for r in skips.split("\n")[1:] if r][:12],
        "skips_by_kind": {k: v for k in (kinds or []) if (v := kind_skips(ask(c, f"SACS_RECORDER replay-skips {k}")))},
    }


def render(now: dict, last: dict | None, kinds: list[str] | None) -> str:
    player = now.get("player")
    out = [f"stream kinds {now['time']}" + (f", player {player}" if player is not None else "")
           + (f" (last read {last['time']})" if last else "")]
    if not now["sent"] and not now["received"]:
        out.append("  no sentByKind / receivedByKind: a mod from before LineKindCounts")
    if now.get("queued") is None:
        out.append("  no PRESENTATION_SENT: a server controller from before PresentationSentCounts; the gap is the mod's "
                   "total, every player's (lines sent before this client joined never reach it)")
    else:
        out.append("  the server controller queued lines for: "
                   + (", ".join(f"p{p}" for p in now.get("queued_players") or []) or "nobody yet"))
    out += kind_rows(now["sent"], now["received"], kinds, last, now.get("queued"), player)
    r = now["route"]
    if r:
        out.append(f"  client walk holds: held {r['held']}, released {r['released']}, dropped {r['dropped']}; "
                   f"waiting or lost to a reset at most {r['held'] - r['released'] - r['dropped']}")
    out.append("  client skip sites: " + (", ".join(f"{k}:{v}" for k, v in sorted(now["sites"].items())) or "none"))
    for side in ("server", "client"):
        w = now[f"walker_{side}"]
        if w:
            out.append(f"  walker-status {side}: " + ", ".join(f"{k}={v}" for k, v in w.items()))
    by_kind = now.get("skips_by_kind") or {}
    for k, v in by_kind.items():
        out.append(f"  {k} skipped by site: " + (", ".join(f"{s}:{n}" for s, n in sorted(v["sites"].items())) or "none"))
        out += [f"    skipped: {row[:160]}" for row in v["rows"]]
    if kinds and not by_kind:
        rows = [row for row in now["skip_rows"] if any(f"|{k}|" in row for k in kinds)]
        out += [f"    skipped: {row[:160]}" for row in rows]
    return "\n".join(out)


def last_read(saved: list[Path], client: str) -> dict | None:
    """The newest saved read of this client endpoint (a read from before the endpoint was saved counts as client 1's),
    so alternating client and client2 reads each compare with their own last one."""
    default = ENDPOINTS["client"]
    for path in reversed(saved):
        try:
            read_ = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        if read_.get("client", default) == client:
            return read_
    return None


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--kinds", help="comma-separated kinds to show (default: every kind either side counted)")
    ap.add_argument("--client", default="client", help="client, client2, client3 or host:port")
    ap.add_argument("--server", default="server")
    ap.add_argument("--player", type=int, help="the server's player id for this client (default: its state file)")
    ap.add_argument("--label", default="", help="a word for the saved file name")
    args = ap.parse_args()
    kinds = [k for k in (args.kinds or "").split(",") if k] or None
    now = read(ENDPOINTS.get(args.server, args.server), ENDPOINTS.get(args.client, args.client), kinds,
               client_player(args.client, args.player))
    OUT.mkdir(parents=True, exist_ok=True)
    last = last_read(sorted(OUT.glob("*.json")), now["client"])
    print(render(now, last, kinds))
    name = time.strftime("%Y%m%d-%H%M%S") + (f"-{args.label}" if args.label else "") + ".json"
    (OUT / name).write_text(json.dumps(now, indent=1), encoding="utf-8")
    print(f"saved {OUT / name}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
