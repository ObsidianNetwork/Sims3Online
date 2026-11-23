#![forbid(unsafe_code)]
//! The v1 wire codec: one module behind the message types (§J.6, §C-§F).
//!
//! It turns a v1 line into fields and back, enforcing the §C.1 line rules, and knows every
//! frame of the grammar: the §C.2 hello, the §D.1 command / reply / refusal, the §D.2
//! refusal-reason pair, the §F.1 event, and the §F.2 chunked bulk form. It never touches a
//! socket or a process; the transport and the session sit on top of it.

use crate::error::WireError;
use crate::messages::{
    is_identifier, is_legal_field, Ack, BulkChunk, Caps, Command, ErrFrame, Event, Hello,
    MenuRefusal, RefusalCode, Welcome, PUBLIC_LINE_MAX_BYTES, PUBLIC_PROTOCOL_VERSION,
};

use sim96_kernel_cleanroom::runtime_bridge::hello_verdict;
use sim96_kernel_cleanroom::wan_transport::{ClientEnv, HelloRejection, RoleTag};

/// A v1 line's frame tag.
pub const TAG_HELLO: &str = "HELLO";
/// `WELCOME`.
pub const TAG_WELCOME: &str = "WELCOME";

/// A lease credential is exactly 128 OS-random bits in lowercase hex.
pub fn valid_resume_token(token: &str) -> bool {
    token.len() == 32 && token.bytes().all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}
/// `CMD`.
pub const TAG_CMD: &str = "CMD";
/// `ACK`.
pub const TAG_ACK: &str = "ACK";
/// `ERR`.
pub const TAG_ERR: &str = "ERR";
/// `EV`.
pub const TAG_EV: &str = "EV";
/// `BULK`.
pub const TAG_BULK: &str = "BULK";
/// `Rf`.
pub const TAG_RF: &str = "Rf";
/// The `JoinSession` verb: the only command an unjoined connection may send on the public socket
/// besides the controller's read-only requests (§D.1, §E).
pub const TAG_JOIN_SESSION: &str = "JoinSession";
/// The read-only `RequestWorld` request an unjoined connection may send (§D.1 class `K`'s join
/// hook; §E; `ClientListenerServer.cs:1525`).
pub const TAG_REQUEST_WORLD: &str = "RequestWorld";
/// The read-only `RequestSnapshot` request an unjoined connection may send (§E;
/// `ClientListenerServer.cs:1526`).
pub const TAG_REQUEST_SNAPSHOT: &str = "RequestSnapshot";
/// The read-only `RequestSimDescription` request an unjoined connection may send (§E;
/// `ClientListenerServer.cs:1526`).
pub const TAG_REQUEST_SIM_DESCRIPTION: &str = "RequestSimDescription";
/// The commands an unjoined connection may send on the public socket, whatever its address:
/// `JoinSession` and the three read-only requests the controller's authority gate allows
/// (`ClientListenerServer.cs:1525-1529`; card 57 §E, §H row 8). One named list, so the gate and
/// its tests agree on exactly this set (F3).
pub const UNJOINED_ALLOWED: [&str; 4] = [
    TAG_JOIN_SESSION,
    TAG_REQUEST_WORLD,
    TAG_REQUEST_SNAPSHOT,
    TAG_REQUEST_SIM_DESCRIPTION,
];
/// The `PickFailed` event kind (§D.2).
pub const KIND_PICK_FAILED: &str = "PickFailed";

/// The wire form of a `-` verb on a pre-command refusal (§C.1.2, §D.1).
pub const NO_VERB: &str = "-";

const LF: char = '\n';

/// Assemble a line from its fields: join with `|`, then check §C.1.1 (no field carries a
/// separator) and §C.1.2 (the content, LF excluded, fits the cap). Returns the content WITHOUT
/// the trailing LF; [`terminate`] adds it.
pub fn line(fields: &[&str]) -> Result<String, WireError> {
    let mut out = String::new();
    for (i, field) in fields.iter().enumerate() {
        if !is_legal_field(field) {
            return Err(WireError::FieldSeparator);
        }
        if i > 0 {
            out.push('|');
        }
        out.push_str(field);
    }
    if out.len() > PUBLIC_LINE_MAX_BYTES {
        return Err(WireError::LineTooLong { len: out.len() });
    }
    Ok(out)
}

