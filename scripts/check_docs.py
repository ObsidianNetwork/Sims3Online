"""check_docs -- verify that the documentation points at things that exist.

  python scripts/check_docs.py              # broken links fail; stale path mentions are warnings
  python scripts/check_docs.py --strict     # stale path mentions fail too
  python scripts/check_docs.py --anchors    # also check #heading anchors in links to Markdown files
  python scripts/check_docs.py --json       # machine-readable report on stdout
  python scripts/check_docs.py docs/architecture.md scripts/         # only these files and folders

What it checks, over every tracked Markdown file (git ls-files '*.md'), or only the ones the given paths name (a
Markdown file, tracked or not yet, or a folder's tracked Markdown files):

1. Links. Every relative link target in [text](target), [text]: target and <img src="target"> must
   resolve to a file or folder in the checkout. Links inside code blocks and code spans are ignored.
   With --anchors, a #fragment on a link to a Markdown file must match a heading in that file
   (GitHub's slug rules).
2. Path mentions. Repository paths written inside code (`scripts/foo.py`, a command in a fenced
   block) must exist, relative to the repository root or to the document. Local-only locations
   (_analysis/, .omo/, .local/, build outputs), placeholders such as <name>, * or {x}, a range such as
   specs/31–35, a git branch named right after the word "branch" (on branch tools/friction), any name that
   is a branch in this clone (local or remote), a ref (refs/heads/feed-next), and what
   scripts/check_docs_allow.txt allows, one line each with its reason.

Exit code: 0 when there are no errors, 1 otherwise, 2 on a usage error. CI runs the default mode.
"""

from __future__ import annotations

import argparse
import fnmatch
import functools
import json
import re
import subprocess
import sys
from dataclasses import asdict, dataclass
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

ROOT = Path(__file__).resolve().parents[1]

# Top-level folders whose paths are worth checking when they appear in code.
PATH_ROOTS = (
    "scripts", "tools", "docs", "scratch", "schemas", "scenarios", "refs", "SimLink", "DedicatedServer",
    "DedicatedServer.Tests", "SimsOnline.Client", "SimsOnline.TestClient", "SimsOnlineServerHostMod",
    "SimsOnlineClientConnectorMod",  # deleted; kept so a doc that names a file in it is reported
    "Assets", ".github",
)
# Paths that exist only on a developer's machine or in build output.
LOCAL_PREFIXES = ("_analysis/", ".omo/", ".local/", ".tools/", "SimLink/build/")
LOCAL_PARTS = ("/bin/", "/obj/")
FENCE = re.compile(r"^(\s*)(```|~~~)")
INLINE_CODE = re.compile(r"(`+)(.+?)\1")
LINK = re.compile(r"!?\[(?:[^\]\[]|\[[^\]]*\])*\]\(\s*<?([^)\s>]+)>?(?:\s+\"[^\"]*\")?\s*\)")
REF_DEF = re.compile(r"^\s{0,3}\[[^\]]+\]:(?!:)\s*<?(\S+?)>?(?:\s+.*)?$")  # not a C#/PowerShell ::
IMG_SRC = re.compile(r"<img\b[^>]*\bsrc=\"([^\"]+)\"", re.IGNORECASE)
HEADING = re.compile(r"^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$")
HTML_ANCHOR = re.compile(r"<a\s+(?:name|id)=\"([^\"]+)\"", re.IGNORECASE)
PATH_TOKEN = re.compile(
    r"(?<![\w./-])((?:" + "|".join(re.escape(r) for r in PATH_ROOTS) + r")/[A-Za-z0-9_./\-]*[A-Za-z0-9_/])"
)
SCHEME = re.compile(r"^[a-zA-Z][a-zA-Z0-9+.-]*:")
BRANCH_BEFORE = re.compile(r"(?:^|[\s(])branch\s+$", re.IGNORECASE)  # "on branch tools/friction": a git branch
REF_PREFIXES = ("refs/heads/", "refs/remotes/", "refs/tags/")
# Path mentions known not to exist and kept on purpose. Each line: <path glob> [<document glob>]  # reason
ALLOWLIST = "scripts/check_docs_allow.txt"


@functools.cache
def git_refs(root: Path) -> frozenset[str]:
    """Branch names in this clone, local and remote (origin/tools/x also as tools/x); empty outside git."""
    try:
        out = subprocess.run(["git", "-C", str(root), "for-each-ref", "--format=%(refname:short)", "refs/heads",
                              "refs/remotes"], capture_output=True, text=True, check=False).stdout.split()
    except OSError:
        return frozenset()
    return frozenset(out) | {name.split("/", 1)[1] for name in out if "/" in name}


@functools.cache
def allowlist(root: Path) -> tuple[tuple[str, str], ...]:
    path = root / ALLOWLIST
    if not path.is_file():
        return ()
    rules = []
    for line in path.read_text(encoding="utf-8").splitlines():
        fields = line.split("#", 1)[0].split()
        if fields:
            rules.append((fields[0], fields[1] if len(fields) > 1 else "*"))
    return tuple(rules)


