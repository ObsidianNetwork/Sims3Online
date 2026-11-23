#![forbid(unsafe_code)]
//! Card 19 · two-machine WAN jitter, out-of-order buffering and fast
//! reconnect.
//!
//! Loopback never reorders; a real WAN pair does. The client-side reorder
//! buffer holds a bounded gap (in **frames**, not bytes) and NACKs it on a
//! cooldown; the server-side ring buffer answers those NACKs with a selective
//! replay — never a `.sims3` checkpoint save (the stall; card 11).
//! The `HELLO` gate refuses a client that would silently run unpatched (the
//! playbook's bare-`boot-client2` lesson) with a named diagnostic instead of
//! a connection reset.

use std::collections::{BTreeMap, VecDeque};

use crate::KernelError;

/// A 30 Hz stream: 32 frames is roughly one second of held reordering
/// (card 19 §B). This budget and the barrier window (card 16) are
/// separate and must not be conflated (§F).
pub const REORDER_WINDOW_FRAMES: u32 = 32;
/// The server's replay window, ~68 s at 30 Hz (§B).
pub const RING_BUFFER_FRAMES: u32 = 2048;
/// 200 ms before a gap is re-requested (§B.1: one NACK per cooldown).
pub const GAP_NACK_COOLDOWN_TICKS: u64 = 6;
/// Bridge protocol spoken as of card 06 §D (`SET_WORLD_TIME` era).
pub const BRIDGE_PROTOCOL_VERSION: u16 = 17;
/// SimLink native version (tester-kit.md; `SimLink.asi` witness).
pub const SIMLINK_NATIVE_VERSION: u16 = 15;
/// The tester kit's public NDJSON TCP port.
pub const DEFAULT_WAN_PORT: u16 = 34019;

/// The card 10 frame identity `(stream_id, epoch, seq)` the reorder buffer
/// and ring buffer key on. An epoch bump (controller restart) makes old
/// rows non-comparable, so a seq reset reads as a new stream, not a rewind.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct StreamFrameRef {
    pub stream_id: u32,
    pub epoch: u32,
    pub seq: u64,
}

/// What the client did with one arriving frame (§B).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum WanIngest {
    Buffered,
    Delivered,
    Duplicate,
    OutOfWindow,
}

/// The client-side reorder buffer. Holds arriving frames until a gap is
/// filled by a selective replay; a frame beyond the window force-flushes the
/// older gaps (the reconnect path uses the ring buffer, not this one).
#[derive(Clone, Debug, Default)]
pub struct WanJitterBuffer {
    pub window: BTreeMap<StreamFrameRef, Vec<u8>>,
    pub delivered: u64,
    pub gaps: Vec<u64>,
    pub last_nack_at: u64,
    pub reorder_hits: u64,
    pub reorder_misses: u64,
    pub nacks_sent: u64,
}

impl WanJitterBuffer {
    pub fn ingest(
        &mut self,
        rf: StreamFrameRef,
        payload: Vec<u8>,
        now_tick: u64,
    ) -> WanIngest {
        if rf.seq <= self.delivered || self.window.contains_key(&rf) {
            return WanIngest::Duplicate;
        }
        if rf.seq > self.delivered + u64::from(REORDER_WINDOW_FRAMES) {
            self.reorder_misses += 1;
            self.advance_past_gaps(now_tick);
            return WanIngest::OutOfWindow;
        }
        self.window.insert(rf, payload);
        // Drain the contiguous run at the cursor.
        let mut outcome = WanIngest::Buffered;
        loop {
            let next = StreamFrameRef { seq: self.delivered + 1, ..rf };
            if self.window.remove(&next).is_some() {
                self.delivered = next.seq;
                self.gaps.retain(|g| *g != next.seq);
                outcome = WanIngest::Delivered;
                self.reorder_hits += 1;
            } else {
                break;
            }
        }
        // One NACK per cooldown window for any gap still held (§B.1).
        if !self.window.is_empty()
            && now_tick.saturating_sub(self.last_nack_at) >= GAP_NACK_COOLDOWN_TICKS
        {
            self.last_nack_at = now_tick;
            self.nacks_sent += 1;
            let missing = self.delivered + 1;
            if !self.gaps.contains(&missing) {
                self.gaps.push(missing);
            }
        }
        outcome
    }

