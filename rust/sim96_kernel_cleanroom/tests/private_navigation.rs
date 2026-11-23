use sim96_kernel_cleanroom::private_navigation::*;
use sim96_kernel_cleanroom::live_navigation::{Bindings,CellBinding,PortalBinding,Provenance,Request};
use sim96_kernel_cleanroom::routing_astar::*;
use sim96_kernel_cleanroom::runtime_bridge::{AuthorityWorld,AUTHORITY_ROUTES};
use sim96_kernel_cleanroom::{SimId,wire::PlayerId,wan_transport::RoleTag};
use std::io::{BufRead,BufReader,Write};
use std::net::{Ipv4Addr,SocketAddr,TcpStream};
use std::time::Duration;

const ACTOR: u64 = 0xfedc_ba98_7654_3210;

fn fixture() -> (Vec<u8>,Bindings) {
    // Synthetic adjacent rectangles. "Measured" exercises the live input gate;
    // this fixture is authored, not captured geometry or a private oracle.
    let mut nav = SimNav { header:NavHeader { format_version:1,flags:NAV_FLAG_PORTALS_BIDIRECTIONAL,
        lot_id:10,..NavHeader::default() },..SimNav::default() };
    for (i,x) in [(0u16,-100),(1,0)] {
        nav.rooms.push(NavRoom { room_id:u32::from(i),cell_first:i,cell_len:1,..NavRoom::default() });
        nav.cells.push(NavCell { cell_id:u32::from(i),room_id:u32::from(i),vertex_first:i*4,
            vertex_len:4,centroid_cm:[x+50,0,-50],..NavCell::default() });
        for (dx,dz) in [(0,0),(0,100),(100,100),(100,0)] {
            nav.vertices.push(NavVertex { x_cm:x+dx,y_cm:0,z_cm:-100+dz });
        }
    }
    nav.portals.push(NavPortal { portal_id:0,cell_a:0,cell_b:1,path_type:PATHTYPE_DOOR_PORTAL,
        door_id:ACTOR-1,jamb_a_cm:[0,-80],jamb_b_cm:[0,-20],width_cm:60,..NavPortal::default() });
    let bindings = Bindings { schema_version:1,epoch:7,generation:1,provenance:Provenance::Measured,
        cells:vec![CellBinding { cell_id:0,lot_id:10,room_id:100,level:0 },
            CellBinding { cell_id:1,lot_id:10,room_id:101,level:0 }],
        portals:vec![PortalBinding { portal_id:0,object_id:ACTOR-1,entry_slot:4,exit_slot:5,cross_lot:false }],
        keys_held:vec![],obstacles:vec![],actors:vec![(ACTOR,SimId(9))] };
    (nav.to_bytes(),bindings)
}
fn request(corr: u64) -> Request {
    Request { epoch:7,generation:1,corr,actor_sim_id:ACTOR,kernel_sim:SimId(9),
        start_xyz_mm:[-500,0,-500],goal_xyz_mm:[500,0,-500],
        start_identity:(10,100,0),goal_identity:(10,101,0),flags:0 }
}
fn send(world: &mut AuthorityWorld, id: u64, message: Message) -> Vec<Delivery> {
    // Exercise the identical codec boundary, not only typed construction.
    let bytes = Frame { id,message }.encode().unwrap();
    world.private_frame(Frame::decode(&bytes).unwrap());
    let deliveries = world.private_navigation.take_deliveries();
    for delivery in &deliveries { world.private_delivery_succeeded(&delivery.frame); }
    deliveries
}
fn status(delivery: &Delivery) -> Status {
    match &delivery.frame.message {
        Message::Result { status,.. } => *status,
        _ => panic!("expected Result"),
    }
}
fn installed() -> AuthorityWorld {
    let mut world = AuthorityWorld::new();
    world.enable_private_navigation();
    assert_eq!(status(&send(&mut world,1,Message::Invalidate { epoch:7,generation:0 })[0]),Status::Done);
    let (simnav,bindings) = fixture();
    assert_eq!(status(&send(&mut world,2,Message::Install { bindings,simnav })[0]),Status::Done);
    world
}