/// [`line`] over already-owned fields, so a frame builder can append argument fields without
/// fighting temporary lifetimes.
fn line_owned(fields: &[String]) -> Result<String, WireError> {
    let refs: Vec<&str> = fields.iter().map(String::as_str).collect();
    line(&refs)
}

/// Append the line feed (§C.1.1).
#[must_use]
pub fn terminate(content: &str) -> String {
    let mut s = String::with_capacity(content.len() + 1);
    s.push_str(content);
    s.push(LF);
    s
}

/// Split raw bytes into fields (§C.1.1, §C.1.2). A trailing LF is required on the wire and a
/// single CR before it is dropped; this also accepts content with neither so a caller that has
/// already framed the line can reuse it. The cap is checked on the content, LF excluded.
pub fn split(raw: &[u8]) -> Result<Vec<String>, WireError> {
    let mut content = raw;
    if content.last() == Some(&b'\n') {
        content = &content[..content.len() - 1];
    } else if content.contains(&b'\n') {
        return Err(WireError::MissingLineFeed);
    }
    if content.last() == Some(&b'\r') {
        content = &content[..content.len() - 1];
    }
    if content.len() > PUBLIC_LINE_MAX_BYTES {
        return Err(WireError::LineTooLong { len: content.len() });
    }
    let text = std::str::from_utf8(content).map_err(|_| WireError::NotUtf8)?;
    if text.contains('\r') || text.contains('\n') {
        return Err(WireError::FieldSeparator);
    }
    Ok(text.split('|').map(str::to_string).collect())
}

fn need(fields: &[String], frame: &'static str, min: usize) -> Result<(), WireError> {
    if fields.len() < min {
        return Err(WireError::TooFewFields {
            frame,
            got: fields.len(),
            min,
        });
    }
    Ok(())
}

fn check_identifier(token: &str) -> Result<(), WireError> {
    if is_identifier(token) {
        Ok(())
    } else {
        Err(WireError::BadIdentifier)
    }
}

/// Encode `HELLO|1|<caps>` (§C.2).
pub fn encode_hello(hello: &Hello) -> Result<String, WireError> {
    line(&[TAG_HELLO, &hello.version.to_string(), &hello.caps.encode()])
}

/// Decode a `HELLO` line's fields.
pub fn decode_hello(fields: &[String]) -> Result<Hello, WireError> {
    need(fields, TAG_HELLO, 3)?;
    if fields[0] != TAG_HELLO {
        return Err(WireError::BadIdentifier);
    }
    let version = fields[1]
        .parse::<u64>()
        .map_err(|_| WireError::BadInt)?;
    let caps = Caps::parse(&fields[2])?;
    Ok(Hello { version, caps })
}

/// Encode `WELCOME|1|<epoch>|<server caps>` (§C.2).
pub fn encode_welcome(welcome: &Welcome) -> Result<String, WireError> {
    line(
        &[
            TAG_WELCOME,
            &welcome.version.to_string(),
            &welcome.epoch.to_string(),
            &welcome.server_caps,
        ],
    )
}

/// Decode a `WELCOME` line's fields.
pub fn decode_welcome(fields: &[String]) -> Result<Welcome, WireError> {
    need(fields, TAG_WELCOME, 4)?;
    if fields[0] != TAG_WELCOME {
        return Err(WireError::BadIdentifier);
    }
    Ok(Welcome {
        version: fields[1].parse::<u64>().map_err(|_| WireError::BadInt)?,
        epoch: fields[2].parse::<u32>().map_err(|_| WireError::BadInt)?,
        server_caps: fields[3].clone(),
    })
}

/// Encode `CMD|<corr>|<Verb>|<commandId>|<playerId>|<args…>` (§D.1).
pub fn encode_command(cmd: &Command) -> Result<String, WireError> {
    check_identifier(&cmd.verb)?;
    let mut fields = vec![
        TAG_CMD.to_string(),
        cmd.corr.to_string(),
        cmd.verb.clone(),
        cmd.command_id.to_string(),
        cmd.player_id.to_string(),
    ];
    fields.extend(cmd.args.iter().cloned());
    line_owned(&fields)
}

