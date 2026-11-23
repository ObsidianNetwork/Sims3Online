//! The driver against a simulated server mod in a [`SimMemory`] region (card 56 §C, §D U4). The fake mod
//! follows `SimsOnlineServerHostMod/BridgeMemoryBuffer.cs` write discipline (`WriteHostLineAt`: mid-write
//! stamp, clear, seq, length, payload, host stamp) and answers CONNECT with HELLO|6 and every other line with
//! `OK|corr`. Time never passes: the driver's pacer steps the mod, so nothing here waits on a clock.
#![forbid(unsafe_code)]

use std::cell::Cell;
use std::time::Duration;

use simlink_bridge::crc32::crc32;
use simlink_bridge::driver::{BridgeDriver, BulkPayload, Pacer};
use simlink_bridge::error::BridgeError;
use simlink_bridge::layout::*;
use simlink_bridge::memory::{GuestMemory, RegionInfo, SimMemory};
use simlink_bridge::text::{encode_text, Guid};
use simlink_bridge::verbs::BridgeCommand;

const BASE: u32 = 0x0100_0000;
const CORR: &str = "0123456789abcdef0123456789abcdef";

fn region() -> SimMemory {
    let mem = SimMemory::new();
    mem.map(BASE - 0x1000, TOTAL_REGION_SIZE + 0x2000, true);
    mem.poke(BASE, &ControlRegionHeader::v11().to_bytes());
    mem.poke(BASE + bulk_region_base_offset() as u32, &BulkRegionHeader::v11().to_bytes());
    mem
}

fn at(offset: usize) -> u32 {
    BASE + offset as u32
}

fn slot(i: usize) -> usize {
    slot_byte_offset(i).unwrap()
}

/// Write a host line the way the mod does.
fn host_write(mem: &SimMemory, offset: usize, seq: i32, line: &str) {
    let payload = line.as_bytes();
    mem.poke(at(offset), &HOST_WRITING_SENDER_ID.to_le_bytes());
    mem.poke(at(offset + 8), &seq.to_le_bytes());
    mem.poke(at(offset + 12), &(payload.len() as i32).to_le_bytes());
    mem.poke(at(offset + SLOT_HEADER_SIZE), payload);
    mem.poke(at(offset), &HOST_SENDER_ID.to_le_bytes());
}

fn sender(mem: &SimMemory, offset: usize) -> u64 {
    u64::from_le_bytes(mem.peek(at(offset), 8).try_into().unwrap())
}

struct FakeMod<'a> {
    mem: &'a SimMemory,
    last_seq: i32,
    multi: bool,
    host_cursor: usize,
    seen: Vec<String>,
}

impl<'a> FakeMod<'a> {
    fn new(mem: &'a SimMemory) -> Self {
        Self { mem, last_seq: 0, multi: false, host_cursor: 0, seen: Vec::new() }
    }

    fn take(&mut self, offset: usize) -> Option<(i32, String)> {
        let h = ControlSlotHeader::from_bytes(&self.mem.peek(at(offset), SLOT_HEADER_SIZE))?;
        if h.sender_id != SERVER_SENDER_ID || h.seq <= self.last_seq || h.length <= 0 {
            return None;
        }
        let line = String::from_utf8(self.mem.peek(at(offset + SLOT_HEADER_SIZE), h.length as usize)).ok()?;
        self.mem.poke(at(offset), &CONSUMED_SENDER_ID.to_le_bytes());
        self.last_seq = h.seq;
        Some((h.seq, line))
    }

    fn step(&mut self) {
        let mut inbound = Vec::new();
        if let Some(f) = self.take(COMPAT_SLOT_OFFSET) {
            inbound.push(f);
        }
        if self.multi {
            for i in CTL_SLOT_BASE..CTL_SLOT_BASE + CTL_SLOT_COUNT {
                if let Some(f) = self.take(slot(i)) {
                    inbound.push(f);
                }
            }
        }
        for (seq, line) in inbound {
            self.seen.push(line.clone());
            let name = line.split('|').next().unwrap_or("");
            if name == "CONNECT" {
                let hello = format!("HELLO|6|{}|10|{}|{}", encode_text("W"), encode_text("abc@now"), encode_text(DEFAULT_HELLO_CAPS));
                host_write(self.mem, COMPAT_SLOT_OFFSET, seq, &hello);
                self.multi = true;
            } else {
                let corr = line.split('|').nth(1).unwrap_or("");
                host_write(self.mem, slot(HOST_SLOT_BASE + self.host_cursor), seq, &format!("OK|{corr}"));
                self.host_cursor = (self.host_cursor + 1) % HOST_SLOT_COUNT;
            }
        }
    }
}

/// The pacer: each pause lets the fake mod run one pump.
struct ModPacer<'a> {
    fake: FakeMod<'a>,
    pauses: usize,
}

impl Pacer for ModPacer<'_> {
    fn pause(&mut self, _ms: u64) {
        self.pauses += 1;
        self.fake.step();
    }
}

/// A pacer that does nothing (and counts).
struct Idle(usize);

impl Pacer for Idle {
    fn pause(&mut self, _ms: u64) {
        self.0 += 1;
    }
}

