# Sims 3 reference assemblies

The public repository does **not** distribute the DLL files listed below. Their
redistribution rights have not been verified. The filenames are retained here only as
the local build interface expected by the mod and tooling; this README is not a license
to extract, copy, or redistribute game files.

To build the in-game mod, provide your own legally obtained, locally usable reference
assemblies or API-only stubs under this directory, subject to the applicable game
license. Do not commit the files. The repository build and CI can validate the
controller and other components without them; the full legacy mod compile is skipped
in CI when these local references are absent. When they are present, run
`scripts/check-mod-compile.ps1 -Strict` on a compatible Windows setup.

| File | Assembly | Game package |
|---|---|---|
| `gameplay_00.dll` | Sims3StoreObjects | `gameplay.package` |
| `gameplay_01.dll` | Sims3GameplayObjects | `gameplay.package` |
| `gameplay_02.dll` | Sims3GameplaySystems | `gameplay.package` |
| `gameplay_03.dll` | UI | `gameplay.package` |
| `scripts_00.dll` | SimIFace | `scripts.package` |
| `scripts_01.dll` | ScriptCore | `scripts.package` |
| `scripts_02.dll` | Sims3Metadata | `scripts.package` |
| `simcore_00.dll` | System.Xml | `simcore.package` |
| `simcore_01.dll` | System | `simcore.package` |
| `simcore_02.dll` | mscorlib | `simcore.package` |

Users must obtain their own legal copy of The Sims 3. Never commit or distribute the
game's original assemblies, executables, packages, saves, or decompiled source. Do not
assume these listed names imply permission to redistribute any corresponding files.
See the [development guide](../../docs/development.md) for building Sims3Online.
