#![forbid(unsafe_code)]
//! Visible-state publication through the normal resident TCP entry.
use simlink_headless_server::{
    messages::{self as m, Command},
    serve::{RunningServer, StreamOptions},
    snapshot::{Sim, Snapshot},
    transport::{StreamReply, StreamRequest},
    ConnId, DriverError, FakeDriver, GameDriver,
};
use std::{
    io::{BufRead, BufReader, Write},
    net::{SocketAddr, TcpListener, TcpStream},
    sync::{
        atomic::{AtomicBool, AtomicU32, AtomicU64, Ordering},
        Arc,
    },
    time::{Duration, Instant},
};

#[derive(Default)]
struct Shared {
    release: AtomicBool,
    hold_motive: AtomicU32,
    held_started: AtomicU64,
    started: AtomicU64,
    generation: AtomicU64,
    claimed: AtomicBool,
    claimed_seen: AtomicU64,
    motive: AtomicU32,
    world: AtomicU64,
    baseline: AtomicBool,
    malformed: AtomicBool,
    dumps: AtomicU64,
    client2_connection: AtomicU64,
    hold_snapshot: AtomicBool,
    snapshot_started: AtomicU64,
    full_snapshots: AtomicU64,
    history_hold: AtomicBool,
    history_started: AtomicU64,
    fresh: AtomicBool,
    fresh_pages: AtomicU64,
    fresh_complete: AtomicBool,
}
enum Pending {
    State(Vec<u64>, u32, bool),
    Snapshot(Option<Snapshot>),
    History(String, Instant),
}
struct Driver {
    fake: FakeDriver,
    shared: Arc<Shared>,
    pending: Option<Pending>,
    tick: i64,
    world_seen: u64,
}
impl GameDriver for Driver {
    fn adjudicate(&mut self, cmd: &Command) -> Result<Vec<u8>, DriverError> {
        self.fake.adjudicate(cmd)
    }
    fn adjudicate_for(&mut self, conn: ConnId, cmd: &Command) -> Result<Vec<u8>, DriverError> {
        if cmd.verb == "JoinSession" && cmd.player_id == 2 {
            self.shared
                .client2_connection
                .store(conn.0, Ordering::Release);
        }
        self.adjudicate(cmd)
    }
    fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, DriverError> {
        Ok(vec![])
    }
    fn claimed_sims(&self) -> Vec<u64> {
        if self.shared.claimed.load(Ordering::Acquire) {
            self.shared.claimed_seen.fetch_add(1, Ordering::AcqRel);
            vec![10, 20]
        } else {
            vec![10]
        }
    }
    fn state_authority_generation(&self) -> u64 {
        self.shared.generation.load(Ordering::Acquire)
    }
    fn presentation_world_generation(&self) -> u64 {
        self.shared.world.load(Ordering::Acquire)
    }
    fn baseline_pending(&self, id: ConnId) -> bool {
        id.0 == self.shared.client2_connection.load(Ordering::Acquire)
            && self.shared.baseline.load(Ordering::Acquire)
    }
    fn admin_request(
        &mut self,
        request: &simlink_headless_server::admin::Request,
    ) -> Result<String, DriverError> {
        assert_eq!(request.name, "LOAD_SAVE");
        self.shared.world.fetch_add(1, Ordering::AcqRel);
        Ok("{\"ok\":true,\"accepted\":true}".into())
    }
    fn poll_routed_events(
        &mut self,
    ) -> Result<Vec<simlink_headless_server::routing::RoutedEvent>, DriverError> {
        let world = self.presentation_world_generation();
        if world == self.world_seen {
            return Ok(vec![]);
        }
        self.world_seen = world;
        self.tick = 0;
        Ok(vec![simlink_headless_server::routing::RoutedEvent {
            event: m::Event {
                seq: 0,
                tick: self.tick as u64,
                corr: 0,
                kind: "__presentation_world_reset".into(),
                fields: vec![world.to_string()],
            },
            owner: None,
            requester: None,
        }])
    }
    fn recorder(&mut self, args: &[String]) -> Result<String, DriverError> {
        if args[0] == "dump" {
            self.shared.dumps.fetch_add(1, Ordering::AcqRel);
        }
        Ok(if args[0] == "snapshot" {
            if self.shared.history_hold.load(Ordering::Acquire) || self.shared.fresh.load(Ordering::Acquire) { "drivers=7" } else { "drivers=" }
        } else if args[0] == "snapshot-driver" {
            if self.shared.fresh.load(Ordering::Acquire) { "next=1\n0|A|7|40|2|3|4|-2" }
            else { "next=1\n0|A|7|20|2|3|4|-2" }
        } else if self.shared.history_hold.load(Ordering::Acquire) && args[0] == "dump" {
            "seq=1;more=0\n1|Bf|20|1:2:10:10"
        } else {
            "seq=0;more=0"
        }
        .into())
    }
    fn snapshot(&mut self, since: i64) -> Result<Option<Snapshot>, DriverError> {
        self.tick += 1;
        if since < 0 {
            self.shared.full_snapshots.fetch_add(1, Ordering::AcqRel);
        }
        let new_world = self.presentation_world_generation() > 0;
        let mut ids = if new_world && since < 0 {
            // NPC40 appears only AFTER the bootstrap full read, so a later
            // delta must reach the public model before state40 can publish.
            vec![(10, 100), (20, 200), (30, 200)]
        } else if new_world {
            if self.tick == 2 { assert_eq!(since, 1, "first new-world delta follows the published full watermark"); }
            vec![(10, 100), (20, 200), (30, 200), (40, 100)]
        } else {
            vec![(10, 100), (20, 100), (30, 200)]
        };
        if !new_world && self.shared.fresh.load(Ordering::Acquire) { ids.push((40, 100)); }
        // Model the native changed-tick filter: an old-world since tick does NOT
        // turn into a full snapshot, and stationary old IDs are never removed by deltas.
        let sims = if new_world && since >= self.tick {
            vec![]
        } else {
            ids.into_iter()
                .map(|(object_id, lot)| Sim {
                    object_id,
                    lot_id: Some(lot),
                    name: "Fixture".into(),
                    household_id: 1,
                    position: [0.0; 3],
                    interaction_id: None,
                    owning_player_id: None,
                    interaction_key: None,
                    queue_count: None,
                    posture_container: None,
                    posture_type: None,
                    description_id: Some(object_id),
                })
                .collect()
        };
        Ok(Some(Snapshot {
            tick: self.tick,
            world_name: "Fixture".into(),
            save_name: "Fixture".into(),
            speed: "Normal".into(),
            sims,
            objects: vec![],
        }))
    }
    fn start_stream_request(
        &mut self,
        request: &StreamRequest,
    ) -> Result<Option<StreamReply>, DriverError> {
        Ok(match request {
            StreamRequest::ExportSimDescription(_) => panic!("participant capability is disabled in this fixture"),
            StreamRequest::ProbeSimState(ids) => {
                let expected = if self.presentation_world_generation() > 0 {
                    40
                } else {
                    20
                };
                assert!(ids.iter().all(|id| *id == expected || (self.shared.fresh.load(Ordering::Acquire) && *id == 40)),
                    "claimed, off-lot and stale-world Sims must never be probed");
                if self.shared.fresh.load(Ordering::Acquire) && ids.contains(&40) {
                    assert!(self.shared.fresh_complete.load(Ordering::Acquire), "Fresh40 may not be probed before its history completes");
                }
                let captured = self.shared.motive.load(Ordering::Acquire);
                assert!(self
                    .pending
                    .replace(Pending::State(
                        ids.clone(),
                        captured,
                        self.shared.malformed.load(Ordering::Acquire)
                    ))
                    .is_none());
                if captured == self.shared.hold_motive.load(Ordering::Acquire) {
                    self.shared.held_started.fetch_add(1, Ordering::AcqRel);
                }
                self.shared.started.fetch_add(1, Ordering::AcqRel);
                None
            }
            StreamRequest::Snapshot(since) => {
                let sample = self.snapshot(*since)?;
                if self.shared.hold_snapshot.load(Ordering::Acquire) {
                    assert!(self.pending.replace(Pending::Snapshot(sample)).is_none());
                    self.shared.snapshot_started.fetch_add(1, Ordering::AcqRel);
                    None
                } else {
                    Some(StreamReply::Snapshot(sample))
                }
            }
            StreamRequest::Recorder(args) => {
                if args[0] == "snapshot-driver" && args[2] != "0" {
                    let fresh = self.shared.fresh.load(Ordering::Acquire);
                    let offset: i32 = args[2].parse().unwrap();
                    let next = if fresh && offset < 14 { offset + 1 } else { -1 };
                    let state = if fresh && next == -1 { 99 } else { 5 };
                    let report = format!("next={next}\n0|E|7|{state}|6|0|{}", if fresh { 40 } else { 20 });
                    assert!(self.pending.replace(Pending::History(report, Instant::now() + Duration::from_millis(if fresh { 80 } else { 0 }))).is_none());
                    self.shared.history_started.fetch_add(1, Ordering::AcqRel);
                    if fresh { self.shared.fresh_pages.fetch_add(1, Ordering::AcqRel); }
                    None
                } else { Some(StreamReply::Recorder(self.recorder(args)?)) }
            }
            StreamRequest::DiagState => Some(StreamReply::DiagState(None)),
        })
    }
    fn poll_stream_request(&mut self) -> Result<Option<StreamReply>, DriverError> {
        if let Some(Pending::History(_, ready)) = &self.pending {
            if self.shared.history_hold.load(Ordering::Acquire) || Instant::now() < *ready { return Ok(None); }
        }
        if let Some(Pending::State(_, motive, _)) = &self.pending {
            let held = self.shared.hold_motive.load(Ordering::Acquire);
            if held != 0 && *motive == held {
                return Ok(None);
            }
        }
        if !self.shared.release.load(Ordering::Acquire) {
            return Ok(None);
        }
        Ok(self.pending.take().map(|pending| match pending {
            Pending::Snapshot(snapshot) => StreamReply::Snapshot(snapshot),
            Pending::History(report, _) => {
                if self.shared.fresh.load(Ordering::Acquire) && report.starts_with("next=-1") {
                    self.shared.fresh_complete.store(true, Ordering::Release);
                }
                StreamReply::Recorder(report)
            }
            Pending::State(ids, motive, malformed) => {
                if malformed {
                    StreamReply::SimState("20:NaN,2,3,4,5,6:-".into())
                } else {
                    StreamReply::SimState(
                        ids.iter()
                            .map(|id| {
                                format!(
                                    "{id}:{},2,3,4,5,6:18446744073709551615/18446744073709551615",
                                    f32::from_bits(motive)
                                )
                            })
                            .collect::<Vec<_>>()
                            .join(";"),
                    )
                }
            }
        }))
    }
    fn stream_request_pending(&self) -> bool {
        self.pending.is_some()
    }
}
fn connect(addr: SocketAddr) -> BufReader<TcpStream> {
    let peer = TcpStream::connect(addr).unwrap();
    peer.set_read_timeout(Some(Duration::from_secs(4))).unwrap();
    BufReader::new(peer)
}
fn exchange(peer: &mut BufReader<TcpStream>, text: &str) -> String {
    writeln!(peer.get_mut(), "{text}").unwrap();
    let mut line = String::new();
    assert_ne!(peer.read_line(&mut line).unwrap(), 0);
    line
}
fn join(addr: SocketAddr, player: i32) -> BufReader<TcpStream> {
    let mut peer = connect(addr);
    assert!(exchange(&mut peer, "HELLO|1|ring=1,wfs=1").starts_with("WELCOME"));
    assert!(exchange(&mut peer, &format!("CMD|1|JoinSession|1|{player}|61|~")).starts_with("ACK"));
    peer
}
fn state(peer: &mut BufReader<TcpStream>) -> m::Event {
    let end = Instant::now() + Duration::from_secs(5);
    loop {
        let mut line = String::new();
        assert_ne!(peer.read_line(&mut line).unwrap(), 0);
        if line.starts_with("EV|") {
            let fields = simlink_headless_server::split(line.trim_end().as_bytes()).unwrap();
            let event = simlink_headless_server::decode_event(&fields).unwrap();
            assert_ne!(
                event.kind, "__VisibleSimState",
                "internal lease marker leaked onto public socket"
            );
            if event.kind == "SimStateChanged" {
                return event;
            }
        }
        assert!(Instant::now() < end, "state event deadline");
    }
}
fn stat(addr: SocketAddr, key: &str) -> u64 {
    let response = exchange(&mut connect(addr), "FRONT_DOOR_STATE");
    let value: serde_json::Value = serde_json::from_str(&response).unwrap();
    value["report"]
        .as_str()
        .unwrap()
        .lines()
        .flat_map(|line| line.split('|'))
        .find_map(|field| field.strip_prefix(&format!("{key}=")))
        .unwrap()
        .parse()
        .unwrap()
}
fn changed_state(peer: &mut BufReader<TcpStream>) -> m::Event {
    for _ in 0..8 {
        let event = state(peer);
        if event.fields[1] != "[1,2,3,4,5,6]" {
            return event;
        }
    }
    panic!("unexpected repeated initial samples");
}
fn state_after_buff(peer: &mut BufReader<TcpStream>) -> m::Event {
    let end = Instant::now() + Duration::from_secs(5);
    let mut buff_seen = false;
    loop {
        let mut line = String::new();
        assert_ne!(peer.read_line(&mut line).unwrap(), 0);
        if line.starts_with("EV|") {
            let fields = simlink_headless_server::split(line.trim_end().as_bytes()).unwrap();
            let event = simlink_headless_server::decode_event(&fields).unwrap();
            if event.kind == "SimAnimation" {
                buff_seen |= m::dec_list(&event.fields[0]).unwrap().iter()
                    .any(|raw| m::hex_decode(raw).unwrap() == "1|Bf|20|1:2:10:10");
            }
            if event.kind == "SimStateChanged" {
                assert!(buff_seen, "post-history authoritative state must follow held live buffs");
                return event;
            }
        }
        assert!(Instant::now() < end, "history/state ordering deadline");
    }
}
fn state_after_new_world_model(peer: &mut BufReader<TcpStream>) -> m::Event {
    let end = Instant::now() + Duration::from_secs(5);
    let mut full_seen = false;
    let mut delta_with_new_sim = false;
    let mut bulk = simlink_headless_server::BulkReceiver::new();
    let mut pending = std::collections::HashMap::new();
    loop {
        let mut line = String::new();
        assert_ne!(peer.read_line(&mut line).unwrap(), 0);
        let event = if line.starts_with("EV|") {
            let fields = simlink_headless_server::split(line.trim_end().as_bytes()).unwrap();
            let event = simlink_headless_server::decode_event(&fields).unwrap();
            if event.kind == "SnapshotDelta" {
                let id = m::dec_bulk_ref(&event.fields[0]).expect("actual snapshot introducer names its bulk");
                assert!(pending.insert(id, event).is_none());
                None
            } else { Some(event) }
        } else if line.starts_with("BULK|") {
            let fields = simlink_headless_server::split(line.trim_end().as_bytes()).unwrap();
            let chunk = simlink_headless_server::decode_bulk(&fields).unwrap();
            let bytes = bulk.submit(&chunk).unwrap();
            if bytes.is_empty() { None } else {
                let mut event = pending.remove(&chunk.bulk_id).expect("bulk must have an introducing snapshot event");
                event.fields[0] = String::from_utf8(bytes).unwrap();
                Some(event)
            }
        } else { None };
        if let Some(event) = event {
            if event.kind == "SnapshotDelta" {
                let body = m::dec_record(&event.fields[0]).unwrap();
                let has_new_sim = m::dec_list(&body[4]).unwrap().iter()
                    .any(|sim| m::dec_record(sim).unwrap()[0] == "40");
                if event.fields[1] == "1" {
                    assert!(!has_new_sim, "fixture's bootstrap full precedes NPC40");
                    full_seen = true;
                    assert_eq!(body[0], "1");
                } else if has_new_sim {
                    assert!(full_seen, "new-world full model must precede later delta");
                    delta_with_new_sim = true;
                }
            }
            if event.kind == "SimStateChanged" && event.fields[0] == "40" {
                assert!(delta_with_new_sim, "state cannot outrun delivery of its new-world model identity");
                return event;
            }
        }
        assert!(Instant::now() < end, "world model/state ordering deadline");
    }
}
fn state_after_fresh_baseline(peer: &mut BufReader<TcpStream>) -> m::Event {
    let end = Instant::now() + Duration::from_secs(5);
    let mut final_history = false;
    loop {
        let mut line = String::new();
        assert_ne!(peer.read_line(&mut line).unwrap(), 0);
        if line.starts_with("EV|") {
            let fields = simlink_headless_server::split(line.trim_end().as_bytes()).unwrap();
            let event = simlink_headless_server::decode_event(&fields).unwrap();
            if event.kind == "SimAnimation" {
                final_history |= m::dec_list(&event.fields[0]).unwrap().iter()
                    .any(|raw| m::hex_decode(raw).unwrap() == "0|E|7|99|6|0|40");
            }
            if event.kind == "SimStateChanged" && event.fields[0] == "40" {
                assert!(final_history, "Fresh state must follow its final baseline/held queue cut");
                assert_eq!(event.fields.len(), 3);
                return event;
            }
        }
        assert!(Instant::now() < end, "fresh state ordering deadline");
    }
}
fn wait(mut ready: impl FnMut() -> bool) {
    let end = Instant::now() + Duration::from_secs(5);
    while !ready() {
        assert!(Instant::now() < end, "fixture deadline");
        std::thread::sleep(Duration::from_millis(5));
    }
}

