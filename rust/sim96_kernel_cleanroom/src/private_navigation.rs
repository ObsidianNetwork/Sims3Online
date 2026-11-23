#![forbid(unsafe_code)]
//! Card 55 inherited-capability protocol. Never accepted on a public socket.
//! The byte contract and terminal rules are in card55-private-frontdoor-handoff.md.

use std::io::{self, Read, Write};
use crate::live_navigation::{Bindings, CellBinding, PortalBinding, Provenance, Request};
use crate::routing_astar::DynamicObstacle;
use crate::SimId;
use std::collections::{BTreeMap, BTreeSet, VecDeque};
use crate::runtime_bridge::AuthorityWorld;
use crate::wire::PlayerId;
use crate::live_navigation::{InputError, PinnedPlan};
use crate::routing_astar::{LegKind, NavGraph};

pub const VERSION: u16 = 1;
pub const MAX_RECORD: usize = 128 * 1024 * 1024;
pub const MAX_ENVELOPE: usize = 1024 * 1024;
pub const MAX_OUTSTANDING: usize = 256;
pub const MAX_SIMNAV: usize = crate::live_navigation::MAX_INPUT_BYTES;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CodecError { Truncated, Oversized, Version, Operation, Invalid, Trailing, Allocation }
impl std::fmt::Display for CodecError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result { write!(f, "{self:?}") }
}
impl std::error::Error for CodecError {}

