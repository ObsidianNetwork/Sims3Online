"""automated server-vs-client presentation comparison.

Every --interval seconds, reads PROBE_PRESENTATION on the server and on a client (through the
bridgectl admin listeners) for the Sims on player-occupied lots, matches them by SimDescriptionId,
and checks what a player would see:

    present    the client has the Sim at all
    interaction current interaction key
    posture    posture type
    container  posture container type (and, separately, container id: furniture identity)
    animation  the state machines animating the Sim: the posture's (anim_posture) and the running
               interaction's (anim_interaction). Native active clips are empty in release builds.
    anim_state the SacsRecorder's last state machine + state for the Sim (SACS_RECORDER on, both sides);
               only judged when both sides recorded one. The same fact for interaction replay and
               presentation replay.
    held       right- and left-hand object types
    position   distance <= --position-tolerance, only while the Sim stands still on both sides
    moving     distance <= --moving-tolerance while the server's Sim walks (moved more than
               WALKING_STEP since the last sample; )

A mismatch only counts when the same check fails for the same Sim in two consecutive samples, so a
client trailing the server by a second is not penalised. Results are ranked by the server's
interaction key at the time (the "category"), so the report replaces hand-testing interaction by
interaction.

    python scripts/presentation_compare.py --duration 3600 --out .omo/run/presentation-baseline
    python scripts/presentation_compare.py --duration 120 --interval 3   # smoke run
    python scripts/presentation_compare.py --duration 120 --clients 1,2,3   # every player's game in one run

--client is 1, 2, 3 (a player's game, scripts/compare_client.py) or HOST:PORT; default client 1. --clients 1,2,3
compares several clients in one run: the server is read once a sample and each client is compared with that same
reading, each with its own tally, in --out/client1, --out/client2, ...; every report line names each client's scores.
Player Sims default to the claims in each compared client's controller log (.omo/run/client.log, client2.log, ...:
sim_claimed) in the checkout that booted that client (stack_who.game_checkouts; this checkout's when no checkout's
files name the client's live game); --claimed overrides.
Writes samples.jsonl, summary.json and report.md to --out (summary and report refresh every
--report-every samples, so an interrupted run still leaves a report).
"""

from __future__ import annotations

import argparse
import contextlib
import importlib.util
import json
import math
import re
import sys
import time
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))
import compare_client  # noqa: E402

CHECKS =["present", "interaction", "posture", "container", "container_id", "anim_posture", "anim_interaction", "anim_state",
          "held", "position", "moving", "outfit", "shown"]
# With the presentation stream a client runs no interaction logic: a puppet's queue is empty and its
# posture object stays standing while the stream animates it. These checks read what a player sees
# whatever runs underneath; the others read the client's logic layer.
VISIBLE_CHECKS = ["present", "anim_state", "held", "position", "moving", "outfit", "shown"]
# A server Sim that moved further than this since the last sample is walking.
WALKING_STEP = 0.5
FIELDS = ["object", "description", "lot", "x", "y", "z", "posture", "container_id", "container_type",
          "interaction", "posture_machine", "interaction_machine", "right", "left", "sacs", "outfit", "hidden", "lod"]


def load_bridgectl():
    spec = importlib.util.spec_from_file_location("bridgectl", ROOT / "scripts" / "bridgectl.py")
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def claimed_from_client_log(path: Path) -> list[int]:
    if not path.exists():
        return []
    ids = re.findall(r'"\$type":"sim_claimed","playerId":\d+,"simObjectId":(\d+)', path.read_text(encoding="utf-8", errors="replace"))
    return sorted({int(i) for i in ids})


def parse_report(report: str) -> dict[int, dict]:
    sims: dict[int, dict] = {}
    for line in report.splitlines()[1:]:
        parts = line.split("|")
        while len(parts) < len(FIELDS) and len(parts) >= len(FIELDS) - 4:
            parts.append("-")  # an older mod without the SacsRecorder / outfit / hidden / lod fields
        if len(parts) != len(FIELDS):
            continue
        rec = dict(zip(FIELDS, parts))
        for key in ("object", "description", "lot", "container_id"):
            rec[key] = int(rec[key])
        for key in ("x", "y", "z"):
            rec[key] = float(rec[key])
        sims[rec["description"]] = rec
    return sims


