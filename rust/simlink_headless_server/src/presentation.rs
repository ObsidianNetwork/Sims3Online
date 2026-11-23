#![forbid(unsafe_code)]
//! Recorder envelopes are parsed here; replay lines stay opaque.

use crate::transport::DriverError;
use std::collections::{HashMap, HashSet};

pub const MAX_LINES: usize = 200;
pub const MAX_DUMP_PAGES: usize = 16;
const BOUNDARY_HANDLE: u32 = 4_294_967_280;
const MAX_BOUNDARY_ROWS: usize = 32_768;
const MAX_BOUNDARY_CHARS: usize = 2_097_152;
const BOUNDARY_PAGE_CHARS: usize = 1500;
// Native pages greedily pack whole rows: two adjacent nonfinal pages together
// consume more than one page's capacity. This covers every bounded native
// capture, without treating a 1500-character page as 1500 records.
const MAX_BOUNDARY_PAGES: usize = 1 + 2 * MAX_BOUNDARY_CHARS / BOUNDARY_PAGE_CHARS;
const MAX_BASELINE_OBJECTS: usize = 4096;
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
#[derive(Debug, Clone)]
struct Coverage { cursor: i64, identity: String }
#[derive(Debug, Default)]
pub struct History {
    pub lines: Vec<String>,
    pub history_trimmed: u64,
    covered: HashMap<u32, Coverage>,
    /// Recorder cursor of a complete frozen Boundary capture, and the objects
    /// whose full transform its baseline rows establish.
    boundary_cut: Option<i64>,
    boundary_poses: HashSet<u64>,
}
impl History {
    pub fn batches(&self) -> std::slice::Chunks<'_, String> { self.lines.chunks(MAX_LINES) }
    pub(crate) fn max_cursor(&self) -> Option<i64> { self.covered.values().map(|coverage| coverage.cursor).max() }

    /// Frozen snapshots cover only their complete SACS driver lifetime, never global traffic.
    pub(crate) fn append_live(&mut self, live: Vec<String>) {
        let mut covered = self.covered.clone();
        let mut setup = HashSet::new();
        for line in live {
            let fields: Vec<_> = line.split('|').collect();
            // A held full transform recorded before the frozen Boundary cut is
            // older than the baseline transform delivered ahead of it: replaying
            // it would rewind the object. Only that row is superseded; partial
            // writes and every other held row keep their bytes and order (the
            // Boundary cursor is not a general cutoff).
            if let (Some(cut), Some(object)) = (self.boundary_cut, full_transform(&fields)) {
                let older = fields[0].parse::<i64>().is_ok_and(|sequence| sequence > 0 && sequence <= cut);
                if older && self.boundary_poses.contains(&object) { continue; }
            }
            let Some((sequence, handle)) = driver_record(&fields) else {
                self.lines.push(line);
                continue;
            };
            let Some(coverage) = covered.get(&handle) else {
                self.lines.push(line);
                continue;
            };
            if fields[1] == "A" {
                if sequence > coverage.cursor || (sequence == 0 && acquire_identity(&fields).as_ref() != Some(&coverage.identity)) {
                    covered.remove(&handle);
                    setup.remove(&handle);
                    self.lines.push(line);
                    continue;
                }
                if sequence == 0 {
                    setup.insert(handle);
                    continue;
                }
            }
            let discard = (sequence > 0 && sequence <= coverage.cursor)
                || (sequence == 0 && setup.contains(&handle) && matches!(fields[1], "S" | "AS" | "P" | "Ps" | "Pn" | "Ph"));
            if sequence > 0 { setup.clear(); }
            if !discard { self.lines.push(line); }
        }
    }
}

fn driver_record(fields: &[&str]) -> Option<(i64, u32)> {
    if fields.len() < 3 || !matches!(fields[1], "A" | "S" | "AS" | "P" | "Ps" | "Pn" | "Ph" | "E" | "R" | "X" | "AW") { return None; }
    let sequence = fields[0].parse::<i64>().ok()?;
    let handle = fields[2].parse::<u32>().ok()?;
    (sequence >= 0 && handle != 4_294_967_280).then_some((sequence, handle))
}

fn placement_object(fields: &[&str]) -> Option<u64> {
    if fields.len() < 3 || !matches!(fields[1], "Pz" | "Po" | "Ob" | "O" | "Od" | "T" | "Tx" | "B" | "U" | "Z" | "Pl" | "H" | "Wd") { return None; }
    if fields[1] == "Wd" && !fields.get(3).and_then(|tag| tag.split_once(':' )).is_some_and(|(name, _)| matches!(name, "SetPosition" | "SetTransform" | "SetForward" | "SetRotation" | "ObjectSetPosition")) { return None; }
    fields[2].parse::<u64>().ok().filter(|id| *id != 0)
}

/// A full object transform write: its latest value alone determines the pose.
fn full_transform(fields: &[&str]) -> Option<u64> {
    if fields.len() < 4 || fields[1] != "Ob" || fields[3].split_once(':')?.0 != "SetTransform" { return None; }
    fields[2].parse::<u64>().ok().filter(|id| *id != 0)
}

/// The object a live (not baseline) line fully places, if it is a full transform write.
pub(crate) fn live_full_transform(line: &str) -> Option<u64> {
    if !line.contains("|Ob|") { return None; }
    let fields: Vec<_> = line.split('|').collect();
    if fields.first() == Some(&"0") { return None; }
    full_transform(&fields)
}

/// Drop a live full transform when a later one for the same object follows in the same send with no
/// other line naming that object between them: the later value alone determines the pose, and a burst
/// released after a hold would otherwise make the client apply every stale pose in one frame.
/// Returns how many lines were dropped.
pub(crate) fn coalesce_transforms(lines: &mut Vec<String>) -> usize {
    let mut later: HashSet<u64> = HashSet::new();
    let mut keep = vec![true; lines.len()];
    for (index, line) in lines.iter().enumerate().rev() {
        if let Some(id) = live_full_transform(line) {
            if !later.insert(id) { keep[index] = false; }
        } else if !later.is_empty() {
            for field in line.split(['|', '~', ',']) {
                if let Ok(id) = field.parse::<u64>() { later.remove(&id); }
            }
        }
    }
    let before = lines.len();
    let mut index = 0;
    lines.retain(|_| { index += 1; keep[index - 1] });
    before - lines.len()
}

