#![forbid(unsafe_code)]
//! The SimLink sidecar's record ring, read from the controller side (layout v1).
//!
//! Oracle: `DedicatedServer/HostBridge/SimLinkShadowReader.cs`; native layout: `SimLink/src/simlink_layout.h`.
//! A TS3W process with the SimLink ASI loaded creates a shared-memory section (`SimsOnline.SimLink.<pid>`) and two
//! auto-reset events (`.control`, `.data`). The controller asks the sidecar to register its internal calls
//! (`StateRegisterRequested`, then `.control`), and afterwards drains a single-producer/single-consumer byte ring
//! of 8-byte-aligned records.
//!
//! Every access to the shared bytes goes through the [`Section`] trait. It exposes only the fixed v1 header slots
//! (each with exactly one access width, none overlapping another) and a copy restricted to the ring data region;
//! the raw byte-offset primitives stay private to [`crate::ffi`], so safe code cannot issue mixed-width or
//! atomic/non-atomic accesses to the same bytes. The live implementation ([`crate::ffi::NativeSection`]) uses
//! atomic loads/stores for header slots and volatile copies for ring bytes. No reference into shared
//! memory is ever built here, so a producer that mutates it concurrently cannot cause Rust aliasing UB. A
//! malformed section is a typed [`RingError`] and a poisoned reader, never a panic or an endless loop.
//! [`MemorySection`] is the injectable in-memory section and producer model the offline tests drive.

use std::fmt;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex, MutexGuard};
use std::time::{Duration, Instant};

pub const MAGIC: [u8; 8] = *b"SIMLINK1";
pub const LAYOUT_VERSION: u32 = 1;
/// `kHeaderSize`: the smallest legal `HeaderSize` field and mapping.
pub const HEADER_BYTES: u32 = 256;
/// `kRecordHeader`: a data record's fixed prefix, before its UTF-16 characters.
pub const RECORD_HEADER: u32 = 24;
pub const KIND_DATA: u16 = 1;
pub const KIND_PAD: u16 = 2;

pub const STATE_CREATED: u32 = 0;
pub const STATE_REGISTER_REQUESTED: u32 = 1;
pub const STATE_REGISTERED: u32 = 2;
pub const STATE_FAILED: u32 = 3;

/// `simlink::off::*`: byte offsets of the header fields.
pub mod off {
    pub const MAGIC: usize = 0;
    pub const LAYOUT_VERSION: usize = 8;
    pub const HEADER_SIZE: usize = 12;
    pub const PID: usize = 16;
    pub const STATE: usize = 20;
    pub const ERROR: usize = 24;
    pub const RING_OFFSET: usize = 28;
    pub const RING_SIZE: usize = 32;
    pub const STRING_LENGTH_OFFSET: usize = 36;
    pub const QPC_FREQUENCY: usize = 40;
    pub const WRITE_POS: usize = 48;
    pub const READ_POS: usize = 56;
    pub const PUBLISHED: usize = 64;
    pub const DROPPED: usize = 72;
    pub const CONTENDED: usize = 80;
    pub const BAD_ARGS: usize = 88;
    pub const NATIVE_VERSION: usize = 96;
    pub const PROBE_RESULT: usize = 100;
}

/// Why a ring is absent or unusable. `NoSection` is the benign "no sidecar" case; the other two mean a section
/// exists but must not be trusted.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RingError {
    /// No section (or no platform support) to open.
    NoSection(String),
    /// A section exists but its header is not a layout-v1 SimLink header, or its fields are out of bounds.
    BadHeader(String),
    /// The ring's contents or positions are corrupt; the reader refuses every later drain.
    Corrupt(String),
}

impl fmt::Display for RingError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::NoSection(why) => write!(f, "no_section: {why}"),
            Self::BadHeader(why) => write!(f, "bad_header: {why}"),
            Self::Corrupt(why) => write!(f, "corrupt_ring: {why}"),
        }
    }
}

impl std::error::Error for RingError {}

