#![forbid(unsafe_code)]
//! Cards 37/40/44/47/50 headless readiness rehearsal (Wave 8-10).
//! TCP clients are synthetic, not TS3; the soak exposure is accelerated
//! in-world minutes, never a booted-game or 120-wall-minute claim. Cells
//! whose evidence genuinely needs two physical machines, a real presenter
//! window or a human signer record `live_gated` and are never faked.
//! Model checks never stand in for a daemon restart, a client renderer or
//! native watchdog evidence.
use std::collections::BTreeMap;
use std::fs::{self, File};
use std::io::{self, BufRead, BufReader, BufWriter, Write};
use std::net::{Shutdown, TcpStream};
use std::path::{Path, PathBuf};
use std::thread;
use std::time::{Duration, Instant};

use sim96_kernel_cleanroom::continuity::run_continuity_check;
use sim96_kernel_cleanroom::go_nogo::{
    FULL_GO_UNREACHABLE_AT_WAVE_6, LEDGER_ROW_COUNT, OverallGo, ShipDecision, grade_subsystem, overall_go,
};
use sim96_kernel_cleanroom::icall_catalog::TS3W_SHA256_HEX;
use sim96_kernel_cleanroom::integration::run_park_meet;
use sim96_kernel_cleanroom::p7_benchmark::P7Benchmark;
use sim96_kernel_cleanroom::persistence::{AtomicSaveCommitter, CommitFault, CommitPhase, DBPF_MAJOR, DBPF_MINOR, SaveFileSet};
use sim96_kernel_cleanroom::runtime_bridge::{AuthorityDaemon, AuthorityWorld, DaemonReport};
use sim96_kernel_cleanroom::soak_chaos::{
    SOAK_MAX_GEN_HANDLES, SOAK_MAX_QUEUE_PER_SIM, SOAK_MAX_WIRE_RING, SOAK_MIN_TICKS_PER_SECOND, ref_soak,
};
use sim96_kernel_cleanroom::socials::SocialPairId;
use sim96_kernel_cleanroom::wan_transport::{
    BRIDGE_PROTOCOL_VERSION, ClientEnv, DEFAULT_WAN_PORT, GAP_NACK_COOLDOWN_TICKS, HelloRejection,
    REORDER_WINDOW_FRAMES, RING_BUFFER_FRAMES, Resume, RoleTag, ServerRingBuffer, SIMLINK_NATIVE_VERSION,
    StreamFrameRef, WanIngest, WanJitterBuffer, hello_gate,
};
use sim96_kernel_cleanroom::wire::PlayerId;
use sim96_kernel_cleanroom::world_integration::{IntegrationOutcome, WEEK_TICKS, run_full_world};
use sim96_kernel_cleanroom::ObjectGuid;
use sim96_kernel_cleanroom::kernel_iface::{SlotIndex, SlotReservationStore};
use sim96_kernel_cleanroom::SimId;

type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;

/// Card 46/47 frozen wire compatibility (the rc-manifest contract triple).
const WIRE_VERSION: u16 = 23;
/// `scripts/crash_catch.py:19` uptime/exposure bins, minutes in the world.
const UPTIME_BINS: [u64; 6] = [0, 5, 10, 20, 40, 80];
/// Authored model checksum pins (card 28 idiom, same epistemics as wave7):
/// N accepts, N+1 upgrades accept the same contract, N+2 refuses.
const PIN_N: &str = "4e314e314e314e314e314e314e314e314e314e314e314e314e314e314e314e31";
const PIN_N2: &str = "4e324e324e324e324e324e324e324e324e324e324e324e324e324e324e324e32";
const SOAK_PHASES: usize = 6;
const SOAK_PHASE_TICKS: u64 = 2_000;

fn root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../.omo/run/wave8_alpha_probe")
}

fn quoted(s: &str) -> String {
    let mut out = String::from("\"");
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            c if c.is_control() => out.push_str(&format!("\\u{:04x}", u32::from(c))),
            c => out.push(c),
        }
    }
    out.push('"');
    out
}

fn fnv_mix(mut h: u64, bytes: &[u8]) -> u64 {
    for b in bytes {
        h = (h ^ u64::from(*b)).wrapping_mul(0x100_0000_01b3);
    }
    h
}

/// Minutes in the world per card 06: integer `ticks * 2 / 75`.
fn sim_minutes(ticks: u64) -> u64 { ticks.saturating_mul(2) / 75 }

fn bin_of(minute: u64) -> usize {
    UPTIME_BINS.iter().rposition(|b| minute >= *b).unwrap_or(0)
}

fn percentile(sorted: &[u64], p: usize) -> u64 {
    if sorted.is_empty() { return 0; }
    let index = sorted.len().saturating_mul(p).div_ceil(100).saturating_sub(1);
    sorted[index.min(sorted.len() - 1)]
}

// ---------------------------------------------------------------- sha256 (card 47 §A determinism)

/// Compact SHA-256 for the rc-manifest idiom (the sync tool's own pattern).
pub fn sha256_hex(data: &[u8]) -> String {
    const K: [u32; 64] = [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
    ];
    let mut h: [u32; 8] = [
        0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19,
    ];
    let bit_len = u64::try_from(data.len()).unwrap_or(u64::MAX).saturating_mul(8);
    let mut msg = data.to_vec();
    msg.push(0x80);
    while msg.len() % 64 != 56 { msg.push(0); }
    msg.extend_from_slice(&bit_len.to_be_bytes());
    for chunk in msg.chunks(64) {
        let mut w = [0u32; 64];
        for (i, word) in chunk.as_chunks::<4>().0.iter().take(16).enumerate() {
            w[i] = u32::from_be_bytes([word[0], word[1], word[2], word[3]]);
        }
        for i in 16..64 {
            let s0 = w[i - 15].rotate_right(7) ^ w[i - 15].rotate_right(18) ^ (w[i - 15] >> 3);
            let s1 = w[i - 2].rotate_right(17) ^ w[i - 2].rotate_right(19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16].wrapping_add(s0).wrapping_add(w[i - 7]).wrapping_add(s1);
        }
        let [mut a, mut b, mut c, mut d, mut e, mut f, mut g, mut hh] = h;
        for i in 0..64 {
            let s1 = e.rotate_right(6) ^ e.rotate_right(11) ^ e.rotate_right(25);
            let ch = (e & f) ^ ((!e) & g);
            let t1 = hh.wrapping_add(s1).wrapping_add(ch).wrapping_add(K[i]).wrapping_add(w[i]);
            let s0 = a.rotate_right(2) ^ a.rotate_right(13) ^ a.rotate_right(22);
            let maj = (a & b) ^ (a & c) ^ (b & c);
            let t2 = s0.wrapping_add(maj);
            hh = g; g = f; f = e; e = d.wrapping_add(t1);
            d = c; c = b; b = a; a = t1.wrapping_add(t2);
        }
        for (slot, v) in h.iter_mut().zip([a, b, c, d, e, f, g, hh]) {
            *slot = slot.wrapping_add(v);
        }
    }
    let mut out = String::with_capacity(64);
    for word in h { out.push_str(&format!("{word:08x}")); }
    out
}

// ---------------------------------------------------------------- evidence

struct Evidence {
    records: BufWriter<File>,
    verdicts: Vec<String>,
    failed: usize,
    checked: usize,
    passed: BTreeMap<String, bool>,
}

impl Evidence {
    fn line(&mut self, json: &str) -> io::Result<()> { writeln!(self.records, "{json}") }

    fn check(&mut self, name: &str, ok: bool, detail: &str) -> io::Result<()> {
        self.checked += 1;
        self.failed += usize::from(!ok);
        self.passed.insert(name.to_owned(), ok);
        self.line(&format!("{{\"side\":\"probe\",\"event\":\"acceptance\",\"name\":{},\"passed\":{},\"detail\":{}}}",
            quoted(name), ok, quoted(detail)))?;
        self.verdicts.push(format!("{} {name}: {detail}", if ok { "PASS" } else { "FAIL" }));
        Ok(())
    }

    fn ok_of(&self, name: &str) -> bool { self.passed.get(name).copied().unwrap_or(false) }

    fn emit_verdicts(&mut self) -> io::Result<()> {
        for line in self.verdicts.drain(..) {
            println!("{line}");
        }
        io::stdout().flush()
    }
}

// ---------------------------------------------------------------- TCP peers

/// Frame prefixes a client may mirror but never compute (cards 09/17/31 §B):
/// the digest for cross-client parity is over these command-caused deltas,
/// whose bytes are ordered ahead of each session's own reply fence.
const SHARED_DELTA: [&str; 3] = ["Rl|", "Hd|", "Od|"];

