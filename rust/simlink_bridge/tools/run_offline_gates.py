"""Card 56 offline gates in the §H harness order, every output saved in one run folder.

    python rust/simlink_bridge/tools/run_offline_gates.py --run-dir .omo/run/card56-<date>-<slug>
    python rust/simlink_bridge/tools/run_offline_gates.py --run-dir <live run> --capture <live run>/csharp_bridge.record

Runs, from the worktree that holds the crate (cargo always with --target-dir rust/simlink_bridge/target):
  gate 1  cargo test and cargo clippy --all-targets -- -D warnings      -> cargo_test.txt, cargo_clippy.txt
  gate 2  tools/mutation_witnesses.py                                    -> mutation_witnesses.txt (+ .log)
  oracle  oracle/ (BridgeLineCodec) over the fixture corpora; fails when a committed oracle output is stale
  gate 4  simlinkctl verb-diff (+ float-diff, the .NET R/F2 formatting the encoder needs) -> verb_diff.jsonl
  gate 5  with --capture: scripts/bridge_record_replies.py writes csharp_replies.txt from gate 6's recorded
          --bridge-record capture (every host frame, EVENT pushes included), appending step (d)'s
          csharp_bridge.2.record when that second capture is in the run folder, and naming each row's record in
          csharp_replies.records.txt; the oracle's decode-replies dumps its reply column, and simlinkctl
          reply-diff --record --row-records compares them, each row's `record` field naming the record it came from
          -> csharp_replies.txt, csharp_reply_dumps.txt, csharp_replies.records.txt, reply_diff.jsonl. The synthetic
          reply corpus always runs beside it as a
          supplement -> reply_diff_synthetic.jsonl: it can fail gate 5, never pass it. Without --capture gate 5
          is PENDING_LIVE (FAIL when the synthetic diff fails), and so is the scorecard (its verbs_roundtripped
          counts the capture diff only)
  gate 7  simlinkctl audit                                               -> audit_unsafe.txt
  gate 8  tools/offload-tooling/sync_sim96_architect.py verify             -> seal_verify.txt
  U6      the referee's kernel run (a scratch copy of tools/offload-tooling/sim96_referee built under this crate's
          target dir, so nothing is written into the sealed tree or the kernel's) -> referee_run.txt
  §H      simlinkctl scorecard                                           -> scorecard.txt
and writes gates.txt (`gate=<n>|status=<status>|evidence=<file>[|reason=<why not PASS>]`). Gates 3 and 6 need the
live game and the stack claim; they are not run here and stay PENDING_LIVE, as does gate 5 (above). A verb the run
folder's corpora (csharp_corpus.txt, rust_corpus.txt) list as `#@unsent` keeps gates 5 and 6 PENDING_LIVE, named
in their reason. Exit 1 when
any gate failed; else 3 when the only open items are those live or capture-pending gates and gate 8's `attrib +R`
in a new checkout (`sync_sim96_architect.py apply-ro`, which this harness never runs); 0 only when every gate passed.
"""

import argparse
import shutil
import subprocess
import sys
from pathlib import Path

CRATE = Path(__file__).resolve().parent.parent
REPO = CRATE.parent.parent
TARGET = CRATE / "target"
FIX = CRATE / "fixtures"

# Card 56 §G: the live rows (3, 5 and 6) stay PENDING_LIVE until their run folder exists; gate 8 is PENDING_APPLY_RO
# in a checkout without `attrib +R`. Neither is a failure: either keeps the exit code at 3.
PENDING = {"PENDING_LIVE", "PENDING_APPLY_RO"}
GATE5_REASON = ("no recorded capture: gate 5 needs gate 6's C# half, the controller's --bridge-record file "
                "<run folder>/csharp_bridge.record (--capture); the synthetic reply corpus passed but is a supplement only")
GATE_H_REASON = ("verbs_roundtripped counts gate 5's diff over the recorded capture (reply_diff.jsonl), and there is "
                 "no capture (--capture)")
LIVE_GATES = [("3", "PENDING_LIVE", "discovery_agreement.txt", "live game and stack claim needed"),
              ("6", "PENDING_LIVE", "live_roundtrip.jsonl", "live game and stack claim needed")]

Gate = tuple[str, bool | str, str, str]
# The halves' corpora in a live run folder (scripts/card56_corpus.py writes them; README "Live gates").
CORPORA = ("csharp_corpus.txt", "rust_corpus.txt")


def unsent_verbs(run_dir: Path | None) -> list[tuple[str, str]]:
    """The verbs the run folder's corpora list as `#@unsent <Record> <reason>`: no safe form, so neither half sent
    them (card 56 §G gate 6, 4c970a49 row 208). Sorted, each verb once."""
    found: dict[str, str] = {}
    for name in CORPORA if run_dir else ():
        path = run_dir / name
        if path.is_file():
            for line in path.read_text(encoding="utf-8").splitlines():
                if line.startswith("#@unsent "):
                    verb, _, why = line[len("#@unsent "):].partition(" ")
                    found.setdefault(verb, why)
    return sorted(found.items())