#[test]
fn card55_e2_counts_only_staged_plans_and_successful_commits() {
    let mut world = installed();
    assert_eq!((world.navigate_requests, world.plans_served), (0, 0));
    assert_eq!(status(&send(&mut world, 3, Message::Plan {
        origin: Origin::Player(999), deadline_tick: 20, request: request(1),
    })[0]), Status::Unauthorized);
    assert_eq!(status(&send(&mut world, 4, Message::Plan {
        origin: Origin::Engine, deadline_tick: 0, request: request(2),
    })[0]), Status::Expired);
    let mut stale = request(3);
    stale.generation = 2;
    assert_eq!(status(&send(&mut world, 5, Message::Plan {
        origin: Origin::Engine, deadline_tick: 20, request: stale,
    })[0]), Status::Stale);
    assert_eq!((world.navigate_requests, world.plans_served), (0, 0));
    assert_eq!(status(&send(&mut world, 6, Message::Plan {
        origin: Origin::Engine, deadline_tick: 20, request: request(4),
    })[0]), Status::Staged);
    assert_eq!((world.navigate_requests, world.plans_served), (1, 0));
    assert!(send(&mut world, 6, Message::Plan {
        origin: Origin::Engine, deadline_tick: 20, request: request(4),
    }).is_empty());
    assert_eq!(status(&send(&mut world, 6, Message::CommitOffer)[0]), Status::Done);
    assert!(send(&mut world, 6, Message::CommitOffer).is_empty());
    assert_eq!((world.navigate_requests, world.plans_served), (1, 1));
    assert_eq!(status(&send(&mut world, 7, Message::Plan {
        origin: Origin::Engine, deadline_tick: 20, request: request(5),
    })[0]), Status::Staged);
    assert_eq!(status(&send(&mut world, 7, Message::Cancel)[0]), Status::Cancelled);
    assert_eq!(status(&send(&mut world, 8, Message::Plan {
        origin: Origin::Engine, deadline_tick: 10, request: request(6),
    })[0]), Status::Staged);
    world.clock.native_tick_cnt = 10;
    assert_eq!(status(&send(&mut world, 8, Message::CommitOffer)[0]), Status::Expired);
    assert_eq!((world.navigate_requests, world.plans_served), (3, 1));
}

#[test]
fn card55_e2_private_sink_and_public_routes_share_read_only_diagnostics() {
    let mut world = installed();
    let mut public = Vec::new();
    world.handle_line(PlayerId(1), RoleTag::Client, "CMD|1|ClaimSim|9|100", &mut public);
    world.handle_line(PlayerId(1), RoleTag::Client, "CMD|2|NavigateAStar|9|0|1|0", &mut public);
    assert!(public.iter().any(|line| line.starts_with("Rt|2|9|")));
    let (tx, rx) = std::sync::mpsc::sync_channel(4);
    world.private_navigation.set_sink(tx);
    world.private_frame(Frame { id: 3, message: Message::Plan {
        origin: Origin::Engine, deadline_tick: 20, request: request(1),
    } });
    let delivery = rx.recv_timeout(Duration::from_secs(1)).unwrap();
    assert_eq!(status(&delivery), Status::Staged);
    world.private_delivery_succeeded(&delivery.frame);
    world.private_frame(Frame { id: 3, message: Message::CommitOffer });
    let delivery = rx.recv_timeout(Duration::from_secs(1)).unwrap();
    assert_eq!(status(&delivery), Status::Done);
    world.private_delivery_succeeded(&delivery.frame);
    assert_eq!((world.navigate_requests, world.plans_served), (2, 2));
    let hash = world.state_hash();
    for _ in 0..2 {
        let mut output = Vec::new();
        world.handle_line(PlayerId(1), RoleTag::Server, "DIAG_STATE|99", &mut output);
        assert!(output[0].ends_with("|navigate_requests=2|plans_served=2|native_oracle_owned=unmeasured"));
        assert_eq!(world.state_hash(), hash);
    }
}

#[test]
fn card55_failed_staged_delivery_is_not_an_accepted_request() {
    let mut world = installed();
    let (tx, _rx) = std::sync::mpsc::sync_channel(0);
    world.private_navigation.set_sink(tx);
    world.private_frame(Frame { id: 3, message: Message::Plan {
        origin: Origin::Engine, deadline_tick: 20, request: request(1),
    } });
    assert!(world.private_navigation.failed());
    assert_eq!((world.navigate_requests, world.plans_served), (0, 0));
}

