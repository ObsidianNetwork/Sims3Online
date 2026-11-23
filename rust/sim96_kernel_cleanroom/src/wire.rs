#![forbid(unsafe_code)]
//! Wire protocol ordering and non-blocking late-join (spec card 10).
//!
//! The 30 Hz tick loop owns the world behind an `Arc<WorldSnapshot>`; a join
//! gather takes an `Arc` clone (one atomic refcount bump, no I/O) and the tick
//! loop forks a new snapshot (CoW) only while a reader holds one. Established
//! players therefore see `tick_drops_during_join == 0` and a presentation gap
//! of at most 1 during a join. `BridgeFrameHeaderRaw` pins the 32-byte bridge
//! slot header layout.
//!
//! Model-only boundary (explicit, card 10 §D.3): this kernel models the join
//! gather against an immutable snapshot clone; it does not touch the game's
//! native `WorldSave` (there is none in the clean-room kernel), and the
//! native transport (shared-memory ring, TCP) is out of scope — the frame
//! codec here is the typed model of the wire bytes, not a socket.

use std::collections::BTreeMap;
use std::sync::Arc;

use crate::handles::KernelError;

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct PlayerId(pub u32);

/// The 32-byte bridge slot header (card 10 §B.2).
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct BridgeFrameHeaderRaw {
    pub magic: u32,
    pub stream_id: u32,
    pub tick_id: u64,
    pub seq: u64,
    pub length: u32,
    pub reserved: u32,
}

const _: () = assert!(std::mem::size_of::<BridgeFrameHeaderRaw>() == 32);
const _: () = assert!(std::mem::offset_of!(BridgeFrameHeaderRaw, magic) == 0);
const _: () = assert!(std::mem::offset_of!(BridgeFrameHeaderRaw, stream_id) == 4);
const _: () = assert!(std::mem::offset_of!(BridgeFrameHeaderRaw, tick_id) == 8);
const _: () = assert!(std::mem::offset_of!(BridgeFrameHeaderRaw, seq) == 16);
const _: () = assert!(std::mem::offset_of!(BridgeFrameHeaderRaw, length) == 24);
const _: () = assert!(std::mem::offset_of!(BridgeFrameHeaderRaw, reserved) == 28);

/// Bridge frame header size in bytes.
pub const BRIDGE_FRAME_HEADER_SIZE: usize = 32;

/// Why a join fingerprint missed the cached checkpoint (card 10 §D.2).
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum JoinMissReason {
    NoFingerprintYet,
    Scope,
    Objects,
    Inventories,
    Relationships,
    Error,
}

/// The outcome of a join request.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum JoinVerdict {
    Hit,
    Miss(JoinMissReason),
}

/// The three fingerprint parts no live channel carries: objects, inventories,
/// relationships (card 10 §D.2).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct JoinFingerprint {
    pub parts: [u64; 3],
}

impl JoinFingerprint {
    /// The fingerprint part order: objects, inventories, relationships.
    const PART_REASONS: [JoinMissReason; 3] =
        [JoinMissReason::Objects, JoinMissReason::Inventories, JoinMissReason::Relationships];

    /// The first part that differs between the two fingerprints, mapped to its
    /// miss reason. `None` when the fingerprints are equal (a HIT).
    pub fn compare(&self, other: &JoinFingerprint) -> Option<JoinMissReason> {
        self.parts
            .iter()
            .zip(other.parts.iter())
            .position(|(a, b)| a != b)
            .map(|i| Self::PART_REASONS[i])
    }
}

/// Deterministic 64-bit FNV-1a over a byte stream (model-only hash; the
/// shipped `scripts/join_fingerprint.py` uses its own digest, the kernel only
/// needs a stable, order-sensitive mix).
fn fnv1a_64(bytes: &[u8]) -> u64 {
    let mut h: u64 = 0xcbf2_9ce4_8422_2325;
    for b in bytes {
        h ^= u64::from(*b);
        h = h.wrapping_mul(0x0000_0100_0000_01b3);
    }
    h
}

/// The join scope: the households of the claimed Sims, their home lots, and
/// the lots those Sims stand on (never the world lot). Keyed by household +
/// lot ids so the same households give the same scope whichever of their Sims
/// are claimed (card 10 §D.2).
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct JoinScope {
    pub households: BTreeMap<u32, u32>,
    /// Sorted set of lot ids (a `BTreeMap` keeps the crate dependency-free).
    pub lots: BTreeMap<u32, ()>,
}

impl JoinScope {
    pub fn new() -> Self {
        Self::default()
    }

    /// Canonical scope key: sorted household ids, then sorted lot ids.
    pub fn key(&self) -> Vec<u64> {
        let mut k = Vec::with_capacity(self.households.len() + self.lots.len());
        for (hh, lot) in &self.households {
            k.push(u64::from(*hh) << 32 | u64::from(*lot));
        }
        for lot in self.lots.keys() {
            k.push(u64::from(*lot));
        }
        k
    }

