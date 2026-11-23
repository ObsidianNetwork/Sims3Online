//! The mod verb set in Rust (card 56 §E). One [`BridgeCommand`] variant per `public sealed record` of
//! `DedicatedServer/HostBridge/BridgeCommand.cs` (60 at 9c83c621), the hand-written [`VERB_TABLE`] (wire verb,
//! argument encoding, reply shape) and [`encode`], which matches `BridgeLineCodec.EncodeCommand` byte for byte
//! (gate 4) except for the three §E.3 verbs, which it refuses by name.
#![forbid(unsafe_code)]

use crate::error::BridgeError;
use crate::text::{encode_text, format_f2, format_r, Guid};

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Vec3 {
    pub x: f32,
    pub y: f32,
    pub z: f32,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum GameSpeed {
    Pause,
    Normal,
    Speed2,
    Speed3,
    Ultra,
}

impl GameSpeed {
    /// `BridgeLineCodec.EncodeSpeed`.
    pub fn wire(self) -> &'static str {
        match self {
            Self::Pause => "Pause",
            Self::Normal => "Normal",
            Self::Speed2 => "Double",
            Self::Speed3 => "Triple",
            Self::Ultra => "Skip",
        }
    }

    /// `BridgeLineCodec.DecodeSpeed`.
    pub fn from_wire(s: &str) -> Option<Self> {
        Some(match s {
            "Pause" => Self::Pause,
            "Normal" => Self::Normal,
            "Double" | "Speed2" => Self::Speed2,
            "Triple" | "Speed3" => Self::Speed3,
            "Skip" | "Ultra" => Self::Ultra,
            _ => return None,
        })
    }

    /// The C# enum member name.
    pub fn name(self) -> &'static str {
        match self {
            Self::Pause => "Pause",
            Self::Normal => "Normal",
            Self::Speed2 => "Speed2",
            Self::Speed3 => "Speed3",
            Self::Ultra => "Ultra",
        }
    }

    pub fn from_name(s: &str) -> Option<Self> {
        [Self::Pause, Self::Normal, Self::Speed2, Self::Speed3, Self::Ultra].into_iter().find(|v| v.name() == s)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HostRole {
    Server,
    Client,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AuthorityGateMode {
    Off,
    Observe,
    Enforce,
}

/// `InteractionRequest`. `parameters` keeps insertion order; lookups are by key, as the C# dictionary's.
#[derive(Debug, Clone, PartialEq)]
pub struct InteractionRequest {
    pub actor: u64,
    pub key: String,
    pub target: Option<u64>,
    pub position: Option<Vec3>,
    pub parameters: Vec<(String, String)>,
}

/// `SimStateSample` (W2 part B).
#[derive(Debug, Clone, PartialEq)]
pub struct SimStateSample {
    pub sim: u64,
    pub motives: Vec<f32>,
    pub buffs: Vec<(u64, u64)>,
}

/// `SimDetailsSample`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SimDetailsSample {
    pub sim: u64,
    pub career_guid: u64,
    pub career_level: i32,
    pub career_branch: String,
    pub funds: i32,
    pub skills: String,
}

/// `SimDescriptionPayload`: three byte tables carried through a bulk slot.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SimDescriptionPayload {
    pub desc: u64,
    pub property_stream: Vec<u8>,
    pub res_key_table: Vec<u8>,
    pub object_id_table: Vec<u8>,
}

impl SimDescriptionPayload {
    pub fn combined(&self) -> Vec<u8> {
        [self.property_stream.as_slice(), self.res_key_table.as_slice(), self.object_id_table.as_slice()].concat()
    }
}

