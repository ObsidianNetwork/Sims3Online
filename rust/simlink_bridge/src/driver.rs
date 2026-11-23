//! The slot and bulk driver (card 56 §B.3, §C, §E, §F; the surface `TRAIT_CONTRACT.md` "Card 56" names):
//! `ProcessMemoryBridge.cs` in Rust, over any [`GuestMemory`]. It is the one module a layout v12 would
//! change (§I item 4, the seam left for the overhaul track).
//!
//! The protocol order is expressed once, here: a command frame is one write of `[sender | seq | length |
//! payload]` (the oracle's `WriteFrame`: the stamp at the lowest address, then the sequence), a consumed
//! frame is the `CONSUMED_SENDER_ID` stamp, and there is no public way to set a sequence or a stamp by itself
//! (§C.1).
#![forbid(unsafe_code)]

use std::collections::VecDeque;
use std::time::{Duration, Instant};

use crate::crc32::crc32;
use crate::discovery;
use crate::error::BridgeError;
use crate::ffi::ProcessAttach;
use crate::layout::{
    bulk_marker, bulk_region_base_offset, control_marker, BulkSlot, BulkSlotHeader, ControlRegionHeader,
    ControlSlot, ControlSlotHeader, BRIDGE_WIRE_PROTOCOL_VERSION, BULK_SLOT_COUNT, BULK_SLOT_HEADER_SIZE,
    BULK_SLOT_PAYLOAD_MAX, BULK_STATE_CTL_CONSUMED, BULK_STATE_CTL_READY, BULK_STATE_CTL_WRITING,
    BULK_STATE_EMPTY, BULK_STATE_HOST_CONSUMED, BULK_STATE_HOST_READY, COMPAT_SLOT_OFFSET, CONSUMED_SENDER_ID,
    CONTROL_SLOT_COUNT, CTL_SLOT_COUNT, HELLO_WIRE_VERSION, HOST_SENDER_ID, HOST_SLOT_COUNT,
    HOST_WRITING_SENDER_ID, LAYOUT_VERSION, LAYOUT_VERSION_OFFSET, MARKER_LEN, SERVER_SENDER_ID, SLOT_HEADER_SIZE,
    SLOT_PAYLOAD_MAX, SLOT_SIZE, SNAPSHOT2_MAGIC, SNAPSHOT2_VERSION_MAX, SNAPSHOT2_VERSION_MIN,
};
use crate::memory::{guest_addr, RegionInfo, GuestMemory};
use crate::reply::{accept_connect_response, caps_indicate_layout_v11, is_plausible_host_frame_line, parse_hello, Hello};
use crate::text::Guid;
use crate::verbs::{encode, refused_line_verb, BridgeCommand};

/// How the driver waits: the oracle sleeps 1 ms in its mid-write spins and 10 ms between connect polls.
/// Tests pass a pacer that steps a simulated mod instead of sleeping.
pub trait Pacer {
    fn pause(&mut self, ms: u64);
}

/// `std::thread::sleep`.
pub struct SleepPacer;

impl Pacer for SleepPacer {
    fn pause(&mut self, ms: u64) {
        std::thread::sleep(Duration::from_millis(ms));
    }
}

/// A sequence seed in `1024..i32::MAX/2`, as the oracle draws it (`ProcessMemoryBridge.cs:50-54`,).
pub fn random_sequence_seed() -> i32 {
    use std::hash::{BuildHasher, Hasher};
    let mut h = std::collections::hash_map::RandomState::new().build_hasher();
    h.write_u128(std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map_or(0, |d| d.as_nanos()));
    let span = (i32::MAX / 2 - 1024) as u64;
    1024 + (h.finish() % span) as i32
}

/// The CONNECT line (`ConnectAsync`).
pub fn connect_line() -> String {
    format!("CONNECT|00000000000000000000000000000000|{BRIDGE_WIRE_PROTOCOL_VERSION}")
}

/// Discovery on a live process: the region base, or `region_not_found` once `deadline` (a timeout from
/// now: 30 s Sandboxie, 60 s Wine, `HeadlessSims3Host.cs:12-15`) passes, or `process_exited`.
pub fn discover(p: &ProcessAttach, deadline: Duration) -> Result<u32, BridgeError> {
    discovery::find_region(p, Instant::now() + deadline)
}

/// The CONNECT reply.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HelloReply {
    /// The line as the mod wrote it.
    pub line: String,
    /// Its fields, when `TryParseHello` reads them (a bare `HELLO|6|...` the oracle also accepts may not parse).
    pub hello: Option<Hello>,
}

/// A host bulk payload, classified (§C.5: this card owns the frame, card 58 the cadence and the body decode).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum BulkPayload {
    /// The first four bytes are `SNAPSHOT2_MAGIC` (little-endian) and there are at least 8: a binary snapshot.
    Snapshot2 { version: u32, bytes: Vec<u8> },
    /// Any other payload is UTF-8 text (`SNAPSHOT|...` or a response).
    Text(String),
}

impl BulkPayload {
    /// `Snapshot2BinaryReader.IsSnapshot2`, then the text fallback (`HostBridgeServer.cs:607`).
    pub fn classify(payload: Vec<u8>) -> Self {
        if payload.len() >= 8 && u32::from_le_bytes([payload[0], payload[1], payload[2], payload[3]]) == SNAPSHOT2_MAGIC {
            let version = u32::from_le_bytes([payload[4], payload[5], payload[6], payload[7]]);
            return Self::Snapshot2 { version, bytes: payload };
        }
        Self::Text(String::from_utf8_lossy(&payload).into_owned())
    }

