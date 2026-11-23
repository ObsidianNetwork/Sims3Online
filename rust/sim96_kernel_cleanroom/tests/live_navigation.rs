use sim96_kernel_cleanroom::{SimId, KernelError};
use sim96_kernel_cleanroom::live_navigation::*;
use sim96_kernel_cleanroom::routing_astar::*;

// Hand-authored rectangles, unrelated to captured or inferred library geometry.
fn fixture(rects: &[(i32, i32, i32, i16, u64)]) -> (SimNav, Bindings) {
    let mut nav = SimNav {
        header: NavHeader { format_version: 1, flags: NAV_FLAG_PORTALS_BIDIRECTIONAL | NAV_FLAG_MULTI_LOT,
            lot_id: 123, ..NavHeader::default() }, ..SimNav::default()
    };
    let mut bindings = Bindings { schema_version: 1, epoch: 7, generation: 1,
        provenance: Provenance::Authored, cells: vec![], portals: vec![],
        keys_held: vec![], obstacles: vec![], actors: vec![(1u64 << 40, SimId(9))] };
    for (i, &(x, z, y, level, lot)) in rects.iter().enumerate() {
        nav.rooms.push(NavRoom { room_id: i as u32, level, cell_first: i as u16,
            cell_len: 1, ..NavRoom::default() });
        nav.cells.push(NavCell { cell_id: i as u32, room_id: i as u32,
            vertex_first: nav.vertices.len() as u16, vertex_len: 4,
            centroid_cm: [x + 50, y, z + 50], ..NavCell::default() });
        for (dx, dz) in [(0,0), (0,100), (100,100), (100,0)] {
            nav.vertices.push(NavVertex { x_cm: x+dx, y_cm: y, z_cm: z+dz });
        }
        bindings.cells.push(CellBinding { cell_id: i as u32, lot_id: lot,
            room_id: 100 + i as u64, level });
    }
    (nav, bindings)
}

fn request() -> Request {
    Request { epoch: 7, generation: 1, corr: 11, actor_sim_id: 1u64 << 40,
        kernel_sim: SimId(9), start_xyz_mm: [-500,0,-500], goal_xyz_mm: [500,0,-500],
        start_identity: (10,100,0), goal_identity: (20,101,0), flags: 0 }
}

fn connect(nav: &mut SimNav, b: &mut Bindings, cross_lot: bool) {
    nav.portals.push(NavPortal { portal_id: 0, cell_a: 0, cell_b: 1,
        path_type: PATHTYPE_DOOR_PORTAL, door_id: 77,
        jamb_a_cm: [0,-80], jamb_b_cm: [0,-20], width_cm: 60, ..NavPortal::default() });
    b.portals.push(PortalBinding { portal_id: 0, object_id: 77,
        entry_slot: 4, exit_slot: 5, cross_lot });
}

#[test]
fn no_data_stale_and_atomic_replacement_with_retained_pin() {
    let mut live = LiveNavigation::default();
    assert_eq!(live.current().err(), Some(InputError::NoData));
    let (nav, b) = fixture(&[(-100,-100,0,0,10)]);
    live.invalidate(7, false);
    live.install(&nav.to_bytes(), b.clone()).unwrap();
    let old = live.pin(7,1).unwrap();
    let mut next = b.clone();
    next.generation = 2;
    next.cells[0].cell_id = 9;
    assert_eq!(live.install(&nav.to_bytes(), next), Err(InputError::InvalidBinding));
    assert_eq!(live.pin(7,1).unwrap().content_hash(), old.content_hash());
    let mut replacement = b.clone();
    replacement.generation = 2;
    assert!(live.install(b"not-a-nav", replacement).is_err());
    assert!(live.pin(7,1).is_ok());
    assert_eq!(live.install(&nav.to_bytes(), b.clone()), Err(InputError::StaleGeneration));
    let mut next = b;
    next.generation = 2;
    live.install(&nav.to_bytes(), next).unwrap();
    assert_eq!(live.pin(7,1).err(), Some(InputError::StaleGeneration));
    assert_eq!(old.map_cm([-50,0,-50],10,100,0,true), Ok(0));
    live.invalidate(8,true);
    assert_eq!(live.current().err(), Some(InputError::NoData));
    assert_eq!(old.map_cm([-50,0,-50],10,100,0,true), Ok(0));
}

#[test]
fn live_rejects_authored_and_inferred_even_after_switch_off() {
    let (nav, mut b) = fixture(&[(-100,-100,0,0,10)]);
    let mut live = LiveNavigation::default();
    live.invalidate(7,true);
    assert_eq!(live.install(&nav.to_bytes(), b.clone()), Err(InputError::InvalidBinding));
    b.provenance = Provenance::Inferred;
    assert_eq!(live.install(&nav.to_bytes(), b.clone()), Err(InputError::InvalidBinding));
    b.provenance = Provenance::Measured;
    live.install(&nav.to_bytes(), b).unwrap();
    live.invalidate(7,false);
    assert!(live.is_live());
    assert_eq!(live.current().err(), Some(InputError::NoData));
}

