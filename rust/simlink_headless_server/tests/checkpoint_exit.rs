#![forbid(unsafe_code)]

pub mod common;

use std::io::{BufRead, BufReader, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::{atomic::{AtomicBool, Ordering}, mpsc, Arc};
use std::time::Duration;

use simlink_bridge::verbs::BridgeCommand;
use simlink_headless_server::bridge_driver::{BridgeEndpoint, BridgeGameDriver};
use simlink_headless_server::messages::hex_encode;
use simlink_headless_server::serve::{RunningServer, StreamOptions};
use simlink_headless_server::DriverError;

struct ExitingBridge {
    exited: Arc<AtomicBool>,
    started: mpsc::Sender<()>,
    fail_checkpoint_poll: bool,
}

impl BridgeEndpoint for ExitingBridge {
    // Suppress join snapshot IO; checkpoint IO and terminal polling still use
    // the real BridgeGameDriver and socket actor.
    fn connected(&self) -> bool { false }
    fn command(&mut self, _: &BridgeCommand) -> Result<Vec<String>, DriverError> { Ok(vec![]) }
    fn begin_checkpoint_command(&mut self, command: &BridgeCommand, _: Duration) -> Result<Option<Vec<String>>, DriverError> {
        if matches!(command, BridgeCommand::SaveGame { .. }) {
            self.started.send(()).unwrap();
            Ok(None)
        } else {
            Ok(Some(vec![]))
        }
    }
    fn poll_checkpoint_command(&mut self) -> Result<Option<Vec<String>>, DriverError> {
        if self.fail_checkpoint_poll && self.exited.load(Ordering::Acquire) {
            Err(DriverError("process_exited".into()))
        } else { Ok(None) }
    }
    fn events(&mut self) -> Result<Vec<Vec<u8>>, DriverError> {
        if self.exited.load(Ordering::Acquire) {
            Err(DriverError("process_exited".into()))
        } else { Ok(vec![]) }
    }
}

fn line(peer: &mut BufReader<TcpStream>) -> Option<String> {
    let mut text = String::new();
    (peer.read_line(&mut text).unwrap() != 0).then(|| text.trim_end().to_owned())
}

#[test]
fn checkpoint_join_event_cannot_be_taken_as_save_reply() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let addr = listener.local_addr().unwrap();
    let scripted = std::thread::spawn(move || {
        let (socket, _) = listener.accept().unwrap();
        socket.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
        let mut socket = BufReader::new(socket);
        for (request, response) in [
            ("HELLO|1|ring=1,wfs=1,role=client", "WELCOME|1|1|ring=1,wfs=1"),
            ("CMD|1|JoinSession|1|7|61|~", "ACK|1|JoinSession|resume_token=0123456789abcdef0123456789abcdef"),
            ("CMD|2|SaveGame|102|7|5465726d696e616c",
                "EV|2|0|1|LotLocalAuthorityModeChanged|7|FullPuppetFallback\nACK|2|SaveGame"),
        ] {
            assert_eq!(line(&mut socket).as_deref(), Some(request));
            writeln!(socket.get_mut(), "{response}").unwrap();
        }
    });
    let mut peer = common::Peer::connect_to(addr);
    assert!(peer.exchange("CMD|1|JoinSession|1|7|61|~").starts_with("ACK|1|JoinSession|"));
    let reply = peer.exchange("CMD|2|SaveGame|102|7|5465726d696e616c");
    scripted.join().unwrap();
    assert_eq!(reply, "ACK|2|SaveGame");
    assert_eq!(peer.next().as_deref(),
        Some("EV|2|0|1|LotLocalAuthorityModeChanged|7|FullPuppetFallback"));
    assert_eq!(peer.next(), None);
}

