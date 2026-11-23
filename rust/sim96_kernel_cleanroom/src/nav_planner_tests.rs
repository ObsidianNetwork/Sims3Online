//! Unit and property tests for `nav_planner`. Synthetic fixtures only: no
//! measured SIMNAV, bindings or route coordinates.

use super::*;
use crate::routing_astar::{NavCell, CELL_NO_SPAWN, CELL_OUT_OF_BOUNDS, NAV_CELL_CM,
    PATHTYPE_HIGH_BIT, PATHTYPE_VEHICLE_WITNESS, STEP_DIAG_MM, STEP_STRAIGHT_MM};

struct SplitMix64(u64);

impl SplitMix64 {
    fn next(&mut self) -> u64 {
        self.0 = self.0.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.0;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    }
    fn below(&mut self, n: u32) -> u32 { (self.next() % u64::from(n.max(1))) as u32 }
}

const SIM: SimId = SimId(7);
static NONE: FootprintBlocker = FootprintBlocker { blocked_cells: BTreeSet::new() };

/// A `cols x rows` 8-connected grid built straight into the adjacency (the
/// sealed `add_grid` re-sorts every list per edge, too slow for big fixtures).
fn fast_grid(cols: u32, rows: u32) -> NavGraph {
    let mut g = NavGraph::new();
    for r in 0..rows {
        for c in 0..cols {
            g.add_cell(0, NavCell::at(r * cols + c, 0,
                (c as i32 * NAV_CELL_CM, 0, r as i32 * NAV_CELL_CM), 0));
        }
    }
    let mut portal_id = 0u32;
    for r in 0..rows {
        for c in 0..cols {
            let id = r * cols + c;
            for (dr, dc, cost) in [(0i32, 1i32, STEP_STRAIGHT_MM), (1, 0, STEP_STRAIGHT_MM),
                (1, 1, STEP_DIAG_MM), (1, -1, STEP_DIAG_MM)] {
                let (nr, nc) = (r as i32 + dr, c as i32 + dc);
                if nr < 0 || nc < 0 || nr >= rows as i32 || nc >= cols as i32 { continue; }
                let to = nr as u32 * cols + nc as u32;
                let e = PortalEdge { portal_id, from_cell: id, to_cell: to,
                    base_cost_mm: u64::from(cost), ..PortalEdge::default() };
                g.adjacency.entry(id).or_default().push(e);
                g.adjacency.entry(to).or_default()
                    .push(PortalEdge { from_cell: to, to_cell: id, ..e });
                portal_id += 1;
            }
        }
    }
    for list in g.adjacency.values_mut() { list.sort_by_key(|e| (e.portal_id, e.to_cell)); }
    g
}

fn set_flags(g: &mut NavGraph, cell: u32, flags: u32) {
    g.cells.get_mut(&cell).expect("cell").flags = flags;
}