/// The Sims each driver is known to animate, learned from the typed lines that name one.
pub(crate) type DriverOwners = HashMap<u32, HashSet<u64>>;

/// The Sim a typed driver line names (A's owner, S's actor, E's and R's actor).
fn named_sim(fields: &[&str]) -> Option<u64> {
    if acquire_identity(fields).is_some() {
        fields.get(3).and_then(|id| id.parse::<u64>().ok())
    } else if fields.len() == 5 && fields[1] == "S" && fields[3].parse::<u32>().is_ok() {
        fields[4].parse::<u64>().ok()
    } else if ((fields.len() == 7 && fields[1] == "E") || (fields.len() == 11 && fields[1] == "R"))
        && fields[3..6].iter().all(|value| value.parse::<u32>().is_ok()) {
        fields[6].parse::<u64>().ok()
    } else { None }
}

/// Record what a line says about who its driver animates. An acquire starts the driver over (a handle
/// can be reused after a release nobody was sent), a release forgets it, the other typed lines add a Sim.
/// `live` is false for history rows: they are older than live lines already learned, so they only add.
pub(crate) fn learn_driver_owner(owners: &mut DriverOwners, line: &str, live: bool) {
    let fields: Vec<_> = line.split('|').collect();
    let Some((_, handle)) = driver_record(&fields) else { return };
    if fields[1] == "X" { if live { owners.remove(&handle); } return; }
    let Some(sim) = named_sim(&fields) else { return };
    let sims = owners.entry(handle).or_default();
    if live && fields[1] == "A" { sims.clear(); }
    sims.insert(sim);
}

fn acquire_identity(fields: &[&str]) -> Option<String> {
    if fields.len() != 8 || fields[1] != "A" { return None; }
    fields[2].parse::<u32>().ok()?;
    fields[3].parse::<u64>().ok()?;
    fields[4].parse::<u32>().ok()?;
    fields[5].parse::<u32>().ok()?;
    fields[6].parse::<u64>().ok()?;
    fields[7].parse::<i32>().ok()?;
    Some(fields[2..].join("|"))
}

fn snapshot_token() -> String {
    use std::hash::{BuildHasher, Hasher};
    use std::sync::atomic::{AtomicU64, Ordering};
    static NEXT: AtomicU64 = AtomicU64::new(0);
    let counter = NEXT.fetch_add(1, Ordering::Relaxed);
    let part = || {
        let mut hasher = std::collections::hash_map::RandomState::new().build_hasher();
        hasher.write_u64(counter);
        hasher.finish()
    };
    format!("{:016x}{:016x}", part(), part())
}
/// The caller serializes this entire gather, including snapshot target selection.
/// There is deliberately no controller history ring or extra byte budget.
pub fn gather_history(
    targets: &[u64], mut recorder: impl FnMut(&[String]) -> Result<String, DriverError>,
) -> Result<History, DriverError> {
    let mut gather = HistoryGather::new(targets);
    while let Some(args) = gather.request() { gather.accept(&recorder(&args)?)?; }
    Ok(gather.result)
}

