#![forbid(unsafe_code)]
//! Card 57 §E: sessions, order, idempotency, epoch, supersede, the authority gate, and the
//! `IdMap`. Also the in-process harness that produces gate 7's `order_probe.jsonl` (§H row 7).

use simlink_headless_server::idmap::IdMap;
use simlink_headless_server::messages::{Command, RefusalCode};
use simlink_headless_server::session::{ConnId, Server};
use sim96_kernel_cleanroom::{runtime_bridge::SERVER_PRIMARY_SIM, SimId};

fn cmd(corr: u64, command_id: u64, player: i32, verb: &str) -> Command {
    Command {
        corr,
        verb: verb.to_string(),
        command_id,
        player_id: player,
        args: vec![],
    }
}

// ---- §E: order and idempotency -----------------------------------------------------

#[test]
fn replayed_command_id_returns_the_original_reply_byte_for_byte() {
    let mut srv = Server::new(1);
    let c = ConnId(10);
    srv.connect(c);
    srv.join(c, 7);
    let reply_for = |k: &Command| format!("ACK|{}|ClaimSim|ok{}", k.corr, k.command_id).into_bytes();

    let first = srv.handle_command(c, &cmd(1, 100, 7, "ClaimSim"), reply_for).unwrap();
    assert!(!first.replayed);
    // A retry reuses corr and commandId; adjudication must NOT run a second time (§D.1).
    let replay = srv
        .handle_command(c, &cmd(1, 100, 7, "ClaimSim"), |_| {
            panic!("adjudicate ran on a replayed commandId")
        })
        .unwrap();
    assert!(replay.replayed);
    assert_eq!(replay.reply, first.reply);
    assert_eq!(replay.seq, first.seq);
}

#[test]
fn seq_is_strictly_increasing_per_client_within_the_epoch() {
    let mut srv = Server::new(1);
    let c = ConnId(1);
    srv.connect(c);
    srv.join(c, 3);
    let mut seen = Vec::new();
    for i in 0..5u64 {
        let o = srv
            .handle_command(c, &cmd(i + 1, 1000 + i, 3, "QueueInteraction"), |k| {
                format!("ACK|{}", k.corr).into_bytes()
            })
            .unwrap();
        seen.push(o.seq);
    }
    assert_eq!(seen, vec![1, 2, 3, 4, 5]);
}

#[test]
fn a_new_command_must_have_a_strictly_increasing_corr() {
    let mut srv = Server::new(1);
    let c = ConnId(1);
    srv.connect(c);
    srv.join(c, 3);
    srv.handle_command(c, &cmd(5, 200, 3, "ClaimSim"), |_| b"ACK|5".to_vec())
        .unwrap();
    // A brand-new commandId with a lower corr is a reissue the kernel gate drops (§D.1).
    let err = srv
        .handle_command(c, &cmd(4, 201, 3, "ClaimSim"), |_| b"x".to_vec())
        .unwrap_err();
    assert_eq!(err.code, RefusalCode::BadLine);
}

#[test]
fn a_restart_starts_a_new_epoch() {
    let srv1 = Server::new(1);
    let mut srv2 = Server::new(2);
    assert_ne!(srv1.epoch, srv2.epoch);
    // In the new epoch a client's event seq starts from 1 again (§E).
    let c = ConnId(1);
    srv2.connect(c);
    srv2.join(c, 9);
    let o = srv2
        .handle_command(c, &cmd(1, 500, 9, "ClaimSim"), |_| b"ACK".to_vec())
        .unwrap();
    assert_eq!(o.seq, 1);
}

#[test]
fn next_event_seq_advances_independently_of_commands() {
    let mut srv = Server::new(7);
    let c = ConnId(2);
    srv.connect(c);
    assert_eq!(srv.next_event_seq(c), Some(1));
    assert_eq!(srv.next_event_seq(c), Some(2));
    assert_eq!(srv.next_event_seq(ConnId(999)), None);
}

// ---- §E: the authority gate --------------------------------------------------------

#[test]
fn unjoined_connection_is_refused_not_joined() {
    let mut srv = Server::new(1);
    let c = ConnId(1);
    srv.connect(c);
    let err = srv
        .handle_command(c, &cmd(1, 10, 7, "ClaimSim"), |_| b"x".to_vec())
        .unwrap_err();
    assert_eq!(err.code, RefusalCode::NotJoined);
    assert_eq!(err.corr, 1);
}