class Probe:
    def __init__(self, bridgectl, endpoint: str, timeout: float):
        self.bridgectl = bridgectl
        self.endpoint = endpoint
        self.timeout = timeout
        self.sock = None

    def command(self, line: str) -> str | None:
        try:
            if self.sock is None:
                self.sock = self.bridgectl.connect(self.endpoint, self.timeout)
            response = self.bridgectl.roundtrip(self.sock, line)
        except SystemExit as ex:
            self.sock = None
            return f"error: {ex}"
        return response.get("report") or response.get("error") or response.get("reason")

    def read(self, lots: list[int], sims: list[int]) -> dict[int, dict] | None:
        lot_field = ",".join(str(x) for x in lots) if lots else "-"
        sim_field = ",".join(str(x) for x in sims) if sims else "-"
        try:
            if self.sock is None:
                self.sock = self.bridgectl.connect(self.endpoint, self.timeout)
            response = self.bridgectl.roundtrip(self.sock, f"PROBE_PRESENTATION {lot_field} {sim_field}")
        except SystemExit as ex:  # bridgectl exits on connect/timeout errors
            print(f"[{self.endpoint}] {ex}", file=sys.stderr)
            self.sock = None
            return None
        if not response.get("ok") or not response.get("accepted"):
            print(f"[{self.endpoint}] rejected: {response.get('error') or response.get('reason')}", file=sys.stderr)
            return None
        return parse_report(response.get("report") or "")


def moved(a: dict | None, b: dict | None) -> float:
    if a is None or b is None:
        return math.inf
    return math.dist((a["x"], a["y"], a["z"]), (b["x"], b["y"], b["z"]))


def compare(server: dict, client: dict | None, server_prev: dict | None, client_prev: dict | None, tolerance: float,
            moving_tolerance: float = 1.0) -> dict[str, bool | None]:
    """True = match, False = mismatch, None = not applicable this sample."""
    if client is None:
        return {"present": False, **{c: None for c in CHECKS if c != "present"}}
    at_rest = (server_prev is not None and client_prev is not None
               and server_prev["posture"] == server["posture"] and server_prev["interaction"] == server["interaction"]
               and moved(server, server_prev) < 0.05 and moved(client, client_prev) < 0.05)
    walking = server_prev is not None and moved(server, server_prev) > WALKING_STEP
    has_container = server["container_type"] != "-" or client["container_type"] != "-"
    return {
        "present": True,
        "interaction": server["interaction"] == client["interaction"],
        "posture": server["posture"] == client["posture"],
        "container": server["container_type"] == client["container_type"],
        "container_id": (server["container_id"] == client["container_id"]) if has_container else None,
        "anim_posture": server["posture_machine"] == client["posture_machine"],
        "anim_interaction": server["interaction_machine"] == client["interaction_machine"],
        "anim_state": (server["sacs"] == client["sacs"]) if server["sacs"] != "-" and client["sacs"] != "-" else None,
        "held": (server["right"], server["left"]) == (client["right"], client["left"]),
        "position": (moved(server, client) <= tolerance) if at_rest else None,
        "moving": (moved(server, client) <= moving_tolerance) if walking else None,
        "outfit": (server["outfit"] == client["outfit"]) if server["outfit"] != "-" and client["outfit"] != "-" else None,
        # Drawn or not (World.ObjectGetHiddenFlags 0 = drawn): a client's game hides a Sim on a lot it does not
        # simulate (, players on separate lots).
        "shown": ((server["hidden"] == "0") == (client["hidden"] == "0")) if server["hidden"] != "-" and client["hidden"] != "-" else None,
    }


def category(interaction: str) -> str:
    return interaction if interaction and interaction != "-" else "(idle)"


