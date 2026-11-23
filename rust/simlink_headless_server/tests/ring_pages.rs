#![forbid(unsafe_code)]
//! The SimLink ring behind the real `LiveBridge`: startup handshake, lane-6 page delivery, queue cap, discard and the
//! fallbacks, offline. The "game" is the fake mod memory of `bridge_replies.rs` plus an in-memory ring section.

use std::cell::RefCell;
use std::time::Duration;

use simlink_bridge::driver::{BridgeDriver, Pacer};
use simlink_bridge::layout::*;
use simlink_bridge::memory::SimMemory;
use simlink_bridge::ring::{off, MemorySection, Registration, RingError, RingReader};
use simlink_bridge::text::encode_text;
use simlink_headless_server::bridge_driver::{BridgeEndpoint, BridgeGameDriver, LiveBridge};
use simlink_headless_server::ring_pages::{PageSource, RingPages, MAX_QUEUED_PAGES, PAGE_LANE};
use simlink_headless_server::GameDriver;

const BASE: u32 = 0x0100_0000;
const PID: u32 = 4321;

fn address(offset: usize) -> u32 { BASE + offset as u32 }
fn host(mem: &SimMemory, offset: usize, seq: i32, line: &str) {
    mem.poke(address(offset), &HOST_WRITING_SENDER_ID.to_le_bytes());
    mem.poke(address(offset + 8), &seq.to_le_bytes());
    mem.poke(address(offset + 12), &(line.len() as i32).to_le_bytes());
    mem.poke(address(offset + SLOT_HEADER_SIZE), line.as_bytes());
    mem.poke(address(offset), &HOST_SENDER_ID.to_le_bytes());
}
fn memory() -> SimMemory {
    let mem = SimMemory::new();
    mem.map(BASE - 0x1000, TOTAL_REGION_SIZE + 0x2000, true);
    mem.poke(BASE, &ControlRegionHeader::v11().to_bytes());
    mem.poke(address(bulk_region_base_offset()), &BulkRegionHeader::v11().to_bytes());
    mem
}

/// A mod that answers every `SACS_RECORDER <sub>` with `sidecar_reply` for `sidecar-on` and an empty report otherwise,
/// and logs the lines it received.
struct Mod<'a> {
    mem: &'a SimMemory,
    multi: bool,
    sidecar_reply: &'static str,
    log: &'a RefCell<Vec<String>>,
}
impl Mod<'_> {
    fn step(&mut self) {
        let slots: Vec<_> = if self.multi {
            (CTL_SLOT_BASE..CTL_SLOT_BASE + CTL_SLOT_COUNT).map(|i| slot_byte_offset(i).unwrap()).collect()
        } else { vec![COMPAT_SLOT_OFFSET] };
        for offset in slots {
            let header = ControlSlotHeader::from_bytes(&self.mem.peek(address(offset), SLOT_HEADER_SIZE)).unwrap();
            if header.sender_id != SERVER_SENDER_ID || header.length <= 0 { continue; }
            let line = String::from_utf8(self.mem.peek(address(offset + SLOT_HEADER_SIZE), header.length as usize)).unwrap();
            self.mem.poke(address(offset), &CONSUMED_SENDER_ID.to_le_bytes());
            let fields: Vec<_> = line.split('|').collect();
            if fields[0] == "CONNECT" {
                let hello = format!("HELLO|6||7|{}|{}", encode_text("fixture"), encode_text(DEFAULT_HELLO_CAPS));
                host(self.mem, COMPAT_SLOT_OFFSET, header.seq, &hello);
                self.multi = true;
                continue;
            }
            self.log.borrow_mut().push(line.clone());
            let text = if fields.get(2) == Some(&"sidecar-on") { self.sidecar_reply } else { "" };
            let reply = format!("AUTONOMY_PROBE|{}|ok|{}", fields[1], encode_text(text));
            host(self.mem, slot_byte_offset(HOST_SLOT_BASE).unwrap(), header.seq, &reply);
        }
    }
}
impl Pacer for Mod<'_> {
    fn pause(&mut self, _: u64) { self.step(); }
}
fn attached<'a>(mem: &'a SimMemory, sidecar_reply: &'static str, log: &'a RefCell<Vec<String>>) -> LiveBridge<&'a SimMemory, Mod<'a>> {
    let mut driver = BridgeDriver::new(mem, BASE, 100, Mod { mem, multi: false, sidecar_reply, log });
    driver.connect().unwrap();
    LiveBridge::from_driver(driver)
}
fn section() -> MemorySection { MemorySection::new(1 << 16, PID) }
fn open(section: &MemorySection) -> Result<RingReader<MemorySection>, RingError> {
    RingReader::open(section.clone(), Some(PID))
}
const WAIT: Duration = Duration::from_secs(1);
fn sidecar_on_sent(log: &RefCell<Vec<String>>) -> usize {
    log.borrow().iter().filter(|line| line.starts_with("SACS_RECORDER") && line.contains("sidecar-on")).count()
}

