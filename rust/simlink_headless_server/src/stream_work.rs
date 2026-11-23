#![forbid(unsafe_code)]
//! Recorder work survives a finite yield; native IO ownership remains in Stream.

use crate::model_cut::Session;

#[derive(Default)]
pub(crate) struct Drain {
    world: u64,
    authority: u64,
    sessions: Vec<Session>,
    claims: Vec<u64>,
    targets: String,
    generation: u64,
    scope_generation: u64,
    pending_since: Option<u64>,
    retry_at: u64,
    turn_pages: usize,
    pub pages: u64,
    pub rows: u64,
    pub utf16_units: u64,
    pub more: u64,
    pub yields: u64,
    pub errors: u64,
    pub invalidations: u64,
}
impl Drain {
    pub fn begin_turn(&mut self) { self.turn_pages = 0; }
    pub fn generation(&self) -> u64 { self.generation }
    /// The push scope: world, authority, sessions and claims. A changed target set is not a new
    /// scope, because the recorder retargets a live push (`push-targets`) without stopping it.
    pub fn scope_generation(&self) -> u64 { self.scope_generation }
    pub fn target_field(&self) -> &str { &self.targets }
    /// Ends the push scope by hand (no audience left to push for).
    pub fn cut_scope(&mut self) { self.scope_generation=self.scope_generation.wrapping_add(1); }
    pub fn pending(&self) -> bool { self.pending_since.is_some() }
    fn invalidate(&mut self) {
        if self.pending_since.take().is_some() { self.invalidations = self.invalidations.saturating_add(1); }
        self.retry_at = 0;
        self.generation = self.generation.wrapping_add(1);
    }
    pub fn observe(&mut self, world: u64, authority: u64, sessions: &[Session], claims: &[u64]) -> bool {
        if self.world == world && self.authority == authority && self.sessions == sessions && self.claims == claims { return false; }
        self.world = world;
        self.authority = authority;
        self.sessions = sessions.to_vec();
        self.claims = claims.to_vec();
        self.scope_generation=self.scope_generation.wrapping_add(1);
        self.invalidate();
        true
    }
    pub fn targets(&mut self, targets: &str) -> bool {
        if self.targets == targets { return false; }
        self.targets = targets.into();
        self.invalidate();
        true
    }
    pub fn resume(&self, now: u64) -> bool { self.pending() && now >= self.retry_at }
    pub fn allowed(&self, now: u64) -> bool {
        self.turn_pages < crate::presentation::MAX_DUMP_PAGES && now >= self.retry_at
    }
    pub fn complete(&mut self, generation: u64, more: bool, now: u64, lines: &[String]) -> bool {
        self.turn_pages += 1;
        self.pages = self.pages.saturating_add(1);
        self.rows = self.rows.saturating_add(lines.len() as u64);
        self.utf16_units = self.utf16_units.saturating_add(lines.iter()
            .map(|line| line.encode_utf16().count() as u64 + 1).sum::<u64>());
        self.more = self.more.saturating_add(u64::from(more));
        if generation != self.generation { return false; }
        self.retry_at = 0;
        if more { self.pending_since.get_or_insert(now); }
        else { self.pending_since = None; }
        if more && self.turn_pages == crate::presentation::MAX_DUMP_PAGES {
            self.yields = self.yields.saturating_add(1);
        }
        more && self.allowed(now)
    }
    pub fn failed(&mut self, generation: u64, now: u64, poll_ms: u64) {
        self.errors = self.errors.saturating_add(1);
        if generation != self.generation { return; }
        self.pending_since.get_or_insert(now);
        self.retry_at = now.saturating_add(poll_ms.max(50));
    }
    pub fn clear(&mut self) { self.invalidate(); }
    pub fn report(&self, now: u64) -> String {
        format!("presentation_dump_pages={}|presentation_dump_rows={}|presentation_dump_utf16_units={}|presentation_dump_more={}|presentation_dump_yields={}|presentation_dump_errors={}|presentation_dump_debt={}|presentation_dump_debt_age_ms={}|presentation_dump_retry_ms={}|presentation_dump_scope_invalidations={}",
            self.pages, self.rows, self.utf16_units, self.more, self.yields, self.errors,
            u8::from(self.pending()), self.pending_since.map_or(0, |start| now.saturating_sub(start)),
            self.retry_at.saturating_sub(now), self.invalidations)
    }
}

