#![forbid(unsafe_code)]

use std::collections::{HashSet, VecDeque};
use std::io::{BufRead, BufReader, Write};
use std::net::{SocketAddr, TcpListener, TcpStream};
use std::sync::mpsc;
use std::time::Duration;
use simlink_bridge::verbs::BridgeCommand;
use simlink_headless_server::{ConnId, DriverError, GameDriver};
use simlink_headless_server::bridge_driver::{BridgeEndpoint, BridgeGameDriver};
use simlink_headless_server::messages::{dec_list, dec_record, hex_decode, hex_encode, Command, Event};
use simlink_headless_server::{serve::RunningServer, wire_v1 as w};

fn snapshot(tick: u64, speed: u32) -> Vec<u8> {
    fn string(out: &mut Vec<u8>, value: &str) {
        out.extend((value.len() as u32).to_le_bytes()); out.extend(value.as_bytes());
    }
    let mut out = Vec::new();
    out.extend(0x32505353u32.to_le_bytes()); out.extend(2u32.to_le_bytes()); out.extend(tick.to_le_bytes());
    string(&mut out, "Fixture World"); string(&mut out, "Fixture Save");
    out.extend(speed.to_le_bytes()); out.extend(1u32.to_le_bytes());
    out.extend(55u64.to_le_bytes()); string(&mut out, "Fixture Sim"); out.extend(18u64.to_le_bytes());
    for value in [1.0f32, 2.0, 3.0] { out.extend(value.to_le_bytes()); }
    out.extend(0u64.to_le_bytes()); string(&mut out, ""); out.extend(u32::MAX.to_le_bytes());
    out.extend(44u64.to_le_bytes()); out.extend(0u32.to_le_bytes());
    out.extend(0u64.to_le_bytes()); string(&mut out, ""); out.extend(66u64.to_le_bytes());
    out.extend(0u32.to_le_bytes());
    out
}

#[derive(Default)]
struct Bridge {
    commands: Vec<BridgeCommand>,
    events: VecDeque<Vec<u8>>,
    snapshot: Option<Vec<u8>>,
    tick: u64,
    reject_save: bool,
    speeds: VecDeque<u32>,
    gates: HashSet<u64>,
    lose_gate_reply: bool,
    cleared: Option<mpsc::Sender<u64>>,
    fail_snapshot_at: Option<u64>,
}
impl BridgeEndpoint for Bridge {
    fn command(&mut self, command: &BridgeCommand) -> Result<Vec<String>, DriverError> {
        self.commands.push(command.clone());
        let encoded = simlink_bridge::verbs::encode(command).unwrap();
        let corr = encoded.split('|').nth(1).unwrap();
        match command {
            BridgeCommand::GateAutonomy { sim, .. } => {
                self.gates.insert(*sim);
                if self.lose_gate_reply { return Err(DriverError("gate reply lost".into())); }
                Ok(vec![format!("AUTONOMY_PROBE|{corr}|ok|{}", hex_encode("GateRegistered"))])
            }
            BridgeCommand::UngateAutonomy { sim, .. } => {
                self.gates.remove(sim);
                if let Some(sender) = &self.cleared { sender.send(*sim).unwrap(); }
                Ok(vec![format!("AUTONOMY_PROBE|{corr}|ok|{}", hex_encode("GateCleared"))])
            }
            BridgeCommand::RequestSnapshot { .. } => {
                self.tick += 1;
                if self.fail_snapshot_at == Some(self.tick) { return Err(DriverError("snapshot unavailable".into())); }
                self.snapshot = Some(snapshot(self.tick, self.speeds.pop_front().unwrap_or(1)));
                Ok(vec![format!("SNAPSHOT2|{corr}")])
            }
            BridgeCommand::QueueInteraction { interaction, .. } => {
                self.events.push_back(format!("EVENT|InteractionQueued|{}|900|{}||1|1|2|3",
                    interaction.actor, hex_encode(&interaction.key)).into_bytes());
                self.events.push_back(b"EVENT|DialogRequest|7|55|4d657373616765|1|50726f6d7074".to_vec());
                self.events.push_back(format!("EVENT|PickFailed|{corr}|55|900|{}", hex_encode("NotRun | tooltip: server tooltip")).into_bytes());
                Ok(vec![format!("OK|{corr}")])
            }
            BridgeCommand::SaveGame { .. } if self.reject_save => Ok(vec![format!("ERROR|{corr}|{}", hex_encode("save rejected"))]),
            BridgeCommand::SacsRecorder { args, .. } => {
                let report = match args[0].as_str() {
                    "ring-on" => "enabled=1", "dump" => "seq=20;more=0", "snapshot" => "drivers=", _ => "",
                };
                Ok(vec![format!("AUTONOMY_PROBE|{corr}|ok|{}", hex_encode(report))])
            }
            _ => Ok(vec![format!("OK|{corr}")]),
        }
    }
    fn events(&mut self) -> Result<Vec<Vec<u8>>, DriverError> { Ok(self.events.drain(..).collect()) }
    fn take_snapshot(&mut self) -> Option<Vec<u8>> { self.snapshot.take() }
}
fn command(corr: u64, verb: &str, args: &[&str]) -> Command {
    Command { corr, command_id: corr, player_id: 77, verb: verb.into(), args: args.iter().map(|s| (*s).into()).collect() }
}

