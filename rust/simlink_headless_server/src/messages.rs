#![forbid(unsafe_code)]
//! The v1 message types and their scalar / composite encodings (card 57 §C.1, §D.2).
//!
//! The card keeps the codec (`crate::wire_v1`, one module, §J.6) behind these types. This
//! module holds only the vocabulary: the constants the codec names, the closed refusal-code
//! table, the caps map, the frame types, and the low-level field encodings of §C.1.3-§C.1.4.
//! Nothing here touches a socket or a process.

pub use crate::error::WireError;

/// The public protocol version, belonging to the public socket only (§C.2). It is not the
/// bridge's `23`/`6` nor the sidecar's `17`/`15`; none is renumbered.
pub const PUBLIC_PROTOCOL_VERSION: u64 = 1;
/// Longest v1 line in bytes, the LF excluded (§C.1.2). A longer line is refused
/// `ERR|0|line_too_long|-|` and the connection closes; the reader never buffers past the cap.
pub const PUBLIC_LINE_MAX_BYTES: usize = 4_194_304;
/// The largest raw byte count a single `BULK` chunk may carry before base64 (§F.2). A full
/// chunk's base64 (`4 * ceil(3_145_632 / 3) = 4_194_176`) leaves 128 bytes for the `BULK`
/// header under [`PUBLIC_LINE_MAX_BYTES`].
pub const PUBLIC_BULK_CHUNK_RAW_BYTES: usize = 3_145_632;
pub const PUBLIC_DESCRIPTION_MAX_RAW_BYTES: usize = 512 * 1024;

// ---- literal witnesses: each public cap against the card's own number (F1) ----------
// Mirroring `rust/simlink_bridge/src/layout.rs:76`, these pin the two §C.1.2 / §F.2 numbers so
// doubling a constant is a compile error rather than a green suite: every bound test now also
// speaks the literal, so the crate's headline "no unbounded read on the public port" number
// (SECURITY.md) is proven by its own build, not derived from the constant it checks.
const _: () = assert!(PUBLIC_LINE_MAX_BYTES == 4_194_304);
const _: () = assert!(PUBLIC_BULK_CHUNK_RAW_BYTES == 3_145_632);

/// The public default port (§G.4): card 19's `DEFAULT_WAN_PORT`, and the C# sidecar's
/// `DefaultPort`. During the wave the differential harness moves the sidecar off it.
pub const DEFAULT_WAN_PORT: u16 = sim96_kernel_cleanroom::wan_transport::DEFAULT_WAN_PORT;
/// The idempotency window: a replayed `commandId` inside the last this-many returns the
/// original reply byte for byte (§D.1). Equal to the kernel's `COMMAND_ID_WINDOW` and card 19's
/// `REORDER_WINDOW_FRAMES` by design (card 33 §D.2).
pub const COMMAND_ID_WINDOW: usize = sim96_kernel_cleanroom::runtime_bridge::COMMAND_ID_WINDOW;

/// The absent nullable marker (§C.1.3): an absent field is `~`, distinct from the empty
/// string, which is the empty field.
pub const NULL_FIELD: &str = "~";
/// A boolean true is the field `1` (§C.1.3).
pub const BOOL_TRUE: &str = "1";
/// A boolean false is the field `0` (§C.1.3).
pub const BOOL_FALSE: &str = "0";
/// The field separator of a v1 line (§C.1.1).
pub const FIELD_SEP: char = '|';
/// A bulk field carries `b<bulk_id>` and its value rides the bulk form (§C.1.5).
pub const BULK_REF_PREFIX: char = 'b';

const HEX_DIGITS: [char; 16] = [
    '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'a', 'b', 'c', 'd', 'e', 'f',
];

/// A `Verb` or `Kind` is the PascalCase name of the C# record it replaces (§D.1, §F.1). A
/// refusal `<code>` shares the same token grammar `[A-Za-z][A-Za-z0-9_]*` and, for a code, is
/// at most 64 bytes (§D.2).
pub fn is_identifier(token: &str) -> bool {
    let mut chars = token.chars();
    match chars.next() {
        Some(c) if c.is_ascii_alphabetic() => {}
        _ => return false,
    }
    chars.all(|c| c.is_ascii_alphanumeric() || c == '_')
}

