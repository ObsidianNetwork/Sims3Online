from __future__ import annotations

import struct
import unittest
from pathlib import Path

from scripts.reference_assembly import (
    METHOD_DEF,
    THROW_NULL_BODY,
    _Metadata,
    inspect,
    reference_stub_problems,
)

STUBS = Path(__file__).resolve().parent.parent / "refs" / "sims3"
# The smallest committed stub (System); every case below mutates a copy of it.
STUB = STUBS / "simcore_01.dll"


def _cli_directory(data: bytes) -> int:
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    optional = pe + 24
    magic = struct.unpack_from("<H", data, optional)[0]
    return optional + (96 if magic == 0x10B else 112) + 8 * 14


def _with_first_method_rva(data: bytes, body: bytes) -> bytes:
    """Point the first method at `body`, written at the start of the first section."""
    metadata = _Metadata(data)
    _, virtual_address, _, raw_pointer = metadata._sections[0]
    patched = bytearray(data)
    patched[raw_pointer : raw_pointer + len(body)] = body
    start, _ = metadata.layout[METHOD_DEF]
    struct.pack_into("<I", patched, start, virtual_address)
    return bytes(patched)


@unittest.skipUnless(STUB.is_file(), "refs/sims3 stubs are not in this checkout")
class ReferenceAssemblyTests(unittest.TestCase):
    def setUp(self) -> None:
        self.stub = STUB.read_bytes()

    def test_every_committed_stub_is_reference_only(self) -> None:
        for path in sorted(STUBS.glob("*.dll")):
            with self.subTest(path=path.name):
                report = inspect(path.read_bytes())
                self.assertEqual(report.problems, [])
                self.assertTrue(report.has_reference_attribute)
                self.assertGreater(report.methods, 0)

    def test_method_with_il_body_is_rejected(self) -> None:
        patched = _with_first_method_rva(self.stub, b"\x0e\x02\x28\x00\x00\x00\x0a\x2a")
        report = inspect(patched)
        self.assertEqual(report.method_bodies, 1)
        self.assertIn(f"1 of {report.methods} methods have IL bodies", report.problems)

    def test_throw_null_placeholder_is_not_a_body(self) -> None:
        self.assertEqual(inspect(_with_first_method_rva(self.stub, THROW_NULL_BODY)).problems, [])

    def test_missing_reference_assembly_attribute_is_rejected(self) -> None:
        patched = self.stub.replace(b"ReferenceAssemblyAttribute\0", b"ReferenceAssemblyAttributX\0")
        self.assertIn("no ReferenceAssemblyAttribute", reference_stub_problems(patched))

    def test_embedded_resources_are_rejected(self) -> None:
        patched = bytearray(self.stub)
        cli_rva, _ = struct.unpack_from("<II", patched, _cli_directory(self.stub))
        cli = _Metadata(self.stub).offset(cli_rva)
        struct.pack_into("<I", patched, cli + 28, 16)
        self.assertIn("1 embedded resources", reference_stub_problems(bytes(patched)))

    def test_native_pe_is_rejected(self) -> None:
        patched = bytearray(self.stub)
        struct.pack_into("<II", patched, _cli_directory(self.stub), 0, 0)
        self.assertEqual(
            reference_stub_problems(bytes(patched)), ["not a managed assembly (no CLI header)"]
        )

    def test_non_pe_and_truncated_files_are_rejected(self) -> None:
        self.assertEqual(reference_stub_problems(b"not an assembly"), ["no PE header"])
        self.assertEqual(len(reference_stub_problems(self.stub[:1024])), 1)


def main() -> int:
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(ReferenceAssemblyTests)
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    failed = len(result.failures) + len(result.errors)
    print(f"\nRESULT total={result.testsRun} failed={failed} skipped={len(result.skipped)}")
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    raise SystemExit(main())
