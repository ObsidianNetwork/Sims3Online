#![forbid(unsafe_code)]
//! Tick-driven park meet over the shipped clean-room subsystem APIs.

use std::collections::{BTreeMap, BTreeSet};
use crate::{HandleArena, KernelError, LotId, ObjectGuid, SimId};
use crate::authority::{HouseholdId, LotSimRate, LotSimulationScope, ServerHouseholdAuthority};
use crate::dialogs::{DialogAnswer, DialogKind, DialogRequest, RemoteModalDialogRouter};
use crate::environment::WorldEnvironmentState;
use crate::interactions::{InteractionFamilyTag, InteractionInstance, InteractionProducer, InteractionQueue};
use crate::persistence::{restore_primary, SaveHeader};
use crate::kernel_iface::{LockingPolicy, PathRng, SlotIndex, SlotReservation, SlotReservationStore};
use crate::{KernelRng, KernelSlots};
use crate::services::{RoleAuthority, ServiceKind, Vec3Fixed, WorldServiceSpawnerGate};
use crate::social_state::{bits, LongTermRelationshipType, RelationshipMatrix, RelationshipPair,
    RelationshipState, ShortTermContextType};
use crate::vehicles::{RoadSplineSegment, SeatSlot, VehicleGuid, VehicleTravel, VehicleTravelState};
use crate::wire::{JoinCounters, PlayerId, TickLoopState, WorldDelta, WorldSnapshot};