#[test]
fn card55_e2_failed_offer_delivery_is_not_served() {
    let mut world = installed();
    assert_eq!(status(&send(&mut world, 3, Message::Plan {
        origin: Origin::Engine, deadline_tick: 20, request: request(1),
    })[0]), Status::Staged);
    let (tx, _rx) = std::sync::mpsc::sync_channel(0);
    world.private_navigation.set_sink(tx);
    world.private_frame(Frame { id: 3, message: Message::CommitOffer });
    assert!(world.private_navigation.failed());
    assert!(!world.private_navigation.enabled);
    assert_eq!((world.navigate_requests, world.plans_served), (1, 0));
}

#[test]
fn card55_e2_oversized_committed_envelope_is_not_a_served_plan() {
    let count = 7_000u16;
    let mut nav = SimNav {
        header: NavHeader { format_version: 1, flags: NAV_FLAG_PORTALS_BIDIRECTIONAL,
            lot_id: 10, ..NavHeader::default() },
        rooms: vec![NavRoom { room_id: 0, cell_len: count, ..NavRoom::default() }],
        ..SimNav::default()
    };
    let mut bindings = Bindings {
        schema_version: 1, epoch: 7, generation: 1, provenance: Provenance::Measured,
        cells: vec![], portals: vec![], keys_held: vec![], obstacles: vec![],
        actors: vec![(ACTOR, SimId(9))],
    };
    for i in 0..count {
        let x = i32::from(i) * 100;
        nav.cells.push(NavCell { cell_id: u32::from(i), room_id: 0,
            vertex_first: i * 4, vertex_len: 4, centroid_cm: [x + 50, 0, 50],
            ..NavCell::default() });
        bindings.cells.push(CellBinding { cell_id: u32::from(i), lot_id: 10, room_id: 100, level: 0 });
        for (dx, dz) in [(0, 0), (0, 100), (100, 100), (100, 0)] {
            nav.vertices.push(NavVertex { x_cm: x + dx, y_cm: 0, z_cm: dz });
        }
        if i > 0 {
            nav.portals.push(NavPortal { portal_id: u32::from(i - 1),
                cell_a: u32::from(i - 1), cell_b: u32::from(i), path_type: PATHTYPE_DOOR_PORTAL,
                door_id: u64::from(i), jamb_a_cm: [x, 20], jamb_b_cm: [x, 80], width_cm: 60,
                ..NavPortal::default() });
            bindings.portals.push(PortalBinding { portal_id: u32::from(i - 1), object_id: u64::from(i),
                entry_slot: 4, exit_slot: 5, cross_lot: false });
            bindings.keys_held.push(i);
        }
    }
    let mut world = AuthorityWorld::new();
    world.enable_private_navigation();
    send(&mut world, 1, Message::Invalidate { epoch: 7, generation: 0 });
    assert_eq!(status(&send(&mut world, 2, Message::Install { bindings, simnav: nav.to_bytes() })[0]), Status::Done);
    let mut request = request(1);
    request.start_xyz_mm = [500, 0, 500];
    request.goal_xyz_mm = [(i32::from(count) - 1) * 1_000 + 500, 0, 500];
    request.goal_identity = request.start_identity;
    assert_eq!(status(&send(&mut world, 3, Message::Plan {
        origin: Origin::Engine, deadline_tick: 20, request,
    })[0]), Status::Staged);
    let replies = send(&mut world, 3, Message::CommitOffer);
    assert_eq!(status(&replies[0]), Status::Invalid);
    assert!(replies[0].presenters.is_empty());
    assert_eq!((world.navigate_requests, world.plans_served), (1, 0));
}