type Fake<'a> = BridgeDriver<&'a SimMemory, ModPacer<'a>>;

fn attached(mem: &SimMemory) -> Fake<'_> {
    let pacer = ModPacer { fake: FakeMod::new(mem), pauses: 0 };
    let (driver, hello) = BridgeDriver::attach(mem, Duration::from_secs(600), pacer).expect("attach");
    assert!(hello.line.starts_with("HELLO|6|"));
    assert_eq!(hello.hello.map(|h| h.wire_version), Some(6));
    driver
}

#[test]
fn attach_finds_the_region_connects_and_routes_commands_to_ctl_slots() {
    let mem = region();
    let mut driver = attached(&mem);
    assert_eq!(driver.base(), BASE);
    assert!(driver.layout_v11());
    assert_eq!(driver.pacer_mut().fake.seen[0], "CONNECT|00000000000000000000000000000000|23");

    driver.send_line(&format!("PING|{CORR}")).unwrap();
    assert_eq!(sender(&mem, slot(CTL_SLOT_BASE)), SERVER_SENDER_ID, "first ctl slot carries the command");
    driver.pacer_mut().pause(1);
    assert_eq!(driver.poll_line().unwrap().as_deref(), Some(&*format!("OK|{CORR}")));
    assert_eq!(sender(&mem, slot(HOST_SLOT_BASE)), CONSUMED_SENDER_ID, "the read stamps consumed");
    assert_eq!(driver.poll_line().unwrap(), None);

    driver.send_line(&format!("PING|{CORR}")).unwrap();
    assert_eq!(sender(&mem, slot(CTL_SLOT_BASE + 1)), SERVER_SENDER_ID, "the ctl cursor advances");
}

#[test]
fn the_frame_recorder_writes_both_directions_in_the_controllers_form() {
    let mem = region();
    let mut driver = attached(&mem);
    let frames = std::sync::Arc::new(std::sync::Mutex::new(Vec::<String>::new()));
    let sink = frames.clone();
    driver.set_frame_recorder(Box::new(move |f: &str| sink.lock().unwrap().push(f.to_string())));

    driver.send_line(&format!("PING|{CORR}")).unwrap();
    let seq = driver.sequence();
    driver.pacer_mut().pause(1);
    assert_eq!(driver.poll_line().unwrap().as_deref(), Some(&*format!("OK|{CORR}")));
    assert_eq!(
        *frames.lock().unwrap(),
        vec![format!("ctl->host seq={seq} slot={CTL_SLOT_BASE} PING|{CORR}"), format!("host->ctl seq={seq} OK|{CORR}")]
    );
    // A refused verb never reaches the mod, so it never reaches the record either.
    assert!(driver.send_line(&format!("APPLY_FORCE_ALIGN|{CORR}")).is_err());
    assert_eq!(frames.lock().unwrap().len(), 2);
}

#[test]
fn a_compat_write_keeps_the_v11_control_header() {
    let mem = region();
    let mut driver = attached(&mem);
    driver.send_compat_line(&format!("PING|{CORR}|{}", "x".repeat(200))).unwrap();
    // The frame is header + payload only, so it overwrites bytes 32..; the repair puts the header back.
    assert_eq!(mem.peek(at(32), 32), ControlRegionHeader::v11().to_bytes()[32..].to_vec());
    let h = ControlSlotHeader::from_bytes(&mem.peek(BASE, 16)).unwrap();
    assert_eq!((h.sender_id, h.seq), (SERVER_SENDER_ID, driver.sequence()));
}

/// The established-v11, valid-ctl-frame path of BridgeMemoryBuffer.cs:155-223.
/// ReadServerLine scans physical ctl indices from zero on EVERY call (:172-179).
/// ReadServerLineAt rejects sequence <= the global cursor (:193-211), bounds the
/// payload (:213-218), advances the cursor, and returns WITHOUT consuming (:220-223).
/// Unlike FakeMod::take, this keeps SERVER stamps exactly as the mod does.
fn read_mod_ctl_line(mem: &SimMemory, last_seq: &mut i32) -> Option<(usize, i32, String)> {
    // These schedules have no compat command/CONNECT and no malformed frames, so
    // the compat priority/reset and invalid-length ERROR branches cannot apply.
    assert_ne!(sender(mem, COMPAT_SLOT_OFFSET), SERVER_SENDER_ID);
    for i in 0..CTL_SLOT_COUNT {
        let offset = slot(CTL_SLOT_BASE + i);
        let h = ControlSlotHeader::from_bytes(&mem.peek(at(offset), SLOT_HEADER_SIZE)).unwrap();
        if h.sender_id != SERVER_SENDER_ID || h.seq <= *last_seq {
            continue;
        }
        assert!(h.length > 0 && h.length as usize <= SLOT_PAYLOAD_MAX);
        let line = String::from_utf8_lossy(&mem.peek(at(offset + SLOT_HEADER_SIZE), h.length as usize)).into_owned();
        *last_seq = h.seq;
        return Some((i, h.seq, line));
    }
    None
}

