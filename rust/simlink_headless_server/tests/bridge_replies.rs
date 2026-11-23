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
use std::collections::VecDeque;

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
    description_reports: VecDeque<&'static str>,
    strict_async: bool,
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
            if fields[0] == "EXPORT_SIM_DESCRIPTION" {
                let answer = self.description_reports.pop_front().unwrap_or("good");
                let bytes = [0u8, 255, 2, 3];
                let slot = BulkSlot::new(0).unwrap().byte_offset();
                let metadata = BulkSlotHeader { state: BULK_STATE_HOST_READY,
                    correlation_id: Guid::parse(if answer == "stale" { STALE } else { corr }).unwrap().to_dotnet_bytes(),
                    total_length: bytes.len() as i32, crc32: crc32(&bytes) };
                self.mem.poke(address(slot), &metadata.to_bytes());
                self.mem.poke(address(slot + BULK_SLOT_HEADER_SIZE), &bytes);
                let normal = format!("result=exported; slot=0; bytes=4; crc={:08X}; props=2; keys=1; objects=1; name=fixture", metadata.crc32);
                let report = match answer {
                    "missing" => "result=missing".into(),
                    "no-metadata" => "result=exported".into(),
                    "duplicate" => format!("{normal}; bytes=4"),
                    "segments" => normal.replace("objects=1", "objects=2"),
                    "oversize" => normal.replace("bytes=4", &format!("bytes={}", BULK_SLOT_PAYLOAD_MAX + 1))
                        .replace("props=2", &format!("props={}", BULK_SLOT_PAYLOAD_MAX - 1)),
                    "bad-crc" => normal.replace(&format!("crc={:08X}", metadata.crc32), "crc=00000000"),
                    _ => normal,
                };
                let hex = match answer {
                    "invalid-utf8" => "FF".into(),
                    "odd-hex" => "123".into(),
                    "mixed" => encode_text(&report).chars().enumerate().map(|(index, value)|
                        if index % 2 == 0 { value.to_ascii_uppercase() } else { value.to_ascii_lowercase() }).collect(),
                    _ => encode_text(&report).to_ascii_uppercase(),
                };
                host(self.mem, slot_byte_offset(HOST_SLOT_BASE).unwrap(), header.seq,
                    &format!("AUTONOMY_PROBE|{corr}|ok|{hex}"));
                continue;
            }
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
    fn pause(&mut self, ms: u64) {
        assert!(!self.strict_async || !self.multi || ms == 0, "async export must not wait for its native reply");
        self.step();
    }
}
fn attached(mem: &SimMemory, stale: bool, text: bool, duplicate: bool) -> LiveBridge<&SimMemory, Mod<'_>> {
    let mut driver = BridgeDriver::new(mem, BASE, 100, Mod { mem, multi: false, stale, text, duplicate,
        description_reports: VecDeque::new(), strict_async: false });
    driver.connect().unwrap();
    LiveBridge::from_driver(driver)
}

fn description_endpoint<'a>(mem: &'a SimMemory, reports: &[&'static str]) -> LiveBridge<&'a SimMemory, Mod<'a>> {
    let mut driver = BridgeDriver::new(mem, BASE, 100, Mod { mem, multi: false, stale: false, text: false,
        duplicate: false, description_reports: reports.iter().copied().collect(), strict_async: true });
    driver.connect().unwrap(); LiveBridge::from_driver(driver)
}

#[test]
fn asynchronous_participant_export_reads_actual_native_metadata_and_owned_bulk_without_waiting() {
    use simlink_headless_server::transport::{StreamReply, StreamRequest};
    for report in ["good", "mixed"] {
        let mem = memory();
        let mut driver = BridgeGameDriver::new(description_endpoint(&mem, &[report]));
        assert!(driver.participant_lifecycle_enabled());
        assert!(driver.start_stream_request(&StreamRequest::ExportSimDescription(u64::MAX)).unwrap().is_none());
        let StreamReply::Description(payload) = driver.poll_stream_request().unwrap().expect("nonblocking fixture completion") else { panic!("description expected"); };
        assert_eq!(payload.desc, u64::MAX);
        assert_eq!(payload.property_stream, [0, 255]);
        assert_eq!(payload.res_key_table, [2]);
        assert_eq!(payload.object_id_table, [3]);
    }
}

#[test]
fn malformed_oversized_or_unowned_async_export_cannot_reuse_a_previous_description() {
    for report in ["missing", "no-metadata", "duplicate", "segments", "oversize", "bad-crc", "stale", "invalid-utf8", "odd-hex"] {
        let mem = memory();
        let mut endpoint = description_endpoint(&mem, &["good", report, "good"]);
        for (index, success) in [true, false, true].into_iter().enumerate() {
            let corr = Guid::parse(&format!("1234567890abcdef{:016x}", index + 1)).unwrap();
            assert!(endpoint.begin_stream_command(&BridgeCommand::ExportSimDescription { corr, desc: u64::MAX }).unwrap().is_none());
            let result = endpoint.poll_stream_command();
            assert_eq!(result.is_ok(), success, "{report}");
            if success && index == 0 {
                // Deliberately leave the first good payload unclaimed. Starting
                // the next export must clear it before accepting its response.
            } else if success {
                let payload = endpoint.take_description().expect("positive recovery");
                assert_eq!(payload.desc, u64::MAX); assert_eq!(payload.property_stream, [0, 255]);
            } else {
                assert!(endpoint.take_description().is_none(), "{report} retained stale bytes");
                let slot = BulkSlot::new(0).unwrap().byte_offset();
                assert_eq!(BulkSlotHeader::from_bytes(&mem.peek(address(slot), BULK_SLOT_HEADER_SIZE)).unwrap().state,
                    BULK_STATE_HOST_READY, "invalid metadata/ownership must not consume a slot");
            }
        }
    }
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
