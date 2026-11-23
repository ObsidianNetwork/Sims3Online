#![forbid(unsafe_code)]
//! Card 48/49 integration test: the `DIAG_STATE` operator surface over the
//! real daemon and loopback TCP, plus its pure-kernel read-only contract, the
//! `--resident` + `DIAG_STATE` + `QUIT -> BYE` lifecycle lock, and the card-30
//! standalone-disposition inventory (cards 48/49/50 hardening pass).
//! The full C0-C7 dress rehearsal lives in `src/bin/wave10_rc_campaign_probe.rs`.
use std::io::{BufRead, BufReader, Write};
use std::net::TcpStream;
use std::thread;
use std::time::{Duration, Instant};

use sim96_kernel_cleanroom::go_nogo::{LEDGER_ROW_COUNT, StandaloneDisposition, UNSUPPORTED_SURFACE_ROWS, full_go_native_obligations, ledger_route_cross_check, standalone_inventory_counts};
use sim96_kernel_cleanroom::runtime_bridge::{AUTHORITY_ROUTES, AuthorityDaemon, AuthorityOwner, AuthorityWorld, MAX_TELEMETRY_SAMPLES};
use sim96_kernel_cleanroom::wan_transport::RoleTag;
use sim96_kernel_cleanroom::wire::PlayerId;

fn read_until(stream: &mut BufReader<TcpStream>, fence: &str) -> Vec<String> {
    let mut rows = Vec::new();
    loop {
        let mut line = String::new();
        assert_ne!(stream.read_line(&mut line).unwrap(), 0, "EOF before {fence}");
        let line = line.trim_end().to_owned();
        if line == fence { return rows; }
        rows.push(line);
    }
}

#[test]
fn diag_state_never_mutates_the_authoritative_world() {
    let pin = "4e314e314e314e314e314e314e314e314e314e314e314e314e314e314e314e31";
    let mut w = AuthorityWorld::new();
    let mut out = Vec::new();
    w.handle_line(PlayerId(1), RoleTag::Client, "HELLO|1|1|1|17|15|client", &mut out);
    w.handle_line(PlayerId(1), RoleTag::Client, "CMD|2|ClaimSim|1|100", &mut out);
    let before = (w.state_hash(), w.command_state_hash(), w.rng_state,
        w.clock.native_tick_cnt, w.checkpoint(pin));
    out.clear();
    w.handle_line(PlayerId(1), RoleTag::Client, "DIAG_STATE|7", &mut out);
    assert_eq!(out.len(), 1, "exactly one structured answer: {out:?}");
    assert!(out[0].starts_with("DIAG_STATE|7|tick="), "{}", out[0]);
    for key in ["|claims=1", "|queue_wm=", "|handles=", "|core_p99_ns=0",
        "|drops=0", "|clock_paused=false", "|hash="] {
        assert!(out[0].contains(key), "{} missing {key}", out[0]);
    }
    assert_eq!(w.state_hash(), before.0);
    assert_eq!((w.command_state_hash(), w.rng_state, w.clock.native_tick_cnt,
        w.checkpoint(pin)), (before.1, before.2, before.3, before.4));
}