    /// A frame too far ahead to be held: drop the older gaps and clear the
    /// window (the card's §B force-flush; a reconnect uses `ServerRingBuffer`).
    fn advance_past_gaps(&mut self, _now: u64) {
        self.window.clear();
        self.gaps.clear();
    }
}

/// The server's per-stream replay window: the last `RING_BUFFER_FRAMES`
/// frames, so a reconnecting client asks for a **selective replay** instead
/// of a fresh save (§B, card 10 §D.3).
#[derive(Clone, Debug, Default)]
pub struct ServerRingBuffer {
    streams: BTreeMap<u32, VecDeque<(StreamFrameRef, Vec<u8>)>>,
}

/// The reconnect answer to `HELLO|resume|last_seq` (§D).
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Resume {
    /// Stall-free reconnect: exactly the withheld frames, in stream order.
    Ok { frames: Vec<(StreamFrameRef, Vec<u8>)> },
    /// The ring cannot cover `last_seq` (longer than `RING_BUFFER_FRAMES`
    /// behind); the client takes the checkpoint path — the only
    /// allowed stall.
    Fresh,
}

impl ServerRingBuffer {
    pub fn append(&mut self, frame: StreamFrameRef, payload: Vec<u8>) {
        let queue = self.streams.entry(frame.stream_id).or_default();
        queue.push_back((frame, payload));
        while queue.len() > RING_BUFFER_FRAMES as usize {
            queue.pop_front();
        }
    }

    /// Frames still held for `(stream, epoch)` at or after `seq`, in order.
    pub fn replay_from(&self, stream: u32, epoch: u32, seq: u64) -> Vec<(StreamFrameRef, Vec<u8>)> {
        let mut out = Vec::new();
        if let Some(queue) = self.streams.get(&stream) {
            for (rf, payload) in queue.iter() {
                if rf.epoch == epoch && rf.seq >= seq {
                    out.push((*rf, payload.clone()));
                }
            }
        }
        out
    }

    /// Decide a `HELLO|resume`: the ring covers the gap or the client must go
    /// fresh. A stale epoch (controller restarted) is a `Fresh`, matching the
    /// card 10 rule that the client must not rewind across epochs.
    pub fn resume(&self, stream: u32, epoch: u32, last_seq: u64) -> Resume {
        match self.streams.get(&stream) {
            None => Resume::Fresh,
            Some(queue) => {
                let covered = queue
                    .front()
                    .is_some_and(|(head, _)| head.epoch == epoch && head.seq <= last_seq + 1);
                if covered {
                    Resume::Ok { frames: self.replay_from(stream, epoch, last_seq + 1) }
                } else {
                    Resume::Fresh
                }
            }
        }
    }

    pub fn stream_len(&self, stream: u32) -> usize {
        self.streams.get(&stream).map_or(0, VecDeque::len)
    }

    pub fn total_frames(&self) -> usize {
        self.streams.values().map(VecDeque::len).sum()
    }
}

/// The `SIMSONLINE_ROLE` tag a client attests to (§C).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RoleTag {
    Server,
    Client,
    Client2,
    Client3,
}

/// The `CLIENT_ENV` attestation a `HELLO` must carry (§C).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ClientEnv {
    /// `SIMSONLINE_SIMLINK_CLIENT_RING=1`.
    pub simlink_client_ring: bool,
    /// `SIMSONLINE_WORLD_FROM_SERVER=1`.
    pub world_from_server: bool,
    /// `SIMSONLINE_ROLE`.
    pub role_tag: RoleTag,
    /// The bridge protocol the client controller speaks.
    pub bridge_version: u16,
    /// The SimLink native version read from the installed `SimLink.asi`.
    pub simlink_native_version: u16,
}

/// Why a `HELLO` was refused — a named diagnostic, never a bare reset (§C).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum HelloRejection {
    MissingSimlinkRing,
    MissingWorldFromServer,
    WrongBridgeVersion { got: u16, want: u16 },
    WrongSimlinkVersion { got: u16, want: u16 },
}

impl From<HelloRejection> for KernelError {
    fn from(_err: HelloRejection) -> Self {
        KernelError::HelloRejected
    }
}

