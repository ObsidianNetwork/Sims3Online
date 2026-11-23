#![forbid(unsafe_code)]
//! Known recorder backlog must survive a finite actor work yield.
pub mod common;

use simlink_headless_server::{
    messages::{self as m, Command}, serve::{RunningServer, StreamOptions},
    snapshot::{Sim, Snapshot}, transport::{StreamReply, StreamRequest}, wire_v1 as w,
    DriverError, FakeDriver, GameDriver,
};
use std::{
    io::{BufRead, BufReader, Write}, net::{TcpListener, TcpStream},
    sync::{atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering}, Arc},
    time::{Duration, Instant},
};

#[derive(Default)]
struct Shared {
    clock: AtomicU64,
    enabled: AtomicBool,
    dumps: AtomicUsize,
    fair: bool,
    new_claim: AtomicBool,
    probes: AtomicUsize,
    exports: AtomicUsize,
    snapshots: AtomicUsize,
    fail_once: AtomicBool,
    failures: AtomicUsize,
    hold_reply: AtomicBool,
}
struct Driver { fake: FakeDriver, shared: Arc<Shared>, pending: Option<StreamReply> }
impl GameDriver for Driver {
    fn adjudicate(&mut self, command: &Command) -> Result<Vec<u8>, DriverError> {
        self.fake.adjudicate(command)
    }
    fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, DriverError> { Ok(vec![]) }
    fn claimed_sims(&self) -> Vec<u64> {
        if self.shared.new_claim.load(Ordering::Acquire) { vec![10, 30] } else { vec![10] }
    }
    fn state_authority_generation(&self) -> u64 { u64::from(self.shared.new_claim.load(Ordering::Acquire)) }
    fn participant_lifecycle_enabled(&self) -> bool { self.shared.fair }
    fn start_stream_request(&mut self, request: &StreamRequest) -> Result<Option<StreamReply>, DriverError> {
        assert!(self.pending.is_none(), "shared native slot cannot be reused before completion");
        let reply = match request {
            StreamRequest::Recorder(args) => {
                let report = match args[0].as_str() {
                    "ring-on" => "recorder=on;ring=on".into(),
                    "snapshot" => format!("sims={};drivers=-", args[1]),
                    "snapshot-driver" => "next=-1".into(),
                    "dump" if args.get(1).is_some_and(|value| value == &simlink_headless_server::presentation::FROM_NOW_CURSOR.to_string())
                        || !self.shared.enabled.load(Ordering::Acquire) => "seq=0;more=0".into(),
                    "dump" => {
                        if self.shared.dumps.load(Ordering::Acquire) == 2 && self.shared.fail_once.swap(false, Ordering::AcqRel) {
                            self.shared.failures.fetch_add(1, Ordering::AcqRel);
                            return Err(DriverError("fixture transient recorder error".into()));
                        }
                        let n = self.shared.dumps.fetch_add(1, Ordering::AcqRel) + 1;
                        if self.shared.fair { format!("seq={n};more=1") }
                        else if n <= 17 {
                            format!("seq={n};more={}\n{n}|Pz|10|{n}|2|3|0|0|1", u8::from(n < 17))
                        } else { "seq=17;more=0".into() }
                    }
                    other => panic!("unexpected recorder action {other}"),
                };
                StreamReply::Recorder(report)
            }
            StreamRequest::DiagState => StreamReply::DiagState(None),
            StreamRequest::Snapshot(_) => {
                self.shared.snapshots.fetch_add(1, Ordering::AcqRel);
                StreamReply::Snapshot(Some(Snapshot {
                tick: 1, world_name: "Fixture".into(), save_name: "Fixture".into(), speed: "Normal".into(),
                sims: if self.shared.fair { [10, 20, 30].into_iter().map(|id| Sim {
                    object_id: id, lot_id: Some(100), name: "Fixture".into(), household_id: 1,
                    position: [0.0; 3], interaction_id: None, owning_player_id: None, interaction_key: None,
                    queue_count: None, posture_container: None, posture_type: None, description_id: Some(id),
                }).collect() } else { vec![] }, objects: vec![],
            })) },
            StreamRequest::ProbeSimState(ids) => {
                assert!(self.shared.fair); assert!(!ids.contains(&10));
                self.shared.probes.fetch_add(1, Ordering::AcqRel);
                StreamReply::SimState(ids.iter().map(|id| format!("{id}:1,2,3,4,5,6:-")).collect::<Vec<_>>().join(";"))
            }
            StreamRequest::ExportSimDescription(desc) => {
                assert!(self.shared.fair);
                self.shared.exports.fetch_add(1, Ordering::AcqRel);
                StreamReply::Description(simlink_bridge::verbs::SimDescriptionPayload {
                    desc: *desc, property_stream: vec![0, 255], res_key_table: vec![], object_id_table: vec![],
                })
            }
        };
        if self.shared.fair && self.shared.enabled.load(Ordering::Acquire)
            && matches!(request, StreamRequest::Recorder(args) if args[0] == "dump") {
            self.pending = Some(reply);
            Ok(None)
        } else { Ok(Some(reply)) }
    }
    fn poll_stream_request(&mut self) -> Result<Option<StreamReply>, DriverError> {
        if self.shared.hold_reply.load(Ordering::Acquire) { Ok(None) } else { Ok(self.pending.take()) }
    }
    fn stream_request_pending(&self) -> bool { self.pending.is_some() }
}

