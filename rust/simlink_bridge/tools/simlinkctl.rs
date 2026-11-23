//! `simlinkctl`: the card-56 harness and live tool (docs/dev-tools.md).
//!
//! Offline (gates 4, 5, 7 and the §H scorecard):
//!   simlinkctl verb-diff  --corpus <verb_corpus.txt> --oracle <oracle_verb_lines.txt> --out <verb_diff.jsonl>
//!   simlinkctl reply-diff --corpus <reply_corpus.txt> --oracle <oracle_reply_dumps.txt> --out <reply_diff_synthetic.jsonl>
//!   simlinkctl reply-diff --record <csharp_bridge.record> --corpus <csharp_replies.txt>
//!                         --oracle <csharp_reply_dumps.txt> --out <reply_diff.jsonl>
//!                         [--row-records <csharp_replies.records.txt>]
//!                         (gate 5 over the recorded capture: the reply column of the extractor's file from that
//!                         record, EVENT pushes included; dumps from the oracle's decode-replies mode over it; the
//!                         row-records file names each row's record, so step (d)'s rows name csharp_bridge.2.record)
//!   simlinkctl float-diff --oracle <oracle_float_lines.txt>
//!   simlinkctl encode     --corpus <live corpus>   (one JSON line per row to stdout: {"verb","line"} as roundtrip
//!                         sends it, or {"verb","refused"}; scripts/bridgectl.py send-corpus reads it, gate 6 step a)
//!   simlinkctl audit
//!   simlinkctl scorecard  --run-dir <dir> [--bridge-command-cs <path>]
//! Live (gates 3 and 6; only under the stack claim):
//!   simlinkctl discover   --pid <pid> [--timeout-ms 30000]
//!   simlinkctl roundtrip  --pid <pid> --corpus <live corpus> --out <rust_replies.jsonl> --record <rust_bridge.record>
//!                         [--timeout-ms 30000]
//!   simlinkctl live-diff  --rust <rust_replies.jsonl> --csharp <csharp_replies.txt> --out <live_roundtrip.jsonl>
//!                         (csharp_replies.txt from scripts/bridge_record_replies.py; replies compared on shape;
//!                         a row the mod answered without running the handler's body is marked `"body":"skipped"`,
//!                         and the last line's `verbs_live` counts verbs, not rows)
#![forbid(unsafe_code)]

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::process::ExitCode;
use std::time::{Duration, Instant};

use simlink_bridge::corpus::{json_str, parse_command, rows};
use simlink_bridge::live::{blank_marks, roundtrip_row, BlankMark, CsRow, RustRow};
use simlink_bridge::reply::dump_line;
use simlink_bridge::scorecard::{allow_unsafe_check, audit, unsafe_modules, Scorecard};
use simlink_bridge::text::{format_f2, format_r};
use simlink_bridge::verbs::{declared_records, encode, refused_rows, ReplyShape, VERB_TABLE};
use simlink_bridge::{BridgeDriver, ProcessAttach};

fn arg(args: &[String], name: &str) -> Option<String> {
    args.iter().position(|a| a == name).and_then(|i| args.get(i + 1)).cloned()
}

fn need(args: &[String], name: &str) -> Result<String, String> {
    arg(args, name).ok_or_else(|| format!("missing {name}"))
}

fn read(path: &str) -> Result<String, String> {
    std::fs::read_to_string(path).map_err(|e| format!("{path}: {e}"))
}

fn write(path: &str, text: &str) -> Result<(), String> {
    std::fs::write(path, text).map_err(|e| format!("{path}: {e}"))
}

fn crate_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
}

/// The oracle's per-row outputs: `Record<TAB>escaped line` (or `<refused>`), in corpus order.
fn oracle_rows(path: &str) -> Result<Vec<(String, String)>, String> {
    Ok(rows(&read(path)?).into_iter().map(|r| (r[0].clone(), r.get(1).cloned().unwrap_or_default())).collect())
}

/// Gate 4: one JSONL row per verb of the table.
fn verb_diff(args: &[String]) -> Result<bool, String> {
    let corpus = rows(&read(&need(args, "--corpus")?)?);
    let oracle = oracle_rows(&need(args, "--oracle")?)?;
    if corpus.len() != oracle.len() {
        return Err(format!("corpus has {} rows, oracle output {}", corpus.len(), oracle.len()));
    }
    let mut per_verb: BTreeMap<&str, (usize, usize, Vec<String>)> = BTreeMap::new();
    for (row, (orecord, oline)) in corpus.iter().zip(&oracle) {
        let cmd = parse_command(row)?;
        if cmd.record() != orecord {
            return Err(format!("row order differs: {} vs {orecord}", cmd.record()));
        }
        let entry = per_verb.entry(cmd.record()).or_default();
        entry.0 += 1;
        match encode(&cmd) {
            Ok(line) if &line == oline => entry.1 += 1,
            Ok(line) => entry.2.push(format!("rust={line} oracle={oline}")),
            Err(refusal) if cmd.row().refused => {
                entry.1 += 1;
                entry.2.push(refusal.name());
            }
            Err(refusal) => entry.2.push(format!("unexpected refusal {}", refusal.name())),
        }
    }
    let mut out = String::new();
    let mut all = true;
    for row in VERB_TABLE.iter() {
        let (cases, matched, notes) = per_verb.get(row.record).cloned().unwrap_or_default();
        let ok = cases > 0 && cases == matched;
        all &= ok;
        if row.refused {
            out.push_str(&format!(
                "{{\"verb\":{},\"wire\":{},\"cases\":{cases},\"refused\":true,\"refusal\":{},\"pass\":{ok}}}\n",
                json_str(row.record),
                json_str(row.wire),
                json_str(notes.first().map_or("", String::as_str))
            ));
        } else {
            out.push_str(&format!(
                "{{\"verb\":{},\"wire\":{},\"cases\":{cases},\"byte_identical\":{matched},\"pass\":{ok}{}}}\n",
                json_str(row.record),
                json_str(row.wire),
                if notes.is_empty() { String::new() } else { format!(",\"diffs\":{}", json_str(&notes.join(" ; "))) }
            ));
        }
    }
    write(&need(args, "--out")?, &out)?;
    println!("verb-diff: {} verbs, all pass = {all}", VERB_TABLE.len());
    Ok(all)
}

/// The reply-corpus tags a verb's reply shape reads (ERROR lines belong to every verb with a reply).
fn tags_for(shape: ReplyShape) -> Vec<String> {
    match shape {
        ReplyShape::Ok => vec!["OK".into(), "ERROR".into()],
        ReplyShape::SessionReady => vec!["SESSION_READY".into(), "ERROR".into()],
        ReplyShape::Snapshot => vec!["SNAPSHOT".into(), "SNAPSHOT_CHUNK".into(), "BULK_REF".into(), "ERROR".into()],
        ReplyShape::Report(name) => vec![name.to_string(), "ERROR".into()],
        ReplyShape::None => vec![],
        ReplyShape::Any => vec!["*".into()],
    }
}