    /// The model-only fingerprint of the state no live channel carries
    /// (card 10 §D.2). `objects` is every object on the scoped lots but Sims,
    /// by id + type, with its spot (0.25 m) when it stands free; `inventories`
    /// is each inventory of the scoped Sims/objects as item ids;
    /// `relationships` is each relationship of the scoped Sims as LTR state +
    /// liking in steps of 10. Live-channel state is excluded.
    pub fn fingerprint(
        &self,
        objects: &BTreeMap<u64, (u64, u64)>,
        inventories: &BTreeMap<u64, Vec<u64>>,
        relationships: &BTreeMap<(u32, u32), (u32, i32)>,
    ) -> JoinFingerprint {
        let mut obj = Vec::new();
        for (id, (ty, spot)) in objects {
            obj.extend_from_slice(&id.to_le_bytes());
            obj.extend_from_slice(&ty.to_le_bytes());
            obj.extend_from_slice(&spot.to_le_bytes());
        }
        let mut inv = Vec::new();
        for (owner, items) in inventories {
            inv.extend_from_slice(&owner.to_le_bytes());
            inv.extend_from_slice(&(items.len() as u64).to_le_bytes());
            for item in items {
                inv.extend_from_slice(&item.to_le_bytes());
            }
        }
        let mut rel = Vec::new();
        for ((a, b), (ltr, liking)) in relationships {
            rel.extend_from_slice(&a.to_le_bytes());
            rel.extend_from_slice(&b.to_le_bytes());
            rel.extend_from_slice(&ltr.to_le_bytes());
            // liking in steps of 10 (floor division; the fingerprint's step).
            rel.extend_from_slice(&(*liking / 10).to_le_bytes());
        }
        JoinFingerprint {
            parts: [fnv1a_64(&obj), fnv1a_64(&inv), fnv1a_64(&rel)],
        }
    }
}

/// An immutable world view for a join gather.
#[derive(Clone, Debug, Default)]
pub struct WorldSnapshot {
    pub tick: u64,
    pub objects: BTreeMap<u64, u64>,
}

impl WorldSnapshot {
    fn apply(&mut self, deltas: &[WorldDelta]) {
        for d in deltas {
            self.tick = d.tick;
            if let Some(obj) = d.object_add {
                self.objects.insert(obj.0, obj.1);
            }
            if let Some(obj) = d.object_remove {
                self.objects.remove(&obj);
            }
        }
    }
}

/// A single tick's world mutation.
#[derive(Clone, Copy, Debug, Default)]
pub struct WorldDelta {
    pub tick: u64,
    pub object_add: Option<(u64, u64)>,
    pub object_remove: Option<u64>,
}

/// The 30 Hz tick loop's world state (card 10 §D.3).
#[derive(Clone, Debug, Default)]
pub struct TickLoopState {
    pub world: Arc<WorldSnapshot>,
    pub dirty: Vec<WorldDelta>,
}

impl TickLoopState {
    pub fn new() -> Self {
        Self { world: Arc::new(WorldSnapshot::default()), dirty: Vec::new() }
    }

    /// Take an `Arc` clone for a join gather. Never blocks the tick loop
    /// ( invariant).
    pub fn snapshot_for_join(&self) -> Arc<WorldSnapshot> {
        Arc::clone(&self.world)
    }

    /// Commit a tick. If a join reader holds a clone, fork a new snapshot
    /// (CoW) so the reader keeps its consistent, immutable view; otherwise
    /// mutate in place (the cheap path). `Arc::make_mut` also safely detaches
    /// weak references when this is the only strong owner.
    pub fn commit_tick(&mut self, delta: WorldDelta) {
        self.dirty.push(delta);
        Arc::make_mut(&mut self.world).apply(&std::mem::take(&mut self.dirty));
    }
}

/// The join-reuse counters the kernel exports (card 10 §D.4).
#[derive(Clone, Debug, Default)]
pub struct JoinCounters {
    pub hits: u64,
    pub misses: u64,
    pub saves_skipped: u64,
    pub tick_drops_during_join: u64,
    pub presentation_sent_gap_max_during_join: u64,
    pub misses_by_reason: BTreeMap<JoinMissReason, u64>,
    pub join_reuse_last_verdict: Option<JoinVerdict>,
    pub join_fingerprint_scan_ms_last: u64,
    pub join_fingerprint_scan_ms_max: u64,
    pub join_fresh_save_ms_last: u64,
    pub join_fresh_save_ms_max: u64,
    pub join_seconds_saved: u64,
    saved_ms: u64,
    last_sent: Option<u64>,
    join_open: bool,
}

impl JoinCounters {
    /// A join gather started (the reader holds an `Arc<WorldSnapshot>`).
    pub fn join_started(&mut self) {
        self.join_open = true;
    }

    /// The join gather finished (the reader released its clone).
    pub fn join_finished(&mut self) {
        self.join_open = false;
    }

    /// Record a presentation frame sent at `tick`. While a join is open the
    /// gap since the last sent frame is accounted: a gap of `g` means
    /// `g - 1` dropped ticks, and the max gap is tracked. A gap >= 2 during a
    /// join is a regression.
    pub fn presentation_sent(&mut self, tick: u64, during_join: bool) {
        if during_join || self.join_open {
            if let Some(last) = self.last_sent {
                let gap = tick.saturating_sub(last);
                self.presentation_sent_gap_max_during_join =
                    self.presentation_sent_gap_max_during_join.max(gap);
                self.tick_drops_during_join += gap.saturating_sub(1);
            }
        }
        self.last_sent = Some(tick);
    }
}

/// The join-reuse state: the last fingerprint + snapshot, and the counters
/// (card 10 §F).
#[derive(Clone, Debug, Default)]
pub struct JoinReuseState {
    pub last: Option<(JoinFingerprint, Arc<WorldSnapshot>)>,
    pub counters: JoinCounters,
    in_flight: Option<Arc<WorldSnapshot>>,
}

