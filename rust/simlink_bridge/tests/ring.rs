#![forbid(unsafe_code)]
//! SimLink ring reader, offline: header validation, corrupt-ring refusal, the registration handshake and SPSC
//! draining against the in-memory producer model (`simlink_bridge::ring::MemorySection`). No game, no Win32.

use std::time::{Duration, Instant};

use simlink_bridge::ring::{
    self, off, MemorySection, Record, Registration, RingError, RingReader, HEADER_BYTES, KIND_DATA, KIND_PAD,
    STATE_CREATED, STATE_FAILED, STATE_REGISTERED, STATE_REGISTER_REQUESTED,
};

const PID: u32 = 4242;
const BASE: usize = HEADER_BYTES as usize;

fn section(ring: u32) -> MemorySection {
    MemorySection::new(ring, PID)
}

fn open(section: &MemorySection) -> RingReader<MemorySection> {
    RingReader::open(section.clone(), Some(PID)).expect("a valid section opens")
}

fn open_err(section: MemorySection, pid: Option<u32>) -> RingError {
    match RingReader::open(section, pid) {
        Ok(_) => panic!("expected the header to be refused"),
        Err(error) => error,
    }
}

fn bad_header(error: &RingError) -> &str {
    match error {
        RingError::BadHeader(why) => why,
        other => panic!("expected BadHeader, got {other:?}"),
    }
}

fn drain_all(ring: &mut RingReader<MemorySection>) -> Vec<Record> {
    let mut out = Vec::new();
    ring.drain(&mut out).expect("drain");
    out
}

// ---- header validation ----------------------------------------------------------------------------------------

#[test]
fn a_valid_header_opens_and_reports_its_fields() {
    let s = section(4096);
    s.poke_u32(off::NATIVE_VERSION, 15);
    s.poke_u32(off::PROBE_RESULT, (-3i32) as u32);
    s.poke_u64(off::PUBLISHED, 7);
    s.poke_u64(off::DROPPED, 2);
    s.poke_u64(off::CONTENDED, 1);
    s.poke_u64(off::BAD_ARGS, 4);
    let ring = open(&s);
    assert_eq!((ring.ring_size(), ring.pid(), ring.state(), ring.error()), (4096, PID, STATE_CREATED, 0));
    let t = ring.telemetry();
    assert_eq!((t.published, t.dropped, t.contended, t.bad_args), (7, 2, 1, 4));
    assert_eq!((t.native_version, t.probe_result, t.qpc_frequency), (15, -3, 10_000_000));
}

#[test]
fn a_header_that_is_not_layout_v1_is_refused() {
    let s = section(4096);
    s.poke(off::MAGIC, b"SIMLINK2");
    assert!(bad_header(&open_err(s, Some(PID))).contains("unexpected layout"));

    let s = section(4096);
    s.poke_u32(off::LAYOUT_VERSION, 2);
    assert!(bad_header(&open_err(s, Some(PID))).contains("version=2"));

    let s = section(4096);
    s.poke_u32(off::HEADER_SIZE, 255);
    assert!(bad_header(&open_err(s, Some(PID))).contains("header size 255"));

    let short = MemorySection::from_bytes(vec![0u8; 100], true);
    assert!(bad_header(&open_err(short, None)).contains("under the 256-byte header"));
}

#[test]
fn the_pid_is_checked_only_when_one_is_expected() {
    let s = section(4096);
    assert!(bad_header(&open_err(s.clone(), Some(PID + 1))).contains("pid"));
    assert!(RingReader::open(s, None).is_ok());
}

#[test]
fn ring_geometry_is_bounds_checked_against_the_mapping() {
    for (ring_size, why) in [(0u32, "power of two"), (96, "power of two"), (4, "power of two")] {
        let s = section(4096);
        s.poke_u32(off::RING_SIZE, ring_size);
        assert!(bad_header(&open_err(s, Some(PID))).contains(why), "ring size {ring_size}");
    }
    let s = section(4096);
    s.poke_u32(off::RING_OFFSET, 255);
    assert!(bad_header(&open_err(s, Some(PID))).contains("inside the 256-byte header"));

    let s = section(4096);
    s.poke_u32(off::RING_OFFSET, 257);
    assert!(bad_header(&open_err(s, Some(PID))).contains("does not fit"), "offset past the end of the ring's room");

    let s = section(4096);
    s.poke_u32(off::RING_SIZE, 8192);
    assert!(bad_header(&open_err(s, Some(PID))).contains("does not fit"));

    let s = section(4096);
    s.poke_u32(off::RING_OFFSET, u32::MAX);
    s.poke_u32(off::RING_SIZE, 1 << 31);
    assert!(bad_header(&open_err(s, Some(PID))).contains("does not fit"), "no u32 overflow");
}