/// One reply line of a recorded capture: its tag ([`capture_tag`]), whether the Rust dump equals the oracle's, and
/// the record file its frame was read from (card 56 §G gate 5: each `reply_diff.jsonl` row names its record).
struct CaptureLine {
    tag: String,
    ok: bool,
    diff: String,
    record: String,
}

/// One `reply_diff.jsonl` row over a recorded capture.
struct CaptureVerdict {
    verb: String,
    reply: String,
    lines: usize,
    matched: usize,
    refused: bool,
    pass: bool,
    /// Listed `#@unsent` by a gate 6 corpus and absent from the capture: not a failure, not a pass (gate 5 pending).
    unsent: bool,
    /// The record file(s) the rows of this verb came from, comma joined: the second C# record for step (d)'s rows
    /// (card 56 §G gate 5, c9a23656), or every record the run read when the verb has no line of its own.
    record: String,
    diffs: Vec<String>,
}

/// What a `csharp_replies.txt` row's reply is judged as: a command row's wire verb, or Raw's table wire when the
/// verb is no table wire (Raw sends any name; its table wire is a placeholder), and None when no reply came, as there
/// is nothing to decode; for an empty-command row (a host frame no command claimed), `EVENT|<kind>` for a push and
/// `HOST` for any other line (the HELLO, a late reply), which is compared but belongs to no verb.
fn capture_tag(row: &[String]) -> Option<String> {
    let at = |i: usize| row.get(i).map_or("", String::as_str);
    let (wire, command, reply) = (at(0), at(1), at(2));
    if !command.is_empty() {
        if reply.is_empty() {
            return None;
        }
        if VERB_TABLE.iter().any(|v| v.wire == wire) {
            return Some(wire.to_string());
        }
        return VERB_TABLE.iter().find(|v| v.record == "Raw").map(|v| v.wire.to_string());
    }
    match reply.strip_prefix("EVENT|") {
        Some(rest) => Some(format!("EVENT|{}", rest.split('|').next().unwrap_or(""))),
        None => Some("HOST".to_string()),
    }
}

/// Gate 5 over a recorded capture: each verb is judged on the replies that answered it (a verb that expects a reply
/// and has none in the capture fails), and each EVENT kind the capture holds gets a row of its own (card 56 gate 5:
/// every kind is decoded). A capture with no push gets one `EVENT` row with no lines.
#[cfg(test)]
fn capture_verdicts(lines: &[CaptureLine]) -> Vec<CaptureVerdict> {
    let named: String = lines.first().map_or(String::new(), |l| l.record.clone());
    capture_verdicts_named(lines, &[], &named)
}

/// [`capture_verdicts`], where a verb in `unsent` (a gate 6 corpus's `#@unsent`, card 56 4c970a49 row 208) that has
/// no capture lines is marked unsent instead of failing. One that has lines is judged as any other. A verb's `record`
/// field names the record its lines were read from; with none, every record the run read (`fallback`).
fn capture_verdicts_named(lines: &[CaptureLine], unsent: &[String], fallback: &str) -> Vec<CaptureVerdict> {
    let records_in = |mine: &[&CaptureLine]| -> String {
        let mut names: Vec<String> = Vec::new();
        for l in mine {
            if !names.contains(&l.record) {
                names.push(l.record.clone());
            }
        }
        if names.is_empty() { fallback.to_string() } else { names.join(",") }
    };
    let judge = |verb: &str, reply: String, tag: &str, refused: bool, needs_lines: bool| {
        let mine: Vec<&CaptureLine> = lines.iter().filter(|l| l.tag == tag).collect();
        let diffs: Vec<String> = mine.iter().filter(|l| !l.ok).map(|l| l.diff.clone()).collect();
        let pass = refused || (diffs.is_empty() && (!needs_lines || !mine.is_empty()));
        let unsent = !pass && mine.is_empty() && unsent.iter().any(|u| u == verb);
        let record = records_in(&mine);
        CaptureVerdict { verb: verb.to_string(), reply, lines: mine.len(), matched: mine.len() - diffs.len(), refused, pass, unsent, record, diffs }
    };
    let mut out: Vec<CaptureVerdict> = VERB_TABLE
        .iter()
        .map(|row| judge(row.record, row.reply.describe(), row.wire, row.refused, !matches!(row.reply, ReplyShape::None)))
        .collect();
    let mut kinds: Vec<&str> = lines.iter().map(|l| l.tag.as_str()).filter(|t| t.starts_with("EVENT|")).collect();
    kinds.sort_unstable();
    kinds.dedup();
    if kinds.is_empty() {
        out.push(judge("EVENT", "event push".to_string(), "EVENT", false, false));
    }
    for kind in kinds {
        out.push(judge(kind, "event push".to_string(), kind, false, true));
    }
    out
}

/// Gate 5 over the recorded capture (`--record`): the reply column of the `csharp_replies.txt` that
/// `bridge_record_replies.py` wrote from that record (every host frame, EVENT pushes included), Rust dumps against
/// the oracle's `decode-replies` dumps of the same file; every row names the record in its `record` field. With
/// `--row-records <file>`, the extractor's one-record-name-per-row file (gate 6 step (d)'s rows, appended after
/// record 1's, name `csharp_bridge.2.record`; card 56 §G gate 5, c9a23656).
fn reply_diff_capture(args: &[String], record: &str) -> Result<bool, String> {
    if !Path::new(record).is_file() {
        return Err(format!("{record}: no such capture"));
    }
    let corpus_path = need(args, "--corpus")?;
    let corpus = rows(&read(&corpus_path)?);
    let oracle_text = read(&need(args, "--oracle")?)?;
    let oracle: Vec<&str> = oracle_text.lines().filter(|l| !l.trim().is_empty()).collect();
    if corpus.len() != oracle.len() {
        return Err(format!("reply file has {} rows, oracle output {}", corpus.len(), oracle.len()));
    }
    let (record, input) = (record.replace('\\', "/"), corpus_path.replace('\\', "/"));
    let mut records: Vec<String> = Vec::new();
    if let Some(path) = arg(args, "--row-records") {
        records = read(&path)?
            .lines()
            .filter(|l| !l.trim().is_empty())
            .map(|l| l.replace('\\', "/"))
            .collect();
        if records.len() != corpus.len() {
            return Err(format!(
                "{path}: the row records file names {} rows, the reply file has {}",
                records.len(),
                corpus.len()
            ));
        }
    }
    let lines: Vec<CaptureLine> = corpus
        .iter()
        .enumerate()
        .zip(&oracle)
        .filter_map(|((at, row), oline)| {
            let tag = capture_tag(row)?;
            let line = row.get(2).cloned().unwrap_or_default();
            let (rust, theirs) = (dump_line(&line), oline.split_once('\t').map_or("", |(_, d)| d));
            let ok = rust == theirs;
            let record = records.get(at).cloned().unwrap_or_else(|| record.clone());
            Some(CaptureLine { tag, ok, diff: if ok { String::new() } else { format!("line={line} rust={rust} oracle={theirs}") }, record })
        })
        .collect();
    let mut unsent = Vec::new();
    for pair in args.windows(2).filter(|p| p[0] == "--unsent-from") {
        let text = read(&pair[1])?;
        unsent.extend(text.lines().filter_map(|l| l.strip_prefix("#@unsent ")).filter_map(|l| l.split(' ').next()).map(String::from));
    }
    let verdicts = capture_verdicts_named(&lines, &unsent, &record);
    let mut out = String::new();
    for v in &verdicts {
        out.push_str(&format!(
            "{{\"verb\":{},\"reply\":{},\"record\":{},\"input\":{},\"lines\":{},\"fields_identical\":{},\"refused\":{},\"pass\":{}{}{}}}\n",
            json_str(&v.verb),
            json_str(&v.reply),
            json_str(&v.record),
            json_str(&input),
            v.lines,
            v.matched,
            v.refused,
            v.pass,
            if v.unsent { ",\"unsent\":true" } else { "" },
            if v.diffs.is_empty() { String::new() } else { format!(",\"diffs\":{}", json_str(&v.diffs.join(" ; "))) }
        ));
    }
    let bad = lines.iter().filter(|l| !l.ok).count();
    let all = verdicts.iter().all(|v| v.pass || v.unsent);
    write(&need(args, "--out")?, &out)?;
    println!("reply-diff (capture {record}): {} reply lines, {bad} differ; all verbs pass = {all}", lines.len());
    Ok(all && bad == 0)
}

