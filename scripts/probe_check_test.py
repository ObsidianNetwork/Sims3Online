"""Unit tests for probe_check: python -m unittest scripts.probe_check_test"""
from __future__ import annotations

import contextlib
import io
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import bridgectl  # noqa: E402
import probe_check as pc  # noqa: E402
import stack_claim  # noqa: E402

SOURCE = '''using System;
namespace P
{
    public class Probe // __NOTE__ in a comment
    {
        private const string Actor = "__ACTOR__";
        private const bool Clear = __CLEAR__;
        private const int Rank = __RANK__;
        private const float Within = __WITHIN__f;
        public const ulong Id = __ID__uL;
        public static string Run() { return "err=no Sim named __ACTOR__"; }
        public static object At() { return __AT__; }
    }
}
'''


class FillTest(unittest.TestCase):
    def test_every_template_in_scripts_fills(self) -> None:
        found = {t.label: t for t in pc.templates()}
        self.assertIn("sim_act.TEMPLATE", found)
        self.assertIn("client_pick.TEMPLATE", found)
        for label, template in found.items():
            with self.subTest(template=label):
                text, missing = pc.fill(template)
                self.assertEqual(missing, [])
                self.assertIsNone(pc.PLACEHOLDER.search(text))

    def test_samples_follow_the_context(self) -> None:
        text, missing = pc.fill(pc.Template("demo", "TEMPLATE", SOURCE, 10))
        self.assertEqual(missing, ["AT"])  # `return __AT__;` names no type: it needs a SAMPLES entry
        for line in ('// Jennifer in a comment', 'Actor = "Jennifer";', "Clear = false;", "Rank = 0;", "Within = 1f;",
                     "Id = 1uL;", '"err=no Sim named Jennifer"'):
            self.assertIn(line, text)
        with mock.patch.dict(pc.SAMPLES, {("demo.TEMPLATE", "AT"): "null"}):
            self.assertEqual(pc.fill(pc.Template("demo", "TEMPLATE", SOURCE, 10))[1], [])

    def test_templates_are_read_without_importing_their_module(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            (Path(folder) / "broken.py").write_text(
                "import no_such_module\nX_TEMPLATE = r'''class A { string s = \"__S__\"; }'''\nNOT_ONE = 'class B {}'\n",
                encoding="utf-8")
            (Path(folder) / "broken_test.py").write_text("T_TEMPLATE = 'class C {}'\n", encoding="utf-8")
            found = pc.templates(folder=Path(folder))
        self.assertEqual([(t.label, t.line) for t in found], [("broken.X_TEMPLATE", 2)])


class CompileTest(unittest.TestCase):
    def setUp(self) -> None:
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.out = Path(folder.name)
        self.template = pc.Template("sim_act", "TEMPLATE", SOURCE.replace("__AT__", "null"), 74)

    def test_a_csc_error_names_the_module_line(self) -> None:
        def broken(source: str, stamp: str) -> str:
            sys.stderr.write(f"Compiling {source}\n{source}(11,35): error CS0103: The name 'x' does not exist\n")
            sys.exit("probe compile failed (exit 1)")

        with mock.patch.object(bridgectl, "compile_probe", broken):
            ok, detail = pc.check_template(self.template, out=self.out)
        self.assertFalse(ok)
        self.assertEqual(detail, "sim_act.py:84 (probe 11,35): error CS0103: The name 'x' does not exist")

    def test_a_compiled_probe_leaves_no_dll(self) -> None:
        dll = self.out / "SimActProbe_chk.dll"

        def built(source: str, stamp: str) -> str:
            self.assertTrue(stamp.startswith("chk"))
            self.assertNotIn("__", Path(source).read_text(encoding="utf-8"))
            dll.write_bytes(b"MZ")
            return str(dll)

        with mock.patch.object(bridgectl, "compile_probe", built):
            self.assertEqual(pc.check_template(self.template, out=self.out), (True, "compiled"))
        self.assertFalse(dll.exists())

    def test_a_quiet_window_refuses_before_any_compile(self) -> None:
        out = io.StringIO()
        with mock.patch.object(stack_claim, "quiet_refusal", return_value="REFUSED: quiet"), \
                mock.patch.object(bridgectl, "compile_probe", side_effect=AssertionError("compiled")), \
                contextlib.redirect_stdout(out):
            self.assertEqual(pc.main(["sim_act"]), 3)
            self.assertEqual(pc.main(["sim_act", "--render-only"]), 0)  # rendering is no build
        self.assertIn("REFUSED: quiet", out.getvalue())


if __name__ == "__main__":
    unittest.main()
