#!/usr/bin/env python3
"""A CPU budget for builds and test runs while a live stack runs, so the games and the desktop keep their cores.

hitch_watch.py (internal documentation) measured 2026-11-10 what hitches Windows during live windows: several lanes build at
once (cargo/rustc, cl, link, dotnet) at Normal priority, the run queue reaches 3-5x the logical cores, and the games and
the desktop wait in it. A build gains nothing from outrunning a live run, so while a live stack claim is held
(a claim line with a time under `stack_claim.STALE_HOURS` old, or a file that is not a claim line, which stack_claim
also counts as held) a build runs:

  * at BELOW_NORMAL priority (children inherit it, so wrapping the top process covers cargo's rustc, MSBuild's nodes, ...),
  * with `CARGO_BUILD_JOBS` set to the budget (never over a value the caller already set), and
  * for `dotnet build|test|publish|pack`, with `-m:<budget>` unless the command already has `-m`/`--maxcpucount`.

The budget is `SIMSONLINE_CPU_JOBS`, else a quarter of the logical cores (4 on 16), at least 2. `SIMSONLINE_CPU_BUDGET=0`
turns it off (`run --off`); nothing turns it on without a claim, so an idle machine always builds at full speed. It
changes only the argv, environment and priority of the child a caller starts through apply(), at launch; it never
enumerates or touches another process, and it does not undo itself if the claim ends mid-build.

    python scripts/cpu_budget.py status
    python scripts/cpu_budget.py run [--off] [--jobs N] -- cargo test ...      # for a hand-typed build

Python callers: `argv, kwargs = cpu_budget.apply(argv, kwargs)` before `subprocess.run(argv, **kwargs)` (scripts/dev.py,
scripts/quick_loop.py and tools/omo/verify_tests.py do).
"""
from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path

BELOW_NORMAL_PRIORITY_CLASS = 0x00004000
DOTNET_VERBS = ("build", "test", "publish", "pack")
MAX_CPU_FLAGS = ("-m", "/m", "-maxcpucount", "--maxcpucount")


def jobs(environ=None) -> int:
    env = os.environ if environ is None else environ
    try:
        return max(1, int(env["SIMSONLINE_CPU_JOBS"]))
    except (KeyError, ValueError):
        return max(2, (os.cpu_count() or 8) // 4)


def claim_held(path=None, now=None) -> bool:
    """A live stack claim exists: the first line of the claim file parses and is under STALE_HOURS old (read from its
    timestamp, never from the purpose text), or is not a claim line at all (stack_claim counts that as held). Never
    raises: no answer is 'not held'."""
    try:
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        import stack_claim
        from datetime import datetime, timedelta
        first = (stack_claim.CLAIM if path is None else Path(path)).read_text(encoding="utf-8").strip().splitlines()[0]
        claim = stack_claim.parse(first)
        if claim is None:
            return True
        return (now or datetime.now()) - claim[1] <= timedelta(hours=stack_claim.STALE_HOURS)
    except Exception:  # noqa: BLE001 - a build must never fail on its budget check
        return False


def active(environ=None, held=None) -> bool:
    env = os.environ if environ is None else environ
    mode = (env.get("SIMSONLINE_CPU_BUDGET") or "").strip().lower()
    if mode in ("0", "off", "no", "false"):
        return False
    return os.name == "nt" and (claim_held() if held is None else held)


def _has_cpu_flag(argv: list[str]) -> bool:
    return any(a.lower() in MAX_CPU_FLAGS or a.lower().startswith(("-m:", "/m:", "--maxcpucount")) for a in argv)


def budget_argv(argv: list[str], n: int) -> list[str]:
    """argv with the build's own parallelism capped (dotnet only; cargo takes CARGO_BUILD_JOBS from the environment)."""
    name = Path(argv[0]).stem.lower() if argv else ""
    if name == "dotnet" and len(argv) > 1 and argv[1] in DOTNET_VERBS and not _has_cpu_flag(argv):
        return [*argv[:2], f"-m:{n}", *argv[2:]]
    return list(argv)


def apply(argv: list[str], kwargs: dict | None = None, environ=None, held=None) -> tuple[list[str], dict]:
    """(argv, subprocess kwargs) under the budget when it is active, else both unchanged. kwargs' env and creationflags are
    kept and extended; the caller's own CARGO_BUILD_JOBS wins."""
    kwargs = dict(kwargs or {})
    if not active(environ, held):
        return list(argv), kwargs
    n = jobs(environ)
    env = dict(kwargs.get("env") if kwargs.get("env") is not None else (os.environ if environ is None else environ))
    env.setdefault("CARGO_BUILD_JOBS", str(n))
    kwargs["env"] = env
    if os.name == "nt":
        kwargs["creationflags"] = kwargs.get("creationflags", 0) | BELOW_NORMAL_PRIORITY_CLASS
    return budget_argv(argv, n), kwargs


def main(argv: list[str] | None = None) -> int:
    argv = list(sys.argv[1:] if argv is None else argv)
    if argv[:1] in (["-h"], ["--help"], ["help"]):
        print(__doc__)
        return 0
    if argv[:1] == ["status"]:
        print(f"cpu budget: {'on' if active() else 'off'} (claim held: {claim_held()}, "
              f"SIMSONLINE_CPU_BUDGET={os.environ.get('SIMSONLINE_CPU_BUDGET', '')!r}), jobs {jobs()}")
        return 0
    if argv[:1] != ["run"] or "--" not in argv:
        print(__doc__)
        return 2
    options, command = argv[1:argv.index("--")], argv[argv.index("--") + 1:]
    env = dict(os.environ)
    if "--off" in options:
        env["SIMSONLINE_CPU_BUDGET"] = "0"
    if "--jobs" in options:
        env["SIMSONLINE_CPU_JOBS"] = options[options.index("--jobs") + 1]
    command, kwargs = apply(command, {"env": env}, environ=env)
    if kwargs["env"] is not env and kwargs.get("creationflags"):
        print(f"cpu_budget: below-normal priority, {jobs(env)} jobs", file=sys.stderr)
    return subprocess.run(command, check=False, **kwargs).returncode


if __name__ == "__main__":
    raise SystemExit(main())