/// Gate 5: the canonical dumps of every corpus line, Rust against the oracle; one row per verb. With `--record`,
/// the corpus is a recorded capture's host lines ([`reply_diff_capture`]); without it, the synthetic supplement.
fn reply_diff(args: &[String]) -> Result<bool, String> {
    if let Some(record) = arg(args, "--record") {
        return reply_diff_capture(args, &record);
    }
    let corpus_path = need(args, "--corpus")?;
    let corpus = rows(&read(&corpus_path)?);
    // The oracle's dumps are compared as written (escaped), so they are split, not unescaped.
    let oracle_text = read(&need(args, "--oracle")?)?;
    let oracle: Vec<&str> = oracle_text.lines().filter(|l| !l.trim().is_empty()).collect();
    if corpus.len() != oracle.len() {
        return Err(format!("corpus has {} rows, oracle output {}", corpus.len(), oracle.len()));
    }
    let mut results: Vec<(String, bool, String)> = Vec::new();
    for (row, oline) in corpus.iter().zip(&oracle) {
        let line = row.get(1).cloned().unwrap_or_default();
        let rust = dump_line(&line);
        let theirs = oline.split_once('\t').map_or("", |(_, d)| d).to_string();
        results.push((row[0].clone(), rust == theirs, if rust == theirs { String::new() } else { format!("line={line} rust={rust} oracle={theirs}") }));
    }
    let mut out = String::new();
    let mut all = true;
    for row in VERB_TABLE.iter() {
        let tags = tags_for(row.reply);
        let mine: Vec<&(String, bool, String)> =
            results.iter().filter(|(tag, _, _)| tags.iter().any(|t| t == "*" || t == tag)).collect();
        let matched = mine.iter().filter(|(_, ok, _)| *ok).count();
        let diffs: Vec<&str> = mine.iter().filter(|(_, ok, _)| !ok).map(|(_, _, d)| d.as_str()).collect();
        // A verb with no reply (DISCONNECT) or a refused one has nothing to decode and passes vacuously.
        let ok = row.refused || matches!(row.reply, ReplyShape::None) || (!mine.is_empty() && diffs.is_empty());
        all &= ok;
        out.push_str(&format!(
            "{{\"verb\":{},\"reply\":{},\"trace\":{},\"lines\":{},\"fields_identical\":{matched},\"refused\":{},\"pass\":{ok}{}}}\n",
            json_str(row.record),
            json_str(&row.reply.describe()),
            json_str(&corpus_path.replace('\\', "/")),
            mine.len(),
            row.refused,
            if diffs.is_empty() { String::new() } else { format!(",\"diffs\":{}", json_str(&diffs.join(" ; "))) }
        ));
    }
    let total_bad = results.iter().filter(|(_, ok, _)| !ok).count();
    write(&need(args, "--out")?, &out)?;
    println!("reply-diff: {} lines, {} differ; all verbs pass = {all}", results.len(), total_bad);
    Ok(all && total_bad == 0)
}

/// Each corpus row as the line `roundtrip` sends (same encoder, same correlation id), or its named refusal.
fn encode_jsonl(corpus: &[Vec<String>]) -> Result<String, String> {
    let mut out = String::new();
    for row in corpus {
        let cmd = parse_command(row)?;
        match encode(&cmd) {
            Ok(line) => out.push_str(&format!("{{\"verb\":{},\"line\":{}}}\n", json_str(cmd.record()), json_str(&line))),
            Err(refusal) => {
                out.push_str(&format!("{{\"verb\":{},\"refused\":{}}}\n", json_str(cmd.record()), json_str(&refusal.name())))
            }
        }
    }
    Ok(out)
}

/// Gate 6 step (a)'s input: the lines the Rust half will send, for the C# half to send first.
fn encode_corpus(args: &[String]) -> Result<bool, String> {
    print!("{}", encode_jsonl(&rows(&read(&need(args, "--corpus")?)?))?);
    Ok(true)
}

/// .NET float formatting against the oracle's `R` and `F2` for the same bit patterns.
fn float_diff(args: &[String]) -> Result<bool, String> {
    let oracle = rows(&read(&need(args, "--oracle")?)?);
    let mut bad = 0;
    for r in &oracle {
        let bits = u32::from_str_radix(&r[0], 16).map_err(|e| e.to_string())?;
        let v = f32::from_bits(bits);
        let (rr, f2) = (format_r(v), format_f2(v));
        if rr != r[1] || f2 != r[2] {
            bad += 1;
            eprintln!("bits={} rust R={rr} F2={f2} oracle R={} F2={}", r[0], r[1], r[2]);
        }
    }
    println!("float-diff: {} values, {bad} differ", oracle.len());
    Ok(bad == 0)
}

fn print_audit() -> Result<bool, String> {
    let rows = audit(&crate_dir()).map_err(|e| e.to_string())?;
    for (path, n, forbids) in &rows {
        println!("{path}\tunsafe={n}\tforbid_unsafe_code={forbids}");
    }
    let u = unsafe_modules(&rows);
    println!("unsafe_modules={u}");
    // Card 56 U1: the crate root's one `#[allow(unsafe_code)]`, on `mod ffi`, and nowhere else.
    let allow = allow_unsafe_check(&crate_dir()).map_err(|e| e.to_string())?;
    match &allow {
        Ok(site) => println!("allow_unsafe_code=1|site={site}"),
        Err(e) => println!("allow_unsafe_code=FAIL|{e}"),
    }
    Ok(u == 0 && allow.is_ok())
}