/// Whether `field` is legal between separators: no `|`, CR or LF (§C.1.1).
pub fn is_legal_field(field: &str) -> bool {
    !field.contains(FIELD_SEP) && !field.contains('\r') && !field.contains('\n')
}

/// The lowercase hex of a string's UTF-8 bytes (§C.1.3). The empty string is the empty field.
#[must_use]
pub fn hex_encode(text: &str) -> String { hex_encode_bytes(text.as_bytes()) }

/// Public byte-array scalar: raw bytes as lowercase hexadecimal, without UTF-8 conversion.
#[must_use]
pub fn hex_encode_bytes(bytes: &[u8]) -> String {
    let mut out = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        out.push(HEX_DIGITS[usize::from(byte >> 4)]);
        out.push(HEX_DIGITS[usize::from(byte & 0x0f)]);
    }
    out
}

/// Decode a lowercase-hex scalar back to its UTF-8 text (§C.1.3). An odd length, a non-hex
/// byte, or bytes that are not valid UTF-8 are a [`WireError`].
pub fn hex_decode(field: &str) -> Result<String, WireError> {
    String::from_utf8(hex_decode_bytes(field)?).map_err(|_| WireError::NotUtf8)
}

/// Decode public lowercase hexadecimal to raw bytes. Invalid UTF-8 remains valid byte-array data.
pub fn hex_decode_bytes(field: &str) -> Result<Vec<u8>, WireError> {
    let bytes = field.as_bytes();
    if !bytes.len().is_multiple_of(2) {
        return Err(WireError::BadHex);
    }
    let mut out = Vec::with_capacity(bytes.len() / 2);
    let mut i = 0;
    while i < bytes.len() {
        let hi = hex_nibble(bytes[i])?;
        let lo = hex_nibble(bytes[i + 1])?;
        out.push((hi << 4) | lo);
        i += 2;
    }
    Ok(out)
}

fn hex_nibble(byte: u8) -> Result<u8, WireError> {
    match byte {
        b'0'..=b'9' => Ok(byte - b'0'),
        b'a'..=b'f' => Ok(byte - b'a' + 10),
        _ => Err(WireError::BadHex),
    }
}

/// Encode a decimal `u64` field (§C.1.3: u64 ids and `seq`).
#[must_use]
pub fn enc_u64(value: u64) -> String {
    value.to_string()
}

/// Decode a decimal `u64` field.
pub fn dec_u64(field: &str) -> Result<u64, WireError> {
    field.parse::<u64>().map_err(|_| WireError::BadInt)
}

/// Encode a decimal `u32` field (the `epoch`, §E).
#[must_use]
pub fn enc_u32(value: u32) -> String {
    value.to_string()
}

/// Decode a decimal `u32` field.
pub fn dec_u32(field: &str) -> Result<u32, WireError> {
    field.parse::<u32>().map_err(|_| WireError::BadInt)
}

/// Encode a decimal `i32` field (`playerId` is i32 as today, §C.1.3).
#[must_use]
pub fn enc_i32(value: i32) -> String {
    value.to_string()
}

/// Decode a decimal `i32` field.
pub fn dec_i32(field: &str) -> Result<i32, WireError> {
    field.parse::<i32>().map_err(|_| WireError::BadInt)
}

/// Encode a boolean as `0`/`1` (§C.1.3).
#[must_use]
pub const fn enc_bool(value: bool) -> &'static str {
    if value {
        BOOL_TRUE
    } else {
        BOOL_FALSE
    }
}

/// Decode a boolean field (§C.1.3).
pub fn dec_bool(field: &str) -> Result<bool, WireError> {
    match field {
        BOOL_TRUE => Ok(true),
        BOOL_FALSE => Ok(false),
        _ => Err(WireError::BadBool),
    }
}

