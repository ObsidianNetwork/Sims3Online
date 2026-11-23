#![forbid(unsafe_code)]
//! Server dispatch triggers and manifests; vehicle motion remains in vehicles.
use std::collections::{BTreeMap, BTreeSet};
use crate::{SimId, LotId, authority::{HouseholdId, ServerHouseholdAuthority},
    dialogs::{DialogKind, DialogRequest, RemoteModalDialogRouter},
    kernel_iface::PathRng, vehicles::VehicleGuid};
pub const SHIFT_DISPATCH_WINDOW_TICKS: u64 = 600;
#[derive(Clone, Copy, Debug)]
pub struct CareerShift {
    pub sim: SimId, pub occupation_id: u32, pub lot_id: LotId,
    pub start_tick: u64, pub end_tick: u64, pub dispatch_at: u64,
}
impl CareerShift {
    pub fn new(sim: SimId, occupation_id: u32, lot_id: LotId, start_tick: u64, end_tick: u64) -> Self {
        Self { sim, occupation_id, lot_id, start_tick, end_tick,
            dispatch_at: start_tick.saturating_sub(SHIFT_DISPATCH_WINDOW_TICKS) }
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum TransitKind { SchoolBus, Carpool, TakeHome, NpcTaxi }
#[derive(Clone, Debug)]
pub struct TransitDispatch {
    pub sim: SimId, pub lot_id: LotId, pub at_tick: u64, pub kind: TransitKind,
    pub passengers: Vec<SimId>, pub vehicle_guid: Option<VehicleGuid>,
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CareerEventRoll {
    pub sim: SimId, pub at_tick: u64, pub roll_value: u32, pub event_id: Option<u32>,
}
#[derive(Debug, Default)]
pub struct CareerAndTransitScheduler {
    pub active_shifts: BTreeMap<SimId, Vec<CareerShift>>,
    pub dispatches: Vec<TransitDispatch>, pub rolls: Vec<CareerEventRoll>, pub next_handle: u64,
    /// Resolved tuning table; no native career-event definitions are guessed.
    pub event_ids: BTreeMap<SimId, Vec<u32>>,
    pub authority: ServerHouseholdAuthority,
    sent: BTreeSet<(SimId, u64, TransitKind)>,
}
impl CareerAndTransitScheduler {
    pub fn roll_for_event(&mut self, sim: SimId, rng: &mut impl PathRng, now: u64) -> &CareerEventRoll {
        rng.next_bounded(u32::MAX);
        let roll_value = rng.state();
        let event_id = self.event_ids.get(&sim).and_then(|ids| {
            if ids.is_empty() { None } else {
                let index = usize::try_from(roll_value).ok()? % ids.len();
                ids.get(index).copied()
            }
        });
        self.rolls.push(CareerEventRoll { sim, at_tick: now, roll_value, event_id });
        &self.rolls[self.rolls.len() - 1]
    }
    fn dispatch(&mut self, shift: CareerShift, kind: TransitKind, passengers: Vec<SimId>, now: u64) -> Option<u64> {
        let key = (shift.sim, shift.start_tick, kind);
        if self.sent.contains(&key) { return None; }
        let handle = self.next_handle.checked_add(1)?;
        self.next_handle = handle;
        self.sent.insert(key);
        self.dispatches.push(TransitDispatch { sim: shift.sim, lot_id: shift.lot_id, at_tick: now,
            kind, passengers, vehicle_guid: None });
        Some(handle)
    }
    pub fn tick_bus_dispatch(&mut self, a: &ServerHouseholdAuthority,
        children_of: &dyn Fn(HouseholdId) -> Vec<SimId>, now: u64) -> Vec<u64> {
        let mut due = Vec::new();
        for hh in a.active_households() {
            for sim in children_of(hh).into_iter().collect::<BTreeSet<_>>() {
                if !a.is_selectable(sim) || !a.members.get(&hh).is_some_and(|m| m.contains(&sim)) { continue; }
                for shift in self.active_shifts.get(&sim).into_iter().flatten() {
                    if now >= shift.start_tick.saturating_sub(SHIFT_DISPATCH_WINDOW_TICKS) && now <= shift.start_tick {
                        due.push(*shift);
                    }
                }
            }
        }
        due.into_iter().filter_map(|s| self.dispatch(s, TransitKind::SchoolBus, vec![s.sim], now)).collect()
    }
    pub fn tick_carpool_dispatch(&mut self, a: &ServerHouseholdAuthority,
        workers_of: &dyn Fn(LotId) -> Vec<SimId>, now: u64) -> Vec<u64> {
        let shifts: Vec<_> = self.active_shifts.values().flatten().copied().collect();
        let mut groups = BTreeSet::new();
        let mut out = Vec::new();
        for s in &shifts {
            if !a.is_selectable(s.sim) { continue; }
            let kind = if now >= s.end_tick { TransitKind::TakeHome }
                else if now >= s.start_tick.saturating_sub(SHIFT_DISPATCH_WINDOW_TICKS) && now <= s.start_tick { TransitKind::Carpool }
                else { continue };
            if !groups.insert((s.lot_id, s.start_tick, s.end_tick, kind)) { continue; }
            let passengers: Vec<_> = workers_of(s.lot_id).into_iter().filter(|sim|
                a.is_selectable(*sim) && shifts.iter().any(|other| other.sim == *sim
                    && other.lot_id == s.lot_id && other.start_tick == s.start_tick && other.end_tick == s.end_tick))
                .collect::<BTreeSet<_>>().into_iter().collect();
            let Some(&first) = passengers.first() else { continue };
            let representative = CareerShift { sim: first, ..*s };
            if let Some(h) = self.dispatch(representative, kind, passengers, now) { out.push(h); }
        }
        self.active_shifts.values_mut().for_each(|ss| ss.retain(|s| s.end_tick > now));
        out
    }
    /// Caller supplies the server authority directory on the scheduler.
    pub fn dispatch_career_dialog(&mut self, sim: SimId, kind: DialogKind,
        router: &mut RemoteModalDialogRouter, now: u64) {
        let Some(&owner_player) = self.authority.owners.get(&sim) else { return };
        if !self.authority.is_selectable(sim) { return; }
        let Some(id) = self.next_handle.checked_add(1) else { return };
        self.next_handle = id;
        router.request_for_claimed_sim(DialogRequest { corr: id, id, sim, owner_player,
            kind, opened_at_tick: now }, &self.authority);
    }
}
/// Card 26 §C golden-corpus hook (canary 23,).
#[cfg(test)]
mod kdiff {
    use super::*;

    #[test]
    fn kdiff_canary_23() {
        let mut a = ServerHouseholdAuthority::default();
        a.claimed_sims.insert(SimId(4));
        a.active_households.insert(HouseholdId(4));
        a.members.insert(HouseholdId(4), BTreeSet::from([SimId(4)]));
        let mut s = CareerAndTransitScheduler::default();
        s.active_shifts.insert(SimId(4), vec![CareerShift::new(SimId(4), 1, LotId(9), 10_000, 20_000)]);
        // The window opens exactly at start - SHIFT_DISPATCH_WINDOW_TICKS.
        let early = s.tick_bus_dispatch(&a, &|h| vec![SimId(h.0)], 9_399).len() as u64;
        let on_time = s.tick_bus_dispatch(&a, &|h| vec![SimId(h.0)], 9_400).len() as u64;
        println!("KDIFF|canary=23|case=bus_window_edge|got={:016x}", (early == 0 && on_time == 1) as u64);
        assert_eq!(early, 0);
        assert_eq!(on_time, 1);
        assert_eq!(s.dispatches[0].at_tick, 9_400);
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn all_household_bus_windows_and_sorted_carpools_are_once_only() {
        let mut a = ServerHouseholdAuthority::default();
        let mut s = CareerAndTransitScheduler::default();
        for id in [2, 5, 7] {
            a.claimed_sims.insert(SimId(id)); a.active_households.insert(HouseholdId(id));
            a.members.insert(HouseholdId(id), BTreeSet::from([SimId(id)]));
            s.active_shifts.insert(SimId(id), vec![CareerShift::new(SimId(id), 1, LotId(10), 10_000, 20_000)]);
        }
        assert!(s.tick_bus_dispatch(&a, &|h| vec![SimId(h.0)], 9399).is_empty());
        assert_eq!(s.tick_bus_dispatch(&a, &|h| vec![SimId(h.0)], 9400).len(), 3);
        assert!(s.tick_bus_dispatch(&a, &|h| vec![SimId(h.0)], 9400).is_empty());
        assert_eq!(s.tick_carpool_dispatch(&a, &|_| vec![SimId(7), SimId(5), SimId(2)], 9400).len(), 1);
        assert_eq!(s.dispatches.last().unwrap().passengers, vec![SimId(2), SimId(5), SimId(7)]);
        assert_eq!(s.tick_carpool_dispatch(&a, &|_| vec![SimId(2), SimId(5), SimId(7)], 20_000).len(), 1);
        assert!(s.active_shifts.values().all(Vec::is_empty));
    }
    #[test]
    fn rolls_and_owner_dialogs_use_server_inputs_without_pausing() {
        let mut s = CareerAndTransitScheduler::default();
        s.event_ids.insert(SimId(9), vec![11, 12]);
        let mut other = CareerAndTransitScheduler {
            event_ids: s.event_ids.clone(),
            ..CareerAndTransitScheduler::default()
        };
        let mut r = crate::KernelRng::from_state(0x8000_0000);
        let mut r2 = crate::KernelRng::from_state(0x8000_0000);
        for now in 0..100 { assert_eq!(s.roll_for_event(SimId(9), &mut r, now), other.roll_for_event(SimId(9), &mut r2, now)); }
        s.authority.claimed_sims.insert(SimId(9));
        s.authority.owners.insert(SimId(9), crate::wire::PlayerId(42));
        let mut router = RemoteModalDialogRouter::default();
        s.dispatch_career_dialog(SimId(9), DialogKind::AcceptCancel, &mut router, 0);
        assert_eq!(router.outbox[0].owner_player, crate::wire::PlayerId(42));
        assert!(router.invariant_clock_not_paused());
        assert_eq!(router.tick(1800).len(), 1);
    }
}
