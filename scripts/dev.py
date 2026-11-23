"""Developer front door: check your setup, lint, and run the tests CI runs, with one command each.

    python scripts/dev.py doctor          what is installed, and which tiers of work it allows
    python scripts/dev.py lint            ruff on scripts/ and tools/; PowerShell parse check if pwsh exists
    python scripts/dev.py test            the Python tests CI runs, script self-tests and a --help check of every CLI
    python scripts/dev.py test --dotnet   ...plus the .NET build and test suite
    python scripts/dev.py test --all      every Python test, including those that need local evidence or tools
    python scripts/dev.py check           lint, then test: run this before you push

The .NET tests need only the .NET SDK; Python and PowerShell checks are optional development tools.
Set up the Python tools with `python -m pip install -r requirements-dev.txt`.

This file uses only the standard library, so `doctor` works before anything is installed. CI's python-tools job runs
`lint` and `test`, so the exclusions below are the single list of what CI skips, each with its reason.
"""

from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import cpu_budget  # noqa: E402  (below-normal priority and job caps while a live stack claim is held)

ROOT = Path(__file__).resolve().parent.parent
UNITTEST_MODULE_TIMEOUT = 300  # seconds per test module in the no-pytest fallback
LINT_PATHS = ("scripts", "tools")

# Python test modules CI does not run, and why. Remove an entry when its reason goes away.
EXCLUDED_TESTS: dict[str, str] = {}
# Single tests skipped only inside a virtual environment.
VENV_DESELECTED = {
    "::test_recorder_uses_physical_cwd_and_parses_unittest_totals": (
        "re-runs python3 through a verified file descriptor, which loses a virtual environment's packages"
    ),
}
# Modules with a __main__ block that are not command-line tools, so the --help check skips them.
NOT_CLIS: set[str] = set()

DOTNET_PROJECTS = (
    "SimsOnline.Client/SimsOnline.Client.csproj",
    "SimsOnline.TestClient/SimsOnline.TestClient.csproj",
    "DedicatedServer.Tests/SimsOnline.DedicatedServer.Tests.csproj",
)
PS_PARSE = (
    "$bad = 0; foreach ($f in $input) { $t = $null; $e = $null; "
    "[void][System.Management.Automation.Language.Parser]::ParseFile($f, [ref]$t, [ref]$e); "
    "if ($e.Count) { $bad++; $e | ForEach-Object { Write-Output (\"{0}:{1}: {2}\" -f $f, "
    "$_.Extent.StartLineNumber, $_.Message) } } }; exit [int]($bad -gt 0)"
)


def run(step: str, argv: list[str], shown: str | None = None, **kwargs) -> bool:
    print(f"\n== {step}: {shown or ' '.join(argv)}", flush=True)
    started = time.monotonic()
    argv, kwargs = cpu_budget.apply(argv, kwargs)
    code = subprocess.run(argv, cwd=ROOT, check=False, **kwargs).returncode
    print(f"== {step}: {'ok' if code == 0 else f'FAILED (exit {code})'} in {time.monotonic() - started:.1f}s",
          flush=True)
    return code == 0


def tracked(pattern: str) -> list[str]:
    out = subprocess.run(["git", "ls-files", "-z", pattern], cwd=ROOT, capture_output=True, text=True, check=True)
    return [path for path in out.stdout.split("\0") if path and (ROOT / path).is_file()]  # skips unstaged deletions


def in_venv() -> bool:
    return sys.prefix != sys.base_prefix


# ------------------------------------------------------------------ doctor

def version_of(argv: list[str]) -> str | None:
    if not shutil.which(argv[0]):
        return None
    try:
        out = subprocess.run(argv, capture_output=True, text=True, timeout=30, check=False)
    except (OSError, subprocess.TimeoutExpired):
        return None
    text = (out.stdout or out.stderr).strip()
    return text.splitlines()[0] if out.returncode == 0 and text else None


def module_present(name: str) -> bool:
    import importlib.util

    return importlib.util.find_spec(name) is not None


