# Tools directory catalog

`git ls-files tools` reports 205 tracked files across test kits, native/managed analysis, interop experiments, research helpers, and local infrastructure. This is not a single installable product; dependencies and side effects vary. This catalog intentionally omits internal agent/coordination harness material. Read each tool's README, module docstring and current help before use.

## Main groups

| Group | Tracked files | Purpose and boundaries |
|---|---:|---|
| `tools/tester_kit/` | 14 | Friend-side install/join/uninstall/log-collection scripts, command wrappers and fork-builder project. README states it is local test tooling, not a player distribution. |
| Small Rust codec probe | 3 | Standalone protocol-codec experiment; not the public server CLI. |
| Root helpers | 3 | Package marker and standalone notebook/usage utilities; inspect before running because they may invoke external services or alter local resources. |

## Tester kit

The `tools/tester_kit/` folder holds install, join, uninstall and log-collection scripts; `.cmd` files wrap PowerShell entry points. `wire_test.py` checks the kit connection/wire behavior. The root orchestrator is `scripts/tester_kit.py`; `python scripts/tester_kit.py --help` lists `build`, `verify`, `install-box`, `list` and `check`. Help was inspected only; no kit was built or installed. Generated kits are local artifacts, not files to commit; they may contain third-party or game-derived material.


## Build and verification entry points

| Task | Entry point |
|---|---|
| Evidence archive CLI usage | `python -m tools.static-analysis.build_evidence_archive --help` |
| static-analysis entry points | Use `python -m tools.static-analysis.<module> --help` so package imports resolve; do not invoke a campaign/validation/publish operation just to inspect usage. |

No live game, remote service, decompiler, archive publication or kit installation was executed for this catalog. internal tooling reference declarations and native analysis tooling do not confer permission to redistribute proprietary input.

## General limits

Tools may access local processes, saved games, packages, analysis binaries, external endpoints or credentials. A structurally valid report can still lack behavioral evidence or use the wrong baseline. Platform boundaries matter: Windows process attachment and Linux/POSIX-safe evidence publication are different capabilities. Inspect every tool's input/output boundary before using it with private data.

## Related

- [Script catalog](scripts-catalog.md)
- [Testing](../testing.md)
- [Repository hygiene](repo-hygiene.md)
