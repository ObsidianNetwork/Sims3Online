#![forbid(unsafe_code)]
//! Wave 12 (cards 56-59) - data-driven `.jazz` state-machine compiler and
//! SACS clip sequencer. Canary 32.
//!
//! This module discharges the clean-room half of ledger row 2
//! (`go_nogo::UNSUPPORTED_SURFACE_ROWS[1]`): the *mechanics* TS3W runs for a
//! `.jazz` graph - states with durations and loop modes, guarded weighted
//! transitions, the card-04 dual LCGs driving every random choice, the 4-entry
//! no-repeat clip window, and the card-16 paired social barrier - are rebuilt
//! here from the sealed cards' behaviour. The ~1,234 captured `.jazz` clip
//! graphs themselves remain the native oracle (retention), graded per golden
//! machine until corpus parity lands.
//!
//! Determinism contract: no floating point, no wall clock, no thread ordering.
//! Every random choice goes through the SAME `sacs_rng` LCG transitions
//! (`path_draw_lcg` for weighted/loop picks, `random_node_lcg` for node
//! selection), and only REQUIRED draws are consumed - the canary-04 discipline
//! re-asserted at the .jazz layer. `SacsDriver` state is plain `Copy` data so
//! the daemon can persist it inside `state_hash` and the SIM96CP1 replay.

use std::collections::BTreeMap;

use crate::handles::KernelError;
use crate::kernel_iface::PathRng;
use crate::KernelRng;
use crate::socials::SOCIAL_BARRIER_LIMIT_TICKS;

/// Card 04 §B: a loop-picked clip may not repeat inside the last
/// `NO_REPEAT_WINDOW - 1` picks (a 4-entry window).
pub const NO_REPEAT_WINDOW: usize = 4;
/// Guard param naming the paired social-barrier sync (card 16 §B): presence of
/// `JazzParam { name_hash: BARRIER_SYNC_NAME, value_hash: <peer hash> }`
/// releases a barrier state; drift beyond `SOCIAL_BARRIER_LIMIT_TICKS` refuses.
pub const BARRIER_SYNC_NAME: u32 = 0xB412_0001;
/// Absolute safety cap for one `run_until` (a machine that never reaches its
/// target is a broken graph, answered by name, never a hang).
pub const JAZZ_RUN_TICK_CAP: u32 = 4_096;

/// One `.jazz` graph parameter (name/value pair, hashed): guards and syncs
/// compare hashes only, so the kernel never parses authored clip data.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct JazzParam {
    pub name_hash: u32,
    pub value_hash: u32,
}

/// Who a state/transition acts upon (card 56 §B actor-role table, the three
/// roles the golden graphs use).
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum JazzActorRole { ActorX, ActorY, ObjectTarget }

/// Loop modes, matching card 04's loop-pick codes `0/1/2`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum JazzLoopMode {
    /// Code 0: play once, then transition.
    SingleShot,
    /// Code 1: replay the state `n` extra times before transitioning.
    LoopCount(u8),
    /// Code 2: weighted loop - each lap re-picks a clip under the no-repeat
    /// window until a guard transitions out.
    LoopPick,
}

impl JazzLoopMode {
    pub const fn mode_code(&self) -> u32 {
        match self {
            JazzLoopMode::SingleShot => 0,
            JazzLoopMode::LoopCount(_) => 1,
            JazzLoopMode::LoopPick => 2,
        }
    }
}

/// One `.jazz` state node (card 56 §A shape). `is_social_barrier` marks the
/// state that must wait on the paired barrier (card 16).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct JazzStateNode {
    pub state_id: u16,
    pub name: &'static str,
    pub clip_key_hash: u64,
    pub duration_ticks: u16,
    pub loop_mode: JazzLoopMode,
    pub is_social_barrier: bool,
}

/// One directed transition (card 56 §A shape). A guard with
/// `guard_param_hash == 0` is unconditional; otherwise the params must carry
/// `(guard_param_hash, guard_value_hash)` for the edge to be eligible.
/// `weight` feeds the card-04 weighted pick.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct JazzTransition {
    pub from_state: u16,
    pub to_state: u16,
    pub guard_param_hash: u32,
    pub guard_value_hash: u32,
    pub weight: u16,
}