/// Encode an optional scalar: `~` when absent, else the value's own encoding (§C.1.3).
#[must_use]
pub fn enc_opt(value: Option<&str>) -> String {
    match value {
        Some(inner) => inner.to_string(),
        None => NULL_FIELD.to_string(),
    }
}

/// Decode an optional scalar: `None` for `~`, else the field verbatim (§C.1.3). Callers decode
/// the inner field with the scalar rule for its type.
pub fn dec_opt(field: &str) -> Option<&str> {
    if field == NULL_FIELD {
        None
    } else {
        Some(field)
    }
}

/// A record is `(f1;f2;…)`, its fields in declaration order (§C.1.4). The caller supplies each
/// field already encoded by these rules; every item must be a legal composite item.
#[must_use]
pub fn enc_record(items: &[String]) -> String {
    let mut out = String::with_capacity(2 + items.iter().map(String::len).sum::<usize>());
    out.push('(');
    for (i, item) in items.iter().enumerate() {
        if i > 0 {
            out.push(';');
        }
        out.push_str(item);
    }
    out.push(')');
    out
}

/// A list is `[i1,i2,…]` (§C.1.4). Each item is already encoded by these rules.
#[must_use]
pub fn enc_list(items: &[String]) -> String {
    let mut out = String::with_capacity(2 + items.iter().map(String::len).sum::<usize>());
    out.push('[');
    for (i, item) in items.iter().enumerate() {
        if i > 0 {
            out.push(',');
        }
        out.push_str(item);
    }
    out.push(']');
    out
}

/// Fallible record encoding for callers accepting already-encoded, untrusted
/// items. The original encoders retain their String API for trusted callers.
pub fn try_enc_record(items: &[String]) -> Result<String, WireError> {
    for item in items { validate_composite_item(item)?; }
    Ok(enc_record(items))
}

/// Fallible list encoding; a raw separator cannot silently add another item.
pub fn try_enc_list(items: &[String]) -> Result<String, WireError> {
    for item in items { validate_composite_item(item)?; }
    Ok(enc_list(items))
}

fn validate_composite_item(item: &str) -> Result<(), WireError> {
    if !is_legal_field(item) { return Err(WireError::FieldSeparator); }
    let mut expected = Vec::new();
    for ch in item.chars() {
        match ch {
            '(' => expected.push(')'),
            '[' => expected.push(']'),
            ')' | ']' if expected.pop() != Some(ch) => return Err(WireError::BadComposite),
            ';' | ',' if expected.is_empty() => return Err(WireError::BadComposite),
            _ => {}
        }
    }
    if expected.is_empty() { Ok(()) } else { Err(WireError::BadComposite) }
}

/// Split a record `(f1;f2;…)` into its items (§C.1.4). Splitting is depth-aware so a record can
/// hold a nested composite: `dec_record`/`dec_list` decode each item by these rules again.
pub fn dec_record(field: &str) -> Result<Vec<String>, WireError> {
    let inner = delimited(field, '(', ')')?;
    split_items(inner, ';')
}

/// Split a list `[i1,i2,…]` into its items (§C.1.4). Depth-aware, so a list may hold records.
pub fn dec_list(field: &str) -> Result<Vec<String>, WireError> {
    let inner = delimited(field, '[', ']')?;
    split_items(inner, ',')
}

fn delimited(field: &str, open: char, close: char) -> Result<&str, WireError> {
    let mut chars = field.chars();
    if chars.next() != Some(open) {
        return Err(WireError::BadComposite);
    }
    if chars.next_back() != Some(close) {
        return Err(WireError::BadComposite);
    }
    Ok(chars.as_str())
}

