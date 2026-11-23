#![forbid(unsafe_code)]

use std::io::{BufRead, BufReader, Write};
use std::net::{TcpListener, TcpStream};
use std::time::Duration;
use simlink_headless_server::serve::{RunningServer, StreamOptions};
use simlink_headless_server::FakeDriver;

fn connect(addr: std::net::SocketAddr) -> BufReader<TcpStream> {
    let socket = TcpStream::connect(addr).unwrap();
    socket.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
    BufReader::new(socket)
}
fn exchange(peer: &mut BufReader<TcpStream>, request: &str) -> String {
    writeln!(peer.get_mut(), "{request}").unwrap();
    let mut line = String::new();
    assert_ne!(peer.read_line(&mut line).unwrap(), 0);
    line.trim_end().into()
}

#[test]
fn loopback_admin_v1_has_authority_but_never_becomes_a_subscriber() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let admin = TcpListener::bind("127.0.0.1:0").unwrap();
    let server = RunningServer::start_configured(listener, Some(admin), || Ok(FakeDriver::new()), 32, StreamOptions::default()).unwrap();
    let mut admin = connect(server.admin_addr().unwrap());
    assert!(exchange(&mut admin, "HELLO|1|").starts_with("WELCOME|1|"));
    assert_eq!(exchange(&mut admin, "CMD|1|SetActiveSim|1|77|123"), "ACK|1|SetActiveSim|ok");
    assert_eq!(exchange(&mut admin, "CMD|2|SetActiveSim|2|88|456"), "ACK|2|SetActiveSim|ok");
    let mut public = connect(server.local_addr());
    assert!(exchange(&mut public, "HELLO|1|ring=1,wfs=1").starts_with("WELCOME"));
    assert!(exchange(&mut public, "CMD|1|SetActiveSim|1|77|123").contains("|NotJoined|"));
    let mut raw = connect(server.admin_addr().unwrap());
    let report = exchange(&mut raw, "FRONT_DOOR_STATE");
    assert!(report.contains("tick_drops_during_join=unmeasured"));
    assert!(report.contains("presentation_sent_gap_max_during_join="));
    assert!(exchange(&mut raw, "PRESENTATION_SENT").contains("\"ok\":true"));
    assert!(exchange(&mut raw, "EVENTS_DROPPED").contains("\"ok\":true"));
    assert!(exchange(&mut raw, "QUIT").contains("\"ok\":true"));
    server.wait().unwrap();
}

#[test]
fn admin_rejects_sidecar_hello_without_falling_back_to_raw_relay() {
    let server = RunningServer::start_configured(
        TcpListener::bind("127.0.0.1:0").unwrap(), Some(TcpListener::bind("127.0.0.1:0").unwrap()),
        || Ok(FakeDriver::new()), 32, StreamOptions::default()).unwrap();
    let mut admin = connect(server.admin_addr().unwrap());
    assert!(exchange(&mut admin, "HELLO|1|1|1|17|15|server").contains("|hello_rejected|"));
    let mut end = String::new();
    assert_eq!(admin.read_line(&mut end).unwrap(), 0);
    server.shutdown().unwrap();
}

fn scripted_join_counter(polls: &[(u64, Option<u64>)]) -> String {
    use std::sync::{mpsc, Arc, atomic::{AtomicU64, Ordering}};
    use simlink_headless_server::{DriverError, GameDriver};
    use simlink_headless_server::messages::{Command, Event};
    use simlink_headless_server::routing::RoutedEvent;

    struct Traffic {
        driver: FakeDriver,
        clock: Arc<AtomicU64>,
        steps: mpsc::Receiver<(u64, Option<u64>)>,
        observed: mpsc::Sender<()>,
    }
    impl GameDriver for Traffic {
        fn adjudicate(&mut self, command: &Command) -> Result<Vec<u8>, DriverError> {
            self.driver.adjudicate(command)
        }
        fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, DriverError> { Ok(Vec::new()) }
        fn poll_routed_events(&mut self) -> Result<Vec<RoutedEvent>, DriverError> {
            let Ok((now, pending_since)) = self.steps.try_recv() else { return Ok(Vec::new()); };
            self.clock.store(now, Ordering::Release);
            self.observed.send(()).unwrap();
            Ok(if let Some(tick) = pending_since { vec![RoutedEvent {
                event: Event { seq: 0, tick, corr: 0, kind: "SimAnimation".into(),
                    fields: vec![simlink_headless_server::messages::enc_list(&["31".into()])] },
                owner: None, requester: None,
            }] } else { Vec::new() })
        }
    }
    let clock = Arc::new(AtomicU64::new(0));
    let driver_clock = Arc::clone(&clock);
    let (steps, input) = mpsc::channel();
    let (observed, output) = mpsc::channel();
    let server = RunningServer::start_configured_with_clock(
        TcpListener::bind("127.0.0.1:0").unwrap(),
        Some(TcpListener::bind("127.0.0.1:0").unwrap()),
        move || Ok(Traffic { driver: FakeDriver::new(), clock: driver_clock, steps: input, observed }),
        32, StreamOptions { base_interval_ms: 0.0, ..StreamOptions::default() },
        move || clock.load(Ordering::Acquire)).unwrap();
    let mut peer = connect(server.local_addr());
    assert!(exchange(&mut peer, "HELLO|1|ring=1,wfs=1").starts_with("WELCOME"));
    assert!(exchange(&mut peer, "CMD|1|JoinSession|1|7|61|~").starts_with("ACK"));
    for &(now, pending_since) in polls {
        steps.send((now, pending_since)).unwrap();
        output.recv_timeout(Duration::from_secs(10)).unwrap();
        if pending_since.is_some() {
            let mut line = String::new();
            assert_ne!(peer.read_line(&mut line).unwrap(), 0);
            assert!(line.contains("|SimAnimation|"), "{line}");
        }
    }
    let mut admin = connect(server.admin_addr().unwrap());
    let mut report = exchange(&mut admin, "FRONT_DOOR_STATE");
    while !report.contains("totals|") {
        assert_ne!(admin.read_line(&mut report).unwrap(), 0);
    }
    server.shutdown().unwrap();
    report
}

#[test]
fn join_counter_samples_empty_pumps_between_sparse_animation_batches() {
    let report = scripted_join_counter(&[
        (0, Some(0)), (250, None), (500, None), (750, None), (1000, Some(1000)),
    ]);
    assert!(report.contains("presentation_sent_gap_max_during_join=1|"), "{report}");
}

#[test]
fn join_counter_rejects_a_stalled_pump_with_animation_pending() {
    // Animation was ready at 250 ms, but the pump missed the 250, 500 and
    // 750 ms opportunities. Unlike empty polls, these are a real stall.
    let report = scripted_join_counter(&[(0, Some(0)), (1000, Some(250))]);
    assert!(report.contains("presentation_sent_gap_max_during_join=4|"), "{report}");
    assert!(!report.contains("presentation_sent_gap_max_during_join=1|"), "Card 58 H7 must fail: {report}");
}