/// Safe access to a mapped SimLink section and its events: the fixed layout-v1 header slots, plus a copy of ring
/// bytes. Every header slot has one fixed width (the width the native side uses) and the slots never overlap, so
/// two `&self` calls can never touch overlapping bytes with different widths. Ring bytes are reachable only
/// through [`Section::copy_ring`], which refuses any range inside the header (the first [`HEADER_BYTES`] bytes)
/// or outside the mapping and returns zeros instead. A header read is an `Acquire` atomic load and a header write
/// a `Release` atomic store.
pub trait Section {
    /// Mapped length in bytes.
    fn len(&self) -> usize;
    /// `Magic` (8 bytes at offset 0).
    fn magic(&self) -> [u8; 8];
    /// `LayoutVersion`.
    fn version(&self) -> u32;
    /// `HeaderSize`.
    fn header_size(&self) -> u32;
    /// `Pid`.
    fn pid(&self) -> u32;
    /// `State`.
    fn state(&self) -> u32;
    /// Writes `State` (the controller's registration request).
    fn set_state(&self, state: u32);
    /// `Error`.
    fn error(&self) -> u32;
    /// `RingOffset`.
    fn ring_offset(&self) -> u32;
    /// `RingSize`.
    fn ring_size(&self) -> u32;
    /// `QpcFrequency`.
    fn qpc_frequency(&self) -> u64;
    /// `WritePos` (producer-owned).
    fn write_pos(&self) -> u64;
    /// `ReadPos` (consumer-owned).
    fn read_pos(&self) -> u64;
    /// Writes `ReadPos`, releasing the ring space behind it.
    fn set_read_pos(&self, pos: u64);
    /// `Published`.
    fn published(&self) -> u64;
    /// `Dropped`.
    fn dropped(&self) -> u64;
    /// `Contended`.
    fn contended(&self) -> u64;
    /// `BadArgs`.
    fn bad_args(&self) -> u64;
    /// `NativeVersion`.
    fn native_version(&self) -> u32;
    /// `ProbeResult`.
    fn probe_result(&self) -> u32;
    /// Copies `out.len()` ring bytes starting at absolute section offset `offset` into `out`. Only the ring data
    /// region is readable: a range that starts inside the header (`offset < HEADER_BYTES`) or ends past the
    /// mapping fills `out` with zeros.
    fn copy_ring(&self, offset: usize, out: &mut [u8]);
    /// Whether the `.control` event was opened.
    fn has_control(&self) -> bool;
    /// Whether the `.data` event was opened.
    fn has_data(&self) -> bool;
    /// `SetEvent(.control)`.
    fn signal_control(&self);
    /// `WaitForSingleObject(.data, ms)`: true when the event was signalled (auto-reset, so it is consumed).
    fn wait_data(&self, ms: u32) -> bool;
    fn is_empty(&self) -> bool {
        self.len() == 0
    }
}

/// One published record (`KindData`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Record {
    pub lane: u16,
    pub seq: i32,
    /// `QueryPerformanceCounter` in the game at publish.
    pub qpc: i64,
    /// The payload's raw UTF-16 code units, exactly as published. The native producer validates only the length,
    /// and the C# oracle keeps lone surrogates (`Marshal.PtrToStringUni`), so nothing here rejects them.
    pub units: Vec<u16>,
}

impl Record {
    /// The payload as text, each unpaired surrogate replaced by U+FFFD.
    pub fn text_lossy(&self) -> String {
        String::from_utf16_lossy(&self.units)
    }

    /// The payload as text, or `None` when it holds an unpaired surrogate.
    pub fn text_strict(&self) -> Option<String> {
        String::from_utf16(&self.units).ok()
    }
}

/// The header counters the native side keeps.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Telemetry {
    pub published: u64,
    pub dropped: u64,
    pub contended: u64,
    pub bad_args: u64,
    pub native_version: i32,
    pub probe_result: i32,
    pub qpc_frequency: i64,
}

/// The consumer side of one SimLink ring.
pub struct RingReader<S: Section> {
    section: S,
    ring_offset: usize,
    ring_size: u32,
    poisoned: Option<String>,
}

