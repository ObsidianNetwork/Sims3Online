#![forbid(unsafe_code)]
//! Offline input adapter. It uses the resident relay's decoders and recipient gate.

use std::io::{self, Write};
use serde_json::{json, Value};
use crate::{bridge_driver::{BridgeGameDriver, OfflineBridge}, messages as m, routing::RoutedEvent, ConnId, GameDriver, Server};

struct Replay {
    server: Server,
    driver: BridgeGameDriver<OfflineBridge>,
    tick: u64,
    last_time: Option<crate::world_time::WorldTime>,
    last_wall: u64,
    pump: crate::stream::Stream,
}
struct RecordedPump {
    claims: Vec<u64>,
    recorder: std::collections::VecDeque<Value>,
}
impl GameDriver for RecordedPump {
    fn adjudicate(&mut self, _: &m::Command) -> Result<Vec<u8>, crate::DriverError> { unreachable!() }
    fn poll_event_lines(&mut self) -> Result<Vec<Vec<u8>>, crate::DriverError> { Ok(Vec::new()) }
    fn claimed_sims(&self) -> Vec<u64> { self.claims.clone() }
    fn recorder(&mut self, args: &[String]) -> Result<String, crate::DriverError> {
        // The script lists a pump's requests in the controller's order (history, then the live dump). This server
        // interleaves the live dump with the history pages so established Sims keep moving during a gather, so a
        // request is answered by the first scripted reply with the same arguments, not by the next one in line.
        // The frames sent to clients are what the differential compares.
        let wanted = json!(args);
        // A history page request also carries this server's snapshot token (and the targets for the boundary
        // handle), which the script cannot know: a scripted reply answers a request it is a prefix of. A reply
        // without snapshot metadata is taken as an untokenized page.
        // This server may ask for the live lines once more in a pump than the controller did (after a history
        // gather ends). The script has no reply for that; the recorder's answer when nothing new was recorded is
        // an empty dump at the cursor asked from.
        if args.first().is_some_and(|name| name == "dump") && args.len() >= 2
            && !self.recorder.iter().any(|packet| packet["args"][0] == "dump") {
            return Ok(format!("seq={};more=0", args[1]));
        }
        let at = self.recorder.iter().position(|packet| packet["args"] == wanted
                || (args.first().is_some_and(|name| name == "snapshot-driver")
                    && packet["args"].as_array().is_some_and(|scripted| scripted.len() >= 3 && scripted.len() < args.len()
                        && scripted.iter().zip(args).all(|(scripted, asked)| scripted.as_str() == Some(asked.as_str())))))
            .ok_or_else(|| crate::DriverError(format!("unexpected recorder request {args:?}; scripted: {:?}",
                self.recorder.iter().map(|packet| packet["args"].to_string()).collect::<Vec<_>>())))?;
        let packet = self.recorder.remove(at).expect("position is in range");
        text(&packet, "report").map(str::to_owned).map_err(|e| crate::DriverError(e.to_string()))
    }
}
fn text<'a>(row: &'a Value, key: &str) -> io::Result<&'a str> {
    row.get(key).and_then(Value::as_str).ok_or_else(|| io::Error::other(format!("missing {key}")))
}
fn number(row: &Value, key: &str) -> io::Result<u64> {
    row.get(key).and_then(Value::as_u64).ok_or_else(|| io::Error::other(format!("missing {key}")))
}
impl Replay {
    fn new() -> Self {
        Self { server: Server::new(1), driver: BridgeGameDriver::new(OfflineBridge), tick: 0, last_time: None, last_wall: 0,
            pump: crate::stream::Stream::new(crate::stream::StreamOptions { base_interval_ms: 0.0, ..crate::stream::StreamOptions::default() }) }
    }
    fn event(&self, kind: &str, fields: Vec<String>) -> m::Event {
        m::Event { seq: 0, tick: self.tick, corr: 0, kind: kind.into(), fields }
    }
    fn route(&mut self, event: m::Event, owner: Option<i32>, requester: Option<ConnId>) -> Vec<Value> {
        let routed = RoutedEvent { event, owner, requester };
        crate::routing::recipients(&self.server, &routed).into_iter().map(|id| {
            json!({"connection":id.0,"seq":self.server.next_event_seq(id).expect("offline seq"),"tick":self.tick,
                "kind":routed.event.kind,"fields":routed.event.fields})
        }).collect()
    }
    fn apply(&mut self, row: &Value) -> io::Result<Vec<Value>> {
        if let Some(tick) = row.get("tick").and_then(Value::as_u64) { self.tick = tick; }
        let mut output = Vec::new();
        let requester = row.get("requester").and_then(Value::as_u64).map(ConnId);
        match text(row, "op")? {
            "join" | "admin" => {
                let id = ConnId(number(row, "connection")?);
                self.server.connect(id);
                if text(row, "op")? == "admin" { self.server.set_admin(id); }
                else { self.server.join(id, number(row, "player")? as i32); self.pump.joined(id); }
            }
            "claim" | "release" => {
                let reply = self.driver.adjudicate(&m::Command { corr: 1, command_id: 1, player_id: number(row, "player")? as i32,
                    verb: if text(row, "op")? == "claim" { "ClaimSim" } else { "ReleaseSim" }.into(),
                    args: vec![number(row, "sim")?.to_string()] }).map_err(io::Error::other)?;
                if !reply.starts_with(b"ACK|") { return Err(io::Error::other("fixture claim rejected")); }
            }
            "pump" => {
                let mut source = RecordedPump { claims: self.driver.claimed_sims(),
                    recorder: row.get("recorder").and_then(Value::as_array).ok_or_else(|| io::Error::other("missing recorder inputs"))?.iter().cloned().collect() };
                let events = self.pump.pump(number(row, "now")?, &mut self.server, &mut source).map_err(io::Error::other)?;
                if !source.recorder.is_empty() { return Err(io::Error::other("unused recorder inputs")); }
                for routed in events {
                    self.tick = routed.event.tick;
                    output.extend(self.route(routed.event, routed.owner, routed.requester));
                }
            }
            "event" => {
                let event = crate::events::decode_bridge_event(text(row, "line")?, self.tick).map_err(io::Error::other)?;
                let owner = if event.kind == "DialogRequested" {
                    event.fields.get(1).and_then(|s| s.parse().ok()).and_then(|sim| self.driver.owner(sim))
                } else { None };
                output = self.route(event, owner, requester);
            }
            "presentation" => {
                let (_, _, lines) = crate::presentation::parse_dump(text(row, "report")?);
                for batch in lines.chunks(crate::presentation::MAX_LINES) {
                    output.extend(self.route(self.event("SimAnimation", vec![m::enc_list(&batch.iter().map(|s| m::hex_encode(s)).collect::<Vec<_>>())]), None, None));
                }
            }
            "snapshot" => {
                let bytes = crate::wire_v1::b64_decode(text(row, "body")?).map_err(io::Error::other)?;
                let snapshot = crate::snapshot::Snapshot::decode(&bytes).ok_or_else(|| io::Error::other("snapshot decode failed"))?;
                self.tick = snapshot.tick.max(0) as u64;
                output = self.route(self.event("SnapshotDelta", vec![snapshot.wire_body(),
                    m::enc_bool(row.get("force_full").and_then(Value::as_bool).unwrap_or(false)).into()]), None, requester);
            }
            "routed" => {
                let fields = row.get("fields").and_then(Value::as_array).ok_or_else(|| io::Error::other("missing fields"))?
                    .iter().map(|s| s.as_str().map(str::to_owned).ok_or_else(|| io::Error::other("bad field"))).collect::<io::Result<Vec<_>>>()?;
                output = self.route(self.event(text(row, "kind")?, fields), None, requester);
            }
            "diag" => {
                let now = number(row, "now")?;
                if let Some(time) = crate::world_time::parse_diag(text(row, "report")?) {
                    if crate::world_time::should_reconcile(time, self.last_time, now.saturating_sub(self.last_wall)) {
                        self.last_time = Some(time);
                        self.last_wall = now;
                        output = self.route(self.event("WorldTimeChanged", vec![m::enc_record(
                            &[time.day.to_string(), time.hours.to_string(), time.minutes.to_string()])]), None, None);
                    }
                }
            }
            "history" => {
                let mut pages = row.get("pages").and_then(Value::as_array).ok_or_else(|| io::Error::other("missing history pages"))?.iter();
                let history = crate::presentation::gather_history(&[55], |args| {
                    let report = if args[0] == "snapshot" { text(row, "drivers") } else {
                        pages.next().ok_or_else(|| io::Error::other("history page missing")).and_then(|page| text(page, "report"))
                    }.map_err(|e| crate::DriverError(e.to_string()))?;
                    Ok(report.to_owned())
                }).map_err(io::Error::other)?;
                output.push(json!({"history_trimmed":history.history_trimmed}));
                for batch in history.batches() {
                    output.extend(self.route(self.event("SimAnimation", vec![m::enc_list(&batch.iter().map(|s| m::hex_encode(s)).collect::<Vec<_>>())]),
                        None, Some(ConnId(number(row, "connection")?))));
                }
            }
            "cadence" => {
                let d = crate::cadence::decide(number(row, "tick")? as i64, row.get("ack").and_then(Value::as_i64),
                    row.get("in_flight").and_then(Value::as_bool).unwrap_or(false),
                    row.get("base").and_then(Value::as_f64).ok_or_else(|| io::Error::other("missing base"))?,
                    crate::cadence::Thresholds { warn_ticks: number(row, "warn")? as i64, hard_ticks: number(row, "hard")? as i64,
                        max_interval_ms: row.get("maximum").and_then(Value::as_f64).ok_or_else(|| io::Error::other("missing maximum"))? });
                output.push(json!({"action":format!("{:?}",d.action),"interval_ms":d.interval_ms}));
            }
            "bulk_metadata" => {
                let line = text(row, "line")?;
                let fields: Vec<_> = line.split('|').collect();
                let slot = fields.get(3).and_then(|v| v.parse::<u64>().ok()).ok_or_else(|| io::Error::other("bad bulk metadata"))?;
                output.push(json!({"bulk":fields[2],"slot":slot,"payload_missing":true}));
            }
            "bridge_command" => {}
            "admin_reply" => {
                let reply = crate::admin::bridge_reply(&[text(row, "line")?.into()]).map_err(io::Error::other)?;
                output.push(json!({"admin_response":reply}));
            }
            "bridge_reply" => {
                let response = simlink_bridge::reply::decode_response(text(row, "line")?).map_err(|e| io::Error::other(format!("{e:?}")))?;
                output.push(json!({"bridge_reply":response.is_some(),"accepted":response.map(|r| r.accepted)}));
            }
            op => return Err(io::Error::other(format!("unknown trace operation {op}"))),
        }
        Ok(output)
    }
}
pub fn stdio() -> io::Result<()> {
    let mut replay = Replay::new();
    let mut stdout = io::stdout().lock();
    let mut input = io::stdin().lock();
    while let Some(line) = crate::serve::read_bounded_line(&mut input).map_err(|e| io::Error::other(e.as_str()))? {
        let row: Value = serde_json::from_slice(&line).map_err(io::Error::other)?;
        for event in replay.apply(&row)? { writeln!(stdout, "{event}")?; }
    }
    stdout.flush()
}
