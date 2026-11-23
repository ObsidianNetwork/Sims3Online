//! The binary's argument contract: a rejected listener endpoint is a usage error (exit 2), and every
//! unrecognized token is named on stderr before any endpoint is validated.
use std::process::Command;

fn run(args: &[&str]) -> std::process::Output {
    Command::new(env!("CARGO_BIN_EXE_simlink_headless_server")).args(args).output().expect("binary runs")
}

#[test]
fn bad_endpoint_exits_2_and_names_unknown_flag_on_stderr() {
    let out = run(&["--public", "not.a.host:80", "--unknown-flag"]);
    let stderr = String::from_utf8_lossy(&out.stderr);
    assert_eq!(out.status.code(), Some(2), "stderr: {stderr}");
    assert!(stderr.contains("ignoring unrecognized argument '--unknown-flag'"), "stderr: {stderr}");
    assert!(!String::from_utf8_lossy(&out.stdout).contains("--unknown-flag"));
}

#[test]
fn loopback_ephemeral_bind_exits_0_and_warns_on_stderr() {
    let out = run(&["--public", "127.0.0.1:0", "--unknown-flag"]);
    assert_eq!(out.status.code(), Some(0));
    assert!(String::from_utf8_lossy(&out.stderr).contains("ignoring unrecognized argument '--unknown-flag'"));
    assert!(String::from_utf8_lossy(&out.stdout).contains("listener bound to 127.0.0.1:"));
}
