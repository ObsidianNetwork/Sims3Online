#!/usr/bin/env python3
"""Print Card 57 gates 5/6's claimed, cold-restored sequence, or check its artifacts."""
import argparse
import json
import os
from pathlib import Path
import shlex

try:
    from .card57_wire_diff import COMPARISON_STEPS
except ImportError:
    from card57_wire_diff import COMPARISON_STEPS


def plan(args):
    powershell = args.shell == "powershell"
    quote = (lambda value: "'" + value.replace("'", "''") + "'") if powershell else shlex.quote
    root = args.run_dir.as_posix()
    scratch_name = args.save_name if args.save_name.endswith(".sims3") else args.save_name + ".sims3"
    base, endpoint, boot, scratch = map(quote, (args.base, args.endpoint, args.boot_save, scratch_name))
    common = f"--endpoint {endpoint} --player {args.player} --boot-save {boot} --save-name {scratch}"
    if args.sim:
        common += " --sim " + quote(args.sim)
    binary = args.target_dir.as_posix() + "/debug/simlink_headless_server" + (".exe" if os.name == "nt" else "")
    build = "cargo build --manifest-path rust/simlink_headless_server/Cargo.toml --target-dir " + quote(
        args.target_dir.as_posix())
    environment = {
        "SIMSONLINE_FRONT_DOOR": "0", "SIMSONLINE_RUST_SIDECAR": "1",
        "SIMSONLINE_RUST_SIDECAR_PORT": "34039", "SIMSONLINE_SIMLINK_CLIENT_RING": "1",
        "SIMSONLINE_WORLD_FROM_SERVER": "1",
    }
    # These are printed commands, not calls from the helper. PING/QUIT use the
    # sidecar's documented loopback operator surface, never the public codec.
    def sidecar_command(verb, expected):
        code = ('import socket; s=socket.create_connection(("127.0.0.1",34039),5); '
                's.settimeout(5); f=s.makefile("rwb"); '
                f'f.write(b"{verb}|1\\n"); f.flush(); '
                f'assert f.readline(4096)==b"{expected}|1\\n", "unexpected sidecar reply"; '
                'f.close(); s.close()')
        return "python -c " + quote(code)

    print("# BLOCKED until Card 58's public state stream is integrated: run gates 5 and 6 in one live window.")
    print("# Front-door launch/readiness fixes B1/B2 must also be integrated from the clients lane.")
    print("# Run from this worktree with the live lane's own session identity, never a builder child's identity.")
    print("# Start from the designated disposable cold baseline, not a post-session modified save.")
    print("# The one-time backup below is made only if absent; both phases restore that immutable backup.")
    print("# S3MM must be off/absent for cold gates. Gate 9 (flag-off .NET suite) is separate evidence.")
    print('python scripts/stack_claim.py take "card57 wire differential"')
    if powershell:
        print("if ($LASTEXITCODE -ne 0) { throw 'Stack claim failed' }")
    else:
        print('test "$?" -eq 0 || exit 1')
    print("# PRECONDITION, before entering the block below: start this exact command in a second terminal under")
    print("# the same live lane's claim. The controller only connects; this checkout has no daemon binary target.")
    print("# Supply a previously verified daemon on PATH, or select its path with --sidecar-bin:")
    daemon = ("& " if powershell else "") + quote(args.sidecar_bin)
    print(f"# {daemon} --resident --port 34039 --connections 32 --ticks 4000000")
    print("# The block refuses a missing/wrong daemon at PING, rather than recording a native fail-open baseline.")
    if args.phase in ("both", "line"):
        print("# PRECONDITION: build the front door before the live window if its external-target binary is absent:")
        print("# " + build)
    if powershell:
        print("& {")
        print("  $ErrorActionPreference='Stop'")
        print("  $PSNativeCommandUseErrorActionPreference=$true")
        print("  try {")
        print("    if ($PSVersionTable.PSVersion -lt [version]'7.4') { throw 'Use PowerShell 7.4 or newer' }")
        for name, value in environment.items():
            print(f"    $env:{name}='{value}'")
        if args.phase in ("both", "line"):
            print(f"    if (!(Test-Path -LiteralPath {quote(binary)} -PathType Leaf)) "
                  "{ throw 'Missing Rust binary; run the cargo build command above' }")
        indent = "    "
    else:
        print("(")
        print("  set -eu")
        print("  cleanup() {")
        print("    result=$?")
        print("    trap - EXIT HUP INT TERM")
        print("    unset " + " ".join(environment))
        for command in ("python scripts/stackctl.py down --force --front-door",
                        "python scripts/stackctl.py down --force", sidecar_command("QUIT", "BYE"),
                        "python scripts/stack_claim.py release", "python scripts/stack_claim.py status"):
            print("    " + command + " || result=1")
        print('    exit "$result"')
        print("  }")
        print("  trap cleanup EXIT")
        print("  trap 'exit 130' INT")
        print("  trap 'exit 143' HUP TERM")
        print("  export " + " ".join(f"{name}={value}" for name, value in environment.items()))
        if args.phase in ("both", "line"):
            print(f"  test -f {quote(binary)}")
        indent = "  "

    def command(text):
        print(indent + text)

    command("python scripts/stackctl.py down --force")
    backup = quote(".omo/run/saves/" + args.base)
    command(f"if (!(Test-Path -LiteralPath {backup})) {{" if powershell else f"if [ ! -d {backup} ]; then")
    command(f"  python scripts/saves.py backup {base}")
    command("}" if powershell else "fi")
    command(sidecar_command("PING", "PONG"))
    if args.phase in ("both", "json"):
        command(f"python scripts/saves.py restore {base}")
        command(f"python scripts/saves.py verify {base}")
        command(f"python scripts/stackctl.py boot-server --save {boot}")
        command(f"python scripts/card57_wire_diff.py record --wire json {common} --out {quote(root + '/json_run.jsonl')}")
        command("python scripts/stackctl.py down --force")
    if args.phase in ("both", "line"):
        command(f"python scripts/saves.py restore {base}")
        command(f"python scripts/saves.py verify {base}")
        command("# Cold-load through C#, then retire that controller before attaching Rust to the same TS3W.")
        command(f"python scripts/stackctl.py boot-server --save {boot}")
        command("python scripts/stackctl.py stop-controller")
        command(f"python scripts/stackctl.py boot-server --front-door --front-door-bin {quote(binary)} --save {boot}")
        command(f"python scripts/card57_wire_diff.py record --wire line {common} --out {quote(root + '/line_run.jsonl')}")
        probe_actor = " --actor " + args.sim if args.sim and args.sim.isdigit() else ""
        command(f"python scripts/rejection_relay_probe.py --wire line --endpoint {endpoint} --player {args.player + 1}"
                f"{probe_actor} --out {quote(root + '/rejection_relay.txt')}")
        command("python scripts/stackctl.py down --force --front-door")
        command(f"python scripts/card57_wire_diff.py compare {quote(root + '/json_run.jsonl')} "
                f"{quote(root + '/line_run.jsonl')} --out {quote(root + '/wire_diff.jsonl')}")
        command(f"python scripts/card57_live_gates.py check {quote(root)}")
    if powershell:
        print("  } finally {")
        print("    Remove-Item " + ",".join("Env:" + name for name in environment) + " -ErrorAction SilentlyContinue")
        print("    try { python scripts/stackctl.py down --force --front-door } finally {")
        print("      try { python scripts/stackctl.py down --force } finally {")
        print("        try { " + sidecar_command("QUIT", "BYE") + " } finally {")
        print("          try { python scripts/stack_claim.py release } finally { python scripts/stack_claim.py status }")
        print("        }")
        print("      }")
        print("    }")
        print("  }")
        print("}")
    else:
        print(")")
    print("# Require final claim status=free. This plan is not live-readiness evidence.")
    return 0


