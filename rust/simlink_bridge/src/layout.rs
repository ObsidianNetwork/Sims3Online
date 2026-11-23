//! Layout v11 mirrored from the mod (card 56 §B). Every value is re-declared here and proved by a
//! compile-time witness in this file; the source of truth is
//! `SimsOnlineServerHostMod/BridgeSharedRegionLayout.cs` (its controller copy is
//! `DedicatedServer/HostBridge/BridgeSharedRegionLayout.cs`). The live control-slot header is 16 bytes, so
//! no witness here cites card 10 §B.2's 32-byte header (card 56 survey contradiction 1).
//!
//! Each `pub const` sits on one line so `tools/mutation_witnesses.py` (gate 2) can mutate it in a scratch
//! copy and record that the build then fails.
#![forbid(unsafe_code)]

use core::mem::{offset_of, size_of};

use crate::error::BridgeError;

// ---- §B.1 constants (values at 9c83c621) ----
pub const LAYOUT_VERSION: u32 = 11;
pub const BRIDGE_WIRE_PROTOCOL_VERSION: u32 = 23;
pub const HELLO_WIRE_VERSION: u32 = 6;

pub const SLOT_SIZE: usize = 4096;
pub const SLOT_HEADER_SIZE: usize = 16;
pub const SLOT_PAYLOAD_MAX: usize = SLOT_SIZE - SLOT_HEADER_SIZE;
pub const CONTROL_HEADER_SIZE: usize = 64;
pub const CONTROL_SLOT_COUNT: usize = 16;
pub const HOST_SLOT_BASE: usize = 0;
pub const HOST_SLOT_COUNT: usize = 8;
pub const CTL_SLOT_BASE: usize = 8;
pub const CTL_SLOT_COUNT: usize = 8;

pub const BULK_SLOT_COUNT: usize = 4;
pub const BULK_SLOT_SIZE: usize = 524288;
pub const BULK_HEADER_SIZE: usize = 64;
pub const BULK_SLOT_HEADER_SIZE: usize = 32;

pub const CONTROL_REGION_SIZE: usize = CONTROL_HEADER_SIZE + CONTROL_SLOT_COUNT * SLOT_SIZE;
pub const BULK_REGION_SIZE: usize = BULK_HEADER_SIZE + BULK_SLOT_COUNT * BULK_SLOT_SIZE;
pub const TOTAL_REGION_SIZE: usize = CONTROL_REGION_SIZE + BULK_REGION_SIZE;

pub const HOST_SENDER_ID: u64 = 0x92348DE77B2A9F34;
pub const SERVER_SENDER_ID: u64 = 0xFFB435D1231AF018;
pub const CONSUMED_SENDER_ID: u64 = 0xAC0DE5EDFEEDFACE;
pub const HOST_WRITING_SENDER_ID: u64 = 0xB17EC0DE57A6E511;

pub const LAYOUT_VERSION_OFFSET: usize = 32;

pub const DEFAULT_HELLO_CAPS: &str = "multi-slot=16,bulk=4x512k,layout=11,snapshot2=1";

pub const BULK_SLOT_PAYLOAD_MAX: usize = BULK_SLOT_SIZE - BULK_SLOT_HEADER_SIZE;

pub const BULK_STATE_EMPTY: u64 = 0x0000000000000000;
pub const BULK_STATE_HOST_WRITING: u64 = 0xC011C01100000001;
pub const BULK_STATE_HOST_READY: u64 = 0xC011C01100000002;
pub const BULK_STATE_HOST_CONSUMED: u64 = 0xC011C01100000003;
pub const BULK_STATE_CTL_WRITING: u64 = 0xC011C01100000011;
pub const BULK_STATE_CTL_READY: u64 = 0xC011C01100000012;
pub const BULK_STATE_CTL_CONSUMED: u64 = 0xC011C01100000013;

// `DedicatedServer/HostBridge/Snapshot2BinaryReader.cs` (`Magic`, `MinVersion`, `Version`), §C.5.
pub const SNAPSHOT2_MAGIC: u32 = 0x32505353;
pub const SNAPSHOT2_VERSION_MIN: u32 = 1;
pub const SNAPSHOT2_VERSION_MAX: u32 = 2;

