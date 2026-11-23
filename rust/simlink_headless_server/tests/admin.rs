#![forbid(unsafe_code)]

use simlink_headless_server::admin::{parse_request, resolve_endpoint};

#[test]
fn admin_timeout_dash_and_loopback_contracts() {
    let request = parse_request("@120 DIAG_WALK 123 - \\- x\\y").unwrap();
    assert_eq!(request.timeout_seconds, 120);
    assert_eq!(request.name, "DIAG_WALK");
    assert_eq!(request.args, ["123", "", "-", "x\\y"]);
    for invalid in ["", "@0 PING", "@901 PING", "@no PING", "@1"] {
        assert!(parse_request(invalid).is_err(), "{invalid}");
    }
    assert!(resolve_endpoint("0.0.0.0:34029").is_err());
    assert!(resolve_endpoint("192.168.0.1:34029").is_err());
    assert!(resolve_endpoint("127.0.0.1:0").unwrap().ip().is_loopback());
    assert!(resolve_endpoint("[::1]:0").unwrap().ip().is_loopback());
    let reply = vec!["OK|00000000000000000000000000000001".into()];
    assert_eq!(simlink_headless_server::admin::bridge_reply(&reply).unwrap(),
        "{\"ok\":true,\"accepted\":true,\"reason\":null,\"report\":null,\"snapshot\":null}");
    assert_eq!(simlink_headless_server::admin::upload_reply(&reply).unwrap(),
        "{\"ok\":true,\"accepted\":true,\"reason\":null,\"report\":null}");
}
