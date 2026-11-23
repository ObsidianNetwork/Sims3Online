#![forbid(unsafe_code)]
//! Sessions, claims, order and idempotency (§E).
//!
//! One [`Server`] owns every connection's session state: the per-connection `corr` order, the
//! per-client event `seq` (strictly increasing from 1 within an `epoch`), the 32-entry
//! `commandId` idempotency window (a replay returns the original reply byte for byte), the join /
//! supersede rule, and the [`IdMap`] that keeps the wire's u64 ids against the kernel's u32
//! `SimId`. A restart is a fresh `Server` with a new epoch.
//!
//! The authority gate runs first on every command path (AGENTS.md reviewers' rule 5, card 57
//! §D.1); the semantic adjudication is injected, so this module never touches a socket, a process
//! or the kernel's world. The live lane wires the real driver in behind the transport trait
//! (§J.6); the offline gates drive it with a closure.

use std::collections::{HashMap, VecDeque};

use crate::idmap::IdMap;
use crate::messages::{Command, COMMAND_ID_WINDOW};
use crate::wire_v1::UNJOINED_ALLOWED;
use crate::rejoin::{ClientEventRing, LeaseEndReason, ResumeOutcome};

/// A connection's identity on the public socket (§E: one session per connection).
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct ConnId(pub u64);

/// The reply a command produced, with the event `seq` it was issued under, kept so a replay is
/// byte-for-byte and causes no second effect (§D.1, §E).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CachedReply {
    /// The exact reply bytes already sent for this `commandId`.
    pub reply: Vec<u8>,
    /// The event `seq` in force when the original reply was produced.
    pub seq: u64,
}

/// The result of handling one command.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CommandOutcome {
    /// The reply bytes to write to the connection.
    pub reply: Vec<u8>,
    /// The per-client event `seq` this command was issued under.
    pub seq: u64,
    /// Whether this outcome was an idempotent replay of an earlier `commandId`.
    pub replayed: bool,
}

/// One connection's session state (§E).
#[derive(Clone, Debug)]
pub struct Connection {
    /// Private lease credential. Never include in diagnostics.
    resume_token: Option<String>,
    token_epoch: u32,
    pub ring: ClientEventRing,
    pub next_bulk: u64,
    /// Set only by the separate loopback listener, never by public hello caps.
    pub admin: bool,
    pub cadence: crate::cadence::PlayerCadence,
    /// This connection's id.
    pub conn_id: ConnId,
    /// Stable requester identity across warm socket rebinds, so outstanding
    /// asynchronous world files and pick outcomes follow the resumed lease.
    pub event_id: ConnId,
    /// The player id `JoinSession` named, if any.
    pub player: Option<i32>,
    /// The last `corr` accepted for a new command; a new command must exceed it (§D.1).
    pub last_corr: u64,
    /// The next per-client event `seq`, from 1 within the epoch (§E).
    pub next_seq: u64,
    /// The `commandId` idempotency window, oldest first, capped at [`COMMAND_ID_WINDOW`].
    window: VecDeque<(u64, CachedReply)>,
    /// Set when a later join of the same player id superseded this connection (§E).
    superseded: bool,
}

impl Connection {
    fn new(conn_id: ConnId, epoch: u32) -> Self {
        Self {
            resume_token: None,
            token_epoch: epoch,
            ring: ClientEventRing::new(epoch),
            next_bulk: 1,
            admin: false,
            cadence: crate::cadence::PlayerCadence::default(),
            conn_id,
            event_id: conn_id,
            player: None,
            last_corr: 0,
            next_seq: 1,
            window: VecDeque::new(),
            superseded: false,
        }
    }

    fn cached(&self, command_id: u64) -> Option<&CachedReply> {
        self.window.iter().find(|(id, _)| *id == command_id).map(|(_, c)| c)
    }

    fn remember(&mut self, command_id: u64, reply: CachedReply) {
        if self.window.len() == COMMAND_ID_WINDOW {
            self.window.pop_front();
        }
        self.window.push_back((command_id, reply));
    }

    /// Whether this connection was superseded by a later join of its player id (§E).
    #[must_use]
    pub fn is_superseded(&self) -> bool {
        self.superseded
    }
}