fn unhex(s: &str) -> Vec<u8> {
    assert_eq!(s.len() % 2, 0);
    s.as_bytes().as_chunks::<2>().0.iter().map(|c|
        u8::from_str_radix(std::str::from_utf8(c).unwrap(),16).unwrap()).collect()
}
fn vectors(key: &str) -> Vec<Vec<u8>> {
    // Kernel intentionally has no JSON dependency. Read only the fixture's
    // machine-consumed hex values, not its labels or explanatory prose.
    let needle = format!("\"{key}\": \"");
    include_str!("../../../testdata/card55-private-attachment-v1.json")
        .lines().filter_map(|line| line.split_once(&needle)
            .map(|(_,s)| unhex(s.split('"').next().unwrap()))).collect()
}
#[test]
fn authored_cross_language_frames_preserve_bytes_and_full_identity() {
    let rows = vectors("wire_hex");
    assert_eq!(rows.len(),9);
    for bytes in rows {
        let frame = Frame::decode(&bytes).unwrap();
        assert_eq!(frame.encode().unwrap(),bytes);
        let mut input = bytes.as_slice();
        assert_eq!(Frame::read(&mut input).unwrap().unwrap().encode().unwrap(),bytes);
        assert!(Frame::read(&mut input).unwrap().is_none());
        if let Message::Plan { request, .. } = frame.message {
            assert_eq!(request.actor_sim_id,0xfedc_ba98_7654_3210);
            assert_eq!(request.corr,0xfedc_ba98_7654_3210);
            assert_eq!(request.start_xyz_mm,[-15,3000,250]);
            assert_eq!(request.start_identity,(10,100,-1));
        }
    }
}
#[test]
fn execution_vector_preserves_unsigned_path_and_portal_slots() {
    let bytes = vectors("envelope_hex").remove(0);
    let envelope = RustNavigationEnvelope::decode(&bytes).unwrap();
    assert_eq!(envelope.actor,0xfedc_ba98_7654_3210);
    assert_eq!(envelope.legs[0].path_type,0x8a86_3e32);
    assert_eq!(envelope.legs[0].object,0xfedc_ba98_7654_3210);
    assert_eq!((envelope.legs[0].entry_slot,envelope.legs[0].exit_slot),(4,5));
    assert_eq!(envelope.encode().unwrap(),bytes);
}
#[test]
fn parser_rejects_truncation_trailing_unknown_and_oversized_before_allocation() {
    for bytes in vectors("wire_hex") {
        for end in 0..bytes.len() { assert!(Frame::decode(&bytes[..end]).is_err()); }
        let mut trailing = bytes.clone();
        trailing.push(0);
        let length = u32::try_from(trailing.len()-4).unwrap().to_le_bytes();
        trailing[..4].copy_from_slice(&length);
        assert!(Frame::decode(&trailing).is_err());
        for (offset,value) in [(4,2),(6,255),(7,1)] {
            let mut bad = bytes.clone(); bad[offset] = value;
            assert!(Frame::decode(&bad).is_err());
        }
    }
    assert!(Frame::read(&mut u32::MAX.to_le_bytes().as_slice()).is_err());
    let bytes = vectors("envelope_hex").remove(0);
    for end in 0..bytes.len() { assert!(RustNavigationEnvelope::decode(&bytes[..end]).is_err()); }
    for (offset,value) in [(0,2),(60,255),(80,1),(88,5)] {
        let mut bad = bytes.clone(); bad[offset] = value;
        assert!(RustNavigationEnvelope::decode(&bad).is_err());
    }
    let mut count = vectors("wire_hex").remove(2);
    count[37..41].copy_from_slice(&u32::MAX.to_le_bytes());
    assert!(matches!(Frame::decode(&count),Err(CodecError::Oversized)));
    let mut envelope = RustNavigationEnvelope::decode(&bytes).unwrap();
    let template = envelope.legs.remove(0);
    let maximum = (1024*1024-80)/53;
    envelope.legs = (0..maximum).map(|index| ExecutionLeg {
        index:u32::try_from(index).unwrap(),..template.clone()
    }).collect();
    assert!(envelope.encode().unwrap().len() <= 1024*1024);
    envelope.legs.push(ExecutionLeg { index:u32::try_from(maximum).unwrap(),..template });
    assert_eq!(envelope.encode(),Err(CodecError::Oversized));
}

#[test]
fn reattachment_resets_request_delivery_state_without_replaying_old_output() {
    for fail_channel in [false,true] {
        let mut world = installed();
        send(&mut world,30,Message::Plan { origin:Origin::Engine,deadline_tick:10,request:request(100) });
        send(&mut world,30,Message::CommitOffer);
        send(&mut world,40,Message::Plan { origin:Origin::Engine,deadline_tick:10,request:request(101) });
        if fail_channel {
            let (tx,_rx) = std::sync::mpsc::sync_channel(0);
            world.private_navigation.set_sink(tx);
            world.private_frame(Frame { id:50,message:Message::Invalidate { epoch:7,generation:1 } });
            assert!(world.private_navigation.failed());
        }
        world.private_close(true);
        assert!(!world.private_navigation.enabled);
        world.enable_private_navigation();
        assert!(!world.private_navigation.failed());
        assert!(world.private_navigation.take_deliveries().is_empty());
        let fence = send(&mut world,1,Message::Invalidate { epoch:8,generation:1 });
        assert_eq!(fence.len(),1);
        assert_eq!(fence[0].frame.id,1);
        assert_eq!(status(&fence[0]),Status::Done);
        assert!(fence[0].presenters.is_empty());
        assert!(world.private_navigation.enabled);
        assert!(send(&mut world,30,Message::CommitOffer).is_empty());
        assert!(send(&mut world,40,Message::CommitOffer).is_empty());
        assert_eq!(status(&send(&mut world,2,Message::Invalidate { epoch:7,generation:1 })[0]),Status::Stale);
        assert_eq!(status(&send(&mut world,3,Message::Invalidate { epoch:8,generation:0 })[0]),Status::Stale);
        assert!(!world.private_navigation.anchor_actor(ACTOR,SimId(10)));
    }
}