def check(run_dir):
    errors = []
    try:
        rows = [json.loads(line) for line in (run_dir / "wire_diff.jsonl").read_text(encoding="utf-8").splitlines()]
        if any(not isinstance(row, dict) for row in rows):
            raise ValueError("wire diff records must be objects")
        summary = rows[-1]
        steps = rows[:-1]
        if not steps or summary.get("summary") is not True or summary.get("steps") != len(steps):
            raise ValueError("wire diff needs step rows and a final counted summary")
        if any(row.get("summary") or row.get("status") not in ("match", "mismatch", "silent_drop") for row in steps):
            raise ValueError("invalid wire diff step")
        if [row["step"] for row in steps] != list(COMPARISON_STEPS):
            raise ValueError("wire diff needs eight ordered scripted steps and the run-wide broadcasts check")
        if summary.get("mismatches") != 0 or summary.get("silent_drops") != 0:
            raise ValueError("wire diff reports mismatches or silent drops")
        if any(row["status"] != "match" or row.get("diff") or row.get("invalid")
               or row.get("silent_drop") for row in steps):
            raise ValueError("wire diff step contradicts passing summary")
    except (OSError, ValueError, KeyError, IndexError, TypeError) as exc:
        errors.append("gate 5: " + str(exc))
    try:
        lines = (run_dir / "rejection_relay.txt").read_text(encoding="utf-8").splitlines()
        counts = [line for line in lines if line.startswith("silent_drops=")]
        verdicts = [line for line in lines if line.startswith("verdict=")]
        if counts != ["silent_drops=0"] or verdicts != ["verdict=PASS"]:
            raise ValueError("rejection probe needs exactly silent_drops=0 and verdict=PASS")
        if any("=FAIL" in line for line in lines):
            raise ValueError("rejection probe contains a failing case")
    except (OSError, ValueError) as exc:
        errors.append("gate 6: " + str(exc))
    print(("FAIL: " + "; ".join(errors)) if errors else "PASS: Card 57 gates 5 and 6 artifacts")
    return int(bool(errors))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="action", required=True)
    p = sub.add_parser("plan", help="print commands only; never claims or calls the game")
    p.add_argument("--run-dir", type=Path, default=Path(".omo/run/card57-wire"))
    p.add_argument("--base", default="card57-cold-base", help="one-time immutable saves.py backup name")
    p.add_argument("--phase", choices=("both", "json", "line"), default="both")
    p.add_argument("--target-dir", type=Path, default=Path("E:/sims-online-target/card57-headless"))
    p.add_argument("--sidecar-bin", default="simlink_authority_daemon.exe" if os.name == "nt"
                   else "simlink_authority_daemon", help="verified prebuilt authority daemon, run in a second terminal")
    p.add_argument("--endpoint", default="127.0.0.1:34019")
    p.add_argument("--player", type=int, default=77)
    p.add_argument("--sim")
    p.add_argument("--boot-save", default="Option4BookChain.sims3")
    p.add_argument("--save-name", default="card57-wire-scratch.sims3")
    p.add_argument("--shell", choices=("posix", "powershell"), default="powershell" if os.name == "nt" else "posix")
    p = sub.add_parser("check")
    p.add_argument("run_dir", type=Path)
    args = parser.parse_args(argv)
    return plan(args) if args.action == "plan" else check(args.run_dir)


if __name__ == "__main__":
    raise SystemExit(main())