fn checkpoint_exit(fail_checkpoint_poll: bool) {
    let exited = Arc::new(AtomicBool::new(false));
    let (started, ready) = mpsc::channel();
    let endpoint = ExitingBridge { exited: Arc::clone(&exited), started, fail_checkpoint_poll };
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let server = RunningServer::start_configured(listener, None,
        move || Ok(BridgeGameDriver::new(endpoint)), 8,
        StreamOptions { base_interval_ms: 0.0, ..StreamOptions::default() }).unwrap();
    let mut peer = common::Peer::connect(&server);
    assert!(peer.exchange("CMD|1|JoinSession|1|7|61|~").starts_with("ACK|1|JoinSession|"));
    for (corr, verb, arg) in [(2, "SaveGame", "|5465726d696e616c"),
        (3, "RequestWorld", ""), (4, "SaveGame", "|517565756564")]
    {
        assert_eq!(peer.exchange(&format!("CMD|{corr}|{verb}|{}|7{arg}", corr + 100)),
            format!("ACK|{corr}|{verb}"));
    }
    ready.recv_timeout(Duration::from_secs(10)).unwrap();
    exited.store(true, Ordering::Release);
    let mut refusals = Vec::new();
    for text in peer {
        if text.split('|').nth(4) == Some("CommandRejected") { refusals.push(text); }
    }
    server.wait().unwrap();
    let mut last_seq = 0;
    for (index, corr) in (2..=4).enumerate() {
        let text = refusals.get(index).unwrap_or_else(||
            panic!("every accepted checkpoint must be refused once before EOF: {refusals:?}"));
        let fields: Vec<_> = text.split('|').collect();
        let seq: u64 = fields[1].parse().unwrap();
        assert!(seq > last_seq);
        last_seq = seq;
        assert_eq!(fields[2..].join("|"),
            format!("0|{corr}|CommandRejected|{}|{}", corr + 100, hex_encode("process_exited")));
    }
    assert_eq!(refusals.len(), 3);
}

#[test]
fn checkpoint_poll_exit_flushes_active_and_queued_refusals_before_eof() {
    checkpoint_exit(true);
}

#[test]
fn event_poll_exit_flushes_active_and_queued_refusals_before_eof() {
    checkpoint_exit(false);
}

struct ExitAfterEvents;
impl simlink_headless_server::GameDriver for ExitAfterEvents {
    fn adjudicate(&mut self, _: &simlink_headless_server::messages::Command) -> Result<Vec<u8>, DriverError> { unreachable!() }
    fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, DriverError> {
        Ok(vec![format!("EV|0|7|42|CommandRejected|142|{}", hex_encode("process_exited")).into_bytes()])
    }
    fn diag_state(&mut self) -> Result<Option<String>, DriverError> {
        Err(DriverError("process_exited".into()))
    }
}

#[test]
fn later_stream_failure_keeps_collected_events_and_command_failures_for_terminal_drain() {
    use simlink_headless_server::{ConnId, Server};
    use simlink_headless_server::stream::Stream;
    let mut server = Server::new(1);
    server.connect(ConnId(1));
    server.join(ConnId(1), 7);
    let mut stream = Stream::new(StreamOptions::default());
    stream.command_failed(ConnId(1), 41, "prior command failure");
    let mut driver = ExitAfterEvents;
    let error = stream.pump(0, &mut server, &mut driver).unwrap_err();
    let events = stream.terminal_events(&mut driver, &error);
    assert_eq!(events.len(), 2);
    assert_eq!((events[0].event.corr, events[0].event.kind.as_str()), (41, "ServerError"));
    assert_eq!((events[1].event.corr, events[1].event.kind.as_str()), (42, "CommandRejected"));
    assert!(stream.terminal_events(&mut driver, &error).is_empty());
}

