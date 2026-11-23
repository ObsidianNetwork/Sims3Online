#![forbid(unsafe_code)]

use simlink_headless_server::world_time::{parse_diag, should_reconcile, WorldTime};

#[test]
fn diag_clock_ranges_drift_and_wall_thresholds_match_controller() {
    let noon = WorldTime { day: 0, hours: 12, minutes: 0 };
    assert_eq!(parse_diag("role=server;clockDay=0;clockHours=12;clockMinutes=0"), Some(noon));
    assert_eq!(parse_diag("clockDay=7;clockHours=0;clockMinutes=0"), None);
    assert_eq!(parse_diag("clockDay=0;clockHours=0"), None);
    assert!(should_reconcile(noon, None, 0));
    assert!(!should_reconcile(noon, Some(noon), 30000));
    assert!(!should_reconcile(WorldTime { minutes: 2, ..noon }, Some(noon), 29999));
    assert!(should_reconcile(WorldTime { minutes: 2, ..noon }, Some(noon), 30000));
    assert!(should_reconcile(WorldTime { minutes: 4, ..noon }, Some(noon), 1));
    // The oracle deliberately does not wrap a week at day 6 -> day 0.
    assert!(should_reconcile(noon, Some(WorldTime { day: 6, ..noon }), 0));
}

#[test]
fn reconcile_carries_game_clock_triplet_with_csharp_computed_hours24_semantics() {
    use simlink_headless_server::{messages::dec_record, stream::{Stream, StreamOptions}};
    let mut stream = Stream::new(StreamOptions::default());
    let event = stream.diag_clock("clockDay=2;clockHours=0;clockMinutes=1", 1000).unwrap();
    assert_eq!(event.event.kind, "WorldTimeChanged");
    assert_eq!(dec_record(&event.event.fields[0]).unwrap(), ["2", "0", "1"]);
    // WorldTime.Hours24 is computed after positional decode, using C# float arithmetic.
    assert_eq!(WorldTime { day: 2, hours: 0, minutes: 1 }.hours24().to_bits(), 0x3c88_8889);
    assert_eq!(WorldTime { day: 2, hours: 23, minutes: 59 }.hours24(), 23.983334_f32);
}
