#![forbid(unsafe_code)]
//! The transport behind the session (§J.6).
//!
//! One module. It defines the [`GameDriver`] seam the front door drives the server game through;
//! tests drive it with a [`FakeDriver`]. [`BridgeGameDriver`] is re-exported from
//! `bridge_driver`, which owns typed class-B and in-process class-K translation.
//! `serve` owns socket readiness and explicit live construction; offline drivers
//! open no process handle. The public socket speaks §C-§F; the game side uses this trait.

use std::collections::VecDeque;

use crate::messages::{Ack, Command};
use crate::wire_v1;
pub use crate::bridge_driver::BridgeGameDriver;

/// A transport failure: the bridge could not carry the verb, or no reply arrived.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct DriverError(pub String);

impl core::fmt::Display for DriverError {
    fn fmt(&self, f: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
        write!(f, "game driver error: {}", self.0)
    }
}

impl std::error::Error for DriverError {}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum StreamRequest {
    Snapshot(i64),
    Recorder(Vec<String>),
    DiagState,
}

#[derive(Clone, Debug)]
pub enum StreamReply {
    Snapshot(Option<crate::snapshot::Snapshot>),
    Recorder(String),
    DiagState(Option<String>),
}

/// The seam between the session and the server game (§J.6).
///
/// A driver turns one command into the reply bytes the server writes back, and hands over any
/// event lines the game pushed since the last call. The front door injects a `&mut dyn GameDriver`
/// into [`crate::session::Server::handle_command`], so ordering and idempotency stay independent
/// of how the game is driven.
pub trait GameDriver {
    /// Drive the game with one command and return the reply bytes for its `corr`.
    fn adjudicate(&mut self, cmd: &Command) -> Result<Vec<u8>, DriverError>;

    /// Drain the event lines the game produced since the last call (replies, pushes, presentation).
    fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, DriverError>;

    /// One outstanding stream/baseline request. Live implementations issue once
    /// and return None; their poll never waits for the native reply. The default
    /// keeps deterministic, immediately-answering drivers compatible.
    fn start_stream_request(&mut self, request: &StreamRequest) -> Result<Option<StreamReply>, DriverError> {
        Ok(Some(match request {
            StreamRequest::Snapshot(since) => StreamReply::Snapshot(self.snapshot(*since)?),
            StreamRequest::Recorder(args) => StreamReply::Recorder(self.recorder(args)?),
            StreamRequest::DiagState => StreamReply::DiagState(self.diag_state()?),
        }))
    }
    fn poll_stream_request(&mut self) -> Result<Option<StreamReply>, DriverError> { Ok(None) }
    fn stream_request_pending(&self) -> bool { false }
    fn baseline_pending(&self, _conn: crate::ConnId) -> bool { false }
    fn baseline_failed(&self, _conn: crate::ConnId) -> bool { false }
    fn baseline_closed(&mut self, _conn: crate::ConnId) {}

    fn adjudicate_for(&mut self, _conn: crate::ConnId, cmd: &Command) -> Result<Vec<u8>, DriverError> {
        self.adjudicate(cmd)
    }
    fn poll_routed_events(&mut self) -> Result<Vec<crate::routing::RoutedEvent>, DriverError> {
        self.poll_event_lines()?.into_iter().map(|line| {
            let event = crate::wire_v1::split(&line).and_then(|fields| crate::wire_v1::decode_event(&fields))
                .map_err(|e| DriverError(e.to_string()))?;
            Ok(crate::routing::RoutedEvent { event, owner: None, requester: None })
        }).collect()
    }
    /// Resolve accepted work after a terminal bridge failure without more IO.
    fn terminal_events(&mut self, _error: &DriverError) -> Vec<crate::routing::RoutedEvent> { Vec::new() }
    /// Publish collected events even when the current bridge poll failed.
    fn take_pending_events(&mut self) -> Vec<crate::routing::RoutedEvent> { Vec::new() }
    fn admin_request(&mut self, _request: &crate::admin::Request) -> Result<String, DriverError> {
        Err(DriverError("bridge unavailable".into()))
    }
    fn admin_upload(&mut self, _blob: &str, _bytes: &[u8]) -> Result<String, DriverError> {
        Err(DriverError("bridge unavailable".into()))
    }
    fn snapshot(&mut self, _since: i64) -> Result<Option<crate::snapshot::Snapshot>, DriverError> { Ok(None) }
    fn recorder(&mut self, _args: &[String]) -> Result<String, DriverError> { Ok(String::new()) }
    fn presentation_pages(&mut self) -> Result<Vec<String>, DriverError> { Ok(Vec::new()) }
    /// Pending until the stream has drained old presentation queues and sequenced the barrier.
    /// Reading it does not consume it: a failed recorder drain must retry the same barrier.
    fn take_epoch_announcement(&mut self) -> Option<u64> { None }
    /// Emit the epoch through the driver after the stream's barrier, then mark it announced.
    fn announced(&mut self, _epoch: u64) {}
    fn ring_available(&self) -> bool { false }
    fn claimed_sims(&self) -> Vec<u64> { Vec::new() }
    fn owner(&self, _sim: u64) -> Option<i32> { None }
    fn event_drop_state(&self) -> String { "-".into() }
    /// Current authoritative routing/road counters, without bridge IO or mutation.
    /// Drivers without a kernel world leave this unmeasured.
    fn routing_state(&self) -> Option<String> { None }
    fn diag_state(&mut self) -> Result<Option<String>, DriverError> { Ok(None) }

