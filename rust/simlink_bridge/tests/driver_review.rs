#![forbid(unsafe_code)]

use std::cell::{Cell, RefCell};
use simlink_bridge::driver::{BridgeDriver, Pacer};
use simlink_bridge::error::BridgeError;
use simlink_bridge::layout::*;
use simlink_bridge::memory::{GuestMemory, RegionInfo, SimMemory};
use simlink_bridge::text::encode_text;

const BASE: u32 = 0x0100_0000;
const CORR: &str = "0123456789abcdef0123456789abcdef";
const NEXT: &str = "fedcba9876543210fedcba9876543210";

struct Memory {
    inner: SimMemory,
    read_error: RefCell<Option<(u32, BridgeError)>>,
    deny_write: Cell<bool>,
    attempts: Cell<usize>,
}
impl Memory {
    fn new() -> Self {
        let inner = SimMemory::new();
        inner.map(BASE, TOTAL_REGION_SIZE, true);
        inner.poke(BASE, &ControlRegionHeader::v11().to_bytes());
        Self { inner, read_error: RefCell::new(None), deny_write: Cell::new(false), attempts: Cell::new(0) }
    }
}
impl GuestMemory for Memory {
    fn read(&self, addr: u32, bytes: &mut [u8]) -> Result<(), BridgeError> {
        if let Some((target, error)) = &*self.read_error.borrow() {
            if addr == *target {
                if let BridgeError::ShortRead { got, .. } = error {
                    let copied = (*got).min(bytes.len());
                    self.inner.read(addr, &mut bytes[..copied])?;
                }
                return Err(error.clone());
            }
        }
        self.inner.read(addr, bytes)
    }
    fn write(&self, addr: u32, bytes: &[u8]) -> Result<(), BridgeError> {
        self.attempts.set(self.attempts.get() + 1);
        if self.deny_write.get() && bytes.len() > SLOT_HEADER_SIZE {
            return Err(BridgeError::WriteFailed { addr });
        }
        self.inner.write(addr, bytes)
    }
    fn regions(&self) -> Result<Vec<RegionInfo>, BridgeError> { self.inner.regions() }
    fn has_exited(&self) -> bool { self.inner.has_exited() }
}
struct Idle;
impl Pacer for Idle { fn pause(&mut self, _: u64) { panic!("submission must not block"); } }
type Driver<'a> = BridgeDriver<&'a Memory, Idle>;

fn offset(index: usize) -> usize { ControlSlot::ctl(index).unwrap().byte_offset() }
fn header(mem: &Memory, at: usize) -> ControlSlotHeader {
    ControlSlotHeader::from_bytes(&mem.inner.peek(BASE + at as u32, SLOT_HEADER_SIZE)).unwrap()
}
fn host(mem: &Memory, seq: i32, line: &str) {
    let at = ControlSlot::host(0).unwrap().byte_offset();
    let h = ControlSlotHeader { sender_id: HOST_SENDER_ID, seq, length: line.len() as i32 };
    mem.inner.poke(BASE + at as u32, &h.to_bytes());
    mem.inner.poke(BASE + (at + SLOT_HEADER_SIZE) as u32, line.as_bytes());
}
fn hello() -> String {
    format!("HELLO|6||10|{}|{}", encode_text("fixture"), encode_text(DEFAULT_HELLO_CAPS))
}
fn driver(mem: &Memory) -> Driver<'_> {
    let mut driver = BridgeDriver::new(mem, BASE, 100, Idle);
    driver.apply_connect_hello(&hello()).unwrap();
    driver
}
// Exact valid-ctl path: physical scan restarts at zero; a single global cursor;
// no consumed stamp (BridgeMemoryBuffer.cs:172-223).
fn dequeue(mem: &Memory, last: &mut i32) -> Option<(i32, String)> {
    for i in 0..CTL_SLOT_COUNT {
        let at = offset(i);
        let h = header(mem, at);
        if h.sender_id != SERVER_SENDER_ID || h.seq <= *last { continue; }
        assert!(h.length > 0 && h.length as usize <= SLOT_PAYLOAD_MAX);
        *last = h.seq;
        let line = String::from_utf8(mem.inner.peek(BASE + (at + SLOT_HEADER_SIZE) as u32, h.length as usize)).unwrap();
        return Some((h.seq, line));
    }
    None
}
fn at_wrap(mem: &Memory) -> (Driver<'_>, i32) {
    let mut driver = driver(mem);
    let mut last = 100;
    for _ in 0..CTL_SLOT_COUNT - 1 {
        driver.send_line(&format!("PING|{CORR}")).unwrap();
        let (seq, _) = dequeue(mem, &mut last).unwrap();
        host(mem, seq, &format!("OK|{CORR}"));
        assert_eq!(driver.poll_line().unwrap(), Some(format!("OK|{CORR}")));
    }
    driver.send_line(&format!("SAVE_GAME|{CORR}|{}", encode_text("Review.sims3"))).unwrap();
    let seq = driver.sequence();
    (driver, seq)
}

