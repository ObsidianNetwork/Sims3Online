#![forbid(unsafe_code)]
//! Sim clock + alarm manager (spec card 06). The authoritative monotonic tick
//! counter is a `u64` owned by the server; every calendar read is integer
//! arithmetic on those ticks (no float drift). The alarm manager is a binary
//! min-heap with a strict total ordering on `(due_tick, insertion_seq,
//! alarm_handle)` so same-tick ties pop deterministically.

/// Native sim tick rate (ticks / second, `SimIFace/Simulator.cs:189`).
pub const K_NATIVE_SIM_TICK_RATE: u32 = 30;
/// Ticks per sim-day (7 × 54_000 = 378_000 for a week).
pub const TICKS_PER_SIM_DAY: u64 = 54_000;
/// Ticks per sim-hour.
pub const TICKS_PER_SIM_HOUR: u64 = 2_250;
/// 37.5 ticks per sim-minute, doubled to stay integer (2 × 37.5).
pub const TICKS_PER_SIM_MIN_2X: u64 = 75;
/// An inactive (unclaimed) lot skips `Lot.Simulate` for this many ticks
/// (`Lot.cs:788`, code 30 / shipped 30).
pub const K_INACTIVE_LOT_SLEEP_TICKS: u32 = 30;

/// The shipped `GameSpeed` table (ITUN), not the code's (1, 3, 10, 20).
pub const SPEED_PAUSE: u32 = 0;
pub const SPEED_NORMAL: u32 = 1;
pub const SPEED_FAST: u32 = 3;
pub const SPEED_ULTRA: u32 = 20;
pub const SPEED_SKIP: u32 = 20;

/// Server-monotonic sim clock. `SET_WORLD_TIME` moves only `start_time_ticks`;
/// the tick counter itself never resets.
#[derive(Clone, Copy, Debug, Default)]
pub struct SimClock {
    pub start_time_ticks: i64,
    pub ticks_advanced: u64,
    pub native_tick_cnt: u64,
    pub speed: u32,
}

impl SimClock {
    pub fn new() -> Self {
        Self::default()
    }

    /// `sSimTickCnt + sStartTime.Ticks + sTicksAdvanced` (card 06 §A).
    pub fn current_ticks(&self) -> u64 {
        let ticks = i128::from(self.start_time_ticks)
            + i128::from(self.ticks_advanced) + i128::from(self.native_tick_cnt);
        if ticks < 0 {
            ticks.rem_euclid(i128::from(7 * TICKS_PER_SIM_DAY)) as u64
        } else {
            ticks as u64
        }
    }

    /// Integer minute-of-day: `(tod % 2250) * 2 / 75` (card 06 §C).
    pub fn minute_of(&self) -> u32 {
        let tod = self.current_ticks() % TICKS_PER_SIM_DAY;
        let m_ticks = tod % TICKS_PER_SIM_HOUR;
        ((m_ticks * 2) / TICKS_PER_SIM_MIN_2X) as u32
    }

    pub fn hour_of(&self) -> u32 {
        let tod = self.current_ticks() % TICKS_PER_SIM_DAY;
        (tod / TICKS_PER_SIM_HOUR) as u32
    }

    /// 0 = Sunday .. 6 = Saturday (`DaysOfTheWeek`).
    pub fn day_of_week(&self) -> u32 {
        ((self.current_ticks() / TICKS_PER_SIM_DAY) % 7) as u32
    }

    /// Move `start_time_ticks` by the shortest signed delta (within ±½ week)
    /// to the target `(day, hour, minute)`. Returns the delta applied.
    pub fn set_world_time_shortest(&mut self, day: u32, hour: u32, minute: u32) -> i64 {
        if day >= 7 || hour >= 24 || minute >= 60 {
            return 0;
        }
        if (self.day_of_week(), self.hour_of(), self.minute_of()) == (day, hour, minute) {
            return 0;
        }
        let week = 7 * TICKS_PER_SIM_DAY;
        let target_ticks = u64::from(day) * TICKS_PER_SIM_DAY
            + u64::from(hour) * TICKS_PER_SIM_HOUR
            + (u64::from(minute) * TICKS_PER_SIM_MIN_2X).div_ceil(2);
        let current = self.current_ticks() % week;
        let forward = (target_ticks + week - current) % week;
        let signed = if forward > week / 2 {
            forward as i64 - week as i64
        } else {
            forward as i64
        };
        self.start_time_ticks = self.start_time_ticks.wrapping_add(signed);
        signed
    }
}