/// Decode a `CMD` line's fields.
pub fn decode_command(fields: &[String]) -> Result<Command, WireError> {
    need(fields, TAG_CMD, 5)?;
    if fields[0] != TAG_CMD {
        return Err(WireError::BadIdentifier);
    }
    check_identifier(&fields[2])?;
    Ok(Command {
        corr: fields[1].parse::<u64>().map_err(|_| WireError::BadInt)?,
        verb: fields[2].clone(),
        command_id: fields[3].parse::<u64>().map_err(|_| WireError::BadInt)?,
        player_id: fields[4].parse::<i32>().map_err(|_| WireError::BadInt)?,
        args: fields[5..].to_vec(),
    })
}

/// Encode `ACK|<corr>|<Verb>|<fields…>` (§D.1).
pub fn encode_ack(ack: &Ack) -> Result<String, WireError> {
    check_identifier(&ack.verb)?;
    let mut fields = vec![TAG_ACK.to_string(), ack.corr.to_string(), ack.verb.clone()];
    fields.extend(ack.fields.iter().cloned());
    line_owned(&fields)
}

/// Decode an `ACK` line's fields.
pub fn decode_ack(fields: &[String]) -> Result<Ack, WireError> {
    need(fields, TAG_ACK, 3)?;
    if fields[0] != TAG_ACK {
        return Err(WireError::BadIdentifier);
    }
    check_identifier(&fields[2])?;
    Ok(Ack {
        corr: fields[1].parse::<u64>().map_err(|_| WireError::BadInt)?,
        verb: fields[2].clone(),
        fields: fields[3..].to_vec(),
    })
}

/// Encode `ERR|<corr>|<code>|<Verb>|<hexText>` (§D.1, §D.2).
pub fn encode_err(err: &ErrFrame) -> Result<String, WireError> {
    line(&[
        TAG_ERR,
        &err.corr.to_string(),
        err.code.as_str(),
        &err.verb,
        &err.hex_text,
    ])
}

/// Decode an `ERR` line's fields.
pub fn decode_err(fields: &[String]) -> Result<ErrFrame, WireError> {
    need(fields, TAG_ERR, 5)?;
    if fields[0] != TAG_ERR {
        return Err(WireError::BadIdentifier);
    }
    let code = RefusalCode::parse(&fields[2]).ok_or(WireError::BadIdentifier)?;
    Ok(ErrFrame {
        corr: fields[1].parse::<u64>().map_err(|_| WireError::BadInt)?,
        code,
        verb: fields[3].clone(),
        hex_text: fields[4].clone(),
    })
}

/// Encode `EV|<seq>|<tick>|<corr>|<Kind>|<fields…>` (§F.1).
pub fn encode_event(event: &Event) -> Result<String, WireError> {
    check_identifier(&event.kind)?;
    let mut fields = vec![
        TAG_EV.to_string(),
        event.seq.to_string(),
        event.tick.to_string(),
        event.corr.to_string(),
        event.kind.clone(),
    ];
    fields.extend(event.fields.iter().cloned());
    line_owned(&fields)
}

/// Decode an `EV` line's fields.
pub fn decode_event(fields: &[String]) -> Result<Event, WireError> {
    need(fields, TAG_EV, 5)?;
    if fields[0] != TAG_EV {
        return Err(WireError::BadIdentifier);
    }
    check_identifier(&fields[4])?;
    Ok(Event {
        seq: fields[1].parse::<u64>().map_err(|_| WireError::BadInt)?,
        tick: fields[2].parse::<u64>().map_err(|_| WireError::BadInt)?,
        corr: fields[3].parse::<u64>().map_err(|_| WireError::BadInt)?,
        kind: fields[4].clone(),
        fields: fields[5..].to_vec(),
    })
}

/// Build the `EV|..|PickFailed|<code>|<hexText>` pick-failed frame (§D.2).
#[must_use]
pub fn event_pick_failed(seq: u64, tick: u64, corr: u64, code: RefusalCode, hex_text: &str) -> Event {
    Event {
        seq,
        tick,
        corr,
        kind: KIND_PICK_FAILED.to_string(),
        fields: vec![code.as_str().to_string(), hex_text.to_string()],
    }
}

/// Encode `BULK|<bulk_id>|<index>|<count>|<b64>` (§F.2).
pub fn encode_bulk(chunk: &BulkChunk) -> Result<String, WireError> {
    line(&[
        TAG_BULK,
        &chunk.bulk_id.to_string(),
        &chunk.index.to_string(),
        &chunk.count.to_string(),
        &chunk.b64,
    ])
}

