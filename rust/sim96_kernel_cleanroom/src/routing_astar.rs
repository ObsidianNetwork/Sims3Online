#![forbid(unsafe_code)]
//! Wave 11 (cards 51-54) - clean-room navmesh graph + deterministic integer A*
//! planner. Canary 31 (graded by cards 51-53; not card 31).
//!
//! This is the module that discharges the planning half of ledger row 1
//! (`go_nogo::UNSUPPORTED_SURFACE_ROWS[0]`): the native `0x41b2a0` A*
//! replanner's *contract* - walkable cells, portal edges with unsigned
//! `PathType` (the card-02 high-bit door witness must survive unchanged),
//! locked doors, dynamic footprint blockers integrated with the card-03
//! reservation/footprint registries - is rebuilt here from the sealed cards'
//! behaviour, never from native bytes. Real Sunset Valley navmesh geometry
//! stays the native oracle until a live differential closes row 1 (hybrid
//! retention), so TS3W remains the ground truth the planner is graded against.
//!
//! Substrate (card 51, deltas K1/K2/K3/K7): the `SIMNAV01` binary format
//! ([`NavHeader`], [`NavRoom`], [`NavCell`], [`NavPortal`],
//! [`ObstacleTemplate`], [`NavVertex`]), loaded and validated by
//! [`SimNav::load`] with named rejections, compiled from its canonical JSON
//! sibling by [`SimNav::from_canonical_json`] (the `simnav_compile` bin). The
//! fixtures are hand-authored arithmetic (`fixtures/simnav/`); nothing is
//! extracted from game files (card 51 §A.2).
//!
//! Determinism contract (card 52 §D): integer millimetre costs only,
//! `BTreeMap`-ordered everything, neighbours by ascending `portal_id`, and a
//! strict `(f_cost, g_cost, cell_id)` all-ascending min-heap tie-break, so the
//! same graph + start + goal + blockers yields the byte-identical plan on
//! every machine, every rebuild.
//!
//! Cost contract (card 52 §C, §K delta K4, §L): `g` and `h` share ONE metric,
//! the 3-D octile [`ast_octile_mm`] (y included, `u128` intermediates). A
//! stored edge cost below that metric is refused by [`ast_validate_edges`]
//! (`NavFormatUnsupported`), so `h` can never over-estimate `g`; door portals
//! add [`DOOR_EXTRA_MM`] to `g` only.
//!
//! Outputs: [`RoutePlan::legs`] in card 52 §F's shape (SubPath per plain
//! edge, Approach/Through/Exit per door edge) and [`RoutePlan::path_hash`]
//! (card 52 §G.1, FNV-1a over the legs, `cost_mm` excluded); 8-word
//! [`PathData`] segments via [`NavGraph::to_path_data`] (canary 01) and the
//! canary-25 `BoundaryRouteAndSeatHandoff` shape via
//! [`NavGraph::boundary_route_and_seat_handoff`].

use std::collections::{BTreeMap, BTreeSet, BinaryHeap};
use std::cmp::Reverse;
use std::mem::{offset_of, size_of};

use crate::handles::KernelError;
use crate::route_presentation::BoundaryRoute;
use crate::kernel_iface::PathData;
use crate::SimId;

/// Navmesh cell size in centimetres - matches the footprint registry grid
/// (`economy::FOOTPRINT_CELL_CM`, card 17 §C.3), so a footprint cell maps 1:1
/// onto a navmesh cell.
pub const NAV_CELL_CM: i32 = 100;
/// Straight cell step, millimetres.
pub const STEP_STRAIGHT_MM: u32 = 1_000;
/// Diagonal cell step, millimetres: `round(1000 * sqrt(2))`. Equal to the
/// octile metric of a 100 cm diagonal (1414), so grid diagonals pass the
/// card 52 §C.2 load-time consistency check.
pub const STEP_DIAG_MM: u32 = 1_414;
/// cm -> mm, exactly, on every axis including y (card 52 §C.1).
pub const MM_PER_CM: u64 = 10;
/// 1024-scale straight axis, sqrt(2)-1 and sqrt(3)-sqrt(2) deltas of the 3-D
/// octile metric: `(d2*1024 + (d1-d0)*424 + d0*326) / 1024` (card 52 §C.1).
pub const OCTILE_AXIS_1024: u64 = 1_024;
pub const OCTILE_DIAG_DELTA_1024: u64 = 424;
pub const OCTILE_OBLIQUE_DELTA_1024: u64 = 326;
/// Added to `g` (never to `h`) for every high-bit `path_type` edge (card 51 §C.1).
pub const DOOR_EXTRA_MM: u64 = 250;
/// The door class bit of the unsigned `path_type` tag (card 02 / card 51 §C.1).
pub const PATHTYPE_HIGH_BIT: u32 = 0x8000_0000;
/// Card 51 §C.1's `path_type` table - the only values a `.simnav` portal may carry.
pub const PATHTYPE_SIM_PATH: u32 = 0x0000_0000;
pub const PATHTYPE_DOOR_PORTAL: u32 = 0x8000_0000;
pub const PATHTYPE_PORTAL_TRAVERSAL: u32 = 0x8000_0001;
pub const PATHTYPE_VEHICLE_WITNESS: u32 = 0x8A86_3E32;
/// Length of the door `Through` leg, centimetres (card 52 §F).
pub const THROUGH_OFFSET_CM: i64 = 10;
/// Sim half-width added to every side of an obstacle AABB (card 51 §C.3).
pub const HALF_WIDTH_CM: i32 = 25;
/// Water cells cost `x 2000 / 1000` when `FLAG_ALLOW_WATER` is set (card 51 §E).
pub const WATER_COST_MULT_X1000: u64 = 2_000;
/// Avoidance scale: exactly 1.0 (card 02 §D, card 51 §C.3).
pub const AVOIDANCE_SCALE_X1000: u32 = 1_000;
/// FNV-1a 64 seed and multiplier of `path_hash` (card 52 §G.1) and of the
/// `.simnav` `content_hash` (card 51 §D).
pub const PATH_HASH_FNV_OFFSET: u64 = 0xCBF2_9CE4_8422_2325;
pub const PATH_HASH_FNV_PRIME: u64 = 0x0000_0100_0000_01B3;

/// `plan_route` flag bit0 (card 52 §B); bits 1..31 reserved.
pub const FLAG_ALLOW_WATER: u32 = 1;
/// `NavCell::flags` (card 51 §E); bits 3..31 reserved zero.
pub const CELL_NO_SPAWN: u32 = 1;
pub const CELL_TERRAIN_WATER: u32 = 2;
pub const CELL_OUT_OF_BOUNDS: u32 = 4;

// ---------------------------------------------------------------------------
// Card 51 §B: the SIMNAV01 binary layout. Little-endian, no padding, every
// cross-reference an index (R3: no pointers); each scalar field's offset is
// asserted below in this same file (R4).
// ---------------------------------------------------------------------------

/// File magic, `53 49 4D 4E 41 56 30 31`.
pub const NAV_MAGIC: &[u8; 8] = b"SIMNAV01";
pub const NAV_FORMAT_VERSION: u32 = 1;
pub const NAV_HEADER_SIZE: usize = 64;
/// `NavHeader::flags`: bit0 templates present, bit1 multi-lot, bit2 portals
/// bidirectional (must be set); bits 3..31 reserved zero.
pub const NAV_FLAG_HAS_OBSTACLE_TEMPLATES: u32 = 1;
pub const NAV_FLAG_MULTI_LOT: u32 = 2;
pub const NAV_FLAG_PORTALS_BIDIRECTIONAL: u32 = 4;
pub const NAV_MAX_CELLS: u32 = 65_536;
pub const NAV_MAX_PORTALS: u32 = 65_536;

/// Card 51 §B.1, 64 bytes.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NavHeader {
    pub magic: [u8; 8],
    pub format_version: u32,
    pub flags: u32,
    pub lot_id: u64,
    pub world_seed: u64,
    pub room_count: u32,
    pub cell_count: u32,
    pub portal_count: u32,
    pub obstacle_template_count: u32,
    pub vertex_count: u32,
    pub reserved_must_be_zero: u32,
    pub content_hash: u64,
}

/// Card 51 §B.2, 16 bytes.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NavRoom {
    pub room_id: u32,
    pub level: i16,
    pub portal_first: u16,
    pub portal_len: u16,
    pub cell_first: u16,
    pub cell_len: u16,
    pub reserved: u16,
}

/// Card 51 §B.2, 32 bytes: a convex nav cell. Also the planner's runtime cell
/// (delta K2): `flags` carries what `walkable` used to (`CELL_OUT_OF_BOUNDS`
/// blocks traversal; it and `CELL_NO_SPAWN` refuse a start). A cell built in
/// code rather than loaded has `vertex_len == 0` and is tested by centroid only.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NavCell {
    pub cell_id: u32,
    pub room_id: u32,
    pub vertex_first: u16,
    pub vertex_len: u16,
    pub portal_first: u16,
    pub portal_len: u16,
    pub centroid_cm: [i32; 3],
    pub flags: u32,
}

/// Card 51 §B.2, 48 bytes. `path_type` is unsigned (R5); `lock_key == 0` is
/// unlocked (card 51 §C.2).
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NavPortal {
    pub portal_id: u32,
    pub cell_a: u32,
    pub cell_b: u32,
    pub path_type: u32,
    pub door_id: u64,
    pub jamb_a_cm: [i32; 2],
    pub jamb_b_cm: [i32; 2],
    pub portal_y_cm: i32,
    pub width_cm: u16,
    pub lock_key: u16,
}

/// Card 51 §B.2, 32 bytes. `object_type_key` is an authored key, not a game GUID.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ObstacleTemplate {
    pub object_type_key: u32,
    pub min_cm: [i32; 3],
    pub max_cm: [i32; 3],
    pub scale_x1000: u32,
}

/// Card 51 §B.2, 12 bytes.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NavVertex {
    pub x_cm: i32,
    pub y_cm: i32,
    pub z_cm: i32,
}

const _: () = assert!(size_of::<NavHeader>() == 64);
const _: () = assert!(offset_of!(NavHeader, magic) == 0x00);
const _: () = assert!(offset_of!(NavHeader, format_version) == 0x08);
const _: () = assert!(offset_of!(NavHeader, flags) == 0x0C);
const _: () = assert!(offset_of!(NavHeader, lot_id) == 0x10);
const _: () = assert!(offset_of!(NavHeader, world_seed) == 0x18);
const _: () = assert!(offset_of!(NavHeader, room_count) == 0x20);
const _: () = assert!(offset_of!(NavHeader, cell_count) == 0x24);
const _: () = assert!(offset_of!(NavHeader, portal_count) == 0x28);
const _: () = assert!(offset_of!(NavHeader, obstacle_template_count) == 0x2C);
const _: () = assert!(offset_of!(NavHeader, vertex_count) == 0x30);
const _: () = assert!(offset_of!(NavHeader, reserved_must_be_zero) == 0x34);
const _: () = assert!(offset_of!(NavHeader, content_hash) == 0x38);
const _: () = assert!(size_of::<NavRoom>() == 16);
const _: () = assert!(offset_of!(NavRoom, room_id) == 0x00);
const _: () = assert!(offset_of!(NavRoom, level) == 0x04);
const _: () = assert!(offset_of!(NavRoom, portal_first) == 0x06);
const _: () = assert!(offset_of!(NavRoom, portal_len) == 0x08);
const _: () = assert!(offset_of!(NavRoom, cell_first) == 0x0A);
const _: () = assert!(offset_of!(NavRoom, cell_len) == 0x0C);
const _: () = assert!(offset_of!(NavRoom, reserved) == 0x0E);
const _: () = assert!(size_of::<NavCell>() == 32);
const _: () = assert!(offset_of!(NavCell, cell_id) == 0x00);
const _: () = assert!(offset_of!(NavCell, room_id) == 0x04);
const _: () = assert!(offset_of!(NavCell, vertex_first) == 0x08);
const _: () = assert!(offset_of!(NavCell, vertex_len) == 0x0A);
const _: () = assert!(offset_of!(NavCell, portal_first) == 0x0C);
const _: () = assert!(offset_of!(NavCell, portal_len) == 0x0E);
const _: () = assert!(offset_of!(NavCell, centroid_cm) == 0x10);
const _: () = assert!(offset_of!(NavCell, flags) == 0x1C);
const _: () = assert!(size_of::<NavPortal>() == 48);
const _: () = assert!(offset_of!(NavPortal, portal_id) == 0x00);
const _: () = assert!(offset_of!(NavPortal, cell_a) == 0x04);
const _: () = assert!(offset_of!(NavPortal, cell_b) == 0x08);
const _: () = assert!(offset_of!(NavPortal, path_type) == 0x0C);
const _: () = assert!(offset_of!(NavPortal, door_id) == 0x10);
const _: () = assert!(offset_of!(NavPortal, jamb_a_cm) == 0x18);
const _: () = assert!(offset_of!(NavPortal, jamb_b_cm) == 0x20);
const _: () = assert!(offset_of!(NavPortal, portal_y_cm) == 0x28);
const _: () = assert!(offset_of!(NavPortal, width_cm) == 0x2C);
const _: () = assert!(offset_of!(NavPortal, lock_key) == 0x2E);
const _: () = assert!(size_of::<ObstacleTemplate>() == 32);
const _: () = assert!(offset_of!(ObstacleTemplate, object_type_key) == 0x00);
const _: () = assert!(offset_of!(ObstacleTemplate, min_cm) == 0x04);
const _: () = assert!(offset_of!(ObstacleTemplate, max_cm) == 0x10);
const _: () = assert!(offset_of!(ObstacleTemplate, scale_x1000) == 0x1C);
const _: () = assert!(size_of::<NavVertex>() == 12);
const _: () = assert!(offset_of!(NavVertex, x_cm) == 0x00);
const _: () = assert!(offset_of!(NavVertex, y_cm) == 0x04);
const _: () = assert!(offset_of!(NavVertex, z_cm) == 0x08);

