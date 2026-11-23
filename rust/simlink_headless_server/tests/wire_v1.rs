#![forbid(unsafe_code)]
//! Card 57 §C.1 line rules, §C.2 hello, §D.1 frames, §D.2 refusal grammar, §F events and the
//! bulk form. Unit tests over the `wire_v1` codec: every §C.1 rule, every §D.2 code, malformed
//! lines, over-long lines and edge cases.

use simlink_headless_server::messages as m;
use simlink_headless_server::wire_v1 as w;

// ---- §C.1.1 line rules -------------------------------------------------------------

#[test]
fn line_joins_fields_with_pipe_and_terminates_lf() {
    let content = w::line(&["CMD", "1", "ClaimSim", "7", "3"]).unwrap();
    assert_eq!(content, "CMD|1|ClaimSim|7|3");
    assert_eq!(w::terminate(&content), "CMD|1|ClaimSim|7|3\n");
}

#[test]
fn field_with_separator_is_refused_on_encode() {
    for bad in ["a|b", "a\nb", "a\rb"] {
        assert_eq!(w::line(&["X", bad]), Err(m::WireError::FieldSeparator));
    }
}

#[test]
fn split_round_trips_and_drops_one_cr_before_lf() {
    // §C.1.1: one CR immediately before the LF is dropped.
    let fields = w::split(b"ACK|1|Ping\n").unwrap();
    assert_eq!(fields, vec!["ACK", "1", "Ping"]);
    let fields = w::split(b"ACK|1|Ping\r\n").unwrap();
    assert_eq!(fields, vec!["ACK", "1", "Ping"]);
    // No trailing LF (already-framed content) is also accepted.
    let fields = w::split(b"EV|1|2|0|Tick").unwrap();
    assert_eq!(fields, vec!["EV", "1", "2", "0", "Tick"]);
}

#[test]
fn interior_cr_or_lf_is_a_field_separator_error() {
    assert_eq!(w::split(b"A|B\rC\n"), Err(m::WireError::FieldSeparator));
    // A bare LF mid-buffer with no trailing LF is a missing-LF (it contains an interior LF).
    assert_eq!(w::split(b"A\nB"), Err(m::WireError::MissingLineFeed));
}

#[test]
fn not_utf8_is_rejected() {
    let mut bytes = b"CMD|".to_vec();
    bytes.extend_from_slice(&[0xff, 0xfe, 0x00]);
    assert_eq!(w::split(&bytes), Err(m::WireError::NotUtf8));
}

#[test]
fn over_long_line_is_refused_both_directions() {
    // §C.1.2: the cap is the card's literal 4_194_304, LF excluded — stated here as a number, not
    // derived from the constant, so doubling PUBLIC_LINE_MAX_BYTES fails this test (F1).
    const CAP: usize = 4_194_304;
    let exactly = "x".repeat(CAP);
    assert!(w::split(exactly.as_bytes()).is_ok());
    let too_long = "x".repeat(CAP + 1);
    assert_eq!(
        w::split(too_long.as_bytes()),
        Err(m::WireError::LineTooLong { len: CAP + 1 })
    );
    // Encoding a field that pushes the content past the cap is refused too.
    let big = "y".repeat(CAP);
    assert!(matches!(
        w::line(&["CMD", &big]),
        Err(m::WireError::LineTooLong { .. })
    ));
}

#[test]
fn the_public_caps_are_the_cards_literals() {
    // F1: the §C.1.2 / §F.2 / §G.4 numbers are the card's own literals. If a constant drifts, this
    // test fails on the number (and the `const _: () = assert!…` witness in messages.rs is a
    // compile error), rather than the bound test moving its target with the constant.
    assert_eq!(m::PUBLIC_LINE_MAX_BYTES, 4_194_304);
    assert_eq!(m::PUBLIC_BULK_CHUNK_RAW_BYTES, 3_145_632);
    assert_eq!(m::DEFAULT_WAN_PORT, 34019);
}

#[test]
fn lf_is_excluded_from_the_cap() {
    // content at the cap plus its LF still decodes (the LF is not counted). The literal, not the
    // constant, so a doubled cap does not silently move this test (F1).
    const CAP: usize = 4_194_304;
    let content = "x".repeat(CAP);
    let mut raw = content.into_bytes();
    raw.push(b'\n');
    assert_eq!(raw.len(), CAP + 1);
    assert!(w::split(&raw).is_ok());
}