pub(crate) struct HistoryGather {
    targets: String,
    target_ids: HashSet<u64>,
    selected: bool,
    drivers: Vec<u32>,
    driver: usize,
    offset: i32,
    pages: usize,
    tokens: Vec<String>,
    metadata: Option<Option<i64>>,
    driver_start: usize,
    boundary_chars: usize,
    live_target_drivers: HashSet<u32>,
    live_target_driver_overflow: bool,
    /// Objects every recipient is already receiving full live transforms for.
    streaming: HashSet<u64>,
    /// Boundary placement rows dropped because their object is streaming live.
    pub streaming_skipped: usize,
    /// Driver lines let through before selection because their driver animates only other Sims.
    pub other_sims_not_held: usize,
    pub result: History,
}
impl HistoryGather {
    pub fn progress_report(&self) -> String {
        let handle = self.drivers.get(self.driver).copied();
        let stage = if !self.selected { "select" } else if handle == Some(BOUNDARY_HANDLE) { "boundary" }
            else if handle.is_some() { "driver" } else { "catchup" };
        format!("presentation_history_stage={stage}|presentation_history_driver={}|presentation_history_offset={}|presentation_history_pages={}|presentation_history_rows={}|presentation_history_utf16_chars={}",
            handle.unwrap_or(0), self.offset, self.pages, self.result.lines.len() - self.driver_start, self.boundary_chars)
    }
    pub fn held_object_ids(&self) -> impl Iterator<Item = u64> + '_ { self.target_ids.iter().copied() }
    pub fn holds_object(&self, id: u64) -> bool { self.target_ids.contains(&id) }
    pub fn can_interleave_dump(&self) -> bool {
        self.selected && !self.drivers[self.driver..].contains(&4_294_967_280)
    }
    #[cfg(test)]
    pub fn holds_live_line(&self, line: &str) -> bool { self.holds_line(line, &DriverOwners::new()) }
    /// Before selection the recorder has not yet said which drivers belong to this gather's Sims, so every
    /// driver line is held, except one whose driver is known to animate only Sims this gather is not for:
    /// a gather waiting its turn must not stall everyone else's animation behind it.
    fn only_other_sims(&self, handle: u32, owners: &DriverOwners) -> bool {
        owners.get(&handle).is_some_and(|sims| !sims.is_empty() && sims.is_disjoint(&self.target_ids))
    }
    fn holds_line(&self, line: &str, owners: &DriverOwners) -> bool {
        let fields: Vec<_> = line.split('|').collect();
        driver_record(&fields).is_some_and(|(_, handle)| (!self.selected && !self.only_other_sims(handle, owners))
            || self.drivers.contains(&handle) || self.live_target_drivers.contains(&handle))
            // The fresh object's boundary baseline can contain Z and a captured
            // transform. Its live placement must follow that baseline, even while
            // independent driver pages yield the stream to established targets.
            || placement_object(&fields).is_some_and(|id| self.target_ids.contains(&id))
    }
    /// Capture may precede client creation. Learn only typed native ownership,
    /// never arbitrary numeric references. Once held, a handle stays held through
    /// this gather: releasing/reacquiring it must not overtake its old held A/R.
    #[cfg(test)]
    pub fn classify_live_line(&mut self, line: &str) -> bool { self.classify_live_line_among(line, &DriverOwners::new()) }
    pub fn classify_live_line_among(&mut self, line: &str, owners: &DriverOwners) -> bool {
        let fields: Vec<_> = line.split('|').collect();
        let target = named_sim(&fields);
        // R's appended native fields are wakeup, timeout, bridgeDriver,
        // bridgeActor (RecordState/RequestState), not arbitrary parameter IDs.
        let bridge_dependency = fields.len() == 11 && fields[1] == "R"
            && fields[7].parse::<f32>().is_ok_and(f32::is_finite)
            && fields[8].parse::<u32>().is_ok() && fields[10].parse::<u32>().is_ok()
            && fields[9].parse::<u32>().is_ok_and(|handle| handle != 0
                && (self.drivers.contains(&handle) || self.live_target_drivers.contains(&handle)
                    || owners.get(&handle).is_some_and(|sims| !sims.is_disjoint(&self.target_ids))));
        if target.is_some_and(|id| self.target_ids.contains(&id)) || bridge_dependency {
            if let Some((_, handle)) = driver_record(&fields) {
                if self.live_target_drivers.len() < 4096 || self.live_target_drivers.contains(&handle) {
                    self.live_target_drivers.insert(handle);
                } else { self.live_target_driver_overflow = true; }
            }
        }
        let held = self.holds_line(line, owners) || self.live_target_driver_overflow;
        if !held && !self.selected && driver_record(&fields).is_some() { self.other_sims_not_held += 1; }
        held
    }
    pub fn live_driver_overflow(&self) -> bool { self.live_target_driver_overflow }
    pub fn new(targets: &[u64]) -> Self {
        Self { targets: targets.iter().map(u64::to_string).collect::<Vec<_>>().join(","),
            target_ids: targets.iter().copied().collect(),
            selected: targets.is_empty(), drivers: Vec::new(), driver: 0, offset: 0, pages: 0,
            tokens: Vec::new(), metadata: None, driver_start: 0, boundary_chars: 0,
            live_target_drivers: HashSet::new(), live_target_driver_overflow: false,
            streaming: HashSet::new(), streaming_skipped: 0, other_sims_not_held: 0,
            result: History::default() }
    }
    /// Name objects whose full transform is already flowing live to every
    /// recipient of this gather. Their latest live line alone determines the
    /// pose, so a boundary baseline for them is older than what the recipients
    /// hold, and holding their live lines behind it stalls them for the length
    /// of the gather. The gather's own targets are never skipped.
    pub fn skip_streaming(&mut self, ids: impl IntoIterator<Item = u64>) {
        self.streaming = ids.into_iter().filter(|id| !self.target_ids.contains(id)).collect();
    }
    /// The same for an object whose full transform starts flowing live to every
    /// recipient while this gather is in flight: a car parked when the gather
    /// began that pulls away before a boundary page names it. Its boundary pose
    /// is the parked one, older than the live poses the recipients already hold,
    /// and holding its live lines from that page to the delivery froze it and
    /// then jumped it (two-lots-travel-stability 2026-11-15, motion 044329: both
    /// clients held the taxi 1.07 s, then moved it 9.7 m). Once the boundary
    /// pages are complete the object's lines are held and this is not reached.
    pub fn note_streaming(&mut self, id: u64) {
        if !self.target_ids.contains(&id) && self.streaming.len() < MAX_BASELINE_OBJECTS { self.streaming.insert(id); }
    }
    pub fn request(&self) -> Option<Vec<String>> {
        if !self.selected { return Some(vec!["snapshot".into(), self.targets.clone()]); }
        self.drivers.get(self.driver).map(|handle| {
            let mut args = vec!["snapshot-driver".into(), handle.to_string(), self.offset.to_string()];
            args.push(self.tokens[self.driver].clone());
            if *handle == 4_294_967_280 { args.push(self.targets.clone()); }
            args
        })
    }
    pub fn accept(&mut self, report: &str) -> Result<(), DriverError> {
        if !self.selected {
            self.drivers = parse_drivers(report);
            self.tokens = self.drivers.iter().map(|_| snapshot_token()).collect();
            self.selected = true;
            return Ok(());
        }
        let header: Vec<_> = report.split('\n').next().unwrap_or("").split(';').collect();
        let tokens: Vec<_> = header.iter().filter_map(|field| field.strip_prefix("snapshot=")).collect();
        let cursors: Vec<_> = header.iter().filter_map(|field| field.strip_prefix("cursor=")).collect();
        let metadata = match (tokens.as_slice(), cursors.as_slice()) {
            ([], []) => None,
            ([token], [cursor]) if token.len() == 32 && token.bytes().all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
                && *token == self.tokens[self.driver] => {
                    Some(cursor.bytes().all(|byte| byte.is_ascii_digit()).then(|| cursor.parse::<i64>().ok()).flatten()
                        .ok_or_else(|| DriverError("snapshot cursor invalid".into()))?)
                }
            _ => return Err(DriverError("snapshot metadata invalid or token mismatch".into())),
        };
        if self.metadata.is_some_and(|previous| previous != metadata) {
            return Err(DriverError("snapshot metadata changed between pages".into()));
        }
        self.metadata = Some(metadata);
        let (legacy_next, lines) = parse_driver(report);
        let next = if metadata.is_some() {
            let values: Vec<_> = header.iter().filter_map(|field| field.strip_prefix("next=")).collect();
            let [value] = values.as_slice() else { return Err(DriverError("snapshot page next offset ambiguous".into())); };
            value.parse::<i32>().ok().filter(|value| *value == -1 || *value > self.offset)
                .ok_or_else(|| DriverError("snapshot page next offset invalid or non-progressing".into()))?
        } else { legacy_next };
        let frozen_boundary = metadata.is_some() && self.drivers[self.driver] == BOUNDARY_HANDLE;
        let page_limit = if frozen_boundary { MAX_BOUNDARY_PAGES } else { MAX_DUMP_PAGES };
        let mut boundary_chars = self.boundary_chars;
        if frozen_boundary {
            let page_chars: usize = lines.iter().map(|line| line.encode_utf16().count() + 1).sum();
            if page_chars > BOUNDARY_PAGE_CHARS {
                return Err(DriverError("boundary snapshot page character bound exceeded".into()));
            }
            if next >= 0 && (lines.is_empty() || usize::try_from(next).ok() != Some(self.offset as usize + lines.len())) {
                return Err(DriverError("boundary snapshot page offset does not match its rows".into()));
            }
            boundary_chars = boundary_chars.saturating_add(page_chars);
            if boundary_chars > MAX_BOUNDARY_CHARS || self.result.lines.len() - self.driver_start + lines.len() > MAX_BOUNDARY_ROWS {
                return Err(DriverError("boundary snapshot capture capacity exceeded".into()));
            }
        }
        if metadata.is_some() && next >= 0 && self.pages + 1 >= page_limit {
            return Err(DriverError("tokenized snapshot history page limit reached".into()));
        }
        self.boundary_chars = boundary_chars;
        self.result.lines.extend(lines);
        self.pages += 1;
        if next < 0 || self.pages == page_limit {
            if self.drivers[self.driver] == 4_294_967_280 {
                // Boundary pages can establish props beyond the fresh Sim set.
                // No dump interleaves before these pages finish, so establish
                // their complete placement hold scope before the first dump.
                // Reference fields (including slot parents) do not confer ownership.
                if !self.streaming.is_empty() {
                    let before = self.result.lines.len();
                    let mut index = 0;
                    let (start, streaming) = (self.driver_start, &self.streaming);
                    self.result.lines.retain(|line| {
                        index += 1;
                        if index <= start { return true; }
                        let fields: Vec<_> = line.split('|').collect();
                        !(fields.first() == Some(&"0") && placement_object(&fields).is_some_and(|id| streaming.contains(&id)))
                    });
                    self.streaming_skipped += before - self.result.lines.len();
                }
                let mut objects = self.target_ids.clone();
                for line in &self.result.lines[self.driver_start..] {
                    let fields: Vec<_> = line.split('|').collect();
                    if fields.first() == Some(&"0") {
                        if let Some(id) = placement_object(&fields) { objects.insert(id); }
                    }
                    if objects.len() > MAX_BASELINE_OBJECTS {
                        return Err(DriverError("boundary placement hold scope capacity exceeded".into()));
                    }
                }
                self.target_ids = objects;
                // Only a complete tokenized capture names a cut that held rows can be compared with.
                if let Some(cursor) = metadata.filter(|_| next == -1) {
                    self.result.boundary_cut = Some(cursor);
                    for line in &self.result.lines[self.driver_start..] {
                        let fields: Vec<_> = line.split('|').collect();
                        if fields.first() == Some(&"0") {
                            if let Some(id) = full_transform(&fields) { self.result.boundary_poses.insert(id); }
                        }
                    }
                }
            }
            if next == -1 {
                if let Some(cursor) = metadata.filter(|_| self.drivers[self.driver] != 4_294_967_280) {
                    let handle = self.drivers[self.driver];
                    let identities: HashSet<_> = self.result.lines[self.driver_start..].iter().filter_map(|line| {
                        let fields: Vec<_> = line.split('|').collect();
                        if fields.first() == Some(&"0") && fields.get(2).and_then(|value| value.parse().ok()) == Some(handle) {
                            acquire_identity(&fields)
                        } else { None }
                    }).collect();
                    if identities.len() == 1 {
                        self.result.covered.insert(handle, Coverage { cursor, identity: identities.into_iter().next().expect("one identity") });
                    }
                }
            }
            if next >= 0 { self.result.history_trimmed += 1; }
            self.driver += 1;
            self.offset = 0;
            self.pages = 0;
            self.metadata = None;
            self.driver_start = self.result.lines.len();
            self.boundary_chars = 0;
        } else { self.offset = next; }
        Ok(())
    }
}