/// One random small graph: a grid with random cell flags, plus door portals
/// (some locked, some high-bit) and over-priced shortcuts.
fn random_graph(rng: &mut SplitMix64) -> (NavGraph, FootprintBlocker, u32) {
    let (cols, rows) = (2 + rng.below(6), 1 + rng.below(6));
    let mut g = NavGraph::new();
    g.add_grid(0, 0, 0, cols, rows, (0, 0));
    let n = cols * rows;
    for id in 0..n {
        let flags = match rng.below(20) {
            0 | 1 => CELL_OUT_OF_BOUNDS,
            2 | 3 => CELL_TERRAIN_WATER,
            4 => CELL_NO_SPAWN,
            _ => 0,
        };
        set_flags(&mut g, id, flags);
    }
    let path_types = [0u32, PATHTYPE_HIGH_BIT, PATHTYPE_VEHICLE_WITNESS, 1];
    for _ in 0..rng.below(5) {
        let (a, b) = (rng.below(n), rng.below(n));
        if a == b { continue; }
        let dist = ast_octile_mm(g.cells[&a].centroid(), g.cells[&b].centroid());
        let center = (rng.below(2) == 0).then(|| {
            let (p, q) = (g.cells[&a].centroid(), g.cells[&b].centroid());
            ((p.0 + q.0) / 2 + 3, (p.1 + q.1) / 2, (p.2 + q.2) / 2 - 2)
        });
        g.add_edge(PortalEdge { from_cell: a, to_cell: b,
            path_type: path_types[rng.below(4) as usize], door_guid: u64::from(a * 1000 + b + 1),
            locked: rng.below(3) == 0, base_cost_mm: dist + u64::from(rng.below(700)),
            center_cm: center, ..PortalEdge::default() });
    }
    // Lock a few grid edges too (high-bit doors in place of plain steps).
    for _ in 0..rng.below(3) {
        let a = rng.below(n);
        if let Some(e) = g.adjacency.get(&a).and_then(|l| l.first()).copied() {
            for (u, v) in [(e.from_cell, e.to_cell), (e.to_cell, e.from_cell)] {
                if let Some(list) = g.adjacency.get_mut(&u) {
                    list.retain(|x| !(x.to_cell == v && x.path_type == e.path_type && x.door_guid == e.door_guid));
                }
            }
            g.add_edge(PortalEdge { path_type: PATHTYPE_HIGH_BIT, door_guid: 77,
                locked: rng.below(2) == 0, ..e });
        }
    }
    let mut blocked = FootprintBlocker::default();
    for id in 0..n {
        if rng.below(9) == 0 { blocked.blocked_cells.insert(id); }
    }
    let flags = if rng.below(2) == 0 { 0 } else { FLAG_ALLOW_WATER };
    (g, blocked, flags)
}

fn unlimited(p: &Planner, start: u32, goal: &Goal) -> Result<PlannedRoute, PlanError> {
    p.plan(SIM, start, goal, Budget::UNLIMITED, &MonotonicClock::new())
}

// ---------------------------------------------------------------------------
// Differential against the sealed planner
// ---------------------------------------------------------------------------

#[test]
fn differential_cell_goal_equals_sealed_planner_on_random_graphs() {
    let mut rng = SplitMix64(0x0A11_CE55_0055_0001);
    let (mut ok, mut refused, mut locked) = (0u32, 0u32, 0u32);
    for _ in 0..4_000 {
        let (g, blocked, flags) = random_graph(&mut rng);
        let planner = Planner::new(&g, &blocked, flags).expect("valid graph");
        let n = u32::try_from(g.cells.len()).unwrap();
        for _ in 0..4 {
            let (start, goal) = (rng.below(n + 1), rng.below(n + 1));
            let sealed = g.plan_route_with(start, goal, SIM, &blocked, flags);
            let ours = unlimited(&planner, start, &Goal::cell(goal));
            match (&sealed, &ours) {
                (Ok(a), Ok(b)) => {
                    assert_eq!(a, &b.plan, "plan differs: start {start} goal {goal}");
                    assert_eq!(b.score_mm, a.total_cost_mm);
                    ok += 1;
                }
                (Err(a), Err(PlanError::Kernel(b))) => {
                    assert_eq!(a, b, "refusal differs: start {start} goal {goal}");
                    refused += 1;
                    if *a == KernelError::PortalLocked { locked += 1; }
                }
                _ => panic!("sealed {sealed:?} vs ours {ours:?} at start {start} goal {goal}"),
            }
            // The gate alone agrees with the sealed verdict, and the search
            // without the gate reaches the same answer.
            let gate = planner.gate(start, &Goal::cell(goal)).unwrap();
            assert_eq!(gate.refusal(), sealed.as_ref().err().copied());
            let ungated = planner.plan_ungated(SIM, start, &Goal::cell(goal), Budget::UNLIMITED,
                &MonotonicClock::new());
            assert_eq!(ungated.map(|r| r.plan), ours.map(|r| r.plan));
        }
    }
    println!("NAVPLAN|case=differential|ok={ok}|refused={refused}|locked={locked}");
    assert!(ok > 3_000 && refused > 3_000 && locked > 20, "coverage ok={ok} refused={refused} locked={locked}");
}

