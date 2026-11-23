#![forbid(unsafe_code)]

use std::collections::BTreeMap;
use sim96_kernel_cleanroom::{
    authority::{LotSimulationScope, ServerHouseholdAuthority},
    clock::{AlarmManager, SimClock},
    guest_mem::{addr32_offset, GuestMem32},
    task_arena::{GenHandle, HandleArena, KernelError},
    interactions::{FailureExplanation, InteractionFamilyTag, InteractionInstance,
        InteractionProducer, InteractionQueue, QueueEffect, RouteStopEventType},
    routing::{self, RouteKernel as KernelRoute, LockingPolicy, PathData, PathType, RouteCalls,
        SlotIndex, SlotReservation, SlotReservationKernel as SlotReservationMap, SlotReservationStore},
    sacs_rng::{path_draw_lcg, random_node_lcg, PathPickMode, SacsPathRng},
    social_state::{self, bits, BuffCommodityDecayModifier, CommodityKind,
        LongTermRelationshipType, MotiveStore, MotiveValue, RelationshipMatrix, RelationshipPair},
    wire::{JoinCounters, JoinFingerprint, JoinMissReason, JoinReuseState,
        PlayerId, PublicProtocolCodec, TickLoopState, WorldDelta},
    ObjectGuid, SimId,
};

struct Words(BTreeMap<u32, u32>);
impl GuestMem32 for Words {
    fn read_u32(&self, addr: u32) -> Option<u32> { self.0.get(&addr).copied() }
}

#[derive(Default)]
struct Calls { trace: Vec<(u32, u32)>, fail_unlock: bool }
impl RouteCalls for Calls {
    fn lookup_route(&mut self, system: u32, handle: u32) -> Option<u32> {
        assert_eq!((system, handle), (1, 0x2500_0001));
        Some(0x1000)
    }
    fn invoke_lock(&mut self, method: u32, route: u32) -> Option<()> {
        assert_eq!((method, route), (0xab10, 0x1000));
        self.trace.push((method, route));
        Some(())
    }
    fn invoke_unlock(&mut self, method: u32, route: u32) -> Option<()> {
        assert_eq!((method, route), (0xab14, 0x1000));
        self.trace.push((method, route));
        (!self.fail_unlock).then_some(())
    }
    fn invoke_path_data(&mut self, method: u32, child: u32) -> Option<u32> {
        assert_eq!((method, child), (0xab30, 0x7000));
        self.trace.push((method, child));
        Some(0xb000)
    }
}

