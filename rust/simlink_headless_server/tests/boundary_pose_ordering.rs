#![forbid(unsafe_code)]
//! Regression from the 070803 ride: a frozen parent pose preceded older held poses.
pub mod common;

use simlink_headless_server::{
    messages::{self as m, Command}, serve::{RunningServer, StreamOptions},
    transport::{StreamReply, StreamRequest}, DriverError, FakeDriver, GameDriver,
};
use std::{
    io::{BufRead, BufReader, Write}, net::{TcpListener, TcpStream},
    sync::{Arc, atomic::{AtomicBool, AtomicU64, Ordering}}, time::{Duration, Instant},
};

fn pose(sequence: u64, object: u64, x: u64) -> String {
    format!("{sequence}|Ob|{object}|SetTransform:m|1~0~0~0~0~1~0~0~0~0~1~0~{x}~0~0~1")
}
const AUDIO: &str = "81|Au|42|o|55|66697874757265|0";
const EFFECT: &str = "82|Vx|42|88|s|0|0";
const UNCOVERED_STATE: &str = "84|Ob|42|EnableDropShadow:b|0";
const ACQUIRE: &str = "0|A|7|20|1|2|3|-2";
const STATE: &str = "0|E|7|5|6|0|20";

#[derive(Default)]
struct Shared {
    clock: AtomicU64,
    fresh: AtomicBool,
    boundary_complete: AtomicBool,
    dump_sent: AtomicBool,
    driver_waiting: AtomicBool,
    release_driver: AtomicBool,
}
struct Driver { fake: FakeDriver, shared: Arc<Shared>, pending: Option<StreamReply> }
impl GameDriver for Driver {
    fn adjudicate(&mut self, command: &Command) -> Result<Vec<u8>, DriverError> {
        self.fake.adjudicate(command)
    }
    fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, DriverError> { Ok(vec![]) }
    fn claimed_sims(&self) -> Vec<u64> {
        if self.shared.fresh.load(Ordering::Acquire) { vec![10, 20] } else { vec![10] }
    }
    fn start_stream_request(&mut self, request: &StreamRequest) -> Result<Option<StreamReply>, DriverError> {
        assert!(self.pending.is_none(), "one native request retains ownership until its reply");
        let StreamRequest::Recorder(args) = request else {
            return Ok(Some(match request {
                StreamRequest::DiagState => StreamReply::DiagState(None),
                StreamRequest::Snapshot(_) => StreamReply::Snapshot(None),
                other => panic!("unexpected request {other:?}"),
            }));
        };
        let fresh = self.shared.fresh.load(Ordering::Acquire);
        let report = match args[0].as_str() {
            "ring-on" => "recorder=on;ring=on".into(),
            "snapshot" if fresh && args[1] == "20" => "sims=20;drivers=4294967280,7".into(),
            "snapshot" => "sims=10;drivers=-".into(),
            "snapshot-driver" if args[1] == "4294967280" => {
                assert_eq!(&args[2], "0");
                assert_eq!(&args[4], "20", "immutable explicit snapshot scope");
                self.shared.boundary_complete.store(true, Ordering::Release);
                format!("lines=1;next=-1;snapshot={};cursor=100\n{}", args[3], pose(0, 42, 100))
            }
            "snapshot-driver" if args[1] == "7" && args[2] == "0" => {
                format!("lines=1;next=1;snapshot={};cursor=120\n{ACQUIRE}", args[3])
            }
            "snapshot-driver" if args[1] == "7" && args[2] == "1" => {
                self.shared.driver_waiting.store(true, Ordering::Release);
                self.pending = Some(StreamReply::Recorder(format!(
                    "lines=1;next=-1;snapshot={};cursor=120\n{STATE}", args[3])));
                return Ok(None);
            }
            "dump" if fresh && self.shared.boundary_complete.load(Ordering::Acquire)
                && !self.shared.dump_sent.swap(true, Ordering::AcqRel) => {
                // Native source sequence80 predates the Boundary cut100. The
                // unrelated/transient rows must survive;110 is genuinely newer.
                format!("seq=120;more=0\n{}\n{AUDIO}\n{EFFECT}\n{}\n{UNCOVERED_STATE}\n{}",
                    pose(80, 42, 80), pose(83, 99, 83), pose(110, 42, 110))
            }
            "dump" => if self.shared.dump_sent.load(Ordering::Acquire) {
                "seq=120;more=0".into()
            } else { "seq=50;more=0".into() },
            other => panic!("unexpected recorder action {other}"),
        };
        Ok(Some(StreamReply::Recorder(report)))
    }
    fn poll_stream_request(&mut self) -> Result<Option<StreamReply>, DriverError> {
        if self.shared.release_driver.load(Ordering::Acquire) { Ok(self.pending.take()) }
        else { Ok(None) }
    }
    fn stream_request_pending(&self) -> bool { self.pending.is_some() }
}