/// Split a composite interior into its items, splitting only at depth 0 (§C.1.4, F2).
///
/// `(`/`[` raise the nesting depth and `)`/`]` lower it; the separator cuts only where the depth
/// is zero, so `([(1;2),3];4)` stays two items and the round trip through `enc_record`/`enc_list`
/// is exact at any depth. A scalar is hex, decimal, `0`/`1`, `~` or a PascalCase name, so it never
/// carries a raw bracket or separator — a bracket seen here is always structural, and a bracket or
/// separator *inside* a scalar arrives hex-encoded and cannot move the depth. Malformed nesting (a
/// stray or wrong-kind closer, or an unclosed opener) is a [`WireError::BadComposite`], never a
/// panic.
fn split_items(inner: &str, sep: char) -> Result<Vec<String>, WireError> {
    if inner.is_empty() {
        return Ok(Vec::new());
    }
    // A stack of the closer each open bracket expects, so `( ... ]` is malformed, not mis-split.
    let mut openers: Vec<char> = Vec::new();
    let mut items = Vec::new();
    let mut start = 0usize;
    for (idx, ch) in inner.char_indices() {
        match ch {
            '(' => openers.push(')'),
            '[' => openers.push(']'),
            ')' | ']' => match openers.pop() {
                Some(expected) if expected == ch => {}
                _ => return Err(WireError::BadComposite),
            },
            _ => {}
        }
        if ch == sep && openers.is_empty() {
            items.push(inner[start..idx].to_string());
            start = idx + ch.len_utf8();
        }
    }
    if !openers.is_empty() {
        return Err(WireError::BadComposite);
    }
    items.push(inner[start..].to_string());
    Ok(items)
}

/// A `b<bulk_id>` field (§C.1.5, §F.2).
#[must_use]
pub fn enc_bulk_ref(bulk_id: u64) -> String {
    format!("{BULK_REF_PREFIX}{bulk_id}")
}

/// Decode a field as a bulk reference, returning its `bulk_id`. A field that is not a
/// `b<digits>` reference is not bulk (§C.1.5).
pub fn dec_bulk_ref(field: &str) -> Option<u64> {
    let digits = field.strip_prefix(BULK_REF_PREFIX)?;
    if digits.is_empty() {
        return None;
    }
    digits.parse::<u64>().ok()
}

/// The closed refusal-code table (§D.2). Where the kernel already names the condition the
/// kernel's name is the code and no synonym is added; the gate, session and game conditions the
/// kernel does not name get their own code. A code is a variant of this enum; the wire form is
/// its [`RefusalCode::as_str`], which the codec round-trips.
///
/// Pre-test lookup sentences have one code each; diagnostic prose is not tooltip text.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RefusalCode {
    // The kernel's own names (runtime_bridge.rs:113-119).
    UnauthorizedClientInsertion,
    ClientWorldServiceSuppressed,
    NativeOracleOwned,
    SlotAlreadyReserved,
    HelloRejected,
    BadLine,
    ClientReselectForbidden,
    // The gate and session conditions the kernel does not name (§D.1, §D.2, §E).
    NotJoined,
    WrongPlayer,
    Superseded,
    ClientNativeModesOff,
    IdSpaceFull,
    // Game refusals (§D.2).
    TestFailed,
    SimNotFound,
    TargetNotFound,
    NoMatchingInteraction,
    PickedObjectNotFound,
    InteractionNotFound,
    QueueRejected,
    Dialog,
    ObjectInUse,
    RouteFailed,
    FailedToStart,
    NotRun,
    // The front door's own codes (§D.2, §C.1.2, §F.2).
    LineTooLong,
    BulkMalformed,
}

