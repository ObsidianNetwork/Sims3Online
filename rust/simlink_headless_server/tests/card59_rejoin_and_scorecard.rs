#![forbid(unsafe_code)]
use simlink_headless_server::{rejoin::*, card59_scorecard, messages::Command, ConnId, Server, GameDriver};
use serde_json::json;

fn event(seq: u64) -> String { format!("EV|{seq}|10|0|SimStateChanged|55") }
fn ring(disconnect: u64) -> ClientEventRing {
    let mut ring = ClientEventRing::new(7);
    for seq in 1..=disconnect { ring.append(7, seq, event(seq), false); }
    ring.mark_disconnected(disconnect, 100);
    ring
}
fn command(corr: u64, player: i32, verb: &str, args: &[&str]) -> Command {
    Command { corr, command_id: corr, player_id: player, verb: verb.into(), args: args.iter().map(|s| (*s).into()).collect() }
}

#[test]
fn cap_grammar_keeps_epoch_u32_and_cursor_u64_without_signs_or_extra_fields() {
    assert_eq!(parse_resume_cap("4294967295.18446744073709551615"), Some((u32::MAX, u64::MAX)));
    for bad in ["", ".1", "1.", "+1.2", "1.-2", "1.2.3", "4294967296.1", "1.18446744073709551616", "1. 2", " 1.2"] {
        assert_eq!(parse_resume_cap(bad), None, "{bad}");
    }
}

#[test]
fn warm_replay_is_the_original_v1_gap_and_reconnect_cancels_the_disconnect_clock() {
    let mut history = ring(3);
    history.append(7, 4, event(4), false);
    history.append(7, 6, event(6), false); // seq 5 belonged to a command ACK, not an EV.
    let outcome = history.try_resume(7, 7, 3, 100 + RESUME_WINDOW_MAX_MS - 1);
    assert_eq!(outcome.header_line(), "RESUME_OK|7|4|2");
    assert_eq!(outcome, ResumeOutcome::Ok { epoch: 7, from_seq: 4, replayed_lines: vec![event(4), event(6)] });
    assert!(!history.disconnected());
    assert_eq!(history.check_lease_expired(7, u64::MAX), None);
}

#[test]
fn all_received_cursor_has_an_empty_replay_but_still_expires_in_a_paused_world() {
    let mut history = ring(2);
    assert!(matches!(history.resume_outcome(7, 7, 2, 120_099), ResumeOutcome::Ok { replayed_lines, .. } if replayed_lines.is_empty()));
    assert_eq!(history.check_lease_expired(7, 120_100), Some(LeaseEndReason::TimeoutExpired));
    assert_eq!(history.try_resume(7, 7, 2, 120_100), ResumeOutcome::Fresh { epoch: 7, reason: LeaseEndReason::TimeoutExpired });
}

#[test]
fn eviction_before_s_plus_one_keeps_the_lease_and_eviction_of_the_gap_ends_it() {
    let mut history = ring(2048);
    for seq in 2049..=4096 { history.append(7, seq, event(seq), false); }
    assert_eq!(history.len(), RING_BUFFER_FRAMES);
    assert_eq!(history.check_lease_expired(7, 101), None);
    history.append(7, 4097, event(4097), false);
    assert_eq!(history.check_lease_expired(7, 101), Some(LeaseEndReason::RingLineEvicted));
}

#[test]
fn byte_bound_closes_the_lease_without_reaching_the_line_bound() {
    let mut history = ring(1);
    for seq in 2..=19 {
        history.append(7, seq, "x".repeat(simlink_headless_server::messages::PUBLIC_LINE_MAX_BYTES), false);
        assert!(history.total_bytes() <= CLIENT_EVENT_RING_MAX_BYTES);
        assert!(history.len() <= RING_BUFFER_FRAMES);
    }
    assert_eq!(history.check_lease_expired(7, 101), Some(LeaseEndReason::RingByteEvicted));
}