impl JoinReuseState {
    /// A `RequestWorld` arrived. HIT: the current fingerprint matches the
    /// cached checkpoint's — serve the cached snapshot, no fresh save.
    /// MISS: `None`, with the reason recorded in the counters.
    pub fn on_request_world(&mut self, current: &JoinFingerprint) -> Option<Arc<WorldSnapshot>> {
        match &self.last {
            Some((fp, snap)) if fp.parts == current.parts => {
                self.counters.hits += 1;
                self.counters.saves_skipped += 1;
                self.counters.join_reuse_last_verdict = Some(JoinVerdict::Hit);
                self.counters.saved_ms += self.counters.join_fresh_save_ms_last;
                self.counters.join_seconds_saved = self.counters.saved_ms / 1000;
                Some(Arc::clone(snap))
            }
            Some((fp, _)) => {
                self.counters.misses += 1;
                let reason = fp.compare(current).unwrap_or(JoinMissReason::Error);
                self.counters.join_reuse_last_verdict = Some(JoinVerdict::Miss(reason));
                *self.counters.misses_by_reason.entry(reason).or_insert(0) += 1;
                None
            }
            None => {
                self.counters.misses += 1;
                self.counters.join_reuse_last_verdict =
                    Some(JoinVerdict::Miss(JoinMissReason::NoFingerprintYet));
                *self.counters
                    .misses_by_reason
                    .entry(JoinMissReason::NoFingerprintYet)
                    .or_insert(0) += 1;
                None
            }
        }
    }

    /// A fresh checkpoint completed: cache its fingerprint + snapshot.
    pub fn on_fresh_checkpoint(&mut self, fp: JoinFingerprint, snap: Arc<WorldSnapshot>) {
        self.last = Some((fp, snap));
        self.in_flight = None;
    }

    /// Requests that arrive together share one gather (single-flight, card 10
    /// §D.2): the first request takes the snapshot, the rest get the same
    /// immutable `Arc` clone.
    pub fn gather_single_flight(&mut self, tick_loop: &TickLoopState) -> Arc<WorldSnapshot> {
        Arc::clone(self.in_flight.get_or_insert_with(|| tick_loop.snapshot_for_join()))
    }

    /// The in-flight gather's snapshot, if any.
    pub fn in_flight_snapshot(&self) -> Option<Arc<WorldSnapshot>> {
        self.in_flight.clone()
    }
}

/// The public-protocol codec surface (card 10 §C).
///
/// Models the `corr` idempotency gate: a command whose `corr <= last_seen` is
/// silently dropped, never rejected. The authority check runs BEFORE the
/// mutation and BEFORE the dedup bookkeeping, so an unauthorized command has
/// no effect and does not consume its `corr` (a later authorized retransmit
/// of the same `corr` is still fresh).
#[derive(Default, Debug)]
pub struct PublicProtocolCodec {
    last_seen_corr: BTreeMap<u32, u64>,
}

impl PublicProtocolCodec {
    pub fn new() -> Self {
        Self::default()
    }

    /// Returns `true` if the command is fresh (apply it), `false` if it is a
    /// duplicate / out-of-order retransmit (drop it, `Ok(())` semantics).
    pub fn accept_command(&mut self, player: u32, corr: u64) -> bool {
        if self.last_seen_corr.get(&player).is_some_and(|last| corr <= *last) {
            return false;
        }
        self.last_seen_corr.insert(player, corr);
        true
    }

    /// Authority gate + idempotency + apply, in that order.
    ///
    /// 1. `authorized == false` -> `Err(KernelError::TestRejected)`; the
    ///    action never runs and the `corr` is not recorded (the command was
    ///    never accepted, so a retransmit is not a duplicate).
    /// 2. `corr <= last_seen[player]` -> silently dropped, `Ok(())`.
    /// 3. fresh -> the action runs; on `Err` the `corr` is not recorded (the
    ///    effect did not happen, so a retry is not a duplicate); on `Ok` the
    ///    `corr` is recorded.
    pub fn apply_command<F: FnOnce() -> Result<(), KernelError>>(
        &mut self,
        player: PlayerId,
        corr: u64,
        authorized: bool,
        action: F,
    ) -> Result<(), KernelError> {
        if !authorized {
            return Err(KernelError::TestRejected);
        }
        if self.last_seen_corr.get(&player.0).is_some_and(|last| corr <= *last) {
            return Ok(());
        }
        action()?;
        self.last_seen_corr.insert(player.0, corr);
        Ok(())
    }
}

/// A stream epoch: a controller restart resets `seq` to a NEW epoch (not to
/// zero), so a client can tell a rewind from a wrap (card 10 §B.1.2).
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct StreamEpoch {
    pub epoch: u64,
    pub seq: u64,
}

impl StreamEpoch {
    /// The next sequence on this stream. At `seq == u64::MAX` the stream
    /// wraps to `seq = 0` under a bumped epoch; the wrapped frame is still
    /// accepted (it is a wrap, not a rewind).
    pub fn advance(&mut self) -> Option<Self> {
        if self.seq == u64::MAX {
            self.epoch = self.epoch.checked_add(1)?;
            self.seq = 0;
        } else {
            self.seq += 1;
        }
        Some(*self)
    }

    /// A controller restart: a distinct epoch, `seq` back to 0.
    pub fn restart(&mut self) -> Option<()> {
        self.epoch = self.epoch.checked_add(1)?;
        self.seq = 0;
        Some(())
    }
}

/// The reader side of a bridge stream: per-stream monotonic `seq` with
/// epoch-aware wrap (card 10 §B.1.2, §E `seq_wrap_u64_max`).
///
/// - `seq > last` in the same epoch: accepted.
/// - `seq == last`: a duplicate, silently dropped (never a reject).
/// - `seq < last` in the same epoch: a rewind, rejected (counted).
/// - a new epoch: accepted (restart or wrap); a same-`seq` rewind after the
///   wrap is NOT accepted (the epoch distinguishes it).
#[derive(Clone, Debug, Default)]
pub struct FrameStream {
    last: BTreeMap<u32, StreamEpoch>,
    pub accepted: u64,
    pub dropped_dupe: u64,
    pub rejected: u64,
}

