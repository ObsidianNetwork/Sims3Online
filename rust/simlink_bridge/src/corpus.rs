//! The differential corpora (card 56 §E.5, §G gates 4-5, §H). Both the Rust tests and the C# oracle
//! (`rust/simlink_bridge/oracle/Program.cs`) read these same files, so both sides encode the same record and
//! decode the same line.
//!
//! `fixtures/verb_corpus.txt`: one record per line, `Record<TAB>corr<TAB>arg...`. A token `~` is null; `\\`,
//! `\t`, `\n`, `\r` are escapes. A list of ids is one token, comma separated (empty for none); a vector is
//! one token `x,y,z`; a list of strings or pairs is a count token followed by its items. The argument order
//! per record is the record's field order (see [`parse_command`]).
//!
//! `fixtures/reply_corpus.txt`: `TAG<TAB>line`, where TAG is the reply family the line belongs to.
#![forbid(unsafe_code)]

use crate::text::{decode_hex_bytes, Guid};
use crate::verbs::{
    AuthorityGateMode, BridgeCommand, GameSpeed, HostRole, InteractionRequest, SimDescriptionPayload,
    SimDetailsSample, SimStateSample, Vec3,
};

/// Undo the corpus escapes.
pub fn unescape(token: &str) -> String {
    let mut out = String::with_capacity(token.len());
    let mut chars = token.chars();
    while let Some(c) = chars.next() {
        if c != '\\' {
            out.push(c);
            continue;
        }
        match chars.next() {
            Some('t') => out.push('\t'),
            Some('n') => out.push('\n'),
            Some('r') => out.push('\r'),
            Some(other) => out.push(other),
            None => out.push('\\'),
        }
    }
    out
}

/// The non-comment, non-blank lines of a corpus file, split on tabs and unescaped.
pub fn rows(text: &str) -> Vec<Vec<String>> {
    text.lines()
        .filter(|l| !l.trim().is_empty() && !l.starts_with('#'))
        .map(|l| l.split('\t').map(unescape).collect())
        .collect()
}

struct Tokens<'a> {
    items: &'a [String],
    at: usize,
}

type R<T> = Result<T, String>;

