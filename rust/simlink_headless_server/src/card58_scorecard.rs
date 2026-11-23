#![forbid(unsafe_code)]
//! Evidence-derived offline scorecard. Live gates cannot be inferred from offline zeros.

use std::{fs, io, path::Path};
use serde_json::{json, Value};
use crate::{messages::Event, routing::RoutedEvent, ConnId, Server};

const STATE_OWNERSHIP: &str = include_str!("../fixtures/card58_state_ownership.md");
const ADMIN_TOOL_CATALOGUE: &str = include_str!("../fixtures/card58_admin_tool_catalogue.md");

pub fn state_classifications(markdown: &str) -> io::Result<(usize, usize)> {
    let mut fields = 0;
    let mut duplicated = 0;
    for line in markdown.lines().filter(|line| line.starts_with('|')) {
        let columns: Vec<_> = line.split('|').map(str::trim).collect();
        if columns.len() != 6 || columns[3] == "Classification" || columns[3].starts_with("---") { continue; }
        match columns[3] {
            "single-owner" | "stale-able" => fields += 1,
            "duplicated" => { fields += 1; duplicated += 1; }
            _ => return Err(io::Error::other(format!("unclassified ownership row: {line}"))),
        }
    }
    if fields == 0 { return Err(io::Error::other("empty ownership inventory")); }
    Ok((fields, duplicated))
}

fn admin_tool_count(markdown: &str) -> io::Result<usize> {
    let admin_contract = markdown.split_once("## F.").and_then(|(_, section)| section.split_once("## G."))
        .map(|(section, _)| section).ok_or_else(|| io::Error::other("missing admin tool catalogue"))?;
    let tools: std::collections::BTreeSet<_> = admin_contract.split('`')
        .filter_map(|token| token.strip_prefix("scripts/")).filter(|name| name.ends_with(".py")).collect();
    if tools.is_empty() { return Err(io::Error::other("empty bridgectl tool catalogue")); }
    Ok(tools.len())
}

pub fn routing_probe() -> (Vec<Value>, u64) {
    let mut server = Server::new(1);
    for id in 1..=3 { server.connect(ConnId(id)); }
    server.join(ConnId(1), 10);
    server.join(ConnId(2), 20);
    server.set_admin(ConnId(3));
    let mut rows = Vec::new();
    let mut leaks = 0;
    for (kind, owner, requester, expected) in [
        ("DialogRequested", Some(10), None, vec![1]),
        ("DialogRequested", Some(20), None, vec![2]),
        ("PickFailed", None, Some(ConnId(1)), vec![1]),
        ("SimDescriptionData", None, Some(ConnId(2)), vec![2]),
        ("WorldFileChunk", None, Some(ConnId(1)), vec![1]),
        ("SimAnimation", None, None, vec![1, 2]),
        ("SnapshotDelta", None, None, vec![1, 2]),
    ] {
        let routed = RoutedEvent { event: Event { seq: 0, tick: 1, corr: 0, kind: kind.into(), fields: Vec::new() }, owner, requester };
        let actual: Vec<_> = crate::routing::recipients(&server, &routed).iter().map(|id| id.0).collect();
        leaks += actual.iter().filter(|id| !expected.contains(id)).count() as u64;
        rows.push(json!({"kind":kind,"recipients":actual,"expected":expected}));
    }
    rows.push(json!({"leak_events":leaks}));
    (rows, leaks)
}

pub fn emit(_root: &Path, dir: &Path) -> io::Result<()> {
    let text = fs::read_to_string(dir.join("trace_diff.jsonl"))?;
    let summary: Value = serde_json::from_str(text.lines().last().ok_or_else(|| io::Error::other("missing trace summary"))?).map_err(io::Error::other)?;
    let metric = |key: &str| summary.get(key).and_then(Value::as_u64).ok_or_else(|| io::Error::other(format!("missing trace metric {key}")));
    let frames_in = metric("frames_in")?;
    let frames_relayed = metric("frames_relayed")?;
    let frames_dropped = metric("frames_dropped")?;
    let seq_regressions = metric("seq_regressions")?;
    let (rows, routing_leaks) = routing_probe();
    let leak_events = routing_leaks + metric("leak_events")?;
    let (state_fields, duplicated_state_fields) = state_classifications(STATE_OWNERSHIP)?;
    let non_loopback_default = crate::bind::BindPosture::default_loopback().non_loopback_default()
        + usize::from(!crate::admin::resolve_endpoint(crate::admin::DEFAULT_ENDPOINT).map_err(io::Error::other)?.ip().is_loopback());
    let history_trimmed = metric("history_trimmed")?;
    let history_trimmed_oracle = metric("history_trimmed_oracle")?;
    let bridgectl_tools_total = admin_tool_count(ADMIN_TOOL_CATALOGUE)?;
    if frames_in == 0 || frames_in != frames_relayed || frames_dropped != 0 || seq_regressions != 0
        || leak_events != 0 || duplicated_state_fields != 0 || non_loopback_default != 0 || history_trimmed != history_trimmed_oracle
        || summary.get("equal") != Some(&Value::Bool(true)) {
        return Err(io::Error::other("Card 58 offline invariant failed"));
    }
    fs::write(dir.join("routing_table.jsonl"), rows.iter().map(|r| format!("{r}\n")).collect::<String>())?;
    fs::write(dir.join("scorecard.txt"), format!(
        "frames_in={frames_in}|frames_relayed={frames_relayed}|frames_dropped={frames_dropped}|seq_regressions={seq_regressions}|leak_events={leak_events}|history_trimmed={history_trimmed}|history_trimmed_oracle={history_trimmed_oracle}|duplicated_state_fields={duplicated_state_fields}|state_fields={state_fields}|non_loopback_default={non_loopback_default}|tick_drops_during_join=unmeasured|presentation_sent_gap_max_during_join=unmeasured|bridgectl_tools_passing=unmeasured|bridgectl_tools_total={bridgectl_tools_total}|live_gates=not_run|full_recorded_snapshot_parity={}\n",
        summary.get("full_recorded_snapshot_parity").and_then(Value::as_bool).unwrap_or(false)
    ))
}

#[cfg(test)]
mod tests {
    use super::{admin_tool_count, state_classifications, ADMIN_TOOL_CATALOGUE, STATE_OWNERSHIP};

    #[test]
    fn tracked_scorecard_fixtures_match_current_rust_surface() {
        assert_eq!(state_classifications(STATE_OWNERSHIP).unwrap(), (4, 0));
        assert_eq!(admin_tool_count(ADMIN_TOOL_CATALOGUE).unwrap(), 2);
    }

    #[test]
    fn admin_tool_catalogue_rejects_missing_or_empty_sections() {
        assert!(admin_tool_count("no sections").is_err());
        assert!(admin_tool_count("## F. Tools\nnone\n## G. Next").is_err());
    }
}
