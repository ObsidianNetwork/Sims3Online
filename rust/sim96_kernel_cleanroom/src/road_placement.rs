#![forbid(unsafe_code)]
//! Card 61: validated, authored road topology and deterministic IWorld placement.
use std::collections::{BTreeMap, BTreeSet};
use std::mem::{offset_of, size_of};

pub const ROAD_MAGIC: &[u8; 9] = b"SIMROAD01";
pub const ROAD_SAMPLE_STEP_CM: i64 = 100;
pub const ROAD_DISTANCE_BAND_CM: i64 = 100;
pub const ROAD_MIN_DISTANCE_CM: i64 = 100;
pub const ROAD_COORD_LIMIT_CM: i32 = 1_000_000;

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct RoadHeader {
    pub magic: [u8; 9],
    pub zero: [u8; 7],
    pub version: u32,
    pub flags: u32,
    pub world_key: u64,
    pub node_count: u32,
    pub lane_count: u32,
    pub vertex_count: u32,
    pub reserved: u32,
    pub content_hash: u64,
}
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct RoadNode {
    pub id: u32,
    pub x: i32,
    pub y: i32,
    pub z: i32,
}
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct RoadLane {
    pub id: u32,
    pub kind: u8,
    pub flags: u8,
    pub zero: u16,
    pub from: u32,
    pub to: u32,
    pub width_cm: u32,
    pub first_vertex: u32,
    pub vertex_count: u32,
    pub reserved: u32,
}
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct RoadVertex {
    pub x: i32,
    pub y: i32,
    pub z: i32,
}

const _: () = {
    assert!(size_of::<RoadHeader>() == 56);
    assert!(offset_of!(RoadHeader, magic) == 0);
    assert!(offset_of!(RoadHeader, zero) == 9);
    assert!(offset_of!(RoadHeader, version) == 16);
    assert!(offset_of!(RoadHeader, flags) == 20);
    assert!(offset_of!(RoadHeader, world_key) == 24);
    assert!(offset_of!(RoadHeader, node_count) == 32);
    assert!(offset_of!(RoadHeader, lane_count) == 36);
    assert!(offset_of!(RoadHeader, vertex_count) == 40);
    assert!(offset_of!(RoadHeader, reserved) == 44);
    assert!(offset_of!(RoadHeader, content_hash) == 48);
    assert!(size_of::<RoadNode>() == 16);
    assert!(offset_of!(RoadNode, id) == 0);
    assert!(offset_of!(RoadNode, x) == 4);
    assert!(offset_of!(RoadNode, y) == 8);
    assert!(offset_of!(RoadNode, z) == 12);
    assert!(size_of::<RoadLane>() == 32);
    assert!(offset_of!(RoadLane, id) == 0);
    assert!(offset_of!(RoadLane, kind) == 4);
    assert!(offset_of!(RoadLane, flags) == 5);
    assert!(offset_of!(RoadLane, zero) == 6);
    assert!(offset_of!(RoadLane, from) == 8);
    assert!(offset_of!(RoadLane, to) == 12);
    assert!(offset_of!(RoadLane, width_cm) == 16);
    assert!(offset_of!(RoadLane, first_vertex) == 20);
    assert!(offset_of!(RoadLane, vertex_count) == 24);
    assert!(offset_of!(RoadLane, reserved) == 28);
    assert!(size_of::<RoadVertex>() == 12);
    assert!(offset_of!(RoadVertex, x) == 0);
    assert!(offset_of!(RoadVertex, y) == 4);
    assert!(offset_of!(RoadVertex, z) == 8);
};