struct Client { reader: BufReader<TcpStream>, last_seq: u64 }
impl Client {
    fn connect(address: SocketAddr) -> Self {
        let socket = TcpStream::connect(address).unwrap();
        socket.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
        let mut client = Self { reader: BufReader::new(socket), last_seq: 0 };
        client.send("HELLO|1|ring=1,wfs=1");
        assert!(client.line().starts_with("WELCOME|1|"));
        client
    }
    fn send(&mut self, line: &str) { writeln!(self.reader.get_mut(), "{line}").unwrap(); }
    fn line(&mut self) -> String {
        let mut line = String::new();
        assert_ne!(self.reader.read_line(&mut line).unwrap(), 0);
        line.trim_end_matches(['\r', '\n']).into()
    }
    fn event(&mut self, line: &str) -> Event {
        let fields = w::split(line.as_bytes()).unwrap();
        let mut event = w::decode_event(&fields).unwrap();
        assert!(event.seq > self.last_seq);
        self.last_seq = event.seq;
        if matches!(event.kind.as_str(), "FullSnapshot" | "SnapshotDelta") {
            let id = simlink_headless_server::messages::dec_bulk_ref(&event.fields[0]).expect("bulk introducer precedes chunks");
            let mut bytes = Vec::new();
            for index in 0.. {
                let line = self.line();
                let chunk = w::decode_bulk(&w::split(line.as_bytes()).unwrap()).unwrap();
                assert_eq!((chunk.bulk_id, chunk.index), (id, index));
                bytes.extend(w::b64_decode(&chunk.b64).unwrap());
                if index + 1 == chunk.count { break; }
            }
            event.fields[0] = String::from_utf8(bytes).unwrap();
            assert!(event.fields[0].contains(&hex_encode("Fixture Sim")));
        }
        event
    }
    fn ack(&mut self, corr: u64) {
        loop {
            let line = self.line();
            if line.starts_with(&format!("ACK|{corr}|")) { break; }
            assert!(line.starts_with("EV|"), "{line}");
            self.event(&line);
        }
    }
    fn until(&mut self, kind: &str, corr: u64) -> Event {
        loop {
            let line = self.line();
            let event = self.event(&line);
            if event.kind == kind && event.corr == corr { return event; }
        }
    }
    fn causal(&mut self) -> Event {
        loop {
            let line = self.line();
            let event = self.event(&line);
            if !matches!(event.kind.as_str(), "SnapshotDelta" | "SimAnimation" | "WorldTimeChanged") { return event; }
        }
    }
    fn join(&mut self, corr: u64, player: i32) -> Event {
        self.send(&format!("CMD|{corr}|JoinSession|{corr}|{player}|66697874757265|~"));
        self.ack(corr);
        self.until("SessionJoined", corr);
        self.until("GameSpeedChanged", corr);
        let snapshot = self.until("FullSnapshot", corr);
        self.until("LotLocalAuthorityModeChanged", corr);
        snapshot
    }
}
fn snapshot_owner(event: &Event) -> String {
    let snapshot = dec_record(&event.fields[0]).unwrap();
    let sims = dec_list(&snapshot[4]).unwrap();
    dec_record(&sims[0]).unwrap()[5].clone()
}
fn test_server(bridge: Bridge) -> RunningServer {
    RunningServer::start_configured(TcpListener::bind("127.0.0.1:0").unwrap(),
        Some(TcpListener::bind("127.0.0.1:0").unwrap()), move || Ok(BridgeGameDriver::new(bridge)), 8,
        simlink_headless_server::serve::StreamOptions::default()).unwrap()
}