    /// The `SNAPSHOT2_VERSION_MIN..=SNAPSHOT2_VERSION_MAX` check the reader applies.
    pub fn version_supported(&self) -> bool {
        matches!(self, Self::Snapshot2 { version, .. } if (SNAPSHOT2_VERSION_MIN..=SNAPSHOT2_VERSION_MAX).contains(version))
    }
}

/// Maximum commands retained locally while the native reader cannot advance.
/// Published frames occupy the fixed native slot band separately.
pub const MAX_PENDING_COMMANDS: usize = 256;

/// One attached bridge region.
pub struct BridgeDriver<M: GuestMemory = ProcessAttach, P: Pacer = SleepPacer> {
    mem: M,
    pacer: P,
    base: u32,
    sequence: i32,
    layout_v11: bool,
    layout_v11_from_hello_caps: bool,
    ctl_write_cursor: usize,
    /// A host frame's sequence proves the mod has dequeued through that command.
    ctl_read_sequence: i32,
    pending_commands: VecDeque<QueuedCommand>,
    in_flight: [Option<(i32, Guid)>; CTL_SLOT_COUNT + 1],
    failure: Option<BridgeError>,
    connect_timeout: Duration,
    /// Host lines a command write displaced (the oracle hands them back to its caller); `poll_line` returns
    /// them first, so none is lost.
    displaced: VecDeque<String>,
    /// Card 56 gate 6: every command frame written and host frame consumed, in the controller's
    /// `--bridge-record` form ([`crate::record`]).
    recorder: Option<FrameRecorder>,
}

/// A sink for [`crate::record`] frames (`ctl->host seq=N slot=S <line>`, `host->ctl seq=N <line>`).
pub type FrameRecorder = Box<dyn FnMut(&str) + Send>;

/// The region memory [`BridgeDriver::memory`] hands out: reads only.
///
/// The type exists to keep writes inside the driver. Every command line goes through
/// [`BridgeDriver::send_line`], [`BridgeDriver::send_compat_line`] or [`BridgeDriver::send_command`], where
/// card 56 §E.3 refuses `APPLY_INTERACTION_QUEUE`, `APPLY_INTERACTIONS` and `APPLY_FORCE_ALIGN` before
/// anything is read, consumed or written. [`GuestMemory::write`] takes `&self`, so a `&M` given out to any
/// caller would let safe code outside this crate publish a frame that never met the refusal. Inspection
/// (a test's peek, a caller's own read of a stamp) stays available.
pub struct GuestMemoryView<'a, M: GuestMemory>(&'a M);

impl<M: GuestMemory> GuestMemoryView<'_, M> {
    /// Fill `buf` from `addr` ([`GuestMemory::read`]).
    pub fn read(&self, addr: u32, buf: &mut [u8]) -> Result<(), BridgeError> {
        self.0.read(addr, buf)
    }

    /// The `u32` at `addr` ([`GuestMemory::read_u32`]).
    pub fn read_u32(&self, addr: u32) -> Result<u32, BridgeError> {
        self.0.read_u32(addr)
    }

    /// The `u64` at `addr` ([`GuestMemory::read_u64`]).
    pub fn read_u64(&self, addr: u32) -> Result<u64, BridgeError> {
        self.0.read_u64(addr)
    }

    /// The target's mappings ([`GuestMemory::regions`]).
    pub fn regions(&self) -> Result<Vec<RegionInfo>, BridgeError> {
        self.0.regions()
    }

    /// True once the target has exited ([`GuestMemory::has_exited`]).
    pub fn has_exited(&self) -> bool {
        self.0.has_exited()
    }
}

#[derive(Debug, Clone, Copy)]
struct Target {
    offset: usize,
    ctl_index: usize,
}

struct QueuedCommand {
    sequence: i32,
    line: String,
    compat: bool,
}

/// A read failure the oracle treats as "nothing there" (`false`); a process exit stays an error.
fn absent_unless_exited(e: BridgeError) -> Result<(), BridgeError> {
    match e {
        BridgeError::ProcessExited => Err(e),
        _ => Ok(()),
    }
}

impl BridgeDriver<ProcessAttach, SleepPacer> {
    /// Attach to a live process: open it, discover the region, CONNECT, wait for HELLO, all within `timeout`.
    pub fn attach_pid(pid: u32, timeout: Duration) -> Result<(Self, HelloReply), BridgeError> {
        Self::attach(ProcessAttach::open(pid)?, timeout, SleepPacer)
    }
}

impl<M: GuestMemory, P: Pacer> BridgeDriver<M, P> {
    /// A driver for the region at `base`, with `seed` as its sequence before the first `++`.
    pub fn new(mem: M, base: u32, seed: i32, pacer: P) -> Self {
        Self {
            mem,
            pacer,
            base,
            sequence: seed,
            layout_v11: false,
            layout_v11_from_hello_caps: false,
            ctl_write_cursor: 0,
            ctl_read_sequence: seed,
            pending_commands: VecDeque::new(),
            in_flight: [None; CTL_SLOT_COUNT + 1],
            failure: None,
            connect_timeout: Duration::from_secs(30),
            displaced: VecDeque::new(),
            recorder: None,
        }
    }

    /// Record every command frame this driver writes and every host frame it consumes from now on, as the
    /// controller's `FrameRecord` does (`ProcessMemoryBridge.cs:42-46`): CONNECT and refused verbs are not frames.
    pub fn set_frame_recorder(&mut self, recorder: FrameRecorder) {
        self.recorder = Some(recorder);
    }