impl<S: Section> RingReader<S> {
    /// Validates the header and takes the section. `expected_pid` is checked against the header's `Pid` when
    /// given. Beyond the C# oracle's magic/version check, the ring geometry is bounds-checked against the mapping
    /// so no later access can leave it.
    pub fn open(section: S, expected_pid: Option<u32>) -> Result<Self, RingError> {
        let len = section.len();
        if len < HEADER_BYTES as usize {
            return Err(RingError::BadHeader(format!("mapping is {len} bytes, under the {HEADER_BYTES}-byte header")));
        }
        let magic = section.magic();
        let version = section.version();
        if magic != MAGIC || version != LAYOUT_VERSION {
            return Err(RingError::BadHeader(format!(
                "unexpected layout (magic={}, version={version})",
                String::from_utf8_lossy(&magic).escape_debug()
            )));
        }
        let header_size = section.header_size();
        if header_size < HEADER_BYTES {
            return Err(RingError::BadHeader(format!("header size {header_size} is under {HEADER_BYTES}")));
        }
        let pid = section.pid();
        if let Some(expected) = expected_pid {
            if pid != expected {
                return Err(RingError::BadHeader(format!("header pid {pid} is not the expected {expected}")));
            }
        }
        let ring_offset = section.ring_offset();
        let ring_size = section.ring_size();
        if ring_size < 8 || !ring_size.is_power_of_two() {
            return Err(RingError::BadHeader(format!("ring size {ring_size} is not a power of two of at least 8")));
        }
        if ring_offset < header_size {
            return Err(RingError::BadHeader(format!("ring offset {ring_offset} is inside the {header_size}-byte header")));
        }
        if u64::from(ring_offset) + u64::from(ring_size) > len as u64 {
            return Err(RingError::BadHeader(format!(
                "ring [{ring_offset}, +{ring_size}) does not fit the {len}-byte mapping"
            )));
        }
        Ok(Self { section, ring_offset: ring_offset as usize, ring_size, poisoned: None })
    }

    pub fn section(&self) -> &S {
        &self.section
    }

    pub fn ring_size(&self) -> u32 {
        self.ring_size
    }

    pub fn state(&self) -> u32 {
        self.section.state()
    }

    pub fn error(&self) -> u32 {
        self.section.error()
    }

    pub fn pid(&self) -> u32 {
        self.section.pid()
    }

    /// Why the ring was abandoned, once a drain found it corrupt.
    pub fn poisoned(&self) -> Option<&str> {
        self.poisoned.as_deref()
    }

    pub fn telemetry(&self) -> Telemetry {
        Telemetry {
            published: self.section.published(),
            dropped: self.section.dropped(),
            contended: self.section.contended(),
            bad_args: self.section.bad_args(),
            native_version: self.section.native_version() as i32,
            probe_result: self.section.probe_result() as i32,
            qpc_frequency: self.section.qpc_frequency() as i64,
        }
    }