#[test]
fn diag_state_over_tcp_streams_with_the_live_pump() {
    let mut daemon = AuthorityDaemon::bind(([127, 0, 0, 1], 0).into()).unwrap();
    daemon.ticks = 400;
    daemon.max_connections = 1;
    let addr = daemon.local_addr().unwrap();
    let server = thread::spawn(move || daemon.serve_world(AuthorityWorld::new()));
    let stream = TcpStream::connect(addr).unwrap();
    stream.set_nodelay(true).unwrap();
    stream.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
    let mut reader = BufReader::new(stream.try_clone().unwrap());
    let mut writer = stream;
    write!(writer, "HELLO|1|1|1|17|15|client\nPING|2\n").unwrap();
    writer.flush().unwrap();
    read_until(&mut reader, "PONG|2");
    // Let the pump run a while, then query the operator surface twice inside
    // one fence: the world advances between answers, so the query itself is
    // the only difference and both lines must parse identically by key set.
    thread::sleep(Duration::from_millis(150));
    write!(writer, "DIAG_STATE|3\nDIAG_STATE|4\nCMD|5|ClaimSim|1|100\nPING|6\n").unwrap();
    writer.flush().unwrap();
    let batch = read_until(&mut reader, "PONG|6");
    let diags: Vec<&String> = batch.iter().filter(|s| s.starts_with("DIAG_STATE|")).collect();
    assert_eq!(diags.len(), 2, "{batch:?}");
    for d in &diags {
        for key in ["|tick=", "|claims=", "|ring_wm=", "|queue_wm=", "|handles=",
            "|core_p99_ns=", "|encode_p99_ns=", "|drops=0", "|clock_paused=false", "|hash=",
            "|telemetry_heap_bytes=", "|ram_bytes="] {
            assert!(d.contains(key), "{d} missing {key}");
        }
        let core: u64 = d.split('|').find_map(|f| f.strip_prefix("core_p99_ns="))
            .and_then(|v| v.parse().ok()).unwrap();
        assert!(core <= 50_000, "pump running p99 over budget: {d}");
        let ram: u64 = d.split('|').find_map(|f| f.strip_prefix("ram_bytes="))
            .and_then(|v| v.parse().ok()).unwrap_or(u64::MAX);
        let heap: u64 = d.split('|').find_map(|f| f.strip_prefix("telemetry_heap_bytes="))
            .and_then(|v| v.parse().ok()).unwrap_or(u64::MAX);
        assert!(heap <= 16_384 * 8 * 5 && ram <= 15_000_000,
            "bounded watermarks on the live surface: {d}");
    }
    writeln!(writer, "PING|7").unwrap();
    writer.flush().unwrap();
    read_until(&mut reader, "PONG|7");
    writer.shutdown(std::net::Shutdown::Both).unwrap();
    let (report, world) = server.join().unwrap().unwrap();
    assert_eq!(report.dropped_ticks_during_join, 0);
    assert_eq!(world.authority.claimed_sims.len(), 1);
}