#[test]
fn negative_coordinates_shared_edges_and_stacked_floors() {
    let (nav, mut b) = fixture(&[(-100,-100,0,0,10), (0,-100,0,0,10), (-100,-100,300,1,10)]);
    b.cells[1].room_id = 100;
    let mut live = LiveNavigation::default();
    live.invalidate(7,false);
    live.install(&nav.to_bytes(), b).unwrap();
    let s = live.current().unwrap();
    assert_eq!(s.map_cm([-50,0,-50],10,100,0,true), Ok(0));
    assert_eq!(s.map_cm([0,0,-50],10,100,0,true), Ok(0));
    assert_eq!(s.map_cm([-50,300,-50],10,102,1,true), Ok(2));
    assert_eq!(s.map_cm([-50,0,-50],10,102,1,true),
        Err(InputError::Kernel(KernelError::RouteStartOutOfBounds)));
    assert_eq!(s.map_cm([-101,0,-50],10,100,0,false),
        Err(InputError::Kernel(KernelError::RouteGoalUnreachable)));
    assert_eq!(Snapshot::quantize_mm([-15,-5,15]), [-2,-1,2]);
    assert_eq!(Snapshot::quantize_mm([i32::MIN,0,i32::MAX]), [-214748365,0,214748365]);
}

#[test]
fn overlapping_interiors_fail_instead_of_choosing_nearest() {
    let (nav, mut b) = fixture(&[(-100,-100,0,0,10), (-80,-100,0,0,10)]);
    b.cells[1].room_id = 100;
    let mut live = LiveNavigation::default();
    live.invalidate(7,false);
    live.install(&nav.to_bytes(), b).unwrap();
    assert_eq!(live.current().unwrap().map_cm([-50,0,-50],10,100,0,true),
        Err(InputError::AmbiguousCell));
}

#[test]
fn cross_lot_requires_explicit_physical_connector_and_actor_mapping() {
    let (mut nav, mut b) = fixture(&[(-100,-100,0,0,10), (0,-100,0,0,20)]);
    connect(&mut nav, &mut b, true);
    let mut live = LiveNavigation::default();
    live.invalidate(7,false);
    let mut bad = b.clone();
    bad.portals[0].cross_lot = false;
    assert_eq!(live.install(&nav.to_bytes(), bad), Err(InputError::InvalidBinding));
    live.install(&nav.to_bytes(), b.clone()).unwrap();
    let p = live.plan(request()).unwrap();
    assert_eq!(p.plan().cells, [0,1]);
    assert_eq!(p.snapshot().graph().cell_lots[&1], 20);
    assert!(p.plan().legs.iter().any(|l| l.path_type == PATHTYPE_DOOR_PORTAL && l.door_id == 77));
    assert_eq!(live.validate_pending(&p), Ok(()));
    let mut wrong = request();
    wrong.actor_sim_id = 9; // A truncation of the full-width id is not its mapping.
    assert_eq!(live.plan(wrong).err(), Some(InputError::InvalidBinding));
    let mut stale = request();
    stale.generation = 2;
    assert_eq!(live.plan(stale).err(), Some(InputError::StaleGeneration));
    let mut wrong_floor = request();
    wrong_floor.goal_xyz_mm[1] = 3000;
    assert_eq!(live.plan(wrong_floor).err(), Some(InputError::Kernel(KernelError::RouteGoalUnreachable)));
    b.generation = 2;
    nav.portals[0].jamb_a_cm[0] = 500;
    assert_eq!(live.install(&nav.to_bytes(), b), Err(InputError::InvalidBinding));
    assert!(live.pin(7,1).is_ok());
    live.invalidate(8,true);
    assert_eq!(live.validate_pending(&p), Err(InputError::StaleGeneration));
    assert_eq!(p.snapshot().graph().cell_lots[&1], 20, "running plan retains its snapshot");
}

#[test]
fn cross_lot_gap_does_not_get_a_nearest_neighbour_bridge() {
    let (mut nav, mut b) = fixture(&[(-100,-100,0,0,10), (100,-100,0,0,20)]);
    connect(&mut nav, &mut b, true);
    let mut live = LiveNavigation::default();
    live.invalidate(7,false);
    assert_eq!(live.install(&nav.to_bytes(), b), Err(InputError::InvalidBinding));
}