impl FrameStream {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn accept_frame(&mut self, stream_id: u32, epoch: u64, seq: u64) -> bool {
        match self.last.get(&stream_id).copied() {
            None => {
                self.last.insert(stream_id, StreamEpoch { epoch, seq });
                self.accepted += 1;
                true
            }
            Some(prev) if epoch > prev.epoch => {
                self.last.insert(stream_id, StreamEpoch { epoch, seq });
                self.accepted += 1;
                true
            }
            Some(prev) if epoch == prev.epoch && seq > prev.seq => {
                self.last.insert(stream_id, StreamEpoch { epoch, seq });
                self.accepted += 1;
                true
            }
            Some(prev) if epoch == prev.epoch && seq == prev.seq => {
                self.dropped_dupe += 1;
                false
            }
            Some(_) => {
                self.rejected += 1;
                false
            }
        }
    }
}

/// A wire frame: the 32-byte slot header plus the UTF-8 payload (card 10
/// §B.2). The epoch is carried out-of-band in the model (the header has no
/// epoch field; the controller restarts `seq` under a new epoch).
#[derive(Clone, Debug)]
pub struct WireFrame {
    pub epoch: u64,
    pub header: BridgeFrameHeaderRaw,
    pub payload: String,
}

/// Emit all bridge lines produced by one tick as one batch: every frame
/// shares the tick's `tick_id` (same-tick grouping, card 10 §B.1.1) and the
/// per-stream `seq` advances strictly within the batch.
pub fn frame_tick(
    magic: u32,
    stream_id: u32,
    tick: u64,
    sequence: &mut StreamEpoch,
    payloads: &[String],
) -> Option<Vec<WireFrame>> {
    let mut cursor = *sequence;
    let mut batch = Vec::with_capacity(payloads.len());
    for payload in payloads {
        let position = cursor.advance()?;
        batch.push(WireFrame {
            epoch: position.epoch,
            header: BridgeFrameHeaderRaw {
                magic,
                stream_id,
                tick_id: tick,
                seq: position.seq,
                length: u32::try_from(payload.len()).ok()?,
                reserved: 0,
            },
            payload: payload.clone(),
        });
    }
    *sequence = cursor;
    Some(batch)
}

/// The typed/frame codec path (card 10 §C): newline-delimited JSON objects
/// with `kind` / `seq` / `tick` / `corr` / `payload`. The kernel models the
/// wire bytes exactly — including the unsigned 64-bit numbers above 2^53 that
/// clients must parse as integers, not doubles (`protocol.md` line 37).
///
/// Model-only boundary: this is the typed model of the wire format, not a
/// JSON parser over arbitrary input and not the native transport.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum WireKind {
    InteractionQueued,
    FullSnapshot,
    DeltaSnapshot,
    SimClaimed,
    SimReleased,
    GameSpeedChanged,
    SimAutonomyChanged,
    PlayerJoined,
    PlayerLeft,
    SetClientRole,
    WorldTimeChanged,
}

impl WireKind {
    pub fn as_str(self) -> &'static str {
        match self {
            WireKind::InteractionQueued => "InteractionQueued",
            WireKind::FullSnapshot => "FullSnapshot",
            WireKind::DeltaSnapshot => "DeltaSnapshot",
            WireKind::SimClaimed => "SimClaimed",
            WireKind::SimReleased => "SimReleased",
            WireKind::GameSpeedChanged => "GameSpeedChanged",
            WireKind::SimAutonomyChanged => "SimAutonomyChanged",
            WireKind::PlayerJoined => "PlayerJoined",
            WireKind::PlayerLeft => "PlayerLeft",
            WireKind::SetClientRole => "SetClientRole",
            WireKind::WorldTimeChanged => "WorldTimeChanged",
        }
    }

    fn parse(s: &str) -> Option<Self> {
        Some(match s {
            "InteractionQueued" => WireKind::InteractionQueued,
            "FullSnapshot" => WireKind::FullSnapshot,
            "DeltaSnapshot" => WireKind::DeltaSnapshot,
            "SimClaimed" => WireKind::SimClaimed,
            "SimReleased" => WireKind::SimReleased,
            "GameSpeedChanged" => WireKind::GameSpeedChanged,
            "SimAutonomyChanged" => WireKind::SimAutonomyChanged,
            "PlayerJoined" => WireKind::PlayerJoined,
            "PlayerLeft" => WireKind::PlayerLeft,
            "SetClientRole" => WireKind::SetClientRole,
            "WorldTimeChanged" => WireKind::WorldTimeChanged,
            _ => return None,
        })
    }
}

/// A newline-delimited public-protocol message (card 10 §C).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct WireMessage {
    pub kind: WireKind,
    pub seq: u64,
    pub tick: u64,
    /// Client -> server commands only; the idempotency key.
    pub corr: Option<u64>,
    pub payload: String,
}

/// Parse an unsigned 64-bit integer exactly. A double-based parser would
/// truncate values above 2^53; this one does not (card 10 §E
/// `unsigned_u64_above_2_53_round_trip`).
fn parse_u64_exact(s: &str) -> Option<u64> {
    let mut v: u64 = 0;
    for c in s.chars() {
        let d = c.to_digit(10)?;
        v = v.checked_mul(10)?.checked_add(u64::from(d))?;
    }
    Some(v)
}

impl PublicProtocolCodec {
    /// Encode a message as one newline-terminated JSON object. `u64` fields
    /// are emitted as bare integers (never doubles).
    pub fn encode_message(m: &WireMessage) -> String {
        let corr = m
            .corr
            .map(|c| format!(",\"corr\":{c}"))
            .unwrap_or_default();
        format!(
            "{{\"kind\":\"{}\",\"seq\":{},\"tick\":{}{corr},\"payload\":{}}}\n",
            m.kind.as_str(),
            m.seq,
            m.tick,
            json_string(&m.payload)
        )
    }