impl RefusalCode {
    /// The wire token for this code (§D.2). `native_oracle_owned` is the kernel's snake_case
    /// name kept verbatim; the rest are the kernel's or the controller's PascalCase names.
    #[must_use]
    pub fn as_str(self) -> &'static str {
        match self {
            Self::UnauthorizedClientInsertion => "UnauthorizedClientInsertion",
            Self::ClientWorldServiceSuppressed => "ClientWorldServiceSuppressed",
            Self::NativeOracleOwned => "native_oracle_owned",
            Self::SlotAlreadyReserved => "SlotAlreadyReserved",
            Self::HelloRejected => "hello_rejected",
            Self::BadLine => "bad_line",
            Self::ClientReselectForbidden => "ClientReselectForbidden",
            Self::NotJoined => "NotJoined",
            Self::WrongPlayer => "WrongPlayer",
            Self::Superseded => "Superseded",
            Self::ClientNativeModesOff => "ClientNativeModesOff",
            Self::IdSpaceFull => "IdSpaceFull",
            Self::TestFailed => "TestFailed",
            Self::SimNotFound => "SimNotFound",
            Self::TargetNotFound => "TargetNotFound",
            Self::NoMatchingInteraction => "NoMatchingInteraction",
            Self::PickedObjectNotFound => "PickedObjectNotFound",
            Self::InteractionNotFound => "InteractionNotFound",
            Self::QueueRejected => "queue_rejected",
            Self::Dialog => "dialog",
            Self::ObjectInUse => "ObjectInUse",
            Self::RouteFailed => "RouteFailed",
            Self::FailedToStart => "FailedToStart",
            Self::NotRun => "NotRun",
            Self::LineTooLong => "line_too_long",
            Self::BulkMalformed => "bulk_malformed",
        }
    }

    /// The code named by a wire token, if this closed table has it.
    #[must_use]
    pub fn parse(token: &str) -> Option<Self> {
        Self::ALL.iter().copied().find(|c| c.as_str() == token)
    }

    /// Every code in the table, for the gate-3 artifact and its coverage test.
    pub const ALL: [Self; 26] = [
        Self::UnauthorizedClientInsertion,
        Self::ClientWorldServiceSuppressed,
        Self::NativeOracleOwned,
        Self::SlotAlreadyReserved,
        Self::HelloRejected,
        Self::BadLine,
        Self::ClientReselectForbidden,
        Self::NotJoined,
        Self::WrongPlayer,
        Self::Superseded,
        Self::ClientNativeModesOff,
        Self::IdSpaceFull,
        Self::TestFailed,
        Self::SimNotFound,
        Self::TargetNotFound,
        Self::NoMatchingInteraction,
        Self::PickedObjectNotFound,
        Self::InteractionNotFound,
        Self::QueueRejected,
        Self::Dialog,
        Self::ObjectInUse,
        Self::RouteFailed,
        Self::FailedToStart,
        Self::NotRun,
        Self::LineTooLong,
        Self::BulkMalformed,
    ];
}

/// The comma-separated `key=value` caps of a `HELLO` (§C.2). Kept in declared order so a
/// replay is byte-stable.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Caps {
    entries: Vec<(String, String)>,
}

impl Caps {
    /// Parse a caps field (`key=value` pairs separated by `,`; an empty field is no caps).
    pub fn parse(field: &str) -> Result<Self, WireError> {
        let mut entries = Vec::new();
        if field.is_empty() {
            return Ok(Self { entries });
        }
        for part in field.split(',') {
            let (key, value) = part
                .split_once('=')
                .ok_or(WireError::BadIdentifier)?;
            if key.is_empty() {
                return Err(WireError::BadIdentifier);
            }
            entries.push((key.to_string(), value.to_string()));
        }
        Ok(Self { entries })
    }

    /// The value of `key`, if declared.
    #[must_use]
    pub fn get(&self, key: &str) -> Option<&str> {
        self.entries
            .iter()
            .find(|(k, _)| k == key)
            .map(|(_, v)| v.as_str())
    }

    /// Append or replace one cap.
    pub fn set(&mut self, key: &str, value: &str) {
        match self.entries.iter_mut().find(|(k, _)| k == key) {
            Some(slot) => slot.1 = value.to_string(),
            None => self.entries.push((key.to_string(), value.to_string())),
        }
    }

    /// Encode back to the caps field.
    #[must_use]
    pub fn encode(&self) -> String {
        self.entries
            .iter()
            .map(|(k, v)| format!("{k}={v}"))
            .collect::<Vec<_>>()
            .join(",")
    }

    /// Whether no caps are declared.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }
}

