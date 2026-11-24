"""Check the mod against the game's own runtime: catch an API that exists in desktop .NET 2 but not inside TS3.

    python scripts/check_mod_runtime.py           # shell (SimsOnlineServerHostMod/*.cs) and module (Presentation/*.cs)

The mod compiles against the desktop .NET 2 mscorlib, but TS3 runs its own, stripped one (simcore_00..02: no
System.IO.File, FileStream, Directory, ...). A call into a missing API compiles, then fails in the game when the method
holding it is first run (Mono resolves the whole method): every SACS_RECORDER command hung on both TS3s because
presentation-load called File.ReadAllBytes (2026-10-14). This compiles the same sources with /nostdlib against the
game's assemblies, so such a call fails here instead. A few old methods touch files on purpose (they run, or fail
harmlessly, outside the game); they are listed in KNOWN with why. quick_loop run --mod runs this before installing.
"""
from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MOD = ROOT / "SimsOnlineServerHostMod"
RUNTIME = ["simcore_00", "simcore_01", "simcore_02"]  # the game's mscorlib, System, System.Xml
ENGINE = ["scripts_00", "scripts_01", "scripts_02", "gameplay_00", "gameplay_01", "gameplay_02", "gameplay_03"]

# (file, method): file access that only works, or fails harmlessly, outside the game.
KNOWN: dict[tuple[str, str], str] = {}
ERROR = re.compile(r"^(?P<file>.+?)\((?P<line>\d+),\d+\): error (?P<code>CS\d+): (?P<text>.*)$")
METHOD = re.compile(r"^\s+(?:(?:public|private|internal|protected|static|override|virtual|sealed|unsafe)\s+)+[\w<>\[\],. ]+\s+(\w+)\s*\(")


def assemblies() -> Path:
    # This checkout, a sibling SimsOnline checkout, or (an app worktree under SimsOnline/.claude/worktrees/<name>) the
    # checkout the worktree lives in.
    # SIMSONLINE_GAME_ASSEMBLIES names the folder outright (any checkout that has one, read-only).
    override = os.environ.get("SIMSONLINE_GAME_ASSEMBLIES")
    candidates = [Path(override)] if override else []
    candidates += [ROOT / "_analysis" / "sims3_assemblies_fixed", ROOT.parent / "SimsOnline" / "_analysis" / "sims3_assemblies_fixed"]
    candidates += [parent / "_analysis" / "sims3_assemblies_fixed" for parent in ROOT.parents if parent.name == "SimsOnline"]
    for candidate in candidates:
        if candidate.exists():
            return candidate
    raise SystemExit("no _analysis/sims3_assemblies_fixed (the game's assemblies)")


def csc() -> Path:
    windir = os.environ.get("WINDIR")
    path = Path(windir) / "Microsoft.NET" / "Framework" / "v2.0.50727" / "csc.exe" if windir else None
    if not path or not path.exists():
        raise SystemExit("no .NET 2 csc (Windows only)")
    return path


def runtime_args(out: Path, extra_refs: list[Path] | None = None) -> list[str]:
    """csc arguments that compile against the game's own runtime (also used by presentation_reload.py)."""
    asm = assemblies()
    refs = [asm / (name + ".dll") for name in RUNTIME + ENGINE] + list(extra_refs or [])
    return [str(csc()), "/nostdlib+", "/noconfig", "/target:library", "/platform:x86", "/nologo", f"/out:{out}"] + [f"/reference:{r}" for r in refs]


def enclosing_method(path: Path, line: int) -> str:
    lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
    for i in range(min(line, len(lines)) - 1, -1, -1):
        m = METHOD.match(lines[i])
        if m:
            return m.group(1)
    return "?"


def check(label: str, sources: list[Path], extra_refs: list[Path] | None = None, keep: bool = False) -> list[str]:
    """Compile sources against the game's runtime; the problems. keep: leave the output (shell_output(label))."""
    out = shell_output(label)
    r = subprocess.run(runtime_args(out, extra_refs) + [str(s) for s in sources], capture_output=True, text=True, check=False)
    problems = []
    for raw in r.stdout.splitlines():
        m = ERROR.match(raw.strip())
        if not m:
            continue
        path = Path(m.group("file"))
        method = enclosing_method(path, int(m.group("line")))
        if (path.name, method) in KNOWN:
            continue
        problems.append(f"{path.name}:{m.group('line')} in {method}: {m.group('code')} {m.group('text')}")
    if r.returncode != 0 and not problems and not any(ERROR.match(x.strip()) for x in r.stdout.splitlines()):
        problems.append("csc failed: " + (r.stdout + r.stderr).strip()[-400:])
    if not keep or r.returncode != 0:
        try:
            out.unlink()
        except OSError:
            pass
    return problems


def shell_output(label: str) -> Path:
    return Path(os.environ.get("TEMP", "."), f"check_mod_runtime_{label}.dll")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--force", action="store_true", help="compile even in another session's quiet window")
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    import stack_claim
    if stack_claim.refuse_build(parser.parse_args().force):
        return 3
    shell_sources = [p for p in MOD.glob("*.cs") if p.name != "Sims3PackageBuilder.cs"]
    handler_sources = sorted((MOD / "Handlers").glob("*.cs"))
    problems = check("shell", shell_sources, keep=True)
    problems += check("handlers", handler_sources, keep=True)
    # The module against the shell just compiled from these sources. bin/Release's DLL is the last make-mod's: after a
    # merge that adds shell API the module uses, it reported the new calls as missing (2026-10-21). If the shell has a
    # known System.IO use (KNOWN), that fails the runtime compile, so a reference DLL is compiled with a stub for it
    # (check_mod_runtime_io_stub.cs); only a shell that fails for another reason falls back to bin/Release.
    module_sources = sorted((MOD / "Presentation").glob("*.cs"))
    shell_dll = shell_output("shell")
    if not shell_dll.exists():
        stub = Path(__file__).with_name("check_mod_runtime_io_stub.cs")
        if stub.exists():
            check("shell", shell_sources + [stub], keep=True)
    if not shell_dll.exists():
        shell_dll = MOD / "bin" / "Release" / "SimsOnlineServerHostMod.dll"
        print("module checked against the last make-mod's shell DLL (the shell did not compile from these sources)")
    if shell_dll.exists():
        problems += check("module", module_sources, [shell_dll], keep=True)
    else:
        # No shell DLL even with the stub and no make-mod DLL either: the module is compiled together with the shell's
        # sources (their allow-listed System.IO errors filtered as above).
        problems += [p for p in check("module", shell_sources + module_sources) if p not in problems]

    import check_mod_mono_safety

    for h in check_mod_mono_safety.scan_mod_sources(MOD):
        problems.append("MONO SAFETY (source): " + h.format())
    for label in ("shell", "handlers", "module"):
        dll = shell_output(label)
        if dll.exists():
            for h in check_mod_mono_safety.scan_clr_hazards(pe_path=dll, label=label):
                problems.append("MONO SAFETY (CLR): " + h.format())
            try:
                dll.unlink()
            except OSError:
                pass
    for p in problems:
        print("NOT IN THE GAME'S RUNTIME: " + p)
    print(f"mod runtime check: {'ok' if not problems else str(len(problems)) + ' problem(s)'}")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