/// n29: preserve delivery at wrap, with the same no-wrap and read-before-wrap
/// comparison schedules. A write that could overtake must wait in the driver.
#[test]
fn ctl_wrap_delivers_an_outstanding_checkpoint_before_the_next_command() {
    for (checkpoint_index, read_before_sync) in [(7, false), (6, false), (7, true)] {
        let mem = region();
        let mut driver = BridgeDriver::new(&mem, BASE, 100, Idle(0));
        // A negotiated v11 HELLO locks routing; header detection alone resets
        // the cursor on each refresh (driver.rs:365-387).
        driver.apply_connect_hello(&format!(
            "HELLO|6|{}|10|{}|{}", encode_text("W"), encode_text("abc@now"), encode_text(DEFAULT_HELLO_CAPS)
        )).unwrap();
        assert!(driver.layout_v11());
        let mut last_seq = 100;
        let prime = format!("PING|{CORR}");
        for i in 0..checkpoint_index {
            driver.send_line(&prime).unwrap();
            assert_eq!(read_mod_ctl_line(&mem, &mut last_seq), Some((i, driver.sequence(), prime.clone())));
            host_write(&mem, slot(HOST_SLOT_BASE), last_seq, &format!("OK|{CORR}"));
            assert_eq!(driver.poll_line().unwrap(), Some(format!("OK|{CORR}")));
        }
        let frames = std::sync::Arc::new(std::sync::Mutex::new(Vec::<String>::new()));
        let sink = frames.clone();
        driver.set_frame_recorder(Box::new(move |f| sink.lock().unwrap().push(f.to_owned())));
        let checkpoint = format!("SAVE_GAME|{CORR}|{}", encode_text("CtlWrapRepro.sims3"));
        let sync = "PING|fedcba9876543210fedcba9876543210".to_owned();
        driver.send_command(&BridgeCommand::SaveGame {
            corr: Guid::parse_wire(CORR).unwrap(), save_name: "CtlWrapRepro.sims3".into(),
        }).unwrap();
        let n = driver.sequence();
        let before_reads = mem.peek(BASE, CONTROL_REGION_SIZE);
        let mut executed = Vec::new();
        if read_before_sync {
            executed.push(read_mod_ctl_line(&mem, &mut last_seq).unwrap());
            host_write(&mem, slot(HOST_SLOT_BASE), last_seq, &format!("OK|{CORR}"));
            assert_eq!(driver.poll_line().unwrap(), Some(format!("OK|{CORR}")));
        }
        driver.send_line(&sync).unwrap();
        let sync_index = (checkpoint_index + 1) % CTL_SLOT_COUNT;
        let queued_at_wrap = checkpoint_index == 7 && !read_before_sync;
        assert_eq!(
            (0..CTL_SLOT_COUNT).filter(|i| {
                let h = ControlSlotHeader::from_bytes(&mem.peek(at(slot(CTL_SLOT_BASE + i)), SLOT_HEADER_SIZE)).unwrap();
                h.sender_id == SERVER_SENDER_ID && h.seq > last_seq
            }).count(),
            if read_before_sync || queued_at_wrap { 1 } else { 2 },
            "a command that would overtake stays queued instead of entering a lower slot"
        );
        let offset = slot(CTL_SLOT_BASE + checkpoint_index);
        assert_eq!(
            mem.peek(at(offset), SLOT_SIZE),
            before_reads[offset..offset + SLOT_SIZE],
            "the checkpoint remains intact until the mod reads it"
        );
        for _ in 0..2 {
            if let Some(frame) = read_mod_ctl_line(&mem, &mut last_seq) {
                let corr = frame.2.split('|').nth(1).unwrap();
                host_write(&mem, slot(HOST_SLOT_BASE), frame.1, &format!("OK|{corr}"));
                executed.push(frame);
            }
            // Reading a host sequence lets the next poll safely flush a wrap.
            while driver.poll_line().unwrap().is_some() {}
        }
        assert_eq!(
            frames.lock().unwrap().iter().filter(|frame| frame.starts_with("ctl->host")).cloned().collect::<Vec<_>>(),
            vec![
                format!("ctl->host seq={n} slot={} {checkpoint}", CTL_SLOT_BASE + checkpoint_index),
                format!("ctl->host seq={} slot={} {sync}", n + 1, CTL_SLOT_BASE + sync_index),
            ]
        );
        assert_eq!(executed, vec![(checkpoint_index, n, checkpoint), (sync_index, n + 1, sync)]);
        assert_eq!(read_mod_ctl_line(&mem, &mut last_seq), None, "neither frame executes twice");
        assert_eq!(driver.pacer_mut().0, 0, "only this test's explicit reads may run the mod model");
    }
}

