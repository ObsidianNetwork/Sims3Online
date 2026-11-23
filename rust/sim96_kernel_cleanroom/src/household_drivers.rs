#![forbid(unsafe_code)]
//! H4 drivers. Bill rate and life-event eligibility are explicit tuning inputs.
use std::collections::{BTreeMap, BTreeSet, VecDeque};
use crate::{LotId, SimId, authority::{HouseholdId, ServerHouseholdAuthority},
    economy::{EconomyErr, EconomyReason, FamilyFunds}};
pub const WEEKLY_BILLS_PERIOD_TICKS: u64 = 378_000;
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct BillHandle(pub u64);
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct LifeEventHandle(pub u64);
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BillKind { Household, Pet, Utility, Steamer, ServantWages, Tutor }
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum DelinquencyStage { Current, FirstNotice, SecondNotice, Repossession }
#[derive(Clone, Copy, Debug)]
pub struct Bill {
    pub handle: BillHandle, pub household: HouseholdId, pub kind: BillKind,
    pub amount: i32, pub due_tick: u64, pub stage: DelinquencyStage, pub settled: bool,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum MailKind { LoveLetter, BookClub, BlogDonation, BlogBuyout, Official, Newspaper }
#[derive(Clone, Debug)]
pub struct MailItem {
    pub kind: MailKind, pub household: HouseholdId, pub recipient: SimId,
    pub created_at: u64, pub delivered_at: Option<u64>,
}
#[derive(Clone, Debug)]
pub struct LifeEventOffer {
    pub handle: LifeEventHandle, pub household: HouseholdId, pub definition_id: u32,
    pub pending_validated: BTreeSet<SimId>, pub started_at: u64, pub expires_at: u64,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BillErr { UnknownBill, InsufficientFunds { need: i32 }, AlreadySettled, InvalidAmount, Economy(EconomyErr) }
#[derive(Clone, Debug, Default)]
pub struct MultiHouseholdMailboxAndLifeEvents {
    pub bills: Vec<Bill>, pub mail_queue: VecDeque<MailItem>,
    pub offers: Vec<LifeEventOffer>, pub next_handle: u64,
    /// Required caller tuning; zero means bills have no monetary charge.
    pub bill_rate_basis_points: u32,
    pub repossessions: BTreeSet<BillHandle>,
    last_billed: BTreeMap<HouseholdId, u64>,
}
impl MultiHouseholdMailboxAndLifeEvents {
    pub fn run_weekly_bills(&mut self, a: &ServerHouseholdAuthority,
        lots: &dyn Fn(HouseholdId) -> Vec<LotId>,
        net_worth: &dyn Fn(LotId) -> i32, now: u64) -> Vec<BillHandle> {
        let mut issued = Vec::new();
        for hh in a.active_households() {
            if self.last_billed.get(&hh).is_some_and(|t| now.saturating_sub(*t) < WEEKLY_BILLS_PERIOD_TICKS) { continue; }
            let unique: BTreeSet<_> = lots(hh).into_iter().collect();
            let worth = unique.into_iter().fold(0i64, |n, lot| n.saturating_add(i64::from(net_worth(lot).max(0))));
            let charge = worth.saturating_mul(i64::from(self.bill_rate_basis_points)) / 10_000;
            let amount = i32::try_from(charge.min(i64::from(crate::economy::FUNDS_MAX))).unwrap_or(crate::economy::FUNDS_MAX);
            let Some(handle) = self.next_handle.checked_add(1) else { break };
            self.next_handle = handle;
            let handle = BillHandle(handle);
            self.bills.push(Bill { handle, household: hh, kind: BillKind::Household, amount,
                due_tick: now.saturating_add(WEEKLY_BILLS_PERIOD_TICKS),
                stage: DelinquencyStage::Current, settled: false });
            self.last_billed.insert(hh, now);
            issued.push(handle);
        }
        issued
    }
    pub fn pay_bill(&mut self, h: BillHandle, funds: &mut FamilyFunds, now: u64) -> Result<(), BillErr> {
        let bill = self.bills.iter_mut().find(|b| b.handle == h).ok_or(BillErr::UnknownBill)?;
        if bill.settled { return Err(BillErr::AlreadySettled); }
        if bill.amount < 0 { return Err(BillErr::InvalidAmount); }
        match funds.modify(bill.household, -bill.amount, EconomyReason::BillPaid, vec![], now) {
            Ok(_) => {}
            Err(EconomyErr::InsufficientFunds { .. }) => return Err(BillErr::InsufficientFunds { need: bill.amount }),
            Err(e) => return Err(BillErr::Economy(e)),
        }
        bill.settled = true;
        bill.stage = DelinquencyStage::Current;
        self.repossessions.remove(&h);
        Ok(())
    }
    /// Called by the same server tick with its sole economy ledger. Log each
    /// missed weekly stage before publishing the transition; retries are safe.
    pub fn advance_delinquency(&mut self, funds: &mut FamilyFunds, now: u64) -> Result<(), EconomyErr> {
        for b in &mut self.bills {
            if b.settled { continue; }
            let weeks = now.saturating_sub(b.due_tick) / WEEKLY_BILLS_PERIOD_TICKS;
            let desired = match weeks { 0 => DelinquencyStage::Current, 1 => DelinquencyStage::FirstNotice,
                2 => DelinquencyStage::SecondNotice, _ => DelinquencyStage::Repossession };
            while b.stage < desired {
                funds.modify(b.household, 0, EconomyReason::BillUnpaid, vec![], now)?;
                b.stage = match b.stage {
                    DelinquencyStage::Current => DelinquencyStage::FirstNotice,
                    DelinquencyStage::FirstNotice => DelinquencyStage::SecondNotice,
                    _ => DelinquencyStage::Repossession,
                };
                if b.stage == DelinquencyStage::Repossession { self.repossessions.insert(b.handle); }
            }
        }
        Ok(())
    }
    pub fn queue_household_mail(&mut self, a: &ServerHouseholdAuthority, now: u64) {
        for hh in a.active_households() {
            let Some(&recipient) = a.members.get(&hh).and_then(|m| m.first()) else { continue };
            for kind in [MailKind::Official, MailKind::Newspaper] {
                if self.mail_queue.iter().any(|m| m.household == hh && m.kind == kind && m.created_at == now) { continue; }
                self.mail_queue.push_back(MailItem { kind, household: hh, recipient,
                    created_at: now, delivered_at: None });
            }
        }
    }
    /// Delivery acknowledgement from the server's mail service, not a local UI.
    pub fn deliver_mail(&mut self, hh: HouseholdId, now: u64) -> usize {
        let mut count = 0;
        for m in &mut self.mail_queue {
            if m.household == hh && m.delivered_at.is_none() && m.created_at <= now {
                m.delivered_at = Some(now); count += 1;
            }
        }
        count
    }
    /// Offers are populated from the tuning/event producer. This boundary
    /// validates membership and expires each offer once across every active HH.
    pub fn tick_life_events(&mut self, a: &ServerHouseholdAuthority, now: u64) -> Vec<LifeEventHandle> {
        let mut changed = Vec::new();
        self.offers.retain_mut(|o| {
            if !a.household_is_active(o.household) || o.expires_at <= now {
                changed.push(o.handle); return false;
            }
            let before = o.pending_validated.len();
            o.pending_validated.retain(|sim| a.members.get(&o.household).is_some_and(|m| m.contains(sim)));
            if before != o.pending_validated.len() { changed.push(o.handle); }
            true
        });
        changed
    }
}
/// Card 26 §C golden-corpus hook (canary 22,).
#[cfg(test)]
mod kdiff {
    use super::*;

