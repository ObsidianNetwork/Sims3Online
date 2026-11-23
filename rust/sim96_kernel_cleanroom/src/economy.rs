#![forbid(unsafe_code)]
//! Card 17 · Build/Buy + household-funds shared economy.
//!
//! Two axes the engine conflates stay separate here: the per-household money
//! ledger (server-authoritative, clamped, append-only) and the per-lot edit
//! lock (epoch-versioned, serialises only editors on the same lot). Neither
//! pauses the 30 Hz tick loop for anyone — the dialog lesson (card 14
//! §B.3) reused: this module never touches `SimClock`.
//!
//! Contract note: `EconomyTx` carries `Vec<ObjectGuid>`, so it derives
//! `Clone` (not `Copy`) while keeping every contract field verbatim.

use std::collections::{BTreeMap, BTreeSet};

use crate::authority::HouseholdId;
use crate::services::{ResourceKey, Vec3Fixed};
use crate::wire::PlayerId;
use crate::{KernelError, LotId, ObjectGuid};

/// The shipped simoleon floor / ceiling (card 17 §B): every mutation is
/// clamped to this range with checked arithmetic, never a float.
pub const FUNDS_MIN: i32 = 0;
pub const FUNDS_MAX: i32 = 99_999_999;

/// Footprint grid cell size in cm for the placement registry (a clean-room
/// witness of the engine's routing footprint buckets, card 17 §C.3).
pub const FOOTPRINT_CELL_CM: u32 = 100;

/// Server-minted, monotonic transaction id. Clients never mint these
/// (card 17 §F); they submit a local corr id and read the `TxId` back.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct TxId(pub u64);

/// Why a funds mutation happened. Unknown cheat names funnel into
/// `CheatCode`; the ledger never stores a raw string (Kani
/// `funnel_to_reason_enum_no_stringy`).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum EconomyReason {
    ObjectPurchase,
    ObjectSale,
    ObjectDestroy,
    BillPaid,
    BillUnpaid,
    CareerPayment,
    RewardPrize,
    CheatCode,
    Undo,
}

/// One append-only ledger row. Replay of the whole log on an empty ledger
/// reproduces the exact final state on every host (invariant §B.1.2).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct EconomyTx {
    pub tx_id: TxId,
    pub household_id: HouseholdId,
    pub delta: i32,
    pub reason: EconomyReason,
    pub tick_id: u64,
    pub affected_guids: Vec<ObjectGuid>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum EconomyErr {
    InsufficientFunds { need: i32, have: i32 },
    ExceedsFundsMax { delta: i32 },
    UnknownHousehold,
    TxAlreadyApplied(TxId),
}

/// The atomic per-household funds ledger. The kernel's transactional
/// replacement for `Household.ActiveHousehold.ModifyFamilyFunds`.
#[derive(Clone, Debug, Default)]
pub struct FamilyFunds {
    pub ledger: BTreeMap<HouseholdId, i32>,
    pub log: Vec<EconomyTx>,
    pub next_tx: u64,
}

impl FamilyFunds {
    /// `ModifyFamilyFunds(delta, reason, affected_guids)` — checked only;
    /// a rejection mutates nothing and logs nothing (Kani `funds_min_clamp`,
    /// `funds_max_clamp`).
    pub fn modify(
        &mut self,
        hh: HouseholdId,
        delta: i32,
        reason: EconomyReason,
        affected_guids: Vec<ObjectGuid>,
        now_tick: u64,
    ) -> Result<TxId, EconomyErr> {
        let cur = *self.ledger.get(&hh).ok_or(EconomyErr::UnknownHousehold)?;
        let next = cur.checked_add(delta).ok_or(EconomyErr::ExceedsFundsMax { delta })?;
        if next < FUNDS_MIN {
            return Err(EconomyErr::InsufficientFunds { need: -delta, have: cur });
        }
        if next > FUNDS_MAX {
            return Err(EconomyErr::ExceedsFundsMax { delta });
        }
        let tx = EconomyTx {
            tx_id: TxId(self.next_tx),
            household_id: hh,
            delta,
            reason,
            tick_id: now_tick,
            affected_guids,
        };
        let id = tx.tx_id;
        self.next_tx = self.next_tx.wrapping_add(1);
        self.log.push(tx);
        *self.ledger.get_mut(&hh).unwrap() = next;
        Ok(id)
    }