/// The compat slot sits at byte 0 of the region and overlaps the marker and the v11 control header
/// (`ProcessMemoryBridge.cs` `CompatSlotOffset`).
pub const COMPAT_SLOT_OFFSET: usize = 0;

/// The marker length both regions use (`ProcessMemoryBridge.cs` `BuildFindMarker`).
pub const MARKER_LEN: usize = 32;

// ---- literal witnesses: each constant against its value in the mod source ----
const _: () = assert!(LAYOUT_VERSION == 11);
const _: () = assert!(BRIDGE_WIRE_PROTOCOL_VERSION == 23);
const _: () = assert!(HELLO_WIRE_VERSION == 6);
const _: () = assert!(SLOT_SIZE == 4096);
const _: () = assert!(SLOT_HEADER_SIZE == 16);
const _: () = assert!(SLOT_PAYLOAD_MAX == 4080);
const _: () = assert!(CONTROL_HEADER_SIZE == 64);
const _: () = assert!(CONTROL_SLOT_COUNT == 16);
const _: () = assert!(HOST_SLOT_BASE == 0);
const _: () = assert!(HOST_SLOT_COUNT == 8);
const _: () = assert!(CTL_SLOT_BASE == 8);
const _: () = assert!(CTL_SLOT_COUNT == 8);
const _: () = assert!(BULK_SLOT_COUNT == 4);
const _: () = assert!(BULK_SLOT_SIZE == 524288);
const _: () = assert!(BULK_HEADER_SIZE == 64);
const _: () = assert!(BULK_SLOT_HEADER_SIZE == 32);
const _: () = assert!(CONTROL_REGION_SIZE == 65600);
const _: () = assert!(BULK_REGION_SIZE == 2097216);
const _: () = assert!(TOTAL_REGION_SIZE == 2162816);
// The literal sum, asserted once (§B.1).
const _: () = assert!(TOTAL_REGION_SIZE == 65600 + 2097216);
const _: () = assert!(HOST_SENDER_ID == 0x92348DE77B2A9F34);
const _: () = assert!(SERVER_SENDER_ID == 0xFFB435D1231AF018);
const _: () = assert!(CONSUMED_SENDER_ID == 0xAC0DE5EDFEEDFACE);
const _: () = assert!(HOST_WRITING_SENDER_ID == 0xB17EC0DE57A6E511);
const _: () = assert!(LAYOUT_VERSION_OFFSET == 32);
const _: () = assert!(const_str_eq(DEFAULT_HELLO_CAPS, "multi-slot=16,bulk=4x512k,layout=11,snapshot2=1"));
const _: () = assert!(BULK_SLOT_PAYLOAD_MAX == 524256);
const _: () = assert!(BULK_STATE_EMPTY == 0);
const _: () = assert!(BULK_STATE_HOST_WRITING == 0xC011C01100000001);
const _: () = assert!(BULK_STATE_HOST_READY == 0xC011C01100000002);
const _: () = assert!(BULK_STATE_HOST_CONSUMED == 0xC011C01100000003);
const _: () = assert!(BULK_STATE_CTL_WRITING == 0xC011C01100000011);
const _: () = assert!(BULK_STATE_CTL_READY == 0xC011C01100000012);
const _: () = assert!(BULK_STATE_CTL_CONSUMED == 0xC011C01100000013);
const _: () = assert!(SNAPSHOT2_MAGIC == 0x32505353);
const _: () = assert!(SNAPSHOT2_VERSION_MIN == 1);
const _: () = assert!(SNAPSHOT2_VERSION_MAX == 2);
const _: () = assert!(COMPAT_SLOT_OFFSET == 0);
const _: () = assert!(MARKER_LEN == 32);