fn fixture() -> (
    Arc<Shared>,
    RunningServer,
    BufReader<TcpStream>,
    BufReader<TcpStream>,
) {
    let shared = Arc::new(Shared::default());
    shared.release.store(true, Ordering::Release);
    shared.motive.store(1.0f32.to_bits(), Ordering::Release);
    let driver_shared = Arc::clone(&shared);
    let server = RunningServer::start_configured(
        TcpListener::bind("127.0.0.1:0").unwrap(),
        Some(TcpListener::bind("127.0.0.1:0").unwrap()),
        move || {
            Ok(Driver {
                fake: FakeDriver::new(),
                shared: driver_shared,
                pending: None,
                tick: 1,
                world_seen: 0,
            })
        },
        8,
        StreamOptions {
            base_interval_ms: 10.0,
            ..StreamOptions::default()
        },
    )
    .unwrap();
    let mut one = join(server.local_addr(), 1);
    let mut two = join(server.local_addr(), 2);
    let first = state(&mut one);
    let second = state(&mut two);
    assert_eq!(first.fields, second.fields);
    assert_eq!(
        first.fields,
        [
            "20",
            "[1,2,3,4,5,6]",
            "[(18446744073709551615;18446744073709551615)]"
        ]
    );
    (shared, server, one, two)
}
#[test]
fn actual_tcp_publishes_unclaimed_visible_state_and_fences_claim_aba_during_native_io() {
    let (shared, server, mut one, mut two) = fixture();
    let admin = server.admin_addr().unwrap();
    wait(|| stat(admin, "delivered") >= 1);
    // Retain a real asynchronous read specifically at motive 2.0, then claim-and-release Sim 20.
    shared.hold_motive.store(2.0f32.to_bits(), Ordering::Release);
    shared.motive.store(2.0f32.to_bits(), Ordering::Release);
    wait(|| shared.held_started.load(Ordering::Acquire) >= 1);
    let stale = stat(admin, "stale");
    shared.claimed.store(true, Ordering::Release);
    shared.generation.fetch_add(1, Ordering::AcqRel);
    let claimed_seen = shared.claimed_seen.load(Ordering::Acquire);
    wait(|| shared.claimed_seen.load(Ordering::Acquire) > claimed_seen);
    shared.claimed.store(false, Ordering::Release);
    shared.generation.fetch_add(1, Ordering::AcqRel);
    shared.motive.store(3.0f32.to_bits(), Ordering::Release);
    shared.hold_motive.store(0, Ordering::Release);
    wait(|| stat(admin, "stale") > stale);
    assert_eq!(changed_state(&mut one).fields[1], "[3,2,3,4,5,6]");
    assert_eq!(changed_state(&mut two).fields[1], "[3,2,3,4,5,6]");
    assert_eq!(stat(admin, "errors"), 0);
    drop(one);
    drop(two);
    server.shutdown().unwrap();
}

