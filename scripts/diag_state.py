from __future__ import annotations

from dataclasses import dataclass
from typing import Final


HEX_TEXT_FIELDS: Final[frozenset[str]] = frozenset(
    {
        "name",
        "postureType",
        "activeInteractionKey",
        "activeInteractionType",
        "activeTargetType",
    }
)

EXACT_COMPARE_FIELDS: Final[tuple[str, ...]] = (
    "postureType",
    "postureContainerObjectId",
    "activeQueueRole",
    "activeInteractionKey",
    "activeBookId",
    "watchBookFound",
    "watchBookId",
)

# route-only social acceptance (owner decision A, 2026-06-21).
# Social interactions (SocializeWith / SocialInteractionA/B) mirror as GoHere-to-target ONLY
# (see internal documentation). The parameterized social instance is NOT
# reconstructable on the client (no Singleton) and its SocializingPosture is SACS-backed
# (forcing it is the freeze). So when the HOST is running a social-route interaction,
# the client legitimately has no matching social instance: activeInteractionKey/activeQueueRole
# WILL diverge and that is an EXPECTED route-only degrade, not a failure. Full posture/animation
# fidelity is deferred to the body-sync milestone.
#
# Mirror of SimsOnlineServerHostMod/TerminalInteractionFilter.cs IsSocialApproachReplay
# (SocialRouteDefinitionPrefixes + SocializeWith). Keep in sync with that file.
SOCIAL_ROUTE_PREFIXES: Final[tuple[str, ...]] = (
    "Sims3.Gameplay.Socializing.SocialInteractionA+",
    "Sims3.Gameplay.Socializing.SocialInteractionB+",
    "Sims3.Gameplay.Actors.Sim+SocializeWith+",
)

# Fields whose divergence is an EXPECTED degrade when the HOST key is social-route. SCOPED on
# purpose -- posture/book/progress stay STRICT so a wrongly-forced SocializingPosture orphan
# or a terminal/ReadBook regression is still caught.
ROUTE_ONLY_DEGRADE_FIELDS: Final[frozenset[str]] = frozenset(
    {"activeInteractionKey", "activeQueueRole"}
)

# Fully-qualified SocializingPosture type name. The posture a Sim adopts during a social is the
# NESTED type Sims3.Gameplay.Actors.Sim+SocializingPosture (decompiled at Sim.cs:46468; verified live
# via sim.Posture.GetType().FullName, stage-2 2026-06-23). Forcing it as data is the
# orphan-freeze hazard (internal documentation). NOTE: a prior value used
# Sims3.Gameplay.ActorSystems.SocializingPosture, which does NOT exist -- the directional degrade
# silently never matched live posture, so postureType stayed strict and social rows false-failed
# until corrected here. Guard-pinned in diag_state_test.py (SocializingPostureConstantTests).
SOCIALIZING_POSTURE: Final[str] = "Sims3.Gameplay.Actors.Sim+SocializingPosture"

# directional posture-degrade (re-run 2026-06-22, owner-approved DIRECTIONAL only). During
# an ACTIVE social the host enters SocializingPosture while the route-only client correctly stays
# StandingPosture. The resulting postureType/postureContainerObjectId divergence is an EXPECTED
# route-only PASS, but ONLY in this direction (host socializing, client not). The reverse -- the
# client wrongly in SocializingPosture while the host is not -- stays STRICT ( orphan guard).
# These MUST NOT be added to ROUTE_ONLY_DEGRADE_FIELDS: that set degrades unconditionally in BOTH
# directions and would disable the guard. This is the directional special-case. See
# internal documentation (reconciliation + body-sync deferral).
SOCIAL_POSTURE_DEGRADE_FIELDS: Final[frozenset[str]] = frozenset(
    {"postureType", "postureContainerObjectId"}
)


def is_social_route_key(interaction_key: str) -> bool:
    """True if interaction_key is a social-approach/route interaction mirrored as GoHere-only.

    Mirrors TerminalInteractionFilter.IsSocialApproachReplay (mod side, C#2)."""
    if not interaction_key:
        return False
    for prefix in SOCIAL_ROUTE_PREFIXES:
        if interaction_key == prefix + "Definition" or interaction_key.startswith(prefix):
            return True
    return False


@dataclass(frozen=True, slots=True)
class DiagInteractionState:
    fields: tuple[tuple[str, str], ...]

    def field(self, name: str, default: str = "") -> str:
        for key, value in self.fields:
            if key == name:
                return value
        return default

    def float_field(self, name: str) -> float | None:
        value = self.field(name)
        if not value:
            return None
        try:
            return float(value)
        except ValueError:
            return None

    def to_dict(self) -> dict[str, str]:
        return dict(self.fields)


@dataclass(frozen=True, slots=True)
class DiagStateCheck:
    field: str
    server: str
    client: str
    ok: bool

    def to_dict(self) -> dict[str, str | bool]:
        return {
            "field": self.field,
            "server": self.server,
            "client": self.client,
            "ok": self.ok,
        }


@dataclass(frozen=True, slots=True)
class DiagStateComparison:
    ok: bool
    checks: tuple[DiagStateCheck, ...]
    failures: tuple[str, ...]
    progress_field: str
    progress_delta: float | None
    social_route: bool = False
    route_only_degrades: tuple[str, ...] = ()

    def to_dict(self) -> dict[str, bool | float | None | str | list[dict[str, str | bool]] | list[str]]:
        return {
            "ok": self.ok,
            "checks": [check.to_dict() for check in self.checks],
            "failures": list(self.failures),
            "progressField": self.progress_field,
            "progressDelta": self.progress_delta,
            "socialRoute": self.social_route,
            "routeOnlyDegrades": list(self.route_only_degrades),
        }