def unsent_args(run_dir: Path) -> list[str]:
    """reply-diff's `--unsent-from` for each corpus in the run folder: an unsent verb missing from the capture is
    marked unsent there instead of failing gate 5 (card 56 4c970a49 row 208)."""
    return [a for name in CORPORA if (run_dir / name).is_file() for a in ("--unsent-from", str(run_dir / name))]


def unsent_reason(unsent: list[tuple[str, str]]) -> str:
    return "not sent, no safe form (card 56 gate 6): " + "; ".join(f"{v} ({why})" for v, why in unsent)


def gate5(synthetic_ok: bool, capture_ok: bool | None = None,
          unsent: list[tuple[str, str]] | None = None) -> tuple[str, str]:
    """Gate 5's status and reason: decided by the recorded capture's diff (`capture_ok`, None when there is no
    capture); the synthetic corpus can only fail it, never pass it (card 56 §G gate 5). A verb gate 6 did not send
    never reaches the capture, so gate 5 stays PENDING_LIVE naming it (4c970a49 row 208)."""
    if not synthetic_ok:
        return "FAIL", "the synthetic reply corpus differs from the oracle (or an oracle output is stale)"
    if capture_ok is None:
        return "PENDING_LIVE", GATE5_REASON
    if not capture_ok:
        return "FAIL", "the recorded capture's host lines decode differently (reply_diff.jsonl)"
    if unsent:
        return "PENDING_LIVE", unsent_reason(unsent)
    return "PASS", ""


def live_gates(run_dir: Path | None = None) -> list[Gate]:
    """Gates 3 and 6, never run here; gate 6's reason names the verbs the run's corpora left unsent."""
    unsent = unsent_verbs(run_dir)
    return [(g, s, ev, unsent_reason(unsent) if g == "6" and unsent else why) for g, s, ev, why in LIVE_GATES]


def gate5_evidence(captured: bool) -> str:
    """Gate 5's evidence files: the capture's diff first when there is one, the synthetic supplement always."""
    capture = "reply_diff.jsonl,csharp_replies.txt,csharp_reply_dumps.txt," if captured else ""
    return capture + "reply_diff_synthetic.jsonl"


def gate_h(score_ok: bool, captured: bool, unsent: list[tuple[str, str]] | None = None) -> tuple[str, str]:
    """The scorecard's status: without a capture verbs_roundtripped cannot reach verbs_declared, so a short
    scorecard is PENDING_LIVE there (gates 4 and 7 report their own failures) and FAIL once a capture is in, unless
    gate 6 left verbs unsent: then it is PENDING_LIVE naming them."""
    if score_ok:
        return "PASS", ""
    if captured and unsent:
        return "PENDING_LIVE", unsent_reason(unsent)
    return ("FAIL", "") if captured else ("PENDING_LIVE", GATE_H_REASON)


def capture_error(capture: Path, run_dir: Path) -> str | None:
    """Why `--capture` is refused, or None: it must be the run folder's own csharp_bridge.record (card 56 gate 6).
    Its step (d) sibling `csharp_bridge.2.record`, when the run folder holds one, is read beside it by the
    extractor, so no other capture path is ever taken."""
    expected = run_dir / "csharp_bridge.record"
    return None if capture == expected else f"--capture must be {expected} (gate 6's C# record in this run folder)"


def status(ok: bool | str) -> str:
    return ok if isinstance(ok, str) else ("PASS" if ok else "FAIL")


def gate_lines(gates: list[Gate], live: list[Gate] | None = None) -> list[str]:
    """One `gate=<n>|status=<s>|evidence=<files>[|reason=<text>]` line per gate, the live gates 3 and 6 last."""
    return [f"gate={g}|status={status(ok)}|evidence={ev}" + (f"|reason={why}" if why else "")
            for g, ok, ev, why in [*gates, *(LIVE_GATES if live is None else live)]]


def exit_code(statuses: list[str]) -> int:
    """0 when every gate passed; 3 when the only open gates are live or capture-pending; 1 otherwise."""
    open_ = set(statuses) - {"PASS"}
    if not open_:
        return 0
    return 3 if open_ <= PENDING else 1


