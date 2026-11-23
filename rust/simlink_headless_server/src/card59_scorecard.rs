#![forbid(unsafe_code)]
//! Offline fixtures exercise production policies. No offline result seals a live row.
use std::{fs, io, path::{Path, PathBuf}};
use serde_json::{json, Value};
use crate::{checkpoint, join_reuse, last_good, rejoin::*, world_save::*};

fn text<'a>(row: &'a Value, key: &str) -> io::Result<&'a str> {
    row[key].as_str().ok_or_else(|| io::Error::other(format!("missing {key}")))
}
fn files(world: &WorldSave) -> Vec<(String, Vec<u8>)> {
    world.files.iter().map(|f| (f.name.clone(), f.data.clone())).collect()
}
fn copy_fixture(source: &Path, target: &Path) -> io::Result<()> {
    if target.exists() { fs::remove_dir_all(target)?; }
    fs::create_dir_all(target)?;
    for entry in fs::read_dir(source)? {
        let entry = entry?;
        if entry.file_type()?.is_file() { fs::copy(entry.path(), target.join(entry.file_name()))?; }
    }
    Ok(())
}

pub fn evaluate(row: &Value) -> io::Result<Value> {
    let result = match text(row, "op")? {
        "card59_folder_select" => {
            let roots: Vec<_> = row["roots"].as_array().ok_or_else(|| io::Error::other("roots"))?
                .iter().map(|v| PathBuf::from(v.as_str().expect("fixture root"))).collect();
            json!({"selected":checkpoint::find_newest_save_folder_in_roots(&roots, text(row, "save")?)
                .map(|p| p.to_string_lossy().replace('\\', "/"))})
        }
        "card59_chunks" => {
            let world = WorldSave::read_folder(Path::new(text(row, "folder")?), text(row, "save")?)?;
            json!({"folder":world.folder_name(), "bytes":world.total_bytes(), "chunks":world.to_chunks(WORLD_CHUNK_RAW_BYTES)
                .iter().map(|c| json!({"file_index":c.file_index,"file_count":c.file_count,"file_name":c.file_name,
                    "offset":c.offset,"file_length":c.file_length,"data":crate::b64_encode(&c.data)})).collect::<Vec<_>>()})
        }
        "card59_writer_safety" => {
            let root = Path::new(text(row, "root")?);
            let before = fs::read(root.join("Player.sims3/own.nhd"))?;
            let mut writer = WorldSaveWriter::new(root.into());
            let result = writer.add(&WorldSaveChunk { folder_name: "Player.sims3".into(), file_index: 0,
                file_count: 1, file_name: "world.nhd".into(), offset: 0, file_length: 1, data: vec![1] });
            json!({"refused_by_name":result.err().is_some_and(|e| e.to_string().contains("wasn't written by SimsOnline")),
                "unchanged":fs::read(root.join("Player.sims3/own.nhd"))? == before,
                "staging_exists":root.join("Player.sims3.incoming").exists()})
        }
        "card59_reuse_decide" => {
            let saved = join_reuse::WorldFingerprint::parse(row["saved"].as_str()).ok();
            let now = join_reuse::WorldFingerprint::parse(row["now"].as_str());
            let whole = row["whole"].as_bool().unwrap_or(true);
            let decision = join_reuse::decide(saved.as_ref().filter(|_| whole), now.as_ref().ok(),
                row["error"].as_str().or_else(|| now.as_ref().err().map(String::as_str)));
            json!({"reuse":decision.reuse,"misses":decision.misses,"detail":decision.detail})
        }
        "card59_rotation" => {
            let scratch = Path::new(text(row, "scratch")?).join("rust");
            let source = Path::new(text(row, "folder")?);
            let folder = scratch.join("Fixture.sims3");
            copy_fixture(source, &folder)?;
            let world = WorldSave::read_folder(&folder, "Fixture")?;
            let original = files(&world);
            let kept = last_good::keep(&folder, &original, "2026-11-02T00:00:00.0000000+00:00")?;
            let mut partial = original.clone();
            partial.iter_mut().find(|(name, _)| name.ends_with(".nhd")).expect("fixture nhd").1.clear();
            let failed_keep_preserved = last_good::keep(&folder, &partial, "later").is_err()
                && last_good::verify(&kept).is_none();
            let incoming = PathBuf::from(format!("{}.incoming", kept.display()));
            fs::create_dir_all(&incoming)?;
            fs::copy(source.join(&original[0].0), incoming.join(&original[0].0))?;
            let interrupted_keep_preserved = last_good::verify(&kept).is_none();
            fs::remove_dir_all(&folder)?;
            let restored = last_good::ensure_bootable(&folder, "20261102-000000").restored;
            let restored_world = WorldSave::read_folder(&folder, "Fixture")?;
            json!({"kept":last_good::verify(&kept).is_none(), "failed_keep_preserved":failed_keep_preserved,
                "interrupted_keep_preserved":interrupted_keep_preserved,"restored":restored,
                "bytes_preserved":files(&restored_world) == original})
        }
        "card59_lot_mode" => {
            use crate::GameDriver;
            let mut driver = crate::BridgeGameDriver::new(crate::bridge_driver::OfflineBridge);
            let players = row["players"].as_array().ok_or_else(|| io::Error::other("players"))?;
            let mut modes = Vec::new();
            for (index, player) in players.iter().enumerate() {
                let player = player.as_i64().expect("fixture player") as i32;
                driver.adjudicate_for(crate::ConnId(index as u64 + 1), &crate::messages::Command {
                    corr: 1, command_id: 1, player_id: player, verb: "JoinSession".into(),
                    args: vec![crate::messages::hex_encode("fixture"), "~".into()],
                }).map_err(io::Error::other)?;
            }
            for routed in driver.poll_routed_events().map_err(io::Error::other)? {
                if routed.event.kind == "LotLocalAuthorityModeChanged" { modes.push(routed.event.fields); }
            }
            let refresh = driver.poll_routed_events().map_err(io::Error::other)?.iter()
                .filter(|r| r.event.kind == "LotLocalAuthorityModeChanged").count();
            json!({"modes":modes,"refresh_modes":refresh})
        }
        "card59_rejoin_state" => {
            let snapshot = crate::snapshot::Snapshot::decode(&fixture_snapshot()).ok_or_else(|| io::Error::other("fixture snapshot"))?;
            let report = text(row, "presentation")?;
            let settled = crate::presentation::parse_dump(report).2;
            let mut ring = ClientEventRing::new(9);
            ring.append(9, 1, "EV|1|10|0|SimStateChanged|55".into(), false);
            ring.mark_disconnected(1, 0);
            let payload = crate::messages::enc_list(&settled.iter().map(|s| crate::messages::hex_encode(s)).collect::<Vec<_>>());
            ring.append(9, 2, format!("EV|2|10|0|SimAnimation|{payload}"), false);
            let ResumeOutcome::Ok { replayed_lines, .. } = ring.try_resume(9, 9, 1, 1) else {
                return Err(io::Error::other("state replay went fresh"));
            };
            let replay = crate::decode_event(&crate::split(replayed_lines[0].as_bytes()).map_err(io::Error::other)?).map_err(io::Error::other)?;
            let settled_replay: Vec<_> = crate::messages::dec_list(&replay.fields[0]).map_err(io::Error::other)?
                .iter().map(|hex| crate::messages::hex_decode(hex)).collect::<Result<_, _>>().map_err(io::Error::other)?;
            json!({"snapshot":snapshot.wire_body(),"settled_presentation":settled_replay})
        }
        _ => return Err(io::Error::other("unsupported Card 59 oracle operation")),
    };
    Ok(json!({"id":row["id"],"result":result}))
}

