#![forbid(unsafe_code)]

use simlink_headless_server::presentation::{gather_history, parse_dump, Ladder, LadderStep, MAX_LINES};

#[test]
fn history_is_on_demand_caps_each_driver_and_counts_only_unfinished_drivers() {
    let mut calls = Vec::new();
    let history = gather_history(&[10, 20], |args| {
        calls.push(args.to_vec());
        if args[0] == "snapshot" { return Ok("drivers=1,2,3".into()); }
        let driver: u32 = args[1].parse().unwrap();
        let offset: i32 = args[2].parse().unwrap();
        Ok(format!("lines=1;next={}\n0|opaque|{driver}|{offset}", if driver == 1 || (driver == 2 && offset == 15) { -1 } else { offset + 1 }))
    }).unwrap();
    assert_eq!(history.history_trimmed, 1);
    assert_eq!(history.lines.len(), 33);
    assert_eq!(calls[0], ["snapshot", "10,20"]);
    assert_eq!(calls.len(), 34);
    assert_eq!(history.batches().flatten().count(), 33);
    let (seq, more, lines) = parse_dump("seq=17;more=1\n1|A\n\n2|S");
    assert_eq!((seq, more, lines.len()), (17, true, 2));
    assert_eq!(MAX_LINES, 200);
}

#[test]
fn silence_rearm_backoff_and_healthy_reset_use_monotonic_inputs() {
    let mut ladder = Ladder::new(true);
    ladder.push_result(0, true);
    assert_eq!(ladder.evaluate(2999), None);
    assert_eq!(ladder.evaluate(3000), Some(LadderStep::UseDumps));
    assert_eq!(ladder.rearm_delay_ms(), 10000);
    assert_eq!(ladder.evaluate(12999), None);
    assert_eq!(ladder.evaluate(13000), Some(LadderStep::TryPush));
    ladder.push_result(13000, false);
    assert_eq!(ladder.rearm_delay_ms(), 20000);
    let mut now = 33000;
    for expected in [40000, 80000, 160000, 300000, 300000] {
        assert_eq!(ladder.evaluate(now), Some(LadderStep::TryPush));
        ladder.push_result(now, false);
        assert_eq!(ladder.rearm_delay_ms(), expected);
        now += expected;
    }
    ladder.push_result(now, true);
    ladder.page(now + 61000);
    assert_eq!(ladder.evaluate(now + 64000), Some(LadderStep::UseDumps));
    assert_eq!(ladder.rearm_delay_ms(), 10000);
}

#[test]
fn pushed_recorder_sequences_are_deduplicated_before_the_page_watermark_advances() {
    let mut ladder = Ladder::new(true);
    let mut cursor = 30;
    let accepted = ladder.pushed_page(0, "seq=35;more=0\n29|old\n30|old\n31|opaque|00ff|text\n35|new", &mut cursor);
    assert_eq!(accepted, ["31|opaque|00ff|text", "35|new"]);
    assert_eq!(cursor, 35);
    assert!(ladder.pushed_page(1, "seq=32;more=0\n31|opaque|00ff|text", &mut cursor).is_empty());
    assert_eq!(cursor, 35);
    assert_eq!(ladder.rearm_dedup_dropped, 3);
}

#[test]
fn pushed_introductions_follow_their_real_event_and_never_become_watermarks() {
    let mut ladder = Ladder::new(true);
    let mut cursor = 30;
    let preamble = ["0|O|99|FixtureProp", "0|A|1|55|1|2|FixtureJazz|1", "0|S|1|100|55"];
    let report = format!("seq=35;more=0\n{}\n35|AS|1|Enter", preamble.join("\n"));
    assert_eq!(ladder.pushed_page(0, &report, &mut cursor),
        [preamble.as_slice(), &["35|AS|1|Enter"]].concat());
    assert_eq!(cursor, 35);
    assert_eq!(ladder.rearm_dedup_dropped, 0);
    assert!(ladder.pushed_page(1, &report, &mut cursor).is_empty());
    assert_eq!(cursor, 35);
    assert_eq!(ladder.rearm_dedup_dropped, 1);
    let mixed = format!("seq=36;more=0\n{}\n35|AS|1|Enter\n0|O|101|FixtureProp\n36|T|101", preamble.join("\n"));
    assert_eq!(ladder.pushed_page(2, &mixed, &mut cursor), ["0|O|101|FixtureProp", "36|T|101"]);
    assert_eq!(cursor, 36);
    assert_eq!(ladder.rearm_dedup_dropped, 2);
}

