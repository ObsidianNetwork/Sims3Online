#![forbid(unsafe_code)]
//! The server's pushed presentation pages, read from the SimLink ring (lane 6).
//!
//! Oracle: `DedicatedServer/HostBridge/SimLinkShadowSession.cs` (`PageLane`, `MaxQueuedPages`, `DrainNow`,
//! `DiscardQueuedPages`) and its start in `AuthoritativeSession` (`SIMSONLINE_SIMLINK_SERVER_RING=1`).
//!
//! [`RingPages`] wraps a [`simlink_bridge::ring::RingReader`] and keeps the lane-6 queue. It runs on the actor
//! thread with the rest of the live handles: the actor pumps about every 10 ms and each pump calls
//! [`PageSource::take_pages`], which reads the ring's atomics and copies out what was published, so there is no
//! reader thread and nothing is shared. A `.data` event cannot be registered with the Mio poll, so wake-ups are
//! the pump cadence, not the event.
//!
//! Everything about *when* to push, how long silence lasts and when to re-arm stays in [`crate::stream::Ladder`];
//! this module only turns the ring into pages and reports whether the ring can be used at all.

use std::collections::VecDeque;
use std::time::Duration;

use simlink_bridge::ring::{Record, RingError, RingReader, Section};

/// `SimLinkShadowSession.PageLane`: the lane the mod pushes dump pages on.
pub const PAGE_LANE: u16 = 6;
/// Finite page budget. Overflow poisons this source; active pages are never silently evicted.
pub const MAX_QUEUED_PAGES: usize = 4096;
/// `SimLinkShadowReader.RequestRegistration`'s wait in `SimLinkShadowSession.TryStartAsync`.
pub const REGISTRATION_TIMEOUT: Duration = Duration::from_secs(5);

/// What the driver needs from a ring: pages in, stale pages out, and whether it still works.
pub trait PageSource {
    /// Every lane-6 page published since the last call, oldest first. Drains the ring first.
    fn take_pages(&mut self) -> Vec<String>;
    /// `DiscardQueuedPages`: drains the ring, drops every queued page and returns how many.
    fn discard_pages(&mut self) -> usize;
    /// False once the ring was found corrupt: nothing more will arrive, and a push must not be re-armed.
    fn available(&self) -> bool;
    /// `key=value|...` counters for the serve report.
    fn report(&self) -> String;
    /// Persistent failure; availability alone cannot distinguish attach absence from active loss.
    fn failure(&self) -> Option<&str> { None }
}

/// The lane-6 page queue over a ring reader.
pub struct RingPages<S: Section> {
    reader: RingReader<S>,
    queue: VecDeque<String>,
    received: u64,
    discarded: u64,
    mirrored_ignored: u64,
    /// Pages dropped straight from the ring by the current discard, not yet returned to the caller.
    discarded_unqueued: usize,
    failure: Option<String>,
}

impl<S: Section> RingPages<S> {
    pub fn new(reader: RingReader<S>) -> Self {
        Self { reader, queue: VecDeque::new(), received: 0, discarded: 0, mirrored_ignored: 0, discarded_unqueued: 0, failure: None }
    }

    /// Pages read from the ring (every lane-6 record, before any cap or discard).
    pub fn pages_received(&self) -> u64 {
        self.received
    }

    /// Pages dropped by the queue cap or `discard_pages`.
    pub fn pages_discarded(&self) -> u64 {
        self.discarded
    }

    /// Why the ring was abandoned, if it was.
    pub fn failure(&self) -> Option<&str> {
        self.failure.as_deref()
    }

    pub fn reader(&self) -> &RingReader<S> {
        &self.reader
    }

    /// `keep` false is the discard path: stale pages are counted and dropped as they are read, so a backlog a
    /// previous owner left behind (a push still on after its controller died) cannot overflow the live budget.
    fn drain(&mut self, keep: bool) {
        if self.failure.is_some() {
            return;
        }
        if self.reader.section().ring_size()>4*1024*1024 {
            self.failure=Some("presentation ring exceeds installed native 4MiB geometry".into());
            return;
        }
        let mut records: Vec<Record> = Vec::new();
        match self.reader.drain(&mut records) {
            Ok(_) => {
                for record in records {
                    if record.lane != PAGE_LANE {
                        // Lanes 1 and 2 are the shadow comparison's mirror; nothing here consumes them.
                        self.mirrored_ignored += 1;
                        continue;
                    }
                    self.received += 1;
                    if self.failure.is_some() {
                        continue;
                    }
                    if !keep {
                        self.discarded += 1;
                        self.discarded_unqueued += 1;
                        continue;
                    }
                    self.queue.push_back(record.text_lossy());
                    if self.queue.len() > MAX_QUEUED_PAGES {
                        self.failure = Some("presentation ring queue capacity exceeded".into());
                        self.queue.clear();
                    }
                }
            }
            Err(error) => {
                eprintln!("serve_report|ring_failed|reason={error}");
                self.failure = Some(error.to_string());
            }
        }
    }
}

