//! Host-line decoding (card 56 §C.4, §C.5, §E.4): the replies (`BridgeLineCodec.TryDecodeResponse`,
//! `DecodeSnapshot`), `HELLO` (`TryParseHello`, `IsConnectHandshakeHello`, `CapsIndicateLayoutV11`), `BULK_REF`
//! (`TryParseBulkRef`), `SNAPSHOT_CHUNK` (`TryDecodeChunk`), `EVENT` pushes (`TryDecodeEvent`), the plausibility
//! gate (`IsPlausibleHostFrameLine`). The `SNAPSHOT2` classification of a bulk payload is `driver::BulkPayload`.
//!
//! Where the oracle returns null this returns `None`; where the oracle throws (an `ERROR` line whose hex
//! reason is malformed, for one) this returns `Err(ReplyError::Throws)`, so the gate-5 differential compares
//! the two exactly. [`dump_line`] is the canonical field dump both sides print.
#![forbid(unsafe_code)]

use crate::text::{decode_text, format_r, parse_f32, parse_hex_u32, parse_i32, parse_i64, parse_u64, Guid, TextError};
use crate::verbs::{GameSpeed, Vec3};

/// The oracle would have thrown out of the decoder.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ReplyError {
    Throws,
}

impl From<TextError> for ReplyError {
    fn from(_: TextError) -> Self {
        Self::Throws
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct SimSnap {
    pub object_id: u64,
    pub name: String,
    pub household: u64,
    pub position: Vec3,
    pub interaction: Option<u64>,
    pub owner: Option<i32>,
    pub interaction_key: Option<String>,
    pub lot: Option<u64>,
    pub queue_count: Option<i32>,
    pub desc: Option<u64>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct ObjSnap {
    pub object_id: u64,
    pub script_class: String,
    pub position: Vec3,
    pub lot: Option<u64>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Snapshot {
    pub tick: i64,
    pub world: String,
    pub save: String,
    pub speed: GameSpeed,
    pub sims: Vec<SimSnap>,
    pub objects: Vec<ObjSnap>,
}

/// `BridgeResponse`.
#[derive(Debug, Clone, PartialEq)]
pub struct BridgeResponse {
    pub corr: Guid,
    pub accepted: bool,
    pub rejection: Option<String>,
    pub snapshot: Option<Snapshot>,
    pub probe_report: Option<String>,
}

impl BridgeResponse {
    fn accepted(corr: Guid) -> Self {
        Self { corr, accepted: true, rejection: None, snapshot: None, probe_report: None }
    }
    fn rejected(corr: Guid, reason: String) -> Self {
        Self { corr, accepted: false, rejection: Some(reason), snapshot: None, probe_report: None }
    }
}

fn trim_bom(line: &str) -> &str {
    line.trim_start_matches('\u{FEFF}')
}

/// `IsPlausibleHostFrameLine`: no control character, a `NAME|` prefix, and a strict correlation id after
/// the prefixes that carry one.
pub fn is_plausible_host_frame_line(line: &str) -> bool {
    if line.is_empty() || line.chars().any(|c| (c as u32) < 32) {
        return false;
    }
    let Some(pipe) = line.find('|') else { return false };
    if pipe == 0 {
        return false;
    }
    let prefix = &line[..pipe];
    if matches!(
        prefix,
        "OK" | "ERROR" | "SESSION_READY" | "SNAPSHOT" | "SNAPSHOT_CHUNK" | "BULK_REF" | "BULK_NAK" | "IMPORT_HOUSEHOLD_CONTENT_REPORT"
    ) {
        let parts: Vec<&str> = line.split('|').collect();
        if parts.len() >= 2 && Guid::parse_wire(parts[1]).is_none() {
            return false;
        }
    }
    true
}

/// The `NAME|corr|kind|hexReport` families and the label of their rejection text.
fn report_label(name: &str) -> Option<&str> {
    Some(match name {
        "AUTONOMY_PROBE" => "autonomy-probe",
        "SIM_LIFECYCLE_PROBE" => "sim-lifecycle-probe",
        "ACTOR_LOOKUP_PROBE" => "actor-lookup-probe",
        "SIM_MOTIVE_PROBE" => "sim-motive-probe",
        "SIM_INTERACTION_QUEUE_PROBE" => "sim-interaction-queue-probe",
        "HOUSEHOLD_CASH_PROBE" => "household-cash-probe",
        "SIM_SKILL_PROBE" => "sim-skill-probe",
        "SIM_AUTONOMY_PROBE" => "sim-autonomy-probe",
        "SIM_STATUS_PROBE" => "sim-status-probe",
        "DIAG_STATE_REPORT"
        | "DIAG_QUEUE_REPORT"
        | "DIAG_INTERACTION_BUNDLE_REPORT"
        | "DIAG_INTERACTION_STATE_REPORT"
        | "DIAG_RESOLVE_REPORT"
        | "DIAG_WALK_REPORT"
        | "DIAG_BLOB_REPORT"
        | "DIAG_LOADTEST_REPORT"
        | "DIAG_RUN_REPORT" => name,
        "STATE_INJECTION_PROBE" => "state-injection-probe",
        "IMPORT_HOUSEHOLD_CONTENT_REPORT" => "household-import",
        "MERGE_TRAVELLING_HOUSEHOLD_REPORT" => "travel-merge",
        _ => return None,
    })
}

/// `TryDecodeResponse`.
pub fn decode_response(line: &str) -> Result<Option<BridgeResponse>, ReplyError> {
    let line = trim_bom(line);
    if !is_plausible_host_frame_line(line) {
        return Ok(None);
    }
    let parts: Vec<&str> = line.split('|').collect();
    let corr = || Guid::parse_wire(parts[1]);
    match parts[0] {
        "OK" if parts.len() >= 2 => Ok(corr().map(BridgeResponse::accepted)),
        "ERROR" if parts.len() >= 3 => match corr() {
            None => Ok(None),
            Some(c) => Ok(Some(BridgeResponse::rejected(c, decode_text(parts[2])?))),
        },
        "SESSION_READY" if parts.len() >= 5 => {
            let Some(c) = corr() else { return Ok(None) };
            let save = decode_text(parts[2])?;
            let world = decode_text(parts[3])?;
            let tick = parse_i64(parts[4])?;
            let mut r = BridgeResponse::accepted(c);
            r.snapshot = Some(Snapshot { tick, world, save, speed: GameSpeed::Pause, sims: vec![], objects: vec![] });
            Ok(Some(r))
        }
        "SNAPSHOT" if parts.len() >= 6 => Ok(decode_snapshot(&parts)),
        name if parts.len() >= 4 => {
            let Some(label) = report_label(name) else { return Ok(None) };
            let Some(c) = corr() else { return Ok(None) };
            let kind = parts[2];
            let report = decode_text(parts[3])?;
            if kind == "ok" {
                let mut r = BridgeResponse::accepted(c);
                r.probe_report = Some(report);
                Ok(Some(r))
            } else {
                Ok(Some(BridgeResponse::rejected(c, format!("{label}={kind}: {report}"))))
            }
        }
        _ => Ok(None),
    }
}

/// `DecodeSnapshot` under `TryDecodeResponse`'s catch: any failure is `None`.
fn decode_snapshot(parts: &[&str]) -> Option<BridgeResponse> {
    let corr = Guid::parse(parts[1]).ok()?;
    let tick = parse_i64(parts[2]).ok()?;
    let world = decode_text(parts[3]).ok()?;
    let save = decode_text(parts[4]).ok()?;
    let speed = GameSpeed::from_wire(parts[5])?;
    let mut sims = Vec::new();
    let mut objects = Vec::new();
    let mut idx = 6;
    if idx < parts.len() {
        let sim_count = parse_i32(parts[idx]).ok()?;
        idx += 1;
        let mut i = 0;
        while i < sim_count && idx < parts.len() {
            if let Some(s) = decode_sim_item(parts[idx]) {
                sims.push(s);
            }
            i += 1;
            idx += 1;
        }
        if idx < parts.len() {
            let obj_count = parse_i32(parts[idx]).ok()?;
            idx += 1;
            let mut i = 0;
            while i < obj_count && idx < parts.len() {
                if let Some(o) = decode_object_item(parts[idx]) {
                    objects.push(o);
                }
                i += 1;
                idx += 1;
            }
        }
    }
    let mut r = BridgeResponse::accepted(corr);
    r.snapshot = Some(Snapshot { tick, world, save, speed, sims, objects });
    Some(r)
}

fn opt<T>(field: &str, parse: impl Fn(&str) -> Result<T, TextError>) -> Result<Option<T>, TextError> {
    if field == "-" {
        Ok(None)
    } else {
        parse(field).map(Some)
    }
}

fn decode_sim_item(item: &str) -> Option<SimSnap> {
    let f: Vec<&str> = item.split(':').collect();
    if f.len() < 8 {
        return None;
    }
    let run = || -> Result<SimSnap, TextError> {
        let object_id = parse_u64(f[0])?;
        let name = decode_text(f[1])?;
        let household = parse_u64(f[2])?;
        let position = Vec3 { x: parse_f32(f[3])?, y: parse_f32(f[4])?, z: parse_f32(f[5])? };
        let interaction = opt(f[6], parse_u64)?;
        let (interaction_key, owner);
        let (mut lot, mut queue_count, mut desc) = (None, None, None);
        if f.len() >= 9 {
            interaction_key = opt(f[7], decode_text)?;
            owner = opt(f[8], parse_i32)?;
            if f.len() >= 10 && f[9] != "-" {
                lot = Some(parse_u64(f[9])?);
            }
            if f.len() >= 11 && f[10] != "-" {
                queue_count = Some(parse_i32(f[10])?);
            }
            if f.len() >= 12 && f[11] != "-" {
                desc = Some(parse_u64(f[11])?);
            }
        } else {
            interaction_key = None;
            owner = opt(f[7], parse_i32)?;
        }
        Ok(SimSnap { object_id, name, household, position, interaction, owner, interaction_key, lot, queue_count, desc })
    };
    run().ok()
}

fn decode_object_item(item: &str) -> Option<ObjSnap> {
    let f: Vec<&str> = item.split(':').collect();
    if f.len() < 6 {
        return None;
    }
    let run = || -> Result<ObjSnap, TextError> {
        Ok(ObjSnap {
            object_id: parse_u64(f[0])?,
            script_class: decode_text(f[1])?,
            position: Vec3 { x: parse_f32(f[2])?, y: parse_f32(f[3])?, z: parse_f32(f[4])? },
            lot: opt(f[5], parse_u64)?,
        })
    };
    run().ok()
}

// ---- HELLO ----

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Hello {
    pub world: String,
    pub tick: i64,
    pub build_stamp: Option<String>,
    pub wire_version: i32,
    pub caps: Option<String>,
}

/// `TryParseHello` (`Ok(None)` where it returns false; `Err` where it throws).
pub fn parse_hello(line: &str) -> Result<Option<Hello>, ReplyError> {
    let line = trim_bom(line);
    if !line.starts_with("HELLO|") {
        return Ok(None);
    }
    let parts: Vec<&str> = line.split('|').collect();
    if parts.len() < 4 {
        return Ok(None);
    }
    let Ok(wire_version) = parse_i32(parts[1]) else { return Ok(None) };
    let Ok(tick) = parse_i64(parts[3]) else { return Ok(None) };
    let build_stamp = if parts.len() >= 5 { Some(decode_text(parts[4])?) } else { None };
    let caps = if parts.len() >= 6 { Some(decode_text(parts[5])?) } else { None };
    Ok(Some(Hello { world: decode_text(parts[2])?, tick, build_stamp, wire_version, caps }))
}

/// `IsConnectHandshakeHello`: a HELLO of wire version 6 or later.
pub fn is_connect_handshake_hello(line: &str) -> Result<bool, ReplyError> {
    Ok(parse_hello(line)?.is_some_and(|h| h.wire_version >= 6))
}

/// `CapsIndicateLayoutV11`.
pub fn caps_indicate_layout_v11(caps: Option<&str>) -> bool {
    let Some(caps) = caps else { return false };
    if caps.trim().is_empty() {
        return false;
    }
    for segment in caps.split(',') {
        let part = segment.trim();
        let lower = part.to_ascii_lowercase();
        if let Some(v) = lower.strip_prefix("layout=") {
            if parse_i32(v).is_ok_and(|v| v >= crate::layout::LAYOUT_VERSION as i32) {
                return true;
            }
        }
        if lower.starts_with("multi-slot=") {
            return true;
        }
    }
    false
}

/// `ProcessMemoryBridge.AcceptConnectResponse`: a handshake HELLO, or any `HELLO|<n>|` with n >= 6. `Err` where
/// the oracle's `IsConnectHandshakeHello` throws (a malformed hex field), which ends its connect.
pub fn accept_connect_response(line: &str) -> Result<bool, ReplyError> {
    if is_connect_handshake_hello(line)? {
        return Ok(true);
    }
    let line = trim_bom(line);
    let Some(rest) = line.strip_prefix("HELLO|") else { return Ok(false) };
    Ok(match rest.find('|') {
        Some(end) if end > 0 => parse_i32(&rest[..end]).is_ok_and(|v| v >= crate::layout::HELLO_WIRE_VERSION as i32),
        _ => false,
    })
}

// ---- BULK_REF and SNAPSHOT_CHUNK ----

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BulkRef {
    pub corr: Guid,
    pub kind: String,
    pub slot: i32,
    pub total_length: i32,
    pub crc32: u32,
    pub part_index: Option<i32>,
    pub part_count: Option<i32>,
}

/// `TryParseBulkRef`.
pub fn parse_bulk_ref(line: &str) -> Option<BulkRef> {
    let line = trim_bom(line);
    let parts: Vec<&str> = line.split('|').collect();
    if parts.len() < 6 || parts[0] != "BULK_REF" {
        return None;
    }
    let corr = Guid::parse(parts[1]).ok()?;
    let kind = parts[2].to_string();
    let slot = parse_i32(parts[3]).ok()?;
    if kind == "snapshot-part" {
        if parts.len() < 8 {
            return None;
        }
        return Some(BulkRef {
            corr,
            kind,
            slot,
            total_length: parse_i32(parts[6]).ok()?,
            crc32: parse_hex_u32(parts[7]).ok()?,
            part_index: Some(parse_i32(parts[4]).ok()?),
            part_count: Some(parse_i32(parts[5]).ok()?),
        });
    }
    Some(BulkRef {
        corr,
        kind,
        slot,
        total_length: parse_i32(parts[4]).ok()?,
        crc32: parse_hex_u32(parts[5]).ok()?,
        part_index: None,
        part_count: None,
    })
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Chunk {
    pub corr: Guid,
    pub index: i32,
    pub count: i32,
    pub payload: String,
}

/// `TryDecodeChunk`: `SNAPSHOT_CHUNK|corr|index|count|payload` (the payload keeps its pipes).
pub fn parse_chunk(line: &str) -> Option<Chunk> {
    let line = trim_bom(line);
    let parts: Vec<&str> = line.splitn(5, '|').collect();
    if parts.len() < 5 || parts[0] != "SNAPSHOT_CHUNK" {
        return None;
    }
    Some(Chunk {
        corr: Guid::parse(parts[1]).ok()?,
        index: parse_i32(parts[2]).ok()?,
        count: parse_i32(parts[3]).ok()?,
        payload: parts[4].to_string(),
    })
}

// ---- EVENT ----

/// The decoded `EVENT|...` pushes (`ServerEvent` records the oracle builds from them).
#[derive(Debug, Clone, PartialEq)]
pub enum Event {
    InteractionQueued {
        actor: u64,
        interaction_id: u64,
        key: String,
        target: Option<u64>,
        position: Option<Vec3>,
        parameters: Option<Vec<(String, String)>>,
    },
    /// Classified and hex-decoded only: the capture-report bundle belongs to the legacy replay path, which
    /// this card does not port (§E.3, §E.5).
    InteractionReplayQueued { report: String },
    InteractionStarted { actor: u64, interaction_id: u64 },
    InteractionCompleted { actor: u64, interaction_id: u64 },
    InteractionFailed { actor: u64, interaction_id: u64, reason: String },
    IntentCaptured { actor: u64, target: u64, key: String, position: Option<Vec3> },
    UserIntent {
        actor: u64,
        target: u64,
        definition_type: String,
        name: String,
        path: String,
        hit: String,
        hit_point: Option<Vec3>,
        picked: String,
    },
    DialogRequested { request_id: i32, sim: u64, kind: String, args: Vec<String> },
    DialogAnswer { request_id: i32, sim: u64, answer: Option<String> },
    /// `PickFailed` becomes the player's `CommandRejected`.
    CommandRejected { command_id: Guid, reason: String },
    CancelIntent { actor: u64, interaction_id: u64 },
    LeaveConversationIntent { actor: u64 },
    SpeedIntent { speed: GameSpeed },
    ServerError { message: String },
}

/// What `TryDecodeEvent` did with a line.
#[derive(Debug, Clone, PartialEq)]
pub enum EventDecode {
    Event(Event),
    /// A kind the oracle decodes, whose fields did not decode (the oracle counts it in `BridgeEventDrops`).
    Dropped { kind: String },
    /// Not an `EVENT|` line, or a kind with no arm.
    Ignored,
}

const INTERACTION_KEYS: [&str; 10] = [
    "bookId",
    "progressNormalized",
    "autonomous",
    "userDirected",
    "effectivelyAutonomous",
    "seatObjectId",
    "seatArea",
    "seatContainmentSlot",
    "seatRoutingSlot",
    "seatIkSuffix",
];
const FORCE_ALIGN_KEYS: [&str; 10] = [
    "hitX",
    "hitY",
    "hitZ",
    "seatArea",
    "seatContainmentSlot",
    "seatIkSuffix",
    "seatRoutingSlot",
    "autonomous",
    "userDirected",
    "effectivelyAutonomous",
];

/// `DecodeParameterTail`: allow-listed `key=value` pairs; a later duplicate replaces the earlier value.
fn decode_parameter_tail(tail: Option<&str>) -> Option<Vec<(String, String)>> {
    let tail = tail.filter(|t| !t.is_empty())?;
    let mut out: Vec<(String, String)> = Vec::new();
    for pair in tail.split(',') {
        let Some(sep) = pair.find('=') else { continue };
        if sep == 0 || sep == pair.len() - 1 {
            continue;
        }
        let key = &pair[..sep];
        if !INTERACTION_KEYS.contains(&key) && !FORCE_ALIGN_KEYS.contains(&key) {
            continue;
        }
        let value = pair[sep + 1..].to_string();
        match out.iter_mut().find(|(k, _)| k == key) {
            Some(slot) => slot.1 = value,
            None => out.push((key.to_string(), value)),
        }
    }
    if out.is_empty() {
        None
    } else {
        Some(out)
    }
}

fn vec3(x: &str, y: &str, z: &str) -> Result<Vec3, TextError> {
    Ok(Vec3 { x: parse_f32(x)?, y: parse_f32(y)?, z: parse_f32(z)? })
}

/// `TryDecodeEvent`.
pub fn decode_event(line: &str) -> EventDecode {
    let line = trim_bom(line);
    let p: Vec<&str> = line.split('|').collect();
    if p.len() < 2 || p[0] != "EVENT" {
        return EventDecode::Ignored;
    }
    let kind = p[1];
    let need = |n: usize| -> Result<(), TextError> {
        if p.len() >= n {
            Ok(())
        } else {
            Err(TextError::InvalidNumber)
        }
    };
    let decoded: Result<Option<Event>, TextError> = (|| {
        Ok(Some(match kind {
            "InteractionQueued" => {
                need(10)?;
                Event::InteractionQueued {
                    actor: parse_u64(p[2])?,
                    interaction_id: parse_u64(p[3])?,
                    key: decode_text(p[4])?,
                    target: if p[5].is_empty() { None } else { Some(parse_u64(p[5])?) },
                    position: if p[6] == "1" { Some(vec3(p[7], p[8], p[9])?) } else { None },
                    parameters: decode_parameter_tail(p.get(10).copied()),
                }
            }
            "InteractionReplayQueued" => {
                need(3)?;
                Event::InteractionReplayQueued { report: decode_text(p[2])? }
            }
            "InteractionStarted" => {
                need(4)?;
                Event::InteractionStarted { actor: parse_u64(p[2])?, interaction_id: parse_u64(p[3])? }
            }
            "InteractionCompleted" => {
                need(4)?;
                Event::InteractionCompleted { actor: parse_u64(p[2])?, interaction_id: parse_u64(p[3])? }
            }
            "InteractionFailed" => {
                need(5)?;
                Event::InteractionFailed {
                    actor: parse_u64(p[2])?,
                    interaction_id: parse_u64(p[3])?,
                    reason: decode_text(p[4])?,
                }
            }
            "IntentCaptured" => {
                need(9)?;
                Event::IntentCaptured {
                    actor: parse_u64(p[2])?,
                    target: parse_u64(p[3])?,
                    key: decode_text(p[4])?,
                    position: if p[5] == "1" { Some(vec3(p[6], p[7], p[8])?) } else { None },
                }
            }
            "UserIntent" => {
                need(12)?;
                Event::UserIntent {
                    actor: parse_u64(p[2])?,
                    target: parse_u64(p[3])?,
                    definition_type: decode_text(p[4])?,
                    name: decode_text(p[5])?,
                    path: decode_text(p[6])?,
                    hit: p[7].to_string(),
                    hit_point: if p[8] == "-" { None } else { Some(vec3(p[8], p[9], p[10])?) },
                    picked: p[11].to_string(),
                }
            }
            "DialogRequest" => {
                need(6)?;
                let request_id = parse_i32(p[2])?;
                let sim = parse_u64(p[3])?;
                let kind = decode_text(p[4])?;
                let count = parse_i32(p[5])?;
                if count < 0 {
                    return Err(TextError::InvalidNumber);
                }
                let mut args = Vec::with_capacity(count as usize);
                for i in 0..count as usize {
                    args.push(match p.get(6 + i) {
                        Some(a) => decode_text(a)?,
                        None => String::new(),
                    });
                }
                Event::DialogRequested { request_id, sim, kind, args }
            }
            "DialogAnswer" => {
                need(5)?;
                Event::DialogAnswer {
                    request_id: parse_i32(p[2])?,
                    sim: parse_u64(p[3])?,
                    answer: if p[4] == "-" { None } else { Some(decode_text(p[4])?) },
                }
            }
            "PickFailed" => {
                need(6)?;
                Event::CommandRejected { command_id: Guid::parse(p[2])?, reason: decode_text(p[5])? }
            }
            "CancelIntent" => {
                need(4)?;
                Event::CancelIntent { actor: parse_u64(p[2])?, interaction_id: parse_u64(p[3])? }
            }
            "LeaveConversationIntent" => {
                need(3)?;
                Event::LeaveConversationIntent { actor: parse_u64(p[2])? }
            }
            "SpeedIntentCaptured" => {
                need(3)?;
                Event::SpeedIntent { speed: GameSpeed::from_wire(p[2]).ok_or(TextError::InvalidNumber)? }
            }
            "ServerError" => {
                need(3)?;
                Event::ServerError { message: decode_text(p[2])? }
            }
            _ => return Ok(None),
        }))
    })();
    match decoded {
        Ok(Some(e)) => EventDecode::Event(e),
        Ok(None) => EventDecode::Ignored,
        Err(_) => EventDecode::Dropped { kind: kind.to_string() },
    }
}

// ---- the canonical dump (gate 5) ----

/// Escape a field for the dump (backslash, tab, CR, LF).
pub fn esc(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for c in s.chars() {
        match c {
            '\\' => out.push_str("\\\\"),
            '\t' => out.push_str("\\t"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            _ => out.push(c),
        }
    }
    out
}

fn o<T: ToString>(v: &Option<T>) -> String {
    v.as_ref().map_or_else(|| "~".to_string(), |x| x.to_string())
}

fn os(v: &Option<String>) -> String {
    v.as_ref().map_or_else(|| "~".to_string(), |x| esc(x))
}

fn v3(p: &Vec3) -> String {
    format!("{},{},{}", format_r(p.x), format_r(p.y), format_r(p.z))
}

fn ov3(p: &Option<Vec3>) -> String {
    p.as_ref().map_or_else(|| "~".to_string(), v3)
}

fn dump_snapshot(s: &Snapshot) -> String {
    let sims: Vec<String> = s
        .sims
        .iter()
        .map(|m| {
            format!(
                "{},{},{},{},{},{},{},{},{},{}",
                m.object_id,
                esc(&m.name),
                m.household,
                v3(&m.position),
                o(&m.interaction),
                o(&m.owner),
                os(&m.interaction_key),
                o(&m.lot),
                o(&m.queue_count),
                o(&m.desc)
            )
        })
        .collect();
    let objs: Vec<String> = s
        .objects
        .iter()
        .map(|b| format!("{},{},{},{}", b.object_id, esc(&b.script_class), v3(&b.position), o(&b.lot)))
        .collect();
    format!(
        "{}|{}|{}|{}|sims=[{}]|objs=[{}]",
        s.tick,
        esc(&s.world),
        esc(&s.save),
        s.speed.name(),
        sims.join(";"),
        objs.join(";")
    )
}

fn dump_response(r: &Result<Option<BridgeResponse>, ReplyError>) -> String {
    match r {
        Err(_) => "throw".to_string(),
        Ok(None) => "~".to_string(),
        Ok(Some(r)) => format!(
            "{}|{}|{}|{}|{}",
            r.corr.to_n(),
            u8::from(r.accepted),
            os(&r.rejection),
            os(&r.probe_report),
            r.snapshot.as_ref().map_or_else(|| "~".to_string(), dump_snapshot)
        ),
    }
}

fn dump_params(p: &Option<Vec<(String, String)>>) -> String {
    match p {
        None => "~".to_string(),
        Some(list) => {
            let mut sorted: Vec<String> = list.iter().map(|(k, v)| format!("{}={}", esc(k), esc(v))).collect();
            sorted.sort();
            sorted.join(",")
        }
    }
}

fn dump_event(e: &EventDecode) -> String {
    let ev = match e {
        EventDecode::Event(ev) => ev,
        EventDecode::Dropped { kind } => return format!("~|dropped={}", esc(kind)),
        EventDecode::Ignored => return "~|dropped=-".to_string(),
    };
    match ev {
        Event::InteractionQueued { actor, interaction_id, key, target, position, parameters } => format!(
            "InteractionQueued|{actor}|{interaction_id}|{}|{}|{}|{}",
            esc(key),
            o(target),
            ov3(position),
            dump_params(parameters)
        ),
        Event::InteractionReplayQueued { .. } => "InteractionReplayQueued".to_string(),
        Event::InteractionStarted { actor, interaction_id } => format!("InteractionStarted|{actor}|{interaction_id}"),
        Event::InteractionCompleted { actor, interaction_id } => format!("InteractionCompleted|{actor}|{interaction_id}"),
        Event::InteractionFailed { actor, interaction_id, reason } => {
            format!("InteractionFailed|{actor}|{interaction_id}|{}", esc(reason))
        }
        Event::IntentCaptured { actor, target, key, position } => {
            format!("IntentCaptured|{actor}|{target}|{}|{}", esc(key), ov3(position))
        }
        Event::UserIntent { actor, target, definition_type, name, path, hit, hit_point, picked } => format!(
            "UserIntentCaptured|{actor}|{target}|{}|{}|{}|{}|{}|{}",
            esc(definition_type),
            esc(name),
            esc(path),
            esc(hit),
            ov3(hit_point),
            esc(picked)
        ),
        Event::DialogRequested { request_id, sim, kind, args } => format!(
            "DialogRequested|{request_id}|{sim}|{}|{}",
            esc(kind),
            args.iter().map(|a| esc(a)).collect::<Vec<_>>().join(",")
        ),
        Event::DialogAnswer { request_id, sim, answer } => format!("DialogAnswerCaptured|{request_id}|{sim}|{}", os(answer)),
        Event::CommandRejected { command_id, reason } => format!("CommandRejected|{}|{}", command_id.to_n(), esc(reason)),
        Event::CancelIntent { actor, interaction_id } => format!("CancelIntentCaptured|{actor}|{interaction_id}"),
        Event::LeaveConversationIntent { actor } => format!("LeaveConversationIntentCaptured|{actor}"),
        Event::SpeedIntent { speed } => format!("SpeedIntentCaptured|{}", speed.name()),
        Event::ServerError { message } => format!("ServerError|{}", esc(message)),
    }
}

fn dump_hello(h: &Result<Option<Hello>, ReplyError>) -> String {
    match h {
        Err(_) => "throw".to_string(),
        Ok(None) => "~".to_string(),
        Ok(Some(h)) => format!(
            "{}|{}|{}|{}|{}|v11={}",
            esc(&h.world),
            h.tick,
            os(&h.build_stamp),
            h.wire_version,
            os(&h.caps),
            u8::from(caps_indicate_layout_v11(h.caps.as_deref()))
        ),
    }
}

fn dump_bulk_ref(b: &Option<BulkRef>) -> String {
    match b {
        None => "~".to_string(),
        Some(b) => format!(
            "{}|{}|{}|{}|{:08X}|{}|{}",
            b.corr.to_n(),
            esc(&b.kind),
            b.slot,
            b.total_length,
            b.crc32,
            o(&b.part_index),
            o(&b.part_count)
        ),
    }
}

fn dump_chunk(c: &Option<Chunk>) -> String {
    match c {
        None => "~".to_string(),
        Some(c) => format!("{}|{}|{}|{}", c.corr.to_n(), c.index, c.count, esc(&c.payload)),
    }
}

/// The canonical dump of one host line: every decoder's view, tab separated. The C# oracle
/// (`rust/simlink_bridge/oracle`) prints the same fields from `BridgeLineCodec`.
pub fn dump_line(line: &str) -> String {
    let connect = match is_connect_handshake_hello(line) {
        Ok(b) => u8::from(b).to_string(),
        Err(_) => "throw".to_string(),
    };
    [
        format!("plausible={}", u8::from(is_plausible_host_frame_line(trim_bom(line)))),
        format!("resp={}", dump_response(&decode_response(line))),
        format!("hello={}", dump_hello(&parse_hello(line))),
        format!("connect_hello={connect}"),
        format!(
            "accept_connect={}",
            accept_connect_response(line).map_or_else(|_| "throw".to_string(), |b| u8::from(b).to_string())
        ),
        format!("bulkref={}", dump_bulk_ref(&parse_bulk_ref(line))),
        format!("chunk={}", dump_chunk(&parse_chunk(line))),
        format!("event={}", dump_event(&decode_event(line))),
    ]
    .join("\t")
}

#[cfg(test)]
mod tests {
    use super::*;

    const C: &str = "0123456789abcdef0123456789abcdef";

    #[test]
    fn ok_error_and_probe_replies() {
        let ok = decode_response(&format!("OK|{C}")).unwrap().unwrap();
        assert!(ok.accepted);
        let err = decode_response(&format!("ERROR|{C}|414243")).unwrap().unwrap();
        assert_eq!(err.rejection.as_deref(), Some("ABC"));
        assert_eq!(decode_response(&format!("ERROR|{C}|414")), Err(ReplyError::Throws));
        let probe = decode_response(&format!("SIM_MOTIVE_PROBE|{C}|none|6869")).unwrap().unwrap();
        assert_eq!(probe.rejection.as_deref(), Some("sim-motive-probe=none: hi"));
        assert_eq!(decode_response("OK|nothex"), Ok(None));
        assert_eq!(decode_response("WHAT|x"), Ok(None));
    }

    #[test]
    fn snapshot_with_sims_and_objects() {
        let line = format!("SNAPSHOT|{C}|42|57|58|Normal|1|7:41:9:1.5:0:2:-:-:-:5:3:99|1|8:42:0:0:0:-");
        let r = decode_response(&line).unwrap().unwrap();
        let s = r.snapshot.unwrap();
        assert_eq!((s.tick, s.world.as_str(), s.speed), (42, "W", GameSpeed::Normal));
        assert_eq!(s.sims[0].lot, Some(5));
        assert_eq!(s.sims[0].desc, Some(99));
        assert_eq!(s.objects[0].script_class, "B");
    }

    #[test]
    fn hello_bulk_ref_chunk_and_events() {
        let hello = format!("HELLO|6|57|10|61|{}", crate::text::encode_text(crate::layout::DEFAULT_HELLO_CAPS));
        let h = parse_hello(&hello).unwrap().unwrap();
        assert!(caps_indicate_layout_v11(h.caps.as_deref()));
        assert_eq!(accept_connect_response(&hello), Ok(true));
        assert_eq!(accept_connect_response("HELLO|5|57|10"), Ok(false));
        let b = parse_bulk_ref(&format!("BULK_REF|{C}|snapshot-part|2|1|3|500|00ABCDEF")).unwrap();
        assert_eq!((b.slot, b.part_index, b.crc32), (2, Some(1), 0x00AB_CDEF));
        let c = parse_chunk(&format!("SNAPSHOT_CHUNK|{C}|0|2|a|b")).unwrap();
        assert_eq!(c.payload, "a|b");
        assert_eq!(
            decode_event("EVENT|InteractionStarted|1|2"),
            EventDecode::Event(Event::InteractionStarted { actor: 1, interaction_id: 2 })
        );
        assert_eq!(decode_event("EVENT|InteractionStarted|1"), EventDecode::Dropped { kind: "InteractionStarted".into() });
        assert_eq!(decode_event("EVENT|CaptureFilterDropped|1|41"), EventDecode::Ignored);
    }
}
