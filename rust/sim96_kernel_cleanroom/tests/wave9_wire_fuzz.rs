#![forbid(unsafe_code)]
//! Card 43 deterministic deployed-daemon campaign. Keep failing counterexamples:
//! an unsupported verb is not evidence that its authority gate works.
use std::collections::BTreeMap;
use std::fs::{self, File};
use std::io::{BufRead, BufReader, Write};
use std::net::{Shutdown, TcpStream};
use std::path::PathBuf;
use std::thread;
use std::time::{Duration, Instant};

use sim96_kernel_cleanroom::continuity::run_continuity_check;
use sim96_kernel_cleanroom::runtime_bridge::{AuthorityDaemon, AuthorityWorld};
use sim96_kernel_cleanroom::wan_transport::RoleTag;
use sim96_kernel_cleanroom::wire::PlayerId;

const VECTORS: usize = 10_000;
const SEED: u64 = 0x4334_3839_dead_beef;
const CLASSES: [&str; 12] = [
    "v11_header", "truncated", "oversized", "missing_env",
    "rejected_env", "duplicate", "stale_epoch", "unowned_claim",
    "unauthorized_insertion", "funds_bounds", "stale_handle", "version_skew",
];

struct SplitMix64(u64);
impl SplitMix64 {
    fn next(&mut self) -> u64 {
        self.0 = self.0.wrapping_add(0x9e37_79b9_7f4a_7c15);
        let mut z = self.0;
        z = (z ^ (z >> 30)).wrapping_mul(0xbf58_476d_1ce4_e5b9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94d0_49bb_1331_11eb);
        z ^ (z >> 31)
    }
}

struct Vector {
    class: usize,
    corr: u64,
    peer: usize,
    line: String,
    expected: String,
}

struct Peer {
    stream: TcpStream,
    reader: BufReader<TcpStream>,
    ticks: Vec<u64>,
}
impl Peer {
    fn new(addr: std::net::SocketAddr) -> Self {
        let stream = TcpStream::connect(addr).unwrap();
        stream.set_nodelay(true).unwrap();
        stream.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
        stream.set_write_timeout(Some(Duration::from_secs(10))).unwrap();
        Self { reader: BufReader::new(stream.try_clone().unwrap()), stream, ticks: Vec::new() }
    }

    fn batch(&mut self, input: &str, fence: u64) -> Vec<String> {
        writeln!(self.stream, "{input}PING|{fence}").unwrap();
        self.stream.flush().unwrap();
        let mut rows = Vec::new();
        let sentinel = format!("PONG|{fence}");
        loop {
            let mut row = String::new();
            assert_ne!(self.reader.read_line(&mut row).unwrap(), 0, "EOF before {sentinel}");
            let row = row.trim_end().to_owned();
            if let Some(tick) = row.strip_prefix("AS|")
                .and_then(|s| s.split('|').next()).and_then(|s| s.parse().ok()) {
                self.ticks.push(tick);
            }
            if row == sentinel { break; }
            rows.push(row);
        }
        rows
    }

    fn observe_tick_after(&mut self, before: u64) {
        while self.ticks.last().copied().unwrap_or(0) <= before {
            let mut row = String::new();
            assert_ne!(self.reader.read_line(&mut row).unwrap(), 0, "no tick after {before}");
            if let Some(tick) = row.trim_end().strip_prefix("AS|")
                .and_then(|s| s.split('|').next()).and_then(|s| s.parse().ok()) {
                self.ticks.push(tick);
            }
        }
    }
}

fn artifact_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../.omo/run/wave7_scenario_probe")
}

// All generated text is ASCII; escaping these characters yields JSON strings.
fn json(s: &str) -> String {
    format!("\"{}\"", s.replace('\\', "\\\\").replace('"', "\\\"")
        .replace('\n', "\\n").replace('\r', "\\r"))
}