#[cfg(test)]
mod frozen_tests {
    #[test]
    fn fresh_capture_classifies_late_driver_ownership_without_holding_unrelated_states() {
        let mut gather = HistoryGather::new(&[30]);
        assert!(gather.classify_live_line("1|E|7|1|2|0|20"));
        gather.accept("drivers=8").unwrap();
        assert!(!gather.classify_live_line("1|E|7|1|2|0|20"));
        for record in ["2|A|9|30|1|2|3|-2", "3|E|9|1|2|0|30", "4|R|9|1|2|0|30|0|0|0|0", "5|X|9",
            "6|A|9|20|1|2|3|-2", "7|R|9|1|2|0|20|0|0|0|0", "8|S|10|123|30", "9|E|10|1|2|0|20"] {
            assert!(gather.classify_live_line(record), "{record}");
        }
        assert!(!gather.classify_live_line("10|E|7|1|2|0|20"));
        assert!(!gather.classify_live_line("11|Ph|7|123|30"), "numeric parameters are not ownership");
        assert!(gather.classify_live_line("12|R|11|1|2|0|20|0|0|9|123"), "typed bridge consumer depends on held driver");
        assert!(gather.classify_live_line("13|E|11|1|2|0|20"), "consumer's later states must not overtake its held request");
        assert!(!gather.live_driver_overflow());
    }
    #[test]
    fn fresh_late_driver_ownership_has_a_finite_fail_closed_cap() {
        let mut gather = HistoryGather::new(&[30]); gather.accept("drivers=8").unwrap();
        for driver in 10..4106 { assert!(gather.classify_live_line(&format!("1|A|{driver}|30|1|2|3|-2"))); }
        assert!(!gather.live_driver_overflow());
        assert!(gather.classify_live_line("2|A|9999|30|1|2|3|-2"));
        assert!(gather.live_driver_overflow());
        assert_eq!(gather.live_target_drivers.len(), 4096);
    }
    fn boundary_rows(gather: &mut HistoryGather, token: &str, rows: &[String]) -> Result<(), DriverError> {
        let mut offset = 0;
        while offset < rows.len() {
            let mut end = offset;
            let mut chars = 0;
            while end < rows.len() && chars + rows[end].encode_utf16().count() < BOUNDARY_PAGE_CHARS {
                chars += rows[end].encode_utf16().count() + 1;
                end += 1;
            }
            assert!(end > offset);
            let next = if end == rows.len() { -1 } else { end as i32 };
            gather.accept(&format!("next={next};snapshot={token};cursor=100\n{}", rows[offset..end].join("\n")))?;
            offset = end;
        }
        Ok(())
    }

