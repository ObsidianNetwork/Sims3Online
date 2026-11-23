#![forbid(unsafe_code)]
//! Loopback endpoint and bridgectl token grammar. Socket lifetime belongs to serve.

use std::net::SocketAddr;

#[path = "analytics.rs"]
pub mod analytics;

pub const DEFAULT_ENDPOINT: &str = "127.0.0.1:34029";
pub const DEFAULT_TIMEOUT_SECONDS: u64 = 15;
pub const MAX_TIMEOUT_SECONDS: u64 = 600;
pub const ANALYTICS_COMMAND: &str = "ANALYTICS";
pub const FRONT_DOOR_LOCAL_COMMANDS: [&str; 3] = [ANALYTICS_COMMAND, "SYNC_POLICY_STATE", "RUST_ROUTE_PLANS"];

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Request {
    pub timeout_seconds: u64,
    pub name: String,
    pub args: Vec<String>,
}
pub fn resolve_endpoint(endpoint: &str) -> Result<SocketAddr, String> {
    let addr: SocketAddr = endpoint.parse().map_err(|_| "--admin must be address:port")?;
    if !addr.ip().is_loopback() { return Err("--admin must bind a loopback address (no auth exists pre-P6)".into()); }
    Ok(addr)
}
pub fn parse_request(line: &str) -> Result<Request, String> {
    let tokens: Vec<_> = line.trim().split(' ').filter(|s| !s.is_empty()).collect();
    let Some(first) = tokens.first() else { return Err("empty request".into()); };
    let mut start = 0;
    let timeout_seconds = if let Some(seconds) = first.strip_prefix('@') {
        start = 1;
        seconds.parse::<u64>().ok().filter(|s| (1..=MAX_TIMEOUT_SECONDS).contains(s))
            .ok_or_else(|| format!("invalid timeout '{first}' (use @1..@{MAX_TIMEOUT_SECONDS})"))?
    } else { DEFAULT_TIMEOUT_SECONDS };
    let mut name = tokens.get(start).ok_or("missing command name")?.to_uppercase();
    if name == "GET" && tokens.get(start + 1) == Some(&"/analytics") {
        let tail = &tokens[start + 2..];
        if !tail.is_empty() && tail != ["HTTP/1.0"] && tail != ["HTTP/1.1"] {
            return Err("invalid GET /analytics request".into());
        }
        return Ok(Request { timeout_seconds, name: ANALYTICS_COMMAND.into(), args: Vec::new() });
    }
    if name == "/ANALYTICS" { name = ANALYTICS_COMMAND.into(); }
    let args = tokens[start + 1..].iter().map(|token| match *token {
        "-" => String::new(), "\\-" => "-".into(), token => token.into(),
    }).collect();
    Ok(Request { timeout_seconds, name, args })
}

pub fn report(report: &str) -> String {
    format!("{{\"ok\":true,\"accepted\":true,\"report\":{}}}", json_string(report))
}
pub fn is_local_analytics(request: &Request) -> bool { request.name == ANALYTICS_COMMAND }
pub fn analytics_report(observations: &[analytics::Observation<'_>]) -> String {
    report(&analytics::render(observations))
}
/// Local dispatch seam for the loopback listener; never forward these to the mod.
pub fn local_reply(request: &Request) -> Option<String> {
    is_local_analytics(request).then(|| {
        if request.args.is_empty() { analytics_report(&[]) } else { error("ANALYTICS takes no arguments") }
    })
}
pub fn commands_reply() -> String {
    let entries = include_str!("admin_commands.txt").lines()
        .chain(["SYNC_POLICY_STATE (front-door-local, read-only; validated script revision and actual packet effects)"])
        .chain(["RUST_ROUTE_PLANS [<corr> [<cursor> [<max_lines> [<epoch>]]]]  (front-door-local; last 16 committed Rt/Vp plans, or one <=1900-byte page of one; epoch required after cursor 0; memory only)"])
        .map(json_string).collect::<Vec<_>>();
    format!("{{\"ok\":true,\"commands\":[{}]}}", entries.join(","))
}
pub fn error(error: impl std::fmt::Display) -> String {
    format!("{{\"ok\":false,\"error\":{}}}", json_string(&error.to_string()))
}
/// C# BridgeAdminListener's JSON envelope; the report text is never reformatted.
pub fn bridge_reply(lines: &[String]) -> Result<String, crate::DriverError> {
    bridge_response(lines, true)
}
pub fn upload_reply(lines: &[String]) -> Result<String, crate::DriverError> {
    bridge_response(lines, false)
}
fn bridge_response(lines: &[String], include_snapshot: bool) -> Result<String, crate::DriverError> {
    let line = lines.last().ok_or_else(|| crate::DriverError("missing bridge reply".into()))?;
    let response = simlink_bridge::reply::decode_response(line).map_err(|e| crate::DriverError(format!("{e:?}")))?
        .ok_or_else(|| crate::DriverError("unrecognized bridge reply".into()))?;
    let optional = |text: Option<&str>| text.map_or_else(|| "null".into(), json_string);
    let snapshot = response.snapshot.map(|snapshot| format!("tick={} sims={} objects={}", snapshot.tick, snapshot.sims.len(), snapshot.objects.len()));
    let tail = if include_snapshot { format!(",\"snapshot\":{}", optional(snapshot.as_deref())) } else { String::new() };
    Ok(format!("{{\"ok\":true,\"accepted\":{},\"reason\":{},\"report\":{}{tail}}}",
        response.accepted, optional(response.rejection.as_deref()), optional(response.probe_report.as_deref())))
}

/// System.Text.Json's default JavaScriptEncoder escaping, including HTML-sensitive
/// ASCII and non-ASCII UTF-16 code units. Field order is part of bridgectl parity.
pub fn json_string(text: &str) -> String {
    use std::fmt::Write;
    let mut out = String::from("\"");
    for ch in text.chars() {
        match ch {
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            '\u{8}' => out.push_str("\\b"),
            '\u{c}' => out.push_str("\\f"),
            ch if !(' '..='\u{7e}').contains(&ch) || matches!(ch, '"' | '\'' | '<' | '>' | '&' | '+' | '`') => {
                let mut units = [0; 2];
                for unit in ch.encode_utf16(&mut units) { write!(out, "\\u{unit:04X}").expect("string write"); }
            }
            ch => out.push(ch),
        }
    }
    out.push('"');
    out
}