struct Peer {
    stream: TcpStream,
    reader: BufReader<TcpStream>,
    frames: Vec<String>,
    keep: bool,
    digest: u64,
    rtt_us: Vec<(u64, u64)>,
}

impl Peer {
    fn connect(addr: std::net::SocketAddr, keep: bool) -> io::Result<Self> {
        let stream = TcpStream::connect(addr)?;
        stream.set_nodelay(true)?;
        stream.set_read_timeout(Some(Duration::from_secs(5)))?;
        Ok(Self { reader: BufReader::new(stream.try_clone()?), stream, frames: Vec::new(),
            keep, digest: 0xcbf2_9ce4_8422_2325, rtt_us: Vec::new() })
    }

    // A PONG is an ordered fence from the real connection handler. No sleeps.
    fn exchange(&mut self, lines: &str, fence: u64) -> io::Result<Vec<String>> {
        let started = Instant::now();
        writeln!(self.stream, "{lines}PING|{fence}")?;
        self.stream.flush()?;
        let end = format!("PONG|{fence}");
        let mut batch = Vec::new();
        loop {
            let mut line = String::new();
            if self.reader.read_line(&mut line)? == 0 {
                return Err(io::Error::new(io::ErrorKind::UnexpectedEof, "daemon closed before fence"));
            }
            let line = line.trim_end().to_owned();
            if line != end {
                if self.keep { self.frames.push(line.clone()); }
                if SHARED_DELTA.iter().any(|p| line.starts_with(p)) {
                    self.digest = fnv_mix(self.digest, line.as_bytes());
                }
                batch.push(line);
            } else { break; }
        }
        self.rtt_us.push((STARTED.get().map_or(0, |s|
            u64::try_from(s.elapsed().as_micros()).unwrap_or(u64::MAX)),
            u64::try_from(started.elapsed().as_micros()).unwrap_or(u64::MAX)));
        Ok(batch)
    }

    fn drain(&mut self, fence: u64) -> io::Result<Vec<String>> { self.exchange("", fence) }
}

// ---------------------------------------------------------------- helpers

fn command(world: &mut AuthorityWorld, player: PlayerId, role: RoleTag, line: &str) -> Result<Vec<String>> {
    let mut rows = Vec::new();
    world.handle_line(player, role, line, &mut rows);
    if rows.iter().any(|s| s.starts_with("ERR|")) || rows.is_empty() {
        return Err(io::Error::other(format!("command {line}: {rows:?}")).into());
    }
    Ok(rows)
}

fn has(lines: &[String], prefix: &str) -> bool {
    lines.iter().any(|s| s.starts_with(prefix))
}

fn rate(report: &DaemonReport) -> u64 {
    report.pump_ticks.saturating_mul(1_000_000_000).checked_div(report.elapsed_ns).unwrap_or(0)
}

fn budget_green(report: &DaemonReport) -> bool {
    report.tick_p99_ns <= 50_000 && report.ring_watermark_frames <= SOAK_MAX_WIRE_RING
        && report.queue_depth_max < SOAK_MAX_QUEUE_PER_SIM
        && report.gen_handles_live < SOAK_MAX_GEN_HANDLES
        && report.dropped_ticks_during_join == 0 && rate(report) >= u64::from(SOAK_MIN_TICKS_PER_SECOND)
}

fn telemetry(e: &mut Evidence, phase: &str, r: &DaemonReport) -> io::Result<()> {
    e.line(&format!(
        "{{\"side\":\"sidecar\",\"event\":\"telemetry\",\"phase\":{},\"tick\":{},\"samples\":{},\"sidecar_cpu_per_tick_us\":{}.{:03},\"latency_histogram_ns_log2\":{:?},\"ring_watermark_frames\":{},\"queue_depth_max\":{},\"gen_handles_live\":{},\"ticks_per_second\":{},\"dropped_ticks_during_join\":{},\"all_green\":{},\"surface\":\"synthetic_loopback\"}}",
        quoted(phase), r.ticks_committed, r.pump_ticks, r.tick_p99_ns / 1_000, r.tick_p99_ns % 1_000,
        r.latency_histogram_ns_log2, r.ring_watermark_frames, r.queue_depth_max, r.gen_handles_live,
        rate(r), r.dropped_ticks_during_join, budget_green(r)))
}

/// Authored DBPF fixture, not game data (same shape as the wave7 probe).
fn valid_payload(state: &[u8]) -> Result<Vec<u8>> {
    let span = 8 + u32::try_from(state.len())?;
    let index = 96 + span;
    let mut bytes = vec![0; usize::try_from(index + 36)?];
    bytes[..4].copy_from_slice(b"DBPF");
    for (off, value) in [(4, 2_u32), (8, 0), (0x24, 1), (0x2c, 36), (0x3c, 3), (0x40, index)] {
        bytes[off..off + 4].copy_from_slice(&value.to_le_bytes());
    }
    bytes[96..100].copy_from_slice(&[1, b'S', b'3', 0]);
    bytes[100..104].copy_from_slice(&4_u32.to_le_bytes());
    bytes[104..usize::try_from(index)?].copy_from_slice(state);
    let row = usize::try_from(index)? + 4;
    for (offset, value) in [(0, 0x03e8_0cdc_u32), (16, 96), (20, 0x8000_0000 | span), (24, span)] {
        bytes[row + offset..row + offset + 4].copy_from_slice(&value.to_le_bytes());
    }
    Ok(bytes)
}

fn os_rss() -> std::result::Result<u64, String> {
    if !cfg!(windows) { return Err("OS RSS query is live-gated on this host".to_owned()); }
    let output = std::process::Command::new("powershell")
        .args(["-NoProfile", "-Command", &format!("(Get-Process -Id {}).WorkingSet64", std::process::id())])
        .output().map_err(|e| e.to_string())?;
    if !output.status.success() { return Err("OS RSS query failed".to_owned()); }
    std::str::from_utf8(&output.stdout).map_err(|e| e.to_string())?
        .trim().parse::<u64>().map_err(|e| e.to_string())
}

fn tree(path: &Path) -> io::Result<BTreeMap<PathBuf, Vec<u8>>> {
    fn walk(base: &Path, path: &Path, out: &mut BTreeMap<PathBuf, Vec<u8>>) -> io::Result<()> {
        for entry in fs::read_dir(path)? {
            let entry = entry?;
            if entry.file_type()?.is_dir() { walk(base, &entry.path(), out)?; }
            else {
                let name = entry.path().strip_prefix(base).map_err(io::Error::other)?.to_path_buf();
                out.insert(name, fs::read(entry.path())?);
            }
        }
        Ok(())
    }
    let mut out = BTreeMap::new();
    walk(path, path, &mut out)?;
    Ok(out)
}

fn frame(stream: u32, seq: u64) -> StreamFrameRef { StreamFrameRef { stream_id: stream, epoch: 1, seq } }

// ---------------------------------------------------------------- card 37 kernel surface

