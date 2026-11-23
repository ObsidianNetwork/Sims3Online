#![forbid(unsafe_code)]

use simlink_headless_server::{ConnId, DriverError, GameDriver, Server};
use simlink_headless_server::messages::Command;
use simlink_headless_server::snapshot::{Object, Snapshot};
use simlink_headless_server::stream::{Stream, StreamOptions};

struct Source { requests: Vec<i64>, next_tick: i64, empty: bool, dump: bool }
impl GameDriver for Source {
    fn adjudicate(&mut self, _: &Command) -> Result<Vec<u8>, DriverError> { unreachable!() }
    fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, DriverError> { Ok(Vec::new()) }
    fn snapshot(&mut self, since: i64) -> Result<Option<Snapshot>, DriverError> {
        self.requests.push(since);
        let snapshot = Snapshot { tick: self.next_tick, world_name: "World".into(), save_name: "Save".into(), speed: "Normal".into(),
            sims: Vec::new(), objects: if self.empty { Vec::new() } else { vec![Object { object_id: 1, script_class: "Object".into(), position: [0.0; 3], lot_id: None }] } };
        self.next_tick += 1;
        Ok(Some(snapshot))
    }
    fn claimed_sims(&self) -> Vec<u64> { vec![55] }
    fn recorder(&mut self, args: &[String]) -> Result<String, DriverError> {
        Ok(if self.dump && args[0] == "dump" { "seq=1;more=0\n1|opaque|line".into() } else { String::new() })
    }
}

#[test]
fn pump_fetches_full_and_delta_separately_latches_broadcast_tick_and_keeps_fast_player_running() {
    let mut server = Server::new(1);
    for (id, player, ack) in [(1, 10, 14999), (2, 20, 0)] {
        server.connect(ConnId(id)); server.join(ConnId(id), player);
        server.connection_mut(ConnId(id)).unwrap().cadence.ack(ack);
    }
    let mut stream = Stream::new(StreamOptions::default());
    stream.last_tick = 15000;
    let mut source = Source { requests: Vec::new(), next_tick: 15001, empty: false, dump: false };
    let events = stream.pump(0, &mut server, &mut source).unwrap();
    assert_eq!(source.requests, [-1, 15000]);
    assert_eq!(events.len(), 2);
    assert_eq!(server.connection(ConnId(2)).unwrap().cadence.resync_from_tick, Some(15002));
    assert_eq!(events[0].requester, Some(ConnId(1)));
    assert_eq!(events[0].event.fields[1], "0");
    assert_eq!(events[1].requester, Some(ConnId(2)));
    assert_eq!(events[1].event.fields[1], "1");
    server.connection_mut(ConnId(1)).unwrap().cadence.ack(15002);
    stream.last_tick = 21003;
    let events = stream.pump(2000, &mut server, &mut source).unwrap();
    assert_eq!(events.len(), 1);
    assert_eq!(events[0].requester, Some(ConnId(1)));
}

#[test]
fn empty_delta_advances_watermark_without_emitting_a_frame() {
    let mut server = Server::new(1);
    server.connect(ConnId(1)); server.join(ConnId(1), 10);
    let mut stream = Stream::new(StreamOptions::default());
    stream.last_tick = 4;
    let mut source = Source { requests: Vec::new(), next_tick: 5, empty: true, dump: false };
    assert!(stream.pump(0, &mut server, &mut source).unwrap().is_empty());
    assert_eq!(stream.last_tick, 5);
}

#[test]
fn push_minimum_interval_does_not_throttle_the_dump_fallback() {
    let mut server = Server::new(1);
    server.connect(ConnId(1)); server.join(ConnId(1), 10);
    let mut stream = Stream::new(StreamOptions { presentation_min_interval_ms: 1000, ..StreamOptions::default() });
    let mut source = Source { requests: Vec::new(), next_tick: 1, empty: false, dump: true };
    let events = stream.pump(0, &mut server, &mut source).unwrap();
    assert!(events.iter().any(|event| event.event.kind == "SimAnimation"));
}