    /// `RequestRegistration`: asks the sidecar to register its internal calls and waits for the answer. True when
    /// it registered; false on `StateFailed`, a missing `.control` event or the timeout.
    pub fn request_registration(&self, timeout: Duration) -> bool {
        if self.state() == STATE_REGISTERED {
            return true;
        }
        if !self.section.has_control() {
            return false;
        }
        self.section.set_state(STATE_REGISTER_REQUESTED);
        self.section.signal_control();
        let deadline = Instant::now() + timeout;
        loop {
            match self.state() {
                STATE_REGISTERED => return true,
                STATE_FAILED => return false,
                _ => {}
            }
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                return false;
            }
            std::thread::sleep(remaining.min(Duration::from_millis(10)));
        }
    }

    /// Waits up to `milliseconds` for the `.data` event.
    pub fn wait_for_data(&self, milliseconds: u32) -> bool {
        self.section.has_data() && self.section.wait_data(milliseconds)
    }

    /// Moves every record published so far into `into` and frees their space (`ReadPos` advances). Returns the
    /// number of data records appended. A corrupt ring appends nothing, leaves `ReadPos` alone and poisons the
    /// reader: every later drain returns the same error without touching the section.
    pub fn drain(&mut self, into: &mut Vec<Record>) -> Result<usize, RingError> {
        if let Some(why) = &self.poisoned {
            return Err(RingError::Corrupt(why.clone()));
        }
        let write = self.section.write_pos();
        let start = self.section.read_pos();
        if write < start || write - start > u64::from(self.ring_size) {
            return Err(self.poison(format!(
                "positions out of range (write {write}, read {start}, ring {})",
                self.ring_size
            )));
        }
        let mut read = start;
        let mut taken = Vec::new();
        while read < write {
            match self.parse_record(read, write) {
                Ok((advance, record)) => {
                    // `parse_record` bounds `advance` by `write - read`, so this cannot overflow; checked anyway.
                    read = match read.checked_add(advance) {
                        Some(next) => next,
                        None => return Err(self.poison(format!("read position {read} overflows by {advance}"))),
                    };
                    taken.extend(record);
                }
                Err(why) => return Err(self.poison(why)),
            }
        }
        if read != start {
            self.section.set_read_pos(read);
        }
        let count = taken.len();
        into.extend(taken);
        Ok(count)
    }

    fn poison(&mut self, why: String) -> RingError {
        self.poisoned = Some(why.clone());
        RingError::Corrupt(why)
    }

    // Parses the record at `read` (within [read, write)). A pad reads only its 8-byte head: the rest of the tail is
    // unwritten.
    fn parse_record(&self, read: u64, write: u64) -> Result<(u64, Option<Record>), String> {
        if !read.is_multiple_of(8) {
            return Err(format!("read position {read} is not 8-byte aligned"));
        }
        let position = (read & (u64::from(self.ring_size) - 1)) as usize;
        let tail = self.ring_size as usize - position;
        if tail < 8 {
            return Err(format!("record at {read} has only {tail} bytes before the ring end"));
        }
        let base = self.ring_offset + position;
        let mut head = [0u8; 8];
        self.section.copy_ring(base, &mut head);
        let size = i32::from_le_bytes([head[0], head[1], head[2], head[3]]);
        let kind = u16::from_le_bytes([head[4], head[5]]);
        let lane = u16::from_le_bytes([head[6], head[7]]);
        if size <= 0 || size % 8 != 0 || size as usize > tail {
            return Err(format!("record at {read} has size {size} (ring {}, position {position})", self.ring_size));
        }
        let size_bytes = size as u64;
        // `write - read` cannot underflow: `drain` only parses while `read < write`.
        if size_bytes > write - read {
            return Err(format!("record at {read} of size {size} runs past the write position {write}"));
        }
        match kind {
            KIND_PAD => Ok((size_bytes, None)),
            KIND_DATA => {
                if (size as u32) < RECORD_HEADER {
                    return Err(format!("data record at {read} has size {size}, under its {RECORD_HEADER}-byte header"));
                }
                let mut rest = [0u8; 16];
                self.section.copy_ring(base + 8, &mut rest);
                let seq = i32::from_le_bytes([rest[0], rest[1], rest[2], rest[3]]);
                let chars = i32::from_le_bytes([rest[4], rest[5], rest[6], rest[7]]);
                let qpc = i64::from_le_bytes([rest[8], rest[9], rest[10], rest[11], rest[12], rest[13], rest[14], rest[15]]);
                if chars < 0 || i64::from(RECORD_HEADER) + i64::from(chars) * 2 > i64::from(size) {
                    return Err(format!("record at {read} claims {chars} chars in {size} bytes"));
                }
                let mut raw = vec![0u8; chars as usize * 2];
                self.section.copy_ring(base + RECORD_HEADER as usize, &mut raw);
                let units: Vec<u16> = raw.as_chunks::<2>().0.iter().map(|pair| u16::from_le_bytes([pair[0], pair[1]])).collect();
                Ok((size_bytes, Some(Record { lane, seq, qpc, units })))
            }
            other => Err(format!("record at {read} has unknown kind {other}")),
        }
    }
}

/// `SimLinkShadowReader.SectionName`.
pub fn section_name(pid: u32) -> String {
    format!("SimsOnline.SimLink.{pid}")
}

/// The Sandboxie `BaseNamedObjects` directory (with its trailing backslash) of a TS3 in box `box_name`, as an NT
/// object path: `\Sandbox\<user>\<box>\Session_<n>\Sessions\<n>\BaseNamedObjects\`.
pub fn sandboxie_directory(user: &str, box_name: &str, session: u32) -> String {
    format!(r"\Sandbox\{user}\{box_name}\Session_{session}\Sessions\{session}\BaseNamedObjects\")
}

/// `SimLinkShadowReader.TryOpen`: the sidecar section of TS3 process `pid`, by Win32 name (`Local\...`), or by NT
/// path when it runs in Sandboxie box `sandboxie_box`. The header's pid must be `pid`.
pub fn open_native(pid: u32, sandboxie_box: Option<&str>) -> Result<RingReader<crate::ffi::NativeSection>, RingError> {
    let name = section_name(pid);
    let section = match sandboxie_box.filter(|name| !name.is_empty()) {
        None => crate::ffi::NativeSection::open_by_name(&format!(r"Local\{name}"))?,
        Some(box_name) => {
            let user = std::env::var("USERNAME").unwrap_or_default();
            let directory = sandboxie_directory(&user, box_name, crate::ffi::own_session_id());
            crate::ffi::NativeSection::open_nt(&directory, &name)?
        }
    };
    RingReader::open(section, Some(pid))
}

// ---- the offline producer model ------------------------------------------------------------------------------

/// How a [`MemorySection`] answers a registration request, as the native sidecar would.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Registration {
    /// Never answers (a timeout).
    Ignore,
    /// Moves `StateRegisterRequested` to `StateRegistered`.
    Succeed,
    /// Moves it to `StateFailed` with this `Error` code.
    Fail(u32),
}