/// The server crate's ownership of sessions, order, idempotency and the id map (§E).
#[derive(Debug, Default)]
pub struct Server {
    /// The u32 stream epoch, new per server process start (§C.2, §E).
    pub epoch: u32,
    pub joins_completed: u64,
    conns: HashMap<ConnId, Connection>,
    /// The live connection for each joined player id, so a rejoin supersedes the old one (§E).
    player_active: HashMap<i32, ConnId>,
    ids: IdMap,
}

impl Server {
    /// A server at the given epoch. A restart constructs a new one with a higher epoch (§E).
    #[must_use]
    pub fn new(epoch: u32) -> Self {
        Self {
            epoch,
            joins_completed: 0,
            conns: HashMap::new(),
            player_active: HashMap::new(),
            ids: IdMap::new(),
        }
    }

    /// Register a new connection and return its id.
    pub fn connect(&mut self, conn_id: ConnId) {
        self.conns.entry(conn_id).or_insert_with(|| Connection::new(conn_id, self.epoch));
    }

    /// Close a connection, dropping it from the active-player table (§E: a disconnect ends the
    /// connection half of the lease; the resumable window is card 59's).
    pub fn disconnect(&mut self, conn_id: ConnId) {
        if let Some(conn) = self.conns.remove(&conn_id) {
            if let Some(player) = conn.player {
                if self.player_active.get(&player) == Some(&conn_id) {
                    self.player_active.remove(&player);
                }
            }
        }
    }

    /// End the joined lease without replacing the still-open connection.
    /// Corr, seq and the commandId window survive leave/rejoin (§E).
    pub fn leave(&mut self, conn_id: ConnId) {
        if let Some(conn) = self.conns.get_mut(&conn_id) {
            conn.ring.end(LeaseEndReason::ExplicitLeave);
            conn.resume_token = None;
            conn.cadence = crate::cadence::PlayerCadence::default();
            if let Some(player) = conn.player.take() {
                if self.player_active.get(&player) == Some(&conn_id) {
                    self.player_active.remove(&player);
                }
            }
        }
    }

    /// Join a connection as `player`. A later join of the same id supersedes the earlier
    /// connection, which then gets `Superseded` on its next command (§E). Returns the superseded
    /// connection id, if any.
    pub fn join(&mut self, conn_id: ConnId, player: i32) -> Option<ConnId> {
        self.joins_completed += 1;
        let previous = self.player_active.insert(player, conn_id);
        if let Some(old) = previous {
            if old != conn_id {
                if let Some(conn) = self.conns.get_mut(&old) {
                    conn.superseded = true;
                    conn.resume_token = None;
                    conn.ring.end(LeaseEndReason::Superseded);
                }
            }
        }
        if let Some(conn) = self.conns.get_mut(&conn_id) {
            conn.player = Some(player);
            conn.ring = ClientEventRing::new(self.epoch);
            conn.token_epoch = self.epoch;
        }
        previous.filter(|old| *old != conn_id)
    }

    /// The live connection for a player id, if one is joined (§E).
    #[must_use]
    pub fn active_connection(&self, player: i32) -> Option<ConnId> {
        self.player_active.get(&player).copied()
    }

    pub fn event_connection(&self, id: ConnId) -> Option<ConnId> {
        if self.conns.contains_key(&id) { return Some(id); }
        self.conns.values().find(|conn| conn.event_id == id).map(|conn| conn.conn_id)
    }

    /// Resolve possession before considering the cursor; active sockets cannot be taken over.
    pub fn disconnected_session(&self, token: &str) -> Option<ConnId> {
        if !crate::wire_v1::valid_resume_token(token) { return None; }
        self.conns.values().find(|conn| {
            conn.token_epoch == self.epoch && conn.player.is_some() && !conn.superseded
                && conn.ring.disconnected()
                && conn.resume_token.as_ref().is_some_and(|expected| {
                    expected.bytes().zip(token.bytes()).fold(0u8, |diff, (a, b)| diff | (a ^ b)) == 0
                })
        }).map(|conn| conn.conn_id)
    }

