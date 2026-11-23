#![forbid(unsafe_code)]
//! Actual serve entry, TCP framing/sequence and resident file reload, no native game.
use simlink_headless_server::messages::{self as m, Command, Event};
use simlink_headless_server::routing::RoutedEvent;
use simlink_headless_server::serve::{RunningServer, StreamOptions};
use simlink_headless_server::{ConnId, DriverError, FakeDriver, GameDriver};
use std::io::{BufRead, BufReader, Write};
use std::net::{SocketAddr, TcpListener, TcpStream};
use std::path::PathBuf;
use std::sync::{
    atomic::{AtomicI64, Ordering},
    mpsc, Arc,
};
use std::time::{Duration, Instant};

const FALSE: &str = "fn split_before(record) { false }";
const HELPERS: &str = "fn selected(r) { r.player_id == \"2\" && r.family == \"motion\" && r.joined_clients == 2 } fn split_before(record) { selected(record) }";
struct Script(PathBuf);
impl Script {
    fn new() -> Self {
        static NEXT: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
        let path = std::env::temp_dir().join(format!(
            "sync-policy-{}-{}.rhai",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        std::fs::write(&path, FALSE).unwrap();
        Self(path)
    }
    fn edit(&self, source: &str) -> String {
        std::fs::write(&self.0, source).unwrap();
        hash(source)
    }
}
impl Drop for Script {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.0);
    }
}
fn hash(source: &str) -> String {
    use sha2::{Digest, Sha256};
    format!("{:x}", Sha256::digest(source.as_bytes()))
}
fn connect(addr: SocketAddr) -> BufReader<TcpStream> {
    let s = TcpStream::connect(addr).unwrap();
    s.set_read_timeout(Some(Duration::from_secs(5))).unwrap();
    BufReader::new(s)
}
fn exchange(peer: &mut BufReader<TcpStream>, request: &str) -> String {
    writeln!(peer.get_mut(), "{request}").unwrap();
    let mut line = String::new();
    assert_ne!(peer.read_line(&mut line).unwrap(), 0);
    line.trim_end().into()
}
fn status(addr: SocketAddr) -> String {
    let line = exchange(&mut connect(addr), "SYNC_POLICY_STATE");
    let json: serde_json::Value = serde_json::from_str(&line).unwrap();
    assert_eq!(json["accepted"], true);
    json["report"].as_str().unwrap().into()
}
fn wait_status(addr: SocketAddr, test: impl Fn(&str) -> bool) -> String {
    let end = Instant::now() + Duration::from_secs(5);
    loop {
        let s = status(addr);
        if test(&s) {
            return s;
        }
        assert!(Instant::now() < end, "policy deadline: {s}");
        std::thread::sleep(Duration::from_millis(10));
    }
}
fn field(report: &str, name: &str) -> u64 {
    report
        .lines()
        .next()
        .unwrap()
        .split('|')
        .find_map(|p| p.strip_prefix(&format!("{name}=")))
        .unwrap()
        .parse()
        .unwrap()
}

