#![forbid(unsafe_code)]

use std::collections::VecDeque;

use simlink_bridge::ring::{off, MemorySection, RingReader};
use simlink_headless_server::messages::{enc_list, hex_encode, Command};
use simlink_headless_server::presentation::FROM_NOW_CURSOR;
use simlink_headless_server::ring_pages::{PageSource, RingPages, PAGE_LANE};
use simlink_headless_server::routing::RoutedEvent;
use simlink_headless_server::stream::{Stream, StreamOptions};
use simlink_headless_server::{ConnId, DriverError, GameDriver, Server};

const PID: u32 = 777;

struct RingDriver {
    ring: MemorySection,
    pages: RingPages<MemorySection>,
    dumps: VecDeque<String>,
    dump_failures: usize,
    calls: Vec<Vec<String>>,
    epoch: Option<u64>,
    announced: Vec<u64>,
    unread_at_announce: Vec<u64>,
    dumps_left_at_announce: Vec<usize>,
    // The mod's push state, and whether `dump` is refused while it is on (`ServerHostMain.cs`).
    pushing: bool,
    refuse_dump_while_pushing: bool,
    // Published into the ring while `push-off` is being handled: a page that landed before it took effect.
    publish_on_push_off: Option<(i32, String)>,
}

impl RingDriver {
    fn new() -> Self {
        let ring = MemorySection::new(1 << 16, PID);
        let pages = RingPages::new(RingReader::open(ring.clone(), Some(PID)).unwrap());
        Self { ring, pages, dumps: VecDeque::new(), dump_failures: 0, calls: Vec::new(), epoch: None,
            announced: Vec::new(), unread_at_announce: Vec::new(), dumps_left_at_announce: Vec::new(),
            pushing: false, refuse_dump_while_pushing: false, publish_on_push_off: None }
    }
    fn page(&self, seq: i32, text: &str) {
        assert!(self.ring.publish(PAGE_LANE, seq, 0, text));
    }
    fn unread(&self) -> u64 { self.ring.peek_u64(off::WRITE_POS) - self.ring.peek_u64(off::READ_POS) }
}

impl GameDriver for RingDriver {
    fn adjudicate(&mut self, _: &Command) -> Result<Vec<u8>, DriverError> { unreachable!() }
    fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, DriverError> { Ok(Vec::new()) }
    fn claimed_sims(&self) -> Vec<u64> { vec![55] }
    fn ring_available(&self) -> bool { self.pages.available() }
    fn presentation_pages(&mut self) -> Result<Vec<String>, DriverError> { Ok(self.pages.take_pages()) }
    fn discard_pages(&mut self) -> Result<usize, DriverError> { Ok(self.pages.discard_pages()) }
    fn take_epoch_announcement(&mut self) -> Option<u64> { self.epoch }
    fn announced(&mut self, epoch: u64) {
        self.unread_at_announce.push(self.unread());
        self.dumps_left_at_announce.push(self.dumps.len());
        self.announced.push(epoch);
        self.epoch = None;
    }
    fn recorder(&mut self, args: &[String]) -> Result<String, DriverError> {
        self.calls.push(args.to_vec());
        match args[0].as_str() {
            "dump" if self.refuse_dump_while_pushing && self.pushing => Err(DriverError("dump refused while pushing".into())),
            "dump" if self.dump_failures > 0 => {
                self.dump_failures -= 1;
                Err(DriverError("dump failed".into()))
            }
            "dump" => Ok(self.dumps.pop_front().unwrap_or_else(|| "seq=0;more=0".into())),
            // Replies use the mod's DescribePush shape, which the ring owner checks field by field.
            "ring-on" => Ok("ring=on;recorder=on".into()),
            "push-on" => {
                self.pushing = true;
                Ok("push=on,cursor=0,sequence=0,behind=0".into())
            }
            "push-targets" | "push-status" => Ok(format!("push={},cursor=0,sequence=0,behind=0", if self.pushing { "on" } else { "off" })),
            "push-off" => {
                self.pushing = false;
                if let Some((seq, text)) = self.publish_on_push_off.take() { self.page(seq, &text); }
                Ok("push=off,cursor=0,sequence=0,behind=0".into())
            }
            "snapshot" => Ok("drivers=".into()),
            _ => Ok(String::new()),
        }
    }
}

