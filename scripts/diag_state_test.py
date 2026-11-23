import tempfile
import unittest
from pathlib import Path
from unittest import mock

from scripts.diag_state import (
    DiagInteractionState,
    SOCIALIZING_POSTURE,
    compare_interaction_states,
    is_social_route_key,
    parse_interaction_state_report,
)
from scripts.telemetry import TelemetryShim

_sink_dir: tempfile.TemporaryDirectory[str] | None = None


def setUpModule() -> None:
    # A failed comparison emits telemetry; keep it out of the working tree's .omo/run.
    global _sink_dir
    _sink_dir = tempfile.TemporaryDirectory()
    sink = Path(_sink_dir.name) / "telemetry-events.jsonl"
    patcher = mock.patch.object(TelemetryShim, "from_env", lambda: TelemetryShim("diag-state-test", sink_path=sink))
    patcher.start()
    unittest.addModuleCleanup(patcher.stop)
    unittest.addModuleCleanup(_sink_dir.cleanup)


def _state(**fields: str) -> DiagInteractionState:
    """Build a DiagInteractionState from already-decoded field values (skips hex decode)."""
    return DiagInteractionState(tuple(fields.items()))


class DiagInteractionStateTests(unittest.TestCase):
    def test_parse_decodes_hex_text_fields_and_numeric_progress(self) -> None:
        state = parse_interaction_state_report(
            "sim=18031287978496800304;"
            "name=4A656E6E69666572204D617274696E657A;"
            "role=server;"
            "postureType=53696D73332E47616D65706C61792E4163746F7253797374656D732E53697474696E67506F7374757265;"
            "postureContainerObjectId=7787005372684539664;"
            "activeInteractionKey=53696D73332E47616D65706C61792E4F626A656374732E52656164426F6F6B2B446566696E6974696F6E;"
            "activeTargetType=53696D73332E47616D65706C61792E4F626A656374732E426F6F6B47656E6572616C;"
            "activeBookId=PureLustGrantRodiek;"
            "activeProgressNormalized=0.0651184097;"
            "watchBookFound=1;"
            "watchBookId=PureLustGrantRodiek;"
            "watchBookProgressNormalized=;"
            "watchBookLength=310"
        )

        self.assertEqual(state.field("name"), "Jennifer Martinez")
        self.assertEqual(state.field("postureType"), "Sims3.Gameplay.ActorSystems.SittingPosture")
        self.assertEqual(state.field("activeInteractionKey"), "Sims3.Gameplay.Objects.ReadBook+Definition")
        self.assertEqual(state.field("activeTargetType"), "Sims3.Gameplay.Objects.BookGeneral")
        self.assertEqual(state.float_field("activeProgressNormalized"), 0.0651184097)
        self.assertIsNone(state.float_field("watchBookProgressNormalized"))

    def test_compare_accepts_matching_posture_book_and_close_progress(self) -> None:
        server = parse_interaction_state_report(
            "postureContainerObjectId=7787005372684539664;"
            "activeInteractionKey=53696D73332E47616D65706C61792E4F626A656374732E52656164426F6F6B2B446566696E6974696F6E;"
            "activeBookId=PureLustGrantRodiek;"
            "activeProgressNormalized=0.065;"
            "watchBookFound=1;"
            "watchBookId=PureLustGrantRodiek;"
            "watchBookProgressNormalized=0.065"
        )
        client = parse_interaction_state_report(
            "postureContainerObjectId=7787005372684539664;"
            "activeInteractionKey=53696D73332E47616D65706C61792E4F626A656374732E52656164426F6F6B2B446566696E6974696F6E;"
            "activeBookId=PureLustGrantRodiek;"
            "activeProgressNormalized=0.068;"
            "watchBookFound=1;"
            "watchBookId=PureLustGrantRodiek;"
            "watchBookProgressNormalized=0.068"
        )

        comparison = compare_interaction_states(server, client, progress_tolerance=0.01)

        self.assertTrue(comparison.ok)
        self.assertAlmostEqual(comparison.progress_delta or 0.0, 0.003)
        self.assertEqual(comparison.failures, ())

    def test_compare_rejects_posture_or_progress_divergence(self) -> None:
        server = parse_interaction_state_report(
            "postureContainerObjectId=7787005372684539664;"
            "activeInteractionKey=53696D73332E47616D65706C61792E4F626A656374732E52656164426F6F6B2B446566696E6974696F6E;"
            "activeBookId=PureLustGrantRodiek;"
            "activeProgressNormalized=0.10"
        )
        client = parse_interaction_state_report(
            "postureContainerObjectId=18031287978496800304;"
            "activeInteractionKey=53696D73332E47616D65706C61792E4F626A656374732E52656164426F6F6B2B446566696E6974696F6E;"
            "activeBookId=PureLustGrantRodiek;"
            "activeProgressNormalized=0.40"
        )

        comparison = compare_interaction_states(server, client, progress_tolerance=0.05)

        self.assertFalse(comparison.ok)
        self.assertIn("postureContainerObjectId", comparison.failures)
        self.assertIn("activeProgressNormalized", comparison.failures)