#[test]
fn recipient_rejoin_during_native_read_requires_a_new_scoped_sample() {
    let (shared, server, mut one, two) = fixture();
    let admin = server.admin_addr().unwrap();
    shared.release.store(false, Ordering::Release);
    shared.motive.store(6.0f32.to_bits(), Ordering::Release);
    let started = shared.started.load(Ordering::Acquire);
    wait(|| shared.started.load(Ordering::Acquire) > started);
    let stale = stat(admin, "stale");
    drop(two);
    let mut replacement = join(server.local_addr(), 2);
    shared.motive.store(7.0f32.to_bits(), Ordering::Release);
    shared.release.store(true, Ordering::Release);
    wait(|| stat(admin, "stale") > stale);
    assert_eq!(changed_state(&mut replacement).fields[1], "[7,2,3,4,5,6]");
    // Only player 2's recipient lease changed. Player 1 must retain its valid
    // captured sample, then receive the later native value in order.
    assert_eq!(changed_state(&mut one).fields[1], "[6,2,3,4,5,6]");
    assert_eq!(changed_state(&mut one).fields[1], "[7,2,3,4,5,6]");
    drop(one);
    drop(replacement);
    server.shutdown().unwrap();
}

#[test]
fn world_replacement_without_navigation_discards_the_old_pending_native_read() {
    let (shared, server, mut one, mut two) = fixture();
    let admin = server.admin_addr().unwrap();
    shared.release.store(false, Ordering::Release);
    shared.motive.store(6.0f32.to_bits(), Ordering::Release);
    let started = shared.started.load(Ordering::Acquire);
    wait(|| shared.started.load(Ordering::Acquire) > started);
    let stale = stat(admin, "stale");
    assert!(exchange(&mut connect(admin), "LOAD_SAVE 46697874757265").contains("true"));
    shared.motive.store(8.0f32.to_bits(), Ordering::Release);
    shared.release.store(true, Ordering::Release);
    wait(|| stat(admin, "stale") > stale);
    let first = state_after_new_world_model(&mut one);
    let second = state_after_new_world_model(&mut two);
    assert_eq!(first.fields[0], "40");
    assert_eq!(second.fields[0], "40");
    assert_eq!(first.fields[1], "[8,2,3,4,5,6]");
    assert_eq!(second.fields[1], "[8,2,3,4,5,6]");
    assert_eq!(stat(admin, "world_ready"), 1);
    assert!(shared.full_snapshots.load(Ordering::Acquire) >= 1);
    drop(one);
    drop(two);
    server.shutdown().unwrap();
}

