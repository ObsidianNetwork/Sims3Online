#![forbid(unsafe_code)]

mod common;

use std::io::{BufRead, BufReader, Write};
use std::net::TcpStream;
use std::time::Duration;
use simlink_bridge::verbs::BridgeCommand;
use simlink_headless_server::bridge_driver::{BridgeEndpoint, BridgeGameDriver};
use simlink_headless_server::messages::{hex_encode, Command, RefusalCode, PUBLIC_LINE_MAX_BYTES};
use simlink_headless_server::serve::read_bounded_line;
use simlink_headless_server::transport::{DriverError, GameDriver};

#[derive(Default)]
struct Endpoint {
    commands: Vec<BridgeCommand>,
    reply: Option<String>,
    fail_snapshot: bool,
}
impl BridgeEndpoint for Endpoint {
    fn command(&mut self, command: &BridgeCommand) -> Result<Vec<String>, DriverError> {
        self.commands.push(command.clone());
        if self.fail_snapshot && matches!(command, BridgeCommand::RequestSnapshot { .. }) {
            return Err(DriverError("snapshot unavailable".into()));
        }
        Ok(self.reply.take().into_iter().collect())
    }
    fn events(&mut self) -> Result<Vec<Vec<u8>>, DriverError> { Ok(Vec::new()) }
}
fn cmd(corr: u64, player: i32, verb: &str, args: &[&str]) -> Command {
    Command { corr, verb: verb.into(), command_id: corr, player_id: player,
        args: args.iter().map(|s| (*s).into()).collect() }
}
fn pick(key: &str) -> String {
    format!("(9007199254740993;{};~;(1;2;3);[])", hex_encode(key))
}
fn non_demo_pick(key: &str) {
    let mut driver = BridgeGameDriver::new(Endpoint {
        reply: Some(format!("ERROR|00000000000000000000000000000001|{}", hex_encode("Target not found: 9"))),
        ..Endpoint::default()
    });
    let reply = driver.adjudicate(&cmd(1, 7, "QueueInteraction", &[&pick(key)])).unwrap();
    assert_eq!(reply, b"ERR|1|TargetNotFound|QueueInteraction|");
    assert!(matches!(&driver.endpoint().commands[..],
        [BridgeCommand::QueueInteraction { interaction, .. }] if interaction.key == key));
}

#[test]
fn b1_terrain_key_lets_ts3w_decide_after_kernel_no_opinion() {
    non_demo_pick("Terrain.GoHere");
}
#[test]
fn b1_definition_type_key_lets_ts3w_decide_after_kernel_no_opinion() {
    non_demo_pick("Sims3.Gameplay.Objects.Bookcase.ReadSomething+Definition");
}
#[test]
fn b1_unknown_dialog_reaches_the_typed_bridge() {
    let mut driver = BridgeGameDriver::new(Endpoint::default());
    let reply = driver.adjudicate(&cmd(1, 7, "AnswerDialog", &["987654", "9007199254740993", "31"])).unwrap();
    assert_eq!(reply, b"ACK|1|AnswerDialog");
    assert!(matches!(&driver.endpoint().commands[..],
        [BridgeCommand::AnswerDialog { request_id: 987654, answer: Some(answer), .. }] if answer == "1"));
}
#[test]
fn b1_cross_player_non_demo_pick_is_refused_before_bridge() {
    let mut driver = BridgeGameDriver::new(Endpoint::default());
    driver.adjudicate(&cmd(1, 7, "ClaimSim", &["9007199254740993"])).unwrap();
    let reply = driver.adjudicate(&cmd(2, 8, "QueueInteraction", &[&pick("Terrain.GoHere")])).unwrap();
    assert_eq!(reply, b"ERR|2|UnauthorizedClientInsertion|QueueInteraction|");
    assert!(driver.endpoint().commands.is_empty());
}

#[test]
fn b2_lower_corr_stays_refused_after_leave_and_rejoin_on_one_socket() {
    let server = common::server();
    let mut peer = common::Peer::connect(&server);
    assert!(peer.exchange("CMD|10|JoinSession|10|7|61|~").starts_with("ACK|10|JoinSession|resume_token="));
    assert_eq!(peer.exchange("CMD|11|LeaveSession|11|7"), "ACK|11|LeaveSession");
    assert_eq!(peer.exchange("CMD|1|JoinSession|12|7|61|~"), "ERR|1|bad_line|JoinSession|");
    assert!(peer.exchange("CMD|12|JoinSession|12|7|61|~").starts_with("ACK|12|JoinSession|resume_token="));
    assert_eq!(peer.exchange("CMD|1|AckSnapshot|13|7|1"), "ERR|1|bad_line|AckSnapshot|");
    common::artifact("fix_round_order.txt", &["lower_corr_after_rejoin=refused".into()]);
    server.shutdown().unwrap();
}