impl NavCell {
    /// A cell built in code (no polygon): id, room, centroid, card 51 §E flags.
    pub fn at(cell_id: u32, room_id: u32, centroid_cm: (i32, i32, i32), flags: u32) -> Self {
        NavCell { cell_id, room_id, centroid_cm: [centroid_cm.0, centroid_cm.1, centroid_cm.2],
            flags, ..NavCell::default() }
    }

    pub fn centroid(&self) -> (i32, i32, i32) {
        (self.centroid_cm[0], self.centroid_cm[1], self.centroid_cm[2])
    }

    /// May a route pass through (or end in) this cell under the plan flags?
    pub fn traversable(&self, plan_flags: u32) -> bool {
        self.flags & CELL_OUT_OF_BOUNDS == 0
            && (self.flags & CELL_TERRAIN_WATER == 0 || plan_flags & FLAG_ALLOW_WATER != 0)
    }

    /// Card 52 §A: a start in an out-of-bounds or no-spawn cell is
    /// `RouteStartOutOfBounds`.
    pub fn can_start(&self) -> bool {
        self.flags & (CELL_OUT_OF_BOUNDS | CELL_NO_SPAWN) == 0
    }
}

/// The only mutation the planner accepts from the world side (card 52 §A):
/// an instance of `templates[template]` at `origin_cm`. `scale_x1000` must be
/// exactly 1000 (card 51 §C.3), else `NavFormatUnsupported`.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct DynamicObstacle {
    pub object_id: u64,
    pub template: u32,
    pub origin_cm: [i32; 3],
    pub scale_x1000: u32,
}

/// One traversal edge, both directions stored. `path_type` keeps the unsigned
/// card-02 discipline: high-bit values (the door-portal witness
/// `0x8A86_3E32`) are door portals, expanded into the card-02 three-leg
/// `Approach -> Through -> Exit` chain and gated on `locked` (the file's
/// `lock_key` resolved against the held keys when the graph is built, delta
/// K3). `base_cost_mm` must be at least the octile distance between its
/// endpoint centroids (card 52 §C.2). `center_cm` is the door centre a loaded
/// portal carries (jamb mean at `portal_y_cm`, card 52 §F); `None` uses the
/// truncating midpoint of the two centroids.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct PortalEdge {
    pub portal_id: u32,
    pub from_cell: u32,
    pub to_cell: u32,
    pub path_type: u32,
    pub door_guid: u64,
    pub locked: bool,
    pub base_cost_mm: u64,
    pub center_cm: Option<(i32, i32, i32)>,
}

impl PortalEdge {
    /// Card 02 §A.1: a high-bit `PathType` (the `DOOR_PORTAL` class the
    /// witness `0x8A86_3E32` belongs to) is a door portal. Signed
    /// reinterpretation is forbidden - it reintroduces the door-stop bug.
    pub fn is_door_portal(&self) -> bool {
        self.path_type & PATHTYPE_HIGH_BIT != 0
    }

    fn door_extra_mm(&self) -> u64 {
        if self.is_door_portal() { DOOR_EXTRA_MM } else { 0 }
    }

    /// `g` cost of traversing this edge: the stored base plus the door class
    /// extra (card 52 §C.2). Never part of the heuristic.
    pub fn step_cost_mm(&self) -> u64 {
        self.base_cost_mm.saturating_add(self.door_extra_mm())
    }
}

/// The kind of a planned leg (card 52 §F, delta K6): `SubPath` for a plain
/// edge, the three-leg `Approach -> Through -> Exit` chain for a door edge,
/// and the canary-25 `SeatHandoff` trailer (presentation only, never part of
/// `RoutePlan::legs` or `path_hash`).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum LegKind { #[default] SubPath, Approach, Through, Exit, SeatHandoff }

/// Card 52 §A's name for [`LegKind`].
pub type RouteLegKind = LegKind;

impl LegKind {
    /// The kind byte of the `path_hash` stream (card 52 §G.1).
    pub fn hash_byte(self) -> u8 {
        match self {
            LegKind::SubPath => 0,
            LegKind::Approach => 1,
            LegKind::Through => 2,
            LegKind::Exit => 3,
            LegKind::SeatHandoff => 4,
        }
    }
}

/// One planned leg (card 52 §A). `cell_id` is the cell the traversed edge
/// ends in; `path_type` is unsigned and only a `Through` leg may carry a
/// high-bit value. Leg costs sum to the plan's `total_cost_mm`: a plain edge
/// puts its whole step on the `SubPath`; a door puts the base on `Approach`
/// and `DOOR_EXTRA_MM` on `Through` (each times the water multiplier).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct RouteLeg {
    pub kind: LegKind,
    pub cell_id: u32,
    pub from_cm: [i32; 3],
    pub to_cm: [i32; 3],
    pub cost_mm: u64,
    pub path_type: u32,
    pub door_id: u64,
}

/// The canary-25 handoff trailer: where the sim arrives and the settle
/// tolerance the fork's `settle_seat_needed` measures against (8 cm, card 25).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SeatHandoff {
    pub seat_cell: u32,
    pub arrival_from_cell: u32,
    pub tolerance_cm: u32,
}

/// A completed plan. `cells` includes start and goal; `total_cost_mm` sums
/// every edge's step cost; `door_crossings` lists the door edges in traversal
/// order; `legs` and `path_hash` are card 52 §F/§G.1 (delta K5).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RoutePlan {
    pub sim: SimId,
    pub start_cell: u32,
    pub goal_cell: u32,
    pub cells: Vec<u32>,
    pub total_cost_mm: u64,
    pub door_crossings: Vec<PortalEdge>,
    pub legs: Vec<RouteLeg>,
    pub path_hash: u64,
}

/// Blocked cells for one plan: the precomputed set (delta K7), derived either
/// from card-51 obstacle templates by the §C.3 AABB test
/// ([`FootprintBlocker::from_obstacles`]) or from the live footprint registry
/// ([`FootprintBlocker::footprint_blocked_cells`]).
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct FootprintBlocker {
    pub blocked_cells: BTreeSet<u32>,
}

impl FootprintBlocker {
    /// Map footprint registry cells `(lot_id, gx, gz)` (`economy::LotEditRegistry`,
    /// `FOOTPRINT_CELL_CM` units) onto the navmesh cell of the same lot whose
    /// centroid is `(gx * 100, _, gz * 100)`. Cells the graph does not carry
    /// are ignored (a footprint outside the navmesh blocks nothing on it).
    pub fn footprint_blocked_cells(graph: &NavGraph,
        registry_cells: impl IntoIterator<Item = (u32, u32, u32)>) -> Self {
        let mut blocked_cells = BTreeSet::new();
        for (lot, gx, gz) in registry_cells {
            let (x, z) = (i32::try_from(gx.saturating_mul(NAV_CELL_CM as u32)).unwrap_or(i32::MAX),
                i32::try_from(gz.saturating_mul(NAV_CELL_CM as u32)).unwrap_or(i32::MAX));
            if let Some(cell) = graph.cells.values()
                .find(|c| graph.cell_lots.get(&c.cell_id) == Some(&u64::from(lot))
                    && c.centroid_cm[0] == x && c.centroid_cm[2] == z) {
                blocked_cells.insert(cell.cell_id);
            }
        }
        Self { blocked_cells }
    }

    /// Card 51 §C.3: each obstacle is its template's AABB scaled by
    /// `(extent * scale_x1000) / 1000` (integer, truncating), translated to
    /// `origin_cm`, and expanded by `HALF_WIDTH_CM` on every side. A cell is
    /// blocked iff its centroid or any of its vertices lies inside (inclusive).
    /// A scale other than 1000, on the instance or the template, or an unknown
    /// template index, is `NavFormatUnsupported`.
    pub fn from_obstacles(graph: &NavGraph, templates: &[ObstacleTemplate],
        obstacles: &[DynamicObstacle]) -> Result<Self, KernelError> {
        let mut blocked_cells = BTreeSet::new();
        for obstacle in obstacles {
            let template = usize::try_from(obstacle.template).ok()
                .and_then(|i| templates.get(i)).ok_or(KernelError::NavFormatUnsupported)?;
            if obstacle.scale_x1000 != AVOIDANCE_SCALE_X1000
                || template.scale_x1000 != AVOIDANCE_SCALE_X1000 {
                return Err(KernelError::NavFormatUnsupported);
            }
            let scale = i64::from(obstacle.scale_x1000);
            let half = i64::from(HALF_WIDTH_CM);
            let mut lo = [0i64; 3];
            let mut hi = [0i64; 3];
            for axis in 0..3 {
                let origin = i64::from(obstacle.origin_cm[axis]);
                lo[axis] = origin + i64::from(template.min_cm[axis]) * scale / 1000 - half;
                hi[axis] = origin + i64::from(template.max_cm[axis]) * scale / 1000 + half;
            }
            for cell in graph.cells.values() {
                let inside = |p: [i32; 3]| (0..3).all(|a| {
                    let v = i64::from(p[a]);
                    lo[a] <= v && v <= hi[a]
                });
                if graph.cell_points(cell).any(inside) {
                    blocked_cells.insert(cell.cell_id);
                }
            }
        }
        Ok(Self { blocked_cells })
    }
}

/// The 3-D octile distance in millimetres between two centimetre points
/// (card 52 §C.1): `|d|` per axis `x 10`, sorted ascending, then
/// `(d2*1024 + (d1-d0)*424 + d0*326) / 1024`, truncating. Intermediates are
/// `u128`, so an extreme `i32` span cannot wrap into a cheap phantom corridor.
/// The ONLY metric: the heuristic and the load-time edge check both call it.
pub fn ast_octile_mm(a: (i32, i32, i32), b: (i32, i32, i32)) -> u64 {
    let mm = |p: i32, q: i32| u128::from(p.abs_diff(q)) * u128::from(MM_PER_CM);
    let mut d = [mm(a.0, b.0), mm(a.1, b.1), mm(a.2, b.2)];
    d.sort_unstable();
    let axis = u128::from(OCTILE_AXIS_1024);
    let wide = (d[2] * axis
        + (d[1] - d[0]) * u128::from(OCTILE_DIAG_DELTA_1024)
        + d[0] * u128::from(OCTILE_OBLIQUE_DELTA_1024)) / axis;
    u64::try_from(wide).unwrap_or(u64::MAX)
}

/// Load-time cost consistency (card 52 §C.2, card 53 witness 12): every stored
/// edge must cost at least the octile distance between its endpoint centroids,
/// and must name cells the graph carries. Otherwise `NavFormatUnsupported` -
/// an under-priced edge is exactly what lets `h` over-estimate `g`.
pub fn ast_validate_edges(graph: &NavGraph) -> Result<(), KernelError> {
    for (from, edges) in &graph.adjacency {
        let a = graph.cells.get(from).ok_or(KernelError::NavFormatUnsupported)?.centroid();
        for edge in edges {
            let b = graph.cells.get(&edge.to_cell)
                .ok_or(KernelError::NavFormatUnsupported)?.centroid();
            if edge.base_cost_mm < ast_octile_mm(a, b) {
                return Err(KernelError::NavFormatUnsupported);
            }
        }
    }
    Ok(())
}

fn fnv1a64(hash: u64, bytes: &[u8]) -> u64 {
    bytes.iter().fold(hash, |h, b| (h ^ u64::from(*b)).wrapping_mul(PATH_HASH_FNV_PRIME))
}

/// FNV-1a 64 over the leg sequence (card 52 §G.1): per leg the kind byte,
/// `cell_id`, `from_cm`, `to_cm`, `path_type`, all little-endian; `cost_mm`
/// and `door_id` excluded.
pub fn route_path_hash(legs: &[RouteLeg]) -> u64 {
    let mut hash = PATH_HASH_FNV_OFFSET;
    for leg in legs {
        hash = fnv1a64(hash, &[leg.kind.hash_byte()]);
        hash = fnv1a64(hash, &leg.cell_id.to_le_bytes());
        for v in leg.from_cm.iter().chain(leg.to_cm.iter()) {
            hash = fnv1a64(hash, &v.to_le_bytes());
        }
        hash = fnv1a64(hash, &leg.path_type.to_le_bytes());
    }
    hash
}

fn arr(p: (i32, i32, i32)) -> [i32; 3] { [p.0, p.1, p.2] }

fn narrow(v: i64) -> i32 {
    i32::try_from(v).unwrap_or(if v < 0 { i32::MIN } else { i32::MAX })
}

/// Truncating-toward-zero mean of two values, summed in `i64` (card 51 §B.3).
fn mean2(p: i32, q: i32) -> i32 { narrow((i64::from(p) + i64::from(q)) / 2) }