/// One variant per C# record. The three §E.3 verbs carry only their correlation id: the Rust driver never
/// encodes them, so their payloads are not modelled.
#[derive(Debug, Clone, PartialEq)]
pub enum BridgeCommand {
    LoadSave { corr: Guid, save_name: String },
    LoadSaveViaMenu { corr: Guid, save_name: String },
    SaveGame { corr: Guid, save_name: String },
    SetGameSpeed { corr: Guid, speed: GameSpeed },
    SetWorldTime { corr: Guid, day: i32, hours: i32, minutes: i32 },
    RequestSnapshot { corr: Guid, since_tick: Option<i64> },
    ProbeSimLifecycle { corr: Guid },
    ProbeActorLookup { corr: Guid },
    ProbeStateInjection { corr: Guid, sim: u64, position: Vec3 },
    QueueInteraction { corr: Guid, interaction: InteractionRequest },
    SetActiveSim { corr: Guid, sim: u64 },
    CancelInteraction { corr: Guid, actor: u64, interaction_id: u64 },
    LeaveConversation { corr: Guid, actor: u64 },
    AnswerDialog { corr: Guid, request_id: i32, answer: Option<String> },
    ShowDialog { corr: Guid, request_id: i32, sim: u64, kind: String, args: Vec<String> },
    DisableAutonomy { corr: Guid, sim: u64, cancel_running: bool },
    EnableAutonomy { corr: Guid, sim: u64, reset: bool },
    GateAutonomy { corr: Guid, sim: u64 },
    UngateAutonomy { corr: Guid, sim: u64, reset: bool },
    SetSimScope { corr: Guid, lots: Vec<u64>, sims: Vec<u64> },
    SetAuthority { corr: Guid, mode: AuthorityGateMode, owned: Vec<u64> },
    ProbeSimState { corr: Guid, sims: Vec<u64> },
    ApplySimState { corr: Guid, state: SimStateSample },
    SetSimAliases { corr: Guid, aliases: Vec<(u64, u64)>, reset: bool },
    ProbeSimPresence { corr: Guid, since_seq: i64 },
    SpawnSim { corr: Guid, sim: u64, desc: u64, position: Vec3 },
    DespawnSim { corr: Guid, sim: u64, desc: u64 },
    ExportSimDescription { corr: Guid, desc: u64 },
    ProbeSimDetails { corr: Guid, sims: Vec<u64> },
    ApplySimDetails { corr: Guid, details: SimDetailsSample },
    ImportSimDescription { corr: Guid, payload: SimDescriptionPayload },
    SacsRecorder { corr: Guid, args: Vec<String> },
    SacsReplay { corr: Guid, lines: Vec<String> },
    DiagState { corr: Guid },
    DiagCaptureInteraction { corr: Guid, sim: u64 },
    QueryInteractionState { corr: Guid, sim: u64, watch_book: Option<u64> },
    InjectReadIntent { corr: Guid, sim: u64, book: u64, mode: String, parameters: Option<Vec<(String, String)>> },
    Ping { corr: Guid },
    Disconnect { corr: Guid },
    Raw { corr: Guid, name: String, args: Vec<String> },
    SetRole { corr: Guid, role: HostRole },
    ApplyPosition { corr: Guid, sim: u64, position: Vec3 },
    ApplyPositions { corr: Guid, positions: Vec<(u64, Vec3)> },
    ApplyMotive { corr: Guid, sim: u64, motive: String, value: f32 },
    ProbeSimMotive { corr: Guid, sim: u64, motive: String },
    ApplyInteractionQueue { corr: Guid },
    ApplyInteractions { corr: Guid },
    ApplyForceAlign { corr: Guid },
    ProbeSimInteractionQueue { corr: Guid, sim: u64 },
    ApplyCash { corr: Guid, household: u64, funds: i32 },
    ProbeHouseholdCash { corr: Guid, household: u64 },
    ApplySkill { corr: Guid, sim: u64, skill: String, level: i32 },
    ProbeSimSkill { corr: Guid, sim: u64, skill: String },
    ApplyAutonomy { corr: Guid, sim: u64, disabled: bool },
    ProbeSimAutonomy { corr: Guid, sim: u64 },
    MergeLotState { corr: Guid, lot: u64, sims: Vec<(u64, Vec3)> },
    ImportHouseholdContent {
        corr: Guid,
        household: u64,
        total_bytes: i32,
        property_stream_bytes: i32,
        res_key_table_bytes: i32,
        object_id_table_bytes: i32,
        crc32: u32,
        entry_count: i32,
        res_key_count: i32,
        object_id_count: i32,
    },
    ExportHouseholdContent { corr: Guid, household: u64 },
    MergeTravellingHousehold {
        corr: Guid,
        household: u64,
        total_bytes: i32,
        crc32: u32,
        household_prop_bytes: i32,
        household_res_key_bytes: i32,
        household_obj_id_bytes: i32,
        inventory_count: i32,
    },
    ShowNotification { corr: Guid, text: String },
}

/// The reply a verb's mod handler sends (every verb may also answer `ERROR|corr|hex`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ReplyShape {
    /// `OK|corr`.
    Ok,
    /// `SESSION_READY|corr|hexSave|hexWorld|tick` once the load finishes.
    SessionReady,
    /// `SNAPSHOT|...`, as one line, `SNAPSHOT_CHUNK` lines, or `BULK_REF` metadata (text or `SNAPSHOT2`).
    Snapshot,
    /// `NAME|corr|kind|hexReport` with the given NAME.
    Report(&'static str),
    /// No reply (fire and forget).
    None,
    /// Whatever the named verb answers.
    Any,
}

impl ReplyShape {
    pub fn describe(self) -> String {
        match self {
            Self::Ok => "OK".to_string(),
            Self::SessionReady => "SESSION_READY".to_string(),
            Self::Snapshot => "SNAPSHOT|SNAPSHOT_CHUNK|BULK_REF".to_string(),
            Self::Report(name) => name.to_string(),
            Self::None => "none".to_string(),
            Self::Any => "any".to_string(),
        }
    }
}

/// One row of the hand-written verb table (§E.2).
#[derive(Debug, Clone, Copy)]
pub struct VerbRow {
    /// The C# record name in `BridgeCommand.cs`.
    pub record: &'static str,
    /// The wire verb (`NAME` of `NAME|corr|...`).
    pub wire: &'static str,
    /// The argument encoding after the correlation id.
    pub args: &'static str,
    pub reply: ReplyShape,
    /// Refused by name (§E.3); counted by the §E.2 count test.
    pub refused: bool,
}

const fn row(record: &'static str, wire: &'static str, args: &'static str, reply: ReplyShape) -> VerbRow {
    VerbRow { record, wire, args, reply, refused: false }
}

