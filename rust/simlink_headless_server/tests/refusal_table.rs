#![forbid(unsafe_code)]
//! Card 57 §D.2, gate 3 (the Rust half): one row per refusal code on each frame it appears in,
//! round-tripped by the Rust codec; the `Add`-refused tooltip split; a six-field `Rf` reading
//! `(TestFailed, empty)`. `emit_refusal_table_artifact` writes §H row 3's `refusal_table.jsonl`.

use simlink_headless_server::messages::{ErrFrame, MenuRefusal, RefusalCode};
use simlink_headless_server::wire_v1 as w;

/// The four pick-failed codes (§D.2,) that ride the `EV|..|PickFailed` frame.
const PICK_FAILED_CODES: [RefusalCode; 4] = [
    RefusalCode::ObjectInUse,
    RefusalCode::RouteFailed,
    RefusalCode::FailedToStart,
    RefusalCode::NotRun,
];

/// The hello detail tokens (§C.2) that ride the `hello_rejected` text.
const HELLO_TOKENS: [&str; 5] = [
    "version_unsupported",
    "json_hello",
    "legacy_sidecar_hello",
    "MissingSimlinkRing",
    "MissingWorldFromServer",
];

fn round_trip_err(code: RefusalCode) -> bool {
    let err = ErrFrame {
        corr: 7,
        code,
        verb: "QueueInteraction".to_string(),
        hex_text: simlink_headless_server::messages::hex_encode("detail"),
    };
    let line = match w::encode_err(&err) {
        Ok(l) => l,
        Err(_) => return false,
    };
    match w::split(line.as_bytes()) {
        Ok(f) => w::decode_err(&f).map(|d| d == err).unwrap_or(false),
        Err(_) => false,
    }
}

fn round_trip_pick_failed(code: RefusalCode) -> bool {
    let ev = w::event_pick_failed(3, 90, 12, code, &simlink_headless_server::messages::hex_encode("x"));
    let line = match w::encode_event(&ev) {
        Ok(l) => l,
        Err(_) => return false,
    };
    match w::split(line.as_bytes()) {
        Ok(f) => {
            let d = match w::decode_event(&f) {
                Ok(d) => d,
                Err(_) => return false,
            };
            d.kind == "PickFailed"
                && d.fields.first().map(String::as_str) == Some(code.as_str())
        }
        Err(_) => false,
    }
}

fn round_trip_rf() -> bool {
    let rf = MenuRefusal {
        actor: "7".into(),
        target: "9".into(),
        hex_type: simlink_headless_server::messages::hex_encode("Social"),
        hex_name: simlink_headless_server::messages::hex_encode("Chat"),
        refused: true,
        code: Some(RefusalCode::TestFailed),
        hex_text: simlink_headless_server::messages::hex_encode("Too busy"),
    };
    let line = match w::encode_menu_refusal(&rf) {
        Ok(l) => l,
        Err(_) => return false,
    };
    match w::split(line.as_bytes()) {
        Ok(f) => w::decode_menu_refusal(&f).map(|d| d == rf).unwrap_or(false),
        Err(_) => false,
    }
}

#[test]
fn every_refusal_code_round_trips_on_the_err_frame() {
    for code in RefusalCode::ALL {
        assert!(round_trip_err(code), "{code:?} did not round-trip on ERR");
    }
}

#[test]
fn pick_failed_codes_round_trip_on_the_event_frame() {
    for code in PICK_FAILED_CODES {
        assert!(round_trip_pick_failed(code), "{code:?} did not round-trip on EV");
    }
}

#[test]
fn menu_refusal_round_trips_and_the_legacy_six_field_reads_test_failed() {
    assert!(round_trip_rf());
    let legacy = w::decode_menu_refusal(&w::split("Rf|7|9|53|43|1".as_bytes()).unwrap()).unwrap();
    assert_eq!(legacy.code, Some(RefusalCode::TestFailed));
    assert_eq!(legacy.hex_text, "");
}

#[test]
fn add_refused_tooltip_splits_into_test_failed_and_the_text() {
    let raw = "Interaction refused | tooltip: The sim is asleep";
    let (code, hex) = w::split_add_tooltip(raw);
    assert_eq!(code, RefusalCode::TestFailed);
    assert_eq!(
        simlink_headless_server::messages::hex_decode(&hex).unwrap(),
        "The sim is asleep"
    );
}

