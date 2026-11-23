#![forbid(unsafe_code)]
//! Public-to-kernel/typed-bridge translation. No public line is a mod command.

use std::collections::{BTreeSet, HashMap, VecDeque};
use std::sync::Arc;
use std::time::{Duration, Instant};
use sim96_kernel_cleanroom::runtime_bridge::AuthorityWorld;
use sim96_kernel_cleanroom::private_navigation::{Frame as NavigationFrame, Message as NavigationMessage,
    RustNavigationEnvelope, Status as NavigationStatus, MAX_RECORD as MAX_PRIVATE_RECORD};
use sim96_kernel_cleanroom::wan_transport::RoleTag;
use sim96_kernel_cleanroom::wire::PlayerId;
use simlink_bridge::text::Guid;
use simlink_bridge::verbs::{BridgeCommand, GameSpeed, InteractionRequest, Vec3};
use simlink_bridge::{BridgeDriver, ProcessAttach, SleepPacer};
use crate::idmap::IdMap;
use crate::messages::{self as m, Ack, Command, ErrFrame, RefusalCode};
use crate::transport::{DriverError, GameDriver, StreamReply, StreamRequest};
use crate::wire_v1 as w;

// Match the kernel outbox: two deliveries per outstanding request plus two controls.
// A full legal kernel batch fits; stalled announcements cannot accumulate further batches.
const MAX_HELD_PRIVATE_DELIVERIES: usize =
    sim96_kernel_cleanroom::private_navigation::MAX_OUTSTANDING * 2 + 2;

/// One class per registered ClientCommand; checked against C# at run time.
pub const PUBLIC_VERBS: [(&str, char); 18] = [
    ("JoinSession", 'F'), ("LeaveSession", 'F'), ("ClaimSim", 'K'),
    ("ReleaseSim", 'F'), ("SetActiveSim", 'B'), ("QueueInteraction", 'K'),
    ("CancelInteraction", 'B'), ("LeaveConversation", 'B'), ("AnswerDialog", 'K'),
    ("RequestSimDescription", 'B'), ("RequestWorld", 'K'), ("SetGameSpeed", 'B'),
    ("SetWorldTime", 'B'), ("RequestSnapshot", 'B'), ("SetClientRole", 'K'),
    ("SaveGame", 'B'), ("AckSnapshot", 'F'), ("SubmitLotExport", 'R'),
];

/// The typed bridge seam allows authority tests without opening any process.
pub trait BridgeEndpoint {
    fn begin_stream_command(&mut self, command: &BridgeCommand) -> Result<Option<Vec<String>>, DriverError> {
        self.command(command).map(Some)
    }
    fn poll_stream_command(&mut self) -> Result<Option<Vec<String>>, DriverError> { Ok(None) }
    /// Checkpoint IO is split from the actor's pump. Test endpoints may finish
    /// immediately; live memory IO sends once and completes on a matching reply.
    fn begin_checkpoint_command(&mut self, command: &BridgeCommand, _timeout: Duration) -> Result<Option<Vec<String>>, DriverError> {
        self.command(command).map(Some)
    }
    fn poll_checkpoint_command(&mut self) -> Result<Option<Vec<String>>, DriverError> { Ok(None) }
    fn connected(&self) -> bool { true }
    fn dropped_replies(&self) -> u64 { 0 }
    fn command(&mut self, command: &BridgeCommand) -> Result<Vec<String>, DriverError>;
    fn events(&mut self) -> Result<Vec<Vec<u8>>, DriverError>;
    fn command_with_timeout(&mut self, command: &BridgeCommand, _timeout: Duration) -> Result<Vec<String>, DriverError> {
        self.command(command)
    }
    fn take_snapshot(&mut self) -> Option<Vec<u8>> { None }
    fn take_description(&mut self) -> Option<simlink_bridge::verbs::SimDescriptionPayload> { None }
    fn upload(&mut self, _corr: Guid, _blob: &str, _bytes: &[u8]) -> Result<Vec<String>, DriverError> {
        Err(DriverError("bulk upload unavailable".into()))
    }
}

/// Explicit offline endpoint; the real kernel still adjudicates class K.
pub struct OfflineBridge;
impl BridgeEndpoint for OfflineBridge {
    fn connected(&self) -> bool { false }
    fn command(&mut self, _command: &BridgeCommand) -> Result<Vec<String>, DriverError> {
        Ok(Vec::new())
    }
    fn events(&mut self) -> Result<Vec<Vec<u8>>, DriverError> {
        Ok(Vec::new())
    }
}

struct CheckpointReply {
    corr: Guid,
    deadline: Instant,
    result: Option<Result<String, DriverError>>,
}

struct PendingStreamReply {
    corr: Guid,
    deadline: Instant,
    result: Option<Result<Vec<String>, DriverError>>,
    snapshot: Option<Vec<u8>>,
    parts: Option<(usize, Vec<Option<Vec<u8>>>)>,
    chunks: Option<Vec<Option<String>>>,
}

pub struct LiveBridge<M: simlink_bridge::memory::GuestMemory = ProcessAttach, P: simlink_bridge::driver::Pacer = SleepPacer> {
    driver: BridgeDriver<M, P>,
    pushed: VecDeque<Vec<u8>>,
    snapshot: Option<Vec<u8>>,
    description: Option<simlink_bridge::verbs::SimDescriptionPayload>,
    reply_drops: u64,
    checkpoint_reply: Option<CheckpointReply>,
    stream_reply: Option<PendingStreamReply>,
}

impl<M: simlink_bridge::memory::GuestMemory, P: simlink_bridge::driver::Pacer> LiveBridge<M, P> {
    pub fn from_driver(driver: BridgeDriver<M, P>) -> Self {
        Self { driver, pushed: VecDeque::new(), snapshot: None, description: None, reply_drops: 0,
            checkpoint_reply: None, stream_reply: None }
    }
    fn drop_reply(&mut self, line: &str, reason: &str) {
        self.reply_drops += 1;
        let fields: Vec<_> = line.split('|').collect();
        eprintln!("serve_report|bridge_reply_dropped|reason={reason}|kind={}|corr={}|total={}",
            fields[0], fields.get(1).copied().unwrap_or(""), self.reply_drops);
        if reason == "unmatched" {
            if let Some(bulk) = simlink_bridge::reply::parse_bulk_ref(line) {
                let index = bulk.part_index.unwrap_or(0);
                if index >= 0 && bulk.total_length > 0 && bulk.slot >= 0 {
                    let offset = index as usize * simlink_bridge::layout::BULK_SLOT_PAYLOAD_MAX;
                    if let Some(remaining) = (bulk.total_length as usize).checked_sub(offset) {
                        let length = remaining.min(simlink_bridge::layout::BULK_SLOT_PAYLOAD_MAX);
                        // Consume an obsolete payload only if its correlation/length/CRC
                        // still match. A reused slot must remain available to its new owner.
                        let _ = self.driver.read_bulk_for(bulk.corr, bulk.slot as usize, length, bulk.crc32);
                    }
                }
            }
        }
    }
    fn unsolicited(&mut self, line: String) {
        if self.stream_reply.as_ref().is_some_and(|pending|
            line.split('|').nth(1).and_then(|id| Guid::parse(id).ok()) == Some(pending.corr)) {
            if self.stream_reply.as_ref().is_some_and(|pending| pending.result.is_some()) {
                self.drop_reply(&line, "duplicate_stream_reply");
                return;
            }
            self.stream_line(line);
            return;
        }
        if let Some(pending) = &mut self.checkpoint_reply {
            if line.split('|').nth(1).and_then(|id| Guid::parse(id).ok()) == Some(pending.corr) {
                pending.result = Some(Ok(line));
                return;
            }
        }
        if line.starts_with("EVENT|") { self.pushed.push_back(line.into_bytes()); }
        else { self.drop_reply(&line, "unmatched"); }
    }
    fn poll_reply(&mut self) -> Result<Option<String>, DriverError> {
        self.driver.poll_line().map_err(|error| {
            if let simlink_bridge::error::BridgeError::CommandWriteFailed { correlation: Some(id), .. } = &error {
                if let Some(pending) = &mut self.checkpoint_reply {
                    if Guid::parse_wire(id) == Some(pending.corr) && pending.result.is_none() {
                        pending.result = Some(Err(driver_error(&error)));
                    }
                }
                if let Some(pending) = &mut self.stream_reply {
                    if Guid::parse_wire(id) == Some(pending.corr) && pending.result.is_none() {
                        pending.result = Some(Err(driver_error(&error)));
                    }
                }
            }
            driver_error(error)
        })
    }
    fn stream_line(&mut self, line: String) {
        let mut pending = self.stream_reply.take().expect("matched stream correlation");
        let outcome = (|| -> Result<Option<Vec<String>>, DriverError> {
            let fields: Vec<_> = line.split('|').collect();
            if fields[0] == "BULK_REF" {
                let slot: usize = fields.get(3).ok_or_else(|| driver_error("bad bulk slot"))?
                    .parse().map_err(driver_error)?;
                let crc = u32::from_str_radix(fields.last().ok_or_else(|| driver_error("bad bulk crc"))?, 16)
                    .map_err(driver_error)?;
                if fields.get(2) == Some(&"snapshot-part") {
                    let index: usize = fields.get(4).ok_or_else(|| driver_error("missing part index"))?.parse().map_err(driver_error)?;
                    let count: usize = fields.get(5).ok_or_else(|| driver_error("missing part count"))?.parse().map_err(driver_error)?;
                    let total: usize = fields.get(6).ok_or_else(|| driver_error("missing snapshot length"))?.parse().map_err(driver_error)?;
                    let max = simlink_bridge::layout::BULK_SLOT_PAYLOAD_MAX;
                    if count == 0 || count > simlink_bridge::layout::BULK_SLOT_COUNT || index >= count
                        || total == 0 || total.div_ceil(max) != count {
                        self.drop_reply(&line, "invalid_part");
                        return Ok(None);
                    }
                    let (expected, parts) = pending.parts.get_or_insert_with(|| (total, vec![None; count]));
                    if *expected != total || parts.len() != count || parts[index].is_some() {
                        self.drop_reply(&line, "duplicate_or_inconsistent_part");
                        return Ok(None);
                    }
                    parts[index] = Some(self.driver.read_bulk_for(pending.corr, slot,
                        (total - index * max).min(max), crc).map_err(driver_error)?);
                    if parts.iter().any(Option::is_none) { return Ok(None); }
                    let bytes: Vec<_> = parts.iter_mut().flat_map(|part| part.take().expect("complete bulk")).collect();
                    if bytes.len() != total { return Err(driver_error("snapshot parts length mismatch")); }
                    pending.snapshot = Some(bytes);
                } else {
                    match self.driver.read_bulk_ref(slot, crc).map_err(driver_error)? {
                        simlink_bridge::driver::BulkPayload::Snapshot2 { bytes, .. } => pending.snapshot = Some(bytes),
                        simlink_bridge::driver::BulkPayload::Text(text) => return Ok(Some(vec![text])),
                    }
                }
                return Ok(Some(vec![format!("SNAPSHOT2|{}", pending.corr.to_n())]));
            }
            if fields[0] == "SNAPSHOT_CHUNK" {
                let index: usize = fields.get(2).ok_or_else(|| driver_error("missing chunk index"))?.parse().map_err(driver_error)?;
                let count: usize = fields.get(3).ok_or_else(|| driver_error("missing chunk count"))?.parse().map_err(driver_error)?;
                if count == 0 || count > 4096 || index >= count {
                    self.drop_reply(&line, "invalid_chunk");
                    return Ok(None);
                }
                let parts = pending.chunks.get_or_insert_with(|| vec![None; count]);
                if parts.len() != count || parts[index].is_some() {
                    self.drop_reply(&line, "duplicate_or_inconsistent_chunk");
                    return Ok(None);
                }
                parts[index] = Some(line.splitn(5, '|').nth(4).ok_or_else(|| driver_error("missing chunk body"))?.into());
                if parts.iter().any(Option::is_none) { return Ok(None); }
                return Ok(Some(vec![parts.iter_mut().map(|part| part.take().expect("complete chunks")).collect()]));
            }
            Ok(Some(vec![line.clone()]))
        })();
        match outcome {
            Ok(Some(reply)) => pending.result = Some(Ok(reply)),
            Err(error) => pending.result = Some(Err(error)),
            Ok(None) => {}
        }
        self.stream_reply = Some(pending);
    }
}
impl<M: simlink_bridge::memory::GuestMemory, P: simlink_bridge::driver::Pacer> BridgeEndpoint for LiveBridge<M, P> {
    fn begin_stream_command(&mut self, command: &BridgeCommand) -> Result<Option<Vec<String>>, DriverError> {
        if self.stream_reply.is_some() { return Err(driver_error("stream command already pending")); }
        let encoded = simlink_bridge::verbs::encode(command).map_err(driver_error)?;
        let corr = encoded.split('|').nth(1).and_then(|id| Guid::parse(id).ok())
            .ok_or_else(|| driver_error("stream correlation missing"))?;
        self.driver.send_command(command).map_err(driver_error)?;
        self.stream_reply = Some(PendingStreamReply { corr, deadline: Instant::now() + Duration::from_secs(30),
            result: None, snapshot: None, parts: None, chunks: None });
        Ok(None)
    }
    fn poll_stream_command(&mut self) -> Result<Option<Vec<String>>, DriverError> {
        // A zero-duration pacing step lets deterministic memory fixtures service
        // their producer; the live pacer never waits for a bridge round trip.
        simlink_bridge::driver::Pacer::pause(self.driver.pacer_mut(), 0);
        let failure = loop {
            match self.poll_reply() {
                Ok(Some(line)) => self.unsolicited(line),
                Ok(None) => break None,
                Err(error) => break Some(error),
            }
        };
        if let Some(pending) = &mut self.stream_reply {
            if let Some(result) = pending.result.take() {
                let pending = self.stream_reply.take().expect("completed stream request");
                if let Some(snapshot) = pending.snapshot { self.snapshot = Some(snapshot); }
                return result.map(Some);
            }
            if Instant::now() >= pending.deadline {
                self.stream_reply = None;
                return Err(driver_error("stream bridge reply timeout"));
            }
        }
        if let Some(error) = failure {
            self.stream_reply = None;
            return Err(error);
        }
        Ok(None)
    }
    fn begin_checkpoint_command(&mut self, command: &BridgeCommand, timeout: Duration) -> Result<Option<Vec<String>>, DriverError> {
        if self.checkpoint_reply.is_some() { return Err(driver_error("checkpoint command already pending")); }
        let encoded = simlink_bridge::verbs::encode(command).map_err(driver_error)?;
        let corr = encoded.split('|').nth(1).and_then(|id| Guid::parse(id).ok())
            .ok_or_else(|| driver_error("checkpoint correlation missing"))?;
        self.driver.send_command(command).map_err(driver_error)?;
        self.checkpoint_reply = Some(CheckpointReply { corr, deadline: Instant::now() + timeout, result: None });
        Ok(None)
    }
    fn poll_checkpoint_command(&mut self) -> Result<Option<Vec<String>>, DriverError> {
        let mut failure = loop {
            match self.poll_reply() {
                Ok(Some(line)) => self.unsolicited(line),
                Ok(None) => break None,
                Err(error) => break Some(error),
            }
        };
        if let Some(pending) = &mut self.checkpoint_reply {
            if let Some(result) = pending.result.take() {
                self.checkpoint_reply = None;
                return result.map(|line| Some(vec![line]));
            }
            if let Some(error) = failure.take() { return Err(error); }
            if Instant::now() >= pending.deadline {
                self.checkpoint_reply = None;
                return Err(driver_error("SAVE_GAME checkpoint bridge reply timeout"));
            }
        }
        if let Some(error) = failure { return Err(error); }
        Ok(None)
    }
    fn dropped_replies(&self) -> u64 { self.reply_drops }
    fn command(&mut self, command: &BridgeCommand) -> Result<Vec<String>, DriverError> {
        self.command_with_timeout(command, Duration::from_secs(30))
    }
    fn command_with_timeout(&mut self, command: &BridgeCommand, timeout: Duration) -> Result<Vec<String>, DriverError> {
        let encoded = simlink_bridge::verbs::encode(command).map_err(driver_error)?;
        let corr = encoded.split('|').nth(1).ok_or_else(|| DriverError("missing bridge corr".into()))?;
        let corr_id = Guid::parse(corr).map_err(driver_error)?;
        self.driver.send_command(command).map_err(driver_error)?;
        let deadline = Instant::now() + timeout;
        let mut bulk_parts: Option<(usize, Vec<Option<Vec<u8>>>)> = None;
        let mut chunks: Option<Vec<Option<String>>> = None;
        loop {
            if let Some(line) = self.poll_reply()? {
                if line.split('|').nth(1).and_then(|id| Guid::parse(id).ok()) == Some(corr_id) {
                    let fields: Vec<_> = line.split('|').collect();
                    if fields[0] == "BULK_REF" {
                        let slot: usize = fields.get(3).ok_or_else(|| driver_error("bad bulk slot"))?.parse().map_err(driver_error)?;
                        let crc = u32::from_str_radix(fields.last().ok_or_else(|| driver_error("bad bulk crc"))?, 16).map_err(driver_error)?;
                        if fields.get(2) == Some(&"snapshot-part") {
                            let index: usize = fields.get(4).ok_or_else(|| driver_error("missing part index"))?.parse().map_err(driver_error)?;
                            let count: usize = fields.get(5).ok_or_else(|| driver_error("missing part count"))?.parse().map_err(driver_error)?;
                            let total: usize = fields.get(6).ok_or_else(|| driver_error("missing snapshot length"))?.parse().map_err(driver_error)?;
                            let max = simlink_bridge::layout::BULK_SLOT_PAYLOAD_MAX;
                            if count == 0 || count > simlink_bridge::layout::BULK_SLOT_COUNT || index >= count
                                || total == 0 || total.div_ceil(max) != count {
                                self.drop_reply(&line, "invalid_part");
                                continue;
                            }
                            let (expected_total, parts) = bulk_parts.get_or_insert_with(|| (total, vec![None; count]));
                            if *expected_total != total || parts.len() != count || parts[index].is_some() {
                                self.drop_reply(&line, "duplicate_or_inconsistent_part");
                                continue;
                            }
                            // Continuation pages are arbitrary binary, not UTF-8. Do not
                            // classify each page and corrupt it with lossy text decoding.
                            let length = (total - index * max).min(max);
                            let bytes = self.driver.read_bulk_for(corr_id,
                                slot, length, crc).map_err(driver_error)?;
                            parts[index] = Some(bytes);
                            if parts.iter().all(Option::is_some) {
                                let bytes: Vec<_> = parts.iter_mut().flat_map(|part| part.take().expect("complete bulk")).collect();
                                if bytes.len() != total { return Err(driver_error("snapshot parts length mismatch")); }
                                self.snapshot = Some(bytes);
                                return Ok(vec![format!("SNAPSHOT2|{corr}")]);
                            }
                            continue;
                        }
                        match self.driver.read_bulk_ref(slot, crc).map_err(driver_error)? {
                            simlink_bridge::driver::BulkPayload::Snapshot2 { bytes, .. } => {
                                self.snapshot = Some(bytes);
                                return Ok(vec![format!("SNAPSHOT2|{corr}")]);
                            }
                            simlink_bridge::driver::BulkPayload::Text(text) => return Ok(vec![text]),
                        }
                    }
                    if let BridgeCommand::ExportSimDescription { desc, .. } = command {
                        if let Some(hex) = fields.get(3) {
                            let report = simlink_bridge::text::decode_text(hex).map_err(driver_error)?;
                            if report.contains("result=exported") {
                                let get = |key: &str| -> Result<&str, DriverError> {
                                    report.split(';').find_map(|s| s.trim().strip_prefix(key))
                                        .ok_or_else(|| driver_error(format!("description metadata missing {key}")))
                                };
                                let length: usize = get("bytes=")?.parse().map_err(driver_error)?;
                                let slot = get("slot=")?.parse().map_err(driver_error)?;
                                let crc = u32::from_str_radix(get("crc=")?, 16).map_err(driver_error)?;
                                let bytes = self.driver.read_bulk_for(Guid::parse_wire(corr).ok_or_else(|| driver_error("bad correlation"))?, slot, length, crc).map_err(driver_error)?;
                                let props: usize = get("props=")?.parse().map_err(driver_error)?;
                                let keys: usize = get("keys=")?.parse().map_err(driver_error)?;
                                let objects: usize = get("objects=")?.parse().map_err(driver_error)?;
                                if props.checked_add(keys).and_then(|n| n.checked_add(objects)) != Some(bytes.len()) {
                                    return Err(driver_error("description segment sizes don't match"));
                                }
                                self.description = Some(simlink_bridge::verbs::SimDescriptionPayload {
                                    desc: *desc, property_stream: bytes[..props].to_vec(),
                                    res_key_table: bytes[props..props + keys].to_vec(), object_id_table: bytes[props + keys..].to_vec(),
                                });
                            }
                        }
                    }
                    if fields[0] == "SNAPSHOT_CHUNK" {
                        let index: usize = fields.get(2).ok_or_else(|| driver_error("missing chunk index"))?.parse().map_err(driver_error)?;
                        let count: usize = fields.get(3).ok_or_else(|| driver_error("missing chunk count"))?.parse().map_err(driver_error)?;
                        if count == 0 || count > 4096 || index >= count {
                            self.drop_reply(&line, "invalid_chunk");
                            continue;
                        }
                        let parts = chunks.get_or_insert_with(|| vec![None; count]);
                        if parts.len() != count || parts[index].is_some() {
                            self.drop_reply(&line, "duplicate_or_inconsistent_chunk");
                            continue;
                        }
                        parts[index] = Some(line.splitn(5, '|').nth(4).ok_or_else(|| driver_error("missing chunk body"))?.into());
                        if parts.iter().all(Option::is_some) {
                            return Ok(vec![parts.iter_mut().map(|part| part.take().expect("complete chunks")).collect()]);
                        }
                        continue;
                    }
                    return Ok(vec![line]);
                }
                self.unsolicited(line);
            }
            if Instant::now() >= deadline {
                return Err(DriverError("bridge reply timeout".into()));
            }
            // The unchanged card-56 memory protocol is polled; this wait belongs at that seam.
            simlink_bridge::driver::Pacer::pause(self.driver.pacer_mut(), 1);
        }
    }
    fn events(&mut self) -> Result<Vec<Vec<u8>>, DriverError> {
        while let Some(line) = self.poll_reply()? {
            self.unsolicited(line);
        }
        Ok(self.pushed.drain(..).collect())
    }
    fn take_snapshot(&mut self) -> Option<Vec<u8>> { self.snapshot.take() }
    fn take_description(&mut self) -> Option<simlink_bridge::verbs::SimDescriptionPayload> { self.description.take() }
    fn upload(&mut self, corr: Guid, blob: &str, bytes: &[u8]) -> Result<Vec<String>, DriverError> {
        let (slot, crc) = self.driver.write_ctl_bulk(corr, bytes).map_err(driver_error)?;
        self.command_with_timeout(&BridgeCommand::Raw { corr, name: "BULK_REF".into(),
            args: vec!["probe-upload".into(), slot.to_string(), bytes.len().to_string(), format!("{crc:08X}"), simlink_bridge::text::encode_text(blob)] },
            Duration::from_secs(crate::admin::MAX_TIMEOUT_SECONDS))
    }
}

