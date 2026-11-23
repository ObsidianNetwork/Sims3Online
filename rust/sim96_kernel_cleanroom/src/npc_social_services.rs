#![forbid(unsafe_code)]
//! Deterministic H4 routing. Spawn identities are bound only at the existing
//! server service boundary; assignments do not fabricate native Sims or cars.
use std::collections::BTreeSet;
use crate::{LotId, SimId, authority::{HouseholdId, ServerHouseholdAuthority},
    kernel_iface::PathRng, services::{ObjectSpawnedFrame, RoleAuthority, WorldServiceSpawnerGate},
    vehicles::VehicleGuid};
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct PartyHandle(pub u64);
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct ServiceAssignmentHandle(pub u64);
#[derive(Clone, Debug)]
pub struct NpcParty {
    pub handle: PartyHandle, pub host_household: HouseholdId, pub host_lot: LotId,
    pub guests: Vec<SimId>, pub started_at: u64, pub expires_at: u64,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum ServiceKind2 { Paper, Mail, Maid, Repairman, Pizza, Burglar, Firefighter,
    GrimReaper, Police, Nanny, Butler, Groundskeeper, Stylist, Doctor, Taxi, Bus }
#[derive(Clone, Debug)]
pub struct ServiceAssignment {
    pub handle: ServiceAssignmentHandle, pub kind: ServiceKind2, pub lot_id: LotId,
    pub household: HouseholdId, pub spawned_sim: Option<SimId>,
    pub vehicle_guid: Option<VehicleGuid>, pub assigned_at: u64,
}
#[derive(Clone, Debug)]
pub struct MultiHouseholdSocialAndServiceRouter {
    pub parties: Vec<NpcParty>, pub assignments: Vec<ServiceAssignment>,
    pub rr_index: usize, pub next_handle: u64, pub role: RoleAuthority,
}
impl Default for MultiHouseholdSocialAndServiceRouter {
    fn default() -> Self { Self { parties: vec![], assignments: vec![], rr_index: 0,
        next_handle: 0, role: RoleAuthority::Server } }
}
impl MultiHouseholdSocialAndServiceRouter {
    pub fn party_candidates(&self, a: &ServerHouseholdAuthority,
        lots: &dyn Fn(HouseholdId) -> Vec<LotId>, net_worth: &dyn Fn(LotId) -> i64) -> Vec<HouseholdId> {
        if self.role != RoleAuthority::Server { return vec![]; }
        let mut candidates: Vec<_> = a.active_households().filter_map(|h| {
            let home = a.home_lots.get(&h).copied().or_else(|| lots(h).into_iter().min())?;
            Some((net_worth(home), h))
        }).collect();
        candidates.sort();
        candidates.into_iter().map(|(_, h)| h).collect()
    }
    pub fn pick_guests(&mut self, a: &ServerHouseholdAuthority,
        members: &dyn Fn(HouseholdId) -> Vec<SimId>, rng: &mut impl PathRng) -> Vec<SimId> {
        if self.role != RoleAuthority::Server { return vec![]; }
        let mut guests = BTreeSet::new();
        for hh in a.active_households() {
            let candidates: Vec<_> = members(hh).into_iter().filter(|s| a.is_selectable(*s)
                && a.members.get(&hh).is_some_and(|m| m.contains(s)))
                .collect::<BTreeSet<_>>().into_iter().collect();
            if candidates.is_empty() { continue; }
            let Ok(n) = u32::try_from(candidates.len()) else { continue };
            let i = if n == 1 { 0 } else { usize::try_from(rng.next_bounded(n)).unwrap_or(0) };
            guests.insert(candidates[i]);
        }
        guests.into_iter().collect()
    }
    pub fn needs_assignment(&self, _kind: ServiceKind2, lot: LotId, a: &ServerHouseholdAuthority) -> bool {
        self.role == RoleAuthority::Server && a.active_households()
            .any(|hh| a.home_lots.get(&hh) == Some(&lot))
    }
    pub fn assign_service(&mut self, kind: ServiceKind2, a: &ServerHouseholdAuthority, now: u64)
        -> Option<ServiceAssignmentHandle> {
        if self.role != RoleAuthority::Server { return None; }
        let households: Vec<_> = a.active_households().filter(|h| a.home_lots.contains_key(h)).collect();
        if households.is_empty() { return None; }
        for offset in 0..households.len() {
            let index = (self.rr_index % households.len() + offset) % households.len();
            let hh = households[index];
            let lot = a.home_lots[&hh];
            if self.assignments.iter().any(|s| s.kind == kind && s.lot_id == lot && s.assigned_at == now) { continue; }
            let handle = self.next_handle.checked_add(1)?;
            self.next_handle = handle;
            self.rr_index = (index + 1) % households.len();
            let handle = ServiceAssignmentHandle(handle);
            self.assignments.push(ServiceAssignment { handle, kind, lot_id: lot, household: hh,
                spawned_sim: None, vehicle_guid: None, assigned_at: now });
            return Some(handle);
        }
        None
    }
    pub fn bind_spawn(&mut self, handle: ServiceAssignmentHandle, sim: SimId, car: Option<VehicleGuid>,
        frame: ObjectSpawnedFrame, gate: &mut WorldServiceSpawnerGate) -> bool {
        if self.role != RoleAuthority::Server || gate.role != RoleAuthority::Server { return false; }
        let Some(s) = self.assignments.iter_mut().find(|s| s.handle == handle
            && s.lot_id == frame.lot_id && s.spawned_sim.is_none()) else { return false };
        gate.on_server_spawn(frame);
        s.spawned_sim = Some(sim); s.vehicle_guid = car;
        true
    }
    pub fn open_party(&mut self, hh: HouseholdId, guests: Vec<SimId>, a: &ServerHouseholdAuthority,
        now: u64, expires_at: u64) -> Option<PartyHandle> {
        if self.role != RoleAuthority::Server || !a.household_is_active(hh) || expires_at <= now { return None; }
        let lot = *a.home_lots.get(&hh)?;
        let handle = self.next_handle.checked_add(1)?;
        self.next_handle = handle;
        let guests = guests.into_iter().filter(|s| a.is_selectable(*s)
            && a.active_households().any(|h| a.members.get(&h).is_some_and(|m| m.contains(s))))
            .collect::<BTreeSet<_>>().into_iter().collect();
        let handle = PartyHandle(handle);
        self.parties.push(NpcParty { handle, host_household: hh, host_lot: lot, guests, started_at: now, expires_at });
        Some(handle)
    }
    pub fn may_send_home(sim: SimId, a: &ServerHouseholdAuthority) -> bool {
        !a.claimed_sims.contains(&sim)
    }
}
/// Card 26 §C golden-corpus hook (canary 24,).
#[cfg(test)]
mod kdiff {
    use super::*;

