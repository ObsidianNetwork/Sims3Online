#![forbid(unsafe_code)]

#[path = "../src/analytics.rs"]
mod analytics;

use simlink_headless_server::{admin, stream::{Stream, StreamOptions}};

#[test]
fn health_table_has_exactly_the_six_card49_panels_and_needs_real_observations() {
    assert_eq!(analytics::PANELS.map(|panel| panel.name), [
        "both_bridges_up", "authoritative_clock", "sidecar_budgets",
        "rejections_by_name", "crashes_exposure", "player_visible_desync",
    ]);
    let signal = "tick=42;inWorld=True\ncollector=\"fixture\"";
    let rendered = analytics::render(&[analytics::Observation { panel: "authoritative_clock", signal }]);
    assert!(rendered.contains(signal));
    for panel in &analytics::PANELS {
        assert!(!panel.source.is_empty() && !panel.healthy.is_empty() && !panel.alarm.is_empty());
        assert!(rendered.contains(panel.name));
    }
    let reply: serde_json::Value = serde_json::from_str(&admin::analytics_report(&[
        admin::analytics::Observation { panel: "authoritative_clock", signal },
    ])).unwrap();
    assert_eq!(reply["ok"], true);
    assert_eq!(reply["accepted"], true);
    assert_eq!(reply["report"].as_str().unwrap(), rendered);
}

#[test]
fn operator_surface_has_backup_restore_rollback_eight_steps_and_four_disclosures() {
    assert_eq!(analytics::RECOVERY_COMMANDS.map(|row| row.name), ["backup", "restore", "runtime_rollback"]);
    assert_eq!(analytics::INCIDENT_STEPS.map(|row| row.step), [1, 2, 3, 4, 5, 6, 7, 8]);
    assert_eq!(analytics::LIMITATIONS.map(|row| row.name), [
        "supported", "blocked_by_design", "out_of_scope", "supervised",
    ]);
    let rendered = analytics::render(&[]);
    for row in &analytics::RECOVERY_COMMANDS {
        assert!(rendered.contains(row.commands));
    }
    for row in &analytics::INCIDENT_STEPS {
        assert!(rendered.contains(row.incident) && rendered.contains(row.action));
    }
    for row in &analytics::LIMITATIONS {
        assert!(rendered.contains(row.commands));
    }
}

#[test]
fn analytics_aliases_dispatch_locally_without_a_game_driver() {
    let stream = Stream::new(StreamOptions::default());
    let expected = admin::analytics_report(&[]);
    for line in ["ANALYTICS", "analytics", "/analytics", "GET /analytics", "GET /analytics HTTP/1.1", "@30 /analytics"] {
        let request = admin::parse_request(line).unwrap();
        assert_eq!(request.name, "ANALYTICS");
        assert!(request.args.is_empty());
        assert!(admin::is_local_analytics(&request));
        assert_eq!(stream.admin_reply(&request), Some(expected.clone()));
    }
    assert!(admin::parse_request("GET /analytics arbitrary").is_err());
    let invalid = admin::parse_request("ANALYTICS boot-server").unwrap();
    let reply: serde_json::Value = serde_json::from_str(&stream.admin_reply(&invalid).unwrap()).unwrap();
    assert_eq!(reply["ok"], false);
    assert_eq!(stream.admin_reply(&admin::parse_request("DIAG_STATE").unwrap()), None);
    assert!(admin::resolve_endpoint("0.0.0.0:34029").is_err());
}