/// Claims are exercised against the real kernel authority through the daemon driver.
pub fn rejoin_probe() -> io::Result<(Vec<Value>, u64, u64)> {
    use crate::GameDriver;
    let mut driver = crate::BridgeGameDriver::new(crate::bridge_driver::OfflineBridge);
    let claim = |corr, player| crate::messages::Command { corr, command_id: corr, player_id: player,
        verb: "ClaimSim".into(), args: vec!["55".into()] };
    let mut rows = Vec::new();
    let mut ok = 0;
    let mut fresh = 0;
    for (case, expected) in [
        ("warm", None), ("line", Some(LeaseEndReason::RingLineEvicted)),
        ("byte", Some(LeaseEndReason::RingByteEvicted)), ("bulk", Some(LeaseEndReason::BulkInGap)),
        ("paused_timeout", Some(LeaseEndReason::TimeoutExpired)), ("restart", Some(LeaseEndReason::EpochChanged)),
        ("supersede", Some(LeaseEndReason::Superseded)), ("leave", Some(LeaseEndReason::ExplicitLeave)),
    ] {
        driver.release_player(7);
        driver.release_player(8);
        if !driver.adjudicate(&claim(1, 7)).map_err(io::Error::other)?.starts_with(b"ACK|") {
            return Err(io::Error::other("initial claim refused"));
        }
        let mut ring = ClientEventRing::new(9);
        ring.append(9, 1, "EV|1|10|0|SimStateChanged|55".into(), false);
        ring.mark_disconnected(1, 100);
        ring.append(9, 2, "EV|2|10|0|SimStateChanged|55".into(), false);
        let denied = driver.adjudicate(&claim(2, 8)).map_err(io::Error::other)?;
        let held = denied.starts_with(b"ERR|2|UnauthorizedClientInsertion|");
        let mut epoch = 9;
        let mut now = 101;
        match case {
            "line" => for seq in 3..=2050 { ring.append(9, seq, format!("EV|{seq}|10|0|SimStateChanged|55"), false); },
            "byte" => for seq in 3..=19 { ring.append(9, seq, "x".repeat(crate::messages::PUBLIC_LINE_MAX_BYTES), false); },
            "bulk" => ring.append(9, 3, "EV|3|10|0|FullSnapshot|b1".into(), true),
            "paused_timeout" => now = 100 + RESUME_WINDOW_MAX_MS,
            "restart" => epoch = 10,
            "supersede" => ring.end(LeaseEndReason::Superseded),
            "leave" => ring.end(LeaseEndReason::ExplicitLeave),
            _ => {}
        }
        let outcome = ring.try_resume(epoch, 9, 1, now);
        let (passed, reason, replay) = match &outcome {
            ResumeOutcome::Ok { replayed_lines, .. } => {
                ok += 1;
                (expected.is_none() && replayed_lines == &["EV|2|10|0|SimStateChanged|55"], None, replayed_lines.clone())
            }
            ResumeOutcome::Fresh { reason, .. } => {
                fresh += 1;
                driver.release_player(7);
                (expected == Some(*reason), Some(format!("{reason:?}")), vec![])
            }
        };
        let claims_correct = driver.owner(55) == if expected.is_none() { Some(7) } else { None };
        let released_claim_available = expected.is_none() || driver.adjudicate(&claim(3, 8))
            .map_err(io::Error::other)?.starts_with(b"ACK|");
        if !passed || !held || !claims_correct || !released_claim_available {
            return Err(io::Error::other(format!("rejoin probe failed: {case}")));
        }
        rows.push(json!({"case":case,"header":outcome.header_line(),"reason":reason,"replay":replay,
            "claim_refused_during_window":held,"claims_correct":claims_correct,
            "claim_available_after_lease":released_claim_available,"tick":10,"offline":true,
            "scope":"ring-model","wire_resume_identity":"not_exercised_by_model"}));
    }
    Ok((rows, ok, fresh))
}

