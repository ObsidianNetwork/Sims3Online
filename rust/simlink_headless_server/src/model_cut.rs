#![forbid(unsafe_code)]
//! Per-recipient model cuts. Private receipts never enter public snapshot framing.

use crate::{messages, ConnId};
use sha2::{Digest, Sha256};
use std::collections::{HashMap, HashSet};

pub(crate) const EVENT: &str = "__SnapshotModel";

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct Session {
    pub conn: ConnId,
    pub player: i32,
    revision: u64,
}

#[cfg(test)]
mod tests {
    use super::*;
    fn ledger() -> (Ledger, Session) {
        let mut ledger = Ledger::default();
        ledger.observe(1, &[(7, 2)]);
        let session = ledger.session(ConnId(7)).unwrap();
        (ledger, session)
    }
    #[test]
    fn receipts_bind_exact_session_world_tick_flags_payload_and_cut_generation() {
        let (mut ledger, session) = ledger();
        let fields = ledger
            .prepare(session, true, 10, None, "full".into())
            .unwrap();
        assert!(ledger.unready(session.conn));
        assert!(ledger.permits(&fields, session.conn, 10));
        for index in 0..3 {
            let mut corrupt = fields.clone();
            corrupt[index].push('x');
            assert!(!ledger.permits(&corrupt, session.conn, 10));
        }
        assert!(!ledger.permits(&fields, ConnId(8), 10));
        assert!(!ledger.permits(&fields, session.conn, 11));
        ledger.mark_unready(session.conn);
        assert!(ledger.complete(&fields, session.conn, 10, true).is_none());
        assert!(ledger.unready(session.conn));
        let fresh = ledger
            .prepare(session, true, 11, None, "full2".into())
            .unwrap();
        ledger.complete(&fresh, session.conn, 11, true).unwrap();
        assert!(!ledger.unready(session.conn));
        ledger.mark_unready(session.conn);
        let delta = ledger
            .prepare(session, false, 12, None, "delta".into())
            .unwrap();
        ledger.complete(&delta, session.conn, 12, true).unwrap();
        assert!(
            ledger.unready(session.conn),
            "a delta cannot repair a missed complete model"
        );
    }
    #[test]
    fn delayed_read_cannot_bind_same_id_rejoin_or_new_world_and_failure_is_recipient_local() {
        let (mut ledger, old) = ledger();
        ledger.invalidate(old.conn);
        ledger.observe(1, &[(7, 2), (8, 3)]);
        assert!(ledger.prepare(old, true, 10, None, "old".into()).is_none());
        let current = ledger.session(old.conn).unwrap();
        let fields = ledger
            .prepare(current, true, 11, None, "new".into())
            .unwrap();
        ledger.observe(2, &[(7, 2), (8, 3)]);
        assert!(!ledger.permits(&fields, old.conn, 11));
        assert!(ledger.prepare(current, true, 12, None, "stale world".into()).is_none());
        let current = ledger.session(old.conn).unwrap();
        let fields = ledger
            .prepare(current, true, 12, Some(4), "world2".into())
            .unwrap();
        let completion = ledger.complete(&fields, old.conn, 12, false).unwrap();
        assert_eq!(completion.previous_resync, Some(4));
        assert!(ledger.unready(old.conn));
        let other = ledger.session(ConnId(8)).unwrap();
        let fields = ledger
            .prepare(other, true, 12, None, "other".into())
            .unwrap();
        ledger.complete(&fields, other.conn, 12, true).unwrap();
        assert!(!ledger.unready(other.conn));
        assert!(ledger.unready(old.conn));
    }
    #[test]
    fn rejected_current_delta_marks_only_its_current_recipient_unready() {
        let (mut ledger, session) = ledger();
        let full = ledger
            .prepare(session, true, 10, None, "full".into())
            .unwrap();
        ledger.complete(&full, session.conn, 10, true).unwrap();
        let mut delta = ledger
            .prepare(session, false, 11, None, "delta".into())
            .unwrap();
        delta[0] = "mismatch".into();
        assert!(!ledger.permits(&delta, session.conn, 11));
        ledger.reject(&delta);
        assert!(ledger.unready(session.conn));
        assert!(ledger.pending.is_empty());
        let next = ledger
            .prepare(session, true, 12, None, "repair".into())
            .unwrap();
        ledger.complete(&next, session.conn, 12, true).unwrap();
        ledger.reject(&delta);
        assert!(
            !ledger.unready(session.conn),
            "old rejects cannot taint a newer completed cut"
        );
    }
}
struct Proof {
    session: Session,
    world: u64,
    generation: u64,
    payload: [u8; 32],
    forced: bool,
    tick: i64,
    previous_resync: Option<i64>,
}
pub(crate) struct Completion {
    pub conn: ConnId,
    pub forced: bool,
    pub tick: i64,
    pub previous_resync: Option<i64>,
}
#[derive(Default)]
pub(crate) struct Ledger {
    world: u64,
    nonce: u64,
    sessions: HashMap<ConnId, Session>,
    generations: HashMap<ConnId, u64>,
    unready: HashSet<ConnId>,
    pending: HashMap<u64, Proof>,
    pub queued: u64,
    pub rejected: u64,
}
impl Ledger {
    fn next(&mut self) -> Option<u64> {
        let Some(next) = self.nonce.checked_add(1) else {
            self.rejected = self.rejected.saturating_add(1);
            return None;
        };
        self.nonce = next;
        Some(next)
    }
    pub fn invalidate(&mut self, conn: ConnId) {
        self.sessions.remove(&conn);
        self.generations.remove(&conn);
        self.unready.remove(&conn);
        self.pending.retain(|_, proof| proof.session.conn != conn);
    }
    pub fn observe(&mut self, world: u64, recipients: &[(u64, i32)]) {
        if world != self.world {
            self.world = world;
            self.pending.clear();
            // Native requests capture Session before IO. A new world must not
            // reuse that proof even when all public IDs remain identical.
            self.sessions.clear();
            self.generations.clear();
            self.unready.clear();
        }
        let stale: Vec<_> = self
            .sessions
            .iter()
            .filter_map(|(conn, session)| {
                (!recipients.contains(&(conn.0, session.player))).then_some(*conn)
            })
            .collect();
        for conn in stale {
            self.invalidate(conn);
        }
        for (conn, player) in recipients {
            let conn = ConnId(*conn);
            if !self.sessions.contains_key(&conn) {
                if let Some(revision) = self.next() {
                    self.sessions.insert(
                        conn,
                        Session {
                            conn,
                            player: *player,
                            revision,
                        },
                    );
                    self.generations.insert(conn, revision);
                    // Initial model ownership remains with the existing native
                    // Join/baseline contract. Only a missed cadence cut needs
                    // recovery; do not introduce extra startup full snapshots.
                }
            }
        }
    }
    pub fn session(&self, conn: ConnId) -> Option<Session> {
        self.sessions.get(&conn).copied()
    }
    pub fn current(&self, session: Session) -> bool {
        self.session(session.conn) == Some(session)
    }
    pub fn unready(&self, conn: ConnId) -> bool {
        !self.sessions.contains_key(&conn) || self.unready.contains(&conn)
    }
    pub fn mark_unready(&mut self, conn: ConnId) {
        self.unready.insert(conn);
        if let Some(generation) = self.next() {
            self.generations.insert(conn, generation);
        } else {
            self.generations.remove(&conn);
        }
    }
    pub fn prepare(
        &mut self,
        session: Session,
        forced: bool,
        tick: i64,
        previous_resync: Option<i64>,
        body: String,
    ) -> Option<Vec<String>> {
        if !self.current(session) {
            self.rejected += 1;
            return None;
        }
        if forced {
            self.mark_unready(session.conn);
        }
        let generation = *self.generations.get(&session.conn)?;
        let token = self.next()?;
        // The actor consumes a pump before another cadence can prepare output.
        // If another caller never consumes its receipts, keep only its newest cut.
        self.pending
            .retain(|_, proof| proof.session.conn != session.conn);
        self.pending.insert(
            token,
            Proof {
                session,
                world: self.world,
                generation,
                payload: Sha256::digest(body.as_bytes()).into(),
                forced,
                tick,
                previous_resync,
            },
        );
        Some(vec![
            body,
            messages::enc_bool(forced).into(),
            token.to_string(),
        ])
    }
    fn proof(&self, fields: &[String], conn: ConnId, tick: u64) -> Option<&Proof> {
        if fields.len() != 3 {
            return None;
        }
        let proof = self.pending.get(&fields[2].parse::<u64>().ok()?)?;
        (proof.session.conn == conn
            && self.current(proof.session)
            && proof.world == self.world
            && self.generations.get(&conn) == Some(&proof.generation)
            && proof.tick.max(0) as u64 == tick
            && messages::enc_bool(proof.forced) == fields[1]
            && proof.payload == <[u8; 32]>::from(Sha256::digest(fields[0].as_bytes())))
        .then_some(proof)
    }
    pub fn permits(&self, fields: &[String], conn: ConnId, tick: u64) -> bool {
        self.proof(fields, conn, tick).is_some()
    }
    pub fn complete(
        &mut self,
        fields: &[String],
        conn: ConnId,
        tick: u64,
        success: bool,
    ) -> Option<Completion> {
        self.proof(fields, conn, tick)?;
        let proof = self.pending.remove(&fields[2].parse::<u64>().ok()?)?;
        if self.generations.get(&conn) != Some(&proof.generation) {
            self.rejected += 1;
            return None;
        }
        if success {
            self.queued += 1;
            if proof.forced {
                self.unready.remove(&conn);
            }
        } else {
            self.mark_unready(conn);
        }
        Some(Completion {
            conn,
            forced: proof.forced,
            tick: proof.tick,
            previous_resync: proof.previous_resync,
        })
    }
    pub fn report(&self) -> String {
        format!(
            "model_state|unready={}|pending={}|queued={}|rejected={}",
            self.unready.len(),
            self.pending.len(),
            self.queued,
            self.rejected
        )
    }
    pub fn reject(&mut self, fields: &[String]) -> Option<Completion> {
        self.rejected += 1;
        if let Some(proof) = fields
            .get(2)
            .and_then(|token| token.parse::<u64>().ok())
            .and_then(|token| self.pending.remove(&token))
        {
            // A rejected current cut means this recipient missed model data.
            // A stale receipt cannot invalidate a later session/world/cut.
            if self.current(proof.session)
                && proof.world == self.world
                && self.generations.get(&proof.session.conn) == Some(&proof.generation)
            {
                self.mark_unready(proof.session.conn);
                return Some(Completion {
                    conn: proof.session.conn,
                    forced: proof.forced,
                    tick: proof.tick,
                    previous_resync: proof.previous_resync,
                });
            }
        }
        None
    }
}
