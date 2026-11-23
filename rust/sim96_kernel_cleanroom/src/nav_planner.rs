#![forbid(unsafe_code)]
//! Overhaul-track navigation planner (`internal documentation`): point
//! goals, object-slot goals, a search budget in expanded cells and in
//! microseconds, and a reachability gate that answers "unreachable" without a
//! search.
//!
//! Compiled only with the `nav-planner` Cargo feature. Nothing in the sealed
//! card 55 path calls it; [`crate::routing_astar`] is unchanged and remains the
//! planner the wire, `live_navigation` and the private envelope use. This
//! module reads [`NavGraph`] and produces the same [`RoutePlan`] shape, under
//! the same card 52 contract: integer millimetres, the octile metric, a
//! `(f, g, node)` ascending min-heap, ascending portal order, blocked cells
//! skipped except target cells, locked door portals refused. With
//! [`Goal::cell`] and [`Budget::UNLIMITED`] the answer equals
//! [`NavGraph::plan_route_with`] exactly (the `differential` tests).
//!
//! A budget can only cut a search short. It never changes a plan that is
//! returned, and running out is [`PlanError::BudgetExceeded`], never a guessed
//! path and never "unreachable".

use std::cell::Cell;
use std::cmp::Reverse;
use std::collections::{BTreeMap, BTreeSet, BinaryHeap};
use std::time::Instant;

use crate::handles::KernelError;
use crate::routing_astar::{ast_octile_mm, ast_validate_edges, route_path_hash, FootprintBlocker,
    LegKind, NavGraph, PortalEdge, RouteLeg, RoutePlan, CELL_TERRAIN_WATER, DOOR_EXTRA_MM,
    FLAG_ALLOW_WATER, THROUGH_OFFSET_CM, WATER_COST_MULT_X1000};
use crate::SimId;

/// How often (in expanded cells) the time budget reads the clock.
pub const CLOCK_CHECK_EVERY: u32 = 32;

/// Heap keys at or above this are "arrive at target" entries, not cells.
const ARRIVE: u64 = 1 << 32;

// ---------------------------------------------------------------------------
// Budget and clock
// ---------------------------------------------------------------------------

/// The search budget. `max_expanded` counts cells taken off the open list and
/// expanded (each cell at most once); `max_micros` is wall time since the
/// search began, read every [`CLOCK_CHECK_EVERY`] expansions.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Budget {
    pub max_expanded: u32,
    pub max_micros: Option<u64>,
}

impl Budget {
    pub const UNLIMITED: Budget = Budget { max_expanded: u32::MAX, max_micros: None };
    /// The SIMNAV cell ceiling (so the node cap never binds on a valid
    /// snapshot) and 100 ms, well inside the C# side's 0.75 s plan wait.
    pub const SERVER_DEFAULT: Budget = Budget { max_expanded: 65_536, max_micros: Some(100_000) };
}

/// Which budget ran out.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BudgetLimit { Nodes, Time }

/// A monotonic microsecond clock, injected so tests can script time.
pub trait Clock {
    fn now_us(&self) -> u64;
}

/// The production clock.
pub struct MonotonicClock { base: Instant }

impl MonotonicClock {
    pub fn new() -> Self { Self { base: Instant::now() } }
}

impl Default for MonotonicClock {
    fn default() -> Self { Self::new() }
}

impl Clock for MonotonicClock {
    fn now_us(&self) -> u64 {
        u64::try_from(self.base.elapsed().as_micros()).unwrap_or(u64::MAX)
    }
}

/// A test clock: each read advances time by `step_us`.
pub struct SteppingClock { now: Cell<u64>, step_us: u64 }

impl SteppingClock {
    pub fn new(step_us: u64) -> Self { Self { now: Cell::new(0), step_us } }
}

impl Clock for SteppingClock {
    fn now_us(&self) -> u64 {
        let t = self.now.get();
        self.now.set(t.saturating_add(self.step_us));
        t
    }
}