struct Traffic {
    fake: FakeDriver,
    events: mpsc::Receiver<(bool, Vec<String>)>,
    tick: Arc<AtomicI64>,
    baseline: Arc<std::sync::atomic::AtomicBool>,
    held_ready: Arc<std::sync::atomic::AtomicBool>,
    held: Vec<RoutedEvent>,
    world_generation: u64,
}
impl GameDriver for Traffic {
    fn presentation_world_generation(&self) -> u64 {
        self.world_generation
    }
    fn admin_request(
        &mut self,
        request: &simlink_headless_server::admin::Request,
    ) -> Result<String, DriverError> {
        if request.name != "LOAD_SAVE" {
            return Err(DriverError("unsupported fixture admin verb".into()));
        }
        self.world_generation += 1;
        self.held.push(RoutedEvent {
            event: Event {
                seq: 0,
                tick: 11,
                corr: 17,
                kind: "__presentation_world_reset".into(),
                fields: vec![],
            },
            owner: None,
            requester: None,
        });
        if request
            .args
            .first()
            .is_some_and(|arg| arg == "6F766572666C6F77")
        {
            for _ in 0..1025 {
                self.held.push(RoutedEvent {
                    event: Event {
                        seq: 0,
                        tick: 11,
                        corr: 17,
                        kind: "SimAnimation".into(),
                        fields: vec![m::enc_list(&[m::hex_encode("1|Ps|7|10|clip_a")])],
                    },
                    owner: None,
                    requester: None,
                });
            }
        }
        if request.args.first().is_some_and(|arg| arg == "796573") {
            self.held.push(RoutedEvent {
                event: Event {
                    seq: 0,
                    tick: 11,
                    corr: 17,
                    kind: "SimAnimation".into(),
                    fields: vec![m::enc_list(&[m::hex_encode("0|Ne|2")])],
                },
                owner: None,
                requester: None,
            });
        }
        Ok("{\"ok\":true,\"accepted\":true}".into())
    }
    fn claimed_sims(&self) -> Vec<u64> {
        vec![10]
    }
    fn recorder(&mut self, args: &[String]) -> Result<String, DriverError> {
        Ok(if args.first().is_some_and(|arg| arg == "snapshot") {
            "drivers=".into()
        } else {
            "seq=0;more=0".into()
        })
    }
    fn baseline_pending(&self, id: ConnId) -> bool {
        id == ConnId(4) && self.baseline.load(Ordering::Acquire)
    }
    fn adjudicate(&mut self, cmd: &Command) -> Result<Vec<u8>, DriverError> {
        self.fake.adjudicate(cmd)
    }
    fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, DriverError> {
        Ok(Vec::new())
    }
    fn snapshot(
        &mut self,
        _: i64,
    ) -> Result<Option<simlink_headless_server::snapshot::Snapshot>, DriverError> {
        Ok(Some(simlink_headless_server::snapshot::Snapshot {
            tick: self.tick.load(Ordering::Acquire),
            world_name: "Fixture".into(),
            save_name: "Fixture".into(),
            speed: "Normal".into(),
            sims: vec![],
            objects: vec![],
        }))
    }
    fn poll_routed_events(&mut self) -> Result<Vec<RoutedEvent>, DriverError> {
        let pending = self.baseline.load(Ordering::Acquire);
        let mut output = if pending {
            Vec::new()
        } else {
            std::mem::take(&mut self.held)
        };
        let events: Vec<_> = self
            .events
            .try_iter()
            .map(|(protected, lines)| RoutedEvent {
                event: Event {
                    seq: 0,
                    tick: 11,
                    corr: 17,
                    kind: "SimAnimation".into(),
                    fields: vec![m::enc_list(
                        &lines.iter().map(|s| m::hex_encode(s)).collect::<Vec<_>>(),
                    )],
                },
                owner: None,
                requester: protected.then_some(ConnId(4)),
            })
            .collect();
        for event in events {
            if pending && event.requester == Some(ConnId(4)) {
                self.held.push(event);
                self.held_ready.store(true, Ordering::Release);
            } else {
                output.push(event);
            }
        }
        Ok(output)
    }
}
struct Peer {
    reader: BufReader<TcpStream>,
    last: u64,
}
impl Peer {
    fn join(addr: SocketAddr, player: i32) -> Self {
        let mut reader = connect(addr);
        assert!(exchange(&mut reader, "HELLO|1|ring=1,wfs=1").starts_with("WELCOME"));
        assert!(
            exchange(&mut reader, &format!("CMD|1|JoinSession|1|{player}|61|~")).starts_with("ACK")
        );
        // The public ring reserves one sequence for each adjudicated command reply.
        Self { reader, last: 1 }
    }
    fn acknowledge(&mut self, player: i32, tick: i64) {
        let reply = exchange(
            &mut self.reader,
            &format!("CMD|2|AckSnapshot|2|{player}|{tick}"),
        );
        assert!(reply.starts_with("ACK"), "{reply}");
        self.last += 1;
    }
    fn animation(&mut self, lines: usize) -> (Vec<String>, usize) {
        let mut output = Vec::new();
        let mut packets = 0;
        while output.len() < lines {
            let mut line = String::new();
            assert_ne!(self.reader.read_line(&mut line).unwrap(), 0);
            if !line.starts_with("EV|") {
                continue;
            }
            let p = simlink_headless_server::split(line.trim_end().as_bytes()).unwrap();
            let event = simlink_headless_server::decode_event(&p).unwrap();
            assert_eq!(event.seq, self.last + 1);
            self.last = event.seq;
            if event.kind == "SimAnimation" {
                assert_eq!(event.tick, 11);
                assert_eq!(event.corr, 17);
                output.extend(
                    m::dec_list(&event.fields[0])
                        .unwrap()
                        .into_iter()
                        .map(|s| m::hex_decode(&s).unwrap()),
                );
                packets += 1;
            }
        }
        (output, packets)
    }
}
fn batch() -> Vec<String> {
    vec![
        "11|Pz|18446744073709551615|0|0|0".into(),
        "12|R|7|5|6|0|10".into(),
        "13|Pz|18446744073709551615|1|0|0".into(),
    ]
}