#[test]
fn join_session_is_allowed_when_unjoined() {
    let mut srv = Server::new(1);
    let c = ConnId(1);
    srv.connect(c);
    let o = srv
        .handle_command(c, &cmd(1, 10, 7, "JoinSession"), |_| b"ACK".to_vec())
        .unwrap();
    assert_eq!(o.seq, 1);
}

#[test]
fn unjoined_read_only_requests_are_not_refused_not_joined() {
    // §E, §H row 8 (F3): the controller's authority gate lets an unjoined connection send
    // `JoinSession` plus the three read-only requests, because a client downloads the world on a
    // separate, unjoined connection. Each must reach adjudication, not be refused `NotJoined`.
    // The front door's decoder validates `Verb` as a token (not a fixed enum) and there is no verb
    // table before the gate, so these verbs do reach it — traced end to end, not just the gate.
    for verb in [
        "RequestWorld",
        "RequestSnapshot",
        "RequestSimDescription",
    ] {
        let mut srv = Server::new(1);
        let c = ConnId(1);
        srv.connect(c); // deliberately never joined
        let outcome = srv
            .handle_command(c, &cmd(1, 10, 7, verb), |k| {
                format!("ACK|{}|{}", k.corr, k.verb).into_bytes()
            })
            .unwrap_or_else(|err| panic!("{verb} on an unjoined connection was refused {:?}", err.code));
        assert!(!outcome.replayed);
        // Today it is answered by the injected adjudication (the offline seam's ACK), not a refusal.
        assert_eq!(outcome.reply, format!("ACK|1|{verb}").into_bytes());
    }
}

#[test]
fn an_unjoined_write_is_still_refused_not_joined() {
    // The unjoined allowance is only JoinSession + the three read-only requests; a world-changing
    // verb on an unjoined connection stays `NotJoined` (F3 must not widen the gate further).
    let mut srv = Server::new(1);
    let c = ConnId(1);
    srv.connect(c);
    let err = srv
        .handle_command(c, &cmd(1, 10, 7, "ClaimSim"), |_| b"x".to_vec())
        .unwrap_err();
    assert_eq!(err.code, RefusalCode::NotJoined);
}

#[test]
fn a_joined_read_only_request_for_another_player_is_wrong_player() {
    // §E: the read-only allowance is for an *unjoined* connection. Once joined, a command for
    // another player is `WrongPlayer` even when its verb is read-only (the oracle's joined branch).
    let mut srv = Server::new(1);
    let c = ConnId(1);
    srv.connect(c);
    srv.join(c, 7);
    let err = srv
        .handle_command(c, &cmd(1, 10, 8, "RequestWorld"), |_| b"x".to_vec())
        .unwrap_err();
    assert_eq!(err.code, RefusalCode::WrongPlayer);
}

#[test]
fn a_command_for_another_player_is_wrong_player() {
    let mut srv = Server::new(1);
    let c = ConnId(1);
    srv.connect(c);
    srv.join(c, 7);
    let err = srv
        .handle_command(c, &cmd(1, 10, 8, "ClaimSim"), |_| b"x".to_vec())
        .unwrap_err();
    assert_eq!(err.code, RefusalCode::WrongPlayer);
}

#[test]
fn a_rejoin_supersedes_the_earlier_connection() {
    let mut srv = Server::new(1);
    let a = ConnId(1);
    let b = ConnId(2);
    srv.connect(a);
    srv.connect(b);
    srv.join(a, 7);
    let superseded = srv.join(b, 7);
    assert_eq!(superseded, Some(a));
    // The old connection's next command is Superseded (§E).
    let err = srv
        .handle_command(a, &cmd(9, 300, 7, "ClaimSim"), |_| b"x".to_vec())
        .unwrap_err();
    assert_eq!(err.code, RefusalCode::Superseded);
    // The new connection is the active one for the player.
    assert_eq!(srv.active_connection(7), Some(b));
    assert!(!srv
        .handle_command(b, &cmd(1, 301, 7, "ClaimSim"), |_| b"ACK".to_vec())
        .unwrap()
        .replayed);
}

// ---- §E: the IdMap -----------------------------------------------------------------