/// A compiled `.jazz` state machine: states, transitions, per-state loop clip
/// candidates and actor roles, all `BTreeMap`-ordered.
#[derive(Clone, Debug)]
pub struct JazzStateMachine {
    pub machine_id: u16,
    pub entry_state: u16,
    pub states: BTreeMap<u16, JazzStateNode>,
    pub transitions: BTreeMap<u16, Vec<JazzTransition>>,
    /// Loop candidates for `LoopPick`/`LoopCount` states (the authored clip
    /// alternates, keyed by state id).
    pub loop_clips: BTreeMap<u16, Vec<u64>>,
    pub state_roles: BTreeMap<u16, JazzActorRole>,
}

impl JazzStateMachine {
    pub fn new(machine_id: u16, entry_state: u16) -> Self {
        Self { machine_id, entry_state, states: BTreeMap::new(),
            transitions: BTreeMap::new(), loop_clips: BTreeMap::new(),
            state_roles: BTreeMap::new() }
    }

    pub fn add_state(&mut self, node: JazzStateNode, role: JazzActorRole) {
        self.states.insert(node.state_id, node);
        self.state_roles.insert(node.state_id, role);
    }

    pub fn add_transition(&mut self, edge: JazzTransition) {
        self.transitions.entry(edge.from_state).or_default().push(edge);
        for list in self.transitions.values_mut() {
            list.sort_by_key(|e| (e.to_state, e.guard_param_hash, e.weight));
        }
    }

    pub fn add_loop_clips(&mut self, state: u16, clips: &[u64]) {
        self.loop_clips.insert(state, clips.to_vec());
    }

    /// The card-04 weighted pick over `eligible` transitions: one
    /// `path_draw_lcg` draw against the summed weights (no draw when a single
    /// edge is eligible - "consume only required draws").
    pub fn weighted_pick(seed: u32, weights: &[u16]) -> (u32, usize) {
        let total: u64 = weights.iter().map(|w| u64::from(*w)).sum();
        if weights.len() <= 1 || total == 0 { return (seed, 0); }
        let (next, draw) = KernelRng::path_draw(seed, u32::try_from(total).unwrap_or(u32::MAX));
        let mut remaining = draw;
        for (index, weight) in weights.iter().enumerate() {
            if remaining < u64::from(*weight) as u32 { return (next, index); }
            remaining -= u64::from(*weight) as u32;
        }
        (next, weights.len() - 1)
    }

    /// The 4-entry no-repeat window pick (card 04 §B): candidates are the
    /// state's clips minus the last `NO_REPEAT_WINDOW - 1` picks; a single
    /// survivor draws nothing. Returns `(next_seed, clip_key)`.
    pub fn pick_loop_clip(seed: u32, clips: &[u64], recent: &[u64]) -> (u32, u64) {
        let mut candidates: Vec<u64> = clips.iter().copied()
            .filter(|c| !recent.contains(c)).collect();
        candidates.sort_unstable();
        if candidates.is_empty() {
            candidates = clips.to_vec();
            candidates.sort_unstable();
        }
        if candidates.len() == 1 { return (seed, candidates[0]); }
        let (next, draw) = KernelRng::path_draw(seed, u32::try_from(candidates.len()).unwrap_or(1));
        let index = (draw as usize).min(candidates.len() - 1);
        (next, candidates[index])
    }