// ---- §C.1.3 scalar encodings -------------------------------------------------------

#[test]
fn hex_round_trips_utf8_including_empty() {
    for text in ["", "hello", "π 你好 🎮", "a|b", "tab\tsep"] {
        let hex = m::hex_encode(text);
        assert!(m::is_legal_field(&hex), "hex output must be a legal field");
        assert_eq!(m::hex_decode(&hex).unwrap(), text);
    }
    assert_eq!(m::hex_encode(""), "");
    assert_eq!(m::hex_decode("").unwrap(), "");
}

#[test]
fn hex_rejects_odd_length_and_bad_nibble() {
    assert_eq!(m::hex_decode("abc"), Err(m::WireError::BadHex));
    assert_eq!(m::hex_decode("zz"), Err(m::WireError::BadHex));
    // Even length, valid hex, but not valid UTF-8.
    assert_eq!(m::hex_decode("ff"), Err(m::WireError::NotUtf8));
}

#[test]
fn bool_and_int_and_opt_scalars() {
    assert_eq!(m::enc_bool(true), "1");
    assert_eq!(m::enc_bool(false), "0");
    assert_eq!(m::dec_bool("1"), Ok(true));
    assert_eq!(m::dec_bool("2"), Err(m::WireError::BadBool));
    assert_eq!(m::dec_u64("18446744073709551615"), Ok(u64::MAX));
    assert_eq!(m::dec_u64("-1"), Err(m::WireError::BadInt));
    assert_eq!(m::dec_i32("-2147483648"), Ok(i32::MIN));
    assert_eq!(m::dec_i32("2147483648"), Err(m::WireError::BadInt));
    // absent nullable is `~`; empty string is the empty field.
    assert_eq!(m::enc_opt(None), "~");
    assert_eq!(m::enc_opt(Some("")), "");
    assert_eq!(m::dec_opt("~"), None);
    assert_eq!(m::dec_opt(""), Some(""));
}

#[test]
fn composites_are_unambiguous_at_depth() {
    // A record of hex scalars and a list, §C.1.4. Items contain no ( ) [ ] ; , because they
    // are hex or `~`.
    let record = m::enc_record(&[m::hex_encode("a"), "~".into(), m::hex_encode("b")]);
    assert_eq!(record, "(61;~;62)");
    let items = m::dec_record("(61;~;62)").unwrap();
    assert_eq!(items, vec!["61", "~", "62"]);
    assert_eq!(m::dec_record("()").unwrap(), Vec::<String>::new());
    assert_eq!(m::dec_list("[1,2,3]").unwrap(), vec!["1", "2", "3"]);
    assert_eq!(m::dec_list("[]").unwrap(), Vec::<String>::new());
    // Wrong delimiters.
    assert_eq!(m::dec_record("[x]"), Err(m::WireError::BadComposite));
    assert_eq!(m::dec_list("(x)"), Err(m::WireError::BadComposite));
}

#[test]
fn nested_composites_split_depth_first() {
    // §C.1.4 "unambiguous at any depth": a record holds a list that holds a record. The flat split
    // mis-split `([(61;62),63];64)` into 3 items; the depth-aware split keeps it at 2, asserted
    // item by item (F2).
    let inner = m::enc_record(&["61".into(), "62".into()]); // (61;62)
    let list = m::enc_list(&[inner.clone(), "63".into()]); // [(61;62),63]
    let outer = m::enc_record(&[list.clone(), "64".into()]); // ([(61;62),63];64)
    assert_eq!(outer, "([(61;62),63];64)");
    let items = m::dec_record(&outer).expect("outer record decodes");
    assert_eq!(items.len(), 2, "depth-2 record mis-split: {items:?}");
    assert_eq!(items[0], "[(61;62),63]");
    assert_eq!(items[1], "64");
    let back = m::dec_list(&items[0]).expect("inner list decodes");
    assert_eq!(back.len(), 2, "depth-2 list mis-split: {back:?}");
    assert_eq!(back[0], "(61;62)");
    assert_eq!(back[1], "63");
    // And the deepest record still holds its two hex fields.
    assert_eq!(m::dec_record(&back[0]).unwrap(), vec!["61", "62"]);
}

