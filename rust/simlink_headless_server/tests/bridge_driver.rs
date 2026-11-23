#![forbid(unsafe_code)]

use simlink_bridge::verbs::BridgeCommand;
use simlink_headless_server::bridge_driver::{BridgeEndpoint, BridgeGameDriver};
use simlink_headless_server::messages::{hex_decode, hex_encode, Command, RefusalCode};
use simlink_headless_server::transport::{DriverError, GameDriver};

#[derive(Default)]
struct RecordedBridge {
    commands: Vec<BridgeCommand>,
    error: Option<String>,
}

impl BridgeEndpoint for RecordedBridge {
    fn command(&mut self, command: &BridgeCommand) -> Result<Vec<String>, DriverError> {
        self.commands.push(command.clone());
        Ok(self.error.take().into_iter().collect())
    }

    fn events(&mut self) -> Result<Vec<Vec<u8>>, DriverError> {
        Ok(Vec::new())
    }
}

fn command(corr: u64, player: i32, verb: &str, args: &[&str]) -> Command {
    Command {
        corr,
        verb: verb.into(),
        command_id: corr,
        player_id: player,
        args: args.iter().map(|s| (*s).into()).collect(),
    }
}

#[test]
fn class_b_uses_typed_commands_and_preserves_u64_ids() {
    let mut driver = BridgeGameDriver::new(RecordedBridge::default());
    let id = "18446744073709551615";
    for (corr, verb, args) in [
        (1, "SetActiveSim", vec![id]),
        (2, "CancelInteraction", vec![id, "9007199254740993"]),
        (3, "LeaveConversation", vec![id]),
        (4, "SetGameSpeed", vec!["Speed2"]),
        (5, "SetWorldTime", vec!["(3;7;38)"]),
        (6, "SaveGame", vec!["73617665"]),
        (7, "RequestSnapshot", vec!["~"]),
        (8, "RequestSimDescription", vec![id]),
    ] {
        let reply = driver.adjudicate(&command(corr, 7, verb, &args)).unwrap();
        assert!(reply.starts_with(format!("ACK|{corr}|{verb}").as_bytes()));
    }
    let sent = &driver.endpoint().commands;
    assert!(matches!(sent[0], BridgeCommand::SetActiveSim { sim: u64::MAX, .. }));
    assert!(matches!(sent[1], BridgeCommand::CancelInteraction { actor: u64::MAX, interaction_id: 9_007_199_254_740_993, .. }));
    assert!(matches!(sent[4], BridgeCommand::SetWorldTime { day: 3, hours: 7, minutes: 38, .. }));
    assert_eq!(sent.len(), 8);
}

#[test]
fn kernel_claims_are_injective_and_supersede_releases_them() {
    let mut driver = BridgeGameDriver::new(RecordedBridge::default());
    let id = "9007199254740993";
    let first = driver.adjudicate(&command(1, -7, "ClaimSim", &[id])).unwrap();
    assert!(String::from_utf8(first).unwrap().contains(id), "kernel id leaked");
    let denied = driver.adjudicate(&command(1, 8, "ClaimSim", &[id])).unwrap();
    assert_eq!(String::from_utf8(denied).unwrap(), "ERR|1|UnauthorizedClientInsertion|ClaimSim|");
    driver.release_player(-7);
    assert!(driver.adjudicate(&command(2, 8, "ClaimSim", &[id])).unwrap().starts_with(b"ACK|2|ClaimSim"));
    assert!(driver.endpoint().commands.is_empty(), "ClaimSim is kernel adjudicated");
}

#[test]
fn queue_and_dialog_rename_before_kernel_adjudication_and_relay_only_on_accept() {
    let mut driver = BridgeGameDriver::new(RecordedBridge::default());
    let request = "(9007199254740993;52656164426f6f6b;~;~;[])";
    let denied = driver.adjudicate(&command(1, 7, "QueueInteraction", &[request])).unwrap();
    assert!(denied.starts_with(b"ERR|1|UnauthorizedClientInsertion|QueueInteraction|"));
    assert!(driver.endpoint().commands.is_empty());
    driver.adjudicate(&command(2, 7, "ClaimSim", &["9007199254740993"])).unwrap();
    let accepted = driver.adjudicate(&command(3, 7, "QueueInteraction", &[request])).unwrap();
    assert!(accepted.starts_with(b"ACK|3|QueueInteraction"));
    assert!(matches!(driver.endpoint().commands[0], BridgeCommand::QueueInteraction { .. }));
    // ClaimSim opens the kernel's first remote dialog (id 1).
    let reply = driver.adjudicate(&command(4, 7, "AnswerDialog", &["1", "9007199254740993", "31"])).unwrap();
    assert!(reply.starts_with(b"ACK|4|AnswerDialog"));
    assert!(matches!(driver.endpoint().commands[1], BridgeCommand::AnswerDialog { request_id: 1, .. }));
}

#[test]
fn game_refusal_lookup_sentences_are_codes_and_only_tooltips_are_text() {
    for (text, code) in [
        ("Sim not found: 0", RefusalCode::SimNotFound),
        ("Target not found: 9", RefusalCode::TargetNotFound),
        ("No matching interaction: Read", RefusalCode::NoMatchingInteraction),
        ("Picked object not found: 9", RefusalCode::PickedObjectNotFound),
        ("Interaction not found in queue or running: 9", RefusalCode::InteractionNotFound),
        ("InteractionQueue.Add refused", RefusalCode::TestFailed),
    ] {
        let endpoint = RecordedBridge {
            error: Some(format!("ERROR|00000000000000000000000000000001|{}", hex_encode(text))),
            ..RecordedBridge::default()
        };
        let mut driver = BridgeGameDriver::new(endpoint);
        let reply = driver.adjudicate(&command(1, 7, "SetActiveSim", &["0"])).unwrap();
        assert_eq!(String::from_utf8(reply).unwrap(), format!("ERR|1|{}|SetActiveSim|", code.as_str()));
    }
    let (code, text) = simlink_headless_server::wire_v1::game_refusal(
        "InteractionQueue.Add refused | tooltip: Too tired",
    );
    assert_eq!(code, RefusalCode::TestFailed);
    assert_eq!(hex_decode(&text).unwrap(), "Too tired");
}

#[test]
fn another_players_claim_never_reaches_the_typed_bridge() {
    let mut driver = BridgeGameDriver::new(RecordedBridge::default());
    driver.adjudicate(&command(1, 7, "ClaimSim", &["9007199254740993"])).unwrap();
    for verb in ["SetActiveSim", "CancelInteraction", "LeaveConversation"] {
        let args = if verb == "CancelInteraction" { vec!["9007199254740993", "1"] } else { vec!["9007199254740993"] };
        let reply = driver.adjudicate(&command(2, 8, verb, &args)).unwrap();
        assert_eq!(String::from_utf8(reply).unwrap(), format!("ERR|2|UnauthorizedClientInsertion|{verb}|"));
    }
    assert!(driver.endpoint().commands.is_empty());
}