/// The combined `--resident` + `DIAG_STATE|corr` + `QUIT|corr -> BYE|corr`
/// service lifecycle lock (Wave 10 hardening): a resident daemon keeps its
/// accept loop open past the 10 s batch deadline, serves `DIAG_STATE` without
/// mutating the authoritative command state, and retires cleanly on `QUIT`
/// with executed-tick drop accounting (`join_drops == 0`). Takes ~11 s by
/// design: the 10 s deadline is the behaviour under test.
#[test]
fn resident_daemon_accepts_late_serves_diag_and_retires_on_quit() {
    let mut daemon = AuthorityDaemon::bind("127.0.0.1:0".parse().unwrap()).unwrap();
    daemon.resident = true;
    daemon.ticks = 100_000; // far beyond what will run; QUIT retires the pump
    daemon.max_connections = 2;
    let addr = daemon.local_addr().unwrap();
    let started = Instant::now();
    let server = thread::spawn(move || daemon.serve_world(AuthorityWorld::new()));

    // One idle slot held from t=0; the late session connects only after the
    // batch deadline and MUST still be accepted (a non-resident accept loop
    // is gone by then - see the 10_000 ms deadline in serve_world).
    let _idle = TcpStream::connect(addr).unwrap();
    thread::sleep(Duration::from_millis(10_400));
    assert!(started.elapsed() > Duration::from_secs(10), "the deadline is the subject");
    let mut late = TcpStream::connect(addr).expect("resident accept loop stays open"); // served #2
    late.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
    late.set_nodelay(true).unwrap();
    let mut reader = BufReader::new(late.try_clone().unwrap());

    write!(late, "HELLO|1|1|1|17|15|client2\nPING|2\n").unwrap();
    let greet = read_until(&mut reader, "PONG|2");
    assert!(greet.iter().any(|s| s.starts_with("WELCOME|1|2|")),
        "late connection greeted: {greet:?}");

    write!(late, "STATE|3\nPING|4\n").unwrap();
    let hash_before = state_hash_in(&read_until(&mut reader, "PONG|4"));
    write!(late, "DIAG_STATE|5\nDIAG_STATE|6\nPING|7\n").unwrap();
    let diag_batch = read_until(&mut reader, "PONG|7");
    let diags: Vec<&String> = diag_batch.iter().filter(|s| s.starts_with("DIAG_STATE|")).collect();
    assert_eq!(diags.len(), 2, "{diag_batch:?}");
    for d in &diags {
        assert!(d.contains("|drops=0") && d.contains("|clock_paused=false"), "{d}");
        assert!(d.contains("|tick=") && d.contains("|hash="), "{d}");
    }
    write!(late, "STATE|8\nPING|9\n").unwrap();
    let hash_after = state_hash_in(&read_until(&mut reader, "PONG|9"));
    assert_eq!(hash_before, hash_after, "DIAG_STATE and pump ticks moved authoritative state");
    assert!(hash_before.as_deref().is_some_and(|h| h.len() == 16), "STATE hash field readable: {hash_before:?}");

    writeln!(late, "QUIT|10").unwrap();
    let quit_started = Instant::now();
    loop {
        let mut line = String::new();
        assert_ne!(reader.read_line(&mut line).unwrap(), 0, "daemon closed without BYE");
        if line.trim_end() == "BYE|10" { break; }
    }
    let (report, world) = server.join().unwrap().expect("resident serve exits cleanly");
    assert!(quit_started.elapsed() < Duration::from_secs(5), "QUIT retired the serve promptly");
    assert_eq!(report.connections_accepted, 2);
    assert!(report.pump_ticks > 3_000 && report.pump_ticks < 100_000,
        "pump ran {} executed ticks, not the 100k budget", report.pump_ticks);
    assert_eq!(report.dropped_ticks_during_join, 0, "pump_executed accounting keeps joins drop-free");
    assert!(!report.dialog_clock_paused);
    assert_eq!(report.state_hash, world.state_hash());
}

fn state_hash_in(rows: &[String]) -> Option<String> {
    rows.iter().find(|s| s.starts_with("STATE|"))
        .map(|s| s.split('|').nth(2).unwrap_or_default().to_owned())
}