#[test]
fn a_hex_separator_or_bracket_in_a_scalar_does_not_move_the_depth() {
    // §C.1.3/§C.1.4: a scalar is hex, so a `;`, `,`, `[` or `]` the text carries arrives encoded
    // (`a;b]` → `613b625d`) and never opens or closes a composite. One level deep and two levels
    // deep, the split stays exact (F2, the escaped-bracket case).
    let hex_scalar = m::hex_encode("a;b]"); // "613b625d" — no raw ';' or ']' on the wire
    assert!(!hex_scalar.contains(';') && !hex_scalar.contains(']'));
    let deep_record = m::enc_record(&[hex_scalar.clone(), "9".into()]); // (613b625d;9)
    let deep_list = m::enc_list(&[deep_record.clone(), "8".into()]); // [(613b625d;9),8]
    let deep_outer = m::enc_record(&[deep_list.clone(), "7".into()]); // ([(613b625d;9),8];7)
    let o = m::dec_record(&deep_outer).unwrap();
    assert_eq!(o, vec!["[(613b625d;9),8]", "7"]);
    let l = m::dec_list(&o[0]).unwrap();
    assert_eq!(l, vec!["(613b625d;9)", "8"]);
    let r = m::dec_record(&l[0]).unwrap();
    assert_eq!(r, vec![hex_scalar.clone(), "9".into()]);
    // The separator and the bracket survive inside the decoded scalar text.
    assert_eq!(m::hex_decode(&r[0]).unwrap(), "a;b]");
}

#[test]
fn malformed_nesting_is_a_wire_error_never_a_panic() {
    // F2: a stray closer, a wrong-kind closer, or an unclosed opener is BadComposite, not a panic.
    assert_eq!(m::dec_record("(1;2))"), Err(m::WireError::BadComposite)); // stray ')'
    assert_eq!(m::dec_record("([1;2))"), Err(m::WireError::BadComposite)); // '[', closed by ')'
    assert_eq!(m::dec_list("[[1,2]"), Err(m::WireError::BadComposite)); // unclosed '['
}

#[test]
fn checked_composite_encoders_reject_injected_structure() {
    for bad in ["a;b", "a,b", "a|b", "a\nb", "[1", "(1]", "1)"] {
        assert!(m::try_enc_record(&[bad.into()]).is_err());
        assert!(m::try_enc_list(&[bad.into()]).is_err());
    }
    assert_eq!(m::try_enc_record(&["[(61;62),63]".into(), "64".into()]).unwrap(), "([(61;62),63];64)");
}

#[test]
fn bulk_ref_field_is_b_and_id() {
    assert_eq!(m::enc_bulk_ref(42), "b42");
    assert_eq!(m::dec_bulk_ref("b42"), Some(42));
    assert_eq!(m::dec_bulk_ref("61"), None);
    assert_eq!(m::dec_bulk_ref("b"), None);
}

// ---- §C.2 hello --------------------------------------------------------------------

fn caps(pairs: &[(&str, &str)]) -> m::Caps {
    let mut c = m::Caps::default();
    for (k, v) in pairs {
        c.set(k, v);
    }
    c
}

#[test]
fn hello_welcome_frames_round_trip() {
    let hello = m::Hello {
        version: 1,
        caps: caps(&[("ring", "1"), ("wfs", "1"), ("role", "client")]),
    };
    let line = w::encode_hello(&hello).unwrap();
    assert_eq!(line, "HELLO|1|ring=1,wfs=1,role=client");
    let back = w::decode_hello(&w::split(line.as_bytes()).unwrap()).unwrap();
    assert_eq!(back, hello);

    let welcome = m::Welcome {
        version: 1,
        epoch: 7,
        server_caps: String::new(),
    };
    let line = w::encode_welcome(&welcome).unwrap();
    assert_eq!(line, "WELCOME|1|7|");
    assert_eq!(
        w::decode_welcome(&w::split(line.as_bytes()).unwrap()).unwrap(),
        welcome
    );
}

#[test]
fn judge_hello_accepts_a_full_declaration() {
    let raw = b"HELLO|1|ring=1,wfs=1,role=client2\n";
    let hello = w::judge_hello(raw).unwrap();
    assert_eq!(hello.version, 1);
    assert_eq!(hello.caps.get("role"), Some("client2"));
}