    /// Idempotent replay of a wire-carried row: the same `TxId` applied twice
    /// advances the ledger exactly once (Kani `motherlode_repeat_idempotent`;
    /// matches the card 10 corr-dedup rule).
    pub fn apply_replay(&mut self, tx: EconomyTx) -> Result<TxId, EconomyErr> {
        if self.log.iter().any(|e| e.tx_id == tx.tx_id) {
            return Err(EconomyErr::TxAlreadyApplied(tx.tx_id));
        }
        let cur = *self.ledger.get(&tx.household_id).ok_or(EconomyErr::UnknownHousehold)?;
        let next = cur.checked_add(tx.delta).ok_or(EconomyErr::ExceedsFundsMax { delta: tx.delta })?;
        if next < FUNDS_MIN {
            return Err(EconomyErr::InsufficientFunds { need: -tx.delta, have: cur });
        }
        if next > FUNDS_MAX {
            return Err(EconomyErr::ExceedsFundsMax { delta: tx.delta });
        }
        self.ledger.insert(tx.household_id, next);
        if tx.tx_id.0 >= self.next_tx {
            self.next_tx = tx.tx_id.0 + 1;
        }
        let id = tx.tx_id;
        self.log.push(tx);
        Ok(id)
    }

    /// Undo never rewrites history: it appends the inverse row with
    /// `reason = Undo` carrying the affected guids (Kani
    /// `undo_appends_inverse`). A `tx` id missing from the log means it was
    /// already folded away or never applied — `TxAlreadyApplied`.
    pub fn undo(&mut self, tx: TxId, now_tick: u64) -> Result<TxId, EconomyErr> {
        let e = self.log.iter().find(|e| e.tx_id == tx).ok_or(EconomyErr::TxAlreadyApplied(tx))?;
        let (hh, delta, guids) = (e.household_id, e.delta, e.affected_guids.clone());
        self.modify(hh, delta.checked_neg().ok_or(EconomyErr::ExceedsFundsMax { delta })?, EconomyReason::Undo, guids, now_tick)
    }

    /// Replay the log onto an empty ledger; must equal `ledger` exactly when
    /// the ledger started at zero with logged opening rows (§B.1 invariant 2).
    pub fn replay_ledger(&self) -> BTreeMap<HouseholdId, i32> {
        let mut out: BTreeMap<HouseholdId, i32> = BTreeMap::new();
        for e in self.log.iter() {
            let cur = out.entry(e.household_id).or_insert(0);
            *cur = cur.saturating_add(e.delta).clamp(FUNDS_MIN, FUNDS_MAX);
        }
        out
    }
}

impl From<EconomyErr> for KernelError {
    fn from(err: EconomyErr) -> Self {
        match err {
            EconomyErr::InsufficientFunds { .. } => KernelError::InsufficientFunds,
            EconomyErr::ExceedsFundsMax { .. } => KernelError::ExceedsFundsMax,
            EconomyErr::UnknownHousehold => KernelError::UnknownHousehold,
            EconomyErr::TxAlreadyApplied(_) => KernelError::TxAlreadyApplied,
        }
    }
}

/// The per-lot edit lock. Held by one `PlayerId`; every acquire bumps the
/// epoch, so a transaction submitted under an older epoch is refused
/// (card 17 §C.2 — the kernel's replacement for the single-player routing
/// flush). Non-pausing: holding a lock never touches `SimClock` (§C.1).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct LotEditLock {
    pub lot_id: LotId,
    pub editor_player_id: PlayerId,
    pub epoch: u32,
    pub taken_at_tick: u64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LotEditOp {
    Place,
    Move,
    Rotate,
    Delete,
    Undo,
}