fn joined_server() -> Server {
    let mut server = Server::new(1);
    server.connect(ConnId(1));
    server.join(ConnId(1), 10);
    server
}
fn animation_lines(events: &[RoutedEvent]) -> Vec<String> {
    events.iter().filter(|event| event.event.kind == "SimAnimation").flat_map(|event| {
        simlink_headless_server::messages::dec_list(&event.event.fields[0]).unwrap().into_iter()
            .map(|hex| simlink_headless_server::messages::hex_decode(&hex).unwrap())
    }).collect()
}
fn encoded(lines: &[&str]) -> Vec<String> { vec![enc_list(&lines.iter().map(|line| hex_encode(line)).collect::<Vec<_>>())] }
fn fields(events: Vec<RoutedEvent>) -> Vec<String> {
    events.into_iter().filter(|event| event.event.kind == "SimAnimation").map(|event| event.event.fields[0].clone()).collect()
}

#[test]
fn pages_published_into_the_ring_reach_clients_and_mirror_lanes_are_ignored() {
    let mut server = joined_server();
    let mut stream = Stream::new(StreamOptions { base_interval_ms: 0.0, ring_requested: true, ..StreamOptions::default() });
    let mut driver = RingDriver::new();
    driver.dumps.push_back("seq=10;more=0".into());
    assert!(stream.pump(0, &mut server, &mut driver).unwrap().is_empty());
    driver.page(1, "seq=12;more=0\n11|opaque|a\n12|opaque|b");
    assert!(driver.ring.publish(1, 2, 0, "11|opaque|mirror-only"));
    assert_eq!(fields(stream.pump(250, &mut server, &mut driver).unwrap()), encoded(&["11|opaque|a", "12|opaque|b"]));
    assert_eq!(driver.unread(), 0, "the pump freed the ring");
    assert!(driver.calls.iter().any(|args| args[0] == "push-on"), "a real ring lets the ladder push");
}

#[test]
fn silence_falls_back_to_dumps_and_the_rearm_pushes_again_over_the_same_ring() {
    let mut server = joined_server();
    let mut stream = Stream::new(StreamOptions { base_interval_ms: 0.0, ring_requested: true, ..StreamOptions::default() });
    let mut driver = RingDriver::new();
    driver.dumps.push_back("seq=10;more=0".into());
    stream.pump(0, &mut server, &mut driver).unwrap();
    driver.page(1, "seq=12;more=0\n11|opaque|a\n12|opaque|b");
    stream.pump(250, &mut server, &mut driver).unwrap();
    driver.dumps.push_back("seq=14;more=0\n13|opaque|c\n14|opaque|d".into());
    assert_eq!(fields(stream.pump(3250, &mut server, &mut driver).unwrap()), encoded(&["13|opaque|c", "14|opaque|d"]));
    assert!(driver.calls.iter().any(|args| args == &["push-off"]), "3 s without a page: back to dumps");
    assert_eq!(stream.ladder.fallbacks, 1);
    driver.dumps.push_back("seq=14;more=0".into());
    stream.pump(13250, &mut server, &mut driver).unwrap();
    assert_eq!(stream.ladder.rearms, 2, "the first arm plus the re-arm after the 10 s backoff");
    driver.page(2, "seq=16;more=0\n15|opaque|e\n16|opaque|f");
    assert_eq!(fields(stream.pump(13500, &mut server, &mut driver).unwrap()), encoded(&["15|opaque|e", "16|opaque|f"]));
}

