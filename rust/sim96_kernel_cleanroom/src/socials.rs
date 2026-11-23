#![forbid(unsafe_code)]
//! Card 16 · paired social postures and conversation turns.
//!
//! The six constraints (card 16 §A) all say the same thing: the
//! client must not re-derive anything. The kernel therefore carries the full
//! paired-social payload (rule key, topic, STC, target effect, jazz state) as
//! one authoritative, ordered record, releases the pair only through a
//! two-sided barrier (`SOCIAL_BARRIER_LIMIT_TICKS` closes the 14–15
//! tick entry-clip drift), and tears a pair down atomically in deterministic
//! `min(a_sim, b_sim)` order on a claim swap.

use std::collections::{BTreeMap, BTreeSet};

use crate::clock::TICKS_PER_SIM_HOUR;
use crate::services::Vec3Fixed;
use crate::social_state::RelationshipState;
use crate::{KernelError, SimId};

/// The barrier release window (card 16 §B.2): both endpoints must report
/// `EnterSocializingPosture` within this many ticks of each other or the pair
/// rolls back. Closes the 14–15 tick drift with slack.
pub const SOCIAL_BARRIER_LIMIT_TICKS: u32 = 20;

/// Clean-room witness id for the engine's `Goodbye` LHS rule (card 16 §C.1);
/// not a recovered native enum value.
pub const GOODBYE_LHS: u32 = 0x0000_0047;

/// Half the world distance between the two jig slots of a released pair, in
/// centimetres (card 16 §B.1 tuning; a clean-room constant, not a dump).
pub const SOCIAL_JIG_HALF_SEPARATION_CM: f32 = 60.0;

/// The wire correlation key for one paired social (card 16 §B).
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct SocialPairId(pub u64);

/// Deterministic pair identity: identical `(a_sim, b_sim, tick_opened)` inputs
/// on server and client mint the same id (Kani `pair_id_is_deterministic_hash`).
pub fn mint_pair_id(a_sim: SimId, b_sim: SimId, tick_opened: u64) -> SocialPairId {
    SocialPairId(
        (u64::from(a_sim.0) << 40) ^ (u64::from(b_sim.0) << 20) ^ tick_opened,
    )
}

/// The resolved `(LHS, RHS)` social-rule pair; carried so the client never
/// re-runs `FindBestSocial` (verdict item 2).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SocialRuleKey {
    pub lhs: u32,
    pub rhs: u32,
}

/// One paired-social execution. `A` (the initiator) is authoritative; `B`
/// (the responder) links through the same `pair_id` even when its wire row
/// carries no `actionKey` at all (card 16 §A, `Key` vs `ActionKey`).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct PairedSocial {
    pub pair_id: SocialPairId,
    pub a_sim: SimId,
    pub b_sim: SimId,
    pub action_key: u32,
    pub topic: u32,
    pub social_rule: SocialRuleKey,
    pub target_effect_bits: u32,
    pub stc_commodity: u32,
    pub stc_delta: i32,
    pub jazz_state: u32,
    pub tick_opened: u64,
}

/// Why a pair did not release, or why it was torn down early (card 16 §B).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SocialSyncErr {
    ///'s entry-clip drift exceeded the barrier window.
    BarrierTimeout { drift_ticks: u32, limit_ticks: u32 },
    /// one side of the pair swapped claims mid-social.
    AbortedByClaimSwap { sim: SimId },
    /// Either side is absent or not selectable (the gate, card 08).
    Unpaired,
    /// The client tried to re-pick its own social locally; the kernel refuses
    /// (verdict item 2 — this API is deliberately the only re-entry point).
    ClientReselectForbidden,
}

impl From<SocialSyncErr> for KernelError {
    fn from(err: SocialSyncErr) -> Self {
        match err {
            SocialSyncErr::BarrierTimeout { .. } => KernelError::SocialBarrierTimeout,
            SocialSyncErr::AbortedByClaimSwap { .. } => KernelError::AbortedByClaimSwap,
            SocialSyncErr::Unpaired => KernelError::Unpaired,
            SocialSyncErr::ClientReselectForbidden => KernelError::ClientReselectForbidden,
        }
    }
}

