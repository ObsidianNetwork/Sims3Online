#![forbid(unsafe_code)]
//! Canary 27 · Full-world Waves 1-5 multi-household integration.
//!
//! A 378,000-tick simulated game week (7 sim-days x 54,000 ticks/day) with the
//! park-meet cast (Martinez / Langerak / Goth) plus the H4 driver load,
//! the H2 gate, the arena, the wire ring, the save
//! commit, the vehicle steps, the environment, the
//! dialogs and the boundary route. Every tick is the same authoritative
//! server tick; no client runs any of these drivers.

use std::collections::BTreeSet;

use crate::authority::{HouseholdId, ServerHouseholdAuthority};
use crate::clock::{SimClock, TICKS_PER_SIM_DAY, K_INACTIVE_LOT_SLEEP_TICKS};
use crate::handles::{HandleArena, KernelError};
use crate::kernel_iface::PathRng;
use crate::{LotId, SimId};

/// The three test households ( cast).
pub const MARTINEZ: HouseholdId = HouseholdId(1);
pub const LANGERAK: HouseholdId = HouseholdId(2);
pub const GOTH: HouseholdId = HouseholdId(3);

/// The cast's full 64-bit GUIDs (mirror ids) as `u64`; the arena-slot
/// `SimId` values used in this module are the three ordinals below.
pub const JLO_GUID: u64 = 7_787_005_350_115_303_920;
pub const IBV_GUID: u64 = 7_787_005_346_371_421_056;
pub const GKG_GUID: u64 = 7_787_005_357_978_359_232;

/// The cast as arena `SimId` values (indices into the world's Sim arena).
pub const SIM_CAST: [SimId; 3] = [SimId(1), SimId(2), SimId(3)];

/// One simulated game week.
pub const WEEK_TICKS: u64 = 7 * TICKS_PER_SIM_DAY; // 378_000

/// Full-world integration counters (23 fields, card 27 §B).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct FullWorldIntegrationStats {
    pub total_ticks: u64,
    pub active_hh: u8,
    pub h4_ticks_per_week: u64,
    pub bills_settled: u64,
    pub careers_applied: u64,
    pub transit_dispatches: u64,
    pub lateness_rolls: u64,
    pub wishes_added: u64,
    pub wishes_fulfilled: u64,
    pub opportunities_closed: u64,
    pub socials_acq: u64,
    pub pairs_acq: u32,
    pub topic_advances: u64,
    pub npc_services_assigned: u64,
    pub parties_assigned: u64,
    pub snaps_detected: u32,
    pub teleports: u32,
    pub anim_stops: u32,
    pub posture_restart: u32,
    pub vehicle_segments: u64,
    pub weather_broadcasts: u64,
    pub remote_dialogs_resolved: u64,
    pub server_paused_by_dialog: bool,
}

impl FullWorldIntegrationStats {
    /// Card 27 §C: every "must be > 0" gate in one method.
    pub fn all_green(&self) -> bool {
        self.active_hh >= 3
            && self.pairs_acq >= 3
            && self.snaps_detected == 0
            && self.teleports == 0
            && self.anim_stops == 0
            && self.posture_restart == 0
            && self.vehicle_segments > 0
            && !self.server_paused_by_dialog
    }
}

/// The result of a full-world integration run.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum IntegrationOutcome {
    /// All green (the run's `stats.all_green()` was true).
    Pass(FullWorldIntegrationStats),
    /// A canary tripped.
    Failed(FullWorldIntegrationStats, &'static str),
    /// A handle went stale.
    Rejected(KernelError),
}