/// The verbs a diff file counts as passing: named by a `"pass":true` row and by no failing row (§H counts verbs,
/// not rows, so a verb with two rows counts once and not at all when either row failed).
fn count_pass(path: &Path) -> Result<Vec<String>, String> {
    let text = read(&path.to_string_lossy())?;
    let verb_of = |l: &str| l.split("\"verb\":\"").nth(1).and_then(|r| r.split('"').next()).map(str::to_string);
    let mut failed: Vec<String> = text.lines().filter(|l| !l.contains("\"pass\":true")).filter_map(verb_of).collect();
    failed.sort();
    failed.dedup();
    let mut ok: Vec<String> = text.lines().filter(|l| l.contains("\"pass\":true")).filter_map(verb_of).collect();
    ok.retain(|v| !failed.contains(v));
    ok.sort();
    ok.dedup();
    Ok(ok)
}

/// The §H `verbs_live` of gate 6's rows (card 56 §G gate 6, c9a23656): the verbs that round-tripped, counted once
/// however many rows a corpus gives them (a paused `SetWorldTime` sends the verb twice, for one) and not counted
/// when any of its rows failed. The same rule as [`count_pass`] over `live_roundtrip.jsonl`.
fn count_live(results: &[simlink_bridge::live::LiveResult]) -> usize {
    let mut verbs: Vec<&str> = results.iter().map(|r| r.verb.as_str()).collect();
    let failed: Vec<&str> = results.iter().filter(|r| !r.pass).map(|r| r.verb.as_str()).collect();
    verbs.sort_unstable();
    verbs.dedup();
    verbs.iter().filter(|v| !failed.contains(v)).count()
}

fn scorecard(args: &[String]) -> Result<bool, String> {
    let run_dir = PathBuf::from(need(args, "--run-dir")?);
    let cs = arg(args, "--bridge-command-cs").map(PathBuf::from).unwrap_or_else(|| {
        crate_dir().join("../../DedicatedServer/HostBridge/BridgeCommand.cs")
    });
    let declared = declared_records(&read(&cs.to_string_lossy())?);
    let refused: Vec<&str> = refused_rows().map(|r| r.record).collect();
    let enc = count_pass(&run_dir.join("verb_diff.jsonl"))?;
    // Gate 5's diff over the recorded capture; the synthetic supplement (reply_diff_synthetic.jsonl) never counts.
    let dec_path = run_dir.join("reply_diff.jsonl");
    let dec = if dec_path.exists() { count_pass(&dec_path)? } else { Vec::new() };
    let live_path = run_dir.join("live_roundtrip.jsonl");
    let live = if live_path.exists() { count_pass(&live_path)? } else { Vec::new() };
    let roundtripped = declared
        .iter()
        .filter(|v| refused.contains(&v.as_str()) || (enc.contains(v) && dec.contains(v)))
        .count();
    let verbs_live = declared.iter().filter(|v| refused.contains(&v.as_str()) || live.contains(v)).count();
    let rows = audit(&crate_dir()).map_err(|e| e.to_string())?;
    let card = Scorecard {
        verbs_declared: declared.len(),
        verbs_roundtripped: roundtripped,
        verbs_refused: refused.len(),
        verbs_live,
        unsafe_modules: unsafe_modules(&rows),
    };
    let line = card.line();
    write(&run_dir.join("scorecard.txt").to_string_lossy(), &format!("{line}\n"))?;
    println!("{line}");
    println!("offline invariant (roundtripped == declared, unsafe_modules == 0): {}", card.offline_holds());
    println!("full invariant (also verbs_live == declared): {}", card.holds());
    Ok(card.offline_holds())
}

fn timeout(args: &[String]) -> Duration {
    Duration::from_millis(arg(args, "--timeout-ms").and_then(|v| v.parse().ok()).unwrap_or(30_000))
}

fn pid(args: &[String]) -> Result<u32, String> {
    need(args, "--pid")?.parse().map_err(|e| format!("--pid: {e}"))
}

/// Gate 3: print the region base this crate's discovery finds (compare with the controller's
/// `attach candidate=0x...` trace line on the same PID).
fn discover(args: &[String]) -> Result<bool, String> {
    let pid = pid(args)?;
    let attach = ProcessAttach::open(pid).map_err(|e| e.to_string())?;
    match simlink_bridge::discover(&attach, timeout(args)) {
        Ok(base) => {
            println!("pid={pid} rust_base=0x{base:X}");
            Ok(true)
        }
        Err(refusal) => {
            println!("pid={pid} refusal={}", refusal.name());
            Ok(false)
        }
    }
}

/// Gate 6, Rust half: attach (CONNECT/HELLO), send each corpus command, wait for the reply with its
/// correlation id, and write `{"verb","line","reply","dump"}` rows. Refused verbs are recorded as refused.
/// A verb that expects a reply and gets none before the deadline is a `"timeout":true` row, and the run exits 1.
/// Every frame after HELLO is appended to `--record` in the controller's `--bridge-record` form.
fn roundtrip(args: &[String]) -> Result<bool, String> {
    let pid = pid(args)?;
    let corpus = rows(&read(&need(args, "--corpus")?)?);
    let record_path = need(args, "--record")?;
    let wait = timeout(args);
    let mut record_file = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(&record_path)
        .map_err(|e| format!("{record_path}: {e}"))?;
    let record_failed = std::sync::Arc::new(std::sync::atomic::AtomicBool::new(false));
    let (mut driver, hello) = BridgeDriver::attach_pid(pid, wait).map_err(|e| e.to_string())?;
    eprintln!("attached base=0x{:X} hello={}", driver.base(), hello.line);
    let failed = record_failed.clone();
    driver.set_frame_recorder(Box::new(move |frame: &str| {
        use std::io::Write;
        let line = simlink_bridge::record::record_line(std::time::SystemTime::now(), frame);
        if let Err(e) = writeln!(record_file, "{line}") {
            eprintln!("simlinkctl: record write failed: {e}");
            failed.store(true, std::sync::atomic::Ordering::Relaxed);
        }
    }));
    let mut out = String::new();
    let mut answered = true;
    for row in &corpus {
        let cmd = parse_command(row)?;
        let line = match encode(&cmd) {
            Ok(line) => line,
            Err(refusal) => {
                out.push_str(&format!("{{\"verb\":{},\"refused\":{}}}\n", json_str(cmd.record()), json_str(&refusal.name())));
                continue;
            }
        };
        let corr = line.split('|').nth(1).unwrap_or("").to_string();
        driver.send_line(&line).map_err(|e| e.to_string())?;
        let deadline = Instant::now() + wait;
        let mut reply = String::new();
        while Instant::now() < deadline && !matches!(cmd.row().reply, ReplyShape::None) {
            match driver.poll_line().map_err(|e| e.to_string())? {
                Some(l) if l.split('|').nth(1) == Some(corr.as_str()) => {
                    reply = l;
                    break;
                }
                Some(_) => continue,
                None => std::thread::sleep(Duration::from_millis(10)),
            }
        }
        let (jsonl, ok) = roundtrip_row(cmd.record(), &line, &reply, !matches!(cmd.row().reply, ReplyShape::None));
        if !ok {
            eprintln!("simlinkctl: no reply to {} within {} ms", cmd.record(), wait.as_millis());
        }
        answered &= ok;
        out.push_str(&jsonl);
        out.push('\n');
    }
    write(&need(args, "--out")?, &out)?;
    if record_failed.load(std::sync::atomic::Ordering::Relaxed) {
        return Err(format!("{record_path}: the frame record is incomplete"));
    }
    Ok(answered)
}