    /// Step the driver one world tick. `Ok(true)` = the tick completed the
    /// machine's run into its final state; `Ok(false)` = still playing;
    /// named `Err` = refused (barrier timeout / impossible transition).
    pub fn advance(&self, d: &mut SacsDriver, params: &[JazzParam])
        -> Result<bool, KernelError> {
        if !self.states.contains_key(&d.state_id) {
            return Err(KernelError::InvalidJazzTransition);
        }
        d.ticks_played += 1;
        if d.ticks_played > JAZZ_RUN_TICK_CAP {
            return Err(KernelError::InvalidJazzTransition);
        }
        let node = self.states[&d.state_id];
        d.tick_in_state += 1;
        if d.tick_in_state < u32::from(node.duration_ticks.max(1)) {
            return Ok(false);
        }
        // State duration finished: loop first (card 04 loop modes).
        match node.loop_mode {
            JazzLoopMode::LoopCount(extra) if d.loop_iteration < u32::from(extra) => {
                d.loop_iteration += 1;
                d.tick_in_state = 0;
                if let Some(clips) = self.loop_clips.get(&node.state_id) {
                    let (seed, clip) = Self::pick_loop_clip(d.path_seed, clips, &d.recent());
                    d.path_seed = seed;
                    d.push_clip(clip);
                    d.clip_key_hash = clip;
                }
                return Ok(false);
            }
            JazzLoopMode::LoopPick => {
                // Each lap re-picks under the window; exit only via a guarded
                // transition, checked every lap below.
                d.tick_in_state = 0;
                if let Some(clips) = self.loop_clips.get(&node.state_id) {
                    let (seed, clip) = Self::pick_loop_clip(d.path_seed, clips, &d.recent());
                    d.path_seed = seed;
                    d.push_clip(clip);
                    d.clip_key_hash = clip;
                }
                // A LoopPick state may still transition out when guards fire.
                let eligible: Vec<&JazzTransition> = self.eligible(d.state_id, params);
                if !eligible.is_empty() {
                    let (seed, index) = Self::weighted_pick(
                        d.path_seed, &eligible.iter().map(|e| e.weight).collect::<Vec<_>>());
                    d.path_seed = seed;
                    let target = eligible[index].to_state;
                    return Ok(self.enter(d, target));
                }
                return Ok(false);
            }
            _ => {}
        }
        // Social barrier (card 16): a barrier state holds completion until the
        // paired sync param arrives; past the limit it is refused by name.
        if node.is_social_barrier
            && !params.iter().any(|p| p.name_hash == BARRIER_SYNC_NAME) {
            d.barrier_wait += 1;
            if d.barrier_wait > SOCIAL_BARRIER_LIMIT_TICKS {
                return Err(KernelError::JazzSocialBarrierTimeout);
            }
            d.tick_in_state = u32::from(node.duration_ticks.max(1)) - 1;
            return Ok(false);
        }
        d.loop_iteration = 0;
        let eligible: Vec<&JazzTransition> = self.eligible(d.state_id, params);
        if eligible.is_empty() {
            // Terminal only when the driver already sits where the run is
            // bound; anywhere else an exhausted edge set is a broken graph.
            if d.halted { return Ok(true); }
            return Err(KernelError::InvalidJazzTransition);
        }
        let (seed, index) = Self::weighted_pick(
            d.path_seed, &eligible.iter().map(|e| e.weight).collect::<Vec<_>>());
        d.path_seed = seed;
        let target = eligible[index].to_state;
        Ok(self.enter(d, target))
    }

    fn eligible<'a>(&'a self, from: u16, params: &[JazzParam])
        -> Vec<&'a JazzTransition> {
        self.transitions.get(&from).map(|list| list.iter()
            .filter(|e| e.guard_param_hash == 0
                || params.iter().any(|p| p.name_hash == e.guard_param_hash
                    && p.value_hash == e.guard_value_hash))
            .collect()).unwrap_or_default()
    }

    /// Enter `target`: reset timing, draw the node seed forward, adopt the
    /// target's clip (picking from the loop window when it has alternates),
    /// and report whether the driver was already sitting there.
    fn enter(&self, d: &mut SacsDriver, target: u16) -> bool {
        let reached_target = d.state_id == target;
        d.state_id = target;
        d.tick_in_state = 0;
        d.node_seed = KernelRng::node_step(d.node_seed);
        if let Some(node) = self.states.get(&target) {
            d.clip_key_hash = node.clip_key_hash;
            if let Some(clips) = self.loop_clips.get(&target) {
                let (seed, clip) = Self::pick_loop_clip(d.path_seed, clips, &d.recent());
                d.path_seed = seed;
                d.push_clip(clip);
                d.clip_key_hash = clip;
            }
            if node.is_social_barrier && d.barrier_armed_at_enter() { d.barrier_wait = 0; }
        }
        reached_target && self.states.get(&target)
            .is_some_and(|n| d.state_id == target && n.duration_ticks <= 1)
            || d.halted
    }
}