    /// Decode one newline-terminated JSON object. Returns `None` on any
    /// malformed field (the model does not guess).
    pub fn decode_message(line: &str) -> Option<WireMessage> {
        let body = line.strip_suffix('\n').unwrap_or(line);
        let mut fields: BTreeMap<String, String> = BTreeMap::new();
        let bytes = body.as_bytes();
        if bytes.first() != Some(&b'{') {
            return None;
        }
        let mut i = 1;
        while i < bytes.len() {
            // expect "key"
            if bytes[i] != b'"' {
                return None;
            }
            i += 1;
            let kstart = i;
            while i < bytes.len() && bytes[i] != b'"' {
                i += 1;
            }
            if i >= bytes.len() {
                return None;
            }
            let key = body[kstart..i].to_string();
            i += 1; // closing quote
            if i >= bytes.len() || bytes[i] != b':' {
                return None;
            }
            i += 1;
            // value: string, integer, or object
            if i < bytes.len() && bytes[i] == b'"' {
                fields.insert(key, parse_json_string(body, &mut i)?);
            } else if i < bytes.len() && (bytes[i] == b'{' || bytes[i] == b'[') {
                // nested object/array: capture to the matching close
                let open = bytes[i];
                let close = if open == b'{' { b'}' } else { b']' };
                let mut depth = 0i32;
                let vstart = i;
                while i < bytes.len() {
                    match bytes[i] {
                        b'"' => {
                            i += 1;
                            while i < bytes.len() && bytes[i] != b'"' {
                                i += 1;
                            }
                            if i >= bytes.len() {
                                return None;
                            }
                        }
                        b if b == open => depth += 1,
                        b if b == close => {
                            depth -= 1;
                            if depth == 0 {
                                break;
                            }
                        }
                        _ => {}
                    }
                    i += 1;
                }
                if depth != 0 {
                    return None;
                }
                fields.insert(key, body[vstart..i].to_string());
                i += 1;
            } else {
                let vstart = i;
                while i < bytes.len() && bytes[i] != b',' && bytes[i] != b'}' {
                    i += 1;
                }
                fields.insert(key, body[vstart..i].trim().to_string());
            }
            if i >= bytes.len() {
                return None;
            }
            match bytes[i] {
                b',' => i += 1,
                b'}' => break,
                _ => return None,
            }
        }
        let kind = WireKind::parse(fields.get("kind")?.as_str())?;
        let seq = parse_u64_exact(fields.get("seq")?.as_str())?;
        let tick = parse_u64_exact(fields.get("tick")?.as_str())?;
        let corr = match fields.get("corr") {
            Some(c) => Some(parse_u64_exact(c.as_str())?),
            None => None,
        };
        let payload = fields.get("payload").cloned().unwrap_or_default();
        Some(WireMessage { kind, seq, tick, corr, payload })
    }
}

/// Read the string literals emitted by `json_string`, including escaped
/// control characters. The cursor remains on a UTF-8 character boundary.
fn parse_json_string(body: &str, cursor: &mut usize) -> Option<String> {
    *cursor += 1;
    let mut value = String::new();
    loop {
        let c = body.get(*cursor..)?.chars().next()?;
        *cursor += c.len_utf8();
        match c {
            '"' => return Some(value),
            '\\' => {
                let escaped = body.get(*cursor..)?.chars().next()?;
                *cursor += escaped.len_utf8();
                value.push(match escaped {
                    '"' => '"', '\\' => '\\', '/' => '/',
                    'n' => '\n', 'r' => '\r', 't' => '\t',
                    'b' => '\u{8}', 'f' => '\u{c}',
                    'u' => {
                        let digits = body.get(*cursor..cursor.checked_add(4)?)?;
                        let code = u32::from_str_radix(digits, 16).ok()?;
                        *cursor += 4;
                        char::from_u32(code)?
                    }
                    _ => return None,
                });
            }
            c if c < ' ' => return None,
            _ => value.push(c),
        }
    }
}

/// Minimal JSON string literal for the model's payload field.
fn json_string(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 2);
    out.push('"');
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04x}", c as u32)),
            c => out.push(c),
        }
    }
    out.push('"');
    out
}

#[cfg(test)]
mod contract_tests {
    use super::*;

    #[test]
    fn current_world_weak_reference_allows_commit() {
        let mut tls = TickLoopState::new();
        let weak = Arc::downgrade(&tls.world);
        tls.commit_tick(WorldDelta { tick: 1, ..WorldDelta::default() });
        assert_eq!(tls.world.tick, 1);
        assert!(weak.upgrade().is_none());
    }

    #[test]
    fn inventory_owner_boundaries_change_fingerprint() {
        let scope = JoinScope::new();
        let objects = BTreeMap::new();
        let relationships = BTreeMap::new();
        let one = BTreeMap::from([(1, vec![2])]);
        let two = BTreeMap::from([(1, vec![]), (2, vec![])]);
        assert_ne!(scope.fingerprint(&objects, &one, &relationships),
            scope.fingerprint(&objects, &two, &relationships));
    }

    #[test]
    fn held_snapshot_and_weak_reader_never_block_or_panic() {
        let mut tls = TickLoopState::new();
        let reader = tls.snapshot_for_join();
        let mut counters = JoinCounters::default();
        counters.join_started();
        counters.presentation_sent(0, true);
        for tick in 1..=10_000 {
            tls.commit_tick(WorldDelta { tick, object_add: Some((1, tick)), object_remove: None });
            counters.presentation_sent(tick, true);
        }
        counters.join_finished();
        assert_eq!(reader.tick, 0);
        assert!(reader.objects.is_empty());
        assert_eq!(tls.world.objects[&1], 10_000);
        assert_eq!(
            (
                counters.tick_drops_during_join,
                counters.presentation_sent_gap_max_during_join
            ),
            (0, 1)
        );
        // The forked snapshot is a new allocation: the weak reference to the
        // pre-join snapshot does not panic and upgrades to nothing once the
        // reader (its last strong ref) is dropped.
        let weak = Arc::downgrade(&reader);
        drop(reader);
        assert!(weak.upgrade().is_none());
        // After the reader releases, in-place mutation resumes.
        tls.commit_tick(WorldDelta { tick: 10_001, ..WorldDelta::default() });
        assert_eq!(tls.world.tick, 10_001);
    }

