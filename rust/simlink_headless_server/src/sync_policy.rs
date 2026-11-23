#![forbid(unsafe_code)]
//! Opt-in developer policy: immutable worker-compiled packet and typed payload rules.
//! Context tick/client count refer to the published decision-table snapshot.
use crate::messages::{self as m, Event};
use crate::sync_rules::{Mapper, Rules};
use rhai::{Engine, Scope, AST, INT};
use sha2::{Digest, Sha256};
use std::collections::{BTreeMap, BTreeSet};
use std::io::{self, Read};
use std::path::{Path, PathBuf};
use std::sync::{
    atomic::{AtomicBool, Ordering},
    mpsc::{self, Receiver, SyncSender},
    Arc,
};
use std::time::Duration;

pub const MAX_SOURCE: usize = 65_536;
const MAX_CLIENTS: usize = 64;
const MAX_KINDS: usize = 64;
const MAX_LINE: usize = 16_384;
const KINDS: &[&str] = &[
    "A", "AS", "S", "P", "Ps", "Pn", "Ph", "E", "R", "X", "AW", "Pz", "Pf", "Au", "O", "Z",
];

#[derive(Clone)]
struct Record {
    player_id: String,
    kind: String,
    family: String,
    joined_clients: INT,
    tick: INT,
}
#[derive(Clone, Default)]
struct Context {
    players: Vec<String>,
    kinds: Vec<String>,
    tick: INT,
}
#[derive(Clone)]
struct Candidate {
    ast: AST,
    sha: String,
    rules: Rules,
}
type Table = BTreeMap<(String, String), bool>;

fn engine() -> Engine {
    let mut e = Engine::new_raw();
    e.set_max_operations(5_000);
    e.set_max_call_levels(16);
    e.set_max_expr_depths(32, 32);
    e.set_max_functions(32);
    e.set_max_variables(64);
    e.set_max_string_size(256);
    e.set_max_array_size(16);
    e.set_max_map_size(8);
    e.set_optimization_level(rhai::OptimizationLevel::None);
    e.register_type_with_name::<Record>("SyncRecord");
    e.register_get("player_id", |r: &mut Record| r.player_id.clone());
    e.register_get("kind", |r: &mut Record| r.kind.clone());
    e.register_get("family", |r: &mut Record| r.family.clone());
    e.register_get("joined_clients", |r: &mut Record| r.joined_clients);
    e.register_get("tick", |r: &mut Record| r.tick);
    e
}
pub fn family(kind: &str) -> &'static str {
    match kind {
        "Pz" | "Pf" => "motion",
        "A" | "AS" | "S" | "P" | "Ps" | "Pn" | "Ph" | "E" | "R" | "X" | "AW" => "animation",
        "Au" => "audio",
        _ => "world",
    }
}
fn source(path: &Path) -> Result<String, String> {
    let mut bytes = Vec::new();
    std::fs::File::open(path)
        .map_err(|e| e.to_string())?
        .take((MAX_SOURCE + 1) as u64)
        .read_to_end(&mut bytes)
        .map_err(|e| e.to_string())?;
    if bytes.len() > MAX_SOURCE {
        return Err("sync policy source exceeds 65536 bytes".into());
    }
    String::from_utf8(bytes).map_err(|e| e.to_string())
}
fn compile(text: &str) -> Result<Candidate, String> {
    let e = engine();
    let ast = e.compile(text).map_err(|error| error.to_string())?;
    // Rhai checks aggregate nested output sizes: sixteen bounded maps need more
    // than the grouping callback's single-record allocation envelope.
    let mut rule_engine = engine();
    rule_engine.set_max_map_size(16 * 8);
    rule_engine.set_max_string_size(8192);
    let rules = Rules::compile(&rule_engine, &ast)?;
    Ok(Candidate {
        ast,
        sha: format!("{:x}", Sha256::digest(text.as_bytes())),
        rules,
    })
}
fn evaluate(e: &Engine, candidate: &Candidate, context: &Context) -> Result<Table, String> {
    let mut table = Table::new();
    for player in context.players.iter().take(MAX_CLIENTS) {
        for kind in context.kinds.iter().take(MAX_KINDS) {
            let record = Record {
                player_id: player.clone(),
                kind: kind.clone(),
                family: family(kind).into(),
                joined_clients: context.players.len() as INT,
                tick: context.tick,
            };
            let split = e
                .call_fn::<bool>(&mut Scope::new(), &candidate.ast, "split_before", (record,))
                .map_err(|error| error.to_string())?;
            table.insert((player.clone(), kind.clone()), split);
        }
    }
    Ok(table)
}
fn validate(candidate: &Candidate) -> Result<(), String> {
    evaluate(
        &engine(),
        candidate,
        &Context {
            players: vec!["1".into(), "2".into(), u64::MAX.to_string()],
            kinds: KINDS.iter().map(|k| (*k).into()).collect(),
            tick: 0,
        },
    )
    .map(|_| ())
}
pub fn check(path: &Path) -> Result<String, String> {
    let candidate = compile(&source(path)?)?;
    validate(&candidate)?;
    Ok(candidate.sha)
}