    #[test]
    fn tokenized_boundary_accepts_native_character_pages_beyond_the_driver_page_limit() {
        let mut gather = HistoryGather::new(&[10]);
        gather.accept("drivers=4294967280").unwrap();
        let snapshot_id = gather.request().unwrap()[3].clone();
        let rows: Vec<_> = (100..500).map(|id| format!("0|Ob|{id}|SetTransform:m|1~0~0~0~0~1~0~0~0~0~1~0~1234.56789~45.12345~1200.45678~1")).collect();
        assert!(rows.iter().map(|row| row.len() + 1).sum::<usize>() > MAX_DUMP_PAGES * BOUNDARY_PAGE_CHARS);
        boundary_rows(&mut gather, &snapshot_id, &rows).unwrap();
        assert!(gather.request().is_none());
        assert_eq!(gather.result.lines, rows);
        assert!(gather.result.covered.is_empty());
        assert_eq!(gather.result.history_trimmed, 0);
    }

    #[test]
    fn frozen_boundary_native_capture_and_page_limits_fail_before_append() {
        for rows in [vec!["0|Q|".into(); MAX_BOUNDARY_ROWS + 1], vec!["x".repeat(1499); MAX_BOUNDARY_CHARS / 1500 + 1]] {
            let mut gather = HistoryGather::new(&[10]);
            gather.accept("drivers=4294967280").unwrap();
            let snapshot_id = gather.request().unwrap()[3].clone();
            assert!(boundary_rows(&mut gather, &snapshot_id, &rows).unwrap_err().0.contains("capacity"));
            assert!(gather.request().is_some());
            assert!(gather.result.covered.is_empty());
        }
        let mut gather = HistoryGather::new(&[10]);
        gather.accept("drivers=4294967280").unwrap();
        let snapshot_id = gather.request().unwrap()[3].clone();
        gather.pages = MAX_BOUNDARY_PAGES - 1;
        assert!(gather.accept(&format!("next=1;snapshot={snapshot_id};cursor=100\n0|Q|")).unwrap_err().0.contains("page limit"));
        assert!(gather.result.lines.is_empty());
    }

    #[test]
    fn frozen_boundary_counts_utf16_not_unicode_scalars_or_utf8_bytes() {
        let mut gather = HistoryGather::new(&[10]);
        gather.accept("drivers=4294967280").unwrap();
        let snapshot_id = gather.request().unwrap()[3].clone();
        let oversized = "😀".repeat(750);
        assert!(gather.accept(&format!("next=-1;snapshot={snapshot_id};cursor=100\n{oversized}")).unwrap_err().0.contains("character bound"));
        assert!(gather.result.lines.is_empty());
        let valid = "😀".repeat(749);
        gather.accept(&format!("next=-1;snapshot={snapshot_id};cursor=100\n{valid}")).unwrap();
        assert_eq!(gather.result.lines, [valid]);
    }

    #[test]
    fn frozen_boundary_cannot_skip_unreturned_rows() {
        let mut gather = HistoryGather::new(&[10]);
        gather.accept("drivers=4294967280").unwrap();
        let snapshot_id = gather.request().unwrap()[3].clone();
        for body in ["", "0|Q|"] {
            assert!(gather.accept(&format!("next=2;snapshot={snapshot_id};cursor=100\n{body}")).unwrap_err().0.contains("offset"));
            assert!(gather.result.lines.is_empty());
        }
    }
    use super::*;

    fn gather() -> HistoryGather {
        let mut gather = HistoryGather::new(&[10]);
        gather.accept("drivers=7").unwrap();
        gather
    }

    fn page(gather: &mut HistoryGather, next: i32, cursor: i64, body: &str) -> Result<(), DriverError> {
        let snapshot_id = gather.request().unwrap()[3].clone();
        gather.accept(&format!("lines=1;next={next};snapshot={snapshot_id};cursor={cursor}\n{body}"))
    }

    #[test]
    fn fresh_object_hold_is_typed_and_does_not_match_unrelated_reference_values() {
        let gather = gather();
        for kind in ["Pz", "Po", "Ob", "O", "Od", "T", "Tx", "B", "U", "Z", "Pl", "H"] {
            assert!(gather.holds_live_line(&format!("12|{kind}|10|20")), "{kind}");
            assert!(!gather.holds_live_line(&format!("12|{kind}|20|10")), "{kind}");
        }
        assert!(gather.holds_live_line("12|Wd|10|ObjectSetPosition:vi|1~2~3|0"));
        assert!(!gather.holds_live_line("12|Wd|10|SetObjectMaterial:su|texture|1"));
        assert!(!gather.holds_live_line("12|Wd|20|ObjectSetPosition:vi|10~2~3|0"));
        for line in ["12|Pz|bad|10", "12|Unknown|10|20", "12|Au|10|o|20|6162", "12|Pz"] {
            assert!(!gather.holds_live_line(line), "{line}");
        }
    }

