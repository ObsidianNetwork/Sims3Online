#![forbid(unsafe_code)]
//! Card 59 F: the daemon's per-client v1 history, not the kernel's frame ring.

use std::collections::VecDeque;

pub const RING_BUFFER_FRAMES: usize = sim96_kernel_cleanroom::wan_transport::RING_BUFFER_FRAMES as usize;
pub const CLIENT_EVENT_RING_MAX_BYTES: usize = 67_108_864;
pub const RESUME_WINDOW_MAX_MS: u64 = 120_000;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RingEntry {
    pub epoch: u32,
    pub seq: u64,
    pub line: String,
    pub names_bulk: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LeaseEndReason {
    RingLineEvicted,
    RingByteEvicted,
    BulkInGap,
    TimeoutExpired,
    EpochChanged,
    Superseded,
    ExplicitLeave,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ResumeOutcome {
    Ok { epoch: u32, from_seq: u64, replayed_lines: Vec<String> },
    Fresh { epoch: u32, reason: LeaseEndReason },
}

impl ResumeOutcome {
    pub fn header_line(&self) -> String {
        match self {
            Self::Ok { epoch, from_seq, replayed_lines } =>
                format!("RESUME_OK|{epoch}|{from_seq}|{}", replayed_lines.len()),
            Self::Fresh { epoch, .. } => format!("RESUME_FRESH|{epoch}"),
        }
    }
}

#[derive(Clone, Debug)]
pub struct ClientEventRing {
    epoch: u32,
    entries: VecDeque<RingEntry>,
    total_bytes: usize,
    last_sent_seq: u64,
    disconnected_at_seq: Option<u64>,
    disconnected_at_ms: Option<u64>,
    ended: Option<LeaseEndReason>,
}

impl ClientEventRing {
    pub fn new(epoch: u32) -> Self {
        Self { epoch, entries: VecDeque::new(), total_bytes: 0, last_sent_seq: 0,
            disconnected_at_seq: None, disconnected_at_ms: None, ended: None }
    }

    pub fn len(&self) -> usize { self.entries.len() }
    pub fn is_empty(&self) -> bool { self.entries.is_empty() }
    pub fn total_bytes(&self) -> usize { self.total_bytes }
    pub fn last_seq(&self) -> u64 { self.last_sent_seq }
    pub fn disconnected(&self) -> bool { self.disconnected_at_ms.is_some() }

    pub fn append(&mut self, epoch: u32, seq: u64, line: String, names_bulk: bool) {
        if epoch != self.epoch { self.end(LeaseEndReason::EpochChanged); return; }
        self.last_sent_seq = seq;
        self.total_bytes += line.len();
        self.entries.push_back(RingEntry { epoch, seq, line, names_bulk });
        if self.disconnected_at_seq.is_some_and(|sent| seq > sent && names_bulk) {
            self.ended.get_or_insert(LeaseEndReason::BulkInGap);
        }
        while self.entries.len() > RING_BUFFER_FRAMES || self.total_bytes > CLIENT_EVENT_RING_MAX_BYTES {
            let reason = if self.entries.len() > RING_BUFFER_FRAMES {
                LeaseEndReason::RingLineEvicted
            } else { LeaseEndReason::RingByteEvicted };
            let entry = self.entries.pop_front().expect("over capacity");
            self.total_bytes -= entry.line.len();
            // Evicting pre-disconnect history is harmless. Close only when
            // the kernel's head <= S+1 gap rule stops covering the cursor.
            if self.disconnected_at_seq.is_some_and(|sent|
                self.entries.front().is_none_or(|head| head.seq > sent.saturating_add(1)))
            {
                self.ended.get_or_insert(reason);
            }
        }
    }

    pub fn mark_disconnected(&mut self, last_sent_seq: u64, now_monotonic_ms: u64) {
        self.disconnected_at_seq = Some(last_sent_seq);
        self.disconnected_at_ms = Some(now_monotonic_ms);
    }

    pub fn end(&mut self, reason: LeaseEndReason) {
        self.entries.clear();
        self.total_bytes = 0;
        self.ended = Some(reason);
    }

    pub fn check_lease_expired(&self, current_epoch: u32, now_monotonic_ms: u64) -> Option<LeaseEndReason> {
        if self.epoch != current_epoch { return Some(LeaseEndReason::EpochChanged); }
        if let Some(reason) = self.ended { return Some(reason); }
        if self.disconnected_at_ms.is_some_and(|at|
            now_monotonic_ms.saturating_sub(at) >= RESUME_WINDOW_MAX_MS)
        {
            return Some(LeaseEndReason::TimeoutExpired);
        }
        None
    }

    pub fn resume_outcome(&self, current_epoch: u32, resume_epoch: u32, last_seq: u64, now: u64) -> ResumeOutcome {
        let fresh = |reason| ResumeOutcome::Fresh { epoch: current_epoch, reason };
        if resume_epoch != current_epoch || self.epoch != current_epoch {
            return fresh(LeaseEndReason::EpochChanged);
        }
        if let Some(reason) = self.check_lease_expired(current_epoch, now) { return fresh(reason); }
        if !self.disconnected() || last_seq > self.last_sent_seq {
            return fresh(LeaseEndReason::RingLineEvicted);
        }
        if last_seq < self.last_sent_seq && self.entries.front().is_none_or(|head|
            head.epoch != current_epoch || head.seq > last_seq.saturating_add(1))
        {
            return fresh(LeaseEndReason::RingLineEvicted);
        }
        if self.entries.iter().any(|e| e.seq > last_seq && e.names_bulk) {
            return fresh(LeaseEndReason::BulkInGap);
        }
        ResumeOutcome::Ok { epoch: current_epoch, from_seq: last_seq.saturating_add(1),
            replayed_lines: self.entries.iter().filter(|e| e.seq > last_seq).map(|e| e.line.clone()).collect() }
    }

    pub fn try_resume(&mut self, current_epoch: u32, resume_epoch: u32, last_seq: u64, now: u64) -> ResumeOutcome {
        let outcome = self.resume_outcome(current_epoch, resume_epoch, last_seq, now);
        if matches!(outcome, ResumeOutcome::Ok { .. }) {
            self.disconnected_at_seq = None;
            self.disconnected_at_ms = None;
        }
        outcome
    }
}

pub fn parse_resume_cap(cap: &str) -> Option<(u32, u64)> {
    let (epoch, seq) = cap.split_once('.')?;
    if epoch.is_empty() || seq.is_empty() || !epoch.bytes().all(|b| b.is_ascii_digit())
        || !seq.bytes().all(|b| b.is_ascii_digit()) { return None; }
    Some((epoch.parse().ok()?, seq.parse().ok()?))
}
