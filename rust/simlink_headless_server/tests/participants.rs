#![forbid(unsafe_code)]
//! Actual serve/socket participant creation, not a fake public Spawn producer.
pub mod common;
use simlink_headless_server::{messages::{self as m, Command}, serve::RunningServer,
    snapshot::{Sim, Snapshot}, stream::StreamOptions, transport::{StreamReply, StreamRequest},
    DriverError, FakeDriver, GameDriver};
use std::{io::{BufRead, BufReader, Write}, net::{TcpListener, TcpStream}, sync::{Arc, atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering}}, time::{Duration, Instant}};

#[derive(Default)]
struct Shared {
    world: AtomicU64,
    authority: AtomicU64,
    exports: AtomicUsize,
    dumps: AtomicUsize,
    witness_full: AtomicUsize,
    release: AtomicBool,
    hold: AtomicBool,
    epoch_commits: AtomicUsize,
    joining_conn: AtomicU64,
    baseline_hold: AtomicBool,
    page: AtomicBool,
    new_claim: AtomicBool,
    legacy: AtomicBool,
    flood: AtomicBool,
}
struct Driver { fake: FakeDriver, shared: Arc<Shared>, pending: Option<simlink_bridge::verbs::SimDescriptionPayload>, announced: u64, events: Vec<Vec<u8>> }
fn sim() -> Sim {
    Sim { object_id: 20, name: "participant".into(), household_id: 1, position: [1., 2., 3.],
        interaction_id: None, owning_player_id: Some(2), interaction_key: None, lot_id: Some(100),
        queue_count: None, posture_container: None, posture_type: None, description_id: Some(200) }
}
impl GameDriver for Driver {
    fn participant_lifecycle_enabled(&self) -> bool { true }
    fn adjudicate(&mut self, command: &Command) -> Result<Vec<u8>, DriverError> { self.fake.adjudicate(command) }
    fn adjudicate_for(&mut self, conn: simlink_headless_server::ConnId, command: &Command) -> Result<Vec<u8>, DriverError> {
        if command.verb == "JoinSession" && command.player_id == 2 { self.shared.joining_conn.store(conn.0, Ordering::Release); }
        self.adjudicate(command)
    }
    fn baseline_pending(&self, conn: simlink_headless_server::ConnId) -> bool {
        self.shared.baseline_hold.load(Ordering::Acquire) && self.shared.joining_conn.load(Ordering::Acquire) == conn.0
    }
    fn presentation_pages(&mut self) -> Result<Vec<String>, DriverError> {
        Ok(if !self.shared.baseline_hold.load(Ordering::Acquire) && self.shared.page.swap(false, Ordering::AcqRel) {
            vec![if self.shared.flood.load(Ordering::Acquire) {
                let rows = (10..4107).map(|driver| format!("{}|A|{driver}|30|1|2|3|-2", driver + 100)).collect::<Vec<_>>();
                format!("seq=5000;more=0\n{}", rows.join("\n"))
            } else if self.shared.new_claim.load(Ordering::Acquire) {
                "seq=52;more=0\n50|A|8|30|1|2|3|-2\n51|E|8|1|2|0|30\n52|E|7|1|2|0|20".into()
            } else { "seq=51;more=0\n50|A|7|20|1|2|3|-2\n51|E|7|1|2|0|20".into() }]
        } else { vec![] })
    }
    fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, DriverError> { Ok(std::mem::take(&mut self.events)) }
    fn claimed_sims(&self) -> Vec<u64> { if self.shared.new_claim.load(Ordering::Acquire) { vec![20, 30] } else { vec![20] } }
    fn presentation_world_generation(&self) -> u64 { self.shared.world.load(Ordering::Acquire) }
    fn state_authority_generation(&self) -> u64 { self.shared.authority.load(Ordering::Acquire) }
    fn take_epoch_announcement(&mut self) -> Option<u64> {
        let world = self.presentation_world_generation(); (world > self.announced).then_some(world)
    }
    fn announced(&mut self, epoch: u64) {
        self.announced = epoch;
        self.shared.epoch_commits.fetch_add(1, Ordering::AcqRel);
        let event = m::Event { seq: 0, tick: 1, corr: 0, kind: "SimAnimation".into(), fields: vec![m::enc_list(&[m::hex_encode(&format!("0|Ne|{epoch}"))])] };
        self.events.push(simlink_headless_server::encode_event(&event).unwrap().into_bytes());
    }
    fn snapshot(&mut self, since: i64) -> Result<Option<Snapshot>, DriverError> {
        if since < 0 { self.shared.witness_full.fetch_add(1, Ordering::AcqRel); }
        let mut sims = if since < 0 { vec![sim()] } else { vec![] };
        if since < 0 && self.shared.new_claim.load(Ordering::Acquire) {
            let mut new = sim(); new.object_id = 30; new.description_id = Some(300); sims.push(new);
        }
        Ok(Some(Snapshot { tick: 1, world_name: "Fixture".into(), save_name: "Fixture".into(),
            speed: "Normal".into(), sims, objects: vec![] }))
    }
    fn recorder(&mut self, args: &[String]) -> Result<String, DriverError> {
        Ok(match args[0].as_str() {
            "snapshot" => if args[1] == "30" { "sims=30;drivers=8".into() } else { "sims=20;drivers=7".into() },
            "snapshot-driver" => format!("{}\n0|A|{}|{}|1|2|3|-2",
                if self.shared.legacy.load(Ordering::Acquire) { "next=-1".into() } else { format!("next=-1;snapshot={};cursor=0", args[3]) },
                args[1], if args[1] == "8" { 30 } else { 20 }),
            "dump" => {
                let seq = self.shared.dumps.fetch_add(1, Ordering::AcqRel) + 1;
                format!("seq={seq};more=0\n{seq}|Ob|90|SetPosition:v|1~2~3")
            }
            _ => "recorder=on;ring=on".into(),
        })
    }
    fn start_stream_request(&mut self, request: &StreamRequest) -> Result<Option<StreamReply>, DriverError> {
        Ok(Some(match request {
            StreamRequest::ExportSimDescription(desc) => {
                let attempt = self.shared.exports.fetch_add(1, Ordering::AcqRel) + 1;
                eprintln!("participant_fixture|export_attempt={attempt}|description={desc}|release={}", self.shared.release.load(Ordering::Acquire));
                let payload = simlink_bridge::verbs::SimDescriptionPayload { desc: *desc,
                    property_stream: vec![self.presentation_world_generation() as u8, 0, 127, 128, 255], res_key_table: vec![], object_id_table: vec![0xb1, 0x23] };
                if self.shared.hold.load(Ordering::Acquire) {
                    self.pending = Some(payload); return Ok(None);
                }
                if !self.shared.release.load(Ordering::Acquire) { return Err(DriverError("fixture export unavailable".into())); }
                StreamReply::Description(payload)
            }
            StreamRequest::Snapshot(since) => StreamReply::Snapshot(self.snapshot(*since)?),
            StreamRequest::Recorder(args) => StreamReply::Recorder(self.recorder(args)?),
            StreamRequest::DiagState => StreamReply::DiagState(None),
            StreamRequest::ProbeSimState(_) => StreamReply::SimState("-".into()),
        }))
    }
    fn poll_stream_request(&mut self) -> Result<Option<StreamReply>, DriverError> {
        if !self.shared.release.load(Ordering::Acquire) { return Ok(None); }
        Ok(self.pending.take().map(StreamReply::Description))
    }
    fn stream_request_pending(&self) -> bool { self.pending.is_some() }
}
fn start(shared: &Arc<Shared>) -> RunningServer {
    start_with_poll(shared, 10)
}
fn start_with_poll(shared: &Arc<Shared>, poll_ms: u64) -> RunningServer {
    let shared = Arc::clone(shared);
    RunningServer::start_configured(TcpListener::bind("127.0.0.1:0").unwrap(), Some(TcpListener::bind("127.0.0.1:0").unwrap()),
        move || Ok(Driver { fake: FakeDriver::default(), shared, pending: None, announced: 0, events: vec![] }), 8,
        StreamOptions { presentation_poll_ms: poll_ms, presentation_min_interval_ms: 0,
            base_interval_ms: 10_000.0, ..StreamOptions::default() }).unwrap()
}
fn join(server: &RunningServer, player: i32) -> common::Peer {
    let mut peer = common::Peer::connect(server);
    assert!(peer.exchange(&format!("CMD|1|JoinSession|1|{player}|6a|~")).starts_with("ACK|1|JoinSession")); peer
}
fn wait(mut f: impl FnMut() -> bool) {
    let end = Instant::now() + Duration::from_secs(5);
    while !f() { assert!(Instant::now() < end, "participant fixture deadline"); std::thread::sleep(Duration::from_millis(5)); }
}
fn report(server: &RunningServer) -> String {
    let mut socket = TcpStream::connect(server.admin_addr().unwrap()).unwrap();
    socket.set_read_timeout(Some(Duration::from_secs(2))).unwrap();
    writeln!(socket, "FRONT_DOOR_STATE").unwrap();
    let mut line = String::new(); BufReader::new(socket).read_line(&mut line).unwrap();
    let value: serde_json::Value = serde_json::from_str(&line).unwrap();
    assert_eq!(value["accepted"], true); value["report"].as_str().unwrap().into()
}
fn initialized(peer: &mut common::Peer, source_world: u8) {
    initialized_for(peer, source_world, 20, 200)
}
fn initialized_for(peer: &mut common::Peer, source_world: u8, sim: u64, desc: u64) {
    let mut description = false;
    let mut spawn = false;
    let mut description_ids: Option<Vec<u64>> = None;
    let mut arrays = std::collections::HashMap::new();
    let mut bulks = simlink_headless_server::wire_v1::BulkReceiver::new();
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        assert!(Instant::now() < deadline, "participant initialization delivery deadline");
        let line = peer.next().expect("peer remains open");
        let fields: Vec<String> = line.split('|').map(str::to_owned).collect();
        if fields[0] == "BULK" {
            let chunk = simlink_headless_server::wire_v1::decode_bulk(&fields).unwrap();
            if let Some(ids) = &description_ids {
                assert!(ids.contains(&chunk.bulk_id), "only this introducer's raw byte fields may precede Spawn");
                let bytes = bulks.submit(&chunk).unwrap();
                if chunk.index + 1 == chunk.count { assert!(arrays.insert(chunk.bulk_id, bytes).is_none()); }
                if arrays.len() == 3 {
                    assert_eq!(arrays[&ids[0]], [source_world, 0, 127, 128, 255]);
                    assert!(arrays[&ids[1]].is_empty());
                    assert_eq!(arrays[&ids[2]], [0xb1, 0x23]);
                    description = true;
                    description_ids = None;
                }
            }
            continue;
        }
        if fields[0] != "EV" { continue; }
        let event = simlink_headless_server::decode_event(&fields).unwrap();
        assert_ne!(event.kind, "FullSnapshot", "internal witness read must not publish an extra public FullSnapshot");
        match event.kind.as_str() {
            "SimDescriptionData" => {
                eprintln!("participant_fixture|peer={:?}|description={:?}", peer.as_ref().local_addr(), event.fields);
                assert_eq!(event.fields[0], desc.to_string());
                assert!(!description && description_ids.is_none(), "no duplicate successful description");
                let ids: Vec<_> = event.fields[1..].iter().map(|field| m::dec_bulk_ref(field).expect("description byte arrays are raw BULKs")).collect();
                assert_eq!(ids.len(), 3);
                assert!(ids.windows(2).all(|pair| pair[0] < pair[1]));
                description_ids = Some(ids);
            }
            "SimSpawned" => {
                eprintln!("participant_fixture|peer={:?}|spawn={:?}", peer.as_ref().local_addr(), event.fields);
                assert!(description && description_ids.is_none(), "complete raw description bytes precede spawn");
                assert_eq!(&event.fields[..2], [sim.to_string(), desc.to_string()]);
                assert_eq!(m::dec_record(&event.fields[2]).unwrap(), ["1", "2", "3"]);
                assert!(!spawn); spawn = true;
            }
            "SimAnimation" => {
                for record in m::dec_list(&event.fields[0]).unwrap() {
                    let record = m::hex_decode(&record).unwrap();
                    if record.contains(&format!("|A|{}|{sim}|", if sim == 30 { 8 } else { 7 })) { eprintln!("participant_fixture|peer={:?}|acquire={record}", peer.as_ref().local_addr()); assert!(spawn, "affected history cannot precede participant spawn"); return; }
                }
            }
            _ => {}
        }
    }
}
#[test]
fn native_enabled_serve_initializes_both_recipients_and_keeps_dump_progress_during_export_failure() {
    for legacy in [false, true] {
    eprintln!("participant_fixture|legacy={legacy}");
    let shared = Arc::new(Shared::default());
    shared.legacy.store(legacy, Ordering::Release);
    let server = start(&shared);
    let mut first = join(&server, 1);
    let mut second = join(&server, 2);
    let joined = report(&server);
    for player in [1, 2] {
        assert!(joined.lines().any(|row| row.starts_with(&format!("client|{player}|"))), "both actual players must be joined: {joined}");
    }
    assert_ne!(shared.joining_conn.load(Ordering::Acquire), 0, "the player2 native baseline gate must see its actual connection");
    wait(|| shared.exports.load(Ordering::Acquire) > 0);
    let before = shared.dumps.load(Ordering::Acquire);
    wait(|| shared.dumps.load(Ordering::Acquire) > before + 3);
    assert!(shared.witness_full.load(Ordering::Acquire) > 0);
    shared.release.store(true, Ordering::Release);
    let first_result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| initialized(&mut first, 0)));
    if let Err(error) = first_result {
        eprintln!("participant_fixture|before_peer_drop|{}", report(&server));
        std::panic::resume_unwind(error);
    }
    initialized(&mut second, 0);
    assert!(shared.exports.load(Ordering::Acquire) >= 3, "two successful recipient exports plus retained refusal");
    assert!(report(&server).contains("participant_queued=2"));
    server.shutdown().unwrap();
    }
}
#[test]
fn native_enabled_delayed_old_world_export_cannot_create_a_participant_after_world_replacement() {
    let shared = Arc::new(Shared::default()); shared.hold.store(true, Ordering::Release);
    let server = start(&shared);
    let mut first = join(&server, 1);
    wait(|| shared.exports.load(Ordering::Acquire) == 1);
    shared.world.store(1, Ordering::Release);
    shared.hold.store(false, Ordering::Release);
    shared.release.store(true, Ordering::Release);
    initialized(&mut first, 1);
    assert!(shared.exports.load(Ordering::Acquire) >= 2);
    assert_eq!(shared.epoch_commits.load(Ordering::Acquire), 1, "old uninitialized gather must not deadlock the epoch cut");
    assert!(report(&server).contains("participant_world_cancellations=1"));
    server.shutdown().unwrap();
}
#[test]
fn completed_native_baseline_materializes_join_hold_before_available_animation_pages() {
    let shared = Arc::new(Shared::default()); shared.release.store(true, Ordering::Release);
    let server = start_with_poll(&shared, 10_000);
    let mut first = join(&server, 1);
    initialized(&mut first, 0);
    shared.baseline_hold.store(true, Ordering::Release);
    shared.release.store(false, Ordering::Release);
    let exports_before = shared.exports.load(Ordering::Acquire);
    let mut second = join(&server, 2);
    assert_ne!(shared.joining_conn.load(Ordering::Acquire), 0, "hold the actual player2 native baseline");
    shared.page.store(true, Ordering::Release);
    shared.baseline_hold.store(false, Ordering::Release);
    wait(|| !shared.page.load(Ordering::Acquire) && shared.exports.load(Ordering::Acquire) > exports_before);
    shared.release.store(true, Ordering::Release);
    initialized(&mut second, 0); // rejects the positive A if it escaped before creation
    server.shutdown().unwrap();
}
#[test]
fn new_claim_materializes_fresh_hold_between_deadlines_while_known_participant_progresses() {
    let shared = Arc::new(Shared::default()); shared.release.store(true, Ordering::Release);
    let server = start_with_poll(&shared, 10_000);
    let mut peer = join(&server, 1);
    initialized(&mut peer, 0);
    shared.release.store(false, Ordering::Release);
    shared.new_claim.store(true, Ordering::Release);
    shared.page.store(true, Ordering::Release);
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        assert!(Instant::now() < deadline, "known participant SACS progress deadline");
        let line = peer.next().expect("known participant remains live");
        let fields: Vec<_> = line.split('|').map(str::to_owned).collect();
        if fields[0] != "EV" { continue; }
        let event = simlink_headless_server::decode_event(&fields).unwrap();
        assert_ne!(event.kind, "SimSpawned", "export remains unavailable");
        if event.kind != "SimAnimation" { continue; }
        let records: Vec<_> = m::dec_list(&event.fields[0]).unwrap().iter().map(|record| m::hex_decode(record).unwrap()).collect();
        assert!(!records.iter().any(|record| record.contains("|A|8|30|")), "new claimed actor may not receive early history");
        if records.iter().any(|record| record == "52|E|7|1|2|0|20") { break; }
    }
    shared.release.store(true, Ordering::Release);
    initialized_for(&mut peer, 0, 30, 300);
    server.shutdown().unwrap();
}
#[test]
fn late_driver_ownership_overflow_stops_the_actor_instead_of_releasing_partial_fresh_history() {
    let shared = Arc::new(Shared::default()); shared.release.store(true, Ordering::Release);
    let server = start_with_poll(&shared, 10_000);
    let mut peer = join(&server, 1); initialized(&mut peer, 0);
    shared.release.store(false, Ordering::Release);
    shared.new_claim.store(true, Ordering::Release);
    shared.flood.store(true, Ordering::Release);
    shared.page.store(true, Ordering::Release);
    wait(|| TcpStream::connect(server.local_addr()).is_err());
    let error = server.shutdown().expect_err("overflow is a named actor failure");
    assert!(error.to_string().contains("fresh presentation driver ownership capacity exceeded"));
}
