#![forbid(unsafe_code)]

use std::collections::HashMap;
use std::io::{BufRead, BufReader, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::{Arc, Mutex};
use std::time::Duration;
use simlink_bridge::verbs::BridgeCommand;
use simlink_headless_server::bridge_driver::{BridgeEndpoint, BridgeGameDriver};
use simlink_headless_server::messages::{self as m, Command, Event};
use simlink_headless_server::routing::RoutedEvent;
use simlink_headless_server::serve::RunningServer;
use simlink_headless_server::snapshot::Snapshot;
use simlink_headless_server::stream::{Stream, StreamOptions};
use simlink_headless_server::transport::{StreamReply, StreamRequest};
use simlink_headless_server::{ConnId, DriverError, FakeDriver, GameDriver, Server};

fn snapshot(tick: i64) -> Snapshot {
    Snapshot { tick, world_name: "W".into(), save_name: "S".into(), speed: "Normal".into(),
        sims: Vec::new(), objects: Vec::new() }
}
fn join(player: i32) -> Command {
    Command { corr: 1, command_id: 1, player_id: player, verb: "JoinSession".into(),
        args: vec![m::hex_encode("joiner"), "~".into()] }
}

#[derive(Default)]
struct EndpointState {
    pending: Option<BridgeCommand>,
    ready: bool,
    snapshots: Vec<Option<i64>>,
}
struct DeferredEndpoint(Arc<Mutex<EndpointState>>);
impl BridgeEndpoint for DeferredEndpoint {
    fn command(&mut self, command: &BridgeCommand) -> Result<Vec<String>, DriverError> {
        if matches!(command, BridgeCommand::RequestSnapshot { .. }) {
            return Err(DriverError("blocking join snapshot was used".into()));
        }
        if let BridgeCommand::SacsRecorder { corr, .. } = command {
            return Ok(vec![format!("AUTONOMY_PROBE|{}|ok|", corr.to_n())]);
        }
        Ok(Vec::new())
    }
    fn events(&mut self) -> Result<Vec<Vec<u8>>, DriverError> { Ok(Vec::new()) }
    fn begin_stream_command(&mut self, command: &BridgeCommand) -> Result<Option<Vec<String>>, DriverError> {
        if !matches!(command, BridgeCommand::RequestSnapshot { .. }) { return self.command(command).map(Some); }
        let mut state = self.0.lock().unwrap();
        assert!(state.pending.is_none(), "two native requests were issued concurrently");
        if let BridgeCommand::RequestSnapshot { since_tick, .. } = command {
            state.snapshots.push(*since_tick);
        }
        state.pending = Some(command.clone());
        Ok(None)
    }
    fn poll_stream_command(&mut self) -> Result<Option<Vec<String>>, DriverError> {
        let mut state = self.0.lock().unwrap();
        if !state.ready { return Ok(None); }
        Ok(state.pending.take().map(|command| match command {
            BridgeCommand::RequestSnapshot { corr, .. } =>
                vec![format!("SNAPSHOT|{}|77|57|53|Normal|0|0", corr.to_n())],
            _ => unreachable!(),
        }))
    }
}
fn deferred_join() -> (BridgeGameDriver<DeferredEndpoint>, Arc<Mutex<EndpointState>>) {
    let state = Arc::new(Mutex::new(EndpointState::default()));
    (BridgeGameDriver::new(DeferredEndpoint(Arc::clone(&state))), state)
}

#[test]
fn i2_i3_ne_follows_the_deferred_full_snapshot_before_joiner_presentation() {
    let state = Arc::new(Mutex::new(EndpointState::default()));
    let endpoint = Arc::clone(&state);
    let server = RunningServer::start_configured(TcpListener::bind("127.0.0.1:0").unwrap(), None,
        move || {
            let mut driver = BridgeGameDriver::new(DeferredEndpoint(endpoint));
            driver.configure_rust_switches(true,false,false);
            driver.poll_routed_events().unwrap();
            // The recorder is empty: establish the already announced epoch through
            // the same seam the stream uses before this late join arrives.
            assert_eq!(driver.take_epoch_announcement(),Some(1));
            driver.announced(1);
            driver.take_pending_events();
            Ok(driver)
        }, 8, StreamOptions { base_interval_ms:0.0,..StreamOptions::default() }).unwrap();
    let mut peer = connect(server.local_addr());
    exchange(&mut peer,"HELLO|1|ring=1,wfs=1");
    assert!(exchange(&mut peer,"CMD|1|JoinSession|1|7|6a|~").starts_with("ACK|1|JoinSession|resume_token="));
    assert!(state.lock().unwrap().pending.is_some(),"ACK overtook no deferred native baseline");
    state.lock().unwrap().ready = true;
    let mut seq = 0;
    for kind in ["SessionJoined","GameSpeedChanged","FullSnapshot","LotLocalAuthorityModeChanged","SimAnimation"] {
        let event = receive(&mut peer);
        assert_eq!(event.kind,kind,"presentation overtook or missed the completed baseline");
        assert!(event.seq > seq);
        seq = event.seq;
        if kind == "SimAnimation" {
            assert_eq!(event.corr,0);
            assert_eq!(m::dec_list(&event.fields[0]).unwrap(),[m::hex_encode("0|Ne|1")]);
        } else { assert_eq!(event.corr,1); }
    }
    server.shutdown().unwrap();
}

#[test]
fn ne_targeted_follows_the_whole_deferred_join_group() {
    let (mut driver,state) = deferred_join();
    driver.configure_rust_switches(true,false,false);
    driver.poll_routed_events().unwrap();
    assert_eq!(driver.take_epoch_announcement(),Some(1));
    driver.announced(1);
    driver.take_pending_events();
    driver.adjudicate_for(ConnId(1),&join(7)).unwrap();
    assert!(driver.baseline_pending(ConnId(1)));
    assert!(driver.take_pending_events().is_empty());
    state.lock().unwrap().ready = true;
    let events = driver.poll_routed_events().unwrap();
    assert_eq!(events.iter().map(|event| event.event.kind.as_str()).collect::<Vec<_>>(),
        ["SessionJoined","GameSpeedChanged","FullSnapshot","LotLocalAuthorityModeChanged","SimAnimation"]);
    assert!(events[..4].iter().all(|event| event.event.corr == 1));
    let ne = &events[4];
    assert_eq!(ne.requester,Some(ConnId(1)));
    assert_eq!(ne.event.corr,0);
    assert_eq!(ne.event.fields,[m::enc_list(&[m::hex_encode("0|Ne|1")])]);
    assert!(!driver.baseline_pending(ConnId(1)));
}

#[test]
fn i1_join_ack_does_not_wait_for_the_native_baseline() {
    let (mut driver, state) = deferred_join();
    let reply = driver.adjudicate_for(ConnId(1), &join(7)).unwrap();
    assert_eq!(reply, b"ACK|1|JoinSession");
    assert!(driver.baseline_pending(ConnId(1)));
    assert!(state.lock().unwrap().pending.is_some());
    assert!(driver.poll_routed_events().unwrap().is_empty());
}

#[test]
fn i2_baseline_is_one_ordered_group_with_the_join_correlation() {
    let (mut driver, state) = deferred_join();
    assert!(driver.adjudicate_for(ConnId(1), &join(7)).unwrap().starts_with(b"ACK|"));
    assert!(driver.poll_routed_events().unwrap().is_empty());
    state.lock().unwrap().ready = true;
    let events = driver.poll_routed_events().unwrap();
    assert_eq!(events.iter().map(|event| event.event.kind.as_str()).collect::<Vec<_>>(),
        ["SessionJoined", "GameSpeedChanged", "FullSnapshot", "LotLocalAuthorityModeChanged"]);
    assert!(events.iter().all(|event| event.event.corr == 1));
    assert!(!driver.baseline_pending(ConnId(1)));
}

#[test]
fn i7_baseline_uses_its_own_full_native_snapshot() {
    let (mut driver, state) = deferred_join();
    driver.adjudicate_for(ConnId(1), &join(7)).unwrap();
    state.lock().unwrap().ready = true;
    let events = driver.poll_routed_events().unwrap();
    let baseline = events.iter().find(|event| event.event.kind == "FullSnapshot").unwrap();
    assert_eq!(baseline.requester, Some(ConnId(1)));
    assert_eq!(baseline.event.fields, [snapshot(77).wire_body()]);
    assert_eq!(state.lock().unwrap().snapshots, [None]);
}

#[test]
fn i1_i2_pipelined_commands_wait_behind_the_complete_baseline_group() {
    let state = Arc::new(Mutex::new(EndpointState::default()));
    let endpoint = Arc::clone(&state);
    let server = RunningServer::start_configured(TcpListener::bind("127.0.0.1:0").unwrap(), None,
        move || Ok(BridgeGameDriver::new(DeferredEndpoint(endpoint))), 8,
        StreamOptions { base_interval_ms: 0.0, ..StreamOptions::default() }).unwrap();
    let mut peer = connect(server.local_addr());
    exchange(&mut peer, "HELLO|1|ring=1,wfs=1");
    writeln!(peer.get_mut(), "CMD|1|JoinSession|1|7|{}|~\nCMD|2|AckSnapshot|2|7|77", m::hex_encode("joiner")).unwrap();
    let mut ack = String::new();
    peer.read_line(&mut ack).unwrap();
    assert!(ack.starts_with("ACK|1|JoinSession|resume_token="), "{ack}");
    assert!(!state.lock().unwrap().ready);
    state.lock().unwrap().ready = true;
    for expected in ["SessionJoined", "GameSpeedChanged", "FullSnapshot", "LotLocalAuthorityModeChanged"] {
        let event = receive(&mut peer);
        assert_eq!(event.kind, expected);
        assert_eq!(event.corr, 1);
    }
    assert!(exchange(&mut peer, "CMD|3|AckSnapshot|3|7|77").starts_with("ACK|2|AckSnapshot"),
        "the pipelined command was lost or overtook the baseline");
    server.shutdown().unwrap();
}

fn connect(addr: std::net::SocketAddr) -> BufReader<TcpStream> {
    let socket = TcpStream::connect(addr).unwrap();
    socket.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
    BufReader::new(socket)
}
fn exchange(peer: &mut BufReader<TcpStream>, line: &str) -> String {
    writeln!(peer.get_mut(), "{line}").unwrap();
    let mut reply = String::new();
    assert_ne!(peer.read_line(&mut reply).unwrap(), 0);
    reply.trim_end().into()
}
fn receive(peer: &mut BufReader<TcpStream>) -> Event {
    loop {
        let mut reply = String::new();
        assert_ne!(peer.read_line(&mut reply).unwrap(), 0);
        let fields = simlink_headless_server::wire_v1::split(reply.trim_end().as_bytes()).unwrap();
        if fields[0] == "BULK" {
            simlink_headless_server::wire_v1::decode_bulk(&fields).unwrap();
            continue;
        }
        return simlink_headless_server::wire_v1::decode_event(&fields).unwrap();
    }
}

struct BaselineOrder {
    fake: FakeDriver,
    conn: Option<ConnId>,
    polls: usize,
}
impl GameDriver for BaselineOrder {
    fn adjudicate(&mut self, command: &Command) -> Result<Vec<u8>, DriverError> { self.fake.adjudicate(command) }
    fn adjudicate_for(&mut self, conn: ConnId, command: &Command) -> Result<Vec<u8>, DriverError> {
        self.conn = Some(conn);
        self.fake.adjudicate(command)
    }
    fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, DriverError> { Ok(Vec::new()) }
    fn baseline_pending(&self, conn: ConnId) -> bool { self.conn == Some(conn) && self.polls < 4 }
    fn poll_routed_events(&mut self) -> Result<Vec<RoutedEvent>, DriverError> {
        let Some(conn) = self.conn else { return Ok(Vec::new()); };
        self.polls += 1;
        let mut events = Vec::new();
        if self.polls == 4 {
            for (kind, fields) in [
                ("SessionJoined", vec!["7".into(), m::hex_encode("joiner"), "~".into(), "[]".into()]),
                ("GameSpeedChanged", vec!["Normal".into()]),
                ("FullSnapshot", vec![snapshot(77).wire_body()]),
                ("LotLocalAuthorityModeChanged", vec!["7".into(), "FullPuppetFallback".into()]),
            ] {
                events.push(RoutedEvent { event: Event { seq: 0, tick: 77, corr: 1, kind: kind.into(), fields },
                    owner: None, requester: Some(conn) });
            }
        }
        for (kind, fields) in [
            ("SnapshotDelta", vec![snapshot(78).wire_body(), "0".into()]),
            ("SimAnimation", vec![m::enc_list(&[m::hex_encode("0|live")])]),
        ] {
            events.push(RoutedEvent { event: Event { seq: 0, tick: 78, corr: 0, kind: kind.into(), fields },
                owner: None, requester: None });
        }
        Ok(events)
    }
}

#[test]
fn i3_stream_events_cannot_overtake_the_join_baseline() {
    let server = RunningServer::start_configured(TcpListener::bind("127.0.0.1:0").unwrap(), None,
        || Ok(BaselineOrder { fake: FakeDriver::new(), conn: None, polls: 0 }), 8,
        StreamOptions { base_interval_ms: 0.0, ..StreamOptions::default() }).unwrap();
    let mut peer = connect(server.local_addr());
    assert!(exchange(&mut peer, "HELLO|1|ring=1,wfs=1").starts_with("WELCOME"));
    assert!(exchange(&mut peer, "CMD|1|JoinSession|1|7|6a|~").starts_with("ACK"));
    for expected in ["SessionJoined", "GameSpeedChanged", "FullSnapshot", "LotLocalAuthorityModeChanged"] {
        assert_eq!(receive(&mut peer).kind, expected);
    }
    assert_eq!(receive(&mut peer).kind, "SnapshotDelta");
    assert_eq!(receive(&mut peer).kind, "SimAnimation");
    server.shutdown().unwrap();
}

#[derive(Default)]
struct DeferredRecorder {
    fake: FakeDriver,
    claims: Vec<u64>,
    pending: Option<(StreamRequest, usize)>,
    pages: HashMap<u64, usize>,
    requests: Vec<Vec<String>>,
    selected: Vec<u64>,
    locked: bool,
    emit: bool,
    duplicate: bool,
    seq: i64,
    produced: Vec<String>,
    stall_history: bool,
    epoch: Option<u64>,
    events: Vec<RoutedEvent>,
    dump_more: bool,
    dump_completions: usize,
}
impl DeferredRecorder {
    fn answer(&mut self, request: StreamRequest) -> StreamReply {
        match request {
            StreamRequest::Snapshot(_) => StreamReply::Snapshot(Some(snapshot(77))),
            StreamRequest::DiagState => StreamReply::DiagState(None),
            StreamRequest::Recorder(args) => StreamReply::Recorder(match args[0].as_str() {
                "snapshot" => format!("drivers={}", args[1]),
                "snapshot-driver" => {
                    let handle: u64 = args[1].parse().unwrap();
                    let offset: usize = args[2].parse().unwrap();
                    let pages = self.pages.get(&handle).copied().unwrap_or(1);
                    let next = if offset + 1 >= pages { -1 } else { (offset + 1) as i32 };
                    if self.selected.last() == Some(&handle) && (next < 0 || offset == 15) { self.locked = false; }
                    format!("lines=1;next={next}\n0|H|{handle}|{offset}")
                }
                "dump" => {
                    self.dump_completions += 1;
                    format!("seq={};more={}",self.seq,u8::from(self.dump_more))
                }
                _ => String::new(),
            }),
        }
    }
}
impl GameDriver for DeferredRecorder {
    fn adjudicate(&mut self, command: &Command) -> Result<Vec<u8>, DriverError> { self.fake.adjudicate(command) }
    fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, DriverError> { Ok(Vec::new()) }
    fn claimed_sims(&self) -> Vec<u64> { self.claims.clone() }
    fn recorder(&mut self, _: &[String]) -> Result<String, DriverError> {
        Err(DriverError("blocking recorder was used".into()))
    }
    fn start_stream_request(&mut self, request: &StreamRequest) -> Result<Option<StreamReply>, DriverError> {
        assert!(self.pending.is_none(), "a request overtook the pending bridge request");
        if let StreamRequest::Recorder(args) = request {
            if args[0] == "snapshot" {
                assert!(!self.locked, "two history snapshots interleaved");
                self.selected = args[1].split(',').map(|value| value.parse().unwrap()).collect();
                self.locked = !self.selected.is_empty();
            } else if args[0] == "dump" {
                assert!(!self.locked, "dump changed the active gather's recorder targets");
            }
            self.requests.push(args.clone());
        }
        self.pending = Some((request.clone(), 2));
        Ok(None)
    }
    fn poll_stream_request(&mut self) -> Result<Option<StreamReply>, DriverError> {
        let Some((request, remaining)) = &mut self.pending else { return Ok(None); };
        if self.stall_history && matches!(request, StreamRequest::Recorder(args) if args[0] == "snapshot") {
            return Ok(None);
        }
        *remaining -= 1;
        if *remaining > 0 { return Ok(None); }
        let (request, _) = self.pending.take().unwrap();
        Ok(Some(self.answer(request)))
    }
    fn stream_request_pending(&self) -> bool { self.pending.is_some() }
    fn take_epoch_announcement(&mut self) -> Option<u64> { self.epoch }
    fn announced(&mut self, epoch: u64) {
        assert_eq!(self.epoch,Some(epoch));
        self.events.push(RoutedEvent { event:Event { seq:0,tick:0,corr:0,kind:"SimAnimation".into(),
            fields:vec![m::enc_list(&[m::hex_encode(&format!("0|Ne|{epoch}"))])] },owner:None,requester:None });
        self.epoch = None;
    }
    fn take_pending_events(&mut self) -> Vec<RoutedEvent> { std::mem::take(&mut self.events) }
    fn presentation_pages(&mut self) -> Result<Vec<String>, DriverError> {
        if !self.emit { return Ok(Vec::new()); }
        self.seq += 1;
        let mut lines = vec![format!("0|L|{}", self.seq)];
        if self.duplicate {
            lines.push("0|H|55|0".into());
            self.duplicate = false;
        }
        self.produced.extend(lines.clone());
        Ok(vec![format!("seq={};more=0\n{}", self.seq, lines.join("\n"))])
    }
}
fn add(server: &mut Server, stream: &mut Stream, id: u64) {
    server.connect(ConnId(id));
    server.join(ConnId(id), id as i32);
    stream.joined(ConnId(id));
}
fn lines(events: &[RoutedEvent], id: u64) -> Vec<String> {
    events.iter().filter(|event| event.event.kind == "SimAnimation"
        && (event.requester.is_none() || event.requester == Some(ConnId(id))))
        .flat_map(|event| m::dec_list(&event.event.fields[0]).unwrap())
        .map(|line| m::hex_decode(&line).unwrap()).collect()
}
fn bootstrapped() -> (Server, Stream, DeferredRecorder, u64) {
    let mut server = Server::new(1);
    let mut stream = Stream::new(StreamOptions { base_interval_ms: 0.0, ..StreamOptions::default() });
    let mut driver = DeferredRecorder { claims: vec![55], ..DeferredRecorder::default() };
    add(&mut server, &mut stream, 1);
    let mut history_seen = false;
    for now in (0..4000).step_by(10) {
        let events = stream.pump(now, &mut server, &mut driver).unwrap();
        history_seen |= lines(&events, 1).contains(&"0|H|55|0".into());
        if history_seen && !stream.joining() && driver.pending.is_none()
            && driver.requests.last().is_some_and(|args| args[0] == "dump" && args[1] != simlink_headless_server::presentation::FROM_NOW_CURSOR.to_string()) {
            return (server, stream, driver, now + 10);
        }
    }
    panic!("first join never completed");
}

#[test]
fn ne_barrier_deferred_dump_limit_fails_closed_across_pumps_and_recovers() {
    let (mut server,mut stream,mut driver,mut now) = bootstrapped();
    let limit = 4096 + 1;
    let before = driver.dump_completions;
    driver.epoch = Some(2);
    driver.dump_more = true;
    driver.emit = true;
    assert!(lines(&stream.pump(now,&mut server,&mut driver).unwrap(),1).is_empty());
    driver.emit = false;
    now += 10;
    let mut failure = None;
    // Every request defers and needs two polls, as on the live bridge. A
    // per-pump reset never reaches the bound; keep that broken case finite.
    for _ in 0..limit*3+10 {
        let result = stream.pump(now,&mut server,&mut driver);
        now += 10;
        match result {
            Ok(events) => assert!(lines(&events,1).is_empty(),"epoch or old buffer escaped the barrier"),
            Err(error) => { failure = Some(error); break; }
        }
    }
    assert_eq!(failure.expect("deferred epoch drain never reached its bound").0,
        "navigation epoch dump page limit reached");
    assert_eq!(driver.dump_completions-before,limit);
    assert_eq!(stream.epoch_dump_limit_hits,1);
    assert_eq!(driver.epoch,Some(2));
    assert!(lines(&stream.take_events(&mut driver),1).is_empty());
    // Exhaustion remains fail-closed on a later still-stuck probe.
    let mut failed_again = false;
    for _ in 0..3 {
        let result = stream.pump(now,&mut server,&mut driver);
        now += 10;
        match result {
            Ok(events) => assert!(lines(&events,1).is_empty()),
            Err(error) => {
                assert_eq!(error.0,"navigation epoch dump page limit reached");
                failed_again = true;
                break;
            }
        }
    }
    assert!(failed_again);
    assert_eq!(driver.dump_completions-before,limit+1);
    assert_eq!(stream.epoch_dump_limit_hits,2);
    assert_eq!(driver.epoch,Some(2));
    driver.dump_more = false;
    let mut recovered = Vec::new();
    for _ in 0..3 {
        recovered.extend(lines(&stream.pump(now,&mut server,&mut driver).unwrap(),1));
        now += 10;
        if driver.epoch.is_none() { break; }
    }
    assert_eq!(recovered,["0|L|1","0|Ne|2"]);
    assert_eq!(driver.epoch,None);
    assert_eq!(stream.epoch_dump_limit_hits,2);
}

#[test]
fn ne_barrier_waits_for_deferred_history_and_drains_without_blocking_or_target_interleaving() {
    let (mut server,mut stream,mut driver,mut now) = bootstrapped();
    add(&mut server,&mut stream,2);
    driver.emit = true;
    for _ in 0..100 {
        stream.pump(now,&mut server,&mut driver).unwrap();
        now += 10;
        if driver.locked && driver.requests.last().is_some_and(|args| args[0] == "snapshot") { break; }
    }
    assert!(driver.locked);
    driver.epoch = Some(2);
    let mut joiner = Vec::new();
    for _ in 0..100 {
        let events = stream.pump(now,&mut server,&mut driver).unwrap();
        joiner.extend(lines(&events,2));
        now += 10;
        if joiner.iter().any(|line| line == "0|Ne|2") { break; }
    }
    let epoch = joiner.iter().position(|line| line == "0|Ne|2").expect("epoch was never announced");
    let history = joiner.iter().position(|line| line == "0|H|55|0").expect("joiner history was lost");
    assert!(history < epoch,"old deferred history followed the new epoch");
    assert!(joiner[..epoch].iter().any(|line| line.starts_with("0|L|")),"held live lines were lost");
    assert_eq!(driver.epoch,None);
    assert!(!driver.locked);
    assert!(!stream.joining());
}

#[test]
fn i4_joiner_waits_for_targets_and_rebinding_keeps_the_gather() {
    let mut server = Server::new(1);
    let mut stream = Stream::new(StreamOptions { base_interval_ms: 0.0, ..StreamOptions::default() });
    let mut driver = DeferredRecorder::default();
    add(&mut server, &mut stream, 1);
    for now in (0..300).step_by(10) { stream.pump(now, &mut server, &mut driver).unwrap(); }
    assert!(stream.joining(), "an empty target set consumed the joiner");
    server.leave(ConnId(1));
    server.connect(ConnId(2)); server.join(ConnId(2), 2);
    stream.rebound(ConnId(1), ConnId(2));
    driver.claims.push(55);
    let mut received = Vec::new();
    for now in (300..4000).step_by(10) {
        received.extend(lines(&stream.pump(now, &mut server, &mut driver).unwrap(), 2));
        if !stream.joining() && received.contains(&"0|H|55|0".into()) { break; }
    }
    assert!(received.contains(&"0|H|55|0".into()));
    assert!(!stream.joining());
    assert!(stream.history_trimmed.contains_key(&ConnId(2)));
    assert!(!stream.history_trimmed.contains_key(&ConnId(1)));
}

#[test]
fn i4_rebinding_an_inflight_gather_keeps_its_history_and_held_lines() {
    let (mut server, mut stream, mut driver, mut now) = bootstrapped();
    add(&mut server, &mut stream, 2);
    driver.emit = true;
    for _ in 0..100 {
        stream.pump(now, &mut server, &mut driver).unwrap();
        now += 10;
        if driver.locked && driver.requests.last().is_some_and(|args| args[0] == "snapshot") { break; }
    }
    assert!(driver.locked);
    server.leave(ConnId(2));
    server.connect(ConnId(3)); server.join(ConnId(3), 2);
    stream.rebound(ConnId(2), ConnId(3));
    let mut received = Vec::new();
    for _ in 0..100 {
        let events = stream.pump(now, &mut server, &mut driver).unwrap();
        assert!(!events.iter().any(|event| event.requester == Some(ConnId(2))));
        received.extend(lines(&events, 3));
        now += 10;
        if !stream.joining() { break; }
    }
    assert!(received.contains(&"0|H|55|0".into()));
    assert!(received.iter().any(|line| line.starts_with("0|L|")));
    assert!(stream.history_trimmed.contains_key(&ConnId(3)));
    assert!(!stream.history_trimmed.contains_key(&ConnId(2)));
}

#[test]
fn i5_history_then_held_keeps_duplicates_fresh_partition_and_one_recorder_gather() {
    let (mut server, mut stream, mut driver, mut now) = bootstrapped();
    add(&mut server, &mut stream, 2);
    driver.claims.push(66);
    driver.pages.insert(55, 17);
    driver.emit = true;
    driver.duplicate = true;
    let mut first = Vec::new();
    let mut second = Vec::new();
    let mut checked_live_pumps = 0;
    for _ in 0..1000 {
        let known = driver.locked && driver.selected == [55];
        let produced = driver.produced.len();
        let events = stream.pump(now, &mut server, &mut driver).unwrap();
        let live = lines(&events, 1);
        if known {
            assert_eq!(live, driver.produced[produced..], "an established player was held during a joiner's gather");
            checked_live_pumps += 1;
        }
        first.extend(live); second.extend(lines(&events, 2));
        now += 10;
        if !stream.joining() && second.contains(&"0|H|55|15".into()) { break; }
    }
    driver.emit = false;
    for _ in 0..100 {
        let events = stream.pump(now, &mut server, &mut driver).unwrap();
        first.extend(lines(&events, 1)); second.extend(lines(&events, 2)); now += 10;
    }
    let history: Vec<_> = (0..16).map(|page| format!("0|H|55|{page}")).collect();
    assert!(checked_live_pumps > 0);
    assert_eq!(&second[..16], history);
    assert_eq!(second.iter().filter(|line| *line == "0|H|55|0").count(), 2, "history/held overlap was deduplicated");
    assert_eq!(second.iter().filter(|line| *line == "0|H|66|0").count(), 1);
    assert_eq!(first.iter().filter(|line| line.starts_with("0|L|") || *line == "0|H|55|0")
        .cloned().collect::<Vec<_>>(), driver.produced, "an established player was held or lost a live line");
    let mut held = vec!["0|H|66|0".to_owned()];
    held.extend(driver.produced.clone());
    assert_eq!(&second[16..], held, "history was not followed by the complete held window");
    assert_eq!(stream.history_trimmed[&ConnId(2)], 1);
    assert_eq!(stream.history_trimmed[&ConnId(1)], 0);
    let selects: Vec<_> = driver.requests.iter().filter(|args| args[0] == "snapshot").map(|args| args[1].as_str()).collect();
    assert_eq!(selects, ["55", "66", "55"]);
}

#[test]
fn i5_gather_abandons_at_thirty_seconds_and_delivers_held_lines_alone() {
    let (mut server, mut stream, mut driver, mut now) = bootstrapped();
    add(&mut server, &mut stream, 2);
    driver.emit = true;
    driver.stall_history = true;
    let mut second = Vec::new();
    let started = now;
    for _ in 0..3300 {
        let events = stream.pump(now, &mut server, &mut driver).unwrap();
        second.extend(lines(&events, 2));
        now += 10;
        if stream.report.iter().any(|line| line.contains("join_history_abandoned")) { break; }
    }
    assert!(now - started <= 30_300, "the thirty-second abandon limit was missed");
    assert!(stream.report.iter().any(|line| line.contains("join_history_abandoned")));
    assert!(!second.is_empty(), "held lines were discarded on abandonment");
    assert!(second.iter().all(|line| line.starts_with("0|L|")), "partial history escaped on abandonment");
    assert!(!stream.joining());
}

#[test]
fn i6_held_events_take_consecutive_sequence_numbers_when_delivered() {
    let server = RunningServer::start_configured(TcpListener::bind("127.0.0.1:0").unwrap(),
        Some(TcpListener::bind("127.0.0.1:0").unwrap()),
        || Ok(DeferredRecorder { claims: vec![55], emit: true, ..DeferredRecorder::default() }), 8,
        StreamOptions { base_interval_ms: 0.0, ..StreamOptions::default() }).unwrap();
    let mut first = connect(server.local_addr());
    exchange(&mut first, "HELLO|1|ring=1,wfs=1");
    assert!(exchange(&mut first, "CMD|1|JoinSession|1|1|61|~").starts_with("ACK"));
    while receive(&mut first).kind != "SimAnimation" {}
    let mut admin = connect(server.admin_addr().unwrap());
    assert!(exchange(&mut admin, "FRONT_DOOR_STATE").contains("presentation_sent_gap_max_during_join=1|"));
    let mut second = connect(server.local_addr());
    exchange(&mut second, "HELLO|1|ring=1,wfs=1");
    assert!(exchange(&mut second, "CMD|1|JoinSession|1|2|62|~").starts_with("ACK"));
    let mut previous = None;
    for _ in 0..6 {
        let event = receive(&mut second);
        if let Some(previous) = previous { assert_eq!(event.seq, previous + 1); }
        previous = Some(event.seq);
    }
    assert!(exchange(&mut admin, "FRONT_DOOR_STATE").contains("presentation_sent_gap_max_during_join=1|"));
    server.shutdown().unwrap();
}
