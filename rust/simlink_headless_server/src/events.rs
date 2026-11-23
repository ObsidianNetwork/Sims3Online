#![forbid(unsafe_code)]
//! Decode bridge event envelopes once. Recorder presentation lines remain opaque.

use crate::{messages as m, DriverError};

pub fn decode_bridge_event(line: &str, tick: u64) -> Result<m::Event, DriverError> {
    let fields: Vec<_> = line.trim_start_matches('\u{feff}').split('|').collect();
    let bad = || DriverError(format!("invalid_bridge_event|{line}"));
    if fields.first() != Some(&"EVENT") { return Err(bad()); }
    let field = |i| fields.get(i).copied().ok_or_else(bad);
    let hex = |i| simlink_bridge::text::decode_text(field(i)?).map(|s| m::hex_encode(&s)).map_err(|_| bad());
    let id = |i| field(i)?.parse::<u64>().map(|v| v.to_string()).map_err(|_| bad());
    let position = |at| -> Result<String, DriverError> {
        Ok(m::enc_record(&(at..at + 3).map(|i| field(i)?.parse::<f32>().map(|v| v.to_string()).map_err(|_| bad())).collect::<Result<Vec<_>, _>>()?))
    };
    let (kind, args) = match field(1)? {
        "InteractionStarted" | "InteractionCompleted" => (field(1)?, vec![id(2)?, id(3)?]),
        "InteractionFailed" => ("InteractionFailed", vec![id(2)?, id(3)?, hex(4)?]),
        "InteractionQueued" => ("InteractionQueued", vec![id(2)?, id(3)?, hex(4)?,
            if field(5)?.is_empty() { "~".into() } else { id(5)? },
            if field(6)? == "1" { position(7)? } else { "~".into() },
            // Optional parameter tail is the bridge's hex-encoded k=v list.
            if let Some(tail) = fields.get(10).filter(|s| !s.is_empty() && **s != "-") {
                let decoded = simlink_bridge::text::decode_text(tail).map_err(|_| bad())?;
                m::enc_list(&decoded.split(';').filter_map(|pair| pair.split_once('='))
                    .map(|(k, v)| m::enc_record(&[m::hex_encode(k), m::hex_encode(v)])).collect::<Vec<_>>())
            } else { "~".into() }]),
        "DialogRequest" => {
            let request = field(2)?.parse::<i32>().map_err(|_| bad())?.to_string();
            let count = field(5)?.parse::<usize>().map_err(|_| bad())?;
            if count > m::PUBLIC_LINE_MAX_BYTES { return Err(bad()); }
            let args = (0..count).map(|i| {
                fields.get(6 + i).map_or(Ok(String::new()), |_| hex(6 + i))
            }).collect::<Result<Vec<_>, _>>()?;
            ("DialogRequested", vec![request, id(3)?, hex(4)?, m::enc_list(&args)])
        }
        "PickFailed" => {
            let text = simlink_bridge::text::decode_text(field(5)?).map_err(|_| bad())?;
            let (code, mut tooltip) = crate::wire_v1::game_refusal(&text);
            if let Some((_, suffix)) = text.split_once(" | tooltip: ") { tooltip = m::hex_encode(suffix); }
            ("PickFailed", vec![code.as_str().into(), tooltip])
        }
        "SpeedIntentCaptured" => ("GameSpeedChanged", vec![simlink_bridge::verbs::GameSpeed::from_wire(field(2)?).ok_or_else(bad)?.name().into()]),
        "ServerError" => ("ServerError", vec![hex(2)?]),
        "DialogAnswer" => ("DialogAnswerCaptured", vec![field(2)?.parse::<i32>().map_err(|_| bad())?.to_string(), id(3)?,
            if field(4)? == "-" { "~".into() } else { hex(4)? }]),
        "CancelIntent" => ("CancelIntentCaptured", vec![id(2)?, id(3)?]),
        "LeaveConversationIntent" => ("LeaveConversationIntentCaptured", vec![id(2)?]),
        "IntentCaptured" => ("IntentCaptured", vec![id(2)?, id(3)?, hex(4)?, if field(5)? == "1" { position(6)? } else { "~".into() }]),
        _ => return Err(bad()),
    };
    Ok(m::Event { seq: 0, tick, corr: 0, kind: kind.into(), fields: args })
}
