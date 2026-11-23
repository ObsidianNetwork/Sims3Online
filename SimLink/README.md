# SimLink

SimLink is an optional native sidecar used by SimsOnline for local communication between a game
process and its controller. It is an experimental component, not a standalone application or
player-facing feature.

The sidecar is built from the source in this directory. It requires a compatible development
toolchain and a locally installed, legally acquired copy of The Sims 3 to test in-game. Game
files and the ASI loader are not included in this repository.

SimLink is optional; the main controller and client projects can be built and their tests run
without building the sidecar. See the [project documentation](../docs/index.md) and the
[testing guide](../docs/testing.md) for the supported development workflow.