impl<'a> Tokens<'a> {
    fn next(&mut self) -> R<&'a str> {
        let t = self.items.get(self.at).ok_or_else(|| format!("missing token {}", self.at))?;
        self.at += 1;
        Ok(t.as_str())
    }
    fn string(&mut self) -> R<String> {
        Ok(self.next()?.to_string())
    }
    fn opt_string(&mut self) -> R<Option<String>> {
        let t = self.next()?;
        Ok(if t == "~" { None } else { Some(t.to_string()) })
    }
    fn u64(&mut self) -> R<u64> {
        self.next()?.parse().map_err(|e| format!("u64: {e}"))
    }
    fn opt_u64(&mut self) -> R<Option<u64>> {
        let t = self.next()?;
        if t == "~" {
            Ok(None)
        } else {
            t.parse().map(Some).map_err(|e| format!("u64: {e}"))
        }
    }
    fn i64(&mut self) -> R<i64> {
        self.next()?.parse().map_err(|e| format!("i64: {e}"))
    }
    fn opt_i64(&mut self) -> R<Option<i64>> {
        let t = self.next()?;
        if t == "~" {
            Ok(None)
        } else {
            t.parse().map(Some).map_err(|e| format!("i64: {e}"))
        }
    }
    fn i32(&mut self) -> R<i32> {
        self.next()?.parse().map_err(|e| format!("i32: {e}"))
    }
    fn u32(&mut self) -> R<u32> {
        self.next()?.parse().map_err(|e| format!("u32: {e}"))
    }
    fn count(&mut self) -> R<usize> {
        self.next()?.parse().map_err(|e| format!("count: {e}"))
    }
    fn bool(&mut self) -> R<bool> {
        match self.next()? {
            "1" => Ok(true),
            "0" => Ok(false),
            t => Err(format!("bool: {t}")),
        }
    }
    fn f32(&mut self) -> R<f32> {
        self.next()?.parse().map_err(|e| format!("f32: {e}"))
    }
    fn vec3(&mut self) -> R<Vec3> {
        parse_vec3(self.next()?)
    }
    fn opt_vec3(&mut self) -> R<Option<Vec3>> {
        let t = self.next()?;
        if t == "~" {
            Ok(None)
        } else {
            parse_vec3(t).map(Some)
        }
    }
    fn ids(&mut self) -> R<Vec<u64>> {
        let t = self.next()?;
        if t.is_empty() {
            return Ok(vec![]);
        }
        t.split(',').map(|v| v.parse().map_err(|e| format!("id: {e}"))).collect()
    }
    fn floats(&mut self) -> R<Vec<f32>> {
        let t = self.next()?;
        if t.is_empty() {
            return Ok(vec![]);
        }
        t.split(',').map(|v| v.parse().map_err(|e| format!("f32: {e}"))).collect()
    }
    fn strings(&mut self) -> R<Vec<String>> {
        let n = self.count()?;
        (0..n).map(|_| self.string()).collect()
    }
    fn dict(&mut self) -> R<Vec<(String, String)>> {
        let n = self.count()?;
        (0..n).map(|_| Ok((self.string()?, self.string()?))).collect()
    }
    fn opt_dict(&mut self) -> R<Option<Vec<(String, String)>>> {
        if self.items.get(self.at).map(String::as_str) == Some("~") {
            self.at += 1;
            return Ok(None);
        }
        self.dict().map(Some)
    }
    fn positions(&mut self) -> R<Vec<(u64, Vec3)>> {
        let n = self.count()?;
        (0..n).map(|_| Ok((self.u64()?, self.vec3()?))).collect()
    }
    fn hex_bytes(&mut self) -> R<Vec<u8>> {
        decode_hex_bytes(self.next()?).map_err(|e| e.to_string())
    }
    fn interaction(&mut self) -> R<InteractionRequest> {
        Ok(InteractionRequest {
            actor: self.u64()?,
            key: self.string()?,
            target: self.opt_u64()?,
            position: self.opt_vec3()?,
            parameters: self.dict()?,
        })
    }
}

fn parse_vec3(t: &str) -> R<Vec3> {
    let v: Vec<&str> = t.split(',').collect();
    if v.len() != 3 {
        return Err(format!("vec3: {t}"));
    }
    let f = |s: &str| s.parse::<f32>().map_err(|e| format!("f32: {e}"));
    Ok(Vec3 { x: f(v[0])?, y: f(v[1])?, z: f(v[2])? })
}

