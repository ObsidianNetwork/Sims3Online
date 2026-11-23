#![forbid(unsafe_code)]
//! Claimed participant initialization. Queue receipts are not native creation ACKs.

use std::collections::{BTreeMap, BTreeSet};
use crate::{ConnId, model_cut::Session, snapshot::Sim, messages as m};

pub(crate) const DESCRIPTION: &str = "__ParticipantDescription";
pub(crate) const SPAWN: &str = "__ParticipantSpawn";
const MAX_CLAIMS: usize = 64;
const MAX_PAIRS: usize = 256;
pub(crate) const MAX_DESCRIPTION_BYTES: usize = m::PUBLIC_DESCRIPTION_MAX_RAW_BYTES;
const RETRY_MS: u64 = 1000;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct Stamp {
    world: u64,
    authority: u64,
    nonce: u64,
}
#[derive(Clone, Debug)]
pub(crate) struct Export {
    pub stamp: Stamp,
    pub conn: ConnId,
    pub sim: Sim,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Phase { Witness, Export, Description, Spawn, Ready }
struct Pair {
    session: Session,
    stamp: Stamp,
    sim: Option<Sim>,
    phase: Phase,
    retry_at: u64,
    token: u64,
    description_fields: Vec<String>,
    spawn_fields: Vec<String>,
}
#[derive(Default)]
pub(crate) struct Ledger {
    enabled: bool,
    world: u64,
    authority: u64,
    nonce: u64,
    claims: BTreeSet<u64>,
    pairs: BTreeMap<(ConnId, u64), Pair>,
    witness_retry_at: u64,
    pub errors: u64,
    pub stale: u64,
    pub queued: u64,
    last_error: String,
    identity_resets: BTreeSet<u64>,
    capacity_blocked: bool,
}
impl Ledger {
    /// Observed session/claim epochs invalidate equal-ID ABA, including prepared events.
    pub fn observe(&mut self, enabled: bool, world: u64, authority: u64,
        claims: &[u64], sessions: &[Session]) -> Vec<u64> {
        self.enabled = enabled;
        if !enabled {
            self.pairs.clear(); self.claims.clear(); self.identity_resets.clear(); self.capacity_blocked = false;
            return Vec::new();
        }
        // A rejected oversized input never allocates an unbounded identity set.
        let too_many_claims = claims.len() > MAX_CLAIMS;
        let claims: BTreeSet<_> = claims.iter().take(MAX_CLAIMS + 1).copied().filter(|id| *id != 0).collect();
        let world_changed = self.world != world;
        let changed = world_changed || self.authority != authority;
        let mut fresh: Vec<_> = if world_changed { claims.iter().copied().collect() }
            else { claims.difference(&self.claims).copied().collect() };
        if changed {
            // Successful creation is not an authority lease. An unrelated Join
            // must not reinitialize every already-created participant. Pending
            // IO/receipts still require the exact pre-request authority epoch.
            self.pairs.retain(|_, pair| !world_changed && pair.phase == Phase::Ready);
            self.witness_retry_at = 0;
        }
        fresh.extend(std::mem::take(&mut self.identity_resets));
        self.world = world;
        self.authority = authority;
        self.claims = claims;
        self.pairs.retain(|(conn, sim), pair| self.claims.contains(sim)
            && sessions.iter().any(|session| session.conn == *conn && *session == pair.session));
        if too_many_claims || self.claims.len().saturating_mul(sessions.len()) > MAX_PAIRS {
            if !self.capacity_blocked { self.fail("participant ledger capacity exceeded"); }
            self.capacity_blocked = true;
            return fresh;
        }
        self.capacity_blocked = false;
        for session in sessions {
            for sim in &self.claims {
                if self.pairs.contains_key(&(session.conn, *sim)) { continue; }
                self.nonce = self.nonce.checked_add(1).expect("participant lease exhausted");
                self.pairs.insert((session.conn, *sim), Pair { session: *session,
                    stamp: Stamp { world, authority, nonce: self.nonce }, sim: None,
                    phase: Phase::Witness, retry_at: 0, token: 0,
                    description_fields: Vec::new(), spawn_fields: Vec::new() });
            }
        }
        fresh
    }
    pub fn witness_stamp(&self, now: u64) -> Option<Stamp> {
        (self.enabled && now >= self.witness_retry_at && self.pairs.values().any(|p| p.phase == Phase::Witness))
            .then_some(Stamp { world: self.world, authority: self.authority, nonce: self.nonce })
    }
    pub fn current_stamp(&self) -> Stamp { Stamp { world: self.world, authority: self.authority, nonce: self.nonce } }
    pub fn witness(&mut self, stamp: Stamp, sims: &[Sim], now: u64) {
        if stamp.world != self.world || stamp.authority != self.authority || stamp.nonce != self.nonce {
            self.stale += 1; return;
        }
        self.witness_retry_at = now.saturating_add(RETRY_MS);
        let mut unique = BTreeSet::new();
        if sims.iter().any(|sim| !unique.insert(sim.object_id)) {
            self.fail("duplicate participant snapshot identity"); return;
        }
        for ((_, id), pair) in &mut self.pairs {
            if let Some(sim) = sims.iter().find(|sim| sim.object_id == *id && sim.description_id.is_some_and(|id| id != 0)
                && sim.position.iter().all(|value| value.is_finite())) {
                let replaced = pair.sim.as_ref().is_some_and(|prior| prior.description_id != sim.description_id);
                if replaced || pair.phase == Phase::Witness {
                    if replaced {
                        self.nonce = self.nonce.checked_add(1).expect("participant identity lease exhausted");
                        pair.stamp = Stamp { world: self.world, authority: self.authority, nonce: self.nonce };
                        pair.token = 0;
                        pair.description_fields.clear(); pair.spawn_fields.clear();
                        self.identity_resets.insert(*id);
                    }
                    pair.sim = Some(sim.clone());
                    pair.phase = Phase::Export;
                    pair.retry_at = 0;
                }
            }
        }
        if self.pairs.values().any(|pair| pair.phase == Phase::Witness) {
            self.fail("participant snapshot missing valid claimed identity");
        }
    }
    pub fn export(&self, now: u64) -> Option<Export> {
        // At most one encoded description is retained awaiting actor receipts.
        // This is a byte bound as well as a shared-native-slot fairness bound.
        if self.pairs.values().any(|pair| matches!(pair.phase, Phase::Description | Phase::Spawn)) { return None; }
        self.pairs.iter().find_map(|((conn, _), pair)| (pair.phase == Phase::Export && now >= pair.retry_at)
            .then(|| Export { stamp: pair.stamp, conn: *conn, sim: pair.sim.clone().expect("witnessed participant") }))
    }
    fn export_pair(&mut self, export: &Export) -> Option<&mut Pair> {
        self.pairs.get_mut(&(export.conn, export.sim.object_id)).filter(|pair| pair.stamp == export.stamp
            && pair.phase == Phase::Export && pair.sim.as_ref().and_then(|sim| sim.description_id) == export.sim.description_id)
    }
    pub fn prepared(&mut self, export: &Export, payload: &simlink_bridge::verbs::SimDescriptionPayload,
        now: u64, tick: u64) -> Vec<crate::routing::RoutedEvent> {
        if self.export_pair(export).is_none() { self.stale += 1; return Vec::new(); }
        let bytes = payload.property_stream.len().saturating_add(payload.res_key_table.len()).saturating_add(payload.object_id_table.len());
        if payload.desc != export.sim.description_id.unwrap_or(0) || bytes == 0 || bytes > MAX_DESCRIPTION_BYTES {
            self.failed_export(export, now, "invalid participant description identity/size"); return Vec::new();
        }
        let description = vec![payload.desc.to_string(), m::hex_encode_bytes(&payload.property_stream),
            m::hex_encode_bytes(&payload.res_key_table), m::hex_encode_bytes(&payload.object_id_table)];
        let spawn = vec![export.sim.object_id.to_string(), payload.desc.to_string(),
            m::enc_record(&export.sim.position.iter().map(f32::to_string).collect::<Vec<_>>())];
        for (kind, fields) in [("SimDescriptionData", &description), ("SimSpawned", &spawn)] {
            let event = m::Event { seq: u64::MAX, tick: u64::MAX, corr: u64::MAX, kind: kind.into(), fields: fields.clone() };
            if crate::wire_v1::encode_event(&event).is_err() {
                self.failed_export(export, now, "participant event exceeds public envelope"); return Vec::new();
            }
        }
        self.nonce = self.nonce.checked_add(1).expect("participant token exhausted");
        let token = self.nonce;
        let pair = self.export_pair(export).expect("validated pair");
        pair.phase = Phase::Description;
        pair.token = token;
        pair.description_fields = description.clone();
        pair.spawn_fields = spawn.clone();
        [(DESCRIPTION, description), (SPAWN, spawn)].into_iter().map(|(kind, mut fields)| {
            fields.push(token.to_string());
            crate::routing::RoutedEvent { event: m::Event { seq: 0, tick, corr: 0, kind: kind.into(), fields },
                owner: None, requester: Some(export.conn) }
        }).collect()
    }
    pub fn permits(&self, event: &crate::routing::RoutedEvent) -> bool {
        let Some(conn) = event.requester else { return false; };
        let Some(token) = event.event.fields.last().and_then(|field| field.parse::<u64>().ok()) else { return false; };
        self.pairs.iter().any(|((id, _), pair)| *id == conn && pair.token == token && match event.event.kind.as_str() {
            DESCRIPTION => pair.phase == Phase::Description && event.event.fields[..event.event.fields.len()-1] == pair.description_fields,
            SPAWN => pair.phase == Phase::Spawn && event.event.fields[..event.event.fields.len()-1] == pair.spawn_fields,
            _ => false,
        })
    }
    pub fn complete(&mut self, event: &crate::routing::RoutedEvent, success: bool, now: u64) {
        if !self.permits(event) { self.stale += 1; return; }
        let ready_token = event.event.fields.last().and_then(|v| v.parse::<u64>().ok()).unwrap();
        let pair = self.pairs.values_mut().find(|pair| pair.token == ready_token).expect("permitted participant token");
        if success {
            pair.phase = if event.event.kind == DESCRIPTION { Phase::Spawn } else { Phase::Ready };
            if pair.phase == Phase::Ready { self.queued += 1; pair.description_fields.clear(); pair.spawn_fields.clear(); }
        } else {
            pair.phase = Phase::Export;
            pair.retry_at = now.saturating_add(RETRY_MS);
            pair.description_fields.clear(); pair.spawn_fields.clear();
            self.fail("participant outbound queue rejected");
        }
    }
    pub fn failed_export(&mut self, export: &Export, now: u64, error: &str) {
        if let Some(pair) = self.export_pair(export) { pair.retry_at = now.saturating_add(RETRY_MS); self.fail(error); }
        else { self.stale += 1; }
    }
    pub fn failed_witness(&mut self, now: u64, error: &str) {
        self.witness_retry_at = now.saturating_add(RETRY_MS); self.fail(error);
    }
    pub fn ready(&self, conn: ConnId, sim: u64) -> bool {
        !self.enabled || !self.claims.contains(&sim)
            || self.pairs.get(&(conn, sim)).is_some_and(|pair| pair.phase == Phase::Ready)
    }
    pub fn recipient_ready(&self, conn: ConnId) -> bool { self.claims.iter().all(|sim| self.ready(conn, *sim)) }
    pub fn pending(&self) -> bool { self.capacity_blocked || self.pairs.values().any(|pair| pair.phase != Phase::Ready) }
    pub fn capacity_failed(&self) -> bool { self.capacity_blocked }
    fn fail(&mut self, error: &str) {
        self.errors += 1;
        self.last_error = m::hex_encode(&error.chars().take(256).collect::<String>());
    }
    pub fn report(&self) -> String {
        let pending = self.pairs.values().filter(|pair| pair.phase != Phase::Ready).count().max(usize::from(self.capacity_blocked));
        let stage = self.pairs.values().find(|pair| pair.phase != Phase::Ready).map_or(if self.capacity_blocked { "capacity" } else { "idle" }, |pair| match pair.phase {
            Phase::Witness => "witness", Phase::Export => "export", Phase::Description => "description_queue",
            Phase::Spawn => "spawn_queue", Phase::Ready => "idle" });
        format!("participant_pending={pending}|participant_stage={stage}|participant_queued={}|participant_errors={}|participant_stale={}|participant_last_error_hex={}",
            self.queued, self.errors, self.stale, if self.last_error.is_empty() { "-" } else { &self.last_error })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn sessions() -> Vec<Session> {
        let mut models = crate::model_cut::Ledger::default();
        models.observe(0, &[(1, 1), (2, 2)]);
        [ConnId(1), ConnId(2)].into_iter().map(|conn| models.session(conn).unwrap()).collect()
    }
    fn sim() -> Sim {
        Sim { object_id: 20, name: "participant".into(), household_id: 1, position: [1., 2., 3.],
            interaction_id: None, owning_player_id: Some(2), interaction_key: None, lot_id: Some(100),
            queue_count: None, posture_container: None, posture_type: None, description_id: Some(200) }
    }
    fn payload() -> simlink_bridge::verbs::SimDescriptionPayload {
        simlink_bridge::verbs::SimDescriptionPayload { desc: 200, property_stream: vec![0, 255], res_key_table: vec![2], object_id_table: vec![3] }
    }
    fn ledger() -> (Ledger, Vec<Session>) {
        let mut ledger = Ledger::default();
        let sessions = sessions();
        ledger.observe(true, 0, 0, &[20], &sessions);
        ledger.witness(ledger.current_stamp(), &[sim()], 0);
        (ledger, sessions)
    }
    #[test]
    fn description_and_spawn_receipts_are_ordered_exact_and_recipient_specific() {
        let (mut ledger, _) = ledger();
        let export = ledger.export(0).unwrap();
        let events = ledger.prepared(&export, &payload(), 0, 10);
        assert_eq!(events.len(), 2);
        assert!(ledger.permits(&events[0]));
        assert!(!ledger.permits(&events[1]));
        let mut forged = events[0].clone(); forged.requester = Some(ConnId(2));
        assert!(!ledger.permits(&forged));
        forged = events[0].clone(); forged.event.fields[0] = "201".into();
        assert!(!ledger.permits(&forged));
        ledger.complete(&events[0], true, 0);
        assert!(!ledger.ready(ConnId(1), 20));
        assert!(ledger.permits(&events[1]));
        ledger.complete(&events[1], true, 0);
        assert!(ledger.ready(ConnId(1), 20));
        assert!(!ledger.ready(ConnId(2), 20));
        let other = ledger.export(0).unwrap();
        assert_eq!(other.conn, ConnId(2));
        assert_eq!(ledger.queued, 1);
    }
    #[test]
    fn pending_export_and_prepared_events_cannot_cross_world_claim_or_session_aba() {
        for change in 0..3 {
            let (mut ledger, mut sessions) = ledger();
            let export = ledger.export(0).unwrap();
            let events = ledger.prepared(&export, &payload(), 0, 10);
            match change {
                0 => { ledger.observe(true, 1, 0, &[20], &sessions); }
                1 => { ledger.observe(true, 0, 1, &[20], &sessions); }
                _ => {
                    let mut models = crate::model_cut::Ledger::default();
                    models.observe(0, &[(1, 1), (2, 2)]);
                    models.invalidate(ConnId(1));
                    models.observe(0, &[(1, 1), (2, 2)]);
                    sessions[0] = models.session(ConnId(1)).unwrap();
                    ledger.observe(true, 0, 0, &[20], &sessions);
                }
            }
            assert!(!ledger.permits(&events[0]));
            assert!(ledger.prepared(&export, &payload(), 0, 10).is_empty());
            assert!(!ledger.ready(ConnId(1), 20));
        }
    }
    #[test]
    fn witness_absence_is_not_presence_or_despawn_and_retries_are_spaced() {
        let mut ledger = Ledger::default();
        ledger.observe(true, 0, 0, &[20], &sessions());
        let stamp = ledger.witness_stamp(0).unwrap();
        ledger.witness(stamp, &[], 0);
        assert!(ledger.export(0).is_none());
        assert!(ledger.witness_stamp(999).is_none());
        assert!(ledger.witness_stamp(1000).is_some());
        ledger.witness(stamp, &[sim()], 1000);
        let export = ledger.export(1000).unwrap();
        ledger.failed_export(&export, 1000, "native refused");
        let other = ledger.export(1000).expect("another recipient keeps its independent turn");
        assert_ne!(other.conn, export.conn);
        ledger.failed_export(&other, 1000, "native refused");
        assert!(ledger.export(1999).is_none());
        assert!(ledger.export(2000).is_some());
    }
    #[test]
    fn invalid_export_is_transactional_and_queue_failure_retries_only_its_pair() {
        let (mut ledger, _) = ledger();
        let export = ledger.export(0).unwrap();
        let mut oversized = payload(); oversized.property_stream = vec![255; MAX_DESCRIPTION_BYTES];
        assert!(ledger.prepared(&export, &oversized, 0, 10).is_empty());
        let next = ledger.export(0).unwrap();
        assert_eq!(next.conn, ConnId(2));
        let events = ledger.prepared(&next, &payload(), 0, 10);
        ledger.complete(&events[0], false, 0);
        assert!(!ledger.permits(&events[1]));
        assert!(ledger.export(999).is_none());
        assert!(ledger.export(1000).is_some());
        assert_eq!(ledger.errors, 2);
    }
    #[test]
    fn unrelated_authority_change_preserves_success_and_reported_identity_replacement_reinitializes() {
        let (mut ledger, sessions) = ledger();
        let export = ledger.export(0).unwrap();
        let events = ledger.prepared(&export, &payload(), 0, 10);
        ledger.complete(&events[0], true, 0);
        ledger.complete(&events[1], true, 0);
        assert!(ledger.observe(true, 0, 1, &[20], &sessions).is_empty());
        assert!(ledger.ready(ConnId(1), 20));
        assert!(!ledger.ready(ConnId(2), 20));
        let mut replacement = sim(); replacement.description_id = Some(201);
        ledger.witness(ledger.current_stamp(), &[replacement], 1);
        assert!(!ledger.ready(ConnId(1), 20));
        assert_eq!(ledger.observe(true, 0, 1, &[20], &sessions), [20]);
        assert!(ledger.export(1).is_some());
    }
    #[test]
    fn capacity_failure_remains_pending_when_all_preexisting_pairs_are_ready() {
        let mut models = crate::model_cut::Ledger::default();
        models.observe(0, &[(1, 1), (2, 2), (3, 3), (4, 4), (5, 5)]);
        let sessions: Vec<_> = (1..=5).map(|conn| models.session(ConnId(conn)).unwrap()).collect();
        let sims: Vec<_> = (20..84).map(|id| { let mut sim = sim(); sim.object_id = id; sim.description_id = Some(id + 100); sim }).collect();
        let claims: Vec<_> = sims.iter().map(|sim| sim.object_id).collect();
        let mut ledger = Ledger::default();
        ledger.observe(true, 0, 0, &claims, &sessions[..4]);
        ledger.witness(ledger.current_stamp(), &sims, 0);
        while let Some(export) = ledger.export(0) {
            let mut payload = payload(); payload.desc = export.sim.description_id.unwrap();
            let events = ledger.prepared(&export, &payload, 0, 10);
            ledger.complete(&events[0], true, 0); ledger.complete(&events[1], true, 0);
        }
        assert!(!ledger.pending());
        ledger.observe(true, 0, 0, &claims, &sessions);
        assert!(ledger.capacity_failed()); assert!(ledger.pending());
        assert!(ledger.report().contains("participant_stage=capacity"));
        assert!(!ledger.recipient_ready(ConnId(5)));
        assert_eq!(ledger.pairs.len(), MAX_PAIRS);
    }
}