/// Reject a client that would silently run unpatched (the playbook
/// lesson enforced at join time; §F: a refused `HELLO` beats a "the game
/// looks weird" bug report).
pub fn hello_gate(env: &ClientEnv) -> Result<(), HelloRejection> {
    if !env.simlink_client_ring {
        return Err(HelloRejection::MissingSimlinkRing);
    }
    if !env.world_from_server {
        return Err(HelloRejection::MissingWorldFromServer);
    }
    if env.bridge_version != BRIDGE_PROTOCOL_VERSION {
        return Err(HelloRejection::WrongBridgeVersion {
            got: env.bridge_version,
            want: BRIDGE_PROTOCOL_VERSION,
        });
    }
    if env.simlink_native_version != SIMLINK_NATIVE_VERSION {
        return Err(HelloRejection::WrongSimlinkVersion {
            got: env.simlink_native_version,
            want: SIMLINK_NATIVE_VERSION,
        });
    }
    Ok(())
}

/// Card 26 §C golden-corpus hook (canary 19,).
#[cfg(test)]
mod kdiff {
    use super::*;

    #[test]
    fn kdiff_canary_19() {
        let mut b = WanJitterBuffer::default();
        let r = b.ingest(StreamFrameRef { stream_id: 1, epoch: 1, seq: 33 }, vec![0u8], 33);
        let flushed = (r == WanIngest::OutOfWindow && b.reorder_misses == 1
            && b.window.is_empty()) as u64;
        println!("KDIFF|canary=19|case=out_of_window_flush|got={:016x}", flushed);
        assert_eq!(flushed, 1);
        let env = ClientEnv {
            simlink_client_ring: true, world_from_server: true,
            role_tag: RoleTag::Client2,
            bridge_version: BRIDGE_PROTOCOL_VERSION,
            simlink_native_version: SIMLINK_NATIVE_VERSION,
        };
        let admitted = hello_gate(&env).is_ok() as u64;
        println!("KDIFF|canary=19|case=hello_gate_accepts|got={:016x}", admitted);
        assert_eq!(admitted, 1);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn frame(stream: u32, seq: u64) -> StreamFrameRef {
        StreamFrameRef { stream_id: stream, epoch: 1, seq }
    }

    #[test]
    fn in_order_frames_stream_through() {
        let mut b = WanJitterBuffer::default();
        for s in 1..=100u64 {
            assert_eq!(b.ingest(frame(1, s), vec![0u8; 4], s), WanIngest::Delivered);
        }
        assert_eq!(b.delivered, 100);
        assert!(b.window.is_empty());
        assert_eq!(b.reorder_misses, 0);
    }

    #[test]
    fn single_gap_is_held_then_drained_when_filled() {
        let mut b = WanJitterBuffer::default();
        for s in 1..=5u64 {
            b.ingest(frame(1, s), vec![0u8], s);
        }
        assert_eq!(b.delivered, 5);
        for s in 7..=10u64 {
            assert_eq!(b.ingest(frame(1, s), vec![0u8], s), WanIngest::Buffered);
        }
        assert_eq!(b.window.len(), 4);
        assert_eq!(b.delivered, 5);
        // Filling seq 6 delivers the whole held run in one go.
        assert_eq!(b.ingest(frame(1, 6), vec![0u8], 6), WanIngest::Delivered);
        assert_eq!(b.delivered, 10);
        assert!(b.window.is_empty());
        assert!(b.gaps.is_empty());
    }

    #[test]
    fn duplicates_and_window_overruns() {
        let mut b = WanJitterBuffer::default();
        b.ingest(frame(1, 10), vec![0u8], 10); // buffered: the cursor still waits at 0
        assert_eq!(b.delivered, 0);
        assert_eq!(b.ingest(frame(1, 10), vec![0u8], 11), WanIngest::Duplicate);
        // One beyond the 32-frame window: flushed, counted, never buffered.
        let overrun = b.delivered + u64::from(REORDER_WINDOW_FRAMES) + 1;
        assert_eq!(b.ingest(frame(1, overrun), vec![0u8], overrun), WanIngest::OutOfWindow);
        assert_eq!(b.reorder_misses, 1);
        assert!(b.window.is_empty());
    }

    #[test]
    fn nack_cooldown_prevents_a_storm() {
        let mut b = WanJitterBuffer::default();
        b.ingest(frame(1, 1), vec![0u8], 1); // delivered = 1, the gap starts at 2
        // Held-gap arrivals spaced one per cooldown window: exactly one NACK
        // per GAP_NACK_COOLDOWN_TICKS, never one per frame.
        for i in 0..9u64 {
            let t = 8 + 6 * i;
            assert_eq!(b.ingest(frame(1, 8 + i), vec![0u8], t), WanIngest::Buffered);
        }
        assert_eq!(b.nacks_sent, 9);
        // A burst of ten more arrivals inside a single window: the cooldown
        // absorbs them all (60 - 56 = 4 < 6).
        for s in 17..27u64 {
            b.ingest(frame(1, s), vec![0u8], 60);
        }
        assert_eq!(b.nacks_sent, 9);
        assert_eq!(b.gaps, vec![2]);
        assert_eq!(b.delivered, 1);
    }

    #[test]
    fn paired_social_barrier_survives_the_reorder_window() {
        // An `Ss` at seq 100 and its `Sa` at 105 (5-frame gap, card 16 §B.2):
        // both land inside the 32-frame reorder budget and deliver in order.
        let mut b = WanJitterBuffer::default();
        for s in 1..=104u64 {
            b.ingest(frame(1, s), vec![0u8], s);
        }
        assert_eq!(b.delivered, 104);
        assert_eq!(b.ingest(frame(1, 106), vec![0u8], 106), WanIngest::Buffered);
        assert_eq!(b.ingest(frame(1, 105), vec![0u8], 105), WanIngest::Delivered);
        assert_eq!(b.delivered, 106);
    }

    #[test]
    fn ring_buffer_caps_and_replays_selectively() {
        let mut ring = ServerRingBuffer::default();
        for s in 1..=10_000u64 {
            ring.append(frame(1, s), vec![s as u8]);
        }
        assert_eq!(ring.stream_len(1), RING_BUFFER_FRAMES as usize);
        assert_eq!(ring.total_frames(), RING_BUFFER_FRAMES as usize);
        // The oldest 7,952 frames are gone; replay starts at the retained tail.
        let replay = ring.replay_from(1, 1, 8_000);
        assert_eq!(replay.first().map(|(rf, _)| rf.seq), Some(8_000));
        assert_eq!(replay.len(), 10_000 - 8_000 + 1);
        // A reconnect 500 frames behind is covered: RESUME_OK, no checkpoint.
        assert!(matches!(ring.resume(1, 1, 9_500), Resume::Ok { frames } if frames.len() == 500));
        // A reconnect 3,000 frames behind is beyond the ring: RESUME_FRESH
        // (the only path allowed to hit the stall).
        assert_eq!(ring.resume(1, 1, 6_999), Resume::Fresh);
        // A stale epoch never rewinds the client's cursor (card 10 §B.1).
        assert_eq!(ring.resume(1, 2, 9_900), Resume::Fresh);
        assert_eq!(ring.resume(7, 1, 1), Resume::Fresh);
    }

    #[test]
    fn hello_gate_refuses_each_missing_or_wrong_env_piece() {
        let good = ClientEnv {
            simlink_client_ring: true,
            world_from_server: true,
            role_tag: RoleTag::Client2,
            bridge_version: 17,
            simlink_native_version: 15,
        };
        assert_eq!(hello_gate(&good), Ok(()));
        assert_eq!(
            hello_gate(&ClientEnv { simlink_client_ring: false, ..good }),
            Err(HelloRejection::MissingSimlinkRing)
        );
        assert_eq!(
            hello_gate(&ClientEnv { world_from_server: false, ..good }),
            Err(HelloRejection::MissingWorldFromServer)
        );
        assert_eq!(
            hello_gate(&ClientEnv { bridge_version: 16, ..good }),
            Err(HelloRejection::WrongBridgeVersion { got: 16, want: 17 })
        );
        assert_eq!(
            hello_gate(&ClientEnv { simlink_native_version: 14, ..good }),
            Err(HelloRejection::WrongSimlinkVersion { got: 14, want: 15 })
        );
        assert_eq!(KernelError::from(HelloRejection::MissingSimlinkRing), KernelError::HelloRejected);
    }

    #[test]
    fn pinned_constants_match_the_sealed_card() {
        assert_eq!(REORDER_WINDOW_FRAMES, 32);
        assert_eq!(RING_BUFFER_FRAMES, 2048);
        assert_eq!(GAP_NACK_COOLDOWN_TICKS, 6);
        assert_eq!(BRIDGE_PROTOCOL_VERSION, 17);
        assert_eq!(SIMLINK_NATIVE_VERSION, 15);
        assert_eq!(DEFAULT_WAN_PORT, 34019);
    }
}
