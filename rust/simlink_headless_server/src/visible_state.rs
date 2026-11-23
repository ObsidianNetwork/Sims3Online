#![forbid(unsafe_code)]
//! Bounded visible, unclaimed Sim state publisher. Native IO belongs to Stream.

use crate::{messages as m, DriverError};
use std::collections::{BTreeSet, HashMap, HashSet, VecDeque};

pub(crate) const INTERVAL: u8 = 5;
pub(crate) const MAX_CANDIDATES: usize = 48;
pub(crate) const PROBE_BATCH: usize = 8;
pub(crate) const EVENT: &str = "__VisibleSimState";
const MAX_BUFFS: usize = 256;

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub(crate) struct Scope {
    pub world: u64,
    pub authority: u64,
    pub recipients: Vec<(u64, i32)>,
    pub candidates: Vec<u64>,
    pub leases: Vec<Lease>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub(crate) struct Lease {
    pub conn: u64,
    pub player: i32,
    pub sim: u64,
    pub generation: u64,
}
impl Scope {
    fn eligible_leases(&self) -> HashSet<Lease> {
        let candidates: HashSet<_> = self.candidates.iter().copied().collect();
        let recipients: HashSet<_> = self.recipients.iter().copied().collect();
        self.leases.iter().filter(|lease| candidates.contains(&lease.sim)
            && recipients.contains(&(lease.conn, lease.player))).copied().collect()
    }
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct Sample {
    pub id: u64,
    motives: [f32; 6],
    buffs: BTreeSet<(u64, u64)>,
}
impl Sample {
    pub fn fields(&self) -> Vec<String> {
        vec![
            self.id.to_string(),
            m::enc_list(&self.motives.map(|v| v.to_string())),
            m::enc_list(
                &self
                    .buffs
                    .iter()
                    .map(|(guid, origin)| m::enc_record(&[guid.to_string(), origin.to_string()]))
                    .collect::<Vec<_>>(),
            ),
        ]
    }
    pub fn motives(&self) -> [f32; 6] {
        self.motives
    }
    fn changed(&self, previous: Option<&Self>) -> bool {
        previous.is_none_or(|previous| {
            self.buffs != previous.buffs
                || self
                    .motives
                    .iter()
                    .zip(previous.motives)
                    .any(|(a, b)| (*a - b).abs() > 0.5)
        })
    }
}

pub(crate) fn parse(report: &str, requested: &[u64]) -> Result<Vec<Sample>, DriverError> {
    let bad = || DriverError("visible state report malformed".into());
    if report.len() > 64 * 1024 || requested.len() > PROBE_BATCH {
        return Err(bad());
    }
    if report == "-" || report.is_empty() {
        return Ok(Vec::new());
    }
    let mut seen = BTreeSet::new();
    let mut samples = Vec::new();
    for entry in report.split(';') {
        let parts: Vec<_> = entry.split(':').collect();
        if parts.len() != 3 {
            return Err(bad());
        }
        let id = parts[0].parse::<u64>().map_err(|_| bad())?;
        if id == 0 || !requested.contains(&id) || !seen.insert(id) {
            return Err(bad());
        }
        let values = parts[1]
            .split(',')
            .map(|value| value.parse::<f32>().map_err(|_| bad()))
            .collect::<Result<Vec<_>, _>>()?;
        let motives: [f32; 6] = values.try_into().map_err(|_| bad())?;
        if motives.iter().any(|value| !value.is_finite()) {
            return Err(bad());
        }
        let mut buffs = BTreeSet::new();
        if parts[2] != "-" {
            for buff in parts[2].split('+') {
                let (guid, origin) = buff.split_once('/').ok_or_else(bad)?;
                let value = (
                    guid.parse::<u64>().map_err(|_| bad())?,
                    origin.parse::<u64>().map_err(|_| bad())?,
                );
                if buffs.len() == MAX_BUFFS || !buffs.insert(value) {
                    return Err(bad());
                }
            }
        }
        samples.push(Sample { id, motives, buffs });
    }
    Ok(samples)
}

#[derive(Clone)]
pub(crate) struct Delivery {
    pub lease: Lease,
    sample: Sample,
    token: u64,
}
impl Delivery {
    /// The last field is private actor metadata, stripped before public encoding.
    pub fn fields(&self) -> Vec<String> {
        let mut fields = self.sample.fields();
        fields.push(self.token.to_string());
        fields
    }
}

#[derive(Default)]
pub(crate) struct Publisher {
    pub scope: Scope,
    cycles: u8,
    jobs: VecDeque<Vec<u64>>,
    last: HashMap<(u64, u64), Delivery>,
    pending: HashMap<u64, Delivery>,
    eligible_leases: HashSet<Lease>,
    eligible_sims: HashSet<u64>,
    next_token: u64,
    pub probes: u64,
    pub delivered: u64,
    pub stale: u64,
    pub errors: u64,
}
impl Publisher {
    pub fn observe(&mut self, scope: Scope) {
        if scope == self.scope {
            return;
        }
        let eligible = scope.eligible_leases();
        let sims: HashSet<_> = eligible.iter().map(|lease| lease.sim).collect();
        if self.scope.world != scope.world || self.scope.authority != scope.authority {
            self.jobs.clear();
            self.last.clear();
            self.pending.clear();
        } else {
            self.last.retain(|_, delivery| eligible.contains(&delivery.lease));
            self.pending.retain(|_, delivery| eligible.contains(&delivery.lease));
            for ids in &mut self.jobs { ids.retain(|id| sims.contains(id)); }
            self.jobs.retain(|ids| !ids.is_empty());
        }
        self.eligible_leases = eligible;
        self.eligible_sims = sims;
        self.scope = scope;
    }
    pub fn cadence(&mut self) {
        self.cycles += 1;
        if self.cycles < INTERVAL {
            return;
        }
        self.cycles = 0;
        if !self.jobs.is_empty() || !self.pending.is_empty() || self.scope.recipients.is_empty() {
            return;
        }
        self.jobs.extend(
            self.scope
                .candidates
                .iter()
                .take(MAX_CANDIDATES)
                .copied()
                .filter(|id| self.eligible_sims.contains(id))
                .collect::<Vec<_>>()
                .chunks(PROBE_BATCH)
                .map(<[u64]>::to_vec),
        );
    }
    pub fn request(&self) -> Option<(Scope, Vec<u64>)> {
        self.jobs
            .front()
            .map(|ids| (self.scope.clone(), ids.clone()))
    }
    pub fn accept(
        &mut self,
        scope: &Scope,
        ids: &[u64],
        report: &str,
    ) -> Result<Vec<Delivery>, DriverError> {
        if scope.world != self.scope.world || scope.authority != self.scope.authority {
            self.stale += 1;
            return Ok(Vec::new());
        }
        for queued in &mut self.jobs { queued.retain(|id| !ids.contains(id)); }
        self.jobs.retain(|ids| !ids.is_empty());
        self.probes += 1;
        let samples = match parse(report, ids) {
            Ok(samples) => samples,
            Err(error) => {
                self.errors += 1;
                return Err(error);
            }
        };
        let mut changed = Vec::new();
        let mut requeue = Vec::new();
        let captured = scope.eligible_leases();
        for sample in samples {
            let mut saw_stale = false;
            for lease in scope.leases.iter().filter(|lease| lease.sim == sample.id && captured.contains(lease)) {
                if !self.eligible_leases.contains(lease) {
                    self.stale += 1;
                    saw_stale = true;
                    continue;
                }
                let key = (lease.conn, lease.sim);
                if !sample.changed(self.last.get(&key).map(|previous| &previous.sample)) { continue; }
                self.next_token = self.next_token.checked_add(1)
                    .ok_or_else(|| DriverError("state delivery token exhausted".into()))?;
                let delivery_id = self.next_token;
                let delivery = Delivery { lease: *lease, sample: sample.clone(), token: delivery_id };
                self.pending.insert(delivery.token, delivery.clone());
                changed.push(delivery);
            }
            if saw_stale
                && self.eligible_sims.contains(&sample.id)
                && !self.jobs.iter().any(|batch| batch.contains(&sample.id))
                && !requeue.contains(&sample.id)
            {
                requeue.push(sample.id);
            }
        }
        if !requeue.is_empty() {
            self.jobs.extend(requeue.chunks(PROBE_BATCH).map(<[u64]>::to_vec));
        }
        Ok(changed)
    }
    pub fn permits(&self, fields: &[String]) -> bool {
        fields.last().and_then(|token| token.parse::<u64>().ok())
            .and_then(|token| self.pending.get(&token))
            .is_some_and(|delivery| self.eligible_leases.contains(&delivery.lease) && delivery.fields() == fields)
    }
    pub fn recipient(&self, fields: &[String]) -> Option<u64> {
        if !self.permits(fields) { return None; }
        self.pending.get(&fields.last()?.parse::<u64>().ok()?).map(|delivery| delivery.lease.conn)
    }
    pub fn complete(&mut self, fields: &[String], delivered: bool) {
        if !self.permits(fields) {
            return;
        }
        let token = fields.last().expect("validated delivery token").parse().expect("validated delivery token");
        if let Some(delivery) = self.pending.remove(&token) {
            if delivered {
                self.last.insert((delivery.lease.conn, delivery.lease.sim), delivery);
                self.delivered += 1;
            }
        }
    }
    pub fn failed(&mut self, scope: &Scope, ids: &[u64]) {
        if scope.world == self.scope.world && scope.authority == self.scope.authority {
            for queued in &mut self.jobs { queued.retain(|id| !ids.contains(id)); }
            self.jobs.retain(|ids| !ids.is_empty());
            self.errors += 1;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn scope() -> Scope {
        Scope {
            world: 1,
            authority: 2,
            recipients: vec![(1, 1)],
            candidates: vec![20],
            leases: vec![Lease { conn: 1, player: 1, sim: 20, generation: 1 }],
        }
    }
    fn due(p: &mut Publisher) {
        for _ in 0..INTERVAL {
            p.cadence();
        }
    }
    fn report(value: &str) -> String {
        format!("20:{value},2,3,4,5,6:18446744073709551615/18446744073709551615")
    }

    #[test]
    fn strict_report_rejects_nonfinite_wrong_counts_duplicates_and_foreign_sims() {
        assert_eq!(
            parse(&report("1"), &[20]).unwrap()[0].buffs,
            BTreeSet::from([(u64::MAX, u64::MAX)])
        );
        for text in [
            "20:1,2,3,4,5:-",
            "20:NaN,2,3,4,5,6:-",
            "20:1,inf,3,4,5,6:-",
            "21:1,2,3,4,5,6:-",
            "20:1,2,3,4,5,6:1/2+1/2",
            "20:1,2,3,4,5,6:1/2/3",
            "20:1,2,3,4,5,6:-;20:1,2,3,4,5,6:-",
        ] {
            assert!(parse(text, &[20]).is_err(), "{text}");
        }
        assert!(parse(
            &format!(
                "20:1,2,3,4,5,6:{}",
                (0..257)
                    .map(|v| format!("{v}/0"))
                    .collect::<Vec<_>>()
                    .join("+")
            ),
            &[20]
        )
        .is_err());
        assert!(parse(&"-".repeat(65_537), &[20]).is_err());
    }
    #[test]
    fn cadence_batch_and_cache_advance_only_after_delivery() {
        let mut p = Publisher::default();
        p.observe(scope());
        for _ in 1..INTERVAL {
            p.cadence();
            assert!(p.request().is_none());
        }
        p.cadence();
        let (scope, ids) = p.request().unwrap();
        let sample = p.accept(&scope, &ids, &report("1")).unwrap().remove(0);
        assert!(p.permits(&sample.fields()));
        p.complete(&sample.fields(), false);
        due(&mut p);
        let (_, ids) = p.request().unwrap();
        let retry = p.accept(&scope, &ids, &report("1")).unwrap().remove(0);
        p.complete(&retry.fields(), true);
        assert_eq!(p.delivered, 1);
        due(&mut p);
        assert!(p.accept(&scope, &ids, &report("1.5")).unwrap().is_empty());
        due(&mut p);
        assert_eq!(p.accept(&scope, &ids, &report("1.51")).unwrap().len(), 1);
    }
    #[test]
    fn scope_fences_pending_reads_and_prepared_events_including_claim_aba() {
        for edit in 0..4 {
            let mut p = Publisher::default();
            p.observe(scope());
            due(&mut p);
            let (old, ids) = p.request().unwrap();
            let sample = p.accept(&old, &ids, &report("1")).unwrap().remove(0);
            let mut next = old.clone();
            match edit {
                0 => next.world += 1,
                1 => next.authority += 2,
                2 => next.recipients[0].0 += 10,
                _ => next.candidates.clear(),
            }
            p.observe(next);
            assert!(!p.permits(&sample.fields()));
            p.complete(&sample.fields(), true);
            assert_eq!(p.delivered, 0);
            assert!(p.accept(&old, &ids, &report("2")).unwrap().is_empty());
            assert_eq!(p.stale, 1);
        }
    }
    #[test]
    fn malformed_mixed_report_is_transactional_and_jobs_are_bounded() {
        let mut p = Publisher::default();
        let mut s = scope();
        s.candidates = (1..=100).collect();
        s.leases = s.candidates.iter().map(|sim| Lease { conn: 1, player: 1, sim: *sim, generation: 1 }).collect();
        p.observe(s);
        due(&mut p);
        assert_eq!(p.jobs.len(), MAX_CANDIDATES / PROBE_BATCH);
        let (scope, ids) = p.request().unwrap();
        assert!(p
            .accept(&scope, &ids, "1:1,2,3,4,5,6:-;2:1,2,3,4,5,NaN:-")
            .is_err());
        assert!(p.pending.is_empty());
        assert!(p.last.is_empty());
        assert_eq!(p.errors, 1);
        assert!(p.jobs.iter().all(|job| job.len() <= PROBE_BATCH));
    }
    #[test]
    fn unrelated_membership_preserves_cache_and_queued_sample_tokens_fence_same_value_aba() {
        let mut p = Publisher::default();
        p.observe(scope());
        due(&mut p);
        let (original, ids) = p.request().unwrap();
        let old = p.accept(&original, &ids, &report("1")).unwrap().remove(0);
        let mut next = original.clone();
        next.candidates.push(40);
        next.leases.push(Lease { conn: 1, player: 1, sim: 40, generation: 2 });
        p.observe(next.clone());
        assert!(p.permits(&old.fields()), "unrelated Fresh candidate must not invalidate this sample");
        p.complete(&old.fields(), true);
        next.leases.retain(|lease| lease.sim == 20);
        next.leases[0].generation = 3;
        p.observe(next.clone());
        due(&mut p);
        let (current, ids) = p.request().unwrap();
        let fresh = p.accept(&current, &ids, &report("1")).unwrap().remove(0);
        assert_ne!(old.token, fresh.token);
        assert!(!p.permits(&old.fields()), "public field equality cannot bind an old token to the new lease");
        p.complete(&old.fields(), true);
        assert_eq!(p.delivered, 1);
        p.complete(&fresh.fields(), true);
        assert_eq!(p.delivered, 2, "history invalidation forces unchanged state to be resent");
    }
    #[test]
    fn per_recipient_success_and_retry_do_not_erase_unaffected_cache() {
        let mut p = Publisher::default();
        let mut both = scope();
        both.recipients.push((2, 2));
        both.leases.push(Lease { conn: 2, player: 2, sim: 20, generation: 2 });
        p.observe(both.clone());
        due(&mut p);
        let (_, ids) = p.request().unwrap();
        let prepared = p.accept(&both, &ids, &report("1")).unwrap();
        assert_eq!(prepared.len(), 2);
        for sample in prepared { p.complete(&sample.fields(), sample.lease.conn == 1); }
        due(&mut p);
        let retry = p.accept(&both, &ids, &report("1")).unwrap();
        assert_eq!(retry.len(), 1);
        assert_eq!(retry[0].lease.conn, 2);
        p.complete(&retry[0].fields(), true);
        let mut blocked = both.clone();
        blocked.leases.retain(|lease| lease.conn != 2);
        p.observe(blocked);
        both.leases[1].generation = 3;
        p.observe(both.clone());
        due(&mut p);
        let retry = p.accept(&both, &ids, &report("1")).unwrap();
        assert_eq!(retry.len(), 1);
        assert_eq!(retry[0].lease.conn, 2, "only the joining recipient needs an unchanged replay");
    }
    #[test]
    fn failed_old_read_after_membership_pruning_does_not_consume_an_unrelated_job() {
        let mut p = Publisher::default();
        let mut s = scope();
        s.candidates.push(30);
        s.leases.push(Lease { conn: 1, player: 1, sim: 30, generation: 2 });
        p.observe(s.clone());
        p.jobs = VecDeque::from([vec![20], vec![30]]);
        let (captured, ids) = p.request().unwrap();
        s.leases.retain(|lease| lease.sim != 20);
        p.observe(s);
        p.failed(&captured, &ids);
        assert_eq!(p.request().unwrap().1, vec![30]);
        assert_eq!(p.errors, 1);
    }
}