struct Fixture {
    server: RunningServer,
    send: mpsc::Sender<(bool, Vec<String>)>,
    tick: Arc<AtomicI64>,
    baseline: Arc<std::sync::atomic::AtomicBool>,
    held_ready: Arc<std::sync::atomic::AtomicBool>,
}
fn fixture(script: &Script) -> (Fixture, Peer, Peer) {
    fixture_with_policy(script, true)
}
fn fixture_with_policy(script: &Script, enabled: bool) -> (Fixture, Peer, Peer) {
    let (send, events) = mpsc::channel();
    let tick = Arc::new(AtomicI64::new(1));
    let driver_tick = Arc::clone(&tick);
    let baseline = Arc::new(std::sync::atomic::AtomicBool::new(false));
    let driver_baseline = Arc::clone(&baseline);
    let held_ready = Arc::new(std::sync::atomic::AtomicBool::new(false));
    let driver_ready = Arc::clone(&held_ready);
    let server = RunningServer::start_configured(
        TcpListener::bind("127.0.0.1:0").unwrap(),
        Some(TcpListener::bind("127.0.0.1:0").unwrap()),
        move || {
            Ok(Traffic {
                fake: FakeDriver::new(),
                events,
                tick: driver_tick,
                baseline: driver_baseline,
                held_ready: driver_ready,
                held: Vec::new(),
                world_generation: 0,
            })
        },
        32,
        StreamOptions {
            sync_policy: enabled.then(|| script.0.clone()),
            base_interval_ms: 10.0,
            ..StreamOptions::default()
        },
    )
    .unwrap();
    let addr = server.admin_addr().unwrap();
    let mut one = Peer::join(server.local_addr(), 1);
    let mut two = Peer::join(server.local_addr(), 2);
    // Actual stream join work must finish before ordinary live packets are eligible.
    // Claims above cause both Fresh and Join history gathers to run to completion.
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        let state = exchange(&mut connect(addr), "FRONT_DOOR_STATE");
        if !state.contains("presentation_history_pending=1") {
            break;
        }
        assert!(
            Instant::now() < deadline,
            "baseline did not complete: {state}"
        );
        std::thread::sleep(Duration::from_millis(10));
    }
    one.acknowledge(1, 1);
    two.acknowledge(2, 1);
    (
        Fixture {
            server,
            send,
            tick,
            baseline,
            held_ready,
        },
        one,
        two,
    )
}

#[test]
fn resident_socket_reload_changes_only_selected_players_grouping_and_preserves_baselines() {
    let script = Script::new();
    let (
        Fixture {
            server,
            send,
            tick,
            baseline,
            held_ready,
        },
        mut one,
        mut two,
    ) = fixture(&script);
    let addr = server.admin_addr().unwrap();
    let _unjoined = connect(server.local_addr());
    wait_status(addr, |s| {
        s.contains(&format!("active_sha256={}", hash(FALSE)))
    });
    send.send((false, batch())).unwrap();
    assert_eq!(one.animation(3), (batch(), 1));
    assert_eq!(two.animation(3), (batch(), 1));
    let sha = script.edit(HELPERS);
    wait_status(addr, |s| s.contains(&format!("active_sha256={sha}")));
    send.send((false, batch())).unwrap();
    assert_eq!(one.animation(3), (batch(), 1));
    assert_eq!(two.animation(3), (batch(), 2));
    // A pending targeted join/history group remains one packet after cutover,
    // even though its player2 motion predicate is true.
    baseline.store(true, Ordering::Release);
    send.send((true, batch())).unwrap();
    let deadline = Instant::now() + Duration::from_secs(5);
    while !held_ready.load(Ordering::Acquire) {
        assert!(Instant::now() < deadline);
        std::thread::sleep(Duration::from_millis(5));
    }
    // Publish a fresh true revision while the targeted baseline is still held.
    let sha = script.edit(
        "fn split_before(record) { record.player_id == \"2\" && record.family == \"motion\" }",
    );
    wait_status(addr, |s| {
        s.contains(&format!("pending_sha256={sha}")) && !s.contains(&format!("active_sha256={sha}"))
    });
    baseline.store(false, Ordering::Release);
    assert_eq!(two.animation(3), (batch(), 1));
    let sha = script.edit(FALSE);
    wait_status(addr, |s| s.contains(&format!("active_sha256={sha}")));
    for bad in [
        "fn split_before(r) {",
        "fn split_before(r) { 1 }",
        "fn split_before(r) { loop {} }",
        "fn unrelated(r) { false }",
    ] {
        let bad_sha = script.edit(bad);
        wait_status(addr, |s| s.contains(&format!("rejected_sha256={bad_sha}")));
        send.send((false, batch())).unwrap();
        assert_eq!(one.animation(3), (batch(), 1));
        assert_eq!(two.animation(3), (batch(), 1));
    }
    let late = "fn split_before(r) { if r.tick > 20 { 1 } else { false } }";
    let sha = script.edit(late);
    wait_status(addr, |s| s.contains(&format!("active_sha256={sha}")));
    tick.store(30, Ordering::Release);
    let rolled = wait_status(addr, |s| {
        field(s, "rollbacks") > 0 && s.contains(&format!("active_sha256={}", hash(FALSE)))
    });
    let rollbacks = field(&rolled, "rollbacks");
    std::thread::sleep(Duration::from_millis(80));
    assert_eq!(
        field(&status(addr), "rollbacks"),
        rollbacks,
        "bad source must remain quarantined"
    );
    send.send((false, batch())).unwrap();
    assert_eq!(one.animation(3), (batch(), 1));
    assert_eq!(two.animation(3), (batch(), 1));
    let previous_fault = "fn split_before(r) { if r.tick > 60 { 1 } else { false } }";
    let active_fault = "fn split_before(r) { if r.tick > 70 { 1 } else { false } }";
    for source in [previous_fault, active_fault] {
        let sha = script.edit(source);
        wait_status(addr, |s| s.contains(&format!("active_sha256={sha}")));
    }
    tick.store(80, Ordering::Release);
    wait_status(addr, |s| {
        field(s, "rollbacks") > rollbacks && s.contains(&format!("active_sha256={}", hash(FALSE)))
    });
    send.send((false, batch())).unwrap();
    assert_eq!(one.animation(3), (batch(), 1));
    assert_eq!(two.animation(3), (batch(), 1));
    server.shutdown().unwrap();
}