/// Closed, versioned result codes. Staged is the sole nonterminal result.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u16)]
pub enum Status {
    Staged = 0, Done = 1, Refused = 2, Stale = 3, Unauthorized = 4,
    Cancelled = 5, Saturated = 6, TransportClosed = 7, Expired = 8,
    Invalid = 9, DeliveryFailed = 10,
}
impl Status {
    fn decode(n: u16) -> Result<Self, CodecError> {
        Ok(match n {
            0 => Self::Staged, 1 => Self::Done, 2 => Self::Refused, 3 => Self::Stale,
            4 => Self::Unauthorized, 5 => Self::Cancelled, 6 => Self::Saturated,
            7 => Self::TransportClosed, 8 => Self::Expired, 9 => Self::Invalid,
            10 => Self::DeliveryFailed, _ => return Err(CodecError::Invalid),
        })
    }
    pub fn is_terminal(self) -> bool { self != Self::Staged }
    fn code(self) -> u16 {
        match self {
            Self::Staged => 0, Self::Done => 1, Self::Refused => 2, Self::Stale => 3,
            Self::Unauthorized => 4, Self::Cancelled => 5, Self::Saturated => 6,
            Self::TransportClosed => 7, Self::Expired => 8, Self::Invalid => 9,
            Self::DeliveryFailed => 10,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Origin { Engine, Player(u64) }

/// IPv4 tuples are the parent's actual connected sidecar endpoints, not an id
/// supplied over the public protocol. Bind refuses unless HELLO has completed.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SessionBinding {
    pub session: u64,
    pub client_ip: [u8; 4],
    pub client_port: u16,
    pub server_ip: [u8; 4],
    pub server_port: u16,
}

pub enum Message {
    Ready { pid: u32, port: u16 },
    BindSession(SessionBinding),
    Install { bindings: Bindings, simnav: Vec<u8> },
    Invalidate { epoch: u64, generation: u64 },
    Plan { origin: Origin, deadline_tick: u64, request: Request },
    CommitOffer,
    Cancel,
    Result { operation: u8, status: Status, epoch: u64, generation: u64, data: Vec<u8> },
}
impl Message {
    pub fn operation(&self) -> u8 {
        match self {
            Self::Ready { .. } => 1, Self::BindSession(_) => 2, Self::Install { .. } => 3,
            Self::Invalidate { .. } => 4, Self::Plan { .. } => 5,
            Self::CommitOffer => 6, Self::Cancel => 7, Self::Result { .. } => 8,
        }
    }
}

pub struct Frame { pub id: u64, pub message: Message }

struct Decoder<'a> { bytes: &'a [u8] }
impl<'a> Decoder<'a> {
    fn take(&mut self, n: usize) -> Result<&'a [u8], CodecError> {
        let (head, tail) = self.bytes.split_at_checked(n).ok_or(CodecError::Truncated)?;
        self.bytes = tail;
        Ok(head)
    }
    fn array<const N: usize>(&mut self) -> Result<[u8; N], CodecError> {
        self.take(N)?.try_into().map_err(|_| CodecError::Truncated)
    }
    fn u8(&mut self) -> Result<u8, CodecError> { Ok(self.array::<1>()?[0]) }
    fn u16(&mut self) -> Result<u16, CodecError> { Ok(u16::from_le_bytes(self.array()?)) }
    fn i16(&mut self) -> Result<i16, CodecError> { Ok(i16::from_le_bytes(self.array()?)) }
    fn u32(&mut self) -> Result<u32, CodecError> { Ok(u32::from_le_bytes(self.array()?)) }
    fn i32(&mut self) -> Result<i32, CodecError> { Ok(i32::from_le_bytes(self.array()?)) }
    fn u64(&mut self) -> Result<u64, CodecError> { Ok(u64::from_le_bytes(self.array()?)) }
    fn xyz(&mut self) -> Result<[i32; 3], CodecError> { Ok([self.i32()?, self.i32()?, self.i32()?]) }
    fn identity(&mut self) -> Result<(u64,u64,i16), CodecError> {
        Ok((self.u64()?, self.u64()?, self.i16()?))
    }
    fn count(&mut self, width: usize, cap: usize) -> Result<usize, CodecError> {
        let n = usize::try_from(self.u32()?).map_err(|_| CodecError::Oversized)?;
        if n > cap || n > self.bytes.len() / width { return Err(CodecError::Oversized); }
        Ok(n)
    }
    fn blob(&mut self, cap: usize) -> Result<Vec<u8>, CodecError> {
        let n = self.count(1, cap)?;
        copy_bytes(self.take(n)?)
    }
    fn end(self) -> Result<(), CodecError> {
        if self.bytes.is_empty() { Ok(()) } else { Err(CodecError::Trailing) }
    }
}
fn reserve<T>(n: usize) -> Result<Vec<T>, CodecError> {
    let mut v = Vec::new();
    v.try_reserve_exact(n).map_err(|_| CodecError::Allocation)?;
    Ok(v)
}
fn copy_bytes(bytes: &[u8]) -> Result<Vec<u8>, CodecError> {
    let mut result = reserve(bytes.len())?;
    result.extend_from_slice(bytes);
    Ok(result)
}
fn size(n: usize) -> Result<[u8; 4], CodecError> {
    Ok(u32::try_from(n).map_err(|_| CodecError::Oversized)?.to_le_bytes())
}
fn xyz(out: &mut Vec<u8>, v: [i32; 3]) { for n in v { out.extend(n.to_le_bytes()); } }
fn identity(out: &mut Vec<u8>, v: (u64,u64,i16)) {
    out.extend(v.0.to_le_bytes()); out.extend(v.1.to_le_bytes()); out.extend(v.2.to_le_bytes());
}
fn bindings_size(b: &Bindings) -> Result<usize, CodecError> {
    [(b.cells.len(),22usize), (b.portals.len(),21), (b.keys_held.len(),2),
        (b.obstacles.len(),28), (b.actors.len(),12)].into_iter()
        .try_fold(41usize, |n,(count,width)| n.checked_add(count.checked_mul(width)
            .ok_or(CodecError::Oversized)?).ok_or(CodecError::Oversized))
}
fn encode_bindings(out: &mut Vec<u8>, b: &Bindings) -> Result<(), CodecError> {
    out.extend(b.schema_version.to_le_bytes()); out.extend(b.epoch.to_le_bytes());
    out.extend(b.generation.to_le_bytes());
    out.push(match b.provenance { Provenance::Measured => 0, Provenance::Authored => 1, Provenance::Inferred => 2 });
    out.extend(size(b.cells.len())?);
    for c in &b.cells {
        out.extend(c.cell_id.to_le_bytes()); identity(out, (c.lot_id,c.room_id,c.level));
    }
    out.extend(size(b.portals.len())?);
    for p in &b.portals {
        out.extend(p.portal_id.to_le_bytes()); out.extend(p.object_id.to_le_bytes());
        out.extend(p.entry_slot.to_le_bytes()); out.extend(p.exit_slot.to_le_bytes()); out.push(u8::from(p.cross_lot));
    }
    out.extend(size(b.keys_held.len())?);
    for key in &b.keys_held { out.extend(key.to_le_bytes()); }
    out.extend(size(b.obstacles.len())?);
    for o in &b.obstacles {
        out.extend(o.object_id.to_le_bytes()); out.extend(o.template.to_le_bytes());
        xyz(out, o.origin_cm); out.extend(o.scale_x1000.to_le_bytes());
    }
    out.extend(size(b.actors.len())?);
    for (actor,sim) in &b.actors { out.extend(actor.to_le_bytes()); out.extend(sim.0.to_le_bytes()); }
    Ok(())
}
fn decode_bindings(d: &mut Decoder<'_>) -> Result<Bindings, CodecError> {
    let schema_version = d.u32()?; let epoch = d.u64()?; let generation = d.u64()?;
    let provenance = match d.u8()? {
        0 => Provenance::Measured, 1 => Provenance::Authored, 2 => Provenance::Inferred,
        _ => return Err(CodecError::Invalid),
    };
    let count = d.count(22, MAX_RECORD / 22)?;
    let mut cells = reserve(count)?;
    for _ in 0..count {
        let cell_id = d.u32()?; let (lot_id,room_id,level) = d.identity()?;
        cells.push(CellBinding { cell_id,lot_id,room_id,level });
    }
    let count = d.count(21, MAX_RECORD / 21)?;
    let mut portals = reserve(count)?;
    for _ in 0..count {
        let portal_id = d.u32()?; let object_id = d.u64()?;
        let entry_slot = d.u32()?; let exit_slot = d.u32()?;
        let cross_lot = match d.u8()? { 0 => false, 1 => true, _ => return Err(CodecError::Invalid) };
        portals.push(PortalBinding { portal_id,object_id,entry_slot,exit_slot,cross_lot });
    }
    let count = d.count(2, MAX_RECORD / 2)?;
    let mut keys_held = reserve(count)?;
    for _ in 0..count { keys_held.push(d.u16()?); }
    let count = d.count(28, MAX_RECORD / 28)?;
    let mut obstacles = reserve(count)?;
    for _ in 0..count {
        obstacles.push(DynamicObstacle { object_id:d.u64()?, template:d.u32()?,
            origin_cm:d.xyz()?, scale_x1000:d.u32()? });
    }
    let count = d.count(12, MAX_RECORD / 12)?;
    let mut actors = reserve(count)?;
    for _ in 0..count { actors.push((d.u64()?,SimId(d.u32()?))); }
    Ok(Bindings { schema_version,epoch,generation,provenance,cells,portals,keys_held,obstacles,actors })
}

