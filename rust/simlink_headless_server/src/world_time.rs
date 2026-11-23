#![forbid(unsafe_code)]
//! WorldTimeReconcilePolicy, including its f32 arithmetic and non-wrapping week.

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct WorldTime { pub day: i32, pub hours: i32, pub minutes: i32 }
impl WorldTime {
    pub fn hours24(self) -> f32 { self.hours as f32 + self.minutes as f32 / 60.0 }
}
pub fn parse_diag(report: &str) -> Option<WorldTime> {
    let field = |key: &str| report.split(';').find_map(|part| part.strip_prefix(key)).and_then(|v| v.trim().parse::<i32>().ok());
    let time = WorldTime { day: field("clockDay=")?, hours: field("clockHours=")?, minutes: field("clockMinutes=")? };
    ((0..=6).contains(&time.day) && (0..=23).contains(&time.hours) && (0..=59).contains(&time.minutes)).then_some(time)
}
pub fn game_hours_drift(a: WorldTime, b: WorldTime) -> f32 {
    (a.day as f32 * 24.0 + a.hours24() - (b.day as f32 * 24.0 + b.hours24())).abs()
}
pub fn should_reconcile(time: WorldTime, last: Option<WorldTime>, wall_ms: u64) -> bool {
    let Some(last) = last else { return true; };
    let drift = game_hours_drift(time, last);
    if time.day != last.day { return wall_ms >= 30000 || drift >= 0.05; }
    drift >= 0.05 || (wall_ms >= 30000 && drift >= 0.02)
}
