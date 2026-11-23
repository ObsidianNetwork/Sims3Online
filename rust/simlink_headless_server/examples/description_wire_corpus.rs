#![forbid(unsafe_code)]
//! Synthetic first-party protocol corpus for the real C# EventReceiver/PublicWireConnection.
//! No native game, proprietary input, production flag or network listener is used.
use simlink_headless_server::{messages as m, serve};
use std::io::Write;

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let cases = [
        ("raw_non_utf8", u64::MAX - 1, [vec![0, 127, 128, 255], vec![], vec![0, 255]]),
        ("empty_tables", 200, [vec![0], vec![], vec![]]),
        ("max_payload", 201, [(0..524_288).map(|i| [0, 127, 128, 255][i % 4]).collect(), vec![], vec![]]),
        ("looks_like_bulk_id", 202, [vec![0xb1, 0x23], vec![], vec![]]),
    ];
    let mut result = Vec::new();
    for (name, desc, arrays) in cases {
        let description = m::Event { seq: 1, tick: 2, corr: 0, kind: "SimDescriptionData".into(),
            fields: std::iter::once(desc.to_string()).chain(arrays.iter().map(|bytes| m::hex_encode_bytes(bytes))).collect() };
        let spawn = m::Event { seq: 2, tick: 2, corr: 0, kind: "SimSpawned".into(), fields: vec![
            u64::MAX.to_string(), desc.to_string(), m::enc_record(&["1.25".into(), "-2.5".into(), "3.75".into()])] };
        let mut next = 1;
        let mut lines = serve::encode_event_lines(&description, &mut next)?;
        lines.extend(serve::encode_event_lines(&spawn, &mut next)?);
        result.push(serde_json::json!({"name":name,"lines":lines}));
    }
    let bytes = serde_json::to_vec(&serde_json::json!({"cases":result}))?;
    if bytes.len() > 8 * 1024 * 1024 { return Err("corpus exceeds bound".into()); }
    std::io::stdout().write_all(&bytes)?;
    Ok(())
}