// ---- startup ----------------------------------------------------------------------------------------------------

#[test]
fn startup_registers_sends_sidecar_on_and_delivers_only_lane_six_pages_in_order() {
    let mem = memory();
    let log = RefCell::new(Vec::new());
    let mut driver = BridgeGameDriver::new(attached(&mem, "sidecar=on;lines=0", &log));
    let ring = section();
    ring.set_registration(Registration::Succeed);
    assert!(!driver.ring_available(), "no ring before the handshake");
    assert_eq!(driver.start_ring(open(&ring), WAIT, true), Ok(()));
    assert_eq!(ring.control_signals(), 1, "RequestRegistration signalled .control once");
    assert_eq!(sidecar_on_sent(&log), 1);
    assert!(driver.ring_available());

    assert!(ring.publish(PAGE_LANE, 1, 10, "seq=3;more=0\n3|A|55|first"));
    assert!(ring.publish(1, 2, 11, "3|A|55|mirrored-line"));
    assert!(ring.publish(2, 3, 12, "3|A|55|mirrored-line-2"));
    assert!(ring.publish(PAGE_LANE, 4, 13, "seq=4;more=0\n4|A|55|second"));
    assert_eq!(driver.presentation_pages().unwrap(),
        ["seq=3;more=0\n3|A|55|first", "seq=4;more=0\n4|A|55|second"]);
    assert!(driver.presentation_pages().unwrap().is_empty(), "taken pages are not delivered twice");
    assert_eq!(ring.peek_u64(off::READ_POS), ring.peek_u64(off::WRITE_POS), "the ring was freed");
}

#[test]
fn without_the_shadow_switch_sidecar_on_is_not_sent_but_pages_still_flow() {
    let mem = memory();
    let log = RefCell::new(Vec::new());
    let mut driver = BridgeGameDriver::new(attached(&mem, "sidecar=on", &log));
    let ring = section();
    ring.set_registration(Registration::Succeed);
    assert_eq!(driver.start_ring(open(&ring), WAIT, false), Ok(()));
    assert_eq!(sidecar_on_sent(&log), 0);
    ring.publish(PAGE_LANE, 1, 1, "page");
    assert_eq!(driver.presentation_pages().unwrap(), ["page"]);
}

// ---- fallbacks: the ring stays unavailable and the driver keeps answering -------------------------------------------

fn assert_stays_on_dumps(driver: &mut impl GameDriver, ring: &MemorySection) {
    assert!(!driver.ring_available());
    ring.publish(PAGE_LANE, 1, 1, "never delivered");
    assert!(driver.presentation_pages().unwrap().is_empty(), "an uninstalled ring delivers nothing");
    assert_eq!(driver.discard_pages().unwrap(), 0);
}

#[test]
fn a_missing_section_is_ring_unavailable() {
    let mem = memory();
    let log = RefCell::new(Vec::new());
    let mut driver = BridgeGameDriver::new(attached(&mem, "sidecar=on", &log));
    let reason = driver.start_ring::<MemorySection>(Err(RingError::NoSection("no section SimsOnline.SimLink.1".into())), WAIT, true)
        .unwrap_err();
    assert!(reason.starts_with("no_section"), "{reason}");
    assert_eq!(sidecar_on_sent(&log), 0, "nothing is asked of the mod without a ring");
    assert_stays_on_dumps(&mut driver, &section());
}

#[test]
fn a_failed_registration_is_ring_unavailable_before_sidecar_on() {
    let mem = memory();
    let log = RefCell::new(Vec::new());
    let mut driver = BridgeGameDriver::new(attached(&mem, "sidecar=on", &log));
    let ring = section();
    ring.set_registration(Registration::Fail(1));
    let reason = driver.start_ring(open(&ring), WAIT, true).unwrap_err();
    assert_eq!(reason, "registration_failed: state=3 error=1");
    assert_eq!(sidecar_on_sent(&log), 0);
    assert_stays_on_dumps(&mut driver, &ring);
}

#[test]
fn a_registration_timeout_is_ring_unavailable() {
    let mem = memory();
    let log = RefCell::new(Vec::new());
    let mut driver = BridgeGameDriver::new(attached(&mem, "sidecar=on", &log));
    let ring = section(); // Registration::Ignore: nobody answers
    let reason = driver.start_ring(open(&ring), Duration::from_millis(40), true).unwrap_err();
    assert_eq!(reason, "registration_failed: state=1 error=0");
    assert_stays_on_dumps(&mut driver, &ring);
}