fn driver_error(error: impl std::fmt::Display) -> DriverError {
    DriverError(error.to_string())
}

enum DispatchError {
    Refusal(RefusalCode),
    Driver(DriverError),
}
impl From<RefusalCode> for DispatchError {
    fn from(code: RefusalCode) -> Self { Self::Refusal(code) }
}
impl From<DriverError> for DispatchError {
    fn from(error: DriverError) -> Self { Self::Driver(error) }
}

pub struct BridgeGameDriver<B = LiveBridge> {
    endpoint: B,
    world: AuthorityWorld,
    ids: IdMap,
    players: HashMap<i32, PlayerId>,
    // The public corr restarts on reconnect; the kernel's per-player corr does not.
    kernel_corr: u64,
    bridge_corr: u64,
    bridge_nonce: [u8; 8],
    request_connection: Option<crate::ConnId>,
    pending_events: Vec<crate::routing::RoutedEvent>,
    pick_requesters: VecDeque<(String, crate::ConnId, u64)>,
    observed_tick: u64,
    event_drops: std::collections::BTreeMap<String, u64>,
    sim_lots: HashMap<u64, Option<u64>>,
    registered_gates: std::collections::HashSet<u64>,
    pub claimed_this_run: BTreeSet<u64>,
    save_name: String,
    checkpoint_save_name: Option<String>,
    save_roots: Option<Vec<std::path::PathBuf>>,
    pub save_faults: crate::checkpoint::FaultInjector,
    save_gate: Arc<crate::checkpoint::SaveGate>,
    reusable_world: Option<(Arc<crate::world_save::WorldSave>, crate::join_reuse::WorldFingerprint)>,
    pub join_reuse_counters: Arc<std::sync::Mutex<crate::join_reuse::JoinReuseCounters>>,
    pub last_good_keeps: Arc<std::sync::atomic::AtomicU64>,
    checkpoint_queue: VecDeque<(crate::ConnId, Command)>,
    checkpoint_job: Option<CheckpointJob>,
    world_transfers: Vec<WorldTransfer>,
    private_record: Option<(u64, usize, Vec<u8>)>,
    stream_request: Option<StreamRequest>,
    join_queue: VecDeque<PendingJoinBaseline>,
    join_request_active: bool,
    join_events: Vec<crate::routing::RoutedEvent>,
    failed_baselines: std::collections::HashSet<crate::ConnId>,
    route_plans: VecDeque<Vec<String>>,
    private_deliveries: VecDeque<sim96_kernel_cleanroom::private_navigation::Delivery>,
    rust_switches: (bool, bool),
    switches_sent: bool,
    /// Card 55 contract 3.1: push the ownership table with `rust-navigation-sync` (off by default).
    nav_sync: bool,
    /// The `PrivateNavigation` attachment `Invalidate` epoch (`u64 >= 1`), not `Server.epoch`.
    nav_epoch: u64,
    nav_epoch_announced: u64,
    nav_epoch_pending: Option<u64>,
    /// A sync with `nav_epoch` is committed on the mod's current attachment.
    nav_synced: bool,
    /// The attachment closed or the bridge reconnected: the next sync carries a bumped epoch.
    nav_epoch_bump: bool,
    /// Sessions or claims changed since the last committed sync.
    nav_dirty: bool,
    /// Public player id -> the connection whose `JoinSession` bound it (the `PrivateNavigation` session).
    player_conns: HashMap<i32, u64>,
}

#[derive(Clone, Copy)]
enum CheckpointStage { Scan, Scope, Save, Saved, Read }
#[derive(Clone)]
struct PendingJoinBaseline {
    conn: crate::ConnId,
    command: Command,
    closed: bool,
}
struct ReadWorld {
    world: Arc<crate::world_save::WorldSave>,
    whole: bool,
}
struct WorldTransfer {
    conn: crate::ConnId,
    corr: u64,
    command_id: u64,
    remaining: usize,
    events: std::sync::mpsc::Receiver<crate::routing::RoutedEvent>,
}
struct CheckpointJob {
    conn: crate::ConnId,
    command: Command,
    save_id: String,
    track_fingerprint: bool,
    armed_fingerprint: bool,
    stage: CheckpointStage,
    retry_at: Option<Instant>,
    started: Instant,
    save_started: Instant,
    fingerprint: Option<crate::join_reuse::WorldFingerprint>,
    reader: Option<std::thread::JoinHandle<Result<ReadWorld, DriverError>>>,
    sent: bool,
    save_completed: bool,
}

impl BridgeGameDriver<LiveBridge> {
    pub fn from_attached(driver: BridgeDriver<ProcessAttach, SleepPacer>) -> Self {
        Self::new(LiveBridge::from_driver(driver))
    }
    /// Only an explicit live CLI mode calls this; offline construction never opens handles.
    pub fn attach_pid(pid: u32) -> Result<Self, DriverError> {
        let (driver, _) = BridgeDriver::attach_pid(pid, Duration::from_secs(30)).map_err(driver_error)?;
        let mut driver = Self::from_attached(driver);
        driver.enable_rust_switches()?;
        Ok(driver)
    }
}

impl<B: BridgeEndpoint> BridgeGameDriver<B> {
    pub fn new(endpoint: B) -> Self {
        use std::hash::{BuildHasher, Hasher};
        let mut nonce = std::collections::hash_map::RandomState::new().build_hasher();
        nonce.write_u32(std::process::id());
        let bridge_nonce = nonce.finish().max(1).to_be_bytes();
        let mut world = AuthorityWorld::new();
        world.attach_routing_census();
        Self {
            // LiveNavigation forbids zero; 1 is the reserved server primary.
            // Claims and navigation share this one existing IdMap bijection.
            endpoint, world, ids: IdMap::starting_at(2),
            players: HashMap::new(), kernel_corr: 0, bridge_corr: 0,
            bridge_nonce,
            request_connection: None, pending_events: Vec::new(), pick_requesters: VecDeque::new(),
            observed_tick: 0,
            event_drops: std::collections::BTreeMap::new(),
            sim_lots: HashMap::new(),
            registered_gates: std::collections::HashSet::new(),
            claimed_this_run: BTreeSet::new(),
            save_name: crate::checkpoint::DEFAULT_SAVE_NAME.into(),
            checkpoint_save_name: std::env::var("SIMSONLINE_CHECKPOINT_SAVE").ok(),
            save_roots: None,
            save_faults: crate::checkpoint::FaultInjector::default(),
            save_gate: Arc::new(crate::checkpoint::SaveGate::new()),
            reusable_world: None,
            join_reuse_counters: Arc::new(std::sync::Mutex::new(crate::join_reuse::JoinReuseCounters::new())),
            last_good_keeps: Arc::new(std::sync::atomic::AtomicU64::new(0)),
            checkpoint_queue: VecDeque::new(),
            checkpoint_job: None,
            world_transfers: Vec::new(),
            private_record: None,
            stream_request: None,
            join_queue: VecDeque::new(),
            join_request_active: false,
            join_events: Vec::new(),
            failed_baselines: std::collections::HashSet::new(),
            route_plans: VecDeque::new(),
            private_deliveries: VecDeque::new(),
            rust_switches: (
                std::env::var("SIMSONLINE_RUST_ROUTE").as_deref() == Ok("1"),
                std::env::var("SIMSONLINE_RUST_ROAD_PLACE").as_deref() == Ok("1"),
            ),
            switches_sent: false,
            nav_sync: std::env::var("SIMSONLINE_RUST_NAV_SYNC").as_deref() == Ok("1"),
            nav_epoch: 1,
            nav_epoch_announced: 0,
            nav_epoch_pending: None,
            nav_synced: false,
            nav_epoch_bump: false,
            nav_dirty: false,
            player_conns: HashMap::new(),
        }
    }
    pub fn endpoint(&self) -> &B { &self.endpoint }