#[test]
fn a_client_missing_a_bulk_goes_fresh_before_the_all_received_lease_ends() {
    let mut history = ClientEventRing::new(7);
    history.append(7, 1, "EV|1|10|0|WorldFile|00|0|1|00|0|b8".into(), true);
    history.append(7, 2, event(2), false);
    history.mark_disconnected(2, 100);
    assert_eq!(history.resume_outcome(7, 7, 0, 101),
        ResumeOutcome::Fresh { epoch: 7, reason: LeaseEndReason::BulkInGap });
    assert_eq!(history.check_lease_expired(7, 101), None);
    history.append(7, 3, "EV|3|10|0|FullSnapshot|b9".into(), true);
    assert_eq!(history.check_lease_expired(7, 101), Some(LeaseEndReason::BulkInGap));
}

#[test]
fn wrong_epoch_future_cursor_leave_and_supersede_cannot_resume() {
    assert_eq!(ring(1).resume_outcome(8, 7, 1, 101).header_line(), "RESUME_FRESH|8");
    assert!(matches!(ring(1).resume_outcome(7, 7, 2, 101), ResumeOutcome::Fresh { .. }));
    for reason in [LeaseEndReason::Superseded, LeaseEndReason::ExplicitLeave] {
        let mut history = ring(1);
        history.end(reason);
        assert_eq!(history.len(), 0);
        assert_eq!(history.total_bytes(), 0);
        assert_eq!(history.check_lease_expired(7, 101), Some(reason));
    }
}

#[test]
fn resume_rebinds_sequence_cadence_and_command_window_without_rejoining_or_releasing_claims() {
    let mut server = Server::new(7);
    let mut driver = simlink_headless_server::BridgeGameDriver::new(simlink_headless_server::bridge_driver::OfflineBridge);
    server.connect(ConnId(1)); server.join(ConnId(1), 7);
    let cmd = command(1, 7, "ClaimSim", &["55"]);
    let outcome = server.handle_command(ConnId(1), &cmd, |cmd| driver.adjudicate(cmd).unwrap()).unwrap();
    let conn = server.connection_mut(ConnId(1)).unwrap();
    conn.ring = ring(1);
    conn.cadence.ack(10);
    server.connect(ConnId(2));
    // The same identified transition HELLO reaches only after token possession.
    assert!(matches!(server.resume_identified_session(ConnId(1), ConnId(2), 7, 1, 101), ResumeOutcome::Ok { .. }));
    assert!(server.connection(ConnId(1)).is_none());
    assert_eq!(server.active_connection(7), Some(ConnId(2)));
    assert_eq!(server.connection(ConnId(2)).unwrap().event_id, ConnId(1));
    let pending = simlink_headless_server::routing::RoutedEvent {
        event: simlink_headless_server::messages::Event { seq: 0, tick: 10, corr: 1,
            kind: "PickFailed".into(), fields: vec![] }, owner: None, requester: Some(ConnId(1)),
    };
    assert_eq!(simlink_headless_server::routing::recipients(&server, &pending), vec![ConnId(2)]);
    assert_eq!(server.connection(ConnId(2)).unwrap().cadence.ack_tick, Some(10));
    assert_eq!(server.handle_command(ConnId(2), &cmd, |_| panic!("replayed effect")).unwrap().reply, outcome.reply);
    assert_eq!(driver.owner(55), Some(7));
    assert!(driver.adjudicate(&command(2, 8, "ClaimSim", &["55"])).unwrap().starts_with(b"ERR|2|UnauthorizedClientInsertion"));
    assert_eq!(driver.claimed_this_run.iter().copied().collect::<Vec<_>>(), vec![55]);
    driver.release_player(7);
    assert!(driver.claimed_this_run.contains(&55), "fingerprint scope is run-wide, not current claims");
}

#[test]
fn monotonic_sweep_retains_claims_until_the_exact_lease_end() {
    let mut server = Server::new(7);
    server.connect(ConnId(1)); server.join(ConnId(1), 7);
    server.connection_mut(ConnId(1)).unwrap().ring = ring(1);
    assert!(server.expired_sessions(120_099).is_empty());
    assert_eq!(server.expired_sessions(120_100), vec![(ConnId(1), 7, LeaseEndReason::TimeoutExpired)]);
    server.disconnect(ConnId(1));
    assert!(server.connection(ConnId(1)).is_none());
    assert!(server.joined_connections().is_empty());
}

