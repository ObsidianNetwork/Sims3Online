#![forbid(unsafe_code)]
//! Server-owned wish/opportunity lifecycle. Tuning is supplied, never inferred
//! from a local UI actor. Emitted state is mirrored; clients do not run this driver.
use std::collections::{BTreeMap, BTreeSet};
use crate::{SimId, LotId, authority::ServerHouseholdAuthority, kernel_iface::PathRng};

pub const MAX_LOCKED_WISHES: u8 = 4;
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct WishHandle(pub u64);
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct OpportunityHandle(pub u64);
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum WishKind { Daily, Lifetime }
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct AspirationWish {
    pub handle: WishHandle, pub sim: SimId, pub kind: WishKind,
    pub points: i16, pub locked: bool, pub due_tick: u64,
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Opportunity {
    pub handle: OpportunityHandle, pub sim: SimId, pub definition_id: u32,
    pub lot_id: LotId, pub started_at: u64, pub expires_at: u64, pub stage: u8,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct LifetimeHappiness(pub i32);
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LockErr { AlreadyLocked, OverCap { cap: u8 }, UnknownWish }
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FulfilErr { NotLocked, UnknownWish }

#[derive(Clone, Debug, Default)]
pub struct ClaimedSimAspirationDriver {
    pub wishes: BTreeMap<SimId, Vec<AspirationWish>>,
    pub opportunities: BTreeMap<SimId, Vec<Opportunity>>,
    pub happiness: BTreeMap<SimId, LifetimeHappiness>,
    pub locked_count: BTreeMap<SimId, u8>,
    pub next_handle: u64,
    /// (reward, lifetime in ticks) from an external tuning source.
    pub daily_templates: Vec<(i16, u64)>,
    completed: BTreeSet<WishHandle>,
}
impl ClaimedSimAspirationDriver {
    pub fn initialise_for(&mut self, sim: SimId, a: &ServerHouseholdAuthority) {
        if a.is_selectable(sim) {
            self.wishes.entry(sim).or_default();
            self.opportunities.entry(sim).or_default();
            self.happiness.entry(sim).or_default();
            self.locked_count.entry(sim).or_default();
        }
    }
    pub fn claim_remove(&mut self, sim: SimId) {
        self.wishes.remove(&sim);
        self.opportunities.remove(&sim);
        self.locked_count.remove(&sim);
        // Lifetime rewards survive an unclaim/reclaim.
    }
    pub fn tick(&mut self, now: u64, rng: &mut impl PathRng, a: &ServerHouseholdAuthority) {
        let removed: Vec<_> = self.wishes.keys().filter(|s| !a.is_selectable(**s)).copied().collect();
        for sim in removed { self.claim_remove(sim); }
        for &sim in &a.claimed_sims {
            self.initialise_for(sim, a);
            let Some(pool) = self.wishes.get_mut(&sim) else { continue };
            pool.retain(|w| w.kind == WishKind::Lifetime || w.due_tick > now);
            let count = pool.iter().filter(|w| w.locked && w.kind == WishKind::Daily).count();
            self.locked_count.insert(sim, u8::try_from(count).unwrap_or(u8::MAX));
            if pool.is_empty() && !self.daily_templates.is_empty() {
                let Ok(n) = u32::try_from(self.daily_templates.len()) else { continue };
                let index = usize::try_from(rng.next_bounded(n)).unwrap_or(0);
                let (points, lifetime) = self.daily_templates[index];
                if let Some(next) = self.next_handle.checked_add(1) {
                    self.next_handle = next;
                    pool.push(AspirationWish { handle: WishHandle(next), sim,
                        kind: WishKind::Daily, points, locked: false,
                        due_tick: now.saturating_add(lifetime.max(1)) });
                }
            }
            if let Some(opportunities) = self.opportunities.get_mut(&sim) {
                opportunities.retain(|o| {
                    if o.expires_at <= now { rng.next_bounded(1); false } else { true }
                });
            }
        }
    }
    pub fn lock_wish(&mut self, sim: SimId, h: WishHandle) -> Result<(), LockErr> {
        let pool = self.wishes.get_mut(&sim).ok_or(LockErr::UnknownWish)?;
        let index = pool.iter().position(|w| w.handle == h).ok_or(LockErr::UnknownWish)?;
        if pool[index].locked || self.completed.contains(&h) { return Err(LockErr::AlreadyLocked); }
        let count = pool.iter().filter(|w| w.locked && w.kind == WishKind::Daily).count();
        if pool[index].kind == WishKind::Daily && count >= usize::from(MAX_LOCKED_WISHES) {
            return Err(LockErr::OverCap { cap: MAX_LOCKED_WISHES });
        }
        pool[index].locked = true;
        self.locked_count.insert(sim, u8::try_from(count + usize::from(pool[index].kind == WishKind::Daily)).unwrap_or(u8::MAX));
        Ok(())
    }
    pub fn fulfil_wish(&mut self, sim: SimId, h: WishHandle, _now: u64) -> Result<LifetimeHappiness, FulfilErr> {
        let pool = self.wishes.get_mut(&sim).ok_or(FulfilErr::UnknownWish)?;
        let index = pool.iter().position(|w| w.handle == h).ok_or(FulfilErr::UnknownWish)?;
        if !pool[index].locked { return Err(FulfilErr::NotLocked); }
        let wish = pool[index];
        let happiness = self.happiness.entry(sim).or_default();
        happiness.0 = happiness.0.saturating_add(i32::from(wish.points));
        if wish.kind == WishKind::Lifetime {
            pool[index].locked = false;
            self.completed.insert(h);
        } else {
            pool.remove(index);
            let count = self.locked_count.entry(sim).or_default();
            *count = count.saturating_sub(1);
        }
        Ok(*happiness)
    }
}
/// Card 26 §C golden-corpus hook (canary 21,).
#[cfg(test)]
mod kdiff {
    use super::*;

    #[test]
    fn kdiff_canary_21() {
        let mut a = ServerHouseholdAuthority::default();
        a.claimed_sims.insert(SimId(1));
        let mut d = ClaimedSimAspirationDriver::default();
        d.initialise_for(SimId(1), &a);
        for id in 1..=5 {
            d.wishes.get_mut(&SimId(1)).unwrap().push(AspirationWish {
                handle: WishHandle(id), sim: SimId(1), kind: WishKind::Daily,
                points: 500, locked: false, due_tick: u64::MAX });
        }
        for id in 1..=4 { d.lock_wish(SimId(1), WishHandle(id)).unwrap(); }
        let cap = match d.lock_wish(SimId(1), WishHandle(5)) {
            Err(LockErr::OverCap { cap }) => u64::from(cap),
            _ => u64::MAX,
        };
        println!("KDIFF|canary=21|case=locked_wish_cap|got={:016x}", cap);
        assert_eq!(cap, u64::from(MAX_LOCKED_WISHES));
        let happy = d.fulfil_wish(SimId(1), WishHandle(1), 0).unwrap();
        println!("KDIFF|canary=21|case=fulfil_reward|got={:016x}", happy.0 as u64);
        assert_eq!(happy, LifetimeHappiness(500));
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn expired_daily_lock_is_released_at_deadline_without_expiring_lifetime() {
        let mut a = ServerHouseholdAuthority::default();
        a.claimed_sims.insert(SimId(1));
        let mut d = ClaimedSimAspirationDriver::default();
        d.initialise_for(SimId(1), &a);
        for (id, kind) in [(1, WishKind::Daily), (2, WishKind::Lifetime)] {
            d.wishes.get_mut(&SimId(1)).unwrap().push(AspirationWish {
                handle: WishHandle(id), sim: SimId(1), kind, points: 100,
                locked: false, due_tick: 5 });
            d.lock_wish(SimId(1), WishHandle(id)).unwrap();
        }
        let mut rng = crate::KernelRng::from_state(1);
        d.tick(4, &mut rng, &a);
        assert_eq!(d.locked_count[&SimId(1)], 1);
        d.tick(5, &mut rng, &a);
        assert_eq!(d.locked_count[&SimId(1)], 0);
        assert_eq!(d.wishes[&SimId(1)].len(), 1);
        assert_eq!(d.wishes[&SimId(1)][0].kind, WishKind::Lifetime);
    }
    #[test]
    fn claims_caps_rewards_and_lifetime_survive_ui_selection() {
        let mut a = ServerHouseholdAuthority::default();
        a.claimed_sims.extend([SimId(1), SimId(2), SimId(3)]);
        let mut d = ClaimedSimAspirationDriver::default();
        for sim in [SimId(1), SimId(2), SimId(3)] { d.initialise_for(sim, &a); }
        for id in 1..=5 {
            d.wishes.get_mut(&SimId(1)).unwrap().push(AspirationWish { handle: WishHandle(id),
                sim: SimId(1), kind: WishKind::Daily, points: 500, locked: false, due_tick: 10 });
        }
        for id in 1..=4 { d.lock_wish(SimId(1), WishHandle(id)).unwrap(); }
        assert_eq!(d.lock_wish(SimId(1), WishHandle(5)), Err(LockErr::OverCap { cap: 4 }));
        assert_eq!(d.fulfil_wish(SimId(1), WishHandle(1), 0), Ok(LifetimeHappiness(500)));
        assert_eq!(d.happiness[&SimId(2)].0, 0);
        a.local_active_actor = Some(SimId(2));
        d.tick(0, &mut crate::KernelRng::from_state(1), &a);
        assert_eq!(d.wishes.len(), 3);
        a.claimed_sims.remove(&SimId(1));
        d.tick(1, &mut crate::KernelRng::from_state(1), &a);
        assert!(!d.wishes.contains_key(&SimId(1)));
        assert!(d.wishes.contains_key(&SimId(2)));
    }
    #[test]
    fn seeded_refresh_and_expiry_are_once_only() {
        let mut a = ServerHouseholdAuthority::default();
        a.claimed_sims.insert(SimId(7));
        let mut d = ClaimedSimAspirationDriver { daily_templates: vec![(100, 10), (200, 20)], ..Default::default() };
        d.initialise_for(SimId(7), &a);
        d.opportunities.get_mut(&SimId(7)).unwrap().push(Opportunity {
            handle: OpportunityHandle(99), sim: SimId(7), definition_id: 1,
            lot_id: LotId(1), started_at: 0, expires_at: 5, stage: 0 });
        let mut other = d.clone();
        let mut r = crate::KernelRng::from_state(0x8000_0000);
        let mut r2 = crate::KernelRng::from_state(0x8000_0000);
        for now in 0..100 { d.tick(now, &mut r, &a); other.tick(now, &mut r2, &a); }
        assert_eq!(d.wishes, other.wishes);
        assert_eq!(r.state(), r2.state());
        assert!(d.opportunities[&SimId(7)].is_empty());
    }
}