#[test]
fn review_b1_queue_full_rejects_without_spending_sequence_or_losing_accepted_commands() {
    let mem = Memory::new();
    let (mut driver, n) = at_wrap(&mem);
    let mut accepted = Vec::new();
    for i in 0..256 {
        let line = format!("PING|{i:032x}");
        driver.send_line(&line).unwrap();
        accepted.push((driver.sequence(), line));
    }
    let (sequence, writes) = (driver.sequence(), mem.inner.write_count());
    let error = driver.send_line(&format!("PING|{NEXT}")).expect_err("the FIFO must be bounded");
    assert_eq!(error.name(), "command_queue_full");
    assert!(error.to_string().contains("256"));
    assert_eq!((driver.sequence(), mem.inner.write_count()), (sequence, writes));
    let mut last = n - 1;
    assert_eq!(dequeue(&mem, &mut last).unwrap().0, n);
    host(&mem, n, &format!("OK|{CORR}"));
    driver.poll_line().unwrap();
    let mut delivered = Vec::new();
    for _ in 0..accepted.len() {
        let frame = dequeue(&mem, &mut last).expect("accepted queue head must eventually publish");
        host(&mem, frame.0, &format!("OK|{}", frame.1.split('|').nth(1).unwrap()));
        delivered.push(frame);
        while driver.poll_line().unwrap().is_some() {}
    }
    assert_eq!(delivered, accepted);
    assert_eq!(dequeue(&mem, &mut last), None);
}

#[test]
fn review_b2_high_slot_read_errors_fail_closed_and_reach_the_submitter() {
    for error in [
        BridgeError::AccessDenied { os_code: 5 },
        BridgeError::ShortRead { addr: BASE + offset(7) as u32, want: SLOT_HEADER_SIZE, got: 8 },
        BridgeError::WriteFailed { addr: BASE + offset(7) as u32 },
        BridgeError::ProcessExited,
    ] {
        let mem = Memory::new();
        let (mut driver, _) = at_wrap(&mem);
        *mem.read_error.borrow_mut() = Some((BASE + offset(7) as u32, error.clone()));
        let before = (driver.sequence(), mem.inner.write_count(), mem.inner.peek(BASE + offset(7) as u32, SLOT_SIZE));
        assert_eq!(driver.send_line(&format!("PING|{NEXT}")), Err(error));
        assert_eq!((driver.sequence(), mem.inner.write_count()), (before.0, before.1));
        assert_eq!(mem.inner.peek(BASE + offset(7) as u32, SLOT_SIZE), before.2);
    }
}

#[test]
fn review_b2_partial_compat_header_cannot_be_treated_as_empty() {
    let mem = Memory::new();
    let mut driver = driver(&mem);
    driver.send_compat_line(&format!("PING|{CORR}")).unwrap();
    let error = BridgeError::ShortRead { addr: BASE, want: SLOT_HEADER_SIZE, got: 8 };
    *mem.read_error.borrow_mut() = Some((BASE, error.clone()));
    let before = (driver.sequence(), mem.inner.write_count());
    assert_eq!(driver.send_line(&format!("PING|{NEXT}")), Err(error));
    assert_eq!((driver.sequence(), mem.inner.write_count()), before);
    assert_ne!(header(&mem, offset(0)).sender_id, SERVER_SENDER_ID);
}

#[test]
fn review_b2_layout_read_error_cannot_downgrade_to_a_compat_write() {
    let mem = Memory::new();
    let mut driver = BridgeDriver::new(&mem, BASE, 100, Idle);
    let error = BridgeError::AccessDenied { os_code: 5 };
    *mem.read_error.borrow_mut() = Some((BASE + LAYOUT_VERSION_OFFSET as u32, error.clone()));
    assert_eq!(driver.send_line(&format!("PING|{CORR}")), Err(error));
    assert_eq!(driver.sequence(), 100);
    assert_eq!(mem.inner.write_count(), 0);
}

#[test]
fn review_b2_nonwrap_unknown_stamps_keep_the_oracle_fallback_and_write_error() {
    for deny in [false, true] {
        let mem = Memory::new();
        let mut driver = driver(&mem);
        for i in 0..CTL_SLOT_COUNT {
            mem.inner.poke(BASE + offset(i) as u32, &HOST_SENDER_ID.to_le_bytes());
        }
        mem.deny_write.set(deny);
        let result = driver.send_line(&format!("PING|{CORR}"));
        if deny { assert_eq!(result.unwrap_err().name(), "write_failed"); }
        else {
            result.unwrap();
            assert_eq!(header(&mem, offset(0)).sender_id, SERVER_SENDER_ID);
        }
    }
}