#[test]
fn pre_reset_snapshot_reply_cannot_restore_old_interest_after_world_generation_changes() {
    let (shared, server, mut one, mut two) = fixture();
    let admin = server.admin_addr().unwrap();
    shared.release.store(false, Ordering::Release);
    shared.hold_snapshot.store(true, Ordering::Release);
    let started = shared.snapshot_started.load(Ordering::Acquire);
    wait(|| shared.snapshot_started.load(Ordering::Acquire) > started);
    assert!(exchange(&mut connect(admin), "LOAD_SAVE 46697874757265").contains("true"));
    wait(|| stat(admin, "world_ready") == 0);
    shared.motive.store(10.0f32.to_bits(), Ordering::Release);
    shared.hold_snapshot.store(false, Ordering::Release);
    shared.release.store(true, Ordering::Release);
    wait(|| stat(admin, "discarded_snapshots") >= 1);
    let first = state_after_new_world_model(&mut one);
    let second = state_after_new_world_model(&mut two);
    assert_eq!(first.fields[0], "40");
    assert_eq!(second.fields[0], "40");
    assert_eq!(first.fields[1], "[10,2,3,4,5,6]");
    assert_eq!(stat(admin, "world_ready"), 1);
    assert!(shared.full_snapshots.load(Ordering::Acquire) >= 1);
    drop(one);
    drop(two);
    server.shutdown().unwrap();
}