// ---- derived-relation witnesses ----
const _: () = assert!(SLOT_PAYLOAD_MAX == SLOT_SIZE - SLOT_HEADER_SIZE);
const _: () = assert!(BULK_SLOT_PAYLOAD_MAX == BULK_SLOT_SIZE - BULK_SLOT_HEADER_SIZE);
// The kernel's control-line budget (`rust/sim96_kernel_cleanroom/src/runtime_bridge.rs:131`
// `MAX_LINE_BYTES = 4080`): equal by value; this crate does not depend on the kernel (§D U5).
pub const MAX_LINE_BYTES: usize = SLOT_PAYLOAD_MAX;
const _: () = assert!(MAX_LINE_BYTES == 4080);
// Bands disjoint and covering 0..16.
const _: () = assert!(HOST_SLOT_BASE + HOST_SLOT_COUNT == CTL_SLOT_BASE);
const _: () = assert!(CTL_SLOT_BASE + CTL_SLOT_COUNT == CONTROL_SLOT_COUNT);
const _: () = assert!(HOST_SLOT_BASE == 0);
// The marker and its header fit inside the 64-byte headers.
const _: () = assert!(MARKER_LEN == LAYOUT_VERSION_OFFSET);
// The region fits a 32-bit guest address space with room to spare.
const _: () = assert!(TOTAL_REGION_SIZE < u32::MAX as usize);

const fn const_str_eq(a: &str, b: &str) -> bool {
    let (a, b) = (a.as_bytes(), b.as_bytes());
    if a.len() != b.len() {
        return false;
    }
    let mut i = 0;
    while i < a.len() {
        if a[i] != b[i] {
            return false;
        }
        i += 1;
    }
    true
}

// ---- §B.2 the four mirrors of region bytes ----

/// The 64-byte control-region header at the region base (marker, then eight `u32`s).
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ControlRegionHeader {
    pub marker: [u8; 32],
    pub layout_version: u32,
    pub control_slot_count: u32,
    pub slot_size: u32,
    pub slot_header_size: u32,
    pub host_slot_base: u32,
    pub host_slot_count: u32,
    pub ctl_slot_base: u32,
    pub ctl_slot_count: u32,
}

/// The 16-byte header of every control slot (`ProcessMemoryBridge.cs` `SequenceOffset`/`LengthOffset` :12-13).
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ControlSlotHeader {
    pub sender_id: u64,
    pub seq: i32,
    pub length: i32,
}

/// The 64-byte bulk sub-region header at `bulk_region_base_offset()`.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct BulkRegionHeader {
    pub marker: [u8; 32],
    pub layout_version: u32,
    pub bulk_slot_count: u32,
    pub bulk_slot_size: u32,
    pub bulk_slot_header_size: u32,
    pub _reserved: [u8; 16],
}

/// The 32-byte header of every bulk slot.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct BulkSlotHeader {
    pub state: u64,
    /// The GUID's bytes as .NET `Guid.ToByteArray` orders them.
    pub correlation_id: [u8; 16],
    pub total_length: i32,
    pub crc32: u32,
}

const _: () = assert!(size_of::<ControlRegionHeader>() == CONTROL_HEADER_SIZE);
const _: () = assert!(size_of::<ControlRegionHeader>() == 64);
const _: () = assert!(offset_of!(ControlRegionHeader, marker) == 0);
const _: () = assert!(offset_of!(ControlRegionHeader, layout_version) == LAYOUT_VERSION_OFFSET);
const _: () = assert!(offset_of!(ControlRegionHeader, layout_version) == 32);
const _: () = assert!(offset_of!(ControlRegionHeader, control_slot_count) == 36);
const _: () = assert!(offset_of!(ControlRegionHeader, slot_size) == 40);
const _: () = assert!(offset_of!(ControlRegionHeader, slot_header_size) == 44);
const _: () = assert!(offset_of!(ControlRegionHeader, host_slot_base) == 48);
const _: () = assert!(offset_of!(ControlRegionHeader, host_slot_count) == 52);
const _: () = assert!(offset_of!(ControlRegionHeader, ctl_slot_base) == 56);
const _: () = assert!(offset_of!(ControlRegionHeader, ctl_slot_count) == 60);

const _: () = assert!(size_of::<ControlSlotHeader>() == SLOT_HEADER_SIZE);
const _: () = assert!(offset_of!(ControlSlotHeader, sender_id) == 0);
const _: () = assert!(offset_of!(ControlSlotHeader, seq) == 8);
const _: () = assert!(offset_of!(ControlSlotHeader, length) == 12);