    /// Offline hosts and tests pick the Rust switches explicitly instead of through the environment.
    /// Call before the first command or pump: the switches go out once per attachment.
    pub fn configure_rust_switches(&mut self, route: bool, road_place: bool, navigation_sync: bool) {
        self.rust_switches = (route, road_place);
        self.nav_sync = navigation_sync;
    }

    fn enable_rust_switches(&mut self) -> Result<(), DriverError> {
        self.send_rust_switches()?;
        self.sync_navigation()
    }

    /// The private attachment closed or the bridge dropped: the kernel's epoch-scoped `high_corr`
    /// fence only resets on a higher `Invalidate` epoch, so the next sync carries a bumped one
    /// (Card 55 contract 3.1, Forced).
    fn nav_detached(&mut self) {
        self.nav_synced = false;
        self.nav_epoch_bump = true;
        if self.nav_sync { self.nav_epoch_pending = None; }
    }

    /// Every driver-initiated attachment close invalidates the sync at this boundary.
    fn close_private_navigation(&mut self, eof: bool) {
        self.world.private_close(eof);
        self.nav_detached();
    }

    /// Sessions or claims changed: push the table now. A failed push stays dirty and retries on
    /// the next pump instead of failing the command that already succeeded.
    fn nav_changed(&mut self) {
        self.nav_dirty = true;
        if let Err(error) = self.sync_navigation() {
            eprintln!("serve_report|navigation_sync_failed|{error}");
        }
    }

    fn navigation_entries(&self) -> Vec<crate::nav_sync::Claimed> {
        self.world.authority.owners.iter().filter_map(|(sim, owner)| {
            let player_id = self.players.iter().find_map(|(public, player)| (player == owner).then_some(*public))?;
            let conn_id = *self.player_conns.get(&player_id)?;
            let wire_actor = self.ids.kernel_to_wire(*sim)?;
            (conn_id != 0 && sim.0 >= 2).then_some(crate::nav_sync::Claimed { conn_id, player_id, wire_actor, kernel_sim: sim.0 })
        }).collect()
    }

    /// `rust-navigation-sync` (Card 55 contract 3.1): the whole ownership table, paged, after
    /// `rust-route-on` and before the mod's first `Invalidate`. Opt-in; nothing is sent when off.
    fn sync_navigation(&mut self) -> Result<(), DriverError> {
        if !self.rust_switches.0 || !self.nav_sync { return Ok(()); }
        if !self.endpoint.connected() {
            if self.nav_synced { self.nav_detached(); }
            return Ok(());
        }
        if !self.switches_sent || (self.nav_synced && !self.nav_dirty) { return Ok(()); }
        if !self.nav_synced && self.nav_epoch_bump {
            self.nav_epoch = self.nav_epoch.saturating_add(1);
            // Plan correlations can restart in the new epoch; retained frames carry no epoch.
            self.route_plans.clear();
            let dropped = self.private_deliveries.len() + self.world.private_navigation.take_deliveries().len();
            self.world.private_navigation.counters.delivery_failed += dropped as u64;
            self.private_deliveries.clear();
            self.nav_epoch_bump = false;
        }
        let entries = self.navigation_entries();
        let pages = crate::nav_sync::pages(&entries);
        // Contract 3.1 ruling 5: index 0 resets staging, so uncertain delivery
        // always restarts at zero. `active` reports the receiver's committed
        // table, including a final commit whose acknowledgment was lost or a
        // receiver reset. Learn it from page zero; only the final page replaces it.
        let mut committed = None;
        for (index, page) in pages.iter().enumerate() {
            let corr = self.next_bridge_id()?;
            let args = crate::nav_sync::command_args(self.nav_epoch, index, pages.len(), page);
            let replies = self.endpoint.command(&BridgeCommand::SacsRecorder { corr, args })?;
            let prefix = format!("AUTONOMY_PROBE|{}|ok|",corr.to_n());
            let reported = replies.iter().filter_map(|reply| reply.strip_prefix(&prefix))
                .filter_map(|hex| simlink_bridge::text::decode_text(hex).ok())
                .find_map(|body| {
                    let (_,active) = body.rsplit_once(";active=")?;
                    let active = active.parse::<usize>().ok()?;
                    (body == crate::nav_sync::accepted_body(self.nav_epoch,index,pages.len(),page,active))
                        .then_some(active)
                }).ok_or_else(|| driver_error("private bridge delivery refused"))?;
            let active = if index + 1 == pages.len() { entries.len() }
                else { *committed.get_or_insert(reported) };
            let accepted = crate::nav_sync::accepted_body(self.nav_epoch, index, pages.len(), page, active);
            Self::private_reply_ok(&replies, "AUTONOMY_PROBE", corr, Some(&accepted))?;
        }
        self.nav_synced = true;
        self.nav_dirty = false;
        if self.nav_epoch != self.nav_epoch_announced {
            self.nav_epoch_pending = Some(self.nav_epoch);
        }
        Ok(())
    }

    fn send_rust_switches(&mut self) -> Result<(), DriverError> {
        if self.switches_sent || !self.endpoint.connected() { return Ok(()); }
        for (enabled, name) in [(self.rust_switches.0, "rust-route-on"), (self.rust_switches.1, "rust-road-place-on")] {
            if enabled {
                let corr = self.next_bridge_id()?;
                let replies = self.endpoint.command(&BridgeCommand::SacsRecorder { corr, args: vec![name.into()] })?;
                Self::private_reply_ok(&replies, "AUTONOMY_PROBE", corr, None)?;
                if name == "rust-route-on" && !self.nav_sync && self.nav_epoch_announced == 0 {
                    self.nav_epoch_pending = Some(self.nav_epoch);
                }
            }
        }
        self.switches_sent = true;
        Ok(())
    }

    fn private_chunk(&mut self, line: &str) -> Result<Option<NavigationFrame>, DriverError> {
        let bad = || driver_error("invalid private navigation chunk");
        if line.len() > simlink_bridge::layout::MAX_LINE_BYTES { return Err(bad()); }
        let fields: Vec<_> = line.split('|').collect();
        if fields.len() != 6 || fields[1] != "PrivateNavigation" { return Err(bad()); }
        let id: u64 = fields[2].parse().map_err(|_| bad())?;
        let offset: usize = fields[3].parse().map_err(|_| bad())?;
        let total: usize = fields[4].parse().map_err(|_| bad())?;
        if id == 0 || !(16..=MAX_PRIVATE_RECORD).contains(&total) || fields[5].is_empty()
            || fields[5].len() % 2 != 0 { return Err(bad()); }
        let size = fields[5].len() / 2;
        if size > 64 * 1024 || offset.checked_add(size).is_none_or(|n| n > total) { return Err(bad()); }
        if offset == 0 {
            if self.private_record.is_some() { return Err(bad()); }
            self.private_record = Some((id,total,Vec::new()));
        }
        let (expected_id,expected_total,bytes) = self.private_record.as_mut().ok_or_else(bad)?;
        if *expected_id != id || *expected_total != total || bytes.len() != offset { return Err(bad()); }
        bytes.try_reserve(size).map_err(|_| bad())?;
        for pair in fields[5].as_bytes().as_chunks::<2>().0 {
            let text = std::str::from_utf8(pair).map_err(|_| bad())?;
            bytes.push(u8::from_str_radix(text,16).map_err(|_| bad())?);
        }
        if bytes.len() == total {
            let (_,_,bytes) = self.private_record.take().ok_or_else(bad)?;
            let frame = NavigationFrame::decode(&bytes).map_err(|_| bad())?;
            if frame.id != id { return Err(bad()); }
            return Ok(Some(frame));
        }
        Ok(None)
    }

    /// Trusted server-mod events call this on the existing actor. It is not a
    /// public command, admin loader or second AuthorityWorld.
    fn private_navigation_frame(&mut self, frame: NavigationFrame) -> Result<(), DriverError> {
        if !self.endpoint.connected() { return Err(driver_error("private attachment disconnected")); }
        if !self.world.private_navigation.enabled {
            if !matches!(frame.message,NavigationMessage::Invalidate { epoch:1.., .. }) {
                return Err(driver_error("private attachment requires epoch fence"));
            }
            self.world.enable_private_navigation();
        }
        if let NavigationMessage::Install { bindings, .. } = &frame.message {
            for (actor,_) in &bindings.actors {
                if *actor == 0 { continue; } // K returns Invalid for the forbidden zero actor.
                let canonical = self.ids.map_wire(*actor).map_err(|e| driver_error(e.as_str()))?;
                if !self.world.private_navigation.anchor_actor(*actor,canonical) {
                    return Err(driver_error("private actor mapping conflicts with claim IdMap"));
                }
            }
        }
        self.world.clock.native_tick_cnt = self.observed_tick;
        self.world.private_frame(frame);
        // The kernel can also close internally on a failed delivery queue.
        if !self.world.private_navigation.enabled { self.nav_detached(); }
        self.flush_private_navigation()
    }

    fn private_reply_ok(replies: &[String], kind: &str, corr: Guid, body: Option<&str>) -> Result<(), DriverError> {
        let prefix = format!("{kind}|{}|ok|",corr.to_n());
        if replies.iter().any(|reply| match body {
            Some(body) => reply == &format!("{prefix}{}",m::hex_encode(body).to_ascii_uppercase()),
            None => reply.starts_with(&prefix),
        }) { Ok(()) }
        else { Err(driver_error("private bridge delivery refused")) }
    }

    fn flush_private_navigation(&mut self) -> Result<(), DriverError> {
        let incoming = self.world.private_navigation.take_deliveries();
        let queued = self.private_deliveries.len() + incoming.len();
        if queued > MAX_HELD_PRIVATE_DELIVERIES {
            self.world.private_navigation.counters.delivery_failed += queued as u64;
            self.private_deliveries.clear();
            self.close_private_navigation(false);
            return Err(driver_error("private delivery queue full"));
        }
        self.private_deliveries.extend(incoming);
        let mut deliveries = std::mem::take(&mut self.private_deliveries).into_iter();
        while let Some(delivery) = deliveries.next() {
            // Keep the complete atomic delivery queued until its epoch can be attributed.
            // An obsolete envelope is still a delivery failure, even before the barrier.
            if !delivery.presenters.is_empty() {
                let epoch = match &delivery.frame.message {
                    NavigationMessage::Result { data, .. } => RustNavigationEnvelope::decode(data).map_err(driver_error)?.epoch,
                    _ => return Err(driver_error("private presenter pairing missing")),
                };
                if (!self.nav_sync || epoch == self.nav_epoch) && self.nav_epoch != self.nav_epoch_announced {
                    self.private_deliveries.push_back(delivery);
                    self.private_deliveries.extend(deliveries);
                    return Ok(());
                }
            }
            let sent = (|| {
                if let NavigationMessage::Result { operation:5,status:NavigationStatus::Done,data, .. } = &delivery.frame.message {
                    let envelope = RustNavigationEnvelope::decode(data).map_err(driver_error)?;
                    if self.nav_sync && envelope.epoch != self.nav_epoch {
                        return Err(driver_error("presenter epoch mismatch"));
                    }
                    let blob = format!("nav-{:016x}-{:016x}-{:016x}-{:016x}-{:016x}",
                        envelope.epoch,envelope.generation,envelope.corr,envelope.actor,delivery.frame.id);
                    // Existing DIAG_BLOB reassembly: 1900 binary bytes plus
                    // hex/metadata fit both the 4080-byte and 64 KiB limits.
                    let count = data.len().div_ceil(1900);
                    for (index,chunk) in data.chunks(1900).enumerate() {
                        let corr = self.next_bridge_id()?;
                        let hex: String = chunk.iter().map(|b| format!("{b:02X}")).collect();
                        let command = BridgeCommand::Raw { corr,name:"DIAG_BLOB".into(),
                            args:vec![blob.clone(),index.to_string(),count.to_string(),hex] };
                        if simlink_bridge::verbs::encode(&command).map_err(driver_error)?.len()
                            > simlink_bridge::layout::MAX_LINE_BYTES {
                            return Err(driver_error("private blob chunk exceeds carrier"));
                        }
                        Self::private_reply_ok(&self.endpoint.command(&command)?,"DIAG_BLOB_REPORT",corr,None)?;
                    }
                    let corr = self.next_bridge_id()?;
                    let accepted = format!("rust-navigation-offer=accepted;blob={blob};request={};epoch={};generation={};corr={};actor={}",
                        delivery.frame.id,envelope.epoch,envelope.generation,envelope.corr,envelope.actor);
                    let replies = self.endpoint.command(&BridgeCommand::SacsRecorder { corr,args:vec![
                        "rust-navigation-offer".into(),blob,delivery.frame.id.to_string(),
                        envelope.epoch.to_string(),envelope.generation.to_string(),envelope.corr.to_string(),
                        envelope.actor.to_string()] })?;
                    Self::private_reply_ok(&replies,"AUTONOMY_PROBE",corr,Some(&accepted))?;
                } else {
                    let corr = self.next_bridge_id()?;
                    let bytes = delivery.frame.encode().map_err(driver_error)?;
                    let hex: String = bytes.iter().map(|b| format!("{b:02X}")).collect();
                    let accepted = format!("rust-navigation-result=accepted;operation={};request={};frame={hex}",
                        delivery.frame.message.operation(),delivery.frame.id);
                    let command = BridgeCommand::SacsRecorder { corr,args:vec!["rust-navigation-result".into(),hex] };
                    let replies = self.endpoint.command(&command)?;
                    Self::private_reply_ok(&replies,"AUTONOMY_PROBE",corr,Some(&accepted))?;
                }
                if !delivery.presenters.is_empty() {
                    let corr = match &delivery.frame.message {
                        NavigationMessage::Result { data, .. } =>
                            RustNavigationEnvelope::decode(data).map_err(driver_error)?.corr,
                        _ => return Err(driver_error("private presenter pairing missing")),
                    };
                    if self.route_plans.len() == 16 { self.route_plans.pop_front(); }
                    self.route_plans.push_back(delivery.presenters.clone());
                    for batch in delivery.presenters.chunks(crate::presentation::MAX_LINES) {
                        self.emit(corr,"SimAnimation",vec![m::enc_list(
                            &batch.iter().map(|line| m::hex_encode(&format!("0|{line}"))).collect::<Vec<_>>())],None);
                    }
                }
                Ok(())
            })();
            if let Err(error) = sent {
                self.world.private_navigation.counters.delivery_failed +=
                    1 + u64::try_from(deliveries.len()).unwrap_or(u64::MAX - 1);
                self.close_private_navigation(false);
                return Err(error);
            }
            self.world.private_delivery_succeeded(&delivery.frame);
        }
        Ok(())
    }
    pub fn configure_saves(&mut self, save: String, checkpoint: Option<String>) {
        self.save_name = save;
        self.checkpoint_save_name = checkpoint;
        if self.save_roots.is_none() { self.save_roots = Some(crate::checkpoint::save_roots()); }
        self.check_boot_save(&self.save_name.clone());
    }
    pub fn set_save_roots(&mut self, roots: Vec<std::path::PathBuf>) { self.save_roots = Some(roots); }