#[test]
fn ctl_wrap_delivers_commands_queued_behind_a_slow_checkpoint_in_sequence_order() {
    let mem = region();
    let mut driver = BridgeDriver::new(&mem, BASE, 100, Idle(0));
    driver.apply_connect_hello(&format!(
        "HELLO|6|{}|10|{}|{}", encode_text("W"), encode_text("abc@now"), encode_text(DEFAULT_HELLO_CAPS)
    )).unwrap();
    let mut last_seq = 100;
    for i in 0..CTL_SLOT_COUNT - 1 {
        driver.send_line(&format!("PING|{CORR}")).unwrap();
        assert_eq!(read_mod_ctl_line(&mem, &mut last_seq).unwrap().0, i);
        host_write(&mem, slot(HOST_SLOT_BASE), last_seq, &format!("OK|{CORR}"));
        assert_eq!(driver.poll_line().unwrap(), Some(format!("OK|{CORR}")));
    }
    let checkpoint = format!("SAVE_GAME|{CORR}|{}", encode_text("CtlWrapRepro.sims3"));
    driver.send_line(&checkpoint).unwrap();
    let n = driver.sequence();
    let mut executed = vec![read_mod_ctl_line(&mem, &mut last_seq).unwrap()];
    assert_eq!(executed[0], (7, n, checkpoint.clone()));
    let writes = mem.write_count();
    let mut expected = vec![(n, checkpoint)];
    // More than two rings must queue while SAVE_GAME is inside its native call.
    for i in 0..CTL_SLOT_COUNT * 3 {
        let line = format!("PING|{:032x}", i + 1);
        driver.send_line(&line).unwrap();
        expected.push((driver.sequence(), line));
    }
    // A fake clock, not a nine-second wall-clock sleep. No reply is published
    // before 9000 ms, just as a synchronous native save holds up the mod pump.
    for now_ms in [0, 1, 8_999] {
        assert_eq!(driver.poll_line().unwrap(), None, "save still running at {now_ms} ms");
        assert_eq!(mem.write_count(), writes, "no queued command may overtake the save");
    }
    // At 9000 ms the save returns, independently of Rust polling its reply.
    host_write(&mem, slot(HOST_SLOT_BASE), n, &format!("OK|{CORR}"));
    let mut replies = Vec::new();
    while let Some(line) = driver.poll_line().unwrap() { replies.push(line); }
    for _ in 1..expected.len() {
        if let Some(frame) = read_mod_ctl_line(&mem, &mut last_seq) {
            host_write(&mem, slot(HOST_SLOT_BASE), frame.1, &format!("OK|{}", frame.2.split('|').nth(1).unwrap()));
            executed.push(frame);
        }
        while let Some(line) = driver.poll_line().unwrap() { replies.push(line); }
    }
    assert_eq!(executed.into_iter().map(|(_, seq, line)| (seq, line)).collect::<Vec<_>>(), expected);
    assert_eq!(replies, expected.iter().map(|(_, line)| format!("OK|{}", line.split('|').nth(1).unwrap())).collect::<Vec<_>>());
    assert_eq!(read_mod_ctl_line(&mem, &mut last_seq), None);
    assert_eq!(driver.pacer_mut().0, 0, "queueing never blocks the actor or waits in the pacer");
}

#[test]
fn an_oversized_frame_is_refused_before_any_write() {
    let mem = region();
    let mut driver = attached(&mem);
    let before = mem.write_count();
    let err = driver.send_line(&"A".repeat(SLOT_PAYLOAD_MAX + 1)).unwrap_err();
    assert_eq!(err, BridgeError::FrameTooLarge { len: SLOT_PAYLOAD_MAX + 1 });
    assert_eq!(mem.write_count(), before);
    assert!(driver.send_line(&"A".repeat(SLOT_PAYLOAD_MAX)).is_ok());
}

#[test]
fn zero_id_errors_do_not_release_queued_commands_but_correlated_errors_do() {
    for (zero_id, valid_wire_id) in [
        ("00000000000000000000000000000000", true),
        ("00000000-0000-0000-0000-000000000000", false),
    ] {
        let mem = region();
        let mut driver = BridgeDriver::new(&mem, BASE, 100, Idle(0));
        driver.apply_connect_hello(&format!(
            "HELLO|6|{}|10|{}|{}", encode_text("W"), encode_text("abc@now"), encode_text(DEFAULT_HELLO_CAPS)
        )).unwrap();
        let mut last_seq = 100;
        for _ in 0..CTL_SLOT_COUNT - 1 {
            driver.send_line(&format!("PING|{CORR}")).unwrap();
            read_mod_ctl_line(&mem, &mut last_seq).unwrap();
            host_write(&mem, slot(HOST_SLOT_BASE), last_seq, &format!("OK|{CORR}"));
            assert_eq!(driver.poll_line().unwrap(), Some(format!("OK|{CORR}")));
        }
        let checkpoint = format!("SAVE_GAME|{CORR}|{}", encode_text("CtlWrapRepro.sims3"));
        driver.send_line(&checkpoint).unwrap();
        let n = driver.sequence();
        let next = "PING|fedcba9876543210fedcba9876543210";
        driver.send_line(next).unwrap();
        // Model the invalid-length branch: the mod reports N but has NOT
        // accepted N or advanced last_seq (BridgeMemoryBuffer.cs:213-218).
        let error = format!("ERROR|{zero_id}|{}", encode_text("Invalid memory bridge frame length."));
        host_write(&mem, slot(HOST_SLOT_BASE), n, &error);
        // The mod emits N-form GUIDs; the existing host-frame validator rejects
        // a hyphenated spelling before watermark handling (reply.rs:83-101).
        assert_eq!(driver.poll_line().unwrap(), valid_wire_id.then_some(error));
        let writes = mem.write_count();
        assert_eq!(driver.poll_line().unwrap(), None);
        assert_eq!(mem.write_count(), writes, "a zero-ID ERROR cannot release the queued wrap");
        assert_eq!(read_mod_ctl_line(&mem, &mut last_seq), Some((7, n, checkpoint)));
        // A correlated refusal is different: the command was dequeued, so
        // its sequence is legitimate progress even though its handler refused.
        let refusal = format!("ERROR|{CORR}|{}", encode_text("Save precondition refused."));
        host_write(&mem, slot(HOST_SLOT_BASE), n, &refusal);
        assert_eq!(driver.poll_line().unwrap(), Some(refusal));
        assert_eq!(driver.poll_line().unwrap(), None);
        assert_eq!(read_mod_ctl_line(&mem, &mut last_seq), Some((0, n + 1, next.to_owned())));
        assert_eq!(read_mod_ctl_line(&mem, &mut last_seq), None);
    }
}

