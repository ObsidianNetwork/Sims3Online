#![forbid(unsafe_code)]
//! Actual resident sockets, including bulk assembly and recipient/session cuts.
pub mod common;
use simlink_headless_server::{
    cadence::Thresholds,
    messages as m,
    serve::{RunningServer, StreamOptions},
    snapshot::{Sim, Snapshot},
    transport::{StreamReply, StreamRequest},
    DriverError, FakeDriver, GameDriver,
};
use std::{
    net::TcpListener,
    sync::{
        atomic::{AtomicBool, AtomicU64, Ordering},
        Arc,
    },
    time::{Duration, Instant},
};

#[derive(Default)]
struct Shared {
    phase: AtomicU64,
    hold: AtomicBool,
    started: AtomicBool,
    clock: AtomicU64,
    running_clock: AtomicBool,
}
struct Driver {
    fake: FakeDriver,
    shared: Arc<Shared>,
    pending: Option<Snapshot>,
}
impl Driver {
    fn sample_model(&self, since: i64) -> Snapshot {
        let phase = self.shared.phase.load(Ordering::Acquire);
        let (tick, ids) = match phase {
            0 => (1, vec![10, 20]),
            1 => (10, vec![10, 20]),
            2 if since < 0 => (10, vec![10, 20]),
            2 => (11, vec![40]),
            3 => (50, vec![10, 20, 900]),
            _ => (51, vec![10, 20, 40]),
        };
        Snapshot {
            tick,
            world_name: "Fixture".into(),
            save_name: "Fixture".into(),
            speed: "Normal".into(),
            sims: ids
                .into_iter()
                .map(|object_id| Sim {
                    object_id,
                    name: "Fixture".into(),
                    household_id: 1,
                    position: [0.0; 3],
                    interaction_id: None,
                    owning_player_id: None,
                    interaction_key: None,
                    lot_id: Some(100),
                    queue_count: None,
                    posture_container: None,
                    posture_type: None,
                    description_id: Some(object_id),
                })
                .collect(),
            objects: vec![],
        }
    }
}
impl GameDriver for Driver {
    fn adjudicate(&mut self, command: &m::Command) -> Result<Vec<u8>, DriverError> {
        self.fake.adjudicate(command)
    }
    fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, DriverError> {
        Ok(vec![])
    }
    fn claimed_sims(&self) -> Vec<u64> {
        vec![10]
    }
    fn start_stream_request(
        &mut self,
        request: &StreamRequest,
    ) -> Result<Option<StreamReply>, DriverError> {
        Ok(Some(match request {
            StreamRequest::ExportSimDescription(_) => panic!("participant capability is disabled in this fixture"),
            StreamRequest::DiagState => StreamReply::DiagState(None),
            StreamRequest::Snapshot(since) => {
                let snapshot = self.sample_model(*since);
                let phase = self.shared.phase.load(Ordering::Acquire);
                if self.shared.hold.load(Ordering::Acquire)
                    && (phase == 3 || phase == 1 && *since < 0)
                {
                    assert!(self.pending.replace(snapshot).is_none());
                    self.shared.started.store(true, Ordering::Release);
                    return Ok(None);
                }
                StreamReply::Snapshot(Some(snapshot))
            }
            StreamRequest::Recorder(args) => StreamReply::Recorder(match args[0].as_str() {
                "snapshot" => "drivers=-".into(),
                "snapshot-driver" => "next=-1".into(),
                "dump" => "seq=0;more=0".into(),
                _ => "ok".into(),
            }),
            StreamRequest::ProbeSimState(ids) => StreamReply::SimState(
                ids.iter()
                    .map(|id| format!("{id}:1,2,3,4,5,6:-"))
                    .collect::<Vec<_>>()
                    .join(";"),
            ),
        }))
    }
    fn poll_stream_request(&mut self) -> Result<Option<StreamReply>, DriverError> {
        if self.shared.hold.load(Ordering::Acquire) {
            return Ok(None);
        }
        Ok(self
            .pending
            .take()
            .map(|snapshot| StreamReply::Snapshot(Some(snapshot))))
    }
}
fn wait(mut condition: impl FnMut() -> bool) {
    let deadline = Instant::now() + Duration::from_secs(5);
    while !condition() {
        assert!(Instant::now() < deadline, "fixture deadline");
        std::thread::sleep(Duration::from_millis(5));
    }
}
struct Reader {
    peer: common::Peer,
    bulk: simlink_headless_server::BulkReceiver,
    pending: std::collections::HashMap<u64, m::Event>,
}
impl Reader {
    fn new(server: &RunningServer, player: i32) -> Self {
        let mut peer = common::Peer::connect(server);
        assert!(peer
            .exchange(&format!("CMD|1|JoinSession|1|{player}|61|~"))
            .starts_with("ACK"));
        Self {
            peer,
            bulk: simlink_headless_server::BulkReceiver::new(),
            pending: Default::default(),
        }
    }
    fn event(&mut self) -> m::Event {
        loop {
            let line = self.peer.next().expect("live actor");
            let fields = simlink_headless_server::split(line.as_bytes()).unwrap();
            if line.starts_with("EV|") {
                let event = simlink_headless_server::decode_event(&fields).unwrap();
                assert!(
                    !event.kind.starts_with("__"),
                    "private model/state receipt leaked"
                );
                if event.kind != "SnapshotDelta" {
                    return event;
                }
                assert_eq!(event.fields.len(), 2);
                let id = m::dec_bulk_ref(&event.fields[0]).unwrap();
                assert!(self.pending.insert(id, event).is_none());
            } else if line.starts_with("BULK|") {
                let chunk = simlink_headless_server::decode_bulk(&fields).unwrap();
                let bytes = self.bulk.submit(&chunk).unwrap();
                if !bytes.is_empty() {
                    let mut event = self
                        .pending
                        .remove(&chunk.bulk_id)
                        .expect("snapshot introducer");
                    event.fields[0] = String::from_utf8(bytes).unwrap();
                    return event;
                }
            }
        }
    }
    fn model(&mut self, tick: u64) -> m::Event {
        let deadline = Instant::now() + Duration::from_secs(5);
        loop {
            let event = self.event();
            assert!(event.kind != "SimStateChanged" || event.fields[0] != "40",
                "new state cannot precede its complete matching model on either socket");
            if event.kind == "SnapshotDelta" && event.tick >= tick {
                return event;
            }
            assert!(Instant::now() < deadline, "model deadline");
        }
    }
    fn ack(&mut self, player: i32, id: u64, tick: u64) {
        assert!(self
            .peer
            .exchange(&format!("CMD|{id}|AckSnapshot|{id}|{player}|{tick}"))
            .starts_with("ACK"));
    }
}
fn ids(event: &m::Event) -> Vec<u64> {
    m::dec_list(&m::dec_record(&event.fields[0]).unwrap()[4])
        .unwrap()
        .iter()
        .map(|sim| m::dec_record(sim).unwrap()[0].parse().unwrap())
        .collect()
}
fn fixture() -> (Arc<Shared>, RunningServer, Reader, Reader) {
    let shared = Arc::new(Shared::default());
    let state = shared.clone();
    let clock = shared.clone();
    let origin = Instant::now();
    let server = RunningServer::start_configured_with_clock(
        TcpListener::bind("127.0.0.1:0").unwrap(),
        None,
        move || {
            Ok(Driver {
                fake: FakeDriver::new(),
                shared: state,
                pending: None,
            })
        },
        8,
        StreamOptions {
            base_interval_ms: 10.0,
            thresholds: Thresholds {
                warn_ticks: 2,
                hard_ticks: 4,
                max_interval_ms: 1000.0,
            },
            ..StreamOptions::default()
        },
        move || {
            if clock.running_clock.load(Ordering::Acquire) {
                1000 + origin.elapsed().as_millis() as u64
            } else {
                clock.clock.load(Ordering::Acquire)
            }
        },
    )
    .unwrap();
    let mut one = Reader::new(&server, 1);
    let mut two = Reader::new(&server, 2);
    shared.clock.store(100, Ordering::Release);
    assert_eq!(one.model(1).fields[1], "0");
    assert_eq!(two.model(1).fields[1], "0");
    one.ack(1, 2, 1);
    two.ack(2, 2, 1);
    (shared, server, one, two)
}
#[test]
fn ordinary_force_full_merges_later_delta_while_other_peer_keeps_exact_delta_before_state() {
    let (shared, server, mut one, mut two) = fixture();
    shared.phase.store(1, Ordering::Release);
    shared.hold.store(true, Ordering::Release);
    shared.clock.store(200, Ordering::Release);
    assert_eq!(one.model(10).fields[1], "0");
    assert_eq!(two.model(10).fields[1], "0");
    two.ack(2, 3, 10);
    shared.clock.store(300, Ordering::Release);
    wait(|| shared.started.load(Ordering::Acquire));
    shared.phase.store(2, Ordering::Release);
    shared.hold.store(false, Ordering::Release);
    let full = one.model(11);
    let delta = two.model(11);
    assert_eq!(full.fields[1], "1");
    assert_eq!(ids(&full), vec![10, 20, 40]);
    assert_eq!(delta.fields[1], "0");
    assert_eq!(ids(&delta), vec![40]);
    one.ack(1, 3, 11);
    two.ack(2, 4, 11);
    shared.running_clock.store(true, Ordering::Release);
    for peer in [&mut one, &mut two] {
        let deadline = Instant::now() + Duration::from_secs(5);
        loop {
            let event = peer.event();
            if event.kind == "SimStateChanged" && event.fields[0] == "40" {
                break;
            }
            assert!(Instant::now() < deadline, "state after model deadline");
        }
    }
    drop(one);
    drop(two);
    server.shutdown().unwrap();
}
#[test]
fn old_native_snapshot_cannot_cross_equal_id_same_socket_leave_join() {
    let (shared, server, mut one, mut two) = fixture();
    shared.hold.store(true, Ordering::Release);
    shared.phase.store(3, Ordering::Release);
    shared.clock.store(200, Ordering::Release);
    wait(|| shared.started.load(Ordering::Acquire));
    assert!(two
        .peer
        .exchange("CMD|3|LeaveSession|3|2")
        .starts_with("ACK"));
    assert!(two
        .peer
        .exchange("CMD|4|JoinSession|4|2|61|~")
        .starts_with("ACK"));
    shared.phase.store(4, Ordering::Release);
    shared.running_clock.store(true, Ordering::Release);
    shared.hold.store(false, Ordering::Release);
    let unaffected = one.model(50);
    assert!(ids(&unaffected).contains(&900));
    let rejoined = two.model(50);
    assert_eq!(
        rejoined.tick, 51,
        "old request session must not bind the new identical recipient"
    );
    assert_eq!(rejoined.fields[1], "0");
    assert!(!ids(&rejoined).contains(&900));
    drop(one);
    drop(two);
    server.shutdown().unwrap();
}