    fn player(&mut self, public: i32) -> Result<PlayerId, RefusalCode> {
        if let Some(player) = self.players.get(&public) { return Ok(*player); }
        let next = u32::try_from(self.players.len()).ok().and_then(|n| n.checked_add(1))
            .ok_or(RefusalCode::IdSpaceFull)?;
        let player = PlayerId(next);
        self.players.insert(public, player);
        Ok(player)
    }

    fn kernel_args(&mut self, cmd: &Command) -> Result<Option<(String, Vec<String>)>, RefusalCode> {
        let bad = RefusalCode::BadLine;
        let result = match cmd.verb.as_str() {
            "ClaimSim" if cmd.args.len() == 1 => {
                let id = self.ids.map_wire(parse(&cmd.args[0])?)?.0.to_string();
                ("ClaimSim", vec![id.clone(), id])
            }
            "QueueInteraction" if cmd.args.len() == 1 => {
                let request = interaction(&cmd.args[0])?;
                let actor = self.ids.map_wire(request.actor)?.0.to_string();
                let target = request.target.map(|t| self.ids.map_wire(t).map(|id| id.0.to_string()))
                    .transpose()?.unwrap_or_else(|| "0".into());
                ("EnqueueInteraction", vec![actor, request.key, "server".into(), target, "100".into()])
            }
            "AnswerDialog" if cmd.args.len() == 3 => {
                let request: i32 = parse(&cmd.args[0])?;
                let _: u64 = parse(&cmd.args[1])?;
                let answer = optional_text(&cmd.args[2])?;
                let choice = answer.and_then(|s| s.parse::<u32>().ok()).unwrap_or(0);
                ("DialogAnswer", vec![request.to_string(), choice.to_string()])
            }
            "SetClientRole" if cmd.args.len() == 1 => ("SetClientRole", vec![match cmd.args[0].as_str() {
                "Server" => "player".into(), "Client" => "presenter".into(), _ => return Err(bad),
            }]),
            "RequestWorld" if cmd.args.is_empty() => ("RequestWorld", vec!["9".into(); 3]),
            verb if PUBLIC_VERBS.iter().any(|(name, class)| *name == verb && *class == 'K') => return Err(bad),
            _ => return Ok(None),
        };
        Ok(Some((result.0.into(), result.1)))
    }

    fn dispatch(&mut self, cmd: &Command) -> Result<Vec<u8>, DispatchError> {
        let Some((_, class)) = PUBLIC_VERBS.iter().find(|(verb, _)| *verb == cmd.verb) else {
            return Err(RefusalCode::BadLine.into());
        };
        if *class == 'R' { return Err(RefusalCode::ClientNativeModesOff.into()); }
        // Match AuthoritativeSession.GateOwnedActorCommand: an unclaimed actor
        // is allowed, but a different player's claimed actor never reaches TS3W.
        let actor = match cmd.verb.as_str() {
            "SetActiveSim" | "CancelInteraction" | "LeaveConversation" => cmd.args.first().map(|s| parse::<u64>(s)).transpose()?,
            "AnswerDialog" => cmd.args.get(1).map(|s| parse::<u64>(s)).transpose()?,
            "QueueInteraction" => match cmd.args.as_slice() {
                [request] => Some(interaction(request)?.actor),
                _ => return Err(RefusalCode::BadLine.into()),
            },
            _ => None,
        };
        if let Some(actor) = actor {
            let sim = self.ids.map_wire(actor)?;
            let player = self.player(cmd.player_id)?;
            if self.world.authority.owners.get(&sim).is_some_and(|owner| *owner != player) {
                return Err(RefusalCode::UnauthorizedClientInsertion.into());
            }
        }
        if cmd.verb == "SetWorldTime" {
            let player = self.player(cmd.player_id)?;
            if self.world.client_roles.get(&player.0).is_some_and(|role| role == "presenter") {
                return Ok(refusal(cmd, RefusalCode::UnauthorizedClientInsertion,
                    &m::hex_encode("Only server-role players may set world time.")));
            }
        }
        if cmd.verb == "RequestWorld" || cmd.verb == "SaveGame" {
            if cmd.verb == "SaveGame" {
                if cmd.args.len() != 1 { return Err(RefusalCode::BadLine.into()); }
                m::hex_decode(&cmd.args[0]).map_err(|_| RefusalCode::BadLine)?;
            } else if !cmd.args.is_empty() { return Err(RefusalCode::BadLine.into()); }
            if self.checkpoint_queue.len() + self.world_transfers.len() >= m::COMMAND_ID_WINDOW {
                return Err(RefusalCode::QueueRejected.into());
            }
            self.save_faults.check_and_consume_save_failure().map_err(driver_error)?;
        }
        let mut fields = Vec::new();
        if let Some((verb, args)) = self.kernel_args(cmd)? {
            let player = self.player(cmd.player_id)?;
            self.kernel_corr = self.kernel_corr.checked_add(1).ok_or(RefusalCode::BadLine)?;
            let line = format!("CMD|{}|{verb}|{}", self.kernel_corr, args.join("|"));
            let mut replies = Vec::new();
            self.world.handle_line(player, RoleTag::Client, &line, &mut replies);
            // Wave 13 keeps SidecarVerdict.Classify's additive semantics. A
            // demo-family/unknown-dialog error is no opinion, never a veto of TS3W.
            if let Some(code) = replies.iter().filter(|line| line.starts_with("ERR|"))
                .filter_map(|line| line.split('|').nth(2)).filter_map(RefusalCode::parse)
                .find(|code| matches!(code,
                    RefusalCode::UnauthorizedClientInsertion | RefusalCode::ClientWorldServiceSuppressed
                    | RefusalCode::SlotAlreadyReserved | RefusalCode::NativeOracleOwned
                    | RefusalCode::ClientReselectForbidden))
            {
                return Err(code.into());
            }
            // Only the claim ACK contains a kernel SimId. Do not leak it (or demo kernel
            // presentation frames) onto the public socket; card 58 owns presentation routing.
            if cmd.verb == "ClaimSim" {
                let id = replies.iter().find(|r| r.starts_with("ACK|"))
                    .and_then(|r| r.split('|').nth(3)).ok_or(RefusalCode::BadLine)?;
                let wire = self.ids.kernel_to_wire(sim96_kernel_cleanroom::SimId(parse(id)?))
                    .ok_or(RefusalCode::BadLine)?;
                fields.push(wire.to_string());
                self.claimed_this_run.insert(wire);
            }
        }
        if cmd.verb == "RequestWorld" || (cmd.verb == "SaveGame" && self.request_connection.is_some()) {
            let conn = self.request_connection.ok_or_else(|| driver_error("checkpoint requires a requester"))?;
            self.checkpoint_queue.push_back((conn, cmd.clone()));
            if cmd.verb == "SaveGame" && self.checkpoint_job.is_none() && self.checkpoint_queue.len() == 1 {
                self.start_checkpoint()?;
                if let Some(mut job) = self.checkpoint_job.take() {
                    match self.advance_checkpoint(&mut job) {
                        Ok(true) => {}
                        Ok(false) => self.checkpoint_job = Some(job),
                        Err(error) => {
                            let (code, text) = w::game_refusal(&error.0);
                            return Ok(refusal(cmd, code, &text));
                        }
                    }
                }
            }
        } else if *class == 'B' || matches!(cmd.verb.as_str(), "QueueInteraction" | "AnswerDialog") {
            let guid = self.next_bridge_id()?;
            let bridge = typed_command(cmd, guid)?;
            let replies = self.endpoint.command(&bridge)?;
            for reply in &replies {
                if let Some(error) = reply.strip_prefix("ERROR|") {
                    let (_, hex) = error.split_once('|').ok_or(RefusalCode::BadLine)?;
                    // The mod's hex is uppercase; the public scalar codec is lowercase only.
                    let text = simlink_bridge::text::decode_text(hex).map_err(|_| RefusalCode::BadLine)?;
                    let (code, hex_text) = w::game_refusal(&text);
                    return Ok(refusal(cmd, code, &hex_text));
                }
                if cmd.verb == "RequestSimDescription" {
                    self.capture_description(cmd)?;
                }
            }
            if cmd.verb == "RequestSnapshot" {
                if let Some(snapshot) = self.decode_snapshot(&replies)? {
                    if let Some(requester) = self.request_connection {
                        let delta = cmd.args[0] != "~";
                        let mut fields = vec![snapshot.wire_body()];
                        if delta { fields.push("0".into()); }
                        self.pending_events.push(crate::routing::RoutedEvent {
                            event: m::Event { seq: 0, tick: snapshot.tick.max(0) as u64, corr: cmd.corr,
                                kind: if delta { "SnapshotDelta" } else { "FullSnapshot" }.into(), fields }, owner: None, requester: Some(requester),
                        });
                    }
                } else if self.request_connection.is_some() && self.endpoint.connected() {
                    return Err(driver_error("snapshot_unavailable").into());
                }
            }
            if cmd.verb == "QueueInteraction" {
                if let Some(requester) = self.request_connection {
                    if self.pick_requesters.len() == 256 { self.pick_requesters.pop_front(); }
                    self.pick_requesters.push_back((guid.to_n(), requester, cmd.corr));
                }
            }
        } else if *class == 'F' {
            match (cmd.verb.as_str(), cmd.args.as_slice()) {
                ("JoinSession", [name, screen]) => {
                    m::hex_decode(name).map_err(|_| RefusalCode::BadLine)?;
                    optional_text(screen)?;
                }
                ("LeaveSession", []) => self.release_player_claims(cmd.player_id, cmd.corr, self.request_connection),
                ("ReleaseSim", [sim]) => {
                    let sim = self.ids.map_wire(parse(sim)?)?;
                    let player = self.player(cmd.player_id)?;
                    if self.world.authority.owners.get(&sim) != Some(&player) {
                        return Err(RefusalCode::UnauthorizedClientInsertion.into());
                    }
                    self.release_sim(sim, cmd.corr, self.request_connection);
                }
                ("AckSnapshot", [tick]) => { let _: i64 = parse(tick)?; }
                _ => return Err(RefusalCode::BadLine.into()),
            }
        }
        w::encode_ack(&Ack { corr: cmd.corr, verb: cmd.verb.clone(), fields })
            .map(String::into_bytes).map_err(|_| RefusalCode::BadLine.into())
    }

    fn release_sim(&mut self, sim: sim96_kernel_cleanroom::SimId, corr: u64, requester: Option<crate::ConnId>) {
        let released = self.ids.kernel_to_wire(sim).and_then(|wire| self.owner(wire).map(|player| (wire, player)));
        if let Some((wire, _)) = released {
            if self.registered_gates.remove(&wire) && self.endpoint.connected() {
                let bridge_corr = self.next_bridge_id().expect("live bridge correlation space");
                if let Err(error) = self.endpoint.command(&BridgeCommand::UngateAutonomy { corr: bridge_corr, sim: wire, reset: false }) {
                    eprintln!("serve_report|ungate_autonomy_failed|sim={wire}|{error}");
                }
            }
        }
        self.world.authority.owners.remove(&sim);
        self.world.private_navigation.claim_changed(sim);
        self.world.authority.claimed_sims.remove(&sim);
        self.world.aspirations.claim_remove(sim);
        self.world.authority.active_households.retain(|hh| {
            self.world.authority.members.get(hh).is_some_and(|members| members.iter()
                .any(|s| self.world.authority.claimed_sims.contains(s)))
        });
        if self.endpoint.connected() {
            if let Some((wire, player)) = released {
                self.emit(corr, "SimReleased", vec![player.to_string(), wire.to_string()], requester);
                self.emit(corr, "SimAutonomyChanged", vec![wire.to_string(), "0".into()], requester);
            }
        }
    }
    fn release_player_claims(&mut self, public: i32, corr: u64, requester: Option<crate::ConnId>) {
        if let Some(player) = self.players.get(&public).copied() {
            self.world.client_roles.remove(&player.0);
            self.player_conns.remove(&public);
            let sims: Vec<_> = self.world.authority.owners.iter()
                .filter(|(_, owner)| **owner == player).map(|(sim, _)| *sim).collect();
            for sim in sims { self.release_sim(sim, corr, requester); }
            self.world.private_navigation.disconnected(player);
        }
    }

    fn check_boot_save(&self, name: &str) {
        if let Some(roots) = &self.save_roots {
            if let Some(folder) = crate::checkpoint::find_newest_save_folder_in_roots(roots, name)
                .or_else(|| roots.iter().map(|root| root.join(crate::checkpoint::ensure_sims3_extension(name)))
                    .find(|folder| folder.is_dir() || crate::last_good::folder_for(folder).is_dir()))
                .or_else(|| roots.first().map(|root| root.join(crate::checkpoint::ensure_sims3_extension(name))))
            {
                let check = crate::last_good::ensure_bootable(&folder,
                    &checkpoint_time(std::time::SystemTime::now()).replace([':', '.'], ""));
                eprintln!("[save] boot check: {}", check.detail);
            }
        }
    }

