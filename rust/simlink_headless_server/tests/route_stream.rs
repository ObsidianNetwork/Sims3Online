#![forbid(unsafe_code)]
//! Startup ownership of the existing opt-in native route presentation switch.
pub mod common;

use simlink_headless_server::{
    messages::Command, serve::RunningServer, snapshot::Snapshot,
    stream::StreamOptions, transport::{StreamReply, StreamRequest},
    DriverError, FakeDriver, GameDriver,
};
use std::{
    collections::VecDeque,
    io::{BufRead, BufReader, Write},
    net::{TcpListener, TcpStream},
    sync::{atomic::{AtomicU64, AtomicUsize, Ordering}, Arc, Mutex},
    time::{Duration, Instant},
};

enum Answer { Error, Report(&'static str), Hold }
#[derive(Default)]
struct Shared {
    world: AtomicU64,
    clock: AtomicU64,
    releases: AtomicUsize,
    calls: Mutex<Vec<(u64, String)>>,
    answers: Mutex<VecDeque<Answer>>,
}
struct Driver { fake: FakeDriver, shared: Arc<Shared>, pending: bool }
impl GameDriver for Driver {
    fn adjudicate(&mut self, command: &Command) -> Result<Vec<u8>, DriverError> {
        self.fake.adjudicate(command)
    }
    fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, DriverError> { Ok(vec![]) }
    fn claimed_sims(&self) -> Vec<u64> { vec![10] }
    fn presentation_world_generation(&self) -> u64 { self.shared.world.load(Ordering::Acquire) }
    fn snapshot(&mut self, _: i64) -> Result<Option<Snapshot>, DriverError> {
        Ok(Some(Snapshot { tick: 1, world_name: "Fixture".into(), save_name: "Fixture".into(),
            speed: "Normal".into(), sims: vec![], objects: vec![] }))
    }
    fn recorder(&mut self, args: &[String]) -> Result<String, DriverError> {
        let action = args[0].as_str();
        self.shared.calls.lock().unwrap().push((self.presentation_world_generation(), action.into()));
        Ok(match action {
            "ring-on" => "recorder=on;ring=on",
            "snapshot" => "sims=10;drivers=-",
            "snapshot-driver" => "next=-1",
            _ => "seq=0;more=0",
        }.into())
    }
    fn start_stream_request(&mut self, request: &StreamRequest) -> Result<Option<StreamReply>, DriverError> {
        if let StreamRequest::Recorder(args) = request {
            if args[0] == "routes-on" {
                self.shared.calls.lock().unwrap().push((self.presentation_world_generation(), "routes-on".into()));
                let answer = self.shared.answers.lock().unwrap().pop_front().unwrap_or(Answer::Report("routes=on;recorder=on;ring=on"));
                return match answer {
                    Answer::Error => Err(DriverError("fixture transient route setup failure".into())),
                    Answer::Report(value) => Ok(Some(StreamReply::Recorder(value.into()))),
                    Answer::Hold => { self.pending = true; Ok(None) }
                };
            }
        }
        Ok(Some(match request {
            StreamRequest::ExportSimDescription(_) => panic!("participant capability is disabled in this fixture"),
            StreamRequest::Recorder(args) => StreamReply::Recorder(self.recorder(args)?),
            StreamRequest::Snapshot(since) => StreamReply::Snapshot(self.snapshot(*since)?),
            StreamRequest::DiagState => StreamReply::DiagState(None),
            StreamRequest::ProbeSimState(_) => StreamReply::SimState("sims=0".into()),
        }))
    }
    fn poll_stream_request(&mut self) -> Result<Option<StreamReply>, DriverError> {
        if self.pending {
            let mut count = self.shared.releases.load(Ordering::Acquire);
            while let Some(next) = count.checked_sub(1) {
                match self.shared.releases.compare_exchange_weak(
                    count, next, Ordering::AcqRel, Ordering::Acquire,
                ) {
                    Ok(_) => {
                        self.pending = false;
                        return Ok(Some(StreamReply::Recorder("routes=on;recorder=on;ring=on".into())));
                    }
                    Err(observed) => count = observed,
                }
            }
        }
        Ok(None)
    }
    fn stream_request_pending(&self) -> bool { self.pending }
}
fn start(shared: &Arc<Shared>, enabled: bool) -> (RunningServer, common::Peer) {
    let driver = Arc::clone(shared);
    let clock = Arc::clone(shared);
    let server = RunningServer::start_configured_with_clock(
        TcpListener::bind("127.0.0.1:0").unwrap(), Some(TcpListener::bind("127.0.0.1:0").unwrap()),
        move || Ok(Driver { fake: FakeDriver::default(), shared: driver, pending: false }), 8,
        StreamOptions { route_stream_requested: enabled, base_interval_ms: 10_000.0, ..StreamOptions::default() },
        move || clock.clock.load(Ordering::Acquire),
    ).unwrap();
    let mut peer = common::Peer::connect(&server);
    assert!(peer.exchange("CMD|1|JoinSession|1|1|6a|~").starts_with("ACK|1|JoinSession"));
    (server, peer)
}
fn wait(mut condition: impl FnMut() -> bool) {
    let deadline = Instant::now() + Duration::from_secs(5);
    while !condition() {
        assert!(Instant::now() < deadline, "route startup deadline");
        std::thread::sleep(Duration::from_millis(5));
    }
}
fn report(server: &RunningServer) -> String {
    let mut socket = TcpStream::connect(server.admin_addr().unwrap()).unwrap();
    socket.set_read_timeout(Some(Duration::from_secs(2))).unwrap();
    writeln!(socket, "FRONT_DOOR_STATE").unwrap();
    let mut text = String::new();
    BufReader::new(socket).read_line(&mut text).unwrap();
    let value: serde_json::Value = serde_json::from_str(&text).unwrap();
    assert_eq!(value["accepted"], true);
    value["report"].as_str().unwrap().into()
}
fn calls(shared: &Shared) -> Vec<(u64, String)> { shared.calls.lock().unwrap().clone() }

#[test]
fn default_off_keeps_the_existing_recorder_order_and_does_not_send_routes_off() {
    let shared = Arc::new(Shared::default());
    let (server, _peer) = start(&shared, false);
    wait(|| calls(&shared).iter().any(|(_, action)| action == "dump"));
    let calls = calls(&shared);
    assert_eq!(calls[0].1, "ring-on");
    assert_eq!(calls[1].1, "dump");
    assert!(!calls.iter().any(|(_, action)| action.starts_with("routes-")));
    assert!(report(&server).contains("presentation_routes_requested=0|presentation_routes_ready=0"));
    server.shutdown().unwrap();
}

#[test]
fn opted_in_warm_start_requires_ring_then_exact_routes_ack_and_retries_without_advancing_cursor() {
    let shared = Arc::new(Shared::default());
    shared.answers.lock().unwrap().extend([
        Answer::Error, Answer::Report("routes=off"),
        Answer::Report("routes=on;routes=off"), Answer::Report("routes=on-not"),
        Answer::Report("routes=on;recorder=on;ring=on"),
    ]);
    let (server, _peer) = start(&shared, true);
    for failed in 1..=4 {
        wait(|| calls(&shared).len() == failed + 1);
        let state = report(&server);
        assert!(state.contains("presentation_routes_requested=1|presentation_routes_ready=0"));
        assert!(state.contains(&format!("presentation_route_config_failures={failed}")));
        assert!(calls(&shared).iter().all(|(_, action)| action == "ring-on" || action == "routes-on"));
        shared.clock.store(failed as u64 * 250 - 1, Ordering::Release);
        std::thread::sleep(Duration::from_millis(20));
        assert_eq!(calls(&shared).len(), failed + 1, "retry before the bounded poll interval");
        shared.clock.store(failed as u64 * 250, Ordering::Release);
    }
    wait(|| calls(&shared).iter().any(|(_, action)| action == "dump"));
    assert_eq!(&calls(&shared)[..6], &[(0, "ring-on".into()), (0, "routes-on".into()),
        (0, "routes-on".into()), (0, "routes-on".into()), (0, "routes-on".into()), (0, "routes-on".into())]);
    assert!(report(&server).contains("presentation_routes_requested=1|presentation_routes_ready=1"));
    server.shutdown().unwrap();
}

#[test]
fn old_world_ack_cannot_release_new_world_setup_and_reacquire_reasserts_the_opt_in() {
    let shared = Arc::new(Shared::default());
    shared.answers.lock().unwrap().extend([Answer::Hold, Answer::Hold]);
    let (server, _peer) = start(&shared, true);
    wait(|| calls(&shared).len() == 2);
    shared.world.store(1, Ordering::Release);
    shared.releases.store(1, Ordering::Release);
    wait(|| calls(&shared).iter().any(|(world, action)| *world == 1 && action == "routes-on"));
    assert!(report(&server).contains("presentation_routes_requested=1|presentation_routes_ready=0"));
    assert!(!calls(&shared).iter().any(|(_, action)| action == "dump"));
    shared.releases.store(1, Ordering::Release);
    wait(|| calls(&shared).iter().any(|(_, action)| action == "dump"));
    assert!(report(&server).contains("presentation_routes_requested=1|presentation_routes_ready=1"));
    shared.world.store(2, Ordering::Release);
    wait(|| calls(&shared).iter().any(|(world, action)| *world == 2 && action == "routes-on"));
    assert!(report(&server).contains("presentation_routes_requested=1|presentation_routes_ready=1"));
    server.shutdown().unwrap();
}