/// Decode a `BULK` line's fields.
pub fn decode_bulk(fields: &[String]) -> Result<BulkChunk, WireError> {
    need(fields, TAG_BULK, 5)?;
    if fields[0] != TAG_BULK {
        return Err(WireError::BadIdentifier);
    }
    let b64 = fields[4].clone();
    b64_decode(&b64)?;
    Ok(BulkChunk {
        bulk_id: fields[1].parse::<u64>().map_err(|_| WireError::BadInt)?,
        index: fields[2].parse::<u64>().map_err(|_| WireError::BadInt)?,
        count: fields[3].parse::<u64>().map_err(|_| WireError::BadInt)?,
        b64,
    })
}

/// Encode the menu-refusal recorder line (§D.2). A clear (`refused == false`) sends both the
/// code and the text empty; a six-field legacy line (no code) is not produced here — use
/// [`decode_menu_refusal`] to read one.
pub fn encode_menu_refusal(rf: &MenuRefusal) -> Result<String, WireError> {
    let (code_field, text_field) = if rf.refused {
        (rf.code.map_or(String::new(), |c| c.as_str().to_string()), rf.hex_text.clone())
    } else {
        (String::new(), String::new())
    };
    line(&[
        TAG_RF,
        &rf.actor,
        &rf.target,
        &rf.hex_type,
        &rf.hex_name,
        if rf.refused { "1" } else { "0" },
        &code_field,
        &text_field,
    ])
}

/// Decode an `Rf` line's fields (§D.2). A line with only six fields (the today's-mod form) reads
/// as `(TestFailed, empty)`; a `…|0` clear reads as no code and empty text.
pub fn decode_menu_refusal(fields: &[String]) -> Result<MenuRefusal, WireError> {
    need(fields, TAG_RF, 6)?;
    if fields[0] != TAG_RF {
        return Err(WireError::BadIdentifier);
    }
    // The merged input recorder uses Rf|seq|ticks|sim|target|reason|hexTooltip,
    // unlike the presentation recorder's six/eight-field Rf. Keep the formats
    // separate: interpreting its reason as a boolean discards TestFailed (=5).
    if fields.len() == 7 && !matches!(fields[5].as_str(), "0" | "1") {
        let reason = fields[5].parse::<u32>().map_err(|_| WireError::BadInt)?;
        crate::messages::hex_decode(&fields[6])?;
        let refused = reason != 0;
        return Ok(MenuRefusal {
            actor: fields[3].clone(), target: fields[4].clone(),
            hex_type: String::new(), hex_name: String::new(), refused,
            code: refused.then_some(RefusalCode::TestFailed),
            hex_text: if refused { fields[6].clone() } else { String::new() },
        });
    }
    let refused = match fields[5].as_str() {
        "1" => true,
        "0" => false,
        _ => return Err(WireError::BadBool),
    };
    let (code, hex_text) = if !refused {
        (None, String::new())
    } else if fields.len() >= 8 {
        let code = if fields[6].is_empty() {
            None
        } else {
            Some(RefusalCode::parse(&fields[6]).ok_or(WireError::BadIdentifier)?)
        };
        (code, fields[7].clone())
    } else if fields.len() == 7 {
        crate::messages::hex_decode(&fields[6])?;
        (Some(RefusalCode::TestFailed), fields[6].clone())
    } else {
        // A six-field grey line is the legacy recorder form: (TestFailed, empty).
        (Some(RefusalCode::TestFailed), String::new())
    };
    Ok(MenuRefusal {
        actor: fields[1].clone(),
        target: fields[2].clone(),
        hex_type: fields[3].clone(),
        hex_name: fields[4].clone(),
        refused,
        code,
        hex_text,
    })
}

/// The hello detail tokens the front door names (§C.2). They are the `<hexText>` of a
/// `hello_rejected`, never a drop or a bare reset (card 33 §C).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum HelloToken {
    VersionUnsupported,
    JsonHello,
    LegacySidecarHello,
    MissingSimlinkRing,
    MissingWorldFromServer,
}

impl HelloToken {
    /// The detail token text (§C.2).
    #[must_use]
    pub fn as_str(self) -> &'static str {
        match self {
            Self::VersionUnsupported => "version_unsupported",
            Self::JsonHello => "json_hello",
            Self::LegacySidecarHello => "legacy_sidecar_hello",
            Self::MissingSimlinkRing => "MissingSimlinkRing",
            Self::MissingWorldFromServer => "MissingWorldFromServer",
        }
    }
}