// ---------------------------------------------------------------------------
// Goals
// ---------------------------------------------------------------------------

/// One place a route may end. `point_cm: None` is the cell centroid (the
/// sealed planner's only goal). A point must lie in `cell`; the caller
/// resolves it (a later wave: `live_navigation::Snapshot::map_cm`, which has
/// no nearest-cell snap). `penalty_mm` ranks targets: the plan minimises path
/// cost plus penalty; ties break deterministically (heap order, then the
/// lower target index within one cell).
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Target {
    pub cell: u32,
    pub point_cm: Option<[i32; 3]>,
    pub penalty_mm: u64,
    /// World facing on arrival (slot goals), an XZ vector of any length.
    pub facing: Option<[i32; 2]>,
    /// `(object_id, slot_id)` for a slot goal.
    pub slot: Option<(u64, u32)>,
}

/// A non-empty list of targets; the first optimal one wins.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Goal {
    pub targets: Vec<Target>,
}

/// An object's placement on the XZ plane. `forward` is any non-zero integer
/// vector (the adapter quantises the game's float forward vector).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ObjectFrame {
    pub origin_cm: [i32; 3],
    pub forward: [i32; 2],
}

/// Where a Sim stands to use an object, in the object's frame: local +z is
/// the object's forward, local +x its right, `right = (forward.z, -forward.x)`.
/// This handedness is an assumption until a later wave checks it against
/// measured slots on a live object.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct RoutingSlot {
    pub slot_id: u32,
    pub local_cm: [i32; 3],
    pub facing_local: [i32; 2],
    pub penalty_mm: u64,
}

/// What [`Goal::slots`] kept and dropped.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct SlotReport {
    pub kept: u32,
    pub dropped_unlocated: u32,
}

/// `n / d` rounded to nearest, halves away from zero (`d > 0`).
fn div_round(n: i128, d: i128) -> i128 {
    if n >= 0 { (n + d / 2) / d } else { -((-n + d / 2) / d) }
}

fn isqrt_u128(v: u128) -> u128 {
    if v == 0 {
        return 0;
    }
    let mut x = 1u128 << (u128::BITS - v.leading_zeros()).div_ceil(2);
    loop {
        let y = (x + v / x) / 2;
        if y >= x {
            return x;
        }
        x = y;
    }
}

fn narrow(v: i128) -> i32 {
    i32::try_from(v).unwrap_or(if v < 0 { i32::MIN } else { i32::MAX })
}

impl ObjectFrame {
    /// `|forward|` in 1/65536 units: the integer square root of the squared
    /// length shifted by 32 bits, so a short vector such as (3, 14) keeps its
    /// precision.
    fn length_x65536(&self) -> Option<i128> {
        let [fx, fz] = self.forward.map(i128::from);
        let len = isqrt_u128((fx * fx + fz * fz).unsigned_abs() << 32);
        (len > 0).then(|| i128::try_from(len).unwrap_or(i128::MAX))
    }

    /// Rotate a local XZ vector into the world frame (no translation),
    /// rounding halves away from zero.
    pub fn rotate(&self, local: [i32; 2]) -> Option<[i32; 2]> {
        let len = self.length_x65536()?;
        let [fx, fz] = self.forward.map(i128::from);
        let (rx, rz) = (fz, -fx);
        let [lx, lz] = local.map(i128::from);
        let axis = |n: i128| narrow(div_round(n << 16, len));
        Some([axis(lx * rx + lz * fx), axis(lx * rz + lz * fz)])
    }

    /// A local point in world centimetres.
    pub fn to_world(&self, local_cm: [i32; 3]) -> Option<[i32; 3]> {
        let [x, z] = self.rotate([local_cm[0], local_cm[2]])?;
        let add = |o: i32, d: i32| narrow(i128::from(o) + i128::from(d));
        Some([add(self.origin_cm[0], x), add(self.origin_cm[1], local_cm[1]),
            add(self.origin_cm[2], z)])
    }
}