#[test]
fn same_recipient_history_aba_fences_pending_probe_and_forces_post_history_state() {
    let (shared, server, mut one, mut two) = fixture();
    let admin = server.admin_addr().unwrap();
    wait(|| stat(admin, "delivered") >= 1);
    shared.release.store(false, Ordering::Release);
    shared.motive.store(2.0f32.to_bits(), Ordering::Release);
    let started = shared.started.load(Ordering::Acquire);
    wait(|| shared.started.load(Ordering::Acquire) > started);
    let stale = stat(admin, "stale");
    shared.history_hold.store(true, Ordering::Release);
    // Pipeline a legitimate leave/rejoin on the SAME socket. The next pump sees
    // the same candidate/world/authority/recipient identities, but a new history
    // lease. A duplicate JoinSession without LeaveSession is correctly refused.
    write!(two.get_mut(), "CMD|2|LeaveSession|2|2\nCMD|3|JoinSession|3|2|61|~\n").unwrap();
    loop {
        let mut line = String::new();
        assert_ne!(two.read_line(&mut line).unwrap(), 0);
        if line.starts_with("ACK|3|JoinSession|") { break; }
        assert!(!line.starts_with("ERR|"), "{line}");
    }
    shared.release.store(true, Ordering::Release);
    wait(|| stat(admin, "stale") > stale);
    wait(|| shared.history_started.load(Ordering::Acquire) > 0);
    assert_eq!(changed_state(&mut one).fields[1], "[2,2,3,4,5,6]", "a Join on peer2 must not invalidate peer1's captured sample");
    let probes = shared.started.load(Ordering::Acquire);
    let dumps = shared.dumps.load(Ordering::Acquire);
    for _ in 0..10 { let _ = stat(admin, "delivered"); }
    assert_eq!(shared.started.load(Ordering::Acquire), probes, "no native state probe may begin through pending history");
    assert!(dumps > 0);
    // Return to the previously CACHED state: it still must be resent because
    // the intervening held Bf can have replaced the client's buffs.
    shared.motive.store(1.0f32.to_bits(), Ordering::Release);
    shared.history_hold.store(false, Ordering::Release);
    assert_eq!(state_after_buff(&mut one).fields[1], "[1,2,3,4,5,6]");
    // This also rejects accidental generic broadcast of peer1's earlier value2:
    // peer2 must see its final held Bf BEFORE its first eligible state delivery.
    assert_eq!(state_after_buff(&mut two).fields[1], "[1,2,3,4,5,6]");
    drop(one);
    drop(two);
    server.shutdown().unwrap();
}