#[test]
fn idmap_assigns_kernel_ids_in_first_seen_order_reserving_the_primary_sim() {
    let mut m = IdMap::new();
    let a = m.map_wire(1000).unwrap();
    let b = m.map_wire(2000).unwrap();
    let c = m.map_wire(3000).unwrap();
    assert_eq!(a, SimId(0));
    // SimId(1) == SERVER_PRIMARY_SIM is skipped, never assigned to a wire id.
    assert_eq!(b, SimId(2));
    assert_eq!(c, SimId(3));
    assert_ne!(a, SERVER_PRIMARY_SIM);
    assert_ne!(b, SERVER_PRIMARY_SIM);
    // A wire id maps to the same kernel id every time.
    assert_eq!(m.map_wire(1000).unwrap(), a);
    // Every kernel id maps back to its wire id.
    assert_eq!(m.kernel_to_wire(SimId(2)), Some(2000));
    assert_eq!(m.kernel_to_wire(SERVER_PRIMARY_SIM), None);
}

#[test]
fn idmap_is_injective() {
    let mut m = IdMap::new();
    let mut kernels = std::collections::HashSet::new();
    for wire in 0..100u64 {
        let k = m.map_wire(wire).unwrap();
        assert!(kernels.insert(k), "kernel id {k:?} handed out twice");
        assert_eq!(m.kernel_to_wire(k), Some(wire));
    }
    assert_eq!(m.len(), 100);
}

#[test]
fn a_full_idmap_is_refused_id_space_full() {
    let mut m = IdMap::starting_at(u32::MAX);
    assert_eq!(m.map_wire(7), Err(RefusalCode::IdSpaceFull));
}

// ---- gate 7 artifact: order_probe.jsonl --------------------------------------------

#[test]
fn emit_order_probe_artifact() {
    let Some(dir) = std::env::var_os("CARD57_ARTIFACT_DIR") else {
        return; // cargo test stays hermetic unless a run folder is asked for
    };
    let mut rows = Vec::new();

    let mut srv = Server::new(1);
    let c = ConnId(1);
    srv.connect(c);
    srv.join(c, 7);
    let reply_for = |k: &Command| format!("ACK|{}|ClaimSim|{}", k.corr, k.command_id).into_bytes();
    let first = srv.handle_command(c, &cmd(1, 100, 7, "ClaimSim"), reply_for).unwrap();
    let replay = srv
        .handle_command(c, &cmd(1, 100, 7, "ClaimSim"), |_| unreachable!())
        .unwrap();
    rows.push(format!(
        "{{\"case\":\"idempotent_replay\",\"command_id\":100,\"first\":\"{}\",\"replay\":\"{}\",\"equal\":{}}}",
        hex(&first.reply),
        hex(&replay.reply),
        first.reply == replay.reply
    ));

    let mut seqs = Vec::new();
    for i in 0..4u64 {
        let o = srv
            .handle_command(c, &cmd(i + 2, 200 + i, 7, "QueueInteraction"), |k| {
                format!("ACK|{}", k.corr).into_bytes()
            })
            .unwrap();
        seqs.push(o.seq);
    }
    let strictly = seqs.windows(2).all(|w| w[1] > w[0]);
    rows.push(format!(
        "{{\"case\":\"seq_strictly_increasing\",\"epoch\":1,\"seqs\":{},\"strictly_increasing\":{}}}",
        serde_ints(&seqs),
        strictly
    ));

    let mut srv2 = Server::new(2);
    srv2.connect(c);
    srv2.join(c, 7);
    let after_restart = srv2
        .handle_command(c, &cmd(1, 900, 7, "ClaimSim"), |_| b"ACK".to_vec())
        .unwrap();
    rows.push(format!(
        "{{\"case\":\"restart_new_epoch\",\"old_epoch\":1,\"new_epoch\":2,\"seq_restarts_at\":{}}}",
        after_restart.seq
    ));

    let mut ids = IdMap::new();
    let k0 = ids.map_wire(111).unwrap().0;
    let k1 = ids.map_wire(222).unwrap().0;
    rows.push(format!(
        "{{\"case\":\"idmap_first_seen\",\"wire0_kernel\":{k0},\"wire1_kernel\":{k1},\"primary_sim_reserved\":{}}}",
        k1 != SERVER_PRIMARY_SIM.0 && k0 != SERVER_PRIMARY_SIM.0
    ));

    std::fs::create_dir_all(&dir).unwrap();
    let path = std::path::Path::new(&dir).join("order_probe.jsonl");
    std::fs::write(&path, format!("{}\n", rows.join("\n")))
        .expect("write order_probe.jsonl");
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

fn serde_ints(vals: &[u64]) -> String {
    let inner = vals
        .iter()
        .map(|v| v.to_string())
        .collect::<Vec<_>>()
        .join(",");
    format!("[{inner}]")
}