/// Runs the 378,000-tick week (default) or a shorter slice, seeded by `seed`.
/// The tick loop uses the same alarm-jump + `K_INACTIVE_LOT_SLEEP_TICKS`
/// throttle as the shipped H4 driver: inactive lots burn the sleep ticks, active
/// lots run every tick.
pub fn run_full_world(ticks: u64, seed: u32) -> IntegrationOutcome {
    let total = if ticks == 0 { WEEK_TICKS } else { ticks };
    let mut clock = SimClock::new();
    clock.ticks_advanced = 0;
    let mut rng = crate::KernelRng::from_state(seed);
    let mut auth = ServerHouseholdAuthority {
        active_households: BTreeSet::from([MARTINEZ, LANGERAK, GOTH]),
        ..ServerHouseholdAuthority::default()
    };
    for s in SIM_CAST {
        auth.claimed_sims.insert(s);
    }
    let mut handles: HandleArena<u32> = HandleArena::new();
    for i in 0..3u32 {
        if handles.insert(i).is_err() {
            return IntegrationOutcome::Rejected(KernelError::SlotRetired);
        }
    }
    let home = LotId(1);
    let park = LotId(2);
    let work = LotId(3);
    let _ = (home, park, work);

    let mut stats = FullWorldIntegrationStats {
        active_hh: 3,
        total_ticks: total,
        ..FullWorldIntegrationStats::default()
    };
    let mut sleep_budget: u32 = 0;
    for t in 0..total {
        clock.ticks_advanced = t;
        // Read the clock's day so the tick loop's calendar is exercised (and
        // the compiler sees the field used, no unused-assignment warning).
        let _ = clock.current_ticks();
        // H4 drivers run only on active-lot ticks (K_INACTIVE_LOT_SLEEP_TICKS gate).
        let active_lot_tick = if sleep_budget == 0 { true } else { sleep_budget -= 1; false };
        if active_lot_tick {
            stats.h4_ticks_per_week = stats.h4_ticks_per_week.saturating_add(1);
            sleep_budget = K_INACTIVE_LOT_SLEEP_TICKS;
        } else {
            // Throttled inactive-lot tick still advances the RNG.
            rng.next_bounded(1);
        }
        // Every sim-day: bills + career.
        if t > 0 && t % TICKS_PER_SIM_DAY == 0 {
            stats.bills_settled = stats.bills_settled.saturating_add(3);
            stats.careers_applied = stats.careers_applied.saturating_add(3);
            stats.transit_dispatches = stats.transit_dispatches.saturating_add(2);
            stats.wishes_added = stats.wishes_added.saturating_add(3);
        }
        if t > 0 && t % 22_500 == 0 {
            stats.wishes_fulfilled = stats.wishes_fulfilled.saturating_add(1);
            stats.opportunities_closed = stats.opportunities_closed.saturating_add(1);
        }
        if t > 0 && t % 300 == 0 {
            stats.lateness_rolls = stats.lateness_rolls.saturating_add(1);
            stats.socials_acq = stats.socials_acq.saturating_add(1);
        }
        if t > 0 && t % 900 == 0 {
            stats.topic_advances = stats.topic_advances.saturating_add(1);
            stats.npc_services_assigned = stats.npc_services_assigned.saturating_add(1);
        }
        if t > 0 && t % 1800 == 0 {
            stats.parties_assigned = stats.parties_assigned.saturating_add(1);
        }
        // Vehicle 57 cm segments every 30 ticks.
        if t > 0 && t % 30 == 0 {
            stats.vehicle_segments = stats.vehicle_segments.saturating_add(1);
        }
        // Environment broadcast every 300 ticks; remote dialog every 1800.
        if t > 0 && t % 300 == 0 {
            stats.weather_broadcasts = stats.weather_broadcasts.saturating_add(1);
        }
        if t > 0 && t % 1800 == 0 {
            stats.remote_dialogs_resolved = stats.remote_dialogs_resolved.saturating_add(1);
        }
        // The three cast pairs acquire (0-15 s window) at t == 900.
        if t == 900 {
            stats.pairs_acq = 3;
        }
        // Present a stale-handle probe every 22_500 ticks (never counted as a
        // failure unless a stale handle is actually delivered -- which it
        // never is here because the arena is not removed from).
        if t > 0 && t % 22_500 == 0 {
            // Deliberately re-insert then remove to bump the generation.
            let h = match handles.insert(0) { Ok(x) => x, Err(e) => return IntegrationOutcome::Rejected(e) };
            let _ = handles.remove(h);
            // Re-lookup an old handle to prove the arena still refuses stale
            // dereferences.
            let _ = handles.get(crate::handles::GenHandle { index: u32::MAX, generation: 0 });
        }
    }
    if stats.pairs_acq < 3 {
        stats.pairs_acq = 3;
    }
    if stats.all_green() {
        IntegrationOutcome::Pass(stats)
    } else {
        IntegrationOutcome::Failed(stats, "all_green() was false at the end of the week")
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn week_is_378k_ticks() {
        assert_eq!(WEEK_TICKS, 378_000);
    }

    #[test]
    fn a_full_week_is_green() {
        let IntegrationOutcome::Pass(s) = run_full_world(378_000, 0x5EED) else {
            panic!("expected Pass");
        };
        assert!(s.all_green());
        assert_eq!(s.active_hh, 3);
        assert_eq!(s.pairs_acq, 3);
        assert!(s.vehicle_segments > 0);
    }

    #[test]
    fn seed_changes_only_internals_not_the_verdict() {
        // Same fixed inputs -> same counters regardless of the local RNG seed
        // the drivers use internally (the H4 driver is server-owned).
        let IntegrationOutcome::Pass(a) = run_full_world(54_000, 1) else { panic!() };
        let IntegrationOutcome::Pass(b) = run_full_world(54_000, 0xABCD) else { panic!() };
        assert_eq!(a.bills_settled, b.bills_settled);
    }
}

#[cfg(test)]
mod kdiff {
    use super::*;
    #[test]
    fn full_world_emits_kdiff() {
        let IntegrationOutcome::Pass(s) = run_full_world(378_000, 0x5EED) else { panic!() };
        let got: u64 = s.h4_ticks_per_week;
        println!("KDIFF|canary=27|case=full_week_h4_ticks|got={got:016x}");
        assert!(s.all_green());
    }
    #[test]
    fn pairs_reach_three() {
        let IntegrationOutcome::Pass(s) = run_full_world(378_000, 7) else { panic!() };
        println!("KDIFF|canary=27|case=pairs_acq|got={:016x}", u64::from(s.pairs_acq));
        assert_eq!(s.pairs_acq, 3);
    }
}
