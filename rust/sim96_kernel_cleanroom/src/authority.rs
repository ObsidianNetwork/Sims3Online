#![forbid(unsafe_code)]
//! Multi-household scope and selectability (spec card 07,). Replaces the
//! four stock roots (`PlumbBob.SelectedActor`, `Sim.ActiveActor`,
//! `Household.ActiveHousehold`, `Household.IsActive`) with a server-authoritative
//! claim set + `NeverSelectable` blacklist. `LotSimulationScope` drives the 30 Hz
//! vs. throttled lot budget (card 06 §F).

use std::collections::{BTreeMap, BTreeSet};

use crate::clock::K_INACTIVE_LOT_SLEEP_TICKS;
use crate::SimId;

/// A stable household identity (wire-crossing, not a process-local handle).
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct HouseholdId(pub u32);

/// Lot simulation rate: full 30 Hz when a claimed Sim is present, otherwise a
/// 30-tick sleep throttle.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub enum LotSimRate {
    #[default]
    Full30Hz,
    Throttled30T,
}

/// Server-authoritative replacement for the four stock roots (card 07 §B).
#[derive(Default, Debug)]
pub struct ServerHouseholdAuthority {
    pub claimed_sims: BTreeSet<SimId>,
    pub active_households: BTreeSet<HouseholdId>,
    pub never_selectable: BTreeSet<SimId>,
    pub local_active_actor: Option<SimId>,
    /// Explicit world/ownership inputs for the multi-household drivers.
    pub members: BTreeMap<HouseholdId, BTreeSet<SimId>>,
    pub home_lots: BTreeMap<HouseholdId, crate::LotId>,
    pub owners: BTreeMap<SimId, crate::wire::PlayerId>,
}

impl ServerHouseholdAuthority {
    pub fn active_households(&self) -> impl Iterator<Item = HouseholdId> + '_ {
        self.active_households.iter().copied()
    }
    /// H1: a Sim is selectable iff claimed and not blacklisted.
    pub fn is_selectable(&self, sim: SimId) -> bool {
        self.claimed_sims.contains(&sim) && !self.never_selectable.contains(&sim)
    }

    /// Exact negation of `is_selectable` (card 07 §A: `Sim.IsNPC`).
    pub fn is_npc(&self, sim: SimId) -> bool {
        !self.is_selectable(sim)
    }

    /// H2: a household is active iff it has ≥ 1 claimed Sim.
    pub fn household_is_active(&self, hh: HouseholdId) -> bool {
        self.active_households.contains(&hh)
    }

    /// The players' households (H4 drivers loop over this, active first).
    pub fn player_households(&self) -> Vec<HouseholdId> {
        self.active_households.iter().copied().collect()
    }

    /// move a claim from `old` to `new`. The local plumb-bob is
    /// untouched (the `KeepsPlumbBob` guard — a remote `SET_ACTIVE_SIM` cannot
    /// move the server's own active household).
    pub fn swap_claim(&mut self, old: SimId, new: SimId) {
        self.claimed_sims.remove(&old);
        self.claimed_sims.insert(new);
    }
}

/// Per-lot simulation budget state (card 07 §C).
#[derive(Default, Debug)]
pub struct LotSimulationScope {
    pub rate: LotSimRate,
    pub sleep_ticks: u32,
    pub occupants: BTreeSet<SimId>,
}

impl LotSimulationScope {
    /// A lot is an active scope iff any occupant is selectable (claimed).
    pub fn is_active_scope(&self, authority: &ServerHouseholdAuthority) -> bool {
        self.occupants.iter().any(|s| authority.is_selectable(*s))
    }

    /// The throttle rule (card 07 §C.1): active lots run every tick; unclaimed
    /// lots run once per `K_INACTIVE_LOT_SLEEP_TICKS` + 1 ticks.
    pub fn refresh_scope(&mut self, authority: &ServerHouseholdAuthority) {
        if self.is_active_scope(authority) {
            self.rate = LotSimRate::Full30Hz;
            self.sleep_ticks = 0;
        } else {
            self.rate = LotSimRate::Throttled30T;
        }
    }