fn report(server: &RunningServer) -> String {
    let stream = TcpStream::connect(server.admin_addr().unwrap()).unwrap();
    stream.set_read_timeout(Some(Duration::from_secs(3))).unwrap();
    let mut reader = BufReader::new(stream);
    writeln!(reader.get_mut(), "FRONT_DOOR_STATE").unwrap();
    let mut line = String::new();
    reader.read_line(&mut line).unwrap();
    serde_json::from_str::<serde_json::Value>(&line).unwrap()["report"].as_str().unwrap().into()
}
fn wait(mut condition: impl FnMut() -> bool) -> bool {
    let deadline = Instant::now() + Duration::from_secs(3);
    while Instant::now() < deadline {
        if condition() { return true; }
        std::thread::sleep(Duration::from_millis(10));
    }
    false
}
fn records(peer: &mut common::Peer) -> Vec<String> {
    let mut result = vec![];
    let mut sequence = 1; // JoinSession reserves reply sequence 1.
    while result.len() < 17 {
        let line = peer.next().unwrap();
        let event = w::decode_event(&w::split(line.as_bytes()).unwrap()).unwrap();
        assert_eq!(event.seq, sequence + 1);
        sequence = event.seq;
        assert_eq!(event.kind, "SimAnimation");
        result.extend(m::dec_list(&event.fields[0]).unwrap().iter().map(|line| m::hex_decode(line).unwrap()));
    }
    result
}

#[test]
fn known_more_after_sixteen_pages_runs_next_pump_before_the_poll_deadline() {
    let shared = Arc::new(Shared::default());
    let driver_shared = Arc::clone(&shared);
    let clock = Arc::clone(&shared);
    let server = RunningServer::start_configured_with_clock(
        TcpListener::bind("127.0.0.1:0").unwrap(), Some(TcpListener::bind("127.0.0.1:0").unwrap()),
        move || Ok(Driver { fake: FakeDriver::new(), shared: driver_shared, pending: None }), 32,
        StreamOptions { base_interval_ms: 0.0, presentation_poll_ms: 1000, ..StreamOptions::default() },
        move || clock.clock.load(Ordering::Acquire),
    ).unwrap();
    let mut peers: Vec<_> = (1..=2).map(|player| {
        let mut peer = common::Peer::connect(&server);
        assert!(peer.exchange(&format!("CMD|1|JoinSession|1|{player}|61|~")).starts_with("ACK|"));
        peer
    }).collect();
    // Both joins may arrive after the first pump planned its audience. Give the
    // second join its normal presentation turn before starting the backlog.
    shared.clock.store(1000, Ordering::Release);
    let ready = wait(|| {
        let state = report(&server);
        state.contains("client|1|") && state.contains("client|2|")
            && state.contains("presentation_join_history_pending=0") && state.contains("presentation_fresh_history_pending=0")
    });
    assert!(ready, "startup histories did not complete: {}", report(&server));
    shared.enabled.store(true, Ordering::Release);
    shared.clock.store(2000, Ordering::Release);
    let progressed = wait(|| shared.dumps.load(Ordering::Acquire) >= 17);
    let before_unwind = report(&server);
    if !progressed {
        server.shutdown().unwrap();
        panic!("known more debt stalled at {} pages with clock fixed before next poll: {before_unwind}", shared.dumps.load(Ordering::Acquire));
    }
    let expected: Vec<_> = (1..=17).map(|n| format!("{n}|Pz|10|{n}|2|3|0|0|1")).collect();
    for peer in &mut peers { assert_eq!(records(peer), expected); }
    assert_eq!(shared.clock.load(Ordering::Acquire), 2000);
    assert!(report(&server).contains("presentation_dump_debt=0"));
    server.shutdown().unwrap();
}