def allowed(doc: str, token: str, root: Path) -> bool:
    return any(fnmatch.fnmatchcase(token, t) and fnmatch.fnmatchcase(doc, d) for t, d in allowlist(root))


@dataclass(frozen=True)
class Problem:
    kind: str  # "link", "anchor" or "path"
    severity: str  # "error" or "warning"
    file: str
    line: int
    target: str
    message: str


def tracked_markdown() -> list[str]:
    out = subprocess.run(["git", "ls-files", "-z", "--", "*.md"], cwd=ROOT, capture_output=True, check=True)
    return sorted(p for p in out.stdout.decode("utf-8").split("\0") if p)


def select_docs(paths: list[str], tracked: list[str], root: Path = ROOT, cwd: Path | None = None) -> list[str]:
    """The Markdown files to check, root-relative with / separators: every tracked one when no path is given, else the
    ones the paths name (relative to cwd or absolute): a Markdown file, tracked or not yet, or a folder's tracked
    Markdown files. ValueError for a path that is missing, outside the checkout or not Markdown."""
    if not paths:
        return tracked
    base, top = (cwd or Path.cwd()), root.resolve()
    chosen: list[str] = []
    for given in paths:
        path = (Path(given) if Path(given).is_absolute() else base / given).resolve()
        try:
            rel = path.relative_to(top).as_posix()
        except ValueError:
            raise ValueError(f"{given}: not in this checkout ({root})") from None
        if path.is_dir():
            prefix = "" if rel == "." else rel + "/"
            chosen += [doc for doc in tracked if doc.startswith(prefix)]
        elif path.is_file():
            if path.suffix.lower() != ".md":
                raise ValueError(f"{given}: not a Markdown file")
            chosen.append(rel)
        else:
            raise ValueError(f"{given}: no such file or folder")
    return sorted(dict.fromkeys(chosen))


def tracked_paths() -> set[str]:
    out = subprocess.run(["git", "ls-files", "-z"], cwd=ROOT, capture_output=True, check=True)
    files = {p for p in out.stdout.decode("utf-8").split("\0") if p}
    dirs = {str(Path(p).parent).replace("\\", "/") for p in files}
    expanded = set(dirs)
    for d in dirs:
        parts = d.split("/")
        expanded.update("/".join(parts[:i]) for i in range(1, len(parts)))
    return files | expanded


def split_code(lines: list[str]) -> tuple[list[str], list[tuple[int, str]]]:
    """Return (prose lines with code removed, [(line number, code text)])."""
    prose: list[str] = []
    code: list[tuple[int, str]] = []
    fence: str | None = None
    for number, line in enumerate(lines, 1):
        match = FENCE.match(line)
        if fence is not None:
            if match and match.group(2) == fence:
                fence = None
            else:
                code.append((number, line))
            prose.append("")
            continue
        if match:
            fence = match.group(2)
            prose.append("")
            continue
        for span in INLINE_CODE.finditer(line):
            code.append((number, span.group(2)))
        prose.append(INLINE_CODE.sub(lambda m: " " * len(m.group(0)), line))
    return prose, code


def github_slug(text: str) -> str:
    text = re.sub(r"<[^>]+>", "", text)  # inline HTML
    text = re.sub(r"!?\[([^\]]*)\]\([^)]*\)", r"\1", text)  # links keep their text
    text = text.replace("`", "").strip().lower()
    text = re.sub(r"[^\w\- ]", "", text)
    return text.replace(" ", "-")


_anchor_cache: dict[Path, set[str]] = {}


def anchors_of(path: Path) -> set[str]:
    if path not in _anchor_cache:
        seen: dict[str, int] = {}
        anchors: set[str] = set()
        fence: str | None = None
        for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
            marker = FENCE.match(line)
            if marker and (fence is None or marker.group(2) == fence):
                fence = None if fence else marker.group(2)
                continue
            if fence:
                continue
            heading = HEADING.match(line)  # raw text: code spans in headings count toward the slug
            if heading:
                slug = github_slug(heading.group(2))
                count = seen.get(slug, 0)
                anchors.add(slug if count == 0 else f"{slug}-{count}")
                seen[slug] = count + 1
            anchors.update(a.lower() for a in HTML_ANCHOR.findall(line))
        _anchor_cache[path] = anchors
    return _anchor_cache[path]


