"""Resolve repository-relative references into the public documentation directory."""

from __future__ import annotations

from pathlib import Path


def _as_root(root: Path | str) -> Path:
    return root if isinstance(root, Path) else Path(root)


def doc_root(root: Path | str) -> str:
    """The repository-relative documentation directory."""
    return "docs"


def layouts_present(root: Path | str) -> list[str]:
    """Return the documentation directory when it exists."""
    return ["docs"] if (_as_root(root) / "docs").is_dir() else []


def normalize(ref: str) -> str:
    """Normalize a documentation reference to a path relative to ``docs/``."""
    clean = ref.replace("\\", "/")
    while clean.startswith("./"):
        clean = clean[2:]
    if clean.startswith("/docs/"):
        clean = clean[len("/docs/"):]
    elif clean.startswith("docs/"):
        clean = clean[len("docs/"):]
    return clean


def resolve(root: Path | str, ref: str) -> Path:
    """Return the documentation file named by ``ref``, or its canonical path if it is absent."""
    return _as_root(root) / "docs" / normalize(ref)


def relative(path: Path, root: Path | str) -> str:
    """Return a path relative to the repository root using forward slashes."""
    return path.relative_to(_as_root(root)).as_posix()


def live_ref(root: Path | str, fragment: str) -> str:
    """Return the repository-relative path for a documentation reference."""
    return relative(resolve(root, fragment), root)


def write_ref(root: Path | str, fragment: str) -> str:
    """Return the repository-relative destination for a new documentation file."""
    return live_ref(root, fragment)


def to_canonical(ref: str, root: Path | None = None) -> str:
    """Return the canonical repository-relative path for a documentation reference."""
    del root
    clean = normalize(ref)
    return f"docs/{clean}" if clean else "docs"


def iter_note_files(root: Path | str, pattern: str = "*.md"):
    """Yield documentation files matching ``pattern`` in sorted order."""
    docs = _as_root(root) / "docs"
    if docs.is_dir():
        yield from sorted(docs.rglob(pattern))


if __name__ == "__main__":
    import sys

    here = Path(__file__).resolve().parents[1]
    if len(sys.argv) > 1:
        print(resolve(here, sys.argv[1]))
    else:
        print("documentation root: docs")
        for path in iter_note_files(here):
            print(f"  {relative(path, here)}")