#[test]
fn sustained_deferred_more_keeps_cadence_state_and_new_participants_progressing() {
    let shared = Arc::new(Shared { fair: true, ..Shared::default() });
    let driver_shared = Arc::clone(&shared);
    let clock = Arc::clone(&shared);
    let server = RunningServer::start_configured_with_clock(
        TcpListener::bind("127.0.0.1:0").unwrap(), Some(TcpListener::bind("127.0.0.1:0").unwrap()),
        move || Ok(Driver { fake: FakeDriver::new(), shared: driver_shared, pending: None }), 32,
        StreamOptions { base_interval_ms: 100.0, presentation_poll_ms: 1000, ..StreamOptions::default() },
        move || clock.clock.load(Ordering::Acquire),
    ).unwrap();
    let _peers: Vec<_> = (1..=2).map(|player| {
        let mut peer = common::Peer::connect(&server);
        assert!(peer.exchange(&format!("CMD|1|JoinSession|1|{player}|61|~")).starts_with("ACK|"));
        peer
    }).collect();
    shared.clock.store(1000, Ordering::Release);
    assert!(wait(|| {
        let state = report(&server);
        state.contains("presentation_join_history_pending=0") && state.contains("presentation_fresh_history_pending=0")
            && state.contains("participant_pending=0") && shared.exports.load(Ordering::Acquire) == 2
    }), "startup: {}", report(&server));
    shared.enabled.store(true, Ordering::Release);
    shared.clock.store(2000, Ordering::Release);
    assert!(wait(|| shared.dumps.load(Ordering::Acquire) >= 17), "no next-pump continuation: {}", report(&server));
    let before_exports = shared.exports.load(Ordering::Acquire);
    let before_snapshots = shared.snapshots.load(Ordering::Acquire);
    let before_probes = shared.probes.load(Ordering::Acquire);
    // Keep actual native ownership across authority replacement, rather than
    // merely changing scope between two unrelated requests.
    shared.hold_reply.store(true, Ordering::Release);
    assert!(wait(|| report(&server).contains("stream_io_pending=dump|")));
    let held_dumps = shared.dumps.load(Ordering::Acquire);
    shared.new_claim.store(true, Ordering::Release);
    shared.clock.store(3000, Ordering::Release);
    std::thread::sleep(Duration::from_millis(50));
    assert_eq!(shared.dumps.load(Ordering::Acquire), held_dumps, "pending native slot must remain owned");
    assert_eq!(shared.exports.load(Ordering::Acquire), before_exports);
    shared.hold_reply.store(false, Ordering::Release);
    for now in (3000..=10_000).step_by(1000) {
        let dumps = shared.dumps.load(Ordering::Acquire);
        shared.clock.store(now, Ordering::Release);
        assert!(wait(|| shared.dumps.load(Ordering::Acquire) > dumps + 1), "drain fairness: {}", report(&server));
    }
    assert!(wait(|| shared.exports.load(Ordering::Acquire) == before_exports + 2));
    assert!(shared.snapshots.load(Ordering::Acquire) > before_snapshots + 4);
    assert!(shared.probes.load(Ordering::Acquire) > before_probes);
    let state = report(&server);
    assert!(state.contains("participant_pending=0"), "{state}");
    assert!(state.contains("participant_errors=0"), "{state}");
    assert!(state.split("visible_state|").nth(1).unwrap().split("|world_ready").next().unwrap().contains("|errors=0"), "{state}");
    assert!(!state.contains("presentation_dump_scope_invalidations=0"), "{state}");
    server.shutdown().unwrap();
}

#[test]
fn transient_dump_error_retries_after_backoff_without_losing_or_duplicating_records() {
    let shared = Arc::new(Shared { fail_once: AtomicBool::new(true), ..Shared::default() });
    let driver_shared = Arc::clone(&shared);
    let clock = Arc::clone(&shared);
    let server = RunningServer::start_configured_with_clock(
        TcpListener::bind("127.0.0.1:0").unwrap(), Some(TcpListener::bind("127.0.0.1:0").unwrap()),
        move || Ok(Driver { fake: FakeDriver::new(), shared: driver_shared, pending: None }), 32,
        StreamOptions { base_interval_ms: 0.0, presentation_poll_ms: 1000, ..StreamOptions::default() },
        move || clock.clock.load(Ordering::Acquire),
    ).unwrap();
    let mut peers: Vec<_> = (1..=2).map(|player| {
        let mut peer = common::Peer::connect(&server);
        assert!(peer.exchange(&format!("CMD|1|JoinSession|1|{player}|61|~")).starts_with("ACK|")); peer
    }).collect();
    shared.clock.store(1000, Ordering::Release);
    assert!(wait(|| {
        let state = report(&server);
        state.contains("presentation_join_history_pending=0") && state.contains("presentation_fresh_history_pending=0")
    }));
    shared.enabled.store(true, Ordering::Release);
    shared.clock.store(2000, Ordering::Release);
    assert!(wait(|| shared.failures.load(Ordering::Acquire) == 1));
    shared.clock.store(2999, Ordering::Release);
    std::thread::sleep(Duration::from_millis(50));
    assert_eq!(shared.dumps.load(Ordering::Acquire), 2, "must not spin during the retry delay");
    shared.clock.store(3000, Ordering::Release);
    assert!(wait(|| shared.dumps.load(Ordering::Acquire) >= 17), "{}", report(&server));
    let expected: Vec<_> = (1..=17).map(|n| format!("{n}|Pz|10|{n}|2|3|0|0|1")).collect();
    for peer in &mut peers { assert_eq!(records(peer), expected); }
    assert!(report(&server).contains("presentation_dump_errors=1"));
    server.shutdown().unwrap();
}