class SocialRouteOnlyAcceptanceTests(unittest.TestCase):
    """DEV-139 owner decision A: a host social-route key converges route-only -- its
    activeInteractionKey/activeQueueRole divergence is an EXPECTED degrade, but posture/book
    stay strict (so a DEV-102 SocializingPosture orphan and ReadBook/terminal regressions are
    still caught). Body-sync (posture/animation) is deferred to DEV-283."""

    SOCIAL_A = "Sims3.Gameplay.Socializing.SocialInteractionA+Definition"
    SOCIALIZE_WITH = "Sims3.Gameplay.Actors.Sim+SocializeWith+Definition"
    READBOOK = "Sims3.Gameplay.Objects.ReadBook+Definition"
    STANDING = "Sims3.Gameplay.Actors.Sim+StandingPosture"
    SOCIALIZING = SOCIALIZING_POSTURE  # single source of truth (pinned by SocializingPostureConstantTests)

    def test_is_social_route_key(self) -> None:
        self.assertTrue(is_social_route_key(self.SOCIAL_A))
        self.assertTrue(is_social_route_key("Sims3.Gameplay.Socializing.SocialInteractionB+Chat"))
        self.assertTrue(is_social_route_key(self.SOCIALIZE_WITH))
        self.assertFalse(is_social_route_key(self.READBOOK))
        self.assertFalse(is_social_route_key("Terrain.GoHere"))
        self.assertFalse(is_social_route_key(""))

    def test_social_route_key_divergence_is_expected_degrade_not_failure(self) -> None:
        # Host runs a social (Standing); client routed then idled -> no social instance.
        server = _state(
            postureType=self.STANDING,
            postureContainerObjectId="18031287978496800304",
            activeQueueRole="current",
            activeInteractionKey=self.SOCIAL_A,
        )
        client = _state(
            postureType=self.STANDING,
            postureContainerObjectId="18031287978496800304",
            activeQueueRole="none",
            activeInteractionKey="",
        )
        cmp = compare_interaction_states(server, client, progress_tolerance=0.05)
        self.assertTrue(cmp.ok)
        self.assertTrue(cmp.social_route)
        self.assertIn("activeInteractionKey", cmp.route_only_degrades)
        self.assertIn("activeQueueRole", cmp.route_only_degrades)
        self.assertEqual(cmp.failures, ())

    def test_social_route_keeps_posture_strict_catches_orphan(self) -> None:
        # Client wrongly entered SocializingPosture ( orphan) while host stayed Standing.
        server = _state(
            postureType=self.STANDING,
            activeQueueRole="current",
            activeInteractionKey=self.SOCIAL_A,
        )
        client = _state(
            postureType=self.SOCIALIZING,
            activeQueueRole="none",
            activeInteractionKey="",
        )
        cmp = compare_interaction_states(server, client, progress_tolerance=0.05)
        self.assertFalse(cmp.ok)
        self.assertTrue(cmp.social_route)
        self.assertIn("postureType", cmp.failures)

    def test_terminal_readbook_key_divergence_still_fails(self) -> None:
        # ReadBook is NOT social-route -> strict; a missing client interaction is a real failure.
        server = _state(
            postureType=self.STANDING,
            activeQueueRole="current",
            activeInteractionKey=self.READBOOK,
        )
        client = _state(
            postureType=self.STANDING,
            activeQueueRole="none",
            activeInteractionKey="",
        )
        cmp = compare_interaction_states(server, client, progress_tolerance=0.05)
        self.assertFalse(cmp.ok)
        self.assertFalse(cmp.social_route)
        self.assertIn("activeInteractionKey", cmp.failures)
        self.assertIn("activeQueueRole", cmp.failures)


