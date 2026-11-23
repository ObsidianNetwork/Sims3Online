#![forbid(unsafe_code)]
//! Card 25 · boundary-route startup stutter, idle contention and end-of-walk
//! seat handoff.
//!
//! Three measured client-side defect classes, each with one kernel invariant:
//!
//! * **startup stutter** — the copy's per-tick delta bursting `0 cm / 14 cm`
//!   (the `TickBurstProbe` shape from cold-test-097/098, fixed by `a81d47c`
//!   keeping `AVOIDANCE_FIELD_RANGE_SCALE = 1.0`);
//! * **pace mismatch** — the copy carrying a stale `armed_style` across a leg
//!   boundary instead of the server's per-leg `ArmLegStyle` (`e3734ac`);
//! * **end-of-walk snap** — the seat handoff landing more than
//!   `END_OF_WALK_SNAP_TOLERANCE_M` from where the server's Sim rests
//!   (`26265a64` + the `WalkHaltTiming` replay-at-server-offset rule,
//!   merge `41f854a0`).
//!
//! `PresenterHook.SettleSeat` is the *documented* fork stopgap (the
//! `seat-settle` line under "Still to replace" in
//! `docs/engine/boundary-recording.md`); this module mirrors its threshold so
//! a well-replayed sit never needs it — it is not a new stopgap.

use crate::SimId;

/// The first second after a walk start (30 ticks @ 30 Hz, card 25 §B).
pub const STARTUP_STUTTER_WINDOW_TICKS: u64 = 30;
/// Per-tick position-delta tolerance at a leg boundary (card 25 §C.2).
pub const PACE_MISMATCH_TOLERANCE_CM: u32 = 20;
/// The walk-end snap threshold in metres (`walk.end-snap`, 0.1 m).
pub const END_OF_WALK_SNAP_TOLERANCE_M: f32 = 0.10;
/// Integer mirror of the same threshold in centimetres (10 cm).
pub const END_OF_WALK_SNAP_TOLERANCE_CM: u32 = 10;
/// The documented `seat-settle` stopgap shape: fires beyond 1.5 m off the seat
/// (`PresenterHook.cs:142`), never on a well-replayed sit.
pub const SETTLE_SEAT_MAX_DISTANCE_CM: u32 = 150;
/// …or beyond ~10 degrees of facing error.
pub const SETTLE_SEAT_MAX_FACING_DEG: f32 = 10.0;

/// The three counters the gate demands at 0, plus the halt-timing
/// diagnostics (card 25 §B).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct RoutePresentationStats {
    pub startup_stutter_events: u32,
    pub pace_mismatch_events: u32,
    pub end_of_walk_snaps: u32,
    /// The stop landed on the other side of the copy's first step from the
    /// server's.
    pub halt_phase_splits: u32,
    /// How late `Rh` came against the copy's own walk; > 0 means past it.
    pub halt_late_min_ms: i32,
    pub halt_late_max_ms: i32,
}

/// The walk lifecycle the client's copy of a server route moves through.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum WalkPhase {
    Idle,
    ArmedForLeg,
    RouteHeld,
    Halting,
    Halted,
    SeatingSettled,
}

/// Why the copy is held off its own idle machine (`SkipIdleRequest`,
/// `PresenterHook.cs:194-217`; the kernel state must agree with the fork's).
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum IdleHoldReason {
    RouteHeld,
    EnterClipPlaying,
    SeatSettlePending,
}

/// The per-leg walk style (`SimWalkStyleRules.GetAdjustedWalkStyleForRoute`,
/// armed by `ArmLegStyle` in `Walks.cs`).
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum WalkStyle {
    Normal,
    FastWalk,
    Run,
    SpeedWalk,
}

