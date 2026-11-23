"""The mod's catch blocks never swallow silently (python -m unittest scripts.mod_catch_test).

Every catch in SimsOnlineServerHostMod/ either does something (SwallowedErrors.Note() counts it for DIAG_STATE's
swallowed=) or says in a comment why it swallows. Only the shell can count: the handler assembly and the presentation
module are separate assemblies that cannot reach SwallowedErrors.
"""

from __future__ import annotations

import re
import unittest
from pathlib import Path

MOD = Path(__file__).resolve().parent.parent / "SimsOnlineServerHostMod"
CATCH = re.compile(r"\bcatch\b\s*(?:\([^)]*\))?\s*\{")


def blank_strings_and_comments(source: str) -> str:
    """The source with string and char literals and comments blanked out (same length, newlines kept)."""
    out = list(source)
    i, n = 0, len(source)

    def blank(start: int, end: int) -> None:
        for k in range(start, min(end, n)):
            if out[k] != "\n":
                out[k] = " "

    while i < n:
        if source.startswith("//", i):
            end = source.find("\n", i)
            end = n if end < 0 else end
            blank(i, end)
            i = end
        elif source.startswith("/*", i):
            end = source.find("*/", i + 2)
            end = n if end < 0 else end + 2
            blank(i, end)
            i = end
        elif source.startswith('@"', i):
            j = i + 2
            while j < n and not (source[j] == '"' and not source.startswith('""', j)):
                j += 2 if source.startswith('""', j) else 1
            blank(i + 2, j)
            i = j + 1
        elif source[i] in "\"'":
            quote, j = source[i], i + 1
            while j < n and source[j] != quote:
                j += 2 if source[j] == "\\" else 1
            blank(i + 1, j)
            i = j + 1
        else:
            i += 1
    return "".join(out)


def silent_catches(source: str) -> list[int]:
    """Line numbers of catch blocks with nothing in them, not even a comment."""
    code = blank_strings_and_comments(source)
    lines = []
    for match in CATCH.finditer(code):
        start = match.end() - 1
        depth, end = 0, start
        while end < len(code):
            depth += {"{": 1, "}": -1}.get(code[end], 0)
            if depth == 0:
                break
            end += 1
        if not source[start + 1:end].strip():
            lines.append(source.count("\n", 0, match.start()) + 1)
    return lines


def mod_sources() -> list[Path]:
    return sorted(MOD.glob("*.cs")) + sorted((MOD / "Handlers").glob("*.cs")) + sorted((MOD / "Presentation").glob("*.cs"))


class ScannerTests(unittest.TestCase):
    def test_finds_only_catches_with_nothing_in_them(self) -> None:
        # Given: catches that are empty, commented, counted, or only look empty inside strings and comments.
        source = "\n".join([
            "try { A(); } catch {}",
            "try { A(); } catch (Exception) { }",
            "try { A(); } catch { // why }",
            "}",
            "try { A(); } catch (Exception) { SwallowedErrors.Note(); }",
            'string s = "catch {}"; // catch {}',
        ])

        # When: it is scanned.
        found = silent_catches(source)

        # Then: only the two empty catches are reported.
        self.assertEqual(found, [1, 2])


class ModCatchTests(unittest.TestCase):
    def test_no_catch_in_the_mod_swallows_silently(self) -> None:
        # Given: every mod source file (shell, handler assembly, presentation module).
        sources = mod_sources()
        self.assertTrue(sources, f"no mod sources under {MOD}")

        # When: each catch block is checked.
        silent = [f"{p.relative_to(MOD.parent).as_posix()}:{line}"
                  for p in sources for line in silent_catches(p.read_text(encoding="utf-8-sig"))]

        # Then: none is empty; each counts with SwallowedErrors.Note() or says why it swallows.
        self.assertEqual(silent, [], "add SwallowedErrors.Note(), or a comment saying why it swallows")

    def test_only_the_shell_counts_swallowed_errors(self) -> None:
        # Given: the handler assembly and the presentation module, built apart from the shell.
        separate = sorted((MOD / "Handlers").glob("*.cs")) + sorted((MOD / "Presentation").glob("*.cs"))

        # When / Then: neither calls SwallowedErrors (it is internal to the shell; a reloaded module would not find it).
        callers = [p.name for p in separate if "SwallowedErrors" in blank_strings_and_comments(p.read_text(encoding="utf-8-sig"))]
        self.assertEqual(callers, [])


if __name__ == "__main__":
    unittest.main()