def find_csc20() -> str | None:
    windir = os.environ.get("WINDIR")
    candidates = [Path(windir) / "Microsoft.NET/Framework/v2.0.50727/csc.exe"] if windir else []
    for prefix in (os.environ.get("WINEPREFIX"), str(Path.home() / ".sims3"), str(Path.home() / ".wine")):
        if prefix:
            candidates.append(Path(prefix) / "drive_c/windows/Microsoft.NET/Framework/v2.0.50727/csc.exe")
    return next((str(path) for path in candidates if path.is_file()), None)


def doctor(_: argparse.Namespace) -> int:
    rows: list[tuple[str, str, str | None, str, bool]] = []  # tier, item, what was found, hint, optional

    def check(tier: str, item: str, found: str | None, hint: str, optional: bool = False) -> None:
        rows.append((tier, item, found, hint, optional))

    sdks = subprocess.run(["dotnet", "--list-sdks"], capture_output=True, text=True,
                          check=False).stdout if shutil.which("dotnet") else ""
    check("T0", ".NET 8 SDK", next((line.split()[0] for line in sdks.splitlines() if line.startswith("8.")), None),
          "install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0")

    py = sys.version_info
    check("T1", "Python 3.12+", f"{py.major}.{py.minor}.{py.micro}" if py >= (3, 12) else None,
          f"this is Python {py.major}.{py.minor}; the tooling needs 3.12 or later")
    missing = [name for name in ("pefile", "jsonschema", "capstone", "PIL", "pytest", "ruff") if not module_present(name)]
    check("T1", "requirements-dev.txt", None if missing else "installed",
          f"missing {', '.join(missing)}: python -m pip install -r requirements-dev.txt")
    check("T1", "PowerShell 7 (pwsh)",
          version_of(["pwsh", "-NoProfile", "-Command", "$PSVersionTable.PSVersion.ToString()"]),
          "install PowerShell 7; the mod build, the compile gate and the pre-commit hook use it")
    check("T1", "Node.js", version_of(["node", "--version"]), "runs scripts/rea_native_corpus_test.mjs", optional=True)

    sys.path.insert(0, str(ROOT / "scripts"))
    try:
        import ts3_paths

        ts3 = ts3_paths.find_ts3w()
    except Exception:  # noqa: BLE001 - doctor reports, never crashes
        ts3 = None
    check("T2", "TS3W.exe", str(ts3) if ts3 else None, "set SIMSONLINE_TS3_PATH to your Game/Bin/TS3W.exe")
    check("T2", ".NET Framework 2.0 csc", find_csc20(),
          "Windows: turn on .NET Framework 3.5; Linux: a Wine prefix ships it (set WINEPREFIX)")
    if sys.platform.startswith("linux"):
        check("T2", "Wine", version_of(["wine", "--version"]), "install Wine to run TS3 on Linux")
    reference_names = (
        "scripts_00.dll", "scripts_01.dll", "scripts_02.dll",
        "gameplay_00.dll", "gameplay_01.dll", "gameplay_02.dll", "gameplay_03.dll",
    )
    reference_dir = ROOT / "refs" / "sims3"
    missing_references = [name for name in reference_names if not (reference_dir / name).is_file()]
    check("T2", "reference assemblies", None if missing_references else "7 local files",
          "supply legally usable local references; see refs/sims3/README.md", optional=True)
    check("T2", "your game assemblies", "_analysis/sims3_assemblies_fixed"
          if (ROOT / "_analysis/sims3_assemblies_fixed").is_dir() else None,
          "for the fork and decompile tools: see refs/sims3/README.md", optional=True)

    width = max(len(item) for _, item, _, _, _ in rows)
    for tier, item, found, hint, optional in rows:
        mark = "ok" if found else ("  " if optional else "--")
        print(f"[{mark}] {tier}  {item:<{width}}  {found or hint}{'  (optional)' if optional and not found else ''}")
    ready = [tier for tier in ("T0", "T1", "T2")
             if all(found for t, _, found, _, optional in rows if t == tier and not optional)]
    print(f"\nReady for: {', '.join(ready) or 'nothing yet'}")
    return 0


