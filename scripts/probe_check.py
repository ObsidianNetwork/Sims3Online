"""Compile every probe template in scripts/ offline, so a template error shows up here and not in a claimed live window.

    python scripts/probe_check.py                         # every template: fill with samples, compile, a line each
    python scripts/probe_check.py sim_act client_pick     # only these modules' templates
    python scripts/probe_check.py tools/probes/SocialSessionProbe.cs   # a probe file as it is
    python scripts/probe_check.py --render-only           # fill and check the placeholders only (no csc needed)
    python scripts/probe_check.py --list                  # the templates found, with their placeholders

Tooling review 2026-10-21 #13: rendered probes failed to compile only once live (G: `ShelfRoomProbe.cs(23,35): error
CS0103`; Main: `OwnQueueProbe.cs(17,17): error CS0246`), and Main wrote a compile_probes.py by hand in a scratchpad.

A template is a module-level *TEMPLATE string in scripts/*.py holding C# with __NAME__ placeholders (sim_act,
client_pick, object_diff, persist_cycle, ts3_view and others), read from the source without importing the module, so
a new one is checked without being registered. Each placeholder gets a sample by its context: inside a string literal
a name, before a numeric suffix (__RADIUS__f, __ID__uL) a number, as a bool, int or float constant false, 0 or 1;
SAMPLES gives the few with no such context (a Vector3, an expression). The compile goes through bridgectl.compile_probe
(make-probe.ps1: csc 2.0 against refs/sims3, as a live probe builds) and the DLL is deleted after. An error line names
the module line it came from. A compile is a build: another session's quiet window refuses it (scripts/stack_claim.py;
--force overrides). Exit 0 when everything compiled, 1 when one failed or a placeholder has no sample, 3 in a quiet
window. The mod_edit_check hook runs the same checks on an edited module: every template filled, a changed one
compiled. Tests: python -m unittest scripts.probe_check_test
"""
from __future__ import annotations

import argparse
import ast
import contextlib
import hashlib
import io
import os
import re
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SCRIPTS = ROOT / "scripts"
OUT = ROOT / ".omo" / "run" / "probe-check"
TEMPLATE_NAME = re.compile(r"^[A-Z_]*TEMPLATE[A-Z_]*$")
PLACEHOLDER = re.compile(r"__([A-Z][A-Z0-9_]*)__")
CSC_ERROR = re.compile(r"\((\d+),(\d+)\): (error CS\d+: .*)$")
SAMPLES = {  # (module.TEMPLATE, placeholder) -> C#, where the context names no type
    ("ts3_view.FOCUS_TEMPLATE", "AT"): "found.Position",
    ("ts3_view.FOCUS_TEMPLATE", "FOCUSED"): "found.FullName.Replace(' ', '_')",
    ("ts3_view.CAMERA_TEMPLATE", "NAMES"): '"Jennifer", "Pablo"',
    ("route_points.TEMPLATE", "SIM"): '"Jennifer"',
    ("route_points.TEMPLATE", "POINTS"): "new Vector3(0f, 0f, 0f)",
}


class Template:
    def __init__(self, module: str, name: str, source: str, line: int) -> None:
        self.module, self.name, self.source, self.line = module, name, source, line

    @property
    def label(self) -> str:
        return f"{self.module}.{self.name}"


def templates(modules: list[str] | None = None, folder: Path = SCRIPTS) -> list[Template]:
    """Every module-level *TEMPLATE string holding a C# class in folder's *.py (tests left out), by source, not import."""
    found = []
    for path in sorted(folder.glob("*.py")):
        if path.stem.endswith("_test") or (modules and path.stem not in modules):
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        if "TEMPLATE" not in text:
            continue
        for node in ast.parse(text).body:
            if (isinstance(node, ast.Assign) and len(node.targets) == 1 and isinstance(node.targets[0], ast.Name)
                    and TEMPLATE_NAME.match(node.targets[0].id) and isinstance(node.value, ast.Constant)
                    and isinstance(node.value.value, str) and "class " in node.value.value):
                found.append(Template(path.stem, node.targets[0].id, node.value.value, node.value.lineno))
    return found