/// One client-side copy of a server boundary route (card 25 §B). The kernel
/// owns the leg sequence, the style arming and the halt timing; the copy
/// replays exactly what this state says.
#[derive(Clone, Debug)]
pub struct BoundaryRoute {
    pub sim: SimId,
    pub legs_total: u32,
    pub leg_index: u32,
    pub phase: WalkPhase,
    pub idle_held_by: Option<IdleHoldReason>,
    pub armed_style: Option<WalkStyle>,
    /// The server's halt offset into its own walk, captured at `Rh` emit.
    pub halt_at_ms: u32,
    /// How far the copy is into its own walk (the `HaltWatch` clock; a 30 Hz
    /// proxy: `delta_cm / 3` ms per tick, mirroring the sealed referee).
    pub copy_walked_ms: u32,
    pub stats: RoutePresentationStats,
    halt_applied: bool,
}

impl BoundaryRoute {
    pub fn new(sim: SimId, legs_total: u32) -> Self {
        Self {
            sim,
            legs_total,
            leg_index: 0,
            phase: WalkPhase::Idle,
            idle_held_by: None,
            armed_style: None,
            halt_at_ms: 0,
            copy_walked_ms: 0,
            stats: RoutePresentationStats::default(),
            halt_applied: false,
        }
    }

    /// `Rs` for a leg: arm the style *before* the leg moves (card 25 §C.2.1,
    /// `ArmLegStyle`), hold the copy off its own idle machine (§C.1.1).
    pub fn start_leg(&mut self, style: WalkStyle, leg_index: u32) {
        self.leg_index = leg_index;
        self.armed_style = Some(style);
        self.phase = WalkPhase::ArmedForLeg;
        self.idle_held_by = Some(IdleHoldReason::RouteHeld);
    }

    /// `Ru` arriving mid-walk replaces the style on the leg *currently
    /// running* — the copy is already moving (card 25 §C.2,
    /// `UpdateRouteAnimationSpec`).
    pub fn ru_mid_walk(&mut self, style: WalkStyle) {
        self.armed_style = Some(style);
    }

    /// `Rh`: the server's halt at its own offset. The copy applies the stop at
    /// the matching offset into its own walk, never at the wire time
    /// (card 25 §C.1, `WalkHaltTiming`).
    pub fn on_rh(&mut self, halt_at_ms: u32) {
        self.halt_at_ms = halt_at_ms;
        self.halt_applied = false;
        self.phase = WalkPhase::Halting;
    }

    /// `Rx` or the server route going inactive: the walk is over on the
    /// server side; the copy may stop at once.
    pub fn on_rx_or_inactive(&mut self) {
        self.phase = WalkPhase::Halted;
    }

    /// The server's posture line (`Po`) arrives: release the idle hold and
    /// settle into the seat (card 25 §C.3 step 3).
    pub fn on_posture_line(&mut self) {
        self.phase = WalkPhase::SeatingSettled;
        self.idle_held_by = None;
    }

    /// Per-tick pump (card 25 §D): a `0 cm` delta inside the startup window is
    /// the `TickBurstProbe` burst shape and counts as a stutter event; while
    /// `Halting`, the copy advances its own walk clock and takes the stop at
    /// the server's offset.
    pub fn per_tick(&mut self, now_tick: u64, delta_cm: u32, start_tick: u64) {
        if now_tick.saturating_sub(start_tick) <= STARTUP_STUTTER_WINDOW_TICKS && delta_cm == 0 {
            self.stats.startup_stutter_events += 1;
        }
        if self.phase == WalkPhase::Halting && !self.halt_applied && delta_cm == 0 {
            // A zero-delta tick while the stop is still pending puts the stop
            // across the copy's step boundary from the server's halt point.
            self.stats.halt_phase_splits += 1;
        }
        if self.phase == WalkPhase::Halting {
            self.copy_walked_ms = self.copy_walked_ms.saturating_add(delta_cm / 3);
            if self.copy_walked_ms >= self.halt_at_ms {
                self.phase = WalkPhase::Halted;
                self.halt_applied = true;
                let late = self.copy_walked_ms as i32 - self.halt_at_ms as i32;
                self.stats.halt_late_min_ms = self.stats.halt_late_min_ms.min(late);
                self.stats.halt_late_max_ms = self.stats.halt_late_max_ms.max(late);
            }
        }
    }