#[test]
fn lease_retirement_is_broadcast_to_the_remaining_player() {
    struct Endpoint;
    impl simlink_headless_server::bridge_driver::BridgeEndpoint for Endpoint {
        fn command(&mut self, _: &simlink_bridge::verbs::BridgeCommand) -> Result<Vec<String>, simlink_headless_server::DriverError> { Ok(vec![]) }
        fn events(&mut self) -> Result<Vec<Vec<u8>>, simlink_headless_server::DriverError> { Ok(vec![]) }
    }
    let mut server = Server::new(7);
    for (id, player) in [(1, 7), (2, 8)] { server.connect(ConnId(id)); server.join(ConnId(id), player); }
    server.connection_mut(ConnId(1)).unwrap().ring = ring(1);
    let mut driver = simlink_headless_server::BridgeGameDriver::new(Endpoint);
    driver.adjudicate_for(ConnId(1), &command(1, 7, "ClaimSim", &["55"])).unwrap();
    driver.poll_routed_events().unwrap();
    for (id, player, _) in server.expired_sessions(120_100) {
        driver.release_player(player);
        server.disconnect(id);
    }
    let events = driver.poll_routed_events().unwrap();
    let release = events.iter().find(|routed| routed.event.kind == "SimReleased").unwrap();
    assert_eq!(release.event.fields, ["7", "55"]);
    assert_eq!(simlink_headless_server::routing::recipients(&server, release), vec![ConnId(2)]);
    assert_eq!(driver.owner(55), None);
    assert!(driver.adjudicate_for(ConnId(2), &command(2, 8, "ClaimSim", &["55"])).unwrap().starts_with(b"ACK|"));
}

#[test]
fn world_file_encoding_is_requester_only_and_cuts_at_256_kib_including_empty_files() {
    use simlink_headless_server::{messages::{hex_encode, Event}, routing::{RoutedEvent, recipients}, serve::encode_event_lines};
    let mut server = Server::new(1);
    for id in 1..=3 { server.connect(ConnId(id)); }
    server.join(ConnId(2), 8); server.set_admin(ConnId(3));
    for size in [0, 262_144, 262_145] {
        let event = Event { seq: 1, tick: 10, corr: 4, kind: "WorldFile".into(),
            fields: vec![hex_encode("Fixture.sims3"), "0".into(), "1".into(), hex_encode("world.nhd"),
                size.to_string(), simlink_headless_server::b64_encode(&vec![1; size])] };
        assert_eq!(recipients(&server, &RoutedEvent { event: event.clone(), owner: None, requester: Some(ConnId(1)) }), vec![ConnId(1)]);
        let lines = encode_event_lines(&event, &mut 1).unwrap();
        assert!(lines[0].ends_with("|b1"));
        assert_eq!(lines.len() - 1, size.div_ceil(262_144).max(1));
        for line in &lines[1..] {
            let chunk = simlink_headless_server::decode_bulk(&simlink_headless_server::split(line.as_bytes()).unwrap()).unwrap();
            assert!(simlink_headless_server::b64_decode(&chunk.b64).unwrap().len() <= 262_144);
        }
    }
}

#[test]
fn offline_rejoin_probe_exercises_both_evictions_bulk_restart_timeout_and_claim_retirement() {
    let (rows, ok, fresh) = card59_scorecard::rejoin_probe().unwrap();
    assert_eq!((rows.len(), ok, fresh), (8, 1, 7));
    assert!(rows.iter().all(|row| row["claims_correct"] == true && row["claim_refused_during_window"] == true));
}