impl<S: Section> PageSource for RingPages<S> {
    fn take_pages(&mut self) -> Vec<String> {
        self.drain(true);
        self.queue.drain(..).collect()
    }

    fn discard_pages(&mut self) -> usize {
        self.drain(false);
        let dropped = self.queue.len();
        self.queue.clear();
        self.discarded += dropped as u64;
        dropped + std::mem::take(&mut self.discarded_unqueued)
    }

    fn available(&self) -> bool {
        self.failure.is_none()
    }

    fn failure(&self) -> Option<&str> { self.failure.as_deref() }

    fn report(&self) -> String {
        let telemetry = self.reader.telemetry();
        format!(
            "ring_pages_received={}|ring_pages_discarded={}|ring_mirrored_ignored={}|native_published={}|native_dropped={}|native_contended={}|native_bad_args={}|native_version={}|native_registration_state={}",
            self.received, self.discarded, self.mirrored_ignored, telemetry.published, telemetry.dropped,
            telemetry.contended, telemetry.bad_args, telemetry.native_version, self.reader.section().state()
        )
    }
}

/// `SimLinkShadowSession.TryStartAsync`: opens nothing itself (the caller passes the open result), runs the
/// registration handshake and, when the mirror is requested, sends `sidecar-on` through `sidecar_on` and requires
/// `sidecar=on` in the reply. `Err` is the reason the ring is unavailable; the driver then stays on dumps.
///
/// `mirror` follows the oracle: `sidecar-on` (the mod mirroring every recorder line onto lanes 1/2) is sent only
/// when `SIMSONLINE_SIMLINK_SHADOW=1`. Pages on lane 6 come from `push-on`, which the stream's ladder sends.
pub fn start<S: Section>(
    opened: Result<RingReader<S>, RingError>,
    registration_timeout: Duration,
    mirror: bool,
    sidecar_on: impl FnOnce() -> Result<String, String>,
) -> Result<RingPages<S>, String> {
    let reader = opened.map_err(|error| error.to_string())?;
    if !reader.request_registration(registration_timeout) {
        return Err(format!("registration_failed: state={} error={}", reader.state(), reader.error()));
    }
    if mirror {
        match sidecar_on() {
            Ok(reply) if reply.contains("sidecar=on") => {}
            Ok(reply) => return Err(format!("sidecar_on_refused: {reply}")),
            Err(error) => return Err(format!("sidecar_on_refused: {error}")),
        }
    }
    Ok(RingPages::new(reader))
}


/// Cooperative Rust-reader admission. Held handle survives until the source is dropped.
/// Older C# readers do not honor this lease: deployment must separately exclude them.
pub struct NativeReaderLease { _file: std::fs::File }
impl NativeReaderLease {
    #[cfg(windows)]
    pub fn acquire(pid: u32) -> Result<Self, String> {
        use std::os::windows::fs::OpenOptionsExt;
        let path = std::env::temp_dir().join(format!("SimsOnline-server-ring-{pid}.lease"));
        let file = std::fs::OpenOptions::new().read(true).write(true).create(true)
            .truncate(false).share_mode(0).open(path).map_err(|e|format!("server ring reader already owned or inaccessible: {e}"))?;
        Ok(Self { _file: file })
    }
    #[cfg(not(windows))]
    pub fn acquire(_pid: u32) -> Result<Self, String> { Err("native ring requires Windows".into()) }
}
#[cfg(all(test,windows))]
mod lease_tests {
    use super::*;
    #[test]
    fn cooperative_lease_is_exclusive_until_driver_owner_is_dropped(){
        let pid=std::process::id();let owner=NativeReaderLease::acquire(pid).unwrap();
        assert!(NativeReaderLease::acquire(pid).is_err());drop(owner);
        assert!(NativeReaderLease::acquire(pid).is_ok());
    }
}