/// One build/buy mutation, paired with an `EconomyTx` for buys and sells.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct LotEditTx {
    pub op: LotEditOp,
    pub guid: ObjectGuid,
    pub resource_key: ResourceKey,
    pub pos: Vec3Fixed,
    pub facing_deg: u16,
    pub lot_id: LotId,
    pub lock_epoch: u32,
    pub tx_id: TxId,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LotEditErr {
    LotBusy { holder: PlayerId, epoch: u32 },
    StaleEpoch { held: u32, current: u32 },
    FootprintBlocked { conflicting_guid: ObjectGuid },
    SlotClearance,
    NotOwnerOfLot,
}

impl From<LotEditErr> for KernelError {
    fn from(err: LotEditErr) -> Self {
        match err {
            LotEditErr::LotBusy { .. } => KernelError::LotBusy,
            LotEditErr::StaleEpoch { .. } => KernelError::StaleEpoch,
            LotEditErr::FootprintBlocked { .. } => KernelError::FootprintBlocked,
            LotEditErr::SlotClearance => KernelError::SlotClearance,
            LotEditErr::NotOwnerOfLot => KernelError::NotOwnerOfLot,
        }
    }
}

/// The lot locks + the footprint / slot-clearance registry. `commit` validates
/// atomically against the registry *before* the delta frame goes out, so no
/// other client ever sees a placement that would snap (card 17 §C.3).
#[derive(Default)]
pub struct LotEditCoordinator {
    held: BTreeMap<LotId, LotEditLock>,
    /// Per-lot epoch counter, monotonic across release/re-acquire (§C.2:
    /// "bumped every acquire"; it never resets when the lock is freed).
    next_epoch: BTreeMap<LotId, u32>,
    /// Footprint registry: grid cell -> occupying guid.
    footprints: BTreeMap<(LotId, u32, u32), ObjectGuid>,
    placed: BTreeMap<ObjectGuid, (LotId, u32, u32)>,
    /// Cells covered by a live routing slot reservation (card 03 §C); a
    /// placement onto one is refused with `SlotClearance`.
    reserved_cells: BTreeSet<(LotId, u32, u32)>,
}

impl LotEditCoordinator {
    pub fn acquire(&mut self, lot: LotId, player: PlayerId, now: u64) -> Result<LotEditLock, LotEditErr> {
        if let Some(lock) = self.held.get(&lot) {
            if lock.editor_player_id != player {
                return Err(LotEditErr::LotBusy { holder: lock.editor_player_id, epoch: lock.epoch });
            }
        }
        let next = self.next_epoch.entry(lot).or_insert(0);
        *next += 1;
        let epoch = *next;
        let lock = LotEditLock { lot_id: lot, editor_player_id: player, epoch, taken_at_tick: now };
        self.held.insert(lot, lock);
        Ok(lock)
    }

    /// Release only the *current* lock generation; a stale token releases
    /// nothing (matches the epoch discipline in §C.2).
    pub fn release(&mut self, lock: LotEditLock) -> bool {
        if self.held.get(&lock.lot_id) == Some(&lock) {
            self.held.remove(&lock.lot_id);
            return true;
        }
        false
    }

    pub fn held_lock(&self, lot: LotId) -> Option<LotEditLock> {
        self.held.get(&lot).copied()
    }

    /// The card 03 hand-off: mark cells covered by a live routing slot
    /// reservation so placements there are refused before they broadcast.
    pub fn reserve_slot_clearance(&mut self, lot: LotId, cell: (u32, u32)) {
        self.reserved_cells.insert((lot, cell.0, cell.1));
    }

    fn cell_of(pos: Vec3Fixed) -> (u32, u32) {
        (pos.0 / FOOTPRINT_CELL_CM, pos.2 / FOOTPRINT_CELL_CM)
    }

