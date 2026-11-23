"""Which client game the comparison tools pair with the server.

    SIMSONLINE_COMPARE_CLIENT=2 python scripts/motion_watch.py Pablo --seconds 20   # player 2's game
    python scripts/motion_watch.py Pablo --client 2                                 # the same
    python scripts/scenarios.py --client 2 run sleep-bed                            # a scenario run against player 2

Client 1 (player 1, Client2Box) uses SIMSONLINE_CLIENT_BRIDGECTL or 127.0.0.1:34030.
Client 2 (player 2, Client3Box) answers on 127.0.0.1:34031,
or SIMSONLINE_CLIENT2_BRIDGECTL as `stackctl.py boot-client2` uses it. Client N >= 3 (player 3, Client4Box, )
answers on 127.0.0.1:<34029 + N>, or SIMSONLINE_CLIENT<N>_BRIDGECTL (`stackctl.py boot-client3`). motion_watch, sim_compare, tune and scenarios
read the choice. A --client option also sets SIMSONLINE_COMPARE_CLIENT, so the tools a run starts follow it.

To watch several clients at once, run one watch per client. A client-N watch's motion folder ends in -cN (N >= 2), so
two watches started in the same second do not share a folder.
"""

from __future__ import annotations

import os

if __package__:
    from .local_endpoints import CLIENT_BRIDGECTL
else:
    from local_endpoints import CLIENT_BRIDGECTL

CLIENT1 = CLIENT_BRIDGECTL
ENV = "SIMSONLINE_COMPARE_CLIENT"
# The players the stack can boot (stackctl.CLIENT_PLAYERS), as the strings steps and options name them.
PLAYERS = ("1", "2", "3")


def client_n(player: int | str) -> str:
    """Client 1 uses the shared setting; later players use SIMSONLINE_CLIENT<N>_BRIDGECTL or 34029 + N."""
    n = int(player)
    if n == 1:
        return CLIENT1
    return os.environ.get(f"SIMSONLINE_CLIENT{n}_BRIDGECTL") or f"127.0.0.1:{34029 + n}"


def client2() -> str:
    return client_n(2)


def clients() -> list[str]:
    """Every player's client endpoint, player 1 first."""
    return [client_n(p) for p in PLAYERS]


def player_of(chosen: str) -> str | None:
    """'1', '2', '3' for a player's endpoint, None for any other."""
    return next((p for p in PLAYERS if client_n(p) == chosen), None)


def resolve(value: str | None) -> str:
    """'1', '2', '3' or HOST:PORT. Empty or None means client 1."""
    text = (value or "").strip()
    if text == "":
        return CLIENT1
    if text in PLAYERS:
        return client_n(text)
    if ":" not in text:
        raise ValueError(f"client {value!r}: give {', '.join(PLAYERS)} or HOST:PORT")
    return text


def endpoint() -> str:
    """The client the tools compare now: SIMSONLINE_COMPARE_CLIENT, else client 1."""
    return resolve(os.environ.get(ENV))


def use(value: str) -> str:
    """Choose the client for this process and the tools it starts. Returns its endpoint."""
    chosen = resolve(value)
    os.environ[ENV] = chosen
    return chosen


def label(chosen: str | None = None) -> str:
    """A folder suffix that keeps watches of different clients apart: '' for client 1, '-cN' for player N, else
    '-c<port>'."""
    chosen = chosen or endpoint()
    player = player_of(chosen)
    if player == "1":
        return ""
    if player:
        return "-c" + player
    return "-c" + chosen.rsplit(":", 1)[-1]


def name(chosen: str | None = None) -> str:
    """'client 1', 'client 2', 'client 3' or 'client HOST:PORT', for a report line."""
    chosen = chosen or endpoint()
    return "client " + (player_of(chosen) or chosen)