#[test]
fn reattachment_preserves_same_epoch_corr_fence_until_epoch_advances() {
    let mut world = installed();
    send(&mut world,30,Message::Plan { origin:Origin::Engine,deadline_tick:10,request:request(100) });
    world.private_close(true);
    world.enable_private_navigation();
    assert!(world.private_navigation.take_deliveries().is_empty());
    assert_eq!(status(&send(&mut world,1,Message::Invalidate { epoch:7,generation:1 })[0]),Status::Done);
    let (simnav,mut bindings) = fixture();
    bindings.generation = 2;
    assert_eq!(status(&send(&mut world,2,Message::Install { bindings,simnav })[0]),Status::Done);
    let mut req = request(100);
    req.generation = 2;
    assert_eq!(status(&send(&mut world,3,Message::Plan { origin:Origin::Engine,deadline_tick:10,request:req })[0]),Status::Invalid);
    assert_eq!(status(&send(&mut world,4,Message::Invalidate { epoch:8,generation:2 })[0]),Status::Done);
    let (simnav,mut bindings) = fixture();
    bindings.epoch = 8; bindings.generation = 3;
    assert_eq!(status(&send(&mut world,5,Message::Install { bindings,simnav })[0]),Status::Done);
    let mut req = request(1);
    req.epoch = 8; req.generation = 3;
    assert_eq!(status(&send(&mut world,6,Message::Plan { origin:Origin::Engine,deadline_tick:10,request:req })[0]),Status::Staged);
}

#[test]
fn bounded_output_saturation_fails_closed_and_counts_delivery_failure() {
    let mut world = AuthorityWorld::new();
    world.enable_private_navigation();
    let (tx,_rx) = std::sync::mpsc::sync_channel(0);
    world.private_navigation.set_sink(tx);
    world.private_frame(Frame { id:1,message:Message::Invalidate { epoch:7,generation:0 } });
    assert!(!world.private_navigation.enabled);
    assert_eq!(world.private_navigation.counters.accepted,1);
    assert_eq!(world.private_navigation.counters.terminal,1);
    assert_eq!(world.private_navigation.counters.delivery_failed,1);
    let (bytes,bindings) = fixture();
    assert!(world.install_navigation(&bytes,bindings).is_ok());
    world.private_frame(Frame { id:2,message:Message::Plan {
        origin:Origin::Engine,deadline_tick:10,request:request(1) } });
    assert_eq!(world.private_navigation.counters.accepted,1);
    assert!(world.private_navigation.take_deliveries().is_empty());
}