/// The card-30 §B ledger, typed by Standalone Disposition and cross-mapped
/// against the live `AUTHORITY_ROUTES` table: proves programmatically why the
/// verdict stays `SelectiveGo` (every row blocks FullGo) and what exact native
/// obligations (row 1's A* at `0x41b2a0`, the `.jazz` corpus, the interaction
/// long tail, ...) would have to close first.
#[test]
fn standalone_inventory_locks_ledger_and_routes() {
    assert_eq!(UNSUPPORTED_SURFACE_ROWS.len(), LEDGER_ROW_COUNT);
    for (i, row) in UNSUPPORTED_SURFACE_ROWS.iter().enumerate() {
        assert_eq!(row.row, i as u8 + 1, "closed, ordered row set");
        assert!(row.blocks_full_go(), "no row may silently stop blocking FullGo");
        assert!(!row.surface.is_empty() && !row.native_obligation.is_empty());
        assert!(row.proves_cards.chars().any(|c| c.is_ascii_digit()),
            "no orphan claim: {}", row.surface);
    }
    // Disposition partition: 3 clean-room obligations (rows 3,8,10),
    // 3 product exclusions (4,5,6), 6 native-oracle retentions
    // (1,2,7,9,11,12) - rows 1 and 2 landed in Waves 11/12 (routing_astar.rs
    // route #25, jazz_sacs.rs route #26).
    assert_eq!(standalone_inventory_counts(), (3, 3, 6));
    assert_eq!(full_go_native_obligations().len(), LEDGER_ROW_COUNT);
    assert_eq!(UNSUPPORTED_SURFACE_ROWS.iter()
        .filter(|r| r.disposition == StandaloneDisposition::RequiresCleanRoomReplacement).count(), 3);
    // Cross-map against the LIVE route table: 27 Rust / 3 native rows; every
    // native verb cited by exactly one ledger row; no cited verb unknown.
    let routes: Vec<(&str, bool)> = AUTHORITY_ROUTES.iter()
        .map(|(v, o)| (*v, *o == AuthorityOwner::NativeTs3W)).collect();
    assert_eq!(routes.iter().filter(|(_, native)| !native).count(), 27);
    assert_eq!(routes.len(), 30);
    assert_eq!(routes.iter().filter(|(_, native)| *native).count(), 3);
    let (dupes, unknown) = ledger_route_cross_check(&routes);
    assert!(dupes.is_empty() && unknown.is_empty(), "dupes={dupes:?} unknown={unknown:?}");
    for (verb, native) in &routes {
        if !native { continue; }
        assert_eq!(UNSUPPORTED_SURFACE_ROWS.iter()
            .filter(|r| r.authority_verbs.contains(verb)).count(), 1,
            "{verb} must be owned by exactly one ledger row");
    }
    // The exact debts: A* at its RVA on row 1, jazz clip playback on row 2,
    // the registry long tail (client reselect) on row 3, GC wedge on row 7.
    assert!(UNSUPPORTED_SURFACE_ROWS[0].native_obligation.contains("0x41b2a0"));
    assert!(UNSUPPORTED_SURFACE_ROWS[1].authority_verbs.is_empty());
    assert!(UNSUPPORTED_SURFACE_ROWS[1].native_obligation.contains("jazz_sacs.rs"));
    assert_eq!(UNSUPPORTED_SURFACE_ROWS[2].authority_verbs, ["FindBestSocial"]);
    assert_eq!(UNSUPPORTED_SURFACE_ROWS[6].authority_verbs, ["WedgeDebug"]);
    // And the inventory agrees with the decision machinery it justifies.
    let d = sim96_kernel_cleanroom::go_nogo::grade_subsystem(30, 30, 0, 0, 10, 2, true, true, true);
    assert_eq!(d, sim96_kernel_cleanroom::go_nogo::ShipDecision::ShipAsSimlinkAuthoritySeam);
}