const fn refused(record: &'static str, wire: &'static str, args: &'static str) -> VerbRow {
    VerbRow { record, wire, args, reply: ReplyShape::None, refused: true }
}

use ReplyShape::{Any, None as NoReply, Ok as OkReply, Report, SessionReady, Snapshot};

/// The verb table, in `BridgeCommand.cs` declaration order (60 rows at 9c83c621, re-read at test time).
pub const VERB_TABLE: &[VerbRow] = &[
    row("LoadSave", "LOAD_SAVE", "hex saveName", SessionReady),
    row("LoadSaveViaMenu", "LOAD_SAVE_MENU", "hex saveName", SessionReady),
    row("SaveGame", "SAVE_GAME", "hex saveName", OkReply),
    row("SetGameSpeed", "SET_GAME_SPEED", "Pause|Normal|Double|Triple|Skip", OkReply),
    row("SetWorldTime", "SET_WORLD_TIME", "day|hours|minutes", OkReply),
    row("RequestSnapshot", "REQUEST_SNAPSHOT", "[sinceTick]", Snapshot),
    row("ProbeSimLifecycle", "PROBE_SIM_LIFECYCLE", "", Report("SIM_LIFECYCLE_PROBE")),
    row("ProbeActorLookup", "PROBE_ACTOR_LOOKUP", "", Report("ACTOR_LOOKUP_PROBE")),
    row("ProbeStateInjection", "PROBE_STATE_INJECTION", "simId|x|y|z (R)", Report("STATE_INJECTION_PROBE")),
    row("QueueInteraction", "QUEUE_INTERACTION", "actor|hexKey|target|hasPos|x|y|z[|hexName|hexPath|hit|picked]", OkReply),
    row("SetActiveSim", "SET_ACTIVE_SIM", "simId", OkReply),
    row("CancelInteraction", "CANCEL_INTERACTION", "actor|interactionId", OkReply),
    row("LeaveConversation", "LEAVE_CONVERSATION", "actor", OkReply),
    row("AnswerDialog", "DIALOG_ANSWER", "requestId|hexAnswer or -", OkReply),
    row("ShowDialog", "SHOW_DIALOG", "requestId|simId|hexKind|argCount|hexArg...", OkReply),
    row("DisableAutonomy", "DISABLE_AUTONOMY", "simId|cancelRunning 0|1", Report("AUTONOMY_PROBE")),
    row("EnableAutonomy", "ENABLE_AUTONOMY", "simId[|reset]", Report("AUTONOMY_PROBE")),
    row("GateAutonomy", "GATE_AUTONOMY", "simId", Report("AUTONOMY_PROBE")),
    row("UngateAutonomy", "UNGATE_AUTONOMY", "simId[|reset]", Report("AUTONOMY_PROBE")),
    row("SetSimScope", "SET_SIM_SCOPE", "lots|sims (SimScopeWire)", Report("AUTONOMY_PROBE")),
    row("SetAuthority", "SET_AUTHORITY", "off|observe|enforce|owned (SimScopeWire)", Report("AUTONOMY_PROBE")),
    row("ProbeSimState", "PROBE_SIM_STATE", "ids (SimScopeWire)", Report("AUTONOMY_PROBE")),
    row("ApplySimState", "APPLY_SIM_STATE", "SimStateWire entry", Report("AUTONOMY_PROBE")),
    row("SetSimAliases", "SET_SIM_ALIASES", "pairs (SimAliasWire)[|reset]", Report("AUTONOMY_PROBE")),
    row("ProbeSimPresence", "PROBE_SIM_PRESENCE", "sinceSeq", Report("AUTONOMY_PROBE")),
    row("SpawnSim", "SPAWN_SIM", "serverObjectId|simDescriptionId|x|y|z (R)", Report("AUTONOMY_PROBE")),
    row("DespawnSim", "DESPAWN_SIM", "serverObjectId|simDescriptionId", Report("AUTONOMY_PROBE")),
    row("ExportSimDescription", "EXPORT_SIM_DESCRIPTION", "simDescriptionId", Report("AUTONOMY_PROBE")),
    row("ProbeSimDetails", "PROBE_SIM_DETAILS", "ids (SimScopeWire)", Report("AUTONOMY_PROBE")),
    row("ApplySimDetails", "APPLY_SIM_DETAILS", "SimDetailsWire entry", Report("AUTONOMY_PROBE")),
    row("ImportSimDescription", "IMPORT_SIM_DESCRIPTION", "descId|total|props|keys|objects|crcX8", Report("AUTONOMY_PROBE")),
    row("SacsRecorder", "SACS_RECORDER", "args... (verbatim)", Report("AUTONOMY_PROBE")),
    row("SacsReplay", "SACS_REPLAY", "hex(lines joined by LF)", Report("AUTONOMY_PROBE")),
    row("DiagState", "DIAG_STATE", "", Report("DIAG_STATE_REPORT")),
    row("DiagCaptureInteraction", "DIAG_CAPTURE_INTERACTION", "simId", Report("DIAG_INTERACTION_BUNDLE_REPORT")),
    row("QueryInteractionState", "DIAG_INTERACTION_STATE", "simId[|hexBookId]", Report("DIAG_INTERACTION_STATE_REPORT")),
    row("InjectReadIntent", "INJECT_READ_INTENT", "simId|bookId|hexMode[|hexParameters]", OkReply),
    row("Ping", "PING", "", OkReply),
    row("Disconnect", "DISCONNECT", "", NoReply),
    row("Raw", "<name>", "args... (verbatim)", Any),
    row("SetRole", "SET_ROLE", "server|client", OkReply),
    row("ApplyPosition", "APPLY_POSITION", "simId|x|y|z (R)", OkReply),
    row("ApplyPositions", "APPLY_POSITIONS", "simId:x:y:z;...", OkReply),
    row("ApplyMotive", "APPLY_MOTIVE", "simId|hexMotive|value (R)", OkReply),
    row("ProbeSimMotive", "PROBE_SIM_MOTIVE", "simId|hexMotive", Report("SIM_MOTIVE_PROBE")),
    refused("ApplyInteractionQueue", "APPLY_INTERACTION_QUEUE", "refused (§E.3)"),
    refused("ApplyInteractions", "APPLY_INTERACTIONS", "refused (§E.3)"),
    refused("ApplyForceAlign", "APPLY_FORCE_ALIGN", "refused (§E.3)"),
    row("ProbeSimInteractionQueue", "PROBE_SIM_INTERACTION_QUEUE", "simId", Report("SIM_INTERACTION_QUEUE_PROBE")),
    row("ApplyCash", "APPLY_CASH", "householdId|funds", OkReply),
    row("ProbeHouseholdCash", "PROBE_HOUSEHOLD_CASH", "householdId", Report("HOUSEHOLD_CASH_PROBE")),
    row("ApplySkill", "APPLY_SKILL", "simId|hexSkill|level", OkReply),
    row("ProbeSimSkill", "PROBE_SIM_SKILL", "simId|hexSkill", Report("SIM_SKILL_PROBE")),
    row("ApplyAutonomy", "APPLY_AUTONOMY", "simId|disabled 0|1", OkReply),
    row("ProbeSimAutonomy", "PROBE_SIM_AUTONOMY", "simId", Report("SIM_AUTONOMY_PROBE")),
    row("MergeLotState", "MERGE_LOT_STATE", "lotId|simId:x:y:z;...", OkReply),
    row("ImportHouseholdContent", "IMPORT_HOUSEHOLD_CONTENT", "hh|total|props|keys|objects|crcX8|entries|keyCount|objCount", Report("DIAG_BLOB_REPORT")),
    row("ExportHouseholdContent", "EXPORT_HOUSEHOLD_CONTENT", "householdId", Report("DIAG_BLOB_REPORT")),
    row("MergeTravellingHousehold", "MERGE_TRAVELLING_HOUSEHOLD", "hh|total|crcX8|props|keys|objects|inventoryCount", Report("DIAG_BLOB_REPORT")),
    row("ShowNotification", "NOTIFY", "hexText", OkReply),
];