    /// Attach the token to the cached join reply as well as the first delivery.
    /// A lost ACK retry must return the identical credential, without rotating it.
    pub fn issue_resume_token(&mut self, id: ConnId, command_id: u64, token: String) -> Vec<u8> {
        let conn = self.conns.get_mut(&id).expect("joined connection");
        conn.resume_token = Some(token.clone());
        let (_, cached) = conn.window.iter_mut().find(|(cmd, _)| *cmd == command_id).expect("join reply");
        cached.reply.extend_from_slice(format!("|resume_token={token}").as_bytes());
        cached.reply.clone()
    }

    /// Called only after token possession selected a disconnected lease.
    pub fn resume_identified_session(&mut self, old: ConnId, new: ConnId, epoch: u32, seq: u64, now: u64) -> ResumeOutcome {
        let outcome = self.conns.get_mut(&old).expect("resume candidate").ring.try_resume(self.epoch, epoch, seq, now);
        if matches!(outcome, ResumeOutcome::Ok { .. }) {
            let mut conn = self.conns.remove(&old).expect("resume candidate");
            conn.conn_id = new;
            self.player_active.insert(conn.player.expect("joined candidate"), new);
            self.conns.insert(new, conn);
        }
        outcome
    }

    pub fn expired_sessions(&self, now: u64) -> Vec<(ConnId, i32, LeaseEndReason)> {
        self.joined_connections().into_iter().filter_map(|id| {
            let conn = &self.conns[&id];
            if !conn.ring.disconnected() { return None; }
            Some((id, conn.player?, conn.ring.check_lease_expired(self.epoch, now)?))
        }).collect()
    }

    pub fn set_admin(&mut self, id: ConnId) {
        if let Some(conn) = self.conns.get_mut(&id) { conn.admin = true; }
    }

    pub fn joined_connections(&self) -> Vec<ConnId> {
        let mut result: Vec<_> = self.player_active.values().copied()
            .filter(|id| self.conns.get(id).is_some_and(|c| !c.admin && !c.superseded)).collect();
        result.sort();
        result
    }

    pub fn connection_mut(&mut self, id: ConnId) -> Option<&mut Connection> { self.conns.get_mut(&id) }

    /// The id map, for the front door to map wire ids to kernel ids and back (§E).
    pub fn ids(&mut self) -> &mut IdMap {
        &mut self.ids
    }

    /// The next per-client event `seq`, bumping the counter (§E: strictly increasing within an
    /// epoch). `None` for an unknown connection, or once the counter is at its ceiling.
    pub fn next_event_seq(&mut self, conn_id: ConnId) -> Option<u64> {
        let conn = self.conns.get_mut(&conn_id)?;
        let seq = conn.next_seq;
        // §E requires a strictly increasing seq. A u64 seq starting at 1 cannot reach u64::MAX in
        // any reachable run (~1.8e19 events for one client in one epoch), but we refuse to hand a
        // duplicate: `checked_add` stops here (`None`) at the ceiling instead of `saturating_add`,
        // which would silently reissue u64::MAX forever and break the order (N10). The serve loop
        // (a later card 57 step) ends the epoch once this returns `None`; the value never wraps.
        conn.next_seq = seq.checked_add(1)?;
        Some(seq)
    }

