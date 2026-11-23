#![forbid(unsafe_code)]
//! Cadence and presentation pump shared by the resident actor and offline replay.

use std::collections::{HashMap, HashSet, VecDeque};
use crate::cadence::{Action, Thresholds};
use crate::messages::{enc_list, hex_encode, Event};
use crate::presentation::{self, Ladder, LadderStep};
use crate::routing::RoutedEvent;
use crate::{ConnId, DriverError, GameDriver, Server};
use crate::transport::{StreamReply, StreamRequest};

// One page per retained recorder event (SacsRecorder.RingCapacity = 4096),
// plus an end probe, counted across deferred replies and pumps. Once exhausted,
// more=1 fails closed; a later end probe can finish the still-pending epoch.
const MAX_EPOCH_DUMP_PAGES: usize = 4096 + 1;

#[derive(Clone, Debug)]
pub struct StreamOptions {
    pub base_interval_ms: f64,
    pub thresholds: Thresholds,
    pub presentation_poll_ms: u64,
    pub presentation_min_interval_ms: u64,
    pub ring_requested: bool,
}
impl Default for StreamOptions {
    fn default() -> Self {
        Self { base_interval_ms: 2000.0, thresholds: Thresholds::default(),
            presentation_poll_ms: 250, presentation_min_interval_ms: 0, ring_requested: false }
    }
}
impl StreamOptions {
    pub fn from_args(args: &[String]) -> Result<Self, String> {
        let mut options = Self { thresholds: Thresholds::from_args(args)?, ..Self::default() };
        if let Some(i) = args.iter().position(|s| s == "--sync-broadcast-seconds") {
            let value: i32 = args.get(i + 1).ok_or("--sync-broadcast-seconds needs a value")?
                .parse().map_err(|_| "invalid --sync-broadcast-seconds")?;
            options.base_interval_ms = f64::from(value.max(0)) * 1000.0;
        }
        options.ring_requested = std::env::var("SIMSONLINE_SIMLINK_SERVER_RING").as_deref() == Ok("1");
        options.presentation_min_interval_ms = std::env::var("SIMSONLINE_PRESENTATION_MIN_INTERVAL_MS")
            .ok().and_then(|v| v.parse().ok()).unwrap_or(0);
        options.presentation_poll_ms = std::env::var("SIMSONLINE_PRESENTATION_POLL_MS").ok()
            .and_then(|v| v.parse::<u64>().ok()).filter(|n| (50..=1000).contains(n)).unwrap_or(250);
        Ok(options)
    }
}

/// Elapsed pump windows when a join relays presentation. Sample every pump,
/// including empty ones: recorder silence is not a stalled presentation pump.
#[derive(Default)]
pub struct JoinGap {
    last_pump_ms: Option<u64>,
    pub max_during_join: u64,
}
impl JoinGap {
    pub fn observe(&mut self, now: u64, joining: bool, interval_ms: u64) -> u64 {
        let gap = if joining {
            let gap = self.last_pump_ms.map_or(1, |previous|
                (now.saturating_sub(previous) / interval_ms.max(1)).max(1));
            self.max_during_join = self.max_during_join.max(gap);
            gap
        } else { 0 };
        self.last_pump_ms = Some(now);
        gap
    }
}

enum Initialization { Ring, Cursor, Ready }
enum CadenceStage { Diag, Full, Delta, Publish }
struct CadenceBatch {
    decisions: Vec<(ConnId, crate::cadence::Decision)>,
    stage: CadenceStage,
    full: Option<crate::snapshot::Snapshot>,
    delta: Option<crate::snapshot::Snapshot>,
}
enum GatherAudience {
    Fresh { counted: Vec<ConnId>, joiners: Vec<ConnId> },
    Join(Vec<ConnId>),
}
struct JoinGather {
    history: presentation::HistoryGather,
    audience: GatherAudience,
    held: Vec<String>,
    started_ms: Option<u64>,
    locked: bool,
    abandoned: bool,
}
struct RecorderConfig {
    args: Vec<String>,
    push_target: Option<String>,
    at_ms: u64,
}
struct EpochBarrier {
    epoch: u64,
    pages: usize,
    drained: bool,
}
enum StreamIo {
    Ring,
    Cursor { initial: bool },
    Config(RecorderConfig),
    Diag,
    Full,
    Delta(i64),
    Gather,
    Dump,
    EpochDump,
}