/// The §E.3 refusals (`RefusedLegacyVerb` rows): three.
pub fn refused_rows() -> impl Iterator<Item = &'static VerbRow> {
    VERB_TABLE.iter().filter(|r| r.refused)
}

/// The §E.3 verb a wire line names, if it is one: the mod dispatches on the first `|` field by exact key
/// (`SimsOnlineServerHostMod/BridgeCodec.cs` `DecodeCommand` `parts[0]`, `ServerHostMain.cs:502`), so the
/// same field decides here. Used by [`encode`] (a `Raw` line) and by the driver's one write path.
pub fn refused_line_verb(line: &str) -> Option<&'static str> {
    let name = line.split('|').next().unwrap_or("");
    refused_rows().map(|r| r.wire).find(|w| *w == name)
}

impl BridgeCommand {
    /// The C# record name of this variant (the [`VERB_TABLE`] key).
    pub fn record(&self) -> &'static str {
        match self {
            Self::LoadSave { .. } => "LoadSave",
            Self::LoadSaveViaMenu { .. } => "LoadSaveViaMenu",
            Self::SaveGame { .. } => "SaveGame",
            Self::SetGameSpeed { .. } => "SetGameSpeed",
            Self::SetWorldTime { .. } => "SetWorldTime",
            Self::RequestSnapshot { .. } => "RequestSnapshot",
            Self::ProbeSimLifecycle { .. } => "ProbeSimLifecycle",
            Self::ProbeActorLookup { .. } => "ProbeActorLookup",
            Self::ProbeStateInjection { .. } => "ProbeStateInjection",
            Self::QueueInteraction { .. } => "QueueInteraction",
            Self::SetActiveSim { .. } => "SetActiveSim",
            Self::CancelInteraction { .. } => "CancelInteraction",
            Self::LeaveConversation { .. } => "LeaveConversation",
            Self::AnswerDialog { .. } => "AnswerDialog",
            Self::ShowDialog { .. } => "ShowDialog",
            Self::DisableAutonomy { .. } => "DisableAutonomy",
            Self::EnableAutonomy { .. } => "EnableAutonomy",
            Self::GateAutonomy { .. } => "GateAutonomy",
            Self::UngateAutonomy { .. } => "UngateAutonomy",
            Self::SetSimScope { .. } => "SetSimScope",
            Self::SetAuthority { .. } => "SetAuthority",
            Self::ProbeSimState { .. } => "ProbeSimState",
            Self::ApplySimState { .. } => "ApplySimState",
            Self::SetSimAliases { .. } => "SetSimAliases",
            Self::ProbeSimPresence { .. } => "ProbeSimPresence",
            Self::SpawnSim { .. } => "SpawnSim",
            Self::DespawnSim { .. } => "DespawnSim",
            Self::ExportSimDescription { .. } => "ExportSimDescription",
            Self::ProbeSimDetails { .. } => "ProbeSimDetails",
            Self::ApplySimDetails { .. } => "ApplySimDetails",
            Self::ImportSimDescription { .. } => "ImportSimDescription",
            Self::SacsRecorder { .. } => "SacsRecorder",
            Self::SacsReplay { .. } => "SacsReplay",
            Self::DiagState { .. } => "DiagState",
            Self::DiagCaptureInteraction { .. } => "DiagCaptureInteraction",
            Self::QueryInteractionState { .. } => "QueryInteractionState",
            Self::InjectReadIntent { .. } => "InjectReadIntent",
            Self::Ping { .. } => "Ping",
            Self::Disconnect { .. } => "Disconnect",
            Self::Raw { .. } => "Raw",
            Self::SetRole { .. } => "SetRole",
            Self::ApplyPosition { .. } => "ApplyPosition",
            Self::ApplyPositions { .. } => "ApplyPositions",
            Self::ApplyMotive { .. } => "ApplyMotive",
            Self::ProbeSimMotive { .. } => "ProbeSimMotive",
            Self::ApplyInteractionQueue { .. } => "ApplyInteractionQueue",
            Self::ApplyInteractions { .. } => "ApplyInteractions",
            Self::ApplyForceAlign { .. } => "ApplyForceAlign",
            Self::ProbeSimInteractionQueue { .. } => "ProbeSimInteractionQueue",
            Self::ApplyCash { .. } => "ApplyCash",
            Self::ProbeHouseholdCash { .. } => "ProbeHouseholdCash",
            Self::ApplySkill { .. } => "ApplySkill",
            Self::ProbeSimSkill { .. } => "ProbeSimSkill",
            Self::ApplyAutonomy { .. } => "ApplyAutonomy",
            Self::ProbeSimAutonomy { .. } => "ProbeSimAutonomy",
            Self::MergeLotState { .. } => "MergeLotState",
            Self::ImportHouseholdContent { .. } => "ImportHouseholdContent",
            Self::ExportHouseholdContent { .. } => "ExportHouseholdContent",
            Self::MergeTravellingHousehold { .. } => "MergeTravellingHousehold",
            Self::ShowNotification { .. } => "ShowNotification",
        }
    }

    /// This variant's verb-table row.
    pub fn row(&self) -> &'static VerbRow {
        let record = self.record();
        VERB_TABLE.iter().find(|r| r.record == record).expect("every variant has a row")
    }
}