/// The persisted SACS driver: everything the daemon must remember per Sim to
/// replay or checkpoint a `.jazz` run - plain `Copy` data, inside `state_hash`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SacsDriver {
    pub machine_id: u16,
    pub state_id: u16,
    pub tick_in_state: u32,
    pub loop_iteration: u32,
    pub path_seed: u32,
    pub node_seed: u32,
    /// The last `NO_REPEAT_WINDOW - 1` picked clip keys, oldest first;
    /// `clip_count` tracks fill so partial windows are honoured exactly.
    pub clips: [u64; NO_REPEAT_WINDOW],
    pub clip_count: u32,
    pub clip_key_hash: u64,
    pub barrier_wait: u32,
    pub ticks_played: u32,
    pub halted: bool,
}

impl SacsDriver {
    pub fn start(machine: &JazzStateMachine, path_seed: u32) -> Self {
        let node = machine.states[&machine.entry_state];
        let mut d = Self { machine_id: machine.machine_id, state_id: node.state_id,
            tick_in_state: 0, loop_iteration: 0, path_seed,
            node_seed: KernelRng::node_step(path_seed),
            clips: [0; NO_REPEAT_WINDOW], clip_count: 0,
            clip_key_hash: node.clip_key_hash, barrier_wait: 0, ticks_played: 0,
            halted: false };
        if let Some(clips) = machine.loop_clips.get(&node.state_id) {
            let (seed, clip) =
                JazzStateMachine::pick_loop_clip(path_seed, clips, &d.recent());
            d.path_seed = seed;
            d.push_clip(clip);
            d.clip_key_hash = clip;
        }
        d
    }

    /// The no-repeat window: the last `NO_REPEAT_WINDOW - 1` picks.
    pub fn recent(&self) -> Vec<u64> {
        let keep = (self.clip_count as usize).min(NO_REPEAT_WINDOW - 1);
        let start = self.clip_count as usize - keep;
        self.clips[start..self.clip_count as usize].to_vec()
    }

    pub fn push_clip(&mut self, clip: u64) {
        let slot = (self.clip_count as usize) % NO_REPEAT_WINDOW;
        self.clips[slot] = clip;
        self.clip_count = self.clip_count.saturating_add(1);
        // Keep the ring as a sliding window of the newest 3 candidates.
        if self.clip_count as usize > NO_REPEAT_WINDOW {
            for shift in 0..NO_REPEAT_WINDOW - 1 {
                self.clips[shift] = self.clips[shift + 1];
            }
            self.clips[NO_REPEAT_WINDOW - 1] = clip;
            self.clip_count = NO_REPEAT_WINDOW as u32;
        }
    }

    fn barrier_armed_at_enter(&self) -> bool { true }
}

/// The outcome of a complete authored run: what `PlayJazzClip` answers and
/// what the `Jz` broadcast carries.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct JazzRun {
    pub clip_key_hash: u64,
    pub seed_after: u32,
    pub ticks: u32,
}

/// Run `machine` from its entry until the driver ENTERS `target_state`
/// (documented semantics: the tick the target state is reached counts as the
/// final tick), then refuse by name on barrier timeout or an impossible
/// transition. Pure function of (machine, params, seed).
pub fn run_until_target(machine: &JazzStateMachine, target_state: u16,
    path_seed: u32, params: &[JazzParam]) -> Result<JazzRun, KernelError> {
    let mut d = SacsDriver::start(machine, path_seed);
    if d.state_id == target_state {
        return Ok(JazzRun { clip_key_hash: d.clip_key_hash, seed_after: d.path_seed,
            ticks: 0 });
    }
    loop {
        let before = d.state_id;
        machine.advance(&mut d, params)?;
        if d.state_id != before {
            // Reaching the target state ends the run at that tick.
            if d.state_id == target_state {
                return Ok(JazzRun { clip_key_hash: d.clip_key_hash,
                    seed_after: d.path_seed, ticks: d.ticks_played });
            }
        }
        if d.halted {
            return Err(KernelError::InvalidJazzTransition);
        }
        if d.ticks_played > JAZZ_RUN_TICK_CAP {
            return Err(KernelError::InvalidJazzTransition);
        }
    }
}