/// One corpus row to a command. `row[0]` is the record name, `row[1]` the correlation id.
pub fn parse_command(row: &[String]) -> Result<BridgeCommand, String> {
    use BridgeCommand as C;
    let record = row.first().ok_or("empty row")?.as_str();
    let mut t = Tokens { items: row, at: 1 };
    let corr = Guid::parse(t.next()?).map_err(|e| e.to_string())?;
    let cmd = match record {
        "LoadSave" => C::LoadSave { corr, save_name: t.string()? },
        "LoadSaveViaMenu" => C::LoadSaveViaMenu { corr, save_name: t.string()? },
        "SaveGame" => C::SaveGame { corr, save_name: t.string()? },
        "SetGameSpeed" => {
            let name = t.next()?;
            C::SetGameSpeed { corr, speed: GameSpeed::from_name(name).ok_or_else(|| format!("speed {name}"))? }
        }
        "SetWorldTime" => C::SetWorldTime { corr, day: t.i32()?, hours: t.i32()?, minutes: t.i32()? },
        "RequestSnapshot" => C::RequestSnapshot { corr, since_tick: t.opt_i64()? },
        "ProbeSimLifecycle" => C::ProbeSimLifecycle { corr },
        "ProbeActorLookup" => C::ProbeActorLookup { corr },
        "ProbeStateInjection" => C::ProbeStateInjection { corr, sim: t.u64()?, position: t.vec3()? },
        "QueueInteraction" => C::QueueInteraction { corr, interaction: t.interaction()? },
        "SetActiveSim" => C::SetActiveSim { corr, sim: t.u64()? },
        "CancelInteraction" => C::CancelInteraction { corr, actor: t.u64()?, interaction_id: t.u64()? },
        "LeaveConversation" => C::LeaveConversation { corr, actor: t.u64()? },
        "AnswerDialog" => C::AnswerDialog { corr, request_id: t.i32()?, answer: t.opt_string()? },
        "ShowDialog" => {
            C::ShowDialog { corr, request_id: t.i32()?, sim: t.u64()?, kind: t.string()?, args: t.strings()? }
        }
        "DisableAutonomy" => C::DisableAutonomy { corr, sim: t.u64()?, cancel_running: t.bool()? },
        "EnableAutonomy" => C::EnableAutonomy { corr, sim: t.u64()?, reset: t.bool()? },
        "GateAutonomy" => C::GateAutonomy { corr, sim: t.u64()? },
        "UngateAutonomy" => C::UngateAutonomy { corr, sim: t.u64()?, reset: t.bool()? },
        "SetSimScope" => C::SetSimScope { corr, lots: t.ids()?, sims: t.ids()? },
        "SetAuthority" => {
            let mode = match t.next()? {
                "Off" => AuthorityGateMode::Off,
                "Observe" => AuthorityGateMode::Observe,
                "Enforce" => AuthorityGateMode::Enforce,
                m => return Err(format!("mode {m}")),
            };
            C::SetAuthority { corr, mode, owned: t.ids()? }
        }
        "ProbeSimState" => C::ProbeSimState { corr, sims: t.ids()? },
        "ApplySimState" => {
            let sim = t.u64()?;
            let motives = t.floats()?;
            let n = t.count()?;
            let buffs = (0..n).map(|_| Ok((t.u64()?, t.u64()?))).collect::<R<Vec<_>>>()?;
            C::ApplySimState { corr, state: SimStateSample { sim, motives, buffs } }
        }
        "SetSimAliases" => {
            let n = t.count()?;
            let aliases = (0..n).map(|_| Ok((t.u64()?, t.u64()?))).collect::<R<Vec<_>>>()?;
            C::SetSimAliases { corr, aliases, reset: t.bool()? }
        }
        "ProbeSimPresence" => C::ProbeSimPresence { corr, since_seq: t.i64()? },
        "SpawnSim" => C::SpawnSim { corr, sim: t.u64()?, desc: t.u64()?, position: t.vec3()? },
        "DespawnSim" => C::DespawnSim { corr, sim: t.u64()?, desc: t.u64()? },
        "ExportSimDescription" => C::ExportSimDescription { corr, desc: t.u64()? },
        "ProbeSimDetails" => C::ProbeSimDetails { corr, sims: t.ids()? },
        "ApplySimDetails" => C::ApplySimDetails {
            corr,
            details: SimDetailsSample {
                sim: t.u64()?,
                career_guid: t.u64()?,
                career_level: t.i32()?,
                career_branch: t.string()?,
                funds: t.i32()?,
                skills: t.string()?,
            },
        },
        "ImportSimDescription" => C::ImportSimDescription {
            corr,
            payload: SimDescriptionPayload {
                desc: t.u64()?,
                property_stream: t.hex_bytes()?,
                res_key_table: t.hex_bytes()?,
                object_id_table: t.hex_bytes()?,
            },
        },
        "SacsRecorder" => C::SacsRecorder { corr, args: t.strings()? },
        "SacsReplay" => C::SacsReplay { corr, lines: t.strings()? },
        "DiagState" => C::DiagState { corr },
        "DiagCaptureInteraction" => C::DiagCaptureInteraction { corr, sim: t.u64()? },
        "QueryInteractionState" => C::QueryInteractionState { corr, sim: t.u64()?, watch_book: t.opt_u64()? },
        "InjectReadIntent" => C::InjectReadIntent {
            corr,
            sim: t.u64()?,
            book: t.u64()?,
            mode: t.string()?,
            parameters: t.opt_dict()?,
        },
        "Ping" => C::Ping { corr },
        "Disconnect" => C::Disconnect { corr },
        "Raw" => C::Raw { corr, name: t.string()?, args: t.strings()? },
        "SetRole" => C::SetRole {
            corr,
            role: match t.next()? {
                "Server" => HostRole::Server,
                "Client" => HostRole::Client,
                r => return Err(format!("role {r}")),
            },
        },
        "ApplyPosition" => C::ApplyPosition { corr, sim: t.u64()?, position: t.vec3()? },
        "ApplyPositions" => C::ApplyPositions { corr, positions: t.positions()? },
        "ApplyMotive" => C::ApplyMotive { corr, sim: t.u64()?, motive: t.string()?, value: t.f32()? },
        "ProbeSimMotive" => C::ProbeSimMotive { corr, sim: t.u64()?, motive: t.string()? },
        "ApplyInteractionQueue" => C::ApplyInteractionQueue { corr },
        "ApplyInteractions" => C::ApplyInteractions { corr },
        "ApplyForceAlign" => C::ApplyForceAlign { corr },
        "ProbeSimInteractionQueue" => C::ProbeSimInteractionQueue { corr, sim: t.u64()? },
        "ApplyCash" => C::ApplyCash { corr, household: t.u64()?, funds: t.i32()? },
        "ProbeHouseholdCash" => C::ProbeHouseholdCash { corr, household: t.u64()? },
        "ApplySkill" => C::ApplySkill { corr, sim: t.u64()?, skill: t.string()?, level: t.i32()? },
        "ProbeSimSkill" => C::ProbeSimSkill { corr, sim: t.u64()?, skill: t.string()? },
        "ApplyAutonomy" => C::ApplyAutonomy { corr, sim: t.u64()?, disabled: t.bool()? },
        "ProbeSimAutonomy" => C::ProbeSimAutonomy { corr, sim: t.u64()? },
        "MergeLotState" => C::MergeLotState { corr, lot: t.u64()?, sims: t.positions()? },
        "ImportHouseholdContent" => C::ImportHouseholdContent {
            corr,
            household: t.u64()?,
            total_bytes: t.i32()?,
            property_stream_bytes: t.i32()?,
            res_key_table_bytes: t.i32()?,
            object_id_table_bytes: t.i32()?,
            crc32: t.u32()?,
            entry_count: t.i32()?,
            res_key_count: t.i32()?,
            object_id_count: t.i32()?,
        },
        "ExportHouseholdContent" => C::ExportHouseholdContent { corr, household: t.u64()? },
        "MergeTravellingHousehold" => C::MergeTravellingHousehold {
            corr,
            household: t.u64()?,
            total_bytes: t.i32()?,
            crc32: t.u32()?,
            household_prop_bytes: t.i32()?,
            household_res_key_bytes: t.i32()?,
            household_obj_id_bytes: t.i32()?,
            inventory_count: t.i32()?,
        },
        "ShowNotification" => C::ShowNotification { corr, text: t.string()? },
        other => return Err(format!("unknown record {other}")),
    };
    if t.at != row.len() {
        return Err(format!("{record}: {} unread tokens", row.len() - t.at));
    }
    Ok(cmd)
}