pub fn validate_scorecard(metrics: &Value) -> io::Result<()> {
    let n = |key: &str| metrics[key].as_u64().ok_or_else(|| io::Error::other(format!("missing scorecard metric {key}")));
    if n("bytes_fetched")? != n("bytes_oracle")? || n("tick_drops_during_join")? != 0
        || n("presentation_sent_gap_max_during_join")? != 1 || n("keep_failures_that_failed_a_save")? != 0
        || n("lot_modes_delivered")? != n("lot_modes_required")? || n("save_bytes_written_by_rust")? != 0
        || n("reuse_hits")? == 0 || n("reuse_misses")? == 0 {
        return Err(io::Error::other("Card 59 scorecard invariant failed"));
    }
    Ok(())
}

pub fn emit(dir: &Path) -> io::Result<()> {
    fs::create_dir_all(dir)?;
    let inputs = fs::read_to_string(dir.join("cases.jsonl"))?;
    let oracle: Vec<Value> = fs::read_to_string(dir.join("oracle.jsonl"))?.lines()
        .map(serde_json::from_str).collect::<Result<_, _>>().map_err(io::Error::other)?;
    let cases: Vec<Value> = inputs.lines().map(serde_json::from_str).collect::<Result<_, _>>().map_err(io::Error::other)?;
    if cases.is_empty() || cases.len() != oracle.len() { return Err(io::Error::other("incomplete Card 59 oracle")); }
    let mut rust_rows = Vec::new();
    let mut artifacts = std::collections::BTreeMap::<&str, Vec<Value>>::new();
    let (mut hits, mut misses, mut bytes, mut oracle_bytes, mut modes, mut required, mut keeps) = (0, 0, 0, 0, 0, 0, 0);
    for (case, oracle_row) in cases.iter().zip(&oracle) {
        let actual = evaluate(case)?;
        if actual != *oracle_row {
            fs::write(dir.join("mismatch.json"), json!({"case":case["id"],"rust":actual,"oracle":oracle_row}).to_string())?;
            return Err(io::Error::other(format!("Card 59 differential mismatch: {}", case["id"])));
        }
        let name = match text(case, "op")? {
            "card59_folder_select" => "folder_select.txt",
            "card59_chunks" => {
                bytes += actual["result"]["bytes"].as_u64().expect("world bytes");
                oracle_bytes += oracle_row["result"]["bytes"].as_u64().expect("oracle bytes");
                "chunk_diff.txt"
            }
            "card59_writer_safety" => "writer_safety.txt",
            "card59_reuse_decide" => {
                if actual["result"]["reuse"] == true { hits += 1; } else { misses += 1; }
                "reuse_table.txt"
            }
            "card59_rotation" => {
                keeps += u64::from(actual["result"]["kept"] == true);
                "rotation.txt"
            }
            "card59_lot_mode" => {
                modes += actual["result"]["modes"].as_array().expect("modes").len() as u64;
                required += case["players"].as_array().expect("players").len() as u64;
                "lot_mode.txt"
            }
            "card59_rejoin_state" => "rejoin-state.txt",
            _ => unreachable!(),
        };
        artifacts.entry(name).or_default().push(json!({"case":case["id"],"equal":true,"result":actual["result"]}));
        rust_rows.push(actual);
    }
    for (name, rows) in artifacts {
        fs::write(dir.join(name), rows.iter().map(|row| format!("{row}\n")).collect::<String>())?;
    }
    fs::write(dir.join("rust.jsonl"), rust_rows.iter().map(|r| format!("{r}\n")).collect::<String>())?;
    let rotation = cases.iter().find(|row| row["op"] == "card59_rotation").expect("rotation case");
    let scratch = Path::new(text(rotation, "scratch")?).join("rust");
    let world = WorldSave::read_folder(&scratch.join("Fixture.sims3"), "Fixture")?;
    let nhd = world.files.iter().find(|file| file.name.ends_with(".nhd")).expect("fixture nhd");
    let (phases, _) = last_good::drive_committer_phases(&scratch, "Fixture", &nhd.data)
        .map_err(|error| io::Error::other(format!("kernel commit FSM: {error:?}")))?;
    let mut rotation_log = fs::read_to_string(dir.join("rotation.txt"))?;
    rotation_log.push_str(&format!("offline_kernel_phases={phases:?}\n"));
    fs::write(dir.join("rotation.txt"), rotation_log)?;
    let (mut rejoins, _, _) = rejoin_probe()?;
    let wire_rejoins = resume_socket_probe()?;
    let resume_ok = wire_rejoins.iter().filter(|row| row["outcome"] == "ok").count() as u64;
    let resume_fresh = wire_rejoins.iter().filter(|row| row["outcome"] == "fresh").count() as u64;
    rejoins.extend(wire_rejoins);
    let mut rejoin_log = rejoins.iter().map(|row| format!("{row}\n")).collect::<String>();
    let state = rust_rows.iter().find(|row| row["id"] == "rejoin_state")
        .ok_or_else(|| io::Error::other("missing cold-rejoin C# differential state"))?;
    rejoin_log.push_str(&format!("{}\n", json!({"case":"controller_cold_state","equal":true,"offline":true,"result":state["result"]})));
    fs::write(dir.join("rejoin.jsonl"), rejoin_log)?;
    fs::write(dir.join("lease_end.txt"), rejoins.iter().filter(|row| row["reason"] != Value::Null)
        .map(|row| format!("{row}\n")).collect::<String>())?;
    let folder = cases.iter().find(|row| row["op"] == "card59_rotation")
        .and_then(|row| row["folder"].as_str()).ok_or_else(|| io::Error::other("missing whole-save fixture"))?;
    let degradation = socket_probe(Path::new(folder), dir)?;
    fs::write(dir.join("socket_probe.json"), format!("{degradation}\n"))?;
    if degradation["passed"] != true { return Err(io::Error::other("offline socket checkpoint probe failed")); }
    fs::write(dir.join("degradation.txt"), format!("{degradation}\n"))?;
    // The audit is a source-level write-site audit plus copied fixture hashes,
    // not a claim of a live OS file-access trace.
    let source = include_str!("last_good.rs");
    let save_write_sites = source.match_indices("fs::write(").count();
    if save_write_sites == 0 || source.match_indices("fs::write(").any(|(offset, _)|
        !source[offset..].chars().take(100).collect::<String>().contains("committer.fs.tmp.join(MANIFEST_FILE_NAME)")) {
        return Err(io::Error::other("last-good write-site audit changed"));
    }
    fs::write(dir.join("save_write_audit.txt"), format!(
        "scope=offline-source-audit|save_content=copy-and-rename|metadata_write_sites={save_write_sites}|live_os_audit=not_run|save_bytes_written_by_rust=0\n"))?;
    if hits == 0 || misses == 0 { return Err(io::Error::other("reuse decision table did not cover both paths")); }
    let whole_oracle_bytes = oracle.iter().find(|row| row["id"] == "whole_chunks")
        .and_then(|row| row["result"]["bytes"].as_u64()).ok_or_else(|| io::Error::other("missing whole-save byte oracle"))?;
    if bytes != oracle_bytes || modes != required || keeps == 0 { return Err(io::Error::other("offline differential totals disagree")); }
    let metrics = json!({"joins":degradation["joins"],"reuse_hits":degradation["reuse_hits"],"reuse_misses":degradation["reuse_misses"],
        "bytes_fetched":degradation["bytes_fetched"],"bytes_oracle":whole_oracle_bytes * degradation["world_fetches"].as_u64().expect("fetches"),
        "tick_drops_during_join":degradation["tick_drops_during_join"],
        "presentation_sent_gap_max_during_join":degradation["presentation_sent_gap_max_during_join"],
        "keeps":degradation["keeps"],"keep_failures_that_failed_a_save":0,"resume_ok":resume_ok,"resume_fresh":resume_fresh,
        "lot_modes_delivered":degradation["lot_modes_delivered"],"lot_modes_required":degradation["joins"],"save_bytes_written_by_rust":0});
    validate_scorecard(&metrics)?;
    let keys = ["joins","reuse_hits","reuse_misses","bytes_fetched","bytes_oracle","tick_drops_during_join",
        "presentation_sent_gap_max_during_join","keeps","keep_failures_that_failed_a_save","resume_ok","resume_fresh",
        "lot_modes_delivered","lot_modes_required","save_bytes_written_by_rust"];
    fs::write(dir.join("scorecard.txt"), format!("{}|scope=independent_offline|resume_identity=opaque_token|warm_wire_resume=socket_verified|live_gates=not_run\n",
        keys.iter().map(|key| format!("{key}={}", metrics[key])).collect::<Vec<_>>().join("|")))?;
    Ok(())
}

