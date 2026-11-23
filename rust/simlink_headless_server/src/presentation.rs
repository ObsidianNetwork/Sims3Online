#![forbid(unsafe_code)]
//! Recorder envelopes are parsed here; replay lines stay opaque.

use crate::transport::DriverError;

pub const MAX_LINES: usize = 200;
pub const MAX_DUMP_PAGES: usize = 16;
pub const FROM_NOW_CURSOR: i64 = i64::MAX / 2;

fn number_after<T: std::str::FromStr>(text: &str, key: &str) -> Option<T> {
    let tail = text.split_once(key)?.1;
    let end = tail.find(|c: char| !c.is_ascii_digit() && c != '-').unwrap_or(tail.len());
    tail[..end].parse().ok()
}
fn rows(report: &str) -> Vec<String> {
    report.split('\n').skip(1).filter(|row| !row.is_empty()).map(str::to_owned).collect()
}
pub fn parse_dump(report: &str) -> (i64, bool, Vec<String>) {
    let header = report.split('\n').next().unwrap_or("");
    (number_after(header, "seq=").unwrap_or(-1), header.contains("more=1"), rows(report))
}
pub fn parse_drivers(report: &str) -> Vec<u32> {
    let Some((_, tail)) = report.split_once("drivers=") else { return Vec::new(); };
    let end = tail.find(|c: char| !c.is_ascii_digit() && c != ',').unwrap_or(tail.len());
    tail[..end].split(',').filter_map(|handle| handle.parse().ok()).collect()
}
pub fn parse_driver(report: &str) -> (i32, Vec<String>) {
    (number_after(report.split('\n').next().unwrap_or(""), "next=").unwrap_or(-1), rows(report))
}
#[derive(Debug, Default)]
pub struct History { pub lines: Vec<String>, pub history_trimmed: u64 }
impl History {
    pub fn batches(&self) -> std::slice::Chunks<'_, String> { self.lines.chunks(MAX_LINES) }
}
/// The caller serializes this entire gather, including snapshot target selection.
/// There is deliberately no controller history ring or extra byte budget.
pub fn gather_history(
    targets: &[u64], mut recorder: impl FnMut(&[String]) -> Result<String, DriverError>,
) -> Result<History, DriverError> {
    let mut gather = HistoryGather::new(targets);
    while let Some(args) = gather.request() { gather.accept(&recorder(&args)?); }
    Ok(gather.result)
}

