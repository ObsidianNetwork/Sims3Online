import unittest

try:
    from .front_door_walk import accepted_command, failed_interaction, score
except ImportError:
    from front_door_walk import accepted_command, failed_interaction, score


class FrontDoorWalkTests(unittest.TestCase):
    def test_native_failure_must_match_the_actor_and_queued_gohere(self):
        rows = [{"kind": "interaction_queued", "payload": {"actorObjectId": 42, "interactionId": 7,
                 "interactionKey": "Sims3.Gameplay.Core.Terrain+GoHere+SameLotDefinition"}},
                {"kind": "interaction_failed", "payload": {"actorObjectId": 43, "interactionId": 7}},
                {"kind": "interaction_failed", "payload": {"actorObjectId": 42, "interactionId": 8}}]
        self.assertIsNone(failed_interaction(rows, 42, "accepted"))
        failure = {"actorObjectId": 42, "interactionId": 7, "reason": "native route failed"}
        rows.append({"kind": "interaction_failed", "payload": failure})
        self.assertEqual(failed_interaction(rows, 42, "accepted"), failure)
        self.assertIsNone(failed_interaction(rows, 42, None))

    def test_an_unrelated_or_unaccepted_command_never_counts(self):
        receipts = [{"kind": "intent_forwarded", "payload": {"actor": 42, "commandId": "a"}},
                    {"kind": "command_accepted", "payload": {"commandId": "b"}}]
        self.assertIsNone(accepted_command(receipts, 42))
        receipts.append({"kind": "command_accepted", "payload": {"commandId": "a"}})
        self.assertEqual(accepted_command(receipts, 42), "a")
        self.assertIsNone(accepted_command(receipts, 43))
        non_gohere = [{"kind": "intent_forwarded", "payload": {"actor": 42, "commandId": "c", "key": "Sims3.Gameplay.Objects.Electronics.PhoneSmart+BrowseTheWeb+Definition"}},
                      {"kind": "command_accepted", "payload": {"commandId": "c"}}]
        self.assertIsNone(accepted_command(non_gohere, 42))
        gohere = [{"kind": "user_intent_forwarded", "payload": {"actor": 42, "commandId": "d", "type": "Terrain+GoHere+SameLotDefinition", "name": "Go Here"}},
                  {"kind": "command_accepted", "payload": {"commandId": "d"}}]
        self.assertEqual(accepted_command(gohere, 42), "d")

    def test_real_command_movement_target_and_both_views_are_required(self):
        before, target = [0, 0, 0], [2, 0, 0]
        positions = [[2, 0, 0], [2.01, 0, 0], [1.99, 0, 0]]
        self.assertTrue(score(before, target, positions, "a")["passed"])
        self.assertFalse(score(before, target, positions, None)["passed"])
        self.assertFalse(score(target, target, positions, "a")["passed"])
        self.assertFalse(score(before, [4.1, 0, 0], positions, "a")["passed"])
        self.assertFalse(score(before, target, positions[:2], "a")["passed"])
        positions[2] = [2.2, 0, 0]
        self.assertFalse(score(before, target, positions, "a")["passed"])

    def test_native_gohere_arrival_radius_does_not_relax_client_convergence(self):
        positions = [[3.1, 0, 0], [3.11, 0, 0], [3.09, 0, 0]]
        measured = score([0, 0, 0], [5, 0, 0], positions, "actual")
        self.assertTrue(measured["passed"])
        self.assertEqual(measured["native_arrival_radius_m"], 2.0)
        self.assertFalse(score([0, 0, 0], [5.2, 0, 0], positions, "actual")["passed"])
        positions[2] = [3.3, 0, 0]
        self.assertFalse(score([0, 0, 0], [5, 0, 0], positions, "actual")["passed"])


if __name__ == "__main__":
    unittest.main()