    fn record(&mut self, frame: &str) {
        if let Some(recorder) = self.recorder.as_mut() {
            recorder(frame);
        }
    }

    /// Discover the region, seed the sequence above the region's maximum, CONNECT and wait for HELLO, all
    /// within `timeout` (`ProcessMemoryBridge.ConnectAsync`).
    pub fn attach(mem: M, timeout: Duration, pacer: P) -> Result<(Self, HelloReply), BridgeError> {
        let deadline = Instant::now() + timeout;
        let base = discovery::find_region(&mem, deadline)?;
        let mut driver = Self::new(mem, base, random_sequence_seed(), pacer);
        driver.seed_sequence_above_region_max();
        driver.connect_timeout = deadline.saturating_duration_since(Instant::now());
        let hello = driver.connect()?;
        Ok((driver, hello))
    }

    pub fn base(&self) -> u32 {
        self.base
    }

    pub fn sequence(&self) -> i32 {
        self.sequence
    }

    pub fn layout_v11(&self) -> bool {
        self.layout_v11
    }

    /// A read-only view of the region's memory (review N4): [`GuestMemoryView`].
    ///
    /// Not a command path. A line written straight through the raw memory skips the §E.3 refusal in
    /// [`BridgeDriver::send_line`] and its siblings, so the driver keeps `&M` to itself and hands out reads
    /// only. A caller that needs the live process's memory as such (card 57's `GuestMem32` adapter) holds
    /// its own [`crate::ffi::ProcessAttach`], never one taken from a driver.
    ///
    /// ```compile_fail
    /// use simlink_bridge::driver::{BridgeDriver, Pacer};
    /// use simlink_bridge::SimMemory;
    ///
    /// struct Idle;
    /// impl Pacer for Idle {
    ///     fn pause(&mut self, _ms: u64) {}
    /// }
    ///
    /// // Refused by the compiler: the driver's memory cannot be written through.
    /// let mem = SimMemory::new();
    /// let driver = BridgeDriver::new(&mem, 0x0100_0000, 1, Idle);
    /// driver.memory().write(0x0100_0000, b"APPLY_FORCE_ALIGN|00000000000000000000000000000000|42").unwrap();
    /// ```
    pub fn memory(&self) -> GuestMemoryView<'_, M> {
        GuestMemoryView(&self.mem)
    }

    pub fn pacer_mut(&mut self) -> &mut P {
        &mut self.pacer
    }

    /// How long `connect` waits for HELLO (default 30 s).
    pub fn set_connect_timeout(&mut self, timeout: Duration) {
        self.connect_timeout = timeout;
    }

    // ---- raw slot access ----

    fn addr(&self, offset: usize) -> Result<u32, BridgeError> {
        guest_addr(self.base, offset)
    }

    /// The u64 stamp at `offset`, `None` when it cannot be read (the oracle's `TryReadSenderId` false).
    fn read_stamp(&self, offset: usize) -> Result<Option<u64>, BridgeError> {
        match self.mem.read_u64(self.addr(offset)?) {
            Ok(v) => Ok(Some(v)),
            Err(e) => absent_unless_exited(e).map(|()| None),
        }
    }

    fn read_bytes(&self, offset: usize, len: usize) -> Result<Option<Vec<u8>>, BridgeError> {
        let mut buf = vec![0u8; len];
        match self.mem.read(self.addr(offset)?, &mut buf) {
            Ok(()) => Ok(Some(buf)),
            Err(e) => absent_unless_exited(e).map(|()| None),
        }
    }

    fn write_bytes(&self, offset: usize, data: &[u8]) -> Result<(), BridgeError> {
        self.mem.write(self.addr(offset)?, data)
    }

    fn mark_consumed(&self, offset: usize) -> Result<(), BridgeError> {
        self.write_bytes(offset, &CONSUMED_SENDER_ID.to_le_bytes())
    }

    /// `WriteFrame`: one write of the whole frame; the compat slot writes only header plus payload, so the v11
    /// control header at bytes 32-63 survives, then gets repaired.
    fn write_frame(&self, offset: usize, sequence: i32, sender: u64, line: &str) -> Result<(), BridgeError> {
        let payload = line.as_bytes();
        if payload.len() > SLOT_PAYLOAD_MAX {
            return Err(BridgeError::FrameTooLarge { len: payload.len() });
        }
        let write_len = if offset == COMPAT_SLOT_OFFSET { SLOT_HEADER_SIZE + payload.len() } else { SLOT_SIZE };
        let mut frame = vec![0u8; write_len];
        let header = ControlSlotHeader { sender_id: sender, seq: sequence, length: payload.len() as i32 };
        frame[..SLOT_HEADER_SIZE].copy_from_slice(&header.to_bytes());
        frame[SLOT_HEADER_SIZE..SLOT_HEADER_SIZE + payload.len()].copy_from_slice(payload);
        self.write_bytes(offset, &frame)?;
        if offset == COMPAT_SLOT_OFFSET && self.layout_v11 {
            self.repair_v11_control_header()?;
        }
        Ok(())
    }

    /// `RepairV11ControlHeaderAfterCompatWrite`: rewrite bytes 32-63.
    fn repair_v11_control_header(&self) -> Result<(), BridgeError> {
        let header = ControlRegionHeader::v11().to_bytes();
        self.write_bytes(LAYOUT_VERSION_OFFSET, &header[LAYOUT_VERSION_OFFSET..])
    }

    /// A host frame at `offset`: `(seq, line)` when it is stamped `HOST_SENDER_ID` with a valid length.
    fn read_host_frame_at(&self, offset: usize) -> Result<Option<(i32, String)>, BridgeError> {
        let Some(frame) = self.read_bytes(offset, SLOT_SIZE)? else { return Ok(None) };
        let Some(h) = ControlSlotHeader::from_bytes(&frame) else { return Ok(None) };
        if h.sender_id != HOST_SENDER_ID || h.length <= 0 || h.length as usize > SLOT_PAYLOAD_MAX {
            return Ok(None);
        }
        let len = h.length as usize;
        let line = String::from_utf8_lossy(&frame[SLOT_HEADER_SIZE..SLOT_HEADER_SIZE + len]).into_owned();
        Ok(Some((h.seq, line)))
    }

    /// `TryReadFrame`: a host frame of exactly `expected` sequence.
    fn read_frame(&self, offset: usize, expected: i32) -> Result<Option<String>, BridgeError> {
        Ok(self.read_host_frame_at(offset)?.filter(|(seq, _)| *seq == expected).map(|(_, line)| line))
    }

    fn host_offsets() -> impl Iterator<Item = usize> {
        (0..HOST_SLOT_COUNT).filter_map(|i| ControlSlot::host(i).ok()).map(ControlSlot::byte_offset)
    }

    fn ctl_offset(index_in_band: usize) -> usize {
        ControlSlot::ctl(index_in_band % CTL_SLOT_COUNT).map(ControlSlot::byte_offset).unwrap_or(0)
    }

    // ---- layout mode ----

    /// `TryReadLayoutVersion`, including the bulk-marker inference for a stomped control header.
    fn read_layout_version(&self) -> Result<u32, BridgeError> {
        let version = self.mem.read_u32(self.addr(LAYOUT_VERSION_OFFSET)?)?;
        if version == LAYOUT_VERSION {
            return Ok(version);
        }
        if self.read_bytes(bulk_region_base_offset(), MARKER_LEN)?.is_some_and(|m| m[..] == bulk_marker()) {
            return Ok(LAYOUT_VERSION);
        }
        Ok(version)
    }

    /// `RefreshLayoutMode`.
    fn refresh_layout_mode(&mut self) -> Result<(), BridgeError> {
        if self.layout_v11_from_hello_caps {
            self.layout_v11 = true;
            return Ok(());
        }
        self.layout_v11 = self.read_layout_version()? == LAYOUT_VERSION;
        if self.layout_v11 {
            self.ctl_write_cursor = 0;
        }
        Ok(())
    }

    /// `ApplyConnectHello`: a HELLO|6+ whose caps name layout 11 (or multi-slot) locks ctl routing.
    pub fn apply_connect_hello(&mut self, hello_line: &str) -> Result<(), BridgeError> {
        if let Ok(Some(h)) = parse_hello(hello_line) {
            if h.wire_version >= HELLO_WIRE_VERSION as i32 && caps_indicate_layout_v11(h.caps.as_deref()) {
                self.layout_v11_from_hello_caps = true;
                self.layout_v11 = true;
                self.ctl_write_cursor = 0;
                return Ok(());
            }
        }
        self.refresh_layout_mode()
    }

    // ---- connect ----

    /// `SeedSequenceAboveRegionMax`: start above every sequence visible in the region (the mod's dedupe
    /// cursor survives controller restarts).
    pub fn seed_sequence_above_region_max(&mut self) {
        let mut max_observed = 0i32;
        let offsets = core::iter::once(COMPAT_SLOT_OFFSET)
            .chain((0..CONTROL_SLOT_COUNT).filter_map(crate::layout::slot_byte_offset));
        for offset in offsets {
            let Ok(Some(header)) = self.read_bytes(offset, 12) else { continue };
            let sender = u64::from_le_bytes(header[..8].try_into().unwrap_or([0; 8]));
            if ![HOST_SENDER_ID, SERVER_SENDER_ID, CONSUMED_SENDER_ID, HOST_WRITING_SENDER_ID].contains(&sender) {
                continue;
            }
            let seq = i32::from_le_bytes(header[8..12].try_into().unwrap_or([0; 4]));
            max_observed = max_observed.max(seq);
        }
        if max_observed <= 0 || self.sequence > max_observed {
            return;
        }
        let seeded = i64::from(max_observed) + 1024;
        if seeded >= i64::from(i32::MAX) - 1024 {
            return;
        }
        self.sequence = seeded as i32;
        // Seeding belongs before CONNECT. Never acknowledge live submissions
        // if an existing caller invokes it during an exchange.
        if self.pending_commands.is_empty() && self.in_flight.iter().all(Option::is_none) {
            self.ctl_read_sequence = self.sequence;
        }
    }

    fn clear_stale_server_command_frames(&self) -> Result<(), BridgeError> {
        for i in 0..CTL_SLOT_COUNT {
            let offset = Self::ctl_offset(i);
            if self.read_stamp(offset)? == Some(SERVER_SENDER_ID) {
                self.mark_consumed(offset)?;
            }
        }
        Ok(())
    }

    fn clear_stale_host_frames(&self) -> Result<(), BridgeError> {
        for offset in Self::host_offsets() {
            if matches!(self.read_stamp(offset)?, Some(HOST_SENDER_ID) | Some(HOST_WRITING_SENDER_ID)) {
                self.mark_consumed(offset)?;
            }
        }
        Ok(())
    }

    /// Write CONNECT on the compat slot after clearing the prior session's frames (`SendRawAsync`, first half).
    pub fn begin_connect(&mut self) -> Result<(), BridgeError> {
        let sequence = self.sequence.checked_add(1).ok_or(BridgeError::SequenceExhausted)?;
        self.clear_stale_server_command_frames()?;
        self.clear_stale_host_frames()?;
        self.pending_commands.clear();
        self.in_flight.fill(None);
        self.failure = None;
        self.ctl_read_sequence = self.sequence;
        self.sequence = sequence;
        self.write_frame(COMPAT_SLOT_OFFSET, self.sequence, SERVER_SENDER_ID, &connect_line())
    }

    /// One poll for the HELLO (`SendRawAsync` loop body, then `ApplyConnectHello` and the orphan drain on
    /// success). `Ok(None)` until it arrives.
    pub fn poll_connect(&mut self) -> Result<Option<HelloReply>, BridgeError> {
        if self.mem.has_exited() {
            return Err(BridgeError::ProcessExited);
        }
        while let Some(response) = self.try_consume_connect_response()? {
            // The oracle's handshake check throws out of ConnectAsync on a malformed HELLO.
            if accept_connect_response(&response).map_err(|_| BridgeError::MalformedHello)? {
                self.refresh_layout_mode()?;
                self.apply_connect_hello(&response)?;
                self.drain_orphan_host_frames_after_connect()?;
                self.ctl_read_sequence = self.sequence;
                let hello = parse_hello(&response).ok().flatten();
                return Ok(Some(HelloReply { line: response, hello }));
            }
        }
        Ok(None)
    }

    /// `CONNECT|<32 zeros>|23`, then a poll every 10 ms until `HELLO|6|<caps>` or the connect timeout
    /// (`region_not_found`).
    pub fn connect(&mut self) -> Result<HelloReply, BridgeError> {
        let deadline = Instant::now() + self.connect_timeout;
        self.begin_connect()?;
        loop {
            if let Some(hello) = self.poll_connect()? {
                return Ok(hello);
            }
            if Instant::now() >= deadline {
                return Err(BridgeError::RegionNotFound);
            }
            self.pacer.pause(10);
        }
    }

    /// `TryConsumeConnectResponse`: a host frame of the current sequence on compat, else on a host slot.
    fn try_consume_connect_response(&mut self) -> Result<Option<String>, BridgeError> {
        let sequence = self.sequence;
        if sequence <= 0 {
            return Ok(None);
        }
        if let Some(line) = self.read_frame(COMPAT_SLOT_OFFSET, sequence)? {
            self.mark_consumed(COMPAT_SLOT_OFFSET)?;
            return Ok(Some(line));
        }
        for offset in Self::host_offsets() {
            if let Some(line) = self.read_frame(offset, sequence)? {
                self.mark_consumed(offset)?;
                return Ok(Some(line));
            }
        }
        Ok(None)
    }

    /// `DrainOrphanHostFramesAfterConnect`.
    fn drain_orphan_host_frames_after_connect(&mut self) -> Result<(), BridgeError> {
        let sequence = self.sequence;
        for _ in 0..=HOST_SLOT_COUNT {
            let mut consumed_any = false;
            for offset in Self::host_offsets() {
                if self.read_frame(offset, sequence)?.is_some() {
                    self.mark_consumed(offset)?;
                    consumed_any = true;
                }
            }
            if !consumed_any {
                break;
            }
        }
        Ok(())
    }

    // ---- lines ----

    fn can_write_ctl_slot(&self, offset: usize) -> Result<bool, BridgeError> {
        Ok(matches!(self.mem.read_u64(self.addr(offset)?)?, 0 | CONSUMED_SENDER_ID | SERVER_SENDER_ID))
    }

    fn command_header(&self, offset: usize) -> Result<ControlSlotHeader, BridgeError> {
        let mut bytes = [0; SLOT_HEADER_SIZE];
        self.mem.read(self.addr(offset)?, &mut bytes)?;
        Ok(ControlSlotHeader::from_bytes(&bytes).expect("complete fixed-size header"))
    }

    /// The mod scans low slots first and keeps SERVER stamps after dequeue
    /// (BridgeMemoryBuffer.cs:155-223). A host sequence is its dequeue watermark
    /// (:463-465); until then, append only ABOVE every possibly unread command.
    /// At wrap, queue locally instead of overtaking or overwriting an older frame.
    fn resolve_command_target(&self, compat: bool) -> Result<Option<Target>, BridgeError> {
        if !self.layout_v11 {
            return Ok(Some(Target { offset: COMPAT_SLOT_OFFSET, ctl_index: 0 }));
        }
        let mut first_writable = 0;
        for i in 0..CTL_SLOT_COUNT {
            let header = self.command_header(Self::ctl_offset(i))?;
            if header.sender_id == SERVER_SENDER_ID && header.seq > self.ctl_read_sequence {
                first_writable = i + 1;
            }
        }
        let header = self.command_header(COMPAT_SLOT_OFFSET)?;
        if header.sender_id == SERVER_SENDER_ID && header.seq > self.ctl_read_sequence {
            return Ok(None);
        }
        // Compat is scanned before every ctl slot, so it must also wait.
        if compat {
            return Ok((first_writable == 0).then_some(Target { offset: COMPAT_SLOT_OFFSET, ctl_index: 0 }));
        }
        for attempt in 0..CTL_SLOT_COUNT {
            let ctl_index = (self.ctl_write_cursor + attempt) % CTL_SLOT_COUNT;
            let offset = Self::ctl_offset(ctl_index);
            if ctl_index >= first_writable && self.can_write_ctl_slot(offset)? {
                return Ok(Some(Target { offset, ctl_index }));
            }
        }
        // Preserve the oracle's non-wrap fallback for nonstandard stamps, but
        // never fall back below an older possibly unread frame.
        Ok((self.ctl_write_cursor >= first_writable)
            .then_some(Target { offset: Self::ctl_offset(self.ctl_write_cursor), ctl_index: self.ctl_write_cursor }))
    }

    fn queue_command(&mut self, line: &str, compat: bool) -> Result<(), BridgeError> {
        if let Some(wire) = refused_line_verb(line) {
            return Err(BridgeError::RefusedLegacyVerb(wire));
        }
        if line.len() > SLOT_PAYLOAD_MAX {
            return Err(BridgeError::FrameTooLarge { len: line.len() });
        }
        self.refresh_layout_mode()?;
        let sequence = self.sequence.checked_add(1).ok_or(BridgeError::SequenceExhausted)?;
        self.flush_commands()?;
        if self.pending_commands.len() >= MAX_PENDING_COMMANDS {
            return Err(BridgeError::CommandQueueFull { limit: MAX_PENDING_COMMANDS });
        }
        // Read failures must reach this submitter before accepting its command.
        self.resolve_command_target(compat)?;
        self.sequence = sequence;
        self.pending_commands.push_back(QueuedCommand { sequence: self.sequence, line: line.to_owned(), compat });
        match self.flush_commands() {
            Err(BridgeError::CommandWriteFailed { sequence: failed, cause, .. }) if failed == sequence => Err(*cause),
            result => result,
        }
    }

    fn flush_commands(&mut self) -> Result<(), BridgeError> {
        if let Some(error) = &self.failure { return Err(error.clone()); }
        while let Some(command) = self.pending_commands.front() {
            let Some(target) = self.resolve_command_target(command.compat)? else { break; };
            // A write error can occur after publication: never retry this frame.
            let command = self.pending_commands.pop_front().expect("queued command");
            if let Err(cause) = self.write_command_at(&command.line, target, self.layout_v11 && !command.compat, command.sequence) {
                let error = BridgeError::CommandWriteFailed {
                    sequence: command.sequence,
                    correlation: command.line.split('|').nth(1).map(str::to_owned),
                    cause: Box::new(cause),
                };
                self.failure = Some(error.clone());
                return Err(error);
            }
        }
        Ok(())
    }

    /// `WaitWhileHostMidWrite`: up to 250 pauses of 1 ms while the slot holds the mod's mid-write stamp.
    fn wait_while_host_mid_write(&mut self, offset: usize) -> Result<(), BridgeError> {
        for _ in 0..250 {
            if self.read_stamp(offset)? != Some(HOST_WRITING_SENDER_ID) {
                return Ok(());
            }
            self.pacer.pause(1);
        }
        Ok(())
    }

    /// `TryPeekDisplacedHostLine`: an unread host frame the command write would displace; corrupt ones are
    /// consumed and skipped.
    fn peek_displaced_host_line(&self) -> Result<Option<String>, BridgeError> {
        loop {
            let found = if self.layout_v11 {
                let mut hit = None;
                for offset in Self::host_offsets() {
                    if let Some((_, line)) = self.read_host_frame_at(offset)? {
                        hit = Some((offset, line));
                        break;
                    }
                }
                hit
            } else {
                self.read_host_frame_at(COMPAT_SLOT_OFFSET)?.map(|(_, line)| (COMPAT_SLOT_OFFSET, line))
            };
            let Some((offset, line)) = found else { return Ok(None) };
            if is_plausible_host_frame_line(&line) {
                return Ok(Some(line));
            }
            self.mark_consumed(offset)?;
        }
    }

    /// Publish an already validated, sequenced command from the FIFO.
    fn write_command_at(&mut self, line: &str, target: Target, advance: bool, sequence: i32) -> Result<(), BridgeError> {
        let mut attempt = 0;
        loop {
            self.wait_while_host_mid_write(target.offset)?;
            if attempt < 50 && self.read_stamp(target.offset)? == Some(HOST_WRITING_SENDER_ID) {
                attempt += 1;
                self.pacer.pause(1);
                continue;
            }
            if line.len() > SLOT_PAYLOAD_MAX {
                return Err(BridgeError::FrameTooLarge { len: line.len() });
            }
            if let Some(displaced) = self.peek_displaced_host_line()? {
                self.displaced.push_back(displaced);
            }
            self.write_frame(target.offset, sequence, SERVER_SENDER_ID, line)?;
            let index = if target.offset == COMPAT_SLOT_OFFSET { CTL_SLOT_COUNT } else { target.ctl_index };
            self.in_flight[index] = line.split('|').nth(1).and_then(Guid::parse_wire).map(|corr| (sequence, corr));
            self.record(&crate::record::ctl_frame(sequence, target.offset, line));
            if advance {
                self.ctl_write_cursor = (target.ctl_index + 1) % CTL_SLOT_COUNT;
            }
            return Ok(());
        }
    }

    /// Sequence a command and publish it when physical scan order permits (v11),
    /// otherwise queue it until [`BridgeDriver::poll_line`] observes mod progress.
    /// A host line it displaces is kept for `poll_line`. The §E.3 verbs are refused
    /// before any memory access or queue change.
    ///
    /// **Not retry-safe.** On the compat slot of a v11 region, the frame is published before the control
    /// header is repaired; if that repair write fails, the `Err` comes back with the command already
    /// written and the sequence advanced (the oracle ignores that write and reports success). A caller must
    /// not resend on `Err`; it should treat the bridge as broken and re-attach.
    /// Queue-full rejection spends no sequence. Deferred write errors identify
    /// the original command in `CommandWriteFailed`; the session then requires
    /// reconnect. A directly attempted write retains its original error type.
    pub fn send_line(&mut self, line: &str) -> Result<(), BridgeError> {
        self.queue_command(line, false)
    }

    /// `encode` then [`BridgeDriver::send_line`]: the typed path, refused for the §E.3 verbs (typed or `Raw`).
    pub fn send_command(&mut self, cmd: &BridgeCommand) -> Result<(), BridgeError> {
        let line = encode(cmd)?;
        self.send_line(&line)
    }

    /// `WriteCompatCommand`: always the compat slot (the beacon-refuting PING). The §E.3 verbs are refused.
    /// Not retry-safe, as [`BridgeDriver::send_line`].
    pub fn send_compat_line(&mut self, line: &str) -> Result<(), BridgeError> {
        self.queue_command(line, true)
    }

    /// The next host line: a displaced one first, else `TryConsumeHostLine` (read, then the consumed stamp).
    pub fn poll_line(&mut self) -> Result<Option<String>, BridgeError> {
        if let Some(line) = self.displaced.pop_front() {
            return Ok(Some(line));
        }
        self.flush_commands()?;
        self.refresh_layout_mode()?;
        if self.sequence <= 0 {
            return Ok(None);
        }
        let offsets: Vec<usize> =
            if self.layout_v11 { Self::host_offsets().collect() } else { vec![COMPAT_SLOT_OFFSET] };
        for offset in offsets {
            let Some((seq, line)) = self.read_host_frame_at(offset)? else { continue };
            self.mark_consumed(offset)?;
            if is_plausible_host_frame_line(&line) {
                // BridgeMemoryBuffer.cs:213-217 reports a rejected frame's own
                // sequence with Guid.Empty, without advancing its dequeue cursor.
                let mut fields = line.split('|');
                let kind = fields.next();
                let correlation = fields.next().and_then(Guid::parse_wire);
                let uncorrelated_error = kind == Some("ERROR") && correlation == Some(Guid::EMPTY);
                if seq <= self.sequence && !uncorrelated_error && !matches!(kind, Some("EVENT") | Some("HELLO"))
                    && self.in_flight.iter().flatten().any(|(sent, corr)| *sent == seq && Some(*corr) == correlation)
                {
                    self.ctl_read_sequence = self.ctl_read_sequence.max(seq);
                    for sent in &mut self.in_flight {
                        if sent.is_some_and(|(sequence, _)| sequence <= self.ctl_read_sequence) { *sent = None; }
                    }
                }
                self.record(&crate::record::host_frame(seq, &line));
                // Publish newly eligible commands in this poll. A write failure
                // is sticky and reported by the next call, preserving this
                // already-consumed reply for its original sender.
                if let Err(error) = self.flush_commands() {
                    if self.failure.is_none() {
                        self.failure = Some(error);
                    }
                }
                return Ok(Some(line));
            }
            if !self.layout_v11 {
                return Ok(None);
            }
        }
        Ok(None)
    }

    /// `IsFindMarkerVisible`: the mod has re-armed its discovery beacon on the compat slot.
    pub fn is_find_marker_visible(&self) -> Result<bool, BridgeError> {
        Ok(self.read_bytes(COMPAT_SLOT_OFFSET, MARKER_LEN)?.is_some_and(|m| m[..] == control_marker()))
    }

    // ---- bulk ----

    fn require_v11(&self) -> Result<(), BridgeError> {
        if self.layout_v11 {
            Ok(())
        } else {
            Err(BridgeError::NotConnected)
        }
    }

    fn read_bulk_header(&self, slot: BulkSlot) -> Result<BulkSlotHeader, BridgeError> {
        let mut buf = [0u8; BULK_SLOT_HEADER_SIZE];
        self.mem.read(self.addr(slot.byte_offset())?, &mut buf)?;
        BulkSlotHeader::from_bytes(&buf).ok_or(BridgeError::BulkMismatch { slot: slot.index(), what: "header" })
    }

    fn host_ready_header(&self, slot: BulkSlot) -> Result<BulkSlotHeader, BridgeError> {
        let h = self.read_bulk_header(slot)?;
        if h.state != BULK_STATE_HOST_READY {
            return Err(BridgeError::StaleStamp { slot: slot.index() });
        }
        Ok(h)
    }

    /// Read `len` payload bytes, check them against `crc`, stamp the slot host-consumed.
    fn take_host_payload(&self, slot: BulkSlot, len: usize, crc: u32) -> Result<Vec<u8>, BridgeError> {
        let mut payload = vec![0u8; len];
        self.mem.read(self.addr(slot.byte_offset() + BULK_SLOT_HEADER_SIZE)?, &mut payload)?;
        if crc32(&payload) != crc {
            return Err(BridgeError::BulkCrcMismatch { slot: slot.index() });
        }
        self.write_bytes(slot.byte_offset(), &BULK_STATE_HOST_CONSUMED.to_le_bytes())?;
        Ok(payload)
    }

    fn checked_length(slot: BulkSlot, h: &BulkSlotHeader) -> Result<usize, BridgeError> {
        if h.total_length <= 0 || h.total_length as usize > BULK_SLOT_PAYLOAD_MAX {
            return Err(BridgeError::BulkMismatch { slot: slot.index(), what: "length" });
        }
        Ok(h.total_length as usize)
    }

    /// A host-ready bulk slot: its payload checked against the header's CRC, the slot stamped host-consumed,
    /// and the bytes classified (`SNAPSHOT2` or text).
    pub fn read_bulk(&mut self, slot: usize) -> Result<BulkPayload, BridgeError> {
        self.require_v11()?;
        let slot = BulkSlot::new(slot)?;
        let h = self.host_ready_header(slot)?;
        let len = Self::checked_length(slot, &h)?;
        Ok(BulkPayload::classify(self.take_host_payload(slot, len, h.crc32)?))
    }

    /// `TryReadHostBulkPayload` after a `BULK_REF`: the header CRC must also equal the reply's.
    pub fn read_bulk_ref(&mut self, slot: usize, expected_crc: u32) -> Result<BulkPayload, BridgeError> {
        self.require_v11()?;
        let slot = BulkSlot::new(slot)?;
        let h = self.host_ready_header(slot)?;
        let len = Self::checked_length(slot, &h)?;
        if h.crc32 != expected_crc {
            return Err(BridgeError::BulkCrcMismatch { slot: slot.index() });
        }
        Ok(BulkPayload::classify(self.take_host_payload(slot, len, expected_crc)?))
    }

    /// `ReadHostBulkPayload` (household export): the correlation id and the exact length must match too.
    pub fn read_bulk_for(&mut self, corr: Guid, slot: usize, expected_len: usize, expected_crc: u32) -> Result<Vec<u8>, BridgeError> {
        self.require_v11()?;
        let slot = BulkSlot::new(slot)?;
        if expected_len == 0 || expected_len > BULK_SLOT_PAYLOAD_MAX {
            return Err(BridgeError::BulkPayloadTooLarge { len: expected_len });
        }
        let h = self.host_ready_header(slot)?;
        if Guid::from_dotnet_bytes(&h.correlation_id) != corr {
            return Err(BridgeError::BulkMismatch { slot: slot.index(), what: "correlation id" });
        }
        if h.total_length < 0 || h.total_length as usize != expected_len {
            return Err(BridgeError::BulkMismatch { slot: slot.index(), what: "length" });
        }
        if h.crc32 != expected_crc {
            return Err(BridgeError::BulkCrcMismatch { slot: slot.index() });
        }
        self.take_host_payload(slot, expected_len, expected_crc)
    }

    /// Publish into ctl bulk `slot` through the ctl state machine (`CtlWriting`, header, payload, `CtlReady`);
    /// `corr` is the GUID in `Guid.ToByteArray` order. The slot must be empty or ctl-consumed.
    ///
    /// An `Err` after the first write leaves the slot `CtlWriting`: the mod does not read it, and `write_bulk`
    /// will not reuse it (`StaleStamp`). Treat it as a broken bridge and re-attach; do not republish.
    ///
    /// The re-attach does not reclaim that slot (review N6, answered): CONNECT clears control frames on both
    /// sides only - here in `begin_connect`, and in the mod's `HandleConnect`
    /// (`SimsOnlineServerHostMod/ServerHostMain.cs`) via `ClearHostOutboundSlotsForReconnect` - and a bulk
    /// slot's state is never reset except by a new mod process, whose region starts zeroed
    /// (`BridgeMemoryBuffer.cs`, `Region`). So a stranded slot stays dead for the rest of the mod session
    /// (`tests/driver_fake_mod.rs::a_reattach_does_not_clear_a_stranded_ctl_writing_bulk_slot`). Recovering
    /// it is an open liveness question for card 57, not behaviour this card changes.
    pub fn write_bulk(&mut self, slot: usize, corr: [u8; 16], bytes: &[u8]) -> Result<(), BridgeError> {
        self.require_v11()?;
        let slot = BulkSlot::new(slot)?;
        if bytes.len() > BULK_SLOT_PAYLOAD_MAX {
            return Err(BridgeError::BulkPayloadTooLarge { len: bytes.len() });
        }
        if bytes.is_empty() {
            return Err(BridgeError::BulkMismatch { slot: slot.index(), what: "empty payload" });
        }
        if !matches!(self.read_stamp(slot.byte_offset())?, Some(BULK_STATE_EMPTY) | Some(BULK_STATE_CTL_CONSUMED)) {
            return Err(BridgeError::StaleStamp { slot: slot.index() });
        }
        let header = BulkSlotHeader {
            state: BULK_STATE_CTL_WRITING,
            correlation_id: corr,
            total_length: bytes.len() as i32,
            crc32: crc32(bytes),
        };
        self.write_bytes(slot.byte_offset(), &BULK_STATE_CTL_WRITING.to_le_bytes())?;
        self.write_bytes(slot.byte_offset(), &header.to_bytes())?;
        self.write_bytes(slot.byte_offset() + BULK_SLOT_HEADER_SIZE, bytes)?;
        self.write_bytes(slot.byte_offset(), &BULK_STATE_CTL_READY.to_le_bytes())
    }

    /// `TryWriteCtlBulkPayload`: the first empty or ctl-consumed slot. Returns `(slot, crc32)`.
    pub fn write_ctl_bulk(&mut self, corr: Guid, bytes: &[u8]) -> Result<(usize, u32), BridgeError> {
        self.require_v11()?;
        if bytes.len() > BULK_SLOT_PAYLOAD_MAX {
            return Err(BridgeError::BulkPayloadTooLarge { len: bytes.len() });
        }
        for i in 0..BULK_SLOT_COUNT {
            let offset = BulkSlot::new(i)?.byte_offset();
            if matches!(self.read_stamp(offset)?, Some(BULK_STATE_EMPTY) | Some(BULK_STATE_CTL_CONSUMED)) {
                self.write_bulk(i, corr.to_dotnet_bytes(), bytes)?;
                return Ok((i, crc32(bytes)));
            }
        }
        Err(BridgeError::NoWritableBulkSlot)
    }
}