def sample(label: str, name: str, before: str, after: str) -> str | None:
    """A C# value for one placeholder from the text around it, or None when the context names no type."""
    if (label, name) in SAMPLES:
        return SAMPLES[(label, name)]
    line = before.rsplit("\n", 1)[-1]
    if len(re.findall(r'(?<!\\)"', line)) % 2 or "//" in line.split('"', 1)[0]:
        return "Jennifer"  # inside a string literal ("err=no Sim named __SIM__") or a comment
    if re.match(r"[fFuUlLdDmM]+\b", after):
        return "1"
    if re.search(r"\bstring\[\]\s+\w+\s*=\s*$", before[-80:]):
        return 'new string[] { "Jennifer" }'  # an array placeholder (__NAMES__)
    declared = re.search(r"\b(bool|int|long|uint|ulong|float|double)\s+\w+\s*=\s*$", before[-80:])
    if declared:
        return {"bool": "false", "float": "1f", "double": "1.0"}.get(declared[1], "0")
    return None


def fill(template: Template) -> tuple[str, list[str]]:
    """The template with every placeholder filled, and the placeholders that got no sample."""
    text, missing = template.source, []
    for m in reversed(list(PLACEHOLDER.finditer(template.source))):
        value = sample(template.label, m[1], template.source[:m.start()], template.source[m.end():m.end() + 4])
        if value is None:
            missing.append(m[1])
        else:
            text = text[:m.start()] + value + text[m.end():]
    return text, sorted(set(missing))


def compile_source(path: Path) -> tuple[bool, list[tuple[int, int, str]], str]:
    """(compiled, csc errors as (line, column, text), other failure text) through bridgectl.compile_probe."""
    sys.path.insert(0, str(SCRIPTS))
    import bridgectl
    err = io.StringIO()
    stamp = "chk" + hashlib.sha1(path.read_bytes()).hexdigest()[:8]
    try:
        with contextlib.redirect_stderr(err):
            dll = bridgectl.compile_probe(str(path), stamp)
    except SystemExit as stop:
        errors = [(int(m[1]), int(m[2]), m[3].strip()) for line in err.getvalue().splitlines()
                  if (m := CSC_ERROR.search(line))]
        return False, errors, "" if errors else (str(stop.code or "") + " " + err.getvalue().strip()[-300:]).strip()
    Path(dll).unlink(missing_ok=True)
    return True, [], ""


def check_template(template: Template, render_only: bool = False, out: Path = OUT) -> tuple[bool, str]:
    text, missing = fill(template)
    if missing:
        return False, "no sample for " + ", ".join(f"__{m}__" for m in missing) + " (add one to probe_check.SAMPLES)"
    if render_only:
        return True, "filled"
    out.mkdir(parents=True, exist_ok=True)
    path = out / f"{template.module}_{template.name}.cs"
    path.write_text(text, encoding="utf-8")
    ok, errors, other = compile_source(path)
    if ok:
        return True, "compiled"
    if errors:
        return False, "; ".join(f"{template.module}.py:{template.line + line - 1} (probe {line},{col}): {text}"
                                for line, col, text in errors[:3])
    return False, other or "compile failed"


def check_file(path: Path) -> tuple[bool, str]:
    ok, errors, other = compile_source(path)
    if ok:
        return True, "compiled"
    return False, "; ".join(f"{path.name}({line},{col}): {text}" for line, col, text in errors[:3]) or other


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("only", nargs="*", metavar="module|file.cs", help="these modules' templates, or probe files")
    p.add_argument("--render-only", action="store_true", help="fill and check placeholders; no compile")
    p.add_argument("--list", action="store_true", help="the templates found and their placeholders")
    p.add_argument("--force", action="store_true", help="compile even in another session's quiet window")
    a = p.parse_args(argv)
    files = [Path(x) for x in a.only if x.endswith(".cs")]
    modules = [x for x in a.only if not x.endswith(".cs")]
    found = templates(modules or None) if modules or not files else []
    if a.list:
        for template in found:
            names = sorted(set(PLACEHOLDER.findall(template.source)))
            print(f"{template.label} ({template.module}.py:{template.line}): " + " ".join(f"__{n}__" for n in names))
        return 0
    if not a.render_only:
        sys.path.insert(0, str(SCRIPTS))
        import stack_claim
        if stack_claim.refuse_build(a.force):
            return 3
        if a.force:
            os.environ[stack_claim.QUIET_OK] = "1"  # compile_probe checks the quiet window too
    failed = 0
    for label, run in [(t.label, lambda t=t: check_template(t, a.render_only)) for t in found] + \
            [(str(f), lambda f=f: check_file(f)) for f in files if not a.render_only]:
        began = time.time()
        ok, detail = run()
        failed += not ok
        print(f"{'ok  ' if ok else 'FAIL'} {label}: {detail} ({time.time() - began:.1f} s)", flush=True)
    total = len(found) + (0 if a.render_only else len(files))
    print(f"probe check: {total - failed} of {total} ok" + (" (rendered only)" if a.render_only else ""))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