fn mid_cm(a: (i32, i32, i32), b: (i32, i32, i32)) -> (i32, i32, i32) {
    (mean2(a.0, b.0), mean2(a.1, b.1), mean2(a.2, b.2))
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

/// The end of a door's `Through` leg (card 52 §F): `center` advanced
/// `THROUGH_OFFSET_CM` along the a->b normal `centroid(b) - centroid(a)`, per
/// axis `delta * offset / len` truncating toward zero, `len` the integer
/// square root of the summed squares in cm. No float, no epsilon; a
/// zero-length normal advances nothing.
fn through_end_cm(center: (i32, i32, i32), a: (i32, i32, i32), b: (i32, i32, i32))
    -> (i32, i32, i32) {
    let d = [i64::from(b.0) - i64::from(a.0),
        i64::from(b.1) - i64::from(a.1),
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
    (narrow(i64::from(center.0) + step(d[0])),
        narrow(i64::from(center.1) + step(d[1])),
        narrow(i64::from(center.2) + step(d[2])))
}

/// The navmesh: cells by id plus adjacency in deterministic order (neighbours
/// by ascending `portal_id`, card 52 §D). `cell_lots` keys each cell to its
/// lot for the footprint registry; `vertices` holds loaded cell polygons
/// (empty for graphs built in code).
#[derive(Clone, Debug, Default)]
pub struct NavGraph {
    pub cells: BTreeMap<u32, NavCell>,
    pub adjacency: BTreeMap<u32, Vec<PortalEdge>>,
    pub cell_lots: BTreeMap<u32, u64>,
    pub vertices: Vec<NavVertex>,
    next_portal_id: u32,
}

impl NavGraph {
    pub fn new() -> Self { Self::default() }

    pub fn add_cell(&mut self, lot_id: u64, cell: NavCell) {
        self.cell_lots.insert(cell.cell_id, lot_id);
        self.cells.insert(cell.cell_id, cell);
    }

    /// Insert a bidirectional edge pair, numbered with the next `portal_id`.
    /// Re-inserting the same `(from, to, path_type, door_guid)` key replaces
    /// it in place and keeps its `portal_id` (e.g. to unlock a door).
    pub fn add_edge(&mut self, edge: PortalEdge) {
        let same = |x: &PortalEdge, to: u32| x.to_cell == to && x.path_type == edge.path_type
            && x.door_guid == edge.door_guid;
        let existing = self.adjacency.get(&edge.from_cell)
            .and_then(|list| list.iter().find(|x| same(x, edge.to_cell)).map(|x| x.portal_id));
        let portal_id = existing.unwrap_or(self.next_portal_id);
        let edge = PortalEdge { portal_id, ..edge };
        let back = PortalEdge { from_cell: edge.to_cell, to_cell: edge.from_cell, ..edge };
        for e in [edge, back] {
            let list = self.adjacency.entry(e.from_cell).or_default();
            if let Some(slot) = list.iter_mut().find(|x| same(x, e.to_cell)) {
                *slot = e;
            } else {
                list.push(e);
            }
        }
        self.next_portal_id = self.next_portal_id.max(portal_id.saturating_add(1));
        self.sort_neighbours();
    }

    fn sort_neighbours(&mut self) {
        for list in self.adjacency.values_mut() {
            list.sort_by_key(|e| (e.portal_id, e.to_cell));
        }
    }

    /// The points card 51 §C.3 tests against an obstacle box: the centroid and
    /// every vertex of the cell's polygon (none for a cell built in code).
    fn cell_points<'a>(&'a self, cell: &NavCell) -> impl Iterator<Item = [i32; 3]> + 'a {
        let first = usize::from(cell.vertex_first);
        let polygon = self.vertices.get(first..first + usize::from(cell.vertex_len))
            .unwrap_or(&[]);
        std::iter::once(cell.centroid_cm)
            .chain(polygon.iter().map(|v| [v.x_cm, v.y_cm, v.z_cm]))
    }

    /// Grid helper: an 8-connected `cols x rows` block of walkable cells,
    /// straight steps costed `STEP_STRAIGHT_MM`, diagonals `STEP_DIAG_MM`.
    /// Cell id = `base + r * cols + c`, centre at `origin + (c, r) * 100 cm`.
    pub fn add_grid(&mut self, lot_id: u32, room_id: u16, base: u32,
        cols: u32, rows: u32, origin_cm: (i32, i32)) {
        for r in 0..rows {
            for c in 0..cols {
                self.add_cell(u64::from(lot_id), NavCell::at(base + r * cols + c,
                    u32::from(room_id),
                    (origin_cm.0 + c as i32 * NAV_CELL_CM, 0, origin_cm.1 + r as i32 * NAV_CELL_CM),
                    0));
            }
        }
        for r in 0..rows {
            for c in 0..cols {
                let id = base + r * cols + c;
                for (dr, dc, cost) in [(0i32, 1i32, STEP_STRAIGHT_MM), (1, 0, STEP_STRAIGHT_MM),
                    (1, 1, STEP_DIAG_MM), (1, -1, STEP_DIAG_MM)] {
                    let (nr, nc) = (r as i32 + dr, c as i32 + dc);
                    if nr < 0 || nc < 0 || nr >= rows as i32 || nc >= cols as i32 { continue; }
                    let to = base + (nr as u32) * cols + nc as u32;
                    self.add_edge(PortalEdge { from_cell: id, to_cell: to,
                        base_cost_mm: u64::from(cost), ..PortalEdge::default() });
                }
            }
        }
    }

    /// Admissible heuristic: literally [`ast_octile_mm`] between the cell
    /// centroids, with no door term (card 52 §C.3).
    fn heuristic_mm(&self, from: u32, goal: u32) -> u64 {
        ast_octile_mm(self.cells[&from].centroid(), self.cells[&goal].centroid())
    }

    /// The water multiplier for an edge into `to` (card 52 §C.2): `x 2000 / 1000`
    /// when the cell is water (only traversable with `FLAG_ALLOW_WATER`).
    fn water_scaled(&self, to: u32, cost: u64) -> Option<u64> {
        if self.cells[&to].flags & CELL_TERRAIN_WATER != 0 {
            cost.checked_mul(WATER_COST_MULT_X1000).map(|v| v / 1000)
        } else {
            Some(cost)
        }
    }

    /// Deterministic A* (card 52 §D): strict `(f_cost, g_cost, cell_id)`
    /// all-ascending min-heap ordering, blocked cells skipped (but a blocked
    /// *goal* stays reachable), high-bit door portals refused when locked.
    /// Named errors only; never a silent nothing.
    pub fn plan_route(&self, start_cell: u32, goal_cell: u32, sim_id: SimId)
        -> Result<RoutePlan, KernelError> {
        self.plan_route_blocked(start_cell, goal_cell, sim_id, &FootprintBlocker::default())
    }

    pub fn plan_route_blocked(&self, start_cell: u32, goal_cell: u32, sim_id: SimId,
        blocked: &FootprintBlocker) -> Result<RoutePlan, KernelError> {
        self.plan_route_with(start_cell, goal_cell, sim_id, blocked, 0)
    }

    /// [`NavGraph::plan_route_blocked`] with card 52 §B's `flags`
    /// (`FLAG_ALLOW_WATER`; any other bit is `NavFormatUnsupported`).
    pub fn plan_route_with(&self, start_cell: u32, goal_cell: u32, sim_id: SimId,
        blocked: &FootprintBlocker, flags: u32) -> Result<RoutePlan, KernelError> {
        if flags & !FLAG_ALLOW_WATER != 0 {
            return Err(KernelError::NavFormatUnsupported);
        }
        let start = self.cells.get(&start_cell).ok_or(KernelError::RouteStartOutOfBounds)?;
        if !self.cells.contains_key(&goal_cell) {
            return Err(KernelError::RouteGoalUnreachable);
        }
        if !start.can_start() || blocked.blocked_cells.contains(&start_cell) {
            return Err(KernelError::RouteStartOutOfBounds);
        }
        // Card 52 §C.2: an inconsistent edge is refused before it can mis-route.
        ast_validate_edges(self)?;
        let mut open = BinaryHeap::new();
        let mut g: BTreeMap<u32, u64> = BTreeMap::new();
        let mut came: BTreeMap<u32, (u32, PortalEdge)> = BTreeMap::new();
        g.insert(start_cell, 0);
        open.push(Reverse((self.heuristic_mm(start_cell, goal_cell), 0u64, start_cell)));
        let mut closed: BTreeSet<u32> = BTreeSet::new();
        let mut locked_blocked = false;
        while let Some(Reverse((_, node_g, node))) = open.pop() {
            if node == goal_cell { break; }
            if !closed.insert(node) { continue; }
            for edge in self.adjacency.get(&node).map(Vec::as_slice).unwrap_or(&[]) {
                let to = edge.to_cell;
                if closed.contains(&to) { continue; }
                let cell = &self.cells[&to];
                if !cell.traversable(flags)
                    || (blocked.blocked_cells.contains(&to) && to != goal_cell) {
                    continue;
                }
                if edge.locked {
                    if edge.is_door_portal() { locked_blocked = true; }
                    continue;
                }
                let tentative = self.water_scaled(to, edge.step_cost_mm())
                    .and_then(|step| node_g.checked_add(step))
                    .ok_or(KernelError::NavFormatUnsupported)?;
                if g.get(&to).is_none_or(|best| tentative < *best) {
                    debug_assert!(!closed.contains(&to), "card 52 §D: a closed node is never relaxed");
                    g.insert(to, tentative);
                    came.insert(to, (node, *edge));
                    open.push(Reverse((tentative.saturating_add(self.heuristic_mm(to, goal_cell)),
                        tentative, to)));
                }
            }
        }
        let Some(&total_cost_mm) = g.get(&goal_cell) else {
            return Err(self.unreachable_error(start_cell, goal_cell, locked_blocked, blocked,
                flags));
        };
        let mut steps: Vec<(u32, u32, PortalEdge)> = Vec::new();
        let mut cursor = goal_cell;
        while cursor != start_cell {
            let (prev, edge) = *came.get(&cursor).ok_or(KernelError::RouteGoalUnreachable)?;
            steps.push((prev, cursor, edge));
            cursor = prev;
        }
        steps.reverse();
        let mut cells = Vec::with_capacity(steps.len() + 1);
        cells.push(start_cell);
        let mut door_crossings = Vec::new();
        let mut legs = Vec::with_capacity(steps.len());
        for (from, to, edge) in &steps {
            cells.push(*to);
            let (a, b) = (self.cells[from].centroid(), self.cells[to].centroid());
            let scaled = |cost: u64| self.water_scaled(*to, cost)
                .ok_or(KernelError::NavFormatUnsupported);
            if edge.is_door_portal() {
                door_crossings.push(*edge);
                let center = edge.center_cm.unwrap_or_else(|| mid_cm(a, b));
                let through = through_end_cm(center, a, b);
                legs.push(RouteLeg { kind: LegKind::Approach, cell_id: *to, from_cm: arr(a),
                    to_cm: arr(center), cost_mm: scaled(edge.base_cost_mm)?, path_type: 0,
                    door_id: edge.door_guid });
                legs.push(RouteLeg { kind: LegKind::Through, cell_id: *to, from_cm: arr(center),
                    to_cm: arr(through), cost_mm: scaled(DOOR_EXTRA_MM)?,
                    path_type: edge.path_type, door_id: edge.door_guid });
                legs.push(RouteLeg { kind: LegKind::Exit, cell_id: *to, from_cm: arr(through),
                    to_cm: arr(b), cost_mm: 0, path_type: 0, door_id: edge.door_guid });
            } else {
                legs.push(RouteLeg { kind: LegKind::SubPath, cell_id: *to, from_cm: arr(a),
                    to_cm: arr(b), cost_mm: scaled(edge.step_cost_mm())?, path_type: 0,
                    door_id: 0 });
            }
        }
        let path_hash = route_path_hash(&legs);
        Ok(RoutePlan { sim: sim_id, start_cell, goal_cell, cells, total_cost_mm,
            door_crossings, legs, path_hash })
    }

    fn unreachable_error(&self, start: u32, goal: u32, locked_blocked: bool,
        blocked: &FootprintBlocker, flags: u32) -> KernelError {
        // Distinguish "a locked door was the only way" from plain
        // unreachability; both are named (card 52 §B), never silence.
        if locked_blocked && self.reachable_ignoring_locks(start, goal, blocked, flags) {
            KernelError::PortalLocked
        } else {
            KernelError::RouteGoalUnreachable
        }
    }

    fn reachable_ignoring_locks(&self, start: u32, goal: u32,
        blocked: &FootprintBlocker, flags: u32) -> bool {
        let mut seen: BTreeSet<u32> = BTreeSet::new();
        let mut stack = vec![start];
        while let Some(node) = stack.pop() {
            if node == goal { return true; }
            if !seen.insert(node) { continue; }
            for edge in self.adjacency.get(&node).map(Vec::as_slice).unwrap_or(&[]) {
                let to = edge.to_cell;
                if seen.contains(&to) { continue; }
                let cell = &self.cells[&to];
                if !cell.traversable(flags) || (blocked.blocked_cells.contains(&to) && to != goal) {
                    continue;
                }
                stack.push(to);
            }
        }
        false
    }

    /// Canary 01 conversion: one owned `PathData` per waypoint, carrying the
    /// *unsigned* path type of the edge that arrived there (door witness
    /// preserved verbatim) and the cell centroid in its three position words.
    /// The padding words of `PathDataRaw` are never fabricated - `PathData`
    /// has no slot for them, matching the Kani `words_defined` proof.
    pub fn to_path_data(&self, plan: &RoutePlan) -> Vec<PathData> {
        let mut out = Vec::with_capacity(plan.cells.len());
        for (index, cell_id) in plan.cells.iter().enumerate() {
            let cell = &self.cells[cell_id];
            let (path_type, object_id) = match index.checked_sub(1)
                .map(|p| (plan.cells[p], *cell_id))
            {
                Some((from, to)) => self.adjacency.get(&from).and_then(|list| list.iter()
                    .find(|e| e.to_cell == to && e.is_door_portal())
                    .map(|e| (e.path_type, e.door_guid)))
                    .unwrap_or((0, 0)),
                None => (0, 0),
            };
            out.push(PathData { path_type, object_id,
                portal_bits: [cell.centroid_cm[0] as u32, cell.centroid_cm[1] as u32,
                    cell.centroid_cm[2] as u32] });
        }
        out
    }

    /// Canary 02 + 25 conversion: the plan's card-52 legs (SubPath per plain
    /// edge, Approach/Through/Exit per door edge) followed by the canary-25
    /// seat-handoff leg at the goal centroid.
    pub fn boundary_legs(&self, plan: &RoutePlan) -> Vec<RouteLeg> {
        let mut legs = plan.legs.clone();
        let goal = self.cells.get(&plan.goal_cell).map(|c| c.centroid_cm).unwrap_or_default();
        legs.push(RouteLeg { kind: LegKind::SeatHandoff, cell_id: plan.goal_cell,
            from_cm: goal, to_cm: goal, cost_mm: 0, path_type: 0, door_id: 0 });
        legs
    }

    /// The canary-25 wrapper: a `BoundaryRoute` armed with the leg count plus
    /// the seat handoff trailer the presenter replays last (8 cm settle
    /// tolerance, card 25 §C).
    pub fn boundary_route_and_seat_handoff(&self, plan: &RoutePlan)
        -> (BoundaryRoute, SeatHandoff) {
        let legs = self.boundary_legs(plan);
        let arrival = plan.cells[plan.cells.len().saturating_sub(2)];
        (BoundaryRoute::new(plan.sim, u32::try_from(legs.len()).unwrap_or(u32::MAX)),
            SeatHandoff { seat_cell: plan.goal_cell, arrival_from_cell: arrival,
                tolerance_cm: 8 })
    }

    /// Deterministic FNV-1a digest of a plan (cells, doors, cost) - the `hash`
    /// field the daemon answers and broadcasts, so both presenter streams can
    /// prove they were handed the same route. Distinct from the card 52 §G.1
    /// [`RoutePlan::path_hash`] that canary 31 grades.
    pub fn plan_hash(plan: &RoutePlan) -> u64 {
        let mut hash: u64 = PATH_HASH_FNV_OFFSET;
        let mut mix = |value: u64| hash = fnv1a64(hash, &value.to_le_bytes());
        for cell in &plan.cells { mix(u64::from(*cell)); }
        for door in &plan.door_crossings { mix(door.door_guid); mix(u64::from(door.path_type)); }
        mix(plan.total_cost_mm);
        hash
    }
}

