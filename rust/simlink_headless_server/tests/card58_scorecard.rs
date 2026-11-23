#![forbid(unsafe_code)]

#[test]
fn ownership_inventory_is_nonempty_classified_and_detects_duplicates() {
    use simlink_headless_server::card58_scorecard::state_classifications;
    let table = "| State field | Owner | Classification | Invalidation |\n|---|---|---|---|\n| ack | session | single-owner | ack |\n| cache | reader | stale-able | refresh |\n";
    assert_eq!(state_classifications(table).unwrap(), (2, 0));
    assert_eq!(state_classifications(&table.replace("stale-able", "duplicated")).unwrap(), (2, 1));
    assert!(state_classifications("").is_err());
    assert!(state_classifications(&table.replace("single-owner", "unknown")).is_err());
    let fixture = include_str!("../fixtures/card58_state_ownership.md");
    let (fields, duplicated) = state_classifications(fixture).unwrap();
    assert_eq!(fields, 4);
    assert_eq!(duplicated, 0);
}