#[test]
fn fresh_join_cleans_retained_claims_before_decorating_its_baseline() {
    let mut driver = BridgeGameDriver::new(Bridge::default());
    driver.adjudicate_for(ConnId(1), &command(1, "ClaimSim", &["55"])).unwrap();
    driver.poll_routed_events().unwrap();
    driver.adjudicate_for(ConnId(2), &command(2, "JoinSession", &["66697874757265", "~"])).unwrap();
    let events = driver.poll_routed_events().unwrap();
    assert_eq!(events.iter().map(|event| event.event.kind.as_str()).collect::<Vec<_>>(),
        ["SimReleased", "SimAutonomyChanged", "SessionJoined", "GameSpeedChanged", "FullSnapshot", "LotLocalAuthorityModeChanged"]);
    assert!(events[..2].iter().all(|event| event.requester.is_none()));
    assert_eq!(snapshot_owner(&events[4].event), "~");
    assert!(driver.endpoint().gates.is_empty());
}

#[test]
fn failed_fresh_join_does_not_release_the_existing_claim_or_gate() {
    let mut driver = BridgeGameDriver::new(Bridge { fail_snapshot_at: Some(2), ..Bridge::default() });
    driver.adjudicate_for(ConnId(1), &command(1, "JoinSession", &["66697874757265", "~"])).unwrap();
    driver.adjudicate_for(ConnId(1), &command(2, "ClaimSim", &["55"])).unwrap();
    driver.poll_routed_events().unwrap();
    assert!(driver.adjudicate_for(ConnId(2), &command(3, "JoinSession", &["66697874757265", "~"])).is_err());
    assert_eq!(driver.owner(55), Some(77));
    assert_eq!(driver.endpoint().gates, HashSet::from([55]));
    assert!(driver.poll_routed_events().unwrap().is_empty());
}

#[test]
fn socket_supersede_releases_before_join_and_has_no_stale_snapshot_owner() {
    let (cleared, receiver) = mpsc::channel();
    let server = test_server(Bridge { cleared: Some(cleared), ..Bridge::default() });
    let mut first = Client::connect(server.local_addr());
    first.join(1, 77);
    first.send("CMD|2|ClaimSim|2|77|55"); first.ack(2); first.until("SimAutonomyChanged", 2);
    let mut replacement = Client::connect(server.local_addr());
    replacement.send("CMD|1|JoinSession|1|77|66697874757265|~"); replacement.ack(1);
    let released = replacement.causal();
    assert_eq!((released.kind.as_str(), released.fields.as_slice()), ("SimReleased", &["77".into(), "55".into()][..]));
    let autonomy = replacement.causal();
    assert_eq!((autonomy.kind.as_str(), autonomy.fields.as_slice()), ("SimAutonomyChanged", &["55".into(), "0".into()][..]));
    assert_eq!(replacement.causal().kind, "SessionJoined");
    assert_eq!(replacement.causal().kind, "GameSpeedChanged");
    let snapshot = replacement.causal();
    assert_eq!(snapshot.kind, "FullSnapshot");
    assert_eq!(snapshot_owner(&snapshot), "~");
    assert_eq!(replacement.causal().kind, "LotLocalAuthorityModeChanged");
    assert_eq!(receiver.recv_timeout(Duration::from_secs(10)).unwrap(), 55);
    server.shutdown().unwrap();
}

#[test]
fn socket_disconnect_ungates_and_broadcasts_before_cold_rejoin() {
    let (cleared, receiver) = mpsc::channel();
    let server = test_server(Bridge { cleared: Some(cleared), ..Bridge::default() });
    let mut first = Client::connect(server.local_addr());
    first.join(1, 77);
    first.send("CMD|2|ClaimSim|2|77|55"); first.ack(2); first.until("SimAutonomyChanged", 2);
    let mut observer = Client::connect(server.local_addr());
    assert_eq!(snapshot_owner(&observer.join(1, 88)), "77");
    drop(first);
    assert_eq!(receiver.recv_timeout(Duration::from_secs(10)).unwrap(), 55);
    assert_eq!(observer.until("SimReleased", 0).fields, ["77", "55"]);
    let autonomy = observer.causal();
    assert_eq!((autonomy.kind.as_str(), autonomy.fields.as_slice()), ("SimAutonomyChanged", &["55".into(), "0".into()][..]));
    let mut replacement = Client::connect(server.local_addr());
    assert_eq!(snapshot_owner(&replacement.join(1, 77)), "~");
    server.shutdown().unwrap();
}