#[test]
fn the_sequence_is_seeded_above_the_region_maximum() {
    let mem = region();
    host_write(&mem, slot(3), 5000, "OK|x");
    let mut d = BridgeDriver::new(&mem, BASE, 2000, Idle(0));
    d.seed_sequence_above_region_max();
    assert_eq!(d.sequence(), 6024);
    let mut high = BridgeDriver::new(&mem, BASE, 9000, Idle(0));
    high.seed_sequence_above_region_max();
    assert_eq!(high.sequence(), 9000, "an already-higher seed stays");
}

#[test]
fn connect_clears_the_prior_sessions_frames() {
    let mem = region();
    mem.poke(at(slot(CTL_SLOT_BASE + 2)), &SERVER_SENDER_ID.to_le_bytes());
    host_write(&mem, slot(HOST_SLOT_BASE + 1), 3, "OK|stale");
    let mut d = BridgeDriver::new(&mem, BASE, 100, Idle(0));
    d.begin_connect().unwrap();
    assert_eq!(sender(&mem, slot(CTL_SLOT_BASE + 2)), CONSUMED_SENDER_ID);
    assert_eq!(sender(&mem, slot(HOST_SLOT_BASE + 1)), CONSUMED_SENDER_ID);
    assert_eq!(sender(&mem, COMPAT_SLOT_OFFSET), SERVER_SENDER_ID);
    let h = ControlSlotHeader::from_bytes(&mem.peek(BASE, 16)).unwrap();
    assert_eq!(h.seq, 101, "stamp and sequence land in the one CONNECT write");
}

#[test]
fn an_unread_host_line_a_write_displaces_comes_back_from_poll_line() {
    let mem = region();
    let mut driver = attached(&mem);
    host_write(&mem, slot(HOST_SLOT_BASE + 5), 1, "EVENT|InteractionStarted|1|2");
    driver.send_line(&format!("PING|{CORR}")).unwrap();
    assert_eq!(driver.poll_line().unwrap().as_deref(), Some("EVENT|InteractionStarted|1|2"));
}

#[test]
fn a_corrupt_host_frame_is_consumed_and_skipped() {
    let mem = region();
    let mut driver = attached(&mem);
    host_write(&mem, slot(HOST_SLOT_BASE), 1, "OK\u{1}|junk");
    host_write(&mem, slot(HOST_SLOT_BASE + 1), 1, &format!("OK|{CORR}"));
    assert_eq!(driver.poll_line().unwrap().as_deref(), Some(&*format!("OK|{CORR}")));
    assert_eq!(sender(&mem, slot(HOST_SLOT_BASE)), CONSUMED_SENDER_ID);
}

/// Clears the mod's mid-write stamp on the compat slot at the third pause.
struct Clear<'a>(&'a SimMemory, usize);

impl Pacer for Clear<'_> {
    fn pause(&mut self, _ms: u64) {
        self.1 += 1;
        if self.1 == 3 {
            self.0.poke(BASE, &CONSUMED_SENDER_ID.to_le_bytes());
        }
    }
}

#[test]
fn a_mid_write_stamp_is_waited_out_then_the_write_proceeds() {
    let mem = region();
    // Compat routing (no v11 header): stomp the header so the layout reads as v10 and the marker is gone.
    mem.poke(at(32), &[0u8; 4]);
    mem.poke(at(bulk_region_base_offset()), &[0u8; 32]);
    mem.poke(BASE, &HOST_WRITING_SENDER_ID.to_le_bytes());
    let mut driver = BridgeDriver::new(&mem, BASE, 50, Clear(&mem, 0));
    driver.send_line(&format!("PING|{CORR}")).unwrap();
    assert!(!driver.layout_v11());
    assert_eq!(driver.pacer_mut().1, 3);
    assert_eq!(sender(&mem, COMPAT_SLOT_OFFSET), SERVER_SENDER_ID);
}

fn put_host_bulk(mem: &SimMemory, index: usize, corr: [u8; 16], payload: &[u8]) -> (BulkSlotHeader, usize) {
    let offset = BulkSlot::new(index).unwrap().byte_offset();
    let header = BulkSlotHeader {
        state: BULK_STATE_HOST_READY,
        correlation_id: corr,
        total_length: payload.len() as i32,
        crc32: crc32(payload),
    };
    mem.poke(at(offset), &header.to_bytes());
    mem.poke(at(offset + BULK_SLOT_HEADER_SIZE), payload);
    (header, offset)
}

