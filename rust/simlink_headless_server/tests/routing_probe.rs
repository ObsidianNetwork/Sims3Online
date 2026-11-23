#![forbid(unsafe_code)]

pub mod common;

use simlink_headless_server::{ConnId, Server};
use simlink_headless_server::routing::{recipients, RoutedEvent};
use simlink_headless_server::messages::Event;
use std::net::{TcpListener, TcpStream};
use std::sync::{mpsc, Arc, Mutex};
use std::time::Duration;
use simlink_headless_server::{BridgeGameDriver, DriverError, GameDriver};
use simlink_headless_server::bridge_driver::OfflineBridge;
use simlink_headless_server::messages::Command;

#[test]
fn release_broadcast_keeps_departing_requester_without_admin_or_superseded_leaks() {
    let mut server = Server::new(1);
    for id in 1..=3 { server.connect(ConnId(id)); }
    server.join(ConnId(1), 10);
    server.join(ConnId(2), 20);
    server.set_admin(ConnId(3));
    for kind in ["SimReleased", "SimAutonomyChanged"] {
        let event = RoutedEvent { event: Event { seq: 0, tick: 1, corr: 7, kind: kind.into(), fields: vec![] },
            owner: None, requester: Some(ConnId(1)) };
        assert_eq!(recipients(&server, &event), [ConnId(1), ConnId(2)]);
    }
    server.leave(ConnId(1));
    for kind in ["SimReleased", "SimAutonomyChanged"] {
        let mut event = RoutedEvent { event: Event { seq: 0, tick: 1, corr: 7, kind: kind.into(), fields: vec![] },
            owner: None, requester: Some(ConnId(1)) };
        assert_eq!(recipients(&server, &event), [ConnId(1), ConnId(2)]);
        event.requester = Some(ConnId(3));
        assert_eq!(recipients(&server, &event), [ConnId(2)]);
        event.requester = None;
        assert_eq!(recipients(&server, &event), [ConnId(2)]);
    }
    server.join(ConnId(1), 10);
    server.connect(ConnId(4));
    server.join(ConnId(4), 10);
    let event = RoutedEvent { event: Event { seq: 0, tick: 1, corr: 7, kind: "SimReleased".into(), fields: vec![] },
        owner: None, requester: Some(ConnId(1)) };
    assert_eq!(recipients(&server, &event), [ConnId(2), ConnId(4)]);
}

#[test]
fn two_claimed_players_and_admin_receive_exact_recipient_sets() {
    let mut server = Server::new(1);
    for id in 1..=3 { server.connect(ConnId(id)); }
    server.join(ConnId(1), 10);
    server.join(ConnId(2), 20);
    server.set_admin(ConnId(3));
    let mut rows = Vec::new();
    let mut leak_events = 0;
    for (kind, owner, requester, expected) in [
        ("SimClaimed", Some(10), Some(ConnId(1)), vec![ConnId(1), ConnId(2)]),
        ("SimAnimation", None, None, vec![ConnId(1), ConnId(2)]),
        ("SnapshotDelta", None, None, vec![ConnId(1), ConnId(2)]),
        ("DialogRequested", Some(10), None, vec![ConnId(1)]),
        ("Sting", Some(20), None, vec![ConnId(2)]),
        ("PickFailed", Some(10), Some(ConnId(1)), vec![ConnId(1)]),
        ("SimDescriptionData", None, Some(ConnId(2)), vec![ConnId(2)]),
        ("WorldFileChunk", None, Some(ConnId(2)), vec![ConnId(2)]),
        ("SimDescriptionData", None, Some(ConnId(3)), vec![ConnId(3)]),
        ("DialogRequested", None, None, vec![]),
        ("LotLocalAuthorityModeChanged", None, Some(ConnId(1)), vec![ConnId(1)]),
        ("LotLocalAuthorityModeChanged", Some(20), None, vec![ConnId(2)]),
        ("LotLocalAuthorityModeChanged", Some(10), Some(ConnId(2)), vec![ConnId(2)]),
        ("SimReleased", Some(10), Some(ConnId(3)), vec![ConnId(1), ConnId(2)]),
        ("SimAutonomyChanged", Some(10), Some(ConnId(3)), vec![ConnId(1), ConnId(2)]),
    ] {
        let event = RoutedEvent {
            event: Event { seq: 0, tick: 1, corr: 0, kind: kind.into(), fields: vec![] },
            owner, requester,
        };
        let got = recipients(&server, &event);
        leak_events += got.iter().filter(|id| !expected.contains(id)).count();
        assert_eq!(got, expected);
        rows.push(format!("{{\"kind\":\"{kind}\",\"recipients\":{:?}}}", got.iter().map(|id| id.0).collect::<Vec<_>>()));
    }
    assert_eq!(leak_events, 0);
    rows.push(format!("{{\"leak_events\":{leak_events}}}"));
    assert!(!rows.is_empty());
}