// ---------------------------------------------------------------------------
// Card 51: the loaded `.simnav` (delta K1) and its conversion to the planner
// graph (K2 cells, K3 lock keys, K7 obstacles).
// ---------------------------------------------------------------------------

/// A loaded, validated `SIMNAV01` file (card 51 §B), tables in file order.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct SimNav {
    pub header: NavHeader,
    pub rooms: Vec<NavRoom>,
    pub cells: Vec<NavCell>,
    pub portals: Vec<NavPortal>,
    pub templates: Vec<ObstacleTemplate>,
    pub vertices: Vec<NavVertex>,
}

/// Bounds-checked little-endian reader: every short read is
/// `NavFormatUnsupported`, never a panic.
struct Le<'a> { bytes: &'a [u8], at: usize }

impl Le<'_> {
    fn take<const N: usize>(&mut self) -> Result<[u8; N], KernelError> {
        let end = self.at.checked_add(N).ok_or(KernelError::NavFormatUnsupported)?;
        let slice = self.bytes.get(self.at..end).ok_or(KernelError::NavFormatUnsupported)?;
        self.at = end;
        let mut out = [0u8; N];
        out.copy_from_slice(slice);
        Ok(out)
    }
    fn u16(&mut self) -> Result<u16, KernelError> { self.take().map(u16::from_le_bytes) }
    fn i16(&mut self) -> Result<i16, KernelError> { self.take().map(i16::from_le_bytes) }
    fn u32(&mut self) -> Result<u32, KernelError> { self.take().map(u32::from_le_bytes) }
    fn i32(&mut self) -> Result<i32, KernelError> { self.take().map(i32::from_le_bytes) }
    fn u64(&mut self) -> Result<u64, KernelError> { self.take().map(u64::from_le_bytes) }
    fn i32s<const N: usize>(&mut self) -> Result<[i32; N], KernelError> {
        let mut out = [0i32; N];
        for v in &mut out { *v = self.i32()?; }
        Ok(out)
    }
}

fn in_range(first: u16, len: u16, count: usize) -> bool {
    usize::from(first) + usize::from(len) <= count
}

fn known_path_type(path_type: u32) -> bool {
    matches!(path_type, PATHTYPE_SIM_PATH | PATHTYPE_DOOR_PORTAL | PATHTYPE_PORTAL_TRAVERSAL
        | PATHTYPE_VEHICLE_WITNESS)
}

impl SimNav {
    /// Serialise in card 51 §B order. Counts and magic come from the tables;
    /// `content_hash` is computed over the header with the hash zeroed plus
    /// the body (card 51 §D) and written back. Does not validate: tests use it
    /// to author deliberately broken files.
    pub fn to_bytes(&self) -> Vec<u8> {
        let count = |n: usize| u32::try_from(n).unwrap_or(u32::MAX);
        let h = NavHeader { magic: *NAV_MAGIC, room_count: count(self.rooms.len()),
            cell_count: count(self.cells.len()), portal_count: count(self.portals.len()),
            obstacle_template_count: count(self.templates.len()),
            vertex_count: count(self.vertices.len()), content_hash: 0, ..self.header };
        let mut out = Vec::new();
        out.extend_from_slice(&h.magic);
        for v in [h.format_version, h.flags] { out.extend_from_slice(&v.to_le_bytes()); }
        for v in [h.lot_id, h.world_seed] { out.extend_from_slice(&v.to_le_bytes()); }
        for v in [h.room_count, h.cell_count, h.portal_count, h.obstacle_template_count,
            h.vertex_count, h.reserved_must_be_zero] {
            out.extend_from_slice(&v.to_le_bytes());
        }
        out.extend_from_slice(&0u64.to_le_bytes());
        for r in &self.rooms {
            out.extend_from_slice(&r.room_id.to_le_bytes());
            out.extend_from_slice(&r.level.to_le_bytes());
            for v in [r.portal_first, r.portal_len, r.cell_first, r.cell_len, r.reserved] {
                out.extend_from_slice(&v.to_le_bytes());
            }
        }
        for c in &self.cells {
            for v in [c.cell_id, c.room_id] { out.extend_from_slice(&v.to_le_bytes()); }
            for v in [c.vertex_first, c.vertex_len, c.portal_first, c.portal_len] {
                out.extend_from_slice(&v.to_le_bytes());
            }
            for v in c.centroid_cm { out.extend_from_slice(&v.to_le_bytes()); }
            out.extend_from_slice(&c.flags.to_le_bytes());
        }
        for p in &self.portals {
            for v in [p.portal_id, p.cell_a, p.cell_b, p.path_type] {
                out.extend_from_slice(&v.to_le_bytes());
            }
            out.extend_from_slice(&p.door_id.to_le_bytes());
            for v in p.jamb_a_cm.iter().chain(p.jamb_b_cm.iter()) {
                out.extend_from_slice(&v.to_le_bytes());
            }
            out.extend_from_slice(&p.portal_y_cm.to_le_bytes());
            for v in [p.width_cm, p.lock_key] { out.extend_from_slice(&v.to_le_bytes()); }
        }
        for t in &self.templates {
            out.extend_from_slice(&t.object_type_key.to_le_bytes());
            for v in t.min_cm.iter().chain(t.max_cm.iter()) {
                out.extend_from_slice(&v.to_le_bytes());
            }
            out.extend_from_slice(&t.scale_x1000.to_le_bytes());
        }
        for v in &self.vertices {
            for c in [v.x_cm, v.y_cm, v.z_cm] { out.extend_from_slice(&c.to_le_bytes()); }
        }
        let hash = fnv1a64(PATH_HASH_FNV_OFFSET, &out);
        out[0x38..0x40].copy_from_slice(&hash.to_le_bytes());
        out
    }

    /// Load and validate a `SIMNAV01` file, in card 51 §F's order (the first
    /// failing check names the error): 1 header magic/version, 2 reserved
    /// fields and flags and template scale, 3 table lengths and dense
    /// self-equal ids and in-range references, 4 portal canonical order and
    /// known `path_type`, 6 `content_hash` (`NavContentHashMismatch`), 7 cell
    /// polygons (3..=8 vertices, the §B.3 centroid recomputed, reserved flag
    /// bits, §B.4 rule 3's lowest-first counter-clockwise convex order).
    /// Check 5 (connectivity) is the planner's `RouteGoalUnreachable`.
    pub fn load(bytes: &[u8]) -> Result<SimNav, KernelError> {
        use KernelError::NavFormatUnsupported as Bad;
        // 1. Header.
        if bytes.len() < NAV_HEADER_SIZE {
            return Err(Bad);
        }
        let mut r = Le { bytes, at: 0 };
        let header = NavHeader {
            magic: r.take()?, format_version: r.u32()?, flags: r.u32()?, lot_id: r.u64()?,
            world_seed: r.u64()?, room_count: r.u32()?, cell_count: r.u32()?,
            portal_count: r.u32()?, obstacle_template_count: r.u32()?, vertex_count: r.u32()?,
            reserved_must_be_zero: r.u32()?, content_hash: r.u64()?,
        };
        if &header.magic != NAV_MAGIC || header.format_version != NAV_FORMAT_VERSION {
            return Err(Bad);
        }
        // 2. Reserved fields and flags.
        let known_flags = NAV_FLAG_HAS_OBSTACLE_TEMPLATES | NAV_FLAG_MULTI_LOT
            | NAV_FLAG_PORTALS_BIDIRECTIONAL;
        if header.reserved_must_be_zero != 0 || header.flags & !known_flags != 0
            || header.flags & NAV_FLAG_PORTALS_BIDIRECTIONAL == 0
            || (header.obstacle_template_count != 0
                && header.flags & NAV_FLAG_HAS_OBSTACLE_TEMPLATES == 0) {
            return Err(Bad);
        }
        // 3. Table lengths against the counts.
        if header.cell_count == 0 || header.cell_count > NAV_MAX_CELLS
            || header.portal_count > NAV_MAX_PORTALS {
            return Err(Bad);
        }
        let expected = NAV_HEADER_SIZE as u64 + 16 * u64::from(header.room_count)
            + 32 * u64::from(header.cell_count) + 48 * u64::from(header.portal_count)
            + 32 * u64::from(header.obstacle_template_count) + 12 * u64::from(header.vertex_count);
        if u64::try_from(bytes.len()) != Ok(expected) {
            return Err(Bad);
        }
        let n = |count: u32| usize::try_from(count).map_err(|_| Bad);
        let (rooms_n, cells_n, portals_n) =
            (n(header.room_count)?, n(header.cell_count)?, n(header.portal_count)?);
        let (templates_n, vertices_n) = (n(header.obstacle_template_count)?, n(header.vertex_count)?);
        let mut nav = SimNav { header, ..SimNav::default() };
        for _ in 0..rooms_n {
            nav.rooms.push(NavRoom { room_id: r.u32()?, level: r.i16()?, portal_first: r.u16()?,
                portal_len: r.u16()?, cell_first: r.u16()?, cell_len: r.u16()?,
                reserved: r.u16()? });
        }
        for _ in 0..cells_n {
            nav.cells.push(NavCell { cell_id: r.u32()?, room_id: r.u32()?,
                vertex_first: r.u16()?, vertex_len: r.u16()?, portal_first: r.u16()?,
                portal_len: r.u16()?, centroid_cm: r.i32s()?, flags: r.u32()? });
        }
        for _ in 0..portals_n {
            nav.portals.push(NavPortal { portal_id: r.u32()?, cell_a: r.u32()?,
                cell_b: r.u32()?, path_type: r.u32()?, door_id: r.u64()?,
                jamb_a_cm: r.i32s()?, jamb_b_cm: r.i32s()?, portal_y_cm: r.i32()?,
                width_cm: r.u16()?, lock_key: r.u16()? });
        }
        for _ in 0..templates_n {
            nav.templates.push(ObstacleTemplate { object_type_key: r.u32()?,
                min_cm: r.i32s()?, max_cm: r.i32s()?, scale_x1000: r.u32()? });
        }
        for _ in 0..vertices_n {
            nav.vertices.push(NavVertex { x_cm: r.i32()?, y_cm: r.i32()?, z_cm: r.i32()? });
        }
        if nav.rooms.iter().any(|room| room.reserved != 0)
            || nav.templates.iter().any(|t| t.scale_x1000 != AVOIDANCE_SCALE_X1000) {
            return Err(Bad);
        }
        let dense = |id: u32, index: usize| usize::try_from(id).is_ok_and(|id| id == index);
        for (i, room) in nav.rooms.iter().enumerate() {
            if !dense(room.room_id, i) || !in_range(room.cell_first, room.cell_len, cells_n)
                || !in_range(room.portal_first, room.portal_len, portals_n) {
                return Err(Bad);
            }
        }
        for (i, cell) in nav.cells.iter().enumerate() {
            if !dense(cell.cell_id, i) || n(cell.room_id)? >= rooms_n
                || !in_range(cell.vertex_first, cell.vertex_len, vertices_n)
                || !in_range(cell.portal_first, cell.portal_len, portals_n) {
                return Err(Bad);
            }
        }
        // 4. Portal canonical order and unsigned path_type class.
        for (i, portal) in nav.portals.iter().enumerate() {
            if !dense(portal.portal_id, i) || portal.cell_a >= portal.cell_b
                || n(portal.cell_b)? >= cells_n || !known_path_type(portal.path_type) {
                return Err(Bad);
            }
        }
        // 6. content_hash over the header with the hash zeroed, plus the body.
        let mut zeroed = bytes.to_vec();
        zeroed[0x38..0x40].fill(0);
        if fnv1a64(PATH_HASH_FNV_OFFSET, &zeroed) != header.content_hash {
            return Err(KernelError::NavContentHashMismatch);
        }
        // 7. Cell polygons.
        for cell in &nav.cells {
            if !(3..=8).contains(&cell.vertex_len)
                || cell.flags & !(CELL_NO_SPAWN | CELL_TERRAIN_WATER | CELL_OUT_OF_BOUNDS) != 0 {
                return Err(Bad);
            }
            let first = usize::from(cell.vertex_first);
            let polygon = &nav.vertices[first..first + usize::from(cell.vertex_len)];
            let len = i64::from(cell.vertex_len);
            let mean = |axis: fn(&NavVertex) -> i32| -> Result<i32, KernelError> {
                let sum: i64 = polygon.iter().map(|v| i64::from(axis(v))).sum();
                i32::try_from(sum / len).map_err(|_| Bad)
            };
            let centroid = [mean(|v| v.x_cm)?, mean(|v| v.y_cm)?, mean(|v| v.z_cm)?];
            if centroid != cell.centroid_cm || !canonical_convex_ccw(polygon) {
                return Err(Bad);
            }
        }
        Ok(nav)
    }