#[test]
fn cli_checks_interface_and_output_before_any_bind_or_attach() {
    let script = Script::new();
    for (text, ok) in [
        (FALSE, true),
        ("fn wrong(r) { false }", false),
        ("fn split_before(r) { 1 }", false),
        ("fn split_before(r) { loop {} }", false),
    ] {
        script.edit(text);
        let result = std::process::Command::new(env!("CARGO_BIN_EXE_simlink_headless_server"))
            .args([
                "--check-sync-policy",
                script.0.to_str().unwrap(),
                "--public",
                "invalid",
                "--pid",
                "0",
            ])
            .output()
            .unwrap();
        assert_eq!(result.status.success(), ok);
        let report: serde_json::Value = serde_json::from_slice(&result.stdout).unwrap();
        assert_eq!(report["ok"], ok);
    }
    script.edit(&" ".repeat(65_537));
    assert!(simlink_headless_server::sync_policy::check(&script.0).is_err());
}

#[test]
fn absent_policy_preserves_original_payload_and_disabled_status() {
    let mut host = simlink_headless_server::sync_policy::Host::start(None).unwrap();
    let event = Event {
        seq: 4,
        tick: 5,
        corr: 6,
        kind: "SimAnimation".into(),
        fields: vec!["[NOTHEX]".into()],
    };
    assert_eq!(host.packets(u64::MAX, &event, false), vec![event]);
    assert!(host.report().contains("enabled=0"));
}

#[test]
fn malformed_oversized_and_zero_sequence_lines_bypass_intact() {
    let script = Script::new();
    let mut host =
        simlink_headless_server::sync_policy::Host::start(Some(script.0.clone())).unwrap();
    for text in [
        "invalid".into(),
        "11|Pz".into(),
        "11|Pz|bad\nline".into(),
        "x|Pz|1".into(),
        "0|A|7|1|2|3|4|-2".into(),
        format!("11|Pz|{}", "x".repeat(16_385)),
    ] {
        let event = Event {
            seq: 0,
            tick: 5,
            corr: 6,
            kind: "SimAnimation".into(),
            fields: vec![m::enc_list(&[m::hex_encode(&text)])],
        };
        assert_eq!(host.packets(u64::MAX, &event, false), vec![event]);
    }
}