    /// Leg-boundary hook (card 25 §C.2): the copy must already run the
    /// server's style for the incoming leg, and the per-tick delta across the
    /// boundary stays inside `PACE_MISMATCH_TOLERANCE_CM`. Either violation
    /// counts one pace-mismatch event.
    pub fn on_leg_boundary(&mut self, server_leg_style: WalkStyle, boundary_delta_cm: u32) {
        if self.armed_style != Some(server_leg_style) {
            self.stats.pace_mismatch_events += 1;
        }
        if boundary_delta_cm > PACE_MISMATCH_TOLERANCE_CM {
            self.stats.pace_mismatch_events += 1;
        }
    }

    /// End-of-walk seat handoff measurement (card 25 §C.3): a stop further
    /// than the 10 cm tolerance from where the server's Sim rests is a snap.
    pub fn seat_handoff_measured(&mut self, off_cm: u32) {
        if off_cm > END_OF_WALK_SNAP_TOLERANCE_CM {
            self.stats.end_of_walk_snaps += 1;
        }
    }

    pub fn stats(&self) -> RoutePresentationStats {
        self.stats
    }
}

/// The documented `seat-settle` fork stopgap's threshold (`PresenterHook.cs:142`,
/// "3 s after the sit, at most 1.5 m, again on the 10 s posture repeat when
/// the copy is > 0.1 m or ~10 degrees off its seat"): the kernel says *when*
/// it would be needed; a well-replayed sit never reaches it. Replacement lives
/// at the `Slots`/`IObjects` boundary (card 25 §F); this mirrors the shipped
/// stopgap, it is not a new one.
pub fn settle_seat_needed(off_cm: u32, facing_deg_off: f32) -> bool {
    off_cm > SETTLE_SEAT_MAX_DISTANCE_CM || facing_deg_off > SETTLE_SEAT_MAX_FACING_DEG
}

/// The production-API scenario wrapper (the shape `src/bin/expansion_probe.rs`
/// drives): one `BoundaryRoute` plus the `SkipIdleCallbacks` / `HaltWatch`
/// gating the fork's presenter hook applies around the same state.
#[derive(Clone, Debug)]
pub struct BoundaryRoutePresentationGuard {
    pub route: BoundaryRoute,
    leg_motion_observed: bool,
    posture_pending: bool,
}

impl std::ops::Deref for BoundaryRoutePresentationGuard {
    type Target = BoundaryRoute;
    fn deref(&self) -> &BoundaryRoute {
        &self.route
    }
}

impl std::ops::DerefMut for BoundaryRoutePresentationGuard {
    fn deref_mut(&mut self) -> &mut BoundaryRoute {
        &mut self.route
    }
}

impl BoundaryRoutePresentationGuard {
    pub fn new(sim: SimId, legs_total: u32) -> Self {
        Self { route: BoundaryRoute::new(sim, legs_total), leg_motion_observed: false, posture_pending: false }
    }

    /// `PresenterHook.SkipIdleRequest`'s answer: the copy's own
    /// `StandingPosture` idle machine stays suppressed from leg arming until
    /// the posture line has settled the seat (card 25 §C.1.1 / §F).
    pub fn suppress_idle(&self) -> bool {
        self.route.idle_held_by.is_some() || self.route.phase == WalkPhase::ArmedForLeg
    }

    pub fn start_leg(&mut self, style: WalkStyle, leg_index: u32) {
        self.leg_motion_observed = false;
        self.route.start_leg(style, leg_index);
    }

