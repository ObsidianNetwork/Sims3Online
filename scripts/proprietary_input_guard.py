"""Public-mirror proprietary-input, privacy, and hygiene guard.

Standard-library-only checker used both by ``export_public_mirror.py --verify``
and by the public mirror CI workflow. Scans either changed files against a
``--base`` git ref or an entire exported repository tree.
"""
from __future__ import annotations

import argparse
from dataclasses import dataclass
import os
from pathlib import Path
import re
import subprocess
import sys

SCRIPTS_DIR = Path(__file__).resolve().parent
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))
import reference_assembly  # noqa: E402

BINARY_SUFFIXES = frozenset({
    ".dll", ".exe", ".package", ".nhd", ".world", ".sims3", ".dbc", ".ebc",
    ".gzf", ".gpr", ".pdb", ".dmp", ".mdmp", ".idb", ".i64", ".bndb", ".so",
    ".dylib", ".asi",
})

EXCLUDED_PREFIXES = (
    ".agents/",
    ".claude/",
    ".codex/",
    ".cursor/",
    ".gemini/",
    ".kiro/",
    ".local/",
    ".omo/",
    ".opencode/",
    ".qoder/",
    ".rtk/",
    ".serena/",
    ".trail/",
    "_analysis/",
    "coord/",
    "docs/" + "cor" + "tex/",
    "docs/" + "native-and-reverse-" + "engineering/",
    "evidence/",
    "libr" + "ary/",
    "openwiki/",
    "schemas/evidence/",
    "scratch/",
    "tools/" + "card57_codec_probe/",
    "tools/" + "col" + "ab_offload/",
    "tools/" + "decomp" + "ile_lane/",
    "tools/" + "ghid" + "ra/",
    "tools/" + "ide/",
    "tools/" + "managed_" + "decompile_metadata/",
    "tools/" + "managed_" + "recompile/",
    "tools/" + "mono" + "host/",
    "tools/" + "native_" + "observatory/",
    "tools/" + "probes/",
    "tools/" + "res" + "earch/",
    "tools/" + "simnav_route_probe/",
    "tools/" + "unr" + "aid_vm/",
)

EXCLUDED_EXACT_FILES = frozenset({
    ".github/public-mirror-manifest.json",
    ".github/workflows/deploy-docs.yml",
    ".github/workflows/sync-public-mirror.yml",
    "AGENTS.local.md",
    "AGENTS.md",
    "CLAUDE.local.md",
    "CLAUDE.md",
    "SimsOnlineServerHostMod/SimsOnlineServerHostMod.Ide.csproj",
    "codegraph.json",
    "docs/" + "guides/" + "dn" + "spy.md",
    "docs/" + "guides/" + "mono" + "patcher.md",
    "docs/" + "guides/" + "remote-test-" + "rig.md",
    "docs/" + "guides/" + "tool-reference-managed-" + "analysis.md",
    "docs/" + "guides/" + "tool-reference-native-" + "analysis.md",
    "docs/" + "protocol-reference/" + "schema-reference.md",
    "docs/" + "repo-reference/" + "native-" + "observatory-reference.md",
    "docs/" + "repo-reference/" + "schemas-catalog.md",
    "docs/" + "reverse-" + "engineering.md",
    "rust/simlink_bridge/" + "AUDIT_" + "UNSAFE.md",
    "scripts/export_public_mirror.py",
    "scripts/export_public_mirror_test.py",
})

SKIP_WALK_DIRS = frozenset({".git", ".vs", "bin", "obj", "target", "node_modules", "__pycache__", "site"})
TEXT_SKIP_SUFFIXES = frozenset({".png", ".ico", ".svg", ".webmanifest", ".woff", ".woff2", ".ttf"})

# Patterns assembled from parts so this clean-room scanner never triggers itself.
_DISASSEMBLY_LINE_RE = re.compile(r"^[0-9a-fA-F]{8}:\s+(?:[0-9a-fA-F]{2}\s+){2,}")
_PSEUDOCODE_RE = re.compile(r"\b(?:FUN|DAT|LAB)_[0-9a-fA-F]{6,}\b|__this" + r"call\b")