/// The two-sided barrier of one pair (card 16 §B.2 sequence steps 2–3).
/// The pair releases exactly when both `Sa` reports have arrived and their
/// drift is within `SOCIAL_BARRIER_LIMIT_TICKS`; a report past the window
/// answers `BarrierTimeout` with the exact drift.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct SocialBarrier {
    pub opened_at: u64,
    pub a_entered_at: Option<u64>,
    pub b_entered_at: Option<u64>,
    pub released: bool,
    pub aborted: bool,
    pub drift_ticks: u32,
}

impl SocialBarrier {
    pub fn new(opened_at: u64) -> Self {
        Self { opened_at, ..Self::default() }
    }
    pub fn enter_a(&mut self, tick: u64) -> Result<(), SocialSyncErr> {
        self.a_entered_at = Some(tick);
        self.try_release()
    }
    pub fn enter_b(&mut self, tick: u64) -> Result<(), SocialSyncErr> {
        self.b_entered_at = Some(tick);
        self.try_release()
    }
    fn try_release(&mut self) -> Result<(), SocialSyncErr> {
        if let (Some(a), Some(b)) = (self.a_entered_at, self.b_entered_at) {
            let drift = a.abs_diff(b) as u32;
            self.drift_ticks = drift;
            if drift <= SOCIAL_BARRIER_LIMIT_TICKS {
                self.released = true;
                Ok(())
            } else {
                Err(SocialSyncErr::BarrierTimeout {
                    drift_ticks: drift,
                    limit_ticks: SOCIAL_BARRIER_LIMIT_TICKS,
                })
            }
        } else {
            Ok(())
        }
    }
    pub fn abort(&mut self) {
        self.aborted = true;
    }
}

/// The deterministic jig / slot footprint pair (card 16 §B.1). Both endpoints
/// compute the same `(a_slot, b_slot, a_facing, b_facing)` from the two
/// authoritative positions; there is no RNG anywhere on this path (the
/// `Random.NextDouble()` jig was's entry-clip drift cause).
pub fn compute_jig_pair(
    pair: &PairedSocial,
    a_pos: Vec3Fixed,
    b_pos: Vec3Fixed,
) -> (Vec3Fixed, Vec3Fixed, f32, f32) {
    let _ = pair; // the pair id correlates the outcome; geometry is position-only
    let dx = b_pos.0 as f32 - a_pos.0 as f32;
    let dz = b_pos.2 as f32 - a_pos.2 as f32;
    let len = (dx * dx + dz * dz).sqrt();
    let (ux, uz) = if len == 0.0 {
        (0.0, 0.0)
    } else {
        (dx / len, dz / len)
    };
    // Integer midpoint first, then the fixed half-separation along the unit
    // line; every step is pure arithmetic, identical on both endpoints.
    let mid_x = (i64::from(a_pos.0) + i64::from(b_pos.0)) / 2;
    let mid_z = (i64::from(a_pos.2) + i64::from(b_pos.2)) / 2;
    let sep = SOCIAL_JIG_HALF_SEPARATION_CM;
    let to_cm = |v: f32| v.max(0.0).round() as u32;
    let a_slot = Vec3Fixed(to_cm(mid_x as f32 - ux * sep), a_pos.1, to_cm(mid_z as f32 - uz * sep));
    let b_slot = Vec3Fixed(to_cm(mid_x as f32 + ux * sep), b_pos.1, to_cm(mid_z as f32 + uz * sep));
    let a_facing = if len == 0.0 { 0.0 } else { dz.atan2(dx) };
    let mut b_facing = a_facing + std::f32::consts::PI;
    if b_facing > std::f32::consts::PI {
        b_facing -= 2.0 * std::f32::consts::PI;
    }
    (a_slot, b_slot, a_facing, b_facing)
}

/// The authoritative paired-social table: `Ss` rows register, `Sa` rows move
/// the barrier, and a swap tears both postures down atomically in
/// deterministic order (card 16 §B.3; the same `min`-first order the
/// `claim_swap` in card 08 uses, so both processes arrive at the same trace).
#[derive(Default)]
pub struct PairedSocialRegistry {
    pairs: BTreeMap<SocialPairId, PairedSocial>,
    barriers: BTreeMap<SocialPairId, SocialBarrier>,
    /// Per-Sim `LeaveConversationImmediately` teardown order, exactly as the
    /// paired rollback queues it (min SimId first).
    pub teardowns: Vec<SimId>,
    last_abort: Option<SocialSyncErr>,
}