    #[test]
    fn a_send_keeps_only_the_newest_transform_unless_another_line_names_the_object_between() {
        let mut lines: Vec<String> = ["5|Ob|42|SetTransform:m|a", "6|Ob|43|SetTransform:m|a", "7|Ob|42|SetTransform:m|b",
            "8|T|10|42|slot|1|0", "9|Ob|42|SetTransform:m|c", "10|Ob|43|SetTransform:m|b", "0|Ob|44|SetTransform:m|base",
            "11|Ob|44|SetTransform:m|live", "12|Ob|42|SetPosition:v|1~2~3", "13|Ob|42|SetTransform:m|d"].map(String::from).to_vec();
        assert_eq!(coalesce_transforms(&mut lines), 2);
        assert_eq!(lines, ["7|Ob|42|SetTransform:m|b", "8|T|10|42|slot|1|0", "9|Ob|42|SetTransform:m|c", "10|Ob|43|SetTransform:m|b",
            "0|Ob|44|SetTransform:m|base", "11|Ob|44|SetTransform:m|live", "12|Ob|42|SetPosition:v|1~2~3", "13|Ob|42|SetTransform:m|d"]);
    }

    #[test]
    fn a_gather_waiting_its_turn_holds_its_own_sims_drivers_and_unknown_ones_but_not_other_sims() {
        let mut owners = DriverOwners::new();
        for line in ["0|A|7|42|1|0|5|-2", "0|S|7|3|42"] { learn_driver_owner(&mut owners, line, false); }
        let mut gather = HistoryGather::new(&[99]);
        assert!(!gather.classify_live_line_among("12|R|7|1|2|0|42|0|0|0|0", &owners), "another Sim's stand-up is not this gather's to hold");
        assert!(!gather.classify_live_line_among("13|P|7|x", &owners));
        assert_eq!(gather.other_sims_not_held, 2);
        assert!(gather.classify_live_line_among("14|P|8|x", &owners), "a driver nobody has named may be the fresh Sim's");
        assert!(gather.classify_live_line("12|R|7|1|2|0|42|0|0|0|0"), "without ownership knowledge everything is held, as before");
        learn_driver_owner(&mut owners, "15|A|9|99|1|0|5|-2", true);
        assert!(gather.classify_live_line_among("15|A|9|99|1|0|5|-2", &owners));
        assert!(gather.classify_live_line_among("16|R|10|1|2|0|42|0|0|9|1", &owners), "a request bridged to the fresh Sim's driver waits with it");
        // the other Sim's driver takes the fresh Sim as a second actor: from here it is held
        learn_driver_owner(&mut owners, "17|S|7|4|99", true);
        assert!(gather.classify_live_line_among("17|S|7|4|99", &owners));
        assert!(gather.classify_live_line_among("18|P|7|y", &owners));
        // a reused handle starts over at its acquire; a release forgets it
        learn_driver_owner(&mut owners, "19|A|7|55|1|0|5|-2", true);
        assert_eq!(owners[&7], HashSet::from([55]));
        learn_driver_owner(&mut owners, "20|X|7", true);
        assert!(!owners.contains_key(&7));
    }

    #[test]
    fn boundary_neither_rebaselines_nor_holds_an_object_that_starts_streaming_during_the_gather() {
        let mut gather = HistoryGather::new(&[20]);
        gather.accept("drivers=4294967280,7").unwrap();
        let snapshot_id = gather.request().unwrap()[3].clone();
        // the car pulls away after the snapshot was taken and before its boundary page arrives
        gather.note_streaming(42);
        gather.note_streaming(20);
        gather.accept(&format!("next=-1;snapshot={snapshot_id};cursor=100\n0|Ob|42|SetTransform:m|parked\n0|T|42|10|slot\n0|Wd|42|ObjectSetOpacity:ff|1|1\n0|Ob|43|SetTransform:m|prop\n0|Ob|20|SetTransform:m|fresh")).unwrap();
        assert_eq!(gather.streaming_skipped, 2);
        assert_eq!(gather.result.lines, ["0|Wd|42|ObjectSetOpacity:ff|1|1", "0|Ob|43|SetTransform:m|prop", "0|Ob|20|SetTransform:m|fresh"]);
        assert!(!gather.holds_live_line("101|Ob|42|SetTransform:m|moving"), "the car's live transform is not delayed behind its parked pose");
        assert!(gather.holds_live_line("101|Ob|43|SetTransform:m|new"), "a prop that did not move is still ordered after its baseline");
        assert!(gather.holds_live_line("101|Ob|20|SetTransform:m|new"), "the gather's own target is never skipped");
        assert!(!gather.result.boundary_poses.contains(&42));
    }

    #[test]
    fn boundary_neither_rebaselines_nor_holds_an_object_already_streaming_live() {
        assert_eq!(live_full_transform("101|Ob|42|SetTransform:m|1~0"), Some(42));
        assert_eq!(live_full_transform("0|Ob|42|SetTransform:m|1~0"), None, "a baseline row is not a live write");
        assert_eq!(live_full_transform("101|Ob|42|SetPosition:v|1~0~0"), None, "a partial write does not determine the pose");
        let mut gather = HistoryGather::new(&[20]);
        gather.skip_streaming([42, 20]);
        gather.accept("drivers=4294967280,7").unwrap();
        let snapshot_id = gather.request().unwrap()[3].clone();
        gather.accept(&format!("next=-1;snapshot={snapshot_id};cursor=100\n0|Ob|42|SetTransform:m|stale\n0|T|42|10|slot\n0|Wd|42|ObjectSetOpacity:ff|1|1\n0|Ob|43|SetTransform:m|prop\n0|Ob|20|SetTransform:m|fresh")).unwrap();
        assert_eq!(gather.streaming_skipped, 2);
        assert_eq!(gather.result.lines, ["0|Wd|42|ObjectSetOpacity:ff|1|1", "0|Ob|43|SetTransform:m|prop", "0|Ob|20|SetTransform:m|fresh"]);
        assert!(!gather.holds_live_line("101|Ob|42|SetTransform:m|new"), "a streaming object's live transform is not delayed");
        assert!(gather.holds_live_line("101|Ob|43|SetTransform:m|new"), "a prop the boundary establishes is still ordered after it");
        assert!(gather.holds_live_line("101|Ob|20|SetTransform:m|new"), "the gather's own target is never skipped");
        assert!(!gather.result.boundary_poses.contains(&42));
    }