#[test]
fn socket_warm_tokens_select_exact_player_ring_and_fresh_never_releases_an_unidentified_lease() {
    let rows = card59_scorecard::resume_socket_probe().unwrap();
    assert_eq!(rows.iter().filter(|row| row["outcome"] == "ok").count(), 2);
    for case in ["active_token", "missing_token", "unknown_token", "second_reconnect_active", "bulk_gap", "expired", "stale_epoch"] {
        assert!(rows.iter().any(|row| row["case"] == case && row["outcome"] == "fresh"
            && row["claims_correct"] == true && row["scope"] == "socket"), "{case}");
    }
}

#[test]
fn scorecard_rejects_every_normative_failure_and_missing_metrics() {
    let valid = json!({"bytes_fetched":9,"bytes_oracle":9,"tick_drops_during_join":0,
        "presentation_sent_gap_max_during_join":1,"keep_failures_that_failed_a_save":0,
        "lot_modes_delivered":2,"lot_modes_required":2,"save_bytes_written_by_rust":0,"reuse_hits":1,"reuse_misses":1});
    card59_scorecard::validate_scorecard(&valid).unwrap();
    for (key, wrong) in [("bytes_fetched",8),("tick_drops_during_join",1),("presentation_sent_gap_max_during_join",0),
        ("keep_failures_that_failed_a_save",1),("lot_modes_delivered",1),("save_bytes_written_by_rust",1),
        ("reuse_hits",0),("reuse_misses",0)] {
        let mut metrics = valid.clone();
        metrics[key] = json!(wrong);
        assert!(card59_scorecard::validate_scorecard(&metrics).is_err(), "{key}");
    }
    assert!(card59_scorecard::validate_scorecard(&json!({})).is_err());
}

#[test]
fn socket_checkpoint_keeps_the_other_client_pumping_and_failure_injection_is_one_shot() {
    let directory = std::env::temp_dir().join(format!("card59-socket-{}", std::process::id()));
    struct Cleanup(std::path::PathBuf);
    impl Drop for Cleanup {
        fn drop(&mut self) { std::fs::remove_dir_all(&self.0).unwrap(); }
    }
    std::fs::create_dir_all(&directory).unwrap();
    let _cleanup = Cleanup(directory.clone());
    let folder = directory.join("server/Fixture.sims3");
    std::fs::create_dir_all(&folder).unwrap();
    std::fs::write(folder.join("world.nhd"), b"synthetic TS3-authored fixture").unwrap();
    let result = card59_scorecard::socket_probe(&folder, &directory).unwrap();
    assert_eq!(result["passed"], true, "{result}");
    assert!(result["reuse_hits"].as_u64().unwrap() > 0);
    assert!(result["reuse_misses"].as_u64().unwrap() > 0);
    assert_eq!(result["presentation_sent_gap_max_during_join"], 1);
    assert_eq!(result["tick_drops_during_join"], 0);
}

struct PolicyTemp(std::path::PathBuf);
impl PolicyTemp {
    fn new() -> Self {
        static NEXT: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
        let path = std::env::temp_dir().join(format!("card59-policy-{}-{}", std::process::id(),
            NEXT.fetch_add(1, std::sync::atomic::Ordering::Relaxed)));
        std::fs::create_dir_all(&path).unwrap();
        Self(path)
    }
}
impl Drop for PolicyTemp {
    fn drop(&mut self) { std::fs::remove_dir_all(&self.0).unwrap(); }
}