#[test]
fn known_sim_state_progresses_on_both_sockets_while_fresh_history_pages_continue() {
    let (shared, server, mut one, mut two) = fixture();
    let admin = server.admin_addr().unwrap();
    shared.fresh.store(true, Ordering::Release);
    wait(|| shared.fresh_pages.load(Ordering::Acquire) > 0);
    assert_eq!(stat(admin, "presentation_join_history_pending"), 0);
    assert!(stat(admin, "presentation_fresh_history_pending") > 0);
    shared.motive.store(9.0f32.to_bits(), Ordering::Release);
    let first = changed_state(&mut one);
    let second = changed_state(&mut two);
    assert_eq!(first.fields[0], "20");
    assert_eq!(second.fields[0], "20");
    assert_eq!(first.fields[1], "[9,2,3,4,5,6]");
    assert_eq!(second.fields[1], "[9,2,3,4,5,6]");
    assert_eq!(first.fields.len(), 3, "private sample token must never reach a socket");
    assert!(stat(admin, "presentation_fresh_history_pending") > 0,
        "both known recipients must progress before the unrelated Fresh gather ends");
    assert!(shared.fresh_pages.load(Ordering::Acquire) > 1);
    assert_eq!(state_after_fresh_baseline(&mut one).fields[1], "[9,2,3,4,5,6]");
    assert_eq!(state_after_fresh_baseline(&mut two).fields[1], "[9,2,3,4,5,6]");
    drop(one);
    drop(two);
    server.shutdown().unwrap();
}