#[test]
fn review_b3_only_replies_to_published_commands_release_the_wrap() {
    for (delta, id, kind) in [(1000, CORR, "OK"), (1, NEXT, "OK"), (0, NEXT, "OK"),
        (0, CORR, "EVENT"), (0, "00000000000000000000000000000000", "ERROR")] {
        let mem = Memory::new();
        let (mut driver, n) = at_wrap(&mem);
        driver.send_line(&format!("PING|{NEXT}")).unwrap();
        let line = format!("{kind}|{id}|{}", encode_text("not dequeue evidence"));
        host(&mem, n + delta, &line);
        let bytes = mem.inner.peek(BASE + offset(0) as u32, SLOT_SIZE);
        assert_eq!(driver.poll_line().unwrap(), Some(line));
        assert_eq!(driver.poll_line().unwrap(), None);
        assert_eq!(mem.inner.peek(BASE + offset(0) as u32, SLOT_SIZE), bytes);
        let mut last = n - 1;
        assert_eq!(dequeue(&mem, &mut last).unwrap().0, n);
        host(&mem, n, &format!("OK|{CORR}"));
        assert_eq!(driver.poll_line().unwrap(), Some(format!("OK|{CORR}")));
        assert_eq!(dequeue(&mem, &mut last), Some((n + 1, format!("PING|{NEXT}"))));
    }
}

#[test]
fn review_b4_host_slot_hello_acknowledges_connect_including_after_seeding() {
    for seed_from_region in [false, true] {
        let mem = Memory::new();
        let mut driver = BridgeDriver::new(&mem, BASE, 100, Idle);
        if seed_from_region {
            let h = ControlSlotHeader { sender_id: SERVER_SENDER_ID, seq: 5000, length: 1 };
            mem.inner.poke(BASE + offset(7) as u32, &h.to_bytes());
            driver.seed_sequence_above_region_max();
            assert_eq!(driver.sequence(), 6024);
        }
        driver.begin_connect().unwrap();
        let connect = driver.sequence();
        host(&mem, connect, &hello());
        assert!(driver.poll_connect().unwrap().is_some());
        assert_eq!(header(&mem, COMPAT_SLOT_OFFSET).sender_id, SERVER_SENDER_ID);
        driver.send_line(&format!("PING|{CORR}")).unwrap();
        assert_eq!((header(&mem, offset(0)).sender_id, header(&mem, offset(0)).seq), (SERVER_SENDER_ID, connect + 1));
    }
}

#[test]
fn review_b5_one_poll_publishes_the_frames_its_reply_unblocked() {
    let mem = Memory::new();
    let (mut driver, n) = at_wrap(&mem);
    driver.send_line(&format!("PING|{NEXT}")).unwrap();
    let mut last = n - 1;
    assert_eq!(dequeue(&mem, &mut last).unwrap().0, n);
    host(&mem, n, &format!("OK|{CORR}"));
    assert_eq!(driver.poll_line().unwrap(), Some(format!("OK|{CORR}")));
    assert_eq!(dequeue(&mem, &mut last), Some((n + 1, format!("PING|{NEXT}"))));
}

#[test]
fn review_deferred_write_failure_keeps_the_consumed_reply_and_original_identity() {
    let mem = Memory::new();
    let (mut driver, n) = at_wrap(&mem);
    driver.send_line(&format!("PING|{NEXT}")).unwrap();
    host(&mem, n, &format!("OK|{CORR}"));
    mem.deny_write.set(true);
    assert_eq!(driver.poll_line().unwrap(), Some(format!("OK|{CORR}")));
    let attempts = mem.attempts.get();
    let error = driver.poll_line().unwrap_err();
    assert_eq!(error.name(), "command_write_failed");
    assert!(error.to_string().contains(NEXT));
    assert!(error.to_string().contains(&(n + 1).to_string()));
    assert_eq!(driver.poll_line(), Err(error));
    assert_eq!(mem.attempts.get(), attempts, "a failed publication is never retried");
}

#[test]
fn review_n1_sequence_exhaustion_is_named_and_has_no_side_effects() {
    let mem = Memory::new();
    let mut driver = BridgeDriver::new(&mem, BASE, i32::MAX, Idle);
    driver.apply_connect_hello(&hello()).unwrap();
    assert_eq!(driver.send_line(&format!("PING|{CORR}")).unwrap_err().name(), "sequence_exhausted");
    assert_eq!(driver.begin_connect().unwrap_err().name(), "sequence_exhausted");
    assert_eq!(driver.sequence(), i32::MAX);
    assert_eq!(mem.inner.write_count(), 0);
}