fn join(parts: &[String]) -> String {
    parts.join("|")
}

fn s<T: ToString>(v: T) -> String {
    v.to_string()
}

fn bit(b: bool) -> String {
    if b { "1" } else { "0" }.to_string()
}

/// `SimScopeWire.Format`: decimal ids joined by ',', "-" when empty.
fn scope_wire(ids: &[u64]) -> String {
    if ids.is_empty() {
        return "-".to_string();
    }
    ids.iter().map(u64::to_string).collect::<Vec<_>>().join(",")
}

/// `SimAliasWire.Format`.
fn alias_wire(pairs: &[(u64, u64)]) -> String {
    if pairs.is_empty() {
        return "-".to_string();
    }
    pairs.iter().map(|(o, d)| format!("{o}:{d}")).collect::<Vec<_>>().join(",")
}

/// `SimStateWire.FormatEntry` (six motives, `F2`; buffs `guid/origin` joined by '+', "-" when none).
fn sim_state_wire(st: &SimStateSample) -> String {
    let motives: Vec<String> = (0..6).map(|i| format_f2(st.motives.get(i).copied().unwrap_or(0.0))).collect();
    let buffs = if st.buffs.is_empty() {
        "-".to_string()
    } else {
        st.buffs.iter().map(|(g, o)| format!("{g}/{o}")).collect::<Vec<_>>().join("+")
    };
    format!("{}:{}:{}", st.sim, motives.join(","), buffs)
}

/// `SimDetailsWire.CleanToken`.
fn clean_token(value: Option<&str>) -> String {
    match value {
        None | Some("") => "-".to_string(),
        Some(v) => v.chars().map(|c| if ":;,|+= ".contains(c) { '_' } else { c }).collect(),
    }
}

/// `SimDetailsWire.FormatEntry`.
fn sim_details_wire(d: &SimDetailsSample) -> String {
    let branch = clean_token(if d.career_guid == 0 { None } else { Some(&d.career_branch) });
    let skills = if d.skills.is_empty() { "-".to_string() } else { d.skills.clone() };
    format!("{}:{}:{}:{}:{}:{}", d.sim, d.career_guid, d.career_level, branch, d.funds, skills)
}