fn route_probe() {
    let mut mem = Words([
        (0x1000, 0x9000), (0x9010, 0xab10), (0x9014, 0xab14),
        (0x10e0, 0x3000), (0x3000, 0x10e0), (0x3008, 0x7000),
        (0x7000, 0x9100), (0x9130, 0xab30),
        (0xb000, 0x8a86_3e32), (0xb008, 0x89ab_cdef), (0xb00c, 0x0123_4567),
        (0xb010, 0x8000_0000), (0xb014, 0x7fc1_2345), (0xb018, 0x3f80_0000),
    ].into_iter().collect());
    let expected = PathData {
        path_type: 0x8a86_3e32, object_id: 0x0123_4567_89ab_cdef,
        portal_bits: [0x8000_0000, 0x7fc1_2345, 0x3f80_0000],
    };
    let mut calls = Calls::default();
    let raw = routing::route_get_path_data_abi_raw(&mem, &mut calls, 1, 0x2500_0001, 0);
    assert_eq!(raw, Some(expected));
    assert_eq!(calls.trace, [(0xab10, 0x1000), (0xab30, 0x7000), (0xab14, 0x1000)]);
    let owned = KernelRoute::from_guest_mem(&mem, &mut calls, 1, 0x2500_0001, 1).unwrap();
    assert_eq!(owned.path(0), raw.as_ref());
    assert_eq!(Some(owned.path_data(0)), raw);
    for index in [1, 0x0aaa_aaab, 0x8000_0000, 0xffff_ffec, u32::MAX] {
        let raw = routing::route_get_path_data_abi_raw(&mem, &mut calls, 1, 0x2500_0001, index);
        assert_eq!(raw, Some(PathData::default()));
        assert_eq!(raw, Some(owned.path_data(index)));
    }
    mem.0.remove(&0xb014);
    let before = calls.trace.len();
    assert_eq!(routing::route_get_path_data_abi_raw(&mem, &mut calls, 1, 0x2500_0001, 0), None);
    assert_eq!(calls.trace[before + 2], (0xab14, 0x1000));
    mem.0.insert(0xb014, 0x7fc1_2345);
    mem.0.remove(&0x9014);
    let before = calls.trace.len();
    assert_eq!(routing::route_get_path_data_abi_raw(&mem, &mut calls, 1, 0x2500_0001, 0), None);
    assert_eq!(calls.trace.len(), before, "unresolved cleanup must not acquire the lock");
    mem.0.insert(0x9014, 0xab14);
    calls.fail_unlock = true;
    assert_eq!(routing::route_get_path_data_abi_raw(&mem, &mut calls, 1, 0x2500_0001, 0), None);

    let fixture = Words([
        (0x100, 0x200), (0x11c, u32::MAX), (0x200, 0x400), (0x208, 0x500),
        (0x400, 0x89ab_cdef), (0x404, 0x0123_4567),
        (0x500, 0x7654_3210), (0x504, 0xfedc_ba98),
    ].into_iter().collect());
    assert_eq!(routing::read_child_pair_abi_raw(&fixture, 0x100, 0x0aaa_aaab), Some(0xfedc_ba98_7654_3210));
    assert_eq!(routing::read_child_pair_abi_raw(&fixture, 0x100, 0x8000_0000), Some(0x0123_4567_89ab_cdef));
    assert_eq!(routing::read_child_pair_abi_raw(&fixture, 0x100, u32::MAX), None);
    assert!(PathType(0x8a86_3e32) > PathType(0x7fff_ffff));
    println!("PASS canaries 1-2: native/fixture hops, raw/owned parity, unlock errors, unsigned tags");
}

