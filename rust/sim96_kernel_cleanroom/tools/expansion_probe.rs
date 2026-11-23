#![forbid(unsafe_code)]
//! Runnable production-API scenario for the H4 expansion, not referee self-tests.
use std::collections::BTreeSet;
use sim96_kernel_cleanroom::{
    SimId, LotId, authority::{HouseholdId, ServerHouseholdAuthority},
    aspirations::{AspirationWish, ClaimedSimAspirationDriver, WishHandle, WishKind, LifetimeHappiness},
    household_drivers::{MultiHouseholdMailboxAndLifeEvents, BillErr, WEEKLY_BILLS_PERIOD_TICKS},
    careers::{CareerAndTransitScheduler, CareerShift},
    npc_social_services::{MultiHouseholdSocialAndServiceRouter, ServiceKind2},
    route_presentation::{BoundaryRoutePresentationGuard, WalkStyle, WalkPhase},
    sacs_rng::SacsPathRng, economy::{FamilyFunds, EconomyReason},
    dialogs::{DialogKind, RemoteModalDialogRouter}, wire::PlayerId,
};
fn main() {
    let mut a = ServerHouseholdAuthority::default();
    for id in 1..=3 {
        a.claimed_sims.insert(SimId(id));
        a.active_households.insert(HouseholdId(id));
        a.members.insert(HouseholdId(id), BTreeSet::from([SimId(id)]));
        a.home_lots.insert(HouseholdId(id), LotId(id * 10));
        a.owners.insert(SimId(id), PlayerId(id + 100));
    }
    let mut aspirations = ClaimedSimAspirationDriver::default();
    let mut rng = SacsPathRng::new(0x8000_0000);
    aspirations.tick(0, &mut rng, &a);
    assert_eq!(aspirations.wishes.len(), 3);
    aspirations.wishes.get_mut(&SimId(3)).unwrap().push(AspirationWish {
        handle: WishHandle(99), sim: SimId(3), kind: WishKind::Lifetime,
        points: 500, locked: false, due_tick: 100 });
    aspirations.lock_wish(SimId(3), WishHandle(99)).unwrap();
    assert_eq!(aspirations.fulfil_wish(SimId(3), WishHandle(99), 1), Ok(LifetimeHappiness(500)));
    assert!(aspirations.fulfil_wish(SimId(3), WishHandle(99), 1).is_err());
    assert_eq!(aspirations.wishes[&SimId(3)].len(), 1);
    assert_eq!(aspirations.happiness[&SimId(1)].0, 0);
    a.local_active_actor = Some(SimId(1));
    aspirations.tick(2, &mut rng, &a);
    assert_eq!(aspirations.happiness[&SimId(3)].0, 500);
    println!("PASS 21: three claimed pools, isolated reward, persistent lifetime wish, no duplicate reward");

    let mut funds = FamilyFunds::default();
    for hh in a.active_households() {
        funds.ledger.insert(hh, 0);
        funds.modify(hh, 1000, EconomyReason::CareerPayment, vec![], 0).unwrap();
    }
    let mut households = MultiHouseholdMailboxAndLifeEvents::default();
    households.bill_rate_basis_points = 100;
    let handles = households.run_weekly_bills(&a, &|h| vec![a.home_lots[&h]], &|_| 50_000, 0);
    assert_eq!(handles.len(), 3);
    households.pay_bill(handles[0], &mut funds, 1).unwrap();
    assert_eq!(households.pay_bill(handles[0], &mut funds, 1), Err(BillErr::AlreadySettled));
    assert_eq!(funds.replay_ledger(), funds.ledger);
    households.advance_delinquency(&mut funds, 4 * WEEKLY_BILLS_PERIOD_TICKS).unwrap();
    assert_eq!(households.repossessions.len(), 2);
    households.queue_household_mail(&a, 1);
    for hh in a.active_households() { assert_eq!(households.deliver_mail(hh, 1), 2); }
    println!("PASS 22: three weekly bills, append-only payment and escalation, six mail deliveries");

    let mut careers = CareerAndTransitScheduler::default();
    careers.authority = a;
    for id in 1..=3 {
        careers.active_shifts.insert(SimId(id),
            vec![CareerShift::new(SimId(id), 1, LotId(99), 10_000, 20_000)]);
        careers.event_ids.insert(SimId(id), vec![42]);
    }
    // Use a separate immutable authority input, as a server tick does.
    let mut authority = std::mem::take(&mut careers.authority);
    assert!(careers.tick_bus_dispatch(&authority, &|h| vec![SimId(h.0)], 9399).is_empty());
    assert_eq!(careers.tick_bus_dispatch(&authority, &|h| vec![SimId(h.0)], 9400).len(), 3);
    assert_eq!(careers.roll_for_event(SimId(3), &mut rng, 9400).event_id, Some(42));
    careers.authority = std::mem::take(&mut authority);
    let mut dialogs = RemoteModalDialogRouter::default();
    careers.dispatch_career_dialog(SimId(3), DialogKind::AcceptCancel, &mut dialogs, 9400);
    assert_eq!(dialogs.outbox[0].owner_player, PlayerId(103));
    assert!(dialogs.invariant_clock_not_paused());
    assert_eq!(dialogs.tick(11_200).len(), 1);
    println!("PASS 23: exact transit window, seeded event, owner 103 dialog and non-pausing timeout");

    let mut services = MultiHouseholdSocialAndServiceRouter::default();
    for now in 0..6 { services.assign_service(ServiceKind2::Mail, &careers.authority, now).unwrap(); }
    assert_eq!(services.assignments.iter().map(|s| s.household.0).collect::<Vec<_>>(), vec![1,2,3,1,2,3]);
    let guests = services.pick_guests(&careers.authority, &|h| vec![SimId(h.0)], &mut rng);
    services.open_party(HouseholdId(2), guests, &careers.authority, 0, 100).unwrap();
    assert_eq!(services.parties[0].guests, vec![SimId(1),SimId(2),SimId(3)]);
    println!("PASS 24: fair multi-household assignment and deterministic server party");

    let mut route = BoundaryRoutePresentationGuard::new(SimId(3), 2);
    for leg in 0..2 {
        route.start_leg(WalkStyle::FastWalk, leg);
        assert!(route.suppress_idle());
        for now in 0..30 { route.per_tick(now, 9, 0); }
        route.observe_leg_motion(WalkStyle::FastWalk, 9, 9);
    }
    route.observe_walk_offset(300); route.on_rh(500); route.on_posture_line();
    assert!(route.suppress_idle());
    route.observe_walk_offset(500); route.observe_seat_placement(0.08);
    assert_eq!(route.phase, WalkPhase::SeatingSettled);
    assert!(!route.suppress_idle());
    assert_eq!((route.stats.startup_stutter_events, route.stats.pace_mismatch_events, route.stats.end_of_walk_snaps), (0,0,0));
    println!("PASS 25: moving startup, per-leg pace and offset-gated atomic seat handoff");
}
