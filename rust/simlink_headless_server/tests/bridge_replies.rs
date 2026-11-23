#![forbid(unsafe_code)]

use simlink_bridge::crc32::crc32;
use simlink_bridge::driver::{BridgeDriver, Pacer};
use simlink_bridge::layout::*;
use simlink_bridge::memory::SimMemory;
use simlink_bridge::text::{encode_text, Guid};
use simlink_bridge::verbs::BridgeCommand;
use simlink_headless_server::bridge_driver::{BridgeEndpoint, BridgeGameDriver, LiveBridge};
use simlink_headless_server::messages::Command;
use simlink_headless_server::{ConnId, GameDriver};

const BASE: u32 = 0x0100_0000;
const STALE: &str = "00000000000000000000000000000001";

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
fn payload() -> Vec<u8> {
    let mut bytes = SNAPSHOT2_MAGIC.to_le_bytes().to_vec();
    bytes.extend(2u32.to_le_bytes()); bytes.extend(7u64.to_le_bytes());
    bytes.extend((BULK_SLOT_PAYLOAD_MAX as u32).to_le_bytes());
    bytes.extend(vec![b'W'; BULK_SLOT_PAYLOAD_MAX]);
    bytes.extend(0u32.to_le_bytes()); // save
    bytes.extend(1u32.to_le_bytes()); // speed
    bytes.extend(0u32.to_le_bytes()); // Sims
    bytes.extend(0u32.to_le_bytes()); // objects
    bytes
}
struct Mod<'a> {
    mem: &'a SimMemory,
    multi: bool,
    stale: bool,
    text: bool,
    duplicate: bool,
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
            let corr = fields[1];
            let mut lines = Vec::new();
            if self.stale {
                lines.push(format!("BULK_REF|{STALE}|snapshot-part|0|0|2|941713|631D296E"));
                self.stale = false;
            }
            if self.text {
                let body = format!("SNAPSHOT|{corr}|7|57|53|Normal|0|0");
                let middle = body.len() / 2;
                lines.push(format!("SNAPSHOT_CHUNK|{corr}|1|2|{}", &body[middle..]));
                if self.duplicate { lines.push(format!("SNAPSHOT_CHUNK|{corr}|1|2|{}", &body[middle..])); }
                lines.push(format!("SNAPSHOT_CHUNK|{corr}|0|2|{}", &body[..middle]));
            } else {
                let bytes = payload();
                let mut refs = Vec::new();
                for (index, bytes) in bytes.chunks(BULK_SLOT_PAYLOAD_MAX).enumerate() {
                    let slot = BulkSlot::new(index).unwrap().byte_offset();
                    let metadata = BulkSlotHeader { state: BULK_STATE_HOST_READY, correlation_id: Guid::parse(corr).unwrap().to_dotnet_bytes(),
                        total_length: bytes.len() as i32, crc32: crc32(bytes) };
                    self.mem.poke(address(slot), &metadata.to_bytes());
                    self.mem.poke(address(slot + BULK_SLOT_HEADER_SIZE), bytes);
                    refs.push(format!("BULK_REF|{corr}|snapshot-part|{index}|{index}|2|{}|{:08X}", payload().len(), metadata.crc32));
                }
                lines.push(refs[1].clone());
                if self.duplicate { lines.push(refs[1].clone()); }
                lines.push(refs[0].clone());
            }
            for (index, line) in lines.iter().enumerate() {
                host(self.mem, slot_byte_offset(HOST_SLOT_BASE + index).unwrap(), header.seq, line);
            }
        }
    }
}
impl Pacer for Mod<'_> {
    fn pause(&mut self, _: u64) { self.step(); }
}
fn attached(mem: &SimMemory, stale: bool, text: bool, duplicate: bool) -> LiveBridge<&SimMemory, Mod<'_>> {
    let mut driver = BridgeDriver::new(mem, BASE, 100, Mod { mem, multi: false, stale, text, duplicate });
    driver.connect().unwrap();
    LiveBridge::from_driver(driver)
}

#[test]
fn warm_stale_id_one_then_reversed_real_join_snapshot_succeeds() {
    let mem = memory();
    let mut driver = BridgeGameDriver::new(attached(&mem, true, false, false));
    let reply = driver.adjudicate_for(ConnId(1), &Command { corr: 1, command_id: 1, player_id: 77,
        verb: "JoinSession".into(), args: vec![encode_text("fixture"), "~".into()] }).unwrap();
    assert!(reply.starts_with(b"ACK|"));
    let events = driver.poll_routed_events().unwrap();
    assert!(events.iter().any(|e| e.event.kind == "SessionJoined"));
    assert!(events.iter().any(|e| e.event.kind == "FullSnapshot"));
    assert_eq!(driver.endpoint().dropped_replies(), 1);
    assert_eq!(driver.event_drop_state(), "BridgeReply:1");
}

#[test]
fn matched_binary_parts_accept_reverse_order_and_count_duplicates() {
    let mem = memory();
    let mut endpoint = attached(&mem, false, false, true);
    let corr = Guid::parse("0123456789abcdef0123456789abcdef").unwrap();
    endpoint.command(&BridgeCommand::RequestSnapshot { corr, since_tick: None }).unwrap();
    assert_eq!(endpoint.take_snapshot().unwrap(), payload());
    assert_eq!(endpoint.dropped_replies(), 1);
}

#[test]
fn matched_legacy_chunks_accept_reverse_order_and_count_duplicates() {
    let mem = memory();
    let mut endpoint = attached(&mem, true, true, true);
    let corr = Guid::parse("0123456789abcdef0123456789abcdef").unwrap();
    let reply = endpoint.command(&BridgeCommand::RequestSnapshot { corr, since_tick: None }).unwrap();
    assert_eq!(reply, [format!("SNAPSHOT|{}|7|57|53|Normal|0|0", corr.to_n())]);
    assert_eq!(endpoint.dropped_replies(), 2);
}

#[test]
fn asynchronous_snapshot_keeps_reversed_parts_chunks_and_duplicate_accounting() {
    for text in [false, true] {
        let mem = memory();
        let mut endpoint = attached(&mem, true, text, true);
        let corr = Guid::parse("0123456789abcdef0123456789abcdef").unwrap();
        assert!(endpoint.begin_stream_command(&BridgeCommand::RequestSnapshot { corr, since_tick: None }).unwrap().is_none());
        let reply = endpoint.poll_stream_command().unwrap().expect("scripted producer answered without sleeping");
        if text {
            assert_eq!(reply, [format!("SNAPSHOT|{}|7|57|53|Normal|0|0", corr.to_n())]);
            assert!(endpoint.take_snapshot().is_none());
        } else {
            assert_eq!(endpoint.take_snapshot().unwrap(), payload());
            assert_eq!(reply, [format!("SNAPSHOT2|{}", corr.to_n())]);
        }
        assert_eq!(endpoint.dropped_replies(), 2);
    }
}