struct Shared {
    bytes: Mutex<Vec<u8>>,
    events: bool,
    control_signals: AtomicU64,
    copied: AtomicU64,
    data: Mutex<bool>,
    data_changed: Condvar,
    registration: Mutex<Registration>,
}

/// A header for a ring of `ring_size` bytes right after it: layout v1, `Pid = pid`, `State = StateCreated`.
pub fn initial_header(ring_size: u32, pid: u32) -> Vec<u8> {
    let mut header = vec![0u8; HEADER_BYTES as usize];
    header[off::MAGIC..off::MAGIC + 8].copy_from_slice(&MAGIC);
    header[off::LAYOUT_VERSION..off::LAYOUT_VERSION + 4].copy_from_slice(&LAYOUT_VERSION.to_le_bytes());
    header[off::HEADER_SIZE..off::HEADER_SIZE + 4].copy_from_slice(&HEADER_BYTES.to_le_bytes());
    header[off::PID..off::PID + 4].copy_from_slice(&pid.to_le_bytes());
    header[off::RING_OFFSET..off::RING_OFFSET + 4].copy_from_slice(&HEADER_BYTES.to_le_bytes());
    header[off::RING_SIZE..off::RING_SIZE + 4].copy_from_slice(&ring_size.to_le_bytes());
    header[off::QPC_FREQUENCY..off::QPC_FREQUENCY + 8].copy_from_slice(&10_000_000i64.to_le_bytes());
    header
}

/// One data record as the native producer writes it, padded to 8 bytes.
pub fn encode_record(lane: u16, seq: i32, qpc: i64, text: &str) -> Vec<u8> {
    let units: Vec<u16> = text.encode_utf16().collect();
    let size = (RECORD_HEADER as usize + units.len() * 2).next_multiple_of(8);
    let mut bytes = vec![0u8; size];
    bytes[0..4].copy_from_slice(&(size as u32).to_le_bytes());
    bytes[4..6].copy_from_slice(&KIND_DATA.to_le_bytes());
    bytes[6..8].copy_from_slice(&lane.to_le_bytes());
    bytes[8..12].copy_from_slice(&seq.to_le_bytes());
    bytes[12..16].copy_from_slice(&(units.len() as i32).to_le_bytes());
    bytes[16..24].copy_from_slice(&qpc.to_le_bytes());
    for (index, unit) in units.iter().enumerate() {
        bytes[24 + index * 2..26 + index * 2].copy_from_slice(&unit.to_le_bytes());
    }
    bytes
}

/// An in-memory [`Section`] that also plays the native producer: a clone is the producer's view of the same bytes
/// and events. It is the offline stand-in for the Win32 section, so every reader and server test runs without a
/// game. Its mutex makes the "shared memory" race-free by construction; the live section's atomics and volatile
/// copies are covered by the Windows round-trip test in `ffi.rs`.
#[derive(Clone)]
pub struct MemorySection {
    shared: Arc<Shared>,
}

fn locked<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

impl MemorySection {
    /// A valid section with a `ring_size`-byte ring, both events present and registration ignored.
    pub fn new(ring_size: u32, pid: u32) -> Self {
        let mut bytes = initial_header(ring_size, pid);
        bytes.resize(HEADER_BYTES as usize + ring_size as usize, 0);
        Self::from_bytes(bytes, true)
    }

    /// A section over exactly these bytes (corruption tests).
    pub fn from_bytes(bytes: Vec<u8>, events: bool) -> Self {
        Self {
            shared: Arc::new(Shared {
                bytes: Mutex::new(bytes),
                events,
                control_signals: AtomicU64::new(0),
                copied: AtomicU64::new(0),
                data: Mutex::new(false),
                data_changed: Condvar::new(),
                registration: Mutex::new(Registration::Ignore),
            }),
        }
    }