    /// The planner graph (deltas K2/K3): cells as loaded (every cell in the
    /// header's lot), one bidirectional edge per portal numbered by its
    /// `portal_id`, costed at the octile distance between the endpoint
    /// centroids (consistent by construction, card 52 §C.2), `lock_key`
    /// resolved against `keys_held`, and the door centre from the jambs.
    pub fn to_graph(&self, keys_held: &[u16]) -> NavGraph {
        let mut graph = NavGraph { vertices: self.vertices.clone(), ..NavGraph::default() };
        for cell in &self.cells {
            graph.add_cell(self.header.lot_id, *cell);
        }
        for p in &self.portals {
            let (Some(a), Some(b)) = (graph.cells.get(&p.cell_a), graph.cells.get(&p.cell_b))
                else { continue };
            let center = (mean2(p.jamb_a_cm[0], p.jamb_b_cm[0]), p.portal_y_cm,
                mean2(p.jamb_a_cm[1], p.jamb_b_cm[1]));
            let edge = PortalEdge { portal_id: p.portal_id, from_cell: p.cell_a,
                to_cell: p.cell_b, path_type: p.path_type, door_guid: p.door_id,
                locked: p.lock_key != 0 && !keys_held.contains(&p.lock_key),
                base_cost_mm: ast_octile_mm(a.centroid(), b.centroid()),
                center_cm: Some(center) };
            let back = PortalEdge { from_cell: p.cell_b, to_cell: p.cell_a, ..edge };
            graph.adjacency.entry(p.cell_a).or_default().push(edge);
            graph.adjacency.entry(p.cell_b).or_default().push(back);
            graph.next_portal_id = graph.next_portal_id.max(p.portal_id.saturating_add(1));
        }
        graph.sort_neighbours();
        graph
    }
}

/// Card 51 §B.4 rule 3 in the (x, z) plane: the lowest `(x, z)` vertex first,
/// then counter-clockwise viewed from +y, convex. Viewed from +y the (x, z)
/// basis is left-handed, so every turn `cross2(e_i, e_i+1) = e.x*f.z - e.z*f.x`
/// is `<= 0` and at least one is `< 0` (the §E example square gives -40000 at
/// each corner). `i128` products: no overflow, no float.
fn canonical_convex_ccw(polygon: &[NavVertex]) -> bool {
    let Some(first) = polygon.first() else { return false };
    if polygon[1..].iter().any(|v| (v.x_cm, v.z_cm) <= (first.x_cm, first.z_cm)) {
        return false;
    }
    let n = polygon.len();
    let edge = |i: usize| {
        let (p, q) = (&polygon[i % n], &polygon[(i + 1) % n]);
        (i128::from(q.x_cm) - i128::from(p.x_cm), i128::from(q.z_cm) - i128::from(p.z_cm))
    };
    let turns: Vec<i128> = (0..n).map(|i| {
        let (e, f) = (edge(i), edge(i + 1));
        e.0 * f.1 - e.1 * f.0
    }).collect();
    turns.iter().all(|t| *t <= 0) && turns.iter().any(|t| *t < 0)
}

/// Card 52 §A's entry point over a loaded `.simnav`: `flags` bit0
/// `FLAG_ALLOW_WATER` (other bits `NavFormatUnsupported`), obstacles blocked
/// by the card 51 §C.3 AABB rule, `lock_key`s resolved against `keys_held`.
/// Pure: same bytes, obstacles, keys and flags give the identical plan. The
/// plan's `sim` is `SimId(0)`; the caller that owns a Sim stamps its own.
pub fn plan_route(nav: &SimNav, start_cell: u32, goal_cell: u32,
    obstacles: &[DynamicObstacle], keys_held: &[u16], flags: u32)
    -> Result<RoutePlan, KernelError> {
    if flags & !FLAG_ALLOW_WATER != 0 {
        return Err(KernelError::NavFormatUnsupported);
    }
    let graph = nav.to_graph(keys_held);
    let blocked = FootprintBlocker::from_obstacles(&graph, &nav.templates, obstacles)?;
    graph.plan_route_with(start_cell, goal_cell, SimId(0), &blocked, flags)
}

// ---------------------------------------------------------------------------
// Card 51 §E: the canonical JSON sibling. Integers only, keys in binary field
// order, header counts and `content_hash` derived (never authored), reserved
// fields omitted (always 0). `from_canonical_json` is the authoring path the
// `simnav_compile` bin uses; `to_canonical_json` re-emits the canonical text.
// ---------------------------------------------------------------------------

enum Json { Int(i128), Arr(Vec<Json>), Obj(Vec<(String, Json)>) }

struct JsonParser<'a> { s: &'a [u8], at: usize }

impl JsonParser<'_> {
    const MAX_DEPTH: u32 = 8;

    fn ws(&mut self) {
        while matches!(self.s.get(self.at), Some(b' ' | b'\n' | b'\r' | b'\t')) { self.at += 1; }
    }

    fn eat(&mut self, byte: u8) -> Result<(), KernelError> {
        self.ws();
        if self.s.get(self.at) == Some(&byte) {
            self.at += 1;
            Ok(())
        } else {
            Err(KernelError::NavFormatUnsupported)
        }
    }

    fn value(&mut self, depth: u32) -> Result<Json, KernelError> {
        use KernelError::NavFormatUnsupported as Bad;
        if depth > Self::MAX_DEPTH {
            return Err(Bad);
        }
        self.ws();
        match self.s.get(self.at) {
            Some(b'{') => {
                self.at += 1;
                let mut fields = Vec::new();
                self.ws();
                if self.s.get(self.at) == Some(&b'}') {
                    self.at += 1;
                    return Ok(Json::Obj(fields));
                }
                loop {
                    self.eat(b'"')?;
                    let start = self.at;
                    while matches!(self.s.get(self.at), Some(b'a'..=b'z' | b'_' | b'0'..=b'9')) {
                        self.at += 1;
                    }
                    let key = String::from_utf8(self.s[start..self.at].to_vec()).map_err(|_| Bad)?;
                    self.eat(b'"')?;
                    self.eat(b':')?;
                    fields.push((key, self.value(depth + 1)?));
                    self.ws();
                    match self.s.get(self.at) {
                        Some(b',') => self.at += 1,
                        Some(b'}') => { self.at += 1; return Ok(Json::Obj(fields)); }
                        _ => return Err(Bad),
                    }
                }
            }
            Some(b'[') => {
                self.at += 1;
                let mut items = Vec::new();
                self.ws();
                if self.s.get(self.at) == Some(&b']') {
                    self.at += 1;
                    return Ok(Json::Arr(items));
                }
                loop {
                    items.push(self.value(depth + 1)?);
                    self.ws();
                    match self.s.get(self.at) {
                        Some(b',') => self.at += 1,
                        Some(b']') => { self.at += 1; return Ok(Json::Arr(items)); }
                        _ => return Err(Bad),
                    }
                }
            }
            Some(b'-' | b'0'..=b'9') => {
                let negative = self.s[self.at] == b'-';
                if negative { self.at += 1; }
                let start = self.at;
                let mut v: i128 = 0;
                while let Some(d @ b'0'..=b'9') = self.s.get(self.at).copied() {
                    v = v.checked_mul(10).and_then(|v| v.checked_add(i128::from(d - b'0')))
                        .ok_or(Bad)?;
                    self.at += 1;
                }
                if self.at == start {
                    return Err(Bad);
                }
                Ok(Json::Int(if negative { -v } else { v }))
            }
            _ => Err(Bad),
        }
    }
}

fn json_fields<'a>(v: &'a Json, keys: &[&str]) -> Result<Vec<&'a Json>, KernelError> {
    match v {
        Json::Obj(fields) if fields.len() == keys.len()
            && fields.iter().zip(keys).all(|((k, _), want)| k == want) =>
            Ok(fields.iter().map(|(_, v)| v).collect()),
        _ => Err(KernelError::NavFormatUnsupported),
    }
}

fn json_arr(v: &Json) -> Result<&[Json], KernelError> {
    match v { Json::Arr(items) => Ok(items), _ => Err(KernelError::NavFormatUnsupported) }
}

fn json_int<T: TryFrom<i128>>(v: &Json) -> Result<T, KernelError> {
    match v {
        Json::Int(i) => T::try_from(*i).map_err(|_| KernelError::NavFormatUnsupported),
        _ => Err(KernelError::NavFormatUnsupported),
    }
}

fn json_ints<const N: usize, T: TryFrom<i128> + Copy + Default>(v: &Json)
    -> Result<[T; N], KernelError> {
    let items = json_arr(v)?;
    if items.len() != N {
        return Err(KernelError::NavFormatUnsupported);
    }
    let mut out = [T::default(); N];
    for (slot, item) in out.iter_mut().zip(items) { *slot = json_int(item)?; }
    Ok(out)
}

fn emit_list(out: &mut String, key: &str, items: &[String], last: bool) {
    out.push_str(&format!("  \"{key}\": "));
    if items.is_empty() {
        out.push_str("[]");
    } else {
        out.push_str("[\n");
        out.push_str(&items.iter().map(|i| format!("    {i}")).collect::<Vec<_>>().join(",\n"));
        out.push_str("\n  ]");
    }
    out.push_str(if last { "\n" } else { ",\n" });
}

impl SimNav {
    /// Parse the canonical JSON form (card 51 §E). Keys must appear exactly
    /// and in binary field order; every number is an integer in its field's
    /// range. The result is not yet validated: compile it with
    /// [`SimNav::to_bytes`] and [`SimNav::load`] the bytes, as the
    /// `simnav_compile` bin does.
    pub fn from_canonical_json(text: &str) -> Result<SimNav, KernelError> {
        let mut parser = JsonParser { s: text.as_bytes(), at: 0 };
        let root = parser.value(0)?;
        parser.ws();
        if parser.at != parser.s.len() {
            return Err(KernelError::NavFormatUnsupported);
        }
        let top = json_fields(&root, &["format_version", "flags", "lot_id", "world_seed",
            "rooms", "cells", "portals", "obstacle_templates", "vertices"])?;
        let mut nav = SimNav { header: NavHeader { magic: *NAV_MAGIC,
            format_version: json_int(top[0])?, flags: json_int(top[1])?,
            lot_id: json_int(top[2])?, world_seed: json_int(top[3])?, ..NavHeader::default() },
            ..SimNav::default() };
        for v in json_arr(top[4])? {
            let f = json_fields(v, &["room_id", "level", "portal_first", "portal_len",
                "cell_first", "cell_len"])?;
            nav.rooms.push(NavRoom { room_id: json_int(f[0])?, level: json_int(f[1])?,
                portal_first: json_int(f[2])?, portal_len: json_int(f[3])?,
                cell_first: json_int(f[4])?, cell_len: json_int(f[5])?, reserved: 0 });
        }
        for v in json_arr(top[5])? {
            let f = json_fields(v, &["cell_id", "room_id", "vertex_first", "vertex_len",
                "portal_first", "portal_len", "centroid_cm", "flags"])?;
            nav.cells.push(NavCell { cell_id: json_int(f[0])?, room_id: json_int(f[1])?,
                vertex_first: json_int(f[2])?, vertex_len: json_int(f[3])?,
                portal_first: json_int(f[4])?, portal_len: json_int(f[5])?,
                centroid_cm: json_ints(f[6])?, flags: json_int(f[7])? });
        }
        for v in json_arr(top[6])? {
            let f = json_fields(v, &["portal_id", "cell_a", "cell_b", "path_type", "door_id",
                "jamb_a_cm", "jamb_b_cm", "portal_y_cm", "width_cm", "lock_key"])?;
            nav.portals.push(NavPortal { portal_id: json_int(f[0])?, cell_a: json_int(f[1])?,
                cell_b: json_int(f[2])?, path_type: json_int(f[3])?, door_id: json_int(f[4])?,
                jamb_a_cm: json_ints(f[5])?, jamb_b_cm: json_ints(f[6])?,
                portal_y_cm: json_int(f[7])?, width_cm: json_int(f[8])?,
                lock_key: json_int(f[9])? });
        }
        for v in json_arr(top[7])? {
            let f = json_fields(v, &["object_type_key", "min_cm", "max_cm", "scale_x1000"])?;
            nav.templates.push(ObstacleTemplate { object_type_key: json_int(f[0])?,
                min_cm: json_ints(f[1])?, max_cm: json_ints(f[2])?, scale_x1000: json_int(f[3])? });
        }
        for v in json_arr(top[8])? {
            let [x_cm, y_cm, z_cm] = json_ints::<3, i32>(v)?;
            nav.vertices.push(NavVertex { x_cm, y_cm, z_cm });
        }
        Ok(nav)
    }