type Point = [i32; 3];
#[derive(Clone, Debug, PartialEq, Eq)]
struct Lane {
    id: u32,
    kind: u8,
    flags: u8,
    width: u32,
    points: Vec<Point>,
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RoadSet {
    pub world_key: u64,
    pub content_hash: u64,
    lanes: Vec<Lane>,
    vehicle_lanes: BTreeSet<u32>,
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RoadView {
    pub eye_cm: Point,
    pub forward_ppm: Point,
    pub up_ppm: Point,
    pub fov_mdeg: u32,
    pub aspect_ppm: u32,
    pub near_cm: u32,
    pub far_cm: u32,
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RoadPlaceRequest {
    pub obj: u64,
    pub position_cm: Point,
    pub lane_kind: u8,
    pub distance_cm: i32,
    pub radius_cm: i32,
    pub view: Option<RoadView>,
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RoadPlaceAnswer {
    pub found: bool,
    pub position_cm: Point,
    pub heading_mdeg: i32,
    pub lane_id: u32,
    pub onscreen: bool,
    pub place_hash: u64,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RoadPlaceError { RoadDataMissing, LaneKindUnsupported, BadRequest }

fn fnv(bytes: impl IntoIterator<Item = u8>) -> u64 {
    bytes.into_iter().fold(0xcbf29ce484222325, |h, b|
        (h ^ u64::from(b)).wrapping_mul(0x100000001b3))
}
fn word(bytes: &[u8], at: usize) -> u32 {
    u32::from_le_bytes(bytes[at..at + 4].try_into().expect("checked record extent"))
}
fn point(bytes: &[u8], at: usize) -> Point {
    std::array::from_fn(|i| word(bytes, at + 4 * i) as i32)
}
fn bounded(p: Point) -> bool {
    p.into_iter().all(|v| v.unsigned_abs() <= ROAD_COORD_LIMIT_CM as u32)
}

pub fn load_simroad(bytes: &[u8]) -> Result<RoadSet, RoadPlaceError> {
    use RoadPlaceError::BadRequest;
    if bytes.len() < 56 || &bytes[..9] != ROAD_MAGIC || bytes[9..16] != [0; 7]
        || word(bytes, 16) != 1 || word(bytes, 20) != 0 || word(bytes, 44) != 0 {
        return Err(BadRequest);
    }
    let (nodes, lanes, vertices) = (word(bytes, 32) as usize,
        word(bytes, 36) as usize, word(bytes, 40) as usize);
    if nodes > 65_536 || lanes > 65_536 || vertices > 1_000_000
        || bytes.len() != 56 + nodes * 16 + lanes * 32 + vertices * 12 {
        return Err(BadRequest);
    }
    let content_hash = u64::from_le_bytes(bytes[48..56].try_into().unwrap());
    if fnv(bytes.iter().enumerate().map(|(i, b)| if (48..56).contains(&i) { 0 } else { *b }))
        != content_hash { return Err(BadRequest); }
    let mut node_positions = BTreeMap::new();
    let mut previous = 0;
    for i in 0..nodes {
        let at = 56 + i * 16;
        let id = word(bytes, at);
        let p = point(bytes, at + 4);
        if id <= previous || !bounded(p) { return Err(BadRequest); }
        node_positions.insert(id, p);
        previous = id;
    }
    let mut result = Vec::with_capacity(lanes);
    let mut adjacency: BTreeMap<u32, Vec<(u32, u32, u8)>> = BTreeMap::new();
    let vertex_base = 56 + nodes * 16 + lanes * 32;
    let mut cursor = 0;
    previous = 0;
    for i in 0..lanes {
        let at = 56 + nodes * 16 + i * 32;
        let id = word(bytes, at);
        let (kind, flags) = (bytes[at + 4], bytes[at + 5]);
        let (from, to, width) = (word(bytes, at + 8), word(bytes, at + 12), word(bytes, at + 16));
        let (first, count) = (word(bytes, at + 20) as usize, word(bytes, at + 24) as usize);
        if id <= previous || !matches!(kind, 0 | 1 | 2 | 3 | 5) || flags & !7 != 0
            || (flags & 4 != 0 && (kind != 0 || flags != 6))
            || bytes[at + 6..at + 8] != [0; 2] || word(bytes, at + 28) != 0
            || !(1..=200_000).contains(&width) || first != cursor
            || count < 2 || first > vertices || count > vertices - first {
            return Err(BadRequest);
        }
        let points: Vec<_> = (first..first + count).map(|j| point(bytes, vertex_base + j * 12)).collect();
        if points.iter().any(|p| !bounded(*p)) || node_positions.get(&from) != points.first()
            || node_positions.get(&to) != points.last()
            || points.windows(2).any(|w| w[0][0] == w[1][0] && w[0][2] == w[1][2]) {
            return Err(BadRequest);
        }
        if kind == 0 && flags & 2 != 0 {
            adjacency.entry(from).or_default().push((to, id, flags));
            adjacency.entry(to).or_default().push((from, id, flags));
        }
        result.push(Lane { id, kind, flags, width, points });
        previous = id;
        cursor += count;
    }
    if cursor != vertices { return Err(BadRequest); }
    let mut seen = BTreeSet::new();
    let mut vehicle_lanes = BTreeSet::new();
    let mut best_real: Option<usize> = None;
    // Ascending roots break ties by smallest node; connectors join but do not count.
    for root in adjacency.keys() {
        if !seen.insert(*root) { continue; }
        let mut pending = vec![*root];
        let mut ids = BTreeSet::new();
        let mut real = 0;
        while let Some(node) = pending.pop() {
            for &(next, lane, flags) in &adjacency[&node] {
                if ids.insert(lane) && flags & 4 == 0 { real += 1; }
                if seen.insert(next) { pending.push(next); }
            }
        }
        if best_real.is_none_or(|best| real > best) {
            best_real = Some(real);
            vehicle_lanes = ids;
        }
    }
    Ok(RoadSet { world_key: u64::from_le_bytes(bytes[24..32].try_into().unwrap()),
        content_hash, lanes: result, vehicle_lanes })
}

fn squared(a: Point, b: Point) -> i128 {
    (0..3).map(|i| (i128::from(a[i]) - i128::from(b[i])).pow(2)).sum()
}
fn length_floor(n: i128) -> i64 {
    let (mut low, mut high) = (0i64, 4_000_001i64);
    while high - low > 1 {
        let mid = (low + high) / 2;
        if i128::from(mid).pow(2) <= n { low = mid; } else { high = mid; }
    }
    low
}
fn dot(a: Point, b: Point) -> f64 {
    (0..3).map(|i| f64::from(a[i]) * f64::from(b[i])).sum()
}
fn valid_view(v: &RoadView) -> bool {
    bounded(v.eye_cm)
        && v.forward_ppm.iter().chain(&v.up_ppm).all(|x| x.unsigned_abs() <= 1_000_000)
        && (dot(v.forward_ppm, v.forward_ppm) - 1e12).abs() <= 2e6
        && (dot(v.up_ppm, v.up_ppm) - 1e12).abs() <= 2e6
        && dot(v.forward_ppm, v.up_ppm).abs() <= 1e6
        && (1..180_000).contains(&v.fov_mdeg)
        && (1..=10_000_000).contains(&v.aspect_ppm)
        && v.near_cm < v.far_cm && v.far_cm <= 4_000_000
}
fn visible(v: &RoadView, p: Point) -> bool {
    let delta = std::array::from_fn(|i| p[i] - v.eye_cm[i]);
    let depth = dot(delta, v.forward_ppm) / 1e6;
    let height = dot(delta, v.up_ppm) / 1e6;
    let f = v.forward_ppm.map(|x| f64::from(x) / 1e6);
    let u = v.up_ppm.map(|x| f64::from(x) / 1e6);
    let right = [f[1]*u[2] - f[2]*u[1], f[2]*u[0] - f[0]*u[2], f[0]*u[1] - f[1]*u[0]];
    let side: f64 = (0..3).map(|i| f64::from(delta[i]) * right[i]).sum();
    let half = depth * (f64::from(v.fov_mdeg) * std::f64::consts::PI / 360_000.0).tan();
    depth >= f64::from(v.near_cm) && depth <= f64::from(v.far_cm)
        && height.abs() <= half && side.abs() <= half * f64::from(v.aspect_ppm) / 1e6
}
fn caller_on(lane: &Lane, p: Point) -> bool {
    lane.points.windows(2).any(|w| {
        let d: [i128; 3] = std::array::from_fn(|i| i128::from(w[1][i]) - i128::from(w[0][i]));
        let q: [i128; 3] = std::array::from_fn(|i| i128::from(p[i]) - i128::from(w[0][i]));
        let len: i128 = d.iter().map(|x| x*x).sum();
        let projection: i128 = (0..3).map(|i| q[i]*d[i]).sum();
        let t = projection.clamp(0, len);
        let q2: i128 = q.iter().map(|x| x*x).sum();
        4 * (q2*len - 2*t*projection + t*t) <= i128::from(lane.width).pow(2)*len
    })
}
fn answer_hash(a: &RoadPlaceAnswer) -> u64 {
    let mut bytes = [0u8; 22];
    bytes[0] = u8::from(a.found);
    for (i, x) in a.position_cm.iter().enumerate() {
        bytes[1 + i*4..5 + i*4].copy_from_slice(&x.to_le_bytes());
    }
    bytes[13..17].copy_from_slice(&a.heading_mdeg.to_le_bytes());
    bytes[17..21].copy_from_slice(&a.lane_id.to_le_bytes());
    bytes[21] = u8::from(a.onscreen);
    fnv(bytes)
}
pub fn place_on_road_offscreen(roads: Option<&RoadSet>, request: &RoadPlaceRequest)
    -> Result<RoadPlaceAnswer, RoadPlaceError> {
    use RoadPlaceError::*;
    if !bounded(request.position_cm) || !(0..=1_000_000).contains(&request.distance_cm)
        || !(0..=100_000).contains(&request.radius_cm)
        || (request.obj == 0 && request.radius_cm != 0)
        || request.view.as_ref().is_some_and(|v| !valid_view(v)) { return Err(BadRequest); }
    if request.lane_kind > 5 { return Err(LaneKindUnsupported); }
    let roads = roads.filter(|r| !r.lanes.is_empty()).ok_or(RoadDataMissing)?;
    let mut answer = RoadPlaceAnswer { found: false, position_cm: [0; 3],
        heading_mdeg: 0, lane_id: 0, onscreen: false, place_hash: 0 };
    let caller_valid = request.lane_kind != 0 || roads.lanes.iter().any(|lane|
        roads.vehicle_lanes.contains(&lane.id) && caller_on(lane, request.position_cm));
    let distance = i64::from(request.distance_cm).max(ROAD_MIN_DISTANCE_CM);
    let mut best = None;
    for lane in roads.lanes.iter().filter(|lane| caller_valid
        && lane.flags & 4 == 0
        && (request.lane_kind == lane.kind || request.lane_kind == 4 && matches!(lane.kind, 2 | 3))
        && 2 * i64::from(request.radius_cm) <= i64::from(lane.width)
        && (request.lane_kind != 0 || roads.vehicle_lanes.contains(&lane.id))) {
        let mut arc = 0;
        for (segment, w) in lane.points.windows(2).enumerate() {
            let len = length_floor(squared(w[0], w[1])).max(1);
            let final_endpoint = (segment + 2 == lane.points.len()).then_some(len);
            for offset in (0..len).step_by(ROAD_SAMPLE_STEP_CM as usize).chain(final_endpoint) {
                let p = std::array::from_fn(|i| (i64::from(w[0][i])
                    + (i64::from(w[1][i]) - i64::from(w[0][i])) * offset / len) as i32);
                let error = (length_floor(squared(p, request.position_cm)) - distance).abs();
                if error > ROAD_DISTANCE_BAND_CM { continue; }
                let onscreen = request.view.as_ref().is_some_and(|v| visible(v, p));
                let key = (onscreen, error, lane.id, arc + offset);
                if best.is_some_and(|previous| key >= previous) { continue; }
                best = Some(key);
                answer = RoadPlaceAnswer { found: true, position_cm: p,
                    heading_mdeg: ((f64::from(w[1][0] - w[0][0])
                        .atan2(f64::from(w[1][2] - w[0][2])) * 180_000.0
                        / std::f64::consts::PI).round() as i32).rem_euclid(360_000),
                    lane_id: lane.id, onscreen, place_hash: 0 };
            }
            arc += len;
        }
    }
    answer.place_hash = answer_hash(&answer);
    Ok(answer)
}

// The authoring schema contains only objects, arrays and integer values.
// Reject duplicate/unknown keys and fractional numbers rather than coerce them.
#[derive(Debug)]
enum Json { Object(BTreeMap<String, Json>), Array(Vec<Json>), Integer(String) }
struct JsonParser<'a> { text: &'a [u8], at: usize }
impl JsonParser<'_> {
    fn ws(&mut self) {
        while self.text.get(self.at).is_some_and(|c| matches!(c, b' ' | b'\t' | b'\r' | b'\n')) {
            self.at += 1;
        }
    }
    fn take(&mut self, byte: u8) -> bool {
        self.ws();
        if self.text.get(self.at) == Some(&byte) { self.at += 1; true } else { false }
    }
    fn string(&mut self) -> Result<String, String> {
        if !self.take(b'"') { return Err("expected JSON key".into()); }
        let mut out = String::new();
        loop {
            let b = *self.text.get(self.at).ok_or("unterminated JSON key")?;
            self.at += 1;
            match b {
                b'"' => return Ok(out),
                b'\\' => {
                    let escape = *self.text.get(self.at).ok_or("truncated escape")?;
                    self.at += 1;
                    out.push(match escape {
                        b'"' => '"', b'\\' => '\\', b'/' => '/', b'b' => '\u{8}',
                        b'f' => '\u{c}', b'n' => '\n', b'r' => '\r', b't' => '\t',
                        b'u' => {
                            let digits = self.text.get(self.at..self.at + 4).ok_or("truncated unicode")?;
                            if !digits.iter().all(u8::is_ascii_hexdigit) {
                                return Err("invalid unicode escape".into());
                            }
                            let hex = std::str::from_utf8(digits).map_err(|e| e.to_string())?;
                            let value = u32::from_str_radix(hex, 16).map_err(|e| e.to_string())?;
                            self.at += 4;
                            char::from_u32(value).ok_or("invalid unicode key")?
                        }
                        _ => return Err("invalid JSON escape".into()),
                    });
                }
                0..=31 => return Err("control character in JSON key".into()),
                32..=127 => out.push(char::from(b)),
                _ => return Err("unknown non-ASCII schema key".into()),
            }
        }
    }
    fn value(&mut self, depth: usize) -> Result<Json, String> {
        self.ws();
        if depth > 8 { return Err("JSON nesting exceeds road schema".into()); }
        if self.take(b'{') {
            let mut fields = BTreeMap::new();
            if self.take(b'}') { return Ok(Json::Object(fields)); }
            loop {
                let key = self.string()?;
                if !self.take(b':') { return Err("expected colon".into()); }
                let value = self.value(depth + 1)?;
                if fields.insert(key, value).is_some() { return Err("duplicate JSON key".into()); }
                if self.take(b'}') { break; }
                if !self.take(b',') { return Err("expected comma".into()); }
            }
            Ok(Json::Object(fields))
        } else if self.take(b'[') {
            let mut items = Vec::new();
            if self.take(b']') { return Ok(Json::Array(items)); }
            loop {
                items.push(self.value(depth + 1)?);
                if self.take(b']') { break; }
                if !self.take(b',') { return Err("expected comma".into()); }
            }
            Ok(Json::Array(items))
        } else {
            let start = self.at;
            if self.text.get(self.at) == Some(&b'-') { self.at += 1; }
            let digits = self.at;
            while self.text.get(self.at).is_some_and(u8::is_ascii_digit) { self.at += 1; }
            if self.at == digits || self.at > digits + 1 && self.text[digits] == b'0' {
                return Err("expected canonical integer".into());
            }
            Ok(Json::Integer(std::str::from_utf8(&self.text[start..self.at]).unwrap().into()))
        }
    }
}
fn fields<'a>(v: &'a Json, keys: &[&str]) -> Result<Vec<&'a Json>, String> {
    let Json::Object(map) = v else { return Err("expected object".into()); };
    if map.len() != keys.len() { return Err("unexpected object fields".into()); }
    keys.iter().map(|k| map.get(*k).ok_or_else(|| format!("missing {k}"))).collect()
}
fn array(v: &Json) -> Result<&[Json], String> {
    match v { Json::Array(a) => Ok(a), _ => Err("expected array".into()) }
}
fn integer<T: std::str::FromStr>(v: &Json) -> Result<T, String> {
    match v {
        Json::Integer(s) => s.parse().map_err(|_| "integer out of range".into()),
        _ => Err("expected integer".into()),
    }
}
fn json_point(v: &Json) -> Result<Point, String> {
    let a = array(v)?;
    if a.len() != 3 { return Err("position must have three coordinates".into()); }
    Ok([integer(&a[0])?, integer(&a[1])?, integer(&a[2])?])
}

pub fn compile_simroad_json(json_text: &str) -> Result<Vec<u8>, String> {
    let mut parser = JsonParser { text: json_text.as_bytes(), at: 0 };
    let root = parser.value(0)?;
    parser.ws();
    if parser.at != parser.text.len() { return Err("trailing JSON content".into()); }
    let top = fields(&root, &["world_key", "nodes", "lanes"])?;
    let world_key: u64 = integer(top[0])?;
    let mut nodes = Vec::new();
    for v in array(top[1])? {
        let f = fields(v, &["id", "position_cm"])?;
        nodes.push((integer::<u32>(f[0])?, json_point(f[1])?));
    }
    let mut lanes = Vec::new();
    for v in array(top[2])? {
        let f = fields(v, &["id", "kind", "flags", "from", "to", "width_cm", "vertices_cm"])?;
        let points = array(f[6])?.iter().map(json_point).collect::<Result<Vec<_>, _>>()?;
        lanes.push((integer::<u32>(f[0])?, integer::<u8>(f[1])?, integer::<u8>(f[2])?,
            integer::<u32>(f[3])?, integer::<u32>(f[4])?, integer::<u32>(f[5])?, points));
    }
    nodes.sort_by_key(|n| n.0);
    lanes.sort_by_key(|l| l.0);
    let vertices: usize = lanes.iter().map(|l| l.6.len()).sum();
    if nodes.len() > 65_536 || lanes.len() > 65_536 || vertices > 1_000_000 {
        return Err("road table count limit".into());
    }
    let mut bytes = vec![0u8; 56];
    bytes[..9].copy_from_slice(ROAD_MAGIC);
    bytes[16..20].copy_from_slice(&1u32.to_le_bytes());
    bytes[24..32].copy_from_slice(&world_key.to_le_bytes());
    for (at, count) in [(32, nodes.len()), (36, lanes.len()), (40, vertices)] {
        bytes[at..at + 4].copy_from_slice(&(count as u32).to_le_bytes());
    }
    for (id, p) in nodes {
        bytes.extend(id.to_le_bytes());
        for coordinate in p { bytes.extend(coordinate.to_le_bytes()); }
    }
    let mut first = 0u32;
    for (id, kind, flags, from, to, width, points) in &lanes {
        bytes.extend(id.to_le_bytes());
        bytes.extend([*kind, *flags, 0, 0]);
        for value in [*from, *to, *width, first, points.len() as u32, 0] {
            bytes.extend(value.to_le_bytes());
        }
        first += points.len() as u32;
    }
    for lane in lanes {
        for p in lane.6 { for coordinate in p { bytes.extend(coordinate.to_le_bytes()); } }
    }
    let hash = fnv(bytes.iter().copied());
    bytes[48..56].copy_from_slice(&hash.to_le_bytes());
    load_simroad(&bytes).map_err(|e| format!("{e:?}"))?;
    Ok(bytes)
}

#[cfg(test)]
pub(crate) mod kdiff {
    use super::*;