#[test]
fn differential_sealed_fixtures() {
    // Doors with an explicit centre, a locked door and the held-key variant.
    let mut g = NavGraph::new();
    g.add_grid(0, 0, 0, 3, 1, (0, 0));
    g.add_grid(0, 1, 10, 3, 1, (500, 0));
    g.add_edge(PortalEdge { from_cell: 2, to_cell: 10, path_type: PATHTYPE_VEHICLE_WITNESS,
        door_guid: 9, base_cost_mm: 3_000, center_cm: Some((350, 0, 0)), ..PortalEdge::default() });
    let p = Planner::new(&g, &NONE, 0).unwrap();
    let ours = unlimited(&p, 0, &Goal::cell(12)).unwrap();
    assert_eq!(ours.plan, g.plan_route(0, 12, SIM).unwrap());
    assert_eq!(ours.plan.door_crossings.len(), 1);
    g.add_edge(PortalEdge { from_cell: 2, to_cell: 10, path_type: PATHTYPE_VEHICLE_WITNESS,
        door_guid: 9, base_cost_mm: 3_000, locked: true, center_cm: Some((350, 0, 0)),
        ..PortalEdge::default() });
    let p = Planner::new(&g, &NONE, 0).unwrap();
    assert_eq!(unlimited(&p, 0, &Goal::cell(12)), Err(PlanError::Kernel(KernelError::PortalLocked)));
    assert_eq!(g.plan_route(0, 12, SIM), Err(KernelError::PortalLocked));
    assert_eq!(p.gate(0, &Goal::cell(12)), Ok(Gate::PortalLocked));
}

#[test]
fn planner_refuses_what_the_sealed_planner_refuses() {
    let mut g = fast_grid(2, 1);
    assert_eq!(Planner::new(&g, &NONE, 2).err(), Some(KernelError::NavFormatUnsupported));
    g.adjacency.get_mut(&0).unwrap()[0].base_cost_mm = 10;
    assert_eq!(Planner::new(&g, &NONE, 0).err(), Some(KernelError::NavFormatUnsupported));
}

// ---------------------------------------------------------------------------
// Point goals
// ---------------------------------------------------------------------------

#[test]
fn point_goal_ends_exactly_at_the_point() {
    let g = fast_grid(5, 5);
    let p = Planner::new(&g, &NONE, 0).unwrap();
    let cell = 4 * 5 + 3; // centroid (300, 0, 400)
    let point = [330, 0, 380];
    let r = unlimited(&p, 0, &Goal::point(cell, point)).unwrap();
    let sealed = g.plan_route(0, cell, SIM).unwrap();
    let leg = *r.plan.legs.last().unwrap();
    assert_eq!((leg.kind, leg.from_cm, leg.to_cm), (LegKind::SubPath, [300, 0, 400], point));
    assert_eq!(leg.cost_mm, ast_octile_mm((300, 0, 400), (330, 0, 380)));
    assert_eq!(r.plan.total_cost_mm, sealed.total_cost_mm + leg.cost_mm);
    assert_eq!(r.plan.legs.len(), sealed.legs.len() + 1);
    assert_eq!(r.plan.legs[..sealed.legs.len()], sealed.legs[..]);
    assert_eq!(r.plan.path_hash, route_path_hash(&r.plan.legs));
    assert_eq!(r.plan.legs.iter().map(|l| l.cost_mm).sum::<u64>(), r.plan.total_cost_mm);
    assert_eq!(r.arrival.point_cm, point);
    // A point at the centroid is the cell goal.
    assert_eq!(unlimited(&p, 0, &Goal::point(cell, [300, 0, 400])).unwrap().plan, sealed);
}

#[test]
fn point_leg_in_water_is_scaled_like_an_edge() {
    let mut g = fast_grid(3, 1);
    set_flags(&mut g, 2, CELL_TERRAIN_WATER);
    let p = Planner::new(&g, &NONE, FLAG_ALLOW_WATER).unwrap();
    let r = unlimited(&p, 0, &Goal::point(2, [240, 0, 0])).unwrap();
    assert_eq!(r.plan.legs.last().unwrap().cost_mm, 800);
}