#[test]
fn engine_offer_preserves_boundary_hash_and_reverses_authoritative_slots_without_claim() {
    let mut world = installed();
    let mut req = request(ACTOR);
    std::mem::swap(&mut req.start_xyz_mm,&mut req.goal_xyz_mm);
    std::mem::swap(&mut req.start_identity,&mut req.goal_identity);
    let expected = world.plan_navigation(request(123)).unwrap();
    let hash = NavGraph::plan_hash(expected.plan());
    assert_eq!(status(&send(&mut world,3,Message::Plan { origin:Origin::Engine,deadline_tick:10,request:req })[0]),Status::Staged);
    let replies = send(&mut world,3,Message::CommitOffer);
    assert_eq!(status(&replies[0]),Status::Done);
    let envelope = match &replies[0].frame.message {
        Message::Result { data,.. } => RustNavigationEnvelope::decode(data).unwrap(),
        _ => panic!("missing envelope"),
    };
    assert_eq!(envelope.actor,ACTOR);
    assert_eq!(envelope.corr,ACTOR);
    assert_eq!(envelope.legs.len(),4);
    assert_eq!(envelope.legs.iter().map(|l| l.kind).collect::<Vec<_>>(),[1,2,3,4]);
    assert_eq!((envelope.legs[1].entry_slot,envelope.legs[1].exit_slot),(5,4));
    assert_eq!(envelope.legs[1].object,ACTOR-1);
    assert_eq!(envelope.legs[1].path_type,PATHTYPE_DOOR_PORTAL);
    // Direction changes the sealed hash; retain the actual K reverse plan hash.
    let mut reverse = request(124);
    std::mem::swap(&mut reverse.start_xyz_mm,&mut reverse.goal_xyz_mm);
    std::mem::swap(&mut reverse.start_identity,&mut reverse.goal_identity);
    let reverse = world.plan_navigation(reverse).unwrap();
    assert_eq!(envelope.hash,NavGraph::plan_hash(reverse.plan()));
    assert_ne!(envelope.hash,hash);
    assert_eq!(replies[0].presenters[0],format!("Rt|{ACTOR}|{ACTOR}|4|{:016x}|{}",envelope.hash,envelope.total_cost_mm));
    assert!(replies[0].presenters[1..].iter().all(|s| s.split('|').count() == 9));
    assert!(world.authority.owners.is_empty());
    assert!(send(&mut world,3,Message::CommitOffer).is_empty());
    let fence = send(&mut world,4,Message::Invalidate { epoch:8,generation:1 });
    assert!(matches!(fence[0].frame.message,Message::Cancel));
    assert_eq!(fence[0].frame.id,3);
    assert_eq!(status(&fence[1]),Status::Done);
}

#[test]
fn invalidation_ownership_disconnect_and_deadline_prevent_late_offer() {
    let mut world = installed();
    world.private_navigation.actor_join(66,PlayerId(1));
    assert_eq!(status(&send(&mut world,3,Message::Plan { origin:Origin::Player(66),
        deadline_tick:20,request:request(100) })[0]),Status::Unauthorized);
    let mut public = Vec::new();
    world.handle_line(PlayerId(1),RoleTag::Client,"CMD|1|ClaimSim|9|100",&mut public);
    assert!(public.iter().any(|s| s.starts_with("ACK|1|SimClaimed|9|")));
    assert_eq!(status(&send(&mut world,4,Message::Plan { origin:Origin::Player(66),
        deadline_tick:20,request:request(101) })[0]),Status::Staged);
    // The real world command boundary cancels the plan on successful claim swap.
    world.handle_line(PlayerId(1),RoleTag::Client,"CMD|2|SwapClaim|9|10",&mut public);
    let cancelled = world.private_navigation.take_deliveries();
    assert_eq!(status(&cancelled[0]),Status::Cancelled);
    assert!(send(&mut world,4,Message::CommitOffer).is_empty());
    assert_eq!(status(&send(&mut world,5,Message::Plan { origin:Origin::Engine,
        deadline_tick:1,request:request(102) })[0]),Status::Staged);
    world.tick();
    assert_eq!(status(&world.private_navigation.take_deliveries()[0]),Status::Expired);
    assert!(send(&mut world,5,Message::CommitOffer).is_empty());
    assert_eq!(status(&send(&mut world,6,Message::Plan { origin:Origin::Engine,
        deadline_tick:20,request:request(103) })[0]),Status::Staged);
    let fence = send(&mut world,7,Message::Invalidate { epoch:8,generation:1 });
    assert_eq!(fence.iter().map(status).collect::<Vec<_>>(),[Status::Cancelled,Status::Done]);
    assert!(send(&mut world,6,Message::CommitOffer).is_empty());
    assert_eq!(status(&send(&mut world,8,Message::Plan { origin:Origin::Engine,
        deadline_tick:20,request:request(104) })[0]),Status::Stale);
    assert_eq!(world.private_navigation.counters.accepted,world.private_navigation.counters.terminal);
}

