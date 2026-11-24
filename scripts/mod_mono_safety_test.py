"""Tests for TS3 EA Mono <PrivateImplementationDetails> / HasFieldRVA safety guards."""

from __future__ import annotations

import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import check_mod_mono_safety as cmms  # noqa: E402
import check_mod_runtime as cmr  # noqa: E402


class SourceGuardsTests(unittest.TestCase):
    def test_detects_eight_byte_array_initializer(self) -> None:
        src = """
        class C {
            static byte[] Header() {
                return new byte[] { 83, 73, 77, 78, 65, 86, 48, 49 };
            }
            static byte[] SafeTwelve() {
                return new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
            }
        }
        """
        hazards = cmms.scan_csharp_source(Path("Test.cs"), src)
        self.assertEqual(len(hazards), 1, [h.format() for h in hazards])
        self.assertEqual(hazards[0].kind, "int64-field-rva-array")

    def test_detects_string_switch_including_const_labels(self) -> None:
        src = """
        static class Verbs { public const string V1 = "V1"; public const string V2 = "V2"; }
        class C {
            static int Dispatch(string s) {
                switch (s) {
                    case Verbs.V1: return 1;
                    case Verbs.V2: return 2;
                    case "V3": return 3;
                    case "V4": return 4;
                    case "V5": return 5;
                    case "V6": return 6;
                    default: return 0;
                }
            }
            static int SmallCharSwitch(char c) {
                switch (c) {
                    case 'a': return 1;
                    case 'b': return 2;
                    case 'c': return 3;
                    case 'd': return 4;
                    case 'e': return 5;
                    case 'f': return 6;
                    default: return 0;
                }
            }
        }
        """
        hazards = cmms.scan_csharp_source(Path("Test.cs"), src)
        self.assertEqual(len(hazards), 1, [h.format() for h in hazards])
        self.assertEqual(hazards[0].kind, "string-switch")

    def test_shipped_mod_sources_are_clean(self) -> None:
        hazards = cmms.scan_mod_sources()
        self.assertEqual(hazards, [], [h.format() for h in hazards])