#[test]
fn several_points_pick_the_cheapest_with_penalty() {
    let g = fast_grid(8, 1);
    let p = Planner::new(&g, &NONE, 0).unwrap();
    // Near target with a big penalty loses to a far one with none.
    let goal = Goal::points(&[(2, [200, 0, 10], 5_000), (6, [600, 0, 0], 0)]);
    let r = unlimited(&p, 0, &goal).unwrap();
    assert_eq!((r.target, r.plan.goal_cell, r.score_mm), (1, 6, 6_000));
    let goal = Goal::points(&[(2, [200, 0, 10], 100), (6, [600, 0, 0], 0)]);
    let r = unlimited(&p, 0, &goal).unwrap();
    assert_eq!((r.target, r.plan.goal_cell), (0, 2));
    assert_eq!(r.score_mm, 2_000 + 100 + 100);
}

#[test]
fn multi_target_equals_the_best_single_target_on_random_graphs() {
    let mut rng = SplitMix64(0x0A11_CE55_0055_0002);
    let mut checked = 0;
    for _ in 0..2_000 {
        let (g, blocked, flags) = random_graph(&mut rng);
        let p = Planner::new(&g, &blocked, flags).unwrap();
        let n = u32::try_from(g.cells.len()).unwrap();
        let start = rng.below(n);
        let k = 1 + rng.below(4);
        let points: Vec<(u32, [i32; 3], u64)> = (0..k).map(|_| {
            let cell = rng.below(n);
            let c = g.cells[&cell].centroid_cm;
            let off = |rng: &mut SplitMix64| rng.below(99) as i32 - 49;
            (cell, [c[0] + off(&mut rng), c[1], c[2] + off(&mut rng)], u64::from(rng.below(3)) * 400)
        }).collect();
        let ours = unlimited(&p, start, &Goal::points(&points));
        let best = points.iter().filter_map(|(cell, pt, pen)| {
            let single = unlimited(&p, start, &Goal::point(*cell, *pt)).ok()?;
            assert_eq!(single.plan.goal_cell, *cell);
            Some(single.score_mm + pen)
        }).min();
        match (best, &ours) {
            (Some(b), Ok(r)) => {
                assert_eq!(r.score_mm, b);
                assert_eq!(r.plan.legs.iter().map(|l| l.cost_mm).sum::<u64>(), r.plan.total_cost_mm);
                checked += 1;
            }
            (None, Err(PlanError::Kernel(_))) => {}
            _ => panic!("best {best:?} vs ours {ours:?}"),
        }
        assert_eq!(p.gate(start, &Goal::points(&points)).unwrap() == Gate::Reachable, ours.is_ok());
    }
    assert!(checked > 1_000, "checked {checked}");
}

// ---------------------------------------------------------------------------
// Slot goals
// ---------------------------------------------------------------------------

#[test]
fn object_frame_rotates_with_integer_rounding() {
    let identity = ObjectFrame { origin_cm: [100, 5, 200], forward: [0, 1] };
    assert_eq!(identity.to_world([30, 0, 40]), Some([130, 5, 240]));
    let east = ObjectFrame { origin_cm: [0, 0, 0], forward: [1, 0] };
    assert_eq!(east.to_world([0, 0, 50]), Some([50, 0, 0]));
    assert_eq!(east.to_world([50, 0, 0]), Some([0, 0, -50]));
    let back = ObjectFrame { origin_cm: [0, 0, 0], forward: [0, -7] };
    assert_eq!(back.to_world([10, 0, 20]), Some([-10, 0, -20]));
    // Length does not matter; a diagonal rounds halves away from zero.
    let long = ObjectFrame { origin_cm: [0, 0, 0], forward: [0, 1_000] };
    assert_eq!(long.to_world([10, 0, 20]), identity.to_world([10, 0, 20]).map(|p| [p[0] - 100, 0, p[2] - 200]));
    let diag = ObjectFrame { origin_cm: [0, 0, 0], forward: [3, 4] };
    assert_eq!(diag.rotate([0, 5]), Some([3, 4]));
    assert_eq!(diag.rotate([5, 0]), Some([4, -3]));
    assert_eq!(ObjectFrame { origin_cm: [0; 3], forward: [0, 0] }.rotate([1, 1]), None);
}