/// How a first line fails the hello. Either a well-formed `HELLO` whose declaration the §C.2 gate
/// refuses with a named detail token (`hello_rejected`), or a line that is not a decodable v1 line
/// at all — non-UTF-8 or a malformed `caps` field — which is a §C.1 line fault answered `bad_line`
/// (§D.2), not a `version_unsupported` hello. Keeping them apart is what names each refusal by its
/// real reason (card 33 §C; N5).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum HelloRefusal {
    /// A §C.2 `hello_rejected` with this closed-list detail token.
    Rejected(HelloToken),
    /// A §C.1 malformed line (non-UTF-8 or an unparseable caps field), refused `bad_line`.
    BadLine,
}

/// Judge a connection's first line as a hello (§C.2). Returns the parsed [`Hello`] or the reason it
/// is refused: a §C.2 `hello_rejected` detail token for a well-formed HELLO the gate refuses, or
/// `bad_line` for a first line that is not a decodable v1 line (non-UTF-8, a malformed caps field).
/// The card-19 gate moves here: the version is checked, then the kernel's `ClientEnv` is built from
/// the declared `ring`/`wfs`/`role` and the kernel's `hello_verdict` is called; the adapter fills
/// `bridge_version`/`simlink_native_version` with the kernel's own constants, so only the two
/// missing-cap reasons can surface from the kernel.
pub fn judge_hello(raw_first_line: &[u8]) -> Result<Hello, HelloRefusal> {
    let text = match std::str::from_utf8(raw_first_line) {
        // A non-UTF-8 first line is a §C.1 line fault, not a version problem (N5).
        Ok(t) => t,
        Err(_) => return Err(HelloRefusal::BadLine),
    };
    let trimmed = text.trim_end_matches(['\r', '\n']);
    if trimmed.starts_with('{') {
        return Err(HelloRefusal::Rejected(HelloToken::JsonHello));
    }
    if trimmed.starts_with("CONNECT") {
        return Err(HelloRefusal::Rejected(HelloToken::LegacySidecarHello));
    }
    let fields: Vec<&str> = trimmed.split('|').collect();
    if fields.first() != Some(&TAG_HELLO) {
        return Err(HelloRefusal::Rejected(HelloToken::VersionUnsupported));
    }
    // The sidecar's 7-field HELLO|corr|ring|wfs|bv|sv|role declaration (§C.2).
    if fields.len() == 7 {
        return Err(HelloRefusal::Rejected(HelloToken::LegacySidecarHello));
    }
    if fields.get(1).copied() != Some("1") {
        return Err(HelloRefusal::Rejected(HelloToken::VersionUnsupported));
    }
    if fields.len() != 3 { return Err(HelloRefusal::BadLine); }
    let caps_field = fields.get(2).copied().unwrap_or("");
    // A malformed caps field is a line fault (`bad_line`), not a version problem (N5).
    let caps = Caps::parse(caps_field).map_err(|_| HelloRefusal::BadLine)?;
    if caps.get("role").is_some_and(|role| !matches!(role, "client" | "client2" | "client3")) {
        return Err(HelloRefusal::BadLine);
    }
    let env = client_env_from_caps(&caps);
    match hello_verdict(&env) {
        Ok(()) => Ok(Hello {
            version: PUBLIC_PROTOCOL_VERSION,
            caps,
        }),
        Err(HelloRejection::MissingSimlinkRing) => {
            Err(HelloRefusal::Rejected(HelloToken::MissingSimlinkRing))
        }
        Err(HelloRejection::MissingWorldFromServer) => {
            Err(HelloRefusal::Rejected(HelloToken::MissingWorldFromServer))
        }
        // Unreachable in this adapter: `client_env_from_caps` fills `bridge_version` and
        // `simlink_native_version` with the kernel's own constants (§C.2), so neither version
        // rejection can be returned. Kept defensive; a future kernel rejection belongs named at
        // its source in the kernel, not mislabeled here as the ring gate (N5).
        Err(_) => Err(HelloRefusal::Rejected(HelloToken::MissingSimlinkRing)),
    }
}