#[test]
fn b2_command_id_retry_keeps_original_reply_after_leave_and_rejoin() {
    let server = common::server();
    let mut peer = common::Peer::connect(&server);
    assert!(peer.exchange("CMD|10|JoinSession|10|7|61|~").starts_with("ACK|10|JoinSession|resume_token="));
    let original = peer.exchange("CMD|11|ClaimSim|100|7|9007199254740993");
    assert!(original.starts_with("ACK|11|ClaimSim|"));
    assert_eq!(peer.exchange("CMD|12|LeaveSession|12|7"), "ACK|12|LeaveSession");
    assert!(peer.exchange("CMD|13|JoinSession|13|7|61|~").starts_with("ACK|13|JoinSession|resume_token="));
    assert_eq!(peer.exchange_with_corr("CMD|14|ClaimSim|100|7|9007199254740993", 11), original);
    // The retried claim must not resurrect the claim released by LeaveSession.
    let mut other = common::Peer::connect(&server);
    assert!(other.exchange("CMD|1|JoinSession|1|8|62|~").starts_with("ACK|1|JoinSession|resume_token="));
    assert!(other.exchange("CMD|2|ClaimSim|2|8|9007199254740993").starts_with("ACK|2|ClaimSim|"));
    server.shutdown().unwrap();
}

#[test]
fn m1_client_role_cannot_set_world_time() {
    let mut driver = BridgeGameDriver::new(Endpoint::default());
    assert_eq!(driver.adjudicate(&cmd(1, 7, "SetClientRole", &["Client"])).unwrap(), b"ACK|1|SetClientRole");
    assert_eq!(driver.adjudicate(&cmd(2, 7, "SetWorldTime", &["(3;7;38)"])).unwrap(),
        format!("ERR|2|UnauthorizedClientInsertion|SetWorldTime|{}",
            hex_encode("Only server-role players may set world time.")).as_bytes());
    assert!(driver.endpoint().commands.is_empty());
}
#[test]
fn m1_invalid_world_time_never_reaches_bridge() {
    let mut driver = BridgeGameDriver::new(Endpoint::default());
    for (i, time) in ["(-1;0;0)", "(7;0;0)", "(0;-1;0)", "(0;24;0)", "(0;0;-1)", "(0;0;60)"].iter().enumerate() {
        let corr = i as u64 + 1;
        assert_eq!(driver.adjudicate(&cmd(corr, 7, "SetWorldTime", &[time])).unwrap(),
            format!("ERR|{corr}|bad_line|SetWorldTime|").as_bytes());
    }
    assert!(driver.endpoint().commands.is_empty());
    for (i, time) in ["(0;0;0)", "(6;23;59)"].iter().enumerate() {
        assert!(driver.adjudicate(&cmd(i as u64 + 20, 7, "SetWorldTime", &[time])).unwrap().starts_with(b"ACK|"));
    }
    assert_eq!(driver.endpoint().commands.len(), 2);
}
#[test]
fn m1_leave_drops_client_role() {
    let mut driver = BridgeGameDriver::new(Endpoint::default());
    driver.adjudicate(&cmd(1, 7, "SetClientRole", &["Client"])).unwrap();
    assert_eq!(driver.adjudicate(&cmd(2, 7, "SetWorldTime", &["(3;7;38)"])).unwrap(),
        format!("ERR|2|UnauthorizedClientInsertion|SetWorldTime|{}",
            hex_encode("Only server-role players may set world time.")).as_bytes());
    driver.release_player(7);
    // The old role must not return when the player uses an actor command.
    assert_eq!(driver.adjudicate(&cmd(3, 7, "SetWorldTime", &["(3;7;38)"])).unwrap(), b"ACK|3|SetWorldTime");
}
#[test]
fn m2_pick_failure_codes_keep_the_server_tooltip() {
    for (reason, code) in [("NotRun", RefusalCode::NotRun), ("FailedToStart", RefusalCode::FailedToStart)] {
        assert_eq!(simlink_headless_server::wire_v1::game_refusal(&format!("{reason} | tooltip: Too tired")),
            (code, hex_encode("Too tired")));
        assert_eq!(simlink_headless_server::wire_v1::game_refusal(reason), (code, String::new()));
    }
}