    /// Handle one command on a connection (§E, §D.1).
    ///
    /// The order is: supersede check, then the authority gate (first on every path), then the
    /// `commandId` idempotency window (a replay returns the original reply byte for byte and
    /// causes no second effect), then a new command (its `corr` must exceed the connection's last,
    /// the injected `adjudicate` produces the reply, the event `seq` advances and both are
    /// remembered). A refusal short-circuits before adjudication.
    pub fn handle_command<F>(
        &mut self,
        conn_id: ConnId,
        cmd: &Command,
        mut adjudicate: F,
    ) -> Result<CommandOutcome, Refusal>
    where
        F: FnMut(&Command) -> Vec<u8>,
    {
        let conn = self
            .conns
            .get(&conn_id)
            .ok_or_else(|| Refusal::new(cmd.corr, crate::messages::RefusalCode::NotJoined, &cmd.verb))?;
        if conn.superseded {
            return Err(Refusal::new(
                cmd.corr,
                crate::messages::RefusalCode::Superseded,
                &cmd.verb,
            ));
        }

        // Authority gate, first on every command path (§D.1, §E). The oracle is
        // `ClientListenerServer.CheckCommandAuthority` (`:1516-1532`): a joined connection speaks
        // only for its own player; an unjoined connection may send `JoinSession` and the three
        // read-only requests, whatever its address. §E closes the old loopback act-for-any bypass,
        // so a loopback peer is treated exactly like a remote one (there is no `is_loopback`
        // branch here) — F3.
        match conn.player {
            _ if conn.admin => {}
            Some(joined) => {
                if cmd.player_id != joined {
                    return Err(Refusal::new(
                        cmd.corr,
                        crate::messages::RefusalCode::WrongPlayer,
                        &cmd.verb,
                    ));
                }
            }
            None => {
                if !UNJOINED_ALLOWED.contains(&cmd.verb.as_str()) {
                    return Err(Refusal::new(
                        cmd.corr,
                        crate::messages::RefusalCode::NotJoined,
                        &cmd.verb,
                    ));
                }
            }
        }

        // Idempotency: a replayed commandId returns the original reply, byte for byte, and no
        // second effect (§D.1).
        if let Some(cached) = conn.cached(cmd.command_id).cloned() {
            return Ok(CommandOutcome {
                reply: cached.reply,
                seq: cached.seq,
                replayed: true,
            });
        }

        // A new command's corr must strictly increase (§D.1; the kernel drops a lower reissue).
        if cmd.corr <= conn.last_corr {
            return Err(Refusal::new(
                cmd.corr,
                crate::messages::RefusalCode::BadLine,
                &cmd.verb,
            ));
        }

        // Reserve the seq before adjudicating: exhaustion must not cause an unrecorded
        // effect or reissue the ceiling value (the same rule as next_event_seq).
        if conn.next_seq.checked_add(1).is_none() {
            return Err(Refusal::new(cmd.corr, crate::messages::RefusalCode::BadLine, &cmd.verb));
        }
        // Adjudicate (injected) and remember under the current event seq.
        let reply = adjudicate(cmd);
        let seq = self
            .conns
            .get_mut(&conn_id)
            .map(|c| {
                let s = c.next_seq;
                // §E: seq strictly increases. `checked_add` keeps the counter at its u64::MAX
                // ceiling rather than wrapping (a documented boundary; N10) — reaching the ceiling
                // is unreachable (a u64 seq from 1 needs ~1.8e19 events per client per epoch), so
                // no real run ever sees the stuck value; the serve loop ends the epoch there.
                c.next_seq = s.checked_add(1).expect("seq reserved before adjudication");
                c.last_corr = cmd.corr;
                c.remember(
                    cmd.command_id,
                    CachedReply {
                        reply: reply.clone(),
                        seq: s,
                    },
                );
                s
            })
            .ok_or_else(|| {
                Refusal::new(
                    cmd.corr,
                    crate::messages::RefusalCode::NotJoined,
                    &cmd.verb,
                )
            })?;
        Ok(CommandOutcome {
            reply,
            seq,
            replayed: false,
        })
    }

    /// The connection record, for inspection in tests.
    #[must_use]
    pub fn connection(&self, conn_id: ConnId) -> Option<&Connection> {
        self.conns.get(&conn_id)
    }
}

/// A named refusal produced by the session, carrying the corr and verb for the `ERR` frame (§D.1).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Refusal {
    /// The refused command's corr.
    pub corr: u64,
    /// The refusal code (§D.2).
    pub code: crate::messages::RefusalCode,
    /// The refused verb.
    pub verb: String,
}

impl Refusal {
    fn new(corr: u64, code: crate::messages::RefusalCode, verb: &str) -> Self {
        Self {
            corr,
            code,
            verb: verb.to_string(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sequence_exhaustion_never_dispatches_an_unrecordable_effect() {
        let mut server = Server::new(1);
        let id = ConnId(1);
        server.connect(id);
        server.join(id, 7);
        server.conns.get_mut(&id).unwrap().next_seq = u64::MAX;
        assert_eq!(server.next_event_seq(id), None);
        let command = Command {
            corr: 1, command_id: 1, player_id: 7, verb: "ClaimSim".into(), args: vec![],
        };
        assert_eq!(server.handle_command(id, &command, |_| panic!("effect after seq exhaustion"))
            .unwrap_err().code, crate::messages::RefusalCode::BadLine);
        assert_eq!(server.next_event_seq(id), None);
        assert_eq!(server.connection(id).unwrap().last_corr, 0);
    }
}
