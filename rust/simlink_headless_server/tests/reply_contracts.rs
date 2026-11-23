#![forbid(unsafe_code)]

mod common;

#[test]
#[cfg(feature = "repo-integration")]
fn five_sidecar_contracts_receive_named_v1_replies_over_tcp() {
    let server = common::server();
    let mut peer = common::Peer::connect(&server);
    let mut rows = Vec::new();
    for (case, request, expected) in [
        ("UnjoinedClaim", "CMD|41|ClaimSim|41|7|9", "ERR|41|NotJoined|ClaimSim|"),
        ("Join", "CMD|42|JoinSession|42|7|616c696365|~", "ACK|42|JoinSession"),
        ("DuplicateJoin", "CMD|43|JoinSession|43|8|616c696365|~", "ERR|43|WrongPlayer|JoinSession|"),
        ("TruncatedCommand", "CMD|44|ClaimSim|44", "ERR|44|bad_line|ClaimSim|"),
        ("UnknownTypeCommand", "CMD|45|NoSuchVerb|45|7", "ERR|45|bad_line|NoSuchVerb|"),
        ("IdlessGarbage", "garbage", "ERR|0|bad_line|-|"),
    ] {
        let reply = peer.exchange(request);
        if case == "Join" {
            let issued = reply.strip_prefix(&format!("{expected}|resume_token=")).expect("join credential");
            assert!(simlink_headless_server::wire_v1::valid_resume_token(issued));
        } else {
            assert_eq!(reply, expected, "{case}");
        }
        if case != "Join" {
            rows.push(format!("{case}\t{request}\t{reply}"));
        }
    }
    assert_eq!(rows.len(), 5);
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
    let source = std::fs::read_to_string(root.join("DedicatedServer.Tests/RustAuthoritySidecarTests.cs")).unwrap();
    let fixture = source.split("class SidecarCommandReplyTests").nth(1).unwrap();
    assert_eq!(fixture.matches("[Test]").count(), 5, "oracle fixture changed");
    common::artifact("reply_contracts.txt", &rows);
    server.shutdown().unwrap();
}

#[test]
fn card60_replays_the_five_recorded_v1_reply_contract_files() {
    let corpus = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/v1_reply_corpus");
    let names = [
        "DuplicateJoin.txt", "IdlessGarbage.txt", "TruncatedCommand.txt",
        "UnjoinedClaim.txt", "UnknownTypeCommand.txt",
    ];
    let mut files: Vec<_> = std::fs::read_dir(&corpus).unwrap()
        .map(|entry| entry.unwrap().file_name().into_string().unwrap()).collect();
    files.sort();
    assert_eq!(files, names, "Card 60 A1 requires exactly one file per reply contract");
    let server = common::server();
    let mut rows = Vec::new();
    for name in names {
        let mut peer = common::Peer::connect(&server);
        let transcript = std::fs::read_to_string(corpus.join(name)).unwrap();
        let mut count = 0;
        for row in transcript.lines() {
            let (request, expected) = row.split_once('\t').expect("request TAB reply");
            let reply = peer.exchange(request);
            let actual = if expected.starts_with("ACK|") && request.split('|').nth(2) == Some("JoinSession") {
                let issued = reply.strip_prefix(&format!("{expected}|resume_token=")).expect("join credential");
                assert!(simlink_headless_server::wire_v1::valid_resume_token(issued), "{name}");
                expected
            } else {
                &reply
            };
            assert_eq!(actual, expected, "{name}: {request}");
            rows.push(format!("{name}\t{request}\t{actual}"));
            count += 1;
        }
        assert!(count > 0, "empty reply contract: {name}");
    }
    common::artifact("v1_reply_corpus_replay.txt", &rows);
    server.shutdown().unwrap();
}