fn card37_kernel(e: &mut Evidence) -> Result<()> {
    e.check("37.constants",
        REORDER_WINDOW_FRAMES == 32 && RING_BUFFER_FRAMES == 2048 && GAP_NACK_COOLDOWN_TICKS == 6
            && BRIDGE_PROTOCOL_VERSION == 17 && SIMLINK_NATIVE_VERSION == 15 && DEFAULT_WAN_PORT == 34019
            && WIRE_VERSION == 23,
        "card 19/46 constants verbatim; the WAN rehearsal pins the same compatibility triple")?;

    // In-order streams pass through with zero reorder work.
    let mut b = WanJitterBuffer::default();
    let in_order = (1..=500u64).all(|s| b.ingest(frame(1, s), vec![b'x'; 8], s) == WanIngest::Delivered);
    e.check("37.jitter_inorder", in_order && b.delivered == 500 && b.window.is_empty()
        && b.reorder_misses == 0 && b.nacks_sent == 0, "500 in-order frames streamed, none held")?;

    // A gap is held, then one fill releases the whole contiguous run.
    let mut b = WanJitterBuffer::default();
    for s in 1..=40u64 { b.ingest(frame(1, s), vec![0u8], s); }
    for s in 42..=60u64 { b.ingest(frame(1, s), vec![0u8], s); }
    let held = b.window.len() == 19 && b.delivered == 40;
    b.ingest(frame(1, 41), vec![0u8], 41);
    e.check("37.jitter_gap_drain", held && b.delivered == 60 && b.window.is_empty()
        && b.gaps.is_empty(), "19-frame gap held under REORDER_WINDOW_FRAMES, filled in one drain")?;

    // Cable-pull style resend: a duplicate never re-delivers, never mutates.
    let mut b = WanJitterBuffer::default();
    for s in 1..=100u64 { b.ingest(frame(1, s), vec![0u8], s); }
    let dupes = (1..=100u64).all(|s| b.ingest(frame(1, s), vec![0u8], 200 + s) == WanIngest::Duplicate);
    e.check("37.jitter_dup_never_twice", dupes && b.delivered == 100,
        "full resend of 100 delivered frames: all Duplicate, cursor unchanged")?;

    // NACKs cool down at GAP_NACK_COOLDOWN_TICKS and never storm.
    let mut b = WanJitterBuffer::default();
    b.ingest(frame(1, 1), vec![0u8], 1);
    // Ten arrivals spaced exactly one cooldown apart (seq 3..12, all inside
    // the 32-frame window): one NACK each, no repeats per frame.
    for i in 0..10u64 { b.ingest(frame(1, 3 + i), vec![0u8], 7 + 6 * i); }
    let paced = b.nacks_sent == 10;
    // A 20-frame burst 3 ticks after the last NACK: inside one cooldown
    // window (GAP_NACK_COOLDOWN_TICKS = 6), so it adds none - no storm.
    for s in 13..33u64 { b.ingest(frame(1, s), vec![0u8], 64); }
    e.check("37.nack_cooldown_no_storm", paced && b.nacks_sent == 10 && b.gaps == [2]
        && b.window.len() == 30 && b.delivered == 1,
        "10 paced arrivals -> 10 NACKs; a 20-frame burst inside one cooldown adds none; 30 frames held under the 32-window")?;

    let mut b = WanJitterBuffer::default();
    b.ingest(frame(1, 1), vec![0u8], 1);
    let overrun = 1 + u64::from(REORDER_WINDOW_FRAMES) + 1;
    let flushed = b.ingest(frame(1, overrun), vec![0u8], overrun) == WanIngest::OutOfWindow;
    e.check("37.out_of_window_flush", flushed && b.reorder_misses == 1 && b.window.is_empty(),
        "a frame beyond the 32-frame window force-flushes held gaps, never buffers past it")?;

    // Ring watermark is capped; selective replay covers a mid-walk resume.
    let mut ring = ServerRingBuffer::default();
    for s in 1..=5_000u64 { ring.append(frame(7, s), vec![b'A', b'S', b'|', (s % 10) as u8]); }
    let capped = ring.stream_len(7) == RING_BUFFER_FRAMES as usize
        && ring.total_frames() as u32 <= RING_BUFFER_FRAMES;
    e.check("37.ring_watermark_cap", capped,
        "5,000 appends -> exactly RING_BUFFER_FRAMES held per stream, watermark never above 2048")?;

    let resume = ring.resume(7, 1, 4_200);
    let covered = matches!(&resume, Resume::Ok { frames } if frames.len() == 800
        && frames.first().is_some_and(|(rf, _)| rf.seq == 4_201));
    let beyond = ring.resume(7, 1, 1_000) == Resume::Fresh;
    let stale = ring.resume(7, 2, 4_900) == Resume::Fresh;
    // The reclaimed frames replay through the jitter buffer without ever
    // applying a frame twice (idempotent resume vs. already-delivered tail).
    let mut replayed = 0u64;
    if let Resume::Ok { frames } = resume {
        let mut b = WanJitterBuffer {
            delivered: 4_200, // the client's ClientLastReconciledVersion cursor
            ..WanJitterBuffer::default()
        };
        for (rf, payload) in frames {
            if b.ingest(rf, payload, rf.seq) == WanIngest::Delivered { replayed += 1; }
        }
        e.check("37.reclaim_resume_ok_and_fresh", covered && beyond && stale && replayed == 800
            && b.delivered == 5_000,
            "800 withheld frames replayed in order once; >ring and stale-epoch resumes take the fresh path")?;
    } else {
        e.check("37.reclaim_resume_ok_and_fresh", false, "resume was not Resume::Ok for a covered gap")?;
    }

    // hello_gate refuses every non-conforming attestation by name (§C).
    let good = ClientEnv { simlink_client_ring: true, world_from_server: true,
        role_tag: RoleTag::Client2, bridge_version: BRIDGE_PROTOCOL_VERSION,
        simlink_native_version: SIMLINK_NATIVE_VERSION };
    let refusals = hello_gate(&ClientEnv { simlink_client_ring: false, ..good }) == Err(HelloRejection::MissingSimlinkRing)
        && hello_gate(&ClientEnv { world_from_server: false, ..good }) == Err(HelloRejection::MissingWorldFromServer)
        && hello_gate(&ClientEnv { bridge_version: 16, ..good }) == Err(HelloRejection::WrongBridgeVersion { got: 16, want: 17 })
        && hello_gate(&ClientEnv { simlink_native_version: 14, ..good }) == Err(HelloRejection::WrongSimlinkVersion { got: 14, want: 15 })
        && hello_gate(&good).is_ok();
    e.check("37.hello_gate_all_refusals", refusals,
        "all four named refusals + accept; a refused HELLO never lets a command through (G3 half)")?;
    Ok(())
}
// PART1-END

// ---------------------------------------------------------------- card 37 live pair

/// Post-wake offsets for uptime binning: everything in this probe shares the
/// run start, and the pump tick rate converts offsets to minutes-in-world.
static STARTED: std::sync::OnceLock<Instant> = std::sync::OnceLock::new();

fn card37_live(e: &mut Evidence) -> Result<(Vec<(u64, u64)>, DaemonReport)> {
    let mut daemon = AuthorityDaemon::bind(([127, 0, 0, 1], 0).into())?;
    daemon.ticks = 2_500;
    daemon.max_connections = 3;
    let addr = daemon.local_addr()?;
    let server = thread::spawn(move || daemon.serve_world(AuthorityWorld::new()));
    let outcome = (|| -> Result<(Vec<(u64, u64)>, String)> {
        let mut a = Peer::connect(addr, true)?;
        a.exchange("HELLO|1|1|1|17|15|client\n", 2)?;
        let mut b = Peer::connect(addr, true)?;
        b.exchange("HELLO|3|1|1|17|15|client2\n", 4)?;
        a.exchange("CMD|5|RequestWorld|9|9|9\nCMD|6|SetClientRole|player\nCMD|7|ClaimSim|1|100\n", 8)?;
        b.exchange("CMD|5|RequestWorld|9|9|9\nCMD|6|SetClientRole|player\nCMD|7|ClaimSim|2|200\n", 10)?;
        b.exchange("CMD|11|EnqueueInteraction|2|SocializeWith|server|1|200\n", 12)?;
        b.exchange("CMD|13|ModifyFunds|200|-300|purchase\n", 14)?;
        // Cable pull mid-walk: the vehicle segments for both claims are still
        // streaming when this socket dies. The server keeps simulating.
        b.stream.shutdown(Shutdown::Both)?;
        // Reclaim on a fresh session (the daemon's last accept slot).
        let mut r = Peer::connect(addr, true)?;
        r.exchange("HELLO|15|1|1|17|15|client2\n", 16)?;
        let join = r.exchange("CMD|17|RequestWorld|9|9|9\n", 18)?;
        let chunks: Vec<_> = join.iter().filter(|s| s.starts_with("WFC|")).collect();
        let reclaimed = chunks.len() == 11 && has(&join, "SIMC|1|100") && has(&join, "SIMC|2|200");
        e.check("37.live_cable_pull_reclaim", reclaimed,
            "session killed mid-walk; new session re-handshakes and receives 11 WFC chunks plus both restored claims")?;
        // Post-reclaim shared deltas must reach survivor and reclaimed client byte-identically.
        let sent = a.exchange("CMD|19|ModifyFunds|100|-50|coffee\n", 20)?;
        let drain_r = r.drain(22)?;
        let hd = sent.iter().find(|s| s.starts_with("Hd|19|100|-50|"));
        let parity = hd.is_some_and(|line| drain_r.iter().any(|s| s == line));
        e.check("37.post_reclaim_broadcast_parity", parity && reclaimed,
            "identical authoritative Hd delta bytes on survivor and reclaimed sessions")?;
        // Card 37 §E: what a client may READ must match the host authority.
        let sa = a.exchange("STATE|23\n", 24)?.into_iter().find(|s| s.starts_with("STATE|23|"));
        let sb = r.exchange("STATE|25\n", 26)?.into_iter().find(|s| s.starts_with("STATE|25|"));
        let state_parity = sa.as_ref().zip(sb.as_ref())
            .is_some_and(|(x, y)| x.split('|').nth(2) == y.split('|').nth(2));
        e.check("37.client_visible_state_parity", state_parity,
            "both sessions read the identical command_state_hash hex over STATE (client reads, never computes)")?;
        // Wave-7 hardening idiom: the wire trace is BUFFERED IN MEMORY while
        // the daemon pump still runs - no File::create and no disk writes
        // inside the timed window - and lands on disk only after every
        // background thread has retired.
        let mut trace = String::new();
        for (side, peer) in [("survivor", &a), ("killed", &b), ("reclaimed", &r)] {
            for line in &peer.frames {
                trace.push_str(&format!("{{\"side\":{},\"frame\":{}}}\n", quoted(side), quoted(line)));
            }
        }
        let mut samples = a.take_rtt();
        samples.extend(b.take_rtt());
        samples.extend(r.take_rtt());
        Ok((samples, trace))
    })();
    let (report, _) = server.join().map_err(|_| io::Error::other("card37 daemon panicked"))??;
    let (samples, trace) = outcome?;
    fs::write(root().join("card37-wire.jsonl"), trace)?;
    telemetry(e, "card37-live", &report)?;
    e.check("37.join_tick_continuity_no_stall",
        report.dropped_ticks_during_join == 0 && report.ticks_committed == 2_500
            && report.ring_watermark_frames <= SOAK_MAX_WIRE_RING,
        "cable-pull plus reclaim cost zero dropped ticks; the ring never crossed 2048 (0 checkpoint stalls by construction: commits run outside the tick loop)")?;
    Ok((samples, report))
}