impl PairedSocialRegistry {
    /// `Ss|pair_id|tick_opened` — open one pair. The A-side row is the only
    /// one that carries `actionKey`; re-opening a live pair is `Unpaired`
    /// (the previous pair must tear down first).
    pub fn open(&mut self, social: PairedSocial) -> Result<(), SocialSyncErr> {
        if self.pairs.contains_key(&social.pair_id) {
            return Err(SocialSyncErr::Unpaired);
        }
        self.barriers
            .insert(social.pair_id, SocialBarrier::new(social.tick_opened));
        self.pairs.insert(social.pair_id, social);
        Ok(())
    }

    /// `Sa|pair_id|sim|EnterSocializingPosture|tick` — one side's posture
    /// report. Either member may report; B's row is accepted without an
    /// action key because the pair is joined on `pair_id` alone.
    pub fn posture_entered(
        &mut self,
        pair: SocialPairId,
        sim: SimId,
        tick: u64,
    ) -> Result<(), SocialSyncErr> {
        let social = self.pairs.get(&pair).ok_or(SocialSyncErr::Unpaired)?;
        let is_a = social.a_sim == sim;
        if !is_a && social.b_sim != sim {
            return Err(SocialSyncErr::Unpaired);
        }
        let opened = social.tick_opened;
        let barrier = self
            .barriers
            .entry(pair)
            .or_insert_with(|| SocialBarrier::new(opened));
        if is_a {
            barrier.enter_a(tick)
        } else {
            barrier.enter_b(tick)
        }
    }

    /// abort (card 16 §B.3): both postures tear down atomically, the
    /// paired `LeaveConversationImmediately` queued `min(a_sim, b_sim)` first.
    /// Returns `Err(Unpaired)` when the pair is not (or no longer) live.
    pub fn abort_pair(&mut self, pair: SocialPairId, why: SocialSyncErr) -> Result<(), KernelError> {
        let social = self.pairs.remove(&pair).ok_or(KernelError::Unpaired)?;
        if let Some(barrier) = self.barriers.get_mut(&pair) {
            barrier.abort();
        }
        self.barriers.remove(&pair);
        let (first, second) = if social.a_sim <= social.b_sim {
            (social.a_sim, social.b_sim)
        } else {
            (social.b_sim, social.a_sim)
        };
        self.teardowns.push(first);
        self.teardowns.push(second);
        self.last_abort = Some(why);
        Ok(())
    }

    pub fn is_released(&self, pair: SocialPairId) -> bool {
        self.barriers.get(&pair).is_some_and(|b| b.released && !b.aborted)
    }

    pub fn abort_reason(&self) -> Option<SocialSyncErr> {
        self.last_abort
    }

    /// The A-side action key is authoritative for both renderers; a B-side
    /// row without a key still resolves the pair (Kani
    /// `b_side_no_action_key_still_syncs`).
    pub fn authoritative_action_key(&self, pair: SocialPairId) -> Option<u32> {
        self.pairs.get(&pair).map(|s| s.action_key)
    }

    pub fn live_pairs(&self) -> usize {
        self.pairs.len()
    }

    /// The live record for one pair id, if the pair is open.
    pub fn peek(&self, pair: SocialPairId) -> Option<PairedSocial> {
        self.pairs.get(&pair).copied()
    }
}

/// The STC topic id (`Conversation.cs mActiveTopics` entries, card 16 §C).
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct ConversationTopic(pub u32);

/// The multi-beat conversation state machine (card 16 §C).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ConversationState {
    Idle,
    Opening,
    Active,
    WindingDown,
    Closed,
}

/// The server-authoritative `Conversation` mirror. Process-local in the
/// engine; the kernel snapshots it so the client's pie-menu test
/// (`SocialComponent.Conversation != null`) matches the server's without ever
/// re-running `FindBestSocial` locally.
#[derive(Clone, Debug)]
pub struct ConversationSnapshot {
    pub id: u64,
    pub members: Vec<SimId>,
    pub whose_turn: Option<SimId>,
    pub who_talked_last: Option<SimId>,
    pub active_topics: Vec<ConversationTopic>,
    pub topical_socials_used: BTreeSet<u32>,
    pub socials_left_before_leave: i32,
    pub state: ConversationState,
    /// Ring buffer of the last 8 topics (`topic_history_ring_wraps_at_8`).
    pub topic_history_ring: [ConversationTopic; 8],
    pub topic_history_head: u8,
}