#[test]
fn judge_hello_names_every_refusal_token() {
    // §C.2 closed detail-token list, each named on a `hello_rejected`.
    assert_eq!(
        w::judge_hello(b"{\"type\":\"hello\"}"),
        Err(w::HelloRefusal::Rejected(w::HelloToken::JsonHello))
    );
    assert_eq!(
        w::judge_hello(b"HELLO|1|ring=0,wfs=1\n"),
        Err(w::HelloRefusal::Rejected(w::HelloToken::MissingSimlinkRing))
    );
    assert_eq!(
        w::judge_hello(b"HELLO|1|ring=1,wfs=0\n"),
        Err(w::HelloRefusal::Rejected(w::HelloToken::MissingWorldFromServer))
    );
    assert_eq!(
        w::judge_hello(b"HELLO|1|ring=1\n"),
        Err(w::HelloRefusal::Rejected(w::HelloToken::MissingWorldFromServer))
    );
    // version not 1.
    assert_eq!(
        w::judge_hello(b"HELLO|2|ring=1,wfs=1\n"),
        Err(w::HelloRefusal::Rejected(w::HelloToken::VersionUnsupported))
    );
    // legacy sidecar CONNECT and the 7-field sidecar HELLO.
    assert_eq!(
        w::judge_hello(b"CONNECT|19|abc|1|1|17|15|client"),
        Err(w::HelloRefusal::Rejected(w::HelloToken::LegacySidecarHello))
    );
    assert_eq!(
        w::judge_hello(b"HELLO|5|1|1|17|15|client"),
        Err(w::HelloRefusal::Rejected(w::HelloToken::LegacySidecarHello))
    );
}

#[test]
fn a_malformed_hello_line_is_bad_line_not_a_version_refusal() {
    // N5: a first line that is not a decodable v1 line — non-UTF-8, or a caps field that will not
    // parse — is a §C.1 line fault answered `bad_line`, not a `version_unsupported` hello.
    let mut not_utf8 = b"HELLO|1|ring=".to_vec();
    not_utf8.extend_from_slice(&[0xff, 0xfe]);
    assert_eq!(w::judge_hello(&not_utf8), Err(w::HelloRefusal::BadLine));
    // A caps field that is not `key=value` (no `=`) fails Caps::parse.
    assert_eq!(w::judge_hello(b"HELLO|1|ring\n"), Err(w::HelloRefusal::BadLine));
}

#[test]
fn hello_rejected_line_is_an_err_frame() {
    // server: ERR|0|hello_rejected|HELLO|<hexText>  (§C.2)
    let err = m::ErrFrame {
        corr: 0,
        code: m::RefusalCode::HelloRejected,
        verb: "HELLO".into(),
        hex_text: m::hex_encode(w::HelloToken::MissingSimlinkRing.as_str()),
    };
    let line = w::encode_err(&err).unwrap();
    assert!(line.starts_with("ERR|0|hello_rejected|HELLO|"));
    assert_eq!(w::decode_err(&w::split(line.as_bytes()).unwrap()).unwrap(), err);
}

// ---- §D.1 commands and replies -----------------------------------------------------

#[test]
fn command_and_ack_frames_round_trip() {
    let cmd = m::Command {
        corr: 3,
        verb: "QueueInteraction".into(),
        command_id: 99,
        player_id: -2,
        args: vec!["61".into(), "~".into()],
    };
    let line = w::encode_command(&cmd).unwrap();
    assert_eq!(line, "CMD|3|QueueInteraction|99|-2|61|~");
    assert_eq!(
        w::decode_command(&w::split(line.as_bytes()).unwrap()).unwrap(),
        cmd
    );

    let ack = m::Ack {
        corr: 3,
        verb: "QueueInteraction".into(),
        fields: vec!["queued".into()],
    };
    let line = w::encode_ack(&ack).unwrap();
    assert_eq!(line, "ACK|3|QueueInteraction|queued");
    assert_eq!(w::decode_ack(&w::split(line.as_bytes()).unwrap()).unwrap(), ack);
}