const INTERACTION_PARAMETER_WIRE_KEYS: [&str; 10] = [
    "bookId",
    "progressNormalized",
    "autonomous",
    "userDirected",
    "effectivelyAutonomous",
    "seatObjectId",
    "seatArea",
    "seatContainmentSlot",
    "seatRoutingSlot",
    "seatIkSuffix",
];

fn lookup<'a>(params: &'a [(String, String)], key: &str) -> Option<&'a str> {
    params.iter().find(|(k, _)| k == key).map(|(_, v)| v.as_str())
}

/// `EncodeSelectedParameters` over `InteractionParameterWireKeys`.
fn interaction_parameters(params: &[(String, String)]) -> String {
    if params.is_empty() {
        return String::new();
    }
    INTERACTION_PARAMETER_WIRE_KEYS
        .iter()
        .filter_map(|k| lookup(params, k).filter(|v| !v.is_empty()).map(|v| format!("{k}={v}")))
        .collect::<Vec<_>>()
        .join(",")
}

fn position_pair(sim: u64, p: Vec3) -> String {
    format!("{}:{}:{}:{}", sim, format_r(p.x), format_r(p.y), format_r(p.z))
}

fn pairs(list: &[(u64, Vec3)]) -> String {
    list.iter().map(|(sim, p)| position_pair(*sim, *p)).collect::<Vec<_>>().join(";")
}

/// `EncodeQueueInteraction`, including the user-pick form (13 fields when `ui.name` is present).
fn queue_interaction(corr: &Guid, i: &InteractionRequest) -> String {
    let p = i.position.unwrap_or(Vec3 { x: 0.0, y: 0.0, z: 0.0 });
    let mut parts = vec![
        s("QUEUE_INTERACTION"),
        corr.to_n(),
        s(i.actor),
        encode_text(&i.key),
        i.target.map(|t| t.to_string()).unwrap_or_default(),
        bit(i.position.is_some()),
        format_r(p.x),
        format_r(p.y),
        format_r(p.z),
    ];
    if lookup(&i.parameters, "ui.name").is_some() {
        parts.push(encode_text(lookup(&i.parameters, "ui.name").unwrap_or("")));
        parts.push(encode_text(lookup(&i.parameters, "ui.path").unwrap_or("")));
        parts.push(lookup(&i.parameters, "ui.hit").unwrap_or("0:0:0").to_string());
        parts.push(lookup(&i.parameters, "ui.picked").unwrap_or("-").to_string());
    }
    join(&parts)
}