#[derive(Clone, Copy, Debug, Default)]
pub struct Sim176Stats {
    pub tick_drops_during_join: u64,
    pub snaps_detected: u64,
    pub stale_handles_observed: u64,
    pub unauthorized_client_insertions: u64,
    pub client_service_spawns: u64,
    pub pairs_transitioned: u64,
    pub dialogs_open: u64,
}
impl Sim176Stats {
    pub fn benchmark_green(&self) -> bool {
        self.tick_drops_during_join == 0 && self.snaps_detected == 0
            && self.stale_handles_observed == 0 && self.unauthorized_client_insertions == 0
            && self.client_service_spawns == 0 && self.pairs_transitioned == 3 && self.dialogs_open == 0
    }
}
pub struct Sim176Outcome {
    pub stats: Sim176Stats,
    pub state_hashes: [u64; 4],
    pub home_scope_runs: [u64; 3],
    pub park_slots_held: usize,
    pub rng_states: [u32; 4],
}
fn state_hash(world: &WorldSnapshot, pairs: &RelationshipMatrix, rng: u32, weather: &WorldEnvironmentState) -> u64 {
    let mut h = 0xcbf2_9ce4_8422_2325u64;
    let mut add = |n: u64| {
        for b in n.to_le_bytes() { h = (h ^ u64::from(b)).wrapping_mul(0x100_0000_01b3); }
    };
    add(world.tick); add(u64::from(rng)); add(weather.tick_id);
    add(u64::from(weather.season)); add(u64::from(weather.moon_phase));
    for (id, value) in &world.objects { add(*id); add(*value); }
    for s in pairs.pairs.values() {
        for n in [u64::from(s.pair.lo.0), u64::from(s.pair.hi.0), s.liking as u64,
            s.stc_commodity as u64, s.stc_type as u64, u64::from(s.stc_perceived.0),
            s.ltr as u64, u64::from(s.interaction_bits), s.ltr_start_ticks,
            s.highest_liking as u64, s.when_last_greeted, s.when_last_talked] { add(n); }
    }
    h
}
pub fn run_park_meet(sim_ticks: u64, seed: u32) -> Result<Sim176Outcome, KernelError> {
    let sims = [SimId(1), SimId(2), SimId(3)];
    let mut authority = ServerHouseholdAuthority::default();
    authority.claimed_sims.extend(sims);
    authority.active_households.extend([HouseholdId(1), HouseholdId(2), HouseholdId(3)]);
    authority.local_active_actor = Some(sims[0]);
    let membership: BTreeMap<_,_> = sims.into_iter().enumerate()
        .map(|(i,s)| (s, HouseholdId(i as u32 + 1))).collect();
    let header = SaveHeader::capture_pin(1, 0, &authority, &membership)
        .map_err(|_| KernelError::VerifyHeaderMismatch)?;
    let mut homes = sims.map(|sim| LotSimulationScope { rate: LotSimRate::Full30Hz,
        sleep_ticks: 0, occupants: [sim].into_iter().collect() });
    let mut park = LotSimulationScope { rate: LotSimRate::Throttled30T,
        sleep_ticks: 0, occupants: BTreeSet::new() };
    let mut runs = [0u64; 3];
    let mut trips: Vec<_> = sims.into_iter().enumerate().map(|(i, sim)| VehicleTravel {
        sim, vehicle: VehicleGuid(i as u64 + 10), seat: SeatSlot(2),
        state: VehicleTravelState::WalkToCurb, dest_lot: LotId(4), curb_slot: SlotIndex(i as u32),
        spline_stream: Vec::new(), snaps_detected: 0, last_pos: Vec3Fixed(0,0,0),
    }).collect();
    let mut slots = KernelSlots::default();
    let mut queue = InteractionQueue::new();
    let mut matrices: [RelationshipMatrix; 4] = std::array::from_fn(|_| RelationshipMatrix::default());
    let mut worlds: [TickLoopState; 4] = std::array::from_fn(|_| TickLoopState::new());
    let mut rng = [seed; 4];
    let mut path_rng = std::array::from_fn::<_,4,_>(|_| KernelRng::from_state(seed));
    let mut weather = [WorldEnvironmentState::default(); 4];
    let mut reader = None;
    let mut counters = JoinCounters::default();
    let mut client_services: [WorldServiceSpawnerGate; 3] =
        std::array::from_fn(|_| WorldServiceSpawnerGate::new(RoleAuthority::PresenterClient));
    let services: Vec<_> = (0..22).map(ServiceKind::Other).collect();
    let mut dialogs = RemoteModalDialogRouter::default();
    let mut arena = HandleArena::default();
    let mut handles = sims.map(|s| arena.insert(s)).into_iter().collect::<Result<Vec<_>,_>>()?;
    let mut stale_handles_observed = 0;
    let mut unauthorized_client_insertions = 0;
    for tick in 0..sim_ticks {
        if tick < 90 {
            for (i, home) in homes.iter_mut().enumerate() {
                home.refresh_scope(&authority);
                if home.should_simulate_this_tick() { runs[i] += 1; }
            }
        }
        park.refresh_scope(&authority);
        park.should_simulate_this_tick();
        if tick == 90 {
            for trip in &mut trips {
                trip.try_transition_to(VehicleTravelState::FadeAndMountVehicle)?;
                trip.try_transition_to(VehicleTravelState::RoadSplineSegmentStream)?;
            }
        }
        if (91..=380).contains(&tick) {
            for (i, trip) in trips.iter_mut().enumerate() {
                trip.apply_segment(RoadSplineSegment { tick_id: tick, vehicle: trip.vehicle,
                    pos: Vec3Fixed(((tick - 90) * [16_400, 14_800, 12_000][i] / 290) as u32, 0, 0),
                    facing_xz: (1,0), seat_slot: trip.seat })?;
            }
        }
        if tick == 380 {
            for trip in &mut trips {
                trip.try_transition_to(VehicleTravelState::DismountAndFadeIn)?;
                trip.try_transition_to(VehicleTravelState::ArriveAtLot)?;
                slots.reserve(SlotReservation { sim: trip.sim, object: ObjectGuid(4),
                    slot: trip.curb_slot, policy: LockingPolicy::MustNotHaveKey, generation: 0 })
                    .map_err(|_| KernelError::SeatAlreadyOccupied)?;
                park.occupants.insert(trip.sim);
            }
            reader = Some(worlds[0].snapshot_for_join());
            dialogs.request_for_claimed_sim(DialogRequest { corr: 1, id: 1, sim: sims[2],
                owner_player: PlayerId(3), kind: DialogKind::AcceptCancel, opened_at_tick: tick }, &authority);
        }
        if tick == 540 {
            if reader.as_ref().is_some_and(|r| r.tick != 379) { return Err(KernelError::TestRejected); }
            reader = None;
            dialogs.answer_from_player(PlayerId(3), 1, 1, DialogAnswer::Selected(1));
        }
        if tick == 700 {
            for h in &mut handles {
                let old = *h;
                let value = arena.remove(old)?;
                *h = arena.insert(value)?;
                if arena.get(old) != Err(KernelError::StaleObjectHandle) { stale_handles_observed += 1; }
            }
        }
        if tick == 800 {
            for (id, (a,b)) in [(sims[0],sims[1]),(sims[0],sims[2]),(sims[1],sims[2])].into_iter().enumerate() {
                queue.insert(InteractionInstance { id: id as u64, family: InteractionFamilyTag::SocializeWith,
                    instance_actor: a, target_sim: Some(b), priority: 4, test_passes: true },
                    queue.len(), InteractionProducer::ServerCommand, &authority)?;
                let pair = RelationshipPair::new(a,b);
                let st = RelationshipState { pair, liking: 3, stc_commodity: 3,
                    stc_type: ShortTermContextType::Friendly, stc_perceived: b,
                    ltr: matrices[0].ltr_threshold_check(pair, 3, bits::HAS_MET),
                    interaction_bits: bits::HAS_MET, ltr_start_ticks: tick, highest_liking: 3,
                    when_last_greeted: tick, when_last_talked: tick };
                for m in &mut matrices { m.set_from_mirror(st); }
            }
            let bad = InteractionInstance { id: 99, family: InteractionFamilyTag::BedSleep,
                instance_actor: sims[0], target_sim: None, priority: 4, test_passes: true };
            if queue.insert(bad, 0, InteractionProducer::ClientAutonomy, &authority).is_ok() {
                unauthorized_client_insertions += 1;
            }
        }
        for h in &handles { if arena.get(*h).is_err() { stale_handles_observed += 1; } }
        for (i, world) in worlds.iter_mut().enumerate() {
            rng[i] = KernelRng::node_step(rng[i]);
            let draw = path_rng[i].next_bounded(100);
            world.commit_tick(WorldDelta { tick, object_add: Some((1, u64::from(draw))), object_remove: None });
        }
        let server_weather = WorldEnvironmentState { tick_id: tick, season: 2, ..weather[0] };
        for w in &mut weather { if !w.apply_server_state(server_weather) { return Err(KernelError::TestRejected); } }
        for gate in &mut client_services { gate.simulate_round(&services); }
        counters.presentation_sent(tick, reader.is_some());
        dialogs.tick(tick);
    }
    authority.local_active_actor = Some(sims[2]);
    restore_primary(&header, &membership, &mut authority).map_err(|_| KernelError::VerifyHeaderMismatch)?;
    if authority.local_active_actor != Some(sims[0]) { return Err(KernelError::TestRejected); }
    let hashes = std::array::from_fn(|i| state_hash(&worlds[i].world, &matrices[i], rng[i], &weather[i]));
    let stats = Sim176Stats { tick_drops_during_join: counters.tick_drops_during_join,
        snaps_detected: trips.iter().map(|v| u64::from(v.snaps_detected)).sum(),
        stale_handles_observed, unauthorized_client_insertions,
        client_service_spawns: client_services.iter().map(|g| g.spawn_frames.len() as u64).sum(),
        pairs_transitioned: matrices[0].pairs.values().filter(|s| s.ltr == LongTermRelationshipType::Acquaintance).count() as u64,
        dialogs_open: dialogs.pending.len() as u64 };
    Ok(Sim176Outcome { stats, state_hashes: hashes, home_scope_runs: runs,
        park_slots_held: (0..3).filter(|i| slots.holder(ObjectGuid(4), SlotIndex(*i)).is_some()).count(),
        rng_states: rng })
}
/// Card 26 §C golden-corpus hook (canary 15, park-meet).
#[cfg(test)]
mod kdiff {
    use super::*;

    #[test]
    fn kdiff_canary_15() {
        let out = run_park_meet(1_000, 0x5EED).expect("park meet runs");
        let green = out.stats.benchmark_green() as u64;
        println!("KDIFF|canary=15|case=park_meet_green|got={:016x}", green);
        assert_eq!(green, 1);
        let again = run_park_meet(1_000, 0x5EED).expect("repeat");
        assert_eq!(out.state_hashes, again.state_hashes, "same seed -> same hashes");
        println!("KDIFF|canary=15|case=park_meet_hash0|got={:016x}", out.state_hashes[0]);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn park_meet_drives_real_subsystems_and_four_converged_worlds() {
        for seed in [0, 1, 0x8a86_3e32, u32::MAX] {
            let out = run_park_meet(1000, seed).unwrap();
            assert!(out.stats.benchmark_green(), "{:?}", out.stats);
            assert_eq!(out.home_scope_runs, [90; 3]);
            assert_eq!(out.park_slots_held, 3);
            assert!(out.state_hashes.iter().all(|h| *h == out.state_hashes[0]));
            assert!(out.rng_states.iter().all(|r| *r == out.rng_states[0]));
        }
        assert!(!run_park_meet(100, 1).unwrap().stats.benchmark_green());
    }
}
