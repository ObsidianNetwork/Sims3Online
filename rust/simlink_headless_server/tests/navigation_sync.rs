#![forbid(unsafe_code)]
//! Fixture-free Card 55 sync receipt checks, runnable in the clean-room export.

use std::{cell::RefCell, rc::Rc};

use simlink_bridge::{text, verbs::BridgeCommand};
use simlink_headless_server::{
    bridge_driver::{BridgeEndpoint, BridgeGameDriver},
    DriverError, GameDriver,
};

#[derive(Default)]
struct State {
    corrupt: Option<&'static str>,
    fail_index: usize,
    total_entries: usize,
    pages: Vec<Vec<String>>,
}

struct Endpoint(Rc<RefCell<State>>);

impl BridgeEndpoint for Endpoint {
    fn command(&mut self, command: &BridgeCommand) -> Result<Vec<String>, DriverError> {
        let mut state = self.0.borrow_mut();
        match command {
            BridgeCommand::RequestSnapshot { corr, .. } => {
                Ok(vec![format!("SNAPSHOT|{}|1|57|53|Normal|0|0", corr.to_n())])
            }
            BridgeCommand::SacsRecorder { corr, args } => {
                let body = if args[0] == "rust-route-on" {
                    "rust-route-on=accepted".to_owned()
                } else {
                    assert_eq!(args[0], "rust-navigation-sync");
                    state.pages.push(args[1..].to_vec());
                    let (epoch, index, count, page) = (&args[1], &args[2], &args[3], &args[4]);
                    let page_entries = if page == "none" { 0 } else { page.split(',').count() };
                    let idx: usize = index.parse().unwrap();
                    let total_pages: usize = count.parse().unwrap();
                    let active = if idx + 1 == total_pages { state.total_entries } else { 0 };
                    let body = format!(
                        "rust-navigation-sync=accepted;epoch={epoch};index={index};count={count};entries={page_entries};active={active}"
                    );
                    let corrupt = if idx == state.fail_index { state.corrupt.take() } else { None };
                    match corrupt {
                        Some("epoch") => body.replacen(&format!("epoch={epoch}"), "epoch=999", 1),
                        Some("index") => body.replacen(&format!("index={index}"), "index=999", 1),
                        Some("count") => body.replacen(&format!("count={count}"), "count=999", 1),
                        Some("entries") => body.replacen(&format!("entries={page_entries}"), "entries=999", 1),
                        Some("active") => body.replacen(&format!("active={active}"), "active=999", 1),
                        Some("operation") => body.replace("rust-navigation-sync", "rust-route-on"),
                        Some("suffix") => format!("{body};extra=1"),
                        Some("hex") => return Ok(vec![format!("AUTONOMY_PROBE|{}|ok|GG", corr.to_n())]),
                        Some("correlation") => {
                            return Ok(vec![format!("AUTONOMY_PROBE|00000000000000000000000000000000|ok|{}", text::encode_text(&body))]);
                        }
                        Some("status") => {
                            return Ok(vec![format!("AUTONOMY_PROBE|{}|refused|{}", corr.to_n(), text::encode_text(&body))]);
                        }
                        Some("kind") => {
                            return Ok(vec![format!("OK|{}|ok|{}", corr.to_n(), text::encode_text(&body))]);
                        }
                        None => body,
                        Some(other) => panic!("unknown corruption {other}"),
                    }
                };
                Ok(vec![format!("AUTONOMY_PROBE|{}|ok|{}", corr.to_n(), text::encode_text(&body))])
            }
            _ => {
                let encoded = simlink_bridge::verbs::encode(command).unwrap();
                let corr = encoded.split('|').nth(1).unwrap();
                Ok(vec![format!("OK|{corr}")])
            }
        }
    }

    fn events(&mut self) -> Result<Vec<Vec<u8>>, DriverError> { Ok(Vec::new()) }
}

