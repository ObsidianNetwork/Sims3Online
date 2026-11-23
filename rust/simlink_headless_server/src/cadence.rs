#![forbid(unsafe_code)]
//! Controller policies, not a new scheduler. Snapshot deltas originate in the mod.

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Thresholds {
    pub warn_ticks: i64,
    pub hard_ticks: i64,
    pub max_interval_ms: f64,
}
impl Default for Thresholds {
    fn default() -> Self {
        Self { warn_ticks: 1500, hard_ticks: 6000, max_interval_ms: 10000.0 }
    }
}
impl Thresholds {
    pub fn from_args(args: &[String]) -> Result<Self, String> {
        let mut result = Self::default();
        for (i, arg) in args.iter().enumerate() {
            if !["--resync-warn-ticks", "--resync-hard-ticks", "--resync-max-interval-seconds"].contains(&arg.as_str()) { continue; }
            let value = args.get(i + 1).ok_or_else(|| format!("{arg} needs a value"))?;
            match arg.as_str() {
                "--resync-warn-ticks" => result.warn_ticks = value.parse().map_err(|_| format!("invalid {arg}"))?,
                "--resync-hard-ticks" => result.hard_ticks = value.parse().map_err(|_| format!("invalid {arg}"))?,
                _ => result.max_interval_ms = value.parse::<f64>().map_err(|_| format!("invalid {arg}"))? * 1000.0,
            }
        }
        if !result.max_interval_ms.is_finite() { return Err("invalid resync interval".into()); }
        Ok(result)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Action { PublishDelta, SlowAndPublish, ForceFull, Suppress }
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Decision {
    pub action: Action,
    pub interval_ms: f64,
    pub resync_from_tick: i64,
}
pub fn ack_lag(tick: i64, ack: i64) -> i64 { tick - ack }

/// ResyncPolicy.Decide: the validity guard must precede the hard threshold.
pub fn decide(tick: i64, ack: Option<i64>, in_flight: bool, base: f64, t: Thresholds) -> Decision {
    let mut result = Decision { action: Action::PublishDelta, interval_ms: base, resync_from_tick: tick };
    let Some(ack) = ack else { return result; };
    let lag = ack_lag(tick, ack);
    if lag > t.hard_ticks {
        result.action = if in_flight { Action::Suppress } else { Action::ForceFull };
    } else if lag > t.warn_ticks {
        let fraction = (lag - t.warn_ticks) as f64 / (t.hard_ticks - t.warn_ticks) as f64;
        result.action = Action::SlowAndPublish;
        result.interval_ms = (base + fraction * (t.max_interval_ms - base)).min(t.max_interval_ms);
    }
    result
}

/// ComputeEffectiveIntervalMs starts at base; RunLoopAsync floors its delay at 100ms.
pub fn effective_interval(decisions: &[Decision], base: f64) -> u64 {
    decisions.iter().fold(base, |interval, d| interval.min(d.interval_ms)).max(100.0) as u64
}

#[derive(Clone, Debug, Default)]
pub struct PlayerCadence {
    pub ack_tick: Option<i64>,
    pub resync_from_tick: Option<i64>,
}
impl PlayerCadence {
    pub fn ack(&mut self, tick: i64) {
        self.ack_tick = Some(self.ack_tick.map_or(tick, |old| old.max(tick)));
        if self.resync_from_tick.is_some_and(|from| self.ack_tick.is_some_and(|ack| ack >= from)) {
            self.resync_from_tick = None;
        }
    }
    pub fn force_full(&mut self, tick: i64) { self.resync_from_tick = Some(tick); }
    pub fn decision(&self, tick: i64, base: f64, thresholds: Thresholds) -> Decision {
        decide(tick, self.ack_tick, self.resync_from_tick.is_some(), base, thresholds)
    }
}