#[test]
fn object_frame_rotation_preserves_length_property() {
    let mut rng = SplitMix64(0x0A11_CE55_0055_0003);
    for _ in 0..20_000 {
        let f = [rng.below(2001) as i32 - 1000, rng.below(2001) as i32 - 1000];
        if f == [0, 0] { continue; }
        let frame = ObjectFrame { origin_cm: [0; 3], forward: f };
        let v = [rng.below(401) as i32 - 200, rng.below(401) as i32 - 200];
        let w = frame.rotate(v).unwrap();
        let len2 = |a: [i32; 2]| f64::from(a[0]).hypot(f64::from(a[1]));
        // Per-axis rounding only: within 1 cm (plus 0.01% for the fixed-point length).
        assert!((len2(w) - len2(v)).abs() <= 1.0 + len2(v) * 1e-4, "{f:?} {v:?} -> {w:?}");
    }
}

/// Synthetic locator for `fast_grid`: each cell spans +/-50 cm round its centroid.
fn grid_locate(cols: u32, rows: u32) -> impl FnMut([i32; 3]) -> Option<u32> {
    move |p| {
        let c = (p[0] + 50).div_euclid(NAV_CELL_CM);
        let r = (p[2] + 50).div_euclid(NAV_CELL_CM);
        (c >= 0 && r >= 0 && (c as u32) < cols && (r as u32) < rows).then(|| r as u32 * cols + c as u32)
    }
}

#[test]
fn slot_goal_routes_to_the_best_slot_and_reports_its_pose() {
    let g = fast_grid(10, 10);
    let p = Planner::new(&g, &NONE, 0).unwrap();
    // A synthetic object at (500, 0, 500) facing -x, with front, back and an
    // off-lot slot.
    let frame = ObjectFrame { origin_cm: [500, 0, 500], forward: [-1, 0] };
    let slots = [
        RoutingSlot { slot_id: 0, local_cm: [0, 0, 80], facing_local: [0, -1], penalty_mm: 0 },
        RoutingSlot { slot_id: 1, local_cm: [0, 0, -80], facing_local: [0, 1], penalty_mm: 0 },
        RoutingSlot { slot_id: 2, local_cm: [0, 0, 5_000], facing_local: [0, -1], penalty_mm: 0 },
    ];
    let (goal, report) = Goal::slots(42, &frame, &slots, grid_locate(10, 10)).unwrap();
    assert_eq!(report, SlotReport { kept: 2, dropped_unlocated: 1 });
    assert_eq!(goal.targets[0].point_cm, Some([420, 0, 500]));
    assert_eq!(goal.targets[1].point_cm, Some([580, 0, 500]));
    let r = unlimited(&p, 0, &goal).unwrap(); // from (0, 0, 0): the -x side is nearer
    assert_eq!(r.arrival, Arrival { point_cm: [420, 0, 500], facing: Some([1, 0]), slot: Some((42, 0)) });
    assert_eq!(r.plan.legs.last().unwrap().to_cm, [420, 0, 500]);
    // From the far side the back slot wins.
    let r = unlimited(&p, 99, &goal).unwrap();
    assert_eq!(r.arrival.slot, Some((42, 1)));
}

#[test]
fn slot_goal_with_no_usable_slot_is_named() {
    let frame = ObjectFrame { origin_cm: [0; 3], forward: [0, 1] };
    let slot = RoutingSlot { slot_id: 0, local_cm: [0, 0, 9_000], ..RoutingSlot::default() };
    assert_eq!(Goal::slots(1, &frame, &[slot], grid_locate(3, 3)), Err(PlanError::NoUsableGoal));
    assert_eq!(Goal::slots(1, &frame, &[], grid_locate(3, 3)), Err(PlanError::NoUsableGoal));
    let flat = ObjectFrame { origin_cm: [0; 3], forward: [0, 0] };
    assert_eq!(Goal::slots(1, &flat, &[RoutingSlot::default()], grid_locate(3, 3)),
        Err(PlanError::NoUsableGoal));
    let g = fast_grid(2, 1);
    let p = Planner::new(&g, &NONE, 0).unwrap();
    assert_eq!(unlimited(&p, 0, &Goal::default()), Err(PlanError::NoUsableGoal));
}

