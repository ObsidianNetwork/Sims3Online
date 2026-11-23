#![forbid(unsafe_code)]

use simlink_headless_server::events::decode_bridge_event;

#[test]
fn bridge_events_keep_u64s_dialog_stings_and_pick_tooltip() {
    let dialog = decode_bridge_event("EVENT|DialogRequest|7|18446744073709551615|5374696E67|2|736F756E64|3535", 8).unwrap();
    assert_eq!(dialog.kind, "DialogRequested");
    assert_eq!(dialog.fields, ["7", "18446744073709551615", "5374696e67", "[736f756e64,3535]"]);
    let speed = decode_bridge_event("EVENT|SpeedIntentCaptured|Double", 9).unwrap();
    assert_eq!(speed.kind, "GameSpeedChanged");
    assert_eq!(speed.fields, ["Speed2"]);
    let line = format!("EVENT|PickFailed|00000000000000000000000000000001|55|6|{}",
        simlink_bridge::text::encode_text("RouteFailed | tooltip: no route"));
    let failure = decode_bridge_event(&line, 10).unwrap();
    assert_eq!(failure.kind, "PickFailed");
    assert_eq!(failure.fields[0], "RouteFailed");
    assert_eq!(failure.fields[1], simlink_headless_server::messages::hex_encode("no route"));
    assert!(decode_bridge_event("EVENT|DialogRequest|broken", 1).is_err());
}