    /// End the player's connection lease on supersede or an explicit leave (§E).
    fn release_player(&mut self, _player: i32) {}
}

/// A deterministic in-process driver for tests and the offline gates. It never touches a socket or
/// a process (§J.6).
#[derive(Clone, Debug, Default)]
pub struct FakeDriver {
    /// Reply bytes handed out, in order, one per [`GameDriver::adjudicate`].
    queued_replies: VecDeque<Vec<u8>>,
    /// Event lines handed out by [`GameDriver::poll_event_lines`], in order.
    queued_events: VecDeque<Vec<u8>>,
    /// Every command line the fake received, in order, for assertions.
    pub sent: Vec<Vec<u8>>,
}

impl FakeDriver {
    /// An empty fake that answers with a generated `ACK|<corr>|<Verb>|ok`.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Queue one exact reply to hand back on the next [`GameDriver::adjudicate`].
    pub fn queue_reply(&mut self, reply: impl Into<Vec<u8>>) {
        self.queued_replies.push_back(reply.into());
    }

    /// Queue event lines to hand back from [`GameDriver::poll_event_lines`].
    pub fn queue_event(&mut self, line: impl Into<Vec<u8>>) {
        self.queued_events.push_back(line.into());
    }

    /// How many commands the fake has received.
    #[must_use]
    pub fn sent_count(&self) -> usize {
        self.sent.len()
    }
}

impl GameDriver for FakeDriver {
    fn adjudicate(&mut self, cmd: &Command) -> Result<Vec<u8>, DriverError> {
        let line = wire_v1::encode_command(cmd).map_err(|e| DriverError(e.to_string()))?;
        self.sent.push(line.into_bytes());
        if let Some(reply) = self.queued_replies.pop_front() {
            return Ok(reply);
        }
        let ack = Ack {
            corr: cmd.corr,
            verb: cmd.verb.clone(),
            fields: vec!["ok".to_string()],
        };
        wire_v1::encode_ack(&ack)
            .map(|s| s.into_bytes())
            .map_err(|e| DriverError(e.to_string()))
    }

    fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, DriverError> {
        Ok(self.queued_events.drain(..).collect())
    }
}

#[cfg(test)]
mod tests {
    use std::path::Path;

    fn has_raw_send_line(source: &str) -> bool {
        let code = simlink_bridge::scorecard::code_only(source);
        code.split(|c: char| !c.is_alphanumeric() && c != '_')
            .any(|token| token == "send_line")
    }

    #[test]
    fn comment_stripper_catches_code_send_line_and_ignores_prose_comments() {
        assert!(has_raw_send_line("self.driver.send_line(line)"));
        assert!(!has_raw_send_line("// relaying via send_line\n"));
        assert!(!has_raw_send_line("/* relaying via send_line */"));
        assert!(!has_raw_send_line("/* outer /* send_line */ send_line */"));
        assert!(!has_raw_send_line("let note = \"send_line\";"));
        assert!(!has_raw_send_line("let note = \"escaped \\\"send_line\\\"\";"));
        assert!(!has_raw_send_line("let note = r#\"quoted \"send_line\"\"#;"));
        assert!(!has_raw_send_line("send_line_count send_lineage"));
        assert!(has_raw_send_line("// send_line\nself.driver.send_line(line)"));
        assert!(has_raw_send_line("/* send_line */ self.driver.send_line(line)"));
        assert!(has_raw_send_line("let note = \"send_line\"; self.driver.send_line(line)"));
    }

    #[test]
    fn src_never_calls_raw_send_line() {
        let dir = Path::new(env!("CARGO_MANIFEST_DIR")).join("src");
        for entry in std::fs::read_dir(dir).unwrap() {
            let path = entry.unwrap().path();
            if path.extension().and_then(|ext| ext.to_str()) == Some("rs") {
                let source = std::fs::read_to_string(&path).unwrap();
                assert!(!has_raw_send_line(&source), "{} contains raw send_line code", path.display());
            }
        }
    }
}