_TEXT_RULES: tuple[tuple[str, str, re.Pattern[str]], ...] = (
    (
        "personal-path",
        "personal Windows or Unix user home path",
        re.compile(
            r"(?:[A-Za-z]:\\Users\\(?!Public\b|Default\b|user\b|<user>\b)[A-Za-z0-9._-]+"
            r"|(?<![A-Za-z0-9_])/home/(?!user\b|<user>\b|runner\b|\[Filtered\])[A-Za-z0-9._-]+)"
        ),
    ),
    (
        "credential",
        "embedded credential, API token, or private key",
        re.compile(
            r"(?:ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}"
            r"|-----BEGIN (?:RSA|OPENSSH|EC|PGP) PRIVATE KEY-----"
            r"|https://[0-9a-f]{16,}@(?!example\.invalid)[^\s\"']+)"
        ),
    ),
    (
        "tracker-id",
        "internal issue-tracker identifier",
        re.compile(r"\b" + "SI" + r"M-\d+\b"),
    ),
    (
        "internal-plan-ref",
        "reference to excluded internal library or trail corpus",
        re.compile(r"(?:^|[\s(\"'`\[])(?:\." + "tr" + "ail|libr" + "ary|open" + "wiki|co" + r"ord)/[A-Za-z0-9_.-]+"),
    ),
    (
        "excluded-re-or-infra",
        "reference to excluded native/decompile/remote-infra tooling",
        re.compile(
            r"(?i)(?:\b" + "ghid" + "ra"
            r"|native[-_]" + "observatory"
            r"|" + "col" + "ab[-_]offload"
            r"|decomp" + "ile[-_]lane"
            r"|managed[-_]" + "recompile"
            r"|remote-test-" + "rig"
            r"|\b" + "unr" + r"aid\b"
            r"|\b" + "cor" + r"tex\b"
            r"|\b" + "rig_(?:peer|cargo|tools)\b"
            r"|\b" + "ts3" + r"(?:w|src)\.py\b"
            r"|\b" + "decomp" + r"_notes\.py\b"
            r"|\b" + "dn" + r"spy\.ps1\b)"
        ),
    ),
)


@dataclass(frozen=True)
class Violation:
    """Single guard finding bound to a relative path and optional 1-based line."""

    path: str
    rule: str
    message: str
    line: int | None = None

    def format_text(self) -> str:
        loc = f"{self.path}:{self.line}" if self.line is not None else self.path
        return f"{loc}: [{self.rule}] {self.message}"

    def format_github(self) -> str:
        if self.line is not None:
            return f"::error file={self.path},line={self.line}::[{self.rule}] {self.message}"
        return f"::error file={self.path}::[{self.rule}] {self.message}"


def normalize_rel(path: str | Path) -> str:
    norm = str(path).replace("\\", "/")
    while norm.startswith("./"):
        norm = norm[2:]
    return norm


def is_excluded_path(rel: str) -> str | None:
    norm = normalize_rel(rel)
    if norm in EXCLUDED_EXACT_FILES:
        return f"excluded repository file: {norm}"
    for prefix in EXCLUDED_PREFIXES:
        if norm.startswith(prefix) or norm == prefix.rstrip("/"):
            return f"excluded repository directory: {prefix}"
    return None


def _git_changed_files(root: Path, base: str) -> list[Path] | None:
    try:
        res = subprocess.run(
            ["git", "-C", str(root), "diff", "--name-only", "--diff-filter=ACMRT", f"{base}...HEAD"],
            stdin=subprocess.DEVNULL,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=30,
            check=False,
        )
    except (OSError, subprocess.SubprocessError):
        return None
    if res.returncode != 0:
        return None
    return [root / line.strip() for line in res.stdout.splitlines() if line.strip()]