fn client_env_from_caps(caps: &Caps) -> ClientEnv {
    let role = match caps.get("role") {
        Some("client2") => RoleTag::Client2,
        Some("client3") => RoleTag::Client3,
        Some("server") => RoleTag::Server,
        _ => RoleTag::Client,
    };
    ClientEnv {
        simlink_client_ring: caps.get("ring") == Some("1"),
        world_from_server: caps.get("wfs") == Some("1"),
        role_tag: role,
        // The adapter fills these with the kernel's own constants (§C.2); v1's version check
        // replaces the sidecar-hop version pair.
        bridge_version: sim96_kernel_cleanroom::wan_transport::BRIDGE_PROTOCOL_VERSION,
        simlink_native_version: sim96_kernel_cleanroom::wan_transport::SIMLINK_NATIVE_VERSION,
    }
}

/// Split the `Add`-refused mod error text `<old text> | tooltip: <text>` into the `TestFailed`
/// code and the tooltip hex text (§D.2). When there is no `| tooltip: ` the whole string is the
/// detail text (the test gave no tooltip split).
#[must_use]
pub fn split_add_tooltip(error_text: &str) -> (RefusalCode, String) {
    const SEP: &str = "| tooltip: ";
    match error_text.find(SEP) {
        Some(pos) => {
            let tooltip = error_text[pos + SEP.len()..].trim_start();
            (RefusalCode::TestFailed, crate::messages::hex_encode(tooltip))
        }
        None => (RefusalCode::TestFailed, crate::messages::hex_encode(error_text)),
    }
}

/// Interpret the mod's pre-test lookup errors and Add refusal without exposing
/// diagnostic prose as tooltip text (§D.2). Only a test tooltip carries text.
#[must_use]
pub fn game_refusal(text: &str) -> (RefusalCode, String) {
    for (prefix, code) in [
        ("FailedToStart", RefusalCode::FailedToStart),
        ("NotRun", RefusalCode::NotRun),
    ] {
        if text.starts_with(prefix) {
            return (code, text.split_once("| tooltip: ")
                .map(|(_, tooltip)| crate::messages::hex_encode(tooltip.trim_start()))
                .unwrap_or_default());
        }
    }
    for (prefix, code) in [
        ("Sim not found", RefusalCode::SimNotFound),
        ("Target not found", RefusalCode::TargetNotFound),
        ("No matching interaction", RefusalCode::NoMatchingInteraction),
        ("Picked object not found", RefusalCode::PickedObjectNotFound),
        ("Interaction not found in queue or running", RefusalCode::InteractionNotFound),
        ("ObjectInUse", RefusalCode::ObjectInUse),
        ("RouteFailed", RefusalCode::RouteFailed),
    ] {
        if text.starts_with(prefix) { return (code, String::new()); }
    }
    if text.contains("refused") || text.contains("tooltip:") {
        return (RefusalCode::TestFailed, text.split_once("| tooltip: ")
            .map(|(_, tooltip)| crate::messages::hex_encode(tooltip.trim_start()))
            .unwrap_or_default());
    }
    (RefusalCode::BadLine, String::new())
}

/// RFC 4648 §4 base64 with padding of a chunk's bytes (§F.2).
#[must_use]
pub fn b64_encode(input: &[u8]) -> String {
    const ALPHABET: &[u8; 64] =
        b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::with_capacity(input.len().div_ceil(3) * 4);
    for chunk in input.chunks(3) {
        let b0 = chunk[0];
        let b1 = *chunk.get(1).unwrap_or(&0);
        let b2 = *chunk.get(2).unwrap_or(&0);
        let n = (u32::from(b0) << 16) | (u32::from(b1) << 8) | u32::from(b2);
        out.push(ALPHABET[usize::try_from((n >> 18) & 0x3f).unwrap()] as char);
        out.push(ALPHABET[usize::try_from((n >> 12) & 0x3f).unwrap()] as char);
        if chunk.len() > 1 {
            out.push(ALPHABET[usize::try_from((n >> 6) & 0x3f).unwrap()] as char);
        } else {
            out.push('=');
        }
        if chunk.len() > 2 {
            out.push(ALPHABET[usize::try_from(n & 0x3f).unwrap()] as char);
        } else {
            out.push('=');
        }
    }
    out
}

