#![forbid(unsafe_code)]

//! Join-world reuse fingerprint decision and counters, ported from
//! `DedicatedServer/Authority/JoinWorldReuse.cs`. Cache ownership stays with the checkpoint driver.

use std::collections::HashMap;
use std::time::Duration;

pub const NO_SCOPE: &str = "-";
pub const NO_FINGERPRINT_YET: &str = "no-fingerprint-yet";
pub const SCOPE: &str = "scope";
pub const OBJECTS: &str = "objects";
pub const INVENTORIES: &str = "inventories";
pub const RELATIONSHIPS: &str = "relationships";
pub const ERROR: &str = "error";
pub const MISS_REASONS: [&str; 6] = [
    NO_FINGERPRINT_YET,
    SCOPE,
    OBJECTS,
    INVENTORIES,
    RELATIONSHIPS,
    ERROR,
];
pub const JOIN_SCAN_TIMEOUT: Duration = Duration::from_secs(3);

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct FingerprintPart {
    pub count: i32,
    pub hash: String,
}

#[derive(Clone, Debug, PartialEq)]
pub struct WorldFingerprint {
    pub scope: String,
    pub objects: FingerprintPart,
    pub inventories: FingerprintPart,
    pub relationships: FingerprintPart,
    pub scan_ms: f64,
}