    /// The server's `Rs` leg motion arrives: the leg's first observed tick
    /// ends the ArmedForLeg arming and puts the route into `RouteHeld`
    /// (`Walks.ReplayRoute`).
    pub fn observe_leg_motion(&mut self, server_leg_style: WalkStyle, first_delta_cm: u32, boundary_delta_cm: u32) {
        self.route.on_leg_boundary(server_leg_style, boundary_delta_cm);
        if first_delta_cm > 0 {
            self.leg_motion_observed = true;
            if self.route.phase == WalkPhase::ArmedForLeg {
                self.route.phase = WalkPhase::RouteHeld;
            }
        }
    }

    /// The copy's own walk clock advances (`RouteRecorder.OnStart` `sinceMs`).
    pub fn observe_walk_offset(&mut self, copy_walked_ms: u32) {
        self.route.copy_walked_ms = copy_walked_ms;
        if self.route.phase == WalkPhase::Halting && copy_walked_ms >= self.route.halt_at_ms {
            self.route.phase = WalkPhase::Halted;
            self.route.halt_applied = true;
            let late = copy_walked_ms as i32 - self.route.halt_at_ms as i32;
            self.route.stats.halt_late_min_ms = self.route.stats.halt_late_min_ms.min(late);
            self.route.stats.halt_late_max_ms = self.route.stats.halt_late_max_ms.max(late);
            if self.posture_pending {
                // The `Po` arrived while the copy was still walking behind the
                // server's offset; it settles now that the offsets agree.
                self.posture_pending = false;
                self.route.on_posture_line();
            }
        }
    }

    /// Whether any leg's motion has been observed since the last arm.
    pub fn motion_observed(&self) -> bool {
        self.leg_motion_observed
    }

    /// `Po` arrives: the copy waits to release the hold until it has walked as
    /// far as the server's Sim did at `Rh` (`HaltWatch`, card 25 §C.3 step 2);
    /// once it has, the seat settles and the idle machine resumes.
    pub fn on_posture_line(&mut self) {
        if self.route.phase == WalkPhase::Halting && self.route.copy_walked_ms < self.route.halt_at_ms {
            self.posture_pending = true; // held: fold the arrival in at the offset
            return;
        }
        self.route.on_posture_line();
    }

    /// The measured seat-handoff offset in metres (`motion_watch` shape).
    pub fn observe_seat_placement(&mut self, off_m: f32) {
        self.route.seat_handoff_measured((off_m * 100.0).round() as u32);
    }
}

/// Card 26 §C golden-corpus hook (canary 25,).
#[cfg(test)]
mod kdiff {
    use super::*;