#[test]
fn a_dead_ring_reports_ring_unavailable_and_the_stream_keeps_using_dumps() {
    let mut server = joined_server();
    let mut stream = Stream::new(StreamOptions { base_interval_ms: 0.0, ring_requested: true, ..StreamOptions::default() });
    let mut driver = RingDriver::new();
    driver.dumps.push_back("seq=10;more=0".into());
    driver.ring.poke(256, &[0u8; 8]);
    driver.ring.poke_u64(off::WRITE_POS, 8);
    stream.pump(0, &mut server, &mut driver).unwrap();
    assert!(!driver.ring_available());
    assert!(stream.report.iter().any(|line| line.starts_with("ring_unavailable")), "{:?}", stream.report);
    assert!(!driver.calls.iter().any(|args| args[0] == "push-on"), "no push is armed over a dead ring");
    driver.dumps.push_back("seq=12;more=0\n11|opaque|a\n12|opaque|b".into());
    let events = stream.pump(250, &mut server, &mut driver).unwrap();
    assert_eq!(fields(events), encoded(&["11|opaque|a", "12|opaque|b"]));
}

#[test]
fn pages_are_discarded_while_nobody_is_joined_and_never_delivered_later() {
    let mut server = Server::new(1);
    server.connect(ConnId(1));
    let mut stream = Stream::new(StreamOptions { base_interval_ms: 0.0, ring_requested: true, ..StreamOptions::default() });
    let mut driver = RingDriver::new();
    for seq in 1..=3 {
        driver.page(seq, &format!("seq={};more=0\n{}|opaque|stale", 10 + seq, 10 + seq));
    }
    assert!(stream.pump(0, &mut server, &mut driver).unwrap().is_empty());
    assert_eq!((driver.pages.pages_received(), driver.pages.pages_discarded()), (3, 3));
    assert_eq!(driver.unread(), 0);
    server.join(ConnId(1), 10);
    driver.dumps.push_back("seq=20;more=0".into());
    assert!(stream.pump(250, &mut server, &mut driver).unwrap().is_empty(), "stale pages never reach a later joiner");
}

fn epoch_stream() -> (Stream, Server) { epoch_stream_with(false) }
// Ring ownership is fixed when the stream is built, so the ring cases ask for it here.
fn epoch_stream_with(ring_requested: bool) -> (Stream, Server) {
    let stream = Stream::new(StreamOptions { base_interval_ms: 0.0, presentation_min_interval_ms: 10000, ring_requested, ..Default::default() });
    let mut server = Server::new(9);
    server.connect(ConnId(3));
    server.join(ConnId(3), -7);
    (stream, server)
}

#[test]
fn the_epoch_barrier_drains_unread_ring_pages_and_dump_pages_before_announcing() {
    for pushing in [false, true] {
        let (mut stream, mut server) = epoch_stream();
        let mut driver = RingDriver::new();
        stream.pump(0, &mut server, &mut driver).unwrap();
        stream.ladder.push_result(0, true);
        driver.page(1, "seq=1;more=0\n1|A|55|old-buffered");
        assert!(animation_lines(&stream.pump(1, &mut server, &mut driver).unwrap()).is_empty(), "held by the cadence");
        driver.page(2, "seq=2;more=0\n2|A|55|old-unread");
        let mut expected = vec!["1|A|55|old-buffered".to_string(), "2|A|55|old-unread".into()];
        stream.ladder.pushing = pushing;
        if !pushing {
            for n in 3..=6 {
                driver.dumps.push_back(format!("seq={n};more={}\n{n}|A|55|old-dump", u8::from(n < 6)));
                expected.push(format!("{n}|A|55|old-dump"));
            }
        }
        driver.epoch = Some(2);
        let events = stream.pump(2, &mut server, &mut driver).unwrap();
        assert_eq!(animation_lines(&events), expected, "pushing={pushing}");
        assert_eq!(driver.announced, [2], "pushing={pushing}");
        assert_eq!(driver.unread_at_announce, [0], "the ring was empty when the epoch was announced");
        assert_eq!(driver.dumps_left_at_announce, [0], "every dump page was read before the epoch was announced");
    }
}