impl Default for ConversationSnapshot {
    fn default() -> Self {
        Self {
            id: 0,
            members: Vec::new(),
            whose_turn: None,
            who_talked_last: None,
            active_topics: Vec::new(),
            topical_socials_used: BTreeSet::new(),
            socials_left_before_leave: 0,
            state: ConversationState::Idle,
            topic_history_ring: [ConversationTopic(0); 8],
            topic_history_head: 0,
        }
    }
}

impl ConversationSnapshot {
    /// Record a topic: append to `mActiveTopics` and stamp the 8-slot ring,
    /// head advancing modulo 8 (the ninth push overwrites slot 0).
    pub fn push_topic(&mut self, topic: ConversationTopic) {
        self.active_topics.push(topic);
        let idx = self.topic_history_head as usize % 8;
        self.topic_history_ring[idx] = topic;
        self.topic_history_head = ((self.topic_history_head as usize + 1) % 8) as u8;
        if self.state == ConversationState::Idle {
            self.state = ConversationState::Active;
        }
    }

    /// One topical social consumed; the "consider leaving" counter steps down
    /// exactly once per social (`STCData.GetNumSocials`, card 16 §C).
    pub fn note_social_used(&mut self, social_key: u32) {
        if self.topical_socials_used.insert(social_key) {
            self.socials_left_before_leave -= 1;
        }
    }

    /// Hand the turn to the next member in registration order (deterministic
    /// round-robin; no host-local selection).
    pub fn advance_turn(&mut self) {
        if self.members.is_empty() {
            return;
        }
        let next = match self.whose_turn {
            Some(current) => {
                let idx = self.members.iter().position(|m| *m == current).unwrap_or(0);
                self.members[(idx + 1) % self.members.len()]
            }
            None => self.members[0],
        };
        self.whose_turn = Some(next);
    }
}

/// The server-side pie-menu eligibility predicate (card 16 §C.1). The 24
/// game-hour gate mirrors `WhenLastTalked` (card 09 §C.3); `Goodbye` needs a
/// live conversation. The answer crosses the wire — the client never
/// re-evaluates this itself.
pub fn pie_menu_eligible(
    social: SocialRuleKey,
    conv: Option<&ConversationSnapshot>,
    rel: &RelationshipState,
    now_tick: u64,
) -> bool {
    let talked_this_session = matches!(conv, Some(c) if !c.members.is_empty());
    let twenty_four_game_hours: u64 = 24 * TICKS_PER_SIM_HOUR;
    let stale = now_tick.saturating_sub(rel.when_last_talked) >= twenty_four_game_hours;
    if !talked_this_session && !stale {
        return false;
    }
    if social.lhs == GOODBYE_LHS {
        return conv.is_some();
    }
    true
}

/// Card 26 §C golden-corpus hook (canary 16, barrier).
#[cfg(test)]
mod kdiff {
    use super::*;