#[derive(Clone, Copy)]
enum ScopeReply { Armed, Error, Timeout, OlderMod }
struct PolicyBridge {
    commands: std::sync::Arc<std::sync::Mutex<Vec<simlink_bridge::verbs::BridgeCommand>>>,
    scope: ScopeReply,
}
impl simlink_headless_server::bridge_driver::BridgeEndpoint for PolicyBridge {
    fn command(&mut self, command: &simlink_bridge::verbs::BridgeCommand) -> Result<Vec<String>, simlink_headless_server::DriverError> {
        use simlink_bridge::verbs::BridgeCommand;
        self.commands.lock().unwrap().push(command.clone());
        let report = match command {
            BridgeCommand::SaveGame { .. } => return Ok(vec!["OK|fixture".into()]),
            BridgeCommand::SacsRecorder { args, .. } => match args[0].as_str() {
                "fingerprint-now" | "fingerprint-saved" =>
                    "scope=h:18;objects=1:o;inventories=1:i;relationships=1:r;ms=1".into(),
                "fingerprint-scope" => match self.scope {
                    ScopeReply::Armed => format!("fingerprint-scope={}", args[1]),
                    ScopeReply::Error => return Ok(vec![format!("ERROR|fixture|{}",
                        simlink_bridge::text::encode_text("scope unavailable"))]),
                    ScopeReply::Timeout => return Err(simlink_headless_server::DriverError("scope reply timeout".into())),
                    ScopeReply::OlderMod => "usage: recorder status".into(),
                },
                _ => panic!("unexpected recorder request: {args:?}"),
            },
            _ => panic!("unexpected native command: {command:?}"),
        };
        Ok(vec![format!("AUTONOMY_PROBE|fixture|ok|{}", simlink_bridge::text::encode_text(&report))])
    }
    fn events(&mut self) -> Result<Vec<Vec<u8>>, simlink_headless_server::DriverError> { Ok(vec![]) }
}

struct PolicyServer {
    _server: simlink_headless_server::serve::RunningServer,
    public: std::io::BufReader<std::net::TcpStream>,
    admin: std::io::BufReader<std::net::TcpStream>,
    commands: std::sync::Arc<std::sync::Mutex<Vec<simlink_bridge::verbs::BridgeCommand>>>,
    counters: std::sync::Arc<std::sync::Mutex<simlink_headless_server::join_reuse::JoinReuseCounters>>,
}
impl PolicyServer {
    fn new(temp: &PolicyTemp, scope: ScopeReply) -> Self {
        use std::net::{TcpListener, TcpStream};
        use std::sync::{Arc, Mutex};
        for (slot, bytes) in [("Fixture", b"checkpoint".as_slice()), ("BackupSlot", b"backup".as_slice())] {
            let folder = temp.0.join(format!("{slot}.sims3"));
            std::fs::create_dir_all(&folder).unwrap();
            std::fs::write(folder.join("world.nhd"), bytes).unwrap();
        }
        let commands = Arc::new(Mutex::new(Vec::new()));
        let mut driver = simlink_headless_server::BridgeGameDriver::new(PolicyBridge { commands: Arc::clone(&commands), scope });
        driver.set_save_roots(vec![temp.0.clone()]);
        driver.configure_saves("Fixture.sims3".into(), None);
        driver.save_faults.run_dir = temp.0.join("flags");
        // The recorded nonempty fingerprint covers a Sim claimed during this run.
        assert!(driver.adjudicate(&command(1, 7, "ClaimSim", &["55"])).unwrap().starts_with(b"ACK|"));
        let counters = Arc::clone(&driver.join_reuse_counters);
        let server = simlink_headless_server::serve::RunningServer::start_configured(
            TcpListener::bind("127.0.0.1:0").unwrap(), Some(TcpListener::bind("127.0.0.1:0").unwrap()),
            move || Ok(driver), 4, simlink_headless_server::serve::StreamOptions::default()).unwrap();
        let connect = |addr| {
            let socket = TcpStream::connect(addr).unwrap();
            socket.set_read_timeout(Some(std::time::Duration::from_secs(10))).unwrap();
            std::io::BufReader::new(socket)
        };
        let mut public = connect(server.local_addr());
        let mut admin = connect(server.admin_addr().unwrap());
        Self::write(&mut public, "HELLO|1|ring=1,wfs=1,role=client");
        assert!(Self::line(&mut public).starts_with("WELCOME|1|"));
        Self::write(&mut admin, "HELLO|1|");
        assert!(Self::line(&mut admin).starts_with("WELCOME|1|"));
        Self { _server: server, public, admin, commands, counters }
    }
    fn write(socket: &mut std::io::BufReader<std::net::TcpStream>, line: &str) {
        use std::io::Write;
        writeln!(socket.get_mut(), "{line}").unwrap();
        socket.get_mut().flush().unwrap();
    }
    fn line(socket: &mut std::io::BufReader<std::net::TcpStream>) -> String {
        String::from_utf8(simlink_headless_server::serve::read_bounded_line(socket).unwrap().expect("server closed")).unwrap()
    }
    fn world(&mut self, corr: u64) -> (String, Vec<u8>) {
        Self::write(&mut self.public, &format!("CMD|{corr}|RequestWorld|{corr}|7"));
        let header = loop {
            let line = Self::line(&mut self.public);
            if line.starts_with("ACK|") {
                assert!(line.starts_with(&format!("ACK|{corr}|RequestWorld")));
                continue;
            }
            let event = simlink_headless_server::decode_event(&simlink_headless_server::split(line.as_bytes()).unwrap()).unwrap();
            assert_eq!(event.kind, "WorldFile", "{event:?}");
            assert_eq!(event.corr, corr);
            assert_eq!(&event.fields[1..3], &["0", "1"]);
            break event;
        };
        let line = Self::line(&mut self.public);
        let chunk = simlink_headless_server::decode_bulk(&simlink_headless_server::split(line.as_bytes()).unwrap()).unwrap();
        assert_eq!((chunk.index, chunk.count), (0, 1));
        assert_eq!(Some(chunk.bulk_id), simlink_headless_server::messages::dec_bulk_ref(&header.fields[5]));
        (simlink_headless_server::messages::hex_decode(&header.fields[0]).unwrap(),
            simlink_headless_server::b64_decode(&chunk.b64).unwrap())
    }
    fn save(&mut self, name: &str) {
        Self::write(&mut self.admin, &format!("CMD|1|SaveGame|1|7|{}", simlink_headless_server::messages::hex_encode(name)));
        let reply = Self::line(&mut self.admin);
        assert!(reply.starts_with("ACK|1|SaveGame"), "{reply}");
    }
    fn saves(&self, name: &str) -> usize {
        self.commands.lock().unwrap().iter().filter(|command| matches!(command,
            simlink_bridge::verbs::BridgeCommand::SaveGame { save_name, .. } if save_name.eq_ignore_ascii_case(name))).count()
    }
    fn recorder_calls(&self, verb: &str) -> usize {
        self.commands.lock().unwrap().iter().filter(|command| matches!(command,
            simlink_bridge::verbs::BridgeCommand::SacsRecorder { args, .. } if args[0] == verb)).count()
    }
}