#[test]
fn b2_stray_cr_refuses_line_without_closing_welcomed_socket() {
    let server = common::server();
    let mut peer = common::Peer::connect(&server);
    assert!(peer.exchange("CMD|1|JoinSession|1|7|61|~").starts_with("ACK|1|JoinSession|resume_token="));
    assert_eq!(peer.exchange("CMD|2|SetClientRole|2|7|Cli\rent"),
        "ERR|2|bad_line|SetClientRole|");
    assert_eq!(peer.exchange("CMD|3|SetClientRole|3|7|Client"), "ACK|3|SetClientRole");
    server.shutdown().unwrap();
}

#[test]
fn b1_claim_succeeds_when_lot_snapshot_fails() {
    let endpoint = Endpoint { fail_snapshot: true, ..Endpoint::default() };
    let mut driver = BridgeGameDriver::new(endpoint);
    let reply = driver.adjudicate_for(simlink_headless_server::ConnId(1),
        &cmd(1, 7, "ClaimSim", &["9007199254740993"])).unwrap();
    assert_eq!(reply, b"ACK|1|ClaimSim|9007199254740993");
    let events = driver.poll_routed_events().unwrap();
    assert!(events.iter().any(|event| event.event.kind == "SimClaimed"
        && event.event.fields == ["7", "9007199254740993", "~"]));
    assert!(events.iter().any(|event| event.event.kind == "SimAutonomyChanged"
        && event.event.fields == ["9007199254740993", "1"]));
}

#[test]
fn m3_bounded_reader_excludes_exactly_one_cr_before_lf() {
    let mut exact = vec![b'x'; PUBLIC_LINE_MAX_BYTES];
    exact.extend_from_slice(b"\r\n");
    for capacity in [1, 8192, PUBLIC_LINE_MAX_BYTES + 2] {
        let mut reader = BufReader::with_capacity(capacity, &exact[..]);
        assert_eq!(read_bounded_line(&mut reader).unwrap().unwrap().len(), PUBLIC_LINE_MAX_BYTES);
    }
    let mut oversized = vec![b'x'; PUBLIC_LINE_MAX_BYTES + 1];
    oversized.extend_from_slice(b"\r\n");
    assert_eq!(read_bounded_line(&mut &oversized[..]), Err(RefusalCode::LineTooLong));
    let mut extra_cr = vec![b'x'; PUBLIC_LINE_MAX_BYTES];
    extra_cr.extend_from_slice(b"\r\r\n");
    assert_eq!(read_bounded_line(&mut &extra_cr[..]), Err(RefusalCode::LineTooLong));
}
#[test]
fn m3_tcp_line_at_content_cap_accepts_crlf_and_refuses_one_more_byte() {
    let server = common::server();
    for extra in [0, 1] {
        let mut socket = TcpStream::connect(server.local_addr()).unwrap();
        socket.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
        socket.set_write_timeout(Some(Duration::from_secs(10))).unwrap();
        let mut reader = BufReader::new(socket.try_clone().unwrap());
        socket.write_all(b"HELLO|1|ring=1,wfs=1,role=client\r\n").unwrap();
        let mut welcome = String::new();
        reader.read_line(&mut welcome).unwrap();
        assert!(welcome.starts_with("WELCOME|1|"));
        let prefix = b"CMD|1|NoSuchVerb|1|7|";
        let mut line = prefix.to_vec();
        line.resize(PUBLIC_LINE_MAX_BYTES + extra, b'0');
        line.extend_from_slice(b"\r\n");
        socket.write_all(&line).unwrap();
        let mut reply = String::new();
        reader.read_line(&mut reply).unwrap();
        // At the exact cap the command reaches the unjoined authority gate;
        // overflow is refused by framing before that gate.
        let expected = if extra == 0 { "ERR|1|NotJoined|NoSuchVerb|" } else { "ERR|0|line_too_long|-|" };
        assert_eq!(reply.trim_end(), expected);
    }
    server.shutdown().unwrap();
}
