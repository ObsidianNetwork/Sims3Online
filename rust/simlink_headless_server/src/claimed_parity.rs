#![forbid(unsafe_code)]
//! Periodic claimed-Sim (`FullPuppetFallback`) motive, details, and household funds parity.
//!
//! Runs strictly non-blocking on idle pump turns behind the `Stream` snapshot/join slot.
//! Notice `BridgeGameDriver` refuses all `SubmitLotExport` (`'R'`) client-native commands and emits
//! `LotLocalAuthorityModeChanged(..., FullPuppetFallback)` on every join, so every claimed Sim on
//! `BridgeGameDriver` operates under `FullPuppetFallback`.

use crate::{messages as m, routing::RoutedEvent, DriverError};
use std::collections::{BTreeSet, HashMap, HashSet, VecDeque};

/// `SimStateWire.MotiveNames` order, emitted lowercase to match `AuthoritativeSession.TrackedMotives`.
pub(crate) const MOTIVE_NAMES: [&str; 6] =
    ["hunger", "energy", "bladder", "social", "hygiene", "fun"];

pub(crate) const MOTIVE_INTERVAL: u8 = 5;
pub(crate) const DETAILS_INTERVAL: u8 = 3;
pub(crate) const DETAILS_RESEND_INTERVAL: u8 = 10;
pub(crate) const MOTIVE_DIFF_THRESHOLD: f32 = 0.5;
const MAX_REPORT_BYTES: usize = 64 * 1024;

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct SimDetailsSample {
    pub sim: u64,
    pub career_guid: u64,
    pub career_level: i32,
    pub career_branch: String,
    pub funds: i32,
    pub skills: String,
}

impl SimDetailsSample {
    fn to_event(&self, tick: u64) -> m::Event {
        m::Event {
            seq: 0,
            tick,
            corr: 0,
            kind: "SimDetailsChanged".into(),
            fields: vec![
                self.sim.to_string(),
                self.career_guid.to_string(),
                self.career_level.to_string(),
                m::hex_encode(&self.career_branch),
                self.funds.to_string(),
                m::hex_encode(&self.skills),
            ],
        }
    }
}

pub(crate) fn parse_sim_details(
    report: &str,
    requested: &[u64],
) -> Result<Vec<SimDetailsSample>, DriverError> {
    let bad = || DriverError("sim details report malformed".into());
    if report.len() > MAX_REPORT_BYTES || requested.len() > crate::visible_state::PROBE_BATCH {
        return Err(bad());
    }
    let trimmed = report.trim();
    if trimmed.is_empty() || trimmed == "-" {
        return Ok(Vec::new());
    }
    let mut seen = BTreeSet::new();
    let mut samples = Vec::new();
    for entry in trimmed.split(';') {
        let parts: Vec<_> = entry.split(':').collect();
        if parts.len() != 6 {
            return Err(bad());
        }
        let sim = parts[0].parse::<u64>().map_err(|_| bad())?;
        if sim == 0 || !requested.contains(&sim) || !seen.insert(sim) {
            return Err(bad());
        }
        let career_guid = parts[1].parse::<u64>().map_err(|_| bad())?;
        let career_level = parts[2].parse::<i32>().map_err(|_| bad())?;
        let funds = parts[4].parse::<i32>().map_err(|_| bad())?;
        if parts[3].is_empty() || parts[5].is_empty() {
            return Err(bad());
        }
        samples.push(SimDetailsSample {
            sim,
            career_guid,
            career_level,
            career_branch: parts[3].into(),
            funds,
            skills: parts[5].into(),
        });
    }
    Ok(samples)
}