/// Process-local environment avoids changing reuse policy under concurrent tests.
fn isolated_policy_case(name: &str, reuse: &str) -> bool {
    if std::env::var("CARD59_POLICY_TEST_CHILD").ok().as_deref() == Some(name) { return false; }
    let output = std::process::Command::new(std::env::current_exe().unwrap())
        .args(["--exact", name, "--nocapture"]).env("CARD59_POLICY_TEST_CHILD", name)
        .env("SIMSONLINE_JOIN_REUSE", reuse).output().unwrap();
    let stdout = String::from_utf8_lossy(&output.stdout);
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(output.status.success(), "{stdout}\n{stderr}");
    if reuse == "0" {
        // This is the machine-consumed prefix parsed by join_fingerprint.py.
        assert!(!stdout.contains("[world] join reuse:") && !stderr.contains("[world] join reuse:"),
            "{stdout}\n{stderr}");
    }
    true
}

#[test]
fn checkpoint_reuse_disabled_takes_no_fingerprint_or_reuse_decision() {
    if isolated_policy_case("checkpoint_reuse_disabled_takes_no_fingerprint_or_reuse_decision", "0") { return; }
    let temp = PolicyTemp::new();
    let service = &mut PolicyServer::new(&temp, ScopeReply::Armed);
    assert_eq!(service.world(1), ("Fixture.sims3".into(), b"checkpoint".to_vec()));
    assert_eq!(service.world(2), ("Fixture.sims3".into(), b"checkpoint".to_vec()));
    assert_eq!(service.saves("Fixture.sims3"), 2);
    assert!(!service.commands.lock().unwrap().iter().any(|command|
        matches!(command, simlink_bridge::verbs::BridgeCommand::SacsRecorder { .. })));
    let counters = service.counters.lock().unwrap();
    assert_eq!((counters.hits(), counters.misses()), (0, 0));
}