#[test]
fn recorded_error_is_decoded_through_the_bridge_and_named() {
    let line = include_str!("fixtures/recorded_error.txt").trim();
    let reply = simlink_bridge::reply::decode_response(line).unwrap().unwrap();
    assert!(!reply.accepted);
    let text = reply.rejection.unwrap();
    assert_eq!(text, "Sim not found: 0");
    assert_eq!(w::game_refusal(&text), (RefusalCode::SimNotFound, String::new()));
}

#[test]
fn merged_seven_field_input_refusal_retains_tooltip() {
    let rf = w::decode_menu_refusal(&w::split(b"Rf|12|400|9007199254740993|9|5|546f6f207469726564").unwrap()).unwrap();
    assert_eq!(rf.actor, "9007199254740993");
    assert_eq!(rf.target, "9");
    assert_eq!(rf.code, Some(RefusalCode::TestFailed));
    assert_eq!(simlink_headless_server::messages::hex_decode(&rf.hex_text).unwrap(), "Too tired");
    // Actual merged MenuRefusal.Line overload: six-field recorder plus tooltip.
    let recorded = w::decode_menu_refusal(&w::split(b"Rf|7|9|536f6369616c|43686174|1|546f6f207469726564").unwrap()).unwrap();
    assert_eq!(recorded.actor, "7");
    assert_eq!(recorded.target, "9");
    assert_eq!(recorded.code, rf.code);
    assert_eq!(recorded.hex_text, rf.hex_text);
}

#[test]
fn emit_refusal_table_artifact() {
    let Some(dir) = std::env::var_os("CARD57_ARTIFACT_DIR") else {
        return;
    };
    let mut rows = Vec::new();

    // One ERR-frame row per code in the table (§D.2).
    for code in RefusalCode::ALL {
        rows.push(format!(
            "{{\"code\":\"{}\",\"frame\":\"ERR\",\"rust_round_trip\":{}}}",
            code.as_str(),
            round_trip_err(code)
        ));
    }
    // The pick-failed codes also ride the EV|PickFailed frame (§D.2).
    for code in PICK_FAILED_CODES {
        rows.push(format!(
            "{{\"code\":\"{}\",\"frame\":\"EV|PickFailed\",\"rust_round_trip\":{}}}",
            code.as_str(),
            round_trip_pick_failed(code)
        ));
    }
    // The menu-refusal recorder frame (§D.2), and the six-field legacy reading.
    rows.push(format!(
        "{{\"code\":\"TestFailed\",\"frame\":\"Rf\",\"rust_round_trip\":{}}}",
        round_trip_rf()
    ));
    rows.push(
        "{\"code\":\"TestFailed\",\"frame\":\"Rf|six-field\",\"reads\":\"(TestFailed, empty)\"}"
            .to_string(),
    );
    // Actual recorded mod ERROR; provenance is also kept beside the fixture.
    let recorded = include_str!("fixtures/recorded_error.txt").trim();
    let captured = simlink_bridge::reply::decode_response(recorded).unwrap().unwrap();
    let (recorded_code, recorded_hex) = w::game_refusal(captured.rejection.as_deref().unwrap());
    rows.push(format!(
        "{{\"code\":\"{}\",\"frame\":\"ERROR|recorded\",\"source\":\"card56-20261101-173207-gate6/csharp_replies.txt\",\"wire\":\"{}\",\"hex_text\":\"{}\"}}",
        recorded_code.as_str(), recorded, recorded_hex
    ));
    // Synthetic Add-tooltip coverage: the recorded capture above has no Add refusal.
    let (code, hex) = w::split_add_tooltip("Interaction refused | tooltip: The sim is asleep");
    rows.push(format!(
        "{{\"code\":\"{}\",\"frame\":\"ERROR|tooltip-split\",\"recorded\":false,\"source\":\"synthetic Add tooltip unit fixture\",\"hex_text\":\"{}\"}}",
        code.as_str(),
        hex
    ));
    // hello_rejected on the hello frame, one row per closed detail token (§C.2).
    for token in HELLO_TOKENS {
        rows.push(format!(
            "{{\"code\":\"hello_rejected\",\"frame\":\"hello\",\"detail_token\":\"{token}\"}}"
        ));
    }

    std::fs::create_dir_all(&dir).unwrap();
    let path = std::path::Path::new(&dir).join("refusal_table.jsonl");
    std::fs::write(&path, format!("{}\n", rows.join("\n"))).expect("write refusal_table.jsonl");
}
