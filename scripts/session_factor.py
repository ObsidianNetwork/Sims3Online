"""An arms before hook that gives an arm one long-session factor: the session probe.

    python scripts/session_factor.py warm      # client 1 rejoins warm twice (quick_loop run --step rejoin:warm)
    python scripts/session_factor.py warm3     # three times
    python scripts/session_factor.py reload    # one presentation module load (presentation_reload.py)
    python scripts/session_factor.py base      # nothing: any other arm name

Four scenarios failed after a 2.5 h session (client 1 rejoined warm twice, a presentation module was hot-loaded) and
passed on a fresh boot (runs 20261021-024712 against 20261021-033640, 20261021-033942). The probe runs the same four,
and the two two-player scenarios whose per-client verdicts a degraded boot would break too, after each factor in
turn, in one process series, with scenarios.py health as the after hook, so each arms row keeps
what the games had (row hook: client1_warmRejoins, client1_hotLoads, client1_ageMin, boot_due):

    python scripts/quick_loop.py run --mod --boot --clients 2      # a fresh boot of the build under test
    python scripts/scenarios.py arms phone-seated phone-standing shower sleep-bed two-same-chair two-hud \\
        --arm base= --arm warm= --arm reload= --order "base warm reload reboot base reload warm" --clients 2 \\
        --before "scripts/session_factor.py {arm}" --after "scripts/scenarios.py health" --no-reboot

The second series swaps the factors, so a fail that follows one factor in both orders points at it. The arm name is
the factor: warm[N] (N warm rejoins, default 2: that session's), reload[N] (N module loads, default 1).
"""
from __future__ import annotations

import re
import subprocess
import sys
from collections.abc import Callable
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DEFAULTS = {"warm": 2, "reload": 1}  # the 024712 session's two warm rejoins and one hot module load
COMMANDS = {"warm": ["scripts/quick_loop.py", "run", "--step", "rejoin:warm"], "reload": ["scripts/presentation_reload.py"]}


def factor(arm: str) -> tuple[str, int] | None:
    """(factor, times) an arm name asks for (warm, warm3, reload, reload2), or None (base and any other name)."""
    m = re.fullmatch(r"(warm|reload)(\d*)", arm)
    return (m.group(1), int(m.group(2) or DEFAULTS[m.group(1)])) if m else None


def run(command: list[str]) -> tuple[int, str]:
    proc = subprocess.run([sys.executable, *command], cwd=ROOT, capture_output=True, text=True, timeout=600)
    out = ((proc.stdout or "") + (proc.stderr or "")).strip()
    return proc.returncode, out.splitlines()[-1] if out else ""


def main(argv: list[str] | None = None, runner: Callable[[list[str]], tuple[int, str]] = run) -> int:
    args = sys.argv[1:] if argv is None else argv
    if len(args) != 1:
        print((__doc__ or "").strip().splitlines()[0])
        return 2
    wanted = factor(args[0])
    if wanted is None:
        print(f"{args[0]}: no factor (factor=none)")
        return 0
    name, times = wanted
    for i in range(times):
        code, last = runner(COMMANDS[name])
        print(f"{name} {i + 1}/{times}: {last or 'no output'}", flush=True)
        if code:
            # the arm would run without its factor: the before hook's exit code and line say so in the arm's events
            print(f"{name} {i + 1}/{times} failed (exit {code}): factor_done={i}")
            return code
    print(f"factor={name} factor_done={times}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