#[test]
fn malformed_reply_recovers_and_skipped_baseline_does_not_advance_the_watermark() {
    let (shared, server, mut one, mut two) = fixture();
    let admin = server.admin_addr().unwrap();
    wait(|| stat(admin, "delivered") >= 1);
    let delivered = stat(admin, "delivered");
    let errors = stat(admin, "errors");
    let dumps = shared.dumps.load(Ordering::Acquire);
    shared.malformed.store(true, Ordering::Release);
    wait(|| stat(admin, "errors") > errors);
    wait(|| shared.dumps.load(Ordering::Acquire) > dumps);
    assert_eq!(stat(admin, "delivered"), delivered);
    shared.baseline.store(true, Ordering::Release);
    shared.malformed.store(false, Ordering::Release);
    shared.motive.store(9.0f32.to_bits(), Ordering::Release);
    assert_eq!(changed_state(&mut one).fields[1], "[9,2,3,4,5,6]");
    assert_eq!(
        stat(admin, "delivered"),
        delivered + 1,
        "one skipped recipient must not prevent the successful peer's cache advancement"
    );
    shared.baseline.store(false, Ordering::Release);
    assert_eq!(changed_state(&mut two).fields[1], "[9,2,3,4,5,6]");
    wait(|| stat(admin, "delivered") >= delivered + 2);
    drop(one);
    drop(two);
    server.shutdown().unwrap();
}