// ---- corrupt rings --------------------------------------------------------------------------------------------

fn corrupt_message(s: &MemorySection) -> String {
    let mut ring = open(s);
    let start = s.peek_u64(off::READ_POS);
    let mut out = Vec::new();
    let error = ring.drain(&mut out).expect_err("the ring is corrupt");
    assert!(out.is_empty(), "a refused drain appends nothing");
    assert_eq!(s.peek_u64(off::READ_POS), start, "a refused drain leaves ReadPos alone");
    let RingError::Corrupt(why) = error else { panic!("expected Corrupt") };
    assert_eq!(ring.poisoned(), Some(why.as_str()));
    // Poisoned: every later drain fails the same way and never touches the section again.
    let copied = s.bytes_copied();
    assert_eq!(ring.drain(&mut out), Err(RingError::Corrupt(why.clone())));
    assert_eq!(s.bytes_copied(), copied);
    why
}

#[test]
fn positions_that_cannot_be_a_ring_are_refused() {
    let s = section(64);
    s.poke_u64(off::READ_POS, 10);
    s.poke_u64(off::WRITE_POS, 8);
    assert!(corrupt_message(&s).contains("positions out of range"));

    let s = section(64);
    s.poke_u64(off::WRITE_POS, 65);
    assert!(corrupt_message(&s).contains("positions out of range"), "WritePos - ReadPos > RingSize");
}

#[test]
fn bad_record_sizes_and_alignment_are_refused() {
    let write_head = |size: i32, kind: u16| {
        let s = section(64);
        s.poke(BASE, &size.to_le_bytes());
        s.poke(BASE + 4, &kind.to_le_bytes());
        s.poke_u64(off::WRITE_POS, 32);
        s
    };
    for size in [0, -8, 12, 72] {
        assert!(corrupt_message(&write_head(size, KIND_DATA)).contains(&format!("size {size}")), "size {size}");
    }
    // The record would end past the write position.
    assert!(corrupt_message(&write_head(40, KIND_DATA)).contains("past the write position"));
    // An unaligned read position.
    let s = section(64);
    s.poke_u64(off::READ_POS, 4);
    s.poke_u64(off::WRITE_POS, 12);
    assert!(corrupt_message(&s).contains("not 8-byte aligned"));
}

#[test]
fn unknown_kinds_and_inconsistent_data_records_are_refused() {
    let s = section(64);
    s.poke(BASE, &16i32.to_le_bytes());
    s.poke(BASE + 4, &9u16.to_le_bytes());
    s.poke_u64(off::WRITE_POS, 16);
    assert!(corrupt_message(&s).contains("unknown kind 9"));

    let s = section(64);
    s.poke(BASE, &16i32.to_le_bytes());
    s.poke(BASE + 4, &KIND_DATA.to_le_bytes());
    s.poke_u64(off::WRITE_POS, 16);
    assert!(corrupt_message(&s).contains("under its 24-byte header"));

    for chars in [-1i32, 5] {
        let s = section(64);
        let mut record = ring::encode_record(6, 1, 1, "abcd");
        record[12..16].copy_from_slice(&chars.to_le_bytes());
        s.poke(BASE, &record);
        s.poke_u64(off::WRITE_POS, record.len() as u64);
        assert!(corrupt_message(&s).contains(&format!("claims {chars} chars")), "chars {chars}");
    }
}

/// A record whose first character is a lone high surrogate, the way the native producer can publish one.
fn lone_surrogate_record(lane: u16, seq: i32) -> Vec<u8> {
    let mut record = ring::encode_record(lane, seq, 1, "ab");
    record[24..26].copy_from_slice(&0xD800u16.to_le_bytes());
    record
}