#[test]
fn command_without_args_has_five_fields() {
    let cmd = m::Command {
        corr: 1,
        verb: "JoinSession".into(),
        command_id: 1,
        player_id: 0,
        args: vec![],
    };
    let line = w::encode_command(&cmd).unwrap();
    assert_eq!(line, "CMD|1|JoinSession|1|0");
    let back = w::decode_command(&w::split(line.as_bytes()).unwrap()).unwrap();
    assert!(back.args.is_empty());
}

#[test]
fn bad_verb_identifier_is_rejected() {
    let cmd = m::Command {
        corr: 1,
        verb: "Bad Verb".into(),
        command_id: 1,
        player_id: 0,
        args: vec![],
    };
    assert_eq!(w::encode_command(&cmd), Err(m::WireError::BadIdentifier));
    assert_eq!(
        w::decode_command(&["CMD".into(), "1".into(), "2bad".into(), "1".into(), "0".into()]),
        Err(m::WireError::BadIdentifier)
    );
}

#[test]
fn truncated_frames_report_too_few_fields() {
    assert_eq!(
        w::decode_command(&["CMD".into(), "1".into(), "Ping".into(), "2".into()]),
        Err(m::WireError::TooFewFields {
            frame: "CMD",
            got: 4,
            min: 5
        })
    );
    assert_eq!(
        w::decode_err(&["ERR".into(), "0".into(), "bad_line".into()]),
        Err(m::WireError::TooFewFields {
            frame: "ERR",
            got: 3,
            min: 5
        })
    );
}

// ---- §D.2 refusal grammar ----------------------------------------------------------

#[test]
fn every_refusal_code_round_trips_on_the_err_frame() {
    for code in m::RefusalCode::ALL {
        let err = m::ErrFrame {
            corr: 12,
            code,
            verb: "QueueInteraction".into(),
            hex_text: m::hex_encode("detail"),
        };
        let line = w::encode_err(&err).unwrap();
        let fields = w::split(line.as_bytes()).unwrap();
        let back = w::decode_err(&fields).unwrap();
        assert_eq!(back.code, code);
        assert_eq!(back, err);
        assert!(m::is_identifier(code.as_str()));
        assert!(code.as_str().len() <= 64, "code over 64 bytes: {code:?}");
    }
}

#[test]
fn empty_detail_and_no_verb_err_frames() {
    // §C.1.2 line_too_long and §D.1 R-class ClientNativeModesOff use `-` verb / empty text.
    let err = m::ErrFrame {
        corr: 0,
        code: m::RefusalCode::LineTooLong,
        verb: w::NO_VERB.into(),
        hex_text: String::new(),
    };
    let line = w::encode_err(&err).unwrap();
    assert_eq!(line, "ERR|0|line_too_long|-|");
    assert_eq!(w::decode_err(&w::split(line.as_bytes()).unwrap()).unwrap(), err);

    let legacy = m::ErrFrame {
        corr: 5,
        code: m::RefusalCode::ClientNativeModesOff,
        verb: "SubmitLotExport".into(),
        hex_text: String::new(),
    };
    assert_eq!(
        w::encode_err(&legacy).unwrap(),
        "ERR|5|ClientNativeModesOff|SubmitLotExport|"
    );
}

#[test]
fn unknown_err_code_is_a_bad_identifier() {
    assert_eq!(
        w::decode_err(&[
            "ERR".into(),
            "0".into(),
            "NoSuchCode".into(),
            "Ping".into(),
            String::new()
        ]),
        Err(m::WireError::BadIdentifier)
    );
}

#[test]
fn pick_failed_event_frame_carries_the_pair() {
    // EV|<seq>|<tick>|<corr>|PickFailed|<code>|<hexText>  (§D.2)
    let ev = w::event_pick_failed(4, 90, 12, m::RefusalCode::RouteFailed, &m::hex_encode("no path"));
    let line = w::encode_event(&ev).unwrap();
    assert_eq!(
        line,
        format!("EV|4|90|12|PickFailed|RouteFailed|{}", m::hex_encode("no path"))
    );
    let back = w::decode_event(&w::split(line.as_bytes()).unwrap()).unwrap();
    assert_eq!(back, ev);
}