# ------------------------------------------------------------------ lint

def lint(_: argparse.Namespace) -> int:
    ok = run("ruff", [sys.executable, "-m", "ruff", "check", *LINT_PATHS])
    if shutil.which("pwsh"):
        scripts = "\n".join(tracked("*.ps1"))
        ok &= run("powershell parse", ["pwsh", "-NoProfile", "-NonInteractive", "-Command", PS_PARSE],
                  shown=f"pwsh parser over {scripts.count(chr(10)) + 1} tracked .ps1 files", input=scripts, text=True)
    else:
        print("\n== powershell parse: skipped (pwsh not found)")
    return 0 if ok else 1


# ------------------------------------------------------------------ test

def help_check() -> bool:
    print("\n== cli --help: every command-line script must answer --help without a game", flush=True)
    failed = []
    for script in tracked("scripts/*.py"):
        if script.endswith("_test.py") or Path(script).name.startswith("_") or script in NOT_CLIS:
            continue
        if "__main__" not in (ROOT / script).read_text(encoding="utf-8", errors="replace"):
            continue
        try:
            result = subprocess.run([sys.executable, script, "--help"], cwd=ROOT, capture_output=True, text=True,
                                    timeout=60, check=False, stdin=subprocess.DEVNULL)
            code, tail = result.returncode, (result.stderr or result.stdout).strip().splitlines()[-1:]
        except subprocess.TimeoutExpired:
            code, tail = "timeout", []
        if code != 0:
            failed.append(script)
            print(f"   FAIL {script} (exit {code}): {tail[0] if tail else ''}")
    print(f"== cli --help: {'ok' if not failed else f'FAILED ({len(failed)})'}", flush=True)
    return not failed


def pytest_pythonpath() -> str:
    """pyproject.toml's [tool.pytest.ini_options] pythonpath as a PYTHONPATH value, for runs without pytest."""
    import tomllib
    entries = tomllib.loads((ROOT / "pyproject.toml").read_text(encoding="utf-8"))["tool"]["pytest"]["ini_options"]["pythonpath"]
    return os.pathsep.join([*(str(ROOT / entry) for entry in entries), os.environ.get("PYTHONPATH", "")]).rstrip(os.pathsep)


def unittest_fallback(include_all: bool) -> tuple[bool, list[str]]:
    """No pytest: run every test module that does not need it under unittest; return (passed, modules not run)."""
    modules, needs_pytest = [], []
    for path in tracked("scripts/*_test.py"):
        if not include_all and path in EXCLUDED_TESTS:  # test() already printed the skip line
            continue
        text = (ROOT / path).read_text(encoding="utf-8", errors="replace")
        if re.search(r"^\s*(import|from)\s+pytest\b", text, re.MULTILINE):
            needs_pytest.append(path)
        else:
            modules.append(path[:-3].replace("/", "."))
    print(f"\n== python tests (unittest fallback): pytest is not installed (python -m pip install -r requirements-dev.txt)\n"
          f"   {len(modules)} modules to run under unittest, {len(needs_pytest)} need pytest and will not run", flush=True)
    started = time.monotonic()
    # One bounded subprocess per module: a module that cannot load here (POSIX-only code on Windows) is listed as
    # not run instead of aborting the rest, and one that hangs is killed and fails.
    env = {**os.environ, "PYTHONPATH": pytest_pythonpath()}

    def one(name: str) -> tuple[str, str, int, int]:
        try:
            done = subprocess.run([sys.executable, "-m", "unittest", name], cwd=ROOT, capture_output=True, text=True, env=env,
                                  stdin=subprocess.DEVNULL, timeout=UNITTEST_MODULE_TIMEOUT, check=False)
        except subprocess.TimeoutExpired:
            return name, "timeout", 0, 1
        ran = re.search(r"^Ran (\d+) tests?", done.stderr, re.MULTILINE)
        if ran is None:
            return name, done.stderr.strip().splitlines()[-1:] and done.stderr.strip().splitlines()[-1] or "no output", 0, -1
        return name, done.stderr if done.returncode else "", int(ran.group(1)), done.returncode

    ran_total, failed = 0, []
    with ThreadPoolExecutor(max_workers=8) as pool:
        for name, text, count, code in pool.map(one, modules):
            ran_total += count
            if code == -1:
                needs_pytest.append(f"{name.replace('.', '/')}.py (could not load: {text})")
            elif count == 0:
                needs_pytest.append(f"{name.replace('.', '/')}.py (no unittest tests: pytest-style functions)")
            elif code != 0:
                failed.append(name)
                print(f"--- FAILED {name} ({text if text == 'timeout' else 'see below'})\n{text if text != 'timeout' else ''}")
    ok = not failed
    print(f"== python tests (unittest fallback): {ran_total} tests in {len(modules) - sum('(could not load' in item or '(no unittest' in item for item in needs_pytest)} modules, "
          f"{'ok' if ok else f'FAILED ({len(failed)} modules: ' + ', '.join(failed) + ')'} in "
          f"{time.monotonic() - started:.1f}s", flush=True)
    return ok, needs_pytest