/// Escape a field for a corpus or oracle output line (the inverse of [`unescape`]).
pub fn escape(s: &str) -> String {
    crate::reply::esc(s)
}

/// A JSON string literal.
pub fn json_str(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 2);
    out.push('"');
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04x}", c as u32)),
            c => out.push(c),
        }
    }
    out.push('"');
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rows_skip_comments_and_unescape() {
        let r = rows("# c\n\nA\tb\\tc\t~\n");
        assert_eq!(r, vec![vec!["A".to_string(), "b\tc".to_string(), "~".to_string()]]);
        assert_eq!(unescape(&escape("a\\b\tc\nd")), "a\\b\tc\nd");
    }

    #[test]
    fn parse_command_reads_nested_shapes() {
        let row: Vec<String> = ["QueueInteraction", "0123456789abcdef0123456789abcdef", "5", "Terrain.GoHere", "~", "1,2,3", "1", "ui.name", "Go"]
            .iter()
            .map(|s| s.to_string())
            .collect();
        let cmd = parse_command(&row).unwrap();
        assert_eq!(cmd.record(), "QueueInteraction");
        let mut short = row.clone();
        short.pop();
        assert!(parse_command(&short).is_err());
        assert_eq!(json_str("a\"b\u{1}"), "\"a\\\"b\\u0001\"");
    }
}