#[test]
fn socket_admin_cannot_join_or_receive_its_release_broadcasts() {
    let server = test_server(Bridge::default());
    let mut public = Client::connect(server.local_addr());
    public.join(1, 77);
    public.send("CMD|2|ClaimSim|2|77|55"); public.ack(2); public.until("SimAutonomyChanged", 2);
    let mut admin = Client::connect(server.admin_addr().unwrap());
    admin.send("CMD|1|JoinSession|1|77|66697874757265|~");
    assert_eq!(admin.line(), "ERR|1|UnauthorizedClientInsertion|JoinSession|");
    public.send("CMD|3|RequestSnapshot|3|77|~"); public.ack(3);
    assert_eq!(snapshot_owner(&public.until("FullSnapshot", 3)), "77");
    admin.send("CMD|2|ReleaseSim|2|77|55");
    assert_eq!(admin.line(), "ACK|2|ReleaseSim");
    assert_eq!(public.until("SimReleased", 2).fields, ["77", "55"]);
    assert_eq!(public.until("SimAutonomyChanged", 2).fields, ["55", "0"]);
    admin.send("CMD|3|AckSnapshot|3|77|1");
    assert_eq!(admin.line(), "ACK|3|AckSnapshot");
    server.shutdown().unwrap();
}

#[test]
fn leave_clears_each_native_gate_and_emits_ordered_release_pairs_once() {
    let mut driver = BridgeGameDriver::new(Bridge::default());
    let id = ConnId(1);
    driver.adjudicate_for(id, &command(1, "JoinSession", &["66697874757265", "~"])).unwrap();
    for (corr, sim) in [(2, "55"), (3, "9007199254740993")] {
        driver.adjudicate_for(id, &command(corr, "ClaimSim", &[sim])).unwrap();
    }
    driver.poll_routed_events().unwrap();
    assert_eq!(driver.endpoint().gates.len(), 2);
    assert!(driver.adjudicate_for(id, &command(4, "LeaveSession", &[])).unwrap().starts_with(b"ACK|"));
    assert!(driver.endpoint().gates.is_empty());
    assert!(driver.claimed_sims().is_empty());
    let events = driver.poll_routed_events().unwrap();
    assert_eq!(events.len(), 4);
    let mut released = Vec::new();
    for pair in events.as_chunks::<2>().0 {
        assert_eq!(pair[0].event.kind, "SimReleased");
        assert_eq!(pair[0].event.fields[0], "77");
        assert_eq!(pair[1].event.kind, "SimAutonomyChanged");
        assert_eq!(pair[1].event.fields, [pair[0].event.fields[1].clone(), "0".into()]);
        assert!(pair.iter().all(|event| event.event.corr == 4 && event.requester == Some(id)));
        released.push(pair[0].event.fields[1].parse::<u64>().unwrap());
    }
    released.sort();
    assert_eq!(released, [55, 9007199254740993]);
    let mut clears: Vec<_> = driver.endpoint().commands.iter().filter_map(|command| match command {
        BridgeCommand::UngateAutonomy { sim, reset: false, .. } => Some(*sim), _ => None,
    }).collect();
    clears.sort();
    assert_eq!(clears, released);
    driver.release_player(77);
    assert!(driver.poll_routed_events().unwrap().is_empty());
    assert_eq!(driver.endpoint().commands.iter().filter(|command| matches!(command, BridgeCommand::UngateAutonomy { .. })).count(), 2);
}