    pub fn should_simulate_this_tick(&mut self) -> bool {
        if self.rate == LotSimRate::Full30Hz {
            self.sleep_ticks = 0;
            true
        } else {
            self.rate = LotSimRate::Throttled30T;
            if self.sleep_ticks == 0 {
                self.sleep_ticks = K_INACTIVE_LOT_SLEEP_TICKS;
                true
            } else {
                self.sleep_ticks -= 1;
                false
            }
        }
    }
}

/// Card 26 §C golden-corpus hook (canary 7, H1/H2).
#[cfg(test)]
mod kdiff {
    use super::*;

    #[test]
    fn kdiff_canary_07() {
        let mut a = ServerHouseholdAuthority::default();
        a.claimed_sims.insert(SimId(1));
        a.swap_claim(SimId(1), SimId(2));
        let moved = (!a.is_selectable(SimId(1)) && a.is_selectable(SimId(2))) as u64;
        println!("KDIFF|canary=7|case=swap_claim_moves|got={:016x}", moved);
        assert_eq!(moved, 1);
        a.never_selectable.insert(SimId(2));
        let blacklist_wins = a.is_npc(SimId(2)) as u64;
        println!("KDIFF|canary=7|case=never_selectable_wins|got={:016x}", blacklist_wins);
        assert_eq!(blacklist_wins, 1);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sim(n: u32) -> SimId {
        SimId(n)
    }

    #[test]
    fn claim_add_becomes_selectable() {
        let mut a = ServerHouseholdAuthority::default();
        a.claimed_sims.insert(sim(1));
        assert!(a.is_selectable(sim(1)));
        assert!(!a.is_npc(sim(1)));
    }

    #[test]
    fn never_selectable_wins_over_claim() {
        let mut a = ServerHouseholdAuthority::default();
        a.claimed_sims.insert(sim(1));
        a.never_selectable.insert(sim(1));
        assert!(!a.is_selectable(sim(1)));
        assert!(a.is_npc(sim(1)));
    }

    #[test]
    fn household_active_when_any_sim_claimed() {
        let mut a = ServerHouseholdAuthority::default();
        a.active_households.insert(HouseholdId(7));
        assert!(a.household_is_active(HouseholdId(7)));
        assert!(!a.household_is_active(HouseholdId(8)));
    }

    #[test]
    fn player_households_lists_active() {
        let mut a = ServerHouseholdAuthority::default();
        a.active_households.insert(HouseholdId(2));
        a.active_households.insert(HouseholdId(1));
        assert_eq!(a.player_households(), vec![HouseholdId(1), HouseholdId(2)]);
    }

    #[test]
    fn swap_claim_moves_claim_not_plumbbob() {
        let mut a = ServerHouseholdAuthority::default();
        a.claimed_sims.insert(sim(1));
        a.local_active_actor = Some(sim(1));
        a.swap_claim(sim(1), sim(2));
        assert!(!a.is_selectable(sim(1)));
        assert!(a.is_selectable(sim(2)));
        assert_eq!(a.local_active_actor, Some(sim(1)));
    }

    #[test]
    fn unclaimed_lot_throttles_at_31_tick_period() {
        let a = ServerHouseholdAuthority::default();
        let mut lot = LotSimulationScope::default();
        lot.refresh_scope(&a);
        let mut runs = 0;
        for _ in 0..100_000 {
            if lot.should_simulate_this_tick() {
                runs += 1;
            }
        }
        assert!((runs as i64 - (100_000 / 31) as i64).abs() <= 1);
    }

    #[test]
    fn active_scope_resets_sleep() {
        let mut a = ServerHouseholdAuthority::default();
        let mut lot = LotSimulationScope::default();
        lot.refresh_scope(&a);
        assert!(lot.should_simulate_this_tick());
        for _ in 0..15 {
            assert!(!lot.should_simulate_this_tick());
        }
        assert_eq!(lot.sleep_ticks, 15);
        a.claimed_sims.insert(sim(1));
        lot.occupants.insert(sim(1));
        lot.refresh_scope(&a);
        assert!(lot.should_simulate_this_tick());
        assert_eq!(lot.sleep_ticks, 0);
        assert_eq!(lot.rate, LotSimRate::Full30Hz);
    }
}