class DirectionalPostureDegradeTests(unittest.TestCase):
    """DEV-139 re-run 2026-06-22 (owner-approved DIRECTIONAL only). During an ACTIVE social the host
    enters SocializingPosture while the route-only client correctly stays StandingPosture. The
    postureType/postureContainerObjectId divergence degrades to an EXPECTED route-only PASS ONLY in
    this direction. The reverse -- the client wrongly in SocializingPosture while the host is not --
    stays STRICT (DEV-102 orphan guard). Posture is intentionally NOT in ROUTE_ONLY_DEGRADE_FIELDS
    (that set degrades both directions and would disable the guard)."""

    SOCIAL_A = "Sims3.Gameplay.Socializing.SocialInteractionA+Definition"
    STANDING = "Sims3.Gameplay.Actors.Sim+StandingPosture"
    SOCIALIZING = SOCIALIZING_POSTURE  # single source of truth (pinned by SocializingPostureConstantTests)

    def test_forward_host_socializing_client_standing_passes(self) -> None:
        # Host actively socializing (SocializingPosture); client correctly route-only (Standing).
        # postureType + postureContainerObjectId diverge -> expected route-only PASS (degraded).
        server = _state(
            postureType=self.SOCIALIZING,
            postureContainerObjectId="18031287978496800304",  # social target/conversation
            activeQueueRole="current",
            activeInteractionKey=self.SOCIAL_A,
        )
        client = _state(
            postureType=self.STANDING,
            postureContainerObjectId="7787005372684539664",  # different -- client standing near sim
            activeQueueRole="none",
            activeInteractionKey="",
        )
        cmp = compare_interaction_states(server, client, progress_tolerance=0.05)
        self.assertTrue(cmp.ok, msg=f"expected route-only PASS, failures={cmp.failures}")
        self.assertTrue(cmp.social_route)
        self.assertIn("postureType", cmp.route_only_degrades)
        self.assertIn("postureContainerObjectId", cmp.route_only_degrades)
        self.assertIn("activeInteractionKey", cmp.route_only_degrades)
        self.assertIn("activeQueueRole", cmp.route_only_degrades)
        self.assertEqual(cmp.failures, ())

    def test_reverse_client_socializing_host_not_still_fails(self) -> None:
        # guard: client WRONGLY in SocializingPosture while host is Standing (not socializing)
        # -> STRICT FAIL on postureType AND postureContainerObjectId. Directionality is load-bearing:
        # the forward degrade must NOT trigger here (host posture is not SocializingPosture).
        server = _state(
            postureType=self.STANDING,
            postureContainerObjectId="7787005372684539664",
            activeQueueRole="current",
            activeInteractionKey=self.SOCIAL_A,
        )
        client = _state(
            postureType=self.SOCIALIZING,
            postureContainerObjectId="18031287978496800304",
            activeQueueRole="none",
            activeInteractionKey="",
        )
        cmp = compare_interaction_states(server, client, progress_tolerance=0.05)
        self.assertFalse(cmp.ok)
        self.assertTrue(cmp.social_route)
        self.assertIn("postureType", cmp.failures)
        self.assertIn("postureContainerObjectId", cmp.failures)
        # neither posture field may be degraded in the reverse direction
        self.assertNotIn("postureType", cmp.route_only_degrades)
        self.assertNotIn("postureContainerObjectId", cmp.route_only_degrades)


class SocializingPostureConstantTests(unittest.TestCase):
    """Pin SOCIALIZING_POSTURE to the live TS3 type. The directional posture degrade keys off an
    EXACT match against this constant, so a wrong value silently disables it (the DEV-139 stage-2
    false-fail, 2026-06-23: a prior value used Sims3.Gameplay.ActorSystems.SocializingPosture, which
    does not exist -- the degrade never matched live posture, postureType stayed strict, and social
    rows false-failed while the unit suite stayed green). This guard locks the constant to the
    decompiled/verified type so that failure mode cannot recur silently."""

    def test_constant_equals_live_socializing_posture_type(self) -> None:
        # Decompiled: Sims3.Gameplay.Actors.SocializingPosture nested in Sim (Sim.cs:46468). A nested
        # type's FullName uses '+', so sim.Posture.GetType().FullName == Sims3.Gameplay.Actors.Sim+
        # SocializingPosture. Verified live on the two-player stack (host SocialInteractionA/B).
        self.assertEqual(
            SOCIALIZING_POSTURE,
            "Sims3.Gameplay.Actors.Sim+SocializingPosture",
            msg="SOCIALIZING_POSTURE drifted from the live TS3 type; the directional degrade would "
            "silently stop matching -- re-verify against sim.Posture.GetType().FullName and Sim.cs.",
        )


if __name__ == "__main__":
    unittest.main()