#[test]
fn a_refused_sidecar_on_is_ring_unavailable() {
    for reply in ["sidecar=off;reason=busy", ""] {
        let mem = memory();
        let log = RefCell::new(Vec::new());
        let mut driver = BridgeGameDriver::new(attached(&mem, reply, &log));
        let ring = section();
        ring.set_registration(Registration::Succeed);
        let reason = driver.start_ring(open(&ring), WAIT, true).unwrap_err();
        assert_eq!(reason, format!("sidecar_on_refused: {reply}"));
        assert_eq!(sidecar_on_sent(&log), 1, "it was asked, and refused");
        assert_stays_on_dumps(&mut driver, &ring);
    }
}

#[test]
fn a_header_for_another_process_is_ring_unavailable() {
    let mem = memory();
    let log = RefCell::new(Vec::new());
    let mut driver = BridgeGameDriver::new(attached(&mem, "sidecar=on", &log));
    let ring = section();
    let reason = driver.start_ring(RingReader::open(ring.clone(), Some(PID + 1)), WAIT, true).unwrap_err();
    assert!(reason.starts_with("bad_header"), "{reason}");
}

// ---- the page queue ---------------------------------------------------------------------------------------------

#[test]
fn queue_overflow_is_persistent_failure_without_partial_pages() {
    let ring = MemorySection::new(1 << 18, PID);
    let mut pages = RingPages::new(open(&ring).unwrap());
    for n in 0..MAX_QUEUED_PAGES + 904 {
        assert!(ring.publish(PAGE_LANE, n as i32, 0, &format!("p{n}")), "ring has room for page {n}");
    }
    let taken = pages.take_pages();
    assert!(taken.is_empty());
    assert!(!pages.available());
    assert!(pages.failure().unwrap().contains("capacity"));
    assert_eq!(pages.pages_received(), (MAX_QUEUED_PAGES + 904) as u64);
    assert_eq!(pages.pages_discarded(),0,"active loss is not an intentional discard");
    assert!(pages.take_pages().is_empty());
}

#[test]
fn discarding_a_stale_backlog_larger_than_the_live_budget_is_not_a_failure() {
    let ring = MemorySection::new(1 << 18, PID);
    let mut pages = RingPages::new(open(&ring).unwrap());
    let stale = MAX_QUEUED_PAGES + 904;
    for n in 0..stale {
        assert!(ring.publish(PAGE_LANE, n as i32, 0, &format!("p{n}")), "ring has room for page {n}");
    }
    assert_eq!(pages.discard_pages(), stale);
    assert!(pages.available(), "{:?}", pages.failure());
    assert_eq!(pages.pages_discarded(), stale as u64);
    assert!(ring.publish(PAGE_LANE, 1, 0, "fresh"));
    assert_eq!(pages.take_pages(), vec!["fresh".to_string()]);
}

#[test]
fn discard_drains_then_drops_queued_pages_and_counts_them() {
    let ring = section();
    let mut pages = RingPages::new(open(&ring).unwrap());
    for n in 0..3 {
        ring.publish(PAGE_LANE, n, 0, "stale");
    }
    ring.publish(1, 9, 0, "mirror");
    assert_eq!(pages.discard_pages(), 3, "the unread ring was drained first, so the stale pages are counted");
    assert!(pages.take_pages().is_empty());
    assert_eq!((pages.pages_received(), pages.pages_discarded()), (3, 3));
    assert!(pages.report().contains("ring_mirrored_ignored=1"));
    ring.publish(PAGE_LANE, 10, 0, "fresh");
    assert_eq!(pages.take_pages(), ["fresh"]);
}

#[test]
fn a_corrupt_ring_stops_delivering_and_reports_unavailable() {
    let mem = memory();
    let log = RefCell::new(Vec::new());
    let mut driver = BridgeGameDriver::new(attached(&mem, "sidecar=on", &log));
    let ring = section();
    ring.set_registration(Registration::Succeed);
    driver.start_ring(open(&ring), WAIT, false).unwrap();
    ring.publish(PAGE_LANE, 1, 0, "good");
    assert_eq!(driver.presentation_pages().unwrap(), ["good"]);
    // The producer writes a record of size 0 and publishes it.
    let write = ring.peek_u64(off::WRITE_POS);
    ring.poke(256 + write as usize, &[0u8; 8]);
    ring.poke_u64(off::WRITE_POS, write + 8);
    assert!(driver.presentation_pages().is_err(), "active corruption must be an explicit driver failure");
    assert!(!driver.ring_available(), "the ring is abandoned");
    ring.publish(PAGE_LANE, 2, 0, "after");
    assert!(driver.presentation_pages().is_err(), "persistent failure must never masquerade as idle");
    let _ = driver.endpoint().ring_available();
}