#[test]
fn a_failed_epoch_dump_keeps_the_epoch_pending_and_loses_no_ring_page() {
    let (mut stream, mut server) = epoch_stream();
    let mut driver = RingDriver::new();
    stream.pump(0, &mut server, &mut driver).unwrap();
    stream.ladder.push_result(0, true);
    stream.ladder.pushing = false;
    driver.page(1, "seq=1;more=0\n1|A|55|from-ring");
    driver.dumps.push_back("seq=2;more=0\n2|A|55|from-dump".into());
    driver.epoch = Some(5);
    driver.dump_failures = 1;
    assert!(stream.pump(2, &mut server, &mut driver).is_err(), "the recorder failed");
    assert_eq!((driver.epoch, driver.announced.len()), (Some(5), 0), "an IO error does not consume the pending epoch");
    assert_eq!(driver.unread(), 0, "the page was already read out of the ring");
    let events = stream.pump(3, &mut server, &mut driver).unwrap();
    assert_eq!(animation_lines(&events), ["1|A|55|from-ring", "2|A|55|from-dump"], "the ring page survived the error");
    assert_eq!(driver.announced, [5]);
}

fn verbs(driver: &RingDriver) -> Vec<&str> { driver.calls.iter().map(|args| args[0].as_str()).collect() }
fn ring_stream() -> Stream { Stream::new(StreamOptions { base_interval_ms: 0.0, ring_requested: true, ..StreamOptions::default() }) }

#[test]
fn a_page_published_before_push_off_takes_effect_is_emitted_and_moves_the_dump_cursor() {
    let mut server = joined_server();
    let mut stream = ring_stream();
    let mut driver = RingDriver::new();
    driver.dumps.push_back("seq=10;more=0".into());
    stream.pump(0, &mut server, &mut driver).unwrap();
    stream.pump(250, &mut server, &mut driver).unwrap();
    assert!(stream.ladder.pushing, "push is on at cursor 10");
    // The mod publishes `11|X|7` (a driver release it will not repeat in a dump) before `push-off` lands.
    driver.publish_on_push_off = Some((11, "seq=11;more=0\n11|X|7".into()));
    driver.dumps.push_back("seq=11;more=0".into());
    let events = stream.pump(3250, &mut server, &mut driver).unwrap();
    assert_eq!(animation_lines(&events), ["11|X|7"], "the page is delivered, not discarded");
    assert_eq!(driver.pages.pages_discarded(), 0, "nothing was thrown away");
    let verbs = verbs(&driver);
    let off = verbs.iter().rposition(|verb| *verb == "push-off").expect("push-off was sent");
    let dump = verbs.iter().rposition(|verb| *verb == "dump").expect("dumps resumed");
    assert!(off < dump, "push-off precedes the resumed dump: {verbs:?}");
    assert_eq!(driver.calls[dump][1], "11", "the dump starts at the page's sequence, not the old cursor 10");
}

#[test]
fn a_warm_mod_that_is_already_pushing_is_stopped_before_the_first_dump() {
    let mut server = joined_server();
    let mut stream = ring_stream();
    let mut driver = RingDriver::new();
    driver.pushing = true;
    driver.refuse_dump_while_pushing = true;
    driver.dumps.push_back("seq=10;more=0".into());
    stream.pump(0, &mut server, &mut driver).unwrap();
    stream.pump(250, &mut server, &mut driver).unwrap();
    assert_eq!(&verbs(&driver)[..4], ["ring-on", "push-off", "dump", "push-on"]);
    assert_eq!(driver.calls[2], ["dump".to_string(), FROM_NOW_CURSOR.to_string()]);
}