def parse_interaction_state_report(report: str) -> DiagInteractionState:
    return DiagInteractionState(tuple((key, _decode_field(key, value)) for key, value in _split_kv(report)))


def compare_interaction_states(
    server: DiagInteractionState,
    client: DiagInteractionState,
    progress_tolerance: float,
) -> DiagStateComparison:
    checks: list[DiagStateCheck] = []
    failures: list[str] = []
    degrades: list[str] = []

    # Route-only social acceptance keys off the HOST (authoritative) interaction key.
    social_route = is_social_route_key(server.field("activeInteractionKey"))

    # directional posture-degrade (re-run 2026-06-22, directional only): during an ACTIVE
    # social the host enters SocializingPosture while the route-only client correctly stays
    # StandingPosture. Degrade postureType/postureContainerObjectId to an expected route-only PASS
    # ONLY in this direction. The reverse -- client wrongly in SocializingPosture while the host is
    # not -- stays STRICT ( orphan guard). Posture is intentionally NOT in
    # ROUTE_ONLY_DEGRADE_FIELDS (that set degrades both directions and would disable the guard).
    social_posture_degrade = (
        social_route
        and server.field("postureType") == SOCIALIZING_POSTURE
        and client.field("postureType") != SOCIALIZING_POSTURE
    )

    for field_name in EXACT_COMPARE_FIELDS:
        server_value = server.field(field_name)
        client_value = client.field(field_name)
        if not server_value and not client_value:
            continue
        ok = server_value == client_value
        if not ok and social_route and field_name in ROUTE_ONLY_DEGRADE_FIELDS:
            # Host runs a social, client routes/idles -> expected route-only degrade ( A).
            # Recorded as ok so the social row can PASS, but tracked in route_only_degrades and
            # NOT counted as a failure. Posture/book/progress stay strict (catch orphan).
            checks.append(DiagStateCheck(field_name, server_value, client_value, True))
            degrades.append(field_name)
            continue
        if not ok and social_posture_degrade and field_name in SOCIAL_POSTURE_DEGRADE_FIELDS:
            # Host actively socializing (SocializingPosture), client correctly route-only (Standing)
            # -> postureType/postureContainerObjectId divergence is an EXPECTED route-only PASS. Only
            # this direction; the reverse (client orphan SocializingPosture) is NOT degraded above and
            # falls through to a strict FAIL, preserving the guard.
            checks.append(DiagStateCheck(field_name, server_value, client_value, True))
            degrades.append(field_name)
            continue
        checks.append(DiagStateCheck(field_name, server_value, client_value, ok))
        if not ok:
            failures.append(field_name)

    progress_field, progress_delta, progress_ok = _compare_progress(server, client, progress_tolerance)
    if progress_field:
        checks.append(
            DiagStateCheck(
                progress_field,
                server.field(progress_field),
                client.field(progress_field),
                progress_ok,
            )
        )
        if not progress_ok:
            failures.append(progress_field)

    comparison = DiagStateComparison(
        ok=len(failures) == 0,
        checks=tuple(checks),
        failures=tuple(failures),
        progress_field=progress_field,
        progress_delta=progress_delta,
        social_route=social_route,
        route_only_degrades=tuple(degrades),
    )
    # emit matrix_failed telemetry when state comparison fails
    if not comparison.ok:
        try:
            import os as _os
            import sys as _sys
            _sys.path.insert(0, _os.path.dirname(_os.path.dirname(_os.path.abspath(__file__))))
            from scripts.telemetry import TelemetryShim
            TelemetryShim.from_env().emit("validation.matrix_failed", {"check_name": "interaction_state_compare", "matched_count": len(checks) - len(failures), "mismatched_count": len(failures), "detail": ",".join(failures[:5])})
        except Exception:
            pass
    return comparison


def _split_kv(segment: str) -> tuple[tuple[str, str], ...]:
    pairs: list[tuple[str, str]] = []
    for piece in segment.split(";"):
        piece = piece.strip()
        if not piece:
            continue
        key, sep, value = piece.partition("=")
        pairs.append((key.strip(), value.strip()) if sep else (piece, ""))
    return tuple(pairs)


def _decode_field(key: str, value: str) -> str:
    if key not in HEX_TEXT_FIELDS or not value or len(value) % 2 != 0:
        return value
    try:
        return bytes.fromhex(value).decode("utf-8")
    except (ValueError, UnicodeDecodeError):
        return value


def _compare_progress(
    server: DiagInteractionState,
    client: DiagInteractionState,
    tolerance: float,
) -> tuple[str, float | None, bool]:
    for field_name in ("watchBookProgressNormalized", "activeProgressNormalized"):
        server_has = bool(server.field(field_name))
        client_has = bool(client.field(field_name))
        if not server_has and not client_has:
            continue

        server_progress = server.float_field(field_name)
        client_progress = client.float_field(field_name)
        if server_progress is None or client_progress is None:
            return field_name, None, False

        delta = abs(server_progress - client_progress)
        return field_name, delta, delta <= tolerance

    return "", None, True