/// Decode RFC 4648 §4 base64 with padding (§F.2). A non-alphabet byte is a
/// [`WireError::BulkMalformed`].
pub fn b64_decode(input: &str) -> Result<Vec<u8>, WireError> {
    fn value(byte: u8) -> Result<u32, WireError> {
        match byte {
            b'A'..=b'Z' => Ok(u32::from(byte - b'A')),
            b'a'..=b'z' => Ok(u32::from(byte - b'a') + 26),
            b'0'..=b'9' => Ok(u32::from(byte - b'0') + 52),
            b'+' => Ok(62),
            b'/' => Ok(63),
            _ => Err(WireError::BulkMalformed),
        }
    }
    let bytes = input.as_bytes();
    if bytes.is_empty() {
        return Ok(Vec::new());
    }
    if !bytes.len().is_multiple_of(4) {
        return Err(WireError::BulkMalformed);
    }
    let pad = match bytes {
        b if b.ends_with(b"==") => 2,
        b if b.ends_with(b"=") => 1,
        _ => 0,
    };
    let groups = bytes.as_chunks::<4>().0;
    let mut out = Vec::with_capacity(groups.len() * 3);
    for (gi, group) in groups.iter().enumerate() {
        // Only the final group carries padding.
        let group_pad = if gi + 1 == groups.len() { pad } else { 0 };
        let mut acc = 0u32;
        for (i, byte) in group.iter().enumerate() {
            let v = if i >= group.len() - group_pad {
                if *byte != b'=' {
                    return Err(WireError::BulkMalformed);
                }
                0
            } else {
                value(*byte)?
            };
            acc = (acc << 6) | v;
        }
        let total = 3 - group_pad;
        for k in 0..total {
            let shift = 16 - k * 8;
            out.push(((acc >> shift) & 0xff) as u8);
        }
    }
    Ok(out)
}

/// One receiver's bulk assembly state (§F.2). Chunks of different bulks may interleave; the
/// receiver assembles by `bulk_id`. A gap, a repeat, a changed `count` or an unknown `bulk_id`
/// drops that bulk and reports [`WireError::BulkMalformed`].
#[derive(Default)]
pub struct BulkReceiver {
    partial: Vec<PartialBulk>,
}

#[derive(Clone, Debug)]
struct PartialBulk {
    bulk_id: u64,
    count: u64,
    received: u64,
    bytes: Vec<u8>,
}

impl BulkReceiver {
    /// A fresh receiver with no bulk in flight.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Feed one decoded chunk. Returns the assembled bytes when the chunk completes its bulk,
    /// or an empty `Vec` when the bulk is still in flight. A structural violation is reported
    /// through `Err` and the offending bulk is dropped.
    pub fn submit(&mut self, chunk: &BulkChunk) -> Result<Vec<u8>, WireError> {
        if chunk.count == 0 || chunk.index >= chunk.count {
            return Err(WireError::BulkMalformed);
        }
        let raw = b64_decode(&chunk.b64)?;
        match self.partial.iter_mut().find(|b| b.bulk_id == chunk.bulk_id) {
            Some(bulk) => {
                if bulk.count != chunk.count || bulk.received != chunk.index {
                    self.drop_bulk(chunk.bulk_id);
                    return Err(WireError::BulkMalformed);
                }
                bulk.bytes.extend_from_slice(&raw);
                bulk.received += 1;
                if bulk.received == bulk.count {
                    let index = self
                        .partial
                        .iter()
                        .position(|b| b.bulk_id == chunk.bulk_id)
                        .expect("just found");
                    Ok(self.partial.remove(index).bytes)
                } else {
                    Ok(Vec::new())
                }
            }
            None => {
                if chunk.index != 0 {
                    // A chunk for a bulk we never saw, or a mid-stream gap with no head.
                    return Err(WireError::BulkMalformed);
                }
                self.partial.push(PartialBulk {
                    bulk_id: chunk.bulk_id,
                    count: chunk.count,
                    received: 1,
                    bytes: raw,
                });
                if chunk.count == 1 {
                    let index = self
                        .partial
                        .iter()
                        .rposition(|b| b.bulk_id == chunk.bulk_id)
                        .expect("just pushed");
                    Ok(self.partial.remove(index).bytes)
                } else {
                    Ok(Vec::new())
                }
            }
        }
    }

    fn drop_bulk(&mut self, bulk_id: u64) {
        self.partial.retain(|b| b.bulk_id != bulk_id);
    }

    /// How many bulks are currently in flight.
    #[must_use]
    pub fn in_flight(&self) -> usize {
        self.partial.len()
    }
}
