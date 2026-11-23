#![forbid(unsafe_code)]

use std::io::{BufRead, BufReader, Write};
use std::net::{TcpListener, TcpStream};
use std::time::Duration;
use simlink_headless_server::serve::{RunningServer, StreamOptions};

#[test]
fn analytics_is_local_but_recorder_push_status_still_reaches_the_native_driver() {
    struct RecordingDriver(std::sync::Arc<std::sync::Mutex<Vec<simlink_headless_server::admin::Request>>>);
    impl simlink_headless_server::GameDriver for RecordingDriver {
        fn adjudicate(&mut self, _: &simlink_headless_server::messages::Command) -> Result<Vec<u8>, simlink_headless_server::DriverError> {
            panic!("admin relay must not dispatch a public gameplay command");
        }
        fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, simlink_headless_server::DriverError> { Ok(vec![]) }
        fn admin_request(&mut self, request: &simlink_headless_server::admin::Request) -> Result<String, simlink_headless_server::DriverError> {
            self.0.lock().unwrap().push(request.clone());
            Ok(simlink_headless_server::admin::report("native recorder fixture"))
        }
    }
    let calls = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
    let driver = RecordingDriver(std::sync::Arc::clone(&calls));
    let server = RunningServer::start_configured(TcpListener::bind("127.0.0.1:0").unwrap(),
        Some(TcpListener::bind("127.0.0.1:0").unwrap()), move || Ok(driver), 4, StreamOptions::default()).unwrap();
    let socket = TcpStream::connect(server.admin_addr().unwrap()).unwrap();
    socket.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
    let mut socket = BufReader::new(socket);
    for request in ["ANALYTICS", "/analytics", "GET /analytics HTTP/1.1"] {
        writeln!(socket.get_mut(), "{request}").unwrap();
        let mut line = String::new();
        assert_ne!(socket.read_line(&mut line).unwrap(), 0);
        let reply: serde_json::Value = serde_json::from_str(&line).unwrap();
        assert_eq!(reply["ok"], true);
        assert_eq!(reply["accepted"], true);
        assert!(reply["report"].is_string());
        assert!(calls.lock().unwrap().is_empty());
    }
    writeln!(socket.get_mut(), "ANALYTICS extra").unwrap();
    let mut line = String::new();
    assert_ne!(socket.read_line(&mut line).unwrap(), 0);
    assert_eq!(serde_json::from_str::<serde_json::Value>(&line).unwrap()["ok"], false);
    assert!(calls.lock().unwrap().is_empty());
    writeln!(socket.get_mut(), "@17 SACS_RECORDER push-status").unwrap();
    let mut line = String::new();
    assert_ne!(socket.read_line(&mut line).unwrap(), 0);
    assert_eq!(serde_json::from_str::<serde_json::Value>(&line).unwrap()["report"], "native recorder fixture");
    assert_eq!(*calls.lock().unwrap(), vec![simlink_headless_server::admin::Request {
        timeout_seconds: 17, name: "SACS_RECORDER".into(), args: vec!["push-status".into()],
    }]);
    server.shutdown().unwrap();
}

#[test]
#[cfg(feature = "repo-integration")]
fn commands_array_is_consumed_by_the_real_bridgectl_renderer() {
    use std::process::{Command, Stdio};
    use simlink_headless_server::FakeDriver;

    let server = RunningServer::start_configured(TcpListener::bind("127.0.0.1:0").unwrap(),
        Some(TcpListener::bind("127.0.0.1:0").unwrap()), || Ok(FakeDriver::new()), 4, StreamOptions::default()).unwrap();
    let socket = TcpStream::connect(server.admin_addr().unwrap()).unwrap();
    socket.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
    let mut socket = BufReader::new(socket);
    writeln!(socket.get_mut(), "commands").unwrap();
    let mut line = String::new(); socket.read_line(&mut line).unwrap();
    let reply: serde_json::Value = serde_json::from_str(&line).unwrap();
    let commands = reply["commands"].as_array().expect("bridgectl needs an array, not a string");
    assert!(commands.iter().all(serde_json::Value::is_string));
    let oracle = std::fs::read_to_string(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../DedicatedServer/HostBridge/BridgeLineCodec.cs")).unwrap();
    let expected: Vec<_> = oracle.split_once("KnownCommands = new[]").unwrap().1.split_once("};").unwrap().0.lines()
        .filter_map(|line| line.trim().strip_prefix('"').and_then(|line| line.strip_suffix("\",").or_else(|| line.strip_suffix('"')))).collect();
    let (local, bridge): (Vec<_>, Vec<_>) = commands.iter().map(|value| value.as_str().unwrap())
        .partition(|command| simlink_headless_server::admin::FRONT_DOOR_LOCAL_COMMANDS
            .contains(&command.split_whitespace().next().unwrap()));
    assert_eq!(bridge, expected);
    assert_eq!(local.iter().map(|command| command.split_whitespace().next().unwrap()).collect::<Vec<_>>(),
        simlink_headless_server::admin::FRONT_DOOR_LOCAL_COMMANDS);
    let mut child = Command::new("python").current_dir(std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../.."))
        .env("PYTHONIOENCODING", "utf-8")
        .args(["-c", "import json,sys; sys.path.insert(0,'scripts'); from bridgectl import render; raise SystemExit(render(json.load(sys.stdin)))"])
        .stdin(Stdio::piped()).stdout(Stdio::piped()).spawn().unwrap();
    child.stdin.take().unwrap().write_all(line.as_bytes()).unwrap();
    let output = child.wait_with_output().unwrap();
    assert!(output.status.success());
    let rendered = String::from_utf8(output.stdout).unwrap();
    assert_eq!(rendered.lines().count(), commands.len());
    assert!(rendered.lines().any(|line| line == "  DIAG_STATE"));
    writeln!(socket.get_mut(), "@5 commands extra").unwrap();
    let mut line = String::new(); socket.read_line(&mut line).unwrap();
    assert!(serde_json::from_str::<serde_json::Value>(&line).unwrap().get("commands").is_none());
    server.shutdown().unwrap();
}
