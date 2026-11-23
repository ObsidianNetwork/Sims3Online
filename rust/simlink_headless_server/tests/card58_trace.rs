#![forbid(unsafe_code)]

#[test]
fn controller_and_rust_replay_same_offline_trace() {
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
    let dir = std::env::var_os("CARD58_ARTIFACT_DIR").map(std::path::PathBuf::from)
        .unwrap_or_else(|| std::env::temp_dir().join(format!("card58-trace-{}", std::process::id())));
    let status = std::process::Command::new("python")
        .current_dir(&root)
        .env_remove("PI_SESSION_ID")
        .arg(root.join("scripts/card58_trace_diff.py"))
        .args(["--rust-binary", env!("CARGO_BIN_EXE_simlink_headless_server"), "--artifact-dir"])
        .arg(&dir).status().unwrap();
    assert!(status.success(), "controller/Rust differential failed");
    let summary = std::fs::read_to_string(dir.join("trace_diff.jsonl")).unwrap();
    let summary: serde_json::Value = serde_json::from_str(summary.lines().last().unwrap()).unwrap();
    assert_eq!(summary["frames_dropped"], 0);
    assert_eq!(summary["seq_regressions"], 0);
    assert_eq!(summary["leak_events"], 0);
}
