#![forbid(unsafe_code)]
//! Interaction queue FSM and client-authority gates (spec card 08, /
//!). The queue is a deterministic per-Sim state machine.
//!
//! a client's own game on a puppet never autonomously enqueues
//! gameplay — only server-authoritative producers pass the insert gate, and an
//! unclaimed Sim cannot queue at all.
//!
//! NeverReplay (card 08 §B): `ReactionInteraction` is a presentation-replay
//! policy — a `PresentationReplay` producer is refused with
//! `Err(NeverReplay)`; the server may still execute reactions
//! (`ServerCommand` / `ServerAutonomy` pass the gate).
//!
//! a mid-social claim swap cancels every queued interaction touching
//! the swapped pair, in deterministic `SimId`-ascending order, emitting each
//! observable effect exactly once: the reservation release, the posture
//! teardown, `FailureExplanation::CancelledByScript`, and the paired
//! `RouteStopEventType::Cancelled`.

use std::collections::BTreeSet;

use crate::authority::ServerHouseholdAuthority;
use crate::handles::KernelError;
use crate::SimId;

/// Who produced an interaction. `ClientAutonomy` and `ClientLocalRandom` are
/// forbidden on the kernel.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum InteractionProducer {
    ServerCommand,
    ServerAutonomy,
    ClientAutonomy,
    ClientLocalRandom,
    PresentationReplay,
}

/// The interaction families the client is allowed to enqueue at all (card 08 §F).
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum InteractionFamilyTag {
    ReadBook,
    ReadBookChooser,
    GetBook,
    BookshelfReadSomething,
    PutBookDown,
    Sit,
    Stand,
    InteractionsSharedSit,
    InteractionsSharedStand,
    GoHome,
    VisitCommunityLot,
    LeaderGoHereWith,
    FollowerMeetUpAtPoint,
    TerrainGoHere,
    PhoneBrowseTheWeb,
    PhoneStreamVideo,
    SocializeWith,
    SocialInteractionA,
    SocialInteractionB,
    LeaveConversationImmediately,
    ReactionInteraction,
    BedSleep,
    SleepingPosture,
    RouteAway,
    EnterRelaxing,
    UseToilet,
    PillowFight,
    FutureSkillInspect,
    Unknown,
}

impl InteractionFamilyTag {
    /// The social families a claim swap must re-host (card 08 §D.2).
    pub fn is_social(self) -> bool {
        matches!(
            self,
            InteractionFamilyTag::SocializeWith
                | InteractionFamilyTag::SocialInteractionA
                | InteractionFamilyTag::SocialInteractionB
                | InteractionFamilyTag::PillowFight
        )
    }

    /// The NeverReplay registry entry (card 08 §B step 1,
    /// `terminal-interaction-registry.md`): client-generated reactions differ
    /// from the host, so a presentation replay of this family is refused.
    pub fn is_never_replay(self) -> bool {
        matches!(self, InteractionFamilyTag::ReactionInteraction)
    }
}

/// Route-stop / failure explanation bits (card 08 §A.1, §D).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u32)]
pub enum FailureExplanation {
    CancelledByScript = 1,
    InitialPlanFailure = 2,
    InternalPlanFailure = 4,
    ReplanFailure = 8,
    VehicleSequenceFailure = 0x10,
    PortalFailure = 0x20,
    DestinationObstructed = 0x40,
    DynamicAdjustmentFailed = 0x80,
}

/// The route-stop event that releases a reservation (card 03 §C).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RouteStopEventType {
    Unknown,
    Failure,
    Cancelled,
    AtDestination,
    DestinationObstructed,
    EncounteredObstacle,
    AtRouteEndDestination,
}

/// Hard cap on per-Sim queue entries (`InteractionQueue.cs:299`).
pub const QUEUE_HARD_CAP: usize = 200;