const _: () = assert!(size_of::<BulkRegionHeader>() == BULK_HEADER_SIZE);
const _: () = assert!(offset_of!(BulkRegionHeader, marker) == 0);
const _: () = assert!(offset_of!(BulkRegionHeader, layout_version) == 32);
const _: () = assert!(offset_of!(BulkRegionHeader, bulk_slot_count) == 36);
const _: () = assert!(offset_of!(BulkRegionHeader, bulk_slot_size) == 40);
const _: () = assert!(offset_of!(BulkRegionHeader, bulk_slot_header_size) == 44);
const _: () = assert!(offset_of!(BulkRegionHeader, _reserved) == 48);

const _: () = assert!(size_of::<BulkSlotHeader>() == BULK_SLOT_HEADER_SIZE);
const _: () = assert!(offset_of!(BulkSlotHeader, state) == 0);
const _: () = assert!(offset_of!(BulkSlotHeader, correlation_id) == 8);
const _: () = assert!(offset_of!(BulkSlotHeader, total_length) == 24);
const _: () = assert!(offset_of!(BulkSlotHeader, crc32) == 28);

impl ControlRegionHeader {
    /// The header the mod writes (`BridgeMemoryBuffer.WriteControlRegionHeader`) behind the control marker.
    pub fn v11() -> Self {
        Self {
            marker: control_marker(),
            layout_version: LAYOUT_VERSION,
            control_slot_count: CONTROL_SLOT_COUNT as u32,
            slot_size: SLOT_SIZE as u32,
            slot_header_size: SLOT_HEADER_SIZE as u32,
            host_slot_base: HOST_SLOT_BASE as u32,
            host_slot_count: HOST_SLOT_COUNT as u32,
            ctl_slot_base: CTL_SLOT_BASE as u32,
            ctl_slot_count: CTL_SLOT_COUNT as u32,
        }
    }

    pub fn to_bytes(&self) -> [u8; CONTROL_HEADER_SIZE] {
        let mut out = [0u8; CONTROL_HEADER_SIZE];
        out[..32].copy_from_slice(&self.marker);
        let words = [
            self.layout_version,
            self.control_slot_count,
            self.slot_size,
            self.slot_header_size,
            self.host_slot_base,
            self.host_slot_count,
            self.ctl_slot_base,
            self.ctl_slot_count,
        ];
        for (i, w) in words.iter().enumerate() {
            out[32 + i * 4..36 + i * 4].copy_from_slice(&w.to_le_bytes());
        }
        out
    }

    pub fn from_bytes(b: &[u8; CONTROL_HEADER_SIZE]) -> Self {
        let mut marker = [0u8; 32];
        marker.copy_from_slice(&b[..32]);
        let w = |i: usize| le_u32(b, 32 + i * 4);
        Self {
            marker,
            layout_version: w(0),
            control_slot_count: w(1),
            slot_size: w(2),
            slot_header_size: w(3),
            host_slot_base: w(4),
            host_slot_count: w(5),
            ctl_slot_base: w(6),
            ctl_slot_count: w(7),
        }
    }
}

impl ControlSlotHeader {
    pub fn to_bytes(&self) -> [u8; SLOT_HEADER_SIZE] {
        let mut out = [0u8; SLOT_HEADER_SIZE];
        out[..8].copy_from_slice(&self.sender_id.to_le_bytes());
        out[8..12].copy_from_slice(&self.seq.to_le_bytes());
        out[12..16].copy_from_slice(&self.length.to_le_bytes());
        out
    }

    pub fn from_bytes(b: &[u8]) -> Option<Self> {
        if b.len() < SLOT_HEADER_SIZE {
            return None;
        }
        Some(Self { sender_id: le_u64(b, 0), seq: le_u32(b, 8) as i32, length: le_u32(b, 12) as i32 })
    }
}

impl BulkRegionHeader {
    pub fn v11() -> Self {
        Self {
            marker: bulk_marker(),
            layout_version: LAYOUT_VERSION,
            bulk_slot_count: BULK_SLOT_COUNT as u32,
            bulk_slot_size: BULK_SLOT_SIZE as u32,
            bulk_slot_header_size: BULK_SLOT_HEADER_SIZE as u32,
            _reserved: [0u8; 16],
        }
    }