def test(args: argparse.Namespace) -> int:
    pytest_argv = [sys.executable, "-m", "pytest", "-q"]
    if not args.all:
        for path, reason in EXCLUDED_TESTS.items():
            print(f"skip {path}: {reason}")
            pytest_argv += ["--ignore", path]
        if in_venv():
            for node, reason in VENV_DESELECTED.items():
                print(f"skip {node} (in a virtual environment): {reason}")
                pytest_argv += ["--deselect", node]
    incomplete: list[str] = []
    if module_present("pytest"):
        ok = run("python tests", pytest_argv + list(args.pytest_args))
    else:
        ok, incomplete = unittest_fallback(args.all)
    ok &= run("stackctl self-test", [sys.executable, "scripts/stackctl.py", "--test"])
    ok &= run("latency report self-test", [sys.executable, "scripts/transport_latency_report.py", "--self-test"])
    ok &= help_check()
    sys.path.insert(0, str(ROOT / "scripts"))
    import stack_claim
    if args.dotnet and stack_claim.refuse_build(args.force):
        print("\n== .NET build and tests: skipped (another session's quiet window; --force to run them)")
        ok = False
    elif args.dotnet:
        for project in DOTNET_PROJECTS:
            ok &= run(f"build {Path(project).stem}", ["dotnet", "build", project, "-nologo", "-v", "q"])
        # Wave 7: DedicatedServer.Tests/ holds two csproj files (this reflection runner and the
        # NUnit VSTest one the grade-wave7 harness drives), so the project file must be spelled
        # out -- `dotnet run --project <dir>` is ambiguous there.
        ok &= run(".NET tests", ["dotnet", "run", "--no-build", "--project",
                                 "DedicatedServer.Tests/SimsOnline.DedicatedServer.Tests.csproj",
                                 "--", "--filter", "all"])
    if incomplete:
        print(f"\nINCOMPLETE: {len(incomplete)} test module(s) need pytest and were NOT run: {', '.join(incomplete)}")
        if not ok:
            print("SOME STEPS FAILED")
        return 1
    print(f"\n{'ALL PASSED' if ok else 'SOME STEPS FAILED'}")
    return 0 if ok else 1


def check(args: argparse.Namespace) -> int:
    lint_code = lint(args)
    test_code = test(args)
    return lint_code or test_code


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("doctor", help="what is installed, and which tiers it allows").set_defaults(func=doctor)
    commands.add_parser("lint", help="ruff and the PowerShell parse check").set_defaults(func=lint)
    for name, func, text in (("test", test, "the tests CI runs"), ("check", check, "lint, then test")):
        sub = commands.add_parser(name, help=text)
        sub.add_argument("--dotnet", action="store_true", help="also build and run the .NET test suite")
        sub.add_argument("--force", action="store_true", help="run --dotnet even in another session's quiet window")
        sub.add_argument("--all", action="store_true", help="include the Python tests CI skips")
        sub.add_argument("pytest_args", nargs="*", help="extra pytest arguments, after --")
        sub.set_defaults(func=func)
    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