/// A queued interaction. `test_passes` models the native `Definition.Test()`
/// result (the insert-time gate); the kernel rejects by policy, not by
/// re-running the native test.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct InteractionInstance {
    pub id: u64,
    pub family: InteractionFamilyTag,
    pub instance_actor: SimId,
    pub target_sim: Option<SimId>,
    pub priority: u32,
    pub test_passes: bool,
}

/// One observable effect of a queue mutation, in deterministic emission order.
/// The claim-swap cleanup emits each of these exactly once per cancelled
/// interaction (card 08 §D.3 invariants 2 and 5).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum QueueEffect {
    /// The strictly-greater preemption sweep dropped a queued entry.
    Preempted { id: u64 },
    /// The swap cancelled a queued interaction.
    Cancelled {
        id: u64,
        sim: SimId,
        explanation: FailureExplanation,
        route_stop: RouteStopEventType,
    },
    /// The cancelled interaction's slot / social-partner reservation is
    /// released (card 03 §C).
    ReservationReleased { id: u64 },
    /// The cancelled interaction's posture container is torn down on both
    /// sides (`CancelSocializing`, `socializing-posture.md`).
    PostureTornDown { id: u64 },
}

/// A per-Sim interaction queue with the authority gate, the
/// NeverReplay presentation gate, the insert-time `Test()` gate, the
/// strictly-greater preemption sweep, and the claim-swap handoff.
#[derive(Default, Debug)]
pub struct InteractionQueue {
    entries: Vec<InteractionInstance>,
    /// `mIsHeadInteractionLocked` (card 08 §A.1 phase 3): while the head is
    /// locked, a new entry cannot take index 0 — it lands at index 1.
    head_locked: bool,
    /// Deterministic effect trace; every observable mutation is recorded here
    /// exactly once, in emission order.
    effects: Vec<QueueEffect>,
}

impl InteractionQueue {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn len(&self) -> usize {
        self.entries.len()
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    pub fn head(&self) -> Option<&InteractionInstance> {
        self.entries.first()
    }

    /// The entries in queue order (index 0 is the head).
    pub fn entries(&self) -> &[InteractionInstance] {
        &self.entries
    }

    /// The deterministic effect trace (card 08 §D.3: both processes arrive at
    /// the same trace).
    pub fn effects(&self) -> &[QueueEffect] {
        &self.effects
    }

    /// `mIsHeadInteractionLocked` (card 08 §A.1 phase 3): set while the head
    /// is being processed so the head cannot be reordered out from under it.
    pub fn lock_head(&mut self) {
        self.head_locked = true;
    }

    /// Release the head lock (the head dequeued, cancelled, or failed).
    pub fn unlock_head(&mut self) {
        self.head_locked = false;
    }

    /// Insert `entry` at `index`. Gate order (card 08 §C, §A.2):
    /// 1. producer gate — client producers are refused outright.
    /// 2. NeverReplay gate — a `PresentationReplay` of a NeverReplay family is
    ///    refused; the server may execute reactions.
    /// 3. Hard cap 200 (`InteractionQueue.cs:299`).
    /// 4. Insert-time `Test()` gate (`InteractionInstance.cs:2151`).
    /// 5. Locked-head ordering — a locked head keeps index 0; the entry lands
    ///    at index 1 (the `AddNext` / continuation position).
    /// 6. Strictly-greater preemption sweep (`RemoveLowerPriorityInteractions`).
    pub fn insert(
        &mut self,
        entry: InteractionInstance,
        index: usize,
        producer: InteractionProducer,
        _authority: &ServerHouseholdAuthority,
    ) -> Result<(), KernelError> {
        // reject non-server producers outright.
        if !matches!(
            producer,
            InteractionProducer::ServerCommand
                | InteractionProducer::ServerAutonomy
                | InteractionProducer::PresentationReplay
        ) {
            return Err(KernelError::UnauthorizedClientInsertion {
                producer,
                family: entry.family,
            });
        }
        // NeverReplay is a presentation-replay policy: only a replay producer
        // is refused; the server may execute reactions (card 08 §B step 1).
        if entry.family.is_never_replay() && producer == InteractionProducer::PresentationReplay {
            return Err(KernelError::NeverReplay(entry.family));
        }
        if self.entries.len() >= QUEUE_HARD_CAP {
            return Err(KernelError::QueueFull);
        }
        // Insert-time Test() gate (InteractionInstance.cs:2151,
        // InteractionQueue.cs:320).
        if !entry.test_passes {
            return Err(KernelError::TestRejected);
        }
        // Locked-head ordering: the locked head keeps index 0; a new entry
        // requested at the head lands at index 1 instead.
        let idx = if self.head_locked && index == 0 {
            1
        } else {
            index
        }
        .min(self.entries.len());
        self.entries.insert(idx, entry);
        self.remove_lower_priority_interactions();
        Ok(())
    }