pub struct Stream {
    pub options: StreamOptions,
    pub last_tick: i64,
    pub history_trimmed: HashMap<ConnId, u64>,
    pub epoch_dump_limit_hits: u64,
    pub ladder: Ladder,
    pub report: Vec<String>,
    next_snapshot_ms: u64,
    next_presentation_ms: u64,
    last_presentation_ms: u64,
    cursor: i64,
    targets: HashSet<u64>,
    sim_lots: HashMap<u64, Option<u64>>,
    joiners: HashSet<ConnId>,
    pushed: Vec<String>,
    last_world_time: Option<crate::world_time::WorldTime>,
    last_world_time_ms: u64,
    push_targets_sent: Option<String>,
    command_failures: Vec<RoutedEvent>,
    initialization: Initialization,
    pending_io: Option<StreamIo>,
    cadence_batch: Option<CadenceBatch>,
    configs: VecDeque<RecorderConfig>,
    gathers: VecDeque<JoinGather>,
    presentation_due: bool,
    skip_cursor: bool,
    want_dump: bool,
    dump_pages: usize,
    dump_targets: String,
    broadcast_due: bool,
    epoch_barrier: Option<EpochBarrier>,
}
impl Stream {
    pub fn new(options: StreamOptions) -> Self {
        Self { ladder: Ladder::new(options.ring_requested), options, last_tick: 0, history_trimmed: HashMap::new(),
            epoch_dump_limit_hits: 0, report: Vec::new(), next_snapshot_ms: 0, next_presentation_ms: 0, last_presentation_ms: 0,
            cursor: -1, targets: HashSet::new(), sim_lots: HashMap::new(), joiners: HashSet::new(), pushed: Vec::new(),
            last_world_time: None, last_world_time_ms: 0, push_targets_sent: None, command_failures: Vec::new(),
            initialization: Initialization::Ring, pending_io: None, cadence_batch: None, configs: VecDeque::new(),
            gathers: VecDeque::new(), presentation_due: false, skip_cursor: false, want_dump: false,
            dump_pages: 0, dump_targets: String::new(), broadcast_due: false, epoch_barrier: None }
    }
    pub fn joined(&mut self, id: ConnId) { self.joiners.insert(id); }
    pub fn joining(&self) -> bool {
        !self.joiners.is_empty() || self.gathers.iter().any(|gather| !gather.abandoned && match &gather.audience {
            GatherAudience::Fresh { joiners, .. } => !joiners.is_empty(),
            GatherAudience::Join(ids) => !ids.is_empty(),
        })
    }
    /// Front-door-local analytics only; recorder verbs remain byte-for-byte bridge relays.
    pub fn admin_reply(&self, request: &crate::admin::Request) -> Option<String> {
        crate::admin::local_reply(request)
    }
    pub fn command_failed(&mut self, id: ConnId, corr: u64, error: &str) {
        let mut event = self.event("ServerError", vec![hex_encode(error)], Some(id));
        event.event.corr = corr;
        self.command_failures.push(event);
    }
    pub fn left(&mut self, id: ConnId) {
        self.joiners.remove(&id);
        self.history_trimmed.remove(&id);
        for gather in &mut self.gathers {
            match &mut gather.audience {
                GatherAudience::Fresh { counted, joiners } => {
                    counted.retain(|conn| *conn != id);
                    joiners.retain(|conn| *conn != id);
                }
                GatherAudience::Join(ids) => ids.retain(|conn| *conn != id),
            }
        }
    }
    pub fn rebound(&mut self, old: ConnId, new: ConnId) {
        if self.joiners.remove(&old) { self.joiners.insert(new); }
        if let Some(count) = self.history_trimmed.remove(&old) { self.history_trimmed.insert(new, count); }
        for gather in &mut self.gathers {
            match &mut gather.audience {
                GatherAudience::Fresh { counted, joiners } => {
                    for id in counted.iter_mut().chain(joiners) { if *id == old { *id = new; } }
                }
                GatherAudience::Join(counted) => for id in counted { if *id == old { *id = new; } },
            }
        }
    }
    pub fn diag_clock(&mut self, report: &str, now: u64) -> Option<RoutedEvent> {
        let time = crate::world_time::parse_diag(report)?;
        if !crate::world_time::should_reconcile(time, self.last_world_time, now.saturating_sub(self.last_world_time_ms)) { return None; }
        self.last_world_time = Some(time);
        self.last_world_time_ms = now;
        self.report.push(format!("world_time_reconcile|at_ms={now}"));
        Some(self.event("WorldTimeChanged", vec![crate::messages::enc_record(
            &[time.day.to_string(), time.hours.to_string(), time.minutes.to_string()])], None))
    }
    fn event(&self, kind: &str, fields: Vec<String>, requester: Option<ConnId>) -> RoutedEvent {
        RoutedEvent { event: Event { seq: 0, tick: self.last_tick.max(0) as u64, corr: 0, kind: kind.into(), fields },
            owner: None, requester }
    }
    pub fn pump(&mut self, now: u64, server: &mut Server, driver: &mut impl GameDriver) -> Result<Vec<RoutedEvent>, DriverError> {
        let mut events = std::mem::take(&mut self.command_failures);
        let result = self.pump_into(now, server, driver, &mut events);
        match result {
            Ok(()) => Ok(events),
            Err(error) => {
                self.command_failures = events;
                Err(error)
            }
        }
    }
    pub fn terminal_events(&mut self, driver: &mut impl GameDriver, error: &DriverError) -> Vec<RoutedEvent> {
        let mut events = self.take_events(driver);
        events.extend(driver.terminal_events(error));
        events
    }
    pub fn take_events(&mut self, driver: &mut impl GameDriver) -> Vec<RoutedEvent> {
        let mut events = std::mem::take(&mut self.command_failures);
        events.extend(driver.take_pending_events());
        events
    }
    fn pump_into(&mut self, now: u64, server: &mut Server, driver: &mut impl GameDriver,
        events: &mut Vec<RoutedEvent>) -> Result<(), DriverError> {
        events.extend(driver.poll_routed_events()?);
        let pending_epoch = driver.take_epoch_announcement();
        if self.epoch_barrier.as_ref().map(|barrier| barrier.epoch) != pending_epoch {
            self.epoch_barrier = pending_epoch.map(|epoch| EpochBarrier { epoch, pages: 0, drained: false });
        }
        if let Some(barrier) = &mut self.epoch_barrier {
            barrier.drained |= self.ladder.pushing;
        }
        if server.joined_connections().is_empty() && self.epoch_barrier.is_none() { return Ok(()); }
        let ids: Vec<_> = server.joined_connections().into_iter().filter(|id| !driver.baseline_pending(*id)).collect();
        if !ids.is_empty() && self.cadence_batch.is_none()
            && now >= self.next_snapshot_ms && self.options.base_interval_ms > 0.0 {
            let decisions: Vec<_> = ids.iter().map(|id| server.connection(*id).expect("joined").cadence
                .decision(self.last_tick, self.options.base_interval_ms, self.options.thresholds)).collect();
            self.next_snapshot_ms = now + crate::cadence::effective_interval(&decisions, self.options.base_interval_ms);
            self.cadence_batch = Some(CadenceBatch { decisions: ids.into_iter().zip(decisions).collect(),
                stage: CadenceStage::Diag, full: None, delta: None });
        }
        for page in driver.presentation_pages()? {
            let lines = self.ladder.pushed_page(now, &page, &mut self.cursor);
            self.pushed.extend(lines);
        }
        if now >= self.next_presentation_ms {
            self.presentation_due = true;
            self.next_presentation_ms = now + self.options.presentation_poll_ms;
        }
        if self.epoch_barrier.is_none() && self.presentation_due
            && matches!(self.initialization, Initialization::Ready) && self.cadence_batch.is_none() {
            self.plan_presentation(now, server, driver);
        }
        self.abandon_gathers(now, events);
        self.flush_presentation(now, server, events);
        if let Some(io) = self.pending_io.take() {
            match driver.poll_stream_request() {
                Ok(Some(reply)) => self.complete_io(io, reply, now, events)?,
                Ok(None) => { self.pending_io = Some(io); return Ok(()); }
                Err(error) => {
                    self.failed_io(io, events);
                    return Err(error);
                }
            }
            // Give the driver's pending baseline a turn before another native
            // request is issued. No positive-duration bridge wait runs here.
            self.finish_epoch_barrier(now, driver, events);
            self.flush_presentation(now, server, events);
            return Ok(());
        }
        loop {
            if driver.stream_request_pending() { break; }
            self.finish_epoch_barrier(now, driver, events);
            if server.joined_connections().is_empty() && self.epoch_barrier.is_none() { break; }
            if self.epoch_barrier.is_none() && self.presentation_due
                && matches!(self.initialization, Initialization::Ready) && self.cadence_batch.is_none() {
                self.plan_presentation(now, server, driver);
            }
            let Some(io) = self.next_io(server, events) else {
                if self.epoch_barrier.is_none() && self.presentation_due
                    && matches!(self.initialization, Initialization::Ready) && self.cadence_batch.is_none() { continue; }
                break;
            };
            let request = match &io {
                StreamIo::Ring => StreamRequest::Recorder(vec!["ring-on".into()]),
                StreamIo::Cursor { .. } => StreamRequest::Recorder(vec!["dump".into(), presentation::FROM_NOW_CURSOR.to_string()]),
                StreamIo::Config(config) => StreamRequest::Recorder(config.args.clone()),
                StreamIo::Diag => StreamRequest::DiagState,
                StreamIo::Full => StreamRequest::Snapshot(-1),
                StreamIo::Delta(since) => StreamRequest::Snapshot(*since),
                StreamIo::Gather => {
                    let gather = self.gathers.front_mut().expect("active gather");
                    gather.locked = true;
                    StreamRequest::Recorder(gather.history.request().expect("unfinished gather"))
                }
                StreamIo::Dump => StreamRequest::Recorder(vec!["dump".into(), self.cursor.to_string(), self.dump_targets.clone()]),
                StreamIo::EpochDump => {
                    let claims = driver.claimed_sims();
                    let lots: HashSet<_> = claims.iter().filter_map(|id| self.sim_lots.get(id).copied().flatten()).collect();
                    let mut targets: Vec<_> = claims.into_iter().chain(self.sim_lots.iter()
                        .filter(|(_, lot)| lot.is_some_and(|lot| lots.contains(&lot))).map(|(id, _)| *id)).collect();
                    targets.sort_unstable();
                    targets.dedup();
                    let targets = if targets.is_empty() { "-".into() }
                        else { targets.iter().map(u64::to_string).collect::<Vec<_>>().join(",") };
                    StreamRequest::Recorder(vec!["dump".into(), self.cursor.to_string(), targets])
                }
            };
            match driver.start_stream_request(&request) {
                Ok(Some(reply)) => self.complete_io(io, reply, now, events)?,
                Ok(None) => { self.pending_io = Some(io); break; }
                Err(error) => { self.failed_io(io, events); return Err(error); }
            }
        }
        self.finish_epoch_barrier(now, driver, events);
        self.flush_presentation(now, server, events);
        Ok(())
    }
    fn finish_epoch_barrier(&mut self, now: u64, driver: &mut impl GameDriver, events: &mut Vec<RoutedEvent>) {
        if self.pending_io.is_some() || driver.stream_request_pending() || !self.gathers.is_empty()
            || !self.epoch_barrier.as_ref().is_some_and(|barrier| barrier.drained) { return; }
        let barrier = self.epoch_barrier.take().expect("drained epoch");
        for batch in self.pushed.chunks(presentation::MAX_LINES) {
            events.push(self.event("SimAnimation", vec![enc_list(&batch.iter().map(|line| hex_encode(line)).collect::<Vec<_>>())], None));
        }
        self.pushed.clear();
        self.broadcast_due = false;
        driver.announced(barrier.epoch);
        events.extend(driver.take_pending_events());
        self.last_presentation_ms = now;
    }
    fn plan_presentation(&mut self, now: u64, server: &Server, driver: &impl GameDriver) {
        self.presentation_due = false;
        let claims = driver.claimed_sims();
        let lots: HashSet<_> = claims.iter().filter_map(|id| self.sim_lots.get(id).copied().flatten()).collect();
        let target_set: HashSet<_> = claims.into_iter().chain(self.sim_lots.iter()
            .filter(|(_, lot)| lot.is_some_and(|lot| lots.contains(&lot))).map(|(id, _)| *id)).collect();
        let mut targets: Vec<_> = target_set.iter().copied().collect();
        targets.sort();
        if targets.is_empty() {
            if self.ladder.pushing {
                self.configs.push_back(RecorderConfig { args: vec!["push-targets".into(), "-".into()], push_target: None, at_ms: now });
            } else { self.skip_cursor = true; }
            self.targets.clear();
            self.pushed.clear();
            self.want_dump = false;
            return;
        }
        let target_field = if targets.is_empty() { "-".into() } else { targets.iter().map(u64::to_string).collect::<Vec<_>>().join(",") };
        match self.ladder.evaluate(now) {
            Some(LadderStep::UseDumps) => {
                self.configs.push_back(RecorderConfig { args: vec!["push-off".into()], push_target: None, at_ms: now });
                self.push_targets_sent = None;
            }
            Some(LadderStep::TryPush) => {
                if driver.ring_available() {
                    self.configs.push_back(RecorderConfig { args: vec!["push-on".into(), self.cursor.to_string(), target_field.clone()],
                        push_target: Some(target_field.clone()), at_ms: now });
                } else {
                    self.report.push("ring_unavailable|safe_native_reader_not_attached".into());
                    self.ladder.push_result(now, false);
                }
            }
            None => {}
        }
        if self.ladder.pushing && self.push_targets_sent.as_ref() != Some(&target_field) {
            self.configs.push_back(RecorderConfig { args: vec!["push-targets".into(), target_field.clone()], push_target: None, at_ms: now });
            self.push_targets_sent = Some(target_field.clone());
        }
        let fresh: Vec<_> = targets.iter().filter(|id| !self.targets.contains(id)).copied().collect();
        let mut joiners: Vec<_> = self.joiners.iter().copied()
            .filter(|id| server.connection(*id).is_some() && !driver.baseline_pending(*id)).collect();
        joiners.sort();
        for id in &joiners { self.joiners.remove(id); }
        let known: Vec<_> = targets.iter().filter(|id| !fresh.contains(id)).copied().collect();
        if !fresh.is_empty() {
            self.gathers.push_back(JoinGather { history: presentation::HistoryGather::new(&fresh),
                audience: GatherAudience::Fresh { counted: server.joined_connections(), joiners: joiners.clone() },
                held: Vec::new(), started_ms: Some(now), locked: false, abandoned: false });
        }
        if !joiners.is_empty() && !known.is_empty() {
            for gather in &mut self.gathers {
                if let GatherAudience::Join(ids) = &mut gather.audience { ids.retain(|id| !joiners.contains(id)); }
            }
            self.gathers.push_back(JoinGather { history: presentation::HistoryGather::new(&known),
                audience: GatherAudience::Join(joiners), held: Vec::new(),
                started_ms: (!self.gathers.iter().any(|gather| matches!(gather.audience, GatherAudience::Fresh { .. }))).then_some(now),
                locked: false, abandoned: false });
        }
        self.targets = target_set;
        if !self.ladder.pushing {
            self.want_dump = true;
            if self.dump_pages == 0 { self.dump_targets = target_field; }
        } else { self.broadcast_due = true; }
    }
    fn next_io(&mut self, server: &mut Server, events: &mut Vec<RoutedEvent>) -> Option<StreamIo> {
        while let Some(batch) = &mut self.cadence_batch {
            match batch.stage {
                CadenceStage::Diag => return Some(StreamIo::Diag),
                CadenceStage::Full => {
                    if batch.decisions.iter().any(|(_, decision)| decision.action == Action::ForceFull) { return Some(StreamIo::Full); }
                    batch.stage = CadenceStage::Delta;
                }
                CadenceStage::Delta => {
                    if batch.decisions.iter().any(|(_, decision)| matches!(decision.action, Action::PublishDelta | Action::SlowAndPublish)) {
                        return Some(StreamIo::Delta(self.last_tick));
                    }
                    batch.stage = CadenceStage::Publish;
                }
                CadenceStage::Publish => self.publish_cadence(server, events),
            }
        }
        match self.initialization {
            Initialization::Ring => return Some(StreamIo::Ring),
            Initialization::Cursor if self.epoch_barrier.is_none() => return Some(StreamIo::Cursor { initial: true }),
            Initialization::Cursor | Initialization::Ready => {}
        }
        // An existing gather owns LastTargets through its final page. Finish its
        // old history/held lines before draining and announcing a new epoch.
        if self.epoch_barrier.is_some() {
            if !self.gathers.is_empty() { return Some(StreamIo::Gather); }
            return self.epoch_barrier.as_ref().filter(|barrier| !barrier.drained).map(|_| StreamIo::EpochDump);
        }
        if !self.gathers.front().is_some_and(|gather| gather.locked) {
            if let Some(config) = self.configs.pop_front() { return Some(StreamIo::Config(config)); }
        }
        if !self.gathers.is_empty() { return Some(StreamIo::Gather); }
        if self.skip_cursor { return Some(StreamIo::Cursor { initial: false }); }
        if self.want_dump { return Some(StreamIo::Dump); }
        None
    }
    fn complete_io(&mut self, io: StreamIo, reply: StreamReply, now: u64,
        events: &mut Vec<RoutedEvent>) -> Result<(), DriverError> {
        match (io, reply) {
            (StreamIo::Ring, StreamReply::Recorder(_)) => self.initialization = Initialization::Cursor,
            (StreamIo::Cursor { initial }, StreamReply::Recorder(report)) => {
                let seq = presentation::parse_dump(&report).0;
                self.cursor = self.cursor.max(seq.max(0));
                if initial { self.initialization = Initialization::Ready; } else { self.skip_cursor = false; }
            }
            (StreamIo::Config(config), StreamReply::Recorder(report)) => {
                if let Some(target) = config.push_target {
                    self.ladder.push_result(config.at_ms, report.contains("push=on"));
                    if self.ladder.pushing {
                        self.push_targets_sent = Some(target);
                        self.want_dump = false;
                        self.dump_pages = 0;
                    }
                }
            }
            (StreamIo::Diag, StreamReply::DiagState(report)) => {
                if let Some(report) = report { if let Some(event) = self.diag_clock(&report, now) { events.push(event); } }
                self.cadence_batch.as_mut().expect("cadence request").stage = CadenceStage::Full;
            }
            (StreamIo::Full, StreamReply::Snapshot(snapshot)) => {
                let batch = self.cadence_batch.as_mut().expect("cadence request");
                batch.full = snapshot; batch.stage = CadenceStage::Delta;
            }
            (StreamIo::Delta(_), StreamReply::Snapshot(snapshot)) => {
                let batch = self.cadence_batch.as_mut().expect("cadence request");
                batch.delta = snapshot; batch.stage = CadenceStage::Publish;
            }
            (StreamIo::Gather, StreamReply::Recorder(report)) => {
                let gather = self.gathers.front_mut().expect("gather request");
                if !gather.abandoned { gather.history.accept(&report); }
                if gather.abandoned || gather.history.request().is_none() {
                    let gather = self.gathers.pop_front().expect("finished gather");
                    let fresh = matches!(gather.audience, GatherAudience::Fresh { .. });
                    self.deliver_gather(gather, events);
                    if fresh {
                        for waiting in &mut self.gathers {
                            if matches!(waiting.audience, GatherAudience::Fresh { .. }) { break; }
                            if waiting.started_ms.is_none() { waiting.started_ms = Some(now); }
                        }
                    }
                    self.broadcast_due = true;
                }
            }
            (StreamIo::Dump, StreamReply::Recorder(report)) => {
                let (seq, more, lines) = presentation::parse_dump(&report);
                if seq >= 0 { self.cursor = self.cursor.max(seq); }
                self.pushed.extend(lines);
                self.dump_pages += 1;
                if !more || self.dump_pages == presentation::MAX_DUMP_PAGES {
                    self.want_dump = false; self.dump_pages = 0; self.broadcast_due = true;
                }
            }
            (StreamIo::EpochDump, StreamReply::Recorder(report)) => {
                let (seq, more, lines) = presentation::parse_dump(&report);
                if seq >= 0 { self.cursor = self.cursor.max(seq); }
                self.pushed.extend(lines);
                if matches!(self.initialization, Initialization::Cursor) { self.initialization = Initialization::Ready; }
                if let Some(barrier) = &mut self.epoch_barrier {
                    barrier.pages = (barrier.pages + 1).min(MAX_EPOCH_DUMP_PAGES);
                    barrier.drained = !more;
                    if more && barrier.pages == MAX_EPOCH_DUMP_PAGES {
                        self.epoch_dump_limit_hits += 1;
                        return Err(DriverError("navigation epoch dump page limit reached".into()));
                    }
                }
            }
            _ => return Err(DriverError("stream reply does not match its request".into())),
        }
        Ok(())
    }
    fn publish_cadence(&mut self, server: &mut Server, events: &mut Vec<RoutedEvent>) {
        let CadenceBatch { decisions, full, mut delta, .. } = self.cadence_batch.take().expect("cadence batch");
        if delta.as_ref().is_some_and(|snapshot| snapshot.sims.is_empty() && snapshot.objects.is_empty()) {
            if full.is_none() { self.last_tick = delta.as_ref().expect("empty delta").tick; }
            delta = None;
        }
        let tick = full.as_ref().map_or(0, |snapshot| snapshot.tick).max(delta.as_ref().map_or(0, |snapshot| snapshot.tick));
        for (id, decision) in decisions {
            if server.connection(id).is_none() { continue; }
            let forced = decision.action == Action::ForceFull;
            let snapshot = if forced { full.as_ref() } else if decision.action != Action::Suppress { delta.as_ref() } else { None };
            if let Some(snapshot) = snapshot {
                if tick <= 0 || (snapshot.sims.is_empty() && snapshot.objects.is_empty() && !forced) { continue; }
                if forced { server.connection_mut(id).expect("cadence recipient").cadence.force_full(tick); }
                let mut event = self.event("SnapshotDelta", vec![snapshot.wire_body(), crate::messages::enc_bool(forced).into()], Some(id));
                event.event.tick = snapshot.tick.max(0) as u64;
                events.push(event);
                self.report.push(format!("resync|player={}|action={:?}|tick={}", server.connection(id).and_then(|conn| conn.player).unwrap_or(0), decision.action, snapshot.tick));
            }
        }
        if tick > 0 { self.last_tick = tick; }
        if full.is_some() { self.sim_lots.clear(); }
        if let Some(snapshot) = full.as_ref().or(delta.as_ref()) {
            self.sim_lots.extend(snapshot.sims.iter().map(|sim| (sim.object_id, sim.lot_id)));
        }
    }
    fn deliver_gather(&mut self, mut gather: JoinGather, events: &mut Vec<RoutedEvent>) {
        match gather.audience {
            GatherAudience::Fresh { counted, .. } => {
                for id in counted { *self.history_trimmed.entry(id).or_default() += gather.history.result.history_trimmed; }
                gather.history.result.lines.append(&mut self.pushed);
                self.pushed = gather.history.result.lines;
            }
            GatherAudience::Join(ids) => {
                let mut lines = if gather.abandoned { Vec::new() } else { gather.history.result.lines };
                lines.append(&mut gather.held);
                for id in ids {
                    *self.history_trimmed.entry(id).or_default() += if gather.abandoned { 0 } else { gather.history.result.history_trimmed };
                    for batch in lines.chunks(presentation::MAX_LINES) {
                        events.push(self.event("SimAnimation", vec![enc_list(&batch.iter().map(|line| hex_encode(line)).collect::<Vec<_>>())], Some(id)));
                    }
                }
            }
        }
    }
    fn abandon_gathers(&mut self, now: u64, events: &mut Vec<RoutedEvent>) {
        let mut deliveries = Vec::new();
        for gather in &mut self.gathers {
            let Some(started) = gather.started_ms else { continue; };
            if gather.abandoned || now.saturating_sub(started) < 30_000 { continue; }
            let GatherAudience::Join(ids) = &mut gather.audience else { continue; };
            self.report.push(format!("join_history_abandoned|limit_ms=30000|held={}|joiners={}", gather.held.len(), ids.len()));
            deliveries.push((std::mem::take(ids), std::mem::take(&mut gather.held)));
            gather.abandoned = true;
        }
        for (ids, lines) in deliveries {
            for id in ids {
                for batch in lines.chunks(presentation::MAX_LINES) {
                    events.push(self.event("SimAnimation", vec![enc_list(&batch.iter().map(|line| hex_encode(line)).collect::<Vec<_>>())], Some(id)));
                }
            }
        }
        if self.pending_io.is_none() {
            while self.gathers.pop_front_if(|gather| gather.abandoned).is_some() {}
        }
    }
    fn flush_presentation(&mut self, now: u64, server: &Server, events: &mut Vec<RoutedEvent>) {
        if self.epoch_barrier.is_some() && self.gathers.is_empty() { return; }
        if self.pushed.is_empty() {
            self.broadcast_due = false;
            return;
        }
        if self.gathers.iter().any(|gather| matches!(gather.audience, GatherAudience::Fresh { .. })) {
            return;
        }
        let gathering = self.gathers.iter().any(|gather| !gather.abandoned && matches!(gather.audience, GatherAudience::Join(_)));
        if !self.broadcast_due && !gathering { return; }
        if self.ladder.pushing && now.saturating_sub(self.last_presentation_ms) < self.options.presentation_min_interval_ms { return; }
        let mut held = HashSet::new();
        for gather in &mut self.gathers {
            if let GatherAudience::Join(ids) = &gather.audience {
                if gather.abandoned { continue; }
                gather.held.extend(self.pushed.iter().cloned());
                held.extend(ids.iter().copied());
            }
        }
        for batch in self.pushed.chunks(presentation::MAX_LINES) {
            let fields = vec![enc_list(&batch.iter().map(|line| hex_encode(line)).collect::<Vec<_>>())];
            if held.is_empty() { events.push(self.event("SimAnimation", fields, None)); }
            else {
                for id in server.joined_connections().into_iter().filter(|id| !held.contains(id)) {
                    events.push(self.event("SimAnimation", fields.clone(), Some(id)));
                }
            }
        }
        self.pushed.clear();
        self.broadcast_due = false;
        self.last_presentation_ms = now;
    }
    fn failed_io(&mut self, io: StreamIo, events: &mut Vec<RoutedEvent>) {
        match io {
            StreamIo::Gather => {
                if let Some(mut gather) = self.gathers.pop_front_if(|gather| matches!(gather.audience, GatherAudience::Join(_))) {
                    self.report.push(format!("join_history_failed|held={}", gather.held.len()));
                    gather.abandoned = true;
                    self.deliver_gather(gather, events);
                }
            }
            StreamIo::Diag | StreamIo::Full | StreamIo::Delta(_) => self.cadence_batch = None,
            StreamIo::Config(config) if config.args.first().is_some_and(|arg| arg == "push-targets") => {
                self.push_targets_sent = None;
            }
            _ => {}
        }
    }
}