/// Card 39's 15 MiB sidecar RSS ceiling: Worker A2's live A/B run breached at
/// 21.1 MiB under `--resident --ticks 4_000_000` because five pump telemetry
/// vectors pre-sized (or grew) with the tick budget - ~100 MB virtual at 4 M
/// ticks. The pump now rides a bounded window: three sample vecs on a ring and
/// two sorted tails capped at `MAX_TELEMETRY_SAMPLES = 16_384` (~640 KB heap
/// for EVERY budget), while any window <= the cap keeps every sample, so the
/// 1_000 / 2_000 / 6_000-tick probe percentiles stay bit-identical.
#[test]
fn resident_telemetry_ring_bounds_a_multi_million_tick_budget() {
    // (1) The A2 shape: a 4,000,000-tick resident budget retired early via
    // `QUIT|corr`. Declared telemetry capacity is bounded by the cap alone.
    let mut daemon = AuthorityDaemon::bind(([127, 0, 0, 1], 0).into()).unwrap();
    daemon.ticks = 4_000_000;
    daemon.max_connections = 1;
    daemon.resident = true;
    let addr = daemon.local_addr().unwrap();
    let server = thread::spawn(move || daemon.serve_world(AuthorityWorld::new()));
    let stream = TcpStream::connect(addr).unwrap();
    stream.set_nodelay(true).unwrap();
    stream.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
    let mut reader = BufReader::new(stream.try_clone().unwrap());
    let mut writer = stream;
    write!(writer, "HELLO|1|1|1|17|15|client\nPING|2\n").unwrap();
    writer.flush().unwrap();
    read_until(&mut reader, "PONG|2");
    writeln!(writer, "QUIT|3").unwrap();
    writer.flush().unwrap();
    // The QUIT branch closes the socket around BYE: read until the answer or
    // EOF, whichever lands first (read_until would panic on a benign EOF).
    let mut saw_bye = false;
    loop {
        let mut line = String::new();
        match reader.read_line(&mut line) {
            Ok(0) | Err(_) => break,
            Ok(_) => { if line.trim_end() == "BYE|3" { saw_bye = true; break; } }
        }
    }
    assert!(saw_bye, "resident QUIT must answer BYE|3 before retiring");
    drop(writer);
    let report = server.join().unwrap().unwrap().0;
    assert_eq!(MAX_TELEMETRY_SAMPLES, 16_384, "the contract's cap");
    assert!(report.telemetry_capacity <= MAX_TELEMETRY_SAMPLES,
        "a 4 M-tick budget must declare at most the capped window: {}",
        report.telemetry_capacity);
    assert!(report.telemetry_samples <= MAX_TELEMETRY_SAMPLES);
    assert!(report.pump_ticks < 4_000_000, "QUIT retired the pump early");
    assert_eq!(report.telemetry_samples as u64, report.pump_ticks,
        "a run shorter than the cap kept every sample - nothing was dropped");
    // The heap bound the ceiling needs: five 8-byte vectors at the cap are
    // 640 KiB, where the unbounded budget cost ~96 MB / 21.1 MiB resident.
    assert!(report.telemetry_capacity * 8 * 5 < 1024 * 1024,
        "telemetry heap stays under 1 MiB for any tick budget");

    // (2) p99 accuracy at graded window sizes: a 1,200-tick batch keeps all
    // 1,200 samples, exactly like the pre-cap probe did.
    let mut daemon2 = AuthorityDaemon::bind(([127, 0, 0, 1], 0).into()).unwrap();
    daemon2.ticks = 1_200;
    daemon2.max_connections = 0;
    let server2 = thread::spawn(move || daemon2.serve_world(AuthorityWorld::new()));
    let report2 = server2.join().unwrap().unwrap().0;
    assert_eq!(report2.telemetry_samples, 1_200, "window below the cap is 100% exact");
    assert_eq!(report2.telemetry_capacity, 1_200, "pre-sized to the smaller budget");
    assert!(report2.tick_p99_ns > 0 && report2.core_p99_ns > 0 && report2.encode_p99_ns > 0,
        "percentiles still computed from the full exact window: {:?}",
        (report2.tick_p99_ns, report2.core_p99_ns, report2.encode_p99_ns));
}