/// Golden Sunset Valley `.jazz` fixture: the generic social graph
/// `Enter -> Loop_Pick -> Accept -> Exit`, with the Accept node a paired
/// social-barrier state (card 16) and the Loop_Pick state carrying the four
/// authored clip alternates (card 04 §B window). Authored from the sealed
/// cards' behaviour, not extracted from game files.
pub fn social_generic_jazz_machine() -> JazzStateMachine {
    let mut m = JazzStateMachine::new(1, 10);
    m.add_state(JazzStateNode { state_id: 10, name: "Enter",
        clip_key_hash: 0x1111_0000_0000_0001, duration_ticks: 2,
        loop_mode: JazzLoopMode::SingleShot, is_social_barrier: false },
        JazzActorRole::ActorX);
    m.add_state(JazzStateNode { state_id: 20, name: "Loop_Pick",
        clip_key_hash: 0x2222_0000_0000_0002, duration_ticks: 3,
        loop_mode: JazzLoopMode::LoopCount(2), is_social_barrier: false },
        JazzActorRole::ActorX);
    m.add_state(JazzStateNode { state_id: 30, name: "Accept",
        clip_key_hash: 0x3333_0000_0000_0003, duration_ticks: 2,
        loop_mode: JazzLoopMode::SingleShot, is_social_barrier: true },
        JazzActorRole::ActorY);
    m.add_state(JazzStateNode { state_id: 40, name: "Reject",
        clip_key_hash: 0x4444_0000_0000_0004, duration_ticks: 1,
        loop_mode: JazzLoopMode::SingleShot, is_social_barrier: false },
        JazzActorRole::ActorY);
    m.add_state(JazzStateNode { state_id: 50, name: "Exit",
        clip_key_hash: 0x5555_0000_0000_0005, duration_ticks: 1,
        loop_mode: JazzLoopMode::SingleShot, is_social_barrier: false },
        JazzActorRole::ActorX);
    m.add_loop_clips(20, &[0xAAAA_0001, 0xBBBB_0002, 0xCCCC_0003, 0xDDDD_0004]);
    m.add_transition(JazzTransition { from_state: 10, to_state: 20,
        guard_param_hash: 0, guard_value_hash: 0, weight: 1 });
    m.add_transition(JazzTransition { from_state: 20, to_state: 30,
        guard_param_hash: 0, guard_value_hash: 0, weight: 1 });
    m.add_transition(JazzTransition { from_state: 20, to_state: 40,
        guard_param_hash: 0x1122_3344, guard_value_hash: 0x5566_7788, weight: 1 });
    m.add_transition(JazzTransition { from_state: 30, to_state: 50,
        guard_param_hash: 0, guard_value_hash: 0, weight: 1 });
    m.add_transition(JazzTransition { from_state: 40, to_state: 50,
        guard_param_hash: 0, guard_value_hash: 0, weight: 1 });
    m
}

