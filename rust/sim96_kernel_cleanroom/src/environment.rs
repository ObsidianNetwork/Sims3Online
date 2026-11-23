#![forbid(unsafe_code)]

pub const MOON_PHASE_MAX: u8 = 7;
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum Season { Spring, Summer, Fall, Winter }
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum PrecipitationKind { None, Rain, Snow, Thunder, Hail }

// User-approved correction: these fields require 40 bytes with a full u64 tick.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct WorldEnvironmentState {
    pub season: u8,
    pub precipitation: u8,
    pub intensity_raw: u16,
    pub temperature_raw: i16,
    pub frost_level_raw: u16,
    pub wet_level_raw: u16,
    pub fog_level_raw: u16,
    pub wind_raw: u16,
    pub cloudiness_raw: u16,
    pub leaves_raw: u16,
    pub moon_phase: u8,
    pub _pad0: u8,
    pub holiday_lights_mask: u32,
    pub lot_dark_rendering: bool,
    pub _pad1: [u8; 3],
    pub tick_id: u64,
}
const _: () = assert!(std::mem::size_of::<WorldEnvironmentState>() == 40);
const _: () = assert!(std::mem::offset_of!(WorldEnvironmentState, season) == 0);
const _: () = assert!(std::mem::offset_of!(WorldEnvironmentState, precipitation) == 1);
const _: () = assert!(std::mem::offset_of!(WorldEnvironmentState, intensity_raw) == 2);
const _: () = assert!(std::mem::offset_of!(WorldEnvironmentState, temperature_raw) == 4);
const _: () = assert!(std::mem::offset_of!(WorldEnvironmentState, frost_level_raw) == 6);
const _: () = assert!(std::mem::offset_of!(WorldEnvironmentState, wet_level_raw) == 8);
const _: () = assert!(std::mem::offset_of!(WorldEnvironmentState, fog_level_raw) == 10);
const _: () = assert!(std::mem::offset_of!(WorldEnvironmentState, wind_raw) == 12);
const _: () = assert!(std::mem::offset_of!(WorldEnvironmentState, cloudiness_raw) == 14);
const _: () = assert!(std::mem::offset_of!(WorldEnvironmentState, leaves_raw) == 16);
const _: () = assert!(std::mem::offset_of!(WorldEnvironmentState, moon_phase) == 18);
const _: () = assert!(std::mem::offset_of!(WorldEnvironmentState, _pad0) == 19);
const _: () = assert!(std::mem::offset_of!(WorldEnvironmentState, holiday_lights_mask) == 20);
const _: () = assert!(std::mem::offset_of!(WorldEnvironmentState, lot_dark_rendering) == 24);
const _: () = assert!(std::mem::offset_of!(WorldEnvironmentState, _pad1) == 25);
const _: () = assert!(std::mem::offset_of!(WorldEnvironmentState, tick_id) == 32);
impl WorldEnvironmentState {
    pub fn advance_moon(&mut self) { self.moon_phase = (self.moon_phase + 1) % (MOON_PHASE_MAX + 1); }
    pub fn add_intensity(&mut self, delta: i32) {
        self.intensity_raw = (i64::from(self.intensity_raw) + i64::from(delta)).clamp(0, 10_000) as u16;
    }
    pub fn add_temperature(&mut self, delta: i32) {
        self.temperature_raw = (i64::from(self.temperature_raw) + i64::from(delta))
            .clamp(i64::from(i16::MIN), i64::from(i16::MAX)) as i16;
    }
    pub fn apply_server_state(&mut self, state: Self) -> bool {
        if state.tick_id < self.tick_id || state.moon_phase > MOON_PHASE_MAX
            || state.season > 3 || state.precipitation > 4 || state.intensity_raw > 10_000 { return false; }
        *self = state;
        true
    }
    pub fn holiday_lights_for(&self, household_index: u32) -> bool {
        household_index < 32 && self.holiday_lights_mask & (1 << household_index) != 0
    }
}
/// Card 26 §C golden-corpus hook (canary 14, weather + 32 B layout).
#[cfg(test)]
mod kdiff {
    use super::*;

    #[test]
    fn kdiff_canary_14() {
        let mut e = WorldEnvironmentState::default();
        for _ in 0..8 {
            e.advance_moon(); // 0..=MOON_PHASE_MAX then wraps: the 8th lands on 0
        }
        println!("KDIFF|canary=14|case=moon_wrap|got={:016x}", u64::from(e.moon_phase));
        assert_eq!(e.moon_phase, 0);
        e.add_intensity(50_000); // clamped at the sealed 10 000 ceiling
        println!("KDIFF|canary=14|case=intensity_clamp|got={:016x}", u64::from(e.intensity_raw));
        assert_eq!(e.intensity_raw, 10_000);
        // A server broadcast with an out-of-range moon phase is refused.
        let bad = WorldEnvironmentState { moon_phase: MOON_PHASE_MAX + 1, ..WorldEnvironmentState::default() };
        let rejected = (!e.apply_server_state(bad)) as u64;
        println!("KDIFF|canary=14|case=apply_rejects_bad_phase|got={:016x}", rejected);
        assert_eq!(rejected, 1);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn authoritative_weather_preserves_signed_temperature_and_clamps() {
        let mut e = WorldEnvironmentState { moon_phase: 7, intensity_raw: 10_000,
            temperature_raw: i16::MAX, ..WorldEnvironmentState::default() };
        e.advance_moon(); e.add_intensity(500); e.add_temperature(1);
        assert_eq!((e.moon_phase, e.intensity_raw, e.temperature_raw), (0, 10_000, i16::MAX));
        let server = WorldEnvironmentState { season: 3, temperature_raw: i16::MIN,
            tick_id: u64::MAX, holiday_lights_mask: 2, ..e };
        assert!(e.apply_server_state(server));
        assert_eq!(e.temperature_raw, i16::MIN);
        assert!(!e.holiday_lights_for(0) && e.holiday_lights_for(1));
        assert!(!e.apply_server_state(WorldEnvironmentState::default()));
    }
}