def collect_target_files(root: Path, base: str | None = None) -> list[Path]:
    if base:
        changed = _git_changed_files(root, base)
        if changed is not None:
            return sorted(p for p in changed if p.is_file())
    files: list[Path] = []
    for current, dirs, names in os.walk(root):
        dirs[:] = sorted(d for d in dirs if d not in SKIP_WALK_DIRS)
        cur_path = Path(current)
        for name in sorted(names):
            files.append(cur_path / name)
    return files


def _check_binary_file(path: Path, rel: str) -> list[Violation]:
    suffix = path.suffix.lower()
    if suffix not in BINARY_SUFFIXES:
        return []
    if suffix == ".dll" and rel.startswith("refs/sims3/"):
        problems = reference_assembly.reference_stub_problems(path.read_bytes())
        if not problems:
            return []
        detail = "; ".join(problems)
        return [Violation(rel, "non-stub-dll", f"DLL under refs/sims3 is not a valid reference stub: {detail}")]
    return [Violation(rel, "prohibited-binary", f"prohibited binary or package artifact: {rel}")]


def scan_file(path: Path, rel: str) -> list[Violation]:
    excluded_reason = is_excluded_path(rel)
    if excluded_reason is not None:
        return [Violation(rel, "excluded-path", excluded_reason)]

    bin_violations = _check_binary_file(path, rel)
    if bin_violations or path.suffix.lower() in BINARY_SUFFIXES or path.suffix.lower() in TEXT_SKIP_SUFFIXES:
        return bin_violations

    try:
        raw = path.read_bytes()
    except OSError as exc:
        return [Violation(rel, "unreadable", f"could not read file: {exc}")]

    if b"\x00" in raw:
        return [Violation(rel, "binary-content", "unexpected NUL-byte binary file in source tree")]

    text = raw.decode("utf-8", errors="replace")
    violations: list[Violation] = []
    disasm_run = 0
    for idx, line in enumerate(text.splitlines(), 1):
        if _DISASSEMBLY_LINE_RE.match(line):
            disasm_run += 1
            if disasm_run == 3:
                violations.append(Violation(rel, "disassembly-dump", "copied multi-line native disassembly dump", idx))
        else:
            disasm_run = 0

        if _PSEUDOCODE_RE.search(line):
            violations.append(Violation(rel, "decompiled-pseudocode", "decompiled native pseudocode symbol marker", idx))

        for rule, desc, pattern in _TEXT_RULES:
            if pattern.search(line):
                violations.append(Violation(rel, rule, f"{desc} found", idx))
    return violations


def scan_tree(root: Path, base: str | None = None) -> list[Violation]:
    root = root.resolve()
    violations: list[Violation] = []
    # Always audit excluded paths across the full tree even when --base limits text scans.
    for dir_name in EXCLUDED_PREFIXES:
        candidate = root / dir_name.rstrip("/")
        if candidate.exists():
            violations.append(Violation(dir_name.rstrip("/"), "excluded-path", f"excluded directory exists: {dir_name}"))
    for exact in sorted(EXCLUDED_EXACT_FILES):
        candidate = root / exact
        if candidate.is_file():
            violations.append(Violation(exact, "excluded-path", f"excluded file exists: {exact}"))

    seen_excluded = {v.path for v in violations}
    for path in collect_target_files(root, base=base):
        rel = normalize_rel(path.relative_to(root))
        for v in scan_file(path, rel):
            if v.rule == "excluded-path" and v.path in seen_excluded:
                continue
            violations.append(v)
    return violations


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Verify a Sims3Online tree is free of proprietary or private leaks.")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument("--base", help="Optional base git ref to restrict content checks to changed files.")
    parser.add_argument("--format", choices=("text", "github"), default="text")
    args = parser.parse_args(argv)

    violations = scan_tree(args.root, base=args.base)
    for v in violations:
        print(v.format_github() if args.format == "github" else v.format_text())
    if violations:
        print(f"proprietary_input_guard: FAILED ({len(violations)} violation(s))", file=sys.stderr)
        return 1
    print(f"proprietary_input_guard: PASS ({args.root})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