/// A client `HELLO|1|<caps>` (§C.2).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Hello {
    /// The declared protocol version; the front door accepts only [`PUBLIC_PROTOCOL_VERSION`].
    pub version: u64,
    /// The declared client caps.
    pub caps: Caps,
}

/// A server `WELCOME|1|<epoch>|<server caps>` (§C.2).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Welcome {
    /// Always [`PUBLIC_PROTOCOL_VERSION`].
    pub version: u64,
    /// The u32 stream epoch, new per server process start (§C.2, §E).
    pub epoch: u32,
    /// The server caps field.
    pub server_caps: String,
}

/// A client `CMD|<corr>|<Verb>|<commandId>|<playerId>|<args…>` (§D.1).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Command {
    /// Per-connection, strictly increasing for a new command; a retry reuses it (§D.1).
    pub corr: u64,
    /// The PascalCase name of the C# record this command replaces (§D.1).
    pub verb: String,
    /// The client's idempotency key, deduped over [`COMMAND_ID_WINDOW`] (§D.1).
    pub command_id: u64,
    /// The player this command is for; i32 as today (§C.1.3).
    pub player_id: i32,
    /// The command's arguments, each an already-encoded field (§D.1).
    pub args: Vec<String>,
}

/// A server reply to a command: `ACK|<corr>|<Verb>|<fields…>` (§D.1).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Ack {
    /// The corr of the command this answers.
    pub corr: u64,
    /// The verb acknowledged.
    pub verb: String,
    /// The reply's fields, already encoded (§D.1).
    pub fields: Vec<String>,
}

/// A refusal frame `ERR|<corr>|<code>|<Verb>|<hexText>` (§D.1, §D.2).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ErrFrame {
    /// The refused command's corr, or 0 for a pre-command refusal (§C.1.2, §C.2).
    pub corr: u64,
    /// The refusal reason code (§D.2).
    pub code: RefusalCode,
    /// The refused verb, or `-` when there is none (§C.1.2, §D.1).
    pub verb: String,
    /// The hex of one line of UTF-8 detail text, empty when there is none (§D.2).
    pub hex_text: String,
}

/// An event `EV|<seq>|<tick>|<corr>|<Kind>|<fields…>` (§F.1). Card 10 §C's envelope fields.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Event {
    /// Per-client, from 1 within the epoch (§F.1, §E).
    pub seq: u64,
    /// The server tick (§F.1).
    pub tick: u64,
    /// The command's corr when the event answers one, else 0 (§F.1).
    pub corr: u64,
    /// The PascalCase name of the C# `ServerEvent` record it replaces (§F.1).
    pub kind: String,
    /// The record's fields in positional order (§C.1, §F.1).
    pub fields: Vec<String>,
}

/// A `BULK|<bulk_id>|<index>|<count>|<b64>` chunk (§F.2).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct BulkChunk {
    /// Per connection, strictly increasing (§F.2).
    pub bulk_id: u64,
    /// 0 to `count - 1`, ascending within one bulk with no gap (§F.2).
    pub index: u64,
    /// The chunk count of the whole bulk; the same on every chunk (§F.2).
    pub count: u64,
    /// RFC 4648 §4 base64 with padding of the chunk's bytes; the empty field is an empty chunk
    /// (§F.2).
    pub b64: String,
}

/// The menu-refusal recorder line `Rf|actor|target|hexType|hexName|<0 or 1>|<code>|<hexText>`
/// (§D.2), relayed opaquely in the presentation stream. A six-field line reads as
/// `(TestFailed, empty)`.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct MenuRefusal {
    /// The actor id field.
    pub actor: String,
    /// The target id field.
    pub target: String,
    /// The hex-encoded interaction type.
    pub hex_type: String,
    /// The hex-encoded interaction name.
    pub hex_name: String,
    /// `true` for a grey refusal (`1`), `false` for a clear (`0`); a clear sends both
    /// code and text empty (§D.2).
    pub refused: bool,
    /// The refusal code, present on the eight-field form (§D.2).
    pub code: Option<RefusalCode>,
    /// The hex detail text (§D.2).
    pub hex_text: String,
}