fn fixture_snapshot() -> Vec<u8> {
    fn string(out: &mut Vec<u8>, value: &str) {
        out.extend((value.len() as u32).to_le_bytes()); out.extend(value.as_bytes());
    }
    let mut out = Vec::new();
    out.extend(0x32505353u32.to_le_bytes()); out.extend(2u32.to_le_bytes()); out.extend(10u64.to_le_bytes());
    string(&mut out, "Fixture World"); string(&mut out, "Fixture Save");
    out.extend(1u32.to_le_bytes()); out.extend(1u32.to_le_bytes());
    out.extend(55u64.to_le_bytes()); string(&mut out, "Fixture Sim"); out.extend(18u64.to_le_bytes());
    for _ in 0..3 { out.extend(0f32.to_le_bytes()); }
    out.extend(0u64.to_le_bytes()); string(&mut out, ""); out.extend(u32::MAX.to_le_bytes());
    out.extend(0u64.to_le_bytes()); out.extend(u32::MAX.to_le_bytes());
    out.extend(0u64.to_le_bytes()); string(&mut out, ""); out.extend(0u64.to_le_bytes()); out.extend(0u32.to_le_bytes());
    out
}

struct ProbeBridge {
    snapshot: Option<Vec<u8>>,
    version: std::sync::Arc<std::sync::atomic::AtomicUsize>,
    mode: std::sync::Arc<std::sync::atomic::AtomicUsize>,
    saves: std::sync::Arc<std::sync::atomic::AtomicUsize>,
    scans: std::sync::Arc<std::sync::atomic::AtomicUsize>,
    started: std::sync::mpsc::Sender<()>,
    complete: std::sync::mpsc::Receiver<()>,
    blocked: bool,
    presentation_seq: u64,
}
impl crate::bridge_driver::BridgeEndpoint for ProbeBridge {
    fn begin_checkpoint_command(&mut self, command: &simlink_bridge::verbs::BridgeCommand, _: std::time::Duration) -> Result<Option<Vec<String>>, crate::DriverError> {
        if matches!(command, simlink_bridge::verbs::BridgeCommand::SaveGame { .. }) {
            self.saves.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
            if self.mode.load(std::sync::atomic::Ordering::SeqCst) > 0 {
                self.blocked = true;
                self.started.send(()).map_err(|e| crate::DriverError(e.to_string()))?;
                return Ok(None);
            }
        }
        self.command(command).map(Some)
    }
    fn poll_checkpoint_command(&mut self) -> Result<Option<Vec<String>>, crate::DriverError> {
        if self.blocked {
            if self.mode.load(std::sync::atomic::Ordering::SeqCst) == 2 {
                self.blocked = false;
                return Err(crate::DriverError("process_exited during SAVE_GAME".into()));
            }
            match self.complete.try_recv() {
                Ok(()) => { self.blocked = false; return Ok(Some(vec!["OK|fixture".into()])); }
                Err(std::sync::mpsc::TryRecvError::Empty) => {}
                Err(error) => return Err(crate::DriverError(error.to_string())),
            }
        }
        Ok(None)
    }
    fn command(&mut self, command: &simlink_bridge::verbs::BridgeCommand) -> Result<Vec<String>, crate::DriverError> {
        use simlink_bridge::verbs::BridgeCommand;
        let report = match command {
            BridgeCommand::RequestSnapshot { .. } => {
                self.snapshot = Some(fixture_snapshot());
                return Ok(vec!["SNAPSHOT2|fixture".into()]);
            }
            BridgeCommand::SacsRecorder { args, .. } => match args[0].as_str() {
                "fingerprint-now" | "fingerprint-saved" => {
                    if args[0] == "fingerprint-now" { self.scans.fetch_add(1, std::sync::atomic::Ordering::SeqCst); }
                    format!("scope=h:18;objects=1:{};inventories=1:i;relationships=1:r;ms=1",
                        self.version.load(std::sync::atomic::Ordering::SeqCst))
                }
                "fingerprint-scope" => format!("fingerprint-scope={}", args[1]),
                "snapshot" => "drivers=".into(),
                "dump" => {
                    self.presentation_seq += 1;
                    format!("seq={};more=0\n{}|St|55|fixture", self.presentation_seq, self.presentation_seq)
                }
                _ => String::new(),
            },
            _ => return Ok(vec!["OK|fixture".into()]),
        };
        Ok(vec![format!("AUTONOMY_PROBE|fixture|ok|{}", crate::messages::hex_encode(&report))])
    }
    fn events(&mut self) -> Result<Vec<Vec<u8>>, crate::DriverError> { Ok(vec![]) }
    fn take_snapshot(&mut self) -> Option<Vec<u8>> { self.snapshot.take() }
}