impl Frame {
    /// Includes the u32 prefix; reject total sizes before reserving any output.
    pub fn encode(&self) -> Result<Vec<u8>, CodecError> {
        let payload = match &self.message {
            Message::Ready { .. } => 6,
            Message::BindSession(_) => 20,
            Message::Install { bindings, simnav } => {
                if simnav.len() > MAX_SIMNAV { return Err(CodecError::Oversized); }
                bindings_size(bindings)?.checked_add(4).and_then(|n| n.checked_add(simnav.len()))
                    .ok_or(CodecError::Oversized)?
            }
            Message::Invalidate { .. } => 16,
            Message::Plan { .. } => 117,
            Message::CommitOffer | Message::Cancel => 0,
            Message::Result { data, .. } => {
                if data.len() > MAX_ENVELOPE { return Err(CodecError::Oversized); }
                23usize.checked_add(data.len()).ok_or(CodecError::Oversized)?
            }
        };
        let total = payload.checked_add(16).ok_or(CodecError::Oversized)?;
        if total > MAX_RECORD { return Err(CodecError::Oversized); }
        let mut out = reserve(total)?;
        out.extend(size(total - 4)?); out.extend(VERSION.to_le_bytes());
        out.push(self.message.operation()); out.push(0); out.extend(self.id.to_le_bytes());
        match &self.message {
            Message::Ready { pid,port } => { out.extend(pid.to_le_bytes()); out.extend(port.to_le_bytes()); }
            Message::BindSession(b) => {
                out.extend(b.session.to_le_bytes()); out.extend(b.client_ip); out.extend(b.client_port.to_le_bytes());
                out.extend(b.server_ip); out.extend(b.server_port.to_le_bytes());
            }
            Message::Install { bindings,simnav } => {
                encode_bindings(&mut out,bindings)?; out.extend(size(simnav.len())?); out.extend(simnav);
            }
            Message::Invalidate { epoch,generation } => {
                out.extend(epoch.to_le_bytes()); out.extend(generation.to_le_bytes());
            }
            Message::Plan { origin,deadline_tick,request:r } => {
                let (tag,session) = match origin { Origin::Engine => (0,0), Origin::Player(s) => (1,*s) };
                out.push(tag); out.extend(session.to_le_bytes()); out.extend(deadline_tick.to_le_bytes());
                out.extend(r.epoch.to_le_bytes()); out.extend(r.generation.to_le_bytes()); out.extend(r.corr.to_le_bytes());
                out.extend(r.actor_sim_id.to_le_bytes()); out.extend(r.kernel_sim.0.to_le_bytes());
                xyz(&mut out,r.start_xyz_mm); xyz(&mut out,r.goal_xyz_mm);
                identity(&mut out,r.start_identity); identity(&mut out,r.goal_identity); out.extend(r.flags.to_le_bytes());
            }
            Message::CommitOffer | Message::Cancel => {}
            Message::Result { operation,status,epoch,generation,data } => {
                out.push(*operation); out.extend(status.code().to_le_bytes());
                out.extend(epoch.to_le_bytes()); out.extend(generation.to_le_bytes());
                out.extend(size(data.len())?); out.extend(data);
            }
        }
        Ok(out)
    }
    pub fn decode(bytes: &[u8]) -> Result<Self, CodecError> {
        if bytes.len() > MAX_RECORD { return Err(CodecError::Oversized); }
        let mut d = Decoder { bytes };
        let len = usize::try_from(d.u32()?).map_err(|_| CodecError::Oversized)?;
        if len != d.bytes.len() { return Err(CodecError::Invalid); }
        if d.u16()? != VERSION { return Err(CodecError::Version); }
        let op = d.u8()?;
        if d.u8()? != 0 { return Err(CodecError::Invalid); }
        let id = d.u64()?;
        let message = match op {
            1 => Message::Ready { pid:d.u32()?,port:d.u16()? },
            2 => Message::BindSession(SessionBinding { session:d.u64()?,client_ip:d.array()?,
                client_port:d.u16()?,server_ip:d.array()?,server_port:d.u16()? }),
            3 => Message::Install { bindings:decode_bindings(&mut d)?,simnav:d.blob(MAX_SIMNAV)? },
            4 => Message::Invalidate { epoch:d.u64()?,generation:d.u64()? },
            5 => {
                let tag = d.u8()?; let session = d.u64()?;
                let origin = match (tag,session) {
                    (0,0) => Origin::Engine, (1,1..) => Origin::Player(session),
                    _ => return Err(CodecError::Invalid),
                };
                let deadline_tick = d.u64()?;
                let request = Request { epoch:d.u64()?,generation:d.u64()?,corr:d.u64()?,
                    actor_sim_id:d.u64()?,kernel_sim:SimId(d.u32()?),start_xyz_mm:d.xyz()?,goal_xyz_mm:d.xyz()?,
                    start_identity:d.identity()?,goal_identity:d.identity()?,flags:d.u32()? };
                Message::Plan { origin,deadline_tick,request }
            }
            6 => Message::CommitOffer, 7 => Message::Cancel,
            8 => {
                let operation = d.u8()?;
                if !(2..=7).contains(&operation) { return Err(CodecError::Operation); }
                Message::Result { operation,status:Status::decode(d.u16()?)?,
                    epoch:d.u64()?,generation:d.u64()?,data:d.blob(MAX_ENVELOPE)? }
            }
            _ => return Err(CodecError::Operation),
        };
        d.end()?;
        Ok(Self { id,message })
    }
    /// Reads only a bounded record; clean EOF is distinct from a truncated frame.
    pub fn read(reader: &mut impl Read) -> io::Result<Option<Self>> {
        let mut prefix = [0;4];
        if reader.read(&mut prefix[..1])? == 0 { return Ok(None); }
        reader.read_exact(&mut prefix[1..])?;
        let n = usize::try_from(u32::from_le_bytes(prefix)).map_err(io_invalid)?;
        if !(12..=MAX_RECORD - 4).contains(&n) { return Err(io_invalid(CodecError::Oversized)); }
        let mut bytes = reserve(n + 4).map_err(io_invalid)?;
        bytes.extend(prefix); bytes.resize(n + 4,0);
        reader.read_exact(&mut bytes[4..])?;
        Self::decode(&bytes).map(Some).map_err(io_invalid)
    }
    pub fn write(&self, writer: &mut impl Write) -> io::Result<()> {
        writer.write_all(&self.encode().map_err(io_invalid)?)?;
        writer.flush()
    }
}
fn io_invalid(error: impl std::fmt::Display) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData,error.to_string())
}

