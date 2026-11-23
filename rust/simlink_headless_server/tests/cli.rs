#![forbid(unsafe_code)]

use std::io::{BufRead, BufReader, Write};
use std::process::{Child, Command, Stdio};
use std::sync::mpsc;
use std::time::Duration;

struct Process(Child);
impl Drop for Process {
    fn drop(&mut self) { let _ = self.0.kill(); let _ = self.0.wait(); }
}

#[test]
fn bind_only_exits_and_codec_stdio_reencodes_real_frames() {
    let output = Command::new(env!("CARGO_BIN_EXE_simlink_headless_server"))
        .args(["--public", "127.0.0.1:0", "--bind-only"]).output().unwrap();
    assert!(output.status.success());
    assert!(String::from_utf8(output.stdout).unwrap().contains("listener bound to 127.0.0.1:"));
    let mut child = Process(Command::new(env!("CARGO_BIN_EXE_simlink_headless_server"))
        .arg("--codec-stdio").stdin(Stdio::piped()).stdout(Stdio::piped()).spawn().unwrap());
    let mut stdin = child.0.stdin.take().unwrap();
    stdin.write_all(b"CMD|3|ClaimSim|9|7|18446744073709551615\nEV|1|2|3|PickFailed|TestFailed|\n").unwrap();
    drop(stdin);
    let mut output = String::new();
    std::io::Read::read_to_string(&mut child.0.stdout.take().unwrap(), &mut output).unwrap();
    assert_eq!(output, "CMD|3|ClaimSim|9|7|18446744073709551615\nEV|1|2|3|PickFailed|TestFailed|\n");
    assert!(child.0.wait().unwrap().success());
}

#[test]
fn resident_cli_reaches_ready_then_quits_cleanly() {
    let mut child = Process(Command::new(env!("CARGO_BIN_EXE_simlink_headless_server"))
        .args(["--public", "127.0.0.1:0", "--admin", "127.0.0.1:0", "--serve", "--fake-driver"])
        .stdin(Stdio::piped()).stdout(Stdio::piped()).stderr(Stdio::piped()).spawn().unwrap());
    let stdout = child.0.stdout.take().unwrap();
    let (tx, rx) = mpsc::channel();
    let reader = std::thread::spawn(move || {
        for line in BufReader::new(stdout).lines() { if tx.send(Some(line.unwrap())).is_err() { return; } }
        let _ = tx.send(None);
    });
    loop {
        let line = rx.recv_timeout(Duration::from_secs(10)).expect("resident startup signal")
            .expect("resident exited before ready");
        if line.contains(": serving ") { break; }
    }
    child.0.stdin.take().unwrap().write_all(b"QUIT\n").unwrap();
    while rx.recv_timeout(Duration::from_secs(10)).expect("resident stdout EOF after QUIT").is_some() {}
    reader.join().unwrap();
    assert!(child.0.wait().unwrap().success());
    let mut stderr = String::new();
    std::io::Read::read_to_string(&mut child.0.stderr.take().unwrap(), &mut stderr).unwrap();
    let report = stderr.lines().find(|line| line.starts_with("serve_report|QUIT|"))
        .expect("missing QUIT report");
    for field in ["navigate_requests=0", "plans_served=0", "native_oracle_owned=0",
        "road_place_requests=0", "road_place_served=0", "road_load_failures=0"] {
        assert!(report.contains(field), "missing {field}: {report}");
    }
}