class CompiledClrAndMonoHostTests(unittest.TestCase):
    @staticmethod
    def _csc20() -> Path | None:
        try:
            return cmr.csc()
        except SystemExit:
            return None

    def test_clr_scanner_catches_and_clears_real_csc20_output(self) -> None:
        csc = self._csc20()
        if csc is None:
            self.skipTest(".NET Framework 2.0 csc.exe not available on this host")
        with tempfile.TemporaryDirectory() as tmp:
            t = Path(tmp)
            bad_cs = t / "Bad.cs"
            bad_dll = t / "Bad.dll"
            bad_cs.write_text(
                """
                public class Bad {
                    public static byte[] Magic() { return new byte[] { 83, 73, 77, 78, 65, 86, 48, 49 }; }
                    public static int Sw(string s) {
                        switch (s) {
                            case "A": return 1; case "B": return 2; case "C": return 3;
                            case "D": return 4; case "E": return 5; case "F": return 6;
                            default: return 0;
                        }
                    }
                }
                """,
                encoding="utf-8",
            )
            subprocess.run(
                [str(csc), "/nologo", "/target:library", "/platform:x86", "/optimize+", f"/out:{bad_dll}", str(bad_cs)],
                check=True,
                capture_output=True,
                text=True,
            )
            bad_hazards = cmms.scan_clr_hazards(pe_path=bad_dll)
            kinds = {h.kind for h in bad_hazards}
            self.assertIn("clr-int64-field-rva", kinds)
            self.assertIn("clr-priv-ref-field", kinds)

            good_cs = t / "Good.cs"
            good_dll = t / "Good.dll"
            good_cs.write_text(
                """
                using System.Collections.Generic;
                using System.Text;
                public class Good {
                    private static readonly Dictionary<string, int> sMap = BuildMap();
                    private static Dictionary<string, int> BuildMap() {
                        Dictionary<string, int> m = new Dictionary<string, int>();
                        m["A"] = 1; m["B"] = 2; m["C"] = 3; m["D"] = 4; m["E"] = 5; m["F"] = 6;
                        return m;
                    }
                    public static byte[] Magic() { return Encoding.UTF8.GetBytes("SIMNAV01"); }
                    public static int Sw(string s) { int v; return sMap.TryGetValue(s, out v) ? v : 0; }
                }
                """,
                encoding="utf-8",
            )
            subprocess.run(
                [str(csc), "/nologo", "/target:library", "/platform:x86", "/optimize+", f"/out:{good_dll}", str(good_cs)],
                check=True,
                capture_output=True,
                text=True,
            )
            self.assertEqual(cmms.scan_clr_hazards(pe_path=good_dll), [])

    def test_compiled_mod_assemblies_have_zero_clr_hazards(self) -> None:
        csc = self._csc20()
        if csc is None:
            self.skipTest(".NET Framework 2.0 csc.exe not available on this host")
        try:
            cmr.assemblies()
        except SystemExit:
            self.skipTest("TS3 assemblies cache not available on this host")

        with tempfile.TemporaryDirectory() as tmp:
            t = Path(tmp)
            shell_sources = [p for p in cmr.MOD.glob("*.cs") if p.name != "Sims3PackageBuilder.cs"]
            handler_sources = sorted((cmr.MOD / "Handlers").glob("*.cs"))
            module_sources = sorted((cmr.MOD / "Presentation").glob("*.cs"))

            shell_dll = t / "shell.dll"
            handlers_dll = t / "handlers.dll"
            module_dll = t / "module.dll"

            subprocess.run(cmr.runtime_args(shell_dll) + ["/optimize+"] + [str(s) for s in shell_sources], check=True)
            subprocess.run(cmr.runtime_args(handlers_dll) + ["/optimize+"] + [str(s) for s in handler_sources], check=True)
            subprocess.run(
                cmr.runtime_args(module_dll, [shell_dll]) + ["/optimize+"] + [str(s) for s in module_sources],
                check=True,
            )

            hazards = (
                cmms.scan_clr_hazards(pe_path=shell_dll, label="shell")
                + cmms.scan_clr_hazards(pe_path=handlers_dll, label="handlers")
                + cmms.scan_clr_hazards(pe_path=module_dll, label="presentation")
            )
            self.assertEqual(hazards, [], [h.format() for h in hazards])

    def test_monohost_int64_field_rva_repro_and_fix(self) -> None:
        out_dir = cmms.ROOT / "tools" / "monohost" / "out"
        exe = out_dir / "monohost.exe"
        ts3 = out_dir / "exe" / "TS3W.exe"
        csc = self._csc20()
        if csc is None or not exe.is_file() or not ts3.is_file():
            self.skipTest("monohost.exe, TS3W.exe copy, or .NET 2 csc.exe not staged")

        with tempfile.TemporaryDirectory() as tmp:
            t = Path(tmp)
            repro_cs = t / "Repro.cs"
            repro_dll = t / "Repro.dll"
            repro_cs.write_text(
                """
                namespace MonoHostBench {
                    public static class Payload {
                        public static byte[] Header() { return new byte[] { 83, 73, 77, 78, 65, 86, 48, 49 }; }
                        public static int Dispatch(string s) {
                            switch (s) {
                                case "A": return 1; case "B": return 2; case "C": return 3;
                                case "D": return 4; case "E": return 5; case "F": return 6;
                                default: return 0;
                            }
                        }
                    }
                    public static class Bench {
                        public static int Dig(int depth) { return Payload.Dispatch("B"); }
                    }
                }
                """,
                encoding="utf-8",
            )
            subprocess.run(
                [str(csc), "/nologo", "/target:library", "/platform:x86", "/optimize+", f"/out:{repro_dll}", str(repro_cs)],
                check=True,
                capture_output=True,
                text=True,
            )
            staged = out_dir / "_monohost_safety_test.dll"
            try:
                shutil.copy2(repro_dll, staged)
                res = subprocess.run(
                    [str(exe), r"exe\TS3W.exe", "asm", "bench", staged.name],
                    cwd=out_dir,
                    capture_output=True,
                    text=True,
                    check=False,
                )
                self.assertIn("EXCEPTION 0xC0000005", res.stdout + res.stderr)

                fixed_cs = t / "Fixed.cs"
                fixed_dll = t / "Fixed.dll"
                fixed_cs.write_text(
                    """
                    using System.Collections.Generic;
                    using System.Text;
                    namespace MonoHostBench {
                        public static class Payload {
                            private static readonly Dictionary<string, int> sMap = Build();
                            private static Dictionary<string, int> Build() {
                                Dictionary<string, int> m = new Dictionary<string, int>();
                                m["A"] = 1; m["B"] = 2; m["C"] = 3; m["D"] = 4; m["E"] = 5; m["F"] = 6;
                                return m;
                            }
                            public static byte[] Header() { return Encoding.UTF8.GetBytes("SIMNAV01"); }
                            public static int Dispatch(string s) { int v; return sMap.TryGetValue(s, out v) ? v : 0; }
                        }
                        public static class Bench {
                            public static int Dig(int depth) {
                                byte[] h = Payload.Header();
                                return h.Length == 8 && h[0] == 83 && Payload.Dispatch("B") == 2 ? 0 : 1;
                            }
                        }
                    }
                    """,
                    encoding="utf-8",
                )
                subprocess.run(
                    [str(csc), "/nologo", "/target:library", "/platform:x86", "/optimize+", f"/out:{fixed_dll}", str(fixed_cs)],
                    check=True,
                    capture_output=True,
                    text=True,
                )
                shutil.copy2(fixed_dll, staged)
                res_ok = subprocess.run(
                    [str(exe), r"exe\TS3W.exe", "asm", "bench", staged.name],
                    cwd=out_dir,
                    capture_output=True,
                    text=True,
                    check=False,
                )
                self.assertEqual(res_ok.returncode, 0, res_ok.stdout)
                self.assertNotIn("EXCEPTION 0xC0000005", res_ok.stdout + res_ok.stderr)
            finally:
                staged.unlink(missing_ok=True)


if __name__ == "__main__":
    unittest.main()