/// Strict total ordering key for the alarm min-heap (card 06 §E).
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct AlarmKey {
    pub due_tick: u64,
    pub insertion_seq: u64,
    pub alarm_handle: u64,
}

const _: () = assert!(std::mem::size_of::<AlarmKey>() == 24);
const _: () = assert!(std::mem::offset_of!(AlarmKey, due_tick) == 0);
const _: () = assert!(std::mem::offset_of!(AlarmKey, insertion_seq) == 8);
const _: () = assert!(std::mem::offset_of!(AlarmKey, alarm_handle) == 16);

#[derive(Debug)]
struct Alarm {
    key: AlarmKey,
    repeating: bool,
    period: u64,
}

/// Deterministic priority min-heap of alarms keyed by `AlarmKey`.
#[derive(Default, Debug)]
pub struct AlarmManager {
    heap: Vec<Alarm>,
    insertion_seq: u64,
    next_handle: u64,
    now: u64,
}

impl AlarmManager {
    pub fn new() -> Self {
        Self::default()
    }

    /// Add an alarm due `delay_ticks` from `now`. Repeaters clamp the period to
    /// ≥ 1 tick. Returns the stable alarm handle.
    pub fn add_alarm(&mut self, delay_ticks: u64, repeating: bool, period_ticks: u64) -> u64 {
        let handle = self.next_handle;
        self.next_handle = self.next_handle.wrapping_add(1);
        let period = if repeating { period_ticks.max(1) } else { 0 };
        self.insert(Alarm {
            key: AlarmKey { due_tick: self.now.saturating_add(delay_ticks), insertion_seq: self.insertion_seq, alarm_handle: handle },
            repeating,
            period,
        });
        self.insertion_seq = self.insertion_seq.wrapping_add(1);
        handle
    }

    fn insert(&mut self, alarm: Alarm) {
        self.heap.push(alarm);
        let mut i = self.heap.len() - 1;
        while i > 0 {
            let parent = (i - 1) / 2;
            if self.heap[i].key < self.heap[parent].key {
                self.heap.swap(i, parent);
                i = parent;
            } else {
                break;
            }
        }
    }

    fn pop_min(&mut self) -> Option<Alarm> {
        if self.heap.is_empty() {
            return None;
        }
        let top = self.heap.swap_remove(0);
        if !self.heap.is_empty() {
            let mut i = 0;
            loop {
                let l = 2 * i + 1;
                let r = l + 1;
                let mut smallest = i;
                if l < self.heap.len() && self.heap[l].key < self.heap[smallest].key {
                    smallest = l;
                }
                if r < self.heap.len() && self.heap[r].key < self.heap[smallest].key {
                    smallest = r;
                }
                if smallest == i {
                    break;
                }
                self.heap.swap(i, smallest);
                i = smallest;
            }
        }
        Some(top)
    }

    /// Cancel an alarm by handle. Returns `true` if it was present.
    pub fn cancel(&mut self, handle: u64) -> bool {
        if let Some(pos) = self.heap.iter().position(|a| a.key.alarm_handle == handle) {
            let _ = self.heap.remove(pos);
            self.rebuild();
            true
        } else {
            false
        }
    }