impl Goal {
    /// The sealed planner's goal: the cell centroid.
    pub fn cell(cell: u32) -> Goal {
        Goal { targets: vec![Target { cell, ..Target::default() }] }
    }

    /// End exactly at `point_cm`, which lies in `cell`.
    pub fn point(cell: u32, point_cm: [i32; 3]) -> Goal {
        Goal { targets: vec![Target { cell, point_cm: Some(point_cm), ..Target::default() }] }
    }

    /// Several candidate points `(cell, point, penalty_mm)`.
    pub fn points(points: &[(u32, [i32; 3], u64)]) -> Goal {
        Goal { targets: points.iter().map(|&(cell, p, penalty_mm)| Target { cell,
            point_cm: Some(p), penalty_mm, ..Target::default() }).collect() }
    }

    /// An object's routing slots, turned into world points and resolved to
    /// cells by `locate`. Unlocated slots are dropped and counted; none left
    /// (or a zero forward vector) is [`PlanError::NoUsableGoal`].
    pub fn slots(object_id: u64, frame: &ObjectFrame, slots: &[RoutingSlot],
        mut locate: impl FnMut([i32; 3]) -> Option<u32>) -> Result<(Goal, SlotReport), PlanError> {
        let mut report = SlotReport::default();
        let mut targets = Vec::new();
        for slot in slots {
            let world = frame.to_world(slot.local_cm).ok_or(PlanError::NoUsableGoal)?;
            let facing = frame.rotate(slot.facing_local).ok_or(PlanError::NoUsableGoal)?;
            match locate(world) {
                Some(cell) => {
                    report.kept += 1;
                    targets.push(Target { cell, point_cm: Some(world), penalty_mm: slot.penalty_mm,
                        facing: Some(facing), slot: Some((object_id, slot.slot_id)) });
                }
                None => report.dropped_unlocated += 1,
            }
        }
        if targets.is_empty() {
            return Err(PlanError::NoUsableGoal);
        }
        Ok((Goal { targets }, report))
    }
}

// ---------------------------------------------------------------------------
// Answers
// ---------------------------------------------------------------------------

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PlanError {
    /// The sealed planner's named refusals: `RouteStartOutOfBounds`,
    /// `RouteGoalUnreachable`, `PortalLocked`, `NavFormatUnsupported`.
    Kernel(KernelError),
    /// A slot or point goal with nothing usable in it.
    NoUsableGoal,
    /// The search ran out of budget. The answer is unknown, not "unreachable".
    BudgetExceeded { limit: BudgetLimit, expanded: u32, elapsed_us: u64 },
}

impl From<KernelError> for PlanError {
    fn from(e: KernelError) -> Self { Self::Kernel(e) }
}

/// The gate's verdict, from the component index alone.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Gate { Reachable, Unreachable, PortalLocked, StartInvalid }