#[test]
fn checkpoint_non_checkpoint_save_cannot_seed_the_reusable_world() {
    if isolated_policy_case("checkpoint_non_checkpoint_save_cannot_seed_the_reusable_world", "1") { return; }
    let temp = PolicyTemp::new();
    let service = &mut PolicyServer::new(&temp, ScopeReply::Armed);
    assert_eq!(service.world(1), ("Fixture.sims3".into(), b"checkpoint".to_vec()));
    service.save("BackupSlot.sims3");
    assert_eq!(service.world(2), ("Fixture.sims3".into(), b"checkpoint".to_vec()));
    assert_eq!(service.saves("BackupSlot.sims3"), 1);
    assert_eq!(service.saves("Fixture.sims3"), 2);
    let commands = service.commands.lock().unwrap();
    let backup = commands.iter().position(|command| matches!(command,
        simlink_bridge::verbs::BridgeCommand::SaveGame { save_name, .. } if save_name == "BackupSlot.sims3")).unwrap();
    assert!(matches!(&commands[backup - 1], simlink_bridge::verbs::BridgeCommand::SacsRecorder { args, .. }
        if args[0] == "fingerprint-saved"));
    assert!(matches!(&commands[backup + 1], simlink_bridge::verbs::BridgeCommand::SacsRecorder { args, .. }
        if args[0] == "fingerprint-now"));
    let counters = service.counters.lock().unwrap();
    assert_eq!((counters.hits(), counters.misses()), (0, 2));
}

#[test]
fn checkpoint_scope_errors_timeouts_and_old_mods_degrade_without_failing_the_save() {
    if isolated_policy_case("checkpoint_scope_errors_timeouts_and_old_mods_degrade_without_failing_the_save", "1") { return; }
    for scope in [ScopeReply::Error, ScopeReply::Timeout, ScopeReply::OlderMod] {
        let temp = PolicyTemp::new();
        let service = &mut PolicyServer::new(&temp, scope);
        service.save("Fixture.sims3");
        assert_eq!(service.world(1), ("Fixture.sims3".into(), b"checkpoint".to_vec()));
        assert_eq!(service.world(2), ("Fixture.sims3".into(), b"checkpoint".to_vec()));
        assert_eq!(service.saves("Fixture.sims3"), 3);
        assert_eq!(service.recorder_calls("fingerprint-scope"), 3);
        assert_eq!(service.recorder_calls("fingerprint-saved"), 0);
        let counters = service.counters.lock().unwrap();
        assert_eq!((counters.hits(), counters.misses()), (0, 2));
    }
}

#[test]
fn checkpoint_boot_restore_finds_missing_or_broken_saves_in_secondary_roots() {
    for missing in [true, false] {
        let temp = PolicyTemp::new();
        let first = temp.0.join("profile");
        let secondary = temp.0.join("OneDrive");
        let folder = secondary.join("Fixture.sims3");
        std::fs::create_dir_all(&first).unwrap();
        std::fs::create_dir_all(&folder).unwrap();
        let files = vec![("world.nhd".into(), b"synthetic whole save".to_vec())];
        std::fs::write(folder.join("world.nhd"), &files[0].1).unwrap();
        simlink_headless_server::last_good::keep(&folder, &files, "2026-11-02T00:00:00Z").unwrap();
        if missing { std::fs::remove_dir_all(&folder).unwrap(); }
        else { std::fs::remove_file(folder.join("world.nhd")).unwrap(); }
        let mut driver = simlink_headless_server::BridgeGameDriver::new(simlink_headless_server::bridge_driver::OfflineBridge);
        driver.set_save_roots(vec![first.clone(), secondary]);
        driver.configure_saves("Fixture.sims3".into(), None);
        assert_eq!(std::fs::read(folder.join("world.nhd")).unwrap(), files[0].1);
        assert!(!first.join("Fixture.sims3").exists());
    }
}