#[test]
fn slot_in_a_blocked_cell_is_reached_but_not_passed_through() {
    // 1 x 5 corridor; the object's footprint blocks cell 2; a slot sits in it.
    let g = fast_grid(5, 1);
    let blocked = FootprintBlocker { blocked_cells: [2].into_iter().collect() };
    let p = Planner::new(&g, &blocked, 0).unwrap();
    let r = unlimited(&p, 0, &Goal::point(2, [210, 0, 0])).unwrap();
    assert_eq!(r.plan.cells, vec![0, 1, 2]);
    // Cell 4 lies beyond the blocked target: it is no corridor.
    let goal = Goal::points(&[(2, [210, 0, 0], 50_000), (4, [400, 0, 0], 0)]);
    assert_eq!(unlimited(&p, 0, &goal).unwrap().plan.goal_cell, 2);
    assert_eq!(p.gate(0, &Goal::cell(4)), Ok(Gate::Unreachable));
}

// ---------------------------------------------------------------------------
// Budget
// ---------------------------------------------------------------------------

#[test]
fn node_budget_is_exact_and_never_changes_a_returned_plan() {
    let g = fast_grid(30, 30);
    let p = Planner::new(&g, &NONE, 0).unwrap();
    let goal = Goal::cell(899);
    let full = unlimited(&p, 0, &goal).unwrap();
    let need = full.stats.expanded;
    let exact = Budget { max_expanded: need, max_micros: None };
    assert_eq!(p.plan(SIM, 0, &goal, exact, &MonotonicClock::new()).unwrap().plan, full.plan);
    let short = Budget { max_expanded: need - 1, max_micros: None };
    assert!(matches!(p.plan(SIM, 0, &goal, short, &MonotonicClock::new()),
        Err(PlanError::BudgetExceeded { limit: BudgetLimit::Nodes, expanded, .. }) if expanded == need - 1));
}

#[test]
fn budget_property_ok_is_identical_and_exceeded_is_honest() {
    let mut rng = SplitMix64(0x0A11_CE55_0055_0004);
    let (mut cut, mut kept) = (0, 0);
    for _ in 0..3_000 {
        let (g, blocked, flags) = random_graph(&mut rng);
        let p = Planner::new(&g, &blocked, flags).unwrap();
        let n = u32::try_from(g.cells.len()).unwrap();
        let (start, goal) = (rng.below(n), Goal::cell(rng.below(n)));
        let full = unlimited(&p, start, &goal);
        let budget = Budget { max_expanded: rng.below(12), max_micros: None };
        match (p.plan(SIM, start, &goal, budget, &MonotonicClock::new()), &full) {
            (Ok(a), Ok(b)) => {
                assert_eq!((&a.plan, a.target, a.score_mm), (&b.plan, b.target, b.score_mm));
                kept += 1;
            }
            (Err(PlanError::BudgetExceeded { limit: BudgetLimit::Nodes, .. }), Ok(b)) => {
                assert!(b.stats.expanded > budget.max_expanded);
                cut += 1;
            }
            // A refusal is the gate's, found without a search; no budget applies.
            (Err(e), Err(f)) => assert_eq!(&e, f),
            (a, b) => panic!("budgeted {a:?} vs unlimited {b:?}"),
        }
    }
    assert!(cut > 300 && kept > 300, "cut {cut} kept {kept}");
}

