#![forbid(unsafe_code)]
//! Cards 34/38/39 acceptance driver. TCP clients are synthetic, not TS3.
//! Missing runtime observations fail acceptance; model checks never stand in
//! for a daemon restart, a client renderer, or native watchdog evidence.
use std::collections::BTreeMap;
use std::fs::{self, File};
use std::io::{self, BufRead, BufReader, BufWriter, Write};
use std::net::{Shutdown, TcpStream};
use std::path::PathBuf;
use std::thread;
use std::time::Duration;

use sim96_kernel_cleanroom::persistence::{
    AtomicSaveCommitter, CommitFault, CommitPhase, SaveFileSet,
};
use sim96_kernel_cleanroom::runtime_bridge::{AuthorityDaemon, AuthorityWorld, DaemonReport};
use sim96_kernel_cleanroom::household_drivers::{DelinquencyStage, WEEKLY_BILLS_PERIOD_TICKS};
use sim96_kernel_cleanroom::handles::GenHandle;
use sim96_kernel_cleanroom::clock::AlarmManager;
use sim96_kernel_cleanroom::kernel_iface::{SlotIndex, SlotReservationStore};
use sim96_kernel_cleanroom::socials::SocialPairId;
use sim96_kernel_cleanroom::social_state::LongTermRelationshipType;
use sim96_kernel_cleanroom::wan_transport::RoleTag;
use sim96_kernel_cleanroom::wire::PlayerId;
use sim96_kernel_cleanroom::{KernelError, ObjectGuid, SimId};

type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;

fn root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../.omo/run/wave7_scenario_probe")
}

fn quoted(s: &str) -> String {
    let mut out = String::from("\"");
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if c.is_control() => out.push_str(&format!("\\u{:04x}", u32::from(c))),
            c => out.push(c),
        }
    }
    out.push('"');
    out
}

struct Evidence {
    records: BufWriter<File>,
    // Windows console writes serialize the whole process and are slow under a
    // pseudo-terminal; verdict lines are buffered so nothing is emitted while
    // a card 39 measured window runs, then flushed together at the end.
    verdicts: Vec<String>,
    failed: usize,
    checked: usize,
}

impl Evidence {
    fn check(&mut self, name: &str, ok: bool, detail: &str) -> io::Result<()> {
        self.checked += 1;
        self.failed += usize::from(!ok);
        writeln!(self.records,
            "{{\"side\":\"probe\",\"event\":\"acceptance\",\"name\":{},\"passed\":{},\"detail\":{}}}",
            quoted(name), ok, quoted(detail))?;
        self.verdicts.push(format!("{} {name}: {detail}", if ok { "PASS" } else { "FAIL" }));
        Ok(())
    }

    fn emit_verdicts(&mut self) -> io::Result<()> {
        for line in self.verdicts.drain(..) {
            println!("{line}");
        }
        io::stdout().flush()
    }
}

struct Peer {
    stream: TcpStream,
    reader: BufReader<TcpStream>,
    frames: Vec<String>,
}

impl Peer {
    fn connect(addr: std::net::SocketAddr) -> io::Result<Self> {
        let stream = TcpStream::connect(addr)?;
        stream.set_nodelay(true)?;
        stream.set_read_timeout(Some(Duration::from_secs(5)))?;
        Ok(Self { reader: BufReader::new(stream.try_clone()?), stream, frames: Vec::new() })
    }

    // A PONG is an ordered fence from the real connection handler. No sleeps
    // or "wait long enough for the broadcast" assertions.
    fn exchange(&mut self, lines: &str, fence: u64) -> io::Result<Vec<String>> {
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
            self.frames.push(line.clone());
            if line == end { break; }
            batch.push(line);
        }
        Ok(batch)
    }
}

fn has(lines: &[String], prefix: &str) -> bool {
    lines.iter().any(|s| s.starts_with(prefix))
}