pub(crate) const IO_NAMES: [&str; 12] = ["ring", "routes", "cursor", "config", "diag", "full", "delta", "gather", "dump", "epoch_dump", "state", "participant"];
#[derive(Default, Clone, Copy)]
struct Stat { count: u64, errors: u64, busy_ms: u64, max_ms: u64 }
#[derive(Default)]
pub(crate) struct Io {
    stats: [Stat; 12],
    pending: Option<(usize, u64)>,
}
impl Io {
    pub fn start(&mut self, kind: usize, now: u64) {
        debug_assert!(self.pending.is_none());
        self.stats[kind].count = self.stats[kind].count.saturating_add(1);
        self.pending = Some((kind, now));
    }
    pub fn finish(&mut self, now: u64, failed: bool) {
        let Some((kind, start)) = self.pending.take() else { return; };
        let elapsed = now.saturating_sub(start);
        let stat = &mut self.stats[kind];
        stat.errors = stat.errors.saturating_add(u64::from(failed));
        stat.busy_ms = stat.busy_ms.saturating_add(elapsed);
        stat.max_ms = stat.max_ms.max(elapsed);
    }
    pub fn report(&self, now: u64) -> String {
        let mut result = format!("stream_io_pending={}|stream_io_pending_age_ms={}",
            self.pending.map_or("idle", |(kind, _)| IO_NAMES[kind]),
            self.pending.map_or(0, |(_, start)| now.saturating_sub(start)));
        for (kind, stat) in self.stats.iter().enumerate() {
            result.push_str(&format!("|stream_io_{}_count={}|stream_io_{}_errors={}|stream_io_{}_busy_ms={}|stream_io_{}_max_ms={}",
                IO_NAMES[kind], stat.count, IO_NAMES[kind], stat.errors, IO_NAMES[kind], stat.busy_ms, IO_NAMES[kind], stat.max_ms));
        }
        result
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn debt_yields_without_becoming_timer_work_and_terminal_clears_it() {
        let mut drain = Drain::default();
        for _ in 0..16 { drain.complete(0, true, 100, &["1|Pz|10|🦀".into()]); }
        assert!(!drain.allowed(100)); assert!(drain.pending()); assert_eq!(drain.yields, 1);
        drain.begin_turn(); assert!(drain.resume(100)); assert!(drain.allowed(100));
        assert!(!drain.complete(0, false, 100, &[])); assert!(!drain.pending());
        assert_eq!(drain.rows, 16); assert_eq!(drain.utf16_units, 16 * 11);
    }
    #[test]
    fn errors_back_off_and_stale_scope_cannot_recreate_work() {
        let mut drain = Drain::default();
        drain.complete(0, true, 10, &[]);
        drain.failed(0, 20, 250);
        assert!(!drain.resume(269)); assert!(!drain.allowed(269)); assert!(drain.resume(270));
        assert!(drain.observe(1, 2, &[], &[10]));
        assert!(!drain.complete(0, true, 300, &[])); assert!(!drain.pending());
        drain.failed(0, 400, 250); assert!(!drain.pending());
        let generation = drain.generation(); drain.complete(generation, true, 500, &[]);
        assert!(drain.targets("20")); assert!(!drain.pending());
        assert!(!drain.complete(generation, true, 500, &[]));
        let generation = drain.generation(); drain.complete(generation, true, 600, &[]);
        assert!(drain.observe(1, 2, &[], &[20])); assert!(!drain.pending());
    }
    #[test]
    fn same_id_session_and_authority_aba_do_not_inherit_pending_debt() {
        let mut ledger = crate::model_cut::Ledger::default();
        ledger.observe(1, &[(7, 2)]);
        let mut drain = Drain::default();
        drain.observe(1, 1, &[ledger.session(crate::ConnId(7)).unwrap()], &[10]);
        let old = drain.generation(); drain.complete(old, true, 100, &[]);
        ledger.invalidate(crate::ConnId(7)); ledger.observe(1, &[(7, 2)]);
        assert!(drain.observe(1, 1, &[ledger.session(crate::ConnId(7)).unwrap()], &[10]));
        let current = drain.generation(); drain.complete(current, true, 110, &[]);
        assert!(!drain.complete(old, false, 120, &[])); assert!(drain.pending());
        drain.failed(old, 130, 250); assert!(drain.resume(130));
        assert!(drain.observe(1, 2, &[ledger.session(crate::ConnId(7)).unwrap()], &[10]));
        assert!(!drain.pending());
    }
    #[test]
    fn io_accounting_keeps_one_owned_slot_and_bounded_named_stats() {
        let mut io = Io::default(); io.start(7, 100);
        assert!(io.report(140).contains("stream_io_pending=gather|stream_io_pending_age_ms=40"));
        io.finish(150, true); io.start(8, 160); io.finish(170, false);
        let report = io.report(200);
        assert!(report.contains("stream_io_gather_count=1|stream_io_gather_errors=1|stream_io_gather_busy_ms=50|stream_io_gather_max_ms=50"));
        assert!(report.contains("stream_io_dump_count=1|stream_io_dump_errors=0|stream_io_dump_busy_ms=10"));
    }
}