#[test]
fn saturation_cancel_and_eof_finish_each_accepted_request_once() {
    let mut world = installed();
    for n in 0u64..256 {
        assert_eq!(status(&send(&mut world,n+3,Message::Plan { origin:Origin::Engine,
            deadline_tick:1000,request:request(n+1) })[0]),Status::Staged);
    }
    assert_eq!(status(&send(&mut world,259,Message::Plan { origin:Origin::Engine,
        deadline_tick:1000,request:request(257) })[0]),Status::Saturated);
    assert_eq!((world.navigate_requests, world.plans_served), (256, 0));
    assert_eq!(status(&send(&mut world,3,Message::Cancel)[0]),Status::Cancelled);
    assert!(send(&mut world,3,Message::Cancel).is_empty());
    world.private_close(true);
    let terminal = world.private_navigation.take_deliveries();
    assert_eq!(terminal.len(),255);
    assert!(terminal.iter().all(|d| status(d) == Status::TransportClosed));
    assert_eq!(world.private_navigation.counters.accepted,259);
    assert_eq!(world.private_navigation.counters.terminal,259);
    assert_eq!(world.private_navigation.counters.eof,1);
    assert_eq!(world.private_navigation.counters.saturation,1);
    let mut out = Vec::new();
    world.handle_line(PlayerId(1),RoleTag::Client,"CMD|1|NavigateAStar|9|0|1|0",&mut out);
    assert!(out.iter().all(|line| !line.starts_with("Rt|")));
    assert_eq!(AUTHORITY_ROUTES.len(),30);
}

struct ChildAttachment {
    child: std::process::Child,
    input: Option<std::process::ChildStdin>,
    output: std::sync::mpsc::Receiver<Option<Frame>>,
    port: u16,
}
impl ChildAttachment {
    fn start() -> Self {
        let mut child = std::process::Command::new(env!("CARGO_BIN_EXE_simlink_authority_daemon"))
            .args(["--private-navigation-stdio","--port","0"])
            .stdin(std::process::Stdio::piped()).stdout(std::process::Stdio::piped())
            .stderr(std::process::Stdio::inherit()).spawn().unwrap();
        let input = child.stdin.take().unwrap();
        let mut output = child.stdout.take().unwrap();
        let (tx,rx) = std::sync::mpsc::channel();
        std::thread::spawn(move || {
            loop {
                let frame = Frame::read(&mut output).unwrap();
                let done = frame.is_none();
                if tx.send(frame).is_err() || done { break; }
            }
        });
        let ready = rx.recv_timeout(Duration::from_secs(10)).unwrap().unwrap();
        let port = match ready.message { Message::Ready { port,pid } => { assert_eq!(pid,child.id()); port }
            _ => panic!("stdout did not begin with binary Ready") };
        Self { child,input:Some(input),output:rx,port }
    }
    fn send(&mut self, id: u64, message: Message) -> Frame {
        Frame { id,message }.write(self.input.as_mut().unwrap()).unwrap();
        self.output.recv_timeout(Duration::from_secs(10)).unwrap().unwrap()
    }
}
impl Drop for ChildAttachment {
    fn drop(&mut self) {
        self.input.take();
        if let Err(e) = self.child.kill() { eprintln!("child cleanup: {e}"); }
        if let Err(e) = self.child.wait() { eprintln!("child reap: {e}"); }
    }
}
fn read_until(socket: &mut BufReader<TcpStream>, prefix: &str) -> String {
    let deadline = std::time::Instant::now() + Duration::from_secs(10);
    loop {
        assert!(std::time::Instant::now() < deadline,"deadline before {prefix}");
        let mut line = String::new();
        assert_ne!(socket.read_line(&mut line).unwrap(),0,"socket ended before {prefix}");
        if line.starts_with(prefix) { return line.trim_end().to_owned(); }
    }
}