/// Typed, ordered execution metadata; portal slots come from authoritative
/// crossings, never from a coordinate lookup.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ExecutionLeg {
    pub index: u32, pub cell: u32, pub kind: u8, pub path_type: u32,
    pub from_cm: [i32;3], pub to_cm: [i32;3],
    pub object: u64, pub entry_slot: u32, pub exit_slot: u32,
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RustNavigationEnvelope {
    pub epoch: u64, pub generation: u64, pub corr: u64, pub actor: u64,
    pub start_mm: [i32;3], pub goal_mm: [i32;3],
    pub hash: u64, pub total_cost_mm: u64, pub legs: Vec<ExecutionLeg>,
}
impl RustNavigationEnvelope {
    pub fn encode(&self) -> Result<Vec<u8>, CodecError> {
        let n = self.legs.len().checked_mul(53).and_then(|n| n.checked_add(80))
            .ok_or(CodecError::Oversized)?;
        if n > MAX_ENVELOPE { return Err(CodecError::Oversized); }
        if self.legs.is_empty() || self.epoch == 0 || self.generation == 0 || self.corr == 0 || self.actor == 0 {
            return Err(CodecError::Invalid);
        }
        let mut out = reserve(n)?;
        out.extend(1u32.to_le_bytes()); out.extend(self.epoch.to_le_bytes());
        out.extend(self.generation.to_le_bytes()); out.extend(self.corr.to_le_bytes()); out.extend(self.actor.to_le_bytes());
        xyz(&mut out,self.start_mm); xyz(&mut out,self.goal_mm); out.extend(size(self.legs.len())?);
        out.extend(self.hash.to_le_bytes()); out.extend(self.total_cost_mm.to_le_bytes());
        for (i,l) in (0u32..).zip(&self.legs) {
            if l.index != i || l.kind > 4 { return Err(CodecError::Invalid); }
            out.extend(l.index.to_le_bytes()); out.extend(l.cell.to_le_bytes()); out.push(l.kind);
            out.extend(l.path_type.to_le_bytes()); xyz(&mut out,l.from_cm); xyz(&mut out,l.to_cm);
            out.extend(l.object.to_le_bytes()); out.extend(l.entry_slot.to_le_bytes()); out.extend(l.exit_slot.to_le_bytes());
        }
        Ok(out)
    }
    pub fn decode(bytes: &[u8]) -> Result<Self, CodecError> {
        if bytes.len() > MAX_ENVELOPE { return Err(CodecError::Oversized); }
        let mut d = Decoder { bytes };
        if d.u32()? != 1 { return Err(CodecError::Version); }
        let epoch = d.u64()?; let generation = d.u64()?; let corr = d.u64()?; let actor = d.u64()?;
        let start_mm = d.xyz()?; let goal_mm = d.xyz()?;
        // Hash and cost follow the count, so check their bytes before allocation.
        let count = usize::try_from(d.u32()?).map_err(|_| CodecError::Oversized)?;
        let hash = d.u64()?; let total_cost_mm = d.u64()?;
        if count == 0 || count > d.bytes.len() / 53 { return Err(CodecError::Invalid); }
        let mut legs = reserve(count)?;
        for index in 0..count {
            let leg = ExecutionLeg { index:d.u32()?,cell:d.u32()?,kind:d.u8()?,path_type:d.u32()?,
                from_cm:d.xyz()?,to_cm:d.xyz()?,object:d.u64()?,entry_slot:d.u32()?,exit_slot:d.u32()? };
            if usize::try_from(leg.index).ok() != Some(index) || leg.kind > 4 { return Err(CodecError::Invalid); }
            legs.push(leg);
        }
        d.end()?;
        if epoch == 0 || generation == 0 || corr == 0 || actor == 0 { return Err(CodecError::Invalid); }
        Ok(Self { epoch,generation,corr,actor,start_mm,goal_mm,hash,total_cost_mm,legs })
    }
}