fn valid_payload(state: &[u8]) -> Result<Vec<u8>> {
    // Authored DBPF fixture, not game data: one 8-byte span and one index row.
    let span = 8 + u32::try_from(state.len())?;
    let index = 96 + span;
    let mut bytes = vec![0; usize::try_from(index + 36)?];
    bytes[..4].copy_from_slice(b"DBPF");
    for (off, value) in [(4, 2_u32), (8, 0), (0x24, 1), (0x2c, 36),
        (0x3c, 3), (0x40, index)] {
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

fn save_checks(e: &mut Evidence) -> Result<()> {
    let payload = valid_payload(&[])?;
    let dir = root().join(format!("save-{}", std::process::id()));
    fs::create_dir_all(&dir)?;
    let paths = SaveFileSet::for_name(&dir, "checkpoint");
    // Distinct nested byte trees make rollback checks sensitive to all three
    // generations, not just the current payload file.
    for (path, bytes) in [(&paths.target, b"target".as_slice()),
        (&paths.backup, b"backup".as_slice()), (&paths.lastgood, b"lastgood".as_slice())] {
        fs::create_dir_all(path.join("nested"))?;
        fs::write(path.join("nested/state.bin"), bytes)?;
    }
    fn tree(path: &std::path::Path) -> io::Result<BTreeMap<PathBuf, Vec<u8>>> {
        fn walk(base: &std::path::Path, path: &std::path::Path,
            out: &mut BTreeMap<PathBuf, Vec<u8>>) -> io::Result<()> {
            for entry in fs::read_dir(path)? {
                let entry = entry?;
                if entry.file_type()?.is_dir() { walk(base, &entry.path(), out)?; }
                else {
                    let name = entry.path().strip_prefix(base)
                        .map_err(io::Error::other)?.to_path_buf();
                    out.insert(name, fs::read(entry.path())?);
                }
            }
            Ok(())
        }
        let mut out = BTreeMap::new();
        walk(path, path, &mut out)?;
        Ok(out)
    }
    for fault in [CommitFault::BackupCopyFault, CommitFault::RenameFault] {
        let before = [tree(&paths.target)?, tree(&paths.backup)?, tree(&paths.lastgood)?];
        let mut commit = AtomicSaveCommitter::new(paths.clone());
        let result = (|| {
            commit.stage(&payload)?;
            commit.verify_nhd()?;
            if fault == CommitFault::RenameFault { commit.rotate_backup()?; }
            commit.rollback_on_fault(fault)
        })();
        let after = [tree(&paths.target)?, tree(&paths.backup)?, tree(&paths.lastgood)?];
        e.check(&format!("38.7.{fault:?}.model"), result.is_ok() && before == after
            && commit.phase != CommitPhase::Done, "all three nested byte trees compared")?;
    }
    let mut commit = AtomicSaveCommitter::new(paths.clone());
    let result = (|| {
        commit.stage(&payload)?;
        commit.verify_nhd()?;
        commit.rotate_backup()?;
        commit.atomic_rename_commit()
    })();
    e.check("38.1.clean_commit.model", result.is_ok() && commit.phase == CommitPhase::Done
        && fs::read(paths.target.join("payload.bin"))? == payload,
        "real filesystem FSM verified before rotate, reached Done after fault retries")?;
    fs::remove_dir_all(dir)?;
    Ok(())
}

fn scenario(e: &mut Evidence) -> Result<(AuthorityWorld, DaemonReport)> {
    let mut daemon = AuthorityDaemon::bind(([127, 0, 0, 1], 0).into())?;
    daemon.ticks = 1_000;
    daemon.max_connections = 2;
    let addr = daemon.local_addr()?;
    let server = thread::spawn(move || daemon.serve_world(AuthorityWorld::new()));
    // The listener is bound before spawning; connect itself supplies readiness.
    let result = (|| -> Result<()> {
        let mut a = Peer::connect(addr)?;
        let welcome_a = a.exchange("HELLO|1|1|1|17|15|client\n", 90_001)?;
        let mut b = Peer::connect(addr)?;
        let welcome_b = b.exchange("HELLO|1|1|1|17|15|client2\n", 90_002)?;
        e.check("34.client_env", has(&welcome_a, "WELCOME|1|1|17|15")
            && has(&welcome_b, "WELCOME|1|2|17|15"),
            "Jennifer Martinez (:34030 role), Iliana Langerak (:34031 role); TCP ports are ephemeral")?;
        for (peer, fence) in [(&mut a, 90_003), (&mut b, 90_004)] {
            let join = peer.exchange(
                "CMD|2|RequestWorld|9|9|9\nCMD|3|SetClientRole|player\n", fence)?;
            let chunks: Vec<_> = join.iter().filter(|s| s.starts_with("WFC|")).collect();
            let complete = chunks.len() == 11 && chunks.iter().enumerate().all(|(i, s)|
                s.starts_with(&format!("WFC|{i}|11|")));
            e.check(&format!("34.bootstrap.{fence}"), complete
                && has(&join, "PRIMARY|100|1") && has(&join, "HISTRESET|2"),
                "ordered WFC 0..10, primary pin, history reset")?;
        }
        let claim_a = a.exchange("CMD|4|ClaimSim|1|100\n", 90_005)?;
        let claim_b = b.exchange("CMD|4|ClaimSim|2|200\n", 90_006)?;
        e.check("34.claims", has(&claim_a, "ACK|4|SimClaimed|1|100")
            && has(&claim_b, "ACK|4|SimClaimed|2|200"),
            "kernel SimId 1/2 label Jennifer/Iliana; native GUIDs are not u32 IDs")?;
        let slot = a.exchange("CMD|5|RouteSlotClaim|1|7|0|1\n", 90_007)?;
        let conflict = b.exchange("CMD|5|RouteSlotClaim|2|7|0|1\n", 90_008)?;
        e.check("34.slot_conflict", has(&slot, "ACK|5|RouteSlotClaim")
            && has(&conflict, "ERR|5|SlotAlreadyReserved|1"), "atomic reservation and named clash")?;
        let social = b.exchange("CMD|6|EnqueueInteraction|2|SocializeWith|server|1|100\n", 90_009)?;
        let mirror = a.exchange("", 90_010)?;
        let rl = social.iter().find(|s| s.starts_with("Rl|6|"));
        e.check("34.relationship_convergence", rl.is_some_and(|s| mirror.contains(s)),
            "completed introduction Rl must reach BOTH clients, not only its requester")?;
        let funds = a.exchange("CMD|6|ModifyFunds|100|-500|purchase\n", 90_011)?;
        let other_funds = b.exchange("", 90_012)?;
        let hd = funds.iter().find(|s| s.starts_with("Hd|6|100|-500|"));
        e.check("34.funds_convergence", hd.is_some_and(|s| other_funds.contains(s)),
            "chair purchase authoritative balance delta must reach BOTH clients")?;
        // Replay one ID 32 times, while still inside the specified window.
        let original = a.exchange("CMD|7|RouteSlotClaim|1|8|0|1\n", 90_013)?;
        let replay = a.exchange(&"CMD|7|RouteSlotClaim|1|8|0|1\n".repeat(32), 90_014)?;
        let ack = original.iter().find(|s| s.starts_with("ACK|7|"));
        e.check("38.8.duplicate_burst", ack.is_some_and(|s|
            replay.iter().filter(|r| *r == s).count() == 32),
            "32 replies equal original success, never SlotAlreadyReserved")?;
        for (peer, fence) in [(&mut a, 90_016), (&mut b, 90_017)] {
            // Subscribe by reading the actual arrival event, bounded by socket timeout.
            while !has(&peer.frames, "Ve|") {
                let mut line = String::new();
                if peer.reader.read_line(&mut line)? == 0 {
                    return Err(io::Error::new(io::ErrorKind::UnexpectedEof, "missing vehicle arrival").into());
                }
                peer.frames.push(line.trim_end().to_owned());
            }
            peer.exchange("", fence)?;
        }
        let mut last = [0_i64; 3];
        let mut steps = 0;
        let mut continuous = true;
        for line in a.frames.iter().filter(|s| s.starts_with("Vp|")) {
            let fields: Vec<_> = line.split('|').collect();
            let pos = [fields[3].parse::<i64>()?, fields[4].parse()?, fields[5].parse()?];
            let d2: i64 = pos.iter().zip(last).map(|(x, y)| (x-y).pow(2)).sum();
            continuous &= d2 <= 60 * 60;
            last = pos;
            steps += 1;
        }
        e.check("34.vehicle_lifecycle", steps == 12 && continuous
            && has(&claim_a, "Vs|") && has(&claim_a, "Vh|") && has(&a.frames, "Ve|")
            && has(&b.frames, "Ve|"), "Vh/Vs/Vp/Ve, all 12 segments <=60cm from origin")?;
        let seeds = |p: &Peer| -> BTreeMap<String, String> {
            p.frames.iter().filter_map(|s| {
                let f: Vec<_> = s.split('|').collect();
                (f.len() == 3 && f[0] == "AS").then(|| (f[1].to_owned(), f[2].to_owned()))
            }).collect()
        };
        let sa = seeds(&a);
        let sb = seeds(&b);
        let common: Vec<_> = sa.iter().filter(|(t, _)| sb.contains_key(*t)).collect();
        e.check("34.sacs_seed_stream", !common.is_empty()
            && common.iter().all(|(t, v)| sb.get(*t) == Some(*v)),
            &format!("{} shared AS ticks compared; native animation variant not observed", common.len()))?;
        // BufWriter: one file, not a WriteFile syscall per frame while the
        // measured demo pump is still running.
        let mut trace = BufWriter::new(File::create(root().join("scenario-wire.jsonl"))?);
        for (side, peer) in [("client1", &a), ("client2", &b)] {
            for line in &peer.frames {
                writeln!(trace, "{{\"side\":{},\"frame\":{}}}", quoted(side), quoted(line))?;
            }
        }
        trace.flush()?;
        a.stream.shutdown(Shutdown::Both)?;
        b.stream.shutdown(Shutdown::Both)?;
        Ok(())
    })();
    let (report, mut world) = server.join().map_err(|_| io::Error::other("daemon thread panicked"))??;
    e.check("34.join_tick_continuity", report.dropped_ticks_during_join == 0
        && report.ticks_committed == 1_000 && !report.dialog_clock_paused,
        &format!("ticks={}, dropped={}, paused={}", report.ticks_committed,
            report.dropped_ticks_during_join, report.dialog_clock_paused))?;
    result?;
    e.check("34.acquaintance_state", world.matrix.get(SimId(1), SimId(2))
        .is_some_and(|relationship| relationship.ltr == LongTermRelationshipType::Acquaintance
            && relationship.liking == 10 && relationship.interaction_bits & 1 == 1),
        "authoritative matrix contains HasMet, liking +10 and Acquaintance, not just an Rl prefix")?;
    let held = world.tls.snapshot_for_join();
    let held_tick = held.tick;
    for _ in 0..10 { world.tick(); }
    e.check("34.cow_snapshot", held.tick == held_tick && world.tls.world.tick > held_tick,
        "retained join Arc stays immutable while the authoritative writer commits")?;
    Ok((world, report))
}

// Authored model checksum pin, not a claim about native fork IL. Production
// callers supply the accepted attempt's real MethodChecksum.
const CHECKSUM: &str = "3438383934343839343438393434383934343839343438393434383934343839343438";

fn command(world: &mut AuthorityWorld, player: PlayerId, role: RoleTag, line: &str) -> Result<Vec<String>> {
    let mut rows = Vec::new();
    world.handle_line(player, role, line, &mut rows);
    if rows.iter().any(|s| s.starts_with("ERR|")) || rows.is_empty() {
        return Err(io::Error::other(format!("command {line}: {rows:?}")).into());
    }
    Ok(rows)
}

fn advance(world: &mut AuthorityWorld, until: u64) {
    while world.clock.native_tick_cnt < until { world.tick(); }
}

fn recovery(e: &mut Evidence, mut world: AuthorityWorld) -> Result<DaemonReport> {
    let now = world.clock.native_tick_cnt;
    // All fixture input crosses the production server boundary, so checkpoint
    // replay covers private driver state rather than cloning test-owned maps.
    for line in [
        "CMD|100|ScheduleAlarm|250000|0|0".to_owned(),
        "CMD|101|AddLockedWish|1|101|500|18446744073709551615".to_owned(),
        "CMD|102|AddLockedWish|1|102|750|18446744073709551615".to_owned(),
        "CMD|103|IssueBills|100|50000".to_owned(),
        format!("CMD|104|ScheduleShift|2|{}|{}|7", now + WEEKLY_BILLS_PERIOD_TICKS + 500,
            now + 3 * WEEKLY_BILLS_PERIOD_TICKS),
        "CMD|105|RetireHandle|0|0".to_owned(),
    ] {
        command(&mut world, PlayerId(0), RoleTag::Server, &line)?;
    }
    let mut uninterrupted = AuthorityWorld::restore_checkpoint(&world.checkpoint(CHECKSUM), CHECKSUM)?;
    let split = now + 200_000;
    let end = now + 2 * WEEKLY_BILLS_PERIOD_TICKS + 1;
    advance(&mut world, split);
    let alarm_before = format!("{:?}", world.alarms);
    let wishes_before = world.aspirations.wishes.clone();
    let happiness_before = world.aspirations.happiness.clone();
    let bills_before = format!("{:?}", world.households);
    let careers_before = format!("{:?}", world.careers);
    let saved_hash = world.state_hash();
    let bytes = world.checkpoint(CHECKSUM);
    let payload = valid_payload(&bytes)?;
    let dir = root().join(format!("recovery-{}", std::process::id()));
    fs::create_dir_all(&dir)?;
    let paths = SaveFileSet::for_name(&dir, "checkpoint");
    let mut commit = AtomicSaveCommitter::new(paths.clone());
    let done = (|| {
        commit.stage(&payload)?;
        commit.verify_nhd()?;
        commit.rotate_backup()?;
        commit.atomic_rename_commit()
    })();
    e.check("38.1.runtime_checkpoint_commit", done.is_ok() && commit.phase == CommitPhase::Done,
        "actual runtime checkpoint inside authored DBPF fixture; five-phase commit reached Done")?;
    drop(world);
    // Restore from bytes read back from the committed filesystem, not a clone.
    let on_disk = fs::read(paths.target.join("payload.bin"))?;
    let index = usize::try_from(u32::from_le_bytes(on_disk[0x40..0x44].try_into()?))?;
    let persisted = &on_disk[104..index];
    e.check("38.method_checksum", AuthorityWorld::restore_checkpoint(persisted,
        "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff").is_err(),
        "wrong accepted-method checksum refuses recovery")?;
    let mut resumed = AuthorityWorld::restore_checkpoint(persisted, CHECKSUM)?;
    e.check("38.2.restart_state", resumed.state_hash() == saved_hash,
        &format!("checkpoint={saved_hash:016x}, restored={:016x}", resumed.state_hash()))?;
    e.check("38.3.pending_alarms", format!("{:?}", resumed.alarms) == alarm_before
        && resumed.fired_alarms.is_empty(), "future alarm due/order preserved, no early firing")?;
    e.check("38.4.locked_wishes", resumed.aspirations.wishes == wishes_before
        && resumed.aspirations.happiness == happiness_before
        && resumed.aspirations.locked_count.get(&SimId(1)) == Some(&2),
        "two exact locked handles, locked_count and LifetimeHappiness survive serialized recovery")?;
    e.check("38.5.pending_bills", format!("{:?}", resumed.households) == bills_before,
        "bill records and private period dedup state survive recovery")?;
    e.check("38.6.pending_career", format!("{:?}", resumed.careers) == careers_before,
        "in-flight shift and private transit dedup state survive recovery")?;
    e.check("38.10.stale_handle", resumed.handles.get(GenHandle { index: 0, generation: 0 })
        == Err(KernelError::StaleObjectHandle), "retired generation not resurrected by serialized replay")?;
    advance(&mut uninterrupted, end);
    advance(&mut resumed, end);
    let continuous_hash = uninterrupted.state_hash();
    let reload_hash = resumed.state_hash();
    e.check("38.2.continuity_bit_identity", continuous_hash == reload_hash,
        &format!("uninterrupted={continuous_hash:016x}, reload={reload_hash:016x}; FNV-1a 64-bit"))?;
    e.check("38.3.alarm_once", resumed.fired_alarms == [0], "future alarm fired exactly once after restart")?;
    e.check("38.4.wishes_after_resume", resumed.aspirations.wishes == wishes_before
        && resumed.aspirations.happiness == happiness_before,
        "locked wish identities and happiness also remain intact after resumed ticks")?;
    e.check("38.5.weekly_cycle", resumed.households.bills.len() == 6
        && resumed.households.bills.iter().filter(|b| b.stage == DelinquencyStage::FirstNotice).count() == 2,
        "three periods across two households, one delinquency step, no double bill")?;
    e.check("38.6.transit_once", resumed.careers.dispatches.len() == 1,
        "exact start-minus-600 dispatch fires once across restart")?;
    let funds_before = resumed.funds.ledger.clone();
    let ledger_rows_before = resumed.funds.log.len();
    let original = resumed.recent_cmds.iter().find(|(p, corr, _)| *p == 1 && *corr == 6)
        .map(|(_, _, rows)| rows.clone()).ok_or_else(|| io::Error::other("original funds CommandId missing"))?;
    let mut daemon = AuthorityDaemon::bind(([127, 0, 0, 1], 0).into())?;
    // Use the same 1,000 measured ticks as the initial phase. A 100-sample
    // p99 is effectively just its second slowest observation.
    daemon.ticks = 1_000;
    daemon.max_connections = 2;
    let addr = daemon.local_addr()?;
    let server = thread::spawn(move || daemon.serve_world(resumed));
    let rejoin = (|| -> Result<()> {
        let mut a = Peer::connect(addr)?;
        a.exchange("HELLO|1|1|1|17|15|client\n", 80_001)?;
        let mut b = Peer::connect(addr)?;
        b.exchange("HELLO|1|1|1|17|15|client2\n", 80_002)?;
        for (peer, request, fence) in [(&mut a, 8, 80_003), (&mut b, 7, 80_004)] {
            let rows = peer.exchange(&format!("CMD|{request}|RequestWorld|9|9|9\n"), fence)?;
            e.check(&format!("38.rejoin.{fence}"), rows.iter().filter(|s| s.starts_with("WFC|")).count() == 11
                && has(&rows, "SIMC|1|100") && has(&rows, "SIMC|2|200"),
                "new TCP sessions receive all eleven chunks and restored claims")?;
        }
        let replay = a.exchange(&"CMD|6|ModifyFunds|100|-500|purchase\n".repeat(32), 80_005)?;
        e.check("38.8.reconnect_replay", original.iter().all(|row|
            replay.iter().filter(|r| *r == row).count() == 32),
            "32 reconnect duplicates return the byte-exact original result")?;
        a.stream.shutdown(Shutdown::Both)?;
        b.stream.shutdown(Shutdown::Both)?;
        Ok(())
    })();
    let (report, mut world) = server.join().map_err(|_| io::Error::other("recovered daemon panicked"))??;
    rejoin?;
    e.check("38.8.no_second_effect", world.funds.ledger == funds_before
        && world.funds.log.len() == ledger_rows_before, "one purchase ledger row across reconnect burst")?;
    command(&mut world, PlayerId(0), RoleTag::Server, "CMD|106|OpenSocial|700|2|1|1001|1|2")?;
    command(&mut world, PlayerId(0), RoleTag::Server, "CMD|107|SocialPostureEntered|700|2")?;
    let waiting = !world.socials.is_released(SocialPairId(700));
    command(&mut world, PlayerId(2), RoleTag::Client2, "CMD|8|RouteSlotClaim|2|9|0|1")?;
    command(&mut world, PlayerId(2), RoleTag::Client2, "CMD|9|SwapClaim|2|3")?;
    e.check("38.9.mid_social_swap", waiting && world.socials.live_pairs() == 0
        && world.socials.teardowns == [SimId(1), SimId(2)]
        && !world.authority.claimed_sims.contains(&SimId(2))
        && world.authority.owners.get(&SimId(3)) == Some(&PlayerId(2))
        && !world.authority.owners.contains_key(&SimId(2))
        && world.slots.holder(ObjectGuid(9), SlotIndex(0)).is_none(),
        "swap inside barrier transfers owner, tears down both postures in order, releases seat, cancels queue")?;
    let final_hash = world.state_hash();
    let recovered_again = AuthorityWorld::restore_checkpoint(&world.checkpoint(CHECKSUM), CHECKSUM)?;
    e.check("38.final_replay", recovered_again.state_hash() == final_hash,
        "post-rejoin and post-swap checkpoint also reconstructs all runtime state")?;
    fs::remove_dir_all(dir)?;
    Ok(report)
}

fn telemetry(e: &mut Evidence, name: &str, r: &DaemonReport) -> Result<()> {
    let rate = std::num::NonZeroU64::new(r.elapsed_ns)
        .map_or(0, |elapsed_ns| r.pump_ticks * 1_000_000_000 / elapsed_ns.get());
    let green = r.tick_p99_ns <= 50_000 && r.ring_watermark_frames < 2048
        && r.queue_depth_max < 200 && r.gen_handles_live < 100_000
        && r.dropped_ticks_during_join == 0 && r.ram_bytes <= 15_000_000 && rate >= 60;
    writeln!(e.records,
        "{{\"side\":\"sidecar\",\"event\":\"telemetry\",\"phase\":{},\"tick\":{},\"samples\":{},\"sidecar_cpu_per_tick_us\":{}.{:03},\"latency_histogram_ns_log2\":{:?},\"ring_watermark_frames\":{},\"queue_depth_max\":{},\"gen_handles_live\":{},\"telemetry_heap_bytes\":{},\"ram_bytes\":{},\"ticks_per_second\":{},\"dropped_ticks_during_join\":{},\"all_green\":{},\"surface\":\"synthetic_loopback\"}}",
        quoted(name), r.ticks_committed, r.pump_ticks, r.tick_p99_ns / 1_000, r.tick_p99_ns % 1_000,
        r.latency_histogram_ns_log2, r.ring_watermark_frames, r.queue_depth_max, r.gen_handles_live,
        r.telemetry_heap_bytes, r.ram_bytes, rate,
        r.dropped_ticks_during_join, green)?;
    e.check(&format!("39.budgets.{name}"), green, &format!(
        "p99={}ns ring={} queue={} handles={} ram={}B ticks/s={rate}",
        r.tick_p99_ns, r.ring_watermark_frames, r.queue_depth_max, r.gen_handles_live, r.ram_bytes))?;
    writeln!(e.records, "{{\"side\":\"sidecar\",\"event\":\"tick_cost_breakdown\",\"phase\":{},\"core_p99_ns\":{},\"encode_p99_ns\":{}}}",
        quoted(name), r.core_p99_ns, r.encode_p99_ns)?;
    println!("tick_cost {name}: core_p99={}ns encode_p99={}ns", r.core_p99_ns, r.encode_p99_ns);
    Ok(())
}

fn run(e: &mut Evidence) -> Result<()> {
    writeln!(e.records, "{{\"side\":\"probe\",\"event\":\"scope\",\"surface\":\"synthetic_loopback\",\"native_game_exercised\":false,\"method_checksum_source\":\"authored_fixture\"}}")?;
    let (world, report) = scenario(e)?;
    telemetry(e, "demo", &report)?;
    save_checks(e)?;
    let mut alarms = AlarmManager::new();
    alarms.add_alarm(5, false, 0);
    alarms.add_alarm(10, false, 0);
    alarms.add_alarm(5, false, 0);
    alarms.fix_load_alarms(7);
    e.check("38.3.alarm_clamp_order", alarms.simulate(7) == [0, 2]
        && alarms.simulate(9).is_empty() && alarms.simulate(10) == [1],
        "past-due alarms clamp to resume tick in insertion order; future due tick is unchanged")?;
    let rejoin = recovery(e, world)?;
    telemetry(e, "rejoin", &rejoin)?;
    if cfg!(windows) {
        let output = std::process::Command::new("powershell").args([
            "-NoProfile", "-Command", &format!("(Get-Process -Id {}).WorkingSet64", std::process::id()),
        ]).output()?;
        if !output.status.success() { return Err(io::Error::other("OS RSS query failed").into()); }
        let rss: u64 = std::str::from_utf8(&output.stdout)?.trim().parse()?;
        writeln!(e.records, "{{\"side\":\"probe\",\"event\":\"os_memory\",\"ram_bytes\":{rss}}}")?;
        e.check("39.rss", rss <= 15_000_000, &format!("OS working set={rss} bytes"))?;
    }
    Ok(())
}

fn main() -> std::process::ExitCode {
    let result = (|| -> Result<bool> {
        fs::create_dir_all(root())?;
        let mut e = Evidence {
            records: BufWriter::new(File::create(root().join("records.json"))?),
            verdicts: Vec::new(), failed: 0, checked: 0,
        };
        if let Err(err) = run(&mut e) {
            e.check("probe.execution", false, &err.to_string())?;
        }
        writeln!(e.records, "{{\"side\":\"probe\",\"event\":\"summary\",\"checks\":{},\"failed\":{},\"passed\":{}}}",
            e.checked, e.failed, e.failed == 0)?;
        e.records.flush()?;
        e.records.get_mut().sync_all()?;
        e.emit_verdicts()?;
        println!("wave7_scenario_probe: checks={} failures={} records={}", e.checked, e.failed,
            root().join("records.json").display());
        Ok(e.failed == 0)
    })();
    match result {
        Ok(true) => std::process::ExitCode::SUCCESS,
        Ok(false) => std::process::ExitCode::FAILURE,
        Err(err) => { eprintln!("wave7_scenario_probe: {err}"); std::process::ExitCode::FAILURE }
    }
}