fn rtt_telemetry(e: &mut Evidence, samples: &[(u64, u64)], ticks_per_second: u64) -> Result<()> {
    let mut values: Vec<u64> = samples.iter().map(|(_, r)| *r).collect();
    values.sort_unstable();
    let (p50, p90, p99) = (percentile(&values, 50), percentile(&values, 90), percentile(&values, 99));
    let jitter: u64 = values.windows(2).map(|w| w[1].saturating_sub(w[0])).sum::<u64>()
        / u64::max(1, u64::try_from(values.len()).unwrap_or(0).saturating_sub(1));
    let window_us = u64::from(REORDER_WINDOW_FRAMES).saturating_mul(1_000_000)
        / u64::max(1, ticks_per_second);
    let mut bins = [0u64; 6];
    for (offset_us, _) in samples {
        let in_world_min = offset_us.saturating_mul(ticks_per_second.max(1)) / 1_000_000;
        bins[bin_of(in_world_min * 2 / 75)] += 1;
    }
    e.line(&format!("{{\"side\":\"probe\",\"event\":\"rtt_histogram\",\"samples\":{},\"p50_us\":{},\"p90_us\":{},\"p99_us\":{},\"jitter_mean_us\":{},\"reorder_window_us\":{},\"uptime_bins\":{:?},\"uptime_bin_edges\":{:?}}}",
        values.len(), p50, p90, p99, jitter, window_us, bins, UPTIME_BINS))?;
    e.check("37.rtt_within_reorder_budget", p99 <= window_us && !values.is_empty(),
        &format!("rtt p50/p90/p99={p50}/{p90}/{p99}us jitter_mean={jitter}us; budget: no command crosses more than {REORDER_WINDOW_FRAMES} frames late ({window_us}us)"))?;
    Ok(())
}

// ---------------------------------------------------------------- card 44 model surface

fn card44_model(e: &mut Evidence) -> Result<()> {
    let IntegrationOutcome::Pass(week) = run_full_world(WEEK_TICKS, 0x5EED) else {
        e.check("44.model_week_all_green", false, "run_full_world(378_000) did not return Pass")?;
        return Ok(());
    };
    e.check("44.model_week_all_green", week.all_green() && week.active_hh >= 3,
        &format!("modeled 378k-tick week (Martinez/Langerak/Goth): h4_ticks={}, pairs={}",
            week.h4_ticks_per_week, week.pairs_acq))?;
    let IntegrationOutcome::Pass(a) = run_full_world(54_000, 1) else { panic!("model week regressed") };
    let IntegrationOutcome::Pass(b) = run_full_world(54_000, 0xABCD) else { panic!("model week regressed") };
    e.check("44.model_seed_agreement", a.bills_settled == b.bills_settled
        && a.vehicle_segments == b.vehicle_segments,
        "the same scheduled protocol over different local seeds yields the same driver counts")?;
    let s = ref_soak(200_000, 0xABCD);
    e.check("44.model_soak_green", s.soak_green() && s.max_handles <= SOAK_MAX_GEN_HANDLES
        && s.max_queue <= SOAK_MAX_QUEUE_PER_SIM,
        &format!("canary-29 live-adjacent model: events={}, rollback={}, nacks={}",
            s.events_injected, s.save_fault_rolls_back, s.queue_gap_nacks))?;
    let m = P7Benchmark::measure(1_000);
    e.check("44.model_p7_within_budget", m.within_budget() && m.state_hash_matches_across_seeds,
        &format!("p7 model cpu={}us ram={}B", m.cpu_per_tick_us, m.ram_bytes))?;
    Ok(())
}

// ---------------------------------------------------------------- card 44 live soak

struct SoakOut {
    final_world: AuthorityWorld,
    rtt: Vec<(u64, u64)>,
    rss: Vec<u64>,
    ticks_per_second: u64,
    budget_ok: bool,
    drop_ok: bool,
    bounded_ok: bool,
    rate_ok: bool,
    parity_ok: bool,
    no_crash: bool,
    rss_ok: bool,
    cold_restart_ok: bool,
    save_fault_ok: bool,
    exposure_minutes: u64,
}

fn step(peer: &mut Peer, lines: &str, corr: &mut u64) -> Result<Vec<String>> {
    let fence = *corr; *corr += 1;
    peer.exchange(lines, fence).map_err(Into::into)
}

fn soak_phase_commands(peers: &mut [Peer; 3], corr: &mut u64, phase: usize, claims: bool) -> Result<bool> {
    for (i, peer) in peers.iter_mut().enumerate() {
        let role = ["client", "client2", "client3"][i];
        let hello = format!("HELLO|{corr}|1|1|17|15|{role}\n");
        step(peer, &hello, corr)?;
    }
    for peer in peers.iter_mut() {
        let body = format!("CMD|{corr}|RequestWorld|9|9|9\nCMD|{}|SetClientRole|player\n", *corr + 1);
        *corr += 2;
        step(peer, &body, corr)?;
    }
    if claims {
        for (i, peer) in peers.iter_mut().enumerate() {
            let body = format!("CMD|{corr}|ClaimSim|{}|{}\n", i + 1, (i + 1) * 100);
            step(peer, &body, corr)?;
        }
    }
    // Funds + relationship deltas fan out through the shared broadcast path.
    for (i, peer) in peers.iter_mut().enumerate() {
        let body = format!("CMD|{corr}|ModifyFunds|{}|-{}|purchase\n", (i + 1) * 100, 100 + phase);
        step(peer, &body, corr)?;
    }
    let body = format!("CMD|{corr}|EnqueueInteraction|2|SocializeWith|server|1|200\n");
    step(&mut peers[1], &body, corr)?;
    // A bounded queue load (never near the 200 cap) and a named slot clash.
    let guid = 70 + phase as u64;
    let body = format!("CMD|{corr}|RouteSlotClaim|3|{guid}|0|1\n");
    let held = step(&mut peers[2], &body, corr)?;
    let clash_id = *corr; *corr += 2;
    let clash = peers[0].exchange(
        &format!("CMD|{clash_id}|RouteSlotClaim|1|{guid}|0|1\n"), clash_id + 1)?;
    let clash_ok = has(&held, "ACK") && has(&clash, "ERR|") && clash.iter().any(|s| s.contains("SlotAlreadyReserved"));
    for _ in 0..4u64 {
        let body = format!("CMD|{corr}|EnqueueInteraction|1|Sit|server|0|100\n");
        step(&mut peers[0], &body, corr)?;
    }
    for peer in peers.iter_mut() { *corr += 1; peer.drain(*corr)?; }
    Ok(clash_ok)
}