/// Both outputs of one atomic offer. A forward is not an execution receipt.
pub struct Delivery { pub frame: Frame, pub presenters: Vec<String> }
struct Pending { origin: Origin, deadline: u64, plan: PinnedPlan }

#[derive(Default)]
pub struct Counters {
    pub accepted: u64, pub terminal: u64, pub duplicate: u64, pub malformed: u64,
    pub saturation: u64, pub delivery_failed: u64, pub eof: u64,
}

/// Owned by the hosting AuthorityWorld, never a planner replica.
#[derive(Default)]
pub struct PrivateNavigation {
    pub enabled: bool,
    pub counters: Counters,
    epoch: u64,
    generation: u64,
    high_id: u64,
    high_corr: u64,
    actors: BTreeMap<u64, SimId>,
    connections: BTreeMap<PlayerId, SessionBinding>,
    active_players: BTreeSet<PlayerId>,
    sessions: BTreeMap<u64, PlayerId>,
    pending: BTreeMap<u64, Pending>,
    committed: BTreeMap<SimId, (u64, Origin)>,
    outbox: VecDeque<Delivery>,
    sink: Option<std::sync::mpsc::SyncSender<Delivery>>,
    failed: bool,
}

impl PrivateNavigation {
    /// Request IDs and delivery state belong to one attachment. Epoch,
    /// generation, actor assignments and same-epoch corr replay fences do not.
    pub fn start_attachment(&mut self) {
        if self.enabled { return; }
        self.high_id = 0;
        self.failed = false;
        self.outbox.clear();
        self.sink = None;
        self.enabled = true;
    }
    /// Anonymous-pipe hosting uses a bounded ordered writer channel; actor
    /// hosting drains the same deliveries in its own serialized dispatch.
    pub fn set_sink(&mut self, sink: std::sync::mpsc::SyncSender<Delivery>) { self.sink = Some(sink); }
    pub fn take_deliveries(&mut self) -> Vec<Delivery> { self.outbox.drain(..).collect() }
    /// The headless host anchors this to its existing IdMap before checking
    /// an Install. A conflicting supplied pair then receives named Invalid.
    pub fn anchor_actor(&mut self, actor: u64, sim: SimId) -> bool {
        if actor == 0 || sim.0 == 0 || self.actors.get(&actor).is_some_and(|old| *old != sim)
            || self.actors.iter().any(|(id,old)| *id != actor && *old == sim) { return false; }
        self.actors.insert(actor,sim);
        true
    }
    pub fn connected(&mut self, player: PlayerId, tuple: SessionBinding) {
        if self.connections.len() < MAX_OUTSTANDING {
            self.connections.insert(player,tuple);
            self.active_players.insert(player);
        }
    }
    /// Called only by the actor's actual successful JoinSession path; session
    /// is its ConnId, not a player-supplied association or socket surrogate.
    pub fn actor_join(&mut self, session: u64, player: PlayerId) {
        if session != 0 && self.sessions.len() < MAX_OUTSTANDING {
            self.sessions.insert(session,player);
            self.active_players.insert(player);
        }
    }
    pub fn disconnected(&mut self, player: PlayerId) {
        self.connections.remove(&player);
        self.active_players.remove(&player);
        let sessions: Vec<_> = self.sessions.iter().filter_map(|(s,p)| (*p == player).then_some(*s)).collect();
        self.sessions.retain(|_,p| *p != player);
        let keys: Vec<_> = self.pending.iter().filter_map(|(id,p)| match p.origin {
            Origin::Player(s) if sessions.contains(&s) => Some(*id),
            Origin::Engine | Origin::Player(_) => None,
        }).collect();
        for key in keys { self.finish(key,Status::TransportClosed); }
        let sims: Vec<_> = self.committed.iter().filter_map(|(sim,(_,origin))| match origin {
            Origin::Player(s) if sessions.contains(s) => Some(*sim),
            Origin::Engine | Origin::Player(_) => None,
        }).collect();
        for sim in sims {
            if let Some((id,_)) = self.committed.remove(&sim) { self.revoke(id); }
        }
    }
    fn push(&mut self, delivery: Delivery) {
        if let Some(sink) = &self.sink {
            if sink.try_send(delivery).is_err() {
                self.counters.delivery_failed += 1; self.failed = true;
            }
        } else if self.outbox.len() < MAX_OUTSTANDING * 2 + 2 {
            self.outbox.push_back(delivery);
        } else {
            self.counters.delivery_failed += 1; self.failed = true;
        }
    }
    fn result(&mut self, id: u64, operation: u8, status: Status, data: Vec<u8>, presenters: Vec<String>) {
        if status.is_terminal() { self.counters.terminal += 1; }
        self.push(Delivery { frame:Frame { id,message:Message::Result {
            operation,status,epoch:self.epoch,generation:self.generation,data } },presenters });
    }
    fn finish(&mut self, id: u64, status: Status) {
        if self.pending.remove(&id).is_some() { self.result(id,5,status,Vec::new(),Vec::new()); }
    }
    pub fn cancel_all(&mut self, status: Status) {
        let keys: Vec<_> = self.pending.keys().copied().collect();
        for key in keys { self.finish(key,status); }
        let committed = std::mem::take(&mut self.committed);
        for (_,(id,_)) in committed { self.revoke(id); }
    }
    fn revoke(&mut self, id: u64) {
        // Revocation is a Cancel command, not a second terminal Result.
        self.push(Delivery { frame:Frame { id,message:Message::Cancel },presenters:Vec::new() });
    }
    pub fn claim_changed(&mut self, sim: SimId) {
        let keys: Vec<_> = self.pending.iter().filter_map(|(id,p)|
            (p.plan.request().kernel_sim == sim).then_some(*id)).collect();
        for id in keys { self.finish(id,Status::Cancelled); }
        if let Some((id,_)) = self.committed.remove(&sim) { self.revoke(id); }
    }
    pub fn expire(&mut self, tick: u64) {
        let keys: Vec<_> = self.pending.iter().filter_map(|(id,p)| (tick >= p.deadline).then_some(*id)).collect();
        for id in keys { self.finish(id,Status::Expired); }
    }
    pub fn failed(&self) -> bool { self.failed }
    pub fn close(&mut self, world: &mut AuthorityWorld, eof: bool) {
        if eof { self.counters.eof += 1; }
        self.cancel_all(Status::TransportClosed);
        self.enabled = false;
        world.invalidate_navigation(self.epoch,true);
        self.connections.clear(); self.sessions.clear(); self.active_players.clear();
        self.sink = None;
    }
    fn authorized(&self, world: &AuthorityWorld, origin: Origin, request: &Request) -> bool {
        if self.actors.get(&request.actor_sim_id) != Some(&request.kernel_sim) { return false; }
        match origin {
            Origin::Engine => true,
            Origin::Player(session) => self.sessions.get(&session).is_some_and(|player|
                self.active_players.contains(player) && world.authority.owners.get(&request.kernel_sim) == Some(player)),
        }
    }
    fn input_status(error: InputError) -> Status {
        match error {
            InputError::StaleGeneration | InputError::NoData => Status::Stale,
            InputError::InvalidBinding | InputError::AmbiguousCell => Status::Invalid,
            InputError::Kernel(_) => Status::Refused,
        }
    }
    /// Must run under the existing world's mutex or on its actor. All queued
    /// output uses nonblocking channels; no pipe/bridge IO occurs here.
    pub fn dispatch(&mut self, world: &mut AuthorityWorld, frame: Frame) {
        let id = frame.id;
        let op = frame.message.operation();
        if !self.enabled || id == 0 { self.counters.malformed += 1; return; }
        match frame.message {
            Message::CommitOffer | Message::Cancel => {
                let Some(pending) = self.pending.remove(&id) else {
                    if op == 7 {
                        let sim = self.committed.iter().find_map(|(sim,(key,_))| (*key == id).then_some(*sim));
                        if let Some(sim) = sim {
                            self.committed.remove(&sim);
                            self.revoke(id);
                            return;
                        }
                    }
                    self.counters.duplicate += 1; return;
                };
                let status = if op == 7 { Some(Status::Cancelled) }
                    else if world.clock.native_tick_cnt >= pending.deadline { Some(Status::Expired) }
                    else if !self.authorized(world,pending.origin,pending.plan.request()) { Some(Status::Unauthorized) }
                    else { world.validate_navigation_offer(&pending.plan).err().map(Self::input_status) };
                if let Some(status) = status { self.result(id,5,status,Vec::new(),Vec::new()); }
                else {
                    match offer(&pending.plan).and_then(|e| e.encode().map(|bytes| (e,bytes))) {
                        Ok((envelope,bytes)) => {
                            if let Some((old,_)) = self.committed.insert(pending.plan.request().kernel_sim,(id,pending.origin)) {
                                self.revoke(old);
                            }
                            let mut lines = vec![format!("Rt|{}|{}|{}|{:016x}|{}",envelope.corr,envelope.actor,
                                envelope.legs.len(),envelope.hash,envelope.total_cost_mm)];
                            for leg in &envelope.legs {
                                let [x,y,z] = leg.to_cm;
                                lines.push(format!("Vp|rt|{}|{}|{}|{}|{x}|{y}|{z}",envelope.corr,
                                    envelope.actor,leg.index,leg.path_type));
                            }
                            self.result(id,5,Status::Done,bytes,lines);
                        }
                        Err(_) => self.result(id,5,Status::Invalid,Vec::new(),Vec::new()),
                    }
                }
                return;
            }
            Message::Ready { .. } | Message::Result { .. } => { self.counters.malformed += 1; return; }
            Message::BindSession(_) | Message::Install { .. } | Message::Invalidate { .. } | Message::Plan { .. } => {}
        }
        if id <= self.high_id { self.counters.duplicate += 1; return; }
        self.high_id = id;
        self.counters.accepted += 1;
        let status = match frame.message {
            Message::BindSession(b) => {
                let tuple = |a: &SessionBinding| (a.client_ip,a.client_port,a.server_ip,a.server_port);
                let player = self.connections.iter().find_map(|(p,c)| (tuple(c) == tuple(&b)).then_some(*p));
                if b.session == 0 || self.sessions.contains_key(&b.session)
                    || player.is_none_or(|p| self.sessions.values().any(|bound| *bound == p)) {
                    Status::Unauthorized
                } else if let Some(player) = player {
                    self.sessions.insert(b.session,player);
                    self.result(id,op,Status::Done,player.0.to_le_bytes().to_vec(),Vec::new());
                    return;
                } else { Status::Unauthorized }
            }
            Message::Invalidate { epoch,generation } => {
                if epoch == 0 || epoch < self.epoch || generation < self.generation { Status::Stale }
                else {
                    self.cancel_all(Status::Cancelled);
                    if epoch != self.epoch { self.high_corr = 0; }
                    self.epoch = epoch; self.generation = generation;
                    world.invalidate_navigation(epoch,true);
                    Status::Done
                }
            }
            Message::Install { bindings,simnav } => {
                let valid_mapping = bindings.actors.iter().all(|(actor,sim)|
                    self.actors.get(actor).is_none_or(|old| old == sim)
                    && !self.actors.iter().any(|(a,s)| a != actor && s == sim));
                if bindings.epoch != self.epoch || bindings.generation <= self.generation { Status::Stale }
                else if !valid_mapping { Status::Invalid }
                else {
                    let generation = bindings.generation;
                    let actors = bindings.actors.clone();
                    match world.install_navigation(&simnav,bindings) {
                        Ok(()) => {
                            self.cancel_all(Status::Cancelled);
                            self.generation = generation;
                            self.actors.extend(actors);
                            Status::Done
                        }
                        Err(e) => Self::input_status(e),
                    }
                }
            }
            Message::Plan { origin,deadline_tick,request } => {
                let corr_reused = request.corr == 0 || request.corr <= self.high_corr;
                if request.epoch == self.epoch && !corr_reused { self.high_corr = request.corr; }
                if request.epoch != self.epoch || request.generation != self.generation { Status::Stale }
                else if corr_reused { Status::Invalid }
                else if self.pending.len() >= MAX_OUTSTANDING || (self.committed.len() >= MAX_OUTSTANDING
                    && !self.committed.contains_key(&request.kernel_sim)) {
                    self.counters.saturation += 1; Status::Saturated
                } else if !self.authorized(world,origin,&request) { Status::Unauthorized }
                else if deadline_tick <= world.clock.native_tick_cnt { Status::Expired }
                else {
                    match world.plan_navigation(request) {
                        Ok(plan) => {
                            self.pending.insert(id,Pending { origin,deadline:deadline_tick,plan });
                            Status::Staged
                        }
                        Err(e) => Self::input_status(e),
                    }
                }
            }
            Message::Ready { .. } | Message::Result { .. } | Message::CommitOffer | Message::Cancel => {
                self.counters.malformed += 1; return;
            }
        };
        self.result(id,op,status,Vec::new(),Vec::new());
    }
}

