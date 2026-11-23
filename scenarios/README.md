# Presentation scenarios

The JSON files in this directory describe repeatable in-game checks for the presentation
layer. They run against a locally configured development stack and require a compatible
copy of The Sims 3. Scenario tests are not part of the ordinary offline unit-test suite.

List available scenarios with:

```text
python scripts/scenarios.py list
```

Run one or more scenarios against an already-running stack with:

```text
python scripts/scenarios.py run <scenario-name>
```

See the [development guide](../docs/development.md) and [testing guide](../docs/testing.md)
for setup, validation, and the project's current limitations. Do not commit saves, game
files, or generated run artifacts.