#[test]
fn inherited_pipes_bind_real_socket_and_share_claim_plan_and_presenter_world() {
    let mut attachment = ChildAttachment::start();
    let mut tcp = TcpStream::connect((Ipv4Addr::LOCALHOST,attachment.port)).unwrap();
    tcp.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
    let mut peer = BufReader::new(tcp.try_clone().unwrap());
    // Reader and bounded deadline exist before sending the handshake.
    writeln!(tcp,"HELLO|1|1|1|17|15|client").unwrap();
    read_until(&mut peer,"WELCOME|");
    let mut tcp2 = TcpStream::connect((Ipv4Addr::LOCALHOST,attachment.port)).unwrap();
    tcp2.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
    let mut peer2 = BufReader::new(tcp2.try_clone().unwrap());
    writeln!(tcp2,"HELLO|1|1|1|17|15|client2").unwrap();
    read_until(&mut peer2,"WELCOME|");
    let local = match tcp.local_addr().unwrap() { SocketAddr::V4(v) => v,_ => unreachable!() };
    let binding = SessionBinding { session:ACTOR,client_ip:local.ip().octets(),client_port:local.port(),
        server_ip:[127,0,0,1],server_port:attachment.port };
    let mut guessed = binding; guessed.client_port = 0;
    let refusal = attachment.send(1,Message::BindSession(guessed));
    assert!(matches!(refusal.message,Message::Result { status:Status::Unauthorized,.. }));
    assert!(matches!(attachment.send(2,Message::BindSession(binding)).message,Message::Result { status:Status::Done,.. }));
    assert!(matches!(attachment.send(3,Message::Invalidate { epoch:7,generation:0 }).message,Message::Result { status:Status::Done,.. }));
    let (simnav,bindings) = fixture();
    assert!(matches!(attachment.send(4,Message::Install { bindings,simnav }).message,Message::Result { status:Status::Done,.. }));
    writeln!(tcp,"CMD|50|ClaimSim|9|100").unwrap();
    read_until(&mut peer,"ACK|50|SimClaimed|9|");
    let staged = attachment.send(5,Message::Plan { origin:Origin::Player(ACTOR),
        deadline_tick:u64::MAX,request:request(ACTOR) });
    assert!(matches!(staged.message,Message::Result { status:Status::Staged,.. }));
    let offer = attachment.send(5,Message::CommitOffer);
    let envelope = match offer.message { Message::Result { data,status:Status::Done,.. } =>
        RustNavigationEnvelope::decode(&data).unwrap(),_ => panic!("no committed offer") };
    assert_eq!(envelope.actor,ACTOR);
    assert_eq!(envelope.legs.len(),4);
    assert_eq!(read_until(&mut peer,"Rt|"),format!("Rt|{ACTOR}|{ACTOR}|4|{:016x}|{}",envelope.hash,envelope.total_cost_mm));
    for index in 0..4 { assert!(read_until(&mut peer,"Vp|rt|").starts_with(&format!("Vp|rt|{ACTOR}|{ACTOR}|{index}|"))); }
    assert_eq!(read_until(&mut peer2,"Rt|"),format!("Rt|{ACTOR}|{ACTOR}|4|{:016x}|{}",envelope.hash,envelope.total_cost_mm));
    for index in 0..4 { assert!(read_until(&mut peer2,"Vp|rt|").starts_with(&format!("Vp|rt|{ACTOR}|{ACTOR}|{index}|"))); }
    // Public installation never becomes an authority route.
    writeln!(tcp,"CMD|51|Install|00").unwrap();
    assert_eq!(read_until(&mut peer,"ERR|51|"),"ERR|51|bad_line|Install");
    let staged = attachment.send(6,Message::Plan { origin:Origin::Player(ACTOR),
        deadline_tick:u64::MAX,request:request(ACTOR+1) });
    assert!(matches!(staged.message,Message::Result { status:Status::Staged,.. }));
    // Half-close and consume the server writer's EOF. Its disconnect hook runs
    // before that EOF, giving an exact barrier instead of a timing guess.
    tcp.shutdown(std::net::Shutdown::Write).unwrap();
    let deadline = std::time::Instant::now() + Duration::from_secs(10);
    loop {
        assert!(std::time::Instant::now() < deadline,"server did not retire the half-closed socket");
        let mut line = String::new();
        if peer.read_line(&mut line).unwrap() == 0 { break; }
    }
    let closed = attachment.output.recv_timeout(Duration::from_secs(10)).unwrap().unwrap();
    assert_eq!(closed.id,6);
    assert!(matches!(closed.message,Message::Result { status:Status::TransportClosed,.. }));
    let revoked = attachment.output.recv_timeout(Duration::from_secs(10)).unwrap().unwrap();
    assert_eq!(revoked.id,5);
    assert!(matches!(revoked.message,Message::Cancel));
    assert!(matches!(attachment.send(7,Message::BindSession(binding)).message,
        Message::Result { status:Status::Unauthorized,.. }));
    assert!(matches!(attachment.send(8,Message::Plan { origin:Origin::Engine,
        deadline_tick:u64::MAX,request:request(ACTOR+2) }).message,
        Message::Result { status:Status::Staged,.. }));
    attachment.input.take(); // Actual inherited EOF, no fabricated callback.
    let closed = attachment.output.recv_timeout(Duration::from_secs(10)).unwrap().unwrap();
    assert_eq!(closed.id,8);
    assert!(matches!(closed.message,Message::Result { status:Status::TransportClosed,.. }));
    assert!(attachment.output.recv_timeout(Duration::from_secs(10)).unwrap().is_none());
    assert!(attachment.child.wait().unwrap().success());
}