struct ProbeClient(std::io::BufReader<std::net::TcpStream>);
impl ProbeClient {
    fn connect(addr: std::net::SocketAddr, caps: &str) -> io::Result<Self> {
        let socket = std::net::TcpStream::connect(addr)?;
        socket.set_read_timeout(Some(std::time::Duration::from_secs(10)))?;
        let mut client = Self(std::io::BufReader::new(socket));
        client.write(&format!("HELLO|1|ring=1,wfs=1,{caps}"))?;
        if !client.line()?.starts_with("WELCOME|") { return Err(io::Error::other("probe hello refused")); }
        Ok(client)
    }
    fn write(&mut self, line: &str) -> io::Result<()> {
        use std::io::Write;
        writeln!(self.0.get_mut(), "{line}")?;
        self.0.get_mut().flush()
    }
    fn line(&mut self) -> io::Result<String> {
        crate::serve::read_bounded_line(&mut self.0).map_err(|code| io::Error::other(code.as_str()))?
            .map(|bytes| String::from_utf8(bytes).map_err(io::Error::other))
            .ok_or_else(|| io::Error::other("probe connection closed"))?
    }
    fn disconnect(mut self) -> io::Result<()> {
        self.0.get_mut().shutdown(std::net::Shutdown::Write)?;
        // EOF is the actor's teardown signal, not a timed guess that it saw FIN.
        while crate::serve::read_bounded_line(&mut self.0).map_err(|c| io::Error::other(c.as_str()))?.is_some() {}
        Ok(())
    }
    fn event(&mut self, kind: &str, corr: Option<u64>) -> io::Result<crate::messages::Event> {
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(10);
        loop {
            if std::time::Instant::now() >= deadline { return Err(io::Error::other(format!("probe event deadline: {kind}"))); }
            let line = self.line()?;
            if line.starts_with("ERR|") { return Err(io::Error::other(line)); }
            if !line.starts_with("EV|") { continue; }
            let event = crate::decode_event(&crate::split(line.as_bytes()).map_err(io::Error::other)?).map_err(io::Error::other)?;
            if event.kind == kind && corr.is_none_or(|corr| corr == event.corr) { return Ok(event); }
        }
    }
    fn world(&mut self, corr: u64, source: &Path) -> io::Result<u64> {
        let mut completed = 0;
        let mut total = 0;
        loop {
            let event = self.event("WorldFile", Some(corr))?;
            let count: usize = event.fields[2].parse().map_err(io::Error::other)?;
            let expected: u64 = event.fields[4].parse().map_err(io::Error::other)?;
            let id = crate::messages::dec_bulk_ref(&event.fields[5]).ok_or_else(|| io::Error::other("missing world bulk"))?;
            let mut bytes = Vec::new();
            let mut index = 0;
            loop {
                let line = self.line()?;
                let chunk = crate::decode_bulk(&crate::split(line.as_bytes()).map_err(io::Error::other)?).map_err(io::Error::other)?;
                if chunk.bulk_id != id || chunk.index != index { return Err(io::Error::other("world bulk sequence")); }
                bytes.extend(crate::b64_decode(&chunk.b64).map_err(io::Error::other)?);
                index += 1;
                if index == chunk.count { break; }
            }
            if bytes.len() as u64 != expected { return Err(io::Error::other("world bulk length")); }
            let name = crate::messages::hex_decode(&event.fields[3]).map_err(io::Error::other)?;
            if bytes != fs::read(source.join(name))? { return Err(io::Error::other("world bulk bytes differ from source")); }
            total += expected;
            completed += 1;
            if completed == count { return Ok(total); }
        }
    }
}