class Tally:
    def __init__(self) -> None:
        self.samples = 0
        self.sim_samples = 0
        self.checks = {c: {"match": 0, "mismatch": 0, "transient": 0} for c in CHECKS}
        self.by_category: dict[str, dict] = defaultdict(lambda: {"sim_samples": 0, **{c: 0 for c in CHECKS}})
        self.examples: dict[tuple[str, str], dict] = {}
        self.last_fail: dict[tuple[int, str], bool] = {}

    def add(self, desc: int, server: dict, client: dict | None, result: dict[str, bool | None]) -> None:
        self.sim_samples += 1
        cat = category(server["interaction"])
        self.by_category[cat]["sim_samples"] += 1
        for check, ok in result.items():
            key = (desc, check)
            if ok is None:
                self.last_fail.pop(key, None)
                continue
            if ok:
                self.checks[check]["match"] += 1
                self.last_fail.pop(key, None)
                continue
            if self.last_fail.get(key):
                self.checks[check]["mismatch"] += 1
                self.by_category[cat][check] += 1
                self.examples.setdefault((cat, check), {
                    "server": {k: server[k] for k in ("interaction", "posture", "container_type", "posture_machine", "interaction_machine", "sacs", "right", "left")},
                    "client": None if client is None else {k: client[k] for k in ("interaction", "posture", "container_type", "posture_machine", "interaction_machine", "sacs", "right", "left")},
                })
            else:
                self.checks[check]["transient"] += 1
            self.last_fail[key] = True

    def summary(self) -> dict:
        scores = {}
        for check, c in self.checks.items():
            judged = c["match"] + c["mismatch"]
            scores[check] = None if judged == 0 else round(c["match"] / judged, 4)
        scored = [v for k, v in scores.items() if v is not None and k != "container_id"]
        ranked = sorted(
            ({"category": cat, **row, "mismatches": sum(row[c] for c in CHECKS if c != "container_id")} for cat, row in self.by_category.items()),
            key=lambda r: (-r["mismatches"], -r["sim_samples"]))
        visible = [scores[k] for k in VISIBLE_CHECKS if scores.get(k) is not None]
        return {
            "samples": self.samples,
            "sim_samples": self.sim_samples,
            "score": round(sum(scored) / len(scored), 4) if scored else None,
            "visible_score": round(sum(visible) / len(visible), 4) if visible else None,
            "check_scores": scores,
            "checks": self.checks,
            "ranked": ranked,
            "examples": [{"category": k[0], "check": k[1], **v} for k, v in self.examples.items()],
        }