    pub fn set_registration(&self, registration: Registration) {
        *locked(&self.shared.registration) = registration;
    }

    /// How many times `.control` was signalled.
    pub fn control_signals(&self) -> u64 {
        self.shared.control_signals.load(Ordering::SeqCst)
    }

    /// Total bytes the reader copied out of the section so far.
    pub fn bytes_copied(&self) -> u64 {
        self.shared.copied.load(Ordering::SeqCst)
    }

    /// Overwrites bytes (a producer write, or corruption).
    pub fn poke(&self, offset: usize, data: &[u8]) {
        let mut bytes = locked(&self.shared.bytes);
        if let Some(target) = bytes.get_mut(offset..offset + data.len()) {
            target.copy_from_slice(data);
        }
    }

    pub fn poke_u32(&self, offset: usize, value: u32) {
        self.poke(offset, &value.to_le_bytes());
    }

    pub fn poke_u64(&self, offset: usize, value: u64) {
        self.poke(offset, &value.to_le_bytes());
    }

    pub fn peek_u64(&self, offset: usize) -> u64 {
        self.load_u64(offset)
    }

    /// Native producer: appends one data record (a pad first when it would not fit before the ring end) and
    /// signals `.data`. False, with `Dropped` bumped, when the ring is full.
    pub fn publish(&self, lane: u16, seq: i32, qpc: i64, text: &str) -> bool {
        let record = encode_record(lane, seq, qpc, text);
        let mut bytes = locked(&self.shared.bytes);
        let word = |bytes: &[u8], at: usize| u64::from_le_bytes(bytes[at..at + 8].try_into().expect("8 bytes"));
        let ring_size = u64::from(u32::from_le_bytes(bytes[off::RING_SIZE..off::RING_SIZE + 4].try_into().expect("4 bytes")));
        let ring_offset = u32::from_le_bytes(bytes[off::RING_OFFSET..off::RING_OFFSET + 4].try_into().expect("4 bytes")) as usize;
        let mut write = word(&bytes, off::WRITE_POS);
        let read = word(&bytes, off::READ_POS);
        let size = record.len() as u64;
        let utf16_units = text.encode_utf16().count() as u64;
        if utf16_units > ring_size / 4 || size > ring_size / 2 {
            let dropped = word(&bytes, off::DROPPED) + 1;
            bytes[off::DROPPED..off::DROPPED + 8].copy_from_slice(&dropped.to_le_bytes());
            return false;
        }
        let mut position = write & (ring_size - 1);
        let tail = ring_size - position;
        let needed = if size > tail { tail + size } else { size };
        if write - read + needed > ring_size {
            let dropped = word(&bytes, off::DROPPED) + 1;
            bytes[off::DROPPED..off::DROPPED + 8].copy_from_slice(&dropped.to_le_bytes());
            return false;
        }
        if size > tail {
            // Only the pad's first 8 bytes are written; the rest of the tail is left as it was.
            let at = ring_offset + position as usize;
            bytes[at..at + 4].copy_from_slice(&(tail as u32).to_le_bytes());
            bytes[at + 4..at + 6].copy_from_slice(&KIND_PAD.to_le_bytes());
            bytes[at + 6..at + 8].copy_from_slice(&0u16.to_le_bytes());
            write += tail;
            position = 0;
        }
        let at = ring_offset + position as usize;
        bytes[at..at + record.len()].copy_from_slice(&record);
        write += size;
        bytes[off::WRITE_POS..off::WRITE_POS + 8].copy_from_slice(&write.to_le_bytes());
        let published = word(&bytes, off::PUBLISHED) + 1;
        bytes[off::PUBLISHED..off::PUBLISHED + 8].copy_from_slice(&published.to_le_bytes());
        drop(bytes);
        self.signal_data();
        true
    }

    /// `SetEvent(.data)` from the producer.
    pub fn signal_data(&self) {
        *locked(&self.shared.data) = true;
        self.shared.data_changed.notify_all();
    }

    fn read_array<const N: usize>(&self, offset: usize) -> [u8; N] {
        let bytes = locked(&self.shared.bytes);
        let mut out = [0u8; N];
        if let Some(source) = bytes.get(offset..offset + N) {
            out.copy_from_slice(source);
        }
        out
    }
}