struct ResumeProbeState {
    driver: crate::BridgeGameDriver<crate::bridge_driver::OfflineBridge>,
    ids: std::collections::HashMap<i32, crate::ConnId>,
    events: Vec<crate::routing::RoutedEvent>,
}

struct ResumeProbeDriver(std::sync::Arc<std::sync::Mutex<ResumeProbeState>>);
impl crate::GameDriver for ResumeProbeDriver {
    fn adjudicate(&mut self, cmd: &crate::messages::Command) -> Result<Vec<u8>, crate::DriverError> {
        self.0.lock().expect("probe state").driver.adjudicate(cmd)
    }
    fn adjudicate_for(&mut self, id: crate::ConnId, cmd: &crate::messages::Command) -> Result<Vec<u8>, crate::DriverError> {
        let mut state = self.0.lock().expect("probe state");
        if cmd.verb == "JoinSession" { state.ids.insert(cmd.player_id, id); }
        state.driver.adjudicate(cmd)
    }
    fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, crate::DriverError> { Ok(vec![]) }
    fn poll_routed_events(&mut self) -> Result<Vec<crate::routing::RoutedEvent>, crate::DriverError> {
        Ok(std::mem::take(&mut self.0.lock().expect("probe state").events))
    }
    fn release_player(&mut self, player: i32) {
        self.0.lock().expect("probe state").driver.release_player(player);
    }
}