#[test]
fn release_sim_clears_only_its_gate_and_publishes_both_events() {
    let mut driver = BridgeGameDriver::new(Bridge::default());
    let id = ConnId(1);
    driver.adjudicate_for(id, &command(1, "ClaimSim", &["55"])).unwrap();
    driver.adjudicate_for(id, &command(2, "ClaimSim", &["66"])).unwrap();
    driver.poll_routed_events().unwrap();
    driver.adjudicate_for(id, &command(3, "ReleaseSim", &["55"])).unwrap();
    assert_eq!(driver.endpoint().gates, HashSet::from([66]));
    assert_eq!(driver.owner(55), None);
    assert_eq!(driver.owner(66), Some(77));
    let events = driver.poll_routed_events().unwrap();
    assert_eq!(events.iter().map(|event| event.event.kind.as_str()).collect::<Vec<_>>(), ["SimReleased", "SimAutonomyChanged"]);
    assert_eq!(events[0].event.fields, ["77", "55"]);
    assert_eq!(events[1].event.fields, ["55", "0"]);
    assert!(events.iter().all(|event| event.event.corr == 3));
    driver.adjudicate_for(id, &command(4, "ClaimSim", &["55"])).unwrap();
    driver.poll_routed_events().unwrap();
    driver.adjudicate_for(id, &command(5, "ReleaseSim", &["55"])).unwrap();
    assert_eq!(driver.endpoint().commands.iter().filter(|command| matches!(command, BridgeCommand::UngateAutonomy { sim: 55, reset: false, .. })).count(), 2);
}

#[test]
fn lease_cleanup_uses_the_same_gate_clear_and_release_notifications() {
    let mut driver = BridgeGameDriver::new(Bridge::default());
    driver.adjudicate_for(ConnId(1), &command(1, "ClaimSim", &["55"])).unwrap();
    driver.poll_routed_events().unwrap();
    driver.release_player(77);
    assert!(driver.endpoint().gates.is_empty());
    let events = driver.poll_routed_events().unwrap();
    assert_eq!(events.iter().map(|event| event.event.kind.as_str()).collect::<Vec<_>>(), ["SimReleased", "SimAutonomyChanged"]);
    assert!(events.iter().all(|event| event.event.corr == 0 && event.requester.is_none()));
}

#[test]
fn release_clears_a_native_gate_even_when_its_registration_reply_was_lost() {
    let mut driver = BridgeGameDriver::new(Bridge { lose_gate_reply: true, ..Bridge::default() });
    driver.adjudicate_for(ConnId(1), &command(1, "ClaimSim", &["55"])).unwrap();
    driver.poll_routed_events().unwrap();
    assert_eq!(driver.endpoint().gates, HashSet::from([55]));
    assert!(driver.adjudicate_for(ConnId(1), &command(2, "LeaveSession", &[])).unwrap().starts_with(b"ACK|"));
    assert!(driver.endpoint().gates.is_empty());
}

#[test]
fn join_and_rejoin_replay_authoritative_speed_then_private_baseline_and_mode() {
    let mut driver = BridgeGameDriver::new(Bridge { speeds: VecDeque::from([0, 0, 4, 4, 3, 3, 2, 2, 1, 1]), ..Bridge::default() });
    let id = ConnId(1);
    for (index, speed) in ["Pause", "Ultra", "Speed3", "Speed2", "Normal"].into_iter().enumerate() {
        let corr = index as u64 * 3 + 1;
        driver.adjudicate_for(id, &command(corr, "JoinSession", &["66697874757265", "~"])).unwrap();
        let events = driver.poll_routed_events().unwrap();
        assert_eq!(events.iter().map(|event| event.event.kind.as_str()).collect::<Vec<_>>(),
            ["SessionJoined", "GameSpeedChanged", "FullSnapshot", "LotLocalAuthorityModeChanged"]);
        assert_eq!(events[1].event.fields, [speed]);
        assert_eq!(events[1].requester, None);
        assert_eq!(events[2].requester, Some(id));
        assert_eq!(events[3].event.fields, ["77", "FullPuppetFallback"]);
        assert_eq!(events[3].requester, Some(id));
        assert!(events.iter().all(|event| event.event.corr == corr));
        driver.adjudicate_for(id, &command(corr + 1, "RequestSnapshot", &["~"])).unwrap();
        // RequestSnapshot is a new baseline, not a repeated mode transition.
        assert_eq!(driver.poll_routed_events().unwrap().iter().map(|event| event.event.kind.as_str()).collect::<Vec<_>>(),
            ["FullSnapshot"]);
        driver.adjudicate_for(id, &command(corr + 2, "LeaveSession", &[])).unwrap();
        driver.poll_routed_events().unwrap();
    }
}