#[test]
fn host_bulk_reads_check_state_crc_and_band() {
    let mem = region();
    let mut driver = attached(&mem);
    let payload = b"SNAPSHOT|abc".to_vec();
    let (header, s1) = put_host_bulk(&mem, 1, [0; 16], &payload);
    let crc = header.crc32;

    assert_eq!(driver.read_bulk_ref(1, crc ^ 1), Err(BridgeError::BulkCrcMismatch { slot: 1 }));
    assert_eq!(sender(&mem, s1), BULK_STATE_HOST_READY, "a refused read consumes nothing");
    assert_eq!(driver.read_bulk_ref(1, crc).unwrap(), BulkPayload::Text("SNAPSHOT|abc".into()));
    assert_eq!(sender(&mem, s1), BULK_STATE_HOST_CONSUMED);
    assert_eq!(driver.read_bulk_ref(1, crc), Err(BridgeError::StaleStamp { slot: 1 }));
    assert_eq!(driver.read_bulk(4), Err(BridgeError::SlotOutOfBand { slot: 4 }));

    // A payload whose bytes were changed after the header was written: the payload CRC catches it.
    mem.poke(at(s1), &header.to_bytes());
    mem.poke(at(s1 + BULK_SLOT_HEADER_SIZE), b"SNAPSHOT|abd");
    assert_eq!(driver.read_bulk(1), Err(BridgeError::BulkCrcMismatch { slot: 1 }));
    assert_eq!(sender(&mem, s1), BULK_STATE_HOST_READY);
}

#[test]
fn a_snapshot2_bulk_payload_is_classified_by_its_magic() {
    let mem = region();
    let mut driver = attached(&mem);
    let mut payload = SNAPSHOT2_MAGIC.to_le_bytes().to_vec();
    payload.extend_from_slice(&2u32.to_le_bytes());
    payload.extend_from_slice(b"body");
    put_host_bulk(&mem, 2, [0; 16], &payload);
    let got = driver.read_bulk(2).unwrap();
    assert!(got.version_supported());
    assert_eq!(got, BulkPayload::Snapshot2 { version: 2, bytes: payload.clone() });
    payload[4] = 9;
    put_host_bulk(&mem, 2, [0; 16], &payload);
    assert!(!driver.read_bulk(2).unwrap().version_supported());
}

#[test]
fn household_bulk_reads_check_the_correlation_id_and_length() {
    let mem = region();
    let mut driver = attached(&mem);
    let corr = Guid::parse_wire(CORR).unwrap();
    let payload = vec![7u8; 300];
    let (header, _) = put_host_bulk(&mem, 0, corr.to_dotnet_bytes(), &payload);
    let crc = header.crc32;
    assert_eq!(
        driver.read_bulk_for(Guid::EMPTY, 0, 300, crc),
        Err(BridgeError::BulkMismatch { slot: 0, what: "correlation id" })
    );
    assert_eq!(driver.read_bulk_for(corr, 0, 299, crc), Err(BridgeError::BulkMismatch { slot: 0, what: "length" }));
    assert_eq!(driver.read_bulk_for(corr, 0, 300, crc).unwrap(), payload);
}

#[test]
fn ctl_bulk_writes_follow_the_state_machine() {
    let mem = region();
    let mut driver = attached(&mem);
    let corr = Guid::parse_wire(CORR).unwrap();
    let (slot_index, crc) = driver.write_ctl_bulk(corr, b"probe bytes").unwrap();
    assert_eq!(slot_index, 0);
    let s0 = BulkSlot::new(0).unwrap().byte_offset();
    let h = BulkSlotHeader::from_bytes(&mem.peek(at(s0), BULK_SLOT_HEADER_SIZE)).unwrap();
    assert_eq!(h.state, BULK_STATE_CTL_READY);
    assert_eq!(h.correlation_id, corr.to_dotnet_bytes());
    assert_eq!((h.total_length, h.crc32), (11, crc32(b"probe bytes")));
    assert_eq!(crc, h.crc32);
    assert_eq!(mem.peek(at(s0 + BULK_SLOT_HEADER_SIZE), 11), b"probe bytes".to_vec());
    assert_eq!(driver.write_bulk(0, [0; 16], b"x"), Err(BridgeError::StaleStamp { slot: 0 }), "ready, not consumed");

    let too_big = vec![0u8; BULK_SLOT_PAYLOAD_MAX + 1];
    assert_eq!(driver.write_ctl_bulk(corr, &too_big), Err(BridgeError::BulkPayloadTooLarge { len: BULK_SLOT_PAYLOAD_MAX + 1 }));
    assert_eq!(driver.write_bulk(1, [0; 16], &too_big), Err(BridgeError::BulkPayloadTooLarge { len: BULK_SLOT_PAYLOAD_MAX + 1 }));
    for _ in 1..BULK_SLOT_COUNT {
        driver.write_ctl_bulk(corr, b"x").unwrap();
    }
    assert_eq!(driver.write_ctl_bulk(corr, b"x"), Err(BridgeError::NoWritableBulkSlot));
    mem.poke(at(s0), &BULK_STATE_CTL_CONSUMED.to_le_bytes());
    assert_eq!(driver.write_ctl_bulk(corr, b"again").unwrap().0, 0, "a ctl-consumed slot is reused");
}