fn vectors() -> Vec<Vector> {
    let mut rng = SplitMix64(SEED);
    (0..VECTORS).map(|i| {
        let class = i * CLASSES.len() / VECTORS;
        let r = rng.next();
        let corr = 100 + u64::try_from(i).unwrap();
        let (peer, line, expected) = match class {
            0 => (0, format!("BridgeLine|v11|{}|{}|{}|{}", r % 32, r, r >> 32, r % 256),
                "ERR|0|bad_line".to_owned()),
            1 => (0, format!("CMD|{corr}|EnqueueInteraction|{}|Sit|server", r % 100),
                format!("ERR|{corr}|bad_line")),
            // Leading zeros are legal integer text. A parser which only
            // checks numeric range will APPLY this >4080-byte frame.
            2 => (0, format!("CMD|{corr}|ClaimSim|{}1|100", "0".repeat(4_081)),
                format!("ERR|{corr}|bad_line")),
            3 => (2, format!("CMD|{corr}|ClaimSim|{}|100", 100 + r % 100),
                "ERR|0|bad_line|say_hello_first".to_owned()),
            4 => (3, format!("CMD|{corr}|ClaimSim|{}|200", 200 + r % 100),
                format!("ERR|{corr}|hello_rejected")),
            // All duplicates are contiguous and have one CommandId. Seed the
            // original immediately before this block, inside the 32-slot window.
            5 => (0, "CMD|4267|RouteSlotClaim|1|8|0|1".to_owned(),
                "ACK|4267|RouteSlotClaim|1|8|0|tick=".to_owned()),
            6 => (0, format!("CMD|{corr}|LotEditTx|100|0|{}|0|0", r % 100),
                format!("ERR|{corr}|StaleEpoch")),
            7 => match r % 3 {
                0 => (1, format!("CMD|{corr}|ClaimSim|1|100"),
                    format!("ERR|{corr}|UnauthorizedClientInsertion|ClaimSim")),
                1 => (1, format!("CMD|{corr}|EnqueueInteraction|1|SocializeWith|server|2|100"),
                    format!("ERR|{corr}|UnauthorizedClientInsertion|SocializeWith")),
                _ => (1, format!("CMD|{corr}|SwapClaim|1|2"),
                    format!("ERR|{corr}|UnauthorizedClientInsertion|SwapClaim")),
            },
            8 => match r % 6 {
                0 => (0, format!("CMD|{corr}|EnqueueInteraction|1|BedSleep|client|0|100"),
                    format!("ERR|{corr}|UnauthorizedClientInsertion|BedSleep")),
                1 => (0, format!("CMD|{corr}|EnqueueInteraction|1|RouteAway|client|0|100"),
                    format!("ERR|{corr}|UnauthorizedClientInsertion|RouteAway")),
                2 => (0, format!("CMD|{corr}|SpawnService|Fire"),
                    format!("ERR|{corr}|ClientWorldServiceSuppressed|Fire")),
                3 => (0, format!("CMD|{corr}|EnqueueInteraction|1|BedSleep|server|0|100"),
                    format!("ERR|{corr}|UnauthorizedClientInsertion|BedSleep")),
                4 => (0, format!("CMD|{corr}|EnqueueInteraction|1|ReactionInteraction|client|0|100"),
                    format!("ERR|{corr}|UnauthorizedClientInsertion|ReactionInteraction")),
                _ => (0, format!("CMD|{corr}|EnqueueInteraction|1|Sit|rng|0|100"),
                    format!("ERR|{corr}|UnauthorizedClientInsertion|Sit")),
            },
            9 => (0, format!("CMD|{corr}|ModifyFunds|100|{}|purchase",
                if r & 1 == 0 { i32::MAX } else { i32::MIN }),
                format!("ERR|{corr}|funds|")),
            10 => (0, format!("CMD|{corr}|DerefHandle|0|4294967295"),
                format!("ERR|{corr}|StaleObjectHandle")),
            11 => (0, format!("HELLO|{corr}|1|1|{}|{}|client", r % 17, r % 15),
                format!("ERR|{corr}|hello_rejected")),
            _ => unreachable!(),
        };
        Vector { class, corr: if class == 5 { 4267 } else { corr }, peer, line, expected }
    }).collect()
}