    pub fn to_bytes(&self) -> [u8; BULK_HEADER_SIZE] {
        let mut out = [0u8; BULK_HEADER_SIZE];
        out[..32].copy_from_slice(&self.marker);
        out[32..36].copy_from_slice(&self.layout_version.to_le_bytes());
        out[36..40].copy_from_slice(&self.bulk_slot_count.to_le_bytes());
        out[40..44].copy_from_slice(&self.bulk_slot_size.to_le_bytes());
        out[44..48].copy_from_slice(&self.bulk_slot_header_size.to_le_bytes());
        out[48..64].copy_from_slice(&self._reserved);
        out
    }

    /// The oracle's `ValidateBulkSubRegion` checks: marker, then version, count, size, header size.
    pub fn is_valid_v11(b: &[u8]) -> bool {
        if b.len() < BULK_HEADER_SIZE {
            return false;
        }
        b[..32] == bulk_marker()
            && le_u32(b, 32) == LAYOUT_VERSION
            && le_u32(b, 36) == BULK_SLOT_COUNT as u32
            && le_u32(b, 40) == BULK_SLOT_SIZE as u32
            && le_u32(b, 44) == BULK_SLOT_HEADER_SIZE as u32
    }
}

impl BulkSlotHeader {
    pub fn to_bytes(&self) -> [u8; BULK_SLOT_HEADER_SIZE] {
        let mut out = [0u8; BULK_SLOT_HEADER_SIZE];
        out[..8].copy_from_slice(&self.state.to_le_bytes());
        out[8..24].copy_from_slice(&self.correlation_id);
        out[24..28].copy_from_slice(&self.total_length.to_le_bytes());
        out[28..32].copy_from_slice(&self.crc32.to_le_bytes());
        out
    }

    pub fn from_bytes(b: &[u8]) -> Option<Self> {
        if b.len() < BULK_SLOT_HEADER_SIZE {
            return None;
        }
        let mut correlation_id = [0u8; 16];
        correlation_id.copy_from_slice(&b[8..24]);
        Some(Self { state: le_u64(b, 0), correlation_id, total_length: le_u32(b, 24) as i32, crc32: le_u32(b, 28) })
    }
}

/// The six bulk-slot stamps as one state machine (§C.3): `Empty -> HostWriting -> HostReady -> HostConsumed`,
/// the same for `Ctl...`.
#[repr(u64)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BulkState {
    Empty = BULK_STATE_EMPTY,
    HostWriting = BULK_STATE_HOST_WRITING,
    HostReady = BULK_STATE_HOST_READY,
    HostConsumed = BULK_STATE_HOST_CONSUMED,
    CtlWriting = BULK_STATE_CTL_WRITING,
    CtlReady = BULK_STATE_CTL_READY,
    CtlConsumed = BULK_STATE_CTL_CONSUMED,
}

const _: () = assert!(BulkState::Empty as u64 == 0);
const _: () = assert!(BulkState::HostWriting as u64 == 0xC011C01100000001);
const _: () = assert!(BulkState::HostReady as u64 == 0xC011C01100000002);
const _: () = assert!(BulkState::HostConsumed as u64 == 0xC011C01100000003);
const _: () = assert!(BulkState::CtlWriting as u64 == 0xC011C01100000011);
const _: () = assert!(BulkState::CtlReady as u64 == 0xC011C01100000012);
const _: () = assert!(BulkState::CtlConsumed as u64 == 0xC011C01100000013);

impl BulkState {
    pub fn from_stamp(stamp: u64) -> Option<Self> {
        [
            Self::Empty,
            Self::HostWriting,
            Self::HostReady,
            Self::HostConsumed,
            Self::CtlWriting,
            Self::CtlReady,
            Self::CtlConsumed,
        ]
        .into_iter()
        .find(|s| *s as u64 == stamp)
    }
}

// ---- offsets (const fn, checked arithmetic) ----