def write_report(out: Path, summary: dict, meta: dict) -> None:
    (out / "summary.json").write_text(json.dumps({"meta": meta, **summary}, indent=1), encoding="utf-8")
    lines = [
        "# Server-vs-client presentation comparison",
        "",
        (f"- Samples: {summary['samples']} ({summary['sim_samples']} Sim-samples), interval {meta['interval']} s, "
         f"player Sims {meta['claimed']}"),
        f"- **Overall score: {summary['score']}** (mean of per-check match rates; persistent mismatches only)",
        (f"- **Visible score: {summary['visible_score']}** ({', '.join(VISIBLE_CHECKS)}: what a player sees; "
         "the rest read the client's logic layer, empty on a presenter)"),
        "",
        "| Check | Score | Match | Persistent mismatch | Transient (1 sample) |",
        "|---|---|---|---|---|",
    ]
    for check in CHECKS:
        c = summary["checks"][check]
        lines.append(f"| {check} | {summary['check_scores'][check]} | {c['match']} | {c['mismatch']} | {c['transient']} |")
    lines += ["", "## Ranked by the server's interaction", "",
              "| Interaction | Sim-samples | Mismatches | " + " | ".join(c for c in CHECKS if c != "container_id") + " |",
              "|---|---|---|" + "---|" * (len(CHECKS) - 1)]
    for row in summary["ranked"][:40]:
        lines.append(f"| `{row['category']}` | {row['sim_samples']} | {row['mismatches']} | "
                     + " | ".join(str(row[c]) for c in CHECKS if c != "container_id") + " |")
    lines += ["", "## First example per (interaction, check)", ""]
    for ex in summary["examples"][:60]:
        lines.append(f"- `{ex['category']}` / **{ex['check']}**: server {ex['server']} / client {ex['client']}")
    (out / "report.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


class Client:
    """One compared client: its probe, its tally, its last reading (with the server's it was compared with), its
    output folder."""

    def __init__(self, name: str, probe: Probe, out: Path) -> None:
        self.name, self.probe, self.out = name, probe, out
        self.tally = Tally()
        self.prev: dict[int, dict] = {}
        self.server_prev: dict[int, dict] = {}


def sample_once(server_probe: Probe, clients: list[Client], lots: list[int], claimed: list[int], tolerance: float,
                moving_tolerance: float) -> tuple[list[int], dict[str, dict]]:
    """One sample: the server read once, then each client compared with that same reading. Returns the lots the claimed
    Sims are on now (the old ones when the server did not answer) and each answering client's samples.jsonl record."""
    server = server_probe.read(lots, claimed)
    if server is None:
        return lots, {}
    # Player lots follow the claimed Sims (they walk to work, visit, etc.).
    by_object = {rec["object"]: rec for rec in server.values()}
    lots = sorted({by_object[c]["lot"] for c in claimed if c in by_object and by_object[c]["lot"]})
    on_lots = {d: r for d, r in server.items() if r["lot"] in lots or r["object"] in claimed}
    records = {}
    for client in clients:
        reading = client.probe.read(lots, claimed)
        if reading is None:
            continue
        client.tally.samples += 1
        record = {"t": round(time.time(), 2), "lots": lots, "sims": {}}
        for desc, rec in on_lots.items():
            result = compare(rec, reading.get(desc), client.server_prev.get(desc), client.prev.get(desc), tolerance, moving_tolerance)
            client.tally.add(desc, rec, reading.get(desc), result)
            record["sims"][str(desc)] = {"server": rec, "client": reading.get(desc), "result": result}
        records[client.name] = record
        client.server_prev, client.prev = server, reading
    return lots, records


def scores_line(clients: list[Client]) -> str:
    """samples=N score=S visible=V for one client (the line before --clients); per client, named, for several."""
    parts = []
    for client in clients:
        s = client.tally.summary()
        parts.append(("" if len(clients) == 1 else f"{client.name}: ")
                     + f"samples={s['samples']} sim_samples={s['sim_samples']} score={s['score']} visible={s['visible_score']}")
    return "; ".join(parts)


def client_log(endpoint: str, checkouts: dict[str, Path] | None = None) -> Path:
    """The controller log a client's claims are in (stackctl.client_files): client.log for client 1 or any endpoint
    that is not a player's, client<N>.log for player N; in the run folder of the checkout that booted that client
    (checkouts: stack_who.game_checkouts, role -> worktree), else this checkout's."""
    player = compare_client.player_of(endpoint)
    role = "client" if player in (None, "1") else f"client{player}"
    return (checkouts or {}).get(role, ROOT) / ".omo" / "run" / f"{role}.log"


def default_claims(endpoints: list[str]) -> tuple[list[int], list[Path]]:
    """The player Sims the compared clients claimed (claimed_from_client_log) and the logs read. Each client's log is
    the one of the checkout that booted it (stack_who.game_checkouts: a stack another worktree booted, Main 2026-10-22),
    else this checkout's."""
    try:
        import stack_who

        checkouts = stack_who.game_checkouts(ROOT)
    except Exception:  # no process table or git to ask: this checkout's logs, as before
        checkouts = {}
    logs = list(dict.fromkeys(client_log(e, checkouts) for e in endpoints))
    return sorted({c for log in logs for c in claimed_from_client_log(log)}), logs


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--server", default="127.0.0.1:34029")
    parser.add_argument("--client", default="", help="1, 2, 3 (a player's game) or HOST:PORT; default client 1")
    parser.add_argument("--clients", default="", help="several clients in one run, comma-separated (1,2,3)")
    parser.add_argument("--claimed", default="",
                        help="comma-separated player Sim object ids (default: the claims in the booting checkout's client logs)")
    parser.add_argument("--interval", type=float, default=3.0)
    parser.add_argument("--duration", type=float, default=3600.0)
    parser.add_argument("--position-tolerance", type=float, default=0.5)
    parser.add_argument("--moving-tolerance", type=float, default=1.0)
    parser.add_argument("--timeout", type=float, default=30.0)
    parser.add_argument("--report-every", type=int, default=20)
    parser.add_argument("--out", default=str(ROOT / ".omo" / "run" / "presentation-compare"))
    parser.add_argument("--no-recorder", action="store_true", help="do not switch SACS_RECORDER on (no anim_state check)")
    args = parser.parse_args(argv)
    if args.client and args.clients:
        parser.error("--client and --clients: give one")
    try:
        endpoints = [compare_client.resolve(c) for c in args.clients.split(",") if c.strip()] or [compare_client.resolve(args.client)]
    except ValueError as ex:
        parser.error(str(ex))
    endpoints = list(dict.fromkeys(endpoints))

    claimed = [int(x) for x in args.claimed.split(",") if x]
    logs: list[Path] = []
    if not claimed:
        claimed, logs = default_claims(endpoints)
        elsewhere = [str(log) for log in logs if log.parent != ROOT / ".omo" / "run"]
        if claimed and elsewhere:
            print(f"[compare] player Sims {claimed} from the booting checkout's {', '.join(elsewhere)}", flush=True)
    if not claimed:
        print("no player Sims" + (f" in {', '.join(str(log) for log in logs)}" if logs else "")
              + ": pass --claimed or start a client that claims a Sim", file=sys.stderr)
        return 2
    out = Path(args.out)
    started = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    bridgectl = load_bridgectl()
    server_probe = Probe(bridgectl, args.server, args.timeout)
    # One client keeps --out itself (as before --clients); several get a folder each: --out/client1, --out/client2
    clients = [Client(compare_client.name(e), Probe(bridgectl, e, args.timeout),
                      out if len(endpoints) == 1 else out / compare_client.name(e).replace(" ", "").replace(":", "-"))
               for e in endpoints]
    metas = {c.name: {"server": args.server, "client": c.probe.endpoint, "claimed": claimed, "interval": args.interval,
                      "position_tolerance": args.position_tolerance, "moving_tolerance": args.moving_tolerance,
                      "started": started} for c in clients}
    for client in clients:
        client.out.mkdir(parents=True, exist_ok=True)
    if not args.no_recorder:
        for probe in [server_probe] + [c.probe for c in clients]:
            print(f"[{probe.endpoint}] {probe.command('SACS_RECORDER on')}", flush=True)
    lots: list[int] = []
    deadline = time.monotonic() + args.duration
    with contextlib.ExitStack() as stack:
        files = {c.name: stack.enter_context((c.out / "samples.jsonl").open("a", encoding="utf-8")) for c in clients}
        while time.monotonic() < deadline:
            begun = time.monotonic()
            lots, records = sample_once(server_probe, clients, lots, claimed, args.position_tolerance, args.moving_tolerance)
            for client in clients:
                if client.name not in records:
                    continue
                files[client.name].write(json.dumps(records[client.name]) + "\n")
                if client.tally.samples % args.report_every == 0:
                    files[client.name].flush()
                    write_report(client.out, client.tally.summary(), metas[client.name])
            if any(c.name in records and c.tally.samples % args.report_every == 0 for c in clients):
                print(f"[compare] {scores_line(clients)}", flush=True)
            time.sleep(max(0.0, args.interval - (time.monotonic() - begun)))
    for client in clients:
        write_report(client.out, client.tally.summary(), metas[client.name])
    reports = " ".join(str(c.out / "report.md") for c in clients)
    print(f"[compare] done {scores_line(clients)} report={reports}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
