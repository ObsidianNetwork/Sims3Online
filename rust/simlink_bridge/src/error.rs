//! The crate's one error type (card 56 §B.3, §C, §D U4, §E.3; the surface `TRAIT_CONTRACT.md` "Card 56"
//! names). Refusals are named and relayed verbatim (card 33 §C); a short read, a failed write, a stale stamp
//! or a closed process is a typed error, never a zero fill or a retried overwrite.
#![forbid(unsafe_code)]

use core::fmt;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum BridgeError {
    /// Discovery (or the HELLO wait) found no layout-v11 region before the caller's deadline. Wire name
    /// `region_not_found`.
    RegionNotFound,
    /// The process exited (or never existed) while attaching, scanning or exchanging. `process_exited`.
    ProcessExited,
    /// Fewer bytes than asked were copied (`got` is 0 when the first page is unmapped).
    ShortRead { addr: u32, want: usize, got: usize },
    /// `WriteProcessMemory` (or the platform's write) failed or wrote fewer bytes; never retried.
    WriteFailed { addr: u32 },
    /// A bulk slot whose stamp is not the one the operation needs; nothing is consumed.
    StaleStamp { slot: usize },
    /// A slot index outside its band.
    SlotOutOfBand { slot: usize },
    /// A bulk payload over `BULK_SLOT_PAYLOAD_MAX`; never truncated.
    BulkPayloadTooLarge { len: usize },
    /// A bulk payload whose CRC-32 does not match its header or its `BULK_REF`.
    BulkCrcMismatch { slot: usize },
    /// A §E.3 verb the Rust driver does not relay (`APPLY_INTERACTION_QUEUE`, `APPLY_INTERACTIONS`,
    /// `APPLY_FORCE_ALIGN`; Main ruling, `docs/decision-log.md` 2026-11-01T01:06:29Z).
    RefusedLegacyVerb(&'static str),
    /// The handle lacks the access right the call needs (a mis-wired or query-only handle).
    AccessDenied { os_code: u32 },
    /// `addr + len` does not fit the 32-bit guest address space.
    AddressOverflow { addr: u32, len: usize },
    /// A query of the address space failed for a reason other than its end.
    QueryFailed { addr: u32, os_code: u32 },
    /// The platform has no implementation (any non-Windows host in this wave; §G row 9).
    Unsupported,
    /// A control line over `SLOT_PAYLOAD_MAX` bytes (the oracle throws the same).
    FrameTooLarge { len: usize },
    /// The local control FIFO is full; this submission spent no sequence.
    CommandQueueFull { limit: usize },
    /// Signed native sequence space is exhausted; reconnect before submitting more.
    SequenceExhausted,
    /// A deferred write failed. Attribution stays with the command that failed,
    /// even when another sender or poll happened to flush it.
    CommandWriteFailed { sequence: i32, correlation: Option<String>, cause: Box<BridgeError> },
    /// No ctl bulk slot is empty or consumed.
    NoWritableBulkSlot,
    /// A bulk header whose correlation id or length does not match the reply that named it.
    BulkMismatch { slot: usize, what: &'static str },
    /// The driver has no layout-v11 session (bulk slots need one).
    NotConnected,
    /// A HELLO whose hex fields do not decode (the oracle's `IsConnectHandshakeHello` throws on it).
    MalformedHello,
}

impl BridgeError {
    /// The stable machine-readable name (card 33 §C), relayed verbatim.
    pub fn name(&self) -> String {
        match self {
            Self::RegionNotFound => "region_not_found".to_string(),
            Self::ProcessExited => "process_exited".to_string(),
            Self::RefusedLegacyVerb(wire) => format!("refused_legacy_verb:{wire}"),
            Self::ShortRead { .. } => "short_read".to_string(),
            Self::WriteFailed { .. } => "write_failed".to_string(),
            Self::StaleStamp { .. } => "stale_stamp".to_string(),
            Self::SlotOutOfBand { .. } => "slot_out_of_band".to_string(),
            Self::BulkPayloadTooLarge { .. } => "bulk_payload_too_large".to_string(),
            Self::BulkCrcMismatch { .. } => "bulk_crc_mismatch".to_string(),
            Self::AccessDenied { .. } => "access_denied".to_string(),
            Self::AddressOverflow { .. } => "address_overflow".to_string(),
            Self::QueryFailed { .. } => "query_failed".to_string(),
            Self::Unsupported => "unsupported".to_string(),
            Self::FrameTooLarge { .. } => "frame_too_large".to_string(),
            Self::CommandQueueFull { .. } => "command_queue_full".to_string(),
            Self::SequenceExhausted => "sequence_exhausted".to_string(),
            Self::CommandWriteFailed { .. } => "command_write_failed".to_string(),
            Self::NoWritableBulkSlot => "no_writable_bulk_slot".to_string(),
            Self::BulkMismatch { .. } => "bulk_mismatch".to_string(),
            Self::NotConnected => "not_connected".to_string(),
            Self::MalformedHello => "malformed_hello".to_string(),
        }
    }
}

impl fmt::Display for BridgeError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::ShortRead { addr, want, got } => write!(f, "short_read at 0x{addr:08X}: {got} of {want} bytes"),
            Self::WriteFailed { addr } => write!(f, "write_failed at 0x{addr:08X}"),
            Self::StaleStamp { slot } => write!(f, "stale_stamp on bulk slot {slot}"),
            Self::SlotOutOfBand { slot } => write!(f, "slot_out_of_band: {slot}"),
            Self::BulkPayloadTooLarge { len } => write!(f, "bulk_payload_too_large: {len} bytes (max 524256)"),
            Self::BulkCrcMismatch { slot } => write!(f, "bulk_crc_mismatch on bulk slot {slot}"),
            Self::AccessDenied { os_code } => write!(f, "access_denied (os error {os_code})"),
            Self::AddressOverflow { addr, len } => write!(f, "address_overflow: 0x{addr:08X}+{len}"),
            Self::QueryFailed { addr, os_code } => write!(f, "query_failed at 0x{addr:08X} (os error {os_code})"),
            Self::FrameTooLarge { len } => write!(f, "frame_too_large: {len} bytes (max 4080)"),
            Self::CommandQueueFull { limit } => write!(f, "command_queue_full: limit {limit}"),
            Self::CommandWriteFailed { sequence, correlation, cause } =>
                write!(f, "command_write_failed: seq={sequence} corr={} {cause}", correlation.as_deref().unwrap_or("-")),
            Self::BulkMismatch { slot, what } => write!(f, "bulk_mismatch on bulk slot {slot}: {what}"),
            other => f.write_str(&other.name()),
        }
    }
}

impl std::error::Error for BridgeError {}