/// `CONTROL_HEADER_SIZE + i * SLOT_SIZE`; `None` on overflow.
pub const fn slot_byte_offset(slot_index: usize) -> Option<usize> {
    match slot_index.checked_mul(SLOT_SIZE) {
        Some(v) => v.checked_add(CONTROL_HEADER_SIZE),
        None => None,
    }
}

pub const fn bulk_region_base_offset() -> usize {
    CONTROL_REGION_SIZE
}

/// `bulk_region_base_offset() + BULK_HEADER_SIZE + i * BULK_SLOT_SIZE`; `None` on overflow.
pub const fn bulk_slot_byte_offset(slot_index: usize) -> Option<usize> {
    match slot_index.checked_mul(BULK_SLOT_SIZE) {
        Some(v) => match v.checked_add(BULK_HEADER_SIZE) {
            Some(w) => w.checked_add(bulk_region_base_offset()),
            None => None,
        },
        None => None,
    }
}

/// A control slot index proved to be inside its band.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ControlSlot(usize);

impl ControlSlot {
    /// Host band (mod writes): `0..HOST_SLOT_COUNT` within the band.
    pub fn host(index_in_band: usize) -> Result<Self, BridgeError> {
        if index_in_band >= HOST_SLOT_COUNT {
            return Err(BridgeError::SlotOutOfBand { slot: HOST_SLOT_BASE + index_in_band });
        }
        Ok(Self(HOST_SLOT_BASE + index_in_band))
    }

    /// Controller band (driver writes): `0..CTL_SLOT_COUNT` within the band.
    pub fn ctl(index_in_band: usize) -> Result<Self, BridgeError> {
        if index_in_band >= CTL_SLOT_COUNT {
            return Err(BridgeError::SlotOutOfBand { slot: CTL_SLOT_BASE + index_in_band });
        }
        Ok(Self(CTL_SLOT_BASE + index_in_band))
    }

    pub fn index(self) -> usize {
        self.0
    }

    pub fn byte_offset(self) -> usize {
        // In-band indices are < CONTROL_SLOT_COUNT, so this never overflows.
        CONTROL_HEADER_SIZE + self.0 * SLOT_SIZE
    }
}

/// A bulk slot index proved to be in range.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct BulkSlot(usize);

impl BulkSlot {
    pub fn new(index: usize) -> Result<Self, BridgeError> {
        if index >= BULK_SLOT_COUNT {
            return Err(BridgeError::SlotOutOfBand { slot: index });
        }
        Ok(Self(index))
    }

    pub fn index(self) -> usize {
        self.0
    }

    pub fn byte_offset(self) -> usize {
        bulk_region_base_offset() + BULK_HEADER_SIZE + self.0 * BULK_SLOT_SIZE
    }
}

// ---- markers ----

/// `BridgeSharedRegionLayout.ControlMarkerByte(i)`, evaluated in C#'s 32-bit signed arithmetic (the right
/// operand of the XOR goes negative from i = 56; the marker uses i < 32).
pub const fn control_marker_byte(i: usize) -> u8 {
    let index = i as i32;
    (((0x53 + (index * 0x2D) + ((index & 3) * 0x11)) ^ (0xA7 - (index * 3))) & 0xFF) as u8
}

/// `BridgeSharedRegionLayout.BulkMarkerByte(i)`.
pub const fn bulk_marker_byte(i: usize) -> u8 {
    let index = i as i32;
    (((0x42 + (index * 0x1F) + ((index & 7) * 0x09)) ^ (0x5C - (index * 5))) & 0xFF) as u8
}

pub const fn control_marker() -> [u8; MARKER_LEN] {
    let mut m = [0u8; MARKER_LEN];
    let mut i = 0;
    while i < MARKER_LEN {
        m[i] = control_marker_byte(i);
        i += 1;
    }
    m
}

pub const fn bulk_marker() -> [u8; MARKER_LEN] {
    let mut m = [0u8; MARKER_LEN];
    let mut i = 0;
    while i < MARKER_LEN {
        m[i] = bulk_marker_byte(i);
        i += 1;
    }
    m
}

pub(crate) fn le_u32(b: &[u8], at: usize) -> u32 {
    let mut w = [0u8; 4];
    w.copy_from_slice(&b[at..at + 4]);
    u32::from_le_bytes(w)
}