#[test]
fn supporting_plane_checks_y_and_rejects_nonplanar_replacement() {
    let (mut nav, mut b) = fixture(&[(0,0,0,0,10)]);
    for v in &mut nav.vertices { v.y_cm = v.x_cm; }
    nav.cells[0].centroid_cm[1] = 50;
    let mut live = LiveNavigation::default();
    live.invalidate(7,false);
    live.install(&nav.to_bytes(), b.clone()).unwrap();
    let s = live.current().unwrap();
    assert_eq!(s.map_cm([25,25,50],10,100,0,true), Ok(0));
    assert_eq!(s.map_cm([25,26,50],10,100,0,true),
        Err(InputError::Kernel(KernelError::RouteStartOutOfBounds)));
    nav.vertices[0].y_cm = 4;
    nav.cells[0].centroid_cm[1] = 51;
    b.generation = 2;
    assert_eq!(live.install(&nav.to_bytes(), b), Err(InputError::InvalidBinding));
    assert!(live.pin(7,1).is_ok());
}

#[test]
fn collinear_first_three_vertices_do_not_erase_height_check() {
    let (mut nav, b) = fixture(&[(0,0,0,0,10)]);
    nav.vertices.insert(1, NavVertex { x_cm: 0, y_cm: 0, z_cm: 50 });
    nav.cells[0].vertex_len = 5;
    nav.cells[0].centroid_cm = [40,0,50];
    let mut live = LiveNavigation::default();
    live.invalidate(7,false);
    live.install(&nav.to_bytes(), b).unwrap();
    assert_eq!(live.current().unwrap().map_cm([50,1,50],10,100,0,true),
        Err(InputError::Kernel(KernelError::RouteStartOutOfBounds)));
}

#[test]
fn disconnected_exterior_cells_fail_and_reserved_flags_are_not_ignored() {
    let (nav, b) = fixture(&[(-100,-100,0,0,10), (0,-100,0,0,20)]);
    let mut live = LiveNavigation::default();
    live.invalidate(7,false);
    live.install(&nav.to_bytes(), b).unwrap();
    assert_eq!(live.plan(request()).err(), Some(InputError::Kernel(KernelError::RouteGoalUnreachable)));
    let mut invalid_flags = request();
    invalid_flags.flags = 2;
    assert_eq!(live.plan(invalid_flags).err(), Some(InputError::Kernel(KernelError::NavFormatUnsupported)));
}

#[test]
fn malformed_actor_and_portal_bindings_are_atomic() {
    let (mut nav, mut b) = fixture(&[(-100,-100,0,0,10), (0,-100,0,0,20)]);
    connect(&mut nav, &mut b, true);
    let mut live = LiveNavigation::default();
    live.invalidate(7,false);
    live.install(&nav.to_bytes(), b.clone()).unwrap();
    for case in 0..4 {
        let mut broken = b.clone();
        broken.generation = 2;
        match case {
            0 => broken.actors.push((99,SimId(9))),
            1 => broken.portals[0].object_id = 0,
            2 => broken.schema_version = 2,
            _ => broken.cells[0].level = 1,
        }
        assert_eq!(live.install(&nav.to_bytes(), broken), Err(InputError::InvalidBinding));
        assert!(live.pin(7,1).is_ok());
    }
}

#[test]
fn authority_install_uses_runtime_graph_and_invalidates_only_successful_replacement() {
    use sim96_kernel_cleanroom::runtime_bridge::AuthorityWorld;
    use sim96_kernel_cleanroom::wan_transport::RoleTag;
    use sim96_kernel_cleanroom::wire::PlayerId;
    let (mut nav, mut b) = fixture(&[(-100,-100,0,0,10), (0,-100,0,0,20)]);
    connect(&mut nav, &mut b, true);
    // Authored test geometry exercises the trusted assertion gate only.
    b.provenance = Provenance::Measured;
    let mut world = AuthorityWorld::new();
    world.invalidate_navigation(7,true);
    world.install_navigation(&nav.to_bytes(), b.clone()).unwrap();
    let mut out = vec![];
    world.handle_line(PlayerId(1), RoleTag::Client, "HELLO|1|1|1|17|15|client", &mut out);
    world.handle_line(PlayerId(1), RoleTag::Client, "CMD|2|ClaimSim|9|100", &mut out);
    out.clear();
    world.handle_line(PlayerId(1), RoleTag::Client, "CMD|3|NavigateAStar|9|0|1|0", &mut out);
    assert!(out.iter().any(|s| s.starts_with("Rt|3|9|4|") && s.ends_with("|1250")), "{out:?}");
    let replies = out.clone();
    b.generation = 2;
    assert!(world.install_navigation(b"malformed", b.clone()).is_err());
    out.clear();
    world.handle_line(PlayerId(1), RoleTag::Client, "CMD|3|NavigateAStar|9|0|1|0", &mut out);
    assert_eq!(out, replies, "a failed install preserves the active reply cache");
    world.install_navigation(&nav.to_bytes(), b).unwrap();
    assert!(world.routes.is_empty());
    assert!(world.recent_cmds.is_empty());
    out.clear();
    world.handle_line(PlayerId(1), RoleTag::Client, "CMD|4|NavigateAStar|9|0|1|0", &mut out);
    assert!(out.iter().any(|s| s.starts_with("Rt|4|9|4|")));
}
