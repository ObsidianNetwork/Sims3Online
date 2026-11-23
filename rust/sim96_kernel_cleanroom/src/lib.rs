#![forbid(unsafe_code)]

pub mod guest_mem;
pub mod kernel_iface;
pub mod kernel_default;
// The git-ignored ABI oracles and the native canaries that need them (review F5).
// `sim96_private_oracles` comes from build.rs: the feature, or both files present.
#[cfg(sim96_private_oracles)]
pub mod routing;
pub mod routing_astar;
pub mod live_navigation;
// Overhaul-track planner (point/slot goals, budget, reachability gate). Off by
// default so the sealed card 55 build is unchanged (internal documentation).
#[cfg(feature = "nav-planner")]
pub mod nav_planner;
pub mod private_navigation;
pub mod road_placement;
pub mod jazz_sacs;
#[cfg(sim96_private_oracles)]
pub mod sacs_rng;
pub mod handles;
pub mod task_arena;
pub mod clock;
pub mod authority;
pub mod interactions;
pub mod social_state;
pub mod wire;
pub mod persistence;
pub mod services;
pub mod vehicles;
pub mod environment;
pub mod dialogs;
pub mod integration;
pub mod aspirations;
pub mod household_drivers;
pub mod careers;
pub mod npc_social_services;
pub mod route_presentation;
pub mod socials;
pub mod economy;
pub mod icall_catalog;
pub mod wan_transport;
pub mod p7_benchmark;
#[cfg(sim96_private_oracles)]
pub mod native_diff;
pub mod world_integration;
pub mod continuity;
pub mod soak_chaos;
pub mod go_nogo;
pub mod runtime_bridge;

pub use handles::{GenHandle, HandleArena, KernelError};

/// The kernel's random source: the SACS oracle in the oracle build, the
/// clean-room mixer without it (`kernel_iface::PathRng`).
#[cfg(sim96_private_oracles)]
pub type KernelRng = sacs_rng::SacsPathRng;
#[cfg(not(sim96_private_oracles))]
pub type KernelRng = kernel_default::MixPathRng;

/// The kernel's routing-slot store (`kernel_iface::SlotReservationStore`).
#[cfg(sim96_private_oracles)]
pub type KernelSlots = routing::SlotReservationMap;
#[cfg(not(sim96_private_oracles))]
pub type KernelSlots = kernel_default::SlotTable;

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct ObjectGuid(pub u64);
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct SimId(pub u32);
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct LotId(pub u32);
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct ObjectId(pub u64);

struct Slot<T> {
    generation: u16,
    value: Option<T>,
}

/// A compact 16-bit slot / 16-bit generation arena. Exhausted slots retire.
pub struct SimArena<T> {
    slots: Vec<Slot<T>>,
}

impl<T> Default for SimArena<T> {
    fn default() -> Self {
        Self { slots: Vec::new() }
    }
}

impl<T> SimArena<T> {
    pub fn insert(&mut self, value: T) -> Option<SimId> {
        if let Some((index, slot)) = self.slots.iter_mut().enumerate().find(|(_, s)| {
            s.value.is_none() && s.generation != u16::MAX
        }) {
            slot.value = Some(value);
            return Some(SimId(
                (u32::from(slot.generation) << 16) | u32::try_from(index).ok()?,
            ));
        }
        let index = u16::try_from(self.slots.len()).ok()?;
        self.slots.push(Slot {
            generation: 0,
            value: Some(value),
        });
        Some(SimId(u32::from(index)))
    }

    pub fn get(&self, id: SimId) -> Option<&T> {
        let index = usize::try_from(id.0 & 0xffff).ok()?;
        let generation = u16::try_from(id.0 >> 16).ok()?;
        let slot = self.slots.get(index)?;
        (slot.generation == generation).then_some(())?;
        slot.value.as_ref()
    }

    pub fn remove(&mut self, id: SimId) -> Option<T> {
        let index = usize::try_from(id.0 & 0xffff).ok()?;
        let generation = u16::try_from(id.0 >> 16).ok()?;
        let slot = self.slots.get_mut(index)?;
        if slot.generation != generation {
            return None;
        }
        let value = slot.value.take()?;
        slot.generation = slot.generation.saturating_add(1);
        Some(value)
    }