    fn rebuild(&mut self) {
        let mut heap = std::mem::take(&mut self.heap);
        for i in (0..heap.len()).rev() {
            let mut j = i;
            loop {
                let l = 2 * j + 1;
                let r = l + 1;
                let mut smallest = j;
                if l < heap.len() && heap[l].key < heap[smallest].key {
                    smallest = l;
                }
                if r < heap.len() && heap[r].key < heap[smallest].key {
                    smallest = r;
                }
                if smallest == j {
                    break;
                }
                heap.swap(j, smallest);
                j = smallest;
            }
        }
        self.heap = heap;
    }

    /// Pop every alarm due at or before `now`, in `AlarmKey` order. Repeaters
    /// reschedule from `old_expiry + k*period` to the first expiry strictly past
    /// `now` (one fire per catch-up). Returns the fired handles.
    pub fn simulate(&mut self, now: u64) -> Vec<u64> {
        self.now = now;
        let mut fired = Vec::new();
        while let Some(top) = self.heap.first() {
            if top.key.due_tick > now {
                break;
            }
            let alarm = self.pop_min().unwrap();
            fired.push(alarm.key.alarm_handle);
            if alarm.repeating {
                if let Some(next_due) = next_expiry(alarm.key.due_tick, alarm.period, now) {
                    self.insert(Alarm {
                        key: AlarmKey { due_tick: next_due, ..alarm.key },
                        repeating: true,
                        period: alarm.period,
                    });
                }
            }
        }
        fired
    }

    /// Load-path clamp (card 06 §E.1): one-shots older than `resume_tick` move
    /// to it; repeaters fast-forward a whole number of periods to the first
    /// expiry after `resume_tick`.
    pub fn fix_load_alarms(&mut self, resume_tick: u64) {
        self.now = resume_tick;
        let alarms = std::mem::take(&mut self.heap);
        for alarm in alarms {
            let due = if alarm.repeating {
                if alarm.key.due_tick <= resume_tick {
                    let Some(next_due) = next_expiry(alarm.key.due_tick, alarm.period, resume_tick) else {
                        continue;
                    };
                    next_due
                } else {
                    alarm.key.due_tick
                }
            } else {
                alarm.key.due_tick.max(resume_tick)
            };
            self.insert(Alarm { key: AlarmKey { due_tick: due, insertion_seq: alarm.key.insertion_seq, alarm_handle: alarm.key.alarm_handle }, repeating: alarm.repeating, period: alarm.period });
        }
    }
}

fn next_expiry(old: u64, period: u64, now: u64) -> Option<u64> {
    let steps = (now - old) / period;
    old.checked_add(steps.checked_add(1)?.checked_mul(period)?)
}

/// Card 26 §C golden-corpus hook (canary 6).
#[cfg(test)]
mod kdiff {
    use super::*;