#[test]
fn trailing_push_introductions_require_an_advancing_page_watermark() {
    let mut ladder = Ladder::new(true);
    let mut cursor = 30;
    let trailing = "0|O|99|FixtureProp\n0|A|1|55|1|2|FixtureJazz|1\n0|S|1|100|55";
    let report = format!("seq=35;more=0\n31|AS|1|Enter\n{trailing}");
    assert_eq!(ladder.pushed_page(0, &report, &mut cursor),
        ["31|AS|1|Enter", "0|O|99|FixtureProp", "0|A|1|55|1|2|FixtureJazz|1", "0|S|1|100|55"]);
    assert_eq!(cursor, 35);
    assert!(ladder.pushed_page(1, &format!("seq=35;more=0\n{trailing}"), &mut cursor).is_empty());
    assert_eq!(cursor, 35);
    assert_eq!(ladder.rearm_dedup_dropped, 0);
}

use std::collections::VecDeque;
use simlink_headless_server::{admin, ConnId, DriverError, GameDriver, Server};
use simlink_headless_server::messages::{enc_list, hex_encode, Command};
use simlink_headless_server::stream::{Stream, StreamOptions};

#[derive(Default)]
struct RingSource {
    pages: Vec<String>,
    dumps: VecDeque<String>,
    calls: Vec<Vec<String>>,
    admin_requests: Vec<admin::Request>,
}
impl GameDriver for RingSource {
    fn adjudicate(&mut self, _: &Command) -> Result<Vec<u8>, DriverError> { unreachable!() }
    fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, DriverError> { Ok(Vec::new()) }
    fn claimed_sims(&self) -> Vec<u64> { vec![55] }
    fn ring_available(&self) -> bool { true }
    fn presentation_pages(&mut self) -> Result<Vec<String>, DriverError> { Ok(std::mem::take(&mut self.pages)) }
    fn admin_request(&mut self, request: &admin::Request) -> Result<String, DriverError> {
        self.admin_requests.push(request.clone());
        Ok(admin::report("push=on;cursor=10;sequence=12;pages=3;lines=8;sentByKind=A:2,S:6"))
    }
    fn recorder(&mut self, args: &[String]) -> Result<String, DriverError> {
        self.calls.push(args.to_vec());
        Ok(match args[0].as_str() {
            "dump" => self.dumps.pop_front().expect("unexpected dump"),
            "push-on" => "push=on".into(),
            "snapshot" => "drivers=".into(),
            _ => String::new(),
        })
    }
}

fn animation_fields(events: Vec<simlink_headless_server::routing::RoutedEvent>) -> Vec<String> {
    events.into_iter().filter(|event| event.event.kind == "SimAnimation")
        .map(|event| event.event.fields[0].clone()).collect()
}
fn encoded(lines: &[&str]) -> Vec<String> { vec![enc_list(&lines.iter().map(|line| hex_encode(line)).collect::<Vec<_>>())] }

#[test]
fn push_status_remains_a_bridge_command_with_the_mods_reply_unchanged() {
    let stream = Stream::new(StreamOptions::default());
    let mut source = RingSource::default();
    let request = admin::parse_request("SACS_RECORDER push-status").unwrap();
    assert_eq!(stream.admin_reply(&request), None);
    let reply = stream.admin_reply(&request)
        .unwrap_or_else(|| source.admin_request(&request).unwrap());
    assert_eq!(source.admin_requests, [request]);
    assert_eq!(reply, admin::report("push=on;cursor=10;sequence=12;pages=3;lines=8;sentByKind=A:2,S:6"));
}