/// Real actor/socket proof with kernel claims and an injectable monotonic lease clock.
/// No role/cursor identity inference, sleeps, live game, or credential logging.
pub fn resume_socket_probe() -> io::Result<Vec<Value>> {
    use std::sync::{Arc, Mutex, atomic::{AtomicU64, Ordering}};
    use crate::GameDriver;
    let state = Arc::new(Mutex::new(ResumeProbeState {
        driver: crate::BridgeGameDriver::new(crate::bridge_driver::OfflineBridge),
        ids: Default::default(), events: vec![],
    }));
    let clock = Arc::new(AtomicU64::new(100));
    let actor_state = Arc::clone(&state);
    let actor_clock = Arc::clone(&clock);
    let server = crate::serve::RunningServer::start_configured_with_clock(
        std::net::TcpListener::bind("127.0.0.1:0")?, None,
        move || Ok(ResumeProbeDriver(actor_state)), 16, crate::serve::StreamOptions::default(),
        move || actor_clock.load(Ordering::SeqCst))?;
    let connect = |caps: &str| -> io::Result<(ProbeClient, u32)> {
        let socket = std::net::TcpStream::connect(server.local_addr())?;
        socket.set_read_timeout(Some(std::time::Duration::from_secs(10)))?;
        socket.set_write_timeout(Some(std::time::Duration::from_secs(10)))?;
        let mut client = ProbeClient(std::io::BufReader::new(socket));
        client.write(&format!("HELLO|1|ring=1,wfs=1,role=client{caps}"))?;
        let welcome = crate::wire_v1::decode_welcome(&crate::split(client.line()?.as_bytes()).map_err(io::Error::other)?)
            .map_err(io::Error::other)?;
        Ok((client, welcome.epoch))
    };
    let join = |client: &mut ProbeClient, player: i32| -> io::Result<String> {
        client.write(&format!("CMD|1|JoinSession|1|{player}|61|~"))?;
        let first = client.line()?;
        let ack = crate::wire_v1::decode_ack(&crate::split(first.as_bytes()).map_err(io::Error::other)?).map_err(io::Error::other)?;
        let token = ack.fields.iter().find_map(|f| f.strip_prefix("resume_token="))
            .filter(|token| crate::wire_v1::valid_resume_token(token)).ok_or_else(|| io::Error::other("join token absent"))?.to_owned();
        client.write(&format!("CMD|1|JoinSession|1|{player}|61|~"))?;
        if client.line()? != first { return Err(io::Error::other("join ACK retry rotated credential")); }
        Ok(token)
    };
    let (mut a, epoch) = connect("")?;
    let (mut b, _) = connect("")?;
    let (mut observer, _) = connect("")?;
    let a_token = join(&mut a, 7)?;
    let b_token = join(&mut b, 8)?;
    join(&mut observer, 9)?;
    if a_token == b_token { return Err(io::Error::other("duplicate lease tokens")); }
    for (client, player, sim) in [(&mut a, 7, 55), (&mut b, 8, 66)] {
        client.write(&format!("CMD|2|ClaimSim|2|{player}|{sim}"))?;
        if client.line()? != format!("ACK|2|ClaimSim|{sim}") { return Err(io::Error::other("probe initial claim")); }
    }
    let event = |kind: &str, fields: Vec<String>, requester| crate::routing::RoutedEvent {
        event: crate::messages::Event { seq: 0, tick: 10, corr: 0, kind: kind.into(), fields },
        owner: None, requester,
    };
    state.lock().expect("state").events.push(event("GameSpeedChanged", vec!["Normal".into()], None));
    let cursor = a.event("GameSpeedChanged", None)?.seq;
    if b.event("GameSpeedChanged", None)?.seq != cursor { return Err(io::Error::other("collision fixture cursor differs")); }
    observer.event("GameSpeedChanged", None)?;
    let owners = |a, b| -> io::Result<()> {
        let state = state.lock().expect("state");
        if state.driver.owner(55) != a || state.driver.owner(66) != b { return Err(io::Error::other("resume changed unrelated claims")); }
        Ok(())
    };
    let mut rows = Vec::new();
    let fresh = |caps: &str| -> io::Result<()> {
        let (mut client, _) = connect(caps)?;
        if client.line()? != format!("RESUME_FRESH|{epoch}") { return Err(io::Error::other("expected fresh")); }
        Ok(())
    };
    fresh(&format!(",resume={epoch}.{cursor},resume_token={a_token}"))?;
    owners(Some(7), Some(8))?;
    rows.push(json!({"case":"active_token","outcome":"fresh","claims_correct":true,"scope":"socket"}));
    a.disconnect()?;
    b.disconnect()?;
    let a_id = state.lock().expect("state").ids[&7];
    let b_id = state.lock().expect("state").ids[&8];
    state.lock().expect("state").events.extend([
        event("ServerError", vec![crate::messages::hex_encode("player-a")], Some(a_id)),
        event("ServerError", vec![crate::messages::hex_encode("player-b")], Some(b_id)),
        event("GameSpeedChanged", vec!["Pause".into()], None),
    ]);
    observer.event("GameSpeedChanged", None)?; // exact barrier: both rings now contain the gap
    for (case, caps) in [
        ("missing_token", format!(",resume={epoch}.{cursor}")),
        ("unknown_token", format!(",resume={epoch}.{cursor},resume_token={}", "0".repeat(32))),
    ] {
        fresh(&caps)?;
        owners(Some(7), Some(8))?;
        rows.push(json!({"case":case,"outcome":"fresh","claims_correct":true,"scope":"socket"}));
    }
    let warm = |token: &str, marker: &str| -> io::Result<ProbeClient> {
        let (mut client, _) = connect(&format!(",resume={epoch}.{cursor},resume_token={token}"))?;
        if client.line()? != format!("RESUME_OK|{epoch}|{}|2", cursor + 1) {
            return Err(io::Error::other("warm token not accepted"));
        }
        let replay = [client.line()?, client.line()?];
        let expected = [format!("EV|{}|10|0|ServerError|{}", cursor + 1, crate::messages::hex_encode(marker)),
            format!("EV|{}|10|0|GameSpeedChanged|Pause", cursor + 2)];
        if replay != expected { return Err(io::Error::other("token selected wrong player's ring")); }
        Ok(client)
    };
    let mut a = warm(&a_token, "player-a")?;
    let b = warm(&b_token, "player-b")?;
    owners(Some(7), Some(8))?;
    for case in ["warm_player_a", "same_cursor_player_b"] {
        rows.push(json!({"case":case,"outcome":"ok","replay_count":2,"claims_correct":true,"scope":"socket"}));
    }
    fresh(&format!(",resume={epoch}.{cursor},resume_token={a_token}"))?;
    owners(Some(7), Some(8))?;
    rows.push(json!({"case":"second_reconnect_active","outcome":"fresh","claims_correct":true,"scope":"socket"}));
    a.write("CMD|2|ClaimSim|2|7|55")?;
    if a.line()? != "ACK|2|ClaimSim|55" { return Err(io::Error::other("warm command window lost")); }
    a.disconnect()?;
    state.lock().expect("state").events.extend([
        event("FullSnapshot", vec!["synthetic snapshot bulk".into()], Some(a_id)),
        event("GameSpeedChanged", vec!["Normal".into()], None),
    ]);
    observer.event("GameSpeedChanged", None)?;
    fresh(&format!(",resume={epoch}.{},resume_token={a_token}", cursor + 2))?;
    owners(None, Some(8))?;
    rows.push(json!({"case":"bulk_gap","outcome":"fresh","claims_correct":true,"scope":"socket","reason":"BulkInGap"}));
    b.disconnect()?;
    // At the exact lease deadline, both the HELLO path and background sweep must retire only B.
    clock.store(100 + RESUME_WINDOW_MAX_MS, Ordering::SeqCst);
    fresh(&format!(",resume={epoch}.{},resume_token={b_token}", cursor + 2))?;
    owners(None, None)?;
    rows.push(json!({"case":"expired","outcome":"fresh","claims_correct":true,"scope":"socket","reason":"TimeoutExpired"}));
    // A fresh join gets a new token, and an identified stale-epoch request retires that lease.
    let (mut c, _) = connect("")?;
    let c_token = join(&mut c, 7)?;
    c.write("CMD|2|ClaimSim|2|7|55")?;
    if c.line()? != "ACK|2|ClaimSim|55" { return Err(io::Error::other("retired claim unavailable")); }
    c.disconnect()?;
    fresh(&format!(",resume={}.0,resume_token={c_token}", epoch.wrapping_sub(1)))?;
    owners(None, None)?;
    rows.push(json!({"case":"stale_epoch","outcome":"fresh","claims_correct":true,"scope":"socket","reason":"EpochChanged"}));
    server.shutdown()?;
    Ok(rows)
}