    /// Validate then apply one edit. Every rejection leaves the world
    /// unmutated and opens no `EconomyTx` (card 17 §C.3).
    pub fn commit(&mut self, tx: LotEditTx) -> Result<(), LotEditErr> {
        let current = self
            .held_lock(tx.lot_id)
            .ok_or(LotEditErr::LotBusy { holder: PlayerId(0), epoch: 0 })?;
        if current.epoch != tx.lock_epoch {
            return Err(LotEditErr::StaleEpoch { held: tx.lock_epoch, current: current.epoch });
        }
        match tx.op {
            LotEditOp::Place | LotEditOp::Move => {
                let cell = Self::cell_of(tx.pos);
                let key = (tx.lot_id, cell.0, cell.1);
                if self.reserved_cells.contains(&key) {
                    return Err(LotEditErr::SlotClearance);
                }
                if let Some(other) = self.footprints.get(&key).copied() {
                    if other != tx.guid {
                        return Err(LotEditErr::FootprintBlocked { conflicting_guid: other });
                    }
                }
                if tx.op == LotEditOp::Move {
                    if let Some(old) = self.placed.remove(&tx.guid) {
                        self.footprints.remove(&(old.0, old.1, old.2));
                    }
                }
                self.footprints.insert(key, tx.guid);
                self.placed.insert(tx.guid, key);
                Ok(())
            }
            LotEditOp::Delete => {
                let Some(cell) = self.placed.remove(&tx.guid) else {
                    return Err(LotEditErr::NotOwnerOfLot);
                };
                self.footprints.remove(&cell);
                Ok(())
            }
            // Rotate changes facing only; the footprint cell is unchanged and
            // the epoch check above is the whole gate. Undo re-applies the
            // engine's own inverse chain through paired EconomyTx rows.
            LotEditOp::Rotate | LotEditOp::Undo => Ok(()),
        }
    }

    /// The live footprint registry cells as `(lot_id, gx, gz)` in
    /// `FOOTPRINT_CELL_CM` units - the input `routing_astar`'s
    /// `footprint_blocked_cells` maps onto navmesh cell ids (card 52 §C).
    pub fn footprint_cells(&self) -> impl Iterator<Item = (u32, u32, u32)> + '_ {
        self.footprints.keys().map(|(lot, gx, gz)| (lot.0, *gx, *gz))
    }

    pub fn footprint_count(&self) -> usize {
        self.footprints.len()
    }
}

/// The shared economy entrypoint: money and the object set, nothing else
/// (class (c) build/buy UI stays each player's own).
#[derive(Default)]
pub struct HouseholdEconomy {
    pub funds: FamilyFunds,
    pub edits: LotEditCoordinator,
}

impl HouseholdEconomy {
    /// A purchase is *paired* with its placement (§B.1 invariant 4): if the
    /// `LotEditTx` is refused, the funds row is rolled back by exactly one
    /// appended `Undo` entry, and `Ok` carries that inverse row's id — the
    /// net-zero purchase leaves the ledger where it started (Kani
    /// `purchase_and_place_atomic_pair`).
    pub fn buy_object(
        &mut self,
        _editor: PlayerId,
        hh: HouseholdId,
        guid: ObjectGuid,
        price: i32,
        tx_lot: LotEditTx,
        now: u64,
    ) -> Result<TxId, EconomyErr> {
        let tx = self.funds.modify(hh, -price, EconomyReason::ObjectPurchase, vec![guid], now)?;
        if self.edits.commit(tx_lot).is_err() {
            return self.funds.undo(tx, now);
        }
        Ok(tx)
    }

    /// A sale is the mirror: the credit only lands when the delete places.
    pub fn sell_object(
        &mut self,
        _editor: PlayerId,
        hh: HouseholdId,
        guid: ObjectGuid,
        price: i32,
        tx_lot: LotEditTx,
        now: u64,
    ) -> Result<TxId, EconomyErr> {
        let tx = self.funds.modify(hh, price, EconomyReason::ObjectSale, vec![guid], now)?;
        if self.edits.commit(tx_lot).is_err() {
            return self.funds.undo(tx, now);
        }
        Ok(tx)
    }