#[test]
fn bulk_slots_need_a_v11_session() {
    let mem = region();
    let mut d = BridgeDriver::new(&mem, BASE, 100, Idle(0));
    assert_eq!(d.read_bulk(0), Err(BridgeError::NotConnected));
    assert_eq!(d.write_bulk(0, [0; 16], b"x"), Err(BridgeError::NotConnected));
}

/// A guest memory that fails the Nth write to one address, so a `write_bulk` can be landed on the
/// `Err`-after-the-first-write case its doc describes (review N6). Nothing else changes: every other
/// read and write is delegated to the region.
struct FailWriteAt<'a> {
    mem: &'a SimMemory,
    addr: u32,
    fail_on_nth: usize,
    hits: Cell<usize>,
}

impl GuestMemory for FailWriteAt<'_> {
    fn read(&self, addr: u32, buf: &mut [u8]) -> Result<(), BridgeError> {
        self.mem.read(addr, buf)
    }

    fn write(&self, addr: u32, data: &[u8]) -> Result<(), BridgeError> {
        if addr == self.addr {
            let nth = self.hits.get() + 1;
            self.hits.set(nth);
            if nth == self.fail_on_nth {
                return Err(BridgeError::WriteFailed { addr });
            }
        }
        self.mem.write(addr, data)
    }

    fn regions(&self) -> Result<Vec<RegionInfo>, BridgeError> {
        self.mem.regions()
    }

    fn has_exited(&self) -> bool {
        self.mem.has_exited()
    }
}

/// Review N6, answered against the fake mod: **a re-attach does not clear a stranded `CtlWriting` bulk
/// slot.** The mod's CONNECT path (`SimsOnlineServerHostMod/ServerHostMain.cs:646` `HandleConnect`)
/// clears host control slots (`BridgeMemoryBuffer.cs:513`), the ctl-server frames and the control header;
/// bulk slot state is only ever moved per-slot by `BridgeBulkBuffer` (`:41`, `:49`, `:110`) and starts
/// `Empty` because the region is one fresh zeroed array per mod process (`BridgeMemoryBuffer.cs:21`).
/// This test pins today's verified behaviour, it is not a ruling: if the re-attach is meant to reclaim the
/// slot (the liveness half of card 57), this is the test that changes.
#[test]
fn a_reattach_does_not_clear_a_stranded_ctl_writing_bulk_slot() {
    let mem = region();
    let s0 = BulkSlot::new(0).unwrap().byte_offset();
    let corr = Guid::parse_wire(CORR).unwrap();
    // `write_bulk` writes CtlWriting (1), the header (2), the payload elsewhere, then CtlReady (3) at the
    // same address: fail the third write to the slot base, after the first two landed.
    let flaky = FailWriteAt { mem: &mem, addr: at(s0), fail_on_nth: 3, hits: Cell::new(0) };
    let pacer = ModPacer { fake: FakeMod::new(&mem), pauses: 0 };
    let mut driver = BridgeDriver::new(&flaky, BASE, 100, pacer);
    assert!(driver.connect().unwrap().line.starts_with("HELLO|6|"));

    let err = driver.write_bulk(0, corr.to_dotnet_bytes(), b"probe bytes").unwrap_err();
    assert_eq!(err, BridgeError::WriteFailed { addr: at(s0) });
    assert_eq!(sender(&mem, s0), BULK_STATE_CTL_WRITING, "the slot is stranded, as the doc says");

    // A second CONNECT on the same driver.
    assert!(driver.connect().unwrap().line.starts_with("HELLO|6|"));
    assert_eq!(sender(&mem, s0), BULK_STATE_CTL_WRITING, "the re-attach does not clear it");

    // A new controller session attached to the same region, which is what the doc tells a caller to do.
    let mut fresh = attached(&mem);
    assert_eq!(sender(&mem, s0), BULK_STATE_CTL_WRITING, "nor does a fresh attach");
    assert_eq!(fresh.write_bulk(0, corr.to_dotnet_bytes(), b"x"), Err(BridgeError::StaleStamp { slot: 0 }));
    assert_eq!(fresh.write_ctl_bulk(corr, b"x").unwrap().0, 1, "the stranded slot is skipped, never reclaimed");

    // Only a new mod process recovers it: there the region is zeroed and the state is Empty.
    let restarted = region();
    assert_eq!(sender(&restarted, s0), BULK_STATE_EMPTY);
}

#[test]
fn a_process_that_exits_is_process_exited_not_a_retry() {
    let mem = region();
    let mut driver = attached(&mem);
    mem.set_exited(true);
    let before = mem.write_count();
    assert_eq!(driver.send_line("PING|x"), Err(BridgeError::ProcessExited));
    assert_eq!(driver.poll_line(), Err(BridgeError::ProcessExited));
    assert_eq!(driver.poll_connect(), Err(BridgeError::ProcessExited));
    assert_eq!(BridgeError::ProcessExited.name(), "process_exited");
    assert_eq!(mem.write_count(), before);
}