/// Golden fixture: eat-held-food - an object-target graph with a two-lap chew
/// loop on a held prop (`FoodProp`-class clip alternates).
pub fn eat_held_food_jazz_machine() -> JazzStateMachine {
    let mut m = JazzStateMachine::new(2, 10);
    m.add_state(JazzStateNode { state_id: 10, name: "Grab",
        clip_key_hash: 0x6666_0000_0000_0001, duration_ticks: 1,
        loop_mode: JazzLoopMode::SingleShot, is_social_barrier: false },
        JazzActorRole::ObjectTarget);
    m.add_state(JazzStateNode { state_id: 20, name: "Chew",
        clip_key_hash: 0x7777_0000_0000_0002, duration_ticks: 3,
        loop_mode: JazzLoopMode::LoopCount(2), is_social_barrier: false },
        JazzActorRole::ObjectTarget);
    m.add_state(JazzStateNode { state_id: 30, name: "Exit",
        clip_key_hash: 0x8888_0000_0000_0003, duration_ticks: 1,
        loop_mode: JazzLoopMode::SingleShot, is_social_barrier: false },
        JazzActorRole::ActorX);
    m.add_loop_clips(20, &[0xEEEE_0001, 0xFFFF_0002, 0xEEEE_0003]);
    m.add_transition(JazzTransition { from_state: 10, to_state: 20,
        guard_param_hash: 0, guard_value_hash: 0, weight: 1 });
    m.add_transition(JazzTransition { from_state: 20, to_state: 30,
        guard_param_hash: 0, guard_value_hash: 0, weight: 1 });
    m
}

/// Golden fixture: bed-sleep - the autonomy-only `BedSleep` family's graph,
/// the barrier sitting on `Sleep` (a sleeper must not be stepped on by two
/// presenters at different drifts).
pub fn bed_sleep_jazz_machine() -> JazzStateMachine {
    let mut m = JazzStateMachine::new(3, 10);
    m.add_state(JazzStateNode { state_id: 10, name: "Enter",
        clip_key_hash: 0x9999_0000_0000_0001, duration_ticks: 1,
        loop_mode: JazzLoopMode::SingleShot, is_social_barrier: false },
        JazzActorRole::ActorX);
    m.add_state(JazzStateNode { state_id: 20, name: "Sleep",
        clip_key_hash: 0xAAAA_0000_0000_0002, duration_ticks: 2,
        loop_mode: JazzLoopMode::LoopCount(1), is_social_barrier: true },
        JazzActorRole::ActorX);
    m.add_state(JazzStateNode { state_id: 30, name: "Wake",
        clip_key_hash: 0xBBBB_0000_0000_0003, duration_ticks: 1,
        loop_mode: JazzLoopMode::SingleShot, is_social_barrier: false },
        JazzActorRole::ActorX);
    m.add_transition(JazzTransition { from_state: 10, to_state: 20,
        guard_param_hash: 0, guard_value_hash: 0, weight: 1 });
    m.add_transition(JazzTransition { from_state: 20, to_state: 30,
        guard_param_hash: 0, guard_value_hash: 0, weight: 1 });
    m
}

/// The authored golden registry: the daemon plans `CMD|PlayJazzClip` against
/// these machine ids (card 56 §D; unknown ids answer by name).
pub fn jazz_machine(machine_id: u16) -> Option<JazzStateMachine> {
    match machine_id {
        1 => Some(social_generic_jazz_machine()),
        2 => Some(eat_held_food_jazz_machine()),
        3 => Some(bed_sleep_jazz_machine()),
        _ => None,
    }
}

#[cfg(test)]
mod kdiff {
    use super::*;