#[test]
fn bridge_backed_session_emits_join_snapshot_claim_queue_dialog_checkpoint_and_rejoin() {
    let mut driver = BridgeGameDriver::new(Bridge::default());
    let id = ConnId(1);
    let join = command(1, "JoinSession", &["66697874757265", "~"]);
    assert!(driver.adjudicate_for(id, &join).unwrap().starts_with(b"ACK|"));
    let events = driver.poll_routed_events().unwrap();
    assert!(events.iter().any(|e| e.event.kind == "SessionJoined"));
    assert!(events.iter().any(|e| e.event.kind == "FullSnapshot" && e.requester == Some(id)));
    assert!(driver.endpoint().commands.iter().any(|c| matches!(c, BridgeCommand::RequestSnapshot { since_tick: None, .. })));
    assert!(driver.adjudicate_for(id, &command(2, "RequestSnapshot", &["~"])).unwrap().starts_with(b"ACK|"));
    assert_eq!(driver.poll_routed_events().unwrap()[0].event.kind, "FullSnapshot");
    assert!(driver.adjudicate_for(id, &command(3, "ClaimSim", &["55"])).unwrap().starts_with(b"ACK|"));
    let events = driver.poll_routed_events().unwrap();
    let claim = events.iter().find(|e| e.event.kind == "SimClaimed").unwrap();
    assert_eq!(claim.event.fields, ["77", "55", "44"]);
    assert!(driver.endpoint().commands.iter().any(|c| matches!(c, BridgeCommand::GateAutonomy { sim: 55, .. })));
    let request = format!("(55;{};~;(1;2;3);[])", hex_encode("Terrain.GoHere"));
    assert!(driver.adjudicate_for(id, &command(4, "QueueInteraction", &[&request])).unwrap().starts_with(b"ACK|"));
    let events = driver.poll_routed_events().unwrap();
    assert!(events.iter().any(|e| e.event.kind == "InteractionQueued"));
    assert!(events.iter().any(|e| e.event.kind == "DialogRequested" && e.owner == Some(77)));
    let failed = events.iter().find(|e| e.event.kind == "PickFailed").unwrap();
    assert_eq!((failed.requester, failed.event.corr), (Some(id), 4));
    assert_eq!(hex_decode(&failed.event.fields[1]).unwrap(), "server tooltip");
    assert!(driver.adjudicate_for(id, &command(5, "AnswerDialog", &["7", "55", "796573"])).unwrap().starts_with(b"ACK|"));
    assert!(driver.endpoint().commands.iter().any(|c| matches!(c, BridgeCommand::AnswerDialog { request_id: 7, .. })));
    assert!(driver.adjudicate_for(id, &command(6, "SaveGame", &["736372617463682e73696d7333"])).unwrap().starts_with(b"ACK|"));
    let events = driver.poll_routed_events().unwrap();
    let saved = events.iter().find(|e| e.event.kind == "SaveCheckpointed").unwrap();
    assert_eq!(hex_decode(&saved.event.fields[0]).unwrap(), "scratch.sims3");
    assert!(hex_decode(&saved.event.fields[1]).unwrap().ends_with("+00:00"));
    driver.adjudicate_for(id, &command(7, "LeaveSession", &[])).unwrap();
    assert!(driver.claimed_sims().is_empty());
    driver.poll_routed_events().unwrap();
    driver.adjudicate_for(id, &command(8, "JoinSession", &["66697874757265", "~"])).unwrap();
    let events = driver.poll_routed_events().unwrap();
    assert!(events.iter().any(|e| e.event.kind == "SessionJoined"));
    assert!(events.iter().any(|e| e.event.kind == "FullSnapshot"));
}

#[test]
fn rejected_save_never_emits_checkpoint_and_requested_delta_keeps_its_kind() {
    let mut driver = BridgeGameDriver::new(Bridge { reject_save: true, ..Bridge::default() });
    let id = ConnId(1);
    assert!(driver.adjudicate_for(id, &command(1, "SaveGame", &["73637261746368"])).unwrap().starts_with(b"ERR|"));
    assert!(driver.poll_routed_events().unwrap().is_empty());
    driver.adjudicate_for(id, &command(2, "RequestSnapshot", &["1"])).unwrap();
    assert_eq!(driver.poll_routed_events().unwrap()[0].event.kind, "SnapshotDelta");
}

