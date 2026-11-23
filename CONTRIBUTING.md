# Contributing

Thanks for your interest in Sims3Online. This is an experimental project, and contributions that
improve correctness, documentation, tests, and developer experience are welcome.

Before proposing a change, read the [project overview](docs/overview.md),
[architecture](docs/architecture.md), and [known limitations](docs/status.md). The host game is
the authority for gameplay; clients forward player input and present the host's results.

## Development

The [developer setup](docs/development.md) and [testing guide](docs/testing.md) cover the .NET
build and test commands. A compatible copy of The Sims 3 is required only for live game testing.

For code changes:

- Keep changes focused and explain their purpose.
- Add or update tests for behavior changes.
- Update public documentation when setup, supported behavior, configuration, or the protocol
  changes.
- Never include game binaries, saves, packages, decompiled game source, credentials, or personal
  machine paths.

For bugs and feature proposals, use [GitHub Issues](https://github.com/ObsidianNetwork/Sims3Online/issues)
or [Discussions](https://github.com/ObsidianNetwork/Sims3Online/discussions). Report security
issues privately as described in [docs/security.md](docs/security.md).

By contributing, you agree that your contributions are licensed under the repository's
[MIT License](LICENSE).