pub(crate) struct HistoryGather {
    targets: String,
    selected: bool,
    drivers: Vec<u32>,
    driver: usize,
    offset: i32,
    pages: usize,
    pub result: History,
}
impl HistoryGather {
    pub fn new(targets: &[u64]) -> Self {
        Self { targets: targets.iter().map(u64::to_string).collect::<Vec<_>>().join(","),
            selected: targets.is_empty(), drivers: Vec::new(), driver: 0, offset: 0, pages: 0,
            result: History::default() }
    }
    pub fn request(&self) -> Option<Vec<String>> {
        if !self.selected { return Some(vec!["snapshot".into(), self.targets.clone()]); }
        self.drivers.get(self.driver).map(|handle|
            vec!["snapshot-driver".into(), handle.to_string(), self.offset.to_string()])
    }
    pub fn accept(&mut self, report: &str) {
        if !self.selected {
            self.drivers = parse_drivers(report);
            self.selected = true;
            return;
        }
        let (next, lines) = parse_driver(report);
        self.result.lines.extend(lines);
        self.pages += 1;
        if next < 0 || self.pages == MAX_DUMP_PAGES {
            if next >= 0 { self.result.history_trimmed += 1; }
            self.driver += 1;
            self.offset = 0;
            self.pages = 0;
        } else { self.offset = next; }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LadderStep { UseDumps, TryPush }
#[derive(Debug)]
pub struct Ladder {
    requested: bool,
    pub pushing: bool,
    last_page_ms: u64,
    started_ms: u64,
    delay_ms: u64,
    rearm_at_ms: Option<u64>,
    pub fallbacks: u64,
    pub rearms: u64,
    pub rearm_dedup_dropped: u64,
    pub report: Vec<String>,
}
impl Ladder {
    pub fn new(requested: bool) -> Self {
        Self { requested, pushing: false, last_page_ms: 0, started_ms: 0,
            delay_ms: 0, rearm_at_ms: requested.then_some(0), fallbacks: 0, rearms: 0, rearm_dedup_dropped: 0, report: Vec::new() }
    }
    fn schedule(&mut self, now: u64, healthy: bool) {
        self.delay_ms = if healthy || self.delay_ms == 0 { 10000 } else { (self.delay_ms * 2).min(300000) };
        self.rearm_at_ms = Some(now + self.delay_ms);
    }
    pub fn page(&mut self, now: u64) { self.last_page_ms = now; }
    /// Only the recorder sequence prefix is inspected; replay payloads stay opaque.
    /// Compare before advancing the page watermark, including late pages in dump mode.
    pub fn pushed_page(&mut self, now: u64, report: &str, cursor: &mut i64) -> Vec<String> {
        let (seq, _, lines) = parse_dump(report);
        let before = self.rearm_dedup_dropped;
        let mut accepted = Vec::new();
        let mut preamble = Vec::new();
        for line in lines {
            if let Some(sequence) = line.split_once('|').and_then(|(prefix, _)| prefix.parse::<i64>().ok()) {
                // Synthetic object/driver introductions belong to the next real
                // event; zero is not a recorder watermark.
                if sequence == 0 {
                    preamble.push(line);
                    continue;
                }
                if sequence > 0 {
                    if sequence <= *cursor {
                        self.rearm_dedup_dropped += 1;
                        preamble.clear();
                        continue;
                    }
                    *cursor = sequence;
                }
            }
            accepted.append(&mut preamble);
            accepted.push(line);
        }
        if seq > *cursor { accepted.append(&mut preamble); }
        *cursor = (*cursor).max(seq);
        if self.rearm_dedup_dropped > before {
            self.report.push(format!("push_rearm_dedup|at_ms={now}|dropped={}|cursor={cursor}", self.rearm_dedup_dropped - before));
        }
        self.page(now);
        accepted
    }
    pub fn push_result(&mut self, now: u64, accepted: bool) {
        self.pushing = accepted;
        if accepted {
            self.started_ms = now;
            self.last_page_ms = now;
            self.rearm_at_ms = None;
            self.rearms += 1;
        } else {
            self.schedule(now, false);
        }
        self.report.push(format!("push_result|at_ms={now}|accepted={accepted}|rearm_delay_ms={}", self.delay_ms));
    }
    pub fn evaluate(&mut self, now: u64) -> Option<LadderStep> {
        if !self.requested { return None; }
        if self.pushing && now.saturating_sub(self.last_page_ms) >= 3000 {
            self.pushing = false;
            self.fallbacks += 1;
            self.schedule(now, self.last_page_ms.saturating_sub(self.started_ms) >= 60000);
            self.report.push(format!("push_fallback|at_ms={now}|silence_ms=3000|rearm_delay_ms={}", self.delay_ms));
            return Some(LadderStep::UseDumps);
        }
        if !self.pushing && self.rearm_at_ms.is_some_and(|at| now >= at) {
            self.rearm_at_ms = None;
            self.report.push(format!("push_retry|at_ms={now}"));
            return Some(LadderStep::TryPush);
        }
        None
    }
    pub fn rearm_delay_ms(&self) -> u64 { self.delay_ms }
    pub fn status(&self) -> String {
        format!("push={};fallbacks={};rearms={};rearm_dedup_dropped={};rearm_delay_ms={}", if self.pushing { "on" } else { "off" }, self.fallbacks, self.rearms, self.rearm_dedup_dropped, self.delay_ms)
    }
}