impl Gate {
    /// The refusal the sealed planner names for this verdict.
    pub fn refusal(self) -> Option<KernelError> {
        match self {
            Gate::Reachable => None,
            Gate::Unreachable => Some(KernelError::RouteGoalUnreachable),
            Gate::PortalLocked => Some(KernelError::PortalLocked),
            Gate::StartInvalid => Some(KernelError::RouteStartOutOfBounds),
        }
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct SearchStats {
    pub expanded: u32,
    pub pushed: u32,
    pub elapsed_us: u64,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Arrival {
    pub point_cm: [i32; 3],
    pub facing: Option<[i32; 2]>,
    pub slot: Option<(u64, u32)>,
}

/// A plan in the sealed [`RoutePlan`] shape (a point goal adds one last
/// `SubPath` leg from the cell centroid, included in cost and `path_hash`),
/// the chosen target's index, the arrival pose, the ranking score (cost plus
/// penalty) and the search counters.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PlannedRoute {
    pub plan: RoutePlan,
    pub target: usize,
    pub arrival: Arrival,
    pub score_mm: u64,
    pub stats: SearchStats,
}

// ---------------------------------------------------------------------------
// The reachability index
// ---------------------------------------------------------------------------

struct UnionFind { parent: Vec<usize>, size: Vec<u32> }

impl UnionFind {
    fn new(n: usize) -> Self { Self { parent: (0..n).collect(), size: vec![1; n] } }

    fn find(&mut self, mut x: usize) -> usize {
        let mut root = x;
        while self.parent[root] != root { root = self.parent[root]; }
        while self.parent[x] != root {
            let next = self.parent[x];
            self.parent[x] = root;
            x = next;
        }
        root
    }

    fn union(&mut self, a: usize, b: usize) {
        let (mut a, mut b) = (self.find(a), self.find(b));
        if a == b { return; }
        if self.size[a] < self.size[b] { std::mem::swap(&mut a, &mut b); }
        self.parent[b] = a;
        self.size[a] += self.size[b];
    }

    fn roots(mut self) -> Vec<usize> {
        (0..self.parent.len()).map(|i| self.find(i)).collect()
    }
}

/// Components of the cells a route may pass through (traversable under the
/// flags and not blocked), joined by unlocked edges (`locked`) and by every
/// edge (`free`), plus "a locked door portal touches this component".
struct ReachIndex {
    dense: BTreeMap<u32, usize>,
    enterable: Vec<bool>,
    locked: Vec<usize>,
    free: Vec<usize>,
    lock_touch: Vec<bool>,
}

impl ReachIndex {
    fn build(graph: &NavGraph, blocked: &FootprintBlocker, flags: u32) -> Self {
        let dense: BTreeMap<u32, usize> = graph.cells.keys().enumerate().map(|(i, c)| (*c, i)).collect();
        let enterable: Vec<bool> = graph.cells.values()
            .map(|c| c.traversable(flags) && !blocked.blocked_cells.contains(&c.cell_id)).collect();
        let mut locked = UnionFind::new(dense.len());
        let mut free = UnionFind::new(dense.len());
        let edges = || graph.adjacency.iter().flat_map(|(from, list)| list.iter().map(move |e| (*from, e)));
        for (from, e) in edges() {
            let (Some(&a), Some(&b)) = (dense.get(&from), dense.get(&e.to_cell)) else { continue };
            if enterable[a] && enterable[b] {
                free.union(a, b);
                if !e.locked { locked.union(a, b); }
            }
        }
        let locked = locked.roots();
        let mut lock_touch = vec![false; dense.len()];
        for (from, e) in edges() {
            let (Some(&a), Some(&b)) = (dense.get(&from), dense.get(&e.to_cell)) else { continue };
            if e.locked && e.is_door_portal() && enterable[a] && enterable[b] {
                lock_touch[locked[a]] = true;
            }
        }
        Self { dense, enterable, locked, free: free.roots(), lock_touch }
    }
}

// ---------------------------------------------------------------------------
// The planner
// ---------------------------------------------------------------------------

/// One planner per snapshot generation, blocked set and flag word. Rebuild it
/// on the same invalidation events as `live_navigation`.
pub struct Planner<'g> {
    graph: &'g NavGraph,
    blocked: &'g FootprintBlocker,
    flags: u32,
    index: ReachIndex,
}

/// A request's targets after dropping cells the graph does not carry: per
/// target cell the best `(terminal cost incl. penalty, target index)`.
struct Resolved {
    best: BTreeMap<u32, (u64, usize)>,
}

impl<'g> Planner<'g> {
    /// Card 52 §B/§C.2 refusals, once: unknown flag bits and under-priced
    /// edges are `NavFormatUnsupported`.
    pub fn new(graph: &'g NavGraph, blocked: &'g FootprintBlocker, flags: u32)
        -> Result<Self, KernelError> {
        if flags & !FLAG_ALLOW_WATER != 0 {
            return Err(KernelError::NavFormatUnsupported);
        }
        ast_validate_edges(graph)?;
        Ok(Self { graph, blocked, flags, index: ReachIndex::build(graph, blocked, flags) })
    }

