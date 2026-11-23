#![forbid(unsafe_code)]

mod common;

use std::io::{BufRead, BufReader, Write};
use std::net::TcpListener;
use std::thread::{self, JoinHandle};
use std::time::Duration;

fn scripted_peer(exchanges: Vec<(&'static str, &'static str)>) -> (common::Peer, JoinHandle<()>) {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let addr = listener.local_addr().unwrap();
    let scripted = thread::spawn(move || {
        let (stream, _) = listener.accept().unwrap();
        stream.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
        stream.set_write_timeout(Some(Duration::from_secs(10))).unwrap();
        let mut reader = BufReader::new(stream);
        for (request, response) in std::iter::once(
            ("HELLO|1|ring=1,wfs=1,role=client", "WELCOME|1|1|ring=1,wfs=1\n"),
        ).chain(exchanges) {
            let mut input = String::new();
            assert_ne!(reader.read_line(&mut input).unwrap(), 0);
            assert_eq!(input.trim_end(), request);
            reader.get_mut().write_all(response.as_bytes()).unwrap();
        }
    });

    (common::Peer::connect_to(addr), scripted)
}

#[test]
fn unsolicited_event_before_reply_does_not_satisfy_exchange() {
    let (mut peer, scripted) = scripted_peer(vec![
        ("CMD|42|JoinSession|42|7|61|~", "ACK|42|JoinSession|resume_token=0123456789abcdef0123456789abcdef\n"),
        ("CMD|43|NoSuchVerb|43|7",
            "EV|1|1|0|LotLocalAuthorityModeChanged|0\nERR|43|bad_line|NoSuchVerb|\n"),
    ]);
    assert!(peer.exchange("CMD|42|JoinSession|42|7|61|~").starts_with("ACK|42|JoinSession|"));
    let reply = peer.exchange("CMD|43|NoSuchVerb|43|7");
    scripted.join().unwrap();
    assert_eq!(reply, "ERR|43|bad_line|NoSuchVerb|");
    assert_eq!(peer.pending, ["EV|1|1|0|LotLocalAuthorityModeChanged|0"]);
}

#[test]
fn correlation_matching_keeps_events_and_other_replies_in_wire_order() {
    let (mut peer, scripted) = scripted_peer(vec![
        ("CMD|43|ClaimSim|43|7|9",
            "EV|43|1|43|SimClaimed|7|9|~\nACK|430|ClaimSim|8\nACK|43|ClaimSim|9\r\n"),
        ("CMD|44|AckSnapshot|44|7|1",
            "EV|44|2|0|SimAutonomyChanged|9|1\nACK|44|AckSnapshot\n"),
    ]);
    assert_eq!(peer.exchange("CMD|43|ClaimSim|43|7|9"), "ACK|43|ClaimSim|9");
    assert_eq!(peer.exchange("CMD|44|AckSnapshot|44|7|1"), "ACK|44|AckSnapshot");
    scripted.join().unwrap();
    assert_eq!(peer.pending, [
        "EV|43|1|43|SimClaimed|7|9|~",
        "ACK|430|ClaimSim|8",
        "EV|44|2|0|SimAutonomyChanged|9|1",
    ]);
}

#[test]
fn malformed_requests_keep_their_error_correlations_and_payload_bytes() {
    let (mut peer, scripted) = scripted_peer(vec![
        ("CMD|44|ClaimSim|44", "EV|1|1|0|LotLocalAuthorityModeChanged|0\nERR|44|bad_line|ClaimSim|\n"),
        ("garbage", "ERR|0|bad_line|-| \r\n"),
        ("CMD|bad|ClaimSim|45|7|9", "ERR|0|bad_line|ClaimSim|\n"),
    ]);
    assert_eq!(peer.exchange("CMD|44|ClaimSim|44"), "ERR|44|bad_line|ClaimSim|");
    assert_eq!(peer.exchange("garbage"), "ERR|0|bad_line|-| ");
    assert_eq!(peer.exchange("CMD|bad|ClaimSim|45|7|9"), "ERR|0|bad_line|ClaimSim|");
    scripted.join().unwrap();
    assert_eq!(peer.pending, ["EV|1|1|0|LotLocalAuthorityModeChanged|0"]);
}

#[test]
fn event_sent_after_join_reply_remains_available_after_the_next_reply() {
    let (mut peer, scripted) = scripted_peer(vec![
        ("CMD|42|JoinSession|42|7|61|~",
            "ACK|42|JoinSession|resume_token=0123456789abcdef0123456789abcdef\nEV|1|1|0|LotLocalAuthorityModeChanged|0\n"),
        ("CMD|43|NoSuchVerb|43|7", "ERR|43|bad_line|NoSuchVerb|\n"),
    ]);
    assert!(peer.exchange("CMD|42|JoinSession|42|7|61|~").starts_with("ACK|42|JoinSession|resume_token="));
    assert_eq!(peer.exchange("CMD|43|NoSuchVerb|43|7"), "ERR|43|bad_line|NoSuchVerb|");
    scripted.join().unwrap();
    assert!(peer.pending.iter().any(|line| line.split('|').nth(4) == Some("LotLocalAuthorityModeChanged")));
    common::artifact("reply_harness.txt", &peer.pending.into_iter().collect::<Vec<_>>());
}

#[test]
fn real_server_keeps_handshake_and_idless_error_contracts() {
    let server = common::server();
    let mut peer = common::Peer::connect(&server);
    assert!(peer.exchange("CMD|42|JoinSession|42|7|61|~").starts_with("ACK|42|JoinSession|resume_token="));
    assert_eq!(peer.exchange("CMD|43|NoSuchVerb|43|7"), "ERR|43|bad_line|NoSuchVerb|");
    assert_eq!(peer.exchange("garbage"), "ERR|0|bad_line|-|");
    server.shutdown().unwrap();
}