    /// Delete with no refund by default; `refund` models salvage.
    pub fn destroy_object(
        &mut self,
        _editor: PlayerId,
        hh: HouseholdId,
        guid: ObjectGuid,
        refund: i32,
        now: u64,
    ) -> Result<TxId, EconomyErr> {
        self.funds.modify(hh, refund, EconomyReason::ObjectDestroy, vec![guid], now)
    }
}

/// Card 26 §C golden-corpus hook (canary 17, ledger).
#[cfg(test)]
mod kdiff {
    use super::*;

    #[test]
    fn kdiff_canary_17() {
        let mut f = FamilyFunds::default();
        f.ledger.insert(HouseholdId(7), 500);
        let err = f.modify(HouseholdId(7), -1000, EconomyReason::ObjectPurchase, Vec::new(), 1);
        let have = match err {
            Err(EconomyErr::InsufficientFunds { need: 1000, have }) => have.max(0) as u64,
            _ => u64::MAX,
        };
        println!("KDIFF|canary=17|case=insufficient_have|got={:016x}", have);
        assert_eq!(have, 500);
        assert_eq!(f.ledger[&HouseholdId(7)], 500); // rejection never mutated
        assert!(f.log.is_empty()); // nor logged
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn hh(id: u32) -> HouseholdId {
        HouseholdId(id)
    }

    fn funds_with(h: HouseholdId, initial: i32) -> FamilyFunds {
        let mut f = FamilyFunds::default();
        f.ledger.insert(h, 0);
        f.modify(h, initial, EconomyReason::CheatCode, Vec::new(), 0).unwrap();
        f
    }

    #[test]
    fn clamps_and_checked_arithmetic_never_mutate_on_rejection() {
        let mut f = funds_with(hh(100), 500);
        let pre_log = f.log.len();
        // Below MIN: rejected, ledger untouched, nothing logged.
        assert_eq!(
            f.modify(hh(100), -1000, EconomyReason::ObjectPurchase, Vec::new(), 1),
            Err(EconomyErr::InsufficientFunds { need: 1000, have: 500 })
        );
        assert_eq!(f.ledger[&hh(100)], 500);
        assert_eq!(f.log.len(), pre_log);
        // Above MAX.
        let mut rich = funds_with(hh(200), FUNDS_MAX);
        assert_eq!(
            rich.modify(hh(200), 2000, EconomyReason::CheatCode, Vec::new(), 2),
            Err(EconomyErr::ExceedsFundsMax { delta: 2000 })
        );
        // checked_add boundary: an i32-overflowing delta is ExceedsFundsMax,
        // never a wrap (Kani `checked_add_no_overflow`).
        rich.ledger.insert(hh(300), i32::MAX - 5);
        assert_eq!(
            rich.modify(hh(300), 10, EconomyReason::CheatCode, Vec::new(), 3),
            Err(EconomyErr::ExceedsFundsMax { delta: 10 })
        );
        // Unknown household.
        assert_eq!(
            rich.modify(hh(999), 0, EconomyReason::ObjectSale, Vec::new(), 4),
            Err(EconomyErr::UnknownHousehold)
        );
    }

    #[test]
    fn successful_purchase_logs_exactly_one_row_and_replays() {
        let mut f = funds_with(hh(100), 500);
        let pre_id = f.next_tx;
        let placed = ObjectGuid(0xfeed);
        let id = f
            .modify(hh(100), -200, EconomyReason::ObjectPurchase, vec![placed], 3)
            .unwrap();
        assert_eq!(id, TxId(pre_id));
        assert_eq!(f.ledger[&hh(100)], 300);
        assert_eq!(f.log.len(), 2); // opening cheat row + purchase
        // Log replay reproduces the ledger exactly from zero.
        let replayed = f.replay_ledger();
        assert_eq!(replayed.get(&hh(100)), Some(&300));
    }

    #[test]
    fn replay_of_same_tx_id_is_idempotent() {
        let mut f = FamilyFunds::default();
        f.ledger.insert(hh(1), 0);
        let row = EconomyTx {
            tx_id: TxId(42),
            household_id: hh(1),
            delta: 50_000,
            reason: EconomyReason::CheatCode,
            tick_id: 7,
            affected_guids: Vec::new(),
        };
        assert_eq!(f.apply_replay(row.clone()), Ok(TxId(42)));
        assert_eq!(
            f.apply_replay(row),
            Err(EconomyErr::TxAlreadyApplied(TxId(42)))
        );
        assert_eq!(f.ledger[&hh(1)], 50_000); // advanced exactly once
        assert_eq!(f.log.len(), 1);
    }

    #[test]
    fn undo_appends_the_inverse_row() {
        let mut f = funds_with(hh(100), 1_000);
        let placed = ObjectGuid(0xabc);
        let buy = f
            .modify(hh(100), -500, EconomyReason::ObjectPurchase, vec![placed], 5)
            .unwrap();
        let undo = f.undo(buy, 6).unwrap();
        assert_eq!(f.ledger[&hh(100)], 1_000);
        let row = &f.log[f.log.len() - 1];
        assert_eq!(row.tx_id, undo);
        assert_eq!(row.delta, 500);
        assert_eq!(row.reason, EconomyReason::Undo);
        assert_eq!(row.affected_guids, vec![placed]);
        // Undoing a row that is not in the log is TxAlreadyApplied-shaped.
        assert_eq!(f.undo(TxId(9_999), 7), Err(EconomyErr::TxAlreadyApplied(TxId(9_999))));
    }

    fn edit_tx(epoch: u32, op: LotEditOp, guid: ObjectGuid, pos: Vec3Fixed) -> LotEditTx {
        LotEditTx {
            op,
            guid,
            resource_key: ResourceKey(1, 2, 3),
            pos,
            facing_deg: 0,
            lot_id: LotId(1),
            lock_epoch: epoch,
            tx_id: TxId(0),
        }
    }

    #[test]
    fn lot_edit_lock_is_exclusive_and_epoch_versioned() {
        let mut ec = LotEditCoordinator::default();
        let l1 = ec.acquire(LotId(1), PlayerId(7), 0).unwrap();
        assert_eq!(l1.epoch, 1);
        // Another editor is refused, with the holder named.
        assert_eq!(
            ec.acquire(LotId(1), PlayerId(8), 5),
            Err(LotEditErr::LotBusy { holder: PlayerId(7), epoch: 1 })
        );
        // Same editor re-acquires and bumps the epoch.
        let l2 = ec.acquire(LotId(1), PlayerId(7), 6).unwrap();
        assert_eq!(l2.epoch, l1.epoch + 1);
        // A transaction under the stale epoch is refused; the current one lands.
        assert_eq!(
            ec.commit(edit_tx(l1.epoch, LotEditOp::Place, ObjectGuid(1), Vec3Fixed(0, 0, 0))),
            Err(LotEditErr::StaleEpoch { held: 1, current: 2 })
        );
        assert!(ec
            .commit(edit_tx(l2.epoch, LotEditOp::Place, ObjectGuid(1), Vec3Fixed(0, 0, 0)))
            .is_ok());
        // Release is epoch-exact.
        assert!(!ec.release(l1));
        assert!(ec.release(l2));
        assert_eq!(
            ec.commit(edit_tx(2, LotEditOp::Rotate, ObjectGuid(1), Vec3Fixed(0, 0, 0))),
            Err(LotEditErr::LotBusy { holder: PlayerId(0), epoch: 0 })
        );
    }

    #[test]
    fn footprint_and_slot_clearance_refuse_before_broadcast() {
        let mut ec = LotEditCoordinator::default();
        let lock = ec.acquire(LotId(1), PlayerId(7), 0).unwrap();
        ec.commit(edit_tx(lock.epoch, LotEditOp::Place, ObjectGuid(1), Vec3Fixed(150, 0, 150)))
            .unwrap();
        // A second object into the same cell names the conflicting guid.
        assert_eq!(
            ec.commit(edit_tx(lock.epoch, LotEditOp::Place, ObjectGuid(2), Vec3Fixed(190, 0, 190))),
            Err(LotEditErr::FootprintBlocked { conflicting_guid: ObjectGuid(1) })
        );
        // A cell covered by a routing slot reservation (card 03) refuses too.
        ec.reserve_slot_clearance(LotId(1), (5, 5));
        assert_eq!(
            ec.commit(edit_tx(lock.epoch, LotEditOp::Place, ObjectGuid(3), Vec3Fixed(520, 0, 520))),
            Err(LotEditErr::SlotClearance)
        );
        // Rejections never mutated the registry.
        assert_eq!(ec.footprint_count(), 1);
        // A rejected re-place still leaves the original in its cell; a Move
        // relocates it.
        ec.commit(edit_tx(lock.epoch, LotEditOp::Move, ObjectGuid(1), Vec3Fixed(900, 0, 900)))
            .unwrap();
        assert_eq!(ec.footprint_count(), 1);
        assert_eq!(
            ec.commit(edit_tx(lock.epoch, LotEditOp::Delete, ObjectGuid(9), Vec3Fixed(0, 0, 0))),
            Err(LotEditErr::NotOwnerOfLot)
        );
    }

    #[test]
    fn purchase_with_blocked_placement_rolls_back_by_one_undo_row() {
        let mut eco = HouseholdEconomy::default();
        eco.funds.ledger.insert(hh(100), 0);
        eco.funds
            .modify(hh(100), 2_000, EconomyReason::CheatCode, Vec::new(), 0)
            .unwrap();
        let lock = eco.edits.acquire(LotId(1), PlayerId(7), 1).unwrap();
        // Pre-occupy the target cell so the placement is refused.
        eco.edits
            .commit(edit_tx(lock.epoch, LotEditOp::Place, ObjectGuid(55), Vec3Fixed(10, 0, 10)))
            .unwrap();
        let before = eco.funds.ledger[&hh(100)];
        let log_before = eco.funds.log.len();
        let result = eco
            .buy_object(PlayerId(7), hh(100), ObjectGuid(66), 300, edit_tx(lock.epoch, LotEditOp::Place, ObjectGuid(66), Vec3Fixed(20, 0, 20)), 2)
            .unwrap();
        // Net zero: the ledger is back at `before`, the last row is the Undo.
        assert_eq!(eco.funds.ledger[&hh(100)], before);
        assert_eq!(eco.funds.log.len(), log_before + 2); // purchase + undo
        assert_eq!(eco.funds.log[eco.funds.log.len() - 1].tx_id, result);
        assert_eq!(eco.funds.log[eco.funds.log.len() - 1].reason, EconomyReason::Undo);
        // The refused object never entered the footprint registry.
        assert_eq!(eco.edits.footprint_count(), 1);
    }

    #[test]
    fn bill_paid_records_exactly_one_row_per_household() {
        let mut eco = HouseholdEconomy::default();
        for id in [100u32, 200, 300] {
            eco.funds.ledger.insert(HouseholdId(id), 0);
            eco.funds
                .modify(HouseholdId(id), 10_000, EconomyReason::CareerPayment, Vec::new(), 0)
                .unwrap();
        }
        for id in [100u32, 200, 300] {
            eco.funds
                .modify(HouseholdId(id), -250, EconomyReason::BillPaid, Vec::new(), 54_000)
                .unwrap();
        }
        let bills: Vec<_> = eco
            .funds
            .log
            .iter()
            .filter(|e| e.reason == EconomyReason::BillPaid)
            .collect();
        assert_eq!(bills.len(), 3);
        assert!(bills.iter().all(|e| e.delta == -250));
        for id in [100u32, 200, 300] {
            assert_eq!(eco.funds.ledger[&HouseholdId(id)], 9_750);
        }
    }
}