    fn water_scaled(&self, to: u32, cost: u64) -> Option<u64> {
        if self.graph.cells[&to].flags & CELL_TERRAIN_WATER != 0 {
            cost.checked_mul(WATER_COST_MULT_X1000).map(|v| v / 1000)
        } else {
            Some(cost)
        }
    }

    fn point_leg_mm(&self, target: &Target) -> Result<u64, KernelError> {
        let Some(p) = target.point_cm else { return Ok(0) };
        let c = self.graph.cells[&target.cell].centroid();
        self.water_scaled(target.cell, ast_octile_mm(c, (p[0], p[1], p[2])))
            .ok_or(KernelError::NavFormatUnsupported)
    }

    fn resolve(&self, goal: &Goal) -> Result<Resolved, KernelError> {
        let mut best: BTreeMap<u32, (u64, usize)> = BTreeMap::new();
        for (i, t) in goal.targets.iter().enumerate() {
            if !self.graph.cells.contains_key(&t.cell) { continue; }
            let terminal = self.point_leg_mm(t)?.checked_add(t.penalty_mm)
                .ok_or(KernelError::NavFormatUnsupported)?;
            let slot = best.entry(t.cell).or_insert((terminal, i));
            if (terminal, i) < *slot { *slot = (terminal, i); }
        }
        Ok(Resolved { best })
    }

    fn enterable(&self, cell: u32) -> bool {
        self.index.dense.get(&cell).is_some_and(|&i| self.index.enterable[i])
    }

    /// Component roots the search from `start` explores (`free`: ignoring locks).
    fn start_roots(&self, start: u32, free: bool) -> Vec<usize> {
        let comp = if free { &self.index.free } else { &self.index.locked };
        let mut roots = BTreeSet::new();
        if let Some(&s) = self.index.dense.get(&start) {
            if self.index.enterable[s] { roots.insert(comp[s]); }
        }
        for e in self.graph.adjacency.get(&start).map(Vec::as_slice).unwrap_or(&[]) {
            if (free || !e.locked) && self.enterable(e.to_cell) {
                roots.insert(comp[self.index.dense[&e.to_cell]]);
            }
        }
        roots.into_iter().collect()
    }

    fn explored(&self, cell: u32, start: u32, roots: &[usize], free: bool) -> bool {
        let comp = if free { &self.index.free } else { &self.index.locked };
        cell == start || (self.enterable(cell) && roots.contains(&comp[self.index.dense[&cell]]))
    }

    fn reaches(&self, target: u32, start: u32, roots: &[usize], free: bool) -> bool {
        if target == start { return true; }
        if !self.graph.cells[&target].traversable(self.flags) { return false; }
        if self.enterable(target) {
            return self.explored(target, start, roots, free);
        }
        // A blocked target is entered from an explored neighbour, never passed through.
        self.graph.adjacency.get(&target).map(Vec::as_slice).unwrap_or(&[]).iter()
            .any(|e| (free || !e.locked) && self.explored(e.to_cell, start, roots, free))
    }

