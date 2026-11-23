#![forbid(unsafe_code)]

use simlink_headless_server::cadence::{decide, effective_interval, Action, PlayerCadence, Thresholds};

#[test]
fn five_rows_default_override_arithmetic_and_clamps() {
    let mut rows = Vec::new();
    for t in [Thresholds::default(), Thresholds { warn_ticks: 20, hard_ticks: 60, max_interval_ms: 800.0 }] {
        for (lag, valid, flight, action, interval) in [
            (t.hard_ticks + 1, false, false, Action::PublishDelta, 200.0),
            (t.warn_ticks, true, false, Action::PublishDelta, 200.0),
            ((t.warn_ticks + t.hard_ticks) / 2, true, false, Action::SlowAndPublish, (200.0 + t.max_interval_ms) / 2.0),
            (t.hard_ticks, true, false, Action::SlowAndPublish, t.max_interval_ms),
            (t.hard_ticks + 1, true, false, Action::ForceFull, 200.0),
            (t.hard_ticks + 1, true, true, Action::Suppress, 200.0),
        ] {
            let d = decide(100 + lag, valid.then_some(100), flight, 200.0, t);
            assert_eq!(d.action, action);
            assert_eq!(d.interval_ms, interval);
            rows.push(format!("warn={}|hard={}|max={}|lag={lag}|valid={valid}|flight={flight}|action={action:?}|interval={interval}",
                t.warn_ticks, t.hard_ticks, t.max_interval_ms));
        }
        let d = decide(t.hard_ticks, Some(0), false, t.max_interval_ms * 2.0, t);
        assert_eq!(d.interval_ms, t.max_interval_ms);
        assert_eq!(effective_interval(&[d], 50.0), 100);
        assert_eq!(decide(1, Some(2), false, 200.0, t).action, Action::PublishDelta);
    }
    if let Some(dir) = std::env::var_os("CARD58_ARTIFACT_DIR") {
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::write(std::path::Path::new(&dir).join("cadence_table.txt"), rows.join("\n")).unwrap();
    }
}

#[test]
fn per_player_latch_only_clears_at_force_full_tick() {
    let mut slow = PlayerCadence::default();
    slow.ack(1);
    slow.force_full(9000);
    slow.ack(8000);
    assert_eq!(slow.decision(15000, 2000.0, Thresholds::default()).action, Action::Suppress);
    slow.ack(9000);
    assert_eq!(slow.decision(15001, 2000.0, Thresholds::default()).action, Action::ForceFull);
    let fast = PlayerCadence::default();
    assert_eq!(fast.decision(15001, 2000.0, Thresholds::default()).action, Action::PublishDelta);
}

#[test]
fn leave_ends_player_cadence_without_resetting_connection_protocol_state() {
    use simlink_headless_server::{ConnId, Server};
    use simlink_headless_server::messages::Command;
    let mut server = Server::new(1);
    let id = ConnId(1);
    server.connect(id); server.join(id, 10);
    server.handle_command(id, &Command { corr: 1, command_id: 1, player_id: 10,
        verb: "ClaimSim".into(), args: vec!["55".into()] }, |_| b"ACK|1|ClaimSim|55".to_vec()).unwrap();
    server.connection_mut(id).unwrap().cadence.ack(99);
    server.connection_mut(id).unwrap().cadence.force_full(100);
    server.leave(id);
    let conn = server.connection(id).unwrap();
    assert_eq!((conn.last_corr, conn.next_seq), (1, 2));
    assert_eq!(conn.cadence.ack_tick, None);
    assert_eq!(conn.cadence.resync_from_tick, None);
}