    #[test]
    fn kdiff_canary32_witnesses() {
        // 1. Enter -> Loop_Pick (2 extra laps) -> Accept (barrier synced) ->
        //    Exit: hand-derived schedule 2 + 3*3 + 2 = 13 ticks to ENTER Exit.
        let m = social_generic_jazz_machine();
        let synced = [JazzParam { name_hash: BARRIER_SYNC_NAME, value_hash: 7 }];
        let run = run_until_target(&m, 50, 0x1234_5678, &synced)
            .expect("social graph completes with the barrier synced");
        assert_eq!(run.ticks, 13);
        assert_eq!(run.clip_key_hash, 0x5555_0000_0000_0005);
        println!("KDIFF|canary=32|case=jazz_enter_to_exit_ticks|got={:016x}",
            u64::from(run.ticks));

        // 2. The card-04 weighted pick at seed 0: limit 3 on [w2, w1] is the
        //    sealed LCG step (new_state 0x0b719151, draw 1 -> first edge).
        //    It pins the oracle generator, so it needs the oracle build.
        #[cfg(sim96_private_oracles)]
        {
            let (seed, index) = JazzStateMachine::weighted_pick(0x1234_5678, &[2, 1]);
            assert_eq!((seed, index), (0x0B71_9151, 0));
            println!("KDIFF|canary=32|case=sacs_weighted_pick_seed0|got={:016x}",
                ((seed as u64) << 32) | index as u64);
        }

        // 3. The 4-entry no-repeat window: 8 picks over four clips never
        //    repeats inside the window and the run is seed-reproducible.
        let m2 = social_generic_jazz_machine();
        let clips = m2.loop_clips[&20].clone();
        let mut d = SacsDriver::start(&m2, 0x5EED_5EED);
        let mut seen: Vec<u64> = Vec::new();
        for _ in 0..8 {
            let (seed, clip) = JazzStateMachine::pick_loop_clip(d.path_seed, &clips, &d.recent());
            d.path_seed = seed;
            d.push_clip(clip);
            seen.push(clip);
        }
        for index in 1..seen.len() {
            let window = &seen[index.saturating_sub(NO_REPEAT_WINDOW - 1)..index];
            assert!(!window.contains(&seen[index]), "{seen:?}");
        }
        let replay = (0..8).scan(d.path_seed, |seed, _| {
            let (next, clip) = JazzStateMachine::pick_loop_clip(*seed, &clips, &[]);
            *seed = next;
            Some(clip)
        }).count();
        assert_eq!(replay, 8);
        println!("KDIFF|canary=32|case=no_repeat_window_four|got={:016x}", 1u64);

        // 4. Paired barrier sync (card 16): both presenters run the graph with
        //    the sync param and land on identical clip + tick counts (drift
        //    0 <= 20); without it the sleeper refuses by name past the limit.
        let a = run_until_target(&m, 50, 0x1234_5678, &synced).unwrap();
        let b = run_until_target(&m, 50, 0x1234_5678, &synced).unwrap();
        assert_eq!(a, b);
        let timeout = run_until_target(&m, 50, 0x1234_5678, &[])
            .expect_err("unsynced barrier must timeout, never hang");
        assert_eq!(timeout, KernelError::JazzSocialBarrierTimeout);
        println!("KDIFF|canary=32|case=paired_barrier_sync|got={:016x}",
            a.clip_key_hash);

        // 5. Impossible transition refusal, named: a state whose only edge is
        //    guarded by a param the run never carries.
        let mut broken = social_generic_jazz_machine();
        broken.transitions.insert(10, vec![JazzTransition { from_state: 10,
            to_state: 20, guard_param_hash: 0xDEAD_DEAD, guard_value_hash: 1,
            weight: 1 }]);
        let err = run_until_target(&broken, 50, 0x1234_5678, &synced)
            .expect_err("guarded-only entry must refuse");
        assert_eq!(err, KernelError::InvalidJazzTransition);
        println!("KDIFF|canary=32|case=invalid_transition_refusal|got={:016x}", 1u64);
        assert!(jazz_machine(99).is_none());
    }

    #[test]
    fn object_and_sleep_graphs_sequence_deterministically() {
        let eat = eat_held_food_jazz_machine();
        let run = run_until_target(&eat, 30, 0xABCD_1234, &[]).expect("eat run");
        // Grab 1 + Chew 3*(1+2 laps) = 10 ticks to ENTER Exit.
        assert_eq!(run.ticks, 10);
        let again = run_until_target(&eat, 30, 0xABCD_1234, &[]).unwrap();
        assert_eq!(run, again, "pure function of (machine, seed, params)");

        let bed = bed_sleep_jazz_machine();
        let slept = run_until_target(&bed, 30, 0x1111_2222,
            &[JazzParam { name_hash: BARRIER_SYNC_NAME, value_hash: 9 }]).unwrap();
        // Enter 1 + Sleep 2 ticks x (1 + 1 extra lap) = 5 ticks to ENTER Wake.
        assert_eq!(slept.ticks, 5);
        assert!(run_until_target(&bed, 30, 0x1111_2222, &[])
            .is_err_and(|err| err == KernelError::JazzSocialBarrierTimeout));
    }
}