    fn gate_resolved(&self, start: u32, resolved: &Resolved) -> Gate {
        let Some(s) = self.graph.cells.get(&start) else { return Gate::StartInvalid };
        if resolved.best.is_empty() { return Gate::Unreachable; }
        if !s.can_start() || self.blocked.blocked_cells.contains(&start) { return Gate::StartInvalid; }
        let roots = self.start_roots(start, false);
        if resolved.best.keys().any(|&t| self.reaches(t, start, &roots, false)) {
            return Gate::Reachable;
        }
        // Did the flood meet a locked door into a cell it could otherwise enter?
        let is_target = |c: u32| resolved.best.contains_key(&c);
        let door_into = |e: &PortalEdge| e.locked && e.is_door_portal()
            && self.graph.cells[&e.to_cell].traversable(self.flags)
            && (self.enterable(e.to_cell) || is_target(e.to_cell));
        let mut met_lock = roots.iter().any(|&r| self.index.lock_touch[r])
            || self.graph.adjacency.get(&start).is_some_and(|l| l.iter().any(door_into));
        if !met_lock {
            met_lock = resolved.best.keys().any(|&t| self.graph.cells[&t].traversable(self.flags)
                && self.graph.adjacency.get(&t).is_some_and(|l| l.iter().any(|e| e.locked
                    && e.is_door_portal() && self.explored(e.to_cell, start, &roots, false))));
        }
        if met_lock {
            let free = self.start_roots(start, true);
            if resolved.best.keys().any(|&t| self.reaches(t, start, &free, true)) {
                return Gate::PortalLocked;
            }
        }
        Gate::Unreachable
    }

    /// The reachability gate: the sealed planner's verdict without a search.
    pub fn gate(&self, start: u32, goal: &Goal) -> Result<Gate, PlanError> {
        let resolved = self.resolve(goal)?;
        Ok(self.gate_resolved(start, &resolved))
    }

    /// Gate first; search only when the gate says `Reachable`.
    pub fn plan(&self, sim: SimId, start: u32, goal: &Goal, budget: Budget, clock: &impl Clock)
        -> Result<PlannedRoute, PlanError> {
        if goal.targets.is_empty() { return Err(PlanError::NoUsableGoal); }
        let resolved = self.resolve(goal)?;
        if let Some(refusal) = self.gate_resolved(start, &resolved).refusal() {
            return Err(PlanError::Kernel(refusal));
        }
        self.search(sim, start, goal, &resolved, budget, clock)
    }

    /// The search alone, without the gate (for the gate/search agreement test).
    pub fn plan_ungated(&self, sim: SimId, start: u32, goal: &Goal, budget: Budget,
        clock: &impl Clock) -> Result<PlannedRoute, PlanError> {
        if goal.targets.is_empty() { return Err(PlanError::NoUsableGoal); }
        let resolved = self.resolve(goal)?;
        match self.gate_resolved(start, &resolved) {
            Gate::StartInvalid => return Err(KernelError::RouteStartOutOfBounds.into()),
            _ if resolved.best.is_empty() => return Err(KernelError::RouteGoalUnreachable.into()),
            _ => {}
        }
        self.search(sim, start, goal, &resolved, budget, clock)
    }

    fn heuristic(&self, from: u32, ends: &[((i32, i32, i32), u64)]) -> u64 {
        let c = self.graph.cells[&from].centroid();
        ends.iter().map(|(t, term)| ast_octile_mm(c, *t).saturating_add(*term)).min().unwrap_or(0)
    }

