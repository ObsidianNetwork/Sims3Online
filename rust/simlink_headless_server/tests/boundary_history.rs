#![forbid(unsafe_code)]
//! Frozen Boundary paging uses native character budgets, not the SACS page cap.
pub mod common;

use simlink_headless_server::{
    messages::{self as m, Command}, serve::RunningServer, snapshot::Snapshot,
    stream::StreamOptions, DriverError, FakeDriver, GameDriver,
};
use std::{net::TcpListener, sync::{Arc, atomic::{AtomicUsize, Ordering}}};

struct Driver { fake: FakeDriver, rows: Arc<Vec<String>>, pages: Arc<AtomicUsize> }
impl GameDriver for Driver {
    fn adjudicate(&mut self, command: &Command) -> Result<Vec<u8>, DriverError> { self.fake.adjudicate(command) }
    fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, DriverError> { Ok(vec![]) }
    fn claimed_sims(&self) -> Vec<u64> { vec![10] }
    fn snapshot(&mut self, _: i64) -> Result<Option<Snapshot>, DriverError> {
        Ok(Some(Snapshot { tick: 1, world_name: "Fixture".into(), save_name: "Fixture".into(),
            speed: "Normal".into(), sims: vec![], objects: vec![] }))
    }
    fn recorder(&mut self, args: &[String]) -> Result<String, DriverError> {
        Ok(match args[0].as_str() {
            "ring-on" => "recorder=on;ring=on".into(),
            "snapshot" => "sims=10;drivers=4294967280,7".into(),
            "snapshot-driver" if args[1] == "4294967280" => {
                assert_eq!(args[4], "10");
                let offset: usize = args[2].parse().unwrap();
                let mut end = offset;
                let mut chars = 0;
                while end < self.rows.len() && chars + self.rows[end].encode_utf16().count() < 1500 {
                    chars += self.rows[end].encode_utf16().count() + 1;
                    end += 1;
                }
                self.pages.fetch_add(1, Ordering::Release);
                let next = if end == self.rows.len() { -1 } else { end as i32 };
                format!("lines={};next={next};snapshot={};cursor=100\n{}", end - offset, args[3], self.rows[offset..end].join("\n"))
            }
            "snapshot-driver" => format!("lines=1;next=-1;snapshot={};cursor=100\n0|A|7|10|1|2|3|-2", args[3]),
            _ => "seq=100;more=0".into(),
        })
    }
}

#[test]
fn normal_serve_delivers_the_complete_native_boundary_past_page_sixteen_before_driver_history() {
    let rows: Arc<Vec<String>> = Arc::new((100..820).map(|id| {
        format!("0|Ob|{id}|SetTransform:m|1~0~0~0~0~1~0~0~0~0~1~0~1234.56789~45.12345~1200.45678~1")
    }).collect());
    assert!(rows.iter().map(|row| row.len() + 1).sum::<usize>() > 16 * 1500);
    let pages = Arc::new(AtomicUsize::new(0));
    let source = Arc::clone(&rows);
    let counter = Arc::clone(&pages);
    let server = RunningServer::start_configured(
        TcpListener::bind("127.0.0.1:0").unwrap(), None,
        move || Ok(Driver { fake: FakeDriver::default(), rows: source, pages: counter }), 8,
        StreamOptions { base_interval_ms: 10_000.0, ..StreamOptions::default() },
    ).unwrap();
    let mut peer = common::Peer::connect(&server);
    assert!(peer.exchange("CMD|1|JoinSession|1|1|6a|~").starts_with("ACK|1|JoinSession"));
    let mut actual = Vec::new();
    while actual.len() <= rows.len() {
        let line = peer.next().expect("live peer");
        let fields: Vec<_> = line.split('|').map(str::to_owned).collect();
        if fields[0] != "EV" { continue; }
        let event = simlink_headless_server::decode_event(&fields).unwrap();
        if event.kind == "SimAnimation" {
            for item in m::dec_list(&event.fields[0]).unwrap() { actual.push(m::hex_decode(&item).unwrap()); }
        }
    }
    assert!(pages.load(Ordering::Acquire) > 16);
    let mut expected = rows.as_ref().clone();
    expected.push("0|A|7|10|1|2|3|-2".into());
    assert_eq!(actual, expected, "no truncation, duplicate baseline, or early driver history");
    server.shutdown().unwrap();
}