def check_links(doc: str, prose: list[str], check_anchors: bool, root: Path = ROOT) -> list[Problem]:
    problems: list[Problem] = []
    base = (root / doc).parent
    for number, line in enumerate(prose, 1):
        targets = [m.group(1) for m in LINK.finditer(line)] + IMG_SRC.findall(line)
        ref = REF_DEF.match(line)
        if ref:
            targets.append(ref.group(1))
        for target in targets:
            if SCHEME.match(target) or target.startswith("//"):
                continue
            path_part, _, fragment = target.partition("#")
            path_part = path_part.split("?", 1)[0]
            resolved = (base / path_part).resolve() if path_part else (root / doc).resolve()
            if path_part and not resolved.exists():
                try:
                    wanted = resolved.relative_to(root).as_posix()
                except ValueError:
                    wanted = ""
                if wanted and (wanted.startswith(LOCAL_PREFIXES)
                               or any(p in f"/{wanted}/" for p in LOCAL_PARTS)):
                    continue  # a developer-local artifact (run output, analysis tree, build dir):
                              # the same exemption check_paths gives path mentions -- the link is
                              # true on the machine that produced it and absent in a clone
                problems.append(Problem("link", "error", doc, number, target, "link target does not exist"))
                continue
            if (check_anchors and fragment and resolved.is_file() and resolved.suffix.lower() == ".md"
                    and fragment.lower() not in anchors_of(resolved)):
                problems.append(Problem("anchor", "error", doc, number, target, "no heading matches this anchor"))
    return problems


def git_ignored(path: str, root: Path = ROOT) -> bool:
    """Whether git ignores the path in this checkout (False outside git or when git is missing)."""
    try:
        return subprocess.run(["git", "-C", str(root), "check-ignore", "-q", "--no-index", "--", path],
                              capture_output=True, check=False).returncode == 0
    except OSError:
        return False


def check_paths(doc: str, code: list[tuple[int, str]], known: set[str], root: Path = ROOT) -> list[Problem]:
    problems: list[Problem] = []
    doc_dir = str(Path(doc).parent).replace("\\", "/")
    reported: set[tuple[int, str]] = set()
    for number, text in code:
        for match in PATH_TOKEN.finditer(text):
            token = match.group(1).rstrip("/.")
            if any(c in text[match.end():match.end() + 2] for c in "<*{$–"):
                continue  # a placeholder or range follows, e.g. internal documentation<system>.md, sim96_specs/31–35
            if BRANCH_BEFORE.search(text[:match.start()]) and "." not in token.rsplit("/", 1)[-1]:
                continue  # a branch whose name starts like a folder, e.g. on branch tools/friction (not a file)
            if token.startswith(LOCAL_PREFIXES) or any(p in f"/{token}/" for p in LOCAL_PARTS):
                continue
            relative = f"{doc_dir}/{token}" if doc_dir != "." else token
            candidates = (token, str(Path(relative)).replace("\\", "/"))
            if any(c in known or (root / c).exists() for c in candidates):
                continue
            if token.endswith(("_", "-")) and any(k.startswith(token) for k in known):
                continue  # a prefix such as scripts/static-analysis_
            if token.startswith(REF_PREFIXES) or token in git_refs(root):
                continue  # a git ref or a branch whose name starts like a folder (tools/seam-map), not a path
            if allowed(doc, token, root):
                continue  # kept on purpose: scripts/check_docs_allow.txt says why
            if git_ignored(token, root):
                continue  # local by design, like .omo/: it exists only on a developer's machine
            if (number, token) in reported:
                continue
            reported.add((number, token))
            problems.append(Problem("path", "warning", doc, number, token, "path mentioned in code does not exist"))
    return problems


def run(strict: bool, check_anchors: bool, docs: list[str] | None = None) -> list[Problem]:
    known = tracked_paths()
    problems: list[Problem] = []
    for doc in tracked_markdown() if docs is None else docs:
        path = ROOT / doc
        if not path.is_file():
            continue
        prose, code = split_code(path.read_text(encoding="utf-8", errors="replace").splitlines())
        problems += check_links(doc, prose, check_anchors)
        problems += check_paths(doc, code, known)
    if strict:
        problems = [Problem(p.kind, "error", p.file, p.line, p.target, p.message) for p in problems]
    return problems


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n", 1)[0], formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--strict", action="store_true", help="treat stale path mentions as errors")
    parser.add_argument("--anchors", action="store_true", help="check #heading anchors in links to Markdown files")
    parser.add_argument("--json", action="store_true", help="print the report as JSON")
    parser.add_argument("paths", nargs="*", help="check only these Markdown files and folders (default: every tracked one)")
    args = parser.parse_args(argv)

    try:
        docs = select_docs(args.paths, tracked_markdown())
    except ValueError as ex:
        print(f"check_docs: {ex}", file=sys.stderr)
        return 2
    problems = run(args.strict, args.anchors, docs)
    errors = [p for p in problems if p.severity == "error"]
    if args.json:
        print(json.dumps([asdict(p) for p in problems], indent=2))
    else:
        for p in problems:
            print(f"{p.file}:{p.line}: {p.severity}: {p.kind} {p.target}: {p.message}")
        warnings = len(problems) - len(errors)
        print(f"check_docs: {len(errors)} error(s), {warnings} warning(s) in {len(docs)} Markdown files")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