    #[test]
    fn kdiff_canary_16() {
        let mut b = SocialBarrier::new(0);
        b.enter_a(0).unwrap();
        let drift = match b.enter_b(21) {
            Err(SocialSyncErr::BarrierTimeout { drift_ticks, limit_ticks })
                if limit_ticks == SOCIAL_BARRIER_LIMIT_TICKS => drift_ticks as u64,
            _ => u64::MAX,
        };
        println!("KDIFF|canary=16|case=barrier_timeout_drift|got={:016x}", drift);
        assert_eq!(drift, 21);
        assert!(!b.released);
        // Deterministic pair id on the sealed mint.
        let same = mint_pair_id(SimId(1), SimId(2), 100) == mint_pair_id(SimId(1), SimId(2), 100);
        println!("KDIFF|canary=16|case=pair_id_deterministic|got={:016x}", same as u64);
        assert!(same);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::social_state::{LongTermRelationshipType, ShortTermContextType};

    fn rel_when_talked(when_last_talked: u64) -> RelationshipState {
        RelationshipState {
            pair: crate::social_state::RelationshipPair::new(SimId(1), SimId(2)),
            liking: 20,
            stc_commodity: 0,
            stc_type: ShortTermContextType::Friendly,
            stc_perceived: SimId(2),
            ltr: LongTermRelationshipType::Acquaintance,
            interaction_bits: 0,
            ltr_start_ticks: 0,
            highest_liking: 20,
            when_last_greeted: 0,
            when_last_talked,
        }
    }

    fn social(a: SimId, b: SimId, tick_opened: u64) -> PairedSocial {
        PairedSocial {
            pair_id: mint_pair_id(a, b, tick_opened),
            a_sim: a,
            b_sim: b,
            action_key: 0xC4A7, // the clean-room `Chat` witness
            topic: 3,
            social_rule: SocialRuleKey { lhs: 1, rhs: 2 },
            target_effect_bits: 0,
            stc_commodity: 5,
            stc_delta: 2,
            jazz_state: 1,
            tick_opened,
        }
    }

    #[test]
    fn barrier_releases_within_limit_and_times_out_beyond_it() {
        // drift == 20 releases; drift == 21 refuses with the exact drift.
        let mut b = SocialBarrier::new(0);
        assert_eq!(b.enter_a(0), Ok(()));
        assert_eq!(b.enter_b(20), Ok(()));
        assert!(b.released);
        let mut b2 = SocialBarrier::new(0);
        b2.enter_a(0).unwrap();
        assert_eq!(
            b2.enter_b(21),
            Err(SocialSyncErr::BarrierTimeout { drift_ticks: 21, limit_ticks: 20 })
        );
        assert!(!b2.released);
    }

    #[test]
    fn pair_id_is_deterministic_and_tick_sensitive() {
        assert_eq!(mint_pair_id(SimId(1), SimId(2), 100), mint_pair_id(SimId(1), SimId(2), 100));
        assert_ne!(mint_pair_id(SimId(1), SimId(2), 100), mint_pair_id(SimId(1), SimId(2), 101));
    }

    #[test]
    fn claim_swap_abort_tears_down_min_sim_first() {
        // Pablo/Iliana-style pair (SimId(5), SimId(2)): Sim 2 queues its
        // LeaveConversationImmediately first on both sides.
        let mut reg = PairedSocialRegistry::default();
        let s = social(SimId(5), SimId(2), 0);
        let pair = s.pair_id;
        reg.open(s).unwrap();
        assert!(reg.posture_entered(pair, SimId(5), 0).is_ok());
        assert!(reg.posture_entered(pair, SimId(2), 3).is_ok());
        assert!(reg.is_released(pair));
        assert_eq!(
            reg.abort_pair(pair, SocialSyncErr::AbortedByClaimSwap { sim: SimId(5) }),
            Ok(())
        );
        assert_eq!(reg.teardowns, vec![SimId(2), SimId(5)]);
        assert!(!reg.is_released(pair));
        assert_eq!(reg.abort_reason(), Some(SocialSyncErr::AbortedByClaimSwap { sim: SimId(5) }));
        // A second abort of the same pair is Unpaired (teardown ran once).
        assert_eq!(reg.abort_pair(pair, SocialSyncErr::Unpaired), Err(KernelError::Unpaired));
        assert_eq!(reg.teardowns.len(), 2);
    }

    #[test]
    fn jig_pair_is_pure_and_symmetric() {
        let s = social(SimId(1), SimId(2), 0);
        let a = Vec3Fixed(1_000, 0, 1_000);
        let b = Vec3Fixed(1_400, 0, 1_000);
        let first = compute_jig_pair(&s, a, b);
        for _ in 0..1000 {
            assert_eq!(compute_jig_pair(&s, a, b), first);
        }
        let (a_slot, b_slot, a_facing, b_facing) = &first;
        // A east of midpoint on the x line, B west-facing: deterministic halves.
        assert_eq!(a_slot.0, 1_140);
        assert_eq!(b_slot.0, 1_260);
        assert!((*a_facing - 0.0f32).abs() < 1e-6);
        assert!((*b_facing - std::f32::consts::PI).abs() < 1e-6 || (*b_facing + std::f32::consts::PI).abs() < 1e-6);
        // Degenerate same-position pair never panics and stays put.
        let same = compute_jig_pair(&s, a, a);
        assert_eq!(same.0, same.1);
        assert_eq!(same.2, 0.0);
    }

    #[test]
    fn topic_history_ring_wraps_at_eight() {
        let mut conv = ConversationSnapshot::default();
        for i in 0..9u32 {
            conv.push_topic(ConversationTopic(100 + i));
        }
        assert_eq!(conv.topic_history_head, 1);
        assert_eq!(conv.topic_history_ring[0], ConversationTopic(108));
        assert_eq!(conv.active_topics.len(), 9);
    }

    #[test]
    fn pie_menu_gate_goodbye_and_twenty_four_hour_rule() {
        // No conversation and a recent talk -> ineligible.
        let rel = rel_when_talked(500);
        assert!(!pie_menu_eligible(
            SocialRuleKey { lhs: 1, rhs: 0 },
            None,
            &rel,
            500 + TICKS_PER_SIM_HOUR * 6,
        ));
        // An active conversation -> eligible for ordinary socials.
        let mut conv = ConversationSnapshot::default();
        conv.members.push(SimId(1));
        assert!(pie_menu_eligible(
            SocialRuleKey { lhs: 1, rhs: 0 },
            Some(&conv),
            &rel,
            500 + TICKS_PER_SIM_HOUR * 6,
        ));
        // Goodbye mirrors `SocialComponent.cs:2096`: needs Conversation != null.
        assert!(!pie_menu_eligible(SocialRuleKey { lhs: GOODBYE_LHS, rhs: 0}, None, &rel, 500));
        assert!(pie_menu_eligible(SocialRuleKey { lhs: GOODBYE_LHS, rhs: 0}, Some(&conv), &rel, 500));
        // No conversation but 30 sim-hours stale -> new-conversation socials
        // become eligible through the 24 h gate.
        let stale_rel = rel_when_talked(0);
        assert!(pie_menu_eligible(
            SocialRuleKey { lhs: 1, rhs: 0 },
            None,
            &stale_rel,
            24 * TICKS_PER_SIM_HOUR,
        ));
    }

    #[test]
    fn b_side_without_action_key_still_pairs_on_pair_id() {
        // The B row is registered only through the A-side record; a posture
        // report from B (no key) releases the pair and the A key remains the
        // one authoritative answer for both renderers.
        let mut reg = PairedSocialRegistry::default();
        let s = social(SimId(1), SimId(2), 50);
        let pair = s.pair_id;
        reg.open(s).unwrap();
        assert!(reg.posture_entered(pair, SimId(2), 52).is_ok());
        assert!(!reg.is_released(pair)); // one side only
        assert!(reg.posture_entered(pair, SimId(1), 50).is_ok());
        assert!(reg.is_released(pair));
        assert_eq!(reg.authoritative_action_key(pair), Some(0xC4A7));
    }

    #[test]
    fn client_reselect_is_a_kernel_refusal_not_a_local_pick() {
        // The registry exposes no re-pick API; the only way a wrong-side
        // posture report surfaces is Unpaired, and a client-initiated re-pick
        // maps to ClientReselectForbidden -> its own KernelError variant.
        let mut reg = PairedSocialRegistry::default();
        let s = social(SimId(1), SimId(2), 0);
        reg.open(s).unwrap();
        assert_eq!(
            reg.posture_entered(s.pair_id, SimId(99), 1),
            Err(SocialSyncErr::Unpaired)
        );
        assert_eq!(
            KernelError::from(SocialSyncErr::ClientReselectForbidden),
            KernelError::ClientReselectForbidden
        );
        assert_eq!(reg.live_pairs(), 1);
    }

    #[test]
    fn conversation_turn_and_social_counters_are_deterministic() {
        let mut conv = ConversationSnapshot {
            members: vec![SimId(1), SimId(2), SimId(3)],
            socials_left_before_leave: 3,
            ..ConversationSnapshot::default()
        };
        conv.advance_turn();
        assert_eq!(conv.whose_turn, Some(SimId(1)));
        conv.advance_turn();
        assert_eq!(conv.whose_turn, Some(SimId(2)));
        conv.who_talked_last = conv.whose_turn;
        conv.note_social_used(11);
        conv.note_social_used(11); // same key counts once
        assert_eq!(conv.socials_left_before_leave, 2);
        assert_eq!(conv.topical_socials_used.len(), 1);
    }
}
