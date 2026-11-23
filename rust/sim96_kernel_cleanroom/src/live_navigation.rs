//! Trusted, memory-only input boundary. This module implements no transport.
use std::sync::Arc;
use std::collections::{BTreeMap, BTreeSet};
use crate::{KernelError, SimId};
use crate::routing_astar::{SimNav, NavGraph, RoutePlan, FootprintBlocker,
    DynamicObstacle, NAV_FLAG_MULTI_LOT};

pub const MAX_INPUT_BYTES: usize = 64 * 1024 * 1024;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum InputError { NoData, StaleGeneration, InvalidBinding, AmbiguousCell, Kernel(KernelError) }
impl From<KernelError> for InputError {
    fn from(e: KernelError) -> Self { Self::Kernel(e) }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Provenance { Measured, Authored, Inferred }

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CellBinding {
    pub cell_id: u32,
    pub lot_id: u64,
    pub room_id: u64,
    pub level: i16,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PortalBinding {
    pub portal_id: u32,
    pub object_id: u64,
    /// Slots are oriented cell_a -> cell_b; reverse traversal swaps them.
    pub entry_slot: u32,
    pub exit_slot: u32,
    pub cross_lot: bool,
}

#[derive(Clone, Debug)]
pub struct Bindings {
    pub schema_version: u32,
    pub epoch: u64,
    pub generation: u64,
    pub provenance: Provenance,
    pub cells: Vec<CellBinding>,
    pub portals: Vec<PortalBinding>,
    pub keys_held: Vec<u16>,
    pub obstacles: Vec<DynamicObstacle>,
    /// Session-assigned IDs, not truncations of full-width actor IDs.
    pub actors: Vec<(u64, SimId)>,
}

/// Immutable after validation; retained plans can finish against this Arc.
pub struct Snapshot {
    nav: SimNav,
    bindings: Bindings,
    graph: NavGraph,
    blocked: FootprintBlocker,
    normals: Vec<[i128; 3]>,
}

impl Snapshot {
    pub fn bindings(&self) -> &Bindings { &self.bindings }
    pub fn content_hash(&self) -> u64 { self.nav.header.content_hash }
    pub fn graph(&self) -> &NavGraph { &self.graph }

    /// Millimetres round to centimetres, halves away from zero, including negatives.
    pub fn quantize_mm(position: [i32; 3]) -> [i32; 3] {
        position.map(|v| ((i64::from(v) + if v < 0 { -5 } else { 5 }) / 10) as i32)
    }

    /// Exact convex containment, including Y on the measured supporting plane.
    /// Only common edges may tie; an interior overlap is ambiguous.
    pub fn map_cm(&self, point: [i32; 3], lot: u64, room: u64, level: i16,
        start: bool) -> Result<u32, InputError> {
        let mut candidates = Vec::new();
        for b in &self.bindings.cells {
            let c = &self.nav.cells[b.cell_id as usize];
            let first = usize::from(c.vertex_first);
            let vs = &self.nav.vertices[first..first + usize::from(c.vertex_len)];
            let a = vs[0];
            let mut boundary = false;
            let mut inside = true;
            for i in 0..vs.len() {
                let (v, w) = (vs[i], vs[(i + 1) % vs.len()]);
                let cross = (i128::from(w.x_cm) - i128::from(v.x_cm))
                    * (i128::from(point[2]) - i128::from(v.z_cm))
                    - (i128::from(w.z_cm) - i128::from(v.z_cm))
                    * (i128::from(point[0]) - i128::from(v.x_cm));
                if cross > 0 { inside = false; break; }
                boundary |= cross == 0;
            }
            let normal = self.normals[b.cell_id as usize];
            let d = [i128::from(point[0])-i128::from(a.x_cm),
                i128::from(point[1])-i128::from(a.y_cm), i128::from(point[2])-i128::from(a.z_cm)];
            if inside && normal.iter().zip(d).map(|(n,x)| n*x).sum::<i128>() == 0 {
                candidates.push((b.cell_id, boundary, (b.lot_id, b.room_id, b.level) == (lot, room, level)));
            }
        }
        if candidates.len() > 1 && candidates.iter().any(|(_, edge, _)| !edge) {
            return Err(InputError::AmbiguousCell);
        }
        candidates.iter().find(|(_,_,matches)| *matches).map(|(id,_,_)| *id).ok_or(InputError::Kernel(
            if start { KernelError::RouteStartOutOfBounds } else { KernelError::RouteGoalUnreachable }))
    }

    pub fn plan(&self, sim: SimId, start: u32, goal: u32, flags: u32) -> Result<RoutePlan, KernelError> {
        self.graph.plan_route_with(start, goal, sim, &self.blocked, flags)
    }
}

#[derive(Default)]
pub struct LiveNavigation {
    live: bool,
    epoch: u64,
    generation: u64,
    snapshot: Option<Arc<Snapshot>>,
}

impl LiveNavigation {
    pub fn is_live(&self) -> bool { self.live }
    /// Load, reconnect and switch-off must call this before accepting new work.
    pub fn invalidate(&mut self, epoch: u64, live: bool) {
        self.snapshot = None;
        self.epoch = epoch;
        self.live |= live;
    }
    pub fn pin(&self, epoch: u64, generation: u64) -> Result<Arc<Snapshot>, InputError> {
        if epoch != self.epoch { return Err(InputError::StaleGeneration); }
        let s = self.snapshot.as_ref().ok_or(InputError::NoData)?;
        if (epoch, generation) != (s.bindings.epoch, s.bindings.generation) {
            return Err(InputError::StaleGeneration);
        }
        Ok(Arc::clone(s))
    }
    pub fn current(&self) -> Result<Arc<Snapshot>, InputError> {
        self.snapshot.as_ref().map(Arc::clone).ok_or(InputError::NoData)
    }
    pub fn install(&mut self, bytes: &[u8], bindings: Bindings) -> Result<(), InputError> {
        if bindings.epoch != self.epoch || bindings.generation <= self.generation {
            return Err(InputError::StaleGeneration);
        }
        if bindings.schema_version != 1 || bindings.provenance == Provenance::Inferred
            || (self.live && bindings.provenance != Provenance::Measured)
            || bytes.len() > MAX_INPUT_BYTES {
            return Err(InputError::InvalidBinding);
        }
        let nav = SimNav::load(bytes)?;
        let mut actors = BTreeSet::new();
        let mut kernel_ids = BTreeSet::new();
        for (actor, sim) in &bindings.actors {
            if *actor == 0 || sim.0 == 0 || !actors.insert(*actor) || !kernel_ids.insert(sim.0) {
                return Err(InputError::InvalidBinding);
            }
        }
        if bindings.cells.len() != nav.cells.len() || bindings.portals.len() != nav.portals.len() {
            return Err(InputError::InvalidBinding);
        }
        let mut normals = Vec::with_capacity(nav.cells.len());
        let mut rooms = BTreeMap::new();
        for (i, b) in bindings.cells.iter().enumerate() {
            if b.cell_id as usize != i || b.level != nav.rooms[nav.cells[i].room_id as usize].level {
                return Err(InputError::InvalidBinding);
            }
            let identity = (b.lot_id, b.room_id, b.level);
            if rooms.insert(nav.cells[i].room_id, identity).is_some_and(|old| old != identity) {
                return Err(InputError::InvalidBinding);
            }
            let c = &nav.cells[i];
            let first = usize::from(c.vertex_first);
            let vs = &nav.vertices[first..first + usize::from(c.vertex_len)];
            // SIMNAV convexity is XZ-only. Runtime supporting surfaces must also
            // be nondegenerate and planar, not an arbitrary nearest floor.
            let a = vs[0];
            let normal = (1..vs.len()-1).find_map(|j| {
                let u = [i128::from(vs[j].x_cm)-i128::from(a.x_cm),
                    i128::from(vs[j].y_cm)-i128::from(a.y_cm),
                    i128::from(vs[j].z_cm)-i128::from(a.z_cm)];
                let v = [i128::from(vs[j+1].x_cm)-i128::from(a.x_cm),
                    i128::from(vs[j+1].y_cm)-i128::from(a.y_cm),
                    i128::from(vs[j+1].z_cm)-i128::from(a.z_cm)];
                let n = [u[1]*v[2]-u[2]*v[1], u[2]*v[0]-u[0]*v[2], u[0]*v[1]-u[1]*v[0]];
                (n[1] != 0).then_some(n)
            }).ok_or(InputError::InvalidBinding)?;
            if vs.iter().any(|v| normal[0]*(i128::from(v.x_cm)-i128::from(a.x_cm))
                + normal[1]*(i128::from(v.y_cm)-i128::from(a.y_cm))
                + normal[2]*(i128::from(v.z_cm)-i128::from(a.z_cm)) != 0) {
                return Err(InputError::InvalidBinding);
            }
            let center = c.centroid_cm;
            if normal[0]*(i128::from(center[0])-i128::from(a.x_cm))
                + normal[1]*(i128::from(center[1])-i128::from(a.y_cm))
                + normal[2]*(i128::from(center[2])-i128::from(a.z_cm)) != 0 {
                return Err(InputError::InvalidBinding);
            }
            normals.push(normal);
        }
        for (i, b) in bindings.portals.iter().enumerate() {
            let p = &nav.portals[i];
            let cross = bindings.cells[p.cell_a as usize].lot_id != bindings.cells[p.cell_b as usize].lot_id;
            if b.portal_id as usize != i || b.object_id != p.door_id || b.cross_lot != cross
                || (cross && nav.header.flags & NAV_FLAG_MULTI_LOT == 0)
                || (p.path_type & 0x8000_0000 != 0 && b.object_id == 0) {
                return Err(InputError::InvalidBinding);
            }
        }
        let mut graph = nav.to_graph(&bindings.keys_held);
        for b in &bindings.cells { graph.cell_lots.insert(b.cell_id, b.lot_id); }
        let blocked = FootprintBlocker::from_obstacles(&graph, &nav.templates, &bindings.obstacles)?;
        let candidate = Snapshot { nav, bindings, graph, blocked, normals };
        for p in &candidate.nav.portals {
            if !candidate.bindings.portals[p.portal_id as usize].cross_lot { continue; }
            // A cross-lot connector must physically meet both measured cells.
            // MULTI_LOT and an edge between distant centroids are not evidence.
            for cell in [p.cell_a, p.cell_b] {
                let b = &candidate.bindings.cells[cell as usize];
                for jamb in [p.jamb_a_cm, p.jamb_b_cm] {
                    if candidate.map_cm([jamb[0], p.portal_y_cm, jamb[1]],
                        b.lot_id, b.room_id, b.level, false) != Ok(cell) {
                        return Err(InputError::InvalidBinding);
                    }
                }
            }
        }
        self.generation = candidate.bindings.generation;
        self.snapshot = Some(Arc::new(candidate));
        Ok(())
    }
}

/// A caller's identity is checked by its trusted adapter; no public authority bypass.
pub struct Request {
    pub epoch: u64,
    pub generation: u64,
    pub corr: u64,
    pub actor_sim_id: u64,
    pub kernel_sim: SimId,
    pub start_xyz_mm: [i32; 3],
    pub goal_xyz_mm: [i32; 3],
    pub start_identity: (u64, u64, i16),
    pub goal_identity: (u64, u64, i16),
    pub flags: u32,
}

pub struct PinnedPlan {
    snapshot: Arc<Snapshot>,
    request: Request,
    plan: RoutePlan,
}

impl PinnedPlan {
    pub fn snapshot(&self) -> &Snapshot { &self.snapshot }
    pub fn request(&self) -> &Request { &self.request }
    pub fn plan(&self) -> &RoutePlan { &self.plan }
}

impl LiveNavigation {
    /// Check before offering work. An already executing plan may instead retain
    /// its snapshot until an explicit cancellation/finish receipt.
    pub fn validate_pending(&self, plan: &PinnedPlan) -> Result<(), InputError> {
        let current = self.pin(plan.request.epoch, plan.request.generation)?;
        if !Arc::ptr_eq(&current, &plan.snapshot) {
            return Err(InputError::StaleGeneration);
        }
        Ok(())
    }

    pub fn plan(&self, request: Request) -> Result<PinnedPlan, InputError> {
        let snapshot = self.pin(request.epoch, request.generation)?;
        if request.corr == 0 || !snapshot.bindings.actors.contains(&(request.actor_sim_id, request.kernel_sim)) {
            return Err(InputError::InvalidBinding);
        }
        let (lot, room, level) = request.start_identity;
        let start = snapshot.map_cm(Snapshot::quantize_mm(request.start_xyz_mm), lot, room, level, true)?;
        let (lot, room, level) = request.goal_identity;
        let goal = snapshot.map_cm(Snapshot::quantize_mm(request.goal_xyz_mm), lot, room, level, false)?;
        let plan = snapshot.plan(request.kernel_sim, start, goal, request.flags)?;
        Ok(PinnedPlan { snapshot, request, plan })
    }
}