#[test]
fn adversarial_wire_vectors_preserve_authority_and_tick_progress() {
    // Given: the real runtime daemon, four isolated TCP sessions, fixed seed.
    fs::create_dir_all(artifact_dir()).unwrap();
    let mut inputs = File::create(artifact_dir().join("wave9-inputs.jsonl")).unwrap();
    let mut results = File::create(artifact_dir().join("wave9-results.jsonl")).unwrap();
    let vectors = vectors();
    assert_eq!(vectors.len(), VECTORS);
    let mut daemon = AuthorityDaemon::bind(([127, 0, 0, 1], 0).into()).unwrap();
    daemon.ticks = 2_000;
    daemon.max_connections = 4;
    let addr = daemon.local_addr().unwrap();
    let started = Instant::now();
    let server = thread::spawn(move || daemon.serve());
    let mut peers = Vec::new();
    for i in 0..4 {
        let mut p = Peer::new(addr);
        // Establish allocation order explicitly, except the missing-env peer.
        if i != 2 {
            let hello = if i == 3 { "HELLO|1|0|1|17|15|client2\n" }
                else { "HELLO|1|1|1|17|15|client\n" };
            let rows = p.batch(hello, 90_000 + i);
            assert!(rows.iter().any(|s| s.starts_with(if i == 3 {
                "ERR|1|hello_rejected" } else { "WELCOME|1|" })));
        }
        peers.push(p);
    }
    peers[0].batch("CMD|2|ClaimSim|1|100\nCMD|3|AcquireLotEdit|100\n", 91_000);
    peers[1].batch("CMD|2|ClaimSim|2|200\n", 91_001);
    let mut replies: Vec<Vec<String>> = (0..4).map(|_| Vec::new()).collect();
    let mut counts = [0_usize; 12];
    let mut state_corruptions = 0;
    // When: pipeline each class without sleeps; the ordered PONG fences prove
    // all preceding input was handled, regardless of scheduler timing.
    for class in 0..CLASSES.len() {
        if class == 5 {
            // A legitimate seed effect is not part of the adversarial burst.
            peers[0].batch("CMD|4267|RouteSlotClaim|1|8|0|1\n", 91_005);
        }
        let state_before = peers[1].batch("STATE|1\n", 91_006)
            .into_iter().find(|s| s.starts_with("STATE|1|")).unwrap();
        let before_tick = peers[1].ticks.last().copied().unwrap_or(0);
        let group: Vec<_> = vectors.iter().filter(|v| v.class == class).collect();
        let peer = group[0].peer;
        let mut batch = String::new();
        for v in &group {
            writeln!(inputs,
                "{{\"seed\":\"{SEED:016x}\",\"class\":{},\"corr\":{},\"peer\":{},\"input\":{},\"expected\":{}}}",
                json(CLASSES[class]), v.corr, v.peer, json(&v.line), json(&v.expected)).unwrap();
            batch.push_str(&v.line);
            batch.push('\n');
            counts[class] += 1;
        }
        if class == 3 {
            // No command before this HELLO may have been applied.
            batch.push_str("HELLO|99|1|1|17|15|client\n");
        }
        replies[peer].extend(peers[peer].batch(&batch, 92_000 + u64::try_from(class).unwrap()));
        let state_after = peers[1].batch("STATE|1\n", 91_007)
            .into_iter().find(|s| s.starts_with("STATE|1|")).unwrap();
        state_corruptions += usize::from(state_before != state_after);
        assert!(state_after.contains("ledger_ok=true"));
        peers[1].observe_tick_after(before_tick);
        writeln!(results, "{{\"event\":\"state_comparison\",\"class\":{},\"before\":{},\"after\":{},\"unchanged\":{}}}",
            json(CLASSES[class]), json(&state_before), json(&state_after), state_before == state_after).unwrap();
    }
    // Then: check all vectors before failing, preserving all counterexamples.
    let mut failures = [0_usize; 12];
    let mut available: Vec<BTreeMap<String, usize>> = (0..4).map(|_| BTreeMap::new()).collect();
    let mut consumed: Vec<BTreeMap<String, usize>> = (0..4).map(|_| BTreeMap::new()).collect();
    for (peer, lines) in replies.iter().enumerate() {
        for line in lines { *available[peer].entry(line.clone()).or_default() += 1; }
    }
    for v in &vectors {
        let matching: usize = available[v.peer].range(v.expected.clone()..)
            .take_while(|(line, _)| line.starts_with(&v.expected)).map(|(_, n)| n).sum();
        let used = consumed[v.peer].entry(v.expected.clone()).or_default();
        let ok = *used < matching;
        *used += 1;
        failures[v.class] += usize::from(!ok);
        writeln!(results, "{{\"class\":{},\"corr\":{},\"passed\":{}}}",
            json(CLASSES[v.class]), v.corr, ok).unwrap();
    }
    for (peer, lines) in replies.iter().enumerate() {
        for line in lines {
            writeln!(results, "{{\"event\":\"wire_reply\",\"peer\":{peer},\"frame\":{}}}", json(line)).unwrap();
        }
    }
    let duplicate_count = replies[0].iter()
        .filter(|s| s.starts_with("ACK|4267|RouteSlotClaim|")).count();
    assert_eq!(duplicate_count, counts[5], "one ORIGINAL result per duplicate");
    // Direct-core state comparison supplements (not substitutes for) the
    // socket verdicts: failed ownership must not overwrite the owner's map.
    let mut world = AuthorityWorld::new();
    let mut rows = Vec::new();
    world.handle_line(PlayerId(1), RoleTag::Client, "CMD|1|ClaimSim|1|100", &mut rows);
    let owners_before = world.authority.owners.clone();
    rows.clear();
    world.handle_line(PlayerId(2), RoleTag::Client2, "CMD|1|ClaimSim|1|100", &mut rows);
    let ownership_intact = world.authority.owners == owners_before;
    // Continuity is an independent reference-world check, NOT a hash of the
    // daemon under attack. Report that limit instead of claiming 0 corruption.
    let continuity = run_continuity_check(0x4334_3839, 500, 1_000);
    assert!(continuity.bit_identical);
    let mut tick_sequences = Vec::new();
    for peer in &mut peers {
        let rows = peer.batch("", 99_999);
        tick_sequences.extend(rows);
        peer.stream.shutdown(Shutdown::Both).unwrap();
    }
    let contiguous_ticks = peers[1].ticks.windows(2).all(|pair| pair[1] == pair[0] + 1);
    let report = server.join().expect("daemon panicked").unwrap();
    let elapsed = started.elapsed().as_secs_f64();
    let tick_rate = f64::from(u32::try_from(report.ticks_committed).unwrap()) / elapsed;
    let ticks: Vec<u64> = replies[0].iter().chain(tick_sequences.iter())
        .filter_map(|s| s.strip_prefix("AS|")?.split('|').next()?.parse().ok()).collect();
    let progressed = ticks.iter().min().zip(ticks.iter().max()).is_some_and(|(lo, hi)| hi > lo);
    let total_failures: usize = failures.iter().sum();
    for (i, name) in CLASSES.iter().enumerate() {
        writeln!(results, "{{\"event\":\"class_summary\",\"class\":{},\"vectors\":{},\"failed\":{}}}",
            json(name), counts[i], failures[i]).unwrap();
        eprintln!("{name}: vectors={} failed={}", counts[i], failures[i]);
    }
    writeln!(results,
        "{{\"event\":\"summary\",\"vectors\":{VECTORS},\"failed\":{total_failures},\"state_corruptions\":{state_corruptions},\"ownership_intact\":{ownership_intact},\"contiguous_ticks\":{contiguous_ticks},\"reference_continuity\":{},\"ticks\":{},\"ticks_per_second\":{tick_rate},\"dropped_ticks_during_join\":{},\"observed_progress\":{progressed}}}",
        continuity.bit_identical, report.ticks_committed, report.dropped_ticks_during_join).unwrap();
    results.sync_all().unwrap();
    inputs.sync_all().unwrap();
    assert_eq!(report.ticks_committed, 2_000);
    assert_eq!(report.dropped_ticks_during_join, 0);
    assert!(progressed, "no AS tick progress observed");
    assert!(contiguous_ticks, "tick loss during the wire campaign");
    assert!(tick_rate >= 60.0, "tick throughput {tick_rate} <60/s");
    assert!(total_failures == 0 && ownership_intact && state_corruptions == 0,
        "{total_failures}/{VECTORS} unsafe or unsupported wire verdicts; ownership_intact={ownership_intact}; see wave9-results.jsonl");
}
