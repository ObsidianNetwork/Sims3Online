#![forbid(unsafe_code)]
//! The kernel's own implementations of the [`crate::kernel_iface`] traits, used
//! when the oracle build is off (review F5, `build.rs`). Nothing here is taken
//! from the game or from the private oracles: the generator is a public-domain
//! integer mixer, not the SACS generator, so its draws differ from it. Any
//! result that must match the game needs the private oracle files present.

use std::collections::BTreeMap;

use crate::kernel_iface::{PathRng, SlotIndex, SlotReservation, SlotReservationStore};
use crate::{ObjectGuid, SimId};

/// Weyl-sequence increment for the 32-bit state (2^32 / golden ratio).
const WEYL_STEP: u32 = 0x9E37_79B9;

/// splitmix64's increment (S. Vigna, public domain).
const SPLITMIX_GAMMA: u64 = 0x9E37_79B9_7F4A_7C15;

/// splitmix64's published finalizer (S. Vigna, public domain), applied to
/// the state plus its increment.
fn mix(x: u64) -> u64 {
    let mut z = x.wrapping_add(SPLITMIX_GAMMA);
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
    z ^ (z >> 31)
}

/// Reduce a mixed word into `0..limit` (`0` when `limit == 0`).
fn bounded(word: u64, limit: u32) -> u32 {
    if limit == 0 {
        return 0;
    }
    u32::try_from(word % u64::from(limit)).unwrap_or(0)
}

/// The interface build's random source: a Weyl counter whose output is mixed.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MixPathRng {
    state: u32,
}

impl PathRng for MixPathRng {
    fn from_state(state: u32) -> Self {
        Self { state }
    }

    fn state(&self) -> u32 {
        self.state
    }

    fn next_bounded(&mut self, limit: u32) -> u32 {
        let (state, result) = Self::path_draw(self.state, limit);
        self.state = state;
        result
    }

    fn path_draw(state: u32, limit: u32) -> (u32, u32) {
        let next = state.wrapping_add(WEYL_STEP);
        (next, bounded(mix(u64::from(next)), limit))
    }

    fn node_step(state: u32) -> u32 {
        // The low word of the mixed value.
        (mix(u64::from(state)) & u64::from(u32::MAX)) as u32
    }
}

/// The interface build's slot table: one live claim per `(object, slot)`
/// (card 03 §C, §C.1, §G). The kernel adds its own rule, not card 03's: a
/// claim's generation must exceed every earlier one on that key, so a replayed
/// old claim cannot come back (the oracle build's store answers the same way).
#[derive(Default, Debug, Clone)]
pub struct SlotTable {
    live: BTreeMap<(ObjectGuid, SlotIndex), (SimId, u32)>,
    newest: BTreeMap<(ObjectGuid, SlotIndex), u32>,
}

impl SlotReservationStore for SlotTable {
    fn reserve(&mut self, r: SlotReservation) -> Result<(), SimId> {
        let key = (r.object, r.slot);
        if let Some(&(holder, _)) = self.live.get(&key) {
            return Err(holder);
        }
        if matches!(self.newest.get(&key), Some(&seen) if r.generation <= seen) {
            return Err(r.sim);
        }
        self.newest.insert(key, r.generation);
        self.live.insert(key, (r.sim, r.generation));
        Ok(())
    }

    fn release(&mut self, r: SlotReservation) -> Option<SimId> {
        let key = (r.object, r.slot);
        match self.live.get(&key) {
            Some(&(holder, generation)) if holder == r.sim && generation == r.generation => {
                self.live.remove(&key);
                Some(holder)
            }
            _ => None,
        }
    }

    fn holder(&self, object: ObjectGuid, slot: SlotIndex) -> Option<SimId> {
        self.live.get(&(object, slot)).map(|&(holder, _)| holder)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::kernel_iface::LockingPolicy;

    fn claim(sim: u32, slot: u32, generation: u32) -> SlotReservation {
        SlotReservation { sim: SimId(sim), object: ObjectGuid(1), slot: SlotIndex(slot),
            policy: LockingPolicy::MustNotHaveKey, generation }
    }

    /// Card 03 §G `reserve_is_atomic_per_object_slot`.
    #[test]
    fn reserve_is_atomic_per_object_slot() {
        let mut t = SlotTable::default();
        assert_eq!(t.reserve(claim(1, 1, 1)), Ok(()));
        assert_eq!(t.reserve(claim(2, 1, 2)), Err(SimId(1)));
        assert_eq!(t.reserve(claim(2, 2, 1)), Ok(()));
        assert_eq!(t.holder(ObjectGuid(1), SlotIndex(1)), Some(SimId(1)));
    }

    /// Card 03 §G `release_bumps_generation` and `stale_generation_release_is_err`;
    /// the replayed-claim refusal is the kernel's own rule.
    #[test]
    fn stale_release_and_replayed_claim_are_refused() {
        let mut t = SlotTable::default();
        t.reserve(claim(1, 1, 5)).unwrap();
        assert_eq!(t.release(claim(2, 1, 5)), None, "another sim cannot release");
        assert_eq!(t.release(claim(1, 1, 4)), None, "an old generation cannot release");
        assert_eq!(t.release(claim(1, 1, 5)), Some(SimId(1)));
        assert_eq!(t.release(claim(1, 1, 5)), None);
        assert_eq!(t.reserve(claim(1, 1, 5)), Err(SimId(1)), "a replayed claim stays released");
        assert_eq!(t.reserve(claim(1, 1, 6)), Ok(()));
    }

    #[test]
    fn rng_is_deterministic_and_bounded() {
        for seed in [0, 1, 0x5EED_F00D, u32::MAX] {
            let (mut a, mut b) = (MixPathRng::from_state(seed), MixPathRng::from_state(seed));
            for limit in [0, 1, 7, 100, u32::MAX] {
                let x = a.next_bounded(limit);
                assert_eq!(x, b.next_bounded(limit));
                assert!(if limit == 0 { x == 0 } else { x < limit });
            }
            assert_eq!(a.state(), b.state());
            let (s, r) = MixPathRng::path_draw(seed, 0);
            assert_ne!(s, seed, "the state advances even when the result is zero");
            assert_eq!(r, 0);
            assert_eq!(MixPathRng::node_step(seed), MixPathRng::node_step(seed));
        }
    }
}