    fn start_checkpoint(&mut self) -> Result<(), DriverError> {
        if self.checkpoint_job.is_some() { return Ok(()); }
        let Some((conn, command)) = self.checkpoint_queue.pop_front() else { return Ok(()); };
        let join = command.verb == "RequestWorld";
        let checkpoint_slot = crate::checkpoint::checkpoint_save_id(self.checkpoint_save_name.as_deref(), &self.save_name);
        let save_id = if join {
            checkpoint_slot.clone()
        } else {
            crate::checkpoint::checkpoint_save_id(None, &m::hex_decode(&command.args[0]).map_err(driver_error)?)
        };
        let track_fingerprint = crate::checkpoint::join_reuse_enabled()
            && (join || save_id.eq_ignore_ascii_case(&checkpoint_slot));
        if !join || !track_fingerprint { self.reusable_world = None; }
        let stage = if join && track_fingerprint { CheckpointStage::Scan }
            else if track_fingerprint { CheckpointStage::Scope }
            else { CheckpointStage::Save };
        self.checkpoint_job = Some(CheckpointJob { conn, command, save_id, stage, track_fingerprint,
            armed_fingerprint: false,
            retry_at: None, started: Instant::now(), save_started: Instant::now(),
            fingerprint: None, reader: None, sent: false, save_completed: false });
        Ok(())
    }

    fn begin_checkpoint_read(&self, job: &mut CheckpointJob) {
        let roots = self.save_roots.clone();
        let id = job.save_id.clone();
        let gate = Arc::clone(&self.save_gate);
        let keeps = Arc::clone(&self.last_good_keeps);
        job.reader = Some(std::thread::spawn(move || {
            let _guard = gate.lock().map_err(driver_error)?;
            let roots = roots.unwrap_or_else(crate::checkpoint::save_roots);
            let folder = crate::checkpoint::find_newest_save_folder_in_roots(&roots, &id)
                .ok_or_else(|| driver_error(format!("SAVE_GAME checkpoint folder not found: {id}")))?;
            let world = crate::world_save::WorldSave::read_folder(&folder,
                &crate::checkpoint::ensure_sims3_extension(&id)).map_err(driver_error)?;
            let files: Vec<_> = world.files.iter().map(|file| (file.name.clone(), file.data.clone())).collect();
            let whole = crate::last_good::problem(&files).is_none();
            match crate::last_good::keep(&folder, &files, &checkpoint_time(std::time::SystemTime::now())) {
                Ok(_) => { keeps.fetch_add(1, std::sync::atomic::Ordering::Relaxed); }
                Err(error) => eprintln!("[save] LAST GOOD COPY NOT KEPT for {}: {error}", folder.display()),
            }
            Ok(ReadWorld { world: Arc::new(world), whole })
        }));
        job.stage = CheckpointStage::Read;
    }

    fn checkpoint_command(&mut self, job: &CheckpointJob) -> Result<BridgeCommand, DriverError> {
        let corr = self.next_bridge_id()?;
        let scope = crate::checkpoint::format_sim_scope(&self.claimed_this_run);
        Ok(match job.stage {
            CheckpointStage::Scan => BridgeCommand::SacsRecorder { corr, args: vec!["fingerprint-now".into(), scope] },
            CheckpointStage::Scope => BridgeCommand::SacsRecorder { corr, args: vec!["fingerprint-scope".into(), scope] },
            CheckpointStage::Save => BridgeCommand::SaveGame { corr,
                save_name: crate::checkpoint::ensure_sims3_extension(&job.save_id) },
            CheckpointStage::Saved => BridgeCommand::SacsRecorder { corr, args: vec!["fingerprint-saved".into()] },
            CheckpointStage::Read => return Err(driver_error("read stage has no bridge command")),
        })
    }

    fn world_events(&mut self, job: &CheckpointJob, world: Arc<crate::world_save::WorldSave>) {
        let (tx, rx) = std::sync::mpsc::sync_channel(1);
        let (conn, corr, tick) = (job.conn, job.command.corr, self.observed_tick);
        self.world_transfers.push(WorldTransfer { conn, corr, command_id: job.command.command_id,
            remaining: world.files.len(), events: rx });
        // Only immutable save bytes cross this seam; native handles stay on the
        // actor. Base64 and file-sized allocations must not stall other clients.
        std::thread::spawn(move || {
            for (index, file) in world.files.iter().enumerate() {
                let event = crate::routing::RoutedEvent { event: m::Event { seq: 0, tick, corr, kind: "WorldFile".into(), fields: vec![
                    m::hex_encode(&world.folder_name()), index.to_string(), world.files.len().to_string(),
                    m::hex_encode(&file.name), file.data.len().to_string(), w::b64_encode(&file.data),
                ] }, owner: None, requester: Some(conn) };
                if tx.send(event).is_err() { break; }
            }
        });
    }

    /// Advance only on a reply or completed disk job; never wait in the actor.
    fn advance_checkpoint(&mut self, job: &mut CheckpointJob) -> Result<bool, DriverError> {
        loop {
            if matches!(job.stage, CheckpointStage::Read) {
                let reader = job.reader.as_ref().expect("read worker");
                if !reader.is_finished() { return Ok(false); }
                let result = job.reader.take().expect("read worker").join()
                    .map_err(|_| driver_error("checkpoint reader panicked"))?;
                match result {
                    Ok(ReadWorld { world, whole }) => {
                        if let Some(fingerprint) = job.fingerprint.take() {
                            if job.track_fingerprint && whole {
                                self.reusable_world = Some((Arc::clone(&world), fingerprint));
                            }
                        }
                        if job.command.verb == "RequestWorld" { self.world_events(job, world); }
                    }
                    Err(error) if job.command.verb == "SaveGame" => {
                        eprintln!("[save] LAST GOOD COPY NOT KEPT for {}: {error}", job.save_id);
                    }
                    Err(error) => return Err(error),
                }
                return Ok(true);
            }
            if let Some(at) = job.retry_at {
                if Instant::now() < at { return Ok(false); }
                job.retry_at = None;
            }
            let timeout = if matches!(job.stage, CheckpointStage::Scan) {
                crate::join_reuse::JOIN_SCAN_TIMEOUT
            } else { Duration::from_secs(30) };
            let response = if job.sent { self.endpoint.poll_checkpoint_command() }
                else {
                    let command = self.checkpoint_command(job)?;
                    job.sent = true;
                    self.endpoint.begin_checkpoint_command(&command, timeout)
                }.or_else(|error| {
                    if matches!(job.stage, CheckpointStage::Scan | CheckpointStage::Scope | CheckpointStage::Saved) && !error.0.contains("process_exited") {
                        Ok(Some(vec![format!("ERROR|checkpoint|{}", simlink_bridge::text::encode_text(&error.0))]))
                    } else { Err(error) }
                })?;
            let Some(replies) = response else { return Ok(false); };
            job.sent = false;
            let error = replies.iter().find_map(|line| line.strip_prefix("ERROR|")
                .and_then(|rest| rest.split_once('|')).map(|(_, hex)| simlink_bridge::text::decode_text(hex)))
                .transpose().map_err(driver_error)?;
            if !matches!(job.stage, CheckpointStage::Scan | CheckpointStage::Scope | CheckpointStage::Saved) {
                if let Some(error) = error.as_deref() {
                    if matches!(job.stage, CheckpointStage::Save) && crate::checkpoint::is_not_ready_to_save(error)
                        && job.save_started.elapsed() < crate::checkpoint::CHECKPOINT_READY_WAIT
                    {
                        job.retry_at = Some(Instant::now() + crate::checkpoint::CHECKPOINT_RETRY_DELAY);
                        return Ok(false);
                    }
                    return Err(driver_error(format!("SAVE_GAME {}: {error}", job.save_id)));
                }
            }
            let report = replies.last().and_then(|line| line.split('|').nth(3))
                .and_then(|hex| simlink_bridge::text::decode_text(hex).ok());
            match job.stage {
                CheckpointStage::Scan => {
                    let fingerprint = crate::join_reuse::WorldFingerprint::parse(report.as_deref());
                    let scan_error = error.as_deref().or_else(|| fingerprint.as_ref().err().map(String::as_str));
                    let decision = crate::join_reuse::decide(self.reusable_world.as_ref().map(|(_, fp)| fp),
                        fingerprint.as_ref().ok(), scan_error);
                    {
                        let mut counters = self.join_reuse_counters.lock().expect("join counters");
                        if let Ok(fingerprint) = &fingerprint { counters.record_scan(fingerprint.scan_ms); }
                        counters.record(&decision);
                        eprintln!("{}", crate::join_reuse::join_reuse_line(&decision, "checkpoint", &counters));
                    }
                    if decision.reuse {
                        let world = Arc::clone(&self.reusable_world.as_ref().expect("reuse fingerprint").0);
                        self.world_events(job, world);
                        return Ok(true);
                    }
                    job.stage = CheckpointStage::Scope;
                }
                CheckpointStage::Scope => {
                    self.reusable_world = None;
                    job.armed_fingerprint = error.is_none()
                        && report.as_deref().is_some_and(|report| report.starts_with("fingerprint-scope="));
                    job.save_started = Instant::now();
                    job.stage = CheckpointStage::Save;
                }
                CheckpointStage::Save => {
                    job.save_completed = true;
                    let join = job.command.verb == "RequestWorld";
                    if join || job.track_fingerprint {
                        self.join_reuse_counters.lock().expect("join counters")
                            .record_save(job.save_started.elapsed().as_secs_f64() * 1000.0, join);
                    }
                    if job.command.verb == "SaveGame" {
                        self.emit(job.command.corr, "SaveCheckpointed", vec![job.command.args[0].clone(),
                            m::hex_encode(&checkpoint_time(std::time::SystemTime::now()))], None);
                    }
                    if job.armed_fingerprint { job.stage = CheckpointStage::Saved; }
                    else { self.begin_checkpoint_read(job); }
                }
                CheckpointStage::Saved => {
                    job.fingerprint = crate::join_reuse::WorldFingerprint::parse(report.as_deref()).ok();
                    if let Some(fp) = &job.fingerprint { self.join_reuse_counters.lock().expect("join counters").record_scan(fp.scan_ms); }
                    self.begin_checkpoint_read(job);
                }
                CheckpointStage::Read => unreachable!(),
            }
        }
    }

    fn poll_routed_events_into(&mut self, result: &mut Vec<crate::routing::RoutedEvent>) -> Result<(), DriverError> {
        self.enable_rust_switches()?;
        self.pump_checkpoint();
        if !self.endpoint.connected() && self.world.private_navigation.enabled {
            self.private_record = None;
            self.close_private_navigation(true);
            let dropped = self.world.private_navigation.take_deliveries().len();
            self.world.private_navigation.counters.delivery_failed += u64::try_from(dropped).unwrap_or(u64::MAX);
        }
        if self.world.private_navigation.enabled {
            self.world.private_navigation.expire(self.observed_tick);
            self.flush_private_navigation()?;
        }
        result.append(&mut self.pending_events);
        self.world_transfers.retain_mut(|transfer| match transfer.events.try_recv() {
            Ok(event) => {
                result.push(event);
                transfer.remaining -= 1;
                transfer.remaining > 0
            }
            Err(std::sync::mpsc::TryRecvError::Empty) => transfer.remaining > 0,
            Err(std::sync::mpsc::TryRecvError::Disconnected) => false,
        });
        let incoming = match self.endpoint.events() {
            Ok(events) => events,
            Err(error) => {
                self.private_record = None;
                if self.world.private_navigation.enabled {
                    self.close_private_navigation(true);
                    let dropped = self.world.private_navigation.take_deliveries().len();
                    self.world.private_navigation.counters.delivery_failed += u64::try_from(dropped).unwrap_or(u64::MAX);
                }
                return Err(error);
            }
        };
        for bytes in incoming {
            let line = std::str::from_utf8(&bytes).map_err(driver_error)?;
            if let Some(request) = line.strip_prefix("EVENT|RoadPlaceRequest|") {
                let (request_corr, fields) = request.split_once('|')
                    .ok_or_else(|| driver_error("road placement correlation missing"))?;
                let mut replies = Vec::new();
                self.world.handle_line(PlayerId(0), RoleTag::Server,
                    &format!("CMD|{request_corr}|PlaceOnRoadOffScreen|{fields}"), &mut replies);
                let args = if let Some(answer) = replies.iter().find(|line| line.starts_with("Rp|")) {
                    let fields: Vec<_> = answer.split('|').collect();
                    if fields[3] == "0" {
                        vec!["rust-road-place-none".into(), request_corr.into(), fields[10].into()]
                    } else {
                        let half_yaw = f64::from(fields[7].parse::<i32>().map_err(driver_error)?)
                            * std::f64::consts::PI / 360_000.0;
                        let mut args = vec!["rust-road-place".into(), request_corr.into()];
                        for value in &fields[4..7] {
                            args.push((f64::from(value.parse::<i32>().map_err(driver_error)?) / 100.0).to_string());
                        }
                        args.extend(["0".into(), half_yaw.sin().to_string(), "0".into(), half_yaw.cos().to_string(),
                            fields[8].into(), fields[9].into(), fields[10].into()]);
                        args
                    }
                } else {
                    let reason = replies.iter().find_map(|line| line.strip_prefix("ERR|"))
                        .and_then(|line| line.split('|').nth(2))
                        .ok_or_else(|| driver_error("road placement reply missing"))?;
                    vec!["rust-road-place-decline".into(), request_corr.into(), reason.into()]
                };
                let corr = self.next_bridge_id()?;
                let replies = self.endpoint.command(&BridgeCommand::SacsRecorder { corr, args })?;
                Self::private_reply_ok(&replies, "AUTONOMY_PROBE", corr, None)?;
                continue;
            }
            if line.starts_with("EVENT|PrivateNavigation") {
                match self.private_chunk(line) {
                    Ok(Some(frame)) => {
                        if self.private_navigation_frame(frame).is_err() {
                            self.close_private_navigation(false);
                            self.flush_private_navigation()?;
                            *self.event_drops.entry("PrivateNavigationDelivery".into()).or_default() += 1;
                        }
                    }
                    Ok(None) => {}
                    Err(_) => {
                        self.private_record = None;
                        self.world.private_navigation.counters.malformed += 1;
                        self.close_private_navigation(false);
                        self.flush_private_navigation()?;
                        *self.event_drops.entry("PrivateNavigation".into()).or_default() += 1;
                    }
                }
                continue;
            }
            if !line.starts_with("EVENT|") {
                eprintln!("serve_report|unmatched_bridge_reply|{line}");
                continue;
            }
            let mut event = match crate::events::decode_bridge_event(line, self.observed_tick) {
                Ok(event) => event,
                Err(error) => {
                    let kind = line.split('|').nth(1).unwrap_or("?");
                    *self.event_drops.entry(kind.to_owned()).or_default() += 1;
                    eprintln!("serve_report|event_decode_drop|{error}");
                    continue;
                }
            };
            let owner = if event.kind == "DialogRequested" {
                event.fields.get(1).and_then(|s| s.parse().ok()).and_then(|sim| self.owner(sim))
            } else { None };
            let mut requester = None;
            if event.kind == "PickFailed" {
                let guid = Guid::parse(line.split('|').nth(2).unwrap_or("")).map(|g| g.to_n()).unwrap_or_default();
                if let Some(index) = self.pick_requesters.iter().position(|(key, _, _)| *key == guid) {
                    let (_, id, corr) = self.pick_requesters.remove(index).expect("found");
                    requester = Some(id);
                    event.corr = corr;
                }
            }
            result.push(crate::routing::RoutedEvent { event, owner, requester });
        }
        Ok(())
    }