    /// `AddNext` (card 08 §A.2): insert behind the (possibly locked) head.
    pub fn add_next(
        &mut self,
        entry: InteractionInstance,
        producer: InteractionProducer,
        authority: &ServerHouseholdAuthority,
    ) -> Result<(), KernelError> {
        self.insert(entry, 1, producer, authority)
    }

    /// `PushAsContinuation` (card 08 §A.2, `InteractionQueue.cs:490`): the
    /// continuation runs after its trigger — it is inserted at index 1 and
    /// respects `mIsHeadInteractionLocked` (a locked head keeps index 0).
    pub fn push_as_continuation(
        &mut self,
        trigger: u64,
        entry: InteractionInstance,
        producer: InteractionProducer,
        authority: &ServerHouseholdAuthority,
    ) -> Result<(), KernelError> {
        let index = self.entries.iter().position(|e| e.id == trigger)
            .ok_or(KernelError::TestRejected)? + 1;
        self.insert(entry, index, producer, authority)
    }

    /// Strictly-greater preemption (`RemoveLowerPriorityInteractions`,
    /// `autonomy-queue.md`): a numerically higher priority value is a lower
    /// priority, and any queued entry strictly lower than the head is dropped.
    /// Equal priority never cancels. Runs on every successful insert.
    fn remove_lower_priority_interactions(&mut self) {
        if let Some(&head) = self.entries.first() {
            let head_priority = head.priority;
            let mut kept = Vec::with_capacity(self.entries.len());
            for e in self.entries.drain(..) {
                if e.priority > head_priority {
                    self.effects.push(QueueEffect::Preempted { id: e.id });
                } else {
                    kept.push(e);
                }
            }
            self.entries = kept;
        }
    }

    /// re-host the queue after a claim swap (card 08 §D.2).
    ///
    /// Every queued social whose `instance_actor` or `target_sim` touches
    /// the swapped pair is cancelled — symmetric in the swap direction — in
    /// deterministic `SimId`-ascending order (the smaller SimId's side first,
    /// so both processes arrive at the same trace). Each cancelled interaction
    /// emits, exactly once: the `CancelledByScript` failure explanation, the
    /// paired `RouteStopEventType::Cancelled`, the reservation release, and the
    /// posture-container teardown. The swap never re-enqueues on the new
    /// Sim's queue (the swap author is the player, not autonomy) and never
    /// runs client autonomy ( still holds).
    pub fn claim_swap(
        &mut self,
        old_sim: SimId,
        new_sim: SimId,
        authority: &mut ServerHouseholdAuthority,
    ) -> Result<(), KernelError> {
        let first = if old_sim <= new_sim { old_sim } else { new_sim };
        let second = if old_sim <= new_sim { new_sim } else { old_sim };
        for sim in [first, second] {
            let mut i = 0;
            while i < self.entries.len() {
                let touches = self.entries[i].family.is_social()
                    && (self.entries[i].instance_actor == sim
                        || self.entries[i].target_sim == Some(sim));
                if touches {
                    let entry = self.entries.remove(i);
                    self.emit_cancellation(entry, sim);
                } else {
                    i += 1;
                }
            }
        }
        if self.entries.is_empty() {
            self.head_locked = false;
        }
        authority.swap_claim(old_sim, new_sim);
        Ok(())
    }