    pub fn get_mut(&mut self, id: SimId) -> Option<&mut T> {
        let index = usize::try_from(id.0 & 0xffff).ok()?;
        let generation = u16::try_from(id.0 >> 16).ok()?;
        let slot = self.slots.get_mut(index)?;
        (slot.generation == generation).then_some(())?;
        slot.value.as_mut()
    }
}

pub struct LotArena<T>(SimArena<T>);

impl<T> Default for LotArena<T> {
    fn default() -> Self {
        Self(SimArena::default())
    }
}

impl<T> LotArena<T> {
    pub fn insert(&mut self, value: T) -> Option<LotId> {
        self.0.insert(value).map(|id| LotId(id.0))
    }
    pub fn get(&self, id: LotId) -> Option<&T> {
        self.0.get(SimId(id.0))
    }
    pub fn remove(&mut self, id: LotId) -> Option<T> {
        self.0.remove(SimId(id.0))
    }
}

pub struct ObjectArena<T>(SimArena<T>);

impl<T> Default for ObjectArena<T> {
    fn default() -> Self {
        Self(SimArena::default())
    }
}

impl<T> ObjectArena<T> {
    pub fn insert(&mut self, value: T) -> Option<ObjectId> {
        self.0.insert(value).map(|id| ObjectId(u64::from(id.0)))
    }
    pub fn get(&self, id: ObjectId) -> Option<&T> {
        self.0.get(SimId(u32::try_from(id.0).ok()?))
    }
    pub fn remove(&mut self, id: ObjectId) -> Option<T> {
        self.0.remove(SimId(u32::try_from(id.0).ok()?))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stale_handle_does_not_alias_reused_slot() {
        let mut arena = SimArena::default();
        let first = arena.insert("first").unwrap();
        assert_eq!(arena.remove(first), Some("first"));
        let next = arena.insert("next").unwrap();
        assert_ne!(first, next);
        assert_eq!(arena.get(first), None);
        assert_eq!(arena.get(next), Some(&"next"));
    }

    #[test]
    fn generation_exhaustion_retires_slot() {
        let mut arena = SimArena {
            slots: vec![Slot {
                generation: u16::MAX - 1,
                value: Some(7),
            }],
        };
        let last = SimId(u32::from(u16::MAX - 1) << 16);
        assert_eq!(arena.remove(last), Some(7));
        let new = arena.insert(9).unwrap();
        assert_eq!(new.0 & 0xffff, 1);
        assert_eq!(arena.get(last), None);
    }

    #[test]
    fn unknown_handle_is_absent() {
        let arena: SimArena<u32> = SimArena::default();
        assert_eq!(arena.get(SimId(0xffff_ffff)), None);
    }

    #[test]
    fn typed_lot_and_object_handles_also_reject_stale_ids() {
        let mut lots = LotArena::default();
        let old_lot = lots.insert(1).unwrap();
        assert_eq!(lots.remove(old_lot), Some(1));
        let new_lot = lots.insert(2).unwrap();
        assert_ne!(old_lot, new_lot);
        assert_eq!(lots.get(old_lot), None);
        let mut objects = ObjectArena::default();
        let old_object = objects.insert(3).unwrap();
        assert_eq!(objects.remove(old_object), Some(3));
        let new_object = objects.insert(4).unwrap();
        assert_ne!(old_object, new_object);
        assert_eq!(objects.get(old_object), None);
        assert_eq!(objects.get(ObjectId(u64::MAX)), None);
    }

    #[test]
    fn cyclic_relationships_are_owned_handles_not_cyclic_borrows() {
        struct Sim {
            lot: LotId,
            interaction: Option<ObjectId>,
        }
        struct Interaction {
            owner: SimId,
            lot: LotId,
        }
        let mut lots = LotArena::default();
        let lot = lots.insert("home").unwrap();
        let mut sims = SimArena::default();
        let sim = sims.insert(Sim { lot, interaction: None }).unwrap();
        let mut interactions = ObjectArena::default();
        let interaction = interactions.insert(Interaction { owner: sim, lot }).unwrap();
        sims.get_mut(sim).unwrap().interaction = Some(interaction);
        let active = interactions.get(sims.get(sim).unwrap().interaction.unwrap()).unwrap();
        assert_eq!(active.owner, sim);
        assert_eq!(active.lot, sims.get(active.owner).unwrap().lot);
        assert_eq!(lots.get(active.lot), Some(&"home"));
        assert!(sims.remove(sim).is_some());
        assert!(sims.get(active.owner).is_none());
    }
}
