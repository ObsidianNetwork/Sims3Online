#![forbid(unsafe_code)]

#[test]
fn scorecard_reads_current_source_and_observed_transcripts() {
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
    let score = simlink_headless_server::scorecard::source_counts(&root).unwrap();
    assert_eq!(score, (18, 18, 30, 3));
    assert_eq!(simlink_headless_server::scorecard::unanswered_pairs("case\tCMD|1|ClaimSim|1|7\tERR|1|NotJoined|ClaimSim|\n"), 0);
    assert_eq!(simlink_headless_server::scorecard::unanswered_pairs("case\tCMD|1|ClaimSim|1|7\t\n"), 1);
    if let Some(dir) = std::env::var_os("CARD57_ARTIFACT_DIR") {
        simlink_headless_server::scorecard::emit(&root, std::path::Path::new(&dir)).unwrap();
        let line = std::fs::read_to_string(std::path::Path::new(&dir).join("scorecard.txt")).unwrap();
        assert_eq!(line.trim(), "verbs_public=18|verbs_classified=18|rust_plus_native=30|native=3|silent_drops=0|non_loopback_default=0");
    }
}