/// Gate 6 assembly: compare the Rust replies (`rust_replies.jsonl`) with the C# replies (`csharp_replies.txt`,
/// `<wire verb><TAB><command line><TAB><reply line>` from `scripts/bridge_record_replies.py`) on their decoded
/// shape ([`simlink_bridge::live`]) and write `live_roundtrip.jsonl`, `verbs_live` in its last line. Each `--corpus`
/// (the halves' corpora from `scripts/card56_corpus.py`) adds its `#@blank` marks to the pairing.
fn live_diff(args: &[String]) -> Result<bool, String> {
    let mut marks: Vec<BlankMark> = Vec::new();
    for pair in args.windows(2).filter(|p| p[0] == "--corpus") {
        marks.extend(blank_marks(&read(&pair[1])?).map_err(|e| format!("{}: {e}", pair[1]))?);
    }
    let rust: Vec<RustRow> = read(&need(args, "--rust")?)?
        .lines()
        .filter(|l| !l.trim().is_empty())
        .map(|l| {
            let field = |name: &str| l.split(&format!("\"{name}\":")).nth(1).map(unjson);
            RustRow {
                verb: field("verb").unwrap_or_default(),
                line: field("line").unwrap_or_default(),
                reply: field("reply").unwrap_or_default(),
                refused: field("refused"),
            }
        })
        .collect();
    let csharp: Vec<CsRow> = rows(&read(&need(args, "--csharp")?)?)
        .into_iter()
        .map(|r| {
            let at = |i: usize| r.get(i).cloned().unwrap_or_default();
            CsRow { wire: at(0), command: at(1), reply: at(2) }
        })
        .collect();
    let results = simlink_bridge::live::live_diff_marked(&rust, &csharp, &marks);
    let verbs: usize = results.iter().map(|r| r.verb.as_str()).collect::<std::collections::BTreeSet<_>>().len();
    let mut out = String::new();
    for r in &results {
        out.push_str(&format!(
            "{{\"verb\":{},\"refused\":{},\"pass\":{}{}{}}}\n",
            json_str(&r.verb),
            r.refused,
            r.pass,
            if r.note.is_empty() { String::new() } else { format!(",\"diff\":{}", json_str(&r.note)) },
            // Card 56 gate 6 (c9a23656): a send the mod answered without running the handler's body counts toward
            // `verbs_live` all the same, and its row says so.
            if r.body_skipped { ",\"body\":\"skipped\"" } else { "" }
        ));
    }
    let live = count_live(&results);
    out.push_str(&format!("{{\"verbs_live\":{live}}}\n"));
    write(&need(args, "--out")?, &out)?;
    println!("verbs_live={live} of {verbs} verbs ({} rows)", results.len());
    Ok(live == verbs)
}