#[test]
fn sync_rejects_mismatched_receipts_and_retries_page_zero_without_announcing() {
    for corrupt in ["epoch", "index", "count", "entries", "active", "operation", "suffix",
        "hex", "correlation", "status", "kind"] {
        let state = Rc::new(RefCell::new(State { corrupt: Some(corrupt), ..State::default() }));
        let mut driver = BridgeGameDriver::new(Endpoint(Rc::clone(&state)));
        driver.configure_rust_switches(true, false, true);

        let error = driver.poll_routed_events().expect_err(corrupt);
        assert!(error.0.contains("private bridge delivery refused"), "{corrupt}: {error}");
        assert_eq!(driver.take_epoch_announcement(), None, "{corrupt}: refused sync announced an epoch");
        assert!(driver.take_pending_events().is_empty(), "{corrupt}: refused sync emitted events");
        assert_eq!(state.borrow().pages, [vec!["1", "0", "1", "none"]], "{corrupt}");

        // An uncertain receipt does not commit nav_synced; retry the same epoch from page zero.
        assert!(driver.poll_routed_events().unwrap().is_empty(), "{corrupt}");
        assert_eq!(state.borrow().pages, [vec!["1", "0", "1", "none"], vec!["1", "0", "1", "none"]], "{corrupt}");
        assert_eq!(driver.take_epoch_announcement(), Some(1), "{corrupt}");
        driver.announced(1);
        let events = driver.poll_routed_events().unwrap();
        assert_eq!(events.len(), 1, "{corrupt}: one accepted epoch event");
        assert_eq!(events[0].event.kind, "SimAnimation");
        assert_eq!(events[0].event.corr, 0);
        assert_eq!(simlink_headless_server::messages::dec_list(&events[0].event.fields[0]).unwrap(), ["307c4e657c31"]);
        assert_eq!(state.borrow().pages.len(), 2, "{corrupt}: committed sync repeated");
        assert_eq!(driver.take_epoch_announcement(), None);
        assert!(driver.poll_routed_events().unwrap().is_empty());
    }
}

#[test]
fn multipage_sync_parses_non_final_pages_and_restarts_from_page_zero_after_later_failure() {
    use simlink_bridge::text::encode_text;
    use simlink_headless_server::{messages::Command, ConnId};

    let state = Rc::new(RefCell::new(State { total_entries: 320, fail_index: 1, corrupt: None, ..State::default() }));
    let mut driver = BridgeGameDriver::new(Endpoint(Rc::clone(&state)));
    // Register 1 joined session and 320 claimed Sims before enabling nav_sync so sync sees >= 3 pages.
    let join = Command {
        corr: 1, command_id: 1, player_id: 7,
        verb: "JoinSession".into(), args: vec![encode_text("Fixture"), "~".into()],
    };
    assert!(driver.adjudicate_for(ConnId(1), &join).unwrap().starts_with(b"ACK|"));
    for i in 0..320u64 {
        let corr = 2 + i;
        let cmd = Command {
            corr, command_id: corr, player_id: 7,
            verb: "ClaimSim".into(), args: vec![(u64::MAX - i).to_string()],
        };
        assert!(driver.adjudicate_for(ConnId(1), &cmd).unwrap().starts_with(b"ACK|"));
    }
    let _ = driver.take_pending_events();

    state.borrow_mut().corrupt = Some("epoch");
    driver.configure_rust_switches(true, false, true);
    let error = driver.poll_routed_events().expect_err("page 1 mismatch must abort sync");
    assert!(error.0.contains("private bridge delivery refused"), "{error}");
    assert_eq!(driver.take_epoch_announcement(), None);
    let sent_before: Vec<_> = state.borrow().pages.iter().map(|p| (p[1].clone(), p[2].clone())).collect();
    let total_pages: usize = sent_before[0].1.parse().unwrap();
    assert!(total_pages >= 3, "expected >= 3 pages for non-final parsing coverage, got {total_pages}");
    assert_eq!(sent_before, vec![
        ("0".into(), total_pages.to_string()),
        ("1".into(), total_pages.to_string()),
    ]);

    state.borrow_mut().pages.clear();
    assert!(driver.poll_routed_events().unwrap().is_empty());
    let sent_retry: Vec<_> = state.borrow().pages.iter().map(|p| p[1].clone()).collect();
    let expected: Vec<_> = (0..total_pages).map(|i| i.to_string()).collect();
    assert_eq!(sent_retry, expected, "retry after page 1 failure must restart from index 0");
    assert_eq!(driver.take_epoch_announcement(), Some(1));
}