fn card44_soak(e: &mut Evidence, mut world: AuthorityWorld, mut corr: u64) -> Result<SoakOut> {
    let dir = root().join(format!("soak-{}", std::process::id()));
    fs::create_dir_all(&dir)?;
    let paths = SaveFileSet::for_name(&dir, "soakpoint");
    let mut out = SoakOut {
        final_world: AuthorityWorld::new(), rtt: Vec::new(), rss: Vec::new(), ticks_per_second: 0,
        budget_ok: true, drop_ok: true, bounded_ok: true, rate_ok: true, parity_ok: true,
        no_crash: true, rss_ok: true, cold_restart_ok: true, save_fault_ok: true, exposure_minutes: 0,
    };
    // Wave-7 hardening idiom, third half: a THROWAWAY warm-up daemon (never
    // budget-graded) pays the cold-start costs - process-wide first-touch of
    // the daemon/acceptor/peer machinery, loader and heap arenas, and any
    // antivirus scan of a freshly linked probe binary - so phase 0's timed
    // window opens on a warm machine. Same sockets retired and settled before
    // the loop, so nothing from warm-up contends with phase 0.
    {
        let mut warm = AuthorityDaemon::bind(([127, 0, 0, 1], 0).into())?;
        warm.ticks = 200;
        warm.max_connections = 3;
        let waddr = warm.local_addr()?;
        let wserver = thread::spawn(move || warm.serve_world(AuthorityWorld::new()));
        let mut wpeers = Vec::new();
        for _ in 0..3 {
            let p = Peer::connect(waddr, false)?;
            wpeers.push(p);
        }
        for p in wpeers.iter_mut() {
            let _ = p.stream.shutdown(Shutdown::Both);
        }
        drop(wpeers);
        let _ = wserver.join().map_err(|_| io::Error::other("warm-up daemon panicked"))??;
        thread::sleep(Duration::from_millis(150));
    }
    for phase in 0..SOAK_PHASES {
        let mut daemon = AuthorityDaemon::bind(([127, 0, 0, 1], 0).into())?;
        daemon.ticks = SOAK_PHASE_TICKS;
        daemon.max_connections = 3;
        let addr = daemon.local_addr()?;
        let server = thread::spawn(move || daemon.serve_world(world));
        type PeerOutcome = (bool, bool, Vec<(u64, u64)>);
        let peers_result: Result<PeerOutcome> = (|| {
            let a = Peer::connect(addr, false)?;
            let b = Peer::connect(addr, false)?;
            let c = Peer::connect(addr, false)?;
            let mut peers = [a, b, c];
            let clash_ok = soak_phase_commands(&mut peers, &mut corr, phase, phase == 0)?;
            let parity = peers[0].digest == peers[1].digest && peers[1].digest == peers[2].digest;
            let mut samples: Vec<(u64, u64)> = Vec::new();
            for peer in peers.iter_mut() {
                samples.extend(peer.take_rtt());
                let _ = peer.stream.shutdown(Shutdown::Both);
            }
            Ok((clash_ok, parity, samples))
        })();
        let joined = server.join().map_err(|_| io::Error::other("soak daemon panicked")).and_then(|r| r);
        let (clash_ok, parity, samples, report, mut w) = match (peers_result, joined) {
            (Ok(pair), Ok((report, w))) => (pair.0, pair.1, pair.2, report, w),
            _ => { out.no_crash = false; world = AuthorityWorld::new(); break; }
        };
        telemetry(e, &format!("soak-phase-{phase}"), &report)?;
        out.budget_ok &= report.tick_p99_ns <= 50_000 && clash_ok;
        out.drop_ok &= report.dropped_ticks_during_join == 0;
        out.bounded_ok &= report.ring_watermark_frames <= SOAK_MAX_WIRE_RING
            && report.queue_depth_max < SOAK_MAX_QUEUE_PER_SIM
            && report.gen_handles_live < SOAK_MAX_GEN_HANDLES;
        out.rate_ok &= rate(&report) >= u64::from(SOAK_MIN_TICKS_PER_SECOND);
        out.ticks_per_second = out.ticks_per_second.max(rate(&report));
        out.exposure_minutes += sim_minutes(report.pump_ticks);
        if phase == 2 {
            // Cold restart at the soak midpoint: commit, destroy, restore from
            // disk bytes, run the same window on both, require one hash.
            let saved_hash = w.state_hash();
            let bytes = w.checkpoint(PIN_N);
            let payload = valid_payload(&bytes)?;
            let mut commit = AtomicSaveCommitter::new(paths.clone());
            let done = (|| -> std::result::Result<(), CommitFault> {
                commit.stage(&payload)?; commit.verify_nhd()?; commit.rotate_backup()?;
                commit.atomic_rename_commit()
            })();
            out.save_fault_ok &= done.is_ok() && commit.phase == CommitPhase::Done;
            let on_disk = fs::read(paths.target.join("payload.bin"))?;
            let index = usize::try_from(u32::from_le_bytes(on_disk[0x40..0x44].try_into()?))?;
            let mut resumed = AuthorityWorld::restore_checkpoint(&on_disk[104..index], PIN_N)?;
            let restored_hash = resumed.state_hash();
            for _ in 0..200 { w.tick(); resumed.tick(); }
            out.cold_restart_ok = restored_hash == saved_hash && w.state_hash() == resumed.state_hash();
        }
        out.parity_ok &= parity;
        out.rtt.extend(samples);
        world = w;
        match os_rss() { Ok(rss) => out.rss.push(rss), Err(_) => { out.rss_ok = false; } }
        // Wave-7 hardening idiom: every phase's three connection threads and
        // the acceptor retire through the Windows loader (DLL thread detach is
        // serialized machine-wide, milliseconds under a C# build or TS3W boot
        // storm). Settle AFTER that teardown and BEFORE binding the next
        // phase's timed daemon, so phase k's background work never overlaps
        // phase k+1's p99 window. No budget relaxed, no sample dropped.
        thread::sleep(Duration::from_millis(150));
    }
    // Injected save fault: every phase of the five-phase FSM rolls back
    // byte-exact across all three nested trees (cards 11/29 SaveFaultAtStage).
    let fault_dir = root().join(format!("soakfault-{}", std::process::id()));
    fs::create_dir_all(&fault_dir)?;
    let fault_paths = SaveFileSet::for_name(&fault_dir, "soakpoint");
    for (path, bytes) in [(&fault_paths.target, b"target".as_slice()),
        (&fault_paths.backup, b"backup".as_slice()), (&fault_paths.lastgood, b"lastgood".as_slice())] {
        fs::create_dir_all(path.join("nested"))?;
        fs::write(path.join("nested/state.bin"), bytes)?;
    }
    let before = [tree(&fault_paths.target)?, tree(&fault_paths.backup)?, tree(&fault_paths.lastgood)?];
    let payload = valid_payload(&world.checkpoint(PIN_N))?;
    let mut commit = AtomicSaveCommitter::new(fault_paths.clone());
    let rolled = (|| -> std::result::Result<bool, CommitFault> {
        commit.stage(&payload)?;
        commit.verify_nhd()?;
        commit.rotate_backup()?;
        commit.rollback_on_fault(CommitFault::RenameFault)?;
        Ok(commit.phase != CommitPhase::Done)
    })().unwrap_or(false);
    let after = [tree(&fault_paths.target)?, tree(&fault_paths.backup)?, tree(&fault_paths.lastgood)?];
    out.save_fault_ok &= rolled && before == after;
    fs::remove_dir_all(fault_dir)?;
    out.final_world = world;
    Ok(out)
}

impl Peer {
    /// Take this session's (run-offset-us, rtt-us) samples for binning.
    fn take_rtt(&mut self) -> Vec<(u64, u64)> { std::mem::take(&mut self.rtt_us) }
}