def run(cmd: list[str], out: Path, cwd: Path = CRATE) -> bool:
    r = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    out.write_text(f"$ {' '.join(cmd)}\n(exit {r.returncode})\n{r.stdout}{r.stderr}", encoding="utf-8", newline="\n")
    print(f"{'ok  ' if r.returncode == 0 else 'FAIL'} {out.name}: {' '.join(cmd[:4])}", flush=True)
    return r.returncode == 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--run-dir", required=True)
    ap.add_argument("--capture", help="gate 6's C# half: the controller's --bridge-record file (csharp_bridge.record)")
    args = ap.parse_args()
    capture = Path(args.capture).resolve() if args.capture else None
    run_dir = Path(args.run_dir).resolve()
    if capture is not None and (why := capture_error(capture, run_dir) or (None if capture.is_file() else "no such file")):
        ap.error(f"--capture {capture}: {why}")
    run_dir.mkdir(parents=True, exist_ok=True)
    td = ["--target-dir", str(TARGET)]
    ctl = ["cargo", "run", "--offline", "-q", *td, "--bin", "simlinkctl", "--"]
    gates: list[Gate] = []

    test = run(["cargo", "test", "--offline", *td], run_dir / "cargo_test.txt")
    clippy = run(["cargo", "clippy", "--offline", *td, "--all-targets", "--", "-D", "warnings"],
                 run_dir / "cargo_clippy.txt")
    gates.append(("1", test and clippy, "cargo_test.txt,cargo_clippy.txt", ""))

    mutation = run([sys.executable, str(CRATE / "tools" / "mutation_witnesses.py"),
                    "--out", str(run_dir / "mutation_witnesses.txt")], run_dir / "mutation_run.txt")
    gates.append(("2", mutation, "mutation_witnesses.txt", ""))

    # The oracle regenerates its outputs into the run folder; the committed fixtures must equal them.
    oracle = ["dotnet", "run", "--project", str(CRATE / "oracle" / "SimlinkOracle.csproj"), "--"]
    fresh = True
    for mode, corpus, name in [("encode", "verb_corpus.txt", "oracle_verb_lines.txt"),
                               ("decode", "reply_corpus.txt", "oracle_reply_dumps.txt"),
                               ("floats", "float_corpus.txt", "oracle_float_lines.txt")]:
        produced = run_dir / name
        ok = run([*oracle, mode, str(FIX / corpus), str(produced)], run_dir / f"oracle_{mode}.txt")
        # A checkout with core.autocrlf writes the committed copy with CRLF; the content is what must match.
        same = ok and produced.read_bytes().replace(b"\r\n", b"\n") == (FIX / name).read_bytes().replace(b"\r\n", b"\n")
        if ok and not same:
            print(f"FAIL fixtures/{name} differs from the oracle's fresh output", flush=True)
        fresh &= same

    verbs = run([*ctl, "verb-diff", "--corpus", str(FIX / "verb_corpus.txt"),
                 "--oracle", str(FIX / "oracle_verb_lines.txt"), "--out", str(run_dir / "verb_diff.jsonl")],
                run_dir / "verb_diff_run.txt")
    floats = run([*ctl, "float-diff", "--oracle", str(FIX / "oracle_float_lines.txt")], run_dir / "float_diff.txt")
    gates.append(("4", fresh and verbs and floats, "verb_diff.jsonl,float_diff.txt", ""))

    replies = run([*ctl, "reply-diff", "--corpus", str(FIX / "reply_corpus.txt"), "--oracle",
                   str(FIX / "oracle_reply_dumps.txt"), "--out", str(run_dir / "reply_diff_synthetic.jsonl")],
                  run_dir / "reply_diff_synthetic_run.txt")
    capture_ok = None
    if capture is not None:
        replies_file, dumps = run_dir / "csharp_replies.txt", run_dir / "csharp_reply_dumps.txt"
        records_file = run_dir / "csharp_replies.records.txt"
        # Gate 6 step (d)'s second capture, if this run folder has one, is read beside the first; both are the run
        # folder's own records, which is the rule `capture_error` keeps (card 56, c9a23656 row 208).
        per_row_records = ["--row-records", str(records_file)] if records_file.is_file() else []
        capture_ok = (
            run([sys.executable, str(REPO / "scripts" / "bridge_record_replies.py"), str(capture),
                 "--out", str(replies_file), "--records-out", str(records_file)], run_dir / "capture_extract.txt")
            and run([*oracle, "decode-replies", str(replies_file), str(dumps)], run_dir / "oracle_capture_decode.txt")
            and run([*ctl, "reply-diff", "--record", str(capture), "--corpus", str(replies_file),
                     "--oracle", str(dumps), "--out", str(run_dir / "reply_diff.jsonl"), *per_row_records,
                     *unsent_args(run_dir)], run_dir / "reply_diff_run.txt"))
    reply_status, reply_reason = gate5(fresh and replies, capture_ok, unsent_verbs(run_dir))
    gates.append(("5", reply_status, gate5_evidence(capture is not None), reply_reason))

    audit = run([*ctl, "audit"], run_dir / "audit_unsafe.txt")
    gates.append(("7", audit and True, "audit_unsafe.txt",
                  ""))

    gates.append(("8", "PENDING_LIVE", "seal_verify.txt", "offline mirror build"))
    gates.append(("7-U6", "PENDING_LIVE", "referee_run.txt", "offline mirror build"))

    score = run([*ctl, "scorecard", "--run-dir", str(run_dir)], run_dir / "scorecard_run.txt")
    score_status, score_reason = gate_h(score, capture is not None, unsent_verbs(run_dir))
    gates.append(("H", score_status, "scorecard.txt", score_reason))

    live = live_gates(run_dir)
    lines = gate_lines(gates, live)
    (run_dir / "gates.txt").write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    print("\n".join(lines))
    return exit_code([status(ok) for _, ok, _, _ in [*gates, *live]])


if __name__ == "__main__":
    sys.exit(main())