#[test]
fn a_lone_surrogate_on_an_ignored_lane_keeps_its_units_and_does_not_poison_the_ring() {
    let s = section(256);
    let bad = lone_surrogate_record(2, 1);
    let good = ring::encode_record(6, 2, 2, "page");
    s.poke(BASE, &bad);
    s.poke(BASE + bad.len(), &good);
    s.poke_u64(off::WRITE_POS, (bad.len() + good.len()) as u64);
    let mut ring = open(&s);
    let got = drain_all(&mut ring);
    assert_eq!(ring.poisoned(), None, "the ring stays healthy");
    assert_eq!(s.peek_u64(off::READ_POS), (bad.len() + good.len()) as u64, "ReadPos advanced past both records");
    assert_eq!(got.len(), 2);
    assert_eq!((got[0].lane, got[0].units.clone()), (2, vec![0xD800u16, u16::from(b'b')]), "exact u16 units kept");
    assert_eq!(got[0].text_strict(), None);
    assert_eq!(got[0].text_lossy(), "\u{FFFD}b");
    assert_eq!((got[1].lane, got[1].text_lossy()), (6, "page".to_string()));
}

#[test]
fn a_lone_surrogate_on_lane_six_does_not_poison_the_ring_either() {
    let s = section(128);
    let bad = lone_surrogate_record(6, 1);
    s.poke(BASE, &bad);
    s.poke_u64(off::WRITE_POS, bad.len() as u64);
    let mut ring = open(&s);
    let got = drain_all(&mut ring);
    assert_eq!(ring.poisoned(), None);
    assert_eq!(s.peek_u64(off::READ_POS), bad.len() as u64);
    assert_eq!(got[0].text_lossy(), "\u{FFFD}b");
}

/// A 64-byte ring whose positions are `read`/`write` and whose record head at `read` has `size` and `kind`.
fn ring_at(read: u64, write: u64, size: i32, kind: u16) -> MemorySection {
    let s = section(64);
    let at = BASE + (read & 63) as usize;
    s.poke(at, &size.to_le_bytes());
    s.poke(at + 4, &kind.to_le_bytes());
    s.poke_u64(off::READ_POS, read);
    s.poke_u64(off::WRITE_POS, write);
    s
}

#[test]
fn an_8_byte_record_at_the_top_of_the_u64_range_is_corrupt_not_an_overflow() {
    // write - read is 7 (<= the ring size) and the read position is 8-aligned, so only the record's own size can
    // catch this: 8 bytes do not fit in the 7 that are published. It must not compute `read + 8` past u64::MAX.
    for kind in [KIND_PAD, KIND_DATA] {
        let s = ring_at(u64::MAX - 7, u64::MAX, 8, kind);
        assert!(corrupt_message(&s).contains("past the write position"), "kind {kind}");
    }
}

#[test]
fn records_larger_than_the_published_bytes_are_corrupt_at_the_u64_boundary() {
    // A full-ring pad (64 bytes) where only 63 bytes are published.
    let s = ring_at(u64::MAX - 63, u64::MAX, 64, KIND_PAD);
    assert!(corrupt_message(&s).contains("past the write position"));
    // A pad that fits is consumed, but the 7 bytes behind it are not a record: still Corrupt, never a panic.
    let s = ring_at(u64::MAX - 15, u64::MAX, 8, KIND_PAD);
    assert!(corrupt_message(&s).contains("size 0"));
    // Both positions at the very top.
    let s = ring_at(u64::MAX, u64::MAX, 8, KIND_PAD);
    let mut ring = open(&s);
    assert_eq!(ring.drain(&mut Vec::new()), Ok(0), "nothing is published");
}

#[test]
fn a_good_record_before_a_bad_one_is_not_half_applied() {
    let s = section(128);
    assert!(s.publish(6, 1, 1, "ok"));
    let good = ring::encode_record(6, 1, 1, "ok").len();
    s.poke(BASE + good, &0i32.to_le_bytes());
    s.poke_u64(off::WRITE_POS, (good + 8) as u64);
    let mut ring = open(&s);
    let mut out = Vec::new();
    assert!(matches!(ring.drain(&mut out), Err(RingError::Corrupt(_))));
    assert!(out.is_empty());
    assert_eq!(s.peek_u64(off::READ_POS), 0);
}

#[test]
fn publish_enforces_native_half_ring_and_quarter_char_limits() {
    let s = section(128);
    // 128-byte ring: quarter = 32 UTF-16 chars, half = 64 aligned bytes (24-byte header + 32 chars = 88 > 64).
    assert!(!s.publish(6, 1, 1, &"a".repeat(21)), "24 + 42 = 66 -> 72 aligned bytes > 64");
    assert_eq!(s.peek_u64(off::DROPPED), 1);
    assert!(!s.publish(6, 2, 1, &"b".repeat(33)), "exceeds ring_size / 4 chars");
    assert_eq!(s.peek_u64(off::DROPPED), 2);
    assert!(s.publish(6, 3, 1, &"c".repeat(20)), "24 + 40 = 64 aligned bytes fits");
    assert_eq!(s.peek_u64(off::PUBLISHED), 1);
}