    #[test]
    fn completed_boundary_holds_props_it_places_but_not_incidental_references() {
        let mut gather = HistoryGather::new(&[20]);
        gather.accept("drivers=4294967280,7").unwrap();
        let snapshot_id = gather.request().unwrap()[3].clone();
        gather.accept(&format!("next=-1;snapshot={snapshot_id};cursor=100\n0|Ob|42|baseline\n0|T|42|10|slot\n0|Au|99|o|11|name\n0|Unknown|12")).unwrap();
        assert!(gather.holds_live_line("101|Ob|42|new"));
        assert!(gather.holds_live_line("102|Pl|42|0|0|0"));
        assert!(gather.holds_live_line("102|Tx|42|10|slot|offset"));
        assert!(gather.holds_live_line("102|B|42|10|bone"));
        assert!(gather.holds_live_line("102|Wd|42|ObjectSetPosition:vi|0~0~0|0"));
        for line in ["101|Pz|10|new", "101|Ob|11|new", "101|Ob|12|new", "101|Ob|99|new"] {
            assert!(!gather.holds_live_line(line), "reference-only or unknown records do not introduce placement: {line}");
        }
        let mut oversized = HistoryGather::new(&[20]);
        oversized.accept("drivers=4294967280").unwrap();
        let snapshot_id = oversized.request().unwrap()[3].clone();
        let lines = (1..=MAX_BASELINE_OBJECTS + 1).map(|id| format!("0|Ob|{id}|baseline")).collect::<Vec<_>>();
        assert!(boundary_rows(&mut oversized, &snapshot_id, &lines).unwrap_err().0.contains("capacity"));
    }

