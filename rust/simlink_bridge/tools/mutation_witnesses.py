"""Card 56 gate 2: every layout constant's compile-time witness is breakable.

    python rust/simlink_bridge/tools/mutation_witnesses.py --out <run dir>/mutation_witnesses.txt

Copies the crate into a scratch directory under its own target dir (never touching the committed file), checks
that the copy builds unmutated, then mutates each `pub const` of src/layout.rs in turn (a number gets `+ 1`, a
string gets one more character) and runs `cargo check --lib` on the copy. Each mutation must fail the build. The
output has one line per constant, `constant=<name>|build=fail` (or `build=pass`, which fails the gate), and the
first compiler error per constant goes to a `.log` file beside it. Exit 0 when every mutation failed the build.
"""

import argparse
import re
import shutil
import subprocess
import sys
from pathlib import Path

CRATE = Path(__file__).resolve().parent.parent
CONST = re.compile(r"^pub const (?P<name>[A-Z0-9_]+): (?P<ty>[^=]+) = (?P<value>.+);$")


def mutate(line: str) -> str | None:
    m = CONST.match(line)
    if not m:
        return None
    ty, value = m.group("ty").strip(), m.group("value")
    if ty == "&str":
        if not value.endswith('"'):
            return None
        new_value = value[:-1] + 'x"'
    else:
        new_value = f"({value}) + 1"
    return f"pub const {m.group('name')}: {ty} = {new_value};"


def cargo_check(copy: Path, target: Path) -> subprocess.CompletedProcess:
    return subprocess.run(
        ["cargo", "check", "--offline", "--lib", "--quiet", "--message-format", "short", "--target-dir", str(target)],
        cwd=copy, capture_output=True, text=True, encoding="utf-8", errors="replace",
    )


def first_error(stderr: str) -> str:
    for line in stderr.splitlines():
        if "error" in line:
            return line.strip()
    return stderr.strip().splitlines()[0] if stderr.strip() else ""


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--out", required=True, help="path of mutation_witnesses.txt")
    args = ap.parse_args()

    scratch = CRATE / "target" / "mutation_scratch"
    copy = scratch / "crate"
    target = scratch / "target"
    if copy.exists():
        shutil.rmtree(copy)
    copy.mkdir(parents=True)
    for name in ["Cargo.toml", "Cargo.lock"]:
        shutil.copy2(CRATE / name, copy / name)
    for name in ["src", "tools", "tests"]:
        shutil.copytree(CRATE / name, copy / name, ignore=shutil.ignore_patterns("*.py", "__pycache__"))

    layout = copy / "src" / "layout.rs"
    original = layout.read_text(encoding="utf-8")
    baseline = cargo_check(copy, target)
    if baseline.returncode != 0:
        print("the unmutated copy does not build:\n" + baseline.stderr, file=sys.stderr)
        return 2

    lines = original.split("\n")
    results: list[str] = []
    log: list[str] = []
    all_fail = True
    for i, line in enumerate(lines):
        mutated = mutate(line)
        if mutated is None:
            continue
        name = CONST.match(line).group("name")
        layout.write_text("\n".join(lines[:i] + [mutated] + lines[i + 1:]), encoding="utf-8", newline="\n")
        r = cargo_check(copy, target)
        build = "pass" if r.returncode == 0 else "fail"
        all_fail &= build == "fail"
        results.append(f"constant={name}|build={build}")
        log.append(f"{name}\tlayout.rs:{i + 1}\t{mutated}\t{first_error(r.stderr)}")
        print(results[-1], flush=True)
    layout.write_text(original, encoding="utf-8", newline="\n")

    out = Path(args.out)
    out.write_text("\n".join(results) + "\n", encoding="utf-8", newline="\n")
    out.with_suffix(".log").write_text("\n".join(log) + "\n", encoding="utf-8", newline="\n")
    print(f"{len(results)} constants, every mutation fails the build = {all_fail}")
    return 0 if all_fail and results else 1


if __name__ == "__main__":
    sys.exit(main())