#[test]
fn menu_refusal_pair_and_six_field_legacy() {
    // eight-field grey refusal
    let rf = m::MenuRefusal {
        actor: "7".into(),
        target: "9".into(),
        hex_type: m::hex_encode("Social"),
        hex_name: m::hex_encode("Chat"),
        refused: true,
        code: Some(m::RefusalCode::TestFailed),
        hex_text: m::hex_encode("Too busy"),
    };
    let line = w::encode_menu_refusal(&rf).unwrap();
    assert_eq!(
        line,
        format!(
            "Rf|7|9|{}|{}|1|TestFailed|{}",
            m::hex_encode("Social"),
            m::hex_encode("Chat"),
            m::hex_encode("Too busy")
        )
    );
    assert_eq!(
        w::decode_menu_refusal(&w::split(line.as_bytes()).unwrap()).unwrap(),
        rf
    );

    // a clear (…|0) sends both empty
    let clear = m::MenuRefusal {
        actor: "7".into(),
        target: "9".into(),
        hex_type: m::hex_encode("Social"),
        hex_name: m::hex_encode("Chat"),
        refused: false,
        code: Some(m::RefusalCode::TestFailed),
        hex_text: m::hex_encode("ignored"),
    };
    let clear_line = w::encode_menu_refusal(&clear).unwrap();
    assert!(clear_line.ends_with("|0||"));
    let decoded_clear = w::decode_menu_refusal(&w::split(clear_line.as_bytes()).unwrap()).unwrap();
    assert!(!decoded_clear.refused);
    assert_eq!(decoded_clear.code, None);
    assert_eq!(decoded_clear.hex_text, "");

    // a six-field legacy line reads as (TestFailed, empty)  (§D.2)
    let legacy = w::decode_menu_refusal(
        &w::split("Rf|7|9|53|43|1".as_bytes())
            .unwrap(),
    )
    .unwrap();
    assert!(legacy.refused);
    assert_eq!(legacy.code, Some(m::RefusalCode::TestFailed));
    assert_eq!(legacy.hex_text, "");
}

#[test]
fn add_refused_tooltip_splits_into_test_failed_and_text() {
    // §D.2: <old text> | tooltip: <text>  →  (TestFailed, hex(text)).
    let raw = "Interaction refused | tooltip: The sim is asleep";
    let (code, hex) = w::split_add_tooltip(raw);
    assert_eq!(code, m::RefusalCode::TestFailed);
    assert_eq!(m::hex_decode(&hex).unwrap(), "The sim is asleep");
    // no tooltip → whole string is the text
    let (code2, hex2) = w::split_add_tooltip("no tooltip here");
    assert_eq!(code2, m::RefusalCode::TestFailed);
    assert_eq!(m::hex_decode(&hex2).unwrap(), "no tooltip here");
}

// ---- §F.1 events -------------------------------------------------------------------

#[test]
fn event_envelope_round_trips() {
    let ev = m::Event {
        seq: 1,
        tick: 500,
        corr: 0,
        kind: "SimMoved".into(),
        fields: vec!["61".into()],
    };
    let line = w::encode_event(&ev).unwrap();
    assert_eq!(line, "EV|1|500|0|SimMoved|61");
    assert_eq!(w::decode_event(&w::split(line.as_bytes()).unwrap()).unwrap(), ev);
}

// ---- §F.2 bulk ---------------------------------------------------------------------

#[test]
fn base64_rfc4648_with_padding() {
    for (raw, want) in [
        ("", ""),
        ("f", "Zg=="),
        ("fo", "Zm8="),
        ("foo", "Zm9v"),
        ("foob", "Zm9vYg=="),
        ("fooba", "Zm9vYmE="),
        ("foobar", "Zm9vYmFy"),
    ] {
        assert_eq!(w::b64_encode(raw.as_bytes()), want);
        assert_eq!(w::b64_decode(want).unwrap(), raw.as_bytes());
    }
    // Raw bytes that are not a valid UTF-8 string (a two-byte value 0x00 0xFF).
    assert_eq!(w::b64_encode(&[0x00u8, 0xFF]), "AP8=");
    assert_eq!(w::b64_decode("AP8=").unwrap(), vec![0x00, 0xFF]);
    assert_eq!(w::b64_decode("Zm9vY").unwrap_err(), m::WireError::BulkMalformed);
    assert_eq!(w::b64_decode("!!!!").unwrap_err(), m::WireError::BulkMalformed);
}