    /// The canonical JSON text (card 51 §E): fixed key order and layout, one
    /// record per line, LF line ends, trailing newline. A canonical fixture
    /// round-trips byte-identically through parse, compile and re-emit.
    pub fn to_canonical_json(&self) -> String {
        let h = &self.header;
        let mut out = format!("{{\n  \"format_version\": {},\n  \"flags\": {},\n  \"lot_id\": {},\n  \"world_seed\": {},\n",
            h.format_version, h.flags, h.lot_id, h.world_seed);
        let tri = |v: [i32; 3]| format!("[{}, {}, {}]", v[0], v[1], v[2]);
        let duo = |v: [i32; 2]| format!("[{}, {}]", v[0], v[1]);
        emit_list(&mut out, "rooms", &self.rooms.iter().map(|r| format!(
            "{{\"room_id\": {}, \"level\": {}, \"portal_first\": {}, \"portal_len\": {}, \"cell_first\": {}, \"cell_len\": {}}}",
            r.room_id, r.level, r.portal_first, r.portal_len, r.cell_first, r.cell_len))
            .collect::<Vec<_>>(), false);
        emit_list(&mut out, "cells", &self.cells.iter().map(|c| format!(
            "{{\"cell_id\": {}, \"room_id\": {}, \"vertex_first\": {}, \"vertex_len\": {}, \"portal_first\": {}, \"portal_len\": {}, \"centroid_cm\": {}, \"flags\": {}}}",
            c.cell_id, c.room_id, c.vertex_first, c.vertex_len, c.portal_first, c.portal_len,
            tri(c.centroid_cm), c.flags)).collect::<Vec<_>>(), false);
        emit_list(&mut out, "portals", &self.portals.iter().map(|p| format!(
            "{{\"portal_id\": {}, \"cell_a\": {}, \"cell_b\": {}, \"path_type\": {}, \"door_id\": {}, \"jamb_a_cm\": {}, \"jamb_b_cm\": {}, \"portal_y_cm\": {}, \"width_cm\": {}, \"lock_key\": {}}}",
            p.portal_id, p.cell_a, p.cell_b, p.path_type, p.door_id, duo(p.jamb_a_cm),
            duo(p.jamb_b_cm), p.portal_y_cm, p.width_cm, p.lock_key)).collect::<Vec<_>>(), false);
        emit_list(&mut out, "obstacle_templates", &self.templates.iter().map(|t| format!(
            "{{\"object_type_key\": {}, \"min_cm\": {}, \"max_cm\": {}, \"scale_x1000\": {}}}",
            t.object_type_key, tri(t.min_cm), tri(t.max_cm), t.scale_x1000))
            .collect::<Vec<_>>(), false);
        emit_list(&mut out, "vertices", &self.vertices.iter()
            .map(|v| tri([v.x_cm, v.y_cm, v.z_cm])).collect::<Vec<_>>(), true);
        out.push_str("}\n");
        out
    }
}

/// Golden Sunset Valley multi-lot fixture (cards 51-54 rehearsal graph):
/// Martinez Home (`lot 100`, 5x5 grid at origin (0,0), plus a two-cell annexe
/// room `lot 100` reachable only through the locked interior door `7002`),
/// a street strip (`lot 300`), Central Park (`lot 200`), the front door `7001`
/// carrying the card-02 unsigned witness `0x8A86_3E32`, a plain street portal,
/// and a detached island (`lot 999`, cells 9000..9002) that is unreachable by
/// construction. Authored from the sealed cards' behaviour - not extracted
/// from game files. Every portal cost equals the octile distance between its
/// endpoint centres, so the graph passes card 52 §C.2's consistency check.
pub fn sunset_valley_park_and_home_navmesh() -> NavGraph {
    let mut g = NavGraph::new();
    g.add_grid(100, 1, 1_000, 5, 5, (0, 0));           // Martinez Home
    g.add_grid(300, 3, 3_000, 5, 1, (600, 0));         // street strip
    g.add_grid(200, 2, 2_000, 5, 5, (1_200, 0));       // Central Park
    g.add_grid(100, 4, 1_100, 2, 1, (0, 600));         // home annexe (door-only)
    g.add_grid(999, 9, 9_000, 3, 1, (0, 0));           // detached island
    // The front door (Martinez home (200,0,0) -> street (600,0,0)): card-02's
    // unsigned witness survives verbatim on a door portal (guid 7_001).
    g.add_edge(PortalEdge { from_cell: 1_002, to_cell: 3_000, path_type: 0x8A86_3E32,
        door_guid: 7_001, base_cost_mm: 4_000, ..PortalEdge::default() });
    // Street (1000,0,0) -> Central Park (1200,0,0): a plain portal, high bit clear.
    g.add_edge(PortalEdge { from_cell: 3_004, to_cell: 2_000, path_type: 0x0000_0001,
        base_cost_mm: 2_000, ..PortalEdge::default() });
    // The locked interior door (0,0,200) -> (0,0,600): the ONLY annexe link,
    // so a plan into the annexe is a named PortalLocked, not a silent detour.
    g.add_edge(PortalEdge { from_cell: 1_010, to_cell: 1_100, path_type: 0x8A86_3E32,
        door_guid: 7_002, locked: true, base_cost_mm: 4_000, ..PortalEdge::default() });
    g
}

#[cfg(test)]
mod tests {
    use super::*;

    fn graph() -> NavGraph { sunset_valley_park_and_home_navmesh() }

    #[test]
    fn sunset_fixture_routes() {
        let g = graph();
        assert_eq!(ast_validate_edges(&g), Ok(()));
        // Direct path cost: 4 diagonal steps at the integer STEP_DIAG_MM.
        let plan = g.plan_route(1_000, 1_024, SimId(1)).expect("grid plan");
        assert_eq!(plan.total_cost_mm, 4 * u64::from(STEP_DIAG_MM));
        assert_eq!(plan.legs.len(), 4);
        assert_eq!(plan.legs.iter().map(|l| l.cost_mm).sum::<u64>(), plan.total_cost_mm);

        // The cross-lot plan passes the front door as Approach -> Through -> Exit,
        // and only the Through leg carries the unsigned witness.
        let plan = g.plan_route(1_000, 2_000, SimId(1)).expect("cross-lot plan");
        let legs = g.boundary_legs(&plan);
        let chain: Vec<LegKind> = legs.iter().map(|l| l.kind).collect();
        assert!(chain.windows(3).any(|w| w == [LegKind::Approach, LegKind::Through,
            LegKind::Exit]), "{chain:?}");
        let high: Vec<&RouteLeg> = legs.iter().filter(|l| l.path_type & PATHTYPE_HIGH_BIT != 0)
            .collect();
        assert_eq!(high.len(), 1);
        assert_eq!(high[0].kind, LegKind::Through);
        assert_eq!(high[0].path_type, 0x8A86_3E32);
        assert_eq!(high[0].door_id, 7_001);
        assert_eq!(legs.first().map(|l| l.kind), Some(LegKind::SubPath));
        assert_eq!(legs.last().map(|l| l.kind), Some(LegKind::SeatHandoff));
        assert_eq!(legs.len(), plan.legs.len() + 1);
        assert_eq!(plan.legs.iter().map(|l| l.cost_mm).sum::<u64>(), plan.total_cost_mm);

        // Locked-door refusal is named; unlocking opens 2000 + (4000 + 250) + 1000.
        assert_eq!(g.plan_route(1_000, 1_101, SimId(1)).err(), Some(KernelError::PortalLocked));
        let mut open = graph();
        open.add_edge(PortalEdge { from_cell: 1_010, to_cell: 1_100, path_type: 0x8A86_3E32,
            door_guid: 7_002, base_cost_mm: 4_000, ..PortalEdge::default() });
        let unlocked = open.plan_route(1_000, 1_101, SimId(1)).expect("via unlocked door");
        assert_eq!(unlocked.total_cost_mm, 7_250);

        // Dynamic footprint detour: blocking the mid cell forces the diagonals.
        let blocked = FootprintBlocker::footprint_blocked_cells(&g, [(100u32, 1, 0)]);
        assert_eq!(blocked.blocked_cells.iter().copied().collect::<Vec<_>>(), vec![1_001]);
        let detour = g.plan_route_blocked(1_000, 1_002, SimId(1), &blocked).expect("detour");
        assert_eq!(detour.total_cost_mm, 2 * u64::from(STEP_DIAG_MM));
        assert_eq!(detour.cells, vec![1_000, 1_006, 1_002]);

        // Unreachable island and a missing goal are both RouteGoalUnreachable.
        assert_eq!(g.plan_route(1_000, 9_001, SimId(1)).err(),
            Some(KernelError::RouteGoalUnreachable));
        assert_eq!(g.plan_route(1_000, 4_242, SimId(1)).err(),
            Some(KernelError::RouteGoalUnreachable));
    }

    #[test]
    fn tie_break_is_strict_and_reproducible() {
        let g = graph();
        // (0,0) -> (100,200) = cell 1_011: straight-then-diagonal (via 1_005)
        // and diagonal-then-straight (via 1_006) tie at equal f; the strict
        // (f, g, cell) min-heap pops the smaller-g 1_005 first and fixes the
        // golden route reproducibly.
        let a = g.plan_route(1_000, 1_011, SimId(1)).expect("tie route");
        let b = g.plan_route(1_000, 1_011, SimId(1)).expect("tie route again");
        assert_eq!(a, b, "the planner is a pure function of the graph");
        assert_eq!(a.total_cost_mm, u64::from(STEP_STRAIGHT_MM) + u64::from(STEP_DIAG_MM));
        assert_eq!(a.cells, vec![1_000, 1_005, 1_011]);
        assert_eq!(NavGraph::plan_hash(&a), NavGraph::plan_hash(&b));
    }

    #[test]
    fn out_of_bounds_and_blocked_start_are_named() {
        let g = graph();
        assert_eq!(g.plan_route(4242, 1_001, SimId(1)).err(),
            Some(KernelError::RouteStartOutOfBounds));
        let blocked = FootprintBlocker { blocked_cells: [1_000].into_iter().collect() };
        assert_eq!(g.plan_route_blocked(1_000, 1_002, SimId(1), &blocked).err(),
            Some(KernelError::RouteStartOutOfBounds));
        let mut no_spawn = graph();
        if let Some(c) = no_spawn.cells.get_mut(&1_000) { c.flags = CELL_NO_SPAWN; }
        assert_eq!(no_spawn.plan_route(1_000, 1_002, SimId(1)).err(),
            Some(KernelError::RouteStartOutOfBounds));
        // A no-spawn cell is still walkable through.
        assert!(no_spawn.plan_route(1_001, 1_000, SimId(1)).is_ok());
    }

    #[test]
    fn canary01_pathdata_conversion_keeps_the_witness_and_drops_padding() {
        let g = graph();
        let plan = g.plan_route(1_002, 3_000, SimId(1)).expect("front-door route");
        let words = g.to_path_data(&plan);
        // The door arrival waypoint carries the unsigned witness and guid.
        let witness = words.iter().find(|w| w.path_type == 0x8A86_3E32)
            .expect("witness survives in defined words");
        assert_eq!(witness.object_id, 7_001);
        // The owned PathData has no padding SLOTS (canary 01's Kani result):
        // six defined words, smaller than the 32-byte raw record.
        assert!(std::mem::size_of::<PathData>() < 32);
        assert_eq!(words.len(), plan.cells.len());
    }

    #[test]
    fn octile_is_three_d_and_overflow_guarded() {
        // Card 52 §L: y counts, so a 300 cm rise is 3000 mm.
        assert_eq!(ast_octile_mm((0, 0, 0), (0, 300, 0)), 3_000);
        assert_eq!(ast_octile_mm((0, 0, 0), (100, 0, 100)), u64::from(STEP_DIAG_MM));
        // diagonal_overflow_guarded: the i32 extremes on all axes, no wrap.
        let far = ast_octile_mm((i32::MIN, i32::MIN, i32::MIN), (i32::MAX, i32::MAX, i32::MAX));
        let span = u64::from(u32::MAX) * MM_PER_CM;
        assert!(far > span && far < 2 * span, "{far}");
    }