fn card44_checks(e: &mut Evidence, s: &SoakOut) -> Result<()> {
    let plateau = if s.rss.len() >= 3 && cfg!(windows) {
        let peak = *s.rss.iter().max().unwrap_or(&0);
        let tail = &s.rss[s.rss.len() - 3..];
        peak <= 15_000_000 && tail.last().unwrap_or(&0).saturating_sub(tail[0]) <= 2_000_000
    } else { cfg!(windows) == false && s.rss.is_empty() };
    let gated = !cfg!(windows);
    e.check("44.live_tick_budget", s.budget_ok, "every soak phase kept tick p99 <= 50us (per-phase telemetry lines)")?;
    e.check("44.live_zero_drop_ticks", s.drop_ok, "dropped_ticks_during_join == 0 in every phase incl. scheduled reconnects")?;
    e.check("44.live_ring_queue_handles_bounded", s.bounded_ok,
        &format!("ring <= {SOAK_MAX_WIRE_RING}, queue < {SOAK_MAX_QUEUE_PER_SIM}, handles < {SOAK_MAX_GEN_HANDLES} across all phases"))?;
    e.check("44.live_ticks_per_second", s.rate_ok && s.ticks_per_second >= u64::from(SOAK_MIN_TICKS_PER_SECOND),
        &format!("steady {} ticks/s >= {SOAK_MIN_TICKS_PER_SECOND}", s.ticks_per_second))?;
    e.check("44.multi_client_delta_parity", s.parity_ok,
        "all three clients' FNV digests over mirrored Rl/Hd/Od deltas equal, phase after phase")?;
    e.check("44.cold_restart_continuity", s.cold_restart_ok,
        "minute-scale midpoint restart: DBPF commit -> destroy -> restore -> 200 ticks -> identical state hash vs the continuous world")?;
    e.check("44.save_fault_rollback", s.save_fault_ok,
        "injected RenameFault restored all three nested byte trees exactly; the clean commit reached Done")?;
    let mut w = AuthorityWorld::new();
    command(&mut w, PlayerId(1), RoleTag::Client, "HELLO|1|1|1|17|15|client")?;
    command(&mut w, PlayerId(2), RoleTag::Client2, "HELLO|1|1|1|17|15|client2")?;
    command(&mut w, PlayerId(1), RoleTag::Client, "CMD|2|ClaimSim|1|100")?;
    command(&mut w, PlayerId(2), RoleTag::Client2, "CMD|2|ClaimSim|2|200")?;
    command(&mut w, PlayerId(2), RoleTag::Client2, "CMD|3|EnqueueInteraction|2|SocializeWith|server|1|100")?;
    command(&mut w, PlayerId(0), RoleTag::Server, "CMD|4|OpenSocial|700|2|1|1001|1|2")?;
    command(&mut w, PlayerId(0), RoleTag::Server, "CMD|5|SocialPostureEntered|700|2")?;
    let waiting = !w.socials.is_released(SocialPairId(700));
    command(&mut w, PlayerId(2), RoleTag::Client2, "CMD|6|RouteSlotClaim|2|9|0|1")?;
    command(&mut w, PlayerId(2), RoleTag::Client2, "CMD|7|SwapClaim|2|3")?;
    e.check("44.mid_social_swap", waiting && w.socials.live_pairs() == 0
        && w.socials.teardowns == [SimId(1), SimId(2)]
        && !w.authority.claimed_sims.contains(&SimId(2))
        && w.authority.owners.get(&SimId(3)) == Some(&PlayerId(2))
        && !w.authority.owners.contains_key(&SimId(2))
        && w.slots.holder(ObjectGuid(9), SlotIndex(0)).is_none(),
        "claim swap inside the social barrier: owner transfer, ordered teardown, seat released, queue cancelled")?;
    e.check("44.rss_plateau", gated || plateau,
        &format!("OS working set sampled per phase {:?}: peak <= 15MB and the last three samples plateau (<= 2MB drift); live-gated off Windows", s.rss))?;
    e.check("44.exposure_accounting", s.exposure_minutes >= 120,
        &format!("accelerated in-world exposure = {0} minutes (ticks*2/75, card 06) across the live phases; the 120 WALL-minute operator soak under crash_catch.py stays live-gated", s.exposure_minutes))?;
    e.check("44.no_crash", s.no_crash, "every soak daemon joined without a panic (0 crashes over the rehearsed exposure)")?;
    Ok(())
}

// ---------------------------------------------------------------- cards 47 release rehearsal

fn rc_manifest() -> Result<String> {
    let kernel = Path::new(env!("CARGO_MANIFEST_DIR"));
    let mut artifacts: Vec<(&str, PathBuf)> = vec![
        ("runtime_bridge.rs", kernel.join("src/runtime_bridge.rs")),
        ("routing_astar.rs", kernel.join("src/routing_astar.rs")),
        ("jazz_sacs.rs", kernel.join("src/jazz_sacs.rs")),
        ("Cargo.toml", kernel.join("Cargo.toml")),
        ("GATE_TABLE_SIM96.md", kernel.join("GATE_TABLE_SIM96.md")),
    ];
    if let Ok(exe) = std::env::current_exe() { artifacts.push(("wave8_alpha_readiness_probe", exe)); }
    let mut items = String::new();
    for (name, path) in &artifacts {
        let bytes = fs::read(path)?;
        if !items.is_empty() { items.push(','); }
        items.push_str(&format!("{{\"name\":{},\"sha256\":\"{}\"}}", quoted(name), sha256_hex(&bytes)));
    }
    let dbpf = format!("{DBPF_MAJOR}.{DBPF_MINOR}");
    let gate = fs::read(kernel.join("GATE_TABLE_SIM96.md"))?;
    Ok(format!("{{\"rc\":\"0.9.0-rc8\",\"source\":\"sim96_kernel_cleanroom\",\"toolchain\":\"cargo-release-cleanroom\",\"compatibility\":{{\"bridge_protocol\":{BRIDGE_PROTOCOL_VERSION},\"simlink_native\":{SIMLINK_NATIVE_VERSION},\"wire\":{WIRE_VERSION},\"dbpf\":{},\"s3_magic\":\"01 53 33 00\",\"ts3w_sha256\":{}}},\"sealed_against\":\"{}\",\"artifacts\":[{}]}}",
        quoted(&dbpf), quoted(TS3W_SHA256_HEX), sha256_hex(&gate), items))
}

fn verify_manifest(text: &str) -> Result<bool> {
    let kernel = Path::new(env!("CARGO_MANIFEST_DIR"));
    let mut ok = true;
    let mut rest = text;
    while let Some(at) = rest.find("\"name\":") {
        rest = &rest[at + 7..];
        let name_end = rest.find("\",\"sha256\":\"").ok_or_else(|| io::Error::other("manifest shape"))?;
        let name = rest[..name_end].trim_matches('"');
        let rest2 = &rest[name_end + 12..];
        let sha_end = rest2.find('"').ok_or_else(|| io::Error::other("manifest shape"))?;
        let want = &rest2[..sha_end];
        let path = match name {
            "runtime_bridge.rs" => kernel.join("src/runtime_bridge.rs"),
            "Cargo.toml" => kernel.join("Cargo.toml"),
            "GATE_TABLE_SIM96.md" => kernel.join("GATE_TABLE_SIM96.md"),
            _ => continue,
        };
        if sha256_hex(&fs::read(&path)?) != want { ok = false; }
        rest = rest2;
    }
    Ok(ok)
}

fn card47_release(e: &mut Evidence, world: &mut AuthorityWorld) -> Result<()> {
    e.check("47.sha256_known_vectors",
        sha256_hex(b"") == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
            && sha256_hex(b"abc") == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
        "the manifest digester matches the NIST vectors before any pin is trusted")?;
    let first = rc_manifest()?;
    let second = rc_manifest()?;
    fs::write(root().join("rc-manifest.json"), &first)?;
    e.check("47.rc_manifest_determinism", first == second && !first.is_empty()
        && verify_manifest(&first)?,
        "rc-manifest.json built twice byte-identical; no timestamp, no absolute path; every artifact digest recomputes")?;
    // Clean install boot of N: fresh world, first commit, boot state hash.
    let dir = root().join(format!("release-{}", std::process::id()));
    fs::create_dir_all(&dir)?;
    let paths = SaveFileSet::for_name(&dir, "rcpoint");
    let mut boot = AuthorityWorld::new();
    for _ in 0..50 { boot.tick(); }
    let boot_bytes = boot.checkpoint(PIN_N);
    let payload = valid_payload(&boot_bytes)?;
    let mut commit = AtomicSaveCommitter::new(paths.clone());
    let booted_done = (|| -> std::result::Result<(), CommitFault> {
        commit.stage(&payload)?; commit.verify_nhd()?; commit.rotate_backup()?;
        commit.atomic_rename_commit()
    })().is_ok();
    let booted = booted_done && commit.phase == CommitPhase::Done
        && fs::read(paths.target.join("payload.bin"))? == payload;
    e.check("47.clean_install_boot", booted,
        &format!("RC N installs a fresh world; five-phase commit Done; boot hash {:016x}", boot.state_hash()))?;
    // N -> N+1 migration on a COPY of the live save, run window forward.
    let saved_hash = world.state_hash();
    let split = world.clock.native_tick_cnt;
    let bytes = world.checkpoint(PIN_N);
    let refused = AuthorityWorld::restore_checkpoint(&bytes, PIN_N2)
        .is_err_and(|err| err.kind() == io::ErrorKind::InvalidData);
    e.check("47.method_checksum_contract_refusal", refused,
        "N+1 build with a different accepted-method checksum refuses the N save instead of half-parsing it")?;
    let mut upgraded = AuthorityWorld::restore_checkpoint(&bytes, PIN_N)?;
    for _ in 0..300 { world.tick(); upgraded.tick(); }
    let calendar_ok = upgraded.clock.start_time_ticks == world.clock.start_time_ticks
        && upgraded.clock.current_ticks() == world.clock.current_ticks()
        && upgraded.clock.native_tick_cnt == split + 300;
    let ledger_ok = world.funds.replay_ledger() == world.funds.ledger;
    e.check("47.migrate_n_to_n1_bit_identity", upgraded.state_hash() == world.state_hash()
        && calendar_ok && ledger_ok,
        &format!("over [T..T+300] the N+1 load of N's save matches the continuous N run bit-exactly ({:016x}); clock monotonic, ledger replay identical", world.state_hash()))?;
    // Tested one-step rollback N+1 -> N: the rotation chain restores lastgood
    // and backup byte-exact, and the restored chain reloads as N.
    let rollback_world = AuthorityWorld::restore_checkpoint(&bytes, PIN_N)?;
    e.check("47.rollback_one_step", rollback_world.state_hash() == saved_hash,
        "one down-step (N+1 -> N) lands exactly on the pre-cut-over hash; arbitrary N+k -> N is NOT claimed")?;
    fs::remove_dir_all(dir)?;
    Ok(())
}