#[test]
fn a_full_bulk_chunk_fits_under_the_cap() {
    // §F.2 arithmetic in the card's literals, not derived from the constants (F1):
    // header (68 max) + base64 (4_194_176) = 4_194_244 <= 4_194_304.
    const CAP: usize = 4_194_304;
    const CHUNK_RAW: usize = 3_145_632;
    const CHUNK_B64: usize = 4_194_176;
    let chunk = vec![0xabu8; CHUNK_RAW];
    let b64 = w::b64_encode(&chunk);
    assert_eq!(b64.len(), CHUNK_B64, "a full chunk's base64 is 4_194_176 bytes");
    let bulk = m::BulkChunk {
        bulk_id: u64::MAX,
        index: u64::MAX,
        count: u64::MAX,
        b64,
    };
    let line = w::encode_bulk(&bulk).unwrap();
    assert!(line.len() <= CAP, "bulk line too long: {}", line.len());
    assert_eq!(w::split(line.as_bytes()).unwrap().len(), 5);
}

#[test]
fn bulk_frame_round_trips() {
    let bulk = m::BulkChunk {
        bulk_id: 3,
        index: 0,
        count: 2,
        b64: w::b64_encode(b"part-one"),
    };
    let line = w::encode_bulk(&bulk).unwrap();
    assert!(line.starts_with("BULK|3|0|2|"));
    assert_eq!(w::decode_bulk(&w::split(line.as_bytes()).unwrap()).unwrap(), bulk);
}

#[test]
fn bulk_receiver_assembles_interleaved_bulks() {
    let mut rx = w::BulkReceiver::new();
    let a0 = m::BulkChunk { bulk_id: 10, index: 0, count: 2, b64: w::b64_encode(b"AB") };
    let b0 = m::BulkChunk { bulk_id: 11, index: 0, count: 1, b64: w::b64_encode(b"Z") };
    assert!(rx.submit(&a0).unwrap().is_empty()); // still in flight
    assert_eq!(rx.in_flight(), 1);
    assert_eq!(rx.submit(&b0).unwrap(), b"Z"); // count==1 completes at once
    let a1 = m::BulkChunk { bulk_id: 10, index: 1, count: 2, b64: w::b64_encode(b"CD") };
    assert_eq!(rx.submit(&a1).unwrap(), b"ABCD");
    assert_eq!(rx.in_flight(), 0);
}

#[test]
fn bulk_receiver_reports_malformed() {
    // a gap (index skip)
    let mut rx = w::BulkReceiver::new();
    rx.submit(&m::BulkChunk { bulk_id: 1, index: 0, count: 3, b64: w::b64_encode(b"a") })
        .unwrap();
    let gap = m::BulkChunk { bulk_id: 1, index: 2, count: 3, b64: w::b64_encode(b"c") };
    assert_eq!(rx.submit(&gap), Err(m::WireError::BulkMalformed));
    // a repeat / count change
    let mut rx = w::BulkReceiver::new();
    rx.submit(&m::BulkChunk { bulk_id: 2, index: 0, count: 2, b64: w::b64_encode(b"a") })
        .unwrap();
    let changed = m::BulkChunk { bulk_id: 2, index: 1, count: 3, b64: w::b64_encode(b"b") };
    assert_eq!(rx.submit(&changed), Err(m::WireError::BulkMalformed));
    // an unknown bulk with no head (index != 0)
    let mut rx = w::BulkReceiver::new();
    let stray = m::BulkChunk { bulk_id: 9, index: 1, count: 2, b64: w::b64_encode(b"x") };
    assert_eq!(rx.submit(&stray), Err(m::WireError::BulkMalformed));
}

#[test]
fn a_client_bulk_line_is_well_formed_for_the_codec_not_refused_by_it() {
    // §F.2: BULK flows server→client only in v1. The codec decodes a client BULK line (what this
    // asserts: the frame parses); the front door's serve loop — a later card 57 step (N1) — answers
    // it with ERR|0|bad_line|-|. The codec has no socket and sends no such refusal, so the test is
    // named for what the codec does, not for a refusal it cannot make (N2).
    let fields = w::split(b"BULK|1|0|1|AAAA\n").unwrap();
    assert_eq!(fields[0], w::TAG_BULK);
    let chunk = w::decode_bulk(&fields).unwrap();
    assert_eq!(chunk.bulk_id, 1);
    assert_eq!(chunk.index, 0);
    assert_eq!(chunk.count, 1);
}