fn report(server: &RunningServer) -> String {
    let mut socket = TcpStream::connect(server.admin_addr().unwrap()).unwrap();
    socket.set_read_timeout(Some(Duration::from_secs(2))).unwrap();
    writeln!(socket, "FRONT_DOOR_STATE").unwrap();
    let mut line = String::new();
    BufReader::new(socket).read_line(&mut line).unwrap();
    let response: serde_json::Value = serde_json::from_str(&line).unwrap();
    assert_eq!(response["accepted"], true);
    response["report"].as_str().unwrap().into()
}
fn wait(mut condition: impl FnMut() -> bool) {
    let end = Instant::now() + Duration::from_secs(5);
    while !condition() {
        assert!(Instant::now() < end, "fixture scheduling deadline");
        std::thread::sleep(Duration::from_millis(5));
    }
}
struct Reader { peer: common::Peer, last: Option<u64>, records: Vec<String> }
impl Reader {
    fn through(&mut self, expected: &str) {
        let end = Instant::now() + Duration::from_secs(5);
        while !self.records.iter().any(|line| line == expected) {
            assert!(Instant::now() < end, "record deadline: {expected}; retained {:?}", self.records);
            let line = self.peer.next().expect("peer remains live");
            if !line.starts_with("EV|") { continue; }
            let fields = simlink_headless_server::split(line.as_bytes()).unwrap();
            let event = simlink_headless_server::decode_event(&fields).unwrap();
            if let Some(last) = self.last { assert_eq!(event.seq, last + 1, "contiguous public events"); }
            self.last = Some(event.seq);
            if event.kind == "SimAnimation" {
                self.records.extend(m::dec_list(&event.fields[0]).unwrap().into_iter()
                    .map(|line| m::hex_decode(&line).unwrap()));
            }
        }
    }
}

#[test]
fn normal_serve_does_not_rewind_frozen_parent_with_older_held_full_transforms() {
    let shared = Arc::new(Shared::default());
    let source = Arc::clone(&shared);
    let clock = Arc::clone(&shared);
    let server = RunningServer::start_configured_with_clock(
        TcpListener::bind("127.0.0.1:0").unwrap(), Some(TcpListener::bind("127.0.0.1:0").unwrap()),
        move || Ok(Driver { fake: FakeDriver::default(), shared: source, pending: None }), 8,
        StreamOptions { base_interval_ms: 0.0, ..StreamOptions::default() },
        move || clock.clock.load(Ordering::Acquire),
    ).unwrap();
    let mut peers = Vec::new();
    for player in [1, 2] {
        let mut peer = common::Peer::connect(&server);
        assert!(peer.exchange(&format!("CMD|1|JoinSession|1|{player}|61|~")).starts_with("ACK|1|JoinSession"));
        peers.push(Reader { peer, last: None, records: Vec::new() });
    }
    shared.clock.store(1000, Ordering::Release);
    wait(|| {
        let value = report(&server);
        value.contains("client|1|") && value.contains("client|2|")
            && value.contains("presentation_join_history_pending=0")
            && value.contains("presentation_fresh_history_pending=0")
    });
    shared.fresh.store(true, Ordering::Release);
    shared.clock.store(2000, Ordering::Release);
    wait(|| shared.driver_waiting.load(Ordering::Acquire));
    assert!(shared.dump_sent.load(Ordering::Acquire));
    for reader in &mut peers {
        reader.through(&pose(83, 99, 83));
        assert!(!reader.records.iter().any(|line| line == &pose(0, 42, 100)
            || line == &pose(80, 42, 80) || line == &pose(110, 42, 110)),
            "affected parent stays protected through the unrelated driver history");
    }
    assert!(report(&server).contains("presentation_fresh_history_pending=1"));
    shared.release_driver.store(true, Ordering::Release);
    for reader in &mut peers { reader.through(&pose(110, 42, 110)); }
    let actual: Vec<_> = peers.iter().map(|reader| reader.records.clone()).collect();
    // Same essential shape as live070803: seq0 parent transform at a frozen
    // cut, followed by an older held positive transform, then newer live state.
    // A pose-only coverage repair may supersede80; no other row is covered.
    let expected = vec![AUDIO.into(), EFFECT.into(), pose(83, 99, 83), pose(0, 42, 100),
        ACQUIRE.into(), STATE.into(), UNCOVERED_STATE.into(), pose(110, 42, 110)];
    assert_eq!(actual, vec![expected.clone(), expected],
        "a frozen parent must not rewind; uncovered state, unrelated objects and transient effects retain bytes/order on BOTH real sockets");
    server.shutdown().unwrap();
}