impl WorldFingerprint {
    pub fn parse(report: Option<&str>) -> Result<Self, String> {
        let text = report.unwrap_or_default().trim();
        if text.is_empty() || text == "none" {
            return Err("no fingerprint taken".into());
        }
        if let Some(error) = text.strip_prefix("error=") {
            return Err(error.into());
        }

        // As in the oracle, keys are ordinal, values are not trimmed, and the last duplicate wins.
        let mut fields = HashMap::new();
        for field in text.split(';') {
            if let Some((key, value)) = field.split_once('=') {
                if !key.is_empty() {
                    fields.insert(key, value);
                }
            }
        }
        let part = |name| -> Option<FingerprintPart> {
            let (count, hash) = fields.get(name)?.split_once(':')?;
            // NumberStyles.None accepts neither a sign nor whitespace.
            if count.is_empty() || !count.bytes().all(|b| b.is_ascii_digit()) || hash.is_empty() {
                return None;
            }
            Some(FingerprintPart {
                count: count.parse().ok()?,
                hash: hash.into(),
            })
        };
        if let (Some(scope), Some(objects), Some(inventories), Some(relationships)) = (
            fields.get("scope").filter(|scope| !scope.is_empty()),
            part(OBJECTS),
            part(INVENTORIES),
            part(RELATIONSHIPS),
        ) {
            let scan_ms = fields
                .get("ms")
                .and_then(|value| {
                    let value = value.trim_matches(|c| matches!(c, ' ' | '\t' | '\n' | '\r' | '\x0b' | '\x0c'));
                    // Rust also accepts "inf"; invariant Double.TryParse only accepts "Infinity".
                    if ["inf", "+inf", "-inf"].iter().any(|s| value.eq_ignore_ascii_case(s)) {
                        None
                    } else {
                        value.parse().ok()
                    }
                })
                .unwrap_or(0.0);
            return Ok(Self {
                scope: (*scope).into(),
                objects,
                inventories,
                relationships,
                scan_ms,
            });
        }

        // The C# diagnostic is capped at 80 UTF-16 code units.
        let units: Vec<_> = text.encode_utf16().collect();
        let diagnostic = if units.len() > 80 {
            format!("{}...", String::from_utf16_lossy(&units[..80]))
        } else {
            text.into()
        };
        Err(format!("not a fingerprint: {diagnostic}"))
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Decision {
    pub reuse: bool,
    pub misses: Vec<String>,
    pub detail: String,
}

pub fn decide(
    at_save: Option<&WorldFingerprint>,
    at_join: Option<&WorldFingerprint>,
    scan_error: Option<&str>,
) -> Decision {
    let miss = |reason: &str, detail: String| Decision {
        reuse: false,
        misses: vec![reason.into()],
        detail,
    };
    let Some(at_save) = at_save else {
        return miss(NO_FINGERPRINT_YET, "the last save has no fingerprint".into());
    };
    let at_join = match (at_join, scan_error) {
        (Some(at_join), None) => at_join,
        _ => return miss(ERROR, format!("the join's scan failed: {}", scan_error.unwrap_or("no reply"))),
    };
    if at_join.scope == NO_SCOPE {
        return miss(SCOPE, "nothing claimed yet, so nothing to compare".into());
    }
    if at_join.scope != at_save.scope {
        return miss(SCOPE, format!("scope {} at the save, {} now", at_save.scope, at_join.scope));
    }

    let mut misses = Vec::new();
    let mut details = Vec::new();
    for (name, saved, now) in [
        (OBJECTS, &at_save.objects, &at_join.objects),
        (INVENTORIES, &at_save.inventories, &at_join.inventories),
        (RELATIONSHIPS, &at_save.relationships, &at_join.relationships),
    ] {
        if saved != now {
            misses.push(name.into());
            details.push(format!(
                "{name} {} -> {}{}",
                saved.count,
                now.count,
                if saved.count == now.count { " (same count, other entries)" } else { "" },
            ));
        }
    }
    Decision {
        reuse: misses.is_empty(),
        misses,
        detail: if details.is_empty() {
            "nothing a joiner needs changed since the last save".into()
        } else {
            details.join(", ")
        },
    }
}

#[derive(Clone, Debug, Default)]
pub struct JoinReuseCounters {
    hits: u32,
    misses: u32,
    saves_skipped: u32,
    seconds_saved: f64,
    last_scan_ms: Option<f64>,
    max_scan_ms: Option<f64>,
    last_save_ms: Option<f64>,
    max_save_ms: Option<f64>,
    last_save_for_estimate_ms: f64,
    misses_by: [u32; 6],
}

impl JoinReuseCounters {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn hits(&self) -> u32 {
        self.hits
    }

    pub fn misses(&self) -> u32 {
        self.misses
    }

    pub fn saves_skipped(&self) -> u32 {
        self.saves_skipped
    }

    pub fn seconds_saved(&self) -> f64 {
        self.seconds_saved
    }

    pub fn misses_by(&self, reason: &str) -> u32 {
        MISS_REASONS.iter().position(|r| *r == reason).map_or(0, |i| self.misses_by[i])
    }

    pub fn record_scan(&mut self, ms: f64) {
        self.last_scan_ms = Some(ms);
        self.max_scan_ms = Some(max_ms(self.max_scan_ms.unwrap_or(ms), ms));
    }

    pub fn record_save(&mut self, ms: f64, join_checkpoint: bool) {
        self.last_save_for_estimate_ms = ms;
        if join_checkpoint {
            self.last_save_ms = Some(ms);
            self.max_save_ms = Some(max_ms(self.max_save_ms.unwrap_or(ms), ms));
        }
    }

    pub fn record(&mut self, decision: &Decision) {
        if decision.reuse {
            self.hits += 1;
            self.saves_skipped += 1;
            self.seconds_saved += self.last_save_for_estimate_ms / 1000.0;
        } else {
            self.misses += 1;
            for reason in &decision.misses {
                if let Some(index) = MISS_REASONS.iter().position(|r| r == reason) {
                    self.misses_by[index] += 1;
                }
            }
        }
    }

    pub fn format(&self) -> String {
        let reasons = MISS_REASONS
            .iter()
            .zip(self.misses_by)
            .map(|(reason, count)| format!("{reason}={count}"))
            .collect::<Vec<_>>()
            .join(" ");
        format!(
            "hits={} misses={} ({reasons}) scan-ms last={} max={} save-ms last={} max={} saves-skipped={} seconds-saved={:.1}",
            self.hits,
            self.misses,
            self.last_scan_ms.map_or_else(|| "-".into(), |ms| format!("{ms:.1}")),
            self.max_scan_ms.map_or_else(|| "-".into(), |ms| format!("{ms:.1}")),
            self.last_save_ms.map_or_else(|| "-".into(), |ms| format!("{ms:.0}")),
            self.max_save_ms.map_or_else(|| "-".into(), |ms| format!("{ms:.0}")),
            self.saves_skipped,
            self.seconds_saved,
        )
    }
}

fn max_ms(a: f64, b: f64) -> f64 {
    // Unlike f64::max, Math.Max propagates NaN.
    if a.is_nan() || b.is_nan() { f64::NAN } else { a.max(b) }
}

pub fn join_reuse_line(decision: &Decision, note: &str, counters: &JoinReuseCounters) -> String {
    let outcome = if decision.reuse { "HIT".into() } else { format!("MISS ({})", decision.misses.join(", ")) };
    format!("[world] join reuse: {outcome}: {}; {note} | {}", decision.detail, counters.format())
}