    #[test]
    fn kdiff_canary_24() {
        let mut a = ServerHouseholdAuthority::default();
        for id in 1..=3 {
            a.active_households.insert(HouseholdId(id));
            a.claimed_sims.insert(SimId(id));
            a.home_lots.insert(HouseholdId(id), LotId(id * 10));
        }
        let mut r = MultiHouseholdSocialAndServiceRouter::default();
        for now in 0..6u64 { r.assign_service(ServiceKind2::Mail, &a, now).unwrap(); }
        // Round-robin fairness: three households, two turns each, no starvation.
        let distinct = r.assignments.iter().map(|s| s.household).collect::<BTreeSet<_>>().len() as u64;
        println!("KDIFF|canary=24|case=rr_households_covered|got={:016x}", distinct);
        assert_eq!(distinct, 3);
        let pattern: Vec<u32> = r.assignments.iter().map(|s| s.household.0).collect();
        assert_eq!(pattern, vec![1, 2, 3, 1, 2, 3]);
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    fn authority() -> ServerHouseholdAuthority {
        let mut a = ServerHouseholdAuthority::default();
        for id in 1..=3 {
            a.active_households.insert(HouseholdId(id)); a.claimed_sims.insert(SimId(id));
            a.members.insert(HouseholdId(id), BTreeSet::from([SimId(id)]));
            a.home_lots.insert(HouseholdId(id), LotId(id * 10));
        }
        a
    }
    #[test]
    fn services_round_robin_without_starvation_or_same_tick_duplicates() {
        let a = authority(); let mut r = MultiHouseholdSocialAndServiceRouter::default();
        for now in 0..6 { r.assign_service(ServiceKind2::Mail, &a, now).unwrap(); }
        assert_eq!(r.assignments.iter().map(|s| s.household.0).collect::<Vec<_>>(), vec![1,2,3,1,2,3]);
        for _ in 0..3 { r.assign_service(ServiceKind2::Paper, &a, 10).unwrap(); }
        assert_eq!(r.assign_service(ServiceKind2::Paper, &a, 10), None);
        assert_eq!(r.assignments.len(), 9);
    }
    #[test]
    fn host_guests_and_claimed_ghost_gate_are_authoritative() {
        let mut a = authority();
        let mut r = MultiHouseholdSocialAndServiceRouter::default();
        assert_eq!(r.party_candidates(&a, &|_| vec![], &|lot| -i64::from(lot.0)),
            vec![HouseholdId(3),HouseholdId(2),HouseholdId(1)]);
        let guests = r.pick_guests(&a, &|hh| vec![SimId(hh.0), SimId(999)], &mut crate::KernelRng::from_state(1));
        assert_eq!(guests, vec![SimId(1),SimId(2),SimId(3)]);
        a.never_selectable.insert(SimId(1));
        assert!(!MultiHouseholdSocialAndServiceRouter::may_send_home(SimId(1), &a));
        r.role = RoleAuthority::PresenterClient;
        assert_eq!(r.assign_service(ServiceKind2::Maid, &a, 0), None);
        assert!(r.open_party(HouseholdId(1), guests, &a, 0, 10).is_none());
    }
}