    fn search(&self, sim: SimId, start: u32, goal: &Goal, resolved: &Resolved, budget: Budget,
        clock: &impl Clock) -> Result<PlannedRoute, PlanError> {
        let t0 = clock.now_us();
        let ends: Vec<((i32, i32, i32), u64)> = resolved.best.iter()
            .map(|(cell, (term, _))| (self.graph.cells[cell].centroid(), *term)).collect();
        let mut stats = SearchStats::default();
        let mut open = BinaryHeap::new();
        let mut g: BTreeMap<u32, u64> = BTreeMap::new();
        let mut came: BTreeMap<u32, (u32, PortalEdge)> = BTreeMap::new();
        let mut closed: BTreeSet<u32> = BTreeSet::new();
        g.insert(start, 0);
        open.push(Reverse((self.heuristic(start, &ends), 0u64, u64::from(start))));
        stats.pushed = 1;
        let mut arrived: Option<u32> = None;
        while let Some(Reverse((_, node_g, key))) = open.pop() {
            if key >= ARRIVE {
                arrived = Some(u32::try_from(key - ARRIVE).unwrap_or(u32::MAX));
                break;
            }
            let node = u32::try_from(key).unwrap_or(u32::MAX);
            let target = resolved.best.get(&node).copied();
            if target.is_some_and(|(term, _)| term == 0) {
                arrived = Some(node);
                break;
            }
            if !closed.insert(node) { continue; }
            if stats.expanded >= budget.max_expanded {
                stats.elapsed_us = clock.now_us().saturating_sub(t0);
                return Err(PlanError::BudgetExceeded { limit: BudgetLimit::Nodes,
                    expanded: stats.expanded, elapsed_us: stats.elapsed_us });
            }
            stats.expanded += 1;
            if let Some(limit) = budget.max_micros {
                if stats.expanded % CLOCK_CHECK_EVERY == 0 {
                    let elapsed = clock.now_us().saturating_sub(t0);
                    if elapsed > limit {
                        return Err(PlanError::BudgetExceeded { limit: BudgetLimit::Time,
                            expanded: stats.expanded, elapsed_us: elapsed });
                    }
                }
            }
            if let Some((term, _)) = target {
                let total = g[&node].checked_add(term).ok_or(KernelError::NavFormatUnsupported)?;
                open.push(Reverse((total, total, ARRIVE | u64::from(node))));
                stats.pushed += 1;
            }
            // A blocked target is an end, never a corridor.
            if node != start && self.blocked.blocked_cells.contains(&node) { continue; }
            for edge in self.graph.adjacency.get(&node).map(Vec::as_slice).unwrap_or(&[]) {
                let to = edge.to_cell;
                if closed.contains(&to) { continue; }
                let cell = &self.graph.cells[&to];
                if !cell.traversable(self.flags)
                    || (self.blocked.blocked_cells.contains(&to) && !resolved.best.contains_key(&to)) {
                    continue;
                }
                if edge.locked { continue; }
                let tentative = self.water_scaled(to, edge.step_cost_mm())
                    .and_then(|step| node_g.checked_add(step))
                    .ok_or(KernelError::NavFormatUnsupported)?;
                if g.get(&to).is_none_or(|best| tentative < *best) {
                    g.insert(to, tentative);
                    came.insert(to, (node, *edge));
                    open.push(Reverse((tentative.saturating_add(self.heuristic(to, &ends)),
                        tentative, u64::from(to))));
                    stats.pushed += 1;
                }
            }
        }
        stats.elapsed_us = clock.now_us().saturating_sub(t0);
        let Some(end) = arrived else {
            // The gate was bypassed or disagrees; name it the same way it would.
            return Err(PlanError::Kernel(self.gate_resolved(start, resolved).refusal()
                .unwrap_or(KernelError::RouteGoalUnreachable)));
        };
        self.build(sim, start, end, goal, resolved, &g, &came, stats)
    }