/// `BridgeLineCodec.EncodeCommand`, byte for byte; the three §E.3 verbs are refused by name, typed or as a
/// `Raw` line that names them.
pub fn encode(cmd: &BridgeCommand) -> Result<String, BridgeError> {
    use BridgeCommand as C;
    let row = cmd.row();
    if row.refused {
        return Err(BridgeError::RefusedLegacyVerb(row.wire));
    }
    let w = row.wire;
    let line = match cmd {
        C::LoadSave { corr, save_name } | C::LoadSaveViaMenu { corr, save_name } | C::SaveGame { corr, save_name } => {
            join(&[s(w), corr.to_n(), encode_text(save_name)])
        }
        C::SetGameSpeed { corr, speed } => join(&[s(w), corr.to_n(), s(speed.wire())]),
        C::SetWorldTime { corr, day, hours, minutes } => join(&[s(w), corr.to_n(), s(day), s(hours), s(minutes)]),
        C::RequestSnapshot { corr, since_tick } => match since_tick {
            Some(t) => join(&[s(w), corr.to_n(), s(t)]),
            None => join(&[s(w), corr.to_n()]),
        },
        C::ProbeSimLifecycle { corr }
        | C::ProbeActorLookup { corr }
        | C::DiagState { corr }
        | C::Ping { corr }
        | C::Disconnect { corr } => join(&[s(w), corr.to_n()]),
        C::ProbeStateInjection { corr, sim, position: p }
        | C::ApplyPosition { corr, sim, position: p } => {
            join(&[s(w), corr.to_n(), s(sim), format_r(p.x), format_r(p.y), format_r(p.z)])
        }
        C::QueueInteraction { corr, interaction } => queue_interaction(corr, interaction),
        C::SetActiveSim { corr, sim }
        | C::GateAutonomy { corr, sim }
        | C::DiagCaptureInteraction { corr, sim }
        | C::ProbeSimInteractionQueue { corr, sim }
        | C::ProbeSimAutonomy { corr, sim }
        | C::LeaveConversation { corr, actor: sim }
        | C::ExportSimDescription { corr, desc: sim }
        | C::ProbeHouseholdCash { corr, household: sim }
        | C::ExportHouseholdContent { corr, household: sim } => join(&[s(w), corr.to_n(), s(sim)]),
        C::CancelInteraction { corr, actor, interaction_id } => join(&[s(w), corr.to_n(), s(actor), s(interaction_id)]),
        C::AnswerDialog { corr, request_id, answer } => join(&[
            s(w),
            corr.to_n(),
            s(request_id),
            answer.as_deref().map_or_else(|| s("-"), encode_text),
        ]),
        C::ShowDialog { corr, request_id, sim, kind, args } => {
            let mut parts = vec![s(w), corr.to_n(), s(request_id), s(sim), encode_text(kind), s(args.len())];
            parts.extend(args.iter().map(|a| encode_text(a)));
            join(&parts)
        }
        C::DisableAutonomy { corr, sim, cancel_running } => join(&[s(w), corr.to_n(), s(sim), bit(*cancel_running)]),
        C::EnableAutonomy { corr, sim, reset } | C::UngateAutonomy { corr, sim, reset } => {
            if *reset {
                join(&[s(w), corr.to_n(), s(sim), s("reset")])
            } else {
                join(&[s(w), corr.to_n(), s(sim)])
            }
        }
        C::SetSimScope { corr, lots, sims } => join(&[s(w), corr.to_n(), scope_wire(lots), scope_wire(sims)]),
        C::SetAuthority { corr, mode, owned } => {
            let mode = match mode {
                AuthorityGateMode::Off => "off",
                AuthorityGateMode::Observe => "observe",
                AuthorityGateMode::Enforce => "enforce",
            };
            join(&[s(w), corr.to_n(), s(mode), scope_wire(owned)])
        }
        C::ProbeSimState { corr, sims } | C::ProbeSimDetails { corr, sims } => join(&[s(w), corr.to_n(), scope_wire(sims)]),
        C::ApplySimState { corr, state } => join(&[s(w), corr.to_n(), sim_state_wire(state)]),
        C::SetSimAliases { corr, aliases, reset } => {
            if *reset {
                join(&[s(w), corr.to_n(), alias_wire(aliases), s("reset")])
            } else {
                join(&[s(w), corr.to_n(), alias_wire(aliases)])
            }
        }
        C::ProbeSimPresence { corr, since_seq } => join(&[s(w), corr.to_n(), s(since_seq)]),
        C::SpawnSim { corr, sim, desc, position: p } => {
            join(&[s(w), corr.to_n(), s(sim), s(desc), format_r(p.x), format_r(p.y), format_r(p.z)])
        }
        C::DespawnSim { corr, sim, desc } => join(&[s(w), corr.to_n(), s(sim), s(desc)]),
        C::ApplySimDetails { corr, details } => join(&[s(w), corr.to_n(), sim_details_wire(details)]),
        C::ImportSimDescription { corr, payload } => {
            let total = payload.property_stream.len() + payload.res_key_table.len() + payload.object_id_table.len();
            join(&[
                s(w),
                corr.to_n(),
                s(payload.desc),
                s(total),
                s(payload.property_stream.len()),
                s(payload.res_key_table.len()),
                s(payload.object_id_table.len()),
                format!("{:08X}", crate::crc32::crc32(&payload.combined())),
            ])
        }
        C::SacsRecorder { corr, args } => {
            let mut parts = vec![s(w), corr.to_n()];
            parts.extend(args.iter().cloned());
            join(&parts)
        }
        C::SacsReplay { corr, lines } => join(&[s(w), corr.to_n(), encode_text(&lines.join("\n"))]),
        C::QueryInteractionState { corr, sim, watch_book } => match watch_book {
            Some(book) => join(&[s(w), corr.to_n(), s(sim), encode_text(&book.to_string())]),
            None => join(&[s(w), corr.to_n(), s(sim)]),
        },
        C::InjectReadIntent { corr, sim, book, mode, parameters } => {
            let params = parameters.as_deref().map(interaction_parameters).unwrap_or_default();
            let mut parts = vec![s(w), corr.to_n(), s(sim), s(book), encode_text(mode)];
            if !params.is_empty() {
                parts.push(encode_text(&params));
            }
            join(&parts)
        }
        C::Raw { corr, name, args } => {
            let mut parts = vec![name.clone(), corr.to_n()];
            parts.extend(args.iter().cloned());
            join(&parts)
        }
        C::SetRole { corr, role } => {
            join(&[s(w), corr.to_n(), s(if *role == HostRole::Server { "server" } else { "client" })])
        }
        C::ApplyPositions { corr, positions } => join(&[s(w), corr.to_n(), pairs(positions)]),
        C::ApplyMotive { corr, sim, motive, value } => {
            join(&[s(w), corr.to_n(), s(sim), encode_text(motive), format_r(*value)])
        }
        C::ProbeSimMotive { corr, sim, motive: name } | C::ProbeSimSkill { corr, sim, skill: name } => {
            join(&[s(w), corr.to_n(), s(sim), encode_text(name)])
        }
        C::ApplyCash { corr, household, funds } => join(&[s(w), corr.to_n(), s(household), s(funds)]),
        C::ApplySkill { corr, sim, skill, level } => join(&[s(w), corr.to_n(), s(sim), encode_text(skill), s(level)]),
        C::ApplyAutonomy { corr, sim, disabled } => join(&[s(w), corr.to_n(), s(sim), bit(*disabled)]),
        C::MergeLotState { corr, lot, sims } => join(&[s(w), corr.to_n(), s(lot), pairs(sims)]),
        C::ImportHouseholdContent {
            corr,
            household,
            total_bytes,
            property_stream_bytes,
            res_key_table_bytes,
            object_id_table_bytes,
            crc32,
            entry_count,
            res_key_count,
            object_id_count,
        } => join(&[
            s(w),
            corr.to_n(),
            s(household),
            s(total_bytes),
            s(property_stream_bytes),
            s(res_key_table_bytes),
            s(object_id_table_bytes),
            format!("{crc32:08X}"),
            s(entry_count),
            s(res_key_count),
            s(object_id_count),
        ]),
        C::MergeTravellingHousehold {
            corr,
            household,
            total_bytes,
            crc32,
            household_prop_bytes,
            household_res_key_bytes,
            household_obj_id_bytes,
            inventory_count,
        } => join(&[
            s(w),
            corr.to_n(),
            s(household),
            s(total_bytes),
            format!("{crc32:08X}"),
            s(household_prop_bytes),
            s(household_res_key_bytes),
            s(household_obj_id_bytes),
            s(inventory_count),
        ]),
        C::ShowNotification { corr, text } => join(&[s(w), corr.to_n(), encode_text(text)]),
        C::ApplyInteractionQueue { .. } | C::ApplyInteractions { .. } | C::ApplyForceAlign { .. } => {
            return Err(BridgeError::RefusedLegacyVerb(w));
        }
    };
    if let Some(wire) = refused_line_verb(&line) {
        return Err(BridgeError::RefusedLegacyVerb(wire));
    }
    Ok(line)
}