    #[test]
    fn epoch_batches_and_authorized_idempotent_commands() {
        let mut seq = StreamEpoch { epoch: 2, seq: u64::MAX - 1 };
        let batch = frame_tick(7, 1, 42, &mut seq, &["a".into(), "b".into()]).unwrap();
        assert_eq!((batch[0].epoch, batch[0].header.seq), (2, u64::MAX));
        assert_eq!((batch[1].epoch, batch[1].header.seq), (3, 0));
        assert!(batch.iter().all(|f| f.header.tick_id == 42));
        let mut codec = PublicProtocolCodec::new();
        let mut effects = 0;
        for corr in [u64::MAX, u64::MAX, 0] {
            codec
                .apply_command(PlayerId(1), corr, true, || {
                    effects += 1;
                    Ok(())
                })
                .unwrap();
        }
        assert_eq!(effects, 1);
        assert_eq!(
            codec.apply_command(PlayerId(2), 1, false, || panic!("unauthorized")),
            Err(KernelError::TestRejected)
        );
    }
}

/// Card 26 §C golden-corpus hook (canary 10, + corr dedup).
#[cfg(test)]
mod kdiff {
    use super::*;

    #[test]
    fn kdiff_canary_10() {
        let mut codec = PublicProtocolCodec::new();
        let fresh = codec.accept_command(7, 5);
        let dup = codec.accept_command(7, 5);
        let lower = codec.accept_command(7, 4);
        let dedup_ok = (fresh && !dup && !lower) as u64;
        println!("KDIFF|canary=10|case=corr_dedup|got={:016x}", dedup_ok);
        assert_eq!(dedup_ok, 1);
        // CoW late-join: a reader held across commits never sees a torn or
        // advanced snapshot, and the writer never blocks.
        let mut tls = TickLoopState::new();
        let reader = tls.snapshot_for_join();
        for t in 0..10u64 {
            tls.commit_tick(WorldDelta { tick: t, object_add: Some((t, t)), object_remove: None });
        }
        let frozen = (reader.tick == 0) as u64;
        println!("KDIFF|canary=10|case=cow_reader_frozen|got={:016x}", frozen);
        assert_eq!(frozen, 1);
        assert_eq!(tls.world.tick, 9);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn snapshot_join_zero_dropped_ticks() {
        let mut state = TickLoopState::new();
        let reader = state.snapshot_for_join();
        for t in 0..10_000 {
            state.commit_tick(WorldDelta { tick: t, object_add: None, object_remove: None });
        }
        assert_eq!(reader.tick, 0);
        assert_eq!(state.world.tick, 9999);
    }

    #[test]
    fn snapshot_cow_fork_correctness() {
        let mut state = TickLoopState::new();
        let reader = state.snapshot_for_join();
        state.commit_tick(WorldDelta { tick: 1, object_add: Some((10, 1)), object_remove: None });
        state.commit_tick(WorldDelta { tick: 2, object_add: Some((20, 2)), object_remove: None });
        assert_eq!(reader.objects.len(), 0);
        assert_eq!(state.world.objects.len(), 2);
        assert_eq!(state.world.objects.get(&10), Some(&1));
        assert_eq!(state.world.objects.get(&20), Some(&2));
    }

    #[test]
    fn corr_dedup_same_and_lower() {
        let mut c = PublicProtocolCodec::new();
        assert!(c.accept_command(1, 42));
        assert!(!c.accept_command(1, 42));
        assert!(!c.accept_command(1, 5));
        assert!(c.accept_command(1, 43));
    }

    #[test]
    fn fingerprint_hit_and_miss() {
        let mut s = JoinReuseState::default();
        let snap = Arc::new(WorldSnapshot::default());
        let fp = JoinFingerprint { parts: [1, 2, 3] };
        assert!(s.on_request_world(&fp).is_none());
        assert_eq!(s.counters.misses, 1);
        assert_eq!(
            s.counters.join_reuse_last_verdict,
            Some(JoinVerdict::Miss(JoinMissReason::NoFingerprintYet))
        );
        s.on_fresh_checkpoint(fp, Arc::clone(&snap));
        assert!(s.on_request_world(&fp).is_some());
        assert_eq!(s.counters.hits, 1);
        let other = JoinFingerprint { parts: [1, 2, 4] };
        assert!(s.on_request_world(&other).is_none());
        assert_eq!(s.counters.misses, 2);
        assert_eq!(
            s.counters.join_reuse_last_verdict,
            Some(JoinVerdict::Miss(JoinMissReason::Relationships))
        );
        assert_eq!(
            s.counters.misses_by_reason.get(&JoinMissReason::Relationships),
            Some(&1)
        );
    }

    #[test]
    fn bridge_header_layout_is_32_bytes() {
        assert_eq!(std::mem::size_of::<BridgeFrameHeaderRaw>(), BRIDGE_FRAME_HEADER_SIZE);
    }

    // ---------- card 10 §E witnesses ----------

    #[test]
    fn seq_wrap_u64_max() {
        let mut stream = FrameStream::new();
        // Stream at u64::MAX - 1; emit 3 frames: u64::MAX, then wrap to 0
        // under a bumped epoch, then 1.
        let mut epoch = StreamEpoch { epoch: 1, seq: u64::MAX - 1 };
        let f1 = epoch.advance().unwrap();
        let f2 = epoch.advance().unwrap();
        let f3 = epoch.advance().unwrap();
        assert_eq!((f1.epoch, f1.seq), (1, u64::MAX));
        assert_eq!((f2.epoch, f2.seq), (2, 0));
        assert_eq!((f3.epoch, f3.seq), (2, 1));
        assert!(stream.accept_frame(1, f1.epoch, f1.seq));
        assert!(stream.accept_frame(1, f2.epoch, f2.seq));
        assert!(stream.accept_frame(1, f3.epoch, f3.seq));
        // An earlier sequence in the same epoch is a rewind, not a duplicate.
        assert!(!stream.accept_frame(1, f2.epoch, f2.seq));
        assert_eq!(stream.dropped_dupe, 0);
        assert_eq!(stream.rejected, 1);
        // A same-epoch rewind below the last seq is rejected (counted).
        assert!(!stream.accept_frame(1, f3.epoch, 0));
        assert_eq!(stream.rejected, 2);
    }

    #[test]
    fn controller_restart_epoch_tag() {
        let mut stream = FrameStream::new();
        assert!(stream.accept_frame(7, 1, 100));
        assert!(stream.accept_frame(7, 1, 101));
        // Controller restarts mid-session: new epoch, seq back to 0.
        assert!(stream.accept_frame(7, 2, 0));
        assert!(stream.accept_frame(7, 2, 1));
        // The old epoch's seq is no longer accepted as a continuation.
        assert!(!stream.accept_frame(7, 1, 102));
        assert_eq!(stream.rejected, 1);
        assert_eq!(stream.dropped_dupe, 0);
    }

    #[test]
    fn same_tick_grouping() {
        let mut seq = StreamEpoch { epoch: 1, seq: 0 };
        let batch = frame_tick(
            0x5349_4D39,
            3,
            1000,
            &mut seq,
            &["a".into(), "b".into(), "c".into(), "d".into(), "e".into()],
        )
        .unwrap();
        assert_eq!(batch.len(), 5);
        // All 5 lines produced on tick 1000 share the tick_id; the reader
        // replays them atomically.
        assert!(batch.iter().all(|f| f.header.tick_id == 1000));
        let mut seen = 0u64;
        for f in &batch {
            assert!(f.header.seq > seen);
            seen = f.header.seq;
        }
        // A reader joining mid-tick sees the whole batch, not a subset.
        let mut reader = FrameStream::new();
        for f in &batch {
            assert!(reader.accept_frame(f.header.stream_id, f.epoch, f.header.seq));
        }
        assert_eq!(reader.accepted, 5);
    }

    #[test]
    fn unsigned_u64_above_2_53_round_trip() {
        // Above 2^53 and not exactly representable as an f64.
        let big = u64::MAX - 2;
        let m = WireMessage {
            kind: WireKind::FullSnapshot,
            seq: big,
            tick: big,
            corr: Some(big),
            payload: "sim".into(),
        };
        let encoded = PublicProtocolCodec::encode_message(&m);
        let decoded = PublicProtocolCodec::decode_message(&encoded).unwrap();
        assert_eq!(decoded, m);
        // A double-based parser would truncate: 2^64/16 as f64 loses bits.
        let as_f64 = big as f64;
        assert_ne!(as_f64 as u64, big);
        assert_eq!(decoded.seq, big);
    }

    #[test]
    fn fingerprint_hash_stable_when_nothing_changed() {
        let scope = JoinScope {
            households: BTreeMap::from([(1u32, 10u32)]),
            lots: BTreeMap::from([(10u32, ()), (11u32, ())]),
        };
        let objects = BTreeMap::from([(100u64, (7u64, 0u64)), (101u64, (8u64, 4u64))]);
        let inventories = BTreeMap::from([(1u64, vec![100u64, 101u64])]);
        let relationships = BTreeMap::from([((1u32, 2u32), (0u32, 40i32))]);
        let a = scope.fingerprint(&objects, &inventories, &relationships);
        let b = scope.fingerprint(&objects, &inventories, &relationships);
        assert_eq!(a, b);
        assert_eq!(a.compare(&b), None);
    }

    #[test]
    fn fingerprint_hash_changes_on_object_add() {
        let scope = JoinScope::new();
        let inventories = BTreeMap::new();
        let relationships = BTreeMap::new();
        let before = BTreeMap::from([(100u64, (7u64, 0u64))]);
        let mut after = before.clone();
        after.insert(101, (9, 0));
        let fa = scope.fingerprint(&before, &inventories, &relationships);
        let fb = scope.fingerprint(&after, &inventories, &relationships);
        assert_ne!(fa, fb);
        assert_eq!(fa.compare(&fb), Some(JoinMissReason::Objects));
    }

    #[test]
    fn fingerprint_hash_changes_on_liking_step() {
        let scope = JoinScope::new();
        let objects = BTreeMap::new();
        let inventories = BTreeMap::new();
        let before = BTreeMap::from([((1u32, 2u32), (0u32, 40i32))]);
        let after = BTreeMap::from([((1u32, 2u32), (0u32, 50i32))]); // +10: crosses a step
        let fa = scope.fingerprint(&objects, &inventories, &before);
        let fb = scope.fingerprint(&objects, &inventories, &after);
        assert_ne!(fa, fb);
        assert_eq!(fa.compare(&fb), Some(JoinMissReason::Relationships));
    }

    #[test]
    fn fingerprint_hash_ignores_within_step_change() {
        let scope = JoinScope::new();
        let objects = BTreeMap::new();
        let inventories = BTreeMap::new();
        let before = BTreeMap::from([((1u32, 2u32), (0u32, 40i32))]);
        let after = BTreeMap::from([((1u32, 2u32), (0u32, 45i32))]); // +5: same step
        let fa = scope.fingerprint(&objects, &inventories, &before);
        let fb = scope.fingerprint(&objects, &inventories, &after);
        assert_eq!(fa, fb);
        // The join would HIT.
        let mut s = JoinReuseState::default();
        s.on_fresh_checkpoint(fa, Arc::new(WorldSnapshot::default()));
        assert!(s.on_request_world(&fb).is_some());
        assert_eq!(s.counters.hits, 1);
    }

    #[test]
    fn single_flight_two_joins_in_one_window() {
        let tls = TickLoopState::new();
        let mut s = JoinReuseState::default();
        let a = s.gather_single_flight(&tls);
        let b = s.gather_single_flight(&tls);
        // Both requests share the same immutable Arc (single-flight).
        assert!(Arc::ptr_eq(&a, &b));
        assert_eq!(Arc::strong_count(&a), 4); // tls.world + in_flight + a + b
        // A fresh checkpoint clears the in-flight gather.
        s.on_fresh_checkpoint(JoinFingerprint::default(), Arc::new(WorldSnapshot::default()));
        assert!(s.in_flight_snapshot().is_none());
    }

    #[test]
    fn command_authority_before_mutation_and_dedup() {
        let mut codec = PublicProtocolCodec::new();
        let mut effects = 0;
        // Unauthorized: no effect, and the corr is NOT consumed.
        assert_eq!(
            codec.apply_command(PlayerId(1), 7, false, || {
                effects += 1;
                Ok(())
            }),
            Err(KernelError::TestRejected)
        );
        assert_eq!(effects, 0);
        // The same corr, now authorized, is still fresh: exactly one effect.
        codec
            .apply_command(PlayerId(1), 7, true, || {
                effects += 1;
                Ok(())
            })
            .unwrap();
        assert_eq!(effects, 1);
        // A duplicate of the applied corr: no second effect, Ok(()).
        codec
            .apply_command(PlayerId(1), 7, true, || {
                effects += 1;
                Ok(())
            })
            .unwrap();
        assert_eq!(effects, 1);
        // A failed action does not consume the corr: the retry applies once.
        let mut ran = 0;
        codec
            .apply_command(PlayerId(1), 8, true, || {
                ran += 1;
                Err(KernelError::TestRejected)
            })
            .unwrap_err();
        codec
            .apply_command(PlayerId(1), 8, true, || {
                ran += 1;
                Ok(())
            })
            .unwrap();
        assert_eq!(ran, 2);
    }

    #[test]
    fn presentation_gap_accounting_is_real() {
        let mut c = JoinCounters::default();
        c.join_started();
        c.presentation_sent(0, true);
        c.presentation_sent(1, true);
        c.presentation_sent(2, true);
        assert_eq!((c.tick_drops_during_join, c.presentation_sent_gap_max_during_join), (0, 1));
        // A stall: the next frame is 3 ticks later -> gap 3, 2 dropped ticks.
        c.presentation_sent(5, true);
        assert_eq!((c.tick_drops_during_join, c.presentation_sent_gap_max_during_join), (2, 3));
        c.join_finished();
        // Outside a join the gap is not accounted.
        c.presentation_sent(100, false);
        assert_eq!((c.tick_drops_during_join, c.presentation_sent_gap_max_during_join), (2, 3));
    }

    #[test]
    fn join_counters_track_every_reason() {
        let mut s = JoinReuseState::default();
        let snap = Arc::new(WorldSnapshot::default());
        let fp = JoinFingerprint { parts: [1, 2, 3] };
        s.on_fresh_checkpoint(fp, Arc::clone(&snap));
        // objects part changed
        assert!(s.on_request_world(&JoinFingerprint { parts: [9, 2, 3] }).is_none());
        assert_eq!(s.counters.misses_by_reason.get(&JoinMissReason::Objects), Some(&1));
        // inventories part changed
        assert!(s.on_request_world(&JoinFingerprint { parts: [1, 9, 3] }).is_none());
        assert_eq!(
            s.counters.misses_by_reason.get(&JoinMissReason::Inventories),
            Some(&1)
        );
        // relationships part changed
        assert!(s.on_request_world(&JoinFingerprint { parts: [1, 2, 9] }).is_none());
        assert_eq!(
            s.counters.misses_by_reason.get(&JoinMissReason::Relationships),
            Some(&1)
        );
        assert_eq!(s.counters.misses, 3);
        assert_eq!(s.counters.hits, 0);
    }

    #[test]
    fn typed_frame_codec_round_trip() {
        let m = WireMessage {
            kind: WireKind::WorldTimeChanged,
            seq: 1234,
            tick: 5678,
            corr: None,
            payload: "{\"day\":3}".into(),
        };
        let encoded = PublicProtocolCodec::encode_message(&m);
        let decoded = PublicProtocolCodec::decode_message(&encoded).unwrap();
        assert_eq!(decoded, m);
        // A command with corr.
        let c = WireMessage {
            kind: WireKind::InteractionQueued,
            seq: 1,
            tick: 2,
            corr: Some(u64::MAX - 2),
            payload: "sit".into(),
        };
        let decoded = PublicProtocolCodec::decode_message(&PublicProtocolCodec::encode_message(&c))
            .unwrap();
        assert_eq!(decoded, c);
        // Malformed: not JSON.
        assert!(PublicProtocolCodec::decode_message("nope").is_none());
        // Malformed: unknown kind.
        assert!(PublicProtocolCodec::decode_message(
            "{\"kind\":\"Bogus\",\"seq\":1,\"tick\":2,\"payload\":{}}"
        )
        .is_none());
    }
}