    #[test]
    fn kdiff_canary_06() {
        let c = SimClock {
            ticks_advanced: TICKS_PER_SIM_HOUR + 38, // the sealed half-tick boundary
            ..SimClock::default()
        };
        let minute = c.minute_of();
        println!("KDIFF|canary=6|case=minute_boundary|got={:016x}", u64::from(minute));
        assert_eq!(minute, 1);
        let mut am = AlarmManager::new();
        let h = am.add_alarm(100, true, 300);
        assert!(am.simulate(99).is_empty());
        let fired = am.simulate(100);
        println!("KDIFF|canary=6|case=alarm_first_fire|got={:016x}", fired.len() as u64);
        assert_eq!(fired, vec![h]);
        assert!(am.simulate(399).is_empty()); // repeater rescheduled to 400
        assert_eq!(am.simulate(400), vec![h]);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn calendar_integer_math_boundaries() {
        let mut c = SimClock::default();
        assert_eq!((c.minute_of(), c.hour_of(), c.day_of_week()), (0, 0, 0));
        c.ticks_advanced = 53_999;
        assert_eq!((c.minute_of(), c.hour_of(), c.day_of_week()), (59, 23, 0));
        c.ticks_advanced = 54_000;
        assert_eq!((c.minute_of(), c.hour_of(), c.day_of_week()), (0, 0, 1));
        c.ticks_advanced = 378_000 - 1;
        assert_eq!(c.day_of_week(), 6);
        c.ticks_advanced = 378_000;
        assert_eq!(c.day_of_week(), 0);
    }

    #[test]
    fn half_tick_minute_formula() {
        let mut c = SimClock {
            ticks_advanced: 2_250 + 37,
            ..SimClock::default()
        };
        assert_eq!(c.minute_of(), 0);
        c.ticks_advanced = 2_250 + 38;
        assert_eq!(c.minute_of(), 1);
    }

    #[test]
    fn set_world_time_shortest_forward_tie() {
        let mut c = SimClock::default();
        let delta = c.set_world_time_shortest(0, 0, 0);
        assert_eq!(delta, 0);
        assert_eq!(c.set_world_time_shortest(3, 12, 0), 189_000);
        assert_eq!((c.day_of_week(), c.hour_of(), c.minute_of()), (3, 12, 0));
        c.native_tick_cnt = 12;
        assert_eq!(c.set_world_time_shortest(3, 12, 0), 0);
        assert_eq!(c.set_world_time_shortest(0, 0, 1), -188_974);
        assert_eq!((c.day_of_week(), c.hour_of(), c.minute_of()), (0, 0, 1));
    }

    #[test]
    fn shortest_backward_jump_from_first_week_keeps_calendar() {
        let mut c = SimClock::default();
        assert_eq!(c.set_world_time_shortest(6, 23, 59), -37);
        assert_eq!(c.start_time_ticks, -37);
        assert_eq!((c.day_of_week(), c.hour_of(), c.minute_of()), (6, 23, 59));
        c.native_tick_cnt = 37;
        assert_eq!((c.day_of_week(), c.hour_of(), c.minute_of()), (0, 0, 0));
    }

    #[test]
    fn alarm_same_tick_pops_by_insertion_seq() {
        let mut m = AlarmManager::new();
        for _ in 0..16 {
            m.add_alarm(100, false, 0);
        }
        let fired = m.simulate(100);
        assert_eq!(fired.len(), 16);
        let mut sorted = fired.clone();
        sorted.sort();
        assert_eq!(fired, sorted);
    }

    #[test]
    fn repeater_clamps_period_to_one() {
        let mut m = AlarmManager::new();
        let h = m.add_alarm(10, true, 0);
        let fired = m.simulate(10);
        assert_eq!(fired, vec![h]);
        let fired = m.simulate(11);
        assert_eq!(fired, vec![h]);
    }

    #[test]
    fn repeater_catchup_fires_once() {
        let mut m = AlarmManager::new();
        let h = m.add_alarm(0, true, 10);
        let fired = m.simulate(50);
        assert_eq!(fired, vec![h]);
        assert!(m.simulate(50).is_empty());
    }

    #[test]
    fn cancel_removes_alarm() {
        let mut m = AlarmManager::new();
        let h = m.add_alarm(100, false, 0);
        assert!(m.cancel(h));
        assert!(!m.cancel(h));
        assert!(m.simulate(100).is_empty());
    }

    #[test]
    fn alarms_remain_relative_after_pump_and_do_not_wrap_at_tick_max() {
        let mut m = AlarmManager::new();
        m.simulate(100);
        let one = m.add_alarm(5, false, 0);
        let repeat = m.add_alarm(5, true, 1);
        assert!(m.simulate(104).is_empty());
        assert_eq!(m.simulate(105), vec![one, repeat]);
        assert_eq!(m.simulate(u64::MAX), vec![repeat]);
        assert!(m.simulate(u64::MAX).is_empty());
    }

    #[test]
    fn fix_load_alarms_clamps_oneshot_and_repeater() {
        let mut m = AlarmManager::new();
        m.add_alarm(5, false, 0);
        m.add_alarm(5, true, 10);
        m.fix_load_alarms(100);
        let fired = m.simulate(100);
        assert_eq!(fired.len(), 1);
        let fired = m.simulate(110);
        assert_eq!(fired.len(), 1);
    }
}