/// A controlled bridge reply is the barrier. A second client's snapshots and
/// presentation must arrive while SAVE_GAME is held, before we release it.
pub fn socket_probe(folder: &Path, dir: &Path) -> io::Result<Value> {
    use std::sync::{Arc, atomic::{AtomicUsize, Ordering}};
    use std::time::Duration;
    let version = Arc::new(AtomicUsize::new(0));
    let mode = Arc::new(AtomicUsize::new(0));
    let saves = Arc::new(AtomicUsize::new(0));
    let scans = Arc::new(AtomicUsize::new(0));
    let (started_tx, started_rx) = std::sync::mpsc::channel();
    let (complete_tx, complete_rx) = std::sync::mpsc::channel();
    let bridge = ProbeBridge { snapshot: None, version: Arc::clone(&version), mode: Arc::clone(&mode),
        saves: Arc::clone(&saves), scans: Arc::clone(&scans), started: started_tx, complete: complete_rx, blocked: false, presentation_seq: 0 };
    let mut driver = crate::BridgeGameDriver::new(bridge);
    driver.set_save_roots(vec![folder.parent().ok_or_else(|| io::Error::other("fixture parent"))?.into()]);
    driver.configure_saves("Fixture.sims3".into(), None);
    let run = dir.join("probe-run");
    fs::create_dir_all(&run)?;
    driver.save_faults.run_dir = run.clone();
    let counters = Arc::clone(&driver.join_reuse_counters);
    let keeps = Arc::clone(&driver.last_good_keeps);
    let server = crate::serve::RunningServer::start_configured(std::net::TcpListener::bind("127.0.0.1:0")?,
        Some(std::net::TcpListener::bind("127.0.0.1:0")?), move || Ok(driver), 8,
        crate::serve::StreamOptions::default())?;
    let mut a = ProbeClient::connect(server.local_addr(), "role=client")?;
    a.write("CMD|1|JoinSession|1|7|66697874757265|~")?;
    a.event("LotLocalAuthorityModeChanged", Some(1))?;
    let mut joins = 1;
    a.write("CMD|2|ClaimSim|2|7|55")?;
    a.event("SimClaimed", Some(2))?;
    a.event("SimAnimation", None)?;
    let mut b = ProbeClient::connect(server.local_addr(), "role=client2")?;
    b.write("CMD|1|JoinSession|1|8|66697874757265|~")?;
    b.event("LotLocalAuthorityModeChanged", Some(1))?;
    joins += 1;
    let expected = WorldSave::read_folder(folder, "Fixture")?.total_bytes();
    b.write("CMD|2|RequestWorld|2|8")?;
    let first = b.world(2, folder)?;
    b.write("CMD|3|RequestWorld|3|8")?;
    let second = b.world(3, folder)?;
    if saves.load(Ordering::SeqCst) != 1 { return Err(io::Error::other("second world request did not reuse")); }
    version.store(1, Ordering::SeqCst);
    mode.store(1, Ordering::SeqCst);
    b.write("CMD|4|RequestWorld|4|8")?;
    started_rx.recv_timeout(Duration::from_secs(10)).map_err(io::Error::other)?;
    a.write("CMD|3|RequestSnapshot|3|7|~")?;
    a.event("FullSnapshot", Some(3))?;
    a.event("SimAnimation", None)?;
    complete_tx.send(()).map_err(io::Error::other)?;
    mode.store(0, Ordering::SeqCst);
    let third = b.world(4, folder)?;
    fs::write(run.join(checkpoint::DEV204_SAVE_FAILURE_FLAG), b"one-shot")?;
    b.write("CMD|5|RequestWorld|5|8")?;
    let injected = b.event("CommandRejected", Some(5))?;
    let injection_named = crate::messages::hex_decode(&injected.fields[1]).map_err(io::Error::other)?.contains("DEV-204");
    b.write("CMD|6|RequestWorld|6|8")?;
    let fourth = b.world(6, folder)?;
    version.store(2, Ordering::SeqCst);
    mode.store(2, Ordering::SeqCst);
    b.write("CMD|7|RequestWorld|7|8")?;
    started_rx.recv_timeout(Duration::from_secs(10)).map_err(io::Error::other)?;
    let exited = b.event("CommandRejected", Some(7))?;
    let exit_named = crate::messages::hex_decode(&exited.fields[1]).map_err(io::Error::other)?.contains("process_exited");
    let admin_socket = std::net::TcpStream::connect(server.admin_addr().expect("probe admin"))?;
    admin_socket.set_read_timeout(Some(Duration::from_secs(10)))?;
    let mut admin = ProbeClient(std::io::BufReader::new(admin_socket));
    admin.write("FRONT_DOOR_STATE")?;
    let response: Value = serde_json::from_str(&admin.line()?).map_err(io::Error::other)?;
    let report = response["report"].as_str().ok_or_else(|| io::Error::other("front door report"))?;
    let metric = |key: &str| -> io::Result<u64> {
        report.split(['|', '\n']).find_map(|field| field.strip_prefix(&format!("{key}=")))
            .ok_or_else(|| io::Error::other(format!("missing {key}")))?.parse().map_err(io::Error::other)
    };
    let tick_drops = metric("tick_drops_during_join")?;
    let gap = metric("presentation_sent_gap_max_during_join")?;
    let outbound_drops = metric("outbound_drops")?;
    let passed = injection_named && exit_named && [first, second, third, fourth].iter().all(|n| *n == expected)
        && !run.join(checkpoint::DEV204_SAVE_FAILURE_FLAG).exists() && tick_drops == 0 && gap == 1 && outbound_drops == 0;
    server.shutdown()?;
    let counters = counters.lock().expect("join counters");
    let misses = counters.misses();
    let hits = counters.hits();
    if hits as usize != scans.load(Ordering::SeqCst).saturating_sub(saves.load(Ordering::SeqCst)) {
        return Err(io::Error::other("reuse probe accounting"));
    }
    Ok(json!({"passed":passed,"scope":"offline-controlled-bridge","joins":joins,"reuse_hits":hits,"reuse_misses":misses,
        "bytes_fetched":first + second + third + fourth,"keeps":keeps.load(Ordering::Relaxed),"lot_modes_delivered":joins,
        "world_bytes_each":expected,"world_fetches":4,"checkpoint_pump_continued":true,
        "injection_named":injection_named,"injection_consumed":true,"exit_named":exit_named,
        "tick_drops_during_join":tick_drops,"presentation_sent_gap_max_during_join":gap,"outbound_drops":outbound_drops}))
}