/// The record names declared in `BridgeCommand.cs` text: every `public sealed record <Name>(` whose
/// declaration (up to its terminating `;` or `{`) derives `: BridgeCommand(CorrelationId)`.
pub fn declared_records(cs_source: &str) -> Vec<String> {
    const OPEN: &str = "public sealed record ";
    let mut out = Vec::new();
    let mut rest = cs_source;
    while let Some(at) = rest.find(OPEN) {
        let after = &rest[at + OPEN.len()..];
        let name: String = after.chars().take_while(|c| c.is_ascii_alphanumeric() || *c == '_').collect();
        let end = after.find([';', '{']).unwrap_or(after.len());
        if after[..end].contains(": BridgeCommand(CorrelationId)") {
            out.push(name);
        }
        rest = &after[end.min(after.len())..];
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_table_has_unique_records_and_three_refusals() {
        let mut names: Vec<&str> = VERB_TABLE.iter().map(|r| r.record).collect();
        names.sort_unstable();
        names.dedup();
        assert_eq!(names.len(), VERB_TABLE.len());
        let refused: Vec<&str> = refused_rows().map(|r| r.wire).collect();
        assert_eq!(refused, ["APPLY_INTERACTION_QUEUE", "APPLY_INTERACTIONS", "APPLY_FORCE_ALIGN"]);
    }

    #[test]
    fn refused_verbs_are_named_refusals() {
        let corr = Guid::EMPTY;
        for cmd in [
            BridgeCommand::ApplyInteractionQueue { corr },
            BridgeCommand::ApplyInteractions { corr },
            BridgeCommand::ApplyForceAlign { corr },
        ] {
            let err = encode(&cmd).unwrap_err();
            assert_eq!(err, BridgeError::RefusedLegacyVerb(cmd.row().wire));
            assert_eq!(err.name(), format!("refused_legacy_verb:{}", cmd.row().wire));
        }
        // `encode`'s own post-check on the joined `Raw` line, pinned on its own: the `Raw` row is not a
        // refusal, so nothing but the final `refused_line_verb(&line)` stops these (`send_command`/`send_line`
        // refuse the same line again in the driver, which is why the driver tests cannot pin it here).
        for wire in ["APPLY_INTERACTION_QUEUE", "APPLY_INTERACTIONS", "APPLY_FORCE_ALIGN"] {
            let raw = BridgeCommand::Raw { corr, name: wire.to_string(), args: vec!["42".to_string()] };
            let err = encode(&raw).unwrap_err();
            assert_eq!(err, BridgeError::RefusedLegacyVerb(wire));
            assert_eq!(err.name(), format!("refused_legacy_verb:{wire}"));
        }
    }

    /// §E.2: the table against `BridgeCommand.cs`, read at test time. Fails when a record is added, removed
    /// or renamed on the C# side.
    #[test]
    fn verb_table_matches_bridge_command_cs() {
        let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../../DedicatedServer/HostBridge/BridgeCommand.cs");
        let source = std::fs::read_to_string(path).expect("read BridgeCommand.cs");
        let declared = declared_records(&source);
        let table: Vec<&str> = VERB_TABLE.iter().map(|r| r.record).collect();
        assert_eq!(declared.len(), table.len(), "verbs_declared differs from the table");
        assert_eq!(declared, table, "record names or order differ from the table");
    }

    #[test]
    fn declared_records_reads_multi_line_records() {
        let src = "public sealed record A(Guid CorrelationId) : BridgeCommand(CorrelationId);\n\
                   public sealed record B(\n  Guid CorrelationId,\n  int X) : BridgeCommand(CorrelationId);\n\
                   public sealed record NotOne(int Y);\n";
        assert_eq!(declared_records(src), ["A", "B"]);
    }
}
