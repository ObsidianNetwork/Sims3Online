//! The Rust half's lossless frame record (card 56 §G gate 6, `rust_bridge.record`).
//!
//! The same `dir seq=N <full line>` form the controller's `--bridge-record` writes (`ProcessMemoryBridge.cs:42-46`
//! names the frames, `DedicatedServer/Program.cs:509-526` the file line): a command frame is
//! `ctl->host seq=N slot=<slot> <line>` when it is written, a host frame `host->ctl seq=N <line>` when it is
//! consumed, and each file line is `[bridge yyyy-MM-dd HH:mm:ss.fffZ] <frame>`. One extractor in `scripts/` reads
//! both files.
#![forbid(unsafe_code)]

use std::time::{SystemTime, UNIX_EPOCH};

use crate::layout::{COMPAT_SLOT_OFFSET, CONTROL_HEADER_SIZE, SLOT_SIZE};

/// `DescribeSlot`: `compat0` for the compat slot, else the slot's index in the control region.
pub fn slot_name(offset: usize) -> String {
    if offset == COMPAT_SLOT_OFFSET {
        "compat0".to_string()
    } else {
        (offset.saturating_sub(CONTROL_HEADER_SIZE) / SLOT_SIZE).to_string()
    }
}

/// A command frame as written.
pub fn ctl_frame(seq: i32, offset: usize, line: &str) -> String {
    format!("ctl->host seq={seq} slot={} {line}", slot_name(offset))
}

/// A host frame as consumed.
pub fn host_frame(seq: i32, line: &str) -> String {
    format!("host->ctl seq={seq} {line}")
}

/// One file line: `[bridge <UTC time to the millisecond>Z] <frame>`.
pub fn record_line(at: SystemTime, frame: &str) -> String {
    let ms = at.duration_since(UNIX_EPOCH).map_or(0, |d| d.as_millis());
    let (days, rem) = ((ms / 86_400_000) as i64, ms % 86_400_000);
    let (y, m, d) = civil_from_days(days);
    let (h, min, s, milli) = (rem / 3_600_000, rem / 60_000 % 60, rem / 1000 % 60, rem % 1000);
    format!("[bridge {y:04}-{m:02}-{d:02} {h:02}:{min:02}:{s:02}.{milli:03}Z] {frame}")
}

/// The proleptic Gregorian date of a day count from 1970-01-01 (H. Hinnant's `civil_from_days`).
fn civil_from_days(z: i64) -> (i64, u32, u32) {
    let z = z + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let m = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    (yoe + era * 400 + i64::from(m <= 2), m, d)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::layout::{slot_byte_offset, CTL_SLOT_BASE, HOST_SLOT_BASE};
    use std::time::Duration;

    #[test]
    fn slots_are_named_as_the_controller_names_them() {
        assert_eq!(slot_name(COMPAT_SLOT_OFFSET), "compat0");
        assert_eq!(slot_name(slot_byte_offset(HOST_SLOT_BASE).unwrap()), "0");
        assert_eq!(slot_name(slot_byte_offset(CTL_SLOT_BASE + 3).unwrap()), "11");
        assert_eq!(ctl_frame(7, slot_byte_offset(CTL_SLOT_BASE).unwrap(), "PING|x"), "ctl->host seq=7 slot=8 PING|x");
        assert_eq!(host_frame(7, "OK|x"), "host->ctl seq=7 OK|x");
    }

    #[test]
    fn file_lines_carry_the_controllers_utc_stamp() {
        let at = |ms| UNIX_EPOCH + Duration::from_millis(ms);
        assert_eq!(record_line(at(1_793_536_496_789), "f"), "[bridge 2026-11-01 12:34:56.789Z] f");
        assert_eq!(record_line(at(951_868_799_001), "f"), "[bridge 2000-02-29 23:59:59.001Z] f");
        assert_eq!(record_line(UNIX_EPOCH, "f"), "[bridge 1970-01-01 00:00:00.000Z] f");
    }
}