#[test]
fn time_budget_stops_a_long_search_with_a_named_answer() {
    let g = fast_grid(60, 60);
    let p = Planner::new(&g, &NONE, 0).unwrap();
    // A goal whose cheapest route is a long detour: wall off most of a column.
    let mut walled = g.clone();
    for r in 0..59 { set_flags(&mut walled, r * 60 + 30, CELL_OUT_OF_BOUNDS); }
    let pw = Planner::new(&walled, &NONE, 0).unwrap();
    let clock = SteppingClock::new(20_000); // every read is 20 ms later
    let budget = Budget { max_expanded: u32::MAX, max_micros: Some(10_000) };
    match pw.plan(SIM, 0, &Goal::cell(59), budget, &clock) {
        Err(PlanError::BudgetExceeded { limit: BudgetLimit::Time, expanded, elapsed_us }) => {
            assert_eq!(expanded, CLOCK_CHECK_EVERY);
            assert_eq!(elapsed_us, 20_000);
        }
        other => panic!("expected a time cut, got {other:?}"),
    }
    // Without a time limit the same clock cuts nothing, and the plan is the sealed one.
    let r = pw.plan(SIM, 0, &Goal::cell(59), Budget::UNLIMITED, &SteppingClock::new(1_000)).unwrap();
    assert_eq!(r.plan, walled.plan_route(0, 59, SIM).unwrap());
    assert!(p.plan(SIM, 0, &Goal::cell(59), Budget::SERVER_DEFAULT, &MonotonicClock::new()).is_ok());
}

// ---------------------------------------------------------------------------
// The reachability gate
// ---------------------------------------------------------------------------

#[test]
fn gate_answers_unreachable_without_searching() {
    // 128 x 128 cells split by an out-of-bounds column: the flood case.
    let mut g = fast_grid(128, 128);
    for r in 0..128 { set_flags(&mut g, r * 128 + 64, CELL_OUT_OF_BOUNDS); }
    let p = Planner::new(&g, &NONE, 0).unwrap();
    let goal = Goal::cell(127);
    assert_eq!(p.gate(0, &goal), Ok(Gate::Unreachable));
    // A zero budget proves no search ran: the gate's refusal comes back, not a budget cut.
    let zero = Budget { max_expanded: 0, max_micros: None };
    assert_eq!(p.plan(SIM, 0, &goal, zero, &MonotonicClock::new()),
        Err(PlanError::Kernel(KernelError::RouteGoalUnreachable)));
    assert!(matches!(p.plan_ungated(SIM, 0, &goal, zero, &MonotonicClock::new()),
        Err(PlanError::BudgetExceeded { .. })));
    // The ungated search floods the whole reachable half before it gives up.
    let t = Instant::now();
    assert_eq!(p.plan_ungated(SIM, 0, &goal, Budget::UNLIMITED, &MonotonicClock::new()),
        Err(PlanError::Kernel(KernelError::RouteGoalUnreachable)));
    let flood = t.elapsed();
    let t = Instant::now();
    for _ in 0..1_000 { assert_eq!(p.gate(0, &goal), Ok(Gate::Unreachable)); }
    let gate = t.elapsed() / 1_000;
    println!("NAVPLAN|case=gate_vs_flood|cells=16384|flood_us={}|gate_ns={}",
        flood.as_micros(), gate.as_nanos());
    assert!(gate * 50 < flood, "gate {gate:?} should be far cheaper than the flood {flood:?}");
}

#[test]
fn gate_start_refusals_and_missing_cells() {
    let mut g = fast_grid(3, 1);
    set_flags(&mut g, 0, CELL_NO_SPAWN);
    let blocked = FootprintBlocker { blocked_cells: [1].into_iter().collect() };
    let p = Planner::new(&g, &blocked, 0).unwrap();
    assert_eq!(p.gate(0, &Goal::cell(2)), Ok(Gate::StartInvalid));
    assert_eq!(p.gate(1, &Goal::cell(2)), Ok(Gate::StartInvalid));
    assert_eq!(p.gate(9, &Goal::cell(2)), Ok(Gate::StartInvalid));
    assert_eq!(p.gate(2, &Goal::cell(9)), Ok(Gate::Unreachable));
    assert_eq!(p.gate(2, &Goal::cell(2)), Ok(Gate::Reachable));
    assert_eq!(unlimited(&p, 9, &Goal::cell(9)),
        Err(PlanError::Kernel(KernelError::RouteStartOutOfBounds)));
}