    #[derive(Clone)]
    struct AuthoredLane { id: u32, kind: u8, flags: u8, from: u32, to: u32, width: u32, points: Vec<Point> }
    fn lane(id: u32, kind: u8, a: Point, b: Point, width: u32) -> AuthoredLane {
        AuthoredLane { id, kind, flags: 2, from: id*2, to: id*2+1, width, points: vec![a, b] }
    }
    fn authored(lanes: &[AuthoredLane]) -> String {
        let mut nodes = BTreeMap::new();
        for l in lanes {
            nodes.insert(l.from, l.points[0]);
            nodes.insert(l.to, *l.points.last().unwrap());
        }
        let nodes = nodes.iter().map(|(id, p)|
            format!(r#"{{"id":{id},"position_cm":{p:?}}}"#)).collect::<Vec<_>>().join(",");
        let lanes = lanes.iter().map(|l| format!(
            r#"{{"id":{},"kind":{},"flags":{},"from":{},"to":{},"width_cm":{},"vertices_cm":{:?}}}"#,
            l.id, l.kind, l.flags, l.from, l.to, l.width, l.points)).collect::<Vec<_>>().join(",");
        format!(r#"{{"world_key":1,"nodes":[{nodes}],"lanes":[{lanes}]}}"#)
    }
    fn compile(lanes: &[AuthoredLane]) -> Vec<u8> {
        compile_simroad_json(&authored(lanes)).unwrap()
    }
    pub(crate) fn straight_bytes() -> Vec<u8> {
        compile(&[lane(1, 0, [0; 3], [2000, 0, 0], 400)])
    }
    fn request(kind: u8) -> RoadPlaceRequest {
        RoadPlaceRequest { obj: 0, position_cm: [0; 3], lane_kind: kind,
            distance_cm: 1000, radius_cm: 0, view: None }
    }
    fn view() -> RoadView {
        RoadView { eye_cm: [0, 0, -1000], forward_ppm: [0, 0, 1_000_000],
            up_ppm: [0, 1_000_000, 0], fov_mdeg: 90_000, aspect_ppm: 1_000_000,
            near_cm: 0, far_cm: 4000 }
    }
    fn answer(position_cm: Point, heading_mdeg: i32, lane_id: u32, onscreen: bool,
        place_hash: u64) -> Result<RoadPlaceAnswer, RoadPlaceError> {
        Ok(RoadPlaceAnswer { found: lane_id != 0, position_cm, heading_mdeg, lane_id, onscreen, place_hash })
    }
    #[test]
    fn canary_33_golden_cases() {
        use RoadPlaceError::*;
        let straight = lane(1, 0, [0; 3], [2000, 0, 0], 400);
        let mut cases = Vec::new();
        cases.push(("straight_two_way", vec![straight.clone()], request(0),
            answer([1000, 0, 0], 90000, 1, false, 0x7825e298d97065c8)));
        let mut branch = lane(2, 0, [0; 3], [0, 0, 2000], 400);
        branch.from = straight.from;
        cases.push(("t_junction_tie", vec![straight.clone(), branch], request(0),
            answer([1000, 0, 0], 90000, 1, false, 0x7825e298d97065c8)));
        let mut hidden = request(2);
        hidden.view = Some(RoadView { fov_mdeg: 30000, ..view() });
        cases.push(("hidden_second", vec![lane(1, 2, [0, 0, 900], [0, 0, 1100], 200),
            lane(2, 2, [900, 0, 0], [1100, 0, 0], 200)], hidden,
            answer([1000, 0, 0], 90000, 2, false, 0x9e328c18515ea4f1)));
        let mut visible = request(2); visible.view = Some(view());
        cases.push(("visible_fallback", vec![lane(1, 2, [0, 0, 900], [0, 0, 1100], 200)],
            visible, answer([0, 0, 1000], 0, 1, true, 0x7138f11c19db9e2f)));
        cases.push(("view_none", vec![lane(1, 2, [0, 0, 900], [0, 0, 1100], 200)],
            request(2), answer([0, 0, 1000], 0, 1, false, 0x7138f01c19db9c7c)));
        let mut fit = request(4); fit.obj = 7; fit.radius_cm = 101;
        cases.push(("wide_footprint", vec![lane(1, 2, [900, 0, 0], [1100, 0, 0], 200),
            lane(2, 3, [900, 0, 0], [1100, 0, 0], 400)], fit,
            answer([1000, 0, 0], 90000, 2, false, 0x9e328c18515ea4f1)));
        let none = answer([0; 3], 0, 0, false, 0xf84841652498787d);
        cases.push(("disconnected_vehicle", vec![lane(1, 0, [0; 3], [0, 0, 100], 400),
            lane(2, 0, [900, 0, 0], [1100, 0, 0], 400)], request(0), none.clone()));
        for (kind, name, id, hash) in [
            (1, "roadside", 2, 0x9e328c18515ea4f1),
            (2, "sidewalk", 3, 0x9183a8edd40f3a8e),
            (3, "footpath", 4, 0xb790526d4bfd79b7),
            (4, "footpath_or_sidewalk", 3, 0x9183a8edd40f3a8e),
            (5, "horse_lane", 6, 0xd0ee18c2469c4e7d),
        ] {
            let lanes = [0, 1, 2, 3, 5].into_iter().map(|k|
                lane(u32::from(k) + 1, k, [900, 0, 0], [1100, 0, 0], 400)).collect();
            cases.push((name, lanes, request(kind), answer([1000, 0, 0], 90000, id, false, hash)));
        }
        cases.push(("no_matching_lane", vec![straight.clone()], request(3), none.clone()));
        cases.push(("empty_set", vec![], request(0), Err(RoadDataMissing)));
        let mut bad = request(0); bad.distance_cm = -1;
        cases.push(("bad_request", vec![straight.clone()], bad, Err(BadRequest)));
        cases.push(("unsupported_kind", vec![straight.clone()], request(6), Err(LaneKindUnsupported)));
        let mut off = request(0); off.position_cm = [0, 0, 1000];
        cases.push(("caller_not_routable", vec![straight], off, none));
        assert_eq!(cases.len(), 17);
        let junction = vec![
            AuthoredLane { id: 1, kind: 0, flags: 2, from: 1, to: 2, width: 400,
                points: vec![[-500, 0, 0], [700, 0, 0]] },
            AuthoredLane { id: 2, kind: 0, flags: 6, from: 2, to: 3, width: 600,
                points: vec![[700, 0, 0], [1000, 0, 0]] },
            AuthoredLane { id: 3, kind: 0, flags: 6, from: 4, to: 3, width: 600,
                points: vec![[1000, 0, 300], [1000, 0, 0]] },
            AuthoredLane { id: 4, kind: 0, flags: 2, from: 4, to: 5, width: 400,
                points: vec![[1000, 0, 300], [1000, 0, 1500]] },
        ];
        cases.push(("junction_connector", junction.clone(), request(0),
            answer([1000, 0, 300], 0, 4, false, 0x764cc2c6b0dddc6e)));
        let mut hub = request(0); hub.position_cm = [1000, 0, 0];
        cases.push(("junction_caller", junction, hub,
            answer([0; 3], 90000, 1, false, 0xf3373949ae01bb53)));
        let c = |id, flags, from, to, a, b| AuthoredLane {
            id, kind: 0, flags, from, to, width: 400, points: vec![a, b] };
        let far = |first, n| vec![
            c(first, 2, n, n+1, [-5000, 0, 5000], [-4000, 0, 5000]),
            c(first+1, 6, n+1, n+2, [-4000, 0, 5000], [-3700, 0, 5000]),
            c(first+2, 6, n+3, n+2, [-3700, 0, 5300], [-3700, 0, 5000]),
        ];
        let mut real = far(1, 1);
        real.extend([c(4, 2, 5, 6, [-500, 0, 0], [500, 0, 0]),
            c(5, 2, 6, 7, [500, 0, 0], [1500, 0, 0])]);
        cases.push(("connector_rank_real", real, request(0),
            answer([1000, 0, 0], 90000, 5, false, 0xaae16f42ceae0f54)));
        let mut tie = far(1, 3);
        tie.push(c(4, 2, 1, 2, [-500, 0, 0], [1500, 0, 0]));
        cases.push(("connector_rank_tie", tie, request(0),
            answer([1000, 0, 0], 90000, 4, false, 0xb790526d4bfd79b7)));
        cases.push(("connector_only_component", vec![
            c(1, 6, 1, 2, [-5000, 0, 5000], [-4700, 0, 5000]),
            c(2, 6, 3, 2, [-4700, 0, 5300], [-4700, 0, 5000]),
            c(3, 2, 4, 5, [-500, 0, 0], [1500, 0, 0]),
        ], request(0), answer([1000, 0, 0], 90000, 3, false, 0x9183a8edd40f3a8e)));
        assert_eq!(cases.len(), 22);
        for (name, lanes, request, want) in cases {
            let roads = load_simroad(&compile(&lanes)).unwrap();
            let component: Option<&[u32]> = match name {
                "junction_connector" | "junction_caller" => Some(&[1, 2, 3, 4]),
                "connector_rank_real" => Some(&[4, 5]),
                "connector_rank_tie" => Some(&[4]),
                "connector_only_component" => Some(&[3]),
                _ => None,
            };
            if let Some(ids) = component {
                assert_eq!(roads.vehicle_lanes, ids.iter().copied().collect(), "{name}");
            }
            let before = roads.clone();
            let got = place_on_road_offscreen(Some(&roads), &request);
            assert_eq!(got, want, "{name}");
            assert_eq!(got, place_on_road_offscreen(Some(&roads), &request));
            assert_eq!(roads, before);
            match got {
                Err(e) => println!("KDIFF|canary=33|case={name}|got={e:?}"),
                Ok(a) => println!("KDIFF|canary=33|case={name}|found={}|x={}|y={}|z={}|heading={}|lane={}|onscreen={}|hash={:016x}",
                    u8::from(a.found), a.position_cm[0], a.position_cm[1], a.position_cm[2],
                    a.heading_mdeg, a.lane_id, u8::from(a.onscreen), a.place_hash),
            }
        }
    }

    fn rehash(bytes: &mut [u8]) {
        bytes[48..56].fill(0);
        let hash = fnv(bytes.iter().copied());
        bytes[48..56].copy_from_slice(&hash.to_le_bytes());
    }
    #[test]
    fn loader_rejects_noncanonical_tables_and_corruption() {
        let original = straight_bytes();
        assert_eq!(original.len(), 144);
        for end in 0..original.len() {
            assert_eq!(load_simroad(&original[..end]), Err(RoadPlaceError::BadRequest));
        }
        // Header, node ordering/bounds, lane kind/flags/reserves/endpoints/width/slices,
        // vertex bounds and zero XZ segments. Rehash to exercise structural checks.
        for (at, value) in [(0, 0), (9, 1), (16, 2), (20, 1), (32, 255), (36, 255),
            (40, 255), (44, 1), (56, 0), (72, 1), (88, 0), (92, 4), (93, 4),
            (94, 1), (96, 9), (100, 9), (108, 1), (112, 1), (116, 1)] {
            let mut bytes = original.clone();
            bytes[at] = value;
            rehash(&mut bytes);
            assert_eq!(load_simroad(&bytes), Err(RoadPlaceError::BadRequest), "offset {at}");
        }
        for (at, value) in [(60, 1_000_001i32), (104, 0), (104, 200_001), (132, 1_000_001), (132, 0)] {
            let mut bytes = original.clone();
            bytes[at..at+4].copy_from_slice(&value.to_le_bytes());
            rehash(&mut bytes);
            assert!(load_simroad(&bytes).is_err(), "offset {at}");
        }
        let mut corrupt = original.clone(); corrupt[48] ^= 1;
        assert!(load_simroad(&corrupt).is_err());
        let mut extra = original; extra.push(0);
        assert!(load_simroad(&extra).is_err());
        for (kind, flags) in [(0, 4), (0, 5), (0, 7), (0, 8), (1, 6), (2, 6), (3, 6), (5, 6)] {
            let mut bytes = straight_bytes();
            bytes[92] = kind;
            bytes[93] = flags;
            rehash(&mut bytes);
            assert_eq!(load_simroad(&bytes), Err(RoadPlaceError::BadRequest), "{kind}/{flags}");
        }
    }
    #[test]
    fn compiler_canonicalizes_and_rejects_bad_authored_input() {
        let a = lane(1, 2, [0; 3], [1000, 0, 0], 400);
        let b = lane(2, 3, [0; 3], [0, 0, 1000], 400);
        assert_eq!(compile(&[a.clone(), b.clone()]), compile(&[b, a.clone()]));
        assert!(compile_simroad_json(&authored(&[a.clone(), a])).is_err());
        let mut connector = lane(1, 0, [0; 3], [1000, 0, 0], 400); connector.flags = 6;
        let roads = load_simroad(&compile(&[connector])).unwrap();
        assert_eq!(roads.lanes[0].flags, 6);
        assert_eq!(roads.vehicle_lanes, BTreeSet::from([1]));
        assert!(!place_on_road_offscreen(Some(&roads), &request(0)).unwrap().found);
        for bad in [
            r#"{"world_key":1,"nodes":[],"lanes":[]} trailing"#,
            r#"{"world_key":1,"nodes":[],"lanes":[],"world_key":2}"#,
            r#"{"world_key":1.0,"nodes":[],"lanes":[]}"#,
            r#"{"world_key":01,"nodes":[],"lanes":[]}"#,
            r#"{"world_key":18446744073709551616,"nodes":[],"lanes":[]}"#,
            r#"{"world_key":1,"nodes":[],"lanes":[],}"#,
            r#"{"world_key":1,"nodes":[{"id":1,"position_cm":[0,0]}],"lanes":[]}"#,
            r#"{"world_key":1,"nodes":[],"l\u+061nes":[]}"#,
        ] { assert!(compile_simroad_json(bad).is_err(), "{bad}"); }
    }
    #[test]
    fn component_uses_lane_count_then_smallest_node_and_weak_connectivity() {
        let a = lane(1, 0, [0; 3], [1000, 0, 0], 400);
        let mut b = lane(2, 0, [1000, 0, 0], [2000, 0, 0], 400);
        b.from = a.to; b.flags = 3;
        let mut c = lane(3, 0, [5000, 0, 0], [6000, 0, 0], 400); c.flags = 0;
        let roads = load_simroad(&compile(&[a, b, c])).unwrap();
        assert_eq!(roads.vehicle_lanes, BTreeSet::from([1, 2]));
        let mut r = request(0); r.position_cm = [1000, 0, 200];
        assert!(place_on_road_offscreen(Some(&roads), &r).unwrap().found);
        r.position_cm[2] = 201;
        assert!(!place_on_road_offscreen(Some(&roads), &r).unwrap().found);
    }
    #[test]
    fn connector_only_components_choose_smallest_node_even_with_zero_real_lanes() {
        let mut a = lane(1, 0, [5000, 0, 0], [6000, 0, 0], 400);
        a.flags = 6; a.from = 10; a.to = 11;
        let mut b = lane(2, 0, [0; 3], [1000, 0, 0], 400);
        b.flags = 6; b.from = 1; b.to = 2;
        let mut c = lane(3, 0, [1000, 0, 0], [2000, 0, 0], 400);
        c.flags = 6; c.from = 2; c.to = 3;
        let roads = load_simroad(&compile(&[a, b, c])).unwrap();
        assert_eq!(roads.vehicle_lanes, BTreeSet::from([2, 3]));
        assert!(roads.lanes.iter().any(|lane|
            roads.vehicle_lanes.contains(&lane.id) && caller_on(lane, [1000, 0, 0])));
        for kind in 0..=5 {
            assert!(!place_on_road_offscreen(Some(&roads), &request(kind)).unwrap().found);
        }
    }
    #[test]
    fn sampling_uses_outgoing_segments_final_endpoint_and_three_dimensions() {
        let mut l = lane(1, 2, [0; 3], [1000, 0, 1000], 200);
        l.points = vec![[0; 3], [1000, 0, 0], [1000, 0, 1000]];
        let roads = load_simroad(&compile(&[l])).unwrap();
        let a = place_on_road_offscreen(Some(&roads), &request(2)).unwrap();
        assert_eq!((a.position_cm, a.heading_mdeg), ([1000, 0, 0], 0));
        let l = lane(1, 2, [-101, -101, 0], [0, 0, 0], 200);
        let roads = load_simroad(&compile(&[l])).unwrap();
        let mut r = request(2); r.position_cm = [-1000, 0, 0];
        let a = place_on_road_offscreen(Some(&roads), &r).unwrap();
        assert_eq!(a.position_cm, [0; 3]); // non-100-multiple final endpoint
        r.distance_cm = 970;
        let a = place_on_road_offscreen(Some(&roads), &r).unwrap();
        assert_eq!(a.position_cm, [-30, -30, 0]); // signed interpolation truncates toward zero
        assert_eq!(a.heading_mdeg, 90000);
    }
    #[test]
    fn request_precedence_and_frustum_limits() {
        let mut r = request(6); r.radius_cm = 1;
        assert_eq!(place_on_road_offscreen(None, &r), Err(RoadPlaceError::BadRequest));
        r.radius_cm = 0;
        assert_eq!(place_on_road_offscreen(None, &r), Err(RoadPlaceError::LaneKindUnsupported));
        r.lane_kind = 4;
        assert_eq!(place_on_road_offscreen(None, &r), Err(RoadPlaceError::RoadDataMissing));
        let mut v = view();
        assert!(valid_view(&v));
        assert!(visible(&v, [0, 0, -1000])); // near face
        assert!(visible(&v, [0, 0, 3000])); // far face
        assert!(!visible(&v, [0, 0, 3001]));
        v.forward_ppm[0] = 2;
        v.up_ppm[0] = 1_000_000;
        assert!(!valid_view(&v));
        r.view = Some(v);
        assert_eq!(place_on_road_offscreen(None, &r), Err(RoadPlaceError::BadRequest));
    }
}