struct StalledWorld {
    active: bool,
    sent: bool,
    conn: Option<simlink_headless_server::ConnId>,
    exited: Arc<AtomicBool>,
    queued: Option<mpsc::Sender<()>>,
    failed: Option<mpsc::Sender<()>>,
}
impl simlink_headless_server::GameDriver for StalledWorld {
    fn adjudicate_for(&mut self, conn: simlink_headless_server::ConnId, cmd: &simlink_headless_server::messages::Command) -> Result<Vec<u8>, DriverError> {
        self.conn = Some(conn);
        self.adjudicate(cmd)
    }
    fn adjudicate(&mut self, cmd: &simlink_headless_server::messages::Command) -> Result<Vec<u8>, DriverError> {
        self.active = true;
        Ok(format!("ACK|{}|{}", cmd.corr, cmd.verb).into_bytes())
    }
    fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, DriverError> { unreachable!() }
    fn poll_routed_events(&mut self) -> Result<Vec<simlink_headless_server::routing::RoutedEvent>, DriverError> {
        if !self.active { return Ok(vec![]); }
        if !self.sent {
            self.sent = true;
            return Ok(vec![simlink_headless_server::routing::RoutedEvent {
                event: simlink_headless_server::messages::Event {
                    seq: 0, tick: 1, corr: 2, kind: "WorldFile".into(),
                    fields: vec![hex_encode("Stalled.sims3"), "0".into(), "1".into(),
                        hex_encode("world.nhd"), (32 * 1024 * 1024).to_string(),
                        simlink_headless_server::b64_encode(&vec![0; 32 * 1024 * 1024])],
                }, owner: None, requester: self.conn,
            }]);
        }
        // The next pump happens after the actor queued and tried to flush the
        // world file. The peer deliberately never reads that output.
        if let Some(queued) = self.queued.take() { queued.send(()).unwrap(); }
        if self.exited.load(Ordering::Acquire) {
            if let Some(failed) = self.failed.take() { failed.send(()).unwrap(); }
            Err(DriverError("process_exited".into()))
        } else { Ok(vec![]) }
    }
}

fn stalled_world() -> (RunningServer, common::Peer, Arc<AtomicBool>, mpsc::Receiver<()>) {
    let exited = Arc::new(AtomicBool::new(false));
    let (queued, queued_rx) = mpsc::channel();
    let (failed, failed_rx) = mpsc::channel();
    let driver = StalledWorld { active: false, sent: false, conn: None, exited: Arc::clone(&exited),
        queued: Some(queued), failed: Some(failed) };
    let server = RunningServer::start_configured(
        TcpListener::bind("127.0.0.1:0").unwrap(), Some(TcpListener::bind("127.0.0.1:0").unwrap()),
        move || Ok(driver), 8, StreamOptions { base_interval_ms: 0.0, ..StreamOptions::default() }).unwrap();
    let mut peer = common::Peer::connect(&server);
    assert!(peer.exchange("CMD|1|JoinSession|1|7|61|~").starts_with("ACK|1|JoinSession"));
    queued_rx.recv_timeout(Duration::from_secs(10)).unwrap();
    (server, peer, exited, failed_rx)
}

#[test]
fn process_exit_bounds_shutdown_when_world_peer_never_reads() {
    let (server, peer, exited, failed) = stalled_world();
    eprintln!("nonreading_peer={}", peer.as_ref().local_addr().unwrap());
    let shutdown = server.shutdown_handle();
    exited.store(true, Ordering::Release);
    failed.recv_timeout(Duration::from_secs(10)).unwrap();
    let (done, completion) = mpsc::channel();
    let waiter = std::thread::spawn(move || { done.send(server.wait()).unwrap(); });
    let result = completion.recv_timeout(Duration::from_secs(8));
    if result.is_err() { shutdown.shutdown(); }
    waiter.join().unwrap();
    assert!(result.is_ok(), "a peer that never reads must not keep the server alive");
    result.unwrap().unwrap();
    drop(peer);
}

