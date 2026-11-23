<!-- Synthetic scorecard fixture for current Rust state ownership boundaries.
This is not a complete state inventory or a restored planning document. -->

| State field | Owner | Classification | Invalidation |
|---|---|---|---|
| `Server::conns` | `Server` | single-owner | disconnect |
| `Server::player_active` | `Server` | single-owner | leave or disconnect |
| `Server::ids` | `Server` | single-owner | server restart |
| `Connection::window` | `Connection` | single-owner | eviction or disconnect |