#[derive(Clone, Default)]
struct Update {
    generation: u64,
    revision: u64,
    sha: String,
    rejected_sha: String,
    error: String,
    activations: u64,
    rejections: u64,
    rollbacks: u64,
    tick: INT,
    table: Table,
    rules: Rules,
}
#[derive(Default)]
struct Counts {
    events: u64,
    lines: u64,
    packets: u64,
    splits: u64,
}

/// The actor holds decisions, never the engine, AST, file handle or script state.
pub struct Host {
    tx: Option<SyncSender<Context>>,
    rx: Option<Receiver<Update>>,
    stop: Arc<AtomicBool>,
    active: Update,
    pending: Option<Update>,
    deferred: u64,
    mapper: Mapper,
    kinds: BTreeSet<String>,
    total: Counts,
    players: BTreeMap<String, Counts>,
    effects: BTreeMap<(String, String), (u64, u64)>,
    invalid: u64,
    protected: u64,
    tick: INT,
}
impl Host {
    pub fn start(path: Option<PathBuf>) -> io::Result<Self> {
        let stop = Arc::new(AtomicBool::new(false));
        let mut host = Self {
            tx: None,
            rx: None,
            stop: Arc::clone(&stop),
            active: Update::default(),
            pending: None,
            deferred: 0,
            mapper: Mapper::default(),
            kinds: KINDS.iter().map(|k| (*k).into()).collect(),
            total: Counts::default(),
            players: BTreeMap::new(),
            effects: BTreeMap::new(),
            invalid: 0,
            protected: 0,
            tick: 0,
        };
        if let Some(path) = path {
            let (input, inputs) = mpsc::sync_channel(1);
            let (output, outputs) = mpsc::sync_channel(1);
            let (ready, readiness) = mpsc::sync_channel(1);
            std::thread::spawn(move || worker(path, inputs, output, stop, ready));
            host.active = readiness
                .recv_timeout(Duration::from_secs(5))
                .map_err(io::Error::other)?
                .map_err(io::Error::other)?;
            host.tx = Some(input);
            host.rx = Some(outputs);
        }
        Ok(host)
    }
    pub fn boundary(&mut self, tick: u64, players: Vec<String>) {
        self.boundary_pinned(tick, players, false);
    }
    pub fn retain_connections(&mut self, connections: &BTreeSet<u64>) {
        self.mapper.retain(connections);
    }
    pub fn reset_world_metadata(&mut self) {
        self.mapper.reset_world();
    }
    pub fn reset_driver_metadata(&mut self) {
        self.mapper.reset();
    }
    /// Baseline/history transactions keep the same table through their final output pump.
    pub fn boundary_pinned(&mut self, tick: u64, players: Vec<String>, pinned: bool) {
        self.tick = tick.min(INT::MAX as u64) as INT;
        if let Some(update) = self.rx.as_ref().and_then(|rx| rx.try_recv().ok()) {
            if update.generation > self.active.generation {
                self.pending = Some(update);
                if pinned {
                    self.deferred += 1;
                }
            }
        }
        if !pinned {
            if let Some(update) = self.pending.take() {
                self.active = update;
            }
        }
        if let Some(tx) = &self.tx {
            let _ = tx.try_send(Context {
                players: players.into_iter().take(MAX_CLIENTS).collect(),
                kinds: self.kinds.iter().take(MAX_KINDS).cloned().collect(),
                tick: self.tick,
            });
        }
    }
    /// Preserve source identity/order; only validated payload fields and packet boundaries may change.
    pub fn packets(&mut self, player: impl ToString, event: &Event, protected: bool) -> Vec<Event> {
        self.packets_for(0, player, event, protected)
    }
    pub fn packets_for(
        &mut self,
        connection: u64,
        player: impl ToString,
        event: &Event,
        protected: bool,
    ) -> Vec<Event> {
        if self.tx.is_none() || event.kind != "SimAnimation" {
            return vec![event.clone()];
        }
        let parsed = (|| -> Result<_, ()> {
            if event.fields.len() != 1 || event.fields[0].len() > m::PUBLIC_LINE_MAX_BYTES {
                return Err(());
            }
            let lines = m::dec_list(&event.fields[0]).map_err(|_| ())?;
            if lines.is_empty() || lines.len() > 200 {
                return Err(());
            }
            let mut decoded = Vec::new();
            let mut kinds = Vec::new();
            let mut synthetic = false;
            for hex in &lines {
                if hex.len() > MAX_LINE * 2 {
                    return Err(());
                }
                let line = m::hex_decode(hex).map_err(|_| ())?;
                if line.chars().any(char::is_control) {
                    return Err(());
                }
                let mut fields = line.split('|');
                let sequence = fields.next().ok_or(())?.parse::<u64>().map_err(|_| ())?;
                let kind = fields.next().ok_or(())?;
                if kind.is_empty()
                    || kind.len() > 8
                    || !kind.bytes().all(|b| b.is_ascii_alphanumeric())
                    || fields.next().is_none()
                {
                    return Err(());
                }
                synthetic |= sequence == 0;
                kinds.push(kind.to_string());
                decoded.push(line);
            }
            Ok((lines, decoded, kinds, synthetic))
        })();
        let Ok((mut lines, decoded, kinds, synthetic)) = parsed else {
            self.invalid += 1;
            self.mapper.invalidate(connection);
            return vec![event.clone()];
        };
        let player = player.to_string();
        // Reserve a maximum-width EV envelope. Rules and pinned aliases are at most128 ASCII bytes.
        // Preflight the worst possible expansion without cloning driver/sound caches.
        let growth: usize = decoded
            .iter()
            .map(|line| {
                let f: Vec<_> = line.split('|').collect();
                match f.get(1).copied() {
                    Some("Ps") => f.get(4).map_or(0, |v| 128usize.saturating_sub(v.len()) * 2),
                    Some("Au") if f.get(3).is_some_and(|op| matches!(*op, "o" | "w")) => {
                        f.get(5).map_or(0, |v| 256usize.saturating_sub(v.len()) * 2)
                    }
                    _ => 0,
                }
            })
            .sum();
        let source_size = event.fields[0]
            .len()
            .max(lines.iter().map(String::len).sum::<usize>() + lines.len() + 2);
        if source_size.saturating_add(growth).saturating_add(128) > m::PUBLIC_LINE_MAX_BYTES {
            self.mapper.output_fallback();
            for line in &decoded {
                self.mapper
                    .observe_original(connection, &player, line, protected);
            }
            return vec![event.clone()];
        }
        let checkpoint = self.mapper.checkpoint();
        let mut changed = false;
        for (hex, line) in lines.iter_mut().zip(&decoded) {
            if let Some(replacement) =
                self.mapper
                    .apply(connection, &player, line, protected, &self.active.rules)
            {
                *hex = m::hex_encode(&replacement);
                changed = true;
            }
        }
        if changed && m::enc_list(&lines).len().saturating_add(128) > m::PUBLIC_LINE_MAX_BYTES {
            self.mapper.rollback_stats(checkpoint);
            self.mapper.output_fallback();
            for line in &decoded {
                self.mapper
                    .observe_original(connection, &player, line, protected);
            }
            return vec![event.clone()];
        }
        let packet = |fields: String| Event {
            seq: event.seq,
            tick: event.tick,
            corr: event.corr,
            kind: event.kind.clone(),
            fields: vec![fields],
        };
        if protected || synthetic {
            self.protected += 1;
            return vec![if changed {
                packet(m::enc_list(&lines))
            } else {
                event.clone()
            }];
        }
        let mut output = Vec::new();
        let mut start = 0;
        let mut splits = 0;
        for (index, kind) in kinds.iter().enumerate() {
            if self.kinds.len() < MAX_KINDS {
                self.kinds.insert(kind.clone());
            }
            let split = index > start
                && self.active.table.get(&(player.clone(), kind.clone())) == Some(&true);
            if split {
                output.push(packet(m::enc_list(&lines[start..index])));
                start = index;
                splits += 1;
            }
            if self
                .effects
                .contains_key(&(player.clone(), family(kind).into()))
                || self.effects.len() < MAX_CLIENTS * 4
            {
                let count = self
                    .effects
                    .entry((player.clone(), family(kind).into()))
                    .or_default();
                count.0 += 1;
                count.1 += u64::from(split);
            }
        }
        output.push(packet(m::enc_list(&lines[start..])));
        if splits == 0 && !changed {
            output[0] = event.clone();
        }
        self.total.events += 1;
        self.total.lines += lines.len() as u64;
        self.total.packets += output.len() as u64;
        self.total.splits += splits;
        if self.players.contains_key(&player) || self.players.len() < MAX_CLIENTS {
            let count = self.players.entry(player).or_default();
            count.events += 1;
            count.lines += lines.len() as u64;
            count.packets += output.len() as u64;
            count.splits += splits;
        }
        output
    }
    pub fn report(&self) -> String {
        let mut rows = vec![format!("policy|enabled={}|active_sha256={}|revision={}|tick={}|context_tick={}|activations={}|rejections={}|rollbacks={}|rejected_sha256={}|last_error_hex={}",
            u8::from(self.tx.is_some()), self.active.sha, self.active.revision, self.tick, self.active.tick,
            self.active.activations,self.active.rejections,self.active.rollbacks,self.active.rejected_sha,m::hex_encode(&self.active.error)),
            format!("totals|applied_events={}|input_lines={}|output_packets={}|splits={}|invalid_events={}|protected_events={}",
                self.total.events,self.total.lines,self.total.packets,self.total.splits,self.invalid,self.protected)];
        for (player, count) in &self.players {
            rows.push(format!(
                "player|{player}|applied_events={}|input_lines={}|output_packets={}|splits={}",
                count.events, count.lines, count.packets, count.splits
            ));
        }
        for ((player, family), (lines, splits)) in &self.effects {
            rows.push(format!(
                "effect|player={player}|family={family}|input_lines={lines}|splits={splits}"
            ));
        }
        if let Some(row) = rows.first_mut() {
            row.push_str(&format!(
                "|rules={}|pending_revision={}|pending_sha256={}|deferred={}",
                self.active.rules.len(),
                self.pending.as_ref().map_or(0, |p| p.revision),
                self.pending.as_ref().map_or("", |p| p.sha.as_str()),
                self.deferred
            ));
        }
        rows.extend(self.mapper.report());
        rows.join("\n")
    }
}
impl Drop for Host {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
    }
}