fn admin_quit_during_drain(preconnected: bool) {
    let (server, peer, exited, failed) = stalled_world();
    let connect = || {
        let socket = TcpStream::connect(server.admin_addr().unwrap()).unwrap();
        socket.set_read_timeout(Some(Duration::from_secs(1))).unwrap();
        BufReader::new(socket)
    };
    let mut admin = preconnected.then(connect);
    if let Some(admin) = &mut admin {
        writeln!(admin.get_mut(), "FRONT_DOOR_STATE").unwrap();
        assert!(line(admin).unwrap().contains("\"ok\":true"));
    }
    exited.store(true, Ordering::Release);
    failed.recv_timeout(Duration::from_secs(10)).unwrap();
    let mut admin = admin.unwrap_or_else(connect);
    writeln!(admin.get_mut(), "QUIT").unwrap();
    assert!(line(&mut admin).unwrap().contains("QUIT"));
    server.wait().unwrap();
    drop(peer);
}

#[test]
fn new_admin_quit_remains_available_while_world_output_is_stalled() {
    admin_quit_during_drain(false);
}

#[test]
fn connected_admin_quit_remains_available_while_world_output_is_stalled() {
    admin_quit_during_drain(true);
}

struct InvalidEventBridge;
impl BridgeEndpoint for InvalidEventBridge {
    fn connected(&self) -> bool { false }
    fn command(&mut self, _: &BridgeCommand) -> Result<Vec<String>, DriverError> { Ok(vec![]) }
    fn begin_checkpoint_command(&mut self, _: &BridgeCommand, _: Duration) -> Result<Option<Vec<String>>, DriverError> { Ok(None) }
    fn poll_checkpoint_command(&mut self) -> Result<Option<Vec<String>>, DriverError> {
        Err(DriverError("process_exited".into()))
    }
    fn events(&mut self) -> Result<Vec<Vec<u8>>, DriverError> { Ok(vec![vec![0xff]]) }
}

#[test]
fn invalid_event_keeps_checkpoint_refusal_already_taken_from_pending_events() {
    use simlink_headless_server::{ConnId, GameDriver};
    let mut driver = BridgeGameDriver::new(InvalidEventBridge);
    let cmd = simlink_headless_server::messages::Command {
        corr: 2, command_id: 102, player_id: 7, verb: "SaveGame".into(), args: vec![hex_encode("Pending")] };
    assert!(driver.adjudicate_for(ConnId(1), &cmd).unwrap().starts_with(b"ACK|"));
    assert!(driver.poll_routed_events().is_err());
    let events = driver.terminal_events(&DriverError("process_exited".into()));
    assert_eq!(events.len(), 1);
    assert_eq!(events[0].event.kind, "CommandRejected");
    assert_eq!(events[0].event.corr, 2);
    assert_eq!(events[0].event.fields, ["102", &hex_encode("process_exited")]);
}

struct PersistentDiagFailure { active: bool, next: u64 }
impl simlink_headless_server::GameDriver for PersistentDiagFailure {
    fn adjudicate(&mut self, cmd: &simlink_headless_server::messages::Command) -> Result<Vec<u8>, DriverError> {
        self.active = true;
        Ok(format!("ACK|{}|{}", cmd.corr, cmd.verb).into_bytes())
    }
    fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, DriverError> {
        if !self.active { return Ok(vec![]); }
        self.next += 1;
        Ok(vec![format!("EV|0|1|0|GameSpeedChanged|{}", ["Pause", "Normal", "Double"][(self.next as usize - 1) % 3]).into_bytes()])
    }
    fn diag_state(&mut self) -> Result<Option<String>, DriverError> {
        Err(DriverError("diagnostic unavailable".into()))
    }
}

#[test]
fn persistent_nonterminal_diag_failure_publishes_events_each_pump() {
    let server = RunningServer::start_configured(TcpListener::bind("127.0.0.1:0").unwrap(), None,
        || Ok(PersistentDiagFailure { active: false, next: 0 }), 4, StreamOptions::default()).unwrap();
    let mut peer = common::Peer::connect(&server);
    assert!(peer.exchange("CMD|1|JoinSession|1|7|61|~").starts_with("ACK|1|JoinSession"));
    for expected in ["Pause", "Normal", "Double"] {
        let text = peer.next().unwrap();
        assert_eq!(text.split('|').nth(5), Some(expected));
    }
    server.shutdown().unwrap();
}
