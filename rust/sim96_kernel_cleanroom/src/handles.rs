#![forbid(unsafe_code)]

use crate::interactions::{InteractionFamilyTag, InteractionProducer};

#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct GenHandle { pub index: u32, pub generation: u32 }
const _: () = assert!(std::mem::size_of::<GenHandle>() == 8);
const _: () = assert!(std::mem::offset_of!(GenHandle, index) == 0);
const _: () = assert!(std::mem::offset_of!(GenHandle, generation) == 4);

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum KernelError {
    StaleObjectHandle, UnknownHandle, SlotRetired,
    UnauthorizedClientInsertion { producer: InteractionProducer, family: InteractionFamilyTag },
    NeverReplay(InteractionFamilyTag), QueueFull, TestRejected,
    ClientWorldServiceSuppressed, VehicleGuidUnknown, SeatAlreadyOccupied,
    SplineSegmentOutOfOrder, DiscontinuousJump(u32),
    StageIoError, VerifyHeaderMismatch, VerifyRecordCrcMismatch, BackupCopyFault, RenameFault,
    // Wave 4 (cards 16 / 17 / 19).
    SocialBarrierTimeout, AbortedByClaimSwap, Unpaired, ClientReselectForbidden,
    InsufficientFunds, ExceedsFundsMax, UnknownHousehold, TxAlreadyApplied,
    LotBusy, StaleEpoch, FootprintBlocked, SlotClearance, NotOwnerOfLot,
    HelloRejected,
    // Wave 5 (cards 21 / 22 / 23 / 24 / 25). `EndOfWalkSnap` carries integer
    // centimetres so the enum keeps `Eq` (the contract's f32 metres would not).
    LockWishOverCap { cap: u8 }, AlreadyLocked, UnknownWish,
    BillUnknown, BillInsufficientFunds { need: i32 }, BillAlreadySettled,
    PartyCandidateEmpty, ServiceAssignmentUnknown,
    StartupStutterDetected { tick: u64 }, PaceMismatchAtLeg { leg: u32 },
    EndOfWalkSnap { distance_cm: u32 },
    // Wave 7 (card 33 §C: the sidecar relays this exact name on a slot clash).
    SlotAlreadyReserved,
    // Wave 11 (cards 51-54, canary 31): the clean-room A* planner's named
    // refusals - a rejection is always seen by the client, never silence.
    RouteGoalUnreachable, RouteStartOutOfBounds, PortalLocked,
    // Card 52 §C.2 / card 51 §F: a nav graph the loader or planner refuses.
    NavFormatUnsupported, NavContentHashMismatch,
    // Wave 12 (cards 56-59, canary 32): the .jazz sequencer's named refusals.
    UnknownJazzMachine, InvalidJazzTransition, JazzSocialBarrierTimeout,
}
struct Slot<T> { generation: u32, value: Option<T> }
pub struct HandleArena<T> { slots: Vec<Slot<T>> }
impl<T> Default for HandleArena<T> {
    fn default() -> Self { Self { slots: Vec::new() } }
}
impl<T> HandleArena<T> {
    pub fn new() -> Self { Self::default() }

    pub fn insert(&mut self, value: T) -> Result<GenHandle, KernelError> {
        for (index, slot) in self.slots.iter_mut().enumerate() {
            if slot.value.is_none() && slot.generation != u32::MAX {
                slot.value = Some(value);
                return Ok(GenHandle { index: index as u32, generation: slot.generation });
            }
        }
        let index = u32::try_from(self.slots.len()).map_err(|_| KernelError::SlotRetired)?;
        self.slots.push(Slot { generation: 0, value: Some(value) });
        Ok(GenHandle { index, generation: 0 })
    }
    pub fn get(&self, h: GenHandle) -> Result<&T, KernelError> {
        let slot = self.slots.get(h.index as usize).ok_or(KernelError::UnknownHandle)?;
        if slot.generation != h.generation { return Err(KernelError::StaleObjectHandle); }
        slot.value.as_ref().ok_or(KernelError::StaleObjectHandle)
    }
    pub fn get_mut(&mut self, h: GenHandle) -> Result<&mut T, KernelError> {
        let slot = self.slots.get_mut(h.index as usize).ok_or(KernelError::UnknownHandle)?;
        if slot.generation != h.generation { return Err(KernelError::StaleObjectHandle); }
        slot.value.as_mut().ok_or(KernelError::StaleObjectHandle)
    }
    pub fn remove(&mut self, h: GenHandle) -> Result<T, KernelError> {
        let slot = self.slots.get_mut(h.index as usize).ok_or(KernelError::UnknownHandle)?;
        if slot.generation != h.generation { return Err(KernelError::StaleObjectHandle); }
        let value = slot.value.take().ok_or(KernelError::StaleObjectHandle)?;
        slot.generation = slot.generation.saturating_add(1);
        Ok(value)
    }
}
/// Card 26 §C golden-corpus hook (canary 5,).
#[cfg(test)]
mod kdiff {
    use super::*;

    #[test]
    fn kdiff_canary_05() {
        let mut a = HandleArena::new();
        let h = a.insert(1u32).unwrap();
        a.remove(h).unwrap();
        let ok_reads = a.get(h).is_ok() as u64;
        println!("KDIFF|canary=5|case=stale_never_ok|got={:016x}", ok_reads);
        assert_eq!(ok_reads, 0);
        assert_eq!(a.get(h), Err(KernelError::StaleObjectHandle));
        let n = a.insert(2u32).unwrap();
        assert_eq!(n.index, h.index);
        assert_eq!(n.generation, h.generation + 1);
        assert_eq!(a.get(n), Ok(&2));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn generation_reuse_rejects_every_stale_access() {
        let mut a = HandleArena::default();
        let first = a.insert(1).unwrap();
        assert_eq!(a.remove(first), Ok(1));
        let next = a.insert(2).unwrap();
        assert_eq!(first.index, next.index);
        assert_ne!(first.generation, next.generation);
        assert_eq!(a.get(first), Err(KernelError::StaleObjectHandle));
        assert_eq!(a.get_mut(first), Err(KernelError::StaleObjectHandle));
        assert_eq!(a.remove(first), Err(KernelError::StaleObjectHandle));
        assert_eq!(a.get(next), Ok(&2));
        assert_eq!(a.get(GenHandle { index: u32::MAX, generation: 0 }), Err(KernelError::UnknownHandle));
    }
    #[test]
    fn exhausted_generation_never_wraps_into_an_old_identity() {
        let mut a = HandleArena { slots: vec![Slot { generation: u32::MAX - 1, value: Some(1) }] };
        let old = GenHandle { index: 0, generation: u32::MAX - 1 };
        a.remove(old).unwrap();
        let new = a.insert(2).unwrap();
        assert_eq!(new.index, 1);
        assert_eq!(a.get(old), Err(KernelError::StaleObjectHandle));
    }
}