#[test]
fn a_warm_mod_is_stopped_before_the_epoch_dump_too() {
    let (mut stream, mut server) = epoch_stream_with(true);
    let mut driver = RingDriver::new();
    driver.pushing = true;
    driver.refuse_dump_while_pushing = true;
    driver.epoch = Some(2);
    for now in 0..4 {
        stream.pump(now, &mut server, &mut driver).expect("no dump reached a pushing mod");
    }
    assert_eq!(&verbs(&driver)[..3], ["ring-on", "push-off", "dump"]);
    assert_eq!(driver.announced, [2]);
}

#[test]
fn without_the_ring_the_startup_sequence_has_no_push_off() {
    let mut server = joined_server();
    let mut stream = Stream::new(StreamOptions { base_interval_ms: 0.0, ..StreamOptions::default() });
    let mut driver = RingDriver::new();
    stream.pump(0, &mut server, &mut driver).unwrap();
    assert_eq!(&verbs(&driver)[..2], ["ring-on", "dump"]);
    assert!(!verbs(&driver).contains(&"push-off"));
}

#[test]
fn pages_consumed_while_idle_on_push_move_the_cursor_so_a_later_fallback_does_not_replay_them() {
    let mut server = joined_server();
    let mut stream = ring_stream();
    let mut driver = RingDriver::new();
    driver.dumps.push_back("seq=10;more=0".into());
    stream.pump(0, &mut server, &mut driver).unwrap();
    stream.pump(250, &mut server, &mut driver).unwrap();
    assert!(stream.ladder.pushing);
    server.leave(ConnId(1));
    driver.page(1, "seq=20;more=0\n11|opaque|stale");
    assert!(stream.pump(500, &mut server, &mut driver).unwrap().is_empty());
    assert_eq!(driver.unread(), 0, "the idle pump freed the ring");
    server.connect(ConnId(2));
    server.join(ConnId(2), 10);
    driver.dumps.push_back("seq=20;more=0".into());
    let mut emitted = Vec::new();
    for now in [600, 3600] {
        emitted.extend(animation_lines(&stream.pump(now, &mut server, &mut driver).unwrap()));
    }
    let dump = driver.calls.iter().rposition(|args| args[0] == "dump").expect("fallback dump");
    assert_eq!(driver.calls[dump][1], "20", "the dump resumes after the dropped page, not at the old cursor 10");
    assert!(emitted.iter().all(|line| !line.contains("stale")), "the dropped line is never emitted: {emitted:?}");
}

#[test]
fn a_lone_surrogate_on_an_ignored_lane_does_not_starve_lane_six_pages() {
    let ring = MemorySection::new(1 << 12, PID);
    let mut ignored = simlink_bridge::ring::encode_record(2, 1, 1, "ab");
    ignored[24..26].copy_from_slice(&0xD800u16.to_le_bytes());
    let mut bad_page = simlink_bridge::ring::encode_record(PAGE_LANE, 3, 3, "xy");
    bad_page[24..26].copy_from_slice(&0xD800u16.to_le_bytes());
    let good = simlink_bridge::ring::encode_record(PAGE_LANE, 2, 2, "seq=1;more=0\n1|opaque|ok");
    let mut at = 256;
    for record in [&ignored, &good, &bad_page] {
        ring.poke(at, record);
        at += record.len();
    }
    ring.poke_u64(off::WRITE_POS, (at - 256) as u64);
    let mut pages = RingPages::new(RingReader::open(ring.clone(), Some(PID)).unwrap());
    assert_eq!(pages.take_pages(), ["seq=1;more=0\n1|opaque|ok", "\u{FFFD}y"], "lane 6 survives, lossy only where needed");
    assert!(pages.available(), "the ring stays healthy");
    assert_eq!(ring.peek_u64(off::READ_POS), ring.peek_u64(off::WRITE_POS), "ReadPos advanced past everything");
}