    #[test]
    fn kdiff_canary_22() {
        let mut a = ServerHouseholdAuthority::default();
        for id in 1..=3 { a.active_households.insert(HouseholdId(id)); }
        let mut d = MultiHouseholdMailboxAndLifeEvents { bill_rate_basis_points: 100, ..Default::default() };
        let issued = d.run_weekly_bills(&a, &|h| vec![LotId(h.0)], &|_| 50_000, 0);
        println!("KDIFF|canary=22|case=weekly_bills_issued|got={:016x}", issued.len() as u64);
        assert_eq!(issued.len(), 3);
        assert!(d.bills.iter().all(|b| b.amount == 500 && b.due_tick == WEEKLY_BILLS_PERIOD_TICKS));
        // No re-bill inside the weekly period (no drift accumulation).
        assert!(d.run_weekly_bills(&a, &|h| vec![LotId(h.0)], &|_| 50_000, WEEKLY_BILLS_PERIOD_TICKS - 1).is_empty());
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn weekly_three_households_payment_and_delinquency_are_isolated() {
        let mut a = ServerHouseholdAuthority::default();
        a.active_households.extend([HouseholdId(1), HouseholdId(2), HouseholdId(3)]);
        let mut d = MultiHouseholdMailboxAndLifeEvents { bill_rate_basis_points: 100, ..Default::default() };
        let lots = |h: HouseholdId| vec![LotId(h.0), LotId(h.0)];
        assert_eq!(d.run_weekly_bills(&a, &lots, &|_| 50_000, 0).len(), 3);
        assert!(d.run_weekly_bills(&a, &lots, &|_| 50_000, WEEKLY_BILLS_PERIOD_TICKS - 1).is_empty());
        let mut funds = FamilyFunds::default();
        for hh in a.active_households() { funds.ledger.insert(hh, 1000); }
        let handle = d.bills[0].handle;
        d.pay_bill(handle, &mut funds, 1).unwrap();
        assert_eq!(funds.ledger[&HouseholdId(1)], 500);
        assert_eq!(d.pay_bill(handle, &mut funds, 2), Err(BillErr::AlreadySettled));
        d.advance_delinquency(&mut funds, 4 * WEEKLY_BILLS_PERIOD_TICKS).unwrap();
        assert_eq!(d.bills[1].stage, DelinquencyStage::Repossession);
        assert_eq!(funds.log.len(), 7);
        d.advance_delinquency(&mut funds, 4 * WEEKLY_BILLS_PERIOD_TICKS).unwrap();
        assert_eq!(funds.log.len(), 7);
    }
    #[test]
    fn rejection_mail_and_life_event_expiry_do_not_leak() {
        let mut a = ServerHouseholdAuthority::default();
        for id in 1..=3 { a.active_households.insert(HouseholdId(id));
            a.members.insert(HouseholdId(id), BTreeSet::from([SimId(id)])); }
        let mut d = MultiHouseholdMailboxAndLifeEvents::default();
        d.queue_household_mail(&a, 0); d.queue_household_mail(&a, 0);
        assert_eq!(d.mail_queue.len(), 6);
        assert_eq!(d.deliver_mail(HouseholdId(2), 1), 2);
        d.offers.push(LifeEventOffer { handle: LifeEventHandle(1), household: HouseholdId(3),
            definition_id: 1, pending_validated: BTreeSet::from([SimId(1), SimId(3)]),
            started_at: 0, expires_at: 10 });
        d.tick_life_events(&a, 0);
        assert_eq!(d.offers[0].pending_validated, BTreeSet::from([SimId(3)]));
        assert_eq!(d.tick_life_events(&a, 10), vec![LifeEventHandle(1)]);
        assert!(d.tick_life_events(&a, 11).is_empty());
    }
}