fn payload_policy(clip: &str, sound: &str) -> String {
    format!(
        r#"fn split_before(record) {{ false }}
fn animation_target() {{ "{clip}" }}
fn rules() {{ [
 #{{kind:"anim_param",player:"2",machine_type:"7",machine_group:"8",machine_instance:"9",param:"10",from:"clip_a",to:animation_target()}},
 #{{kind:"audio_name",player:"2",op:"o",from:"sound_a",to:"{sound}"}}
] }}"#
    )
}
fn ps(seq: u64, driver: u32, value: &str) -> String {
    format!("{seq}|Ps|{driver}|10|{value}")
}
fn sound(seq: u64, value: &str) -> String {
    format!(
        "{seq}|Au|18446744073709551615|o|55|{}|1",
        m::hex_encode(value)
    )
}
#[test]
fn resident_payload_rules_pin_baselines_and_preserve_sound_lifetime_across_edits() {
    let script = Script::new();
    let old = script.edit(&payload_policy("clip_b", "sound_b"));
    let (
        Fixture {
            server,
            send,
            baseline,
            held_ready,
            ..
        },
        mut one,
        mut two,
    ) = fixture(&script);
    let addr = server.admin_addr().unwrap();
    let acquire = "0|A|7|18446744073709551615|7|8|9|-2".to_string();
    let intro = vec![acquire.clone(), ps(0, 7, "clip_a")];
    send.send((false, intro.clone())).unwrap();
    assert_eq!(one.animation(2), (intro, 1));
    assert_eq!(two.animation(2), (vec![acquire, ps(0, 7, "clip_b")], 1));
    let live = vec![ps(1, 7, "clip_a"), sound(2, "sound_a")];
    send.send((false, live.clone())).unwrap();
    assert_eq!(one.animation(2), (live, 1));
    assert_eq!(
        two.animation(2),
        (vec![ps(1, 7, "clip_b"), sound(2, "sound_b")], 1)
    );
    baseline.store(true, Ordering::Release);
    send.send((true, vec![ps(0, 7, "clip_a"), sound(0, "sound_a")]))
        .unwrap();
    let end = Instant::now() + Duration::from_secs(5);
    while !held_ready.load(Ordering::Acquire) {
        assert!(Instant::now() < end);
        std::thread::sleep(Duration::from_millis(5));
    }
    let new = script.edit(&payload_policy("clip_c", "sound_c"));
    wait_status(addr, |s| {
        s.contains(&format!("pending_sha256={new}")) && s.contains(&format!("active_sha256={old}"))
    });
    baseline.store(false, Ordering::Release);
    assert_eq!(
        two.animation(2),
        (vec![ps(0, 7, "clip_b"), sound(0, "sound_b")], 1),
        "final old baseline pump stays old"
    );
    wait_status(addr, |s| s.contains(&format!("active_sha256={new}")));
    // An existing handle keeps its alias for a repeated synthetic start, despite the edit.
    send.send((true, vec![sound(0, "sound_a")])).unwrap();
    assert_eq!(two.animation(1), (vec![sound(0, "sound_b")], 1));
    let property = "4|Au|18446744073709551615|s|55|0|0.5".to_string();
    let stop = "5|Au|18446744073709551615|x|55".to_string();
    let live = vec![
        ps(3, 7, "clip_a"),
        property.clone(),
        stop.clone(),
        sound(6, "sound_a"),
    ];
    send.send((false, live.clone())).unwrap();
    assert_eq!(one.animation(4), (live, 1));
    assert_eq!(
        two.animation(4),
        (
            vec![ps(3, 7, "clip_c"), property, stop, sound(6, "sound_c")],
            1
        )
    );
    // Released, unknown, reused-with-different-resource drivers fail closed. A later A restores eligibility.
    let raw = vec![
        "7|X|7".into(),
        ps(8, 7, "clip_a"),
        "9|A|7|18446744073709551615|7|8|11|-2".into(),
        ps(10, 7, "clip_a"),
        ps(11, 8, "clip_a"),
    ];
    send.send((false, raw.clone())).unwrap();
    assert_eq!(one.animation(5), (raw.clone(), 1));
    assert_eq!(two.animation(5), (raw, 1));
    let a = vec!["12|A|8|18446744073709551615|7|8|9|-2".into()];
    send.send((false, a.clone())).unwrap();
    assert_eq!(one.animation(1), (a.clone(), 1));
    assert_eq!(two.animation(1), (a, 1));
    send.send((false, vec![ps(13, 8, "clip_a")])).unwrap();
    assert_eq!(one.animation(1), (vec![ps(13, 8, "clip_a")], 1));
    assert_eq!(two.animation(1), (vec![ps(13, 8, "clip_c")], 1));
    // A bad rule rejects the entire candidate; valid delivery continues under the previous table.
    let bad=script.edit(r#"fn split_before(r) {false} fn rules() {[#{kind:"audio_name",player:"2",op:"x",from:"sound_a",to:"sound_b"}]}"#);
    wait_status(addr, |s| {
        s.contains(&format!("rejected_sha256={bad}")) && s.contains(&format!("active_sha256={new}"))
    });
    send.send((false, vec![ps(14, 8, "clip_a")])).unwrap();
    assert_eq!(one.animation(1), (vec![ps(14, 8, "clip_a")], 1));
    assert_eq!(two.animation(1), (vec![ps(14, 8, "clip_c")], 1));
    let state = status(addr);
    assert!(state.contains("last_transform|player=2|kind=Ps|source_seq=14|driver=8|param=10"));
    assert!(state.contains("transform|player=2|animation_values="));
    server.shutdown().unwrap();
}

#[test]
fn uninspectable_forwarded_batch_invalidates_old_machine_and_sound_associations() {
    let script = Script::new();
    script.edit(&payload_policy("clip_b", "sound_b"));
    let (Fixture { server, send, .. }, mut one, mut two) = fixture(&script);
    let a = vec!["1|A|7|1|7|8|9|-2".into(), sound(2, "sound_a")];
    send.send((false, a.clone())).unwrap();
    assert_eq!(one.animation(2), (a, 1));
    assert_eq!(
        two.animation(2),
        (vec!["1|A|7|1|7|8|9|-2".into(), sound(2, "sound_b")], 1)
    );
    let bypass = vec![
        "3|A|7|1|7|8|11|-2".into(),
        format!("4|O|1|{}", "a".repeat(16_385)),
    ];
    send.send((false, bypass.clone())).unwrap();
    assert_eq!(one.animation(2), (bypass.clone(), 1));
    assert_eq!(two.animation(2), (bypass, 1));
    let unknown = vec![ps(5, 7, "clip_a"), sound(0, "sound_a")];
    send.send((false, unknown.clone())).unwrap();
    assert_eq!(one.animation(2), (unknown.clone(), 1));
    assert_eq!(
        two.animation(2),
        (unknown, 1),
        "unknown source lifetime cannot reuse stale alias"
    );
    server.shutdown().unwrap();
}
#[test]
fn near_limit_payload_fallback_keeps_source_and_actual_forwarded_lifecycle_then_actor_continues() {
    let script = Script::new();
    script.edit(&payload_policy(&"a".repeat(128), &"b".repeat(128)));
    let (Fixture { server, send, .. }, mut one, mut two) = fixture(&script);
    let addr = server.admin_addr().unwrap();
    let mut source = vec![
        "1|A|7|1|7|8|9|-2".into(),
        ps(2, 7, "clip_a"),
        sound(3, "sound_a"),
    ];
    source.extend((4..201).map(|i| format!("{i}|O|1|")));
    let target = m::PUBLIC_LINE_MAX_BYTES - 160;
    let initial = m::enc_list(&source.iter().map(|s| m::hex_encode(s)).collect::<Vec<_>>()).len();
    let mut remaining = (target - initial) / 2;
    for line in &mut source[3..] {
        let n = remaining.min(16_384 - line.len());
        line.push_str(&"x".repeat(n));
        remaining -= n;
    }
    assert_eq!(remaining, 0);
    let body = m::enc_list(&source.iter().map(|s| m::hex_encode(s)).collect::<Vec<_>>());
    assert!(simlink_headless_server::wire_v1::encode_event(&Event {
        seq: u64::MAX,
        tick: u64::MAX,
        corr: u64::MAX,
        kind: "SimAnimation".into(),
        fields: vec![body]
    })
    .is_ok());
    send.send((false, source.clone())).unwrap();
    assert_eq!(one.animation(200), (source.clone(), 1));
    assert_eq!(two.animation(200), (source, 1));
    assert!(status(addr).contains("output_fallbacks=2"));
    // The original A did reach both clients; subsequent Ps is scoped to its actual machine.
    send.send((false, vec![ps(201, 7, "clip_a")])).unwrap();
    assert_eq!(one.animation(1), (vec![ps(201, 7, "clip_a")], 1));
    assert_eq!(two.animation(1), (vec![ps(201, 7, &"a".repeat(128))], 1));
    // The original audio start reached clients, so its pinned alias must be original, not an unforwarded replacement.
    let new = script.edit(&payload_policy("clip_c", "sound_c"));
    wait_status(addr, |s| s.contains(&format!("active_sha256={new}")));
    send.send((true, vec![sound(0, "sound_a")])).unwrap();
    assert_eq!(two.animation(1), (vec![sound(0, "sound_a")], 1));
    let stop = "202|Au|18446744073709551615|x|55".to_string();
    let source = vec![stop.clone(), sound(203, "sound_a")];
    send.send((false, source.clone())).unwrap();
    assert_eq!(one.animation(2), (source, 1));
    assert_eq!(two.animation(2), (vec![stop, sound(203, "sound_c")], 1));
    server.shutdown().unwrap();
}

#[test]
fn ordered_world_replacement_resets_actor_metadata_and_allows_later_activation_with_or_without_ne()
{
    for with_ne in [false, true] {
        let script = Script::new();
        script.edit(&payload_policy("clip_b", "sound_b"));
        let (Fixture { server, send, .. }, mut one, mut two) = fixture(&script);
        let addr = server.admin_addr().unwrap();
        let initial = vec!["1|A|7|1|7|8|9|-2".into(), sound(2, "sound_a")];
        send.send((false, initial.clone())).unwrap();
        assert_eq!(one.animation(2), (initial, 1));
        assert_eq!(
            two.animation(2),
            (vec!["1|A|7|1|7|8|9|-2".into(), sound(2, "sound_b")], 1)
        );
        let reply = exchange(
            &mut connect(addr),
            if with_ne {
                "LOAD_SAVE 796573"
            } else {
                "LOAD_SAVE 6E6F"
            },
        );
        assert!(reply.contains("\"accepted\":true"), "{reply}");
        if with_ne {
            assert_eq!(one.animation(1), (vec!["0|Ne|2".into()], 1));
            assert_eq!(two.animation(1), (vec!["0|Ne|2".into()], 1));
        }
        let new = script.edit(&payload_policy("clip_c", "sound_c"));
        wait_status(addr, |s| s.contains(&format!("active_sha256={new}")));
        let next = vec![ps(3, 7, "clip_a"), sound(0, "sound_a")];
        send.send((false, next.clone())).unwrap();
        assert_eq!(one.animation(2), (next, 1));
        assert_eq!(
            two.animation(2),
            (vec![ps(3, 7, "clip_a"), sound(0, "sound_c")], 1)
        );
        server.shutdown().unwrap();
    }
}
#[test]
fn real_ne_record_resets_only_machine_bindings_at_its_position_in_a_mixed_batch() {
    let script = Script::new();
    script.edit(&payload_policy("clip_b", "sound_b"));
    let (Fixture { server, send, .. }, mut one, mut two) = fixture(&script);
    let raw = vec![
        "1|A|7|1|7|8|9|-2".into(),
        ps(2, 7, "clip_a"),
        "0|Ne|2".into(),
        ps(3, 7, "clip_a"),
        "4|A|7|1|7|8|9|-2".into(),
        ps(5, 7, "clip_a"),
    ];
    send.send((false, raw.clone())).unwrap();
    assert_eq!(one.animation(6), (raw.clone(), 1));
    let mut mapped = raw;
    mapped[1] = ps(2, 7, "clip_b");
    mapped[5] = ps(5, 7, "clip_b");
    assert_eq!(two.animation(6), (mapped, 1));
    server.shutdown().unwrap();
}

#[test]
fn overflowing_deferred_world_cut_stops_actor_without_publishing_held_prefix() {
    use std::io::Read;
    let script = Script::new();
    script.edit(&payload_policy("clip_b", "sound_b"));
    let (Fixture { server, .. }, mut one, mut two) = fixture(&script);
    let mut admin = connect(server.admin_addr().unwrap());
    writeln!(admin.get_mut(), "LOAD_SAVE 6F766572666C6F77").unwrap();
    let mut output = String::new();
    one.reader.read_to_string(&mut output).unwrap();
    assert!(
        !output.contains("|SimAnimation|"),
        "must not publish any held prefix: {output}"
    );
    output.clear();
    two.reader.read_to_string(&mut output).unwrap();
    assert!(!output.contains("|SimAnimation|"));
    let failure = server.shutdown().unwrap_err();
    assert!(
        failure
            .to_string()
            .contains("world replacement event hold capacity exceeded"),
        "{failure}"
    );
}

#[test]
fn default_off_world_marker_keeps_original_output_without_policy_holding_or_aliasing() {
    let script = Script::new();
    script.edit(&payload_policy("clip_b", "sound_b"));
    let (Fixture { server, .. }, mut one, mut two) = fixture_with_policy(&script, false);
    // More than the opt-in hold limit is legal existing driver output when policy is disabled.
    let reply = exchange(
        &mut connect(server.admin_addr().unwrap()),
        "LOAD_SAVE 6F766572666C6F77",
    );
    assert!(reply.contains("\"accepted\":true"));
    let exact = vec!["1|Ps|7|10|clip_a".to_string(); 1025];
    assert_eq!(one.animation(1025), (exact.clone(), 1025));
    assert_eq!(two.animation(1025), (exact, 1025));
    assert!(status(server.admin_addr().unwrap()).contains("enabled=0"));
    server.shutdown().unwrap();
}

#[test]
fn recorder_uppercase_audio_names_map_without_widening_public_hex_codec() {
    let script = Script::new();
    script.edit(&payload_policy("clip_b", "sound_b"));
    let (Fixture { server, send, .. }, mut one, mut two) = fixture(&script);
    let source = "1|Au|18446744073709551615|o|55|736F756E645F61|1".to_string();
    assert!(
        m::hex_decode("736F756E645F61").is_err(),
        "public scalar codec stays lowercase-only"
    );
    send.send((false, vec![source.clone()])).unwrap();
    assert_eq!(one.animation(1), (vec![source], 1));
    assert_eq!(two.animation(1), (vec![sound(1, "sound_b")], 1));
    let before = hash(&payload_policy("clip_b", "sound_b"));
    let next = script.edit(&payload_policy("clip_c", "sound_c"));
    assert_ne!(before, next);
    wait_status(server.admin_addr().unwrap(), |s| {
        s.contains(&format!("active_sha256={next}"))
    });
    send.send((
        false,
        vec!["0|Au|18446744073709551615|o|55|736F756E645F61|1".into()],
    ))
    .unwrap();
    assert_eq!(two.animation(1), (vec![sound(0, "sound_b")], 1));
    let stop = "2|Au|18446744073709551615|x|55".to_string();
    send.send((false, vec![stop.clone()])).unwrap();
    assert_eq!(two.animation(1), (vec![stop], 1));
    assert!(status(server.admin_addr().unwrap()).contains("malformed_records=0"));
    let mixed = "3|Au|18446744073709551615|o|55|736f756e645F61|1".to_string();
    let property = "4|Au|18446744073709551615|s|55|0|0.5".to_string();
    let stop = "5|Au|18446744073709551615|x|55".to_string();
    send.send((false, vec![mixed, property.clone(), stop.clone()]))
        .unwrap();
    assert_eq!(
        two.animation(3),
        (vec![sound(3, "sound_c"), property, stop], 1)
    );
    // Invalid UTF-8 is exact source passthrough and invalidates that uncertain lifetime.
    let invalid = "6|Au|18446744073709551615|o|55|FF|1".to_string();
    send.send((false, vec![invalid.clone()])).unwrap();
    assert_eq!(two.animation(1), (vec![invalid], 1));
    send.send((false, vec![sound(0, "sound_a")])).unwrap();
    assert_eq!(two.animation(1), (vec![sound(0, "sound_a")], 1));
    assert!(status(server.admin_addr().unwrap()).contains("malformed_records=2"));
    server.shutdown().unwrap();
}

#[test]
fn native_w_names_flags_and_malformed_name_lifetimes_keep_exact_delivery() {
    let script = Script::new();
    let policy = |to: &str| payload_policy("clip_b", to).replace("op:\"o\"", "op:\"w\"");
    script.edit(&policy("sound_b"));
    let (Fixture { server, send, .. }, mut one, mut two) = fixture(&script);
    let start =
        |seq: u64, name: &str| format!("{seq}|Au|18446744073709551615|w|55|{name}|4294967295");
    let native = start(1, "736f756e645F61");
    send.send((false, vec![native.clone()])).unwrap();
    assert_eq!(one.animation(1), (vec![native], 1));
    assert_eq!(
        two.animation(1),
        (vec![start(1, &m::hex_encode("sound_b"))], 1)
    );
    let next = script.edit(&policy("sound_c"));
    wait_status(server.admin_addr().unwrap(), |s| {
        s.contains(&format!("active_sha256={next}"))
    });
    send.send((false, vec![start(0, "736F756E645F61")]))
        .unwrap();
    assert_eq!(
        two.animation(1),
        (vec![start(0, &m::hex_encode("sound_b"))], 1)
    );
    let property = "2|Au|18446744073709551615|s|55|0|0.5".to_string();
    let stop = "3|Au|18446744073709551615|x|55".to_string();
    send.send((
        false,
        vec![property.clone(), stop.clone(), start(4, "736F756E645F61")],
    ))
    .unwrap();
    assert_eq!(
        two.animation(3),
        (vec![property, stop, start(4, &m::hex_encode("sound_c"))], 1)
    );
    // Each malformed start replaces an already-known handle in native delivery.
    // Forward its bytes, clear the old alias, and keep any later synthetic source unchanged.
    for (i, bad) in ["0", "GG", "FF", "C328"].into_iter().enumerate() {
        let bad = start(10 + i as u64 * 2, bad);
        send.send((false, vec![bad.clone()])).unwrap();
        assert_eq!(two.animation(1), (vec![bad], 1));
        let synthetic = start(0, "736F756E645F61");
        send.send((false, vec![synthetic.clone()])).unwrap();
        assert_eq!(two.animation(1), (vec![synthetic], 1));
        send.send((false, vec![start(11 + i as u64 * 2, "736F756E645F61")]))
            .unwrap();
        assert_eq!(
            two.animation(1),
            (vec![start(11 + i as u64 * 2, &m::hex_encode("sound_c"))], 1)
        );
    }
    assert!(status(server.admin_addr().unwrap()).contains("malformed_records=8"));
    server.shutdown().unwrap();
}