pub(crate) fn parse_household_cash(
    report: &str,
    expected_household: u64,
) -> Result<(u64, i32), DriverError> {
    let bad = || DriverError("household cash report malformed".into());
    if report.len() > MAX_REPORT_BYTES || expected_household == 0 {
        return Err(bad());
    }
    let mut household = None;
    let mut funds = None;
    for field in report.trim().split(';') {
        let Some((key, val)) = field.split_once('=') else {
            continue;
        };
        match key {
            "household" => {
                if household.is_some() {
                    return Err(bad());
                }
                household = Some(val.parse::<u64>().map_err(|_| bad())?);
            }
            "funds" => {
                if funds.is_some() {
                    return Err(bad());
                }
                funds = Some(val.parse::<i32>().map_err(|_| bad())?);
            }
            _ => {}
        }
    }
    match (household, funds) {
        (Some(id), Some(amount)) if id == expected_household && id != 0 => Ok((id, amount)),
        _ => Err(bad()),
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) enum ParityJob {
    SimState {
        world: u64,
        authority: u64,
        sims: Vec<u64>,
    },
    SimDetails {
        world: u64,
        authority: u64,
        sims: Vec<u64>,
        resend: bool,
    },
    HouseholdCash {
        world: u64,
        authority: u64,
        household: u64,
    },
}

impl ParityJob {
    fn generation(&self) -> (u64, u64) {
        match self {
            Self::SimState { world, authority, .. }
            | Self::SimDetails { world, authority, .. }
            | Self::HouseholdCash { world, authority, .. } => (*world, *authority),
        }
    }
}

#[derive(Default)]
pub(crate) struct ClaimedParity {
    world: u64,
    authority: u64,
    sims: Vec<u64>,
    active_sims: HashSet<u64>,
    owners: Vec<(u64, u32)>,
    households: Vec<u64>,
    active_households: HashSet<u64>,
    next_cadence_ms: Option<u64>,
    motive_cycles: u8,
    details_cycles: u8,
    details_resend_counter: u8,
    jobs: VecDeque<ParityJob>,
    last_motives: HashMap<(u64, usize), f32>,
    last_details: HashMap<u64, SimDetailsSample>,
    last_funds: HashMap<u64, i32>,
}

impl ClaimedParity {
    pub fn observe(
        &mut self,
        world: u64,
        authority: u64,
        sims: &[u64],
        owners: &[(u64, u32)],
        households: &[u64],
    ) {
        if self.world != world {
            self.world = world;
            self.authority = authority;
            self.jobs.clear();
            self.last_motives.clear();
            self.last_details.clear();
            self.last_funds.clear();
            self.details_resend_counter = 0;
        } else if self.authority != authority || self.owners.as_slice() != owners {
            let old_owners: HashMap<u64, u32> = self.owners.iter().copied().collect();
            self.authority = authority;
            self.jobs.clear();
            self.last_motives.clear();
            self.last_funds.clear();
            let owner_map: HashMap<u64, u32> = owners.iter().copied().collect();
            self.last_details.retain(|sim, _| {
                owner_map.get(sim).is_some_and(|owner| old_owners.get(sim) == Some(owner))
            });
        }
        if self.sims.as_slice() != sims {
            self.sims = sims.to_vec();
            self.active_sims = sims.iter().copied().collect();
            self.last_motives.retain(|(sim, _), _| self.active_sims.contains(sim));
            self.last_details.retain(|sim, _| self.active_sims.contains(sim));
        }
        self.owners = owners.to_vec();
        if self.households.as_slice() != households {
            self.households = households.to_vec();
            self.active_households = households.iter().copied().collect();
            self.last_funds.retain(|hh, _| self.active_households.contains(hh));
        }
    }

    pub fn step_cadence(&mut self, now_ms: u64, interval_ms: f64) {
        if !interval_ms.is_finite() || interval_ms <= 0.0 {
            return;
        }
        let step_ms = (interval_ms.round() as u64).max(1);
        let due = match self.next_cadence_ms {
            Some(at) if now_ms >= at => true,
            Some(_) => return,
            None => {
                self.next_cadence_ms = Some(now_ms.saturating_add(step_ms));
                return;
            }
        };
        if !due {
            return;
        }
        self.next_cadence_ms = Some(now_ms.saturating_add(step_ms));
        if self.sims.is_empty() && self.households.is_empty() {
            self.motive_cycles = 0;
            self.details_cycles = 0;
            return;
        }
        if !self.jobs.is_empty() {
            return;
        }
        self.details_cycles = self.details_cycles.saturating_add(1);
        if self.details_cycles >= DETAILS_INTERVAL {
            self.details_cycles = 0;
            if !self.sims.is_empty() {
                self.details_resend_counter = self.details_resend_counter.saturating_add(1);
                let resend = self.details_resend_counter >= DETAILS_RESEND_INTERVAL;
                if resend {
                    self.details_resend_counter = 0;
                }
                for chunk in self.sims.chunks(crate::visible_state::PROBE_BATCH) {
                    self.jobs.push_back(ParityJob::SimDetails {
                        world: self.world,
                        authority: self.authority,
                        sims: chunk.to_vec(),
                        resend,
                    });
                }
            }
        }
        self.motive_cycles = self.motive_cycles.saturating_add(1);
        if self.motive_cycles >= MOTIVE_INTERVAL {
            self.motive_cycles = 0;
            for chunk in self.sims.chunks(crate::visible_state::PROBE_BATCH) {
                self.jobs.push_back(ParityJob::SimState {
                    world: self.world,
                    authority: self.authority,
                    sims: chunk.to_vec(),
                });
            }
            for &household in &self.households {
                self.jobs.push_back(ParityJob::HouseholdCash {
                    world: self.world,
                    authority: self.authority,
                    household,
                });
            }
        }
    }

    pub fn next_job(&mut self) -> Option<ParityJob> {
        self.jobs.pop_front()
    }

    pub fn accept(
        &mut self,
        job: &ParityJob,
        report: &str,
        tick: u64,
    ) -> Result<Vec<RoutedEvent>, DriverError> {
        if job.generation() != (self.world, self.authority) {
            return Ok(Vec::new());
        }
        let mut out = Vec::new();
        match job {
            ParityJob::SimState { sims, .. } => {
                for sample in crate::visible_state::parse(report, sims)? {
                    if !self.active_sims.contains(&sample.id) {
                        continue;
                    }
                    for (index, raw_value) in sample.motives().into_iter().enumerate() {
                        let key = (sample.id, index);
                        let changed = self
                            .last_motives
                            .get(&key)
                            .is_none_or(|prev| (*prev - raw_value).abs() > MOTIVE_DIFF_THRESHOLD);
                        if !changed {
                            continue;
                        }
                        self.last_motives.insert(key, raw_value);
                        let value = if raw_value == 0.0 { 0.0 } else { raw_value };
                        out.push(RoutedEvent {
                            event: m::Event {
                                seq: 0,
                                tick,
                                corr: 0,
                                kind: "SimMotiveChanged".into(),
                                fields: vec![
                                    sample.id.to_string(),
                                    m::hex_encode(MOTIVE_NAMES[index]),
                                    value.to_string(),
                                ],
                            },
                            owner: None,
                            requester: None,
                        });
                    }
                }
            }
            ParityJob::SimDetails { sims, resend, .. } => {
                for sample in parse_sim_details(report, sims)? {
                    if !self.active_sims.contains(&sample.sim) {
                        continue;
                    }
                    if *resend || self.last_details.get(&sample.sim) != Some(&sample) {
                        out.push(RoutedEvent {
                            event: sample.to_event(tick),
                            owner: None,
                            requester: None,
                        });
                        self.last_details.insert(sample.sim, sample);
                    }
                }
            }
            ParityJob::HouseholdCash { household, .. } => {
                let (hh, funds) = parse_household_cash(report, *household)?;
                if !self.active_households.contains(&hh) {
                    return Ok(Vec::new());
                }
                if self.last_funds.get(&hh) != Some(&funds) {
                    self.last_funds.insert(hh, funds);
                    out.push(RoutedEvent {
                        event: m::Event {
                            seq: 0,
                            tick,
                            corr: 0,
                            kind: "HouseholdFundsChanged".into(),
                            fields: vec![hh.to_string(), funds.to_string()],
                        },
                        owner: None,
                        requester: None,
                    });
                }
            }
        }
        Ok(out)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn advance_until_job(parity: &mut ClaimedParity, now: &mut u64) -> ParityJob {
        for _ in 0..20 {
            *now += 100;
            parity.step_cadence(*now, 100.0);
            if let Some(job) = parity.next_job() {
                return job;
            }
        }
        panic!("cadence failed to schedule a parity job");
    }

    #[test]
    fn motives_honor_threshold_and_wire_names_in_sim_state_order() {
        let mut parity = ClaimedParity::default();
        parity.observe(0, 1, &[55], &[(55, 1)], &[44]);
        let mut now = 0;
        let state_job = loop {
            let job = advance_until_job(&mut parity, &mut now);
            if matches!(job, ParityJob::SimState { .. }) {
                break job;
            }
            if matches!(job, ParityJob::SimDetails { .. }) {
                parity.accept(&job, "-", 10).unwrap();
            }
        };
        let initial = parity
            .accept(&state_job, "55:-0.00,10.00,20.00,30.00,40.00,50.00:-", 11)
            .unwrap();
        assert_eq!(initial.len(), 6);
        assert_eq!(initial[0].event.kind, "SimMotiveChanged");
        assert_eq!(
            initial[0].event.fields,
            ["55", &m::hex_encode("hunger"), "0"]
        );
        assert_eq!(
            initial[2].event.fields,
            ["55", &m::hex_encode("bladder"), "20"]
        );
        assert_eq!(
            initial[5].event.fields,
            ["55", &m::hex_encode("fun"), "50"]
        );

        let subtle = parity
            .accept(&state_job, "55:0.49,10.50,20.00,30.51,40.00,49.49:-", 12)
            .unwrap();
        assert_eq!(subtle.len(), 2);
        assert_eq!(
            subtle[0].event.fields,
            ["55", &m::hex_encode("social"), "30.51"]
        );
        assert_eq!(
            subtle[1].event.fields,
            ["55", &m::hex_encode("fun"), "49.49"]
        );
    }

    #[test]
    fn details_diff_and_resend_every_tenth_probe_with_hex_tokens() {
        let mut parity = ClaimedParity::default();
        parity.observe(0, 1, &[55], &[(55, 1)], &[]);
        let mut now = 0;
        let report = "55:0:0:-:500:-";
        let mut seen = Vec::new();
        for _ in 0..10 {
            let job = loop {
                let job = advance_until_job(&mut parity, &mut now);
                if matches!(job, ParityJob::SimDetails { .. }) {
                    break job;
                }
                if matches!(job, ParityJob::SimState { .. }) {
                    parity.accept(&job, "-", now).unwrap();
                }
            };
            seen.push(parity.accept(&job, report, now).unwrap().len());
        }
        assert_eq!(seen, vec![1, 0, 0, 0, 0, 0, 0, 0, 0, 1]);

        let changed_job = ParityJob::SimDetails {
            world: 0,
            authority: 1,
            sims: vec![55],
            resend: false,
        };
        let events = parity
            .accept(&changed_job, "55:123:2:Culinary:450:1=3", 99)
            .unwrap();
        assert_eq!(events.len(), 1);
        assert_eq!(events[0].event.kind, "SimDetailsChanged");
        assert_eq!(
            events[0].event.fields,
            [
                "55",
                "123",
                "2",
                &m::hex_encode("Culinary"),
                "450",
                &m::hex_encode("1=3")
            ]
        );
        assert!(parity
            .accept(&changed_job, "55:123:2:Culinary:450:1=3", 100)
            .unwrap()
            .is_empty());
    }

    #[test]
    fn household_cash_diffs_and_rejects_mismatched_or_malformed() {
        let mut parity = ClaimedParity::default();
        parity.observe(0, 1, &[55], &[(55, 1)], &[44]);
        let job = ParityJob::HouseholdCash {
            world: 0,
            authority: 1,
            household: 44,
        };
        let first = parity.accept(&job, "household=44;funds=1250", 10).unwrap();
        assert_eq!(first.len(), 1);
        assert_eq!(first[0].event.kind, "HouseholdFundsChanged");
        assert_eq!(first[0].event.fields, ["44", "1250"]);
        assert!(parity
            .accept(&job, "household=44;funds=1250", 11)
            .unwrap()
            .is_empty());
        let updated = parity.accept(&job, "household=44;funds=900", 12).unwrap();
        assert_eq!(updated.len(), 1);
        assert_eq!(updated[0].event.fields, ["44", "900"]);
        assert!(parity
            .accept(&job, "household=99;funds=900", 13)
            .is_err());
        assert!(parity.accept(&job, "garbage", 14).is_err());
    }

    #[test]
    fn authority_and_world_cuts_drop_stale_in_flight_replies() {
        let mut parity = ClaimedParity::default();
        parity.observe(1, 1, &[55], &[(55, 1)], &[44]);
        let stale_state = ParityJob::SimState {
            world: 1,
            authority: 1,
            sims: vec![55],
        };
        parity.observe(1, 2, &[55], &[(55, 1)], &[44]);
        assert!(parity
            .accept(&stale_state, "55:1,2,3,4,5,6:-", 10)
            .unwrap()
            .is_empty());
        let stale_details = ParityJob::SimDetails {
            world: 1,
            authority: 2,
            sims: vec![55],
            resend: false,
        };
        parity.observe(2, 2, &[55], &[(55, 1)], &[44]);
        assert!(parity
            .accept(&stale_details, "55:0:0:-:500:-", 11)
            .unwrap()
            .is_empty());
    }
}