// ---- the registration handshake -------------------------------------------------------------------------------

#[test]
fn registration_walks_created_requested_registered() {
    let s = section(4096);
    s.set_registration(Registration::Succeed);
    let ring = open(&s);
    assert_eq!(ring.state(), STATE_CREATED);
    assert!(ring.request_registration(Duration::from_secs(1)));
    assert_eq!(ring.state(), STATE_REGISTERED);
    assert_eq!(s.control_signals(), 1);
    // Already registered: true without another request.
    assert!(ring.request_registration(Duration::ZERO));
    assert_eq!(s.control_signals(), 1);
}

#[test]
fn registration_writes_the_request_state_before_signalling() {
    let s = section(4096);
    let ring = open(&s);
    assert!(!ring.request_registration(Duration::from_millis(30)), "nobody answers: a timeout");
    assert_eq!(ring.state(), STATE_REGISTER_REQUESTED);
    assert_eq!(s.control_signals(), 1);
}

#[test]
fn a_failed_registration_reports_the_native_error() {
    let s = section(4096);
    s.set_registration(Registration::Fail(2));
    let ring = open(&s);
    let started = Instant::now();
    assert!(!ring.request_registration(Duration::from_secs(5)));
    assert!(started.elapsed() < Duration::from_secs(2), "StateFailed ends the wait early");
    assert_eq!((ring.state(), ring.error()), (STATE_FAILED, 2));
}

#[test]
fn without_a_control_event_registration_is_false_and_writes_nothing() {
    let mut bytes = ring::initial_header(4096, PID);
    bytes.resize(BASE + 4096, 0);
    let s = MemorySection::from_bytes(bytes, false);
    let ring = open(&s);
    assert!(!ring.request_registration(Duration::from_millis(10)));
    assert_eq!((ring.state(), s.control_signals()), (STATE_CREATED, 0));
    assert!(!ring.wait_for_data(0), "no .data event either");
}

// ---- draining -------------------------------------------------------------------------------------------------

#[test]
fn records_drain_across_batches_in_order_and_free_their_space() {
    let s = section(4096);
    let mut ring = open(&s);
    assert!(drain_all(&mut ring).is_empty());
    for seq in 0..3 {
        assert!(s.publish(6, seq, 100 + i64::from(seq), &format!("page {seq}")));
    }
    let first = drain_all(&mut ring);
    assert_eq!(first.iter().map(|r| (r.seq, r.qpc, r.text_lossy())).collect::<Vec<_>>(),
        vec![(0, 100, "page 0".to_string()), (1, 101, "page 1".to_string()), (2, 102, "page 2".to_string())]);
    assert_eq!(s.peek_u64(off::READ_POS), s.peek_u64(off::WRITE_POS));
    assert!(s.publish(1, 3, 103, "mirror"));
    assert!(s.publish(6, 4, 104, ""));
    let second = drain_all(&mut ring);
    assert_eq!(second.iter().map(|r| (r.lane, r.seq, r.text_lossy())).collect::<Vec<_>>(),
        vec![(1, 3, "mirror".to_string()), (6, 4, String::new())], "every lane comes through; lane filtering is the consumer's");
    assert_eq!(ring.telemetry().published, 5);
}

#[test]
fn utf16_including_surrogate_pairs_round_trips() {
    let s = section(4096);
    let mut ring = open(&s);
    let text = "dump|\u{1F600}|caf\u{e9}|\u{4e2d}\u{6587}";
    assert!(s.publish(6, 9, 1, text));
    assert_eq!(drain_all(&mut ring)[0].text_strict().as_deref(), Some(text));
}