    fn pump_checkpoint(&mut self) {
        if let Err(error) = self.start_checkpoint() {
            eprintln!("serve_report|checkpoint_failure|{error}");
        }
        let Some(mut job) = self.checkpoint_job.take() else { return; };
        match self.advance_checkpoint(&mut job) {
            Ok(true) => {
                eprintln!("serve_report|checkpoint_complete|save={}|elapsed_ms={}", job.save_id, job.started.elapsed().as_millis());
            }
            Ok(false) => self.checkpoint_job = Some(job),
            Err(error) => {
                eprintln!("serve_report|checkpoint_failure|save={}|{error}", job.save_id);
                if !job.save_completed || job.command.verb == "RequestWorld" {
                    self.emit(job.command.corr, "CommandRejected", vec![job.command.command_id.to_string(),
                        m::hex_encode(&error.0)], Some(job.conn));
                }
            }
        }
    }
}

impl<B: BridgeEndpoint> GameDriver for BridgeGameDriver<B> {
    fn start_stream_request(&mut self, request: &StreamRequest) -> Result<Option<StreamReply>, DriverError> {
        if self.stream_request.is_some() { return Err(driver_error("stream request already pending")); }
        let corr = self.next_bridge_id()?;
        let command = match request {
            StreamRequest::Snapshot(since) => BridgeCommand::RequestSnapshot { corr, since_tick: (*since >= 0).then_some(*since) },
            StreamRequest::Recorder(args) => BridgeCommand::SacsRecorder { corr, args: args.clone() },
            StreamRequest::DiagState => BridgeCommand::DiagState { corr },
        };
        match self.endpoint.begin_stream_command(&command)? {
            Some(replies) => self.decode_stream_reply(request, &replies).map(Some),
            None => { self.stream_request = Some(request.clone()); Ok(None) }
        }
    }
    fn poll_stream_request(&mut self) -> Result<Option<StreamReply>, DriverError> {
        let replies = match self.endpoint.poll_stream_command() {
            Ok(Some(replies)) => replies,
            Ok(None) => return Ok(None),
            Err(error) => { self.stream_request = None; return Err(error); }
        };
        let request = self.stream_request.take().ok_or_else(|| driver_error("stream reply has no request"))?;
        self.decode_stream_reply(&request, &replies).map(Some)
    }
    fn stream_request_pending(&self) -> bool { self.stream_request.is_some() }
    fn baseline_pending(&self, conn: crate::ConnId) -> bool {
        self.failed_baselines.contains(&conn) || self.join_queue.iter().any(|job| job.conn == conn && !job.closed)
    }
    fn baseline_failed(&self, conn: crate::ConnId) -> bool { self.failed_baselines.contains(&conn) }
    fn baseline_closed(&mut self, conn: crate::ConnId) {
        self.failed_baselines.remove(&conn);
        if self.join_request_active {
            if let Some(job) = self.join_queue.front_mut().filter(|job| job.conn == conn) { job.closed = true; }
        }
        self.join_queue.retain(|job| job.conn != conn || job.closed);
    }
    fn adjudicate(&mut self, cmd: &Command) -> Result<Vec<u8>, DriverError> {
        match self.dispatch(cmd) {
            Ok(reply) => Ok(reply),
            Err(DispatchError::Refusal(code)) => Ok(refusal(cmd, code, "")),
            Err(DispatchError::Driver(error)) => Err(error),
        }
    }
    fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, DriverError> {
        self.endpoint.events()
    }
    fn adjudicate_for(&mut self, conn: crate::ConnId, cmd: &Command) -> Result<Vec<u8>, DriverError> {
        self.enable_rust_switches()?;
        self.request_connection = Some(conn);
        let prior_claims: Vec<_> = self.claimed_sims().into_iter().filter(|sim| self.owner(*sim) == Some(cmd.player_id)).collect();
        let reply = self.adjudicate(cmd);
        let reply = reply.and_then(|reply| {
            if reply.starts_with(b"ACK|") && self.endpoint.connected() {
                if cmd.verb == "JoinSession" {
                    self.join_queue.push_back(PendingJoinBaseline { conn, command: cmd.clone(), closed: false });
                    if self.join_queue.len() == 1 && !self.join_request_active { self.advance_join_baselines(true)?; }
                } else {
                    self.command_events(cmd, &prior_claims)?;
                }
            }
            if reply.starts_with(b"ACK|") && cmd.verb == "JoinSession" && !self.endpoint.connected() {
                let player = self.player(cmd.player_id).map_err(|error| driver_error(error.as_str()))?;
                self.world.private_navigation.actor_join(conn.0, player);
                self.player_conns.insert(cmd.player_id, conn.0);
                self.emit(cmd.corr, "LotLocalAuthorityModeChanged",
                    vec![cmd.player_id.to_string(), "FullPuppetFallback".into()], Some(conn));
                self.nav_changed();
            }
            if reply.starts_with(b"ACK|") && matches!(cmd.verb.as_str(), "LeaveSession" | "ClaimSim" | "ReleaseSim") {
                self.nav_changed();
            }
            Ok(reply)
        });
        self.request_connection = None;
        reply
    }
    fn poll_routed_events(&mut self) -> Result<Vec<crate::routing::RoutedEvent>, DriverError> {
        let joins = self.advance_join_baselines(false);
        let mut result = std::mem::take(&mut self.join_events);
        let outcome = joins.and_then(|()| self.poll_routed_events_into(&mut result));
        result.append(&mut self.pending_events);
        if let Err(error) = outcome {
            self.pending_events = result;
            return Err(error);
        }
        Ok(result)
    }
    fn take_pending_events(&mut self) -> Vec<crate::routing::RoutedEvent> {
        let mut events = std::mem::take(&mut self.join_events);
        events.append(&mut self.pending_events);
        events
    }
    fn take_epoch_announcement(&mut self) -> Option<u64> {
        self.nav_epoch_pending
    }
    fn announced(&mut self, epoch: u64) {
        if self.rust_switches.0 && self.nav_epoch_pending == Some(epoch) {
            self.emit(0, "SimAnimation", vec![m::enc_list(&[m::hex_encode(&format!("0|Ne|{epoch}"))])], None);
            self.nav_epoch_announced = epoch;
            self.nav_epoch_pending = None;
        }
    }
    fn terminal_events(&mut self, error: &DriverError) -> Vec<crate::routing::RoutedEvent> {
        while let Some(job) = self.join_queue.pop_front() {
            if job.closed { continue; }
            self.failed_baselines.insert(job.conn);
            self.emit(job.command.corr, "CommandRejected", vec![job.command.command_id.to_string(), m::hex_encode(&error.0)], Some(job.conn));
        }
        self.join_request_active = false;
        if let Some(job) = self.checkpoint_job.take() {
            if !job.save_completed || job.command.verb == "RequestWorld" {
                self.emit(job.command.corr, "CommandRejected", vec![job.command.command_id.to_string(),
                    m::hex_encode(&error.0)], Some(job.conn));
            }
        }
        while let Some((conn, command)) = self.checkpoint_queue.pop_front() {
            self.emit(command.corr, "CommandRejected", vec![command.command_id.to_string(),
                m::hex_encode(&error.0)], Some(conn));
        }
        for transfer in std::mem::take(&mut self.world_transfers) {
            if transfer.remaining > 0 {
                self.emit(transfer.corr, "CommandRejected", vec![transfer.command_id.to_string(),
                    m::hex_encode(&error.0)], Some(transfer.conn));
            }
        }
        std::mem::take(&mut self.pending_events)
    }
    fn admin_request(&mut self, request: &crate::admin::Request) -> Result<String, DriverError> {
        if request.name == "RUST_ROUTE_PLANS" {
            return Ok(self.route_plans_reply(&request.args));
        }
        let mut loaded_name = None;
        if request.name == "LOAD_SAVE" {
            self.private_record = None;
            if self.world.private_navigation.enabled {
                self.close_private_navigation(false);
                self.flush_private_navigation()?;
            } else {
                self.nav_detached();
            }
            if self.checkpoint_job.is_some() || !self.checkpoint_queue.is_empty() {
                return Err(driver_error("LOAD_SAVE refused while SAVE_GAME checkpoint is in progress"));
            }
            self.reusable_world = None;
            self.claimed_this_run.clear();
            if let Some(name) = request.args.first().and_then(|s| simlink_bridge::text::decode_text(s).ok()) {
                self.check_boot_save(&name);
                loaded_name = Some(name);
            }
        }
        let corr = self.next_bridge_id()?;
        let replies = self.endpoint.command_with_timeout(&BridgeCommand::Raw {
            corr, name: request.name.clone(), args: request.args.clone(),
        }, Duration::from_secs(request.timeout_seconds))?;
        if !replies.iter().any(|reply| reply.starts_with("ERROR|")) {
            if let Some(name) = loaded_name {
                self.save_name = name;
                self.route_plans.clear();
                self.world.road_set = None;
                self.observed_tick = 0;
                self.switches_sent = false;
            }
        } else if request.name == "LOAD_SAVE" {
            // The attachment closed before the command, so a refused load still bumps the epoch.
            if let Err(error) = self.sync_navigation() {
                eprintln!("serve_report|navigation_sync_failed|{error}");
            }
        }
        if let Some(bytes) = self.endpoint.take_snapshot() {
            let snapshot = crate::snapshot::Snapshot::decode(&bytes).ok_or_else(|| driver_error("snapshot2_decode_failed"))?;
            let summary = format!("tick={} sims={} objects={}", snapshot.tick, snapshot.sims.len(), snapshot.objects.len());
            return Ok(format!("{{\"ok\":true,\"accepted\":true,\"reason\":null,\"report\":null,\"snapshot\":{}}}",
                crate::admin::json_string(&summary)));
        }
        crate::admin::bridge_reply(&replies)
    }
    fn admin_upload(&mut self, blob: &str, bytes: &[u8]) -> Result<String, DriverError> {
        let corr = self.next_bridge_id()?;
        crate::admin::upload_reply(&self.endpoint.upload(corr, blob, bytes)?)
    }
    fn snapshot(&mut self, since: i64) -> Result<Option<crate::snapshot::Snapshot>, DriverError> {
        let corr = self.next_bridge_id()?;
        let replies = self.endpoint.command(&BridgeCommand::RequestSnapshot { corr, since_tick: (since >= 0).then_some(since) })?;
        self.decode_snapshot(&replies)
    }
    fn recorder(&mut self, args: &[String]) -> Result<String, DriverError> {
        let corr = self.next_bridge_id()?;
        let replies = self.endpoint.command(&BridgeCommand::SacsRecorder { corr, args: args.to_vec() })?;
        let Some(reply) = replies.last() else { return Ok(String::new()); };
        if let Some(hex) = reply.split('|').nth(3) {
            return simlink_bridge::text::decode_text(hex).map_err(driver_error);
        }
        Err(driver_error(format!("recorder reply malformed: {reply}")))
    }
    fn diag_state(&mut self) -> Result<Option<String>, DriverError> {
        let corr = self.next_bridge_id()?;
        let replies = self.endpoint.command(&BridgeCommand::DiagState { corr })?;
        self.decode_diag(&replies)
    }
    fn claimed_sims(&self) -> Vec<u64> {
        let mut sims: Vec<_> = self.world.authority.owners.keys().filter_map(|sim| self.ids.kernel_to_wire(*sim)).collect();
        sims.sort();
        sims
    }
    fn owner(&self, sim: u64) -> Option<i32> {
        self.world.authority.owners.iter().find_map(|(kernel, owner)| {
            if self.ids.kernel_to_wire(*kernel) != Some(sim) { return None; }
            self.players.iter().find_map(|(public, player)| (*player == *owner).then_some(*public))
        })
    }
    fn event_drop_state(&self) -> String {
        let mut counts = self.event_drops.iter().map(|(kind, count)| format!("{kind}:{count}")).collect::<Vec<_>>();
        let replies = self.endpoint.dropped_replies();
        if replies > 0 { counts.push(format!("BridgeReply:{replies}")); }
        if counts.is_empty() { "-".into() } else { counts.join("/") }
    }
    fn routing_state(&self) -> Option<String> {
        Some(format!("navigate_requests={}|plans_served={}|native_oracle_owned={}|road_place_requests={}|road_place_served={}|road_load_failures={}",
            self.world.navigate_requests, self.world.plans_served, self.world.native_oracle_owned_text(),
            self.world.road_place_requests, self.world.road_place_served, self.world.road_load_failures))
    }
    fn release_player(&mut self, public: i32) {
        self.release_player_claims(public, 0, None);
        self.nav_changed();
    }
}