    /// Emit the four observable effects of one cancellation, in order,
    /// exactly once (card 08 §D.3 invariants 2 and 5).
    fn emit_cancellation(&mut self, entry: InteractionInstance, sim: SimId) {
        self.effects.push(QueueEffect::Cancelled {
            id: entry.id,
            sim,
            explanation: FailureExplanation::CancelledByScript,
            route_stop: RouteStopEventType::Cancelled,
        });
        self.effects.push(QueueEffect::ReservationReleased { id: entry.id });
        self.effects.push(QueueEffect::PostureTornDown { id: entry.id });
    }

    /// The set of social families present in the queue (for reservation
    /// release bookkeeping).
    pub fn social_heads(&self) -> BTreeSet<SimId> {
        self.entries
            .iter()
            .filter(|e| e.family.is_social())
            .map(|e| e.instance_actor)
            .collect()
    }
}

/// Card 26 §C golden-corpus hook (canary 8, +).
#[cfg(test)]
mod kdiff {
    use super::*;

    #[test]
    fn kdiff_canary_08() {
        let mut a = ServerHouseholdAuthority::default();
        a.claimed_sims.insert(SimId(1));
        a.claimed_sims.insert(SimId(2));
        let mut q = InteractionQueue::new();
        let social = InteractionInstance {
            id: 1, family: InteractionFamilyTag::SocializeWith,
            instance_actor: SimId(1), target_sim: Some(SimId(2)),
            priority: 5, test_passes: true,
        };
        q.insert(social, 0, InteractionProducer::ServerCommand, &a).unwrap();
        let client_accepted = q
            .insert(social, 0, InteractionProducer::ClientAutonomy, &a)
            .is_ok() as u64;
        println!("KDIFF|canary=8|case=client_insert_rejected|got={:016x}", client_accepted);
        assert_eq!(client_accepted, 0);
        q.claim_swap(SimId(1), SimId(2), &mut a).unwrap();
        let torn_down = q
            .effects()
            .iter()
            .filter(|e| matches!(e, QueueEffect::PostureTornDown { .. }))
            .count() as u64;
        println!("KDIFF|canary=8|case=swap_posture_teardown_once|got={:016x}", torn_down);
        assert_eq!(torn_down, 1);
        assert!(q.is_empty());
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn inst(
        id: u64,
        family: InteractionFamilyTag,
        actor: SimId,
        target: Option<SimId>,
        priority: u32,
        passes: bool,
    ) -> InteractionInstance {
        InteractionInstance {
            id,
            family,
            instance_actor: actor,
            target_sim: target,
            priority,
            test_passes: passes,
        }
    }

    /// Authority with `SimId(1)` and `SimId(2)` claimed (the
    /// Pablo / Iliana pair).
    fn authority_pair() -> ServerHouseholdAuthority {
        let mut a = ServerHouseholdAuthority::default();
        a.claimed_sims.insert(SimId(1));
        a.claimed_sims.insert(SimId(2));
        a
    }

    #[test]
    fn queue_cap_200_rejects() {
        let mut q = InteractionQueue::new();
        let a = authority_pair();
        for i in 0..QUEUE_HARD_CAP {
            q.insert(
                inst(i as u64, InteractionFamilyTag::Sit, SimId(1), None, 10, true),
                0,
                InteractionProducer::ServerCommand,
                &a,
            )
            .unwrap();
        }
        assert_eq!(
            q.insert(
                inst(999, InteractionFamilyTag::Sit, SimId(1), None, 10, true),
                0,
                InteractionProducer::ServerCommand,
                &a
            ),
            Err(KernelError::QueueFull)
        );
    }

    #[test]
    fn sim174_client_autonomy_rejected() {
        let mut q = InteractionQueue::new();
        let a = authority_pair();
        let e = inst(1, InteractionFamilyTag::BedSleep, SimId(1), None, 5, true);
        assert_eq!(
            q.insert(e, 0, InteractionProducer::ClientAutonomy, &a),
            Err(KernelError::UnauthorizedClientInsertion {
                producer: InteractionProducer::ClientAutonomy,
                family: InteractionFamilyTag::BedSleep
            })
        );
    }

    #[test]
    fn sim174_route_away_rejected() {
        let mut q = InteractionQueue::new();
        let a = authority_pair();
        let e = inst(1, InteractionFamilyTag::RouteAway, SimId(1), None, 5, true);
        assert_eq!(
            q.insert(e, 0, InteractionProducer::ClientAutonomy, &a),
            Err(KernelError::UnauthorizedClientInsertion {
                producer: InteractionProducer::ClientAutonomy,
                family: InteractionFamilyTag::RouteAway
            })
        );
    }

    #[test]
    fn sim174_reaction_rejected() {
        let mut q = InteractionQueue::new();
        let a = authority_pair();
        let e = inst(1, InteractionFamilyTag::ReactionInteraction, SimId(1), None, 5, true);
        assert_eq!(
            q.insert(e, 0, InteractionProducer::ClientLocalRandom, &a),
            Err(KernelError::UnauthorizedClientInsertion {
                producer: InteractionProducer::ClientLocalRandom,
                family: InteractionFamilyTag::ReactionInteraction
            })
        );
    }

    #[test]
    fn sim174_server_autonomy_allowed() {
        let mut q = InteractionQueue::new();
        let a = authority_pair();
        let e = inst(1, InteractionFamilyTag::ReadBook, SimId(1), None, 5, true);
        assert_eq!(
            q.insert(e, 0, InteractionProducer::ServerAutonomy, &a),
            Ok(())
        );
    }

    #[test]
    fn insert_test_rejects_when_failing() {
        let mut q = InteractionQueue::new();
        let a = authority_pair();
        let e = inst(1, InteractionFamilyTag::ReadBook, SimId(1), None, 5, false);
        assert_eq!(
            q.insert(e, 0, InteractionProducer::ServerCommand, &a),
            Err(KernelError::TestRejected)
        );
    }

    #[test]
  fn server_autonomy_can_queue_for_unclaimed_sim() {
        let mut q = InteractionQueue::new();
        let a = authority_pair(); // only SimId(1) and SimId(2) claimed
        let e = inst(1, InteractionFamilyTag::ReadBook, SimId(9), None, 5, true);
        assert_eq!(
      q.insert(e, 0, InteractionProducer::ServerAutonomy, &a),
      Ok(())
        );
    assert_eq!(q.len(), 1);
    }

    #[test]
    fn never_replay_refuses_presentation_replay_only() {
        let mut q = InteractionQueue::new();
        let a = authority_pair();
        // A presentation replay of a reaction is refused (card 08 §B step 1).
        let replay = inst(1, InteractionFamilyTag::ReactionInteraction, SimId(1), None, 5, true);
        assert_eq!(
            q.insert(replay, 0, InteractionProducer::PresentationReplay, &a),
            Err(KernelError::NeverReplay(InteractionFamilyTag::ReactionInteraction))
        );
        // The server may execute reactions (the server's own game runs
        // PlayReaction; the kernel queue is the server's authority queue).
        let server = inst(2, InteractionFamilyTag::ReactionInteraction, SimId(1), None, 5, true);
        assert_eq!(
            q.insert(server, 0, InteractionProducer::ServerCommand, &a),
            Ok(())
        );
        assert_eq!(q.len(), 1);
        assert_eq!(q.head().unwrap().id, 2);
    }

    #[test]
    fn priority_preempt_strictly_greater() {
        let mut q = InteractionQueue::new();
        let a = authority_pair();
        // Head at priority 4 (numerically higher = lower priority).
        q.insert(
            inst(1, InteractionFamilyTag::Sit, SimId(1), None, 4, true),
            0,
            InteractionProducer::ServerCommand,
            &a,
        )
        .unwrap();
        // A new entry at priority 3 (numerically lower = higher priority)
        // takes the head and preempts the old head.
        q.insert(
            inst(2, InteractionFamilyTag::Sit, SimId(1), None, 3, true),
            0,
            InteractionProducer::ServerCommand,
            &a,
        )
        .unwrap();
        assert_eq!(q.head().unwrap().id, 2);
        assert_eq!(q.len(), 1);
        assert!(q.effects().contains(&QueueEffect::Preempted { id: 1 }));
    }

    #[test]
    fn priority_equal_never_preempts() {
        let mut q = InteractionQueue::new();
        let a = authority_pair();
        q.insert(
            inst(1, InteractionFamilyTag::Sit, SimId(1), None, 4, true),
            0,
            InteractionProducer::ServerCommand,
            &a,
        )
        .unwrap();
        // Equal priority never cancels (autonomy-queue.md).
        q.insert(
            inst(2, InteractionFamilyTag::Sit, SimId(1), None, 4, true),
            0,
            InteractionProducer::ServerCommand,
            &a,
        )
        .unwrap();
        assert_eq!(q.len(), 2);
        assert_eq!(q.head().unwrap().id, 2);
        assert!(q
            .effects()
            .iter()
            .all(|e| !matches!(e, QueueEffect::Preempted { .. })));
    }

    #[test]
    fn locked_head_keeps_index_zero_continuation_lands_at_one() {
        let mut q = InteractionQueue::new();
        let a = authority_pair();
        q.insert(
            inst(1, InteractionFamilyTag::Sit, SimId(1), None, 4, true),
            0,
            InteractionProducer::ServerCommand,
            &a,
        )
        .unwrap();
        q.lock_head();
        // A continuation of the running head must land at index 1, behind the
        // locked head (mIsHeadInteractionLocked, card 08 §A.1 phase 3).
        q.push_as_continuation(
            1,
            inst(2, InteractionFamilyTag::ReadBook, SimId(1), None, 4, true),
            InteractionProducer::ServerCommand,
            &a,
        )
        .unwrap();
        assert_eq!(q.head().unwrap().id, 1);
        assert_eq!(q.entries()[1].id, 2);
        q.unlock_head();
        // After the unlock, index 0 is available again.
        q.insert(
            inst(3, InteractionFamilyTag::Sit, SimId(1), None, 4, true),
            0,
            InteractionProducer::ServerCommand,
            &a,
        )
        .unwrap();
        assert_eq!(q.head().unwrap().id, 3);
    }

    #[test]
    fn sim189_swap_cancels_social_head() {
        let mut q = InteractionQueue::new();
        let mut a = authority_pair();
        q.insert(
            inst(1, InteractionFamilyTag::SocializeWith, SimId(1), Some(SimId(2)), 5, true),
            0,
            InteractionProducer::ServerCommand,
            &a,
        )
        .unwrap();
        q.claim_swap(SimId(1), SimId(2), &mut a).unwrap();
        assert!(q.is_empty());
        assert!(!a.is_selectable(SimId(1)));
        assert!(a.is_selectable(SimId(2)));
        // The four observable effects, exactly once, in order.
        assert_eq!(
            q.effects(),
            &[
                QueueEffect::Cancelled {
                    id: 1,
                    sim: SimId(1),
                    explanation: FailureExplanation::CancelledByScript,
                    route_stop: RouteStopEventType::Cancelled
                },
                QueueEffect::ReservationReleased { id: 1 },
                QueueEffect::PostureTornDown { id: 1 }
            ]
        );
    }

    #[test]
    fn sim189_swap_symmetric_and_deterministic() {
        let mut q = InteractionQueue::new();
        let mut a = authority_pair();
        q.insert(
            inst(1, InteractionFamilyTag::SocializeWith, SimId(2), Some(SimId(1)), 5, true),
            0,
            InteractionProducer::ServerCommand,
            &a,
        )
        .unwrap();
        q.claim_swap(SimId(2), SimId(1), &mut a).unwrap();
        assert!(q.is_empty());
        // Deterministic order: the smaller SimId (1) is processed first, so
        // the cancellation is attributed to SimId(1) even though the head's
        // actor is SimId(2).
        assert_eq!(
            q.effects(),
            &[
                QueueEffect::Cancelled {
                    id: 1,
                    sim: SimId(1),
                    explanation: FailureExplanation::CancelledByScript,
                    route_stop: RouteStopEventType::Cancelled
                },
                QueueEffect::ReservationReleased { id: 1 },
                QueueEffect::PostureTornDown { id: 1 }
            ]
        );
    }

    #[test]
    fn sim189_swap_cancels_every_touching_entry_once() {
        let mut q = InteractionQueue::new();
        let mut a = authority_pair();
        // Two queued socials touching the pair, plus an unrelated entry.
        q.insert(
            inst(1, InteractionFamilyTag::SocializeWith, SimId(1), Some(SimId(2)), 5, true),
            0,
            InteractionProducer::ServerCommand,
            &a,
        )
        .unwrap();
        q.insert(
            inst(2, InteractionFamilyTag::PillowFight, SimId(2), Some(SimId(1)), 5, true),
            1,
            InteractionProducer::ServerCommand,
            &a,
        )
        .unwrap();
        q.insert(
            inst(3, InteractionFamilyTag::Sit, SimId(1), None, 5, true),
            2,
            InteractionProducer::ServerCommand,
            &a,
        )
        .unwrap();
        q.claim_swap(SimId(1), SimId(2), &mut a).unwrap();
    // Only socials are re-hosted; unrelated gameplay remains queued.
    assert_eq!(q.head().unwrap().id, 3);
        let cancelled: Vec<u64> = q
            .effects()
            .iter()
            .filter_map(|e| match e {
                QueueEffect::Cancelled { id, .. } => Some(*id),
                _ => None,
            })
            .collect();
    assert_eq!(cancelled, vec![1, 2]);
        // Each effect exactly once per cancelled id.
    for id in [1u64, 2] {
            assert_eq!(
                q.effects()
                    .iter()
                    .filter(|e| *e == &QueueEffect::ReservationReleased { id })
                    .count(),
                1
            );
            assert_eq!(
                q.effects()
                    .iter()
                    .filter(|e| *e == &QueueEffect::PostureTornDown { id })
                    .count(),
                1
            );
        }
    }

    #[test]
    fn sim189_swap_no_stranded_head_invariant() {
        let mut q = InteractionQueue::new();
        let mut a = authority_pair();
        q.insert(
            inst(1, InteractionFamilyTag::SocializeWith, SimId(1), Some(SimId(2)), 5, true),
            0,
            InteractionProducer::ServerCommand,
            &a,
        )
        .unwrap();
        q.claim_swap(SimId(1), SimId(2), &mut a).unwrap();
        // Invariant 1 (card 08 §D.3): no stranded head — the queue is empty,
        // so neither side has a social head.
        assert!(q.head().is_none() || !q.head().unwrap().family.is_social());
        assert!(q.social_heads().is_empty());
    }

    #[test]
    fn failure_explanation_bits_are_distinct() {
        assert_eq!(FailureExplanation::CancelledByScript as u32, 1);
        assert_eq!(FailureExplanation::ReplanFailure as u32, 8);
        assert_eq!(FailureExplanation::DynamicAdjustmentFailed as u32, 0x80);
    }
}