#[test]
fn gate5_sequence_crosses_the_real_socket_actor_and_snapshot_bulk_codec() {
    use std::io::{BufRead, BufReader, Write};
    use std::net::{TcpListener, TcpStream};
    use std::time::Duration;
    use simlink_headless_server::{messages::Event, serve::RunningServer, wire_v1 as w};
    struct Client { reader: BufReader<TcpStream>, last_seq: u64 }
    impl Client {
        fn send(&mut self, line: &str) { writeln!(self.reader.get_mut(), "{line}").unwrap(); }
        fn line(&mut self) -> String {
            let mut line = String::new();
            assert_ne!(self.reader.read_line(&mut line).unwrap(), 0);
            line.trim_end_matches(['\r', '\n']).into()
        }
        fn event(&mut self, line: &str) -> Event {
            let fields = w::split(line.as_bytes()).unwrap();
            let mut event = w::decode_event(&fields).unwrap();
            assert!(event.seq > self.last_seq);
            self.last_seq = event.seq;
            if matches!(event.kind.as_str(), "FullSnapshot" | "SnapshotDelta") {
                let id = simlink_headless_server::messages::dec_bulk_ref(&event.fields[0]).expect("bulk introducer precedes chunks");
                let mut bytes = Vec::new();
                for index in 0.. {
                    let line = self.line();
                    let chunk = w::decode_bulk(&w::split(line.as_bytes()).unwrap()).unwrap();
                    assert_eq!((chunk.bulk_id, chunk.index), (id, index));
                    bytes.extend(w::b64_decode(&chunk.b64).unwrap());
                    if index + 1 == chunk.count { break; }
                }
                event.fields[0] = String::from_utf8(bytes).unwrap();
                assert!(event.fields[0].contains(&hex_encode("Fixture Sim")));
            }
            event
        }
        fn ack(&mut self, corr: u64) {
            loop {
                let line = self.line();
                if line.starts_with(&format!("ACK|{corr}|")) { break; }
                assert!(line.starts_with("EV|"), "{line}");
                self.event(&line);
            }
        }
        fn until(&mut self, kind: &str, corr: u64) -> Event {
            loop {
                let line = self.line();
                let event = self.event(&line);
                if event.kind == kind && event.corr == corr { return event; }
            }
        }
    }
    let server = RunningServer::start(TcpListener::bind("127.0.0.1:0").unwrap(), BridgeGameDriver::new(Bridge::default()), 8).unwrap();
    let socket = TcpStream::connect(server.local_addr()).unwrap();
    socket.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
    let mut client = Client { reader: BufReader::new(socket), last_seq: 0 };
    client.send("HELLO|1|ring=1,wfs=1");
    assert!(client.line().starts_with("WELCOME|1|"));
    client.send("CMD|1|JoinSession|1|77|66697874757265|~"); client.ack(1);
    client.until("SessionJoined", 1);
    let line = client.line();
    let speed = client.event(&line);
    assert_eq!((speed.kind.as_str(), speed.fields.as_slice()), ("GameSpeedChanged", &["Normal".into()][..]));
    client.until("FullSnapshot", 1); client.until("LotLocalAuthorityModeChanged", 1);
    client.send("CMD|2|RequestSnapshot|2|77|~"); client.ack(2); client.until("FullSnapshot", 2);
    client.send("CMD|3|ClaimSim|3|77|55"); client.ack(3); client.until("SimClaimed", 3);
    client.send(&format!("CMD|4|QueueInteraction|4|77|(55;{};~;(1;2;3);[])", hex_encode("Terrain.GoHere")));
    client.ack(4); client.until("InteractionQueued", 0); client.until("DialogRequested", 0); client.until("PickFailed", 4);
    client.send("CMD|5|AnswerDialog|5|77|7|55|796573"); client.ack(5);
    client.send("CMD|6|SaveGame|6|77|736372617463682e73696d7333"); client.ack(6); client.until("SaveCheckpointed", 6);
    client.send("CMD|7|LeaveSession|7|77"); client.ack(7);
    assert_eq!(client.until("SimReleased", 7).fields, ["77", "55"]);
    let line = client.line();
    let autonomy = client.event(&line);
    assert_eq!(autonomy.kind, "SimAutonomyChanged");
    assert_eq!(autonomy.fields, ["55", "0"]);
    let before_rejoin = client.last_seq;
    client.send("CMD|8|JoinSession|8|77|66697874757265|~"); client.ack(8);
    client.until("SessionJoined", 8);
    let line = client.line();
    let speed = client.event(&line);
    assert_eq!((speed.kind.as_str(), speed.fields.as_slice()), ("GameSpeedChanged", &["Normal".into()][..]));
    client.until("FullSnapshot", 8); client.until("LotLocalAuthorityModeChanged", 8);
    assert!(client.last_seq > before_rejoin);
    server.shutdown().unwrap();
}