fn unjson(field: &str) -> String {
    let s = field.trim_start();
    let Some(body) = s.strip_prefix('"') else { return String::new() };
    let mut out = String::new();
    let mut chars = body.chars();
    while let Some(c) = chars.next() {
        match c {
            '"' => break,
            '\\' => match chars.next() {
                Some('n') => out.push('\n'),
                Some('t') => out.push('\t'),
                Some('r') => out.push('\r'),
                Some(o) => out.push(o),
                None => {}
            },
            c => out.push(c),
        }
    }
    out
}

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let result = match args.first().map(String::as_str) {
        Some("verb-diff") => verb_diff(&args),
        Some("reply-diff") => reply_diff(&args),
        Some("float-diff") => float_diff(&args),
        Some("encode") => encode_corpus(&args),
        Some("audit") => print_audit(),
        Some("scorecard") => scorecard(&args),
        Some("discover") => discover(&args),
        Some("roundtrip") => roundtrip(&args),
        Some("live-diff") => live_diff(&args),
        _ => Err("usage: simlinkctl verb-diff|reply-diff|float-diff|encode|audit|scorecard|discover|roundtrip|live-diff ...".into()),
    };
    match result {
        Ok(true) => ExitCode::SUCCESS,
        Ok(false) => ExitCode::from(1),
        Err(e) => {
            eprintln!("simlinkctl: {e}");
            ExitCode::from(2)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn line(tag: &str, ok: bool) -> CaptureLine {
        CaptureLine { tag: tag.into(), ok, diff: if ok { String::new() } else { format!("{tag} differs") }, record: "csharp_bridge.record".into() }
    }

    fn verdict<'a>(all: &'a [CaptureVerdict], verb: &str) -> &'a CaptureVerdict {
        all.iter().find(|v| v.verb == verb).unwrap_or_else(|| panic!("no row for {verb}"))
    }

    #[test]
    fn a_capture_attributes_each_reply_to_its_verb_and_each_event_kind_to_its_own_row() {
        let lines = [
            line("PING", true),
            line("DIAG_STATE", false),
            line("EVENT|InteractionStarted", true),
            line("EVENT|SimAdded", false),
            line("HOST", true),
        ];
        let all = capture_verdicts(&lines);
        assert!(verdict(&all, "Ping").pass);
        assert_eq!(verdict(&all, "Ping").lines, 1);
        assert!(!verdict(&all, "DiagState").pass, "a reply the decoders read differently fails its verb");
        assert!(!verdict(&all, "SetGameSpeed").pass, "a verb with no reply in the capture is not shown");
        assert!(verdict(&all, "Disconnect").pass, "a verb without a reply has nothing to decode");
        assert!(verdict(&all, "ApplyInteractions").pass && verdict(&all, "ApplyInteractions").refused);
        assert!(verdict(&all, "EVENT|InteractionStarted").pass);
        assert!(!verdict(&all, "EVENT|SimAdded").pass, "every EVENT kind the capture holds is decoded");
        assert_eq!(all.len(), VERB_TABLE.len() + 2, "one row per verb, plus one per EVENT kind");
    }

    fn cs_row(wire: &str, command: &str, reply: &str) -> Vec<String> {
        vec![wire.into(), command.into(), reply.into()]
    }

    #[test]
    fn a_command_whose_verb_is_not_a_table_wire_is_judged_as_raw() {
        // Raw's table wire is a placeholder (`<name>`); its lines carry the real name (DIAG_WALK, ...).
        let raw = VERB_TABLE.iter().find(|v| v.record == "Raw").unwrap().wire;
        assert!(!VERB_TABLE.iter().any(|v| v.wire == "DIAG_WALK"));
        let tag = capture_tag(&cs_row("DIAG_WALK", "DIAG_WALK|x|42||3", "DIAG_WALK_REPORT|x|ok|6869"));
        assert_eq!(tag.as_deref(), Some(raw));
        let all = capture_verdicts(&[line(raw, true)]);
        assert!(verdict(&all, "Raw").pass && verdict(&all, "Raw").lines == 1);
    }

    #[test]
    fn capture_tags_follow_the_reply_file_rows() {
        assert_eq!(capture_tag(&cs_row("PING", "PING|x", "OK|x")).as_deref(), Some("PING"));
        assert_eq!(capture_tag(&cs_row("DISCONNECT", "DISCONNECT|x", "")), None, "no reply, nothing to decode");
        assert_eq!(capture_tag(&cs_row("EVENT", "", "EVENT|InteractionStarted|1|2")).as_deref(), Some("EVENT|InteractionStarted"));
        assert_eq!(capture_tag(&cs_row("OK", "", "OK|y")).as_deref(), Some("HOST"));
        assert_eq!(capture_tag(&cs_row("HELLO", "", "HELLO|6|57|10")).as_deref(), Some("HOST"));
    }

    #[test]
    fn encode_writes_the_lines_roundtrip_sends_and_names_the_refusals() {
        let corpus = rows(&read(&crate_dir().join("fixtures/verb_corpus.txt").to_string_lossy()).unwrap());
        let pick = |name: &str| corpus.iter().find(|r| r[0] == name).cloned().unwrap();
        let out = encode_jsonl(&[pick("Ping"), pick("ApplyInteractions")]).unwrap();
        let corr = &pick("Ping")[1];
        assert_eq!(
            out,
            format!(
                "{{\"verb\":\"Ping\",\"line\":\"PING|{corr}\"}}\n{{\"verb\":\"ApplyInteractions\",\"refused\":\"refused_legacy_verb:APPLY_INTERACTIONS\"}}\n"
            )
        );
    }

    /// A capture fixture: one reply row per reply verb except `missing`, the oracle's dumps equal to the Rust ones
    /// except for `differs`, and a corpus listing `unsent` as `#@unsent`. Returns reply-diff's verdict and output.
    fn capture_fixture(name: &str, missing: &[&str], differs: Option<&str>, unsent: &[&str]) -> (bool, String) {
        let dir = std::env::temp_dir().join(format!("card56-{name}-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let (mut replies, mut dumps) = (String::new(), String::new());
        for (i, row) in VERB_TABLE.iter().enumerate() {
            if row.refused || matches!(row.reply, ReplyShape::None) || missing.contains(&row.record) {
                continue;
            }
            let wire = if row.record == "Raw" { "PROBE_SIM_STATUS" } else { row.wire };
            let corr = format!("{i:032x}");
            let reply = format!("OK|{corr}");
            replies.push_str(&format!("{wire}\t{wire}|{corr}\t{reply}\n"));
            let dump = if differs == Some(row.record) { "tampered".to_string() } else { dump_line(&reply) };
            dumps.push_str(&format!("{reply}\t{dump}\n"));
        }
        let file = |n: &str, text: &str| {
            let p = dir.join(n);
            std::fs::write(&p, text).unwrap();
            p.to_string_lossy().into_owned()
        };
        let corpus: String = unsent.iter().map(|v| format!("#@unsent {v} no safe form\n")).collect();
        let args: Vec<String> = [
            "reply-diff", "--corpus", &file("csharp_replies.txt", &replies), "--oracle", &file("dumps.txt", &dumps),
            "--out", &dir.join("reply_diff.jsonl").to_string_lossy(), "--unsent-from", &file("csharp_corpus.txt", &corpus),
        ]
        .iter()
        .map(|s| s.to_string())
        .collect();
        let ok = reply_diff_capture(&args, &file("csharp_bridge.record", "x\n")).unwrap();
        (ok, std::fs::read_to_string(dir.join("reply_diff.jsonl")).unwrap())
    }

    const UNSENT: [&str; 3] = ["AnswerDialog", "SetSimScope", "SetAuthority"];

    #[test]
    fn a_capture_missing_exactly_the_unsent_verbs_passes_reply_diff_with_them_marked_unsent() {
        // Card 56 gate 6 (4c970a49 row 208): an unsent verb's reply never reaches the capture; gate 5 stays pending
        // naming it, it does not fail as "decodes differently".
        let (ok, out) = capture_fixture("unsent", &UNSENT, None, &UNSENT);
        assert!(ok, "{out}");
        for verb in UNSENT {
            let row = out.lines().find(|l| l.contains(&format!("\"verb\":\"{verb}\""))).unwrap();
            assert!(row.contains("\"pass\":false") && row.contains("\"unsent\":true"), "{row}");
        }
        // A verb missing from the capture that the corpus does not list unsent still fails.
        assert!(!capture_fixture("unlisted", &UNSENT, None, &UNSENT[..2]).0);
    }

    #[test]
    fn a_sent_verb_decoding_differently_still_fails_beside_the_unsent_ones() {
        let (ok, out) = capture_fixture("differs", &UNSENT, Some("Ping"), &UNSENT);
        assert!(!ok, "{out}");
        let ping = out.lines().find(|l| l.contains("\"verb\":\"Ping\"")).unwrap();
        assert!(ping.contains("\"pass\":false") && ping.contains("tampered"), "{ping}");
        assert!(out.lines().any(|l| l.contains("\"verb\":\"SetSimScope\"") && l.contains("\"unsent\":true")));
    }

    #[test]
    fn a_capture_without_event_pushes_says_so_in_one_empty_row() {
        let all = capture_verdicts(&[line("PING", true)]);
        assert_eq!(verdict(&all, "EVENT").lines, 0);
        assert!(verdict(&all, "EVENT").pass, "the card asks for every kind the capture holds; it holds none");
        assert_eq!(all.len(), VERB_TABLE.len() + 1);
    }

    /// The §H `verbs_live` counts verbs, not rows: card 56 gate 6 (c9a23656) sends one verb twice when its undo
    /// needs a pair (`SetWorldTime`'s set-plus-one-minute and its restore), and gate 6 counts that as one verb.
    #[test]
    fn verbs_live_counts_a_verb_once_and_not_at_all_when_one_of_its_rows_fails() {
        let row = |verb: &str, pass: bool| simlink_bridge::live::LiveResult {
            verb: verb.into(),
            refused: false,
            pass,
            note: String::new(),
            body_skipped: false,
        };
        let paired = [row("SetWorldTime", true), row("SetWorldTime", true), row("Ping", true)];
        assert_eq!(count_live(&paired), 2, "two rows of one verb count once");
        let one_failed = [row("SetWorldTime", true), row("SetWorldTime", false), row("Ping", true)];
        assert_eq!(count_live(&one_failed), 1, "a verb with a failing row is not live");
    }

    /// The reply a verb's shape answers, with the half's own correlation id.
    fn answer(shape: &ReplyShape, corr: &str) -> String {
        match shape {
            ReplyShape::None => String::new(),
            ReplyShape::Ok | ReplyShape::Any => format!("OK|{corr}"),
            ReplyShape::SessionReady => format!("SESSION_READY|{corr}|414243|57|9"),
            ReplyShape::Snapshot => format!("SNAPSHOT|{corr}|42|57|58|Normal|1|7:41:9:1.5:0:2:-:-:-:5:3:99|0"),
            ReplyShape::Report(name) => {
                format!("{name}|{corr}|ok|{}", simlink_bridge::text::encode_text("result=ok"))
            }
        }
    }

    /// Step (d)'s second process sends the three literal verbs and its own DISCONNECT, so those rows come from
    /// `csharp_bridge.2.record`; every other C# row comes from `csharp_bridge.record` (card 56 gate 6, c9a23656).
    const STEP_D: [&str; 4] = ["AnswerDialog", "SetSimScope", "SetAuthority", "Disconnect"];

    fn in_dir(dir: &Path, name: &str) -> String {
        dir.join(name).to_string_lossy().into_owned()
    }

    /// Writes a synthetic gate 6 run into its own folder: the Rust half's `rust_replies.jsonl` over
    /// `fixtures/live_corpus_sample.txt` (a row for every declared verb — SetWorldTime's six rows among them — and
    /// the §E.3 three as named refusals), the
    /// C# half's `csharp_replies.txt` with the same lines on its own correlation ids, the oracle's dumps of its
    /// reply column, and the per-row record names, minus `drop_csharp` (one verb taken out of the C# capture). It
    /// writes no `#@unsent` line: the ruling sends every verb.
    fn synthetic_gate6(name: &str, drop_csharp: Option<&str>) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("card56-{name}-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let sample = in_dir(&crate_dir(), "fixtures/live_corpus_sample.txt");
        let corpus = rows(&read(&sample).unwrap());
        let (r1, r2) = (dir.join("csharp_bridge.record"), dir.join("csharp_bridge.2.record"));
        let (mut rust, mut cs, mut dumps, mut recs) = (String::new(), String::new(), String::new(), String::new());
        for (at, row) in corpus.iter().enumerate() {
            let cmd = parse_command(row).unwrap_or_else(|e| panic!("{row:?}: {e}"));
            let verb = cmd.record();
            let line = match encode(&cmd) {
                Ok(line) => line,
                Err(refusal) => {
                    rust.push_str(&format!(
                        "{{\"verb\":{},\"refused\":{}}}\n",
                        json_str(verb),
                        json_str(&refusal.name())
                    ));
                    continue;
                }
            };
            let ours = line.split('|').nth(1).unwrap_or("").to_string();
            let reply = answer(&cmd.row().reply, &ours);
            rust.push_str(&roundtrip_row(verb, &line, &reply, !matches!(cmd.row().reply, ReplyShape::None)).0);
            rust.push('\n');
            if drop_csharp == Some(verb) {
                continue;
            }
            // The C# half's own id, on the same line and the same reply shape (gate 6 compares shapes, not values).
            let theirs = format!("{at:032x}");
            let command: Vec<String> = line.split('|').enumerate().map(|(i, f)| if i == 1 { theirs.clone() } else { f.to_string() }).collect();
            let command = command.join("|");
            let reply_cs = answer(&cmd.row().reply, &theirs);
            let esc = simlink_bridge::corpus::escape;
            cs.push_str(&format!("{}\t{}\t{}\n", esc(line.split('|').next().unwrap_or("")), esc(&command), esc(&reply_cs)));
            dumps.push_str(&format!("{verb}\t{}\n", dump_line(&reply_cs)));
            recs.push_str(&format!("{}\n", (if STEP_D.contains(&verb) { &r2 } else { &r1 }).to_string_lossy()));
        }
        std::fs::write(dir.join("rust_replies.jsonl"), rust).unwrap();
        std::fs::write(dir.join("csharp_replies.txt"), cs).unwrap();
        std::fs::write(dir.join("csharp_reply_dumps.txt"), dumps).unwrap();
        std::fs::write(dir.join("csharp_replies.records.txt"), recs).unwrap();
        std::fs::write(&r1, "ctl->host seq=1 slot=8 CONNECT|00000000000000000000000000000000|23\n").unwrap();
        std::fs::write(&r2, "ctl->host seq=1 slot=8 CONNECT|00000000000000000000000000000000|23\n").unwrap();
        std::fs::write(dir.join("rust_corpus.txt"), read(&sample).unwrap()).unwrap();
        std::fs::write(dir.join("csharp_corpus.txt"), read(&sample).unwrap()).unwrap();
        dir
    }

    /// gate 5 over the synthetic run, told which record each row came from.
    fn gate5_over(dir: &Path) -> (bool, String) {
        let args: Vec<String> = [
            "reply-diff", "--record", &in_dir(dir, "csharp_bridge.record"), "--corpus",
            &in_dir(dir, "csharp_replies.txt"), "--oracle", &in_dir(dir, "csharp_reply_dumps.txt"), "--out",
            &in_dir(dir, "reply_diff.jsonl"), "--row-records", &in_dir(dir, "csharp_replies.records.txt"),
        ]
        .iter()
        .map(|s| s.to_string())
        .collect();
        let ok = reply_diff_capture(&args, &in_dir(dir, "csharp_bridge.record")).unwrap();
        (ok, read(&in_dir(dir, "reply_diff.jsonl")).unwrap())
    }

    /// gate 6's comparison over the synthetic run, with both halves' corpora for the `#@blank` marks.
    fn gate6_over(dir: &Path) -> (bool, String) {
        let args: Vec<String> = [
            "live-diff", "--rust", &in_dir(dir, "rust_replies.jsonl"), "--csharp", &in_dir(dir, "csharp_replies.txt"),
            "--corpus", &in_dir(dir, "csharp_corpus.txt"), "--corpus", &in_dir(dir, "rust_corpus.txt"), "--out",
            &in_dir(dir, "live_roundtrip.jsonl"),
        ]
        .iter()
        .map(|s| s.to_string())
        .collect();
        let ok = live_diff(&args).unwrap();
        (ok, read(&in_dir(dir, "live_roundtrip.jsonl")).unwrap())
    }

    #[test]
    fn the_offline_path_over_a_synthetic_gate_6_run_shows_all_sixty_verbs_live() {
        // Card 56 §G gates 5 and 6 and §H, after the c9a23656 count ruling: with no `#@unsent` verb left, the two
        // records and the Rust replies cover every declared verb, so `verbs_live` reaches `verbs_declared`.
        let dir = synthetic_gate6("count", None);
        let (live_ok, live) = gate6_over(&dir);
        assert!(live_ok, "{live}");
        assert_eq!(live.lines().last().unwrap(), "{\"verbs_live\":60}", "{live}");
        assert!(!live.contains("\"pass\":false") && !live.contains("unsent"), "{live}");
        // Sixty verbs in sixty-five rows: gate 6 counts the verb, not its rows (card 56 c9a23656, Main's Q1).
        assert_eq!(live.lines().filter(|l| l.contains("\"verb\":")).count(), 65, "{live}");
        assert_eq!(live.lines().filter(|l| l.contains("\"verb\":\"SetWorldTime\"")).count(), 2,
                   "both of its set rows paired and passed");
        let (g5_ok, diff) = gate5_over(&dir);
        assert!(g5_ok, "{diff}");
        assert!(!diff.contains("\"pass\":false") && !diff.contains("\"unsent\":true"), "{diff}");
        assert_eq!(diff.lines().filter(|l| l.contains("\"verb\":")).count(), VERB_TABLE.len() + 1, "one row per verb, plus EVENT");
        // The §H scorecard, over the same folder with a real gate-4 diff beside it.
        let vd: Vec<String> = [
            "verb-diff", "--corpus", &in_dir(&crate_dir(), "fixtures/verb_corpus.txt"), "--oracle",
            &in_dir(&crate_dir(), "fixtures/oracle_verb_lines.txt"), "--out", &in_dir(&dir, "verb_diff.jsonl"),
        ]
        .iter()
        .map(|s| s.to_string())
        .collect();
        assert!(verb_diff(&vd).unwrap());
        let sc: Vec<String> = ["scorecard", "--run-dir", &dir.to_string_lossy()].iter().map(|s| s.to_string()).collect();
        assert!(scorecard(&sc).unwrap());
        let line = read(&in_dir(&dir, "scorecard.txt")).unwrap();
        assert!(line.contains("verbs_declared=60") && line.contains("verbs_live=60"), "{line}");
    }

    #[test]
    fn dropping_one_verb_from_the_csharp_capture_leaves_both_live_gates_short_naming_it() {
        // Card 56 §G gate 6: a verb whose reply never reaches the capture makes gate 5 short of it too, and both
        // gates stay PENDING_LIVE naming it (the harness reads that from these rows).
        let dir = synthetic_gate6("drop", Some("Ping"));
        let (live_ok, live) = gate6_over(&dir);
        assert!(!live_ok);
        assert_eq!(live.lines().last().unwrap(), "{\"verbs_live\":59}", "{live}");
        let ping = live.lines().find(|l| l.contains("\"verb\":\"Ping\"")).unwrap();
        assert!(ping.contains("\"pass\":false") && ping.contains("no C# row"), "{ping}");
        let (g5_ok, diff) = gate5_over(&dir);
        assert!(!g5_ok, "{diff}");
        let row = diff.lines().find(|l| l.contains("\"verb\":\"Ping\"")).unwrap();
        assert!(row.contains("\"pass\":false") && !row.contains("\"unsent\":true"), "{row}");
    }

    #[test]
    fn the_rows_extracted_from_the_second_record_name_that_record() {
        // Card 56 §G gate 5: `reply_diff.jsonl` has one row per verb, "each row with a `record` field naming the
        // record file it came from"; step (d)'s rows name `csharp_bridge.2.record`.
        let dir = synthetic_gate6("records", None);
        let (_, diff) = gate5_over(&dir);
        let second = in_dir(&dir, "csharp_bridge.2.record").replace('\\', "/");
        for verb in ["AnswerDialog", "SetSimScope", "SetAuthority"] {
            let row = diff.lines().find(|l| l.contains(&format!("\"verb\":\"{verb}\""))).unwrap();
            assert!(row.contains(&format!("\"record\":\"{second}\"")), "{row}");
        }
        let first = in_dir(&dir, "csharp_bridge.record").replace('\\', "/");
        for verb in ["Ping", "DiagState", "SaveGame"] {
            let row = diff.lines().find(|l| l.contains(&format!("\"verb\":\"{verb}\""))).unwrap();
            assert!(row.contains(&format!("\"record\":\"{first}\"")), "{row}");
        }
    }

    #[test]
    fn reply_diff_refuses_a_row_records_file_that_does_not_line_up_with_the_capture() {
        // A stale provenance file would attribute rows to the wrong record.
        let dir = synthetic_gate6("stale", None);
        std::fs::write(dir.join("csharp_replies.records.txt"), "x.record\n").unwrap();
        let args: Vec<String> = [
            "reply-diff", "--record", &in_dir(&dir, "csharp_bridge.record"), "--corpus",
            &in_dir(&dir, "csharp_replies.txt"), "--oracle", &in_dir(&dir, "csharp_reply_dumps.txt"), "--out",
            &in_dir(&dir, "reply_diff.jsonl"), "--row-records", &in_dir(&dir, "csharp_replies.records.txt"),
        ]
        .iter()
        .map(|s| s.to_string())
        .collect();
        let err = reply_diff_capture(&args, &in_dir(&dir, "csharp_bridge.record")).unwrap_err();
        assert!(err.contains("records"), "{err}");
    }

    #[test]
    fn live_diff_marks_a_row_the_mod_answered_without_running_the_body() {
        // Card 56 §G gate 6 (c9a23656): such a row is marked `body=skipped` in `live_roundtrip.jsonl`.
        const R: &str = "0123456789abcdef0123456789abcdef";
        const C: &str = "fedcba9876543210fedcba9876543210";
        let dir = std::env::temp_dir().join(format!("card56-marked-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let refusal = format!("ERROR|{}|{}", R, simlink_bridge::text::encode_text("Sim not found: 0"));
        let theirs = format!("ERROR|{}|{}", C, simlink_bridge::text::encode_text("Sim not found: 0"));
        let rust_rows = format!(
            "{}\n{}\n",
            roundtrip_row("SetActiveSim", &format!("SET_ACTIVE_SIM|{R}|0"), &refusal, true).0,
            roundtrip_row("Ping", &format!("PING|{R}"), &format!("OK|{R}"), true).0
        );
        let csharp = format!(
            "SET_ACTIVE_SIM\tSET_ACTIVE_SIM|{C}|0\t{}\nPING\tPING|{C}\tOK|{C}\n",
            simlink_bridge::corpus::escape(&theirs)
        );
        let (rust_p, cs_p, out_p) = (in_dir(&dir, "rust_replies.jsonl"), in_dir(&dir, "csharp_replies.txt"), in_dir(&dir, "live_roundtrip.jsonl"));
        std::fs::write(&rust_p, rust_rows).unwrap();
        std::fs::write(&cs_p, csharp).unwrap();
        let args: Vec<String> = ["live-diff", "--rust", &rust_p, "--csharp", &cs_p, "--out", &out_p]
            .iter()
            .map(|s| s.to_string())
            .collect();
        assert!(live_diff(&args).unwrap());
        let out = read(&out_p).unwrap();
        let refused_row = out.lines().find(|l| l.contains("\"verb\":\"SetActiveSim\"")).unwrap();
        assert!(refused_row.contains("\"body\":\"skipped\""), "{refused_row}");
        let ran = out.lines().find(|l| l.contains("\"verb\":\"Ping\"")).unwrap();
        assert!(!ran.contains("body"), "{ran}");
        assert_eq!(out.lines().last().unwrap(), "{\"verbs_live\":2}");
    }
}