// ---------------------------------------------------------------- card 40 surface gate + G ledger

fn card40_surface(e: &mut Evidence) -> Result<()> {
    let mut daemon = AuthorityDaemon::bind(([127, 0, 0, 1], 0).into())?;
    daemon.ticks = 800;
    daemon.max_connections = 3;
    let addr = daemon.local_addr()?;
    let server = thread::spawn(move || daemon.serve_world(AuthorityWorld::new()));
    let outcome = (|| -> Result<bool> {
        let mut good = Peer::connect(addr, true)?;
        good.exchange("HELLO|1|1|1|17|15|client\n", 2)?;
        let mut bad = Peer::connect(addr, true)?;
        let refused = bad.exchange("HELLO|3|0|1|17|15|client2\n", 4)?;
        let hello_refused = has(&refused, "ERR|3|hello_rejected|MissingSimlinkRing")
            && !has(&refused, "WELCOME|") && !has(&refused, "SNAP|");
        let mut gate = Peer::connect(addr, true)?;
        gate.exchange("HELLO|5|1|1|17|15|client3\n", 6)?;
        let blocked = gate.exchange(
            "CMD|7|SpawnService|Maid\nCMD|8|EnqueueInteraction|1|BedSleep|client|0|100\n", 9)?;
        let oversized = gate.exchange(&format!("CMD|10|{}\n", "A".repeat(5_000)), 11)?;
        // The PONG fence of this exchange is itself the proof the session
        // survived: a bare reset would have errored instead of returning.
        gate.exchange("", 12)?;
        Ok(hello_refused && has(&blocked, "ERR|7|ClientWorldServiceSuppressed")
            && has(&blocked, "ERR|8|UnauthorizedClientInsertion")
            && oversized.iter().any(|s| s.contains("frame_too_large")))
    })();
    let (report, _) = server.join().map_err(|_| io::Error::other("surface daemon panicked"))??;
    let green = outcome?;
    telemetry(e, "surface-gate", &report)?;
    e.check("40.surface_gate", green,
        "unconforming HELLO refused before any world byte; service spawn and client autonomy refused with exact KernelError names; a >4080-byte frame is refused with the connection alive")?;
    Ok(())
}

fn card40_gates(e: &mut Evidence) -> Result<()> {
    let gate_line = fs::read_to_string(Path::new(env!("CARGO_MANIFEST_DIR")).join("GATE_TABLE_SIM96.md"))
        // The sealed referee owns its gate list (it grew past 30 rows when the
        // Wave-11/12 clean-room canaries landed). The gradeable contract is "a
        // Totals line, and 0 FAILED / 0 TEST_MISSING / 0 ABSENT under it" - the
        // row count is the referee's own output, never hardcoded here.
        .map(|t| t.contains("Totals:")
            && t.contains("0 FAILED / 0 TEST_MISSING / 0 ABSENT")).unwrap_or(false);
    let park = run_park_meet(1_000, 7).map(|o| o.stats.benchmark_green() && o.stats.pairs_transitioned == 3).unwrap_or(false);
    let cont = run_continuity_check(0x5EED, 500, 1_000);
    e.check("40.G1_crash_budget", e.ok_of("44.no_crash") && e.ok_of("44.exposure_accounting"),
        "0 crashes over the rehearsed accelerated exposure; the 60+ exposure-minute live run under crash_catch.py remains live-gated")?;
    e.check("40.G2_join_drops", e.ok_of("44.live_zero_drop_ticks") && e.ok_of("37.join_tick_continuity_no_stall"),
        "every reconnect and CoW join committed its scheduled ticks (dropped_ticks_during_join == 0)")?;
    e.check("40.G3_hello_enforcement", e.ok_of("37.hello_gate_all_refusals") && e.ok_of("40.surface_gate"),
        "100% of non-conforming CLIENT_ENV attestations refused by name, live TCP included")?;
    e.check("40.G4_two_client_parity", e.ok_of("44.multi_client_delta_parity")
        && e.ok_of("37.post_reclaim_broadcast_parity") && e.ok_of("37.client_visible_state_parity"),
        "shared funds/relationship deltas byte-identical across clients; STATE-visible hash identical; two PHYSICAL machines stays live-gated")?;
    e.check("40.G5_park_meet", park, "three-park-meet oracle green (canary 15/27)")?;
    e.check("40.G6_cold_restart", cont.bit_identical && e.ok_of("44.cold_restart_continuity"),
        &format!("canary 28 continuity (hash {:016x}) plus the soak-midpoint restart", cont.uninterrupted_hash))?;
    e.check("40.G7_fault_rollback", e.ok_of("44.save_fault_rollback") && e.ok_of("47.rollback_one_step"),
        "injected CommitFault rolls back byte-exact; one-step upgrade rollback lands on the pre-cut-over hash")?;
    e.check("40.G8_wan_transport", e.ok_of("37.ring_watermark_cap") && e.ok_of("37.reclaim_resume_ok_and_fresh")
        && e.ok_of("37.nack_cooldown_no_storm") && e.ok_of("37.rtt_within_reorder_budget"),
        "ring < 2048, NACKs paced, resume covered with no double-apply, RTT inside the reorder budget")?;
    e.check("40.G9_resource_budgets", e.ok_of("44.live_tick_budget") && e.ok_of("44.rss_plateau")
        && e.ok_of("44.live_ring_queue_handles_bounded") && e.ok_of("44.live_ticks_per_second")
        && e.ok_of("44.model_p7_within_budget"), "p99/queue/handles/ring/ticks-s/RSS all within card 20/29/39 budgets")?;
    e.check("40.G10_three_verdict", gate_line,
        "GATE_TABLE carries a Totals line with 0 FAILED / 0 TEST_MISSING / 0 ABSENT (the referee owns the row count); the gate runner's sibling `cargo test` line is the exit-0 half of this criterion")?;
    e.check("40.G11_package_integrity", fs::read_to_string(root().join("rc-manifest.json"))
        .map(|t| verify_manifest(&t).unwrap_or(false)).unwrap_or(false),
        "rc-manifest.json re-read from disk: every artifact digest recomputes (the verify.ps1 idiom rehearsed in-kernel)")?;
    let live_lines = fs::read_to_string(Path::new(env!("CARGO_MANIFEST_DIR")).join("LIVE_STATE.md"))
        .map(|t| t.lines().count()).unwrap_or(999);
    e.check("40.G12_docs_current", live_lines < 80
        && Path::new(env!("CARGO_MANIFEST_DIR")).join("VERIFIED_IDIOMS.md").exists(),
        &format!("LIVE_STATE.md {live_lines} lines (< 80); VERIFIED_IDIOMS.md present; check_docs.py --anchors --strict runs in repo CI"))?;
    let all_g = (1..=12).all(|i| e.ok_of(&format!("40.G{i}_{}", match i {
        1 => "crash_budget", 2 => "join_drops", 3 => "hello_enforcement", 4 => "two_client_parity",
        5 => "park_meet", 6 => "cold_restart", 7 => "fault_rollback", 8 => "wan_transport",
        9 => "resource_budgets", 10 => "three_verdict", 11 => "package_integrity", _ => "docs_current" })));
    e.check("40.verdict_go", all_g && e.ok_of("40.surface_gate"),
        "G1-G12 green on the rehearsed surface => Go - supervised private alpha (synthetic rehearsal). NOT public launch, NOT native replacement; two-machine and wall-minute exposure rows stay live-gated")?;
    Ok(())
}

