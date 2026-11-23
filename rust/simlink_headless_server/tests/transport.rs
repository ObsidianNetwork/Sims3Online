#![forbid(unsafe_code)]
//! Card 57 §A / §J.6: the transport behind the session, the `GameDriver` seam, the fake, and the
//! `GuestMem32`/`GuestMem32Mut` adapter over a process. Everything here runs offline: the fake
//! driver and a fake process stand in for TS3W, and no process is ever attached.

use sim96_kernel_cleanroom::guest_mem::{GuestMem32, GuestMem32Mut};
use simlink_headless_server::guest_mem32::{GuestProcess, ProcessGuest32};
use simlink_headless_server::messages::Command;
use simlink_headless_server::session::{ConnId, Server};
use simlink_headless_server::transport::{FakeDriver, GameDriver};

fn cmd(corr: u64, command_id: u64, player: i32, verb: &str) -> Command {
    Command {
        corr,
        verb: verb.to_string(),
        command_id,
        player_id: player,
        args: vec![],
    }
}

// ---- §J.6: the fake driver drives the session --------------------------------------

#[test]
fn fake_driver_answers_a_command_through_the_session() {
    let mut srv = Server::new(1);
    let mut driver = FakeDriver::new();
    let c = ConnId(1);
    srv.connect(c);
    srv.join(c, 7);
    let out = srv
        .handle_command(c, &cmd(1, 50, 7, "ClaimSim"), |k| {
            driver.adjudicate(k).unwrap()
        })
        .unwrap();
    assert_eq!(String::from_utf8(out.reply).unwrap(), "ACK|1|ClaimSim|ok");
    assert_eq!(driver.sent_count(), 1);
    // The seam drove the fake exactly once with this command. What `FakeDriver` records is the front
    // door's own public re-encoding of the `Command`, kept for test inspection only; it is NOT a
    // claim about the line the game side sees — the game-side line is the card 57 §D.1 class-B /
    // class-K bridge translation the pending `BridgeGameDriver` impl must build (F5). So assert the
    // command identity reached the seam, not the raw public `CMD|…` wire shape.
    let recorded = String::from_utf8(driver.sent[0].clone()).unwrap();
    assert!(recorded.contains("|ClaimSim|"), "seam received: {recorded}");
}

#[test]
fn a_replayed_command_id_never_reaches_the_driver() {
    let mut srv = Server::new(1);
    let mut driver = FakeDriver::new();
    driver.queue_reply(b"CUSTOM-REPLY");
    let c = ConnId(2);
    srv.connect(c);
    srv.join(c, 3);
    let first = srv
        .handle_command(c, &cmd(1, 9, 3, "QueueInteraction"), |k| {
            driver.adjudicate(k).unwrap()
        })
        .unwrap();
    assert_eq!(&first.reply, b"CUSTOM-REPLY");
    // Same commandId: the window replays the original bytes; the driver is not called again.
    let replay = srv
        .handle_command(c, &cmd(1, 9, 3, "QueueInteraction"), |k| {
            driver.adjudicate(k).unwrap()
        })
        .unwrap();
    assert!(replay.replayed);
    assert_eq!(&replay.reply, b"CUSTOM-REPLY");
    assert_eq!(driver.sent_count(), 1, "the driver ran twice for one commandId");
}

#[test]
fn fake_driver_hands_back_queued_event_lines() {
    let mut driver = FakeDriver::new();
    driver.queue_event("EV|1|10|0|Tick");
    driver.queue_event("EV|2|11|0|Tick");
    let events = driver.poll_event_lines().unwrap();
    assert_eq!(events.len(), 2);
    assert_eq!(String::from_utf8(events[0].clone()).unwrap(), "EV|1|10|0|Tick");
    assert!(driver.poll_event_lines().unwrap().is_empty());
}

// ---- §A: the GuestMem32 adapter over a process (a fake, offline) --------------------

/// An in-memory 32-bit address space standing in for the attached TS3W process.
struct FakeProcess {
    base: u32,
    bytes: Vec<u8>,
}

impl FakeProcess {
    fn new(base: u32, len: usize) -> Self {
        Self { base, bytes: vec![0; len] }
    }
}

impl GuestProcess for FakeProcess {
    fn read_guest(&self, addr: u32, buf: &mut [u8]) -> Option<()> {
        let off = usize::try_from(addr.checked_sub(self.base)?).ok()?;
        let end = off.checked_add(buf.len())?;
        if end > self.bytes.len() {
            return None;
        }
        buf.copy_from_slice(&self.bytes[off..end]);
        Some(())
    }

    fn write_guest(&mut self, addr: u32, bytes: &[u8]) -> Option<()> {
        let off = usize::try_from(addr.checked_sub(self.base)?).ok()?;
        let end = off.checked_add(bytes.len())?;
        if end > self.bytes.len() {
            return None;
        }
        self.bytes[off..end].copy_from_slice(bytes);
        Some(())
    }
}

#[test]
fn guest_mem32_adapter_reads_and_writes_words() {
    let mut guest = ProcessGuest32::new(FakeProcess::new(0x1000, 8));
    assert_eq!(guest.write_u32(0x1000, 0xDEAD_BEEF), Some(()));
    assert_eq!(guest.read_u32(0x1000), Some(0xDEAD_BEEF));
    // Little-endian signed view.
    assert_eq!(guest.write_u32(0x1004, 0xFFFF_FFFF), Some(()));
    assert_eq!(guest.read_i32(0x1004), Some(-1));
}

#[test]
fn guest_mem32_adapter_returns_none_off_the_end() {
    let guest = ProcessGuest32::new(FakeProcess::new(0x1000, 4));
    // Fully mapped word at the base reads Some.
    assert_eq!(guest.read_u32(0x1000), Some(0));
    // A partial word past the end, an address below the base, and a wrapping address all read None
    // (the kernel's guest_mem.rs contract, never a panic).
    assert_eq!(guest.read_u32(0x1001), None);
    assert_eq!(guest.read_u32(0x0FFF), None);
    assert_eq!(guest.read_u32(u32::MAX), None);
}

#[test]
fn guest_mem32_mut_write_rejects_unmapped() {
    let mut guest = ProcessGuest32::new(FakeProcess::new(0x1000, 4));
    assert_eq!(guest.write_u32(0x0FFF, 1), None);
    assert_eq!(guest.write_u32(0x1004, 1), None);
    assert_eq!(guest.write_u32(0x1000, 7), Some(()));
    assert_eq!(guest.read_u32(0x1000), Some(7));
}