fn offer(pinned: &PinnedPlan) -> Result<RustNavigationEnvelope, CodecError> {
    let request = pinned.request();
    let plan = pinned.plan();
    if plan.legs.len().checked_add(1).is_none_or(|n| n > (MAX_ENVELOPE - 80) / 53) {
        return Err(CodecError::Oversized);
    }
    let boundary = pinned.snapshot().graph().boundary_legs(plan);
    let mut legs = reserve(boundary.len())?;
    let mut crossing_index = 0;
    for (index,leg) in (0u32..).zip(boundary) {
        let (object,entry_slot,exit_slot) = match leg.kind {
            LegKind::Approach | LegKind::Through | LegKind::Exit => {
                let crossing = plan.door_crossings.get(crossing_index).ok_or(CodecError::Invalid)?;
                let binding = pinned.snapshot().bindings().portals.iter()
                    .find(|b| b.portal_id == crossing.portal_id && b.object_id == crossing.door_guid)
                    .ok_or(CodecError::Invalid)?;
                // SIMNAV's canonical portal requires cell_a < cell_b (K loader).
                let (entry,exit) = if crossing.from_cell < crossing.to_cell {
                    (binding.entry_slot,binding.exit_slot)
                } else { (binding.exit_slot,binding.entry_slot) };
                if leg.kind == LegKind::Exit { crossing_index += 1; }
                (binding.object_id,entry,exit)
            }
            LegKind::SubPath | LegKind::SeatHandoff => (0,0,0),
        };
        legs.push(ExecutionLeg { index,cell:leg.cell_id,kind:leg.kind.hash_byte(),path_type:leg.path_type,
            from_cm:leg.from_cm,to_cm:leg.to_cm,object,entry_slot,exit_slot });
    }
    Ok(RustNavigationEnvelope { epoch:request.epoch,generation:request.generation,corr:request.corr,
        actor:request.actor_sim_id,start_mm:request.start_xyz_mm,goal_mm:request.goal_xyz_mm,
        hash:NavGraph::plan_hash(plan),total_cost_mm:plan.total_cost_mm,legs })
}