struct Scripted {
    driver: BridgeGameDriver<OfflineBridge>,
    events: Arc<Mutex<Vec<RoutedEvent>>>,
    drained: mpsc::Sender<()>,
    connections: Arc<Mutex<std::collections::HashMap<i32, ConnId>>>,
}
impl GameDriver for Scripted {
    fn adjudicate(&mut self, cmd: &Command) -> Result<Vec<u8>, DriverError> { self.driver.adjudicate(cmd) }
    fn adjudicate_for(&mut self, id: ConnId, cmd: &Command) -> Result<Vec<u8>, DriverError> {
        let reply = self.driver.adjudicate_for(id, cmd)?;
        if cmd.verb == "JoinSession" && reply.starts_with(b"ACK|") {
            self.connections.lock().unwrap().insert(cmd.player_id, id);
        }
        Ok(reply)
    }
    fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, DriverError> { Ok(Vec::new()) }
    fn poll_routed_events(&mut self) -> Result<Vec<RoutedEvent>, DriverError> {
        let mut events = std::mem::take(&mut *self.events.lock().unwrap());
        if !events.is_empty() {
            for event in &mut events {
                if event.event.kind == "DialogRequested" {
                    event.owner = event.event.fields.get(1).and_then(|s| s.parse().ok()).and_then(|sim| self.driver.owner(sim));
                }
            }
            self.drained.send(()).unwrap();
        }
        Ok(events)
    }
}
struct Peer(common::Peer);
impl Peer {
    fn new(addr: std::net::SocketAddr) -> Self {
        let socket = TcpStream::connect(addr).unwrap();
        socket.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
        Self(common::Peer::from(socket))
    }
    fn exchange(&mut self, line: &str) -> String { self.0.exchange(line) }
    fn through_barrier(&mut self, corr: u64, player: i32) -> Vec<String> {
        assert_eq!(self.exchange(&format!("CMD|{corr}|AckSnapshot|{corr}|{player}|1")),
            format!("ACK|{corr}|AckSnapshot"));
        self.0.pending.drain(..).inspect(|line| {
            assert!(line.starts_with("EV|"), "unexpected line {line}");
        }).collect()
    }
}

#[test]
fn real_sockets_with_claims_prove_admin_and_other_player_non_leak() {
    let events = Arc::new(Mutex::new(Vec::new()));
    let (tx, rx) = mpsc::channel();
    let connections = Arc::new(Mutex::new(std::collections::HashMap::new()));
    let driver = Scripted { driver: BridgeGameDriver::new(OfflineBridge), events: Arc::clone(&events), drained: tx, connections: Arc::clone(&connections) };
    let server = simlink_headless_server::serve::RunningServer::start_configured(
        TcpListener::bind("127.0.0.1:0").unwrap(), Some(TcpListener::bind("127.0.0.1:0").unwrap()),
        move || Ok(driver), 8, simlink_headless_server::serve::StreamOptions::default()).unwrap();
    let mut first = Peer::new(server.local_addr());
    let mut second = Peer::new(server.local_addr());
    let mut admin = Peer::new(server.admin_addr().unwrap());
    assert!(first.exchange("HELLO|1|ring=1,wfs=1").starts_with("WELCOME"));
    assert!(second.exchange("HELLO|1|ring=1,wfs=1").starts_with("WELCOME"));
    assert!(admin.exchange("HELLO|1|").starts_with("WELCOME"));
    assert!(first.exchange("CMD|1|JoinSession|1|10|61|~").starts_with("ACK|1|JoinSession|resume_token="));
    assert!(second.exchange("CMD|1|JoinSession|1|20|62|~").starts_with("ACK|1|JoinSession|resume_token="));
    assert_eq!(first.exchange("CMD|2|ClaimSim|2|10|55"), "ACK|2|ClaimSim|55");
    assert_eq!(second.exchange("CMD|2|ClaimSim|2|20|66"), "ACK|2|ClaimSim|66");
    let mut rows = Vec::new();
    let mut leaks = 0usize;
    for (index, (kind, fields, requester, expected)) in [
        ("DialogRequested", vec!["7", "55", "5374696e67", "[]"], None, vec![1]),
        ("SimDescriptionData", vec!["9", "[]", "[]", "[]"], Some(connections.lock().unwrap()[&20]), vec![2]),
        ("SimAnimation", vec!["[6f7061717565]"], None, vec![1, 2]),
    ].into_iter().enumerate() {
        events.lock().unwrap().push(RoutedEvent {
            event: Event { seq: 0, tick: index as u64 + 1, corr: 0, kind: kind.into(), fields: fields.into_iter().map(str::to_owned).collect() },
            owner: None, requester,
        });
        rx.recv_timeout(Duration::from_secs(10)).unwrap();
        let corr = index as u64 + 10;
        let delivered = [first.through_barrier(corr, 10), second.through_barrier(corr, 20), admin.through_barrier(corr, 99)];
        let recipients: Vec<_> = delivered.iter().enumerate().filter_map(|(i, events)| (!events.is_empty()).then_some(i + 1)).collect();
        leaks += recipients.iter().filter(|id| !expected.contains(id)).count();
        assert_eq!(recipients, expected);
        rows.push(serde_json::json!({"kind":kind,"recipients":recipients,"socket_lines":delivered}).to_string());
    }
    rows.push(serde_json::json!({"leak_events":leaks}).to_string());
    assert_eq!(leaks, 0);
    if let Some(dir) = std::env::var_os("CARD58_ARTIFACT_DIR") {
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::write(std::path::Path::new(&dir).join("routing_probe.jsonl"), rows.join("\n")).unwrap();
    }
    server.shutdown().unwrap();
}
