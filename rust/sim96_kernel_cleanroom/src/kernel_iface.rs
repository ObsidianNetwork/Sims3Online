#![forbid(unsafe_code)]
//! The tracked interface between the clean-room kernel and its private ABI
//! oracles (review F5). Shapes and traits only: no game constants, no
//! generator parameters, no translated logic.
//!
//! `src/routing.rs` and `src/sacs_rng.rs` are git-ignored oracles, built when
//! both exist or with the `private-oracles` feature (`build.rs`). Every
//! tracked module names the kernel's random
//! source and slot store through [`crate::KernelRng`] and
//! [`crate::KernelSlots`], which resolve to the oracles with the feature on
//! and to `kernel_default` without it, so a fresh clone builds and tests the
//! whole kernel, the daemon included.

use crate::{ObjectGuid, SimId};

/// One route waypoint as the managed side sees it (card 01 §C: a `u32` tag,
/// a `u64` object id and three position words). Zeroed is the failure value.
#[derive(Default, Debug, Clone, Copy, PartialEq, Eq)]
pub struct PathData {
    pub path_type: u32,
    pub object_id: u64,
    pub portal_bits: [u32; 3],
}

/// A routing slot on one object (card 03 §C).
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct SlotIndex(pub u32);

/// Which footprint-lock rule admitted a reservation (card 03 §C).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LockingPolicy {
    MustHaveKey,
    MustNotHaveKey,
}

/// A routing-slot claim by one Sim (card 03 §C). `generation` makes a stale
/// release a no-op instead of dropping a newer claim.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SlotReservation {
    pub sim: SimId,
    pub object: ObjectGuid,
    pub slot: SlotIndex,
    pub policy: LockingPolicy,
    pub generation: u32,
}

/// The routing-slot claim table (card 03 §C, §C.1, §G).
pub trait SlotReservationStore {
    /// Claim `(r.object, r.slot)` for `r.sim`. `Err(sim)` names who blocks it:
    /// the current holder, or `r.sim` itself when `r.generation` is stale.
    fn reserve(&mut self, r: SlotReservation) -> Result<(), SimId>;
    /// Drop the claim `r` describes. `Some(holder)` only when the sim and the
    /// generation both match the live claim; anything else is `None`.
    fn release(&mut self, r: SlotReservation) -> Option<SimId>;
    /// Who holds `(object, slot)` now.
    fn holder(&self, object: ObjectGuid, slot: SlotIndex) -> Option<SimId>;
}

/// The kernel's seeded random source (card 04's two transitions, by contract
/// only). Every result is a pure function of the state, so a replay from the
/// same seed repeats; `result < limit` whenever `limit > 0`, and `0` when
/// `limit == 0`.
pub trait PathRng: Sized {
    /// A generator positioned at `state`.
    fn from_state(state: u32) -> Self;
    /// The state the next draw starts from.
    fn state(&self) -> u32;
    /// One path draw from the current state, advancing it.
    fn next_bounded(&mut self, limit: u32) -> u32;
    /// The stateless path draw: `(new_state, result)`.
    fn path_draw(state: u32, limit: u32) -> (u32, u32);
    /// The stateless node-seed step: the next node seed.
    fn node_step(state: u32) -> u32;
}