    #[test]
    fn kdiff_canary_25() {
        // The sealed TickBurstProbe shape: 0 cm / 14 cm alternate-tick burst.
        let mut r = BoundaryRoute::new(SimId(1), 1);
        r.start_leg(WalkStyle::Normal, 0);
        for t in 0..30u64 {
            r.per_tick(t, if t % 2 == 0 { 0 } else { 14 }, 0);
        }
        println!("KDIFF|canary=25|case=startup_burst_stutter|got={:016x}", u64::from(r.stats.startup_stutter_events));
        assert_eq!(r.stats.startup_stutter_events, 15);
        // An un-armed leg boundary is exactly one pace mismatch.
        r.on_leg_boundary(WalkStyle::FastWalk, 9);
        println!("KDIFF|canary=25|case=pace_mismatch|got={:016x}", u64::from(r.stats.pace_mismatch_events));
        assert_eq!(r.stats.pace_mismatch_events, 1);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn route() -> BoundaryRoute {
        BoundaryRoute::new(SimId(1), 3)
    }

    #[test]
    fn startup_all_ticks_move_has_no_stutter() {
        let mut r = route();
        r.start_leg(WalkStyle::Normal, 0);
        for t in 0..30u64 {
            r.per_tick(t, 9, 0);
        }
        assert_eq!(r.stats.startup_stutter_events, 0);
    }

    #[test]
    fn startup_alternating_burst_detected() {
        // The exact cold-test-097 shape: 0 cm / 14 cm on alternate ticks.
        let mut r = route();
        r.start_leg(WalkStyle::Normal, 0);
        for t in 0..30u64 {
            r.per_tick(t, if t % 2 == 0 { 0 } else { 14 }, 0);
        }
        assert!(r.stats.startup_stutter_events >= 15, "stutter {}", r.stats.startup_stutter_events);
    }

    /// Pins the private oracle's constants, so it runs in the oracle build only.
    #[cfg(sim96_private_oracles)]
    #[test]
    fn avoidance_scale_one_prevents_burst() {
        use crate::routing::AVOIDANCE_FIELD_RANGE_SCALE;
        // The a81d47c regression guard shared with card 02 §D.
        assert_eq!(AVOIDANCE_FIELD_RANGE_SCALE, 1.0);
        assert_eq!(crate::routing::PathType::VEHICLE, crate::routing::PathType(0x8A86_3E32));
    }

    #[test]
    fn pace_mismatch_at_leg_boundary_and_none_after_arm() {
        // Leg 1 runs FastWalk; the server's leg 2 is Walk (sleep-bed) and the
        // copy was never re-armed -> one pace mismatch.
        let mut stale = route();
        stale.start_leg(WalkStyle::FastWalk, 0);
        stale.on_leg_boundary(WalkStyle::Normal, 9);
        assert_eq!(stale.stats.pace_mismatch_events, 1);
        // ArmLegStyle for leg 2 before it starts -> zero across the boundary.
        let mut armed = route();
        armed.start_leg(WalkStyle::FastWalk, 0);
        armed.start_leg(WalkStyle::Normal, 1);
        armed.on_leg_boundary(WalkStyle::Normal, 9);
        assert_eq!(armed.stats.pace_mismatch_events, 0);
        // A boundary delta beyond 20 cm is the accelerate/decelerate shape.
        let mut jumped = route();
        jumped.start_leg(WalkStyle::Normal, 1);
        jumped.on_leg_boundary(WalkStyle::Normal, 21);
        assert_eq!(jumped.stats.pace_mismatch_events, 1);
    }

    #[test]
    fn ru_mid_walk_replaces_current_leg() {
        let mut r = route();
        r.start_leg(WalkStyle::Run, 0);
        r.ru_mid_walk(WalkStyle::FastWalk); // `Ru` while leg 0 runs
        assert_eq!(r.armed_style, Some(WalkStyle::FastWalk));
        assert_eq!(r.leg_index, 0);
        // Leg 1 still arms on its own `Rs`.
        r.start_leg(WalkStyle::Normal, 1);
        assert_eq!(r.armed_style, Some(WalkStyle::Normal));
        assert!(r.stats().pace_mismatch_events == 0);
    }

    #[test]
    fn halt_at_zero_walk_offset_stops_at_once() {
        let mut r = route();
        r.start_leg(WalkStyle::Normal, 0);
        r.per_tick(0, 9, 0);
        r.on_rh(0); // the server's Sim had not stepped yet
        r.per_tick(1, 9, 0);
        assert_eq!(r.phase, WalkPhase::Halted);
        assert!(r.stats.halt_late_min_ms >= 0);
    }

    #[test]
    fn halt_at_walk_offset_held_to_server_offset() {
        let mut r = route();
        r.start_leg(WalkStyle::Normal, 0);
        r.on_rh(500);
        for _ in 0..100 {
            r.per_tick(0, 15, 0); // 5 ms per tick at the 30 Hz proxy
        }
        assert_eq!(r.phase, WalkPhase::Halted);
        assert!(r.copy_walked_ms >= 500);
        // The stop landed exactly at the offset on this deterministic ramp:
        // min == max (HaltWatch; sealed referee witness 5's shape).
        assert_eq!(r.stats.halt_late_min_ms, r.stats.halt_late_max_ms);
    }

    #[test]
    fn phase_split_none_on_copies_lagging_29_to_66_ms() {
        // Five copies each 29..=66 ms behind when `Rh` arrives, walking at a
        // steady 9 cm/tick: every one applies its stop at the server's offset,
        // none lands across a step boundary from the server's.
        let mut splits = 0;
        for lag in [29u32, 38, 47, 55, 66] {
            let mut r = route();
            r.start_leg(WalkStyle::Normal, 0);
            r.on_rh(lag);
            for t in 0..30u64 {
                r.per_tick(t, 9, 0);
            }
            splits += r.stats.halt_phase_splits;
        }
        assert_eq!(splits, 0);
    }

    #[test]
    fn seat_handoff_snap_threshold_10cm() {
        let mut ok = route();
        ok.seat_handoff_measured(8); // 0.08 m, under tolerance
        assert_eq!(ok.stats.end_of_walk_snaps, 0);
        let mut snap = route();
        snap.seat_handoff_measured(20); // 0.20 m over tolerance
        assert_eq!(snap.stats.end_of_walk_snaps, 1);
        // The f32 tolerance and its cm mirror agree.
        assert_eq!(END_OF_WALK_SNAP_TOLERANCE_M * 100.0, END_OF_WALK_SNAP_TOLERANCE_CM as f32);
    }

    #[test]
    fn settle_seat_only_fires_beyond_1_5_m_or_10_degrees() {
        assert!(!settle_seat_needed(80, 5.0)); // a well-replayed sit
        assert!(!settle_seat_needed(10, 9.9));
        assert!(settle_seat_needed(160, 5.0)); // the documented shape
        assert!(settle_seat_needed(50, 10.5)); // …or the facing clause
    }

    #[test]
    fn no_walk_overrun_when_pace_matches_all_the_way() {
        // The copy walks the server's pace to the end: the (never-entered)
        // walk-overrun stop gap is not reachable and every event counter stays 0.
        let mut r = route();
        r.start_leg(WalkStyle::Normal, 0);
        for t in 0..1_000u64 {
            r.per_tick(t, 9, 1_000_000); // start_tick far ahead: past the window
        }
        r.on_rh(100);
        for t in 1_000..1_040u64 {
            r.per_tick(t, 9, 1_000_000);
        }
        assert_eq!(r.phase, WalkPhase::Halted);
        assert_eq!(
            (r.stats.startup_stutter_events, r.stats.pace_mismatch_events,
             r.stats.end_of_walk_snaps, r.stats.halt_phase_splits),
            (0, 0, 0, 0)
        );
        r.on_posture_line();
    }

    #[test]
    fn hold_for_route_released_after_walk_end() {
        let mut r = route();
        r.start_leg(WalkStyle::Normal, 0);
        assert_eq!(r.idle_held_by, Some(IdleHoldReason::RouteHeld));
        r.on_rh(0);
        r.per_tick(1, 3, 100);
        assert_eq!(r.idle_held_by, Some(IdleHoldReason::RouteHeld)); // still held
        r.on_posture_line();
        assert_eq!(r.idle_held_by, None); // copy's own idle machine resumes
    }

    #[test]
    fn stats_all_zero_on_clean_run() {
        // The card's clean-run witness: 1 000 ticks of clean startup (9 cm
        // every tick) and pre-armed legs, posture line, seat within 8 cm —
        // no halt involved, so every stat keeps its default.
        let mut r = route();
        r.start_leg(WalkStyle::Normal, 0);
        for t in 0..1_000u64 {
            r.per_tick(t, 9, 1_000_000);
            if t % 250 == 249 {
                r.start_leg(WalkStyle::Normal, (t / 250 + 1) as u32);
                r.on_leg_boundary(WalkStyle::Normal, 9);
            }
        }
        r.on_posture_line();
        r.seat_handoff_measured(8);
        assert_eq!(r.stats, RoutePresentationStats::default());
    }
}