impl<B: BridgeEndpoint> BridgeGameDriver<B> {
    fn decode_stream_reply(&mut self, request: &StreamRequest, replies: &[String]) -> Result<StreamReply, DriverError> {
        match request {
            StreamRequest::Snapshot(_) => self.decode_snapshot(replies).map(StreamReply::Snapshot),
            StreamRequest::Recorder(_) => {
                let Some(reply) = replies.last() else { return Ok(StreamReply::Recorder(String::new())); };
                let hex = reply.split('|').nth(3).ok_or_else(|| driver_error(format!("recorder reply malformed: {reply}")))?;
                simlink_bridge::text::decode_text(hex).map(StreamReply::Recorder).map_err(driver_error)
            }
            StreamRequest::DiagState => self.decode_diag(replies).map(StreamReply::DiagState),
        }
    }
    fn advance_join_baselines(&mut self, eager: bool) -> Result<(), DriverError> {
        loop {
            let Some(job) = self.join_queue.front().cloned() else { return Ok(()); };
            let reply = if self.join_request_active {
                self.poll_stream_request()
            } else {
                if self.stream_request_pending() { return Ok(()); }
                self.start_stream_request(&StreamRequest::Snapshot(-1))
            };
            let snapshot = match reply {
                Ok(None) => { self.join_request_active = true; return Ok(()); }
                Ok(Some(StreamReply::Snapshot(Some(snapshot)))) => Ok(snapshot),
                Ok(Some(_)) => Err(driver_error("join_snapshot_unavailable")),
                Err(error) => Err(error),
            };
            self.join_request_active = false;
            self.join_queue.pop_front();
            if job.closed { continue; }
            match snapshot {
                Ok(mut snapshot) => {
                    let start = self.pending_events.len();
                    self.release_player_claims(job.command.player_id, 0, None);
                    for sim in &mut snapshot.sims { sim.owning_player_id = self.owner(sim.object_id); }
                    self.emit(job.command.corr, "SessionJoined", vec![job.command.player_id.to_string(),
                        job.command.args[0].clone(), "~".into(), m::enc_list(&[m::hex_encode("presentation_stream")])], None);
                    self.emit(job.command.corr, "GameSpeedChanged", vec![snapshot.speed.clone()], None);
                    self.emit(job.command.corr, "FullSnapshot", vec![snapshot.wire_body()], Some(job.conn));
                    let player = self.player(job.command.player_id).map_err(|error| driver_error(error.as_str()))?;
                    self.world.private_navigation.actor_join(job.conn.0, player);
                    self.player_conns.insert(job.command.player_id, job.conn.0);
                    self.emit(job.command.corr, "LotLocalAuthorityModeChanged",
                        vec![job.command.player_id.to_string(), "FullPuppetFallback".into()], Some(job.conn));
                    if self.nav_epoch_announced > 0 {
                        self.emit(0, "SimAnimation", vec![m::enc_list(&[m::hex_encode(
                            &format!("0|Ne|{}", self.nav_epoch_announced))])], Some(job.conn));
                    }
                    self.join_events.extend(self.pending_events.split_off(start));
                    self.nav_changed();
                }
                Err(error) => {
                    self.failed_baselines.insert(job.conn);
                    if eager { return Err(error); }
                    self.emit(job.command.corr, "CommandRejected",
                        vec![job.command.command_id.to_string(), m::hex_encode(&error.0)], Some(job.conn));
                    if error.0.contains("process_exited") { return Err(error); }
                }
            }
        }
    }
    /// `RUST_ROUTE_PLANS` alone dumps every retained plan; `<corr> [<cursor> [<max_lines> [<epoch>]]]`
    /// returns one bounded page of one plan (Card 55 contract 3.3).
    fn route_plans_reply(&self, args: &[String]) -> String {
        if args.is_empty() {
            return crate::admin::report(&self.route_plans.iter().flatten().cloned().collect::<Vec<_>>().join("\n"));
        }
        let parsed = (|| {
            if args.len() > 4 { return None; }
            let corr: u64 = args[0].parse().ok().filter(|corr| *corr > 0)?;
            let cursor: u32 = args.get(1).map_or(Some(0), |arg| arg.parse().ok())?;
            let max_lines: u32 = args.get(2).map_or(Some(crate::nav_sync::PLAN_PAGE_LINES_MAX), |arg| arg.parse().ok())?;
            let epoch = if let Some(arg) = args.get(3) {
                if !matches!(arg.as_bytes().first(), Some(b'1'..=b'9')) || !arg.bytes().all(|b| b.is_ascii_digit()) {
                    return None;
                }
                Some(arg.parse::<u64>().ok()?)
            } else { None };
            Some((corr, cursor, max_lines, epoch))
        })();
        match parsed {
            None => crate::admin::error("RUST_ROUTE_PLANS takes [<corr> [<cursor> [<max_lines> [<epoch>]]]]"),
            Some((_, cursor, _, None)) if cursor > 0 => crate::admin::error("rust-route-client=blocked;reason=epoch-required"),
            Some((_, _, _, Some(epoch))) if epoch != self.nav_epoch => crate::admin::error("rust-route-client=blocked;reason=epoch-mismatch"),
            Some((corr, cursor, max_lines, _)) => match crate::nav_sync::plan_page(&self.route_plans, self.nav_epoch, corr, cursor, max_lines) {
                Ok(page) => crate::admin::report(&page),
                Err(error) => crate::admin::error(error.text()),
            },
        }
    }

    fn decode_diag(&mut self, replies: &[String]) -> Result<Option<String>, DriverError> {
        let report = replies.last().and_then(|line| line.split('|').nth(3))
            .map(|hex| simlink_bridge::text::decode_text(hex).map_err(driver_error)).transpose()?;
        if let Some(tick) = report.as_deref().and_then(|report| report.split(';')
            .find_map(|field| field.strip_prefix("tick="))).and_then(|tick| tick.parse::<u64>().ok()) {
            self.observed_tick = self.observed_tick.max(tick);
        }
        Ok(report)
    }
    fn decode_snapshot(&mut self, replies: &[String]) -> Result<Option<crate::snapshot::Snapshot>, DriverError> {
        let mut snapshot = if let Some(bytes) = self.endpoint.take_snapshot() {
            Some(crate::snapshot::Snapshot::decode(&bytes).ok_or_else(|| driver_error("snapshot2_decode_failed"))?)
        } else {
            replies.iter().find_map(|line| simlink_bridge::reply::decode_response(line).ok().flatten()
                .and_then(|r| r.snapshot).map(crate::snapshot::Snapshot::from_bridge))
        };
        if let Some(snapshot) = &mut snapshot {
            self.observed_tick = snapshot.tick.max(0) as u64;
            for sim in &mut snapshot.sims {
                self.sim_lots.insert(sim.object_id, sim.lot_id);
                sim.owning_player_id = self.owner(sim.object_id);
            }
        }
        Ok(snapshot)
    }
    fn emit(&mut self, corr: u64, kind: &str, fields: Vec<String>, requester: Option<crate::ConnId>) {
        self.pending_events.push(crate::routing::RoutedEvent {
            event: m::Event { seq: 0, tick: self.observed_tick, corr, kind: kind.into(), fields },
            owner: None, requester,
        });
    }
    fn command_events(&mut self, cmd: &Command, prior_claims: &[u64]) -> Result<(), DriverError> {
        match cmd.verb.as_str() {
            "ClaimSim" => {
                let sim: u64 = cmd.args[0].parse().map_err(driver_error)?;
                if !prior_claims.contains(&sim) {
                    // Retain the clear obligation even if the registration reply is lost.
                    self.registered_gates.insert(sim);
                    match self.next_bridge_id() {
                        Ok(corr) => {
                            if let Err(error) = self.endpoint.command(&BridgeCommand::GateAutonomy { corr, sim }) {
                                eprintln!("serve_report|gate_autonomy_failed|sim={sim}|{error}");
                            }
                        }
                        Err(error) => eprintln!("serve_report|gate_autonomy_failed|sim={sim}|{error}"),
                    }
                    if self.sim_lots.get(&sim).copied().flatten().is_none() {
                        if let Err(error) = self.snapshot(-1) {
                            eprintln!("serve_report|claim_lot_snapshot_failed|sim={sim}|{error}");
                        }
                    }
                    let lot = self.sim_lots.get(&sim).copied().flatten().map_or_else(|| "~".into(), |lot| lot.to_string());
                    self.emit(cmd.corr, "SimClaimed", vec![cmd.player_id.to_string(), sim.to_string(), lot], None);
                    self.emit(cmd.corr, "SimAutonomyChanged", vec![sim.to_string(), "1".into()], None);
                }
            }
            "SetGameSpeed" => self.emit(cmd.corr, "GameSpeedChanged", cmd.args.clone(), None),
            "SetWorldTime" => self.emit(cmd.corr, "WorldTimeChanged", cmd.args.clone(), None),
            _ => {}
        }
        Ok(())
    }
    fn next_bridge_id(&mut self) -> Result<Guid, DriverError> {
        self.bridge_corr = self.bridge_corr.checked_add(1).ok_or_else(|| driver_error("bridge corr exhausted"))?;
        let mut guid = [0; 16];
        guid[..8].copy_from_slice(&self.bridge_nonce);
        guid[8..].copy_from_slice(&self.bridge_corr.to_be_bytes());
        Ok(Guid(guid))
    }
    fn capture_description(&mut self, cmd: &Command) -> Result<(), RefusalCode> {
        let Some(requester) = self.request_connection else { return Ok(()); };
        if let Some(description) = self.endpoint.take_description() {
            let byte_list = |bytes: &[u8]| m::enc_list(&bytes.iter().map(u8::to_string).collect::<Vec<_>>());
            self.pending_events.push(crate::routing::RoutedEvent {
                event: m::Event { seq: 0, tick: self.observed_tick, corr: cmd.corr, kind: "SimDescriptionData".into(),
                    fields: vec![description.desc.to_string(), byte_list(&description.property_stream), byte_list(&description.res_key_table), byte_list(&description.object_id_table)] },
                owner: None, requester: Some(requester),
            });
        }
        Ok(())
    }
}

fn checkpoint_time(now: std::time::SystemTime) -> String {
    let elapsed = now.duration_since(std::time::UNIX_EPOCH).expect("checkpoint after Unix epoch");
    let seconds = elapsed.as_secs();
    let mut days = seconds / 86400;
    let mut year = 1970u64;
    let leap = |year| year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);
    loop {
        let length = if leap(year) { 366 } else { 365 };
        if days < length { break; }
        days -= length; year += 1;
    }
    let months = [31, if leap(year) { 29 } else { 28 }, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
    let mut month = 0;
    while days >= months[month] { days -= months[month]; month += 1; }
    format!("{year:04}-{:02}-{:02}T{:02}:{:02}:{:02}.{:07}+00:00",
        month + 1, days + 1, seconds / 3600 % 24, seconds / 60 % 60, seconds % 60, elapsed.subsec_nanos() / 100)
}

#[cfg(test)]
#[test]
fn checkpoint_timestamp_uses_utc_roundtrip_ticks_and_gregorian_leap_days() {
    let epoch = std::time::UNIX_EPOCH;
    assert_eq!(checkpoint_time(epoch), "1970-01-01T00:00:00.0000000+00:00");
    assert_eq!(checkpoint_time(epoch + Duration::new(951782400, 123456789)), "2000-02-29T00:00:00.1234567+00:00");
    assert_eq!(checkpoint_time(epoch + Duration::from_secs(951782400 + 86400)), "2000-03-01T00:00:00.0000000+00:00");
}

fn parse<T: std::str::FromStr>(field: &str) -> Result<T, RefusalCode> {
    field.parse().map_err(|_| RefusalCode::BadLine)
}
fn optional_text(field: &str) -> Result<Option<String>, RefusalCode> {
    m::dec_opt(field).map(m::hex_decode).transpose().map_err(|_| RefusalCode::BadLine)
}
fn interaction(field: &str) -> Result<InteractionRequest, RefusalCode> {
    let bad = RefusalCode::BadLine;
    let fields = m::dec_record(field).map_err(|_| bad)?;
    let [actor, key, target, position, parameters] = fields.as_slice() else { return Err(bad); };
    let position = m::dec_opt(position).map(|p| {
        let values = m::dec_record(p).map_err(|_| bad)?;
        let [x, y, z] = values.as_slice() else { return Err(bad); };
        Ok(Vec3 { x: parse(x)?, y: parse(y)?, z: parse(z)? })
    }).transpose()?;
    let parameters = m::dec_list(parameters).map_err(|_| bad)?.iter().map(|item| {
        let pair = m::dec_record(item).map_err(|_| bad)?;
        let [k, v] = pair.as_slice() else { return Err(bad); };
        Ok((m::hex_decode(k).map_err(|_| bad)?, m::hex_decode(v).map_err(|_| bad)?))
    }).collect::<Result<_, RefusalCode>>()?;
    Ok(InteractionRequest {
        actor: parse(actor)?, key: m::hex_decode(key).map_err(|_| bad)?,
        target: m::dec_opt(target).map(parse).transpose()?, position, parameters,
    })
}

/// C# positional argument order, with scalar and composite validation at the public boundary.
pub fn typed_command(cmd: &Command, corr: Guid) -> Result<BridgeCommand, RefusalCode> {
    let bad = RefusalCode::BadLine;
    Ok(match (cmd.verb.as_str(), cmd.args.as_slice()) {
        ("SetActiveSim", [sim]) => BridgeCommand::SetActiveSim { corr, sim: parse(sim)? },
        ("CancelInteraction", [actor, id]) => BridgeCommand::CancelInteraction { corr, actor: parse(actor)?, interaction_id: parse(id)? },
        ("LeaveConversation", [actor]) => BridgeCommand::LeaveConversation { corr, actor: parse(actor)? },
        ("SetGameSpeed", [speed]) => BridgeCommand::SetGameSpeed { corr, speed: GameSpeed::from_name(speed).ok_or(bad)? },
        ("SetWorldTime", [time]) => {
            let fields = m::dec_record(time).map_err(|_| bad)?;
            let [day, hours, minutes] = fields.as_slice() else { return Err(bad); };
            let (day, hours, minutes) = (parse::<i32>(day)?, parse::<i32>(hours)?, parse::<i32>(minutes)?);
            if !(0..=6).contains(&day) || !(0..=23).contains(&hours) || !(0..=59).contains(&minutes) {
                return Err(bad);
            }
            BridgeCommand::SetWorldTime { corr, day, hours, minutes }
        }
        ("SaveGame", [name]) => BridgeCommand::SaveGame { corr, save_name: m::hex_decode(name).map_err(|_| bad)? },
        ("RequestSnapshot", [since]) => BridgeCommand::RequestSnapshot { corr, since_tick: m::dec_opt(since).map(parse).transpose()? },
        ("RequestSimDescription", [desc]) => BridgeCommand::ExportSimDescription { corr, desc: parse(desc)? },
        ("QueueInteraction", [field]) => BridgeCommand::QueueInteraction { corr, interaction: interaction(field)? },
        ("AnswerDialog", [request, sim, answer]) => {
            let _: u64 = parse(sim)?;
            BridgeCommand::AnswerDialog { corr, request_id: parse(request)?, answer: optional_text(answer)? }
        }
        _ => return Err(bad),
    })
}