/// Card 39/44 tail hardening: a ten-way concurrent `RequestWorld` bootstrap
/// storm must never stall a peer's command latency. Worker A2's live A/B run
/// logged `command_latency_max_ms = 2046.35` once, a `ClaimSim`/`PING` waiting
/// behind another connection thread's bootstrap (`11 WFC chunks` + checkpoint
/// encoding) under the world mutex. The daemon now holds that mutex only for
/// the gates, the CoW `Arc<WorldSnapshot>` clone and the bounded claim copy;
/// the chunk rows format lock-free, like the pump's broadcast. Session B also
/// reads the new bounded-watermark fields from every live `DIAG_STATE`.
#[test]
fn concurrent_request_world_never_stalls_peer_command_latency() {
    let mut daemon = AuthorityDaemon::bind(([127, 0, 0, 1], 0).into()).unwrap();
    daemon.ticks = 3_000;
    daemon.max_connections = 12;
    let addr = daemon.local_addr().unwrap();
    let server = thread::spawn(move || daemon.serve_world(AuthorityWorld::new()));

    // Session B: joined, owns a household, measures every command round trip.
    let bstream = TcpStream::connect(addr).unwrap();
    bstream.set_nodelay(true).unwrap();
    bstream.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
    let mut bread = BufReader::new(bstream.try_clone().unwrap());
    let mut bwrite = bstream;
    write!(bwrite, "HELLO|1|1|1|17|15|client\nCMD|2|ClaimSim|7|777\nPING|3\n").unwrap();
    bwrite.flush().unwrap();
    let joined = read_until(&mut bread, "PONG|3");
    assert!(joined.iter().any(|s| s.starts_with("ACK|2|SimClaimed")), "{joined:?}");

    // Ten concurrent bootstrap storms while B paces commands underneath.
    let storms: Vec<_> = (0..10).map(|_| {
        thread::spawn(move || {
            let stream = TcpStream::connect(addr).unwrap();
            stream.set_nodelay(true).unwrap();
            stream.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
            let mut reader = BufReader::new(stream.try_clone().unwrap());
            let mut writer = stream;
            write!(writer, "HELLO|1|1|1|17|15|client\nPING|2\n").unwrap();
            writer.flush().unwrap();
            read_until(&mut reader, "PONG|2");
            write!(writer, "CMD|3|RequestWorld|0|0|0\nPING|4\n").unwrap();
            writer.flush().unwrap();
            let rows = read_until(&mut reader, "PONG|4");
            assert_eq!(rows.iter().filter(|s| s.starts_with("WFC|")).count(), 11,
                "full ordered chunk stream delivered under contention: {rows:?}");
            assert!(rows.iter().any(|s| s == "HISTRESET|3"), "{rows:?}");
        })
    }).collect();

    let mut worst = Duration::ZERO;
    let mut corr = 10_u64;
    for k in 0..50 {
        corr += 2;
        let line = if k % 2 == 0 {
            format!("CMD|{corr}|ModifyFunds|777|-1|purchase\n")
        } else {
            format!("DIAG_STATE|{corr}\n")
        };
        let fence = corr + 1;
        let t0 = Instant::now();
        writeln!(bwrite, "{line}PING|{fence}").unwrap();
        bwrite.flush().unwrap();
        let batch = read_until(&mut bread, &format!("PONG|{fence}"));
        let rt = t0.elapsed();
        if rt > worst { worst = rt; }
        let prefix = if k % 2 == 0 { format!("Hd|{corr}|") } else { format!("DIAG_STATE|{corr}|") };
        let answered: Vec<_> = batch.iter().filter(|s| s.starts_with(&prefix)).collect();
        assert_eq!(answered.len(), 1, "command {k} answered exactly once: {batch:?}");
        if k % 2 == 1 {
            let d = &answered[0];
            assert!(d.contains("|telemetry_heap_bytes=") && d.contains("|ram_bytes="), "{d}");
            let ram: u64 = d.split('|').find_map(|f| f.strip_prefix("ram_bytes="))
                .and_then(|v| v.parse().ok()).unwrap_or(u64::MAX);
            assert!(ram <= 15_000_000, "bounded sidecar watermark breached: {d}");
        }
        thread::sleep(Duration::from_millis(1));
    }
    for t in storms { t.join().unwrap(); }
    let _ = bwrite.shutdown(std::net::Shutdown::Both);
    let report = server.join().unwrap().unwrap().0;
    assert_eq!(report.connections_accepted, 11, "storm ten plus session B");
    assert!(worst.as_millis() < 25,
        "peer command round-trips never stalled behind the bootstrap storm: worst {worst:?}");
    assert!(report.ram_bytes <= 15_000_000 && report.telemetry_heap_bytes > 0,
        "report watermarks: {:?}", (report.ram_bytes, report.telemetry_heap_bytes));
}