fn main() {
    route_probe();
    let mut arena = HandleArena::new();
    let mut slots = SlotReservationMap::default();
    let mut seed = 0x5eed_1234u32;
    for iteration in 0..10_000u32 {
        seed ^= seed << 13;
        seed ^= seed >> 17;
        seed ^= seed << 5;
        let limit = iteration.wrapping_mul(0x9e37_79b9);
        let wide = u128::from(seed) * 0x41c6_4e6du128 + 0x3039;
        let expected = (wide as u32, ((((wide >> 16) & 0xffff_ffff) * u128::from(limit)) >> 32) as u32);
        assert_eq!(path_draw_lcg(seed, limit), expected);
        let mut owned = SacsPathRng::new(seed);
        assert_eq!(owned.next_bounded(limit), expected.1);
        assert_eq!(owned.state(), expected.0);
        assert_eq!(random_node_lcg(seed), (u128::from(seed) * 0x278d_de6d) as u32);
        assert_eq!(addr32_offset(seed, 0x18, limit, 4),
            ((u128::from(seed) + u128::from(limit) * 0x18 + 4) & 0xffff_ffff) as u32);
        let handle = arena.insert(seed).unwrap();
        assert_eq!(arena.remove(handle), Ok(seed));
        assert_eq!(arena.get(handle), Err(KernelError::StaleObjectHandle));
        assert_eq!(arena.get_mut(handle), Err(KernelError::StaleObjectHandle));
        let token = SlotReservation { sim: SimId(1), object: ObjectGuid(42), slot: SlotIndex(3),
            policy: LockingPolicy::MustHaveKey, generation: iteration };
        slots.reserve(token).unwrap();
        assert_eq!(slots.reserve(SlotReservation { sim: SimId(2), ..token }), Err(SimId(1)));
        assert_eq!(slots.release(token), Some(SimId(1)));
        assert_eq!(slots.release(token), None);
    }
    assert_eq!(arena.get(GenHandle::from_parts(u32::MAX, 0)), Err(KernelError::UnknownHandle));
    let mut loop_rng = SacsPathRng::new(0x1234_5678);
    assert_eq!(loop_rng.pick_path(&[8, 2, 2], PathPickMode::Shortest, true), Some(1));
    assert_eq!(loop_rng.state(), 0x1234_5678);
    assert_eq!(loop_rng.pick_path(&[8, 2, 2], PathPickMode::Shortest, false), Some(1));
    assert_eq!(loop_rng.state(), 0x0b71_9151);
    println!("PASS canaries 3-5: 10000 independent integer RNG/address vectors, reservation conflicts, stale handles");

    let mut clock = SimClock::default();
    assert_eq!(clock.set_world_time_shortest(3, 12, 0), 189_000);
    assert_eq!((clock.day_of_week(), clock.hour_of(), clock.minute_of()), (3, 12, 0));
    let mut alarms = AlarmManager::new();
    let first = alarms.add_alarm(10, true, 2);
    let second = alarms.add_alarm(10, false, 0);
    assert_eq!(alarms.simulate(100), [first, second]);
    assert_eq!(alarms.simulate(102), [first]);
    let mut authority = ServerHouseholdAuthority::default();
    let mut lot = LotSimulationScope::default();
    lot.refresh_scope(&authority);
    let runs = (0..100_000).filter(|_| lot.should_simulate_this_tick()).count();
    assert_eq!(runs, 3226);
    authority.claimed_sims.insert(SimId(2));
    lot.occupants.insert(SimId(2));
    lot.refresh_scope(&authority);
    assert!((0..30).all(|_| lot.should_simulate_this_tick()));
    println!("PASS canaries 6-7: clock jump, ordered catch-up, 100000-tick throttle, active scope");

    let entry = InteractionInstance {
        id: 1, family: InteractionFamilyTag::SocialInteractionA,
        instance_actor: SimId(1), target_sim: Some(SimId(2)),
        priority: 10, test_passes: true,
    };
    authority.claimed_sims.extend([SimId(1), SimId(2)]);
    let mut queue = InteractionQueue::new();
    assert!(queue.insert(entry, 0, InteractionProducer::ClientAutonomy, &authority).is_err());
    assert!(queue.insert(entry, 0, InteractionProducer::ClientLocalRandom, &authority).is_err());
    queue.insert(entry, 0, InteractionProducer::ServerCommand, &authority).unwrap();
    queue.insert(InteractionInstance { id: 2, instance_actor: SimId(2),
        target_sim: Some(SimId(1)), ..entry }, 1,
        InteractionProducer::ServerCommand, &authority).unwrap();
    queue.claim_swap(SimId(2), SimId(1), &mut authority).unwrap();
    assert!(queue.is_empty());
    for id in [1, 2] {
        assert_eq!(queue.effects().iter().filter(|effect| matches!(effect,
            QueueEffect::Cancelled { id: cancelled, explanation: FailureExplanation::CancelledByScript,
                route_stop: RouteStopEventType::Cancelled, .. } if *cancelled == id)).count(), 1);
        assert_eq!(queue.effects().iter().filter(|effect| matches!(effect,
            QueueEffect::ReservationReleased { id: released } if *released == id)).count(), 1);
        assert_eq!(queue.effects().iter().filter(|effect| matches!(effect,
            QueueEffect::PostureTornDown { id: torn } if *torn == id)).count(), 1);
    }
    let effects_after_swap = queue.effects().len();
    queue.claim_swap(SimId(2), SimId(1), &mut authority).unwrap();
    assert_eq!(queue.effects().len(), effects_after_swap);
    assert_eq!(authority.local_active_actor, None);
    queue.insert(entry, 0, InteractionProducer::ServerCommand, &authority).unwrap();
    queue.insert(InteractionInstance { id: 3, priority: 1, ..entry }, 0,
        InteractionProducer::ServerCommand, &authority).unwrap();
    assert_eq!(queue.entries().len(), 1);
    assert_eq!(queue.head().unwrap().id, 3);
    println!("PASS canary 8: rejected client producers, symmetric swap cleanup effects exactly once, priority preemption");

    let mut motives = MotiveStore { hunger: MotiveValue(500_000), ..Default::default() };
    let buffs = [BuffCommodityDecayModifier { commodity: CommodityKind::Hunger, delta_raw_per_tick: -5 }];
    for _ in 0..100_000 { social_state::tick_motive_decay(&mut motives, &buffs); }
    assert_eq!(motives.hunger.0, 0);
    let relationships = RelationshipMatrix::default();
    let pair = RelationshipPair::new(SimId(2), SimId(1));
    assert_eq!(relationships.ltr_threshold_check(pair, 90, bits::DATING), LongTermRelationshipType::Stranger);
    assert_eq!(relationships.ltr_threshold_check(pair, 20, bits::HAS_MET), LongTermRelationshipType::Acquaintance);
    println!("PASS canary 9: 100000 motive ticks, symmetric keys, HasMet gate");

    let mut ticks = TickLoopState::new();
    let mut counters = JoinCounters::default();
    counters.presentation_sent(0, false);
    counters.join_started();
    std::thread::scope(|scope| {
        let reader = ticks.snapshot_for_join();
        let (ready_tx, ready_rx) = std::sync::mpsc::sync_channel(0);
        let (done_tx, done_rx) = std::sync::mpsc::sync_channel(0);
        let join = scope.spawn(move || {
            ready_tx.send(()).unwrap();
            done_rx.recv_timeout(std::time::Duration::from_secs(10)).unwrap();
            assert_eq!(reader.tick, 0);
            assert!(reader.objects.is_empty());
        });
        ready_rx.recv_timeout(std::time::Duration::from_secs(10)).unwrap();
        for tick in 1..=10_000 {
            ticks.commit_tick(WorldDelta { tick, object_add: Some((42, tick)), object_remove: None });
            counters.presentation_sent(tick, true);
        }
        assert_eq!(ticks.world.tick, 10_000);
        assert_eq!(ticks.world.objects.get(&42), Some(&10_000));
        done_tx.send(()).unwrap();
        join.join().unwrap();
    });
    counters.join_finished();
    assert_eq!(counters.tick_drops_during_join, 0);
    assert_eq!(counters.presentation_sent_gap_max_during_join, 1);
    let mut reuse = JoinReuseState::default();
    let fingerprint = JoinFingerprint { parts: [1, 2, 3] };
    assert!(reuse.on_request_world(&fingerprint).is_none());
    reuse.on_fresh_checkpoint(fingerprint, ticks.snapshot_for_join());
    assert!(reuse.on_request_world(&fingerprint).is_some());
    assert!(reuse.on_request_world(&JoinFingerprint { parts: [1, 2, 4] }).is_none());
    assert_eq!(reuse.counters.misses_by_reason[&JoinMissReason::Relationships], 1);
    let mut protocol = PublicProtocolCodec::new();
    let mut applied = 0;
    assert_eq!(protocol.apply_command(PlayerId(1), u64::MAX, false, || {
        applied += 1; Ok(())
    }), Err(KernelError::TestRejected));
    for _ in 0..2 {
        protocol.apply_command(PlayerId(1), u64::MAX, true, || {
            applied += 1; Ok(())
        }).unwrap();
    }
    assert_eq!(applied, 1);
    println!("PASS canary 10: 10000 concurrent join ticks, measured gaps, fingerprint misses, authority before dedup");
}