pub(crate) fn refusal(cmd: &Command, code: RefusalCode, hex_text: &str) -> Vec<u8> {
    // These fields have already passed the codec; no encoding failure can be hidden.
    w::encode_err(&ErrFrame { corr: cmd.corr, code, verb: cmd.verb.clone(), hex_text: hex_text.into() })
        .expect("validated refusal fields").into_bytes()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[derive(Default)]
    struct InstrumentBridge {
        commands: Vec<BridgeCommand>,
        events: Vec<Vec<u8>>,
        tick: u64,
        refuse_load: bool,
        defer_snapshots: bool,
        snapshot_ready: bool,
        pending_snapshot: Option<Guid>,
    }
    impl BridgeEndpoint for InstrumentBridge {
        fn command(&mut self, command: &BridgeCommand) -> Result<Vec<String>, DriverError> {
            self.commands.push(command.clone());
            let encoded = simlink_bridge::verbs::encode(command).unwrap();
            let corr = encoded.split('|').nth(1).unwrap();
            Ok(vec![match command {
                BridgeCommand::Raw { name, .. } if name == "LOAD_SAVE" && self.refuse_load =>
                    format!("ERROR|{corr}|{}", simlink_bridge::text::encode_text("load refused")),
                BridgeCommand::DiagState { .. } =>
                    format!("DIAG_STATE_REPORT|{corr}|ok|{}", simlink_bridge::text::encode_text(&format!("inWorld=True;tick={}", self.tick))),
                BridgeCommand::SacsRecorder { .. } => format!("AUTONOMY_PROBE|{corr}|ok|"),
                _ => format!("OK|{corr}"),
            }])
        }
        fn events(&mut self) -> Result<Vec<Vec<u8>>, DriverError> { Ok(std::mem::take(&mut self.events)) }
        fn begin_stream_command(&mut self, command: &BridgeCommand) -> Result<Option<Vec<String>>, DriverError> {
            if let BridgeCommand::RequestSnapshot { corr, .. } = command {
                if self.defer_snapshots {
                    assert!(self.pending_snapshot.replace(*corr).is_none());
                    return Ok(None);
                }
            }
            self.command(command).map(Some)
        }
        fn poll_stream_command(&mut self) -> Result<Option<Vec<String>>, DriverError> {
            if !self.snapshot_ready { return Ok(None); }
            Ok(self.pending_snapshot.take().map(|corr|
                vec![format!("SNAPSHOT|{}|77|57|53|Normal|0|0", corr.to_n())]))
        }
    }

    #[test]
    fn card55_deferred_join_registers_connection_after_claim_cleanup_and_marks_sync_dirty() {
        let mut driver = BridgeGameDriver::new(InstrumentBridge { defer_snapshots: true, ..InstrumentBridge::default() });
        driver.configure_rust_switches(false, false, false);
        driver.player(7).unwrap();
        driver.player_conns.insert(7, 9);
        let command = Command { corr: 1, command_id: 1, player_id: 7, verb: "JoinSession".into(),
            args: vec![m::hex_encode("Fixture"), "~".into()] };
        assert_eq!(driver.adjudicate_for(crate::ConnId(11), &command).unwrap(), b"ACK|1|JoinSession");
        assert!(driver.baseline_pending(crate::ConnId(11)));
        assert_eq!(driver.player_conns.get(&7), Some(&9), "registration overtook the baseline");
        driver.nav_dirty = false;
        driver.endpoint.snapshot_ready = true;
        let events = driver.poll_routed_events().unwrap();
        assert_eq!(events.iter().map(|event| event.event.kind.as_str()).collect::<Vec<_>>(),
            ["SessionJoined", "GameSpeedChanged", "FullSnapshot", "LotLocalAuthorityModeChanged"]);
        assert_eq!(driver.player_conns.get(&7), Some(&11), "claim cleanup lost the deferred connection binding");
        assert!(driver.nav_dirty, "baseline completion did not schedule the new ownership table");
        assert!(!driver.baseline_pending(crate::ConnId(11)));
    }

    #[test]
    fn epoch_delivery_review_held_queue_limit_closes_and_counts_every_dropped_delivery() {
        use sim96_kernel_cleanroom::private_navigation::{Delivery, ExecutionLeg, MAX_OUTSTANDING};
        let limit = MAX_OUTSTANDING * 2 + 2;
        let mut driver = BridgeGameDriver::new(InstrumentBridge::default());
        driver.configure_rust_switches(true,false,true);
        driver.world.enable_private_navigation();
        driver.nav_epoch_pending = Some(1);
        let envelope = RustNavigationEnvelope { epoch:1,generation:1,corr:1,actor:2,
            start_mm:[0;3],goal_mm:[0;3],hash:0,total_cost_mm:0,
            legs:vec![ExecutionLeg { index:0,cell:0,kind:4,path_type:0,from_cm:[0;3],to_cm:[0;3],
                object:0,entry_slot:0,exit_slot:0 }] };
        driver.private_deliveries.push_back(Delivery { frame:NavigationFrame { id:1,
            message:NavigationMessage::Result { operation:5,status:NavigationStatus::Done,
                epoch:1,generation:1,data:envelope.encode().unwrap() } },
            presenters:vec!["Rt|1|2|1|0000000000000000|0".into()] });
        for id in 2..=limit as u64 {
            driver.private_deliveries.push_back(Delivery { frame:NavigationFrame { id,
                message:NavigationMessage::Result { operation:4,status:NavigationStatus::Done,
                    epoch:1,generation:1,data:vec![] } },presenters:vec![] });
        }
        driver.flush_private_navigation().unwrap();
        assert_eq!(driver.private_deliveries.len(),limit,"the full legal kernel batch fits");
        assert!(driver.world.private_navigation.enabled);
        assert_eq!(driver.world.private_navigation.counters.delivery_failed,0);
        driver.world.private_frame(NavigationFrame { id:1,
            message:NavigationMessage::Invalidate { epoch:1,generation:0 } });
        let error = driver.flush_private_navigation().unwrap_err();
        assert_eq!(error.0,"private delivery queue full");
        assert!(driver.private_deliveries.is_empty());
        assert!(!driver.world.private_navigation.enabled);
        assert_eq!(driver.world.private_navigation.counters.delivery_failed,(limit+1) as u64);
        assert!(driver.nav_epoch_bump);
        assert_eq!(driver.take_epoch_announcement(),None);
        assert!(driver.route_plans.is_empty());
        assert!(driver.pending_events.iter().all(|e| e.event.kind != "SimAnimation"));
        assert!(driver.endpoint.commands.is_empty(),"no held plan was offered");
    }

    #[test]
    fn card55_review_load_save_resets_only_accepted_world_state() {
        for refused in [false, true] {
            let mut driver = BridgeGameDriver::new(InstrumentBridge { refuse_load: refused, ..InstrumentBridge::default() });
            driver.rust_switches = (true, true);
            driver.poll_routed_events().unwrap();
            driver.route_plans.push_back(vec!["Rt|1|2|1|0000000000000001|0".into()]);
            let bytes = sim96_kernel_cleanroom::road_placement::compile_simroad_json(
                r#"{"world_key":1,"nodes":[],"lanes":[]}"#).unwrap();
            driver.world.load_road_set(&bytes).unwrap();
            driver.observed_tick = 500;
            let reply = driver.admin_request(&crate::admin::parse_request("LOAD_SAVE 46697874757265").unwrap()).unwrap();
            assert_eq!(serde_json::from_str::<serde_json::Value>(&reply).unwrap()["accepted"], !refused);
            assert_eq!(driver.route_plans.is_empty(), !refused);
            assert_eq!(driver.world.road_set.is_none(), !refused);
            assert_eq!(driver.observed_tick, if refused { 500 } else { 0 });
            assert_eq!(driver.switches_sent, refused);
            driver.poll_routed_events().unwrap();
            driver.poll_routed_events().unwrap();
            let switches = driver.endpoint.commands.iter().filter(|command| matches!(
                command, BridgeCommand::SacsRecorder { args, .. } if args[0].ends_with("-on"))).count();
            assert_eq!(switches, if refused { 2 } else { 4 });
            if !refused {
                driver.endpoint.events.push(b"EVENT|RoadPlaceRequest|1|0|0|0|0|0|1000|0|none".to_vec());
                driver.poll_routed_events().unwrap();
                assert!(matches!(driver.endpoint.commands.last().unwrap(),
                    BridgeCommand::SacsRecorder { args, .. } if args == &["rust-road-place-decline", "1", "RoadDataMissing"]));
            }
        }
    }

    #[test]
    fn card55_e2_routing_getter_is_read_only_and_projects_every_counter() {
        let mut driver = BridgeGameDriver::new(InstrumentBridge::default());
        driver.world.navigate_requests = 11;
        driver.world.plans_served = 7;
        driver.world.native_oracle_owned = Some(2);
        driver.world.road_place_requests = 13;
        driver.world.road_place_served = 5;
        driver.world.road_load_failures = 1;
        let hash = driver.world.state_hash();
        for _ in 0..2 {
            assert_eq!(driver.routing_state().as_deref(), Some(
                "navigate_requests=11|plans_served=7|native_oracle_owned=2|road_place_requests=13|road_place_served=5|road_load_failures=1"));
            assert_eq!(driver.world.state_hash(), hash);
            assert!(driver.endpoint.commands.is_empty());
        }
        assert_eq!(crate::FakeDriver::new().routing_state(), None);
    }

    #[test]
    fn card55_switches_are_sent_once_on_attach_pump_and_defaults_are_off() {
        for switches in [(false, false), (true, false), (false, true), (true, true)] {
            let mut driver = BridgeGameDriver::new(InstrumentBridge::default());
            driver.rust_switches = switches;
            driver.poll_routed_events().unwrap();
            driver.poll_routed_events().unwrap();
            let commands: Vec<_> = driver.endpoint.commands.iter().map(|command| match command {
                BridgeCommand::SacsRecorder { args, .. } => args[0].as_str(),
                _ => panic!("unexpected bridge command"),
            }).collect();
            let mut expected = Vec::new();
            if switches.0 { expected.push("rust-route-on"); }
            if switches.1 { expected.push("rust-road-place-on"); }
            assert_eq!(commands, expected);
        }
    }

    #[test]
    fn card55_diag_advances_the_navigation_deadline_clock_without_a_snapshot() {
        let mut driver = BridgeGameDriver::new(InstrumentBridge { tick: 500, ..InstrumentBridge::default() });
        driver.diag_state().unwrap();
        assert_eq!(driver.observed_tick, 500);
        driver.endpoint.tick = 400;
        driver.diag_state().unwrap();
        assert_eq!(driver.observed_tick, 500, "a late diagnostic must not rewind deadlines");
    }

    #[test]
    fn card55_stream_diag_advances_the_navigation_deadline_clock_without_a_snapshot() {
        let mut driver = BridgeGameDriver::new(InstrumentBridge { tick: 500, ..InstrumentBridge::default() });
        driver.start_stream_request(&StreamRequest::DiagState).unwrap();
        assert_eq!(driver.observed_tick, 500);
        driver.endpoint.tick = 400;
        driver.start_stream_request(&StreamRequest::DiagState).unwrap();
        assert_eq!(driver.observed_tick, 500, "a late stream diagnostic must not rewind deadlines");
    }

    #[test]
    fn card55_road_request_round_trip_all_selectors_none_and_named_refusals() {
        let mut driver = BridgeGameDriver::new(InstrumentBridge::default());
        driver.rust_switches = (false, false);
        let request = |driver: &mut BridgeGameDriver<InstrumentBridge>, corr: u64, kind: u8, y: i32, radius: i32| {
            driver.endpoint.events.push(format!("EVENT|RoadPlaceRequest|{corr}|18446744073709551615|0|{y}|0|{kind}|1000|{radius}|none").into_bytes());
            assert!(driver.poll_routed_events().unwrap().is_empty(), "trusted road event leaked to players");
            match driver.endpoint.commands.last().unwrap() {
                BridgeCommand::SacsRecorder { args, .. } => args.clone(),
                _ => panic!("missing road answer"),
            }
        };
        assert_eq!(request(&mut driver, 1, 0, 0, 50),
            ["rust-road-place-decline", "1", "RoadDataMissing"]);
        let lanes = [0, 1, 2, 3, 5].into_iter().map(|kind| format!(
            "{{\"id\":{},\"kind\":{kind},\"flags\":2,\"from\":1,\"to\":2,\"width_cm\":400,\"vertices_cm\":[[0,0,0],[2000,0,0]]}}", kind + 1))
            .collect::<Vec<_>>().join(",");
        let bytes = sim96_kernel_cleanroom::road_placement::compile_simroad_json(&format!(
            "{{\"world_key\":1,\"nodes\":[{{\"id\":1,\"position_cm\":[0,0,0]}},{{\"id\":2,\"position_cm\":[2000,0,0]}}],\"lanes\":[{lanes}]}}")).unwrap();
        driver.world.load_road_set(&bytes).unwrap();
        for kind in 0..=5 {
            let args = request(&mut driver, u64::from(kind) + 2, kind, 0, 50);
            assert_eq!(args[0], "rust-road-place");
            assert_eq!(&args[2..6], ["10", "0", "0", "0"]);
            assert!((args[6].parse::<f64>().unwrap() - std::f64::consts::FRAC_1_SQRT_2).abs() < 1e-15);
            assert_eq!(args[7], "0");
            assert!((args[8].parse::<f64>().unwrap() - std::f64::consts::FRAC_1_SQRT_2).abs() < 1e-15);
            assert_eq!(args[10], "0");
            assert_eq!(args[11].len(), 16);
        }
        assert_eq!(request(&mut driver, 8, 6, 0, 50), ["rust-road-place-decline", "8", "LaneKindUnsupported"]);
        assert_eq!(request(&mut driver, 9, 0, 1000, 50), ["rust-road-place-none", "9", "f84841652498787d"]);
        assert_eq!(request(&mut driver, 10, 0, 0, 100001), ["rust-road-place-decline", "10", "BadRequest"]);
        assert_eq!((driver.world.road_place_requests, driver.world.road_place_served), (10, 7));
        let commands = driver.endpoint.commands.len();
        assert!(driver.adjudicate(&Command { corr: 11, command_id: 11, player_id: 1,
            verb: "PlaceOnRoadOffScreen".into(), args: vec![] }).unwrap().starts_with(b"ERR|11|bad_line|"));
        assert_eq!(driver.endpoint.commands.len(), commands, "road requests became a public verb");
    }

    #[test]
    fn terminal_exit_refuses_world_transfer_before_first_file_and_after_partial_delivery() {
        use crate::world_save::{WorldSave, WorldSaveFile};
        for partial in [false, true] {
            let mut driver = BridgeGameDriver::new(OfflineBridge);
            let cmd = Command { corr: 42, command_id: 142, player_id: 7,
                verb: "RequestWorld".into(), args: vec![] };
            assert!(driver.adjudicate_for(crate::ConnId(77), &cmd).unwrap().starts_with(b"ACK|"));
            driver.start_checkpoint().unwrap();
            let job = driver.checkpoint_job.take().unwrap();
            driver.world_events(&job, Arc::new(WorldSave { save_name: "Fixture".into(), files: vec![
                WorldSaveFile { name: "first.nhd".into(), data: vec![1] },
                WorldSaveFile { name: "second.data".into(), data: vec![2] },
            ] }));
            if partial {
                let deadline = Instant::now() + Duration::from_secs(10);
                loop {
                    assert!(Instant::now() < deadline, "world worker did not publish its first file");
                    let events = driver.poll_routed_events().unwrap();
                    if events.iter().any(|event| event.event.kind == "WorldFile") { break; }
                    std::thread::yield_now();
                }
            }
            let events = driver.terminal_events(&driver_error("process_exited"));
            let refused: Vec<_> = events.iter().filter(|event| event.event.kind == "CommandRejected").collect();
            assert_eq!(refused.len(), 1, "unfinished transfer must keep its request identity; partial={partial}");
            assert_eq!(refused[0].requester, Some(crate::ConnId(77)));
            assert_eq!(refused[0].event.corr, 42);
            assert_eq!(refused[0].event.fields, ["142", &m::hex_encode("process_exited")]);
            assert!(driver.terminal_events(&driver_error("process_exited")).is_empty());
            assert!(driver.world_transfers.is_empty());
        }
    }

    #[test]
    fn m1_release_player_removes_kernel_client_role() {
        let mut driver = BridgeGameDriver::new(OfflineBridge);
        let cmd = Command {
            corr: 1, command_id: 1, player_id: 7,
            verb: "SetClientRole".into(), args: vec!["Client".into()],
        };
        driver.adjudicate(&cmd).unwrap();
        let player = driver.players[&7];
        assert_eq!(driver.world.client_roles.get(&player.0).map(String::as_str), Some("presenter"));
        driver.release_player(7);
        assert!(!driver.world.client_roles.contains_key(&player.0));
    }
}