    #[test]
    fn water_is_blocked_unless_allowed_then_doubled() {
        let mut g = NavGraph::new();
        g.add_cell(1, NavCell::at(0, 0, (0, 0, 0), 0));
        g.add_cell(1, NavCell::at(1, 0, (100, 0, 0), CELL_TERRAIN_WATER));
        g.add_edge(PortalEdge { from_cell: 0, to_cell: 1, base_cost_mm: 1_000,
            ..PortalEdge::default() });
        let none = FootprintBlocker::default();
        assert_eq!(g.plan_route_with(0, 1, SimId(1), &none, 0).err(),
            Some(KernelError::RouteGoalUnreachable));
        let wet = g.plan_route_with(0, 1, SimId(1), &none, FLAG_ALLOW_WATER).expect("wading");
        assert_eq!(wet.total_cost_mm, 2_000);
        assert_eq!(wet.legs.iter().map(|l| l.cost_mm).sum::<u64>(), 2_000);
        assert_eq!(g.plan_route_with(0, 1, SimId(1), &none, 2).err(),
            Some(KernelError::NavFormatUnsupported), "reserved plan flag bit");
    }
}

/// Card 51 §G and card 52 §H over the authored `.simnav` fixtures
/// (`fixtures/simnav/*.simnav.json`, hand-written arithmetic, nothing from
/// game files).
#[cfg(test)]
mod simnav {
    use super::*;

    const TWO_ROOM_DOOR: &str = include_str!("../fixtures/simnav/two_room_door.simnav.json");
    const CORRIDOR_DETOUR: &str =
        include_str!("../fixtures/simnav/corridor_with_detour.simnav.json");

    /// Card 51 §D content hashes of the compiled fixtures, pinned.
    const TWO_ROOM_DOOR_HASH: u64 = 0x970A_A1FB_290C_4300;
    const CORRIDOR_DETOUR_HASH: u64 = 0xB408_4BC4_E381_F7AA;

    fn compile(text: &str) -> (SimNav, Vec<u8>) {
        let nav = SimNav::from_canonical_json(text).expect("canonical json");
        let bytes = nav.to_bytes();
        let loaded = SimNav::load(&bytes).expect("fixture loads");
        (loaded, bytes)
    }

    /// Recompile a mutated nav so the content hash is right and a later check fires.
    fn rehash(nav: &SimNav) -> Vec<u8> { nav.to_bytes() }

    #[test]
    fn nav_header_offsets_asserted() {
        // The const offset_of!/size_of! assertions above compile only if the
        // layout matches card 51 §B; this test pins the table strides too.
        assert_eq!((size_of::<NavHeader>(), size_of::<NavRoom>(), size_of::<NavCell>(),
            size_of::<NavPortal>(), size_of::<ObstacleTemplate>(), size_of::<NavVertex>()),
            (64, 16, 32, 48, 32, 12));
    }

    #[test]
    fn simnav_roundtrip_hash_stable() {
        for (text, pinned) in [(TWO_ROOM_DOOR, TWO_ROOM_DOOR_HASH),
            (CORRIDOR_DETOUR, CORRIDOR_DETOUR_HASH)] {
            let (nav, bytes) = compile(text);
            assert_eq!(nav.header.content_hash, pinned);
            assert_eq!(nav.to_bytes(), bytes, "binary re-emits byte-identically");
            assert_eq!(nav.to_canonical_json(), text.replace('\r', ""),
                "the fixture is canonical JSON");
        }
    }

    #[test]
    fn simnav_canonical_order_enforced() {
        let (mut nav, _) = compile(TWO_ROOM_DOOR);
        nav.portals[0].cell_a = 1;
        nav.portals[0].cell_b = 0;
        assert_eq!(SimNav::load(&rehash(&nav)), Err(KernelError::NavFormatUnsupported));
        nav.portals[0].cell_a = 1;
        nav.portals[0].cell_b = 1;
        assert_eq!(SimNav::load(&rehash(&nav)), Err(KernelError::NavFormatUnsupported));
        // Cell vertices starting anywhere but the lowest (x, z) are a different file, refused.
        let (mut nav, _) = compile(TWO_ROOM_DOOR);
        nav.vertices[0..4].rotate_left(1);
        nav.cells[0].centroid_cm = [0, 0, 0];
        assert_eq!(SimNav::load(&rehash(&nav)), Err(KernelError::NavFormatUnsupported));
        // Clockwise winding is refused.
        let (mut nav, _) = compile(TWO_ROOM_DOOR);
        nav.vertices[1..4].reverse();
        assert_eq!(SimNav::load(&rehash(&nav)), Err(KernelError::NavFormatUnsupported));
    }

    #[test]
    fn simnav_centroid_recomputes() {
        let (mut nav, _) = compile(TWO_ROOM_DOOR);
        nav.cells[1].centroid_cm[0] += 1;
        assert_eq!(SimNav::load(&rehash(&nav)), Err(KernelError::NavFormatUnsupported));
    }

    #[test]
    fn simnav_content_hash_mismatch_is_named() {
        let (_, mut bytes) = compile(TWO_ROOM_DOOR);
        let last = bytes.len() - 1;
        bytes[last] ^= 1;
        assert_eq!(SimNav::load(&bytes), Err(KernelError::NavContentHashMismatch));
    }

    #[test]
    fn simnav_header_checks() {
        let (nav, bytes) = compile(TWO_ROOM_DOOR);
        assert_eq!(SimNav::load(&bytes[..63]), Err(KernelError::NavFormatUnsupported));
        assert_eq!(SimNav::load(&bytes[..bytes.len() - 1]),
            Err(KernelError::NavFormatUnsupported), "table length vs counts");
        let mut bad_magic = bytes.clone();
        bad_magic[7] = b'2';
        assert_eq!(SimNav::load(&bad_magic), Err(KernelError::NavFormatUnsupported));
        for mutate in [
            |n: &mut SimNav| n.header.format_version = 2,
            |n: &mut SimNav| n.header.flags |= 8,
            |n: &mut SimNav| n.header.flags &= !NAV_FLAG_PORTALS_BIDIRECTIONAL,
            |n: &mut SimNav| n.header.reserved_must_be_zero = 1,
            |n: &mut SimNav| n.rooms[0].reserved = 1,
            |n: &mut SimNav| n.cells[0].flags |= 8,
            |n: &mut SimNav| n.cells[1].cell_id = 7,
            |n: &mut SimNav| n.cells[0].vertex_len = 2,
            |n: &mut SimNav| n.portals[0].path_type = 0x0000_0002,
            |n: &mut SimNav| n.cells.clear(),
        ] {
            let mut broken = nav.clone();
            mutate(&mut broken);
            assert_eq!(SimNav::load(&rehash(&broken)), Err(KernelError::NavFormatUnsupported));
        }
    }

    #[test]
    fn pathtype_portal_is_unsigned() {
        let (mut nav, _) = compile(TWO_ROOM_DOOR);
        nav.portals[0].path_type = PATHTYPE_VEHICLE_WITNESS;
        nav.portals[0].lock_key = 0;
        let nav = SimNav::load(&rehash(&nav)).expect("the witness is a known class");
        assert!(nav.portals[0].path_type > 0x7FFF_FFFF);
        let plan = plan_route(&nav, 0, 1, &[], &[], 0).expect("door-shaped");
        assert_eq!(plan.total_cost_mm, 1_250);
        assert_eq!(plan.legs.len(), 3);
        assert_eq!(plan.legs[1].path_type, PATHTYPE_VEHICLE_WITNESS);
    }

    #[test]
    fn avoidance_scale_is_one() {
        let (mut nav, _) = compile(CORRIDOR_DETOUR);
        nav.templates[0].scale_x1000 = 999;
        assert_eq!(SimNav::load(&rehash(&nav)), Err(KernelError::NavFormatUnsupported));
        let (nav, _) = compile(CORRIDOR_DETOUR);
        let obstacle = DynamicObstacle { object_id: 1, template: 0, origin_cm: [100, 0, 0],
            scale_x1000: 10 };
        assert_eq!(plan_route(&nav, 0, 1, &[obstacle], &[], 0).err(),
            Some(KernelError::NavFormatUnsupported));
        let unknown = DynamicObstacle { template: 9, scale_x1000: 1_000, ..obstacle };
        assert_eq!(plan_route(&nav, 0, 1, &[unknown], &[], 0).err(),
            Some(KernelError::NavFormatUnsupported));
    }

    #[test]
    fn hash_covers_seed() {
        let (mut nav, bytes) = compile(TWO_ROOM_DOOR);
        nav.header.world_seed += 1;
        let reseeded = SimNav::load(&rehash(&nav)).expect("still valid");
        assert_ne!(reseeded.header.content_hash, SimNav::load(&bytes).unwrap().header.content_hash);
    }

    /// The loaded door fixture reproduces card 53's locked-door rows: named
    /// `PortalLocked` without the key (K3, `lock_key = 5` resolved against
    /// `keys_held`), and with it the same plan the code-built graph gives.
    #[test]
    fn loaded_door_matches_the_kdiff_door_row() {
        let (nav, _) = compile(TWO_ROOM_DOOR);
        assert_eq!(plan_route(&nav, 0, 1, &[], &[], 0).err(), Some(KernelError::PortalLocked));
        assert_eq!(plan_route(&nav, 0, 1, &[], &[4], 0).err(), Some(KernelError::PortalLocked));
        let plan = plan_route(&nav, 0, 1, &[], &[5], 0).expect("key held");
        let mut code = NavGraph::new();
        code.add_cell(0, NavCell::at(0, 0, (0, 0, 0), 0));
        code.add_cell(0, NavCell::at(1, 0, (100, 0, 0), 0));
        code.add_edge(PortalEdge { from_cell: 0, to_cell: 1, path_type: PATHTYPE_DOOR_PORTAL,
            door_guid: 7, base_cost_mm: 1_000, ..PortalEdge::default() });
        let built = code.plan_route(0, 1, SimId(0)).expect("code-built door");
        assert_eq!((plan.total_cost_mm, plan.legs.len(), plan.path_hash),
            (built.total_cost_mm, built.legs.len(), built.path_hash));
        assert_eq!(plan.legs[1].from_cm, [50, 0, 0], "door centre from the jambs");
        assert_eq!(plan.legs[1].to_cm, [60, 0, 0], "advanced 10 cm along a->b");
    }

    /// K7: a template instance blocks by the card 51 §C.3 AABB test and the
    /// plan re-routes exactly as card 53's `dynamic_footprint_reroute` row.
    #[test]
    fn astar_dynamic_footprint_blocks() {
        let (nav, _) = compile(CORRIDOR_DETOUR);
        let open = plan_route(&nav, 0, 1, &[], &[], 0).expect("open corridor");
        assert_eq!((open.cells.clone(), open.total_cost_mm), (vec![0, 2, 1], 2_000));
        let obstacle = DynamicObstacle { object_id: 42, template: 0, origin_cm: [100, 0, 0],
            scale_x1000: AVOIDANCE_SCALE_X1000 };
        let graph = nav.to_graph(&[]);
        let blocked = FootprintBlocker::from_obstacles(&graph, &nav.templates, &[obstacle])
            .expect("valid obstacle");
        assert_eq!(blocked.blocked_cells.iter().copied().collect::<Vec<_>>(), vec![2]);
        let detour = plan_route(&nav, 0, 1, &[obstacle], &[], 0).expect("detour");
        assert_eq!((detour.cells.clone(), detour.total_cost_mm, detour.path_hash),
            (vec![0, 3, 1], 2_828, 0x3B68_702E_452D_151D));
        // A vertex alone blocks, with inclusive bounds: at origin (185, 0, 70)
        // the expanded box starts at x = 185 - 10 - 25 = 150, exactly the
        // vertex (150, 0, 50) that cells 1 and 2 share; both centroids are
        // outside. One centimetre further and nothing is blocked.
        let blocks = |origin_cm: [i32; 3]| FootprintBlocker::from_obstacles(&graph,
            &nav.templates, &[DynamicObstacle { origin_cm, ..obstacle }])
            .expect("valid obstacle").blocked_cells.into_iter().collect::<Vec<_>>();
        assert_eq!(blocks([185, 0, 70]), vec![1, 2]);
        assert_eq!(blocks([186, 0, 70]), Vec::<u32>::new());
        assert_eq!(plan_route(&nav, 0, 1, &[obstacle], &[], 0),
            plan_route(&nav, 0, 1, &[obstacle], &[], 0), "astar_pure_and_deterministic");
    }

    /// The loader never panics: 4000 SplitMix64 byte mutations of a valid file
    /// each load to a named result.
    #[test]
    fn loader_is_panic_free_on_mutations() {
        let (_, bytes) = compile(CORRIDOR_DETOUR);
        let mut state = 0x5EED_5133_0000_0051u64;
        let mut next = || {
            state = state.wrapping_add(0x9E37_79B9_7F4A_7C15);
            let mut z = state;
            z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
            z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
            z ^ (z >> 31)
        };
        for _ in 0..4_000 {
            let mut m = bytes.clone();
            let cut = usize::try_from(next() % (m.len() as u64 + 1)).unwrap_or(0);
            if next() % 5 == 0 { m.truncate(cut); }
            for _ in 0..(next() % 4 + 1) {
                if m.is_empty() { break; }
                let at = usize::try_from(next() % m.len() as u64).unwrap_or(0);
                m[at] = (next() & 0xFF) as u8;
            }
            // Recompute the hash half the time, so checks 7+ are reached too.
            if next() % 2 == 0 && m.len() >= NAV_HEADER_SIZE {
                m[0x38..0x40].fill(0);
                let h = fnv1a64(PATH_HASH_FNV_OFFSET, &m);
                m[0x38..0x40].copy_from_slice(&h.to_le_bytes());
            }
            match SimNav::load(&m) {
                Ok(nav) => { let _ = plan_route(&nav, 0, 1, &[], &[], 0); }
                Err(e) => assert!(matches!(e, KernelError::NavFormatUnsupported
                    | KernelError::NavContentHashMismatch), "{e:?}"),
            }
        }
    }
}

