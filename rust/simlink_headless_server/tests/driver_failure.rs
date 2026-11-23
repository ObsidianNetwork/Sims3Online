#![forbid(unsafe_code)]

use std::io::{BufRead, BufReader, Write};
use std::net::{TcpListener, TcpStream};
use std::time::Duration;
use simlink_headless_server::{DriverError, GameDriver, messages::{Command, hex_decode}, serve::RunningServer, wire_v1 as w};

struct FailedDriver;
impl GameDriver for FailedDriver {
    fn adjudicate(&mut self, _: &Command) -> Result<Vec<u8>, DriverError> { Err(DriverError("snapshot unavailable".into())) }
    fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, DriverError> { Ok(Vec::new()) }
}
#[test]
fn valid_command_driver_failure_is_controller_rejection_not_public_bad_line() {
    let server = RunningServer::start(TcpListener::bind("127.0.0.1:0").unwrap(), FailedDriver, 4).unwrap();
    let socket = TcpStream::connect(server.local_addr()).unwrap();
    socket.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
    let mut socket = BufReader::new(socket);
    writeln!(socket.get_mut(), "HELLO|1|ring=1,wfs=1").unwrap();
    let mut line = String::new(); socket.read_line(&mut line).unwrap();
    assert!(line.starts_with("WELCOME|"));
    writeln!(socket.get_mut(), "CMD|1|JoinSession|1|77|66697874757265|~").unwrap();
    line.clear(); socket.read_line(&mut line).unwrap();
    let rejected = w::decode_event(&w::split(line.trim_end().as_bytes()).unwrap()).expect("controller rejects with CommandRejected, not bad_line");
    assert_eq!((rejected.kind.as_str(), rejected.corr), ("CommandRejected", 1));
    assert_eq!(rejected.fields[0], "1");
    assert_eq!(hex_decode(&rejected.fields[1]).unwrap(), "snapshot unavailable");
    line.clear(); socket.read_line(&mut line).unwrap();
    let error = w::decode_event(&w::split(line.trim_end().as_bytes()).unwrap()).unwrap();
    assert_eq!(error.kind, "ServerError");
    assert!(error.seq > rejected.seq);
    writeln!(socket.get_mut(), "CMD|nope|RequestSnapshot|2|77|~").unwrap();
    line.clear(); socket.read_line(&mut line).unwrap();
    assert!(line.starts_with("ERR|0|bad_line|RequestSnapshot|"));
    server.shutdown().unwrap();
}

#[test]
fn native_io_and_snapshot_decode_failures_remain_driver_errors_with_fresh_ids() {
    use simlink_headless_server::bridge_driver::{BridgeEndpoint, BridgeGameDriver};
    use simlink_bridge::{text::Guid, verbs::BridgeCommand};
    struct FailedEndpoint { decode: bool, ids: Vec<Guid> }
    impl BridgeEndpoint for FailedEndpoint {
        fn command(&mut self, command: &BridgeCommand) -> Result<Vec<String>, DriverError> {
            let line = simlink_bridge::verbs::encode(command).unwrap();
            self.ids.push(Guid::parse(line.split('|').nth(1).unwrap()).unwrap());
            if self.decode { Ok(Vec::new()) } else { Err(DriverError("bridge read failed".into())) }
        }
        fn take_snapshot(&mut self) -> Option<Vec<u8>> { self.decode.then(|| b"not a snapshot".to_vec()) }
        fn events(&mut self) -> Result<Vec<Vec<u8>>, DriverError> { Ok(Vec::new()) }
    }
    for decode in [false, true] {
        let mut driver = BridgeGameDriver::new(FailedEndpoint { decode, ids: Vec::new() });
        for corr in 1..=2 {
            let command = Command { corr, command_id: corr, player_id: 77, verb: "RequestSnapshot".into(), args: vec!["~".into()] };
            let error = driver.adjudicate_for(simlink_headless_server::ConnId(1), &command).unwrap_err();
            assert_eq!(error.0, if decode { "snapshot2_decode_failed" } else { "bridge read failed" });
        }
        let ids = &driver.endpoint().ids;
        assert_ne!(&ids[0].0[..8], &[0; 8]);
        assert_eq!(&ids[0].0[..8], &ids[1].0[..8]);
        assert_eq!(u64::from_be_bytes(ids[0].0[8..].try_into().unwrap()), 1);
        assert_eq!(u64::from_be_bytes(ids[1].0[8..].try_into().unwrap()), 2);
    }
}