#[test]
fn the_ring_fills_exactly_then_drops_then_recovers() {
    let s = section(64);
    let mut ring = open(&s);
    // 24 header + 8 payload bytes (4 chars) = a 32-byte record: two fill the 64-byte ring exactly.
    assert!(s.publish(6, 1, 1, "abcd"));
    assert!(s.publish(6, 2, 2, "efgh"));
    assert_eq!(s.peek_u64(off::WRITE_POS) - s.peek_u64(off::READ_POS), 64);
    assert!(!s.publish(6, 3, 3, "ijkl"), "a full ring drops");
    assert_eq!(ring.telemetry().dropped, 1);
    let all = drain_all(&mut ring);
    assert_eq!(all.len(), 2);
    assert_eq!(s.peek_u64(off::READ_POS), 64);
    assert!(s.publish(6, 4, 4, "mnop"), "space is free again, and the next record wraps to offset 0 with no pad");
    assert_eq!(drain_all(&mut ring)[0].seq, 4);
    assert_eq!(s.peek_u64(off::READ_POS), 96);
}

#[test]
fn a_pad_is_skipped_and_only_its_eight_byte_head_is_read() {
    let s = section(128);
    let mut ring = open(&s);
    assert!(s.publish(6, 1, 1, &"a".repeat(12))); // 48 bytes (within native <= 64B / <= 32 chars limits)
    assert!(s.publish(6, 2, 1, &"a".repeat(12))); // +48 = 96 bytes
    assert_eq!(drain_all(&mut ring).len(), 2);
    let before = s.bytes_copied();
    assert!(s.publish(6, 3, 2, &"b".repeat(16))); // 56 bytes: 32 left before the end, so a pad then wrap
    // The pad's body is poisoned: reading any of it would corrupt the payload below.
    s.poke(BASE + 96 + 8, &[0xFF; 24]);
    let got = drain_all(&mut ring);
    assert_eq!(got.len(), 1);
    assert_eq!(got[0].text_lossy(), "b".repeat(16));
    assert_eq!(s.bytes_copied() - before, 8 + 24 + 32, "pad head (8) + data header (24) + 16 chars (32)");
    assert_eq!(s.peek_u64(off::READ_POS), 96 + 32 + 56);
    // The pad head really is a kind-2 record covering the tail.
    let mut head = [0u8; 8];
    s.copy_out(BASE + 96, &mut head);
    assert_eq!((u32::from_le_bytes([head[0], head[1], head[2], head[3]]), u16::from_le_bytes([head[4], head[5]])), (32, KIND_PAD));
    let mut data = [0u8; 8];
    s.copy_out(BASE, &mut data);
    assert_eq!(u16::from_le_bytes([data[4], data[5]]), KIND_DATA);
}

#[test]
fn many_wraps_keep_every_record_and_position_consistent() {
    let s = section(256);
    let mut ring = open(&s);
    let mut seen = 0;
    for round in 0..200 {
        let text = "x".repeat(round % 23);
        assert!(s.publish(6, round as i32, 0, &text), "round {round}");
        if round % 2 == 0 {
            for record in drain_all(&mut ring) {
                assert_eq!(record.seq, seen);
                seen += 1;
            }
        }
    }
    for record in drain_all(&mut ring) {
        assert_eq!(record.seq, seen);
        seen += 1;
    }
    assert_eq!(seen, 200);
}

#[test]
fn the_data_event_wakes_once_per_signal() {
    let s = section(4096);
    let ring = open(&s);
    assert!(!ring.wait_for_data(0));
    assert!(s.publish(6, 1, 1, "x"));
    assert!(ring.wait_for_data(0));
    assert!(!ring.wait_for_data(0), "auto-reset");
}

// ---- names ------------------------------------------------------------------------------------------------------

#[test]
fn section_and_sandboxie_object_paths_are_formatted_like_the_oracle() {
    assert_eq!(ring::section_name(1234), "SimsOnline.SimLink.1234");
    assert_eq!(
        ring::sandboxie_directory("player", "DefaultBox", 2),
        r"\Sandbox\player\DefaultBox\Session_2\Sessions\2\BaseNamedObjects\"
    );
    let directory = ring::sandboxie_directory("u", "b", 1);
    assert_eq!(
        format!("{directory}{}.control", ring::section_name(7)),
        r"\Sandbox\u\b\Session_1\Sessions\1\BaseNamedObjects\SimsOnline.SimLink.7.control"
    );
}

#[test]
fn opening_a_pid_with_no_sidecar_is_no_section() {
    // PID 1 never has a SimLink section; the open is a clean refusal on every platform.
    assert!(matches!(ring::open_native(1, None), Err(RingError::NoSection(_))));
    assert!(matches!(ring::open_native(1, Some("NoSuchBox")), Err(RingError::NoSection(_))));
}
