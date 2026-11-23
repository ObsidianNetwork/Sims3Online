#![forbid(unsafe_code)]

use std::cell::Cell;
use std::time::Duration;
use simlink_bridge::driver::{BridgeDriver, Pacer};
use simlink_bridge::error::BridgeError;
use simlink_bridge::layout::*;
use simlink_bridge::memory::{GuestMemory, RegionInfo, SimMemory};
use simlink_bridge::text::{encode_text, Guid};
use simlink_bridge::verbs::BridgeCommand;
use simlink_headless_server::bridge_driver::{BridgeEndpoint, LiveBridge};

const BASE: u32 = 0x0100_0000;
const PRIME: &str = "0123456789abcdef0123456789abcdef";
const CHECKPOINT: &str = "fedcba9876543210fedcba9876543210";
const SYNC: &str = "11111111111111111111111111111111";

struct Memory {
    inner: SimMemory,
    failed_writes: Cell<usize>,
}
impl GuestMemory for Memory {
    fn read(&self, addr: u32, bytes: &mut [u8]) -> Result<(), BridgeError> { self.inner.read(addr, bytes) }
    fn write(&self, addr: u32, bytes: &[u8]) -> Result<(), BridgeError> {
        if bytes.get(SLOT_HEADER_SIZE..).is_some_and(|payload| payload.starts_with(b"SAVE_GAME|")) {
            self.failed_writes.set(self.failed_writes.get() + 1);
            return Err(BridgeError::WriteFailed { addr });
        }
        self.inner.write(addr, bytes)
    }
    fn regions(&self) -> Result<Vec<RegionInfo>, BridgeError> { self.inner.regions() }
    fn has_exited(&self) -> bool { self.inner.has_exited() }
}
fn host(mem: &Memory, seq: i32) {
    let at = ControlSlot::host(0).unwrap().byte_offset();
    let line = format!("OK|{PRIME}");
    let header = ControlSlotHeader { sender_id: HOST_SENDER_ID, seq, length: line.len() as i32 };
    mem.inner.poke(BASE + at as u32, &header.to_bytes());
    mem.inner.poke(BASE + (at + SLOT_HEADER_SIZE) as u32, line.as_bytes());
}
struct Pump<'a> { mem: &'a Memory, replied: bool }
impl Pacer for Pump<'_> {
    fn pause(&mut self, _: u64) {
        if !self.replied {
            let at = ControlSlot::ctl(7).unwrap().byte_offset();
            let header = ControlSlotHeader::from_bytes(&self.mem.inner.peek(BASE + at as u32, SLOT_HEADER_SIZE)).unwrap();
            host(self.mem, header.seq);
            self.replied = true;
        }
    }
}

#[test]
fn queued_checkpoint_write_failure_reaches_its_sender_during_another_command() {
    let inner = SimMemory::new();
    inner.map(BASE, TOTAL_REGION_SIZE, true);
    inner.poke(BASE, &ControlRegionHeader::v11().to_bytes());
    let mem = Memory { inner, failed_writes: Cell::new(0) };
    let mut driver = BridgeDriver::new(&mem, BASE, 100, Pump { mem: &mem, replied: false });
    driver.apply_connect_hello(&format!(
        "HELLO|6||10|{}|{}", encode_text("fixture"), encode_text(DEFAULT_HELLO_CAPS)
    )).unwrap();
    for _ in 0..7 {
        driver.send_line(&format!("PING|{PRIME}")).unwrap();
        host(&mem, driver.sequence());
        assert_eq!(driver.poll_line().unwrap(), Some(format!("OK|{PRIME}")));
    }
    driver.send_line(&format!("PING|{PRIME}")).unwrap();
    let mut endpoint = LiveBridge::from_driver(driver);
    assert_eq!(endpoint.begin_checkpoint_command(&BridgeCommand::SaveGame {
        corr: Guid::parse_wire(CHECKPOINT).unwrap(), save_name: "Review.sims3".into(),
    }, Duration::from_secs(30)).unwrap(), None);
    let error = endpoint.command(&BridgeCommand::Raw {
        corr: Guid::parse_wire(SYNC).unwrap(), name: "PING".into(), args: vec![],
    }).unwrap_err();
    assert!(error.to_string().contains(CHECKPOINT), "transport failure identifies the command that failed");
    let checkpoint_error = endpoint.poll_checkpoint_command().expect_err("the original sender needs its own failure");
    assert!(checkpoint_error.to_string().contains(CHECKPOINT));
    assert!(checkpoint_error.to_string().contains("write_failed"));
    assert_eq!(mem.failed_writes.get(), 1, "the failed native write is never retried");
}