#[test]
fn a_connect_without_a_hello_ends_at_the_deadline_by_name() {
    let mem = region();
    let mut d = BridgeDriver::new(&mem, BASE, 100, Idle(0));
    d.set_connect_timeout(Duration::ZERO);
    let err = d.connect().unwrap_err();
    assert_eq!(err, BridgeError::RegionNotFound);
    assert_eq!(err.name(), "region_not_found");
    let empty = SimMemory::new();
    empty.map(0x0100_0000, 0x10000, true);
    assert_eq!(BridgeDriver::attach(&empty, Duration::ZERO, Idle(0)).err(), Some(BridgeError::RegionNotFound));
}

#[test]
fn the_beacon_is_seen_when_the_mod_rearms_it() {
    let mem = region();
    let driver = attached(&mem);
    assert!(!driver.is_find_marker_visible().unwrap(), "HELLO overwrote the marker");
    mem.poke(BASE, &control_marker());
    assert!(driver.is_find_marker_visible().unwrap());
    assert!(!driver.memory().has_exited());
    // N4: what the driver hands out is a read-only view (`GuestMemoryView`), so a caller outside the crate can
    // inspect the region but cannot publish a frame past the §E.3 refusal. Reads work from here; the negative
    // half (a `write` through the driver does not compile) is the `compile_fail` doctest on
    // `BridgeDriver::memory`, which a test crate cannot express.
    let mut marker = [0u8; MARKER_LEN];
    driver.memory().read(BASE, &mut marker).unwrap();
    assert_eq!(marker, control_marker());
}

/// Card 56 §E.3: a refused verb leaves the region untouched (no write, no consumed stamp on a waiting host
/// frame) and the mod sees nothing, whichever path it comes in by.
fn assert_refused(send: impl FnOnce(&mut Fake<'_>) -> Result<(), BridgeError>, wire: &'static str) {
    let mem = region();
    let mut driver = attached(&mem);
    host_write(&mem, slot(HOST_SLOT_BASE + 3), 1, "EVENT|InteractionStarted|1|2");
    let (writes, seen, seq) = (mem.write_count(), driver.pacer_mut().fake.seen.len(), driver.sequence());
    assert_eq!(send(&mut driver), Err(BridgeError::RefusedLegacyVerb(wire)));
    assert_eq!(BridgeError::RefusedLegacyVerb(wire).name(), format!("refused_legacy_verb:{wire}"));
    assert_eq!(mem.write_count(), writes, "nothing written");
    assert_eq!(driver.sequence(), seq, "no sequence spent");
    assert_eq!(sender(&mem, slot(HOST_SLOT_BASE + 3)), HOST_SENDER_ID, "the waiting host frame is not consumed");
    driver.pacer_mut().pause(1);
    assert_eq!(driver.pacer_mut().fake.seen.len(), seen, "the mod received nothing");
    driver.send_line(&format!("PING|{CORR}")).unwrap();
}

#[test]
fn the_typed_legacy_verbs_are_refused_by_the_driver() {
    let corr = Guid::parse_wire(CORR).unwrap();
    assert_refused(|d| d.send_command(&BridgeCommand::ApplyForceAlign { corr }), "APPLY_FORCE_ALIGN");
    assert_refused(|d| d.send_command(&BridgeCommand::ApplyInteractions { corr }), "APPLY_INTERACTIONS");
    assert_refused(|d| d.send_command(&BridgeCommand::ApplyInteractionQueue { corr }), "APPLY_INTERACTION_QUEUE");
}

#[test]
fn a_raw_command_naming_a_legacy_verb_is_refused() {
    let corr = Guid::parse_wire(CORR).unwrap();
    let raw = |name: &str| BridgeCommand::Raw { corr, name: name.into(), args: vec!["42".into()] };
    assert_refused(|d| d.send_command(&raw("APPLY_FORCE_ALIGN")), "APPLY_FORCE_ALIGN");
    assert_refused(|d| d.send_command(&raw("APPLY_INTERACTION_QUEUE")), "APPLY_INTERACTION_QUEUE");
    // Only the exact verb the mod dispatches on is refused; a Raw line with another name still goes out.
    let mem = region();
    let mut driver = attached(&mem);
    driver.send_command(&raw("APPLY_FORCE_ALIGN_PROBE")).unwrap();
    assert_eq!(sender(&mem, slot(CTL_SLOT_BASE)), SERVER_SENDER_ID);
}

#[test]
fn send_line_refuses_a_bare_legacy_line() {
    assert_refused(|d| d.send_line(&format!("APPLY_INTERACTIONS|{CORR}")), "APPLY_INTERACTIONS");
    assert_refused(|d| d.send_line(&format!("APPLY_FORCE_ALIGN|{CORR}|42|1,2,3")), "APPLY_FORCE_ALIGN");
}

#[test]
fn send_compat_line_refuses_a_bare_legacy_line() {
    assert_refused(|d| d.send_compat_line(&format!("APPLY_INTERACTION_QUEUE|{CORR}|42")), "APPLY_INTERACTION_QUEUE");
    assert_refused(|d| d.send_compat_line("APPLY_FORCE_ALIGN"), "APPLY_FORCE_ALIGN");
}