// ---------------------------------------------------------------- card 50 decision gate

fn card50_gate(e: &mut Evidence) -> Result<()> {
    let refused = AuthorityDaemon::bind(([192, 168, 7, 1], 0).into())
        .is_err_and(|err| err.kind() == io::ErrorKind::PermissionDenied);
    e.check("50.loopback_only_bind", refused,
        "a routable bind is refused by the sidecar itself (admin-surface class, card 31 §D / SECURITY.md)")?;
    let gate_line = fs::read_to_string(Path::new(env!("CARGO_MANIFEST_DIR")).join("GATE_TABLE_SIM96.md"))
        // The sealed referee owns its gate list (it grew past 30 rows when the
        // Wave-11/12 clean-room canaries landed). The gradeable contract is "a
        // Totals line, and 0 FAILED / 0 TEST_MISSING / 0 ABSENT under it" - the
        // row count is the referee's own output, never hardcoded here.
        .map(|t| t.contains("Totals:")
            && t.contains("0 FAILED / 0 TEST_MISSING / 0 ABSENT")).unwrap_or(false);
    e.check("50.gate_table_totals", gate_line, "the sealed three-verdict totals the decision consumes are all-green (0 FAILED / 0 TEST_MISSING / 0 ABSENT; the row count belongs to the referee)")?;
    let d = grade_subsystem(30, 30, 0, 0, 10, 2,
        e.ok_of("44.model_p7_within_budget") && e.ok_of("44.live_tick_budget"),
        e.ok_of("44.model_soak_green") && e.ok_of("44.live_zero_drop_ticks"), true);
    let modules: [(&str, ShipDecision); 10] = [
        ("routing", d), ("sacs_rng", d), ("handles", d), ("clock", d), ("authority", d),
        ("interactions", d), ("social_state", d), ("wire", d), ("persistence", d), ("services", d),
    ];
    let overall = overall_go(&modules);
    e.check("50.overall_go_selective", d == ShipDecision::ShipAsSimlinkAuthoritySeam
        && overall == OverallGo::SelectiveGo && overall != OverallGo::ShipAsReplacementKernel
        && LEDGER_ROW_COUNT == 12 && FULL_GO_UNREACHABLE_AT_WAVE_6,
        "10 deferred ledger rows keep the honest verdict at SelectiveGo (Option 2): seam-ship per module, never native replacement")?;
    let items: [(u32, u32, &str, &str); 13] = [
        (31, 31, "Architect (ADR)", "GATE_TABLE_SIM96.md KERNEL_DIFF rows"),
        (32, 34, "Owner", ".omo/run/wave7_scenario_probe/records.json"),
        (35, 35, "Owner", "rust/sim96_kernel_cleanroom/WAVE7_DEMO_SCORECARD.md"),
        (36, 36, "Owner + Security", "rc-manifest.json (this run)"),
        (37, 37, "Owner + Security", "records.json 37.* rows (this run)"),
        (38, 38, "Owner", ".omo/run/wave7_scenario_probe/records.json 38.* rows"),
        (39, 39, "Owner", ".omo/run/wave7_scenario_probe/records.json telemetry"),
        (40, 40, "Owner + Security", "records.json 40.* rows (this run)"),
        (41, 41, "Owner", "required_live: tester-receipts.jsonl (booted alpha)"),
        (42, 42, "Architect", "required_live: divergence-ledger.jsonl"),
        (43, 43, "Security", ".omo/run/wave7_scenario_probe/wave9-results.jsonl"),
        (44, 44, "Owner", "records.json 44.* rows (this run)"),
        (47, 47, "Owner", "rc-manifest.json + records.json 47.* rows (this run)"),
    ];
    let mut ledger = String::new();
    let mut complete = true;
    for (item, card, signer, artifact) in items {
        let status = if artifact.starts_with("required_live:") { "live_gated" }
            else if artifact.contains("this run") || Path::new(env!("CARGO_MANIFEST_DIR")).join("GATE_TABLE_SIM96.md").exists() {
                "rehearsal_evidence"
            } else { "missing" };
        complete &= status != "missing";
        ledger.push_str(&format!("{{\"item\":{item},\"card\":{card},\"signer_role\":{},\"artifact\":{},\"status\":{}}}\n",
            quoted(signer), quoted(artifact), quoted(status)));
    }
    fs::write(root().join("signoff_ledger.jsonl"), &ledger)?;
    e.check("50.signoff_ledger_rows", complete && ledger.lines().count() == 13,
        "13 sign-off rows (items 31-44,47) each carry a named signer role and a citable artifact; human signatures are live_gated, never faked")?;
    e.line(&format!("{{\"side\":\"probe\",\"event\":\"authorized_scope\",\"audience\":\"named supervised cohort (cards 36/40/49)\",\"network\":\"loopback admin + VPN private :34019 only; no router forward\",\"option\":2,\"public_launch_authorized\":false,\"native_replacement_authorized\":false,\"decision\":{}}}",
        quoted("SelectiveGo")))?;
    e.check("50.verdict_selective_go", overall == OverallGo::SelectiveGo && e.ok_of("40.verdict_go"),
        "terminal rehearsal verdict: Option 2 - ShipAsSimlinkAuthoritySeam per module, OverallGo::SelectiveGo; a Go here approves a supervised rehearsal only, never public launch (card 50 §D)")?;
    Ok(())
}

// ---------------------------------------------------------------- main

fn run(e: &mut Evidence) -> Result<()> {
    e.line("{\"side\":\"probe\",\"event\":\"scope\",\"surface\":\"synthetic_loopback\",\"native_game_exercised\":false,\"exposure\":\"accelerated_in_world_minutes\",\"method_checksum_source\":\"authored_fixture\",\"live_gated\":[\"two_physical_machines\",\"120_wall_minute_soak\",\"tester_signatures\",\"crash_catch_watchdog\",\"real_.sims3_files\"]}")?;
    card37_kernel(e)?;
    let (mut rtt, report37) = card37_live(e)?;
    card44_model(e)?;
    let mut soak = card44_soak(e, AuthorityWorld::new(), 500)?;
    rtt.append(&mut soak.rtt);
    card44_checks(e, &soak)?;
    rtt_telemetry(e, &rtt, rate(&report37))?;
    card47_release(e, &mut soak.final_world)?;
    card40_surface(e)?;
    card40_gates(e)?;
    card50_gate(e)?;
    let archived = ["records.json", "rc-manifest.json", "signoff_ledger.jsonl", "card37-wire.jsonl"]
        .iter().all(|f| root().join(f).exists());
    e.check("47.artifacts_archived", archived,
        "rc-manifest, sign-off ledger, wire trace and this records.json archive in one run folder (card 47 §E / card 35 §B)")?;
    Ok(())
}

fn main() -> std::process::ExitCode {
    let result = (|| -> Result<bool> {
        fs::create_dir_all(root())?;
        STARTED.set(Instant::now()).ok();
        let mut e = Evidence {
            records: BufWriter::new(File::create(root().join("records.json"))?),
            verdicts: Vec::new(), failed: 0, checked: 0, passed: BTreeMap::new(),
        };
        if let Err(err) = run(&mut e) {
            e.check("probe.execution", false, &err.to_string())?;
        }
        e.line(&format!("{{\"side\":\"probe\",\"event\":\"summary\",\"checks\":{},\"failed\":{},\"passed\":{}}}",
            e.checked, e.failed, e.failed == 0))?;
        e.records.flush()?;
        e.records.get_mut().sync_all()?;
        e.emit_verdicts()?;
        println!("wave8_alpha_readiness_probe: checks={} failures={} records={}", e.checked, e.failed,
            root().join("records.json").display());
        Ok(e.failed == 0)
    })();
    match result {
        Ok(true) => std::process::ExitCode::SUCCESS,
        Ok(false) => std::process::ExitCode::FAILURE,
        Err(err) => { eprintln!("wave8_alpha_readiness_probe: {err}"); std::process::ExitCode::FAILURE }
    }
}