pub(crate) fn le_u64(b: &[u8], at: usize) -> u64 {
    let mut w = [0u8; 8];
    w.copy_from_slice(&b[at..at + 8]);
    u64::from_le_bytes(w)
}

#[cfg(test)]
mod tests {
    use super::*;

    // The C# functions, transcribed from their one-line definitions, compared for every valid index.
    fn cs_slot_byte_offset(i: i32) -> i32 {
        64 + i * 4096
    }
    fn cs_bulk_slot_byte_offset(i: i32) -> i32 {
        (64 + 16 * 4096) + 64 + i * 524288
    }

    #[test]
    fn offsets_match_the_csharp_functions_for_every_valid_index() {
        for i in 0..CONTROL_SLOT_COUNT {
            assert_eq!(slot_byte_offset(i), Some(cs_slot_byte_offset(i as i32) as usize), "slot {i}");
        }
        for i in 0..BULK_SLOT_COUNT {
            assert_eq!(bulk_slot_byte_offset(i), Some(cs_bulk_slot_byte_offset(i as i32) as usize), "bulk {i}");
            assert_eq!(BulkSlot::new(i).map(BulkSlot::byte_offset), Ok(cs_bulk_slot_byte_offset(i as i32) as usize));
        }
        assert_eq!(bulk_region_base_offset(), 65600);
        assert_eq!(slot_byte_offset(usize::MAX), None);
        assert_eq!(bulk_slot_byte_offset(usize::MAX), None);
    }

    #[test]
    fn bands_are_typed() {
        assert_eq!(ControlSlot::host(7).map(ControlSlot::index), Ok(7));
        assert_eq!(ControlSlot::ctl(0).map(ControlSlot::index), Ok(8));
        assert_eq!(ControlSlot::host(8), Err(BridgeError::SlotOutOfBand { slot: 8 }));
        assert_eq!(ControlSlot::ctl(8), Err(BridgeError::SlotOutOfBand { slot: 16 }));
        assert_eq!(BulkSlot::new(4), Err(BridgeError::SlotOutOfBand { slot: 4 }));
        for i in 0..HOST_SLOT_COUNT {
            assert_eq!(ControlSlot::host(i).map(ControlSlot::byte_offset), Ok(cs_slot_byte_offset(i as i32) as usize));
        }
    }

    #[test]
    fn hello_caps_byte_for_byte() {
        assert_eq!(DEFAULT_HELLO_CAPS.as_bytes(), b"multi-slot=16,bulk=4x512k,layout=11,snapshot2=1");
    }

    #[test]
    fn markers_match_known_first_bytes() {
        // i=0: (0x53 ^ 0xA7) = 0xF4; bulk i=0: (0x42 ^ 0x5C) = 0x1E.
        assert_eq!(control_marker()[0], 0xF4);
        assert_eq!(bulk_marker()[0], 0x1E);
        // i=31 exercises the negative right-hand side of the XOR.
        let i = 31i32;
        let expect = (((0x53 + i * 0x2D + (i & 3) * 0x11) ^ (0xA7 - i * 3)) & 0xFF) as u8;
        assert_eq!(control_marker_byte(31), expect);
        let i = 60i32;
        let expect = (((0x53 + i * 0x2D + (i & 3) * 0x11) ^ (0xA7 - i * 3)) & 0xFF) as u8;
        assert_eq!(control_marker_byte(60), expect, "a negative XOR operand, as C# int");
    }

    #[test]
    fn headers_round_trip_and_bulk_state_parses() {
        let h = ControlRegionHeader::v11();
        assert_eq!(ControlRegionHeader::from_bytes(&h.to_bytes()), h);
        assert!(BulkRegionHeader::is_valid_v11(&BulkRegionHeader::v11().to_bytes()));
        let s = ControlSlotHeader { sender_id: SERVER_SENDER_ID, seq: -5, length: 12 };
        assert_eq!(ControlSlotHeader::from_bytes(&s.to_bytes()), Some(s));
        assert_eq!(BulkState::from_stamp(BULK_STATE_CTL_READY), Some(BulkState::CtlReady));
        assert_eq!(BulkState::from_stamp(7), None);
    }
}