    #[allow(clippy::too_many_arguments)]
    fn build(&self, sim: SimId, start: u32, end: u32, goal: &Goal, resolved: &Resolved,
        g: &BTreeMap<u32, u64>, came: &BTreeMap<u32, (u32, PortalEdge)>, stats: SearchStats)
        -> Result<PlannedRoute, PlanError> {
        let (_, index) = resolved.best[&end];
        let target = &goal.targets[index];
        let mut steps: Vec<(u32, u32, PortalEdge)> = Vec::new();
        let mut cursor = end;
        while cursor != start {
            let (prev, edge) = *came.get(&cursor).ok_or(KernelError::RouteGoalUnreachable)?;
            steps.push((prev, cursor, edge));
            cursor = prev;
        }
        steps.reverse();
        let mut cells = Vec::with_capacity(steps.len() + 1);
        cells.push(start);
        let mut door_crossings = Vec::new();
        let mut legs = Vec::with_capacity(steps.len() + 1);
        for (from, to, edge) in &steps {
            cells.push(*to);
            let (a, b) = (self.graph.cells[from].centroid(), self.graph.cells[to].centroid());
            let scaled = |cost: u64| self.water_scaled(*to, cost).ok_or(KernelError::NavFormatUnsupported);
            if edge.is_door_portal() {
                door_crossings.push(*edge);
                let center = edge.center_cm.unwrap_or_else(|| mid_cm(a, b));
                let through = through_end_cm(center, a, b);
                legs.push(RouteLeg { kind: LegKind::Approach, cell_id: *to, from_cm: arr(a),
                    to_cm: arr(center), cost_mm: scaled(edge.base_cost_mm)?, path_type: 0,
                    door_id: edge.door_guid });
                legs.push(RouteLeg { kind: LegKind::Through, cell_id: *to, from_cm: arr(center),
                    to_cm: arr(through), cost_mm: scaled(DOOR_EXTRA_MM)?, path_type: edge.path_type,
                    door_id: edge.door_guid });
                legs.push(RouteLeg { kind: LegKind::Exit, cell_id: *to, from_cm: arr(through),
                    to_cm: arr(b), cost_mm: 0, path_type: 0, door_id: edge.door_guid });
            } else {
                legs.push(RouteLeg { kind: LegKind::SubPath, cell_id: *to, from_cm: arr(a),
                    to_cm: arr(b), cost_mm: scaled(edge.step_cost_mm())?, path_type: 0, door_id: 0 });
            }
        }
        let centroid = self.graph.cells[&end].centroid_cm;
        let mut total_cost_mm = g[&end];
        let point = target.point_cm.unwrap_or(centroid);
        if point != centroid {
            let cost_mm = self.point_leg_mm(target)?;
            total_cost_mm = total_cost_mm.checked_add(cost_mm).ok_or(KernelError::NavFormatUnsupported)?;
            legs.push(RouteLeg { kind: LegKind::SubPath, cell_id: end, from_cm: centroid, to_cm: point,
                cost_mm, path_type: 0, door_id: 0 });
        }
        let score_mm = total_cost_mm.checked_add(target.penalty_mm)
            .ok_or(KernelError::NavFormatUnsupported)?;
        let path_hash = route_path_hash(&legs);
        Ok(PlannedRoute {
            plan: RoutePlan { sim, start_cell: start, goal_cell: end, cells, total_cost_mm,
                door_crossings, legs, path_hash },
            target: index,
            arrival: Arrival { point_cm: point, facing: target.facing, slot: target.slot },
            score_mm,
            stats,
        })
    }
}

// Leg geometry, restated from card 52 §F (the sealed helpers are private to
// `routing_astar`); the `differential` tests hold the two equal.
fn arr(p: (i32, i32, i32)) -> [i32; 3] { [p.0, p.1, p.2] }

fn mean2(p: i32, q: i32) -> i32 { narrow((i128::from(p) + i128::from(q)) / 2) }

fn mid_cm(a: (i32, i32, i32), b: (i32, i32, i32)) -> (i32, i32, i32) {
    (mean2(a.0, b.0), mean2(a.1, b.1), mean2(a.2, b.2))
}

fn through_end_cm(center: (i32, i32, i32), a: (i32, i32, i32), b: (i32, i32, i32)) -> (i32, i32, i32) {
    let d = [i64::from(b.0) - i64::from(a.0), i64::from(b.1) - i64::from(a.1),
        i64::from(b.2) - i64::from(a.2)];
    let len = isqrt_u128(d.iter().map(|v| u128::from(v.unsigned_abs()).pow(2)).sum());
    if len == 0 {
        return center;
    }
    let step = |v: i64| -> i64 {
        let mag = u128::from(v.unsigned_abs()) * u128::from(THROUGH_OFFSET_CM.unsigned_abs()) / len;
        let mag = i64::try_from(mag).unwrap_or(i64::MAX);
        if v < 0 { -mag } else { mag }
    };
    let add = |c: i32, v: i64| narrow(i128::from(c) + i128::from(step(v)));
    (add(center.0, d[0]), add(center.1, d[1]), add(center.2, d[2]))
}

#[cfg(test)]
#[path = "nav_planner_tests.rs"]
mod tests;