    #[test]
    fn frozen_pages_reuse_token_and_cover_only_complete_driver_lifetime() {
        let mut gather = gather();
        let first = gather.request().unwrap();
        assert_eq!(first.len(), 4);
        assert_eq!(first[3].len(), 32);
        assert!(first[3].bytes().all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b)));
        page(&mut gather, 2, 9, "0|A|7|1|2|3|4|-2\n0|S|7|5|10").unwrap();
        assert_eq!(gather.request().unwrap()[3], first[3]);
        page(&mut gather, -1, 9, "0|R|7|5|6|0|10").unwrap();
        assert!(gather.request().is_none());
        gather.result.append_live([
            "0|A|7|1|2|3|4|-2", "0|S|7|5|10", "9|R|7|5|6|0|10",
            "10|AW|7", "0|Rg|7|99", "8|R|8|5|6|0|20", "0|I|7|5", "11|R|7|5|8|0|10",
        ].map(String::from).to_vec());
        assert_eq!(gather.result.lines, [
            "0|A|7|1|2|3|4|-2", "0|S|7|5|10", "0|R|7|5|6|0|10",
            "10|AW|7", "0|Rg|7|99", "8|R|8|5|6|0|20", "0|I|7|5", "11|R|7|5|8|0|10",
        ]);
    }

    #[test]
    fn new_positive_acquire_and_mismatched_synthetic_acquire_end_coverage() {
        for acquire in ["10|A|7|1|2|3|4|-2", "0|A|7|999|2|3|4|-2"] {
            let mut gather = gather();
            page(&mut gather, -1, 9, "0|A|7|1|2|3|4|-2").unwrap();
            let live = [acquire, "0|S|7|5|10", "9|R|7|5|6|0|10"].map(String::from).to_vec();
            gather.result.append_live(live.clone());
            assert_eq!(&gather.result.lines[1..], live);
        }
    }

    #[test]
    fn legacy_missing_acquire_and_legacy_incomplete_pages_never_filter_live() {
        for mode in 0..3 {
            let mut gather = gather();
            if mode == 0 {
                gather.accept("lines=1;next=-1\n0|A|7|1|2|3|4|-2").unwrap();
            } else if mode == 1 {
                page(&mut gather, -1, 9, "0|R|7|5|6|0|10").unwrap();
            } else {
                for index in 0..MAX_DUMP_PAGES {
                    gather.accept(&format!("next={}\n0|A|7|1|2|3|4|-2", index + 1)).unwrap();
                }
                assert_eq!(gather.result.history_trimmed, 1);
            }
            let before = gather.result.lines.len();
            let live = ["0|A|7|1|2|3|4|-2", "0|S|7|5|10", "9|R|7|5|6|0|10"].map(String::from).to_vec();
            gather.result.append_live(live.clone());
            assert_eq!(&gather.result.lines[before..], live);
        }
    }

    #[test]
    fn tokenized_page_limit_rejects_incomplete_history() {
        let mut gather = gather();
        for index in 0..MAX_DUMP_PAGES - 1 {
            page(&mut gather, index as i32 + 1, 9, "0|A|7|1|2|3|4|-2").unwrap();
        }
        assert!(page(&mut gather, MAX_DUMP_PAGES as i32, 9, "0|S|7|5|10").is_err());
        assert!(gather.result.covered.is_empty());
    }

    #[test]
    fn boundary_state_freezes_explicit_scope_but_retains_protected_paging_and_no_cutoff() {
        let mut gather = HistoryGather::new(&[10]);
        gather.accept("drivers=4294967280").unwrap();
        let request = gather.request().unwrap();
        assert_eq!(&request[..3], ["snapshot-driver", "4294967280", "0"]);
        assert_eq!(request[4], "10");
        let token = &request[3];
        gather.accept(&format!("lines=1;next=1;snapshot={token};cursor=100\n0|Ob|10|SetPosition:v|0~0~0")).unwrap();
        assert!(!gather.can_interleave_dump(), "frozen boundary props lack a complete live hold scope");
        let next = gather.request().unwrap();
        assert_eq!(next[2], "1"); assert_eq!(&next[3], token); assert_eq!(next[4], "10");
        gather.accept(&format!("lines=0;next=-1;snapshot={token};cursor=100")).unwrap();
        assert!(gather.result.covered.is_empty());
        assert_eq!(gather.result.max_cursor(), None, "boundary cursor is never a global cutoff");
        gather.result.append_live(vec!["10|Ob|10|SetPosition:v|1~0~0".into()]);
        assert_eq!(gather.result.lines.last().unwrap(), "10|Ob|10|SetPosition:v|1~0~0");
    }

    #[test]
    fn legacy_boundary_ignores_optional_arguments_and_keeps_protected_paging() {
        let mut gather = HistoryGather::new(&[10, 20]);
        gather.accept("drivers=4294967280,7").unwrap();
        assert_eq!(gather.request().unwrap()[4], "10,20");
        gather.accept("next=1\n0|Ob|10|baseline").unwrap();
        assert!(!gather.can_interleave_dump());
        gather.accept("next=-1\n0|Ob|20|baseline").unwrap();
        assert!(gather.can_interleave_dump());
        assert!(gather.result.covered.is_empty());
        assert_eq!(gather.result.max_cursor(), None);
    }

    #[test]
    fn inconsistent_metadata_is_an_error_instead_of_partial_history() {
        for mode in ["cursor", "token", "absent", "duplicate", "next"] {
            let mut gather = gather();
            page(&mut gather, 1, 9, "0|A|7|1|2|3|4|-2").unwrap();
            let snapshot_id = gather.request().unwrap()[3].clone();
            let report = match mode {
                "cursor" => format!("next=-1;snapshot={snapshot_id};cursor=10"),
                "token" => "next=-1;snapshot=00000000000000000000000000000000;cursor=9".into(),
                "absent" => "next=-1".into(),
                "duplicate" => format!("next=-1;snapshot={snapshot_id};cursor=9;cursor=9"),
                _ => format!("snapshot={snapshot_id};cursor=9"),
            };
            assert!(gather.accept(&report).is_err(), "{mode}");
            assert!(gather.result.covered.is_empty());
        }
    }

    #[test]
    fn tokenized_driver_and_boundary_require_one_exact_progressing_next_field() {
        for handle in [7u32, 4_294_967_280] {
            for next in ["next=garbage;next=-1", "next=1;next=-1", "next=-1;next=1", "next=0", "next=-2", "next=garbage", "offset=1"] {
                let mut gather = HistoryGather::new(&[10]);
                gather.accept(&format!("drivers={handle}")).unwrap();
                let snapshot_id = gather.request().unwrap()[3].clone();
                let report = format!("{next};snapshot={snapshot_id};cursor=100\n0|Ob|10|baseline");
                assert!(gather.accept(&report).is_err(), "{handle}: {next}");
                assert!(gather.result.lines.is_empty(), "invalid pages must not append partial history");
                assert!(gather.result.covered.is_empty());
            }
        }
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
    /// Lines the recorder kept back until their object mattered to a target, accepted below the cursor.
    pub late_lines: u64,
    delivered: std::collections::HashSet<i64>,
    delivered_order: std::collections::VecDeque<i64>,
    /// Every sequence at or above this was either delivered (and is in `delivered`) or never sent.
    delivered_floor: Option<i64>,
    pub report: Vec<String>,
}
/// Delivered sequences remembered for telling a re-sent line from a late one.
const DELIVERED_WINDOW: usize = 131_072;
impl Ladder {
    pub fn new(requested: bool) -> Self {
        Self { requested, pushing: false, last_page_ms: 0, started_ms: 0,
            delay_ms: 0, rearm_at_ms: requested.then_some(0), fallbacks: 0, rearms: 0, rearm_dedup_dropped: 0, late_lines: 0,
            delivered: Default::default(), delivered_order: Default::default(), delivered_floor: None, report: Vec::new() }
    }
    fn schedule(&mut self, now: u64, healthy: bool) {
        self.delay_ms = if healthy || self.delay_ms == 0 { 10000 } else { (self.delay_ms * 2).min(300000) };
        self.rearm_at_ms = Some(now + self.delay_ms);
    }
    pub fn page(&mut self, now: u64) { self.last_page_ms = now; }
    fn note(&mut self, sequence: i64) {
        self.delivered_floor.get_or_insert(sequence);
        if self.delivered.insert(sequence) {
            self.delivered_order.push_back(sequence);
            if self.delivered_order.len() > DELIVERED_WINDOW {
                if let Some(old) = self.delivered_order.pop_front() {
                    self.delivered.remove(&old);
                    self.delivered_floor = self.delivered_floor.max(Some(old + 1));
                }
            }
        }
    }
    /// Lines that reached clients by a dump: a later pushed page repeating them is a duplicate.
    pub fn delivered(&mut self, lines: &[String]) {
        for line in lines {
            if let Some(sequence) = line.split_once('|').and_then(|(prefix, _)| prefix.parse::<i64>().ok()).filter(|n| *n > 0) {
                self.note(sequence);
            }
        }
    }
    /// The recorder holds an object's lines until the object matters to a target, then sends them with
    /// their original sequences ahead of the line that made it matter. Such a line sits below the
    /// cursor but was never delivered; a re-sent line was.
    fn late(&self, sequence: i64) -> bool {
        self.delivered_floor.is_some_and(|floor| sequence >= floor) && !self.delivered.contains(&sequence)
    }
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
                    if sequence <= *cursor && !self.late(sequence) {
                        self.rearm_dedup_dropped += 1;
                        preamble.clear();
                        continue;
                    }
                    if sequence <= *cursor { self.late_lines += 1; }
                    *cursor = (*cursor).max(sequence);
                    self.note(sequence);
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
    /// An acknowledged stop preserves an existing fallback delay; ordinary scope
    /// cuts without a scheduled retry may resume after their ordered barrier.
    pub fn quiesced(&mut self, now: u64) {
        self.pushing=false;
        if self.rearm_at_ms.is_none() { self.rearm_at_ms=self.requested.then_some(now); }
    }
    pub fn rearm_delay_ms(&self) -> u64 { self.delay_ms }
    pub fn status(&self) -> String {
        format!("push={};fallbacks={};rearms={};rearm_dedup_dropped={};rearm_delay_ms={};late_lines={}", if self.pushing { "on" } else { "off" }, self.fallbacks, self.rearms, self.rearm_dedup_dropped, self.delay_ms, self.late_lines)
    }
}