#[test]
fn pump_keeps_a_monotonic_cursor_across_dump_fallback_late_pages_and_push_rearm() {
    let mut server = Server::new(1);
    server.connect(ConnId(1)); server.join(ConnId(1), 10);
    let mut stream = Stream::new(StreamOptions { base_interval_ms: 0.0, ring_requested: true, ..StreamOptions::default() });
    let mut source = RingSource::default();
    source.dumps.push_back("seq=10;more=0".into());
    assert!(stream.pump(0, &mut server, &mut source).unwrap().is_empty());
    source.pages.push("seq=12;more=0\n11|opaque|a\n12|opaque|b".into());
    assert_eq!(animation_fields(stream.pump(250, &mut server, &mut source).unwrap()), encoded(&["11|opaque|a", "12|opaque|b"]));
    source.dumps.push_back("seq=14;more=0\n13|opaque|c\n14|opaque|d".into());
    assert_eq!(animation_fields(stream.pump(3250, &mut server, &mut source).unwrap()), encoded(&["13|opaque|c", "14|opaque|d"]));
    source.pages.push("seq=13;more=0\n12|opaque|b\n13|opaque|c".into());
    source.dumps.push_back("seq=13;more=0".into());
    assert!(stream.pump(3500, &mut server, &mut source).unwrap().is_empty());
    source.pages.push("seq=15;more=0\n14|opaque|d\n15|opaque|e".into());
    source.dumps.push_back("seq=16;more=0\n16|opaque|f".into());
    assert_eq!(animation_fields(stream.pump(3750, &mut server, &mut source).unwrap()), encoded(&["15|opaque|e", "16|opaque|f"]));
    assert!(source.calls.iter().any(|args| args == &["dump", "15", "55"]));
    assert!(stream.pump(13250, &mut server, &mut source).unwrap().is_empty());
    assert!(source.calls.iter().any(|args| args == &["push-on", "16", "55"]));
    source.pages.push("seq=18;more=0\n14|opaque|d\n16|opaque|f\n17|opaque|g\n18|opaque|h".into());
    assert_eq!(animation_fields(stream.pump(13500, &mut server, &mut source).unwrap()), encoded(&["17|opaque|g", "18|opaque|h"]));
    assert_eq!((stream.ladder.fallbacks, stream.ladder.rearms, stream.ladder.rearm_dedup_dropped), (1, 2, 5));
    let status = stream.ladder.status();
    let counters: std::collections::HashMap<_, _> = status.split(';')
        .map(|item| item.split_once('=').unwrap()).collect();
    assert_eq!(counters["fallbacks"], "1");
    assert_eq!(counters["rearms"], "2");
    assert_eq!(counters["rearm_dedup_dropped"], "5");
}

#[test]
fn held_push_batches_deduplicate_pages_received_between_poll_windows() {
    let mut server = Server::new(1);
    server.connect(ConnId(1)); server.join(ConnId(1), 10);
    let mut stream = Stream::new(StreamOptions { base_interval_ms: 0.0, ring_requested: true,
        presentation_min_interval_ms: 1000, ..StreamOptions::default() });
    let mut source = RingSource::default();
    source.dumps.push_back("seq=10;more=0".into());
    stream.pump(0, &mut server, &mut source).unwrap();
    source.pages.push("seq=11;more=0\n11|opaque|held".into());
    assert!(stream.pump(250, &mut server, &mut source).unwrap().is_empty());
    source.pages.push("seq=12;more=0\n11|opaque|held\n12|opaque|held".into());
    assert!(stream.pump(300, &mut server, &mut source).unwrap().is_empty());
    assert_eq!(animation_fields(stream.pump(1000, &mut server, &mut source).unwrap()), encoded(&["11|opaque|held", "12|opaque|held"]));
    assert_eq!(stream.ladder.rearm_dedup_dropped, 1);
}