/// Canary 31 kernel differential (card 53 §G): the golden corpus of card 53
/// §C, rebuilt on this kernel's own types and planned by this kernel, each
/// row printed as a `KDIFF|canary=31|...` line and asserted against the
/// pinned cost, leg count, door count, cell path and `path_hash`.
///
/// The four door rows carry the referee's re-pin (`f8f3ee50`, ruling R1,
/// Claude Main 2026-11-01): the original pins came from the referee oracle's
/// `ast_advanced_cm` defect (it returned the 10 cm step instead of
/// `from + step`); the oracle was fixed, and this kernel keeps card 52 §F.
#[cfg(test)]
mod kdiff {
    use super::*;

    fn portal(g: &mut NavGraph, a: u32, b: u32, path_type: u32, door: u64, locked: bool,
        cost: u64) {
        g.add_edge(PortalEdge { from_cell: a, to_cell: b, path_type, door_guid: door, locked,
            base_cost_mm: cost, ..PortalEdge::default() });
    }

    fn line(g: &[(u32, i32, i32, i32)]) -> NavGraph {
        let mut n = NavGraph::new();
        for (id, x, y, z) in g { n.add_cell(0, NavCell::at(*id, *id / 3, (*x, *y, *z), 0)); }
        n
    }

    type Expect = Result<(u64, usize, usize, u64, Vec<u32>), KernelError>;
    type GoldenCase = (&'static str, NavGraph, u32, u32, Vec<u32>, Expect);

    /// Card 53 §C, in its row order: (case, graph, start, goal, blocked, expected).
    fn golden() -> Vec<GoldenCase> {
        let mut out = Vec::new();

        let mut n = line(&[(0, 0, 0, 0), (1, 100, 0, 0)]);
        portal(&mut n, 0, 1, 0, 0, false, 1000);
        out.push(("two_cell_flat_subpath", n, 0, 1, vec![],
            Ok((1000, 1, 0, 0xAAE3_DE62_7131_BDBA, vec![0, 1]))));

        let mut n = line(&[(0, 0, 0, 0), (1, 100, 0, 0), (2, 200, 0, 0)]);
        portal(&mut n, 0, 1, PATHTYPE_HIGH_BIT, 7, false, 1000);
        portal(&mut n, 1, 2, PATHTYPE_HIGH_BIT, 8, false, 1000);
        out.push(("three_room_door_chain", n, 0, 2, vec![],
            Ok((2500, 6, 2, 0x9F54_F198_65B4_FC6E, vec![0, 1, 2]))));

        let mut n = line(&[(0, 0, 0, 0), (1, 0, 150, 0), (2, 0, 300, 0)]);
        portal(&mut n, 0, 1, 0, 0, false, 1500);
        portal(&mut n, 1, 2, 0, 0, false, 1500);
        portal(&mut n, 0, 2, 0, 0, false, 3200);
        out.push(("multi_floor_stair_vs_lift", n, 0, 2, vec![],
            Ok((3000, 2, 0, 0x01DA_34F8_F223_83CB, vec![0, 1, 2]))));

        let mut n = line(&[(0, 0, 0, 0), (1, 500, 0, 0), (2, 1000, 0, 0)]);
        portal(&mut n, 0, 1, 0, 0, false, 5000);
        portal(&mut n, 1, 2, PATHTYPE_HIGH_BIT, 99, false, 5000);
        out.push(("cross_lot_boundary_gate", n, 0, 2, vec![],
            Ok((10250, 4, 1, 0x9947_BB36_E58E_20A3, vec![0, 1, 2]))));

        let mut n = line(&[(0, 0, 0, 0), (1, 200, 0, 0), (2, 100, 0, 0), (3, 100, 100, 0)]);
        portal(&mut n, 0, 2, 0, 0, false, 1000);
        portal(&mut n, 2, 1, 0, 0, false, 1000);
        portal(&mut n, 0, 3, 0, 0, false, 1414);
        portal(&mut n, 3, 1, 0, 0, false, 1414);
        out.push(("dynamic_footprint_reroute", n, 0, 1, vec![2],
            Ok((2828, 2, 0, 0x3B68_702E_452D_151D, vec![0, 3, 1]))));

        // lock_key = 5 resolved against keys_held when the graph is built (delta K3).
        let mut n = line(&[(0, 0, 0, 0), (1, 100, 0, 0)]);
        portal(&mut n, 0, 1, PATHTYPE_HIGH_BIT, 7, true, 1000);
        out.push(("locked_door_is_named", n, 0, 1, vec![], Err(KernelError::PortalLocked)));
        let mut n = line(&[(0, 0, 0, 0), (1, 100, 0, 0)]);
        portal(&mut n, 0, 1, PATHTYPE_HIGH_BIT, 7, false, 1000);
        out.push(("locked_door_with_key", n, 0, 1, vec![],
            Ok((1250, 3, 1, 0x0AB8_AB25_FB28_0BC8, vec![0, 1]))));

        let mut n = line(&[(0, 0, 0, 0), (1, 100, 0, 0), (2, 900, 0, 900)]);
        portal(&mut n, 0, 1, 0, 0, false, 1000);
        out.push(("unreachable_island", n, 0, 2, vec![],
            Err(KernelError::RouteGoalUnreachable)));

        let mut n = line(&[(0, 0, 0, 0), (1, 100, 0, 0)]);
        if let Some(c) = n.cells.get_mut(&0) { c.flags = CELL_OUT_OF_BOUNDS; }
        portal(&mut n, 0, 1, 0, 0, false, 1000);
        out.push(("start_out_of_bounds", n, 0, 1, vec![],
            Err(KernelError::RouteStartOutOfBounds)));

        let mut n = NavGraph::new();
        for r in 0..3u32 { for c in 0..3u32 {
            n.add_cell(0, NavCell::at(r * 3 + c, (r * 3 + c) / 3, ((c * 100) as i32, 0,
                (r * 100) as i32), 0));
        } }
        for r in 0..3u32 { for c in 0..3u32 {
            let id = r * 3 + c;
            if c + 1 < 3 { portal(&mut n, id, id + 1, 0, 0, false, 1000); }
            if r + 1 < 3 { portal(&mut n, id, id + 3, 0, 0, false, 1000); }
        } }
        out.push(("symmetric_corridor_tie_break", n, 0, 8, vec![],
            Ok((4000, 4, 0, 0x65CE_06A8_D62F_2F3D, vec![0, 1, 4, 5, 8]))));

        let mut n = line(&[(0, 0, 0, 0), (1, 100, 0, 0)]);
        portal(&mut n, 0, 1, 0x8A86_3E32, 0, false, 1000);
        out.push(("unsigned_pathtype_witness_cost", n, 0, 1, vec![],
            Ok((1250, 3, 1, 0xF932_9FE6_8236_10F8, vec![0, 1]))));
        out
    }

    #[test]
    fn kdiff_canary31_golden_corpus() {
        let corpus = golden();
        assert_eq!(corpus.len(), 11, "card 53 §C: eleven rows for ten numbered vectors");
        let mut mismatches = Vec::new();
        for (case, graph, start, goal, blocked, expect) in corpus {
            let blocker = FootprintBlocker { blocked_cells: blocked.into_iter().collect() };
            let got = graph.plan_route_blocked(start, goal, SimId(1), &blocker);
            let again = graph.plan_route_blocked(start, goal, SimId(1), &blocker);
            assert_eq!(got, again, "{case}: astar_pure_and_deterministic");
            let observed = got.map(|p| {
                assert_eq!(p.legs.len(), p.cells.len() - 1 + 2 * p.door_crossings.len(),
                    "{case}: legs == edges + 2*doors");
                assert_eq!(p.legs.iter().map(|l| l.cost_mm).sum::<u64>(), p.total_cost_mm);
                (p.total_cost_mm, p.legs.len(), p.door_crossings.len(), p.path_hash, p.cells)
            });
            match &observed {
                Ok((cost, legs, doors, hash, _)) => println!(
                    "KDIFF|canary=31|case={case}|got={cost:016x}|legs={legs}|doors={doors}|hash={hash:016x}"),
                Err(err) => println!("KDIFF|canary=31|case={case}|got={err:?}"),
            }
            if observed != expect {
                mismatches.push(format!("{case}: got {observed:x?}, card 53 pins {expect:x?}"));
            }
        }
        assert!(mismatches.is_empty(), "{mismatches:#?}");
    }

    /// Card 53 §E witness 10: two plans differing only in `path_type` hash apart.
    #[test]
    fn kdiff_canary31_hash_sensitivity() {
        let plan_with = |path_type: u32| {
            let mut n = line(&[(0, 0, 0, 0), (1, 100, 0, 0)]);
            portal(&mut n, 0, 1, path_type, 7, false, 1000);
            n.plan_route(0, 1, SimId(1)).expect("door plan")
        };
        let (a, b) = (plan_with(PATHTYPE_HIGH_BIT), plan_with(0x8A86_3E32));
        assert_eq!(a.total_cost_mm, b.total_cost_mm);
        assert_ne!(a.path_hash, b.path_hash);
        println!("KDIFF|canary=31|case=hash_sensitivity|got={:016x}", a.path_hash ^ b.path_hash);
    }

    /// Card 53 §E witness 12 (`astar_cost_consistency_rejects`): a portal priced
    /// below its octile distance is refused by name, never planned through.
    #[test]
    fn kdiff_canary31_cost_consistency_rejects() {
        let mut n = line(&[(0, 0, 0, 0), (1, 0, 300, 0)]);
        portal(&mut n, 0, 1, 0, 0, false, 1000);
        assert_eq!(ast_validate_edges(&n), Err(KernelError::NavFormatUnsupported));
        let got = n.plan_route(0, 1, SimId(1)).err();
        assert_eq!(got, Some(KernelError::NavFormatUnsupported));
        println!("KDIFF|canary=31|case=astar_cost_consistency_rejects|got={:?}",
            KernelError::NavFormatUnsupported);
    }

    /// SplitMix64 (golden gamma 0x9E37_79B9_7F4A_7C15), the corpus generator of card 53 §F.
    struct SplitMix64(u64);
    impl SplitMix64 {
        fn next_u64(&mut self) -> u64 {
            self.0 = self.0.wrapping_add(0x9E37_79B9_7F4A_7C15);
            let mut z = self.0;
            z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
            z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
            z ^ (z >> 31)
        }
        fn below(&mut self, n: u32) -> u32 { (self.next_u64() >> 32) as u32 % n }
    }

    /// Card 53 §F's fuzz invariants over this kernel: 10 000 load-consistent
    /// random graphs, each planned twice. Deterministic, contiguous, never
    /// below the octile distance, `legs == edges + 2*doors`, failures named,
    /// 0 panics.
    #[test]
    fn kdiff_canary31_fuzz_10000() {
        let name = "CleanRoomAStarAndPortalPlanner";
        let mut rng = SplitMix64((0x5EED_1234_ABCD_9876u64 + 30) ^ name.len() as u64);
        let path_types = [0u32, PATHTYPE_HIGH_BIT, 0x8A86_3E32, 0x0000_0001];
        let mut planned = 0u32;
        for _ in 0..10_000 {
            let n = 3 + rng.below(12);
            let mut g = NavGraph::new();
            for id in 0..n {
                let flags = if rng.below(9) != 0 { 0 } else { CELL_OUT_OF_BOUNDS };
                g.add_cell(0, NavCell::at(id, id / 3, ((id * 97 % 512) as i32,
                    (id * 41 % 64) as i32, (id * 71 % 512) as i32), flags));
            }
            let mut next_cost = 1u64;
            for i in 0..n - 1 {
                let dist = ast_octile_mm(g.cells[&i].centroid(), g.cells[&(i + 1)].centroid());
                next_cost = next_cost.wrapping_mul(3).max(dist).max(1);
                let path_type = path_types[rng.below(4) as usize];
                portal(&mut g, i, i + 1, path_type, u64::from(i), i % 3 != 0, next_cost);
            }
            let (start, goal) = (rng.below(n), rng.below(n));
            let a = g.plan_route(start, goal, SimId(1));
            assert_eq!(a, g.plan_route(start, goal, SimId(1)));
            match a {
                Ok(p) => {
                    planned += 1;
                    assert!(p.cells.windows(2).all(|w| g.adjacency[&w[0]].iter()
                        .any(|e| e.to_cell == w[1])), "contiguous");
                    assert_eq!(p.legs.len(), p.cells.len() - 1 + 2 * p.door_crossings.len());
                    assert!(p.total_cost_mm >= ast_octile_mm(g.cells[&start].centroid(),
                        g.cells[&goal].centroid()));
                    assert_eq!(p.path_hash, route_path_hash(&p.legs));
                }
                Err(err) => assert!(matches!(err, KernelError::RouteStartOutOfBounds
                    | KernelError::RouteGoalUnreachable | KernelError::PortalLocked), "{err:?}"),
            }
        }
        println!("KDIFF|canary=31|case=fuzz_10000|got={planned:016x}");
    }
}