fn worker(
    path: PathBuf,
    inputs: Receiver<Context>,
    output: SyncSender<Update>,
    stop: Arc<AtomicBool>,
    ready: SyncSender<Result<Update, String>>,
) {
    let initial = source(&path)
        .and_then(|text| compile(&text))
        .and_then(|candidate| {
            validate(&candidate)?;
            Ok(candidate)
        });
    let mut active = match initial {
        Ok(candidate) => candidate,
        Err(error) => {
            let _ = ready.send(Err(error));
            return;
        }
    };
    let mut seen = active.sha.clone();
    let mut previous = None;
    let mut state = Update {
        generation: 1,
        revision: 1,
        sha: active.sha.clone(),
        activations: 1,
        rules: active.rules.clone(),
        ..Update::default()
    };
    if ready.send(Ok(state.clone())).is_err() {
        return;
    }
    let e = engine();
    let mut context = Context::default();
    let mut pending = None;
    while !stop.load(Ordering::Acquire) {
        if let Some(update) = pending.take() {
            match output.try_send(update) {
                Ok(()) => {}
                Err(mpsc::TrySendError::Full(update)) => {
                    pending = Some(update);
                    std::thread::sleep(Duration::from_millis(5));
                    continue;
                }
                Err(mpsc::TrySendError::Disconnected(_)) => return,
            }
        }
        let mut changed = false;
        if let Ok(new_context) = inputs.recv_timeout(Duration::from_millis(25)) {
            context = new_context;
            changed = true;
        }
        match source(&path) {
            Ok(text) => {
                let sha = format!("{:x}", Sha256::digest(text.as_bytes()));
                if sha != seen {
                    seen = sha.clone();
                    changed = true;
                    match compile(&text).and_then(|candidate| {
                        validate(&candidate)?;
                        evaluate(&e, &candidate, &context)?;
                        Ok(candidate)
                    }) {
                        Ok(candidate) => {
                            previous = Some(std::mem::replace(&mut active, candidate));
                            state.sha = active.sha.clone();
                            state.activations += 1;
                            state.revision += 1;
                            state.error.clear();
                        }
                        Err(error) => {
                            state.rejections += 1;
                            state.rejected_sha = sha;
                            state.error = error;
                        }
                    }
                }
            }
            Err(error) => {
                let sha = format!("{:x}", Sha256::digest(error.as_bytes()));
                if state.rejected_sha != sha {
                    state.rejections += 1;
                    state.rejected_sha = sha;
                    state.error = error;
                    changed = true;
                }
            }
        }
        if !changed {
            continue;
        }
        match evaluate(&e, &active, &context) {
            Ok(table) => state.table = table,
            Err(error) => {
                state.rejected_sha = active.sha.clone();
                state.error = error;
                state.rollbacks += 1;
                state.revision += 1;
                active = previous.take().unwrap_or_else(|| {
                    compile("fn split_before(record) { false }").expect("builtin policy")
                });
                state.table = match evaluate(&e, &active, &context) {
                    Ok(table) => table,
                    Err(previous_error) => {
                        state
                            .error
                            .push_str(&format!("; previous policy failed: {previous_error}"));
                        active =
                            compile("fn split_before(record) { false }").expect("builtin policy");
                        evaluate(&e, &active, &context).expect("bounded builtin returns bool")
                    }
                };
                state.sha = active.sha.clone();
            }
        }
        state.rules = active.rules.clone();
        state.tick = context.tick;
        state.generation += 1;
        state.error = state.error.chars().take(512).collect();
        pending = Some(state.clone());
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn maximum_partition_preserves_all_bytes_without_replicating_whole_batch() {
        let mut host = Host::start(None).unwrap();
        let (tx, _rx) = mpsc::sync_channel(1);
        host.tx = Some(tx);
        host.active
            .table
            .insert((u64::MAX.to_string(), "Pz".into()), true);
        let lines: Vec<_> = (1..=200)
            .map(|seq| m::hex_encode(&format!("{seq}|Pz|{}|{}", u64::MAX, "a".repeat(500))))
            .collect();
        let event = Event {
            seq: 9,
            tick: 11,
            corr: 17,
            kind: "SimAnimation".into(),
            fields: vec![m::enc_list(&lines)],
        };
        let packets = host.packets(u64::MAX, &event, false);
        assert_eq!(packets.len(), 200);
        let flattened: Vec<_> = packets
            .iter()
            .flat_map(|p| m::dec_list(&p.fields[0]).unwrap())
            .collect();
        assert_eq!(flattened, lines);
        assert_eq!(
            packets.iter().map(|p| p.fields[0].len()).sum::<usize>(),
            event.fields[0].len() + 199
        );
        assert!(packets
            .iter()
            .all(|p| (p.seq, p.tick, p.corr) == (9, 11, 17)));
    }
}