impl MemorySection {
    fn load_u32(&self, off: usize) -> u32 {
        u32::from_le_bytes(self.read_array(off))
    }

    fn load_u64(&self, off: usize) -> u64 {
        u64::from_le_bytes(self.read_array(off))
    }

    /// Copies `out.len()` bytes at `off` (header included: the offline tests inspect and corrupt it) into `out`,
    /// zeros when out of range. Not part of [`Section`].
    pub fn copy_out(&self, off: usize, out: &mut [u8]) {
        let bytes = locked(&self.shared.bytes);
        match bytes.get(off..off.saturating_add(out.len())) {
            Some(source) => out.copy_from_slice(source),
            None => out.fill(0),
        }
    }
}

impl Section for MemorySection {
    fn len(&self) -> usize {
        locked(&self.shared.bytes).len()
    }

    fn magic(&self) -> [u8; 8] {
        self.read_array(off::MAGIC)
    }

    fn version(&self) -> u32 {
        self.load_u32(off::LAYOUT_VERSION)
    }

    fn header_size(&self) -> u32 {
        self.load_u32(off::HEADER_SIZE)
    }

    fn pid(&self) -> u32 {
        self.load_u32(off::PID)
    }

    fn state(&self) -> u32 {
        self.load_u32(off::STATE)
    }

    fn set_state(&self, state: u32) {
        self.poke_u32(off::STATE, state);
    }

    fn error(&self) -> u32 {
        self.load_u32(off::ERROR)
    }

    fn ring_offset(&self) -> u32 {
        self.load_u32(off::RING_OFFSET)
    }

    fn ring_size(&self) -> u32 {
        self.load_u32(off::RING_SIZE)
    }

    fn qpc_frequency(&self) -> u64 {
        self.load_u64(off::QPC_FREQUENCY)
    }

    fn write_pos(&self) -> u64 {
        self.load_u64(off::WRITE_POS)
    }

    fn read_pos(&self) -> u64 {
        self.load_u64(off::READ_POS)
    }

    fn set_read_pos(&self, pos: u64) {
        self.poke_u64(off::READ_POS, pos);
    }

    fn published(&self) -> u64 {
        self.load_u64(off::PUBLISHED)
    }

    fn dropped(&self) -> u64 {
        self.load_u64(off::DROPPED)
    }

    fn contended(&self) -> u64 {
        self.load_u64(off::CONTENDED)
    }

    fn bad_args(&self) -> u64 {
        self.load_u64(off::BAD_ARGS)
    }

    fn native_version(&self) -> u32 {
        self.load_u32(off::NATIVE_VERSION)
    }

    fn probe_result(&self) -> u32 {
        self.load_u32(off::PROBE_RESULT)
    }

    fn copy_ring(&self, offset: usize, out: &mut [u8]) {
        let bytes = locked(&self.shared.bytes);
        let range = if offset < HEADER_BYTES as usize { None } else { bytes.get(offset..offset.saturating_add(out.len())) };
        match range {
            Some(source) => {
                out.copy_from_slice(source);
                self.shared.copied.fetch_add(out.len() as u64, Ordering::SeqCst);
            }
            None => out.fill(0),
        }
    }

    fn has_control(&self) -> bool {
        self.shared.events
    }

    fn has_data(&self) -> bool {
        self.shared.events
    }

    fn signal_control(&self) {
        self.shared.control_signals.fetch_add(1, Ordering::SeqCst);
        let policy = *locked(&self.shared.registration);
        if self.load_u32(off::STATE) == STATE_REGISTER_REQUESTED {
            match policy {
                Registration::Ignore => {}
                Registration::Succeed => self.poke_u32(off::STATE, STATE_REGISTERED),
                Registration::Fail(code) => {
                    self.poke_u32(off::ERROR, code);
                    self.poke_u32(off::STATE, STATE_FAILED);
                }
            }
        }
    }

    fn wait_data(&self, ms: u32) -> bool {
        let guard = locked(&self.shared.data);
        let (mut guard, _) = self
            .shared
            .data_changed
            .wait_timeout_while(guard, Duration::from_millis(u64::from(ms)), |signalled| !*signalled)
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        std::mem::take(&mut *guard)
    }
}