#[derive(Default)]
struct HistorySource { claims: Vec<u64>, calls: Vec<Vec<String>> }
impl GameDriver for HistorySource {
    fn adjudicate(&mut self, _: &Command) -> Result<Vec<u8>, DriverError> { unreachable!() }
    fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, DriverError> { Ok(Vec::new()) }
    fn claimed_sims(&self) -> Vec<u64> { self.claims.clone() }
    fn recorder(&mut self, args: &[String]) -> Result<String, DriverError> {
        self.calls.push(args.to_vec());
        Ok(match args[0].as_str() {
            "snapshot" => format!("drivers={}", args[1]),
            "snapshot-driver" => format!("lines=1;next=-1\n0|history|{}", args[1]),
            _ => "seq=20;more=0".into(),
        })
    }
}
#[test]
fn presentation_pump_retains_empty_joiners_and_splits_fresh_from_known_history() {
    let mut server = Server::new(1);
    let mut stream = Stream::new(StreamOptions { base_interval_ms: 0.0, ..StreamOptions::default() });
    let mut source = HistorySource::default();
    server.connect(ConnId(1)); server.join(ConnId(1), 10); stream.joined(ConnId(1));
    assert!(stream.pump(0, &mut server, &mut source).unwrap().is_empty());
    assert!(source.calls.iter().any(|args| args == &["dump", &simlink_headless_server::presentation::FROM_NOW_CURSOR.to_string()]));
    source.claims = vec![55];
    let events = stream.pump(250, &mut server, &mut source).unwrap();
    assert_eq!(events.len(), 1);
    assert_eq!(events[0].requester, None);
    server.connect(ConnId(2)); server.join(ConnId(2), 20); stream.joined(ConnId(2));
    source.calls.clear(); source.claims.push(66);
    let events = stream.pump(500, &mut server, &mut source).unwrap();
    let gathers: Vec<_> = source.calls.iter().filter(|args| args[0] == "snapshot").map(|args| args[1].as_str()).collect();
    assert_eq!(gathers, ["66", "55"]);
    assert_eq!(events.len(), 2);
    assert_eq!(events[0].requester, Some(ConnId(2)));
    assert_eq!(events[1].requester, None);
    source.claims.clear(); source.calls.clear();
    assert!(stream.pump(750, &mut server, &mut source).unwrap().is_empty());
    assert!(source.calls.iter().any(|args| args[0] == "dump" && args[1] == simlink_headless_server::presentation::FROM_NOW_CURSOR.to_string()));
}

#[test]
fn join_gap_counts_stalled_pump_windows_only_during_join() {
    let mut gap = simlink_headless_server::stream::JoinGap::default();
    gap.observe(100, false, 250);
    gap.observe(2100, true, 250);
    assert_eq!(gap.max_during_join, 8);
    gap.observe(12100, false, 250);
    assert_eq!(gap.max_during_join, 8);
    gap.observe(12350, true, 250);
    assert_eq!(gap.max_during_join, 8);
}

#[test]
fn sparse_animation_during_join_does_not_count_empty_pumps_as_drops() {
    let mut server = Server::new(1);
    let mut stream = Stream::new(StreamOptions { base_interval_ms: 0.0, ..StreamOptions::default() });
    let mut source = HistorySource { claims: vec![55], ..HistorySource::default() };
    let mut gap = simlink_headless_server::stream::JoinGap::default();
    server.connect(ConnId(1)); server.join(ConnId(1), 10); stream.joined(ConnId(1));
    let mut sent = Vec::new();
    for now in [0, 250, 500, 750, 1000] {
        if now == 1000 {
            server.connect(ConnId(2)); server.join(ConnId(2), 20); stream.joined(ConnId(2));
        }
        let joining = stream.joining();
        let events = stream.pump(now, &mut server, &mut source).unwrap();
        let has_presentation = events.iter().any(|event| event.event.kind == "SimAnimation");
        if has_presentation { sent.push(now); }
        gap.observe(now, joining && has_presentation, 250);
    }
    assert_eq!(sent, [0, 1000], "scripted recorder traffic has three empty polls");
    assert_eq!((sent[1] - sent[0]) / 250, 4, "old send-to-send measurement fails Card 58");
    assert_eq!(gap.max_during_join, 1);
}

#[test]
fn join_gap_preserves_real_stalls_and_ignores_subwindow_jitter() {
    let mut gap = simlink_headless_server::stream::JoinGap::default();
    assert_eq!(gap.observe(0, false, 50), 0);
    assert_eq!(gap.observe(51, true, 50), 1);
    assert_eq!(gap.max_during_join, 1);
    assert_eq!(gap.observe(151, true, 50), 2);
    assert_eq!(gap.max_during_join, 2);
    assert_eq!(gap.observe(651, true, 50), 10);
    assert_eq!(gap.max_during_join, 10);
}
