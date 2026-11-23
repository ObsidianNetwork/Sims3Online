#![forbid(unsafe_code)]

mod common;

#[test]
fn public_loopback_has_no_admin_bypass_and_supersede_ends_claims() {
    let server = common::server();
    let mut first = common::Peer::connect(&server);
    let mut rows = Vec::new();
    for (i, verb) in simlink_headless_server::bridge_driver::PUBLIC_VERBS.iter().enumerate() {
        if simlink_headless_server::wire_v1::UNJOINED_ALLOWED.contains(&verb.0) {
            continue;
        }
        let request = format!("CMD|{}|{}|{}|7", i + 1, verb.0, i + 1);
        let reply = first.exchange(&request);
        assert_eq!(reply, format!("ERR|{}|NotJoined|{}|", i + 1, verb.0));
        rows.push(format!("unjoined_loopback\t{request}\t{reply}"));
    }
    assert_eq!(rows.len(), 14);
    assert!(first.exchange("CMD|50|JoinSession|50|7|61|~").starts_with("ACK|50|JoinSession|resume_token="));
    assert!(first.exchange("CMD|51|ClaimSim|51|7|9007199254740993").starts_with("ACK|51|ClaimSim|9007199254740993"));
    let mut other = common::Peer::connect(&server);
    assert!(other.exchange("CMD|1|JoinSession|1|8|62|~").starts_with("ACK|1|JoinSession|resume_token="));
    assert_eq!(other.exchange("CMD|2|ClaimSim|2|8|9007199254740993"), "ERR|2|UnauthorizedClientInsertion|ClaimSim|");
    let mut replacement = common::Peer::connect(&server);
    assert!(replacement.exchange("CMD|1|JoinSession|1|7|61|~").starts_with("ACK|1|JoinSession|resume_token="));
    let superseded = first.exchange("CMD|52|ClaimSim|52|7|9007199254740993");
    assert_eq!(superseded, "ERR|52|Superseded|ClaimSim|");
    rows.push(format!("supersede\tCMD|52|ClaimSim|52|7|9007199254740993\t{superseded}"));
    let reclaimed = other.exchange("CMD|3|ClaimSim|3|8|9007199254740993");
    assert!(reclaimed.starts_with("ACK|3|ClaimSim|9007199254740993"));
    rows.push(format!("claims_released\tCMD|3|ClaimSim|3|8|9007199254740993\t{reclaimed}"));
    rows.push("admin_listener=card58_not_claimed_by_this_gate".into());
    common::artifact("admin_and_supersede.txt", &rows);
    server.shutdown().unwrap();
}
